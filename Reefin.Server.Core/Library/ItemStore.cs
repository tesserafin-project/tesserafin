#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using Reefin.Common.Extensions;
using Reefin.Controller.Configuration;
using Reefin.Controller.Entities;
using Reefin.Controller.Library;
using Reefin.Controller.Persistence;

namespace Reefin.Server.Core.Library
{
    /// <summary>
    /// Sole production implementation of <see cref="IItemStore"/> - item id generation plus the
    /// save-then-register subset of item creation actually used by named/shadow user-view creation
    /// (RFC <c>docs/rfc-di-query-user-views-v2.md</c> §2, §8, PR106a).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Depends only on <see cref="IItemPersistenceService"/>, <see cref="IItemCacheStore"/> and
    /// <see cref="IServerConfigurationManager"/> (plus a logger, purely for the same
    /// swallow-and-log behavior <c>LibraryManager.CreateItems</c> applies to its own event
    /// handlers). None of these implementations (<c>ItemPersistenceService</c>,
    /// <c>ItemLookupService</c> for <see cref="IItemCacheStore"/>) reference
    /// <c>ILibraryManager</c>, <c>IUserViewManager</c>, <c>IChannelManager</c> or
    /// <c>ILiveTvManager</c> - this satisfies RFC invariant I1 (eager construction graph) for this
    /// leaf. <c>LibraryManager</c> is a *consumer* of this class (via <see cref="IItemStore"/>),
    /// never the other way around.
    /// </para>
    /// <para>
    /// <b>Characterized failure semantics (deliberately not improved on)</b>: <see cref="CreateItem"/>
    /// reproduces the current, non-transactional behavior of
    /// <c>LibraryManager.CreateItems</c> (LibraryManager.cs:2290-2295) for the single-item,
    /// <c>parent == null</c>, <see cref="Reefin.Controller.Entities.UserView"/> subset:
    /// <see cref="IItemPersistenceService.SaveItems"/> runs first: if it throws, nothing is
    /// registered in the cache and the exception propagates unchanged. If it succeeds,
    /// <see cref="RegisterItem"/> runs next: if *that* throws, the item is already durably
    /// persisted but never made it into the cache, and the exception still propagates - there is no
    /// compensating delete/rollback of the save, exactly as in the historical code. Callers that
    /// need transactional all-or-nothing semantics must build that on top of this port; it is out of
    /// scope for this leaf, which exists to mirror the current behavior for the item-store split, not
    /// to fix it.
    /// </para>
    /// </remarks>
    internal sealed class ItemStore : IItemStore
    {
        private readonly IItemPersistenceService _persistenceService;
        private readonly IItemCacheStore _itemCacheStore;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly ILogger<ItemStore> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ItemStore"/> class.
        /// </summary>
        /// <param name="persistenceService">The item persistence service (save side).</param>
        /// <param name="itemCacheStore">The item cache lifecycle port (register side).</param>
        /// <param name="configurationManager">The server configuration manager (id generation).</param>
        /// <param name="logger">The logger, used only to swallow-and-log <see cref="ItemSaved"/> handler failures.</param>
        public ItemStore(
            IItemPersistenceService persistenceService,
            IItemCacheStore itemCacheStore,
            IServerConfigurationManager configurationManager,
            ILogger<ItemStore> logger)
        {
            _persistenceService = persistenceService;
            _itemCacheStore = itemCacheStore;
            _configurationManager = configurationManager;
            _logger = logger;
        }

        /// <inheritdoc />
        public event EventHandler<ItemChangeEventArgs>? ItemSaved;

        /// <inheritdoc />
        public Guid GetNewItemId(string key, Type type)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            ArgumentNullException.ThrowIfNull(type);

            // Mirrors LibraryManager.GetNewItemIdInternal(key, type, forceCaseInsensitive: false)
            // exactly (LibraryManager.cs:787-809). The forceCaseInsensitive: true variant is only
            // ever used internally by LibraryManager for alternate-version id resolution, which is
            // out of scope for this port (see IItemStore remarks).
            string programDataPath = _configurationManager.ApplicationPaths.ProgramDataPath;
            if (key.StartsWith(programDataPath, StringComparison.Ordinal))
            {
                // Try to normalize paths located underneath program-data in an attempt to make them more portable
                key = key.Substring(programDataPath.Length)
                    .TrimStart('/', '\\')
                    .Replace('/', '\\');
            }

            if (!_configurationManager.Configuration.EnableCaseSensitiveItemIds)
            {
                key = key.ToLowerInvariant();
            }

            key = type.FullName + key;

            return key.GetMD5();
        }

        /// <inheritdoc />
        public void CreateItem(BaseItem item, BaseItem? parent)
        {
            ArgumentNullException.ThrowIfNull(item);

            // Save-then-register, no rollback on partial failure - see the type-level remarks for the
            // exact characterized behavior. Mirrors LibraryManager.CreateItems(LibraryManager.cs:2290-2295)
            // restricted to a single item (the alternate-local-versions branch, LibraryManager.cs:2256-2288,
            // never fires for the UserView/parent==null subset this port covers - see RFC §2).
            _persistenceService.SaveItems(new List<BaseItem> { item }, CancellationToken.None);

            RegisterItem(item);

            if (parent is Folder folder)
            {
                folder.Children = null;
                folder.UserData = null;
            }

            // Same SourceType.Library filter LibraryManager.CreateItems applies before raising
            // ItemAdded (LibraryManager.cs:2303-2328) - live-tv guide noise stays excluded.
            if (item.SourceType != SourceType.Library)
            {
                return;
            }

            if (ItemSaved is null)
            {
                return;
            }

            try
            {
                ItemSaved(
                    this,
                    new ItemChangeEventArgs
                    {
                        // No item.GetParent(this)-style fallback: this port has no item-lookup
                        // dependency (see IItemStore remarks). At the real call sites the item is a
                        // freshly-constructed UserView with an empty ParentId, for which
                        // LibraryManager's own "parent ?? item.GetParent(this)" fallback would also
                        // resolve to null without ever calling the lookup - so this matches the
                        // historical behavior for the subset this port actually serves.
                        Item = item,
                        Parent = parent
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ItemSaved event handler");
            }
        }

        /// <inheritdoc />
        public void RegisterItem(BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

            _itemCacheStore.Register(item);
        }
    }
}
