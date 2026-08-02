using System;
using System.IO;
using Tesserafin.Common.IO;
using Xunit;

namespace Tesserafin.Common.Tests.IO;

/// <summary>
/// The leaf-name contract shared by every route that combines an externally supplied name with a
/// server-managed root directory.
/// </summary>
public class SafeDirectoryLeafNameTests
{
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("....")]
    public void IsValid_DotOnly_Rejected(string name)
        => Assert.False(SafeDirectoryLeafName.IsValid(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(" \t \n ")]
    public void IsValid_EmptyOrWhitespaceOnly_Rejected(string? name)
        => Assert.False(SafeDirectoryLeafName.IsValid(name));

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("../b")]
    [InlineData("..\\b")]
    [InlineData("/etc/passwd")]
    [InlineData("/")]
    [InlineData("\\")]
    [InlineData("C:\\Windows")]
    [InlineData("C:")]
    [InlineData("C:name")]
    public void IsValid_SeparatorsRootedOrDriveQualified_Rejected(string name)
        => Assert.False(SafeDirectoryLeafName.IsValid(name));

    [Theory]
    [InlineData("My Playlist")]
    [InlineData("Sigur Rós")]
    [InlineData("東京")]
    [InlineData("АBC")]
    [InlineData("Rock & Roll (1979)")]
    [InlineData("A.K.A.")]
    [InlineData(".hidden")]
    [InlineData("trailing.")]
    [InlineData("  padded  ")]
    [InlineData("AC-DC")]
    [InlineData("100% Hits!")]
    [InlineData("a,b;c'd\"e")]
    public void IsValid_LegitimateNames_Accepted(string name)
        => Assert.True(SafeDirectoryLeafName.IsValid(name));

    [Fact]
    public void Validate_Valid_ReturnsName()
        => Assert.Equal("My Playlist", SafeDirectoryLeafName.Validate("My Playlist", "name"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    public void Validate_Invalid_Throws(string? name)
    {
        var ex = Assert.Throws<ArgumentException>(() => SafeDirectoryLeafName.Validate(name, "theParameter"));
        Assert.Equal("theParameter", ex.ParamName);
    }

    [Fact]
    public void CombineWithRoot_Valid_IsDirectChild()
    {
        var tmp = Directory.CreateTempSubdirectory("leafname-");
        try
        {
            var result = SafeDirectoryLeafName.CombineWithRoot(tmp.FullName, "My Playlist", "name");

            Assert.Equal(Path.Combine(tmp.FullName, "My Playlist"), result);
            Assert.Equal(Path.TrimEndingDirectorySeparator(tmp.FullName), Path.GetDirectoryName(result));
        }
        finally
        {
            tmp.Delete(true);
        }
    }

    [Fact]
    public void CombineWithRoot_RootWithTrailingSeparator_IsDirectChild()
    {
        var tmp = Directory.CreateTempSubdirectory("leafname-");
        try
        {
            var result = SafeDirectoryLeafName.CombineWithRoot(
                tmp.FullName + Path.DirectorySeparatorChar,
                "Name",
                "name");

            Assert.Equal(Path.Combine(tmp.FullName, "Name"), result);
        }
        finally
        {
            tmp.Delete(true);
        }
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../sibling")]
    [InlineData("/etc/passwd")]
    [InlineData("sub/child")]
    public void CombineWithRoot_CannotEscapeOrLandOnRoot(string name)
    {
        var tmp = Directory.CreateTempSubdirectory("leafname-");
        try
        {
            Assert.Throws<ArgumentException>(
                () => SafeDirectoryLeafName.CombineWithRoot(tmp.FullName, name, "name"));
        }
        finally
        {
            tmp.Delete(true);
        }
    }

    [Fact]
    public void CombineWithRoot_PrefixConfusion_DoesNotResolveToSibling()
    {
        var tmp = Directory.CreateTempSubdirectory("leafname-");
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(tmp.FullName, "playlists")).FullName;
            Directory.CreateDirectory(Path.Combine(tmp.FullName, "playlists-evil"));

            // The historical failure: an empty leaf collapsed the combined path onto the root itself,
            // after which a collision suffix produced a sibling of the root ("playlists" -> "playlists1").
            Assert.Throws<ArgumentException>(() => SafeDirectoryLeafName.CombineWithRoot(root, string.Empty, "name"));

            var result = SafeDirectoryLeafName.CombineWithRoot(root, "-evil", "name");
            Assert.Equal(Path.Combine(root, "-evil"), result);
        }
        finally
        {
            tmp.Delete(true);
        }
    }

    [Fact]
    public void CombineWithRoot_EmptyRoot_Throws()
        => Assert.Throws<ArgumentException>(() => SafeDirectoryLeafName.CombineWithRoot(" ", "name", "name"));
}
