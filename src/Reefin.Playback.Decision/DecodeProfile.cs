using System.Collections.Generic;

namespace Reefin.Playback.Decision;

/// <summary>
/// One direct-play combination a client declares: this exact container packaged with this exact
/// video codec and this exact audio codec (for <see cref="MediaKind.Video"/>), or this exact
/// container with this exact audio codec (for <see cref="MediaKind.Audio"/>).
/// </summary>
/// <remarks>
/// PR102b: the legacy <c>DirectPlayProfile</c> associates type, container, video codec(s), and
/// audio codec(s) as a single declared unit - a client that lists MP4/H.264 and, separately,
/// WebM/VP9 has declared exactly those two combinations, never MP4/VP9. Flattening the two
/// profiles into aggregate container/video-codec/audio-codec sets (as v2.0-v2.1 did) loses that
/// association and lets the engine accept combinations the client never declared - a false direct
/// play, which fails at the client rather than at decision time. <see cref="DecodeProfile"/>
/// preserves the association exactly as declared, one record per legacy <c>DirectPlayProfile</c>
/// entry, order preserved.
/// </remarks>
/// <param name="Type">Whether this profile is for audio-only or video (with or without audio) direct play.</param>
/// <param name="Containers">
/// The containers this profile accepts. An empty list is a wildcard - matches any container -
/// same semantics as the legacy <c>DirectPlayProfile</c>, where an empty <c>Container</c> string
/// means unrestricted.
/// </param>
/// <param name="VideoCodecs">
/// The video codecs this profile accepts. Empty is a wildcard (matches any video codec), same
/// semantics as a legacy <c>DirectPlayProfile</c> with no <c>VideoCodec</c> declared. Always empty
/// for a <see cref="MediaKind.Audio"/> profile.
/// </param>
/// <param name="AudioCodecs">
/// The audio codecs this profile accepts. Empty is a wildcard (matches any audio codec), same
/// semantics as a legacy <c>DirectPlayProfile</c> with no <c>AudioCodec</c> declared.
/// </param>
public sealed record DecodeProfile(
    MediaKind Type,
    IReadOnlyList<string> Containers,
    IReadOnlyList<string> VideoCodecs,
    IReadOnlyList<string> AudioCodecs);
