using System;
using System.Globalization;
using System.Linq;

namespace Tesserafin.Server.Tests.LogForging
{
    /// <summary>
    /// The bytes a raw request got back, parsed only as far as these tests need.
    /// </summary>
    /// <param name="StatusCode">The status code, or <see langword="null"/> if the parser never produced a response line.</param>
    /// <param name="Location">The <c>Location</c> header, if any.</param>
    /// <param name="Raw">Everything the server wrote.</param>
    internal sealed record RawResponse(int? StatusCode, string? Location, string Raw)
    {
        public static RawResponse Parse(string raw)
        {
            var lines = raw.Split("\r\n");
            int? status = null;
            if (lines.Length > 0)
            {
                var parts = lines[0].Split(' ');
                if (parts.Length > 1 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var parsed))
                {
                    status = parsed;
                }
            }

            var location = lines
                .Skip(1)
                .TakeWhile(line => line.Length > 0)
                .FirstOrDefault(line => line.StartsWith("Location:", StringComparison.OrdinalIgnoreCase))
                ?["Location:".Length..]
                .Trim();

            return new RawResponse(status, location, raw);
        }
    }
}
