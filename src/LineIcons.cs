using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace SelectOR
{
    // Iconos de línea propios (rejilla 36×36, trazo 2,2) que sustituyen a los emojis de colores.
    public static class LineIcons
    {
        const float Grid = 36f, Stroke = 2.2f;

        // Paleta por significado
        static readonly Color Gre = Color.FromArgb(0x66, 0xC5, 0x6A), Blu = Color.FromArgb(0x78, 0x90, 0xE2),
            Gol = Color.FromArgb(0xF0, 0xC4, 0x5A), Amb = Color.FromArgb(0xFF, 0xB8, 0x4D),
            Red = Color.FromArgb(0xEF, 0x53, 0x50), Tea = Color.FromArgb(0x4D, 0xB6, 0xAC),
            Gry = Color.FromArgb(0xC8, 0xCD, 0xD3), Pur = Color.FromArgb(0xB3, 0x9D, 0xDB),
            Brz = Color.FromArgb(0xD0, 0x8A, 0x50);

        enum Kind { Stroke, Fill, Exclude }

        sealed class Op
        {
            public Kind K; public GraphicsPath Path; public float W; public Region Clip;
        }

        sealed class Icon
        {
            public Color Color; public List<Op> Ops;
        }

        static readonly Dictionary<string, Icon> Map = Build();

        // Quita los selectores de variación (U+FE0F / U+FE0E)
        static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf('️') < 0 && s.IndexOf('︎') < 0) return s.Trim();
            return s.Replace("️", "").Replace("︎", "").Trim();
        }

        public static bool Has(string emoji) => Map.ContainsKey(Norm(emoji));

        public static Color ColorOf(string emoji) => Map.TryGetValue(Norm(emoji), out var ic) ? ic.Color : Gry;

        public static void Draw(Graphics g, string emoji, RectangleF r, Color? tint = null)
        {
            if (g == null || r.Width <= 0 || r.Height <= 0) return;
            if (!Map.TryGetValue(Norm(emoji), out var ic)) return;
            float s = Math.Min(r.Width, r.Height) / Grid;
            var col = tint ?? ic.Color;
            var st = g.Save();
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TranslateTransform(r.X + (r.Width - Grid * s) / 2f, r.Y + (r.Height - Grid * s) / 2f);
                g.ScaleTransform(s, s);
                // Trazo mínimo de ~1,1 px para que no se pierda a 16 px
                float minW = 1.1f / s;
                using var pen = new Pen(col, Stroke) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var brush = new SolidBrush(col);
                lock (ic)
                {
                    foreach (var op in ic.Ops)
                    {
                        switch (op.K)
                        {
                            case Kind.Stroke: pen.Width = Math.Max(op.W, minW); g.DrawPath(pen, op.Path); break;
                            case Kind.Fill: g.FillPath(brush, op.Path); break;
                            case Kind.Exclude: g.SetClip(op.Clip, CombineMode.Exclude); break;
                        }
                    }
                }
            }
            finally { g.Restore(st); }
        }

        // ---------- Constructor de iconos ----------

        sealed class B
        {
            public readonly List<Op> Ops = new();
            float _w = Stroke;

            B Add(Kind k, GraphicsPath p) { Ops.Add(new Op { K = k, Path = p, W = _w }); return this; }

            public B W(float w) { _w = w; return this; }
            public B P(string d) => Add(Kind.Stroke, Svg.Parse(d));
            public B F(string d) => Add(Kind.Fill, Svg.Parse(d));

            public B C(float cx, float cy, float r) { var p = new GraphicsPath(); p.AddEllipse(cx - r, cy - r, 2 * r, 2 * r); return Add(Kind.Stroke, p); }
            public B D(float cx, float cy, float r) { var p = new GraphicsPath(); p.AddEllipse(cx - r, cy - r, 2 * r, 2 * r); return Add(Kind.Fill, p); }
            public B E(float cx, float cy, float rx, float ry) { var p = new GraphicsPath(); p.AddEllipse(cx - rx, cy - ry, 2 * rx, 2 * ry); return Add(Kind.Stroke, p); }
            public B R(float x, float y, float w, float h, float rr = 0) => Add(Kind.Stroke, RoundRect(x, y, w, h, rr));
            public B FR(float x, float y, float w, float h) { var p = new GraphicsPath(); p.AddRectangle(new RectangleF(x, y, w, h)); return Add(Kind.Fill, p); }

            // Rectángulo redondeado girado alrededor de su centro
            public B RotRect(float cx, float cy, float w, float h, float rr, float deg)
            {
                var p = RoundRect(cx - w / 2, cy - h / 2, w, h, rr);
                using var m = new Matrix();
                m.RotateAt(deg, new PointF(cx, cy));
                p.Transform(m);
                return Add(Kind.Stroke, p);
            }

            public B Poly(float cx, float cy, float r, int n, float rotDeg)
            {
                var pts = new PointF[n];
                for (int i = 0; i < n; i++)
                {
                    double a = (rotDeg + 360.0 * i / n) * Math.PI / 180;
                    pts[i] = new PointF(cx + r * (float)Math.Cos(a), cy + r * (float)Math.Sin(a));
                }
                var p = new GraphicsPath(); p.AddPolygon(pts); return Add(Kind.Stroke, p);
            }

            public B Star(float cx, float cy, float ro, float ri, int n)
            {
                var pts = new PointF[n * 2];
                for (int i = 0; i < n * 2; i++)
                {
                    double a = (-90 + 180.0 * i / n) * Math.PI / 180;
                    float rr = i % 2 == 0 ? ro : ri;
                    pts[i] = new PointF(cx + rr * (float)Math.Cos(a), cy + rr * (float)Math.Sin(a));
                }
                var p = new GraphicsPath(); p.AddPolygon(pts); return Add(Kind.Stroke, p);
            }

            // Rueda dentada
            public B Gear(float cx, float cy, float ro, float ri, int n)
            {
                var pts = new List<PointF>();
                double step = 2 * Math.PI / n;
                for (int i = 0; i < n; i++)
                {
                    double a = i * step - Math.PI / 2;
                    foreach (var (f, rr) in new[] { (-0.30, ri), (-0.17, ro), (0.17, ro), (0.30, ri) })
                        pts.Add(new PointF(cx + rr * (float)Math.Cos(a + f * step), cy + rr * (float)Math.Sin(a + f * step)));
                }
                var p = new GraphicsPath(); p.AddPolygon(pts.ToArray()); return Add(Kind.Stroke, p);
            }

            // Destello de cuatro puntas
            public B Sparkle(float cx, float cy, float r)
            {
                string f(float v) => v.ToString(CultureInfo.InvariantCulture);
                float k = r * 0.18f;
                return P($"M{f(cx)} {f(cy - r)}C{f(cx + k)} {f(cy - k)} {f(cx + k)} {f(cy - k)} {f(cx + r)} {f(cy)}" +
                         $"C{f(cx + k)} {f(cy + k)} {f(cx + k)} {f(cy + k)} {f(cx)} {f(cy + r)}" +
                         $"C{f(cx - k)} {f(cy + k)} {f(cx - k)} {f(cy + k)} {f(cx - r)} {f(cy)}" +
                         $"C{f(cx - k)} {f(cy - k)} {f(cx - k)} {f(cy - k)} {f(cx)} {f(cy - r)}z");
            }

            // Gira todo lo dibujado hasta ahora alrededor del centro
            public B Rot(float deg)
            {
                using var mx = new Matrix();
                mx.RotateAt(deg, new PointF(Grid / 2, Grid / 2));
                foreach (var op in Ops) if (op.Path != null) op.Path.Transform(mx);
                return this;
            }

            public B L(float x1, float y1, float x2, float y2) { var p = new GraphicsPath(); p.AddLine(x1, y1, x2, y2); return Add(Kind.Stroke, p); }

            public B PL(params float[] xy)
            {
                var pts = new PointF[xy.Length / 2];
                for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(xy[2 * i], xy[2 * i + 1]);
                var p = new GraphicsPath(); p.AddLines(pts); return Add(Kind.Stroke, p);
            }

            // Lo que se dibuje después no pisa la última figura (con un margen)
            public B X(float gap = 1.6f, int count = 1)
            {
                var rg = new Region(new RectangleF(0, 0, 0, 0));
                using var pen = new Pen(Color.Black, Stroke + 2 * gap) { LineJoin = LineJoin.Round };
                for (int k = Ops.Count - 1, left = count; k >= 0 && left > 0; k--)
                {
                    if (Ops[k].K == Kind.Exclude) continue;
                    var path = Ops[k].Path;
                    using var wp = (GraphicsPath)path.Clone();
                    wp.Widen(pen);
                    rg.Union(path);
                    rg.Union(wp);
                    left--;
                }
                Ops.Add(new Op { K = Kind.Exclude, Clip = rg });
                return this;
            }
        }

        static GraphicsPath RoundRect(float x, float y, float w, float h, float rr)
        {
            var p = new GraphicsPath();
            if (rr <= 0) { p.AddRectangle(new RectangleF(x, y, w, h)); return p; }
            float d = rr * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------- Catálogo ----------

        static Dictionary<string, Icon> Build()
        {
            var m = new Dictionary<string, Icon>(StringComparer.Ordinal);
            void A(string e, Color c, Action<B> f)
            {
                var b = new B(); f(b);
                m[Norm(e)] = new Icon { Color = c, Ops = b.Ops };
            }

            // Personas: hombros
            const string Shoulders = "M7 32c1-5.5 5-8.5 11-8.5s10 3 11 8.5";

            // --- Trenes y vía ---
            A("🚆", Gol, b => b.R(9, 5, 18, 22, 4).P("M9 17h18").D(14, 22, 1.5f).D(22, 22, 1.5f).P("M12 27l-4 5M24 27l4 5"));
            A("🚄", Gol, b => b.P("M3 12h14c8 0 13.5 5 15 11v2H3z").P("M7 16.5h11").P("M21.5 14.5l3 4h5").P("M3 30h30"));
            A("🚂", Gol, b => b.C(9, 28.5f, 3).C(17, 28.5f, 3).C(25.5f, 28.5f, 3).X(1.2f, 3)
                .P("M4 24V15h16v9").P("M20 24V8h10v16").P("M18.5 8h13").R(23, 11, 4, 4.5f, 1).P("M8 15l-1-5h5l-1 5").P("M2 24h30"));
            A("🚉", Gol, b => b.P("M3 11l4-5h22l4 5z").P("M7 11v20M29 11v20").R(12, 15, 12, 12, 3).P("M12 21.5h12")
                .D(15, 24.5f, 1.2f).D(21, 24.5f, 1.2f).P("M3 31h30"));
            A("🛤", Amb, b => b.P("M12 32 16 4M24 32 20 4").P("M11 26h14M12 19h12M13 12h10"));
            A("🚦", Gre, b => b.R(11, 3, 14, 30, 4).C(18, 10, 3).C(18, 18, 3).C(18, 26, 3));
            A("🚧", Amb, b => b.R(3, 10, 30, 10, 2).P("M10 10 5 20M17 10l-5 10M24 10l-5 10M31 10l-5 10")
                .P("M8 20v11M28 20v11M5 31h6M25 31h6").D(8, 6.5f, 1.7f).D(28, 6.5f, 1.7f));
            A("🚚", Amb, b => b.C(9, 26.5f, 3).C(26, 26.5f, 3).X(1.2f, 2).P("M3 8h18v16H3z").P("M21 13h6l5 5v6H21"));
            A("🚒", Red, b => b.C(9, 27, 3).C(27, 27, 3).X(1.2f, 2).P("M3 12h20v13H3z").P("M23 15h5l5 5v5H23")
                .W(1.8f).P("M4 5.5h17M4 9h17M8 5.5v3.5M12.5 5.5v3.5M17 5.5v3.5"));
            A("🚑", Gry, b => b.C(9, 27, 3).C(27, 27, 3).X(1.2f, 2).P("M3 10h20v15H3z").P("M23 14h5l5 5v6H23").P("M13 13.5v8M9 17.5h8"));
            A("🚀", Pur, b => b.P("M28.5 7.5c-6.5-.5-12 2.5-15.5 8.5l-4.5.5-3.5 3.5 6 1.5 3 3 1.5 6 3.5-3.5.5-4.5c6-3.5 9-9 8.5-15.5z")
                .C(22, 14, 2.5f).P("M9.5 24.5c-2 1-3.5 3-4 6 3-.5 5-2 6-4"));
            A("🛰", Tea, b => b.R(14, 14, 8, 8, 1.5f).R(2, 14.5f, 9.5f, 7, 1).R(24.5f, 14.5f, 9.5f, 7, 1).P("M6.75 14.5v7M29.25 14.5v7")
                .P("M11.5 18H14M22 18h2.5M18 22v3").P("M13.5 29.5a4.5 4.5 0 0 1 9 0z").Rot(-35));

            // --- Avisos y estados ---
            A("⚠", Amb, b => b.P("M18 5L33 30H3z").P("M18 14v7").D(18, 25.5f, 1.6f));
            A("⛔", Red, b => b.C(18, 18, 13.5f).W(3).P("M11 18h14"));
            A("✅", Gre, b => b.R(5, 5, 26, 26, 6).P("M11 18.5l5 5 9.5-10"));
            A("❌", Red, b => b.W(3).P("M10 10l16 16M26 10L10 26"));
            A("🛑", Red, b => b.Poly(18, 18, 14.5f, 8, 22.5f).W(1.6f).Poly(18, 18, 10.5f, 8, 22.5f));
            A("❓", Blu, b => b.W(2.8f).P("M12.5 12.5a5.5 5.5 0 1 1 8.4 4.7c-1.9 1.2-2.9 2.6-2.9 5.3").D(18, 29, 1.9f));
            A("➕", Gre, b => b.W(3).P("M18 7v22M7 18h22"));
            A("🟢", Gre, b => b.D(18, 18, 10));
            A("⬇", Tea, b => b.W(2.6f).P("M18 5v24M9 20l9 9 9-9"));
            A("🔄", Tea, b => b.P("M29 14a11.5 11.5 0 0 0-21-2.5").P("M7 22a11.5 11.5 0 0 0 21 2.5").P("M7 5v7h7").P("M29 31v-7h-7"));
            A("🔔", Amb, b => b.P("M7.5 26c2-2 2.5-5 2.5-10a8 8 0 0 1 16 0c0 5 .5 8 2.5 10z").P("M15 29.5a3 3 0 0 0 6 0").P("M18 5v3"));

            // --- Personas ---
            A("👤", Blu, b => b.C(18, 12, 6).P("M6 31c1-7 6-11 12-11s11 4 12 11"));
            A("👥", Blu, b => b.C(13, 12, 5).P("M4 29c1-6 5-9 9-9s8 3 9 9").C(25, 13, 4).P("M24 20c4 0 7 3 8 8"));
            A("🧍", Blu, b => b.C(18, 7, 3.5f).P("M18 12.5v10").P("M10.5 16 18 13l7.5 3").P("M18 22.5l-4.5 9M18 22.5l4.5 9"));
            A("👮", Blu, b => b.C(18, 17, 5).P("M11 11.5h14").P("M12.5 11.5l.8-4.3c3-1.6 6.4-1.6 9.4 0l.8 4.3").P(Shoulders).D(22.5f, 28, 1.3f));
            A("👷", Amb, b => b.C(18, 18, 5).P("M11.5 13.5a6.5 6.5 0 0 1 13 0").P("M9 13.5h18").P("M18 7v3").P(Shoulders));
            A("🧑‍✈", Blu, b => b.C(18, 18, 5).P("M10 10.5l8-4 8 4-1.5 2.5h-13z").P(Shoulders).P("M18 24.5l-1.3 2.8 1.3 4 1.3-4z"));
            A("🙋", Blu, b => b.C(14, 11.5f, 4.8f).P("M4 31c.8-6 4.5-9.5 10-9.5 2 0 3.8.5 5.3 1.4").P("M20.5 24 25.5 9.5")
                .P("M25.5 9.5 23.2 5M25.5 9.5l.3-5.3M25.5 9.5l3-4.3M25.5 9.5l4.3-1.8"));
            A("👋", Amb, b => b.P("M13 19V9.5M17 18V7M21 18V8M25 19.5V11.5")
                .P("M25 19.5v2.5c0 5.5-3.5 9-8 9-3.5 0-5.5-1.5-7.5-4.5l-4-6.5c-.8-1.3-.4-2.8.8-3.4 1.2-.6 2.6 0 3.3 1.1l2.4 3.8V19")
                .P("M28.5 6c1.8.9 3 2.5 3.4 4.5"));
            A("🤝", Blu, b => b.P("M2 13.5h5.5l5-2.5c1.5-.7 3.2-.8 4.7-.2")
                .P("M34 13.5h-5l-4-1.5h-4.5l-6.8 5.2c-1.1.9-1.2 2.4-.2 3.3.9.8 2.3.8 3.2 0l3.2-2.6 7.6 6.6H34")
                .P("M2 23.5h5l6.5 5.3c.9.7 2.2.6 2.9-.3s.6-2.1-.3-2.8")
                .P("M16.6 25.8l1.8 1.5c.9.7 2.2.6 2.9-.3s.6-2.1-.3-2.8l-1.3-1")
                .P("M20.6 24.1l1 .8c.9.7 2.2.6 2.9-.3s.6-2.1-.3-2.8"));

            // --- Dinero, empresa, banca ---
            A("🏦", Gre, b => b.P("M5 14 18 6l13 8").P("M8 14v13M14 14v13M22 14v13M28 14v13M5 29h26"));
            A("🏢", Blu, b => b.R(9, 4, 18, 27, 2).P("M13.5 10h2.5M20 10h2.5M13.5 15h2.5M20 15h2.5M13.5 20h2.5M20 20h2.5")
                .P("M16 31v-5h4v5").P("M4 31h28"));
            A("💶", Gre, b => b.R(3, 9, 30, 18, 3).P("M22 14.3a5 5 0 1 0 0 7.4").P("M12.5 16.5h6.5M12.5 19.5h6.5"));
            A("🛒", Gre, b => b.P("M3 6h4l3.5 16h16.5l3.5-11.5H8.5").D(13, 28.5f, 2.2f).D(25, 28.5f, 2.2f));
            A("🎫", Gol, b => b.P("M4 10h28v5a3 3 0 0 0 0 6v5H4v-5a3 3 0 0 0 0-6z").P("M22 12.5v2M22 17v2M22 21.5v2"));
            A("📊", Tea, b => b.P("M5 31h26").R(8, 19, 5, 12, 1).R(15.5f, 10, 5, 21, 1).R(23, 15, 5, 16, 1));
            A("🏙", Tea, b => b.P("M3 31V17h7v14M10 31V8h9v23M19 31V13h7v18M26 31V19h7v12").P("M2 31h32").P("M13.5 13h2M13.5 18h2M13.5 23h2"));
            A("📦", Amb, b => b.R(3, 7, 30, 6, 1.5f).P("M5.5 13v15.5a2 2 0 0 0 2 2h21a2 2 0 0 0 2-2V13").P("M14.5 18h7"));
            A("🧳", Amb, b => b.R(5, 11, 26, 19, 3).P("M13 11V7.5a2 2 0 0 1 2-2h6a2 2 0 0 1 2 2V11").P("M11 11v19M25 11v19"));

            // --- Premios ---
            A("🏆", Gol, b => b.P("M11 5h14v8a7 7 0 0 1-14 0z").P("M11 8H6.5c0 4 1.8 6.6 5.2 7.4M25 8h4.5c0 4-1.8 6.6-5.2 7.4")
                .P("M18 20v5").R(12, 25, 12, 5, 1.5f));
            const string Ribbon = "M11 4l5 11.5M25 4l-5 11.5";
            A("🏅", Gol, b => b.P(Ribbon).C(18, 23, 8).W(1.8f).Star(18, 23.4f, 4.6f, 2, 5));
            A("🥇", Gol, b => b.P(Ribbon).C(18, 23, 8).P("M15.8 20.5l2.7-1.8v8.5"));
            A("🥈", Gry, b => b.P(Ribbon).C(18, 23, 8).P("M15.4 20.8a2.7 2.7 0 1 1 4.5 2l-4.5 4.4h5.3"));
            A("🥉", Brz, b => b.P(Ribbon).C(18, 23, 8).P("M15.6 19h4.8l-2.6 3.2a2.4 2.4 0 1 1-2.5 3.6"));
            A("🎖", Gol, b => b.P("M12 3h12v8l-6 4.5-6-4.5z").P("M18 3v12.5").Star(18, 25, 7.5f, 3.3f, 5));
            A("👑", Gol, b => b.P("M5 26 3 11l8.5 6.5L18 7l6.5 10.5L33 11l-2 15z").P("M6 30.5h24"));
            A("⭐", Gol, b => b.Star(18, 19, 14.5f, 6.2f, 5));
            A("🌟", Gol, b =>
            {
                b.Star(18, 19, 10, 4.3f, 5);
                for (int i = 0; i < 5; i++)
                {
                    double a = (-54 + 72 * i) * Math.PI / 180;
                    float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
                    b.L(18 + 12.5f * c, 19 + 12.5f * s, 18 + 15.5f * c, 19 + 15.5f * s);
                }
            });
            A("✨", Gol, b => b.Sparkle(14, 21, 10).Sparkle(27, 8.5f, 5).Sparkle(28.5f, 27.5f, 3.5f));
            A("💎", Blu, b => b.P("M9.5 6h17l6.5 8.5L18 32 3 14.5z").P("M3 14.5h30M13 6l-2 8.5L18 32l7-17.5-2-8.5"));
            A("🎉", Gol, b => b.P("M5 31l6-17 11 11z").P("M15 10c1-2 0-4 2-5M21 13c3-1 5 1 7-1M24 20c2 0 3 1 5 0")
                .D(22, 6, 1.3f).D(29, 9.5f, 1.3f).D(30, 25, 1.3f));
            A("🎓", Pur, b => b.P("M2.5 13.5 18 6l15.5 7.5L18 21z").P("M9 17.5v6c0 2.4 4 4.5 9 4.5s9-2.1 9-4.5v-6").P("M30.5 15v8"));
            A("🎗", Pur, b => b.P("M12.5 32 21.5 17c2.5-4.2 2.5-7.3 0-10.3-1.9-2.2-5.1-2.2-7 0-2.5 3-2.5 6.1 0 10.3l9 15"));
            A("♾", Pur, b => b.P("M18 18c-2.6-3.6-5-6-8-6a6 6 0 0 0 0 12c3 0 5.4-2.4 8-6s5-6 8-6a6 6 0 0 1 0 12c-3 0-5.4-2.4-8-6z"));
            A("🏟", Tea, b => b.E(18, 14, 14, 6).P("M4 14v8c0 3.3 6.3 6 14 6s14-2.7 14-6v-8").E(18, 14, 7, 2.5f));
            A("☄", Amb, b => b.C(24, 12, 5).P("M20.5 8.5 11 18M27.5 15.5 18 25M20.5 15.5 6 30"));
            A("🕰", Pur, b => b.P("M8 31V16a10 10 0 0 1 20 0v15").C(18, 16, 6).P("M18 12.5V16l2.5 1.5").P("M5 31h26"));

            // --- Seguridad y llaves ---
            A("🔑", Amb, b => b.C(10, 18, 6).D(10, 18, 1.5f).P("M16 18h16M27 18v5M31.5 18v4"));
            A("🗝", Amb, b => b.C(11, 11, 6.5f).C(11, 11, 2.2f).P("M15.6 15.6 31 31M26 26l-3.5 3.5M29.3 29.3l-3 3"));
            A("🔒", Amb, b => b.R(8, 16, 20, 15, 3).P("M12 16v-5a6 6 0 0 1 12 0v5").D(18, 22.5f, 1.8f).P("M18 22.5v3.5"));
            A("🔓", Gre, b => b.R(8, 16, 20, 15, 3).P("M12 16v-5a6 6 0 0 1 11.6-2.2").D(18, 22.5f, 1.8f).P("M18 22.5v3.5"));
            A("🔐", Amb, b => b.R(3, 17, 19, 14, 3).P("M7 17v-4a5.5 5.5 0 0 1 11 0v4").P("M12.5 22.5v3.5")
                .C(28, 9, 3.6f).P("M28 12.6V31M28 25h3M28 29.5h3"));
            A("🛡", Pur, b => b.P("M18 4l12 4.5v8.5c0 7.5-5 12.5-12 15-7-2.5-12-7.5-12-15V8.5z"));
            A("⚖", Pur, b => b.P("M18 5v25M11 30.5h14M6.5 9h23").D(18, 5, 1.6f)
                .P("M6.5 9 3 19M6.5 9 10 19M29.5 9 26 19M29.5 9 33 19").P("M2.5 19a4 4 0 0 0 8 0zM25.5 19a4 4 0 0 0 8 0z"));
            A("🪪", Blu, b => b.R(3, 7, 30, 22, 3).C(12, 16, 3).P("M7 24c.8-3 2.6-4.5 5-4.5s4.2 1.5 5 4.5").P("M21 14h8M21 19h8M21 24h5"));

            // --- Comunicación ---
            A("💬", Blu, b => b.P("M7 7h22a4 4 0 0 1 4 4v11a4 4 0 0 1-4 4H16l-7 5.5V26H7a4 4 0 0 1-4-4V11a4 4 0 0 1 4-4z")
                .D(12, 16.5f, 1.6f).D(18, 16.5f, 1.6f).D(24, 16.5f, 1.6f));
            A("✉", Blu, b => b.R(4, 8, 28, 20, 3).P("M5.5 10 18 19.5 30.5 10"));
            A("📨", Blu, b => b.R(10, 10, 23, 17, 2.5f).P("M11.5 12 21.5 19.5 31.5 12").P("M3 14h4M2 18.5h5M3 23h4"));
            A("📢", Amb, b => b.P("M5 14.5v7h5l15 7.5V7l-15 7.5z").P("M10 21.5l2 8h4l-1.5-6.5").P("M29 14a5 5 0 0 1 0 8"));
            A("🔊", Gry, b => b.P("M4 14h6l8-7v22l-8-7H4z").P("M23 13a7 7 0 0 1 0 10M27 9a12.5 12.5 0 0 1 0 18"));
            A("🔇", Red, b => b.P("M4 14h6l8-7v22l-8-7H4z").P("M23 14l8 8M31 14l-8 8"));
            A("📻", Gry, b => b.R(4, 13, 28, 18, 3).C(12, 22, 4.5f).P("M21 18h7M21 22h7M21 26h7").P("M9 13 26 5"));
            A("📡", Tea, b => b.P("M6 14a12 12 0 0 0 16 16z").P("M14 22l7-7").D(22, 14, 1.8f).P("M10.5 26.5l-3 5.5h9")
                .P("M24 7a5 5 0 0 1 5 5M24 2.5a9.5 9.5 0 0 1 9.5 9.5"));
            A("🎙", Gry, b => b.R(13, 3, 10, 17, 5).P("M8.5 15a9.5 9.5 0 0 0 19 0").P("M18 24.5V31M12 31.5h12"));
            A("🔗", Tea, b => b.RotRect(12.5f, 23.5f, 18, 9, 4.5f, -45).RotRect(23.5f, 12.5f, 18, 9, 4.5f, -45));
            A("🌐", Tea, b => b.C(18, 18, 13.5f).E(18, 18, 6, 13.5f).P("M4.5 18h27").P("M6.5 11h23M6.5 25h23"));
            A("🖥", Gry, b => b.R(4, 6, 28, 18, 2.5f).P("M18 24v6M12 30h12"));

            // --- Mapas y orientación ---
            A("🗺", Tea, b => b.P("M4 8l9-3 10 3 9-3v23l-9 3-10-3-9 3z").P("M13 5v23M23 8v23"));
            A("🧭", Tea, b => b.C(18, 18, 13.5f).P("M23.5 12.5 20.5 20.5 12.5 23.5 15.5 15.5z").F("M23.5 12.5 20.5 20.5 15.5 15.5z"));
            A("🌍", Tea, b => b.C(18, 18, 13.5f).P("M9 9.5c2.5.5 4 2 4 4s-2.5 2.5-2 4.5 3 2 4.5 4-.5 4.5.5 6.5")
                .P("M22 5.5c-.5 2.5 1 4 3 4.5s3.5 2 3 4-2.5 2-3 4 .5 3.5 2 4.5"));
            A("🏁", Gry, b => b.P("M8 32V4").R(8, 5, 22, 14).FR(8, 5, 5.5f, 7).FR(19, 5, 5.5f, 7).FR(13.5f, 12, 5.5f, 7).FR(24.5f, 12, 5.5f, 7));
            A("📏", Amb, b => b.R(3, 12, 30, 12, 2).P("M8 12v5M13 12v3.5M18 12v5M23 12v3.5M28 12v5"));
            A("📐", Amb, b => b.P("M5 31V5l26 26z").P("M10 26v-8.5l8.5 8.5z").P("M5 11h3M5 16h2M5 21h3"));
            A("📌", Red, b => b.P("M13 4h10M14.5 4l-.5 8-5 5v2h18v-2l-5-5-.5-8").P("M18 19v13"));
            A("⛰", Gry, b => b.P("M2.5 30 13.5 9.5l5.5 8 4-5L33.5 30z").P("M9.5 17l3 2.5 3-2.5 3 2"));

            // --- Tiempo y clima ---
            A("🗓", Blu, b => b.R(5, 7, 26, 24, 3).P("M5 14h26").P("M11 4v6M18 4v6M25 4v6").P("M10 20h16M10 25h10"));
            A("📅", Blu, b => b.R(5, 7, 26, 24, 3).P("M5 14h26M12 4v6M24 4v6")
                .D(12, 20, 1.5f).D(18, 20, 1.5f).D(24, 20, 1.5f).D(12, 25.5f, 1.5f).D(18, 25.5f, 1.5f));
            A("🕒", Blu, b => b.C(18, 18, 13.5f).P("M18 10v8h6.5"));
            A("☀", Amb, b => b.C(18, 18, 6.5f).P("M18 3v4M18 29v4M3 18h4M29 18h4M7.4 7.4l2.8 2.8M25.8 25.8l2.8 2.8M7.4 28.6l2.8-2.8M25.8 10.2l2.8-2.8"));
            A("☁", Gry, b => b.P("M9 26.5h18a6 6 0 0 0 0-12a8.5 8.5 0 0 0-16 1.8A5.4 5.4 0 0 0 9 26.5z"));
            // (2026-11-09) luna creciente (plus nocturno)
            A("🌙", Gol, b => b.P("M23 5.5a12.5 12.5 0 1 0 8 20.5A10.5 10.5 0 0 1 23 5.5z"));
            A("⛅", Amb, b => b.P("M12 31H29a5 5 0 0 0 0-10a7 7 0 0 0-13.3 1.6A4.7 4.7 0 0 0 12 31z").X(1.4f)
                .C(12.5f, 13, 5.5f).P("M12.5 3.5v2M3 13h2M5.8 6.3l1.4 1.4M19.2 6.3l-1.4 1.4M5.8 19.7l1.4-1.4"));
            A("🌧", Blu, b => b.P("M9 22h18a5.5 5.5 0 0 0 0-11a8 8 0 0 0-15.2 1.7A5 5 0 0 0 9 22z").P("M12 26l-2 5M18.5 26l-2 5M25 26l-2 5"));
            A("❄", Blu, b =>
            {
                for (int i = 0; i < 6; i++)
                {
                    double a = (-90 + 60 * i) * Math.PI / 180;
                    float ux = (float)Math.Cos(a), uy = (float)Math.Sin(a), vx = -uy, vy = ux;
                    b.L(18, 18, 18 + 13.5f * ux, 18 + 13.5f * uy);
                    b.PL(18 + 6.5f * ux + 3.5f * vx, 18 + 6.5f * uy + 3.5f * vy, 18 + 9.5f * ux, 18 + 9.5f * uy,
                         18 + 6.5f * ux - 3.5f * vx, 18 + 6.5f * uy - 3.5f * vy);
                }
            });
            A("🧊", Tea, b => b.P("M18 4 31 11.5v13L18 32 5 24.5v-13z").P("M5 11.5 18 19l13-7.5M18 19v13"));
            A("🔥", Red, b => b.P("M18 32c-5.5 0-9.5-3.8-9.5-9 0-5 3.5-7.5 4.5-13 3 1.8 4.5 4.5 4.5 7.5 1.8-.8 3-2.6 3.2-5 4 3 7.3 6.8 7.3 11 0 5-4.2 8.5-10 8.5z")
                .P("M18 32c-2.5 0-4.2-1.6-4.2-3.8 0-2.3 2-3.7 3.2-6.2 2 1.7 5.2 3.5 5.2 6.2 0 2.2-1.7 3.8-4.2 3.8z"));
            A("⚡", Gol, b => b.P("M20.5 3 8 20h9.5L15.5 33 28 15.5h-9.5z"));
            A("💡", Gol, b => b.P("M13 24c0-3-5-5.5-5-11a10 10 0 0 1 20 0c0 5.5-5 8-5 11z").P("M13.5 28h9M15 32h6"));

            // --- Herramientas y objetos ---
            A("🔎", Gry, b => b.C(15.5f, 15.5f, 9.5f).W(2.8f).P("M22.5 22.5l8.5 8.5"));
            A("🔍", Gry, b => b.C(15.5f, 15.5f, 9.5f).W(2.8f).P("M22.5 22.5l8.5 8.5"));
            A("⚙", Gry, b => b.Gear(18, 18, 14, 10.5f, 8).C(18, 18, 4.5f));
            A("🔧", Gry, b => b.P("M25.4 6.4 22.6 9.2 26.8 13.4 29.6 10.6A7 7 0 0 1 20.2 19.4L12.1 27.5A2.5 2.5 0 0 1 8.5 23.9L16.6 15.8A7 7 0 0 1 25.4 6.4z"));
            A("📋", Gry, b => b.R(13, 3, 10, 6, 2).X(1.2f).R(7, 6, 22, 26, 3).P("M12 16h12M12 21h12M12 26h8"));
            A("📝", Gry, b => b.P("M30.5 13l3 3-11 11-4 1 1-4z").X(1.4f).R(5, 4, 18, 28, 2.5f).P("M9.5 10h9M9.5 15h9M9.5 20h5"));
            A("📁", Gry, b => b.P("M4 9a2 2 0 0 1 2-2h7l3 3h14a2 2 0 0 1 2 2v16a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2z").P("M4 14h28"));
            A("🗂", Gry, b => b.P("M7.5 9.5V7.5A1.5 1.5 0 0 1 9 6h5l2 2.5h11.5A1.5 1.5 0 0 1 29 10v2")
                .P("M4 14a2 2 0 0 1 2-2h7l2.5 2.5H30a2 2 0 0 1 2 2V29a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2z"));
            A("🖼", Gry, b => b.R(4, 6, 28, 24, 2.5f).P("M4.5 26l8.5-8.5 6.5 6.5 4-4 8 8").C(23, 13, 2.5f));
            A("📷", Gry, b => b.R(3, 10, 30, 20, 3).P("M11 10l2.5-4h9l2.5 4").C(18, 20, 5.5f).D(27.5f, 14.5f, 1.2f));
            A("🚪", Gry, b => b.P("M9 32V5a1 1 0 0 1 1-1h16a1 1 0 0 1 1 1v27").P("M5 32h26").D(22.5f, 19, 1.7f));
            A("🗑", Red, b => b.P("M6 9h24M14 9V6a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v3")
                .P("M9 9l1.5 21a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2L27 9").P("M15 14v13M21 14v13"));
            A("💾", Gry, b => b.P("M7 4h17l7 7v18a3 3 0 0 1-3 3H7a3 3 0 0 1-3-3V7a3 3 0 0 1 3-3z").P("M11 4v7h11V4").P("M10 32v-9h16v9"));
            A("📜", Pur, b => b.P("M10 9.5V27a3 3 0 0 0 3 3h14").P("M10 9.5H7a3 3 0 1 1 3-3V9.5").P("M10 6.5a3 3 0 0 1 3-3h13a3 3 0 0 1 3 3V24")
                .P("M27 30a3 3 0 0 0 3-3v-3H18v3a3 3 0 0 1-3 3").P("M15 11h9M15 15.5h9M15 20h6"));
            A("🎛", Gry, b => b.R(3, 7, 30, 22, 3).C(11, 15, 3.5f).C(25, 15, 3.5f).P("M11 15v-3.5M25 15l2.5-2.5").P("M8 23.5h20").D(15, 23.5f, 2.2f));
            A("🎚", Gry, b => b.R(5.5f, 19.5f, 7, 5, 1.5f).R(14.5f, 8.5f, 7, 5, 1.5f).R(23.5f, 14.5f, 7, 5, 1.5f).X(1.0f, 3)
                .P("M9 5v26M18 5v26M27 5v26"));

            return m;
        }

        // ---------- Lector de trazados SVG (M L H V C S Q T A Z, absolutos y relativos) ----------

        static class Svg
        {
            public static GraphicsPath Parse(string d)
            {
                var gp = new GraphicsPath();
                int i = 0, n = d.Length;
                char cmd = ' ', prev = ' ';
                float x = 0, y = 0, sx = 0, sy = 0, lcx = 0, lcy = 0;
                bool started = false;

                void Ws() { while (i < n && (d[i] == ' ' || d[i] == ',' || d[i] == '\t' || d[i] == '\r' || d[i] == '\n')) i++; }
                float Num()
                {
                    Ws();
                    int st = i;
                    if (i < n && (d[i] == '-' || d[i] == '+')) i++;
                    bool dot = false;
                    while (i < n && (char.IsDigit(d[i]) || (d[i] == '.' && !dot))) { if (d[i] == '.') dot = true; i++; }
                    if (i < n && (d[i] == 'e' || d[i] == 'E')) { i++; if (i < n && (d[i] == '-' || d[i] == '+')) i++; while (i < n && char.IsDigit(d[i])) i++; }
                    return float.Parse(d.AsSpan(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
                }
                bool Flag() { Ws(); bool v = d[i] == '1'; i++; return v; }

                while (true)
                {
                    Ws();
                    if (i >= n) break;
                    char ch = d[i];
                    if (char.IsLetter(ch) && ch != 'e' && ch != 'E') { cmd = ch; i++; }
                    else if (cmd == 'M') cmd = 'L';
                    else if (cmd == 'm') cmd = 'l';
                    bool rel = char.IsLower(cmd);
                    char C = char.ToUpperInvariant(cmd);
                    float ox = rel ? x : 0, oy = rel ? y : 0;
                    switch (C)
                    {
                        case 'M':
                            x = ox + Num(); y = oy + Num();
                            gp.StartFigure(); sx = x; sy = y; started = true;
                            break;
                        case 'L':
                        {
                            float nx = ox + Num(), ny = oy + Num();
                            gp.AddLine(x, y, nx, ny); x = nx; y = ny;
                            break;
                        }
                        case 'H': { float nx = ox + Num(); gp.AddLine(x, y, nx, y); x = nx; break; }
                        case 'V': { float ny = oy + Num(); gp.AddLine(x, y, x, ny); y = ny; break; }
                        case 'C':
                        {
                            float x1 = ox + Num(), y1 = oy + Num(), x2 = ox + Num(), y2 = oy + Num(), nx = ox + Num(), ny = oy + Num();
                            gp.AddBezier(x, y, x1, y1, x2, y2, nx, ny);
                            lcx = x2; lcy = y2; x = nx; y = ny;
                            break;
                        }
                        case 'S':
                        {
                            char pu = char.ToUpperInvariant(prev);
                            float x1 = pu == 'C' || pu == 'S' ? 2 * x - lcx : x, y1 = pu == 'C' || pu == 'S' ? 2 * y - lcy : y;
                            float x2 = ox + Num(), y2 = oy + Num(), nx = ox + Num(), ny = oy + Num();
                            gp.AddBezier(x, y, x1, y1, x2, y2, nx, ny);
                            lcx = x2; lcy = y2; x = nx; y = ny;
                            break;
                        }
                        case 'Q':
                        {
                            float qx = ox + Num(), qy = oy + Num(), nx = ox + Num(), ny = oy + Num();
                            Quad(gp, x, y, qx, qy, nx, ny);
                            lcx = qx; lcy = qy; x = nx; y = ny;
                            break;
                        }
                        case 'T':
                        {
                            char pu = char.ToUpperInvariant(prev);
                            float qx = pu == 'Q' || pu == 'T' ? 2 * x - lcx : x, qy = pu == 'Q' || pu == 'T' ? 2 * y - lcy : y;
                            float nx = ox + Num(), ny = oy + Num();
                            Quad(gp, x, y, qx, qy, nx, ny);
                            lcx = qx; lcy = qy; x = nx; y = ny;
                            break;
                        }
                        case 'A':
                        {
                            float rx = Num(), ry = Num(), rot = Num();
                            bool fa = Flag(), fs = Flag();
                            float nx = ox + Num(), ny = oy + Num();
                            Arc(gp, x, y, rx, ry, rot, fa, fs, nx, ny);
                            x = nx; y = ny;
                            break;
                        }
                        case 'Z':
                            gp.CloseFigure(); x = sx; y = sy;
                            break;
                        default:
                            throw new FormatException("Orden SVG no admitida: " + cmd);
                    }
                    prev = cmd;
                    if (!started) throw new FormatException("El trazado debe empezar con M");
                }
                return gp;
            }

            static void Quad(GraphicsPath gp, float x0, float y0, float qx, float qy, float x, float y)
            {
                gp.AddBezier(x0, y0, x0 + 2f / 3 * (qx - x0), y0 + 2f / 3 * (qy - y0), x + 2f / 3 * (qx - x), y + 2f / 3 * (qy - y), x, y);
            }

            // Arco elíptico SVG convertido a curvas de Bézier
            static void Arc(GraphicsPath gp, float x1, float y1, float rx, float ry, float rotDeg, bool fa, bool fs, float x2, float y2)
            {
                if (rx == 0 || ry == 0 || (x1 == x2 && y1 == y2)) { gp.AddLine(x1, y1, x2, y2); return; }
                double phi = rotDeg * Math.PI / 180, cs = Math.Cos(phi), sn = Math.Sin(phi);
                double dx = (x1 - x2) / 2.0, dy = (y1 - y2) / 2.0;
                double x1p = cs * dx + sn * dy, y1p = -sn * dx + cs * dy;
                double arx = Math.Abs(rx), ary = Math.Abs(ry);
                double lam = x1p * x1p / (arx * arx) + y1p * y1p / (ary * ary);
                if (lam > 1) { double k = Math.Sqrt(lam); arx *= k; ary *= k; }
                double num = arx * arx * ary * ary - arx * arx * y1p * y1p - ary * ary * x1p * x1p;
                double den = arx * arx * y1p * y1p + ary * ary * x1p * x1p;
                double coef = Math.Sqrt(Math.Max(0, num / den)) * (fa == fs ? -1 : 1);
                double cxp = coef * arx * y1p / ary, cyp = -coef * ary * x1p / arx;
                double cx = cs * cxp - sn * cyp + (x1 + x2) / 2.0, cy = sn * cxp + cs * cyp + (y1 + y2) / 2.0;
                double Ang(double ux, double uy, double vx, double vy) => Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
                double t1 = Ang(1, 0, (x1p - cxp) / arx, (y1p - cyp) / ary);
                double dt = Ang((x1p - cxp) / arx, (y1p - cyp) / ary, (-x1p - cxp) / arx, (-y1p - cyp) / ary);
                if (!fs && dt > 0) dt -= 2 * Math.PI; else if (fs && dt < 0) dt += 2 * Math.PI;
                int segs = Math.Max(1, (int)Math.Ceiling(Math.Abs(dt) / (Math.PI / 2) - 1e-6));
                double delta = dt / segs, t = 4.0 / 3 * Math.Tan(delta / 4);
                PointF Pt(double a) => new PointF((float)(cx + arx * Math.Cos(a) * cs - ary * Math.Sin(a) * sn), (float)(cy + arx * Math.Cos(a) * sn + ary * Math.Sin(a) * cs));
                (double, double) Dv(double a) => (-arx * Math.Sin(a) * cs - ary * Math.Cos(a) * sn, -arx * Math.Sin(a) * sn + ary * Math.Cos(a) * cs);
                double a1 = t1;
                for (int k = 0; k < segs; k++)
                {
                    double a2 = a1 + delta;
                    var p1 = Pt(a1); var p2 = Pt(a2);
                    var (d1x, d1y) = Dv(a1); var (d2x, d2y) = Dv(a2);
                    if (k == 0) p1 = new PointF(x1, y1);
                    if (k == segs - 1) p2 = new PointF(x2, y2);
                    gp.AddBezier(p1, new PointF((float)(p1.X + t * d1x), (float)(p1.Y + t * d1y)),
                                 new PointF((float)(p2.X - t * d2x), (float)(p2.Y - t * d2y)), p2);
                    a1 = a2;
                }
            }
        }
    }
}
