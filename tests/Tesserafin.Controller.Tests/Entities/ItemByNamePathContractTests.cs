using System;
using System.IO;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Model.IO;
using Xunit;

namespace Tesserafin.Controller.Tests.Entities;

/// <summary>
/// Item-by-name entities compose their directory from a name that originates outside the server —
/// media metadata, an NFO file, a provider response. Each composed path must be a direct child of the
/// entity's own root; before the leaf-name contract, a name made only of dots or white space was
/// reduced to the empty string and the composed path became the root itself.
/// </summary>
[Collection(BaseItemStaticStateFixture.Name)]
public sealed class ItemByNamePathContractTests : IDisposable
{
    private readonly DirectoryInfo _tmp;
    private readonly IFileSystem? _previousFileSystem;
    private readonly IServerConfigurationManager? _previousConfigurationManager;

    public ItemByNamePathContractTests()
    {
        _previousFileSystem = BaseItem.FileSystem;
        _previousConfigurationManager = BaseItem.ConfigurationManager;

        _tmp = Directory.CreateTempSubdirectory("ibn-contract-");

        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.ArtistsPath).Returns(CreateRoot("artists"));
        paths.SetupGet(p => p.GenrePath).Returns(CreateRoot("genres"));
        paths.SetupGet(p => p.MusicGenrePath).Returns(CreateRoot("musicgenres"));
        paths.SetupGet(p => p.StudioPath).Returns(CreateRoot("studios"));
        paths.SetupGet(p => p.YearPath).Returns(CreateRoot("years"));
        paths.SetupGet(p => p.PeoplePath).Returns(CreateRoot("people"));

        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.SetupGet(c => c.ApplicationPaths).Returns(paths.Object);

        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(f => f.GetValidFilename(It.IsAny<string>()))
            .Returns<string>(s => s.Replace('/', ' ').Replace('\\', ' ').Replace(':', ' '));

