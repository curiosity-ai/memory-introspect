// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// The option surface mirrors the switches of `dotnet-trace collect`.

using System;
using System.Collections.Generic;
using Memory.Introspect.Diagnostics.NETCore.Client;

namespace Memory.Introspect.Trace
{
    /// <summary>
    /// Configures a single <c>dotnet-trace collect</c> equivalent capture.
    /// </summary>
    public sealed class TraceCollectionOptions
    {
        /// <summary>
        /// Provider specifications in <c>dotnet-trace --providers</c> syntax, i.e.
        /// <c>Name[:Keywords[:Level[:KeyValueArgs]]]</c> where Keywords is a hexadecimal mask
        /// (e.g. <c>Microsoft-Windows-DotNETRuntime:0x1:5</c>).
        /// </summary>
        public IReadOnlyList<string> Providers { get; set; }

        /// <summary>
        /// Already strongly-typed providers to enable, merged with <see cref="Providers"/>.
        /// </summary>
        public IReadOnlyList<EventPipeProvider> ProviderConfigurations { get; set; }

        /// <summary>
        /// Named profiles from <see cref="TraceProfiles.All"/>, e.g. "dotnet-common" or
        /// "gc-collect". When no providers, profiles or CLR events are configured at all, the
        /// <see cref="TraceProfiles.DefaultProfileNames"/> are used — same as the CLI tool.
        /// </summary>
        public IReadOnlyList<string> Profiles { get; set; }

        /// <summary>
        /// A '+' separated list of CLR event keyword names to enable on
        /// Microsoft-Windows-DotNETRuntime, e.g. <c>"gc+gchandle+exception"</c>. Equivalent to
        /// <c>--clrevents</c>.
        /// </summary>
        public string ClrEvents { get; set; }

        /// <summary>The verbosity for <see cref="ClrEvents"/>; a name ("verbose") or number ("5").</summary>
        public string ClrEventLevel { get; set; }

        /// <summary>
        /// How long to record. When null (or zero) the capture runs until the cancellation
        /// token fires, the stopping event is seen, or the target process exits.
        /// </summary>
        public TimeSpan? Duration { get; set; }

        /// <summary>
        /// The size of the runtime's in-memory circular buffer, in MB. Raise this for very
        /// large or very chatty processes to avoid dropped events. When null the value from
        /// <c>MemoryIntrospectorOptions.CircularBufferSizeInMB</c> is used.
        /// </summary>
        public int? CircularBufferSizeInMB { get; set; }

        /// <summary>
        /// The buffer used when pumping the EventPipe stream to the output. Larger buffers
        /// reduce syscall overhead when a large process produces events faster than they can
        /// be written out. Defaults to <see cref="TraceCollector.DefaultStreamCopyBufferSizeInBytes"/>.
        /// </summary>
        public int StreamCopyBufferSizeInBytes { get; set; } = TraceCollector.DefaultStreamCopyBufferSizeInBytes;

        /// <summary>
        /// Whether to request rundown events. Rundown is needed to resolve dynamically
        /// generated (jitted) method names, but on large applications it noticeably increases
        /// both the time to stop the session and the resulting file size. Null keeps whatever
        /// the selected profiles ask for.
        /// </summary>
        public bool? Rundown { get; set; }

        /// <summary>
        /// An explicit rundown keyword, overriding both <see cref="Rundown"/> and the profiles.
        /// </summary>
        public long? RundownKeyword { get; set; }

        /// <summary>
        /// If true (the default) a stack trace is recorded for every emitted event. Turning it
        /// off makes event collection considerably cheaper on busy processes, but requires
        /// .NET 9 or later on the target.
        /// </summary>
        public bool RequestStackwalk { get; set; } = true;

        /// <summary>
        /// If the runtime is suspended waiting for a diagnostics connection (the
        /// <c>DOTNET_DefaultDiagnosticPortSuspend</c> scenario), resume it once the session is
        /// up. Equivalent to <c>--resume-runtime</c>.
        /// </summary>
        public bool ResumeRuntime { get; set; }

        /// <summary>
        /// Where to write the .nettrace data. When set the trace is streamed straight to disk,
        /// which is what you want for large captures; when null the trace is buffered in
        /// memory and exposed through <see cref="TraceResult.NetTraceData"/>.
        /// </summary>
        public string OutputPath { get; set; }

        /// <summary>
        /// An additional output format to produce alongside the .nettrace file. Equivalent to
        /// <c>--format</c>.
        /// </summary>
        public TraceFileFormat Format { get; set; } = TraceFileFormat.NetTrace;

        /// <summary>
        /// Where to write the <see cref="Format"/> output. When null it is derived from
        /// <see cref="OutputPath"/> by replacing its extension.
        /// </summary>
        public string ConvertedOutputPath { get; set; }

        /// <summary>
        /// Stop the trace when an event from this provider is seen. Equivalent to
        /// <c>--stopping-event-provider-name</c>.
        /// </summary>
        public string StoppingEventProviderName { get; set; }

        /// <summary>
        /// Narrows <see cref="StoppingEventProviderName"/> to a single event name. Equivalent
        /// to <c>--stopping-event-event-name</c>.
        /// </summary>
        public string StoppingEventEventName { get; set; }

        /// <summary>
        /// Narrows the stopping event further to events whose payload fields match these
        /// values. Equivalent to <c>--stopping-event-payload-filter</c>. Requires both
        /// <see cref="StoppingEventProviderName"/> and <see cref="StoppingEventEventName"/>.
        /// </summary>
        public IReadOnlyDictionary<string, string> StoppingEventPayloadFilter { get; set; }

        /// <summary>
        /// The diagnostic port to connect through instead of the process id. When null the
        /// value from <c>MemoryIntrospectorOptions.DiagnosticPort</c> is used.
        /// </summary>
        public string DiagnosticPort { get; set; }

        /// <summary>
        /// When the target runtime rejects the requested rundown configuration, retry with a
        /// progressively simpler one (the same behaviour as the CLI tool). Set to false to
        /// fail instead.
        /// </summary>
        public bool RetryOnUnsupportedConfiguration { get; set; } = true;

        /// <summary>Optional progress callback, invoked about once per second while recording.</summary>
        public IProgress<TraceProgress> Progress { get; set; }
    }
}
