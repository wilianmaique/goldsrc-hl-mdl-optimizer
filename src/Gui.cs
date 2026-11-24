using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("WorkflowTests")]

namespace MdlOptimizer
{
    static class App
    {
        [STAThread]
        public static void Main(string[] args)
        {
            L.ApplyUICulture();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainWindow(args));
        }
    }

    sealed class IconLabel : Label
    {
        readonly int iconSize;
        readonly int gap;
        Glyph icon;
        Color iconColor;

        public IconLabel(string title, Glyph icon, int iconSize, int gap, Color color)
        {
            Text = title;
            this.icon = icon;
            this.iconSize = iconSize;
            this.gap = gap;
            iconColor = color;
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
            TextAlign = ContentAlignment.MiddleLeft;
            AutoEllipsis = true;
        }

        public void SetIcon(Glyph value, Color color)
        {
            icon = value;
            iconColor = color;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            float scale = e.Graphics.DpiX / 96f;
            int size = (int)Math.Round(iconSize * scale);
            int offset = size + (int)Math.Round(gap * scale);
            var textArea = new Rectangle(offset, 0, Math.Max(0, Width - offset), Height);
            Theme.DrawGlyph(e.Graphics, icon, new Rectangle(0, (Height - size) / 2, size, size), iconColor);
            TextRenderer.DrawText(e.Graphics, Text, Font, textArea, ForeColor, TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | Theme.PaintTextFlags);
        }
    }

    public sealed class MainWindow : ChromeForm
    {
        sealed class Job
        {
            public string Path;
            public RunResult Result;
            public Exception Error;
        }

        readonly ModelList list = new ModelList();
        readonly AppHeader header = new AppHeader();
        readonly QueueScrollBar queueScrollBar;
        readonly Dictionary<Control, string> captions = new Dictionary<Control, string>();
        readonly ComparisonCanvas canvas = new ComparisonCanvas();
        readonly ComparisonSlider slider = new ComparisonSlider();
        readonly ThemedCheck diff = new ThemedCheck(), exact = new ThemedCheck(), whole = new ThemedCheck();
        readonly ThemedRadio full = new ThemedRadio(), fast = new ThemedRadio();
        readonly TextBox output = new TextBox();
        readonly IconLabel status = new IconLabel("", Glyph.Info, 16, 8, Theme.Muted);
        readonly Label summary = new Label(), where = new Label(), reservedNote = new Label();
        readonly RowStyle summaryRow = new RowStyle(SizeType.Absolute, 0);
        readonly RowStyle viewRow = new RowStyle(SizeType.Absolute, 0);
        readonly RowStyle reservedRow = new RowStyle(SizeType.Absolute, 0);
        readonly RowStyle footerRow = new RowStyle(SizeType.Absolute, 205);
        readonly RowStyle optionsRow = new RowStyle(SizeType.Absolute, 106);
        readonly RowStyle queueToolsRow = new RowStyle(SizeType.Absolute, 65);
        readonly RowStyle scrubRow = new RowStyle(SizeType.Absolute, 56);
        readonly ThemedProgress bar = new ThemedProgress();
        readonly ToolTip tips = new ToolTip();
        readonly Icon appIcon = new Icon(typeof(MainWindow), "App.ico");
        readonly List<Button> editButtons = new List<Button>();
        readonly Action<string> openFolder;
        Button start, cancel, open, clear;
        RunResult lastSaved;
        CancellationTokenSource token;
        Func<string> statusText;
        bool closing, compactLayout;
        static readonly Color Review = Color.FromArgb(244, 189, 99);
        static readonly Color Failure = Color.FromArgb(255, 137, 137);

        public MainWindow(string[] initial) : this(initial, OpenInExplorer) { }

        internal MainWindow(string[] initial, Action<string> openFolder)
        {
            this.openFolder = openFolder;
            queueScrollBar = new QueueScrollBar(list);
            SuspendLayout();
            Text = "MDL Optimizer";
            Icon = appIcon;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Theme.Font(10.5f);
            ForeColor = Theme.Text;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(860, 520);
            ClientSize = new Size(1184, 774);

            var page = Grid(1, 2, Theme.Bg);
            FixedRow(page, 63);
            FlexRow(page);
            header.Dock = DockStyle.Fill;
            header.Margin = Padding.Empty;
            page.Controls.Add(header, 0, 0);
            var body = Grid(1, 2, Theme.Bg);
            body.Padding = new Padding(16);
            FlexRow(body);
            body.RowStyles.Add(footerRow);
            page.Controls.Add(body, 0, 1);

            var work = Grid(2, 1, Theme.Bg);
            work.Margin = new Padding(0, 0, 0, 17);
            work.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38.5f));
            work.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 61.5f));
            work.Controls.Add(BuildQueue(), 0, 0);
            work.Controls.Add(BuildComparison(), 1, 0);
            body.Controls.Add(work, 0, 0);
            body.Controls.Add(BuildFooter(), 0, 1);
            Controls.Add(page);
            AcceptButton = start;
            header.LanguageChanged += (s, e) => ApplyLanguage();
            ApplyLanguage();

            foreach (Control target in new Control[] { this, list, canvas }) {
                target.AllowDrop = true;
                target.DragEnter += (s, e) => {
                    if (token == null && e.Data.GetDataPresent(DataFormats.FileDrop))
                        e.Effect = DragDropEffects.Copy;
                };
                target.DragDrop += (s, e) => {
                    if (token == null && e.Data.GetDataPresent(DataFormats.FileDrop))
                        AddFiles((string[])e.Data.GetData(DataFormats.FileDrop));
                };
            }
            FormClosing += (s, e) => {
                if (token == null) return;
                closing = true;
                token.Cancel();
                e.Cancel = true;
            };
            AddFiles(initial ?? new string[0]);
            ClientSizeChanged += (s, e) => AdjustDensity();
            ResumeLayout(true);
        }

        T Bind<T>(T control, string key) where T : Control
        {
            captions.Add(control, key);
            control.Text = L.T(key);
            return control;
        }

        internal void ApplyLanguage()
        {
            SuspendLayout();
            try {
                foreach (var caption in captions) caption.Key.Text = L.T(caption.Value);
                header.ApplyLanguage();
                list.ApplyLanguage();
                queueScrollBar.ApplyLanguage();
                bar.ApplyLanguage();
                canvas.ApplyLanguage();
                slider.ApplyLanguage();
                output.AccessibleName = L.T("options.output");
                reservedNote.AccessibleName = L.T("audit.title");
                list.BeginUpdate();
                try {
                    foreach (ListViewItem item in list.Items) {
                        item.SubItems[1].Text = L.T((string)item.SubItems[1].Tag);
                        var job = (Job)item.Tag;
                        if (job.Error != null) item.ToolTipText = LocalizedText.Error(job.Error);
                    }
                } finally {
                    list.EndUpdate();
                }
                if (statusText != null) status.Text = statusText();
                SelectResult();
            } finally {
                ResumeLayout(true);
            }
        }

        void AdjustDensity()
        {
            bool compact = ClientSize.Height < Theme.Scale(this, 665);
            if (compact == compactLayout) return;
            compactLayout = compact;
            footerRow.Height = Theme.Scale(this, compact ? 148 : 205);
            optionsRow.Height = Theme.Scale(this, compact ? 84 : 106);
            queueToolsRow.Height = Theme.Scale(this, compact ? 48 : 65);
            scrubRow.Height = Theme.Scale(this, compact ? 39 : 56);
        }

        protected override void OnLoad(EventArgs e)
        {
            var area = Screen.FromControl(this).WorkingArea;
            if (WindowState == FormWindowState.Normal && (Width > area.Width || Height > area.Height)) {
                Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
                CenterToScreen();
            }
            AdjustDensity();
            base.OnLoad(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
            if (disposing) appIcon.Dispose();
        }

        Control BuildQueue()
        {
            var card = Card(new Padding(13, 10, 13, 8));
            card.Margin = new Padding(0, 0, 8, 0);
            var content = Grid(1, 4);
            FixedRow(content, 41);
            content.RowStyles.Add(queueToolsRow);
            FlexRow(content);
            FixedRow(content, 39);
            card.Controls.Add(content);
            content.Controls.Add(Heading("queue.title", Glyph.Layers), 0, 0);

            var buttons = Grid(3, 1);
            AutoColumn(buttons);
            AutoColumn(buttons);
            FillColumn(buttons);
            var add = MakeButton("queue.add", Glyph.Plus, ButtonTone.Primary, 159, 39, ChooseModels);
            clear = MakeButton("queue.clear", Glyph.Broom, ButtonTone.Neutral, 110, 39, (s, e) => ClearQueue());
            add.Margin = new Padding(0, 0, 9, 4);
            clear.Margin = new Padding(0, 0, 0, 4);
            buttons.Controls.Add(add, 0, 0);
            buttons.Controls.Add(clear, 1, 0);
            editButtons.Add(add);
            editButtons.Add(clear);
            content.Controls.Add(buttons, 0, 1);

            var frame = Frame();
            var viewport = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Stage, Margin = Padding.Empty };
            list.Dock = DockStyle.None;
            list.Margin = Padding.Empty;
            list.SelectedIndexChanged += (s, e) => SelectResult();
            viewport.Controls.Add(list);
            frame.Controls.Add(viewport);
            frame.Controls.Add(queueScrollBar);
            content.Controls.Add(frame, 0, 2);
            var hint = Bind(new IconLabel("", Glyph.Info, 18, 8, Theme.Muted), "queue.drop");
            hint.Margin = new Padding(9, 3, 0, 0);
            hint.ForeColor = Theme.Quiet;
            hint.Font = Theme.Font(10);
            content.Controls.Add(hint, 0, 3);
            return card;
        }

        Control BuildComparison()
        {
            var card = Card(new Padding(12, 10, 12, 8));
            card.Margin = new Padding(7, 0, 0, 0);
            var content = Grid(1, 6);
            FixedRow(content, 51);
            FlexRow(content);
            content.RowStyles.Add(scrubRow);
            content.RowStyles.Add(summaryRow);
            content.RowStyles.Add(viewRow);
            content.RowStyles.Add(reservedRow);
            card.Controls.Add(content);

            var heading = Grid(3, 1);
            heading.Margin = new Padding(5, 0, 0, 10);
            FillColumn(heading);
            AutoColumn(heading);
            AutoColumn(heading);
            heading.Controls.Add(Heading("preview.title", Glyph.Image), 0, 0);
            PlaceChoice(whole, "preview.whole", heading, 1);
            PlaceChoice(diff, "preview.differences", heading, 2);
            whole.Enabled = diff.Enabled = false;
            whole.CheckedChanged += (s, e) => SelectResult();
            diff.CheckedChanged += (s, e) => {
                canvas.Heat = diff.Checked;
                slider.Enabled = diff.Enabled && !diff.Checked;
            };
            content.Controls.Add(heading, 0, 0);

            var frame = Frame();
            canvas.Dock = DockStyle.Fill;
            canvas.Font = Theme.Font(12);
            canvas.Margin = Padding.Empty;
            frame.Controls.Add(canvas);
            content.Controls.Add(frame, 0, 1);

            var scrub = Grid(3, 1);
            scrub.Padding = new Padding(14, 3, 14, 0);
            AutoColumn(scrub);
            FillColumn(scrub);
            AutoColumn(scrub);
            scrub.Controls.Add(SliderLabel("preview.original"), 0, 0);
            slider.Dock = DockStyle.Fill;
            slider.Margin = new Padding(16, 0, 16, 0);
            slider.BackColor = Theme.Card;
            slider.Enabled = false;
            slider.ValueChanged += (s, e) => canvas.Split = 100 - slider.Value;
            canvas.Split = 100 - slider.Value;
            scrub.Controls.Add(slider, 1, 0);
            scrub.Controls.Add(SliderLabel("preview.optimized"), 2, 0);
            content.Controls.Add(scrub, 0, 2);

            AddDetail(content, summary, 3, true);
            AddDetail(content, where, 4, false);
            AddDetail(content, reservedNote, 5, false);
            reservedNote.SizeChanged += (s, e) => FitAuditNote();
            return card;
        }

        Control BuildFooter()
        {
            var card = Card(new Padding(1, 4, 1, 4));
            var content = Grid(1, 3);
            content.RowStyles.Add(optionsRow);
            FixedRow(content, 1);
            FlexRow(content);
            card.Controls.Add(content);
            var options = Grid(3, 2);
            options.Padding = new Padding(13, 2, 12, 3);
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
            FillColumn(options);
            AutoColumn(options);
            FlexRow(options);
            FlexRow(options);
            options.Controls.Add(OptionLabel("options.saveTo", Glyph.Folder), 0, 0);

            var field = new Surface(Theme.Stage, Theme.Line, 7);
            field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            field.Height = 35;
            field.Padding = new Padding(10, 1, 10, 1);
            field.Margin = new Padding(0, 0, 11, 0);
            var fieldLayout = Grid(1, 1, Theme.Stage);
            output.BackColor = Theme.Stage;
            output.ForeColor = Theme.Text;
            output.BorderStyle = BorderStyle.None;
            output.Margin = Padding.Empty;
            output.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            output.Text = Path.Combine(String.IsNullOrWhiteSpace(docs) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : docs, "MDL Optimizer");
            output.Enter += (s, e) => field.BorderColor = Theme.Accent;
            output.Leave += (s, e) => field.BorderColor = Theme.Line;
            field.MouseDown += (s, e) => output.Focus();
            fieldLayout.MouseDown += (s, e) => output.Focus();
            fieldLayout.Controls.Add(output, 0, 0);
            field.Controls.Add(fieldLayout);
            options.Controls.Add(field, 1, 0);
            var browse = MakeButton("options.browse", Glyph.FolderOpen, ButtonTone.Neutral, 146, 35, ChooseOutput);
            options.Controls.Add(browse, 2, 0);
            editButtons.Add(browse);

            options.Controls.Add(OptionLabel("options.validation", Glyph.Shield), 0, 1);
            var modes = Grid(4, 1);
            AutoColumn(modes);
            AutoColumn(modes);
            AutoColumn(modes);
            FillColumn(modes);
            PlaceChoice(full, "options.full", modes, 0);
            PlaceChoice(fast, "options.fast", modes, 1);
            PlaceChoice(exact, "options.exact", modes, 2);
            full.Margin = fast.Margin = new Padding(0, 0, 25, 0);
            exact.Margin = Padding.Empty;
            full.Checked = true;
            options.Controls.Add(modes, 1, 1);
            options.SetColumnSpan(modes, 2);
            content.Controls.Add(options, 0, 0);
            content.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Theme.Line, Margin = Padding.Empty }, 0, 1);

            var actions = Grid(4, 1);
            actions.Padding = new Padding(13, 0, 11, 0);
            FillColumn(actions);
            AutoColumn(actions);
            AutoColumn(actions);
            AutoColumn(actions);
            var progress = Grid(1, 4);
            progress.Margin = new Padding(0, 0, 24, 0);
            FlexRow(progress);
            FixedRow(progress, 30);
            FixedRow(progress, 9);
            FlexRow(progress);
            status.ForeColor = Theme.Quiet;
            status.Font = Theme.Font(10);
            status.TextChanged += (s, e) => tips.SetToolTip(status, status.Text);
            progress.Controls.Add(status, 0, 1);
            bar.Dock = DockStyle.Fill;
            bar.Margin = Padding.Empty;
            progress.Controls.Add(bar, 0, 2);
            actions.Controls.Add(progress, 0, 0);
            open = MakeButton("actions.open", Glyph.FolderOpen, ButtonTone.Primary, 196, 41, OpenResult);
            cancel = MakeButton("actions.cancel", Glyph.Close, ButtonTone.Neutral, 112, 41, (s, e) => { if (token != null) token.Cancel(); });
            start = MakeButton("actions.start", Glyph.Play, ButtonTone.Success, 166, 41, async delegate { await StartAsync(); });
            start.Font = Theme.Font(10.5f, FontStyle.Bold);
            open.Margin = cancel.Margin = new Padding(0, 0, 10, 0);
            open.Visible = open.Enabled = cancel.Enabled = false;
            actions.Controls.Add(open, 1, 0);
            actions.Controls.Add(cancel, 2, 0);
            actions.Controls.Add(start, 3, 0);
            content.Controls.Add(actions, 0, 2);
            return card;
        }

        static TableLayoutPanel Grid(int columns, int rows, Color? background = null)
        {
            var grid = new TableLayoutPanel {
                ColumnCount = columns, RowCount = rows, Dock = DockStyle.Fill,
                Margin = Padding.Empty, BackColor = background ?? Theme.Card
            };
            if (columns == 1) FillColumn(grid);
            if (rows == 1) FlexRow(grid);
            return grid;
        }
        static void FixedRow(TableLayoutPanel grid, int height) { grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); }
        static void FlexRow(TableLayoutPanel grid) { grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); }
        static void AutoColumn(TableLayoutPanel grid) { grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); }
        static void FillColumn(TableLayoutPanel grid) { grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); }

        static Surface Card(Padding padding)
        {
            return new Surface(Theme.Card, Color.Empty, 12) { Dock = DockStyle.Fill, Padding = padding, Margin = Padding.Empty };
        }
        static Surface Frame()
        {
            return new Surface(Theme.Stage, Color.Empty, 9) { Dock = DockStyle.Fill, Padding = new Padding(2), Margin = Padding.Empty };
        }
        IconLabel Heading(string key, Glyph glyph)
        {
            return Bind(new IconLabel("", glyph, 27, 13, Theme.Accent) { Font = Theme.Font(14, FontStyle.Bold), ForeColor = Theme.Text }, key);
        }
        IconLabel OptionLabel(string key, Glyph glyph)
        {
            return Bind(new IconLabel("", glyph, 19, 8, Theme.Accent) { Font = Theme.Font(10.5f, FontStyle.Bold) }, key);
        }
        Label SliderLabel(string key)
        {
            return Bind(new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = Padding.Empty, Font = Theme.Font(10) }, key);
        }
        void AddDetail(TableLayoutPanel grid, Label label, int row, bool bold)
        {
            label.Dock = DockStyle.Fill;
            label.Margin = new Padding(6, 0, 3, 0);
            label.AutoEllipsis = true;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Font = Theme.Font(bold ? 10 : 9, bold ? FontStyle.Bold : FontStyle.Regular);
            label.ForeColor = bold ? Theme.Text : Theme.Quiet;
            label.TextChanged += (s, e) => tips.SetToolTip(label, label.Text);
            grid.Controls.Add(label, 0, row);
        }
        void PlaceChoice(ButtonBase choice, string key, TableLayoutPanel parent, int column)
        {
            Bind(choice, key);
            choice.AutoSize = true;
            choice.Anchor = AnchorStyles.Left;
            choice.Margin = new Padding(12, 0, 2, 0);
            choice.ForeColor = Theme.Text;
            parent.Controls.Add(choice, column, 0);
        }
        Button MakeButton(string key, Glyph glyph, ButtonTone tone, int width, int height, EventHandler click)
        {
            var button = new ActionButton("", glyph, tone) {
                Anchor = AnchorStyles.Left, AutoSize = false, Size = new Size(width, height), Margin = Padding.Empty
            };
            button.Click += click;
            return Bind(button, key);
        }

        void ChooseModels(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog { Filter = L.T("dialog.modelFilter") + "|*.mdl", Multiselect = true, Title = L.T("dialog.addModels") })
                if (dlg.ShowDialog(this) == DialogResult.OK) AddFiles(dlg.FileNames);
        }

        void ChooseOutput(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog { Description = L.T("dialog.chooseOutput"), SelectedPath = output.Text })
                if (dlg.ShowDialog(this) == DialogResult.OK) output.Text = dlg.SelectedPath;
        }

        public void AddFiles(IEnumerable<string> paths)
        {
            if (token != null) return;
            var existing = new HashSet<string>(list.Items.Cast<ListViewItem>().Select(x => ((Job)x.Tag).Path), StringComparer.OrdinalIgnoreCase);
            int previousCount = list.Items.Count;
            list.BeginUpdate();
            try {
                foreach (string path in paths) {
                    if (String.IsNullOrWhiteSpace(path) || !Path.GetExtension(path).Equals(".mdl", StringComparison.OrdinalIgnoreCase)
                        || !File.Exists(DiskPath.Full(path))) continue;
                    string file = DiskPath.Display(DiskPath.Full(path));
                    if (!existing.Add(file)) continue;
                    var row = new ListViewItem(new[] { Path.GetFileName(file), "" }) { Tag = new Job { Path = file }, ToolTipText = file };
                    SetJobState(row, "queue.pending");
                    list.Items.Add(row);
                }
            } finally {
                list.EndUpdate();
            }
            if (list.Items.Count > 0 && list.SelectedItems.Count == 0) list.Items[0].Selected = true;
            start.Enabled = clear.Enabled = list.Items.Count > 0;
            if (previousCount != list.Items.Count || list.Items.Count == 0) ShowQueueStatus();
        }

        void ClearQueue()
        {
            lastSaved = null;
            list.Items.Clear();
            SelectResult();
            start.Enabled = clear.Enabled = false;
            ShowQueueStatus();
        }

        static void SetJobState(ListViewItem row, string key)
        {
            row.SubItems[1].Tag = key;
            row.SubItems[1].Text = L.T(key);
        }

        void SetStatus(Func<string> format)
        {
            statusText = format;
            status.Text = format();
        }

        void ShowQueueStatus()
        {
            int count = list.Items.Count;
            SetStatus(() => count == 0 ? L.T("queue.empty") : count == 1 ? L.T("queue.single") : L.T("queue.count", count));
            status.SetIcon(Glyph.Info, Theme.Muted);
            bar.Value = 0;
            bar.ForeColor = Theme.Accent;
        }

        RunResult SelectedResult()
        {
            return list.SelectedItems.Count == 0 ? null : ((Job)list.SelectedItems[0].Tag).Result;
        }

        internal void ShowReservedAudit(RunResult result)
        {
            if (result == null || !result.ReservedPassed.HasValue) {
                reservedNote.Text = "";
                reservedRow.Height = 0;
                return;
            }
            bool passed = result.ReservedPassed.Value;
            reservedNote.ForeColor = passed ? Theme.Quiet : Review;
            if (passed) {
                reservedNote.Text = L.T("audit.passed", result.ReservedViews);
            } else {
                string detail = result.ReservedFailedViews > 0
                    ? L.T("audit.outliers", result.ReservedFailedViews, result.ReservedViews)
                    : L.T("audit.global", result.ReservedViews);
                reservedNote.Text = L.T("audit.failed", detail);
            }
            FitAuditNote();
        }

        void FitAuditNote()
        {
            if (reservedNote.Text.Length == 0 || reservedNote.Width < 1) return;
            var room = new Size(reservedNote.Width, 0);
            int height = reservedNote.GetPreferredSize(room).Height + Theme.Scale(reservedNote, 7);
            if (reservedRow.Height != height) reservedRow.Height = height;
        }

        void SelectResult()
        {
            if (open == null) return;
            var result = SelectedResult();
            var saved = result ?? lastSaved;
            string folder = saved == null ? null : Path.GetDirectoryName(saved.GameFile ?? saved.ExactFile);
            open.Tag = folder;
            open.AccessibleDescription = folder != null && result == null ? L.T("result.lastFolder", folder) : folder;
            tips.SetToolTip(open, open.AccessibleDescription);
            open.Enabled = open.Visible = folder != null;
            ShowReservedAudit(result);
            bool hasPreview = result != null;
            try {
                canvas.SetFolder(hasPreview ? result.Directory : null, whole.Checked);
            } catch (Exception ex) {
                hasPreview = false;
                SetStatus(() => L.T("preview.unavailable", LocalizedText.Error(ex)));
                status.SetIcon(Glyph.Warning, Review);
            }
            whole.Enabled = result != null;
            diff.Enabled = hasPreview;
            slider.Enabled = hasPreview && !diff.Checked;

            if (result == null) {
                summary.Text = "";
                var error = list.SelectedItems.Count == 0 ? null : ((Job)list.SelectedItems[0].Tag).Error;
                where.Text = error == null ? "" : LocalizedText.Error(error);
            } else {
                summary.Text = L.T("result.summary",
                    result.OriginalTriangles, result.GameTriangles, 1.0 - (double)result.GameTriangles / Math.Max(1, result.OriginalTriangles));
                where.Text = L.T("result.views", result.Views, result.DisplayView(whole.Checked));
            }
            summaryRow.Height = String.IsNullOrEmpty(summary.Text) ? 0 : summary.Font.Height + 5;
            viewRow.Height = String.IsNullOrEmpty(where.Text) ? 0 : where.Font.Height + 5;
        }

        static void OpenInExplorer(string folder)
        {
            var process = Process.Start(new ProcessStartInfo { FileName = folder, Verb = "open", UseShellExecute = true });
            if (process != null) process.Dispose();
        }

        void OpenResult(object sender, EventArgs e)
        {
            var folder = open.Tag as string;
            if (folder == null) return;
            if (!Directory.Exists(DiskPath.Full(folder))) {
                SetStatus(() => L.T("result.folderMissing", folder));
                status.SetIcon(Glyph.Warning, Review);
                return;
            }
            try {
                openFolder(folder);
            } catch (System.ComponentModel.Win32Exception ex) {
                SetStatus(() => L.T("result.folderError", LocalizedText.Error(ex)));
                status.SetIcon(Glyph.Warning, Review);
            }
        }

        public async Task StartAsync()
        {
            if (token != null || list.Items.Count == 0) return;
            if (String.IsNullOrWhiteSpace(output.Text)) {
                SetStatus(() => L.T("options.outputRequired"));
                status.SetIcon(Glyph.Warning, Review);
                output.Focus();
                return;
            }
            string parent = output.Text.Trim();
            var options = new RunOptions { Thorough = full.Checked, ExactOnly = exact.Checked };
            var currentToken = new CancellationTokenSource();
            token = currentToken;
            foreach (var button in editButtons) button.Enabled = false;
            output.Enabled = full.Enabled = fast.Enabled = exact.Enabled = start.Enabled = false;
            cancel.Enabled = true;
            status.SetIcon(Glyph.Info, Theme.Accent);
            bar.ForeColor = Theme.Accent;
            bar.Value = 0;

            int done = 0, errors = 0, reviews = 0;
            var jobs = list.Items.Cast<ListViewItem>().ToArray();
            try {
                foreach (var item in jobs) {
                    if (currentToken.IsCancellationRequested) break;
                    var job = (Job)item.Tag;
                    job.Error = null;
                    job.Result = null;
                    item.ToolTipText = job.Path;
                    SetJobState(item, "queue.running");
                    item.Selected = true;
                    item.EnsureVisible();
                    SelectResult();
                    var timer = Stopwatch.StartNew();
                    long last = -200;
                    int finished = done + errors;
                    try {
                        var result = await Task.Run(() => Optimizer.RunLocalized(job.Path, parent, options, () => currentToken.IsCancellationRequested, (pct, msg) => {
                            long now = timer.ElapsedMilliseconds;
                            if (now - last < 140 && pct < 94) return;
                            last = now;
                            BeginInvoke((Action)(() => {
                                if (token != currentToken || job.Result != null || job.Error != null) return;
                                SetStatus(() => Path.GetFileName(job.Path) + "   ·   " + msg.ToString());
                                int batchPercent = (int)((finished * 100L + Math.Max(0, Math.Min(100, pct))) / jobs.Length);
                                bar.Value = batchPercent;
                            }));
                        }));
                        job.Result = result;
                        lastSaved = result;
                        done++;
                        if (result.ReservedPassed == false) {
                            reviews++;
                            SetJobState(item, "queue.review");
                        } else {
                            SetJobState(item, "queue.done");
                        }
                    } catch (OperationCanceledException) {
                        SetJobState(item, "queue.cancelled");
                        break;
                    } catch (Exception ex) {
                        errors++;
                        job.Error = ex;
                        item.ToolTipText = LocalizedText.Error(ex);
                        SetJobState(item, "queue.failed");
                    }
                    bar.Value = (int)((done + errors) * 100L / jobs.Length);
                    SelectResult();
                }
                bool cancelled = currentToken.IsCancellationRequested;
                SetStatus(() => L.T("run.summary", done, errors)
                    + (reviews > 0 ? "   ·   " + L.T("run.reviews", reviews) : "")
                    + (cancelled ? "   ·   " + L.T("queue.cancelled") : ""));
                bool needsAttention = errors > 0 || reviews > 0 || cancelled;
                bar.ForeColor = errors > 0 ? Failure : (needsAttention ? Review : Theme.Green);
                status.SetIcon(needsAttention ? Glyph.Warning : Glyph.Check, bar.ForeColor);
            } finally {
                token = null;
                currentToken.Dispose();
                foreach (var button in editButtons) button.Enabled = true;
                output.Enabled = full.Enabled = fast.Enabled = exact.Enabled = true;
                start.Enabled = list.Items.Count > 0;
                cancel.Enabled = false;
                if (closing) Close();
            }
        }
    }
}
