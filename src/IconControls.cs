// Controles con iconos vectoriales (estación, clima, día) y un deslizador de hora elegante.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    /// <summary>Selector segmentado con iconos vectoriales dibujados a mano. Selección única.</summary>
    public class Segmented : Control
    {
        public string[] Kinds;      // claves de glifo o "txt:LETRA"
        public string[] Tips;
        int _sel;
        int _hover = -1;
        public event Action Changed;

        public int SelectedIndex
        {
            get => _sel;
            set { _sel = value; Invalidate(); }
        }

        ToolTip _tip = new ToolTip();

        public Segmented(string[] kinds, string[] tips = null)
        {
            Kinds = kinds; Tips = tips;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Height = 40;
            Cursor = Cursors.Hand;
        }

        int IndexAt(int x)
        {
            if (Kinds == null || Kinds.Length == 0) return -1;
            int seg = Width / Kinds.Length;
            if (seg <= 0) return -1;
            int i = x / seg;
            return Math.Max(0, Math.Min(Kinds.Length - 1, i));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.X);
            if (i != _hover)
            {
                _hover = i; Invalidate();
                if (Tips != null && i >= 0 && i < Tips.Length) _tip.SetToolTip(this, Tips[i]);
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = IndexAt(e.X);
            if (i >= 0) { _sel = i; Invalidate(); Changed?.Invoke(); }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            using (var bg = new SolidBrush(Theme.ResolveBg(this))) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int n = Kinds.Length;
            float seg = (float)Width / n;
            Theme.FillRound(g, new Rectangle(0, 0, Width, Height), 10, Theme.Surface2);
            for (int i = 0; i < n; i++)
            {
                var r = new RectangleF(i * seg, 0, seg, Height);
                bool sel = i == _sel;
                if (sel)
                {
                    var rr = Rectangle.Round(new RectangleF(r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6));
                    using (var lg = new LinearGradientBrush(rr, Theme.Accent, Theme.Accent2, LinearGradientMode.Vertical))
                    using (var pth = Theme.Round(rr, 8)) g.FillPath(lg, pth);
                }
                else if (i == _hover)
                {
                    var rr = Rectangle.Round(new RectangleF(r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6));
                    Theme.FillRound(g, rr, 8, Theme.SurfaceHi);
                }
                var glyphColor = sel ? Color.White : Color.FromArgb(196, 200, 205);
                int gs = Math.Min(26, Height - 12);
                var box = new Rectangle((int)(r.X + r.Width / 2 - gs / 2), (int)(r.Y + r.Height / 2 - gs / 2), gs, gs);
                Glyphs.Draw(g, Kinds[i], box, glyphColor);
            }
        }
    }

    public static class Glyphs
    {
        // Colores tipo emoji
        static readonly Color SunColor = Color.FromArgb(255, 193, 7);
        static readonly Color SunRay = Color.FromArgb(255, 152, 0);
        static readonly Color LeafColor = Color.FromArgb(239, 108, 45);
        static readonly Color SnowColor = Color.FromArgb(129, 199, 245);
        static readonly Color CloudColor = Color.FromArgb(176, 190, 197);
        static readonly Color DropColor = Color.FromArgb(66, 165, 245);
        static readonly Color PetalColor = Color.FromArgb(244, 143, 177);
        static readonly Color PetalCore = Color.FromArgb(255, 213, 79);

        public static void Draw(Graphics g, string kind, Rectangle b, Color textColor)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (kind != null && kind.StartsWith("txt:"))
            {
                using (var f = Theme.Font(b.Height * 0.52f, FontStyle.Bold))
                    TextRenderer.DrawText(g, kind.Substring(4), f, b, textColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            int cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
            switch (kind)
            {
                case "summer":
                case "clear": DrawSun(g, b, cx, cy); break;
                case "spring": DrawFlower(g, b, cx, cy); break;
                case "autumn": DrawLeaf(g, b, cx, cy); break;
                case "winter":
                case "snow": DrawSnow(g, b, cx, cy); break;
                case "rain": DrawRain(g, b, cx, cy); break;
                case "activity": DrawFlag(g, b, cx, cy); break;
                case "explore": DrawCompass(g, b, cx, cy); break;
                case "clock": DrawClock(g, b, cx, cy); break;
                case "globe": DrawGlobe(g, b, cx, cy); break;
                case "map": DrawPin(g, b, cx, cy); break;
                case "play": DrawPlay(g, b, cx, cy); break;
                case "train": DrawTrain(g, b, cx, cy); break;
                case "connect": DrawConnect(g, b, cx, cy); break;
                case "gear": DrawGear(g, b, cx, cy); break;
                case "info": DrawInfo(g, b, cx, cy); break;
                case "bank": DrawBank(g, b, cx, cy); break;
                case "speaker": DrawSpeaker(g, b, cx, cy); break;
            }
        }

        // Altavoz con dos ondas (megafonía).
        static void DrawSpeaker(Graphics g, Rectangle b, int cx, int cy)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var col = new SolidBrush(Color.FromArgb(210, 214, 220));
            using var pen = new Pen(Color.FromArgb(210, 214, 220), Math.Max(1.4f, b.Height * 0.09f));
            int h = (int)(b.Height * 0.62f);                 // alto del cuerpo del altavoz
            int left = cx - (int)(b.Width * 0.30f);          // caja pequeña pegada a la izquierda
            int boxW = Math.Max(2, (int)(b.Width * 0.14f));
            int boxH = Math.Max(2, h / 2);
            g.FillRectangle(col, left, cy - boxH / 2, boxW, boxH);
            // cono (trapecio) hacia la derecha
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                int x0 = left + boxW, x1 = x0 + (int)(b.Width * 0.18f);
                path.AddPolygon(new[]
                {
                    new Point(x0, cy - boxH / 2), new Point(x1, cy - h / 2),
                    new Point(x1, cy + h / 2),    new Point(x0, cy + boxH / 2)
                });
                g.FillPath(col, path);
            }
            // dos ondas
            int wx = left + boxW + (int)(b.Width * 0.22f);
            for (int i = 0; i < 2; i++)
            {
                int r = (int)(b.Height * (0.20f + i * 0.16f));
                g.DrawArc(pen, wx - r, cy - r, r * 2, r * 2, -55, 110);
            }
            g.SmoothingMode = old;
        }

        // Bandera a cuadros (meta) — icono de "actividad" neutro y visible sobre cualquier fondo
        // (antes era verde y no se distinguía sobre el acento magenta ni sobre la superficie).
        static void DrawFlag(Graphics g, Rectangle b, int cx, int cy)
        {
            var old = g.SmoothingMode;
            int px = b.X + (int)(b.Width * 0.30f);
            using (var pole = new Pen(Color.FromArgb(210, 214, 220), Math.Max(1.8f, b.Width * 0.07f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pole, px, b.Y + 1, px, b.Bottom - 1);
            // paño ondeante: rejilla 4×3 de cuadros blancos/negros
            int fw = b.Right - 2 - px, fh = (int)(b.Height * 0.46f), fy = b.Y + 2;
            if (fw < 4 || fh < 3) return;
            g.SmoothingMode = SmoothingMode.None;
            int cols = 4, rows = 3;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int x0 = px + c * fw / cols, x1 = px + (c + 1) * fw / cols;
                    int y0 = fy + r * fh / rows, y1 = fy + (r + 1) * fh / rows;
                    bool dark = ((r + c) & 1) == 0;
                    using (var sb = new SolidBrush(dark ? Color.FromArgb(35, 38, 45) : Color.FromArgb(240, 242, 245)))
                        g.FillRectangle(sb, x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
                }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var border = new Pen(Color.FromArgb(120, 60, 66, 74), Math.Max(1f, b.Width * 0.03f)))
                g.DrawRectangle(border, px, fy, fw, fh);
            g.SmoothingMode = old;
        }

        static void DrawCompass(Graphics g, Rectangle b, int cx, int cy)
        {
            int r = (int)(b.Width * 0.42f);
            using (var pen = new Pen(Color.FromArgb(200, 205, 210), Math.Max(1.6f, b.Width * 0.06f))) g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
            using (var np = new SolidBrush(Color.FromArgb(229, 57, 53)))
            using (var p1 = new GraphicsPath()) { p1.AddPolygon(new[] { new Point(cx, cy - r + 2), new Point(cx - 4, cy), new Point(cx + 4, cy) }); g.FillPath(np, p1); }
            using (var sp = new SolidBrush(Color.FromArgb(224, 228, 232)))
            using (var p2 = new GraphicsPath()) { p2.AddPolygon(new[] { new Point(cx, cy + r - 2), new Point(cx - 4, cy), new Point(cx + 4, cy) }); g.FillPath(sp, p2); }
        }

        static void DrawClock(Graphics g, Rectangle b, int cx, int cy)
        {
            int r = (int)(b.Width * 0.44f);
            using (var face = new SolidBrush(Color.FromArgb(236, 239, 241))) g.FillEllipse(face, cx - r, cy - r, r * 2, r * 2);
            using (var ring = new Pen(Color.FromArgb(120, 130, 138), 1.6f)) g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
            using (var hp = new Pen(Color.FromArgb(46, 125, 50), Math.Max(1.6f, b.Width * 0.06f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(hp, cx, cy, cx, cy - (int)(r * 0.6f));
                g.DrawLine(hp, cx, cy, cx + (int)(r * 0.55f), cy + (int)(r * 0.15f));
            }
        }

        static void DrawGlobe(Graphics g, Rectangle b, int cx, int cy)
        {
            int r = (int)(b.Width * 0.44f);
            using (var oc = new SolidBrush(Color.FromArgb(66, 165, 245))) g.FillEllipse(oc, cx - r, cy - r, r * 2, r * 2);
            using (var land = new SolidBrush(Color.FromArgb(102, 187, 106)))
            {
                g.FillEllipse(land, cx - r + 2, cy - 3, r, (int)(r * 0.7f));
                g.FillEllipse(land, cx + 1, cy - r + 3, (int)(r * 0.8f), r);
            }
            using (var mp = new Pen(Color.FromArgb(120, 255, 255, 255), 1f))
            {
                g.DrawEllipse(mp, cx - r, cy - r, r * 2, r * 2);
                g.DrawEllipse(mp, cx - (int)(r * 0.5f), cy - r, r, r * 2);
                g.DrawLine(mp, cx - r, cy, cx + r, cy);
            }
        }

        static void DrawPin(Graphics g, Rectangle b, int cx, int cy)
        {
            int w = (int)(b.Width * 0.5f), h = (int)(b.Height * 0.8f);
            var top = new Rectangle(cx - w / 2, b.Y + 2, w, w);
            using (var path = new GraphicsPath())
            {
                path.AddArc(top, 160, 220);
                path.AddLine(top.X + (int)(w * 0.08f), top.Bottom - (int)(w * 0.15f), cx, b.Y + 2 + h);
                path.AddLine(cx, b.Y + 2 + h, top.Right - (int)(w * 0.08f), top.Bottom - (int)(w * 0.15f));
                path.CloseFigure();
                using (var pb = new SolidBrush(Color.FromArgb(229, 57, 53))) g.FillPath(pb, path);
            }
            using (var dot = new SolidBrush(Color.White)) g.FillEllipse(dot, cx - w / 6, top.Y + top.Height / 3, w / 3, w / 3);
        }

        static void DrawPlay(Graphics g, Rectangle b, int cx, int cy)
        {
            using (var tb = new SolidBrush(Color.White))
            using (var p = new GraphicsPath())
            {
                int r = (int)(b.Width * 0.34f);
                p.AddPolygon(new[] { new Point(cx - r + 3, cy - r), new Point(cx + r, cy), new Point(cx - r + 3, cy + r) });
                g.FillPath(tb, p);
            }
        }

        static void DrawTrain(Graphics g, Rectangle b, int cx, int cy)
        {
            var body = new Rectangle(b.X + 2, b.Y + (int)(b.Height * 0.22f), b.Width - 4, (int)(b.Height * 0.5f));
            using (var bb = new SolidBrush(Color.FromArgb(120, 144, 156)))
            using (var p = Theme.Round(body, body.Height / 3)) g.FillPath(bb, p);
            using (var win = new SolidBrush(Color.FromArgb(129, 212, 250)))
                for (int i = 0; i < 3; i++) g.FillRectangle(win, body.X + 3 + i * (body.Width / 3), body.Y + 3, body.Width / 5, body.Height / 3);
            using (var wh = new SolidBrush(Color.FromArgb(60, 64, 68)))
            { g.FillEllipse(wh, body.X + 3, body.Bottom - 3, 6, 6); g.FillEllipse(wh, body.Right - 9, body.Bottom - 3, 6, 6); }
        }

        static void DrawConnect(Graphics g, Rectangle b, int cx, int cy)
        {
            using (var pen = new Pen(Color.White, Math.Max(2f, b.Width * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                int r = (int)(b.Width * 0.18f);
                g.DrawArc(pen, cx - r * 2, cy - r, r * 2, r * 2, 40, 260);
                g.DrawArc(pen, cx, cy - r, r * 2, r * 2, 220, 260);
                g.DrawLine(pen, cx - r / 2, cy, cx + r / 2, cy);
            }
        }


        static void DrawGear(Graphics g, Rectangle b, int cx, int cy)
        {
            int r = (int)(b.Width * 0.30f), r2 = (int)(b.Width * 0.42f);
            using (var gb = new SolidBrush(Color.FromArgb(176, 190, 197)))
            {
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    var t = new Rectangle(cx - 2, cy - r2, 4, r2 - r + 3);
                    var st = g.Save(); g.TranslateTransform(cx, cy); g.RotateTransform((float)(a * 180 / Math.PI)); g.TranslateTransform(-cx, -cy);
                    g.FillRectangle(gb, cx - 3, cy - r2, 6, r2 - r + 4); g.Restore(st);
                }
                g.FillEllipse(gb, cx - r, cy - r, r * 2, r * 2);
            }
            using (var hole = new SolidBrush(Theme.Surface2)) g.FillEllipse(hole, cx - r / 2, cy - r / 2, r, r);
        }

        // Edificio con frontón y columnas (banca / empresa)
        static void DrawBank(Graphics g, Rectangle b, int cx, int cy)
        {
            int w = (int)(b.Width * 0.72f), left = cx - w / 2, right = cx + w / 2;
            int top = b.Y + (int)(b.Height * 0.16f);
            int baseY = b.Bottom - (int)(b.Height * 0.14f);
            int roofY = top + (int)(b.Height * 0.20f);
            using var col = new SolidBrush(Color.FromArgb(210, 214, 220));
            // frontón (triángulo)
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddPolygon(new[] { new Point(cx, top), new Point(left, roofY), new Point(right, roofY) });
                g.FillPath(col, path);
            }
            // base
            g.FillRectangle(col, left, baseY, right - left, Math.Max(2, (int)(b.Height * 0.10f)));
            // columnas
            int n = 3, cw = Math.Max(2, (int)(w * 0.13f));
            int span = right - left - cw;
            for (int i = 0; i < n; i++)
            {
                int x = left + (n == 1 ? span / 2 : i * span / (n - 1));
                g.FillRectangle(col, x, roofY + 2, cw, baseY - roofY - 3);
            }
        }

        static void DrawInfo(Graphics g, Rectangle b, int cx, int cy)
        {
            int r = (int)(b.Width * 0.44f);
            using (var cb = new SolidBrush(Color.FromArgb(76, 175, 80))) g.FillEllipse(cb, cx - r, cy - r, r * 2, r * 2);
            using (var f = Theme.Font(b.Height * 0.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, "i", f, new Rectangle(cx - r, cy - r, r * 2, r * 2), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        static void DrawSun(Graphics g, Rectangle b, int cx, int cy)
        {
            int rad = (int)(b.Width * 0.24f);
            using (var pen = new Pen(SunRay, Math.Max(2f, b.Width * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                for (int a = 0; a < 8; a++)
                {
                    double ang = a * Math.PI / 4;
                    g.DrawLine(pen, cx + (int)(Math.Cos(ang) * (rad + 2)), cy + (int)(Math.Sin(ang) * (rad + 2)),
                                     cx + (int)(Math.Cos(ang) * (rad + 6)), cy + (int)(Math.Sin(ang) * (rad + 6)));
                }
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(cx - rad, cy - rad, rad * 2, rad * 2);
                using (var pgb = new PathGradientBrush(path) { CenterColor = Color.FromArgb(255, 224, 130), SurroundColors = new[] { SunColor } })
                    g.FillPath(pgb, path);
            }
        }

        static void DrawFlower(Graphics g, Rectangle b, int cx, int cy)
        {
            int pr = (int)(b.Width * 0.17f);
            using (var br = new SolidBrush(PetalColor))
                for (int a = 0; a < 5; a++)
                {
                    double ang = a * 2 * Math.PI / 5 - Math.PI / 2;
                    int px = cx + (int)(Math.Cos(ang) * pr * 1.1), py = cy + (int)(Math.Sin(ang) * pr * 1.1);
                    g.FillEllipse(br, px - pr, py - pr, pr * 2, pr * 2);
                }
            using (var cb = new SolidBrush(PetalCore)) g.FillEllipse(cb, cx - pr * 3 / 4, cy - pr * 3 / 4, pr * 3 / 2, pr * 3 / 2);
        }

        static void DrawLeaf(Graphics g, Rectangle b, int cx, int cy)
        {
            using (var leaf = new GraphicsPath())
            {
                leaf.AddBezier(cx - 1, b.Y + 2, b.Right - 3, cy - 5, b.Right - 3, cy + 5, cx - 1, b.Bottom - 2);
                leaf.AddBezier(cx - 1, b.Bottom - 2, b.X + 3, cy + 5, b.X + 3, cy - 5, cx - 1, b.Y + 2);
                using (var br = new LinearGradientBrush(b, Color.FromArgb(255, 152, 0), LeafColor, LinearGradientMode.Vertical))
                    g.FillPath(br, leaf);
                using (var vp = new Pen(Color.FromArgb(150, 80, 20), 1.4f)) g.DrawLine(vp, cx - 1, b.Y + 5, cx - 1, b.Bottom - 4);
            }
        }

        static void DrawSnow(Graphics g, Rectangle b, int cx, int cy)
        {
            using (var pen = new Pen(SnowColor, Math.Max(2f, b.Width * 0.085f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                for (int a = 0; a < 6; a++)
                {
                    double ang = a * Math.PI / 3;
                    int ex = cx + (int)(Math.Cos(ang) * b.Width / 2.6), ey = cy + (int)(Math.Sin(ang) * b.Width / 2.6);
                    g.DrawLine(pen, cx, cy, ex, ey);
                    // pequeñas ramas
                    int mx = cx + (int)(Math.Cos(ang) * b.Width / 4), my = cy + (int)(Math.Sin(ang) * b.Width / 4);
                    g.DrawLine(pen, mx, my, mx + (int)(Math.Cos(ang + 0.7) * b.Width / 8), my + (int)(Math.Sin(ang + 0.7) * b.Width / 8));
                    g.DrawLine(pen, mx, my, mx + (int)(Math.Cos(ang - 0.7) * b.Width / 8), my + (int)(Math.Sin(ang - 0.7) * b.Width / 8));
                }
        }

        static void DrawRain(Graphics g, Rectangle b, int cx, int cy)
        {
            var cloud = new Rectangle(b.X + 1, b.Y + 2, b.Width - 2, (int)(b.Height * 0.5f));
            using (var br = new SolidBrush(CloudColor))
            {
                g.FillEllipse(br, cloud.X, cloud.Y + cloud.Height / 3, cloud.Width / 2, cloud.Height * 2 / 3);
                g.FillEllipse(br, cloud.X + cloud.Width / 3, cloud.Y, cloud.Width * 2 / 3, cloud.Height);
                g.FillEllipse(br, cloud.X + cloud.Width / 4, cloud.Y + cloud.Height / 4, cloud.Width / 2, cloud.Height * 3 / 4);
            }
            using (var pen = new Pen(DropColor, Math.Max(2f, b.Width * 0.08f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                for (int i = 0; i < 3; i++)
                {
                    int dx = b.X + 5 + i * (b.Width - 10) / 2;
                    g.DrawLine(pen, dx, b.Bottom - 8, dx - 2, b.Bottom - 2);
                }
        }
    }

    /// <summary>Deslizador de hora (0–23:59) con degradado día/noche y campo editable HH:MM.</summary>
    public class HourSlider : Control
    {
        int _minutes = 12 * 60;
        bool _drag, _syncing;
        public event Action Changed;
        readonly TextBox _entry;

        public int Minutes { get => _minutes; set { _minutes = Math.Max(0, Math.Min(1439, value)); SyncEntry(); Invalidate(); } }
        public string TimeText => $"{_minutes / 60:00}:{_minutes % 60:00}";

        public HourSlider()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Height = 40; Cursor = Cursors.Hand;
            _entry = new TextBox
            {
                BorderStyle = BorderStyle.None, BackColor = Theme.Surface2, ForeColor = Theme.Text,
                Font = Theme.Font(11f, FontStyle.Bold), TextAlign = HorizontalAlignment.Center,
                Width = 50, Height = 22, MaxLength = 5, Text = TimeText
            };
            _entry.Enter += (s, e) => Cursor = Cursors.IBeam;
            _entry.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ParseEntry(); e.SuppressKeyPress = true; } };
            _entry.Leave += (s, e) => ParseEntry();
            Controls.Add(_entry);
        }

        protected override void OnResize(EventArgs e) { if (_entry != null) _entry.Location = new Point(Width - 54, (Height - _entry.Height) / 2); base.OnResize(e); }

        int TrackX0 => 8;
        int TrackX1 => Width - 64;

        void SyncEntry() { _syncing = true; _entry.Text = TimeText; _syncing = false; }

        void ParseEntry()
        {
            if (_syncing) return;
            var t = _entry.Text.Trim();
            int h = -1, m = 0;
            var parts = t.Split(':', '.', 'h', 'H');
            if (parts.Length >= 1 && int.TryParse(parts[0], out h))
            {
                if (parts.Length >= 2 && !int.TryParse(parts[1], out m)) m = 0;
                if (h >= 0 && h <= 23 && m >= 0 && m <= 59) { _minutes = h * 60 + m; Invalidate(); Changed?.Invoke(); }
            }
            SyncEntry();
        }

        void SetFromX(int x)
        {
            int x0 = TrackX0, x1 = TrackX1;
            float t = (float)(x - x0) / Math.Max(1, x1 - x0);
            t = Math.Max(0, Math.Min(1, t));
            // pasos de 5 min; se limita a 23:55 (1435) para que NUNCA llegue a 24:00 (1440).
            _minutes = Math.Min(1435, (int)Math.Round(t * 1439f / 5f) * 5);
            SyncEntry(); Invalidate(); Changed?.Invoke();
        }

        protected override void OnMouseDown(MouseEventArgs e) { if (e.X < TrackX1 + 10) { _drag = true; SetFromX(e.X); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _drag = false; base.OnMouseUp(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (_drag) SetFromX(e.X); base.OnMouseMove(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            using (var bg = new SolidBrush(Theme.ResolveBg(this))) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int x0 = TrackX0, x1 = TrackX1, cy = Height / 2;
            var track = new Rectangle(x0, cy - 5, x1 - x0, 10);
            using (var lg = new LinearGradientBrush(track, Color.Black, Color.Black, LinearGradientMode.Horizontal))
            {
                var blend = new ColorBlend(5)
                {
                    Colors = new[] { Color.FromArgb(30, 40, 70), Color.FromArgb(230, 150, 60), Color.FromArgb(120, 190, 235), Color.FromArgb(230, 120, 60), Color.FromArgb(30, 40, 70) },
                    Positions = new[] { 0f, 0.28f, 0.5f, 0.75f, 1f }
                };
                lg.InterpolationColors = blend;
                using (var pth = Theme.Round(track, 5)) g.FillPath(lg, pth);
            }
            float t = _minutes / 1439f;
            int kx = x0 + (int)(t * (x1 - x0));
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, kx - 8, cy - 9, 16, 18);
            using (var p = new Pen(Theme.Accent, 2.5f)) g.DrawEllipse(p, kx - 8, cy - 9, 16, 18);
        }
    }
}
