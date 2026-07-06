#pragma warning disable CS1591

using Reefin.Model.Dto;

namespace Reefin.Model.MediaInfo
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
