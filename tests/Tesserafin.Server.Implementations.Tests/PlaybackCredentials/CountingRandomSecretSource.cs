using System;
using Tesserafin.Controller.Net.PlaybackCredentials;

namespace Tesserafin.Server.Implementations.Tests.PlaybackCredentials;

/// <summary>
/// A deterministic stand-in for the CSPRNG that records exactly how many bytes were asked for.
/// </summary>
/// <remarks>
/// Entropy is asserted by SUBSTITUTION, not by sampling. A test that mints ten thousand values and
/// asserts they all differ proves almost nothing about the entropy of any one of them, costs
/// seconds, and fails occasionally for free. Recording the requested byte count proves the property
/// the contract actually states.
/// </remarks>
public sealed class CountingRandomSecretSource : IRandomSecretSource
{
    private byte _seed;

    /// <summary>Gets the largest single request seen.</summary>
    public int LargestRequestedByteCount { get; private set; }

    /// <summary>Gets how many times the source was asked for bytes.</summary>
    public int CallCount { get; private set; }

    /// <inheritdoc />
    public void Fill(Span<byte> destination)
    {
        CallCount++;
        LargestRequestedByteCount = Math.Max(LargestRequestedByteCount, destination.Length);

        // Distinct per call, so two credentials are never accidentally equal and a test that meant
        // to mint two things cannot silently be testing one.
        _seed++;
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = (byte)(_seed + i);
        }
    }
}
