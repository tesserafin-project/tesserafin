#nullable disable
#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using Reefin.Common.Configuration;
using Reefin.Common.Plugins;
using Reefin.Controller.Plugins;
using Reefin.Model.Plugins;
using Reefin.Model.Serialization;

namespace Reefin.Providers.Plugins.AudioDb
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasEmbeddedImage
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin Instance { get; private set; }

        public override Guid Id => new Guid("a629c0da-fac5-4c7e-931a-7174223f14c8");

        public override string Name => "AudioDB";

        public override string Description => "Get artist and album metadata or images from AudioDB.";

        // TODO remove when plugin removed from server.
        public override string ConfigurationFileName => "Reefin.Plugin.AudioDb.xml";

        public string ImageResourceName => GetType().Namespace + ".reefin-plugin-tadb.svg";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html"
            };
        }
    }
}
