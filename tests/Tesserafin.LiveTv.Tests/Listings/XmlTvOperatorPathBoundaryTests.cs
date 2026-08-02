using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.LiveTv.Listings;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.LiveTv;
using Xunit;

namespace Tesserafin.LiveTv.Tests.Listings;

/// <summary>
/// The XMLTV listings path is an operator-configured host path by design: pointing the server at a
/// file the operator chose is the feature. These tests pin the boundary that makes that acceptable —
/// the configured path is only ever read, and every file the provider creates or removes lives under
/// the server's own cache directory.
/// </summary>
public sealed class XmlTvOperatorPathBoundaryTests : IDisposable
{
    private const string Listing = """
        <?xml version="1.0" encoding="utf-8"?>
        <tv>
          <channel id="3297"><display-name>Channel 3297</display-name></channel>
        </tv>
        """;

    private readonly DirectoryInfo _tmp;
    private readonly string _cachePath;
    private readonly XmlTvListingsProvider _sut;

    public XmlTvOperatorPathBoundaryTests()
    {
        _tmp = Directory.CreateTempSubdirectory("xmltv-boundary-");
        _cachePath = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "cache")).FullName;

        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.CachePath).Returns(_cachePath);

        var config = new Mock<IServerConfigurationManager>();
        config.SetupGet(c => c.ApplicationPaths).Returns(paths.Object);
        config.SetupGet(c => c.Configuration).Returns(new ServerConfiguration());

        var fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
        fixture.Inject(config);
        fixture.Inject(new Mock<IHttpClientFactory>());
        _sut = fixture.Create<XmlTvListingsProvider>();
    }

    [Fact]
    public async Task GetChannels_OperatorPath_IsOnlyRead()
    {
        var operatorDir = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "operator")).FullName;
        var operatorFile = Path.Combine(operatorDir, "guide.xml");
        await File.WriteAllTextAsync(operatorFile, Listing, TestContext.Current.CancellationToken);
        var originalBytes = await File.ReadAllBytesAsync(operatorFile, TestContext.Current.CancellationToken);
        var originalWriteTime = File.GetLastWriteTimeUtc(operatorFile);

        var info = new ListingsProviderInfo { Id = "operator-configured", Path = operatorFile };

        await _sut.GetChannels(info, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(operatorFile));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(operatorFile, TestContext.Current.CancellationToken));
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(operatorFile));

        // Nothing was created next to, or in place of, the operator's own file.
        Assert.Equal(new[] { operatorFile }, Directory.GetFileSystemEntries(operatorDir));
    }

    [Fact]
    public async Task GetChannels_OperatorPath_CachesUnderTheServerCacheDirectory()
    {
        var operatorFile = Path.Combine(_tmp.FullName, "guide.xml");
        await File.WriteAllTextAsync(operatorFile, Listing, TestContext.Current.CancellationToken);

        var info = new ListingsProviderInfo { Id = "operator-configured", Path = operatorFile };

        await _sut.GetChannels(info, TestContext.Current.CancellationToken);

        var cached = Path.Combine(_cachePath, "xmltv", info.Id + ".xml");
        Assert.True(File.Exists(cached));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_cachePath, "xmltv")),
            Path.GetFullPath(Path.GetDirectoryName(cached)!));
    }

    [Fact]
    public async Task Validate_MissingOperatorPath_ThrowsWithoutCreatingIt()
    {
        var missing = Path.Combine(_tmp.FullName, "not-there.xml");
        var info = new ListingsProviderInfo { Id = "operator-configured", Path = missing };

        await Assert.ThrowsAsync<FileNotFoundException>(() => _sut.Validate(info, false, true));

        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task GetChannels_ArbitraryOperatorPath_RemainsSupported()
    {
        // An operator-selected path outside any server-managed root is the documented, supported
        // configuration. It must keep working.
        var outside = Directory.CreateTempSubdirectory("xmltv-outside-");
        try
        {
            var operatorFile = Path.Combine(outside.FullName, "elsewhere.xml");
            await File.WriteAllTextAsync(operatorFile, Listing, TestContext.Current.CancellationToken);

            var info = new ListingsProviderInfo { Id = "arbitrary", Path = operatorFile };

            var channels = await _sut.GetChannels(info, TestContext.Current.CancellationToken);

            Assert.Single(channels);
            Assert.Equal("3297", channels[0].Id);
        }
        finally
        {
            outside.Delete(true);
        }
    }

    public void Dispose() => _tmp.Delete(true);
}
