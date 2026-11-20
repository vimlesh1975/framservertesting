using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IPCNamedPipes;
using Wtvision.DataShare;

namespace FrameServerViewer
{
    public class ReceiverPanel : UserControl, IIPCEventsCallback
    {
        private readonly ComboBox _channelBox;
        private readonly Button _connectBtn;
        private readonly Button _snapshotBtn;
        private readonly CheckBox _checkerBox;
        private readonly Label _statusLabel;
        private readonly Label _statsLabel;
        private readonly PictureBox _previewBox;

        private CancellationTokenSource _cts;
        private IPCServer _ipcServer;
        private Bitmap _currentBitmap;
        private readonly object _bitmapLock = new object();

        public ReceiverPanel()
        {
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(24, 24, 28);
            ForeColor = Color.WhiteSmoke;

            var header = new Label
            {
                Text = "FRAME SERVER RECEIVER (VIEWER)",
                Dock = DockStyle.Top,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                BackColor = Color.FromArgb(42, 42, 50),
                ForeColor = Color.SkyBlue
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
                Width = 185,
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

            _connectBtn = new Button
            {
                Text = "Connect",
                Width = 86,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 212),
                ForeColor = Color.White,
                Margin = new Padding(0, 1, 6, 0)
            };
            _connectBtn.Click += OnConnectClick;

            _snapshotBtn = new Button
            {
                Text = "Save PNG",
                Width = 76,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 55, 65),
                ForeColor = Color.White,
                Enabled = false,
                Margin = new Padding(0, 1, 8, 0)
            };
            _snapshotBtn.Click += OnSnapshotClick;

            _checkerBox = new CheckBox
            {
                Text = "Checker",
                Checked = true,
                AutoSize = true,
                Margin = new Padding(0, 5, 0, 0)
            };
            _checkerBox.CheckedChanged += (s, e) => _previewBox.Invalidate();

            topPanel.Controls.AddRange(new Control[] { _channelBox, _connectBtn, _snapshotBtn, _checkerBox });

            var statusBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 26,
                BackColor = Color.FromArgb(34, 34, 40),
                Padding = new Padding(8, 4, 8, 4)
            };

            _statusLabel = new Label
            {
                Text = "Idle. Click Connect to receive.",
                Dock = DockStyle.Left,
                AutoSize = true,
                ForeColor = Color.LightGray
            };

            _statsLabel = new Label
            {
                Text = "0x0 | 0.0 fps | 0 frames",
                Dock = DockStyle.Right,
                AutoSize = true,
                ForeColor = Color.SkyBlue
            };

            statusBar.Controls.Add(_statusLabel);
            statusBar.Controls.Add(_statsLabel);

