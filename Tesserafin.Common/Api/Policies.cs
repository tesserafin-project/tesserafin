namespace Tesserafin.Common.Api;

/// <summary>
/// Policies for the API authorization.
/// </summary>
public static class Policies
{
    /// <summary>
    /// Policy name for requiring first time setup or elevated privileges.
    /// </summary>
    public const string FirstTimeSetupOrElevated = "FirstTimeSetupOrElevated";

    /// <summary>
    /// Policy name for requiring elevated privileges.
    /// </summary>
    public const string RequiresElevation = "RequiresElevation";

    /// <summary>
    /// Policy name for allowing local access only.
    /// </summary>
    public const string LocalAccessOnly = "LocalAccessOnly";

    /// <summary>
    /// Policy name for escaping schedule controls.
    /// </summary>
    public const string IgnoreParentalControl = "IgnoreParentalControl";

    /// <summary>
    /// Policy name for requiring download permission.
    /// </summary>
    public const string Download = "Download";

    /// <summary>
    /// Policy name for requiring first time setup or default permissions.
    /// </summary>
    public const string FirstTimeSetupOrDefault = "FirstTimeSetupOrDefault";

    /// <summary>
    /// Policy name for requiring local access or elevated privileges.
    /// </summary>
    public const string LocalAccessOrRequiresElevation = "LocalAccessOrRequiresElevation";

    /// <summary>
    /// Policy name for requiring (anonymous) LAN access.
    /// </summary>
    public const string AnonymousLanAccessPolicy = "AnonymousLanAccessPolicy";

    /// <summary>
    /// Policy name for escaping schedule controls or requiring first time setup.
    /// </summary>
    public const string FirstTimeSetupOrIgnoreParentalControl = "FirstTimeSetupOrIgnoreParentalControl";

    /// <summary>
    /// Policy name for accessing SyncPlay.
    /// </summary>
    public const string SyncPlayHasAccess = "SyncPlayHasAccess";

    /// <summary>
    /// Policy name for creating a SyncPlay group.
    /// </summary>
    public const string SyncPlayCreateGroup = "SyncPlayCreateGroup";

    /// <summary>
    /// Policy name for joining a SyncPlay group.
    /// </summary>
    public const string SyncPlayJoinGroup = "SyncPlayJoinGroup";

    /// <summary>
    /// Policy name for accessing a SyncPlay group.
    /// </summary>
    public const string SyncPlayIsInGroup = "SyncPlayIsInGroup";

    /// <summary>
    /// Policy name for accessing collection management.
    /// </summary>
    public const string CollectionManagement = "CollectionManagement";

    /// <summary>
    /// Policy name for accessing LiveTV.
    /// </summary>
    public const string LiveTvAccess = "LiveTvAccess";

    /// <summary>
    /// Policy name for managing LiveTV.
    /// </summary>
    public const string LiveTvManagement = "LiveTvManagement";

    /// <summary>
    /// Policy name for accessing subtitles management.
    /// </summary>
    public const string SubtitleManagement = "SubtitleManagement";

    /// <summary>
    /// Policy name for accessing lyric management.
    /// </summary>
    public const string LyricManagement = "LyricManagement";

    /// <summary>
    /// Policy name for accessing content pack management.
    /// </summary>
    public const string ContentPackManagement = "ContentPackManagement";

    /// <summary>
    /// Policy name for delivering media bytes (#153).
    /// </summary>
    /// <remarks>
    /// The ONLY policy that accepts a playback capability, and it is carried by media routes and
    /// nothing else. A general API endpoint keeps the default policy, which names one
    /// authentication scheme and will never select the capability scheme, so a capability cannot
    /// reach it however valid it is.
    /// </remarks>
    public const string MediaDelivery = "MediaDelivery";
}
