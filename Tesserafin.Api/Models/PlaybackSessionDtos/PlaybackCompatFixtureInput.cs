using System.Collections.Generic;
using Tesserafin.Playback.Decision;

namespace Tesserafin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// The v2 engine inputs a fixture export replays: everything the v2 decision engine needs to
/// reproduce <see cref="PlaybackCompatFixtureExpected"/>, mirroring the fixture schema's
/// <c>input</c> object.
/// </summary>
/// <param name="Context">The media kind the request was for.</param>
/// <param name="Capabilities">The client capabilities the shadow run captured.</param>
/// <param name="Sources">The media source snapshots the shadow run captured.</param>
/// <param name="RequestedMediaSourceId">The specific source requested, or <see langword="null"/> to let the engine choose.</param>
/// <param name="Constraints">The playback constraints the shadow run captured, minus fields the lab does not model.</param>
public sealed record PlaybackCompatFixtureInput(
    PlaybackCompatFixtureContext Context,
    ClientCapabilities Capabilities,
    IReadOnlyList<MediaSourceSnapshot> Sources,
    string? RequestedMediaSourceId,
    PlaybackCompatFixtureConstraints Constraints);
