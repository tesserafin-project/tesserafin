namespace Tesserafin.Api.CodeQlCanaryLookAlike;

/// <summary>
/// TEMPORARY look-alike for the cs/log-forging barrier model canary (#203). Not merged.
/// </summary>
public static class LookAlikeLogValueExtensions
{
    /// <summary>
    /// Same method name as the modelled helper, different namespace and type, and it neutralises
    /// nothing. The model must not cover it.
    /// </summary>
    /// <param name="value">The value returned unchanged.</param>
    /// <returns>The value, unchanged.</returns>
    public static string? ToSingleLogLine(this string? value)
    {
        return value;
    }
}
