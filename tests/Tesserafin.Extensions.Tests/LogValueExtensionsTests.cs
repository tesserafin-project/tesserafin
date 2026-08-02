using Tesserafin.Extensions;
using Xunit;

namespace Tesserafin.Extensions.Tests
{
    /// <summary>
    /// The contract of <see cref="LogValueExtensions.ToSingleLogLine"/>: it flattens exactly the
    /// two characters that were measured to end a physical log record, and touches nothing else.
    /// </summary>
    public static class LogValueExtensionsTests
    {
        [Theory]
        [InlineData("alice")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("/Items/0f1a?query=value&other=1")]
        [InlineData("C:\\media\\Films\\Le Fabuleux Destin d'Amélie Poulain (2001).mkv")]
        [InlineData("already escaped \\r\\n stays literal")]
        [InlineData("tab\tand\vvertical tab survive")]
        public static void OrdinaryValue_IsReturnedCharacterForCharacterUnchanged(string value)
        {
            Assert.Equal(value, value.ToSingleLogLine());
        }

        [Fact]
        public static void ValueWithoutSeparators_IsReturnedByReference()
        {
            // Not merely equal: the common path must not allocate.
            const string Value = "an ordinary value";
            Assert.Same(Value, Value.ToSingleLogLine());
        }

        [Fact]
        public static void Null_IsReturnedAsNull()
        {
            string? value = null;
            Assert.Null(value.ToSingleLogLine());
        }

        [Fact]
        public static void Empty_IsReturnedAsEmpty()
        {
            Assert.Equal(string.Empty, string.Empty.ToSingleLogLine());
        }

        [Theory]
        [InlineData("a\rb", "a\\rb")]
        [InlineData("a\nb", "a\\nb")]
        [InlineData("a\r\nb", "a\\r\\nb")]
        [InlineData("a\n\rb", "a\\n\\rb")]
        [InlineData("\r", "\\r")]
        [InlineData("\n", "\\n")]
        [InlineData("\r\n\r\n", "\\r\\n\\r\\n")]
        public static void CarriageReturnAndLineFeed_BecomeTheirTwoCharacterEscapes(string value, string expected)
        {
            Assert.Equal(expected, value.ToSingleLogLine());
        }

        [Fact]
        public static void ForgedRecord_LosesItsSeparatorButKeepsItsText()
        {
            const string Hostile =
                "alice\r\n[12:00:00.000] [ERR] [1] Tesserafin.Security: administrator account deleted by bob";

            var flattened = Hostile.ToSingleLogLine();

            Assert.NotNull(flattened);
            Assert.DoesNotContain('\r', flattened);
            Assert.DoesNotContain('\n', flattened);

            // Nothing is redacted, hashed or truncated: every character of the payload survives,
            // it simply can no longer terminate the record it is written into.
            Assert.Contains("administrator account deleted by bob", flattened, System.StringComparison.Ordinal);
            Assert.Equal(Hostile.Length + 2, flattened.Length);
        }

        [Theory]
        [InlineData("a\u2028b")]
        [InlineData("a\u2029b")]
        [InlineData("a\u0085b")]
        public static void UnicodeSeparators_AreDeliberatelyLeftAlone(string value)
        {
            // Measured against both shipped formatters: none of these ends a physical record, so
            // neutralising them would be scope creep that silently alters ordinary values.
            Assert.Same(value, value.ToSingleLogLine());
        }
    }
}
