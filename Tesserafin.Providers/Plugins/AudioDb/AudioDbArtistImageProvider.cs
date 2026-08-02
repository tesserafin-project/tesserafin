#nullable disable

#pragma warning disable CS1591

using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Common.Net;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Providers;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Model.Providers;

namespace Tesserafin.Providers.Plugins.AudioDb
{
    public class AudioDbArtistImageProvider : IRemoteImageProvider, IHasOrder
    {
        private readonly IServerConfigurationManager _config;
        private readonly IFileSystem _fileSystem;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;

        public AudioDbArtistImageProvider(IServerConfigurationManager config, IFileSystem fileSystem, IHttpClientFactory httpClientFactory)
        {
            _config = config;
            _fileSystem = fileSystem;
            _httpClientFactory = httpClientFactory;
        }

        /// <inheritdoc />
        public string Name => "TheAudioDB";

        /// <inheritdoc />
        // After fanart
        public int Order => 1;

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return
            [
                ImageType.Primary,
                ImageType.Logo,
                ImageType.Banner,
                ImageType.Backdrop
            ];
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            if (item.TryGetProviderId(MetadataProvider.MusicBrainzArtist, out var id))
            {
                await AudioDbArtistProvider.Current.EnsureArtistInfo(id, cancellationToken).ConfigureAwait(false);

                var path = AudioDbArtistProvider.GetArtistInfoPath(_config.ApplicationPaths, id);

                // No key configured means nothing was downloaded; report no images rather than throw.
                if (!_fileSystem.GetFileSystemInfo(path).Exists)
                {
                    return [];
                }

                FileStream jsonStream = AsyncFile.OpenRead(path);
                await using (jsonStream.ConfigureAwait(false))
                {
                    var obj = await JsonSerializer.DeserializeAsync<AudioDbArtistProvider.RootObject>(jsonStream, _jsonOptions, cancellationToken).ConfigureAwait(false);

                    if (obj is not null && obj.artists is not null && obj.artists.Count > 0)
                    {
                        return GetImages(obj.artists[0]);
                    }
                }
            }

            return [];
        }

        private List<RemoteImageInfo> GetImages(AudioDbArtistProvider.Artist item)
        {
            var list = new List<RemoteImageInfo>();

            if (!string.IsNullOrWhiteSpace(item.strArtistThumb))
            {
                list.Add(new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = item.strArtistThumb,
                    Type = ImageType.Primary
                });
            }

            if (!string.IsNullOrWhiteSpace(item.strArtistLogo))
            {
                list.Add(new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = item.strArtistLogo,
                    Type = ImageType.Logo
                });
            }

            if (!string.IsNullOrWhiteSpace(item.strArtistBanner))
            {
                list.Add(new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = item.strArtistBanner,
                    Type = ImageType.Banner
                });
            }

            if (!string.IsNullOrWhiteSpace(item.strArtistFanart))
            {
                list.Add(new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = item.strArtistFanart,
                    Type = ImageType.Backdrop
                });
            }

            if (!string.IsNullOrWhiteSpace(item.strArtistFanart2))
            {
                list.Add(new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = item.strArtistFanart2,
                    Type = ImageType.Backdrop
                });
            }

            if (!string.IsNullOrWhiteSpace(item.strArtistFanart3))
            {
                list.Add(new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = item.strArtistFanart3,
                    Type = ImageType.Backdrop
                });
            }

            return list;
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
            return httpClient.GetAsync(url, cancellationToken);
        }

        /// <inheritdoc />
        public bool Supports(BaseItem item)
            => item is MusicArtist;
    }
}
