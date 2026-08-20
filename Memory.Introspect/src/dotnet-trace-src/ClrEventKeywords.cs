// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// The keyword values mirror the runtime's clretwall.man, the same table
// https://github.com/dotnet/diagnostics/blob/main/src/Tools/dotnet-trace/ProviderUtils.cs
// exposes as the textual `--clrevents` names.

using System;

namespace Memory.Introspect.Trace
{
    /// <summary>
    /// The keywords of the Microsoft-Windows-DotNETRuntime provider, i.e. the categories of CLR
    /// events that <c>dotnet-trace --clrevents</c> selects by name. Combine them with <c>|</c>.
    /// </summary>
    [Flags]
    public enum ClrEventKeywords : long
    {
        None = 0,

        /// <summary>GC events (<c>gc</c>).</summary>
        Gc = 0x1,

        /// <summary>GC handle events (<c>gchandle</c>).</summary>
        GcHandle = 0x2,

        /// <summary>Assembly loader events (<c>assemblyloader</c>, historically <c>fusion</c>).</summary>
        AssemblyLoader = 0x4,

        /// <summary>Alias of <see cref="AssemblyLoader"/> (<c>fusion</c>).</summary>
        Fusion = 0x4,

        /// <summary>Loader events (<c>loader</c>).</summary>
        Loader = 0x8,

        /// <summary>JIT events (<c>jit</c>).</summary>
        Jit = 0x10,

        /// <summary>NGen events (<c>ngen</c>).</summary>
        NGen = 0x20,

        /// <summary>Start of an enumeration (<c>startenumeration</c>).</summary>
        StartEnumeration = 0x40,

        /// <summary>End of an enumeration (<c>endenumeration</c>).</summary>
        EndEnumeration = 0x80,

        /// <summary>Security events (<c>security</c>).</summary>
        Security = 0x400,

        /// <summary>AppDomain resource management events (<c>appdomainresourcemanagement</c>).</summary>
        AppDomainResourceManagement = 0x800,

        /// <summary>Verbose JIT tracing (<c>jittracing</c>).</summary>
        JitTracing = 0x1000,

        /// <summary>Interop events (<c>interop</c>).</summary>
        Interop = 0x2000,

        /// <summary>Lock contention events (<c>contention</c>).</summary>
        Contention = 0x4000,

        /// <summary>Exception events (<c>exception</c>).</summary>
        Exception = 0x8000,

        /// <summary>Threading and thread pool events (<c>threading</c>).</summary>
        Threading = 0x10000,

        /// <summary>Jitted method IL-to-native maps, needed to symbolise jitted frames (<c>jittedmethodiltonativemap</c>).</summary>
        JittedMethodILToNativeMap = 0x20000,

        /// <summary>Override and suppress NGen events (<c>overrideandsuppressngenevents</c>).</summary>
        OverrideAndSuppressNGenEvents = 0x40000,

        /// <summary>Type events (<c>type</c>).</summary>
        Type = 0x80000,

        /// <summary>GC heap dump events (<c>gcheapdump</c>).</summary>
        GcHeapDump = 0x100000,

        /// <summary>High frequency object allocation sampling (<c>gcsampledobjectallocationhigh</c>).</summary>
        GcSampledObjectAllocationHigh = 0x200000,

        /// <summary>GC heap survival and movement events (<c>gcheapsurvivalandmovement</c>).</summary>
        GcHeapSurvivalAndMovement = 0x400000,

        /// <summary>Induce a GC heap collection for the dump (<c>gcheapcollect</c>).</summary>
        GcHeapCollect = 0x800000,

        /// <summary>Alias of <see cref="GcHeapCollect"/> (<c>managedheadcollect</c>).</summary>
        ManagedHeapCollect = 0x800000,

        /// <summary>GC heap and type names (<c>gcheapandtypenames</c>).</summary>
        GcHeapAndTypeNames = 0x1000000,

        /// <summary>Low frequency object allocation sampling (<c>gcsampledobjectallocationlow</c>).</summary>
        GcSampledObjectAllocationLow = 0x2000000,

        /// <summary>Perf track events (<c>perftrack</c>).</summary>
        PerfTrack = 0x20000000,

        /// <summary>Stack walk events (<c>stack</c>).</summary>
        Stack = 0x40000000,

        /// <summary>Thread transfer events (<c>threadtransfer</c>).</summary>
        ThreadTransfer = 0x80000000,

        /// <summary>Debugger events (<c>debugger</c>).</summary>
        Debugger = 0x100000000,

        /// <summary>Monitoring events (<c>monitoring</c>).</summary>
        Monitoring = 0x200000000,

        /// <summary>Code symbol events (<c>codesymbols</c>).</summary>
        CodeSymbols = 0x400000000,

        /// <summary>EventSource events (<c>eventsource</c>).</summary>
        EventSource = 0x800000000,

        /// <summary>Compilation events (<c>compilation</c>).</summary>
        Compilation = 0x1000000000,

        /// <summary>Compilation diagnostic events (<c>compilationdiagnostic</c>).</summary>
        CompilationDiagnostic = 0x2000000000,

        /// <summary>Method diagnostic events (<c>methoddiagnostic</c>).</summary>
        MethodDiagnostic = 0x4000000000,

        /// <summary>Type diagnostic events (<c>typediagnostic</c>).</summary>
        TypeDiagnostic = 0x8000000000,

        /// <summary>JIT instrumentation data (<c>jitinstrumentationdata</c>).</summary>
        JitInstrumentationData = 0x10000000000,

        /// <summary>Profiler events (<c>profiler</c>).</summary>
        Profiler = 0x20000000000,

        /// <summary>WaitHandle events (<c>waithandle</c>).</summary>
        WaitHandle = 0x40000000000,

        /// <summary>Allocation sampling events (<c>allocationsampling</c>).</summary>
        AllocationSampling = 0x80000000000,
    }
}
