using System;
using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Narrow leaf port covering item id generation and the create/register subset of
    /// <see cref="ILibraryManager"/> actually exercised by named/shadow user-view creation
    /// (<c>GetNamedView</c>/<c>GetShadowView</c>, RFC <c>docs/rfc-di-query-user-views-v2.md</c> §2).
    /// </summary>
    /// <remarks>
    /// Introduced in PR106a as an explicit prerequisite for <c>IUserViewFactory</c> (PR106b):
    /// without it, <c>GetNamedView</c>/<c>GetShadowView</c> cannot be extracted off
    /// <see cref="ILibraryManager"/>, since they need <see cref="GetNewItemId"/> and the
    /// save-then-register sequence, neither of which is exposed on any existing port. Deliberately
    /// narrow: this is the subset of item-CRUD that named/shadow view creation actually uses
    /// (single-item <c>UserView</c>, <c>parent</c> always <c>null</c> at the real call sites), not a
    /// general-purpose replacement for <see cref="ILibraryManager"/>'s full create/update/delete
    /// surface. See the concrete <c>ItemStore</c> implementation (Tesserafin.Server.Core) for the
    /// characterized failure semantics and the RFC invariant I1 proof (no dependency on
    /// <see cref="ILibraryManager"/>, <see cref="IUserViewManager"/>, <c>IChannelManager</c> or
    /// <c>ILiveTvManager</c>).
    /// </remarks>
    public interface IItemStore
    {
        /// <summary>
        /// Occurs when an item has been both persisted and registered in the item cache by
        /// <see cref="CreateItem"/>, restricted to <see cref="SourceType.Library"/> items - the same
        /// filter <c>LibraryManager.CreateItems</c> applies before raising its historical
        /// <c>ItemAdded</c> event. <c>LibraryManager</c> subscribes to this event and relays it as
        /// <c>ItemAdded</c> (RFC §2, decided contract): a given creation raises exactly one
        /// <c>ItemAdded</c> notification, either via this relay (creation through
        /// <see cref="CreateItem"/>) or via the historical <c>LibraryManager.CreateItems</c> path
        /// (which does not go through this port), never both.
        /// </summary>
        event EventHandler<ItemChangeEventArgs>? ItemSaved;

        /// <summary>
        /// Computes the deterministic id for an item from its resolve key and type. Pure function of
        /// the server configuration (program-data path normalization, case sensitivity) and an MD5
        /// hash of <paramref name="type"/>'s full name plus the (possibly normalized) key - mirrors
        /// <c>LibraryManager.GetNewItemId</c> exactly, including its always-<c>false</c>
        /// case-insensitivity forcing (the private <c>forceCaseInsensitive: true</c> overload used
        /// internally for alternate-version resolution is out of scope for this port).
        /// </summary>
        /// <param name="key">The resolve key (e.g. a path or a synthetic named-view key).</param>
        /// <param name="type">The item type used to namespace the id.</param>
        /// <returns>The deterministic <see cref="Guid"/> for this key/type pair.</returns>
        Guid GetNewItemId(string key, Type type);

        /// <summary>
        /// Persists and registers a single item, mirroring the subset of
        /// <c>LibraryManager.CreateItems</c> actually exercised by named/shadow user-view creation:
        /// save then register, in that order, with no rollback if registration fails after a
        /// successful save. See the concrete implementation's remarks for the exact characterized
        /// failure semantics.
        /// </summary>
        /// <param name="item">The item to create.</param>
        /// <param name="parent">
        /// The parent, if any. At the real call sites (<c>GetNamedView</c>/<c>GetShadowView</c>) this
        /// is always <c>null</c>; <see cref="ItemSaved"/> is raised with this exact value as
        /// <see cref="ItemChangeEventArgs.Parent"/> (no id-based parent lookup fallback - this port
        /// has no item-lookup dependency).
        /// </param>
        void CreateItem(BaseItem item, BaseItem? parent);

        /// <summary>
        /// Registers an already-persisted item in the item cache. Pure pass-through, mirroring
        /// <c>LibraryManager.RegisterItem</c>.
        /// </summary>
        /// <param name="item">The item to register.</param>
        void RegisterItem(BaseItem item);
    }
}
