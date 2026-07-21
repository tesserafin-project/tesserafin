using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Enums;
using Tesserafin.Model.IO;
using Tesserafin.Naming.Common;
using Tesserafin.Naming.Video;
using Tesserafin.Server.Core.Library.Resolvers.Movies;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public class MovieResolverTests
{
    private static readonly NamingOptions _namingOptions = new();
    private static readonly VideoListResolver _videoListResolver = new(_namingOptions);

    [Fact]
    public void Resolve_GivenLocalAlternateVersion_ResolvesToVideo()
    {
        var movieResolver = new MovieResolver(Mock.Of<IImageProcessor>(), Mock.Of<ILogger<MovieResolver>>(), _namingOptions, Mock.Of<IDirectoryService>(), _videoListResolver);
        var itemResolveArgs = new ItemResolveArgs(
            Mock.Of<IServerApplicationPaths>(),
            null)
        {
            Parent = null,
            FileInfo = new FileSystemMetadata
            {
                FullName = "/movies/Black Panther (2018)/Black Panther (2018) - 1080p 3D.mk3d"
            }
        };

        Assert.NotNull(movieResolver.Resolve(itemResolveArgs));
    }

    [Fact]
    public void ResolveMultiple_GivenTvShowsCollection_CreatesEpisodeItems()
    {
        // For a tvshows collection, the multi-version grouping must still produce
        // Episode BaseItems (not generic Video) so downstream metadata fetching
        // and series-aware logic apply.
        var movieResolver = new MovieResolver(Mock.Of<IImageProcessor>(), Mock.Of<ILogger<MovieResolver>>(), _namingOptions, Mock.Of<IDirectoryService>(), _videoListResolver);

        var parent = new Folder { Path = "/TV/Show/Season 1" };
        var files = new List<FileSystemMetadata>
        {
            new() { FullName = "/TV/Show/Season 1/Show - S01E01 - 1080p.mkv", Name = "Show - S01E01 - 1080p.mkv", IsDirectory = false },
            new() { FullName = "/TV/Show/Season 1/Show - S01E01 - 720p.mkv", Name = "Show - S01E01 - 720p.mkv", IsDirectory = false },
            new() { FullName = "/TV/Show/Season 1/Show - S01E02.mkv", Name = "Show - S01E02.mkv", IsDirectory = false }
        };

        var result = movieResolver.ResolveMultiple(parent, files, CollectionType.tvshows, Mock.Of<IDirectoryService>());

        Assert.NotNull(result);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item => Assert.IsType<Episode>(item));

        // The S01E01 item should have one alternate version
        var s01e01 = result.Items.Find(i => i.Path.Contains("S01E01", System.StringComparison.Ordinal));
        Assert.NotNull(s01e01);
        Assert.Single(((Video)s01e01).LocalAlternateVersions);
    }

    [Fact]
    public void ResolveMultiple_GivenMoviesCollection_CreatesMovieItems()
    {
        // For a movies collection, the multi-version grouping must produce Movie
        // BaseItems (not generic Video) so downstream movie-specific logic applies.
        var movieResolver = new MovieResolver(Mock.Of<IImageProcessor>(), Mock.Of<ILogger<MovieResolver>>(), _namingOptions, Mock.Of<IDirectoryService>(), _videoListResolver);

        var parent = new Folder { Path = "/movies/Inception (2010)" };
        var files = new List<FileSystemMetadata>
        {
            new() { FullName = "/movies/Inception (2010)/Inception (2010) - 1080p.mkv", Name = "Inception (2010) - 1080p.mkv", IsDirectory = false },
            new() { FullName = "/movies/Inception (2010)/Inception (2010) - 720p.mkv", Name = "Inception (2010) - 720p.mkv", IsDirectory = false }
        };

        var result = movieResolver.ResolveMultiple(parent, files, CollectionType.movies, Mock.Of<IDirectoryService>());

        Assert.NotNull(result);
        Assert.Single(result.Items);
        Assert.All(result.Items, item => Assert.IsType<Movie>(item));
        Assert.Single(((Video)result.Items[0]).LocalAlternateVersions);
    }
}
