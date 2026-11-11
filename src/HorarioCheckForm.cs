// HORARIO DE LA SALA · COMPROBAR HORARIO (2026-11-11, petición): el informe de HorarioCheck en una ventana. A la izquierda,
// cada problema (bloquea / aviso / bien) con su servicio, su hora, dónde, qué pasa y qué hacer; a la derecha, el sitio en
// el mapa; abajo, la línea de tiempo del día: los trenes de cada servicio, los que no saldrán y cuántos hay a la vez frente
// al máximo. «Aplicar sugerencia» cambia el servicio (retrasar sus salidas u otra frecuencia) y se vuelve a comprobar.
// Es una estimación hecha en tu SelectOR, no la simulación del servidor.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class HorarioCheckForm : Form
    {
        static string Tr(string s) => I18n.T(s);
        HorarioCheck.Result _r;
        readonly RouteGraph _g;
        readonly RouteLiveMap _map;
        readonly ListBox _list;
        readonly Timeline _tl;
        readonly Label _sum;
        readonly RoundButton _apply, _again;
        public Func<HorarioCheck.Issue, Task<bool>> Apply;          // aplica la sugerencia (guarda el servicio): true si se ha hecho
        public Func<Task<HorarioCheck.Result>> Recheck;              // vuelve a comprobar (con el horario guardado)

        static readonly Color CBad = Color.FromArgb(239, 83, 80), CWarn = Color.FromArgb(255, 183, 77), COk = Color.FromArgb(102, 187, 106);
        static Color SevCol(int s) => s == 2 ? CBad : s == 1 ? CWarn : COk;
        static string SevText(int s) => s == 2 ? Tr("BLOQUEA") : s == 1 ? Tr("AVISO") : Tr("BIEN");
        internal static readonly Color[] Palette =
        {
            Color.FromArgb(79, 195, 247), Color.FromArgb(255, 183, 77), Color.FromArgb(129, 199, 132), Color.FromArgb(240, 98, 146),
            Color.FromArgb(179, 136, 255), Color.FromArgb(255, 241, 118), Color.FromArgb(77, 208, 225), Color.FromArgb(255, 138, 101),
            Color.FromArgb(174, 213, 129), Color.FromArgb(149, 117, 205), Color.FromArgb(244, 143, 177), Color.FromArgb(144, 202, 249)
        };

        public HorarioCheckForm(HorarioCheck.Result r, RouteGraph g, RouteMapData data)
        {
            _r = r; _g = g;
            Text = Tr("Comprobar horario");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(Theme.Px(1480), Theme.Px(900)); MinimumSize = new Size(Theme.Px(1100), Theme.Px(660));
            DialogKit.Dress(this);
            Font = Theme.Font(9.5f);
            var head = new DialogKit.Header(Tr("Comprobar horario"), Tr("Una estimación hecha en tu SelectOR con todo el horario, no la simulación del servidor: el tráfico real (jugadores, señales) puede cambiarlo."), "🔎") { Dock = DockStyle.Top };
            _sum = new Label { Dock = DockStyle.Top, Height = Theme.Px(34), Font = Theme.Font(10f, FontStyle.Bold), ForeColor = Theme.Text, BackColor = Theme.Surface, Padding = new Padding(Theme.Px(16), 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft };

            _list = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Surface2, ForeColor = Theme.Text, DrawMode = DrawMode.OwnerDrawVariable, IntegralHeight = false };
            _list.MeasureItem += MeasureRow; _list.DrawItem += DrawRow;
            _list.SelectedIndexChanged += (s, e) => Selected();
            Native.UseDarkScrollBars(_list);
            var left = new Panel { Dock = DockStyle.Left, Width = Theme.Px(640), BackColor = Theme.Bg, Padding = new Padding(Theme.Px(14), Theme.Px(10), Theme.Px(6), Theme.Px(6)) };
            var host = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface2, BorderColor = Theme.Border, Radius = 9, Padding = new Padding(Theme.Px(4)) };
            host.Controls.Add(_list); left.Controls.Add(host);

            _map = new RouteLiveMap { Dock = DockStyle.Fill };
            _map.EmptyText = data == null ? Tr("El mapa de la ruta aún no está listo.") : "";
            if (data != null) _map.SetData(data);
            _map.Overlay = DrawMap;
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Px(6), Theme.Px(10), Theme.Px(14), Theme.Px(6)) };
            right.Controls.Add(_map);

            _tl = new Timeline(this) { Dock = DockStyle.Bottom, Height = Theme.Px(230) };

            var foot = new DialogKit.Footer();
            _apply = DialogKit.Primary(Tr("Aplicar sugerencia"), 190);
            _again = DialogKit.Secondary(Tr("Volver a comprobar"), 180);
            var close = DialogKit.Secondary(Tr("Cerrar"), 110);
            _apply.Click += async (s, e) => await DoApply();
            _again.Click += async (s, e) => await DoRecheck();
            close.Click += (s, e) => Close();
            foot.AddRight(close, _again, _apply);

            Controls.Add(right); Controls.Add(left); Controls.Add(_tl); Controls.Add(_sum); Controls.Add(head); Controls.Add(foot);
            Fill();
        }

        void Fill()
        {
            _sum.Text = $"✖ {_r.Bad} {Tr("bloquean")}     ⚠ {_r.Warn} {Tr("avisos")}     ✓ {_r.Issues.Count(i => i.Sev == 0)} {Tr("bien")}     ·     "
                      + string.Format(Tr("{0} salidas en el día, {1} no saldrán (máximo {2} a la vez)"), _r.Trips.Count, _r.Trips.Count(t => t.Dropped), _r.Max);
            _sum.ForeColor = _r.Bad > 0 ? CBad : _r.Warn > 0 ? CWarn : COk;
            _list.BeginUpdate(); _list.Items.Clear(); foreach (var i in _r.Issues) _list.Items.Add(i); _list.EndUpdate();
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            Selected();
        }

        HorarioCheck.Issue Sel => _list.SelectedItem as HorarioCheck.Issue;

        void Selected()
        {
            var i = Sel;
            _apply.Enabled = i != null && i.CanApply && Apply != null;
            _again.Enabled = Recheck != null;
            var pts = new List<(double lat, double lon)>();
            if (i != null)
            {
                pts.AddRange(i.Spots);
                foreach (int e in i.Edges.Distinct()) foreach (var p in EdgeLL(e)) pts.Add(p);
            }
            if (pts.Count > 0 && _map.Data != null) _map.FitLL(pts); else if (_map.Data != null) _map.FitAll();
            _map.Invalidate(); _tl.Invalidate();
        }

        IEnumerable<(double lat, double lon)> EdgeLL(int e)
        {
            if (_g == null || e < 0 || e >= _g.Edges.Length) yield break;
            var E = _g.Edges[e]; if (E.X == null) yield break;
            for (int k = 0; k < E.X.Length; k++) yield return _g.Unproj(E.X[k], E.Y[k]);
        }

        // ── la lista
        string Line1(HorarioCheck.Issue i) => SevText(i.Sev) + (i.Service.Length > 0 ? "  ·  " + i.Service : "") + (i.When.Length > 0 ? "  ·  " + i.When : "");
        string Line2(HorarioCheck.Issue i) => (i.Where.Length > 0 ? i.Where + ": " : "") + i.What;
        void MeasureRow(object sender, MeasureItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _r.Issues.Count) return;
            var i = _r.Issues[e.Index]; int w = Math.Max(100, _list.ClientSize.Width - Theme.Px(40));
            using var f = Theme.Font(9f); using var fb = Theme.Font(9f, FontStyle.Bold);
            int h = Theme.Px(10) + TextRenderer.MeasureText(Line1(i), fb, new Size(w, 999), TextFormatFlags.WordBreak).Height
                  + TextRenderer.MeasureText(Line2(i), f, new Size(w, 999), TextFormatFlags.WordBreak).Height
                  + (i.Fix.Length > 0 ? TextRenderer.MeasureText("→ " + i.Fix, f, new Size(w, 999), TextFormatFlags.WordBreak).Height : 0) + Theme.Px(8);
            e.ItemHeight = Math.Min(255, h);
        }
        void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _r.Issues.Count) return;
            var i = _r.Issues[e.Index]; var g = e.Graphics; var rc = e.Bounds;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(sel ? Color.FromArgb(46, 58, 70) : Theme.Surface2)) g.FillRectangle(bg, rc);
            using (var bar = new SolidBrush(SevCol(i.Sev))) g.FillRectangle(bar, rc.X + Theme.Px(4), rc.Y + Theme.Px(6), Theme.Px(4), rc.Height - Theme.Px(12));
            using (var sep = new Pen(Color.FromArgb(40, 255, 255, 255))) g.DrawLine(sep, rc.X, rc.Bottom - 1, rc.Right, rc.Bottom - 1);
            int x = rc.X + Theme.Px(18), w = rc.Width - Theme.Px(26), y = rc.Y + Theme.Px(5);
            using var f = Theme.Font(9f); using var fb = Theme.Font(9f, FontStyle.Bold);
            var fl = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int h1 = TextRenderer.MeasureText(Line1(i), fb, new Size(w, 999), TextFormatFlags.WordBreak).Height;
            TextRenderer.DrawText(g, Line1(i), fb, new Rectangle(x, y, w, h1), SevCol(i.Sev), fl); y += h1;
            int h2 = TextRenderer.MeasureText(Line2(i), f, new Size(w, 999), TextFormatFlags.WordBreak).Height;
            TextRenderer.DrawText(g, Line2(i), f, new Rectangle(x, y, w, h2), Theme.Text, fl); y += h2;
            if (i.Fix.Length > 0)
                TextRenderer.DrawText(g, "→ " + i.Fix + (i.CanApply ? "  (" + Tr("se puede aplicar") + ")" : ""), f, new Rectangle(x, y, w, rc.Bottom - y), Color.FromArgb(144, 202, 249), fl);
        }

        // ── el mapa: el sitio del problema elegido
        void DrawMap(Graphics g, Func<double, double, PointF> scr)
        {
            var i = Sel; if (i == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var col = SevCol(i.Sev);
            using (var pen = new Pen(Color.FromArgb(230, col), 6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                foreach (int e in i.Edges.Distinct())
                {
                    var pts = EdgeLL(e).Select(p => scr(p.lat, p.lon)).ToArray();
                    if (pts.Length > 1) g.DrawLines(pen, pts);
                }
            using (var b = new SolidBrush(Color.FromArgb(90, col)))
            using (var p = new Pen(col, 2.5f))
                foreach (var s in i.Spots.Take(12))
                {
                    var q = scr(s.lat, s.lon);
                    g.FillEllipse(b, q.X - 14, q.Y - 14, 28, 28); g.DrawEllipse(p, q.X - 14, q.Y - 14, 28, 28);
                }
        }

        // ── aplicar y volver a comprobar
        async Task DoApply()
        {
            var i = Sel; if (i == null || !i.CanApply || Apply == null) return;
            _apply.Enabled = false;
            try { if (await Apply(i) && Recheck != null) await DoRecheck(); }
            finally { if (!IsDisposed) Selected(); }
        }
        async Task DoRecheck()
        {
            if (Recheck == null) return;
            _again.Enabled = false; _sum.Text = Tr("Comprobando…");
            try { var r = await Recheck(); if (IsDisposed || r == null) return; _r = r; Fill(); }
            finally { if (!IsDisposed) _again.Enabled = true; }
        }

        // ── la línea de tiempo del día
        sealed class Timeline : Control
        {
            readonly HorarioCheckForm _f;
            public Timeline(HorarioCheckForm f) { _f = f; DoubleBuffered = true; ResizeRedraw = true; BackColor = Theme.Surface; }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; var r = _f._r; g.Clear(BackColor);
                if (r == null || r.Services == null) return;
                int t0 = 4 * 60, t1 = 24 * 60;   // de 04:00 a 24:00
                int left = Theme.Px(170), right = Width - Theme.Px(14), top = Theme.Px(30), bandH = Theme.Px(34);
                float X(int min) => left + (right - left) * (min - t0) / (float)(t1 - t0);
                using var f = Theme.Font(8f); using var fb = Theme.Font(8.25f, FontStyle.Bold);
                // horas
                using (var grid = new Pen(Color.FromArgb(40, 255, 255, 255)))
                    for (int h = 4; h <= 24; h += 2)
                    {
                        float x = X(h * 60); g.DrawLine(grid, x, top - Theme.Px(4), x, Height - Theme.Px(6));
                        TextRenderer.DrawText(g, $"{h % 24:00}:00", f, new Point((int)x - Theme.Px(14), Theme.Px(8)), Theme.Subtle);
                    }
                // cuántos a la vez frente al máximo
                TextRenderer.DrawText(g, Tr("Trenes IA a la vez"), fb, new Point(Theme.Px(12), top + Theme.Px(8)), Theme.Text);
                int peak = Math.Max(Math.Max(1, r.Max), r.Active.Max());
                for (int m = t0; m < t1 && m < 1440; m++)
                {
                    int n = r.Active[m]; if (n == 0) continue;
                    float h = bandH * n / (float)peak;
                    using var b = new SolidBrush(n >= r.Max && r.Max > 0 ? Color.FromArgb(200, CWarn) : Color.FromArgb(160, 120, 170, 220));
                    g.FillRectangle(b, X(m), top + bandH - h, Math.Max(1, X(m + 1) - X(m)), h);
                }
                if (r.Max > 0) using (var mp = new Pen(CBad, 1.2f) { DashStyle = DashStyle.Dash }) { float y = top + bandH - bandH * r.Max / (float)peak; g.DrawLine(mp, left, y, right, y); TextRenderer.DrawText(g, Tr("máximo") + " " + r.Max, f, new Point(right - Theme.Px(70), (int)y - Theme.Px(14)), CBad); }
                // un renglón por servicio
                var sel = _f.Sel;
                int n0 = r.Services.Count, y0 = top + bandH + Theme.Px(8);
                float rowH = Math.Max(Theme.Px(8), Math.Min(Theme.Px(16), (Height - y0 - Theme.Px(24)) / (float)Math.Max(1, n0)));
                for (int si = 0; si < n0; si++)
                {
                    float y = y0 + si * rowH;
                    var col = Palette[si % Palette.Length];
                    bool mine = sel != null && (sel.Svc == si || sel.ShiftSvc == si || sel.EverySvc == si || (sel.Service ?? "").Contains(r.Services[si].Name));
                    if (mine) using (var hb = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillRectangle(hb, 0, y, Width, rowH);
                    TextRenderer.DrawText(g, r.Services[si].Name, mine ? fb : f, new Rectangle(Theme.Px(12), (int)y, left - Theme.Px(16), (int)rowH), mine ? Theme.Text : Theme.Subtle, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    foreach (var t in r.Trips.Where(t => t.Svc == si))
                    {
                        float a = X(Math.Max(t0, t.Appear / 60)), b = X(Math.Min(t1, t.End / 60));
                        if (b <= left || a >= right) continue;
                        var rect = new RectangleF(a, y + 2, Math.Max(2, b - a), rowH - 4);
                        if (t.Dropped) using (var p = new Pen(Color.FromArgb(120, 150, 156, 164))) g.DrawRectangle(p, rect.X, rect.Y, rect.Width, rect.Height);
                        else using (var bb = new SolidBrush(Color.FromArgb(mine ? 220 : 130, col))) g.FillRectangle(bb, rect);
                    }
                }
                // cuándo pasa el problema elegido
                if (sel != null)
                    using (var p = new Pen(SevCol(sel.Sev), 2f))
                        foreach (int m in sel.Minutes.Distinct()) { float x = X(m); if (x >= left && x <= right) g.DrawLine(p, x, top - Theme.Px(2), x, Height - Theme.Px(4)); }
                TextRenderer.DrawText(g, Tr("Rectángulo vacío: esa salida no se hará (ya hay el máximo de trenes IA)."), f, new Point(Theme.Px(12), Height - Theme.Px(18)), Theme.Subtle);
            }
        }
    }
}
