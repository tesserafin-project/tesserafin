using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.AspNetCore.Http;
using Moq;
using Reefin.Api.Constants;
using Reefin.Common.Configuration;
using Reefin.Data;
using Reefin.Database.Implementations.Entities;
using Reefin.Database.Implementations.Enums;
using Reefin.Server.Implementations.Users;
using AccessSchedule = Reefin.Database.Implementations.Entities.AccessSchedule;

namespace Reefin.Api.Tests
{
    public static class TestHelpers
    {
        public static ClaimsPrincipal SetupUser(
            Mock<IUserManager> userManagerMock,
            Mock<IHttpContextAccessor> httpContextAccessorMock,
            string role,
            IEnumerable<AccessSchedule>? accessSchedules = null)
        {
            var user = new User(
                "reefin",
                typeof(DefaultAuthenticationProvider).FullName!,
                typeof(DefaultPasswordResetProvider).FullName!);

            user.AddDefaultPermissions();
            user.AddDefaultPreferences();

            // Set administrator flag.
            user.SetPermission(PermissionKind.IsAdministrator, role.Equals(UserRoles.Administrator, StringComparison.OrdinalIgnoreCase));

            // Add access schedules if set.
            if (accessSchedules is not null)
            {
                foreach (var accessSchedule in accessSchedules)
                {
                    user.AccessSchedules.Add(accessSchedule);
                }
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.Role, role),
                new Claim(ClaimTypes.Name, "reefin"),
                new Claim(InternalClaimTypes.UserId, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                new Claim(InternalClaimTypes.DeviceId, Guid.Empty.ToString("N", CultureInfo.InvariantCulture)),
                new Claim(InternalClaimTypes.Device, "test"),
                new Claim(InternalClaimTypes.Client, "test"),
                new Claim(InternalClaimTypes.Version, "test"),
                new Claim(InternalClaimTypes.Token, "test"),
            };

            var identity = new ClaimsIdentity(claims);

            userManagerMock
                .Setup(u => u.GetUserById(It.IsAny<Guid>()))
                .Returns(user);

            httpContextAccessorMock
                .Setup(h => h.HttpContext!.Connection.RemoteIpAddress)
                .Returns(new IPAddress(0));

            return new ClaimsPrincipal(identity);
        }

        public static void SetupConfigurationManager(in Mock<IConfigurationManager> configurationManagerMock, bool startupWizardCompleted)
        {
            var commonConfiguration = new BaseApplicationConfiguration
            {
                IsStartupWizardCompleted = startupWizardCompleted
            };

            configurationManagerMock
                .Setup(c => c.CommonConfiguration)
                .Returns(commonConfiguration);
        }
    }
}
