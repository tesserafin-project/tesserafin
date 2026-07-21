#pragma warning disable CS1591

namespace Tesserafin.Model.Configuration
{
    public class MetadataConfiguration
    {
        public MetadataConfiguration()
        {
            UseFileCreationTimeForDateAdded = true;
        }

        public bool UseFileCreationTimeForDateAdded { get; set; }
    }
}
