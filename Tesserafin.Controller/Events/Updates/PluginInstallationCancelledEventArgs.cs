using Tesserafin.Data.Events;
using Tesserafin.Model.Updates;

namespace Tesserafin.Controller.Events.Updates
{
    /// <summary>
    /// An event that occurs when a plugin installation is cancelled.
    /// </summary>
    public class PluginInstallationCancelledEventArgs : GenericEventArgs<InstallationInfo>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PluginInstallationCancelledEventArgs"/> class.
        /// </summary>
        /// <param name="arg">The installation info.</param>
        public PluginInstallationCancelledEventArgs(InstallationInfo arg) : base(arg)
        {
        }
    }
}
