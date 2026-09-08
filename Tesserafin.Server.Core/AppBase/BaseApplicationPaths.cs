using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tesserafin.Common.Configuration;
using Tesserafin.Extensions;

namespace Tesserafin.Server.Core.AppBase
{
    /// <summary>
    /// Provides a base class to hold common application paths used by both the UI and Server.
    /// This can be subclassed to add application-specific paths.
    /// </summary>
    public abstract class BaseApplicationPaths : IApplicationPaths
    {
        /// <summary>
        /// The prefix of the directory sanity marker files this server writes.
        /// </summary>
        /// <remarks>
        /// Written exactly as spelled here: lowercase, NFC, ASCII. Recognition is case-insensitive and
        /// treats NFC and NFD as the same marker, but nothing ever writes another spelling.
        /// </remarks>
        private const string MarkerPrefix = ".tesserafin-";

        /// <summary>
        /// The pre-rename prefix of the directory sanity marker files.
        /// </summary>
        /// <remarks>
        /// Recognised so that an installation created before the rename passes its own sanity check and is
        /// migrated onto <see cref="MarkerPrefix"/> in place. Never written.
        /// </remarks>
        private const string LegacyMarkerPrefix = ".reefin-";

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseApplicationPaths"/> class.
        /// </summary>
        /// <param name="programDataPath">The program data path.</param>
        /// <param name="logDirectoryPath">The log directory path.</param>
        /// <param name="configurationDirectoryPath">The configuration directory path.</param>
        /// <param name="cacheDirectoryPath">The cache directory path.</param>
        /// <param name="webDirectoryPath">The web directory path.</param>
        protected BaseApplicationPaths(
            string programDataPath,
            string logDirectoryPath,
            string configurationDirectoryPath,
            string cacheDirectoryPath,
            string webDirectoryPath)
        {
            ProgramDataPath = programDataPath;
            LogDirectoryPath = logDirectoryPath;
            ConfigurationDirectoryPath = configurationDirectoryPath;
            CachePath = cacheDirectoryPath;
            WebPath = webDirectoryPath;
            DataPath = Directory.CreateDirectory(Path.Combine(ProgramDataPath, "data")).FullName;
        }

        /// <inheritdoc/>
        public string ProgramDataPath { get; }

        /// <inheritdoc/>
        public string WebPath { get; }

        /// <inheritdoc/>
        public string ProgramSystemPath { get; } = AppContext.BaseDirectory;

        /// <inheritdoc/>
        public string DataPath { get; }

        /// <inheritdoc />
        public string VirtualDataPath => "%AppDataPath%";

        /// <inheritdoc/>
        public string ImageCachePath => Path.Combine(CachePath, "images");

        /// <inheritdoc/>
        public string PluginsPath => Path.Combine(ProgramDataPath, "plugins");

        /// <inheritdoc/>
        public string PluginConfigurationsPath => Path.Combine(PluginsPath, "configurations");

        /// <inheritdoc/>
        public string LogDirectoryPath { get; }

        /// <inheritdoc/>
        public string ConfigurationDirectoryPath { get; }

        /// <inheritdoc/>
        public string SystemConfigurationFilePath => Path.Combine(ConfigurationDirectoryPath, "system.xml");

        /// <inheritdoc/>
        public string CachePath { get; set; }

        /// <inheritdoc/>
        public string TempDirectory => Path.Join(Path.GetTempPath(), "tesserafin");

        /// <inheritdoc />
        public string TrickplayPath => Path.Combine(DataPath, "trickplay");

        /// <inheritdoc />
        public string BackupPath => Path.Combine(DataPath, "backups");

        /// <inheritdoc />
        public virtual void MakeSanityCheckOrThrow()
        {
            CreateAndCheckMarker(ConfigurationDirectoryPath, "config");
            CreateAndCheckMarker(LogDirectoryPath, "log");
            CreateAndCheckMarker(PluginsPath, "plugin");
            CreateAndCheckMarker(ProgramDataPath, "data");
            CreateAndCheckMarker(CachePath, "cache");
            CreateAndCheckMarker(DataPath, "data");
            CreateCacheDirTag(CachePath);
        }

        /// <inheritdoc />
        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
        {
            Directory.CreateDirectory(path);

            CheckOrCreateMarker(path, MarkerPrefix + markerName, LegacyMarkerPrefix + markerName, recursive);
        }

