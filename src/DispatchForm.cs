// PUESTO DE MANDO (1.2.75). Ventana del puesto de mando de la sala compartida, como un CTC:
//  · el PLANO GENERAL de la ruta (al abrir): las estaciones, los trenes, las dependencias con jefe de circulación (las tuyas,
//    en verde) y la 🔔 donde alguien pide paso. Un clic en una estación o en un tramo abre esa dependencia;
//  · cada DEPENDENCIA (una estación, o un trayecto entre estaciones): su esquema videográfico (Schematic.cs,
//    SchematicDraw.cs) con lo que pasa en directo: la posición de las agujas, el aspecto de las señales, los cantones
//    ocupados y los itinerarios establecidos. «← Plano general» (o Esc) vuelve al plano;
//  · a la izquierda, la lista de dependencias (estaciones y, después, trayectos).
// Los datos llegan por una conexión de SelectOR con la sala (SwitchPilot, como el mapa de la sala), sin entrar en ella: no
// hace falta estar conduciendo. Rueda del ratón: zoom; arrastrar: mover; doble clic: ajustar a la ventana.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class DispatchForm : Form
    {
        readonly string _routeDir, _host, _me; readonly int _port;
        readonly Func<string, RouteGraph> _buildGraph;
        RouteGraph _g; RoomMapGeo _geo; List<Schematic.Zone> _zones;
        readonly Dictionary<string, Schematic> _cache = new(StringComparer.Ordinal);   // esquema de cada dependencia
        Dictionary<int, List<(double off, int dir, int obj)>> _sbe;                    // señales de cada tramo, con su sentido
        List<(string name, double x, double y, int n)> _stations;                       // estaciones: su centro
        RectangleF _routeBox;                                                            // la ruta entera (proyección local)
        List<SchematicHeader> _heads = new(); SchematicHeader _sel;
        Schematic _cur; string _curZone;                                                 // la dependencia que se ve (null: el plano)
        SwitchPilot _pilot;
        readonly Dictionary<int, (int pos, DateTime at)> _swSet = new();
        readonly ListBox _list;
        readonly Canvas _canvas;
        readonly Label _status, _title;
        readonly Timer _timer, _blink;
        bool _blinking;
        PointF _origin; float _zoom = 1.4f; bool _fitted;
        PointF _ovOrigin; float _ovZoom = 0.05f; bool _ovFitted;
        Point _dragFrom; PointF _dragOrigin; bool _dragging;
        string _hover;                                                                   // (plano) la dependencia bajo el ratón
        RectangleF _backBox;                                                             // «← Plano general»
        // (1.2.76) el tablero: toda la ruta en una sola pantalla (RouteBoard.cs). Mientras se prepara, el plano de antes
        RouteBoard _board; PointF _bOrigin; float _bZoom = 0.05f; bool _bFitted;
        bool Board => _board != null;

        static string General => "🗺  " + I18n.T("Plano general");
        bool Overview => _curZone == null;

        sealed class Canvas : Panel { public Canvas() { DoubleBuffered = true; ResizeRedraw = true; } }

        static string Tr(string s) => I18n.T(s);

        public DispatchForm(string routeDir, string host, int port, string me, Func<string, RouteGraph> buildGraph)
        {
            _routeDir = routeDir; _host = host; _port = port; _me = string.IsNullOrWhiteSpace(me) ? "SelectOR" : me.Trim(); _buildGraph = buildGraph;
            Text = Tr("Puesto de mando · Sala compartida de CGL");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(Theme.Px(1280), Theme.Px(760)); MinimumSize = new Size(Theme.Px(760), Theme.Px(460));
            DialogKit.Dress(this);
            KeyPreview = true;

            var top = new Panel { Dock = DockStyle.Top, Height = Theme.Px(40), BackColor = Color.FromArgb(21, 26, 32) };
            _title = new Label { Text = Tr("PUESTO DE MANDO"), AutoSize = true, Font = Theme.Font(10f, FontStyle.Bold), ForeColor = Theme.Text, BackColor = top.BackColor, Location = new Point(Theme.Px(14), Theme.Px(11)) };
            _status = new Label { Text = Tr("Preparando el esquema de la ruta…"), AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Font = Theme.Font(9f), ForeColor = Theme.Subtle, BackColor = top.BackColor, Dock = DockStyle.Right, Width = Theme.Px(420), Padding = new Padding(0, 0, Theme.Px(14), 0) };
            top.Controls.Add(_title); top.Controls.Add(_status);

            var side = new Panel { Dock = DockStyle.Left, Width = Theme.Px(240), BackColor = Theme.BgSidebar, Padding = new Padding(Theme.Px(8)) };
            var cap = new Label { Text = Tr("DEPENDENCIAS"), Dock = DockStyle.Top, Height = Theme.Px(24), Font = Theme.Font(8.5f, FontStyle.Bold), ForeColor = Theme.Subtle, BackColor = side.BackColor };
            _list = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.BgSidebar, ForeColor = Theme.Text, Font = Theme.Font(9.5f),
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.Px(26), IntegralHeight = false
            };
            _list.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                string nm = _list.Items[e.Index].ToString();
                using (var b = new SolidBrush(sel ? Theme.Surface2 : Theme.BgSidebar)) e.Graphics.FillRectangle(b, e.Bounds);
                if (sel) using (var a = new SolidBrush(SchematicDraw.StationCol)) e.Graphics.FillRectangle(a, e.Bounds.X, e.Bounds.Y + 4, 3, e.Bounds.Height - 8);
                bool line = nm != General && IsLineZone(nm);
                if (nm == General) using (var p = new Pen(Color.FromArgb(60, 70, 82))) e.Graphics.DrawLine(p, e.Bounds.X + 6, e.Bounds.Bottom - 1, e.Bounds.Right - 6, e.Bounds.Bottom - 1);
                bool bell = _unread.Contains(nm);
                string own = nm == General ? null : OwnerOf(nm);
                int ownW = own != null ? Theme.Px(70) : 0;
                using var f = line ? Theme.Font(8.6f) : nm == General ? Theme.Font(9.5f, FontStyle.Bold) : null;
                TextRenderer.DrawText(e.Graphics, nm, f ?? _list.Font, new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - (bell ? 34 : 12) - ownW, e.Bounds.Height),
                                      sel || nm == General ? Theme.Text : line ? Color.FromArgb(130, 140, 150) : Theme.Subtle, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (own != null) DrawOwnerTag(e.Graphics, e.Bounds, own, Mine(nm), bell ? 26 : 4);
                if (bell) TextRenderer.DrawText(e.Graphics, "🔔", _list.Font, new Rectangle(e.Bounds.Right - 26, e.Bounds.Y, 24, e.Bounds.Height), Color.FromArgb(255, 213, 79), TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
            _list.SelectedIndexChanged += (s, e) => { if (_list.SelectedItem is string st) { if (st == General) ShowOverview(); else ShowZone(st); } };
            try { Native.UseDarkScrollBars(_list); } catch { }
            side.Controls.Add(_list); side.Controls.Add(cap);
            BuildJefeBar(top);   // jefes de circulación (DispatchForm.Jefe.cs; necesita la lista)
            BuildItinBar(top);   // mando manual / automático (DispatchForm.Itinerarios.cs)

            _canvas = new Canvas { Dock = DockStyle.Fill, BackColor = SchematicDraw.Bg, Cursor = Cursors.Hand };
            _canvas.Paint += (s, e) => Paint_(e.Graphics);
            _canvas.MouseWheel += (s, e) =>
            {
                if (Board)
                {
                    float k = e.Delta > 0 ? 1.18f : 1 / 1.18f, nz = Math.Max(0.004f, Math.Min(9f, _bZoom * k)); k = nz / _bZoom;
                    _bOrigin = new PointF(e.X - (e.X - _bOrigin.X) * k, e.Y - (e.Y - _bOrigin.Y) * k); _bZoom = nz;
                }
                else if (Overview)
                {
                    float k = e.Delta > 0 ? 1.15f : 1 / 1.15f, nz = Math.Max(0.004f, Math.Min(3f, _ovZoom * k)); k = nz / _ovZoom;
                    _ovOrigin = new PointF(e.X - (e.X - _ovOrigin.X) * k, e.Y - (e.Y - _ovOrigin.Y) * k); _ovZoom = nz;
                }
                else
                {
                    float k = e.Delta > 0 ? 1.15f : 1 / 1.15f, nz = Math.Max(0.3f, Math.Min(6f, _zoom * k)); k = nz / _zoom;
                    _origin = new PointF(e.X - (e.X - _origin.X) * k, e.Y - (e.Y - _origin.Y) * k); _zoom = nz;
                }
                _canvas.Invalidate();
            };
            _canvas.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { _dragFrom = e.Location; _dragOrigin = Board ? _bOrigin : Overview ? _ovOrigin : _origin; _dragging = false; _canvas.Focus(); } };
            _canvas.MouseMove += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    if (!_dragging && (Math.Abs(e.X - _dragFrom.X) > Theme.Px(6) || Math.Abs(e.Y - _dragFrom.Y) > Theme.Px(6))) _dragging = true;   // (1.2.75: con 3 px un clic con algo de temblor era un arrastre)
                    if (_dragging)
                    {
                        var no = new PointF(_dragOrigin.X + e.X - _dragFrom.X, _dragOrigin.Y + e.Y - _dragFrom.Y);
                        if (Board) _bOrigin = no; else if (Overview) _ovOrigin = no; else _origin = no;
                        _canvas.Invalidate();
                    }
                    return;
                }
                if (Board)
                {
                    bool own = Me != null && (RouteBoardDraw.SignalAt(_board, e.Location, Theme.Px(4)) is RouteBoard.Sg bs && MySig(bs.S.Obj)
                                              || RouteBoardDraw.SwitchAt(_board, e.Location) is RouteBoard.Sw bw && MyNode(bw.Node));
                    _canvas.Cursor = own || _pick != null || StationAt(e.Location) != null ? Cursors.Hand : Cursors.SizeAll;
                    if (_pick != null) _canvas.Invalidate();
                }
                else if (Overview)
                {
                    string h = ZoneAt(e.Location);
                    if (h != _hover) { _hover = h; _canvas.Invalidate(); }
                    _canvas.Cursor = h != null ? Cursors.Hand : Cursors.SizeAll;
                }
                else
                {
                    using var g = _canvas.CreateGraphics();
                    _canvas.Cursor = SchematicDraw.ExitAt(_cur, _origin, _zoom, e.Location, g) != null || _backBox.Contains(e.Location) || Me != null && SchematicDraw.SignalAt(_cur, e.Location, Theme.Px(4)) is Schematic.Signal hs && hs.Sig != null && MySig(hs.Sig.Obj) ? Cursors.Hand : Cursors.SizeAll;
                }
            };
            _canvas.MouseLeave += (s, e) => { if (_hover != null) { _hover = null; _canvas.Invalidate(); } };
            _canvas.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left || _dragging) { _dragging = false; return; }
                if (NoticeClick(e.Location)) return;   // una notificación de Tab: a esa señal (DispatchForm.Avisos.cs)
                if (Board)
                {
                    if (PickClick(e.Location)) return;     // eligiendo el destino de un itinerario
                    if (JefeClick(e.Location)) return;     // con el mando: una aguja o una señal
                    var st = StationAt(e.Location); if (st != null && _list.Items.Contains(st)) Select(st);
                    return;
                }
                if (Overview) { var z = ZoneAt(e.Location); if (z != null) Select(z); return; }
                if (_backBox.Contains(e.Location)) { PickCancel(); Select(General); return; }
                if (PickClick(e.Location)) return;     // eligiendo el destino de un itinerario (DispatchForm.Itinerarios.cs)
                if (JefeClick(e.Location)) return;     // con el mando: una aguja o una señal (DispatchForm.Jefe.cs)
                using var g2 = _canvas.CreateGraphics();
                var x = SchematicDraw.ExitAt(_cur, _origin, _zoom, e.Location, g2);
                if (x != null && _list.Items.Contains(x.Station)) Select(x.Station);
            };
            _canvas.DoubleClick += (s, e) => { if (Board) FitBoard(); else if (Overview) FitOverview(); else Fit(); };
            new ToolTip().SetToolTip(_canvas, Tr("rueda: zoom · arrastrar: mover · doble clic: ajustar"));
            _canvas.Resize += (s, e) => { if (Board) { if (!_bFitted) FitBoard(); } else if (Overview ? !_ovFitted : !_fitted) { if (Overview) FitOverview(); else Fit(); } };

            Controls.Add(_canvas); Controls.Add(side); Controls.Add(top);

            // (1.2.75) luces a destellos (amarillo a destellos, rebase autorizado…): repintar cada cuarto de segundo mientras haya
            _blink = new Timer { Interval = 250 };
            _blink.Tick += (s, e) => { if (_blinking && (Board || !Overview)) _canvas.Invalidate(); };
            _blink.Start();
            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => { UpdateStatus(); if (JefeBarStale()) UpdateJefe(); JefeKeepAlive(); RefreshHeaders(); _canvas.Invalidate(); };
            Shown += async (s, e) => await Prepare();
            FormClosing += (s, e) => LeaveOnClose();   // con el mando: se suelta
            FormClosed += (s, e) => { _timer.Stop(); _blink.Stop(); try { _pilot?.Dispose(); } catch { } };
        }

        bool IsLineZone(string z) => _zones?.FirstOrDefault(q => q.Name == z) is { Line: true };

        void Select(string item) { int i = _list.Items.IndexOf(item); if (i >= 0) { if (_list.SelectedIndex == i) { if (item == General) ShowOverview(); else ShowZone(item); } else _list.SelectedIndex = i; } }

        async Task Prepare()
        {
            try
            {
                (_g, _geo, _zones) = await Task.Run(() =>
                {
                    var g = _buildGraph(_routeDir); if (g == null) return (null, null, null);
                    return (g, RoomMapGeo.Build(g, _routeDir), Schematic.Zones(g));
                });
            }
            catch (Exception ex) { _status.Text = Tr("No se ha podido leer la ruta: ") + ex.Message; return; }
            if (_g == null) { _status.Text = Tr("No se ha podido leer la ruta."); return; }
            _sbe = SchematicCircuits.SignalsByEdge(_g, _geo);
            _stations = Schematic.Stations(_g);
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var E in _g.Edges) for (int i = 0; i < E.X.Length; i++) { x0 = Math.Min(x0, E.X[i]); x1 = Math.Max(x1, E.X[i]); y0 = Math.Min(y0, E.Y[i]); y1 = Math.Max(y1, E.Y[i]); }
            _routeBox = RectangleF.FromLTRB(x0, y0, x1, y1);
            _list.Items.Clear(); _list.Items.Add(General);
            foreach (var z in _zones.Where(z => !z.Line).OrderBy(z => z.Name, StringComparer.CurrentCulture)) _list.Items.Add(z.Name);
            foreach (var z in _zones.Where(z => z.Line).OrderBy(z => z.Name, StringComparer.CurrentCulture)) _list.Items.Add(z.Name);
            // conexión con la sala, solo para mirar
            try
            {
                _pilot = new SwitchPilot(_host, _port, _me, 1);   // (prioridad 0 = conexión corta de la contraseña: no recibe el mapa)
                _pilot.SwitchSet += (tx, tz, id, pos) =>
                {
                    if (_geo != null && _geo.ById.TryGetValue((tx, tz, id), out var sw)) lock (_swSet) _swSet[sw.Order] = (pos, DateTime.UtcNow);
                };
                _pilot.TabAsked += OnTab;   // avisos de Tab (DispatchForm.Avisos.cs)
                _pilot.EnsureStarted();
                _ = PrepareJefe();
                PrepareItins();
            }
            catch { }
            _timer.Start();
            IndexSignals();   // qué dependencia tiene cada señal, aguja y tramo (avisos de Tab, jefes, plano)
            _list.SelectedIndex = 0;
            UpdateStatus();
        }

        void ShowOverview()
        {
            _curZone = null; _cur = null; _sel = null; _heads = new();
            _title.Text = Tr("PUESTO DE MANDO") + "  ·  " + Tr("Plano general");
            UpdateJefe();
            if (Board) { FitBoard(); return; }
            if (!_ovFitted) FitOverview(); else _canvas.Invalidate();
        }

        void ShowZone(string zone)
        {
            if (_g == null) return;
            if (Board)
            {
                // (1.2.76) en el tablero: a esa estación o trayecto, sin cambiar de pantalla; la dependencia queda elegida para el jefe
                _cur = null; _curZone = zone;
                _title.Text = Tr("PUESTO DE MANDO") + "  ·  " + zone;
                if (_unread.Remove(zone)) _list.Invalidate();
                _sel = new SchematicHeader { Name = zone, Zones = new List<string> { zone } };
                _heads = new List<SchematicHeader> { _sel };
                RefreshHeaders(); UpdateJefe();
                FocusZone(zone);
                return;
            }
            if (!_cache.TryGetValue(zone, out var s))
            {
                var z = _zones?.FirstOrDefault(q => q.Name == zone) ?? new Schematic.Zone { Name = zone };
                try { s = Schematic.Build(_g, _geo, z); } catch { s = null; }
                _cache[zone] = s;
            }
            _cur = s; _curZone = zone;
            _title.Text = Tr("PUESTO DE MANDO") + "  ·  " + zone;
            if (_unread.Remove(zone)) _list.Invalidate();
            // la dependencia entera, para el jefe de circulación
            _sel = new SchematicHeader { Name = zone, Zones = new List<string> { zone } };
            _heads = new List<SchematicHeader> { _sel };
            RefreshHeaders();
            UpdateJefe();
            Fit();
        }

        // (el jefe de circulación) los recuadros que hay: en una dependencia, ella misma
        void RefreshHeaders()
        {
            foreach (var h in _heads)
            {
                var owners = h.Zones.Select(OwnerOf).Where(o => o != null).Distinct().ToList();
                h.Jefe = owners.Count == 0 ? null : string.Join(", ", owners);
                h.Mine = h.Zones.All(Mine);
                h.Selected = h == _sel;
            }
        }

        void Fit()
        {
            if (_cur == null || _canvas.Width < 50) { _canvas.Invalidate(); return; }
            // la estación: sus andenes y lo de alrededor (sus agujas cercanas); los ramales largos, arrastrando
            var b = _cur.Bounds;
            if (_cur.Platforms.Count > 0)
            {
                float px0 = _cur.Platforms.Min(q => q.Bar.Left), px1 = _cur.Platforms.Max(q => q.Bar.Right);
                var near = _cur.Switches.Where(w => w.At.X > px0 - 420 && w.At.X < px1 + 420).Select(w => w.At.X).ToList();
                float x0 = Math.Min(px0, near.Count > 0 ? near.Min() : px0) - 120, x1 = Math.Max(px1, near.Count > 0 ? near.Max() : px1) + 120;
                if (x1 - x0 < b.Width) b = RectangleF.FromLTRB(Math.Max(b.Left, x0), b.Top, Math.Min(b.Right, x1), b.Bottom);
            }
            float zx = (_canvas.Width - Theme.Px(300)) / Math.Max(1f, b.Width), zy = (_canvas.Height - Theme.Px(130)) / Math.Max(1f, b.Height);
            _zoom = Math.Max(0.35f, Math.Min(2.2f, Math.Min(zx, zy)));
            _origin = new PointF((_canvas.Width - b.Width * _zoom) / 2 - b.Left * _zoom, (_canvas.Height - b.Height * _zoom) / 2 - b.Top * _zoom + Theme.Px(18));
            _fitted = true; _canvas.Invalidate();
        }

        void FitOverview()
        {
            if (_g == null || _canvas.Width < 50 || _routeBox.Width <= 0) { _canvas.Invalidate(); return; }
            float pad = Theme.Px(60);
            _ovZoom = Math.Min((_canvas.Width - 2 * pad) / _routeBox.Width, (_canvas.Height - 2 * pad) / Math.Max(1f, _routeBox.Height));
            _ovOrigin = new PointF((_canvas.Width - _routeBox.Width * _ovZoom) / 2 - _routeBox.Left * _ovZoom, (_canvas.Height - _routeBox.Height * _ovZoom) / 2 - _routeBox.Top * _ovZoom);
            _ovFitted = true; _canvas.Invalidate();
        }

        PointF Ov(double x, double y) => new PointF((float)(_ovOrigin.X + x * _ovZoom), (float)(_ovOrigin.Y + y * _ovZoom));

        // ── (1.2.76) el tablero
        PointF Bp(PointF q) => new PointF(_bOrigin.X + q.X * _bZoom, _bOrigin.Y + q.Y * _bZoom);

        void FitBoard()
        {
            if (_board == null || _canvas.Width < 50) { _canvas.Invalidate(); return; }
            var b = _board.Bounds; float pad = Theme.Px(50);
            _bZoom = Math.Min((_canvas.Width - 2 * pad) / Math.Max(1f, b.Width), (_canvas.Height - 2 * pad) / Math.Max(1f, b.Height));
            _bOrigin = new PointF((_canvas.Width - b.Width * _bZoom) / 2 - b.Left * _bZoom, (_canvas.Height - b.Height * _bZoom) / 2 - b.Top * _bZoom);
            _bFitted = true; _canvas.Invalidate();
        }

        // centrar en una zona: una estación (sus andenes y alrededor) o un trayecto (sus puntos)
        void FocusZone(string zone)
        {
            if (_board == null) return;
            RectangleF r = RectangleF.Empty;
            var st = _board.Stations.FirstOrDefault(s => s.name == zone);
            if (st.name != null) r = RectangleF.Inflate(st.box, 220, 160);
            else if (_zones?.FirstOrDefault(z => z.Name == zone) is { Anchors.Count: > 0 } z)
            {
                var pts = z.Anchors.Select(a => _board.W(a.x, a.y)).ToList();
                r = RectangleF.FromLTRB(pts.Min(q => q.X), pts.Min(q => q.Y), pts.Max(q => q.X), pts.Max(q => q.Y)); r.Inflate(150, 150);
            }
            if (r.IsEmpty) { FitBoard(); return; }
            CenterOn(r);
        }

        void CenterOn(RectangleF r, float minZoom = 0f)
        {
            float pad = Theme.Px(40);
            _bZoom = Math.Max(minZoom, Math.Min(4f, Math.Min((_canvas.Width - 2 * pad) / Math.Max(1f, r.Width), (_canvas.Height - 2 * pad) / Math.Max(1f, r.Height))));
            _bOrigin = new PointF(_canvas.Width / 2f - (r.X + r.Width / 2) * _bZoom, _canvas.Height / 2f - (r.Y + r.Height / 2) * _bZoom);
            _bFitted = true; _canvas.Invalidate();
        }

        // la estación bajo el ratón (su nombre en el tablero)
        string StationAt(Point p)
        {
            if (_board == null) return null;
            foreach (var (name, r) in RouteBoardDraw.StationLabels(_board)) if (r.Contains(p)) return name;
            return null;
        }

        // (IndexSignals) el tablero, con el número de cada aguja en su estación (el de su esquema de siempre)
        static RouteBoard MakeBoard(RouteGraph g, RoomMapGeo geo, List<Schematic.Zone> zones, Dictionary<string, Schematic> built)
        {
            var num = new Dictionary<int, (string, int)>();
            foreach (var z in zones.Where(z => !z.Line))
                if (built.TryGetValue(z.Name, out var s) && s != null)
                    foreach (var w in s.Switches) { int n = SchematicDraw.SwitchNumber(s, w.Node); if (n > 0 && !num.ContainsKey(w.Node)) num[w.Node] = (z.Name, n); }
            return RouteBoard.Build(g, geo, n => num.TryGetValue(n, out var v) ? v : null);
        }

        // lo que pasa en la sala ahora, en el tablero (los mismos datos que en un esquema, en metros de cada tramo)
        SchematicLive LiveBoard()
        {
            var p = _pilot; var geo = _geo; var b = _board;
            if (p == null || geo == null || b == null || !p.Ready) return null;
            var st = p.States; var stAt = p.StatesAt; var sig = p.SigStates; var blocked = p.Blocked;
            bool sigOk = sig != null && sig.Length == 2 * geo.Heads.Count && geo.Heads.Count > 0;
            int Pos(int order)
            {
                lock (_swSet) if (_swSet.TryGetValue(order, out var o) && o.at > stAt) return o.pos;
                return st != null && order >= 0 && order < st.Length ? st[order] : -1;
            }
            int Asp(RoomMapGeo.Sig sg)
            {
                if (!sigOk) return -1;
                int best = -1;
                foreach (int k in sg.Heads) { if (!geo.Heads[k].Normal) continue; int v = 2 * k < sig.Length ? sig[2 * k] - 1 : -1; if (v >= 0 && v < 8 && v > best) best = v; }
                return best;
            }
            var itinLocked = new HashSet<int>();
            var live = new SchematicLive
            {
                SwitchPos = Pos, Aspect = Asp,
                Figure = sg => !sigOk ? null : RoomMapDraw.FigureOf(geo, sg, k => 2 * k < sig.Length ? sig[2 * k] - 1 : -1, k => 2 * k + 1 < sig.Length ? sig[2 * k + 1] - 1 : -1),
                Locked = o => itinLocked.Contains(o) || blocked != null && o >= 0 && o < blocked.Length && blocked[o] is 'S' or 'T' or 'O'
            };
            int PosNode(int n) => geo.OrderOf.TryGetValue(n, out int o) ? Pos(o) : -1;
            void Occ(int e, double off)
            {
                if (!live.Occupied.TryGetValue(e, out var l)) live.Occupied[e] = l = new();
                if (l.Count > 0 && off >= l[^1].a - 12 && off <= l[^1].b + 12) l[^1] = (Math.Min(l[^1].a, off - 2), Math.Max(l[^1].b, off + 2));
                else l.Add((off - 2, off + 2));
            }
            foreach (var t in p.Trains)
            {
                if (!OrGeo.TryLatLon(t.tx * 2048.0 + t.x, t.tz * 2048.0 + t.z, out double la, out double lo)) continue;
                var body = new List<PointF>();
                void At(double a, double o)
                {
                    if (b.Locate(a, o, out int e, out double off)) { Occ(e, off); body.Add(b.At(e, off)); }
                    else { var (x, y) = _g.Proj(a, o); body.Add(b.W(x, y)); }
                }
                try
                {
                    if (p.TrainTrack.TryGetValue(t.name, out var k))
                    {
                        var pts = geo.Graph.PathAhead(k.node, la, lo, k.dir, k.mu, 0, Math.Max(k.len, 5), PosNode, out _);
                        if (pts != null)
                            for (int i = 0; i < pts.Count; i++)
                            {
                                if (i > 0)
                                {
                                    var (a1, o1) = pts[i - 1]; var (a2, o2) = pts[i];
                                    double dm = LiveMates.Haversine(a1, o1, a2, o2); int n = (int)Math.Min(40, dm / 8);
                                    for (int j = 1; j < n; j++) { double f = (double)j / n; At(a1 + (a2 - a1) * f, o1 + (o2 - o1) * f); }
                                }
                                At(pts[i].lat, pts[i].lon);
                            }
                    }
                }
                catch { }
                if (body.Count == 0) At(la, lo);
                if (body.Count == 0) continue;
                string sub = t.kmh >= 1 ? $"{t.kmh:0} km/h" : Tr("parado");
                live.Trains.Add((t.player ? t.name : RoomLoose.Is(t.name) ? RoomLoose.Label(t.name) : Tr("IA"), RoomLoose.Is(t.name) ? Tr("suelto") : sub, body.ToArray(), t.player));
            }
            // itinerarios de las señales abiertas (de estación, con alguna aguja, y los rebases), solo las que se ven
            if (sigOk && _sbe != null && _bZoom >= 0.08f)
            {
                var view = new RectangleF((-_bOrigin.X) / _bZoom, (-_bOrigin.Y) / _bZoom, _canvas.Width / _bZoom, _canvas.Height / _bZoom); view.Inflate(400, 400);
                foreach (var sg in b.Signals)
                {
                    if (!view.Contains(sg.At)) continue;
                    int a = Asp(sg.S); if (a < 1) continue;
                    if (!SchematicCircuits.SignalPlace(_sbe, sg.S, out int e0, out double o0, out int d0)) continue;
                    bool callOn = RoomMapDraw.IsCallOn(a);
                    var got = new List<(int e, double x0, double x1)>();
                    if (!SchematicCircuits.Walk(_g, _sbe, e0, o0, d0, PosNode, (e, x0, x1) => got.Add((e, x0, x1))) && !callOn) continue;
                    foreach (var (e, x0, x1) in got) { if (!live.Route.TryGetValue(e, out var rl)) live.Route[e] = rl = new(); rl.Add((x0, x1, callOn)); }
                }
            }
            AddItins(live, PosNode, itinLocked);
            AddFlashes(live);
            return live;
        }

        // (plano) la dependencia bajo el ratón: una estación (su nombre o su punto) o, si no, el tramo de vía más cercano
        string ZoneAt(Point p)
        {
            if (_g == null) return null;
            foreach (var (name, r) in _stLabels) if (r.Contains(p) && _list.Items.Contains(name)) return name;
            string best = null; double bd = Theme.Px(14);
            if (_stations != null)
                foreach (var st in _stations)
                {
                    var q = Ov(st.x, st.y); double d = Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y));
                    if (d < bd && _list.Items.Contains(st.name)) { bd = d; best = st.name; }
                }
            if (best != null || _edgeZone == null) return best;
            double be = Theme.Px(9); int bestE = -1;
            for (int e = 0; e < _g.Edges.Length; e++)
            {
                var E = _g.Edges[e];
                for (int i = 1; i < E.X.Length; i++)
                {
                    var a = Ov(E.X[i - 1], E.Y[i - 1]); var b = Ov(E.X[i], E.Y[i]);
                    double d = SegDist(p, a, b); if (d < be) { be = d; bestE = e; }
                }
            }
            return bestE >= 0 && _edgeZone.TryGetValue(bestE, out var z) ? z : null;
        }

        static double SegDist(Point p, PointF a, PointF b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, L2 = dx * dx + dy * dy;
            double t = L2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / L2));
            double qx = a.X + dx * t - p.X, qy = a.Y + dy * t - p.Y; return Math.Sqrt(qx * qx + qy * qy);
        }

        // ── el plano general
        readonly List<(string name, RectangleF r)> _stLabels = new();
        void DrawOverview(Graphics g, Rectangle area)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var bg = new SolidBrush(SchematicDraw.Bg)) g.FillRectangle(bg, area);
            if (_g == null) return;
            // vías: de su dependencia (con jefe: su color; la de debajo del ratón, más clara)
            var plain = Color.FromArgb(128, 138, 150);
            Color Col(string z) => z == null ? plain
                                  : z == _hover ? Color.FromArgb(225, 230, 236)
                                  : Mine(z) ? Color.FromArgb(102, 187, 106) : OwnerOf(z) != null ? Color.FromArgb(255, 213, 79) : plain;
            float lw = Math.Max(1.7f, Math.Min(3.4f, 44f * _ovZoom));
            var pens = new Dictionary<Color, Pen>();
            Pen PenOf(Color c) { if (!pens.TryGetValue(c, out var p)) pens[c] = p = new Pen(c, c == plain ? lw : lw + 1.2f) { LineJoin = LineJoin.Round }; return p; }
            for (int e = 0; e < _g.Edges.Length; e++)
            {
                var E = _g.Edges[e]; if (E.X.Length < 2) continue;
                var pts = new PointF[E.X.Length];
                for (int i = 0; i < pts.Length; i++) pts[i] = Ov(E.X[i], E.Y[i]);
                if (!area.IntersectsWith(Rectangle.Round(Bounds(pts)))) continue;
                string z = _edgeZone != null && _edgeZone.TryGetValue(e, out var zz) ? zz : null;
                g.DrawLines(PenOf(Col(z)), pts);
            }
            foreach (var p in pens.Values) p.Dispose();
            // trenes: un punto con el nombre de su maquinista (los de la IA, en gris)
            var pl = _pilot;
            if (pl != null && pl.Ready)
                using (var tf = Theme.Font(7.6f, FontStyle.Bold))
                    foreach (var t in pl.Trains)
                    {
                        if (!OrGeo.TryLatLon(t.tx * 2048.0 + t.x, t.tz * 2048.0 + t.z, out double la, out double lo)) continue;
                        var (x, y) = _g.Proj(la, lo); var q = Ov(x, y);
                        var col = t.player ? SchematicDraw.Occ : SchematicDraw.AiCol;
                        using (var b = new SolidBrush(col)) g.FillEllipse(b, q.X - 4.5f, q.Y - 4.5f, 9, 9);
                        using (var op = new Pen(Color.FromArgb(200, 10, 12, 14), 1.2f)) g.DrawEllipse(op, q.X - 4.5f, q.Y - 4.5f, 9, 9);
                        if (t.player || RoomLoose.Is(t.name)) using (var tb = new SolidBrush(col)) g.DrawString(t.player ? t.name : RoomLoose.Label(t.name), tf, tb, q.X + 6, q.Y - 14);
                    }
            // estaciones: su punto y su nombre (sin montarse unos nombres con otros); la 🔔 si alguien pide paso allí
            _stLabels.Clear();
            if (_stations != null)
                using (var sf = Theme.Font(8.4f, FontStyle.Bold)) using (var bf = new Font("Segoe UI Symbol", 10f))
                {
                    var taken = new List<RectangleF>();
                    foreach (var st in _stations.OrderByDescending(s => s.name == _hover).ThenByDescending(s => s.n))
                    {
                        var q = Ov(st.x, st.y);
                        string own = OwnerOf(st.name);
                        var c = st.name == _hover ? Color.White : Mine(st.name) ? Color.FromArgb(102, 187, 106) : own != null ? Color.FromArgb(255, 213, 79) : SchematicDraw.StationCol;
                        using (var b = new SolidBrush(c)) g.FillRectangle(b, q.X - 4, q.Y - 4, 8, 8);
                        using (var op = new Pen(Color.FromArgb(220, 10, 12, 14), 1.2f)) g.DrawRectangle(op, q.X - 4, q.Y - 4, 8, 8);
                        string label = st.name + (own != null ? "  (" + own + ")" : "");
                        var sz = g.MeasureString(label, sf);
                        RectangleF r = default; bool ok = false;
                        foreach (var (dx, dy) in new[] { (7f, -sz.Height / 2), (-7f - sz.Width, -sz.Height / 2), (-sz.Width / 2, 6f), (-sz.Width / 2, -6f - sz.Height) })
                        {
                            r = new RectangleF(q.X + dx, q.Y + dy, sz.Width, sz.Height);
                            if (!taken.Any(t => t.IntersectsWith(r))) { ok = true; break; }
                        }
                        if (ok)
                        {
                            taken.Add(r);
                            using (var hb = new SolidBrush(Color.FromArgb(170, 11, 13, 16))) g.FillRectangle(hb, r);
                            using var tb = new SolidBrush(c); g.DrawString(label, sf, tb, r.X, r.Y);
                            _stLabels.Add((st.name, r));
                        }
                        if (_unread.Contains(st.name))
                            using (var bb = new SolidBrush(Color.FromArgb(255, 213, 79))) g.DrawString("\U0001F514", bf, bb, q.X - 8, q.Y - 26);
                    }
                }
            // trayectos con aviso: la 🔔 en su centro
            if (_zones != null)
                using (var bf = new Font("Segoe UI Symbol", 10f)) using (var bb = new SolidBrush(Color.FromArgb(255, 213, 79)))
                    foreach (var z in _zones.Where(z => z.Line && _unread.Contains(z.Name) && z.Anchors?.Count > 0))
                    { var q = Ov(z.Anchors.Average(a => a.x), z.Anchors.Average(a => a.y)); g.DrawString("\U0001F514", bf, bb, q.X - 8, q.Y - 10); }
            // la dependencia bajo el ratón (o la ayuda)
            if (_hover != null)
                using (var hf = Theme.Font(10f, FontStyle.Bold))
                {
                    string t = _hover + (OwnerOf(_hover) is string o ? "  ·  " + string.Format(Tr("Jefe de circulación: {0}"), o) : "") + "  ·  " + Tr("clic para abrirla");
                    var sz = g.MeasureString(t, hf);
                    var r = new RectangleF(area.Left + Theme.Px(14), area.Bottom - sz.Height - Theme.Px(18), sz.Width + 16, sz.Height + 8);
                    using (var b = new SolidBrush(Color.FromArgb(235, 26, 31, 38))) g.FillRectangle(b, r);
                    using (var tb = new SolidBrush(Theme.Text)) g.DrawString(t, hf, tb, r.X + 8, r.Y + 4);
                }
            else
                using (var hf = Theme.Font(9f))
                using (var tb = new SolidBrush(Theme.Subtle))
                    g.DrawString(Tr("Plano general · clic en una estación o en un tramo para abrir esa dependencia"), hf, tb, area.Left + Theme.Px(14), area.Bottom - Theme.Px(30));
        }

        static RectangleF Bounds(PointF[] p)
        {
            float x0 = p.Min(q => q.X), y0 = p.Min(q => q.Y), x1 = p.Max(q => q.X), y1 = p.Max(q => q.Y);
            return RectangleF.FromLTRB(x0, y0, x1 + 1, y1 + 1);
        }

        // lo que pasa en la sala ahora, en el esquema
        SchematicLive Live()
        {
            var p = _pilot; var geo = _geo; var s = _cur;
            if (p == null || geo == null || s == null || !p.Ready) return null;
            var st = p.States; var stAt = p.StatesAt; var sig = p.SigStates; var blocked = p.Blocked;
            bool sigOk = sig != null && sig.Length == 2 * geo.Heads.Count && geo.Heads.Count > 0;
            int Pos(int order)
            {
                lock (_swSet) if (_swSet.TryGetValue(order, out var o) && o.at > stAt) return o.pos;
                return st != null && order >= 0 && order < st.Length ? st[order] : -1;
            }
            int Asp(RoomMapGeo.Sig sg)
            {
                if (!sigOk) return -1;
                int best = -1;
                foreach (int k in sg.Heads)
                {
                    if (!geo.Heads[k].Normal) continue;
                    int v = 2 * k < sig.Length ? sig[2 * k] - 1 : -1;
                    if (v >= 0 && v < 8 && v > best) best = v;
                }
                return best;
            }
            var itinLocked = new HashSet<int>();   // (órdenes de agujas enclavadas en un itinerario: AddItins)
            var live = new SchematicLive
            {
                SwitchPos = Pos, Aspect = Asp,
                Figure = sg => !sigOk ? null : RoomMapDraw.FigureOf(geo, sg, k => 2 * k < sig.Length ? sig[2 * k] - 1 : -1, k => 2 * k + 1 < sig.Length ? sig[2 * k + 1] - 1 : -1),
                Locked = o => itinLocked.Contains(o) || blocked != null && o >= 0 && o < blocked.Length && blocked[o] is 'S' or 'T' or 'O'
            };
            int PosNode(int n) => geo.OrderOf.TryGetValue(n, out int o) ? Pos(o) : -1;
            // trenes: su cuerpo en el esquema y lo que ocupan (metros de cada tramo)
            void Occ(int e, double off)
            {
                if (!live.Occupied.TryGetValue(e, out var l)) live.Occupied[e] = l = new();
                if (l.Count > 0 && off >= l[^1].a - 12 && off <= l[^1].b + 12) l[^1] = (Math.Min(l[^1].a, off - 2), Math.Max(l[^1].b, off + 2));
                else l.Add((off - 2, off + 2));
            }
            foreach (var t in p.Trains)
            {
                if (!OrGeo.TryLatLon(t.tx * 2048.0 + t.x, t.tz * 2048.0 + t.z, out double la, out double lo)) continue;
                var body = new List<PointF>();
                void At(double a, double o)
                {
                    if (s.Map(a, o, out var q)) body.Add(q);
                    if (s.Locate(a, o, out int e, out double off)) Occ(e, off);
                }
                try
                {
                    if (p.TrainTrack.TryGetValue(t.name, out var k))
                    {
                        var pts = geo.Graph.PathAhead(k.node, la, lo, k.dir, k.mu, 0, Math.Max(k.len, 5), PosNode, out _);
                        if (pts != null)
                            for (int i = 0; i < pts.Count; i++)
                            {
                                if (i > 0)
                                {
                                    var (a1, o1) = pts[i - 1]; var (a2, o2) = pts[i];
                                    double dm = LiveMates.Haversine(a1, o1, a2, o2); int n = (int)Math.Min(40, dm / 8);
                                    for (int j = 1; j < n; j++) { double f = (double)j / n; At(a1 + (a2 - a1) * f, o1 + (o2 - o1) * f); }
                                }
                                At(pts[i].lat, pts[i].lon);
                            }
                    }
                }
                catch { }
                if (body.Count == 0) At(la, lo);
                if (body.Count == 0) continue;
                string sub = t.kmh >= 1 ? $"{t.kmh:0} km/h" : Tr("parado");
                live.Trains.Add((t.player ? t.name : RoomLoose.Is(t.name) ? RoomLoose.Label(t.name) : Tr("IA"), RoomLoose.Is(t.name) ? Tr("suelto") : sub, body.ToArray(), t.player));
            }
            // itinerarios: de cada señal abierta a la siguiente, por las agujas como están (los de plena vía, no)
            if (sigOk && _sbe != null)
                foreach (var sg in s.Signals)
                {
                    if (sg.Sig == null || sg.Sig.Edge < 0) continue;
                    int a = Asp(sg.Sig); if (a < 1) continue;
                    if (!_sbe.TryGetValue(sg.Sig.Edge, out var l)) continue;
                    int dir = 0; foreach (var q in l) if (q.obj == sg.Sig.Obj) { dir = q.dir; break; }
                    if (dir == 0) continue;
                    bool callOn = RoomMapDraw.IsCallOn(a);
                    var got = new List<(int e, double x0, double x1)>();
                    // solo los itinerarios de estación (pasan por alguna aguja) y los rebases autorizados
                    if (!SchematicCircuits.Walk(_g, _sbe, sg.Sig.Edge, sg.Sig.Off, dir, PosNode, (e, x0, x1) => got.Add((e, x0, x1))) && !callOn) continue;
                    foreach (var (e, x0, x1) in got)
                    {
                        if (!live.Route.TryGetValue(e, out var rl)) live.Route[e] = rl = new();
                        rl.Add((x0, x1, callOn));
                    }
                }
            AddItins(live, PosNode, itinLocked);   // (1.2.75) los itinerarios de la sala (DispatchForm.Itinerarios.cs)
            AddFlashes(live);
            return live;
        }

        void Paint_(Graphics g)
        {
            var area = _canvas.ClientRectangle;
            if (Board)
            {
                var lv = LiveBoard();
                RouteBoardDraw.Draw(g, _board, lv, _bOrigin, _bZoom, area);
                _blinking = lv?.Blinking == true;
                if (_pick == null && !(_itinMsg != null && DateTime.UtcNow < _itinMsgUntil))
                    using (var hf = Theme.Font(8.6f)) using (var tb = new SolidBrush(Theme.Subtle))
                        g.DrawString(Tr("rueda: zoom · arrastrar: mover · doble clic: toda la ruta · clic en una estación: acercar"), hf, tb, area.Left + Theme.Px(12), area.Top + Theme.Px(10));
                DrawNotices(g, area);
                DrawJefeBand(g, area);
                DrawPick(g, area);
                return;
            }
            if (Overview) { DrawOverview(g, area); DrawNotices(g, area); return; }
            if (_cur == null)
            {
                using (var bg = new SolidBrush(SchematicDraw.Bg)) g.FillRectangle(bg, area);
                TextRenderer.DrawText(g, Tr("Esta dependencia no tiene esquema."), Theme.Font(10f), area, Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else { var lv = Live(); SchematicDraw.Draw(g, _cur, lv, _origin, _zoom, area); _blinking = lv?.Blinking == true; }
            // «← Plano general»
            using (var bf = Theme.Font(9f, FontStyle.Bold))
            {
                string t = "← " + Tr("Plano general");
                var sz = g.MeasureString(t, bf);
                _backBox = new RectangleF(area.Left + Theme.Px(10), area.Top + Theme.Px(10), sz.Width + 16, sz.Height + 8);
                using (var b = new SolidBrush(Color.FromArgb(235, 30, 36, 44))) g.FillRectangle(b, _backBox);
                using (var p = new Pen(Color.FromArgb(90, 100, 112))) g.DrawRectangle(p, _backBox.X, _backBox.Y, _backBox.Width, _backBox.Height);
                using (var tb = new SolidBrush(Theme.Text)) g.DrawString(t, bf, tb, _backBox.X + 8, _backBox.Y + 4);
            }
            DrawNotices(g, area);
            DrawJefeBand(g, area);
            DrawPick(g, area);
        }

        void UpdateStatus()
        {
            if (_g == null) return;
            var p = _pilot;
            string conn = p == null ? Tr("sin conexión con la sala")
                        : p.Ready ? string.Format(Tr("{0} trenes en la sala"), p.Trains.Count(t => t.player)) + (p.Trains.Any(t => !t.player) ? " · " + string.Format(Tr("{0} de la IA"), p.Trains.Count(t => !t.player)) : "")
                        : p.Unsupported ? Tr("la sala no contesta") : Tr("conectando con la sala…");
            string clock = OrClock();
            _status.Text = (clock != null ? clock + "  ·  " : "") + conn;
        }

        // la hora de la sala: la que trae la foto de la sala (si la hay) no se sabe aquí; la de España con su adelanto
        public Func<string> ClockText;
        string OrClock() { try { return ClockText?.Invoke(); } catch { return null; } }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { if (PickCancel()) return true; if (!Overview) { Select(General); return true; } Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
