#pragma warning disable CS1591

using System.Collections.Generic;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.Library;

public interface IMediaStreamLanguageService
{
    IReadOnlyList<string> GetMediaStreamLanguages(MediaStreamType mediaStreamType);
}
