using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace PrintSpoolerGuardian
{
    /// <summary>
    /// Generates a printer icon programmatically for the system tray.
    /// No external .ico file needed — drawn with GDI+ at runtime.
    /// </summary>
    public static class IconHelper
    {
        // Keep the source bitmap alive for the icon's lifetime.
        // Icon.FromHandle() wraps a GDI handle owned by the bitmap;
        // disposing the bitmap would invalidate the handle.
        private static Bitmap _sourceBitmap;

        /// <summary>
        /// Creates a 16x16 printer icon for the system tray.
        /// </summary>
        public static Icon CreatePrinterIcon()
        {
            _sourceBitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(_sourceBitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // === Paper feeding in from the top ===
                using (var paperBrush = new SolidBrush(Color.FromArgb(245, 245, 245)))
                {
                    g.FillRectangle(paperBrush, 4, 0, 8, 5);
                }
                using (var paperPen = new Pen(Color.FromArgb(80, 80, 80)))
                {
                    g.DrawRectangle(paperPen, 4, 0, 8, 5);
                }
                // Paper lines (subtle detail)
                using (var linePen = new Pen(Color.FromArgb(200, 200, 200)))
                {
                    linePen.Width = 1;
                    g.DrawLine(linePen, 5, 2, 11, 2);
                }

                // === Printer body (dark gray rectangle) ===
                using (var bodyBrush = new SolidBrush(Color.FromArgb(55, 55, 58)))
                {
                    g.FillRectangle(bodyBrush, 0, 5, 16, 11);
                }
                using (var outlinePen = new Pen(Color.FromArgb(30, 30, 30)))
                {
                    g.DrawRectangle(outlinePen, 0, 5, 15, 10);
                }

                // === Paper output slot (darker inset) ===
                using (var slotBrush = new SolidBrush(Color.FromArgb(35, 35, 38)))
                {
                    g.FillRectangle(slotBrush, 2, 9, 12, 3);
                }
                // Slot top edge highlight
                using (var slotEdgePen = new Pen(Color.FromArgb(70, 70, 75)))
                {
                    g.DrawLine(slotEdgePen, 2, 9, 13, 9);
                }

                // === Status LED (green dot) ===
                using (var ledBrush = new SolidBrush(Color.FromArgb(0, 200, 80)))
                {
                    g.FillEllipse(ledBrush, 11, 6, 3, 3);
                }
                // LED highlight
                using (var ledHighlight = new SolidBrush(Color.FromArgb(120, 255, 150)))
                {
                    g.FillEllipse(ledHighlight, 12, 6, 1, 1);
                }

                // === Subtle body highlight (top edge reflection) ===
                using (var highlightPen = new Pen(Color.FromArgb(100, 100, 105)))
                {
                    g.DrawLine(highlightPen, 1, 6, 14, 6);
                }
            }

            return Icon.FromHandle(_sourceBitmap.GetHicon());
        }
    }
}
