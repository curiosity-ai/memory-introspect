using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Memory.Introspect.Diagnostics.NETCore.Client;
using Memory.Introspect.Trace;
using Microsoft.Diagnostics.Tracing;

namespace Memory.Introspect.GcPauses
{
    /// <summary>
    /// Watches a process for long GC pauses with a cheap, always-on EventPipe session, explains
    /// each long pause from the events the GC already emits, and escalates to a short detailed
    /// capture when one is seen.
    /// </summary>
    /// <remarks>
    /// <para>The always-on session enables only the GC keyword of
    /// <c>Microsoft-Windows-DotNETRuntime</c> at Informational level, without stack walking or
    /// rundown. That is roughly a dozen small events per GC: enough to know how long every pause
    /// was, how long threads took to stop, which GC ran and why, how long mark / plan / relocate /
    /// compact took (GCGlobalHeapHistory, .NET 7+), and which kind of root the marking time went
    /// to (GCMarkWithType).</para>
    /// <para>The events are produced while the world is stopped but are only read once it
    /// restarts, so a pause is reported right after it ends — never during it. That holds for
    /// self-monitoring too: the monitor's own thread is paused with everything else.</para>
    /// </remarks>
    public sealed class GcPauseMonitor : IAsyncDisposable
    {
        /// <summary>The GC keyword of Microsoft-Windows-DotNETRuntime.</summary>
        private const long GcKeyword = (long)ClrEventKeywords.Gc;

        private readonly int _processId;
        private readonly string _diagnosticPort;
        private readonly GcPauseMonitorOptions _options;
        private readonly MemoryIntrospector _introspector;
        private readonly TextWriter _log;

        private readonly object _lock = new();
        private readonly Queue<GcPause> _recentLongPauses = new();
        private readonly CancellationTokenSource _disposed = new();

        private EventPipeSession _session;
        private EventPipeEventSource _source;
        private GcPauseTracker _tracker;
        private Task _processing;

        private Task<GcPauseDetailedReport> _captureTask;
        private CancellationTokenSource _captureStop;
        private bool _captureStopArmed;
        private DateTime _captureNotBeforeUtc = DateTime.MinValue;
        private int _capturesStarted;
        private readonly List<GcPauseDetailedReport> _reports = new();

        private long _pauseCount;
        private long _longPauseCount;
        private double _totalPauseMSec;
        private double _maxPauseMSec;

        private GcPauseMonitor(int processId, string diagnosticPort, GcPauseMonitorOptions options, MemoryIntrospector introspector, TextWriter log)
        {
            _processId = processId;
            _diagnosticPort = diagnosticPort;
            _options = options;
            _introspector = introspector;
            _log = log ?? TextWriter.Null;
        }

        /// <summary>The process being monitored.</summary>
        public int ProcessId => _processId;

        /// <summary>Whether the always-on session records stacks (only before .NET 9, which cannot turn them off).</summary>
        public bool StackwalkEnabled { get; private set; }

        /// <summary>Number of GC events read by the always-on session.</summary>
        public long EventsProcessed => _tracker?.EventCount ?? 0;

        /// <summary>Events the runtime dropped from the always-on session because its buffer was full.</summary>
        public long EventsLost => _source?.EventsLost ?? 0;

        /// <summary>Number of pauses seen so far.</summary>
        public long PauseCount => Interlocked.Read(ref _pauseCount);

        /// <summary>Number of long pauses seen so far.</summary>
        public long LongPauseCount => Interlocked.Read(ref _longPauseCount);

        /// <summary>Total time the process spent paused since the monitor started.</summary>
        public TimeSpan TotalPauseTime { get { lock (_lock) { return TimeSpan.FromMilliseconds(_totalPauseMSec); } } }

        /// <summary>The longest pause seen so far.</summary>
        public TimeSpan MaxPause { get { lock (_lock) { return TimeSpan.FromMilliseconds(_maxPauseMSec); } } }

        /// <summary>The most recent long pauses, oldest first.</summary>
        public IReadOnlyList<GcPause> RecentLongPauses { get { lock (_lock) { return _recentLongPauses.ToArray(); } } }

