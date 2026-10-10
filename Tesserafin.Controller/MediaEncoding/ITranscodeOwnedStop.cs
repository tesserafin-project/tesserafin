using System;
using System.Threading.Tasks;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Stops the transcoding jobs of a play session that the caller is found to own, and no others.
/// </summary>
/// <remarks>
/// A separate interface for the reason <see cref="ITranscodeOutputStop"/> is one: adding members
/// to a published interface breaks every implementation of it.
///
/// <see cref="ITranscodeManager.KillTranscodingJobs"/> selects by play session id, which is the
/// client's to send. Two users' jobs can carry the same one, so who owns a job is asked of each
/// job that is stopped - not of the first one found, on behalf of all the others.
/// </remarks>
public interface ITranscodeOwnedStop
{
    /// <summary>
    /// Stops every job of <paramref name="playSessionId"/> that <paramref name="isCallers"/>
    /// accepts. The jobs that are stopped are the ones that were asked about: a job that replaces
    /// one of them meanwhile is not.
    /// </summary>
    /// <param name="playSessionId">The play session id the caller named.</param>
    /// <param name="isCallers">Whether a job is the caller's to stop. Asked once per job.</param>
    /// <returns>
    /// A task that completes when the processes are stopped, and fails if one could not be. The
    /// jobs' files are removed afterwards and are not waited for.
    /// </returns>
    Task StopTranscodingJobs(string playSessionId, Func<TranscodingJob, bool> isCallers);
}
