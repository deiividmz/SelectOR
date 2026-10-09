// Dibujo del detalle del .tdb en el mapa del HUD (MapDetail.cs), igual en el mini-mapa y en el mapa grande. Cada
// capa sale a partir de un zoom (px por metro) para no llenar el mapa: la vía siempre (de un solo color; sustituye
// al trazado antiguo); los PK desde un zoom medio; andenes y apartaderos un poco más cerca; cruces y puntos de
// carga más cerca aún; los nombres de los apartaderos y las toperas, con zoom de estación.
// Colores: la vía en gris medio y cada elemento en un tono propio (andén azul liso, apartadero violeta rayado,
// punto de carga magenta, cruce blanco); el ÁMBAR queda solo para el itinerario de la hoja de ruta, que va encima.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    static class MapDetailDraw
    {
        public static readonly Color TrackCol = Color.FromArgb(150, 158, 166);
        public static readonly Color PlatformCol = Color.FromArgb(70, 150, 230), PlatformEdge = Color.FromArgb(205, 228, 250);
        public static readonly Color SidingCol = Color.FromArgb(225, 176, 138, 240), SidingText = Color.FromArgb(222, 205, 252);
        public static readonly Color DiamondCol = Color.FromArgb(240, 242, 244);
        public static readonly Color PickupCol = Color.FromArgb(214, 92, 160);
        public static readonly Color EndCol = Color.FromArgb(200, 210, 214);
        public static readonly Color LabelBg = Color.FromArgb(232, 26, 29, 33);
        public const double PkZoom = 0.06, PlatformZoom = 0.1, SidingZoom = 0.1, PointZoom = 0.15, NameZoom = 0.25, EndZoom = 0.25;

        const double PlatformWidthM = 5.0, PlatformEdgeM = 1.75;   // anchura del andén y distancia de su borde al eje de la vía

        // La polilínea desplazada en paralelo: d > 0 a la IZQUIERDA del sentido de sus puntos (en pantalla, con la y hacia
        // abajo, la izquierda de (dx, dy) es (dy, −dx)), d < 0 a la derecha.
        static PointF[] Offset(PointF[] p, float d)
        {
            var r = new PointF[p.Length];
            for (int i = 0; i < p.Length; i++)
            {
                float nx = 0, ny = 0;
                if (i > 0) { float dx = p[i].X - p[i - 1].X, dy = p[i].Y - p[i - 1].Y, n = (float)Math.Sqrt(dx * dx + dy * dy); if (n > 0.001f) { nx += dy / n; ny -= dx / n; } }
                if (i + 1 < p.Length) { float dx = p[i + 1].X - p[i].X, dy = p[i + 1].Y - p[i].Y, n = (float)Math.Sqrt(dx * dx + dy * dy); if (n > 0.001f) { nx += dy / n; ny -= dx / n; } }
                float m = (float)Math.Sqrt(nx * nx + ny * ny);
                if (m < 0.001f) { r[i] = p[i]; continue; }
                nx /= m; ny /= m;
                r[i] = new PointF(p[i].X + nx * d, p[i].Y + ny * d);
            }
            return r;
        }

        static bool In(RectangleF vis, PointF p) => p.X >= vis.Left && p.X <= vis.Right && p.Y >= vis.Top && p.Y <= vis.Bottom;

        // Debajo de estaciones y trenes: apartaderos, andenes, vía, cruces, puntos de carga, toperas y PK.
        public static void Under(Graphics g, Rectangle area, Func<double, double, PointF> proj, HudMapDetail d, double pxPerM, float scale)
        {
            if (d == null) return;
            var vis = RectangleF.Inflate(area, 12, 12);
            bool Box(HudMapDetail.Line l)
            {
                var c1 = proj(l.MinLa, l.MinLo); var c2 = proj(l.MaxLa, l.MaxLo);
                return vis.IntersectsWith(RectangleF.FromLTRB(Math.Min(c1.X, c2.X) - 1, Math.Min(c1.Y, c2.Y) - 1, Math.Max(c1.X, c2.X) + 1, Math.Max(c1.Y, c2.Y) + 1));
            }
            // En pantalla, sin los puntos que caen a menos de 0,8 px del anterior (con toda la ruta a la vista, una vía de
            // cientos de vértices se queda en unos pocos): el trazo es el mismo y se dibuja mucho más deprisa.
            var buf = new List<PointF>(256);
            PointF[] Pts(HudMapDetail.Line l)
            {
                buf.Clear();
                int n = l.Lat.Length;
                PointF last = default;
                for (int i = 0; i < n; i++)
                {
                    var p = proj(l.Lat[i], l.Lon[i]);
                    if (i == 0 || i == n - 1) { buf.Add(p); last = p; continue; }
                    float dx = p.X - last.X, dy = p.Y - last.Y;
                    if (dx * dx + dy * dy >= 0.64f) { buf.Add(p); last = p; }
                }
                if (buf.Count == 1) buf.Add(buf[0]);
                return buf.ToArray();
            }
            // En el mini-mapa (pocos cientos de píxeles) todo un poco más fino, para que no se amontone.
            bool compact = area.Width < 420;
            float k = Math.Max(1f, scale * 0.6f) * (compact ? 0.72f : 1f);
            float w = Math.Max(compact ? 2.2f : 2.6f, 1.9f * scale);   // grosor de la vía

            // apartaderos: banda violeta RAYADA bajo la vía (se distingue del andén, que es liso)
            var sidingMid = new List<(PointF p, string name)>();
            if (pxPerM >= SidingZoom)
                using (var pen = new Pen(SidingCol, w + 5f * k) { LineJoin = LineJoin.Round, DashPattern = new[] { 0.7f, 0.45f } })
                    foreach (var l in d.Sidings)
                    {
                        if (!Box(l)) continue;
                        var p = Pts(l);
                        try { g.DrawLines(pen, p); } catch { }
                        sidingMid.Add((p[p.Length / 2], l.Name));
                    }
            // andenes / apeaderos: losa azul LISA de extremos rectos con borde claro, AL LADO de la vía en que está (lo dice
            // el objeto del mundo); con zoom, con su anchura real (unos 5 m, con el borde a 1,75 m del eje de la vía). Si
            // no se sabe el lado, centrada en la vía, como antes.
            if (pxPerM >= PlatformZoom)
            {
                float pwSide = (float)Math.Clamp(PlatformWidthM * pxPerM, compact ? 3.0 : 4.0, 40.0);
                float pwMid = (float)Math.Clamp(6.0 * pxPerM, compact ? 6.0 : 9.0, 16.0) * k;
                float offSide = (float)Math.Max(w / 2 + pwSide / 2 + 1.5, (PlatformEdgeM + PlatformWidthM / 2) * pxPerM);
                using var edgeS = new Pen(PlatformEdge, pwSide + 2f) { StartCap = LineCap.Flat, EndCap = LineCap.Flat, LineJoin = LineJoin.Round };
                using var penS = new Pen(PlatformCol, pwSide) { StartCap = LineCap.Flat, EndCap = LineCap.Flat, LineJoin = LineJoin.Round };
                using var edgeM = new Pen(PlatformEdge, pwMid + 3f) { StartCap = LineCap.Square, EndCap = LineCap.Square, LineJoin = LineJoin.Round };
                using var penM = new Pen(PlatformCol, pwMid) { StartCap = LineCap.Square, EndCap = LineCap.Square, LineJoin = LineJoin.Round };
                foreach (var l in d.Platforms)
                {
                    if (!Box(l)) continue;
                    try
                    {
                        var p = Pts(l);
                        if (l.Side == 0) { g.DrawLines(edgeM, p); g.DrawLines(penM, p); continue; }
                        if ((l.Side & 1) != 0) { var q = Offset(p, offSide); g.DrawLines(edgeS, q); g.DrawLines(penS, q); }
                        if ((l.Side & 2) != 0) { var q = Offset(p, -offSide); g.DrawLines(edgeS, q); g.DrawLines(penS, q); }
                    }
                    catch { }
                }
            }
            // vía (toda del mismo color)
            using (var pT = TrackPen(w))
                foreach (var l in d.Track)
                {
                    if (!Box(l)) continue;
                    try { g.DrawLines(pT, Pts(l)); } catch { }
                }

            if (pxPerM >= PointZoom)
            {
                // cruces de vía sin desvío: rombo blanco con borde oscuro
                using (var b = new SolidBrush(DiamondCol)) using (var pen = new Pen(Color.FromArgb(40, 44, 48), 1.2f))
                    foreach (var (la, lo) in d.Diamonds)
                    {
                        var p = proj(la, lo); if (!In(vis, p)) continue;
                        Diamond(g, b, pen, p, 4.6f * k);
                    }
                // puntos de carga: cuadrado magenta con una «C»
                foreach (var (la, lo) in d.Pickups)
                {
                    var p = proj(la, lo); if (!In(vis, p)) continue;
                    Pickup(g, p, k, pxPerM >= NameZoom && !compact);
                }
            }
            using var fS = Theme.Font(7.8f * Math.Max(1f, scale * 0.55f) * (compact ? 0.9f : 1f), FontStyle.Bold);
            // toperas (zoom de estación)
            if (pxPerM >= EndZoom)
                using (var b = new SolidBrush(EndCol)) using (var pen = new Pen(Color.FromArgb(40, 44, 48), 1f))
                    foreach (var (la, lo) in d.Ends)
                    {
                        var p = proj(la, lo); if (!In(vis, p)) continue;
                        float r = 3f * k;
                        g.FillRectangle(b, p.X - r, p.Y - r, 2 * r, 2 * r); g.DrawRectangle(pen, p.X - r, p.Y - r, 2 * r, 2 * r);
                    }
            // nombres de los apartaderos: etiqueta oscura con borde violeta (sin solaparse)
            if (pxPerM >= NameZoom)
            {
                var drawn = new List<RectangleF>();
                foreach (var (p, name) in sidingMid) if (In(vis, p)) Label(g, fS, p, name, SidingText, SidingCol, drawn);
                foreach (var (la, lo, name) in d.SidingNames) { var p = proj(la, lo); if (In(vis, p)) Label(g, fS, p, name, SidingText, SidingCol, drawn); }
            }
            // PK: un rótulo cada cierta distancia en pantalla (los hitos van cada kilómetro, o cada hectómetro)
            if (pxPerM >= PkZoom)
            {
                var drawn = new List<PointF>();
                float minGap = 70f * Math.Max(1f, scale * 0.55f);
                using var fPk = Theme.Font(7.5f * Math.Max(1f, scale * 0.55f) * (compact ? 0.9f : 1f));
                using var dot = new SolidBrush(Color.FromArgb(185, 190, 195));
                var ci = I18n.English ? System.Globalization.CultureInfo.GetCultureInfo("en-GB") : System.Globalization.CultureInfo.GetCultureInfo("es-ES");
                foreach (var (la, lo, v) in d.Pk)
                {
                    var p = proj(la, lo); if (!In(vis, p)) continue;
                    bool near = false; foreach (var q in drawn) if (Math.Abs(q.X - p.X) < minGap && Math.Abs(q.Y - p.Y) < minGap * 0.5f) { near = true; break; }
                    if (near) continue;
                    drawn.Add(p);
                    g.FillEllipse(dot, p.X - 2.2f, p.Y - 2.2f, 4.4f, 4.4f);
                    string t = "PK " + v.ToString(Math.Abs(v - Math.Round(v)) < 0.05 ? "0" : "0.#", ci);
                    var at = new Point((int)p.X + 5, (int)p.Y - fPk.Height - 1);
                    TextRenderer.DrawText(g, t, fPk, new Point(at.X + 1, at.Y + 1), Color.FromArgb(10, 10, 10), TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(g, t, fPk, at, Color.FromArgb(200, 205, 210), TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                }
            }
        }

        // La vía del HUD (su color y su grosor) para los mapas que solo enseñan la vía (Ruta, «Mapa» de Conducción libre,
        // Actividad y Horarios): el resto del detalle no se dibuja.
        public static float TrackWidth(Rectangle area, float scale) => Math.Max(area.Width < 420 ? 2.2f : 2.6f, 1.9f * scale);
        public static Pen TrackPen(float w) => new Pen(TrackCol, w) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

        public static void Track(Graphics g, Rectangle area, Func<double, double, PointF> proj, HudMapDetail d, float scale)
        {
            if (d == null) return;
            var vis = RectangleF.Inflate(area, 12, 12);
            var buf = new List<PointF>(256);
            using var pen = TrackPen(TrackWidth(area, scale));
            foreach (var l in d.Track)
            {
                var c1 = proj(l.MinLa, l.MinLo); var c2 = proj(l.MaxLa, l.MaxLo);
                if (!vis.IntersectsWith(RectangleF.FromLTRB(Math.Min(c1.X, c2.X) - 1, Math.Min(c1.Y, c2.Y) - 1, Math.Max(c1.X, c2.X) + 1, Math.Max(c1.Y, c2.Y) + 1))) continue;
                buf.Clear();
                PointF last = default;
                for (int i = 0, n = l.Lat.Length; i < n; i++)
                {
                    var p = proj(l.Lat[i], l.Lon[i]);
                    if (i == 0 || i == n - 1) { buf.Add(p); last = p; continue; }
                    float dx = p.X - last.X, dy = p.Y - last.Y;
                    if (dx * dx + dy * dy >= 0.64f) { buf.Add(p); last = p; }
                }
                if (buf.Count == 1) buf.Add(buf[0]);
                try { g.DrawLines(pen, buf.ToArray()); } catch { }
            }
        }

        // Etiqueta legible sobre el mapa: fondo oscuro, borde del color del elemento y texto claro. No se pinta si
        // tapa otra ya pintada (lista «drawn»).
        public static void Label(Graphics g, Font f, PointF p, string text, Color fg, Color accent, List<RectangleF> drawn)
        {
            var sz = TextRenderer.MeasureText(text, f, Size.Empty, TextFormatFlags.NoPadding);
            var r = new RectangleF(p.X + 7, p.Y + 4, sz.Width + 10, sz.Height + 4);
            if (drawn != null)
            {
                foreach (var q in drawn) if (q.IntersectsWith(RectangleF.Inflate(r, 3, 2))) return;
                drawn.Add(r);
            }
            var rr = Rectangle.Round(r);
            using (var path = Theme.Round(rr, 5))
            {
                using (var b = new SolidBrush(LabelBg)) g.FillPath(b, path);
                using (var pen = new Pen(Color.FromArgb(255, accent), 1.2f)) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, text, f, rr, fg, TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        public static void Diamond(Graphics g, Brush b, Pen pen, PointF p, float r)
        {
            var pts = new[] { new PointF(p.X, p.Y - r), new PointF(p.X + r, p.Y), new PointF(p.X, p.Y + r), new PointF(p.X - r, p.Y) };
            g.FillPolygon(b, pts); g.DrawPolygon(pen, pts);
        }

        public static void Pickup(Graphics g, PointF p, float k, bool letter)
        {
            float r = (letter ? 6f : 3.6f) * k;
            var rc = new RectangleF(p.X - r, p.Y - r, 2 * r, 2 * r);
            using (var path = Theme.Round(Rectangle.Round(rc), Math.Max(2, (int)(r * 0.5f))))
            using (var b = new SolidBrush(PickupCol)) using (var pen = new Pen(Color.FromArgb(250, 230, 242), 1f))
            { g.FillPath(b, path); g.DrawPath(pen, path); }
            if (!letter) return;
            using var f = Theme.Font(6.5f * k, FontStyle.Bold);
            TextRenderer.DrawText(g, "C", f, Rectangle.Round(rc), Color.White, TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
