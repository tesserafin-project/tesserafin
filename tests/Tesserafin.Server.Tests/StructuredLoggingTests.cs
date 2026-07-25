using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Tesserafin.Server.Helpers;
using Xunit;

namespace Tesserafin.Server.Tests
{
    /// <summary>
    /// The JSON-lines container logging contract from #91 / [A5].
    /// </summary>
    public class StructuredLoggingTests
    {
        [Fact]
        public void JsonFormatter_EmitsOneValidJsonObjectPerLine()
        {
            var lines = Emit(logger =>
            {
                logger.Information("first");
                logger.Warning("second");
            });

            Assert.Equal(2, lines.Count);
            foreach (var line in lines)
            {
                Assert.Equal(JsonValueKind.Object, line.ValueKind);
                Assert.False(string.IsNullOrEmpty(line.GetProperty("timestamp").GetString()));
                Assert.False(string.IsNullOrEmpty(line.GetProperty("level").GetString()));
            }

            Assert.Equal("Information", lines[0].GetProperty("level").GetString());
            Assert.Equal("first", lines[0].GetProperty("message").GetString());
            Assert.Equal("Warning", lines[1].GetProperty("level").GetString());
        }

        [Fact]
        public void JsonFormatter_KeepsTheA4SelectionEventSearchable()
        {
            // The shape MediaEncoder logs once per start (#90 / [A4]). The point of A5 is that this
            // event stays queryable by field, rather than collapsing into an opaque sentence.
            var lines = Emit(logger => logger.Information(
                "Hardware acceleration decision: Mode={Mode} Backend={Backend} Reason={Reason} ConfiguredBackend={ConfiguredBackend} CandidatesConsidered={CandidatesConsidered}",
                "Software",
                "none",
                "AllProbesFailed",
                "vaapi",
                2));

            var root = Assert.Single(lines);
            Assert.Equal("Software", root.GetProperty("Mode").GetString());
            Assert.Equal("none", root.GetProperty("Backend").GetString());
            Assert.Equal("AllProbesFailed", root.GetProperty("Reason").GetString());
            Assert.Equal("vaapi", root.GetProperty("ConfiguredBackend").GetString());
            Assert.Equal(2, root.GetProperty("CandidatesConsidered").GetInt32());
        }

