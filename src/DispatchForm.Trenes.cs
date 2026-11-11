// (2026-11-09) La columna derecha del puesto, en dos: arriba, los trenes de la dependencia que se ve (en el plano general, los
// de toda la sala) y, abajo, el chat de la sala (DispatchForm.Chat.cs). De cada tren: su número y su maquinista, el tipo de
// servicio, el destino, la velocidad y su composición en 2D (si su .con está en este equipo; el número, el tipo, el destino
// y el .con los manda el SelectOR de cada maquinista a la sala: SwitchPilot.SetTrainNo).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class DispatchForm
    {
        // la composición 2D de un .con por su nombre (sin extensión); null si no se puede (la pone MainMenuForm.PuestoMando.cs)
        public Func<string, Task<Bitmap>> ConsistStrip;

        Panel _rightCol, _trainsBox, _trainsSep; Label _trainsHead; TrainList _trainList;
        readonly Dictionary<string, Bitmap> _strips = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _stripBusy = new(StringComparer.OrdinalIgnoreCase);
        // (2026-11-09) el tipo y el destino de cada maquinista, para avisar si los cambia durante la conducción
        readonly Dictionary<string, (string type, string dest, string stops)> _lastNos = new(StringComparer.Ordinal);
        readonly Dictionary<string, (DateTime at, string before)> _noChanged = new(StringComparer.Ordinal);
        static string NoText(string type, string dest)
        {
            string ty = type.Length > 0 ? Tr(MainMenuForm.SalaServiceName(type)) : "";
            return ty + (dest.Length > 0 ? (ty.Length > 0 ? " → " : "→ ") + dest : "");
        }

        sealed class TrainRow
        {
            public string Name, Title, Sub, Speed, Con, Changed, Stops; public bool Player, Ai, Loose; public double Kmh;
        }

        // (2026-11-09) clic en la tarjeta de un tren: se localiza (el plano, centrado en él, y un aro que lo señala); doble
        // clic: se sigue (otro doble clic o arrastrar el plano lo dejan)
        string _followTrain;
        string _locateTrain; DateTime _locateUntil;
        void FollowToggle(string name)
        {
            _followTrain = _followTrain == name ? null : name;
            if (_followTrain != null) FollowTick(true);
            _trainList?.Invalidate(); _canvas.Invalidate();
        }
        void LocateTrain(string name)
        {
            if (_followTrain != null && _followTrain != name) _followTrain = null;   // (otro tren: deja de seguir el de antes)
            CenterOnTrain(name, true);
            _locateTrain = name; _locateUntil = DateTime.UtcNow.AddSeconds(2.5);
            _trainList?.Invalidate(); _canvas.Invalidate();
        }

        // el aro que señala el tren localizado (late durante 2,5 s)
        void DrawLocate(Graphics g)
        {
            if (_locateTrain == null || DateTime.UtcNow > _locateUntil) return;
            var pt = TrainScreen(_locateTrain); if (pt == null) return;
            double left = (_locateUntil - DateTime.UtcNow).TotalSeconds, ph = (2.5 - left) * 2.2 % 1.0;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float r = Theme.Px(14) + (float)ph * Theme.Px(26);
            int a = (int)(230 * (1 - ph) * Math.Min(1, left / 0.6));
            using var pen = new Pen(Color.FromArgb(Math.Max(0, a), 102, 197, 106), 3f);
            g.DrawEllipse(pen, pt.Value.X - r, pt.Value.Y - r, 2 * r, 2 * r);
            using var pen2 = new Pen(Color.FromArgb(Math.Max(0, (int)(200 * Math.Min(1, left / 0.6))), 102, 197, 106), 2f);
            float r2 = Theme.Px(12); g.DrawEllipse(pen2, pt.Value.X - r2, pt.Value.Y - r2, 2 * r2, 2 * r2);
        }

        // dónde se ve un tren en el plano (null si no se ve)
        PointF? TrainScreen(string name)
        {
            var p = _pilot; if (p == null || !p.Ready) return null;
            var t = p.Trains.FirstOrDefault(q => q.name == name); if (t.name == null) return null;
            if (!TrainLL(t.name, t.tx, t.tz, t.x, t.z, out double la, out double lo)) return null;
            try
            {
                if (Board && _board != null) { var (x, y) = _board.G.Proj(la, lo); var w = _board.W(x, y); return new PointF(_bOrigin.X + w.X * _bZoom, _bOrigin.Y + w.Y * _bZoom); }
                if (!Overview && _cur != null && _cur.Map(la, lo, out var q)) return new PointF(_origin.X + q.X * _zoom, _origin.Y + q.Y * _zoom);
            }
            catch { }
            return null;
        }

        // (al seguirlo, cada vez que se repinta) el plano, centrado en el tren
        void FollowTick(bool first = false)
        {
            if (_followTrain == null) return;
            if (!CenterOnTrain(_followTrain, first)) { _followTrain = null; _trainList?.Invalidate(); }   // (ya no está en la sala)
        }

        // el plano, centrado en un tren; si está fuera de la dependencia que se ve, a la suya. false si ya no está en la sala
        bool CenterOnTrain(string name, bool zoomIn)
        {
            var p = _pilot; if (p == null || !p.Ready) return true;
            var t = p.Trains.FirstOrDefault(q => q.name == name);
            if (t.name == null) return false;
            if (!TrainLL(t.name, t.tx, t.tz, t.x, t.z, out double la, out double lo)) return true;
            try
            {
                // (2026-11-09) viendo una banda: si sale de ella, a la suya
                if (_curBand > 0 && !BandZones(_curBand).Any(q => TrainInZone(t.name, t.tx, t.tz, t.x, t.z, q)))
                {
                    string zz = _zones?.Select(q => q.Name).FirstOrDefault(q => TrainInZone(t.name, t.tx, t.tz, t.x, t.z, q));
                    int nb = BandOfZone(zz);
                    string keep = _followTrain;
                    if (nb > 0) Select(Bandas.Label(nb));
                    _followTrain = keep;
                }
                else if (_curBand == 0 && _curZone != null && !DepZones(_curZone).Any(q => TrainInZone(t.name, t.tx, t.tz, t.x, t.z, q)))
                {
                    string z = _zones?.Select(q => q.Name).FirstOrDefault(q => !IsLineZone(q) && TrainInZone(t.name, t.tx, t.tz, t.x, t.z, q))
                            ?? _zones?.Select(q => q.Name).FirstOrDefault(q => TrainInZone(t.name, t.tx, t.tz, t.x, t.z, q));
                    string keep = _followTrain;
                    if (z != null && Listed(z)) Select(z); else Select(General);
                    _followTrain = keep;   // (cambiar de dependencia no lo deja de seguir)
                }
                float cx = _canvas.Width / 2f, cy = _canvas.Height / 2f;
                if (Board && _board != null)
                {
                    var (x, y) = _board.G.Proj(la, lo); var w = _board.W(x, y);
                    if (zoomIn && _bZoom < 0.6f) _bZoom = 1.2f;   // (de lejos no se ve: se acerca)
                    _bOrigin = new PointF(cx - w.X * _bZoom, cy - w.Y * _bZoom); _bFitted = true;
                }
                else if (!Overview && _cur != null && _cur.Map(la, lo, out var q))
                {
                    _origin = new PointF(cx - q.X * _zoom, cy - q.Y * _zoom); _fitted = true;
                }
            }
            catch { }
            _canvas.Invalidate();
            return true;
        }

        Panel BuildRightColumn()
        {
            InitSmooth();   // (2026-11-09) trenes sin saltos (DispatchForm.Suave.cs)
            _canvas.Paint += (s, e) => DrawLocate(e.Graphics);   // (encima de todo: el aro del tren localizado)
            _canvas.MouseMove += (s, e) => { if (_dragging && _followTrain != null) { _followTrain = null; _trainList?.Invalidate(); } };   // (moverlo a mano: deja de seguir)
            _rightCol = new Panel { Dock = DockStyle.Right, Width = Theme.Px(300), BackColor = Theme.BgSidebar };
            var chat = BuildChat(); chat.Dock = DockStyle.Fill;
            _trainsBox = new Panel { Dock = DockStyle.Top, Height = Theme.Px(300), BackColor = Theme.BgSidebar, Padding = new Padding(Theme.Px(8), Theme.Px(6), Theme.Px(8), Theme.Px(6)) };
            _trainsHead = new Label { Dock = DockStyle.Top, Height = Theme.Px(22), Font = Theme.Font(8.5f, FontStyle.Bold), ForeColor = Theme.Subtle, BackColor = Theme.BgSidebar, AutoEllipsis = true, Text = Tr("TRENES EN LA SALA") };
            _trainList = new TrainList(this) { Dock = DockStyle.Fill };
            new ToolTip().SetToolTip(_trainList, Tr("Clic: localizar el tren · doble clic: seguirlo"));
            _trainsBox.Controls.Add(_trainList); _trainsBox.Controls.Add(_trainsHead);
            _trainsSep = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(52, 60, 70) };
            _rightCol.Controls.Add(chat); _rightCol.Controls.Add(_trainsSep); _rightCol.Controls.Add(_trainsBox);
            _rightCol.Resize += (s, e) => _trainsBox.Height = Math.Max(Theme.Px(130), _rightCol.Height / 2);
            _list.SelectedIndexChanged += (s, e) => { try { BeginInvoke((Action)TrainsTick); } catch { } };
            FormClosed += (s, e) => { foreach (var b in _strips.Values) b?.Dispose(); _strips.Clear(); };
            return _rightCol;
        }

        // (cada segundo) los trenes de la dependencia que se ve
        void TrainsTick()
        {
            if (_curBand > 0 && _schema) BuildBandSchema(_curBand);   // (2026-11-09) el esquema de la banda, en cuanto se pueda hacer
            if (_trainList == null || IsDisposed) return;
            var rows = new List<TrainRow>();
            var p = _pilot;
            if (p != null && p.Ready)
                foreach (var kv in p.TrainNos)
                {
                    string stopsNow = string.Join(" · ", kv.Value.stops ?? Array.Empty<string>());
                    if (_lastNos.TryGetValue(kv.Key, out var was) && (was.type != kv.Value.type || was.dest != kv.Value.dest || was.stops != stopsNow))
                    {
                        _noChanged[kv.Key] = (DateTime.UtcNow, NoText(was.type, was.dest) + (was.stops != stopsNow ? " · " + Tr("paradas") + ": " + (was.stops.Length > 0 ? was.stops : "—") : ""));
                        Chime();   // (el «din-don» del puesto, no el sonido de Windows)
                    }
                    _lastNos[kv.Key] = (kv.Value.type, kv.Value.dest, stopsNow);
                }
            foreach (var k in _noChanged.Where(kv => DateTime.UtcNow - kv.Value.at > TimeSpan.FromMinutes(2)).Select(kv => kv.Key).ToList()) _noChanged.Remove(k);
            var bandZones = _curBand > 0 ? BandZones(_curBand) : new List<string>();
            var curZones = _curBand == 0 && _curZone != null ? DepZones(_curZone) : new List<string>();
            if (p != null && p.Ready)
                foreach (var t in p.Trains)
                {
                    if (_curBand == 0 && _curZone != null && !curZones.Any(q => TrainInZone(t.name, t.tx, t.tz, t.x, t.z, q))) continue;   // (2026-11-10) la dependencia con sus tramos
                    if (_curBand > 0 && !bandZones.Any(q => TrainInZone(t.name, t.tx, t.tz, t.x, t.z, q))) continue;   // (2026-11-09) los de la banda
                    var r = new TrainRow { Name = t.name, Player = t.player, Ai = RoomAi.Is(t.name), Loose = RoomLoose.Is(t.name), Kmh = t.kmh };
                    r.Speed = t.kmh >= 1 ? $"{t.kmh:0} km/h" : Tr("parado");
                    if (t.player)
                    {
                        if (p.TrainNos.TryGetValue(t.name, out var n))
                        {
                            r.Title = n.no + " · " + t.name;
                            string ty = n.type.Length > 0 ? Tr(MainMenuForm.SalaServiceName(n.type)) : "";
                            r.Sub = ty + (n.dest.Length > 0 ? (ty.Length > 0 ? " → " : "→ ") + n.dest : "");
                            r.Con = n.con.Length > 0 ? n.con : null;
                            if (n.stops != null && n.stops.Length > 0) r.Stops = Tr("Paradas") + ": " + string.Join(" · ", n.stops);   // (2026-11-10)
                            if (_noChanged.TryGetValue(t.name, out var ch)) r.Changed = string.Format(Tr("Ha cambiado su servicio · antes: {0}"), ch.before.Length > 0 ? ch.before : "—");
                        }
                        else r.Title = t.name;
                        if (string.IsNullOrEmpty(r.Sub)) r.Sub = Tr("Sin tipo de servicio ni destino");
                    }
                    else if (r.Loose) { r.Title = RoomLoose.Label(t.name); r.Sub = Tr("suelto"); }
                    else if (r.Ai) { r.Title = RoomAi.Label(t.name); r.Sub = Tr("Tren de la IA"); }
                    else { r.Title = Tr("IA"); r.Sub = Tr("Tren de la IA"); }
                    if (r.Con != null) WantStrip(r.Con);
                    rows.Add(r);
                }
            rows = rows.OrderByDescending(r => r.Player).ThenBy(r => r.Loose).ThenBy(r => r.Title, StringComparer.CurrentCulture).ToList();
            string where = _curBand > 0 ? string.Format(Tr("TRENES EN {0}"), "BR" + _curBand)
                         : _curZone == null ? Tr("TRENES EN LA SALA") : string.Format(Tr("TRENES EN {0}"), _curZone.ToUpperInvariant());
            _trainsHead.Text = where + (rows.Count > 0 ? "  ·  " + rows.Count : "");
            _trainList.SetRows(rows);
            FollowTick();
        }

        // ¿está el tren en esa dependencia? Por los tramos de cada dependencia (DispatchForm.Capas.cs) o, si aún no están, por su esquema
        bool TrainInZone(string name, int tx, int tz, double x, double z, string zone)
        {
            if (!OrGeo.TryLatLon(tx * 2048.0 + x, tz * 2048.0 + z, out double la, out double lo)) return false;
            try
            {
                if (_board != null && _zoneSpans != null && _zoneSpans.TryGetValue(zone, out var spans))
                    return _board.Locate(la, lo, out int e, out double off) && spans.Any(s => s.e == e && off >= s.a - 5 && off <= s.b + 5);
                if (_cache.TryGetValue(zone, out var sc) && sc != null)
                    return sc.Locate(la, lo, out int e2, out _) && sc.Drawn(e2);
            }
            catch { }
            return false;
        }

        async void WantStrip(string con)
        {
            if (ConsistStrip == null || _strips.ContainsKey(con) || !_stripBusy.Add(con)) return;
            Bitmap bmp = null;
            try { bmp = await ConsistStrip(con); } catch { }
            if (IsDisposed) { bmp?.Dispose(); return; }
            _strips[con] = bmp;   // (null: no está en este equipo; no se vuelve a pedir)
            _trainList?.Invalidate();
        }

        void TrainsCollapse(bool on)
        {
            if (_rightCol == null) return;
            _rightCol.Width = on ? Theme.Px(64) : Theme.Px(300);
            _trainsBox.Visible = _trainsSep.Visible = !on;
        }

        // La lista: un renglón por tren, dibujado a mano (rueda: subir y bajar)
        sealed class TrainList : Control
        {
            readonly DispatchForm _f; List<TrainRow> _rows = new(); int _scroll;
            public TrainList(DispatchForm f)
            {
                _f = f;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.BgSidebar;
            }
            bool Followed(TrainRow r) => r.Name == _f._followTrain;
            int RowH(TrainRow r) => Theme.Px((r.Con != null && _f._strips.TryGetValue(r.Con, out var b) && b != null ? 74 : 50) + (r.Stops != null ? 18 : 0) + (r.Changed != null ? 34 : 0) + (Followed(r) ? 20 : 0));
            int Total => _rows.Sum(RowH) + Theme.Px(4) * Math.Max(0, _rows.Count - 1);
            int MaxScroll => Math.Max(0, Total - Height);
            bool HasBar => Total > Height;
            int BarW => Theme.Px(8);
            // (2026-11-09) la barra de desplazamiento (a la derecha, si no caben): rueda, arrastrar el tirador o clic en la guía
            Rectangle Thumb()
            {
                if (!HasBar) return Rectangle.Empty;
                int th = Math.Max(Theme.Px(30), Height * Height / Math.Max(1, Total));
                int ty = MaxScroll == 0 ? 0 : (int)((long)(Height - th) * _scroll / MaxScroll);
                return new Rectangle(Width - BarW, ty, BarW, th);
            }
            bool _barDrag; int _barFrom, _barScroll0;
            void ScrollTo(int v) { _scroll = Math.Max(0, Math.Min(v, MaxScroll)); Invalidate(); }
            public void SetRows(List<TrainRow> rows) { _rows = rows; _scroll = Math.Max(0, Math.Min(_scroll, MaxScroll)); Invalidate(); }
            protected override void OnMouseWheel(MouseEventArgs e) { ScrollTo(_scroll - Math.Sign(e.Delta) * Theme.Px(48)); base.OnMouseWheel(e); }
            protected override void OnMouseEnter(EventArgs e) { Focus(); base.OnMouseEnter(e); }
            protected override void OnResize(EventArgs e) { _scroll = Math.Max(0, Math.Min(_scroll, MaxScroll)); base.OnResize(e); }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && HasBar && e.X >= Width - BarW - Theme.Px(2))
                {
                    var th = Thumb();
                    if (th.Contains(e.X, e.Y) || e.X >= Width - BarW) { if (!th.Contains(e.X, e.Y)) ScrollTo((int)((long)MaxScroll * e.Y / Math.Max(1, Height))); _barDrag = true; _barFrom = e.Y; _barScroll0 = _scroll; Capture = true; }
                }
                base.OnMouseDown(e);
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                if (_barDrag) { var th = Thumb(); int track = Math.Max(1, Height - th.Height); ScrollTo(_barScroll0 + (int)((long)(e.Y - _barFrom) * MaxScroll / track)); }
                else Cursor = RowAt(e.Location) != null && !(HasBar && e.X >= Width - BarW) ? Cursors.Hand : Cursors.Default;
                base.OnMouseMove(e);
            }
            protected override void OnMouseUp(MouseEventArgs e)
            {
                if (_barDrag) { _barDrag = false; Capture = false; }
                else if (e.Button == MouseButtons.Left && !(HasBar && e.X >= Width - BarW)) { var r = RowAt(e.Location); if (r != null) _f.LocateTrain(r.Name); }
                base.OnMouseUp(e);
            }
            protected override void OnMouseDoubleClick(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && !(HasBar && e.X >= Width - BarW)) { var r = RowAt(e.Location); if (r != null) _f.FollowToggle(r.Name); }
                base.OnMouseDoubleClick(e);
            }
            TrainRow RowAt(Point p)
            {
                int y = -_scroll;
                foreach (var r in _rows) { int h = RowH(r); if (p.Y >= y && p.Y < y + h) return r; y += h + Theme.Px(4); }
                return null;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(BackColor);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                if (_rows.Count == 0)
                {
                    string t = _f._pilot == null || !_f._pilot.Ready ? Tr("Conectando con la sala…") : _f._curZone == null ? Tr("Ningún tren en la sala") : Tr("Ningún tren en esta dependencia");
                    using var f9 = Theme.Font(9f);
                    TextRenderer.DrawText(g, t, f9, new Rectangle(0, 0, Width, Theme.Px(60)), Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                    return;
                }
                using var fb = Theme.Font(9.2f, FontStyle.Bold);
                using var fs = Theme.Font(8.2f);
                int y = -_scroll;
                foreach (var r in _rows)
                {
                    int h = RowH(r);
                    if (y + h >= 0 && y <= Height)
                    {
                        var rc = new Rectangle(0, y, Width - 1 - (HasBar ? BarW + Theme.Px(4) : 0), h);
                        bool fol = Followed(r);
                        using (var path = RoundRect(rc, Theme.Px(6)))
                        {
                            using (var bg = new SolidBrush(r.Changed != null ? Color.FromArgb(58, 46, 22) : Theme.Surface2)) g.FillPath(bg, path);
                            if (r.Changed != null) using (var pen = new Pen(Color.FromArgb(255, 186, 48), 1.6f)) g.DrawPath(pen, path);
                            else if (fol) using (var pen = new Pen(Color.FromArgb(102, 197, 106), 1.8f)) g.DrawPath(pen, path);
                        }
                        var col = r.Player ? SchematicDraw.Occ : r.Ai ? RoomAi.Color : r.Loose ? Color.FromArgb(150, 156, 164) : SchematicDraw.AiCol;
                        using (var cb = new SolidBrush(col)) g.FillRectangle(cb, rc.X + Theme.Px(4), rc.Y + Theme.Px(8), Theme.Px(3), h - Theme.Px(16));
                        int x0 = rc.X + Theme.Px(14), w = rc.Width - Theme.Px(14) - Theme.Px(8);
                        // velocidad (a la derecha)
                        var spSz = TextRenderer.MeasureText(g, r.Speed, fb, Size.Empty, TextFormatFlags.NoPadding);
                        var spCol = r.Kmh >= 1 ? Theme.Text : Theme.Subtle;
                        TextRenderer.DrawText(g, r.Speed, fb, new Rectangle(rc.Right - Theme.Px(8) - spSz.Width, rc.Y + Theme.Px(7), spSz.Width, Theme.Px(18)), spCol, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        TextRenderer.DrawText(g, r.Title, fb, new Rectangle(x0, rc.Y + Theme.Px(7), w - spSz.Width - Theme.Px(8), Theme.Px(18)), Theme.Text, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                        TextRenderer.DrawText(g, r.Sub, fs, new Rectangle(x0, rc.Y + Theme.Px(27), w, Theme.Px(16)), Theme.Subtle, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                        int stopsH = 0;
                        if (r.Stops != null)   // (2026-11-10) sus paradas intermedias
                        {
                            stopsH = Theme.Px(18);
                            TextRenderer.DrawText(g, r.Stops, fs, new Rectangle(x0, rc.Y + Theme.Px(44), w, Theme.Px(16)), Color.FromArgb(120, 200, 230), TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                        }
                        // (2026-11-09) el aviso: ha cambiado el tipo de servicio o el destino (dos minutos)
                        if (r.Changed != null)
                            TextRenderer.DrawText(g, "⚠ " + r.Changed, fs, new Rectangle(x0, rc.Bottom - Theme.Px(36) - (fol ? Theme.Px(20) : 0), w, Theme.Px(30)), Color.FromArgb(255, 186, 48), TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                        // (2026-11-09) el que se sigue
                        if (fol)
                            TextRenderer.DrawText(g, "● " + Tr("Siguiendo su movimiento · doble clic para dejarlo"), fs, new Rectangle(x0, rc.Bottom - Theme.Px(21), w, Theme.Px(16)), Color.FromArgb(102, 197, 106), TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                        // la composición 2D (como mucho del ancho del renglón)
                        if (r.Con != null && _f._strips.TryGetValue(r.Con, out var bmp) && bmp != null && bmp.Height > 0)
                        {
                            float sh = Theme.Px(20), sw = bmp.Width * sh / bmp.Height;
                            if (sw > w) { sh *= w / sw; sw = w; }
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(bmp, new RectangleF(x0, rc.Y + Theme.Px(48) + stopsH + (Theme.Px(20) - sh) / 2, sw, sh));
                        }
                    }
                    y += h + Theme.Px(4);
                }
                // la barra
                if (HasBar)
                {
                    using (var tb = new SolidBrush(Color.FromArgb(34, 39, 46))) g.FillRectangle(tb, Width - BarW, 0, BarW, Height);
                    var th = Thumb();
                    using (var path = RoundRect(new Rectangle(th.X + 1, th.Y + 1, th.Width - 2, th.Height - 2), Theme.Px(3)))
                    using (var b = new SolidBrush(_barDrag ? Color.FromArgb(150, 160, 172) : Color.FromArgb(96, 106, 118))) g.FillPath(b, path);
                }
            }

            static GraphicsPath RoundRect(Rectangle r, int rad)
            {
                var p = new GraphicsPath(); int d = rad * 2;
                p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure(); return p;
            }
        }
    }
}
