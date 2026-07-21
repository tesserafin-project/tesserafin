using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tesserafin.Playback.Decision;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for (de)serializing the playback decision domain
/// model, so tests and later engine/adapter code all serialize it consistently: enums as strings,
/// null properties omitted from the output.
/// </summary>
public static class PlaybackDecisionJson
{
    /// <summary>
    /// Gets the shared serializer options for the playback decision domain model.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
