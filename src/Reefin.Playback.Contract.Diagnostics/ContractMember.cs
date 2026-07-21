namespace Reefin.Playback.Contract.Diagnostics;

/// <summary>
/// One segment of a <see cref="ContractPath"/>: a member the SERVER declares in the playback
/// request contract, named by a server-owned enum rather than by any text the client sent
/// (issue #75, "chemins connus représentés par un enum interne au serveur, jamais par une chaîne
/// fournie par le client").
/// </summary>
/// <remarks>
/// This enum is the whole reason unknown members are unobservable in this iteration: a member the
/// client sent that has no value here simply cannot be named, and issue #75 forbids carrying its
/// name in any other form - including truncated, normalized, or hashed. See
/// <see cref="ContractMappingDiagnostic.UnknownMemberTotal"/>.
/// </remarks>
public enum ContractMember
{
    /// <summary>No segment. Pads the unused tail of a shorter <see cref="ContractPath"/>.</summary>
    None = 0,

    /// <summary>The request's declared client capabilities root.</summary>
    Capabilities = 1,

    /// <summary>The decode-only facet of the declared capabilities.</summary>
    Decode = 2,

    /// <summary>The declared container+codec direct-play combinations.</summary>
    DirectPlayProfiles = 3,

    /// <summary>The declared per-video-codec decode limits.</summary>
    VideoCodecs = 4,

    /// <summary>The declared per-audio-codec decode limits.</summary>
    AudioCodecs = 5,

    /// <summary>The declared subtitle formats and delivery methods.</summary>
    SubtitleDelivery = 6,

    /// <summary>The declared "this client can play HLS renditions" flag.</summary>
    SupportsHls = 7,

    /// <summary>The declared "this client can play DASH renditions" flag.</summary>
    SupportsDash = 8,

    /// <summary>The declared transcoding output targets, in the client's preference order.</summary>
    OutputProfiles = 9,

    /// <summary>
    /// Issue #75 slice 75b: the request body root itself, as a container - the outermost object the
    /// bounded structural scan walks. Names the level a top-level unknown member is attributed to
    /// (see <see cref="ContractUnknownMemberCount"/>). A container segment, never a value.
    /// </summary>
    Request = 10,
}
