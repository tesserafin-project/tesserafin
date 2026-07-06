#pragma warning disable CS1591

using System;
using Reefin.Common.Configuration;
using Reefin.Common.Plugins;
using Reefin.Model.Plugins;
using Reefin.Model.Serialization;

namespace Reefin.Server.Integration.Tests
{
    public class TestPluginWithoutPages : BasePlugin<BasePluginConfiguration>
    {
        public TestPluginWithoutPages(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static TestPluginWithoutPages? Instance { get; private set; }

        public override Guid Id => new Guid("ae95cbe6-bd3d-4d73-8596-490db334611e");

        public override string Name => nameof(TestPluginWithoutPages);

        public override string Description => "Server test Plugin without web pages.";
    }
}
