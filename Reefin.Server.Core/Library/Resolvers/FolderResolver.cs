#nullable disable

using Reefin.Controller.Entities;
using Reefin.Controller.Library;
using Reefin.Controller.Resolvers;

namespace Reefin.Server.Core.Library.Resolvers
{
    /// <summary>
    /// Class FolderResolver.
    /// </summary>
    public class FolderResolver : GenericFolderResolver<Folder>
    {
        /// <summary>
        /// Gets the priority.
        /// </summary>
        /// <value>The priority.</value>
        public override ResolverPriority Priority => ResolverPriority.Last;

        /// <summary>
        /// Resolves the specified args.
        /// </summary>
        /// <param name="args">The args.</param>
        /// <returns>Folder.</returns>
        protected override Folder Resolve(ItemResolveArgs args)
        {
            if (args.IsDirectory)
            {
                return new Folder();
            }

            return null;
        }
    }
}
