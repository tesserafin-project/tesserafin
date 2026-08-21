using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Tesserafin.Api.Auth.PlaybackCapabilityPolicy;
using Tesserafin.Api.Extensions;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Session;
using Tesserafin.Extensions;

namespace Tesserafin.Api.Auth.HlsJobOwnership;

/// <summary>
/// Compares the caller of an HLS resource with the transcoding job that produced it
/// (#153-LTV-R3).
/// </summary>
/// <remarks>
/// WHAT #153-LTV-R2 MEASURED, AND WHY A POLICY COULD NOT FIX IT. <c>Policies.MediaDelivery</c>
/// succeeds for <c>GetIsApiKey() || !GetUserId().IsEmpty()</c>; <c>DefaultAuthorizationHandler</c>
/// applies exactly two gates to a durable principal — remote access when off the local network,
/// and the parental schedule — and succeeds an administrator outright. None of those is job
/// ownership, and an administrator passes every permission policy in this server because
/// <c>UserPermissionRequirement</c> subclasses <c>DefaultAuthorizationRequirement</c>. So a second
/// authenticated user read the first user's live segment bytes. The comparison has to happen here,
/// against the job, and it admits no exemption for a role, for remote access or for the local
/// network.
///
/// TWO CREDENTIALS, NEVER ONE FALLING BACK ON THE OTHER. If a capability was presented, the
/// capability decides — and if it does not match the job the answer is a refusal, not a second
/// attempt as a durable token. Falling back would make an invalid capability strictly better than
/// no capability at all.
/// </remarks>
public sealed class HlsJobOwnershipAuthorizer : IHlsJobOwnershipAuthorizer
{
    private readonly IHlsSegmentBindingRegistry _bindings;
    private readonly ISessionManager _sessionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsJobOwnershipAuthorizer"/> class.
    /// </summary>
    /// <param name="bindings">The job binding registry.</param>
    /// <param name="sessionManager">The session manager, used to resolve a capability's session to its device.</param>
    public HlsJobOwnershipAuthorizer(IHlsSegmentBindingRegistry bindings, ISessionManager sessionManager)
    {
        _bindings = bindings;
        _sessionManager = sessionManager;
    }

    /// <inheritdoc />
    public HlsJobOwnershipDecision AuthorizeByPlaylistId(HttpContext context, string playlistId)
        => Decide(context, _bindings.ResolveByPlaylistId(playlistId));

    /// <inheritdoc />
    public HlsJobOwnershipDecision AuthorizeBySegmentName(HttpContext context, string segmentName)
        => Decide(context, _bindings.ResolveBySegmentName(segmentName));

    /// <inheritdoc />
    public HlsJobOwnershipDecision AuthorizeByOutputPath(HttpContext context, string outputPath)
        => Decide(context, _bindings.ResolveByOutputPath(outputPath));

    /// <inheritdoc />
    public bool OwnsJob(HttpContext context, Guid ownerUserId, string? ownerDeviceId)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (ownerUserId.IsEmpty() || string.IsNullOrEmpty(ownerDeviceId))
        {
            return false;
        }

