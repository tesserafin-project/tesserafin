using System.Threading.Tasks;
using Xunit;

namespace Reefin.Server.Integration.Tests
{
    /// <summary>
    /// Surface HTTP du point de terminaison de découverte <c>/api-docs/openapi.json</c>.
    ///
    /// <para>
    /// Ce test n'est PAS une source de contrat. Il l'a été : il écrivait la réponse brute
    /// dans son répertoire <c>bin/</c>, et <c>.github/workflows/openapi-generate.yml</c>
    /// téléversait ce fichier comme s'il était le contrat. Cet artefact était dépendant de
    /// l'hôte (<c>servers</c> réécrit depuis l'en-tête <c>Host</c>, ordre des clés non
    /// déterministe), donc différent du contrat canonique commité — issue #48. Le rôle de
    /// génération a été retiré ; le générateur unique est <c>./ci/openapi-generate.sh</c>.
    /// </para>
    ///
    /// <para>
    /// Ce qui reste ici ne fait doublon avec rien : <see cref="OpenApiContractTests"/> appelle
    /// bien le même point de terminaison, mais n'y vérifie que <c>EnsureSuccessStatusCode</c> —
    /// le type de média servi n'est asserté nulle part ailleurs. C'est la même convention que
    /// le reste de la suite d'intégration (cf. <c>BrandingControllerTests</c>,
    /// <c>DashboardControllerTests</c>).
    /// </para>
    /// </summary>
    public sealed class OpenApiSpecTests : IClassFixture<ReefinApplicationFactory>
    {
        private readonly ReefinApplicationFactory _factory;

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenApiSpecTests"/> class.
        /// </summary>
        /// <param name="factory">The application factory.</param>
        public OpenApiSpecTests(ReefinApplicationFactory factory)
        {
            _factory = factory;
        }

        /// <summary>
        /// Le point de terminaison de découverte répond 2xx et sert du JSON UTF-8.
        /// </summary>
        /// <returns>A task.</returns>
        [Fact]
        public async Task GetSpec_ReturnsCorrectResponse()
        {
            // Arrange
            var client = _factory.CreateClient();

            // Act
            var response = await client.GetAsync("/api-docs/openapi.json", TestContext.Current.CancellationToken);

            // Assert
            response.EnsureSuccessStatusCode();
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        }
    }
}
