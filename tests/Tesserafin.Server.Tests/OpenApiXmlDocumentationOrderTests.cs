using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tesserafin.Server.Extensions;
using Xunit;

namespace Tesserafin.Server.Tests
{
    /// <summary>
    /// The cross-machine half of the OpenAPI contract guard (#94, [C1.1]).
    ///
    /// <para>
    /// The generated contract used to depend on the order in which the server's XML documentation
    /// files were handed to Swashbuckle. <c>Directory.EnumerateFiles</c> returns them in filesystem
    /// order, which is unspecified and differs between hosts, and for a property whose type is a
    /// <c>$ref</c> the last registered file carrying a comment for that member wins. The same
    /// commit therefore produced two different documents — structurally identical, differing only
    /// in <c>description</c> strings — depending on which machine generated it, which is what kept
    /// the hosted test workflow red.
    /// </para>
    ///
    /// <para>
    /// Nothing in the existing suite fails when that ordering is removed:
    /// <c>OpenApiContractTests.Contract_IsByteIdentical_AcrossColdGenerations</c> reboots the
    /// application on one filesystem, where the enumeration order is stable, so it structurally
    /// cannot see an order that differs between hosts, and
    /// <c>CommittedContract_MatchesRunningServer</c> only notices on a runner that happens to
    /// enumerate differently — luck, not a guard. These tests are that guard: they drive the
    /// ordering directly with explicit permutations, so they fail on any machine the moment the
    /// canonical order is weakened.
    /// </para>
    /// </summary>
    public class OpenApiXmlDocumentationOrderTests
    {
        // Real assembly documentation names, deliberately including a case variation: it is what
        // separates an ordinal comparer from a culture-aware one.
        private static readonly string[] _files =
        {
            "Tesserafin.Api.xml",
            "Tesserafin.Controller.xml",
            "Tesserafin.Model.xml",
            "Tesserafin.api.xml",
        };

        [Fact]
        public void CanonicalOrder_IsIndependentOfEnumerationOrder()
        {
            var expected = ApiServiceCollectionExtensions.CanonicaliseXmlDocumentationOrder(_files);

            foreach (var permutation in Permutations(_files))
            {
                var actual = ApiServiceCollectionExtensions.CanonicaliseXmlDocumentationOrder(permutation);

                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public void CanonicalOrder_IsOrdinal()
        {
            // Hand-written, not `_files.Order(...)`: comparing the result against the same
            // expression that produces it would pass whatever the comparer was. Ordinal puts
            // 'A' (0x41) before 'C' (0x43) before 'a' (0x61), so "Tesserafin.api.xml" sorts LAST.
            // A culture-aware comparer treats case as a tertiary weight and would put it second,
            // next to "Tesserafin.Api.xml"; OrdinalIgnoreCase would call the two equal and, since
            // OrderBy is stable, leave them in input order.
            var expected = new[]
            {
                "Tesserafin.Api.xml",
                "Tesserafin.Controller.xml",
                "Tesserafin.Model.xml",
                "Tesserafin.api.xml",
            };

            var actual = ApiServiceCollectionExtensions.CanonicaliseXmlDocumentationOrder(
                new[]
                {
                    "Tesserafin.api.xml",
                    "Tesserafin.Model.xml",
                    "Tesserafin.Api.xml",
                    "Tesserafin.Controller.xml",
                });

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void XmlDocumentationFiles_ReadsTopLevelXmlInCanonicalOrder()
        {
            // The production entry point, against a real directory: proves the enumeration is
            // canonicalised where it is actually read, not only in the pure helper, and that the
            // filter is still "*.xml, top directory only".
            var directory = Directory.CreateTempSubdirectory("tesserafin-openapi-xmldoc-");

            try
            {
                foreach (var name in new[] { "Tesserafin.api.xml", "Tesserafin.Model.xml", "Tesserafin.Api.xml" })
                {
                    File.WriteAllText(Path.Combine(directory.FullName, name), "<doc />");
                }

                File.WriteAllText(Path.Combine(directory.FullName, "Tesserafin.Server.dll"), string.Empty);
                var nested = directory.CreateSubdirectory("nested");
                File.WriteAllText(Path.Combine(nested.FullName, "Tesserafin.Nested.xml"), "<doc />");

                var actual = ApiServiceCollectionExtensions.XmlDocumentationFiles(directory.FullName);

                Assert.Equal(
                    new[]
                    {
                        Path.Combine(directory.FullName, "Tesserafin.Api.xml"),
                        Path.Combine(directory.FullName, "Tesserafin.Model.xml"),
                        Path.Combine(directory.FullName, "Tesserafin.api.xml"),
                    },
                    actual);
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }

        private static IEnumerable<IReadOnlyList<string>> Permutations(IReadOnlyList<string> items)
        {
            if (items.Count <= 1)
            {
                yield return items;
                yield break;
            }

            for (var i = 0; i < items.Count; i++)
            {
                var head = items[i];
                var rest = items.Where((_, index) => index != i).ToArray();

                foreach (var tail in Permutations(rest))
                {
                    yield return tail.Prepend(head).ToArray();
                }
            }
        }
    }
}
