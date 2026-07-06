#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Movies;

namespace Reefin.Controller.Collections
{
    public class CollectionModifiedEventArgs : EventArgs
    {
        public CollectionModifiedEventArgs(BoxSet collection, IReadOnlyCollection<BaseItem> itemsChanged)
        {
            Collection = collection;
            ItemsChanged = itemsChanged;
        }

        /// <summary>
        /// Gets or sets the collection.
        /// </summary>
        /// <value>The collection.</value>
        public BoxSet Collection { get; set; }

        /// <summary>
        /// Gets or sets the items changed.
        /// </summary>
        /// <value>The items changed.</value>
        public IReadOnlyCollection<BaseItem> ItemsChanged { get; set; }
    }
}
