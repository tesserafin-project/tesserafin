using System;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Attributes;
using Tesserafin.Api.Auth.HlsJobOwnership;
using Tesserafin.Api.Auth.PlaybackCapabilityPolicy;
using Tesserafin.Api.Helpers;
using Tesserafin.Common.Api;
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
    private readonly ITranscodeManager _transcodeManager;
    private readonly IHlsJobOwnershipAuthorizer _jobOwnership;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsSegmentController"/> class.
    /// </summary>
    /// <param name="transcodeManager">Instance of the <see cref="ITranscodeManager"/> interface.</param>
    /// <param name="jobOwnership">Instance of the <see cref="IHlsJobOwnershipAuthorizer"/> interface.</param>
    // #153-LTV-R3. IServerConfigurationManager is gone from this controller on purpose. It was
    // here only to reach GetTranscodePath(), the flat folder every job shares, and every path this
    // controller builds now comes from a job binding instead. Removing the dependency is what
    // makes that structural rather than a convention: there is no longer a way to name the folder.
    public HlsSegmentController(
        ITranscodeManager transcodeManager,
        IHlsJobOwnershipAuthorizer jobOwnership)
    {
        _transcodeManager = transcodeManager;
        _jobOwnership = jobOwnership;
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
        // #153-LTV-R3, finding R2-2. This route used to resolve
        // `Path.Combine(transcodeFolderPath, segmentId + extension)` from the caller-supplied
        // segment name alone, with only a containment check against a folder every job shares. It
        // read no job and no caller, so any authenticated principal reached any job's audio bytes.
        // Now the segment name only SELECTS a job, the caller is compared with that job, and the
        // path is built from the job's own canonical root afterwards.
        var decision = _jobOwnership.AuthorizeBySegmentName(HttpContext, segmentId);
        if (decision.Binding is not { } binding || !decision.IsAuthorized)
        {
            return decision.Outcome == HlsJobOwnershipOutcome.NoSuchJob
                ? NotFound("Hls segment not found.")
                : Unauthorized();
        }

        if (!Guid.TryParse(itemId, out var routeItemId) || !routeItemId.Equals(binding.ItemId))
        {
            return Unauthorized();
        }

        var file = Path.GetFullPath(Path.Combine(
            binding.CanonicalRoot,
            string.Concat(segmentId, Path.GetExtension(Request.Path.Value.AsSpan()))));

        if (!string.Equals(Path.GetDirectoryName(file), binding.CanonicalRoot, StringComparison.Ordinal))
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
        // #153-LTV-R3. This route is UNREACHABLE and stays that way. Upstream jellyfin b176beb88e
        // ("Reduce string allocations") rewrote the guard below and dropped its negation: the
        // condition it replaced was `!Path.GetExtension(file).Equals(".m3u8", …)`. The route
        // literal is `stream.m3u8`, so `Request.Path` always ends in `.m3u8`, so `file` always
        // does, so the guard always fires. Every caller gets a 400.
        //
        // That is what makes #153-LTV-R2's finding R2-3 — "the playlist is resolved from
        // playlistId alone" — correct as source reading and NOT exploitable: nobody obtains the
        // playlist text, so no capability digest can leak through it. It is neither a bearer
        // credential leak nor a text leak, and `TheLegacyPlaylistRoute_RefusesEveryCaller` pins
        // that. Un-inverting the guard would OPEN a route that is currently closed, which is the
        // opposite of this branch's direction, so it is deliberately not repaired here.
        //
        // What IS repaired: the action no longer names the shared transcode folder at all. It used
        // to compose `Path.Combine(transcodeFolderPath, playlistId + extension)` before rejecting
        // the request, so the resolution existed even though nothing reached it. Now the only path
        // it could ever build comes from a job binding, and it never gets that far.
        var decision = _jobOwnership.AuthorizeByPlaylistId(HttpContext, playlistId);
        if (decision.Binding is not { } binding || !decision.IsAuthorized)
        {
            return decision.Outcome == HlsJobOwnershipOutcome.NoSuchJob
                ? NotFound("Hls segment not found.")
                : Unauthorized();
        }

        if (!Guid.TryParse(itemId, out var routeItemId) || !routeItemId.Equals(binding.ItemId))
        {
            return Unauthorized();
        }

        var file = Path.GetFullPath(Path.Combine(
            binding.CanonicalRoot,
            string.Concat(playlistId, Path.GetExtension(Request.Path.Value.AsSpan()))));

        if (!string.Equals(Path.GetDirectoryName(file), binding.CanonicalRoot, StringComparison.Ordinal)
            || Path.GetExtension(file.AsSpan()).Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Invalid segment.");
        }

        return GetFileResult(file, binding.CanonicalPlaylistPath);
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
        // #153-LTV-R3. Both parameters are caller-named, and this used to act on whatever job they
        // selected. That is not a disclosure — nothing is read and nothing is served — but it let
        // any authenticated caller end anyone else's transcode. The job is resolved from server
        // state first and the same owner comparison the byte routes use decides.
        //
        // A job this caller does not own answers exactly as one that never existed: 204, having
        // done nothing. Distinguishing the two would turn this route into a probe for which play
        // sessions are live.
        var job = _transcodeManager.GetTranscodingJob(playSessionId);
        if (job is null || !_jobOwnership.OwnsJob(HttpContext, job.UserId, job.OwnerDeviceId))
        {
            return NoContent();
        }

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
        // #153-LTV-R3. The caller is compared with the job before any path exists. The authorizer
        // resolves the binding exactly once and hands it back, so nothing below re-reads server
        // state that could have changed under it.
        var decision = _jobOwnership.AuthorizeByPlaylistId(HttpContext, playlistId);
        if (decision.Binding is not { } binding || !decision.IsAuthorized)
        {
            // Closed refusal, with no fallback to the historical resolution. A live job's segment
            // files outlive it (#153-LTV-S0 measured 22 of them surviving a teardown); once the
            // job is gone they are unreachable rather than served to whoever names them.
            return decision.Outcome == HlsJobOwnershipOutcome.NoSuchJob
                ? NotFound("Hls segment not found.")
                : Unauthorized();
        }

        if (!Guid.TryParse(itemId, out var routeItemId) || !routeItemId.Equals(binding.ItemId))
        {
            // itemId is consumed here, and this is the only place it ever was not.
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
