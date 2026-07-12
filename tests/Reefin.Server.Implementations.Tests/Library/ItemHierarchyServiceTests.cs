using System;
using System.Linq;
using Moq;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Library;
using Reefin.Server.Core.Library;
using Xunit;

namespace Reefin.Server.Implementations.Tests.Library;

/// <summary>
/// Tests for <see cref="ItemHierarchyService"/>. The critical technique across these tests is
/// setting <see cref="BaseItem.LibraryManager"/> to a <see cref="MockBehavior.Strict"/> mock that
/// throws if any member is touched: since <see cref="ItemHierarchyService"/> only ever calls the
/// lookup-aware <c>BaseItem</c> overloads, the static must never be touched, and a strict mock
/// proves that empirically rather than by inspection.
/// </summary>
[Collection(Reefin.Server.Implementations.Tests.Library.LibraryManager.LibraryManagerStaticStateFixture.Name)]
public class ItemHierarchyServiceTests
{
    private static void SetStrictStaticLibraryManager()
    {
        BaseItem.LibraryManager = new Mock<ILibraryManager>(MockBehavior.Strict).Object;
    }

    [Fact]
    public void GetAncestors_MultiHopChain_ResolvesThroughLookupNotStatic()
    {
        SetStrictStaticLibraryManager();

        var p2 = new UserRootFolder { Id = Guid.NewGuid(), Name = "root", ParentId = Guid.Empty };
        var p1 = new Folder { Id = Guid.NewGuid(), Name = "mid", ParentId = p2.Id };
        var item = new Audio { Id = Guid.NewGuid(), Name = "leaf", ParentId = p1.Id };

        var lookup = new Mock<IItemLookupService>();
        lookup.Setup(l => l.GetItemById(p1.Id)).Returns(p1);
        lookup.Setup(l => l.GetItemById(p2.Id)).Returns(p2);

        var service = new ItemHierarchyService(lookup.Object);

        var ancestors = service.GetAncestors(item).ToList();

        Assert.Equal(2, ancestors.Count);
        Assert.Same(p1, ancestors[0]);
        Assert.Same(p2, ancestors[1]);
    }

    [Fact]
    public void FindAncestor_ReturnsNearestAncestorOfType()
    {
        SetStrictStaticLibraryManager();

        var p2 = new UserRootFolder { Id = Guid.NewGuid(), Name = "root", ParentId = Guid.Empty };
        var p1 = new Folder { Id = Guid.NewGuid(), Name = "mid", ParentId = p2.Id };
        var item = new Audio { Id = Guid.NewGuid(), Name = "leaf", ParentId = p1.Id };

        var lookup = new Mock<IItemLookupService>();
        lookup.Setup(l => l.GetItemById(p1.Id)).Returns(p1);
        lookup.Setup(l => l.GetItemById(p2.Id)).Returns(p2);

        var service = new ItemHierarchyService(lookup.Object);

        Assert.Same(p2, service.FindAncestor<UserRootFolder>(item));
        Assert.Same(p1, service.FindAncestor<Folder>(item));
    }

    [Fact]
    public void GetParent_ResolvesImmediateParentThroughLookup()
    {
        SetStrictStaticLibraryManager();

        var p2 = new UserRootFolder { Id = Guid.NewGuid(), Name = "root", ParentId = Guid.Empty };
        var p1 = new Folder { Id = Guid.NewGuid(), Name = "mid", ParentId = p2.Id };
        var item = new Audio { Id = Guid.NewGuid(), Name = "leaf", ParentId = p1.Id };

        var lookup = new Mock<IItemLookupService>();
        lookup.Setup(l => l.GetItemById(p1.Id)).Returns(p1);
        lookup.Setup(l => l.GetItemById(p2.Id)).Returns(p2);

        var service = new ItemHierarchyService(lookup.Object);

        Assert.Same(p1, service.GetParent(item));
    }

    [Fact]
    public void GetParent_EmptyParentId_ReturnsNull()
    {
        SetStrictStaticLibraryManager();

        var item = new Audio { Id = Guid.NewGuid(), Name = "leaf", ParentId = Guid.Empty };

        var lookup = new Mock<IItemLookupService>();

        var service = new ItemHierarchyService(lookup.Object);

        Assert.Null(service.GetParent(item));
        lookup.Verify(l => l.GetItemById(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void GetOwner_ResolvesOwnerThroughLookup()
    {
        SetStrictStaticLibraryManager();

        var owner = new Folder { Id = Guid.NewGuid(), Name = "owner" };
        var owned = new Audio { Id = Guid.NewGuid(), Name = "owned", OwnerId = owner.Id };

        var lookup = new Mock<IItemLookupService>();
        lookup.Setup(l => l.GetItemById(owner.Id)).Returns(owner);

        var service = new ItemHierarchyService(lookup.Object);

        Assert.Same(owner, service.GetOwner(owned));

        var unowned = new Audio { Id = Guid.NewGuid(), Name = "unowned", OwnerId = Guid.Empty };
        Assert.Null(service.GetOwner(unowned));
    }

    [Fact]
    public void GetParent_NullItem_Throws()
    {
        SetStrictStaticLibraryManager();

        var lookup = new Mock<IItemLookupService>();
        var service = new ItemHierarchyService(lookup.Object);

        Assert.Throws<ArgumentNullException>(() => service.GetParent(null!));
    }
}
