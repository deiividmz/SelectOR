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
        public readonly List<(string name, string sub, PointF[] body, bool player)> Trains = new();
        public readonly HashSet<int> Flash = new();               // señales en las que alguien acaba de pedir paso (Tab): parpadean
        public readonly Dictionary<int, List<(double a, double b)>> Occupied = new();          // por tramo: metros con un tren
        public readonly Dictionary<int, List<(double a, double b, bool callOn)>> Route = new(); // por tramo: itinerarios establecidos
        public readonly Dictionary<int, List<(double a, double b)>> Cancelling = new();         // (1.2.75) itinerarios anulándose (temporizados)
        public readonly Dictionary<int, List<(double a, double b)>> Queued = new();             // (1.2.75) itinerarios en cola (discontinuo)
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

        // número de cada aguja en el esquema (de izquierda a derecha)
        public static int SwitchNumber(Schematic s, int node) => _swNum.GetValue(s, k =>
        {
            var d = new Dictionary<int, int>(); int i = 1;
            foreach (var sw in k.Switches.OrderBy(q => q.At.X).ThenBy(q => q.At.Y)) d[sw.Node] = i++;
            return d;
        }).TryGetValue(node, out int n) ? n : 0;

        // (1.2.75) dónde ha quedado cada señal en el último dibujo (mástil, figura, preanuncio y número), para el clic: antes
        // solo valía el pie del mástil, sobre la vía, y la señal se pinta apartada
        static readonly ConditionalWeakTable<Schematic, List<(Schematic.Signal sg, RectangleF box)>> _sigBoxes = new();
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
            using (var pb = new SolidBrush(PlatCol))
            using (var pe = new Pen(PlatEdge, 1f))
                foreach (var p in s.Platforms)
                {
                    var a = P(new PointF(p.Bar.Left, p.Bar.Top)); var b = P(new PointF(p.Bar.Right, p.Bar.Top));
                    var r = new RectangleF(a.X, a.Y + lw / 2 + 1.5f, b.X - a.X, ph);
                    g.FillRectangle(pb, r); g.DrawRectangle(pe, r.X, r.Y, r.Width, r.Height);
                }

            // vías: cada cantón aparte, de su color
            var circ = _circ.GetValue(s, SchematicCircuits.Circuits);
            float gap = Math.Max(1.2f, Math.Min(2.5f, 1.6f * zoom));
            bool Over(List<(double a, double b)> l, double a, double b) => l != null && l.Any(q => q.a < b - 0.5 && a + 0.5 < q.b);
            foreach (var (e, a, b) in circ)
            {
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
                using var pen = new Pen(col, lw) { LineJoin = LineJoin.Miter, StartCap = LineCap.Flat, EndCap = LineCap.Flat };
                g.DrawLines(pen, pts);
            }
            // (1.2.75) itinerarios en cola: encima, en discontinuo azul
            if (live != null && live.Queued.Count > 0)
                using (var qp = new Pen(QueueColor, Math.Max(1.6f, lw - 1.5f)) { DashStyle = DashStyle.Dash })
                    foreach (var kv in live.Queued)
                        foreach (var (a, b) in kv.Value)
                        {
                            var pc = s.Piece(kv.Key, a, b);
                            if (pc != null && pc.Length >= 2) g.DrawLines(qp, pc.Select(P).ToArray());
                        }
            // agujas: el lado que no está puesto, cortado junto a la aguja
            if (live?.SwitchPos != null)
                using (var cut = new Pen(Bg, lw + 2.5f) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                    foreach (var sw in s.Switches)
                    {
                        int pos = sw.Order >= 0 ? live.SwitchPos(sw.Order) : -1;
                        if (pos != 0 && pos != 1) continue;
                        var leg = sw.Legs[1 - pos]; if (leg == null || leg.Length < 2) continue;
                        var a = Lerp(leg[0], leg[^1], 2.5f / Math.Max(1f, Dist(leg[0], leg[^1])));
                        var b = Lerp(leg[0], leg[^1], 10f / Math.Max(1f, Dist(leg[0], leg[^1])));
                        g.DrawLine(cut, P(a), P(b));
                    }
            // número de cada aguja (azul si está enclavada)
            if (zoom >= 0.7f)
                using (var nf = Theme.Font(6.6f))
                    foreach (var sw in s.Switches)
                    {
                        int n = SwitchNumber(s, sw.Node); if (n <= 0) continue;
                        bool locked = live?.Locked != null && sw.Order >= 0 && live.Locked(sw.Order);
                        // del lado contrario al que sale la desviada (para no taparla)
                        var leg = sw.Legs[0] ?? sw.Legs[1]; float side = 1;
                        if (sw.Legs[0] != null && sw.Legs[1] != null) side = (sw.Legs[0][^1].Y + sw.Legs[1][^1].Y) / 2 > sw.At.Y + 0.5f ? -1 : 1;
                        var q = P(sw.At); string t = n.ToString();
                        var sz = g.MeasureString(t, nf);
                        var box = new RectangleF(q.X - sz.Width / 2 - 1, side > 0 ? q.Y + lw / 2 + 3 : q.Y - lw / 2 - 3 - sz.Height, sz.Width + 2, sz.Height);
                        using (var bb = new SolidBrush(Color.FromArgb(230, 22, 26, 32))) g.FillRectangle(bb, box);
                        using (var bp = new Pen(locked ? LockCol : Color.FromArgb(95, 105, 115), 1f)) g.DrawRectangle(bp, box.X, box.Y, box.Width, box.Height);
                        using (var tbb = new SolidBrush(locked ? LockCol : Color.FromArgb(190, 196, 204))) g.DrawString(t, nf, tbb, box.X + 1, box.Y);
                    }
            // travesías y cruces
            using (var rp = new Pen(TrackCol, 1.4f))
                foreach (var c in s.Slips)
                {
                    var q = P(c); float r = 5f * Math.Min(1.6f, zoom);
                    g.DrawPolygon(rp, new[] { new PointF(q.X - r, q.Y), new PointF(q.X, q.Y - r * 0.7f), new PointF(q.X + r, q.Y), new PointF(q.X, q.Y + r * 0.7f) });
                }
            using (var xp = new Pen(TrackCol, 1.4f))
                foreach (var c in s.Crossings) { var q = P(c); float r = 4f; g.DrawLine(xp, q.X - r, q.Y - r, q.X + r, q.Y + r); g.DrawLine(xp, q.X - r, q.Y + r, q.X + r, q.Y - r); }
            // toperas
            using (var bp = new Pen(TrackCol, lw))
                foreach (var b in s.Buffers) { var q = P(b); g.DrawLine(bp, q.X, q.Y - 5, q.X, q.Y + 5); }

            // señales: mástil y luz del lado al que miran. (1.2.75) Al acercar, crecen con el esquema (sk: hasta 3,5 veces)
            float sk = Math.Max(1f, Math.Min(3.5f, zoom / 1.4f));
            float lr0 = 2.3f * Math.Max(0.85f, Math.Min(1.4f, zoom)) * sk;   // radio de cada luz de la figura
            float stackStep = Math.Max(8f * sk, 3.3f * lr0 + 1);              // dos señales en el mismo sitio: una tras otra
            var boxes = new List<(Schematic.Signal sg, RectangleF box)>();
            _sigBoxes.AddOrUpdate(s, boxes);
            void Box(Schematic.Signal sg, PointF foot, RectangleF r) => boxes.Add((sg, RectangleF.Union(r, new RectangleF(foot.X - 3, Math.Min(foot.Y, r.Y), 6, Math.Abs(foot.Y - r.Y) + 1))));
            using (var mp = new Pen(Color.FromArgb(150, 160, 170), 1.1f * Math.Min(2f, sk)))
            using (var op = new Pen(Color.FromArgb(220, 15, 17, 19), 1.2f * Math.Min(2f, sk)))
                foreach (var sg in s.Signals)
                {
                    var q = P(sg.At); bool up = sg.Face < 0; float dy = (up ? -1 : 1) * (8f * sk + sg.Stack * stackStep);
                    float arm = 4.5f * sk;
                    g.DrawLine(mp, q.X, q.Y + (up ? -lw / 2 : lw / 2), q.X, q.Y + dy);
                    g.DrawLine(mp, q.X, q.Y + dy, q.X + arm * sg.Face, q.Y + dy);
                    int asp = live?.Aspect?.Invoke(sg.Sig) ?? -1;
                    float r = 3.2f * sk, lx = q.X + (arm + r) * sg.Face, ly = q.Y + dy;
                    if (live != null && sg.Sig != null && live.Flash.Contains(sg.Sig.Obj) && DateTime.Now.Millisecond < 600)
                        using (var fr = new Pen(Color.FromArgb(255, 213, 79), 2.2f)) g.DrawEllipse(fr, lx - r - 6, ly - r - 6, 2 * r + 12, 2 * r + 12);
                    float idSize = 6.6f * Math.Min(1.8f, sk);
                    // (1.2.75) la figura de la señal, tumbada desde el mástil, y el número del preanuncio al final
                    var fig = live?.Figure?.Invoke(sg.Sig);
                    if (fig != null)
                    {
                        if (RoomMapDraw.Flashing(fig)) live.Blinking = true;
                        var fb = RoomMapDraw.DrawFigure(g, fig, new PointF(q.X + arm * sg.Face, ly), lr0, zoom < 0.7f, sg.Face);
                        float end = sg.Face > 0 ? fb.Right + 2 * sk : fb.Left - 2 * sk;
                        if (fig.Pap != null)
                        {
                            float fs = 6.8f * Math.Min(2.2f, sk);
                            using var pf = Theme.Font(fs, FontStyle.Bold);
                            var psz = g.MeasureString(fig.Pap, pf);
                            float px = sg.Face > 0 ? end : end - psz.Width - 1;
                            var ps = RoomMapDraw.DrawPap(g, fig, px, ly - psz.Height / 2, fs);
                            end = sg.Face > 0 ? end + ps.Width + 2 * sk : end - ps.Width - 2 * sk;
                        }
                        if (zoom >= 1.0f && sg.Sig?.Obj >= 0)
                        {
                            string t = sg.Sig.Obj.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            using var fi = Theme.Font(idSize);
                            var sz = g.MeasureString(t, fi);
                            float tx = sg.Face > 0 ? end : end - sz.Width + 2, ty = ly - sz.Height / 2;
                            using var ib = new SolidBrush(Color.FromArgb(150, 160, 170)); g.DrawString(t, fi, ib, tx, ty);
                            end = sg.Face > 0 ? tx + sz.Width : tx;
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
                        float tx = sg.Face > 0 ? lx + r + 1 : lx - r - sz.Width + 2, ty = ly - sz.Height / 2;
                        using var ib = new SolidBrush(Color.FromArgb(150, 160, 170)); g.DrawString(t, fi, ib, tx, ty);
                    }
                    Box(sg, q, RectangleF.FromLTRB(Math.Min(q.X, lx - r), ly - r, Math.Max(q.X, lx + r), ly + r));
                }

            // (1.2.75) número de cada vía de andén: encima de todo (antes iba debajo de la vía, que lo cruzaba como tachado),
            // a la altura de la barra del andén, a la izquierda, con un borde del color del fondo
            if (zoom >= 0.45f)
                using (var f = Theme.Font(Math.Max(7f, Math.Min(15f, 6f * zoom)), FontStyle.Bold))
                using (var tagB = new SolidBrush(TagBg))
                using (var tagT = new SolidBrush(TagTxt))
                using (var halo = new Pen(Bg, 2.5f))
                using (var edge = new Pen(Color.FromArgb(70, 120, 180), 1f))
                {
                    var done = new HashSet<string>();
                    foreach (var p in s.Platforms)
                    {
                        if (p.Label == null || !done.Add(p.Label + "@" + p.Bar.Top.ToString("0"))) continue;
                        var a = P(new PointF(p.Bar.Left, p.Bar.Top));
                        var sz = g.MeasureString(p.Label, f);
                        float top = a.Y + lw / 2 + 1.5f;   // (la barra del andén empieza ahí)
                        var box = new RectangleF(a.X - sz.Width - 6, top, sz.Width + 2, sz.Height);
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
                        var col = tr.player ? Occ : AiCol;
                        var pts = tr.body.Select(P).ToArray();
                        var mid = pts[pts.Length / 2];
                        // y una línea fina encima de su vía, para ver dónde está dentro del cantón
                        using (var pen = new Pen(Color.FromArgb(235, col), Math.Max(1.5f, lw - 2.5f)) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                            if (pts.Length >= 2) g.DrawLines(pen, pts);
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
            // la estación a la que se va
            using (var f = Theme.Font(Math.Max(6.5f, Math.Min(8.4f, Schematic.Row * zoom * 0.5f)), FontStyle.Bold))   // (que no se pisen con las filas juntas)
            using (var tb = new SolidBrush(Txt))
            using (var dp = new Pen(Dim, 1.2f) { DashStyle = DashStyle.Dot })
            using (var ab = new SolidBrush(Color.FromArgb(170, 182, 196)))
                foreach (bool right in new[] { false, true })
                {
                    float col = right ? P(new PointF(s.Bounds.Right, 0)).X + 14 : P(new PointF(s.Bounds.Left, 0)).X - 14;
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
                float col = x.Right ? origin.X + s.Bounds.Right * zoom + 14 : origin.X + s.Bounds.Left * zoom - 14;
                var sz = g.MeasureString(x.Station ?? "", f);
                var r = new RectangleF(x.Right ? col : col - 10 - sz.Width, q.Y - sz.Height / 2, sz.Width + 10, sz.Height);
                if (r.Contains(mouse)) return x;
            }
            return null;
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
    }
}
