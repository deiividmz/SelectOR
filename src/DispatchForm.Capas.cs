// (1.2.91, petición 2026-11-07) PUESTO DE MANDO · capas encima del trazado, con el botón «🗺 Capas» (todas de serie):
//  · Dependencias: en el trazado real, el tramo de cada dependencia (estación o trayecto de línea) como una franja suave de su
//    color bajo sus vías, con una raya en cada límite y, en los trayectos, su nombre. El tramo es el que dibuja su esquema
//    (Schematic.DrawnEdges/DrawnRange), calculado una vez en segundo plano.
//  · PK: los hitos kilométricos de la ruta (los del .tdb: RouteGraph.Edge.MilePosts), sin amontonarse.
//  · Objetos de la sala (operarios, derrumbes, fuego, cartel de parada absoluta…): los que pone el administrador, para que el
//    jefe de circulación los vea mientras lleva los trenes; en el trazado real y en el esquema. Se leen cada 15 s
//    (room_objects_get: los puede leer cualquiera).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class DispatchForm
    {
        static bool _showZones = true, _showPk = true, _showObjs = true;
        public string ObjectsRoute = "CGL_V22N_RE";
        Dictionary<string, List<(int e, double a, double b)>> _zoneSpans; bool _zoneSpansBusy;
        List<(int e, double off)> _zoneLimits = new();   // donde se tocan dos dependencias distintas (en un mismo tramo)
        readonly List<(string zone, PointF[] pts, float w)> _zoneHits = new();   // (1.2.91) las franjas del último dibujo, para el clic
        List<ObjetosSalaForm.RoomObj> _roomObjs = new(); DateTime _objNext = DateTime.MinValue; bool _objBusy;
        Label _layersBtn;

        static readonly Color[] ZonePalette =
        {
            Color.FromArgb(92, 160, 255), Color.FromArgb(255, 170, 60), Color.FromArgb(110, 210, 120), Color.FromArgb(220, 110, 230),
            Color.FromArgb(70, 210, 210), Color.FromArgb(240, 220, 80), Color.FromArgb(255, 115, 115), Color.FromArgb(160, 140, 255),
        };
        Color ZoneColor(string name)
        {
            int bz = BandOfZone(name); if (bz > 0) return Bandas.Colors[bz];   // (2026-11-09) con bandas: el color de su banda
            int i = _zones?.FindIndex(z => z.Name == name) ?? -1;
            return ZonePalette[(i < 0 ? 0 : i) % ZonePalette.Length];
        }

        void BuildLayersBtn(Panel top)
        {
            _layersBtn = new EmojiLabel
            {
                Text = "🗺  " + Tr("Capas"), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Font(9f, FontStyle.Bold), Cursor = Cursors.Hand,
                Dock = DockStyle.Right, Width = Theme.Px(104), Margin = Padding.Empty, ForeColor = Theme.Text, BackColor = Theme.Surface2
            };
            _layersBtn.Click += (s, e) =>
            {
                var m = MenuStyle.Apply(new ContextMenuStrip());
                void Item(string text, Func<bool> get, Action<bool> set)
                {
                    var it = new ToolStripMenuItem(text) { Checked = get(), CheckOnClick = true };
                    it.CheckedChanged += (s2, e2) => { set(it.Checked); _canvas.Invalidate(); };
                    m.Items.Add(it);
                }
                Item(Tr("Tramo de cada dependencia"), () => _showZones, v => _showZones = v);
                Item(Tr("Puntos kilométricos (PK)"), () => _showPk, v => { _showPk = v; SchematicDraw.ShowPk = v; });   // (2026-11-10: también en el esquema)
                Item(Tr("Objetos de la sala"), () => _showObjs, v => _showObjs = v);
                Item(Tr("Túneles"), () => _showTunnels, v => _showTunnels = v);   // (2026-11-09) DispatchForm.Tuneles.cs
                Item(Tr("Notas del administrador"), () => _showNotes, v => _showNotes = v);   // (2026-11-09) DispatchForm.Notas.cs
                Item(Tr("Sentido de bloqueo"), () => _showBlocks, v => _showBlocks = v);   // (2026-11-10) DispatchForm.Bloqueo.cs
                m.Show(_layersBtn, new Point(0, _layersBtn.Height));
            };
            new ToolTip().SetToolTip(_layersBtn, Tr("Lo que se ve encima del trazado: el tramo de cada dependencia, los PK y los objetos de la sala"));
            var gap = new Panel { Dock = DockStyle.Right, Width = Theme.Px(6), BackColor = top.BackColor };
            top.Controls.Add(_layersBtn); top.Controls.Add(gap);
        }

        // (cada segundo, con el resto del puesto) los tramos de las dependencias, una vez; los objetos, cada 15 s
        void LayersTick()
        {
            if (_zoneSpans == null && !_zoneSpansBusy && _g != null && _zones != null && _board != null)
            {
                _zoneSpansBusy = true;
                var g = _g; var geo = _geo; var zones = _zones.ToList();
                Task.Run(() =>
                {
                    var d = new Dictionary<string, List<(int e, double a, double b)>>();
                    var built = new Dictionary<string, Schematic>();
                    foreach (var z in zones)
                    {
                        Schematic s; try { s = Schematic.Build(g, geo, z); } catch { continue; }
                        if (s == null) continue;
                        built[z.Name] = s;
                        var l = new List<(int, double, double)>();
                        foreach (var e in s.DrawnEdges) if (s.DrawnRange(e, out double a, out double b) && b - a > 0.5) l.Add((e, a, b));
                        d[z.Name] = l;
                    }
                    return (d, built);
                }).ContinueWith(t =>
                {
                    if (IsDisposed || t.IsFaulted) { _zoneSpansBusy = false; return; }
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            _zoneSpans = t.Result.d; _zoneLimits = Limits(t.Result.d); _zoneSpansBusy = false;
                            foreach (var kv in t.Result.built) if (!_cache.ContainsKey(kv.Key)) _cache[kv.Key] = kv.Value;   // (luego ya no se hacen)
                            _canvas.Invalidate();
                        }));
                    }
                    catch { _zoneSpansBusy = false; }
                });
            }
            NotesTick();   // (2026-11-09) notas del administrador, cada 20 s
            AlertsTick();  // (2026-11-09) avisos de incidencias, cada 15 s (DispatchForm.Incidencias.cs)
            TunnelsTick(); // (2026-11-09) los túneles, una vez (DispatchForm.Tuneles.cs)
            if (!_objBusy && DateTime.UtcNow >= _objNext)
            {
                _objBusy = true; _objNext = DateTime.UtcNow.AddSeconds(15);
                _ = Poll();
                async Task Poll()
                {
                    try
                    {
                        var (json, err) = await Supa.RpcAsync("room_objects_get", new { p_route = ObjectsRoute });
                        if (err != null || json == null || IsDisposed) return;
                        _roomObjs = ObjetosSalaForm.Parse(json, out _);
                        _canvas.Invalidate();
                    }
                    catch { }
                    finally { _objBusy = false; }
                }
            }
        }

        // (1.2.91) la dependencia bajo el ratón (su franja en el último dibujo; la más cercana, y las estaciones antes)
        string ZoneBandAt(Point p)
        {
            if (!_showZones || _zoneHits.Count == 0) return null;
            string best = null; double bd = double.MaxValue; bool bestLine = true;
            foreach (var (zone, pts, w) in _zoneHits)
            {
                double lim = w / 2 + Theme.Px(3), d = double.MaxValue;
                for (int i = 1; i < pts.Length; i++) d = Math.Min(d, SegDist(p, pts[i - 1], pts[i]));
                if (d > lim) continue;
                bool line = IsLineZone(zone);
                if (best == null || (bestLine && !line) || (bestLine == line && d < bd)) { best = zone; bd = d; bestLine = line; }
            }
            return best;
        }
        // los límites: un extremo de una dependencia a menos de 3 m de un extremo de otra, en el mismo tramo
        static List<(int e, double off)> Limits(Dictionary<string, List<(int e, double a, double b)>> d)
        {
            var ends = new Dictionary<int, List<(double off, string zone)>>();
            foreach (var kv in d) foreach (var (e, a, b) in kv.Value)
                {
                    if (!ends.TryGetValue(e, out var l)) ends[e] = l = new();
                    l.Add((a, kv.Key)); l.Add((b, kv.Key));
                }
            var res = new List<(int, double)>();
            foreach (var kv in ends)
            {
                var l = kv.Value.OrderBy(x => x.off).ToList();
                for (int i = 1; i < l.Count; i++)
                    if (l[i].zone != l[i - 1].zone && l[i].off - l[i - 1].off < 3 && !res.Any(r => r.Item1 == kv.Key && Math.Abs(r.Item2 - l[i].off) < 3))
                        res.Add((kv.Key, (l[i].off + l[i - 1].off) / 2));
            }
            return res;
        }

        // ── en el trazado real, debajo de las vías: el tramo de cada dependencia
        void DrawZonesUnder(Graphics g, Func<PointF, PointF> P, float zoom, RectangleF view)
        {
            _zoneHits.Clear();
            if (!_showZones || _zoneSpans == null || _board == null) return;
            float lw = Math.Max(1.6f, Math.Min(7f, 1.0f + zoom * 1.7f));
            float w = zoom < 0.05f ? lw * 2 + 4 : lw * 3 + 7;
            bool InView(PointF[] pts) { foreach (var q in pts) if (view.Contains(q)) return true; return false; }
            var labels = new List<(string name, PointF at, Color col)>();
            foreach (var kv in _zoneSpans)
            {
                var col = ZoneColor(kv.Key);
                bool line = IsLineZone(kv.Key);
                int alpha = _curBand > 0 ? (BandOfZone(kv.Key) == _curBand ? 120 : 14) : line ? 30 : 64;   // (2026-11-09) viendo una banda: ella, resaltada
                using var pen = new Pen(Color.FromArgb(alpha, col), w) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                PointF[] longest = null; float longLen = 0;
                foreach (var (e, a, b) in kv.Value)
                {
                    var pc = _board.Piece(e, a, b); if (pc == null || pc.Length < 2) continue;
                    var sp = pc.Select(P).ToArray(); if (!InView(sp)) continue;
                    g.DrawLines(pen, sp);
                    _zoneHits.Add((kv.Key, sp, w));
                    float l = 0; for (int i = 1; i < sp.Length; i++) l += Dist(sp[i - 1], sp[i]);
                    if (l > longLen) { longLen = l; longest = sp; }
                }
                if (line && longest != null && longLen > Theme.Px(140) && zoom < 1.2f) labels.Add((kv.Key, Mid(longest), col));
            }
            // los límites entre dependencias: una raya clara atravesada (de cerca)
            if (zoom >= 0.05f)
                using (var tick = new Pen(Color.FromArgb(215, 230, 235, 240), Math.Max(1.6f, lw * 0.55f)))
                    foreach (var (e, off) in _zoneLimits)
                    {
                        var p0 = P(_board.At(e, Math.Max(0, off - 1))); var p1 = P(_board.At(e, Math.Min(_board.G.Edges[e].Len, off + 1)));
                        var m = new PointF((p0.X + p1.X) / 2, (p0.Y + p1.Y) / 2); if (!view.Contains(m)) continue;
                        Tick(g, tick, m, p1, w * 1.1f);
                    }
            if (labels.Count == 0) return;
            using var f = Theme.Font(8f, FontStyle.Bold);
            var taken = new List<RectangleF>();
            foreach (var (name, at, col) in labels)
            {
                var sz = g.MeasureString(name, f);
                var r = new RectangleF(at.X - sz.Width / 2, at.Y + w / 2 + 2, sz.Width, sz.Height);
                if (taken.Any(t => t.IntersectsWith(r))) continue;
                taken.Add(r);
                using (var bb = new SolidBrush(Color.FromArgb(200, 20, 24, 30))) g.FillRectangle(bb, RectangleF.Inflate(r, 3, 1));
                using (var tb = new SolidBrush(Color.FromArgb(235, col))) g.DrawString(name, f, tb, r.X, r.Y);
            }
        }
        static void Tick(Graphics g, Pen pen, PointF end, PointF next, float w)
        {
            float dx = next.X - end.X, dy = next.Y - end.Y, l = (float)Math.Sqrt(dx * dx + dy * dy); if (l < 0.01f) return;
            float nx = -dy / l * w * 0.75f, ny = dx / l * w * 0.75f;
            g.DrawLine(pen, end.X - nx, end.Y - ny, end.X + nx, end.Y + ny);
        }
        static float Dist(PointF a, PointF b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        static PointF Mid(PointF[] pts)
        {
            float total = 0; for (int i = 1; i < pts.Length; i++) total += Dist(pts[i - 1], pts[i]);
            float half = total / 2, acc = 0;
            for (int i = 1; i < pts.Length; i++)
            {
                float l = Dist(pts[i - 1], pts[i]);
                if (acc + l >= half && l > 0) { float k = (half - acc) / l; return new PointF(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * k, pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * k); }
                acc += l;
            }
            return pts[pts.Length / 2];
        }

        // ── en el trazado real, encima: los PK y los objetos
        void DrawBoardLayers(Graphics g, Rectangle area)
        {
            if (_board == null) return;
            PointF P(PointF q) => new PointF(_bOrigin.X + q.X * _bZoom, _bOrigin.Y + q.Y * _bZoom);
            var view = RectangleF.Inflate(area, 20, 20);
            if (_showPk && _bZoom >= 0.05f)
            {
                var drawn = new List<PointF>();
                float minGap = Theme.Px(80);
                using var f = Theme.Font(7.6f);
                using var dot = new SolidBrush(Color.FromArgb(200, 205, 210));
                var ci = I18n.Culture;
                var E = _board.G.Edges;
                for (int k = 0; k < E.Length; k++)
                    foreach (var (off, v) in E[k].MilePosts)
                    {
                        var p = P(_board.At(k, off)); if (!view.Contains(p)) continue;
                        bool near = false; foreach (var q in drawn) if (Math.Abs(q.X - p.X) < minGap && Math.Abs(q.Y - p.Y) < minGap * 0.4f) { near = true; break; }
                        if (near) continue;
                        drawn.Add(p);
                        g.FillEllipse(dot, p.X - 2.4f, p.Y - 2.4f, 4.8f, 4.8f);
                        string t = "PK " + v.ToString(Math.Abs(v - Math.Round(v)) < 0.05 ? "0" : "0.#", ci);
                        var at = new PointF(p.X + 5, p.Y - f.Height - 1);
                        using (var sh = new SolidBrush(Color.FromArgb(200, 10, 12, 14))) g.DrawString(t, f, sh, at.X + 1, at.Y + 1);
                        using (var tb = new SolidBrush(Color.FromArgb(205, 212, 218))) g.DrawString(t, f, tb, at.X, at.Y);
                    }
            }
            if (_showObjs && _roomObjs.Count > 0)
                DrawObjects(g, view, o =>
                {
                    var (x, y) = _board.G.Proj(o.Lat, o.Lon);
                    return P(_board.W(x, y));
                }, _bZoom >= 0.45f);
        }

        // ── en el esquema de una dependencia: los objetos que caen en sus vías
        void DrawSchematicLayers(Graphics g, Rectangle area)
        {
            if (!_showObjs || _cur == null || _roomObjs.Count == 0) return;
            var view = RectangleF.Inflate(area, 20, 20);
            DrawObjects(g, view, o =>
            {
                if (!_cur.Locate(o.Lat, o.Lon, out int e, out double off)) return (PointF?)null;
                var pc = _cur.Piece(e, off - 1, off + 1); if (pc == null || pc.Length == 0) return (PointF?)null;
                var q = pc[pc.Length / 2];
                return new PointF(_origin.X + q.X * _zoom, _origin.Y + q.Y * _zoom);
            }, true);
        }

        void DrawObjects(Graphics g, RectangleF view, Func<ObjetosSalaForm.RoomObj, PointF?> where, bool labels)
        {
            using var fe = Theme.Font(9.5f);
            using var fl = Theme.Font(7.4f, FontStyle.Bold);
            _btHits.Clear();
            float r = Theme.Px(10);
            var taken = new List<RectangleF>();   // (los rótulos no se pisan: el siguiente, debajo; si no cabe, sin rótulo)
            foreach (var o in _roomObjs)
            {
                PointF? at0; try { at0 = where(o); } catch { at0 = null; }
                if (at0 == null || !view.Contains(at0.Value)) continue;
                var p = at0.Value;
                bool fire = o.Kind == "fuego" || o.Kind == "incendio", worker = ObjetosSalaForm.IsWorker(o.Kind), sign = o.Kind == "cartel_parada", bt = ObjetosSalaForm.IsBt(o.Kind);
                var col = bt ? BtColor(o.Id) : fire ? Color.FromArgb(255, 110, 40) : worker ? (o.Kind.StartsWith("obra_") ? Color.FromArgb(255, 170, 40) : ObjetosSalaForm.PersonColor(o.Kind)) : sign ? Color.FromArgb(230, 60, 60) : Color.FromArgb(190, 140, 90);
                using (var b = new SolidBrush(Color.FromArgb(235, 22, 26, 32))) g.FillEllipse(b, p.X - r, p.Y - r, 2 * r, 2 * r);
                using (var pen = new Pen(col, 2f)) g.DrawEllipse(pen, p.X - r, p.Y - r, 2 * r, 2 * r);
                if (bt) _btHits.Add((new RectangleF(p.X - r - 3, p.Y - r - 3, 2 * r + 6, 2 * r + 6), o, p));   // (2026-11-10: clic derecho, su indicación)
                string emo = bt ? "🚩" : fire ? "🔥" : worker ? (o.Kind.StartsWith("obra_") ? "👷" : ObjetosSalaForm.PersonIcon(o.Kind).Trim()) : sign ? "🛑" : "⛰";
                ColorText.Text(g, emo, fe, Rectangle.Round(new RectangleF(p.X - r, p.Y - r, 2 * r, 2 * r)), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                if (!labels) continue;
                string t = bt ? BtLabel(o.Id) : Tr(ObjetosSalaForm.Kinds.FirstOrDefault(k => k.kind == o.Kind).label ?? o.Kind);
                var sz = g.MeasureString(t, fl);
                var box = new RectangleF(p.X - sz.Width / 2 - 3, p.Y + r + 2, sz.Width + 6, sz.Height);
                int tries = 0; while (taken.Any(t => t.IntersectsWith(box)) && tries++ < 3) box.Y += box.Height + 1;
                if (taken.Any(t => t.IntersectsWith(box))) continue;
                taken.Add(box);
                using (var lp = new Pen(Color.FromArgb(120, col), 1f)) if (box.Y > p.Y + r + 3) g.DrawLine(lp, p.X, p.Y + r, p.X, box.Y);
                using (var bb = new SolidBrush(Color.FromArgb(225, 22, 26, 32))) g.FillRectangle(bb, box);
                using (var tb = new SolidBrush(col)) g.DrawString(t, fl, tb, box.X + 3, box.Y);
            }
        }
    }
}
