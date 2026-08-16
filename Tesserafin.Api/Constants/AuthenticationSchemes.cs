namespace Tesserafin.Api.Constants;

/// <summary>
/// Authentication schemes for user authentication in the API.
/// </summary>
public static class AuthenticationSchemes
{
    /// <summary>
    /// Scheme name for the custom legacy authentication.
    /// </summary>
    public const string CustomAuthentication = "CustomAuthentication";

    /// <summary>
    /// Scheme name for a short-lived, media-scoped playback capability (#153).
    /// </summary>
    /// <remarks>
    /// Deliberately a SEPARATE scheme rather than another key
    /// <see cref="Tesserafin.Controller.Net.IAuthorizationContext"/> learns to read. A scheme is
    /// only ever selected by an endpoint that names it, so a capability presented to an endpoint
    /// that does not name it is not a weak credential — it is not a credential at all, and no
    /// per-endpoint denylist has to be maintained to keep it that way.
    /// </remarks>
    public const string PlaybackCapability = "PlaybackCapability";
}
