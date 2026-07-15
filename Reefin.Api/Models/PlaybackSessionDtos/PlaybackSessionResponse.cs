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
/// The version of the decision engine that produced this session's plan. Through PR112, the
/// legacy planner is the only source of truth for this response (see
/// <see cref="PlaybackSessionDtos.PlaybackSessionResponseMapper"/>), so this is always
/// <see cref="LegacyDecisionVersion"/> — never <c>PlaybackEngine.EngineVersion</c>: v2 does not
/// yet produce the decision this response reflects, it only shadow-compares against it (see
/// docs/pr92-design-playback-api-and-diagnostics.md §6). A future PR that makes v2 the source of
/// truth (PR115) would populate this from the engine that actually decided.
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
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// The sentinel <see cref="DecisionVersion"/> for sessions whose plan was produced by the
    /// legacy planner — the source of truth through PR112/PR115 — rather than the v2 decision
    /// engine. Deliberately distinct from any real <c>PlaybackEngine.EngineVersion</c> value
    /// (which starts at 1, per <see cref="PlaybackDecision"/>'s own invariant), so a client can
    /// tell a legacy-sourced decision apart from a real v2 one once PR115 starts populating this
    /// field from the engine.
    /// </summary>
    public const int LegacyDecisionVersion = 0;
}
