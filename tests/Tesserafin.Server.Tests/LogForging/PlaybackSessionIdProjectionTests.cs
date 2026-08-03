using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using Tesserafin.Api.Controllers;
using Tesserafin.Controller.MediaEncoding;
using Xunit;

namespace Tesserafin.Server.Tests.LogForging
{
    /// <summary>
    /// <see cref="PlaybackSessionsController"/>'s <c>DELETE</c> success statement used to hand the
    /// route-bound <see cref="PlaybackSessionId"/> to the logger as a typed object. It now hands
    /// over <c>id.Value.ToString("N")</c> instead, so that the projection CodeQL cannot see through
    /// a two-node flow becomes an ordinary, visible expression in application dataflow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That is only a legitimate change if it is a pure re-spelling of the same bytes. It is:
    /// <see cref="PlaybackSessionId"/> is a <c>readonly record struct</c> over a single
    /// <see cref="Guid"/> whose <c>ToString()</c> is declared as <c>Value.ToString("N")</c>, so the
    /// projection is that override's own body. These tests hold that equivalence down at both
    /// layers the change can be observed from: the string the type produces, and the bytes the two
    /// formatters this server actually ships write when the argument's static type changes from a
    /// struct to a <see cref="string"/>.
    /// </para>
    /// <para>
    /// The formatter half is the one that cannot be reasoned about. Serilog captures a
    /// <see cref="string"/> and an unknown value type through different branches of its property
    /// converter, and the JSON-lines renderer used inside the container image emits properties as
    /// first-class fields. Whether those two branches converge on the same bytes is a measurement,
    /// not a deduction.
    /// </para>
    /// </remarks>
    public sealed class PlaybackSessionIdProjectionTests
    {
        /// <summary>
        /// The production template, verbatim. Any drift here and the equivalence below is being
        /// proven about a statement this server does not write.
        /// </summary>
        private const string DeleteTemplate =
            "Playback session {SessionId} deleted (attempt {PlaybackAttemptId}).";

        private const string OrdinaryAttempt = "attempt-7";

        public static TheoryData<string> RepresentativeGuids() => new()
        {
            // The all-zero value: the one Guid a caller can name without knowing any session.
            "00000000-0000-0000-0000-000000000000",
            // Every hexadecimal digit, so a digit-only or letter-only formatting bug cannot hide.
            "01234567-89ab-cdef-0123-456789abcdef",
            // Leading zeroes in every group: the classic place a "trim" bug loses characters.
            "00000001-0002-0003-0004-000000000005",
            // Trailing zeroes, the mirror case.
            "10000000-2000-3000-4000-500000000000",
            // Upper-case input: "N" must normalise, or the rendered id changes case.
            "ABCDEF01-2345-6789-ABCD-EF0123456789",
            // A value whose first group could be read as an integer with a sign.
            "ffffffff-ffff-ffff-ffff-ffffffffffff",
        };

        [Theory]
        [MemberData(nameof(RepresentativeGuids))]
        public void ToString_IsExactlyTheValueProjection(string guid)
        {
            var id = new PlaybackSessionId(Guid.Parse(guid, CultureInfo.InvariantCulture));

            AssertProjectionIsTheSameString(id);
        }

        [Fact]
        public void ToString_IsExactlyTheValueProjection_ForGeneratedIds()
        {
            for (var i = 0; i < 256; i++)
            {
                AssertProjectionIsTheSameString(PlaybackSessionId.NewId());
            }
        }

        [Theory]
        [MemberData(nameof(RepresentativeGuids))]
        public void ToString_IsExactlyTheValueProjection_ForRouteBoundIds(string guid)
        {
            // The pilot's source node is a route-bound value, so the binder's own construction
            // path is the one that has to hold the equivalence, not only the primary constructor.
            Assert.True(PlaybackSessionId.TryParse(guid, CultureInfo.InvariantCulture, out var bound));
            AssertProjectionIsTheSameString(bound);

            AssertProjectionIsTheSameString(PlaybackSessionId.Parse(guid, CultureInfo.InvariantCulture));
        }

