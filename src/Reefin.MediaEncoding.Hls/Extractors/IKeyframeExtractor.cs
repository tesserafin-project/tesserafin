using System;
using System.Diagnostics.CodeAnalysis;
using Reefin.MediaEncoding.Keyframes;

namespace Reefin.MediaEncoding.Hls.Extractors;

/// <summary>
/// Keyframe extractor.
/// </summary>
public interface IKeyframeExtractor
{
    /// <summary>
    /// Gets a value indicating whether the extractor is based on container metadata.
    /// </summary>
    bool IsMetadataBased { get; }

    /// <summary>
    /// Attempt to extract keyframes.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="filePath">The path to the file.</param>
    /// <param name="keyframeData">The keyframes.</param>
    /// <returns>A value indicating whether the keyframe extraction was successful.</returns>
    bool TryExtractKeyframes(Guid itemId, string filePath, [NotNullWhen(true)] out KeyframeData? keyframeData);
}
