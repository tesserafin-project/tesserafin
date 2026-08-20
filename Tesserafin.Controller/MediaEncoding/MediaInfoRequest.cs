#nullable disable

#pragma warning disable CS1591

using Tesserafin.Controller.Library;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;

namespace Tesserafin.Controller.MediaEncoding
{
    public class MediaInfoRequest
    {
        public MediaSourceInfo MediaSource { get; set; }

        public bool ExtractChapters { get; set; }

        public DlnaProfileType MediaType { get; set; }

        /// <summary>
        /// Gets or sets a reader over the media itself, for a source whose
        /// <see cref="MediaSourceInfo.Path"/> is an address the prober must not be sent to fetch —
        /// a live tuner stream published behind an <c>[Authorize]</c>d route, above all
        /// (#153-LTV-R1). When set, the prober reads the bytes from here instead of opening the
        /// path, exactly as the transcode already does.
        /// </summary>
        public IDirectStreamProvider DirectStreamReader { get; set; }
    }
}