        [Fact]
        public void ToString_IsExactlyTheValueProjection_UnderAHostileCulture()
        {
            // "N" is a hexadecimal rendering of 16 bytes; no culture may reach it. A Turkish
            // culture is the standard probe because it is where an accidental culture-sensitive
            // case mapping shows up ("I" -> "ı").
            var original = CultureInfo.CurrentCulture;
            var originalUi = CultureInfo.CurrentUICulture;
            try
            {
                foreach (var name in new[] { "tr-TR", "ar-SA", "de-DE" })
                {
                    var hostile = new CultureInfo(name);
                    CultureInfo.CurrentCulture = hostile;
                    CultureInfo.CurrentUICulture = hostile;
                    Thread.CurrentThread.CurrentCulture = hostile;

                    AssertProjectionIsTheSameString(
                        new PlaybackSessionId(Guid.Parse("ABCDEF01-2345-6789-ABCD-EF0123456789", CultureInfo.InvariantCulture)));
                    AssertProjectionIsTheSameString(PlaybackSessionId.NewId());
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
                CultureInfo.CurrentUICulture = originalUi;
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        [Fact]
        public void TextFormatter_RendersTheProjectionAndTheStructIdentically()
        {
            using var probe = RealFormatterLogProbe.Text();
            var logger = probe.LoggerFor<PlaybackSessionsController>();
            var id = PlaybackSessionId.NewId();

#pragma warning disable CA2254 // The template is a constant; both calls are the same statement.
            logger.LogInformation(DeleteTemplate, id, OrdinaryAttempt);
            logger.LogInformation(DeleteTemplate, id.Value.ToString("N"), OrdinaryAttempt);
#pragma warning restore CA2254

            var lines = probe.Lines();
            Assert.Equal(2, lines.Length);
            Assert.Equal(2, probe.TextRecordCount());

            var before = MessageOf(lines[0]);
            var after = MessageOf(lines[1]);

            Assert.Equal(before, after, StringComparer.Ordinal);
            Assert.Equal(
                "Playback session " + id.Value.ToString("N") + " deleted (attempt attempt-7).",
                after,
                StringComparer.Ordinal);
        }

        [Fact]
        public void JsonFormatter_RendersTheProjectionAndTheStructIdentically()
        {
            using var probe = RealFormatterLogProbe.Json();
            var logger = probe.LoggerFor<PlaybackSessionsController>();
            var id = PlaybackSessionId.NewId();

#pragma warning disable CA2254 // The template is a constant; both calls are the same statement.
            logger.LogInformation(DeleteTemplate, id, OrdinaryAttempt);
            logger.LogInformation(DeleteTemplate, id.Value.ToString("N"), OrdinaryAttempt);
#pragma warning restore CA2254

            var lines = probe.Lines();
            Assert.Equal(2, lines.Length);

            var before = JsonDocument.Parse(lines[0]).RootElement;
            var after = JsonDocument.Parse(lines[1]).RootElement;

            // The rendered message and, more importantly for a structured sink, the property that
            // carries the identifier: a JSON consumer keying on SessionId must not be able to tell
            // the two spellings apart.
            Assert.Equal(before.GetProperty("message").GetString(), after.GetProperty("message").GetString(), StringComparer.Ordinal);
            Assert.Equal(before.GetProperty("SessionId").ValueKind, after.GetProperty("SessionId").ValueKind);
            Assert.Equal(before.GetProperty("SessionId").GetString(), after.GetProperty("SessionId").GetString(), StringComparer.Ordinal);
            Assert.Equal(id.Value.ToString("N"), after.GetProperty("SessionId").GetString(), StringComparer.Ordinal);

            // The sibling argument is untouched by this pilot and must still render as it did.
            Assert.Equal(OrdinaryAttempt, after.GetProperty("PlaybackAttemptId").GetString(), StringComparer.Ordinal);
        }

        [Fact]
        public void Projection_IsOneRecordUnderBothShippedFormatters()
        {
            // Not a hostile-input test — a PlaybackSessionId cannot carry a separator, and
            // manufacturing one would contradict the type invariant. This states the physical
            // property the projection has to keep: one logging call, one physical record.
            using (var text = RealFormatterLogProbe.Text())
            {
#pragma warning disable CA2254
                text.LoggerFor<PlaybackSessionsController>()
                    .LogInformation(DeleteTemplate, PlaybackSessionId.NewId().Value.ToString("N"), OrdinaryAttempt);
#pragma warning restore CA2254
                Assert.Equal(1, text.TextRecordCount());
                Assert.Single(text.Lines());
            }

            using var json = RealFormatterLogProbe.Json();
#pragma warning disable CA2254
            json.LoggerFor<PlaybackSessionsController>()
                .LogInformation(DeleteTemplate, PlaybackSessionId.NewId().Value.ToString("N"), OrdinaryAttempt);
#pragma warning restore CA2254
            Assert.Single(json.Lines());
        }

        private static void AssertProjectionIsTheSameString(PlaybackSessionId id)
        {
            var declared = id.ToString();
            var projected = id.Value.ToString("N");

            Assert.Equal(declared, projected, StringComparer.Ordinal);
            Assert.Equal(32, projected.Length);
            Assert.DoesNotContain('\r', projected);
            Assert.DoesNotContain('\n', projected);
            Assert.All(projected, c => Assert.True(
                (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
                FormattableString.Invariant($"'{c}' is not a lower-case hexadecimal digit in \"{projected}\"")));
        }

        private static string MessageOf(string record)
        {
            var marker = typeof(PlaybackSessionsController).FullName + ": ";
            var index = record.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(index >= 0, record);
            return record[(index + marker.Length)..].TrimEnd('\r');
        }
    }
}
