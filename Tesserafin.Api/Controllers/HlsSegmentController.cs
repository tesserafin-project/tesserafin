using System;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Attributes;
using Tesserafin.Api.Auth.PlaybackCapabilityPolicy;
using Tesserafin.Api.Helpers;
using Tesserafin.Common.Api;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Model.Net;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// The hls segment controller.
/// </summary>
[Route("")]
[ApiExplorerSettings(IgnoreApi = true)]
public class HlsSegmentController : BaseTesserafinApiController
{
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly ITranscodeManager _transcodeManager;
    private readonly IHlsSegmentBindingRegistry _segmentBindings;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsSegmentController"/> class.
    /// </summary>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="transcodeManager">Instance of the <see cref="ITranscodeManager"/> interface.</param>
    /// <param name="segmentBindings">Instance of the <see cref="IHlsSegmentBindingRegistry"/> interface.</param>
    public HlsSegmentController(
        IServerConfigurationManager serverConfigurationManager,
        ITranscodeManager transcodeManager,
        IHlsSegmentBindingRegistry segmentBindings)
    {
        _serverConfigurationManager = serverConfigurationManager;
        _transcodeManager = transcodeManager;
        _segmentBindings = segmentBindings;
    }

    /// <summary>
    /// Gets the specified audio segment for an audio item.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="segmentId">The segment id.</param>
    /// <response code="200">Hls audio segment returned.</response>
    /// <returns>A <see cref="FileStreamResult"/> containing the audio stream.</returns>
    // The upstream comment this replaced said authentication could not be required "just yet due to
    // seeing some requests come from Chrome without full query string". Its sibling route
    // Audio/{itemId}/hls1/{playlistId}/{segmentId}.{container} has carried Policies.MediaDelivery
    // throughout, from the same players, so the exemption outlived whatever it was for. Measured
    // against master, this route served a segment to a caller presenting no credential at all.
    [HttpGet("Audio/{itemId}/hls/{segmentId}/stream.mp3", Name = "GetHlsAudioSegmentLegacyMp3")]
    [HttpGet("Audio/{itemId}/hls/{segmentId}/stream.aac", Name = "GetHlsAudioSegmentLegacyAac")]
    [Authorize(Policy = Policies.MediaDelivery)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesAudioFile]
    [SuppressMessage("Microsoft.Performance", "CA1801:ReviewUnusedParameters", MessageId = "itemId", Justification = "Required for ServiceStack")]
    [RequiresPlaybackCapability(PlaybackCapabilityScope.Media, "itemId", null)]
    public ActionResult GetHlsAudioSegmentLegacy([FromRoute, Required] string itemId, [FromRoute, Required] string segmentId)
    {
        // TODO: Deprecate with new iOS app
        var file = string.Concat(segmentId, Path.GetExtension(Request.Path.Value.AsSpan()));
        var transcodePath = _serverConfigurationManager.GetTranscodePath();
        file = Path.GetFullPath(Path.Combine(transcodePath, file));
        var fileDir = Path.GetDirectoryName(file);
        if (string.IsNullOrEmpty(fileDir) || !fileDir.StartsWith(transcodePath, StringComparison.InvariantCulture))
        {
            return BadRequest("Invalid segment.");
        }

        return FileStreamResponseHelpers.GetStaticFileResult(file, MimeTypes.GetMimeType(file));
    }

    /// <summary>
    /// Gets a hls video playlist.
    /// </summary>
    /// <param name="itemId">The video id.</param>
    /// <param name="playlistId">The playlist id.</param>
    /// <response code="200">Hls video playlist returned.</response>
    /// <returns>A <see cref="FileStreamResult"/> containing the playlist.</returns>
    [HttpGet("Videos/{itemId}/hls/{playlistId}/stream.m3u8")]
    [Authorize(Policy = Policies.MediaDelivery)]
    [RequiresPlaybackCapability(PlaybackCapabilityScope.Media, "itemId", "mediaSourceId")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesPlaylistFile]
    [SuppressMessage("Microsoft.Performance", "CA1801:ReviewUnusedParameters", MessageId = "itemId", Justification = "Required for ServiceStack")]
    public ActionResult GetHlsPlaylistLegacy([FromRoute, Required] string itemId, [FromRoute, Required] string playlistId)
    {
        var file = string.Concat(playlistId, Path.GetExtension(Request.Path.Value.AsSpan()));
        var transcodePath = _serverConfigurationManager.GetTranscodePath();
        file = Path.GetFullPath(Path.Combine(transcodePath, file));
        var fileDir = Path.GetDirectoryName(file);
        if (string.IsNullOrEmpty(fileDir) || !fileDir.StartsWith(transcodePath, StringComparison.InvariantCulture)
            || Path.GetExtension(file.AsSpan()).Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Invalid segment.");
        }

        return GetFileResult(file, file);
    }

