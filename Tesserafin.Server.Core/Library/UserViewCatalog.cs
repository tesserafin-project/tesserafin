#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data;
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations.Entities;
using Tesserafin.Database.Implementations.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Channels;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.Library;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Sole production implementation of <see cref="IUserViewCatalog"/> - <c>GetUserViews</c>,
    /// ported off <c>UserViewManager.cs:54-175</c> with behavior preserved exactly, built entirely on
    /// the PR106-108 leaves (RFC <c>docs/rfc-di-query-user-views-v2.md</c> §9, PR109).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Depends on <see cref="IUserRootFolderProvider"/> (PR107, root folder + children),
    /// <see cref="IUserViewFactory"/> (PR106b, named/shadow view creation), <see cref="IChannelCatalog"/>
    /// and <see cref="ILiveTvPresenceProvider"/> (PR108, external catalogs), <see cref="IItemSortService"/>
    /// (already a leaf, RFC §6), <see cref="IServerConfigurationManager"/> (<c>EnableFolderView</c>),
    /// <see cref="ILocalizationManager"/> (view labels) and <c>Lazy&lt;IItemQueryService&gt;</c> (the
    /// playlist/boxset probe, RFC §3). None of the first six reference <c>ILibraryManager</c>,
    /// <c>IUserViewManager</c>, <c>IChannelManager</c> or <c>ILiveTvManager</c> - this satisfies RFC
    /// invariant I1 (eager construction graph) for this leaf, verified by a dedicated architectural
    /// test (<c>UserViewCatalogTests.DiWiring_...</c>).
    /// </para>
    /// <para>
    /// <b>PR109 finding not anticipated by the RFC's §8 dependency table - <c>IItemQueryService</c>
    /// must be <c>Lazy</c>, not direct</b>: the RFC's own table lists a plain <c>IItemQueryService</c>
    /// dependency for this port and asserts "Non" (no SCC edge) without tracing into its concrete
    /// implementation's own constructor. <c>ItemQueryService</c> (the sole implementation
    /// <c>ApplicationHost</c> wires up) takes <b>both</b> <c>IUserViewManager</c> <b>and</b>
    /// <c>IChannelManager</c> directly, non-Lazy, in its own constructor
    /// (<c>Tesserafin.Server.Core/Library/ItemQueryService.cs:38</c>) - it needs them for its own
    /// <c>Folder.GetItems</c>/<c>GetItemList</c> compatibility fallback (the pre-PR86 code path for
    /// folders that don't support the raw-query fast path). A direct <c>IItemQueryService</c>
    /// injection here would therefore create the eager construction-graph edges
    /// <c>UserViewCatalog -&gt; IItemQueryService -&gt; IUserViewManager</c> and
    /// <c>-&gt; IChannelManager</c> - two of the four forbidden SCC members, violating I1 exactly like
    /// a direct (non-Lazy) <c>IProviderManager</c> injection would in <c>UserViewFactory</c> (RFC
    /// §2/§8) or a direct <c>IEnumerable&lt;ILiveTvService&gt;</c> injection would in
    /// <c>LiveTvPresenceProvider</c> (RFC §5/§8, PR108 finding). Worse than those two cases: nothing
    /// depends on <see cref="UserViewCatalog"/> eagerly yet in this PR, so a direct injection would
    /// build and pass every PR109 test, then detonate at PR110 once <c>LibraryManager</c> is wired to
    /// inject <see cref="IUserViewCatalog"/> directly (closing a real cycle,
    /// <c>LibraryManager -&gt; IUserViewCatalog -&gt; IItemQueryService -&gt; IUserViewManager -&gt;
    /// ILibraryManager</c>). Same mitigation as the two precedents:
    /// <c>Lazy&lt;IItemQueryService&gt;</c>, excluded from the eager construction graph (I1) while
    /// still providing the real runtime dependency the probe needs.
    /// </para>
    /// <para>
    /// <b>I2-relevant runtime edge, reported, not yet blessed (same register as the
    /// <c>LiveTvPresenceProvider</c> PR108 escalation)</b>: unlike the two I2 exceptions already
    /// accepted for <c>UserViewFactory</c> (<c>QueueRefresh</c> post-persistence; the new-item
    /// <c>UpdateToRepositoryAsync</c> static path) and the count-only <c>Lazy&lt;IReadOnlyList&lt;
    /// ILiveTvService&gt;&gt;</c> exception accepted for <c>LiveTvPresenceProvider</c>,
    /// <c>Lazy&lt;IItemQueryService&gt;.Value</c> here is forced on <em>every</em>
    /// <see cref="GetUserViews"/> call that visits a playlists/boxsets library (RFC §9's actual call
    /// site) - i.e. a real runtime edge into the SCC (<c>ItemQueryService -&gt; IUserViewManager</c>,
    /// <c>-&gt; IChannelManager</c>) on the view-listing path itself, not merely view-creation-adjacent.
    /// This needs explicit sign-off at the RFC/PR110-111 boundary (add to §8's exception list if
    /// accepted, or redesign - e.g. a narrower probe-only port over
    /// <see cref="IItemQueryService.GetItemList(Folder, Tesserafin.Controller.Entities.InternalItemsQuery)"/> - if not); not
    /// this PR's call to make unilaterally.
    /// </para>
    /// <para>
    /// <b>Probe rewrite (RFC §3)</b>: the historical probe calls the obsolete
    /// <c>folder.GetItemList(query, channelManager, collectionManager, userViewManager,
    /// tvSeriesManager, itemSortService)</c> six-parameter overload. For a top-level library folder
    /// (<c>SupportsRawQueryItems</c> true, <c>SourceType != Channel</c>, non-recursive, no
    /// <c>ItemIds</c> - always true for the playlists/boxsets libraries this branch handles).
    /// <c>ItemQueryService.GetItemList(Folder, InternalItemsQuery)</c>'s raw-query fast path
    /// (<c>folder.GetRawQueryItems(query)</c> + its own relocated <c>PostFilterAndSort</c>) computes
    /// the exact same result as the historical <c>GetItemsInternal</c> branch it replaces for this
    /// case - both reduce to <c>postFilterAndSort(getRawQueryItems(query), query, collectionManager)</c>
    /// (<c>Folder.cs:1137</c>) since neither the channel nor recursive branch is ever taken here. This
    /// leaf therefore calls <c>IItemQueryService.GetItemList(Folder, InternalItemsQuery)</c> - never
    /// <c>Folder.GetItems</c>/<c>GetItemList</c> directly, satisfying the RFC's invariant. See
    /// <c>UserViewCatalogParityTests.GetUserViews_PlaylistBoxsetProbe_...</c> for the dedicated parity
    /// assertion.
    /// </para>
    /// </remarks>
    internal sealed class UserViewCatalog : IUserViewCatalog
    {
        private readonly IUserRootFolderProvider _userRootFolderProvider;
        private readonly IUserViewFactory _userViewFactory;
        private readonly IChannelCatalog _channelCatalog;
        private readonly ILiveTvPresenceProvider _liveTvPresenceProvider;
        private readonly IItemSortService _itemSortService;
        private readonly IServerConfigurationManager _config;
        private readonly ILocalizationManager _localizationManager;
        private readonly Lazy<IItemQueryService> _itemQueryServiceFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserViewCatalog"/> class.
        /// </summary>
        /// <param name="userRootFolderProvider">The user root folder provider leaf (PR107).</param>
        /// <param name="userViewFactory">The user view factory leaf (PR106b).</param>
        /// <param name="channelCatalog">The channel catalog leaf (PR108).</param>
        /// <param name="liveTvPresenceProvider">The Live TV presence provider leaf (PR108).</param>
        /// <param name="itemSortService">The item sort service (already a leaf, RFC §6).</param>
        /// <param name="config">The server configuration manager (<c>EnableFolderView</c>).</param>
        /// <param name="localizationManager">The localization manager (view labels).</param>
        /// <param name="itemQueryServiceFactory">
        /// The item query service, lazily resolved - <b>must</b> stay <see cref="Lazy{T}"/>, never a
        /// direct <see cref="IItemQueryService"/> injection (see type-level remarks: RFC I1 finding,
        /// PR109).
        /// </param>
        public UserViewCatalog(
            IUserRootFolderProvider userRootFolderProvider,
            IUserViewFactory userViewFactory,
            IChannelCatalog channelCatalog,
            ILiveTvPresenceProvider liveTvPresenceProvider,
            IItemSortService itemSortService,
            IServerConfigurationManager config,
            ILocalizationManager localizationManager,
            Lazy<IItemQueryService> itemQueryServiceFactory)
        {
            _userRootFolderProvider = userRootFolderProvider;
            _userViewFactory = userViewFactory;
            _channelCatalog = channelCatalog;
            _liveTvPresenceProvider = liveTvPresenceProvider;
            _itemSortService = itemSortService;
            _config = config;
            _localizationManager = localizationManager;
            _itemQueryServiceFactory = itemQueryServiceFactory;
        }

        /// <inheritdoc />
        public Folder[] GetUserViews(UserViewQuery query)
        {
            var user = query.User;

            var folders = _userRootFolderProvider.GetUserRootFolder()
                .GetChildren(user, true, null, _itemSortService)
                .OfType<Folder>()
                .ToList();

            var groupedFolders = new List<ICollectionFolder>();
            var list = new List<Folder>();

            foreach (var folder in folders)
            {
                var collectionFolder = folder as ICollectionFolder;
                var folderViewType = collectionFolder?.CollectionType;

                // Playlist and BoxSet libraries require special handling because the folder only references linked items
                if (folderViewType == CollectionType.playlists || folderViewType == CollectionType.boxsets)
                {
                    var items = _itemQueryServiceFactory.Value.GetItemList(
                        folder,
                        new InternalItemsQuery(user)
                        {
                            ParentId = folder.ParentId
                        });

                    if (!items.Any(item => item.IsVisible(user)))
                    {
                        continue;
                    }
                }

                if (UserView.IsUserSpecific(folder))
                {
                    list.Add(_userViewFactory.GetNamedView(user, folder.Name, folder.Id, folderViewType, null!));
                    continue;
                }

                if (collectionFolder is not null && UserView.IsEligibleForGrouping(folder) && user.IsFolderGrouped(folder.Id))
                {
                    groupedFolders.Add(collectionFolder);
                    continue;
                }

                if (query.PresetViews.Contains(folderViewType))
                {
                    list.Add(GetUserView(folder, folderViewType, string.Empty));
                }
                else
                {
                    list.Add(folder);
                }
            }

            foreach (var viewType in new[] { CollectionType.movies, CollectionType.tvshows })
            {
                var parents = groupedFolders.Where(i => i.CollectionType == viewType || i.CollectionType is null)
                    .ToList();

                if (parents.Count > 0)
                {
                    var localizationKey = viewType == CollectionType.tvshows
                        ? "TvShows"
                        : "Movies";

                    list.Add(GetUserView(parents, viewType, localizationKey, string.Empty, user, query.PresetViews));
                }
            }

            if (_config.Configuration.EnableFolderView)
            {
                var name = _localizationManager.GetLocalizedString("Folders");
                list.Add(_userViewFactory.GetNamedView(name, CollectionType.folders, string.Empty));
            }

            if (query.IncludeExternalContent)
            {
                var channelResult = _channelCatalog.GetChannelsAsync(new ChannelQuery
                {
                    UserId = user.Id
                }).GetAwaiter().GetResult();

                var channels = channelResult.Items;

                list.AddRange(channels);

                if (_liveTvPresenceProvider.GetEnabledUsers().Select(i => i.Id).Contains(user.Id))
                {
                    list.Add(_liveTvPresenceProvider.GetLiveTvFolder(CancellationToken.None));
                }
            }

            if (!query.IncludeHidden)
            {
                list = list.Where(i => !user.GetPreferenceValues<Guid>(PreferenceKind.MyMediaExcludes).Contains(i.Id)).ToList();
            }

            var sorted = _itemSortService.Sort(list, user, [ItemSortBy.SortName], SortOrder.Ascending).ToList();
            var orders = user.GetPreferenceValues<Guid>(PreferenceKind.OrderedViews);

            return list
                .OrderBy(i =>
                {
                    var index = Array.IndexOf(orders, i.Id);
                    if (index == -1
                        && i is UserView view
                        && !view.DisplayParentId.IsEmpty())
                    {
                        index = Array.IndexOf(orders, view.DisplayParentId);
                    }

                    return index == -1 ? int.MaxValue : index;
                })
                .ThenBy(sorted.IndexOf)
                .ThenBy(i => i.SortName)
                .ToArray();
        }

        /// <summary>
        /// Mirrors <c>UserViewManager</c>'s private <c>GetUserView(List&lt;ICollectionFolder&gt;, ...)</c>
        /// grouping helper.
        /// </summary>
        private Folder GetUserView(
            List<ICollectionFolder> parents,
            CollectionType? viewType,
            string localizationKey,
            string sortName,
            User user,
            CollectionType?[] presetViews)
        {
            if (parents.Count == 1 && parents.All(i => i.CollectionType == viewType))
            {
                if (!presetViews.Contains(viewType))
                {
                    return (Folder)parents[0];
                }

                return GetUserView((Folder)parents[0], viewType, string.Empty);
            }

            var name = _localizationManager.GetLocalizedString(localizationKey);
            return _userViewFactory.GetNamedView(user, name, viewType, sortName);
        }

        /// <summary>
        /// Mirrors <c>UserViewManager</c>'s public shadow-view <c>GetUserView(Folder, ...)</c>
        /// overload - out of scope on <see cref="IUserViewCatalog"/> itself (PR109 covers only
        /// <c>GetUserViews</c>), kept private here purely as an internal helper for
        /// <see cref="GetUserViews"/>.
        /// </summary>
        private UserView GetUserView(Folder parent, CollectionType? viewType, string sortName)
        {
            return _userViewFactory.GetShadowView(parent, viewType, sortName);
        }
    }
}
