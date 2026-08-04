using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Tesserafin.Controller.Library;

/// <summary>
/// Resolves a caller-supplied virtual folder name to a path inside the server-controlled
/// virtual folder root.
/// </summary>
/// <remarks>
/// <para>
/// A virtual folder name is a <em>name</em>, never a location. <see cref="Path.Combine(string, string)"/>
/// cannot express that invariant: it returns its second argument verbatim when that argument is
/// rooted, and it never rejects a relative traversal sequence. Every operation that turns a caller
/// supplied name into a filesystem path must go through this type so that create, read, update,
/// rename and delete all share one rule.
/// </para>
/// <para>
/// Rejection is deliberate: a hostile name is refused, never rewritten into some other valid
/// target. Rejection messages never contain a host path.
/// </para>
/// </remarks>
public static class VirtualFolderPath
{
    /// <summary>
    /// The client-visible message used when a virtual folder name is refused.
    /// </summary>
    /// <remarks>
    /// Deliberately free of any host path so that a refusal cannot be used to probe the
    /// server's filesystem layout.
    /// </remarks>
    public const string InvalidNameMessage = "The virtual folder name is not a valid library name.";

    /// <summary>
    /// Resolves <paramref name="virtualFolderName"/> to its path inside <paramref name="rootPath"/>.
    /// </summary>
    /// <param name="rootPath">The server-controlled virtual folder root.</param>
    /// <param name="virtualFolderName">The caller-supplied virtual folder name.</param>
    /// <returns>The full path of the intended direct child of <paramref name="rootPath"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="virtualFolderName"/> does not name a direct child of <paramref name="rootPath"/>.
    /// </exception>
    public static string Resolve(string rootPath, string? virtualFolderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        if (!TryResolve(rootPath, virtualFolderName, out var resolved))
        {
            throw new ArgumentException(InvalidNameMessage, nameof(virtualFolderName));
        }

        return resolved;
    }

    /// <summary>
    /// Attempts to resolve <paramref name="virtualFolderName"/> to its path inside <paramref name="rootPath"/>.
    /// </summary>
    /// <param name="rootPath">The server-controlled virtual folder root.</param>
    /// <param name="virtualFolderName">The caller-supplied virtual folder name.</param>
    /// <param name="fullPath">The resolved full path, when the name is accepted.</param>
    /// <returns><c>true</c> if the name resolves to a direct child of the root; otherwise <c>false</c>.</returns>
    public static bool TryResolve(string rootPath, string? virtualFolderName, [NotNullWhen(true)] out string? fullPath)
    {
        fullPath = null;

        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(virtualFolderName))
        {
            return false;
        }

        var name = virtualFolderName;

        // A name may not address a location. '/' and '\' are the only directory separators on any
        // platform this runs on, and both are refused everywhere so that a Windows-style separator
        // cannot be smuggled through a Linux host.
        if (name.AsSpan().IndexOfAny('/', '\\') >= 0)
        {
            return false;
        }

        if (Path.IsPathRooted(name))
        {
            return false;
        }

        // "." and ".." and any other all-dot name select a directory rather than name one.
        if (name.Trim('.').Length == 0)
        {
            return false;
        }

        string root;
        string candidate;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(name, root));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException)
        {
            return false;
        }

        // Containment is checked on a canonical path-component boundary: the resolved path's
        // parent must be the root exactly. This refuses relative traversal, nested paths and
        // sibling directories whose name merely starts with the root's name.
        if (!string.Equals(Path.GetDirectoryName(candidate), root, StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(candidate), name, StringComparison.Ordinal))
        {
            return false;
        }

        // Path resolution above is purely lexical and cannot see through a link. A virtual folder
        // the server created is a real directory, so a name landing on a link is refused rather
        // than followed out of the root.
        if (IsLink(candidate))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private static bool IsLink(string path)
    {
        try
        {
            // LinkTarget is populated from the entry itself, so a link whose target does not
            // exist is still recognised as a link.
            return new FileInfo(path).LinkTarget is not null
                   || new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fail closed: a name whose status cannot be established is not usable.
            return true;
        }
    }
}
