// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Adapted from https://github.com/dotnet/diagnostics/blob/main/src/Tools/dotnet-trace/CommandLine/Commands/CollectCommand.cs
// The console/child-process/CLI plumbing is dropped; what remains is the EventPipe session
// setup (providers, rundown keyword + retry strategies, buffer sizing, stopping events) and
// the stream pumping, reshaped into a library friendly API.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Memory.Introspect.Diagnostics.Monitoring.EventPipe;
using Memory.Introspect.Diagnostics.NETCore.Client;

namespace Memory.Introspect.Trace
{
    /// <summary>
    /// Collects an EventPipe trace from a target process, the programmatic equivalent of
    /// <c>dotnet-trace collect</c>.
    /// </summary>
    public static class TraceCollector
    {
        /// <summary>The buffer size used to pump the EventPipe stream when none is configured.</summary>
        public const int DefaultStreamCopyBufferSizeInBytes = 1024 * 1024;

        public static async Task<TraceResult> CollectAsync(
            int processId,
            TraceCollectionOptions options,
            string fallbackDiagnosticPort,
            int fallbackCircularBufferSizeInMB,
            TextWriter log,
            CancellationToken cancellationToken)
        {
            options ??= new TraceCollectionOptions();
            log ??= TextWriter.Null;

            int bufferSizeInMB = options.CircularBufferSizeInMB ?? fallbackCircularBufferSizeInMB;
            if (bufferSizeInMB <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "CircularBufferSizeInMB must be greater than zero.");
            }

            int copyBufferSize = options.StreamCopyBufferSizeInBytes > 0
                ? options.StreamCopyBufferSizeInBytes
                : DefaultStreamCopyBufferSizeInBytes;

            string diagnosticPort = string.IsNullOrEmpty(options.DiagnosticPort) ? fallbackDiagnosticPort : options.DiagnosticPort;

            TraceResult result = new()
            {
                ProcessId = processId,
                CircularBufferSizeInMB = bufferSizeInMB,
                RequestedDuration = options.Duration,
            };

            Stream outputStream = null;
            MemoryStream memoryStream = null;
            EventPipeSession session = null;

