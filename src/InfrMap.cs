// Mapa de una infracción en Revisión: el tramo de vía por el que iba el tren, 2 km antes y 2 km después del punto
// de la infracción, con los límites de velocidad que declara la ruta (sus postes de velocidad, como la hoja de ruta)
// y el punto marcado. El tramo es el que guardó SelectOR al registrar el servicio (MainMenuForm.InfrLugar.cs); se
// sigue por las vías de la ruta instalada (RoadBook) para sacar los postes que hay en él.
//  · Mapa: la vía del tramo coloreada según su límite, con las señales de límite donde cambia, las demás vías de
//    alrededor en gris, el punto de la infracción (y hasta dónde siguió el exceso) y el sentido de la marcha (de
//    izquierda a derecha; la flecha N dice dónde queda el norte).
//  · Perfil: los límites a lo largo del tramo (−2 … +2 km) y la velocidad del tren en la infracción.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace SelectOR
{
    // Lo que guardó la infracción sobre dónde fue (null si no lo guardó: anteriores a la 1.2.64).
    public sealed class InfrPlace
    {
        public double Lat = double.NaN, Lon = double.NaN, D0, D1, MaxKmh = double.NaN, LimitKmh = double.NaN;
        public string Trail = "", RDir = "";

        public static InfrPlace From(JsonElement d)
        {
            if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("trail", out var t) || t.ValueKind != JsonValueKind.String) return null;
            double N(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
            var p = new InfrPlace { Trail = t.GetString() ?? "", Lat = N("lat"), Lon = N("lon"), D0 = N("d0"), D1 = N("d1"), MaxKmh = N("max_kmh"), LimitKmh = N("limit_kmh") };
            if (d.TryGetProperty("rdir", out var r) && r.ValueKind == JsonValueKind.String) p.RDir = r.GetString() ?? "";
            if (double.IsNaN(p.D0)) p.D0 = 0;
            if (double.IsNaN(p.D1) || p.D1 < p.D0) p.D1 = p.D0;
            return p.Trail.Length > 0 ? p : null;
        }
    }

    public sealed class InfrMap
    {
        public double[] X = Array.Empty<double>(), Y = Array.Empty<double>(), D = Array.Empty<double>();   // muestras del tramo (proyección local) y su distancia desde el principio
        public double Len, DInf, DEnd;                       // longitud del tramo, punto de la infracción y fin del exceso
        public readonly List<(double d, double kmh)> Limits = new();   // postes dentro del tramo
        public double InitKmh = double.NaN;                  // límite al principio del tramo (NaN = no se sabe)
        public bool InitFromMonitor;                         // … sacado del monitor de Open Rails (no hay poste antes)
        public double TrainKmh = double.NaN;
        public readonly List<(float[] x, float[] y)> Context = new();   // las demás vías de alrededor

        public double LimitAt(double d)
        {
            double k = InitKmh;
            foreach (var l in Limits) { if (l.d <= d + 0.01) k = l.kmh; else break; }
            return k;
        }

        // null + motivo si no se puede.
        public static (InfrMap map, string why) Build(RouteGraph g, string dir, InfrPlace p)
        {
            var trail = MainMenuForm.DecodePolyline(p.Trail);
            if (trail.Count < 2) return (null, I18n.T("El tramo guardado no se entiende."));
            var cum = new double[trail.Count];
            for (int i = 1; i < trail.Count; i++) cum[i] = cum[i - 1] + LiveMates.Haversine(trail[i - 1].lat, trail[i - 1].lon, trail[i].lat, trail[i].lon);
            (double lat, double lon) AtTrail(double d)
            {
                d = Math.Max(0, Math.Min(cum[^1], d));
                int i = 1; while (i < cum.Length - 1 && cum[i] < d) i++;
                double seg = cum[i] - cum[i - 1], f = seg > 0 ? (d - cum[i - 1]) / seg : 0;
                return (trail[i - 1].lat + (trail[i].lat - trail[i - 1].lat) * f, trail[i - 1].lon + (trail[i].lon - trail[i - 1].lon) * f);
            }
            var inf = !double.IsNaN(p.Lat) && !double.IsNaN(p.Lon) ? (p.Lat, p.Lon) : AtTrail(p.D0);
            var end = AtTrail(p.D1);

            // el tramo por las vías de la ruta: un punto cada 250 m del rastro
            var rb = new RoadBook(); rb.Reset(dir); rb.Graph = g;
            int added = 0; double next = 0;
            for (int i = 0; i < trail.Count; i++)
                if (cum[i] >= next || i == trail.Count - 1)
                {
                    if (rb.AddPoint(trail[i].lat, trail[i].lon, 40)) added++;
                    next = cum[i] + 250;
                }
            if (added < 2) return (null, I18n.T("El tramo no coincide con las vías de esta ruta (¿otra versión?)."));
            rb.Replan(false, 0, 0, false, 0);
            if (!rb.HasPlan || rb.TotalLen < 50) return (null, I18n.T("No se ha podido seguir el tramo por las vías de la ruta."));

            const double step = 8;
            int ns = (int)Math.Ceiling(rb.TotalLen / step) + 1;
            var xs = new double[ns]; var ys = new double[ns]; var ds = new double[ns];
            for (int i = 0; i < ns; i++)
            {
                double d = Math.Min(rb.TotalLen, i * step);
                var (la, lo) = rb.LatLonAt(d); var (x, y) = g.Proj(la, lo);
                xs[i] = x; ys[i] = y; ds[i] = d;
            }
            double Near(double lat, double lon, double from)
            {
                var (px, py) = g.Proj(lat, lon); double best = double.MaxValue, bd = from;
                for (int i = 0; i < ns; i++) { if (ds[i] < from) continue; double q = (xs[i] - px) * (xs[i] - px) + (ys[i] - py) * (ys[i] - py); if (q < best) { best = q; bd = ds[i]; } }
                return bd;
            }
            double dI = Near(inf.Item1, inf.Item2, 0);
            double dE = Math.Max(dI, Near(end.lat, end.lon, dI));
            double w0 = Math.Max(0, dI - 2000), w1 = Math.Min(rb.TotalLen, Math.Max(dI + 2000, dE + 200));

            var m = new InfrMap { TrainKmh = p.MaxKmh };
            var lx = new List<double>(); var ly = new List<double>(); var ld = new List<double>();
            for (int i = 0; i < ns; i++) if (ds[i] >= w0 - 0.01 && ds[i] <= w1 + 0.01) { lx.Add(xs[i]); ly.Add(ys[i]); ld.Add(ds[i] - w0); }
            if (lx.Count < 2) return (null, I18n.T("No se ha podido seguir el tramo por las vías de la ruta."));
            m.X = lx.ToArray(); m.Y = ly.ToArray(); m.D = ld.ToArray();
            m.Len = w1 - w0; m.DInf = dI - w0; m.DEnd = dE - w0;
            foreach (var (d, kmh) in rb.Limits)
            {
                if (d <= w0 + 0.01) m.InitKmh = kmh;
                else if (d <= w1) m.Limits.Add((d - w0, kmh));
            }
            // sin poste antes del tramo: hasta el primero, el límite que marcaba Open Rails en la infracción (si no hay
            // otro poste entre medias)
            if (double.IsNaN(m.InitKmh) && !double.IsNaN(p.LimitKmh) && !m.Limits.Any(l => l.d <= m.DInf)) { m.InitKmh = p.LimitKmh; m.InitFromMonitor = true; }

            // las demás vías de alrededor
            double minX = m.X.Min() - 400, maxX = m.X.Max() + 400, minY = m.Y.Min() - 400, maxY = m.Y.Max() + 400;
            foreach (var e in g.Edges)
            {
                if (e.X == null || e.X.Length < 2) continue;
                bool inBox = false;
                for (int i = 0; i < e.X.Length && !inBox; i++) inBox = e.X[i] >= minX && e.X[i] <= maxX && e.Y[i] >= minY && e.Y[i] <= maxY;
                if (inBox) m.Context.Add((e.X, e.Y));
            }
            return (m, null);
        }

        // ---------------------------------------------------------------- dibujo

        public static Color LimitColor(double kmh) =>
            double.IsNaN(kmh) ? Color.FromArgb(150, 158, 166)
            : kmh <= 30 ? Color.FromArgb(239, 83, 80) : kmh <= 50 ? Color.FromArgb(255, 138, 101) : kmh <= 70 ? Color.FromArgb(255, 183, 77)
            : kmh <= 90 ? Color.FromArgb(255, 213, 79) : kmh <= 110 ? Color.FromArgb(205, 220, 57) : kmh <= 140 ? Color.FromArgb(102, 187, 106)
            : kmh <= 180 ? Color.FromArgb(77, 182, 172) : Color.FromArgb(100, 181, 246);

        static string K(double kmh) => Math.Round(kmh).ToString("0", CultureInfo.InvariantCulture);
        static readonly Color Bg = Color.FromArgb(30, 34, 38), Red = Color.FromArgb(239, 83, 80);

        // Mapa: sentido de la marcha de izquierda a derecha.
        public void PaintMap(Graphics g, Rectangle r, Font fSmall, Font fBold, float k)
        {
            var oldClip = g.Clip;
            using (var bp = Theme.Round(r, (int)(8 * k))) { using (var b = new SolidBrush(Bg)) g.FillPath(b, bp); g.SetClip(bp); }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // giro: el tramo de izquierda a derecha
            int iA = 0, iB = X.Length - 1;
            double ang = Math.Atan2(Y[iB] - Y[iA], X[iB] - X[iA]), ca = Math.Cos(-ang), sa = Math.Sin(-ang);
            double cx0 = (X.Min() + X.Max()) / 2, cy0 = (Y.Min() + Y.Max()) / 2;
            (double, double) Rot(double x, double y) { x -= cx0; y -= cy0; return (x * ca - y * sa, x * sa + y * ca); }
            double rminX = double.MaxValue, rmaxX = double.MinValue, rminY = double.MaxValue, rmaxY = double.MinValue;
            for (int i = 0; i < X.Length; i++) { var (a, b) = Rot(X[i], Y[i]); rminX = Math.Min(rminX, a); rmaxX = Math.Max(rmaxX, a); rminY = Math.Min(rminY, b); rmaxY = Math.Max(rmaxY, b); }
            double bw = Math.Max(50, rmaxX - rminX), bh = Math.Max(50, rmaxY - rminY);
            float padX = 26 * k, padY = 34 * k;
            double sc = Math.Min((r.Width - 2 * padX) / bw, (r.Height - 2 * padY) / bh);
            double mx = (rminX + rmaxX) / 2, my = (rminY + rmaxY) / 2;
            PointF P(double x, double y) { var (a, b) = Rot(x, y); return new PointF((float)(r.X + r.Width / 2.0 + (a - mx) * sc), (float)(r.Y + r.Height / 2.0 + (b - my) * sc)); }

            // las demás vías
            using (var cp = new Pen(Color.FromArgb(78, 86, 94), Math.Max(1f, 1.4f * k)))
                foreach (var (x, y) in Context)
                {
                    var pts = new PointF[x.Length];
                    for (int i = 0; i < x.Length; i++) pts[i] = P(x[i], y[i]);
                    g.DrawLines(cp, pts);
                }
            // el exceso: debajo, en rojo
            if (DEnd > DInf + 1)
                using (var op = new Pen(Color.FromArgb(110, Red), 11 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    var seg = new List<PointF>();
                    for (int i = 0; i < D.Length; i++) if (D[i] >= DInf - 0.01 && D[i] <= DEnd + 0.01) seg.Add(P(X[i], Y[i]));
                    if (seg.Count > 1) g.DrawLines(op, seg.ToArray());
                }
            // el tramo, de color según su límite
            for (int i = 1; i < D.Length; i++)
                using (var pen = new Pen(LimitColor(LimitAt(D[i - 1])), 4.2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, P(X[i - 1], Y[i - 1]), P(X[i], Y[i]));
            // sentido de la marcha
            {
                var a = P(X[^2], Y[^2]); var b = P(X[^1], Y[^1]);
                double an = Math.Atan2(b.Y - a.Y, b.X - a.X); float L = 9 * k;
                var tip = new PointF(b.X + (float)Math.Cos(an) * 4 * k, b.Y + (float)Math.Sin(an) * 4 * k);
                var p1 = new PointF(tip.X - (float)Math.Cos(an - 0.5) * L, tip.Y - (float)Math.Sin(an - 0.5) * L);
                var p2 = new PointF(tip.X - (float)Math.Cos(an + 0.5) * L, tip.Y - (float)Math.Sin(an + 0.5) * L);
                using var ab = new SolidBrush(LimitColor(LimitAt(Len))); g.FillPolygon(ab, new[] { tip, p1, p2 });
            }
            // señales de límite donde cambia (y la del principio), sin pisarse
            var signs = new List<(double d, double kmh, bool monitor)>();
            if (!double.IsNaN(InitKmh)) signs.Add((0, InitKmh, InitFromMonitor));
            signs.AddRange(Limits.Select(l => (l.d, l.kmh, false)));
            float sr = 10.5f * k; var placed = new List<PointF>();
            foreach (var (d, kmh, mon) in signs)
            {
                var at = PointAtD(d, P);
                // por encima de la vía (en la pantalla), con un palito
                var c = new PointF(at.X, at.Y - 22 * k);
                if (placed.Any(q => Math.Abs(q.X - c.X) < 2 * sr + 3 * k && Math.Abs(q.Y - c.Y) < 2 * sr + 3 * k)) c = new PointF(at.X, at.Y + 22 * k);
                if (placed.Any(q => Math.Abs(q.X - c.X) < 2 * sr + 3 * k && Math.Abs(q.Y - c.Y) < 2 * sr + 3 * k)) continue;
                placed.Add(c);
                using (var lp = new Pen(Color.FromArgb(160, 230, 230, 230), 1f)) g.DrawLine(lp, at, new PointF(c.X, c.Y + (c.Y < at.Y ? sr : -sr)));
                var cr = new RectangleF(c.X - sr, c.Y - sr, 2 * sr, 2 * sr);
                using (var wb = new SolidBrush(mon ? Color.FromArgb(225, 228, 232) : Color.White)) g.FillEllipse(wb, cr);
                using (var rp = new Pen(Red, 2.6f * k) { DashStyle = mon ? DashStyle.Dash : DashStyle.Solid }) g.DrawEllipse(rp, cr.X + 1.3f * k, cr.Y + 1.3f * k, cr.Width - 2.6f * k, cr.Height - 2.6f * k);
                TextRenderer.DrawText(g, K(kmh), fBold, Rectangle.Round(cr), Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
            // la infracción
            {
                var at = PointAtD(DInf, P);
                float rr = 7.5f * k;
                using (var rb = new SolidBrush(Red)) g.FillEllipse(rb, at.X - rr, at.Y - rr, 2 * rr, 2 * rr);
                using (var wp = new Pen(Color.White, 1.8f * k)) g.DrawEllipse(wp, at.X - rr, at.Y - rr, 2 * rr, 2 * rr);
                TextRenderer.DrawText(g, "!", fBold, new Rectangle((int)(at.X - rr), (int)(at.Y - rr), (int)(2 * rr), (int)(2 * rr)), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (!double.IsNaN(TrainKmh))
                {
                    string t = K(TrainKmh) + " km/h";
                    var sz = TextRenderer.MeasureText(g, t, fBold, Size.Empty, TextFormatFlags.NoPadding);
                    var lr = new Rectangle((int)(at.X - sz.Width / 2f - 5 * k), (int)(at.Y + rr + 4 * k), sz.Width + (int)(10 * k), sz.Height + (int)(4 * k));
                    if (lr.Bottom > r.Bottom - 2) lr.Y = (int)(at.Y - rr - 4 * k - lr.Height);
                    using (var lb = new SolidBrush(Color.FromArgb(220, 60, 24, 24))) using (var lpth = Theme.Round(lr, lr.Height / 2)) g.FillPath(lb, lpth);
                    TextRenderer.DrawText(g, t, fBold, lr, Color.FromArgb(255, 205, 205), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }
            // escala y norte
            {
                double[] nice = { 100, 200, 250, 500, 1000, 2000 };
                double want = r.Width * 0.22 / sc, len = nice.LastOrDefault(v => v <= want); if (len <= 0) len = 100;
                float px = (float)(len * sc), x0 = r.X + 10 * k, y0 = r.Bottom - 10 * k;
                using (var sp = new Pen(Color.FromArgb(200, 220, 224, 228), 1.5f * k)) { g.DrawLine(sp, x0, y0, x0 + px, y0); g.DrawLine(sp, x0, y0 - 4 * k, x0, y0); g.DrawLine(sp, x0 + px, y0 - 4 * k, x0 + px, y0); }
                string lt = len >= 1000 ? (len / 1000).ToString("0", CultureInfo.InvariantCulture) + " km" : len.ToString("0", CultureInfo.InvariantCulture) + " m";
                TextRenderer.DrawText(g, lt, fSmall, new Point((int)(x0 + px + 5 * k), (int)(y0 - 8 * k)), Color.FromArgb(200, 220, 224, 228), TextFormatFlags.NoPadding);
                // norte (en la proyección, el norte es −y)
                var (nx, ny) = (0 * ca - (-1) * sa, 0 * sa + (-1) * ca);
                float ncx = r.Right - 16 * k, ncy = r.Y + 18 * k, nl = 9 * k;
                var tip = new PointF(ncx + (float)nx * nl, ncy + (float)ny * nl);
                using (var np = new Pen(Color.FromArgb(210, 220, 224, 228), 1.6f * k) { EndCap = LineCap.ArrowAnchor }) g.DrawLine(np, ncx - (float)nx * nl * 0.6f, ncy - (float)ny * nl * 0.6f, tip.X, tip.Y);
                TextRenderer.DrawText(g, "N", fSmall, new Point((int)(tip.X + nx * 4 * k - 4 * k), (int)(tip.Y + ny * 4 * k - 7 * k)), Color.FromArgb(210, 220, 224, 228), TextFormatFlags.NoPadding);
            }
            g.Clip = oldClip;
        }

        PointF PointAtD(double d, Func<double, double, PointF> P)
        {
            int i = 1; while (i < D.Length - 1 && D[i] < d) i++;
            double seg = D[i] - D[i - 1], f = seg > 0 ? Math.Max(0, Math.Min(1, (d - D[i - 1]) / seg)) : 0;
            return P(X[i - 1] + (X[i] - X[i - 1]) * f, Y[i - 1] + (Y[i] - Y[i - 1]) * f);
        }

        // Perfil: los límites a lo largo del tramo y la velocidad del tren en la infracción (0 = la infracción).
        public void PaintProfile(Graphics g, Rectangle r, Font fSmall, Font fBold, float k)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bp = Theme.Round(r, (int)(8 * k))) using (var b = new SolidBrush(Bg)) g.FillPath(b, bp);
            double vmax = 40;
            foreach (var l in Limits) vmax = Math.Max(vmax, l.kmh);
            if (!double.IsNaN(InitKmh)) vmax = Math.Max(vmax, InitKmh);
            if (!double.IsNaN(TrainKmh)) vmax = Math.Max(vmax, TrainKmh);
            vmax *= 1.18;
            int left = (int)(34 * k), top = (int)(10 * k), bottom = (int)(20 * k), rightPad = (int)(10 * k);
            var a = new Rectangle(r.X + left, r.Y + top, r.Width - left - rightPad, r.Height - top - bottom);
            float Xd(double d) => a.X + (float)(d / Math.Max(1, Len) * a.Width);
            float Yv(double v) => a.Bottom - (float)(v / vmax * a.Height);
            var grid = Color.FromArgb(52, 58, 64); var txt = Color.FromArgb(150, 158, 166);
            double stepV = new[] { 20.0, 40, 50, 100 }.First(s => vmax / s <= 5);
            using (var gp = new Pen(grid, 1f))
                for (double v = stepV; v < vmax; v += stepV)
                {
                    g.DrawLine(gp, a.X, Yv(v), a.Right, Yv(v));
                    TextRenderer.DrawText(g, K(v), fSmall, new Rectangle(r.X, (int)Yv(v) - 8, left - (int)(5 * k), 16), txt, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            // la distancia, desde la infracción
            using (var tp = new Pen(grid, 1f))
                for (int km = -2; km <= 2; km++)
                {
                    double d = DInf + km * 1000;
                    if (d < -1 || d > Len + 1) continue;
                    float x = Xd(d);
                    g.DrawLine(tp, x, a.Y, x, a.Bottom);
                    string t = km == 0 ? "0" : (km > 0 ? "+" : "−") + Math.Abs(km) + " km";
                    TextRenderer.DrawText(g, t, fSmall, new Rectangle((int)x - 30, a.Bottom + (int)(3 * k), 60, (int)(14 * k)), txt, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                }
            // el exceso
            if (DEnd > DInf + 1) using (var ob = new SolidBrush(Color.FromArgb(46, Red))) g.FillRectangle(ob, Xd(DInf), a.Y, Math.Max(2, Xd(DEnd) - Xd(DInf)), a.Height);
            // los límites, en escalones
            var steps = new List<(double d, double kmh)>();
            if (!double.IsNaN(InitKmh)) steps.Add((0, InitKmh));
            steps.AddRange(Limits);
            for (int i = 0; i < steps.Count; i++)
            {
                double d0 = steps[i].d, d1 = i + 1 < steps.Count ? steps[i + 1].d : Len, v = steps[i].kmh;
                var col = LimitColor(v);
                using (var lp = new Pen(col, 2.4f * k)) g.DrawLine(lp, Xd(d0), Yv(v), Xd(d1), Yv(v));
                if (i + 1 < steps.Count) using (var vp = new Pen(Color.FromArgb(120, col), 1.2f * k)) g.DrawLine(vp, Xd(d1), Yv(v), Xd(d1), Yv(steps[i + 1].kmh));
                if (Xd(d1) - Xd(d0) > 22 * k)
                    TextRenderer.DrawText(g, K(v), fSmall, new Point((int)Xd(d0) + (int)(3 * k), (int)Yv(v) - (int)(14 * k)), col, TextFormatFlags.NoPadding);
            }
            if (steps.Count == 0)
                TextRenderer.DrawText(g, I18n.T("Sin postes de velocidad en este tramo"), fSmall, a, txt, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            // el tren en la infracción
            if (!double.IsNaN(TrainKmh))
            {
                float y = Yv(TrainKmh), x0 = Xd(DInf), x1 = Math.Max(x0 + 2, Xd(DEnd));
                using (var tp = new Pen(Red, 2f * k) { DashStyle = DashStyle.Dash }) g.DrawLine(tp, x0, y, x1, y);
                float rr = 4.5f * k;
                using (var rb = new SolidBrush(Red)) g.FillEllipse(rb, x0 - rr, y - rr, 2 * rr, 2 * rr);
                string t = K(TrainKmh);
                var sz = TextRenderer.MeasureText(g, t, fBold, Size.Empty, TextFormatFlags.NoPadding);
                int tx = (int)x0 - sz.Width - (int)(6 * k); if (tx < a.X) tx = (int)x1 + (int)(6 * k);
                TextRenderer.DrawText(g, t, fBold, new Point(tx, (int)y - sz.Height / 2), Color.FromArgb(255, 160, 160), TextFormatFlags.NoPadding);
            }
        }
    }
}
