using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Reefin.Controller.Library;
using Reefin.Controller.Persistence;

namespace Reefin.Server.Core.Library.Validators;

/// <summary>
/// Class ArtistsPostScanTask.
/// </summary>
public class ArtistsPostScanTask : ILibraryPostScanTask
{
    /// <summary>
    /// The _library manager.
    /// </summary>
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ArtistsValidator> _logger;
    private readonly IItemRepository _itemRepo;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtistsPostScanTask" /> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="itemRepo">The item repository.</param>
    public ArtistsPostScanTask(
        ILibraryManager libraryManager,
        ILogger<ArtistsValidator> logger,
        IItemRepository itemRepo)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _itemRepo = itemRepo;
    }

    /// <summary>
    /// Runs the specified progress.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Task.</returns>
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return new ArtistsValidator(_libraryManager, _logger, _itemRepo).Run(progress, cancellationToken);
    }
}
