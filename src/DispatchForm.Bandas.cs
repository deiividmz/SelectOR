// (2026-11-09) Bandas de regulación en el puesto (Bandas.cs): en la lista, las bandas («BR1 · Santa Ana – Aeropuerto»);
// al elegir una se despliegan debajo sus dependencias. La banda se ve en el trazado real, encuadrada y resaltada con su
// color; el jefe de circulación la toma entera («Tomar esta banda»: todas sus dependencias) y los trenes de la columna
// derecha son los de la banda. Las dependencias sueltas se siguen abriendo como antes. Solo en la ruta de la sala.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace SelectOR
{
    public sealed partial class DispatchForm
    {
        int _openBand, _curBand;   // la banda desplegada en la lista · la que se ve (0: ninguna)
        bool _filling;
        readonly HashSet<string> _listable = new(StringComparer.Ordinal);   // las dependencias que caben en la lista (aunque estén plegadas)

        bool BandsOn => _zones != null && string.Equals(Path.GetFileName((_routeDir ?? "").TrimEnd('\\', '/')), Bandas.Route, StringComparison.OrdinalIgnoreCase)
                        && _zones.Any(z => Bandas.Of.ContainsKey(z.Name));
        int BandOfZone(string z) => z != null && BandsOn && Bandas.Of.TryGetValue(z, out int b) ? b : 0;
        static bool IsBandItem(string s, out int b)
        {
            for (b = 1; b <= Bandas.Count; b++) if (s == Bandas.Label(b)) return true;
            b = 0; return false;
        }
        List<string> BandZones(int b) => _zones == null ? new List<string>() : _zones.Where(z => BandOfZone(z.Name) == b).Select(z => z.Name).ToList();
        bool Listed(string z) => z != null && (_list.Items.Contains(z) || _listable.Contains(z));

        // la lista: el plano general, las bandas (la desplegada, con sus dependencias) y lo que no tenga banda
        void FillList(bool mineOnly, List<string> mine)
        {
            _filling = true;
            try
            {
                _list.BeginUpdate();
                _list.Items.Clear(); _list.Items.Add(General); _listable.Clear();
                var zs = _zones.Where(z => !mineOnly || mine.Contains(z.Name) || DepZones(z.Name).Any(mine.Contains)).ToList();
                foreach (var z in zs) _listable.Add(z.Name);
                IEnumerable<string> Ordered(IEnumerable<Schematic.Zone> l)
                    => l.Where(z => !z.Line).OrderBy(z => z.Name, StringComparer.CurrentCulture).Concat(l.Where(z => z.Line && IsDepHead(z.Name)).OrderBy(z => z.Name, StringComparer.CurrentCulture)).Select(z => z.Name);   // (2026-11-10) los tramos, con su estación (DispatchForm.Dependencias.cs)
                if (BandsOn)
                {
                    for (int b = 1; b <= Bandas.Count; b++)
                    {
                        var inb = zs.Where(z => BandOfZone(z.Name) == b).ToList();
                        if (inb.Count == 0) continue;
                        _list.Items.Add(Bandas.Label(b));
                        if (b == _openBand) foreach (var n in Ordered(inb)) _list.Items.Add(n);
                    }
                    foreach (var n in Ordered(zs.Where(z => BandOfZone(z.Name) == 0))) _list.Items.Add(n);
                }
                else foreach (var n in Ordered(zs)) _list.Items.Add(n);
                _list.EndUpdate();
            }
            finally { _filling = false; }
        }

        // rehacer la lista (desplegada otra banda) sin perder lo elegido
        void Refill()
        {
            var mine = MyZones(); bool on = _mineOnly && mine.Count > 0;
            string sel = _list.SelectedItem as string;
            FillList(on, mine);
            if (sel != null) { int i = _list.Items.IndexOf(sel); if (i >= 0) { _filling = true; try { _list.SelectedIndex = i; } finally { _filling = false; } } }
            _list.Invalidate();
        }

        // (2026-11-09) un único esquema para toda la ruta (Schematic.Route): el de cada banda, unidas de principio a fin. Todas
        // las bandas lo comparten (_cache[su nombre]); elegir una encuadra su trozo. Se hace en segundo plano la primera vez.
        bool _routeBuilding;
        Schematic _route;

        // (2026-11-10) montado estación a estación (Schematic.RouteZones): el esquema auditado de cada estación y de cada tramo de
        // línea, en su sitio del mapa y unidos por sus vías; la banda solo dice qué piezas se encuadran y cuáles se apagan
        void BuildBandSchema(int b) => BuildRouteSchema();
        void BuildRouteSchema()
        {
            if (_route != null || _routeBuilding || _zones == null || _g == null) return;
            _routeBuilding = true;
            var labels = Enumerable.Range(1, Bandas.Count).Where(b => BandZones(b).Count > 0).ToDictionary(b => b, b => Bandas.Label(b));
            var g = _g; var geo = _geo; var zl = _zones.ToList();
            System.Threading.Tasks.Task.Run(() =>
            {
                try { return Schematic.RouteZones(g, geo, "CGL", zl, null, "Estación Sur"); } catch { return null; }   // (la línea, desde Estación Sur)
            }).ContinueWith(t =>
            {
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        _routeBuilding = false;
                        var s = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? t.Result : null;
                        foreach (var (b, label) in labels)
                        {
                            if (s?.Parts == null || !s.Parts.Any(p => BandOfZone(p.Station) == b)) _noSchema.Add(label);
                            else _cache[label] = s;
                        }
                        _route = s ?? Schematic.Placeholder();   // (no se vuelve a intentar)
                        if (_curBand > 0) ShowBand(_curBand);   // (si se sigue viendo una banda: ya con el esquema)
                        else if (_curZone != null && _depParts == null && _schema) { _depWaiting = false; ShowZone(_curZone); }   // (2026-11-10) o una dependencia: ya en el de la ruta
                    }));
                }
                catch { }
            });
        }

        // encuadrar la banda dentro del esquema de la ruta
        void FitPart(RectangleF b)
        {
            if (_canvas.Width < 50) { _canvas.Invalidate(); return; }
            b.Inflate(60, 40);
            float zx = (_canvas.Width - Theme.Px(120)) / Math.Max(1f, b.Width), zy = (_canvas.Height - Theme.Px(130)) / Math.Max(1f, b.Height);
            _zoom = Math.Max(0.08f, Math.Min(2.2f, Math.Min(zx, zy)));
            _origin = new PointF(_canvas.Width / 2f - (b.Left + b.Width / 2) * _zoom, _canvas.Height / 2f - (b.Top + b.Height / 2) * _zoom + Theme.Px(18));
            _fitted = true; _canvas.Invalidate();
        }

        // (al pintar) en el esquema de la ruta, las otras bandas un poco apagadas: se ve dónde empieza y acaba la elegida
        void DimOtherBands(System.Drawing.Graphics g, Rectangle area)
        {
            if (_cur?.Parts == null || _curBand <= 0) return;
            var me = BandParts(_curBand); if (me.Count == 0) return;
            using var dim = new SolidBrush(Color.FromArgb(150, SchematicDraw.Bg));
            using var fr = new Pen(Color.FromArgb(110, Bandas.Colors[_curBand]), 1.5f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            var st = g.Save(); g.SetClip(area);
            var mine = new HashSet<Schematic>(me);
            foreach (var p in _cur.Parts)
            {
                if (mine.Contains(p)) continue;
                var r = p.Bounds; r.Inflate(20, 30);
                g.FillRectangle(dim, _origin.X + r.Left * _zoom, _origin.Y + r.Top * _zoom, r.Width * _zoom, r.Height * _zoom);
            }
            // (2026-11-10, petición) las demás bandas, también en su recuadro (tenue) y con su nombre: se ve dónde acaba cada una
            var vis = new RectangleF((area.Left - _origin.X) / _zoom, (area.Top - _origin.Y) / _zoom, area.Width / _zoom, area.Height / _zoom);
            using (var tf = Theme.Font(8f, FontStyle.Bold))
                for (int b = 1; b <= Bandas.Count; b++)
                {
                    if (b == _curBand) continue;
                    using var fo = new Pen(Color.FromArgb(55, Bandas.Colors[b]), 1.2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                    using var tb = new SolidBrush(Color.FromArgb(120, Bandas.Colors[b]));
                    foreach (var m0 in BandBlocks(BandParts(b)))
                    {
                        var m = m0; m.Inflate(30, 40);
                        if (!m.IntersectsWith(vis)) continue;
                        g.DrawRectangle(fo, _origin.X + m.Left * _zoom, _origin.Y + m.Top * _zoom, m.Width * _zoom, m.Height * _zoom);
                        g.DrawString(Bandas.Label(b).Split(' ')[0], tf, tb, _origin.X + m.Left * _zoom + 3, _origin.Y + m.Top * _zoom + 2);
                    }
                }
            // (cada trozo seguido de la banda, en su recuadro: una banda puede ir por la línea y por un ramal)
            foreach (var m0 in BandBlocks(me))
            {
                var m = m0; m.Inflate(30, 40);
                g.DrawRectangle(fr, _origin.X + m.Left * _zoom, _origin.Y + m.Top * _zoom, m.Width * _zoom, m.Height * _zoom);
            }
            g.Restore(st);
        }

        // las piezas del esquema de la ruta que son de la banda b
        List<Schematic> BandParts(int b) => _cur?.Parts == null ? new List<Schematic>() : _cur.Parts.Where(p => BandOfZone(p.Station) == b).ToList();
        static RectangleF Union(IEnumerable<Schematic> ps) { RectangleF u = RectangleF.Empty; foreach (var p in ps) u = u.IsEmpty ? p.Bounds : RectangleF.Union(u, p.Bounds); return u; }
        // las piezas de la banda juntadas en bloques (las que se tocan o casi, a 400 px)
        static List<RectangleF> BandBlocks(List<Schematic> ps)
        {
            var bl = ps.Select(p => p.Bounds).ToList();
            for (bool merged = true; merged;)
            {
                merged = false;
                for (int i = 0; i < bl.Count && !merged; i++)
                    for (int j = i + 1; j < bl.Count; j++)
                        if (RectangleF.Inflate(bl[i], 400, 200).IntersectsWith(bl[j])) { bl[i] = RectangleF.Union(bl[i], bl[j]); bl.RemoveAt(j); merged = true; break; }
            }
            return bl;
        }

        // (2026-11-09) de qué dependencia es cada tramo de vía: de la que cubre más metros de él (un tramo largo que empieza en
        // una estación y sigue kilómetros por la línea es de la línea, no de la estación)
        Dictionary<int, string> _majorZone;
        Dictionary<int, string> MajorZone()
        {
            if (_majorZone != null) return _majorZone;
            var acc = new Dictionary<int, Dictionary<string, double>>();
            foreach (var kv in _zoneSpans)
                foreach (var (e, a, bb) in kv.Value)
                {
                    if (!acc.TryGetValue(e, out var d)) acc[e] = d = new();
                    d.TryGetValue(kv.Key, out double l); d[kv.Key] = l + Math.Max(0, bb - a);
                }
            _majorZone = acc.ToDictionary(kv => kv.Key, kv => kv.Value.OrderByDescending(x => x.Value).First().Key);
            foreach (var kv in _edgeZone) if (!_majorZone.ContainsKey(kv.Key)) _majorZone[kv.Key] = kv.Value;
            return _majorZone;
        }

        void ShowBand(int b)
        {
            if (_g == null || b <= 0) return;
            bool refill = _openBand != b;
            _openBand = b; _curBand = b; _depParts = null; _depWaiting = false;
            string label = Bandas.Label(b);
            // (2026-11-09) la banda es la «dependencia» que se ve: con esquema (si lo tiene y está elegido), su esquema; si no, el
            // trazado real encuadrado en ella
            _curZone = label;
            if (_schema && !_noSchema.Contains(label) && !_cache.ContainsKey(label)) BuildRouteSchema();
            _cur = !Board && _cache.TryGetValue(label, out var bs) ? bs : null;
            _title.Text = label;   // (la banda: «BR7 · Carles Romero – Huerta de Molinero»; con «PUESTO DE MANDO» delante no cabía junto a la hora)
            var zones = BandZones(b);
            foreach (var z in zones) _unread.Remove(z);
            _sel = new SchematicHeader { Name = label, Zones = zones };
            _heads = new List<SchematicHeader> { _sel };
            RefreshHeaders(); UpdateJefe();
            if (refill) BeginInvoke((Action)Refill);
            var bp = BandParts(b);
            if (bp.Count > 0) { _cur.Focus = null; _cur.FocusSet = new HashSet<Schematic>(bp); FitPart(Union(bp)); } else if (_cur != null) Fit(); else FitBand(b);
            UpdateViewBtn();
            TrainsTick();
            _canvas.Invalidate();
        }

        // el trazado real, encuadrado en la banda
        void FitBand(int b)
        {
            if (_board == null || _canvas.Width < 50) { _canvas.Invalidate(); return; }
            RectangleF u = RectangleF.Empty;
            void Add(PointF[] pc) { if (pc == null || pc.Length == 0) return; var r = RectangleF.FromLTRB(pc.Min(p => p.X), pc.Min(p => p.Y), pc.Max(p => p.X), pc.Max(p => p.Y)); u = u.IsEmpty ? r : RectangleF.Union(u, r); }
            var zones = new HashSet<string>(BandZones(b));
            if (_zoneSpans != null)
                foreach (var kv in _zoneSpans) { if (!zones.Contains(kv.Key)) continue; foreach (var (e, a, bb) in kv.Value) Add(_board.Piece(e, a, bb)); }
            else if (_edgeZone != null)
                foreach (var kv in _edgeZone) if (zones.Contains(kv.Value)) Add(_board.Piece(kv.Key, 0, _board.G.Edges[kv.Key].Len));
            if (u.IsEmpty) { FitBoard(); return; }
            u.Inflate(Math.Max(60, u.Width * 0.06f), Math.Max(60, u.Height * 0.06f));
            CenterOn(u);
        }

        // (DrawItem) una fila de banda: su color, su nombre y quién la tiene
        void DrawBandRow(System.Windows.Forms.DrawItemEventArgs e, int b, bool sel)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(sel ? Theme.Surface2 : Color.FromArgb(26, 30, 36))) g.FillRectangle(bg, e.Bounds);
            var col = Bandas.Colors[b];
            using (var cb = new SolidBrush(col)) g.FillRectangle(cb, e.Bounds.X + 6, e.Bounds.Y + 5, 4, e.Bounds.Height - 10);
            string arrow = _openBand == b ? "▾" : "▸";
            var zones = BandZones(b);
            var owners = zones.Select(OwnerOf).Where(o => o != null).Distinct().ToList();
            bool bell = zones.Any(z => _unread.Contains(z));
            int ownW = owners.Count > 0 ? Theme.Px(70) : 0;
            using var f = Theme.Font(8.7f, FontStyle.Bold);
            System.Windows.Forms.TextRenderer.DrawText(g, arrow + " " + Bandas.Label(b), f,
                new Rectangle(e.Bounds.X + 14, e.Bounds.Y, e.Bounds.Width - 18 - ownW - (bell ? 26 : 0), e.Bounds.Height), Theme.Text,
                System.Windows.Forms.TextFormatFlags.VerticalCenter | System.Windows.Forms.TextFormatFlags.EndEllipsis | System.Windows.Forms.TextFormatFlags.NoPrefix);
            if (owners.Count > 0) DrawOwnerTag(g, e.Bounds, owners.Count == 1 ? owners[0] : owners[0] + " +" + (owners.Count - 1), zones.All(Mine), bell ? 26 : 4);
            if (bell) ColorText.Text(g, "🔔", _list.Font, new Rectangle(e.Bounds.Right - 26, e.Bounds.Y, 24, e.Bounds.Height), Color.FromArgb(255, 213, 79), System.Windows.Forms.TextFormatFlags.VerticalCenter | System.Windows.Forms.TextFormatFlags.NoPrefix);
        }
    }
}
