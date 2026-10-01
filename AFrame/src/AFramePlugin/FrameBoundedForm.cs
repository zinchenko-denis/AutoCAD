using System;
using System.Drawing;
using System.Windows.Forms;

namespace AFramePlugin
{
    // Shared native-size boundary for resizable facade parameter dialogs.
    public class FrameBoundedForm : Form
    {
        private bool _constrainingWindow;

        // WinForms can retain the requested ClientSize when Windows has already
        // limited the HWND to the monitor or expanded it to MinimumSize. Clamp
        // both bounds before the base class records its client-size cache, so
        // layout and DrawToBitmap use exactly the visible native client area.
        protected override void SetClientSizeCore(int width, int height)
        {
            Rectangle work = Screen.FromPoint(Location).WorkingArea;
            FitMinimum(work.Size);
            Size border = SizeFromClientSize(Size.Empty);
            int minWidth = Math.Max(1, MinimumSize.Width - border.Width),
                minHeight = Math.Max(1, MinimumSize.Height - border.Height);
            base.SetClientSizeCore(Math.Max(minWidth, Math.Min(width, Math.Max(1, work.Width - border.Width))),
                Math.Max(minHeight, Math.Min(height, Math.Max(1, work.Height - border.Height))));
        }

        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            if (_constrainingWindow) { base.SetBoundsCore(x, y, width, height, specified); return; }
            Rectangle work = Screen.FromPoint(new Point(x, y)).WorkingArea;
            FitMinimum(work.Size);
            width = Math.Min(Math.Max(width, MinimumSize.Width), work.Width);
            height = Math.Min(Math.Max(height, MinimumSize.Height), work.Height);
            x = Math.Max(work.Left, Math.Min(x, work.Right - width));
            y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
            base.SetBoundsCore(x, y, width, height, specified);
        }

        private void FitMinimum(Size available)
        {
            // Font autoscaling also scales MinimumSize. A minimum larger than
            // the working area must not force the action buttons off-screen.
            if (_constrainingWindow) return;
            Size minimum = new Size(Math.Min(MinimumSize.Width, available.Width), Math.Min(MinimumSize.Height, available.Height));
            if (minimum == MinimumSize) return;
            _constrainingWindow = true;
            try { MinimumSize = minimum; }
            finally { _constrainingWindow = false; }
        }

    }
}
