﻿using System;
using System.Diagnostics.Contracts;

namespace SharpAvi.Output
{
    internal class AviVideoStream : AviStreamBase, IAviVideoStreamInternal
    {
        private readonly IAviStreamWriteHandler writeHandler;
        private FourCC streamCodec;
        private int width;
        private int height;
        private BitsPerPixel bitsPerPixel;

        public AviVideoStream(int index, IAviStreamWriteHandler writeHandler, 
            int width, int height, BitsPerPixel bitsPerPixel)
            : base(index)
        {
            Contract.Requires(index >= 0);
            Contract.Requires(writeHandler != null);
            Contract.Requires(width > 0);
            Contract.Requires(height > 0);
            Contract.Requires(Enum.IsDefined(typeof(BitsPerPixel), bitsPerPixel));

            this.writeHandler = writeHandler;
            this.width = width;
            this.height = height;
            this.bitsPerPixel = bitsPerPixel;
            this.streamCodec = KnownFourCCs.Codecs.Uncompressed;
            FramesWritten = 0;
        }


        public int Width
        {
            get { return width; }
            set
            {
                CheckNotFrozen();
                width = value;
            }
        }

        public int Height
        {
            get { return height; }
            set
            {
                CheckNotFrozen();
                height = value;
            }
        }

        public BitsPerPixel BitsPerPixel
        {
            get { return bitsPerPixel; }
            set
            {
                CheckNotFrozen();
                bitsPerPixel = value;
            }
        }

        public FourCC Codec
        {
            get { return streamCodec; }
            set
            {
                CheckNotFrozen();
                streamCodec = value;
            }
        }

        public void WriteFrame(bool isKeyFrame, byte[] frameData, int startIndex, int count)
        {
            writeHandler.WriteVideoFrame(this, isKeyFrame, frameData, startIndex, count);
            FramesWritten++;
        }

        public int FramesWritten
        {
            get;
            private set;
        }


        public override FourCC StreamType
        {
            get { return KnownFourCCs.StreamTypes.Video; }
        }

        protected override FourCC GenerateChunkId()
        {
            return KnownFourCCs.Chunks.VideoFrame(Index, Codec != KnownFourCCs.Codecs.Uncompressed);
        }

        public override void WriteHeader()
        {
            writeHandler.WriteStreamHeader(this);
        }

        public override void WriteFormat()
        {
            writeHandler.WriteStreamFormat(this);
        }
    }
}
