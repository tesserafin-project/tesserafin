#pragma warning disable CS1591

using System.Collections.Generic;
using Reefin.Model.Entities;

namespace Reefin.Controller.Library;

public interface IMediaStreamLanguageService
{
    IReadOnlyList<string> GetMediaStreamLanguages(MediaStreamType mediaStreamType);
}
