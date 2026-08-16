namespace Tesserafin.Api.Constants;

/// <summary>
/// Internal claim types for authorization.
/// </summary>
public static class InternalClaimTypes
{
    /// <summary>
    /// User Id.
    /// </summary>
    public const string UserId = "Tesserafin-UserId";

    /// <summary>
    /// Device Id.
    /// </summary>
    public const string DeviceId = "Tesserafin-DeviceId";

    /// <summary>
    /// Device.
    /// </summary>
    public const string Device = "Tesserafin-Device";

    /// <summary>
    /// Client.
    /// </summary>
    public const string Client = "Tesserafin-Client";

    /// <summary>
    /// Version.
    /// </summary>
    public const string Version = "Tesserafin-Version";

    /// <summary>
    /// Token.
    /// </summary>
    public const string Token = "Tesserafin-Token";

    /// <summary>
    /// Is Api Key.
    /// </summary>
    public const string IsApiKey = "Tesserafin-IsApiKey";

    /// <summary>
    /// The public identifier of the playback capability that authenticated this request. Present
    /// only on a principal produced by the playback-capability scheme, which is what tells the
    /// media authorization handler and the scope filter which kind of principal they are looking at.
    /// Never the secret.
    /// </summary>
    public const string PlaybackCapabilityId = "Tesserafin-PlaybackCapabilityId";

    /// <summary>
    /// One claim per scope the authenticating capability carries.
    /// </summary>
    public const string PlaybackCapabilityScope = "Tesserafin-PlaybackCapabilityScope";

    /// <summary>
    /// The item the authenticating capability is bound to, absent for a scope that is not
    /// item-bound.
    /// </summary>
    public const string PlaybackCapabilityItemId = "Tesserafin-PlaybackCapabilityItemId";

    /// <summary>
    /// The media source the authenticating capability is bound to, when it names one.
    /// </summary>
    public const string PlaybackCapabilityMediaSourceId = "Tesserafin-PlaybackCapabilityMediaSourceId";

    /// <summary>
    /// The play session the authenticating capability is bound to.
    /// </summary>
    public const string PlaybackCapabilityPlaySessionId = "Tesserafin-PlaybackCapabilityPlaySessionId";
}
