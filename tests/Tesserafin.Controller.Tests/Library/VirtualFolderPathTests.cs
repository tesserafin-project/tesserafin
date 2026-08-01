using System;
using System.IO;
using Tesserafin.Controller.Library;
using Xunit;

namespace Tesserafin.Controller.Tests.Library
{
    /// <summary>
    /// Filesystem trust-boundary controls for <see cref="VirtualFolderPath"/>.
    /// </summary>
    /// <remarks>
    /// A virtual folder name is a name, never a location. Every case below that is expected to be
    /// refused would, with a bare <c>Path.Combine</c>, have selected a filesystem location outside
    /// the server-controlled root.
    /// </remarks>
    public sealed class VirtualFolderPathTests : IDisposable
    {
        private readonly string _sandbox;
        private readonly string _root;

        public VirtualFolderPathTests()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), "vfp-" + Guid.NewGuid().ToString("N"));
            _root = Path.Combine(_sandbox, "userviews");
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            if (Directory.Exists(_sandbox))
            {
                Directory.Delete(_sandbox, true);
            }
        }

        [Theory]
        [InlineData("Movies")]
        [InlineData("TV Shows")]
        [InlineData("Musique française")]
        [InlineData("動画")]
        [InlineData("Kids' stuff")]
        [InlineData("a.b.c")]
        public void TryResolve_ValidName_ResolvesToDirectChildOfRoot(string name)
        {
            Assert.True(VirtualFolderPath.TryResolve(_root, name, out var resolved));
            Assert.Equal(Path.Combine(_root, name), resolved);
            Assert.Equal(_root, Path.GetDirectoryName(resolved));
        }

        [Theory]
        // relative traversal
        [InlineData("..")]
        [InlineData("../evil")]
        [InlineData("../../evil")]
        [InlineData("a/../../evil")]
        // rooted input
        [InlineData("/etc")]
        [InlineData("/tmp/evil")]
        [InlineData("//evil")]
        // separator variants
        [InlineData("a/b")]
        [InlineData("a\\b")]
        [InlineData("..\\evil")]
        [InlineData("\\evil")]
        // dot-only names
        [InlineData(".")]
        [InlineData("...")]
        // empty / whitespace
        [InlineData("")]
        [InlineData("   ")]
        public void TryResolve_HostileName_IsRefused(string name)
        {
            Assert.False(VirtualFolderPath.TryResolve(_root, name, out var resolved));
            Assert.Null(resolved);
        }

        [Fact]
        public void TryResolve_NullName_IsRefused()
        {
            Assert.False(VirtualFolderPath.TryResolve(_root, null, out var resolved));
            Assert.Null(resolved);
        }

        [Fact]
        public void TryResolve_SiblingPrefixCollision_IsRefused()
        {
            // A sibling of the root whose name merely starts with the root's name must not be
            // reachable: a naive StartsWith containment check would accept this.
            var sibling = _root + "-evil";
            Directory.CreateDirectory(sibling);

            Assert.False(VirtualFolderPath.TryResolve(_root, "../" + Path.GetFileName(sibling), out _));
        }

        [Fact]
        public void TryResolve_NameLandingOnDirectorySymlink_IsRefused()
        {
            var outside = Path.Combine(_sandbox, "outside");
            Directory.CreateDirectory(outside);
            var link = Path.Combine(_root, "escape");
            Directory.CreateSymbolicLink(link, outside);

            // Purely lexical resolution cannot see through this: the link is a direct child of the
            // root by name, and only an explicit link check refuses it.
            Assert.Equal(_root, Path.GetDirectoryName(Path.GetFullPath("escape", _root)));
            Assert.False(VirtualFolderPath.TryResolve(_root, "escape", out _));
        }

        [Fact]
        public void TryResolve_NameLandingOnFileSymlink_IsRefused()
        {
            var outside = Path.Combine(_sandbox, "outside");
            Directory.CreateDirectory(outside);
            var target = Path.Combine(outside, "target.xml");
            File.WriteAllText(target, "<x/>");
            File.CreateSymbolicLink(Path.Combine(_root, "escape-file"), target);

            Assert.False(VirtualFolderPath.TryResolve(_root, "escape-file", out _));
        }

        [Fact]
        public void TryResolve_NameLandingOnDanglingSymlink_IsRefused()
        {
            // A dangling link still creates through to its target when a directory is created,
            // so "the entry does not exist" is not a safe reason to accept it.
            File.CreateSymbolicLink(
                Path.Combine(_root, "escape-dangling"),
                Path.Combine(_sandbox, "outside", "nonexistent"));

            Assert.False(VirtualFolderPath.TryResolve(_root, "escape-dangling", out _));
        }

        [Fact]
        public void TryResolve_ExistingRealDirectory_RemainsUsable()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Existing Library"));

            Assert.True(VirtualFolderPath.TryResolve(_root, "Existing Library", out var resolved));
            Assert.Equal(Path.Combine(_root, "Existing Library"), resolved);
        }

        [Fact]
        public void Resolve_HostileName_ThrowsWithoutRevealingHostPath()
        {
            var ex = Assert.Throws<ArgumentException>(() => VirtualFolderPath.Resolve(_root, "../evil"));

            Assert.StartsWith(VirtualFolderPath.InvalidNameMessage, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_sandbox, ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Resolve_ValidName_ReturnsPath()
        {
            Assert.Equal(Path.Combine(_root, "Movies"), VirtualFolderPath.Resolve(_root, "Movies"));
        }

        [Fact]
        public void TryResolve_EmptyRoot_IsRefused()
        {
            Assert.False(VirtualFolderPath.TryResolve(string.Empty, "Movies", out _));
        }
    }
}
