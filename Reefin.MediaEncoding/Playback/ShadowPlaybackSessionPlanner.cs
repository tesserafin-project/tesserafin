using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using Reefin.Controller.MediaEncoding;
using Reefin.Model.Dlna;
using Reefin.Playback.Decision;
using Reefin.Playback.Dlna;
using Reefin.Playback.Engine;
using Reefin.Playback.Shadow;

namespace Reefin.MediaEncoding.Playback;

/// <summary>
/// Decorates an <see cref="IPlaybackSessionPlanner"/> to run the v2 <see cref="IPlaybackEngine"/> in
/// shadow alongside it (PR98): legacy stays the source of truth for the plan returned to callers;
/// v2 runs only to have its decision projected, compared against the legacy plan, and logged as a
/// classified divergence. NO client-facing change: the shadow run never affects, and can never
/// throw into, the live path — every exception the v2 side might raise (including on inputs it
/// mishandles, such as a source with no streams) is caught and logged, not propagated.
/// </summary>
public sealed class ShadowPlaybackSessionPlanner : IPlaybackSessionPlanner
{
    private readonly IPlaybackSessionPlanner _inner;
    private readonly IPlaybackEngine _engine;
    private readonly ILogger<ShadowPlaybackSessionPlanner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShadowPlaybackSessionPlanner"/> class.
    /// </summary>
    /// <param name="inner">The legacy planner. Its result is the source of truth, returned unchanged.</param>
    /// <param name="engine">The v2 decision engine, run only in shadow.</param>
    /// <param name="logger">The logger the shadow comparison is reported through.</param>
    public ShadowPlaybackSessionPlanner(IPlaybackSessionPlanner inner, IPlaybackEngine engine, ILogger<ShadowPlaybackSessionPlanner> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _engine = engine;
        _logger = logger;
    }

    /// <inheritdoc/>
    public PlaybackPlan? PlanAudio(MediaOptions options)
    {
        var plan = _inner.PlanAudio(options);
        RunShadowSafely(PlaybackMediaKind.Audio, options, plan);
        return plan;
    }

    /// <inheritdoc/>
    public PlaybackPlan? PlanVideo(MediaOptions options)
    {
        var plan = _inner.PlanVideo(options);
        RunShadowSafely(PlaybackMediaKind.Video, options, plan);
        return plan;
    }

    /// <summary>
    /// Runs the v2 shadow decision and swallows every exception it might raise: the shadow path must
    /// never affect, delay meaningfully, or fail the live planning path.
    /// </summary>
    private void RunShadowSafely(PlaybackMediaKind kind, MediaOptions options, PlaybackPlan? plan)
    {
        try
        {
            RunShadow(kind, options, plan);
        }
#pragma warning disable CA1031 // Do not catch general exception types - shadow-mode safety requires it: v2 must never affect the live path.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Shadow v2 playback decision threw for item {ItemId} ({Kind}); legacy plan is unaffected.", options.ItemId, kind);
        }
    }

    private void RunShadow(PlaybackMediaKind kind, MediaOptions options, PlaybackPlan? plan)
    {
        var capabilities = DlnaPlaybackAdapter.ToCapabilities(options.Profile);
        var constraints = DlnaPlaybackAdapter.ToConstraints(options);
        var sources = options.MediaSources.Select(DlnaPlaybackAdapter.ToSnapshot).ToList();
        var mediaKind = kind == PlaybackMediaKind.Video ? MediaKind.Video : MediaKind.Audio;
        var context = DlnaPlaybackAdapter.ToContext(options.ItemId, Guid.Empty, options.MediaSourceId, mediaKind, PlaybackEngine.EngineVersion);

        var decision = _engine.Decide(context, capabilities, sources, constraints);

        var legacyVector = LegacyDecisionProjector.Project(plan);
        var v2Vector = V2DecisionProjector.Project(decision);
        var divergence = ShadowComparer.Compare(legacyVector, v2Vector);

        if (divergence.Class == DivergenceClass.Equivalent)
        {
            _logger.LogDebug(
                "Shadow v2 decision [{Class}] for item {ItemId} ({Kind}): {Summary}",
                divergence.Class,
                options.ItemId,
                kind,
                divergence.Summary);
        }
        else if (divergence.Class is DivergenceClass.PotentialRegression or DivergenceClass.Unexplained)
        {
            _logger.LogWarning(
                "Shadow v2 decision [{Class}] for item {ItemId} ({Kind}): {Summary}",
                divergence.Class,
                options.ItemId,
                kind,
                divergence.Summary);
        }
        else
        {
            _logger.LogInformation(
                "Shadow v2 decision [{Class}] for item {ItemId} ({Kind}): {Summary}",
                divergence.Class,
                options.ItemId,
                kind,
                divergence.Summary);
        }
    }
}
