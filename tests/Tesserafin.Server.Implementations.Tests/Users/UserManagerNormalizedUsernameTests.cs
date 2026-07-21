using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common;
using Tesserafin.Common.Net;
using Tesserafin.Controller;
using Tesserafin.Controller.Authentication;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Events;
using Tesserafin.Controller.Library;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Locking;
using Tesserafin.Database.Providers.Sqlite;
using Tesserafin.Model.Cryptography;
using Tesserafin.Server.Implementations.Users;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Users
{
    public sealed class UserManagerNormalizedUsernameTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<TesserafinDbContext> _dbOptions;
        private readonly UserManager _userManager;

        public UserManagerNormalizedUsernameTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();

            _dbOptions = new DbContextOptionsBuilder<TesserafinDbContext>()
                .UseSqlite(_connection)
                .Options;

            // Create the schema
            using var ctx = CreateDbContext();
            ctx.Database.EnsureCreated();

            var factory = new Mock<IDbContextFactory<TesserafinDbContext>>();
            factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
            factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateDbContext);

            var cryptoProvider = new Mock<ICryptoProvider>();
            var configManager = new Mock<IServerConfigurationManager>();
            var appPaths = new Mock<IServerApplicationPaths>();
            appPaths.Setup(x => x.ProgramDataPath).Returns(Path.GetTempPath());
            configManager.Setup(x => x.ApplicationPaths).Returns(appPaths.Object);

            var appHost = new Mock<IApplicationHost>();

            var defaultAuthProvider = new DefaultAuthenticationProvider(
                NullLogger<DefaultAuthenticationProvider>.Instance,
                cryptoProvider.Object);
            var invalidAuthProvider = new InvalidAuthProvider();
            var defaultPasswordResetProvider = new DefaultPasswordResetProvider(
                configManager.Object,
                appHost.Object);

            _userManager = new UserManager(
                factory.Object,
                new NoopEventManager(),
                new Mock<INetworkManager>().Object,
                appHost.Object,
                new Mock<IImageProcessor>().Object,
                NullLogger<UserManager>.Instance,
                configManager.Object,
                new IPasswordResetProvider[] { defaultPasswordResetProvider },
                new IAuthenticationProvider[] { defaultAuthProvider, invalidAuthProvider });
        }

        public void Dispose()
        {
            _userManager.Dispose();
            _connection.Dispose();
        }

        private TesserafinDbContext CreateDbContext()
        {
            return new TesserafinDbContext(
                _dbOptions,
                NullLogger<TesserafinDbContext>.Instance,
                new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
                new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
        }

        // ----- GetUserByName tests -----

        [Theory]
        // German umlauts
        [InlineData("münchen", "MÜNCHEN")]
        // Spanish tilde-n
        [InlineData("Ñoño", "ÑOÑO")]
        // ASCII, invariant uppercase lookup
        [InlineData("reefin", "REEFIN")]
        // Turkish cedilla: invariant 'i' uppercases to 'I' (U+0049), not Turkish 'İ' (U+0130)
        [InlineData("Çelebi", "ÇELEBI")]
        public async Task GetUserByName_WithNonAsciiUsername_FindsUserByNormalizedName(
            string username, string normalizedLookup)
        {
            await _userManager.CreateUserAsync(username);

            var found = _userManager.GetUserByName(normalizedLookup);

            Assert.NotNull(found);
            Assert.Equal(username, found.Username);
        }

        [Theory]
        // German umlaut, look up by both upper and lower case
        [InlineData("münchen")]
        // Spanish tilde-n
        [InlineData("Ñoño")]
        // lowercase 'i' — invariant ToUpperInvariant gives 'I', not Turkish 'İ'
        [InlineData("ali")]
        // mixed ASCII + umlaut
        [InlineData("testüser")]
        public async Task GetUserByName_WithVariousCase_FindsUserCaseInsensitively(string username)
        {
            await _userManager.CreateUserAsync(username);

            var upperFound = _userManager.GetUserByName(username.ToUpperInvariant());
            var lowerFound = _userManager.GetUserByName(username.ToLowerInvariant());
            var exactFound = _userManager.GetUserByName(username);

            Assert.NotNull(upperFound);
            Assert.NotNull(lowerFound);
            Assert.NotNull(exactFound);
        }

        [Theory]
        [InlineData("nonexistent")]
        // No user with NormalizedUsername = "MÜNCHEN" has been created
        [InlineData("MÜNCHEN")]
        public void GetUserByName_WhenUserDoesNotExist_ReturnsNull(string lookupName)
        {
            var result = _userManager.GetUserByName(lookupName);

            Assert.Null(result);
        }

        // ----- CreateUserAsync duplicate detection tests -----

        [Theory]
        // German umlaut, case-swapped duplicate
        [InlineData("münchen", "MÜNCHEN")]
        // Spanish tilde-n, lowercase duplicate
        [InlineData("Ñoño", "ñoño")]
        // ASCII, uppercase duplicate
        [InlineData("alice", "ALICE")]
        // Turkish cedilla: "çelebi".ToUpperInvariant() == "ÇELEBI" == "ÇELEBI".ToUpperInvariant()
        [InlineData("çelebi", "ÇELEBI")]
        public async Task CreateUserAsync_WhenNormalizedNameAlreadyExists_ThrowsArgumentException(
            string existingUsername, string duplicateUsername)
        {
            await _userManager.CreateUserAsync(existingUsername);

            await Assert.ThrowsAsync<ArgumentException>(
                () => _userManager.CreateUserAsync(duplicateUsername));
        }

        [Theory]
        // Different non-ASCII names that do not collide after normalization
        [InlineData("münchen", "münchen2")]
        [InlineData("ali", "ali2")]
        // Visually similar but different Unicode code points: ñ (U+00F1) vs n (U+006E)
        [InlineData("noño", "nono")]
        public async Task CreateUserAsync_WithDistinctNonAsciiUsernames_CreatesBothUsers(
            string firstUsername, string secondUsername)
        {
            var first = await _userManager.CreateUserAsync(firstUsername);
            var second = await _userManager.CreateUserAsync(secondUsername);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotEqual(first.Id, second.Id);
        }

        // ----- RenameUser tests -----

        [Theory]
        // Rename to non-ASCII name
        [InlineData("alice", "münchen")]
        // Rename between similar non-ASCII and ASCII
        [InlineData("müller", "mueller")]
        // Contains 'i': invariant uppercase is always 'I', never Turkish 'İ'
        [InlineData("ali", "ALI2")]
        // Rename to Spanish tilde-n name
        [InlineData("testuser", "Ñoño")]
        public async Task RenameUser_SetsNormalizedUsernameToUpperInvariant(
            string originalName, string newName)
        {
            var user = await _userManager.CreateUserAsync(originalName);

            await _userManager.RenameUser(user.Id, originalName, newName);

            var renamed = _userManager.GetUserById(user.Id);
            Assert.NotNull(renamed);
            Assert.Equal(newName, renamed.Username);
            Assert.Equal(newName.ToUpperInvariant(), renamed.NormalizedUsername);
        }

        [Theory]
        // Same name different case: NormalizedUsername already taken
        [InlineData("münchen", "MÜNCHEN")]
        // Spanish, lowercase conflicts with existing uppercase-normalised entry
        [InlineData("Ñoño", "ñoño")]
        // ASCII, capitalised conflict
        [InlineData("alice", "Alice")]
        // Mixed ASCII + umlaut
        [InlineData("testüser", "TESTÜSER")]
        public async Task RenameUser_WhenNormalizedNameConflictsWithExistingUser_ThrowsArgumentException(
            string existingUsername, string conflictingNewName)
        {
            var targetUser = await _userManager.CreateUserAsync("renametarget");
            await _userManager.CreateUserAsync(existingUsername);

            await Assert.ThrowsAsync<ArgumentException>(
                () => _userManager.RenameUser(targetUser.Id, "renametarget", conflictingNewName));
        }

        private sealed class NoopEventManager : IEventManager
        {
            public void Publish<T>(T eventArgs)
                where T : EventArgs
            {
            }

            public Task PublishAsync<T>(T eventArgs)
                where T : EventArgs
                => Task.CompletedTask;
        }
    }
}
