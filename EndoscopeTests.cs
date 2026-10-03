using System;
using System.Runtime.InteropServices;
using System.IO;
using System.Drawing;
using System.Collections.Generic;
using System.Text;

namespace GC0309Endoscope
{
	internal static class EndoscopeTests
	{
		private static int Main()
		{
			ValidateDriverTarget();
			ValidateNativeLayouts();
			ValidateCommandEncoding();
			ValidateResolutionEncoding();
			ValidateDecoderFragments();
			ValidateHigherResolutionFrames();
			ValidateRequestedFrameDimensions();
			ValidateDecoderRecovery();
			ValidatePixelConversion();
			ValidateFrameRotation();
			ValidateRecording();
			Console.WriteLine("All 11 endoscope test groups passed.");
			return 0;
		}

		private static void ValidateDriverTarget()
		{
			EndoscopeDriver.ValidateTarget(EndoscopeDriver.HardwarePrefix + "TEST");
			foreach (string invalid in new string[] { "", "USB\\VID_3456&PID_4321\\TEST", "USB\\VID_3456&PID_4321&MI_00\\TEST", "USB\\VID_1234&PID_4321&MI_01\\TEST" })
			{
				bool rejected = false;
				try { EndoscopeDriver.ValidateTarget(invalid); }
				catch (ArgumentException) { rejected = true; }
				if (!rejected) { throw new Exception("An unrelated device identifier was accepted."); }
			}
		}

		private static void ValidateNativeLayouts()
		{
			if (Marshal.SizeOf(typeof(EndoscopeDevice.UsbOverlapped)) != 32 || Marshal.OffsetOf(typeof(EndoscopeDevice.UsbOverlapped), "Event").ToInt32() != 24) { throw new Exception("Incorrect x64 overlapped USB layout."); }
			if (Marshal.SizeOf(typeof(EndoscopeDriver.DeviceInformation)) != 32) { throw new Exception("Incorrect x64 device-information layout."); }
			if (Marshal.SizeOf(typeof(EndoscopeDriver.InstallationParameters)) != 584) { throw new Exception("Incorrect x64 installation-parameter layout."); }
			if (Marshal.SizeOf(typeof(EndoscopeDriver.DriverInformation)) != 1568) { throw new Exception("Incorrect x64 driver-information layout."); }
		}

		private static void ValidateCommandEncoding()
		{
			if (BitConverter.ToString(EndoscopeDevice.Command(EndoscopeDevice.StartStream, new byte[0])) != "55-97") { throw new Exception("Incorrect stream command."); }
			if (BitConverter.ToString(EndoscopeDevice.Command(EndoscopeDevice.FrameRate, new byte[] { 15 })) != "55-99-0F") { throw new Exception("Incorrect frame-rate command."); }
			bool rejected = false;
			try { EndoscopeDevice.Command(EndoscopeDevice.StartStream, null); }
			catch (ArgumentNullException) { rejected = true; }
			if (!rejected) { throw new Exception("Null command parameters were accepted."); }
			using (EndoscopeDevice device = new EndoscopeDevice())
			{
				rejected = false;
				try { device.Read(new byte[1]); }
				catch (InvalidOperationException) { rejected = true; }
				if (!rejected) { throw new Exception("Reading an unopened device was accepted."); }
				rejected = false;
				try { device.StartStreaming(); }
				catch (InvalidOperationException) { rejected = true; }
				if (!rejected) { throw new Exception("Starting an unopened device was accepted."); }
			}
		}

		private static byte[] PacketFixture(int payloadLength, byte identifier)
		{
			byte[] packet = new byte[payloadLength + 12];
			packet[0] = 12;
			packet[1] = (byte)(0x8C | identifier);
			packet[6] = (byte)(payloadLength >> 8);
			packet[7] = (byte)payloadLength;
			for (int index = 12; index < packet.Length; index += 4)
			{
				packet[index] = 64; packet[index + 1] = 128; packet[index + 2] = 192; packet[index + 3] = 128;
			}
			return packet;
		}

