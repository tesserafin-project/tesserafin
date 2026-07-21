#pragma warning disable CA1002, CA2227, CS1591

using System.Collections.Generic;

namespace Tesserafin.Controller.Providers
{
    public class ArtistInfo : ItemLookupInfo
    {
        public ArtistInfo()
        {
            SongInfos = new List<SongInfo>();
        }

        public List<SongInfo> SongInfos { get; set; }
    }
}
