// Sistema de diseño: paleta (inspiración ferroviaria española) y controles con esquinas
// redondeadas y antialiasing. WinForms no trae nada de esto, se dibuja a mano.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public static class Theme
    {
        // Paleta Open Rails: verde del logo + grises + blanco.
        public static readonly Color Bg = Color.FromArgb(34, 36, 38);        // gris oscuro
        public static readonly Color BgSidebar = Color.FromArgb(27, 29, 31);
        public static readonly Color Surface = Color.FromArgb(45, 48, 51);
        public static readonly Color Surface2 = Color.FromArgb(58, 62, 66);
        public static readonly Color SurfaceHi = Color.FromArgb(74, 79, 84);
        public static readonly Color Border = Color.FromArgb(72, 77, 82);
        public static readonly Color Text = Color.FromArgb(240, 242, 243);   // casi blanco
        public static readonly Color Subtle = Color.FromArgb(170, 176, 181); // gris claro
        // Verde Open Rails
        public static readonly Color Accent = Color.FromArgb(76, 175, 80);
        public static readonly Color AccentHi = Color.FromArgb(102, 197, 106);
        public static readonly Color Accent2 = Color.FromArgb(46, 125, 50);  // verde oscuro (degradados)
        public static readonly Color Gold = Color.FromArgb(240, 196, 90);    // estrella de favoritos

        public static readonly string FontFamily = "Segoe UI";

        // ---- Escala de la interfaz ----
        // La maqueta está pensada para un escritorio de 1600×900 útiles. En pantallas menores
        // (portátiles de 1366×768, 1280×720…) todo se dibuja proporcionalmente más pequeño para que
        // quepa entero, y en monitores grandes crece un poco. TODAS las fuentes y las medidas que se
        // calculan en ejecución pasan por aquí, así que la interfaz entera encoge o crece a la vez.
        public const float DesignW = 1600f, DesignH = 900f;
        public static float UiScale { get; private set; } = 1f;

        public static void ComputeUiScale(Rectangle workArea)
        {
            float f = Math.Min(workArea.Width / DesignW, workArea.Height / DesignH);
            if (float.IsNaN(f) || f <= 0) f = 1f;
            // Solo se ENCOGE: en una pantalla mayor que el diseño se deja tal cual (crecer no hace
            // falta para que quepa y cambiaría el aspecto de quien ya lo tiene bien).
            f = Math.Max(0.70f, Math.Min(1f, f));
            // Ajuste manual opcional (SELECTOR_UISCALE=0,85), útil para pantallas raras.
            try
            {
                var s = Environment.GetEnvironmentVariable("SELECTOR_UISCALE");
                if (!string.IsNullOrWhiteSpace(s)
                    && float.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.InvariantCulture, out var manual)
                    && manual >= 0.6f && manual <= 1.4f)
                    f = manual;
            }
            catch { }
            UiScale = f;
        }

        // Mezcla de dos colores (0 = a, 1 = b). Las gráficas la usan para pistas y fondos.
        public static Color Mix(Color a, Color b, float t) =>
            Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        // Las letras se crean con el tamaño que tendrían a escala 100 % de Windows (DpiComp = 96/ppp).
        // La interfaz está maquetada en píxeles: si la letra crece con la escala de Windows (125 %,
        // 150 %…) y las cajas no, los textos, celdas y logotipos salen cortados en esos equipos.
        public static float DpiComp { get; private set; } = 1f;

        public static void InitDpi()
        {
            try
            {
                using var g = Graphics.FromHwnd(IntPtr.Zero);
                if (g.DpiX > 0) DpiComp = 96f / g.DpiX;
            }
            catch { DpiComp = 1f; }
        }

        public static Font Font(float size) => new Font(FontFamily, Math.Max(5f, size * UiScale) * DpiComp);
        public static Font Font(float size, FontStyle style) => new Font(FontFamily, Math.Max(5f, size * UiScale) * DpiComp, style);

        // Medida en píxeles a la escala actual (para los tamaños que se calculan en ejecución).
        public static int Px(double v) => (int)Math.Round(v * UiScale);

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) return p;   // rect degenerado (p. ej. al restaurar la ventana): path vacío, sin romper GDI+
            if (radius <= 0) { p.AddRectangle(r); p.CloseFigure(); return p; }
            d = Math.Min(d, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color c)
        {
            var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Round(r, radius)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
            g.SmoothingMode = old;
        }

        public static void DrawRoundBorder(Graphics g, Rectangle r, int radius, Color c, float w = 1f)
        {
            var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Round(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), radius))
            using (var pen = new Pen(c, w)) g.DrawPath(pen, p);
            g.SmoothingMode = old;
        }

        /// <summary>Color de fondo OPACO real de un control: sube por los padres saltando los transparentes.
        /// Se usa para pintar controles dibujados a mano de forma opaca (evita fantasmas de transparencia).</summary>
        public static Color ResolveBg(Control c)
        {
            var p = c?.Parent;
            while (p != null) { if (p.BackColor.A == 255) return p.BackColor; p = p.Parent; }
            return Bg;
        }
    }

    /// <summary>Panel con fondo redondeado y borde opcional.</summary>
    public class Card : Panel
    {
        public int Radius = 12;
        public Color Fill = Theme.Surface;
        public Color BorderColor = Color.Empty;
        public Card()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var r = ClientRectangle;
            // fondo opaco del contenedor (evita fantasmas de transparencia) + tarjeta redondeada encima
            using (var bg = new SolidBrush(Theme.ResolveBg(this))) e.Graphics.FillRectangle(bg, r);
            Theme.FillRound(e.Graphics, r, Radius, Fill);
            if (BorderColor != Color.Empty) Theme.DrawRoundBorder(e.Graphics, r, Radius, BorderColor);
            base.OnPaint(e);
        }
    }

    /// <summary>Botón dibujado a mano: redondeado, con hover/press, icono opcional y degradado opcional.</summary>
    public class RoundButton : Control
    {
        public int Radius = 10;
        public Color BaseColor = Theme.Surface2;
        public Color HoverColor = Theme.SurfaceHi;
        public Color TextColor = Theme.Text;
        public Color? GradientTo = null;   // si se define, degradado horizontal
        public bool Active = false;
        public Color ActiveColor = Theme.Accent;
        public Color ActiveTextColor = Color.White;
        public string Icon = null;
        public string GlyphKind = null;   // icono vectorial a color (ver Glyphs)
        public bool ShowGlyph = true;     // en ventanas estrechas se prescinde del icono para no cortar el texto
        public float FontSize = 9.5f;
        public FontStyle FontStyle = FontStyle.Regular;
        public bool Glossy = false;       // acabado pulido (brillo superior + borde) para botones principales
        public bool LeftAlign = false;    // icono + texto alineados a la izquierda (menú lateral)
        public int LeftPad = 14;          // sangría izquierda cuando LeftAlign
        public bool Tab = false;          // estilo PESTAÑA moderna: plano, texto gris y subrayado verde si está activa
        bool _hover, _down;

        public RoundButton()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Cursor = Cursors.Hand;
            Height = 34;
        }

        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnMouseEnter(EventArgs e) { if (!Enabled) return; _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (!Enabled) return; _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        // Pestaña moderna: sin relleno; hover con un tinte suave; activa = texto verde + barra inferior.
        void PaintTab(Graphics g)
        {
            var r = ClientRectangle;
            var bg = Theme.ResolveBg(this);
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
            if (_hover && !Active && Enabled)
                Theme.FillRound(g, new Rectangle(r.X + 2, r.Y + 3, r.Width - 4, r.Height - 8), 7, Blend(bg, Color.White, 0.05f));
            var tc = !Enabled ? Color.FromArgb(120, 125, 130) : Active ? Theme.AccentHi : (_hover ? Theme.Text : Theme.Subtle);
            using var f = Theme.Font(FontSize, Active ? FontStyle.Bold : FontStyle);
            var tsz = TextRenderer.MeasureText(g, Text, f, Size.Empty, TextFormatFlags.NoPadding);
            int gs = string.IsNullOrEmpty(GlyphKind) || !ShowGlyph ? 0 : Math.Min(r.Height - 16, 18);
            const int gap = 8;
            int content = tsz.Width + (gs > 0 ? gs + gap : 0);
            int x = r.X + Math.Max(4, (r.Width - content) / 2);
            int cy = r.Y + (r.Height - 3) / 2;
            if (gs > 0)
            {
                Glyphs.Draw(g, GlyphKind, new Rectangle(x, cy - gs / 2, gs, gs), tc);
                x += gs + gap;
            }
            TextRenderer.DrawText(g, Text, f, new Rectangle(x, r.Y, Math.Max(10, r.Right - x - 2), r.Height - 3), tc,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (Active)
            {
                int bw = Math.Min(r.Width - 8, content + 16);
                Theme.FillRound(g, new Rectangle(r.X + (r.Width - bw) / 2, r.Bottom - 3, bw, 3), 1, Theme.Accent);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Tab) { PaintTab(g); return; }
            var r = ClientRectangle;
            // fondo opaco del color del contenedor (evita artefactos de transparencia)
            using (var bg = new SolidBrush(Theme.ResolveBg(this))) g.FillRectangle(bg, r);
            bool off = !Enabled;   // atenuado cuando está deshabilitado
            Color fill = Active ? ActiveColor : (_down ? Blend(BaseColor, Color.Black, 0.15f) : (_hover ? HoverColor : BaseColor));
            if (off) fill = Blend(BaseColor, Theme.Bg, 0.5f);
            using (var path = Theme.Round(r, Radius))
            {
                if (!off && GradientTo.HasValue && !Active)
                {
                    using (var lg = new LinearGradientBrush(r, fill, GradientTo.Value, LinearGradientMode.Horizontal))
                        g.FillPath(lg, path);
                }
                else if (!off && Active && GradientTo.HasValue)
                {
                    using (var lg = new LinearGradientBrush(r, ActiveColor, Theme.Accent2, LinearGradientMode.Horizontal))
                        g.FillPath(lg, path);
                }
                else using (var b = new SolidBrush(fill)) g.FillPath(b, path);

                if (Glossy && !off)
                {
                    // brillo superior (gloss) recortado a la mitad de arriba
                    var top = new Rectangle(r.X, r.Y, r.Width, Math.Max(2, r.Height / 2));
                    var oldClip = g.Clip;
                    g.SetClip(path, System.Drawing.Drawing2D.CombineMode.Replace);
                    using (var gloss = new LinearGradientBrush(top, Color.FromArgb(_down ? 22 : 60, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                        g.FillRectangle(gloss, top);
                    g.Clip = oldClip;
                    // borde sutil claro para dar volumen
                    using (var edge = Theme.Round(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Radius))
                    using (var lp = new Pen(Color.FromArgb(70, 255, 255, 255), 1.2f))
                        g.DrawPath(lp, edge);
                }
            }
            var tc = off ? Color.FromArgb(135, 140, 146) : (Active ? ActiveTextColor : TextColor);
            using (var f = Theme.Font(FontSize, FontStyle))
            {
                if (!string.IsNullOrEmpty(GlyphKind) && ShowGlyph)
                {
                    int gs = Math.Min(r.Height - 10, 22);
                    var tsz = TextRenderer.MeasureText(g, Text, f, Size.Empty, TextFormatFlags.NoPadding);
                    const int gap = 10;
                    int total = gs + gap + tsz.Width;
                    int startX = LeftAlign ? r.X + LeftPad : r.X + Math.Max(6, (r.Width - total) / 2);
                    var gbox = new Rectangle(startX, r.Y + (r.Height - gs) / 2, gs, gs);
                    Glyphs.Draw(g, GlyphKind, gbox, tc);
                    var trect = new Rectangle(gbox.Right + gap, r.Y, r.Width - (gbox.Right + gap - r.X) - 4, r.Height);
                    TextRenderer.DrawText(g, Text, f, trect, tc, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
                else
                {
                    var txt = string.IsNullOrEmpty(Icon) ? Text : Icon + "  " + Text;
                    var align = LeftAlign
                        ? TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                        : TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
                    var trect = LeftAlign ? new Rectangle(r.X + LeftPad, r.Y, r.Width - LeftPad - 6, r.Height) : r;
                    TextRenderer.DrawText(g, txt, f, trect, tc, align);
                }
            }
        }

        static Color Blend(Color a, Color b, float t) =>
            Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
    }

    /// <summary>Caja de texto con marco redondeado (fondo Surface2), con placeholder.</summary>
    public class RoundedInput : Card
    {
        public TextBox Box { get; }
        public RoundedInput(string placeholder = null)
        {
            Radius = 9; Fill = Theme.Surface2; BorderColor = Theme.Border;
            Padding = new Padding(12, 7, 10, 7);
            Height = 34;
            Box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Surface2,
                ForeColor = Theme.Text,
                Dock = DockStyle.Fill,
                Font = Theme.Font(10f)
            };
            if (placeholder != null) Box.PlaceholderText = placeholder;
            Controls.Add(Box);
        }
    }

    /// <summary>Casilla de verificación dibujada, con estado marcado bien visible (verde + check).</summary>
    public class ThemeCheck : CheckBox
    {
        bool _hover;
        public ThemeCheck()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            AutoSize = false; Height = 28; Cursor = Cursors.Hand;
            Font = Theme.Font(9.5f);
        }
        protected override bool ShowFocusCues => false;   // sin rectángulo de foco (dejaba líneas)
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;   // relleno con bordes duros (evita seam de 1px)
            using (var bg = new SolidBrush(Theme.ResolveBg(this))) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int s = 18, y = (Height - s) / 2;
            var box = new Rectangle(0, y, s, s);
            if (Checked)
            {
                using (var p = Theme.Round(box, 5))
                using (var lg = new LinearGradientBrush(box, Theme.AccentHi, Theme.Accent2, LinearGradientMode.Vertical)) g.FillPath(lg, p);
                using (var pen = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(pen, new[] { new Point(box.X + 4, box.Y + 9), new Point(box.X + 8, box.Y + 13), new Point(box.X + 14, box.Y + 5) });
            }
            else
            {
                Theme.FillRound(g, box, 5, Theme.Surface2);
                Theme.DrawRoundBorder(g, box, 5, _hover ? Theme.Accent : Theme.Border, 1.4f);
            }
            var tr = new Rectangle(s + 8, 0, Width - s - 8, Height);
            TextRenderer.DrawText(g, Text, Font, tr, Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Botón de opción dibujado, con estado marcado bien visible (punto verde).</summary>
    public class ThemeRadio : RadioButton
    {
        bool _hover;
        public ThemeRadio()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            AutoSize = false; Height = 28; Cursor = Cursors.Hand;
            Font = Theme.Font(10f);
        }
        protected override bool ShowFocusCues => false;   // sin rectángulo de foco (dejaba líneas)
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;   // relleno con bordes duros (evita seam de 1px)
            using (var bg = new SolidBrush(Theme.ResolveBg(this))) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int s = 18, y = (Height - s) / 2;
            var box = new Rectangle(0, y, s, s);
            using (var b = new SolidBrush(Theme.Surface2)) g.FillEllipse(b, box);
            using (var pen = new Pen(Checked ? Theme.Accent : (_hover ? Theme.Accent : Theme.Border), 1.6f)) g.DrawEllipse(pen, box);
            if (Checked) using (var b = new SolidBrush(Theme.AccentHi)) g.FillEllipse(b, box.X + 5, box.Y + 5, s - 10, s - 10);
            var tr = new Rectangle(s + 8, 0, Width - s - 8, Height);
            TextRenderer.DrawText(g, Text, Font, tr, Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Franja fina con degradado verde (Open Rails).</summary>
    public class LiveryStripe : Control
    {
        public LiveryStripe()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Height = 3;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var r = ClientRectangle;
            using (var lg = new LinearGradientBrush(r, Theme.Accent, Theme.Accent2, LinearGradientMode.Horizontal))
                e.Graphics.FillRectangle(lg, r);
        }
    }

    /// <summary>Logotipo del proyecto "SelectOR": anillo verde estilo Open Rails + texto.</summary>
    public class SelectorLogo : Control
    {
        public SelectorLogo()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Height = 40; Width = 230;
        }

        // Marca: placa verde con vías en perspectiva (raíles convergentes + traviesas).
        public static void DrawRing(Graphics g, Rectangle box)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // placa verde redondeada con degradado
            using (var path = Theme.Round(box, box.Width / 4))
            using (var lg = new LinearGradientBrush(box, Theme.AccentHi, Theme.Accent2, LinearGradientMode.Vertical))
                g.FillPath(lg, path);

            var clipOld = g.Clip;
            using (var clip = Theme.Round(box, box.Width / 4)) g.SetClip(clip);

            int topY = box.Y + (int)(box.Height * 0.20f);
            int botY = box.Bottom - (int)(box.Height * 0.12f);
            int cxTop = box.X + box.Width / 2;
            int spreadTop = (int)(box.Width * 0.10f);
            int spreadBot = (int)(box.Width * 0.34f);
            // traviesas (más cortas y juntas hacia arriba)
            using (var sp = new Pen(Color.FromArgb(210, 255, 255, 255), Math.Max(1.4f, box.Width * 0.05f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                for (int i = 0; i <= 4; i++)
                {
                    float t = i / 4f;
                    int y = (int)(topY + (botY - topY) * t);
                    int half = (int)(spreadTop + (spreadBot - spreadTop) * t) + (int)(box.Width * 0.03f);
                    g.DrawLine(sp, cxTop - half, y, cxTop + half, y);
                }
            // raíles
            using (var rp = new Pen(Color.White, Math.Max(1.8f, box.Width * 0.07f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(rp, cxTop - spreadTop, topY, cxTop - spreadBot, botY);
                g.DrawLine(rp, cxTop + spreadTop, topY, cxTop + spreadBot, botY);
            }
            g.Clip = clipOld;

            // cursor de ratón (selección de ruta), más grande para mayor claridad
            float u = box.Width / 100f * 1.5f;
            int px = box.X + (int)(box.Width * 0.55f), py = box.Y + (int)(box.Height * 0.38f);
            using (var cur = new GraphicsPath())
            {
                cur.AddPolygon(new[] {
                    new PointF(px, py),
                    new PointF(px, py + 26 * u),
                    new PointF(px + 7 * u, py + 19 * u),
                    new PointF(px + 12 * u, py + 30 * u),
                    new PointF(px + 16 * u, py + 28 * u),
                    new PointF(px + 11 * u, py + 17 * u),
                    new PointF(px + 20 * u, py + 17 * u),
                });
                // Puntero blanco con CONTORNO NEGRO grueso para que destaque a simple vista.
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, cur);
                using (var pen = new Pen(Color.Black, Math.Max(1.8f, 2.4f * u)) { LineJoin = LineJoin.Round }) g.DrawPath(pen, cur);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Theme.ResolveBg(this))) g.FillRectangle(bg, ClientRectangle);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int size = Math.Min(Height - 6, 34);
            var ring = new Rectangle(2, (Height - size) / 2, size, size);
            DrawRing(g, ring);
            int x = ring.Right + 10;
            using (var f = Theme.Font(17f, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, "Select", f, new Point(x - 2, (Height - 26) / 2), Theme.Text, TextFormatFlags.NoPadding);
                var w = TextRenderer.MeasureText(g, "Select", f, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g, "OR", f, new Point(x - 2 + w, (Height - 26) / 2), Theme.Accent, TextFormatFlags.NoPadding);
            }
        }
    }

    // ComboBox tematizado por completo: además del texto/items (owner-draw), pinta ENCIMA el botón de la
    // flecha y el borde con los colores del tema (WinForms deja ese botón en color del sistema, claro).
    public class ThemeCombo : ComboBox
    {
        const int WM_PAINT = 0x000F;
        public ThemeCombo()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            DrawMode = DrawMode.OwnerDrawFixed;
        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != WM_PAINT) return;
            using (var g = Graphics.FromHwnd(Handle))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int bw = 22;
                var btn = new Rectangle(Width - bw, 0, bw, Height);
                using (var b = new SolidBrush(Theme.Surface2)) g.FillRectangle(b, btn);
                int cx = btn.X + btn.Width / 2, cy = Height / 2, s = 4;
                using (var p = new Pen(Enabled ? Theme.Subtle : Color.FromArgb(90, Theme.Subtle), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(p, new[] { new Point(cx - s, cy - 2), new Point(cx, cy + s - 1), new Point(cx + s, cy - 2) });
                using (var bp = new Pen(Color.FromArgb(90, 0, 0, 0), 1f))
                    g.DrawRectangle(bp, 0, 0, Width - 1, Height - 1);
            }
        }
    }
}
