// Tamaño del HUD (1.2.82, Ajustes → Gráficos): en pantallas grandes el HUD, la hoja de ruta y el chat se veían muy pequeños.
// Cada ventana del HUD sigue maquetada en sus píxeles de siempre («lógicos») y, al pintar, se amplía con una transformación
// (Graphics.ScaleTransform): líneas, iconos y textos crecen juntos y nítidos (no es una imagen estirada). Lo que hay que
// tener en cuenta en cada ventana:
//  · su tamaño de verdad = el lógico × F (Up); para pintar y para los botones, el lógico (Down / LW, LH);
//  · el ratón llega en píxeles de verdad: se pasa a lógicos (Down) antes de mirar qué se ha pulsado;
//  · TextRenderer no hace caso de la transformación: los textos se dibujan con Text(), que pone la letra a la escala y
//    el recuadro en píxeles de verdad. MeasureText se queda como está (mide en lógicos: la letra es la de siempre).
// Con F = 1 todo es exactamente como antes.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public static class HudScale
    {
        static float _f = 1f;
        public static float F => _f;
        public static readonly int[] Choices = { 100, 125, 150, 175, 200 };
        public static event Action Changed;

        public static void Set(int percent)
        {
            float f = Math.Max(1f, Math.Min(2f, percent / 100f));
            if (Math.Abs(f - _f) < 0.001f) return;
            _f = f;
            try { Changed?.Invoke(); } catch { }
        }

        public static int Up(int v) => _f == 1f ? v : (int)Math.Round(v * _f);
        public static Size Up(Size s) => _f == 1f ? s : new Size(Up(s.Width), Up(s.Height));
        public static Rectangle Up(Rectangle r) => _f == 1f ? r : Rectangle.FromLTRB((int)Math.Floor(r.Left * _f), (int)Math.Floor(r.Top * _f), (int)Math.Ceiling(r.Right * _f), (int)Math.Ceiling(r.Bottom * _f));
        public static int Down(int v) => _f == 1f ? v : (int)Math.Round(v / _f);
        public static Point Down(Point p) => _f == 1f ? p : new Point((int)Math.Floor(p.X / _f), (int)Math.Floor(p.Y / _f));
        public static MouseEventArgs Down(MouseEventArgs e) => _f == 1f ? e : new MouseEventArgs(e.Button, e.Clicks, (int)Math.Floor(e.X / _f), (int)Math.Floor(e.Y / _f), e.Delta);

        // al empezar a pintar una ventana del HUD
        public static void Begin(Graphics g) { if (_f != 1f) g.ScaleTransform(_f, _f); }

        // ── Textos (TextRenderer, a la escala de la transformación que tenga el Graphics)
        public static void Text(IDeviceContext dc, string text, Font font, Rectangle bounds, Color fore, TextFormatFlags flags)
        {
            if (!(dc is Graphics g) || !Scaled(g, out float sx, out _)) { TextRenderer.DrawText(dc, text, font, bounds, fore, flags); return; }
            var r = ToDevice(g, bounds);
            var st = g.Save();
            try
            {
                g.ResetTransform();
                using var f2 = new Font(font.FontFamily, font.Size * sx, font.Style, font.Unit);
                TextRenderer.DrawText(g, text, f2, r, fore, flags & ~TextFormatFlags.PreserveGraphicsTranslateTransform);
            }
            finally { g.Restore(st); }
        }
        public static void Text(IDeviceContext dc, string text, Font font, Rectangle bounds, Color fore) => Text(dc, text, font, bounds, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        public static void Text(IDeviceContext dc, string text, Font font, Point pt, Color fore, TextFormatFlags flags)
        {
            if (!(dc is Graphics g) || !Scaled(g, out float sx, out _)) { TextRenderer.DrawText(dc, text, font, pt, fore, flags); return; }
            var p = ToDevice(g, new Rectangle(pt, Size.Empty)).Location;
            var st = g.Save();
            try
            {
                g.ResetTransform();
                using var f2 = new Font(font.FontFamily, font.Size * sx, font.Style, font.Unit);
                TextRenderer.DrawText(g, text, f2, p, fore, flags & ~TextFormatFlags.PreserveGraphicsTranslateTransform);
            }
            finally { g.Restore(st); }
        }
        public static void Text(IDeviceContext dc, string text, Font font, Point pt, Color fore) => Text(dc, text, font, pt, fore, TextFormatFlags.Default);
        public static void Text(IDeviceContext dc, string text, Font font, Rectangle bounds, Color fore, Color back, TextFormatFlags flags)
        {
            if (!(dc is Graphics g) || !Scaled(g, out _, out _)) { TextRenderer.DrawText(dc, text, font, bounds, fore, back, flags); return; }
            using (var b = new SolidBrush(back)) g.FillRectangle(b, bounds);
            Text(dc, text, font, bounds, fore, flags);
        }

        static bool Scaled(Graphics g, out float sx, out float sy)
        {
            sx = sy = 1f;
            try
            {
                using var m = g.Transform;
                var el = m.Elements;
                sx = (float)Math.Sqrt(el[0] * el[0] + el[1] * el[1]); sy = (float)Math.Sqrt(el[2] * el[2] + el[3] * el[3]);
                return Math.Abs(sx - 1f) > 0.001f || Math.Abs(sy - 1f) > 0.001f;
            }
            catch { return false; }
        }
        static Rectangle ToDevice(Graphics g, Rectangle r)
        {
            using var m = g.Transform;
            var pts = new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Bottom) };
            m.TransformPoints(pts);
            return Rectangle.FromLTRB((int)Math.Round(pts[0].X), (int)Math.Round(pts[0].Y), (int)Math.Round(pts[1].X), (int)Math.Round(pts[1].Y));
        }
    }
}
