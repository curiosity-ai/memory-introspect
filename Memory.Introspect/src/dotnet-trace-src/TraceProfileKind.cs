// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Memory.Introspect.Trace
{
    /// <summary>
    /// The built-in tracing profiles, the strongly typed form of the profile names accepted by
    /// <c>dotnet-trace collect --profile</c>. Combine them with <c>|</c> to enable several at
    /// once, exactly as passing <c>--profile</c> more than once does.
    /// </summary>
    [Flags]
    public enum TraceProfileKind
    {
        /// <summary>No profile.</summary>
        None = 0,

        /// <summary>
        /// <c>dotnet-common</c>: lightweight runtime diagnostics (GC, assembly loader, loader,
        /// JIT, exceptions, threading, jitted method maps and compilation) at low overhead.
        /// </summary>
        DotNetCommon = 1 << 0,

        /// <summary>
        /// <c>dotnet-sampled-thread-time</c>: samples managed thread stacks at ~100 Hz. Required
        /// for <c>TopMethods</c> reports and speedscope/chromium output to be meaningful.
        /// </summary>
        DotNetSampledThreadTime = 1 << 1,

        /// <summary><c>gc-verbose</c>: GC collections plus sampled object allocations.</summary>
        GcVerbose = 1 << 2,

        /// <summary><c>gc-collect</c>: GC collections only, at very low overhead.</summary>
        GcCollect = 1 << 3,

        /// <summary><c>database</c>: ADO.NET and Entity Framework database commands.</summary>
        Database = 1 << 4,

        /// <summary>
        /// The profiles used when nothing at all is configured, matching the
        /// <c>dotnet-trace collect</c> default.
        /// </summary>
        Default = DotNetCommon | DotNetSampledThreadTime,
    }
}
