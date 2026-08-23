using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// The <c>startTimeTicks</c> boundary on the dynamic HLS segment routes, measured against a booted
/// server, one real HTTP request at a time (#153-LTV-CQL-R1).
/// </summary>
/// <remarks>
/// WHY THIS SUITE EXISTS ALONGSIDE THE UNIT TESTS. <c>DynamicHlsStartTimeTicksBoundaryTests</c>
/// constructs the filter context itself and calls the filter. That proves what the filter DOES; it
/// cannot prove that the framework discovers it and runs it, and a filter MVC never invokes is
/// green in every unit test while <c>startTimeTicks=999</c> sails through in production. Only a
/// real request through the real pipeline answers that, so it is answered here.
///
/// AND WHY THE ABSENT/ZERO ROWS ARE NOT DECORATION. They are the control. A 400 means the boundary
/// refused only if the same url without the parameter does NOT return 400 — otherwise the status
/// would be the route's own answer to a fixture that cannot serve a segment, and the boundary
/// would be invisible.
///
/// WHAT THE UNPARSEABLE ROW MEASURES, AND WHY IT IS DIFFERENT. <c>startTimeTicks=abc</c> never
/// reaches the filter: model binding fails, and <c>[ApiController]</c>'s model-state filter
/// answers 400 with a <c>ValidationProblemDetails</c> BODY ahead of every action filter. So the
/// status a caller sees is the same as the boundary's and the body is not. That is framework
/// behaviour, identical on every typed parameter of every route in this server, and it is recorded
/// here rather than left for a reader to discover: a refusal with a body is still a refusal, and
/// what matters is that it is never the media.
/// </remarks>
[Collection(MediaBoundarySuite.Name)]
public sealed class DynamicHlsStartTimeTicksHttpMatrixTests
{
    private readonly MediaBoundaryFixture _fixture;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicHlsStartTimeTicksHttpMatrixTests"/> class.
    /// </summary>
    /// <param name="fixture">The shared server and library fixture.</param>
    public DynamicHlsStartTimeTicksHttpMatrixTests(MediaBoundaryFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>Gets the two dynamic segment route families.</summary>
    public static TheoryData<string> Families { get; } = new() { "Videos", "Audio" };

    /// <summary>Gets the values the route cannot honour, as they appear in a query string.</summary>
    public static TheoryData<string, string> ForbiddenValues { get; } = new()
    {
        { "Videos", "1" },
        { "Videos", "300000000" },
        { "Videos", "-1" },
        { "Videos", "9223372036854775807" },
        { "Videos", "-9223372036854775808" },
        { "Audio", "1" },
        { "Audio", "300000000" },
        { "Audio", "-1" },
        { "Audio", "9223372036854775807" },
        { "Audio", "-9223372036854775808" }
    };

    /// <summary>Gets the rows the route must keep accepting at the boundary.</summary>
    public static TheoryData<string, string?> AllowedValues { get; } = new()
    {
        { "Videos", null },
        { "Videos", "0" },
        { "Audio", null },
        { "Audio", "0" }
    };

    /// <summary>
    /// A forbidden value is refused with a deterministic 400, an empty body, and — the half a
    /// status alone cannot give — not one byte of the fixture's media.
    /// </summary>
    /// <param name="family">The route family.</param>
    /// <param name="ticks">The forbidden value, as sent.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Theory]
    [MemberData(nameof(ForbiddenValues))]
    public async Task AForbiddenStartTimeTicks_IsRefusedWithFourHundredAndZeroBytes(string family, string ticks)
    {
        using var client = _fixture.DurableHeaderClient();

        var (status, body) = await MediaBoundaryFixture
            .SendAsync(client, "GET", Segment(family, ticks))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(body);
        Assert.False(body.SequenceEqual(_fixture.GetMediaBytes()), "the refusal carried the fixture's media.");
    }

    /// <summary>
    /// THE CONTROL, and the shape it actually has to take. With an allowed value the request
    /// REACHES THE ACTION, and this fixture's library item has no encoder behind it, so the action
    /// throws and <c>ExceptionMiddleware</c> answers 400 with <c>Error processing request.</c> in
    /// the body. The status is therefore NOT a discriminator on this route and asserting on it
    /// would have made every row above vacuous.
    ///
    /// The body is the discriminator: the boundary answers 400 with nothing at all, and anything
    /// that got past the boundary answers with a body. So "the boundary did not refuse this" is
    /// measured as "bytes came back", which is exactly the property the forbidden rows deny.
    /// </summary>
    /// <param name="family">The route family.</param>
    /// <param name="ticks">The allowed value, or null for absent.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Theory]
    [MemberData(nameof(AllowedValues))]
    public async Task AnAllowedStartTimeTicks_IsNotRefusedByTheBoundary(string family, string? ticks)
    {
        using var client = _fixture.DurableHeaderClient();

        var (_, body) = await MediaBoundaryFixture
            .SendAsync(client, "GET", Segment(family, ticks))
            .ConfigureAwait(true);

        Assert.NotEmpty(body);
        Assert.False(body.SequenceEqual(_fixture.GetMediaBytes()), "the fixture served its media.");
    }

    /// <summary>
    /// An unparseable value is refused too, by model binding rather than by the boundary. Recorded
    /// because the shape differs: the status is the same 400 and the body is a validation problem
    /// document rather than empty. It is never the media, which is the property that matters.
    /// </summary>
    /// <param name="family">The route family.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Theory]
    [MemberData(nameof(Families))]
    public async Task AnUnparseableStartTimeTicks_IsRefusedByModelBindingWithAProblemBody(string family)
    {
        using var client = _fixture.DurableHeaderClient();

        var (status, body) = await MediaBoundaryFixture
            .SendAsync(client, "GET", Segment(family, "abc"))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotEmpty(body);
        Assert.False(body.SequenceEqual(_fixture.GetMediaBytes()), "the refusal carried the fixture's media.");
    }

    /// <summary>
    /// An unauthenticated caller never reaches the boundary: authorization runs first and answers
    /// 401, so the boundary tells a stranger nothing about which values this route accepts.
    /// </summary>
    /// <param name="family">The route family.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Theory]
    [MemberData(nameof(Families))]
    public async Task AnAnonymousCallerIsRefusedBeforeTheBoundary(string family)
    {
        using var client = _fixture.AnonymousClient();

        var (status, body) = await MediaBoundaryFixture
            .SendAsync(client, "GET", Segment(family, "300000000"))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.False(body.SequenceEqual(_fixture.GetMediaBytes()), "an anonymous caller received the media.");
    }

    private string Segment(string family, string? ticks)
    {
        // runtimeTicks and actualSegmentLengthTicks are [Required] on both actions. Omitting them
        // makes model binding answer 400 before any filter runs, which would make every row below
        // agree for a reason that has nothing to do with the boundary — the control row is what
        // caught that.
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"/{family}/{_fixture.ItemId:N}/hls1/main/0.mp4"
            + $"?mediaSourceId={_fixture.MediaSourceId}"
            + $"&runtimeTicks={TimeSpan.FromMinutes(1).Ticks}"
            + $"&actualSegmentLengthTicks={TimeSpan.FromSeconds(6).Ticks}");

        return ticks is null
            ? url
            : string.Create(CultureInfo.InvariantCulture, $"{url}&startTimeTicks={ticks}");
    }
}
