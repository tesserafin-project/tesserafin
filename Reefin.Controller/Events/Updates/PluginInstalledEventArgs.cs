using Reefin.Data.Events;
using Reefin.Model.Updates;

namespace Reefin.Controller.Events.Updates
{
    /// <summary>
    /// An event that occurs when a plugin is installed.
    /// </summary>
    public class PluginInstalledEventArgs : GenericEventArgs<InstallationInfo>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PluginInstalledEventArgs"/> class.
        /// </summary>
        /// <param name="arg">The installation info.</param>
        public PluginInstalledEventArgs(InstallationInfo arg) : base(arg)
        {
        }
    }
}
