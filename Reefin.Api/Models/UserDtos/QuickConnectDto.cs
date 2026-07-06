using System.ComponentModel.DataAnnotations;

namespace Reefin.Api.Models.UserDtos;

/// <summary>
/// The quick connect request body.
/// </summary>
public class QuickConnectDto
{
    /// <summary>
    /// Gets or sets the quick connect secret.
    /// </summary>
    [Required]
    public string Secret { get; set; } = null!;
}