        /// <summary>The detailed captures completed so far.</summary>
        public IReadOnlyList<GcPauseDetailedReport> DetailedReports { get { lock (_lock) { return _reports.ToArray(); } } }

        /// <summary>True while a detailed capture is running.</summary>
        public bool IsCapturingDetails { get { lock (_lock) { return _captureTask is { IsCompleted: false }; } } }

        internal static async Task<GcPauseMonitor> StartAsync(
            int processId,
            string diagnosticPort,
            GcPauseMonitorOptions options,
            MemoryIntrospector introspector,
            TextWriter log,
            CancellationToken cancellationToken)
        {
            options ??= new GcPauseMonitorOptions();
            if (options.LongPauseThreshold <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "LongPauseThreshold must be positive.");
            }

            GcPauseMonitor monitor = new(processId, diagnosticPort, options, introspector, log);
            await monitor.StartSessionAsync(cancellationToken).ConfigureAwait(false);
            return monitor;
        }

        private async Task StartSessionAsync(CancellationToken cancellationToken)
        {
            DiagnosticsClient client = TraceCollector.CreateClient(_processId, _diagnosticPort);
            EventPipeProvider[] providers =
            {
                new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, GcKeyword),
            };

            try
            {
                // No rundown (nothing to resolve) and no stack walk: the cheapest possible session.
                _session = await client.StartEventPipeSessionAsync(
                    new EventPipeSessionConfiguration(providers, _options.CircularBufferSizeInMB, rundownKeyword: 0, requestStackwalk: false),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (UnsupportedCommandException)
            {
                // Runtimes before .NET 9 cannot turn the stack walk off.
                _log.WriteLine("[gc-pauses] The target runtime cannot disable stack walking (pre .NET 9); the always-on session will record stacks.");
                StackwalkEnabled = true;
                _session = await client.StartEventPipeSessionAsync(
                    new EventPipeSessionConfiguration(providers, _options.CircularBufferSizeInMB, rundownKeyword: 0, requestStackwalk: true),
                    cancellationToken).ConfigureAwait(false);
            }

            _tracker = new GcPauseTracker(_options.LongPauseThreshold.TotalMilliseconds, OnPause);

            // EventPipeEventSource reads the header synchronously from the stream, so build it on the
            // processing thread too.
            TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _processing = Task.Factory.StartNew(() =>
            {
                try
                {
                    _source = new EventPipeEventSource(_session.EventStream);
                    _tracker.Subscribe(_source.Clr);
                    ready.TrySetResult();
                    _source.Process();
                }
                catch (Exception ex)
                {
                    ready.TrySetResult();
                    if (!_disposed.IsCancellationRequested)
                    {
                        _log.WriteLine($"[gc-pauses] Event processing stopped: {ex.Message}");
                    }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            await ready.Task.ConfigureAwait(false);
            _log.WriteLine($"[gc-pauses] Monitoring pid {_processId} for pauses >= {_options.LongPauseThreshold.TotalMilliseconds:N0} ms");
        }

        private void OnPause(GcPause pause)
        {
            Interlocked.Increment(ref _pauseCount);
            lock (_lock)
            {
                pause.DuringDetailedCapture = _captureTask is { IsCompleted: false };
                _totalPauseMSec += pause.PauseMSec;
                _maxPauseMSec = Math.Max(_maxPauseMSec, pause.PauseMSec);
            }

            Invoke(_options.OnPause, pause);

            if (!pause.IsLong)
            {
                return;
            }

            Interlocked.Increment(ref _longPauseCount);
            lock (_lock)
            {
                _recentLongPauses.Enqueue(pause);
                while (_recentLongPauses.Count > Math.Max(1, _options.RecentLongPauseCapacity))
                {
                    _recentLongPauses.Dequeue();
                }
            }

            Invoke(_options.OnLongPause, pause);
            MaybeEscalate(pause);
        }

        private void MaybeEscalate(GcPause pause)
        {
            GcPauseDetailedCaptureOptions detailed = _options.DetailedCapture;
            if (detailed is null || !detailed.Enabled)
            {
                return;
            }

            lock (_lock)
            {
                if (_captureTask is { IsCompleted: false })
                {
                    // A capture is already running and has now recorded a long pause: wrap it up
                    // shortly. Armed once, so a stream of long pauses cannot keep pushing it out.
                    if (detailed.StopAfterLongPause is TimeSpan grace && _captureStop is not null && !_captureStopArmed)
                    {
                        _captureStopArmed = true;
                        try { _captureStop.CancelAfter(grace); } catch (ObjectDisposedException) { }
                    }
                    return;
                }

                if (_disposed.IsCancellationRequested || _capturesStarted >= detailed.MaxCaptures || DateTime.UtcNow < _captureNotBeforeUtc)
                {
                    return;
                }

                _capturesStarted++;
                _captureTask = Task.Run(() => CaptureDetailsCoreAsync(pause, detailed.MaxDuration, autoStop: true, CancellationToken.None));
            }
        }

        /// <summary>
        /// Runs a detailed capture now, regardless of the escalation policy, and returns its report.
        /// It records for <paramref name="duration"/> (or until cancelled) even if long pauses occur.
        /// </summary>
        public Task<GcPauseDetailedReport> CaptureDetailsAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (_captureTask is { IsCompleted: false })
                {
                    throw new InvalidOperationException("A detailed capture is already running.");
                }

                _captureTask = Task.Run(() => CaptureDetailsCoreAsync(null, duration, autoStop: false, cancellationToken));
                return _captureTask;
            }
        }

        private async Task<GcPauseDetailedReport> CaptureDetailsCoreAsync(GcPause trigger, TimeSpan duration, bool autoStop, CancellationToken cancellationToken)
        {
            GcPauseDetailedCaptureOptions detailed = _options.DetailedCapture ?? new GcPauseDetailedCaptureOptions();
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
            lock (_lock)
            {
                _captureStop = stop;
                // A manual capture runs for the duration it was asked for.
                _captureStopArmed = !autoStop;
            }

            GcPauseDetailedReport report;
            try
            {
                if (trigger is not null)
                {
                    _log.WriteLine($"[gc-pauses] Long pause detected ({trigger.Summary}); starting a detailed capture.");
                }

                report = await GcPauseDetailedCapture.RunAsync(
                    _introspector, _processId, _diagnosticPort, detailed, duration, trigger,
                    _options.LongPauseThreshold.TotalMilliseconds, _log, stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.WriteLine($"[gc-pauses] Detailed capture failed: {ex}");
                report = new GcPauseDetailedReport { Trigger = trigger, Exception = ex };
            }
            finally
            {
                lock (_lock)
                {
                    _captureStop = null;
                    _captureNotBeforeUtc = DateTime.UtcNow + detailed.Cooldown;
                }
            }

            lock (_lock)
            {
                _reports.Add(report);
            }

            Invoke(detailed.OnReport, report);
            return report;
        }

        private void Invoke<T>(Action<T> callback, T value)
        {
            if (callback is null)
            {
                return;
            }

            try
            {
                callback(value);
            }
            catch (Exception ex)
            {
                // A throwing callback must not take the event pump down with it.
                _log.WriteLine($"[gc-pauses] Callback threw: {ex}");
            }
        }

        /// <summary>
        /// Stops monitoring. A detailed capture that is running is stopped early, analysed, and
        /// awaited.
        /// </summary>
        public async Task StopAsync()
        {
            if (_disposed.IsCancellationRequested)
            {
                return;
            }

            _disposed.Cancel();

            Task<GcPauseDetailedReport> capture;
            lock (_lock)
            {
                capture = _captureTask;
            }

            if (capture is not null)
            {
                try { await capture.ConfigureAwait(false); } catch { }
            }

            if (_session is not null)
            {
                try
                {
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                    await _session.StopAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.WriteLine($"[gc-pauses] Stopping the session threw: {ex.Message}");
                }
            }

            if (_processing is not null)
            {
                await Task.WhenAny(_processing, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            }

            _source?.Dispose();
            _session?.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _disposed.Dispose();
        }
    }
}
