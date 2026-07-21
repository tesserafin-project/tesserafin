using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Session;
using Xunit;

namespace Tesserafin.Model.Tests
{
    public class StreamBuilderTests
    {
        [Theory]
        // Chrome
        [InlineData("Chrome", "mp4-h264-aac-vtt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("Chrome", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Chrome", "mp4-h264-ac3-aacDef-srt-2600k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux", "HLS.mp4")] // #6450
        [InlineData("Chrome", "mp4-h264-ac3-aacExt-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioIsExternal, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Chrome", "mp4-h264-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Chrome", "mkv-h264-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Chrome", "mp4-hevc-aac-srt-15200k", PlayMethod.DirectPlay)]
        [InlineData("Chrome", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")]
        [InlineData("Chrome", "mp4-hevc-ac3-aacDef-srt-15200k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux", "HLS.mp4")]
        [InlineData("Chrome", "mkv-vp9-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported, "Remux", "HLS.mp4")] // #6450
        [InlineData("Chrome", "mkv-vp9-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Chrome", "mkv-vp9-vorbis-vtt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("Chrome", "mp4-h264-hi10p-aac-5000k", PlayMethod.DirectPlay)]
        [InlineData("Chrome", "mkv-h264-hi10p-aac-5000k-brokenfps", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported, "Remux", "HLS.mp4")]
        [InlineData("Chrome", "mp4-dvh1.05-eac3-15200k", PlayMethod.Transcode, TranscodeReason.VideoRangeTypeNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode", "HLS.mp4")]
        [InlineData("Chrome", "mkv-dvhe.05-eac3-28000k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.VideoRangeTypeNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode", "HLS.mp4")]
        [InlineData("Chrome", "mkv-dvhe.08-eac3-15200k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.VideoRangeTypeNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode", "HLS.mp4")]
        [InlineData("Chrome", "mp4-dvhe.08-eac3-15200k", PlayMethod.Transcode, TranscodeReason.VideoRangeTypeNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode", "HLS.mp4")]
        [InlineData("Chrome", "numstreams-32", PlayMethod.DirectPlay)]
        [InlineData("Chrome", "numstreams-33", PlayMethod.DirectPlay)]
        // Firefox
        [InlineData("Firefox", "mp4-h264-aac-vtt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("Firefox", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Firefox", "mp4-h264-ac3-aacDef-srt-2600k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux", "HLS.mp4")] // #6450
        [InlineData("Firefox", "mp4-h264-ac3-aacExt-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioIsExternal, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Firefox", "mp4-h264-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Firefox", "mkv-h264-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Firefox", "mp4-hevc-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported, "Transcode", "HLS.mp4")]
        [InlineData("Firefox", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode", "HLS.mp4")]
        [InlineData("Firefox", "mp4-hevc-ac3-aacDef-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.SecondaryAudioNotSupported, "Transcode", "HLS.mp4")]
        [InlineData("Firefox", "mkv-vp9-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported, "Remux", "HLS.mp4")] // #6450
        [InlineData("Firefox", "mkv-vp9-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Firefox", "mkv-vp9-vorbis-vtt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("Firefox", "mp4-h264-hi10p-aac-5000k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.VideoProfileNotSupported, "Transcode", "HLS.mp4")]
        [InlineData("Firefox", "mkv-h264-hi10p-aac-5000k-brokenfps", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.VideoProfileNotSupported, "Transcode", "HLS.mp4")]
        [InlineData("Firefox", "mp4-dvh1.05-eac3-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode", "HLS.mp4")]
        [InlineData("Firefox", "mkv-dvhe.05-eac3-28000k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode", "HLS.mp4")]
        [InlineData("Firefox", "mkv-dvhe.08-eac3-15200k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode", "HLS.mp4")]
        [InlineData("Firefox", "mp4-dvhe.08-eac3-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode", "HLS.mp4")]
        // Safari
        [InlineData("SafariNext", "mp4-h264-aac-vtt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("SafariNext", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("SafariNext", "mp4-h264-ac3-aacDef-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("SafariNext", "mp4-h264-ac3-aacExt-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("SafariNext", "mp4-h264-ac3-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("SafariNext", "mkv-h264-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.AudioChannelsNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("SafariNext", "mp4-hevc-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecTagNotSupported, "Remux", "HLS.mp4")] // #6450
        [InlineData("SafariNext", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecTagNotSupported | TranscodeReason.AudioChannelsNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("SafariNext", "mp4-hevc-ac3-aacExt-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecTagNotSupported | TranscodeReason.AudioChannelsNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("SafariNext", "mp4-h264-hi10p-aac-5000k", PlayMethod.Transcode, TranscodeReason.VideoProfileNotSupported, "Remux", "HLS.mp4")]
        [InlineData("SafariNext", "mkv-h264-hi10p-aac-5000k-brokenfps", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.VideoProfileNotSupported, "Remux", "HLS.mp4")]
        [InlineData("SafariNext", "mp4-dvh1.05-eac3-15200k", PlayMethod.DirectPlay)]
        [InlineData("SafariNext", "mkv-dvhe.05-eac3-28000k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.VideoCodecTagNotSupported | TranscodeReason.AudioChannelsNotSupported, "DirectStream", "HLS.mp4")]
        [InlineData("SafariNext", "mkv-dvhe.08-eac3-15200k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.VideoCodecTagNotSupported | TranscodeReason.AudioChannelsNotSupported, "DirectStream", "HLS.mp4")]
        [InlineData("SafariNext", "mp4-dvhe.08-eac3-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecTagNotSupported | TranscodeReason.AudioChannelsNotSupported, "DirectStream", "HLS.mp4")]
        // AndroidPixel
        [InlineData("AndroidPixel", "mp4-h264-aac-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("AndroidPixel", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("AndroidPixel", "mp4-h264-ac3-aacDef-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("AndroidPixel", "mp4-h264-ac3-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("AndroidPixel", "mp4-hevc-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.ContainerBitrateExceedsLimit, "Transcode")]
        [InlineData("AndroidPixel", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.ContainerBitrateExceedsLimit, "Transcode")]
        // Yatse
        [InlineData("Yatse", "mp4-h264-aac-srt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        [InlineData("Yatse", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported)] // #6450
        [InlineData("Yatse", "mp4-h264-ac3-aacDef-srt-2600k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux")] // #6450
        [InlineData("Yatse", "mp4-h264-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported)]
        [InlineData("Yatse", "mp4-hevc-aac-srt-15200k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        [InlineData("Yatse", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode")] // Full transcode because profile only has ts which does not allow hevc
        [InlineData("Yatse", "mp4-hevc-ac3-aacDef-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.SecondaryAudioNotSupported, "Transcode")] // Full transcode because profile only has ts which does not allow hevc
        // RokuSSPlus
        [InlineData("RokuSSPlus", "mp4-h264-aac-srt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        [InlineData("RokuSSPlus", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported)] // #6450
        [InlineData("RokuSSPlus", "mp4-h264-ac3-aacDef-srt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450 should be DirectPlay
        [InlineData("RokuSSPlus", "mp4-h264-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported)] // #6450
        [InlineData("RokuSSPlus", "mp4-hevc-aac-srt-15200k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        [InlineData("RokuSSPlus", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode")] // Full transcode because profile only has ts which does not allow hevc
        [InlineData("RokuSSPlus", "mp4-hevc-ac3-aacDef-srt-15200k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        [InlineData("RokuSSPlus", "mp4-hevc-ac3-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode")] // Full transcode because profile only has ts which does not allow hevc
        // TesserafinMediaPlayer
        [InlineData("TesserafinMediaPlayer", "mp4-h264-aac-vtt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        [InlineData("TesserafinMediaPlayer", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        [InlineData("TesserafinMediaPlayer", "mp4-h264-ac3-srt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        [InlineData("TesserafinMediaPlayer", "mp4-hevc-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.ContainerBitrateExceedsLimit, "Transcode")]
        [InlineData("TesserafinMediaPlayer", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.ContainerBitrateExceedsLimit, "Transcode")]
        [InlineData("TesserafinMediaPlayer", "mkv-vp9-aac-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("TesserafinMediaPlayer", "mkv-vp9-ac3-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("TesserafinMediaPlayer", "mkv-vp9-vorbis-vtt-2600k", PlayMethod.DirectPlay)] // #6450
        // Non-HLS Progressive transcoding
        [InlineData("Chrome-NoHLS", "mp4-h264-aac-vtt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("Chrome-NoHLS", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, "DirectStream", "http")] // #6450
        [InlineData("Chrome-NoHLS", "mp4-h264-ac3-aacDef-srt-2600k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux", "http")] // #6450
        [InlineData("Chrome-NoHLS", "mp4-h264-ac3-aacExt-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioIsExternal, "Remux", "http")] // #6450
        [InlineData("Chrome-NoHLS", "mp4-h264-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, "DirectStream", "http")] // #6450
        [InlineData("Chrome-NoHLS", "mp4-hevc-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported, "Transcode", "http")]
        [InlineData("Chrome-NoHLS", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode", "http")]
        [InlineData("Chrome-NoHLS", "mp4-hevc-ac3-aacDef-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.SecondaryAudioNotSupported, "Transcode", "http")]
        [InlineData("Chrome-NoHLS", "mkv-vp9-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.AudioCodecNotSupported, "DirectStream", "http")] // webm requested, aac not supported
        [InlineData("Chrome-NoHLS", "mkv-vp9-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.AudioCodecNotSupported, "DirectStream", "http")] // #6450
        [InlineData("Chrome-NoHLS", "mkv-vp9-vorbis-vtt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux", "http")] // #6450
        // TranscodeMedia
        [InlineData("TranscodeMedia", "mp4-h264-aac-vtt-2600k", PlayMethod.Transcode, TranscodeReason.DirectPlayError, "Remux", "HLS.mp4")]
        [InlineData("TranscodeMedia", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported | TranscodeReason.DirectPlayError, "DirectStream", "HLS.mp4")]
        [InlineData("TranscodeMedia", "mp4-h264-ac3-aacDef-srt-2600k", PlayMethod.Transcode, TranscodeReason.DirectPlayError, "Remux", "HLS.mp4")]
        [InlineData("TranscodeMedia", "mp4-h264-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported | TranscodeReason.DirectPlayError, "DirectStream", "HLS.mp4")]
        [InlineData("TranscodeMedia", "mp4-hevc-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.DirectPlayError, "Remux", "HLS.mp4")]
        [InlineData("TranscodeMedia", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported | TranscodeReason.DirectPlayError, "DirectStream", "HLS.mp4")]
        [InlineData("TranscodeMedia", "mp4-hevc-ac3-aacDef-srt-15200k", PlayMethod.Transcode, TranscodeReason.DirectPlayError, "Remux", "HLS.mp4")]
        [InlineData("TranscodeMedia", "mkv-av1-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported | TranscodeReason.DirectPlayError, "DirectStream", "http")]
        [InlineData("TranscodeMedia", "mkv-av1-vorbis-srt-2600k", PlayMethod.Transcode, TranscodeReason.DirectPlayError, "Remux", "http")]
        [InlineData("TranscodeMedia", "mkv-vp9-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported | TranscodeReason.DirectPlayError, "DirectStream", "http")]
        [InlineData("TranscodeMedia", "mkv-vp9-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported | TranscodeReason.DirectPlayError, "DirectStream", "http")]
        [InlineData("TranscodeMedia", "mkv-vp9-vorbis-vtt-2600k", PlayMethod.Transcode, TranscodeReason.DirectPlayError, "Remux", "http")]
        // DirectMedia
        [InlineData("DirectMedia", "mp4-h264-aac-vtt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")]
        [InlineData("DirectMedia", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")]
        [InlineData("DirectMedia", "mp4-h264-ac3-aacDef-srt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")]
        [InlineData("DirectMedia", "mp4-h264-ac3-srt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")]
        [InlineData("DirectMedia", "mp4-hevc-aac-srt-15200k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")]
        [InlineData("DirectMedia", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")]
        [InlineData("DirectMedia", "mkv-vp9-aac-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("DirectMedia", "mkv-vp9-ac3-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("DirectMedia", "mkv-vp9-vorbis-vtt-2600k", PlayMethod.DirectPlay)]
        // LowBandwidth
        [InlineData("LowBandwidth", "mp4-h264-aac-vtt-2600k", PlayMethod.Transcode, TranscodeReason.ContainerBitrateExceedsLimit, "Transcode")]
        [InlineData("LowBandwidth", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.ContainerBitrateExceedsLimit, "Transcode")]
        [InlineData("LowBandwidth", "mp4-h264-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.ContainerBitrateExceedsLimit, "Transcode")]
        [InlineData("LowBandwidth", "mp4-hevc-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.ContainerBitrateExceedsLimit, "Transcode")]
        [InlineData("LowBandwidth", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.ContainerBitrateExceedsLimit, "Transcode")]
        [InlineData("LowBandwidth", "mkv-vp9-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.ContainerBitrateExceedsLimit, "Transcode")]
        [InlineData("LowBandwidth", "mkv-vp9-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.ContainerBitrateExceedsLimit, "Transcode")]
        [InlineData("LowBandwidth", "mkv-vp9-vorbis-vtt-2600k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported | TranscodeReason.ContainerBitrateExceedsLimit, "Transcode")]
        // Null
        [InlineData("Null", "mp4-h264-aac-vtt-2600k", null, TranscodeReason.ContainerBitrateExceedsLimit)]
        [InlineData("Null", "mp4-h264-ac3-aac-srt-2600k", null, TranscodeReason.ContainerBitrateExceedsLimit)]
        [InlineData("Null", "mp4-h264-ac3-srt-2600k", null, TranscodeReason.ContainerBitrateExceedsLimit)]
        [InlineData("Null", "mp4-hevc-aac-srt-15200k", null, TranscodeReason.ContainerBitrateExceedsLimit)]
        [InlineData("Null", "mp4-hevc-ac3-aac-srt-15200k", null, TranscodeReason.ContainerBitrateExceedsLimit)]
        [InlineData("Null", "mkv-vp9-aac-srt-2600k", null, TranscodeReason.ContainerBitrateExceedsLimit)]
        [InlineData("Null", "mkv-vp9-ac3-srt-2600k", null, TranscodeReason.ContainerBitrateExceedsLimit)]
        [InlineData("Null", "mkv-vp9-vorbis-vtt-2600k", null, TranscodeReason.ContainerBitrateExceedsLimit)]
        // AndroidTV
        [InlineData("AndroidTVExoPlayer", "mp4-h264-aac-vtt-2600k", PlayMethod.DirectPlay)]
        [InlineData("AndroidTVExoPlayer", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("AndroidTVExoPlayer", "mp4-h264-ac3-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("AndroidTVExoPlayer", "mp4-hevc-aac-srt-15200k", PlayMethod.DirectPlay)]
        [InlineData("AndroidTVExoPlayer", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.DirectPlay)]
        [InlineData("AndroidTVExoPlayer", "mkv-vp9-aac-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("AndroidTVExoPlayer", "mkv-vp9-ac3-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("AndroidTVExoPlayer", "mkv-vp9-vorbis-vtt-2600k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode")] // Full transcode because profile only has ts which does not allow vp9
        [InlineData("AndroidTVExoPlayer", "mp4-hevc-aac-4000k-r180", PlayMethod.DirectPlay)] // #13712
        // AndroidTV NoHevcRotation
        [InlineData("AndroidTVExoPlayer-NoHevcRotation", "mp4-hevc-aac-4000k-r180", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.VideoRotationNotSupported, "Transcode")] // #13712
        // Tizen 3 Stereo
        [InlineData("Tizen3-stereo", "mp4-h264-aac-vtt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mp4-h264-ac3-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mp4-h264-dts-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mp4-hevc-aac-srt-15200k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mp4-hevc-truehd-srt-15200k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported)]
        [InlineData("Tizen3-stereo", "mkv-vp9-aac-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mkv-vp9-ac3-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mkv-vp9-vorbis-vtt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mkv-dvhe.08-eac3-15200k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mp4-dvh1.05-eac3-15200k", PlayMethod.Transcode, TranscodeReason.VideoRangeTypeNotSupported | TranscodeReason.AudioChannelsNotSupported, "Transcode")]
        [InlineData("Tizen3-stereo", "mp4-dvhe.08-eac3-15200k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mkv-dvhe.05-eac3-28000k", PlayMethod.Transcode, TranscodeReason.VideoBitrateNotSupported | TranscodeReason.VideoRangeTypeNotSupported | TranscodeReason.AudioChannelsNotSupported, "Transcode")]
        [InlineData("Tizen3-stereo", "numstreams-32", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "numstreams-33", PlayMethod.Transcode, TranscodeReason.StreamCountExceedsLimit, "Remux")]
        // Tizen 4 4K 5.1
        [InlineData("Tizen4-4K-5.1", "mp4-h264-aac-vtt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "mp4-h264-ac3-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "mp4-h264-dts-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported)]
        [InlineData("Tizen4-4K-5.1", "mp4-hevc-aac-srt-15200k", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "mp4-hevc-truehd-srt-15200k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported)]
        [InlineData("Tizen4-4K-5.1", "mkv-vp9-aac-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "mkv-vp9-ac3-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "mkv-vp9-vorbis-vtt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "mkv-dvhe.08-eac3-15200k", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "mp4-dvh1.05-eac3-15200k", PlayMethod.Transcode, TranscodeReason.VideoRangeTypeNotSupported, "Transcode")]
        [InlineData("Tizen4-4K-5.1", "mp4-dvhe.08-eac3-15200k", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "mkv-dvhe.05-eac3-28000k", PlayMethod.Transcode, TranscodeReason.VideoRangeTypeNotSupported, "Transcode")]
        [InlineData("Tizen4-4K-5.1", "numstreams-32", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "numstreams-33", PlayMethod.Transcode, TranscodeReason.StreamCountExceedsLimit, "Remux")]
        // WebOS 23
        [InlineData("WebOS-23", "mkv-dvhe.08-eac3-15200k", PlayMethod.Transcode, TranscodeReason.VideoRangeTypeNotSupported, "Remux")]
        [InlineData("WebOS-23", "mp4-dvh1.05-eac3-15200k", PlayMethod.DirectPlay)]
        [InlineData("WebOS-23", "mp4-dvhe.08-eac3-15200k", PlayMethod.DirectPlay)]
        [InlineData("WebOS-23", "mkv-dvhe.05-eac3-28000k", PlayMethod.Transcode, TranscodeReason.VideoRangeTypeNotSupported, "Remux")]
        public async Task BuildVideoItemSimple(string deviceName, string mediaSource, PlayMethod? playMethod, TranscodeReason why = default, string transcodeMode = "DirectStream", string transcodeProtocol = "")
        {
            var options = await GetMediaOptions(deviceName, mediaSource);
            BuildVideoItemSimpleTest(options, playMethod, why, transcodeMode, transcodeProtocol);
        }

        [Theory]
        // Chrome
        [InlineData("Chrome", "mp4-h264-aac-vtt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("Chrome", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Chrome", "mp4-h264-ac3-aacDef-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")] // #6450 <BUG: this is direct played>
        [InlineData("Chrome", "mp4-h264-ac3-aacExt-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Chrome", "mp4-h264-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Chrome", "mp4-hevc-aac-srt-15200k", PlayMethod.DirectPlay)]
        [InlineData("Chrome", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")]
        [InlineData("Chrome", "mkv-vp9-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported, "Remux", "HLS.mp4")] // #6450
        [InlineData("Chrome", "mkv-vp9-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Chrome", "mkv-vp9-vorbis-vtt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux", "HLS.mp4")] // #6450
        // Firefox
        [InlineData("Firefox", "mp4-h264-aac-vtt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("Firefox", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Firefox", "mp4-h264-ac3-aacDef-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Firefox", "mp4-h264-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Firefox", "mp4-hevc-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported, "Transcode", "HLS.mp4")]
        [InlineData("Firefox", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode", "HLS.mp4")]
        [InlineData("Firefox", "mkv-vp9-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported, "Remux", "HLS.mp4")] // #6450
        [InlineData("Firefox", "mkv-vp9-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.ContainerNotSupported | TranscodeReason.AudioCodecNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Firefox", "mkv-vp9-vorbis-vtt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        // Safari
        [InlineData("SafariNext", "mp4-h264-aac-vtt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("SafariNext", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("SafariNext", "mp4-h264-ac3-aacDef-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("SafariNext", "mp4-h264-ac3-aacExt-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("SafariNext", "mp4-h264-ac3-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("SafariNext", "mp4-hevc-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecTagNotSupported, "Remux", "HLS.mp4")] // #6450
        [InlineData("SafariNext", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecTagNotSupported | TranscodeReason.AudioChannelsNotSupported, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("SafariNext", "mp4-hevc-ac3-aacExt-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecTagNotSupported | TranscodeReason.AudioChannelsNotSupported, "DirectStream", "HLS.mp4")] // #6450

        // AndroidPixel
        [InlineData("AndroidPixel", "mp4-h264-aac-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("AndroidPixel", "mp4-h264-ac3-aacDef-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("AndroidPixel", "mp4-h264-ac3-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("AndroidPixel", "mp4-hevc-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.ContainerBitrateExceedsLimit, "Transcode")]
        [InlineData("AndroidPixel", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.ContainerBitrateExceedsLimit, "Transcode")]
        // Yatse
        [InlineData("Yatse", "mp4-h264-aac-srt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        [InlineData("Yatse", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported)] // #6450
        [InlineData("Yatse", "mp4-h264-ac3-aacDef-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported)] // #6450
        [InlineData("Yatse", "mp4-h264-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported)]
        [InlineData("Yatse", "mp4-hevc-aac-srt-15200k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        [InlineData("Yatse", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode")] // Full transcode because profile only has ts which does not allow hevc
        // RokuSSPlus
        [InlineData("RokuSSPlus", "mp4-h264-aac-srt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        [InlineData("RokuSSPlus", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported)] // #6450 should be DirectPlay
        [InlineData("RokuSSPlus", "mp4-h264-ac3-aacDef-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported)] // #6450
        [InlineData("RokuSSPlus", "mp4-h264-ac3-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported)] // #6450
        [InlineData("RokuSSPlus", "mp4-hevc-aac-srt-15200k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        [InlineData("RokuSSPlus", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode")] // Full transcode because profile only has ts which does not allow hevc
        [InlineData("RokuSSPlus", "mp4-hevc-ac3-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode")] // Full transcode because profile only has ts which does not allow hevc
        // TesserafinMediaPlayer
        [InlineData("TesserafinMediaPlayer", "mp4-h264-aac-vtt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        [InlineData("TesserafinMediaPlayer", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        [InlineData("TesserafinMediaPlayer", "mp4-h264-ac3-srt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        [InlineData("TesserafinMediaPlayer", "mp4-hevc-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.ContainerBitrateExceedsLimit, "Transcode")] // #6450
        [InlineData("TesserafinMediaPlayer", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.ContainerBitrateExceedsLimit, "Transcode")] // #6450
        [InlineData("TesserafinMediaPlayer", "mkv-vp9-aac-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("TesserafinMediaPlayer", "mkv-vp9-ac3-srt-2600k", PlayMethod.DirectPlay)] // #6450
        [InlineData("TesserafinMediaPlayer", "mkv-vp9-vorbis-vtt-2600k", PlayMethod.DirectPlay)] // #6450
        // AndroidTV
        [InlineData("AndroidTVExoPlayer", "mp4-h264-aac-vtt-2600k", PlayMethod.DirectPlay)]
        [InlineData("AndroidTVExoPlayer", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("AndroidTVExoPlayer", "mp4-h264-ac3-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("AndroidTVExoPlayer", "mp4-hevc-aac-srt-15200k", PlayMethod.DirectPlay)]
        [InlineData("AndroidTVExoPlayer", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.DirectPlay)]
        [InlineData("AndroidTVExoPlayer", "mkv-vp9-aac-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("AndroidTVExoPlayer", "mkv-vp9-ac3-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("AndroidTVExoPlayer", "mkv-vp9-vorbis-vtt-2600k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.AudioCodecNotSupported, "Transcode")] // Full transcode because profile only has ts which does not allow vp9
        // Tizen 3 Stereo
        [InlineData("Tizen3-stereo", "mp4-h264-aac-vtt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mp4-h264-ac3-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mp4-h264-dts-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mp4-hevc-aac-srt-15200k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mp4-hevc-truehd-srt-15200k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported)]
        [InlineData("Tizen3-stereo", "mkv-vp9-aac-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mkv-vp9-ac3-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen3-stereo", "mkv-vp9-vorbis-vtt-2600k", PlayMethod.DirectPlay)]
        // Tizen 4 4K 5.1
        [InlineData("Tizen4-4K-5.1", "mp4-h264-aac-vtt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "mp4-h264-ac3-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "mp4-h264-dts-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported)]
        [InlineData("Tizen4-4K-5.1", "mp4-hevc-aac-srt-15200k", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "mp4-hevc-truehd-srt-15200k", PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported)]
        [InlineData("Tizen4-4K-5.1", "mkv-vp9-aac-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "mkv-vp9-ac3-srt-2600k", PlayMethod.DirectPlay)]
        [InlineData("Tizen4-4K-5.1", "mkv-vp9-vorbis-vtt-2600k", PlayMethod.DirectPlay)]
        public async Task BuildVideoItemWithFirstExplicitStream(string deviceName, string mediaSource, PlayMethod? playMethod, TranscodeReason why = default, string transcodeMode = "DirectStream", string transcodeProtocol = "")
        {
            var options = await GetMediaOptions(deviceName, mediaSource);
            options.AudioStreamIndex = 1;
            options.SubtitleStreamIndex = options.MediaSources[0].MediaStreams.Count - 1;

            var streamInfo = BuildVideoItemSimpleTest(options, playMethod, why, transcodeMode, transcodeProtocol);
            Assert.Equal(streamInfo?.AudioStreamIndex, options.AudioStreamIndex);
            Assert.Equal(streamInfo?.SubtitleStreamIndex, options.SubtitleStreamIndex);
        }

        [Theory]
        // Chrome
        [InlineData("Chrome", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux", "HLS.mp4")] // #6450
        [InlineData("Chrome", "mp4-h264-ac3-aac-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux", "HLS.mp4")]
        [InlineData("Chrome", "mp4-h264-ac3-aacExt-srt-2600k", PlayMethod.Transcode, TranscodeReason.AudioIsExternal, "DirectStream", "HLS.mp4")] // #6450
        [InlineData("Chrome", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux", "HLS.mp4")]
        // Firefox
        [InlineData("Firefox", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux", "HLS.mp4")] // #6450
        [InlineData("Firefox", "mp4-h264-ac3-aac-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux", "HLS.mp4")]
        [InlineData("Firefox", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.SecondaryAudioNotSupported, "Transcode", "HLS.mp4")]
        // Yatse
        [InlineData("Yatse", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux")] // #6450
        [InlineData("Yatse", "mp4-h264-ac3-aac-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux")]
        [InlineData("Yatse", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported | TranscodeReason.SecondaryAudioNotSupported, "Transcode")] // Full transcode because profile only has ts which does not allow hevc
        // RokuSSPlus
        [InlineData("RokuSSPlus", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        [InlineData("RokuSSPlus", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")] // #6450
        // no streams
        [InlineData("Chrome", "no-streams", PlayMethod.Transcode, TranscodeReason.VideoCodecNotSupported, "Transcode", "HLS.mp4")] // #6450
        // AndroidTV
        [InlineData("AndroidTVExoPlayer", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")]
        [InlineData("AndroidTVExoPlayer", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.DirectPlay, (TranscodeReason)0, "Remux")]
        // Tizen 3 Stereo
        [InlineData("Tizen3-stereo", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux")]
        [InlineData("Tizen3-stereo", "mp4-h264-ac3-aac-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux")]
        [InlineData("Tizen3-stereo", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux")]
        // Tizen 4 4K 5.1
        [InlineData("Tizen4-4K-5.1", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux")]
        [InlineData("Tizen4-4K-5.1", "mp4-h264-ac3-aac-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux")]
        [InlineData("Tizen4-4K-5.1", "mp4-hevc-ac3-aac-srt-15200k", PlayMethod.Transcode, TranscodeReason.SecondaryAudioNotSupported, "Remux")]
        // TranscodeMedia
        [InlineData("TranscodeMedia", "mp4-h264-ac3-aac-srt-2600k", PlayMethod.Transcode, TranscodeReason.DirectPlayError, "Remux", "HLS.mp4")]
        [InlineData("TranscodeMedia", "mp4-h264-ac3-aac-mp3-srt-2600k", PlayMethod.Transcode, TranscodeReason.DirectPlayError, "Remux", "HLS.ts")]
        public async Task BuildVideoItemWithDirectPlayExplicitStreams(string deviceName, string mediaSource, PlayMethod? playMethod, TranscodeReason why = default, string transcodeMode = "DirectStream", string transcodeProtocol = "")
        {
            var options = await GetMediaOptions(deviceName, mediaSource);
            var streamCount = options.MediaSources[0].MediaStreams.Count;
            if (streamCount > 0)
            {
                options.AudioStreamIndex = streamCount - 2;
                options.SubtitleStreamIndex = streamCount - 1;
            }

            var streamInfo = BuildVideoItemSimpleTest(options, playMethod, why, transcodeMode, transcodeProtocol);
            Assert.Equal(streamInfo?.AudioStreamIndex, options.AudioStreamIndex);
            Assert.Equal(streamInfo?.SubtitleStreamIndex, options.SubtitleStreamIndex);
        }

        // --- Issue #59: AllowTranscoding:false must forbid every real re-encode, on every branch,
        // while AllowDirectStream:true must keep permitting a genuine remux / stream-copy.
        //
        // AllowTranscoding:false reaches StreamBuilder as MediaSourceInfo.SupportsTranscoding=false
        // (MediaInfoHelper: "if (!enableTranscoding) mediaSource.SupportsTranscoding = false"), and
        // AllowDirectStream:true as SupportsDirectStream=true. That is the exact combination that
        // used to slip a real re-encode through GetVideoTranscodeProfile's OR guard.

        /// <summary>
        /// Matrix row 3 - codecs the client cannot decode, so the only remaining plan is a real
        /// re-encode. AllowTranscoding:false must yield NO plan at all rather than a transcode.
        /// This is the #59 regression: before the fix these cases returned PlayMethod.Transcode
        /// (the enum's default is Transcode = 0) and the server went on to actually re-encode.
        /// </summary>
        [Theory]
        // Full transcode in the baseline suite above (video codec is re-encoded):
        [InlineData("AndroidTVExoPlayer", "mkv-vp9-vorbis-vtt-2600k")]
        [InlineData("AndroidTVExoPlayer-NoHevcRotation", "mp4-hevc-aac-4000k-r180")]
        [InlineData("LowBandwidth", "mkv-vp9-vorbis-vtt-2600k")]
        public async Task BuildVideoItem_ReEncodeRequired_TranscodingForbidden_YieldsNoPlan(string deviceName, string mediaSource)
        {
            var options = await GetMediaOptions(deviceName, mediaSource);
            var source = options.MediaSources[0];

            // AllowTranscoding:false + AllowDirectStream:true
            source.SupportsTranscoding = false;
            source.SupportsDirectStream = true;

            var streamInfo = GetStreamBuilder().GetOptimalVideoStream(options);

            // No viable plan. Asserting null (not merely "PlayMethod != Transcode") matters: because
            // PlayMethod is a non-nullable enum defaulting to Transcode, a half-built StreamInfo
            // would still read as Transcode downstream.
            Assert.Null(streamInfo);
        }

        /// <summary>
        /// Matrix row 4 - non-regression: the very same sources still transcode normally when
        /// AllowTranscoding is true. Guards against the fix degenerating into a blanket refusal.
        /// </summary>
        [Theory]
        [InlineData("AndroidTVExoPlayer", "mkv-vp9-vorbis-vtt-2600k")]
        [InlineData("AndroidTVExoPlayer-NoHevcRotation", "mp4-hevc-aac-4000k-r180")]
        [InlineData("LowBandwidth", "mkv-vp9-vorbis-vtt-2600k")]
        public async Task BuildVideoItem_ReEncodeRequired_TranscodingAllowed_StillTranscodes(string deviceName, string mediaSource)
        {
            var options = await GetMediaOptions(deviceName, mediaSource);
            var source = options.MediaSources[0];

            source.SupportsTranscoding = true;
            source.SupportsDirectStream = true;

            var streamInfo = GetStreamBuilder().GetOptimalVideoStream(options);

            Assert.NotNull(streamInfo);
            Assert.Equal(PlayMethod.Transcode, streamInfo.PlayMethod);

            // A real re-encode: the source video codec is genuinely absent from the output codecs.
            var sourceVideoCodecs = source.MediaStreams
                .Where(stream => stream.Type == MediaStreamType.Video)
                .Select(stream => stream.Codec);
            Assert.All(sourceVideoCodecs, codec => Assert.DoesNotContain(codec, streamInfo.VideoCodecs));
        }

        /// <summary>
        /// Matrix row 2 - the STOP condition. Container is not directly playable but every codec is
        /// copyable, so this is a genuine remux. AllowDirectStream:true must keep it working even
        /// though AllowTranscoding is false: the fix keys on the method actually required, so a
        /// plan that copies every stream is never treated as a transcode.
        /// </summary>
        [Theory]
        // Baseline suite classifies these as PlayMethod.Transcode in "Remux" mode: video and audio
        // are both stream-copied, only the container changes.
        [InlineData("WebOS-23", "mkv-dvhe.08-eac3-15200k")]
        [InlineData("WebOS-23", "mkv-dvhe.05-eac3-28000k")]
        public async Task BuildVideoItem_RemuxOnly_TranscodingForbidden_StillRemuxes(string deviceName, string mediaSource)
        {
            var options = await GetMediaOptions(deviceName, mediaSource);
            var source = options.MediaSources[0];

            source.SupportsTranscoding = false;
            source.SupportsDirectStream = true;

            var streamInfo = GetStreamBuilder().GetOptimalVideoStream(options);

            Assert.NotNull(streamInfo);

            // Nothing is re-encoded: both the video and the audio codec are carried through
            // unchanged. This is the assertion that would break if the fix over-blocked remuxes.
            var sourceVideo = source.MediaStreams.First(stream => stream.Type == MediaStreamType.Video);
            var sourceAudio = source.MediaStreams.First(stream => stream.Type == MediaStreamType.Audio);

            Assert.Contains(sourceVideo.Codec, streamInfo.TargetVideoCodec);
            Assert.Single(streamInfo.TargetVideoCodec);
            Assert.Contains(sourceAudio.Codec, streamInfo.AudioCodecs);
            Assert.Single(streamInfo.AudioCodecs);
        }

        /// <summary>
        /// Matrix row 1 - a directly playable source is untouched by the constraint: DirectPlay does
        /// not re-encode, so AllowTranscoding:false is irrelevant to it.
        /// </summary>
        [Theory]
        [InlineData("AndroidTVExoPlayer", "mp4-h264-aac-vtt-2600k")]
        [InlineData("AndroidTVExoPlayer", "mkv-vp9-aac-srt-2600k")]
        public async Task BuildVideoItem_DirectPlayable_TranscodingForbidden_StillDirectPlays(string deviceName, string mediaSource)
        {
            var options = await GetMediaOptions(deviceName, mediaSource);
            var source = options.MediaSources[0];

            source.SupportsTranscoding = false;
            source.SupportsDirectStream = true;

            var streamInfo = GetStreamBuilder().GetOptimalVideoStream(options);

            Assert.NotNull(streamInfo);
            Assert.Equal(PlayMethod.DirectPlay, streamInfo.PlayMethod);
        }

        /// <summary>
        /// Matrix row 5 - every method forbidden. The pre-existing "neither transcoding nor direct
        /// streaming is permitted" guard still applies and no plan is produced.
        /// </summary>
        [Fact]
        public async Task BuildVideoItem_AllMethodsForbidden_YieldsNoPlan()
        {
            var options = await GetMediaOptions("AndroidTVExoPlayer", "mkv-vp9-vorbis-vtt-2600k");
            var source = options.MediaSources[0];

            source.SupportsTranscoding = false;
            source.SupportsDirectStream = false;
            source.SupportsDirectPlay = false;

            var streamInfo = GetStreamBuilder().GetOptimalVideoStream(options);

            Assert.Null(streamInfo);
        }

        private StreamInfo? BuildVideoItemSimpleTest(MediaOptions options, PlayMethod? playMethod, TranscodeReason why, string transcodeMode, string transcodeProtocol)
        {
            if (string.IsNullOrEmpty(transcodeProtocol))
            {
                transcodeProtocol = "HLS.ts";
            }

            var builder = GetStreamBuilder();

            var streamInfo = builder.GetOptimalVideoStream(options);
            Assert.NotNull(streamInfo);

            if (playMethod is not null)
            {
                Assert.Equal(playMethod, streamInfo.PlayMethod);
            }

            Assert.Equal(why, streamInfo.TranscodeReasons);

            var audioStreamIndexInput = options.AudioStreamIndex;
            var targetVideoStream = streamInfo.TargetVideoStream;
            var targetAudioStream = streamInfo.TargetAudioStream;

            var mediaSource = options.MediaSources.First(source => source.Id == streamInfo.MediaSourceId);
            Assert.NotNull(mediaSource);
            var videoStreams = mediaSource.MediaStreams.Where(stream => stream.Type == MediaStreamType.Video);
            var audioStreams = mediaSource.MediaStreams.Where(stream => stream.Type == MediaStreamType.Audio);
            // TODO: Check AudioStreamIndex vs options.AudioStreamIndex
            var inputAudioStream = mediaSource.GetDefaultAudioStream(audioStreamIndexInput ?? mediaSource.DefaultAudioStreamIndex);

            var uri = ParseUri(streamInfo);

            if (playMethod == PlayMethod.DirectPlay)
            {
                // Check expected container
                var containers = mediaSource.Container.Split(',');
                Assert.Contains(uri.Extension, containers);
                // TODO: Test transcode too

                // Check expected video codec (1)
                if (targetVideoStream?.Codec is not null)
                {
                    Assert.Contains(targetVideoStream?.Codec, streamInfo.TargetVideoCodec);
                    Assert.Single(streamInfo.TargetVideoCodec);
                }

                if (targetAudioStream?.Codec is not null)
                {
                    // Check expected audio codecs (1)
                    Assert.Contains(targetAudioStream?.Codec, streamInfo.TargetAudioCodec);
                    Assert.Single(streamInfo.TargetAudioCodec);
                }

                if (transcodeMode.Equals("DirectStream", StringComparison.Ordinal))
                {
                    Assert.Equal(streamInfo.Container, uri.Extension);
                }
            }
            else if (playMethod == PlayMethod.Transcode)
            {
                Assert.NotNull(streamInfo.Container);
                Assert.NotEmpty(streamInfo.VideoCodecs);
                Assert.NotEmpty(streamInfo.AudioCodecs);

                // Check expected container (todo: this could be a test param)
                if (transcodeProtocol.Equals("http", StringComparison.Ordinal))
                {
                    // Assert.Equal("webm", val.Container);
                    Assert.Equal(streamInfo.Container, uri.Extension);
                    Assert.Equal("stream", uri.Filename);
                    Assert.Equal(MediaStreamProtocol.http, streamInfo.SubProtocol);
                }
                else if (transcodeProtocol.Equals("HLS.mp4", StringComparison.Ordinal))
                {
                    Assert.Equal("mp4", streamInfo.Container);
                    Assert.Equal("m3u8", uri.Extension);
                    Assert.Equal("master", uri.Filename);
                    Assert.Equal(MediaStreamProtocol.hls, streamInfo.SubProtocol);
                }
                else
                {
                    Assert.Equal("ts", streamInfo.Container);
                    Assert.Equal("m3u8", uri.Extension);
                    Assert.Equal("master", uri.Filename);
                    Assert.Equal(MediaStreamProtocol.hls, streamInfo.SubProtocol);
                }

                // Full transcode
                if (transcodeMode.Equals("Transcode", StringComparison.Ordinal))
                {
                    if ((streamInfo.TranscodeReasons & (StreamBuilder.ContainerReasons | TranscodeReason.DirectPlayError | TranscodeReason.VideoRangeTypeNotSupported)) == 0)
                    {
                        Assert.All(
                            videoStreams,
                            stream => Assert.DoesNotContain(stream.Codec, streamInfo.VideoCodecs));
                    }

                    // TODO: fill out tests here
                }

                // DirectStream and Remux
                else
                {
                    // Check expected video codec (1)
                    Assert.Contains(targetVideoStream?.Codec, streamInfo.TargetVideoCodec);
                    Assert.Single(streamInfo.TargetVideoCodec);

                    if (transcodeMode.Equals("DirectStream", StringComparison.Ordinal))
                    {
                        // Check expected audio codecs (1)
                        if (targetAudioStream?.IsExternal == false)
                        {
                            // Check expected audio codecs (1)
                            if ((why & TranscodeReason.AudioChannelsNotSupported) == 0)
                            {
                                Assert.DoesNotContain(targetAudioStream.Codec, streamInfo.AudioCodecs);
                            }
                            else
                            {
                                Assert.Equal(targetAudioStream.Channels, streamInfo.TranscodingMaxAudioChannels);
                            }
                        }
                    }
                    else if (transcodeMode.Equals("Remux", StringComparison.Ordinal))
                    {
                        // Check expected audio codecs (1)
                        Assert.Contains(targetAudioStream?.Codec, streamInfo.AudioCodecs);
                        Assert.Single(streamInfo.AudioCodecs);
                    }

                    // Video details
                    var videoStream = targetVideoStream;
                    Assert.False(streamInfo.EstimateContentLength);
                    Assert.Equal(TranscodeSeekInfo.Auto, streamInfo.TranscodeSeekInfo);
                    Assert.Contains(videoStream?.Profile?.ToLowerInvariant() ?? string.Empty, streamInfo.TargetVideoProfile?.Split(",").Select(s => s.ToLowerInvariant()) ?? Array.Empty<string>());
                    Assert.Equal(videoStream?.Level, streamInfo.TargetVideoLevel);
                    Assert.Equal(videoStream?.BitDepth, streamInfo.TargetVideoBitDepth);
                    Assert.InRange(streamInfo.VideoBitrate.GetValueOrDefault(), videoStream?.BitRate.GetValueOrDefault() ?? 0, int.MaxValue);

                    // Audio codec not supported
                    if ((why & TranscodeReason.AudioCodecNotSupported) != 0)
                    {
                        // Audio stream specified
                        if (options.AudioStreamIndex >= 0)
                        {
                            // TODO:fixme
                            if (targetAudioStream?.IsExternal == false)
                            {
                                Assert.DoesNotContain(targetAudioStream.Codec, streamInfo.AudioCodecs);
                            }
                        }

                        // Audio stream not specified
                        else
                        {
                            bool isDefault = targetAudioStream?.IsDefault == true;
                            var language = targetAudioStream?.Language;

                            // Collect candidate audio streams
                            var candidateAudioStreams = audioStreams.Where(stream =>
                            {
                                return isDefault ? stream.IsDefault : (stream.Language == language);
                            });

                            Assert.All(candidateAudioStreams, stream =>
                            {
                                if (!stream.IsExternal)
                                {
                                    Assert.DoesNotContain(stream.Codec, streamInfo.AudioCodecs);
                                }
                            });
                        }
                    }
                }
            }
            else if (playMethod is null)
            {
                Assert.Equal(MediaStreamProtocol.http, streamInfo.SubProtocol);
                Assert.Equal("stream", uri.Filename);

                Assert.False(streamInfo.EstimateContentLength);
                Assert.Equal(TranscodeSeekInfo.Auto, streamInfo.TranscodeSeekInfo);
            }

            return streamInfo;
        }

        private static async ValueTask<T> TestData<T>(string name)
        {
            var path = Path.Join("Test Data", typeof(T).Name + "-" + name + ".json");

            using var stream = File.OpenRead(path);

            var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonDefaults.Options);
            if (value is not null)
            {
                return value;
            }

            throw new SerializationException("Invalid test data: " + name);
        }

        private StreamBuilder GetStreamBuilder()
        {
            var transcodeSupport = new Mock<ITranscoderSupport>();
            var logger = new NullLogger<StreamBuilderTests>();

            return new StreamBuilder(transcodeSupport.Object, logger);
        }

        private static async ValueTask<MediaOptions> GetMediaOptions(string deviceProfile, params string[] sources)
        {
            var mediaSources = sources.Select(src => TestData<MediaSourceInfo>(src))
                .Select(val => val.Result)
                .ToArray();
            var mediaSourceId = mediaSources[0]?.Id;

            var dp = await TestData<DeviceProfile>(deviceProfile);

            return new MediaOptions()
            {
                ItemId = new Guid("11D229B7-2D48-4B95-9F9B-49F6AB75E613"),
                MediaSourceId = mediaSourceId,
                MediaSources = mediaSources,
                DeviceId = "test-deviceId",
                Profile = dp,
                AllowAudioStreamCopy = true,
                AllowVideoStreamCopy = true,
                EnableDirectStream = false // This is disabled in server
            };
        }

        private static (string Path, NameValueCollection Query, string Filename, string Extension) ParseUri(StreamInfo val)
        {
            var href = val.ToUrl("media:", "ACCESSTOKEN", null).Split("?", 2);
            var path = href[0];

            var queryString = href.ElementAtOrDefault(1);
            var query = string.IsNullOrEmpty(queryString) ? global::System.Web.HttpUtility.ParseQueryString(queryString ?? string.Empty) : new NameValueCollection();

            var filename = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            if (extension.Length > 0)
            {
                extension = extension.Substring(1);
            }

            return (path, query, filename, extension);
        }

        [Theory]
        // EnableSubtitleExtraction = false, internal subtitles
        [InlineData("srt", "srt", false, false, PlayMethod.Transcode, SubtitleDeliveryMethod.Encode)]
        [InlineData("srt", "srt", false, false, PlayMethod.DirectPlay, SubtitleDeliveryMethod.External)]
        [InlineData("pgssub", "pgssub", false, false, PlayMethod.Transcode, SubtitleDeliveryMethod.Encode)]
        [InlineData("pgssub", "pgssub", false, false, PlayMethod.DirectPlay, SubtitleDeliveryMethod.External)]
        [InlineData("pgssub", "srt", false, false, PlayMethod.Transcode, SubtitleDeliveryMethod.Encode)]
        // EnableSubtitleExtraction = false, external subtitles
        [InlineData("srt", "srt", false, true, PlayMethod.Transcode, SubtitleDeliveryMethod.External)]
        // EnableSubtitleExtraction = true, internal subtitles
        [InlineData("srt", "srt", true, false, PlayMethod.Transcode, SubtitleDeliveryMethod.External)]
        [InlineData("pgssub", "pgssub", true, false, PlayMethod.Transcode, SubtitleDeliveryMethod.External)]
        [InlineData("pgssub", "pgssub", true, false, PlayMethod.DirectPlay, SubtitleDeliveryMethod.External)]
        [InlineData("pgssub", "srt", true, false, PlayMethod.Transcode, SubtitleDeliveryMethod.Encode)]
        // EnableSubtitleExtraction = true, external subtitles
        [InlineData("srt", "srt", true, true, PlayMethod.Transcode, SubtitleDeliveryMethod.External)]
        public void GetSubtitleProfile_RespectsExtractionSetting(
            string codec,
            string profileFormat,
            bool enableSubtitleExtraction,
            bool isExternal,
            PlayMethod playMethod,
            SubtitleDeliveryMethod expectedMethod)
        {
            var mediaSource = new MediaSourceInfo();
            var subtitleStream = new MediaStream
            {
                Type = MediaStreamType.Subtitle,
                Index = 0,
                IsExternal = isExternal,
                Path = isExternal ? "/media/sub." + codec : null,
                Codec = codec,
                SupportsExternalStream = MediaStream.IsTextFormat(codec)
            };

            var subtitleProfiles = new[]
            {
                new SubtitleProfile { Format = profileFormat, Method = SubtitleDeliveryMethod.External }
            };

            var transcoderSupport = new Mock<ITranscoderSupport>();
            transcoderSupport.Setup(t => t.CanExtractSubtitles(It.IsAny<string>())).Returns(enableSubtitleExtraction);

            var result = StreamBuilder.GetSubtitleProfile(
                mediaSource,
                subtitleStream,
                subtitleProfiles,
                playMethod,
                transcoderSupport.Object,
                null,
                null);

            Assert.Equal(expectedMethod, result.Method);
        }

        [Theory]
        // External text subs embedded into MKV when transcoding (#16403)
        [InlineData("srt", true, PlayMethod.Transcode, "mkv", MediaStreamProtocol.http, SubtitleDeliveryMethod.Embed)]
        [InlineData("ass", true, PlayMethod.Transcode, "mkv", MediaStreamProtocol.http, SubtitleDeliveryMethod.Embed)]
        // External graphical subs embedded into MKV when transcoding
        [InlineData("pgssub", true, PlayMethod.Transcode, "mkv", MediaStreamProtocol.http, SubtitleDeliveryMethod.Embed)]
        [InlineData("dvdsub", true, PlayMethod.Transcode, "mkv", MediaStreamProtocol.http, SubtitleDeliveryMethod.Embed)]
        // External subs remain external when transcoding to non-MKV containers
        [InlineData("srt", true, PlayMethod.Transcode, "mp4", MediaStreamProtocol.hls, SubtitleDeliveryMethod.External)]
        [InlineData("srt", true, PlayMethod.Transcode, "ts", MediaStreamProtocol.hls, SubtitleDeliveryMethod.External)]
        // External subs remain external during DirectPlay even with MKV
        [InlineData("srt", true, PlayMethod.DirectPlay, "mkv", null, SubtitleDeliveryMethod.External)]
        // Internal subs still embedded into MKV when transcoding (existing behavior)
        [InlineData("srt", false, PlayMethod.Transcode, "mkv", MediaStreamProtocol.http, SubtitleDeliveryMethod.Embed)]
        [InlineData("pgssub", false, PlayMethod.Transcode, "mkv", MediaStreamProtocol.http, SubtitleDeliveryMethod.Embed)]
        public void GetSubtitleProfile_ReturnsExpectedDeliveryMethod(
            string codec,
            bool isExternal,
            PlayMethod playMethod,
            string outputContainer,
            MediaStreamProtocol? transcodingSubProtocol,
            SubtitleDeliveryMethod expectedMethod)
        {
            var mediaSource = new MediaSourceInfo();
            var subtitleStream = new MediaStream
            {
                Codec = codec,
                Language = "eng",
                IsExternal = isExternal,
                Type = MediaStreamType.Subtitle,
                SupportsExternalStream = true
            };

            var subtitleProfiles = new[]
            {
                new SubtitleProfile { Format = codec, Method = SubtitleDeliveryMethod.Embed },
                new SubtitleProfile { Format = codec, Method = SubtitleDeliveryMethod.External }
            };

            var transcoderSupport = new Mock<ITranscoderSupport>();
            transcoderSupport.Setup(x => x.CanExtractSubtitles(It.IsAny<string>())).Returns(true);

            var result = StreamBuilder.GetSubtitleProfile(
                mediaSource,
                subtitleStream,
                subtitleProfiles,
                playMethod,
                transcoderSupport.Object,
                outputContainer,
                transcodingSubProtocol);

            Assert.Equal(expectedMethod, result.Method);
        }
    }
}
