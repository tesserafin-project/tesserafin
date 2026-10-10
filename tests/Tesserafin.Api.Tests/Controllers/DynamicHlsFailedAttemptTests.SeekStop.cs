using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Tesserafin.Controller.MediaEncoding;
using Xunit;

namespace Tesserafin.Api.Tests.Controllers;

/// <summary>
/// The stop a segment request makes before it restarts a transcode - a seek. It stops the attempt
/// on the output the caller was found to own, and nothing else.
/// </summary>
/// <remarks>
/// It used to select by what the client sent: the play session id, or the device id when there
/// was none. Both are query parameters. A client that sent neither stopped every transcode on the
/// server that had been started the same way, and one that sent somebody else's stopped theirs.
/// </remarks>
public sealed partial class DynamicHlsFailedAttemptTests
{
    private const string OtherDevice = "another-device";

    // What the two clients put in their urls: nothing, a device only, a play session only.
    public static TheoryData<string?, string?> WhatAClientNames() => new()
    {
        { null, null },
        { OwnerDevice, null },
        { null, SessionA }
    };

    [Theory]
    [MemberData(nameof(WhatAClientNames))]
    public async Task SeekByOneUser_DoesNotStopAnotherUsersTranscode_WhateverTheUrlsName(string? namedDevice, string? playSession)
    {
        // Two users, two outputs - the User-Agent tells them apart - and urls that name the same things.
        var theirs = await StartPlayback(hardware: true, segments: 6, playSession: playSession, namedDevice: namedDevice, userAgent: "one client");
        var mine = await StartPlayback(hardware: true, segments: 6, playSession: playSession, user: _stranger, device: OtherDevice, namedDevice: namedDevice, userAgent: "another client");
        Assert.NotEqual(theirs.Job.Path, mine.Job.Path);

        var successor = await Seek(40, playSession, _stranger, OtherDevice, namedDevice, "another client");

        Assert.False(theirs.Job.HasExited, "one user's seek stopped another user's transcode");
        Assert.Same(theirs.Job, _manager!.GetTranscodingJob(theirs.Job.Path!, TranscodingJobType.Hls));

        // And it is not that the stop does nothing: the seeker's own attempt is the one that went.
        await mine.Ended.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Null(mine.Job.Failure);
        Assert.Equal(mine.Job.Path, successor.Job.Path);
        Assert.Same(successor.Job, _manager.GetTranscodingJob(mine.Job.Path!, TranscodingJobType.Hls));
        Assert.Equal(3, _starts.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SeekOnOnePlayback_LeavesTheSameUsersOtherPlaybackRunning(bool named)
    {
        // One user, one device, two playbacks: two play sessions, or two clients that name nothing.
        var other = await StartPlayback(hardware: true, segments: 6, playSession: named ? SessionB : null, namedDevice: named ? OwnerDevice : null, userAgent: "one client");
        var seeking = await StartPlayback(hardware: true, segments: 6, playSession: named ? SessionA : null, namedDevice: named ? OwnerDevice : null, userAgent: "another client");
        Assert.NotEqual(other.Job.Path, seeking.Job.Path);

        var successor = await Seek(40, named ? SessionA : null, null, OwnerDevice, named ? OwnerDevice : null, "another client");

        Assert.False(other.Job.HasExited, "a seek on one playback stopped the same user's other playback");
        Assert.Same(other.Job, _manager!.GetTranscodingJob(other.Job.Path!, TranscodingJobType.Hls));
        await seeking.Ended.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Same(successor.Job, _manager.GetTranscodingJob(seeking.Job.Path!, TranscodingJobType.Hls));
    }

    [Fact]
    public async Task SeekWithAnotherUsersPlaySessionId_StopsNothingOfTheirs()
    {
        // The play session id is not a secret the stop may rest on: here it is borrowed, from
        // another device and another client, so the output is the borrower's own.
        var theirs = await StartPlayback(hardware: true, segments: 6);

        var next = NextStart();
        var request = Request(40, user: _stranger, device: OtherDevice, namedDevice: OtherDevice, userAgent: "another client");
        var mine = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        mine.Write(40, "segment 40");
        mine.Write(41, "segment 41");
        Assert.Equal(StatusCodes.Status200OK, (await request.WaitAsync(Patience, TestContext.Current.CancellationToken)).Status);

        Assert.NotEqual(theirs.Job.Path, mine.Job.Path);
        Assert.False(theirs.Job.HasExited, "a seek that named another user's play session stopped their transcode");
        Assert.Same(theirs.Job, _manager!.GetTranscodingJob(theirs.Job.Path!, TranscodingJobType.Hls));
    }

    [Fact]
    public async Task AttemptReplacedBySomebodyElsesWhileTheSeekWaitedForTheLock_IsNotStopped_AndTheSeekIsRefused()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);

        // The owner's seek has been found to own the output and is about to take its lock.
        var beforeLock = new Gate("the lock");
        _lockGate = beforeLock;
        var seek = Request(40);
        await beforeLock.Reached;

        // Meanwhile the attempt is released, and another user replaying the same url starts one
        // there: with no job on the output, that is allowed - on an output emptied first.
        await _manager!.KillTranscodingJobs(OwnerDevice, SessionA, _ => false);
        var next = NextStart();
        var replay = Request(0, user: _stranger, device: OtherDevice);
        var theirs = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal(attempt.Job.Path, theirs.Job.Path);

        // Answered with what its own transcode writes (POLISH-2-R4), which also frees the output's lock.
        theirs.Write(0, "theirs 0");
        theirs.Write(1, "theirs 1");
        Assert.Equal("theirs 0", (await replay.WaitAsync(Patience, TestContext.Current.CancellationToken)).Text);

        beforeLock.Open();
        var response = await Settled(seek);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Status);
        Assert.False(theirs.Job.HasExited, "a seek decided on an attempt that is gone stopped the one that replaced it");
        Assert.Same(theirs.Job, _manager.GetTranscodingJob(theirs.Job.Path!, TranscodingJobType.Hls));
        Assert.Equal(2, _starts.Count);
    }

    [Fact]
    public async Task AttemptReplacedByTheSameUsersWhileTheSeekWaitedForTheLock_TheOneThereNowIsStopped()
    {
        var attempt = await StartPlayback(hardware: true, segments: 6);

        var beforeLock = new Gate("the lock");
        _lockGate = beforeLock;
        var seek = Request(40);
        await beforeLock.Reached;

        // Released and started again by the same user: the seek was decided on an attempt that is gone.
        await _manager!.KillTranscodingJobs(OwnerDevice, SessionA, _ => false);
        var next = NextStart();
        var again = Request(8);
        var replacement = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        replacement.Write(8, "segment 8");
        replacement.Write(9, "segment 9");
        Assert.Equal(StatusCodes.Status200OK, (await again.WaitAsync(Patience, TestContext.Current.CancellationToken)).Status);

        next = NextStart();
        beforeLock.Open();
        var successor = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        successor.Write(40, "segment 40");
        successor.Write(41, "segment 41");
        var response = await seek.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("segment 40", response.Text);
        await attempt.Ended.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await replacement.Ended.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Null(replacement.Job.Failure);
        Assert.Same(successor.Job, _manager.GetTranscodingJob(attempt.Job.Path!, TranscodingJobType.Hls));
        Assert.Equal(1, _starts.Count(s => !s.Job.HasExited));
    }

    /// <summary>
    /// A request far enough ahead that the transcode is restarted for it, taken to its answer.
    /// </summary>
    private async Task<Start> Seek(int segment, string? playSession, Guid? user, string device, string? namedDevice, string userAgent)
    {
        var next = NextStart();
        var request = Request(segment, playSession: playSession, user: user, device: device, namedDevice: namedDevice, userAgent: userAgent);
        var successor = await next.WaitAsync(Patience, TestContext.Current.CancellationToken);
        successor.Write(segment, "after the seek");
        successor.Write(segment + 1, "and the next");
        var response = await request.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal("after the seek", response.Text);
        return successor;
    }
}
