using System;
using System.Collections.Generic;
using Reefin.Playback.Decision;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// The stable client-facing playback session response (docs/pr92-design-playback-api-and-diagnostics.md
/// §4.2): a versioned projection of a playback decision, never the internal
/// <c>Reefin.Controller.MediaEncoding.PlaybackSession</c> record. Every property is either a
/// primitive or drawn from the <see cref="Reefin.Playback.Decision"/> vocabulary (PR91) — no
/// <c>Reefin.Model.Dlna</c> type (<c>DeviceProfile</c>, <c>MediaOptions</c>, <c>StreamInfo</c>)
/// ever appears here, so the shape stays stable regardless of which engine produced the decision.
/// </summary>
/// <param name="Id">The session identifier.</param>
/// <param name="Kind">Whether this is an audio or video session.</param>
/// <param name="DecisionVersion">
/// The version of the decision engine that produced this session's plan. Since PR115a, this is
/// the real <c>PlaybackDecision.EngineVersion</c> when the session's plan authority is v2 (canary
/// cohort member, or full v2 mode) and this response reflects that decision (see
/// <see cref="PlaybackSessionDtos.PlaybackSessionResponseMapper"/>'s
/// <c>Map(PlaybackSession, Reefin.MediaEncoding.Playback.V2PlanRecord?)</c> overload) — otherwise
/// it is <see cref="LegacyDecisionVersion"/>, the sentinel for a legacy-projected response.
/// </param>
/// <param name="Method">The chosen playback method.</param>
/// <param name="Output">The shape of the output the client will receive.</param>
/// <param name="SelectedStreams">The streams selected for playback.</param>
/// <param name="Transforms">
/// The pipeline transforms this decision implies. For legacy-sourced sessions this is
/// best-effort, not read directly off the plan — see
/// <see cref="PlaybackSessionDtos.PlaybackSessionResponseMapper"/> for the derivation and its
/// documented approximations.
/// </param>
/// <param name="Reasons">
/// A flat summary of the reason codes behind this decision (no technical detail beyond the code
/// itself, and never a file path, token, or ffmpeg argument). For legacy-sourced sessions, only
/// the constraint codes that mirror <c>Reefin.Model.Session.TranscodeReason</c> one-to-one can be
/// derived — positive/marker codes such as <see cref="ReasonCode.TonemapRequired"/> or
/// <see cref="ReasonCode.MethodChosen"/> have no legacy reason bit and never appear here.
/// </param>
/// <param name="CreatedAt">When the session was first created.</param>
/// <param name="UpdatedAt">When the session was last created or replaced.</param>
/// <param name="PlaybackAttemptId">
/// Issue #43. The opaque attempt id the client supplied on the request that created or last
/// re-planned this session, echoed back verbatim, or <see langword="null"/> when none was supplied.
/// Echoing it lets a client confirm the server filed its attempt under the value it meant, and lets
/// an operator join this response to the client's own trace for the SAME attempt — including across
/// a retry, where the value is identical while the <c>RequestId</c> of issue #42 is not.
/// Additive and optional: a client that never sends it never sees it, and nothing else changes.
/// </param>
public sealed record PlaybackSessionResponse(
    Guid Id,
    MediaKind Kind,
    int DecisionVersion,
    PlaybackMethod Method,
    OutputSpec Output,
    SelectedStreams SelectedStreams,
    IReadOnlyList<TransformKind> Transforms,
    IReadOnlyList<ReasonCode> Reasons,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PlaybackAttemptId = null)
{
    /// <summary>
    /// The sentinel <see cref="DecisionVersion"/> for sessions whose response was projected from
    /// the legacy planner rather than an authoritative v2 decision — either because no
    /// <c>V2PlanRecord</c> is retained for the session, or one is retained but its decision is not
    /// viable (PR115a: v2 was authoritative for the planning call but produced nothing executable,
    /// so the response must not claim v2 authorship it did not deliver on). Deliberately distinct
    /// from any real <c>PlaybackDecision.EngineVersion</c> value (which starts at 1, per
    /// <see cref="PlaybackDecision"/>'s own invariant), so a client can tell a legacy-sourced
    /// decision apart from a real v2 one.
    /// </summary>
    public const int LegacyDecisionVersion = 0;
}
