using Reefin.Controller.Entities;
using Reefin.Model.IO;

namespace Reefin.Controller.Resolvers
{
    /// <summary>
    /// Provides a base "rule" that anyone can use to have paths ignored by the resolver.
    /// </summary>
    public interface IResolverIgnoreRule
    {
        /// <summary>
        /// Checks whether or not the file should be ignored.
        /// </summary>
        /// <param name="fileInfo">The file information.</param>
        /// <param name="parent">The parent BaseItem.</param>
        /// <returns>True if the file should be ignored.</returns>
        bool ShouldIgnore(FileSystemMetadata fileInfo, BaseItem? parent);
    }
}