    /// <summary>
    /// Stops an active encoding.
    /// </summary>
    /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <response code="204">Encoding stopped successfully.</response>
    /// <returns>A <see cref="NoContentResult"/> indicating success.</returns>
    [HttpDelete("Videos/ActiveEncodings")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult StopEncodingProcess(
        [FromQuery, Required] string deviceId,
        [FromQuery, Required] string playSessionId)
    {
        _transcodeManager.KillTranscodingJobs(deviceId, playSessionId, _ => true);
        return NoContent();
    }

    /// <summary>
    /// Gets a hls video segment.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="playlistId">The playlist id.</param>
    /// <param name="segmentId">The segment id.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <response code="200">Hls video segment returned.</response>
    /// <response code="404">Hls segment not found.</response>
    /// <returns>A <see cref="FileStreamResult"/> containing the video segment.</returns>
    // Same disposition as GetHlsAudioSegmentLegacy above: the exemption outlived its reason, and
    // the route served video segments anonymously until this change.
    [HttpGet("Videos/{itemId}/hls/{playlistId}/{segmentId}.{segmentContainer}")]
    [Authorize(Policy = Policies.MediaDelivery)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesVideoFile]
    [SuppressMessage("Microsoft.Performance", "CA1801:ReviewUnusedParameters", MessageId = "itemId", Justification = "Required for ServiceStack")]
    // The media source is read from the request, exactly as `GetHlsPlaylistLegacy` above already
    // does (#153-LTV-S1). It was `null`, and a null demand agrees only with a capability that is
    // itself bound to no media source — so a client that mints one per media source, which is what
    // the shipped web client does for a transcode, could never reach a single Live TV segment. The
    // item binding still holds either way: this widens which capability satisfies the route, not
    // which item it reaches.
    // #153-LTV-R1: and the play session, which was null. ValidateCapability guards its
    // play-session comparison with `if (demand.PlaySessionId is not null && …)`, so a null demand
    // skipped the check entirely and LTV-R0 reached a segment with a capability minted under a
    // play session the server had never issued — 200, 387 468 bytes. The propagator now writes the
    // play session into every fragment uri, so the client can satisfy the demand without the
    // capability's binding ever being read off the wire.
    [RequiresPlaybackCapability(PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId")]
    public ActionResult GetHlsVideoSegmentLegacy(
        [FromRoute, Required] string itemId,
        [FromRoute, Required] string playlistId,
        [FromRoute, Required] string segmentId,
        [FromRoute, Required] string segmentContainer)
    {
        // #153-LTV-R1, LTV-R0 finding 2. Nothing below derives the file from the caller's
        // parameters. The job is resolved from server state FIRST, the path is canonicalised
        // against the root that job actually writes into, and route, validated capability and job
        // are compared before anything is opened.
        var binding = _segmentBindings.ResolveByPlaylistId(playlistId);
        if (binding is null)
        {
            // Closed refusal, with no fallback to the historical resolution. A live job's segment
            // files outlive it (#153-LTV-S0 measured 22 of them surviving a teardown); once the
            // job is gone they are unreachable rather than served to whoever names them.
            return NotFound("Hls segment not found.");
        }

        if (!Guid.TryParse(itemId, out var routeItemId) || !routeItemId.Equals(binding.ItemId))
        {
            // itemId is consumed here, and this is the only place it ever was not.
            return Unauthorized();
        }

        var provenance = PlaybackCapabilityProvenance.Resolve(HttpContext);
        if (provenance.Outcome == PlaybackCapabilityProvenanceOutcome.Refuse)
        {
            return Unauthorized();
        }

        var capability = provenance.Capability;
        if (capability is not null && !AgreesWithTheJob(capability, binding))
        {
            return Unauthorized();
        }

        if (!RequestedMediaSourceAgrees(binding))
        {
            return Unauthorized();
        }

        // ffmpeg was told to write this job's segments as "{playlistId}%d.{ext}", so a segment name
        // that does not begin with the job's own playlist identifier is not this job's segment.
        // This is what stops a capability for job A reaching job B's bytes: segmentId alone never
        // names a file any more.
        if (!segmentId.StartsWith(binding.PlaylistId, StringComparison.Ordinal))
        {
            return NotFound("Hls segment not found.");
        }

        var file = Path.GetFullPath(Path.Combine(
            binding.CanonicalRoot,
            string.Concat(segmentId, Path.GetExtension(Request.Path.Value.AsSpan()))));

        if (!string.Equals(Path.GetDirectoryName(file), binding.CanonicalRoot, StringComparison.Ordinal))
        {
            return BadRequest("Invalid segment.");
        }

        return GetFileResult(file, binding.CanonicalPlaylistPath);
    }

    /// <summary>
    /// Every binding the capability carries has to be the job's own. A capability bound to no media
    /// source cannot stand in for one bound to the job's, and a capability minted under another
    /// play session cannot reach this job's segments (#153-LTV-R1, LTV-R0 findings 2 and 5).
    /// </summary>
    private static bool AgreesWithTheJob(ValidatedPlaybackCapability capability, HlsSegmentBinding binding)
    {
        if (capability.ItemId is { } boundItem && !boundItem.Equals(binding.ItemId))
        {
            return false;
        }

        if (!string.Equals(capability.MediaSourceId, binding.MediaSourceId, StringComparison.Ordinal))
        {
            return false;
        }

        // The capability's own play session, taken from the validation result rather than from the
        // url. LTV-R0 minted a capability under a play session the server had never issued and
        // reached a segment with it: 200, 387 468 bytes. This is the comparison that was missing.
        return string.IsNullOrEmpty(capability.PlaySessionId)
            ? string.IsNullOrEmpty(binding.PlaySessionId)
            : string.Equals(capability.PlaySessionId, binding.PlaySessionId, StringComparison.Ordinal);
    }

    /// <summary>
    /// A caller-named media source must be the job's. Naming none is allowed only for a job that
    /// has none either, so the route cannot be downgraded by omitting the parameter.
    /// </summary>
    private bool RequestedMediaSourceAgrees(HlsSegmentBinding binding)
    {
        var requested = Request.Query["mediaSourceId"].ToString();
        if (string.IsNullOrEmpty(requested))
        {
            // Naming none is NOT a downgrade path, and requiring it would break a client that never
            // had one to name. A capability's own media-source binding is compared with the job's
            // unconditionally in AgreesWithTheJob - including null against non-null, which is the
            // item-only downgrade the mission forbids - and a request carrying no capability at all
            // is still pinned by the route's item, by the job's own segment prefix and by the
            // canonical root, so it cannot reach another job's bytes either way.
            //
            // Measured: demanding the parameter refused every durable-token client whose playlist
            // was never propagated. The #153-LTV-S0 rig scenario fetches its playlist with a durable
            // token, so nothing propagates a capability into it, so its segment uris are bare - and
            // every one of them answered 401 (playlist 200, four uris, three fetched, all 401).
            return true;
        }

        return string.Equals(requested, binding.MediaSourceId, StringComparison.Ordinal);
    }

    private ActionResult GetFileResult(string path, string playlistPath)
    {
        var transcodingJob = _transcodeManager.OnTranscodeBeginRequest(playlistPath, TranscodingJobType.Hls);

        Response.OnCompleted(() =>
        {
            if (transcodingJob is not null)
            {
                _transcodeManager.OnTranscodeEndRequest(transcodingJob);
            }

            return Task.CompletedTask;
        });

        return FileStreamResponseHelpers.GetStaticFileResult(path, MimeTypes.GetMimeType(path));
    }
}
