#pragma warning disable CS1591

namespace Tesserafin.Model.Dlna
{
    public class ResolutionConfiguration
    {
        public ResolutionConfiguration(int maxWidth, int maxBitrate)
        {
            MaxWidth = maxWidth;
            MaxBitrate = maxBitrate;
        }

        public int MaxWidth { get; set; }

        public int MaxBitrate { get; set; }
    }
}
