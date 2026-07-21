using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Tesserafin.Playback.Decision;

/// <summary>
/// The output of the v2 playback decision engine: which method was chosen, what streams/output it
/// implies, what transformations the pipeline must perform, and the structured trace explaining
/// why. Invariants are enforced by construction: there is no public constructor, only the
/// validating static factories below, one per <see cref="PlaybackMethod"/> plus
/// <see cref="NotViable(PlaybackMethod, ReasonNode, int)"/> for the no-plan case. The private
/// constructor itself validates (not just the factories), because it also doubles as the
/// <see cref="JsonConstructorAttribute"/> target for <see cref="PlaybackDecisionJson"/>: without
/// validation living in the constructor, deserializing arbitrary/malformed JSON could reconstruct
/// exactly the invalid states this type exists to make unconstructable.
/// </summary>
public sealed record PlaybackDecision
{
    [JsonConstructor]
    private PlaybackDecision(
        PlaybackMethod method,
        bool isViable,
        string selectedSource,
        SelectedStreams selectedStreams,
        OutputSpec output,
        IReadOnlyList<TransformKind> transforms,
        ReasonNode reasoning,
        int engineVersion)
    {
        Validate(method, isViable, selectedSource, transforms, reasoning, engineVersion);

        this.Method = method;
        this.IsViable = isViable;
        this.SelectedSource = selectedSource;
        this.SelectedStreams = selectedStreams;
        this.Output = output;
        this.Transforms = transforms;
        this.Reasoning = reasoning;
        this.EngineVersion = engineVersion;
    }

    /// <summary>
    /// Gets the playback method this decision selected.
    /// </summary>
    public PlaybackMethod Method { get; }

    /// <summary>
    /// Gets a value indicating whether a viable playback plan was found. When <see langword="false"/>,
    /// <see cref="Reasoning"/> explains why, and <see cref="SelectedSource"/>,
    /// <see cref="SelectedStreams"/>, <see cref="Output"/>, and <see cref="Transforms"/> carry their
    /// empty/default values.
    /// </summary>
    public bool IsViable { get; }

    /// <summary>
    /// Gets the identifier of the media source selected for playback, or an empty string when
    /// <see cref="IsViable"/> is <see langword="false"/>.
    /// </summary>
    public string SelectedSource { get; }

    /// <summary>
    /// Gets the streams selected for playback.
    /// </summary>
    public SelectedStreams SelectedStreams { get; }

    /// <summary>
    /// Gets the shape of the output this decision produces.
    /// </summary>
    public OutputSpec Output { get; }

    /// <summary>
    /// Gets the transformations the pipeline must perform to realize this decision.
    /// </summary>
    public IReadOnlyList<TransformKind> Transforms { get; }

    /// <summary>
    /// Gets the structured trace explaining how this decision was reached.
    /// </summary>
    public ReasonNode Reasoning { get; }

    /// <summary>
    /// Gets the version of the engine that produced this decision.
    /// </summary>
    public int EngineVersion { get; }

    /// <summary>
    /// Creates a direct play decision: the source is played back unmodified, with no remuxing or
    /// transcoding.
    /// </summary>
    /// <param name="source">The selected media source identifier. Must be non-empty.</param>
    /// <param name="streams">The streams selected for playback.</param>
    /// <param name="output">The shape of the output (expected to mirror the source, unchanged, for direct play).</param>
    /// <param name="reasoning">The structured trace explaining the decision.</param>
    /// <param name="engineVersion">The version of the engine producing this decision. Must be at least 1.</param>
    /// <returns>A viable decision with <see cref="Method"/> of <see cref="PlaybackMethod.DirectPlay"/> and an empty <see cref="Transforms"/> list.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="source"/> is empty or <paramref name="engineVersion"/> is less than 1.</exception>
    public static PlaybackDecision DirectPlay(
        string source,
        SelectedStreams streams,
        OutputSpec output,
        ReasonNode reasoning,
        int engineVersion) =>
        new(PlaybackMethod.DirectPlay, true, source, streams, output, [], reasoning, engineVersion);

    /// <summary>
    /// Creates a remux decision: the source's streams are copied into a different output container
    /// without re-encoding.
    /// </summary>
    /// <param name="source">The selected media source identifier. Must be non-empty.</param>
    /// <param name="streams">The streams selected for playback.</param>
    /// <param name="output">The shape of the output.</param>
    /// <param name="transforms">
    /// The transforms the pipeline must perform. Must contain <see cref="TransformKind.RemuxContainer"/>
    /// and must not contain <see cref="TransformKind.TranscodeVideo"/> or
    /// <see cref="TransformKind.TranscodeAudio"/>, since a remux copies streams rather than re-encoding them.
    /// </param>
    /// <param name="reasoning">The structured trace explaining the decision.</param>
    /// <param name="engineVersion">The version of the engine producing this decision. Must be at least 1.</param>
    /// <returns>A viable decision with <see cref="Method"/> of <see cref="PlaybackMethod.Remux"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="source"/> is empty, <paramref name="engineVersion"/> is less
    /// than 1, or <paramref name="transforms"/> violates the constraints described above.
    /// </exception>
    public static PlaybackDecision Remux(
        string source,
        SelectedStreams streams,
        OutputSpec output,
        IReadOnlyList<TransformKind> transforms,
        ReasonNode reasoning,
        int engineVersion) =>
        new(PlaybackMethod.Remux, true, source, streams, output, transforms, reasoning, engineVersion);

