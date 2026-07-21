using Tesserafin.Model.Dlna;
using Tesserafin.Playback.Decision;

namespace Tesserafin.Playback.Dlna;

/// <summary>
/// Convenience facade over the individual v2-to-DLNA reverse mappers
/// (<see cref="ReverseClientCapabilitiesMapper"/>, <see cref="ReverseConstraintsMapper"/>).
/// </summary>
/// <remarks>
/// TEMPORARY (PR112b): the mirror image of <see cref="DlnaPlaybackAdapter"/>, needed only because
/// the v2 client contract (<c>Tesserafin.Api.Models.PlaybackSessionDtos.CreatePlaybackSessionRequest</c>)
/// now accepts <see cref="ClientCapabilities"/>/<see cref="PlaybackConstraints"/> directly, while the
/// legacy <c>StreamBuilder</c> pipeline - not the v2 engine - is still the source of truth for live
/// decisions. Kept as a separate class from <see cref="DlnaPlaybackAdapter"/> rather than adding
/// reverse methods to it: <c>ArchitectureTests.DlnaPlaybackAdapter_AllPublicMethodsReturnDomainTypes</c>
/// asserts every public method on that facade returns a <see cref="Tesserafin.Playback.Decision"/> type,
/// and this facade's methods return the opposite direction (legacy DLNA types), so mixing them would
/// break that invariant instead of just needing a new one.
/// </remarks>
/// <remarks>
/// PR114a correction: this type maps the client's REQUEST (pre-decision) - legacy still needs it as
/// long as <c>StreamBuilder</c>, not the v2 engine, decides what to play. That is a different concern
/// from <see cref="PlaybackExecutionPlanAdapter"/> (new in PR114a), which maps a v2 DECISION
/// (post-<c>PlaybackEngine.Decide</c>) into a legacy <c>StreamInfo</c> so the execution machinery can
/// run it. Landing the execution layer does not retire this type - it stays until legacy stops being
/// consulted for live decisions at all, i.e. the PR115 canary cutover, not before.
/// </remarks>
public static class ReverseDlnaAdapter
{
    /// <summary>
    /// Reconstructs a legacy <see cref="DeviceProfile"/> from domain <see cref="ClientCapabilities"/>.
    /// </summary>
    /// <param name="capabilities">The domain capabilities to reconstruct a device profile from.</param>
    /// <returns>An equivalent legacy device profile.</returns>
    public static DeviceProfile ToDeviceProfile(ClientCapabilities capabilities) => ReverseClientCapabilitiesMapper.ToDeviceProfile(capabilities);

    /// <summary>
    /// Applies domain <see cref="PlaybackConstraints"/> onto an existing <see cref="MediaOptions"/>.
    /// </summary>
    /// <param name="options">The media options to apply the constraints onto.</param>
    /// <param name="constraints">The domain constraints to apply.</param>
    public static void ApplyConstraints(MediaOptions options, PlaybackConstraints constraints) => ReverseConstraintsMapper.ApplyTo(options, constraints);
}
