#pragma warning disable CS1591

using System;
using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library
{
    public static class LibraryManagerExtensions
    {
        public static BaseItem? GetItemById(this ILibraryManager manager, string id)
        {
            return manager.GetItemById(new Guid(id));
        }
    }
}
