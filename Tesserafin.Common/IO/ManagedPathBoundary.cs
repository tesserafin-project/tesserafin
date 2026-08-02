using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Tesserafin.Common.IO;

/// <summary>
/// The shared contract for reading from, and writing beneath, a directory root that the server owns.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SafeDirectoryLeafName"/> proves that a composed path is spelled as a child of an
/// intended root. That proof is purely lexical: <see cref="Path.GetFullPath(string)"/> collapses
/// <c>.</c> and <c>..</c> but does not resolve symbolic links or reparse points, so a path that is
/// canonically inside a managed root can still resolve, at open time, to an object outside every
/// root the server owns.
/// </para>
/// <para>
/// This contract closes that gap by refusing to traverse links. Every path component below the
/// managed root — not only the final one — must be an ordinary directory or an ordinary file. A
/// symbolic link in any position is rejected, whether it resolves, dangles, or points back inside
/// the root.
/// </para>
/// <para>
/// The managed root itself is deliberately never inspected. A deployment is free to mount the
/// configuration, data or backup root through a link; that placement is an operator decision and is
/// the trust anchor, not an escape. Only components the server or its callers put <em>beneath</em>
/// that anchor are checked.
/// </para>
/// <para>
/// <strong>Residual race.</strong> The target runtime exposes no no-follow open. <see cref="FileOptions"/>
/// has no equivalent of <c>O_NOFOLLOW</c>, and <see cref="File.OpenHandle(string, FileMode, FileAccess, FileShare, FileOptions, long)"/>
/// resolves links exactly as <see cref="File.OpenRead(string)"/> does, so a handle obtained first
/// cannot be validated afterwards either. Every check here is therefore check-then-use, and a
/// component replaced with a link between the check and the subsequent open or extract would not be
/// detected. That residual is accepted rather than eliminated because creating the link in the first
/// place already requires write access to a server-owned root — that is, authority equivalent to the
/// server process or to the host administrator of the mounted volume. Eliminating it would require
/// platform interop for <c>openat</c>. This contract is defence in depth against a link that is
/// already present; it is not atomic.
/// </para>
/// </remarks>
public static class ManagedPathBoundary
{
    /// <summary>
    /// Determines whether <paramref name="candidatePath"/> is an existing ordinary file reached from
    /// <paramref name="managedRoot"/> without traversing a link.
    /// </summary>
    /// <param name="managedRoot">The server-managed root directory. Must not be supplied by a caller.</param>
    /// <param name="candidatePath">The path to validate.</param>
    /// <param name="resolvedPath">
    /// When this method returns <c>true</c>, the canonical path assembled component by component by
    /// this contract. Callers use that value rather than their own input so the path they act on is
    /// derived from the check.
    /// </param>
    /// <returns>
    /// <c>true</c> if the path is canonically contained by the root, every component below the root
    /// is link free, and the final component is an existing ordinary file; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// A dangling link is rejected: on the supported platforms a broken link still reports
    /// <see cref="FileAttributes.ReparsePoint"/>, so it fails the component check rather than the
    /// existence check.
    /// </remarks>
    public static bool TryResolveContainedFile(string managedRoot, string? candidatePath, [NotNullWhen(true)] out string? resolvedPath)
    {
        resolvedPath = null;

        if (!TryGetSegments(managedRoot, candidatePath, out var current, out var segments))
        {
            return false;
        }

        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            var last = i == segments.Length - 1;

            if (!TryGetAttributes(current, out var attributes))
            {
                return false;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (last ? isDirectory : !isDirectory)
            {
                return false;
            }
        }

        resolvedPath = current;
        return true;
    }

    /// <summary>
    /// Determines whether <paramref name="candidatePath"/> is an existing ordinary file reached from
    /// <paramref name="managedRoot"/> without traversing a link.
    /// </summary>
    /// <param name="managedRoot">The server-managed root directory. Must not be supplied by a caller.</param>
    /// <param name="candidatePath">The path to validate.</param>
    /// <returns><c>true</c> if the path satisfies the contract; otherwise <c>false</c>.</returns>
    public static bool IsContainedFile(string managedRoot, string? candidatePath)
        => TryResolveContainedFile(managedRoot, candidatePath, out _);

    /// <summary>
    /// Prepares <paramref name="candidatePath"/> to be written beneath <paramref name="managedRoot"/>,
    /// creating any missing intermediate directories without traversing or replacing a link.
    /// </summary>
    /// <param name="managedRoot">The server-managed root directory. Must not be supplied by a caller.</param>
    /// <param name="candidatePath">The path that is about to be written.</param>
    /// <param name="resolvedPath">
    /// When this method returns <c>true</c>, the canonical destination assembled component by
    /// component by this contract.
    /// </param>
    /// <returns>
    /// <c>true</c> if the path is canonically contained by the root, every existing component below
    /// the root is link free, and the final component is either absent or an existing ordinary file;
    /// otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// A final component that already exists as a link is rejected rather than followed, so an
    /// overwriting write can never be redirected through a link that was planted in the destination.
    /// Directories are created one component at a time; <see cref="Directory.CreateDirectory(string)"/>
    /// applied to the whole chain would silently traverse a linked parent.
    /// </remarks>
    public static bool TryPrepareWriteTarget(string managedRoot, string? candidatePath, [NotNullWhen(true)] out string? resolvedPath)
    {
        resolvedPath = null;

        if (!TryGetSegments(managedRoot, candidatePath, out var current, out var segments))
        {
            return false;
        }

        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            var last = i == segments.Length - 1;

            if (TryGetAttributes(current, out var attributes))
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (last ? isDirectory : !isDirectory)
                {
                    return false;
                }

                continue;
            }

            if (last)
            {
                // The destination file does not exist yet, which is the ordinary case.
                break;
            }

            try
            {
                Directory.CreateDirectory(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }
        }

        resolvedPath = current;
        return true;
    }

    /// <summary>
    /// Validates <paramref name="candidatePath"/> as an existing ordinary file beneath
    /// <paramref name="managedRoot"/> and returns its canonical path.
    /// </summary>
    /// <param name="managedRoot">The server-managed root directory. Must not be supplied by a caller.</param>
    /// <param name="candidatePath">The path to validate.</param>
    /// <param name="parameterName">The name of the caller's parameter that produced <paramref name="candidatePath"/>.</param>
    /// <returns>The canonical full path of the validated file.</returns>
    /// <exception cref="ArgumentException">The path is not a link-free existing file inside the root.</exception>
    /// <remarks>
    /// The message deliberately names neither the root nor the candidate, so a rejection cannot be
    /// used to probe the host filesystem layout.
    /// </remarks>
    public static string ValidateContainedFile(string managedRoot, string? candidatePath, string parameterName)
    {
        if (!TryResolveContainedFile(managedRoot, candidatePath, out var resolvedPath))
        {
            throw new ArgumentException(
                "The value does not resolve to a usable file inside the intended directory.",
                parameterName);
        }

        return resolvedPath;
    }

    private static bool TryGetSegments(string managedRoot, string? candidatePath, out string canonicalRoot, out string[] segments)
    {
        canonicalRoot = string.Empty;
        segments = [];

        if (string.IsNullOrWhiteSpace(managedRoot) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        string candidate;
        try
        {
            canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedRoot));
            candidate = Path.GetFullPath(candidatePath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        var prefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        segments = candidate[prefix.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return segments.Length > 0;
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Fail closed: an attribute read that cannot be completed is not a proof of safety.
            attributes = FileAttributes.ReparsePoint;
            return true;
        }
    }
}
