using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GC0309Endoscope
{
	internal sealed class RawFrame
	{
		internal readonly int Width;
		internal readonly int Height;
		internal readonly byte[] Pixels;

		internal RawFrame(int width, int height, byte[] pixels)
		{
			if (width <= 0 || height <= 0 || width % 2 != 0 || pixels == null || pixels.Length != checked(width * height * 2))
			{
				throw new ArgumentException("A complete YUY2 frame with an even width is required.");
			}
			Width = width;
			Height = height;
			Pixels = pixels;
		}
	}

	// Reassembles camera packets across USB transfers and accepts complete frames only.
	internal sealed class RawFrameDecoder
	{
		private const int QuarterTurnsPerRevolution = 4;
		private const int ClockwiseQuarterTurn = 1;
		private const int HalfTurn = 2;
		private const int CounterclockwiseQuarterTurn = 3;
		private const int PixelsPerChromaPair = 2;
		private const int BytesPerPixel = 2;
		private const int BlueChromaOffset = 1;
		private const int RedChromaOffset = 3;
		private const int HeaderLength = 12;
		private const int MaximumPayload = 3072;
		private const int QhdWidth = EndoscopeDevice.MaximumDiagnosticWidth;
		private const int QhdHeight = EndoscopeDevice.MaximumDiagnosticHeight;
		private const int QhdFrameLength = QhdWidth * QhdHeight * 2;
		private const int FullHdWidth = 1920;
		private const int FullHdHeight = 1080;
		private const int FullHdFrameLength = FullHdWidth * FullHdHeight * 2;
		private const int AdvertisedWidth = 1920;
		private const int AdvertisedHeight = 1440;
		private const int AdvertisedFrameLength = AdvertisedWidth * AdvertisedHeight * 2;
		private const int HdWidth = 1280;
		private const int HdHeight = 720;
		private const int HdFrameLength = HdWidth * HdHeight * 2;
		private const int MaximumFrame = QhdFrameLength;
		private const int MaximumTransfer = 65536;
		private const int PayloadLengthOffset = 6;
		private const byte HeaderFlagsMask = 0xF0;
		private const byte HeaderFlags = 0x80;
		private const byte FrameIdentifierMask = 1;
		private const double RedChrominance = 1.402;
		private const double GreenBlueChrominance = 0.344136;
		private const double GreenRedChrominance = 0.714136;
		private const double BlueChrominance = 1.772;
		private const int NeutralChrominance = 128;
		private const int MaximumComponent = 255;
		private const int SquareWidth = 450;
		private const int SquareHeight = 450;
		private const int SquareFrameLength = SquareWidth * SquareHeight * 2;
		private const int CompactWidth = 576;
		private const int CompactHeight = 432;
		private const int CompactFrameLength = CompactWidth * CompactHeight * 2;
		private const int WideWidth = 580;
		private const int WideHeight = 450;
		private const int WideFrameLength = WideWidth * WideHeight * 2;
		private const int VgaWidth = 640;
		private const int VgaHeight = 480;
		private const int VgaFrameLength = VgaWidth * VgaHeight * 2;
		private readonly byte[] pending = new byte[MaximumTransfer + MaximumPayload + HeaderLength];
		private readonly byte[] frame = new byte[MaximumFrame];
		private int pendingCount;
		private int frameCount;
		private int previousIdentifier = -1;
		private bool synchronized;
		private bool damaged;
		private readonly Size requestedResolution;
		internal int DroppedFrames { get; private set; }
		internal int InvalidBytes { get; private set; }

		internal RawFrameDecoder(Size requestedResolution = default(Size))
		{
			if (!requestedResolution.IsEmpty && (requestedResolution.Width <= 0 || requestedResolution.Width % 2 != 0 || requestedResolution.Height <= 0 || requestedResolution.Width > QhdWidth || requestedResolution.Height > QhdHeight))
			{
				throw new ArgumentException("Invalid diagnostic frame dimensions.", "requestedResolution");
			}
			this.requestedResolution = requestedResolution;
		}

		internal List<RawFrame> Feed(byte[] transfer, int count)
		{
			if (transfer == null || count < 0 || count > transfer.Length || count > MaximumTransfer) { throw new ArgumentException("Invalid USB transfer length."); }
			Buffer.BlockCopy(transfer, 0, pending, pendingCount, count);
			pendingCount += count;
			List<RawFrame> completed = new List<RawFrame>();
			int position = 0;
			// Each iteration consumes a byte or packet; an incomplete packet waits for the next transfer.
			while (position + HeaderLength <= pendingCount)
			{
				int length = (pending[position + PayloadLengthOffset] << 8) | pending[position + PayloadLengthOffset + 1];
				if (pending[position] != HeaderLength || (pending[position + 1] & HeaderFlagsMask) != HeaderFlags || length > MaximumPayload)
				{
					position++;
					InvalidBytes++;
					damaged = true;
					continue;
				}
				if (position + HeaderLength + length > pendingCount) { break; }
				int identifier = pending[position + 1] & FrameIdentifierMask;
				if (previousIdentifier != identifier)
				{
					if (previousIdentifier >= 0 && synchronized && !damaged)
					{
						int width = 0, height = 0;
						switch (frameCount)
						{
							case SquareFrameLength: width = SquareWidth; height = SquareHeight; break;
							case CompactFrameLength: width = CompactWidth; height = CompactHeight; break;
							case WideFrameLength: width = WideWidth; height = WideHeight; break;
							case VgaFrameLength: width = VgaWidth; height = VgaHeight; break;
							case QhdFrameLength: width = QhdWidth; height = QhdHeight; break;
							case FullHdFrameLength: width = FullHdWidth; height = FullHdHeight; break;
							case AdvertisedFrameLength: width = AdvertisedWidth; height = AdvertisedHeight; break;
							case HdFrameLength: width = HdWidth; height = HdHeight; break;
						}
						if (!requestedResolution.IsEmpty && frameCount == requestedResolution.Width * requestedResolution.Height * 2)
						{
							width = requestedResolution.Width;
							height = requestedResolution.Height;
						}
						if (width > 0)
						{
							byte[] pixels = new byte[frameCount];
							Buffer.BlockCopy(frame, 0, pixels, 0, frameCount);
							completed.Add(new RawFrame(width, height, pixels));
						}
						else { DroppedFrames++; }
					}
					else if (previousIdentifier >= 0) { DroppedFrames++; }
					synchronized = previousIdentifier >= 0;
					previousIdentifier = identifier;
					frameCount = 0;
					damaged = false;
				}
				if (length <= MaximumFrame - frameCount)
				{
					Buffer.BlockCopy(pending, position + HeaderLength, frame, frameCount, length);
					frameCount += length;
				}
				else { damaged = true; }
				position += HeaderLength + length;
			}
			pendingCount -= position;
			Buffer.BlockCopy(pending, position, pending, 0, pendingCount);
			return completed;
		}

		// Rotates luminance samples and resamples shared chroma into horizontal YUY2 pairs.
		internal static RawFrame Rotate(RawFrame source, int clockwiseQuarterTurns)
		{
			if (source == null) { throw new ArgumentNullException("source"); }
			if (clockwiseQuarterTurns < 0 || clockwiseQuarterTurns >= QuarterTurnsPerRevolution) { throw new ArgumentOutOfRangeException("clockwiseQuarterTurns"); }
			if (clockwiseQuarterTurns == 0) { return source; }
			bool swapsDimensions = clockwiseQuarterTurns != HalfTurn;
			if (swapsDimensions && source.Height % PixelsPerChromaPair != 0) { throw new ArgumentException("Quarter-turn YUY2 rotation requires an even source height.", "source"); }
			int width = swapsDimensions ? source.Height : source.Width;
			int height = swapsDimensions ? source.Width : source.Height;
			byte[] pixels = new byte[source.Pixels.Length];
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x += PixelsPerChromaPair)
				{
					int destination = (y * width + x) * BytesPerPixel;
					int blue = 0, red = 0;
					for (int pixel = 0; pixel < PixelsPerChromaPair; pixel++)
					{
						int sourceX, sourceY;
						switch (clockwiseQuarterTurns)
						{
							case ClockwiseQuarterTurn: sourceX = y; sourceY = source.Height - 1 - (x + pixel); break;
							case CounterclockwiseQuarterTurn: sourceX = source.Width - 1 - y; sourceY = x + pixel; break;
							default: sourceX = source.Width - 1 - (x + pixel); sourceY = source.Height - 1 - y; break;
						}
						int luminance = (sourceY * source.Width + sourceX) * BytesPerPixel;
						int pair = (sourceY * source.Width + sourceX - sourceX % PixelsPerChromaPair) * BytesPerPixel;
						pixels[destination + pixel * BytesPerPixel] = source.Pixels[luminance];
						blue += source.Pixels[pair + BlueChromaOffset];
						red += source.Pixels[pair + RedChromaOffset];
					}
					pixels[destination + BlueChromaOffset] = (byte)((blue + 1) / PixelsPerChromaPair);
					pixels[destination + RedChromaOffset] = (byte)((red + 1) / PixelsPerChromaPair);
				}
			}
			return new RawFrame(width, height, pixels);
		}

		internal static Bitmap ToBitmap(RawFrame source)
		{
			if (source == null) { throw new ArgumentNullException("source"); }
			Bitmap bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
			BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, source.Width, source.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
			try
			{
				byte[] row = new byte[locked.Stride];
				for (int y = 0; y < source.Height; y++)
				{
					for (int x = 0; x < source.Width; x += 2)
					{
						int offset = (y * source.Width + x) * 2;
						int blue = source.Pixels[offset + 1] - NeutralChrominance;
						int red = source.Pixels[offset + 3] - NeutralChrominance;
						for (int pixel = 0; pixel < 2; pixel++)
						{
							int luminance = source.Pixels[offset + pixel * 2];
							int output = (x + pixel) * 3;
							row[output] = (byte)Math.Max(0, Math.Min(MaximumComponent, Math.Round(luminance + BlueChrominance * blue)));
							row[output + 1] = (byte)Math.Max(0, Math.Min(MaximumComponent, Math.Round(luminance - GreenBlueChrominance * blue - GreenRedChrominance * red)));
							row[output + 2] = (byte)Math.Max(0, Math.Min(MaximumComponent, Math.Round(luminance + RedChrominance * red)));
						}
					}
					Marshal.Copy(row, 0, IntPtr.Add(locked.Scan0, y * locked.Stride), row.Length);
				}
			}
			finally { bitmap.UnlockBits(locked); }
			return bitmap;
		}
	}
}
