#pragma warning disable CS1591

namespace Tesserafin.Model.Dlna
{
    public interface ITranscoderSupport
    {
        bool CanEncodeToAudioCodec(string codec);

        bool CanEncodeToSubtitleCodec(string codec);

        bool CanExtractSubtitles(string codec);
    }
}
