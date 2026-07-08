#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using Reefin.Controller.Channels;
using Reefin.Controller.Library;
using Reefin.Controller.MediaSegments;
using Reefin.Model.Dto;
using Reefin.Model.Entities;

namespace Reefin.Controller.Entities
{
    public interface IHasMediaSources
    {
        Guid Id { get; set; }

        long? RunTimeTicks { get; set; }

        string Path { get; }

        /// <summary>
        /// Gets the media sources.
        /// </summary>
        /// <param name="enablePathSubstitution"><c>true</c> to enable path substitution, <c>false</c> to not.</param>
        /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
        /// <param name="mediaSegmentManager">Instance of the <see cref="IMediaSegmentManager"/> interface.</param>
        /// <param name="channelManager">Instance of the <see cref="IChannelManager"/> interface.</param>
        /// <returns>A list of media sources.</returns>
        IReadOnlyList<MediaSourceInfo> GetMediaSources(bool enablePathSubstitution, IMediaSourceManager mediaSourceManager, IMediaSegmentManager mediaSegmentManager, IChannelManager channelManager);

        IReadOnlyList<MediaStream> GetMediaStreams(IMediaSourceManager mediaSourceManager);
    }
}
