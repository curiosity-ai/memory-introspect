// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

using System.IO;
using System.Runtime.InteropServices;
using Memory.Introspect.Diagnostics.NETCore.Client;
using Microsoft.Internal.Common.Utils;

namespace Memory.Introspect
{
    public partial class Dumper
    {
        /// <summary>
        /// The dump type determines the kinds of information that are collected from the process.
        /// </summary>
        public enum CollectionType
        {
            Full,       // The largest dump containing all memory including the module images.

            Heap,       // A large and relatively comprehensive dump containing module lists, thread lists, all
                        // stacks, exception information, handle information, and all memory except for mapped images.

            Mini,       // A small dump containing module lists, thread lists, exception information and all stacks.

            Triage      // A small dump containing module lists, thread lists, exception information, all stacks and PII removed.
        }

        public Dumper()
        {
        }

        public int Collect(TextWriter stdOutput, TextWriter stdError, int processId, string output, bool diag, bool crashreport, CollectionType type, string name, string diagnosticPort)
        {
            try
            {
                CommandUtils.ResolveProcessForAttach(processId, name, diagnosticPort, string.Empty, out int resolvedProcessId);
                processId = resolvedProcessId;

                if (output == null)
                {
                    // Build timestamp based file path
                    string timestamp = $"{DateTime.Now:yyyyMMdd_HHmmss}";
                    output = Path.Combine(Directory.GetCurrentDirectory(), RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"dump_{timestamp}.dmp" : $"core_{timestamp}");
                }

                // Make sure the dump path is NOT relative. This path could be sent to the runtime
                // process on Linux which may have a different current directory.
                output = Path.GetFullPath(output);

                // Display the type of dump and dump path
                string dumpTypeMessage = null;
                switch (type)
                {
                    case CollectionType.Full:
                        dumpTypeMessage = "full";
                        break;
                    case CollectionType.Heap:
                        dumpTypeMessage = "dump with heap";
                        break;
                    case CollectionType.Mini:
                        dumpTypeMessage = "dump";
                        break;
                    case CollectionType.Triage:
                        dumpTypeMessage = "triage dump";
                        break;
                }
                stdOutput.WriteLine($"Writing {dumpTypeMessage} to {output}");

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (crashreport)
                    {
                        Console.WriteLine("Crash reports not supported on Windows.");
                        return -1;
                    }

                    Windows.CollectDump(processId, output, type);
                }
                else
                {
                    DiagnosticsClient client;
                    if (!string.IsNullOrEmpty(diagnosticPort))
                    {
                        IpcEndpointConfig diagnosticPortConfig = IpcEndpointConfig.Parse(diagnosticPort);
                        if (!diagnosticPortConfig.IsConnectConfig)
                        {
                            Console.WriteLine("dotnet-dump only supports connect mode to a runtime.");
                            return -1;
                        }
                        client = new DiagnosticsClient(diagnosticPortConfig);
                    }
                    else
                    {
                        client = new DiagnosticsClient(processId);
                    }

                    Memory.Introspect.Diagnostics.NETCore.Client.DumpType dumpType = Memory.Introspect.Diagnostics.NETCore.Client.DumpType.Normal;
                    switch (type)
                    {
                        case CollectionType.Full:
                            dumpType = Memory.Introspect.Diagnostics.NETCore.Client.DumpType.Full;
                            break;
                        case CollectionType.Heap:
                            dumpType = Memory.Introspect.Diagnostics.NETCore.Client.DumpType.WithHeap;
                            break;
                        case CollectionType.Mini:
                            dumpType = Memory.Introspect.Diagnostics.NETCore.Client.DumpType.Normal;
                            break;
                        case CollectionType.Triage:
                            dumpType = Memory.Introspect.Diagnostics.NETCore.Client.DumpType.Triage;
                            break;
                    }

                    Memory.Introspect.Diagnostics.NETCore.Client.WriteDumpFlags flags = Memory.Introspect.Diagnostics.NETCore.Client.WriteDumpFlags.None;
                    if (diag)
                    {
                        flags |= Memory.Introspect.Diagnostics.NETCore.Client.WriteDumpFlags.LoggingEnabled;
                    }
                    if (crashreport)
                    {
                        flags |= Memory.Introspect.Diagnostics.NETCore.Client.WriteDumpFlags.CrashReportEnabled;
                    }
                    // Send the command to the runtime to initiate the core dump
                    client.WriteDump(dumpType, output, flags);
                }
            }
            catch (DiagnosticToolException dte)
            {
                stdError.WriteLine($"[ERROR] {dte.Message}");
                return -1;
            }
            catch (Exception ex)
            {
                if (diag)
                {
                    stdError.WriteLine($"{ex}");
                }
                else
                {
                    stdError.WriteLine($"{ex.Message}");
                }
                return -1;
            }

            stdOutput.WriteLine($"Complete");
            return 0;
        }
    }
}
