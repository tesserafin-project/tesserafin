#pragma warning disable CS1591

namespace Reefin.Model.Configuration
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
