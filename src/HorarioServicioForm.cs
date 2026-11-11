// HORARIO DE LA SALA · ITINERARIO DEL SERVICIO (2026-11-11, petición). Una sola ventana para crear o cambiar un servicio de
// trenes IA, en vez del formulario de campos y la ventana aparte del «Recorrido»:
//  · a la izquierda, los datos (nombre, tipo, composición, horas, activo) y el ITINERARIO: origen → paradas en orden →
//    destino (y la vuelta, si es de ida y vuelta o circular), con las vías marcadas (salida, llegada y de paso) en su sitio;
//  · a la derecha, el mapa de la ruta con el camino que hará (el mismo cálculo que el mapa del horario), el origen, el
//    destino, las paradas numeradas y las vías marcadas. Clic derecho en un sitio: añadir o quitar una parada; clic
//    izquierdo, lo que diga la barra de encima del mapa (origen, destino, parada, vía de salida, de llegada o de paso).
// Sitios: estaciones (los andenes de la ruta, como siempre), TERMINALES DE MERCANCÍAS y APARTADEROS (con nombre en la ruta,
// agrupadas sus vías: MainMenuForm.FreightPlaces y SidingPlaces, los mismos que «Tu tren en la sala»).
// Se guarda igual que antes (room_ai_service_save_trip y, si cambian las vías marcadas, room_ai_service_route): el texto
// del horario que lee el despachador no cambia. Una parada fuera del camino (un apartadero en una vía muerta, p. ej.) sale
// avisada: el despachador solo para donde pasa (para pasar por ella, márcala como vía de paso).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class HorarioServicioForm : Form
    {
        static string Tr(string s) => I18n.T(s);

        // un sitio de la ruta: 0 estación · 1 terminal de mercancías · 2 apartadero · 3 otro (un nombre guardado que ya no sale)
        public sealed class Place
        {
            public string Name; public int Kind; public double Lat, Lon; public bool HasPos;
            public string Tag => Kind == 1 ? Tr("mercancías") : Kind == 2 ? Tr("apartadero") : Kind == 3 ? Tr("otro") : "";
            public override string ToString() => Tag.Length > 0 ? Name + "  ·  " + Tag : Name;
        }

        readonly HorarioSalaForm.Service _s;
        readonly bool _isNew;
        readonly RouteGraph _g;
        readonly List<Place> _places;
        readonly List<string> _cons;
        readonly Func<string, object, Task<(string json, string err)>> _rpc;
        readonly string _route, _routeDir;

        // lo que se está editando
        string _origin, _dest;
        readonly List<string> _stops = new();
        int _o = -1, _d = -1; readonly List<int> _vias = new();
        List<(double lat, double lon)> _path; int _pathGen;
        public long SavedId { get; private set; }
        // (2026-11-11) la comprobación del horario con este servicio como queda (HorarioSalaForm.Precheck): avisa, no impide guardar
        public Func<HorarioSalaForm.Service, Task<List<HorarioCheck.Issue>>> Precheck;

        // controles
        readonly RoundedInput _name, _first, _every, _last;
        readonly ComboBox _kind, _con, _trip, _cbOrigin, _cbDest, _mode, _cbAdd;
        readonly ThemeCheck _active;
        readonly ListBox _list;
        readonly Label _help, _err, _km;
        readonly RouteLiveMap _map;
        readonly Label[] _tool = new Label[6];
        int _tm = 2;   // 0 origen · 1 destino · 2 parada · 3 vía de salida · 4 vía de llegada · 5 vía de paso
        Func<double, double, PointF> _scr;
        readonly List<(int edge, List<(double lat, double lon)> pts)> _lines = new();
        int _hoverEdge = -1; Place _hoverPlace;
        Point _down;

        static readonly Color COrigin = Color.FromArgb(102, 187, 106), CDest = Color.FromArgb(239, 83, 80), CVia = Color.FromArgb(66, 165, 245),
                              CStop = Color.FromArgb(120, 200, 230), CPath = Color.FromArgb(255, 213, 79), CWarn = Color.FromArgb(255, 183, 77);

        sealed class Row { public int Kind; public string Text, Sub; public Color Col; public string Stop; public int Via = -1; public bool Warn; }
        readonly List<Row> _rows = new();

        public HorarioServicioForm(HorarioSalaForm.Service sv, List<Place> places, List<string> consists, RouteGraph g, RouteMapData data,
                                   Func<string, object, Task<(string json, string err)>> rpc, string route, string routeDir)
        {
            _isNew = sv == null; _s = sv ?? new HorarioSalaForm.Service();
            _g = g; _rpc = rpc; _route = route; _routeDir = routeDir;
            _places = new List<Place>(places ?? new List<Place>());
            // (los guardados que ya no salen en la ruta, como «otro»: que no se pierdan)
            foreach (var n in new[] { _s.Origin, _s.Destination }.Concat(StopsOf(_s.Stops)))
                if (!string.IsNullOrWhiteSpace(n) && !_places.Any(p => MainMenuForm.SamePlace(p.Name, n, exact: true)))
                {
                    // (p. ej. «Industrias CAF 9», como lo llama el despachador: con el tipo y el sitio de «Industrias CAF»)
                    var like = _places.FirstOrDefault(p => MainMenuForm.SamePlace(p.Name, n));
                    _places.Add(like != null ? new Place { Name = n.Trim(), Kind = like.Kind, Lat = like.Lat, Lon = like.Lon, HasPos = like.HasPos } : new Place { Name = n.Trim(), Kind = 3 });
                }
            _cons = new List<string>(consists ?? new List<string>());
            if (_s.Consist != "" && !_cons.Contains(_s.Consist)) _cons.Insert(0, _s.Consist);
            _origin = _s.Origin; _dest = _s.Destination;
            _stops.AddRange(StopsOf(_s.Stops).Select(Canon));
            ParseHint(_s.Hint);

            Text = _isNew ? Tr("Nuevo servicio") : Tr("Editar servicio");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(Theme.Px(1440), Theme.Px(860)); MinimumSize = new Size(Theme.Px(1100), Theme.Px(640));
            DialogKit.Dress(this);
            Font = Theme.Font(9.5f);

            var head = new DialogKit.Header(_isNew ? Tr("Nuevo servicio") : string.Format(Tr("Itinerario de {0}"), _s.Name),
                                            Tr("Trenes IA de la sala compartida de CGL"), "🚆") { Dock = DockStyle.Top };

            // ── izquierda: datos e itinerario
            var left = new Panel { Dock = DockStyle.Left, Width = Theme.Px(470), BackColor = Theme.Surface, Padding = new Padding(Theme.Px(16), Theme.Px(10), Theme.Px(16), Theme.Px(10)) };
            var rows = new List<Control>();
            Control Cap(string t) => new Label { Text = t, Height = Theme.Px(24), Font = Theme.Font(8.5f, FontStyle.Bold), ForeColor = Theme.Subtle, BackColor = left.BackColor, TextAlign = ContentAlignment.BottomLeft };
            Label Lab(string t) => new Label { Text = t, AutoSize = false, Height = Theme.Px(20), ForeColor = Theme.Subtle, BackColor = left.BackColor, TextAlign = ContentAlignment.BottomLeft, Dock = DockStyle.Top };
            // una fila con varias columnas: (texto, control, ancho relativo)
            Control Cols(params (string label, Control c, int w)[] cs)
            {
                var p = new TableLayoutPanel { ColumnCount = cs.Length, RowCount = 1, Height = Theme.Px(58), BackColor = left.BackColor, Margin = Padding.Empty, Padding = Padding.Empty };
                foreach (var (_, _, w) in cs) p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, w));
                for (int i = 0; i < cs.Length; i++)
                {
                    var cell = new Panel { Dock = DockStyle.Fill, BackColor = left.BackColor, Margin = new Padding(0, 0, i < cs.Length - 1 ? Theme.Px(8) : 0, 0) };
                    cs[i].c.Dock = DockStyle.Top;
                    cell.Controls.Add(cs[i].c); cell.Controls.Add(Lab(cs[i].label));
                    p.Controls.Add(cell, i, 0);
                }
                return p;
            }
            ComboBox Combo(IEnumerable<object> items, int sel) { var c = DialogKit.Combo(); foreach (var x in items) c.Items.Add(x); if (c.Items.Count > 0) c.SelectedIndex = Math.Max(0, Math.Min(sel, c.Items.Count - 1)); return c; }

            _name = new RoundedInput(); _name.Box.Text = _s.Name; _name.Height = Theme.Px(34);
            _kind = Combo(HorarioSalaForm.Kinds.Select(k => (object)KindName(k)), Math.Max(0, Array.IndexOf(HorarioSalaForm.Kinds, _s.Kind)));
            _con = Combo(_cons.Cast<object>(), Math.Max(0, _cons.IndexOf(_s.Consist)));
            _first = new RoundedInput(); _first.Box.Text = _s.First; _first.Height = Theme.Px(34);
            _every = new RoundedInput(); _every.Box.Text = _s.Every.ToString(CultureInfo.InvariantCulture); _every.Height = Theme.Px(34);
            _last = new RoundedInput(); _last.Box.Text = _s.Last; _last.Height = Theme.Px(34);
            _active = new ThemeCheck { Text = Tr("Activo (sale según el horario)"), Checked = _s.Active, ForeColor = Theme.Text, BackColor = left.BackColor, AutoSize = false, Height = Theme.Px(28) };
            string[] trips = { Tr("Solo ida"), Tr("Ida y vuelta"), Tr("Circular") };
            _trip = Combo(trips, Math.Max(0, Array.IndexOf(HorarioSalaForm.Trips, _s.Trip ?? "")));
            _cbOrigin = Combo(_places.Cast<object>(), Math.Max(0, IndexOfPlace(_origin)));
            _cbDest = Combo(_places.Cast<object>(), Math.Max(0, IndexOfPlace(_dest)));
            string[] modes = { Tr("En todas las estaciones"), Tr("Sin paradas intermedias"), Tr("Solo en las del itinerario") };
            _mode = Combo(modes, _s.Stops == "todas" ? 0 : _s.Stops == "ninguna" ? 1 : 2);
            if (_isNew) { _origin = _places.Count > 0 ? _places[_cbOrigin.SelectedIndex].Name : ""; _dest = _places.Count > 1 ? _places[Math.Min(1, _places.Count - 1)].Name : _origin; _cbDest.SelectedIndex = Math.Min(1, _places.Count - 1); }

            rows.Add(Cap(Tr("DATOS DEL SERVICIO")));
            rows.Add(Cols((Tr("Nombre (p. ej. C1)"), _name, 55), (Tr("Tipo"), _kind, 45)));
            rows.Add(Cols((Tr("Composición"), _con, 100)));
            rows.Add(Cols((Tr("Primera salida"), _first, 34), (Tr("Cada (min)"), _every, 30), (Tr("Última salida"), _last, 36)));
            rows.Add(_active);
            rows.Add(Cap(Tr("ITINERARIO")));
            rows.Add(Cols((Tr("Recorrido"), _trip, 50), (Tr("Paradas"), _mode, 50)));
            rows.Add(Cols((Tr("Origen"), _cbOrigin, 50), (Tr("Destino"), _cbDest, 50)));

            _list = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Surface2, ForeColor = Theme.Text, Font = Theme.Font(9f),
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.Px(40), IntegralHeight = false
            };
            _list.DrawItem += DrawRow;
            _list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
            Native.UseDarkScrollBars(_list);
            var addBar = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = Theme.Px(42), ColumnCount = 3, BackColor = left.BackColor, Padding = new Padding(0, Theme.Px(6), 0, 0) };
            addBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            addBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            addBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _cbAdd = Combo(_places.Cast<object>(), 0); _cbAdd.Dock = DockStyle.Fill;
            var add = DialogKit.Secondary(Tr("Añadir parada"), 130); add.Margin = new Padding(Theme.Px(6), 0, 0, 0);
            var del = DialogKit.Secondary(Tr("Quitar"), 90); del.Margin = new Padding(Theme.Px(6), 0, 0, 0);
            add.Click += (s, e) => { if (_cbAdd.SelectedItem is Place p) ToggleStop(p.Name, true); };
            del.Click += (s, e) => RemoveSelected();
            addBar.Controls.Add(_cbAdd, 0, 0); addBar.Controls.Add(add, 1, 0); addBar.Controls.Add(del, 2, 0);
            _km = new Label { Dock = DockStyle.Bottom, Height = Theme.Px(24), ForeColor = Theme.Subtle, BackColor = left.BackColor, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Font(8.75f) };
            _help = new Label { Dock = DockStyle.Bottom, Height = Theme.Px(40), ForeColor = Theme.Subtle, BackColor = left.BackColor, Font = Theme.Font(8.5f) };
            var listHost = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface2, BorderColor = Theme.Border, Radius = 9, Padding = new Padding(Theme.Px(6)) };
            listHost.Controls.Add(_list);
            left.Controls.Add(listHost); left.Controls.Add(addBar); left.Controls.Add(_km); left.Controls.Add(_help);
            for (int i = rows.Count - 1; i >= 0; i--) { rows[i].Dock = DockStyle.Top; left.Controls.Add(rows[i]); }

            // ── derecha: el mapa con su barra de herramientas
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Px(12), Theme.Px(10), Theme.Px(14), Theme.Px(10)) };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Theme.Px(44), BackColor = Theme.Bg, WrapContents = false, Padding = new Padding(0, 0, 0, Theme.Px(6)) };
            var barLab = new Label { Text = Tr("Clic en el mapa:"), AutoSize = true, ForeColor = Theme.Subtle, BackColor = Theme.Bg, Margin = new Padding(0, Theme.Px(10), Theme.Px(8), 0) };
            bar.Controls.Add(barLab);
            string[] tools = { Tr("Origen"), Tr("Destino"), Tr("Parada"), Tr("Vía de salida"), Tr("Vía de llegada"), Tr("Vía de paso") };
            Color[] tcol = { COrigin, CDest, CStop, COrigin, CDest, CVia };
            for (int k = 0; k < tools.Length; k++)
            {
                int kk = k;
                var b = new Label { Text = tools[k], AutoSize = false, Size = new Size(Theme.Px(k >= 3 ? 112 : 84), Theme.Px(32)), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand, Font = Theme.Font(8.75f, FontStyle.Bold), Tag = tcol[k], Margin = new Padding(0, 0, Theme.Px(6), 0) };
                b.Click += (s, e) => { _tm = kk; Tools(); };
                _tool[k] = b; bar.Controls.Add(b);
            }
            _map = new RouteLiveMap { Dock = DockStyle.Fill };
            _map.EmptyText = data == null ? Tr("El mapa de la ruta aún no está listo.") : "";
            if (data != null) _map.SetData(data);
            _map.Overlay = (gr, scr) => { _scr = scr; DrawMap(gr, scr); };
            _map.MouseDown += (s, e) => _down = e.Location;
            _map.MouseUp += (s, e) => { if (Math.Abs(e.X - _down.X) + Math.Abs(e.Y - _down.Y) <= 5) MapClick(e.Location, e.Button); };
            _map.MouseMove += (s, e) => Hover(e.Location);
            right.Controls.Add(_map); right.Controls.Add(bar);

            // ── pie
            var foot = new DialogKit.Footer();
            var ok = DialogKit.Primary(Tr("Guardar"), 150);
            var cancel = DialogKit.Secondary(Tr("Cancelar"), 120);
            var test = DialogKit.Secondary(Tr("Probar camino"), 140);
            _err = new Label { AutoSize = false, Width = Theme.Px(640), ForeColor = Color.FromArgb(255, 138, 128), BackColor = foot.BackColor, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Theme.Px(16), 0, 0, 0) };
            foot.AddLeft(_err);
            foot.AddRight(cancel, ok, test);
            ok.Click += async (s, e) => { ok.Enabled = false; try { await Save(); } finally { if (!IsDisposed) ok.Enabled = true; } };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            test.Click += (s, e) => TestPath();

            Controls.Add(right); Controls.Add(left); Controls.Add(head); Controls.Add(foot);

            // las vías del mapa (para pinchar las marcadas)
            if (_g != null)
                for (int e = 0; e < _g.Edges.Length; e++)
                {
                    var E = _g.Edges[e];
                    if (E.Vec < 0 || E.X == null || E.X.Length < 2) continue;
                    var pts = new List<(double, double)>();
                    for (int i = 0; i < E.X.Length; i++) pts.Add(_g.Unproj(E.X[i], E.Y[i]));
                    _lines.Add((e, pts));
                }

            _cbOrigin.SelectedIndexChanged += (s, e) => { if (_cbOrigin.SelectedItem is Place p) SetOrigin(p.Name); };
            _cbDest.SelectedIndexChanged += (s, e) => { if (_cbDest.SelectedItem is Place p) SetDest(p.Name); };
            _trip.SelectedIndexChanged += (s, e) => Changed();
            _mode.SelectedIndexChanged += (s, e) => Changed(false);
            _kind.SelectedIndexChanged += (s, e) => Changed(false);
            Shown += (s, e) => { Tools(); Changed(); };
        }

        static string KindName(string k) => k == "larga" ? Tr("Larga distancia") : k == "media" ? Tr("Media distancia") : k == "mercancias" ? Tr("Mercancías") : Tr("Cercanías");
        static IEnumerable<string> StopsOf(string stops) => stops == null || stops == "todas" || stops == "ninguna" ? Enumerable.Empty<string>()
                                                           : stops.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0);
        // el nombre tal y como está en la lista de sitios (para comparar y enseñar)
        string Canon(string n) => _places.FirstOrDefault(p => MainMenuForm.SamePlace(p.Name, n, exact: true))?.Name ?? n.Trim();
        int IndexOfPlace(string n) => _places.FindIndex(p => MainMenuForm.SamePlace(p.Name, n, exact: true));
        Place PlaceOf(string n) => _places.FirstOrDefault(p => MainMenuForm.SamePlace(p.Name, n, exact: true));
        string TripCode => HorarioSalaForm.Trips[Math.Max(0, Math.Min(HorarioSalaForm.Trips.Length - 1, _trip.SelectedIndex))];
        bool Freight => HorarioSalaForm.Kinds[Math.Max(0, _kind.SelectedIndex)] == "mercancias";

        void ParseHint(string h)
        {
            foreach (var part in (h ?? "").Split(';'))
            {
                var kv = part.Split('='); if (kv.Length != 2) continue;
                if (kv[0] == "o" && int.TryParse(kv[1], out int o)) _o = o;
                else if (kv[0] == "d" && int.TryParse(kv[1], out int d)) _d = d;
                else if (kv[0] == "v") foreach (var x in kv[1].Split(',')) if (int.TryParse(x, out int v) && !_vias.Contains(v)) _vias.Add(v);
            }
        }
        string Hint => _o < 0 && _d < 0 && _vias.Count == 0 ? "" :
            string.Join(";", new[] { _o >= 0 ? "o=" + _o : null, _d >= 0 ? "d=" + _d : null, _vias.Count > 0 ? "v=" + string.Join(",", _vias) : null }.Where(x => x != null));

        // ── cambios
        void SetOrigin(string n)
        {
            if (MainMenuForm.SamePlace(n, _origin, exact: true)) return;
            _origin = n;
            if (_o >= 0 && !VecOf(_o, n)) _o = -1;   // (la vía de salida marcada era de la otra estación)
            _stops.RemoveAll(x => MainMenuForm.SamePlace(x, n, exact: true));
            int i = IndexOfPlace(n); if (i >= 0 && _cbOrigin.SelectedIndex != i) _cbOrigin.SelectedIndex = i;
            Changed();
        }
        void SetDest(string n)
        {
            if (MainMenuForm.SamePlace(n, _dest, exact: true)) return;
            _dest = n;
            if (_d >= 0 && !VecOf(_d, n)) _d = -1;
            _stops.RemoveAll(x => MainMenuForm.SamePlace(x, n, exact: true));
            int i = IndexOfPlace(n); if (i >= 0 && _cbDest.SelectedIndex != i) _cbDest.SelectedIndex = i;
            Changed();
        }
        void ToggleStop(string n, bool onlyAdd = false)
        {
            if (MainMenuForm.SamePlace(n, _origin, exact: true) || MainMenuForm.SamePlace(n, _dest, exact: true)) return;
            int k = _stops.FindIndex(x => MainMenuForm.SamePlace(x, n, exact: true));
            if (k >= 0) { if (!onlyAdd) _stops.RemoveAt(k); }
            else _stops.Add(Canon(n));
            if (_stops.Count > 0 && _mode.SelectedIndex != 2) _mode.SelectedIndex = 2;   // (con paradas elegidas: «solo en las del itinerario»)
            Changed(false);
        }
        void RemoveSelected()
        {
            int i = _list.SelectedIndex; if (i < 0 || i >= _rows.Count) return;
            var r = _rows[i];
            if (r.Stop != null) _stops.RemoveAll(x => MainMenuForm.SamePlace(x, r.Stop, exact: true));
            else if (r.Via >= 0) _vias.Remove(r.Via);
            else if (r.Kind == 0) _o = -1;
            else if (r.Kind == 3) _d = -1;
            else return;
            Changed(r.Via >= 0 || r.Kind == 0 || r.Kind == 3);
        }

        // ¿la vía (nodo vectorial) tiene andén o vía de apartadero del sitio?
        bool VecOf(int vec, string place)
        {
            if (_g == null) return true;
            foreach (var E in _g.Edges)
                if (E.Vec == vec && E.Platforms.Concat(E.Sidings).Any(p => MainMenuForm.SamePlace(p.name, place))) return true;
            return false;
        }

        // algo ha cambiado: el camino (si hace falta) y la lista
        async void Changed(bool path = true)
        {
            _err.Text = "";
            Rebuild();
            if (!path || _g == null) return;
            int gen = ++_pathGen;
            string o = _origin, d = _dest, h = Hint, t = TripCode;
            _km.Text = Tr("Calculando el camino…");
            var p = await Task.Run(() => { try { return HorarioSalaForm.PathPoints(_g, o, d, h, t); } catch { return null; } });
            if (IsDisposed || gen != _pathGen) return;
            _path = p;
            Rebuild();
            if (p != null && p.Count > 1 && _map.Data != null) _map.FitLL(p);
            _map.Invalidate();
        }

        // distancia (m) de un punto al camino y en qué parte del camino cae: el PRIMER paso cerca (en una de ida y vuelta, el de
        // la ida); fuera del camino, al final
        const double NearM = 250;
        (double m, int at) OnPath(double lat, double lon)
        {
            if (_path == null || _path.Count == 0) return (double.MaxValue, int.MaxValue);
            double best = double.MaxValue; int first = -1;
            double kx = 111320 * Math.Cos(lat * Math.PI / 180), ky = 110540;
            for (int i = 0; i < _path.Count; i++)
            {
                double dx = (_path[i].lon - lon) * kx, dy = (_path[i].lat - lat) * ky, d = Math.Sqrt(dx * dx + dy * dy);
                if (d < best) best = d;
                if (first < 0 && d <= NearM) first = i;
            }
            return (best, first >= 0 ? first : int.MaxValue);
        }

        // (un sitio grande, p. ej. una estación con muchas vías: por el andén o vía más cerca del camino, no por su centro)
        readonly Dictionary<string, List<(double lat, double lon)>> _spots = new(StringComparer.CurrentCultureIgnoreCase);
        (double m, int at) OnPathPlace(string name, Place p)
        {
            if (!_spots.TryGetValue(name, out var l))
            {
                l = new List<(double, double)>();
                if (_g != null) foreach (var (e, off) in HorarioSalaForm.PlaceSpots(_g, name)) { var (x, y) = _g.PointAt(e, off); l.Add(_g.Unproj(x, y)); }
                if (l.Count == 0 && p != null && p.HasPos) l.Add((p.Lat, p.Lon));
                _spots[name] = l;
            }
            double bm = double.MaxValue; int ba = int.MaxValue;
            foreach (var (la, lo) in l) { var (m, at) = OnPath(la, lo); bm = Math.Min(bm, m); if (m <= NearM) ba = Math.Min(ba, at); }
            return (bm, bm <= NearM ? ba : int.MaxValue);
        }

        (double lat, double lon)? VecMid(int vec)
        {
            if (_g == null) return null;
            var E = _g.Edges.FirstOrDefault(x => x.Vec == vec); if (E == null) return null;
            var (x, y) = _g.PointAt(Array.IndexOf(_g.Edges, E), E.Len / 2);
            return _g.Unproj(x, y);
        }

        string Describe(int vec)
        {
            var E = _g?.Edges.FirstOrDefault(x => x.Vec == vec);
            if (E == null) return string.Format(Tr("vía (nodo {0})"), vec);
            var raw = E.PlatformNames.Select(x => x.raw).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? E.Platforms.Select(x => x.name).FirstOrDefault() ?? E.Sidings.Select(x => x.name).FirstOrDefault();
            return raw != null ? $"{raw} ({vec})" : string.Format(Tr("vía (nodo {0})"), vec);
        }

        // la lista del itinerario: origen → (paradas y vías de paso, en el orden del camino) → destino → vuelta
        void Rebuild()
        {
            _rows.Clear();
            var po = PlaceOf(_origin); var pd = PlaceOf(_dest);
            _rows.Add(new Row { Kind = 0, Col = COrigin, Text = Tr("Sale de") + " " + _origin + Tagged(po), Sub = Tr("Vía de salida") + ": " + (_o >= 0 ? Describe(_o) : Tr("la que esté libre")) });
            var mids = new List<(int at, Row r)>();
            int mode = _mode.SelectedIndex;
            if (mode == 2 && !Freight)
                foreach (var n in _stops)
                {
                    var p = PlaceOf(n);
                    var (m, at) = OnPathPlace(n, p);
                    bool off = _path != null && m > NearM;
                    mids.Add((at, new Row { Kind = 1, Col = CStop, Stop = n, Warn = off, Text = n + Tagged(p),
                                            Sub = off ? Tr("No está en el camino: el tren no pasa por ahí (márcala como vía de paso en el mapa)") : Tr("Para aquí") }));
                }
            foreach (int v in _vias)
            {
                var mid = VecMid(v);
                var (_, at) = mid.HasValue ? OnPath(mid.Value.lat, mid.Value.lon) : (0, int.MaxValue);
                mids.Add((at, new Row { Kind = 2, Col = CVia, Via = v, Text = Tr("Pasa por") + " " + Describe(v), Sub = Tr("Vía de paso marcada") }));
            }
            int stopNo = 0;
            foreach (var (_, r) in mids.OrderBy(x => x.at))
            {
                if (r.Kind == 1) r.Text = (++stopNo) + ".  " + r.Text;
                _rows.Add(r);
            }
            if (mode == 0 && !Freight) _rows.Insert(1, new Row { Kind = 5, Col = CStop, Text = Tr("Para en todas las estaciones del camino"), Sub = Tr("Para elegir solo algunas: «Solo en las del itinerario»") });
            if (mode == 1 || Freight) _rows.Insert(1, new Row { Kind = 5, Col = Theme.Subtle, Text = Tr("Sin paradas intermedias"), Sub = Freight ? Tr("Los trenes de mercancías van de origen a destino sin parar") : "" });
            _rows.Add(new Row { Kind = 3, Col = CDest, Text = (TripCode == "circular" ? Tr("Pasa por") : Tr("Llega a")) + " " + _dest + Tagged(pd) + (TripCode == "vuelta" ? "  ·  " + Tr("cambia de cabina") : ""),
                                Sub = Tr("Vía de llegada") + ": " + (_d >= 0 ? Describe(_d) : Tr("la que esté libre")) });
            if (TripCode == "vuelta") _rows.Add(new Row { Kind = 4, Col = COrigin, Text = Tr("Vuelve a") + " " + _origin, Sub = Tr("Con las mismas paradas, al revés") });
            else if (TripCode == "circular") _rows.Add(new Row { Kind = 4, Col = COrigin, Text = Tr("Acaba en") + " " + _origin, Sub = Tr("Sin cambiar de cabina") });

            int sel = _list.SelectedIndex;
            _list.BeginUpdate(); _list.Items.Clear(); foreach (var r in _rows) _list.Items.Add(r.Text); _list.EndUpdate();
            if (sel >= 0 && sel < _list.Items.Count) _list.SelectedIndex = sel;
            _help.Text = Freight ? Tr("Mercancías: el origen y el destino pueden ser terminales de mercancías o apartaderos.")
                                 : Tr("Clic derecho en un sitio del mapa: añadir o quitar una parada. Supr: quitar lo elegido de la lista.");
            if (_g == null) _km.Text = Tr("No se ha podido leer la ruta.");
            else if (_path == null) _km.Text = _pathGen > 0 ? string.Format(Tr("No hay vía entre {0} y {1}."), _origin, _dest) : "";
            else _km.Text = string.Format(Tr("Camino: unos {0} km (el despachador busca el camino exacto al crear el tren)"), (Length(_path) / 1000).ToString("0.0", CultureInfo.CurrentCulture));
            _map?.Invalidate();
        }

        static string Tagged(Place p) => p == null || p.Tag.Length == 0 ? "" : "  ·  " + p.Tag;

        static double Length(List<(double lat, double lon)> p)
        {
            double s = 0;
            for (int i = 1; i < p.Count; i++)
            {
                double kx = 111320 * Math.Cos(p[i].lat * Math.PI / 180), dx = (p[i].lon - p[i - 1].lon) * kx, dy = (p[i].lat - p[i - 1].lat) * 110540;
                s += Math.Sqrt(dx * dx + dy * dy);
            }
            return s;
        }

        void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _rows.Count) return;
            var r = _rows[e.Index]; var g = e.Graphics; var rc = e.Bounds;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(sel ? Color.FromArgb(48, 62, 74) : Theme.Surface2)) g.FillRectangle(bg, rc);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // la línea del itinerario y su punto
            int cx = rc.X + Theme.Px(16), cy = rc.Y + rc.Height / 2;
            using (var lp = new Pen(Color.FromArgb(90, 160, 170, 180), 2f))
            {
                if (e.Index > 0) g.DrawLine(lp, cx, rc.Top, cx, cy);
                if (e.Index < _rows.Count - 1) g.DrawLine(lp, cx, cy, cx, rc.Bottom);
            }
            float rr = r.Kind == 0 || r.Kind == 3 ? Theme.Px(7) : Theme.Px(5);
            var col = r.Warn ? CWarn : r.Col;
            using (var b = new SolidBrush(r.Kind == 3 ? Theme.Surface2 : col)) g.FillEllipse(b, cx - rr, cy - rr, 2 * rr, 2 * rr);
            using (var p = new Pen(col, r.Kind == 3 ? 2.5f : 1.2f)) g.DrawEllipse(p, cx - rr, cy - rr, 2 * rr, 2 * rr);
            if (r.Kind == 2) using (var p = new Pen(Theme.Surface2, 2f)) g.DrawLine(p, cx - rr + 2, cy, cx + rr - 2, cy);
            g.SmoothingMode = SmoothingMode.None;
            int x = rc.X + Theme.Px(32), w = rc.Width - Theme.Px(36);
            using var fb = Theme.Font(9.25f, FontStyle.Bold); using var fs = Theme.Font(8.25f);
            TextRenderer.DrawText(g, (r.Warn ? "⚠ " : "") + r.Text, fb, new Rectangle(x, rc.Y + Theme.Px(3), w, Theme.Px(18)), r.Kind == 5 ? Theme.Subtle : Theme.Text, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, r.Sub ?? "", fs, new Rectangle(x, rc.Y + Theme.Px(21), w, Theme.Px(16)), r.Warn ? CWarn : Theme.Subtle, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        void Tools()
        {
            for (int k = 0; k < _tool.Length; k++)
            {
                var b = _tool[k]; var c = (Color)b.Tag;
                b.BackColor = _tm == k ? Color.FromArgb(80, c) : Theme.Surface2;
                b.ForeColor = _tm == k ? Color.White : Theme.Text;
            }
            _map.Cursor = Cursors.Default;
        }

        // ── el mapa
        Place PlaceAt(Point p)
        {
            if (_scr == null) return null;
            Place best = null; double bd = Theme.Px(12);
            foreach (var pl in _places)
            {
                if (!pl.HasPos) continue;
                var q = _scr(pl.Lat, pl.Lon);
                double d = Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y));
                if (d < bd) { bd = d; best = pl; }
            }
            return best;
        }

        int EdgeAt(Point p)
        {
            if (_scr == null) return -1;
            double best = Theme.Px(9); int bestE = -1;
            foreach (var (e, pts) in _lines)
            {
                PointF prev = default; bool first = true;
                foreach (var (lat, lon) in pts)
                {
                    var q = _scr(lat, lon);
                    if (!first)
                    {
                        double dx = q.X - prev.X, dy = q.Y - prev.Y, l2 = dx * dx + dy * dy;
                        double t = l2 < 1e-6 ? 0 : Math.Max(0, Math.Min(1, ((p.X - prev.X) * dx + (p.Y - prev.Y) * dy) / l2));
                        double ex = prev.X + t * dx - p.X, ey = prev.Y + t * dy - p.Y, d = Math.Sqrt(ex * ex + ey * ey);
                        if (d < best) { best = d; bestE = e; }
                    }
                    prev = q; first = false;
                }
            }
            return bestE;
        }

        void Hover(Point p)
        {
            Place hp = _tm <= 2 ? PlaceAt(p) : null;
            int he = _tm >= 3 ? EdgeAt(p) : -1;
            if (hp != _hoverPlace || he != _hoverEdge)
            {
                _hoverPlace = hp; _hoverEdge = he;
                _map.Cursor = hp != null || he >= 0 ? Cursors.Hand : Cursors.Default;
                _map.Invalidate();
            }
        }

        void MapClick(Point p, MouseButtons b)
        {
            if (b == MouseButtons.Right) { var pl = PlaceAt(p); if (pl != null) ToggleStop(pl.Name); return; }
            if (b != MouseButtons.Left) return;
            if (_tm <= 2)
            {
                var pl = PlaceAt(p); if (pl == null) return;
                if (_tm == 0) SetOrigin(pl.Name); else if (_tm == 1) SetDest(pl.Name); else ToggleStop(pl.Name);
                return;
            }
            int edge = EdgeAt(p); if (edge < 0 || _g == null) return;
            var E = _g.Edges[edge]; if (E.Vec < 0) return;
            if (_tm == 3)
            {
                if (!VecOf(E.Vec, _origin)) { _err.Text = string.Format(Tr("Esa vía no es de {0}: la de salida tiene que ser una vía del origen."), _origin); return; }
                _o = _o == E.Vec ? -1 : E.Vec;
            }
            else if (_tm == 4)
            {
                if (!VecOf(E.Vec, _dest)) { _err.Text = string.Format(Tr("Esa vía no es de {0}: la de llegada tiene que ser una vía del destino."), _dest); return; }
                _d = _d == E.Vec ? -1 : E.Vec;
            }
            else { if (_vias.Contains(E.Vec)) _vias.Remove(E.Vec); else _vias.Add(E.Vec); }   // (otro clic en la misma: se quita)
            Changed();
        }

        void DrawMap(Graphics g, Func<double, double, PointF> scr)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // el camino
            if (_path != null && _path.Count > 1)
            {
                var pts = _path.Select(x => scr(x.lat, x.lon)).ToArray();
                using (var halo = new Pen(Color.FromArgb(130, 0, 0, 0), 9f) { LineJoin = LineJoin.Round }) g.DrawLines(halo, pts);
                using (var pen = new Pen(Color.FromArgb(235, CPath), 4.5f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLines(pen, pts);
            }
            // las vías marcadas
            void Line(int vec, Color c, float w, string tag)
            {
                var E = _g?.Edges.FirstOrDefault(x => x.Vec == vec); if (E == null) return;
                int e = Array.IndexOf(_g.Edges, E);
                var l = _lines.FirstOrDefault(x => x.edge == e); if (l.pts == null) return;
                var pts = l.pts.Select(x => scr(x.lat, x.lon)).ToArray();
                using var pen = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                if (pts.Length > 1) g.DrawLines(pen, pts);
                if (tag != null && pts.Length > 0) Chip(g, tag, pts[pts.Length / 2], c);
            }
            if (_hoverEdge >= 0 && _hoverEdge < (_g?.Edges.Length ?? 0)) Line(_g.Edges[_hoverEdge].Vec, Color.FromArgb(200, 255, 255, 255), 5f, null);
            for (int i = 0; i < _vias.Count; i++) Line(_vias[i], CVia, 6f, (i + 1).ToString(CultureInfo.InvariantCulture));
            if (_o >= 0) Line(_o, COrigin, 7f, Tr("salida"));
            if (_d >= 0) Line(_d, CDest, 7f, Tr("llegada"));
            // los sitios: apartaderos (rombos pequeños), mercancías (cuadrados) y estaciones (puntos); los del itinerario, encima
            foreach (var pl in _places)
            {
                if (!pl.HasPos) continue;
                var q = scr(pl.Lat, pl.Lon);
                bool hv = pl == _hoverPlace;
                float r = hv ? 5.5f : pl.Kind == 2 ? 3f : 3.4f;
                var c = pl.Kind == 1 ? RouteLiveMap.FreightCol : pl.Kind == 2 ? Color.FromArgb(150, 156, 164) : Color.FromArgb(215, 220, 226);
                using var b = new SolidBrush(hv ? Color.White : c);
                if (pl.Kind == 2) g.FillPolygon(b, new[] { new PointF(q.X, q.Y - r - 1), new PointF(q.X + r + 1, q.Y), new PointF(q.X, q.Y + r + 1), new PointF(q.X - r - 1, q.Y) });
                else if (pl.Kind == 1) g.FillRectangle(b, q.X - r, q.Y - r, 2 * r, 2 * r);
                else g.FillEllipse(b, q.X - r, q.Y - r, 2 * r, 2 * r);
                if (hv) Tag(g, pl.ToString(), q, Color.White);
            }
            int no = 0;
            foreach (var row in _rows.Where(x => x.Stop != null))
            {
                no++;
                var pl = PlaceOf(row.Stop); if (pl == null || !pl.HasPos) continue;
                var q = scr(pl.Lat, pl.Lon);
                var c = row.Warn ? CWarn : CStop;
                using (var b = new SolidBrush(c)) g.FillEllipse(b, q.X - 7, q.Y - 7, 14, 14);
                using var f = Theme.Font(7.5f, FontStyle.Bold);
                TextRenderer.DrawText(g, no.ToString(CultureInfo.InvariantCulture), f, new Rectangle((int)q.X - 8, (int)q.Y - 8, 16, 16), Color.FromArgb(20, 24, 28), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                Tag(g, pl.Name, q, c);
            }
            void End(string n, Color c, bool ring)
            {
                var pl = PlaceOf(n); if (pl == null || !pl.HasPos) return;
                var q = scr(pl.Lat, pl.Lon);
                using (var b = new SolidBrush(ring ? Color.FromArgb(20, 22, 26) : c)) g.FillEllipse(b, q.X - 8, q.Y - 8, 16, 16);
                using (var p = new Pen(ring ? c : Color.White, ring ? 3f : 2f)) g.DrawEllipse(p, q.X - 8, q.Y - 8, 16, 16);
                Tag(g, pl.Name, q, c);
            }
            End(_dest, CDest, true); End(_origin, COrigin, false);
            g.SmoothingMode = SmoothingMode.None;
        }

        static void Tag(Graphics g, string text, PointF at, Color c)
        {
            using var f = Theme.Font(8.25f, FontStyle.Bold);
            var sz = TextRenderer.MeasureText(g, text, f, Size.Empty, TextFormatFlags.NoPadding);
            var pt = new Point((int)(at.X + 11), (int)(at.Y - sz.Height / 2f));
            using (var b = new SolidBrush(Color.FromArgb(215, 16, 18, 22))) g.FillRectangle(b, pt.X - 4, pt.Y - 2, sz.Width + 8, sz.Height + 4);
            TextRenderer.DrawText(g, text, f, pt, c, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        static void Chip(Graphics g, string text, PointF at, Color c)
        {
            using var f = Theme.Font(8.25f, FontStyle.Bold);
            var sz = TextRenderer.MeasureText(g, text, f, Size.Empty, TextFormatFlags.NoPadding);
            var r = new Rectangle((int)at.X - sz.Width / 2 - 4, (int)at.Y - sz.Height - 9, sz.Width + 8, sz.Height + 4);
            using (var b = new SolidBrush(Color.FromArgb(230, 24, 28, 34))) g.FillRectangle(b, r);
            TextRenderer.DrawText(g, text, f, new Point(r.X + 4, r.Y + 2), c, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        void TestPath()
        {
            double km = _g == null ? -1 : HorarioSalaForm.PathKm(_g, _origin, _dest);
            string msg = _g == null ? Tr("No se ha podido leer la ruta.")
                       : km < 0 ? string.Format(Tr("No hay vía entre {0} y {1}."), _origin, _dest)
                       : string.Format(Tr("Hay camino de {0} a {1}: unos {2} km. (El despachador busca el camino exacto al crear el tren.)"), _origin, _dest, km.ToString("0.0", CultureInfo.CurrentCulture));
            ThemedBox.Show(this, msg, Tr("Probar camino"), MessageBoxButtons.OK, km < 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        // ── guardar (lo mismo que antes: room_ai_service_save_trip y, si cambian las vías marcadas, room_ai_service_route)
        static readonly Regex Hm = new Regex(@"^([01]\d|2[0-3]):[0-5]\d$");
        public string StopsText => _mode.SelectedIndex == 0 ? "todas" : _mode.SelectedIndex == 1 ? "ninguna"
                                 : string.Join(", ", _rows.Where(r => r.Stop != null).Select(r => r.Stop));

        async Task Save()
        {
            _err.Text = "";
            string name = _name.Box.Text.Trim(), first = _first.Box.Text.Trim(), last = _last.Box.Text.Trim(), every = _every.Box.Text.Trim();
            string Fail(string m) { _err.Text = m; return m; }
            if (name.Length == 0 || name.Length > 24 || name.Contains('|')) { Fail(Tr("El nombre: de 1 a 24 letras, sin «|».")); return; }
            if (!Hm.IsMatch(first) || !Hm.IsMatch(last)) { Fail(Tr("Las horas, como 07:30.")); return; }
            if (!int.TryParse(every, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ev) || ev < 0 || ev > 1440) { Fail(Tr("Cada cuántos minutos: de 0 a 1440.")); return; }
            if (string.IsNullOrWhiteSpace(_origin) || string.IsNullOrWhiteSpace(_dest)) { Fail(Tr("Elige el origen y el destino.")); return; }
            if (MainMenuForm.SamePlace(_origin, _dest, exact: true)) { Fail(Tr("El origen y el destino son la misma estación.")); return; }
            if (_origin.Length > 80 || _dest.Length > 80 || _origin.Contains('|') || _dest.Contains('|')) { Fail(Tr("Elige el origen y el destino.")); return; }
            int ci = _con.SelectedIndex;
            if (ci < 0 || ci >= _cons.Count) { Fail(Tr("Elige la composición.")); return; }
            string stops = StopsText;
            if (_mode.SelectedIndex == 2 && stops.Length == 0) { Fail(Tr("Añade alguna parada al itinerario, o elige «En todas las estaciones» o «Sin paradas intermedias».")); return; }
            if (stops.Length > 600) { Fail(Tr("Demasiadas paradas: quita alguna.")); return; }
            // (2026-11-11) la comprobación del horario con este servicio: si avisa de algo, se dice y se elige
            if (Precheck != null)
            {
                var tmp = new HorarioSalaForm.Service
                {
                    Id = _isNew ? 0 : _s.Id, Name = name, Kind = HorarioSalaForm.Kinds[Math.Max(0, _kind.SelectedIndex)], Origin = _origin.Trim(), Destination = _dest.Trim(),
                    Consist = _cons[ci], First = first, Every = ev, Last = last, Stops = stops, Active = _active.Checked, Hint = Hint, Trip = TripCode
                };
                _err.Text = Tr("Comprobando el horario…");
                List<HorarioCheck.Issue> warn = null;
                try { warn = await Precheck(tmp); } catch { warn = null; }
                if (IsDisposed) return;
                _err.Text = "";
                if (warn != null && warn.Count > 0)
                {
                    string txt = string.Join("\n", warn.Take(6).Select(w => "• " + (w.Sev == 2 ? Tr("BLOQUEA") : Tr("AVISO")) + (w.When.Length > 0 ? " (" + w.When + ")" : "") + ": " + (w.Where.Length > 0 ? w.Where + ": " : "") + w.What))
                               + (warn.Count > 6 ? "\n" + string.Format(Tr("…y {0} más (botón «Comprobar horario»)."), warn.Count - 6) : "");
                    if (ThemedBox.Show(this, Tr("La comprobación del horario (una estimación) avisa de esto con este servicio:") + "\n\n" + txt + "\n\n" + Tr("¿Guardar igualmente?"),
                                       Tr("Comprobar horario"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                }
            }
            string baseDir = Path.GetDirectoryName(Path.GetDirectoryName((_routeDir ?? "").TrimEnd('\\', '/'))) ?? "";
            string spec = AiConsistSpec.Build(baseDir, _cons[ci]);
            if (spec == null) { Fail(string.Format(Tr("No se puede leer la composición «{0}» en tu Open Rails."), _cons[ci])); return; }
            var (json, err) = await _rpc("room_ai_service_save_trip", new
            {
                p_id = !_isNew ? (long?)_s.Id : null, p_name = name, p_kind = HorarioSalaForm.Kinds[Math.Max(0, _kind.SelectedIndex)], p_origin = _origin.Trim(), p_destination = _dest.Trim(),
                p_consist = _cons[ci], p_first_time = first, p_every_min = ev, p_last_time = last, p_stops = stops, p_active = _active.Checked, p_route = _route,
                p_consist_spec = spec, p_trip = TripCode
            });
            if (IsDisposed) return;
            if (err != null)
            {
                Fail(err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0 || err.IndexOf("room_ai_service_save_trip", StringComparison.OrdinalIgnoreCase) >= 0
                     ? Tr("El servidor aún no guarda el recorrido de ida y vuelta (falta horario-gestores-vueltas.sql).") : err);
                return;
            }
            long id = _s.Id;
            if (_isNew && long.TryParse((json ?? "").Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out long nid)) id = nid;
            SavedId = id;
            // las vías marcadas (salida, llegada y de paso), si han cambiado
            if (Hint != (_s.Hint ?? "") && id > 0)
            {
                var (_, e2) = await _rpc("room_ai_service_route", new { p_id = id, p_route_hint = Hint });
                if (IsDisposed) return;
                if (e2 != null)
                    ThemedBox.Show(this, (e2.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0 ? Tr("El servidor aún no guarda el recorrido de los servicios (falta horario-recorrido.sql).") : e2)
                                   + "\n\n" + Tr("El resto del servicio sí se ha guardado."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
