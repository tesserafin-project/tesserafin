#nullable disable

#pragma warning disable CS1591

using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;

namespace Tesserafin.Controller.Providers
{
    public class LocalImageInfo
    {
        public FileSystemMetadata FileInfo { get; set; }

        public ImageType Type { get; set; }
    }
}
