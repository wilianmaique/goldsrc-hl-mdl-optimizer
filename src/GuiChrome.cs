using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MdlOptimizer
{
    public class ChromeForm : Form
    {
        const int WmGetMinMaxInfo = 0x0024, WmStyleChanging = 0x007c, GwlStyle = -16;
        const int WmNcCalcSize = 0x0083, WmNcHitTest = 0x0084, WmNcPaint = 0x0085, WmNcActivate = 0x0086;
        const int WsThickFrame = 0x00040000, WsSysMenu = 0x00080000, WsMinimizeBox = 0x00020000, WsMaximizeBox = 0x00010000;
        bool creatingWindow;

        public ChromeForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            Padding = new Padding(Theme.Scale(this, 4));
        }

        protected override CreateParams CreateParams
        {
            get {
                var cp = base.CreateParams;
                // The HWND needs a sizing frame for native drag/resize and the
                // system menu. WinForms must calculate client and restore sizes
                // as borderless because the frame has no non-client pixels.
                if (creatingWindow) cp.Style |= WsThickFrame;
                cp.Style |= WsSysMenu | WsMinimizeBox | WsMaximizeBox;
                return cp;
            }
        }

        protected override void CreateHandle()
        {
            creatingWindow = true;
            try { base.CreateHandle(); }
            finally { creatingWindow = false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct StyleChange { public int OldStyle, NewStyle; }

        [StructLayout(LayoutKind.Sequential)]
        struct MinMaxInfo
        {
            public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmStyleChanging && message.WParam == (IntPtr)GwlStyle) {
                var style = (StyleChange)Marshal.PtrToStructure(message.LParam, typeof(StyleChange));
                style.NewStyle |= WsThickFrame;
                Marshal.StructureToPtr(style, message.LParam, false);
            }
            // The full frame is drawn in the client area. Opening a native dialog
            // otherwise repaints the standard sizing border over the dark client.
            if (message.Msg == WmNcPaint) {
                message.Result = IntPtr.Zero;
                return;
            }
            if (message.Msg == WmNcActivate) message.LParam = new IntPtr(-1);
            if (message.Msg == WmGetMinMaxInfo) {
                base.WndProc(ref message);
                var monitor = Screen.FromHandle(message.HWnd);
                var area = monitor.WorkingArea;
                var sizes = (MinMaxInfo)Marshal.PtrToStructure(message.LParam, typeof(MinMaxInfo));
                sizes.MaxPosition = new Point(area.Left - monitor.Bounds.Left, area.Top - monitor.Bounds.Top);
                sizes.MaxSize = new Point(area.Width, area.Height);
                Marshal.StructureToPtr(sizes, message.LParam, false);
                return;
            }
            if (message.Msg == WmNcCalcSize) {
                message.Result = IntPtr.Zero;
                return;
            }
            if (message.Msg == WmNcHitTest && WindowState == FormWindowState.Normal) {
                int packed = unchecked((int)message.LParam.ToInt64());
                Point p = PointToClient(new Point((short)packed, (short)(packed >> 16)));
                bool left = p.X < Padding.Left, right = p.X >= ClientSize.Width - Padding.Right;
                bool top = p.Y < Padding.Top, bottom = p.Y >= ClientSize.Height - Padding.Bottom;
                int hit = top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) :
                    left ? 10 : right ? 11 : 1;
                message.Result = (IntPtr)hit;
                return;
            }
            base.WndProc(ref message);
        }
    }

    sealed class AppHeader : Panel
    {
        const int WmNcHitTest = 0x0084, HtCaption = 2;
        const int WmNcLeftDown = 0x00A1, WmNcLeftDoubleClick = 0x00A3, WmNcRightUp = 0x00A5;
        const string Title = "MDL Optimizer";
        const TextFormatFlags TextFlags = Theme.PaintTextFlags | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        readonly Font titleFont = Theme.Font(18f, FontStyle.Bold);
        readonly Font detailFont = Theme.Font(10f);
        readonly CaptionButton minimize, maximize, close;
        readonly ToolTip hints = new ToolTip();
        readonly Label languageLabel = new Label();
        readonly LanguageSelector languages = new LanguageSelector("", "Português (Brasil)", "English", "Español", "Русский");
        static readonly string[] LanguageCodes = { "", "pt-BR", "en", "es", "ru" };
        bool updatingLanguage;
        Form owner;

        public event EventHandler LanguageChanged;

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

        public AppHeader()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            Margin = Padding.Empty;
            Dock = DockStyle.Fill;
            TabStop = false;
            BackColor = CaptionButton.Background;
            AccessibleRole = AccessibleRole.TitleBar;
            AccessibleName = Title;
            languageLabel.AutoSize = true;
            languageLabel.Font = detailFont;
            languageLabel.ForeColor = Theme.Quiet;
            languages.Name = "languageSelector";
            languages.Font = detailFont;
            languages.SelectedIndexChanged += SelectLanguage;
            minimize = new CaptionButton(CaptionAction.Minimize);
            maximize = new CaptionButton(CaptionAction.Maximize);
            close = new CaptionButton(CaptionAction.Close);
            minimize.Click += delegate { if (owner != null) owner.WindowState = FormWindowState.Minimized; };
            maximize.Click += delegate {
                if (owner != null) owner.WindowState = owner.WindowState == FormWindowState.Maximized ?
                    FormWindowState.Normal : FormWindowState.Maximized;
            };
            close.Click += delegate { if (owner != null) owner.Close(); };
            Controls.AddRange(new Control[] { languageLabel, languages, minimize, maximize, close });
            ApplyLanguage();
        }

        void SelectLanguage(object sender, EventArgs args)
        {
            if (updatingLanguage || languages.SelectedIndex < 0) return;
            string code = LanguageCodes[languages.SelectedIndex];
            if (code == L.Preference) return;
            bool saved = L.SetPreference(code);
            ApplyLanguage();
            if (LanguageChanged != null) LanguageChanged(this, EventArgs.Empty);
            if (!saved) MessageBox.Show(this, L.T("chrome.sessionOnly"), Title,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public void ApplyLanguage()
        {
            updatingLanguage = true;
            try {
                AccessibleDescription = L.T("chrome.subtitle");
                languages.AccessibleName = languageLabel.Text = L.T("chrome.language");
                string systemLabel = L.T("chrome.systemLanguage");
                languages.SetSystemLabel(systemLabel);
                languages.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageCodes, L.Preference));
                minimize.AccessibleName = L.T("chrome.minimize");
                close.AccessibleName = L.T("chrome.close");
                hints.SetToolTip(minimize, minimize.AccessibleName);
                hints.SetToolTip(close, close.AccessibleName);
                UpdateWindowState(this, EventArgs.Empty);
            }
            finally { updatingLanguage = false; }
            PerformLayout();
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            owner = FindForm();
            if (owner != null) owner.Resize += UpdateWindowState;
            UpdateWindowState(this, EventArgs.Empty);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (owner != null) owner.Resize -= UpdateWindowState;
            owner = null;
            base.OnHandleDestroyed(e);
        }

        void UpdateWindowState(object sender, EventArgs args)
        {
            bool restoredIcon = owner != null && owner.WindowState == FormWindowState.Maximized;
            maximize.Restore = restoredIcon;
            string name = L.T(restoredIcon ? "chrome.restore" : "chrome.maximize");
            maximize.AccessibleName = name;
            hints.SetToolTip(maximize, name);
            maximize.Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (minimize == null || close == null) return;
            int width = Theme.Scale(this, 45);
            int right = ClientSize.Width - Theme.Scale(this, 7);
            int height = Math.Max(0, ClientSize.Height - 1);
            close.SetBounds(right - width, 0, width, height);
            maximize.SetBounds(right - width * 2, 0, width, height);
            minimize.SetBounds(right - width * 3, 0, width, height);
            int gap = Theme.Scale(this, 8);
            Size choice = languages.GetPreferredSize(Size.Empty);
            languages.SetBounds(minimize.Left - gap * 2 - choice.Width, (height - choice.Height) / 2,
                choice.Width, choice.Height);
            languageLabel.Location = new Point(languages.Left - gap - languageLabel.Width, (height - languageLabel.Height) / 2);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmNcHitTest) {
                m.Result = (IntPtr)HtCaption;
                return;
            }
            if (owner != null && (m.Msg == WmNcLeftDown || m.Msg == WmNcLeftDoubleClick || m.Msg == WmNcRightUp)) {
                m.Result = SendMessage(owner.Handle, m.Msg, (IntPtr)HtCaption, m.LParam);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int h = ClientSize.Height;
            if (h < 4 || ClientSize.Width < 4) return;
            float scale = g.DpiX / 96f;
            int pad = (int)Math.Round(14 * scale), inset = (int)Math.Round(25 * scale);
            int logoH = Math.Min((int)Math.Round(40 * scale), h - 2);
            var logo = new Rectangle(inset, (h - logoH) / 2, (int)Math.Round(45 * scale), logoH);
            using (var tile = Theme.Rounded(logo, 12 * scale))
            using (var brush = new LinearGradientBrush(logo, Color.FromArgb(15, 44, 74), Color.FromArgb(10, 30, 53), 70))
                g.FillPath(brush, tile);
            int glyph = Math.Min(logoH - 2, (int)Math.Round(30 * scale));
            Theme.DrawGlyph(g, Glyph.Layers, new Rectangle(logo.Left + (logo.Width - glyph) / 2,
                logo.Top + (logo.Height - glyph) / 2, glyph, glyph), Theme.Accent);

            int titleWidth = Measure(g, Title, titleFont);
            var title = new Rectangle(logo.Right + pad, 0, titleWidth, h);
            TextRenderer.DrawText(g, Title, titleFont, title, Theme.Text, TextFlags);
            var subtitle = AccessibleDescription;
            var caption = new Rectangle(title.Right + 2 * pad, 0, Measure(g, subtitle, detailFont), h);
            if (caption.Right + pad <= languageLabel.Left)
                TextRenderer.DrawText(g, subtitle, detailFont, caption, Theme.Quiet, TextFlags);
        }

        static int Measure(Graphics graphics, string text, Font font)
        {
            return TextRenderer.MeasureText(graphics, text, font, new Size(int.MaxValue, int.MaxValue), TextFlags).Width;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) hints.Dispose();
            base.Dispose(disposing);
        }
    }

    enum CaptionAction { Minimize, Maximize, Close }

    sealed class CaptionButton : Button
    {
        internal static readonly Color Background = Color.FromArgb(12, 21, 32);
        readonly CaptionAction action;
        bool hovered;
        public bool Restore { get; set; }

        public CaptionButton(CaptionAction action)
        {
            this.action = action;
            TabStop = false;
            FlatStyle = FlatStyle.Flat;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Background;
            Margin = Padding.Empty;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color surface = hovered ? (action == CaptionAction.Close ? Color.FromArgb(184, 44, 58) : Color.FromArgb(30, 47, 67)) : Background;
            using (var brush = new SolidBrush(surface)) g.FillRectangle(brush, ClientRectangle);
            float unit = g.DpiX / 96f;
            float size = (float)Math.Round(11 * unit), x = (float)Math.Round((Width - size) / 2);
            float y = (float)Math.Round((Height - size) / 2);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Theme.Text, Math.Max(1f, 1.35f * unit))) {
                if (action == CaptionAction.Minimize) {
                    g.DrawLine(pen, x, y + size / 2, x + size, y + size / 2);
                } else if (action == CaptionAction.Close) {
                    g.DrawLine(pen, x, y, x + size, y + size);
                    g.DrawLine(pen, x + size, y, x, y + size);
                } else if (Restore) {
                    float offset = 2.5f * unit;
                    g.DrawLines(pen, new [] { new PointF(x + offset, y + offset), new PointF(x + offset, y),
                        new PointF(x + size, y), new PointF(x + size, y + size - offset), new PointF(x + size - offset, y + size - offset) });
                    g.DrawRectangle(pen, x, y + offset, size - offset, size - offset);
                } else {
                    g.DrawRectangle(pen, x, y, size, size);
                }
            }
            if (Focused && ShowFocusCues) Theme.DrawFocus(g, Rectangle.Inflate(ClientRectangle, -4, -4), Theme.Quiet);
        }
    }
}
