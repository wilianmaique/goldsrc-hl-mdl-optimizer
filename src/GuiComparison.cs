using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MdlOptimizer
{
    sealed class ModelList : ListView
    {
        const TextFormatFlags CellFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | Theme.PaintTextFlags;
        internal static readonly Color HeaderFill = Color.FromArgb(27, 42, 58);
        static readonly Color SelectedFill = Color.FromArgb(21, 44, 66);
        readonly ImageList rowMetrics = new ImageList();
        IntPtr headerFont;
        Control viewport;
        bool fitting, fitted, notificationPending;
        double? stateShare;
        int lastFirst = -1, lastPage = -1, lastCount = -1;
        Size lastViewportSize;

        const int GetHeader = 0x101f, GetTopIndex = 0x1027, GetCountPerPage = 0x1028, ScrollList = 0x1014;
        internal event EventHandler ViewportChanged;

        internal int FirstVisibleRow { get { return NativeValue(GetTopIndex); } }
        internal int VisibleRowCount { get { return Math.Max(1, NativeValue(GetCountPerPage)); } }
        internal int HeaderHeight
        {
            get
            {
                if (!IsHandleCreated) return 0;
                var header = SendMessage(Handle, GetHeader, IntPtr.Zero, IntPtr.Zero);
                Rectangle bounds;
                return header != IntPtr.Zero && GetClientRect(header, out bounds) ? bounds.Height : 0;
            }
        }

        int NativeValue(int message)
        {
            return IsHandleCreated ? (int)SendMessage(Handle, message, IntPtr.Zero, IntPtr.Zero) : 0;
        }

        internal void ScrollToRow(int row)
        {
            if (!IsHandleCreated || Items.Count == 0) return;
            int target = Math.Max(0, Math.Min(row, Items.Count - VisibleRowCount));
            int distance = target - FirstVisibleRow;
            if (distance == 0) return;
            int rowHeight = GetItemRect(0).Height;
            SendMessage(Handle, ScrollList, IntPtr.Zero, (IntPtr)(distance * rowHeight));
        }

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr window, out Rectangle bounds);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr handle);

        public ModelList()
        {
            View = View.Details;
            FullRowSelect = true;
            MultiSelect = false;
            HideSelection = false;
            ShowItemToolTips = true;
            HeaderStyle = ColumnHeaderStyle.Nonclickable;
            BorderStyle = BorderStyle.None;
            OwnerDraw = true;
            DoubleBuffered = true;
            BackColor = Theme.Stage;
            ForeColor = Theme.Text;
            Dock = DockStyle.None;
            Margin = Padding.Empty;
            Columns.Add(new ColumnHeader());
            Columns.Add(new ColumnHeader());
            ApplyLanguage();
            // The native list uses its small-image height for row layout and hit testing.
            rowMetrics.ImageSize = new Size(1, 36);
            SmallImageList = rowMetrics;
        }

        public void ApplyLanguage()
        {
            AccessibleName = L.T("chrome.queue");
            Columns[0].Text = L.T("chrome.file");
            Columns[1].Text = L.T("chrome.state");
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            rowMetrics.ImageSize = new Size(1, Math.Min(256, Math.Max(Font.Height + Px(10), Px(36))));
            // The native header takes its height from its own font. Its actual text is
            // owner-drawn below with the same readable size as the file names.
            const int SetFont = 0x0030;
            IntPtr header = SendMessage(Handle, GetHeader, IntPtr.Zero, IntPtr.Zero);
            if (header != IntPtr.Zero)
            {
                headerFont = Theme.Font(Math.Max(17f, Font.SizeInPoints)).ToHfont();
                SendMessage(header, SetFont, headerFont, IntPtr.Zero);
            }
            FitViewport();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            base.OnHandleDestroyed(e);
            if (headerFont != IntPtr.Zero) { DeleteObject(headerFont); headerFont = IntPtr.Zero; }
            fitted = false;
            notificationPending = false;
        }

        int Px(int value) { return Theme.Scale(this, value); }

        protected override void OnParentChanged(EventArgs e)
        {
            if (viewport != null) viewport.ClientSizeChanged -= ViewportResized;
            base.OnParentChanged(e);
            viewport = Parent;
            if (viewport != null) viewport.ClientSizeChanged += ViewportResized;
            FitViewport();
        }

        void ViewportResized(object sender, EventArgs e) { FitViewport(); }

        void FitViewport()
        {
            if (fitting || viewport == null || !IsHandleCreated || Columns.Count != 2) return;
            fitting = true;
            try
            {
                int width = viewport.ClientSize.Width, height = viewport.ClientSize.Height;
                int preferred = stateShare.HasValue ? (int)Math.Round(width * stateShare.Value)
                    : Math.Min(width / 2, Math.Max(Px(134), width / 3));
                int minimum = Math.Min(width / 2, Px(52));
                int stateWidth = Math.Max(minimum, Math.Min(width - minimum, preferred));
                // Keep native scrolling, hit testing, accessibility and keyboard navigation.
                // The parent clips the native scrollbar gutter. Columns use the visible width
                // so the entire Estado cell remains inside the viewport even without a scrollbar.
                int gutter = Math.Max(SystemInformation.VerticalScrollBarWidth, Width - ClientSize.Width);
                if (Columns[0].Width != width - stateWidth) Columns[0].Width = width - stateWidth;
                if (Columns[1].Width != stateWidth) Columns[1].Width = stateWidth;
                SetBounds(0, 0, width + gutter, height + Height - ClientSize.Height);
                // During a shrink comctl32 may briefly reserve a horizontal scrollbar before
                // finishing the new column layout. Its final client height must match the host.
                if (ClientSize.Height != height) Height += height - ClientSize.Height;
                fitted = true;
            }
            finally { fitting = false; }
            QueueNotification();
        }

        void QueueNotification()
        {
            if (notificationPending || ViewportChanged == null || !IsHandleCreated || Disposing || IsDisposed) return;
            notificationPending = true;
            BeginInvoke((MethodInvoker)delegate {
                notificationPending = false;
                if (viewport == null || !IsHandleCreated || Disposing || IsDisposed) return;
                int first = FirstVisibleRow, page = VisibleRowCount, count = Items.Count;
                Size size = viewport.ClientSize;
                if (first == lastFirst && page == lastPage && count == lastCount && size == lastViewportSize) return;
                lastFirst = first;
                lastPage = page;
                lastCount = count;
                lastViewportSize = size;
                var changed = ViewportChanged;
                if (changed != null) changed(this, EventArgs.Empty);
            });
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            switch (m.Msg)
            {
                case 0x0005: // WM_SIZE also fires when a native scrollbar appears.
                    FitViewport();
                    break;
                case 0x000f: // WM_PAINT covers internal scrolling such as incremental search.
                case 0x0100: // WM_KEYDOWN
                case 0x0115: // WM_VSCROLL
                case 0x020a: // WM_MOUSEWHEEL
                case 0x1007: // LVM_INSERTITEMA
                case 0x104d: // LVM_INSERTITEMW
                case 0x1008: // LVM_DELETEITEM
                case 0x1009: // LVM_DELETEALLITEMS
                case 0x1013: // LVM_ENSUREVISIBLE
                case ScrollList:
                    QueueNotification();
                    break;
            }
        }

        protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e)
        {
            base.OnColumnWidthChanged(e);
            if (!fitting && fitted && viewport != null && viewport.ClientSize.Width > 0 && Columns.Count == 2)
            {
                int width = viewport.ClientSize.Width;
                int requested = e.ColumnIndex == 0 ? width - Columns[0].Width : Columns[1].Width;
                stateShare = (double)requested / width;
                FitViewport();
            }
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            using (var fill = new SolidBrush(HeaderFill)) e.Graphics.FillRectangle(fill, e.Bounds);
            Rectangle text = e.Bounds;
            int inset = Px(12);
            text.X += inset;
            text.Width = Math.Max(0, text.Width - inset * 2);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, Theme.Font(Font.SizeInPoints, FontStyle.Bold),
                text, Theme.Text, CellFlags);
        }

        void DrawCellBackground(Graphics graphics, Rectangle cell, bool selected)
        {
            using (var background = new SolidBrush(BackColor)) graphics.FillRectangle(background, cell);
            int visibleWidth = viewport == null ? ClientSize.Width : viewport.ClientSize.Width;
            if (!selected || visibleWidth < 4 || cell.Height < 4) return;
            var selection = new Rectangle(1, cell.Top + 1, visibleWidth - 3, cell.Height - 3);
            using (var path = Theme.Rounded(selection, Px(6)))
            using (var fill = new SolidBrush(SelectedFill))
            using (var edge = new Pen(Theme.Accent))
            {
                var state = graphics.Save();
                graphics.SetClip(cell, CombineMode.Intersect);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillPath(fill, path);
                graphics.DrawPath(edge, path);
                graphics.Restore(state);
            }
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            // Some versions of comctl32 return the entire row for the first subitem.
            var cell = e.Bounds;
            if (e.ColumnIndex == 0) cell.Width = Columns[0].Width;
            DrawCellBackground(e.Graphics, cell, e.Item.Selected);
            int left = cell.Left + Px(12);
            int size = Px(16);
            var icon = new Rectangle(left, cell.Top + (cell.Height - size) / 2, size, size);
            Color textColor = Theme.Text;
            if (e.ColumnIndex == 0) Theme.DrawGlyph(e.Graphics, Glyph.File, icon, Theme.Accent);
            else textColor = DrawStatus(e.Graphics, icon, e.SubItem.Tag as string);
            var text = Rectangle.FromLTRB(icon.Right + Px(7), cell.Top, cell.Right - Px(5), cell.Bottom);
            if (text.Width > 0) TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, text, textColor, CellFlags);
        }

        Color DrawStatus(Graphics graphics, Rectangle bounds, string status)
        {
            if (status == "queue.done")
            {
                var state = graphics.Save();
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var fill = new SolidBrush(Theme.Green)) graphics.FillEllipse(fill, bounds);
                bounds.Inflate(-Px(2), -Px(2));
                Theme.DrawGlyph(graphics, Glyph.Check, bounds, Theme.Stage);
                graphics.Restore(state);
                return Theme.Green;
            }
            bool review = status == "queue.review";
            Color color = review ? Color.FromArgb(249, 193, 102)
                : status == "queue.failed" ? Color.FromArgb(250, 139, 137)
                : status == "queue.running" ? Theme.Accent : Theme.Quiet;
            if (review || status == "queue.failed") Theme.DrawGlyph(graphics, Glyph.Warning, bounds, color);
            else if (status == "queue.cancelled") Theme.DrawGlyph(graphics, Glyph.Close, bounds, color);
            else
            {
                var state = graphics.Save();
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                bounds.Inflate(-Px(2), -Px(2));
                using (var pen = new Pen(color, Math.Max(1, Px(1))))
                {
                    graphics.DrawEllipse(pen, bounds);
                    float x = bounds.Left + bounds.Width / 2f, y = bounds.Top + bounds.Height / 2f;
                    graphics.DrawLines(pen, new[] { new PointF(x, y - bounds.Height * .28f),
                        new PointF(x, y), new PointF(x + bounds.Width * .24f, y + bounds.Height * .12f) });
                }
                graphics.Restore(state);
            }
            return color;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (viewport != null) viewport.ClientSizeChanged -= ViewportResized;
                viewport = null;
                SmallImageList = null;
                rowMetrics.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public sealed class ComparisonCanvas : Control
    {
        string emptyText;
        Bitmap before, after, difference;
        string loadedFolder;
        bool loadedWholeBody, heat;
        int split = 50;

        public ComparisonCanvas()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Theme.Stage;
            AccessibleRole = AccessibleRole.Graphic;
            Margin = Padding.Empty;
            ApplyLanguage();
        }

        public void ApplyLanguage()
        {
            emptyText = L.T("chrome.emptyPreview");
            AccessibleName = L.T("chrome.preview");
            if (difference != null) VisualResult.LocalizePreview(difference, before.Height);
            Invalidate();
        }

        public int Split
        {
            get { return split; }
            set
            {
                int position = Math.Max(0, Math.Min(100, value));
                if (position == split) return;
                split = position;
                if (!heat && before != null) Invalidate();
            }
        }

        public bool Heat
        {
            get { return heat; }
            set { if (heat != value) { heat = value; Invalidate(); } }
        }

        static Bitmap LoadDetached(string file)
        {
            using (var stream = File.OpenRead(DiskPath.Full(file)))
            using (var image = Image.FromStream(stream)) return new Bitmap(image);
        }

        public void SetFolder(string folder, bool wholeBody = false)
        {
            if (String.Equals(folder, loadedFolder, StringComparison.OrdinalIgnoreCase) &&
                (folder == null || wholeBody == loadedWholeBody)) return;
            ClearImages();
            Invalidate();
            if (folder == null) return;
            string prefix = wholeBody ? "corpo-" : "";
            try
            {
                before = LoadDetached(Path.Combine(folder, prefix + "antes.png"));
                after = LoadDetached(Path.Combine(folder, prefix + "depois.png"));
                difference = LoadDetached(Path.Combine(folder, prefix + "diferencas.png"));
                VisualResult.LocalizePreview(difference, before.Height);
                loadedFolder = folder;
                loadedWholeBody = wholeBody;
            }
            catch { ClearImages(); throw; }
        }

        void ClearImages()
        {
            if (before != null) before.Dispose();
            if (after != null) after.Dispose();
            if (difference != null) difference.Dispose();
            before = after = difference = null;
            loadedFolder = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ClearImages();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width < 1 || ClientSize.Height < 1) return;
            Image image = heat ? difference : before;
            if (image == null) { DrawPlaceholder(e.Graphics); return; }
            double scale = Math.Min((double)Width / image.Width, (double)Height / image.Height);
            int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
            var dest = new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
            var graphics = e.Graphics;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(!heat && split == 0 ? after : image, dest);
            if (heat || split == 0 || split == 100) return;
            // Split is the percentage of the original left visible. Advancing the
            // slider towards Otimizado reveals more of the optimized frame.
            int x = dest.Left + w * (100 - split) / 100;
            var state = graphics.Save();
            graphics.SetClip(Rectangle.FromLTRB(dest.Left, dest.Top, x, dest.Bottom), CombineMode.Intersect);
            graphics.DrawImage(after, dest);
            graphics.Restore(state);
            using (var pen = new Pen(Theme.Accent, Math.Max(1f, graphics.DpiX / 72f)))
                graphics.DrawLine(pen, x, dest.Top, x, dest.Bottom - 1);
        }

        void DrawPlaceholder(Graphics graphics)
        {
            float scale = graphics.DpiX / 96f;
            int padding = (int)Math.Round(12 * scale), gap = (int)Math.Round(12 * scale);
            int width = Math.Max(1, Width - padding * 2);
            const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak |
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int textHeight = TextRenderer.MeasureText(graphics, emptyText, Font, new Size(width, 0), flags).Height;
            int iconSize = Math.Min((int)(60 * scale), Math.Min(width / 2, Height - textHeight - padding * 2 - gap));
            if (iconSize < 22 * scale) { iconSize = 0; gap = 0; }
            int y = (Height - iconSize - gap - textHeight) / 2;
            if (iconSize > 0)
            {
                var state = graphics.Save();
                graphics.TranslateTransform(Width / 2f, y + iconSize / 2f);
                graphics.RotateTransform(-6);
                var card = new Rectangle(-iconSize / 2, -iconSize / 3, iconSize * 4 / 5, iconSize * 3 / 4);
                using (var edge = new Pen(Color.FromArgb(52, 71, 91), Math.Max(1f, 2.4f * scale)))
                using (var path = Theme.Rounded(card, 5 * scale))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.DrawPath(edge, path);
                }
                graphics.RotateTransform(13);
                var front = new Rectangle(-iconSize * 7 / 16, -iconSize / 2, iconSize, iconSize);
                var cover = Rectangle.Inflate(front, -iconSize / 10, -iconSize / 7);
                using (var fill = new SolidBrush(BackColor)) graphics.FillRectangle(fill, cover);
                Theme.DrawGlyph(graphics, Glyph.Image, front, Color.FromArgb(73, 94, 117));
                graphics.Restore(state);
            }
            var text = new Rectangle(padding, y + iconSize + gap, width, textHeight);
            TextRenderer.DrawText(graphics, emptyText, Font, text, Theme.Quiet, flags | Theme.PaintTextFlags);
        }
    }

    sealed class ComparisonSlider : TrackBar
    {
        public ComparisonSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            Minimum = 0;
            Maximum = 100;
            Value = 50;
            TickStyle = TickStyle.None;
            BackColor = Theme.Card;
            ApplyLanguage();
        }

        public void ApplyLanguage() { AccessibleName = L.T("chrome.split"); }

        int Radius { get { return Math.Max(1, Math.Min(Math.Min(Width, Height) / 4, DeviceDpi / 12)); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int r = Radius, left = r + 2, right = Width - r - 3, y = Height / 2;
            if (right <= left || Height < 3) return;
            float fraction = (float)(Value - Minimum) / Math.Max(1, Maximum - Minimum);
            float x = left + fraction * (right - left);
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var rail = new Pen(Theme.Line, Math.Max(2f, r * .6f)))
            using (var paint = new LinearGradientBrush(new Point(left, y), new Point(right, y),
                Enabled ? Theme.Accent : Color.FromArgb(55, 112, 168), Color.FromArgb(170, 193, 216)))
            using (var active = new Pen(paint, rail.Width))
            using (var thumb = new SolidBrush(Enabled ? Theme.Text : Color.FromArgb(173, 190, 208)))
            {
                rail.StartCap = rail.EndCap = active.StartCap = active.EndCap = LineCap.Round;
                graphics.DrawLine(rail, left, y, right, y);
                if (x > left) graphics.DrawLine(active, left, y, x, y);
                graphics.FillEllipse(thumb, x - r, y - r, r * 2, r * 2);
                if (Focused && ShowFocusCues)
                    using (var ring = new Pen(Theme.Accent)) graphics.DrawEllipse(ring, x - r - 2, y - r - 2, r * 2 + 4, r * 2 + 4);
            }
        }

        protected override void OnValueChanged(EventArgs e) { base.OnValueChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        void MoveThumb(int x)
        {
            int left = Radius + 2, length = Math.Max(1, Width - 2 * Radius - 5);
            double ratio = Math.Max(0, Math.Min(1, (double)(x - left) / length));
            int next = Minimum + (int)Math.Round(ratio * (Maximum - Minimum));
            if (next == Value) return;
            Value = next;
            OnScroll(EventArgs.Empty);
        }

        protected override void WndProc(ref Message m)
        {
            const int MouseDown = 0x0201, MouseUp = 0x0202, MouseMove = 0x0200;
            // Keep the trackbar's native keyboard and accessibility behavior. Mouse input
            // follows the painted rail, including a direct jump when clicking either end.
            if (Enabled && (m.Msg == MouseDown || (Capture && (m.Msg == MouseMove || m.Msg == MouseUp))))
            {
                if (m.Msg == MouseDown) { Focus(); Capture = true; }
                MoveThumb(unchecked((short)m.LParam.ToInt64()));
                if (m.Msg == MouseUp) Capture = false;
                return;
            }
            base.WndProc(ref m);
        }
    }
}