        [Fact]
        public void JsonFormatter_KeepsExceptionsInTheirOwnField()
        {
            var lines = Emit(logger => logger.Error(
                new InvalidOperationException("probe exploded"),
                "Database health probe failed for {Component}",
                "sqlite"));

            var root = Assert.Single(lines);
            var exception = root.GetProperty("exception").GetString();
            Assert.Contains("InvalidOperationException", exception, StringComparison.Ordinal);
            Assert.Contains("probe exploded", exception, StringComparison.Ordinal);

            // The exception is a field of its own; it is not concatenated into the message.
            Assert.DoesNotContain("probe exploded", root.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Equal("sqlite", root.GetProperty("Component").GetString());
        }

        [Theory]
        [InlineData(null, false, null)]
        [InlineData("json", true, null)]
        [InlineData("JSON", true, null)]
        [InlineData("text", false, null)]
        [InlineData("yaml", false, "yaml")]
        public void Read_ResolvesTheFormat(string? configured, bool expectJson, string? expectedRejectedFormat)
        {
            var options = LoggingEnvironmentOptions.Read(Build((LoggingEnvironmentOptions.LogFormatKey, configured)));

            Assert.Equal(expectJson, options.UseJsonConsole);
            Assert.Null(options.MinimumLevel);
            Assert.Equal(expectedRejectedFormat, options.RejectedFormat);
        }

        [Theory]
        [InlineData("Debug", "Debug", null)]
        [InlineData("debug", "Debug", null)]
        [InlineData("  Warning  ", "Warning", null)]
        [InlineData("loud", null, "loud")]
        [InlineData("", null, null)]
        public void Read_ValidatesTheLevel(string? configured, string? expectedLevel, string? expectedRejected)
        {
            var options = LoggingEnvironmentOptions.Read(Build((LoggingEnvironmentOptions.LogLevelKey, configured)));

            Assert.Equal(expectedLevel, options.MinimumLevel);
            Assert.Equal(expectedRejected, options.RejectedLevel);
        }

        [Fact]
        public void BuildSerilogConfiguration_JsonBranch_WithholdsConfiguredSinksButKeepsLevels()
        {
            var source = Build(
                ("Serilog:MinimumLevel:Default", "Information"),
                ("Serilog:MinimumLevel:Override:Microsoft", "Warning"),
                ("Serilog:WriteTo:0:Name", "Console"),
                ("Serilog:WriteTo:0:Args:outputTemplate", "[{Level}] {Message}"),
                (LoggingEnvironmentOptions.LogLevelKey, "Debug"),
                (LoggingEnvironmentOptions.LogFormatKey, "json"));
            var options = LoggingEnvironmentOptions.Read(source);

            var effective = StructuredLogging.BuildSerilogConfiguration(source, options, dropConfiguredSinks: true);

            Assert.Null(effective["Serilog:WriteTo:0:Name"]);
            Assert.Null(effective["Serilog:WriteTo:0:Args:outputTemplate"]);
            Assert.Equal("Warning", effective["Serilog:MinimumLevel:Override:Microsoft"]);
            Assert.Equal("Debug", effective["Serilog:MinimumLevel:Default"]);
        }

        [Fact]
        public void BuildSerilogConfiguration_TextBranch_KeepsSinksAndAppliesTheLevel()
        {
            var source = Build(
                ("Serilog:MinimumLevel:Default", "Information"),
                ("Serilog:WriteTo:0:Name", "Console"),
                (LoggingEnvironmentOptions.LogLevelKey, "Error"));
            var options = LoggingEnvironmentOptions.Read(source);

            var effective = StructuredLogging.BuildSerilogConfiguration(source, options, dropConfiguredSinks: false);

            Assert.Equal("Console", effective["Serilog:WriteTo:0:Name"]);
            Assert.Equal("Error", effective["Serilog:MinimumLevel:Default"]);
        }

        [Fact]
        public void BuildSerilogConfiguration_WithoutOverrides_ReturnsTheSourceUnchanged()
        {
            var source = Build(("Serilog:MinimumLevel:Default", "Information"));
            var options = LoggingEnvironmentOptions.Read(source);

            Assert.Same(source, StructuredLogging.BuildSerilogConfiguration(source, options, dropConfiguredSinks: false));
        }

        [Fact]
        public void ConfiguredLevel_ChangesWhichEventsAreEmitted()
        {
            Assert.DoesNotContain("only visible at Debug", EmitAtConfiguredLevel(null), StringComparison.Ordinal);
            Assert.Contains("only visible at Debug", EmitAtConfiguredLevel("Debug"), StringComparison.Ordinal);
        }

        private static string EmitAtConfiguredLevel(string? level)
        {
            var source = Build(
                ("Serilog:MinimumLevel:Default", "Information"),
                (LoggingEnvironmentOptions.LogLevelKey, level));
            var options = LoggingEnvironmentOptions.Read(source);
            var effective = StructuredLogging.BuildSerilogConfiguration(source, options, dropConfiguredSinks: true);

            var buffer = new StringWriter();
            using (var logger = new LoggerConfiguration()
                       .ReadFrom.Configuration(effective)
                       .WriteTo.Sink(new TextWriterSink(StructuredLogging.CreateJsonFormatter(), buffer))
                       .CreateLogger())
            {
                logger.Debug("only visible at Debug");
            }

            return buffer.ToString();
        }

        private static List<JsonElement> Emit(Action<ILogger> write)
        {
            var buffer = new StringWriter();
            using (var logger = new LoggerConfiguration()
                       .MinimumLevel.Verbose()
                       .WriteTo.Sink(new TextWriterSink(StructuredLogging.CreateJsonFormatter(), buffer))
                       .CreateLogger())
            {
                write(logger);
            }

            // Every emitted line must parse. A single non-JSON line breaks the container contract,
            // so this is asserted for all of them, not just the one under test.
            var parsed = new List<JsonElement>();
            foreach (var line in buffer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var document = JsonDocument.Parse(line);
                parsed.Add(document.RootElement.Clone());
            }

            return parsed;
        }

        private static IConfiguration Build(params (string Key, string? Value)[] values)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
                .Build();

        /// <summary>
        /// Captures formatter output verbatim, so the assertions see exactly the bytes the console
        /// sink would have written.
        /// </summary>
        private sealed class TextWriterSink : ILogEventSink
        {
            private readonly ITextFormatter _formatter;
            private readonly TextWriter _writer;

            public TextWriterSink(ITextFormatter formatter, TextWriter writer)
            {
                _formatter = formatter;
                _writer = writer;
            }

            public void Emit(LogEvent logEvent) => _formatter.Format(logEvent, _writer);
        }
    }
}
