namespace Tesserafin.Providers.Tests.ProviderAuth
{
    /// <summary>One finding from the provider-authentication audit.</summary>
    /// <param name="Rule">The rule that fired, as a stable kebab-case identifier.</param>
    /// <param name="Provider">The provider the finding is attributed to, or a namespace.</param>
    /// <param name="Detail">
    /// What is wrong and what to do about it. Deliberately describes the offending value's
    /// <em>position and length</em> and never quotes it: a gate that prints the credential it found
    /// leaks it into CI logs.
    /// </param>
    public readonly record struct ProviderAuthViolation(string Rule, string Provider, string Detail)
    {
        /// <inheritdoc />
        public override string ToString() => $"[{Rule}] {Provider}: {Detail}";
    }
}
