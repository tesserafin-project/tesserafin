using System;
using System.Globalization;
using System.Net;

namespace Tesserafin.Server.Diagnostics.RemoteAccess;

/// <summary>
/// Decides whether a caller-supplied string is a hostname this layer will resolve, and what its
/// canonical form is.
/// </summary>
/// <remarks>
/// <para>
/// This is a security boundary, not a convenience. The only thing downstream does with the result
/// is hand it to the system resolver, so the validator's job is to make sure a caller cannot
/// smuggle anything else through: no scheme, no path, no query, no fragment, no credentials, no
/// port, no wildcard, no IP literal, and no whitespace games. A URL is rejected outright rather
/// than having its host extracted, because extracting it would mean the API silently accepted a
/// shape it does not document.
/// </para>
/// <para>
/// <c>localhost</c> and <c>.local</c> are rejected too. Neither can be a public name, so accepting
/// them could only produce a diagnostic about the host talking to itself — which is exactly the
/// kind of self-referential "evidence" this whole slice exists to refuse.
/// </para>
/// </remarks>
public static class HostnameInput
{
    /// <summary>The longest hostname this layer will consider, per DNS limits.</summary>
    public const int MaximumLength = 253;

    /// <summary>
    /// Validates and normalizes a proposed hostname.
    /// </summary>
    /// <param name="candidate">The caller-supplied string.</param>
    /// <param name="normalized">The IDNA-normalized ASCII hostname, when accepted.</param>
    /// <returns><c>true</c> if the value is a hostname this layer will resolve.</returns>
    public static bool TryNormalize(string? candidate, out string? normalized)
    {
        normalized = null;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        // No leading or trailing whitespace, and no interior whitespace of any kind. Compared
        // against the original so that a value needing a trim is rejected rather than repaired:
        // silently repairing input means the caller and the server disagree about what was asked.
        if (!string.Equals(candidate, candidate.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in candidate)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return false;
            }
        }

        // Anything that could make this a URL, a credential carrier or a port specification.
        if (candidate.Contains("://", StringComparison.Ordinal)
            || candidate.Contains('/', StringComparison.Ordinal)
            || candidate.Contains('\\', StringComparison.Ordinal)
            || candidate.Contains('?', StringComparison.Ordinal)
            || candidate.Contains('#', StringComparison.Ordinal)
            || candidate.Contains('@', StringComparison.Ordinal)
            || candidate.Contains(':', StringComparison.Ordinal)
            || candidate.Contains('*', StringComparison.Ordinal)
            || candidate.Contains(',', StringComparison.Ordinal)
            || candidate.Contains('%', StringComparison.Ordinal)
            || candidate.Contains('[', StringComparison.Ordinal)
            || candidate.Contains(']', StringComparison.Ordinal))
        {
            return false;
        }

        // An IP literal is not a name. Checked before IDNA so that a dotted-quad cannot be
        // laundered into something that looks like a label sequence.
        if (IPAddress.TryParse(candidate, out _))
        {
            return false;
        }

        string ascii;
        try
        {
            ascii = new IdnMapping { AllowUnassigned = false, UseStd3AsciiRules = true }.GetAscii(candidate);
        }
        catch (ArgumentException)
        {
            // Not representable as an IDNA A-label sequence.
            return false;
        }

        if (ascii.Length == 0 || ascii.Length > MaximumLength)
        {
            return false;
        }

        // A trailing root dot is legal in DNS but would make two spellings of one name compare
        // unequal downstream, so it is rejected rather than stripped.
        if (ascii.EndsWith('.') || ascii.StartsWith('.') || ascii.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var lowered = ascii.ToLowerInvariant();

        if (string.Equals(lowered, "localhost", StringComparison.Ordinal)
            || lowered.EndsWith(".localhost", StringComparison.Ordinal)
            || lowered.EndsWith(".local", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var label in lowered.Split('.'))
        {
            if (!IsValidLabel(label))
            {
                return false;
            }
        }

        // A single label cannot be a public name. Requiring a dot keeps the resolver from being
        // asked about search-domain-relative names, whose answer depends on the host's resolver
        // configuration rather than on public DNS.
        if (!lowered.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        normalized = lowered;
        return true;
    }

    private static bool IsValidLabel(string label)
    {
        if (label.Length == 0 || label.Length > 63)
        {
            return false;
        }

        if (label.StartsWith('-') || label.EndsWith('-'))
        {
            return false;
        }

        foreach (var character in label)
        {
            var isLetterOrDigit = (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9');
            if (!isLetterOrDigit && character != '-')
            {
                return false;
            }
        }

        return true;
    }
}
