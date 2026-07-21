namespace Tesserafin.Playback.Decision;

/// <summary>
/// Identifies what a <see cref="ReasonNode"/> is talking about: a container, a specific
/// video/audio/subtitle stream, a source, or the playback method as a whole.
/// </summary>
/// <param name="Kind">The kind of entity being referenced.</param>
/// <param name="StreamIndex">The stream index, when <paramref name="Kind"/> is <see cref="ReasonSubjectKind.VideoStream"/>, <see cref="ReasonSubjectKind.AudioStream"/>, or <see cref="ReasonSubjectKind.Subtitle"/>; otherwise <see langword="null"/>.</param>
/// <param name="SourceId">The source identifier, when <paramref name="Kind"/> is <see cref="ReasonSubjectKind.Source"/>; otherwise <see langword="null"/>.</param>
public sealed record ReasonSubject(ReasonSubjectKind Kind, int? StreamIndex, string? SourceId)
{
    /// <summary>
    /// Creates a subject referring to the source or output container.
    /// </summary>
    /// <returns>A subject of kind <see cref="ReasonSubjectKind.Container"/>.</returns>
    public static ReasonSubject Container() => new(ReasonSubjectKind.Container, null, null);

    /// <summary>
    /// Creates a subject referring to a specific video stream.
    /// </summary>
    /// <param name="index">The video stream index.</param>
    /// <returns>A subject of kind <see cref="ReasonSubjectKind.VideoStream"/>.</returns>
    public static ReasonSubject VideoStream(int index) => new(ReasonSubjectKind.VideoStream, index, null);

    /// <summary>
    /// Creates a subject referring to a specific audio stream.
    /// </summary>
    /// <param name="index">The audio stream index.</param>
    /// <returns>A subject of kind <see cref="ReasonSubjectKind.AudioStream"/>.</returns>
    public static ReasonSubject AudioStream(int index) => new(ReasonSubjectKind.AudioStream, index, null);

    /// <summary>
    /// Creates a subject referring to a specific subtitle stream.
    /// </summary>
    /// <param name="index">The subtitle stream index.</param>
    /// <returns>A subject of kind <see cref="ReasonSubjectKind.Subtitle"/>.</returns>
    public static ReasonSubject Subtitle(int index) => new(ReasonSubjectKind.Subtitle, index, null);

    /// <summary>
    /// Creates a subject referring to a media source.
    /// </summary>
    /// <param name="id">The source identifier.</param>
    /// <returns>A subject of kind <see cref="ReasonSubjectKind.Source"/>.</returns>
    public static ReasonSubject Source(string id) => new(ReasonSubjectKind.Source, null, id);

    /// <summary>
    /// Creates a subject referring to the overall playback method.
    /// </summary>
    /// <returns>A subject of kind <see cref="ReasonSubjectKind.Method"/>.</returns>
    public static ReasonSubject Method() => new(ReasonSubjectKind.Method, null, null);
}