		private static void ValidateResolutionEncoding()
		{
			byte[] command = EndoscopeDevice.ResolutionCommand(new Size(640, 480), CameraSensorProfile.GC0309);
			if (BitConverter.ToString(command) != "55-9B-00-00-00-00-01-E8-02-88-02-80-01-E0") { throw new Exception("Incorrect resolution command or byte order."); }
			command = EndoscopeDevice.ResolutionCommand(new Size(450, 450), CameraSensorProfile.BF2013);
			if (BitConverter.ToString(command) != "55-9B-27-18-89-04-75-00-00-00-01-C2-01-C2") { throw new Exception("Incorrect BF2013 sensor profile."); }
			foreach (CameraSensorProfile invalid in new CameraSensorProfile[] { CameraSensorProfile.Unspecified, CameraSensorProfile.BF2013, (CameraSensorProfile)99 })
			{
				bool rejected = false;
				try { EndoscopeDevice.ResolutionCommand(new Size(640, 480), invalid); }
				catch (ArgumentException) { rejected = true; }
				if (!rejected) { throw new Exception("An unidentified or incompatible sensor profile was accepted."); }
			}
			command = EndoscopeDevice.ResolutionCommand(new Size(2560, 1440), CameraSensorProfile.GC0309);
			if (BitConverter.ToString(command) != "55-9B-00-00-00-00-01-E8-02-88-0A-00-05-A0") { throw new Exception("Incorrect QHD diagnostic command."); }
			command = EndoscopeDevice.ResolutionCommand(new Size(1920, 1440), CameraSensorProfile.GC0309);
			if (BitConverter.ToString(command) != "55-9B-00-00-00-00-01-E8-02-88-07-80-05-A0") { throw new Exception("Incorrect advertised-resolution diagnostic command."); }
			foreach (Size invalid in new Size[] { Size.Empty, new Size(451, 450), new Size(3840, 2160), new Size(640, -1) })
			{
				bool rejected = false;
					try { EndoscopeDevice.ResolutionCommand(invalid, CameraSensorProfile.GC0309); }
				catch (ArgumentException) { rejected = true; }
				if (!rejected) { throw new Exception("An invalid resolution was accepted."); }
			}
		}

		private static void ValidateDecoderFragments()
		{
			RawFrameDecoder decoder = new RawFrameDecoder();
			byte[] boundary = PacketFixture(0, 0);
			decoder.Feed(boundary, boundary.Length);
			byte[] packet = PacketFixture(500, 1);
			byte[] beginning = new byte[7];
			byte[] remainder = new byte[packet.Length - beginning.Length];
			Buffer.BlockCopy(packet, 0, beginning, 0, beginning.Length);
			Buffer.BlockCopy(packet, beginning.Length, remainder, 0, remainder.Length);
			if (decoder.Feed(beginning, beginning.Length).Count != 0) { throw new Exception("A partial header produced a frame."); }
			decoder.Feed(remainder, remainder.Length);
			for (int index = 1; index < 810; index++) { decoder.Feed(packet, packet.Length); }
			List<RawFrame> frames = decoder.Feed(boundary, boundary.Length);
			if (frames.Count != 1 || frames[0].Width != 450 || frames[0].Height != 450 || frames[0].Pixels[2] != 192) { throw new Exception("Fragmented frame reconstruction failed."); }
			bool rejected = false;
			try { decoder.Feed(packet, packet.Length + 1); }
			catch (ArgumentException) { rejected = true; }
			if (!rejected) { throw new Exception("Invalid transfer length was accepted."); }
		}

		private static void ValidateDecoderRecovery()
		{
			RawFrameDecoder decoder = new RawFrameDecoder();
			byte[] packet = PacketFixture(500, 0);
			decoder.Feed(packet, packet.Length);
			byte[] malformed = new byte[32];
			decoder.Feed(malformed, malformed.Length);
			packet = PacketFixture(500, 1);
			decoder.Feed(packet, packet.Length);
			packet = PacketFixture(500, 0);
			for (int index = 0; index < 810; index++) { decoder.Feed(packet, packet.Length); }
			byte[] boundary = PacketFixture(0, 1);
			List<RawFrame> recovered = decoder.Feed(boundary, boundary.Length);
			if (recovered.Count != 1 || decoder.InvalidBytes < malformed.Length || decoder.DroppedFrames < 2) { throw new Exception("Corruption recovery failed."); }
			packet = PacketFixture(500, 1);
			for (int index = 0; index < 15000; index++) { decoder.Feed(packet, packet.Length); }
			boundary = PacketFixture(0, 0);
			if (decoder.Feed(boundary, boundary.Length).Count != 0) { throw new Exception("An oversized frame was accepted."); }
		}

