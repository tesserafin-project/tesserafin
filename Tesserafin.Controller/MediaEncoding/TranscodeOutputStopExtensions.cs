using System;
using System.Threading.Tasks;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Reaches <see cref="ITranscodeOutputStop"/> and <see cref="ITranscodeOwnedStop"/> from the
/// <see cref="ITranscodeManager"/> a caller already holds.
/// </summary>
public static class TranscodeOutputStopExtensions
{
    /// <summary>
    /// Stops the job writing to <paramref name="path"/>, if it is still the one
    /// <paramref name="generation"/> names.
    /// </summary>
    /// <param name="transcodeManager">The transcode manager.</param>
    /// <param name="path">The job's output path.</param>
    /// <param name="type">The job type.</param>
    /// <param name="generation">The generation of the job the caller was authorized against.</param>
    /// <param name="deleteFiles">Whether the job's files are removed.</param>
    /// <returns>A task that completes when the job is stopped, or at once when it is no longer there.</returns>
    /// <exception cref="NotSupportedException">
    /// The manager cannot stop a job this way. Not a silent success: the caller is about to start
    /// another process on the same output.
    /// </exception>
    public static Task StopTranscodingJob(this ITranscodeManager transcodeManager, string path, TranscodingJobType type, long generation, Func<string, bool> deleteFiles)
        => transcodeManager is ITranscodeOutputStop stop
            ? stop.StopTranscodingJob(path, type, generation, deleteFiles)
            : throw new NotSupportedException("This transcode manager cannot stop a job by its output.");

    /// <summary>
    /// Stops every job of <paramref name="playSessionId"/> that <paramref name="isCallers"/> accepts.
    /// </summary>
    /// <param name="transcodeManager">The transcode manager.</param>
    /// <param name="playSessionId">The play session id the caller named.</param>
    /// <param name="isCallers">Whether a job is the caller's to stop.</param>
    /// <returns>A task that completes when the processes are stopped.</returns>
    /// <exception cref="NotSupportedException">
    /// The manager cannot stop jobs this way. Not a silent success: the caller is about to be told
    /// its transcodes have stopped.
    /// </exception>
    public static Task StopTranscodingJobs(this ITranscodeManager transcodeManager, string playSessionId, Func<TranscodingJob, bool> isCallers)
        => transcodeManager is ITranscodeOwnedStop stop
            ? stop.StopTranscodingJobs(playSessionId, isCallers)
            : throw new NotSupportedException("This transcode manager cannot stop a play session's jobs by their owner.");
}
