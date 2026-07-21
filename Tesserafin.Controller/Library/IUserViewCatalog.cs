using Tesserafin.Controller.Entities;
using Tesserafin.Model.Library;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Composed leaf port mirroring <see cref="IUserViewManager.GetUserViews"/> - the single
    /// user-views listing entry point, built entirely on the PR106-108 leaves (RFC
    /// <c>docs/rfc-di-query-user-views-v2.md</c> §9, PR109). Deliberately narrower than
    /// <see cref="IUserViewManager"/>: only <c>GetUserViews</c> is in scope for this PR -
    /// <c>GetUserSubView</c>/<c>GetLatestItems</c> stay on <see cref="IUserViewManager"/>, out of
    /// scope here.
    /// </summary>
    /// <remarks>
    /// See the concrete <c>UserViewCatalog</c> implementation (Tesserafin.Server.Core) for the RFC
    /// invariant I1 (no eager construction-graph edge to <c>ILibraryManager</c>,
    /// <see cref="IUserViewManager"/>, <c>IChannelManager</c> or <c>ILiveTvManager</c>) proof, and for
    /// a PR109 finding not anticipated by the RFC's dependency list: <c>IItemQueryService</c> (used
    /// for the playlist/boxset probe, RFC §3) is not eager-safe to inject directly here, the same
    /// trap class as <c>Lazy&lt;IProviderManager&gt;</c> (PR106b) and
    /// <c>Lazy&lt;IReadOnlyList&lt;ILiveTvService&gt;&gt;</c> (PR108). <see cref="IUserViewManager"/>
    /// keeps its own <c>GetUserViews</c> member for its own consumers; this port does not replace it
    /// (migration is PR110).
    /// </remarks>
    public interface IUserViewCatalog
    {
        /// <summary>
        /// Gets user views, identical in behavior to <c>IUserViewManager.GetUserViews</c> for the
        /// same <paramref name="query"/> (RFC §9).
        /// </summary>
        /// <param name="query">Query to use.</param>
        /// <returns>Set of folders.</returns>
        Folder[] GetUserViews(UserViewQuery query);
    }
}
