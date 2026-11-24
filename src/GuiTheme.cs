using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MdlOptimizer
{
    enum Glyph { Layers, Image, File, Folder, FolderOpen, External, Plus, Broom, Play, Close, Shield, Info, Check, Warning, Globe, ChevronDown, ChevronUp }
    enum ButtonTone { Neutral, Primary, Success }

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(8, 15, 23);
        public static readonly Color Card = Color.FromArgb(16, 27, 38);
        public static readonly Color Stage = Color.FromArgb(13, 22, 33);
        public static readonly Color Line = Color.FromArgb(47, 66, 86);
        public static readonly Color Text = Color.FromArgb(243, 246, 251);
        public static readonly Color Quiet = Color.FromArgb(181, 196, 213);
        public static readonly Color Accent = Color.FromArgb(29, 143, 255);
        public static readonly Color Green = Color.FromArgb(44, 217, 119);
        public static readonly Color Muted = Color.FromArgb(132, 151, 174);
        internal const TextFormatFlags PaintTextFlags =
            TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
        internal const ControlStyles OpaquePainting = ControlStyles.UserPaint | ControlStyles.Opaque |
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw;
        static readonly Dictionary<Tuple<float, FontStyle>, Font> fonts = new Dictionary<Tuple<float, FontStyle>, Font>();

        static Theme()
        {
            Application.ApplicationExit += delegate {
                foreach (var font in fonts.Values) font.Dispose();
                fonts.Clear();
            };
        }

        public static Font Font(float size, FontStyle style = FontStyle.Regular)
        {
            var key = Tuple.Create(size, style);
            Font font;
            if (!fonts.TryGetValue(key, out font)) {
                font = new Font("Segoe UI", size, style);
                fonts.Add(key, font);
            }
            return font;
        }

        internal static int Scale(Control control, int pixels)
        {
            return (int)Math.Round(pixels * control.DeviceDpi / 96.0);
        }

        internal static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            if (diameter < 1) {
                path.AddRectangle(bounds);
                return path;
            }
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        static PointF[] Points(params float[] coordinates)
        {
            var points = new PointF[coordinates.Length / 2];
            for (int i = 0; i < points.Length; i++)
                points[i] = new PointF(coordinates[i * 2], coordinates[i * 2 + 1]);
            return points;
        }

        public static void DrawGlyph(Graphics graphics, Glyph glyph, Rectangle bounds, Color color)
        {
            float side = Math.Min(bounds.Width, bounds.Height);
            if (side < 1) return;
            var state = graphics.Save();
            try {
                graphics.TranslateTransform(bounds.X + (bounds.Width - side) / 2, bounds.Y + (bounds.Height - side) / 2);
                graphics.ScaleTransform(side / 24, side / 24);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(color, 1.85f))
                using (var brush = new SolidBrush(color)) {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    switch (glyph) {
                        case Glyph.Globe:
                            graphics.DrawEllipse(pen, 2.5f, 2.5f, 19, 19);
                            graphics.DrawEllipse(pen, 7.5f, 2.5f, 9, 19);
                            graphics.DrawLine(pen, 3, 12, 21, 12);
                            break;
                        case Glyph.ChevronDown:
                            graphics.DrawLines(pen, Points(5, 9, 12, 16, 19, 9));
                            break;
                        case Glyph.ChevronUp:
                            graphics.DrawLines(pen, Points(5, 15, 12, 8, 19, 15));
                            break;
                        case Glyph.Layers:
                            graphics.FillPolygon(brush, Points(12, 1, 23, 7, 12, 13, 1, 7));
                            pen.Width = 2.35f;
                            graphics.DrawLines(pen, Points(2, 12, 12, 17.4f, 22, 12));
                            graphics.DrawLines(pen, Points(2, 17, 12, 22.4f, 22, 17));
                            break;
                        case Glyph.Image:
                            using (var frame = Rounded(new RectangleF(2, 3, 20, 17.5f), 2.1f))
                                graphics.DrawPath(pen, frame);
                            graphics.FillEllipse(brush, 6, 6.5f, 3.7f, 3.7f);
                            graphics.DrawLines(pen, Points(3.2f, 16.5f, 8.2f, 11.8f, 11.3f, 14.6f, 15.8f, 10, 21.2f, 15.5f));
                            break;
                        case Glyph.File:
                            graphics.DrawPolygon(pen, Points(5, 2.5f, 14, 2.5f, 19.5f, 8.2f, 19.5f, 21.2f, 5, 21.2f));
                            graphics.DrawLines(pen, Points(13.8f, 3, 13.8f, 8.4f, 19, 8.4f));
                            graphics.DrawLine(pen, 8.5f, 13, 15.5f, 13);
                            graphics.DrawLine(pen, 8.5f, 16.7f, 12.4f, 16.7f);
                            break;
                        case Glyph.Folder:
                        case Glyph.FolderOpen:
                            graphics.DrawPolygon(pen, Points(2, 6, 9, 6, 11.5f, 8.7f, 20, 8.7f, 20, 18.8f, 2, 18.8f));
                            var flap = Points(3, 19, 5.9f, 11, 22, 11, 19.3f, 19);
                            if (glyph == Glyph.Folder) graphics.FillPolygon(brush, flap);
                            else graphics.DrawPolygon(pen, flap);
                            break;
                        case Glyph.External:
                            graphics.DrawLines(pen, Points(11, 4, 3.5f, 4, 3.5f, 20.5f, 20, 20.5f, 20, 13.5f));
                            graphics.DrawLines(pen, Points(14.5f, 3.5f, 20.5f, 3.5f, 20.5f, 9.5f));
                            graphics.DrawLine(pen, 10, 14, 20, 4);
                            break;
                        case Glyph.Plus:
                            graphics.DrawLine(pen, 12, 3, 12, 21);
                            graphics.DrawLine(pen, 3, 12, 21, 12);
                            break;
                        case Glyph.Broom:
                            graphics.DrawLine(pen, 19.7f, 2.7f, 12.6f, 11);
                            graphics.DrawPolygon(pen, Points(9.3f, 9.4f, 15.1f, 14.4f, 12.3f, 20.4f, 9.6f, 21, 3.3f, 15.6f, 3, 13.8f));
                            graphics.DrawLine(pen, 8, 12, 13.5f, 16.7f);
                            break;
                        case Glyph.Play:
                            graphics.FillPolygon(brush, Points(5, 2.8f, 21, 12, 5, 21.2f));
                            break;
                        case Glyph.Close:
                            graphics.DrawLine(pen, 5.5f, 5.5f, 18.5f, 18.5f);
                            graphics.DrawLine(pen, 18.5f, 5.5f, 5.5f, 18.5f);
                            break;
                        case Glyph.Shield:
                            using (var shield = new GraphicsPath()) {
                                shield.AddLines(Points(12, 2, 21, 5.5f, 21, 11.1f));
                                shield.AddBezier(21, 11.1f, 21, 16, 17, 20, 12, 22);
                                shield.AddBezier(12, 22, 7, 20, 3, 16, 3, 11.1f);
                                shield.AddLine(3, 11.1f, 3, 5.5f);
                                shield.CloseFigure();
                                graphics.DrawPath(pen, shield);
                                var cut = graphics.Save();
                                graphics.SetClip(new RectangleF(0, 0, 12, 24), CombineMode.Intersect);
                                graphics.FillPath(brush, shield);
                                graphics.Restore(cut);
                            }
                            break;
                        case Glyph.Info:
                            graphics.DrawEllipse(pen, 2.5f, 2.5f, 19, 19);
                            graphics.FillEllipse(brush, 10.8f, 6.5f, 2.4f, 2.4f);
                            graphics.DrawLine(pen, 12, 11.5f, 12, 17.3f);
                            break;
                        case Glyph.Check:
                            pen.Width = 2.8f;
                            graphics.DrawLines(pen, Points(4.8f, 12, 9.5f, 16.5f, 19.3f, 6.8f));
                            break;
                        case Glyph.Warning:
                            graphics.DrawPolygon(pen, Points(12, 3, 22, 20.5f, 2, 20.5f));
                            graphics.DrawLine(pen, 12, 9, 12, 13);
                            graphics.FillEllipse(brush, 10.8f, 15.8f, 2.4f, 2.4f);
                            break;
                    }
                }
            } finally { graphics.Restore(state); }
        }

        internal static Color ParentColor(Control control)
        {
            for (Control parent = control.Parent; parent != null; parent = parent.Parent)
                if (parent.BackColor.A == 255) return parent.BackColor;
            return Bg;
        }

        internal static void FillBackground(Graphics graphics, Control control)
        {
            // A buffered paint or WM_PRINTCLIENT can share a translated, clipped destination.
            using (var brush = new SolidBrush(ParentColor(control)))
                graphics.FillRectangle(brush, control.ClientRectangle);
        }

        internal static void DrawFocus(Graphics graphics, Rectangle bounds, Color color)
        {
            if (bounds.Width < 2 || bounds.Height < 2) return;
            using (var pen = new Pen(color) { DashStyle = DashStyle.Dot })
                graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        }
    }

    sealed class Surface : Panel
    {
        Color border;
        readonly int radius;

        public Color BorderColor
        {
            get { return border; }
            set { if (border != value) { border = value; Invalidate(); } }
        }

        public Surface(Color fill, Color border, int radius)
        {
            this.border = border;
            this.radius = radius;
            BackColor = fill;
            DoubleBuffered = true;
            ResizeRedraw = true;
            Margin = Padding.Empty;
        }

        public override Rectangle DisplayRectangle
        {
            get {
                var area = base.DisplayRectangle;
                // The diagonal of a quarter-circle meets both edges at r * (1 - 1/sqrt(2)).
                // Docked rectangular children must stay inside that point and the border.
                int inset = (int)Math.Ceiling((radius * .293 + 1) * DeviceDpi / 96.0);
                int left = Math.Max(area.Left, inset), top = Math.Max(area.Top, inset);
                return Rectangle.FromLTRB(left, top, Math.Max(left, Math.Min(area.Right, Width - inset)),
                                                     Math.Max(top, Math.Min(area.Bottom, Height - inset)));
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            Theme.FillBackground(graphics, this);
            float scale = graphics.DpiX / 96f;
            if (Width <= scale || Height <= scale) return;
            var bounds = new RectangleF(scale / 2, scale / 2, Width - scale, Height - scale);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(bounds, radius * scale))
            using (var brush = new SolidBrush(BackColor)) {
                graphics.FillPath(brush, path);
                if (!border.IsEmpty)
                    using (var pen = new Pen(border, scale)) graphics.DrawPath(pen, path);
            }
        }
    }

    sealed class ActionButton : Button
    {
        readonly Glyph glyph;
        readonly ButtonTone tone;
        bool hot, spaceDown;

        public ActionButton(string text, Glyph icon, ButtonTone tone)
        {
            this.glyph = icon;
            this.tone = tone;
            Text = text;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(Theme.OpaquePainting, true);
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ForeColor = tone == ButtonTone.Success ? Color.FromArgb(4, 25, 22) : Theme.Text;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            return new Size(text.Width + Theme.Scale(this, 55) + Padding.Horizontal,
                            Math.Max(Theme.Scale(this, 36), text.Height + Theme.Scale(this, 12)) + Padding.Vertical);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space && !spaceDown) { spaceDown = true; Invalidate(); }
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space) { spaceDown = false; Invalidate(); }
        }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); spaceDown = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            Theme.FillBackground(graphics, this);
            float scale = graphics.DpiX / 96f;
            if (Width <= scale || Height <= scale) return;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color top, bottom, edge;
            if (!Enabled) {
                top = Color.FromArgb(34, 48, 63);
                bottom = Color.FromArgb(28, 39, 52);
                edge = Color.FromArgb(59, 77, 98);
            } else if (tone == ButtonTone.Primary) {
                top = Color.FromArgb(22, 131, 251);
                bottom = Color.FromArgb(9, 94, 205);
                edge = Color.FromArgb(45, 154, 255);
            } else if (tone == ButtonTone.Success) {
                top = Color.FromArgb(60, 227, 138);
                bottom = Color.FromArgb(15, 186, 101);
                edge = Theme.Green;
            } else {
                top = Color.FromArgb(32, 48, 66);
                bottom = Color.FromArgb(24, 36, 50);
                edge = Color.FromArgb(77, 101, 126);
            }
            if (Enabled && hot) {
                top = ControlPaint.Light(top, .12f);
                bottom = ControlPaint.Light(bottom, .12f);
                edge = ControlPaint.Light(edge, .20f);
            }
            if (Enabled && (spaceDown || (hot && Capture && MouseButtons == MouseButtons.Left))) {
                top = ControlPaint.Dark(top, .12f);
                bottom = ControlPaint.Dark(bottom, .12f);
            }
            var bounds = new RectangleF(scale / 2, scale / 2, Width - scale, Height - scale);
            using (var shape = Theme.Rounded(bounds, 7 * scale))
            using (var fill = new LinearGradientBrush(bounds, top, bottom, LinearGradientMode.Vertical))
            using (var outline = new Pen(edge, scale)) {
                graphics.FillPath(fill, shape);
                graphics.DrawPath(outline, shape);
            }
            const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
                                          TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            int icon = Theme.Scale(this, 16), gap = Theme.Scale(this, 8);
            int textWidth = TextRenderer.MeasureText(graphics, Text, Font, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
            int left = Math.Max(Theme.Scale(this, 6), (Width - icon - gap - textWidth) / 2);
            Color ink = Enabled ? ForeColor : Theme.Muted;
            Theme.DrawGlyph(graphics, glyph, new Rectangle(left, (Height - icon) / 2, icon, icon), ink);
            int textLeft = left + icon + gap;
            var textBounds = new Rectangle(textLeft, 0, Math.Max(0, Width - textLeft - Theme.Scale(this, 4)), Height);
            TextRenderer.DrawText(graphics, Text, Font, textBounds, ink, flags | Theme.PaintTextFlags);
            if (Focused && ShowFocusCues) {
                var focus = Rectangle.Inflate(ClientRectangle, -Theme.Scale(this, 4), -Theme.Scale(this, 4));
                Theme.DrawFocus(graphics, focus, ink);
            }
        }
    }

    static class ChoicePainter
    {
        public static Size Preferred(ButtonBase control)
        {
            var text = TextRenderer.MeasureText(control.Text, control.Font, Size.Empty,
                                                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            return new Size(text.Width + Theme.Scale(control, 29), Math.Max(text.Height + 2, Theme.Scale(control, 24)));
        }

        public static void Draw(Graphics graphics, ButtonBase control, bool radio, bool selected, bool showFocus)
        {
            Theme.FillBackground(graphics, control);
            int size = Theme.Scale(control, 17), x = Theme.Scale(control, 2);
            int y = (control.Height - size) / 2;
            float scale = graphics.DpiX / 96f;
            var bounds = new RectangleF(x + scale / 2, y + scale / 2, size - scale, size - scale);
            bool enabled = control.Enabled;
            bool hover = enabled && control.ClientRectangle.Contains(control.PointToClient(Control.MousePosition));
            Color edge = selected && enabled ? Theme.Accent : Theme.Muted;
            if (hover) edge = selected ? Color.FromArgb(75, 169, 255) : Theme.Quiet;
            Color inside = selected ? (enabled ? Color.FromArgb(12, 117, 233) : Theme.Line) : Theme.Stage;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = new GraphicsPath())
            using (var brush = new SolidBrush(inside))
            using (var pen = new Pen(edge, scale * 1.35f)) {
                if (radio) path.AddEllipse(bounds);
                else using (var square = Theme.Rounded(bounds, scale * 3)) path.AddPath(square, false);
                graphics.FillPath(brush, path);
                graphics.DrawPath(pen, path);
            }
            if (selected) {
                Color mark = enabled ? Theme.Text : Theme.Quiet;
                if (radio) {
                    var dot = bounds;
                    dot.Inflate(-size * .29f, -size * .29f);
                    using (var brush = new SolidBrush(mark)) graphics.FillEllipse(brush, dot);
                } else {
                    Theme.DrawGlyph(graphics, Glyph.Check, new Rectangle(x, y, size, size), mark);
                }
            }
            int textLeft = x + size + Theme.Scale(control, 7);
            var textBounds = new Rectangle(textLeft, 0, Math.Max(0, control.Width - textLeft), control.Height);
            TextRenderer.DrawText(graphics, control.Text, control.Font, textBounds, enabled ? Theme.Text : Theme.Muted,
                Theme.PaintTextFlags | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (showFocus) Theme.DrawFocus(graphics, textBounds, Theme.Quiet);
        }
    }

    sealed class ThemedCheck : CheckBox
    {
        public ThemedCheck()
        {
            AutoSize = true;
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            SetStyle(Theme.OpaquePainting, true);
            Cursor = Cursors.Hand;
        }
        public override Size GetPreferredSize(Size proposedSize) { return ChoicePainter.Preferred(this); }
        protected override void OnPaint(PaintEventArgs e) { ChoicePainter.Draw(e.Graphics, this, false, Checked, Focused && ShowFocusCues); }
    }

    sealed class ThemedRadio : RadioButton
    {
        public ThemedRadio()
        {
            AutoSize = true;
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            SetStyle(Theme.OpaquePainting, true);
            Cursor = Cursors.Hand;
        }
        public override Size GetPreferredSize(Size proposedSize) { return ChoicePainter.Preferred(this); }
        protected override void OnPaint(PaintEventArgs e) { ChoicePainter.Draw(e.Graphics, this, true, Checked, Focused && ShowFocusCues); }
    }

    sealed class ThemedProgress : ProgressBar
    {
        public ThemedProgress()
        {
            SetStyle(Theme.OpaquePainting, true);
            ForeColor = Theme.Green;
            ApplyLanguage();
        }

        public void ApplyLanguage() { AccessibleName = L.T("chrome.progress"); }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            // The native progress control changes its value and range via PBM_* messages.
            if (message.Msg >= 0x401 && message.Msg <= 0x406) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            Theme.FillBackground(graphics, this);
            if (Width < 1 || Height < 1) return;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(0, 0, Width, Height);
            using (var track = Theme.Rounded(bounds, Height / 2f))
            using (var brush = new SolidBrush(Theme.Line)) graphics.FillPath(brush, track);
            if (Maximum == Minimum || Value <= Minimum) return;
            bounds.Width *= (float)(Value - Minimum) / (Maximum - Minimum);
            using (var progress = Theme.Rounded(bounds, Height / 2f))
            using (var brush = new SolidBrush(ForeColor)) graphics.FillPath(brush, progress);
        }
    }
}
