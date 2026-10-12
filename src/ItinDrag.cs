// Editar el itinerario arrastrando, igual en el planificador de Conducción libre y en el mapa grande del HUD:
//  · coger el trazado y soltarlo en otra vía → un punto «vía» ahí (el recorrido pasa por esa vía exacta);
//  · coger un punto (numerado o «vía») y soltarlo en otra vía → se mueve allí;
//  · clic derecho en un punto → se quita;
//  · mientras se arrastra se ve dónde queda (pegado a la vía) y el recorrido nuevo, en discontinuo.
// Lo ya recorrido no se toca. Cada cambio se puede deshacer (RoadBook.Undo, Ctrl+Z).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class ItinDrag
    {
        const float PointHitPx = 11, PathHitPx = 8;
        int _move = -1, _insert = -1;
        List<(double lat, double lon)[]> _preview;
        (double lat, double lon)? _snap;
        DateTime _previewAt = DateTime.MinValue;
        public bool Active => _move >= 0 || _insert >= 0;
        public bool MovingPoint => _move >= 0;

        // ¿Qué hay bajo el ratón? índice del punto (o -1) y, si no, el punto al que llega el trozo del trazado (o -1).
        public static (int point, int leg) Hit(RoadBook rb, Func<double, double, PointF> proj, Func<Point, (double lat, double lon)> inv, double mPerPx, Point p)
        {
            if (rb == null || rb.Graph == null) return (-1, -1);
            int best = -1; double bd = PointHitPx * PointHitPx;
            for (int i = rb.ReachedPoints; i < rb.Points.Count; i++)
            {
                var w = rb.Points[i]; if (w.Guide) continue;
                var q = proj(w.Lat, w.Lon); double dx = q.X - p.X, dy = q.Y - p.Y, d2 = dx * dx + dy * dy;
                if (d2 <= bd) { bd = d2; best = i; }
            }
            if (best >= 0) return (best, -1);
            var (la, lo) = inv(p);
            return (-1, rb.LegAt(la, lo, Math.Max(3, PathHitPx * mPerPx)));
        }

        // Pulsar: true si coge algo (un punto o el trazado). Clic derecho en un punto: lo quita (changed = true).
        public bool Down(RoadBook rb, Func<double, double, PointF> proj, Func<Point, (double lat, double lon)> inv, double mPerPx, Point p, MouseButtons b, out bool changed)
        {
            changed = false; _move = _insert = -1; _preview = null; _snap = null;
            var (pt, leg) = Hit(rb, proj, inv, mPerPx, p);
            if (b == MouseButtons.Right)
            {
                if (pt >= 0) changed = rb.RemovePoint(pt);
                return pt >= 0;
            }
            if (b != MouseButtons.Left) return false;
            if (pt >= 0) { _move = pt; return true; }
            if (leg >= 0) { _insert = leg; return true; }
            return false;
        }

        // Arrastrando: el recorrido nuevo (como mucho unas 8 veces por segundo; cada vez son dos caminos por las vías).
        public void Move(RoadBook rb, Func<Point, (double lat, double lon)> inv, double mPerPx, Point p)
        {
            if (!Active || rb?.Graph == null) return;
            if ((DateTime.UtcNow - _previewAt).TotalMilliseconds < 120) return;
            _previewAt = DateTime.UtcNow;
            var (la, lo) = inv(p);
            try { _preview = rb.PreviewEdit(_move, _insert, la, lo, MaxM(mPerPx), out _snap); } catch { _preview = null; _snap = null; }
        }

        // Soltar: se aplica (true = ha cambiado el itinerario).
        public bool Up(RoadBook rb, Func<Point, (double lat, double lon)> inv, double mPerPx, Point p, bool moved)
        {
            bool changed = false;
            if (moved && rb?.Graph != null)
            {
                var (la, lo) = inv(p);
                if (_move >= 0) changed = rb.MovePoint(_move, la, lo, MaxM(mPerPx));
                else if (_insert >= 0) changed = rb.InsertVia(_insert, la, lo, MaxM(mPerPx));
            }
            Cancel();
            return changed;
        }

        public void Cancel() { _move = _insert = -1; _preview = null; _snap = null; }

        static double MaxM(double mPerPx) => Math.Max(25, 18 * mPerPx);

        // Lo que se ve mientras se arrastra: el recorrido nuevo en discontinuo y dónde queda el punto.
        public void Draw(Graphics g, Func<double, double, PointF> proj)
        {
            if (!Active) return;
            var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_preview != null)
                foreach (var leg in _preview)
                {
                    if (leg.Length < 2) continue;
                    var pts = new PointF[leg.Length];
                    for (int i = 0; i < pts.Length; i++) pts[i] = proj(leg[i].lat, leg[i].lon);
                    using (var under = new Pen(Color.FromArgb(170, 20, 22, 24), 7f) { LineJoin = LineJoin.Round }) try { g.DrawLines(under, pts); } catch { }
                    using (var pen = new Pen(Color.FromArgb(255, 226, 150), 3.4f) { LineJoin = LineJoin.Round, DashPattern = new[] { 2.2f, 1.4f } }) try { g.DrawLines(pen, pts); } catch { }
                }
            if (_snap is (double sl, double so))
            {
                var q = proj(sl, so);
                if (_insert >= 0) DrawVia(g, q, 7.5f, Color.FromArgb(255, 226, 150));
                else
                    using (var b = new SolidBrush(Color.FromArgb(255, 226, 150))) using (var pen = new Pen(Color.FromArgb(30, 34, 38), 2f))
                    { g.FillEllipse(b, q.X - 9, q.Y - 9, 18, 18); g.DrawEllipse(pen, q.X - 9, q.Y - 9, 18, 18); }
            }
            g.SmoothingMode = sm;
        }

        // Punto «vía»: un rombo pequeño, sin número.
        public static void DrawVia(Graphics g, PointF p, float r, Color fill)
        {
            var pts = new[] { new PointF(p.X, p.Y - r), new PointF(p.X + r, p.Y), new PointF(p.X, p.Y + r), new PointF(p.X - r, p.Y) };
            using (var b = new SolidBrush(fill)) g.FillPolygon(b, pts);
            using (var pen = new Pen(Color.FromArgb(30, 34, 38), 1.8f)) g.DrawPolygon(pen, pts);
        }
    }
}
