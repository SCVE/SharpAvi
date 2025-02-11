﻿using SharpAvi.Format;
using SharpAvi.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace SharpAvi.Codecs
{
    /// <summary>
    /// Encodes video stream in MPEG-4 format using one of VCM codecs installed on the system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supported codecs include Microsoft MPEG-4 V3 and V2, Xvid, DivX and x264vfw.
    /// The codec to be used is selected from the ones installed on the system.
    /// The encoder can be forced to use MPEG-4 codecs that are not explicitly supported. However, in this case
    /// it is not guaranteed to work properly.
    /// </para>
    /// <para>
    /// For <c>x264vfw</c> codec, it is recommended to enable <c>Zero Latency</c> option in its settings.
    /// 64-bit support is limited, as there are no 64-bit versions of Microsoft and DivX codecs, 
    /// and Xvid can produce some errors.
    /// </para>
    /// <para>
    /// In multi-threaded scenarios, like asynchronous encoding, it is recommended to wrap this encoder into
    /// <see cref="SingleThreadedVideoEncoderWrapper"/> for the stable work.
    /// </para>
    /// </remarks>
    public class Mpeg4VcmVideoEncoder : IVideoEncoder, IDisposable
    {
        /// <summary>
        /// Checks whether <see cref="Mpeg4VcmVideoEncoder"/> is supported on this platform.
        /// </summary>
        /// <returns><c>True</c> if supported, <c>false</c> otherwise.</returns>
        public static bool IsSupported() => Environment.OSVersion.Platform == PlatformID.Win32NT;

        private static void CheckSupportedPlatform()
        {
            if (!IsSupported())
            {
                throw new PlatformNotSupportedException($"{nameof(Mpeg4VcmVideoEncoder)} is only supported on the Windows platform.");
            }
        }

        /// <summary>
        /// Default preferred order of the supported codecs.
        /// </summary>
        public static ReadOnlyCollection<FourCC> DefaultCodecPreference { get; } 
            = new ReadOnlyCollection<FourCC>(
                new[]
                {
                    CodecIds.MicrosoftMpeg4V3,
                    CodecIds.MicrosoftMpeg4V2,
                    CodecIds.Xvid,
                    CodecIds.X264,
                    CodecIds.DivX,
                });

        /// <summary>
        /// Gets info about the supported codecs that are installed on the system.
        /// </summary>
        /// <exception cref="PlatformNotSupportedException">
        /// Running not on Windows.
        /// </exception>
        public static CodecInfo[] GetAvailableCodecs()
        {
            CheckSupportedPlatform();

            var result = new List<CodecInfo>();

            var inBitmapInfo = CreateBitmapInfo(8, 8, 32, CodecIds.Uncompressed);
            inBitmapInfo.ImageSize = (uint)4;

            foreach (var codec in DefaultCodecPreference)
            {
                var outBitmapInfo = CreateBitmapInfo(8, 8, 24, codec);
                VfwApi.CompressorInfo compressorInfo;
                var compressorHandle = GetCompressor(inBitmapInfo, outBitmapInfo, out compressorInfo);
                if (compressorHandle != IntPtr.Zero)
                {
                    VfwApi.ICClose(compressorHandle);
                    result.Add(new CodecInfo(codec, compressorInfo.Description));
                }
            }

            return result.ToArray();
        }

        private static IntPtr GetCompressor(VfwApi.BitmapInfoHeader inBitmapInfo, VfwApi.BitmapInfoHeader outBitmapInfo, out VfwApi.CompressorInfo compressorInfo)
        {
            // Using ICLocate is time-consuming. Besides, it does not clean up something, so the process does not terminate on exit.
            // Instead open a specific codec and query it for needed features.

            var compressorHandle = VfwApi.ICOpen((uint)KnownFourCCs.CodecTypes.Video, outBitmapInfo.Compression, VfwApi.ICMODE_COMPRESS);

            if (compressorHandle != IntPtr.Zero)
            {
                var inHeader = inBitmapInfo;
                var outHeader = outBitmapInfo;
                var result = VfwApi.ICSendMessage(compressorHandle, VfwApi.ICM_COMPRESS_QUERY, ref inHeader, ref outHeader);

                if (result == VfwApi.ICERR_OK)
                {
                    var infoSize = VfwApi.ICGetInfo(compressorHandle, out compressorInfo, Marshal.SizeOf(typeof(VfwApi.CompressorInfo)));
                    if (infoSize > 0 && compressorInfo.SupportsFastTemporalCompression)
                        return compressorHandle;
                }

                VfwApi.ICClose(compressorHandle);
            }

            compressorInfo = new VfwApi.CompressorInfo();
            return IntPtr.Zero;
        }

        private static VfwApi.BitmapInfoHeader CreateBitmapInfo(int width, int height, ushort bitCount, FourCC codec)
        {
            return new VfwApi.BitmapInfoHeader
            {
                SizeOfStruct = (uint)Marshal.SizeOf(typeof(VfwApi.BitmapInfoHeader)),
                Width = width,
                Height = height,
                BitCount = (ushort)bitCount,
                Planes = 1,
                Compression = (uint)codec,
            };
        }


        private readonly int width;
        private readonly int height;
        private readonly byte[] sourceBuffer;
        private readonly VfwApi.BitmapInfoHeader inBitmapInfo;
        private readonly VfwApi.BitmapInfoHeader outBitmapInfo;
        private readonly IntPtr compressorHandle;
        private readonly VfwApi.CompressorInfo compressorInfo;
        private readonly int maxEncodedSize;
        private readonly int quality;
        private readonly int keyFrameRate;


        private int frameIndex = 0;
        private int framesFromLastKey;
        private bool isDisposed;
        private bool needEnd;

        /// <summary>
        /// Creates a new instance of <see cref="Mpeg4VcmVideoEncoder"/>.
        /// </summary>
        /// <param name="width">Frame width.</param>
        /// <param name="height">Frame height.</param>
        /// <param name="fps">Frame rate.</param>
        /// <param name="frameCount">
        /// Number of frames to be encoded.
        /// If not known, specify 0.
        /// </param>
        /// <param name="quality">
        /// Compression quality in the range [1..100].
        /// Less values mean less size and lower image quality.
        /// </param>
        /// <param name="codecPreference">
        /// List of codecs that can be used by this encoder, in preferred order.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// No compatible codec was found in the system.
        /// </exception>
        /// <exception cref="PlatformNotSupportedException">
        /// Running not on Windows.
        /// </exception>
        /// <remarks>
        /// <para>
        /// It is not guaranteed that the codec will respect the specified <paramref name="quality"/> value.
        /// This depends on its implementation.
        /// </para>
        /// <para>
        /// If no preferred codecs are specified, then <see cref="DefaultCodecPreference"/> is used.
        /// MPEG-4 codecs that are not explicitly supported can be specified. However, in this case
        /// the encoder is not guaranteed to work properly.
        /// </para>
        /// </remarks>
        public Mpeg4VcmVideoEncoder(int width, int height, double fps, int frameCount, int quality, params FourCC[] codecPreference)
        {
            Argument.IsPositive(width, nameof(width));
            Argument.IsPositive(height, nameof(height));
            Argument.IsPositive(fps, nameof(fps));
            Argument.IsNotNegative(frameCount, nameof(frameCount));
            Argument.IsInRange(quality, 1, 100, nameof(quality));

            CheckSupportedPlatform();

            this.width = width;
            this.height = height;
            sourceBuffer = new byte[width * height * 4];

            inBitmapInfo = CreateBitmapInfo(width, height, 32, CodecIds.Uncompressed);
            inBitmapInfo.ImageSize = (uint)sourceBuffer.Length;

            if (codecPreference == null || codecPreference.Length == 0)
            {
                codecPreference = DefaultCodecPreference.ToArray();
            }
            foreach (var codec in codecPreference)
            {
                outBitmapInfo = CreateBitmapInfo(width, height, 24, codec);
                compressorHandle = GetCompressor(inBitmapInfo, outBitmapInfo, out compressorInfo);
                if (compressorHandle != IntPtr.Zero)
                    break;
            }

            if (compressorHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("No compatible MPEG-4 encoder found.");
            }

            try
            {
                maxEncodedSize = GetMaxEncodedSize();

                // quality for ICM ranges from 0 to 10000
                this.quality = compressorInfo.SupportsQuality ? quality * 100 : 0;

                // typical key frame rate ranges from FPS to 2*FPS
                keyFrameRate = (int)Math.Round((2 - 0.01 * quality) * fps);

                if (compressorInfo.RequestsCompressFrames)
                {
                    InitCompressFramesInfo(fps, frameCount);
                }

                StartCompression();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// Performs any necessary cleanup before this instance is garbage-collected.
        /// </summary>
        ~Mpeg4VcmVideoEncoder()
        {
            Dispose();
        }

        private int GetMaxEncodedSize()
        {
            var inHeader = inBitmapInfo;
            var outHeader = outBitmapInfo;
            return VfwApi.ICSendMessage(compressorHandle, VfwApi.ICM_COMPRESS_GET_SIZE, ref inHeader, ref outHeader);
        }

        private void InitCompressFramesInfo(double fps, int frameCount)
        {
            var info = new VfwApi.CompressFramesInfo
            {
                StartFrame = 0,
                FrameCount = frameCount,
                Quality = quality,
                KeyRate = keyFrameRate,
            };
            AviUtils.SplitFrameRate((decimal)fps, out info.FrameRateNumerator, out info.FrameRateDenominator);

            var result = VfwApi.ICSendMessage(compressorHandle, VfwApi.ICM_COMPRESS_FRAMES_INFO, ref info, Marshal.SizeOf(typeof(VfwApi.CompressFramesInfo)));
            CheckICResult(result);
        }

        private void StartCompression()
        {
            var inHeader = inBitmapInfo;
            var outHeader = outBitmapInfo;
            var result = VfwApi.ICSendMessage(compressorHandle, VfwApi.ICM_COMPRESS_BEGIN, ref inHeader, ref outHeader);
            CheckICResult(result);

            needEnd = true;
            framesFromLastKey = keyFrameRate;
        }

        private void EndCompression()
        {
            var result = VfwApi.ICSendMessage(compressorHandle, VfwApi.ICM_COMPRESS_END, IntPtr.Zero, IntPtr.Zero);
            CheckICResult(result);
        }


        #region IVideoEncoder Members

        /// <summary>Video codec.</summary>
        public FourCC Codec => outBitmapInfo.Compression;

        /// <summary>Number of bits per pixel in the encoded image.</summary>
        public BitsPerPixel BitsPerPixel => BitsPerPixel.Bpp24;

        /// <summary>
        /// Maximum size of the encoded frame.
        /// </summary>
        public int MaxEncodedSize => maxEncodedSize;

        /// <summary>Encodes a frame.</summary>
        public int EncodeFrame(byte[] source, int srcOffset, byte[] destination, int destOffset, out bool isKeyFrame)
        {
            Argument.IsNotNull(source, nameof(source));
            Argument.IsNotNegative(srcOffset, nameof(srcOffset));
            Argument.ConditionIsMet(srcOffset + 4 * width * height <= source.Length,
                "Source end offset exceeds the source length.");
            Argument.IsNotNull(destination, nameof(destination));
            Argument.IsNotNegative(destOffset, nameof(destOffset));

#if NET5_0_OR_GREATER
            return EncodeFrame(source.AsSpan(srcOffset), destination.AsSpan(destOffset), out isKeyFrame);
#else
            BitmapUtils.FlipVertical(source, srcOffset, sourceBuffer, 0, height, width * 4);

            var sourceHandle = GCHandle.Alloc(sourceBuffer, GCHandleType.Pinned);
            var encodedHandle = GCHandle.Alloc(destination, GCHandleType.Pinned);
            try
            {
                var sourcePtr = sourceHandle.AddrOfPinnedObject();
                var encodedPtr = encodedHandle.AddrOfPinnedObject();

                return EncodeFrame(sourcePtr, encodedPtr, (uint)(destination.Length - destOffset), out isKeyFrame);
            }
            finally
            {
                sourceHandle.Free();
                encodedHandle.Free();
            }
#endif
        }

#if NET5_0_OR_GREATER
        /// <summary>Encodes a frame.</summary>
        public unsafe int EncodeFrame(ReadOnlySpan<byte> source, Span<byte> destination, out bool isKeyFrame)
        {
            Argument.ConditionIsMet(4 * width * height <= source.Length,
                "Source end offset exceeds the source length.");

            fixed (void* srcPtr = source, destPtr = destination)
            {
                var srcIntPtr = new IntPtr(srcPtr);
                var destIntPtr = new IntPtr(destPtr);
                return EncodeFrame(srcIntPtr, destIntPtr, (uint)destination.Length, out isKeyFrame);
            }
        }
#endif

        private int EncodeFrame(IntPtr sourcePtr, IntPtr destinationPtr, uint destinationSize, out bool isKeyFrame)
        {
            var outInfo = outBitmapInfo;
            outInfo.ImageSize = destinationSize;
            var inInfo = inBitmapInfo;
            var flags = framesFromLastKey >= keyFrameRate ? VfwApi.ICCOMPRESS_KEYFRAME : 0;

            var result = VfwApi.ICCompress(compressorHandle, flags,
                ref outInfo, destinationPtr, ref inInfo, sourcePtr,
                out _, out var outFlags, frameIndex,
                0, quality, IntPtr.Zero, IntPtr.Zero);
            CheckICResult(result);
            frameIndex++;


            isKeyFrame = (outFlags & VfwApi.AVIIF_KEYFRAME) == VfwApi.AVIIF_KEYFRAME;
            if (isKeyFrame)
            {
                framesFromLastKey = 1;
            }
            else
            {
                framesFromLastKey++;
            }

            return (int)outInfo.ImageSize;
        }

        #endregion


        #region IDisposable Members

        /// <summary>
        /// Releases all unmanaged resources used by the encoder.
        /// </summary>
        public void Dispose()
        {
            if (!isDisposed)
            {
                if (needEnd)
                    EndCompression();

                if (compressorHandle != IntPtr.Zero)
                    VfwApi.ICClose(compressorHandle);

                isDisposed = true;
                GC.SuppressFinalize(this);
            }
        }

        #endregion


        private void CheckICResult(int result)
        {
            if (result != VfwApi.ICERR_OK)
            {
                var errorDesc = VfwApi.GetErrorDescription(result);
                var resultStr = errorDesc == null
                    ? result.ToString()
                    : string.Format("{0} ({1})", result, errorDesc);
                throw new InvalidOperationException(string.Format("Encoder operation returned an error: {0}.", resultStr));
            }
        }
    }
}
