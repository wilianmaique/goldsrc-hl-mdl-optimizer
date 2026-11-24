using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MdlOptimizer
{
    sealed class QueueScrollBar : Control
    {
        readonly ModelList list;
        bool needed, hovered, dragging;
        int grabOffset, wheelRemainder;

        public QueueScrollBar(ModelList list)
        {
            this.list = list;
            ApplyLanguage();
            AccessibleRole = AccessibleRole.ScrollBar;
            Dock = DockStyle.Right;
            Width = 16;
            Margin = Padding.Empty;
            BackColor = Theme.Stage;
            SetStyle(Theme.OpaquePainting | ControlStyles.Selectable, true);
            Visible = false;
            list.ViewportChanged += UpdateRange;
            UpdateRange(this, EventArgs.Empty);
        }

        public void ApplyLanguage() { AccessibleName = L.T("chrome.queueScroll"); }

        int PageRows { get { return Math.Max(1, list.VisibleRowCount); } }
        int LastPosition { get { return Math.Max(0, list.Items.Count - PageRows); } }

        void UpdateRange(object sender, EventArgs e)
        {
            if (IsDisposed || Disposing || list.IsDisposed) return;
            bool overflow = list.IsHandleCreated && LastPosition > 0;
            if (needed != overflow) {
                needed = overflow;
                if (!needed) {
                    Capture = false;
                    wheelRemainder = 0;
                }
                Visible = needed;
            }
            if (needed) {
                Invalidate();
                if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            }
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new ScrollPosition(this);
        }

        sealed class ScrollPosition : ControlAccessibleObject
        {
            readonly QueueScrollBar bar;
            public ScrollPosition(QueueScrollBar bar) : base(bar) { this.bar = bar; }

            public override string Value
            {
                get { return (bar.list.FirstVisibleRow + 1).ToString(); }
                set {
                    int row;
                    if (Int32.TryParse(value, out row)) bar.list.ScrollToRow(Math.Max(1, row) - 1);
                }
            }
        }

        Rectangle Track
        {
            get {
                int inset = Theme.Scale(this, 4);
                int top = list.HeaderHeight + inset;
                return new Rectangle(inset, top, Math.Max(1, Width - 2 * inset), Math.Max(0, Height - top - inset));
            }
        }

        Rectangle Thumb(Rectangle track)
        {
            int total = Math.Max(1, list.Items.Count);
            int length = (int)Math.Round((double)track.Height * PageRows / total);
            length = Math.Min(track.Height, Math.Max(Theme.Scale(this, 24), length));
            int maximum = LastPosition;
            int position = Math.Min(maximum, Math.Max(0, list.FirstVisibleRow));
            int offset = maximum == 0 ? 0 : (int)Math.Round((double)(track.Height - length) * position / maximum);
            return new Rectangle(track.Left, track.Top + offset, track.Width, length);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var fill = new SolidBrush(BackColor)) g.FillRectangle(fill, ClientRectangle);
            using (var header = new SolidBrush(ModelList.HeaderFill)) g.FillRectangle(header, 0, 0, Width, list.HeaderHeight);
            var track = Track;
            if (track.Height <= 0) return;
            var thumb = Thumb(track);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var rail = Theme.Rounded(track, track.Width / 2f))
            using (var fill = new SolidBrush(Color.FromArgb(28, 43, 58))) g.FillPath(fill, rail);
            Color color = dragging ? Theme.Accent : (hovered || Focused ? Theme.Muted : Color.FromArgb(89, 114, 140));
            using (var grip = Theme.Rounded(thumb, thumb.Width / 2f))
            using (var fill = new SolidBrush(color)) g.FillPath(fill, grip);
            if (Focused && ShowFocusCues) Theme.DrawFocus(g, Rectangle.Inflate(thumb, 2, 1), Theme.Quiet);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || e.Y < list.HeaderHeight) return;
            var track = Track;
            if (track.Height == 0) return;
            var thumb = Thumb(track);
            Focus();
            if (e.Y >= thumb.Top && e.Y < thumb.Bottom) {
                grabOffset = e.Y - thumb.Top;
                dragging = true;
                Capture = true;
                Invalidate();
            } else {
                list.ScrollToRow(list.FirstVisibleRow + (e.Y < thumb.Top ? -PageRows : PageRows));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            var track = Track;
            int travel = track.Height - Thumb(track).Height;
            if (travel <= 0) return;
            int offset = Math.Max(0, Math.Min(travel, e.Y - grabOffset - track.Top));
            list.ScrollToRow((int)Math.Round((double)offset * LastPosition / travel));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && dragging) Capture = false;
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) { dragging = false; Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int delta = SystemInformation.MouseWheelScrollDelta;
            wheelRemainder += e.Delta;
            int steps = wheelRemainder / delta;
            wheelRemainder %= delta;
            int lines = SystemInformation.MouseWheelScrollLines;
            if (steps != 0) list.ScrollToRow(list.FirstVisibleRow - steps * (lines < 0 ? PageRows : lines));
        }

        protected override bool IsInputKey(Keys key)
        {
            return key == Keys.Up || key == Keys.Down || base.IsInputKey(key);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Modifiers != Keys.None) return;
            int row = list.FirstVisibleRow;
            switch (e.KeyCode) {
                case Keys.Up: row--; break;
                case Keys.Down: row++; break;
                case Keys.PageUp: row -= PageRows; break;
                case Keys.PageDown: row += PageRows; break;
                case Keys.Home: row = 0; break;
                case Keys.End: row = LastPosition; break;
                default: return;
            }
            list.ScrollToRow(row);
            e.SuppressKeyPress = true;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) list.ViewportChanged -= UpdateRange;
            base.Dispose(disposing);
        }
    }
}
