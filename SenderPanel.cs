using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IPCNamedPipes;
using Wtvision.DataShare;

namespace FrameServerViewer
{
    public class SenderPanel : UserControl, IIPCEventsCallback
    {
        private readonly ComboBox _channelBox;
        private readonly ComboBox _resBox;
        private readonly ComboBox _fpsBox;
        private readonly Button _openVideoBtn;
        private readonly Button _clearVideoBtn;
        private readonly Button _startBtn;
        private readonly Label _statusLabel;
        private readonly Label _statsLabel;
        private readonly PictureBox _previewBox;

        private CancellationTokenSource _cts;
        private Process _ffmpegProcess;
        private readonly object _ffmpegLock = new object();
        private volatile string _videoFilePath;
        private volatile bool _sourceChanged;
        private Bitmap _currentBitmap;
        private readonly object _bitmapLock = new object();

        public SenderPanel()
        {
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(24, 24, 28);
            ForeColor = Color.WhiteSmoke;

            var header = new Label
            {
                Text = "FRAME SERVER (SENDER)",
                Dock = DockStyle.Top,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                BackColor = Color.FromArgb(42, 42, 50),
                ForeColor = Color.LightGreen
            };

            var topPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(6, 6, 6, 4),
                BackColor = Color.FromArgb(34, 34, 40),
                WrapContents = false
            };

            _channelBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                Width = 175,
                Margin = new Padding(2, 2, 6, 0)
            };
            _channelBox.Items.AddRange(new object[]
            {
                "FrameServer R3 output (1)",
                "FrameServer R3 output (2)",
                "FrameServer R3 output (3)",
                "FrameServer R3 output (4)"
            });
            _channelBox.SelectedIndex = 1;

