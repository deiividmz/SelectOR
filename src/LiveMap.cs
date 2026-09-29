// Mapa en vivo: los demás usuarios de la comunidad SelectOR que conducen en la MISMA ruta, dibujados
// en el minimapa del HUD y en el mapa grande (flecha de color con su nombre). La ventana principal
// envía la posición propia y recibe la de los demás cada 5 s (live_update en Supabase); aquí se
// guarda ese estado y cada marca se desliza entre una lectura y la siguiente, como la del tren propio.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    // Otro usuario en el mapa, ya en su posición del momento (lo que dibujan los mapas).
    public sealed class LiveMarker
    {
        public string Id, Name, Short, Train, Company;
        public double Lat, Lon, Heading, SpeedKmh;
        public bool HasHeading;
        public Color Color;
    }

    // Lo que llega del servidor en cada consulta.
    public struct LiveRow
    {
        public string Id, Name, Train, Company;
        public double Lat, Lon, SpeedKmh;
        public double? Heading;
    }

    public sealed class LiveMates
    {
        sealed class Mate
        {
            public string Id, Name, Train, Company;
            public double FLat, FLon, TLat, TLon, FHdg, THdg, T0, Dur = 5, Speed;
            public bool HasHdg;
            public Color Color;
            public DateTime SeenUtc;
        }

        // Cada usuario conserva su color durante toda la conducción (por orden de aparición).
        public static readonly Color[] Palette =
        {
            Color.FromArgb(100, 160, 240), Color.FromArgb(240, 160, 80), Color.FromArgb(190, 130, 230),
            Color.FromArgb(235, 110, 150), Color.FromArgb(230, 200, 90), Color.FromArgb(80, 200, 220),
            Color.FromArgb(230, 110, 100), Color.FromArgb(170, 210, 90)
        };

        readonly Dictionary<string, Mate> _m = new();
        readonly Dictionary<string, Color> _colors = new();
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        int _next;

        public int Count => _m.Count;

        // Conducción nueva: sin nadie y con los colores por repartir.
        public void Reset() { _m.Clear(); _colors.Clear(); _next = 0; }

        public void Update(List<LiveRow> rows)
        {
            double now = _clock.Elapsed.TotalSeconds;
            var seen = new HashSet<string>();
            foreach (var r in rows)
            {
                if (string.IsNullOrEmpty(r.Id)) continue;
                seen.Add(r.Id);
                if (!_m.TryGetValue(r.Id, out var m))
                {
                    m = new Mate { Id = r.Id, FLat = r.Lat, FLon = r.Lon, TLat = r.Lat, TLon = r.Lon, T0 = now };
                    if (!_colors.TryGetValue(r.Id, out var c)) { c = Palette[_next++ % Palette.Length]; _colors[r.Id] = c; }
                    m.Color = c;
                    if (r.Heading.HasValue) { m.FHdg = m.THdg = r.Heading.Value; m.HasHdg = true; }
                    _m[r.Id] = m;
                }
                else
                {
                    var (la, lo, h) = Pos(m, now);
                    // Salto grande (cambio de tren, teletransporte): directo, sin deslizar.
                    bool jump = Haversine(la, lo, r.Lat, r.Lon) > 3000;
                    m.FLat = jump ? r.Lat : la; m.FLon = jump ? r.Lon : lo;
                    m.TLat = r.Lat; m.TLon = r.Lon;
                    m.Dur = Math.Max(1, Math.Min(8, now - m.T0)); m.T0 = now;
                    if (r.Heading.HasValue) { m.FHdg = m.HasHdg ? h : r.Heading.Value; m.THdg = r.Heading.Value; m.HasHdg = true; }
                }
                m.Name = string.IsNullOrWhiteSpace(r.Name) ? "?" : r.Name.Trim();
                m.Train = r.Train ?? ""; m.Company = r.Company; m.Speed = r.SpeedKmh;
                m.SeenUtc = DateTime.UtcNow;
            }
            // Quien no viene en la respuesta ya no está en la ruta (o dejó de conducir).
            foreach (var k in new List<string>(_m.Keys)) if (!seen.Contains(k)) _m.Remove(k);
        }

        // Sin respuesta del servidor: se mantienen hasta 30 s sin noticias y luego desaparecen.
        public void Expire()
        {
            var lim = DateTime.UtcNow.AddSeconds(-30);
            foreach (var k in new List<string>(_m.Keys)) if (_m[k].SeenUtc < lim) _m.Remove(k);
        }

        static (double lat, double lon, double hdg) Pos(Mate m, double now)
        {
            double u = Math.Max(0, Math.Min(1, (now - m.T0) / m.Dur));
            double d = ((m.THdg - m.FHdg) % 360 + 540) % 360 - 180;   // giro más corto
            return (m.FLat + (m.TLat - m.FLat) * u, m.FLon + (m.TLon - m.FLon) * u, (m.FHdg + d * u + 360) % 360);
        }

        // Posiciones del momento (se llama en cada fotograma del mapa).
        public List<LiveMarker> Current()
        {
            var list = new List<LiveMarker>(_m.Count);
            if (_m.Count == 0) return list;
            double now = _clock.Elapsed.TotalSeconds;
            foreach (var m in _m.Values)
            {
                var (la, lo, h) = Pos(m, now);
                list.Add(new LiveMarker
                {
                    Id = m.Id, Name = m.Name, Short = ShortName(m.Name), Train = m.Train, Company = m.Company,
                    Lat = la, Lon = lo, Heading = h, HasHeading = m.HasHdg, SpeedKmh = m.Speed, Color = m.Color
                });
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        // «Lucía Fernández» → «Lucía F.»; un solo nombre se deja tal cual (recortado si es muy largo).
        public static string ShortName(string n)
        {
            n = (n ?? "").Trim();
            var p = n.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string s = p.Length >= 2 && p[1].Length > 0 ? p[0] + " " + char.ToUpper(p[1][0]) + "." : n;
            return s.Length > 16 ? s.Substring(0, 15) + "…" : s;
        }

        public static double Haversine(double la1, double lo1, double la2, double lo2)
        {
            const double R = 6371000.0;
            double dLa = (la2 - la1) * Math.PI / 180.0, dLo = (lo2 - lo1) * Math.PI / 180.0;
            double a = Math.Sin(dLa / 2) * Math.Sin(dLa / 2) +
                       Math.Cos(la1 * Math.PI / 180) * Math.Cos(la2 * Math.PI / 180) * Math.Sin(dLo / 2) * Math.Sin(dLo / 2);
            return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }

        public static double Bearing(double la1, double lo1, double la2, double lo2)
        {
            double f1 = la1 * Math.PI / 180, f2 = la2 * Math.PI / 180, dl = (lo2 - lo1) * Math.PI / 180;
            double y = Math.Sin(dl) * Math.Cos(f2);
            double x = Math.Cos(f1) * Math.Sin(f2) - Math.Sin(f1) * Math.Cos(f2) * Math.Cos(dl);
            return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
        }
    }

    // Dibujo de los demás usuarios (lo usan el minimapa y el mapa grande).
    static class LiveMapDraw
    {
        const TextFormatFlags TF = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        // Flechas y etiquetas. detail=false (minimapa): solo el nombre corto; detail=true (mapa grande):
        // nombre, tren con velocidad (o «parado en …») y empresa de servicio o «Conducción libre».
        public static void Draw(Graphics g, Rectangle area, Func<double, double, PointF> proj, IReadOnlyList<LiveMarker> mates,
                                float arrowScale, bool detail, double[] stLat, double[] stLon, string[] stName)
        {
            if (mates == null || mates.Count == 0) return;
            float a = arrowScale * (detail ? 0.875f : 0.95f);
            var vis = RectangleF.Inflate(area, 20, 20);
            var pts = new PointF[mates.Count];
            for (int i = 0; i < mates.Count; i++)
            {
                pts[i] = proj(mates[i].Lat, mates[i].Lon);
                if (vis.Contains(pts[i])) Arrow(g, pts[i], mates[i], a);
            }
            // Etiquetas encima de todas las flechas.
            using var fN = Theme.Font(detail ? 9.5f : 7.5f, FontStyle.Bold);
            using var fS = Theme.Font(8.5f);
            var placed = new List<Rectangle>();   // etiquetas ya puestas (para no pisarse)
            for (int i = 0; i < mates.Count; i++)
            {
                if (!area.Contains(Point.Round(pts[i]))) continue;
                var m = mates[i];
                var lines = detail
                    ? new[] { m.Name, TrainLine(m, stLat, stLon, stName), CompanyLine(m.Company) }
                    : new[] { m.Short };
                Pill(g, area, pts[i], lines, m.Color, fN, fS, 12 * a + 6, placed);
            }
        }

        static void Arrow(Graphics g, PointF p, LiveMarker m, float a)
        {
            var st = g.Save();
            g.TranslateTransform(p.X, p.Y);
            if (m.HasHeading) g.RotateTransform((float)m.Heading);
            var tri = new[] { new PointF(0, -9 * a), new PointF(7 * a, 8 * a), new PointF(0, 4f * a), new PointF(-7 * a, 8 * a) };
            var c = m.Color;
            using (var halo = new SolidBrush(Color.FromArgb(62, c))) g.FillEllipse(halo, -12 * a, -12 * a, 24 * a, 24 * a);
            using (var b = new SolidBrush(c)) g.FillPolygon(b, tri);
            using (var pen = new Pen(Color.FromArgb((int)(c.R * 0.18), (int)(c.G * 0.18), (int)(c.B * 0.18)), 1.2f * a)) g.DrawPolygon(pen, tri);
            g.Restore(st);
        }

        public static string TrainLine(LiveMarker m, double[] stLat, double[] stLon, string[] stName)
        {
            string tren = string.IsNullOrWhiteSpace(m.Train) ? "" : Clip(m.Train.Trim(), 34);
            string mov;
            if (m.SpeedKmh < 2)
            {
                string est = NearStation(m.Lat, m.Lon, stLat, stLon, stName);
                mov = est != null ? string.Format(I18n.T("parado en {0}"), est) : I18n.T("parado");
            }
            else mov = Math.Round(m.SpeedKmh).ToString("0") + " km/h";
            return tren.Length > 0 ? tren + " · " + mov : mov;
        }

        public static string CompanyLine(string company) =>
            string.IsNullOrWhiteSpace(company) ? I18n.T("Conducción libre") : Clip(company.Trim(), 30) + " · " + I18n.T("de servicio");

        static string Clip(string s, int n) => s.Length > n ? s.Substring(0, n - 1) + "…" : s;

        static string NearStation(double lat, double lon, double[] stLat, double[] stLon, string[] stName)
        {
            if (stLat == null || stName == null) return null;
            double best = 400; string name = null;
            for (int i = 0; i < stLat.Length && i < stName.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(stName[i])) continue;
                double d = LiveMates.Haversine(lat, lon, stLat[i], stLon[i]);
                if (d < best) { best = d; name = stName[i]; }
            }
            return name;
        }

        // Etiqueta con borde del color del usuario junto a su flecha: a la derecha si cabe; si pisa otra
        // etiqueta, prueba a la izquierda y un poco más arriba o abajo.
        static void Pill(Graphics g, Rectangle area, PointF p, string[] lines, Color col, Font fN, Font fS, float gap, List<Rectangle> placed)
        {
            int padX = 8, padY = 4, bar = 3;
            int w = 0, h = 0;
            var hs = new int[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                var f = i == 0 ? fN : fS;
                var sz = TextRenderer.MeasureText(g, lines[i], f, Size.Empty, TF);
                w = Math.Max(w, sz.Width); hs[i] = sz.Height + (i == 0 ? 1 : 0); h += hs[i];
            }
            w += padX * 2 + bar; h += padY * 2;
            Rectangle Cand(bool right, int k)
            {
                int cx = right ? (int)(p.X + gap) : (int)(p.X - gap) - w;
                int cy = (int)(p.Y - h / 2f) + k * (h + 4);
                cx = Math.Max(area.Left + 2, Math.Min(area.Right - w - 2, cx));
                cy = Math.Max(area.Top + 2, Math.Min(area.Bottom - h - 2, cy));
                return new Rectangle(cx, cy, w, h);
            }
            bool Libre(Rectangle r) { foreach (var q in placed) if (Rectangle.Inflate(q, 3, 3).IntersectsWith(r)) return false; return true; }
            var rc = Cand(p.X + gap + w <= area.Right - 2, 0);
            if (!Libre(rc))
                foreach (var (right, k) in new[] { (false, 0), (true, 1), (true, -1), (false, 1), (false, -1), (true, 2), (true, -2) })
                {
                    var c = Cand(right, k);
                    if (Libre(c)) { rc = c; break; }
                }
            placed.Add(rc);
            int x = rc.X, y = rc.Y;
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(rc, 6))
            {
                using (var b = new SolidBrush(Color.FromArgb(235, 36, 39, 42))) g.FillPath(b, path);
                using (var pen = new Pen(col)) g.DrawPath(pen, path);
            }
            using (var b = new SolidBrush(col)) g.FillRectangle(b, rc.X + 1, rc.Y + 4, bar - 1, rc.Height - 8);
            g.Restore(st);
            int ty = y + padY;
            for (int i = 0; i < lines.Length; i++)
            {
                TextRenderer.DrawText(g, lines[i], i == 0 ? fN : fS, new Point(x + padX + bar, ty),
                    i == 0 ? Color.FromArgb(240, 242, 243) : Color.FromArgb(170, 176, 181), TF);
                ty += hs[i];
            }
        }

        // Aviso del minimapa: «N maquinistas en la ruta» (cuenta también a los que quedan fuera de la vista).
        public static void Chip(Graphics g, Rectangle area, int count)
        {
            if (count <= 0) return;
            string t = count == 1 ? I18n.T("1 maquinista en la ruta") : string.Format(I18n.T("{0} maquinistas en la ruta"), count);
            using var f = Theme.Font(7.5f, FontStyle.Bold);
            var sz = TextRenderer.MeasureText(g, t, f, Size.Empty, TF);
            var rc = new Rectangle(area.Right - sz.Width - 26, area.Top + 4, sz.Width + 20, sz.Height + 6);
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(rc, rc.Height / 2))
            {
                using (var b = new SolidBrush(Color.FromArgb(235, 36, 39, 42))) g.FillPath(b, path);
                using (var pen = new Pen(Color.FromArgb(76, 175, 80))) g.DrawPath(pen, path);
            }
            using (var b = new SolidBrush(Color.FromArgb(102, 197, 106))) g.FillEllipse(b, rc.X + 6, rc.Y + rc.Height / 2 - 3, 6, 6);
            g.Restore(st);
            TextRenderer.DrawText(g, t, f, new Point(rc.X + 15, rc.Y + 3), Color.FromArgb(220, 240, 222), TF);
        }

        // Leyenda del mapa grande (arriba a la derecha): tú y los demás usuarios de la ruta, con su color.
        // Devuelve la zona de cada fila para centrar el mapa en ese usuario al hacer clic.
        public static List<(Rectangle hit, LiveMarker m)> Legend(Graphics g, Rectangle area, string meName, string meCompany,
                                                                 IReadOnlyList<LiveMarker> mates, out Rectangle box)
        {
            var hits = new List<(Rectangle, LiveMarker)>();
            box = Rectangle.Empty;
            if (mates == null || mates.Count == 0) return hits;
            using var fT = Theme.Font(8.5f, FontStyle.Bold);
            using var fL = Theme.Font(9f, FontStyle.Bold);
            using var fl = Theme.Font(8.5f);
            int rowH = Math.Max(20, TextRenderer.MeasureText(g, "Ág", fL, Size.Empty, TF).Height + 6);
            int maxRows = Math.Max(1, (area.Height - 90) / rowH);
            var rows = new List<(string name, string status, Color c, LiveMarker m)>
                { (I18n.T("Tú") + (string.IsNullOrWhiteSpace(meName) ? "" : " (" + LiveMates.ShortName(meName) + ")"),
                   Status(meCompany), Color.FromArgb(120, 210, 150), null) };
            foreach (var m in mates) rows.Add((m.Short, Status(m.Company), m.Color, m));
            int extra = Math.Max(0, rows.Count - maxRows);
            if (extra > 0) rows.RemoveRange(maxRows, rows.Count - maxRows);

            string title = I18n.T("EN LA RUTA AHORA"), sub = I18n.T("Comunidad SelectOR"), foot = I18n.T("Clic en un nombre: centrar");
            if (extra > 0) foot = string.Format(I18n.T("y {0} más"), extra) + " · " + foot;
            int nameW = 0, stW = 0;
            foreach (var r in rows)
            {
                nameW = Math.Max(nameW, TextRenderer.MeasureText(g, r.name, fL, Size.Empty, TF).Width);
                stW = Math.Max(stW, TextRenderer.MeasureText(g, r.status, fl, Size.Empty, TF).Width);
            }
            int w = Math.Max(Math.Max(TextRenderer.MeasureText(g, foot, fl, Size.Empty, TF).Width, TextRenderer.MeasureText(g, title, fT, Size.Empty, TF).Width),
                             26 + nameW + 14 + stW) + 26;
            w = Math.Min(w, area.Width - 20);
            int hTitle = TextRenderer.MeasureText(g, title, fT, Size.Empty, TF).Height, hSub = TextRenderer.MeasureText(g, sub, fl, Size.Empty, TF).Height;
            int h = 10 + hTitle + 2 + hSub + 8 + rows.Count * rowH + 4 + hSub + 10;
            box = new Rectangle(area.Right - w - 8, area.Top + 8, w, h);
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(box, 10))
            {
                using (var b = new SolidBrush(Color.FromArgb(240, 36, 39, 42))) g.FillPath(b, path);
                using (var pen = new Pen(Color.FromArgb(58, 62, 66))) g.DrawPath(pen, path);
            }
            g.Restore(st);
            int x = box.X + 13, y = box.Y + 10;
            TextRenderer.DrawText(g, title, fT, new Point(x, y), Color.FromArgb(143, 224, 147), TF); y += hTitle + 2;
            TextRenderer.DrawText(g, sub, fl, new Point(x, y), Color.FromArgb(170, 176, 181), TF); y += hSub + 8;
            foreach (var r in rows)
            {
                var hit = new Rectangle(box.X + 4, y, box.Width - 8, rowH);
                if (r.m != null) hits.Add((hit, r.m));
                int cy = y + rowH / 2;
                var s2 = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(r.c)) g.FillEllipse(b, x, cy - 5, 10, 10);
                g.Restore(s2);
                int ty = cy - TextRenderer.MeasureText(g, "Ág", fL, Size.Empty, TF).Height / 2;
                TextRenderer.DrawText(g, r.name, fL, new Rectangle(x + 18, ty, nameW + 4, rowH), Color.FromArgb(240, 242, 243), TF | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, r.status, fl, new Rectangle(x + 18 + nameW + 14, ty + 1, box.Right - (x + 18 + nameW + 14) - 10, rowH),
                    Color.FromArgb(170, 176, 181), TF | TextFormatFlags.EndEllipsis);
                y += rowH;
            }
            TextRenderer.DrawText(g, foot, fl, new Point(x, y + 4), Color.FromArgb(125, 131, 136), TF);
            return hits;
        }

        static string Status(string company) =>
            string.IsNullOrWhiteSpace(company) ? I18n.T("Conducción libre") : Clip(company.Trim(), 30) + " · " + I18n.T("servicio");
    }
}
