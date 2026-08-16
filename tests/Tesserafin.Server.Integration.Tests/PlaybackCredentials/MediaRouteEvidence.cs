namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// What a route's positive case is able to prove.
/// </summary>
public enum MediaRouteEvidence
{
    /// <summary>
    /// The route delivers the fixture's own bytes, so the positive case asserts the bytes.
    /// </summary>
    Bytes,

    /// <summary>
    /// The route reaches its action but cannot produce bytes from this fixture — no attachment, no
    /// trickplay tile, no running transcode. The positive case asserts the request was not refused
    /// by authorization and that the answer is indistinguishable from the same request made with a
    /// durable token, which is the whole of what authorization can be held responsible for.
    /// </summary>
    Entry
}
