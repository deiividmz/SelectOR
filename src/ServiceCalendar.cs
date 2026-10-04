// Calendario de los servicios (al lado de la lista de Servicios): un mes, con los días que tienen
// servicios coloreados según cuántos hubo (más intenso = más servicios). Un clic en un día lleva la
// lista a ese día; las flechas cambian de mes. Al desplazar la lista, el calendario la sigue.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class ServiceCalendar : Control
    {
        IReadOnlyDictionary<DateTime, (int n, double net)> _days = new Dictionary<DateTime, (int, double)>();
        DateTime _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        DateTime _current = DateTime.MinValue;   // día que se ve arriba en la lista
        int _hover = -1;                         // celda (0..41), -2 = mes anterior, -3 = mes siguiente
        public event Action<DateTime> DayClicked;

        static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
        readonly Font _fTitle = Theme.Font(10f, FontStyle.Bold), _fDow = Theme.Font(8f, FontStyle.Bold),
                      _fDay = Theme.Font(9f), _fDayB = Theme.Font(9f, FontStyle.Bold), _fSmall = Theme.Font(8.5f);
        Rectangle _prev, _next, _grid;
        int _cell;

        public ServiceCalendar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var f in new[] { _fTitle, _fDow, _fDay, _fDayB, _fSmall }) f.Dispose();
            base.Dispose(disposing);
        }

        // Días con servicios. Si el mes que se ve no tiene ninguno, se va al del servicio más reciente.
        public void SetDays(IReadOnlyDictionary<DateTime, (int n, double net)> days)
        {
            _days = days ?? new Dictionary<DateTime, (int, double)>();
            bool any = false; DateTime latest = DateTime.MinValue;
            foreach (var d in _days.Keys) { if (d.Year == _month.Year && d.Month == _month.Month) any = true; if (d > latest) latest = d; }
            if (!any && latest != DateTime.MinValue) _month = new DateTime(latest.Year, latest.Month, 1);
            Invalidate();
        }

        // La lista se ha desplazado: se marca ese día y se enseña su mes.
        public void SetCurrent(DateTime day)
        {
            day = day.Date;
            if (day == _current) return;
            _current = day;
            if (day != DateTime.MinValue) _month = new DateTime(day.Year, day.Month, 1);
            Invalidate();
        }

        CultureInfo Ci => I18n.English ? CultureInfo.GetCultureInfo("en-GB") : Es;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int pad = Theme.Px(10), w = Width - 2 * pad;
            var card = new Rectangle(0, 0, Width - 1, Height - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Theme.Surface)) using (var p = Theme.Round(card, Theme.Px(10))) g.FillPath(b, p);
            g.SmoothingMode = SmoothingMode.None;

            // Cabecera: ‹ Mes Año ›
            int y = pad, hh = Theme.Px(26);
            _prev = new Rectangle(pad, y, Theme.Px(26), hh);
            _next = new Rectangle(Width - pad - Theme.Px(26), y, Theme.Px(26), hh);
            string title = _month.ToString("MMMM yyyy", Ci);
            title = Ci.TextInfo.ToUpper(title[0]) + title.Substring(1);
            TextRenderer.DrawText(g, title, _fTitle, new Rectangle(_prev.Right, y, _next.Left - _prev.Right, hh), Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            foreach (var (r, t, z) in new[] { (_prev, "‹", -2), (_next, "›", -3) })
            {
                if (_hover == z) using (var hb = new SolidBrush(Theme.SurfaceHi)) using (var hp = Theme.Round(r, Theme.Px(5))) g.FillPath(hb, hp);
                TextRenderer.DrawText(g, t, _fTitle, r, _hover == z ? Theme.Text : Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            y += hh + Theme.Px(6);

            // Días de la semana (lunes primero)
            _cell = Math.Max(Theme.Px(18), w / 7);
            int gx = pad + (w - _cell * 7) / 2;
            string[] dow = I18n.English ? new[] { "M", "T", "W", "T", "F", "S", "S" } : new[] { "L", "M", "X", "J", "V", "S", "D" };
            for (int c = 0; c < 7; c++)
                TextRenderer.DrawText(g, dow[c], _fDow, new Rectangle(gx + c * _cell, y, _cell, Theme.Px(18)), Theme.Subtle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            y += Theme.Px(20);
            int ch = Math.Min(_cell, Theme.Px(32));
            _grid = new Rectangle(gx, y, _cell * 7, ch * 6);

            int max = 1, mN = 0; double mNet = 0;
            int dim = DateTime.DaysInMonth(_month.Year, _month.Month);
            for (int d = 1; d <= dim; d++)
                if (_days.TryGetValue(new DateTime(_month.Year, _month.Month, d), out var v)) { max = Math.Max(max, v.n); mN += v.n; mNet += v.net; }
            int offset = ((int)_month.DayOfWeek + 6) % 7;   // lunes = 0
            var today = DateTime.Today;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int k = 0; k < 42; k++)
            {
                int d = k - offset + 1;
                if (d < 1 || d > dim) continue;
                var day = new DateTime(_month.Year, _month.Month, d);
                var rc = new Rectangle(gx + (k % 7) * _cell + 1, y + (k / 7) * ch + 1, _cell - 2, ch - 2);
                bool has = _days.TryGetValue(day, out var v);
                if (has)
                {
                    int a = 70 + (int)Math.Round(150.0 * v.n / max);
                    using var b = new SolidBrush(Color.FromArgb(Math.Min(230, a), Theme.Accent));
                    using var p = Theme.Round(rc, Theme.Px(5));
                    g.FillPath(b, p);
                }
                else if (_hover == k)
                {
                    using var b = new SolidBrush(Theme.Surface2); using var p = Theme.Round(rc, Theme.Px(5)); g.FillPath(b, p);
                }
                if (day == _current || (has && _hover == k))
                    using (var pen = new Pen(day == _current ? Theme.Text : Theme.Subtle, 1.5f)) using (var p = Theme.Round(rc, Theme.Px(5))) g.DrawPath(pen, p);
                else if (day == today)
                    using (var pen = new Pen(Theme.AccentHi, 1f) { DashStyle = DashStyle.Dot }) using (var p = Theme.Round(rc, Theme.Px(5))) g.DrawPath(pen, p);
                TextRenderer.DrawText(g, d.ToString(), has ? _fDayB : _fDay, rc, has ? Color.White : Theme.Subtle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            g.SmoothingMode = SmoothingMode.None;
            y = _grid.Bottom + Theme.Px(8);

            // Totales del mes y leyenda
            string tot = mN == 0 ? I18n.T("Sin servicios este mes")
                : string.Format(I18n.T(mN == 1 ? "{0} servicio" : "{0} servicios"), mN.ToString("N0", Es)) + "  ·  " + mNet.ToString("+#,##0;−#,##0", Es) + " €";
            TextRenderer.DrawText(g, tot, _fSmall, new Rectangle(pad, y, w, Theme.Px(18)), mN == 0 ? Theme.Subtle : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            y += Theme.Px(22);
            string lo = I18n.T("Menos"), hi = I18n.T("Más");
            int sw = Theme.Px(12), lw = TextRenderer.MeasureText(g, lo, _fSmall).Width, hw = TextRenderer.MeasureText(g, hi, _fSmall).Width;
            int total = lw + hw + 4 * (sw + 3) + Theme.Px(10), lx = pad + (w - total) / 2;
            TextRenderer.DrawText(g, lo, _fSmall, new Point(lx, y), Theme.Subtle, TextFormatFlags.NoPadding);
            lx += lw + Theme.Px(5);
            for (int k = 0; k < 4; k++)
            {
                using var b = new SolidBrush(Color.FromArgb(70 + 50 * k, Theme.Accent));
                g.FillRectangle(b, lx, y + Theme.Px(2), sw, sw); lx += sw + 3;
            }
            TextRenderer.DrawText(g, hi, _fSmall, new Point(lx + Theme.Px(5), y), Theme.Subtle, TextFormatFlags.NoPadding);
        }

        // Alto que necesita para su ancho.
        public int NeededHeight(int width)
        {
            int pad = Theme.Px(10), cell = Math.Max(Theme.Px(18), (width - 2 * pad) / 7), ch = Math.Min(cell, Theme.Px(32));
            return pad + Theme.Px(26) + Theme.Px(6) + Theme.Px(20) + ch * 6 + Theme.Px(8) + Theme.Px(22) + Theme.Px(18) + pad;
        }

        int HitAt(Point p)
        {
            if (_prev.Contains(p)) return -2;
            if (_next.Contains(p)) return -3;
            if (!_grid.Contains(p) || _cell <= 0) return -1;
            int ch = _grid.Height / 6, c = (p.X - _grid.X) / _cell, r = (p.Y - _grid.Y) / Math.Max(1, ch);
            return r * 7 + c;
        }

        DateTime DayAt(int k)
        {
            int offset = ((int)_month.DayOfWeek + 6) % 7, d = k - offset + 1;
            return d < 1 || d > DateTime.DaysInMonth(_month.Year, _month.Month) ? DateTime.MinValue : new DateTime(_month.Year, _month.Month, d);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitAt(e.Location);
            if (h >= 0 && DayAt(h) == DateTime.MinValue) h = -1;
            bool hand = h == -2 || h == -3 || (h >= 0 && _days.ContainsKey(DayAt(h)));
            Cursor = hand ? Cursors.Hand : Cursors.Default;
            if (h != _hover) { _hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != -1) { _hover = -1; Invalidate(); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int h = HitAt(e.Location);
            if (h == -2) { _month = _month.AddMonths(-1); Invalidate(); return; }
            if (h == -3) { _month = _month.AddMonths(1); Invalidate(); return; }
            if (h < 0) return;
            var d = DayAt(h);
            if (d != DateTime.MinValue && _days.ContainsKey(d)) { _current = d; Invalidate(); DayClicked?.Invoke(d); }
        }
    }
}
