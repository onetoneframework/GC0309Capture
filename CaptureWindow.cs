using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GC0309Endoscope
{
	internal sealed class CaptureWindow : Form
	{
		private const int FramesPerSecond = EndoscopeDevice.CaptureFramesPerSecond;
		private const int RefreshMilliseconds = 67;
		private const int TransferCapacity = 65536;
		private const int TimeoutError = 121;
		private const int MaximumCatchupFrames = 60;
		private const int ShutdownSeconds = 3;
		private readonly PictureBox preview = new PictureBox();
		private readonly Label status = new Label();
		private readonly Button photo = new Button();
		private readonly Button record = new Button();
		private readonly ComboBox resolution = new ComboBox();
		private readonly Button applyResolution = new Button();
		private readonly ComboBox rotation = new ComboBox();
		private readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer();
		private readonly object synchronization = new object();
		private readonly string outputDirectory;
		private readonly Stopwatch recordingTime = new Stopwatch();
		private RawFrame latest;
		private RawFrame displayed;
		private RawFrame orientedFrame;
		private CancellationTokenSource cancellation;
		private Task capture;
		private AviRecording recording;
		private FileStream recordingFile;
		private int recordedFrames;
		private int acceptedFrames;
		private int droppedFrames;
		private string captureError = "";

		internal CaptureWindow(string outputDirectory)
		{
			this.outputDirectory = Path.GetFullPath(outputDirectory);
			Text = "GC0309 endoscope capture";
			ClientSize = new Size(760, 650);
			preview.Dock = DockStyle.Fill;
			preview.SizeMode = PictureBoxSizeMode.Zoom;
			preview.BackColor = Color.Black;
			FlowLayoutPanel controls = new FlowLayoutPanel();
			controls.Dock = DockStyle.Bottom;
			controls.Height = 100;
			photo.Text = "Save photo";
			record.Text = "Start recording";
			photo.AutoSize = true;
			record.AutoSize = true;
			status.AutoSize = true;
			resolution.DropDownStyle = ComboBoxStyle.DropDownList;
			resolution.Items.Add("640 x 480");
			resolution.Items.Add("450 x 450");
			resolution.SelectedIndex = 0;
			applyResolution.Text = "Apply resolution";
			applyResolution.AutoSize = true;
			rotation.DropDownStyle = ComboBoxStyle.DropDownList;
			rotation.Items.AddRange(new object[] { "Rotation: 0", "Rotation: 90 CW", "Rotation: 180", "Rotation: 270 CW" });
			rotation.Width = 140;
			rotation.SelectedIndex = 0;
			controls.Controls.Add(photo); controls.Controls.Add(record); controls.Controls.Add(resolution); controls.Controls.Add(applyResolution); controls.Controls.Add(rotation); controls.Controls.Add(status);
			Controls.Add(preview); Controls.Add(controls);
			photo.Click += delegate(object sender, EventArgs arguments)
			{
				try
				{
					if (preview.Image == null) { return; }
					string path = Path.Combine(this.outputDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".png");
					preview.Image.Save(path, ImageFormat.Png);
					status.Text = "Saved " + Path.GetFileName(path);
				}
				catch (Exception error) { Trace.TraceError(error.ToString()); MessageBox.Show(this, error.Message, "Photo save failed"); }
			};
			record.Click += delegate(object sender, EventArgs arguments)
			{
				try { ToggleRecording(); }
				catch (Exception error) { Trace.TraceError(error.ToString()); MessageBox.Show(this, error.Message, "Recording failed"); }
			};
			applyResolution.Click += delegate(object sender, EventArgs arguments)
			{
				try { StartCapture(); }
				catch (Exception error) { Trace.TraceError(error.ToString()); MessageBox.Show(this, error.Message, "Resolution change failed"); }
			};
			rotation.SelectedIndexChanged += delegate(object sender, EventArgs arguments)
			{
				try { displayed = null; RefreshPreview(); }
				catch (Exception error) { Trace.TraceError(error.ToString()); MessageBox.Show(this, error.Message, "Rotation failed"); }
			};
			refresh.Interval = RefreshMilliseconds;
			refresh.Tick += delegate(object sender, EventArgs arguments) { RefreshPreview(); };
			Shown += delegate(object sender, EventArgs arguments) { StartCapture(); };
			FormClosing += delegate(object sender, FormClosingEventArgs arguments)
			{
				refresh.Stop();
				if (cancellation != null) { cancellation.Cancel(); }
				if (capture != null && !capture.Wait(TimeSpan.FromSeconds(ShutdownSeconds))) { Trace.TraceWarning("Camera worker did not stop within the USB shutdown timeout."); }
				if (recording != null) { ToggleRecording(); }
				if (preview.Image != null) { preview.Image.Dispose(); }
				refresh.Dispose();
				if (cancellation != null) { cancellation.Dispose(); }
			};
		}

		private void StartCapture()
		{
			if (recording != null) { throw new InvalidOperationException("Stop recording before changing resolution."); }
			refresh.Stop();
			if (cancellation != null)
			{
				cancellation.Cancel();
				if (!capture.Wait(TimeSpan.FromSeconds(ShutdownSeconds))) { throw new IOException("The previous camera stream has not stopped yet."); }
				cancellation.Dispose();
			}
			if (preview.Image != null) { preview.Image.Dispose(); preview.Image = null; }
			displayed = null;
			orientedFrame = null;
			lock (synchronization) { latest = null; acceptedFrames = 0; droppedFrames = 0; captureError = ""; }
			Size requestedResolution = resolution.SelectedIndex == 0
				? new Size(EndoscopeDevice.MaximumResolutionWidth, EndoscopeDevice.MaximumResolutionHeight)
				: new Size(EndoscopeDevice.SquareResolutionSide, EndoscopeDevice.SquareResolutionSide);
			Directory.CreateDirectory(outputDirectory);
			cancellation = new CancellationTokenSource();
			capture = Task.Run(delegate
			{
				try
				{
					using (EndoscopeDevice device = new EndoscopeDevice())
					{
						device.Open();
						device.StartStreaming(requestedResolution, CameraSensorProfile.GC0309);
						try
						{
							byte[] transfer = new byte[TransferCapacity];
							RawFrameDecoder decoder = new RawFrameDecoder();
							// Cancellation and the finite USB timeout bound worker shutdown.
							while (!cancellation.IsCancellationRequested)
							{
								int count;
								try { count = device.Read(transfer); }
								catch (Win32Exception error)
									{
										if (error.NativeErrorCode != TimeoutError) { throw; }
										Trace.TraceWarning("Camera USB read timed out.");
										continue;
									}
								foreach (RawFrame frame in decoder.Feed(transfer, count))
								{
									lock (synchronization) { latest = frame; acceptedFrames++; droppedFrames = decoder.DroppedFrames; }
								}
							}
						}
						finally { device.Write(EndoscopeDevice.Command(EndoscopeDevice.StopStream, new byte[0])); }
					}
				}
				catch (Exception error)
				{
					Trace.TraceError(error.ToString());
					lock (synchronization) { captureError = error.Message; }
				}
			});
			refresh.Start();
		}

		private void RefreshPreview()
		{
			RawFrame frame;
			bool failed;
			lock (synchronization)
			{
				frame = latest;
				failed = captureError.Length > 0;
				status.Text = captureError.Length > 0 ? captureError : "Valid frames: " + acceptedFrames + "; discarded: " + droppedFrames;
			}
			photo.Enabled = frame != null;
			record.Enabled = frame != null && !failed;
			if (failed && recording != null) { ToggleRecording(); }
			if (frame == null) { return; }
			if (!Object.ReferenceEquals(displayed, frame))
			{
				orientedFrame = RawFrameDecoder.Rotate(frame, rotation.SelectedIndex);
				Image old = preview.Image;
				preview.Image = RawFrameDecoder.ToBitmap(orientedFrame);
				displayed = frame;
				if (old != null) { old.Dispose(); }
				Text = "GC0309 endoscope capture - " + orientedFrame.Width + " x " + orientedFrame.Height;
			}
			frame = orientedFrame;
			if (recording != null)
			{
				try
				{
					int required = (int)(recordingTime.Elapsed.TotalSeconds * FramesPerSecond);
					// Repeat the latest valid frame to preserve elapsed time when camera packets are lost.
					for (int index = 0; recordedFrames < required && index < MaximumCatchupFrames; index++)
					{
						recording.AddFrame(frame);
						recordedFrames++;
					}
				}
				catch (Exception error)
				{
					Trace.TraceError(error.ToString());
					ToggleRecording();
					MessageBox.Show(this, error.Message, "Recording stopped");
				}
			}
		}

		private void ToggleRecording()
		{
			if (recording != null)
			{
				try { recording.Dispose(); }
				finally { recordingFile.Dispose(); recording = null; recordingFile = null; recordingTime.Stop(); record.Text = "Start recording"; resolution.Enabled = true; applyResolution.Enabled = true; rotation.Enabled = true; }
				return;
			}
			RawFrame frame;
			lock (synchronization) { frame = latest; }
			if (frame == null) { throw new InvalidOperationException("Wait for a complete camera frame."); }
			frame = RawFrameDecoder.Rotate(frame, rotation.SelectedIndex);
			string path = Path.Combine(outputDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".avi");
			recordingFile = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
			try
			{
				recording = new AviRecording(recordingFile, frame);
				recording.Initialize(FramesPerSecond);
			}
			catch { recordingFile.Dispose(); recordingFile = null; recording = null; throw; }
			recordedFrames = 0;
			recordingTime.Restart();
			record.Text = "Stop recording";
			resolution.Enabled = false;
			applyResolution.Enabled = false;
			rotation.Enabled = false;
		}
	}
}
