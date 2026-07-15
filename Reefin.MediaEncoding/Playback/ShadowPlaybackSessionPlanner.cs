using System;
using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Logging;
using Reefin.Controller.MediaEncoding;
using Reefin.Model.Configuration;
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
/// throw into, the live path - every exception the v2 side might raise (including on inputs it
/// mishandles, such as a source with no streams) is caught and logged, not propagated.
/// </summary>
/// <remarks>
/// PR100 hardening: the shadow run is now opt-in (<see cref="PlaybackShadowOptions.Enabled"/>,
/// default <see langword="false"/>), samplable (<see cref="PlaybackShadowOptions.SampleRate"/>),
/// time-budgeted (<see cref="PlaybackShadowOptions.MaxExecutionMs"/>), and aggregated: individual
/// benign outcomes (equivalence, expected improvement, known v2 limitations) are no longer logged
/// one-by-one - only genuine regressions/exceptions still log individually (Warning), while
/// everything is counted in <see cref="ShadowMetrics"/> and periodically summarized in a single
/// Information log line. When disabled or sampled out, this class does zero mapping, zero
/// allocation, and zero engine work: the legacy path is untouched.
/// </remarks>
public sealed class ShadowPlaybackSessionPlanner : IPlaybackSessionPlanner
{
    private readonly IPlaybackSessionPlanner _inner;
    private readonly IPlaybackEngine _engine;
    private readonly ILogger<ShadowPlaybackSessionPlanner> _logger;
    private readonly Func<PlaybackShadowOptions> _optionsAccessor;
    private readonly IShadowDiagnosticsStore _diagnosticsStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShadowPlaybackSessionPlanner"/> class with
    /// shadow mode always enabled at full sample rate and a fresh <see cref="ShadowMetrics"/>
    /// instance. Kept for source/binary compatibility with PR98 call sites and tests; production
    /// wiring should use the overload that accepts an options accessor bound to live server
    /// configuration.
    /// </summary>
    /// <param name="inner">The legacy planner. Its result is the source of truth, returned unchanged.</param>
    /// <param name="engine">The v2 decision engine, run only in shadow.</param>
    /// <param name="logger">The logger the shadow comparison is reported through.</param>
    public ShadowPlaybackSessionPlanner(IPlaybackSessionPlanner inner, IPlaybackEngine engine, ILogger<ShadowPlaybackSessionPlanner> logger)
        : this(inner, engine, logger, static () => new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 })
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShadowPlaybackSessionPlanner"/> class.
    /// </summary>
    /// <param name="inner">The legacy planner. Its result is the source of truth, returned unchanged.</param>
    /// <param name="engine">The v2 decision engine, run only in shadow.</param>
    /// <param name="logger">The logger the shadow comparison is reported through.</param>
    /// <param name="optionsAccessor">
    /// Returns the current <see cref="PlaybackShadowOptions"/> on every call, so live server
    /// configuration changes (enable/disable, sample rate, budget) take effect without restart.
    /// </param>
    /// <param name="metrics">
    /// The aggregate metrics sink. Defaults to a private instance if not supplied; pass a shared
    /// one if the metrics need to be observed from outside (e.g. a diagnostics endpoint).
    /// </param>
    /// <param name="diagnosticsStore">
    /// PR113: where a successful shadow run publishes its <see cref="ShadowDiagnosticRecord"/> for
    /// later correlation by <see cref="PlaybackSessionManager"/>. Defaults to a no-op instance when
    /// not supplied, keeping every pre-PR113 call site source/binary compatible - the shadow run
    /// then simply retains nothing, same as before this parameter existed.
    /// </param>
    public ShadowPlaybackSessionPlanner(
        IPlaybackSessionPlanner inner,
        IPlaybackEngine engine,
        ILogger<ShadowPlaybackSessionPlanner> logger,
        Func<PlaybackShadowOptions> optionsAccessor,
        ShadowMetrics? metrics = null,
        IShadowDiagnosticsStore? diagnosticsStore = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(optionsAccessor);

        _inner = inner;
        _engine = engine;
        _logger = logger;
        _optionsAccessor = optionsAccessor;
        Metrics = metrics ?? new ShadowMetrics();
        _diagnosticsStore = diagnosticsStore ?? NoOpShadowDiagnosticsStore.Instance;
    }

    /// <summary>
    /// Gets the aggregate shadow metrics accumulated by this instance.
    /// </summary>
    public ShadowMetrics Metrics { get; }

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
    /// Strictly short-circuits when shadow mode is disabled or this call is sampled out: no
    /// mapping, no projection, no engine invocation, no allocation beyond reading the current
    /// options. Otherwise times and runs the shadow comparison, swallowing every exception the v2
    /// side might raise so the live path is never affected.
    /// </summary>
    private void RunShadowSafely(PlaybackMediaKind kind, MediaOptions options, PlaybackPlan? plan)
    {
        var shadowOptions = _optionsAccessor();
        if (!shadowOptions.Enabled)
        {
            return;
        }

        if (shadowOptions.SampleRate < 1.0 && Random.Shared.NextDouble() >= shadowOptions.SampleRate)
        {
            return;
        }

        var totalStopwatch = Stopwatch.StartNew();
        try
        {
            RunShadow(kind, options, plan, shadowOptions, totalStopwatch);
        }
#pragma warning disable CA1031 // Do not catch general exception types - shadow-mode safety requires it: v2 must never affect the live path.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            totalStopwatch.Stop();
            var budgetExceeded = totalStopwatch.Elapsed.TotalMilliseconds > shadowOptions.MaxExecutionMs;
            var summary = Metrics.RecordException(totalStopwatch.Elapsed, budgetExceeded);

            _logger.LogWarning(ex, "Shadow v2 playback decision threw for item {ItemId} ({Kind}); legacy plan is unaffected.", options.ItemId, kind);
            if (budgetExceeded)
            {
                _logger.LogWarning(
                    "Shadow v2 playback decision for item {ItemId} ({Kind}) exceeded the {BudgetMs}ms budget before throwing: took {ElapsedMs}ms.",
                    options.ItemId,
                    kind,
                    shadowOptions.MaxExecutionMs,
                    totalStopwatch.Elapsed.TotalMilliseconds);
            }

            LogPeriodicSummaryIfAny(summary);
        }
    }

    private void RunShadow(PlaybackMediaKind kind, MediaOptions options, PlaybackPlan? plan, PlaybackShadowOptions shadowOptions, Stopwatch totalStopwatch)
    {
        var mappingStopwatch = Stopwatch.StartNew();
        var capabilities = DlnaPlaybackAdapter.ToCapabilities(options.Profile);
        var constraints = DlnaPlaybackAdapter.ToConstraints(options);
        var sources = options.MediaSources.Select(DlnaPlaybackAdapter.ToSnapshot).ToList();
        var mediaKind = kind == PlaybackMediaKind.Video ? MediaKind.Video : MediaKind.Audio;
        var context = DlnaPlaybackAdapter.ToContext(options.ItemId, Guid.Empty, options.MediaSourceId, mediaKind, PlaybackEngine.EngineVersion);
        mappingStopwatch.Stop();

        var engineStopwatch = Stopwatch.StartNew();
        var decision = _engine.Decide(context, capabilities, sources, constraints);
        engineStopwatch.Stop();

        var comparisonStopwatch = Stopwatch.StartNew();
        var legacyVector = LegacyDecisionProjector.Project(plan);
        var v2Vector = V2DecisionProjector.Project(decision);
        var divergence = ShadowComparer.Compare(legacyVector, v2Vector);
        comparisonStopwatch.Stop();

        totalStopwatch.Stop();
        var budgetExceeded = totalStopwatch.Elapsed.TotalMilliseconds > shadowOptions.MaxExecutionMs;
        var summary = Metrics.RecordExecution(divergence.Class, totalStopwatch.Elapsed, budgetExceeded);

        if (budgetExceeded)
        {
            _logger.LogWarning(
                "Shadow v2 playback decision for item {ItemId} ({Kind}) exceeded the {BudgetMs}ms budget: mapping={MappingMs}ms engine={EngineMs}ms comparison={ComparisonMs}ms total={TotalMs}ms.",
                options.ItemId,
                kind,
                shadowOptions.MaxExecutionMs,
                mappingStopwatch.Elapsed.TotalMilliseconds,
                engineStopwatch.Elapsed.TotalMilliseconds,
                comparisonStopwatch.Elapsed.TotalMilliseconds,
                totalStopwatch.Elapsed.TotalMilliseconds);
        }

        if (divergence.Class is DivergenceClass.PotentialRegression or DivergenceClass.Unexplained)
        {
            _logger.LogWarning(
                "Shadow v2 decision [{Class}] for item {ItemId} ({Kind}): {Summary}",
                divergence.Class,
                options.ItemId,
                kind,
                divergence.Summary);
        }

        // Equivalent / ExpectedImprovement / KnownV2Limitation are benign: no per-execution log
        // (that was the PR98 noise source). They are still counted above and surface via the
        // periodic aggregate summary below.
        LogPeriodicSummaryIfAny(summary);

        // PR113: publish for retention last, strictly after totalStopwatch has already stopped and
        // every timing/logging concern above has run - the record allocation and AsyncLocal write
        // must never perturb the measured shadow duration that feeds Metrics.RecordExecution. A
        // shadow run that reached this point always has a record to offer, even one that exceeded
        // its time budget. PlaybackSessionManager (the only synchronous caller of Plan(), which this
        // call is nested inside) reads this back post-hoc once it has minted/known the real session
        // id; the no-op default store simply drops it.
        _diagnosticsStore.Publish(new ShadowDiagnosticRecord(
            decision,
            legacyVector,
            divergence,
            context,
            capabilities,
            sources,
            constraints,
            kind,
            DateTimeOffset.UtcNow));
    }

    private void LogPeriodicSummaryIfAny(ShadowMetricsSnapshot? summary)
    {
        if (summary is null)
        {
            return;
        }

        _logger.LogInformation("Shadow v2 playback metrics summary: {Summary}", summary.ToSummaryString());
    }
}
