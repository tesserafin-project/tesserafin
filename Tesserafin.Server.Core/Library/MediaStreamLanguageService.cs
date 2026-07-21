#pragma warning disable CS1591

using System.Collections.Generic;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Model.Entities;

namespace Tesserafin.Server.Core.Library;

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
