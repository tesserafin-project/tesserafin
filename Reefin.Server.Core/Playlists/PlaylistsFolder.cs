#pragma warning disable CS1591

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Reefin.Common;
using Reefin.Controller.Channels;
using Reefin.Controller.Collections;
using Reefin.Controller.Entities;
using Reefin.Controller.Library;
using Reefin.Controller.Playlists;
using Reefin.Controller.TV;
using Reefin.Data.Enums;
using Reefin.Database.Implementations.Entities;
using Reefin.Model.Querying;

namespace Reefin.Server.Core.Playlists
{
    [RequiresSourceSerialisation]
    public class PlaylistsFolder : BasePluginFolder
    {
        public PlaylistsFolder()
        {
            Name = "Playlists";
        }

        [JsonIgnore]
        public override bool IsHidden => true;

        [JsonIgnore]
        public override bool SupportsInheritedParentImages => false;

        [JsonIgnore]
        public override CollectionType? CollectionType => Reefin.Data.Enums.CollectionType.playlists;

        // Only unsafe when query.User is set (the base routing is used as-is for query.User is
        // null, cf. the base.GetItemsInternal call in GetItemsInternal below) - kept false
        // unconditionally anyway.
        [JsonIgnore]
        public override bool SupportsRawQueryItems => false;

        protected override IEnumerable<BaseItem> GetEligibleChildrenForRecursiveChildren(User user)
        {
            return base.GetEligibleChildrenForRecursiveChildren(user).OfType<Playlist>();
        }

        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager)
        {
            if (query.User is null)
            {
                query.Recursive = false;
                return base.GetItemsInternal(query, channelManager, collectionManager, userViewManager, tvSeriesManager);
            }

            query.Recursive = true;
            query.IncludeItemTypes = [BaseItemKind.Playlist];

            return QueryWithPostFiltering(query);
        }

        public override string GetClientTypeName()
        {
            return "ManualPlaylistsFolder";
        }
    }
}
