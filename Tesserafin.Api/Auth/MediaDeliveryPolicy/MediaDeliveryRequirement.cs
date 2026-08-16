using Tesserafin.Api.Auth.DefaultAuthorizationPolicy;

namespace Tesserafin.Api.Auth.MediaDeliveryPolicy;

/// <summary>
/// Requires either an ordinary authenticated user or a live playback capability (#153).
/// </summary>
/// <remarks>
/// Subclasses <see cref="DefaultAuthorizationRequirement"/> deliberately, following the same
/// pattern as <c>UserPermissionRequirement</c>. That means <c>DefaultAuthorizationHandler</c> still
/// runs for it — so the remote-access permission and the parental schedule are still enforced for a
/// durable-token principal, and it can still call <c>Fail</c>, which is decisive. What the base
/// handler will NOT do for a subclassed requirement is succeed it; that is left to
/// <see cref="MediaDeliveryHandler"/>, which is how a capability principal (with no user
/// permissions to check) gets through without weakening the durable-token path.
/// </remarks>
public class MediaDeliveryRequirement : DefaultAuthorizationRequirement
{
}
