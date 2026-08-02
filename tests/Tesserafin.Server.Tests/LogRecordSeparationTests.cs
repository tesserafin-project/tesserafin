using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Tesserafin.Extensions;
using Tesserafin.Server.Helpers;
using Xunit;

namespace Tesserafin.Server.Tests
{
    /// <summary>
    /// Proves the physical-record contract through the real Serilog pipeline and the two
    /// formatters this server actually ships, rather than by inspecting the sanitiser alone.
    /// </summary>
    /// <remarks>
    /// The text template is copied verbatim from
    /// <c>Tesserafin.Server/Resources/Configuration/logging.json</c>, which
    /// <c>LoggingEnvironmentOptions</c> documents as the default outside the container image. The
    /// JSON-lines formatter is the one the container selects with
    /// <c>TESSERAFIN_LOG_FORMAT=json</c>. Both are asserted because the defect exists on one and
    /// not the other, and the code must not depend on which an operator chose.
    /// </remarks>
    public class LogRecordSeparationTests
    {
        private const string ConsoleOutputTemplate =
            "[{Timestamp:HH:mm:ss.fff}] [{Level:u3}] [{ThreadId}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

        private const string Hostile =
            "alice\r\n[12:00:00.000] [ERR] [1] Tesserafin.Security: administrator account deleted by bob";

        [Fact]
        public void TextFormatter_UnflattenedHostileValue_ForgesASecondRecord()
        {
            // The defect this contract exists for. If this ever stops holding, the sanitiser is
            // guarding nothing and the tests below prove nothing.
            var records = Render(
                TextFormatter(),
                logger => logger.Information("Authentication request for {UserName}", Hostile));

            Assert.Equal(2, records.Length);
            Assert.Contains("[ERR]", records[1], StringComparison.Ordinal);
            Assert.Contains("administrator account deleted by bob", records[1], StringComparison.Ordinal);
        }

        [Fact]
        public void TextFormatter_FlattenedHostileValue_ProducesExactlyOneRecord()
        {
            var records = Render(
                TextFormatter(),
                logger => logger.Information("Authentication request for {UserName}", Hostile.ToSingleLogLine()));

            Assert.Single(records);

            // The payload is still there and still readable; it just cannot end the record.
            Assert.Contains("administrator account deleted by bob", records[0], StringComparison.Ordinal);
            Assert.Contains("\\r\\n", records[0], StringComparison.Ordinal);
        }

        [Fact]
        public void TextFormatter_FlattenedValue_CannotForgeATimestampLevelOrCategory()
        {
            var records = Render(
                TextFormatter(),
                logger => logger.Information("Authentication request for {UserName}", Hostile.ToSingleLogLine()));

            var record = Assert.Single(records);

            // Exactly one prefix, and it is the real one: the forged "[12:00:00.000] [ERR]" now
            // sits inside the message instead of starting a record of its own.
            Assert.StartsWith("[", record, StringComparison.Ordinal);
            Assert.Contains("[INF]", record, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(record, "[INF]"));
            Assert.Equal("Tesserafin.Probe", ExtractSourceContext(record));
        }

        [Fact]
        public void TextFormatter_OrdinaryValue_IsRenderedByteForByteAsBefore()
        {
            const string Ordinary = "/Items/0f1a?query=value";

            var withSanitiser = Render(
                TextFormatter(),
                logger => logger.Information("URL {Url}", Ordinary.ToSingleLogLine()));
            var without = Render(
                TextFormatter(),
                logger => logger.Information("URL {Url}", Ordinary));

            Assert.Single(withSanitiser);
            Assert.Equal(StripTimestamp(without[0]), StripTimestamp(withSanitiser[0]));
        }

        [Fact]
        public void JsonFormatter_WasAlreadySafe_AndStaysOneRecordAndOneObject()
        {
            var unflattened = Render(
                StructuredLogging.CreateJsonFormatter(),
                logger => logger.Information("Authentication request for {UserName}", Hostile));
            var flattened = Render(
                StructuredLogging.CreateJsonFormatter(),
                logger => logger.Information("Authentication request for {UserName}", Hostile.ToSingleLogLine()));

            // The container formatter escapes the separators itself, so it emitted one record
            // before this change and still does. Recorded so nobody reads the sanitiser as the
            // reason the container is safe.
            Assert.Single(unflattened);
            Assert.Single(flattened);

            using var document = JsonDocument.Parse(flattened[0]);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);

            // The structured property survives under its template name, and the event keeps its
            // level: the sanitiser changes the value, never the shape of the event.
            Assert.Equal("Information", document.RootElement.GetProperty("level").GetString());
            var userName = document.RootElement.GetProperty("UserName").GetString();
            Assert.NotNull(userName);
            Assert.DoesNotContain('\n', userName);
        }

        [Fact]
        public void JsonFormatter_StructuredTemplateAndPropertiesRemainIntact()
        {
            var records = Render(
                StructuredLogging.CreateJsonFormatter(),
                logger => logger.Information(
                    "Authorizing device with code {Code} to login as user {UserId}",
                    "12\r\n34".ToSingleLogLine(),
                    Guid.Empty));

            using var document = JsonDocument.Parse(Assert.Single(records));
            Assert.Equal("12\\r\\n34", document.RootElement.GetProperty("Code").GetString());
            Assert.Equal(Guid.Empty.ToString(), document.RootElement.GetProperty("UserId").GetString());
        }

        private static ITextFormatter TextFormatter()
            => new MessageTemplateTextFormatter(ConsoleOutputTemplate, CultureInfo.InvariantCulture);

        private static string ExtractSourceContext(string record)
        {
            // "[ts] [INF] [1] <SourceContext>: message"
            var start = record.IndexOf("] [1] ", StringComparison.Ordinal) + "] [1] ".Length;
            var end = record.IndexOf(':', start);
            return record[start..end];
        }

        private static string StripTimestamp(string record)
        {
            var end = record.IndexOf(']', StringComparison.Ordinal);
            return record[(end + 1)..];
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        private static string[] Render(ITextFormatter formatter, Action<ILogger> write)
        {
            var buffer = new StringWriter();
            using (var logger = new LoggerConfiguration()
                       .MinimumLevel.Verbose()
                       .Enrich.WithProperty("ThreadId", 1)
                       .Enrich.WithProperty("SourceContext", "Tesserafin.Probe")
                       .WriteTo.Sink(new CapturingSink(formatter, buffer))
                       .CreateLogger())
            {
                write(logger);
            }

            return buffer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

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
