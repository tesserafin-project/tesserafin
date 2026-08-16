using System;
using System.Security.Cryptography;
using Tesserafin.Controller.Net.PlaybackCredentials;

namespace Tesserafin.Server.Implementations.Security.PlaybackCredentials;

/// <summary>
/// The production randomness boundary: the platform CSPRNG, and nothing else.
/// </summary>
public sealed class CryptoRandomSecretSource : IRandomSecretSource
{
    /// <inheritdoc />
    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}
