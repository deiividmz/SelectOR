// (2026-11-09) La señal de las fichas de servicio de Empresas (ServiceCards), como la imagen de referencia del usuario:
// una caja negra redondeada con tres focos, un mástil corto y, debajo, un foco redondo aparte.
//  · Arriba verde (beneficio) · en medio rojo (anulado) · abajo amarillo (en conducción) · el de aparte blanco (en negativo).
//  · Todo con un contorno blanco fino (se ve sobre el fondo oscuro) y cada foco con su aro blanco.
//  · Encendido: su color con un brillo alrededor y un reflejo; apagado: gris muy oscuro.
// Dibujado en coma flotante (nítido a cualquier escala de la interfaz).

using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace SelectOR
{
    public static class SignalDraw
    {
        static readonly Color Body = Color.FromArgb(12, 13, 16), Off = Color.FromArgb(44, 48, 54), Ring = Color.FromArgb(236, 239, 243);
        public static readonly Color Green = Color.FromArgb(70, 214, 96), Red = Color.FromArgb(240, 52, 52), Yellow = Color.FromArgb(255, 200, 40), White = Color.FromArgb(255, 255, 255);

        // Medidas a 100 % (se multiplican por la escala de la interfaz)
        const float W = 18f, Lamp = 11f, Gap = 3f, Pad = 3.5f, StemW = 4f, StemH = 5f, Ext = 17f, Outline = 1.5f, RingW = 1.4f;
        public static float Width => W * Theme.Px(100) / 100f;

        // x: borde izquierdo; cy: el centro vertical de toda la señal (caja + mástil + foco de abajo)
        public static void Service(Graphics g, int x, int cy, bool green, bool red, bool yellow, bool white)
        {
            float k = Theme.Px(100) / 100f;
            float w = W * k, ld = Lamp * k, gap = Gap * k, pad = Pad * k, stw = StemW * k, sth = StemH * k, ext = Ext * k, o = Outline * k, rw = Math.Max(1f, RingW * k);
            float boxH = pad * 2 + ld * 3 + gap * 2, total = boxH + sth + ext;
            float x0 = x, y0 = cy - total / 2f;
            var box = new RectangleF(x0, y0, w, boxH);
            var stem = new RectangleF(x0 + (w - stw) / 2f, y0 + boxH - 1, stw, sth + 2);
            var er = new RectangleF(x0 + (w - ext) / 2f, y0 + boxH + sth, ext, ext);

            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // contorno blanco: la silueta un poco más grande, detrás
            using (var wb = new SolidBrush(Ring))
            {
                using (var p = Capsule(RectangleF.Inflate(box, o, o))) g.FillPath(wb, p);
                g.FillRectangle(wb, RectangleF.Inflate(stem, o, 0));
                g.FillEllipse(wb, RectangleF.Inflate(er, o, o));
            }
            using (var bb = new SolidBrush(Body))
            {
                using (var p = Capsule(box)) g.FillPath(bb, p);
                g.FillRectangle(bb, stem);
                g.FillEllipse(bb, er);
            }
            float lx = x0 + (w - ld) / 2f;
            DrawLamp(g, new RectangleF(lx, y0 + pad, ld, ld), green, Green, rw);
            DrawLamp(g, new RectangleF(lx, y0 + pad + ld + gap, ld, ld), red, Red, rw);
            DrawLamp(g, new RectangleF(lx, y0 + pad + 2 * (ld + gap), ld, ld), yellow, Yellow, rw);
            float inset = ext * 0.2f;
            DrawLamp(g, RectangleF.Inflate(er, -inset, -inset), white, White, rw);
            g.Restore(st);
        }

        static GraphicsPath Capsule(RectangleF r)
        {
            var p = new GraphicsPath(); float d = Math.Min(r.Width, r.Height);
            p.AddArc(r.X, r.Y, d, d, 180, 180);
            p.AddArc(r.X, r.Bottom - d, d, d, 0, 180);
            p.CloseFigure();
            return p;
        }

        static void DrawLamp(Graphics g, RectangleF r, bool on, Color c, float rw)
        {
            if (on)
            {
                // brillo alrededor (un halo suave del color del foco)
                var halo = RectangleF.Inflate(r, r.Width * 0.55f, r.Height * 0.55f);
                using (var hp = new GraphicsPath())
                {
                    hp.AddEllipse(halo);
                    using var pg = new PathGradientBrush(hp) { CenterColor = Color.FromArgb(150, c), SurroundColors = new[] { Color.FromArgb(0, c) } };
                    g.FillEllipse(pg, halo);
                }
                // el foco: más claro en el centro
                using (var lp = new GraphicsPath())
                {
                    lp.AddEllipse(r);
                    using var pg = new PathGradientBrush(lp) { CenterColor = Blend(c, Color.White, 0.45f), SurroundColors = new[] { c }, CenterPoint = new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.40f) };
                    g.FillEllipse(pg, r);
                }
            }
            else
                using (var b = new SolidBrush(Off)) g.FillEllipse(b, r);
            using (var p = new Pen(Ring, rw)) g.DrawEllipse(p, r);
            // reflejo pequeño arriba a la izquierda
            using (var hb = new SolidBrush(Color.FromArgb(on ? 150 : 55, 255, 255, 255)))
                g.FillEllipse(hb, r.X + r.Width * 0.24f, r.Y + r.Height * 0.20f, r.Width * 0.24f, r.Height * 0.20f);
        }

        static Color Blend(Color a, Color b, float t) => Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
}
