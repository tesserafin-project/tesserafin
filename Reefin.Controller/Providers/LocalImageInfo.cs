#nullable disable

#pragma warning disable CS1591

using Reefin.Model.Entities;
using Reefin.Model.IO;

namespace Reefin.Controller.Providers
{
    public class LocalImageInfo
    {
        public FileSystemMetadata FileInfo { get; set; }

        public ImageType Type { get; set; }
    }
}
