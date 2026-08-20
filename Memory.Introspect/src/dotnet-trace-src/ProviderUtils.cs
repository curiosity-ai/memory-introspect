// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Adapted from https://github.com/dotnet/diagnostics/blob/main/src/Tools/dotnet-trace/ProviderUtils.cs
// The console/IConsole plumbing is replaced with an optional TextWriter log.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Memory.Introspect.Diagnostics.NETCore.Client;

namespace Memory.Introspect.Trace
{
    /// <summary>
    /// Parses `dotnet-trace` style provider specifications and merges them with profiles and
    /// CLR event keyword lists into a single provider collection.
    /// </summary>
    public static class ProviderUtils
    {
        public const string CLREventProviderName = "Microsoft-Windows-DotNETRuntime";

        private const EventLevel DefaultEventLevel = EventLevel.Informational;
        private const long DefaultKeywords = 0;

        // Keep this in sync with the runtime repo's clretwall.man
        private static readonly Dictionary<string, long> CLREventKeywords = new(StringComparer.InvariantCultureIgnoreCase)
        {
            { "gc", 0x1 },
            { "gchandle", 0x2 },
            { "fusion", 0x4 },
            { "assemblyloader", 0x4 },
            { "loader", 0x8 },
            { "jit", 0x10 },
            { "ngen", 0x20 },
            { "startenumeration", 0x40 },
            { "endenumeration", 0x80 },
            { "security", 0x400 },
            { "appdomainresourcemanagement", 0x800 },
            { "jittracing", 0x1000 },
            { "interop", 0x2000 },
            { "contention", 0x4000 },
            { "exception", 0x8000 },
            { "threading", 0x10000 },
            { "jittedmethodiltonativemap", 0x20000 },
            { "overrideandsuppressngenevents", 0x40000 },
            { "type", 0x80000 },
            { "gcheapdump", 0x100000 },
            { "gcsampledobjectallocationhigh", 0x200000 },
            { "gcheapsurvivalandmovement", 0x400000 },
            { "gcheapcollect", 0x800000 },
            { "managedheadcollect", 0x800000 },
            { "gcheapandtypenames", 0x1000000 },
            { "gcsampledobjectallocationlow", 0x2000000 },
            { "perftrack", 0x20000000 },
            { "stack", 0x40000000 },
            { "threadtransfer", 0x80000000 },
            { "debugger", 0x100000000 },
            { "monitoring", 0x200000000 },
            { "codesymbols", 0x400000000 },
            { "eventsource", 0x800000000 },
            { "compilation", 0x1000000000 },
            { "compilationdiagnostic", 0x2000000000 },
            { "methoddiagnostic", 0x4000000000 },
            { "typediagnostic", 0x8000000000 },
            { "jitinstrumentationdata", 0x10000000000 },
            { "profiler", 0x20000000000 },
            { "waithandle", 0x40000000000 },
            { "allocationsampling", 0x80000000000 },
        };

        /// <summary>The CLR event keyword names accepted by <see cref="ToCLREventPipeProvider"/>.</summary>
        public static IReadOnlyCollection<string> KnownCLREventKeywords => CLREventKeywords.Keys.ToList().AsReadOnly();

        [Flags]
        private enum ProviderSource
        {
            None = 0,
            ProvidersArg = 1,
            CLREventsArg = 2,
            ProfileArg = 4,
        }

