using Microsoft.Extensions.Logging;
using Reefin.Controller.Entities;
using Reefin.Controller.MediaEncoding;
using Reefin.Model.Dlna;
using Reefin.Model.Globalization;
using Reefin.Model.IO;
using Reefin.Naming.Common;

namespace Reefin.Providers.MediaInfo
{
    /// <summary>
    /// Resolves external subtitle files for <see cref="Video"/>.
    /// </summary>
    public class SubtitleResolver : MediaInfoResolver
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SubtitleResolver"/> class for external subtitle file processing.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="localizationManager">The localization manager.</param>
        /// <param name="mediaEncoder">The media encoder.</param>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="namingOptions">The <see cref="NamingOptions"/> object containing FileExtensions, MediaDefaultFlags, MediaForcedFlags and MediaFlagDelimiters.</param>
        public SubtitleResolver(
            ILogger<SubtitleResolver> logger,
            ILocalizationManager localizationManager,
            IMediaEncoder mediaEncoder,
            IFileSystem fileSystem,
            NamingOptions namingOptions)
            : base(
                logger,
                localizationManager,
                mediaEncoder,
                fileSystem,
                namingOptions,
                DlnaProfileType.Subtitle)
        {
        }
    }
}