            _previewBox = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black };
            _previewBox.Paint += (s, e) =>
            {
                lock (_bitmapLock)
                {
                    PreviewHelper.PaintFrame(e.Graphics, _previewBox.ClientRectangle, _currentBitmap, _checkerBox.Checked);
                }
            };

            Controls.Add(_previewBox);
            Controls.Add(topPanel);
            Controls.Add(header);
            Controls.Add(statusBar);
        }

        private void OnConnectClick(object sender, EventArgs e)
        {
            if (_cts != null)
            {
                StopCapture();
                return;
            }

            string channel = _channelBox.Text?.Trim();
            if (string.IsNullOrEmpty(channel)) channel = "FrameServer R3 output (2)";
            StartCapture(channel);
        }

        private void StartCapture(string channel)
        {
            _cts = new CancellationTokenSource();
            _channelBox.Enabled = false;
            _connectBtn.Text = "Disconnect";
            _connectBtn.BackColor = Color.FromArgb(180, 48, 48);
            SetStatus($"Listening for '{channel}'...");

            try
            {
                _ipcServer = new IPCServer(channel, this, 4, null);
                _ipcServer.Start();
            }
            catch (Exception ex)
            {
                SetStatus($"IPC note: {ex.Message} (reading shared memory)");
            }

            var token = _cts.Token;
            Task.Run(() => CaptureLoop(channel, token), token);
        }

        public void StopCapture()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }

            try { _ipcServer?.Stop(); } catch { }
            _ipcServer = null;

            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke((Action)(() =>
                {
                    _channelBox.Enabled = true;
                    _connectBtn.Text = "Connect";
                    _connectBtn.BackColor = Color.FromArgb(0, 120, 212);
                    SetStatus("Disconnected.");
                }));
            }
        }

        private void CaptureLoop(string channel, CancellationToken token)
        {
            FrameReaderMemInstance reader = null;
            FrameFeedMemInvoker feedInvoker = null;

            try
            {
                reader = new FrameReaderMemInstance(channel);
                while (!token.IsCancellationRequested && !reader.InitFrameDefsMemAccess())
                {
                    SetStatus($"Waiting for '{channel}' shared memory...");
                    Thread.Sleep(200);
                }

                if (token.IsCancellationRequested) return;

                reader.GetImageFrameDefs(out short width, out short height, out short bpp, out short fpsRaw);
                if (width <= 0 || height <= 0)
                {
                    SetStatus($"Invalid frame definitions: {width}x{height}");
                    return;
                }

                reader.SetupImageFrame(width, height, bpp, true);
                feedInvoker = new FrameFeedMemInvoker();
                feedInvoker.SetupFeed(channel, false);

                SetStatus($"Connected to '{channel}' ({width}x{height}, {bpp} bpp)");

                int byteCount = width * height * Math.Max(4, bpp / 8);
                byte[] managedBuffer = new byte[byteCount];
                long totalFrames = 0;
                int fpsCounter = 0;
                var fpsWatch = Stopwatch.StartNew();

                while (!token.IsCancellationRequested)
                {
                    feedInvoker.InvokeFeed();

                    IntPtr ptr = reader.GetNextRawImageFrame();
                    if (ptr != IntPtr.Zero)
                    {
                        try
                        {
                            Marshal.Copy(ptr, managedBuffer, 0, byteCount);
                            UpdateFrameBitmap(width, height, managedBuffer);
                            totalFrames++;
                            fpsCounter++;
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(ptr);
                        }
                    }

                    if (fpsWatch.ElapsedMilliseconds >= 1000)
                    {
                        double fps = fpsCounter * 1000.0 / fpsWatch.ElapsedMilliseconds;
                        fpsCounter = 0;
                        fpsWatch.Restart();
                        UpdateStats($"{width}x{height} | {fps:F1} fps | {totalFrames} frames");
                    }

                    Thread.Sleep(4);
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    SetStatus($"Error: {ex.Message}");
                }
            }
            finally
            {
                try { feedInvoker?.Dispose(); } catch { }
                try { reader?.Dispose(); } catch { }
            }
        }

        private void UpdateFrameBitmap(int width, int height, byte[] bgraBytes)
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

            Bitmap old;
            lock (_bitmapLock)
            {
                old = _currentBitmap;
                _currentBitmap = bmp;
            }
            old?.Dispose();

            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke((Action)(() =>
                {
                    _snapshotBtn.Enabled = true;
                    _previewBox.Invalidate();
                }));
            }
        }

        private void OnSnapshotClick(object sender, EventArgs e)
        {
            Bitmap copy;
            lock (_bitmapLock)
            {
                if (_currentBitmap == null) return;
                copy = new Bitmap(_currentBitmap);
            }

            using (copy)
            using (var dlg = new SaveFileDialog
            {
                Filter = "PNG Image|*.png",
                FileName = $"frameserver_r3_{DateTime.Now:yyyyMMdd_HHmmss}.png"
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    copy.Save(dlg.FileName, ImageFormat.Png);
                    SetStatus($"Saved {dlg.FileName}");
                }
            }
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

        public void OnAsyncConnect(IPCPipeInfo pipeInfo, out object state)
        {
            state = null;
            SetStatus($"IPC client connected on '{pipeInfo?.Name}'.");
            try
            {
                byte[] msg = MessageProcessment.ConstructMessage("IDYourself", "FrameServerViewer");
                pipeInfo.PipeStream.Write(msg, 0, msg.Length);
                pipeInfo.PipeStream.Flush();
            }
            catch { }
        }

        public void OnAsyncDisconnect(IPCPipeInfo pipeInfo, object state) => SetStatus("IPC client disconnected.");
        public void OnAsyncClientUpdate(string name, PipeStream pipeStream, IPCData ipcData, object state, bool isGUI) { }
        public void OnAsyncMessage(IPCPipeInfo pipeInfo, byte[] data, int bytes, object state)
        {
            string msgType = MessageProcessment.GetMessageType(data, bytes);
            string content = MessageProcessment.GetMessageContent(data, bytes);
            SetStatus($"IPC [{msgType}]: {content}");
        }
    }
}

