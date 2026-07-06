#nullable disable

#pragma warning disable CS1591

using Reefin.Model.Dlna;
using Reefin.Model.Dto;

namespace Reefin.Controller.MediaEncoding
{
    public class MediaInfoRequest
    {
        public MediaSourceInfo MediaSource { get; set; }

        public bool ExtractChapters { get; set; }

        public DlnaProfileType MediaType { get; set; }
    }
}
