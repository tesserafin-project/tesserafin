using System;
using Reefin.MediaEncoding.Playback;
using Xunit;

namespace Reefin.MediaEncoding.Tests.Playback;

/// <summary>
/// Tests for <see cref="CanaryCohort"/> (PR115a): membership must be a deterministic, stable
/// function of (user, device, percentage) - the properties that make a canary rollout coherent -
/// never a per-request draw.
/// </summary>
public class CanaryCohortTests
{
    [Fact]
    public void IsInCohort_ZeroPercentage_EnrollsNobody()
    {
        for (var i = 0; i < 100; i++)
        {
            Assert.False(CanaryCohort.IsInCohort(Guid.NewGuid(), $"device-{i}", 0));
        }
    }

    [Fact]
    public void IsInCohort_FullPercentage_EnrollsEverybody()
    {
        for (var i = 0; i < 100; i++)
        {
            Assert.True(CanaryCohort.IsInCohort(Guid.NewGuid(), $"device-{i}", 100));
        }
    }

    [Fact]
    public void Bucket_SamePair_AlwaysSameBucket()
    {
        var userId = Guid.Parse("a1b2c3d4-e5f6-4a1b-8c2d-3e4f5a6b7c8d");
        var first = CanaryCohort.Bucket(userId, "web-abc123");

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(first, CanaryCohort.Bucket(userId, "web-abc123"));
        }
    }

    [Fact]
    public void Bucket_DeviceIdCasing_DoesNotChangeBucket()
    {
        var userId = Guid.NewGuid();

        Assert.Equal(CanaryCohort.Bucket(userId, "Web-ABC123"), CanaryCohort.Bucket(userId, "web-abc123"));
    }

    [Fact]
    public void Bucket_NullAndEmptyDeviceId_AreEquivalent()
    {
        var userId = Guid.NewGuid();

        Assert.Equal(CanaryCohort.Bucket(userId, null), CanaryCohort.Bucket(userId, string.Empty));
    }

    [Fact]
    public void Bucket_AlwaysInZeroToNinetyNineRange()
    {
        for (var i = 0; i < 500; i++)
        {
            var bucket = CanaryCohort.Bucket(Guid.NewGuid(), $"device-{i}");

            Assert.InRange(bucket, 0, 99);
        }
    }

    /// <summary>
    /// Raising the percentage must only ever ADD pairs to the cohort: a pair served v2 at 5% must
    /// still be served v2 at 20%, or a rollout increase would flip existing canary sessions back
    /// and forth between engines.
    /// </summary>
    [Fact]
    public void IsInCohort_RaisingPercentage_NeverEvictsAPair()
    {
        for (var i = 0; i < 200; i++)
        {
            var userId = Guid.NewGuid();
            var deviceId = $"device-{i}";
            for (var percentage = 1; percentage < 100; percentage++)
            {
                if (CanaryCohort.IsInCohort(userId, deviceId, percentage))
                {
                    Assert.True(CanaryCohort.IsInCohort(userId, deviceId, percentage + 1));
                }
            }
        }
    }

    /// <summary>
    /// Distribution sanity, not statistical rigor: at a 50% cohort, 1000 distinct pairs should land
    /// somewhere near half in / half out. Bounds are deliberately loose (35-65%) so this never
    /// flakes; it exists to catch a broken hash (everything in one bucket), not bias in FNV-1a.
    /// </summary>
    [Fact]
    public void IsInCohort_HalfPercentage_RoughlyHalvesAStablePopulation()
    {
        var enrolled = 0;
        for (var i = 0; i < 1000; i++)
        {
            // Deterministic population (no Guid.NewGuid) so the assertion can never flake between runs.
            var userId = new Guid(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);
            if (CanaryCohort.IsInCohort(userId, $"device-{i}", 50))
            {
                enrolled++;
            }
        }

        Assert.InRange(enrolled, 350, 650);
    }
}
