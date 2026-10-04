// Transición entre pestañas: al abrir una sección se congela su imagen y se funde desde el fondo,
// para que el contenido no aparezca de golpe.
//
// La imagen se toma con PrintWindow sobre la ventana (no con DrawToBitmap): las tablas son ListView
// y esas NO se dibujan bien con WM_PRINT — salían en negro y la sección se quedaba a oscuras hasta
// terminar de cargar. Además el avance va por reloj, no por ticks: si el hilo de interfaz se queda
// ocupado cargando, al volver a pintar la capa ya se ha consumido y se quita sola.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class PageFade : Control
    {
        const int DurationMs = 110, StepMs = 15;   // corto: el contenido llega antes (1.2.48)

        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        const uint PW_RENDERFULLCONTENT = 2;
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr rgn, uint flags);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        const uint RDW_UPDATENOW = 0x0100, RDW_ALLCHILDREN = 0x0080;
        const int SRCCOPY = 0x00CC0020;
        // Fundido con AlphaBlend de GDI: unas diez veces más rápido que DrawImage con matriz de color de GDI+
        // (que tardaba 15–25 ms por fotograma con la página entera y congelaba el cambio de pestaña).
        [StructLayout(LayoutKind.Sequential)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("msimg32.dll")] static extern bool AlphaBlend(IntPtr dst, int xd, int yd, int wd, int hd, IntPtr src, int xs, int ys, int ws, int hs, BLENDFUNCTION f);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        IntPtr _memDc, _hbmp, _oldBmp;

        void MakeGdiCopy()
        {
            FreeGdiCopy();
            if (_shot == null) return;
            try
            {
                _hbmp = _shot.GetHbitmap();
                _memDc = CreateCompatibleDC(IntPtr.Zero);
                _oldBmp = SelectObject(_memDc, _hbmp);
            }
            catch { FreeGdiCopy(); }
        }

        void FreeGdiCopy()
        {
            if (_memDc != IntPtr.Zero) { if (_oldBmp != IntPtr.Zero) SelectObject(_memDc, _oldBmp); DeleteDC(_memDc); }
            if (_hbmp != IntPtr.Zero) DeleteObject(_hbmp);
            _memDc = _hbmp = _oldBmp = IntPtr.Zero;
        }

        Bitmap _shot;
        Rectangle _src;                 // trozo de la ventana que ocupa la sección
        readonly Timer _timer;
        readonly Stopwatch _clock = new();

        public PageFade()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Visible = false;
            TabStop = false;
            _timer = new Timer { Interval = StepMs };
            _timer.Tick += (s, e) => { if (_clock.ElapsedMilliseconds >= DurationMs) Stop(); else Invalidate(); };
        }

        // Arranca la transición sobre la sección que se acaba de mostrar (ya pintada).
        public void Play(Control page)
        {
            try
            {
                Stop();
                if (page == null || !page.IsHandleCreated || page.Width < 8 || page.Height < 8) return;
                var form = page.FindForm();
                if (form == null || !form.Visible) return;   // ventana aún oculta: no hay nada que fundir
                if (!Capture(page)) return;   // sin imagen válida, mejor sin transición que a oscuras
                MakeGdiCopy();
                Bounds = new Rectangle(0, 0, page.Width, page.Height);
                _clock.Restart();
                Visible = true;
                BringToFront();
                Invalidate();
                _timer.Start();
            }
            catch { Stop(); }
        }

        // Rápida: se pinta YA lo pendiente de la sección y de sus controles (también las tablas) y se copian
        // sus píxeles, que acaban de dibujarse en la superficie de la ventana. Unos pocos ms frente a los
        // ~50 ms de volver a renderizar la ventana entera con PrintWindow.
        bool Capture(Control page)
        {
            try { if (CaptureFast(page)) return true; } catch { }
            return CaptureFull(page);
        }

        bool CaptureFast(Control page)
        {
            var form = page.FindForm();
            if (form == null || !form.IsHandleCreated || !form.Visible || form.WindowState == FormWindowState.Minimized) return false;
            int w = page.ClientSize.Width, h = page.ClientSize.Height;
            if (w < 8 || h < 8) return false;
            RedrawWindow(page.Handle, IntPtr.Zero, IntPtr.Zero, RDW_UPDATENOW | RDW_ALLCHILDREN);
            var origin = form.PointToClient(page.PointToScreen(Point.Empty));
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            bool ok;
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr dst = g.GetHdc();
                IntPtr src = GetDC(form.Handle);
                try { ok = src != IntPtr.Zero && BitBlt(dst, 0, 0, w, h, src, origin.X, origin.Y, SRCCOPY); }
                finally { if (src != IntPtr.Zero) ReleaseDC(form.Handle, src); g.ReleaseHdc(dst); }
            }
            if (!ok || LooksBlank(bmp)) { bmp.Dispose(); return false; }
            _shot = bmp;
            _src = new Rectangle(0, 0, w, h);
            return true;
        }

        // ¿Imagen vacía (todo negro)? Se miran unos pocos puntos repartidos.
        static bool LooksBlank(Bitmap b)
        {
            int nonBlack = 0;
            for (int yy = 1; yy <= 5; yy++)
                for (int xx = 1; xx <= 5; xx++)
                {
                    var c = b.GetPixel(b.Width * xx / 6, b.Height * yy / 6);
                    if (c.R + c.G + c.B > 12) nonBlack++;
                }
            return nonBlack < 3;
        }

        bool CaptureFull(Control page)
        {
            var form = page.FindForm();
            if (form == null || !form.IsHandleCreated) return false;
            if (!GetWindowRect(form.Handle, out var wr)) return false;
            int w = wr.R - wr.L, h = wr.B - wr.T;
            if (w < 16 || h < 16 || form.Width < 1) return false;

            var bmp = new Bitmap(w, h);
            bool ok;
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                ok = PrintWindow(form.Handle, hdc, PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
            }
            if (!ok) { bmp.Dispose(); return false; }

            // La ventana puede estar a otra escala (DPI) que las coordenadas de los controles.
            float s = w / (float)form.Width;
            var origin = page.PointToScreen(Point.Empty);
            var src = new Rectangle(origin.X - wr.L, origin.Y - wr.T,
                                    (int)Math.Round(page.Width * s), (int)Math.Round(page.Height * s));
            src.Intersect(new Rectangle(0, 0, w, h));
            if (src.Width < 8 || src.Height < 8) { bmp.Dispose(); return false; }

            _shot = bmp;
            _src = src;
            return true;
        }

        void Stop()
        {
            _timer.Stop();
            _clock.Reset();
            // Siempre se asigna: con la ventana oculta (pestañas preparadas en la pantalla de inicio) Visible
            // devuelve false aunque la capa esté puesta, y se quedaba tapando la sección al abrir el menú.
            Visible = false;
            FreeGdiCopy();
            var old = _shot; _shot = null;
            old?.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);
            if (_shot == null) return;

            float t = Math.Min(1f, _clock.ElapsedMilliseconds / (float)DurationMs);
            float a = 1f - (1f - t) * (1f - t);   // entra rápido y se posa suave
            byte alpha = (byte)Math.Round(255 * Math.Max(0f, Math.Min(1f, a)));
            bool done = false;
            if (_memDc != IntPtr.Zero)
            {
                IntPtr hdc = g.GetHdc();
                try { done = AlphaBlend(hdc, 0, 0, Width, Height, _memDc, _src.X, _src.Y, _src.Width, _src.Height, new BLENDFUNCTION { SourceConstantAlpha = alpha }); }
                finally { g.ReleaseHdc(hdc); }
            }
            if (!done)
            {
                var cm = new ColorMatrix { Matrix33 = alpha / 255f };
                using var ia = new ImageAttributes();
                ia.SetColorMatrix(cm);
                g.DrawImage(_shot, new Rectangle(0, 0, Width, Height), _src.X, _src.Y, _src.Width, _src.Height, GraphicsUnit.Pixel, ia);
            }
            if (t >= 1f) BeginInvoke((Action)Stop);   // se quita sola aunque el temporizador se retrase
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1;
            if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }   // los clics pasan de largo
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer?.Dispose(); _shot?.Dispose(); }
            FreeGdiCopy();
            base.Dispose(disposing);
        }
    }
}
