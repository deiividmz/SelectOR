// Plano de una ruta (rutas autorizadas): el mismo dibujo del mapa grande del HUD (MapDetailDraw) a partir del plano
// que subió quien la usa o la pide, para verla sin tenerla instalada. Rueda: acercar (hacia el cursor) · arrastrar:
// mover · doble clic: toda la ruta · ⤢: en una ventana grande. Cada capa sale a su zoom, como en el HUD.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class RoutePlanView : Control
    {
        RoutePlan _plan;
        string _empty = "";
        double _zoom = 1, _cLat = double.NaN, _cLon = double.NaN;
        Point? _drag; double _dLat, _dLon; bool _moved;
        Rectangle _hitExpand, _hitKey;
        bool _keyOpen = true;
        public bool Expandable = true;
        public string WindowTitle = "";
        // Para el planificador de itinerarios (Conducción libre): lo que se dibuja encima del plano con la vista actual
        // (no va en la imagen guardada: cambia sin volver a dibujar el plano), el clic sin arrastrar sobre el plano
        // (lat, lon y metros por píxel) y el texto de ayuda de abajo.
        public Action<Graphics, Func<double, double, PointF>, Rectangle> Overlay;
        public event Action<double, double, double> MapClick;
        public string Hint;
        public void Redraw() => Invalidate();
        public void ResetView() { _zoom = 1; _cLat = _cLon = double.NaN; Invalidate(); }
        static readonly Color MapBg = Color.FromArgb(30, 34, 38);
        // Fuentes de cada repintado, creadas una vez (al arrastrar se repinta muchas veces por segundo).
        readonly Font _fEmpty = Theme.Font(9.5f), _fHint = Theme.Font(8f), _fExp = Theme.Font(12f, FontStyle.Bold),
                      _fKeyT = Theme.Font(8.25f, FontStyle.Bold), _fKey = Theme.Font(8.25f);
        Font _fSt; float _fStK;
        protected override void Dispose(bool disposing)
        {
            if (disposing) { foreach (var f in new[] { _fEmpty, _fHint, _fExp, _fKeyT, _fKey, _fSt }) f?.Dispose(); _settle?.Dispose(); _cache?.Dispose(); }
            base.Dispose(disposing);
        }

        public RoutePlanView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Bg;
        }

        public RoutePlan Plan => _plan;

        DateTime _movedUtc;
        System.Windows.Forms.Timer _settle;
        public bool Moving => _drag != null || DateTime.UtcNow - _movedUtc < TimeSpan.FromMilliseconds(180);
        void Touch()
        {
            _movedUtc = DateTime.UtcNow;
            if (_settle == null) { _settle = new System.Windows.Forms.Timer { Interval = 200 }; _settle.Tick += (s, e) => { if (!Moving) { _settle.Stop(); Invalidate(); } }; }
            _settle.Stop(); _settle.Start();
        }

        public void SetPlan(RoutePlan p, string emptyText = null)
        {
            _plan = p; _empty = emptyText ?? ""; _zoom = 1; _cLat = _cLon = double.NaN;
            _cache?.Dispose(); _cache = null;
            Cursor = p != null && !p.Empty ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        Rectangle MapRect => new Rectangle(0, 0, Width - 1, Height - 1);

        (double lat, double lon) Center()
        {
            if (!double.IsNaN(_cLat)) return (_cLat, _cLon);
            return ((_plan.MinLat + _plan.MaxLat) / 2, (_plan.MinLon + _plan.MaxLon) / 2);
        }

        // px por metro con el zoom actual (encuadre de toda la ruta × zoom)
        double PxPerM()
        {
            var mr = MapRect;
            double lat0 = (_plan.MinLat + _plan.MaxLat) / 2, kx = Math.Max(0.01, Math.Cos(lat0 * Math.PI / 180));
            double spanX = Math.Max((_plan.MaxLon - _plan.MinLon) * 111320.0 * kx, 200), spanY = Math.Max((_plan.MaxLat - _plan.MinLat) * 111320.0, 200);
            int pad = Theme.Px(24);
            return Math.Min((mr.Width - 2 * pad) / spanX, (mr.Height - 2 * pad) / spanY) * _zoom;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var area = MapRect;
            using (var path = Theme.Round(area, Theme.Px(10)))
            {
                using (var b = new SolidBrush(MapBg)) g.FillPath(b, path);
                var st = g.Save();
                g.SetClip(path);
                if (_plan == null || _plan.Empty)
                {
                    TextRenderer.DrawText(g, _empty, _fEmpty, Rectangle.Inflate(area, -20, -20), Theme.Subtle,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                }
                else Paint(g, area);
                g.Restore(st);
            }
        }

        // El plano se dibuja en una imagen (con todo su detalle) una vez por vista. Mientras se arrastra o se usa la rueda,
        // se mueve y se escala esa imagen —casi instantáneo, también en las rutas de decenas de miles de tramos— y al parar
        // se vuelve a dibujar con todo el detalle.
        Bitmap _cache; double _cZoom, _cLat0, _cLon0;

        Func<double, double, PointF> ViewProj(Rectangle area)
        {
            double px = PxPerM();
            var (cLat, cLon) = Center();
            double mLat = 111320.0, mLon = 111320.0 * Math.Cos(cLat * Math.PI / 180);
            double cx = area.X + area.Width / 2.0, cy = area.Y + area.Height / 2.0;
            return (la, lo) => new PointF((float)(cx + (lo - cLon) * mLon * px), (float)(cy - (la - cLat) * mLat * px));
        }

        void Paint(Graphics g, Rectangle area)
        {
            bool fresh = _cache != null && _cache.Width == area.Width && _cache.Height == area.Height;
            if (Moving && fresh)
            {
                double sc = _zoom / _cZoom;
                var pc = ViewProj(area)(_cLat0, _cLon0);
                float w = (float)(area.Width * sc), h = (float)(area.Height * sc);
                using (var bb = new SolidBrush(MapBg)) g.FillRectangle(bb, area);
                g.InterpolationMode = InterpolationMode.Low;
                g.DrawImage(_cache, new RectangleF(pc.X - w / 2, pc.Y - h / 2, w, h));
            }
            else if (fresh && _cZoom == _zoom && (_cLat0, _cLon0) == Center())
                g.DrawImageUnscaled(_cache, area.X, area.Y);   // la misma vista: la imagen ya está hecha
            else
            {
                if (!fresh) { _cache?.Dispose(); _cache = new Bitmap(Math.Max(1, area.Width), Math.Max(1, area.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb); }
                using (var cg = Graphics.FromImage(_cache))
                {
                    cg.SmoothingMode = SmoothingMode.AntiAlias;
                    cg.Clear(MapBg);
                    RenderMap(cg, new Rectangle(0, 0, _cache.Width, _cache.Height));
                }
                _cZoom = _zoom; (_cLat0, _cLon0) = Center();
                g.DrawImageUnscaled(_cache, area.X, area.Y);
            }
            if (Overlay != null) { try { Overlay(g, ViewProj(area), area); } catch { } }
            Overlays(g, area);
        }

        void RenderMap(Graphics g, Rectangle area)
        {
            double px = PxPerM();
            var Proj = ViewProj(area);
            float k = Math.Max(1f, Math.Min(1.5f, Width / 900f + 0.6f));
            MapDetailDraw.Under(g, area, Proj, _plan.Detail, px, k);

            // estaciones: punto claro con borde azul y nombre en etiqueta (sin que se tapen)
            var drawn = new List<RectangleF>();
            float kSt = Math.Max(1f, k * 0.9f);
            if (_fSt == null || Math.Abs(_fStK - kSt) > 0.01f) { _fSt?.Dispose(); _fSt = Theme.Font(8.25f * kSt, FontStyle.Bold); _fStK = kSt; }
            var fSt = _fSt;
            using var bDot = new SolidBrush(MapDetailDraw.PlatformEdge);
            using var ring = new Pen(MapDetailDraw.PlatformCol, 1.4f);
            var vis = RectangleF.Inflate(area, 20, 10);
            float r = 3.4f * k;
            foreach (var (la, lo, name) in _plan.Stations)
            {
                var p = Proj(la, lo);
                if (!vis.Contains(p)) continue;
                g.FillEllipse(bDot, p.X - r, p.Y - r, 2 * r, 2 * r);
                g.DrawEllipse(ring, p.X - r, p.Y - r, 2 * r, 2 * r);
            }
            foreach (var (la, lo, name) in _plan.Stations)
            {
                var p = Proj(la, lo);
                if (!vis.Contains(p) || string.IsNullOrWhiteSpace(name)) continue;
                MapDetailDraw.Label(g, fSt, new PointF(p.X + r - 3, p.Y - fSt.Height / 2f - 6), name, Color.FromArgb(236, 240, 244), MapDetailDraw.PlatformCol, drawn);
            }
        }

        // Lo que va encima del plano (no se mueve con él): leyenda, ayuda y botón de ampliar.
        void Overlays(Graphics g, Rectangle area)
        {
            DrawKey(g, area);
            var fH = _fHint;
            string hint = Hint ?? I18n.T("Rueda: acercar · arrastrar: mover · doble clic: toda la ruta");
            var hs = TextRenderer.MeasureText(hint, fH);
            var hr = new Rectangle(area.Right - hs.Width - Theme.Px(14), area.Bottom - hs.Height - Theme.Px(10), hs.Width + Theme.Px(8), hs.Height + Theme.Px(4));
            using (var path = Theme.Round(hr, Theme.Px(6))) using (var b = new SolidBrush(Color.FromArgb(200, 22, 25, 28))) g.FillPath(b, path);
            TextRenderer.DrawText(g, hint, fH, hr, Color.FromArgb(170, 176, 182), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            _hitExpand = Rectangle.Empty;
            if (Expandable)
            {
                int s = Theme.Px(30);
                _hitExpand = new Rectangle(area.Right - s - Theme.Px(8), area.Y + Theme.Px(8), s, s);
                using (var path = Theme.Round(_hitExpand, Theme.Px(7))) using (var b = new SolidBrush(Color.FromArgb(220, 40, 44, 48))) g.FillPath(b, path);
                var fE = _fExp;
                TextRenderer.DrawText(g, "⤢", fE, _hitExpand, Color.FromArgb(230, 232, 234), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        // Leyenda (arriba a la izquierda), como la del mapa grande del HUD; clic en el título: plegar / desplegar.
        void DrawKey(Graphics g, Rectangle area)
        {
            var fT = _fKeyT; var f = _fKey;
            string title = I18n.T("Leyenda") + (_keyOpen ? "  ▾" : "  ▸");
            var rows = new (string text, int kind)[]
            {
                (I18n.T("estación"), 1), (I18n.T("andén"), 3), (I18n.T("apartadero"), 4),
                (I18n.T("cruce de vías"), 6), (I18n.T("punto de carga"), 7), (I18n.T("topera"), 8),
            };
            int rowH = f.Height + 5, padX = 10;
            int w = TextRenderer.MeasureText(title, fT).Width + 2 * padX;
            foreach (var rr in rows) w = Math.Max(w, 34 + TextRenderer.MeasureText(rr.text, f).Width + padX);
            int h = 8 + fT.Height + 6 + (_keyOpen ? rows.Length * rowH + 4 : 0);
            var box = new Rectangle(area.X + 8, area.Y + 8, w, h);
            using (var path = Theme.Round(box, 9)) { using var b = new SolidBrush(Color.FromArgb(235, 30, 33, 36)); g.FillPath(b, path); }
            TextRenderer.DrawText(g, title, fT, new Point(box.X + padX, box.Y + 7), Color.FromArgb(185, 190, 195), TextFormatFlags.NoPadding);
            _hitKey = new Rectangle(box.X, box.Y, box.Width, 8 + fT.Height + 6);
            if (!_keyOpen) return;
            int y = box.Y + 8 + fT.Height + 8;
            foreach (var (text, kind) in rows)
            {
                int cy = y + rowH / 2 - 2, x0 = box.X + padX;
                switch (kind)
                {
                    case 1:
                        using (var b = new SolidBrush(MapDetailDraw.PlatformEdge)) g.FillEllipse(b, x0 + 5, cy - 4, 8, 8);
                        using (var p = new Pen(MapDetailDraw.PlatformCol, 1.4f)) g.DrawEllipse(p, x0 + 5, cy - 4, 8, 8);
                        break;
                    case 3:
                        using (var pen = new Pen(MapDetailDraw.PlatformEdge, 10f) { StartCap = LineCap.Square, EndCap = LineCap.Square }) g.DrawLine(pen, x0 + 4, cy, x0 + 14, cy);
                        using (var pen = new Pen(MapDetailDraw.PlatformCol, 7f) { StartCap = LineCap.Square, EndCap = LineCap.Square }) g.DrawLine(pen, x0 + 4, cy, x0 + 14, cy);
                        using (var pen = new Pen(MapDetailDraw.TrackCol, 2.6f)) g.DrawLine(pen, x0, cy, x0 + 18, cy);
                        break;
                    case 4:
                        using (var pen = new Pen(MapDetailDraw.SidingCol, 8f) { DashPattern = new[] { 0.7f, 0.45f } }) g.DrawLine(pen, x0 + 1, cy, x0 + 17, cy);
                        using (var pen = new Pen(MapDetailDraw.TrackCol, 2.6f)) g.DrawLine(pen, x0, cy, x0 + 18, cy);
                        break;
                    case 6:
                        using (var pen = new Pen(MapDetailDraw.TrackCol, 2.2f)) { g.DrawLine(pen, x0, cy - 5, x0 + 18, cy + 5); g.DrawLine(pen, x0, cy + 5, x0 + 18, cy - 5); }
                        using (var b = new SolidBrush(MapDetailDraw.DiamondCol)) using (var pen = new Pen(Color.FromArgb(40, 44, 48), 1.2f)) MapDetailDraw.Diamond(g, b, pen, new PointF(x0 + 9, cy), 4.6f);
                        break;
                    case 7: MapDetailDraw.Pickup(g, new PointF(x0 + 9, cy), 1f, true); break;
                    case 8:
                        using (var pen = new Pen(MapDetailDraw.TrackCol, 2.6f)) g.DrawLine(pen, x0, cy, x0 + 12, cy);
                        using (var b = new SolidBrush(MapDetailDraw.EndCol)) using (var pen = new Pen(Color.FromArgb(40, 44, 48), 1f)) { g.FillRectangle(b, x0 + 10, cy - 3, 6, 6); g.DrawRectangle(pen, x0 + 10, cy - 3, 6, 6); }
                        break;
                }
                TextRenderer.DrawText(g, text, f, new Point(box.X + 34, y + 1), Color.FromArgb(230, 232, 234), TextFormatFlags.NoPadding);
                y += rowH;
            }
        }

        bool HasPlan => _plan != null && !_plan.Empty;

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (HasPlan && FindForm()?.ContainsFocus == true) Focus(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!HasPlan) return;
            if (e is HandledMouseEventArgs h) h.Handled = true;
            var (la, lo) = Center(); double px = PxPerM();
            double mLat = 111320.0, mLon = 111320.0 * Math.Cos(la * Math.PI / 180);
            var mr = MapRect; double cx = mr.X + mr.Width / 2.0, cy = mr.Y + mr.Height / 2.0;
            double pLat = la - (e.Y - cy) / (mLat * px), pLon = lo + (e.X - cx) / (mLon * px);
            double nz = Math.Max(1, Math.Min(2000, _zoom * (e.Delta > 0 ? 1.4 : 1 / 1.4)));
            double f = _zoom / nz;
            _zoom = nz;
            _cLat = pLat + (la - pLat) * f; _cLon = pLon + (lo - pLon) * f;
            if (_zoom <= 1.0001) _cLat = _cLon = double.NaN;
            Touch(); Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!HasPlan || e.Button != MouseButtons.Left) return;
            Focus();
            _moved = false;
            if (_hitExpand.Contains(e.Location) || _hitKey.Contains(e.Location)) return;
            _drag = e.Location; (_dLat, _dLon) = Center(); Cursor = Cursors.SizeAll;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag == null) { if (HasPlan) Cursor = _hitExpand.Contains(e.Location) || _hitKey.Contains(e.Location) ? Cursors.Hand : Cursors.SizeAll; return; }
            if (Math.Abs(e.X - _drag.Value.X) + Math.Abs(e.Y - _drag.Value.Y) > 3) _moved = true;
            double px = PxPerM(), mLat = 111320.0, mLon = 111320.0 * Math.Cos(_dLat * Math.PI / 180);
            _cLat = _dLat + (e.Y - _drag.Value.Y) / (mLat * px); _cLon = _dLon - (e.X - _drag.Value.X) / (mLon * px);
            Touch(); Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool wasDrag = _drag != null && _moved;
            _drag = null;
            if (!HasPlan || e.Button != MouseButtons.Left || wasDrag) return;
            if (_hitKey.Contains(e.Location)) { _keyOpen = !_keyOpen; Invalidate(); return; }
            if (_hitExpand.Contains(e.Location)) { OpenWindow(); return; }
            if (MapClick != null)
            {
                var (la, lo) = Center(); double px = PxPerM();
                double mLat = 111320.0, mLon = 111320.0 * Math.Cos(la * Math.PI / 180);
                var mr = MapRect; double cx = mr.X + mr.Width / 2.0, cy = mr.Y + mr.Height / 2.0;
                MapClick(la - (e.Y - cy) / (mLat * px), lo + (e.X - cx) / (mLon * px), 1 / px);
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (!HasPlan || _hitExpand.Contains(e.Location) || _hitKey.Contains(e.Location)) return;
            if (MapClick != null) return;   // en el planificador cada clic marca un punto: la vista entera, con su botón
            _zoom = 1; _cLat = _cLon = double.NaN; Invalidate();
        }

        void OpenWindow()
        {
            using var f = new Form
            {
                Text = WindowTitle, BackColor = Theme.Bg, ForeColor = Theme.Text, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, KeyPreview = true, Padding = new Padding(10)
            };
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            f.ClientSize = new Size(Math.Min(1300, wa.Width - 80), Math.Min(860, wa.Height - 80));
            try { f.Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? AppContext.BaseDirectory); } catch { }
            var v = new RoutePlanView { Dock = DockStyle.Fill, Expandable = false };
            v.SetPlan(_plan, _empty);
            f.Controls.Add(v);
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
            f.Shown += (s, e) => v.Focus();
            f.ShowDialog(FindForm());
        }
    }
}