        /// <summary>
        /// Creates a CACHEDIR.TAG file in the specified directory per the Cache Directory Tagging specification.
        /// This signals to backup tools (e.g. Restic, Borg) that the directory contains cached data
        /// and can be excluded from backups.
        /// </summary>
        /// <param name="path">The cache directory path.</param>
        internal static void CreateCacheDirTag(string path)
        {
            var tagPath = Path.Combine(path, "CACHEDIR.TAG");
            if (!File.Exists(tagPath))
            {
                File.WriteAllText(
                    tagPath,
                    "Signature: 8a477f597d28d172789f06886806bc55\n"
                    + "# This file is a cache directory tag created by Tesserafin.\n"
                    + "# For information about cache directory tags, see:\n"
                    + "#\thttps://bford.info/cachedir/\n");
            }
        }

        private static IEnumerable<string> GetMarkers(string path, bool recursive = false)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,

                // The markers are dotfiles, which .NET reports as hidden on Unix, and an unreadable
                // directory must surface rather than be silently reported as marker-free.
                AttributesToSkip = FileAttributes.None,
                IgnoreInaccessible = false,

                // A marker that differs only in case is the same marker, on every file system.
                MatchCasing = MatchCasing.CaseInsensitive
            };

            return Directory.EnumerateFiles(path, MarkerPrefix + "*", options)
                .Concat(Directory.EnumerateFiles(path, LegacyMarkerPrefix + "*", options));
        }

        private static bool MarkerNameEquals(string fileName, string markerName)
        {
            return string.Equals(NormalizeMarkerName(fileName), NormalizeMarkerName(markerName), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeMarkerName(string fileName)
        {
            try
            {
                return fileName.IsNormalized(NormalizationForm.FormC) ? fileName : fileName.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                // A file name that is not valid Unicode cannot be normalised. Compare it as it came off
                // the file system rather than failing a state root over it.
                return fileName;
            }
        }

        private static bool IsDirectlyUnder(string filePath, string directoryPath)
        {
            var parent = Path.GetDirectoryName(filePath);

            return parent is not null
                && string.Equals(
                    Path.TrimEndingDirectorySeparator(parent),
                    Path.TrimEndingDirectorySeparator(directoryPath),
                    StringComparison.Ordinal);
        }

        private static void CheckOrCreateMarker(string path, string markerName, string legacyMarkerName, bool recursive = false)
        {
            string? rootMarker = null;
            string? otherMarkers = null;
            List<string>? legacyMarkers = null;

            try
            {
                foreach (var marker in GetMarkers(path, recursive))
                {
                    var fileName = Path.GetFileName(marker);

                    if (MarkerNameEquals(fileName, markerName))
                    {
                        if (rootMarker is null && (!recursive || IsDirectlyUnder(marker, path)))
                        {
                            rootMarker = marker;
                        }
                    }
                    else if (MarkerNameEquals(fileName, legacyMarkerName))
                    {
                        (legacyMarkers ??= new List<string>()).Add(marker);
                    }
                    else
                    {
                        otherMarkers ??= marker;
                    }
                }
            }
            catch
            {
                // Error while checking for marker files, assume none exist and keep going
                // TODO: add some logging
            }

            if (otherMarkers is not null)
            {
                throw new InvalidOperationException($"Expected to find only {markerName} but found marker for {otherMarkers}.");
            }

            // The order is the whole of the migration contract: write the new marker first, and only then
            // remove the pre-rename one. The reverse order leaves the directory unmarked if the process
            // dies between the two, and an unmarked state root is indistinguishable from a fresh one.
            var markerPath = rootMarker ?? Path.Combine(path, markerName);
            if (!File.Exists(markerPath))
            {
                FileHelper.CreateEmpty(markerPath);
            }

            if (legacyMarkers is null || !File.Exists(markerPath))
            {
                return;
            }

            foreach (var legacyMarker in legacyMarkers)
            {
                try
                {
                    File.Delete(legacyMarker);
                }
                catch
                {
                    // The new marker is already on disk, so a surviving pre-rename marker is cosmetic.
                    // It is recognised again, and its removal retried, on the next start.
                    // TODO: add some logging
                }
            }
        }
    }
}
