#pragma warning disable CS1591

using System.Text.Json.Serialization;

namespace Tesserafin.Controller.Entities
{
    [Tesserafin.Common.RequiresSourceSerialisation]
    public class PhotoAlbum : Folder
    {
        [JsonIgnore]
        public override bool AlwaysScanInternalMetadataPath => true;

        [JsonIgnore]
        public override bool SupportsPlayedStatus => false;

        [JsonIgnore]
        public override bool SupportsInheritedParentImages => false;
    }
}
