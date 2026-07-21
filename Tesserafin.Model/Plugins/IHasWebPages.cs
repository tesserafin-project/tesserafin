#pragma warning disable CS1591

using System.Collections.Generic;

namespace Tesserafin.Model.Plugins
{
    public interface IHasWebPages
    {
        IEnumerable<PluginPageInfo> GetPages();
    }
}
