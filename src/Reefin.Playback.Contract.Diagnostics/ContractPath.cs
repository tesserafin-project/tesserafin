namespace Reefin.Playback.Contract.Diagnostics;

/// <summary>
/// A path to a known member of the playback request contract, expressed as a fixed-arity tuple of
/// server-owned <see cref="ContractMember"/> segments (issue #75).
/// </summary>
/// <remarks>
/// Fixed arity, not a list of segments, and enum-typed, not text: a path can only ever name
/// something the server itself declares. There is deliberately no way to build a path out of
/// anything a client sent - not even a normalized or truncated form of it. Three segments cover the
/// whole contract surface this iteration diagnoses (<c>Capabilities.Decode.VideoCodecs</c> is the
/// deepest); a shorter path pads its tail with <see cref="ContractMember.None"/>.
/// </remarks>
/// <param name="Root">The outermost segment.</param>
/// <param name="Branch">The middle segment, or <see cref="ContractMember.None"/> for a one-segment path.</param>
/// <param name="Leaf">The innermost segment, or <see cref="ContractMember.None"/> for a shorter path.</param>
public readonly record struct ContractPath(
    ContractMember Root,
    ContractMember Branch,
    ContractMember Leaf)
{
    /// <summary>Gets the path to the declared direct-play combinations.</summary>
    public static ContractPath DecodeDirectPlayProfiles => new(ContractMember.Capabilities, ContractMember.Decode, ContractMember.DirectPlayProfiles);

    /// <summary>Gets the path to the declared per-video-codec decode limits.</summary>
    public static ContractPath DecodeVideoCodecs => new(ContractMember.Capabilities, ContractMember.Decode, ContractMember.VideoCodecs);

    /// <summary>Gets the path to the declared per-audio-codec decode limits.</summary>
    public static ContractPath DecodeAudioCodecs => new(ContractMember.Capabilities, ContractMember.Decode, ContractMember.AudioCodecs);

    /// <summary>Gets the path to the declared subtitle formats and delivery methods.</summary>
    public static ContractPath DecodeSubtitleDelivery => new(ContractMember.Capabilities, ContractMember.Decode, ContractMember.SubtitleDelivery);

    /// <summary>Gets the path to the declared HLS support flag.</summary>
    public static ContractPath DecodeSupportsHls => new(ContractMember.Capabilities, ContractMember.Decode, ContractMember.SupportsHls);

    /// <summary>Gets the path to the declared DASH support flag.</summary>
    public static ContractPath DecodeSupportsDash => new(ContractMember.Capabilities, ContractMember.Decode, ContractMember.SupportsDash);

    /// <summary>Gets the path to the declared transcoding output targets.</summary>
    public static ContractPath OutputProfiles => new(ContractMember.Capabilities, ContractMember.OutputProfiles, ContractMember.None);

    /// <summary>
    /// Gets the path naming the request body root as a container (issue #75 slice 75b). A top-level
    /// member the client sent that the contract does not declare is attributed here.
    /// </summary>
    public static ContractPath Request => new(ContractMember.Request, ContractMember.None, ContractMember.None);

    /// <summary>
    /// Gets the path naming the declared client-capabilities object as a container (issue #75 slice
    /// 75b). An unknown member directly under <c>Capabilities</c> is attributed here.
    /// </summary>
    public static ContractPath Capabilities => new(ContractMember.Capabilities, ContractMember.None, ContractMember.None);

    /// <summary>
    /// Gets the path naming the declared decode-capabilities object as a container (issue #75 slice
    /// 75b). An unknown member directly under <c>Capabilities.Decode</c> is attributed here.
    /// </summary>
    public static ContractPath Decode => new(ContractMember.Capabilities, ContractMember.Decode, ContractMember.None);
}