        var provenance = PlaybackCapabilityProvenance.Resolve(context);
        return provenance.Outcome != PlaybackCapabilityProvenanceOutcome.Refuse
               && CallerIsTheOwner(context, provenance.Capability, ownerUserId, ownerDeviceId);
    }

    private HlsJobOwnershipDecision Decide(HttpContext context, HlsSegmentBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(context);

        // STEP 1. The job's metadata, and nothing else. No file has been named yet, let alone
        // opened: this method returns a decision, never a path.
        if (binding is null)
        {
            // No fallback to the historical resolution. A live job's segment files outlive it
            // (#153-LTV-S0 measured 22 surviving a teardown); once the job is gone they are
            // unreachable rather than served to whoever names them.
            return HlsJobOwnershipDecision.NoSuchJob();
        }

        // STEP 2. A job with no resolvable owner belongs to nobody, and nobody can be it. Both
        // halves are reachable: an api-key principal resolves to Guid.Empty, and a credential that
        // names no device leaves the job device-less. An absent owner is not a wildcard.
        if (binding.UserId.IsEmpty() || string.IsNullOrEmpty(binding.DeviceId))
        {
            return HlsJobOwnershipDecision.Refused();
        }

        // STEP 3. Which credential is deciding.
        var provenance = PlaybackCapabilityProvenance.Resolve(context);
        if (provenance.Outcome == PlaybackCapabilityProvenanceOutcome.Refuse)
        {
            return HlsJobOwnershipDecision.Refused();
        }

        var capability = provenance.Capability;
        if (capability is not null && !CapabilityMatchesTheJob(capability, binding))
        {
            // A presented capability is the whole decision. There is deliberately no fallback to
            // the durable path below: falling back would make an invalid capability strictly
            // better than presenting none at all.
            return HlsJobOwnershipDecision.Refused();
        }

        // STEP 4. Who the caller is, resolved exactly as the job's own owner was recorded.
        return CallerIsTheOwner(context, capability, binding.UserId, binding.DeviceId)
            ? HlsJobOwnershipDecision.Authorized(binding)
            : HlsJobOwnershipDecision.Refused();
    }

    /// <summary>
    /// The identity comparison, shared by both credentials. It comes from the validated principal
    /// and from the validation result; a url parameter may corroborate it elsewhere, never create
    /// it here.
    /// </summary>
    private bool CallerIsTheOwner(HttpContext context, ValidatedPlaybackCapability? capability, Guid ownerUserId, string? ownerDeviceId)
    {
        var user = context.User;
        if (user?.Identity is null || !user.Identity.IsAuthenticated)
        {
            return false;
        }

        // An api key resolves to no user, so it cannot be anybody's owner. Checked explicitly
        // rather than left to the empty-guid test below, so that removing either one is a visible
        // change rather than a silently redundant one.
        if (user.GetIsApiKey())
        {
            return false;
        }

        // On the capability path the user comes from the validation result rather than from a
        // claim the request carries.
        var callerUserId = capability?.Validation.UserId ?? user.GetUserId();
        if (callerUserId.IsEmpty() || !callerUserId.Equals(ownerUserId))
        {
            return false;
        }

        // The device, resolved by the SAME function that recorded the job's. A durable token has a
        // device claim; a capability has none and is resolved through its session. Deriving it
        // differently on the two sides would make a job unreachable by the client that started it.
        var callerDeviceId = HlsJobOwnerDevice.Resolve(
            user.GetDeviceId(),
            capability?.Validation.SessionId,
            _sessionManager);

        return !string.IsNullOrEmpty(callerDeviceId)
               && string.Equals(callerDeviceId, ownerDeviceId, StringComparison.Ordinal);
    }

    /// <summary>
    /// The capability's own bindings, all of which have to be the job's. The user, the device and
    /// the session are NOT compared here: those are identity, and identity is compared once, in
    /// <see cref="CallerIsTheOwner"/>, for both credentials.
    /// </summary>
    private static bool CapabilityMatchesTheJob(ValidatedPlaybackCapability capability, HlsSegmentBinding binding)
    {
        // Defence in depth: the feature is written on the accepted branch only.
        if (!capability.Validation.IsValid)
        {
            return false;
        }

        // Scope. A capability minted for subtitles or fonts is not a media capability.
        if (capability.Scope != PlaybackCapabilityScope.Media)
        {
            return false;
        }

        // User, from the validation result rather than from any claim the request carries.
        if (!capability.Validation.UserId.Equals(binding.UserId))
        {
            return false;
        }

        // Item. A capability bound to no item cannot stand in for one bound to the job's.
        if (capability.ItemId is not { } boundItem || !boundItem.Equals(binding.ItemId))
        {
            return false;
        }

        // Media source, compared unconditionally — including null against non-null, which is the
        // item-only downgrade the mission forbids.
        if (!string.Equals(capability.MediaSourceId, binding.MediaSourceId, StringComparison.Ordinal))
        {
            return false;
        }

        // Play session, from the validation result rather than from the url. LTV-R0 minted a
        // capability under a play session the server had never issued and reached a segment with
        // it: 200, 387 468 bytes.
        var playSessionsAgree = string.IsNullOrEmpty(capability.PlaySessionId)
            ? string.IsNullOrEmpty(binding.PlaySessionId)
            : string.Equals(capability.PlaySessionId, binding.PlaySessionId, StringComparison.Ordinal);

        if (!playSessionsAgree)
        {
            return false;
        }

        return true;
    }
}
