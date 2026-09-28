// Paneles de dibujo para el banner de la ruta y la vista previa del tren.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    /// <summary>Banner redondeado con la imagen de la ruta (cover), degradado y titulo.</summary>
    public class BannerPanel : Panel
    {
        Bitmap _image;
        public string RouteName = "";
        public string Placeholder = "Selecciona una ruta para empezar";
        public int Radius = 14;

        public BannerPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public Bitmap Image { get => _image; set { _image = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = ClientRectangle;
            using (var bg = new SolidBrush(Theme.ResolveBg(this))) g.FillRectangle(bg, rect);
            using (var clip = Theme.Round(rect, Radius))
            {
                g.SetClip(clip);
                using (var b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, rect);

                if (_image != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    double s = Math.Max((double)rect.Width / _image.Width, (double)rect.Height / _image.Height);
                    int w = (int)(_image.Width * s), h = (int)(_image.Height * s);
                    g.DrawImage(_image, rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h);
                    // viñeta general para dar profundidad
                    using (var lg = new LinearGradientBrush(rect, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(90, 0, 0, 0), LinearGradientMode.Vertical))
                        g.FillRectangle(lg, rect);
                }
                g.ResetClip();

                if (string.IsNullOrEmpty(RouteName))
                {
                    using (var f = Theme.Font(12f))
                        TextRenderer.DrawText(g, I18n.T(Placeholder), f, rect, Theme.Subtle,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                else
                {
                    // Solo el nombre, centrado, con difuminado detrás. La descripción va en la franja inferior.
                    var nf = Theme.Font(21f, FontStyle.Bold);
                    var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis;
                    int maxW = rect.Width - 80;
                    var nameSize = TextRenderer.MeasureText(g, RouteName, nf, new Size(maxW, 70), flags);
                    int cx = rect.X + rect.Width / 2;
                    int top = rect.Y + (rect.Height - nameSize.Height) / 2;
                    // banda horizontal difuminada (arriba/abajo transparente) que oscurece la franja del título
                    int bandH = nameSize.Height + 70;
                    var band = new Rectangle(rect.X, top + nameSize.Height / 2 - bandH / 2, rect.Width, bandH);
                    using (var vb = new LinearGradientBrush(band, Color.Transparent, Color.Transparent, LinearGradientMode.Vertical))
                    {
                        vb.InterpolationColors = new ColorBlend(4)
                        {
                            Colors = new[] { Color.FromArgb(0, 0, 0, 0), Color.FromArgb(150, 0, 0, 0), Color.FromArgb(150, 0, 0, 0), Color.FromArgb(0, 0, 0, 0) },
                            Positions = new[] { 0f, 0.34f, 0.66f, 1f }
                        };
                        g.FillRectangle(vb, band);
                    }
                    // halo radial más denso centrado en el título (efecto de difuminado)
                    int rw = Math.Min(rect.Width, nameSize.Width + 240), rh = nameSize.Height + 80;
                    var halo = new Rectangle(cx - rw / 2, top + nameSize.Height / 2 - rh / 2, rw, rh);
                    using (var gp = new GraphicsPath())
                    {
                        gp.AddEllipse(halo);
                        using (var pgb = new PathGradientBrush(gp) { CenterColor = Color.FromArgb(175, 0, 0, 0), SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) } })
                            g.FillPath(pgb, gp);
                    }
                    var nameRect = new Rectangle(cx - maxW / 2, top, maxW, nameSize.Height + 4);
                    TextRenderer.DrawText(g, RouteName, nf, new Rectangle(nameRect.X + 1, nameRect.Y + 2, nameRect.Width, nameRect.Height), Color.FromArgb(200, 0, 0, 0), flags);
                    TextRenderer.DrawText(g, RouteName, nf, nameRect, Color.White, flags);
                    nf.Dispose();
                }
            }
            Theme.DrawRoundBorder(g, rect, Radius, Theme.Border);
        }
    }

    /// <summary>Vista previa del tren: imagen (contain) o silueta dibujada + nombre. Arrastrable para girar.</summary>
    public class TrainPreviewPanel : Panel
    {
        Bitmap _image;
        public string Caption = "";
        public int Radius = 12;
        public bool Rotatable = false;
        // Texto cuando no hay render: «Generando vista 3D…» mientras se dibuja, o el aviso que ponga
        // cada sección cuando sencillamente no hay nada elegido.
        public string EmptyText;

        public event Action<int, int> Dragged;   // (dx, dy) en píxeles
        public event Action ResetRequested;
        public event Action Zoomed;              // la rueda ha cambiado el zoom: hay que volver a dibujar

        // Zoom de la cámara con la rueda del ratón: >1 acerca, <1 aleja. Doble clic lo devuelve a 1.
        public float Zoom { get; private set; } = 1f;
        const float ZoomMin = 0.5f, ZoomMax = 3f;
        /// <summary>Distancia de la cámara (en radios del modelo) para <see cref="ShapeRenderer.Render"/> con el zoom aplicado.</summary>
        public float CamDistance(float baseDistance = 2.25f) => baseDistance / Zoom;

        bool _dragging; System.Drawing.Point _last;

        public TrainPreviewPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            MouseDown += (s, e) => { if (Rotatable && e.Button == MouseButtons.Left) { _dragging = true; _last = e.Location; Cursor = Cursors.SizeAll; } };
            MouseUp += (s, e) => { _dragging = false; Cursor = Cursors.Default; };
            MouseMove += (s, e) =>
            {
                if (_dragging)
                {
                    Dragged?.Invoke(e.X - _last.X, e.Y - _last.Y);
                    _last = e.Location;
                }
            };
            DoubleClick += (s, e) => { if (Rotatable) { Zoom = 1f; ResetRequested?.Invoke(); } };
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Rotatable || _image == null || e.Delta == 0) return;
            float z = Math.Max(ZoomMin, Math.Min(ZoomMax, Zoom * (float)Math.Pow(1.15, e.Delta / 120.0)));
            if (e is HandledMouseEventArgs h) h.Handled = true;   // que no desplace la página de detrás
            if (Math.Abs(z - Zoom) < 0.001f) return;
            Zoom = z;
            Zoomed?.Invoke();
        }

        public Bitmap Image { get => _image; set { _image = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = ClientRectangle;
            // Sin marco ni tarjeta: fondo plano igual que el panel padre; el render flota encima.
            using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, rect);

            var imgArea = new Rectangle(rect.X, rect.Y, rect.Width, rect.Height - 24);
            if (_image != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                double s = Math.Min((double)imgArea.Width / _image.Width, (double)imgArea.Height / _image.Height);
                int w = (int)(_image.Width * s), h = (int)(_image.Height * s);
                g.DrawImage(_image, imgArea.X + (imgArea.Width - w) / 2, imgArea.Y + (imgArea.Height - h) / 2, w, h);
            }
            else
            {
                using (var f = Theme.Font(9.5f))
                    TextRenderer.DrawText(g, EmptyText ?? I18n.T("Generando vista 3D…"), f, imgArea, Theme.Subtle,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            var capRect = new Rectangle(rect.X + 8, rect.Bottom - 24, rect.Width - 16, 20);
            using (var f = Theme.Font(9.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, Caption ?? "", f, capRect, Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (Rotatable && _image != null)
                using (var f = Theme.Font(8f))
                    TextRenderer.DrawText(g, I18n.T("↺ arrastra para girar · rueda: zoom"), f, new Rectangle(rect.X + 8, rect.Y + 6, rect.Width - 16, 16), Color.FromArgb(150, Theme.Subtle),
                        TextFormatFlags.Left | TextFormatFlags.Top);
        }

        // Silueta estilizada de locomotora (cuando no hay imagen).
        static void DrawTrainSilhouette(Graphics g, Rectangle a)
        {
            int cx = a.X + a.Width / 2, cy = a.Y + a.Height / 2;
            int bw = Math.Min(a.Width - 40, 220);
            int bh = Math.Max(28, Math.Min(a.Height - 40, 64));
            var body = new Rectangle(cx - bw / 2, cy - bh / 2, bw, bh);

            using (var path = new GraphicsPath())
            {
                // cuerpo con morro inclinado (frente a la derecha)
                int nose = bh; // ancho del morro
                path.AddArc(body.X, body.Y, 14, 14, 180, 90);
                path.AddLine(body.X + 7, body.Y, body.Right - nose, body.Y);
                path.AddBezier(body.Right - nose, body.Y, body.Right, body.Y + bh * 0.15f,
                               body.Right, body.Y + bh * 0.5f, body.Right, body.Y + bh * 0.5f);
                path.AddBezier(body.Right, body.Y + bh * 0.5f, body.Right, body.Bottom - bh * 0.15f,
                               body.Right - nose, body.Bottom, body.Right - nose, body.Bottom);
                path.AddLine(body.Right - nose, body.Bottom, body.X + 7, body.Bottom);
                path.AddArc(body.X, body.Bottom - 14, 14, 14, 90, 90);
                path.CloseFigure();
                using (var b = new SolidBrush(Theme.Surface2)) g.FillPath(b, path);
                using (var pen = new Pen(Theme.Border, 1.5f)) g.DrawPath(pen, path);
            }
            // ventanas (franja de acento)
            int wy = body.Y + 8, wh = Math.Max(8, bh / 3);
            for (int i = 0; i < 3; i++)
            {
                var win = new Rectangle(body.X + 16 + i * 26, wy, 18, wh);
                if (win.Right > body.Right - bh) break;
                Theme.FillRound(g, win, 3, Theme.Accent);
            }
            // ruedas
            using (var b = new SolidBrush(Theme.Border))
            {
                g.FillEllipse(b, body.X + 20, body.Bottom - 6, 14, 14);
                g.FillEllipse(b, body.Right - 60, body.Bottom - 6, 14, 14);
            }
        }
    }
}