            try
            {
                // ---- providers -------------------------------------------------------------
                TraceProfileKind profiles = options.Profiles;
                if (profiles == TraceProfileKind.None
                    && (options.Providers is null || options.Providers.Count == 0)
                    && (options.ProviderConfigurations is null || options.ProviderConfigurations.Count == 0)
                    && options.ClrEvents == ClrEventKeywords.None)
                {
                    profiles = TraceProfiles.DefaultProfiles;
                    log.WriteLine($"[trace] No profile or providers specified, defaulting to trace profiles '{string.Join("' + '", TraceProfiles.Expand(profiles).Select(p => p.Name))}'.");
                }

                List<EventPipeProvider> providerCollection = ProviderUtils.ComputeProviderConfig(
                    options.Providers,
                    options.ClrEvents,
                    options.ClrEventLevel,
                    profiles,
                    options.ProviderConfigurations,
                    log);

                if (providerCollection.Count == 0)
                {
                    throw new DiagnosticToolException("No providers were specified to start a trace.");
                }
                result.Providers = providerCollection.AsReadOnly();

                // ---- rundown ---------------------------------------------------------------
                (long rundownKeyword, RetryStrategy retryStrategy) = ResolveRundown(profiles, options);
                result.RundownKeyword = rundownKeyword;

                // ---- stopping event --------------------------------------------------------
                IDictionary<string, string> payloadFilter = ValidateStoppingEvent(options);
                bool hasStoppingEvent = !string.IsNullOrEmpty(options.StoppingEventProviderName);

                // ---- session ---------------------------------------------------------------
                DiagnosticsClient client = CreateClient(processId, diagnosticPort);

                session = await StartSessionWithRetryAsync(
                    client, providerCollection, bufferSizeInMB, rundownKeyword, options.RequestStackwalk, retryStrategy, result, log, cancellationToken).ConfigureAwait(false);

                if (options.ResumeRuntime)
                {
                    try
                    {
                        await client.ResumeRuntimeAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (UnsupportedCommandException)
                    {
                        // Noop if the command is unsupported, since the target is most likely a 3.1 app.
                    }
                }

                // ---- output ----------------------------------------------------------------
                if (!string.IsNullOrEmpty(options.OutputPath))
                {
                    string directory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    outputStream = new FileStream(options.OutputPath, FileMode.Create, FileAccess.Write, FileShare.Read, copyBufferSize);
                    result.TraceFilePath = Path.GetFullPath(options.OutputPath);
                }
                else
                {
                    memoryStream = new MemoryStream();
                    outputStream = memoryStream;
                }

                using ManualResetEventSlim shouldExit = new(false);
                using CancellationTokenRegistration ctRegistration = cancellationToken.Register(() => shouldExit.Set());

                Stopwatch stopwatch = Stopwatch.StartNew();
                EventMonitor eventMonitor = null;
                Task copyTask;

                if (hasStoppingEvent)
                {
                    log.WriteLine($"[trace] Will stop on event '{options.StoppingEventProviderName}/{options.StoppingEventEventName}'");
                    eventMonitor = new EventMonitor(
                        options.StoppingEventProviderName,
                        options.StoppingEventEventName,
                        payloadFilter,
                        onEvent: _ =>
                        {
                            result.StoppedByStoppingEvent = true;
                            shouldExit.Set();
                        },
                        onPayloadFilterMismatch: traceEvent =>
                        {
                            log.WriteLine($"[trace] One or more field names specified in the payload filter for event '{traceEvent.ProviderName}/{traceEvent.EventName}' do not match any of the known field names: '{string.Join(" ", traceEvent.PayloadNames)}'. As a result the requested stopping event is unreachable; collection continues for the remaining duration.");
                            result.StoppingEventPayloadFilterMismatched = true;
                        },
                        eventStream: new PassthroughStream(session.EventStream, outputStream, copyBufferSize, leaveDestinationStreamOpen: true),
                        callOnEventOnlyOnce: true);

                    copyTask = eventMonitor.ProcessAsync(CancellationToken.None);
                }
                else
                {
                    copyTask = session.EventStream.CopyToAsync(outputStream, copyBufferSize, CancellationToken.None);
                }

                // If the target exits (or the stream otherwise ends) we are done too.
                Task shouldExitTask = copyTask.ContinueWith(_ => shouldExit.Set(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

                TimeSpan duration = options.Duration ?? TimeSpan.Zero;
                bool shouldStopAfterDuration = duration > TimeSpan.Zero;

                log.WriteLine($"[trace] Recording trace from pid {processId} (buffer {bufferSizeInMB} MB, rundown keyword 0x{rundownKeyword:X})"
                    + (shouldStopAfterDuration ? $" for {duration.TotalSeconds:0.##}s" : " until stopped"));

                DateTime nextProgress = DateTime.UtcNow;
                while (!shouldExit.Wait(100))
                {
                    if (shouldStopAfterDuration && stopwatch.Elapsed >= duration)
                    {
                        break;
                    }

                    if (options.Progress is not null && DateTime.UtcNow >= nextProgress)
                    {
                        nextProgress = DateTime.UtcNow.AddSeconds(1);
                        options.Progress.Report(new TraceProgress(stopwatch.Elapsed, SafeLength(outputStream)));
                    }
                }

                stopwatch.Stop();
                result.Elapsed = stopwatch.Elapsed;

                // If the copy already ended (target exited, etc.) there is no session left to stop.
                if (!copyTask.IsCompleted)
                {
                    log.WriteLine("[trace] Stopping the trace. This may take a while depending on the application being traced.");
                    try
                    {
                        await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        log.WriteLine($"[trace] StopAsync threw: {ex.Message}");
                    }
                }

                try
                {
                    await copyTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                if (eventMonitor is not null)
                {
                    await eventMonitor.DisposeAsync().ConfigureAwait(false);
                }

                // Surface any exception from the continuation as well.
                await shouldExitTask.ConfigureAwait(false);

                await outputStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                result.TraceSizeInBytes = SafeLength(outputStream);

                if (memoryStream is not null)
                {
                    result.NetTraceData = memoryStream.ToArray();
                    result.TraceSizeInBytes = result.NetTraceData.Length;
                }

                result.Cancelled = cancellationToken.IsCancellationRequested;
                result.Success = result.TraceSizeInBytes > 0;
                log.WriteLine($"[trace] Trace completed: {result.TraceSizeInBytes:N0} bytes in {result.Elapsed.TotalSeconds:0.##}s");
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
            }
            catch (Exception ex)
            {
                result.Exception = ex;
                log.WriteLine($"[trace] Failed: {ex}");
            }
            finally
            {
                session?.Dispose();

                if (outputStream is not null && !ReferenceEquals(outputStream, memoryStream))
                {
                    await outputStream.DisposeAsync().ConfigureAwait(false);
                }
                memoryStream?.Dispose();
            }

            // ---- optional format conversion ------------------------------------------------
            if (result.Success && options.Format != TraceFileFormat.NetTrace)
            {
                try
                {
                    result.ConvertedFilePath = result.ConvertTo(options.Format, options.ConvertedOutputPath, log);
                }
                catch (Exception ex)
                {
                    log.WriteLine($"[trace] Conversion to {options.Format} failed: {ex}");
                    result.Exception ??= ex;
                }
            }

            return result;
        }

        private static long SafeLength(Stream stream)
        {
            try { return stream.CanSeek || stream is FileStream ? stream.Length : 0; }
            catch { return 0; }
        }

        private static (long RundownKeyword, RetryStrategy RetryStrategy) ResolveRundown(TraceProfileKind profiles, TraceCollectionOptions options)
        {
            long rundownKeyword = 0;
            RetryStrategy retryStrategy = RetryStrategy.NothingToRetry;

            foreach (TraceProfile profile in TraceProfiles.Expand(profiles))
            {
                rundownKeyword |= profile.RundownKeyword;
                if (profile.RetryStrategy > retryStrategy)
                {
                    retryStrategy = profile.RetryStrategy;
                }
            }

            if (rundownKeyword == 0)
            {
                rundownKeyword = EventPipeSession.DefaultRundownKeyword;
            }

            if (options.Rundown.HasValue)
            {
                if (options.Rundown.Value)
                {
                    rundownKeyword |= EventPipeSession.DefaultRundownKeyword;
                    retryStrategy = (rundownKeyword == EventPipeSession.DefaultRundownKeyword)
                        ? RetryStrategy.NothingToRetry
                        : RetryStrategy.DropKeywordKeepRundown;
                }
                else
                {
                    rundownKeyword = 0;
                    retryStrategy = RetryStrategy.NothingToRetry;
                }
            }

            // An explicit keyword always wins over both the profiles and the rundown flag.
            if (options.RundownKeyword.HasValue)
            {
                rundownKeyword = options.RundownKeyword.Value;
                retryStrategy = (rundownKeyword == EventPipeSession.DefaultRundownKeyword || rundownKeyword == 0)
                    ? RetryStrategy.NothingToRetry
                    : RetryStrategy.DropKeywordKeepRundown;
            }

            if (!options.RetryOnUnsupportedConfiguration)
            {
                retryStrategy = RetryStrategy.ForbiddenToRetry;
            }

            return (rundownKeyword, retryStrategy);
        }

        private static async Task<EventPipeSession> StartSessionWithRetryAsync(
            DiagnosticsClient client,
            List<EventPipeProvider> providers,
            int bufferSizeInMB,
            long rundownKeyword,
            bool requestStackwalk,
            RetryStrategy retryStrategy,
            TraceResult result,
            TextWriter log,
            CancellationToken cancellationToken)
        {
            try
            {
                EventPipeSessionConfiguration config = new(providers, bufferSizeInMB, rundownKeyword, requestStackwalk);
                return await client.StartEventPipeSessionAsync(config, cancellationToken).ConfigureAwait(false);
            }
            catch (UnsupportedCommandException ex)
            {
                if (retryStrategy == RetryStrategy.DropKeywordKeepRundown)
                {
                    log.WriteLine("[trace] The runtime being traced doesn't support the custom rundown keyword used by this configuration, retrying with the standard rundown keyword.");
                    result.RundownKeyword = EventPipeSession.DefaultRundownKeyword;
                    EventPipeSessionConfiguration config = new(providers, bufferSizeInMB, EventPipeSession.DefaultRundownKeyword, requestStackwalk);
                    return await client.StartEventPipeSessionAsync(config, cancellationToken).ConfigureAwait(false);
                }

                if (retryStrategy == RetryStrategy.DropKeywordDropRundown)
                {
                    log.WriteLine("[trace] The runtime being traced doesn't support the custom rundown keyword used by this configuration, retrying with rundown omitted.");
                    result.RundownKeyword = 0;
                    EventPipeSessionConfiguration config = new(providers, bufferSizeInMB, 0L, requestStackwalk);
                    return await client.StartEventPipeSessionAsync(config, cancellationToken).ConfigureAwait(false);
                }

                throw new DiagnosticToolException($"Unable to start a tracing session: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new DiagnosticToolException($"Not enough permissions to access the specified app: {ex.GetType()}");
            }
        }

        private static IDictionary<string, string> ValidateStoppingEvent(TraceCollectionOptions options)
        {
            bool hasProviderName = !string.IsNullOrEmpty(options.StoppingEventProviderName);
            bool hasEventName = !string.IsNullOrEmpty(options.StoppingEventEventName);
            bool hasPayloadFilter = options.StoppingEventPayloadFilter is { Count: > 0 };

            if (!hasProviderName && (hasEventName || hasPayloadFilter))
            {
                throw new DiagnosticToolException($"{nameof(TraceCollectionOptions.StoppingEventProviderName)} is required to stop tracing after a specific event.");
            }

            if (!hasEventName && hasPayloadFilter)
            {
                throw new DiagnosticToolException($"{nameof(TraceCollectionOptions.StoppingEventEventName)} is required when a {nameof(TraceCollectionOptions.StoppingEventPayloadFilter)} is given.");
            }

            return options.StoppingEventPayloadFilter is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(options.StoppingEventPayloadFilter);
        }

        internal static DiagnosticsClient CreateClient(int processId, string diagnosticPort)
        {
            if (!string.IsNullOrEmpty(diagnosticPort))
            {
                IpcEndpointConfig endpoint = IpcEndpointConfig.Parse(diagnosticPort);
                return new DiagnosticsClient(endpoint);
            }
            return new DiagnosticsClient(processId);
        }
    }

    /// <summary>Progress information reported while a trace is being recorded.</summary>
    public readonly struct TraceProgress
    {
        public TraceProgress(TimeSpan elapsed, long sizeInBytes)
        {
            Elapsed = elapsed;
            SizeInBytes = sizeInBytes;
        }

        /// <summary>How long the trace has been running.</summary>
        public TimeSpan Elapsed { get; }

        /// <summary>How much trace data has been written so far.</summary>
        public long SizeInBytes { get; }

        public override string ToString() => $"[{Elapsed:dd\\:hh\\:mm\\:ss}] {SizeInBytes:N0} bytes";
    }
}
