using Reefin.MediaEncoding.Playback;
using Reefin.Playback.Decision;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// PR117 (docs/pr116d-url-contract-design.md §2.2): the companion, client-facing descriptor of the
/// executable URL for a session already planned via <c>Playback/Sessions</c> - returned only from
/// <c>GET Playback/Sessions/{id}/Stream</c>, never composed into <see cref="PlaybackSessionListItem"/>
/// or <see cref="PlaybackDiagnosticDetail"/> (design doc §1.4: those two reach the admin-elevated,
/// cross-user diagnostics surface, and a URL there would leak the access token
/// <see cref="Url"/> carries to any administrator browsing another user's session).
/// </summary>
/// <param name="Url">
/// The executable URL, resolved at call time - direct output of <c>StreamInfo.ToUrl(null, accessToken, null)</c>,
/// relative (the client already knows its own <c>baseUrl</c>, same posture as the legacy
/// <c>TranscodingUrl</c>).
/// </param>
/// <param name="Protocol">Same vocabulary as <see cref="OutputSpec.Protocol"/> (<see cref="StreamingProtocol.Hls"/>/<see cref="StreamingProtocol.Http"/>).</param>
/// <param name="ServedBy">
/// The version of the engine that actually produced <see cref="Url"/>, resolved at THIS call - same
/// sentinel as <see cref="PlaybackSessionResponse.LegacyDecisionVersion"/>, but deliberately distinct
/// from the <c>DecisionVersion</c> the originating <c>POST</c>/<c>PUT</c> announced (design doc §3.1:
/// the two can diverge, this field is authoritative for what is actually served).
/// </param>
/// <param name="FallbackReason">
/// Why legacy was served instead of v2, or <see langword="null"/> when <see cref="ServedBy"/> is a
/// real v2 engine version. Same restricted enum already surfaced on the admin diagnostics detail
/// route (design doc §2.2/§4).
/// </param>
/// <param name="SubtitleUrl">
/// The external subtitle delivery URL, present only when the served stream's selected subtitle
/// delivery method is <see cref="SubtitleDeliveryMethod.External"/> - <see langword="null"/>
/// otherwise (design doc §2.2).
/// </param>
public sealed record PlaybackSessionStreamDescriptor(
    string Url,
    StreamingProtocol Protocol,
    int ServedBy,
    PlaybackLiveFallbackReason? FallbackReason,
    string? SubtitleUrl);
