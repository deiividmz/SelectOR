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

        public static Color RouteColor => RouteCol;
        public static readonly Color QueueColor = Color.FromArgb(100, 181, 246);   // (1.2.75) itinerarios en cola

        public static void Draw(Graphics g, Schematic s, SchematicLive live, PointF origin, float zoom, Rectangle area)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var bg = new SolidBrush(Bg)) g.FillRectangle(bg, area);
            if (s == null) return;
            PointF P(PointF q) => new PointF(origin.X + q.X * zoom, origin.Y + q.Y * zoom);
            float lw = Math.Max(2.6f, Math.Min(6f, 3.4f * zoom));

            // andenes (1.2.75, como en los esquemas de ADIF): una barra junto a su vía y el número de la vía a cada extremo
            float ph = Math.Max(3.5f, Math.Min(9f, 7f * zoom));
            var platBars = new List<RectangleF>();
            using (var pb = new SolidBrush(PlatCol))
            using (var pe = new Pen(PlatEdge, 1f))
                foreach (var p in s.Platforms)
                {
                    var a = P(new PointF(p.Bar.Left, p.Bar.Top)); var b = P(new PointF(p.Bar.Right, p.Bar.Top));
                    var r = new RectangleF(a.X, p.Above ? a.Y - lw / 2 - 1.5f - ph : a.Y + lw / 2 + 1.5f, b.X - a.X, ph);
                    g.FillRectangle(pb, r); g.DrawRectangle(pe, r.X, r.Y, r.Width, r.Height);
                    platBars.Add(r);
                }

            // vías: cada cantón aparte, de su color
            var circ = _circ.GetValue(s, SchematicCircuits.Circuits);
            float gap = Math.Max(1.2f, Math.Min(2.5f, 1.6f * zoom));
            bool Over(List<(double a, double b)> l, double a, double b) => l != null && l.Any(q => q.a < b - 0.5 && a + 0.5 < q.b);
            var lines = new List<PointF[]>();   // (1.2.81) lo dibujado, para colocar las señales
            // (1.2.81) la vía de un paso superior, la última y con un borde del color del fondo: corta las que pasan por debajo
            var overE = new HashSet<int>(s.Bridges.Select(q => q.Edge));
            foreach (var (e, a, b) in circ.OrderBy(c => overE.Contains(c.e) ? 1 : 0))
            {
                if (s.HiddenEdges.Contains(e)) continue;   // (1.2.81: los de una tijera van en su símbolo)
                var pc = s.Piece(e, a, b); if (pc == null) continue;
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
                using var pen = new Pen(col, lw) { LineJoin = LineJoin.Miter, StartCap = LineCap.Flat, EndCap = LineCap.Flat };
                g.DrawLines(pen, pts);
                lines.Add(pts);
            }
            // (1.2.75) itinerarios en cola: encima, en discontinuo azul
            if (live != null && live.Queued.Count > 0)
                using (var qp = new Pen(QueueColor, Math.Max(1.6f, lw - 1.5f)) { DashStyle = DashStyle.Dash })
                    foreach (var kv in live.Queued)
                        foreach (var (a, b) in kv.Value)
                        {
                            if (s.HiddenEdges.Contains(kv.Key)) continue;   // (1.2.81: las de una tijera, en su símbolo)
                            var pc = s.Piece(kv.Key, a, b);
                            if (pc != null && pc.Length >= 2) g.DrawLines(qp, pc.Select(P).ToArray());
                        }
            // agujas: (1.2.81) la rama puesta, marcada; la otra, cortada junto a la aguja (DrawSwitchState)
            var tijN = new HashSet<int>(s.ScissorGroups.SelectMany(q => q.Nodes));
            if (live?.SwitchPos != null)
                foreach (var sw in s.Switches)
                {
                    if (tijN.Contains(sw.Node)) continue;   // (1.2.81: van en el símbolo de su tijera)
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
                        if (tijN.Contains(sw.Node)) continue;
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
            // travesías y cruces
            // (1.2.81, petición) cada escape en tijera, un símbolo: un recuadro con sus dos paralelas y sus dos diagonales en X,
            // unido a sus cuatro agujas; lo que está puesto, iluminado (paralela a directa en blanco, diagonal en amarillo; con
            // un tren, en rojo; con un itinerario, de su color); lo demás, apagado. Su número (T1, T2…) encima
            foreach (var tg in s.ScissorGroups)
            {
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
                foreach (var c in s.Crossings) { var q = P(c); if (tBoxes.Any(b => b.Contains(q))) continue; float r = 4f; g.DrawLine(xp, q.X - r, q.Y - r, q.X + r, q.Y + r); g.DrawLine(xp, q.X - r, q.Y + r, q.X + r, q.Y - r); }
            // (1.2.81) pasos superiores: los pretiles del puente a los dos lados de la vía de arriba
            using (var bp = new Pen(Color.FromArgb(170, 180, 190), Math.Max(1.2f, lw * 0.4f)))
                foreach (var (at, d, _) in s.Bridges)
                {
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
                foreach (var b in s.Buffers) { var q = P(b); g.DrawLine(bp, q.X, q.Y - 5, q.X, q.Y + 5); }

            // señales: mástil y luz del lado al que miran. (1.2.75) Al acercar, crecen con el esquema (sk: hasta 3,5 veces)
            // (1.2.81) cada una colocada donde no pise a otra señal, un número de aguja, otra vía ni el rótulo de una salida
            // (PlaceSignals): con las filas juntas, dos señales a la misma altura en vías vecinas se montaban
            float sk = Math.Max(1f, Math.Min(3.5f, zoom / 1.4f));
            float lr0 = 2.3f * Math.Max(0.85f, Math.Min(1.4f, zoom)) * sk;   // radio de cada luz de la figura
            var boxes = new List<(Schematic.Signal sg, RectangleF box)>();
            _sigBoxes.AddOrUpdate(s, boxes);
            void Box(Schematic.Signal sg, PointF foot, RectangleF r) => boxes.Add((sg, RectangleF.Union(r, new RectangleF(foot.X - 3, Math.Min(foot.Y, r.Y), 6, Math.Abs(foot.Y - r.Y) + 1))));
            float idSize = 6.6f * Math.Min(1.8f, sk), papSize = 6.8f * Math.Min(2.2f, sk);
            var places = PlaceSignals(g, s, live, P, zoom, sk, lr0, lw, idSize, papSize, lines, numBoxes, platBars);
            using (var mp = new Pen(Color.FromArgb(150, 160, 170), 1.1f * Math.Min(2f, sk)))
            using (var op = new Pen(Color.FromArgb(220, 15, 17, 19), 1.2f * Math.Min(2f, sk)))
                foreach (var sg in s.Signals)
                {
                    var q = P(sg.At);
                    var pl = places.TryGetValue(sg, out var pp) ? pp : new SigPlace { Side = sg.Face < 0 ? -1 : 1, Mast = 8f * sk, Dir = sg.Face, Arm = 4.5f * sk };
                    int dir = pl.Dir; float dy = pl.Side * pl.Mast, arm = pl.Arm;
                    g.DrawLine(mp, q.X, q.Y + pl.Side * lw / 2, q.X, q.Y + dy);
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

            // (1.2.81) los números de las agujas, sin pisar otro número ni una señal (dos agujas a la misma altura en vías vecinas,
            // como en Dos Hermanos): en su sitio; si no, corrido hacia el tronco (las ramas salen hacia el otro lado) o al otro lado
            numBoxes.Clear();
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
                    foreach (var x in s.Exits.Where(q => q.Right == right))
                    {
                        var q = P(x.At);
                        if (Math.Abs(col - q.X) > 3) g.DrawLine(dp, q.X, q.Y, col, q.Y);
                        float d = right ? 1 : -1, ah = 5f;
                        g.FillPolygon(ab, new[] { new PointF(col, q.Y - ah), new PointF(col + d * 7, q.Y), new PointF(col, q.Y + ah) });
                        string t = x.Station ?? "";
                        var sz = g.MeasureString(t, f);
                        g.DrawString(t, f, tb, right ? col + 10 : col - 10 - sz.Width, q.Y - sz.Height / 2);
                    }
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
                var sz = g.MeasureString(x.Station ?? "", f);
                var r = new RectangleF(x.Right ? col : col - 10 - sz.Width, q.Y - sz.Height / 2, sz.Width + 10, sz.Height);
                if (r.Contains(mouse)) return x;
            }
            return null;
        }

        // (1.2.81) dónde va cada señal: lado de la vía (Side ±1), largo del mástil, hacia dónde sale la figura (Dir ±1) y largo
        // del brazo. Primero la de siempre (del lado al que mira, figura hacia delante); si pisa algo, la alternativa que menos
        // pise y menos se aparte de lo normal. Se recuerda para el mismo zoom (al mover el esquema no cambia nada).
        public struct SigPlace { public int Side, Dir; public float Mast, Arm; }
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

        static Dictionary<Schematic.Signal, SigPlace> PlaceSignals(Graphics g, Schematic s, SchematicLive live, Func<PointF, PointF> P, float zoom,
            float sk, float lr0, float lw, float idSize, float papSize, List<PointF[]> lines, List<RectangleF> nums, List<RectangleF> plats)
        {
            bool compact = zoom < 0.7f;
            // lo que ocupa la figura de cada una (en compacto, lo de dos luces: si no, cambiaría de sitio con cada aspecto)
            var size = new Dictionary<Schematic.Signal, (float along, float across, float extra)>();
            var key = new System.Text.StringBuilder();
            using (var pf = Theme.Font(papSize, FontStyle.Bold))
            using (var fi = Theme.Font(idSize))
                foreach (var sg in s.Signals)
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
                    if (zoom >= 1.0f && sg.Sig?.Obj >= 0) extra += 2 * sk + g.MeasureString(sg.Sig.Obj.ToString(System.Globalization.CultureInfo.InvariantCulture), fi).Width;
                    size[sg] = (along, across, extra);
                }
            string k = key.ToString();
            if (_places.TryGetValue(s, out var pc) && Math.Abs(pc.Zoom - zoom) < 1e-4f && pc.Key == k) return pc.Map;

            // todo, relativo al primer punto (así vale al mover el esquema)
            var o = P(new PointF(0, 0));
            PointF Rel(PointF p) => new PointF(p.X - o.X, p.Y - o.Y);
            RectangleF RelR(RectangleF r) => new RectangleF(r.X - o.X, r.Y - o.Y, r.Width, r.Height);
            var segs = new List<(PointF a, PointF b)>();
            foreach (var l in lines) for (int i = 1; i < l.Length; i++) segs.Add((Rel(l[i - 1]), Rel(l[i])));
            var numsR = nums.Select(RelR).ToList(); var platsR = plats.Select(RelR).ToList();

            float m0 = 8f * sk, step2 = Math.Max(8f * sk, 3.3f * lr0 + 1), arm0 = 4.5f * sk;
            // la figura, el mástil (vertical) y el brazo (horizontal), por separado: juntos, su recuadro tapaba mucho más
            (RectangleF fig, RectangleF mast, RectangleF arm) Shape(Schematic.Signal sg, SigPlace p)
            {
                var q = Rel(P(sg.At)); var (along, across, extra) = size[sg];
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
                return c + 200 * hit + 150 * mhit + 60 * near + (closer ? 120 : 0);
            }
            // las opciones: (lado, mástil, figura, brazo) y lo que cuesta apartarse de lo normal
            List<(SigPlace p, float pen)> Options(Schematic.Signal sg)
            {
                int side0 = sg.Face < 0 ? -1 : 1, dir0 = sg.Face; var (along, across, extra) = size[sg];
                float mMin = Math.Max(lw / 2 + across / 2 + 2f, 4f);
                var masts = new List<(float m, float pen)> { (m0, 0) };
                if (mMin < m0 - 1) masts.Add((mMin, 6));
                masts.Add((m0 + step2, 14)); masts.Add((m0 + 2 * step2, 32));
                var l = new List<(SigPlace, float)>();
                foreach (int side in new[] { side0, -side0 })
                    foreach (int dir in new[] { dir0, -dir0 })
                        foreach (var (m, mp) in masts)
                            foreach (var (arm, ap) in new[] { (arm0, 0f), (arm0 + along + extra + 4 * sk, 18f) })
                                l.Add((new SigPlace { Side = side, Dir = dir, Mast = m, Arm = arm }, mp + ap + (side != side0 ? 40 : 0) + (dir != dir0 ? 50 : 0)));
                return l;
            }
            var map = new Dictionary<Schematic.Signal, SigPlace>();
            var pens = new Dictionary<Schematic.Signal, float>(); var idx = new Dictionary<Schematic.Signal, int>();
            // primero las que no tienen problema en su sitio de siempre; luego las demás, de la más apretada a la menos
            var def = s.Signals.ToDictionary(sg => sg, sg => new SigPlace { Side = sg.Face < 0 ? -1 : 1, Dir = sg.Face, Mast = m0, Arm = arm0 });
            var shapes = s.Signals.Select(sg => (sg, sh: Shape(sg, def[sg]))).ToList();
            var order = s.Signals.Select(sg =>
            {
                float c = Cost(sg, def[sg], 0);
                var (f, m, a) = Shape(sg, def[sg]); var fi = RectangleF.Inflate(f, 1.5f, 1.5f);
                foreach (var (o2, sh) in shapes) if (o2 != sg && (fi.IntersectsWith(sh.fig) || m.IntersectsWith(sh.fig) || a.IntersectsWith(sh.fig) || f.IntersectsWith(sh.mast) || f.IntersectsWith(sh.arm))) c += 500;
                return (sg, c);
            }).OrderBy(t => t.c).ThenBy(t => t.sg.At.X).ToList();
            foreach (var (sg, _) in order)
            {
                var best = def[sg]; float bc = Cost(sg, best, 0);
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
