namespace Tesserafin.Controller.Net.PlaybackCredentials;

/// <summary>
/// The randomness boundary for credential secrets.
/// </summary>
/// <remarks>
/// Injected rather than called statically so entropy behaviour is testable by substitution instead
/// of by sampling. A probabilistic test that mints ten thousand values and asserts they differ
/// proves almost nothing and fails occasionally for free; a test that pins the source and asserts
/// the exact byte count requested proves the property that actually matters.
/// </remarks>
public interface IRandomSecretSource
{
    /// <summary>
    /// Fills a buffer with cryptographically secure random bytes.
    /// </summary>
    /// <param name="destination">The buffer to fill completely.</param>
    void Fill(System.Span<byte> destination);
}
