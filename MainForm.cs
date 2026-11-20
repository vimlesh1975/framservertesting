using System.Drawing;
using System.Windows.Forms;

namespace FrameServerViewer
{
    public class MainForm : Form
    {
        private readonly SenderPanel _senderPanel;
        private readonly ReceiverPanel _receiverPanel;

        public MainForm()
        {
            Text = "wTVision FrameServer (Sender) & Receiver — Side-by-Side";
            Width = 1480;
            Height = 760;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 24, 28);

            _senderPanel = new SenderPanel();
            _receiverPanel = new ReceiverPanel();

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                BackColor = Color.FromArgb(60, 60, 70)
            };
            split.Panel1.Controls.Add(_senderPanel);
            split.Panel2.Controls.Add(_receiverPanel);

            Load += (s, e) => split.SplitterDistance = split.Width / 2;
            FormClosing += (s, e) =>
            {
                _senderPanel.StopServer();
                _receiverPanel.StopCapture();
            };

            Controls.Add(split);
        }
    }
}
