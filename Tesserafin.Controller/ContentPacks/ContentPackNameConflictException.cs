using System;

namespace Tesserafin.Controller.ContentPacks;

/// <summary>
/// Thrown when a content pack name collides with an existing pack.
/// </summary>
/// <remarks>
/// Raised both by the read-before-write check and by translating the unique-index violation that
/// decides a genuine race. Only a violation confirmed by re-reading the offending name is
/// translated; any other database failure propagates unchanged.
/// </remarks>
public class ContentPackNameConflictException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ContentPackNameConflictException"/> class.
    /// </summary>
    public ContentPackNameConflictException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentPackNameConflictException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public ContentPackNameConflictException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentPackNameConflictException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ContentPackNameConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
