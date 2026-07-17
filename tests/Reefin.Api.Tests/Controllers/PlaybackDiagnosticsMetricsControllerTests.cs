using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Reefin.Api.Controllers;
using Reefin.Api.Models.PlaybackSessionDtos;
using Reefin.Common.Api;
using Reefin.Controller.Configuration;
using Reefin.MediaEncoding.Playback;
using Reefin.Model.Configuration;
using Xunit;

namespace Reefin.Api.Tests.Controllers;

/// <summary>
/// PR115d: <see cref="PlaybackDiagnosticsMetricsController"/> - the admin-only operational gate
/// surface. Uses real <see cref="PlaybackOperationalMetrics"/>/<see cref="PlaybackStopThresholdGuard"/>
/// instances (concrete, no interface, same as their production shape) rather than mocks.
/// </summary>
public class PlaybackDiagnosticsMetricsControllerTests
{
    [Fact]
    public void Controller_RequiresElevation()
    {
        var attribute = typeof(PlaybackDiagnosticsMetricsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single();

        Assert.Equal(Policies.RequiresElevation, attribute.Policy);
    }

    [Fact]
    public void GetOperationalMetrics_ReflectsRecordedCounters()
    {
        var metrics = new PlaybackOperationalMetrics();
        metrics.RecordServed();
        metrics.RecordServed();
        metrics.RecordFallback(PlaybackLiveFallbackReason.KillSwitch);

        var shadowOptions = new PlaybackShadowOptions();
        var configManagerMock = new Mock<IServerConfigurationManager>();
        configManagerMock.Setup(c => c.Configuration).Returns(new Reefin.Model.Configuration.ServerConfiguration { PlaybackShadow = shadowOptions });

        var guard = new PlaybackStopThresholdGuard(() => shadowOptions, metrics, NullLogger<PlaybackStopThresholdGuard>.Instance);
        var controller = new PlaybackDiagnosticsMetricsController(metrics, guard, configManagerMock.Object);

        var result = controller.GetOperationalMetrics();

        var response = Assert.IsType<PlaybackOperationalMetricsResponse>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, response.ServedByV2Count);
        Assert.Equal(1, response.ServedByLegacyCount);
        Assert.Equal(1, response.FallbackReasonCounts[nameof(PlaybackLiveFallbackReason.KillSwitch)]);
        Assert.False(response.StopThresholdGuardTripped);
        Assert.True(response.StopThresholdGuardEnabled);
    }

    [Fact]
    public void GetOperationalMetrics_GuardTripped_ReflectsTrippedState()
    {
        var metrics = new PlaybackOperationalMetrics();
        metrics.RecordFallback(PlaybackLiveFallbackReason.AdapterError);

        var shadowOptions = new PlaybackShadowOptions();
        shadowOptions.StopThresholds.MinimumSampleSize = 1;
        shadowOptions.StopThresholds.AdapterErrorRateThreshold = 0.10;
        var configManagerMock = new Mock<IServerConfigurationManager>();
        configManagerMock.Setup(c => c.Configuration).Returns(new Reefin.Model.Configuration.ServerConfiguration { PlaybackShadow = shadowOptions });

        var guard = new PlaybackStopThresholdGuard(() => shadowOptions, metrics, NullLogger<PlaybackStopThresholdGuard>.Instance);
        var controller = new PlaybackDiagnosticsMetricsController(metrics, guard, configManagerMock.Object);

        var result = controller.GetOperationalMetrics();

        var response = Assert.IsType<PlaybackOperationalMetricsResponse>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.True(response.StopThresholdGuardTripped);
    }
}
