#pragma warning disable CS1591

namespace Reefin.Controller.Library
{
    public class DeleteOptions
    {
        public DeleteOptions()
        {
            DeleteFromExternalProvider = true;
        }

        public bool DeleteFileLocation { get; set; }

        public bool DeleteFromExternalProvider { get; set; }
    }
}
