namespace Tesserafin.Controller.Net.PlaybackCredentials;

/// <summary>
/// Why a playback capability was refused.
/// </summary>
/// <remarks>
/// These are internal diagnosis, not a wire vocabulary. <see cref="Unknown"/>, <see cref="Expired"/>
/// and <see cref="Revoked"/> are deliberately distinct here so the tests can tell them apart, and
/// deliberately indistinguishable on the wire — all three answer 401 with no distinguishing body,
/// so a caller cannot use the response to learn which capabilities exist.
/// </remarks>
public enum PlaybackCapabilityFailure
{
    /// <summary>
    /// No failure.
    /// </summary>
    None = 0,

    /// <summary>
    /// No capability was presented.
    /// </summary>
    Missing = 1,

    /// <summary>
    /// The presented value matches no live capability.
    /// </summary>
    Unknown = 2,

    /// <summary>
    /// The capability existed and its expiry has passed.
    /// </summary>
    Expired = 3,

    /// <summary>
    /// The capability was revoked with its owning session, user or play session.
    /// </summary>
    Revoked = 4,

    /// <summary>
    /// The capability is live but does not carry the scope the route demands.
    /// </summary>
    ScopeMismatch = 5,

    /// <summary>
    /// The capability is live but is bound to a different item.
    /// </summary>
    ItemMismatch = 6,

    /// <summary>
    /// The capability is live but is bound to a different media source.
    /// </summary>
    MediaSourceMismatch = 7,

    /// <summary>
    /// The capability is live but is bound to a different session or play session.
    /// </summary>
    SessionMismatch = 8,

    /// <summary>
    /// Renewal was attempted outside the renewal window, which is the capability's final minutes.
    /// Chaining renewals from the moment of issue would turn a short-lived credential into a
    /// durable one with extra steps.
    /// </summary>
    RenewalTooEarly = 9,

    /// <summary>
    /// Renewal was attempted after expiry. An expired capability is not resurrectable; the client
    /// mints a new one with its durable token.
    /// </summary>
    RenewalAfterExpiry = 10
}
