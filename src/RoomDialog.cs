// «Nueva sala» / «Editar sala» (Multijugador → Servidores SelectOR, administrador): a dos columnas, en pasos numerados.
//   izquierda: 1 · sala (nombre y descripción) · 2 · servidor (ruta, IP y puerto) · 3 · contraseña
//   derecha:   4 · jugadores (límite, carné mínimo y abierta/cerrada) · 5 · quién puede entrar (todos, los socios de
//              unas empresas o unos usuarios)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class RoomDialog : Form
    {
        public sealed class Data
        {
            public string Name = "", Description = "", Route = "", Host = "", Password = "", PassMode = "none", Access = "all";   // PassMode: keep · none · set
            public int Port = 30000, MaxPlayers = 8, MinPoints;
            public bool Active = true;
            public List<string> CompanyIds = new(), Users = new();
        }
        public Func<Data, Task<string>> Submit;   // guarda; devuelve el error o null

        readonly List<(string id, string name)> _companies;
        readonly bool _hadPassword;
        readonly RoundedInput _name, _desc, _host, _port, _pw, _max, _points, _users;
        readonly ThemeCombo _route;
        readonly FlowLayoutPanel _pwChips, _accChips, _stateChips;
        readonly CheckedListBox _cos;
        readonly Label _err, _cosCap, _usersCap, _pwCap;
        readonly RoundButton _ok;
        int _pwMode, _acc, _state;
        static string T(string s) => I18n.T(s);

        public RoomDialog(bool editing, Data d, IList<string> routes, List<(string id, string name)> companies, bool hadPassword)
        {
            _companies = companies ?? new(); _hadPassword = hadPassword;
            Text = editing ? T("Editar sala") : T("Nueva sala");
            Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            DialogKit.Dress(this);
            ClientSize = new Size(Theme.Px(880), Theme.Px(600));

            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var head = new DialogKit.Header(Text, T("Servidor SelectOR: los usuarios lo ven en Multijugador y entran con un clic."), "🖥");
            var foot = new DialogKit.Footer();
            _ok = DialogKit.Fit(DialogKit.Primary("💾  " + T("Guardar sala")), 170);
            var cancel = DialogKit.Secondary(T("Cancelar"));
            _err = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(229, 115, 115), Font = Theme.Font(9f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            foot.AddRight(_ok, cancel);
            foot.Controls.Add(_err); _err.BringToFront();
            _ok.Click += async (s, e) => await Accept();
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            int W = Theme.Px(390), x1 = Theme.Px(28), x2 = x1 + W + Theme.Px(44), y = Theme.Px(18), half = (W - Theme.Px(14)) / 2;
            int col = x1;
            Label Step(int n, string text) { var l = new DialogKit.StepLabel { N = n, Text = text, Location = new Point(col, y), Size = new Size(W, Theme.Px(22)) }; body.Controls.Add(l); y += Theme.Px(28); return l; }
            Label Cap(string text, int cx, int w) { var l = new Label { Text = text, Location = new Point(cx, y), Size = new Size(w, Theme.Px(17)), ForeColor = Theme.Subtle, Font = Theme.Font(8.5f) }; body.Controls.Add(l); return l; }
            RoundedInput Input(int cx, int w, string ph, string val) { var i = new RoundedInput(ph) { Location = new Point(cx, y), Width = w, Height = Theme.Px(34) }; i.Box.Text = val ?? ""; body.Controls.Add(i); return i; }
            FlowLayoutPanel Chips(string[] names, int sel, Action<int> on)
            {
                var bar = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Location = new Point(col - Theme.Px(2), y), Margin = new Padding(0), Padding = new Padding(0) };
                using var f = Theme.Font(9f, FontStyle.Bold);
                for (int k = 0; k < names.Length; k++)
                {
                    int idx = k;
                    var b = new RoundButton { Text = names[k], Height = Theme.Px(32), Radius = 16, FontSize = 9f, FontStyle = FontStyle.Bold, Margin = new Padding(Theme.Px(2), 0, Theme.Px(6), 0), Active = k == sel, BaseColor = Theme.Surface, HoverColor = Theme.SurfaceHi, TextColor = Theme.Subtle };
                    b.Width = TextRenderer.MeasureText(names[k], f).Width + Theme.Px(30);
                    b.Click += (s, e) => { foreach (Control c in bar.Controls) if (c is RoundButton rb) { rb.Active = rb == b; rb.Invalidate(); } on(idx); };
                    bar.Controls.Add(b);
                }
                body.Controls.Add(bar);
                return bar;
            }

            // ---- izquierda ----
            Step(1, T("SALA"));
            Cap(T("Nombre de la sala"), x1, W); y += Theme.Px(19);
            _name = Input(x1, W, T("p. ej. Eje Sur · mercancías nocturnas"), d.Name); y += Theme.Px(42);
            Cap(T("Descripción (opcional)"), x1, W); y += Theme.Px(19);
            _desc = Input(x1, W, T("p. ej. Sesión de los jueves a las 21:00"), d.Description); y += Theme.Px(50);
            Step(2, T("SERVIDOR"));
            Cap(T("Ruta"), x1, W); y += Theme.Px(19);
            _route = new ThemeCombo { Location = new Point(x1, y), Width = W, Font = Theme.Font(9.75f), ItemHeight = Theme.Px(24), BackColor = Theme.Surface2, ForeColor = Theme.Text, MaxDropDownItems = 14 };
            _route.DrawItem += DialogKit.DrawComboItem;
            foreach (var r in routes) _route.Items.Add(r);
            int ri = routes.ToList().FindIndex(r => string.Equals(r, d.Route, StringComparison.OrdinalIgnoreCase));
            if (_route.Items.Count > 0) _route.SelectedIndex = Math.Max(0, ri);
            body.Controls.Add(_route); y += Theme.Px(42);
            int pw = Theme.Px(110);
            Cap(T("IP o nombre del servidor"), x1, W - pw - Theme.Px(14)); Cap(T("Puerto"), x1 + W - pw, pw); y += Theme.Px(19);
            _host = Input(x1, W - pw - Theme.Px(14), "203.0.113.10", d.Host);
            _port = Input(x1 + W - pw, pw, "30000", d.Port.ToString());
            foreach (var i in new[] { _host, _port }) i.Box.Font = new Font("Consolas", 10.5f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold);
            y += Theme.Px(50);
            Step(3, T("CONTRASEÑA"));
            var pwNames = hadPassword ? new[] { T("Mantener la actual"), T("Sin contraseña"), T("Cambiarla") } : new[] { T("Sin contraseña"), T("Con contraseña") };
            _pwMode = hadPassword ? 0 : (d.PassMode == "set" ? 1 : 0);
            _pwChips = Chips(pwNames, _pwMode, i => { _pwMode = i; ShowPw(); });
            y += Theme.Px(40);
            _pwCap = Cap(T("Contraseña (al menos 3 caracteres; no distingue mayúsculas)"), x1, W); y += Theme.Px(19);
            _pw = Input(x1, W, T("la que darás a los jugadores"), "");
            int yLeftEnd = y + Theme.Px(42);

            // ---- derecha ----
            col = x2; y = Theme.Px(18);
            Step(4, T("JUGADORES"));
            int nw = (W - Theme.Px(14)) / 2;
            Cap(T("Límite de jugadores"), x2, nw); Cap(T("Carné mínimo (0 = cualquiera)"), x2 + nw + Theme.Px(14), nw); y += Theme.Px(19);
            _max = Input(x2, nw, "8", d.MaxPlayers.ToString());
            _points = Input(x2 + nw + Theme.Px(14), nw, "0", d.MinPoints.ToString());
            foreach (var i in new[] { _max, _points }) { i.Box.TextAlign = HorizontalAlignment.Center; i.Box.Font = new Font("Consolas", 11f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold); }
            y += Theme.Px(46);
            _state = d.Active ? 0 : 1;
            _stateChips = Chips(new[] { "● " + T("Abierta"), "⛔ " + T("Cerrada") }, _state, i => _state = i);
            y += Theme.Px(50);
            Step(5, T("QUIÉN PUEDE ENTRAR"));
            _acc = Math.Max(0, Array.IndexOf(new[] { "all", "companies", "users" }, d.Access));
            _accChips = Chips(new[] { "👥 " + T("Todos"), "🏢 " + T("Empresas"), "⭐ " + T("Usuarios") }, _acc, i => { _acc = i; ShowAccess(); });
            y += Theme.Px(42);
            _cosCap = Cap(T("Los socios de estas empresas:"), x2, W); y += Theme.Px(19);
            var cosHost = new Card { Fill = Theme.Surface2, BorderColor = Theme.Border, Radius = 9, Padding = new Padding(8, 6, 6, 6), Location = new Point(x2, y), Size = new Size(W, Theme.Px(150)) };
            _cos = new CheckedListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Surface2, ForeColor = Theme.Text, Font = Theme.Font(9.5f), CheckOnClick = true, IntegralHeight = false };
            foreach (var c in _companies) _cos.Items.Add(c.name, d.CompanyIds.Contains(c.id));
            cosHost.Controls.Add(_cos); Native.UseDarkScrollBars(_cos);
            body.Controls.Add(cosHost);
            _usersCap = new Label { Text = T("Estos usuarios (separados por comas):"), Location = _cosCap.Location, Size = _cosCap.Size, ForeColor = Theme.Subtle, Font = Theme.Font(8.5f) };
            body.Controls.Add(_usersCap);
            _users = new RoundedInput(T("p. ej. ana, beto, carla")) { Location = new Point(x2, y), Width = W, Height = Theme.Px(34) };
            _users.Box.Text = string.Join(", ", d.Users);
            body.Controls.Add(_users);
            _cos.Parent.Tag = "cos";
            var allNote = new Label { Text = T("Cualquier usuario de SelectOR con sesión iniciada (y el carné mínimo, si lo pones)."), Location = new Point(x2, y), Size = new Size(W, Theme.Px(40)), ForeColor = Theme.Subtle, Font = Theme.Font(9f) };
            body.Controls.Add(allNote);
            void ShowAccessInner()
            {
                cosHost.Visible = _cosCap.Visible = _acc == 1;
                _users.Visible = _usersCap.Visible = _acc == 2;
                allNote.Visible = _acc == 0;
            }
            _showAccess = ShowAccessInner;
            int yRightEnd = y + Theme.Px(160);

            ClientSize = new Size(ClientSize.Width, stripe.Height + head.Height + Math.Max(yLeftEnd, yRightEnd) + Theme.Px(10) + foot.Height);
            Controls.Add(body); Controls.Add(foot); Controls.Add(head); Controls.Add(stripe);
            ShowPw(); ShowAccess();
            Shown += (s, e) => _name.Box.Focus();
        }

        Action _showAccess;
        void ShowAccess() => _showAccess?.Invoke();
        bool PwSet => _hadPassword ? _pwMode == 2 : _pwMode == 1;
        void ShowPw() { _pw.Visible = _pwCap.Visible = PwSet; }

        async Task Accept()
        {
            if (!_ok.Enabled) return;
            string err = null;
            int port = 0, max = 0, pts = 0;
            if (_name.Box.Text.Trim().Length == 0) err = T("Falta el nombre de la sala");
            else if (_route.SelectedItem == null) err = T("Falta la ruta");
            else if (_host.Box.Text.Trim().Length == 0) err = T("Falta la IP o el nombre del servidor");
            else if (!int.TryParse(_port.Box.Text.Trim(), out port) || port < 1 || port > 65535) err = T("El puerto debe ser un número entre 1 y 65535.");
            else if (!int.TryParse(_max.Box.Text.Trim(), out max) || max < 1 || max > 128) err = T("El límite de jugadores debe estar entre 1 y 128.");
            else if (!int.TryParse(_points.Box.Text.Trim(), out pts) || pts < 0 || pts > 15) err = T("El carné mínimo va de 0 a 15 puntos.");
            else if (PwSet && _pw.Box.Text.Trim().Length < 3) err = T("La contraseña debe tener al menos 3 caracteres");
            else if (_acc == 1 && _cos.CheckedIndices.Count == 0) err = T("Elige al menos una empresa");
            if (err == null && Submit != null)
            {
                var d = new Data
                {
                    Name = _name.Box.Text.Trim(), Description = _desc.Box.Text.Trim(), Route = _route.SelectedItem as string, Host = _host.Box.Text.Trim(), Port = port,
                    PassMode = _hadPassword ? (_pwMode == 0 ? "keep" : _pwMode == 1 ? "none" : "set") : (_pwMode == 1 ? "set" : "none"),
                    Password = _pw.Box.Text.Trim(), MaxPlayers = max, MinPoints = pts, Active = _state == 0,
                    Access = _acc == 1 ? "companies" : _acc == 2 ? "users" : "all",
                    CompanyIds = _cos.CheckedIndices.Cast<int>().Where(i => i < _companies.Count).Select(i => _companies[i].id).ToList(),
                    Users = _users.Box.Text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(u => u.Trim()).Where(u => u.Length > 0).ToList()
                };
                _ok.Enabled = false; _err.ForeColor = Theme.Subtle; _err.Text = T("Guardando…");
                try { err = await Submit(d); } catch (Exception ex) { err = ex.Message; }
                if (IsDisposed) return;
                _ok.Enabled = true;
            }
            if (err != null) { _err.ForeColor = Color.FromArgb(229, 115, 115); _err.Text = err; return; }
            DialogResult = DialogResult.OK; Close();
        }
    }
}
