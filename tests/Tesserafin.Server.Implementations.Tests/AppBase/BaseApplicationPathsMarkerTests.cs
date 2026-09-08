using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Model.Configuration;
using Tesserafin.Server.Core;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.AppBase;

/// <summary>
/// Covers the W2-A6 directory sanity marker rename: every first-party marker is written as
/// <c>.tesserafin-*</c>, a pre-rename <c>.reefin-*</c> tree is migrated in place, and the new marker
/// is always on disk before the old one is removed.
/// </summary>
/// <remarks>
/// The marker spellings are written here as literals on purpose. Asserting against the production
/// constants would make every one of these tests pass under a mutated prefix, which is exactly the
/// regression they exist to catch.
/// </remarks>
public sealed class BaseApplicationPathsMarkerTests : IDisposable
{
    private const string NewPrefix = ".tesserafin-";
    private const string LegacyPrefix = ".reefin-";

    private readonly string _root;

    public BaseApplicationPathsMarkerTests()
    {
        _root = Directory.CreateTempSubdirectory("tesserafin-w2a6-").FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public void MakeSanityCheckOrThrow_FreshTree_WritesOnlyNewMarkers()
    {
        var paths = CreatePaths();

        paths.MakeSanityCheckOrThrow();

        Assert.Empty(LegacyMarkersUnder(_root));

        Assert.Equal(
            new[]
            {
                ".tesserafin-cache",
                ".tesserafin-config",
                ".tesserafin-data",
                ".tesserafin-data",
                ".tesserafin-log",
                ".tesserafin-plugin",
                ".tesserafin-root"
            },
            MarkersUnder(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void GetTranscodePath_FreshTree_WritesOnlyNewMarker()
    {
        var transcodePath = Path.Combine(_root, "transcodes");

        BuildConfigurationManager(transcodePath).GetTranscodePath();

        Assert.Equal(
            new[] { ".tesserafin-transcode" },
            MarkersUnder(transcodePath).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void GetTranscodePath_LegacyMarker_IsMigrated()
    {
        var transcodePath = Directory.CreateDirectory(Path.Combine(_root, "transcodes")).FullName;
        SeedMarker(transcodePath, ".reefin-transcode");

        BuildConfigurationManager(transcodePath).GetTranscodePath();

        Assert.True(File.Exists(Path.Combine(transcodePath, ".tesserafin-transcode")));
        Assert.False(File.Exists(Path.Combine(transcodePath, ".reefin-transcode")));
    }

    [Fact]
    public void MakeSanityCheckOrThrow_LegacyTree_MigratesEveryRootIndependently()
    {
        var paths = CreatePaths();

        // Deliberately mixed: two roots pre-rename, one already migrated, the rest absent. Each root is
        // decided on its own contents, so a partially migrated installation must converge in one pass.
        SeedMarker(paths.ConfigurationDirectoryPath, ".reefin-config");
        SeedMarker(paths.LogDirectoryPath, ".reefin-log");
        SeedMarker(paths.CachePath, ".tesserafin-cache");

        paths.MakeSanityCheckOrThrow();

        Assert.Empty(LegacyMarkersUnder(_root));
        Assert.True(File.Exists(Path.Combine(paths.ConfigurationDirectoryPath, ".tesserafin-config")));
        Assert.True(File.Exists(Path.Combine(paths.LogDirectoryPath, ".tesserafin-log")));
        Assert.True(File.Exists(Path.Combine(paths.PluginsPath, ".tesserafin-plugin")));
        Assert.True(File.Exists(Path.Combine(paths.ProgramDataPath, ".tesserafin-data")));
        Assert.True(File.Exists(Path.Combine(paths.CachePath, ".tesserafin-cache")));
        Assert.True(File.Exists(Path.Combine(paths.DataPath, ".tesserafin-data")));
        Assert.True(File.Exists(Path.Combine(paths.RootFolderPath, ".tesserafin-root")));
    }

    [Fact]
    public void CreateAndCheckMarker_LegacyMarker_CreatesTheNewFileRatherThanOnlyRemovingTheOld()
    {
        var paths = CreatePaths();
        var directory = Directory.CreateDirectory(Path.Combine(_root, "state")).FullName;
        SeedMarker(directory, ".reefin-config");

        paths.CreateAndCheckMarker(directory, "config");

        var migrated = Path.Combine(directory, ".tesserafin-config");
        Assert.True(File.Exists(migrated), $"{migrated} was not created");
        Assert.False(File.Exists(Path.Combine(directory, ".reefin-config")));
        Assert.Equal(new[] { ".tesserafin-config" }, MarkersUnder(directory).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void CreateAndCheckMarker_NewMarkerCannotBeWritten_LeavesTheLegacyMarkerInPlace()
    {
        var paths = CreatePaths();
        var directory = Directory.CreateDirectory(Path.Combine(_root, "state")).FullName;
        SeedMarker(directory, ".reefin-config");

        // A directory sitting on the new marker's path makes the write fail. The pre-rename marker must
        // survive that: removing it before the replacement exists would leave the state root unmarked,
        // which is indistinguishable from a fresh one.
        Directory.CreateDirectory(Path.Combine(directory, ".tesserafin-config"));

        Assert.ThrowsAny<Exception>(() => paths.CreateAndCheckMarker(directory, "config"));

        Assert.True(
            File.Exists(Path.Combine(directory, ".reefin-config")),
            "the pre-rename marker was removed even though the new one was never written");
    }

    [Fact]
    public void CreateAndCheckMarker_CaseVariantNewMarker_DoesNotWriteASecondFile()
    {
        var paths = CreatePaths();
        var directory = Directory.CreateDirectory(Path.Combine(_root, "state")).FullName;
        SeedMarker(directory, ".TESSERAFIN-config");

        paths.CreateAndCheckMarker(directory, "config");

        Assert.Equal(new[] { ".TESSERAFIN-config" }, MarkersUnder(directory).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void CreateAndCheckMarker_CaseVariantLegacyMarker_IsMigrated()
    {
        var paths = CreatePaths();
        var directory = Directory.CreateDirectory(Path.Combine(_root, "state")).FullName;
        SeedMarker(directory, ".REEFIN-config");

        paths.CreateAndCheckMarker(directory, "config");

        Assert.Equal(new[] { ".tesserafin-config" }, MarkersUnder(directory).Select(Path.GetFileName).ToArray());
    }

    [Theory]
    [InlineData(".tesserafin-log")]
    [InlineData(".reefin-log")]
    public void CreateAndCheckMarker_ForeignMarker_Throws(string foreignMarker)
    {
        var paths = CreatePaths();
        var directory = Directory.CreateDirectory(Path.Combine(_root, "state")).FullName;
        SeedMarker(directory, foreignMarker);

        Assert.Throws<InvalidOperationException>(() => paths.CreateAndCheckMarker(directory, "config"));
    }

    [Fact]
    public void CreateAndCheckMarker_Recursive_MigratesNestedLegacyMarkers()
    {
        var paths = CreatePaths();
        var directory = Directory.CreateDirectory(Path.Combine(_root, "transcodes")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(directory, "nested")).FullName;
        SeedMarker(nested, ".reefin-transcode");

        paths.CreateAndCheckMarker(directory, "transcode", true);

        Assert.True(File.Exists(Path.Combine(directory, ".tesserafin-transcode")));
        Assert.Empty(LegacyMarkersUnder(directory));
    }

    private static void SeedMarker(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, fileName), Array.Empty<byte>());
    }

    private static IEnumerable<string> MarkersUnder(string path)
        => AllFilesUnder(path).Where(f => Path.GetFileName(f).StartsWith(NewPrefix, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> LegacyMarkersUnder(string path)
        => AllFilesUnder(path).Where(f => Path.GetFileName(f).StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> AllFilesUnder(string path)
        => Directory.Exists(path)
            ? Directory.EnumerateFiles(
                path,
                "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.None,
                    IgnoreInaccessible = false
                })
            : Array.Empty<string>();

    private ServerApplicationPaths CreatePaths()
        => new ServerApplicationPaths(
            Path.Combine(_root, "data"),
            Path.Combine(_root, "log"),
            Path.Combine(_root, "config"),
            Path.Combine(_root, "cache"),
            Path.Combine(_root, "web"));

    private IConfigurationManager BuildConfigurationManager(string transcodePath)
    {
        var manager = new Mock<IConfigurationManager>(MockBehavior.Strict);
        manager.SetupGet(m => m.CommonApplicationPaths).Returns(CreatePaths());
        manager.Setup(m => m.GetConfiguration("encoding"))
            .Returns(new EncodingOptions { TranscodingTempPath = transcodePath });

        return manager.Object;
    }
}
