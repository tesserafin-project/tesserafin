#pragma warning disable CS1591

namespace Reefin.Model.Dlna
{
    public interface ITranscoderSupport
    {
        bool CanEncodeToAudioCodec(string codec);

        bool CanEncodeToSubtitleCodec(string codec);

        bool CanExtractSubtitles(string codec);
    }
}
