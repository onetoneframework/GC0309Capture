using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GC0309Endoscope
{
	// Writes indexed, uncompressed YUY2 video to an AVI 1.0 stream.
	internal sealed class AviRecording : IDisposable
	{
		private const uint HasIndex = 0x10;
		private const uint KeyFrame = 0x10;
		private const long MaximumFileLength = 2000000000;
		private const int MicrosecondsPerSecond = 1000000;
		private const int HeaderLength = 56;
		private const int FormatLength = 40;
		private const uint HeaderListLength = 192;
		private const uint StreamListLength = 116;
		private const int IndexEntryLength = 16;
		private const int RiffSizeOffset = 4;
		private const int ChunkHeaderLength = 8;
		private const int MaximumFrameRate = 60;
		private const ushort Yuy2BitDepth = 16;
		private readonly BinaryWriter writer;
		private readonly List<uint> offsets = new List<uint>();
		private readonly RawFrame format;
		private long listLengthPosition;
		private long moviePosition;
		private long totalFramesPosition;
		private long streamFramesPosition;
		private bool initialized;
		private bool disposed;

		internal AviRecording(Stream destination, RawFrame format)
		{
			if (destination == null || !destination.CanWrite || !destination.CanSeek || destination.Position != 0 || destination.Length != 0) { throw new ArgumentException("A writable, seekable empty stream is required."); }
			if (format == null) { throw new ArgumentNullException("format"); }
			writer = new BinaryWriter(destination, Encoding.ASCII, true);
			this.format = format;
		}

		internal void Initialize(int framesPerSecond)
		{
			if (initialized || disposed || framesPerSecond <= 0 || framesPerSecond > MaximumFrameRate) { throw new InvalidOperationException("Invalid AVI initialization."); }
			writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(0U); writer.Write(Encoding.ASCII.GetBytes("AVI "));
			writer.Write(Encoding.ASCII.GetBytes("LIST")); writer.Write(HeaderListLength); writer.Write(Encoding.ASCII.GetBytes("hdrl"));
			writer.Write(Encoding.ASCII.GetBytes("avih")); writer.Write(HeaderLength);
			writer.Write(MicrosecondsPerSecond / framesPerSecond); writer.Write(format.Pixels.Length * framesPerSecond); writer.Write(0U); writer.Write(HasIndex);
			totalFramesPosition = writer.BaseStream.Position;
			writer.Write(0U); writer.Write(0U); writer.Write(1U); writer.Write(format.Pixels.Length); writer.Write(format.Width); writer.Write(format.Height);
			for (int index = 0; index < 4; index++) { writer.Write(0U); }
			writer.Write(Encoding.ASCII.GetBytes("LIST")); writer.Write(StreamListLength); writer.Write(Encoding.ASCII.GetBytes("strl"));
			writer.Write(Encoding.ASCII.GetBytes("strh")); writer.Write(HeaderLength);
			writer.Write(Encoding.ASCII.GetBytes("vidsYUY2")); writer.Write(0U); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0U);
			writer.Write(1U); writer.Write(framesPerSecond); writer.Write(0U);
			streamFramesPosition = writer.BaseStream.Position;
			writer.Write(0U); writer.Write(format.Pixels.Length); writer.Write(UInt32.MaxValue); writer.Write(0U);
			writer.Write((short)0); writer.Write((short)0); writer.Write((short)format.Width); writer.Write((short)format.Height);
			writer.Write(Encoding.ASCII.GetBytes("strf")); writer.Write(FormatLength);
			writer.Write(FormatLength); writer.Write(format.Width); writer.Write(format.Height); writer.Write((ushort)1); writer.Write(Yuy2BitDepth);
			writer.Write(Encoding.ASCII.GetBytes("YUY2")); writer.Write(format.Pixels.Length);
			for (int index = 0; index < 4; index++) { writer.Write(0U); }
			writer.Write(Encoding.ASCII.GetBytes("LIST")); listLengthPosition = writer.BaseStream.Position;
			writer.Write(0U); moviePosition = writer.BaseStream.Position; writer.Write(Encoding.ASCII.GetBytes("movi"));
			initialized = true;
		}

		internal void AddFrame(RawFrame frame)
		{
			if (!initialized || disposed) { throw new InvalidOperationException("Initialize the AVI before adding frames."); }
			if (frame == null || frame.Width != format.Width || frame.Height != format.Height) { throw new ArgumentException("Recording dimensions cannot change."); }
			if (writer.BaseStream.Position + ChunkHeaderLength + frame.Pixels.Length + ChunkHeaderLength + (offsets.Count + 1L) * IndexEntryLength >= MaximumFileLength) { throw new IOException("The AVI recording reached its size limit."); }
			offsets.Add(checked((uint)(writer.BaseStream.Position - moviePosition)));
			writer.Write(Encoding.ASCII.GetBytes("00db")); writer.Write(frame.Pixels.Length); writer.Write(frame.Pixels);
		}

		public void Dispose()
		{
			if (disposed) { return; }
			disposed = true;
			if (initialized)
			{
				long movieEnd = writer.BaseStream.Position;
				writer.Write(Encoding.ASCII.GetBytes("idx1")); writer.Write(offsets.Count * IndexEntryLength);
				foreach (uint offset in offsets)
				{
					writer.Write(Encoding.ASCII.GetBytes("00db")); writer.Write(KeyFrame); writer.Write(offset); writer.Write(format.Pixels.Length);
				}
				long end = writer.BaseStream.Position;
				writer.BaseStream.Position = RiffSizeOffset; writer.Write(checked((uint)(end - ChunkHeaderLength)));
				writer.BaseStream.Position = listLengthPosition; writer.Write(checked((uint)(movieEnd - moviePosition)));
				writer.BaseStream.Position = totalFramesPosition; writer.Write(offsets.Count);
				writer.BaseStream.Position = streamFramesPosition; writer.Write(offsets.Count);
				writer.BaseStream.Position = end;
			}
			writer.Flush(); writer.Dispose();
		}
	}
}
