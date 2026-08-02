using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Tesserafin.Api.Controllers;
using Tesserafin.Common.Api;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.SystemBackupService;
using Tesserafin.Server.Implementations.SystemBackupService;
using Xunit;

namespace Tesserafin.Api.Tests.Controllers;

/// <summary>
/// Pins who may reach backup selection, and which archives selection is willing to name.
/// </summary>
/// <remarks>
/// Every fixture is created inside one temporary subdirectory that the test owns and deletes. No
/// path outside that subdirectory is read, written or created.
/// </remarks>
public sealed class BackupControllerTests : IDisposable
{
    private readonly DirectoryInfo _tmp;
    private readonly string _backupRoot;
    private readonly string _outside;
    private readonly Mock<IBackupService> _backupService;
    private readonly BackupController _sut;

    public BackupControllerTests()
    {
        _tmp = Directory.CreateTempSubdirectory("backup-controller-");
        _backupRoot = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "backups")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "outside")).FullName;

        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.BackupPath).Returns(_backupRoot);

        _backupService = new Mock<IBackupService>();
        _sut = new BackupController(_backupService.Object, paths.Object);
    }

    [Fact]
    public void Controller_RequiresAnElevatedAdministrator()
    {
        // The HTTP-level negative control for every principal below an elevated administrator:
        // there is no anonymous, first-time-setup or ordinary-user route into these actions.
        var authorize = typeof(BackupController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ToArray();

        Assert.Single(authorize);
        Assert.Equal(Policies.RequiresElevation, authorize[0].Policy);

        var actions = typeof(BackupController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName);

        foreach (var action in actions)
        {
            Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            Assert.Empty(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        }
    }

    [Fact]
    public async Task GetBackup_OrdinaryArchiveInTheBackupDirectory_IsSelected()
    {
        var archive = Path.Combine(_backupRoot, "backup.zip");
        await File.WriteAllTextAsync(archive, "x", TestContext.Current.CancellationToken);
        _backupService
            .Setup(s => s.GetBackupManifest(archive))
            .ReturnsAsync(new BackupManifestDto
            {
                Path = archive,
                ServerVersion = new Version(1, 0, 0, 0),
                BackupEngineVersion = new Version(0, 2, 0),
                DateCreated = DateTime.UnixEpoch,
                Options = new BackupOptionsDto()
            });

        var result = await _sut.GetBackup("backup.zip");

        Assert.IsNotType<NotFoundResult>(result.Result);
        _backupService.Verify(s => s.GetBackupManifest(archive), Times.Once);
    }

    [Fact]
    public async Task GetBackup_LinkInsideTheBackupDirectory_IsNotSelected()
    {
        var elsewhere = Path.Combine(_outside, "elsewhere.zip");
        await File.WriteAllTextAsync(elsewhere, "x", TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(Path.Combine(_backupRoot, "link.zip"), elsewhere);

        var result = await _sut.GetBackup("link.zip");

        Assert.IsType<NotFoundResult>(result.Result);
        _backupService.Verify(s => s.GetBackupManifest(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetBackup_DanglingLinkInsideTheBackupDirectory_IsNotSelected()
    {
        File.CreateSymbolicLink(Path.Combine(_backupRoot, "dangling.zip"), Path.Combine(_outside, "absent.zip"));

        var result = await _sut.GetBackup("dangling.zip");

        Assert.IsType<NotFoundResult>(result.Result);
        _backupService.Verify(s => s.GetBackupManifest(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("../outside/elsewhere.zip")]
    [InlineData("/etc/passwd")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    public async Task GetBackup_PathsThatDoNotNameAnArchiveInTheBackupDirectory_AreNotSelected(string path)
    {
        var result = await _sut.GetBackup(path);

        Assert.IsType<NotFoundResult>(result.Result);
        _backupService.Verify(s => s.GetBackupManifest(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void StartRestoreBackup_LinkInsideTheBackupDirectory_IsNotScheduled()
    {
        var elsewhere = Path.Combine(_outside, "elsewhere.zip");
        File.WriteAllText(elsewhere, "x");
        File.CreateSymbolicLink(Path.Combine(_backupRoot, "link.zip"), elsewhere);

        var result = _sut.StartRestoreBackup(new BackupRestoreRequestDto { ArchiveFileName = "link.zip" });

        Assert.IsType<NotFoundResult>(result);
        _backupService.Verify(s => s.ScheduleRestoreAndRestartServer(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void StartRestoreBackup_OrdinaryArchiveInTheBackupDirectory_IsScheduled()
    {
        var archive = Path.Combine(_backupRoot, "backup.zip");
        File.WriteAllText(archive, "x");

        var result = _sut.StartRestoreBackup(new BackupRestoreRequestDto { ArchiveFileName = "backup.zip" });

        Assert.IsType<NoContentResult>(result);
        _backupService.Verify(s => s.ScheduleRestoreAndRestartServer(archive), Times.Once);
    }

    public void Dispose() => _tmp.Delete(true);
}
