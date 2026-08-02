using System;
using System.IO;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Moq;
using Tesserafin.Common.IO;
using Tesserafin.Model.IO;
using Tesserafin.Model.Playlists;
using Tesserafin.Server.Core.Playlists;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Playlists;

/// <summary>
/// The playlist create route composes its directory from a caller-supplied name. The route is
/// <c>[Authorize]</c> — any authenticated user reaches it — so the leaf-name contract, not the
/// caller, has to guarantee the result stays a direct child of the playlists folder.
/// </summary>
public class PlaylistNameContractTests
{
    private readonly IFixture _fixture;

    public PlaylistNameContractTests()
    {
        _fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });

        // The real ManagedFileSystem.GetValidFilename replaces separators and the drive separator
        // with a space and leaves everything else, including dots, untouched.
        _fixture.Freeze<Mock<IFileSystem>>()
            .Setup(f => f.GetValidFilename(It.IsAny<string>()))
            .Returns<string>(s => s.Replace('/', ' ').Replace('\\', ' ').Replace(':', ' '));
    }

    public static TheoryData<string?> HostileNames { get; } = new()
    {
        (string?)null,
        string.Empty,
        " ",
        "   ",
        ".",
        "..",
        "...",
    };

    [Theory]
    [MemberData(nameof(HostileNames))]
    public async Task CreatePlaylist_NameWithNoUsableLeaf_Rejected(string? name)
    {
        var sut = _fixture.Create<PlaylistManager>();

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.CreatePlaylist(new PlaylistCreationRequest { Name = name! }));
    }

    [Theory]
    [MemberData(nameof(HostileNames))]
    public async Task CreatePlaylist_NameWithNoUsableLeaf_CreatesNothingOnDisk(string? name)
    {
        var tmp = Directory.CreateTempSubdirectory("playlist-contract-");
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(tmp.FullName, "playlists")).FullName;
            var before = Directory.GetFileSystemEntries(tmp.FullName);

            var sut = _fixture.Create<PlaylistManager>();

            await Assert.ThrowsAsync<ArgumentException>(
                () => sut.CreatePlaylist(new PlaylistCreationRequest { Name = name! }));

            // The historical failure: an empty leaf collapsed onto the root, the collision loop then
            // appended "1" and produced "<parent>/playlists1" — a sibling of the managed root.
            Assert.False(Directory.Exists(root + "1"));
            Assert.Equal(before, Directory.GetFileSystemEntries(tmp.FullName));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally
        {
            tmp.Delete(true);
        }
    }

    // Names that merely contain dots — as opposed to consisting only of dots — are ordinary names.
    // They remain accepted and remain a direct child of the playlists root.
    [Theory]
    [InlineData("  ..  ")]
    [InlineData("A.K.A.")]
    [InlineData(".hidden")]
    [InlineData("Sigur Rós")]
    [InlineData("東京事変")]
    [InlineData("Rock & Roll (1979)")]
    public void LegitimateName_RemainsADirectChildOfTheRoot(string name)
    {
        var tmp = Directory.CreateTempSubdirectory("playlist-contract-");
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(tmp.FullName, "playlists")).FullName;

            var path = SafeDirectoryLeafName.CombineWithRoot(root, name, nameof(name));

            Assert.Equal(
                Path.TrimEndingDirectorySeparator(root),
                Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path)!));
            Assert.Equal(name, Path.GetFileName(path));
        }
        finally
        {
            tmp.Delete(true);
        }
    }
}
