#pragma warning disable CS1591

using Tesserafin.Model.Dto;

namespace Tesserafin.Model.MediaInfo
{
    public class LiveStreamResponse
    {
        public LiveStreamResponse(MediaSourceInfo mediaSource)
        {
            MediaSource = mediaSource;
        }

        public MediaSourceInfo MediaSource { get; }
    }
}
