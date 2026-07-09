#pragma warning disable CS1591

using System.Collections.Generic;
using Reefin.Controller.Library;
using Reefin.Controller.Persistence;
using Reefin.Model.Entities;

namespace Reefin.Server.Core.Library;

public class MediaStreamLanguageService : IMediaStreamLanguageService
{
    private readonly IMediaStreamRepository _mediaStreamRepository;

    public MediaStreamLanguageService(IMediaStreamRepository mediaStreamRepository)
    {
        _mediaStreamRepository = mediaStreamRepository;
    }

    public IReadOnlyList<string> GetMediaStreamLanguages(MediaStreamType mediaStreamType)
    {
        return _mediaStreamRepository.GetMediaStreamLanguages(mediaStreamType);
    }
}
