namespace Tesserafin.Controller.Net.PlaybackCredentials;

/// <summary>
/// Why a WebSocket ticket was refused.
/// </summary>
/// <remarks>
/// As with <see cref="PlaybackCapabilityFailure"/>, these separate causes all answer the same
/// undifferentiated 401 on the wire. <see cref="AlreadyUsed"/> in particular must not be
/// distinguishable from <see cref="Unknown"/> by a caller: telling a replayer that their value was
/// once real is telling them something.
/// </remarks>
public enum WebSocketTicketFailure
{
    /// <summary>
    /// No failure.
    /// </summary>
    None = 0,

    /// <summary>
    /// No ticket was presented.
    /// </summary>
    Missing = 1,

    /// <summary>
    /// The presented value matches no live ticket. Includes a ticket already consumed by an
    /// earlier successful upgrade, because consumption removes it.
    /// </summary>
    Unknown = 2,

    /// <summary>
    /// The ticket existed and its very short lifetime has passed.
    /// </summary>
    Expired = 3,

    /// <summary>
    /// The ticket was presented a second time. Distinguished from <see cref="Unknown"/> only
    /// internally, so a test can prove replay was the reason.
    /// </summary>
    AlreadyUsed = 4,

    /// <summary>
    /// The ticket was revoked with its owning session or user.
    /// </summary>
    Revoked = 5,

    /// <summary>
    /// The ticket is live but is bound to a different session.
    /// </summary>
    SessionMismatch = 6,

    /// <summary>
    /// The ticket is live but is bound to a different device.
    /// </summary>
    DeviceMismatch = 7
}
