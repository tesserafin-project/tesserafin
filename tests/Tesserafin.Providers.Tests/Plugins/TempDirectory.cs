using System;
using System.IO;

namespace Tesserafin.Providers.Tests.Plugins
{
    /// <summary>A disposable temporary directory.</summary>
    public sealed class TempDirectory : IDisposable
    {
        /// <summary>Initializes a new instance of the <see cref="TempDirectory"/> class.</summary>
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tesserafin-provider-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        /// <summary>Gets the directory path.</summary>
        public string Path { get; }

        /// <inheritdoc />
        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (DirectoryNotFoundException)
            {
                // Already gone; nothing to clean up.
            }
        }
    }
}
