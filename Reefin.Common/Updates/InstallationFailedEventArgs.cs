#nullable disable
#pragma warning disable CS1591

using System;

namespace Reefin.Common.Updates
{
    public class InstallationFailedEventArgs : InstallationEventArgs
    {
        public Exception Exception { get; set; }
    }
}
