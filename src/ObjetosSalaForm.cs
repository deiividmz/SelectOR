// OBJETOS DE LA SALA (petición 2026-11-04): ventana del administrador, desde el Puesto de Mando («🚧 Objetos»).
// A la izquierda, qué poner (tipo y figura) y la lista de lo que hay; a la derecha, el mapa de la ruta con los objetos.
//  · Tipos: operarios de vía (4) y, fijos, derrumbe de tierras, derrumbe grande, roca caída y piedras en la vía (rocas y
//    tierra escaneadas de Poly Haven, CC0). Encima de la vía, un derrumbe se pone atravesado. Y fuego e incendio (llamas,
//    luz de noche y humo: MapAgent\Viajeros3D.Objetos.cs).
//  · Clic en el mapa: se pone ahí, mirando a la vía más cercana. Clic en uno: se elige. Botón derecho en uno: girarlo o
//    quitarlo.
//  · Se guarda en Supabase (sql/objetos-sala.sql: room_objects) con su sitio en Open Rails (tesela y metros, de lat/lon con
//    OrGeo.TryWorld). Los que conducen en la sala los ven en el 3D (MainMenuForm.ObjetosSala.cs → el agente:
//    MapAgent\Viajeros3D.Objetos.cs). Solo decoran: no paran trenes ni tocan señales.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class ObjetosSalaForm : Form
    {
        public sealed class RoomObj
        {
            public long Id; public string Kind, Figure, Note; public int TileX, TileZ; public double X, Z, Bearing, Lat, Lon;
        }
        public static readonly (string kind, string label)[] Kinds =
        {
            ("obra_trabaja", "Operario trabajando"), ("obra_agachado", "Operario agachado"),
            ("obra_saluda", "Operario que saluda al tren"), ("obra_espera", "Operario de pie"),
            ("derrumbe", "Derrumbe de tierras"), ("derrumbe_grande", "Derrumbe grande"), ("roca", "Roca caída"), ("piedras", "Piedras en la vía"),
            ("fuego", "Fuego"), ("incendio", "Incendio"),
        };
        static bool IsWorker(string kind) => kind.StartsWith("obra_");
        public static readonly (string fig, string label)[] Figures =
        {
            ("*", "Cualquiera"), ("obra_h07", "Hombre · chaleco naranja"), ("obra_h08", "Hombre · chaleco amarillo"), ("obra_m01", "Mujer · chaleco amarillo"),
            ("obra_h02", "Hombre · buzo azul"), ("obra_h05", "Hombre · buzo azul (2)"), ("obra_h03", "Hombre · buzo y herramientas"),
            ("obra_h06", "Hombre · buzo y herramientas (2)"), ("obra_h04", "Hombre · peto y camisa blanca"), ("obra_h01", "Hombre · careta de soldar"),
        };
        static string Tr(string s) => I18n.T(s);
        static string KindLabel(string k) => Tr(Kinds.FirstOrDefault(x => x.kind == k).label ?? k);

        readonly string _routeDir, _route;
        readonly Func<Task<RouteMapData>> _mapData;
        public Func<string, object, Task<(string json, string err)>> Rpc = Supa.RpcAsync;   // (las pruebas la cambian)
        public Action Changed;   // (MainMenuForm: que lo mande ya a Open Rails)
        readonly RouteLiveMap _map;
        readonly ThemeCombo _kind, _fig;
        readonly ListBox _list;
        readonly Label _count;
        List<RoomObj> _objs = new();
        long _sel = -1;
        bool _busy;

        public ObjetosSalaForm(string routeDir, string route, Func<Task<RouteMapData>> mapData)
        {
            _routeDir = routeDir; _route = route; _mapData = mapData;
            Text = Tr("Objetos de la sala");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(Theme.Px(1180), Theme.Px(740)); MinimumSize = new Size(Theme.Px(820), Theme.Px(480));
            DialogKit.Dress(this);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete && _sel >= 0) { e.Handled = true; _ = Remove(_sel); } };

            var side = new Panel { Dock = DockStyle.Left, Width = Theme.Px(320), BackColor = Theme.BgSidebar, Padding = new Padding(Theme.Px(12)) };
            Label Cap(string t) => new Label { Text = t, Dock = DockStyle.Top, Height = Theme.Px(26), Font = Theme.Font(8.5f, FontStyle.Bold), ForeColor = Theme.Subtle, BackColor = side.BackColor, TextAlign = ContentAlignment.BottomLeft };
            Panel Gap(int h) => new Panel { Dock = DockStyle.Top, Height = Theme.Px(h), BackColor = side.BackColor };
            _kind = DialogKit.Combo(); _kind.Dock = DockStyle.Top; _kind.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (var (_, l) in Kinds) _kind.Items.Add(Tr(l));
            _kind.SelectedIndex = 0;
            _kind.SelectedIndexChanged += (s, e) => { UpdateStatus(); _fig.Enabled = IsWorker(Kinds[Math.Max(0, _kind.SelectedIndex)].kind); };   // (la figura, solo los operarios)
            _fig = DialogKit.Combo(); _fig.Dock = DockStyle.Top; _fig.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (var (_, l) in Figures) _fig.Items.Add(Tr(l));
            _fig.SelectedIndex = 0;
            var hint = new Label
            {
                Dock = DockStyle.Top, Height = Theme.Px(92), Font = Theme.Font(8.75f), ForeColor = Theme.Subtle, BackColor = side.BackColor,
                Text = Tr("Clic en el mapa: se pone ahí, mirando a la vía (acércate con la rueda para afinar). Clic en uno: se elige. Botón derecho en uno: girarlo o quitarlo.") + "\n"
                       + Tr("Lo ven en el 3D todos los que conducen en la sala. Solo decoran: no paran trenes.")
            };
            _count = Cap(Tr("EN LA RUTA"));
            _list = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.BgSidebar, ForeColor = Theme.Text, Font = Theme.Font(9.25f),
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.Px(40), IntegralHeight = false
            };
            _list.DrawItem += DrawRow;
            _list.SelectedIndexChanged += (s, e) =>
            {
                if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _objs.Count) return;
                var o = _objs[_list.SelectedIndex]; if (o.Id == _sel) return;
                _sel = o.Id; _map.FitLL(new[] { (o.Lat, o.Lon) }); _map.Invalidate();
            };
            try { Native.UseDarkScrollBars(_list); } catch { }
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Theme.Px(96), BackColor = side.BackColor, Padding = new Padding(0, Theme.Px(6), 0, 0) };
            var turn = DialogKit.Secondary(Tr("Girar 45°"), 140); turn.Click += (s, e) => { if (_sel >= 0) _ = Turn(_sel, 45); };
            var del = DialogKit.Secondary(Tr("Quitar"), 140); del.Click += (s, e) => { if (_sel >= 0) _ = Remove(_sel); };
            var all = DialogKit.Danger(Tr("Quitar todos"), 140); all.Click += async (s, e) => await RemoveAll();
            buttons.Controls.AddRange(new Control[] { turn, del, all });
            // (orden de Dock.Top: lo último añadido queda arriba)
            side.Controls.Add(_list); side.Controls.Add(buttons);
            foreach (var c in new Control[] { _count, Gap(8), hint, Gap(8), _fig, Cap(Tr("FIGURA")), _kind, Cap(Tr("PONER EN EL MAPA")) }) side.Controls.Add(c);

            _map = new RouteLiveMap { Dock = DockStyle.Fill };
            _map.EmptyText = Tr("Cargando el mapa de la ruta…");
            _map.Overlay = DrawOverlay;
            _map.MapClick += OnMapClick;
            Controls.Add(_map); Controls.Add(side);
            Shown += async (s, e) => { await LoadMap(); await Reload(); };
        }

        async Task LoadMap()
        {
            RouteMapData d = null;
            try { d = await _mapData(); } catch { d = null; }
            if (IsDisposed) return;
            if (d == null) { _map.EmptyText = Tr("No se ha podido leer el mapa de la ruta."); return; }
            _map.SetData(d);
        }

        async Task Reload()
        {
            var (json, err) = await Rpc("room_objects_get", new { p_route = _route });
            if (IsDisposed) return;
            if (err != null) { _map.Status = Tr("No se ha podido leer la lista: ") + err; return; }
            _objs = Parse(json, out _);
            _list.BeginUpdate(); _list.Items.Clear(); foreach (var o in _objs) _list.Items.Add(o.Id); _list.EndUpdate();
            int i = _objs.FindIndex(o => o.Id == _sel); if (i >= 0) _list.SelectedIndex = i; else _sel = -1;
            _count.Text = Tr("EN LA RUTA") + $" ({_objs.Count})";
            UpdateStatus();
            _map.Invalidate();
        }
        void UpdateStatus() => _map.Status = string.Format(Tr("{0} objetos · clic en el mapa para poner un «{1}»"), _objs.Count, KindLabel(Kinds[Math.Max(0, _kind.SelectedIndex)].kind));

        public static List<RoomObj> Parse(string json, out long version)
        {
            var l = new List<RoomObj>(); version = 0;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number) version = v.GetInt64();
                if (r.TryGetProperty("objects", out var a) && a.ValueKind == JsonValueKind.Array)
                    foreach (var e in a.EnumerateArray())
                        l.Add(new RoomObj
                        {
                            Id = e.GetProperty("id").GetInt64(), Kind = e.GetProperty("kind").GetString(), Figure = e.GetProperty("figure").GetString(),
                            TileX = e.GetProperty("tile_x").GetInt32(), TileZ = e.GetProperty("tile_z").GetInt32(), X = e.GetProperty("x").GetDouble(), Z = e.GetProperty("z").GetDouble(),
                            Bearing = e.GetProperty("bearing").GetDouble(), Lat = e.GetProperty("lat").GetDouble(), Lon = e.GetProperty("lon").GetDouble(),
                            Note = e.TryGetProperty("note", out var n) ? n.GetString() ?? "" : ""
                        });
            }
            catch { }
            return l;
        }

        // el sitio más cercano con nombre (estación) y a cuánto
        string Where(RoomObj o)
        {
            var d = _map.Data; if (d?.StName == null || d.StName.Length == 0) return "";
            double best = double.MaxValue; int bi = -1;
            for (int i = 0; i < d.StName.Length; i++)
            {
                double dy = (d.StLat[i] - o.Lat) * 111320, dx = (d.StLon[i] - o.Lon) * 111320 * Math.Cos(o.Lat * Math.PI / 180), dd = dx * dx + dy * dy;
                if (dd < best) { best = dd; bi = i; }
            }
            if (bi < 0) return "";
            double m = Math.Sqrt(best);
            return m < 400 ? string.Format(Tr("en {0}"), d.StName[bi]) : string.Format(Tr("a {0:0.0} km de {1}"), m / 1000, d.StName[bi]);
        }

        void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _objs.Count) return;
            var o = _objs[e.Index]; bool sel = o.Id == _sel;
            using (var b = new SolidBrush(sel ? Theme.Surface2 : Theme.BgSidebar)) e.Graphics.FillRectangle(b, e.Bounds);
            if (sel) using (var a = new SolidBrush(Color.FromArgb(255, 160, 40))) e.Graphics.FillRectangle(a, e.Bounds.X, e.Bounds.Y + 5, 3, e.Bounds.Height - 10);
            int h = e.Bounds.Height / 2;
            ColorText.Text(e.Graphics, (IsWorker(o.Kind) ? "🚧 " : o.Kind == "fuego" || o.Kind == "incendio" ? "🔥 " : "⛰ ") + KindLabel(o.Kind), _list.Font, new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 2, e.Bounds.Width - 14, h),
                Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            string fig = IsWorker(o.Kind) ? Tr(Figures.FirstOrDefault(x => x.fig == o.Figure).label ?? o.Figure) + " · " : "";
            using var small = Theme.Font(8.25f);
            ColorText.Text(e.Graphics, fig + Where(o), small, new Rectangle(e.Bounds.X + 30, e.Bounds.Y + h - 2, e.Bounds.Width - 34, h),
                Theme.Subtle, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        // en el mapa: un círculo naranja con una raya hacia donde mira (el elegido, más grande y con borde blanco)
        void DrawOverlay(Graphics g, Func<double, double, PointF> scr)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var o in _objs)
            {
                var p = scr(o.Lat, o.Lon); bool sel = o.Id == _sel;
                float r = sel ? 9 : 6;
                if (!IsWorker(o.Kind))
                {
                    // su tamaño de verdad (largo × ancho, en m) girado a su rumbo
                    var (len, wid) = o.Kind == "derrumbe_grande" ? (19.0, 10.4) : o.Kind == "derrumbe" ? (13.0, 7.2) : o.Kind == "roca" ? (4.4, 3.6) : o.Kind == "incendio" ? (6.0, 9.0) : o.Kind == "fuego" ? (2.2, 2.2) : (6.8, 4.8);
                    bool fire = o.Kind == "fuego" || o.Kind == "incendio";
                    float k = (float)Math.Max(1, _map.PxPerM);
                    var st = g.Save(); g.TranslateTransform(p.X, p.Y); g.RotateTransform((float)o.Bearing);
                    float L = (float)Math.Max(len * k, 2 * r + 4), Wd = (float)Math.Max(wid * k, 2 * r);
                    using (var b = new SolidBrush(fire ? Color.FromArgb(220, 235, 70, 20) : Color.FromArgb(200, 150, 105, 60))) g.FillEllipse(b, -Wd / 2, -L / 2, Wd, L);
                    using (var pen = new Pen(sel ? Color.White : Color.FromArgb(70, 45, 20), sel ? 2.5f : 1.2f)) g.DrawEllipse(pen, -Wd / 2, -L / 2, Wd, L);
                    g.Restore(st);
                    continue;
                }
                double a = o.Bearing * Math.PI / 180;
                var tip = new PointF(p.X + (float)Math.Sin(a) * (r + 9), p.Y - (float)Math.Cos(a) * (r + 9));
                using (var pen = new Pen(Color.FromArgb(255, 200, 120), 2.2f)) g.DrawLine(pen, p, tip);
                using (var b = new SolidBrush(o.Kind == "obra_saluda" ? Color.FromArgb(255, 200, 40) : Color.FromArgb(255, 130, 20))) g.FillEllipse(b, p.X - r, p.Y - r, 2 * r, 2 * r);
                using (var pen = new Pen(sel ? Color.White : Color.FromArgb(60, 30, 0), sel ? 2.5f : 1.2f)) g.DrawEllipse(pen, p.X - r, p.Y - r, 2 * r, 2 * r);
            }
        }

        RoomObj At(Point p)
        {
            RoomObj best = null; double bd = Theme.Px(12) * Theme.Px(12);
            foreach (var o in _objs)
            {
                var q = _map.ScreenOf(o.Lat, o.Lon); double d = (q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y);
                if (d < bd) { bd = d; best = o; }
            }
            return best;
        }

        void Select(long id)
        {
            _sel = id; int i = _objs.FindIndex(o => o.Id == id);
            if (i >= 0 && _list.SelectedIndex != i) _list.SelectedIndex = i;
            _list.Invalidate(); _map.Invalidate();
        }

        void OnMapClick(double lat, double lon, MouseButtons button, Point at)
        {
            var hit = At(at);
            if (button == MouseButtons.Right)
            {
                if (hit == null) return;
                Select(hit.Id);
                var m = MenuStyle.Apply(new ContextMenuStrip());
                m.Items.Add(Tr("Girar 45°"), null, (s, e) => _ = Turn(hit.Id, 45));
                m.Items.Add(Tr("Girar 90°"), null, (s, e) => _ = Turn(hit.Id, 90));
                m.Items.Add(Tr("Girar 180°"), null, (s, e) => _ = Turn(hit.Id, 180));
                m.Items.Add(Tr("Mirar a la vía"), null, (s, e) => _ = Turn(hit.Id, double.NaN));
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Tr("Quitar"), null, (s, e) => _ = Remove(hit.Id));
                m.Show(_map, at);
                return;
            }
            if (hit != null) { Select(hit.Id); return; }
            _ = Place(lat, lon);
        }

        // hacia la vía más cercana (a menos de 60 m); si está encima de ella, a lo largo; sin vía cerca, al norte
        double FaceTrack(double lat, double lon, string kind = "obra_")
        {
            var d = _map.Data; if (d == null) return 0;
            if (!IsWorker(kind) && d.Snap(lat, lon, 60, out double tla, out double tlo))
            {
                double dy0 = (tla - lat) * 111320, dx0 = (tlo - lon) * 111320 * Math.Cos(lat * Math.PI / 180);
                if (dx0 * dx0 + dy0 * dy0 <= 3 * 3 && d.TrackBearing(lat, lon, 60, out double tb)) return (tb + 90 + 360) % 360;   // (encima de la vía: atravesado)
            }
            if (d.Snap(lat, lon, 60, out double sla, out double slo))
            {
                double dy = (sla - lat) * 111320, dx = (slo - lon) * 111320 * Math.Cos(lat * Math.PI / 180);
                if (dx * dx + dy * dy > 0.8 * 0.8) return (Math.Atan2(dx, dy) * 180 / Math.PI + 360) % 360;
            }
            return d.TrackBearing(lat, lon, 60, out double b) ? (b + 360) % 360 : 0;
        }

        async Task Place(double lat, double lon)
        {
            if (_busy) return;
            if (!OrGeo.TryWorld(_routeDir, lat, lon, out double wx, out double wz))
            {
                ThemedBox.Show(this, Tr("No se ha podido pasar ese punto a la posición de Open Rails."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            int tx = (int)Math.Round(wx / 2048.0), tz = (int)Math.Round(wz / 2048.0);
            var kind = Kinds[Math.Max(0, _kind.SelectedIndex)].kind; var fig = IsWorker(kind) ? Figures[Math.Max(0, _fig.SelectedIndex)].fig : "*";
            _busy = true;
            try
            {
                var (json, err) = await Rpc("room_object_save", new
                {
                    p_id = (long?)null, p_kind = kind, p_figure = fig, p_tile_x = tx, p_tile_z = tz, p_x = (float)(wx - tx * 2048.0), p_z = (float)(wz - tz * 2048.0),
                    p_bearing = (float)FaceTrack(lat, lon, kind), p_lat = lat, p_lon = lon, p_note = "", p_route = _route
                });
                if (IsDisposed) return;
                if (err != null) { ThemedBox.Show(this, Tr("No se ha podido poner: ") + err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                if (long.TryParse(json?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)) _sel = id;
                await Reload(); Changed?.Invoke();
            }
            finally { _busy = false; }
        }

        async Task Turn(long id, double deg)
        {
            var o = _objs.FirstOrDefault(x => x.Id == id); if (o == null || _busy) return;
            double b = double.IsNaN(deg) ? FaceTrack(o.Lat, o.Lon, o.Kind) : (o.Bearing + deg) % 360;
            _busy = true;
            try
            {
                var (_, err) = await Rpc("room_object_save", new
                {
                    p_id = o.Id, p_kind = o.Kind, p_figure = o.Figure, p_tile_x = o.TileX, p_tile_z = o.TileZ, p_x = (float)o.X, p_z = (float)o.Z,
                    p_bearing = (float)b, p_lat = o.Lat, p_lon = o.Lon, p_note = o.Note ?? "", p_route = _route
                });
                if (IsDisposed) return;
                if (err != null) { ThemedBox.Show(this, Tr("No se ha podido girar: ") + err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                await Reload(); Changed?.Invoke();
            }
            finally { _busy = false; }
        }

        async Task Remove(long id)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var (_, err) = await Rpc("room_object_delete", new { p_id = (long?)id, p_route = _route });
                if (IsDisposed) return;
                if (err != null) { ThemedBox.Show(this, Tr("No se ha podido quitar: ") + err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                if (_sel == id) _sel = -1;
                await Reload(); Changed?.Invoke();
            }
            finally { _busy = false; }
        }

        async Task RemoveAll()
        {
            if (_busy || _objs.Count == 0) return;
            if (ThemedBox.Show(this, string.Format(Tr("¿Quitar los {0} objetos de la sala?"), _objs.Count), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _busy = true;
            try
            {
                var (_, err) = await Rpc("room_object_delete", new { p_id = (long?)null, p_route = _route });
                if (IsDisposed) return;
                if (err != null) { ThemedBox.Show(this, Tr("No se han podido quitar: ") + err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                _sel = -1; await Reload(); Changed?.Invoke();
            }
            finally { _busy = false; }
        }
    }
}
