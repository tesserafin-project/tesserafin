using System.Collections.Generic;
using Reefin.Playback.Decision;

namespace Reefin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// The v2 engine's observed decision, mirroring the fixture schema's <c>expected</c> object - the
/// decision this export's <see cref="PlaybackCompatFixtureInput"/> is expected to reproduce when
/// replayed through the compatibility lab.
/// </summary>
/// <param name="Method">The playback method the v2 engine chose.</param>
/// <param name="SelectedStreams">The streams the v2 engine selected.</param>
/// <param name="Output">The output shape the v2 engine chose, minus fields the lab does not model.</param>
/// <param name="Transforms">The pipeline transforms the v2 engine's decision implies.</param>
/// <param name="ReasonCodes">Every <see cref="ReasonCode"/> in the v2 engine's reasoning tree, flattened.</param>
/// <param name="IsViable">Whether the v2 engine found a viable plan at all.</param>
public sealed record PlaybackCompatFixtureExpected(
    PlaybackMethod Method,
    PlaybackCompatFixtureSelectedStreams SelectedStreams,
    PlaybackCompatFixtureOutput Output,
    IReadOnlyList<string> Transforms,
    IReadOnlyList<string> ReasonCodes,
    bool IsViable);
