#nullable disable

using System;
using Tesserafin.Model.Updates;

namespace Tesserafin.Common.Updates
{
    /// <summary>
    /// Defines the <see cref="InstallationEventArgs" />.
    /// </summary>
    public class InstallationEventArgs : EventArgs
    {
        /// <summary>
        /// Gets or sets the <see cref="InstallationInfo"/>.
        /// </summary>
        public InstallationInfo InstallationInfo { get; set; }

        /// <summary>
        /// Gets or sets the <see cref="VersionInfo"/>.
        /// </summary>
        public VersionInfo VersionInfo { get; set; }
    }
}
