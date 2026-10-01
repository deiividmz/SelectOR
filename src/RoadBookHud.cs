// HUD transparente de la hoja de ruta: estaciones del itinerario en orden, con los km que faltan y la
// hora estimada de paso (hora del simulador). Las pasadas quedan en gris con la hora real de paso.
// Clic en el punto de una estación: parar en ella o pasar sin parar. Se mueve arrastrando, se
// agranda por la esquina y se pliega o se cierra desde la cabecera, como el HUD del chat.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public class RoadBookHud : Form
    {
        readonly AppPrefs _prefs;
        readonly RoadBook _rb;
        readonly System.Windows.Forms.Timer _tick;
        public Func<double> GameNow;          // hora del simulador (s desde medianoche; NaN = no se sabe)
        public Func<string> Info;             // línea bajo la cabecera (velocidad máx. del tren, etc.)
        public Action CloseRequested, Changed, OpenMap;

        bool _collapsed, _hover, _down, _dragging, _resizing;
        Point _downScreen, _formAtDown; Size _sizeAtDown;
        Rectangle _hitCollapse, _hitClose, _hitGrip, _hitMap, _hitAll, _hitNone;
        readonly List<(Rectangle hit, int stop)> _dotHits = new();
        int _hoverZone = -1, _scroll, _lastNext = -1;

        const int HdrH = 32, SubH = 22, RowH = 34, FootH = 22, MinW = 260, MinH = 150, MaxW = 700, MaxH = 1000;
        const double IdleOpacity = 0.82;
        static readonly Color Amber = Color.FromArgb(240, 180, 70);
        static readonly Color Teal = Color.FromArgb(94, 190, 155);

        public RoadBookHud(AppPrefs prefs, RoadBook rb)
        {
            _prefs = prefs; _rb = rb;
            _collapsed = prefs?.RoadHudCollapsed ?? false;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Surface;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Opacity = IdleOpacity;
            Size = TargetSize();
            Location = InitialLocation();
            ApplyRegion();
            _tick = new System.Windows.Forms.Timer { Interval = 250 };
            _tick.Tick += (s, e) =>
            {
                bool inside = Bounds.Contains(Cursor.Position);
                if (inside != _hover) { _hover = inside; Opacity = inside ? 1.0 : IdleOpacity; Invalidate(); }
            };
            _tick.Start();
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x00000080 /*TOOLWINDOW*/; return cp; }
        }

        Size TargetSize()
        {
            int w = Math.Max(MinW, Math.Min(MaxW, _prefs?.RoadHudW ?? 330));
            int h = Math.Max(MinH, Math.Min(MaxH, _prefs?.RoadHudH ?? 360));
            return _collapsed ? new Size(w, HdrH) : new Size(w, h);
        }

        Point InitialLocation()
        {
            try
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                if (_prefs != null && _prefs.RoadHudX >= 0 && _prefs.RoadHudY >= 0)
                    return new Point(Math.Min(Math.Max(_prefs.RoadHudX, wa.Left), wa.Right - Width),
                                     Math.Min(Math.Max(_prefs.RoadHudY, wa.Top), wa.Bottom - Height));
                return new Point(wa.Right - Width - 18, wa.Top + 90);   // arriba a la derecha
            }
            catch { return new Point(60, 60); }
        }

        void ApplyRegion() { using var p = Theme.Round(new Rectangle(0, 0, Width, Height), 12); Region = new Region(p); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); ApplyRegion(); }

        void SavePos()
        {
            if (_prefs == null) return;
            _prefs.RoadHudX = Left; _prefs.RoadHudY = Top;
            if (!_collapsed) { _prefs.RoadHudW = Width; _prefs.RoadHudH = Height; }
        }

        void ToggleCollapse()
        {
            _collapsed = !_collapsed;
            if (_prefs != null) _prefs.RoadHudCollapsed = _collapsed;
            Size = TargetSize(); ApplyRegion(); Invalidate(); SavePos();
        }

        int Zone(Point p)
        {
            if (_hitClose.Contains(p)) return 0;
            if (_hitCollapse.Contains(p)) return 1;
            if (_hitMap.Contains(p)) return 2;
            if (_hitAll.Contains(p)) return 3;
            if (_hitNone.Contains(p)) return 4;
            for (int i = 0; i < _dotHits.Count; i++) if (_dotHits[i].hit.Contains(p)) return 10 + i;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _downScreen = Cursor.Position; _formAtDown = Location;
                if (!_collapsed && _hitGrip.Contains(e.Location)) { _resizing = true; _sizeAtDown = Size; }
                else _down = true;
                _dragging = false;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var now = Cursor.Position;
            if (_resizing)
            {
                int w = Math.Max(MinW, Math.Min(MaxW, _sizeAtDown.Width + now.X - _downScreen.X));
                int h = Math.Max(MinH, Math.Min(MaxH, _sizeAtDown.Height + now.Y - _downScreen.Y));
                if (w != Width || h != Height) Size = new Size(w, h);
            }
            else if (_down)
            {
                if (!_dragging && (Math.Abs(now.X - _downScreen.X) > 3 || Math.Abs(now.Y - _downScreen.Y) > 3)) _dragging = true;
                if (_dragging) Location = new Point(_formAtDown.X + now.X - _downScreen.X, _formAtDown.Y + now.Y - _downScreen.Y);
            }
            else
            {
                int z = Zone(e.Location);
                if (z != _hoverZone) { _hoverZone = z; Invalidate(); }
                Cursor = z >= 0 ? Cursors.Hand : (!_collapsed && _hitGrip.Contains(e.Location) ? Cursors.SizeNWSE : Cursors.Default);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool click = _down && !_dragging, moved = _dragging || _resizing;
            _down = _dragging = _resizing = false;
            if (moved) SavePos();
            if (click && e.Button == MouseButtons.Left)
            {
                int z = Zone(e.Location);
                if (z == 0) CloseRequested?.Invoke();
                else if (z == 1) ToggleCollapse();
                else if (z == 2) OpenMap?.Invoke();
                else if (z == 3 || z == 4) { _rb.SetAllHalts(z == 3); Changed?.Invoke(); Invalidate(); }
                else if (z >= 10) { _rb.ToggleHalt(_dotHits[z - 10].stop); Changed?.Invoke(); Invalidate(); }
                else if (_collapsed) ToggleCollapse();
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e) { if (_hoverZone != -1) { _hoverZone = -1; Invalidate(); } base.OnMouseLeave(e); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _scroll = Math.Max(0, _scroll - Math.Sign(e.Delta));
            Invalidate();
            base.OnMouseWheel(e);
        }

        double Game() { try { return GameNow?.Invoke() ?? double.NaN; } catch { return double.NaN; } }

        static string Clock(double secs)
        {
            if (double.IsNaN(secs)) return "--:--";
            int s = ((int)Math.Round(secs) % 86400 + 86400) % 86400;
            return $"{s / 3600:00}:{s % 3600 / 60:00}";
        }

        static string Km(double m) => (m / 1000.0).ToString(m >= 10000 ? "0" : "0.0", I18n.English ? System.Globalization.CultureInfo.InvariantCulture : System.Globalization.CultureInfo.GetCultureInfo("es-ES")) + " km";

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, ClientRectangle);
            using (var acc = new SolidBrush(Amber)) g.FillRectangle(acc, 0, 0, Width, 2);
            _dotHits.Clear();

            // Cabecera
            var ico = new Rectangle(10, (HdrH - 16) / 2 + 1, 14, 16);
            DrawListGlyph(g, ico, Amber);
            _hitClose = new Rectangle(Width - 28, 5, 22, HdrH - 10);
            _hitCollapse = new Rectangle(_hitClose.Left - 24, 5, 22, HdrH - 10);
            _hitMap = new Rectangle(_hitCollapse.Left - 26, 5, 24, HdrH - 10);
            double now = Game();
            var end = _rb.Stops.Count > 0 ? _rb.Stops[^1] : null;
            string title = I18n.T("Hoja de ruta");
            if (end != null && !double.IsNaN(end.EtaS) && !double.IsNaN(now)) title += "  ·  " + string.Format(I18n.T("llegada {0}"), Clock(now + end.EtaS));
            using (var tf = Theme.Font(9.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, title, tf, new Rectangle(ico.Right + 8, 0, _hitMap.Left - ico.Right - 10, HdrH), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (_hoverZone == 2) using (var hb = new SolidBrush(Theme.SurfaceHi)) using (var hp = Theme.Round(_hitMap, 6)) g.FillPath(hb, hp);
            using (var pen = new Pen(_hoverZone == 2 ? Theme.Text : Theme.Subtle, 1.4f))
            {
                var m = new Rectangle(_hitMap.Left + 5, _hitMap.Top + 5, _hitMap.Width - 10, _hitMap.Height - 10);
                g.DrawRectangle(pen, m);
                g.DrawLine(pen, m.Left + m.Width / 3, m.Top, m.Left + m.Width / 3, m.Bottom);
                g.DrawLine(pen, m.Left + 2 * m.Width / 3, m.Top, m.Left + 2 * m.Width / 3, m.Bottom);
            }
            DrawHdrBtn(g, _hitCollapse, _collapsed ? "+" : "–", _hoverZone == 1);
            DrawHdrBtn(g, _hitClose, "×", _hoverZone == 0);
            if (_collapsed) return;

            // Línea de estado
            string sub;
            Color subCol = Theme.Subtle;
            if (_rb.Building) sub = I18n.T("Preparando el esquema de vías…");
            else if (_rb.Problem != null) { sub = _rb.Problem; subCol = Color.FromArgb(235, 150, 120); }
            else if (!_rb.HasPlan) sub = I18n.T("Marca el itinerario en el mapa grande.");
            else if (_rb.OffRoute) { sub = I18n.T("Fuera del itinerario: se recalculará en unos segundos…"); subCol = Color.FromArgb(235, 150, 120); }
            else sub = string.Format(I18n.T("Faltan {0}"), Km(Math.Max(0, _rb.TotalLen - _rb.Progress))) + (Info?.Invoke() is string inf && inf.Length > 0 ? "  ·  " + inf : "");
            using (var sf = Theme.Font(8.3f))
                TextRenderer.DrawText(g, sub, sf, new Rectangle(12, HdrH, Width - 24, SubH), subCol,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            using (var p = new Pen(Color.FromArgb(45, 255, 255, 255))) g.DrawLine(p, 8, HdrH + SubH, Width - 8, HdrH + SubH);

            var list = new Rectangle(0, HdrH + SubH + 2, Width, Height - HdrH - SubH - FootH - 4);
            if (!_rb.HasPlan || _rb.Stops.Count == 0)
            {
                using var f = Theme.Font(9f);
                string t = _rb.HasPlan ? I18n.T("No hay estaciones en el itinerario.")
                         : I18n.T("Abre el mapa grande, pulsa «Itinerario» y haz clic en la vía, en uno o varios puntos.");
                TextRenderer.DrawText(g, t, f, Rectangle.Inflate(list, -16, -10), Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
            else DrawRows(g, list, now);

            // Pie: «Paradas: N · Todas · Ninguna» y la pista
            _hitAll = _hitNone = Rectangle.Empty;
            using (var ff = Theme.Font(7.8f))
            using (var fl = Theme.Font(7.8f, FontStyle.Bold))
            {
                int x = 12, fy = Height - FootH;
                if (_rb.HasPlan && _rb.Stops.Exists(s => !s.IsEnd && !s.IsReverse && !s.Passed))
                {
                    string cnt = string.Format(I18n.T("Paradas: {0}"), _rb.HaltCount);
                    var cs = TextRenderer.MeasureText(cnt, ff, Size.Empty, TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, cnt, ff, new Point(x, fy + (FootH - cs.Height) / 2), Color.FromArgb(150, 156, 162), TextFormatFlags.NoPadding);
                    x += cs.Width + 10;
                    foreach (var (txt, zone) in new[] { (I18n.T("Todas"), 3), (I18n.T("Ninguna"), 4) })
                    {
                        var ts = TextRenderer.MeasureText(txt, fl, Size.Empty, TextFormatFlags.NoPadding);
                        var r = new Rectangle(x - 4, fy + 3, ts.Width + 8, FootH - 6);
                        if (_hoverZone == zone) using (var hb = new SolidBrush(Theme.SurfaceHi)) using (var hp = Theme.Round(r, 5)) g.FillPath(hb, hp);
                        TextRenderer.DrawText(g, txt, fl, r, Amber, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        if (zone == 3) _hitAll = r; else _hitNone = r;
                        x = r.Right + 6;
                    }
                    x += 4;
                }
                TextRenderer.DrawText(g, I18n.T("Clic en una estación: parar o pasar"), ff, new Rectangle(x, fy, Width - x - 22, FootH), Color.FromArgb(125, 131, 136),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            _hitGrip = new Rectangle(Width - 18, Height - 18, 18, 18);
            using (var gp = new Pen(Color.FromArgb(110, 255, 255, 255), 1.2f))
                for (int k = 4; k <= 12; k += 4) g.DrawLine(gp, Width - 3 - k, Height - 3, Width - 3, Height - 3 - k);
        }

        void DrawRows(Graphics g, Rectangle list, double now)
        {
            var stops = _rb.Stops;
            int next = stops.FindIndex(s => !s.Passed);
            int visible = Math.Max(1, list.Height / RowH);
            // al pasar una estación, la lista avanza sola (se deja una pasada a la vista)
            if (next != _lastNext) { _lastNext = next; _scroll = Math.Max(0, (next < 0 ? stops.Count : next) - 1); }
            _scroll = Math.Max(0, Math.Min(_scroll, Math.Max(0, stops.Count - visible)));
            var oldClip = g.Clip; g.SetClip(list);
            using var fName = Theme.Font(9.3f, FontStyle.Bold);
            using var fSmall = Theme.Font(8f);
            using var fTime = Theme.Font(10.5f, FontStyle.Bold);
            int y = list.Top;
            for (int i = _scroll; i < stops.Count && y < list.Bottom; i++, y += RowH)
            {
                var s = stops[i];
                bool isNext = i == next;
                var row = new Rectangle(4, y, Width - 8, RowH);
                if (isNext) using (var b = new SolidBrush(Color.FromArgb(40, Amber))) using (var p = Theme.Round(row, 8)) g.FillPath(b, p);
                // línea vertical del recorrido
                int cx = 20, cy = y + RowH / 2;
                using (var lp = new Pen(Color.FromArgb(s.Passed ? 70 : 140, Amber), 2f))
                {
                    if (i > 0) g.DrawLine(lp, cx, y, cx, cy);
                    if (i < stops.Count - 1) g.DrawLine(lp, cx, cy, cx, y + RowH);
                }
                var dot = new Rectangle(cx - 6, cy - 6, 12, 12);
                var dotCol = s.Passed ? Color.FromArgb(120, 126, 132) : (s.IsEnd ? Color.FromArgb(225, 95, 95) : Amber);
                if (s.Halt || s.IsEnd) using (var b = new SolidBrush(dotCol)) g.FillEllipse(b, dot);
                else { using (var b = new SolidBrush(Theme.Surface)) g.FillEllipse(b, dot); using (var p = new Pen(dotCol, 2f)) g.DrawEllipse(p, dot); }
                if (s.IsReverse)
                {
                    using var rp = new Pen(Theme.Surface, 1.6f);
                    g.DrawArc(rp, dot.Left + 2, dot.Top + 2, dot.Width - 4, dot.Height - 4, 200, 250);
                }
                if (!s.IsEnd && !s.IsReverse && !s.Passed)
                {
                    int idx = _dotHits.Count;
                    _dotHits.Add((new Rectangle(row.Left, Math.Max(row.Top, list.Top), row.Width - 12, Math.Min(row.Bottom, list.Bottom) - Math.Max(row.Top, list.Top)), i));
                    if (_hoverZone == 10 + idx)
                    {
                        using (var hb = new SolidBrush(Color.FromArgb(28, 255, 255, 255))) using (var hp = Theme.Round(row, 8)) g.FillPath(hb, hp);
                        using (var p = new Pen(Theme.Text, 1.2f)) g.DrawEllipse(p, Rectangle.Inflate(dot, 3, 3));
                    }
                }

                var nameCol = s.Passed ? Color.FromArgb(130, 136, 142) : Theme.Text;
                string timeTxt = s.Passed ? (double.IsNaN(s.PassedAt) ? "✓" : "✓ " + Clock(s.PassedAt))
                               : (double.IsNaN(s.EtaS) || double.IsNaN(now) ? "--:--" : Clock(now + s.EtaS));
                int timeW = TextRenderer.MeasureText(g, "✓ 00:00", fTime, Size.Empty, TextFormatFlags.NoPadding).Width + 6;
                var tRect = new Rectangle(Width - timeW - 14, y, timeW, RowH);
                TextRenderer.DrawText(g, timeTxt, fTime, tRect, s.Passed ? Color.FromArgb(130, 136, 142) : (isNext ? Amber : Theme.Text),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                int nx = cx + 14, nw = tRect.Left - nx - 6;
                TextRenderer.DrawText(g, s.Name, fName, new Rectangle(nx, y + 2, nw, RowH / 2), nameCol,
                    TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                string det;
                if (s.Passed) det = I18n.T("pasada");
                else
                {
                    double left = Math.Max(0, s.Dist - _rb.Progress);
                    det = Km(left) + " · " + (s.IsEnd ? I18n.T("final") : s.IsReverse ? I18n.T("invierte la marcha") : s.Halt ? I18n.T("para") : I18n.T("pasa sin parar"));
                    if (!double.IsNaN(s.EtaS) && s.EtaS < 3600 * 6) det += " · " + string.Format(I18n.T("en {0} min"), Math.Max(0, (int)Math.Round(s.EtaS / 60)));
                }
                TextRenderer.DrawText(g, det, fSmall, new Rectangle(nx, y + RowH / 2 + 1, nw, RowH / 2 - 2), Color.FromArgb(150, 156, 162),
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            g.Clip = oldClip;
            // indicadores de que hay más arriba / abajo
            using var ab = new SolidBrush(Color.FromArgb(150, Theme.Text));
            if (_scroll > 0) g.FillPolygon(ab, new[] { new Point(Width - 10, list.Top + 3), new Point(Width - 14, list.Top + 9), new Point(Width - 6, list.Top + 9) });
            if (_scroll + visible < stops.Count) g.FillPolygon(ab, new[] { new Point(Width - 10, list.Bottom - 3), new Point(Width - 14, list.Bottom - 9), new Point(Width - 6, list.Bottom - 9) });
        }

        static void DrawListGlyph(Graphics g, Rectangle r, Color c)
        {
            using var pen = new Pen(c, 1.6f);
            int x = r.Left + 3;
            g.DrawLine(pen, x, r.Top + 1, x, r.Bottom - 1);
            using var b = new SolidBrush(c);
            for (int i = 0; i < 3; i++)
            {
                int y = r.Top + 2 + i * 6;
                g.FillEllipse(b, x - 3, y - 1, 6, 6);
                g.DrawLine(pen, x + 6, y + 2, r.Right, y + 2);
            }
        }

        void DrawHdrBtn(Graphics g, Rectangle r, string t, bool hover)
        {
            if (hover) using (var b = new SolidBrush(Theme.SurfaceHi)) using (var p = Theme.Round(r, 6)) g.FillPath(b, p);
            using var f = Theme.Font(11f, FontStyle.Bold);
            TextRenderer.DrawText(g, t, f, r, hover ? Theme.Text : Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        public void CloseHud()
        {
            try { _tick.Stop(); _tick.Dispose(); } catch { }
            SavePos();
            try { Close(); } catch { }
        }
    }
}
