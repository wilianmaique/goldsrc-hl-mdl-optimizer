using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MdlOptimizer
{
    sealed class LanguageSelector : Button
    {
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        int selectedIndex = -1;
        bool hovered, dismissingByClick;

        internal event EventHandler SelectedIndexChanged;

        public LanguageSelector(params string[] labels)
        {
            SetStyle(Theme.OpaquePainting, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Theme.Card;
            ForeColor = Theme.Text;
            AccessibleRole = AccessibleRole.ButtonDropDown;
            Cursor = Cursors.Hand;

            menu.ShowImageMargin = menu.ShowCheckMargin = false;
            menu.AutoSize = false;
            menu.BackColor = Theme.Card;
            menu.Renderer = new LanguageMenuRenderer();
            foreach (string label in labels) {
                menu.Items.Add(new ToolStripMenuItem(label) { AutoSize = false, ForeColor = Theme.Text, Margin = Padding.Empty });
            }
            menu.Opening += (s, e) => SizeMenu();
            menu.Opened += (s, e) => {
                if (selectedIndex >= 0) menu.Items[selectedIndex].Select();
                Invalidate();
            };
            menu.Closing += (s, e) => {
                // A click on the field closes the popup before the button receives the click.
                dismissingByClick = e.CloseReason == ToolStripDropDownCloseReason.AppClicked &&
                    MouseButtons == MouseButtons.Left && ClientRectangle.Contains(PointToClient(MousePosition));
            };
            menu.Closed += (s, e) => Invalidate();
            menu.ItemClicked += (s, e) => SelectedIndex = menu.Items.IndexOf(e.ClickedItem);
            ContextMenuStrip = menu;
        }

        internal int SelectedIndex
        {
            get { return selectedIndex; }
            set {
                if (value < 0 || value >= menu.Items.Count) throw new ArgumentOutOfRangeException("value");
                if (value == selectedIndex) return;
                if (selectedIndex >= 0) ((ToolStripMenuItem)menu.Items[selectedIndex]).Checked = false;
                selectedIndex = value;
                var item = (ToolStripMenuItem)menu.Items[value];
                item.Checked = true;
                AccessibleDescription = Text = item.Text;
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        internal void SetSystemLabel(string text)
        {
            menu.Items[0].Text = text;
            if (selectedIndex == 0) AccessibleDescription = Text = text;
        }

        int Px(int value) { return Theme.Scale(this, value); }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int longest = 0;
            if (menu != null) foreach (ToolStripItem item in menu.Items) {
                longest = Math.Max(longest, TextRenderer.MeasureText(item.Text, Font, Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width);
            }
            return new Size(Math.Max(Px(178), longest + Px(68)), Math.Max(Px(34), Font.Height + Px(12)));
        }

        void SizeMenu()
        {
            menu.Font = Font;
            int pad = Px(5), row = Math.Max(Px(32), Font.Height + Px(12));
            foreach (ToolStripItem item in menu.Items) {
                item.Size = new Size(Width, row);
            }
            // The menu manages its own padding. Add the remaining top inset to the first row.
            menu.Items[0].Margin = new Padding(0, Math.Max(0, pad - menu.Padding.Top), 0, 0);
            menu.Size = new Size(Width, row * menu.Items.Count + 2 * pad);
            using (var outline = Theme.Rounded(menu.ClientRectangle, Px(8)))
                menu.Region = new Region(outline);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (dismissingByClick) { dismissingByClick = false; return; }
            if (menu.Visible) menu.Close();
            else menu.Show(this, new Point(0, Height + Px(4)));
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F4 || keyData == (Keys.Alt | Keys.Down) || keyData == Keys.Enter) {
                PerformClick();
                return true;
            }
            switch (keyData) {
                case Keys.Up:   SelectedIndex = Math.Max(0, selectedIndex - 1); return true;
                case Keys.Down: SelectedIndex = Math.Min(menu.Items.Count - 1, selectedIndex + 1); return true;
                case Keys.Home: SelectedIndex = 0; return true;
                case Keys.End:  SelectedIndex = menu.Items.Count - 1; return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (!Char.IsControl(e.KeyChar) && e.KeyChar != ' ') {
                for (int n = 1; n <= menu.Items.Count; n++) {
                    int i = (selectedIndex + n) % menu.Items.Count;
                    if (!menu.Items[i].Text.StartsWith(e.KeyChar.ToString(), StringComparison.CurrentCultureIgnoreCase)) continue;
                    SelectedIndex = i;
                    e.Handled = true;
                    break;
                }
            }
            base.OnKeyPress(e);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; dismissingByClick = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.FillBackground(g, this);
            float scale = g.DpiX / 96f;
            if (Width <= scale || Height <= scale) return;
            bool open = menu.Visible, focused = Focused && ShowFocusCues;
            Color edge = open || focused ? Theme.Accent : hovered && Enabled ? Theme.Muted : Theme.Line;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(scale / 2, scale / 2, Width - scale, Height - scale);
            using (var shape = Theme.Rounded(bounds, 7 * scale))
            using (var fill = new LinearGradientBrush(bounds, hovered || open ? Color.FromArgb(34, 52, 72) : Color.FromArgb(27, 42, 58),
                                                      Theme.Card, LinearGradientMode.Vertical))
            using (var pen = new Pen(edge, scale)) {
                g.FillPath(fill, shape);
                g.DrawPath(pen, shape);
            }
            int globe = Px(17), arrow = Px(16);
            Theme.DrawGlyph(g, Glyph.Globe, new Rectangle(Px(12), (Height - globe) / 2, globe, globe), Enabled ? Theme.Accent : Theme.Muted);
            var text = Rectangle.FromLTRB(Px(38), 0, Width - Px(32), Height);
            TextRenderer.DrawText(g, Text, Font, text, Enabled ? ForeColor : Theme.Muted, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | Theme.PaintTextFlags);
            Theme.DrawGlyph(g, open ? Glyph.ChevronUp : Glyph.ChevronDown,
                new Rectangle(Width - Px(11) - arrow, (Height - arrow) / 2, arrow, arrow), open ? Theme.Accent : Theme.Quiet);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) menu.Dispose();
            base.Dispose(disposing);
        }
    }

    sealed class LanguageMenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(Theme.Card);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            float s = e.Graphics.DpiX / 96f;
            var bounds = new RectangleF(s / 2, s / 2, e.ToolStrip.Width - s, e.ToolStrip.Height - s);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shape = Theme.Rounded(bounds, 8 * s))
            using (var pen = new Pen(Theme.Line, s)) e.Graphics.DrawPath(pen, shape);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var item = (ToolStripMenuItem)e.Item;
            if (!item.Selected && !item.Checked) return;
            float s = e.Graphics.DpiX / 96f;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shape = Theme.Rounded(new RectangleF(5 * s, s, item.Width - 10 * s, item.Height - 2 * s), 5 * s))
            using (var fill = new SolidBrush(item.Selected ? Color.FromArgb(31, 58, 84) : Color.FromArgb(21, 44, 66)))
                e.Graphics.FillPath(fill, shape);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var item = (ToolStripMenuItem)e.Item;
            int pad = Theme.Scale(e.ToolStrip, 14), mark = Theme.Scale(e.ToolStrip, 15);
            var text = Rectangle.FromLTRB(pad, 0, item.Width - 2 * pad - mark, item.Height);
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, text, Theme.Text, TextFormatFlags.SingleLine |
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | Theme.PaintTextFlags);
            if (item.Checked) Theme.DrawGlyph(e.Graphics, Glyph.Check,
                new Rectangle(item.Width - pad - mark, (item.Height - mark) / 2, mark, mark), Theme.Accent);
        }
    }
}
