using System;
using System.Collections.Concurrent;
using Tesserafin.Model.LiveTv;

namespace Tesserafin.Controller.LiveTv
{
    /// <summary>
    /// The recordings in progress, by timer id: written by the recordings manager, read by the access path
    /// to tell whether a video is one (DEBT-SERVER-9). It depends on nothing, so a reader does not reach
    /// the library manager the recordings manager depends on.
    /// </summary>
    public sealed class ActiveRecordings : ConcurrentDictionary<string, ActiveRecordingInfo>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ActiveRecordings"/> class.
        /// </summary>
        public ActiveRecordings()
            : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        /// <summary>
        /// Gets the recording in progress at a path, as <see cref="IRecordingsManager.GetActiveRecordingInfo"/> does.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns>The recording; <c>null</c> when none is in progress at that path.</returns>
        public ActiveRecordingInfo? GetActiveRecordingInfo(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || IsEmpty)
            {
                return null;
            }

            foreach (var (_, recordingInfo) in this)
            {
                if (string.Equals(recordingInfo.Path, path, StringComparison.Ordinal)
                    && !recordingInfo.CancellationTokenSource.IsCancellationRequested)
                {
                    return recordingInfo.Timer.Status == RecordingStatus.InProgress ? recordingInfo : null;
                }
            }

            return null;
        }
    }
}
