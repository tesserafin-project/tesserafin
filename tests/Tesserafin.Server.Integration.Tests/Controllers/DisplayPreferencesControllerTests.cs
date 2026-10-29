using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Tesserafin.Api.Models.UserDtos;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Dto;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.Controllers;

/// <summary>
/// Display preferences through the HTTP API: read, save and read back, as clients do.
/// </summary>
public sealed class DisplayPreferencesControllerTests : IClassFixture<TesserafinApplicationFactory>
{
    private const string Client = "emby";

    private readonly TesserafinApplicationFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;
    private static string? _accessToken;

    public DisplayPreferencesControllerTests(TesserafinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RepeatedSaves_ReplaceHomeSectionsAndCustomPreferences()
    {
        var client = await CreateAdministratorClientAsync();
        var scope = "usersettings-" + Guid.NewGuid().ToString("N");

        var initial = await GetAsync(client, scope);
        Assert.Equal("SortName", initial.SortBy);
        Assert.DoesNotContain(initial.CustomPrefs.Keys, key => key.StartsWith("homesection", StringComparison.Ordinal));

        await SaveAsync(client, scope, "DateCreated", ["latestmedia", "resume", "nextup"], new() { ["theme"] = "dark", ["landing-movies"] = null });
        var first = await GetAsync(client, scope);
        Assert.Equal("DateCreated", first.SortBy);
        Assert.Equal(["latestmedia", "resume", "nextup"], HomeSections(first));
        Assert.Equal("dark", first.CustomPrefs["theme"]);

        // A later save with fewer sections and other custom keys replaces both sets.
        await SaveAsync(client, scope, "PremiereDate", ["livetv"], new() { ["layout"] = "list" });
        var second = await GetAsync(client, scope);
        Assert.Equal("PremiereDate", second.SortBy);
        Assert.Equal(["livetv"], HomeSections(second));
        Assert.Equal("list", second.CustomPrefs["layout"]);
        Assert.DoesNotContain("theme", second.CustomPrefs.Keys);

        // Saving the same values again changes nothing.
        await SaveAsync(client, scope, "PremiereDate", ["livetv"], new() { ["layout"] = "list" });
        var third = await GetAsync(client, scope);
        Assert.Equal(HomeSections(second), HomeSections(third));
        Assert.Equal(second.CustomPrefs, third.CustomPrefs);
    }

    [Fact]
    public async Task Users_KeepTheirOwnPreferencesForTheSameScope()
    {
        var administrator = await CreateAdministratorClientAsync();
        var other = await CreateUserClientAsync(administrator);
        var scope = "usersettings-" + Guid.NewGuid().ToString("N");

        await SaveAsync(administrator, scope, "DateCreated", ["resume", "nextup"], new() { ["owner"] = "administrator" });
        var untouched = await GetAsync(other, scope);
        Assert.Equal("SortName", untouched.SortBy);
        Assert.Empty(HomeSections(untouched));
        Assert.DoesNotContain("owner", untouched.CustomPrefs.Keys);

        await SaveAsync(other, scope, "Random", ["livetv"], new() { ["owner"] = "other" });
        var administratorView = await GetAsync(administrator, scope);
        Assert.Equal("DateCreated", administratorView.SortBy);
        Assert.Equal(["resume", "nextup"], HomeSections(administratorView));
        Assert.Equal("administrator", administratorView.CustomPrefs["owner"]);

        var otherView = await GetAsync(other, scope);
        Assert.Equal("Random", otherView.SortBy);
        Assert.Equal(["livetv"], HomeSections(otherView));
        Assert.Equal("other", otherView.CustomPrefs["owner"]);

        // A user who is not an administrator cannot name another user.
        var administratorId = (await AuthHelper.GetUserDtoAsync(administrator)).Id;
        using var forbidden = await other.GetAsync(
            $"DisplayPreferences/{scope}?client={Client}&userId={administratorId:N}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task ConcurrentRequests_ShareOneScopeAndLeaveOneCompleteSave()
    {
        var client = await CreateAdministratorClientAsync();
        var scope = "usersettings-" + Guid.NewGuid().ToString("N");

        // First reads of a scope nobody has read yet, all at once.
        var reads = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetAsync(
            $"DisplayPreferences/{scope}?client={Client}",
            TestContext.Current.CancellationToken)));
        Assert.All(reads, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        foreach (var response in reads)
        {
            response.Dispose();
        }

        // Saves of one scope, all at once: writer k sends k + 1 sections of its own kind.
        string[] kinds = ["resume", "nextup", "latestmedia", "livetv", "librarybuttons", "smalllibrarytiles"];
        await Task.WhenAll(kinds.Select((kind, writer) => SaveAsync(
            client,
            scope,
            "SortName",
            Enumerable.Repeat(kind, writer + 1).ToArray(),
            new() { ["writer"] = kind })));

        var saved = await GetAsync(client, scope);
        var sections = HomeSections(saved);
        var winner = Assert.Single(sections.Distinct());
        Assert.Equal(Array.IndexOf(kinds, winner) + 1, sections.Length);
    }

    [Fact]
    public async Task SavedPreferences_SurviveAServerRestart()
    {
        // A server of its own, stopped and started again on the same data directory: what a client
        // reads after the restart comes from the SQLite file, not from a cache of the first process.
        var root = Path.Combine(Path.GetTempPath(), "tesserafin-prefs-restart-" + Guid.NewGuid().ToString("N"));
        string[] scopes = ["usersettings-" + Guid.NewGuid().ToString("N"), "usersettings-" + Guid.NewGuid().ToString("N")];
        try
        {
            string token;
            DisplayPreferencesDto[] before;
            using (var first = new FixedRootFactory(root))
            {
                var client = first.CreateClient();
                token = await AuthHelper.CompleteStartupAsync(client);
                client.DefaultRequestHeaders.AddAuthHeader(token);
                await SaveAsync(client, scopes[0], "DateCreated", ["latestmedia", "resume"], new() { ["theme"] = "dark" });

                // The last save before the stop is a replacement, concurrent with a save of another scope.
                await Task.WhenAll(
                    SaveAsync(client, scopes[0], "PremiereDate", ["nextup"], new() { ["layout"] = "list" }),
                    SaveAsync(client, scopes[1], "Random", ["livetv", "resume"], new() { ["owner"] = "second" }));
                before = await Task.WhenAll(scopes.Select(scope => GetAsync(client, scope)));
            }

            SqliteConnection.ClearAllPools();
            using (var second = new FixedRootFactory(root))
            {
                var client = second.CreateClient();
                client.DefaultRequestHeaders.AddAuthHeader(token);
                var after = await Task.WhenAll(scopes.Select(scope => GetAsync(client, scope)));

                Assert.Equal("PremiereDate", after[0].SortBy);
                Assert.Equal(["nextup"], HomeSections(after[0]));
                Assert.Equal("list", after[0].CustomPrefs["layout"]);
                Assert.DoesNotContain("theme", after[0].CustomPrefs.Keys);
                Assert.Equal(["livetv", "resume"], HomeSections(after[1]));
                for (var index = 0; index < scopes.Length; index++)
                {
                    Assert.Equal(before[index].SortBy, after[index].SortBy);
                    Assert.Equal(before[index].CustomPrefs, after[index].CustomPrefs);
                }

                // The restarted server still writes: a save and a read back on the reopened file.
                await SaveAsync(client, scopes[0], "SortName", ["resume"], new() { ["after"] = "restart" });
                var saved = await GetAsync(client, scopes[0]);
                Assert.Equal(["resume"], HomeSections(saved));
                Assert.Equal("restart", saved.CustomPrefs["after"]);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static string[] HomeSections(DisplayPreferencesDto preferences)
        => preferences.CustomPrefs
            .Where(pref => pref.Key.StartsWith("homesection", StringComparison.Ordinal))
            .OrderBy(pref => int.Parse(pref.Key["homesection".Length..], System.Globalization.CultureInfo.InvariantCulture))
            .Select(pref => pref.Value!)
            .ToArray();

    private async Task<DisplayPreferencesDto> GetAsync(HttpClient client, string scope)
    {
        using var response = await client.GetAsync($"DisplayPreferences/{scope}?client={Client}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preferences = await response.Content.ReadFromJsonAsync<DisplayPreferencesDto>(_jsonOptions, TestContext.Current.CancellationToken);
        return preferences!;
    }

    private async Task SaveAsync(HttpClient client, string scope, string sortBy, string[] homeSections, Dictionary<string, string?> custom)
    {
        var body = new DisplayPreferencesDto { SortBy = sortBy, Client = Client };
        for (var order = 0; order < homeSections.Length; order++)
        {
            body.CustomPrefs["homesection" + order] = homeSections[order];
        }

        foreach (var (key, value) in custom)
        {
            body.CustomPrefs[key] = value;
        }

        using var response = await client.PostAsJsonAsync($"DisplayPreferences/{scope}?client={Client}", body, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<HttpClient> CreateAdministratorClientAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));
        return client;
    }

    private async Task<HttpClient> CreateUserClientAsync(HttpClient administrator)
    {
        var name = "prefs-" + Guid.NewGuid().ToString("N")[..8];
        using (var created = await administrator.PostAsJsonAsync("Users/New", new CreateUserByName { Name = name }, _jsonOptions, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        // Its own device: signing in on the administrator's device would end the administrator's session.
        var authorization = AuthHelper.DummyAuthHeader.Replace("69420", Guid.NewGuid().ToString("N"), StringComparison.Ordinal);
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, authorization);
        request.Content = JsonContent.Create(new AuthenticateUserByName { Username = name, Pw = string.Empty }, options: _jsonOptions);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var auth = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);
        var token = auth.RootElement.GetProperty("AccessToken").GetString();
        client.DefaultRequestHeaders.Add(AuthHelper.AuthHeaderName, authorization + $", Token={token}");
        return client;
    }

    /// <summary>
    /// A server whose data directory is chosen by the test, so that a second one can reopen it.
    /// </summary>
    private sealed class FixedRootFactory : TesserafinApplicationFactory
    {
        private readonly string _root;

        public FixedRootFactory(string root)
        {
            _root = root;
        }

        protected override string CreateWebHostPathRoot() => _root;
    }
}
