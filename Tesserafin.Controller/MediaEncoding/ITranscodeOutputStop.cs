using System;
using System.Threading.Tasks;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Stops one transcoding job, named by what the server knows of it and by nothing a client sends.
/// </summary>
/// <remarks>
/// A separate interface, not a new member on <see cref="ITranscodeManager"/>: adding members to a
/// published interface breaks every implementation of it. The manager that ships implements
/// both; <see cref="TranscodeOutputStopExtensions"/> reaches this one from the other.
///
/// <see cref="ITranscodeManager.KillTranscodingJobs"/> selects by play session id, or by device
/// id when there is none. Both are query parameters on the routes that restart a transcode: a
/// request that sends neither selects every job started the same way, and one that sends
/// somebody else's selects theirs.
/// </remarks>
public interface ITranscodeOutputStop
{
    /// <summary>
    /// Stops the job writing to <paramref name="path"/>, if it is still the one
    /// <paramref name="generation"/> names. Its files are left to <paramref name="deleteFiles"/>.
    /// </summary>
    /// <param name="path">The job's output path.</param>
    /// <param name="type">The job type.</param>
    /// <param name="generation">The <see cref="TranscodingJob.Generation"/> of the job the caller was authorized against.</param>
    /// <param name="deleteFiles">Whether the job's files are removed.</param>
    /// <returns>A task that completes when the job is stopped, or at once when it is no longer there.</returns>
    Task StopTranscodingJob(string path, TranscodingJobType type, long generation, Func<string, bool> deleteFiles);
}
