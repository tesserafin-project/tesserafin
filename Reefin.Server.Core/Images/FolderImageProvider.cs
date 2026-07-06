#pragma warning disable CS1591

using Reefin.Common.Configuration;
using Reefin.Controller.Drawing;
using Reefin.Controller.Entities;
using Reefin.Controller.Entities.Audio;
using Reefin.Controller.Library;
using Reefin.Controller.Providers;
using Reefin.Model.IO;

namespace Reefin.Server.Core.Images
{
    public class FolderImageProvider : BaseFolderImageProvider<Folder>
    {
        public FolderImageProvider(IFileSystem fileSystem, IProviderManager providerManager, IApplicationPaths applicationPaths, IImageProcessor imageProcessor, ILibraryManager libraryManager)
            : base(fileSystem, providerManager, applicationPaths, imageProcessor, libraryManager)
        {
        }

        protected override bool Supports(BaseItem item)
        {
            if (item is PhotoAlbum || item is MusicAlbum)
            {
                return false;
            }

            if (item is Folder && item.IsTopParent)
            {
                return false;
            }

            return true;
        }
    }
}
