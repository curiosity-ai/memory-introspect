using System;

namespace Memory.Introspect.GcPauses
{
    /// <summary>Configures <see cref="GcPauseMonitor"/>.</summary>
    public sealed class GcPauseMonitorOptions
    {
        /// <summary>A pause at least this long is reported as long and can trigger a detailed capture.</summary>
        public TimeSpan LongPauseThreshold { get; set; } = TimeSpan.FromMilliseconds(200);

        /// <summary>Invoked for every pause (every GC and every other execution engine suspension).</summary>
        public Action<GcPause> OnPause { get; set; }

        /// <summary>Invoked for every pause at least <see cref="LongPauseThreshold"/> long.</summary>
        public Action<GcPause> OnLongPause { get; set; }

        /// <summary>
        /// The runtime's buffer for the always-on session, in MB. The always-on session only carries
        /// a handful of small events per GC, so a small buffer is plenty.
        /// </summary>
        public int CircularBufferSizeInMB { get; set; } = 16;

        /// <summary>How many long pauses <see cref="GcPauseMonitor.RecentLongPauses"/> keeps.</summary>
        public int RecentLongPauseCapacity { get; set; } = 32;

        /// <summary>
        /// What to capture when a long pause is detected. Set <see cref="GcPauseDetailedCaptureOptions.Enabled"/>
        /// to false to only run the cheap always-on monitoring.
        /// </summary>
        public GcPauseDetailedCaptureOptions DetailedCapture { get; set; } = new();
    }

    /// <summary>
    /// The escalation the monitor performs after a long pause: a short, more expensive trace
    /// that records the next long pause(s) in detail, optionally followed by a heap snapshot.
    /// </summary>
    public sealed class GcPauseDetailedCaptureOptions
    {
        /// <summary>Whether a long pause triggers a detailed capture at all.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Upper bound on how long a detailed capture records.</summary>
        public TimeSpan MaxDuration { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Stop the detailed capture this long after it has recorded a long pause. Long pauses
        /// tend to repeat, so the capture usually ends well before <see cref="MaxDuration"/>.
        /// Null records for the full <see cref="MaxDuration"/>.
        /// </summary>
        public TimeSpan? StopAfterLongPause { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Where the .nettrace (and .gcdump) files go. Defaults to a folder under the temp directory.</summary>
        public string OutputDirectory { get; set; }

        /// <summary>
        /// Record call stacks (with rundown, so they resolve to method names). This is what names the
        /// code that called <c>GC.Collect</c> or allocated the object that triggered the GC.
        /// </summary>
        public bool CollectStacks { get; set; } = true;

        /// <summary>
        /// Enable the GC keyword at Verbose level: allocation ticks (what was being allocated
        /// before the pause), pinned objects at GC time and, under server GC, per-phase join events.
        /// </summary>
        public bool VerboseGcEvents { get; set; } = true;

        /// <summary>
        /// Also record which object ranges survived and moved (GCHeapSurvivalAndMovement). Very
        /// verbose on a large heap; off by default.
        /// </summary>
        public bool ObjectSurvivalAndMovement { get; set; }

        /// <summary>
        /// After the trace, take a .gcdump to show which types make up the live graph the GC had to
        /// mark. Taking it induces one more full GC, so it is off by default.
        /// </summary>
        public bool CollectGcDump { get; set; }

        /// <summary>How many types the heap snapshot histogram keeps.</summary>
        public int GcDumpTopTypes { get; set; } = 15;

        /// <summary>Upper bound on the number of detailed captures over the monitor's lifetime.</summary>
        public int MaxCaptures { get; set; } = 3;

        /// <summary>Minimum time between the end of one detailed capture and the start of the next.</summary>
        public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Runtime buffer for the detailed session, in MB.</summary>
        public int CircularBufferSizeInMB { get; set; } = 256;

        /// <summary>Invoked once a detailed capture has been analysed.</summary>
        public Action<GcPauseDetailedReport> OnReport { get; set; }
    }
}