        BaseItem.ConfigurationManager = configurationManager.Object;
        BaseItem.FileSystem = fileSystem.Object;
    }

    public static TheoryData<string> HostileNames { get; } = new()
    {
        ".",
        "..",
        "...",
        "  ..  ",
        string.Empty,
        "   ",
    };

    public static TheoryData<string> LegitimateNames { get; } = new()
    {
        "Sigur Rós",
        "AC-DC",
        "Rock & Roll",
        "東京事変",
        "A.K.A",
    };

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void GetPath_HostileName_Rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => MusicArtist.GetPath(name));
        Assert.Throws<ArgumentException>(() => Genre.GetPath(name));
        Assert.Throws<ArgumentException>(() => MusicGenre.GetPath(name));
        Assert.Throws<ArgumentException>(() => Studio.GetPath(name));
        Assert.Throws<ArgumentException>(() => Year.GetPath(name));
        Assert.Throws<ArgumentException>(() => Person.GetPath(name));
    }

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void GetPath_HostileName_NeverResolvesToTheRoot(string name)
    {
        // The pre-fix failure mode: the composed path equalled the item-by-name root, so a later
        // Directory.CreateDirectory / delete operated on every item under that root at once.
        foreach (var (root, act) in Routes())
        {
            string? composed = null;
            try
            {
                composed = act(name);
            }
            catch (ArgumentException)
            {
                continue;
            }

            Assert.NotEqual(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(composed)));
        }
    }

    [Theory]
    [MemberData(nameof(LegitimateNames))]
    public void GetPath_LegitimateName_IsPreservedAsDirectChild(string name)
    {
        Assert.Equal(name, Path.GetFileName(MusicArtist.GetPath(name)));
        Assert.Equal(name, Path.GetFileName(Genre.GetPath(name)));
        Assert.Equal(name, Path.GetFileName(MusicGenre.GetPath(name)));
        Assert.Equal(name, Path.GetFileName(Studio.GetPath(name)));
        Assert.Equal(name, Path.GetFileName(Year.GetPath(name)));

        var paths = BaseItem.ConfigurationManager.ApplicationPaths;
        Assert.Equal(Path.Combine(paths.ArtistsPath, name), MusicArtist.GetPath(name));
        Assert.Equal(Path.Combine(paths.GenrePath, name), Genre.GetPath(name));
        Assert.Equal(Path.Combine(paths.MusicGenrePath, name), MusicGenre.GetPath(name));
        Assert.Equal(Path.Combine(paths.StudioPath, name), Studio.GetPath(name));
        Assert.Equal(Path.Combine(paths.YearPath, name), Year.GetPath(name));
    }

    [Theory]
    [InlineData("../../etc/shadow")]
    [InlineData("/etc/shadow")]
    [InlineData("a\\..\\..\\b")]
    public void GetPath_TraversalAttempt_StaysUnderRoot(string name)
    {
        var artistsRoot = Path.GetFullPath(BaseItem.ConfigurationManager.ApplicationPaths.ArtistsPath);

        var path = Path.GetFullPath(MusicArtist.GetPath(name));

        Assert.StartsWith(artistsRoot + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(artistsRoot),
            Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void GetPath_Person_UsesPrefixSubdirectoryUnderRoot()
    {
        var peopleRoot = Path.GetFullPath(BaseItem.ConfigurationManager.ApplicationPaths.PeoplePath);

        var path = Path.GetFullPath(Person.GetPath("Zoe Saldana"));

        Assert.Equal(Path.Combine(peopleRoot, "Z", "Zoe Saldana"), path);
    }

    /// <summary>
    /// Databases written before the contract existed can hold an item whose <c>Path</c> is the
    /// item-by-name root itself. Refreshing such a row re-derives its path through
    /// <c>GetPath(name, normalizeName: false)</c>, which passes the persisted value straight into
    /// the contract. That must resolve to a direct child rather than back onto the root.
    /// </summary>
    [Fact]
    public void Rebase_PersistedPathEqualToTheRoot_ResolvesToADirectChild()
    {
        var artistsRoot = BaseItem.ConfigurationManager.ApplicationPaths.ArtistsPath;

        // A row written before the contract existed: Path is the root itself.
        var persisted = new MusicArtist { Name = "..", Path = artistsRoot };

        var rebased = MusicArtist.GetPath(Path.GetFileName(persisted.Path), false);

        Assert.Equal(Path.Combine(artistsRoot, Path.GetFileName(artistsRoot)), rebased);
        Assert.NotEqual(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(artistsRoot)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(rebased)));
    }

    [Fact]
    public void Rebase_PersistedOrdinaryPath_IsUnchanged()
    {
        var artistsRoot = BaseItem.ConfigurationManager.ApplicationPaths.ArtistsPath;
        var persisted = new MusicArtist { Name = "Sigur Rós", Path = Path.Combine(artistsRoot, "Sigur Rós") };

        Assert.Equal(persisted.Path, MusicArtist.GetPath(Path.GetFileName(persisted.Path), false));
    }

    [Theory]
    [InlineData("with:colon")]
    [InlineData("with\\backslash")]
    public void GetPath_PersistedLeafRejectedByTheContract_ThrowsRatherThanEscaping(string persistedLeaf)
    {
        // normalizeName: false is the rebasing overload — it does not run GetValidFilename, so a
        // leaf that a pre-contract server wrote to disk reaches the contract unchanged. Rejection is
        // the documented outcome; the caller (a metadata refresh) logs and continues.
        Assert.Throws<ArgumentException>(() => MusicArtist.GetPath(persistedLeaf, false));
    }

    public void Dispose()
    {
        BaseItem.FileSystem = _previousFileSystem!;
        BaseItem.ConfigurationManager = _previousConfigurationManager!;
        _tmp.Delete(true);
    }

    private static (string Root, Func<string, string> Act)[] Routes()
    {
        var paths = BaseItem.ConfigurationManager.ApplicationPaths;
        return
        [
            (paths.ArtistsPath, MusicArtist.GetPath),
            (paths.GenrePath, Genre.GetPath),
            (paths.MusicGenrePath, MusicGenre.GetPath),
            (paths.StudioPath, Studio.GetPath),
            (paths.YearPath, Year.GetPath),
            (paths.PeoplePath, Person.GetPath),
        ];
    }

    private string CreateRoot(string name)
        => Directory.CreateDirectory(Path.Combine(_tmp.FullName, name)).FullName;
}
