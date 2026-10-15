namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Resolves the progressive job that writes an output file (#293).
/// </summary>
/// <remarks>
/// A separate interface for the reason <see cref="ITranscodeOwnedStop"/> is one: adding members to
/// the published <see cref="IHlsSegmentBindingRegistry"/> breaks every implementation of it.
///
/// The progressive stream routes derive their output file the way <c>DynamicHlsController</c>
/// does, from values the client sends, so a replayed url arrives at the same file. A progressive
/// job is never an HLS job, and <see cref="IHlsSegmentBindingRegistry.ResolveByOutputPath"/>
/// selects HLS jobs only.
/// </remarks>
public interface IProgressiveJobBindingRegistry
{
    /// <summary>
    /// Returns the binding of the progressive job that writes a given output file, or
    /// <see langword="null"/> if no active progressive job does.
    /// </summary>
    /// <param name="outputPath">The output file the route resolved for the request.</param>
    /// <returns>The binding, or <see langword="null"/>.</returns>
    HlsSegmentBinding? ResolveProgressiveByOutputPath(string outputPath);
}
