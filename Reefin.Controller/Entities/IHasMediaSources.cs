#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
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
        /// <returns>A list of media sources.</returns>
        IReadOnlyList<MediaSourceInfo> GetMediaSources(bool enablePathSubstitution);

        IReadOnlyList<MediaStream> GetMediaStreams();
    }
}