    /// <summary>
    /// Creates a transcode decision: one or more streams are re-encoded into formats the client can
    /// play.
    /// </summary>
    /// <param name="source">The selected media source identifier. Must be non-empty.</param>
    /// <param name="streams">The streams selected for playback.</param>
    /// <param name="output">The shape of the output.</param>
    /// <param name="transforms">
    /// The transforms the pipeline must perform. Must be non-empty and contain at least one of
    /// <see cref="TransformKind.TranscodeVideo"/> or <see cref="TransformKind.TranscodeAudio"/>.
    /// </param>
    /// <param name="reasoning">The structured trace explaining the decision.</param>
    /// <param name="engineVersion">The version of the engine producing this decision. Must be at least 1.</param>
    /// <returns>A viable decision with <see cref="Method"/> of <see cref="PlaybackMethod.Transcode"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="source"/> is empty, <paramref name="engineVersion"/> is less
    /// than 1, or <paramref name="transforms"/> violates the constraints described above.
    /// </exception>
    public static PlaybackDecision Transcode(
        string source,
        SelectedStreams streams,
        OutputSpec output,
        IReadOnlyList<TransformKind> transforms,
        ReasonNode reasoning,
        int engineVersion) =>
        new(PlaybackMethod.Transcode, true, source, streams, output, transforms, reasoning, engineVersion);

    /// <summary>
    /// Creates a non-viable decision: no playback plan could be produced for the request.
    /// </summary>
    /// <param name="attemptedMethod">The method that was being attempted when no viable plan was found.</param>
    /// <param name="reasoning">
    /// The structured trace explaining the failure. Must contain a <see cref="ReasonCode.NoViablePlan"/>
    /// node somewhere in its tree (searched recursively through <see cref="ReasonNode.Children"/>).
    /// </param>
    /// <param name="engineVersion">The version of the engine producing this decision. Must be at least 1.</param>
    /// <returns>
    /// A decision with <see cref="IsViable"/> of <see langword="false"/>, an empty
    /// <see cref="SelectedSource"/>, <see cref="SelectedStreams.None"/>, <see cref="OutputSpec.Empty"/>,
    /// and an empty <see cref="Transforms"/> list.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="reasoning"/> does not contain a <see cref="ReasonCode.NoViablePlan"/>
    /// node, or when <paramref name="engineVersion"/> is less than 1.
    /// </exception>
    public static PlaybackDecision NotViable(PlaybackMethod attemptedMethod, ReasonNode reasoning, int engineVersion) =>
        new(attemptedMethod, false, string.Empty, SelectedStreams.None, OutputSpec.Empty, [], reasoning, engineVersion);

    /// <summary>
    /// Validates every invariant for the state a constructor call is about to build. Runs for both
    /// entry points into this type - the public factories above and JSON deserialization via the
    /// <see cref="JsonConstructorAttribute"/>-annotated constructor - so neither can produce an
    /// inconsistent <see cref="PlaybackDecision"/>.
    /// </summary>
    /// <param name="method">The candidate <see cref="Method"/> value.</param>
    /// <param name="isViable">The candidate <see cref="IsViable"/> value.</param>
    /// <param name="selectedSource">The candidate <see cref="SelectedSource"/> value.</param>
    /// <param name="transforms">The candidate <see cref="Transforms"/> value.</param>
    /// <param name="reasoning">The candidate <see cref="Reasoning"/> value.</param>
    /// <param name="engineVersion">The candidate <see cref="EngineVersion"/> value.</param>
    /// <exception cref="ArgumentException">Thrown when any invariant described on the factory methods is violated.</exception>
    private static void Validate(
        PlaybackMethod method,
        bool isViable,
        string selectedSource,
        IReadOnlyList<TransformKind> transforms,
        ReasonNode reasoning,
        int engineVersion)
    {
        if (engineVersion < 1)
        {
            throw new ArgumentException("Engine version must be at least 1.", nameof(engineVersion));
        }

        if (!isViable)
        {
            if (!ContainsReasonCode(reasoning, ReasonCode.NoViablePlan))
            {
                throw new ArgumentException("A non-viable decision's reasoning must contain a NoViablePlan node.", nameof(reasoning));
            }

            return;
        }

        if (string.IsNullOrEmpty(selectedSource))
        {
            throw new ArgumentException("A viable decision must have a non-empty selected source.", nameof(selectedSource));
        }

        switch (method)
        {
            case PlaybackMethod.DirectPlay:
                if (transforms.Count != 0)
                {
                    throw new ArgumentException("A direct play decision must not have any transforms.", nameof(transforms));
                }

                break;

            case PlaybackMethod.Remux:
                if (!transforms.Contains(TransformKind.RemuxContainer))
                {
                    throw new ArgumentException("A remux decision's transforms must contain RemuxContainer.", nameof(transforms));
                }

                if (transforms.Contains(TransformKind.TranscodeVideo) || transforms.Contains(TransformKind.TranscodeAudio))
                {
                    throw new ArgumentException("A remux decision copies streams; its transforms must not contain TranscodeVideo or TranscodeAudio.", nameof(transforms));
                }

                break;

            case PlaybackMethod.Transcode:
                if (transforms.Count == 0)
                {
                    throw new ArgumentException("A transcode decision's transforms must not be empty.", nameof(transforms));
                }

                if (!transforms.Contains(TransformKind.TranscodeVideo) && !transforms.Contains(TransformKind.TranscodeAudio))
                {
                    throw new ArgumentException("A transcode decision's transforms must contain at least one of TranscodeVideo or TranscodeAudio.", nameof(transforms));
                }

                break;
        }
    }

    private static bool ContainsReasonCode(ReasonNode node, ReasonCode code)
    {
        if (node.Code == code)
        {
            return true;
        }

        return node.Children.Any(child => ContainsReasonCode(child, code));
    }
}
