#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using BitFaster.Caching.Lru;
using Reefin.Controller.Configuration;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Library;
using Reefin.Controller.LiveTv;
using Reefin.Controller.Persistence;
using Reefin.Database.Implementations.Entities;
using Reefin.Extensions;

namespace Reefin.Server.Core.Library
{
    /// <summary>
    /// Owns the item lookup cache (a <see cref="FastConcurrentLru{TKey,TValue}"/> read-through
    /// cache over <see cref="IItemRepository"/>) and exposes it as the public, read-only
    /// <see cref="IItemLookupService"/> plus the internal <see cref="IItemCacheStore"/> lifecycle
    /// port used by <see cref="LibraryManager"/> to register and invalidate cache entries.
    /// </summary>
    /// <remarks>
    /// Extracted from <c>LibraryManager</c> in PR75. Depends only on <see cref="IItemRepository"/>
    /// and <see cref="IServerConfigurationManager"/> - it must never take a dependency on
    /// <c>ILibraryManager</c>, to avoid a circular dependency with the class that consumes it.
    /// Hardened to <c>internal sealed</c> in PR76: outside this assembly the service is only ever
    /// reachable through <see cref="IItemLookupService"/> (reads) or <see cref="IItemCacheStore"/>
    /// (lifecycle), never as the concrete type. A reflection guard test keeps it that way.
    /// </remarks>
    internal sealed class ItemLookupService : IItemLookupService, IItemCacheStore
    {
        private readonly IItemRepository _itemRepository;
        private readonly FastConcurrentLru<Guid, BaseItem> _cache;

        /// <summary>
        /// Initializes a new instance of the <see cref="ItemLookupService"/> class.
        /// </summary>
        /// <param name="itemRepository">The item repository.</param>
        /// <param name="configurationManager">The server configuration manager (used for the cache size).</param>
        public ItemLookupService(IItemRepository itemRepository, IServerConfigurationManager configurationManager)
        {
            _itemRepository = itemRepository;
            _cache = new FastConcurrentLru<Guid, BaseItem>(configurationManager.Configuration.CacheSize);
        }

        /// <inheritdoc />
        public BaseItem? GetItemById(Guid id)
        {
            if (id.IsEmpty())
            {
                throw new ArgumentException("Guid can't be empty", nameof(id));
            }

            if (_cache.TryGet(id, out var item))
            {
                return item;
            }

            item = _itemRepository.RetrieveItem(id);

            if (item is not null)
            {
                Register(item);
            }

            return item;
        }

        /// <inheritdoc />
        public T? GetItemById<T>(Guid id)
            where T : BaseItem
        {
            var item = GetItemById(id);
            if (item is T typedItem)
            {
                return typedItem;
            }

            return null;
        }

        /// <inheritdoc />
        public T? GetItemById<T>(Guid id, User? user)
            where T : BaseItem
        {
            var item = GetItemById<T>(id);
            return ItemIsVisible(item, user) ? item : null;
        }

        /// <inheritdoc />
        public void Register(BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (ShouldCacheItem(item))
            {
                _cache.AddOrUpdate(item.Id, item);
            }
        }

        /// <inheritdoc />
        public void Remove(Guid id)
        {
            _cache.TryRemove(id, out _);
        }

        /// <inheritdoc />
        public void RemoveRange(IEnumerable<Guid> ids)
        {
            foreach (var id in ids)
            {
                _cache.TryRemove(id, out _);
            }
        }

        /// <summary>
        /// Determines whether an item is eligible for the item lookup cache. IItemByName
        /// implementors are excluded except <see cref="MusicArtist"/>; non-folder items are
        /// excluded except <see cref="Video"/> and <see cref="LiveTvChannel"/>.
        /// </summary>
        private static bool ShouldCacheItem(BaseItem item)
        {
            if (item is IItemByName)
            {
                return item is MusicArtist;
            }

            if (!item.IsFolder)
            {
                return item is Video or LiveTvChannel;
            }

            return true;
        }

        /// <summary>
        /// Determines whether <paramref name="item"/> is visible to <paramref name="user"/>.
        /// <see cref="UserRootFolder"/> is always visible regardless of a user's tag/parental
        /// restrictions.
        /// </summary>
        private static bool ItemIsVisible(BaseItem? item, User? user)
        {
            if (item is null)
            {
                return false;
            }

            if (user is null)
            {
                return true;
            }

            return item is UserRootFolder || item.IsVisibleStandalone(user);
        }
    }
}
