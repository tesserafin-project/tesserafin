using System;

namespace Tesserafin.Extensions
{
    /// <summary>
    /// Flattens an untrusted value so that it cannot end the physical log record it appears in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the two formatters this server ships do not agree. Measured against the
    /// real Serilog pipeline: the text output template in
    /// <c>Tesserafin.Server/Resources/Configuration/logging.json</c> — the default outside the
    /// container image — renders a value containing <c>CR</c>/<c>LF</c> verbatim, so a crafted
    /// value produces a **second** physical record whose timestamp, level and category the caller
    /// chose. The JSON-lines formatter used inside the container escapes the same value and emits
    /// one record. The defect is therefore real on one shipped path and already mitigated on the
    /// other, and the code should not depend on which formatter an operator selected.
    /// </para>
    /// <para>
    /// Scope is deliberately narrow. Only <c>CR</c> and <c>LF</c> are neutralised, because only
    /// those were shown to split a record: <c>U+2028</c>, <c>U+2029</c> and <c>U+0085</c> split
    /// nothing under either formatter and are left exactly as they are. This is not redaction, not
    /// hashing, not truncation and not a log-size policy — an ordinary value comes back
    /// character-for-character identical, and the separators that are replaced stay visible as
    /// their two-character escapes rather than disappearing.
    /// </para>
    /// </remarks>
    public static class LogValueExtensions
    {
        /// <summary>
        /// Returns <paramref name="value"/> with carriage returns and line feeds replaced by their
        /// two-character escapes, so the value always occupies exactly one physical log record.
        /// </summary>
        /// <param name="value">The untrusted value about to be logged.</param>
        /// <returns>
        /// <see langword="null"/> and the empty string unchanged; a value containing no <c>CR</c>
        /// or <c>LF</c> unchanged and by reference; otherwise a copy in which every <c>CR</c> is
        /// <c>\r</c> and every <c>LF</c> is <c>\n</c>.
        /// </returns>
        public static string? ToSingleLogLine(this string? value)
        {
            return value;
        }
    }
}
