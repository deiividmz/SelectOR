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
using System.Linq;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    // Rastro del tren en el mapa durante TODA la conducción, desde el origen. Lo guarda la ventana
    // principal (no el HUD): así no se pierde al cerrar y reabrir el HUD, al ponerse de servicio ni al
    // registrar el servicio a mitad de viaje. Un punto cada 8 m; si pasa de MaxPoints, en lugar de
    // borrar lo más antiguo se deja uno de cada dos en el tramo viejo (los últimos KeepRecent, enteros):
    // el rastro sigue entero desde la salida, solo con menos detalle en lo lejano.
    public sealed class DriveTrail
    {
        public const int MaxPoints = 12000, KeepRecent = 2000;
        public readonly List<(double lat, double lon)> Points = new();

        public void Clear() => Points.Clear();

        /// <summary>Añade la posición si el tren se ha movido más de 8 m desde la última. true = añadida.</summary>
        public bool Add(double lat, double lon)
        {
            if (Points.Count > 0)
            {
                var (la, lo) = Points[^1];
                double dLat = (lat - la) * Math.PI / 180, dLon = (lon - lo) * Math.PI / 180;
                double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                           Math.Cos(la * Math.PI / 180) * Math.Cos(lat * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
                if (6371000.0 * 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h)) <= 8) return false;
            }
            Points.Add((lat, lon));
            if (Points.Count > MaxPoints) Thin();
            return true;
        }

        // Aclara el tramo antiguo por DISTANCIA: se queda con unos MaxPoints/2 puntos repartidos a la misma
        // separación a lo largo de todo ese tramo (no «uno de cada dos» en cada pasada, que dejaba el
        // principio de un viaje largo casi sin puntos). La salida y los últimos KeepRecent quedan siempre.
        void Thin()
        {
            int old = Points.Count - KeepRecent;
            double largo = 0;
            for (int i = 1; i <= old; i++) largo += Meters(Points[i - 1], Points[i]);
            double paso = largo / (MaxPoints / 2);
            var res = new List<(double lat, double lon)>(MaxPoints) { Points[0] };
            double acum = 0;
            for (int i = 1; i < old; i++)
            {
                acum += Meters(Points[i - 1], Points[i]);
                if (acum >= paso) { res.Add(Points[i]); acum = 0; }
            }
            res.AddRange(Points.GetRange(old, KeepRecent));
            Points.Clear();
            Points.AddRange(res);
        }

        static double Meters((double lat, double lon) a, (double lat, double lon) b)
        {
            double dLat = (b.lat - a.lat) * Math.PI / 180, dLon = (b.lon - a.lon) * Math.PI / 180;
            double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(a.lat * Math.PI / 180) * Math.Cos(b.lat * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 6371000.0 * 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
        }
    }

    public class ServiceHudOverlay : Form
    {
        readonly string _company, _train, _route;
        readonly Image _logo;
        readonly Func<DateTime?> _startUtc;   // inicio del cronómetro (null = el escenario aún está cargando)
        public Func<bool> Paused;             // Open Rails en pausa: el cronómetro está parado
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
        readonly DriveTrail _trail;                               // rastro recorrido (de la ventana principal)
        readonly List<(double lat, double lon)> _crumb;           // = _trail.Points
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
        // Mapa en vivo: los demás usuarios de la comunidad en esta ruta, y quién soy yo (nombre y
        // empresa de servicio, o null en conducción libre) para la leyenda del mapa grande.
        public Func<List<LiveMarker>> Mates;
        public Func<(string name, string company)> Me;
        List<LiveMarker> CurMates() { try { return Mates?.Invoke(); } catch { return null; } }
        ServiceMapWindow _bigMap;   // ventana de mapa grande con transparencia
        // Hoja de ruta (itinerario marcado en el mapa grande; lo lleva la ventana principal).
        public RoadBook Book;
        public Action BookEnsureGraph, BookChanged;
        // Itinerarios guardados (MainMenuForm.Itinerarios.cs): lista de la ruta, guardar, cargar y borrar.
        public Func<IReadOnlyList<SavedItinerary>> SavedList;
        public Func<IWin32Window, string> SaveBook;
        public Func<SavedItinerary, string> LoadSaved;
        public Action<SavedItinerary> DeleteSaved;
        public void MapNotice(string msg) { try { if (_bigMap != null && !_bigMap.IsDisposed) _bigMap.Notice(msg); } catch { } }

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
                                 Action paToggle = null,
                                 DriveTrail trail = null)
        {
            _trail = trail ?? new DriveTrail(); _crumb = _trail.Points;
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
            if (!moved) { var mm = CurMates(); moved = mm != null && mm.Count > 0; }   // los demás también se mueven
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
                        if (_trail.Add(lat, lon))
                        {
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
                bool paused = false; try { paused = Paused?.Invoke() ?? false; } catch { }
                // Quieto mientras carga el escenario; en pausa, fijo (sin parpadear).
                DrawDot(g, 15, timerRect.Top + timerRect.Height / 2, Rec, running && (paused || _blink));
                using var fT = Theme.Font(15f, FontStyle.Bold);
                TextRenderer.DrawText(g, Elapsed(), fT, timerRect, paused ? Theme.Subtle : Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (_km != null || paused)
                {
                    int tw = TextRenderer.MeasureText(g, Elapsed(), fT, Size.Empty, TextFormatFlags.NoPadding).Width;
                    string extra = paused ? "  ·  " + I18n.T("EN PAUSA") : "  ·  " + KmText();
                    TextRenderer.DrawText(g, extra, Theme.Font(10f, FontStyle.Bold),
                        new Rectangle(28 + tw, top + 36, Width - 28 - tw - 8, 28), paused ? Theme.Gold : Blend(Theme.Text, Theme.Subtle, 0.35f),
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
            var mates = CurMates();
            HudMapRender.Draw(g, area, _segLat, _segLon, _trkLat, _trkLon, _stLat, _stLon, _stName, _crumb,
                _hasPos, _curLat, _curLon, _hasHeading, _heading, anchoM, 7.5f, 1f, I18n.T("Cargando mapa…"), mates: mates, book: Book,
                detail: _detail);   // el mismo detalle del .tdb que el mapa grande
            // Cuántos usuarios hay en la ruta (arriba a la derecha; mientras se enseña el zoom, no).
            if (mates != null && mates.Count > 0 && DateTime.UtcNow >= _zoomShownUntil) LiveMapDraw.Chip(g, area, mates.Count);

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
            Route = _route, Mates = CurMates(), Me = MeInfo(), Detail = _detail
        };

        // Detalle del .tdb para el mapa grande (lo calcula MainMenuForm una vez por ruta)
        HudMapDetail _detail;
        public void SetDetail(HudMapDetail d) { _detail = d; }
        public HudMapDetail Detail => _detail;
        // Próxima señal: su color y a cuántos metros (lo lee MainMenuForm del Track Monitor con el mapa grande abierto)

        (string name, string company) MeInfo() { try { return Me?.Invoke() ?? default; } catch { return default; } }

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
        internal AppPrefs Prefs => _prefs;
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
        public HudMapDetail Detail;                     // detalle del .tdb (solo lo dibuja el mapa grande)
        public List<LiveMarker> Mates;                  // los demás usuarios de la ruta (o null)
        public (string name, string company) Me;        // para la leyenda del mapa grande
    }

    // Renderizador COMPARTIDO del mapa (lo usan el mini-mapa del HUD y la ventana grande → idénticos).
    static class HudMapRender
    {
        static readonly Color Teal = Color.FromArgb(94, 190, 155);
        public static readonly Color ItineraryCol = Color.FromArgb(240, 180, 70);   // itinerario de la hoja de ruta (el ámbar de siempre)

        public const double BaseScale = 216.0 / 1200.0;   // px por metro del mini-mapa (para zoom coherente)

        // Devuelve la proyección inversa (píxel → lat/lon) de lo dibujado, o null si no se dibujó el mapa.
        public static Func<PointF, (double lat, double lon)> Draw(Graphics g, Rectangle area,
            double[][] segLat, double[][] segLon, double[] trkLat, double[] trkLon,
            double[] stLat, double[] stLon, string[] stName,
            System.Collections.Generic.IReadOnlyList<(double lat, double lon)> crumb,
            bool hasPos, double curLat, double curLon, bool hasHeading, double heading,
            double worldWidthM, float labelPt, float arrowScale, string loadingText,
            double viewCenterLat = double.NaN, double viewCenterLon = double.NaN, double viewPxPerM = 0,
            IReadOnlyList<LiveMarker> mates = null, bool mateDetail = false, RoadBook book = null,
            HudMapDetail detail = null)
        {
            segLat ??= Array.Empty<double[]>(); segLon ??= Array.Empty<double[]>();
            trkLat ??= Array.Empty<double>(); trkLon ??= Array.Empty<double>();
            stLat ??= Array.Empty<double>(); stLon ??= Array.Empty<double>(); stName ??= Array.Empty<string>();
            bool haveSeg = segLat.Length > 0;
            bool haveTrk = trkLat.Length >= 2;
            bool haveCrumb = crumb != null && crumb.Count >= 2;
            if (!haveSeg && !haveTrk && !haveCrumb && !hasPos && (mates == null || mates.Count == 0))
            {
                using var f0 = Theme.Font(Math.Max(9f, labelPt));
                TextRenderer.DrawText(g, loadingText, f0, area, Theme.Subtle, TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return null;
            }

            int pad = 8;
            double availW = area.Width - 2 * pad, availH = area.Height - 2 * pad;
            Func<double, double, PointF> Proj;
            Func<PointF, (double lat, double lon)> Inv;
            if (viewPxPerM > 0 && !double.IsNaN(viewCenterLat))
            {
                // VISTA MANUAL (zoom/pan del usuario): centrada en (viewCenterLat/Lon) con escala viewPxPerM.
                double mLat = 111320.0, mLon = 111320.0 * Math.Cos(viewCenterLat * Math.PI / 180.0);
                double cx = area.X + area.Width / 2.0, cy = area.Y + area.Height / 2.0;
                Proj = (la, lo) => new PointF((float)(cx + (lo - viewCenterLon) * mLon * viewPxPerM), (float)(cy - (la - viewCenterLat) * mLat * viewPxPerM));
                Inv = p => (viewCenterLat - (p.Y - cy) / (mLat * viewPxPerM), viewCenterLon + (p.X - cx) / (mLon * viewPxPerM));
            }
            else if (hasPos)
            {
                double mLat = 111320.0, mLon = 111320.0 * Math.Cos(curLat * Math.PI / 180.0);
                double sc = availW / worldWidthM;
                double cx = area.X + area.Width / 2.0, cy = area.Y + area.Height / 2.0;
                Proj = (la, lo) => new PointF((float)(cx + (lo - curLon) * mLon * sc), (float)(cy - (la - curLat) * mLat * sc));
                Inv = p => (curLat - (p.Y - cy) / (mLat * sc), curLon + (p.X - cx) / (mLon * sc));
            }
            else
            {
                double minLat = double.MaxValue, maxLat = double.MinValue, minLon = double.MaxValue, maxLon = double.MinValue;
                void Ext(double la, double lo) { if (la < minLat) minLat = la; if (la > maxLat) maxLat = la; if (lo < minLon) minLon = lo; if (lo > maxLon) maxLon = lo; }
                if (haveSeg) for (int s = 0; s < segLat.Length; s++) for (int i = 0; i < segLat[s].Length; i++) Ext(segLat[s][i], segLon[s][i]);
                else if (haveTrk) for (int i = 0; i < trkLat.Length; i++) Ext(trkLat[i], trkLon[i]);
                else foreach (var c in crumb) Ext(c.lat, c.lon);
                if (minLat > maxLat) return null;
                double lat0 = (minLat + maxLat) / 2.0;
                double k = Math.Cos(lat0 * Math.PI / 180.0); if (k < 0.01) k = 0.01;
                double spanX = Math.Max((maxLon - minLon) * k, 1e-6), spanY = Math.Max(maxLat - minLat, 1e-6);
                double sc = Math.Min(availW / spanX, availH / spanY);
                double ox = area.X + pad + (availW - spanX * sc) / 2.0, oy = area.Y + pad + (availH - spanY * sc) / 2.0;
                Proj = (la, lo) => new PointF((float)(ox + (lo - minLon) * k * sc), (float)(oy + (maxLat - la) * sc));
                Inv = p => (maxLat - (p.Y - oy) / sc, minLon + (p.X - ox) / (k * sc));
            }

            var oldClip = g.Clip; g.SetClip(area);

            // Con el detalle del .tdb, la vía la dibuja MapDetailDraw, más gruesa: el trazado fino solo hasta que llega.
            if (haveSeg && detail == null)
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
            else if (haveTrk && detail == null)
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

            // Detalle del .tdb (mini-mapa y mapa grande): vía, andenes, apartaderos, desvíos, cruces, PK…
            double pxPerM = 0;
            if (detail != null)
            {
                var c0 = Inv(new PointF(area.X + area.Width / 2f, area.Y + area.Height / 2f));
                var q0 = Proj(c0.lat, c0.lon); var q1 = Proj(c0.lat + 0.001, c0.lon);
                pxPerM = Math.Abs(q1.Y - q0.Y) / 111.32;
                MapDetailDraw.Under(g, area, Proj, detail, pxPerM, arrowScale);
            }

            // Itinerario de la hoja de ruta, encima del detalle: lo que falta en ámbar, lo recorrido más apagado, y los
            // puntos marcados (el formato de siempre; se corta justo bajo la flecha del tren).
            if (book != null && book.PathLat.Length >= 2)
            {
                var pts = new PointF[book.PathLat.Length];
                for (int i = 0; i < pts.Length; i++) pts[i] = Proj(book.PathLat[i], book.PathLon[i]);
                // corte exacto bajo la flecha del tren (la misma posición que se dibuja), no en el vértice anterior
                double dp = book.DrawProgress(hasPos, curLat, curLon);
                int i0 = book.VertexAfter(dp);
                var (cutLa, cutLo) = book.LatLonAt(dp); var cut = Proj(cutLa, cutLo);
                var rest = new List<PointF>(pts.Length - i0 + 1) { cut }; for (int i = i0; i < pts.Length; i++) rest.Add(pts[i]);
                var past = new List<PointF>(i0 + 1); for (int i = 0; i < i0; i++) past.Add(pts[i]); past.Add(cut);
                float kb = Math.Max(1f, arrowScale * 0.7f);   // (el formato de siempre: halo suave y trazo de 3,2)
                if (rest.Count >= 2)
                    using (var halo = new Pen(Color.FromArgb(70, 0, 0, 0), 6f * kb) { LineJoin = LineJoin.Round, StartCap = LineCap.Flat, EndCap = LineCap.Round })
                        try { g.DrawLines(halo, rest.ToArray()); } catch { }
                if (past.Count >= 2)
                    using (var pen = new Pen(Color.FromArgb(150, 150, 130, 90), 3.2f * kb) { LineJoin = LineJoin.Round })
                        try { g.DrawLines(pen, past.ToArray()); } catch { }
                if (rest.Count >= 2)
                    using (var pen = new Pen(Color.FromArgb(235, ItineraryCol), 3.2f * kb) { LineJoin = LineJoin.Round, StartCap = LineCap.Flat, EndCap = LineCap.Round })
                        try { g.DrawLines(pen, rest.ToArray()); } catch { }
            }
            if (book != null && book.HasPlan)
            {
                float rs = 5f * Math.Max(1f, arrowScale * 0.6f);
                using var fill = new SolidBrush(Color.FromArgb(255, 245, 225));
                using var ring = new Pen(ItineraryCol, 2.4f * Math.Max(1f, arrowScale * 0.5f));
                foreach (var st in book.Stops)
                {
                    if (st.IsEnd || st.Passed || !(st.Halt || st.IsReverse)) continue;
                    var p = Proj(st.Lat, st.Lon);
                    if (p.X < area.Left - 10 || p.X > area.Right + 10 || p.Y < area.Top - 10 || p.Y > area.Bottom + 10) continue;
                    g.FillEllipse(fill, p.X - rs, p.Y - rs, 2 * rs, 2 * rs);
                    g.DrawEllipse(ring, p.X - rs, p.Y - rs, 2 * rs, 2 * rs);
                }
            }
            if (book != null && book.Points.Count > 0)
                using (var fN = Theme.Font(Math.Max(7f, labelPt * 0.85f), FontStyle.Bold))
                    for (int i = 0, num = 0; i < book.Points.Count; i++)
                    {
                        if (book.Points[i].Guide) continue;   // puntos de paso del recorrido (.pat): no se dibujan
                        num++;
                        var p = Proj(book.Points[i].Lat, book.Points[i].Lon);
                        float r = 7f * Math.Max(1f, arrowScale * 0.6f);
                        bool hecho = i < book.ReachedPoints;
                        using (var b = new SolidBrush(hecho ? Color.FromArgb(150, 130, 110) : ItineraryCol)) g.FillEllipse(b, p.X - r, p.Y - r, 2 * r, 2 * r);
                        using (var pen = new Pen(Color.FromArgb(40, 30, 10), 1.4f)) g.DrawEllipse(pen, p.X - r, p.Y - r, 2 * r, 2 * r);
                        TextRenderer.DrawText(g, num.ToString(), fN, Rectangle.Round(new RectangleF(p.X - r, p.Y - r, 2 * r, 2 * r)), Color.FromArgb(30, 22, 8),
                            TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }

            if (haveCrumb)
            {
                var pts = new PointF[crumb.Count];
                for (int i = 0; i < crumb.Count; i++) pts[i] = Proj(crumb[i].lat, crumb[i].lon);
                using var pen = new Pen(Teal, 2f * arrowScale) { LineJoin = LineJoin.Round };
                try { g.DrawLines(pen, pts); } catch { }
            }

            using (var fSt = Theme.Font(labelPt, FontStyle.Bold))
            using (var bDot = new SolidBrush(detail != null ? MapDetailDraw.PlatformEdge : Blend(Teal, Theme.Text, 0.2f)))
            using (var ringDot = new Pen(MapDetailDraw.PlatformCol, 1.4f))
                for (int i = 0; i < stLat.Length; i++)
                {
                    var p = Proj(stLat[i], stLon[i]);
                    if (p.X < area.Left - 30 || p.X > area.Right + 30 || p.Y < area.Top - 10 || p.Y > area.Bottom + 10) continue;
                    float r = 2.6f * arrowScale;
                    g.FillEllipse(bDot, p.X - r, p.Y - r, 2 * r, 2 * r);
                    if (detail != null) g.DrawEllipse(ringDot, p.X - r, p.Y - r, 2 * r, 2 * r);
                    string nm = i < stName.Length ? stName[i] : null;
                    // Con el detalle del .tdb: el nombre en una etiqueta oscura con borde azul (el de los andenes), legible
                    // sobre la vía; sin él, como siempre (texto con sombra).
                    if (detail != null && !string.IsNullOrWhiteSpace(nm))
                    {
                        MapDetailDraw.Label(g, fSt, new PointF(p.X + r - 3, p.Y - fSt.Height / 2f - 6), nm, Color.FromArgb(236, 240, 244), MapDetailDraw.PlatformCol, null);
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(nm))
                    {
                        int lw = (int)(labelPt * 22);
                        var lblRect = new Rectangle((int)p.X + (int)(r + 3), (int)p.Y - (int)labelPt, lw, (int)(labelPt * 2));
                        TextRenderer.DrawText(g, nm, fSt, new Rectangle(lblRect.X + 1, lblRect.Y + 1, lblRect.Width, lblRect.Height),
                            Color.FromArgb(190, 0, 0, 0), TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
                        TextRenderer.DrawText(g, nm, fSt, lblRect, Blend(Theme.Text, Teal, 0.25f),
                            TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
                    }
                }

            // Los demás usuarios de la ruta, debajo de la flecha propia.
            if (mates != null && mates.Count > 0)
                LiveMapDraw.Draw(g, area, Proj, mates, arrowScale, mateDetail, stLat, stLon, stName);

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
            return Inv;
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
        bool _resizing; Size _sizeAtDown; Rectangle _hitGrip;          // tamaño de la VENTANA (por la esquina inferior derecha)
        Rectangle _hitKey; bool _keyDown;                              // leyenda del detalle (clic: plegar / desplegar)
        const int MinW = 420, MinH = 300;
        bool _free; double _pxPerM = HudMapRender.BaseScale; double _cLat, _cLon; bool _haveCenter;   // zoom/pan
        Rectangle _hitClose;
        // Hoja de ruta: botones de la cabecera y clic en la vía para marcar el itinerario.
        Rectangle _hitRoute, _hitUndo, _hitClearRoute, _hitSave, _hitSaved; int _hoverHdr = -1;
        // Pestaña «Guardados»: lista de itinerarios guardados de la ruta (clic: cargar; ✕ dos veces: borrar).
        bool _savedOpen, _savedDown; int _savedScroll;
        Rectangle _savedPanel; List<(Rectangle row, Rectangle del, SavedItinerary it)> _savedHits = new();
        string _confirmDelId; DateTime _confirmDelUntil;
        string _notice; DateTime _noticeUntil;   // aviso breve sobre el mapa (guardado, cargado…)
        public void Notice(string msg) { if (string.IsNullOrWhiteSpace(msg)) return; _notice = msg; _noticeUntil = DateTime.UtcNow.AddSeconds(5); Invalidate(); }
        Func<PointF, (double lat, double lon)> _inv;   // píxel → lat/lon del último dibujo
        bool _panMoved; Point _panDown;
        RoadBook Book => _hud?.Book;
        // Leyenda de los demás usuarios de la ruta: su caja y cada fila (clic → centrar en ese usuario).
        Rectangle _legendBox; List<(Rectangle hit, LiveMarker m)> _legendHits = new();
        bool _legendDown;
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
            // Tamaño y sitio en que lo dejaste (dentro de la pantalla); la primera vez, centrado.
            var wa = Screen.PrimaryScreen.WorkingArea;
            var pr = hud?.Prefs;
            int w = Math.Max(560, (int)(wa.Width * 0.55)), h = Math.Max(400, (int)(wa.Height * 0.6));
            if (pr != null && pr.BigMapW > 0 && pr.BigMapH > 0) { w = pr.BigMapW; h = pr.BigMapH; }
            w = Math.Max(MinW, Math.Min(w, wa.Width)); h = Math.Max(MinH, Math.Min(h, wa.Height));
            Size = new Size(w, h);
            Location = pr != null && pr.BigMapX >= 0 && pr.BigMapY >= 0
                ? new Point(Math.Min(Math.Max(pr.BigMapX, wa.Left), wa.Right - w), Math.Min(Math.Max(pr.BigMapY, wa.Top), wa.Bottom - h))
                : new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2);
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
                bool otros = sn?.Mates != null && sn.Mates.Count > 0;   // los demás se deslizan también
                if (mov || toca || otros) { UpdateFollow(); Invalidate(); }
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

        protected override void OnResize(EventArgs e) { base.OnResize(e); ApplyRegion(); Invalidate(); }

        void SavePlace()
        {
            var pr = _hud?.Prefs; if (pr == null) return;
            pr.BigMapX = Left; pr.BigMapY = Top; pr.BigMapW = Width; pr.BigMapH = Height;
            try { pr.Save(); } catch { }
        }

        protected override void OnMouseEnter(EventArgs e) { Opacity = 0.95; base.OnMouseEnter(e); }
        // Al sacar el ratón, el teclado vuelve al simulador: si se quedaba aquí, F2 (guardar), F9 o los
        // mandos del tren no le llegaban a Open Rails.
        protected override void OnMouseLeave(EventArgs e)
        {
            if (!_winDrag && !_panning && !_resizing) { Opacity = 0.82; OrControl.ReturnFocusIfOurs(); }
            base.OnMouseLeave(e);
        }

        // Una tecla pulsada con el mapa delante es para el simulador: se le reenvía (y se queda con el teclado).
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var k = keyData & Keys.KeyCode;
            if (k != Keys.ShiftKey && k != Keys.ControlKey && k != Keys.Menu && k != Keys.None)
            {
                OrControl.ForwardKey((int)k, (keyData & Keys.Shift) != 0, (keyData & Keys.Control) != 0, (keyData & Keys.Alt) != 0);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
            if (_hitGrip.Contains(e.Location)) { _resizing = true; _downScreen = Cursor.Position; _sizeAtDown = Size; }   // esquina → tamaño
            else if (_hitKey.Contains(e.Location)) _keyDown = true;                                                          // leyenda → plegar
            else if (_savedOpen && _savedPanel.Contains(e.Location)) _savedDown = true;                                      // pestaña «Guardados»
            else if (e.Y < HdrH) { _winDrag = true; _moved = false; _downScreen = Cursor.Position; _formAtDown = Location; }   // cabecera → mueve la ventana
            else if (_legendBox.Contains(e.Location)) _legendDown = true;                                                // leyenda → clic en un nombre
            else { _panning = true; _lastPan = e.Location; _panDown = e.Location; _panMoved = false; }                    // mapa → desplaza el contenido
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_resizing)
            {
                var now = Cursor.Position;
                int w = _sizeAtDown.Width + now.X - _downScreen.X, h = _sizeAtDown.Height + now.Y - _downScreen.Y;
                try
                {
                    var wa = Screen.FromControl(this).WorkingArea;   // nunca más allá de la pantalla
                    w = Math.Min(w, wa.Right - Left); h = Math.Min(h, wa.Bottom - Top);
                }
                catch { }
                w = Math.Max(MinW, w); h = Math.Max(MinH, h);
                if (w != Width || h != Height) Size = new Size(w, h);
            }
            else if (_winDrag)
            {
                var now = Cursor.Position; int dx = now.X - _downScreen.X, dy = now.Y - _downScreen.Y;
                if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3) _moved = true;
                Location = new Point(_formAtDown.X + dx, _formAtDown.Y + dy);
            }
            else if (_panning)
            {
                if (!_panMoved && (Math.Abs(e.X - _panDown.X) > 4 || Math.Abs(e.Y - _panDown.Y) > 4)) _panMoved = true;
                if (_panMoved) { PanBy(e.X - _lastPan.X, e.Y - _lastPan.Y); _lastPan = e.Location; }
            }
            else
            {
                bool sobre = false;
                foreach (var (hit, _) in _legendHits) if (hit.Contains(e.Location)) { sobre = true; break; }
                int hh = HdrZone(e.Location);
                if (hh != _hoverHdr) { _hoverHdr = hh; Invalidate(new Rectangle(0, 0, Width, HdrH)); }
                Cursor = _hitGrip.Contains(e.Location) ? Cursors.SizeNWSE : _hitKey.Contains(e.Location) ? Cursors.Hand
                       : sobre || hh >= 0 ? Cursors.Hand : (Book?.Editing == true && e.Y > HdrH ? Cursors.Cross : Cursors.Default);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool wasWinDrag = _winDrag, moved = _moved, legend = _legendDown, wasPan = _panning, panMoved = _panMoved;
            if (_resizing) { _resizing = false; SavePlace(); Invalidate(); base.OnMouseUp(e); return; }
            if (_keyDown)
            {
                _keyDown = false;
                var pr = _hud?.Prefs;
                if (pr != null && _hitKey.Contains(e.Location)) { pr.BigMapKey = !pr.BigMapKey; try { pr.Save(); } catch { } Invalidate(); }
                base.OnMouseUp(e); return;
            }
            if (_savedDown)
            {
                _savedDown = false;
                SavedClick(e.Location);
                base.OnMouseUp(e); return;
            }
            if (wasWinDrag && moved) SavePlace();
            _winDrag = false; _panning = false; _moved = false; _legendDown = false; _panMoved = false;
            if (wasWinDrag && !moved && _hitClose.Contains(e.Location)) { Close(); return; }
            if (wasWinDrag && !moved && HdrZone(e.Location) is int z && z >= 0) { HdrClick(z); return; }
            // modo «Itinerario»: un clic (sin arrastrar) sobre la vía añade un punto
            if (wasPan && !panMoved && Book is RoadBook rb && rb.Editing && _inv != null && e.Button == MouseButtons.Left)
            {
                if (rb.Graph == null) { _hud?.BookEnsureGraph?.Invoke(); Invalidate(); }
                else
                {
                    var a = _inv(new PointF(e.X, e.Y)); var b = _inv(new PointF(e.X + 30, e.Y));
                    double maxM = Math.Max(15, Haversine(a.lat, a.lon, b.lat, b.lon));   // 30 px de margen
                    if (rb.AddPoint(a.lat, a.lon, maxM)) { try { _hud?.BookChanged?.Invoke(); } catch { } }
                    Invalidate();
                }
                base.OnMouseUp(e);
                return;
            }
            if (legend)
                foreach (var (hit, m) in _legendHits)
                    if (hit.Contains(e.Location))
                    {
                        // Centra el mapa en ese usuario (vista libre: doble clic vuelve a seguir a tu tren).
                        _free = true; _cLat = m.Lat; _cLon = m.Lon; _haveCenter = true; Invalidate();
                        break;
                    }
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (_savedOpen && _savedPanel.Contains(e.Location)) { _savedScroll = Math.Max(0, _savedScroll - Math.Sign(e.Delta)); Invalidate(); base.OnMouseWheel(e); return; }
            ZoomAt(e.Location, e.Delta > 0 ? 1.2 : 1 / 1.2);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Y >= HdrH && !_legendBox.Contains(e.Location) && !(_savedOpen && _savedPanel.Contains(e.Location))) { _free = false; _pxPerM = HudMapRender.BaseScale; UpdateFollow(); Invalidate(); }   // volver a centrar en el tren
            base.OnMouseDoubleClick(e);
        }

        int HdrZone(Point p)
        {
            if (p.Y >= HdrH) return -1;
            if (_hitRoute.Contains(p)) return 0;
            if (_hitUndo.Contains(p)) return 1;
            if (_hitClearRoute.Contains(p)) return 2;
            if (_hitSave.Contains(p)) return 3;
            if (_hitSaved.Contains(p)) return 4;
            return -1;
        }

        void HdrClick(int z)
        {
            var rb = Book; if (rb == null) return;
            if (z == 0) { rb.Editing = !rb.Editing; if (rb.Editing) _hud?.BookEnsureGraph?.Invoke(); }
            else if (z == 1) { rb.Undo(); try { _hud?.BookChanged?.Invoke(); } catch { } }
            else if (z == 2) { rb.ClearAll(); try { _hud?.BookChanged?.Invoke(); } catch { } }
            else if (z == 3) { string m = null; try { m = _hud?.SaveBook?.Invoke(this); } catch { } Notice(m); }
            else if (z == 4) { _savedOpen = !_savedOpen; _savedScroll = 0; _confirmDelId = null; }
            Invalidate();
        }

        static double Haversine(double la1, double lo1, double la2, double lo2)
        {
            double dLa = (la2 - la1) * Math.PI / 180.0, dLo = (lo2 - lo1) * Math.PI / 180.0;
            double a = Math.Sin(dLa / 2) * Math.Sin(dLa / 2) + Math.Cos(la1 * Math.PI / 180) * Math.Cos(la2 * Math.PI / 180) * Math.Sin(dLo / 2) * Math.Sin(dLo / 2);
            return 2 * 6371000.0 * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }

        void DrawHdrButton(Graphics g, Rectangle r, string text, bool active, bool hover)
        {
            var amber = Color.FromArgb(240, 180, 70);
            var fill = active ? Blend(Theme.Surface2, amber, 0.25f) : hover ? Theme.SurfaceHi : Theme.Surface2;
            using (var b = new SolidBrush(fill)) using (var p = Theme.Round(r, 7)) g.FillPath(b, p);
            if (active) using (var pen = new Pen(amber, 1.2f)) using (var p = Theme.Round(r, 7)) g.DrawPath(pen, p);
            using var f = Theme.Font(8.8f, FontStyle.Bold);
            TextRenderer.DrawText(g, text, f, r, active ? amber : Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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

            // Cabecera: pista de uso (o del modo «Itinerario») + botones de la hoja de ruta + cerrar
            _hitClose = new Rectangle(Width - 30, 7, 16, 16);
            var rb = Book;
            int bx = _hitClose.Left - 10;
            _hitRoute = _hitUndo = _hitClearRoute = _hitSave = _hitSaved = Rectangle.Empty;
            if (rb != null)
            {
                using var fb = Theme.Font(8.8f, FontStyle.Bold);
                int W(string t) => TextRenderer.MeasureText(t, fb).Width + 18;
                string tRoute = rb.Editing ? "✓ " + I18n.T("Listo") : "+ " + I18n.T("Itinerario");
                _hitRoute = new Rectangle(bx - W(tRoute), 4, W(tRoute), HdrH - 8); bx = _hitRoute.Left - 6;
                if (rb.Points.Count > 0)
                {
                    string tClr = I18n.T("Borrar"), tUndo = "\u21B6 " + I18n.T("Deshacer");
                    _hitClearRoute = new Rectangle(bx - W(tClr), 4, W(tClr), HdrH - 8); bx = _hitClearRoute.Left - 6;
                    _hitUndo = new Rectangle(bx - W(tUndo), 4, W(tUndo), HdrH - 8); bx = _hitUndo.Left - 6;
                    DrawHdrButton(g, _hitClearRoute, tClr, false, _hoverHdr == 2);
                    DrawHdrButton(g, _hitUndo, tUndo, false, _hoverHdr == 1);
                    string tSave = I18n.T("Guardar");
                    _hitSave = new Rectangle(bx - W(tSave), 4, W(tSave), HdrH - 8); bx = _hitSave.Left - 6;
                    DrawHdrButton(g, _hitSave, tSave, false, _hoverHdr == 3);
                }
                int nSaved = 0; try { nSaved = _hud?.SavedList?.Invoke()?.Count ?? 0; } catch { }
                string tSaved = "\u2605 " + I18n.T("Guardados") + (nSaved > 0 ? "  " + nSaved : "");
                _hitSaved = new Rectangle(bx - W(tSaved), 4, W(tSaved), HdrH - 8); bx = _hitSaved.Left - 6;
                DrawHdrButton(g, _hitSaved, tSaved, _savedOpen, _hoverHdr == 4);
                DrawHdrButton(g, _hitRoute, tRoute, rb.Editing, _hoverHdr == 0);
            }
            string hdr = rb != null && rb.Editing
                ? (rb.Building ? I18n.T("Preparando el esquema de vías…")
                   : rb.Graph == null && rb.Problem != null ? rb.Problem
                   : I18n.T("Haz clic en la vía para añadir puntos del itinerario"))
                : I18n.T("rueda: zoom · arrastra: mover · doble clic: centrar");
            TextRenderer.DrawText(g, hdr, Theme.Font(9.5f, FontStyle.Bold), new Rectangle(14, 0, bx - 20, HdrH),
                rb != null && rb.Editing ? Color.FromArgb(240, 180, 70) : Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            using (var pen = new Pen(Theme.Subtle, 1.8f))
            {
                g.DrawLine(pen, _hitClose.Left + 3, _hitClose.Top + 3, _hitClose.Right - 3, _hitClose.Bottom - 3);
                g.DrawLine(pen, _hitClose.Right - 3, _hitClose.Top + 3, _hitClose.Left + 3, _hitClose.Bottom - 3);
            }
            using (var pen = new Pen(Blend(Theme.Surface, Color.Black, 0.3f))) g.DrawLine(pen, 8, HdrH, Width - 8, HdrH);

            var area = MapArea();
            if (snap == null) { DrawGrip(g); return; }
            double availW = area.Width - 16;
            double worldWidthM = availW * (1200.0 / 216.0);
            // Vista MANUAL (zoom/pan) si el usuario interactuó; si no, sigue al tren con escala base.
            double vLat = _haveCenter ? _cLat : double.NaN, vLon = _haveCenter ? _cLon : double.NaN;
            double vPx = _haveCenter ? _pxPerM : 0;
            _inv = HudMapRender.Draw(g, area, snap.SegLat, snap.SegLon, snap.TrkLat, snap.TrkLon, snap.StLat, snap.StLon, snap.StName,
                snap.Crumb, snap.HasPos, snap.CurLat, snap.CurLon, snap.HasHeading, snap.Heading,
                worldWidthM, 9.5f, 2.0f, I18n.T("Cargando mapa…"), vLat, vLon, vPx, snap.Mates, mateDetail: true, book: Book,
                detail: snap.Detail);
            DrawKey(g, area, snap.Detail);
            _legendHits = LiveMapDraw.Legend(g, area, snap.Me.name, snap.Me.company, snap.Mates, out _legendBox);
            DrawSaved(g, area);
            DrawNotice(g, area);
            DrawGrip(g);
        }

        // Pestaña «Guardados»: a la derecha del mapa, los itinerarios guardados de esta ruta.
        void DrawSaved(Graphics g, Rectangle area)
        {
            _savedHits.Clear(); _savedPanel = Rectangle.Empty;
            if (!_savedOpen) return;
            IReadOnlyList<SavedItinerary> list = null;
            try { list = _hud?.SavedList?.Invoke(); } catch { }
            list ??= Array.Empty<SavedItinerary>();
            using var fT = Theme.Font(9.5f, FontStyle.Bold); using var fN = Theme.Font(9f, FontStyle.Bold); using var fS = Theme.Font(8.2f);
            const int RowH = 46, Pad = 10;
            int w = Math.Min(330, area.Width - 40);
            int headH = fT.Height + 16;
            int maxRows = Math.Max(1, (area.Height - 16 - headH - 8) / RowH);
            int rows = Math.Max(1, Math.Min(list.Count, maxRows));
            _savedScroll = Math.Max(0, Math.Min(_savedScroll, Math.Max(0, list.Count - maxRows)));
            int h = headH + rows * RowH + 8 + (list.Count == 0 ? 20 : 0);
            _savedPanel = new Rectangle(area.Right - w - 8, area.Top + 8, w, h);
            using (var path = Theme.Round(_savedPanel, 10))
            {
                using (var b = new SolidBrush(Color.FromArgb(242, 30, 33, 36))) g.FillPath(b, path);
                using (var pen = new Pen(Color.FromArgb(120, HudMapRender.ItineraryCol), 1.2f)) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, "★ " + I18n.T("Itinerarios guardados"), fT, new Rectangle(_savedPanel.X + Pad, _savedPanel.Y + 8, w - 2 * Pad, fT.Height + 2),
                HudMapRender.ItineraryCol, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            int y = _savedPanel.Y + headH;
            if (list.Count == 0)
            {
                TextRenderer.DrawText(g, I18n.T("Aún no hay itinerarios guardados en esta ruta. Márcalo con «+ Itinerario» y pulsa «Guardar»."), fS,
                    new Rectangle(_savedPanel.X + Pad, y, w - 2 * Pad, RowH + 16), Theme.Subtle, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                return;
            }
            var ci = I18n.English ? System.Globalization.CultureInfo.GetCultureInfo("en-GB") : System.Globalization.CultureInfo.GetCultureInfo("es-ES");
            var mouse = PointToClient(Cursor.Position);
            for (int i = _savedScroll; i < list.Count && i < _savedScroll + maxRows; i++)
            {
                var it = list[i];
                var row = new Rectangle(_savedPanel.X + 4, y, w - 8, RowH - 4);
                var del = new Rectangle(row.Right - 30, row.Y + (row.Height - 24) / 2, 24, 24);
                bool hov = row.Contains(mouse);
                if (hov) using (var b = new SolidBrush(Theme.SurfaceHi)) using (var p = Theme.Round(row, 7)) g.FillPath(b, p);
                TextRenderer.DrawText(g, it.Name ?? "", fN, new Rectangle(row.X + 8, row.Y + 4, row.Width - 46, fN.Height + 2), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                int pts = it.Points?.Count(p => !p.Guide) ?? 0;
                string sub = string.Format(I18n.T("{0} km · {1} puntos · {2}"), it.Km.ToString("0.#", ci), pts,
                    it.Created.ToString(I18n.English ? "d MMM yyyy" : "d 'de' MMM yyyy", ci));
                TextRenderer.DrawText(g, sub, fS, new Rectangle(row.X + 8, row.Y + 6 + fN.Height, row.Width - 46, fS.Height + 2), Theme.Subtle,
                    TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                bool confirm = _confirmDelId == it.Id && DateTime.UtcNow < _confirmDelUntil;
                var delCol = confirm ? Color.FromArgb(235, 110, 110) : del.Contains(mouse) ? Theme.Text : Theme.Subtle;
                if (confirm) using (var b = new SolidBrush(Color.FromArgb(90, 176, 64, 64))) using (var p = Theme.Round(del, 6)) g.FillPath(b, p);
                using (var pen = new Pen(delCol, 1.6f))
                {
                    g.DrawLine(pen, del.Left + 8, del.Top + 8, del.Right - 8, del.Bottom - 8);
                    g.DrawLine(pen, del.Right - 8, del.Top + 8, del.Left + 8, del.Bottom - 8);
                }
                _savedHits.Add((row, del, it));
                y += RowH;
            }
            if (list.Count > maxRows)
                TextRenderer.DrawText(g, $"{_savedScroll + 1}-{Math.Min(list.Count, _savedScroll + maxRows)} / {list.Count}", fS,
                    new Rectangle(_savedPanel.X, _savedPanel.Bottom - 14, w - Pad, 12), Theme.Subtle, TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }

        void SavedClick(Point pt)
        {
            foreach (var (row, del, it) in _savedHits)
            {
                if (del.Contains(pt))
                {
                    // borrar pide una segunda pulsación
                    if (_confirmDelId == it.Id && DateTime.UtcNow < _confirmDelUntil)
                    {
                        _confirmDelId = null;
                        try { _hud?.DeleteSaved?.Invoke(it); } catch { }
                        Notice(string.Format(I18n.T("Itinerario borrado: {0}"), it.Name));
                    }
                    else { _confirmDelId = it.Id; _confirmDelUntil = DateTime.UtcNow.AddSeconds(4); Notice(I18n.T("Pulsa otra vez la ✕ para borrarlo.")); }
                    Invalidate(); return;
                }
                if (row.Contains(pt))
                {
                    string m = null;
                    try { m = _hud?.LoadSaved?.Invoke(it); } catch { }
                    _savedOpen = false;
                    Notice(m);
                    Invalidate(); return;
                }
            }
        }

        // Aviso breve abajo, en el centro del mapa.
        void DrawNotice(Graphics g, Rectangle area)
        {
            if (string.IsNullOrEmpty(_notice) || DateTime.UtcNow >= _noticeUntil) return;
            using var f = Theme.Font(9.5f, FontStyle.Bold);
            var sz = TextRenderer.MeasureText(_notice, f, new Size(area.Width - 80, 200), TextFormatFlags.WordBreak);
            var r = new Rectangle(area.X + (area.Width - sz.Width - 28) / 2, area.Bottom - sz.Height - 34, sz.Width + 28, sz.Height + 14);
            using (var path = Theme.Round(r, 9))
            {
                using (var b = new SolidBrush(Color.FromArgb(240, 30, 33, 36))) g.FillPath(b, path);
                using (var pen = new Pen(HudMapRender.ItineraryCol, 1.3f)) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, _notice, f, r, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        // Leyenda del detalle del .tdb (arriba a la izquierda): plegada, solo su título.
        void DrawKey(Graphics g, Rectangle area, HudMapDetail d)
        {
            _hitKey = Rectangle.Empty;
            if (d == null) return;
            bool open = _hud?.Prefs?.BigMapKey != false;
            using var fT = Theme.Font(8.5f, FontStyle.Bold); using var f = Theme.Font(8.5f);
            string title = I18n.T("Leyenda") + (open ? "  ▾" : "  ▸");
            var rows = new (string text, int kind, Color c)[]
            {
                (I18n.T("itinerario"), 9, Color.Empty), (I18n.T("andén"), 3, Color.Empty), (I18n.T("apartadero"), 4, Color.Empty),
                (I18n.T("cruce de vías"), 6, Color.Empty), (I18n.T("punto de carga"), 7, Color.Empty), (I18n.T("topera"), 8, Color.Empty),
            };
            int rowH = f.Height + 6, padX = 10;
            int w = TextRenderer.MeasureText(title, fT).Width + 2 * padX;
            foreach (var r in rows) w = Math.Max(w, 34 + TextRenderer.MeasureText(r.text, f).Width + padX);
            int h = 8 + fT.Height + 6 + (open ? rows.Length * rowH + 4 : 0);
            var box = new Rectangle(area.X + 8, area.Y + 8, w, h);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(box, 9)) { using var b = new SolidBrush(Color.FromArgb(235, 30, 33, 36)); g.FillPath(b, path); }
            TextRenderer.DrawText(g, title, fT, new Point(box.X + padX, box.Y + 7), Color.FromArgb(185, 190, 195), TextFormatFlags.NoPadding);
            _hitKey = new Rectangle(box.X, box.Y, box.Width, 8 + fT.Height + 6);
            if (!open) return;
            int y = box.Y + 8 + fT.Height + 8;
            foreach (var (text, kind, c) in rows)
            {
                int cy = y + rowH / 2 - 2;
                switch (kind)
                {
                    case 0: using (var b = new SolidBrush(c)) g.FillRectangle(b, box.X + padX, cy - 2, 18, 5); break;
                    case 3:
                        using (var pen = new Pen(MapDetailDraw.PlatformEdge, 10f) { StartCap = LineCap.Square, EndCap = LineCap.Square }) g.DrawLine(pen, box.X + padX + 4, cy, box.X + padX + 14, cy);
                        using (var pen = new Pen(MapDetailDraw.PlatformCol, 7f) { StartCap = LineCap.Square, EndCap = LineCap.Square }) g.DrawLine(pen, box.X + padX + 4, cy, box.X + padX + 14, cy);
                        using (var pen = new Pen(MapDetailDraw.TrackCol, 2.6f)) g.DrawLine(pen, box.X + padX, cy, box.X + padX + 18, cy);
                        break;
                    case 4:
                        using (var pen = new Pen(MapDetailDraw.SidingCol, 8f) { DashPattern = new[] { 0.7f, 0.45f } }) g.DrawLine(pen, box.X + padX + 1, cy, box.X + padX + 17, cy);
                        using (var pen = new Pen(MapDetailDraw.TrackCol, 2.6f)) g.DrawLine(pen, box.X + padX, cy, box.X + padX + 18, cy);
                        break;
                    case 9:
                        using (var pen = new Pen(Color.FromArgb(70, 0, 0, 0), 6f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, box.X + padX + 2, cy, box.X + padX + 16, cy);
                        using (var pen = new Pen(Color.FromArgb(235, HudMapRender.ItineraryCol), 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, box.X + padX + 2, cy, box.X + padX + 16, cy);
                        break;
                    case 6:
                        using (var pen = new Pen(MapDetailDraw.TrackCol, 2.2f)) { g.DrawLine(pen, box.X + padX, cy - 5, box.X + padX + 18, cy + 5); g.DrawLine(pen, box.X + padX, cy + 5, box.X + padX + 18, cy - 5); }
                        using (var b = new SolidBrush(MapDetailDraw.DiamondCol)) using (var pen = new Pen(Color.FromArgb(40, 44, 48), 1.2f)) MapDetailDraw.Diamond(g, b, pen, new PointF(box.X + padX + 9, cy), 4.6f);
                        break;
                    case 7: MapDetailDraw.Pickup(g, new PointF(box.X + padX + 9, cy), 1f, true); break;
                    case 8:
                        using (var pen = new Pen(MapDetailDraw.TrackCol, 2.6f)) g.DrawLine(pen, box.X + padX, cy, box.X + padX + 12, cy);
                        using (var b = new SolidBrush(MapDetailDraw.EndCol)) using (var pen = new Pen(Color.FromArgb(40, 44, 48), 1f)) { g.FillRectangle(b, box.X + padX + 10, cy - 3, 6, 6); g.DrawRectangle(pen, box.X + padX + 10, cy - 3, 6, 6); }
                        break;
                }
                TextRenderer.DrawText(g, text, f, new Point(box.X + 34, y + 1), Color.FromArgb(230, 232, 234), TextFormatFlags.NoPadding);
                y += rowH;
            }
        }

        // Tirador de la esquina inferior derecha (como el mini-mapa, la hoja de ruta y el chat).
        void DrawGrip(Graphics g)
        {
            _hitGrip = new Rectangle(Width - 20, Height - 20, 20, 20);
            var col = _resizing ? Theme.Accent : Color.FromArgb(150, 255, 255, 255);
            using var pen = new Pen(col, 1.6f);
            for (int k = 4; k <= 12; k += 4) g.DrawLine(pen, Width - 4 - k, Height - 4, Width - 4, Height - 4 - k);
        }

        protected override void OnFormClosing(FormClosingEventArgs e) { try { _tick?.Stop(); _tick?.Dispose(); } catch { } base.OnFormClosing(e); }

        static Color Blend(Color a, Color b, float t)
            => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
}
