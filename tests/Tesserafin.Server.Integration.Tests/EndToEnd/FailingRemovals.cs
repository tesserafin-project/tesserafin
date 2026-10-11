using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.Configuration;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.IO;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// The real file system, whose deferred removals can be made to fail.
/// </summary>
public sealed class FailingRemovals : ManagedFileSystem
{
    private readonly IHttpContextAccessor _httpContext;
    private int _failed;

    /// <summary>
    /// Initializes a new instance of the <see cref="FailingRemovals"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="shortcutHandlers">The shortcut handlers.</param>
    /// <param name="httpContext">The request in progress, if any.</param>
    public FailingRemovals(
        ILogger<ManagedFileSystem> logger,
        IApplicationPaths applicationPaths,
        IEnumerable<IShortcutHandler> shortcutHandlers,
        IHttpContextAccessor httpContext)
        : base(logger, applicationPaths, shortcutHandlers)
    {
        _httpContext = httpContext;
    }

    /// <summary>
    /// Gets or sets the directory in which a removal made outside a segment request fails.
    /// </summary>
    public string? FailDeferredRemovalsUnder { get; set; }

    /// <summary>
    /// Gets the number of removals that were made to fail.
    /// </summary>
    public int Failed => Volatile.Read(ref _failed);

    /// <inheritdoc/>
    public override void DeleteFile(string path)
    {
        if (FailDeferredRemovalsUnder is { } directory
            && path.StartsWith(directory, StringComparison.Ordinal)
            && !IsASegmentRequest())
        {
            Interlocked.Increment(ref _failed);
            throw new IOException("The file is in use: " + path);
        }

        base.DeleteFile(path);
    }

    private bool IsASegmentRequest()
    {
        try
        {
            return _httpContext.HttpContext?.Request.Path.Value?.Contains("/hls1/", StringComparison.Ordinal) == true;
        }
        catch (ObjectDisposedException)
        {
            // The request that scheduled the removal is over.
            return false;
        }
    }
}
