#pragma warning disable CS1591

namespace Tesserafin.Model.Playlists
{
    public class PlaylistCreationResult
    {
        public PlaylistCreationResult(string id)
        {
            Id = id;
        }

        public string Id { get; }
    }
}