        /// <summary>
        /// Merges explicit provider specifications, named profiles and a CLR event keyword list
        /// into a single set of providers, exactly the way `dotnet-trace collect` does.
        /// </summary>
        /// <param name="providersArg">Provider specs, each 'Name[:Keywords[:Level[:KeyValueArgs]]]'.</param>
        /// <param name="clreventsArg">A '+' separated list of CLR event keyword names.</param>
        /// <param name="clreventlevel">The verbosity level for <paramref name="clreventsArg"/>.</param>
        /// <param name="profiles">Named profiles from <see cref="TraceProfiles.All"/>.</param>
        /// <param name="extraProviders">Already strongly-typed providers to merge in.</param>
        /// <param name="log">Optional log for the provider table and warnings.</param>
        public static List<EventPipeProvider> ComputeProviderConfig(
            IReadOnlyList<string> providersArg,
            string clreventsArg,
            string clreventlevel,
            IReadOnlyList<string> profiles,
            IReadOnlyList<EventPipeProvider> extraProviders = null,
            TextWriter log = null)
        {
            log ??= TextWriter.Null;
            Dictionary<string, EventPipeProvider> merged = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ProviderSource> providerSources = new(StringComparer.OrdinalIgnoreCase);

            void AddExplicit(EventPipeProvider provider)
            {
                if (!merged.TryGetValue(provider.Name, out EventPipeProvider existing))
                {
                    merged[provider.Name] = provider;
                    providerSources[provider.Name] = ProviderSource.ProvidersArg;
                }
                else
                {
                    merged[provider.Name] = MergeProviderConfigs(existing, provider);
                }
            }

            if (providersArg is not null)
            {
                foreach (string providerArg in providersArg)
                {
                    AddExplicit(ToProvider(providerArg, log));
                }
            }

            if (extraProviders is not null)
            {
                foreach (EventPipeProvider provider in extraProviders)
                {
                    if (provider is null)
                    {
                        continue;
                    }
                    AddExplicit(provider);
                }
            }

            if (profiles is not null)
            {
                foreach (string profileName in profiles)
                {
                    TraceProfile traceProfile = TraceProfiles.Find(profileName)
                        ?? throw new DiagnosticToolException($"Invalid profile name: {profileName}. Known profiles: {string.Join(", ", TraceProfiles.All.Select(p => p.Name))}");

                    foreach (EventPipeProvider provider in traceProfile.Providers)
                    {
                        // Prefer providers set explicitly over implicit profile configuration.
                        if (!merged.ContainsKey(provider.Name))
                        {
                            merged[provider.Name] = provider;
                            providerSources[provider.Name] = ProviderSource.ProfileArg;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(clreventsArg))
            {
                EventPipeProvider provider = ToCLREventPipeProvider(clreventsArg, clreventlevel);
                if (provider is not null)
                {
                    if (!merged.ContainsKey(provider.Name))
                    {
                        merged[provider.Name] = provider;
                        providerSources[provider.Name] = ProviderSource.CLREventsArg;
                    }
                    else
                    {
                        log.WriteLine("Warning: The CLR provider was already specified through providers or a profile. Ignoring the CLR event keyword list.");
                    }
                }
            }

            List<EventPipeProvider> unifiedProviders = merged.Values.ToList();
            PrintProviders(unifiedProviders, providerSources, log);
            return unifiedProviders;
        }

        private static EventPipeProvider MergeProviderConfigs(EventPipeProvider providerConfigA, EventPipeProvider providerConfigB)
        {
            Debug.Assert(string.Equals(providerConfigA.Name, providerConfigB.Name, StringComparison.OrdinalIgnoreCase));

            EventLevel level = (providerConfigA.EventLevel == EventLevel.LogAlways || providerConfigB.EventLevel == EventLevel.LogAlways)
                ? EventLevel.LogAlways
                : (providerConfigA.EventLevel > providerConfigB.EventLevel ? providerConfigA.EventLevel : providerConfigB.EventLevel);

            if (providerConfigA.Arguments != null && providerConfigB.Arguments != null)
            {
                throw new DiagnosticToolException($"Provider \"{providerConfigA.Name}\" is declared multiple times with filter arguments.");
            }

            return new EventPipeProvider(providerConfigA.Name, level, providerConfigA.Keywords | providerConfigB.Keywords, providerConfigA.Arguments ?? providerConfigB.Arguments);
        }

        private static void PrintProviders(IReadOnlyList<EventPipeProvider> providers, Dictionary<string, ProviderSource> enabledBy, TextWriter log)
        {
            if (ReferenceEquals(log, TextWriter.Null))
            {
                return;
            }

            if (providers.Count == 0)
            {
                log.WriteLine("No .NET providers were configured.");
                return;
            }

            log.WriteLine(string.Format("{0,-40}", "Provider Name") + string.Format("{0,-20}", "Keywords") + string.Format("{0,-20}", "Level") + "Enabled By");
            foreach (EventPipeProvider provider in providers)
            {
                List<string> sources = new();
                if (enabledBy.TryGetValue(provider.Name, out ProviderSource source))
                {
                    if ((source & ProviderSource.ProvidersArg) != 0) { sources.Add("providers"); }
                    if ((source & ProviderSource.CLREventsArg) != 0) { sources.Add("clrevents"); }
                    if ((source & ProviderSource.ProfileArg) != 0) { sources.Add("profile"); }
                }
                log.WriteLine(string.Format("{0,-80}", GetProviderDisplayString(provider)) + string.Join(", ", sources));
            }
        }

        private static string GetProviderDisplayString(EventPipeProvider provider) =>
            string.Format("{0,-40}", provider.Name) + string.Format("0x{0,-18}", $"{provider.Keywords:X16}") + string.Format("{0,-8}", provider.EventLevel + $"({(int)provider.EventLevel})");

        /// <summary>
        /// Builds a Microsoft-Windows-DotNETRuntime provider from a '+' separated list of CLR
        /// event keyword names, e.g. "gc+gchandle+exception".
        /// </summary>
        public static EventPipeProvider ToCLREventPipeProvider(string clreventslist, string clreventlevel)
        {
            if (string.IsNullOrEmpty(clreventslist))
            {
                return null;
            }

            string[] clrevents = clreventslist.Split('+');
            long clrEventsKeywordsMask = 0;
            for (int i = 0; i < clrevents.Length; i++)
            {
                if (CLREventKeywords.TryGetValue(clrevents[i], out long keyword))
                {
                    clrEventsKeywordsMask |= keyword;
                }
                else
                {
                    throw new DiagnosticToolException($"{clrevents[i]} is not a valid CLR event keyword");
                }
            }

            EventLevel level = EventLevel.Informational;
            if (!string.IsNullOrEmpty(clreventlevel))
            {
                level = GetEventLevel(clreventlevel);
            }

            return new EventPipeProvider(CLREventProviderName, level, clrEventsKeywordsMask, null);
        }

        private static EventLevel GetEventLevel(string token)
        {
            if (int.TryParse(token, out int level) && level >= 0)
            {
                return level > (int)EventLevel.Verbose ? EventLevel.Verbose : (EventLevel)level;
            }

            switch (token.ToLowerInvariant())
            {
                case "critical": return EventLevel.Critical;
                case "error": return EventLevel.Error;
                case "informational": return EventLevel.Informational;
                case "logalways": return EventLevel.LogAlways;
                case "verbose": return EventLevel.Verbose;
                case "warning": return EventLevel.Warning;
                default: throw new DiagnosticToolException($"Unknown EventLevel: {token}");
            }
        }

        /// <summary>
        /// Parses a single provider specification of the form
        /// 'Name[:Keywords[:Level[:KeyValueArgs]]]', where Keywords is a hexadecimal mask and
        /// KeyValueArgs is '[key1=value1][;key2=value2]'.
        /// </summary>
        public static EventPipeProvider ToProvider(string provider, TextWriter log = null)
        {
            log ??= TextWriter.Null;

            if (string.IsNullOrWhiteSpace(provider))
            {
                throw new ArgumentNullException(nameof(provider));
            }

            string[] tokens = provider.Split(new[] { ':' }, 4, StringSplitOptions.None); // Keep empty tokens

            string providerName = tokens.Length > 0 ? tokens[0] : null;

            if (Guid.TryParse(providerName, out _))
            {
                log.WriteLine($"Warning: provider argument {providerName} appears to be a GUID which is not supported. Providers need to be referenced by their textual name.");
            }

            if (string.IsNullOrWhiteSpace(providerName))
            {
                throw new DiagnosticToolException("Provider name was not specified.");
            }

            long keywords = tokens.Length > 1 && !string.IsNullOrWhiteSpace(tokens[1])
                ? Convert.ToInt64(tokens[1], 16)
                : DefaultKeywords;

            EventLevel eventLevel = tokens.Length > 2 && !string.IsNullOrWhiteSpace(tokens[2])
                ? GetEventLevel(tokens[2])
                : DefaultEventLevel;

            string filterData = tokens.Length > 3 ? tokens[3] : null;
            Dictionary<string, string> argument = string.IsNullOrWhiteSpace(filterData) ? null : ParseArgumentString(filterData);
            return new EventPipeProvider(providerName, eventLevel, keywords, argument);
        }

        private static Dictionary<string, string> ParseArgumentString(string argument)
        {
            if (argument == "")
            {
                return null;
            }
            Dictionary<string, string> argumentDict = new();

            int keyStart = 0;
            int keyEnd = 0;
            int valStart = 0;
            int valEnd = 0;
            int curIdx = 0;
            bool inQuote = false;
            argument = Regex.Unescape(argument);
            foreach (char c in argument)
            {
                if (inQuote)
                {
                    if (c == '\"')
                    {
                        inQuote = false;
                    }
                }
                else
                {
                    if (c == '=')
                    {
                        keyEnd = curIdx;
                        valStart = curIdx + 1;
                    }
                    else if (c == ';')
                    {
                        valEnd = curIdx;
                        AddKeyValueToArgumentDict(argumentDict, argument, keyStart, keyEnd, valStart, valEnd);
                        keyStart = curIdx + 1; // new key starts
                    }
                    else if (c == '\"')
                    {
                        inQuote = true;
                    }
                }
                curIdx += 1;
            }
            if (valStart > valEnd)
            {
                valEnd = curIdx;
            }
            if (keyStart < keyEnd)
            {
                AddKeyValueToArgumentDict(argumentDict, argument, keyStart, keyEnd, valStart, valEnd);
            }
            return argumentDict;
        }

        private static void AddKeyValueToArgumentDict(Dictionary<string, string> argumentDict, string argument, int keyStart, int keyEnd, int valStart, int valEnd)
        {
            string key = argument.Substring(keyStart, keyEnd - keyStart);
            string val = argument.Substring(valStart, valEnd - valStart);
            if (val.StartsWith("\"") && val.EndsWith("\""))
            {
                val = val.Substring(1, val.Length - 2);
            }
            argumentDict.Add(key, val);
        }
    }
}
