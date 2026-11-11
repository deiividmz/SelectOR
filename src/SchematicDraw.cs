// PUESTO DE MANDO: el dibujo del esquema (Schematic.cs) al estilo de un videográfico (1.2.75), con lo que pasa en la sala.
//  · Vías gruesas partidas en cantones (en cada señal, con un hueco): gris si están libres, rojo si hay un tren, verde el
//    itinerario de una señal abierta (de ella a la siguiente, por las agujas como están; blanco si es un rebase autorizado).
//  · Agujas: el lado puesto sigue continuo y el otro, cortado junto a la aguja; cada una con su número en un recuadro
//    (azul si está enclavada).
//  · Señales: mástil y luz del lado al que miran, con su número (de Open Rails) con zoom suficiente.
//  · Trenes: su cantón en rojo y un rótulo con el nombre del maquinista (naranja; los de la IA, en gris).
// Todo en píxeles de pantalla: P(x, y) = origen + zoom · esquema.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SelectOR
{
    public sealed class SchematicLive
    {
        public Func<int, int> SwitchPos;                          // orden de la foto de agujas → 0/1 (−1: no se sabe)
        public Func<int, bool> Locked;                            // orden de la foto de agujas → enclavada
        public Func<RoomMapGeo.Sig, int> Aspect;                  // aspecto (0…7; −1: no se sabe)
        public Func<RoomMapGeo.Sig, RoomMapDraw.Figure> Figure;   // (1.2.75) su figura del RCF (luces y pantalla de preanuncio); null: no se sabe
        public bool Blinking;                                     // (lo pone el dibujo) hay alguna luz a destellos: repintar a menudo
        public Func<int, string> SignalWhy;                       // (1.2.77) por qué está en parada una señal que espera un tren (null: sin motivo)
        public readonly List<(string name, string sub, PointF[] body, bool player, bool ai)> Trains = new();   // ai: (1.2.77) del horario de la sala
        public readonly HashSet<int> Flash = new();               // señales en las que alguien acaba de pedir paso (Tab): parpadean
        public readonly Dictionary<int, List<(double a, double b)>> Occupied = new();          // por tramo: metros con un tren
        public readonly Dictionary<int, List<(double a, double b, bool callOn)>> Route = new(); // por tramo: itinerarios establecidos
        public readonly Dictionary<int, List<(double a, double b)>> Cancelling = new();         // (1.2.75) itinerarios anulándose (temporizados)
        public readonly Dictionary<int, List<(double a, double b)>> Queued = new();             // (1.2.75) itinerarios en cola (discontinuo)
        public readonly HashSet<int> Discord = new();             // (1.2.81) agujas (orden) de un itinerario que no están como él dice: en rojo
        public HashSet<int> LevelClosed;                          // (1.2.84) pasos a nivel (elemento del .tdb) con las barreras bajadas; null: no se sabe
    }

    // (puesto de mando) la dependencia elegida, sus zonas y su jefe de circulación
    public sealed class SchematicHeader { public string Name; public List<string> Zones = new(); public string Jefe; public bool Mine, Selected; }

    public static class SchematicDraw
    {
        public static readonly Color Bg = Color.FromArgb(11, 13, 16), TrackCol = Color.FromArgb(118, 127, 138), Dim = Color.FromArgb(92, 102, 112),
                                     Occ = Color.FromArgb(240, 160, 48), Txt = Color.FromArgb(207, 214, 221), StationCol = Color.FromArgb(242, 201, 76),
                                     PlatCol = Color.FromArgb(52, 63, 78), AiCol = Color.FromArgb(150, 158, 166),
                                     PlatEdge = Color.FromArgb(78, 92, 110), TagBg = Color.FromArgb(30, 64, 104), TagTxt = Color.FromArgb(226, 236, 248),
                                     Busy = Color.FromArgb(226, 75, 74), RouteCol = Color.FromArgb(120, 200, 80), CancelCol = Color.FromArgb(255, 183, 77), CallOnCol = Color.FromArgb(236, 238, 240),
                                     LockCol = Color.FromArgb(79, 165, 247);

        static readonly ConditionalWeakTable<Schematic, List<(int e, double a, double b)>> _circ = new();
        static readonly ConditionalWeakTable<Schematic, Dictionary<int, int>> _swNum = new();

        // número de cada aguja en el esquema (de izquierda a derecha). (1.2.81) Cada travesía, un número para todas sus agujas
        // (en su sitio de izquierda a derecha); cada una, con una letra (SwitchLabel)
        public static int SwitchNumber(Schematic s, int node) => _swNum.GetValue(s, k =>
        {
            var d = new Dictionary<int, int>(); int i = 1;
            var tij = new HashSet<int>(k.ScissorGroups.SelectMany(q => q.Nodes));   // (1.2.81: las tijeras, con su propia serie T1, T2…)
            var items = k.Switches.Where(sw => !tij.Contains(sw.Node)).Select(sw => (at: sw.At, nodes: new[] { sw.Node })).Concat(k.SlipGroups.Select(sg => (at: sg.At, nodes: sg.Nodes)));
            foreach (var it in items.OrderBy(q => q.at.X).ThenBy(q => q.at.Y)) { foreach (int nd in it.nodes) d[nd] = i; i++; }
            return d;
        }).TryGetValue(node, out int n) ? n : 0;
        // (1.2.81) como se ve: «7»; en una travesía, «12a», «12b»…
        public static int ScissorNumber(Schematic s, Schematic.ScissorGroup grp) => s.ScissorGroups.OrderBy(q => q.Center.X).ThenBy(q => q.Center.Y).ToList().IndexOf(grp) + 1;
        public static string SwitchLabel(Schematic s, int node)
        {
            var tg = s.ScissorGroups.FirstOrDefault(q => q.Nodes.Contains(node));
            if (tg != null) return "T" + ScissorNumber(s, tg) + ((char)('a' + Array.IndexOf(tg.Nodes, node))).ToString();
            int n = SwitchNumber(s, node); if (n <= 0) return node.ToString();
            var grp = s.SlipGroups.FirstOrDefault(q => q.Nodes.Contains(node));
            return grp == null || grp.Nodes.Length < 2 ? n.ToString() : n + ((char)('a' + Array.IndexOf(grp.Nodes, node))).ToString();
        }
        // (1.2.81) las agujas bajo el ratón (las sueltas y las de una travesía), de la más cercana a la más lejana
        public static List<(int node, int order, string label)> SwitchesAt(Schematic s, PointF origin, float zoom, Point p, float r)
        {
            var l = new List<(int node, int order, string label, float d)>();
            if (s == null) return new();
            float D(PointF a) { float x = origin.X + a.X * zoom - p.X, y = origin.Y + a.Y * zoom - p.Y; return (float)Math.Sqrt(x * x + y * y); }
            foreach (var sw in s.Switches) { float d = D(sw.At); if (d < r) l.Add((sw.Node, sw.Order, SwitchLabel(s, sw.Node), d)); }
            foreach (var sg in s.SlipGroups) { float d = D(sg.At); if (d < r) for (int i = 0; i < sg.Nodes.Length; i++) l.Add((sg.Nodes[i], sg.Orders[i], SwitchLabel(s, sg.Nodes[i]), d)); }
            // (1.2.81) en el símbolo de una tijera, sus cuatro agujas (y no sueltas: se quitan las que ya salían por su punto)
            foreach (var tg in s.ScissorGroups)
            {
                var box = ScissorBox(tg, origin, zoom);
                if (!RectangleF.Inflate(box, 6, 6).Contains(p)) continue;
                l.RemoveAll(q => tg.Nodes.Contains(q.node));
                for (int i = 0; i < 4; i++) l.Add((tg.Nodes[i], tg.Orders[i], SwitchLabel(s, tg.Nodes[i]), 0));
            }
            return l.OrderBy(q => q.d).ThenBy(q => q.label).Select(q => (q.node, q.order, q.label)).ToList();
        }

        // (1.2.75) dónde ha quedado cada señal en el último dibujo (mástil, figura, preanuncio y número), para el clic: antes
        // solo valía el pie del mástil, sobre la vía, y la señal se pinta apartada
        static readonly ConditionalWeakTable<Schematic, List<(Schematic.Signal sg, RectangleF box)>> _sigBoxes = new();
        static readonly ConditionalWeakTable<Schematic, Dictionary<(int, double, double), RectangleF>> _circBox = new();
        // (1.2.81) y cada número de aguja (su texto y su recuadro), y cada símbolo de tijera: para la auditoría de diseño
        static readonly ConditionalWeakTable<Schematic, List<(string t, RectangleF box)>> _numDrawn = new();
        public static List<(string t, RectangleF box)> NumbersDrawn(Schematic s) => s != null && _numDrawn.TryGetValue(s, out var l) ? l : new();
        public static Schematic.Signal SignalAt(Schematic s, Point p, float slack = 4)
        {
            if (s == null || !_sigBoxes.TryGetValue(s, out var l)) return null;
            Schematic.Signal best = null; float bd = float.MaxValue;
            foreach (var (sg, box) in l)
            {
                var b = RectangleF.Inflate(box, slack, slack);
                if (!b.Contains(p)) continue;
                float cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2, d = (cx - p.X) * (cx - p.X) + (cy - p.Y) * (cy - p.Y);
                if (d < bd) { bd = d; best = sg; }
            }
            return best;
        }

        public static Action<string> Prof;   // (medidas: auditoría)
        public static Color RouteColor => RouteCol;
        public static readonly Color QueueColor = Color.FromArgb(100, 181, 246);   // (1.2.75) itinerarios en cola
        static readonly Color SidingCol = Color.FromArgb(150, 168, 186);   // (2026-11-10) el nombre de los apartaderos
        static readonly Color PkCol = Color.FromArgb(196, 186, 140);       // (2026-11-10) los PK
        public static bool ShowPk = true;                                   // (2026-11-10) los PK en el esquema (Capas del puesto)

        public static void Draw(Graphics g, Schematic s, SchematicLive live, PointF origin, float zoom, Rectangle area)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var bg = new SolidBrush(Bg)) g.FillRectangle(bg, area);
            if (s == null) return;
            PointF P(PointF q) => new PointF(origin.X + q.X * zoom, origin.Y + q.Y * zoom);
            float lw = Math.Max(2.6f, Math.Min(6f, 3.4f * zoom));
            // (2026-11-10) solo lo que se ve (con margen): con la ruta entera en un esquema se dibujaba todo en cada repintado
            var vis = RectangleF.Inflate(new RectangleF((area.Left - origin.X) / zoom, (area.Top - origin.Y) / zoom, area.Width / zoom, area.Height / zoom), 160 / zoom, 160 / zoom);
            bool VisP(PointF q) => vis.Contains(q);
            bool VisA(PointF[] a)
            {
                if (a == null || a.Length == 0) return false;
                float x0 = a[0].X, x1 = x0, y0 = a[0].Y, y1 = y0;
                for (int i = 1; i < a.Length; i++) { if (a[i].X < x0) x0 = a[i].X; else if (a[i].X > x1) x1 = a[i].X; if (a[i].Y < y0) y0 = a[i].Y; else if (a[i].Y > y1) y1 = a[i].Y; }
                return x1 >= vis.Left && x0 <= vis.Right && y1 >= vis.Top && y0 <= vis.Bottom;
            }

            // andenes (1.2.75, como en los esquemas de ADIF): una barra junto a su vía y el número de la vía a cada extremo
            float ph = Math.Max(3.5f, Math.Min(9f, 7f * zoom));
            var platBars = new List<RectangleF>();
            using (var pb = new SolidBrush(PlatCol))
            using (var pe = new Pen(PlatEdge, 1f))
                foreach (var p in s.Platforms)
                {
                    if (!vis.IntersectsWith(RectangleF.Inflate(p.Bar, 2, 20))) continue;
                    var a = P(new PointF(p.Bar.Left, p.Bar.Top)); var b = P(new PointF(p.Bar.Right, p.Bar.Top));
                    var r = new RectangleF(a.X, p.Above ? a.Y - lw / 2 - 1.5f - ph : a.Y + lw / 2 + 1.5f, b.X - a.X, ph);
                    g.FillRectangle(pb, r); g.DrawRectangle(pe, r.X, r.Y, r.Width, r.Height);
                    platBars.Add(r);
                }

            Prof?.Invoke("circ");
            // vías: cada cantón aparte, de su color
            var circ = _circ.GetValue(s, SchematicCircuits.Circuits);
            // (2026-11-10) el recuadro de cada cantón, una vez: para no calcular su trazado si no se ve
            var cbox = _circBox.GetValue(s, ss => new Dictionary<(int, double, double), RectangleF>());
            float gap = Math.Max(1.2f, Math.Min(2.5f, 1.6f * zoom));
            bool Over(List<(double a, double b)> l, double a, double b) => l != null && l.Any(q => q.a < b - 0.5 && a + 0.5 < q.b);
            var lines = new List<PointF[]>();   // (1.2.81) lo dibujado, para colocar las señales
            // (1.2.81) la vía de un paso superior, la última y con un borde del color del fondo: corta las que pasan por debajo
            var overE = new HashSet<int>(s.Bridges.Select(q => q.Edge));
            var pens = new Dictionary<Color, Pen>();   // (un lápiz por color, no uno por cantón)
            foreach (var (e, a, b) in overE.Count == 0 ? (IEnumerable<(int e, double a, double b)>)circ : circ.OrderBy(c => overE.Contains(c.e) ? 1 : 0))
            {
                if (s.HiddenEdges.Contains(e)) continue;   // (1.2.81: los de una tijera van en su símbolo)
                if (cbox.TryGetValue((e, a, b), out var cb0) && !vis.IntersectsWith(cb0)) continue;
                var pc = s.Piece(e, a, b);
                if (pc != null && pc.Length > 0 && !cbox.ContainsKey((e, a, b))) cbox[(e, a, b)] = RectangleF.FromLTRB(pc.Min(q => q.X) - 1, pc.Min(q => q.Y) - 1, pc.Max(q => q.X) + 1, pc.Max(q => q.Y) + 1);
                if (pc == null || !VisA(pc)) continue;
                var pts = Trim(pc.Select(P).ToArray(), gap);
                if (pts == null) continue;
                Color col = TrackCol;
                if (live != null)
                {
                    live.Occupied.TryGetValue(e, out var oc);
                    if (Over(oc, a, b)) col = Busy;
                    else if (live.Cancelling.TryGetValue(e, out var cl) && Over(cl, a, b)) col = CancelCol;
                    else if (live.Route.TryGetValue(e, out var rl))
                    {
                        var hit = rl.Where(q => q.a < b - 0.5 && a + 0.5 < q.b).ToList();
                        if (hit.Count > 0) col = hit.All(q => q.callOn) ? CallOnCol : RouteCol;
                    }
                }
                if (overE.Contains(e))
                    using (var halo = new Pen(Bg, lw + Math.Max(4f, 3f * zoom)) { LineJoin = LineJoin.Round, StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                        g.DrawLines(halo, Trim(pts, Math.Max(6f, 5f * zoom)) ?? pts);
                if (!pens.TryGetValue(col, out var pen)) pens[col] = pen = new Pen(col, lw) { LineJoin = LineJoin.Miter, StartCap = LineCap.Flat, EndCap = LineCap.Flat };
                g.DrawLines(pen, pts);
                lines.Add(pts);
            }
            foreach (var pn in pens.Values) pn.Dispose();
            Prof?.Invoke("links");
            // (2026-11-09) esquema de la ruta: las vías que unen una banda con la siguiente
            if (s.Links.Count > 0)
            {
                // (2026-11-10) donde pasa por encima de otra vía (paso superior, Edge = −1 − su número): antes, un borde del color del
                // fondo que corta la de abajo
                var over = new Dictionary<int, List<(PointF at, PointF d)>>();
                foreach (var (at, d, e) in s.Bridges) if (e < 0) { if (!over.TryGetValue(-1 - e, out var ol)) over[-1 - e] = ol = new(); ol.Add((at, d)); }
                using var lp = new Pen(TrackCol, lw) { LineJoin = LineJoin.Miter, StartCap = LineCap.Flat, EndCap = LineCap.Flat };
                using var halo = new Pen(Bg, lw + Math.Max(4f, 3f * zoom)) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
                float hh = Math.Max(7f, Math.Min(16f, 8f * zoom));
                for (int li = 0; li < s.Links.Count; li++)
                {
                    var l = s.Links[li]; if (!VisA(l)) continue;
                    if (over.TryGetValue(li, out var ol))
                        foreach (var (at, d) in ol) { var q = P(at); g.DrawLine(halo, q.X - d.X * hh, q.Y - d.Y * hh, q.X + d.X * hh, q.Y + d.Y * hh); }
                    g.DrawLines(lp, l.Select(P).ToArray());
                }
            }
            // (1.2.75) itinerarios en cola: encima, en discontinuo azul
            if (live != null && live.Queued.Count > 0)
                using (var qp = new Pen(QueueColor, Math.Max(1.6f, lw - 1.5f)) { DashStyle = DashStyle.Dash })
                    foreach (var kv in live.Queued)
                        foreach (var (a, b) in kv.Value)
                        {
                            if (s.HiddenEdges.Contains(kv.Key)) continue;   // (1.2.81: las de una tijera, en su símbolo)
                            var pc = s.Piece(kv.Key, a, b);
                            if (pc != null && pc.Length >= 2 && VisA(pc)) g.DrawLines(qp, pc.Select(P).ToArray());
                        }
            Prof?.Invoke("agujas");
            // agujas: (1.2.81) la rama puesta, marcada; la otra, cortada junto a la aguja (DrawSwitchState)
            var tijN = new HashSet<int>(s.ScissorGroups.SelectMany(q => q.Nodes));
            if (live?.SwitchPos != null)
                foreach (var sw in s.Switches)
                {
                    if (tijN.Contains(sw.Node) || !VisP(sw.At)) continue;   // (1.2.81: van en el símbolo de su tijera)
                    int pos = sw.Order >= 0 ? live.SwitchPos(sw.Order) : -1;
                    bool locked = live.Locked != null && sw.Order >= 0 && live.Locked(sw.Order);
                    var l0 = sw.Legs[0]?.Select(P).ToArray(); var l1 = sw.Legs[1]?.Select(P).ToArray();
                    Color? lc = pos == 0 || pos == 1 ? LegLiveCol(live, sw.LegEdge[pos], sw.LegFromA[pos], sw.LegLen[pos]) : null;
                    DrawSwitchState(g, pos == 1 ? l1 : l0, pos == 1 ? l0 : l1, pos, locked, lw, sw.Straight >= 0 ? sw.Straight : FlatLeg(sw.Legs), lc);
                    if (sw.Order >= 0 && live.Discord.Contains(sw.Order)) DrawDiscord(g, P(sw.At), lw);
                }
            // número de cada aguja (azul si está enclavada). (1.2.81) Aquí solo su sitio de siempre (las señales lo evitan); se
            // dibuja después de las señales, apartado si choca con otro número o con una señal
            var numBoxes = new List<RectangleF>();
            var numItems = new List<(Schematic.Switch sw, RectangleF box, string t, bool locked, float side, PointF q, SizeF sz)>();
            if (zoom >= 0.7f)
                using (var nf = Theme.Font(6.6f))
                    foreach (var sw in s.Switches)
                    {
                        if (tijN.Contains(sw.Node) || !VisP(sw.At)) continue;
                        int n = SwitchNumber(s, sw.Node); if (n <= 0) continue;
                        bool locked = live?.Locked != null && sw.Order >= 0 && live.Locked(sw.Order);
                        // del lado contrario al que sale la desviada (para no taparla)
                        float side = 1;
                        if (sw.Legs[0] != null && sw.Legs[1] != null) side = (sw.Legs[0][^1].Y + sw.Legs[1][^1].Y) / 2 > sw.At.Y + 0.5f ? -1 : 1;
                        var q = P(sw.At); string t = n.ToString();
                        var sz = g.MeasureString(t, nf);
                        var box = new RectangleF(q.X - sz.Width / 2 - 1, side > 0 ? q.Y + lw / 2 + 3 : q.Y - lw / 2 - 3 - sz.Height, sz.Width + 2, sz.Height);
                        numBoxes.Add(box); numItems.Add((sw, box, t, locked, side, q, sz));
                    }
            Prof?.Invoke("cruces");
            // travesías y cruces
            // (1.2.81, petición) cada escape en tijera, un símbolo: un recuadro con sus dos paralelas y sus dos diagonales en X,
            // unido a sus cuatro agujas; lo que está puesto, iluminado (paralela a directa en blanco, diagonal en amarillo; con
            // un tren, en rojo; con un itinerario, de su color); lo demás, apagado. Su número (T1, T2…) encima
            foreach (var tg in s.ScissorGroups)
            {
                if (!VisP(tg.At[0])) continue;
                var box = ScissorBox(tg, origin, zoom);
                bool Has(Dictionary<int, List<(double a, double b)>> d, int e) => d != null && d.TryGetValue(e, out var l) && l.Count > 0;
                int[] pos = tg.Orders.Select(o => live?.SwitchPos != null && o >= 0 ? live.SwitchPos(o) : -1).ToArray();
                bool known = pos.All(x => x >= 0);
                // una vía de la tijera está puesta si sus dos agujas van hacia ella (la paralela: a directa; la diagonal: a desviada)
                bool SetPar(int i, int j) => known && pos[i] == tg.Straight[i] && pos[j] == tg.Straight[j];
                bool SetDia(int i, int j) => known && tg.Straight[i] >= 0 && tg.Straight[j] >= 0 && pos[i] == 1 - tg.Straight[i] && pos[j] == 1 - tg.Straight[j];
                Color Col(int e, bool set, Color setCol)
                {
                    if (live != null && Has(live.Occupied, e)) return Busy;
                    if (live != null && Has(live.Cancelling, e)) return CancelCol;
                    if (live != null && live.Route.TryGetValue(e, out var rl) && rl.Count > 0) return rl.All(q => q.callOn) ? CallOnCol : RouteCol;
                    return set ? setCol : Color.FromArgb(62, 70, 80);
                }
                var cc = ScissorCorners(tg, origin, zoom); var c0 = cc[0]; var c1 = cc[1]; var c2 = cc[2]; var c3 = cc[3];
                var poly = new[] { c0, c1, c3, c2 };
                // de cada aguja a su esquina: (1.2.81) del color de la vía de la tijera que sale de ella (ocupada, itinerario…),
                // para que el itinerario siga sin cortes de la vía de fuera al símbolo
                var cornerE = new[] { new[] { tg.ParTop, tg.DiagDown }, new[] { tg.ParTop, tg.DiagUp }, new[] { tg.ParBot, tg.DiagUp }, new[] { tg.ParBot, tg.DiagDown } };
                for (int i = 0; i < 4; i++)
                {
                    Color sc = TrackCol;
                    if (live != null)
                    {
                        var es = cornerE[i];
                        if (es.Any(e => Has(live.Occupied, e))) sc = Busy;
                        else if (es.Any(e => Has(live.Cancelling, e))) sc = CancelCol;
                        else
                        {
                            var rl = es.SelectMany(e => live.Route.TryGetValue(e, out var l) ? l : Enumerable.Empty<(double a, double b, bool callOn)>()).ToList();
                            if (rl.Count > 0) sc = rl.All(q => q.callOn) ? CallOnCol : RouteCol;
                        }
                    }
                    using var lp = new Pen(sc, lw) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
                    g.DrawLine(lp, P(tg.At[i]), cc[i]);
                    if (live != null && sc == TrackCol && cornerE[i].Any(e => Has(live.Queued, e)))
                        using (var qp = new Pen(QueueColor, Math.Max(1.6f, lw - 1.5f)) { DashStyle = DashStyle.Dash }) g.DrawLine(qp, P(tg.At[i]), cc[i]);
                }
                bool locked = live?.Locked != null && tg.Orders.Any(o => o >= 0 && live.Locked(o));
                using (var fb = new SolidBrush(Bg)) g.FillPolygon(fb, poly);
                float il = Math.Max(1.6f, lw - 0.6f);
                var tl = new[] { (tg.ParTop, c0, c1, SetPar(0, 1), SwitchStraightCol), (tg.ParBot, c2, c3, SetPar(2, 3), SwitchStraightCol),
                                    (tg.DiagDown, c0, c3, SetDia(0, 3), SwitchDivergeCol), (tg.DiagUp, c2, c1, SetDia(2, 1), SwitchDivergeCol) };
                foreach (bool lit in new[] { false, true })   // (lo apagado debajo)
                    foreach (var (e, a, b, set, sc) in tl)
                    {
                        var col = Col(e, set, sc); bool on = col != Color.FromArgb(62, 70, 80);
                        if (on != lit) continue;
                        using var pn = new Pen(col, on ? il + 0.8f : il) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                        g.DrawLine(pn, a, b);
                    }
                // (1.2.81) itinerario en cola por la tijera: en discontinuo azul sobre su vía
                if (live != null)
                    using (var qp = new Pen(QueueColor, Math.Max(1.4f, il - 0.4f)) { DashStyle = DashStyle.Dash })
                        foreach (var (e, a, b, _, _) in tl)
                            if (Has(live.Queued, e) && !Has(live.Occupied, e) && !(live.Route.TryGetValue(e, out var rq) && rq.Count > 0)) g.DrawLine(qp, a, b);
                using (var bp = new Pen(locked ? LockCol : !known && live?.SwitchPos != null ? SwitchUnknownCol : Color.FromArgb(110, 120, 132), 1.2f))
                    g.DrawPolygon(bp, poly);
                if (zoom >= 0.55f)
                {
                    using var nf = Theme.Font(6.6f, FontStyle.Bold);
                    string t = "T" + ScissorNumber(s, tg); var sz = g.MeasureString(t, nf);
                    var tb = new RectangleF((c0.X + c1.X) / 2 - sz.Width / 2 - 1, box.Top - sz.Height - 2, sz.Width + 2, sz.Height);
                    using (var bb = new SolidBrush(Color.FromArgb(230, 22, 26, 32))) g.FillRectangle(bb, tb);
                    using (var tp = new Pen(locked ? LockCol : Color.FromArgb(95, 105, 115), 1f)) g.DrawRectangle(tp, tb.X, tb.Y, tb.Width, tb.Height);
                    using (var tt = new SolidBrush(locked ? LockCol : Color.FromArgb(190, 196, 204))) g.DrawString(t, nf, tt, tb.X + 1, tb.Y);
                    numBoxes.Add(tb);
                }
                numBoxes.Add(box);
            }
            // (1.2.81) el rombo de cada travesía, del color de sus agujas (blanco todas a directa, amarillo alguna a desviada,
            // azul enclavada, naranja sin dato), y su número como el de una aguja
            foreach (var sg in s.SlipGroups)
            {
                if (!VisP(sg.At)) continue;
                var q = P(sg.At); float r = 5f * Math.Min(1.6f, zoom);
                Color col = TrackCol;
                if (live?.SwitchPos != null)
                {
                    var ps = sg.Orders.Select(o => o >= 0 ? live.SwitchPos(o) : -1).ToList();
                    col = ps.Any(x => x < 0) ? SwitchUnknownCol : ps.Any(x => x == 1) ? SwitchDivergeCol : SwitchStraightCol;
                    if (live.Locked != null && sg.Orders.Any(o => o >= 0 && live.Locked(o))) col = LockCol;
                }
                var dia = new[] { new PointF(q.X - r, q.Y), new PointF(q.X, q.Y - r * 0.7f), new PointF(q.X + r, q.Y), new PointF(q.X, q.Y + r * 0.7f) };
                using (var fb = new SolidBrush(Bg)) g.FillPolygon(fb, dia);
                using (var rp = new Pen(col, col == TrackCol ? 1.4f : 2f)) g.DrawPolygon(rp, dia);
                if (zoom >= 0.7f)
                {
                    int n = SwitchNumber(s, sg.Nodes[0]); if (n <= 0) continue;
                    bool locked = live?.Locked != null && sg.Orders.Any(o => o >= 0 && live.Locked(o));
                    using var nf = Theme.Font(6.6f);
                    string t = n.ToString(); var sz = g.MeasureString(t, nf);
                    var box = new RectangleF(q.X - sz.Width / 2 - 1, q.Y - r * 0.7f - 3 - sz.Height, sz.Width + 2, sz.Height);
                    numBoxes.Add(box); numItems.Add((null, box, t, locked, -1, q, sz));
                }
            }
            // (las de las diagonales de una tijera van en su símbolo: dentro de lo que ocupan sus cuatro agujas, no)
            var tBoxes = s.ScissorGroups.Select(q => { var ps = q.At.Select(P).ToArray(); return RectangleF.FromLTRB(ps.Min(t => t.X) - 4, ps.Min(t => t.Y) - 4, ps.Max(t => t.X) + 4, ps.Max(t => t.Y) + 4); }).ToList();
            using (var xp = new Pen(TrackCol, 1.4f))
                foreach (var c in s.Crossings) { if (!VisP(c)) continue; var q = P(c); if (tBoxes.Any(b => b.Contains(q))) continue; float r = 4f; g.DrawLine(xp, q.X - r, q.Y - r, q.X + r, q.Y + r); g.DrawLine(xp, q.X - r, q.Y + r, q.X + r, q.Y - r); }
            // (1.2.81) pasos superiores: los pretiles del puente a los dos lados de la vía de arriba
            using (var bp = new Pen(Color.FromArgb(170, 180, 190), Math.Max(1.2f, lw * 0.4f)))
                foreach (var (at, d, _) in s.Bridges)
                {
                    if (!VisP(at)) continue;
                    var q = P(at); var n = new PointF(-d.Y, d.X);
                    float h = Math.Max(6f, Math.Min(14f, 7f * zoom)), o = lw / 2 + Math.Max(2.5f, 2f * zoom), fl = Math.Max(2.5f, 2.2f * zoom);
                    foreach (int side in new[] { -1, 1 })
                    {
                        PointF Pt(float along, float across) => new PointF(q.X + d.X * along + n.X * across * side, q.Y + d.Y * along + n.Y * across * side);
                        g.DrawLines(bp, new[] { Pt(-h - fl, o + fl), Pt(-h, o), Pt(h, o), Pt(h + fl, o + fl) });
                    }
                }
            // toperas
            using (var bp = new Pen(TrackCol, lw))
                foreach (var b in s.Buffers) { if (!VisP(b)) continue; var q = P(b); g.DrawLine(bp, q.X, q.Y - 5, q.X, q.Y + 5); }
            // (1.2.84) pasos a nivel: la carretera cruzando la vía (un rectángulo; las vías de un mismo paso, en uno solo): rojo
            // con las barreras bajadas, oscuro con el paso abierto, solo el borde si no se sabe. «PN» encima al acercar.
            var pnBoxes = new List<RectangleF>();
            if (s.LevelXs.Count > 0)
            {
                float hw = Math.Max(3.5f, 2.6f * Math.Min(2.2f, zoom)), hh = Math.Max(6f, lw / 2 + 4f * Math.Min(2.2f, zoom));
                var used = new HashSet<Schematic.LevelX>();
                foreach (var lx in s.LevelXs)
                {
                    if (!VisP(lx.At) || !used.Add(lx)) continue;
                    var grp = new List<Schematic.LevelX> { lx };
                    var q0 = P(lx.At);
                    foreach (var o in s.LevelXs)
                        if (!used.Contains(o) && o.Cr.Cluster == lx.Cr.Cluster && Math.Abs(P(o.At).X - q0.X) < 14) { grp.Add(o); used.Add(o); }
                    var pts = grp.Select(o => P(o.At)).ToList();
                    float cx = pts.Average(q => q.X), y0 = pts.Min(q => q.Y) - hh, y1 = pts.Max(q => q.Y) + hh;
                    var rect = RectangleF.FromLTRB(cx - hw, y0, cx + hw, y1);
                    bool? closed = live?.LevelClosed == null ? null : grp.Any(o => live.LevelClosed.Contains(o.Cr.Item));
                    using (var fb = new SolidBrush(closed == true ? Color.FromArgb(214, 58, 46) : Bg)) g.FillRectangle(fb, rect);
                    using (var op = new Pen(closed == true ? Color.FromArgb(255, 205, 200) : Color.FromArgb(190, 198, 206), 1.3f) { DashStyle = closed == null ? DashStyle.Dash : DashStyle.Solid })
                        g.DrawRectangle(op, rect.X, rect.Y, rect.Width, rect.Height);
                    // la vía, por dentro (sigue viéndose que la cruza)
                    using (var tp = new Pen(closed == true ? Color.FromArgb(255, 225, 220) : TrackCol, Math.Max(1f, lw * 0.6f)))
                        foreach (var q in pts) g.DrawLine(tp, rect.Left + 1, q.Y, rect.Right - 1, q.Y);
                    var box = RectangleF.Inflate(rect, 1.5f, 1.5f);
                    if (zoom >= 0.6f)
                    {
                        using var pf = Theme.Font(Math.Max(6.4f, 6.4f * Math.Min(1.8f, zoom / 1.2f)), FontStyle.Bold);
                        string pnt = PnLabel(grp.Select(o => o.Cr));   // (2026-11-10) con su PK exacto
                        var sz = g.MeasureString(pnt, pf);
                        var tr = new RectangleF(cx - sz.Width / 2, y0 - sz.Height, sz.Width, sz.Height);
                        using (var tb = new SolidBrush(closed == true ? Color.FromArgb(255, 120, 105) : Color.FromArgb(170, 178, 186))) g.DrawString(pnt, pf, tb, tr.X, tr.Y);
                        box = RectangleF.Union(box, tr);
                    }
                    numBoxes.Add(box); pnBoxes.Add(box);
                }
            }

            Prof?.Invoke("senales0");
            // señales: mástil y luz del lado al que miran. (1.2.75) Al acercar, crecen con el esquema (sk: hasta 3,5 veces)
            // (1.2.81) cada una colocada donde no pise a otra señal, un número de aguja, otra vía ni el rótulo de una salida
            // (PlaceSignals): con las filas juntas, dos señales a la misma altura en vías vecinas se montaban
            float sk = Math.Max(1f, Math.Min(3.5f, zoom / 1.4f));
            float lr0 = 2.3f * Math.Max(0.85f, Math.Min(1.4f, zoom)) * sk;   // radio de cada luz de la figura
            var boxes = new List<(Schematic.Signal sg, RectangleF box)>();
            _sigBoxes.AddOrUpdate(s, boxes);
            void Box(Schematic.Signal sg, PointF foot, RectangleF r) => boxes.Add((sg, RectangleF.Union(r, new RectangleF(foot.X - 3, Math.Min(foot.Y, r.Y), 6, Math.Abs(foot.Y - r.Y) + 1))));
            float idSize = 6.6f * Math.Min(1.8f, sk), papSize = 6.8f * Math.Min(2.2f, sk);
            var sigsVis = s.Signals.Where(q => VisP(q.At)).ToList();
            Prof?.Invoke("place");
            var places = PlaceSignals(g, s, sigsVis, live, P, zoom, sk, lr0, lw, idSize, papSize, lines, numBoxes, platBars);
            var beamsDone = new HashSet<(double, double, double)>();   // (2026-11-10) cada viga de pórtico, una vez
            using (var mp = new Pen(Color.FromArgb(150, 160, 170), 1.1f * Math.Min(2f, sk)))
            using (var op = new Pen(Color.FromArgb(220, 15, 17, 19), 1.2f * Math.Min(2f, sk)))
                foreach (var sg in sigsVis)
                {
                    var q = P(sg.At);
                    var pl = places.TryGetValue(sg, out var pp) ? pp : new SigPlace { Side = sg.Face < 0 ? -1 : 1, Mast = 8f * sk, Dir = sg.Face, Arm = 4.5f * sk };
                    int dir = pl.Dir; float dy = pl.Side * pl.Mast, arm = pl.Arm;
                    // (1.2.84) el pie del mástil, un poco hacia donde mira la señal: las dos de un mismo punto (una en cada
                    // sentido, entre dos vías) ya no salen del mismo sitio y no parecen un mástil con dos brazos
                    // (2026-11-10) en un pórtico, de la viga
                    q = pl.G ? new PointF(q.X + pl.Gx, q.Y) : new PointF(q.X + dir * (pl.Fo != 0 ? pl.Fo : FootOff(sk)), q.Y);
                    if (pl.G)
                    {
                        var key = (Math.Round(q.X), Math.Round(q.Y + pl.B0), Math.Round(q.Y + pl.B1));
                        if (beamsDone.Add(key)) DrawGantry(g, q.X, q.Y + pl.B0, q.Y + pl.B1, sk);
                        using (var tk = new SolidBrush(Color.FromArgb(150, 160, 170))) g.FillRectangle(tk, q.X - 1.6f * Math.Min(2f, sk), q.Y - 0.8f, 3.2f * Math.Min(2f, sk), 1.6f);   // (dónde cruza su vía)
                    }
                    else g.DrawLine(mp, q.X, q.Y + pl.Side * lw / 2, q.X, q.Y + dy);
                    g.DrawLine(mp, q.X, q.Y + dy, q.X + arm * dir, q.Y + dy);
                    int asp = live?.Aspect?.Invoke(sg.Sig) ?? -1;
                    float r = 3.2f * sk, lx = q.X + (arm + r) * dir, ly = q.Y + dy;
                    if (live != null && sg.Sig != null && live.Flash.Contains(sg.Sig.Obj) && DateTime.Now.Millisecond < 600)
                        using (var fr = new Pen(Color.FromArgb(255, 213, 79), 2.2f)) g.DrawEllipse(fr, lx - r - 6, ly - r - 6, 2 * r + 12, 2 * r + 12);
                    // (1.2.75) la figura de la señal, tumbada desde el mástil, y el número del preanuncio al final
                    var fig = live?.Figure?.Invoke(sg.Sig);
                    if (fig != null)
                    {
                        if (RoomMapDraw.Flashing(fig)) live.Blinking = true;
                        var fb = RoomMapDraw.DrawFigure(g, fig, new PointF(q.X + arm * dir, ly), lr0, zoom < 0.7f, dir);
                        float end = dir > 0 ? fb.Right + 2 * sk : fb.Left - 2 * sk;
                        if (fig.Pap != null)
                        {
                            using var pf = Theme.Font(papSize, FontStyle.Bold);
                            var psz = g.MeasureString(fig.Pap, pf);
                            float px = dir > 0 ? end : end - psz.Width - 1;
                            var ps = RoomMapDraw.DrawPap(g, fig, px, ly - psz.Height / 2, papSize);
                            end = dir > 0 ? end + ps.Width + 2 * sk : end - ps.Width - 2 * sk;
                        }
                        if (zoom >= 1.0f && sg.Sig?.Obj >= 0)
                        {
                            string t = sg.Sig.Obj.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            using var fi = Theme.Font(idSize);
                            var sz = g.MeasureString(t, fi);
                            float tx = dir > 0 ? end : end - sz.Width + 2, ty = ly - sz.Height / 2;
                            using var ib = new SolidBrush(Color.FromArgb(150, 160, 170)); g.DrawString(t, fi, ib, tx, ty);
                            end = dir > 0 ? tx + sz.Width : tx;
                        }
                        Box(sg, q, RectangleF.FromLTRB(Math.Min(fb.Left, end), fb.Top, Math.Max(fb.Right, end), fb.Bottom));
                        continue;
                    }
                    if (RoomMapDraw.IsCallOn(asp))
                    {
                        using (var top = new SolidBrush(RoomMapDraw.AspectColor(0))) g.FillPie(top, lx - r, ly - r, 2 * r, 2 * r, 180, 180);
                        using (var bot = new SolidBrush(RoomMapDraw.CallOnWhite)) g.FillPie(bot, lx - r, ly - r, 2 * r, 2 * r, 0, 180);
                    }
                    else using (var lb = new SolidBrush(asp < 0 ? Color.FromArgb(70, 76, 84) : RoomMapDraw.AspectColor(asp))) g.FillEllipse(lb, lx - r, ly - r, 2 * r, 2 * r);
                    g.DrawEllipse(op, lx - r, ly - r, 2 * r, 2 * r);
                    if (zoom >= 1.0f && sg.Sig?.Obj >= 0)
                    {
                        string t = sg.Sig.Obj.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        using var fi = Theme.Font(idSize);
                        var sz = g.MeasureString(t, fi);
                        float tx = dir > 0 ? lx + r + 1 : lx - r - sz.Width + 2, ty = ly - sz.Height / 2;
                        using var ib = new SolidBrush(Color.FromArgb(150, 160, 170)); g.DrawString(t, fi, ib, tx, ty);
                    }
                    Box(sg, q, RectangleF.FromLTRB(Math.Min(q.X, lx - r), ly - r, Math.Max(q.X, lx + r), ly + r));
                }

            Prof?.Invoke("nums");
            // (1.2.81) los números de las agujas, sin pisar otro número ni una señal (dos agujas a la misma altura en vías vecinas,
            // como en Dos Hermanos): en su sitio; si no, corrido hacia el tronco (las ramas salen hacia el otro lado) o al otro lado
            numBoxes.Clear(); numBoxes.AddRange(pnBoxes);   // (1.2.84: sin pisar un paso a nivel)
            var drawnNums = new List<(string t, RectangleF box)>(); _numDrawn.AddOrUpdate(s, drawnNums);
            foreach (var tg in s.ScissorGroups) numBoxes.Add(RectangleF.Inflate(ScissorBox(tg, origin, zoom), 2, 14));   // (1.2.81: sin pisar el símbolo de una tijera ni su número)
            if (numItems.Count > 0)
                using (var nf = Theme.Font(6.6f))
                    foreach (var (sw, box0, t, locked, side, q, sz) in numItems)
                    {
                        bool Busy(RectangleF c) => numBoxes.Any(r => r.IntersectsWith(RectangleF.Inflate(c, 1, 1))) || boxes.Any(b => b.box.IntersectsWith(c));
                        var box = box0;
                        if (Busy(box))
                        {
                            float legX = 0; if (sw != null) foreach (var l in sw.Legs) if (l != null && l.Length > 1) legX += l[^1].X - sw.At.X;
                            float tdir = legX > 0 ? -1 : 1, w = box.Width + 3;
                            float yOther = side > 0 ? q.Y - lw / 2 - 3 - sz.Height : q.Y + lw / 2 + 3;
                            var cands = new List<RectangleF>();
                            foreach (float y in new[] { box.Y, yOther })
                                foreach (float k in new[] { w / 2 + 3, w + 3, 1.6f * w + 3 })
                                    cands.Add(new RectangleF(box.X + tdir * k, y, box.Width, box.Height));
                            // (1.2.81) y, si no, hacia el otro lado y más lejos
                            foreach (float y in new[] { box.Y, yOther })
                                foreach (float k in new[] { w / 2 + 3, w + 3, 2.4f * w + 3, 3.2f * w + 3 })
                                    cands.Add(new RectangleF(box.X - tdir * k, y, box.Width, box.Height));
                            foreach (float y in new[] { box.Y, yOther }) cands.Add(new RectangleF(box.X + tdir * (2.4f * w + 3), y, box.Width, box.Height));
                            var free = cands.Where(c => !Busy(c)).Select(c => (RectangleF?)c).FirstOrDefault();
                            if (free != null) box = free.Value;
                            else
                            {
                                // ninguno libre: el que menos tape (sobre todo, señales)
                                float Tapa(RectangleF c) => boxes.Sum(b => { var r = RectangleF.Intersect(b.box, c); return r.Width * r.Height * 3; })
                                                         + numBoxes.Sum(b => { var r = RectangleF.Intersect(b, c); return r.Width * r.Height; });
                                box = cands.Prepend(box).OrderBy(Tapa).First();
                            }
                        }
                        numBoxes.Add(box); drawnNums.Add((t, box));
                        using (var bb = new SolidBrush(Color.FromArgb(230, 22, 26, 32))) g.FillRectangle(bb, box);
                        using (var bp = new Pen(locked ? LockCol : Color.FromArgb(95, 105, 115), 1f)) g.DrawRectangle(bp, box.X, box.Y, box.Width, box.Height);
                        using (var tbb = new SolidBrush(locked ? LockCol : Color.FromArgb(190, 196, 204))) g.DrawString(t, nf, tbb, box.X + 1, box.Y);
                    }

            Prof?.Invoke("tags");
            // (1.2.75) número de cada vía de andén: encima de todo (antes iba debajo de la vía, que lo cruzaba como tachado),
            // a la altura de la barra del andén, a la izquierda, con un borde del color del fondo
            // (1.2.81) del alto de una fila como mucho (con las filas juntas, el número tapaba la vía de al lado) y sin pisar
            // el de otro andén (se corre a la izquierda)
            var tagBoxes = new List<RectangleF>();
            if (zoom >= 0.45f)
                using (var f = Theme.Font(Math.Max(6.2f, Math.Min(15f, Math.Min(6f * zoom, Schematic.Row * zoom * 0.42f))), FontStyle.Bold))
                using (var tagB = new SolidBrush(TagBg))
                using (var tagT = new SolidBrush(TagTxt))
                using (var halo = new Pen(Bg, 2.5f))
                using (var edge = new Pen(Color.FromArgb(70, 120, 180), 1f))
                {
                    var done = new HashSet<string>();
                    var taken = tagBoxes;
                    foreach (var p in s.Platforms)
                    {
                        if (!vis.IntersectsWith(RectangleF.Inflate(p.Bar, 2, 20))) continue;
                        if (p.Label == null || !done.Add(p.Label + "@" + p.Bar.Top.ToString("0"))) continue;
                        var a = P(new PointF(p.Bar.Left, p.Bar.Top));
                        var sz = g.MeasureString(p.Label, f);
                        // a la altura de la barra del andén (centrado en ella), a su izquierda
                        float mid = p.Above ? a.Y - lw / 2 - 1.5f - ph / 2 : a.Y + lw / 2 + 1.5f + ph / 2;
                        var box = new RectangleF(a.X - sz.Width - 6, mid - sz.Height / 2, sz.Width + 2, sz.Height);
                        // (1.2.81) ni encima de una señal ni de un número de aguja: más a la izquierda o, si no, al otro extremo
                        var b2 = P(new PointF(p.Bar.Right, p.Bar.Top));
                        // (y sin pisar otra vía: una diagonal, una aguja; la suya, horizontal a su altura, no cuenta)
                        bool OnTrack(RectangleF r) => lines.Any(l => { for (int i = 1; i < l.Length; i++) { if (Math.Abs(l[i].Y - a.Y) < 1.5f && Math.Abs(l[i - 1].Y - a.Y) < 1.5f) continue; if (SegHits(r, l[i - 1], l[i])) return true; } return false; });
                        bool Busy(RectangleF r) => taken.Any(t => t.IntersectsWith(RectangleF.Inflate(r, 1, 0)))
                            || boxes.Any(t => t.box.IntersectsWith(r)) || numBoxes.Any(t => t.IntersectsWith(r)) || OnTrack(r);
                        var cands = new List<RectangleF>();
                        for (int k = 0; k < 4; k++) cands.Add(new RectangleF(a.X - sz.Width - 6 - k * (sz.Width + 5), box.Y, box.Width, box.Height));
                        for (int k = 0; k < 4; k++) cands.Add(new RectangleF(b2.X + 4 + k * (sz.Width + 5), box.Y, box.Width, box.Height));
                        var free = cands.Where(r => !Busy(r)).Select(r => (RectangleF?)r).FirstOrDefault();
                        if (free != null) box = free.Value;
                        else for (int k = 0; k < 6 && taken.Any(t => t.IntersectsWith(RectangleF.Inflate(box, 1, 0))); k++) box.X -= box.Width + 3;
                        taken.Add(box);
                        using (var path = Rounded(box, 3))
                        {
                            g.DrawPath(halo, path); g.FillPath(tagB, path); g.DrawPath(edge, path);
                        }
                        g.DrawString(p.Label, f, tagT, box.X + 1, box.Y);
                    }
                }

            Prof?.Invoke("trenes");
            // trenes: el rótulo con el nombre (su cantón ya va en rojo)
            if (live != null)
                using (var f = Theme.Font(7.4f, FontStyle.Bold))
                using (var fs = Theme.Font(6.8f))
                    foreach (var tr in live.Trains)
                    {
                        if (tr.body == null || tr.body.Length < 1) continue;
                        var col = tr.player ? Occ : tr.ai ? RoomAi.Color : AiCol;
                        var pts = tr.body.Select(P).ToArray();
                        var mid = pts[pts.Length / 2];
                        // y una línea fina encima de su vía, para ver dónde está dentro del cantón
                        using (var pen = new Pen(Color.FromArgb(235, col), Math.Max(1.5f, lw - 2.5f)) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                            if (pts.Length >= 2) g.DrawLines(pen, pts);
                        DrawHeadArrow(g, pts, col, lw);   // (1.2.81) hacia dónde va
                        var sz = g.MeasureString(tr.name, f);
                        var box = new RectangleF(mid.X - sz.Width / 2 - 3, mid.Y - 12 - sz.Height, sz.Width + 6, sz.Height);
                        using (var bb = new SolidBrush(Color.FromArgb(235, 26, 31, 38))) g.FillRectangle(bb, box);
                        using (var bp = new Pen(col, 1f)) g.DrawRectangle(bp, box.X, box.Y, box.Width, box.Height);
                        using (var tb = new SolidBrush(col)) g.DrawString(tr.name, f, tb, box.X + 3, box.Y);
                        if (!string.IsNullOrEmpty(tr.sub))
                        {
                            var s2 = g.MeasureString(tr.sub, fs);
                            using var sb = new SolidBrush(Txt); g.DrawString(tr.sub, fs, sb, mid.X - s2.Width / 2, mid.Y + 7);
                        }
                    }

            Prof?.Invoke("salidas");
            // salidas (1.2.75): todas las de un lado en la misma columna, con una línea de puntos hasta ella, una flecha y
            // la estación a la que se va. (1.2.81) La columna, pasadas también las señales y los números de andén (una señal
            // al final de la vía quedaba debajo del nombre de la estación)
            float colR = P(new PointF(s.Bounds.Right, 0)).X, colL = P(new PointF(s.Bounds.Left, 0)).X;
            foreach (var r in boxes.Select(b => b.box).Concat(tagBoxes)) { colR = Math.Max(colR, r.Right); colL = Math.Min(colL, r.Left); }
            colR += 14; colL -= 14;
            _exitCols.AddOrUpdate(s, new[] { (colL - origin.X) / zoom, (colR - origin.X) / zoom });
            using (var f = Theme.Font(Math.Max(6.5f, Math.Min(8.4f, Schematic.Row * zoom * 0.5f)), FontStyle.Bold))   // (que no se pisen con las filas juntas)
            using (var tb = new SolidBrush(Txt))
            using (var dp = new Pen(Dim, 1.2f) { DashStyle = DashStyle.Dot })
            using (var ab = new SolidBrush(Color.FromArgb(170, 182, 196)))
                foreach (bool right in new[] { false, true })
                {
                    float col = right ? colR : colL;
                    foreach (var x in s.Exits.Where(q => q.Right == right && VisP(q.At)))
                    {
                        var q = P(x.At);
                        if (s.Parts != null) col = q.X + (right ? 8 : -8);   // (2026-11-10, ruta entera) cada salida junto a su vía: el borde del esquema está a kilómetros
                        if (Math.Abs(col - q.X) > 3) g.DrawLine(dp, q.X, q.Y, col, q.Y);
                        float d = right ? 1 : -1, ah = 5f;
                        g.FillPolygon(ab, new[] { new PointF(col, q.Y - ah), new PointF(col + d * 7, q.Y), new PointF(col, q.Y + ah) });
                        string t = x.Station ?? "";
                        var sz = g.MeasureString(t, f);
                        float tx = right ? col + 10 : col - 10 - sz.Width;
                        if (s.Parts != null) using (var eb = new SolidBrush(Color.FromArgb(225, Bg))) g.FillRectangle(eb, tx - 2, q.Y - sz.Height / 2 + 1, sz.Width + 4, sz.Height - 2);   // (ruta entera: junto a su vía puede caer sobre otra; que se lea)
                        g.DrawString(t, f, tb, tx, q.Y - sz.Height / 2);
                    }
                }
            // (2026-11-10, petición) los rótulos pequeños junto a la vía, sin pisar vías, señales, andenes ni otros rótulos (debajo; si
            // no cabe, encima; si tampoco, no sale: más cerca se ve): primero los PK (una marquita en la vía y «PK 12»; el mismo PK en
            // las vías de al lado, una vez), luego el nombre de los apartaderos. De lejos no: serían una mancha
            bool pkOn = ShowPk && s.KmPosts.Count > 0 && zoom >= 0.12f, sidOn = s.SidingLabels.Count > 0 && zoom >= 0.25f;
            if (pkOn || sidOn)
            {
                var taken = new List<RectangleF>(numBoxes);
                taken.AddRange(tagBoxes); taken.AddRange(boxes.Select(b => b.box));
                foreach (var pf in s.Platforms) { var a0 = P(new PointF(pf.Bar.Left, pf.Bar.Top)); var a1 = P(new PointF(pf.Bar.Right, pf.Bar.Bottom)); taken.Add(RectangleF.FromLTRB(a0.X, a0.Y - 2, a1.X, a1.Y + 2)); }
                // (ni encima de otra vía: con las vías juntas tapaba la de al lado)
                var tsegs = new List<(PointF a, PointF b)>();
                foreach (var ln in lines) for (int k = 1; k < ln.Length; k++) tsegs.Add((ln[k - 1], ln[k]));
                foreach (var l in s.Links) if (VisA(l)) for (int k = 1; k < l.Length; k++) tsegs.Add((P(l[k - 1]), P(l[k])));
                bool Free(RectangleF bx) => !taken.Any(o => o.IntersectsWith(bx)) && !tsegs.Any(sg => SegHitsRect(sg.a, sg.b, bx));
                using var sbg = new SolidBrush(Color.FromArgb(200, Bg));
                if (pkOn)
                {
                    using var kf = Theme.Font(Math.Max(6.6f, Math.Min(8.4f, 6.4f * zoom + 2.6f)), FontStyle.Bold);
                    using var kb = new SolidBrush(PkCol);
                    using var kpen = new Pen(PkCol, 1.3f);
                    var done = new List<(double v, PointF q)>();
                    var ci = I18n.Culture ?? System.Globalization.CultureInfo.CurrentCulture;
                    foreach (var kp in s.KmPosts.OrderBy(k => k.At.Y))
                    {
                        if (!VisP(kp.At)) continue;
                        var q = P(kp.At);
                        if (done.Any(d => Math.Abs(d.v - kp.V) < 0.05 && Math.Abs(d.q.X - q.X) < 300 && Math.Abs(d.q.Y - q.Y) < 160)) continue;   // (el mismo, en la vía de al lado)
                        string t = "PK " + kp.V.ToString(Math.Abs(kp.V - Math.Round(kp.V)) < 0.05 ? "0" : "0.#", ci);
                        var sz = g.MeasureString(t, kf);
                        float tk = lw / 2 + 3;
                        var below = new RectangleF(q.X - sz.Width / 2, q.Y + tk + 1, sz.Width, sz.Height - 2);
                        var above = new RectangleF(q.X - sz.Width / 2, q.Y - tk - 1 - (sz.Height - 2), sz.Width, sz.Height - 2);
                        RectangleF? box = Free(below) ? below : Free(above) ? above : null;
                        if (box is not RectangleF bx) continue;
                        taken.Add(bx); done.Add((kp.V, q));
                        g.DrawLine(kpen, q.X, q.Y - tk, q.X, q.Y + tk);
                        g.FillRectangle(sbg, bx); g.DrawString(t, kf, kb, bx.X, bx.Y - 1);
                    }
                }
                if (sidOn)
                {
                    using var sf = Theme.Font(Math.Max(6.4f, Math.Min(8.2f, 6.2f * zoom + 2.4f)));
                    using var stb = new SolidBrush(SidingCol);
                    foreach (var sl in s.SidingLabels)
                    {
                        if (!VisP(sl.At)) continue;
                        var q = P(sl.At); var sz = g.MeasureString(sl.Name, sf);
                        var below = new RectangleF(q.X - sz.Width / 2, q.Y + lw / 2 + 2, sz.Width, sz.Height - 2);
                        var above = new RectangleF(q.X - sz.Width / 2, q.Y - lw / 2 - 2 - (sz.Height - 2), sz.Width, sz.Height - 2);
                        RectangleF? box = Free(below) ? below : Free(above) ? above : null;
                        if (box is not RectangleF bx) continue;
                        taken.Add(bx);
                        g.FillRectangle(sbg, bx); g.DrawString(sl.Name, sf, stb, bx.X, bx.Y - 1);
                    }
                }
            }
            Prof?.Invoke("rotulos");
            // (2026-11-09) en una banda: el nombre de cada estación encima de sus andenes
            if (s.IsBand)
            {
                using var f = Theme.Font(10.5f, FontStyle.Bold);
                using var sb = new SolidBrush(StationCol);
                using var fr = new Pen(Color.FromArgb(120, StationCol), 1.2f);
                using var bb = new SolidBrush(Color.FromArgb(235, 18, 21, 26));
                var taken = new List<RectangleF>();
                // (esquema de la ruta) un rótulo por estación y banda; los de la banda elegida, primero; los de las demás, tenues y
                // solo si caben
                var visTracks = s.Tracks.Where(t => t.Pts != null && VisA(t.Pts)).ToList();
                List<(PointF a, PointF b)> segsScr = null;
                if (s.Parts != null)
                {
                    segsScr = new List<(PointF a, PointF b)>();
                    foreach (var tk in visTracks) { if (s.HiddenEdges.Contains(tk.Edge)) continue; for (int k = 1; k < tk.Pts.Length; k++) segsScr.Add((P(tk.Pts[k - 1]), P(tk.Pts[k]))); }
                    foreach (var l in s.Links) if (VisA(l)) for (int k = 1; k < l.Length; k++) segsScr.Add((P(l[k - 1]), P(l[k])));
                    var clipR = RectangleF.Inflate(g.VisibleClipBounds, 200, 200);
                    segsScr.RemoveAll(sg => !clipR.Contains(sg.a) && !clipR.Contains(sg.b) && !SegHitsRect(sg.a, sg.b, clipR));
                }
                var focusB = s.Focus?.Bounds;
                bool InFocus(RectangleF pb) => s.FocusSet != null ? s.FocusSet.Any(p => p.Bounds == pb) : focusB == null || pb == focusB.Value;
                var groups = s.Platforms.Where(p => !string.IsNullOrEmpty(p.Station) && vis.IntersectsWith(RectangleF.Inflate(p.Bar, 600 / zoom, 600 / zoom)))
                              .GroupBy(p => (p.Station, part: s.PartBounds(new PointF(p.Bar.Left, p.Bar.Top))))
                              .OrderBy(q => InFocus(q.Key.part) ? 0 : 1).ToList();
                using var sbDim = new SolidBrush(Color.FromArgb(110, StationCol));
                using var frDim = new Pen(Color.FromArgb(45, StationCol), 1.2f);
                foreach (var grp in groups)
                {
                    float x0 = grp.Min(p => p.Bar.Left), x1 = grp.Max(p => p.Bar.Right), top = grp.Min(p => p.Bar.Top);
                    var pb = grp.Key.part;   // (en el esquema de la ruta: solo las vías de su banda)
                    bool focus = InFocus(pb);
                    float yTop = visTracks.Count > 0 ? Math.Min(top, visTracks.Where(t => t.Pts.Any(q => q.X >= x0 && q.X <= x1)).SelectMany(t => t.Pts).Select(q => q.Y).Where(y => y >= top - 10 * Schematic.Row && y >= pb.Top - 1).DefaultIfEmpty(top).Min()) : top;
                    var a = P(new PointF((x0 + x1) / 2, yTop));
                    string t = grp.Key.Station.ToUpperInvariant();
                    var sz = g.MeasureString(t, f);
                    var box = new RectangleF(a.X - sz.Width / 2 - 8, a.Y - 34 - sz.Height, sz.Width + 16, sz.Height + 6);
                    // (2026-11-10, ruta por estaciones) encima de las vías de otras piezas que pasan por encima, no entre ellas
                    // (si no hay hueco cerca, donde estaba: lejos parecería de otra estación)
                    if (segsScr != null && segsScr.Any(sg => SegHitsRect(sg.a, sg.b, box)))
                    {
                        float step = Math.Max(8f, Schematic.Row * zoom), y0 = box.Y;
                        int k = 1; for (; k <= 2; k++) { box.Y = y0 - k * step; if (!segsScr.Any(sg => SegHitsRect(sg.a, sg.b, box))) break; }
                        if (k > 2)
                        {
                            // (2026-11-10) debajo de sus vías (una línea que va por debajo de otra: encima quedaba sobre la otra)
                            float bot = grp.Max(p => p.Bar.Bottom);
                            float yBot = visTracks.Where(t => t.Pts.Any(q => q.X >= x0 && q.X <= x1)).SelectMany(t => t.Pts).Select(q => q.Y).Where(y => y <= bot + 10 * Schematic.Row && y <= pb.Bottom + 1).DefaultIfEmpty(bot).Max();
                            var below = new RectangleF(box.X, P(new PointF(0, Math.Max(bot, yBot))).Y + 18, box.Width, box.Height);
                            if (!segsScr.Any(sg => SegHitsRect(sg.a, sg.b, below))) box = below;
                            else
                            {
                                for (k = 3; k <= 4; k++) { box.Y = y0 - k * step; if (!segsScr.Any(sg => SegHitsRect(sg.a, sg.b, box))) break; }
                                if (k > 4) box.Y = y0;
                            }
                        }
                    }
                    if (focus) for (int k = 0; k < 4 && taken.Any(o => o.IntersectsWith(box)); k++) box.Y -= box.Height + 4;
                    else if (taken.Any(o => o.IntersectsWith(box))) continue;
                    taken.Add(box);
                    g.FillRectangle(bb, box); g.DrawRectangle(focus ? fr : frDim, box.X, box.Y, box.Width, box.Height);
                    g.DrawString(t, f, focus ? sb : sbDim, box.X + 8, box.Y + 3);
                }
                return;
            }
            // el nombre de la dependencia, centrado encima, en su recuadro (como el rótulo de un esquema de ADIF)
            using (var f = Theme.Font(12f, FontStyle.Bold))
            using (var sb = new SolidBrush(StationCol))
            using (var fr = new Pen(Color.FromArgb(120, StationCol), 1.2f))
            using (var bb = new SolidBrush(Color.FromArgb(235, 18, 21, 26)))
            {
                var l = P(new PointF(s.Bounds.Left, s.Bounds.Top)); var r = P(new PointF(s.Bounds.Right, s.Bounds.Top));
                string t = s.Station.ToUpperInvariant();
                var sz = g.MeasureString(t, f);
                var box = new RectangleF((l.X + r.X) / 2 - sz.Width / 2 - 10, l.Y - 58, sz.Width + 20, sz.Height + 6);
                g.FillRectangle(bb, box); g.DrawRectangle(fr, box.X, box.Y, box.Width, box.Height);
                g.DrawString(t, f, sb, box.X + 10, box.Y + 3);
            }
        }

        // (2026-11-10) el rótulo de un paso a nivel: «PN · PK 12,345» (el PK de la primera de sus vías que lo tenga)
        public static string PnLabel(IEnumerable<PasosNivel.Cr> crs)
        {
            string pk = null;
            foreach (var c in crs) { if (c.PkDone && !double.IsNaN(c.Pk)) { pk = PuntoKm.Text(c.Pk); break; } }
            return pk == null ? I18n.T("PN") : I18n.T("PN") + " · PK " + pk;
        }

        // ¿el segmento a–b pasa por el rectángulo r?
        static bool SegHitsRect(PointF a, PointF b, RectangleF r)
        {
            if (r.Contains(a) || r.Contains(b)) return true;
            if (Math.Max(a.X, b.X) < r.Left || Math.Min(a.X, b.X) > r.Right || Math.Max(a.Y, b.Y) < r.Top || Math.Min(a.Y, b.Y) > r.Bottom) return false;
            static bool X(PointF p1, PointF p2, PointF p3, PointF p4)
            {
                float d = (p2.X - p1.X) * (p4.Y - p3.Y) - (p2.Y - p1.Y) * (p4.X - p3.X);
                if (Math.Abs(d) < 1e-6) return false;
                float t = ((p3.X - p1.X) * (p4.Y - p3.Y) - (p3.Y - p1.Y) * (p4.X - p3.X)) / d, u = ((p3.X - p1.X) * (p2.Y - p1.Y) - (p3.Y - p1.Y) * (p2.X - p1.X)) / d;
                return t >= 0 && t <= 1 && u >= 0 && u <= 1;
            }
            PointF tl = new(r.Left, r.Top), tr = new(r.Right, r.Top), bl = new(r.Left, r.Bottom), br = new(r.Right, r.Bottom);
            return X(a, b, tl, tr) || X(a, b, bl, br) || X(a, b, tl, bl) || X(a, b, tr, br);
        }

        // rótulo de salida bajo el ratón (para saltar a esa estación)
        public static Schematic.Exit ExitAt(Schematic s, PointF origin, float zoom, Point mouse, Graphics g)
        {
            if (s == null) return null;
            using var f = Theme.Font(Math.Max(6.5f, Math.Min(8.4f, Schematic.Row * zoom * 0.5f)), FontStyle.Bold);
            foreach (var x in s.Exits)
            {
                if (string.IsNullOrEmpty(x.Station)) continue;   // (sin estación: no se puede ir)
                var q = new PointF(origin.X + x.At.X * zoom, origin.Y + x.At.Y * zoom);
                float col = _exitCols.TryGetValue(s, out var ec) ? origin.X + ec[x.Right ? 1 : 0] * zoom
                    : x.Right ? origin.X + s.Bounds.Right * zoom + 14 : origin.X + s.Bounds.Left * zoom - 14;
                if (s.Parts != null) col = q.X + (x.Right ? 8 : -8);   // (ruta entera: junto a su vía)
                var sz = g.MeasureString(x.Station ?? "", f);
                var r = new RectangleF(x.Right ? col : col - 10 - sz.Width, q.Y - sz.Height / 2, sz.Width + 10, sz.Height);
                if (r.Contains(mouse)) return x;
            }
            return null;
        }

        // (1.2.81) dónde va cada señal: lado de la vía (Side ±1), largo del mástil, hacia dónde sale la figura (Dir ±1) y largo
        // del brazo. Primero la de siempre (del lado al que mira, figura hacia delante); si pisa algo, la alternativa que menos
        // pise y menos se aparte de lo normal. Se recuerda para el mismo zoom (al mover el esquema no cambia nada).
        // (2026-11-10, petición) G: en un PÓRTICO (varias señales juntas de vías vecinas, mismo sentido): cuelga de una viga
        // vertical que cruza las vías en Gx (respecto del pie, en píxeles) de B0 a B1 (respecto de la vía de la señal); el brazo
        // sale de la viga. Fo: lo que se aparta el pie hacia donde mira (más si tiene otra señal de espaldas en el mismo punto).
        public struct SigPlace { public int Side, Dir; public float Mast, Arm; public bool G; public float Gx, B0, B1, Fo; }
        static float FootOff(float sk) => Math.Max(2.5f, 1.8f * sk);   // (1.2.84) el pie del mástil, apartado hacia donde mira la señal
        // (2026-11-10) la viga de un pórtico: de y0 a y1 en x, de celosía (dos largueros y diagonales), con sus dos pies
        static void DrawGantry(Graphics g, float x, float y0, float y1, float sk)
        {
            float k = Math.Min(2f, sk), h = 1.5f * k, f = 3.6f * k;
            using var bp = new Pen(Color.FromArgb(175, 184, 194), Math.Max(1f, 0.9f * k));
            g.DrawLine(bp, x - h, y0, x - h, y1); g.DrawLine(bp, x + h, y0, x + h, y1);
            using (var lp = new Pen(Color.FromArgb(120, 175, 184, 194), Math.Max(0.8f, 0.6f * k)))
                for (float y = y0, st = 3 * h; y + st <= y1; y += st) g.DrawLine(lp, x - h, y, x + h, y + st);
            using var fp = new Pen(Color.FromArgb(175, 184, 194), Math.Max(1.2f, 1.2f * k));
            g.DrawLine(fp, x - f, y0, x + f, y0); g.DrawLine(fp, x - f, y1, x + f, y1);
        }
        sealed class PlaceCache { public float Zoom; public string Key; public Dictionary<Schematic.Signal, SigPlace> Map; }
        static readonly ConditionalWeakTable<Schematic, PlaceCache> _places = new();
        static readonly ConditionalWeakTable<Schematic, float[]> _exitCols = new();   // columnas de las salidas (izquierda, derecha), en metros del esquema

        // (1.2.81) las cuatro esquinas del símbolo de una tijera en pantalla (arriba izq, arriba dcha, abajo izq, abajo dcha): cada
        // lado, en la fila de su pareja de agujas y centrado en ella, del mismo ancho los dos (lo que haya entre sus agujas, como
        // mucho 1,7 filas: no se deforma al acercar). Si las dos parejas no están una encima de otra, un paralelogramo: así cada
        // aguja queda siempre fuera, unida por su vía a su esquina (con un rectángulo centrado en las cuatro, en Ingeniero Pizias
        // la vía de la playa entraba por la mitad del lado de abajo)
        public static PointF[] ScissorCorners(Schematic.ScissorGroup tg, PointF origin, float zoom)
        {
            PointF Pp(PointF a) => new PointF(origin.X + a.X * zoom, origin.Y + a.Y * zoom);
            PointF tl = Pp(tg.At[0]), tr = Pp(tg.At[1]), bl = Pp(tg.At[2]), br = Pp(tg.At[3]);
            float spanT = Math.Abs(tr.X - tl.X), spanB = Math.Abs(br.X - bl.X);
            float w = Math.Max(22, Math.Min(1.7f * Schematic.Row * zoom, Math.Min(spanT, spanB)));
            float ct = (tl.X + tr.X) / 2, cb = (bl.X + br.X) / 2, yt = (tl.Y + tr.Y) / 2, yb = (bl.Y + br.Y) / 2;
            if (Math.Abs(yb - yt) < 8) { float m = (yt + yb) / 2; yt = m - 4; yb = m + 4; }
            return new[] { new PointF(ct - w / 2, yt), new PointF(ct + w / 2, yt), new PointF(cb - w / 2, yb), new PointF(cb + w / 2, yb) };
        }
        // lo que ocupa (para el clic y para que nada se ponga encima)
        public static RectangleF ScissorBox(Schematic.ScissorGroup tg, PointF origin, float zoom)
        {
            var c = ScissorCorners(tg, origin, zoom);
            return RectangleF.FromLTRB(c.Min(p => p.X), c.Min(p => p.Y), c.Max(p => p.X), c.Max(p => p.Y));
        }
        static float SegDist(PointF p, PointF a, PointF b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
            float t = l2 < 1e-6f ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2));
            float x = a.X + dx * t - p.X, y = a.Y + dy * t - p.Y; return (float)Math.Sqrt(x * x + y * y);
        }
        // ¿corta el segmento a-b al rectángulo? (Liang-Barsky)
        static bool SegHits(RectangleF r, PointF a, PointF b)
        {
            if (Math.Max(a.X, b.X) < r.Left || Math.Min(a.X, b.X) > r.Right || Math.Max(a.Y, b.Y) < r.Top || Math.Min(a.Y, b.Y) > r.Bottom) return false;
            float t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
            bool Clip(float p, float q)
            {
                if (Math.Abs(p) < 1e-6f) return q >= 0;
                float t = q / p;
                if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
                else { if (t < t0) return false; if (t < t1) t1 = t; }
                return true;
            }
            return Clip(-dx, a.X - r.Left) && Clip(dx, r.Right - a.X) && Clip(-dy, a.Y - r.Top) && Clip(dy, r.Bottom - a.Y);
        }

        static Dictionary<Schematic.Signal, SigPlace> PlaceSignals(Graphics g, Schematic s, List<Schematic.Signal> sigs, SchematicLive live, Func<PointF, PointF> P, float zoom,
            float sk, float lr0, float lw, float idSize, float papSize, List<PointF[]> lines, List<RectangleF> nums, List<RectangleF> plats)
        {
            bool compact = zoom < 0.7f;
            // lo que ocupa la figura de cada una (en compacto, lo de dos luces: si no, cambiaría de sitio con cada aspecto)
            var size = new Dictionary<Schematic.Signal, (float along, float across, float extra)>();
            var key = new System.Text.StringBuilder();
            using (var pf = Theme.Font(papSize, FontStyle.Bold))
            using (var fi = Theme.Font(idSize))
                foreach (var sg in sigs)
                {
                    var fig = live?.Figure?.Invoke(sg.Sig);
                    float along, across, extra = 0;
                    if (fig != null)
                    {
                        float step = lr0 * 2.3f, pad = lr0 * 0.6f;
                        var fs = RoomMapDraw.FigureSize(fig, lr0, false);
                        if (compact) { along = step + 2 * lr0 + 2 * pad; across = 2 * lr0 + 2 * pad; }
                        else { along = fs.Height; across = fs.Width; }
                        if (fig.Pap != null) { extra += 2 * sk + g.MeasureString(fig.Pap, pf).Width + 1; key.Append(fig.Pap); }
                        key.Append(compact ? 'c' : (char)('a' + Math.Min(20, (int)(fs.Width / Math.Max(1, lr0)))));
                    }
                    else { float r = 3.2f * sk; along = 2 * r; across = 2 * r; key.Append('o'); }
                    key.Append(sg.Side < 0 ? '-' : sg.Side > 0 ? '+' : '0');   // (2026-11-06: el lado llega al leer el mundo)
                    if (zoom >= 1.0f && sg.Sig?.Obj >= 0) extra += 2 * sk + g.MeasureString(sg.Sig.Obj.ToString(System.Globalization.CultureInfo.InvariantCulture), fi).Width;
                    size[sg] = (along, across, extra);
                }
            key.Append('#').Append(sigs.Count).Append(':').Append(sigs.Sum(q => (long)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(q)));   // (solo las que se ven: otra vista, otra colocación)
            string k = key.ToString();
            if (_places.TryGetValue(s, out var pc) && Math.Abs(pc.Zoom - zoom) < 1e-4f && pc.Key == k) return pc.Map;
            // (2026-11-10) de lejos, cada una en su sitio de siempre, sin buscarle hueco: las figuras son de un par de píxeles y
            // buscarlo para cientos de señales a cada paso de la rueda hacía ir el puesto a tirones
            if (zoom < 0.3f && sigs.Count > 40)
            {
                var quick = new Dictionary<Schematic.Signal, SigPlace>();
                foreach (var sg in sigs)
                {
                    int side0 = sg.Side != 0 ? sg.Side : sg.Face < 0 ? -1 : 1; var (_, across, _) = size[sg];
                    quick[sg] = new SigPlace { Side = side0, Dir = sg.Face, Mast = Math.Min(8f * sk, Math.Max(lw / 2 + across / 2 + 2f, 4f)), Arm = 4.5f * sk };
                }
                _places.AddOrUpdate(s, new PlaceCache { Zoom = zoom, Key = k, Map = quick });
                return quick;
            }

            // todo, relativo al primer punto (así vale al mover el esquema)
            var o = P(new PointF(0, 0));
            PointF Rel(PointF p) => new PointF(p.X - o.X, p.Y - o.Y);
            RectangleF RelR(RectangleF r) => new RectangleF(r.X - o.X, r.Y - o.Y, r.Width, r.Height);
            var segs = new List<(PointF a, PointF b)>();
            foreach (var l in lines) for (int i = 1; i < l.Length; i++) segs.Add((Rel(l[i - 1]), Rel(l[i])));
            var numsR = nums.Select(RelR).ToList(); var platsR = plats.Select(RelR).ToList();
            // (2026-11-10) el símbolo de cada tijera (una señal encima no se lee ni se sabe de qué vía es) y las vigas de los pórticos
            foreach (var tg in s.ScissorGroups) numsR.Add(RelR(RectangleF.Inflate(ScissorBox(tg, P(PointF.Empty), zoom), 1, 1)));
            var beamsR = new List<RectangleF>();
            // (2026-11-10) dos señales de espaldas en el mismo punto y del mismo lado: los pies más apartados (si no, parecía un
            // mástil con dos brazos)
            var feet = sigs.ToDictionary(sg => sg, sg => Rel(P(sg.At)));
            int SideOf(Schematic.Signal sg) => sg.Side != 0 ? sg.Side : sg.Face < 0 ? -1 : 1;
            var backToBack = new Dictionary<Schematic.Signal, float>();   // (su Fo)
            {
                var byX = sigs.OrderBy(q => feet[q].X).ToList();
                for (int i = 0; i < byX.Count; i++)
                    for (int j = i + 1; j < byX.Count && feet[byX[j]].X - feet[byX[i]].X < 45f; j++)
                    {
                        var a = byX[i]; var b = byX[j];
                        // (de espaldas, a menos de 12 px; de frente —la de la izquierda mira a la derecha—, mientras sus figuras se
                        // pisarían: hasta 45 px)
                        float gap = feet[b].X - feet[a].X; bool facing = a.Face > 0 && b.Face < 0;
                        if (a.Face != b.Face && SideOf(a) == SideOf(b) && Math.Abs(feet[a].Y - feet[b].Y) < 2 && (gap < 12 || facing))
                        {
                            // las dos desde su punto medio, cada una hacia donde mira (si estaban «de frente», una figura encima de la
                            // otra): pie = medio + mira·F2 → Fo = mira·(medio − pie) + F2
                            float mid = (feet[a].X + feet[b].X) / 2, f2 = FootOff(sk) + Math.Max(3f, 2.2f * sk);
                            foreach (var x in new[] { a, b }) { float fo = x.Face * (mid - feet[x].X) + f2; backToBack[x] = Math.Abs(fo) < 0.01f ? 0.01f : fo; }
                        }
                    }
            }

            float m0 = 8f * sk, step2 = Math.Max(8f * sk, 3.3f * lr0 + 1), arm0 = 4.5f * sk;
            // la figura, el mástil (vertical) y el brazo (horizontal), por separado: juntos, su recuadro tapaba mucho más
            (RectangleF fig, RectangleF mast, RectangleF arm) Shape(Schematic.Signal sg, SigPlace p)
            {
                var q = Rel(P(sg.At)); var (along, across, extra) = size[sg];
                q = p.G ? new PointF(q.X + p.Gx, q.Y) : new PointF(q.X + p.Dir * (p.Fo != 0 ? p.Fo : FootOff(sk)), q.Y);   // (1.2.84: el pie, un poco hacia donde mira; 2026-11-10: o la viga)
                float ly = q.Y + p.Side * p.Mast, x0 = q.X + p.Dir * p.Arm, x1 = x0 + p.Dir * (along + extra);
                var fig = RectangleF.FromLTRB(Math.Min(x0, x1), ly - across / 2, Math.Max(x0, x1), ly + across / 2);
                float m1 = q.Y + p.Side * (lw / 2 + 1.5f);
                var mast = RectangleF.FromLTRB(q.X - 0.6f, Math.Min(m1, ly) - 0.6f, q.X + 0.6f, Math.Max(m1, ly) + 0.6f);
                var arm = RectangleF.FromLTRB(Math.Min(q.X, x0) - 0.6f, ly - 0.6f, Math.Max(q.X, x0) + 0.6f, ly + 0.6f);
                return (fig, mast, arm);
            }
            var placed = new List<(RectangleF fig, RectangleF mast, RectangleF arm)>();
            float Cost(Schematic.Signal sg, SigPlace p, float pen)
            {
                var (fig, mast, arm) = Shape(sg, p);
                var fi = RectangleF.Inflate(fig, 1.5f, 1.5f);
                float c = pen;
                foreach (var (f2, m2, a2) in placed)
                {
                    if (fi.IntersectsWith(f2)) c += 1000;
                    if (mast.IntersectsWith(f2) || arm.IntersectsWith(f2) || fig.IntersectsWith(m2) || fig.IntersectsWith(a2)) c += 400;
                    else if (RectangleF.Inflate(mast, 2.5f, 2.5f).IntersectsWith(m2) || RectangleF.Inflate(arm, 2.5f, 2.5f).IntersectsWith(a2)) c += 400;   // (dos mástiles que se tocan: no se sabe cuál es de cada vía)
                }
                foreach (var r in numsR) { if (fi.IntersectsWith(r)) c += 300; else if (mast.IntersectsWith(r) || arm.IntersectsWith(r)) c += 80; }
                foreach (var r in platsR) if (fig.IntersectsWith(r)) c += 15;
                if (!p.G) foreach (var r in beamsR) if (fi.IntersectsWith(r) || mast.IntersectsWith(r)) c += 600;   // (la viga de un pórtico)
                var fs = RectangleF.Inflate(fig, 0.5f, 0.5f); var fn = RectangleF.Inflate(fig, 3.5f, 3.5f); var foot = Rel(P(sg.At));
                // la propia vía: lo que pasa junto al pie de la señal o va horizontal a su altura (la vía se corta en cada señal)
                bool Own(PointF a, PointF b) => SegDist(foot, a, b) <= 6f || Math.Abs(a.Y - foot.Y) < 1.5f && Math.Abs(b.Y - foot.Y) < 1.5f;
                int hit = 0, mhit = 0, near = 0; bool closer = false;
                var fc = new PointF(fig.X + fig.Width / 2, fig.Y + fig.Height / 2); float own = p.Mast;
                foreach (var (a, b) in segs)
                {
                    if (!closer && !Own(a, b) && SegDist(fc, a, b) < own + 3) closer = true;   // (más cerca de otra vía que de la suya)
                    if (hit < 3 && SegHits(fs, a, b)) hit++;
                    else if (near < 2 && SegHits(fn, a, b) && !Own(a, b)) near++;   // (pegada a otra vía, no a la suya: parece de esa vía)
                    if (mhit < 3 && (SegHits(mast, a, b) || SegHits(arm, a, b) && !Own(a, b))) mhit++;
                }
                // (1.2.84) «pegada a otra vía» y «más cerca de otra vía que de la suya» ya no cuentan: con el lado y el mástil fijos
                // no se pueden evitar (vías juntas), y lo único que conseguían era mandar la figura lejos, donde la otra vía acaba
                return c + 200 * hit + 150 * mhit;
            }
            // las opciones: (lado, mástil, figura, brazo) y lo que cuesta apartarse de lo normal
            List<(SigPlace p, float pen)> Options(Schematic.Signal sg)
            {
                // (1.2.84, elegido por el usuario: «figura en escalera») siempre de su lado de la vía, con el mástil de siempre y
                // la figura hacia donde mira: si pisa a otra, solo se adelanta a lo largo de la vía (las de vías vecinas quedan
                // escalonadas). Antes cambiaba de lado, alargaba el mástil o le daba la vuelta, y con varias juntas no se sabía
                // de qué vía era cada una ni hacia dónde miraba.
                // El mástil, corto: la figura pegada a su vía (con el de antes, la señal → de una vía y la ← de la de al lado se
                // encontraban a media altura en el mismo punto y parecían un solo mástil con dos brazos).
                // (2026-11-06) del lado en que está de verdad; si no se sabe (encima de la vía), a la derecha de la marcha
                int side0 = sg.Side != 0 ? sg.Side : sg.Face < 0 ? -1 : 1, dir0 = sg.Face; var (along, across, extra) = size[sg];
                float mast = Math.Min(m0, Math.Max(lw / 2 + across / 2 + 2f, 4f));
                float stepA = Math.Max(4f, (along + extra + 4 * sk) / 2);
                var l = new List<(SigPlace, float)>();
                float fo = backToBack.TryGetValue(sg, out float bfo) ? bfo : FootOff(sk);
                for (int k = 0; k < 6; k++)
                    l.Add((new SigPlace { Side = side0, Dir = dir0, Mast = mast, Arm = arm0 + k * stepA, Fo = fo }, k * (8 + k)));
                return l;
            }
            var map = new Dictionary<Schematic.Signal, SigPlace>();
            var pens = new Dictionary<Schematic.Signal, float>(); var idx = new Dictionary<Schematic.Signal, int>();
            // (2026-11-10, petición) PÓRTICOS: tres o más señales del mismo sentido en vías vecinas, casi en el mismo punto (sus
            // figuras se escalonarían o se pisarían): todas colgadas de una viga que cruza las vías, cada una junto a su vía
            var gantry = new Dictionary<Schematic.Signal, SigPlace>();
            if (zoom >= 0.7f)
            {
                float rowPx = Schematic.Row * zoom;
                var grpOf = sigs.ToDictionary(q => q, q => q);
                Schematic.Signal Find(Schematic.Signal x) { while (grpOf[x] != x) x = grpOf[x] = grpOf[grpOf[x]]; return x; }
                var byX = sigs.Where(q => !backToBack.ContainsKey(q)).OrderBy(q => feet[q].X).ToList();
                for (int i = 0; i < byX.Count; i++)
                    for (int j = i + 1; j < byX.Count; j++)
                    {
                        var a = byX[i]; var b = byX[j];
                        float dx = feet[b].X - feet[a].X;
                        float reach = Math.Max(14f * sk, 0.75f * Math.Min(size[a].along + size[a].extra, size[b].along + size[b].extra));
                        if (dx > reach) break;
                        float dy = Math.Abs(feet[a].Y - feet[b].Y);
                        if (a.Face != b.Face || dy < 0.6f * rowPx || dy > 1.6f * rowPx) continue;
                        grpOf[Find(a)] = Find(b);
                    }
                foreach (var grp in byX.GroupBy(Find).Select(q => q.ToList()).Where(q => q.Count >= 3))
                {
                    // una por vía (fila)
                    var mem = grp.GroupBy(q => (int)Math.Round(feet[q].Y)).Select(q => q.OrderBy(x => Math.Abs(feet[x].X - grp.Average(y => feet[y].X))).First()).ToList();
                    for (int tries = 0; tries < 3 && mem.Count >= 3; tries++)
                    {
                        int dir = mem[0].Face;
                        // la viga: donde está la primera señal del grupo según la marcha (la figura sale hacia delante: ninguna se
                        // adelanta a donde está)
                        float bx = dir > 0 ? mem.Min(q => feet[q].X) : mem.Max(q => feet[q].X);
                        bx += dir * FootOff(sk);
                        var tops = new List<float>(); var bots = new List<float>();
                        var cand = new Dictionary<Schematic.Signal, SigPlace>();
                        foreach (var sg in mem)
                        {
                            var (along, across, extra) = size[sg];
                            float mast = Math.Min(m0, Math.Max(lw / 2 + across / 2 + 2f, 4f));
                            int sd = SideOf(sg);
                            float cy = sd * mast;
                            tops.Add(feet[sg].Y + cy - across / 2 - 3); bots.Add(feet[sg].Y + cy + across / 2 + 3);
                            cand[sg] = new SigPlace { Side = sd, Dir = dir, Mast = mast, Arm = Math.Max(3f, 2.5f * sk), G = true, Gx = bx - feet[sg].X };
                        }
                        float y0 = Math.Min(tops.Min(), mem.Min(q => feet[q].Y) - lw), y1 = Math.Max(bots.Max(), mem.Max(q => feet[q].Y) + lw);
                        foreach (var sg in mem) { var c = cand[sg]; c.B0 = y0 - feet[sg].Y; c.B1 = y1 - feet[sg].Y; cand[sg] = c; }
                        // ¿cabe? (ninguna figura encima de otra vía, de una tijera o de un número)
                        var bad = mem.Where(sg => Cost(sg, cand[sg], 0) >= 150).ToList();
                        if (bad.Count == 0) { foreach (var kv in cand) gantry[kv.Key] = kv.Value; beamsR.Add(RectangleF.FromLTRB(bx - 2, y0, bx + 2, y1)); break; }
                        mem = mem.Except(bad).ToList();
                    }
                }
            }
            // primero las que no tienen problema en su sitio de siempre; luego las demás, de la más apretada a la menos
            var def = sigs.ToDictionary(sg => sg, sg => gantry.TryGetValue(sg, out var gp) ? gp : Options(sg)[0].p);   // (1.2.84: su sitio de siempre, el primero de las opciones; 2026-11-10: o su pórtico)
            var shapes = sigs.Select(sg => (sg, sh: Shape(sg, def[sg]))).ToList();
            var order = sigs.Select(sg =>
            {
                float c = Cost(sg, def[sg], 0);
                var (f, m, a) = Shape(sg, def[sg]); var fi = RectangleF.Inflate(f, 1.5f, 1.5f);
                foreach (var (o2, sh) in shapes) if (o2 != sg && (fi.IntersectsWith(sh.fig) || m.IntersectsWith(sh.fig) || a.IntersectsWith(sh.fig) || f.IntersectsWith(sh.mast) || f.IntersectsWith(sh.arm))) c += 500;
                return (sg, c);
            }).OrderBy(t => t.c).ThenBy(t => t.sg.At.X).ToList();
            foreach (var (sg, _) in order)
            {
                var best = def[sg]; float bc = Cost(sg, best, 0);
                if (gantry.ContainsKey(sg)) bc = 0;   // (2026-11-10: en su pórtico)
                if (bc > 0)
                    foreach (var (p, pen) in Options(sg))
                    {
                        if (pen >= bc) continue;
                        float c = Cost(sg, p, pen);
                        if (c < bc) { bc = c; best = p; }
                    }
                map[sg] = best; pens[sg] = bc == 0 ? 0 : Options(sg).Where(o => o.p.Equals(best)).Select(o => o.pen).DefaultIfEmpty(0).First();
                idx[sg] = placed.Count; placed.Add(Shape(sg, best));
            }
            // (1.2.81) segunda pasada: cada una, otra vez, sabiendo dónde han quedado las demás (con tres o cuatro señales en el
            // mismo punto de dos vías, la primera cogía el hueco y la última ya no tenía sitio)
            var gone = (new RectangleF(-1e6f, -1e6f, 0, 0), new RectangleF(-1e6f, -1e6f, 0, 0), new RectangleF(-1e6f, -1e6f, 0, 0));
            for (int pass = 0; pass < 2; pass++)
                foreach (var (sg, _) in order)
                {
                    if (gantry.ContainsKey(sg)) continue;   // (2026-11-10: en su pórtico)
                    int ix = idx[sg]; var keep = placed[ix]; placed[ix] = gone;
                    var best = map[sg]; float bc = Cost(sg, best, pens[sg]);
                    if (bc > pens[sg] + 0.5f)
                    {
                        float c0 = Cost(sg, def[sg], 0); if (c0 < bc) { bc = c0; best = def[sg]; }
                        foreach (var (p, pen) in Options(sg))
                        {
                            if (pen >= bc) continue;
                            float c = Cost(sg, p, pen);
                            if (c < bc) { bc = c; best = p; }
                        }
                        if (!best.Equals(map[sg])) { map[sg] = best; pens[sg] = best.Equals(def[sg]) ? 0 : Options(sg).Where(o => o.p.Equals(best)).Select(o => o.pen).DefaultIfEmpty(0).First(); keep = Shape(sg, best); }
                    }
                    placed[ix] = keep;
                }
            _places.AddOrUpdate(s, new PlaceCache { Zoom = zoom, Key = k, Map = map });
            return map;
        }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }

        // una línea (en pantalla) recortada «gap» píxeles por cada punta: el hueco entre dos cantones
        static PointF[] Trim(PointF[] p, float gap)
        {
            float len = 0; for (int i = 1; i < p.Length; i++) len += Dist(p[i - 1], p[i]);
            if (len < 2 * gap + 1) return len >= 1.5f ? p : null;
            var l = new List<PointF>(); float acc = 0, a = gap, b = len - gap;
            l.Add(At(p, a));
            for (int i = 1; i < p.Length; i++) { acc += Dist(p[i - 1], p[i]); if (acc > a && acc < b) l.Add(p[i]); }
            l.Add(At(p, b));
            return l.ToArray();
        }
        static PointF At(PointF[] p, float d)
        {
            for (int i = 1; i < p.Length; i++) { float l = Dist(p[i - 1], p[i]); if (d <= l) return Lerp(p[i - 1], p[i], l > 0 ? d / l : 0); d -= l; }
            return p[^1];
        }
        static float Dist(PointF a, PointF b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        static PointF Lerp(PointF a, PointF b, float t) { t = Math.Max(0, Math.Min(1, t)); return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t); }

        // (1.2.81, elegido por el usuario: «rama puesta en color») La posición de una aguja (en pantalla; «set»: la rama
        // puesta, «other»: la otra; las dos empiezan en la aguja): la puesta, en amarillo un buen trozo (unos 40 px, también de
        // lejos); la otra, cortada junto a la aguja y en gris oscuro después. Posición desconocida: las dos en ámbar a trazos.
        // (Enclavada: el número de la aguja en azul, como antes.)
        public static readonly Color SwitchSetCol = Color.FromArgb(255, 214, 64), SwitchOffCol = Color.FromArgb(52, 58, 66), SwitchUnknownCol = Color.FromArgb(255, 152, 0);
        // (1.2.81, petición) un color por posición: a directa, blanco; a desviada, amarillo
        public static readonly Color SwitchStraightCol = Color.FromArgb(236, 240, 244), SwitchDivergeCol = Color.FromArgb(255, 214, 64);
        // straight: cuál de las dos ramas es la directa (−1: no se sabe → el amarillo de siempre)
        // (1.2.81) el color en vivo de una rama de aguja junto a ella (con un tren, anulándose o con un itinerario), o null
        public static Color? LegLiveCol(SchematicLive live, int e, bool fromA, double len)
        {
            if (live == null || e < 0) return null;
            double a = fromA ? 0 : Math.Max(0, len - 30), b = fromA ? Math.Min(len, 30) : len;
            bool Ov(List<(double a, double b)> l) => l != null && l.Any(q => q.a < b && a < q.b);
            if (live.Occupied.TryGetValue(e, out var o) && Ov(o)) return Busy;
            if (live.Cancelling.TryGetValue(e, out var c) && Ov(c)) return CancelCol;
            if (live.Route.TryGetValue(e, out var r))
            {
                var h = r.Where(q => q.a < b && a < q.b).ToList();
                if (h.Count > 0) return h.All(q => q.callOn) ? CallOnCol : RouteCol;
            }
            return null;
        }
        // setLive: (1.2.81) la rama puesta lleva un itinerario o un tren: se marca de ese color (no en blanco/amarillo, que lo tapaba)
        public static void DrawSwitchState(Graphics g, PointF[] set, PointF[] other, int pos, bool locked, float lw, int straight = -1, Color? setLive = null)
        {
            const float Mark = 40f;
            if (pos != 0 && pos != 1)
            {
                using var up = new Pen(SwitchUnknownCol, lw + 1f) { DashStyle = DashStyle.Dash };
                foreach (var l in new[] { set, other })
                    if (l != null && l.Length >= 2) { var part = Part(l, 0, Math.Min(Len(l) * 0.85f, Mark)); if (part.Length >= 2) g.DrawLines(up, part); }
                return;
            }
            if (other != null && other.Length >= 2)
            {
                float Lo = Len(other), c1 = Math.Min(Lo * 0.3f, lw * 0.9f + 1.5f), c2 = Math.Min(Lo * 0.5f, c1 + Math.Max(8f, lw * 2.5f)), c3 = Math.Min(Lo * 0.85f, Mark);
                using (var cut = new Pen(Bg, lw + 2.6f) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                    if (c2 > c1) g.DrawLine(cut, AtLen(other, c1), AtLen(other, c2));
                var dim = Part(other, c2, c3);
                if (c3 > c2 && dim.Length >= 2) using (var dp = new Pen(SwitchOffCol, lw + 0.6f) { StartCap = LineCap.Flat, EndCap = LineCap.Flat, LineJoin = LineJoin.Round }) g.DrawLines(dp, dim);
            }
            if (set == null || set.Length < 2) return;
            float mark = Math.Min(Len(set) * 0.85f, Mark);
            var seg = Part(set, 0, mark);
            var setCol = setLive ?? (straight < 0 ? SwitchSetCol : pos == straight ? SwitchStraightCol : SwitchDivergeCol);
            if (seg.Length >= 2) using (var sp = new Pen(setCol, setLive != null ? lw : lw + 1.2f) { StartCap = LineCap.Round, EndCap = LineCap.Flat, LineJoin = LineJoin.Round }) g.DrawLines(sp, seg);
        }
        // (1.2.81) Hacia dónde va un tren: una flecha en su cabeza (el cuerpo va de la cola a la cabeza, en el sentido de la marcha)
        public static void DrawHeadArrow(Graphics g, PointF[] pts, Color col, float lw)
        {
            if (pts == null || pts.Length < 2) return;
            var tip = pts[^1];
            // un punto de atrás a unos píxeles, para la dirección (los últimos pueden estar casi encima)
            PointF back = pts[0]; float acc = 0;
            for (int i = pts.Length - 2; i >= 0; i--) { acc += Dist(pts[i], pts[i + 1]); back = pts[i]; if (acc >= 6) break; }
            float dx = tip.X - back.X, dy = tip.Y - back.Y, n = (float)Math.Sqrt(dx * dx + dy * dy);
            if (n < 0.5f) return;
            dx /= n; dy /= n;
            float L = Math.Max(9f, lw * 2.6f), W = Math.Max(5f, lw * 1.5f);
            var a = new PointF(tip.X + dx * L * 0.85f, tip.Y + dy * L * 0.85f);
            var b0 = new PointF(tip.X - dx * L * 0.15f - dy * W, tip.Y - dy * L * 0.15f + dx * W);
            var b1 = new PointF(tip.X - dx * L * 0.15f + dy * W, tip.Y - dy * L * 0.15f - dx * W);
            using var br = new SolidBrush(col);
            using var outline = new Pen(Color.FromArgb(230, 11, 13, 16), 1.2f);
            var poly = new[] { a, b0, b1 };
            g.FillPolygon(br, poly); g.DrawPolygon(outline, poly);
        }

        // (1.2.81) aguja que no está como dice su itinerario: un anillo rojo
        public static readonly Color DiscordCol = Color.FromArgb(244, 67, 54);
        public static void DrawDiscord(Graphics g, PointF q, float lw)
        {
            float r = lw + 7f;
            using var pen = new Pen(DiscordCol, 2f);
            g.DrawEllipse(pen, q.X - r, q.Y - r, 2 * r, 2 * r);
        }
        // (1.2.81) en el esquema, la rama directa es la que sigue en su fila (la más horizontal)
        static int FlatLeg(PointF[][] legs)
        {
            if (legs?[0] == null || legs[1] == null || legs[0].Length < 2 || legs[1].Length < 2) return -1;
            float Slope(PointF[] l) { var a = l[0]; var b = l[^1]; float dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y); return dx < 0.01f ? 99 : dy / dx; }
            float s0 = Slope(legs[0]), s1 = Slope(legs[1]);
            return Math.Abs(s0 - s1) < 0.05f ? -1 : s0 < s1 ? 0 : 1;
        }
        static float Len(PointF[] l) { float L = 0; for (int i = 1; i < l.Length; i++) L += Dist(l[i - 1], l[i]); return L; }
        static PointF AtLen(PointF[] l, float d)
        {
            for (int i = 1; i < l.Length; i++)
            {
                float s = Dist(l[i - 1], l[i]);
                if (d <= s || i == l.Length - 1) return Lerp(l[i - 1], l[i], s > 0 ? d / s : 0);
                d -= s;
            }
            return l[0];
        }
        static PointF[] Part(PointF[] l, float a, float b)
        {
            var r = new List<PointF> { AtLen(l, a) }; float acc = 0;
            for (int i = 1; i < l.Length; i++) { acc += Dist(l[i - 1], l[i]); if (acc > a && acc < b) r.Add(l[i]); }
            r.Add(AtLen(l, b));
            return r.ToArray();
        }
    }
}
