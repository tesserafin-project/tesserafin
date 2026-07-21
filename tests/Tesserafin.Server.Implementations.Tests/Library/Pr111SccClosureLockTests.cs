using System;
using System.Linq;
using Tesserafin.Controller.Collections;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.TV;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

/// <summary>
/// PR111 closure lock: the query/user-views/channel/live-tv SCC (RFC
/// <c>docs/rfc-di-query-user-views-v2.md</c> §7) was broken at construction by PR110 by removing two
/// specific things - <c>LibraryManager</c>'s <c>Lazy&lt;IUserViewManager&gt;</c> field/ctor
/// parameter, and three dead ctor parameters on <c>UserViewManager</c>
/// (<c>ILiveTvManager</c>/<c>ICollectionManager</c>/<c>ITVSeriesManager</c>, unused after
/// <c>GetUserViews</c> started delegating to <see cref="IUserViewCatalog"/>). Neither removal had a
/// dedicated regression test before PR111 - the existing <c>DiWiring_...</c> reflection tests all
/// target the six new PR106-108 leaves (<c>UserViewFactory</c>, <c>ItemStore</c>,
/// <c>UserRootFolderProvider</c>, <c>ChannelCatalog</c>, <c>LiveTvPresenceProvider</c>,
/// <c>UserViewCatalog</c>), not <c>LibraryManager</c>/<c>UserViewManager</c> themselves. This file
/// closes that gap with two narrow, reflection-only assertions - no instantiation, no mocking,
/// nothing that would break if either class's other dependencies change shape.
/// </summary>
/// <remarks>
/// Deliberately narrow: <c>UserViewManager</c> still legitimately depends on <c>ILibraryManager</c>
/// and <c>IChannelManager</c> (the residual one-way edges documented in PR110's report -
/// <c>GetItemsForLatestItems</c>/<c>GetLatestChannelItemsInternal</c> - and in
/// <c>docs/pr111-di-closure-audit.md</c> §8). Forbidding those here would fail on correct code; only
/// the three dead types PR110 actually removed are asserted absent.
/// </remarks>
public class Pr111SccClosureLockTests
{
    [Fact]
    public void DiWiring_LibraryManagerConstructorGraph_NoUserViewManagerEdgeDirectOrLazy()
    {
        var ctor = typeof(Tesserafin.Server.Core.Library.LibraryManager).GetConstructors().Single();
        var parameterTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();

        Assert.DoesNotContain(typeof(IUserViewManager), parameterTypes);
        Assert.DoesNotContain(typeof(Lazy<IUserViewManager>), parameterTypes);
    }

    [Fact]
    public void DiWiring_UserViewManagerConstructorGraph_NoDeadPr110Dependencies()
    {
        var ctor = typeof(UserViewManager).GetConstructors().Single();
        var parameterTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();

        // Dead as of PR110: GetUserViews stopped duplicating its own grouping/listing logic and
        // now delegates entirely to IUserViewCatalog, which needed none of these three.
        Assert.DoesNotContain(typeof(ILiveTvManager), parameterTypes);
        Assert.DoesNotContain(typeof(ICollectionManager), parameterTypes);
        Assert.DoesNotContain(typeof(ITVSeriesManager), parameterTypes);
    }
}
