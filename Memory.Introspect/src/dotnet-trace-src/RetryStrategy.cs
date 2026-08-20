// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Adapted from https://github.com/dotnet/diagnostics/blob/main/src/Tools/dotnet-trace/RetryStrategies.cs
//
// This enum describes the various strategies for retrying a trace session start.
// The rough idea is that these numbers form a state machine.
// Any time a session start fails, a retry will be attempted by matching the
// condition of the config as well as this strategy number to generate a
// modified config as well as a modified strategy.
//
// This is designed with forward compatibility in mind. We might have newer
// capabilities that only exist in newer runtimes, but we will never know exactly
// how we should retry. So this gives us a way to encode the retry strategy in the
// profiles without having to introduce new concepts.

namespace Memory.Introspect.Trace
{
    /// <summary>
    /// Describes how <see cref="TraceCollector"/> should retry starting an EventPipe session
    /// when the target runtime rejects the requested configuration.
    /// </summary>
    public enum RetryStrategy
    {
        /// <summary>The configuration uses no optional features, so a failure is fatal.</summary>
        NothingToRetry = 0,

        /// <summary>Retry with the standard rundown keyword instead of the custom one.</summary>
        DropKeywordKeepRundown = 1,

        /// <summary>Retry with rundown disabled entirely.</summary>
        DropKeywordDropRundown = 2,

        /// <summary>Never retry.</summary>
        ForbiddenToRetry = 3
    }
}
