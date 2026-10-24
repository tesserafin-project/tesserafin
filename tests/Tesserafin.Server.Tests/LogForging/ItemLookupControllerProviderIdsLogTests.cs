using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Tesserafin.Api.Controllers;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.IO;
using Tesserafin.Model.Providers;
using Xunit;

namespace Tesserafin.Server.Tests.LogForging
{
    /// <summary>
    /// <see cref="ItemLookupController.ApplySearchCriteria"/> logs the caller's provider ids and the
    /// item name (CodeQL <c>cs/log-forging</c> #32). The endpoint requires elevation, so the caller
    /// is an administrator; the shape of the values, not the strength of the gate, is the point.
    /// </summary>
    /// <remarks>
    /// The file sink in <c>logging.json</c> renders <c>{Message}</c> without <c>j</c>: a
    /// destructured dictionary keeps raw CR/LF there, while the console's <c>{Message:lj}</c>
    /// escapes it. Both templates are probed, and the old shape is driven red on the file template
    /// so the one-line assertions are not vacuous. Closure of #32 is the default-branch CodeQL run.
    /// </remarks>
    public sealed class ItemLookupControllerProviderIdsLogTests
    {
        private const string Hostile = "tt123\r\n" + RealFormatterLogProbe.ForgedRecordPrefix;

        [Fact]
        public void Controller_StillRequiresElevation()
        {
            var attribute = typeof(ItemLookupController)
                .GetMethod(nameof(ItemLookupController.ApplySearchCriteria))!
                .GetCustomAttributes<AuthorizeAttribute>()
                .Single();
            Assert.Equal(Tesserafin.Common.Api.Policies.RequiresElevation, attribute.Policy);
        }

        [Fact]
        public void DestructuredHostileProviderIds_SplitTheFileRecord()
        {
            using var probe = RealFormatterLogProbe.File();

            probe.LoggerFor<ItemLookupController>().LogInformation(
                "Setting provider id's to item {ItemId}-{ItemName}: {@ProviderIds}",
                Guid.NewGuid(),
                "Book",
                new Dictionary<string, string> { ["Imdb"] = Hostile });

            Assert.True(probe.Lines().Length > 1);
        }

        [Theory]
        [InlineData("file")]
        [InlineData("text")]
        public async Task HostileProviderIdsAndName_WriteExactlyOneRecord(string template)
        {
            using var probe = template == "file" ? RealFormatterLogProbe.File() : RealFormatterLogProbe.Text();
            var ids = new Dictionary<string, string> { ["Imdb"] = Hostile, [Hostile] = "1" };

            await Apply(probe, Hostile, ids);

            Assert.Single(probe.Lines());
            Assert.DoesNotContain('\r', probe.Raw);
            Assert.Contains(RealFormatterLogProbe.ForgedRecordPrefix, probe.Raw, StringComparison.Ordinal);

            // The log got a rendering; the item keeps the caller's dictionary.
            Assert.Equal(Hostile, ids["Imdb"]);
        }

        [Fact]
        public async Task HostileProviderIds_WriteExactlyOneJsonRecord()
        {
            using var probe = RealFormatterLogProbe.Json();

            await Apply(probe, Hostile, new Dictionary<string, string> { ["Imdb"] = Hostile });

            Assert.Single(probe.Lines());
        }

        [Fact]
        public async Task OrdinaryValues_StayReadable()
        {
            using var probe = RealFormatterLogProbe.File();

            await Apply(probe, "Dune", new Dictionary<string, string> { ["Imdb"] = "tt0087182" });

            var line = Assert.Single(probe.Lines());
            Assert.Contains("Dune", line, StringComparison.Ordinal);
            Assert.Contains("tt0087182", line, StringComparison.Ordinal);
        }

        [Fact]
        public async Task NullProviderIds_LogsOneRecord()
        {
            using var probe = RealFormatterLogProbe.File();

            await Apply(probe, "Dune", null!);

            Assert.Single(probe.Lines());
        }

        private static async Task Apply(RealFormatterLogProbe probe, string name, Dictionary<string, string> ids)
        {
            var item = new Book { Id = Guid.NewGuid(), Name = name };
            var library = new Mock<ILibraryManager>();
            library.Setup(l => l.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<Guid>())).Returns(item);
            var providers = new Mock<IProviderManager>();
            providers
                .Setup(p => p.RefreshFullItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var controller = new ItemLookupController(
                providers.Object,
                Mock.Of<IFileSystem>(),
                library.Object,
                probe.LoggerFor<ItemLookupController>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
                }
            };

            var result = await controller.ApplySearchCriteria(item.Id, new RemoteSearchResult { ProviderIds = ids });

            Assert.IsType<NoContentResult>(result);
        }
    }
}
