// Cabecera «TU VIAJE» de Exploración, en forma de BILLETE: salida y llegada unidas por la línea del recorrido (con
// el tren, los km y cuántos recorridos hay entre las dos), un botón para intercambiarlas y otro para el mapa; debajo,
// una fila de chips con la hora, la estación del año, el clima y el modo actividad. Un clic en un chip abre su selector.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SysPath = System.IO.Path;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        TripRouteLine _tripLine;
        TripIconButton _tripSwap;
        TripChip _chipHour, _chipSeason, _chipWeather, _chipActivity;
        readonly Dictionary<string, double> _pathKmCache = new(StringComparer.OrdinalIgnoreCase);

        static readonly string[] SeasonKinds = { "spring", "summer", "autumn", "winter" };
        static readonly string[] WeatherKinds = { "clear", "snow", "rain" };
        string[] SeasonNames => new[] { Tr("Primavera"), Tr("Verano"), Tr("Otoño"), Tr("Invierno") };
        string[] WeatherNames => new[] { Tr("Despejado"), Tr("Nieve"), Tr("Lluvia") };

        Card BuildTripHeader()
        {
            var top = new Card { Dock = DockStyle.Top, Height = 112, Fill = TripFill, Radius = 14, Padding = new Padding(16, 8, 16, 8) };
            _tripCap = new Label { Text = Tr("TU VIAJE"), Dock = DockStyle.Top, Height = 18, ForeColor = Theme.AccentHi, BackColor = TripFill, Font = Theme.Font(8f, FontStyle.Bold) };

            _cboStart = NewCombo(); _cboStart.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboStart.SelectedIndexChanged += (s, e) => { OnStartChanged(); UpdateTripMeta(); };
            _cboEnd = NewCombo(); _cboEnd.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboEnd.SelectedIndexChanged += (s, e) => { UpdateStatus(); UpdateTripMeta(); };
            _cboStart.Dock = _cboEnd.Dock = DockStyle.None;
            _cboStart.Anchor = _cboEnd.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _cboStart.Margin = _cboEnd.Margin = new Padding(0);

            // salida · línea del recorrido · llegada · intercambiar · mapa
            var od = new TableLayoutPanel { Dock = DockStyle.Top, Height = 42, ColumnCount = 7, RowCount = 1, BackColor = TripFill, Margin = new Padding(0) };
            od.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
            od.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            od.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            od.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
            od.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            od.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
            od.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            od.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var tip = new ToolTip();
            var dotStart = new TripEndMark { Kind = 0, Dock = DockStyle.Fill, BackColor = TripFill, Margin = new Padding(0) };
            var dotEnd = new TripEndMark { Kind = 1, Dock = DockStyle.Fill, BackColor = TripFill, Margin = new Padding(0, 0, 0, 0) };
            tip.SetToolTip(dotStart, Tr("Salida")); tip.SetToolTip(dotEnd, Tr("Llegada"));
            _tripLine = new TripRouteLine { Dock = DockStyle.Fill, BackColor = TripFill, Margin = new Padding(6, 0, 6, 0) };
            _tripSwap = new TripIconButton { Anchor = AnchorStyles.None, Size = new Size(36, 32), BackColor = TripFill, Margin = new Padding(0) };
            _tripSwap.Click += (s, e) => SwapTrip();
            tip.SetToolTip(_tripSwap, Tr("Intercambiar salida y llegada"));
            var btnMap = new RoundButton { Text = Tr("Mapa"), GlyphKind = "map", Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 32, Radius = 9, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 9.5f, Margin = new Padding(4, 0, 0, 0) };
            btnMap.Click += (s, e) => OpenPathMap();
            tip.SetToolTip(btnMap, Tr("Ver mapa del recorrido"));
            od.Controls.Add(dotStart, 0, 0); od.Controls.Add(_cboStart, 1, 0); od.Controls.Add(_tripLine, 2, 0);
            od.Controls.Add(dotEnd, 3, 0); od.Controls.Add(_cboEnd, 4, 0); od.Controls.Add(_tripSwap, 5, 0); od.Controls.Add(btnMap, 6, 0);

            // condiciones: la hora y los selectores se siguen usando (al conducir), pero viven en los desplegables de los chips
            _hourSlider = new HourSlider { Minutes = 12 * 60, Height = 34 };
            _hourSlider.Changed += () => { UpdateStatus(); UpdateTripChips(); };
            _segSeason = new Segmented(SeasonKinds, SeasonNames) { SelectedIndex = 1, CardStyle = true, Height = 58 };
            _segWeather = new Segmented(WeatherKinds, WeatherNames) { SelectedIndex = 0, CardStyle = true, Height = 58 };
            _chkExploreActivity = MakeCheck("Explorar en modo actividad");

            var chips = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = TripFill, Margin = new Padding(0), Padding = new Padding(22, 0, 0, 0) };
            _chipHour = new TripChip { Glyph = "clock", Margin = new Padding(0, 1, 8, 1) };
            _chipSeason = new TripChip { Margin = new Padding(0, 1, 8, 1) };
            _chipWeather = new TripChip { Margin = new Padding(0, 1, 8, 1) };
            _chipActivity = new TripChip { Glyph = "activity", Toggle = true, Text = Tr("Modo actividad"), Margin = new Padding(0, 1, 8, 1) };
            tip.SetToolTip(_chipHour, Tr("Hora de salida"));
            tip.SetToolTip(_chipSeason, Tr("Estación del año"));
            tip.SetToolTip(_chipWeather, Tr("Clima"));
            tip.SetToolTip(_chipActivity, Tr("Explorar en modo actividad"));
            _chipHour.Click += (s, e) => ShowHourPicker(_chipHour);
            _chipSeason.Click += (s, e) => ShowSegPicker(_chipSeason, _segSeason, 380);
            _chipWeather.Click += (s, e) => ShowSegPicker(_chipWeather, _segWeather, 290);
            _chipActivity.Click += (s, e) => { _chkExploreActivity.Checked = !_chkExploreActivity.Checked; UpdateTripChips(); };
            chips.Controls.AddRange(new Control[] { _chipHour, _chipSeason, _chipWeather, _chipActivity });
            var hint = new Label
            {
                Text = Tr("Clic en hora, estación o clima para cambiarlos"), Dock = DockStyle.Right, AutoSize = false, Width = 330,
                ForeColor = Theme.Subtle, BackColor = TripFill, Font = Theme.Font(8.5f), TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true
            };
            var condRow = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = TripFill, Margin = new Padding(0) };
            condRow.Controls.Add(chips); condRow.Controls.Add(hint);
            // con la ventana estrecha, el texto de ayuda deja sitio a los chips
            condRow.Resize += (s, e) => hint.Visible = condRow.Width - 330 > chips.Controls.Cast<Control>().Sum(c => c.Width + c.Margin.Horizontal) + 30;
            var gap = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = TripFill };

            top.Controls.Add(condRow); top.Controls.Add(gap); top.Controls.Add(od); top.Controls.Add(_tripCap);
            UpdateTripChips();
            return top;
        }

        // ---------------- Horarios: estación y clima con los mismos chips que Exploración ----------------
        TripChip _chipTTSeason, _chipTTWeather;

        Control BuildTTCondChips()
        {
            var row = new Panel { Dock = DockStyle.Bottom, Height = Theme.Px(40), BackColor = Theme.Surface, Margin = new Padding(0) };
            _chipTTSeason = new TripChip();
            _chipTTWeather = new TripChip();
            var tip = new ToolTip();
            tip.SetToolTip(_chipTTSeason, Tr("Estación del año"));
            tip.SetToolTip(_chipTTWeather, Tr("Clima"));
            _chipTTSeason.Click += (s, e) => ShowSegPicker(_chipTTSeason, _segTTSeason, 380, UpdateTTChips);
            _chipTTWeather.Click += (s, e) => ShowSegPicker(_chipTTWeather, _segTTWeather, 290, UpdateTTChips);
            row.Controls.Add(_chipTTSeason); row.Controls.Add(_chipTTWeather);
            // los dos chips juntos, centrados en la columna (y otra vez si cambian de ancho al elegir)
            void Center()
            {
                int gap = Theme.Px(8), total = _chipTTSeason.Width + gap + _chipTTWeather.Width;
                int x = Math.Max(0, (row.ClientSize.Width - total) / 2), y = Theme.Px(6) + Math.Max(0, (row.ClientSize.Height - Theme.Px(6) - _chipTTSeason.Height) / 2);
                _chipTTSeason.Location = new Point(x, y);
                _chipTTWeather.Location = new Point(x + _chipTTSeason.Width + gap, y);
            }
            row.Resize += (s, e) => Center();
            _chipTTSeason.SizeChanged += (s, e) => Center();
            _chipTTWeather.SizeChanged += (s, e) => Center();
            UpdateTTChips();
            Center();
            return row;
        }

        void UpdateTTChips()
        {
            if (_chipTTSeason == null) return;
            int s = Math.Max(0, Math.Min(3, _segTTSeason.SelectedIndex)), w = Math.Max(0, Math.Min(2, _segTTWeather.SelectedIndex));
            _chipTTSeason.Glyph = SeasonKinds[s]; _chipTTSeason.Text = SeasonNames[s];
            _chipTTWeather.Glyph = WeatherKinds[w]; _chipTTWeather.Text = WeatherNames[w];
            _chipTTSeason.Fit(); _chipTTWeather.Fit();
        }

        void UpdateTripChips()
        {
            if (_chipHour == null) return;
            _chipHour.Text = _hourSlider.TimeText;
            int s = Math.Max(0, Math.Min(3, _segSeason.SelectedIndex)), w = Math.Max(0, Math.Min(2, _segWeather.SelectedIndex));
            _chipSeason.Glyph = SeasonKinds[s]; _chipSeason.Text = SeasonNames[s];
            _chipWeather.Glyph = WeatherKinds[w]; _chipWeather.Text = WeatherNames[w];
            _chipActivity.Active = _chkExploreActivity.Checked;
            foreach (var c in new[] { _chipHour, _chipSeason, _chipWeather, _chipActivity }) c.Fit();
        }

        // La línea del billete: km del recorrido elegido, cuántos hay entre las dos estaciones y si hay vuelta.
        void UpdateTripLine()
        {
            if (_tripLine == null) return;
            var p = CurrentPath();
            string st = _cboStart?.SelectedItem as string ?? "", en = _cboEnd?.SelectedItem as string ?? "";
            if (p == null || _pathsAll.Count == 0)
            {
                _tripLine.Km = 0; _tripLine.Count = 0; _tripLine.Empty = _pathsAll.Count == 0 ? Tr("Esta ruta no tiene recorridos.") : "";
            }
            else
            {
                if (!_pathKmCache.TryGetValue(p.FilePath ?? "", out double km)) { km = EstimatePathKm(p.FilePath); _pathKmCache[p.FilePath ?? ""] = km; }
                _tripLine.Km = km; _tripLine.Empty = null;
                _tripLine.Count = _pathsAll.Count(x => (x.Start ?? "") == st && (x.End ?? "") == en);
                int from = _pathsAll.Count(x => (x.Start ?? "") == st);
                _tripLine.Tip = string.Format(Tr("Recorrido {0}  ·  {1} desde {2}"), SysPath.GetFileNameWithoutExtension(p.FilePath), Plural(from, "{0} recorrido sale", "{0} recorridos salen"), p.Start);
            }
            _tripLine.Invalidate();
            if (_tripSwap != null)
            {
                _tripSwap.Enabled = st.Length > 0 && st != en && _pathsAll.Any(x => (x.Start ?? "") == en && (x.End ?? "") == st);
                _tripSwap.Invalidate();
            }
        }

        // Intercambia salida y llegada (solo si hay un recorrido en el otro sentido).
        void SwapTrip()
        {
            string st = _cboStart.SelectedItem as string ?? "", en = _cboEnd.SelectedItem as string ?? "";
            if (!_pathsAll.Any(x => (x.Start ?? "") == en && (x.End ?? "") == st)) return;
            int i = _cboStart.Items.IndexOf(en);
            if (i < 0) return;
            _cboStart.SelectedIndex = i;   // rellena las llegadas (OnStartChanged)
            int j = _cboEnd.Items.IndexOf(st);
            if (j >= 0) _cboEnd.SelectedIndex = j;
        }

        // ---------------- desplegables de los chips ----------------
        ToolStripDropDown _tripPop;

        ToolStripDropDown NewTripPopup(Control body)
        {
            try { _tripPop?.Close(); _tripPop?.Dispose(); } catch { }
            var host = new ToolStripControlHost(body) { AutoSize = false, Size = body.Size, Margin = new Padding(0), Padding = new Padding(0) };
            var pop = new ToolStripDropDown { Padding = new Padding(10), BackColor = Theme.Surface, Renderer = new TripPopRenderer(), DropShadowEnabled = true, AutoClose = true };
            pop.Items.Add(host);
            pop.HandleCreated += (s, e) => { try { int v = 2; DwmSetWindowAttribute(pop.Handle, 33, ref v, 4); } catch { } };
            _tripPop = pop;
            return pop;
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void ShowHourPicker(Control anchor)
        {
            var body = new Panel { Size = new Size(330, 76), BackColor = Theme.Surface };
            var presets = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(0) };
            foreach (var (t, min) in new[] { ("06:30", 390), ("12:00", 720), ("19:45", 1185), ("23:00", 1380) })
            {
                var b = BankChip(t, false); b.Height = 26; b.Margin = new Padding(0, 4, 6, 2); b.BaseColor = Theme.Surface2;
                b.Click += (s, e) => { _hourSlider.Minutes = min; UpdateStatus(); UpdateTripChips(); _tripPop?.Close(); };
                presets.Controls.Add(b);
            }
            _hourSlider.Dock = DockStyle.Top;
            body.Controls.Add(presets); body.Controls.Add(_hourSlider);
            var pop = NewTripPopup(body);
            pop.Closed += (s, e) => { body.Controls.Remove(_hourSlider); };
            pop.Show(anchor, new Point(0, anchor.Height + 4));
        }

        void ShowSegPicker(Control anchor, Segmented seg, int width, Action after = null)
        {
            seg.Dock = DockStyle.None; seg.Size = new Size(width, 58);
            var body = new Panel { Size = seg.Size, BackColor = Theme.Surface };
            body.Controls.Add(seg);
            Action changed = null;
            changed = () => { UpdateStatus(); UpdateTripChips(); after?.Invoke(); _tripPop?.Close(); };
            seg.Changed += changed;
            var pop = NewTripPopup(body);
            pop.Closed += (s, e) => { seg.Changed -= changed; body.Controls.Remove(seg); };
            pop.Show(anchor, new Point(0, anchor.Height + 4));
        }

        sealed class TripPopRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { using var b = new SolidBrush(Theme.Surface); e.Graphics.FillRectangle(b, e.AffectedBounds); }
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using var p = new Pen(Theme.Surface2);
                e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }
        }

        // ---------------- controles dibujados ----------------

        // Marca de salida (punto verde) o de llegada (chincheta naranja).
        sealed class TripEndMark : Control
        {
            public int Kind;
            public TripEndMark() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = Width / 2f, cy = Height / 2f;
                if (Kind == 0)
                {
                    using (var b = new SolidBrush(Color.FromArgb(60, 102, 187, 106))) g.FillEllipse(b, cx - 9, cy - 9, 18, 18);
                    using (var b = new SolidBrush(Color.FromArgb(102, 187, 106))) g.FillEllipse(b, cx - 5, cy - 5, 10, 10);
                }
                else
                {
                    var c = Color.FromArgb(255, 167, 89);
                    using var path = new GraphicsPath();
                    path.AddArc(cx - 6, cy - 9, 12, 12, 140, 260);
                    path.AddLine(cx + 4.6f, cy + 0.9f, cx, cy + 8);
                    path.CloseFigure();
                    using (var b = new SolidBrush(c)) g.FillPath(b, path);
                    using (var b = new SolidBrush(TripFill)) g.FillEllipse(b, cx - 2.4f, cy - 5.4f, 4.8f, 4.8f);
                }
            }
        }

        // La línea del billete: discontinua de la salida a la llegada, con el tren en medio y los datos debajo.
        sealed class TripRouteLine : Control
        {
            public double Km; public int Count; public string Empty; public string Tip = "";
            readonly Font _f = Theme.Font(7.75f, FontStyle.Bold);
            readonly ToolTip _tt = new ToolTip();
            string _shownTip;
            public TripRouteLine() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
            protected override void Dispose(bool disposing) { if (disposing) { _f.Dispose(); _tt.Dispose(); } base.Dispose(disposing); }
            protected override void OnPaint(PaintEventArgs e)
            {
                if (_shownTip != Tip) { _shownTip = Tip; _tt.SetToolTip(this, Tip); }
                var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
                int cy = Height / 2 - 5;
                var acc = Color.FromArgb(102, 187, 106);
                using (var p = new Pen(Color.FromArgb(150, acc), 2f) { DashPattern = new[] { 3f, 3f } }) g.DrawLine(p, 2, cy, Width - 3, cy);
                using (var b = new SolidBrush(acc)) { g.FillEllipse(b, 0, cy - 3, 6, 6); }
                using (var b = new SolidBrush(Color.FromArgb(255, 167, 89))) g.FillEllipse(b, Width - 7, cy - 3, 6, 6);
                // el tren, sobre una pastilla del color de la tarjeta (tapa la línea)
                int tw = 30, th = 20;
                var tr = new Rectangle(Width / 2 - tw / 2, cy - th / 2, tw, th);
                using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, tr.X - 2, tr.Y, tr.Width + 4, tr.Height);
                Theme.FillRound(g, tr, 9, Color.FromArgb(52, 74, 58));
                Glyphs.Draw(g, "train", new Rectangle(tr.X + tw / 2 - 8, tr.Y + th / 2 - 8, 16, 16), Theme.Text);
                string txt = Empty ?? (Km > 0 ? Km.ToString("N1", System.Globalization.CultureInfo.GetCultureInfo(I18n.English ? "en-GB" : "es-ES")) + " km" : "")
                             + (Empty == null && Count > 0 ? (Km > 0 ? "  ·  " : "") + string.Format(I18n.T(Count == 1 ? "{0} recorrido" : "{0} recorridos"), Count) : "");
                TextRenderer.DrawText(g, txt, _f, new Rectangle(0, cy + 11, Width, Height - cy - 11), Theme.Subtle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }

        // Botón de intercambiar (dos flechas opuestas).
        sealed class TripIconButton : Control
        {
            bool _hover;
            public TripIconButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Cursor = Cursors.Hand; }
            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                Theme.FillRound(g, r, 9, Enabled && _hover ? Theme.SurfaceHi : Theme.Surface2);
                var c = Enabled ? Theme.Text : Color.FromArgb(110, 116, 120);
                float cx = Width / 2f, cy = Height / 2f;
                using var p = new Pen(c, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLine(p, cx - 8, cy - 4, cx + 8, cy - 4); g.DrawLines(p, new[] { new PointF(cx + 4, cy - 8), new PointF(cx + 8, cy - 4), new PointF(cx + 4, cy) });
                g.DrawLine(p, cx + 8, cy + 4, cx - 8, cy + 4); g.DrawLines(p, new[] { new PointF(cx - 4, cy), new PointF(cx - 8, cy + 4), new PointF(cx - 4, cy + 8) });
            }
        }

        // Chip de condición: icono, texto y ▾ (o, si es de activar/desactivar, la casilla).
        sealed class TripChip : Control
        {
            public string Glyph; public bool Toggle, Active;
            bool _hover;
            readonly Font _f = Theme.Font(9f, FontStyle.Bold);
            public TripChip() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Cursor = Cursors.Hand; Height = 30; }
            protected override void Dispose(bool disposing) { if (disposing) _f.Dispose(); base.Dispose(disposing); }
            public void Fit() { Width = 12 + 18 + 6 + TextRenderer.MeasureText(Text ?? "", _f, Size.Empty, TextFormatFlags.NoPadding).Width + 8 + 14 + 8; Invalidate(); }
            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Parent?.BackColor ?? TripFill); g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                bool on = Toggle && Active;
                Theme.FillRound(g, r, 15, on ? Color.FromArgb(44, 62, 48) : _hover ? Color.FromArgb(62, 70, 66) : Color.FromArgb(52, 60, 55));
                if (on || _hover) Theme.DrawRoundBorder(g, r, 15, on ? Theme.Accent : Color.FromArgb(90, 100, 95), 1.3f);
                int x = 12;
                if (!string.IsNullOrEmpty(Glyph)) Glyphs.Draw(g, Glyph, new Rectangle(x, Height / 2 - 9, 18, 18), Theme.Text);
                x += 24;
                int tw = TextRenderer.MeasureText(Text ?? "", _f, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g, Text ?? "", _f, new Rectangle(x, 0, tw + 2, Height), on || !Toggle ? Theme.Text : Theme.Subtle, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                x += tw + 8;
                float cy = Height / 2f;
                if (Toggle)
                {
                    var box = new Rectangle(x, (int)cy - 7, 14, 14);
                    Theme.FillRound(g, box, 4, on ? Theme.Accent : Color.FromArgb(70, 78, 74));
                    if (on) using (var p = new Pen(Color.White, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLines(p, new[] { new PointF(box.X + 3.5f, cy), new PointF(box.X + 6, cy + 3), new PointF(box.X + 10.5f, cy - 3) });
                }
                else
                    using (var p = new Pen(Theme.Subtle, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(p, new[] { new PointF(x + 3, cy - 2), new PointF(x + 7, cy + 2), new PointF(x + 11, cy - 2) });
            }
        }
    }
}
