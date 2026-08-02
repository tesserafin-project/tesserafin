using Microsoft.Extensions.Logging;

namespace Tesserafin.Providers.Tests.Plugins
{
    /// <summary>One line a provider asked its logger to write.</summary>
    /// <param name="Level">The severity.</param>
    /// <param name="Message">The formatted message.</param>
    public readonly record struct LogEntry(LogLevel Level, string Message);
}
