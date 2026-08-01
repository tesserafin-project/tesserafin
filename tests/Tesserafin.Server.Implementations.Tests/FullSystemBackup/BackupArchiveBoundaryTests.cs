using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Server.Implementations.FullSystemBackup;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.FullSystemBackup;

/// <summary>
/// Pins the backup boundary on both sides: the archives the service is willing to select and read,
/// and the destinations it is willing to write.
/// </summary>
/// <remarks>
/// <para>
/// Restoring writes every archive entry into a server-managed root, and the archive is
/// attacker-shaped data — entry names come from whatever zip the administrator was handed. Entry
/// names alone are handled by canonical containment, including the prefix-confusion case where a
/// sibling directory shares a name prefix with the destination root.
/// </para>
/// <para>
/// Canonical containment is not sufficient on its own, because it is lexical: a link already present
/// on disk redirects a path that is spelled inside a managed root. Establishing such a link requires
/// write access to a server-owned root, which is authority equivalent to the server process or to
/// the host administrator of the mounted volume — no API caller can create one. The tests below pin
/// the resulting defence in depth rather than a remotely reachable boundary.
/// </para>
/// <para>
/// Every fixture is created inside one temporary subdirectory that the test owns and deletes. No
/// path outside that subdirectory is read, written or created.
/// </para>
/// </remarks>
public sealed class BackupArchiveBoundaryTests : IDisposable
{
    private const string ManifestJson = """
        {
          "ServerVersion": "1.0.0.0",
          "BackupEngineVersion": "0.2.0",
          "DateCreated": "2024-01-01T00:00:00+00:00",
          "DatabaseTables": [],
          "Options": { "Metadata": false, "Trickplay": false, "Subtitles": false, "Database": false }
        }
        """;

    private readonly DirectoryInfo _tmp;
    private readonly string _configRoot;
    private readonly string _dataRoot;
    private readonly string _backupRoot;
    private readonly string _outside;
    private readonly BackupService _sut;

