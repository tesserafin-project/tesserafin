using System;
using System.IO;
using System.Threading.Tasks;
using MediaBrowser.MediaEncoding.Encoder;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Reefin.MediaEncoding.Tests.Encoder;

/// <summary>
/// Verifies <see cref="VaapiHardwareProbe"/> against a real VAAPI render node, not a mock - the
/// whole point of this probe (transcoding-pipeline plan PR8) is that it catches devices that look
/// present but don't actually work, so a mocked process runner would prove nothing.
/// </summary>
public class VaapiHardwareProbeTests
{
    private const string RealVaapiRenderNode = "/dev/dri/renderD128";

    [Fact]
    public async Task ProbeAsync_RealVaapiHardware_Succeeds()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists(RealVaapiRenderNode), "requires a real VAAPI render node - not present in this environment");

        var probe = new VaapiHardwareProbe(new FfmpegProcessRunner(), NullLogger.Instance);

        var result = await probe.ProbeAsync("ffmpeg", RealVaapiRenderNode, TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task ProbeAsync_NonexistentDevice_Fails()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "VAAPI is Linux-only");

        var probe = new VaapiHardwareProbe(new FfmpegProcessRunner(), NullLogger.Instance);

        var result = await probe.ProbeAsync("ffmpeg", "/dev/dri/renderD999", TestContext.Current.CancellationToken);

        Assert.False(result);
    }
}
