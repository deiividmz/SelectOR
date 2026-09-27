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
        const int DurationMs = 170, StepMs = 15;

        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        const uint PW_RENDERFULLCONTENT = 2;

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
                if (!Capture(page)) return;   // sin imagen válida, mejor sin transición que a oscuras
                Bounds = new Rectangle(0, 0, page.Width, page.Height);
                _clock.Restart();
                Visible = true;
                BringToFront();
                Invalidate();
                _timer.Start();
            }
            catch { Stop(); }
        }

        bool Capture(Control page)
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
            if (Visible) Visible = false;
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
            var cm = new ColorMatrix { Matrix33 = Math.Max(0f, Math.Min(1f, a)) };
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                g.DrawImage(_shot, new Rectangle(0, 0, Width, Height),
                            _src.X, _src.Y, _src.Width, _src.Height, GraphicsUnit.Pixel, ia);
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
            base.Dispose(disposing);
        }
    }
}
