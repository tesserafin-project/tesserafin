#pragma warning disable CS1591

using Reefin.Controller.Library;

namespace Reefin.Providers.Manager
{
    public class RefreshResult
    {
        public ItemUpdateType UpdateType { get; set; }

        public string? ErrorMessage { get; set; }

        public int Failures { get; set; }
    }
}
