#nullable disable

#pragma warning disable CS1591

using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;

namespace Tesserafin.Controller.MediaEncoding
{
    public class MediaInfoRequest
    {
        public MediaSourceInfo MediaSource { get; set; }

        public bool ExtractChapters { get; set; }

        public DlnaProfileType MediaType { get; set; }
    }
}
