#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Threading;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.Persistence;

public interface IMediaAttachmentRepository
{
    /// <summary>
    /// Gets the media attachments.
    /// </summary>
    /// <param name="filter">The query.</param>
    /// <returns>IEnumerable{MediaAttachment}.</returns>
    IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery filter);

    /// <summary>
    /// Saves the media attachments.
    /// </summary>
    /// <param name="id">The identifier.</param>
    /// <param name="attachments">The attachments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    void SaveMediaAttachments(Guid id, IReadOnlyList<MediaAttachment> attachments, CancellationToken cancellationToken);
}
