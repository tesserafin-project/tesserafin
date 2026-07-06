#pragma warning disable CS1591

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Playlists;
using Reefin.Common;
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

        protected override IEnumerable<BaseItem> GetEligibleChildrenForRecursiveChildren(User user)
        {
            return base.GetEligibleChildrenForRecursiveChildren(user).OfType<Playlist>();
        }

        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query)
        {
            if (query.User is null)
            {
                query.Recursive = false;
                return base.GetItemsInternal(query);
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
