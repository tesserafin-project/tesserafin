using System;
using System.Globalization;

namespace Reefin.Playback.Shadow;

/// <summary>
/// A tri-state stream selection for a <see cref="DecisionVector"/> axis (video/audio/subtitle),
/// replacing a bare <c>int?</c> which conflated three distinct meanings under a single
/// <see langword="null"/>: "the projector doesn't know", "no stream was selected", and (implicitly)
/// "a stream was selected". Collapsing those meant a real divergence - legacy selecting no subtitle
/// while v2 selects one - could pass through <see cref="ShadowComparer"/> unnoticed. See
/// docs/pr93-compatibility-lab.md §4.
/// </summary>
public readonly record struct StreamSelection
{
    /// <summary>
    /// The projector could not determine whether a stream was selected. Never counts as a
    /// divergence against any other value, on either axis of a comparison.
    /// </summary>
    public static readonly StreamSelection Unknown = new(State.Unknown, 0);

    /// <summary>
    /// The projector positively determined that no stream was selected on this axis.
    /// </summary>
    public static readonly StreamSelection None = new(State.None, 0);

    private readonly State _state;
    private readonly int _index;

    private StreamSelection(State state, int index)
    {
        _state = state;
        _index = index;
    }

    private enum State
    {
        Unknown,
        None,
        Selected,
    }

    /// <summary>
    /// Gets a value indicating whether this selection is <see cref="Unknown"/>.
    /// </summary>
    public bool IsUnknown => _state == State.Unknown;

    /// <summary>
    /// Gets a value indicating whether this selection is <see cref="None"/>.
    /// </summary>
    public bool IsNone => _state == State.None;

    /// <summary>
    /// Gets a value indicating whether this selection carries a stream index.
    /// </summary>
    public bool IsSelected => _state == State.Selected;

    /// <summary>
    /// Gets the selected stream index.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="IsSelected"/> is <see langword="false"/>.</exception>
    public int Index => _state == State.Selected
        ? _index
        : throw new InvalidOperationException("This StreamSelection has no index: it is Unknown or None.");

    /// <summary>
    /// Creates a selection for the stream at <paramref name="index"/>.
    /// </summary>
    /// <param name="index">The selected stream index.</param>
    /// <returns>A <see cref="StreamSelection"/> carrying <paramref name="index"/>.</returns>
    public static StreamSelection Selected(int index) => new(State.Selected, index);

    /// <summary>
    /// Whether two selections diverge. A <see cref="Unknown"/> on either side never counts as a
    /// divergence (the projector simply couldn't tell). Otherwise, <see cref="None"/> against
    /// <see cref="Selected"/> diverges, and two <see cref="Selected"/> values diverge when their
    /// indices differ.
    /// </summary>
    /// <param name="a">The first selection to compare.</param>
    /// <param name="b">The second selection to compare.</param>
    /// <returns><see langword="true"/> if the two selections diverge.</returns>
    public static bool Differ(StreamSelection a, StreamSelection b)
    {
        if (a.IsUnknown || b.IsUnknown)
        {
            return false;
        }

        if (a.IsNone && b.IsNone)
        {
            return false;
        }

        if (a.IsSelected && b.IsSelected)
        {
            return a._index != b._index;
        }

        // One is None, the other is Selected.
        return true;
    }

    /// <summary>
    /// True specifically when one side is <see cref="None"/> and the other is <see cref="Selected"/>
    /// - the "a stream silently appeared/disappeared" case this type exists to catch, which
    /// <see cref="ShadowComparer"/> escalates to at least <see cref="DivergenceClass.PotentialRegression"/>
    /// regardless of any other equalities.
    /// </summary>
    /// <param name="a">The first selection to compare.</param>
    /// <param name="b">The second selection to compare.</param>
    /// <returns><see langword="true"/> if exactly one of the two selections is <see cref="None"/> and the other is <see cref="Selected"/>.</returns>
    public static bool IsNoneVsSelected(StreamSelection a, StreamSelection b) =>
        (a.IsNone && b.IsSelected) || (b.IsNone && a.IsSelected);

    /// <inheritdoc/>
    public override string ToString() => _state switch
    {
        State.None => "none",
        State.Selected => _index.ToString(CultureInfo.InvariantCulture),
        _ => "unknown",
    };
}
