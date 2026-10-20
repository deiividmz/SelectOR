// Mapa en vivo: los demás usuarios de la comunidad SelectOR que conducen en la MISMA ruta, dibujados
// en el minimapa del HUD y en el mapa grande (flecha de color con su nombre). La ventana principal
// envía la posición propia y recibe la de los demás cada 5 s (live_update en Supabase); aquí se
// guarda ese estado y cada marca se desliza entre una lectura y la siguiente, como la del tren propio.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    // Otro usuario en el mapa, ya en su posición del momento (lo que dibujan los mapas).
    public sealed class LiveMarker
    {
        public string Id, Name, Short, Train, Company;
        public double Lat, Lon, Heading, SpeedKmh;
        public bool HasHeading, Me;
        public Color Color;
        public bool Room;          // jugador de la sala de Servidores SelectOR (su nombre en Open Rails)
        public string Status;      // lo que dice la leyenda a su lado (si no, la empresa o «Conducción libre»)
        public string Role;        // en una sala: su papel (despachador, ayudante, jugador), en su tarjeta del mapa
        public double LengthM;     // largo del tren (0: no se sabe; el mapa grande lo dibuja de unos 80 m)
        public List<(double lat, double lon)> Body;   // el tren por la vía, de la cola a la cabeza (en una sala; si no, se calcula)
    }

    // Lo que llega del servidor en cada consulta.
    public struct LiveRow
    {
        public string Id, Name, Train, Company;
        public double Lat, Lon, SpeedKmh;
        public double? Heading, AgeS;   // AgeS: segundos desde que el conductor la envió
        public bool IsMe;
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
        const TextFormatFlags TF = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
                                   | TextFormatFlags.PreserveGraphicsClipping;   // el texto respeta el marco del mapa (TextRenderer no lo hace si no)

        // Cómo se dibujan los trenes en cada mapa (1.2.72):
        //  · Mini (minimapa del HUD): tu tren, una flecha de navegador; los demás, fichas redondas pequeñas, sin nombres;
        //  · Big (mapa grande del HUD): el tren a escala por la vía (su largo y sus coches); muy alejado, la ficha redonda;
        //  · Route (pestaña «Ruta»): fichas redondas con la foto del maquinista (si la enseña: MapPhotos) o sus iniciales.
        public enum TrainStyle { Big, Mini, Route }
        public static double MyTrainLengthM;   // largo de tu tren (lo pone SelectOR al lanzar: TrainSpec.LengthM)
        public static readonly Color MyColor = Color.FromArgb(120, 210, 150);

        // Trenes y etiquetas. detail=false (minimapa): sin etiquetas; detail=true (mapa grande, «Ruta»):
        // nombre, tren con velocidad (o «parado en …») y empresa de servicio o «Conducción libre».
        public static void Draw(Graphics g, Rectangle area, Func<double, double, PointF> proj, IReadOnlyList<LiveMarker> mates,
                                float arrowScale, bool detail, double[] stLat, double[] stLon, string[] stName,
                                float locoA = 0, bool names = true,   // locoA: escala de los trenes según el zoom (0 = la de siempre)
                                Func<double, double, double?> trackAxis = null,   // rumbo de la vía en un punto (los que no tienen rumbo)
                                TrainStyle style = TrainStyle.Big)
        {
            if (mates == null || mates.Count == 0) return;
            if (style != TrainStyle.Mini) MapPhotos.Want(mates);
            float a = arrowScale * (detail ? 0.875f : 0.95f), k = locoA > 0 ? locoA : a;
            var vis = RectangleF.Inflate(area, 20, 20);
            var pts = new PointF[mates.Count];
            var placed = new List<Rectangle>();   // trenes y etiquetas ya puestos (para no pisarse)
            for (int i = 0; i < mates.Count; i++)
            {
                var m = mates[i];
                pts[i] = proj(m.Lat, m.Lon);
                if (!vis.Contains(pts[i])) continue;
                double? ax = m.HasHeading ? null : trackAxis?.Invoke(m.Lat, m.Lon);
                RectangleF r = style switch
                {
                    TrainStyle.Mini => Ficha(g, pts[i], m.HasHeading, m.Heading, m.Color, null, null, Math.Max(4.5f, Math.Min(6.5f, 5f * k))),
                    TrainStyle.Route => Ficha(g, pts[i], m.HasHeading, m.Heading, m.Color, Initials(m.Name), MapPhotos.Get(m), Math.Max(9f, Math.Min(14f, 10.5f * k))),
                    _ => Scaled(g, proj, m.Lat, m.Lon, m.HasHeading, m.Heading, m.LengthM, m.Body, trackAxis, m.Color, Initials(m.Name), MapPhotos.Get(m), k),
                };
                placed.Add(Rectangle.Round(r));
            }
            if (style == TrainStyle.Mini) return;   // (el minimapa, sin etiquetas: no caben)
            // Etiquetas encima de todos los trenes.
            using var fN = Theme.Font(detail ? 9.5f : 7.5f, FontStyle.Bold);
            using var fS = Theme.Font(8.5f);
            for (int i = 0; i < mates.Count; i++)
            {
                if (!area.Contains(Point.Round(pts[i]))) continue;
                var m = mates[i];
                var lines = detail
                    ? new[] { m.Name, TrainLine(m, stLat, stLon, stName), m.Room ? (m.Role ?? I18n.T("Jugador de la sala")) : CompanyLine(m.Company) }
                    : new[] { m.Short };
                Pill(g, area, pts[i], lines, m.Color, fN, fS, 12 * a + 6, placed);
            }
        }

        // Locomotora vista desde arriba (la posición de un tren en los mapas): el cuerpo a lo largo de la marcha, el morro
        // en punta hacia donde va (heading: grados desde el norte, en sentido horario; sin rumbo, hacia el este) y acabando en
        // el punto (la cabeza del tren), del color
        // del tren, con la ventanilla de la cabina y, si se da, el nombre dentro (el cuerpo se alarga para que quepa; el
        // texto, siempre derecho). Devuelve lo que ocupa en la pantalla.
        // Sin rumbo (no se sabe hacia dónde va): sin morro, con cabina en los dos extremos, centrada y a lo largo de la vía
        // si se sabe (axis: rumbo de la vía en ese punto, sin sentido); si tampoco, tumbada.
        public static RectangleF Loco(Graphics g, PointF p, bool hasHeading, double heading, Color c, string name, float a, double? axis = null)
        {
            float W = 7.5f * a, N = hasHeading ? W * 0.72f : 0, cab = W * 0.55f, pad = W * 0.32f;
            int cabs = hasHeading ? 1 : 2;
            string txt = string.IsNullOrWhiteSpace(name) ? null : Clip(name.Trim(), 14);
            using var font = new Font(Theme.Font(9f).FontFamily, Math.Max(8f, W * 0.66f), FontStyle.Bold, GraphicsUnit.Pixel);
            float tw = 0;
            if (txt != null) tw = g.MeasureString(txt, font, PointF.Empty, StringFormat.GenericTypographic).Width;
            float body = Math.Max(13f * a, txt != null ? tw + 2 * pad + cabs * cab : 0);   // sin el morro
            // con rumbo, el morro acaba justo en el punto (la cabeza del tren: ahí empieza su camino por delante); sin rumbo, centrada
            float L = body + N, x0 = hasHeading ? -L : -L / 2, xn = x0 + body;
            double ang = (hasHeading ? heading : axis ?? 90) - 90;
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // pequeña (mapa alejado): un círculo de su color alrededor, para que se vea entre las vías
            if (W <= 16f && txt == null)
            {
                var (ccx, ccy) = hasHeading ? ((float)(p.X - Math.Sin(heading * Math.PI / 180) * L / 2), (float)(p.Y + Math.Cos(heading * Math.PI / 180) * L / 2)) : (p.X, p.Y);
                float rr = Math.Max(L, W) * 0.62f + 4;
                using (var fb = new SolidBrush(Color.FromArgb(70, c))) g.FillEllipse(fb, ccx - rr, ccy - rr, 2 * rr, 2 * rr);
                using (var rp = new Pen(Color.FromArgb(230, c), 2f)) g.DrawEllipse(rp, ccx - rr, ccy - rr, 2 * rr, 2 * rr);
                using (var rp2 = new Pen(Color.FromArgb(200, 15, 17, 19), 1f)) g.DrawEllipse(rp2, ccx - rr - 1.5f, ccy - rr - 1.5f, 2 * rr + 3, 2 * rr + 3);
            }
            g.TranslateTransform(p.X, p.Y);
            g.RotateTransform((float)ang);
            float r = W * 0.28f;
            using var path = new GraphicsPath();
            path.AddArc(x0, -W / 2, 2 * r, 2 * r, 180, 90);
            if (hasHeading)
            {
                path.AddLine(x0 + r, -W / 2, xn, -W / 2);
                path.AddLine(xn, -W / 2, xn + N, 0);
                path.AddLine(xn + N, 0, xn, W / 2);
                path.AddLine(xn, W / 2, x0 + r, W / 2);
            }
            else
            {
                path.AddLine(x0 + r, -W / 2, xn - r, -W / 2);
                path.AddArc(xn - 2 * r, -W / 2, 2 * r, 2 * r, 270, 90);
                path.AddArc(xn - 2 * r, W / 2 - 2 * r, 2 * r, 2 * r, 0, 90);
                path.AddLine(xn - r, W / 2, x0 + r, W / 2);
            }
            path.AddArc(x0, W / 2 - 2 * r, 2 * r, 2 * r, 90, 90);
            path.CloseFigure();
            using (var halo = new Pen(Color.FromArgb(60, c), Math.Min(W * 0.35f, 5f)) { LineJoin = LineJoin.Round }) g.DrawPath(halo, path);
            using (var b = new SolidBrush(c)) g.FillPath(b, path);
            var dark = Color.FromArgb(235, (int)(c.R * 0.16), (int)(c.G * 0.16), (int)(c.B * 0.16));
            using (var pen = new Pen(dark, Math.Max(1.1f, 0.65f * a)) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
            // ventanilla de la cabina, junto al morro (sin rumbo: una en cada extremo)
            using (var wb = new SolidBrush(Color.FromArgb(150, dark)))
            {
                g.FillRectangle(wb, xn - cab + W * 0.08f, -W * 0.3f, cab - W * 0.2f, W * 0.6f);
                if (!hasHeading) g.FillRectangle(wb, x0 + W * 0.12f, -W * 0.3f, cab - W * 0.2f, W * 0.6f);
            }
            if (txt != null)
            {
                float cx = hasHeading ? x0 + pad + (body - cab - 2 * pad) / 2 + pad * 0.1f : x0 + body / 2;
                var ts = g.Save();
                g.TranslateTransform(cx, 0);
                double norm = ((ang % 360) + 360) % 360;
                if (norm > 90 && norm < 270) g.RotateTransform(180);   // (que no quede boca abajo)
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using (var tb = new SolidBrush(dark)) g.DrawString(txt, font, tb, new PointF(0, W * 0.04f), sf);
                g.Restore(ts);
            }
            g.Restore(st);
            // lo que ocupa (las cuatro esquinas, giradas)
            double rad = ang * Math.PI / 180, co = Math.Cos(rad), si = Math.Sin(rad);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var (lx, ly) in new[] { (x0, -W / 2), (x0 + L, -W / 2), (x0, W / 2), (x0 + L, W / 2) })
            {
                float sx = p.X + (float)(lx * co - ly * si), sy = p.Y + (float)(lx * si + ly * co);
                minX = Math.Min(minX, sx); maxX = Math.Max(maxX, sx); minY = Math.Min(minY, sy); maxY = Math.Max(maxY, sy);
            }
            return RectangleF.FromLTRB(minX, minY, maxX, maxY);
        }

        // Iniciales para la ficha: las dos primeras palabras («Ana Ruiz» → AR; «DavidMZP» → DA).
        public static string Initials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var parts = name.Trim().Split(new[] { ' ', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries);
            string s = parts.Length >= 2 ? $"{parts[0][0]}{parts[1][0]}" : parts[0].Length >= 2 ? parts[0].Substring(0, 2) : parts[0];
            return s.ToUpperInvariant();
        }

        static Color Dark(Color c) => Color.FromArgb(235, (int)(c.R * 0.18), (int)(c.G * 0.18), (int)(c.B * 0.18));

        // Ficha redonda (r: radio en px): del color del tren, con la foto del maquinista (recortada en círculo) o sus
        // iniciales, aro blanco y una muesca fuera del círculo hacia donde va (sin rumbo, sin muesca).
        public static RectangleF Ficha(Graphics g, PointF p, bool hasHeading, double heading, Color c, string initials, Image photo, float r)
        {
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (hasHeading)
            {
                var tr = g.Save();
                g.TranslateTransform(p.X, p.Y); g.RotateTransform((float)heading);
                float tip = r + Math.Max(6.5f, r * 0.8f), half = Math.Max(4f, r * 0.66f);
                var notch = new[] { new PointF(-half, -r * 0.7f), new PointF(0, -tip), new PointF(half, -r * 0.7f) };
                using (var b = new SolidBrush(c)) g.FillPolygon(b, notch);
                using (var pen = new Pen(Dark(c), Math.Max(1f, r * 0.1f)) { LineJoin = LineJoin.Round }) g.DrawPolygon(pen, notch);
                g.Restore(tr);
            }
            var box = new RectangleF(p.X - r, p.Y - r, 2 * r, 2 * r);
            using (var b = new SolidBrush(c)) g.FillEllipse(b, box);
            if (photo != null && r >= 7)
            {
                using var clip = new GraphicsPath();
                var inner = RectangleF.Inflate(box, -Math.Max(1.5f, r * 0.16f), -Math.Max(1.5f, r * 0.16f));
                clip.AddEllipse(inner);
                var cs = g.Save();
                g.SetClip(clip, CombineMode.Intersect);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(photo, inner);
                g.Restore(cs);
            }
            else if (!string.IsNullOrEmpty(initials) && r >= 7)
            {
                using var f = new Font(Theme.Font(9f).FontFamily, Math.Max(7f, r * 0.82f), FontStyle.Bold, GraphicsUnit.Pixel);
                using var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var tb = new SolidBrush(Dark(c));
                g.DrawString(initials, f, tb, new PointF(p.X, p.Y + r * 0.04f), sf);
            }
            using (var pen = new Pen(Color.White, Math.Max(1.2f, r * 0.14f))) g.DrawEllipse(pen, box);
            using (var pen = new Pen(Color.FromArgb(200, 15, 17, 19), 1f)) g.DrawEllipse(pen, RectangleF.Inflate(box, Math.Max(1.2f, r * 0.14f) / 2 + 0.6f, Math.Max(1.2f, r * 0.14f) / 2 + 0.6f));
            g.Restore(st);
            return RectangleF.Inflate(box, Math.Max(4.5f, r * 0.75f), Math.Max(4.5f, r * 0.75f));
        }

        // Flecha de navegador (tu tren en el minimapa): punta hacia donde va, borde blanco y un halo de su color; sin rumbo,
        // a lo largo de la vía (axis) o, si tampoco, un punto.
        public static RectangleF NavArrow(Graphics g, PointF p, bool hasHeading, double heading, Color c, float a, double? axis = null)
        {
            float s = 7f * a;
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(p.X, p.Y);
            using (var halo = new SolidBrush(Color.FromArgb(60, c))) g.FillEllipse(halo, -1.8f * s, -1.8f * s, 3.6f * s, 3.6f * s);
            if (!hasHeading && axis == null)
            {
                using (var b = new SolidBrush(c)) g.FillEllipse(b, -0.8f * s, -0.8f * s, 1.6f * s, 1.6f * s);
                using (var pen = new Pen(Color.White, Math.Max(1.6f, s * 0.24f))) g.DrawEllipse(pen, -0.8f * s, -0.8f * s, 1.6f * s, 1.6f * s);
            }
            else
            {
                g.RotateTransform((float)(hasHeading ? heading : axis.Value));
                var tri = new[] { new PointF(0, -1.35f * s), new PointF(1f * s, 1f * s), new PointF(0, 0.5f * s), new PointF(-1f * s, 1f * s) };
                using (var b = new SolidBrush(c)) g.FillPolygon(b, tri);
                using (var pen = new Pen(Color.White, Math.Max(1.6f, s * 0.24f)) { LineJoin = LineJoin.Round }) g.DrawPolygon(pen, tri);
            }
            g.Restore(st);
            return new RectangleF(p.X - 1.8f * s, p.Y - 1.8f * s, 3.6f * s, 3.6f * s);
        }

        // Tren a escala (mapa grande): su cuerpo por la vía (body, de la cola a la cabeza; si no se sabe, se sigue la vía
        // hacia atrás desde la cabeza con su largo) en coches de unos 20 m, la cabeza del color del tren con la punta hacia
        // donde va. Si en pantalla mide menos de 34 px (mapa alejado: no se vería) o no se sabe hacia dónde va, la ficha redonda.
        public static RectangleF Scaled(Graphics g, Func<double, double, PointF> proj, double lat, double lon, bool hasHeading, double heading,
                                        double lengthM, List<(double lat, double lon)> body, Func<double, double, double?> trackAxis,
                                        Color c, string initials, Image photo, float k)
        {
            var p = proj(lat, lon);
            double L = lengthM > 5 ? lengthM : 80;
            var ll = body != null && body.Count >= 2 ? body : hasHeading ? WalkBack(lat, lon, heading, L, trackAxis) : null;
            if (ll != null)
            {
                var px = ll.Select(q => proj(q.lat, q.lon)).ToList();
                float len = 0; for (int i = 1; i < px.Count; i++) len += Dist(px[i - 1], px[i]);
                if (len >= 34) return ScaleTrain(g, px, len, L, c);
            }
            return Ficha(g, p, hasHeading, heading, c, initials, photo, Math.Max(8f, Math.Min(14f, 10f * k)));
        }

        static float Dist(PointF a, PointF b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        // Desde la cabeza, hacia atrás por la vía (cada 5 m, girando como mucho 25° por paso hacia el rumbo de la vía más
        // cercana); sin vía cerca, en línea recta. De la cola a la cabeza.
        static List<(double lat, double lon)> WalkBack(double lat, double lon, double heading, double L, Func<double, double, double?> axis)
        {
            var res = new List<(double lat, double lon)> { (lat, lon) };
            double b = (heading + 180) % 360, la = lat, lo = lon;
            const double step = 5;
            for (double d = 0; d < L; d += step)
            {
                double s = Math.Min(step, L - d);
                la += s * Math.Cos(b * Math.PI / 180) / 111320.0;
                lo += s * Math.Sin(b * Math.PI / 180) / (111320.0 * Math.Max(0.1, Math.Cos(la * Math.PI / 180)));
                res.Add((la, lo));
                var ax = axis?.Invoke(la, lo);
                if (ax != null)
                {
                    double best = ax.Value;
                    if (Math.Abs(AngDiff(ax.Value + 180, b)) < Math.Abs(AngDiff(ax.Value, b))) best = ax.Value + 180;
                    double turn = Math.Max(-25, Math.Min(25, AngDiff(best, b)));
                    b = ((b + turn) % 360 + 360) % 360;
                }
            }
            res.Reverse();
            return res;
        }

        static double AngDiff(double to, double from) { double d = ((to - from) % 360 + 540) % 360 - 180; return d; }

        // El tren a escala en pantalla (pts: de la cola a la cabeza; len: su largo en px; lengthM: en metros).
        static RectangleF ScaleTrain(Graphics g, List<PointF> pts, float len, double lengthM, Color c)
        {
            float pxPerM = (float)(len / lengthM);
            float W = Math.Max(5f, Math.Min(11f, 3.1f * pxPerM));
            int cars = (int)Math.Max(1, Math.Min(40, Math.Round(lengthM / 20.0)));
            float carL = len / cars, gap = Math.Min(2f, carL * 0.12f);
            // un punto del cuerpo a una distancia desde la cola
            PointF At(float s)
            {
                s = Math.Max(0, Math.Min(len, s));
                for (int i = 1; i < pts.Count; i++)
                {
                    float d = Dist(pts[i - 1], pts[i]);
                    if (s <= d || i == pts.Count - 1) { float t = d < 1e-3f ? 0 : Math.Min(1, s / d); return new PointF(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * t, pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * t); }
                    s -= d;
                }
                return pts[^1];
            }
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var light = Color.FromArgb(c.A, c.R + (255 - c.R) * 35 / 100, c.G + (255 - c.G) * 35 / 100, c.B + (255 - c.B) * 35 / 100);
            using var pen = new Pen(Dark(c), 1.1f) { LineJoin = LineJoin.Round };
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = cars - 1; i >= 0; i--)   // (de la cola a la cabeza: la cabeza, encima)
            {
                float s0 = i * carL + (i > 0 ? gap / 2 : 0), s1 = (i + 1) * carL - (i < cars - 1 ? gap / 2 : 0);
                var a0 = At(s0); var a1 = At(s1);
                float dx = a1.X - a0.X, dy = a1.Y - a0.Y, dl = (float)Math.Sqrt(dx * dx + dy * dy);
                if (dl < 0.5f) continue;
                float ux = dx / dl, uy = dy / dl, nx = -uy * W / 2, ny = ux * W / 2;
                bool head = i == cars - 1;
                PointF[] poly = head
                    ? new[] { new PointF(a0.X + nx, a0.Y + ny), new PointF(a1.X - ux * W * 0.7f + nx, a1.Y - uy * W * 0.7f + ny), a1,
                              new PointF(a1.X - ux * W * 0.7f - nx, a1.Y - uy * W * 0.7f - ny), new PointF(a0.X - nx, a0.Y - ny) }
                    : new[] { new PointF(a0.X + nx, a0.Y + ny), new PointF(a1.X + nx, a1.Y + ny), new PointF(a1.X - nx, a1.Y - ny), new PointF(a0.X - nx, a0.Y - ny) };
                using (var b = new SolidBrush(head ? c : light)) g.FillPolygon(b, poly);
                g.DrawPolygon(pen, poly);
                foreach (var q in poly) { minX = Math.Min(minX, q.X); minY = Math.Min(minY, q.Y); maxX = Math.Max(maxX, q.X); maxY = Math.Max(maxY, q.Y); }
            }
            g.Restore(st);
            return minX > maxX ? RectangleF.Empty : RectangleF.FromLTRB(minX, minY, maxX, maxY);
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
                                                                 IReadOnlyList<LiveMarker> mates, out Rectangle box,
                                                                 bool includeMe = true, string meStatus = null)
        {
            var hits = new List<(Rectangle, LiveMarker)>();
            box = Rectangle.Empty;
            if (mates == null || mates.Count == 0) return hits;
            using var fT = Theme.Font(8.5f, FontStyle.Bold);
            using var fL = Theme.Font(9f, FontStyle.Bold);
            using var fl = Theme.Font(8.5f);
            int rowH = Math.Max(20, TextRenderer.MeasureText(g, "Ág", fL, Size.Empty, TF).Height + 6);
            int maxRows = Math.Max(1, (area.Height - 90) / rowH);
            var rows = new List<(string name, string status, Color c, LiveMarker m)>();
            if (includeMe)
                rows.Add((I18n.T("Tú") + (string.IsNullOrWhiteSpace(meName) ? "" : " (" + LiveMates.ShortName(meName) + ")"),
                          meStatus ?? Status(meCompany), Color.FromArgb(120, 210, 150), null));
            foreach (var m in mates) rows.Add((m.Short, m.Status ?? Status(m.Company), m.Color, m));
            int extra = Math.Max(0, rows.Count - maxRows);
            if (extra > 0) rows.RemoveRange(maxRows, rows.Count - maxRows);

            bool room = System.Linq.Enumerable.All(mates, m => m.Room);   // en una sala: sus jugadores
            string title = I18n.T(room ? "EN LA SALA" : "EN LA RUTA AHORA"), sub = I18n.T(room ? "Sala compartida" : "Comunidad SelectOR"), foot = I18n.T("Clic en un nombre: centrar");
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
