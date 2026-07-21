namespace Tesserafin.Controller.Diagnostics;

/// <summary>
/// The "there is no HTTP request here" implementation of <see cref="IRequestCorrelationAccessor"/>:
/// always returns <c>null</c>. Used as the default for every call site constructed outside the
/// ASP.NET Core pipeline — existing test constructors above all — so that adding request
/// correlation stays strictly additive and never forces a signature change on a caller that has no
/// request to correlate to.
/// </summary>
public sealed class NullRequestCorrelationAccessor : IRequestCorrelationAccessor
{
    /// <summary>
    /// The shared instance. Stateless, therefore safe to share.
    /// </summary>
    public static readonly NullRequestCorrelationAccessor Instance = new();

    private NullRequestCorrelationAccessor()
    {
    }

    /// <inheritdoc />
    public string? CurrentRequestId => null;
}
