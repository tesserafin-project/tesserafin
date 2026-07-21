#pragma warning disable CS1591

using System;

namespace Tesserafin.Common.Plugins
{
    public interface IPluginAssembly
    {
        void SetAttributes(string assemblyFilePath, string dataFolderPath, Version assemblyVersion);

        void SetId(Guid assemblyId);
    }
}
