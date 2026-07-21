using Tesserafin.Model.Configuration;
using Xunit;

namespace Tesserafin.Model.Tests.Configuration;

/// <summary>
/// PR104: <see cref="PlaybackShadowOptions.SampleRate"/> and
/// <see cref="PlaybackShadowOptions.MaxExecutionMs"/> clamp out-of-range values on <c>set</c>
/// instead of throwing - see the class remarks on <see cref="PlaybackShadowOptions"/> for why clamp
/// was chosen over throw for this particular options POCO.
/// </summary>
public sealed class PlaybackShadowOptionsTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(-1.0, 0.0)]
    [InlineData(-0.0001, 0.0)]
    [InlineData(1.0001, 1.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(double.NegativeInfinity, 0.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(double.NaN, 0.0)]
    public void SampleRate_ClampsToZeroOneRange(double input, double expected)
    {
        var options = new PlaybackShadowOptions { SampleRate = input };

        Assert.Equal(expected, options.SampleRate);
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(int.MinValue, 1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void MaxExecutionMs_ClampsToAtLeastOne(int input, int expected)
    {
        var options = new PlaybackShadowOptions { MaxExecutionMs = input };

        Assert.Equal(expected, options.MaxExecutionMs);
    }

    [Fact]
    public void Defaults_AreAlreadyInRange()
    {
        var options = new PlaybackShadowOptions();

        Assert.False(options.Enabled);
        Assert.Equal(1.0, options.SampleRate);
        Assert.Equal(50, options.MaxExecutionMs);
        Assert.Equal(PlaybackEngineMode.Legacy, options.Mode);
        Assert.Equal(0, options.CanaryPercentage);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(42, 42)]
    [InlineData(-1, 0)]
    [InlineData(101, 100)]
    [InlineData(int.MinValue, 0)]
    [InlineData(int.MaxValue, 100)]
    public void CanaryPercentage_ClampsToZeroHundredRange(int input, int expected)
    {
        var options = new PlaybackShadowOptions { CanaryPercentage = input };

        Assert.Equal(expected, options.CanaryPercentage);
    }

    /// <summary>
    /// PR115a: the pre-PR115a <c>Enabled</c> flag still means shadow mode when <c>Mode</c> is left
    /// at its <see cref="PlaybackEngineMode.Legacy"/> default, so existing configurations keep
    /// their behavior; an explicit non-default <c>Mode</c> always wins over <c>Enabled</c>.
    /// </summary>
    [Theory]
    [InlineData(PlaybackEngineMode.Legacy, false, PlaybackEngineMode.Legacy)]
    [InlineData(PlaybackEngineMode.Legacy, true, PlaybackEngineMode.Shadow)]
    [InlineData(PlaybackEngineMode.Shadow, false, PlaybackEngineMode.Shadow)]
    [InlineData(PlaybackEngineMode.Shadow, true, PlaybackEngineMode.Shadow)]
    [InlineData(PlaybackEngineMode.Canary, false, PlaybackEngineMode.Canary)]
    [InlineData(PlaybackEngineMode.Canary, true, PlaybackEngineMode.Canary)]
    [InlineData(PlaybackEngineMode.V2, false, PlaybackEngineMode.V2)]
    [InlineData(PlaybackEngineMode.V2, true, PlaybackEngineMode.V2)]
    public void GetEffectiveMode_CombinesModeAndLegacyEnabledFlag(PlaybackEngineMode mode, bool enabled, PlaybackEngineMode expected)
    {
        var options = new PlaybackShadowOptions { Mode = mode, Enabled = enabled };

        Assert.Equal(expected, options.GetEffectiveMode());
    }
}
