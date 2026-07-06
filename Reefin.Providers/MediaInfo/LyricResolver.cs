using Microsoft.Extensions.Logging;
using Reefin.Controller.Entities.Audio;
using Reefin.Controller.MediaEncoding;
using Reefin.Model.Dlna;
using Reefin.Model.Globalization;
using Reefin.Model.IO;
using Reefin.Naming.Common;

namespace Reefin.Providers.MediaInfo;

/// <summary>
/// Resolves external lyric files for <see cref="Audio"/>.
/// </summary>
public class LyricResolver : MediaInfoResolver
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LyricResolver"/> class for external subtitle file processing.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="localizationManager">The localization manager.</param>
    /// <param name="mediaEncoder">The media encoder.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="namingOptions">The <see cref="NamingOptions"/> object containing FileExtensions, MediaDefaultFlags, MediaForcedFlags and MediaFlagDelimiters.</param>
    public LyricResolver(
        ILogger<LyricResolver> logger,
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
            DlnaProfileType.Lyric)
    {
    }
}