		private static void ValidateRequestedFrameDimensions()
		{
			RawFrameDecoder decoder = new RawFrameDecoder(new Size(800, 600));
			byte[] boundary = PacketFixture(0, 0);
			decoder.Feed(boundary, boundary.Length);
			byte[] packet = PacketFixture(3000, 1);
			for (int index = 0; index < 320; index++) { decoder.Feed(packet, packet.Length); }
			List<RawFrame> frames = decoder.Feed(boundary, boundary.Length);
			if (frames.Count != 1 || frames[0].Width != 800 || frames[0].Height != 600) { throw new Exception("Requested diagnostic dimensions were not reconstructed."); }
			foreach (Size invalid in new Size[] { new Size(801, 600), new Size(-2, 480), new Size(3840, 2160) })
			{
				bool rejected = false;
				try { new RawFrameDecoder(invalid); }
				catch (ArgumentException) { rejected = true; }
				if (!rejected) { throw new Exception("Invalid diagnostic decoder dimensions were accepted."); }
			}
		}

		private static void ValidateHigherResolutionFrames()
		{
			RawFrameDecoder decoder = new RawFrameDecoder();
			byte[] boundary = PacketFixture(0, 0);
			decoder.Feed(boundary, boundary.Length);
			byte[] packet = PacketFixture(3072, 1);
			for (int index = 0; index < 200; index++) { decoder.Feed(packet, packet.Length); }
			List<RawFrame> frames = decoder.Feed(boundary, boundary.Length);
			if (frames.Count != 1 || frames[0].Width != 640 || frames[0].Height != 480 || frames[0].Pixels.Length != 614400) { throw new Exception("Higher-resolution packet reconstruction failed."); }
			packet = PacketFixture(3072, 0);
			for (int index = 0; index < 199; index++) { decoder.Feed(packet, packet.Length); }
			boundary = PacketFixture(0, 1);
			if (decoder.Feed(boundary, boundary.Length).Count != 0) { throw new Exception("An incomplete higher-resolution frame was accepted."); }
			foreach (Size size in new Size[] { new Size(1280, 720), new Size(1920, 1080), new Size(1920, 1440), new Size(2560, 1440) })
			{
				decoder = new RawFrameDecoder();
				boundary = PacketFixture(0, 0);
				decoder.Feed(boundary, boundary.Length);
				packet = PacketFixture(3072, 1);
				int packets = size.Width * size.Height * 2 / 3072;
				for (int index = 0; index < packets; index++) { decoder.Feed(packet, packet.Length); }
				frames = decoder.Feed(boundary, boundary.Length);
				if (frames.Count != 1 || frames[0].Width != size.Width || frames[0].Height != size.Height) { throw new Exception("HD diagnostic frame reconstruction failed."); }
			}
		}

		private static void ValidatePixelConversion()
		{
			RawFrame frame = new RawFrame(2, 1, new byte[] { 0, 128, 255, 128 });
			using (Bitmap image = RawFrameDecoder.ToBitmap(frame))
			{
				if (image.GetPixel(0, 0).ToArgb() != Color.Black.ToArgb() || image.GetPixel(1, 0).ToArgb() != Color.White.ToArgb()) { throw new Exception("Neutral YUY2 pixel conversion failed."); }
			}
			frame = new RawFrame(2, 1, new byte[] { 128, 0, 128, 255 });
			using (Bitmap image = RawFrameDecoder.ToBitmap(frame))
			{
				Color color = image.GetPixel(0, 0);
				if (color.R != 255 || color.B != 0) { throw new Exception("Chrominance channel ordering or clamping failed."); }
			}
			bool rejected = false;
			try { new RawFrame(2, 2, new byte[4]); }
			catch (ArgumentException) { rejected = true; }
			if (!rejected) { throw new Exception("An incomplete pixel frame was accepted."); }
		}

