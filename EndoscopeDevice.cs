using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Diagnostics;

namespace GC0309Endoscope
{
	internal enum CameraSensorProfile
	{
		Unspecified,
		BF2013,
		GC0309
	}

	// Installs the operating system's signed USB transport for this camera interface.
	internal static class EndoscopeDriver
	{
		internal const string HardwarePrefix = @"USB\VID_3456&PID_4321&MI_01\";
		internal const string InterfaceIdentifier = "{06362D83-AB94-4B7D-9339-914E632FCFD6}";
		private const uint SingleInformationFile = 0x00010000;
		private const uint IncludeExcludedDrivers = 0x00000800;
		private const uint ClassDriver = 1;
		private const uint GlobalScope = 1;
		private const uint DeviceRegistry = 1;
		private const uint RegistryWrite = 0x20006;
		private const uint MultipleString = 7;
		private const int NoMoreItems = 259;
		private const int DriverDetailsSize = 1584;
		private const int DriverSectionOffset = 32;
		private const int DriverDetailsCapacity = 4096;
		private static readonly IntPtr InvalidHandle = new IntPtr(-1);

		internal static void ValidateTarget(string instanceIdentifier)
		{
			if (String.IsNullOrWhiteSpace(instanceIdentifier) || !instanceIdentifier.StartsWith(HardwarePrefix, StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException("Only the GC0309 camera interface MI_01 can be configured.", "instanceIdentifier");
			}
		}

		internal static void Install(string instanceIdentifier)
		{
			ValidateTarget(instanceIdentifier);
			IntPtr devices = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
			if (devices == InvalidHandle) { throw new Win32Exception(); }
			try
			{
				DeviceInformation information = new DeviceInformation();
				information.Size = (uint)Marshal.SizeOf(information);
				if (!SetupDiOpenDeviceInfo(devices, instanceIdentifier, IntPtr.Zero, 0, ref information)) { throw new Win32Exception(); }
				InstallationParameters parameters = new InstallationParameters();
				parameters.Size = (uint)Marshal.SizeOf(parameters);
				if (!SetupDiGetDeviceInstallParams(devices, ref information, ref parameters)) { throw new Win32Exception(); }
				parameters.Flags |= SingleInformationFile;
				parameters.ExtendedFlags |= IncludeExcludedDrivers;
				parameters.DriverPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "winusb.inf");
				if (!SetupDiSetDeviceInstallParams(devices, ref information, ref parameters)) { throw new Win32Exception(); }
				if (!SetupDiBuildDriverInfoList(devices, ref information, ClassDriver)) { throw new Win32Exception(); }
				DriverInformation driver = new DriverInformation();
				bool selected = false;
				for (uint index = 0; index < 16; index++)
				{
					driver.Size = (uint)Marshal.SizeOf(driver);
					if (!SetupDiEnumDriverInfo(devices, ref information, ClassDriver, index, ref driver))
					{
						if (Marshal.GetLastWin32Error() == NoMoreItems) { break; }
						throw new Win32Exception();
					}
					Console.WriteLine("Available signed driver: " + driver.Description);
					IntPtr details = Marshal.AllocHGlobal(DriverDetailsCapacity);
					string section;
					try
					{
						Marshal.WriteInt32(details, DriverDetailsSize);
						uint required;
						if (!SetupDiGetDriverInfoDetail(devices, ref information, ref driver, details, DriverDetailsCapacity, out required)) { throw new Win32Exception(); }
						section = Marshal.PtrToStringUni(IntPtr.Add(details, DriverSectionOffset));
					}
					finally { Marshal.FreeHGlobal(details); }
					if (section.Equals("WINUSB", StringComparison.OrdinalIgnoreCase))
					{
						selected = true;
						break;
					}
				}
				if (!selected) { throw new InvalidOperationException("The generic Microsoft WinUSB driver was not found in winusb.inf."); }
				bool restartRequired;
				if (!DiInstallDevice(IntPtr.Zero, devices, ref information, ref driver, 0, out restartRequired)) { throw new Win32Exception(); }
				IntPtr registry = SetupDiOpenDevRegKey(devices, ref information, GlobalScope, 0, DeviceRegistry, RegistryWrite);
				if (registry == InvalidHandle) { throw new Win32Exception(); }
				try
				{
					byte[] value = Encoding.Unicode.GetBytes(InterfaceIdentifier + "\0\0");
					int result = RegSetValueEx(registry, "DeviceInterfaceGUIDs", 0, MultipleString, value, (uint)value.Length);
					if (result != 0) { throw new Win32Exception(result); }
				}
				finally { RegCloseKey(registry); }
				Console.WriteLine("WinUSB installed. Restart camera interface. Computer restart required: " + restartRequired);
			}
			finally { SetupDiDestroyDeviceInfoList(devices); }
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DeviceInformation
		{
			internal uint Size;
			internal Guid ClassIdentifier;
			internal uint DeviceInstance;
			internal UIntPtr Reserved;
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		internal struct InstallationParameters
		{
			internal uint Size, Flags, ExtendedFlags;
			internal IntPtr ParentWindow, InstallCallback, InstallContext, FileQueue;
			internal UIntPtr Reserved;
			internal uint ReservedValue;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string DriverPath;
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		internal struct DriverInformation
		{
			internal uint Size, DriverType;
			internal UIntPtr Reserved;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Description;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Manufacturer;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Provider;
			internal System.Runtime.InteropServices.ComTypes.FILETIME DriverDate;
			internal ulong DriverVersion;
		}

		// Native signatures retain the parameter counts and types required by Windows.
		[DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classIdentifier, IntPtr parent);
		[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiOpenDeviceInfo(IntPtr devices, string identifier, IntPtr parent, uint flags, ref DeviceInformation information);
		[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstallParams(IntPtr devices, ref DeviceInformation information, ref InstallationParameters parameters);
		[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiSetDeviceInstallParams(IntPtr devices, ref DeviceInformation information, ref InstallationParameters parameters);
		[DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiBuildDriverInfoList(IntPtr devices, ref DeviceInformation information, uint type);
		[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiEnumDriverInfo(IntPtr devices, ref DeviceInformation information, uint type, uint index, ref DriverInformation driver);
		[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDriverInfoDetail(IntPtr devices, ref DeviceInformation information, ref DriverInformation driver, IntPtr details, uint capacity, out uint required);
		[DllImport("newdev.dll", SetLastError = true)] private static extern bool DiInstallDevice(IntPtr parent, IntPtr devices, ref DeviceInformation information, ref DriverInformation driver, uint flags, out bool restartRequired);
		[DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiOpenDevRegKey(IntPtr devices, ref DeviceInformation information, uint scope, uint profile, uint keyType, uint access);
		[DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegSetValueEx(IntPtr key, string name, uint reserved, uint type, byte[] value, uint length);
		[DllImport("advapi32.dll")] private static extern int RegCloseKey(IntPtr key);
		[DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
	}

	internal sealed class EndoscopeDevice : IDisposable
	{
		internal const byte InputEndpoint = 0x82;
		internal const byte OutputEndpoint = 0x02;
		internal const byte BulkAlternateSetting = 1;
		internal const byte CommandPrefix = 0x55;
		internal const byte StopStream = 0x96;
		internal const byte StartStream = 0x97;
		internal const byte FrameRate = 0x99;
		internal const byte IsochronousMode = 0x9D;
		internal const byte CaptureFramesPerSecond = 15;
		internal const byte Resolution = 0x9B;
		internal const int MaximumResolutionWidth = 640;
		internal const int MaximumResolutionHeight = 480;
		internal const int MaximumDiagnosticWidth = 2560;
		internal const int MaximumDiagnosticHeight = 1440;
		internal const int SquareResolutionSide = 450;
		private const int ResolutionParameterLength = 12;
		private const int ResolutionWidthOffset = 8;
		private const int ResolutionHeightOffset = 10;
		private const int ByteShift = 8;
		private const uint PresentInterfaces = 0x12;
		private const uint ReadWriteAccess = 0xC0000000;
		private const uint ShareReadWrite = 3;
		private const uint ExistingFile = 3;
		private const uint OverlappedFile = 0x40000000;
		private const uint PipeTimeoutPolicy = 3;
		private const uint TransferTimeoutMilliseconds = 1000;
		private const int DetailPathOffset = 4;
		private const int DetailStructureSize = 8;
		private const int TransferSize = 65536;
		private const int ProbeSeconds = 5;
		private const int ProbeLogMilliseconds = 1000;
		private const int ReadQueueLength = 8;
		private const int InputPendingError = 997;
		private const int OperationCancelledError = 995;
		private const int TransferTimeoutError = 121;
		private SafeFileHandle file;
		private IntPtr usb;
		private UsbReadTransfer[] readQueue;
		private int readIndex;

		internal static byte[] Command(byte operation, byte[] parameters)
		{
			if (parameters == null) { throw new ArgumentNullException("parameters"); }
			byte[] result = new byte[parameters.Length + 2];
			result[0] = CommandPrefix;
			result[1] = operation;
			Buffer.BlockCopy(parameters, 0, result, 2, parameters.Length);
			return result;
		}

		internal void Open()
		{
			if (usb != IntPtr.Zero) { throw new InvalidOperationException("The camera is already open."); }
			Guid identifier = new Guid(EndoscopeDriver.InterfaceIdentifier);
			IntPtr devices = SetupDiGetClassDevs(ref identifier, null, IntPtr.Zero, PresentInterfaces);
			if (devices == new IntPtr(-1)) { throw new Win32Exception(); }
			try
			{
				DeviceInterface information = new DeviceInterface();
				information.Size = (uint)Marshal.SizeOf(information);
				if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref identifier, 0, ref information)) { throw new Win32Exception(Marshal.GetLastWin32Error(), "No configured GC0309 camera interface is available."); }
				uint size;
				SetupDiGetDeviceInterfaceDetail(devices, ref information, IntPtr.Zero, 0, out size, IntPtr.Zero);
				IntPtr details = Marshal.AllocHGlobal((int)size);
				try
				{
					Marshal.WriteInt32(details, DetailStructureSize);
					if (!SetupDiGetDeviceInterfaceDetail(devices, ref information, details, size, out size, IntPtr.Zero)) { throw new Win32Exception(); }
					string path = Marshal.PtrToStringUni(IntPtr.Add(details, DetailPathOffset));
					if (path.IndexOf("vid_3456&pid_4321&mi_01", StringComparison.OrdinalIgnoreCase) < 0) { throw new InvalidOperationException("Unexpected USB interface path."); }
					file = CreateFile(path, ReadWriteAccess, ShareReadWrite, IntPtr.Zero, ExistingFile, OverlappedFile, IntPtr.Zero);
					if (file.IsInvalid) { throw new Win32Exception(); }
					if (!WinUsb_Initialize(file, out usb)) { throw new Win32Exception(); }
					if (!WinUsb_SetCurrentAlternateSetting(usb, BulkAlternateSetting)) { throw new Win32Exception(); }
					uint timeout = TransferTimeoutMilliseconds;
					if (!WinUsb_SetPipePolicy(usb, InputEndpoint, PipeTimeoutPolicy, sizeof(uint), ref timeout)) { throw new Win32Exception(); }
				}
				finally { Marshal.FreeHGlobal(details); }
			}
			catch { Dispose(); throw; }
			finally { SetupDiDestroyDeviceInfoList(devices); }
		}

		internal void Write(byte[] command)
		{
			if (usb == IntPtr.Zero) { throw new InvalidOperationException("Open the camera before sending commands."); }
			if (command == null || command.Length < 2 || command[0] != CommandPrefix) { throw new ArgumentException("Invalid camera command.", "command"); }
			uint written;
			if (!WinUsb_WritePipe(usb, OutputEndpoint, command, (uint)command.Length, out written, IntPtr.Zero)) { throw new Win32Exception(); }
			if (written != command.Length) { throw new IOException("The USB command was only partially written."); }
		}

		internal int Read(byte[] buffer)
		{
			if (usb == IntPtr.Zero) { throw new InvalidOperationException("Open the camera before reading."); }
			if (buffer == null || buffer.Length < TransferSize) { throw new ArgumentException("A full-size USB transfer buffer is required.", "buffer"); }
			if (readQueue == null)
			{
				readQueue = new UsbReadTransfer[ReadQueueLength];
				for (int index = 0; index < readQueue.Length; index++)
				{
					UsbReadTransfer transfer = new UsbReadTransfer();
					readQueue[index] = transfer;
					transfer.Buffer = new byte[TransferSize];
					transfer.PinnedBuffer = GCHandle.Alloc(transfer.Buffer, GCHandleType.Pinned);
					transfer.Completion = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset);
					transfer.Overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UsbOverlapped)));
					QueueRead(transfer);
				}
			}
			UsbReadTransfer current = readQueue[readIndex];
			uint received;
			bool completed = WinUsb_GetOverlappedResult(usb, current.Overlapped, out received, true);
			int error = completed ? 0 : Marshal.GetLastWin32Error();
			if (completed) { Buffer.BlockCopy(current.Buffer, 0, buffer, 0, (int)received); }
			readIndex = (readIndex + 1) % readQueue.Length;
			QueueRead(current);
			if (!completed) { throw new Win32Exception(error); }
			return (int)received;
		}

		private void QueueRead(UsbReadTransfer transfer)
		{
			transfer.Completion.Reset();
			UsbOverlapped overlapped = new UsbOverlapped();
			overlapped.Event = transfer.Completion.SafeWaitHandle.DangerousGetHandle();
			Marshal.StructureToPtr(overlapped, transfer.Overlapped, false);
			uint received;
			if (!WinUsb_ReadPipe(usb, InputEndpoint, transfer.PinnedBuffer.AddrOfPinnedObject(), TransferSize, out received, transfer.Overlapped))
			{
				int error = Marshal.GetLastWin32Error();
				if (error != InputPendingError) { throw new Win32Exception(error); }
			}
			transfer.Submitted = true;
		}

		internal static byte[] ResolutionCommand(System.Drawing.Size resolution, CameraSensorProfile profile)
		{
			if (resolution.Width <= 0 || resolution.Width > MaximumDiagnosticWidth || resolution.Width % 2 != 0 || resolution.Height <= 0 || resolution.Height > MaximumDiagnosticHeight)
			{
				throw new ArgumentException("Resolution must fit the diagnostic buffer range and have an even width.", "resolution");
			}
			byte[] parameters = new byte[ResolutionParameterLength];
			// These window values come from LinkBack's sensor-specific default presets.
			switch (profile)
			{
				case CameraSensorProfile.BF2013:
					if (resolution.Width != SquareResolutionSide || resolution.Height != SquareResolutionSide) { throw new ArgumentException("The recovered BF2013 preset supports 450x450 only."); }
					Buffer.BlockCopy(new byte[] { 0x27, 0x18, 0x89, 0x04, 0x75 }, 0, parameters, 0, 5);
					break;
				case CameraSensorProfile.GC0309:
					Buffer.BlockCopy(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x01, 0xE8, 0x02, 0x88 }, 0, parameters, 0, ResolutionWidthOffset);
					break;
				default: throw new ArgumentException("An explicit sensor profile is required; zero-register requests can stop image delivery.");
			}
			parameters[ResolutionWidthOffset] = (byte)(resolution.Width >> ByteShift);
			parameters[ResolutionWidthOffset + 1] = (byte)resolution.Width;
			parameters[ResolutionHeightOffset] = (byte)(resolution.Height >> ByteShift);
			parameters[ResolutionHeightOffset + 1] = (byte)resolution.Height;
			return Command(Resolution, parameters);
		}

		internal void Probe(string outputPath, System.Drawing.Size requestedResolution = default(System.Drawing.Size), CameraSensorProfile profile = CameraSensorProfile.Unspecified)
		{
			Open();
			StartStreaming(requestedResolution, profile);
			FileStream videoFile = null;
			AviRecording video = null;
			try
			{
				Stopwatch elapsed = Stopwatch.StartNew();
				long loggedMilliseconds = 0;
				byte[] buffer = new byte[TransferSize];
				RawFrameDecoder decoder = new RawFrameDecoder(requestedResolution);
				RawFrame latest = null;
				Stopwatch videoTime = new Stopwatch();
				int frames = 0;
				int acceptedFrames = 0;
				using (FileStream output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write))
				{
					// The diagnostic capture has a fixed duration and bounded USB read timeout.
					while (elapsed.Elapsed.TotalSeconds < ProbeSeconds)
					{
						int count;
						try { count = Read(buffer); }
						catch (Win32Exception error)
						{
							if (error.NativeErrorCode != TransferTimeoutError) { throw; }
							Console.Error.WriteLine("No camera data received during the USB timeout interval.");
							continue;
						}
						output.Write(buffer, 0, count);
						foreach (RawFrame frame in decoder.Feed(buffer, count))
						{
							acceptedFrames++;
							latest = frame;
							if (video == null)
							{
								using (System.Drawing.Bitmap photo = RawFrameDecoder.ToBitmap(frame)) { photo.Save(Path.ChangeExtension(outputPath, ".png"), System.Drawing.Imaging.ImageFormat.Png); }
								videoFile = new FileStream(Path.ChangeExtension(outputPath, ".avi"), FileMode.CreateNew, FileAccess.Write);
								video = new AviRecording(videoFile, frame);
								video.Initialize(CaptureFramesPerSecond);
								videoTime.Start();
								Console.WriteLine("Verified frame dimensions: " + frame.Width + " x " + frame.Height);
							}
						}
						if (video != null)
						{
							int required = (int)(videoTime.Elapsed.TotalSeconds * CaptureFramesPerSecond);
							while (frames < required)
							{
								video.AddFrame(latest);
								frames++;
							}
						}
						if (elapsed.ElapsedMilliseconds - loggedMilliseconds >= ProbeLogMilliseconds)
						{
							Console.WriteLine("Captured bytes: " + output.Length);
							loggedMilliseconds = elapsed.ElapsedMilliseconds;
						}
					}
					Console.WriteLine("Captured " + output.Length + " bytes.");
					Console.WriteLine("Recorded video frames: " + frames + "; discarded camera frames: " + decoder.DroppedFrames);
					Console.WriteLine("Complete camera frames: " + acceptedFrames);
					if (video == null) { throw new IOException("The camera did not produce a complete recognized frame."); }
				}
			}
			finally
			{
				try { Write(Command(StopStream, new byte[0])); }
				finally
				{
					try { if (video != null) { video.Dispose(); } }
					finally { if (videoFile != null) { videoFile.Dispose(); } }
				}
			}
		}

		internal void StartStreaming(System.Drawing.Size requestedResolution = default(System.Drawing.Size), CameraSensorProfile profile = CameraSensorProfile.Unspecified)
		{
			byte[] resolutionCommand = requestedResolution.IsEmpty ? new byte[0] : ResolutionCommand(requestedResolution, profile);
			Write(Command(StopStream, new byte[0]));
			Write(Command(FrameRate, new byte[] { CaptureFramesPerSecond }));
			Write(Command(IsochronousMode, new byte[] { 0 }));
			if (resolutionCommand.Length > 0) { Write(resolutionCommand); }
			Write(Command(StartStream, new byte[0]));
		}

		public void Dispose()
		{
			if (readQueue != null)
			{
				if (!WinUsb_AbortPipe(usb, InputEndpoint)) { Trace.TraceWarning("USB read cancellation failed: " + Marshal.GetLastWin32Error()); }
				foreach (UsbReadTransfer transfer in readQueue)
				{
					if (transfer == null) { continue; }
					if (transfer.Submitted)
					{
						uint received;
						if (!WinUsb_GetOverlappedResult(usb, transfer.Overlapped, out received, true))
						{
							int error = Marshal.GetLastWin32Error();
							if (error != OperationCancelledError) { Trace.TraceWarning("USB read ended with error: " + error); }
						}
					}
					if (transfer.Overlapped != IntPtr.Zero) { Marshal.FreeHGlobal(transfer.Overlapped); }
					if (transfer.Completion != null) { transfer.Completion.Dispose(); }
					if (transfer.PinnedBuffer.IsAllocated) { transfer.PinnedBuffer.Free(); }
				}
				readQueue = null;
				readIndex = 0;
			}
			if (usb != IntPtr.Zero) { WinUsb_Free(usb); usb = IntPtr.Zero; }
			if (file != null) { file.Dispose(); file = null; }
		}

		private sealed class UsbReadTransfer
		{
			internal byte[] Buffer;
			internal GCHandle PinnedBuffer;
			internal System.Threading.EventWaitHandle Completion;
			internal IntPtr Overlapped;
			internal bool Submitted;
		}

		[StructLayout(LayoutKind.Sequential)] internal struct UsbOverlapped
		{
			internal UIntPtr Internal, InternalHigh;
			internal uint Offset, OffsetHigh;
			internal IntPtr Event;
		}

		[StructLayout(LayoutKind.Sequential)] private struct DeviceInterface
		{
			internal uint Size;
			internal Guid Identifier;
			internal uint Flags;
			internal UIntPtr Reserved;
		}

		// Native signatures retain the Windows API contracts.
		[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid identifier, string enumerator, IntPtr parent, uint flags);
		[DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr device, ref Guid identifier, uint index, ref DeviceInterface information);
		[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devices, ref DeviceInterface information, IntPtr details, uint capacity, out uint required, IntPtr device);
		[DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
		[DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_Initialize(SafeFileHandle file, out IntPtr usb);
		[DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_SetCurrentAlternateSetting(IntPtr usb, byte alternateSetting);
		[DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_SetPipePolicy(IntPtr usb, byte endpoint, uint policy, uint size, ref uint value);
		[DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_ReadPipe(IntPtr usb, byte endpoint, IntPtr buffer, uint capacity, out uint received, IntPtr overlapped);
		[DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_GetOverlappedResult(IntPtr usb, IntPtr overlapped, out uint received, bool wait);
		[DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_AbortPipe(IntPtr usb, byte endpoint);
		[DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_WritePipe(IntPtr usb, byte endpoint, byte[] buffer, uint length, out uint written, IntPtr overlapped);
		[DllImport("winusb.dll")] private static extern bool WinUsb_Free(IntPtr usb);
	}

	internal static class Program
	{
		[STAThread]
		private static int Main(string[] arguments)
		{
			try
			{
				if (arguments.Length == 2 && arguments[0] == "install-driver")
				{
					EndoscopeDriver.Install(arguments[1]);
					return 0;
				}
				if (arguments.Length == 2 && arguments[0] == "probe")
				{
					using (EndoscopeDevice device = new EndoscopeDevice()) { device.Probe(arguments[1]); }
					return 0;
				}
				if (arguments.Length == 4 && arguments[0] == "probe-resolution")
				{
					CameraSensorProfile profile;
					if (!Enum.TryParse(arguments[1], true, out profile)) { throw new ArgumentException("Specify BF2013 or GC0309."); }
					string[] dimensions = arguments[2].Split('x');
					int width, height;
					if (dimensions.Length != 2 || !Int32.TryParse(dimensions[0], out width) || !Int32.TryParse(dimensions[1], out height)) { throw new ArgumentException("Use a resolution such as 640x480."); }
					System.Drawing.Size requested = new System.Drawing.Size(width, height);
					EndoscopeDevice.ResolutionCommand(requested, profile);
					Console.WriteLine("Requested resolution: " + width + " x " + height);
					using (EndoscopeDevice device = new EndoscopeDevice()) { device.Probe(arguments[3], requested, profile); }
					return 0;
				}
				if (arguments.Length == 2 && arguments[0] == "preview")
				{
					System.Windows.Forms.Application.EnableVisualStyles();
					System.Windows.Forms.Application.Run(new CaptureWindow(arguments[1]));
					return 0;
				}
				Console.Error.WriteLine("Usage: GC0309Endoscope install-driver <camera MI_01 instance identifier> | probe <new file> | probe-resolution <sensor profile> <width>x<height> <new file> | preview <capture directory>");
				return 2;
			}
			catch (Exception error)
			{
				Console.Error.WriteLine(error.ToString());
				return 1;
			}
		}
	}
}
