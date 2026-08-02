using System;
using System.IO;
using Tesserafin.Common.IO;
using Xunit;

namespace Tesserafin.Common.Tests.IO;

/// <summary>
/// Pins the no-follow contract for server-managed roots. Every fixture is created inside a single
/// temporary subdirectory that the test owns and deletes; no path outside that subdirectory is read,
/// written or created.
/// </summary>
public sealed class ManagedPathBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _tmp;
    private readonly string _root;
    private readonly string _outside;

    public ManagedPathBoundaryTests()
    {
        _tmp = Directory.CreateTempSubdirectory("managed-path-boundary-");
        _root = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "root")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "outside")).FullName;
    }

    [Fact]
    public void TryResolveContainedFile_OrdinaryChild_IsAccepted()
    {
        var file = Path.Combine(_root, "backup.zip");
        File.WriteAllText(file, "x");

        Assert.True(ManagedPathBoundary.TryResolveContainedFile(_root, file, out var resolved));
        Assert.Equal(file, resolved);
    }

    [Fact]
    public void TryResolveContainedFile_OrdinaryNestedChild_IsAccepted()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "a", "b")).FullName;
        var file = Path.Combine(nested, "backup.zip");
        File.WriteAllText(file, "x");

        Assert.True(ManagedPathBoundary.TryResolveContainedFile(_root, file, out var resolved));
        Assert.Equal(file, resolved);
    }

    [Fact]
    public void TryResolveContainedFile_FinalComponentIsALink_IsRejected()
    {
        var target = Path.Combine(_outside, "secret.zip");
        File.WriteAllText(target, "x");
        var link = Path.Combine(_root, "link.zip");
        File.CreateSymbolicLink(link, target);

        Assert.False(ManagedPathBoundary.TryResolveContainedFile(_root, link, out _));
    }

    [Fact]
    public void TryResolveContainedFile_LinkPointingBackInsideTheRoot_IsRejected()
    {
        // The rule is "no link", not "no link that escapes". A link that currently resolves inside
        // the root can be repointed without the path changing.
        var real = Path.Combine(_root, "real.zip");
        File.WriteAllText(real, "x");
        var link = Path.Combine(_root, "inner-link.zip");
        File.CreateSymbolicLink(link, real);

        Assert.False(ManagedPathBoundary.TryResolveContainedFile(_root, link, out _));
    }

    [Fact]
    public void TryResolveContainedFile_ParentComponentIsALink_IsRejected()
    {
        // The final component of this path is an ordinary file and reports no link target at all;
        // only walking the components above it exposes the escape.
        var victim = Path.Combine(_outside, "victim.zip");
        File.WriteAllText(victim, "x");
        Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), _outside);

        var viaParent = Path.Combine(_root, "linked", "victim.zip");

        Assert.Null(new FileInfo(viaParent).LinkTarget);
        Assert.False(ManagedPathBoundary.TryResolveContainedFile(_root, viaParent, out _));
    }

    [Fact]
    public void TryResolveContainedFile_DanglingLink_IsRejected()
    {
        var dangling = Path.Combine(_root, "dangling.zip");
        File.CreateSymbolicLink(dangling, Path.Combine(_outside, "does-not-exist.zip"));

        Assert.False(ManagedPathBoundary.TryResolveContainedFile(_root, dangling, out _));
    }

    [Theory]
    [InlineData("../escape.zip")]
    [InlineData("a/../../escape.zip")]
    public void TryResolveContainedFile_TraversalOutOfTheRoot_IsRejected(string relative)
    {
        var escape = Path.Combine(_tmp.FullName, "escape.zip");
        File.WriteAllText(escape, "x");

        Assert.False(ManagedPathBoundary.TryResolveContainedFile(_root, Path.Combine(_root, relative), out _));
    }

    [Fact]
    public void TryResolveContainedFile_PrefixConfusionSibling_IsRejected()
    {
        var sibling = Directory.CreateDirectory(_root + "-evil").FullName;
        var file = Path.Combine(sibling, "escape.zip");
        File.WriteAllText(file, "x");

        Assert.False(ManagedPathBoundary.TryResolveContainedFile(_root, file, out _));
    }

    [Fact]
    public void TryResolveContainedFile_TheRootItself_IsRejected()
        => Assert.False(ManagedPathBoundary.TryResolveContainedFile(_root, _root, out _));

    [Fact]
    public void TryResolveContainedFile_ADirectory_IsRejected()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;

        Assert.False(ManagedPathBoundary.TryResolveContainedFile(_root, directory, out _));
    }

    [Fact]
    public void TryResolveContainedFile_MissingFile_IsRejected()
        => Assert.False(ManagedPathBoundary.TryResolveContainedFile(_root, Path.Combine(_root, "absent.zip"), out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolveContainedFile_EmptyCandidate_IsRejected(string? candidate)
        => Assert.False(ManagedPathBoundary.TryResolveContainedFile(_root, candidate, out _));

    [Fact]
    public void TryResolveContainedFile_LinkedRoot_IsTheTrustAnchorAndIsAccepted()
    {
        // An operator is free to mount a managed root through a link. That placement is a
        // deployment decision, so the root is never itself inspected.
        var linkedRoot = Path.Combine(_tmp.FullName, "linked-root");
        Directory.CreateSymbolicLink(linkedRoot, _root);
        var file = Path.Combine(_root, "backup.zip");
        File.WriteAllText(file, "x");

        Assert.True(ManagedPathBoundary.TryResolveContainedFile(linkedRoot, Path.Combine(linkedRoot, "backup.zip"), out _));
    }

    [Fact]
    public void TryPrepareWriteTarget_AbsentFile_IsAcceptedAndParentsAreCreated()
    {
        var target = Path.Combine(_root, "a", "b", "restored.txt");

        Assert.True(ManagedPathBoundary.TryPrepareWriteTarget(_root, target, out var resolved));
        Assert.Equal(target, resolved);
        Assert.True(Directory.Exists(Path.Combine(_root, "a", "b")));
    }

    [Fact]
    public void TryPrepareWriteTarget_ExistingOrdinaryFile_IsAccepted()
    {
        var target = Path.Combine(_root, "restored.txt");
        File.WriteAllText(target, "old");

        Assert.True(ManagedPathBoundary.TryPrepareWriteTarget(_root, target, out _));
    }

    [Fact]
    public void TryPrepareWriteTarget_ExistingLinkAtTheDestination_IsRejectedAndLeftIntact()
    {
        var victim = Path.Combine(_outside, "victim.txt");
        File.WriteAllText(victim, "original");
        var link = Path.Combine(_root, "link.txt");
        File.CreateSymbolicLink(link, victim);

        Assert.False(ManagedPathBoundary.TryPrepareWriteTarget(_root, link, out _));
        Assert.Equal(victim, new FileInfo(link).LinkTarget);
        Assert.Equal("original", File.ReadAllText(victim));
    }

    [Fact]
    public void TryPrepareWriteTarget_LinkedParentComponent_IsRejectedAndNothingIsCreated()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), _outside);
        var target = Path.Combine(_root, "linked", "planted.txt");

        Assert.False(ManagedPathBoundary.TryPrepareWriteTarget(_root, target, out _));
        Assert.False(File.Exists(Path.Combine(_outside, "planted.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(_outside));
    }

    [Fact]
    public void TryPrepareWriteTarget_TraversalOutOfTheRoot_IsRejected()
        => Assert.False(ManagedPathBoundary.TryPrepareWriteTarget(_root, Path.Combine(_root, "..", "escape.txt"), out _));

    [Fact]
    public void ValidateContainedFile_Rejection_DoesNotLeakThePath()
    {
        var link = Path.Combine(_root, "link.zip");
        File.CreateSymbolicLink(link, Path.Combine(_outside, "secret.zip"));

        var ex = Assert.Throws<ArgumentException>(
            () => ManagedPathBoundary.ValidateContainedFile(_root, link, "path"));

        Assert.DoesNotContain(_root, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_outside, ex.Message, StringComparison.Ordinal);
    }

    public void Dispose() => _tmp.Delete(true);
}
