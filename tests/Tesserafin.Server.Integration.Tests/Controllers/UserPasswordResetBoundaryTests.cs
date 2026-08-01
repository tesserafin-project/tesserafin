using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Api.Models.UserDtos;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Users;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.Controllers
{
    /// <summary>
    /// Boundary controls for <c>POST /Users/Password</c>.
    /// <para>
    /// The request body carries a caller-controlled <see cref="UpdateUserPassword.ResetPassword"/> flag that
    /// selects between the reset branch and the branch that verifies the current password. These controls pin
    /// the property that matters: the flag is read only after the controller has already selected the target
    /// user from server-held claims and authorized the operation, so it cannot change which account is
    /// affected, cannot grant an ordinary user any authority over another account, and cannot clear an
    /// administrator password.
    /// </para>
    /// </summary>
    public sealed class UserPasswordResetBoundaryTests : IClassFixture<TesserafinApplicationFactory>
    {
        private readonly TesserafinApplicationFactory _factory;
        private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;

        private static string? _adminToken;
        private static int _userSequence;

        public UserPasswordResetBoundaryTests(TesserafinApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task Reset_Unauthenticated_Unauthorized()
        {
            var client = _factory.CreateClient();
            var victim = await CreateUserAsync();

            using var response = await client.PostAsJsonAsync(
                "Users/Password",
                new UpdateUserPassword { ResetPassword = true },
                _jsonOptions,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            await AssertPasswordIntactAsync(victim);
        }

        [Fact]
        public async Task Reset_OrdinaryUserTargetingAnotherUser_Forbidden()
        {
            var attacker = await CreateUserAsync();
            var victim = await CreateUserAsync();
            var client = await AuthenticatedClientAsync(attacker);

            using var response = await client.PostAsJsonAsync(
                "Users/Password?userId=" + victim.Id.ToString("D", CultureInfo.InvariantCulture),
                new UpdateUserPassword { ResetPassword = true },
                _jsonOptions,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await AssertPasswordIntactAsync(victim);
        }

        [Fact]
        public async Task Reset_OrdinaryUserTargetingAnotherUserOnLegacyRoute_Forbidden()
        {
            var attacker = await CreateUserAsync();
            var victim = await CreateUserAsync();
            var client = await AuthenticatedClientAsync(attacker);

            using var response = await client.PostAsJsonAsync(
                "Users/" + victim.Id.ToString("D", CultureInfo.InvariantCulture) + "/Password",
                new UpdateUserPassword { ResetPassword = true },
                _jsonOptions,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await AssertPasswordIntactAsync(victim);
        }

        [Theory]
        // Alternate GUID renderings accepted by Guid.TryParse.
        [InlineData("D")]
        [InlineData("N")]
        [InlineData("B")]
        [InlineData("P")]
        public async Task Reset_OrdinaryUserTargetingAnotherUserWithAlternateGuidFormat_NeverSucceeds(string format)
        {
            var attacker = await CreateUserAsync();
            var victim = await CreateUserAsync();
            var client = await AuthenticatedClientAsync(attacker);

            var rendered = Uri.EscapeDataString(victim.Id.ToString(format, CultureInfo.InvariantCulture).ToUpperInvariant());

            using var response = await client.PostAsJsonAsync(
                "Users/Password?userId=" + rendered,
                new UpdateUserPassword { ResetPassword = true },
                _jsonOptions,
                TestContext.Current.CancellationToken);

            Assert.NotEqual(HttpStatusCode.NoContent, response.StatusCode);
            await AssertPasswordIntactAsync(victim);
        }

        [Fact]
        public async Task Reset_OrdinaryUserWithConflictingDuplicateUserId_NeverSucceeds()
        {
            var attacker = await CreateUserAsync();
            var victim = await CreateUserAsync();
            var client = await AuthenticatedClientAsync(attacker);

            var attackerId = attacker.Id.ToString("D", CultureInfo.InvariantCulture);
            var victimId = victim.Id.ToString("D", CultureInfo.InvariantCulture);

            var statuses = new List<HttpStatusCode>();

            foreach (var query in new[]
                     {
                         $"Users/Password?userId={victimId}&userId={attackerId}",
                         $"Users/Password?userId={attackerId}&userId={victimId}",
                     })
            {
                using var response = await client.PostAsJsonAsync(
                    query,
                    new UpdateUserPassword { ResetPassword = true },
                    _jsonOptions,
                    TestContext.Current.CancellationToken);

                statuses.Add(response.StatusCode);

                // Whichever value the binder picks, the other account must never be touched.
                await AssertPasswordIntactAsync(victim);
            }

            // Duplicate scalar query values bind to the first occurrence. Naming the victim first is
            // authorized against the victim and rejected; naming the caller first stays self-scoped and
            // is then rejected for lack of credential proof.
            Assert.Equal(HttpStatusCode.Forbidden, statuses[0]);
            Assert.Equal(HttpStatusCode.Forbidden, statuses[1]);
        }

        [Fact]
        public async Task Reset_OrdinaryUserWithoutPreferenceAccess_Forbidden()
        {
            var user = await CreateUserAsync();
            var client = await AuthenticatedClientAsync(user);

            await SetPreferenceAccessAsync(user.Id, enabled: false);

            using var response = await client.PostAsJsonAsync(
                "Users/Password",
                new UpdateUserPassword { ResetPassword = true },
                _jsonOptions,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await AssertPasswordIntactAsync(user);
        }

        [Fact]
        public async Task Reset_MalformedFlag_BadRequestAndPasswordIntact()
        {
            var user = await CreateUserAsync();
            var client = await AuthenticatedClientAsync(user);

            using var content = new StringContent(
                "{\"ResetPassword\":\"true\"}",
                Encoding.UTF8,
                "application/json");

            using var response = await client.PostAsync("Users/Password", content, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertPasswordIntactAsync(user);
        }

        [Fact]
        public async Task Reset_MissingFlagWithWrongCurrentPassword_ForbiddenAndPasswordIntact()
        {
            var user = await CreateUserAsync();
            var client = await AuthenticatedClientAsync(user);

            using var content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var response = await client.PostAsync("Users/Password", content, TestContext.Current.CancellationToken);

            // The flag defaults to false, so the branch that verifies the current password is taken.
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await AssertPasswordIntactAsync(user);
        }

        [Fact]
        public async Task Reset_AdministratorOwnAccount_RejectedAndPasswordIntact()
        {
            var adminClient = _factory.CreateClient();
            adminClient.DefaultRequestHeaders.AddAuthHeader(await AdminTokenAsync());

            using var response = await adminClient.PostAsJsonAsync(
                "Users/Password",
                new UpdateUserPassword { ResetPassword = true },
                _jsonOptions,
                TestContext.Current.CancellationToken);

            // An administrator password must never become empty.
            Assert.NotEqual(HttpStatusCode.NoContent, response.StatusCode);

            var stillAdmin = _factory.CreateClient();
            stillAdmin.DefaultRequestHeaders.AddAuthHeader(await AdminTokenAsync());
            using var check = await stillAdmin.GetAsync("Users/Me", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        }

        [Fact]
        public async Task Reset_OrdinaryUserOwnAccountWithoutCredentialProof_Forbidden()
        {
            var user = await CreateUserAsync();
            var bystander = await CreateUserAsync();
            var client = await AuthenticatedClientAsync(user);

            using var response = await client.PostAsJsonAsync(
                "Users/Password",
                new UpdateUserPassword { ResetPassword = true },
                _jsonOptions,
                TestContext.Current.CancellationToken);

            // A valid access token alone must not be enough to void the account credential.
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await AssertPasswordIntactAsync(user);
            await AssertPasswordIntactAsync(bystander);
        }

        [Fact]
        public async Task Reset_OrdinaryUserOwnAccountWithWrongCredentialProof_Forbidden()
        {
            var user = await CreateUserAsync();
            var client = await AuthenticatedClientAsync(user);

            using var response = await client.PostAsJsonAsync(
                "Users/Password",
                new UpdateUserPassword { ResetPassword = true, CurrentPw = user.Password + "-wrong" },
                _jsonOptions,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await AssertPasswordIntactAsync(user);
        }

        [Fact]
        public async Task Reset_OrdinaryUserOwnAccountWithCredentialProof_SucceedsAndRevokesOtherSessions()
        {
            var user = await CreateUserAsync();
            var bystander = await CreateUserAsync();

            var tokenA = await AuthenticateAsync(user.Username, user.Password, "device-a");
            var tokenB = await AuthenticateAsync(user.Username, user.Password, "device-b");
            Assert.NotNull(tokenA);
            Assert.NotNull(tokenB);

            var clientA = _factory.CreateClient();
            clientA.DefaultRequestHeaders.AddAuthHeader(tokenA!);

            using var response = await clientA.PostAsJsonAsync(
                "Users/Password",
                new UpdateUserPassword { ResetPassword = true, CurrentPw = user.Password },
                _jsonOptions,
                TestContext.Current.CancellationToken);

            // The self-service operation still succeeds once the caller proves the credential.
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.True(await CanAuthenticateAsync(user.Username, string.Empty));

            // The credential change must not leave other sessions live.
            var clientB = _factory.CreateClient();
            clientB.DefaultRequestHeaders.AddAuthHeader(tokenB!);
            using var otherSession = await clientB.GetAsync("Users/Me", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, otherSession.StatusCode);

            // No other account is affected.
            await AssertPasswordIntactAsync(bystander);
        }

        [Fact]
        public async Task Reset_OrdinaryUserWithNoConfiguredCredential_Succeeds()
        {
            var user = await CreateUserAsync();
            var client = await AuthenticatedClientAsync(user);

            using var first = await client.PostAsJsonAsync(
                "Users/Password",
                new UpdateUserPassword { ResetPassword = true, CurrentPw = user.Password },
                _jsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

            // There is nothing left to prove, so a further reset stays available.
            var emptyToken = await AuthenticateAsync(user.Username, string.Empty);
            Assert.NotNull(emptyToken);
            var clientAfter = _factory.CreateClient();
            clientAfter.DefaultRequestHeaders.AddAuthHeader(emptyToken!);

            using var second = await clientAfter.PostAsJsonAsync(
                "Users/Password",
                new UpdateUserPassword { ResetPassword = true },
                _jsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        }

        private async Task<TestUser> CreateUserAsync()
        {
            var index = Interlocked.Increment(ref _userSequence);
            var name = "boundaryUser" + index.ToString(CultureInfo.InvariantCulture);
            var password = "Pa$$w0rd-" + index.ToString(CultureInfo.InvariantCulture);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(await AdminTokenAsync());

            using var response = await client.PostAsJsonAsync(
                "Users/New",
                new CreateUserByName { Name = name, Password = password },
                _jsonOptions,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var dto = await response.Content.ReadFromJsonAsync<UserDto>(_jsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(dto);

            return new TestUser(dto!.Id, name, password);
        }

        private async Task SetPreferenceAccessAsync(Guid userId, bool enabled)
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(await AdminTokenAsync());

            using var getResponse = await client.GetAsync(
                "Users/" + userId.ToString("D", CultureInfo.InvariantCulture),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
            var dto = await getResponse.Content.ReadFromJsonAsync<UserDto>(_jsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(dto);

            var policy = dto!.Policy ?? new UserPolicy();
            policy.EnableUserPreferenceAccess = enabled;

            using var policyResponse = await client.PostAsJsonAsync(
                "Users/" + userId.ToString("D", CultureInfo.InvariantCulture) + "/Policy",
                policy,
                _jsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, policyResponse.StatusCode);
        }

        private async Task<HttpClient> AuthenticatedClientAsync(TestUser user)
        {
            var token = await AuthenticateAsync(user.Username, user.Password);
            Assert.NotNull(token);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(token!);
            return client;
        }

        private async Task AssertPasswordIntactAsync(TestUser user)
        {
            Assert.True(
                await CanAuthenticateAsync(user.Username, user.Password),
                user.Username + " can no longer authenticate with its own password.");
            Assert.False(
                await CanAuthenticateAsync(user.Username, string.Empty),
                user.Username + " authenticated with an empty password.");
        }

        private async Task<bool> CanAuthenticateAsync(string username, string password)
            => await AuthenticateAsync(username, password) is not null;

        private async Task<string?> AuthenticateAsync(string username, string password, string? deviceId = null)
        {
            var client = _factory.CreateClient();

            var authHeader = deviceId is null
                ? AuthHelper.DummyAuthHeader
                : AuthHelper.DummyAuthHeader.Replace("DeviceId=\"69420\"", "DeviceId=\"" + deviceId + "\"", StringComparison.Ordinal);

            using var request = new HttpRequestMessage(HttpMethod.Post, "Users/AuthenticateByName");
            request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, authHeader);
            request.Content = JsonContent.Create(
                new AuthenticateUserByName { Username = username, Pw = password },
                options: _jsonOptions);

            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            return document.RootElement.GetProperty("AccessToken").GetString();
        }

        private async Task<string> AdminTokenAsync()
        {
            var client = _factory.CreateClient();
            return _adminToken ??= await AuthHelper.CompleteStartupAsync(client);
        }

        private sealed record TestUser(Guid Id, string Username, string Password);
    }
}
