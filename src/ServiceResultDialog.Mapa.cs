// Mapa del informe del servicio: el detalle de la ruta (vía, andenes, apartaderos… como en el HUD), encima el
// recorrido REAL del tren en ámbar, la salida y la llegada con su hora, las paradas comerciales con la suya y los
// puntos donde se anotó cada infracción del carné. Un clic lo abre en grande (con rueda para acercar y arrastre).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class ServiceResultDialog
    {
        public sealed class TripMap
        {
            public readonly List<(double lat, double lon)> Trail = new();
            public HudMapDetail Detail;
            public readonly List<(double lat, double lon, string name, string time)> Stops = new();
            public readonly List<(double lat, double lon, string code)> Infractions = new();
            public string StartTime = "", EndTime = "";
        }

        static readonly Color MapBg = Color.FromArgb(30, 34, 38);
        static readonly Color StartCol = Color.FromArgb(102, 187, 106), EndCol = Color.FromArgb(239, 83, 80);

        // Dibuja el mapa en «area». zoom/centro: 1 y NaN = encuadre de todo el recorrido.
        internal static void PaintTripMap(Graphics g, Rectangle area, TripMap m, bool showInfr, float k,
                                          double zoom = 1, double cLat = double.NaN, double cLon = double.NaN)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(MapBg)) g.FillRectangle(b, area);
            if (m == null || m.Trail.Count < 2) return;

            // encuadre: el recorrido y las paradas, con margen
            double minLa = double.MaxValue, maxLa = double.MinValue, minLo = double.MaxValue, maxLo = double.MinValue;
            void Ext(double la, double lo) { if (la < minLa) minLa = la; if (la > maxLa) maxLa = la; if (lo < minLo) minLo = lo; if (lo > maxLo) maxLo = lo; }
            foreach (var p in m.Trail) Ext(p.lat, p.lon);
            foreach (var s in m.Stops) Ext(s.lat, s.lon);
            double lat0 = (minLa + maxLa) / 2, kx = Math.Max(0.01, Math.Cos(lat0 * Math.PI / 180));
            double mLat = 111320.0, mLon = 111320.0 * kx;
            double spanX = Math.Max((maxLo - minLo) * mLon, 300), spanY = Math.Max((maxLa - minLa) * mLat, 300);   // ≥ 300 m
            int pad = (int)(34 * k);
            double fit = Math.Min((area.Width - 2 * pad) / spanX, (area.Height - 2 * pad) / spanY);
            double pxPerM = fit * Math.Max(1, zoom);
            if (double.IsNaN(cLat)) { cLat = lat0; cLon = (minLo + maxLo) / 2; }
            double cx = area.X + area.Width / 2.0, cy = area.Y + area.Height / 2.0;
            PointF Proj(double la, double lo) => new PointF((float)(cx + (lo - cLon) * mLon * pxPerM), (float)(cy - (la - cLat) * mLat * pxPerM));

            var oldClip = g.Clip; g.SetClip(area);
            if (m.Detail != null) MapDetailDraw.Under(g, area, Proj, m.Detail, pxPerM, k);

            // recorrido real: halo oscuro y trazo ámbar (el color del itinerario)
            var pts = new PointF[m.Trail.Count];
            for (int i = 0; i < pts.Length; i++) pts[i] = Proj(m.Trail[i].lat, m.Trail[i].lon);
            using (var halo = new Pen(Color.FromArgb(110, 0, 0, 0), 7f * k) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                try { g.DrawLines(halo, pts); } catch { }
            using (var pen = new Pen(Color.FromArgb(240, HudMapRender.ItineraryCol), 3.4f * k) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                try { g.DrawLines(pen, pts); } catch { }

            var drawn = new List<RectangleF>();
            using var fLab = Theme.Font(8f * Math.Max(1f, k * 0.92f), FontStyle.Bold);
            var halts = new List<PointF>();
            foreach (var s in m.Stops) halts.Add(Proj(s.lat, s.lon));
            var start = pts[0]; var end = pts[^1];

            // primero se reservan los rótulos que más importan (salida y llegada), luego paradas e infracciones
            void Dot(PointF p, float r, Color fill, Color ring, float w)
            {
                using (var b = new SolidBrush(fill)) g.FillEllipse(b, p.X - r, p.Y - r, 2 * r, 2 * r);
                using (var pen = new Pen(ring, w)) g.DrawEllipse(pen, p.X - r, p.Y - r, 2 * r, 2 * r);
            }
            // paradas
            float rs = 4.6f * k;
            foreach (var p in halts) Dot(p, rs, Color.FromArgb(255, 245, 225), HudMapRender.ItineraryCol, 2.2f * k);
            // infracciones: triángulo rojo con «!»
            var infrPts = new List<(PointF p, string code)>();
            if (showInfr)
                foreach (var it in m.Infractions)
                {
                    var p = Proj(it.lat, it.lon); infrPts.Add((p, it.code));
                    float r = 7.5f * k;
                    var tri = new[] { new PointF(p.X, p.Y - r), new PointF(p.X + r * 0.95f, p.Y + r * 0.7f), new PointF(p.X - r * 0.95f, p.Y + r * 0.7f) };
                    using (var b = new SolidBrush(Carne.Red)) g.FillPolygon(b, tri);
                    using (var pen = new Pen(Color.FromArgb(255, 235, 235), 1.2f * k)) g.DrawPolygon(pen, tri);
                    using var fx = Theme.Font(6.5f * k, FontStyle.Bold);
                    TextRenderer.DrawText(g, "!", fx, new Rectangle((int)(p.X - r), (int)(p.Y - r * 0.55f), (int)(2 * r), (int)(r * 1.3f)), Color.White,
                        TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            // salida y llegada
            Dot(start, 6.5f * k, StartCol, Color.White, 2f * k);
            Dot(end, 6.5f * k, EndCol, Color.White, 2f * k);

            // rótulos
            string Lt(string a, string t) => string.IsNullOrEmpty(t) ? a : a + " · " + t;
            MapDetailDraw.Label(g, fLab, start, Lt(I18n.T("Salida"), m.StartTime), Theme.Text, StartCol, drawn);
            MapDetailDraw.Label(g, fLab, end, Lt(I18n.T("Llegada"), m.EndTime), Theme.Text, EndCol, drawn);
            for (int i = 0; i < m.Stops.Count; i++)
                MapDetailDraw.Label(g, fLab, halts[i], Lt(m.Stops[i].name, m.Stops[i].time), Theme.Text, HudMapRender.ItineraryCol, drawn);
            foreach (var (p, code) in infrPts)
                MapDetailDraw.Label(g, fLab, p, Carne.Code(code), Color.FromArgb(255, 220, 220), Carne.Red, drawn);
            g.Clip = oldClip;
        }

        // Leyenda bajo el mapa.
        internal static void PaintTripKey(Graphics g, Rectangle r, bool infr, Font f)
        {
            int x = r.X;
            void Item(Action<int, int> icon, string text)
            {
                int cy = r.Y + r.Height / 2;
                icon(x, cy); x += 22;
                int w = TextRenderer.MeasureText(text, f, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g, text, f, new Rectangle(x, r.Y, w + 2, r.Height), Theme.Subtle, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
                x += w + 16;
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Item((ix, cy) => { using var p = new Pen(HudMapRender.ItineraryCol, 3.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round }; g.DrawLine(p, ix, cy, ix + 16, cy); }, I18n.T("Recorrido"));
            Item((ix, cy) => { using var b = new SolidBrush(StartCol); g.FillEllipse(b, ix + 3, cy - 5, 10, 10); }, I18n.T("Salida"));
            Item((ix, cy) => { using var b = new SolidBrush(EndCol); g.FillEllipse(b, ix + 3, cy - 5, 10, 10); }, I18n.T("Llegada"));
            Item((ix, cy) => { using var b = new SolidBrush(Color.FromArgb(255, 245, 225)); using var p = new Pen(HudMapRender.ItineraryCol, 2f); g.FillEllipse(b, ix + 4, cy - 4, 8, 8); g.DrawEllipse(p, ix + 4, cy - 4, 8, 8); }, I18n.T("Parada"));
            if (infr)
                Item((ix, cy) => { using var b = new SolidBrush(Carne.Red); g.FillPolygon(b, new[] { new PointF(ix + 8, cy - 6), new PointF(ix + 15, cy + 5), new PointF(ix + 1, cy + 5) }); }, I18n.T("Infracción"));
        }

        // Tarjeta del informe: rótulo, mapa y leyenda. Un clic lo abre en grande.
        sealed class TripMapCard : Control
        {
            readonly TripMap _m; readonly double _km; readonly bool _infr;
            readonly Font _fT = Theme.Font(7.5f, FontStyle.Bold), _fK = Theme.Font(8.25f);
            public TripMapCard(TripMap m, double km, bool infr)
            {
                _m = m; _km = km; _infr = infr && m.Infractions.Count > 0;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Height = Theme.Px(330);
                Cursor = Cursors.Hand;
            }
            protected override void Dispose(bool disposing) { if (disposing) { _fT.Dispose(); _fK.Dispose(); } base.Dispose(disposing); }
            Rectangle MapRect => new Rectangle(Theme.Px(12), Theme.Px(34), Width - Theme.Px(24), Height - Theme.Px(34) - Theme.Px(36));
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Bg);
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                g.SmoothingMode = SmoothingMode.AntiAlias; Theme.FillRound(g, r, Theme.Px(12), Theme.Surface);
                int x = Theme.Px(16);
                string cap = I18n.T("RECORRIDO DEL SERVICIO") + (_km > 0 ? "  ·  " + _km.ToString("N1", Es) + " km" : "");
                TextRenderer.DrawText(g, cap, _fT, new Rectangle(x, Theme.Px(12), Width - 2 * x, Theme.Px(16)), Theme.Subtle, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "⤢ " + I18n.T("Clic para ampliar"), _fT, new Rectangle(x, Theme.Px(12), Width - 2 * x, Theme.Px(16)), Theme.Subtle, TextFormatFlags.NoPadding | TextFormatFlags.Right);
                var mr = MapRect;
                using (var path = Theme.Round(mr, Theme.Px(8)))
                {
                    var st = g.Save();
                    g.SetClip(path);
                    PaintTripMap(g, mr, _m, _infr, 1f);
                    g.Restore(st);
                }
                PaintTripKey(g, new Rectangle(x, mr.Bottom + Theme.Px(8), Width - 2 * x, Theme.Px(22)), _infr, _fK);
            }
            protected override void OnClick(EventArgs e)
            {
                base.OnClick(e);
                using var f = new TripMapWindow(_m, _infr, _km);
                f.ShowDialog(FindForm());
            }
        }

        // Ventana grande: el mismo mapa, con rueda para acercar/alejar, arrastre para mover y doble clic para encuadrar.
        sealed class TripMapWindow : Form
        {
            readonly TripMap _m; readonly bool _infr;
            double _zoom = 1, _cLat = double.NaN, _cLon = double.NaN;
            Point? _drag; double _dLat, _dLon;
            readonly Font _fK = Theme.Font(9f);
            public TripMapWindow(TripMap m, bool infr, double km)
            {
                _m = m; _infr = infr;
                Text = I18n.T("Recorrido del servicio") + (km > 0 ? " · " + km.ToString("N1", Es) + " km" : "");
                BackColor = Theme.Bg; ForeColor = Theme.Text;
                StartPosition = FormStartPosition.CenterParent;
                var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                ClientSize = new Size(Math.Min(1200, wa.Width - 80), Math.Min(820, wa.Height - 80));
                MinimizeBox = false; KeyPreview = true;
                try { Icon = Icon.ExtractAssociatedIcon((Environment.ProcessPath ?? AppContext.BaseDirectory)); } catch { }
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            }
            protected override void Dispose(bool disposing) { if (disposing) _fK.Dispose(); base.Dispose(disposing); }
            Rectangle MapRect => new Rectangle(0, 0, ClientSize.Width, ClientSize.Height - 40);

            // centro actual (si aún es el encuadre, el del recorrido)
            (double lat, double lon) Center()
            {
                if (!double.IsNaN(_cLat)) return (_cLat, _cLon);
                double a = double.MaxValue, b = double.MinValue, c = double.MaxValue, d = double.MinValue;
                foreach (var p in _m.Trail) { a = Math.Min(a, p.lat); b = Math.Max(b, p.lat); c = Math.Min(c, p.lon); d = Math.Max(d, p.lon); }
                foreach (var s in _m.Stops) { a = Math.Min(a, s.lat); b = Math.Max(b, s.lat); c = Math.Min(c, s.lon); d = Math.Max(d, s.lon); }
                return ((a + b) / 2, (c + d) / 2);
            }
            // metros por píxel de la vista actual (el mismo cálculo que PaintTripMap)
            (double mppLat, double mppLon) ViewScale()
            {
                double a = double.MaxValue, b = double.MinValue, c = double.MaxValue, d = double.MinValue;
                foreach (var p in _m.Trail) { a = Math.Min(a, p.lat); b = Math.Max(b, p.lat); c = Math.Min(c, p.lon); d = Math.Max(d, p.lon); }
                foreach (var s in _m.Stops) { a = Math.Min(a, s.lat); b = Math.Max(b, s.lat); c = Math.Min(c, s.lon); d = Math.Max(d, s.lon); }
                double kx = Math.Max(0.01, Math.Cos((a + b) / 2 * Math.PI / 180)), mLat = 111320.0, mLon = 111320.0 * kx;
                double spanX = Math.Max((d - c) * mLon, 300), spanY = Math.Max((b - a) * mLat, 300);
                var mr = MapRect; int pad = (int)(34 * 1.3f);
                double px = Math.Min((mr.Width - 2 * pad) / spanX, (mr.Height - 2 * pad) / spanY) * _zoom;
                return (1 / (mLat * px), 1 / (mLon * px));
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Bg);
                PaintTripMap(g, MapRect, _m, _infr, 1.3f, _zoom, _cLat, _cLon);
                PaintTripKey(g, new Rectangle(16, MapRect.Bottom + 9, ClientSize.Width - 32, 22), _infr, _fK);
                string hint = I18n.T("Rueda: acercar · arrastrar: mover · doble clic: todo el recorrido");
                TextRenderer.DrawText(g, hint, _fK, new Rectangle(16, MapRect.Bottom + 9, ClientSize.Width - 32, 22), Theme.Subtle, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                // acerca hacia el cursor: el punto bajo el ratón se queda donde está
                var (la, lo) = Center(); var (sLa, sLo) = ViewScale(); var mr = MapRect;
                double cx = mr.X + mr.Width / 2.0, cy = mr.Y + mr.Height / 2.0;
                double pLat = la - (e.Y - cy) * sLa, pLon = lo + (e.X - cx) * sLo;
                double nz = Math.Max(1, Math.Min(400, _zoom * (e.Delta > 0 ? 1.4 : 1 / 1.4)));
                double f = _zoom / nz;
                _zoom = nz;
                _cLat = pLat + (la - pLat) * f; _cLon = pLon + (lo - pLon) * f;
                if (_zoom <= 1.0001) { _cLat = _cLon = double.NaN; }
                Invalidate();
            }
            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _drag = e.Location; (_dLat, _dLon) = Center(); Cursor = Cursors.SizeAll; } }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_drag == null) return;
                var (sLa, sLo) = ViewScale();
                _cLat = _dLat + (e.Y - _drag.Value.Y) * sLa; _cLon = _dLon - (e.X - _drag.Value.X) * sLo;
                Invalidate();
            }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _drag = null; Cursor = Cursors.Default; }
            protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); _zoom = 1; _cLat = _cLon = double.NaN; Invalidate(); }
        }
    }
}
