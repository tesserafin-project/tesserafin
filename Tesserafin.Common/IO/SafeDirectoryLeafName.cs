using System;
using System.IO;

namespace Tesserafin.Common.IO;

/// <summary>
/// The shared contract for a leaf name that is combined with a server-managed root directory.
/// </summary>
/// <remarks>
/// <para>
/// A leaf name is the single final path component of a directory or file that the server creates
/// inside a root it owns. It is never a path. Callers that compose a name supplied from outside the
/// server — a playlist name, a collection name, an item-by-name value taken from media metadata —
/// must reduce that name to a leaf and then combine it through <see cref="CombineWithRoot"/> so the
/// result is provably a direct child of the intended root.
/// </para>
/// <para>
/// The contract deliberately rejects rather than repairs. Silently rewriting a rejected name would
/// let two distinct inputs collapse onto one directory, which is the failure mode this contract
/// exists to prevent.
/// </para>
/// </remarks>
public static class SafeDirectoryLeafName
{
    /// <summary>
    /// Determines whether <paramref name="name"/> satisfies the leaf-name contract.
    /// </summary>
    /// <param name="name">The candidate leaf name.</param>
    /// <returns><c>true</c> if the name is a valid leaf name; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// A valid leaf name is non-empty, is not composed only of white space, is not composed only of
    /// <c>.</c> characters, contains no directory separator and no volume separator, is not rooted,
    /// and is equal to its own <see cref="Path.GetFileName(string)"/>. Unicode letters, spaces and
    /// ordinary punctuation are all valid.
    /// </remarks>
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (IsDotOnly(name))
        {
            return false;
        }

        if (name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || name.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || name.Contains(':', StringComparison.Ordinal)
            || name.Contains(Path.VolumeSeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        if (Path.IsPathRooted(name) || Path.IsPathFullyQualified(name))
        {
            return false;
        }

        // Any residual separator handling the checks above did not cover is caught here:
        // a leaf name is by definition its own file name.
        return string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Validates <paramref name="name"/> against the leaf-name contract and returns it.
    /// </summary>
    /// <param name="name">The candidate leaf name.</param>
    /// <param name="parameterName">The name of the caller's parameter that produced <paramref name="name"/>.</param>
    /// <returns>The validated leaf name.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid leaf name.</exception>
    public static string Validate(string? name, string parameterName)
    {
        if (!IsValid(name))
        {
            throw new ArgumentException(
                "The value does not resolve to a usable directory or file name.",
                parameterName);
        }

        // Path.GetFileName is an identity transform on a value that has already been proven to be a
        // leaf. It is applied anyway so that the returned value is derived from the sanitiser rather
        // than flowing through unchanged.
        return Path.GetFileName(name!);
    }

    /// <summary>
    /// Combines a validated leaf name with a server-managed root and proves the result is a direct
    /// child of that root.
    /// </summary>
    /// <param name="root">The server-managed root directory. Must not be supplied by a caller.</param>
    /// <param name="name">The candidate leaf name.</param>
    /// <param name="parameterName">The name of the caller's parameter that produced <paramref name="name"/>.</param>
    /// <returns>The canonical full path of the direct child.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="root"/> is empty, <paramref name="name"/> is not a valid leaf name, or the
    /// combined path does not canonically resolve to a direct child of <paramref name="root"/>.
    /// </exception>
    public static string CombineWithRoot(string root, string? name, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var leaf = Validate(name, parameterName);

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, leaf));

        var parent = Path.GetDirectoryName(candidate);
        if (parent is null
            || !string.Equals(Path.TrimEndingDirectorySeparator(parent), canonicalRoot, StringComparison.Ordinal)
            || string.Equals(candidate, canonicalRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The value does not resolve to a direct child of the intended directory.",
                parameterName);
        }

        return candidate;
    }

    private static bool IsDotOnly(string name)
    {
        foreach (var c in name)
        {
            if (c != '.')
            {
                return false;
            }
        }

        return true;
    }
}