            _resBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 86,
                Margin = new Padding(0, 2, 6, 0)
            };
            _resBox.Items.AddRange(new object[] { "1280x720", "1920x1080", "800x600" });
            _resBox.SelectedIndex = 0;

            _fpsBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 48,
                Margin = new Padding(0, 2, 6, 0)
            };
            _fpsBox.Items.AddRange(new object[] { "25", "30", "50", "60" });
            _fpsBox.SelectedIndex = 1;

            _openVideoBtn = new Button
            {
                Text = "Open Video...",
                Width = 92,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(65, 65, 78),
                ForeColor = Color.White,
                Margin = new Padding(0, 1, 4, 0)
            };
            _openVideoBtn.Click += OnOpenVideoClick;

            _clearVideoBtn = new Button
            {
                Text = "Test",
                Width = 48,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 55, 65),
                ForeColor = Color.LightGray,
                Enabled = false,
                Margin = new Padding(0, 1, 6, 0)
            };
            _clearVideoBtn.Click += OnClearVideoClick;

            _startBtn = new Button
            {
                Text = "Start Server",
                Width = 92,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(35, 134, 54),
                ForeColor = Color.White,
                Margin = new Padding(0, 1, 0, 0)
            };
            _startBtn.Click += OnStartClick;

            topPanel.Controls.AddRange(new Control[]
            {
                _channelBox, _resBox, _fpsBox, _openVideoBtn, _clearVideoBtn, _startBtn
            });

            var statusBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 26,
                BackColor = Color.FromArgb(34, 34, 40),
                Padding = new Padding(8, 4, 8, 4)
            };

            _statusLabel = new Label
            {
                Text = "Server stopped (Test Pattern ready).",
                Dock = DockStyle.Left,
                AutoSize = true,
                ForeColor = Color.LightGray
            };

            _statsLabel = new Label
            {
                Text = "0x0 | 0.0 fps | 0 sent",
                Dock = DockStyle.Right,
                AutoSize = true,
                ForeColor = Color.LightGreen
            };

            statusBar.Controls.Add(_statusLabel);
            statusBar.Controls.Add(_statsLabel);

            _previewBox = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black };
            _previewBox.Paint += (s, e) =>
            {
                lock (_bitmapLock)
                {
                    PreviewHelper.PaintFrame(e.Graphics, _previewBox.ClientRectangle, _currentBitmap, true);
                }
            };

            Controls.Add(_previewBox);
            Controls.Add(topPanel);
            Controls.Add(header);
            Controls.Add(statusBar);
        }

        private void OnOpenVideoClick(object sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Select Video File for FrameServer Sender",
                Filter = "Video Files|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.mxf;*.ts;*.wmv|All Files|*.*"
            };

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            _videoFilePath = dlg.FileName;
            _sourceChanged = true;
            _clearVideoBtn.Enabled = true;
            KillFfmpeg();

            if (_cts == null)
            {
                StartFromUi();
            }
            else
            {
                SetStatus($"Switching to video: {Path.GetFileName(_videoFilePath)}");
            }
        }

        private void OnClearVideoClick(object sender, EventArgs e)
        {
            _videoFilePath = null;
            _sourceChanged = true;
            _clearVideoBtn.Enabled = false;
            KillFfmpeg();
            SetStatus(_cts != null ? "Switched to Test Pattern." : "Server stopped (Test Pattern ready).");
        }

        private void OnStartClick(object sender, EventArgs e)
        {
            if (_cts != null)
            {
                StopServer();
                return;
            }
            StartFromUi();
        }

        private void StartFromUi()
        {
            string channel = _channelBox.Text?.Trim();
            if (string.IsNullOrEmpty(channel)) channel = "FrameServer R3 output (2)";

            var parts = (_resBox.SelectedItem?.ToString() ?? "1280x720").Split('x');
            short width = short.Parse(parts[0]);
            short height = short.Parse(parts[1]);
            short fps = short.Parse(_fpsBox.SelectedItem?.ToString() ?? "30");

            StartServer(channel, width, height, fps);
        }

        private void StartServer(string channel, short width, short height, short fps)
        {
            _cts = new CancellationTokenSource();
            _channelBox.Enabled = false;
            _resBox.Enabled = false;
            _fpsBox.Enabled = false;
            _startBtn.Text = "Stop Server";
            _startBtn.BackColor = Color.FromArgb(180, 48, 48);

            string srcLabel = string.IsNullOrEmpty(_videoFilePath)
                ? "Test Pattern"
                : Path.GetFileName(_videoFilePath);
            SetStatus($"Serving '{channel}' ({width}x{height} @ {fps}fps) [{srcLabel}]");

            var token = _cts.Token;
            Task.Run(() => ServerLoop(channel, width, height, fps, token), token);
        }

        public void StopServer()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }

            KillFfmpeg();

            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke((Action)(() =>
                {
                    _channelBox.Enabled = true;
                    _resBox.Enabled = true;
                    _fpsBox.Enabled = true;
                    _startBtn.Text = "Start Server";
                    _startBtn.BackColor = Color.FromArgb(35, 134, 54);
                    SetStatus("Server stopped.");
                }));
            }
        }

        private void KillFfmpeg()
        {
            lock (_ffmpegLock)
            {
                if (_ffmpegProcess != null)
                {
                    try
                    {
                        if (!_ffmpegProcess.HasExited) _ffmpegProcess.Kill();
                    }
                    catch { }
                    try { _ffmpegProcess.Dispose(); } catch { }
                    _ffmpegProcess = null;
                }
            }
        }

        private void ServerLoop(string channel, short width, short height, short fps, CancellationToken token)
        {
            FrameCreatorMemInstance creator = null;
            IPCClient ipcClient = null;
            IntPtr unmanagedBuffer = IntPtr.Zero;

            try
            {
                creator = new FrameCreatorMemInstance(channel);
                creator.SetupImageFrame(width, height, 32, fps);
                creator.RegisterImageSignal();
                creator.RegisterImageFeedEvent();

                try
                {
                    ipcClient = new IPCClient(".", channel, this);
                    ipcClient.Connect(100);
                }
                catch { }

                int byteCount = width * height * 4;
                unmanagedBuffer = Marshal.AllocHGlobal(byteCount);
                byte[] managedBuffer = new byte[byteCount];

                long totalSent = 0;
                int fpsCounter = 0;
                var fpsWatch = Stopwatch.StartNew();
                int frameIntervalMs = Math.Max(1, 1000 / fps);
                float ballX = width * 0.2f;
                float ballY = height * 0.4f;
                float vx = 8f;
                float vy = 5f;

                while (!token.IsCancellationRequested)
                {
                    _sourceChanged = false;
                    string videoPath = _videoFilePath;

                    if (!string.IsNullOrEmpty(videoPath))
                    {
                        RunVideoStream(videoPath, width, height, fps, byteCount, managedBuffer, unmanagedBuffer,
                            creator, ref totalSent, ref fpsCounter, fpsWatch, token);
                    }
                    else
                    {
                        var frameStart = Stopwatch.StartNew();
                        using (var bmp = RenderTestFrame(width, height, totalSent, ref ballX, ref ballY, ref vx, ref vy))
                        {
                            CopyBitmapToBuffer(bmp, width, height, managedBuffer, byteCount);
                            PublishFrame(creator, unmanagedBuffer, managedBuffer, byteCount);
                            SetPreviewBitmap(new Bitmap(bmp));
                        }

                        totalSent++;
                        fpsCounter++;
                        MaybeUpdateStats(width, height, ref fpsCounter, fpsWatch, totalSent);

                        int sleep = frameIntervalMs - (int)frameStart.ElapsedMilliseconds;
                        if (sleep > 0) Thread.Sleep(sleep);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    SetStatus($"Server error: {ex.Message}");
                }
            }
            finally
            {
                KillFfmpeg();
                if (unmanagedBuffer != IntPtr.Zero) Marshal.FreeHGlobal(unmanagedBuffer);
                try { creator?.Dispose(); } catch { }
            }
        }

        private void RunVideoStream(
            string videoPath,
            short width,
            short height,
            short fps,
            int byteCount,
            byte[] managedBuffer,
            IntPtr unmanagedBuffer,
            FrameCreatorMemInstance creator,
            ref long totalSent,
            ref int fpsCounter,
            Stopwatch fpsWatch,
            CancellationToken token)
        {
            string args = $"-re -stream_loop -1 -i \"{videoPath}\" " +
                          $"-vf \"scale={width}:{height}:force_original_aspect_ratio=decrease," +
                          $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black@0\" " +
                          $"-r {fps} -pix_fmt bgra -f rawvideo -";

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            Process proc;
            try
            {
                proc = Process.Start(psi);
                lock (_ffmpegLock)
                {
                    _ffmpegProcess = proc;
                }
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to start ffmpeg: {ex.Message}");
                _videoFilePath = null;
                return;
            }

            SetStatus($"Playing: {Path.GetFileName(videoPath)} ({width}x{height} @ {fps}fps)");
            Stream stdout = proc.StandardOutput.BaseStream;

            while (!token.IsCancellationRequested && !_sourceChanged)
            {
                if (!ReadExact(stdout, managedBuffer, byteCount))
                {
                    break;
                }

                PublishFrame(creator, unmanagedBuffer, managedBuffer, byteCount);
                SetPreviewBitmap(BitmapFromBgra(width, height, managedBuffer));

                totalSent++;
                fpsCounter++;
                MaybeUpdateStats(width, height, ref fpsCounter, fpsWatch, totalSent);
            }

            KillFfmpeg();
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }

        private static void CopyBitmapToBuffer(Bitmap bmp, int width, int height, byte[] buffer, int byteCount)
        {
            var rect = new Rectangle(0, 0, width, height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(data.Scan0, buffer, 0, byteCount);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private static Bitmap BitmapFromBgra(int width, int height, byte[] bgraBytes)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(bgraBytes, 0, data.Scan0, bgraBytes.Length);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return bmp;
        }

        private static void PublishFrame(FrameCreatorMemInstance creator, IntPtr unmanagedBuffer, byte[] managedBuffer, int byteCount)
        {
            Marshal.Copy(managedBuffer, 0, unmanagedBuffer, byteCount);
            creator.GetVideoFeed(true);
            creator.WriteOnImageBuffer(unmanagedBuffer, byteCount);
        }

        private void SetPreviewBitmap(Bitmap bmp)
        {
            Bitmap old;
            lock (_bitmapLock)
            {
                old = _currentBitmap;
                _currentBitmap = bmp;
            }
            old?.Dispose();

            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke((Action)(() => _previewBox.Invalidate()));
            }
        }

        private void MaybeUpdateStats(short width, short height, ref int fpsCounter, Stopwatch fpsWatch, long totalSent)
        {
            if (fpsWatch.ElapsedMilliseconds >= 1000)
            {
                double actualFps = fpsCounter * 1000.0 / fpsWatch.ElapsedMilliseconds;
                fpsCounter = 0;
                fpsWatch.Restart();
                UpdateStats($"{width}x{height} | {actualFps:F1} fps | {totalSent} sent");
            }
        }

        private static Bitmap RenderTestFrame(int width, int height, long frameIndex, ref float bx, ref float by, ref float vx, ref float vy)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                int radius = Math.Max(24, height / 16);
                bx += vx;
                by += vy;
                if (bx < radius || bx > width - radius) vx = -vx;
                if (by < radius || by > height - radius) vy = -vy;

                using (var ballBrush = new SolidBrush(Color.FromArgb(220, 0, 174, 255)))
                using (var ballPen = new Pen(Color.White, 3))
                {
                    g.FillEllipse(ballBrush, bx - radius, by - radius, radius * 2, radius * 2);
                    g.DrawEllipse(ballPen, bx - radius, by - radius, radius * 2, radius * 2);
                }

                int barH = Math.Max(64, height / 9);
                int barY = height - barH - (height / 14);
                int barX = width / 16;
                int barW = width - (barX * 2);

                using (var barBrush = new LinearGradientBrush(
                    new Rectangle(barX, barY, barW, barH),
                    Color.FromArgb(215, 16, 40, 80),
                    Color.FromArgb(215, 10, 110, 180),
                    LinearGradientMode.Horizontal))
                {
                    g.FillRectangle(barBrush, barX, barY, barW, barH);
                }

                using (var accentBrush = new SolidBrush(Color.FromArgb(245, 255, 170, 0)))
                {
                    g.FillRectangle(accentBrush, barX, barY, 10, barH);
                }

                using (var titleFont = new Font("Segoe UI", Math.Max(14f, barH * 0.30f), FontStyle.Bold))
                using (var subFont = new Font("Consolas", Math.Max(11f, barH * 0.22f), FontStyle.Regular))
                {
                    g.DrawString("wTVision FrameServer Test Feed", titleFont, Brushes.White, barX + 24, barY + 10);
                    g.DrawString($"Frame #{frameIndex}  |  {DateTime.Now:HH:mm:ss.fff}  |  BGRA Alpha", subFont, Brushes.LightGoldenrodYellow, barX + 24, barY + (barH * 0.54f));
                }
            }
            return bmp;
        }

        private void SetStatus(string text)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke((Action)(() => _statusLabel.Text = text));
            else _statusLabel.Text = text;
        }

        private void UpdateStats(string text)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke((Action)(() => _statsLabel.Text = text));
            else _statsLabel.Text = text;
        }

        public void OnAsyncConnect(IPCPipeInfo pipeInfo, out object state) => state = null;
        public void OnAsyncDisconnect(IPCPipeInfo pipeInfo, object state) { }
        public void OnAsyncClientUpdate(string name, PipeStream pipeStream, IPCData ipcData, object state, bool isGUI) { }
        public void OnAsyncMessage(IPCPipeInfo pipeInfo, byte[] data, int bytes, object state)
        {
            string msgType = MessageProcessment.GetMessageType(data, bytes);
            if (msgType == "IDYourself")
            {
                try
                {
                    byte[] reply = MessageProcessment.ConstructMessage("IDReply", "FrameServerSender-Graphics");
                    pipeInfo.PipeStream.Write(reply, 0, reply.Length);
                    pipeInfo.PipeStream.Flush();
                }
                catch { }
            }
        }
    }
}
