#pragma warning disable SA1649 // File name should match first type name

using System;
using Microsoft.Extensions.Logging;

namespace Tesserafin.Providers.Tests.Plugins
{
    /// <summary>A generic <see cref="ILogger{TCategoryName}"/> wrapper over <see cref="RecordingLogger"/>.</summary>
    /// <typeparam name="T">The log category.</typeparam>
    public sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly RecordingLogger _inner;

        /// <summary>Initializes a new instance of the <see cref="RecordingLogger{T}"/> class.</summary>
        /// <param name="inner">The recording logger to delegate to.</param>
        public RecordingLogger(RecordingLogger inner)
        {
            _inner = inner;
        }

        /// <inheritdoc />
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => _inner.BeginScope(state);

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
