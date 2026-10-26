// Ajustes de SelectOR (botón junto a «Opciones OR» y «Acerca de SelectOR»), en tres pestañas: Gráficos (ReShade;
// SelectorSettingsDialog.Graficos.cs), Clima (SelectorSettingsDialog.Clima.cs) y Mandos: los joysticks y
// cuadrantes conectados (en vivo), las palancas (regulador, frenos, inversor: «Asignar» y se mueve la palanca) y los
// botones («Asignar un botón»: se pulsa y se elige qué hace). Se guarda al momento (AppPrefs.JoyBindings) y vale para
// la próxima vez que se conduzca (MainMenuForm.Mandos.cs). Joysticks.cs: la lectura y las acciones.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class SelectorSettingsDialog : Form
    {
        readonly AppPrefs _prefs;
        readonly Timer _tick;
        List<Joy.Device> _devs = new();
        readonly Dictionary<string, Joy.State> _st = new();
        DateTime _enumAt = DateTime.MinValue;
        // captura: «Asignar» → la primera palanca que se mueva / el primer botón que se pulse
        string _capAxis; bool _capButton; DateTime _capUntil;
        readonly Dictionary<string, float[]> _capBase = new(); readonly Dictionary<string, ulong> _capBtnBase = new();
        string _status;
        readonly ThemeCheck _chkOn;
        readonly DevicesView _devView;
        readonly RowsView _axes, _buttons;
        readonly Label _lblStatus;
        readonly RoundButton _btnQuick, _btnAddButton;

        public List<JoyBinding> Bindings => _prefs.JoyBindings;

        readonly RoundButton _tabJoy, _tabGfx, _tabClima;
        Panel _pageJoy, _pageGfx, _pageClima;
        RoundButton _reset;   // (solo de los mandos)

        public SelectorSettingsDialog(AppPrefs prefs, string page = null)
        {
            _prefs = prefs;
            _prefs.JoyBindings ??= new List<JoyBinding>();
            Text = I18n.T("Ajustes de SelectOR");
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            // (alto: las 7 palancas a la vista; en pantallas pequeñas, lo que quepa, y la lista se mueve con la rueda)
            ClientSize = new Size(Theme.Px(1040), Math.Min(Theme.Px(800), Math.Max(Theme.Px(600), (Screen.PrimaryScreen?.WorkingArea.Height ?? 900) - 60)));
            DialogKit.Dress(this);
            KeyPreview = true;

            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var header = new DialogKit.Header(I18n.T("Ajustes de SelectOR"), I18n.T("Tus mandos para conducir, los gráficos y el clima de Open Rails"), "🎮");
            // pestañas
            var tabs = new Panel { Dock = DockStyle.Top, Height = Theme.Px(52), BackColor = Theme.Bg, Padding = new Padding(Theme.Px(20), Theme.Px(12), Theme.Px(20), 0) };
            _tabJoy = DialogKit.Fit(DialogKit.Secondary(I18n.T("Mandos")), 130); _tabGfx = DialogKit.Fit(DialogKit.Secondary(I18n.T("Gráficos")), 130);
            _tabClima = DialogKit.Fit(DialogKit.Secondary(I18n.T("Clima")), 130);
            _tabJoy.Height = _tabGfx.Height = _tabClima.Height = Theme.Px(36);
            _tabJoy.Location = new Point(Theme.Px(20), Theme.Px(12)); _tabGfx.Location = new Point(_tabJoy.Right + Theme.Px(8), Theme.Px(12));
            _tabClima.Location = new Point(_tabGfx.Right + Theme.Px(8), Theme.Px(12));
            _tabJoy.Click += (s, e) => ShowPage("mandos"); _tabGfx.Click += (s, e) => ShowPage("graficos"); _tabClima.Click += (s, e) => ShowPage("clima");
            tabs.Controls.Add(_tabJoy); tabs.Controls.Add(_tabGfx); tabs.Controls.Add(_tabClima);
            // (1.2.77) Sala: los tiempos del multijugador, solo el administrador (SelectorSettingsDialog.Sala.cs)
            if (Supa.IsSuperadmin)
            {
                _tabSala = DialogKit.Fit(DialogKit.Secondary(I18n.T("Sala")), 130); _tabSala.Height = Theme.Px(36);
                _tabSala.Location = new Point(_tabClima.Right + Theme.Px(8), Theme.Px(12));
                _tabSala.Click += (s, e) => ShowPage("sala");
                tabs.Controls.Add(_tabSala);
            }

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Px(20), Theme.Px(12), Theme.Px(20), Theme.Px(8)) };

            // fila de arriba: activar y buscar
            var top = new Panel { Dock = DockStyle.Top, Height = Theme.Px(40), BackColor = Theme.Bg };
            _chkOn = new ThemeCheck { Text = I18n.T("Usar mis mandos al conducir"), Checked = _prefs.JoyEnabled, AutoSize = false, Location = new Point(0, Theme.Px(6)), Font = Theme.Font(10f, FontStyle.Bold) };
            _chkOn.Size = new Size(TextRenderer.MeasureText(_chkOn.Text, _chkOn.Font).Width + Theme.Px(40), Theme.Px(28));
            _chkOn.CheckedChanged += (s, e) => { _prefs.JoyEnabled = _chkOn.Checked; Save(); };
            var btnFind = DialogKit.Fit(DialogKit.Secondary(I18n.T("Buscar mandos")), 140);
            btnFind.Height = Theme.Px(32); btnFind.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnFind.Click += (s, e) => { Enumerate(); _status = _devs.Count == 0 ? I18n.T("No hay ningún mando conectado.") : string.Format(I18n.T("{0} mando(s) conectado(s)."), _devs.Count); };
            top.Controls.Add(_chkOn); top.Controls.Add(btnFind);
            top.Layout += (s, e) => btnFind.Location = new Point(top.ClientSize.Width - btnFind.Width, Theme.Px(4));

            _devView = new DevicesView { Dock = DockStyle.Top, Height = Theme.Px(150), Owner2 = this };

            // dos columnas: palancas y botones
            var cols = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Bg, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, Theme.Px(10), 0, 0) };
            cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            var left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(0, 0, Theme.Px(10), 0) };
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(Theme.Px(10), 0, 0, 0) };
            var lblAx = SectionLabel(I18n.T("PALANCAS"));
            _btnQuick = DialogKit.Fit(DialogKit.Secondary(I18n.T("Cuadrante de 3 palancas: asignar")), 120);
            _btnQuick.Height = Theme.Px(30); _btnQuick.Dock = DockStyle.Bottom;
            _btnQuick.Click += (s, e) => QuickQuadrant();
            _axes = new RowsView { Dock = DockStyle.Fill };
            _axes.ChipClick += AxisChip;
            left.Controls.Add(_axes); left.Controls.Add(_btnQuick); left.Controls.Add(lblAx);

            var lblBt = SectionLabel(I18n.T("BOTONES"));
            _btnAddButton = DialogKit.Fit(DialogKit.Primary(I18n.T("Asignar un botón")), 160);
            _btnAddButton.Height = Theme.Px(34); _btnAddButton.Dock = DockStyle.Top;
            _btnAddButton.Click += (s, e) => StartCapture(null, true);
            var gap = new Panel { Dock = DockStyle.Top, Height = Theme.Px(8), BackColor = Theme.Bg };
            _buttons = new RowsView { Dock = DockStyle.Fill, Empty = I18n.T("Ningún botón asignado. Pulsa «Asignar un botón» y luego el botón del mando.") };
            _buttons.ChipClick += ButtonChip;
            right.Controls.Add(_buttons); right.Controls.Add(gap); right.Controls.Add(_btnAddButton); right.Controls.Add(lblBt);
            cols.Controls.Add(left, 0, 0); cols.Controls.Add(right, 1, 0);

            body.Controls.Add(cols); body.Controls.Add(_devView); body.Controls.Add(top);

            var footer = new DialogKit.Footer();
            var close = DialogKit.Primary(I18n.T("Cerrar"), 140);
            close.Click += (s, e) => Close();
            var reset = _reset = DialogKit.Secondary(I18n.T("Quitar todo"), 140);
            reset.Click += (s, e) =>
            {
                if (ThemedBox.Show(this, I18n.T("¿Quitar todas las asignaciones de palancas y botones?"), "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Bindings.Clear(); Save(); Rebuild();
            };
            _lblStatus = new Label { AutoSize = false, Width = Theme.Px(560), ForeColor = Theme.AccentHi, BackColor = Theme.Surface, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Font(9.5f, FontStyle.Bold) };
            footer.AddRight(close, reset);
            footer.AddLeft(_lblStatus);

            _pageJoy = body; _pageGfx = BuildGfxPage(); _pageClima = BuildClimaPage();
            if (_tabSala != null) { _pageSala = BuildSalaPage(); Controls.Add(_pageSala); }
            Controls.Add(_pageClima); Controls.Add(_pageGfx); Controls.Add(body); Controls.Add(footer); Controls.Add(tabs); Controls.Add(header); Controls.Add(stripe);
            ShowPage(page ?? "mandos");

            Enumerate();
            Rebuild();
            _tick = new Timer { Interval = 30 };
            _tick.Tick += (s, e) => Tick();
            _tick.Start();
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && (_capAxis != null || _capButton)) { StopCapture(I18n.T("Cancelado.")); e.Handled = true; } };
        }

        public void ShowPage(string page)
        {
            bool sala = page == "sala" && _pageSala != null, gfx = page == "graficos", clima = page == "clima", joy = !gfx && !clima && !sala;
            _pageGfx.Visible = gfx; _pageClima.Visible = clima; _pageJoy.Visible = joy;
            if (_pageSala != null) _pageSala.Visible = sala;
            if (_tabSala != null) { _tabSala.BaseColor = sala ? Theme.Accent : Theme.Surface2; _tabSala.HoverColor = sala ? Theme.AccentHi : Theme.SurfaceHi; _tabSala.TextColor = sala ? Color.White : Theme.Text; _tabSala.Invalidate(); }
            if (_reset != null) _reset.Visible = joy;
            foreach (var (b, on) in new[] { (_tabJoy, joy), (_tabGfx, gfx), (_tabClima, clima) })
            { b.BaseColor = on ? Theme.Accent : Theme.Surface2; b.HoverColor = on ? Theme.AccentHi : Theme.SurfaceHi; b.TextColor = on ? Color.White : Theme.Text; b.Invalidate(); }
            _status = null;
        }

        static Label SectionLabel(string t) => new Label { Text = t, Dock = DockStyle.Top, Height = Theme.Px(26), ForeColor = Theme.Subtle, BackColor = Theme.Bg, Font = Theme.Font(8.5f, FontStyle.Bold) };

        void Save() { try { _prefs.Save(); } catch { } }

        protected override void OnFormClosed(FormClosedEventArgs e) { _tick.Stop(); _tick.Dispose(); try { _previewTimer?.Dispose(); } catch { } Save(); base.OnFormClosed(e); }

        void Enumerate()
        {
            _devs = Joy.Source.Enumerate();
            _enumAt = DateTime.UtcNow;
            foreach (var d in _devs) if (!_st.ContainsKey(d.Key)) _st[d.Key] = new Joy.State();
            _devView.Devices = _devs;
            _devView.Height = Theme.Px(_devs.Count == 0 ? 60 : Math.Min(3, _devs.Count) * 62 + 12);
        }

        Joy.Device Dev(string key) => _devs.FirstOrDefault(d => d.Key == key);
        public Joy.State StateOf(string key) => _st.TryGetValue(key, out var s) ? s : null;

        void Tick()
        {
            if (DateTime.UtcNow - _enumAt > TimeSpan.FromSeconds(3)) Enumerate();   // (se enchufa o se quita un mando)
            foreach (var d in _devs) Joy.Source.Read(d, _st[d.Key]);
            if (_capAxis != null || _capButton) Capture();
            _lblStatus.Text = _status ?? "";
            UpdateRows();
            _devView.Invalidate(); _axes.Invalidate(); _buttons.Invalidate();
        }

        // ---------------- captura ----------------
        void StartCapture(string axisAction, bool button)
        {
            _capAxis = axisAction; _capButton = button; _capUntil = DateTime.UtcNow.AddSeconds(12);
            _capBase.Clear(); _capBtnBase.Clear();
            foreach (var d in _devs) { Joy.Source.Read(d, _st[d.Key]); _capBase[d.Key] = (float[])_st[d.Key].Axes.Clone(); _capBtnBase[d.Key] = _st[d.Key].Buttons; }
            _status = _devs.Count == 0 ? I18n.T("No hay ningún mando conectado.")
                    : button ? I18n.T("Pulsa el botón del mando… (Esc: cancelar)") : string.Format(I18n.T("Mueve la palanca del {0}… (Esc: cancelar)"), Joy.Label(axisAction).ToLowerInvariant());
            if (_devs.Count == 0) { _capAxis = null; _capButton = false; }
        }

        void StopCapture(string msg) { _capAxis = null; _capButton = false; _status = msg; }

        void Capture()
        {
            if (DateTime.UtcNow > _capUntil) { StopCapture(I18n.T("No se ha movido nada: vuelve a intentarlo.")); return; }
            foreach (var d in _devs)
            {
                var s = _st[d.Key];
                if (_capAxis != null && _capBase.TryGetValue(d.Key, out var b0))
                    for (int i = 0; i < d.Axes && i < 6; i++)
                        if (Math.Abs(s.Axes[i] - b0[i]) > 0.25f)
                        {
                            string act = _capAxis;
                            Bindings.RemoveAll(x => x.Axis && (x.Action == act || Joy.Conflicts(act).Contains(x.Action) || (x.Device == d.Key && x.Index == i)));
                            Bindings.Add(new JoyBinding { Device = d.Key, DeviceName = d.Name, Axis = true, Index = i, Action = act });
                            Save(); Rebuild();
                            StopCapture(string.Format(I18n.T("{0}: palanca {1} de {2}."), Joy.Label(act), Joy.AxisName(d.Key, i), d.Name)
                                        + (Joy.Conflicts(act).Length > 0 ? " " + I18n.T("(no va a la vez que el regulador o el freno dinámico sueltos)") : ""));
                            return;
                        }
                if (_capButton && _capBtnBase.TryGetValue(d.Key, out var bb))
                {
                    ulong pressed = s.Buttons & ~bb;
                    for (int i = 0; i < 64; i++)
                        if ((pressed & (1UL << i)) != 0)
                        {
                            _capButton = false;
                            PickButtonAction(d, i);
                            return;
                        }
                    _capBtnBase[d.Key] = s.Buttons & bb;   // (lo que se suelta, se puede volver a pulsar)
                }
            }
        }

        // El botón ya pulsado: ¿qué hace? (lista de órdenes de Open Rails)
        void PickButtonAction(Joy.Device d, int index)
        {
            _status = string.Format(I18n.T("Botón {0} de {1}: elige qué hace."), Joy.ButtonName(d.Key, index), d.Name);
            var cm = MenuStyle.Apply(new ContextMenuStrip { ShowImageMargin = false });
            cm.Items.Add(new ToolStripMenuItem(string.Format(I18n.T("Botón {0} · {1}"), Joy.ButtonName(d.Key, index), d.Name)) { Enabled = false });
            cm.Items.Add(new ToolStripSeparator());
            foreach (var (id, label) in Joy.ButtonActions)
            {
                string act = id;
                var it = new ToolStripMenuItem(I18n.T(label)) { Checked = Bindings.Any(x => !x.Axis && x.Device == d.Key && x.Index == index && x.Action == act) };
                it.Click += (s, e) => AssignButton(d, index, act);
                cm.Items.Add(it);
            }
            cm.Closed += (s, e) => { try { BeginInvoke((Action)cm.Dispose); } catch { } };
            var at = _btnAddButton.PointToScreen(new Point(0, _btnAddButton.Height));
            cm.Show(at);
        }

        // (también lo usan las pruebas)
        public void AssignButton(Joy.Device d, int index, string act)
        {
            Bindings.RemoveAll(x => !x.Axis && x.Device == d.Key && x.Index == index);
            Bindings.Add(new JoyBinding { Device = d.Key, DeviceName = d.Name, Axis = false, Index = index, Action = act });
            Save(); Rebuild();
            _status = string.Format(I18n.T("Botón {0}: {1}."), Joy.ButtonName(d.Key, index), Joy.Label(act));
        }
        public string Status => _status;
        public RowsView AxesView => _axes;
        public RowsView ButtonsView => _buttons;
        public void ClickAddButton() => StartCapture(null, true);
        public void ClickQuick() => QuickQuadrant();

        // Cuadrante de 3 palancas (o cualquier mando con 3 ejes o más): regulador, freno de tren y freno de locomotora.
        void QuickQuadrant()
        {
            var d = _devs.FirstOrDefault(x => x.Axes >= 3);
            if (d == null) { _status = I18n.T("No hay ningún mando con 3 palancas conectado."); return; }
            string[] acts = { "THROTTLE", "TRAIN_BRAKE", "ENGINE_BRAKE" };
            Bindings.RemoveAll(x => x.Axis && (acts.Contains(x.Action) || x.Action == "THROTTLE_DYNAMIC" || (x.Device == d.Key && x.Index < 3)));
            for (int i = 0; i < 3; i++) Bindings.Add(new JoyBinding { Device = d.Key, DeviceName = d.Name, Axis = true, Index = i, Action = acts[i] });
            Save(); Rebuild();
            _status = string.Format(I18n.T("{0}: palanca 1 regulador, 2 freno de tren, 3 freno de locomotora (muévelas para comprobarlo)."), d.Name);
        }

        // ---------------- filas ----------------
        void Rebuild()
        {
            _axes.Rows = Joy.AxisActions.Select(a =>
            {
                var b = Bindings.FirstOrDefault(x => x.Axis && x.Action == a.id);
                return new RowsView.Row
                {
                    Tag = a.id, Title = I18n.T(a.label),
                    Sub = b == null ? I18n.T("Sin asignar") : string.Format(I18n.T("Palanca {0} · {1}"), Joy.AxisName(b.Device, b.Index), b.DeviceName),
                    HasBar = b != null, Centered = Joy.Centered(a.id),
                    Chips = b == null ? new[] { I18n.T("Asignar") } : new[] { I18n.T("Asignar"), b.Invert ? I18n.T("Invertida ✓") : I18n.T("Invertir"), I18n.T("Quitar") }
                };
            }).ToList();
            _buttons.Rows = Bindings.Where(x => !x.Axis).OrderBy(x => x.DeviceName).ThenBy(x => x.Index).Select(b => new RowsView.Row
            {
                Tag = b, Title = Joy.Label(b.Action), Sub = string.Format(I18n.T("Botón {0} · {1}"), Joy.ButtonName(b.Device, b.Index), b.DeviceName), Lamp = true,
                Chips = new[] { I18n.T("Quitar") }
            }).ToList();
            _btnQuick.Visible = _devs.Any(x => x.Axes >= 3);
            UpdateRows();
        }

        void UpdateRows()
        {
            foreach (var r in _axes.Rows)
            {
                var b = Bindings.FirstOrDefault(x => x.Axis && x.Action == (string)r.Tag);
                var s = b == null ? null : StateOf(b.Device);
                r.Live = s != null && Dev(b.Device) != null;
                r.Value = s == null ? 0 : Joy.Value(b, s.Axes[Math.Max(0, Math.Min(5, b.Index))]);
            }
            foreach (var r in _buttons.Rows)
            {
                var b = (JoyBinding)r.Tag; var s = StateOf(b.Device);
                r.Live = s != null && Dev(b.Device) != null;
                r.On = s != null && Joy.Down(s, b.Index);
            }
        }

        void AxisChip(RowsView.Row r, int chip)
        {
            string act = (string)r.Tag;
            var b = Bindings.FirstOrDefault(x => x.Axis && x.Action == act);
            if (chip == 0) { StartCapture(act, false); return; }
            if (b == null) return;
            if (chip == 1) { b.Invert = !b.Invert; Save(); Rebuild(); }
            if (chip == 2) { Bindings.Remove(b); Save(); Rebuild(); _status = string.Format(I18n.T("{0}: sin asignar."), Joy.Label(act)); }
        }

        void ButtonChip(RowsView.Row r, int chip)
        {
            if (chip == 0 && r.Tag is JoyBinding b) { Bindings.Remove(b); Save(); Rebuild(); }
        }

        // ---------------- los mandos, en vivo ----------------
        sealed class DevicesView : Control
        {
            public List<Joy.Device> Devices = new();
            public SelectorSettingsDialog Owner2;
            public DevicesView() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Theme.Bg; }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
                using var fN = Theme.Font(9.5f, FontStyle.Bold); using var fS = Theme.Font(8f);
                if (Devices.Count == 0)
                {
                    var r0 = new Rectangle(0, 4, Width - 1, Height - 10);
                    using (var p = Theme.Round(r0, 10)) using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, p);
                    TextRenderer.DrawText(g, I18n.T("No hay ningún mando conectado. Enchúfalo y pulsa «Buscar mandos»."), fS, r0, Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }
                int y = 4, h = Theme.Px(56);
                foreach (var d in Devices.Take(3))
                {
                    var box = new Rectangle(0, y, Width - 1, h);
                    using (var p = Theme.Round(box, 10)) using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, p);
                    TextRenderer.DrawText(g, d.Name, fN, new Rectangle(box.X + 12, box.Y + 6, box.Width / 3, 20), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(g, string.Format(I18n.T("{0} palancas · {1} botones"), d.Axes, d.Buttons) + (d.Pov ? " · " + I18n.T("cruceta") : ""), fS, new Rectangle(box.X + 12, box.Y + 28, box.Width / 3, 18), Theme.Subtle, TextFormatFlags.NoPrefix);
                    var s = Owner2?.StateOf(d.Key);
                    int ax = box.X + box.Width / 3 + 10, aw = Math.Max(40, (box.Width / 3 - 20) / Math.Max(1, d.Axes) - 8);
                    for (int i = 0; i < d.Axes; i++)
                    {
                        int x = ax + i * (aw + 8);
                        TextRenderer.DrawText(g, d.XInput ? new[] { "LX", "LY", "RX", "RY", "LT", "RT" }[i] : Joy.AxisName(i), fS, new Point(x, box.Y + 6), Theme.Subtle);
                        var bar = new Rectangle(x, box.Y + 26, aw, 10);
                        using (var bp = Theme.Round(bar, 5)) using (var bb = new SolidBrush(Theme.Surface2)) g.FillPath(bb, bp);
                        float v = s == null ? 0 : s.Axes[i];
                        var fill = new Rectangle(bar.X, bar.Y, Math.Max(4, (int)(bar.Width * v)), bar.Height);
                        using (var fp = Theme.Round(fill, 5)) using (var fb = new SolidBrush(Theme.Accent)) g.FillPath(fb, fp);
                        TextRenderer.DrawText(g, ((int)Math.Round(v * 100)) + "%", fS, new Point(x, box.Y + 38), Theme.Subtle);
                    }
                    int bx = box.X + 2 * box.Width / 3 + 10, r = 8;
                    // los botones y, si la tiene, la cruceta (↑ → ↓ ←); en los de Xbox, con su letra
                    var idx = Enumerable.Range(0, d.Buttons).ToList();
                    if (d.Pov) idx.AddRange(Enumerable.Range(Joy.PovFirst, 4));
                    for (int k = 0; k < idx.Count; k++)
                    {
                        int i = idx[k], col = k % 12, row = k / 12;
                        var c = new Rectangle(bx + col * (2 * r + 6), box.Y + 8 + row * (2 * r + 6), 2 * r, 2 * r);
                        bool on = s != null && Joy.Down(s, i);
                        using (var cb = new SolidBrush(on ? Theme.AccentHi : Theme.Surface2)) g.FillEllipse(cb, c);
                        using var f7 = Theme.Font(6.5f);
                        string lbl = i >= Joy.PovFirst ? new[] { "↑", "→", "↓", "←" }[i - Joy.PovFirst]
                                   : d.XInput ? new[] { "A", "B", "X", "Y", "LB", "RB", "⧉", "≡", "LS", "RS", "↑", "↓", "←", "→" }[Math.Min(13, i)] : (i + 1).ToString();
                        TextRenderer.DrawText(g, lbl, f7, c, on ? Color.Black : Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }
                    y += h + 6;
                }
            }
        }

        // Filas pintadas: título, debajo la asignación, una barra con el valor (o una luz) y botoncitos a la derecha.
        public sealed class RowsView : Control
        {
            public sealed class Row { public object Tag; public string Title, Sub; public bool HasBar, Centered, Lamp, Live, On; public float Value; public string[] Chips = Array.Empty<string>(); }
            public List<Row> Rows = new();
            public string Empty;
            public event Action<Row, int> ChipClick;
            readonly List<(Rectangle r, Row row, int chip)> _hits = new();
            int _hover = -1, _scroll;
            public RowsView() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Theme.Bg; }
            int RowH => Theme.Px(48);

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
                _hits.Clear();
                using var fT = Theme.Font(9.5f, FontStyle.Bold); using var fS = Theme.Font(8.25f); using var fC = Theme.Font(8.5f, FontStyle.Bold);
                if (Rows.Count == 0 && Empty != null)
                {
                    TextRenderer.DrawText(g, Empty, fS, new Rectangle(4, 6, Width - 8, 60), Theme.Subtle, TextFormatFlags.WordBreak);
                    return;
                }
                _scroll = Math.Max(0, Math.Min(_scroll, Rows.Count * (RowH + 6) - Height));
                int y = -_scroll;
                // (la barra, en la misma columna en todas las filas: a la izquierda de los botoncitos de la fila que más ocupan)
                int chipsMax = 0;
                foreach (var rr in Rows) { int w0 = 0; foreach (var c0 in rr.Chips) w0 += TextRenderer.MeasureText(c0, fC).Width + 16 + 6; chipsMax = Math.Max(chipsMax, w0); }
                foreach (var row in Rows)
                {
                    var box = new Rectangle(0, y, Width - 1, RowH);
                    y += RowH + 6;
                    if (box.Bottom < 0 || box.Top > Height) continue;
                    using (var p = Theme.Round(box, 9)) using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, p);
                    // botoncitos (de derecha a izquierda)
                    int cx = box.Right - 10;
                    for (int i = row.Chips.Length - 1; i >= 0; i--)
                    {
                        int w = TextRenderer.MeasureText(row.Chips[i], fC).Width + 16;
                        var c = new Rectangle(cx - w, box.Y + (RowH - 28) / 2, w, 28);
                        bool hov = _hits.Count == _hover;
                        using (var cp = Theme.Round(c, 7)) using (var cb = new SolidBrush(hov ? Theme.SurfaceHi : Theme.Surface2)) g.FillPath(cb, cp);
                        TextRenderer.DrawText(g, row.Chips[i], fC, c, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                        _hits.Add((c, row, i));
                        cx = c.Left - 6;
                    }
                    int tx = box.X + 12;
                    if (row.Lamp)
                    {
                        var l = new Rectangle(tx, box.Y + (RowH - 14) / 2, 14, 14);
                        using (var lb = new SolidBrush(!row.Live ? Theme.Surface2 : row.On ? Theme.AccentHi : Color.FromArgb(70, 76, 82))) g.FillEllipse(lb, l);
                        tx += 24;
                    }
                    int barRight = Width - 1 - 10 - chipsMax;
                    int textW = Math.Max(60, (row.HasBar ? barRight - 108 : cx) - tx - 6);
                    TextRenderer.DrawText(g, row.Title, fT, new Rectangle(tx, box.Y + 5, textW, 20), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(g, row.Sub ?? "", fS, new Rectangle(tx, box.Y + 25, textW, 18), row.Live || !row.HasBar ? Theme.Subtle : Color.FromArgb(200, 150, 120), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    if (row.HasBar)
                    {
                        var bar = new Rectangle(barRight - 102, box.Y + (RowH - 12) / 2 - 3, 94, 12);
                        using (var bp = Theme.Round(bar, 6)) using (var bb = new SolidBrush(Theme.Surface2)) g.FillPath(bb, bp);
                        if (row.Live)
                        {
                            Rectangle fill;
                            if (row.Centered)
                            {
                                int mid = bar.X + bar.Width / 2, w = (int)(bar.Width / 2 * Math.Abs(row.Value));
                                fill = row.Value >= 0 ? new Rectangle(mid, bar.Y, Math.Max(3, w), bar.Height) : new Rectangle(mid - w, bar.Y, Math.Max(3, w), bar.Height);
                            }
                            else fill = new Rectangle(bar.X, bar.Y, Math.Max(4, (int)(bar.Width * row.Value)), bar.Height);
                            using (var fp = Theme.Round(fill, 6)) using (var fb = new SolidBrush(Theme.Accent)) g.FillPath(fb, fp);
                            string txt = (row.Tag as string) == "THROTTLE_DYNAMIC"
                                ? (row.Value > 0 ? I18n.T("regulador") + " " + (int)Math.Round(row.Value * 100) + "%" : row.Value < 0 ? I18n.T("freno dinámico") + " " + (int)Math.Round(-row.Value * 100) + "%" : I18n.T("en reposo"))
                                : row.Centered ? (row.Value > 0.5f ? I18n.T("adelante") : row.Value < -0.5f ? I18n.T("atrás") : I18n.T("neutro")) : ((int)Math.Round(row.Value * 100)) + "%";
                            using var f8 = Theme.Font(7.5f, FontStyle.Bold);
                            TextRenderer.DrawText(g, txt, f8, new Rectangle(bar.X, bar.Bottom + 1, bar.Width, 14), Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                        }
                    }
                }
            }

            int HitAt(Point p) { for (int i = 0; i < _hits.Count; i++) if (_hits[i].r.Contains(p)) return i; return -1; }
            protected override void OnMouseMove(MouseEventArgs e) { int h = HitAt(e.Location); if (h != _hover) { _hover = h; Invalidate(); } Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; base.OnMouseMove(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button != MouseButtons.Left) return;
                int h = HitAt(e.Location);
                if (h >= 0) ChipClick?.Invoke(_hits[h].row, _hits[h].chip);
            }
            protected override void OnMouseWheel(MouseEventArgs e) { _scroll -= Math.Sign(e.Delta) * (RowH + 6); Invalidate(); base.OnMouseWheel(e); }
            public void Click(object tag, int chip) { var r = Rows.FirstOrDefault(x => Equals(x.Tag, tag)); if (r != null) ChipClick?.Invoke(r, chip); }   // (pruebas)
        }
    }
}
