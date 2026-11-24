using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using MdlOptimizer;

static class WorkflowTests
{
    static void Check(bool ok,string description)
    {
        if(!ok)throw new Exception("Falhou: "+description);
        Console.WriteLine("OK  "+description);
    }
    static IEnumerable<Control> Children(Control parent)
    {
        foreach(Control c in parent.Controls) {yield return c;foreach(var sub in Children(c))yield return sub;}
    }
    const int WmPrintClient = 0x318, PrfClient = 0x4, WmNcActivate = 0x86, WmNcPaint = 0x85;
    static readonly Point BufferOrigin = new Point(7, 5);

    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("gdi32.dll")] static extern int SaveDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool RestoreDC(IntPtr dc, int saved);
    [DllImport("gdi32.dll")] static extern bool SetViewportOrgEx(IntPtr dc, int x, int y, IntPtr previous);
    [DllImport("gdi32.dll")] static extern int IntersectClipRect(IntPtr dc, int left, int top, int right, int bottom);

    static Bitmap RepaintRegion(Control choice, Rectangle dirty, Color previousColor)
    {
        var bitmap = new Bitmap(choice.Width + BufferOrigin.X * 2, choice.Height + BufferOrigin.Y * 2,
            PixelFormat.Format24bppRgb);
        using(var graphics = Graphics.FromImage(bitmap)) {
            graphics.Clear(previousColor);
            IntPtr dc = graphics.GetHdc();
            int saved = SaveDC(dc);
            try {
                // The child may have a nonzero origin when WinForms reuses a paint buffer.
                SetViewportOrgEx(dc, BufferOrigin.X, BufferOrigin.Y, IntPtr.Zero);
                IntersectClipRect(dc, dirty.Left, dirty.Top, dirty.Right, dirty.Bottom);
                SendMessage(choice.Handle, WmPrintClient, dc, (IntPtr)PrfClient);
            } finally {
                RestoreDC(dc, saved);
                graphics.ReleaseHdc(dc);
            }
        }
        return bitmap;
    }

    static void CheckChoicePixels(Control choice)
    {
        var dirty = Rectangle.Inflate(choice.ClientRectangle, -1, -2);
        var sample = dirty;
        sample.Offset(BufferOrigin);
        using(var first = RepaintRegion(choice, dirty, Color.Magenta))
        using(var second = RepaintRegion(choice, dirty, Color.Lime)) {
            for(int y = 0; y < first.Height; y++)
                for(int x = 0; x < first.Width; x++) {
                    var pixel = first.GetPixel(x, y);
                    if(sample.Contains(x, y)) {
                        if(pixel != second.GetPixel(x, y))
                            throw new Exception(choice.Text + " preservou pixels da pintura anterior.");
                    } else if(pixel.ToArgb() != Color.Magenta.ToArgb()) {
                        throw new Exception(choice.Text + " desenhou fora da região invalidada.");
                    }
                }
        }
    }

    internal static void CheckModalRepaint(Form form)
    {
        var choices = Children(form).Where(c => c is CheckBox || c is RadioButton).ToArray();
        foreach(bool active in new[] { false, true }) {
            form.Enabled = active;
            SendMessage(form.Handle, WmNcActivate, active ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero);
            SendMessage(form.Handle, WmNcPaint, (IntPtr)1, IntPtr.Zero);
            foreach(var choice in choices) CheckChoicePixels(choice);
        }
        Check(choices.Any(c => c is CheckBox) && choices.Any(c => c is RadioButton),
            "repintura parcial das opções ao perder e recuperar a ativação da janela");
    }

    const int WmKeyDown = 0x100, WmCommand = 0x111, WmMouseMove = 0x200;
    const int WmLeftDown = 0x201, WmLeftUp = 0x202, WmMouseWheel = 0x20a;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);

    static void ClickButton(Button button)
    {
        // PerformClick skips a hidden form. Deliver the button's BN_CLICKED notification.
        SendMessage(button.Parent.Handle, WmCommand, IntPtr.Zero, button.Handle);
    }

    static bool OwnWindowVisible(Control control)
    {
        // Control.Visible also checks its ancestors. The test form itself stays hidden.
        return (GetWindowLong(control.Handle, -16) & 0x10000000) != 0;
    }

    static IntPtr MousePosition(int x, int y) { return (IntPtr)((y << 16) | (x & 0xffff)); }

    static void PressKey(Control control, Keys key)
    {
        var message = Message.Create(control.Handle, WmKeyDown, (IntPtr)key, IntPtr.Zero);
        if (!control.PreProcessMessage(ref message))
            SendMessage(message.HWnd, message.Msg, message.WParam, message.LParam);
    }

    static void CheckLanguageSelector(LanguageSelector selector, string config)
    {
        // Leave room for the arrow itself. A native light drop button fills
        // most of this area even when the rest of the field uses a dark theme.
        using (var field = new Bitmap(selector.Width, selector.Height)) {
            selector.DrawToBitmap(field, selector.ClientRectangle);
            int samples = 0, dark = 0;
            int arrowWidth = Theme.Scale(selector, 16);
            for (int y = field.Height / 4; y < 3 * field.Height / 4; y++) {
                for (int x = field.Width - arrowWidth; x < field.Width - 2; x++) {
                    samples++;
                    if (field.GetPixel(x, y).GetBrightness() < .4) dark++;
                }
            }
            Check(samples > 0 && dark > samples * .65, "a seta do seletor usa a superfície escura do cabeçalho");
        }
        var menu = selector.ContextMenuStrip;
        ClickButton(selector);
        Check(menu.Visible && menu.Items.Count == 5 && ((ToolStripMenuItem)menu.Items[0]).Checked,
            "o clique abre as cinco opções e marca o idioma ativo");
        using (var popup = new Bitmap(menu.Width, menu.Height)) {
            menu.DrawToBitmap(popup, menu.ClientRectangle);
            Check(menu.Items.Cast<ToolStripItem>().All(item => popup.GetPixel(
                item.Bounds.Right - Theme.Scale(menu, 8), item.Bounds.Top + item.Height / 2).GetBrightness() < .4),
                "as opções do menu aberto usam a paleta escura");
        }
        PressKey(menu, Keys.Down);
        PressKey(menu, Keys.Escape);
        Check(!menu.Visible && selector.SelectedIndex == 0 && !File.Exists(config),
            "Escape fecha a lista sem alterar a preferência");
        PressKey(selector, Keys.F4);
        PressKey(menu, Keys.Down);
        PressKey(menu, Keys.Enter);
        Check(!menu.Visible && selector.SelectedIndex == 1 && L.Preference == "pt-BR",
            "F4, seta e Enter selecionam o português pelo teclado");
        ClickButton(selector);
        var english = menu.Items[2];
        var point = MousePosition(english.Bounds.Left + english.Width / 2, english.Bounds.Top + english.Height / 2);
        SendMessage(menu.Handle, WmMouseMove, IntPtr.Zero, point);
        SendMessage(menu.Handle, WmLeftDown, (IntPtr)1, point);
        SendMessage(menu.Handle, WmLeftUp, IntPtr.Zero, point);
        Check(!menu.Visible && selector.Text == "English" && L.Preference == "en",
            "o clique na opção fecha a lista e aplica o idioma");
    }

    static void CheckQueueViewport(ListView list)
    {
        var bounds = list.Parent.RectangleToScreen(list.Bounds);
        var visible = Rectangle.Intersect(bounds, list.Parent.RectangleToScreen(list.Parent.ClientRectangle));
        var client = list.RectangleToScreen(list.ClientRectangle);
        int columns = list.Columns.Cast<ColumnHeader>().Sum(c => c.Width);
        Check(visible.Width > 0 && visible.Height > 0 && client.Contains(visible) && client.Left + columns <= visible.Right,
            "as duas colunas cabem na fila com as barras do sistema fora da área visível");
    }

    static void CheckLanguages(string scratch, string fixture)
    {
        string config = Path.Combine(scratch, "preferences", "language.txt");
        var systems = new[] { "es-MX", "ru-RU", "en-GB", "pt-PT", "fr-FR" };
        var expected = new[] { "es", "ru", "en", "pt-BR", "en" };
        for (int i = 0; i < systems.Length; i++) {
            L.Initialize(config, CultureInfo.GetCultureInfo(systems[i]));
            Check(L.Language == expected[i] && L.Preference == "", "idioma inicial do Windows " + systems[i]);
        }
        var spanish = CultureInfo.GetCultureInfo("es-MX");
        L.Initialize(config, spanish);
        using (var form = new MainWindow(new[] { fixture })) {
            // Button activation and keyboard focus need a visible HWND.
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Show();
            var controls = Children(form).ToArray();
            foreach (var c in controls) { IntPtr child = c.Handle; }
            var selector = controls.OfType<LanguageSelector>().Single(c => c.Name == "languageSelector");
            var list = controls.OfType<ListView>().Single();
            var path = controls.OfType<TextBox>().Single();
            var mode = controls.OfType<RadioButton>().Single(c => c.Text == "Rápida");
            var start = controls.OfType<Button>().Single(c => c.Text == "Optimizar");
            var row = list.Items[0];
            row.Selected = true;
            path.Text = scratch;
            mode.Checked = true;
            Check(selector.SelectedIndex == 0 && !File.Exists(config), "a primeira execução acompanha o sistema");
            Check(selector.TabStop && selector.Focus(), "o seletor de idioma aceita foco pelo teclado");
            CheckLanguageSelector(selector, config);
            var buttonLabels = new[] { "Otimizar", "Optimize", "Optimizar", "Оптимизировать" };
            var queueLabels = new[] { "Na fila", "Queued", "En cola", "В очереди" };
            for (int i = 0; i < buttonLabels.Length; i++) {
                selector.SelectedIndex = i + 1;
                Check(start.Text == buttonLabels[i] && row.SubItems[1].Text == queueLabels[i] &&
                    row.Text == Path.GetFileName(fixture) && row.Selected && mode.Checked && path.Text == scratch,
                    "troca imediata com a fila e as opções preservadas em " + L.Language);
            }
        }
        L.Initialize(config, spanish);
        using (var reopened = new MainWindow(new string[0])) {
            var selector = Children(reopened).OfType<LanguageSelector>().Single();
            IntPtr owner = reopened.Handle, child = selector.Handle;
            Check(L.Preference == "ru" && selector.Text == "Русский", "idioma escolhido ao reabrir");
            selector.SelectedIndex = 0;
            Check(L.Preference == "" && L.Language == "es" && selector.Text == "Idioma del sistema",
                "retorno ao idioma do sistema");
        }
        File.WriteAllText(config, "invalid");
        L.Initialize(config, CultureInfo.GetCultureInfo("pt-BR"));
        Check(L.Preference == "" && L.Language == "pt-BR", "preferência inválida recuperada com o idioma do Windows");
    }

    internal static void CheckQueueScrolling(string scratch)
    {
        using(var form = new MainWindow(new string[0])) {
            IntPtr owner = form.Handle;
            var controls = Children(form).ToArray();
            foreach(var c in controls) { IntPtr child = c.Handle; }
            var list = controls.OfType<ListView>().Single();
            var clear = controls.OfType<Button>().Single(c => c.Text == "Limpar");
            var start = controls.OfType<Button>().Single(c => c.Text == "Otimizar");
            var bar = controls.SingleOrDefault(c => c.AccessibleName == "Rolagem da fila de modelos");
            Size normal = form.Size;
            Check(list.Items.Count == 0 && !clear.Enabled && !start.Enabled, "fila vazia ao iniciar");
            foreach(var size in new[] { normal, form.MinimumSize }) {
                form.Size = size;
                form.PerformLayout();
                CheckQueueViewport(list);
            }
            var paths = Enumerable.Range(0, 32).Select(i => Path.Combine(scratch, "nome-longo-para-testar-a-coluna-" + i + ".mdl")).ToArray();
            foreach(var p in paths) File.WriteAllText(p, "");
            form.AddFiles(new[] { paths[0], paths[0] });
            Check(list.Items.Count == 1 && list.Items[0].SubItems[1].Text == "Na fila" &&
                clear.Enabled && start.Enabled && (bar == null || !OwnWindowVisible(bar)), "um modelo cabe na fila compacta sem rolagem");

            form.AddFiles(paths);
            foreach(var size in new[] { form.MinimumSize, normal }) {
                form.Size = size;
                form.PerformLayout();
                Application.DoEvents();
                CheckQueueViewport(list);
                Check(bar != null && bar.AccessibleRole == AccessibleRole.ScrollBar && OwnWindowVisible(bar),
                    "a barra do tema aparece quando a fila ultrapassa a altura disponível");
            }
            var point = list.PointToScreen(new Point(5, list.ClientSize.Height / 2));
            if(SystemInformation.MouseWheelScrollLines != 0) {
                SendMessage(list.Handle, WmMouseWheel, (IntPtr)(-120 << 16), MousePosition(point.X, point.Y));
                Check(list.TopItem.Index > 0, "a roda do mouse rola a fila");
            }
            SendMessage(list.Handle, WmKeyDown, (IntPtr)Keys.End, IntPtr.Zero);
            Check(list.SelectedIndices.Count == 1 && list.SelectedIndices[0] == paths.Length - 1 && list.TopItem.Index > 0,
                "a navegação da fila alcança e seleciona o último modelo");
            SendMessage(bar.Handle, WmKeyDown, (IntPtr)Keys.Home, IntPtr.Zero);
            Check(list.TopItem.Index == 0, "Home na barra retorna ao início da fila");
            int x = bar.Width / 2;
            int startY = list.GetItemRect(0).Top + Math.Max(8, bar.DeviceDpi / 10);
            using(var shot = new Bitmap(bar.Width, bar.Height)) {
                bar.DrawToBitmap(shot, bar.ClientRectangle);
                var rail = shot.GetPixel(x, (startY + bar.Height) / 2);
                var grip = shot.GetPixel(x, startY);
                Check(rail.GetBrightness() < .35 && grip.GetBrightness() > rail.GetBrightness() && grip.B > grip.R,
                    "a barra exibe trilho escuro e alça azul visível");
            }
            var bottom = MousePosition(x, bar.Height - 5);
            SendMessage(bar.Handle, WmLeftDown, (IntPtr)1, bottom);
            SendMessage(bar.Handle, WmLeftUp, IntPtr.Zero, bottom);
            Check(list.TopItem.Index > 0, "um clique no trilho avança a lista");
            SendMessage(bar.Handle, WmKeyDown, (IntPtr)Keys.Home, IntPtr.Zero);
            SendMessage(bar.Handle, WmLeftDown, (IntPtr)1, MousePosition(x, startY));
            SendMessage(bar.Handle, WmMouseMove, (IntPtr)1, bottom);
            SendMessage(bar.Handle, WmLeftUp, IntPtr.Zero, bottom);
            int visibleBottom = list.PointToClient(list.Parent.PointToScreen(new Point(0, list.Parent.ClientSize.Height))).Y;
            Check(list.Items[paths.Length-1].Bounds.Bottom <= visibleBottom,
                "o arrasto até o fim da barra revela a última linha");

            ClickButton(clear);
            Application.DoEvents();
            Check(list.Items.Count == 0 && !OwnWindowVisible(bar) && !clear.Enabled && !start.Enabled,
                "Limpar recolhe a barra e restaura os comandos da fila vazia");
            form.AddFiles(new[] { paths[0] });
            Check(list.TopItem.Index == 0 && list.SelectedIndices.Count == 1 && !OwnWindowVisible(bar),
                "uma nova fila começa selecionada no topo");
        }
    }

    [STAThread] static int Main(string[] args)
    {
        string fixture=Path.GetFullPath(args[0]),scratch=Path.GetFullPath(args[1]),proof=Path.GetFullPath(args[2]);
        Directory.CreateDirectory(scratch);
        try {
            var s=Studio.Load(fixture);
            var original=Optimizer.Meshes(s).ToDictionary(m=>m,m=>m.Faces);
            var exact=Simplifier.Exact(s);
            Check(Optimizer.Count(s)>0&&exact.Values.Sum(x=>x.Count)<=Optimizer.Count(s),"malha exata válida");
            string bad=Path.Combine(scratch,"incompleto.mdl");File.WriteAllBytes(bad,s.Data.Take(160).ToArray());
            bool refused=false;
            try {Studio.Load(bad);}catch(InvalidDataException){refused=true;}
            Check(refused,"arquivo truncado recusado");
            var audit=new VisualAudit(s,false);
            var control=audit.Compare(original,null,null);
            Check(control.ExactMatch&&control.Passed,"original contra original sem diferenças");
            var broken=new Dictionary<Mesh,List<Tri>>();
            foreach(var pair in original) broken[pair.Key]=new List<Tri>();
            var failed=audit.Compare(broken,null,null);
            Check(!failed.Passed&&!failed.ExactMatch,"remoção de toda a superfície reprovada pelo teste visual");
            var keep=Path.Combine(scratch,"existente.mdl");File.WriteAllText(keep,"preservar");
            refused=false;try{s.Save(keep,exact,true);}catch(IOException){refused=true;}
            Check(refused&&File.ReadAllText(keep)=="preservar","saída existente protegida");
            var aborted=Path.Combine(scratch,"cancelado");Directory.CreateDirectory(aborted);
            int callbacks=0;bool cancelled=false;
            try{Optimizer.Run(fixture,aborted,new RunOptions{Thorough=false},()=>callbacks>=4,(pct,msg)=>callbacks++);}
            catch(OperationCanceledException){cancelled=true;}
            Check(cancelled&&!Directory.EnumerateFileSystemEntries(aborted).Any(),"cancelamento remove a saída parcial");
            string deep=Path.Combine(scratch,"caminho-longo");
            while(deep.Length<285) deep=Path.Combine(deep,new string('x',48));
            var deepRun=Optimizer.Run(fixture,deep,new RunOptions {Thorough=false,ExactOnly=true},null,null);
            Check(File.Exists(DiskPath.Full(deepRun.ExactFile)) && File.Exists(DiskPath.Full(Path.Combine(deepRun.Directory,"relatorio.json"))),
                "exportação MDL, PNG e relatório em caminho superior a 260 caracteres");
            var target=Path.Combine(scratch,"gui");Directory.CreateDirectory(target);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            CheckLanguages(scratch, fixture);
            CheckQueueScrolling(scratch);
            int status=0;
            var openedFolders = new List<string>();
            using(var form=new MainWindow(new[]{fixture}, openedFolders.Add)) {
                // Exercise the real window, background queue, progress dispatch and comparison
                // controls. Creating window handles does not show the window on the desktop.
                IntPtr handle=form.Handle;
                foreach(Control c in Children(form)) { IntPtr child=c.Handle; }
                CheckModalRepaint(form);
                var path=Children(form).OfType<TextBox>().Single(c=>c.AccessibleName=="Pasta de saída");
                path.Text=target;
                var mode=Children(form).OfType<RadioButton>().Single(c=>c.Text=="Completa");
                Check(mode.Checked,"validação completa selecionada por padrão");
                var folderButton = Children(form).OfType<Button>().Single(c => c.Text == "Abrir pasta do resultado");
                Check(!OwnWindowVisible(folderButton), "o atalho aguarda a publicação do primeiro modelo");
                form.BeginInvoke((Action)(async delegate {
                    try {
                        var active = form.StartAsync();
                        var language = Children(form).OfType<LanguageSelector>().Single();
                        language.SelectedIndex = 4;
                        var activeList = Children(form).OfType<ListView>().Single();
                        Check(language.Enabled && activeList.Items[0].SubItems[1].Text == "Анализ" && path.Text == target,
                            "troca para russo durante a otimização em andamento");
                        await active;
                        Check(activeList.Items[0].SubItems[1].Text == L.T("queue.done") ||
                            activeList.Items[0].SubItems[1].Text == L.T("queue.review"), "conclusão no idioma escolhido durante a execução");
                        language.SelectedIndex = 1;
                        var list=Children(form).OfType<ListView>().Single();
                        var row=list.Items[0];
                        var output=Directory.GetDirectories(target).Single();
                        var firstGame=Directory.GetFiles(output,"*_opt.mdl",SearchOption.AllDirectories).Single();
                        var gameFolder=Path.GetDirectoryName(firstGame);
                        Check(OwnWindowVisible(folderButton) && folderButton.Enabled, "a publicação exibe o botão de abrir a pasta");
                        ClickButton(folderButton);
                        Check(openedFolders.SequenceEqual(new[] { gameFolder }, StringComparer.OrdinalIgnoreCase),
                            "o botão abre a subpasta que contém o MDL otimizado");
                        var auditNote=Children(form).OfType<Label>().Single(c=>c.AccessibleName=="Auditoria reservada");
                        Check(auditNote.Text.StartsWith("Auditoria reservada") && File.Exists(Path.Combine(output,"auditoria-reservada.png")),
                            "a otimização completa mostra o resultado e exporta o PNG da auditoria reservada");
                        Check(File.Exists(Path.Combine(output,"antes.png"))&&File.Exists(Path.Combine(output,"depois.png")),"imagens de comparação exportadas");
                        var canvas=Children(form).OfType<ComparisonCanvas>().Single();
                        var slider=Children(form).OfType<TrackBar>().Single();
                        slider.Value=0;
                        Check(canvas.Split==100,"extremo Original do controle de comparação");
                        slider.Value=100;
                        Check(canvas.Split==0,"extremo Otimizado do controle de comparação");
                        slider.Value=50;
                        var differences=Children(form).OfType<CheckBox>().Single(c=>c.Text=="Diferenças");
                        differences.Checked=true;
                        Check(canvas.Heat&&!slider.Enabled,"o mapa de diferenças pausa o divisor");
                        differences.Checked=false;
                        Check(!canvas.Heat&&slider.Enabled&&canvas.Split==50,"a comparação retoma a posição escolhida");
                        var selector=Children(form).OfType<CheckBox>().Single(c=>c.Text=="Corpo inteiro");
                        selector.Checked=true;
                        Check(File.Exists(Path.Combine(output,"corpo-diferencas.png")),"visão de detalhe e de corpo inteiro disponíveis");
                        form.PerformLayout();
                        using(var shot=new Bitmap(form.Width,form.Height)) {
                            form.DrawToBitmap(shot,new Rectangle(0,0,shot.Width,shot.Height));
                            shot.Save(Path.Combine(proof,"interface.png"),System.Drawing.Imaging.ImageFormat.Png);
                        }
                        var second=Optimizer.Run(fixture,target,new RunOptions{Thorough=true},null,null);
                        Check(second.ReservedPassed.HasValue && auditNote.Text.Contains(second.ReservedViews.ToString()) &&
                            row.SubItems[1].Text==(second.ReservedPassed.Value?"Concluído":"Revisar visual"),
                            "o estado da fila corresponde à auditoria reservada");
                        Check(!String.Equals(output,second.Directory,StringComparison.OrdinalIgnoreCase),"nova execução não substitui resultado anterior");
                        Check(Optimizer.HashFile(firstGame)==Optimizer.HashFile(second.GameFile),"malha e exportação reproduzíveis");
                        form.ShowReservedAudit(new RunResult {ReservedPassed=false,ReservedViews=30,ReservedFailedViews=2});
                        Check(auditNote.Text.Contains("2 de 30") && auditNote.Text.Contains("reprovada") &&
                            auditNote.Text.Contains("auditoria-reservada.png") && auditNote.Text.Contains("cópia exata"),
                            "uma reprovação informa as vistas, o PNG e a cópia exata");
                        form.ShowReservedAudit(new RunResult {ReservedPassed=false,ReservedViews=30});
                        Check(auditNote.Text.Contains("limite global"),"reprovação global sem vistas individuais fora dos limites");
                        form.ShowReservedAudit(new RunResult {ReservedPassed=true,ReservedViews=30});
                        Check(auditNote.Text.Contains("aprovada em 30 vistas"),"aprovação reservada identificada na interface");
                        form.ShowReservedAudit(deepRun);
                        Check(String.IsNullOrEmpty(auditNote.Text),"validação rápida não exibe um resultado reservado");
                        form.AddFiles(new[]{bad});
                        list.Items[1].Selected=true;
                        Check(list.SelectedItems.Count==1 && list.SelectedItems[0]==list.Items[1] &&
                            list.Items[1].SubItems[1].Text=="Na fila" && OwnWindowVisible(folderButton) && folderButton.Enabled,
                            "selecionar um modelo pendente mantém o último resultado disponível");
                        ClickButton(folderButton);
                        Check(openedFolders.SequenceEqual(new[] { gameFolder, gameFolder }, StringComparer.OrdinalIgnoreCase),
                            "sem resultado na seleção, o clique abre a pasta da última publicação");
                        ClickButton(Children(form).OfType<Button>().Single(c=>c.Text=="Limpar"));
                        Check(list.Items.Count==0 && !OwnWindowVisible(folderButton), "Limpar remove o atalho do lote anterior");

                        // The exact export is standalone, including when the input has companions.
                        string alias = Path.Combine(scratch, "atalho-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mdl");
                        File.Copy(DiskPath.Full(deepRun.ExactFile), alias);
                        string exactTarget = Path.Combine(scratch, "gui-exata");
                        path.Text = exactTarget;
                        Children(form).OfType<RadioButton>().Single(c=>c.Text=="Rápida").Checked = true;
                        Children(form).OfType<CheckBox>().Single(c=>c.Text=="Somente cópia exata").Checked = true;
                        form.AddFiles(new[] { fixture, alias });
                        await form.StartAsync();
                        var exactFiles = Directory.GetFiles(exactTarget, "*_exato.mdl", SearchOption.AllDirectories);
                        string aliasExport = Path.GetFileNameWithoutExtension(alias) + "_exato.mdl";
                        string lastExact = Path.GetDirectoryName(exactFiles.Single(p=>Path.GetFileName(p)==aliasExport));
                        string firstExact = Path.GetDirectoryName(exactFiles.Single(p=>Path.GetFileName(p)!=aliasExport));
                        ClickButton(folderButton);
                        list.Items[0].Selected = true;
                        ClickButton(folderButton);
                        Check(openedFolders.SequenceEqual(new[] { gameFolder, gameFolder, lastExact, firstExact }, StringComparer.OrdinalIgnoreCase),
                            "no modo exato, cada modelo selecionado abre sua própria pasta de MDL");
                        ClickButton(Children(form).OfType<Button>().Single(c=>c.Text=="Limpar"));
                        form.AddFiles(new[] { bad });
                        await form.StartAsync();
                        language.SelectedIndex = 2;
                        Check(list.Items[0].SubItems[1].Text == "Failed" &&
                            list.Items[0].ToolTipText.Contains("Invalid MDL") &&
                            Children(form).OfType<Label>().Any(c => c.Text == list.Items[0].ToolTipText),
                            "o erro anterior e o detalhe da fila são traduzidos após a falha");
                    } catch(Exception e) {Console.Error.WriteLine(e);status=1;}
                    finally {Application.ExitThread();}
                }));
                Application.Run();
            }
            return status;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
