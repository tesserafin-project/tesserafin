using Reefin.Playback.Decision;

namespace Reefin.MediaEncoding.Playback;

/// <summary>
/// Issue #75: the request-scoped facts the shadow run cannot recover on its own, handed to it
/// through the ambient capture scope <see cref="IShadowDiagnosticsStore.BeginCapture(ShadowCaptureInputs?)"/>
/// already opens around a synchronous planning call - deliberately reusing that existing channel
/// rather than adding a second one.
/// </summary>
/// <remarks>
/// Both members are optional, and both are absent on the legacy <c>MediaInfoHelper</c> path, which
/// has no v2 request body: the diagnostic is then simply not produced, rather than produced with
/// invented values.
/// </remarks>
/// <param name="DeclaredCapabilities">
/// The <see cref="ClientCapabilities"/> the client sent verbatim in the v2 request body - the
/// "before" side of issue #75's mapping comparison. Never retained: only counts and presence flags
/// derived from it survive into <see cref="ShadowDiagnosticRecord.ContractMapping"/>.
/// </param>
/// <param name="PayloadSizeBytes">
/// The request's <c>Content-Length</c> header value, or <see langword="null"/> when the header is
/// absent. Read from the header only - request buffering is NOT enabled to measure the body.
/// </param>
public sealed record ShadowCaptureInputs(
    ClientCapabilities? DeclaredCapabilities,
    long? PayloadSizeBytes);
