// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Adapted from https://github.com/dotnet/diagnostics/blob/main/src/Tools/dotnet-trace/Profile.cs
// and .../CommandLine/Commands/ListProfilesCommandHandler.cs
//
// The `collect-linux` only profiles (cpu-sampling / thread-time) are intentionally not
// ported: they drive the Linux perf_events subsystem through an external collector rather
// than EventPipe, which is out of scope for an in-process library.

using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using Memory.Introspect.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace Memory.Introspect.Trace
{
    /// <summary>
    /// A named, pre-defined set of EventPipe provider configurations that allows common
    /// tracing scenarios to be specified succinctly. Equivalent to a `dotnet-trace` profile
    /// as listed by `dotnet-trace list-profiles`.
    /// </summary>
    public sealed class TraceProfile
    {
        internal TraceProfile(TraceProfileKind kind, string name, IEnumerable<EventPipeProvider> providers, string description)
        {
            Kind = kind;
            Name = name;
            Providers = providers == null ? Array.Empty<EventPipeProvider>() : new List<EventPipeProvider>(providers).AsReadOnly();
            Description = description;
        }

        /// <summary>The profile as a <see cref="TraceProfileKind"/> flag.</summary>
        public TraceProfileKind Kind { get; }

        /// <summary>The profile name as used by the CLI tool, e.g. "dotnet-common".</summary>
        public string Name { get; }

        /// <summary>The providers this profile enables.</summary>
        public IReadOnlyList<EventPipeProvider> Providers { get; }

        /// <summary>A human readable description of what the profile captures.</summary>
        public string Description { get; }

        /// <summary>The rundown keyword this profile requires.</summary>
        public long RundownKeyword { get; internal set; } = EventPipeSession.DefaultRundownKeyword;

        /// <summary>How to retry if the target runtime rejects this profile's configuration.</summary>
        public RetryStrategy RetryStrategy { get; internal set; } = RetryStrategy.NothingToRetry;

        public override string ToString() => $"{Name} - {Description}";
    }

    /// <summary>
    /// The built-in tracing profiles, mirroring `dotnet-trace list-profiles`.
    /// </summary>
    public static class TraceProfiles
    {
        // GC | AssemblyLoader | Loader | JIT | Exceptions | Threading | JittedMethodILToNativeMap | Compilation
        private const long DotNetCommonKeyword = 0x1 | 0x4 | 0x8 | 0x10 | 0x8000 | 0x10000 | 0x20000 | 0x1000000000;

        private const string DotNetCommonDescription =
            "Lightweight .NET runtime diagnostics designed to stay low overhead.\n" +
            "Includes GC, AssemblyLoader, Loader, JIT, Exceptions, Threading, JittedMethodILToNativeMap, and Compilation events.\n" +
            "Equivalent to providers \"Microsoft-Windows-DotNETRuntime:0x100003801D:4\".";

        /// <summary>All built-in profiles.</summary>
        public static IReadOnlyList<TraceProfile> All { get; } = new[]
        {
            new TraceProfile(
                TraceProfileKind.DotNetCommon,
                "dotnet-common",
                new[] { new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, DotNetCommonKeyword) },
                DotNetCommonDescription),

            new TraceProfile(
                TraceProfileKind.DotNetSampledThreadTime,
                "dotnet-sampled-thread-time",
                new[] { new EventPipeProvider(SamplingProfiler.SampleProfilerProviderName, EventLevel.Informational) },
                "Samples .NET thread stacks (~100 Hz) to estimate how much wall clock time code is using."),

            new TraceProfile(
                TraceProfileKind.GcVerbose,
                "gc-verbose",
                new[]
                {
                    new EventPipeProvider(
                        name: "Microsoft-Windows-DotNETRuntime",
                        eventLevel: EventLevel.Verbose,
                        keywords: (long)ClrTraceEventParser.Keywords.GC |
                                  (long)ClrTraceEventParser.Keywords.GCHandle |
                                  (long)ClrTraceEventParser.Keywords.Exception)
                },
                "Tracks GC collections and samples object allocations."),

            new TraceProfile(
                TraceProfileKind.GcCollect,
                "gc-collect",
                new[]
                {
                    new EventPipeProvider(
                        name: "Microsoft-Windows-DotNETRuntime",
                        eventLevel: EventLevel.Informational,
                        keywords: (long)ClrTraceEventParser.Keywords.GC),
                    new EventPipeProvider(
                        name: "Microsoft-Windows-DotNETRuntimePrivate",
                        eventLevel: EventLevel.Informational,
                        keywords: (long)ClrTraceEventParser.Keywords.GC)
                },
                "Tracks GC collections only at very low overhead.")
            {
                RundownKeyword = (long)ClrTraceEventParser.Keywords.GC,
                RetryStrategy = RetryStrategy.DropKeywordDropRundown
            },

            new TraceProfile(
                TraceProfileKind.Database,
                "database",
                new[]
                {
                    new EventPipeProvider(
                        name: "System.Threading.Tasks.TplEventSource",
                        eventLevel: EventLevel.Informational,
                        keywords: (long)TplEtwProviderTraceEventParser.Keywords.TasksFlowActivityIds),
                    new EventPipeProvider(
                        name: "Microsoft-Diagnostics-DiagnosticSource",
                        eventLevel: EventLevel.Verbose,
                        keywords: (long)DiagnosticSourceKeywords.Messages | (long)DiagnosticSourceKeywords.Events,
                        arguments: new Dictionary<string, string>
                        {
                            {
                                "FilterAndPayloadSpecs",
                                "SqlClientDiagnosticListener/System.Data.SqlClient.WriteCommandBefore@Activity1Start:-Command;Command.CommandText;ConnectionId;Operation;Command.Connection.ServerVersion;Command.CommandTimeout;Command.CommandType;Command.Connection.ConnectionString;Command.Connection.Database;Command.Connection.DataSource;Command.Connection.PacketSize\r\n" +
                                "SqlClientDiagnosticListener/System.Data.SqlClient.WriteCommandAfter@Activity1Stop:\r\n" +
                                "Microsoft.EntityFrameworkCore/Microsoft.EntityFrameworkCore.Database.Command.CommandExecuting@Activity2Start:-Command.CommandText;Command;ConnectionId;IsAsync;Command.Connection.ClientConnectionId;Command.Connection.ServerVersion;Command.CommandTimeout;Command.CommandType;Command.Connection.ConnectionString;Command.Connection.Database;Command.Connection.DataSource;Command.Connection.PacketSize\r\n" +
                                "Microsoft.EntityFrameworkCore/Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted@Activity2Stop:"
                            }
                        })
                },
                "Captures ADO.NET and Entity Framework database commands."),
        };

        /// <summary>
        /// The profiles used when no profile, provider or CLR event is configured, matching
        /// the `dotnet-trace collect` default.
        /// </summary>
        public const TraceProfileKind DefaultProfiles = TraceProfileKind.Default;

        /// <summary>Finds a profile by kind, or null when <paramref name="kind"/> is not a single known profile.</summary>
        public static TraceProfile Find(TraceProfileKind kind) => All.FirstOrDefault(p => p.Kind == kind);

        /// <summary>
        /// Finds a profile by its CLI name (case-insensitive), or null when there is no such
        /// profile. Useful when mapping user input onto <see cref="TraceProfileKind"/>.
        /// </summary>
        public static TraceProfile Find(string name) =>
            All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Expands a (possibly combined) <paramref name="kinds"/> into the profiles it names.</summary>
        public static IEnumerable<TraceProfile> Expand(TraceProfileKind kinds)
        {
            foreach (TraceProfile profile in All)
            {
                if ((kinds & profile.Kind) == profile.Kind)
                {
                    yield return profile;
                }
            }
        }

        /// <summary>
        /// Throws when <paramref name="kinds"/> contains bits that do not correspond to a known
        /// profile, so a typo'd cast fails loudly instead of silently tracing nothing.
        /// </summary>
        internal static void Validate(TraceProfileKind kinds)
        {
            TraceProfileKind known = TraceProfileKind.None;
            foreach (TraceProfile profile in All)
            {
                known |= profile.Kind;
            }

            TraceProfileKind unknown = kinds & ~known;
            if (unknown != TraceProfileKind.None)
            {
                throw new DiagnosticToolException(
                    $"Unknown trace profile flag(s): {unknown}. Known profiles: {string.Join(", ", All.Select(p => p.Kind.ToString()))}");
            }
        }

        /// <summary>
        /// Keywords for the DiagnosticSourceEventSource provider.
        /// </summary>
        /// <remarks>See https://github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.DiagnosticSource/src/System/Diagnostics/DiagnosticSourceEventSource.cs</remarks>
        private enum DiagnosticSourceKeywords : long
        {
            Messages = 0x1,
            Events = 0x2,
            IgnoreShortCutKeywords = 0x0800,
            AspNetCoreHosting = 0x1000,
            EntityFrameworkCoreCommands = 0x2000
        }
    }
}
