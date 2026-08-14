using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tesserafin.Server.Diagnostics.RemoteAccess;

namespace Tesserafin.Server.Integration.Tests;

/// <summary>
/// Captures everything the application logs, including templates, state, scopes and exceptions.
/// </summary>
/// <remarks>
/// Deliberately captures MORE than the formatted message. A hostname leaked as a structured state
/// value or a scope would never appear in the rendered string of a well-written log line, and that
/// is exactly the leak worth catching: telemetry pipelines ship the structured values.
/// </remarks>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentBag<string> _captured = new();

    public IReadOnlyCollection<string> Captured => _captured;

    public void Clear() => _captured.Clear();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _captured);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentBag<string> _sink;

        public CapturingLogger(string category, ConcurrentBag<string> sink)
        {
            _category = category;
            _sink = sink;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            _sink.Add($"scope:{_category}:{state}");
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    _sink.Add($"scope-state:{_category}:{pair.Key}={pair.Value}");
                }
            }

            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _sink.Add($"message:{_category}:{formatter(state, exception)}");
            _sink.Add($"template:{_category}:{state}");
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    _sink.Add($"state:{_category}:{pair.Key}={pair.Value}");
                }
            }

            if (exception is not null)
            {
                _sink.Add($"exception:{_category}:{exception}");
            }
        }
    }
}
