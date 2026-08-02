using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Tesserafin.Server.Helpers;

namespace Tesserafin.Server.Tests.LogForging
{
    /// <summary>
    /// Hands production code a real <see cref="ILogger{TCategoryName}"/> whose events reach one of
    /// the two formatters this server actually ships, and keeps the bytes those formatters wrote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The point of the tranche these tests belong to is a <em>physical</em> property of the log:
    /// how many records a single logging call produces, and who chose the timestamp, level and
    /// category on each of them. A captured <c>ILogger</c> argument cannot show that — it stops one
    /// layer above the formatter, which is exactly the layer where the defect lives. So the probe
    /// wires <c>Serilog</c> into <c>Microsoft.Extensions.Logging</c> the way the server does and
    /// renders through the same templates.
    /// </para>
    /// <para>
    /// The text template is copied verbatim from
    /// <c>Tesserafin.Server/Resources/Configuration/logging.json</c>, the default outside the
    /// container image. The JSON-lines formatter is the one the container selects with
    /// <c>TESSERAFIN_LOG_FORMAT=json</c>.
    /// </para>
    /// </remarks>
    internal sealed partial class RealFormatterLogProbe : IDisposable
    {
        /// <summary>
        /// A forged record prefix: a timestamp, a level and a category the caller chose. If it ever
        /// starts a physical record of its own, the reader can no longer tell it from a real one.
        /// </summary>
        public const string ForgedRecordPrefix =
            "[12:00:00.000] [ERR] [1] Tesserafin.Security: administrator account deleted by mallory";

        private const string ConsoleOutputTemplate =
            "[{Timestamp:HH:mm:ss.fff}] [{Level:u3}] [{ThreadId}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

        private readonly StringWriter _buffer;
        private readonly Logger _logger;
        private readonly SerilogLoggerFactory _factory;

        private RealFormatterLogProbe(ITextFormatter formatter, LogEventLevel minimumLevel)
        {
            _buffer = new StringWriter();
            _logger = new LoggerConfiguration()
                .MinimumLevel.Is(minimumLevel)
                .Enrich.WithProperty("ThreadId", 1)
                .WriteTo.Sink(new CapturingSink(formatter, _buffer))
                .CreateLogger();
            _factory = new SerilogLoggerFactory(_logger, dispose: false);
        }

        /// <summary>
        /// Gets the factory production code should take its logger from.
        /// </summary>
        public ILoggerFactory Factory => _factory;

        /// <summary>
        /// Gets everything the formatter wrote, byte for byte.
        /// </summary>
        public string Raw => _buffer.ToString();

        /// <summary>
        /// Renders through the text output template: the shipped default outside the container.
        /// </summary>
        /// <param name="minimumLevel">
        /// The minimum level. <see cref="LogEventLevel.Information"/> is what
        /// <c>Tesserafin.Server/Resources/Configuration/logging.json</c> ships;
        /// <see cref="LogEventLevel.Verbose"/> models an operator who raised the verbosity, which
        /// is the only way a <c>LogDebug</c> statement is written at all.
        /// </param>
        /// <returns>The probe.</returns>
        public static RealFormatterLogProbe Text(LogEventLevel minimumLevel = LogEventLevel.Information)
            => new(new MessageTemplateTextFormatter(ConsoleOutputTemplate, CultureInfo.InvariantCulture), minimumLevel);

        /// <summary>
        /// Renders through the JSON-lines formatter the container image selects.
        /// </summary>
        /// <param name="minimumLevel">The minimum level.</param>
        /// <returns>The probe.</returns>
        public static RealFormatterLogProbe Json(LogEventLevel minimumLevel = LogEventLevel.Information)
            => new(StructuredLogging.CreateJsonFormatter(), minimumLevel);

        /// <summary>
        /// Counts the physical records the text formatter emitted, by counting the line starts that
        /// carry a record prefix. Stack-trace continuation lines do not carry one, so an event that
        /// legitimately spans several lines still counts as one record.
        /// </summary>
        /// <returns>The number of physical records.</returns>
        public int TextRecordCount() => RecordPrefix().Count(Raw);

        /// <summary>
        /// Gets the non-empty lines the formatter wrote.
        /// </summary>
        /// <returns>The lines.</returns>
        public string[] Lines() => Raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        /// <summary>
        /// Creates a logger for <typeparamref name="T"/>, as dependency injection would.
        /// </summary>
        /// <typeparam name="T">The category.</typeparam>
        /// <returns>The logger.</returns>
        public ILogger<T> LoggerFor<T>() => _factory.CreateLogger<T>();

        public void Dispose()
        {
            _factory.Dispose();
            _logger.Dispose();
            _buffer.Dispose();
        }

        [GeneratedRegex(@"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] \[", RegexOptions.Multiline)]
        private static partial Regex RecordPrefix();

        private sealed class CapturingSink : ILogEventSink
        {
            private readonly ITextFormatter _formatter;
            private readonly TextWriter _writer;

            public CapturingSink(ITextFormatter formatter, TextWriter writer)
            {
                _formatter = formatter;
                _writer = writer;
            }

            public void Emit(LogEvent logEvent) => _formatter.Format(logEvent, _writer);
        }
    }
}
