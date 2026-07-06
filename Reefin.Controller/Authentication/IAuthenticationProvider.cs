#pragma warning disable CS1591

using System.Threading.Tasks;
using Reefin.Database.Implementations.Entities;
using Reefin.Model.Users;

namespace Reefin.Controller.Authentication
{
    public interface IAuthenticationProvider
    {
        string Name { get; }

        bool IsEnabled { get; }

        Task<ProviderAuthenticationResult> Authenticate(string username, string password);

        Task ChangePassword(User user, string newPassword);
    }

    public interface IRequiresResolvedUser
    {
        Task<ProviderAuthenticationResult> Authenticate(string username, string password, User? resolvedUser);
    }

    public interface IHasNewUserPolicy
    {
        UserPolicy GetNewUserPolicy();
    }

    public class ProviderAuthenticationResult
    {
        public required string Username { get; set; }

        public string? DisplayName { get; set; }
    }
}
