using System.Text.Json;
using Tesserafin.Extensions.Json.Converters;
using Tesserafin.Model.Session;
using Xunit;

namespace Tesserafin.Extensions.Tests.Json.Converters;

public class JsonFlagEnumTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        Converters =
        {
            new JsonFlagEnumConverter<TranscodeReason>()
        }
    };

    [Theory]
    [InlineData(TranscodeReason.AudioIsExternal | TranscodeReason.ContainerNotSupported, "[\"ContainerNotSupported\",\"AudioIsExternal\"]")]
    [InlineData(TranscodeReason.AudioIsExternal | TranscodeReason.ContainerNotSupported | TranscodeReason.VideoBitDepthNotSupported, "[\"ContainerNotSupported\",\"AudioIsExternal\",\"VideoBitDepthNotSupported\"]")]
    [InlineData((TranscodeReason)0, "[]")]
    public void Serialize_Transcode_Reason(TranscodeReason transcodeReason, string output)
    {
        var result = JsonSerializer.Serialize(transcodeReason, _jsonOptions);

        Assert.Equal(output, result);
    }
}
