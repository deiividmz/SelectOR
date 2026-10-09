// Visor 3D en grande: el modelo de una vista 3D (Conducción libre, Horarios, Editor, Flota, Compra) en su propia ventana,
// renderizado de nuevo a la resolución de la ventana (y con supersampling al soltar) para verlo al detalle.
//  · Arrastrar: girar · botón derecho (o central) arrastrando: mover la cámara · rueda: acercar hacia el cursor
//    (acercar = cerrar el ángulo de visión, como un teleobjetivo: la cámara nunca se mete dentro del modelo)
//  · Doble clic o «Restablecer»: la vista inicial · vistas rápidas: frente, lateral, detrás, arriba
//  · «Guardar imagen…»: PNG con fondo transparente, al doble del tamaño de la ventana.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    /// <summary>Lo que necesita el visor para dibujar el modelo de una vista 3D tal como está.</summary>
    public sealed class ShapeViewSource
    {
        public ShapeGeom Geom;
        public bool? Flip;
        public float BaseDistance = 2.25f;
        public float Yaw, Pitch;
        public string Caption = "";
        public Action BeforeRender;   // p. ej. el teleindicador elegido (redirige la textura del cartel)
    }

    public sealed class ShapeViewerWindow : Form
    {
        readonly ShapeViewSource _src;
        readonly Canvas _canvas;
        float _yaw, _pitch, _zoom = 1f, _panX, _panY;
        readonly float _yaw0, _pitch0;
        const float ZoomMin = 0.6f, ZoomMax = 24f;
        const float Fov0 = 30f;          // ángulo de visión con zoom 1 (el de las vistas pequeñas)
        const float CamDist = 2.1f;      // distancia de la cámara, en radios del modelo: el modelo llena la ventana
        readonly Timer _fine = new Timer { Interval = 220 };   // al dejar de mover: render con supersampling
        Point _last; MouseButtons _dragBtn = MouseButtons.None;

        public static void Open(ShapeViewSource src, IWin32Window owner)
        {
            if (src?.Geom == null || src.Geom.IsEmpty) return;
            var w = new ShapeViewerWindow(src);
            w.Show(owner);
        }

        ShapeViewerWindow(ShapeViewSource src)
        {
            _src = src; _yaw = _yaw0 = src.Yaw; _pitch = _pitch0 = src.Pitch;
            Text = I18n.T("Vista 3D") + (string.IsNullOrWhiteSpace(src.Caption) ? "" : " · " + src.Caption);
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterScreen; KeyPreview = true; ShowInTaskbar = true;
            try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? AppContext.BaseDirectory); } catch { }
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(Math.Max(900, wa.Width * 85 / 100), Math.Max(600, wa.Height * 85 / 100));
            MinimumSize = new Size(640, 420);

            // barra de arriba: nombre y botones
            var bar = new Panel { Dock = DockStyle.Top, Height = Theme.Px(48), BackColor = Theme.BgSidebar, Padding = new Padding(Theme.Px(14), Theme.Px(8), Theme.Px(10), Theme.Px(8)) };
            var cap = new Label { Text = src.Caption ?? "", Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Font(10.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            var btns = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Theme.BgSidebar, Margin = new Padding(0) };
            RoundButton B(string t, Action a)
            {
                var b = new RoundButton { Text = t, Height = Theme.Px(32), Radius = 9, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 9f, Margin = new Padding(Theme.Px(6), 0, 0, 0) };
                using (var f = Theme.Font(9f)) b.Width = TextRenderer.MeasureText(t, f).Width + Theme.Px(26);
                b.Click += (s, e) => a();
                btns.Controls.Add(b);
                return b;
            }
            // vistas rápidas (la cámara del render está a 0,62 rad del eje del modelo y 0,13 rad por encima)
            B(I18n.T("Frente"), () => SetView(-35.5f, -7.5f));
            B(I18n.T("Lateral"), () => SetView(54.5f, -7.5f));
            B(I18n.T("Detrás"), () => SetView(144.5f, -7.5f));
            B(I18n.T("Arriba"), () => SetView(54.5f, 74f));   // alineado como el lateral: el tren entero a lo ancho
            B(I18n.T("Restablecer"), Reset);
            B(I18n.T("Guardar imagen…"), SaveImage);
            bar.Controls.Add(cap); bar.Controls.Add(btns);

            _canvas = new Canvas { Dock = DockStyle.Fill, Hint = I18n.T("Arrastrar: girar · botón derecho: mover · rueda: acercar · doble clic: vista inicial · Esc: cerrar") };
            Controls.Add(_canvas); Controls.Add(bar);

            _canvas.MouseDown += (s, e) => { _dragBtn = e.Button; _last = e.Location; _canvas.Cursor = e.Button == MouseButtons.Left ? Cursors.SizeAll : Cursors.Hand; };
            _canvas.MouseUp += (s, e) => { _dragBtn = MouseButtons.None; _canvas.Cursor = Cursors.Default; Fine(); };
            _canvas.MouseMove += (s, e) =>
            {
                if (_dragBtn == MouseButtons.None) return;
                int dx = e.X - _last.X, dy = e.Y - _last.Y; _last = e.Location;
                if (_dragBtn == MouseButtons.Left)
                {
                    _yaw += dx * 0.4f;
                    _pitch = Math.Max(-80f, Math.Min(80f, _pitch - dy * 0.4f));
                }
                else
                {
                    // mover: lo de debajo del ratón sigue al ratón
                    float k = UnitsPerPixel();
                    _panX -= dx * k; _panY += dy * k;
                }
                Draw(false);
            };
            _canvas.MouseWheel += (s, e) =>
            {
                if (e.Delta == 0) return;
                float z = Math.Max(ZoomMin, Math.Min(ZoomMax, _zoom * (float)Math.Pow(1.18, e.Delta / 120.0)));
                if (Math.Abs(z - _zoom) < 0.0001f) return;
                // acerca hacia el cursor: el punto que hay debajo se queda donde está
                float k1 = UnitsPerPixel(); _zoom = z; float k2 = UnitsPerPixel();
                float ox = e.X - _canvas.Width / 2f, oy = e.Y - _canvas.Height / 2f;
                _panX += ox * (k1 - k2); _panY -= oy * (k1 - k2);
                Draw(false);
            };
            _canvas.DoubleClick += (s, e) => Reset();
            _canvas.Resize += (s, e) => { if (IsHandleCreated) Draw(false); };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); else if (e.KeyCode == Keys.R) Reset(); };
            _fine.Tick += (s, e) => { _fine.Stop(); Draw(true); };
            Shown += (s, e) => Draw(true);
            FormClosed += (s, e) => { _fine.Dispose(); _canvas.Image?.Dispose(); };
        }

        float Distance() => Math.Min(_src.BaseDistance, CamDist);
        float Fov() => Fov0 / _zoom;
        // radios del modelo por píxel de pantalla (en el plano del centro de la vista)
        float UnitsPerPixel() => 2f * (float)Math.Tan(Fov() * Math.PI / 360.0) * Distance() / Math.Max(1, _canvas.Height);

        void SetView(float yaw, float pitch) { _yaw = yaw; _pitch = pitch; Draw(true); }
        void Reset() { _yaw = _yaw0; _pitch = _pitch0; _zoom = 1f; _panX = _panY = 0; Draw(true); }
        void Fine() { _fine.Stop(); _fine.Start(); }

        // fine = con supersampling (más nítido; al soltar el ratón o tras un cambio puntual).
        void Draw(bool fine)
        {
            int w = _canvas.Width, h = _canvas.Height;
            if (w < 40 || h < 40) return;
            try { _src.BeforeRender?.Invoke(); } catch { }
            var bmp = ShapeRenderer.Render(_src.Geom, w, h, _yaw, _pitch, fine ? 2 : 1, _src.Flip, Distance(), _panX, _panY, Fov());
            if (bmp == null) return;
            var old = _canvas.Image; _canvas.Image = bmp; old?.Dispose();
            if (!fine) Fine();
        }

        void SaveImage()
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "PNG (*.png)|*.png", DefaultExt = "png", AddExtension = true,
                FileName = MakeFileName(string.IsNullOrWhiteSpace(_src.Caption) ? "vista3d" : _src.Caption) + ".png"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { _src.BeforeRender?.Invoke(); } catch { }
            int w = Math.Min(7680, _canvas.Width * 2), h = Math.Min(4320, _canvas.Height * 2);
            using var bmp = ShapeRenderer.Render(_src.Geom, w, h, _yaw, _pitch, 2, _src.Flip, Distance(), _panX, _panY, Fov());
            if (bmp == null) { ThemedBox.Show(this, I18n.T("No se ha podido generar la imagen."), "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            try { bmp.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png); }
            catch (Exception ex) { ThemedBox.Show(this, I18n.T("No se ha podido guardar la imagen: ") + ex.Message, "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        static string MakeFileName(string s)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim().Length == 0 ? "vista3d" : s.Trim();
        }

        // Lienzo: la imagen tal cual (ya viene al tamaño exacto) y una línea de ayuda abajo.
        sealed class Canvas : Control
        {
            public Bitmap Image { get => _img; set { _img = value; Invalidate(); } }
            Bitmap _img;
            public string Hint = "";
            readonly Font _f = Theme.Font(8.5f);
            public Canvas() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true); TabStop = true; }
            protected override void Dispose(bool disposing) { if (disposing) _f.Dispose(); base.Dispose(disposing); }
            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Focus(); }   // para que la rueda llegue aquí
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                using (var br = new LinearGradientBrush(ClientRectangle, Color.FromArgb(46, 50, 54), Color.FromArgb(26, 28, 31), LinearGradientMode.Vertical))
                    g.FillRectangle(br, ClientRectangle);
                if (_img != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_img, (Width - _img.Width) / 2, (Height - _img.Height) / 2, _img.Width, _img.Height);
                }
                TextRenderer.DrawText(g, Hint, _f, new Rectangle(12, Height - 26, Width - 24, 20), Color.FromArgb(170, Theme.Subtle), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
