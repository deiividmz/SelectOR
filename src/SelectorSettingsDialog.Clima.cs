// Ajustes de SelectOR → Clima (ClimaFiles.cs): activar el clima de SelectOR (de serie, desactivado), elegir los grupos
// (nubes, lluvia, nieve, brillo de las luces, cielo nocturno) y ver cada imagen de Open Rails al lado de la de SelectOR.
// El administrador, además, cambia las imágenes del clima sin publicar versión: añade imágenes (o una carpeta entera:
// solo las que tienen el nombre de una imagen de clima de Open Rails y no son iguales a la suya), las quita y publica el
// clima firmado con la clave de este equipo.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class SelectorSettingsDialog
    {
        ThemeCheck _chkClima;
        Label _lblClimaVer, _lblClimaStatus, _lblClimaAdmin;
        ClimaGroupList _climaGroups;
        ClimaPreview _climaPreview;
        ClimaAdminList _climaAdminList;
        RoundButton _btnClimaPublish;
        Panel _climaAdminBox, _climaAdminStrip; Label _lblClimaAdminSum;
        ClimaFiles.Manifest _climaPub;                      // el publicado (o el último descargado)
        List<ClimaFiles.Item> _climaDraft = new();          // lo que va a publicar el administrador
        bool _climaBusy;

        Panel BuildClimaPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Px(20), Theme.Px(12), Theme.Px(20), Theme.Px(8)), Visible = false };

            var top = new Panel { Dock = DockStyle.Top, Height = Theme.Px(40), BackColor = Theme.Bg };
            _chkClima = new ThemeCheck { Text = I18n.T("Usar el clima de SelectOR en Open Rails"), Checked = _prefs.ClimaEnabled, AutoSize = false, Location = new Point(0, Theme.Px(6)), Font = Theme.Font(10f, FontStyle.Bold) };
            _chkClima.Size = new Size(TextRenderer.MeasureText(_chkClima.Text, _chkClima.Font).Width + Theme.Px(40), Theme.Px(28));
            _chkClima.CheckedChanged += async (s, e) =>
            {
                _prefs.ClimaEnabled = _chkClima.Checked; Save(); _climaGroups.Enabled2 = _chkClima.Checked; _climaGroups.Invalidate(); ClimaCloudRefresh();
                if (_chkClima.Checked) await ClimaSync();
            };
            _lblClimaVer = new Label { AutoSize = true, ForeColor = Theme.Subtle, BackColor = Theme.Bg, Font = Theme.Font(8.5f), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            top.Controls.Add(_chkClima); top.Controls.Add(_lblClimaVer);
            top.Layout += (s, e) => _lblClimaVer.Location = new Point(top.ClientSize.Width - _lblClimaVer.PreferredWidth, Theme.Px(12));

            var info = new Label
            {
                Text = I18n.T("Nubes, lluvia, nieve, brillo de las luces y cielo nocturno. Se ponen en la carpeta Content de Open Rails solo mientras conduces desde SelectOR y al volver se devuelven las de Open Rails. Vale desde la próxima vez que conduzcas."),
                Dock = DockStyle.Top, Height = Theme.Px(40), ForeColor = Theme.Subtle, BackColor = Theme.Bg, Font = Theme.Font(8.75f)
            };
            _lblClimaStatus = new Label { Dock = DockStyle.Top, Height = Theme.Px(24), ForeColor = Theme.AccentHi, BackColor = Theme.Bg, Font = Theme.Font(9f, FontStyle.Bold) };

            var cols = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Bg, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, Theme.Px(4), 0, 0) };
            cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44)); cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
            var left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(0, 0, Theme.Px(12), 0) };
            _climaGroups = new ClimaGroupList { Dock = DockStyle.Top, Height = Theme.Px(5 * 44 + 4), Enabled2 = _prefs.ClimaEnabled };
            _climaGroups.Toggled += id =>
            {
                _prefs.ClimaGroups ??= new List<string>();
                if (!_prefs.ClimaGroups.Remove(id)) _prefs.ClimaGroups.Add(id);
                Save(); ClimaRefresh();
            };
            _climaGroups.Picked += id => { _climaPreview.ShowGroup(id, ClimaShownEffective()); };

            // administrador: un botón que abre su ventana (las imágenes del clima y publicar; ClimaAdminDialog)
            _climaAdminStrip = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = Supa.IsSuperadmin, Padding = new Padding(0, Theme.Px(14), 0, 0) };
            var ah = new Label { Text = I18n.T("ADMINISTRADOR"), Dock = DockStyle.Top, Height = Theme.Px(24), ForeColor = Color.FromArgb(255, 183, 77), BackColor = Theme.Bg, Font = Theme.Font(8.5f, FontStyle.Bold) };
            var manage = DialogKit.Fit(DialogKit.Secondary(I18n.T("Gestionar el clima…")), 200); manage.Height = Theme.Px(34); manage.Dock = DockStyle.Top;
            manage.Click += (s, e) => ClimaAdminDialog();
            _lblClimaAdminSum = new Label { Dock = DockStyle.Top, Height = Theme.Px(40), ForeColor = Theme.Subtle, BackColor = Theme.Bg, Font = Theme.Font(8.5f), Padding = new Padding(0, Theme.Px(6), 0, 0) };
            _climaAdminStrip.Controls.Add(_lblClimaAdminSum); _climaAdminStrip.Controls.Add(manage); _climaAdminStrip.Controls.Add(ah);

            // (la ventana del administrador: lista, botones y su aviso; se crean una vez y se ponen en ella al abrirla)
            _climaAdminBox = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Px(20), Theme.Px(12), Theme.Px(20), Theme.Px(8)) };
            _climaAdminList = new ClimaAdminList { Dock = DockStyle.Fill };
            var abtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Theme.Px(44), BackColor = Theme.Bg, WrapContents = false, Padding = new Padding(0, Theme.Px(8), 0, 0) };
            RoundButton Btn(string t, bool primary = false)
            {
                var b = DialogKit.Fit(primary ? DialogKit.Primary(I18n.T(t)) : DialogKit.Secondary(I18n.T(t)), 90);
                b.Height = Theme.Px(30); b.Margin = new Padding(0, 0, Theme.Px(6), 0); abtns.Controls.Add(b); return b;
            }
            Btn("Añadir imágenes…").Click += (s, e) => ClimaAddFiles(false);
            Btn("Añadir una carpeta…").Click += (s, e) => ClimaAddFiles(true);
            Btn("Añadir nubes…").Click += (s, e) => ClimaAddClouds();   // (1.2.77) texturas de nubes a elegir
            Btn("Quitar").Click += (s, e) => ClimaRemoveSelected();
            _btnClimaPublish = Btn("Publicar", true);
            _btnClimaPublish.Click += async (s, e) => await ClimaPublish();
            _lblClimaAdmin = new Label { Dock = DockStyle.Bottom, Height = Theme.Px(40), ForeColor = Theme.Subtle, BackColor = Theme.Bg, Font = Theme.Font(8.75f), Padding = new Padding(0, Theme.Px(6), 0, 0) };
            _climaAdminBox.Controls.Add(_climaAdminList); _climaAdminBox.Controls.Add(_lblClimaAdmin); _climaAdminBox.Controls.Add(abtns);
            left.Controls.Add(_climaAdminStrip); left.Controls.Add(BuildClimaCloudPicker()); left.Controls.Add(_climaGroups);   // (1.2.77) nubes a elegir, debajo de los grupos

            _climaPreview = new ClimaPreview { Dock = DockStyle.Fill, Margin = new Padding(Theme.Px(12), 0, 0, 0) };
            cols.Controls.Add(left, 0, 0); cols.Controls.Add(_climaPreview, 1, 0);

            page.Controls.Add(cols); page.Controls.Add(_lblClimaStatus); page.Controls.Add(info); page.Controls.Add(top);
            _climaPub = ClimaFiles.LoadCached();
            _climaDraft = _climaPub?.files.ToList() ?? new List<ClimaFiles.Item>();
            _climaDraftClouds = _climaPub?.clouds?.ToList() ?? new List<ClimaFiles.Cloud>();
            ClimaRefresh();
            page.VisibleChanged += async (s, e) => { if (page.Visible) await ClimaLoad(); };
            return page;
        }

        // Lo que se ve: el borrador del administrador o el publicado.
        List<ClimaFiles.Item> ClimaShown() => Supa.IsSuperadmin ? _climaDraft : _climaPub?.files ?? new List<ClimaFiles.Item>();

        bool _climaLoaded;
        async Task ClimaLoad()
        {
            if (_climaLoaded || _climaBusy) return;
            _climaLoaded = true;
            if (_prefs.ClimaEnabled) { await ClimaSync(); return; }
            // sin activar: solo qué hay publicado (sin descargar las imágenes)
            var (text, sig, _, err) = await ClimaFiles.FetchAsync();
            if (IsDisposed) return;
            if (err == null && text != null)
            {
                var m = ClimaFiles.Parse(text, sig);
                if (m != null) { bool d0 = ClimaDirty(); _climaPub = m; if (!d0) { _climaDraft = m.files.ToList(); _climaDraftClouds = m.clouds?.ToList() ?? new List<ClimaFiles.Cloud>(); } }
            }
            _lblClimaStatus.Text = err != null && err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0 ? I18n.T("El servidor aún no tiene el clima.") : "";
            ClimaRefresh();
        }

        async Task ClimaSync()
        {
            if (_climaBusy) return;
            _climaBusy = true;
            try
            {
                _lblClimaStatus.ForeColor = Theme.AccentHi; _lblClimaStatus.Text = I18n.T("Comprobando el clima…");
                var (m, err) = await ClimaFiles.SyncAsync((d, t) => { try { BeginInvoke((Action)(() => { if (t > 0 && d < t) _lblClimaStatus.Text = string.Format(I18n.T("Descargando el clima: {0} de {1}…"), d + 1, t); })); } catch { } }, _prefs.ClimaClouds);
                if (IsDisposed) return;
                bool wasDirty = ClimaDirty();
                if (m != null) _climaPub = m;
                if (!wasDirty) { _climaDraft = _climaPub?.files.ToList() ?? new List<ClimaFiles.Item>(); _climaDraftClouds = _climaPub?.clouds?.ToList() ?? new List<ClimaFiles.Cloud>(); }
                _lblClimaStatus.ForeColor = err != null ? Color.FromArgb(229, 115, 115) : Theme.AccentHi;
                _lblClimaStatus.Text = err != null ? (err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0 ? I18n.T("El servidor aún no tiene el clima.") : err)
                                     : _climaPub == null ? I18n.T("Todavía no hay clima publicado.") : I18n.T("Clima al día.");
                ClimaRefresh();
            }
            finally { _climaBusy = false; }
        }

        bool ClimaDirty()
        {
            var a = (_climaPub?.files ?? new List<ClimaFiles.Item>()).Select(f => f.name + ":" + f.sha).OrderBy(x => x);
            var b = _climaDraft.Select(f => f.name + ":" + f.sha).OrderBy(x => x);
            return !a.SequenceEqual(b) || ClimaCloudsDirty();
        }

        void ClimaRefresh()
        {
            if (_climaGroups == null) return;
            var shown = ClimaShown();
            _lblClimaVer.Text = _climaPub == null ? I18n.T("Sin clima publicado")
                : string.Format(I18n.T("Clima n.º {0} · {1}"), _climaPub.ver, DateTime.TryParse(_climaPub.date, out var d) ? d.ToLocalTime().ToString("d MMM yyyy", I18n.English ? System.Globalization.CultureInfo.GetCultureInfo("en-GB") : System.Globalization.CultureInfo.GetCultureInfo("es-ES")) : "");
            _lblClimaVer.Parent?.PerformLayout();
            _climaGroups.Rows = ClimaFiles.Groups.Select(g =>
            {
                var fs = shown.Where(f => ClimaFiles.GroupOf(f.name) == g.id).ToList();
                return new ClimaGroupList.Row { Id = g.id, Label = I18n.T(g.label), Count = fs.Count, Changed = fs.Count(f => !ClimaFiles.SameAsOriginal(f)), Bytes = fs.Sum(f => f.size), On = _prefs.ClimaGroups?.Contains(g.id) == true };
            }).ToList();
            _climaGroups.Invalidate();
            _climaPreview.ShowGroup(_climaPreview.Group ?? _climaGroups.Rows.FirstOrDefault(r => r.Count > 0)?.Id ?? "clouds", ClimaShownEffective());
            ClimaCloudRefresh();
            _climaAdminStrip.Visible = Supa.IsSuperadmin;
            if (Supa.IsSuperadmin)
            {
                var pub = new HashSet<string>((_climaPub?.files ?? new List<ClimaFiles.Item>()).Select(f => f.name + ":" + f.sha));
                var names = new HashSet<string>(_climaDraft.Select(f => f.name));
                _climaAdminList.Rows = _climaDraft.OrderBy(f => f.name).Select(f => new ClimaAdminList.Row
                                       { Name = f.name, Group = I18n.T(ClimaFiles.Groups.First(g => g.id == ClimaFiles.GroupOf(f.name)).label), Bytes = f.size, State = pub.Contains(f.name + ":" + f.sha) ? 0 : 1, Same = ClimaFiles.SameAsOriginal(f) })
                                   .Concat((_climaPub?.files ?? new List<ClimaFiles.Item>()).Where(f => !names.Contains(f.name)).Select(f => new ClimaAdminList.Row
                                       { Name = f.name, Group = I18n.T(ClimaFiles.Groups.First(g => g.id == ClimaFiles.GroupOf(f.name)).label), Bytes = f.size, State = 2 }))
                                   .ToList();
                // (1.2.77) las texturas de nubes a elegir
                var pubC = new HashSet<string>((_climaPub?.clouds ?? new List<ClimaFiles.Cloud>()).Select(c => c.sha));
                var draftC = new HashSet<string>(_climaDraftClouds.Select(c => c.sha));
                _climaAdminList.Rows.AddRange(_climaDraftClouds.Select(c => new ClimaAdminList.Row { Name = "☁ " + c.label, Group = I18n.T("Nubes a elegir"), Bytes = c.size, State = pubC.Contains(c.sha) ? 0 : 1, CloudSha = c.sha }));
                _climaAdminList.Rows.AddRange((_climaPub?.clouds ?? new List<ClimaFiles.Cloud>()).Where(c => !draftC.Contains(c.sha)).Select(c => new ClimaAdminList.Row { Name = "☁ " + c.label, Group = I18n.T("Nubes a elegir"), Bytes = c.size, State = 2, CloudSha = c.sha }));
                _climaAdminList.Invalidate();
                bool dirty = ClimaDirty();
                _btnClimaPublish.Enabled = dirty && !_climaBusy;
                _lblClimaAdminSum.Text = string.Format(I18n.T("{0} imagen(es) en el clima"), _climaDraft.Count) + (_climaDraftClouds.Count > 0 ? " · " + string.Format(I18n.T("{0} nubes a elegir"), _climaDraftClouds.Count) : "") + (dirty ? " · " + I18n.T("hay cambios sin publicar") : "");
                _lblClimaAdmin.Text = !Updater.HasSigningKey ? I18n.T("Este equipo no tiene la clave de firma: aquí no se puede publicar el clima.")
                                    : dirty ? I18n.T("Hay cambios sin publicar.") : I18n.T("Todo publicado.");
            }
        }

        // Administrador: añadir imágenes (sueltas o una carpeta). Solo las de nombre conocido y distintas de las de Open Rails.
        async void ClimaAddFiles(bool folder)
        {
            if (_climaBusy || !Supa.IsSuperadmin) return;
            var paths = new List<string>();
            if (folder)
            {
                using var fd = new FolderBrowserDialog { Description = I18n.T("Carpeta con las imágenes del clima (con los nombres de las de Content de Open Rails)"), UseDescriptionForTitle = true };
                if (fd.ShowDialog(this) != DialogResult.OK) return;
                paths.AddRange(Directory.GetFiles(fd.SelectedPath).Where(p => ClimaFiles.CanonicalName(Path.GetFileName(p)) != null));
            }
            else
            {
                using var od = new OpenFileDialog { Multiselect = true, Filter = I18n.T("Imágenes de clima") + " (*.png;*.bmp)|*.png;*.bmp", Title = I18n.T("Imágenes del clima") };
                if (od.ShowDialog(this) != DialogResult.OK) return;
                paths.AddRange(od.FileNames);
            }
            await ClimaAddPaths(paths);
        }

        public async Task ClimaAddPaths(List<string> paths)
        {
            if (_climaBusy || !Supa.IsSuperadmin) return;
            _climaBusy = true;
            int added = 0, same = 0; var bad = new List<string>();
            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    string p = paths[i], name = ClimaFiles.CanonicalName(Path.GetFileName(p));
                    _lblClimaAdmin.Text = string.Format(I18n.T("Subiendo {0} ({1} de {2})…"), Path.GetFileName(p), i + 1, paths.Count);
                    if (name == null) { bad.Add(Path.GetFileName(p)); continue; }
                    byte[] data;
                    try { data = await File.ReadAllBytesAsync(p); } catch { bad.Add(name); continue; }
                    string orig = ClimaFiles.OriginalPath(name);
                    if (orig != null && File.ReadAllBytes(orig).AsSpan().SequenceEqual(data)) same++;   // (va igual: el juego completo de cada grupo)
                    var (item, err) = await ClimaFiles.UploadAsync(name, data);
                    if (err != null) { bad.Add(name + " (" + err + ")"); continue; }
                    _climaDraft.RemoveAll(f => f.name == name); _climaDraft.Add(item); added++;
                }
            }
            finally { _climaBusy = false; }
            ClimaRefresh();
            var msg = string.Format(I18n.T("{0} añadida(s)."), added);
            if (same > 0) msg += " " + string.Format(I18n.T("({0} igual(es) que las de Open Rails)"), same);
            if (bad.Count > 0) msg += " " + I18n.T("No valen:") + " " + string.Join(", ", bad.Take(4)) + (bad.Count > 4 ? "…" : "");
            _lblClimaAdmin.Text = msg;
        }

        void ClimaRemoveSelected()
        {
            var r = _climaAdminList.Selected;
            if (r == null) { _lblClimaAdmin.Text = I18n.T("Elige una imagen de la lista."); return; }
            if (r.CloudSha != null)
            {
                if (r.State == 2) _climaDraftClouds.Add(_climaPub.clouds.First(c => c.sha == r.CloudSha));   // (quitada: vuelve)
                else _climaDraftClouds.RemoveAll(c => c.sha == r.CloudSha);
                ClimaRefresh(); return;
            }
            if (r.State == 2) { var f = _climaPub.files.First(x => x.name == r.Name); _climaDraft.Add(f); }   // (quitada: vuelve)
            else _climaDraft.RemoveAll(f => f.name == r.Name);
            ClimaRefresh();
        }

        public async Task ClimaPublish(bool ask = true)
        {
            if (_climaBusy || !ClimaDirty()) return;
            if (ask && ThemedBox.Show(this, string.Format(I18n.T("¿Publicar el clima con {0} imagen(es)? Lo recibirán todos los usuarios que tengan el clima activado."), _climaDraft.Count),
                                      "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _climaBusy = true; _btnClimaPublish.Enabled = false;
            _lblClimaAdmin.Text = I18n.T("Publicando…");
            try
            {
                var (ver, err) = await ClimaFiles.PublishAsync(_climaDraft, _climaDraftClouds);
                if (err != null) { _lblClimaAdmin.Text = err; return; }
                _climaPub = ClimaFiles.LoadCached();
                _climaDraft = _climaPub?.files.ToList() ?? _climaDraft;
                _climaDraftClouds = _climaPub?.clouds?.ToList() ?? new List<ClimaFiles.Cloud>();
                _lblClimaStatus.ForeColor = Theme.AccentHi; _lblClimaStatus.Text = string.Format(I18n.T("Clima n.º {0} publicado."), ver);
            }
            finally { _climaBusy = false; ClimaRefresh(); }
        }

        // Administrador: la ventana con las imágenes del clima (añadir, quitar y publicar).
        void ClimaAdminDialog()
        {
            using var dlg = new Form
            {
                Text = I18n.T("Gestionar el clima"), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, ClientSize = new Size(Theme.Px(720), Theme.Px(440)), BackColor = Theme.Bg
            };
            DialogKit.Dress(dlg);
            var foot = new DialogKit.Footer();
            var close = DialogKit.Primary(I18n.T("Cerrar"), 130); close.Click += (s, e) => dlg.Close();
            foot.AddRight(close);
            dlg.Controls.Add(_climaAdminBox); dlg.Controls.Add(foot);
            DialogKit.Crown(dlg, I18n.T("Gestionar el clima"), I18n.T("Lo que reciben los usuarios con el clima activado."), "⛅");
            dlg.FormClosing += (s, e) => dlg.Controls.Remove(_climaAdminBox);   // (se queda para la próxima vez)
            ClimaRefresh();
            dlg.ShowDialog(this);
        }

        // ---------------------------------------------------------------- grupos (casilla + nombre + imágenes)
        sealed class ClimaGroupList : Control
        {
            public sealed class Row { public string Id, Label; public int Count, Changed; public long Bytes; public bool On; }
            public List<Row> Rows = new();
            public bool Enabled2 = true;
            public string Current;
            public event Action<string> Toggled, Picked;
            int _hover = -1;
            public ClimaGroupList() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Theme.Bg; }
            Rectangle RowRect(int i) => new Rectangle(0, i * Theme.Px(44), Width - 1, Theme.Px(40));
            Rectangle BoxRect(int i) { var r = RowRect(i); return new Rectangle(r.X + Theme.Px(12), r.Y + (r.Height - Theme.Px(18)) / 2, Theme.Px(18), Theme.Px(18)); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
                using var f = Theme.Font(9.5f, FontStyle.Bold); using var fs = Theme.Font(8.5f);
                for (int i = 0; i < Rows.Count; i++)
                {
                    var r = RowRect(i); var row = Rows[i];
                    bool cur = row.Id == Current;
                    using (var p = Theme.Round(r, 8)) using (var b = new SolidBrush(cur ? Color.FromArgb(40, 60, 46) : i == _hover ? Theme.Surface2 : Theme.Surface)) g.FillPath(b, p);
                    if (cur) Theme.DrawRoundBorder(g, Rectangle.Inflate(r, -1, -1), 8, Theme.Accent, 1.4f);
                    var bx = BoxRect(i);
                    bool on = row.On && row.Count > 0;
                    using (var p = Theme.Round(bx, 4))
                    {
                        using (var b = new SolidBrush(on && Enabled2 ? Theme.Accent : Theme.Surface2)) g.FillPath(b, p);
                        using (var pen = new Pen(on && Enabled2 ? Theme.Accent : Theme.Border, 1.2f)) g.DrawPath(pen, p);
                    }
                    if (row.On) using (var pen = new Pen(Enabled2 && row.Count > 0 ? Color.White : Theme.Subtle, 2f)) g.DrawLines(pen, new[] { new PointF(bx.X + bx.Width * .22f, bx.Y + bx.Height * .52f), new PointF(bx.X + bx.Width * .43f, bx.Y + bx.Height * .72f), new PointF(bx.X + bx.Width * .78f, bx.Y + bx.Height * .3f) });
                    int tx = bx.Right + Theme.Px(12);
                    TextRenderer.DrawText(g, row.Label, f, new Rectangle(tx, r.Y, r.Width / 2, r.Height), Enabled2 && row.Count > 0 ? Theme.Text : Theme.Subtle, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    string sub = row.Count == 0 ? I18n.T("sin cambios") : string.Format(I18n.T(row.Count == 1 ? "{0} imagen · {1}" : "{0} imágenes · {1}"), row.Count, FmtBytes(row.Bytes));
                    if (row.Count > 1 && row.Changed == 0) sub = string.Format(I18n.T("{0} imágenes (todas iguales) · {1}"), row.Count, FmtBytes(row.Bytes));
                    else if (row.Count > 1 && row.Changed < row.Count) sub = string.Format(I18n.T(row.Changed == 1 ? "{0} imágenes (1 distinta) · {1}" : "{0} imágenes ({2} distintas) · {1}"), row.Count, FmtBytes(row.Bytes), row.Changed);
                    else if (row.Count == 1 && row.Changed == 0) sub = string.Format(I18n.T("1 imagen (igual) · {0}"), FmtBytes(row.Bytes));
                    TextRenderer.DrawText(g, sub, fs, new Rectangle(r.X, r.Y, r.Width - Theme.Px(12), r.Height), Theme.Subtle, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
                }
            }
            int At(Point p) { for (int i = 0; i < Rows.Count; i++) if (RowRect(i).Contains(p)) return i; return -1; }
            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = At(e.Location); Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; if (h != _hover) { _hover = h; Invalidate(); } }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }
            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                int i = At(e.Location); if (i < 0) return;
                if (Rectangle.Inflate(BoxRect(i), Theme.Px(8), Theme.Px(10)).Contains(e.Location)) { if (Enabled2) Toggled?.Invoke(Rows[i].Id); }
                Current = Rows[i].Id; Picked?.Invoke(Current); Invalidate();
            }
            public void ClickBox(string id) => Toggled?.Invoke(id);   // (pruebas)
        }

        static string FmtBytes(long b) => b >= 1024 * 1024 ? (b / 1048576.0).ToString("0.0", I18n.English ? System.Globalization.CultureInfo.GetCultureInfo("en-GB") : System.Globalization.CultureInfo.GetCultureInfo("es-ES")) + " MB" : Math.Max(1, b / 1024) + " KB";

        // ---------------------------------------------------------------- vista previa: Open Rails | SelectOR
        sealed class ClimaPreview : Control
        {
            public string Group;
            List<ClimaFiles.Item> _files = new();
            int _idx;
            Bitmap _a, _b; string _aPath, _bPath;
            Rectangle _prev, _next;
            public ClimaPreview() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Theme.Bg; }
            public string CurrentName => _idx >= 0 && _idx < _files.Count ? _files[_idx].name : null;
            public void ShowGroup(string group, List<ClimaFiles.Item> all)
            {
                string keep = Group == group ? CurrentName : null;
                Group = group;
                _files = all.Where(f => ClimaFiles.GroupOf(f.name) == group).OrderBy(f => f.name).ToList();
                _idx = Math.Max(0, keep == null ? 0 : _files.FindIndex(f => f.name == keep));
                Load(); Invalidate();
            }
            void Load()
            {
                var f = _idx < _files.Count ? _files[_idx] : null;
                string a = f == null ? null : ClimaFiles.OriginalPath(f.name), b = f == null ? null : ClimaFiles.CachedPath(f);
                if (a != _aPath) { _a?.Dispose(); _a = Thumb(a); _aPath = a; }
                if (b != _bPath) { _b?.Dispose(); _b = Thumb(b); _bPath = b; }
            }
            static Bitmap Thumb(string path)
            {
                if (path == null) return null;
                try
                {
                    using var img = Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));
                    double k = Math.Min(1, 520.0 / Math.Max(img.Width, img.Height));
                    var bmp = new Bitmap(Math.Max(1, (int)(img.Width * k)), Math.Max(1, (int)(img.Height * k)));
                    using var g = Graphics.FromImage(bmp); g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(img, 0, 0, bmp.Width, bmp.Height);
                    return bmp;
                }
                catch { return null; }
            }
            protected override void Dispose(bool disposing) { if (disposing) { _a?.Dispose(); _b?.Dispose(); } base.Dispose(disposing); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
                var box = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var p = Theme.Round(box, 10)) using (var br = new SolidBrush(Theme.Surface)) g.FillPath(br, p);
                using var f = Theme.Font(9f); using var fb = Theme.Font(9f, FontStyle.Bold); using var fs = Theme.Font(8.5f, FontStyle.Bold);
                _prev = _next = Rectangle.Empty;
                if (_files.Count == 0)
                {
                    TextRenderer.DrawText(g, I18n.T("El clima de SelectOR no cambia este grupo: se ven las imágenes de Open Rails."), f, Rectangle.Inflate(box, -24, -24), Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                    return;
                }
                // cabecera: el nombre y, si hay varias, flechas
                var head = new Rectangle(box.X + 12, box.Y + 8, box.Width - 24, Theme.Px(28));
                string t = _files[_idx].name + (_files.Count > 1 ? "   (" + (_idx + 1) + " / " + _files.Count + ")" : "");
                TextRenderer.DrawText(g, t, fb, head, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                if (_files.Count > 1)
                {
                    _prev = new Rectangle(head.X, head.Y, Theme.Px(34), head.Height); _next = new Rectangle(head.Right - Theme.Px(34), head.Y, Theme.Px(34), head.Height);
                    TextRenderer.DrawText(g, "◀", fb, _prev, Theme.AccentHi, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    TextRenderer.DrawText(g, "▶", fb, _next, Theme.AccentHi, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                int top = head.Bottom + 6, gap = 12, w = (box.Width - 24 - gap) / 2, h = box.Height - top - Theme.Px(34);
                Pane(g, new Rectangle(box.X + 12, top, w, h), _a, I18n.T("Open Rails"), Theme.Subtle, fs, f);
                bool same = ClimaFiles.SameAsOriginal(_files[_idx]);
                Pane(g, new Rectangle(box.X + 12 + w + gap, top, w, h), _b, same ? I18n.T("SelectOR · igual que la de Open Rails") : I18n.T("SelectOR"), same ? Theme.Subtle : Theme.AccentHi, fs, f);
            }
            static void Pane(Graphics g, Rectangle r, Bitmap img, string label, Color lc, Font fs, Font f)
            {
                // tablero (las imágenes de lluvia, nieve y brillos son casi todo transparencia)
                int c = Theme.Px(10);
                using (var b1 = new SolidBrush(Color.FromArgb(42, 46, 50))) using (var b2 = new SolidBrush(Color.FromArgb(58, 62, 66)))
                    for (int y = r.Y; y < r.Bottom; y += c) for (int x = r.X; x < r.Right; x += c)
                        g.FillRectangle(((x - r.X) / c + (y - r.Y) / c) % 2 == 0 ? b1 : b2, x, y, Math.Min(c, r.Right - x), Math.Min(c, r.Bottom - y));
                if (img != null)
                {
                    double k = Math.Min((r.Width - 8) / (double)img.Width, (r.Height - 8) / (double)img.Height);
                    int w = Math.Max(1, (int)(img.Width * k)), h = Math.Max(1, (int)(img.Height * k));
                    g.InterpolationMode = k > 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
                    g.DrawImage(img, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
                }
                else TextRenderer.DrawText(g, I18n.T("(sin descargar)"), f, r, Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, label, fs, new Rectangle(r.X, r.Bottom + 6, r.Width, 20), lc, TextFormatFlags.HorizontalCenter);
            }
            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (_files.Count < 2) return;
                if (_prev.Contains(e.Location)) _idx = (_idx + _files.Count - 1) % _files.Count;
                else if (_next.Contains(e.Location)) _idx = (_idx + 1) % _files.Count;
                else return;
                Load(); Invalidate();
            }
            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); Cursor = _prev.Contains(e.Location) || _next.Contains(e.Location) ? Cursors.Hand : Cursors.Default; }
        }

        // ---------------------------------------------------------------- lista del administrador
        sealed class ClimaAdminList : Control
        {
            public sealed class Row { public string Name, Group; public long Bytes; public int State; public bool Same; public string CloudSha; }   // 0 publicada · 1 nueva · 2 se quita · CloudSha: una textura de nubes a elegir
            public List<Row> Rows = new();
            int _sel = -1, _top;
            public Row Selected => _sel >= 0 && _sel < Rows.Count ? Rows[_sel] : null;
            public ClimaAdminList() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Theme.Bg; }
            int RowH => Theme.Px(28);
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
                var box = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var p = Theme.Round(box, 8)) using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, p);
                using var f = Theme.Font(9f); using var fs = Theme.Font(8.25f);
                if (Rows.Count == 0) { TextRenderer.DrawText(g, I18n.T("Sin imágenes: añade las del clima."), f, box, Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
                int vis = Math.Max(1, (Height - 8) / RowH); _top = Math.Max(0, Math.Min(_top, Rows.Count - vis));
                for (int i = _top; i < Rows.Count && i < _top + vis; i++)
                {
                    var r = new Rectangle(4, 4 + (i - _top) * RowH, Width - 9, RowH - 2); var row = Rows[i];
                    if (i == _sel) using (var p = Theme.Round(r, 6)) using (var b = new SolidBrush(Color.FromArgb(40, 60, 46))) g.FillPath(b, p);
                    var tf = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
                    TextRenderer.DrawText(g, row.Name, f, new Rectangle(r.X + 8, r.Y, r.Width * 30 / 100, r.Height), row.State == 2 ? Theme.Subtle : Theme.Text, tf);
                    TextRenderer.DrawText(g, row.Group + " · " + FmtBytes(row.Bytes) + (row.Same ? " · " + I18n.T("igual que la de Open Rails") : ""), fs, new Rectangle(r.X + r.Width * 31 / 100, r.Y, r.Width * 56 / 100, r.Height), Theme.Subtle, tf);
                    string st = row.State == 0 ? I18n.T("publicada") : row.State == 1 ? I18n.T("nueva") : I18n.T("se quita");
                    Color sc = row.State == 0 ? Theme.Subtle : row.State == 1 ? Theme.AccentHi : Color.FromArgb(229, 115, 115);
                    TextRenderer.DrawText(g, st, fs, new Rectangle(r.X, r.Y, r.Width - 8, r.Height), sc, tf | TextFormatFlags.Right);
                }
            }
            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); int i = _top + (e.Y - 4) / RowH; _sel = i >= 0 && i < Rows.Count ? i : -1; Invalidate(); }
            protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); _top = Math.Max(0, _top - Math.Sign(e.Delta)); Invalidate(); }
            public void Select(string name) { _sel = Rows.FindIndex(r => r.Name == name); Invalidate(); }   // (pruebas)
        }
    }
}
