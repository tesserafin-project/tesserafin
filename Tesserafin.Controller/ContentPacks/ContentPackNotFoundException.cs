using System;

namespace Tesserafin.Controller.ContentPacks;

/// <summary>
/// Thrown when an operation names a content pack that does not exist.
/// </summary>
public class ContentPackNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ContentPackNotFoundException"/> class.
    /// </summary>
    public ContentPackNotFoundException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentPackNotFoundException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public ContentPackNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentPackNotFoundException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ContentPackNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
