#pragma warning disable CS1591

using Reefin.Common.Configuration;
using Reefin.Controller.Drawing;
using Reefin.Controller.Entities;
using Reefin.Controller.Library;
using Reefin.Controller.Providers;
using Reefin.Model.IO;

namespace Reefin.Server.Core.Images
{
    public class PhotoAlbumImageProvider : BaseFolderImageProvider<PhotoAlbum>
    {
        public PhotoAlbumImageProvider(IFileSystem fileSystem, IProviderManager providerManager, IApplicationPaths applicationPaths, IImageProcessor imageProcessor, ILibraryManager libraryManager)
            : base(fileSystem, providerManager, applicationPaths, imageProcessor, libraryManager)
        {
        }
    }
}
