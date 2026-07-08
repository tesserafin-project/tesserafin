#nullable disable

#pragma warning disable CS1591

using System.Collections.Generic;
using Reefin.Controller.Channels;
using Reefin.Controller.Collections;
using Reefin.Controller.Entities;
using Reefin.Controller.Library;
using Reefin.Controller.TV;
using Reefin.Model.Querying;

namespace Reefin.Server.Core.Library
{
    public class ItemQueryService : IItemQueryService
    {
        private readonly IChannelManager _channelManager;
        private readonly ICollectionManager _collectionManager;
        private readonly IUserViewManager _userViewManager;
        private readonly ITVSeriesManager _tvSeriesManager;

        public ItemQueryService(IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager)
        {
            _channelManager = channelManager;
            _collectionManager = collectionManager;
            _userViewManager = userViewManager;
            _tvSeriesManager = tvSeriesManager;
        }

        public QueryResult<BaseItem> GetItems(Folder folder, InternalItemsQuery query)
            => folder.GetItems(query, _channelManager, _collectionManager, _userViewManager, _tvSeriesManager);

        public IReadOnlyList<BaseItem> GetItemList(Folder folder, InternalItemsQuery query)
            => folder.GetItemList(query, _channelManager, _collectionManager, _userViewManager, _tvSeriesManager);
    }
}
