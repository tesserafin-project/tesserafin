#pragma warning disable CS1591

using Tesserafin.Controller.Library;

namespace Tesserafin.Providers.Manager
{
    public class RefreshResult
    {
        public ItemUpdateType UpdateType { get; set; }

        public string? ErrorMessage { get; set; }

        public int Failures { get; set; }
    }
}