    public BackupArchiveBoundaryTests()
    {
        _tmp = Directory.CreateTempSubdirectory("backup-boundary-");
        _configRoot = CreateRoot("Config");
        _dataRoot = CreateRoot("Data");
        _backupRoot = CreateRoot("backups");
        _outside = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "outside")).FullName;

        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.ConfigurationDirectoryPath).Returns(_configRoot);
        paths.SetupGet(p => p.DataPath).Returns(_dataRoot);
        paths.SetupGet(p => p.BackupPath).Returns(_backupRoot);
        paths.SetupGet(p => p.RootFolderPath).Returns(CreateRoot("Root"));
        paths.SetupGet(p => p.InternalMetadataPath).Returns(CreateRoot("metadata"));
        paths.SetupGet(p => p.DefaultInternalMetadataPath).Returns(CreateRoot("metadata-default"));
        paths.SetupGet(p => p.CachePath).Returns(CreateRoot("cache"));
        paths.SetupGet(p => p.LogDirectoryPath).Returns(CreateRoot("log"));
        paths.SetupGet(p => p.ProgramDataPath).Returns(_tmp.FullName);

        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(new Version(99, 0, 0, 0));

        var fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
        fixture.Inject(paths);
        fixture.Inject(host);
        _sut = fixture.Create<BackupService>();
    }

    [Fact]
    public async Task RestoreBackupAsync_HostileEntryNames_StayInsideTheDestinationRoot()
    {
        // A sibling of the destination root whose name shares its prefix. A bare string-prefix
        // containment test would accept a path that lands here.
        var prefixSibling = Directory.CreateDirectory(_configRoot + "-evil").FullName;
        var parent = Directory.GetParent(_configRoot)!.FullName;

        var archive = CreateArchive(
            ("Config/legitimate.txt", "restored"),
            ("Config/../escape-parent.txt", "escaped"),
            ("Config/../../escape-grandparent.txt", "escaped"),
            ("../escape-relative.txt", "escaped"),
            ("Config/../Config-evil/escape-prefix.txt", "escaped"),
            // An absolute entry name. It points inside the fixture rather than at a real host path,
            // so a regression is caught by the assertions below instead of writing to the host.
            (Path.Combine(_outside, "escape-absolute.txt"), "escaped"),
            ("Config/nested/../ok.txt", "restored"));

        await _sut.RestoreBackupAsync(archive);

        Assert.True(File.Exists(Path.Combine(_configRoot, "legitimate.txt")));
        Assert.Equal("restored", await File.ReadAllTextAsync(Path.Combine(_configRoot, "legitimate.txt"), TestContext.Current.CancellationToken));

        Assert.False(File.Exists(Path.Combine(parent, "escape-parent.txt")));
        Assert.False(File.Exists(Path.Combine(_tmp.FullName, "escape-parent.txt")));
        Assert.False(File.Exists(Path.Combine(_tmp.FullName, "escape-grandparent.txt")));
        Assert.False(File.Exists(Path.Combine(parent, "escape-relative.txt")));
        Assert.False(File.Exists(Path.Combine(prefixSibling, "escape-prefix.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(prefixSibling));
        Assert.Empty(Directory.GetFileSystemEntries(_outside));
    }

    [Fact]
    public async Task RestoreBackupAsync_LegitimateArchive_RestoresEveryRoot()
    {
        var archive = CreateArchive(
            ("Config/system.xml", "config"),
            ("Config/users/user.json", "user"),
            ("Data/playlists/list.xml", "playlist"));

        await _sut.RestoreBackupAsync(archive);

        Assert.Equal("config", await File.ReadAllTextAsync(Path.Combine(_configRoot, "system.xml"), TestContext.Current.CancellationToken));
        Assert.Equal("user", await File.ReadAllTextAsync(Path.Combine(_configRoot, "users", "user.json"), TestContext.Current.CancellationToken));
        Assert.Equal("playlist", await File.ReadAllTextAsync(Path.Combine(_dataRoot, "playlists", "list.xml"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestoreBackupAsync_LegitimateArchive_OverwritesAnExistingOrdinaryFile()
    {
        await File.WriteAllTextAsync(Path.Combine(_configRoot, "system.xml"), "stale", TestContext.Current.CancellationToken);

        await _sut.RestoreBackupAsync(CreateArchive(("Config/system.xml", "fresh")));

        Assert.Equal("fresh", await File.ReadAllTextAsync(Path.Combine(_configRoot, "system.xml"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestoreBackupAsync_LinkInsideTheDestinationRoot_IsNeitherFollowedNorOverwritten()
    {
        var victim = Path.Combine(_outside, "victim.txt");
        await File.WriteAllTextAsync(victim, "original", TestContext.Current.CancellationToken);

        var link = Path.Combine(_configRoot, "link.txt");
        File.CreateSymbolicLink(link, victim);

        await _sut.RestoreBackupAsync(CreateArchive(("Config/link.txt", "overwritten")));

        Assert.Equal("original", await File.ReadAllTextAsync(victim, TestContext.Current.CancellationToken));
        Assert.Equal(victim, new FileInfo(link).LinkTarget);
    }

    [Fact]
    public async Task RestoreBackupAsync_LinkedParentInsideTheDestinationRoot_IsNotFollowed()
    {
        Directory.CreateSymbolicLink(Path.Combine(_configRoot, "linked"), _outside);

        await _sut.RestoreBackupAsync(CreateArchive(("Config/linked/planted.txt", "planted")));

        Assert.False(File.Exists(Path.Combine(_outside, "planted.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(_outside));
    }

    [Fact]
    public async Task RestoreBackupAsync_DanglingLinkInsideTheDestinationRoot_IsNotMaterialised()
    {
        var absent = Path.Combine(_outside, "absent.txt");
        var link = Path.Combine(_configRoot, "dangling.txt");
        File.CreateSymbolicLink(link, absent);

        await _sut.RestoreBackupAsync(CreateArchive(("Config/dangling.txt", "planted")));

        Assert.False(File.Exists(absent));
        Assert.Empty(Directory.GetFileSystemEntries(_outside));
        Assert.Equal(absent, new FileInfo(link).LinkTarget);
    }

    [Fact]
    public async Task GetBackupManifest_LinkInsideTheBackupDirectory_IsNotRead()
    {
        // The archive-selection flow the alerts point at: a name that is a direct child of the
        // backup directory both lexically and canonically, but resolves outside it.
        var elsewhere = Path.Combine(_outside, "elsewhere.zip");
        WriteArchive(elsewhere);
        var link = Path.Combine(_backupRoot, "link.zip");
        File.CreateSymbolicLink(link, elsewhere);

        Assert.Null(await _sut.GetBackupManifest(link));
    }

    [Fact]
    public async Task GetBackupManifest_LinkedParentUnderTheBackupDirectory_IsNotRead()
    {
        var elsewhere = Path.Combine(_outside, "elsewhere.zip");
        WriteArchive(elsewhere);
        Directory.CreateSymbolicLink(Path.Combine(_backupRoot, "linked"), _outside);

        Assert.Null(await _sut.GetBackupManifest(Path.Combine(_backupRoot, "linked", "elsewhere.zip")));
    }

    [Fact]
    public async Task GetBackupManifest_PathOutsideTheBackupDirectory_IsNotRead()
    {
        var elsewhere = Path.Combine(_outside, "elsewhere.zip");
        WriteArchive(elsewhere);

        Assert.Null(await _sut.GetBackupManifest(elsewhere));
        Assert.Null(await _sut.GetBackupManifest(Path.Combine(_backupRoot, "..", "outside", "elsewhere.zip")));
    }

    [Fact]
    public async Task GetBackupManifest_OrdinaryArchiveInTheBackupDirectory_IsRead()
    {
        var archive = Path.Combine(_backupRoot, "backup.zip");
        WriteArchive(archive);

        var manifest = await _sut.GetBackupManifest(archive);

        Assert.NotNull(manifest);
        Assert.Equal(archive, manifest.Path);
    }

    [Fact]
    public async Task EnumerateBackups_SkipsALinkAndStillListsOrdinaryArchives()
    {
        var ordinary = Path.Combine(_backupRoot, "ordinary.zip");
        WriteArchive(ordinary);

        var elsewhere = Path.Combine(_outside, "elsewhere.zip");
        WriteArchive(elsewhere);
        File.CreateSymbolicLink(Path.Combine(_backupRoot, "link.zip"), elsewhere);

        var manifests = await _sut.EnumerateBackups();

        Assert.Single(manifests);
        Assert.Equal(ordinary, manifests[0].Path);
    }

    private string CreateArchive(params (string EntryName, string Content)[] entries)
    {
        var archivePath = Path.Combine(
            _tmp.FullName,
            string.Create(CultureInfo.InvariantCulture, $"backup-{entries.Length}-{entries[0].EntryName.GetHashCode(StringComparison.Ordinal)}.zip"));

        using var stream = new FileStream(archivePath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        Write(zip, "manifest.json", ManifestJson);
        foreach (var (entryName, content) in entries)
        {
            Write(zip, entryName, content);
        }

        return archivePath;

        static void Write(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name);
            using var entryStream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            entryStream.Write(bytes, 0, bytes.Length);
        }
    }

    private static void WriteArchive(string archivePath)
    {
        using var stream = new FileStream(archivePath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("manifest.json");
        using var entryStream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(ManifestJson);
        entryStream.Write(bytes, 0, bytes.Length);
    }

    private string CreateRoot(string name)
        => Directory.CreateDirectory(Path.Combine(_tmp.FullName, "roots", name)).FullName;

    public void Dispose() => _tmp.Delete(true);
}