		private static void ValidateFrameRotation()
		{
			RawFrame source = new RawFrame(2, 2, new byte[] { 1, 10, 2, 20, 3, 30, 4, 40 });
			if (!Object.ReferenceEquals(source, RawFrameDecoder.Rotate(source, 0))) { throw new Exception("Zero rotation changed the source frame."); }
			byte[][] expected = new byte[][] {
				new byte[] { 3, 20, 1, 30, 4, 20, 2, 30 },
				new byte[] { 4, 30, 3, 40, 2, 10, 1, 20 },
				new byte[] { 2, 20, 4, 30, 1, 20, 3, 30 }
			};
			for (int turns = 1; turns <= expected.Length; turns++)
			{
				RawFrame rotated = RawFrameDecoder.Rotate(source, turns);
				if (BitConverter.ToString(rotated.Pixels) != BitConverter.ToString(expected[turns - 1])) { throw new Exception("Incorrect rotated luminance or chroma samples."); }
			}
			if (BitConverter.ToString(source.Pixels) != "01-0A-02-14-03-1E-04-28") { throw new Exception("Rotation mutated the original pixels."); }
			RawFrame rectangle = new RawFrame(4, 2, new byte[] { 1, 128, 2, 128, 3, 128, 4, 128, 5, 128, 6, 128, 7, 128, 8, 128 });
			RawFrame portrait = RawFrameDecoder.Rotate(rectangle, 1);
			if (portrait.Width != 2 || portrait.Height != 4 || BitConverter.ToString(portrait.Pixels) != "05-80-01-80-06-80-02-80-07-80-03-80-08-80-04-80") { throw new Exception("Rectangular rotation did not swap dimensions correctly."); }
			using (MemoryStream destination = new MemoryStream())
			{
				using (AviRecording recording = new AviRecording(destination, portrait)) { recording.Initialize(15); recording.AddFrame(portrait); }
				byte[] video = destination.ToArray();
				if (BitConverter.ToInt32(video, 64) != 2 || BitConverter.ToInt32(video, 68) != 4) { throw new Exception("Rotated AVI dimensions are incorrect."); }
				for (int index = 0; index < portrait.Pixels.Length; index++)
				{
					if (video[232 + index] != portrait.Pixels[index]) { throw new Exception("Rotated AVI payload is incorrect."); }
				}
			}
			foreach (int invalid in new int[] { -1, 4 })
			{
				bool rejected = false;
				try { RawFrameDecoder.Rotate(source, invalid); }
				catch (ArgumentOutOfRangeException) { rejected = true; }
				if (!rejected) { throw new Exception("An invalid rotation was accepted."); }
			}
			bool nullRejected = false;
			try { RawFrameDecoder.Rotate(null, 1); }
			catch (ArgumentNullException) { nullRejected = true; }
			if (!nullRejected) { throw new Exception("A null rotation source was accepted."); }
			RawFrame oddHeight = new RawFrame(2, 1, new byte[] { 1, 128, 2, 128 });
			if (RawFrameDecoder.Rotate(oddHeight, 2).Pixels[0] != 2) { throw new Exception("Half-turn rotation rejected an odd height."); }
			bool oddHeightRejected = false;
			try { RawFrameDecoder.Rotate(oddHeight, 1); }
			catch (ArgumentException) { oddHeightRejected = true; }
			if (!oddHeightRejected) { throw new Exception("A quarter turn accepted an odd YUY2 output width."); }
		}

		private static void ValidateRecording()
		{
			RawFrame frame = new RawFrame(2, 1, new byte[] { 0, 128, 255, 128 });
			using (MemoryStream nonempty = new MemoryStream(new byte[1]))
			{
				bool rejected = false;
				try { new AviRecording(nonempty, frame); }
				catch (ArgumentException) { rejected = true; }
				if (!rejected) { throw new Exception("A nonempty recording destination was accepted."); }
			}
			using (MemoryStream destination = new MemoryStream())
			{
				using (AviRecording recording = new AviRecording(destination, frame))
				{
					bool rejected = false;
					try { recording.AddFrame(frame); }
					catch (InvalidOperationException) { rejected = true; }
					if (!rejected) { throw new Exception("An uninitialized AVI accepted a frame."); }
					recording.Initialize(15);
					recording.AddFrame(frame); recording.AddFrame(frame);
					rejected = false;
					try { recording.AddFrame(new RawFrame(4, 1, new byte[8])); }
					catch (ArgumentException) { rejected = true; }
					if (!rejected) { throw new Exception("An AVI accepted changed dimensions."); }
				}
				byte[] result = destination.ToArray();
				if (Encoding.ASCII.GetString(result, 0, 4) != "RIFF" || BitConverter.ToUInt32(result, 4) != result.Length - 8) { throw new Exception("Invalid AVI RIFF length."); }
				if (BitConverter.ToInt32(result, 48) != 2 || BitConverter.ToInt32(result, 140) != 2) { throw new Exception("Invalid AVI frame counts."); }
				if (Encoding.ASCII.GetString(result, 248, 4) != "idx1" || BitConverter.ToUInt32(result, 264) != 4 || BitConverter.ToUInt32(result, 280) != 16) { throw new Exception("Invalid AVI frame index."); }
			}
		}
	}
}
