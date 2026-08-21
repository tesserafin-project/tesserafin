using System;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Model.Dlna;

namespace Tesserafin.Controller.Streaming;

/// <summary>
/// The stream state dto.
/// </summary>
public class StreamState : EncodingJobInfo, IDisposable
{
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ITranscodeManager _transcodeManager;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamState" /> class.
    /// </summary>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager" /> interface.</param>
    /// <param name="transcodingType">The <see cref="TranscodingJobType" />.</param>
    /// <param name="transcodeManager">The <see cref="ITranscodeManager" /> singleton.</param>
    public StreamState(IMediaSourceManager mediaSourceManager, TranscodingJobType transcodingType, ITranscodeManager transcodeManager)
        : base(transcodingType)
    {
        _mediaSourceManager = mediaSourceManager;
        _transcodeManager = transcodeManager;
    }

    /// <summary>
    /// Gets or sets the requested url.
    /// </summary>
    public string? RequestedUrl { get; set; }

    /// <summary>
    /// Gets or sets the request.
    /// </summary>
    public StreamingRequestDto Request
    {
        get => (StreamingRequestDto)BaseRequest;
        set
        {
            BaseRequest = value;
            IsVideoRequest = VideoRequest is not null;
        }
    }

    /// <summary>
    /// Gets the video request.
    /// </summary>
    public VideoRequestDto? VideoRequest => Request as VideoRequestDto;

    /// <summary>
    /// Gets or sets the direct stream provider: the already-open live stream this request is
    /// reading, when there is one.
    /// </summary>
    /// <remarks>
    /// Load-bearing, not vestigial. Its presence is what selects <c>-i pipe:0</c> in
    /// <see cref="Tesserafin.Controller.MediaEncoding.EncodingHelper.GetInputArgument"/> and what
    /// makes <c>TranscodeManager.StartFfMpeg</c> pump the stream into ffmpeg's standard input,
    /// instead of letting ffmpeg fetch the <c>[Authorize]</c>d
    /// <c>/LiveTv/LiveStreamFiles/**</c> URL it has no credential for. See
    /// <see cref="Tesserafin.Controller.MediaEncoding.DirectStreamPump"/>.
    /// </remarks>
    public IDirectStreamProvider? DirectStreamProvider { get; set; }

    /// <summary>
    /// Gets or sets the path to wait for.
    /// </summary>
    public string? WaitForPath { get; set; }

    /// <summary>
    /// Gets a value indicating whether the request outputs video.
    /// </summary>
    public bool IsOutputVideo => Request is VideoRequestDto;

    /// <summary>
    /// Gets the segment length.
    /// </summary>
    public int SegmentLength
    {
        get
        {
            if (Request.SegmentLength.HasValue)
            {
                return Request.SegmentLength.Value;
            }

            if (EncodingHelper.IsCopyCodec(OutputVideoCodec))
            {
                var userAgent = UserAgent ?? string.Empty;

                if (userAgent.Contains("AppleTV", StringComparison.OrdinalIgnoreCase)
                    || userAgent.Contains("cfnetwork", StringComparison.OrdinalIgnoreCase)
                    || userAgent.Contains("ipad", StringComparison.OrdinalIgnoreCase)
                    || userAgent.Contains("iphone", StringComparison.OrdinalIgnoreCase)
                    || userAgent.Contains("ipod", StringComparison.OrdinalIgnoreCase))
                {
                    return 6;
                }

                if (IsSegmentedLiveStream)
                {
                    return 3;
                }

                return 6;
            }

            return 3;
        }
    }

    /// <summary>
    /// Gets the minimum number of segments.
    /// </summary>
    public int MinSegments
    {
        get
        {
            if (Request.MinSegments.HasValue)
            {
                return Request.MinSegments.Value;
            }

            return SegmentLength >= 10 ? 2 : 3;
        }
    }

    /// <summary>
    /// Gets or sets the user agent.
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Gets or sets the device id of the validated token this request arrived on (#153-LTV-R3).
    /// </summary>
    /// <remarks>
    /// <c>StreamingRequestDto.DeviceId</c> is a query parameter and is what the client
    /// claims about itself. This is the <c>Tesserafin-DeviceId</c> claim, which the server issued
    /// and validated. A transcoding job's owner is recorded from this, so that the segment routes
    /// compare a validated claim against a validated claim rather than against a url.
    /// </remarks>
    public string? OwnerDeviceId { get; set; }

    /// <summary>
    /// Gets or sets the session a validated playback capability on this request belongs to
    /// (#153-LTV-R3).
    /// </summary>
    /// <remarks>
    /// A capability principal carries no device claim, so this is how a transcode started under
    /// one still records a device: <c>HlsJobOwnerDevice</c> turns the session into its device, on
    /// both the recording side and the comparing side.
    /// </remarks>
    public string? OwnerCapabilitySessionId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to estimate the content length.
    /// </summary>
    public bool EstimateContentLength { get; set; }

    /// <summary>
    /// Gets or sets the transcode seek info.
    /// </summary>
    public TranscodeSeekInfo TranscodeSeekInfo { get; set; }

    /// <summary>
    /// Gets or sets the transcoding job.
    /// </summary>
    public TranscodingJob? TranscodingJob { get; set; }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public override void ReportTranscodingProgress(TimeSpan? transcodingPosition, float? framerate, double? percentComplete, long? bytesTranscoded, int? bitRate)
    {
        _transcodeManager.ReportTranscodingProgress(TranscodingJob!, this, transcodingPosition, framerate, percentComplete, bytesTranscoded, bitRate);
    }

    /// <summary>
    /// Disposes the stream state.
    /// </summary>
    /// <param name="disposing">Whether the object is currently being disposed.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            // REVIEW: Is this the right place for this?
            if (MediaSource.RequiresClosing
                && string.IsNullOrWhiteSpace(Request.LiveStreamId)
                && !string.IsNullOrWhiteSpace(MediaSource.LiveStreamId))
            {
                _mediaSourceManager.CloseLiveStream(MediaSource.LiveStreamId).GetAwaiter().GetResult();
            }
        }

        TranscodingJob = null;

        _disposed = true;
    }
}
