using System.Text.Json.Serialization;

namespace Tesserafin.LiveTv.Listings.SchedulesDirectDtos
{
    /// <summary>
    /// Token request dto.
    /// </summary>
    public class TokenRequestDto
    {
        /// <summary>
        /// Gets or sets the username.
        /// </summary>
        [JsonPropertyName("username")]
        public string? Username { get; set; }

        /// <summary>
        /// Gets or sets the hashed password.
        /// </summary>
        [JsonPropertyName("password")]
        public string? Password { get; set; }
    }
}
