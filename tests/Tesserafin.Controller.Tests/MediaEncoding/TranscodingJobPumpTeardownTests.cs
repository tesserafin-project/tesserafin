using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Controller.MediaEncoding;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

/// <summary>
/// A <see cref="TranscodingJob"/> owns its <see cref="DirectStreamPump"/> and must end it.
/// </summary>
/// <remarks>
/// Without this, a stopped Live TV job leaves a task reading the tuner and writing into a dead
/// process's stdin. It is invisible at runtime - the segments are already on disk and playback
/// looks fine - so only an explicit assertion catches it.
/// </remarks>
public class TranscodingJobPumpTeardownTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public void Stop_WithADirectStreamPump_EndsThePump()
    {
        using var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance);
        var source = new NeverEndingStream();
        var destination = new MemoryStream();
        var pump = DirectStreamPump.Start(source, destination, NullLogger.Instance, CancellationToken.None);
        job.DirectStreamPump = pump;

        job.Stop();

        Assert.True(pump.Completion.IsCompleted, "TranscodingJob.Stop returned while the pump was still running.");
        Assert.True(source.IsDisposed, "The pump did not release the producer.");
    }

    [Fact]
    public void Dispose_WithADirectStreamPump_EndsThePump()
    {
        // The path taken when ffmpeg exits on its own: OnFfMpegProcessExited disposes the job
        // without calling Stop first.
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance);
        var source = new NeverEndingStream();
        var destination = new MemoryStream();
        var pump = DirectStreamPump.Start(source, destination, NullLogger.Instance, CancellationToken.None);
        job.DirectStreamPump = pump;

        job.Dispose();

        Assert.True(pump.Completion.IsCompleted, "TranscodingJob.Dispose returned while the pump was still running.");
        Assert.Null(job.DirectStreamPump);
    }

    [Fact]
    public async Task Stop_WithNoPump_IsUnchanged()
    {
        using var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance);

        var exception = await Task.Run(() => Record.Exception(job.Stop), TestContext.Current.CancellationToken).WaitAsync(_timeout, TestContext.Current.CancellationToken);

        Assert.Null(exception);
    }

    private sealed class NeverEndingStream : Stream
    {
        public bool IsDisposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            buffer.Span[..1024].Fill(0x47);
            return 1024;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
