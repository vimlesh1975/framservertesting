using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FrameServerViewer
{
    internal static class PreviewHelper
    {
        public static void PaintFrame(Graphics g, Rectangle bounds, Bitmap bitmap, bool drawCheckerboard)
        {
            if (drawCheckerboard)
            {
                const int cell = 16;
                using var light = new SolidBrush(Color.FromArgb(52, 52, 58));
                using var dark = new SolidBrush(Color.FromArgb(36, 36, 42));
                g.FillRectangle(dark, bounds);
                for (int y = 0; y < bounds.Height; y += cell)
                {
                    for (int x = ((y / cell) % 2) * cell; x < bounds.Width; x += cell * 2)
                    {
                        g.FillRectangle(light, x, y, cell, cell);
                    }
                }
            }
            else
            {
                g.Clear(Color.Black);
            }

            if (bitmap == null) return;

            g.InterpolationMode = InterpolationMode.Bilinear;
            float aspect = (float)bitmap.Width / Math.Max(1, bitmap.Height);
            float boxAspect = (float)bounds.Width / Math.Max(1, bounds.Height);
            Rectangle dest;
            if (boxAspect > aspect)
            {
                int h = bounds.Height;
                int w = (int)(h * aspect);
                dest = new Rectangle((bounds.Width - w) / 2, 0, w, h);
            }
            else
            {
                int w = bounds.Width;
                int h = (int)(w / aspect);
                dest = new Rectangle(0, (bounds.Height - h) / 2, w, h);
            }

            g.DrawImage(bitmap, dest);
        }
    }
}

