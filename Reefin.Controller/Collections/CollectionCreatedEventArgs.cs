#nullable disable

#pragma warning disable CS1591

using System;
using Reefin.Controller.Entities.Movies;

namespace Reefin.Controller.Collections
{
    public class CollectionCreatedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets or sets the collection.
        /// </summary>
        /// <value>The collection.</value>
        public BoxSet Collection { get; set; }

        /// <summary>
        /// Gets or sets the options.
        /// </summary>
        /// <value>The options.</value>
        public CollectionCreationOptions Options { get; set; }
    }
}
