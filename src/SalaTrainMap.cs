// (2026-11-09) El mapa de la ruta en la ventana «Tu tren en la sala» (MainMenuForm.TrenSala.cs): las vías y las estaciones,
// la de salida en verde, el destino elegido en ámbar y Estación Sur marcada (la paridad del número va respecto a ella). Al
// pasar el ratón por una estación sale su nombre; un clic la elige como destino. Rueda: zoom; arrastrar: mover.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class SalaTrainMap : Control
    {
        readonly List<PointF[]> _lines = new();
        public List<(string name, double x, double y)> Stations = new();
        public string Origin, Dest, South;
        public HashSet<string> Freight = new();   // (2026-11-09) las instalaciones de mercancías (marrón, cuadradas)
        public HashSet<string> Sidings = new();   // (2026-11-10) los apartaderos (pequeños, rombos): solo paradas, no destino
        public HashSet<string> Stops = new();     // (2026-11-10) las paradas intermedias elegidas (azul claro, con su nombre)
        public event Action<string> Picked;
        public event Action<string> StopToggled;  // (2026-11-10) clic derecho: añadir o quitar una parada
        RectangleF _bounds; float _zoom = 1; PointF _pan; string _hover;
        Point _down; bool _drag; PointF _panDown;

        public SalaTrainMap(RouteGraph g, IEnumerable<(string name, double x, double y)> stations)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(18, 21, 26); Cursor = Cursors.Hand;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var e in g?.Edges ?? new RouteGraph.Edge[0])
            {
                if (e?.X == null || e.X.Length < 2) continue;
                var pts = new PointF[e.X.Length];
                for (int i = 0; i < pts.Length; i++) { pts[i] = new PointF(e.X[i], e.Y[i]); x0 = Math.Min(x0, e.X[i]); y0 = Math.Min(y0, e.Y[i]); x1 = Math.Max(x1, e.X[i]); y1 = Math.Max(y1, e.Y[i]); }
                _lines.Add(pts);
            }
            Stations = stations.ToList();
            _bounds = x1 > x0 ? RectangleF.FromLTRB(x0, y0, x1, y1) : new RectangleF(0, 0, 1, 1);
        }

        float Scale0 => Math.Min((Width - 40) / Math.Max(1f, _bounds.Width), (Height - 40) / Math.Max(1f, _bounds.Height));
        PointF P(double x, double y)
        {
            float k = Scale0 * _zoom;
            float cx = Width / 2f + _pan.X, cy = Height / 2f + _pan.Y;
            return new PointF(cx + (float)(x - (_bounds.X + _bounds.Width / 2)) * k, cy + (float)(y - (_bounds.Y + _bounds.Height / 2)) * k);
        }

        string StationAt(Point p)
        {
            string best = null; float bd = 12 * 12;
            foreach (var s in Stations)
            {
                var q = P(s.x, s.y); float d = (q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y);
                if (d < bd) { bd = d; best = s.name; }
            }
            return best;
        }

        protected override void OnMouseDown(MouseEventArgs e) { _down = e.Location; _drag = false; _panDown = _pan; base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (!_drag && Math.Abs(e.X - _down.X) + Math.Abs(e.Y - _down.Y) > 5) _drag = true;
                if (_drag) { _pan = new PointF(_panDown.X + e.X - _down.X, _panDown.Y + e.Y - _down.Y); Invalidate(); return; }
            }
            var h = StationAt(e.Location);
            if (h != _hover) { _hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { if (_hover != null) { _hover = null; Invalidate(); } base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !_drag) { var s = StationAt(e.Location); if (s != null && s != Origin && !Sidings.Contains(s)) Picked?.Invoke(s); }
            if (e.Button == MouseButtons.Right) { var s = StationAt(e.Location); if (s != null && s != Origin && s != Dest) StopToggled?.Invoke(s); }
            _drag = false; base.OnMouseUp(e);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            float k = e.Delta > 0 ? 1.25f : 1 / 1.25f, nz = Math.Max(1f, Math.Min(60f, _zoom * k));   // (hasta ×60) k = nz / _zoom;
            // (que lo que hay bajo el ratón se quede donde está)
            float cx = Width / 2f + _pan.X, cy = Height / 2f + _pan.Y;
            _pan = new PointF(_pan.X + (e.X - cx) * (1 - k), _pan.Y + (e.Y - cy) * (1 - k));
            if (nz <= 1.001f) _pan = PointF.Empty;
            _zoom = nz; Invalidate();
        }
        protected override void OnMouseEnter(EventArgs e) { Focus(); base.OnMouseEnter(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.FromArgb(120, 140, 150, 160), 1.6f) { LineJoin = LineJoin.Round })
                foreach (var l in _lines) { var pts = l.Select(q => P(q.X, q.Y)).ToArray(); if (pts.Length >= 2) g.DrawLines(pen, pts); }
            using var f = Theme.Font(8f);
            using var fb = Theme.Font(8.5f, FontStyle.Bold);
            var labels = new List<RectangleF>();
            void Label(string text, PointF q, Color c, Font font)
            {
                var sz = g.MeasureString(text, font);
                var r = new RectangleF(q.X + 7, q.Y - sz.Height / 2, sz.Width + 6, sz.Height);
                if (r.Right > Width - 2) r.X = q.X - 7 - r.Width;
                if (labels.Any(o => o.IntersectsWith(r))) r.Y += sz.Height;
                labels.Add(r);
                using (var bg = new SolidBrush(Color.FromArgb(215, 18, 21, 26))) g.FillRectangle(bg, r);
                using (var tb = new SolidBrush(c)) g.DrawString(text, font, tb, r.X + 3, r.Y);
            }
            foreach (var s in Stations)
            {
                var q = P(s.x, s.y);
                bool o = s.name == Origin, d = s.name == Dest, so = s.name == South, h = s.name == _hover;
                bool fr = Freight.Contains(s.name), sd = Sidings.Contains(s.name), sp = Stops.Contains(s.name);
                var c = o ? Color.FromArgb(102, 197, 106) : d ? Color.FromArgb(255, 184, 77) : sp ? Color.FromArgb(120, 200, 230) : so ? Color.FromArgb(120, 144, 226) : fr ? Color.FromArgb(196, 140, 92) : sd ? Color.FromArgb(140, 146, 154) : Color.FromArgb(205, 210, 216);
                float r = o || d ? 6 : sp ? 5 : so || h ? 5 : sd ? 2.6f : 3.2f;
                using (var b = new SolidBrush(c))
                {
                    if (sd && !sp && !h) g.FillPolygon(b, new[] { new PointF(q.X, q.Y - r - 1), new PointF(q.X + r + 1, q.Y), new PointF(q.X, q.Y + r + 1), new PointF(q.X - r - 1, q.Y) });
                    else if (fr && !o && !d) g.FillRectangle(b, q.X - r, q.Y - r, 2 * r, 2 * r); else g.FillEllipse(b, q.X - r, q.Y - r, 2 * r, 2 * r);
                }
                if (o || d) using (var p = new Pen(Color.White, 1.5f)) g.DrawEllipse(p, q.X - r, q.Y - r, 2 * r, 2 * r);
            }
            // nombres: salida, destino, Estación Sur y el de debajo del ratón
            foreach (var s in Stations)
            {
                var q = P(s.x, s.y);
                if (s.name == Origin) Label(s.name, q, Color.FromArgb(102, 197, 106), fb);
                else if (s.name == Dest) Label(s.name, q, Color.FromArgb(255, 184, 77), fb);
                else if (Stops.Contains(s.name)) Label(s.name, q, Color.FromArgb(120, 200, 230), f);
                else if (s.name == South) Label(s.name, q, Color.FromArgb(120, 144, 226), f);
                else if (s.name == _hover) Label(s.name, q, Color.White, f);
            }
            using (var hint = new SolidBrush(Color.FromArgb(150, 170, 176, 182)))
                g.DrawString(I18n.T("Clic en una estación: destino · clic derecho: parada · rueda: zoom · arrastrar: mover") + "   ·   ●  " + I18n.T("viajeros") + "   ■  " + I18n.T("mercancías") + "   ◆  " + I18n.T("apartadero"), f, hint, 6, Height - 18);
        }
    }
}
