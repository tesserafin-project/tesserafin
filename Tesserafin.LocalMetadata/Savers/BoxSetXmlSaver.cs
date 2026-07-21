using System.IO;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Model.IO;

namespace Tesserafin.LocalMetadata.Savers
{
    /// <summary>
    /// Box set xml saver.
    /// </summary>
    public class BoxSetXmlSaver : BaseXmlSaver
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="BoxSetXmlSaver"/> class.
        /// </summary>
        /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
        /// <param name="configurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
        /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
        /// <param name="itemPeopleService">Instance of the <see cref="IItemPeopleService"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{BoxSetXmlSaver}"/> interface.</param>
        public BoxSetXmlSaver(IFileSystem fileSystem, IServerConfigurationManager configurationManager, ILibraryManager libraryManager, IItemPeopleService itemPeopleService, ILogger<BoxSetXmlSaver> logger)
            : base(fileSystem, configurationManager, libraryManager, itemPeopleService, logger)
        {
        }

        /// <inheritdoc />
        public override bool IsEnabledFor(BaseItem item, ItemUpdateType updateType)
        {
            if (!item.SupportsLocalMetadata)
            {
                return false;
            }

            return item is BoxSet && updateType >= ItemUpdateType.MetadataDownload;
        }

        /// <inheritdoc />
        protected override Task WriteCustomElementsAsync(BaseItem item, XmlWriter writer)
            => Task.CompletedTask;

        /// <inheritdoc />
        protected override string GetLocalSavePath(BaseItem item)
        {
            return Path.Combine(item.Path, "collection.xml");
        }
    }
}
