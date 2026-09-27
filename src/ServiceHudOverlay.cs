// HUD acompañante de servicio: ventanita pequeña, siempre encima, semitransparente y arrastrable,
// que aparece mientras conduces un servicio de empresa en Open Rails. NO roba el foco al simulador
// (WS_EX_NOACTIVATE) y solo se ve si OR corre en ventana / ventana sin bordes (no en fullscreen
// exclusivo). Todo se dibuja a mano (sin controles hijos) para que arrastrar/plegar sea fiable.
//
// Incluye un MINI-MAPA desplegable (plegado por defecto): dibuja el trazado de la ruta (puntos de
// /API/MAP/INIT/), la traza recorrida y una FLECHA ORIENTADA con la posición actual del tren
// (lat/lon en vivo de /API/MAP/). El mapa crece hacia ARRIBA para no tapar los mandos.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public class ServiceHudOverlay : Form
    {
        readonly string _company, _train, _route;
        readonly Image _logo;
        readonly Func<DateTime?> _startUtc;   // inicio del cronómetro (null = el escenario aún está cargando)
        readonly AppPrefs _prefs;
        readonly System.Windows.Forms.Timer _tick;
        readonly bool _service;      // true = servicio de empresa (tiempo + viajeros); false = conducción normal
        readonly Func<(int onboard, int boarded, int capacity)> _pax;   // viajeros en vivo (o null)
        readonly Func<(bool has, double lat, double lon)> _pos;         // posición en vivo (o null)
        readonly Func<double> _km;   // km recorridos en vivo (o null)
        double _kmVal;
        readonly Func<string> _paxNote;   // «Próxima: X · ~N esperan» / «Estación: +a / −b» (o null)
        string _pNote = "";
        int _pOn, _pBoard, _pCap;    // último snapshot de viajeros
        bool _showPax;               // hay tren de viajeros → mostrar fila de viajeros
        bool _blink;                 // parpadeo del piloto de grabación
        bool _collapsed;             // modo mini (solo piloto + cronómetro)
        bool _mapOpen;               // mini-mapa desplegado
        bool _hover;

        // Datos del mapa
        double[][] _segLat = Array.Empty<double[]>(), _segLon = Array.Empty<double[]>();   // polilíneas de vía (TDB)
        double[] _trkLat = Array.Empty<double>(), _trkLon = Array.Empty<double>();          // nube de puntos (respaldo API)
        double[] _stLat = Array.Empty<double>(), _stLon = Array.Empty<double>();
        string[] _stName = Array.Empty<string>();
        readonly List<(double lat, double lon)> _crumb = new();   // traza recorrida (posiciones sucesivas)
        double _curLat, _curLon; bool _hasPos;
        double _heading; bool _hasHeading;
        double _headingTarget;                                   // el rumbo mostrado gira suave hacia este
        readonly System.Windows.Forms.Timer _anim;               // ~25 fps: la marca del tren se desliza

        // Arrastre
        bool _down, _dragging;
        Point _downScreen;
        Point _formAtDown;

        // Zonas clicables (en coordenadas de cliente)
        Rectangle _hitBigMap, _hitMap, _hitCollapse, _hitClose, _hitCab;
        // Pupitre (indicadores del tren): botón junto a los del minimapa y el mapa grande.
        public Func<bool> CabVisible;
        public Action ToggleCab;
        ServiceMapWindow _bigMap;   // ventana de mapa grande con transparencia

        static readonly Color Rec = Color.FromArgb(224, 86, 86);
        static readonly Color Teal = Color.FromArgb(94, 190, 155);
        const int ExpandedW = 244, ExpandedH = 72, ExpandedHPax = 114, ExpandedHNoSvc = 40, ExpandedHNoSvcPax = 86, CollapsedW = 128, CollapsedH = 34;
        const int MapHeaderH = 6, MapAreaH = 150;
        // Límites del mini-mapa al agrandarlo con el ratón.
        const int MapMinW = ExpandedW, MapMinH = 110, MapMaxW = 1100, MapMaxH = 800;

        // Tamaño actual del mini-mapa (se guarda en las preferencias).
        int _mapW = ExpandedW, _mapH = MapAreaH;
        int MapBlockH => MapHeaderH + _mapH;
        bool _resizing; Size _sizeAtDown; Rectangle _hitGrip;

        // Megafonía: fila propia con el altavoz (encender/apagar) y la línea elegida (abre la lista).
        Func<(bool avail, bool on, string line)> _pa;
        Func<List<(string id, string name)>> _paLines;
        Action<string> _paPick;
        Action _paToggle;
        Rectangle _hitPaIcon, _hitPaLine;
        PaLinePicker _paPicker;
        const int PaRowH = 31;
        int PaRow => _paShown ? PaRowH : 0;
        bool _paShown;   // hay fila de megafonía (y su alto está ya contado en el tamaño de la ventana)
        bool PaAvail { get { try { return _pa != null && _pa().avail; } catch { return false; } } }

        // La llama la aplicación cuando ya sabe si esta ruta tiene megafonía: la fila aparece o
        // desaparece y la ventana crece o encoge para dejarle sitio.
        public void NotifyPaChanged()
        {
            if (IsDisposed) return;
            bool now = PaAvail;
            if (now != _paShown) { _paShown = now; ApplySize(); }
            Invalidate();
        }

        public ServiceHudOverlay(string company, Image logo, string train, string route, Func<DateTime?> startUtc, AppPrefs prefs,
                                 bool service = true,
                                 Func<(int onboard, int boarded, int capacity)> pax = null,
                                 Func<(bool has, double lat, double lon)> pos = null,
                                 Func<double> km = null,
                                 Func<string> paxNote = null,
                                 Func<(bool avail, bool on, string line)> pa = null,
                                 Func<List<(string id, string name)>> paLines = null,
                                 Action<string> paPick = null,
                                 Action paToggle = null)
        {
            _service = service; _km = km; _paxNote = paxNote;
            _pa = pa; _paLines = paLines; _paPick = paPick; _paToggle = paToggle;
            _company = string.IsNullOrWhiteSpace(company) ? (service ? "Empresa" : "SelectOR") : company;
            _logo = logo; _train = train ?? ""; _route = route ?? ""; _startUtc = startUtc; _prefs = prefs; _pax = pax; _pos = pos;
            _collapsed = prefs?.HudCollapsed ?? false;
            _mapOpen = prefs?.HudMapOpen ?? false;
            _mapW = Clamp(prefs?.HudMapW ?? ExpandedW, MapMinW, MapMaxW);
            _mapH = Clamp(prefs?.HudMapH ?? MapAreaH, MapMinH, MapMaxH);
            _zoom = ClampZoom(prefs?.HudMapZoom ?? 1.0);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Surface;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Opacity = 0.88;
            Size = TargetSize();
            Location = InitialLocation();
            ApplyRegion();

            _tick = new System.Windows.Forms.Timer { Interval = 500 };
            _tick.Tick += (s, e) => OnTick();
            _tick.Start();
            _anim = new System.Windows.Forms.Timer { Interval = 40 };
            _anim.Tick += (s, e) => OnAnim();
            _anim.Start();
        }

        // Fotograma del mapa: lee la posición suavizada y gira el rumbo poco a poco. Solo se
        // redibuja el mapa, y solo si algo se ha movido.
        void OnAnim()
        {
            if (_pos == null) return;
            bool moved = false;
            try
            {
                var (has, lat, lon) = _pos();
                if (has && !(lat == 0 && lon == 0) && (!_hasPos || lat != _curLat || lon != _curLon))
                {
                    _curLat = lat; _curLon = lon; _hasPos = true; moved = true;
                }
            }
            catch { }
            if (_hasHeading)
            {
                double d = ((_headingTarget - _heading) % 360 + 540) % 360 - 180;   // giro más corto
                if (Math.Abs(d) > 0.2) { _heading = (_heading + d * 0.18 + 360) % 360; moved = true; }
                else if (d != 0) { _heading = _headingTarget; moved = true; }
            }
            if (moved && _mapOpen && !_collapsed && Visible)
                Invalidate(new Rectangle(0, 0, Width, MapBlockH));
        }

        // No robar el foco al simulador al aparecer.
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/;
                return cp;
            }
        }

        // Recibe el trazado de la ruta y las estaciones (lat/lon), ya en el hilo de UI.
        public void SetMap(HudMapData m)
        {
            if (m == null) return;
            _segLat = m.SegLat ?? Array.Empty<double[]>();
            _segLon = m.SegLon ?? Array.Empty<double[]>();
            _trkLat = m.TrkLat ?? Array.Empty<double>();
            _trkLon = m.TrkLon ?? Array.Empty<double>();
            _stLat = m.StLat ?? Array.Empty<double>();
            _stLon = m.StLon ?? Array.Empty<double>();
            _stName = m.StName ?? Array.Empty<string>();
            if (_mapOpen) Invalidate();
        }

        Point InitialLocation()
        {
            try
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                if (_prefs != null && _prefs.HudX >= 0 && _prefs.HudY >= 0)
                {
                    int x = Math.Min(Math.Max(_prefs.HudX, wa.Left), wa.Right - Width);
                    int y = Math.Min(Math.Max(_prefs.HudY, wa.Top), wa.Bottom - Height);
                    return new Point(x, y);
                }
                return new Point(wa.Left + 18, wa.Bottom - Height - 18);   // esquina inferior izquierda
            }
            catch { return new Point(40, 40); }
        }

        void ApplyRegion()
        {
            int r = _collapsed ? 16 : 12;
            using var p = RoundPath(new Rectangle(0, 0, Width, Height), r);
            Region = new Region(p);
        }

        int InfoHeight() => (!_service ? (_showPax ? ExpandedHNoSvcPax : ExpandedHNoSvc) : (_showPax ? ExpandedHPax : ExpandedH))
                            + PaRow;
        // Fila de viajeros: bajo el cronómetro en servicio; bajo la fila de km en conducción libre.
        int PaxRowTop(int top) => _service ? top + 66 : top + 38;
        int InfoTop() => (_mapOpen && !_collapsed) ? MapBlockH : 0;

        static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

        Size TargetSize()
        {
            if (_collapsed) return new Size(CollapsedW, CollapsedH);
            int h = InfoHeight() + (_mapOpen ? MapBlockH : 0);
            return new Size(_mapOpen ? _mapW : ExpandedW, h);
        }

        // Ajusta el tamaño manteniendo el BORDE INFERIOR fijo (el HUD vive abajo a la izquierda,
        // y el mapa crece hacia arriba).
        void ApplySize()
        {
            var target = TargetSize();
            if (target == Size) return;
            int bottom = Top + Height;
            Size = target;
            Top = Math.Max(0, bottom - Height);
            ApplyRegion();
        }

        void OnTick()
        {
            _blink = !_blink;
            if (_pax != null)
            {
                try { var s = _pax(); _pOn = s.onboard; _pBoard = s.boarded; _pCap = s.capacity; } catch { }
            }
            if (_km != null) { try { _kmVal = _km(); } catch { } }
            if (_paxNote != null) { try { _pNote = _paxNote() ?? ""; } catch { _pNote = ""; } }
            if (_pos != null)
            {
                try
                {
                    var (has, lat, lon) = _pos();
                    if (has && !(lat == 0 && lon == 0))
                    {
                        _curLat = lat; _curLon = lon; _hasPos = true;
                        if (_crumb.Count == 0 || Haversine(_crumb[^1].lat, _crumb[^1].lon, lat, lon) > 8)
                        {
                            _crumb.Add((lat, lon));
                            if (_crumb.Count > 4000) _crumb.RemoveRange(0, 500);
                            // rumbo entre las dos últimas posiciones con distancia suficiente
                            if (_crumb.Count >= 2)
                            {
                                var a = _crumb[^2]; var b = _crumb[^1];
                                _headingTarget = Bearing(a.lat, a.lon, b.lat, b.lon);
                                if (!_hasHeading) _heading = _headingTarget;   // la primera vez, directo
                                _hasHeading = true;
                            }
                        }
                    }
                }
                catch { }
            }
            bool want = _pax != null && _pCap > 0;   // tren de viajeros (en servicio o en conducción libre)
            bool pa = PaAvail;                        // megafonía: llega al descargarse el paquete de la ruta
            if (want != _showPax || pa != _paShown) { _showPax = want; _paShown = pa; ApplySize(); }
            Invalidate();
        }

        void ToggleCollapse()
        {
            _collapsed = !_collapsed;
            if (_prefs != null) _prefs.HudCollapsed = _collapsed;
            ApplySize();
            Invalidate();
        }

        void ToggleMap()
        {
            _mapOpen = !_mapOpen;
            if (_prefs != null) _prefs.HudMapOpen = _mapOpen;
            ApplySize();
            Invalidate();
        }

        string KmText()
        {
            string s = _kmVal.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            if (!I18n.English) s = s.Replace('.', ',');
            return s + " km";
        }

        string Elapsed()
        {
            DateTime? st = null;
            try { st = _startUtc?.Invoke(); } catch { }
            int s = st == null ? 0 : (int)Math.Max(0, (DateTime.UtcNow - st.Value).TotalSeconds);
            int h = s / 3600, m = s % 3600 / 60, ss = s % 60;
            return $"{h:00}:{m:00}:{ss:00}";
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Opacity = 1.0; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();   // fuera los botones de zoom
            if (!_down) Opacity = 0.88;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // Esquina inferior derecha: agrandar o encoger el mini-mapa.
                if (_mapOpen && !_collapsed && _hitGrip.Contains(e.Location))
                {
                    _resizing = true; _down = false; _dragging = false;
                    _downScreen = Cursor.Position; _sizeAtDown = new Size(_mapW, _mapH);
                    Opacity = 1.0;
                    base.OnMouseDown(e);
                    return;
                }
                _down = true; _dragging = false;
                _downScreen = Cursor.Position; _formAtDown = Location;
                Opacity = 1.0;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_resizing)
            {
                var now = Cursor.Position;
                int w = Clamp(_sizeAtDown.Width + (now.X - _downScreen.X), MapMinW, MapMaxW);
                int h = Clamp(_sizeAtDown.Height + (now.Y - _downScreen.Y), MapMinH, MapMaxH);
                try
                {
                    var wa = Screen.FromControl(this).WorkingArea;      // nunca más grande que la pantalla
                    w = Math.Min(w, Math.Max(MapMinW, wa.Width - 40));
                    h = Math.Min(h, Math.Max(MapMinH, wa.Height - InfoHeight() - 60));
                }
                catch { }
                if (w != _mapW || h != _mapH)
                {
                    _mapW = w; _mapH = h;
                    Size = TargetSize();
                    ApplyRegion();
                    Invalidate();
                }
                base.OnMouseMove(e);
                return;
            }
            // El puntero avisa de que ahí se puede redimensionar.
            Cursor = (_mapOpen && !_collapsed && _hitGrip.Contains(e.Location)) ? Cursors.SizeNWSE : Cursors.Default;
            if (_down)
            {
                var now = Cursor.Position;
                int dx = now.X - _downScreen.X, dy = now.Y - _downScreen.Y;
                if (!_dragging && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3)) _dragging = true;
                if (_dragging) Location = new Point(_formAtDown.X + dx, _formAtDown.Y + dy);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_resizing)
            {
                _resizing = false;
                if (!_hover) Opacity = 0.88;
                if (_prefs != null)
                {
                    _prefs.HudMapW = _mapW; _prefs.HudMapH = _mapH;
                    _prefs.HudX = Location.X; _prefs.HudY = Location.Y;
                }
                base.OnMouseUp(e);
                return;
            }
            bool wasDrag = _dragging;
            _down = false; _dragging = false;
            if (!_hover) Opacity = 0.88;

            if (wasDrag)
            {
                if (_prefs != null) { _prefs.HudX = Location.X; _prefs.HudY = Location.Y; }
            }
            else
            {
                if (_collapsed) { ToggleCollapse(); }
                else if (_hitClose.Contains(e.Location)) { Hide(); return; }   // se oculta; se vuelve a abrir desde la barra superior
                else if (_hitCab.Contains(e.Location)) { try { ToggleCab?.Invoke(); } catch { } Invalidate(); }
                else if (_hitZoomIn.Contains(e.Location)) { Zoom(1); }
                else if (_hitZoomOut.Contains(e.Location)) { Zoom(-1); }
                else if (_hitBigMap.Contains(e.Location)) { ToggleBigMap(); }
                else if (_hitMap.Contains(e.Location)) { ToggleMap(); }
                else if (_hitCollapse.Contains(e.Location)) { ToggleCollapse(); }
                else if (_hitPaIcon.Contains(e.Location)) { TogglePa(); }
                else if (_hitPaLine.Contains(e.Location)) { OpenLinePicker(); }
            }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int r = _collapsed ? 16 : 12;
            var rc = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var bg = new SolidBrush(Theme.Surface)) FillRound(g, rc, r, bg);
            using (var pen = new Pen(Blend(Theme.Surface, Color.Black, 0.35f))) DrawRound(g, rc, r, pen);
            using (var accent = new SolidBrush(Rec))
                g.FillRectangle(accent, new Rectangle(0, 6, 3, Height - 12));

            if (_collapsed)
            {
                DrawDot(g, 14, Height / 2, _service ? Rec : Teal, _service ? _blink : true);
                string pillTxt = _service ? Elapsed() : (_km != null ? KmText() : "SelectOR");
                TextRenderer.DrawText(g, pillTxt, Theme.Font(_service ? 12.5f : 10f, FontStyle.Bold),
                    new Rectangle(28, 0, Width - 32, Height), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                _hitBigMap = _hitMap = _hitCollapse = _hitClose = _hitGrip = _hitCab = Rectangle.Empty;
                _hitPaIcon = _hitPaLine = _hitZoomIn = _hitZoomOut = Rectangle.Empty;
                return;
            }

            int infoTop = InfoTop();
            if (_mapOpen) DrawMap(g); else _hitZoomIn = _hitZoomOut = Rectangle.Empty;
            DrawInfo(g, infoTop);
            DrawPaRow(g, infoTop);
            DrawResizeGrip(g);
        }

        // Fila de megafonía: altavoz (clic = encender/apagar) y línea elegida (clic = elegir otra).
        void DrawPaRow(Graphics g, int top)
        {
            if (!_paShown) { _hitPaIcon = _hitPaLine = Rectangle.Empty; return; }
            (bool avail, bool on, string line) st;
            try { st = _pa(); } catch { _hitPaIcon = _hitPaLine = Rectangle.Empty; return; }

            // Sin línea divisoria: el icono va separado del borde y el texto, del icono.
            int y = top + InfoHeight() - PaRowH;
            const int IcoX = 11, IcoW = 21, TxtX = IcoX + IcoW + 9;
            int cy = y + 3 + IcoW / 2;
            _hitPaIcon = new Rectangle(IcoX, y + 3, IcoW, IcoW);
            var col = st.on ? Teal : Rec;   // desconectada: en rojo, y con el altavoz tachado
            DrawSpeakerGlyph(g, _hitPaIcon, col, st.on);
            if (!st.on)   // desconectada: solo el altavoz tachado (se pulsa para volver a activarla)
            {
                _hitPaLine = Rectangle.Empty;
                if (_hover)
                    TextRenderer.DrawText(g, I18n.T("megafonía desconectada"), Theme.Font(8.5f),
                        new Rectangle(TxtX, y + 3, Width - TxtX - 8, IcoW), Blend(Rec, Theme.Surface, 0.3f),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                return;
            }

            bool vol = DateTime.UtcNow < _paVolShownUntil && _prefs != null;
            string txt = vol ? string.Format(I18n.T("volumen {0} %"), _prefs.PaVolume)
                             : string.IsNullOrEmpty(st.line) ? I18n.T("sin línea") : st.line;
            using var f = Theme.Font(9.25f, vol ? FontStyle.Regular : FontStyle.Bold);
            int maxW = Width - TxtX - 20;
            int tw = Math.Min(TextRenderer.MeasureText(g, txt, f, Size.Empty, TextFormatFlags.NoPadding).Width, maxW);
            var txtRect = new Rectangle(TxtX, y + 3, maxW, IcoW);
            TextRenderer.DrawText(g, txt, f, txtRect, st.on ? Blend(Teal, Theme.Text, 0.25f) : Theme.Subtle,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // Flechita: avisa de que ahí se elige la línea. La zona clicable abarca texto y flecha.
            int ax = Math.Min(TxtX + tw + 9, Width - 14);
            using (var pen = new Pen(Blend(Theme.Subtle, Theme.Surface, _hover ? 0.1f : 0.45f), 1.5f)
                   { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new Point(ax, cy - 2), new Point(ax + 4, cy + 2), new Point(ax + 8, cy - 2) });
            _hitPaLine = new Rectangle(TxtX, y + 3, Math.Min(tw + 22, Width - TxtX - 6), IcoW);
        }

        static void DrawSpeakerGlyph(Graphics g, Rectangle b, Color col, bool on)
        {
            using var br = new SolidBrush(col);
            using var pen = new Pen(col, Math.Max(1.5f, b.Height * 0.10f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            int cy = b.Y + b.Height / 2;
            int x0 = b.X + (int)(b.Width * 0.04f);
            int bodyW = Math.Max(3, (int)(b.Width * 0.15f));
            int bodyH = Math.Max(5, (int)(b.Height * 0.32f));
            int coneW = Math.Max(5, (int)(b.Width * 0.22f));
            int coneH = Math.Max(9, (int)(b.Height * 0.66f));
            g.FillRectangle(br, x0, cy - bodyH / 2, bodyW, bodyH);
            using (var path = new GraphicsPath())
            {
                int x1 = x0 + bodyW, x2 = x1 + coneW;
                path.AddPolygon(new[] { new Point(x1, cy - bodyH / 2), new Point(x2, cy - coneH / 2),
                                        new Point(x2, cy + coneH / 2), new Point(x1, cy + bodyH / 2) });
                g.FillPath(br, path);
            }
            int xw = x0 + bodyW + coneW + 1;
            if (on)
                for (int i = 0; i < 2; i++)
                {
                    int r = (int)(b.Height * (0.20f + i * 0.15f));
                    g.DrawArc(pen, xw - r, cy - r, r * 2, r * 2, -55, 110);
                }
            else
                g.DrawLine(pen, x0 - 1, cy + (int)(b.Height * 0.30f), xw + (int)(b.Width * 0.10f), cy - (int)(b.Height * 0.34f));
        }

        // La rueda del ratón sobre la fila de megafonía sube y baja el volumen de los avisos.
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (PaAvail && _prefs != null && (_hitPaIcon.Contains(e.Location) || _hitPaLine.Contains(e.Location)))
            {
                _prefs.PaVolume = Math.Max(0, Math.Min(100, _prefs.PaVolume + (e.Delta > 0 ? 10 : -10)));
                Audio.SetVolume(_prefs.PaVolume);
                try { _prefs.Save(); } catch { }
                _paVolShownUntil = DateTime.UtcNow.AddSeconds(2);   // se enseña un momento el valor
                Invalidate();
                return;
            }
            // Sobre el mapa: zoom (hacia arriba acerca, hacia abajo aleja). Se guarda al momento.
            if (_mapOpen && !_collapsed && e.Y >= 0 && e.Y < MapBlockH)
            {
                Zoom(e.Delta > 0 ? 1 : -1);
                return;
            }
            base.OnMouseWheel(e);
        }

        // ---- Zoom del mini-mapa ----
        const double ZoomMin = 0.15, ZoomMax = 8.0, ZoomStep = 1.25;
        double _zoom = 1.0;
        Rectangle _hitZoomIn, _hitZoomOut;
        DateTime _zoomShownUntil;

        static double ClampZoom(double z) => double.IsNaN(z) || z <= 0 ? 1.0 : Math.Max(ZoomMin, Math.Min(ZoomMax, z));

        void Zoom(int dir)
        {
            double z = ClampZoom(dir > 0 ? _zoom * ZoomStep : _zoom / ZoomStep);
            if (Math.Abs(z - 1.0) < 0.03) z = 1.0;   // al pasar por el zoom de siempre, se queda justo en él
            _zoomShownUntil = DateTime.UtcNow.AddSeconds(1.5);   // se enseña un momento la escala
            if (z != _zoom)
            {
                _zoom = z;
                if (_prefs != null) { _prefs.HudMapZoom = z; try { _prefs.Save(); } catch { } }
            }
            Invalidate();
        }

        // Metros que caben a lo ancho del mapa con el zoom actual (misma escala al cambiar su tamaño).
        double MapWorldWidthM(Rectangle area) => Math.Max(1, area.Width - 16) / (HudMapRender.BaseScale * _zoom);

        DateTime _paVolShownUntil;

        void TogglePa()
        {
            try { _paToggle?.Invoke(); } catch { }
            Invalidate();   // la fila ocupa lo mismo encendida que apagada: no se redimensiona
        }

        // Lista de líneas junto al HUD. Es una ventana aparte para no robarle el foco al simulador.
        void OpenLinePicker()
        {
            if (_paPicker != null && !_paPicker.IsDisposed) { _paPicker.Close(); _paPicker = null; return; }
            List<(string id, string name)> lines;
            try { lines = _paLines?.Invoke() ?? new List<(string, string)>(); } catch { return; }
            string cur = null;
            try { cur = _pa().line; } catch { }
            _paPicker = new PaLinePicker(lines, cur,
                id => { try { _paPick?.Invoke(id); } catch { } Invalidate(); },
                () => TogglePa());   // «Desconectar megafonía» desde la propia lista
            var pt = PointToScreen(new Point(0, Height));
            _paPicker.ShowAt(pt.X, pt.Y + 4, Width);
        }

        // Tirador de la esquina inferior derecha: arrastrándolo se agranda o se encoge el mini-mapa.
        void DrawResizeGrip(Graphics g)
        {
            if (!_mapOpen || _collapsed) { _hitGrip = Rectangle.Empty; return; }
            _hitGrip = new Rectangle(Width - 18, Height - 18, 16, 16);
            var col = _resizing ? Theme.Accent : Blend(Theme.Text, Theme.Surface, _hover ? 0.35f : 0.65f);
            using var pen = new Pen(col, 1.6f);
            for (int i = 0; i < 3; i++)
            {
                int o = 4 + i * 4;
                g.DrawLine(pen, _hitGrip.Right - o, _hitGrip.Bottom - 2, _hitGrip.Right - 2, _hitGrip.Bottom - o);
            }
        }

        void DrawInfo(Graphics g, int top)
        {
            // separador entre mapa e info
            if (top > 0)
                using (var pen = new Pen(Blend(Theme.Surface, Color.Black, 0.3f)))
                    g.DrawLine(pen, 8, top, Width - 8, top);

            // Fila 1: (logo) + título + iconos (mapa grande / mapa / plegar / cerrar)
            int lx = 12, ly = top + 9, ls = 24;
            bool hasLogo = _service && _logo != null;
            if (hasLogo)
            {
                using var clip = RoundPath(new Rectangle(lx, ly, ls, ls), 7);
                var old = g.Clip; g.SetClip(clip);
                g.DrawImage(_logo, new Rectangle(lx, ly, ls, ls));
                g.Clip = old;
            }
            int titleX = hasLogo ? lx + ls + 8 : lx;
            if (_service)
                TextRenderer.DrawText(g, _company, Theme.Font(12f, FontStyle.Bold),
                    new Rectangle(titleX, ly - 1, Width - titleX - 112, ls + 2), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            else if (_km != null)
            {
                // Conducción normal: solo los km recorridos, en la misma fila que los iconos.
                DrawDot(g, 15, ly + ls / 2, Teal, true);
                TextRenderer.DrawText(g, KmText(), Theme.Font(10f, FontStyle.Bold),
                    new Rectangle(28, ly - 1, Width - 28 - 112, ls + 2), Blend(Theme.Text, Teal, 0.15f),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }

            _hitClose = new Rectangle(Width - 23, top + 8, 16, 16);
            _hitCollapse = new Rectangle(Width - 44, top + 8, 16, 16);
            _hitMap = new Rectangle(Width - 66, top + 8, 16, 16);
            _hitBigMap = new Rectangle(Width - 88, top + 8, 16, 16);
            _hitCab = ToggleCab != null ? new Rectangle(Width - 110, top + 8, 16, 16) : Rectangle.Empty;
            var icoCol = _hover ? Theme.Subtle : Blend(Theme.Subtle, Theme.Surface, 0.35f);
            var mapCol = _mapOpen ? Teal : icoCol;
            var bigCol = (_bigMap != null && !_bigMap.IsDisposed) ? Teal : icoCol;
            using (var pen = new Pen(bigCol, 1.6f)) DrawExpandGlyph(g, _hitBigMap, pen);
            if (!_hitCab.IsEmpty)
            {
                // Esfera con aguja: el pupitre. En verde azulado cuando está a la vista.
                bool cabOn = false; try { cabOn = CabVisible?.Invoke() ?? false; } catch { }
                using var pen = new Pen(cabOn ? Teal : icoCol, 1.6f);
                var r = _hitCab;
                g.DrawArc(pen, r.Left + 1, r.Top + 2, r.Width - 2, r.Width - 2, 150, 240);
                int cx = r.Left + r.Width / 2, cy = r.Top + 2 + (r.Width - 2) / 2;
                g.DrawLine(pen, cx, cy, r.Right - 4, r.Top + 5);
            }
            using (var pen = new Pen(mapCol, 1.6f)) DrawMapGlyph(g, _hitMap, pen);
            using (var pen = new Pen(icoCol, 1.6f))
            {
                g.DrawLine(pen, _hitCollapse.Left + 3, _hitCollapse.Top + 8, _hitCollapse.Right - 3, _hitCollapse.Top + 8);
                g.DrawLine(pen, _hitClose.Left + 3, _hitClose.Top + 3, _hitClose.Right - 3, _hitClose.Bottom - 3);
                g.DrawLine(pen, _hitClose.Right - 3, _hitClose.Top + 3, _hitClose.Left + 3, _hitClose.Bottom - 3);
            }

            // Fila 2: piloto + cronómetro + km recorridos (SOLO en servicio de empresa)
            if (_service)
            {
                var timerRect = new Rectangle(28, top + 36, 150, 28);
                bool running = false; try { running = _startUtc?.Invoke() != null; } catch { }
                DrawDot(g, 15, timerRect.Top + timerRect.Height / 2, Rec, running && _blink);   // quieto mientras carga el escenario
                using var fT = Theme.Font(15f, FontStyle.Bold);
                TextRenderer.DrawText(g, Elapsed(), fT, timerRect, Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (_km != null)
                {
                    int tw = TextRenderer.MeasureText(g, Elapsed(), fT, Size.Empty, TextFormatFlags.NoPadding).Width;
                    TextRenderer.DrawText(g, "  ·  " + KmText(), Theme.Font(10f, FontStyle.Bold),
                        new Rectangle(28 + tw, top + 36, Width - 28 - tw - 8, 28), Blend(Theme.Text, Theme.Subtle, 0.35f),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }

            // Fila de viajeros (tren de viajeros, en servicio o libre): a bordo · subidos
            if (_showPax)
            {
                int py = PaxRowTop(top);
                DrawDot(g, 15, py + 10, Teal, true);
                using var fOn = Theme.Font(10.5f, FontStyle.Bold);
                string onTxt = string.Format(I18n.T("{0} a bordo"), _pOn.ToString("N0"));
                TextRenderer.DrawText(g, onTxt, fOn, new Rectangle(28, py, Width - 34, 20), Teal,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                int onW = TextRenderer.MeasureText(g, onTxt, fOn, Size.Empty, TextFormatFlags.NoPadding).Width;
                using var fSub = Theme.Font(9f);
                TextRenderer.DrawText(g, "  ·  " + string.Format(I18n.T("{0} subidos"), _pBoard.ToString("N0")),
                    fSub, new Rectangle(28 + onW, py, Width - 34 - onW, 20), Theme.Subtle,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                // Próxima parada (esperan N) o, justo tras una parada, cuántos subieron/bajaron.
                if (!string.IsNullOrEmpty(_pNote))
                {
                    using var fNote = Theme.Font(8.5f);
                    TextRenderer.DrawText(g, "▸ " + _pNote, fNote, new Rectangle(28, py + 21, Width - 36, 18), Blend(Teal, Theme.Text, 0.35f),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
            }

        }

        void DrawMap(Graphics g)
        {
            var area = new Rectangle(6, MapHeaderH, Width - 12, _mapH - 4);
            double anchoM = MapWorldWidthM(area);
            HudMapRender.Draw(g, area, _segLat, _segLon, _trkLat, _trkLon, _stLat, _stLon, _stName, _crumb,
                _hasPos, _curLat, _curLon, _hasHeading, _heading, anchoM, 7.5f, 1f, I18n.T("Cargando mapa…"));

            // Botones de zoom abajo a la izquierda (solo con el ratón encima, para no tapar el mapa).
            if (!_hover) { _hitZoomIn = _hitZoomOut = Rectangle.Empty; }
            else
            {
                const int B = 18;
                _hitZoomIn = new Rectangle(area.Left + 5, area.Bottom - 2 * B - 8, B, B);
                _hitZoomOut = new Rectangle(area.Left + 5, area.Bottom - B - 5, B, B);
                var fondo = Color.FromArgb(215, Theme.Surface);
                var borde = Blend(Theme.Surface, Theme.Text, 0.35f);
                foreach (var (r, mas, lim) in new[] { (_hitZoomIn, true, _zoom >= ZoomMax), (_hitZoomOut, false, _zoom <= ZoomMin) })
                {
                    using (var b = new SolidBrush(fondo)) FillRound(g, r, 5, b);
                    using (var p = new Pen(borde)) DrawRound(g, r, 5, p);
                    using var pen = new Pen(lim ? Blend(Theme.Subtle, Theme.Surface, 0.5f) : Theme.Text, 1.8f);
                    int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
                    g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
                    if (mas) g.DrawLine(pen, cx, cy - 5, cx, cy + 5);
                }
            }

            // Al cambiar el zoom se enseña un momento cuánto abarca el mapa a lo ancho.
            if (DateTime.UtcNow < _zoomShownUntil)
            {
                string t = anchoM >= 1000 ? (anchoM / 1000.0).ToString(anchoM >= 10000 ? "0" : "0.0") + " km" : ((int)Math.Round(anchoM / 10.0) * 10) + " m";
                t = "↔ " + t;
                using var f = Theme.Font(8.5f, FontStyle.Bold);
                var sz = TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding);
                var rc = new Rectangle(area.Right - sz.Width - 16, area.Top + 4, sz.Width + 10, sz.Height + 6);
                using (var b = new SolidBrush(Color.FromArgb(215, Theme.Surface))) FillRound(g, rc, 5, b);
                TextRenderer.DrawText(g, t, f, rc, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        // Instantánea del estado del mapa para que la ventana grande lo reproduzca idéntico.
        public MapSnap Snapshot() => new MapSnap
        {
            SegLat = _segLat, SegLon = _segLon, TrkLat = _trkLat, TrkLon = _trkLon,
            StLat = _stLat, StLon = _stLon, StName = _stName, Crumb = _crumb,
            HasPos = _hasPos, CurLat = _curLat, CurLon = _curLon, HasHeading = _hasHeading, Heading = _heading,
            Route = _route
        };

        void DrawMapGlyph(Graphics g, Rectangle b, Pen pen)
        {
            // Iconito de mapa plegado (dos paneles con pliegue).
            int x = b.Left + 2, y = b.Top + 3, w = b.Width - 4, h = b.Height - 6;
            g.DrawRectangle(pen, x, y, w, h);
            g.DrawLine(pen, x + w / 3, y, x + w / 3, y + h);
            g.DrawLine(pen, x + 2 * w / 3, y, x + 2 * w / 3, y + h);
        }

        void DrawExpandGlyph(Graphics g, Rectangle b, Pen pen)
        {
            // Iconito "abrir en grande" (cuatro esquinas tipo maximizar).
            int x = b.Left + 2, y = b.Top + 2, w = b.Width - 4, h = b.Height - 4, s = 4;
            g.DrawLine(pen, x, y, x + s, y); g.DrawLine(pen, x, y, x, y + s);                             // sup-izq
            g.DrawLine(pen, x + w, y, x + w - s, y); g.DrawLine(pen, x + w, y, x + w, y + s);             // sup-der
            g.DrawLine(pen, x, y + h, x + s, y + h); g.DrawLine(pen, x, y + h, x, y + h - s);             // inf-izq
            g.DrawLine(pen, x + w, y + h, x + w - s, y + h); g.DrawLine(pen, x + w, y + h, x + w, y + h - s); // inf-der
        }

        public bool BigMapOpen => _bigMap != null && !_bigMap.IsDisposed;
        public bool Service => _service;

        public void ToggleBigMap()
        {
            if (_bigMap != null && !_bigMap.IsDisposed) { try { _bigMap.Close(); } catch { } _bigMap = null; return; }
            _bigMap = new ServiceMapWindow(this);
            _bigMap.FormClosed += (s, e) => { _bigMap = null; try { Invalidate(); } catch { } };
            _bigMap.Show();
            Invalidate();
        }

        void DrawDot(Graphics g, int cx, int cy, Color color, bool on)
        {
            var c = on ? color : Blend(color, Theme.Surface, 0.55f);
            using var b = new SolidBrush(c);
            g.FillEllipse(b, cx - 5, cy - 5, 10, 10);
        }

        public void CloseHud()
        {
            try { _tick?.Stop(); _anim?.Stop(); } catch { }
            try { if (_bigMap != null && !_bigMap.IsDisposed) _bigMap.Close(); } catch { }
            _bigMap = null;
            try { _prefs?.Save(); } catch { }
            try { Close(); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { _tick?.Stop(); _tick?.Dispose(); _anim?.Stop(); _anim?.Dispose(); } catch { }
            base.OnFormClosing(e);
        }

        // ---- geometría / geo ----
        static double Haversine(double la1, double lo1, double la2, double lo2)
        {
            const double R = 6371000.0;
            double dLa = (la2 - la1) * Math.PI / 180.0, dLo = (lo2 - lo1) * Math.PI / 180.0;
            double a = Math.Sin(dLa / 2) * Math.Sin(dLa / 2) +
                       Math.Cos(la1 * Math.PI / 180) * Math.Cos(la2 * Math.PI / 180) * Math.Sin(dLo / 2) * Math.Sin(dLo / 2);
            return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }

        static double Bearing(double la1, double lo1, double la2, double lo2)
        {
            double φ1 = la1 * Math.PI / 180, φ2 = la2 * Math.PI / 180, dλ = (lo2 - lo1) * Math.PI / 180;
            double y = Math.Sin(dλ) * Math.Cos(φ2);
            double x = Math.Cos(φ1) * Math.Sin(φ2) - Math.Sin(φ1) * Math.Cos(φ2) * Math.Cos(dλ);
            double θ = Math.Atan2(y, x) * 180.0 / Math.PI;
            return (θ + 360.0) % 360.0;
        }

        static void FillRound(Graphics g, Rectangle rc, int r, Brush b) { using var p = RoundPath(rc, r); g.FillPath(b, p); }
        static void DrawRound(Graphics g, Rectangle rc, int r, Pen pen) { using var p = RoundPath(rc, r); g.DrawPath(pen, p); }
        static GraphicsPath RoundPath(Rectangle rc, int r)
        {
            var p = new GraphicsPath();
            p.AddArc(rc.X, rc.Y, r, r, 180, 90);
            p.AddArc(rc.Right - r, rc.Y, r, r, 270, 90);
            p.AddArc(rc.Right - r, rc.Bottom - r, r, r, 0, 90);
            p.AddArc(rc.X, rc.Bottom - r, r, r, 90, 90);
            p.CloseFigure();
            return p;
        }
        static Color Blend(Color a, Color b, float t)
            => Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
    }

    // Datos del mini-mapa que MainMenuForm prepara y pasa al HUD (todo en lat/lon).
    public sealed class HudMapData
    {
        public double[][] SegLat;   // polilíneas de vía (líneas del TDB); vacío → usa la nube
        public double[][] SegLon;
        public double[] TrkLat;     // nube de puntos (respaldo API)
        public double[] TrkLon;
        public double[] StLat;      // estaciones
        public double[] StLon;
        public string[] StName;     // nombres de estación (limpios, agrupados)
    }

    // Instantánea del estado del mapa (para reproducirlo idéntico en la ventana grande).
    public sealed class MapSnap
    {
        public double[][] SegLat, SegLon;
        public double[] TrkLat, TrkLon, StLat, StLon;
        public string[] StName;
        public System.Collections.Generic.IReadOnlyList<(double lat, double lon)> Crumb;
        public bool HasPos, HasHeading;
        public double CurLat, CurLon, Heading;
        public string Route;
    }

    // Renderizador COMPARTIDO del mapa (lo usan el mini-mapa del HUD y la ventana grande → idénticos).
    static class HudMapRender
    {
        static readonly Color Teal = Color.FromArgb(94, 190, 155);

        public const double BaseScale = 216.0 / 1200.0;   // px por metro del mini-mapa (para zoom coherente)

        public static void Draw(Graphics g, Rectangle area,
            double[][] segLat, double[][] segLon, double[] trkLat, double[] trkLon,
            double[] stLat, double[] stLon, string[] stName,
            System.Collections.Generic.IReadOnlyList<(double lat, double lon)> crumb,
            bool hasPos, double curLat, double curLon, bool hasHeading, double heading,
            double worldWidthM, float labelPt, float arrowScale, string loadingText,
            double viewCenterLat = double.NaN, double viewCenterLon = double.NaN, double viewPxPerM = 0)
        {
            segLat ??= Array.Empty<double[]>(); segLon ??= Array.Empty<double[]>();
            trkLat ??= Array.Empty<double>(); trkLon ??= Array.Empty<double>();
            stLat ??= Array.Empty<double>(); stLon ??= Array.Empty<double>(); stName ??= Array.Empty<string>();
            bool haveSeg = segLat.Length > 0;
            bool haveTrk = trkLat.Length >= 2;
            bool haveCrumb = crumb != null && crumb.Count >= 2;
            if (!haveSeg && !haveTrk && !haveCrumb && !hasPos)
            {
                using var f0 = Theme.Font(Math.Max(9f, labelPt));
                TextRenderer.DrawText(g, loadingText, f0, area, Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            int pad = 8;
            double availW = area.Width - 2 * pad, availH = area.Height - 2 * pad;
            Func<double, double, PointF> Proj;
            if (viewPxPerM > 0 && !double.IsNaN(viewCenterLat))
            {
                // VISTA MANUAL (zoom/pan del usuario): centrada en (viewCenterLat/Lon) con escala viewPxPerM.
                double mLat = 111320.0, mLon = 111320.0 * Math.Cos(viewCenterLat * Math.PI / 180.0);
                double cx = area.X + area.Width / 2.0, cy = area.Y + area.Height / 2.0;
                Proj = (la, lo) => new PointF((float)(cx + (lo - viewCenterLon) * mLon * viewPxPerM), (float)(cy - (la - viewCenterLat) * mLat * viewPxPerM));
            }
            else if (hasPos)
            {
                double mLat = 111320.0, mLon = 111320.0 * Math.Cos(curLat * Math.PI / 180.0);
                double sc = availW / worldWidthM;
                double cx = area.X + area.Width / 2.0, cy = area.Y + area.Height / 2.0;
                Proj = (la, lo) => new PointF((float)(cx + (lo - curLon) * mLon * sc), (float)(cy - (la - curLat) * mLat * sc));
            }
            else
            {
                double minLat = double.MaxValue, maxLat = double.MinValue, minLon = double.MaxValue, maxLon = double.MinValue;
                void Ext(double la, double lo) { if (la < minLat) minLat = la; if (la > maxLat) maxLat = la; if (lo < minLon) minLon = lo; if (lo > maxLon) maxLon = lo; }
                if (haveSeg) for (int s = 0; s < segLat.Length; s++) for (int i = 0; i < segLat[s].Length; i++) Ext(segLat[s][i], segLon[s][i]);
                else if (haveTrk) for (int i = 0; i < trkLat.Length; i++) Ext(trkLat[i], trkLon[i]);
                else foreach (var c in crumb) Ext(c.lat, c.lon);
                if (minLat > maxLat) return;
                double lat0 = (minLat + maxLat) / 2.0;
                double k = Math.Cos(lat0 * Math.PI / 180.0); if (k < 0.01) k = 0.01;
                double spanX = Math.Max((maxLon - minLon) * k, 1e-6), spanY = Math.Max(maxLat - minLat, 1e-6);
                double sc = Math.Min(availW / spanX, availH / spanY);
                double ox = area.X + pad + (availW - spanX * sc) / 2.0, oy = area.Y + pad + (availH - spanY * sc) / 2.0;
                Proj = (la, lo) => new PointF((float)(ox + (lo - minLon) * k * sc), (float)(oy + (maxLat - la) * sc));
            }

            var oldClip = g.Clip; g.SetClip(area);

            if (haveSeg)
            {
                using var pen = new Pen(Color.FromArgb(120, 130, 138), Math.Max(1.4f, arrowScale)) { LineJoin = LineJoin.Round };
                EnsureBoxes(segLat, segLon);
                var vis = RectangleF.Inflate(area, 6, 6);
                for (int s = 0; s < segLat.Length; s++)
                {
                    var poly = segLat[s]; if (poly.Length < 2) continue;
                    // Fuera de la vista: ni se proyecta (el mapa se redibuja hasta 25 veces por segundo).
                    var c1 = Proj(_bbMinLa[s], _bbMinLo[s]); var c2 = Proj(_bbMaxLa[s], _bbMaxLo[s]);
                    if (!vis.IntersectsWith(RectangleF.FromLTRB(Math.Min(c1.X, c2.X), Math.Min(c1.Y, c2.Y), Math.Max(c1.X, c2.X) + 1, Math.Max(c1.Y, c2.Y) + 1))) continue;
                    var sp = new PointF[poly.Length];
                    for (int i = 0; i < poly.Length; i++) sp[i] = Proj(segLat[s][i], segLon[s][i]);
                    try { g.DrawLines(pen, sp); } catch { }
                }
            }
            else if (haveTrk)
                // Respaldo (sin archivo de vías o sin estaciones que casen): puntos de vía de la API de OR,
                // del mismo color que las líneas y con tamaño suficiente para que se lea el trazado.
                using (var b = new SolidBrush(Color.FromArgb(120, 130, 138)))
                    for (int i = 0; i < trkLat.Length; i++)
                    {
                        var p = Proj(trkLat[i], trkLon[i]);
                        if (p.X < area.Left - 4 || p.X > area.Right + 4 || p.Y < area.Top - 4 || p.Y > area.Bottom + 4) continue;
                        float d = Math.Max(1.4f, 1.1f * arrowScale);
                        g.FillEllipse(b, p.X - d, p.Y - d, 2 * d, 2 * d);
                    }

            if (haveCrumb)
            {
                var pts = new PointF[crumb.Count];
                for (int i = 0; i < crumb.Count; i++) pts[i] = Proj(crumb[i].lat, crumb[i].lon);
                using var pen = new Pen(Teal, 2f * arrowScale) { LineJoin = LineJoin.Round };
                try { g.DrawLines(pen, pts); } catch { }
            }

            using (var fSt = Theme.Font(labelPt, FontStyle.Bold))
            using (var bDot = new SolidBrush(Blend(Teal, Theme.Text, 0.2f)))
                for (int i = 0; i < stLat.Length; i++)
                {
                    var p = Proj(stLat[i], stLon[i]);
                    if (p.X < area.Left - 30 || p.X > area.Right + 30 || p.Y < area.Top - 10 || p.Y > area.Bottom + 10) continue;
                    float r = 2.6f * arrowScale;
                    g.FillEllipse(bDot, p.X - r, p.Y - r, 2 * r, 2 * r);
                    string nm = i < stName.Length ? stName[i] : null;
                    if (!string.IsNullOrWhiteSpace(nm))
                    {
                        int lw = (int)(labelPt * 22);
                        var lblRect = new Rectangle((int)p.X + (int)(r + 3), (int)p.Y - (int)labelPt, lw, (int)(labelPt * 2));
                        TextRenderer.DrawText(g, nm, fSt, new Rectangle(lblRect.X + 1, lblRect.Y + 1, lblRect.Width, lblRect.Height),
                            Color.FromArgb(190, 0, 0, 0), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
                        TextRenderer.DrawText(g, nm, fSt, lblRect, Blend(Theme.Text, Teal, 0.25f),
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
                    }
                }

            if (hasPos)
            {
                var p = Proj(curLat, curLon);
                var st = g.Save();
                g.TranslateTransform(p.X, p.Y);
                if (hasHeading) g.RotateTransform((float)heading);
                float a = arrowScale;
                var tri = new[] { new PointF(0, -9 * a), new PointF(7 * a, 8 * a), new PointF(0, 4f * a), new PointF(-7 * a, 8 * a) };
                using (var halo = new SolidBrush(Color.FromArgb(60, 120, 200, 140))) g.FillEllipse(halo, -12 * a, -12 * a, 24 * a, 24 * a);
                using (var b = new SolidBrush(Color.FromArgb(120, 210, 150))) g.FillPolygon(b, tri);
                using (var pen = new Pen(Color.FromArgb(20, 40, 24), 1.2f * a)) g.DrawPolygon(pen, tri);
                g.Restore(st);
            }

            g.Clip = oldClip;
        }

        // Caja (lat/lon) de cada tramo de vía, calculada una vez por mapa.
        static double[][] _bbFor;
        static double[] _bbMinLa, _bbMaxLa, _bbMinLo, _bbMaxLo;

        static void EnsureBoxes(double[][] segLat, double[][] segLon)
        {
            if (ReferenceEquals(_bbFor, segLat)) return;
            int n = segLat.Length;
            _bbMinLa = new double[n]; _bbMaxLa = new double[n]; _bbMinLo = new double[n]; _bbMaxLo = new double[n];
            for (int s = 0; s < n; s++)
            {
                double a = double.MaxValue, b = double.MinValue, c = double.MaxValue, d = double.MinValue;
                for (int i = 0; i < segLat[s].Length; i++)
                {
                    double la = segLat[s][i], lo = segLon[s][i];
                    if (la < a) a = la; if (la > b) b = la; if (lo < c) c = lo; if (lo > d) d = lo;
                }
                _bbMinLa[s] = a; _bbMaxLa[s] = b; _bbMinLo[s] = c; _bbMaxLo[s] = d;
            }
            _bbFor = segLat;
        }

        static Color Blend(Color a, Color b, float t)
            => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    // Ventana de MAPA GRANDE con transparencia: reproduce el mismo mapa del HUD a mayor tamaño.
    public class ServiceMapWindow : Form
    {
        readonly ServiceHudOverlay _hud;
        readonly System.Windows.Forms.Timer _tick;
        bool _winDrag, _moved; Point _downScreen; Point _formAtDown;   // arrastre de la VENTANA (por la cabecera)
        bool _panning; Point _lastPan;                                 // desplazamiento del MAPA
        bool _free; double _pxPerM = HudMapRender.BaseScale; double _cLat, _cLon; bool _haveCenter;   // zoom/pan
        Rectangle _hitClose;
        const int HdrH = 30;
        double _lastLat, _lastLon, _lastHdg; int _frames;

        public ServiceMapWindow(ServiceHudOverlay hud)
        {
            _hud = hud;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Surface;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Opacity = 0.82;
            var wa = Screen.PrimaryScreen.WorkingArea;
            int w = Math.Max(560, (int)(wa.Width * 0.55)), h = Math.Max(400, (int)(wa.Height * 0.6));
            Size = new Size(w, h);
            Location = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2);
            ApplyRegion();
            // ~25 fps: sigue a la marca del tren (ya suavizada por el HUD) y redibuja solo si se ha
            // movido; además, un repintado cada medio segundo para lo demás (viajeros, estaciones).
            _tick = new System.Windows.Forms.Timer { Interval = 40 };
            _tick.Tick += (s, e) =>
            {
                var sn = _hud?.Snapshot();
                bool mov = sn != null && sn.HasPos && (sn.CurLat != _lastLat || sn.CurLon != _lastLon || sn.Heading != _lastHdg);
                if (sn != null) { _lastLat = sn.CurLat; _lastLon = sn.CurLon; _lastHdg = sn.Heading; }
                bool toca = ++_frames % 12 == 0;
                if (mov || toca) { UpdateFollow(); Invalidate(); }
            };
            _tick.Start();
        }

        Rectangle MapArea() => new Rectangle(8, HdrH + 4, Width - 16, Height - HdrH - 12);

        // En modo "seguir" (no libre), centra la vista en el tren en cada tick.
        void UpdateFollow()
        {
            if (_free) return;
            var snap = _hud?.Snapshot();
            if (snap != null && snap.HasPos) { _cLat = snap.CurLat; _cLon = snap.CurLon; _haveCenter = true; }
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            // TOOLWINDOW pero SIN NOACTIVATE: así puede recibir la rueda del ratón al hacer clic (para el zoom).
            get { var cp = base.CreateParams; cp.ExStyle |= 0x00000080; return cp; }
        }

        void ApplyRegion()
        {
            using var p = new GraphicsPath();
            int r = 14; var rc = new Rectangle(0, 0, Width, Height);
            p.AddArc(rc.X, rc.Y, r, r, 180, 90); p.AddArc(rc.Right - r, rc.Y, r, r, 270, 90);
            p.AddArc(rc.Right - r, rc.Bottom - r, r, r, 0, 90); p.AddArc(rc.X, rc.Bottom - r, r, r, 90, 90); p.CloseFigure();
            Region = new Region(p);
        }

        protected override void OnMouseEnter(EventArgs e) { Opacity = 0.95; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { if (!_winDrag && !_panning) Opacity = 0.82; base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
            if (e.Y < HdrH) { _winDrag = true; _moved = false; _downScreen = Cursor.Position; _formAtDown = Location; }   // cabecera → mueve la ventana
            else { _panning = true; _lastPan = e.Location; }                                                             // mapa → desplaza el contenido
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_winDrag)
            {
                var now = Cursor.Position; int dx = now.X - _downScreen.X, dy = now.Y - _downScreen.Y;
                if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3) _moved = true;
                Location = new Point(_formAtDown.X + dx, _formAtDown.Y + dy);
            }
            else if (_panning)
            {
                PanBy(e.X - _lastPan.X, e.Y - _lastPan.Y);
                _lastPan = e.Location;
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool wasWinDrag = _winDrag, moved = _moved;
            _winDrag = false; _panning = false; _moved = false;
            if (wasWinDrag && !moved && _hitClose.Contains(e.Location)) { Close(); return; }
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ZoomAt(e.Location, e.Delta > 0 ? 1.2 : 1 / 1.2);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Y >= HdrH) { _free = false; _pxPerM = HudMapRender.BaseScale; UpdateFollow(); Invalidate(); }   // volver a centrar en el tren
            base.OnMouseDoubleClick(e);
        }

        void ZoomAt(Point m, double factor)
        {
            if (!_haveCenter) return;
            _free = true;
            var area = MapArea();
            double cx = area.X + area.Width / 2.0, cy = area.Y + area.Height / 2.0;
            double mLat = 111320.0, mLon = 111320.0 * Math.Cos(_cLat * Math.PI / 180.0);
            double lon = _cLon + (m.X - cx) / (mLon * _pxPerM);
            double lat = _cLat - (m.Y - cy) / (mLat * _pxPerM);
            _pxPerM = Math.Max(0.02, Math.Min(60.0, _pxPerM * factor));
            double mLon2 = 111320.0 * Math.Cos(lat * Math.PI / 180.0);
            _cLon = lon - (m.X - cx) / (mLon2 * _pxPerM);
            _cLat = lat + (m.Y - cy) / (mLat * _pxPerM);
            Invalidate();
        }

        void PanBy(int dx, int dy)
        {
            if (!_haveCenter) return;
            _free = true;
            double mLat = 111320.0, mLon = 111320.0 * Math.Cos(_cLat * Math.PI / 180.0);
            _cLon -= dx / (mLon * _pxPerM);
            _cLat += dy / (mLat * _pxPerM);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var snap = _hud?.Snapshot();
            using (var b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, ClientRectangle);

            // Cabecera (nombre de ruta + pista de uso: rueda para zoom, arrastre para mover, doble clic para centrar)
            string hdr = I18n.T("rueda: zoom · arrastra: mover · doble clic: centrar");
            TextRenderer.DrawText(g, hdr, Theme.Font(9.5f, FontStyle.Bold), new Rectangle(14, 0, Width - 48, HdrH), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            _hitClose = new Rectangle(Width - 30, 7, 16, 16);
            using (var pen = new Pen(Theme.Subtle, 1.8f))
            {
                g.DrawLine(pen, _hitClose.Left + 3, _hitClose.Top + 3, _hitClose.Right - 3, _hitClose.Bottom - 3);
                g.DrawLine(pen, _hitClose.Right - 3, _hitClose.Top + 3, _hitClose.Left + 3, _hitClose.Bottom - 3);
            }
            using (var pen = new Pen(Blend(Theme.Surface, Color.Black, 0.3f))) g.DrawLine(pen, 8, HdrH, Width - 8, HdrH);

            var area = MapArea();
            if (snap == null) return;
            double availW = area.Width - 16;
            double worldWidthM = availW * (1200.0 / 216.0);
            // Vista MANUAL (zoom/pan) si el usuario interactuó; si no, sigue al tren con escala base.
            double vLat = _haveCenter ? _cLat : double.NaN, vLon = _haveCenter ? _cLon : double.NaN;
            double vPx = _haveCenter ? _pxPerM : 0;
            HudMapRender.Draw(g, area, snap.SegLat, snap.SegLon, snap.TrkLat, snap.TrkLon, snap.StLat, snap.StLon, snap.StName,
                snap.Crumb, snap.HasPos, snap.CurLat, snap.CurLon, snap.HasHeading, snap.Heading,
                worldWidthM, 9.5f, 2.0f, I18n.T("Cargando mapa…"), vLat, vLon, vPx);
        }

        protected override void OnFormClosing(FormClosingEventArgs e) { try { _tick?.Stop(); _tick?.Dispose(); } catch { } base.OnFormClosing(e); }

        static Color Blend(Color a, Color b, float t)
            => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
}
