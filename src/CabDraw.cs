// Piezas de dibujo del pupitre: velocímetro, manómetros, indicadores de arco, barras, esfuerzo,
// inversor y pilotos. Réplica en GDI+ de los widgets de Cockpit-SF (cab_widgets.py) con sus
// escalas y sus colores funcionales (rojo TDP, ámbar TFA…), sobre el fondo de SelectOR.
// Todo se dibuja con Graphics.DrawString (no TextRenderer) para que el HUD pueda escalarse con
// una transformación sin que el texto se descoloque.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace SelectOR
{
    public static class CabDraw
    {
        // Paleta funcional de Cockpit-SF (inspirada en DMI).
        public static readonly Color Dial = Color.FromArgb(14, 18, 24);
        public static readonly Color Stroke = Color.FromArgb(62, 72, 88);
        public static readonly Color Text = Color.FromArgb(235, 240, 248);
        public static readonly Color Dim = Color.FromArgb(155, 170, 192);
        public static readonly Color Dark = Color.FromArgb(95, 108, 128);
        public static readonly Color Blue = Color.FromArgb(88, 166, 255);
        public static readonly Color Amber = Color.FromArgb(240, 180, 60);
        public static readonly Color Green = Color.FromArgb(86, 194, 120);
        public static readonly Color Yellow = Color.FromArgb(234, 205, 90);
        public static readonly Color Orange = Color.FromArgb(232, 146, 70);
        public static readonly Color Red = Color.FromArgb(232, 96, 96);
        public static readonly Color White = Color.FromArgb(230, 236, 245);

        static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
        static string N(double v, int dec) => v.ToString(dec <= 0 ? "N0" : "N" + dec, Es);

        // Tamaños en PÍXELES (no en puntos ni escalados por la interfaz): se calculan como fracción
        // de cada esfera, así que la letra crece y encoge exactamente con el módulo.
        static Font F(float px, bool bold = false) =>
            new Font(Theme.FontFamily, Math.Max(5f, px), bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);

        static void Center(Graphics g, string s, Font f, Color c, float cx, float cy)
        {
            var sz = g.MeasureString(s, f);
            using var b = new SolidBrush(c);
            g.DrawString(s, f, b, cx - sz.Width / 2, cy - sz.Height / 2);
        }

        // Texto con sombra: para lo que va FUERA de las esferas, directamente sobre la imagen del
        // simulador, que puede ser clara u oscura.
        public static void Shadowed(Graphics g, string s, Font f, Color c, float x, float y)
        {
            using (var sh = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
            {
                g.DrawString(s, f, sh, x + 1, y + 1);
                g.DrawString(s, f, sh, x - 1, y + 1);
                g.DrawString(s, f, sh, x + 1, y - 1);
            }
            using var b = new SolidBrush(c);
            g.DrawString(s, f, b, x, y);
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Recuadro de módulo: fondo algo más claro que el HUD y borde sutil.
        public static void Panel(Graphics g, RectangleF r)
        {
            using var path = Round(r, 7);
            using (var b = new SolidBrush(Color.FromArgb(32, 38, 46))) g.FillPath(b, path);
            using (var p = new Pen(Color.FromArgb(52, 60, 72))) g.DrawPath(p, path);
        }

        static PointF Polar(float cx, float cy, float r, double deg)
        {
            double a = deg * Math.PI / 180.0;
            return new PointF(cx + (float)(r * Math.Cos(a)), cy + (float)(r * Math.Sin(a)));
        }

        static double Clamp(double v, double a, double b) => v < a ? a : v > b ? b : v;

        // Capas: el pupitre guarda en caché lo fijo (esferas, marcas, números, rótulos) y en cada
        // fotograma de la animación solo pinta lo que se mueve (agujas, arco y lectura digital).
        // 0 = todo · 1 = solo lo fijo · 2 = solo lo que se mueve.
        public static int Layer;
        static bool Fijo => Layer != 2;
        static bool Movil => Layer != 1;

        // ============================ Velocímetro ============================
        // Esfera de 270° estilo ETCS: marcas y números, aguja y lectura digital grande.
        // cruise: velocidad objetivo del regulador («Speed target» de OR) → triángulo amarillo por
        // fuera del aro, que se desliza hasta su valor.
        // Abajo, la señal de velocidad: el límite de ahora en el rombo («anuncio») o, mientras la
        // curva de frenado manda (target = límite al que se está frenando), ese próximo límite en el
        // círculo («límite»).
        public static void Speedometer(Graphics g, RectangleF r, double speed, double limit, bool hasLimit, double max, bool avail,
                                       double target = double.NaN, double cruise = double.NaN)
        {
            float size = Math.Min(r.Width, r.Height);
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2 + size * 0.03f, R = size * 0.46f;
            const float a0 = 135, sweep = 270;
            double A(double v) => a0 + sweep * Clamp(v, 0, max) / max;

            if (Fijo) Face(g, cx, cy, R, Stroke, Math.Max(1, size * 0.008f));

            // Marcas: cada 10 (menores) y rótulos según la escala.
            // Paso de rótulos según la escala (que sale de la velocidad máxima del tren).
            double lab = max <= 100 ? 10 : max <= 200 ? 20 : max <= 300 ? 50 : 100;   // (alta velocidad: de 100 en 100, sin amontonarse)
            double menor = lab / 2;
            if (Fijo)
            using (var pm = new Pen(Dark, Math.Max(1, size * 0.005f)))
            using (var pM = new Pen(Dim, Math.Max(1.2f, size * 0.009f)))
            using (var ft = F(Math.Max(8, size * 0.074f), true))
            {
                for (double v = 0; v <= max + 0.01; v += menor)
                {
                    bool major = Math.Abs(v % lab) < 0.01;
                    float r1 = R * 0.86f, r2 = major ? R * 0.70f : R * 0.77f;
                    var q1 = Polar(cx, cy, r1, A(v)); var q2 = Polar(cx, cy, r2, A(v));
                    Mark(g, major ? pM : pm, major, q1, q2);
                    if (major)
                    {
                        var q = Polar(cx, cy, R * 0.545f, A(v));
                        Ink(g, v.ToString("0", Es), ft, avail ? Text : Dark, q.X, q.Y);
                    }
                }
            }

            if (!Movil) return;

            // Velocidad objetivo del regulador: triángulo amarillo por fuera del aro.
            if (avail && !double.IsNaN(cruise) && cruise > 0)
            {
                double ac = A(cruise);
                var t1 = Polar(cx, cy, R * 1.02f, ac - 3.5); var t2 = Polar(cx, cy, R * 1.02f, ac + 3.5);
                var t3 = Polar(cx, cy, R * 0.90f, ac);
                using var b = new SolidBrush(Yellow);
                g.FillPolygon(b, new[] { t1, t2, t3 });
            }

            // Aguja.
            if (avail)
            {
                var tip = Polar(cx, cy, R * 0.84f, A(speed));
                var tail = Polar(cx, cy, R * 0.14f, A(speed) + 180);
                using var p = new Pen(White, Math.Max(1.6f, size * 0.016f)) { StartCap = LineCap.Round, EndCap = LineCap.Triangle };
                g.DrawLine(p, tail, tip);
            }
            using (var b = new SolidBrush(Color.FromArgb(70, 80, 96))) g.FillEllipse(b, cx - size * 0.035f, cy - size * 0.035f, size * 0.07f, size * 0.07f);

            // Lectura digital con «km/h» pequeño a su derecha.
            string num = avail ? N(speed, 0) : "—";
            using (var fv = F(Math.Max(9, size * 0.105f), true))
            using (var fu = F(Math.Max(6, size * 0.046f)))
            {
                float nw = g.MeasureString(num, fv).Width, uw = g.MeasureString("km/h", fu).Width;
                float y = cy + R * 0.48f, x0 = cx - (nw + uw * 0.9f) / 2;
                Ink(g, num, fv, avail ? Text : Dark, x0 + nw / 2, y);
                Ink(g, "km/h", fu, Dim, x0 + nw + uw * 0.40f, y + size * 0.018f, true);
            }

            // Señal abajo: rombo con el límite de ahora; con curva de frenado, círculo con el próximo.
            if (avail && hasLimit && limit > 0)
            {
                if (double.IsNaN(target)) SpeedSign(g, cx, cy + R * 0.80f, R * 0.32f, N(limit, 0), true);
                else SpeedSign(g, cx, cy + R * 0.80f, R * 0.29f, N(target, 0), false);
            }
        }

        // Señal de velocidad, réplica de las imágenes «anuncio» (rombo) y «límite» (círculo): blanca,
        // borde negro grueso y el número en negro, en Arial Negrita, ocupando lo mismo que en ellas.
        // s = diagonal del rombo o diámetro del círculo, en píxeles. Proporciones medidas sobre las
        // imágenes originales (rombo de 226 px: borde de 14 y «145» de 139 × 65; círculo de 194 px:
        // aro de 19 y «145» de 125 × 58).
        public static void SpeedSign(Graphics g, float cx, float cy, float s, string num, bool diamond)
        {
            if (s < 4 || string.IsNullOrEmpty(num)) return;
            var blanco = Lit ? Color.FromArgb(246, 240, 226) : Color.White;
            var negro = Color.FromArgb(12, 12, 12);

            GraphicsPath Forma(float d)
            {
                var p = new GraphicsPath();
                if (diamond)
                    p.AddPolygon(new[] { new PointF(cx, cy - d / 2), new PointF(cx + d / 2, cy), new PointF(cx, cy + d / 2), new PointF(cx - d / 2, cy) });
                else
                    p.AddEllipse(cx - d / 2, cy - d / 2, d, d);
                return p;
            }

            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Un filo claro alrededor (como el blanco que rodea la señal en las imágenes), para que el
            // borde negro no se pierda sobre la esfera oscura.
            float halo = Math.Max(1f, s * 0.035f);
            using (var p = Forma(s + 2 * halo * (diamond ? 1.414f : 1f)))
            using (var b = new SolidBrush(Color.FromArgb(Lit ? 150 : 120, blanco))) g.FillPath(b, p);
            // Borde: la forma entera en negro y encima la blanca de dentro (grosor exacto, sin Pen).
            using (var p = Forma(s)) using (var b = new SolidBrush(negro)) g.FillPath(b, p);
            float borde = diamond ? s * 0.062f * 1.414f : s * 0.098f;   // en el rombo, medido en perpendicular al lado
            using (var p = Forma(s - 2 * borde)) using (var b = new SolidBrush(blanco)) g.FillPath(b, p);

            // Número: el contorno de las cifras se escala al alto medido; a lo ancho, con la misma
            // proporción que «145» en las imágenes, sin pasar del ancho que ocupaba.
            float altoT = s * (diamond ? 0.288f : 0.299f), anchoT = s * (diamond ? 0.615f : 0.645f);
            using (var fam = SignFont())
            using (var txt = new GraphicsPath())
            {
                txt.AddString(num, fam, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
                var bb = txt.GetBounds();
                if (bb.Width > 0 && bb.Height > 0)
                {
                    float sy = altoT / bb.Height, sx = sy * SignCondense(fam);
                    if (bb.Width * sx > anchoT) sx = anchoT / bb.Width;
                    using var m = new Matrix();
                    m.Translate(cx, cy + s * (diamond ? 0.008f : 0.005f));
                    m.Scale(sx, sy);
                    m.Translate(-(bb.X + bb.Width / 2), -(bb.Y + bb.Height / 2));
                    txt.Transform(m);
                    using var b = new SolidBrush(negro);
                    g.FillPath(b, txt);
                }
            }
            g.Restore(st);
        }

        // Arial (la letra de las señales); si no está, la del programa.
        static FontFamily SignFont()
        {
            try { return new FontFamily("Arial"); } catch { return new FontFamily(Theme.FontFamily); }
        }

        // Cuánto hay que estrechar (o ensanchar) las cifras de esta letra para que «145» quede con
        // la proporción de las imágenes (139 × 65 → 2,14 veces más ancho que alto).
        static float _condense;
        static float SignCondense(FontFamily fam)
        {
            if (_condense > 0) return _condense;
            float c = 1f;
            try
            {
                using var p = new GraphicsPath();
                p.AddString("145", fam, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
                var bb = p.GetBounds();
                if (bb.Width > 0 && bb.Height > 0) c = (139f / 65f) / (bb.Width / bb.Height);
            }
            catch { }
            return _condense = Math.Max(0.6f, Math.Min(1.4f, c));
        }

        // ============================ Manómetro ============================
        // Esfera redonda de 270° con una o varias agujas (TDP rojo, TFA ámbar, cilindro blanco).
        public struct Needle
        {
            public string Label; public Color Color; public double Value; public bool Avail;
            public Needle(string l, Color c, double v, bool a) { Label = l; Color = c; Value = v; Avail = a; }
        }

        // Estilo de PressureDialGauge (Cockpit-SF): esfera oscura, aro fino, escala blanca con
        // todos los números en negrita, «bar» en la parte baja, agujas de flecha con sombra y
        // pivote metálico. Debajo, fuera de la esfera, los rótulos: el título (blanco) y/o las
        // etiquetas de las agujas con su color (TDP rojo, TFA ámbar).
        static readonly Color DialWhite = Color.FromArgb(230, 236, 245), DialGray = Color.FromArgb(130, 145, 165);

        public static void Manometer(Graphics g, float cx, float cy, float R, string title, string unit,
                                     double min, double max, Needle[] needles, bool needleLabels, float labelPx)
        {
            const float a0 = 135, sweep = 270;
            double A(double v) => a0 + sweep * (Clamp(v, min, max) - min) / (max - min);
            float rd = R - Math.Max(2f, R * 0.022f);          // radio útil de la esfera

            if (Fijo) ManometerFace(g, cx, cy, R, rd, title, unit, min, max, needles, needleLabels, labelPx, A);
            if (!Movil) return;

            // Agujas de flecha: punta, hombro, cintura en el eje y cola corta; con sombra.
            foreach (var n in needles)
            {
                if (!n.Avail) continue;
                double ang = A(n.Value) * Math.PI / 180.0;
                float dx = (float)Math.Cos(ang), dy = (float)Math.Sin(ang), px = -dy, py = dx;
                PointF P(float along, float side) => new PointF(cx + dx * along + px * side, cy + dy * along + py * side);
                var pts = new[]
                {
                    P(rd * 0.72f, 0), P(rd * 0.58f, rd * 0.026f), P(0, rd * 0.013f), P(-rd * 0.12f, rd * 0.015f),
                    P(-rd * 0.12f, -rd * 0.015f), P(0, -rd * 0.013f), P(rd * 0.58f, -rd * 0.026f),
                };
                float so = Math.Max(1f, rd * 0.009f);
                var sombra = Array.ConvertAll(pts, q => new PointF(q.X + so, q.Y + so));
                using (var b = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.FillPolygon(b, sombra);
                using (var b = new SolidBrush(n.Color)) g.FillPolygon(b, pts);
                using (var p = new Pen(Color.FromArgb(n.Color.R * 10 / 16, n.Color.G * 10 / 16, n.Color.B * 10 / 16), 0.8f)) g.DrawPolygon(p, pts);
            }

            // Pivote metálico con brillo arriba a la izquierda.
            float pr = Math.Max(3f, rd * 0.06f);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(cx - pr, cy - pr, 2 * pr, 2 * pr);
                using var pg = new PathGradientBrush(path)
                {
                    CenterPoint = new PointF(cx - pr * 0.3f, cy - pr * 0.3f),
                    InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { Color.FromArgb(30, 40, 55), Color.FromArgb(80, 95, 115), Color.FromArgb(180, 195, 215) },
                        Positions = new[] { 0f, 0.45f, 1f },
                    },
                };
                g.FillPath(pg, path);
                using var p = new Pen(Color.FromArgb(15, 20, 28), 1f);
                g.DrawPath(p, path);
            }
            using (var b = new SolidBrush(Color.FromArgb(20, 25, 35))) g.FillEllipse(b, cx - pr * 0.4f, cy - pr * 0.4f, pr * 0.8f, pr * 0.8f);
        }

        // Parte fija del manómetro: esfera, escala, unidad y rótulos de debajo.
        static void ManometerFace(Graphics g, float cx, float cy, float R, float rd, string title, string unit,
                                  double min, double max, Needle[] needles, bool needleLabels, float labelPx, Func<double, double> A)
        {
            const float a0 = 135, sweep = 270;
            Face(g, cx, cy, R, Color.FromArgb(60, 72, 90), Math.Max(1f, R * 0.009f));

            float ra = rd * 0.82f;                             // arco de fondo
            using (var p = new Pen(Lit ? Color.FromArgb(110, 86, 58) : Color.FromArgb(70, 82, 100), Math.Max(1.4f, rd * 0.014f)))
                g.DrawArc(p, cx - ra, cy - ra, 2 * ra, 2 * ra, a0, sweep);

            // Marcas: enteras (largas, blancas) y medias (cortas, grises); si la escala pasa de 15,
            // de dos en dos.
            double mayor = max - min > 15 ? 2 : 1, menor = mayor / 2;
            using (var pm = new Pen(DialGray, Math.Max(1f, rd * 0.0065f)))
            using (var pM = new Pen(DialWhite, Math.Max(1.6f, rd * 0.017f)))
                for (double v = min; v <= max + 1e-6; v += menor)
                {
                    bool M = Math.Abs((v - min) / mayor - Math.Round((v - min) / mayor)) < 1e-6;
                    Mark(g, M ? pM : pm, M, Polar(cx, cy, M ? rd * 0.78f : rd * 0.87f, A(v)), Polar(cx, cy, rd * 0.93f, A(v)));
                }
            using (var fn = F(Math.Max(9, rd * 0.19f), true))
                for (double v = min; v <= max + 1e-6; v += mayor)
                {
                    var q = Polar(cx, cy, rd * 0.63f, A(v));
                    Ink(g, v.ToString("0", Es), fn, DialWhite, q.X, q.Y);
                }
            if (!string.IsNullOrEmpty(unit))
                using (var fu = F(Math.Max(8, rd * 0.17f), true)) Ink(g, unit, fu, Dim, cx, cy + rd * 0.40f, true);

            // Rótulos bajo la esfera (con sombra: van sobre la imagen del simulador).
            var seg = new List<(string t, Color c)>();
            if (!string.IsNullOrEmpty(title)) seg.Add((title.ToUpperInvariant(), Text));
            if (needleLabels)
                foreach (var n in needles)
                    if (!string.IsNullOrEmpty(n.Label)) seg.Add((n.Label.ToUpperInvariant(), n.Avail ? n.Color : Dark));
            if (seg.Count == 0) return;
            using var fl = F(labelPx, true);
            float gap = labelPx * 0.9f, total = 0;
            foreach (var (t, _) in seg) total += g.MeasureString(t, fl).Width;
            total += gap * (seg.Count - 1);
            float x = cx - total / 2, ly = cy + R + labelPx * 0.35f;
            foreach (var (t, c) in seg)
            {
                Shadowed(g, t, fl, c, x, ly);
                x += g.MeasureString(t, fl).Width + gap;
            }
        }

        // ============================ Indicador de arco ============================
        // Amperímetro, voltímetro, potencia y RPM: arco de 240° con zonas de aviso/peligro.
        // En los bipolares (potencia) el cero está arriba y el arco crece hacia el signo.
        public static void Arc(Graphics g, RectangleF r, string title, string unit, double value, double min, double max,
                               double warn, double danger, Color accent, int dec, bool bipolar, bool avail)
        {
            Panel(g, r);
            float pad = r.Height * 0.06f;
            using (var fT = F(Math.Max(6, r.Height * 0.085f), true))
            {
                using var b = new SolidBrush(Dim);
                g.DrawString(title, fT, b, r.X + pad, r.Y + pad * 0.6f);
            }
            float size = Math.Min(r.Width - 2 * pad, r.Height * 0.95f);
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height * 0.62f, R = size * 0.40f;
            const float a0 = 150, sweep = 240;
            double A(double v) => a0 + sweep * (Clamp(v, min, max) - min) / (max - min);
            float w = size * 0.07f;

            using (var p = new Pen(Color.FromArgb(40, 48, 60), w)) g.DrawArc(p, cx - R, cy - R, 2 * R, 2 * R, a0, sweep);
            // Zonas de aviso y peligro (tenues, por dentro).
            float Ri = R - w * 0.9f;
            bool hayWarn = !double.IsNaN(warn) && warn > min && warn < max;
            bool hayDanger = !double.IsNaN(danger) && danger > min && danger < max;
            if (hayWarn)
                using (var p = new Pen(Color.FromArgb(90, Amber), w * 0.35f))
                    g.DrawArc(p, cx - Ri, cy - Ri, 2 * Ri, 2 * Ri, (float)A(warn), (float)(A(hayDanger ? danger : max) - A(warn)));
            if (hayDanger)
                using (var p = new Pen(Color.FromArgb(110, Red), w * 0.35f))
                    g.DrawArc(p, cx - Ri, cy - Ri, 2 * Ri, 2 * Ri, (float)A(danger), (float)(A(max) - A(danger)));

            if (avail)
            {
                double origen = bipolar ? Clamp(0, min, max) : min;
                var col = hayDanger && value >= danger ? Red : hayWarn && value >= warn ? Amber
                        : bipolar && value < 0 ? Orange : accent;
                float s0 = (float)A(origen), s1 = (float)A(value);
                using var p = new Pen(col, w);
                if (Math.Abs(s1 - s0) > 0.3f) g.DrawArc(p, cx - R, cy - R, 2 * R, 2 * R, Math.Min(s0, s1), Math.Abs(s1 - s0));
            }

            using (var fv = F(Math.Max(7, r.Height * 0.17f), true))
                Center(g, avail ? N(value, dec) : "—", fv, avail ? Text : Dark, cx, cy);
            using (var fu = F(Math.Max(6, r.Height * 0.08f)))
                Center(g, unit, fu, Dim, cx, cy + R * 0.55f);
        }

        // ============================ Barra vertical ============================
        // Freno dinámico y combustible.
        public static void Bar(Graphics g, RectangleF r, string title, string unit, double value, double min, double max,
                               Color accent, int dec, bool avail)
        {
            Panel(g, r);
            float pad = r.Height * 0.06f;
            using (var fT = F(Math.Max(6, r.Height * 0.085f), true))
            using (var b = new SolidBrush(Dim)) g.DrawString(title, fT, b, r.X + pad, r.Y + pad * 0.6f);

            float bw = r.Width * 0.22f, top = r.Y + r.Height * 0.24f, bot = r.Bottom - r.Height * 0.10f;
            var track = new RectangleF(r.X + r.Width * 0.18f, top, bw, bot - top);
            using (var tp = Round(track, 3)) using (var b = new SolidBrush(Dial)) g.FillPath(b, tp);
            if (avail)
            {
                float f = (float)((Clamp(value, min, max) - min) / (max - min));
                var fill = new RectangleF(track.X, track.Bottom - track.Height * f, track.Width, track.Height * f);
                if (fill.Height > 0.5f) using (var fp = Round(fill, 3)) using (var b = new SolidBrush(accent)) g.FillPath(b, fp);
            }
            float tx = track.Right + r.Width * 0.08f;
            using (var fv = F(Math.Max(7, r.Height * 0.15f), true))
            using (var b = new SolidBrush(avail ? Text : Dark))
                g.DrawString(avail ? N(value, dec) : "—", fv, b, tx, r.Y + r.Height * 0.42f);
            using (var fu = F(Math.Max(6, r.Height * 0.08f)))
            using (var b = new SolidBrush(Dim)) g.DrawString(unit, fu, b, tx, r.Y + r.Height * 0.62f);
        }

        // ============================ Esfuerzo ============================
        // Barra horizontal bipolar: tracción (verde) a la derecha del cero, freno (ámbar) a la izquierda.
        public static void Effort(Graphics g, RectangleF r, double kn, double min, double max, bool avail)
        {
            Panel(g, r);
            float pad = r.Height * 0.14f;
            using (var fT = F(Math.Max(6, r.Height * 0.20f), true))
            using (var b = new SolidBrush(Dim)) g.DrawString(I18n.T("ESFUERZO TRACCIÓN / FRENO"), fT, b, r.X + pad, r.Y + pad * 0.5f);
            using (var fv = F(Math.Max(7, r.Height * 0.26f), true))
            {
                string t = avail ? N(kn, 0) + " kN" : "—";
                var sz = g.MeasureString(t, fv);
                using var b = new SolidBrush(avail ? (kn >= 0 ? Green : Amber) : Dark);
                g.DrawString(t, fv, b, r.Right - pad - sz.Width, r.Y + pad * 0.2f);
            }
            var track = new RectangleF(r.X + pad, r.Y + r.Height * 0.58f, r.Width - 2 * pad, r.Height * 0.24f);
            using (var tp = Round(track, 3)) using (var b = new SolidBrush(Dial)) g.FillPath(b, tp);
            float X(double v) => track.X + track.Width * (float)((Clamp(v, min, max) - min) / (max - min));
            float x0 = X(0);
            if (avail && Math.Abs(kn) > 0.5)
            {
                float x1 = X(kn);
                var fill = new RectangleF(Math.Min(x0, x1), track.Y, Math.Abs(x1 - x0), track.Height);
                using var b = new SolidBrush(kn >= 0 ? Green : Amber);
                g.FillRectangle(b, fill);
            }
            using (var p = new Pen(White, 1.5f)) g.DrawLine(p, x0, track.Y - 3, x0, track.Bottom + 3);   // el cero
        }

        // ============================ Inversor ============================
        public static void Reverser(Graphics g, RectangleF r, double rev, char dir, bool avail)
        {
            Panel(g, r);
            float pad = r.Width * 0.12f;
            using (var fT = F(Math.Max(6, r.Width * 0.11f), true)) Center(g, I18n.T("INVERSOR"), fT, Dim, r.X + r.Width / 2, r.Y + r.Height * 0.08f);
            var slot = new RectangleF(r.X + r.Width * 0.30f, r.Y + r.Height * 0.18f, r.Width * 0.14f, r.Height * 0.72f);
            using (var sp = Round(slot, slot.Width / 2)) using (var b = new SolidBrush(Dial)) g.FillPath(b, sp);
            string[] marcas = { "F", "N", "R" };
            Color[] cols = { Green, Amber, Red };
            using var fl = F(Math.Max(7, r.Width * 0.16f), true);
            for (int i = 0; i < 3; i++)
            {
                float y = slot.Y + slot.Height * (0.08f + 0.42f * i);
                bool on = avail && ((i == 0 && dir == 'F') || (i == 1 && dir == 'N') || (i == 2 && dir == 'R'));
                using var b = new SolidBrush(on ? cols[i] : Dark);
                g.DrawString(marcas[i], fl, b, slot.Right + pad * 0.6f, y);
            }
            if (avail)
            {
                float f = (float)((1 - Clamp(rev, -1, 1)) / 2);   // +1 arriba (F), −1 abajo (R)
                float ky = slot.Y + slot.Height * (0.1f + 0.8f * f);
                var knob = new RectangleF(slot.X - slot.Width * 0.45f, ky - slot.Width * 0.55f, slot.Width * 1.9f, slot.Width * 1.1f);
                using var kp = Round(knob, 3);
                var col = dir == 'F' ? Green : dir == 'R' ? Red : Amber;
                using var b = new SolidBrush(col);
                g.FillPath(b, kp);
            }
        }

        // ============================ Piloto pequeño ============================
        public static void MiniTile(Graphics g, RectangleF r, string glyph, bool on, Color color, bool avail, bool blinkOff)
        {
            bool lit = avail && on && !blinkOff;
            using (var path = Round(r, 4))
            {
                using var b = new SolidBrush(lit ? Color.FromArgb(215, Blend(color, Color.Black, 0.55f)) : Color.FromArgb(200, 22, 27, 34));
                g.FillPath(b, path);
                using var p = new Pen(lit ? color : Color.FromArgb(210, 58, 66, 78), lit ? 1.4f : 1f);
                g.DrawPath(p, path);
            }
            using var f = F(Math.Max(7, r.Height * 0.62f), true);
            Center(g, glyph, f, lit ? color : Color.FromArgb(avail ? 150 : 90, Dim), r.X + r.Width / 2, r.Y + r.Height / 2 + 0.5f);
        }

        // ============================ Pictograma ============================
        // Botones redondos de la carpeta Pictogramas (incrustados en el .exe como «pict.<nombre>.png»):
        // cara gris = apagado, cara blanca = encendido. Se cargan una vez y se reutilizan.
        static readonly Dictionary<string, Image> _picts = new(StringComparer.OrdinalIgnoreCase);

        public static Image Pict(string name)
        {
            lock (_picts)
            {
                if (_picts.TryGetValue(name, out var img)) return img;
                try
                {
                    using var st = typeof(CabDraw).Assembly.GetManifestResourceStream("pict." + name + ".png");
                    if (st != null) using (var tmp = Image.FromStream(st)) img = new Bitmap(tmp);   // copia: el flujo se cierra
                }
                catch { img = null; }
                _picts[name] = img;
                return img;
            }
        }

        public static void Pictogram(Graphics g, RectangleF r, string name)
        {
            var img = Pict(name);
            if (img == null) return;
            var im = g.InterpolationMode; var po = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(img, r);
            g.InterpolationMode = im; g.PixelOffsetMode = po;
        }

        // ============================ De noche: esferas retroiluminadas ============================
        // Con Lit activo (lo decide el pupitre según la altura del sol en el simulador), la esfera
        // toma un fondo cálido que se aclara hacia el centro, un halo de luz en el borde interior, y
        // las marcas, los números y los textos pasan a un blanco cálido con resplandor ámbar.
        public static bool Lit;
        static readonly Color LitInk = Color.FromArgb(255, 228, 176), LitMinor = Color.FromArgb(196, 150, 96),
                              LitSoft = Color.FromArgb(214, 172, 118), LitHalo = Color.FromArgb(255, 158, 58);

        static void Face(Graphics g, float cx, float cy, float R, Color border, float borderW)
        {
            if (!Lit)
                using (var b = new SolidBrush(Dial)) g.FillEllipse(b, cx - R, cy - R, 2 * R, 2 * R);
            else
            {
                using var path = new GraphicsPath();
                path.AddEllipse(cx - R, cy - R, 2 * R, 2 * R);
                using (var pg = new PathGradientBrush(path)
                {
                    CenterPoint = new PointF(cx, cy),
                    InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { Color.FromArgb(18, 16, 15), Color.FromArgb(34, 29, 24), Color.FromArgb(52, 44, 34) },
                        Positions = new[] { 0f, 0.35f, 1f },
                    },
                }) g.FillPath(pg, path);
                // Luz del bisel: un anillo cálido difuso justo por dentro del borde.
                for (int i = 0; i < 3; i++)
                {
                    float rr = R * (0.975f - i * 0.018f);
                    using var p = new Pen(Color.FromArgb(46 - i * 12, LitHalo), Math.Max(1.5f, R * 0.03f));
                    g.DrawEllipse(p, cx - rr, cy - rr, 2 * rr, 2 * rr);
                }
            }
            using (var p = new Pen(Lit ? Color.FromArgb(120, 96, 70) : border, borderW)) g.DrawEllipse(p, cx - R, cy - R, 2 * R, 2 * R);
        }

        // Marca: de noche, con resplandor debajo y en color cálido.
        static void Mark(Graphics g, Pen day, bool major, PointF a, PointF b)
        {
            if (!Lit) { g.DrawLine(day, a, b); return; }
            using (var h = new Pen(Color.FromArgb(major ? 60 : 36, LitHalo), day.Width * 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(h, a, b);
            using var p = new Pen(major ? LitInk : LitMinor, day.Width);
            g.DrawLine(p, a, b);
        }

        // Texto centrado: de noche, en blanco cálido (o ámbar suave si es secundario) con halo.
        static void Ink(Graphics g, string s, Font f, Color day, float cx, float cy, bool secondary = false)
        {
            if (!Lit) { Center(g, s, f, day, cx, cy); return; }
            var sz = g.MeasureString(s, f);
            float x = cx - sz.Width / 2, y = cy - sz.Height / 2, o = Math.Max(1f, f.Size * 0.07f);
            using (var h = new SolidBrush(Color.FromArgb(secondary ? 30 : 46, LitHalo)))
                for (int k = 0; k < 8; k++)
                {
                    double a = k * Math.PI / 4;
                    g.DrawString(s, f, h, x + (float)(Math.Cos(a) * o), y + (float)(Math.Sin(a) * o));
                }
            using var b = new SolidBrush(secondary ? LitSoft : LitInk);
            g.DrawString(s, f, b, x, y);
        }

        static Color Blend(Color a, Color b, float t) =>
            Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        // ============================ Piloto ============================
        public static void Tile(Graphics g, RectangleF r, string label, string glyph, bool on, Color color, bool avail, bool blinkOff)
        {
            bool lit = avail && on && !blinkOff;
            using (var path = Round(r, 6))
            {
                using var b = new SolidBrush(lit ? Color.FromArgb(70, color) : Color.FromArgb(30, 36, 44));
                g.FillPath(b, path);
                using var p = new Pen(lit ? color : Color.FromArgb(48, 56, 66), lit ? 1.4f : 1f);
                g.DrawPath(p, path);
            }
            var fc = lit ? color : avail ? Dim : Color.FromArgb(70, 80, 94);
            float gh = r.Height * 0.42f;
            using (var fg = F(Math.Max(7, gh * 0.8f), true)) Center(g, glyph, fg, fc, r.X + r.Width / 2, r.Y + r.Height * 0.36f);
            using (var fl = F(Math.Max(5.5f, Math.Min(r.Height * 0.20f, r.Width * 0.085f)), lit))
                Center(g, label, fl, lit ? Text : fc, r.X + r.Width / 2, r.Y + r.Height * 0.76f);
        }
    }
}
