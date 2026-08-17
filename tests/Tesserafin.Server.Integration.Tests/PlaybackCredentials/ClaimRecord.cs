namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// One claim, reduced to the pair the #153-A0-R3 parity comparison is about.
/// </summary>
/// <param name="Type">The claim type.</param>
/// <param name="Value">The claim value.</param>
public sealed record ClaimRecord(string Type, string Value);
