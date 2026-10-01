// Formulario principal del selector de trenes y rutas (diseño moderno, toque ferroviario ES).
// Reutiliza las clases de ORTS.Menu para descubrir el contenido y lanza RunActivity.exe.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ORTS.Menu;
using ORTS.Settings;
using SysPath = System.IO.Path;
using OrPath = ORTS.Menu.Path;
using Activity = ORTS.Menu.Activity;

namespace SelectOR
{
    public partial class MainMenuForm : Form
    {
        // ---- Estado de Open Rails ----
        UserSettings _settings;
        List<Folder> _folders = new List<Folder>();
        List<Route> _routesAll = new List<Route>();
        List<TrainItem> _consistsAll = new List<TrainItem>();
        List<Activity> _activitiesAll = new List<Activity>();
        List<OrPath> _pathsAll = new List<OrPath>();
        List<TimetableInfo> _timetablesAll = new List<TimetableInfo>();

        Folder _curFolder;
        Route _curRoute;
        readonly Dictionary<string, List<TrainItem>> _consistCache = new Dictionary<string, List<TrainItem>>();   // (con lock: se escribe desde hilos)

        // Lectura ADELANTADA de la carpeta que viene marcada mientras el usuario elige en la pantalla de
        // inicio (casi siempre elige esa): rutas, índice y trenes. La carga normal espera a que termine
        // antes de tocar nada, así nunca se pisan.
        Task _prefetchTask; string _prefetchPath; List<Route> _prefetchRoutes;

        void StartPrefetch(Folder f)
        {
            _prefetchPath = f.Path;
            _prefetchTask = Task.Run(() =>
            {
                LoadLog("adelantando la carpeta marcada: " + f.Name);
                var rt = Task.Run(() => SafeList(() => Route.GetRoutes(f).OrderBy(r => r.Name).ToList()));
                try
                {
                    ContentIndex.Open(f.Path);
                    // Solo si el índice vale (lectura rápida): leer el contenido entero en frío podría
                    // hacer esperar mucho si al final se elige otra carpeta.
                    if (ContentIndex.Loaded)
                    {
                        var cons = FastConsists.Load(f.Path);
                        if (cons.Count > 0) lock (_consistCache) _consistCache[f.Path] = cons;
                    }
                }
                catch { }
                try { _prefetchRoutes = rt.Result; } catch { }
                LoadLog("carpeta marcada adelantada");
            });
        }

        void WaitPrefetch() { try { _prefetchTask?.Wait(); } catch { } }
        int _folderToken, _routeToken;

        AppPrefs _prefs;
        bool _kiosk;
        int _activePage;

        // ---- Controles principales ----
        ComboBox _cboFolder;
        RoundedInput _routeSearch;
        CheckBox _chkFavOnly;
        ListBox _lstRoutes;
        BannerPanel _banner;
        TextBox _routeDescBox;
        Panel _pageHost;
        RoundButton[] _pills;
        RoundButton _btnPlay, _btnConnect;
        Panel _empDutyHost;   // grupo "Ponerme de servicio" + vehículo en la barra inferior (Empresas)
        Label _lblStatus;

        // Actividad
        RoundedInput _actSearch;
        ListBox _lstActivities;
        TextBox _txtBriefing;
        Panel _pageRuta, _pageActividad, _pageExplora, _pageHorarios, _pageMulti, _pageEditor, _pageEmpresas;
        // Exploración
        RoundedInput _consistSearch;
        CheckBox _chkTrainFavOnly;
        ComboBox _cboTrainCompany;   // filtro de trenes por empresa (Exploración)
        Control _trainCompanyHost;   // fila etiqueta+combo del filtro (se oculta si no hay empresas)
        ListBox _lstConsists;
        ListStatePanel _routesOverlay, _consistsOverlay;   // "Cargando…" / "Sin rutas" / "Sin trenes"

        PageFade _pageFade;      // fundido al cambiar de sección
        bool _consistsLoading;   // trenes (.con) del contenido en camino (lo usa el editor)
        bool _noContent;         // no hay ninguna carpeta de contenido configurada
        TrainPreviewPanel _trainPreview;
        ComboBox _cboStart, _cboEnd;   // "Empezar en" / "Ir hacia": seleccionan el recorrido (.pat)
        HourSlider _hourSlider;
        Segmented _segSeason, _segWeather;
        CheckBox _chkExploreActivity;
        // Horarios
        ComboBox _cboTTSet, _cboTT, _cboTTTrain;
        ComboBox _cboTTCompany;   // filtro de trenes por empresa (Horarios)
        Label _ttCompanyLbl; Panel _ttCompanyHost; TableLayoutPanel _ttGrid; int _ttCompanyRow;   // fila "Empresa" (colapsable)
        Segmented _segTTSeason, _segTTWeather;
        TextBox _txtTTBriefing;
        // Filtro por empresa (Exploración/Horarios)
        const string AllCompaniesLabel = "Todas las empresas";
        bool _companyFilterLoading;
        // Multijugador
        RadioButton _rbClient, _rbServer;
        TextBox _txtMPUser, _txtMPHost, _txtMPPort;
        Label _lblMPInfo;
        string _mpClientHost;      // la IP escrita para unirse (se guarda mientras el campo enseña la IP propia)
        bool _mpShowingOwnIp;      // el campo Host enseña la IP de este equipo (modo Servidor)
        Label _lblMPUserRule;
        ListBox _lstServers;
        Label _lblServers;
        List<GameServer> _serversAll = new List<GameServer>();

        System.Windows.Forms.Timer _revealGuard;   // red de seguridad: muestra el menú si la carga se atasca

        public MainMenuForm(bool kiosk)
        {
            _kiosk = kiosk;
            _prefs = AppPrefs.Load();
            NotifySound.Enabled = () => _prefs?.NotifySound != false;
            try { _settings = new UserSettings(new string[0]); DetectLanguage(); } catch { }
            // Escala de la interfaz según la pantalla, ANTES de montar nada: las fuentes ya se crean
            // a la medida buena y luego se escalan los tamaños fijos de la maqueta.
            try { Theme.ComputeUiScale(Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 900)); } catch { }
            BuildUi();
            if (Math.Abs(Theme.UiScale - 1f) > 0.001f) Scale(new SizeF(Theme.UiScale, Theme.UiScale));
            ApplyScaledWindowLimits();
            LoadLog("interfaz construida");
            LoadStep("ui");
            // La ventana arranca oculta tras la pantalla de inicio, y con ella oculta el evento Load
            // no llega a dispararse: la lectura del contenido se pone en marcha en cuanto existe la
            // ventana nativa (y Load queda como respaldo; StartData solo entra una vez).
            HandleCreated += (s, e) => { try { BeginInvoke((Action)StartData); } catch { StartData(); } };
            // Red de seguridad: si algo se atascase leyendo el contenido, el menú aparece igualmente.
            _revealGuard = new System.Windows.Forms.Timer { Interval = 30000 };
            _revealGuard.Tick += (s, e) => { _revealGuard.Stop(); RevealWindow(); };
            _revealGuard.Start();
            Load += (s, e) => StartData();
            KeyPreview = true;
            KeyDown += OnKeyDown;
        }

        static string Tr(string es) => I18n.T(es);

        void DetectLanguage()
        {
            try
            {
                var lang = _settings?.Language ?? "";
                if (string.IsNullOrEmpty(lang))
                    lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                // SelectOR está en español SOLO si Open Rails está en español; cualquier otro
                // idioma (o ninguno) → inglés.
                I18n.English = !lang.StartsWith("es", StringComparison.OrdinalIgnoreCase);
                // Override opcional de idioma (útil para pruebas): SELECTOR_LANG=es → español, cualquier otro → inglés.
                var force = Environment.GetEnvironmentVariable("SELECTOR_LANG");
                if (!string.IsNullOrEmpty(force)) I18n.English = !force.StartsWith("es", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
        }

        // ============================ UI ============================

        void BuildUi()
        {
            Text = "SelectOR";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterScreen;
            ApplyScaledWindowLimits();
            WindowState = FormWindowState.Maximized;   // arranca maximizada, pero se puede redimensionar
            MaximizeBox = true;
            try { Icon = Icon.ExtractAssociatedIcon((Environment.ProcessPath ?? AppContext.BaseDirectory)); } catch { }

            BuildHeader();
            BuildBottomBar();
            BuildSidebar();
            BuildCenter();

            // Maqueta raíz determinista con TableLayoutPanel (cabecera + cuerpo).
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Theme.Bg };
            _root = root;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SidebarW));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));   // cabecera
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // cuerpo
            _header.Dock = DockStyle.Fill; _sidebar.Dock = DockStyle.Fill; _center.Dock = DockStyle.Fill;
            root.Controls.Add(_header, 0, 0); root.SetColumnSpan(_header, 2);
            root.Controls.Add(_sidebar, 0, 1);
            root.Controls.Add(_center, 1, 1);
            Controls.Add(root);
            ApplySidebarForPage(_activePage);   // por si se arranca directamente en Empresas

            // Barra inferior (CONDUCIR + estado) como hijo del formulario, anclada abajo y posicionada
            // con el tamaño del formulario (fiable gracias a MaximizedBounds).
            _bottomBar.Dock = DockStyle.None;
            _bottomBar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_bottomBar);
            SizeChanged += (s, e) => LayoutBottomBar();
            SizeChanged += (s, e) => OnWindowSizeChanged();   // columna RUTAS y textos adaptables
            Layout += (s, e) => LayoutBottomBar();
            Shown += (s, e) => { ApplyResponsiveChrome(); ApplyAdaptiveTexts(); ApplyTabIcons(); LayoutBottomBar(); };
            Shown += (s, e) => StartUpdateChecks();   // aviso de nueva versión (al arrancar y cada 3 h)            LayoutBottomBar();
        }

        // Secciones con barra inferior: las que llevan CONDUCIR (Actividad, Exploración, Horarios),
        // Multijugador con su CONECTAR y Empresas con «Ponerme de servicio». Ruta y el Editor no
        // tienen ninguna acción ahí, así que ese espacio se lo queda el contenido.
        static bool PageHasBottomBar(int page) => page == 1 || page == 2 || page == 3 || page == 4 || page == PageEmpresas;

        // Alturas que se encogen en pantallas bajas (portátiles de 768 px) y crecen en monitores altos.
        int BottomBarH() => Theme.Px(ClientSize.Height >= Theme.Px(900) ? 80 : ClientSize.Height >= Theme.Px(780) ? 70 : 62);
        int HeaderH() => Theme.Px(ClientSize.Height >= Theme.Px(900) ? 65 : 56);

        // Tamaño mínimo y de arranque: a la escala de la interfaz y SIEMPRE dentro del escritorio, para
        // que en pantallas pequeñas la ventana no acabe siendo más grande que la propia pantalla.
        void ApplyScaledWindowLimits()
        {
            var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
            int minW = Math.Min(Theme.Px(1180), Math.Max(760, work.Width - 40));
            int minH = Math.Min(Theme.Px(720), Math.Max(520, work.Height - 40));
            var min = new Size(minW, minH);
            if (MinimumSize != min) MinimumSize = min;
            var start = new Size(Math.Min(Theme.Px(1280), work.Width - 60), Math.Min(Theme.Px(820), work.Height - 60));
            if (WindowState == FormWindowState.Normal && (ClientSize.Width < start.Width || ClientSize.Height < start.Height))
                ClientSize = start;
        }

        void LayoutBottomBar()
        {
            if (_bottomBar == null) return;
            bool show = PageHasBottomBar(_activePage);
            if (_bottomBar.Visible != show) _bottomBar.Visible = show;
            ApplyCenterPadding();
            if (!show) return;
            int h = BottomBarH();
            int sw = CurrentSidebarW();   // 0 en Empresas (la columna RUTAS se oculta)
            int w = ClientSize.Width, ch = ClientSize.Height;
            if (w <= sw || ch <= h) return;
            var target = new Rectangle(sw, ch - h, w - sw, h);
            if (_bottomBar.Bounds != target) _bottomBar.Bounds = target;
            _bottomBar.BringToFront();
        }

        // El contenido reserva sitio para la barra solo cuando la hay.
        void ApplyCenterPadding()
        {
            if (_center == null) return;
            int bottom = PageHasBottomBar(_activePage) ? BottomBarH() + Theme.Px(4) : Theme.Px(10);
            var pad = new Padding(Theme.Px(12), Theme.Px(6), Theme.Px(16), bottom);
            if (_center.Padding != pad) _center.Padding = pad;
        }

        // Índices de pestaña (el orden lo fija BuildCenter).
        public const int PageEditor = 5, PageEmpresas = 6;

        const int SidebarW = 340;   // ancho de la columna RUTAS (ventana grande / maximizada)
        TableLayoutPanel _root;     // maqueta raíz (cabecera + [RUTAS | contenido])
        // En ventanas estrechas (restaurada) la columna RUTAS cede ancho al contenido.
        int SidebarWidth() => Theme.Px(ClientSize.Width >= Theme.Px(1600) ? SidebarW : ClientSize.Width >= Theme.Px(1400) ? 310 : 280);
        int CurrentSidebarW() => (_sidebar != null && _sidebar.Visible) ? SidebarWidth() : 0;

        // En Empresas la columna RUTAS no sirve: se oculta para dar todo el ancho a las tablas.
        void ApplySidebarForPage(int page)
        {
            bool show = page != PageEditor && page != PageEmpresas;
            if (_sidebar != null && _sidebar.Visible != show) _sidebar.Visible = show;
            if (_root != null && _root.ColumnStyles.Count > 0)
            {
                float w = show ? SidebarWidth() : 0;
                if (_root.ColumnStyles[0].Width != w) _root.ColumnStyles[0].Width = w;
            }
            LayoutBottomBar();
        }

        // ---------- pantalla de inicio ----------
        // La ventana principal no se enseña hasta que el contenido está leído: mientras tanto se ve
        // la pantalla de inicio con el logotipo y su barra de progreso. Cada parte de la carga avisa
        // al terminar con LoadStep(); cuando están todas, se cierra la pantalla y aparece el menú.
        static readonly (string key, int weight, string label)[] LoadSteps =
        {
            ("ui",         8, "Preparando la interfaz…"),
            ("folders",    6, "Leyendo los ajustes de Open Rails…"),
            ("routes",    18, "Leyendo las rutas…"),
            ("consists",  28, "Leyendo los trenes del contenido…"),
            ("engines",   14, "Leyendo los datos de las máquinas…"),
            ("units",     12, "Buscando las formaciones fijas…"),
            ("stock",      7, "Preparando el editor de composiciones…"),
            ("editor",     7, "Preparando el editor de composiciones…"),
        };
        readonly HashSet<string> _loadDone = new(StringComparer.Ordinal);
        bool _uiRevealed;

        // Diagnóstico opcional del arranque: con SELECTOR_LOADLOG=1 se apunta en
        // %TEMP%\selector_load.log cuánto tarda cada parte desde que se abre el programa.
        static void LoadLog(string texto)
        {
            try
            {
                if (Environment.GetEnvironmentVariable("SELECTOR_LOADLOG") != "1") return;
                double ms = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selector_load.log"),
                    $"{ms,8:F0} ms   {texto}{Environment.NewLine}");
            }
            catch { }
        }

        void LoadStep(string key)
        {
            if (_uiRevealed) return;
            LoadLog("paso: " + key);
            lock (_loadDone) { if (!_loadDone.Add(key)) return; }
            int done = 0, total = 0, pending = 0; string next = null;
            foreach (var st in LoadSteps)
            {
                total += st.weight;
                bool ok; lock (_loadDone) ok = _loadDone.Contains(st.key);
                if (ok) done += st.weight;
                else if (next == null) { next = st.label; pending = st.weight; }
            }
            int pct = total > 0 ? (int)Math.Round(done * 100.0 / total) : 100;
            int creep = total > 0 ? (int)Math.Round((done + pending) * 100.0 / total) : 100;
            SplashScreen.Report(pct, creep, next ?? "Todo listo");
            if (next == null)
            {
                // Todo leído: se guarda el índice del contenido para que el próximo arranque vuele.
                Task.Run(() => { try { ContentIndex.Save(); } catch { } });
                RevealWindow();
            }
        }

        // Se acabó la carga (o se ha agotado la espera): fuera la pantalla de inicio, dentro el menú.
        void RevealWindow()
        {
            if (IsHandleCreated && InvokeRequired) { try { BeginInvoke((Action)RevealWindow); } catch { } return; }
            if (_uiRevealed) return;
            _uiRevealed = true;
            // La ventana se enseña ANTES de cerrar la pantalla de inicio: en ese momento el primer
            // plano todavía es nuestro (lo tiene esa pantalla), que es lo que permite a Windows
            // pasárselo al menú. Si se hiciera al revés, el menú se quedaría detrás de otras ventanas.
            try { Show(); BringToFront(); Activate(); } catch { }
            try { Native.ForceForeground(Handle); } catch { }
            try { SplashScreen.Finish(); } catch { }
            try { Activate(); Native.ForceForeground(Handle); } catch { }
        }

        protected override void SetVisibleCore(bool value)
        {
            // Application.Run intenta enseñar la ventana nada más arrancar. Se le dice que todavía no,
            // pero creando ya su ventana nativa para que la interfaz se prepare mientras carga.
            if (value && !_uiRevealed)
            {
                if (!IsHandleCreated) CreateHandle();
                base.SetVisibleCore(false);
                return;
            }
            base.SetVisibleCore(value);
        }

        // Textos que se acortan cuando la ventana es estrecha (restaurada) para que no se corten.
        // «reserva» descuenta un hueco que el control puede perder en cualquier momento (el botón
        // «Comprar este tren», que aparece y desaparece): así el rótulo no cambia al aparecer aquel.
        readonly List<(Control c, string full, string shortText, Func<int> reserve)> _adaptiveTexts = new();
        void AddAdaptiveText(Control c, string full, string shortText, Func<int> reserve = null)
        {
            _adaptiveTexts.Add((c, full, shortText, reserve));
            c.SizeChanged += (s, e) => ApplyAdaptiveTexts();   // al cambiar de ancho se vuelve a decidir
            ApplyAdaptiveTexts();
        }
        void ApplyAdaptiveTexts()
        {
            foreach (var (c, full, sh, reserve) in _adaptiveTexts)
            {
                string t = full;
                try
                {
                    // ¿Cabe el texto largo dentro del propio control? Se mide con su letra y
                    // descontando lo que ocupan el icono o la casilla, igual que al dibujarlo; así
                    // vale para cualquier tamaño de ventana, idioma o escala de la interfaz.
                    if (c.Width > 0)
                    {
                        var btn = c as RoundButton;
                        Font f = btn != null ? Theme.Font(btn.FontSize, btn.FontStyle) : c.Font;
                        bool icon = btn != null && !string.IsNullOrEmpty(btn.GlyphKind);
                        int reserved = btn != null
                            ? (!icon ? 8 : btn.Tab ? 32 : 42)   // icono + separación
                            : (c is CheckBox ? 28 : 8);         // casilla + separación
                        try { reserved += Math.Max(0, reserve?.Invoke() ?? 0); } catch { }
                        // Las pestañas se dibujan sin relleno lateral; el resto, con él: se mide igual.
                        var flags = btn != null && btn.Tab ? TextFormatFlags.NoPadding : TextFormatFlags.Default;
                        int Fit(string s2) => TextRenderer.MeasureText(s2, f, Size.Empty, flags).Width;
                        if (Fit(full) > c.Width - reserved) t = sh;
                        // Si ni siquiera el texto corto cabe con el icono, el botón prescinde de él
                        // antes que cortar la palabra (las pestañas lo deciden juntas en ApplyTabIcons).
                        // Se reproduce el reparto exacto del dibujo: icono + separación, centrados.
                        if (icon && !btn.Tab)
                        {
                            int gs = Math.Min(btn.Height - 10, 22), gap = 10;
                            int content = gs + gap + TextRenderer.MeasureText(t, f, Size.Empty, TextFormatFlags.NoPadding).Width;
                            int startX = Math.Max(6, (btn.Width - content) / 2);
                            bool room = Fit(t) <= btn.Width - (startX + gs + gap) - 4;
                            if (btn.ShowGlyph != room) { btn.ShowGlyph = room; btn.Invalidate(); }
                        }
                        if (btn != null) f.Dispose();
                    }
                }
                catch { }
                if (c.Text != t) { c.Text = t; c.Invalidate(); }
            }
        }

        // Los iconos de las pestañas solo se dibujan si con ellos cabe el texto de TODAS: o los llevan
        // todas o ninguna, para que la barra no quede desigual.
        void ApplyTabIcons()
        {
            if (_pills == null) return;
            bool room = true;
            try
            {
                foreach (var pill in _pills)
                {
                    if (pill == null || pill.Width <= 0) continue;
                    using var f = Theme.Font(pill.FontSize, pill.FontStyle);
                    int w = TextRenderer.MeasureText(pill.Text, f, Size.Empty, TextFormatFlags.NoPadding).Width;
                    if (w + 26 > pill.Width - 6) { room = false; break; }
                }
            }
            catch { }
            foreach (var pill in _pills)
                if (pill != null && pill.ShowGlyph != room) { pill.ShowGlyph = room; pill.Invalidate(); }
        }

        // Al cambiar el tamaño de la ventana (maximizar / restaurar / redimensionar): adaptar la maqueta.
        void OnWindowSizeChanged()
        {
            if (WindowState == FormWindowState.Minimized) return;
            ApplySidebarForPage(_activePage);
            ApplyAdaptiveTexts();
            ApplyTabIcons();
            ApplyEditorSize();
            ApplyResponsiveChrome();
        }

        // Cabecera, barra inferior y bloque «de servicio» ajustados al tamaño real de la ventana.
        void ApplyResponsiveChrome()
        {
            if (_root != null && _root.RowStyles.Count > 0)
            {
                float hh = HeaderH();
                if (_root.RowStyles[0].Height != hh) _root.RowStyles[0].Height = hh;
            }
            if (_empDutyHost != null)
            {
                int dw = Math.Min(Theme.Px(584), Math.Max(Theme.Px(300), ClientSize.Width / 2));
                if (_empDutyHost.Width != dw) _empDutyHost.Width = dw;
            }
            LayoutBottomBar();
        }

        Panel _bottomBar, _header, _sidebar, _center;

        Label _lblVersion;

        void BuildHeader()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Theme.Surface, Name = "top" };
            var stripe = new LiveryStripe { Dock = DockStyle.Bottom };   // franja verde: separa cabecera y contenido
            var logo = new SelectorLogo { Location = new Point(16, 11), Width = 190, Height = 40 };
            _lblVersion = new Label { Text = "", ForeColor = Theme.Subtle, AutoSize = true, Location = new Point(210, 24), Font = Theme.Font(8f) };

            var lblPack = new Label { Text = Tr("Contenido"), ForeColor = Theme.Subtle, AutoSize = true };
            _cboFolder = new ThemeCombo { Width = 210, DropDownHeight = 300 };
            StyleCombo(_cboFolder);
            _cboFolder.SelectedIndexChanged += (s, e) => OnFolderChanged();

            var btnResume = MakeChip(Tr("Reanudar"), 138, "resume");
            btnResume.Click += (s, e) => OpenResume();
            var btnOptions = MakeChip(Tr("Opciones OR"), 176, "gear");
            btnOptions.Click += (s, e) => LaunchSibling("Menu.exe", "");
            var btnAbout = MakeChip(Tr("Acerca de SelectOR"), 210, "info");
            btnAbout.Click += (s, e) => ShowAbout();

            top.Controls.AddRange(new Control[] { logo, _lblVersion, lblPack, _cboFolder, btnResume, btnOptions, btnAbout });
            _lblVersion.TextChanged += (s, e) => top.PerformLayout();   // la versión se rellena más tarde
            top.Layout += (s, e) =>
            {
                // Todo el contenido se reparte dentro del hueco que queda SOBRE la franja verde y
                // centrado en él: así, cuando la cabecera se encoge (ventana baja o escala pequeña),
                // el logotipo y los botones se ajustan en vez de quedar cortados por la franja.
                int band = Math.Max(Theme.Px(28), top.ClientSize.Height - stripe.Height);
                int chipH = Math.Max(Theme.Px(24), Math.Min(Theme.Px(32), band - Theme.Px(10)));
                int chipY = Math.Max(0, (band - chipH) / 2);
                int gap = Theme.Px(8), edge = Theme.Px(16);

                logo.Height = Math.Max(Theme.Px(26), Math.Min(Theme.Px(40), band - Theme.Px(6)));
                logo.Width = Theme.Px(190);
                logo.Location = new Point(edge, (band - logo.Height) / 2);

                btnAbout.Height = btnOptions.Height = btnResume.Height = chipH;
                int right = top.ClientSize.Width - edge;
                btnAbout.Location = new Point(right - btnAbout.Width, chipY);
                btnOptions.Location = new Point(btnAbout.Left - btnOptions.Width - gap, chipY);
                btnResume.Location = new Point(btnOptions.Left - btnResume.Width - gap, chipY);

                // Ventana estrecha: el selector de contenido se encoge (hasta 150 px) para no pisar la versión
                // de Open Rails; si aun así no cabe, la versión se oculta (sigue en «Acerca de SelectOR»).
                _lblVersion.Location = new Point(logo.Right + Theme.Px(14), (band - _lblVersion.PreferredHeight) / 2);
                int verRight = _lblVersion.Left + _lblVersion.PreferredWidth + edge;
                int room = btnResume.Left - Theme.Px(14) - (verRight + lblPack.Width + gap);
                _cboFolder.Width = Math.Max(Theme.Px(150), Math.Min(Theme.Px(210), room));
                _cboFolder.Location = new Point(btnResume.Left - _cboFolder.Width - Theme.Px(14), (band - _cboFolder.Height) / 2);
                lblPack.Location = new Point(_cboFolder.Left - lblPack.Width - gap, (band - lblPack.PreferredHeight) / 2);
                _lblVersion.Visible = lblPack.Left >= verRight;
            };

            // La franja va DENTRO del panel de cabecera (evita ambigüedades de docking a nivel de formulario)
            top.Controls.Add(stripe);
            _header = top;
        }

        void BuildBottomBar()
        {
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 98, BackColor = Theme.Surface, Name = "bottom" };
            var stripe = new LiveryStripe { Dock = DockStyle.Top };

            // Columna derecha con el botón CONDUCIR, centrado vertical y horizontalmente
            var rightCol = new Panel { Dock = DockStyle.Right, Width = 296, BackColor = Theme.Surface };
            _btnPlay = new RoundButton
            {
                Text = Tr("CONDUCIR"), GlyphKind = "play", Radius = 12, Size = new Size(240, 60),
                ActiveColor = Theme.Accent, BaseColor = Theme.Accent, HoverColor = Theme.AccentHi,
                TextColor = Color.White, FontSize = 14f, FontStyle = FontStyle.Bold,
                Anchor = AnchorStyles.Right
            };
            _btnPlay.Click += (s, e) => Play();
            rightCol.Controls.Add(_btnPlay);

            // Multijugador: el mismo botón grande, en el mismo sitio, pero para entrar en la partida.
            _btnConnect = new RoundButton
            {
                Text = Tr("CONECTAR"), GlyphKind = "connect", Radius = 12, Size = new Size(240, 60),
                ActiveColor = Theme.Accent, BaseColor = Theme.Accent, HoverColor = Theme.AccentHi,
                TextColor = Color.White, FontSize = 14f, FontStyle = FontStyle.Bold,
                Anchor = AnchorStyles.Right, Visible = false
            };
            _btnConnect.Click += (s, e) => PlayMultiplayer();
            rightCol.Controls.Add(_btnConnect);

            Action centerPlay = () =>
            {
                foreach (var b in new[] { _btnPlay, _btnConnect })
                    b.Location = new Point(rightCol.ClientSize.Width - b.Width - 20, Math.Max(4, (rightCol.ClientSize.Height - b.Height) / 2));
            };
            rightCol.Resize += (s, e) => centerPlay();
            rightCol.HandleCreated += (s, e) => centerPlay();

            // Grupo "de servicio" para la sección Empresas: ocupa el sitio de CONDUCIR.
            // Ya no hay flota: se conduce el tren seleccionado en Exploración/Horarios/Actividad.
            _empDutyHost = new Panel { Dock = DockStyle.Right, Width = 584, BackColor = Theme.Surface, Visible = false };
            var dutyFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Surface, Padding = new Padding(0, 15, 20, 0) };
            _empDutyBtn = new RoundButton
            {
                Text = Tr("Ponerme de servicio"), GlyphKind = "play", Radius = 12, Size = new Size(256, 50),
                ActiveColor = Theme.Accent, BaseColor = Theme.Accent, HoverColor = Theme.AccentHi,
                TextColor = Color.White, FontSize = 11.5f, FontStyle = FontStyle.Bold, Margin = new Padding(0)
            };
            _empDutyBtn.Click += (s, e) => ToggleDuty();
            // Mismo estilo que "Ponerme de servicio" pero en AZUL, para registrar un servicio dejado pendiente.
            _empCloseOpenBtn = new RoundButton
            {
                Text = Tr("Registrar servicio"), GlyphKind = "bank", Radius = 12, Size = new Size(240, 50),
                BaseColor = Color.FromArgb(37, 99, 235), HoverColor = Color.FromArgb(59, 130, 246),
                ActiveColor = Color.FromArgb(29, 78, 216),
                TextColor = Color.White, FontSize = 12f, FontStyle = FontStyle.Bold, Visible = false, Margin = new Padding(0, 0, 14, 0)
            };
            _empCloseOpenBtn.Click += (s, e) => FinalizeService();
            dutyFlow.Controls.Add(_empDutyBtn); dutyFlow.Controls.Add(_empCloseOpenBtn);
            _empDutyHost.Controls.Add(dutyFlow);

            _lblStatus = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Subtle, Padding = new Padding(20, 0, 0, 0), Font = Theme.Font(10f) };

            bottom.Controls.Add(_lblStatus);
            bottom.Controls.Add(rightCol);
            bottom.Controls.Add(_empDutyHost);
            bottom.Controls.Add(stripe);
            _bottomBar = bottom;   // se añade dentro del panel central en BuildCenter
        }

        void BuildSidebar()
        {
            var left = new Panel { Dock = DockStyle.Left, Width = 340, BackColor = Theme.BgSidebar, Padding = new Padding(16, 14, 10, 14), Name = "left" };
            var lbl = MakeSectionLabel("RUTAS");
            lbl.Dock = DockStyle.Top;

            _routeSearch = new RoundedInput(Tr("Buscar ruta…")) { Dock = DockStyle.Top, Margin = new Padding(0) };
            _routeSearch.Box.TextChanged += (s, e) => RefreshRouteList();
            var searchHost = Pad(_routeSearch, 0, 6, 0, 6);
            searchHost.Dock = DockStyle.Top;

            _chkFavOnly = MakeCheck("★  Solo favoritas");
            _chkFavOnly.Dock = DockStyle.Top;
            _chkFavOnly.CheckedChanged += (s, e) => RefreshRouteList();

            _lstRoutes = MakeListBox(Theme.BgSidebar, 50);
            _lstRoutes.DrawItem += DrawRouteItem;
            _lstRoutes.SelectedIndexChanged += (s, e) => OnRouteChanged();
            _lstRoutes.MouseDoubleClick += (s, e) => { if (_lstRoutes.SelectedItem is Route) ToggleRouteFavorite(); };
            var routeCtx = new ContextMenuStrip();
            var miFav = new ToolStripMenuItem(Tr("Añadir / quitar de favoritas"));
            miFav.Click += (s, e) => ToggleRouteFavorite();
            routeCtx.Items.Add(miFav);
            _lstRoutes.ContextMenuStrip = routeCtx;

            left.Controls.Add(_lstRoutes);
            left.Controls.Add(_chkFavOnly);
            left.Controls.Add(searchHost);
            left.Controls.Add(lbl);

            _routesOverlay = new ListStatePanel { Bg = Theme.BgSidebar };
            left.Controls.Add(_routesOverlay);
            void syncRoutesOverlay() { if (_routesOverlay != null) { _routesOverlay.Bounds = _lstRoutes.Bounds; if (_routesOverlay.Visible) _routesOverlay.BringToFront(); } }
            _lstRoutes.SizeChanged += (s, e) => syncRoutesOverlay();
            _lstRoutes.LocationChanged += (s, e) => syncRoutesOverlay();
            left.SizeChanged += (s, e) => syncRoutesOverlay();
            syncRoutesOverlay();
            _sidebar = left;
        }

        void BuildCenter()
        {
            var center = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(12, 6, 16, 84), Name = "center" };

            _pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

            // barra de pestañas tipo "segmented": TableLayoutPanel con columnas iguales
            // (reparto determinista, independiente del orden de acoplado de los paneles).
            var names = new[] { "Ruta", "Actividad", "Exploración", "Horarios", "Multijugador", "Editor de composiciones", "Empresas" };
            var kinds = new[] { "map", "activity", "explore", "clock", "globe", "train", "bank" };
            var pillHost = new TableLayoutPanel
            {
                Dock = DockStyle.Top, Height = 46, BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 1),
                ColumnCount = names.Length, RowCount = 1, Margin = new Padding(0)
            };
            for (int i = 0; i < names.Length; i++) pillHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / names.Length));
            pillHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            // Línea fina bajo la barra de pestañas (el subrayado verde de la activa se apoya en ella).
            pillHost.Paint += (s, e) => { using var p = new Pen(Theme.Border); e.Graphics.DrawLine(p, 0, pillHost.Height - 1, pillHost.Width, pillHost.Height - 1); };
            _pills = new RoundButton[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                var pill = new RoundButton
                {
                    Text = Tr(names[i]), GlyphKind = kinds[i], Tab = true, Dock = DockStyle.Fill,
                    Margin = new Padding(0), FontSize = 10f
                };
                pill.Click += (s, e) => ShowPage(idx);
                if (i == PageEditor) AddAdaptiveText(pill, Tr("Editor de composiciones"), Tr("Editor"));   // ventana estrecha
                _pills[i] = pill;
                pillHost.Controls.Add(pill, i, 0);
            }

            _pageRuta = BuildRutaPage();
            _pageActividad = BuildActividadPage();
            _pageExplora = BuildExploraPage();
            _pageHorarios = BuildHorariosPage();
            _pageMulti = BuildMultijugadorPage();
            _pageEditor = BuildEditorPage();
            _pageEmpresas = BuildEmpresasPage();
            foreach (var p in new[] { _pageRuta, _pageActividad, _pageExplora, _pageHorarios, _pageMulti, _pageEditor, _pageEmpresas })
            {
                p.Dock = DockStyle.Fill; p.Visible = false; _pageHost.Controls.Add(p);
            }

            _pageFade = new PageFade();
            _pageHost.Controls.Add(_pageFade);

            center.Controls.Add(_pageHost);
            center.Controls.Add(pillHost);
            _center = center;

            int startTab = (_prefs.LastTab >= 0 && _prefs.LastTab < 7) ? _prefs.LastTab : 1;
            ShowPage(startTab);
        }

        // Pestaña "Ruta": imagen/título de la ruta + descripción (antes en la cabecera) + botón del mapa.
        // Con trazado disponible (MainMenuForm.RutaMapa.cs): solo la descripción, compacta, y el mapa en vivo.
        Panel BuildRutaPage()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 46));   // banner
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 54));   // descripción
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // botón mapa
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // mapa en vivo (si la ruta tiene trazado)
            _rutaGrid = t;

            _banner = new BannerPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10) };
            t.Controls.Add(_banner, 0, 0);

            var descCard = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, Radius = 12, Padding = new Padding(14, 12, 14, 12) };
            var lblD = MakeSectionLabel("DESCRIPCIÓN DE LA RUTA"); lblD.Dock = DockStyle.Top;
            _routeDescBox = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Subtle,
                Font = Theme.Font(10f), TabStop = false, Cursor = Cursors.Default
            };
            Native.UseDarkScrollBars(_routeDescBox);
            var gapD = new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Surface };
            descCard.Controls.Add(_routeDescBox);
            descCard.Controls.Add(gapD);
            descCard.Controls.Add(lblD);
            t.Controls.Add(descCard, 0, 1);
            _rutaDescCard = descCard; _rutaDescLbl = lblD; _rutaDescGap = gapD;

            var btnMap = new RoundButton { Text = Tr("Ver mapa de la ruta"), GlyphKind = "map", Dock = DockStyle.Fill, Height = 40, Radius = 9, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 10f, Margin = new Padding(0, 10, 0, 2) };
            btnMap.Click += (s, e) => OpenRouteMap();
            var btnHost = new Panel { Dock = DockStyle.Fill, Height = 52, BackColor = Theme.Bg, Padding = new Padding(0, 10, 0, 2) };
            btnHost.Controls.Add(btnMap);
            t.Controls.Add(btnHost, 0, 2);
            _rutaBtnHost = btnHost;

            _rutaMap = new RouteLiveMap { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 2), Visible = false };
            t.Controls.Add(_rutaMap, 0, 3);
            _rutaLiveTimer = new Timer { Interval = RutaLiveIntervalMs };
            _rutaLiveTimer.Tick += async (s, e) => await RutaLiveTick();
            t.SizeChanged += (s, e) => RutaDescRelayout();

            var page = new Panel { BackColor = Theme.Bg };
            page.Controls.Add(t);
            return page;
        }

        Panel BuildActividadPage()
        {
            var page = new Panel { BackColor = Theme.Bg };
            _actSearch = new RoundedInput(Tr("Buscar actividad…")) { Dock = DockStyle.Top };
            _actSearch.Box.TextChanged += (s, e) => RefreshActivityList();
            var searchHost = Pad(_actSearch, 0, 0, 0, 8); searchHost.Dock = DockStyle.Top;

            _lstActivities = MakeListBox(Theme.Surface, 30);
            _lstActivities.DrawItem += (s, e) => DrawTextRow(_lstActivities, e, false);
            _lstActivities.SelectedIndexChanged += (s, e) => OnActivitySelected();
            _lstActivities.DoubleClick += (s, e) => Play();
            var listCard = WrapCard(_lstActivities);
            listCard.Dock = DockStyle.Fill;

            var briefingCard = new Card { Dock = DockStyle.Bottom, Height = 172, Fill = Theme.Surface, Radius = 12, Padding = new Padding(14, 12, 14, 12) };
            var lblB = MakeSectionLabel("RESUMEN"); lblB.Dock = DockStyle.Top;
            _txtBriefing = MakeReadonlyText(); _txtBriefing.Dock = DockStyle.Fill;
            var gapAct = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Theme.Surface };  // separación etiqueta→texto
            briefingCard.Controls.Add(_txtBriefing);
            briefingCard.Controls.Add(gapAct);
            briefingCard.Controls.Add(lblB);
            var briefHost = Pad(briefingCard, 0, 8, 0, 0); briefHost.Dock = DockStyle.Bottom;

            page.Controls.Add(listCard);
            page.Controls.Add(briefHost);
            page.Controls.Add(searchHost);
            return page;
        }

        Panel BuildExploraPage()
        {
            var page = new Panel { BackColor = Theme.Bg };
            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));

            // ---- izquierda: trenes ----
            var pTrain = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 0, 8, 0) };
            var lblT = MakeSectionLabel("TREN (composición)"); lblT.Dock = DockStyle.Top;
            _consistSearch = new RoundedInput(Tr("Buscar tren…")) { Dock = DockStyle.Top };
            _consistSearch.Box.TextChanged += (s, e) => RefreshConsistList();
            var cSearchHost = Pad(_consistSearch, 0, 6, 0, 6); cSearchHost.Dock = DockStyle.Top;
            _chkTrainFavOnly = MakeCheck("★  Solo favoritos"); _chkTrainFavOnly.Dock = DockStyle.Top;
            _chkTrainFavOnly.CheckedChanged += (s, e) => RefreshConsistList();
            // filtro por empresa: etiqueta "Empresa" (AutoSize, nunca se parte) a la izquierda +
            // desplegable que rellena el ancho restante. Oculto hasta que haya empresas con trenes.
            var coGrid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Height = 44, BackColor = Theme.Bg, Padding = new Padding(2, 6, 0, 8) };
            coGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            coGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            coGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var coLbl = new Label { Text = Tr("Empresa"), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(9.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 0) };
            _cboTrainCompany = new ThemeCombo { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 3, 0, 3) };
            StyleCombo(_cboTrainCompany);
            _cboTrainCompany.Items.Add(Tr(AllCompaniesLabel)); _cboTrainCompany.SelectedIndex = 0;
            _cboTrainCompany.SelectedIndexChanged += (s, e) => { if (!_companyFilterLoading) RefreshConsistList(); };
            coGrid.Controls.Add(coLbl, 0, 0);
            coGrid.Controls.Add(_cboTrainCompany, 1, 0);
            _trainCompanyHost = coGrid; _trainCompanyHost.Visible = false;   // solo si hay empresas con trenes
            _lstConsists = MakeListBox(Theme.Surface, 28);
            _lstConsists.DrawItem += DrawConsistItem;
            _lstConsists.SelectedIndexChanged += (s, e) => OnConsistSelected();
            var consistCtx = new ContextMenuStrip();
            var miTFav = new ToolStripMenuItem(Tr("Añadir / quitar de favoritos")); miTFav.Click += (s, e) => ToggleTrainFavorite();
            consistCtx.Items.Add(miTFav);
            _lstConsists.ContextMenuStrip = consistCtx;
            _lstConsists.MouseDoubleClick += (s, e) => { if (_lstConsists.SelectedItem is TrainItem) ToggleTrainFavorite(); };
            var trainListCard = WrapCard(_lstConsists); trainListCard.Dock = DockStyle.Fill;
            _consistsOverlay = new ListStatePanel { Bg = Theme.Surface };
            trainListCard.Controls.Add(_consistsOverlay);
            void syncConsistsOverlay() { if (_consistsOverlay != null) { _consistsOverlay.Bounds = _lstConsists.Bounds; if (_consistsOverlay.Visible) _consistsOverlay.BringToFront(); } }
            _lstConsists.SizeChanged += (s, e) => syncConsistsOverlay();
            _lstConsists.LocationChanged += (s, e) => syncConsistsOverlay();
            trainListCard.SizeChanged += (s, e) => syncConsistsOverlay();
            syncConsistsOverlay();
            pTrain.Controls.Add(trainListCard);
            pTrain.Controls.Add(_trainCompanyHost);   // justo encima de la lista
            pTrain.Controls.Add(cSearchHost);
            pTrain.Controls.Add(_chkTrainFavOnly);
            pTrain.Controls.Add(lblT);

            // ---- derecha: condiciones + preview ----
            var pOpt = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(8, 0, 0, 0) };
            var lblO = MakeSectionLabel("RECORRIDO Y CONDICIONES"); lblO.Dock = DockStyle.Top;
            var grid = MakeFieldGrid(210);
            _cboStart = NewCombo(); _cboStart.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboStart.SelectedIndexChanged += (s, e) => OnStartChanged();
            _cboEnd = NewCombo(); _cboEnd.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboEnd.SelectedIndexChanged += (s, e) => UpdateStatus();
            _hourSlider = new HourSlider { Minutes = 12 * 60 };
            _hourSlider.Changed += UpdateStatus;
            _segSeason = new Segmented(new[] { "spring", "summer", "autumn", "winter" }, new[] { Tr("Primavera"), Tr("Verano"), Tr("Otoño"), Tr("Invierno") }) { SelectedIndex = 1 };
            _segWeather = new Segmented(new[] { "clear", "snow", "rain" }, new[] { Tr("Despejado"), Tr("Nieve"), Tr("Lluvia") }) { SelectedIndex = 0 };
            _chkExploreActivity = MakeCheck("Explorar en modo actividad");
            AddAdaptiveText(_chkExploreActivity, Tr("Explorar en modo actividad"), Tr("Modo actividad"));
            AddField(grid, 0, "Empezar en", _cboStart);
            AddField(grid, 1, "Ir hacia", _cboEnd);
            AddField(grid, 2, "Hora", _hourSlider);
            AddField(grid, 3, "Estación", _segSeason);
            AddField(grid, 4, "Clima", _segWeather);
            AddField(grid, 5, "", _chkExploreActivity);

            var previewCard = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 10, 0, 14) };
            var lblP = MakeSectionLabel("TREN SELECCIONADO"); lblP.Dock = DockStyle.Top;
            _trainPreview = new TrainPreviewPanel { Dock = DockStyle.Fill };
            _trainPreview.Dragged += OnPreviewDrag;
            _trainPreview.ResetRequested += OnPreviewReset;
            _trainPreview.Zoomed += () => { if (_previewGeom != null) RenderLive(); };
            _previewRerender = new Timer { Interval = 140 };
            _previewRerender.Tick += (s, e) => { _previewRerender.Stop(); RenderLive(); };
            _trainPreview.Resize += (s, e) => { if (_previewGeom != null) { _previewRerender.Stop(); _previewRerender.Start(); } };
            var btnComp = new RoundButton { Text = Tr("Ver composición 2D"), GlyphKind = "train", Dock = DockStyle.Fill, Height = 34, Radius = 9, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 9.5f };
            btnComp.Click += (s, e) => OpenComposition();
            AddAdaptiveText(btnComp, Tr("Ver composición 2D"), Tr("Composición 2D"),
                            () => _buyWrapExplore != null && !_buyWrapExplore.Visible ? _buyWrapExplore.Width : 0);
            var btnCompHost = new Panel { Dock = DockStyle.Bottom, Height = 42, BackColor = Theme.Bg, Padding = new Padding(0, 8, 0, 0) };
            _buyWrapExplore = MakeBuyTrainWrap(() => _lstConsists?.SelectedItem as TrainItem, 34);   // oculto salvo gerente/gestor/superadmin
            btnCompHost.Controls.Add(btnComp);           // Fill
            btnCompHost.Controls.Add(_buyWrapExplore);   // Right
            var (hdrP, stChip) = MakeTrainHeader(lblP); _lblStatusExplore = stChip;   // título + estado en la flota
            _teleExplore = MakeTeleUi(hdrP, () => RenderLive());                       // teleindicador (si el tren tiene destinos)
            previewCard.Controls.Add(_trainPreview);
            previewCard.Controls.Add(btnCompHost);
            previewCard.Controls.Add(hdrP);

            var btnMap = new RoundButton { Text = Tr("Ver mapa del recorrido"), GlyphKind = "map", Dock = DockStyle.Top, Height = 32, Radius = 9, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 9.5f };
            btnMap.Click += (s, e) => OpenPathMap();
            var btnMapHost = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Bg, Padding = new Padding(0, 4, 0, 4) };
            btnMapHost.Controls.Add(btnMap);

            pOpt.Controls.Add(previewCard);
            pOpt.Controls.Add(btnMapHost);
            pOpt.Controls.Add(grid);
            pOpt.Controls.Add(lblO);

            split.Controls.Add(pTrain, 0, 0);
            split.Controls.Add(pOpt, 1, 0);
            page.Controls.Add(split);
            return page;
        }

        Panel BuildHorariosPage()
        {
            var page = new Panel { BackColor = Theme.Bg };
            var lblH = MakeSectionLabel("HORARIOS (TIMETABLE)"); lblH.Dock = DockStyle.Top;
            var grid = MakeFieldGrid(150);
            _cboTTSet = NewCombo(); _cboTTSet.SelectedIndexChanged += (s, e) => OnTimetableSetChanged();
            _cboTT = NewCombo(); _cboTT.SelectedIndexChanged += (s, e) => OnTimetableChanged();
            _cboTTTrain = NewCombo(); _cboTTTrain.SelectedIndexChanged += (s, e) => { UpdateStatus(); UpdateTimetableBriefing(); UpdateTimetablePreview(); };
            _cboTTCompany = NewCombo(); _cboTTCompany.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboTTCompany.Items.Add(Tr(AllCompaniesLabel)); _cboTTCompany.SelectedIndex = 0;
            _cboTTCompany.SelectedIndexChanged += (s, e) => { if (!_companyFilterLoading) OnTimetableChanged(); };
            _segTTSeason = new Segmented(new[] { "spring", "summer", "autumn", "winter" }, new[] { Tr("Primavera"), Tr("Verano"), Tr("Otoño"), Tr("Invierno") }) { SelectedIndex = 1 };
            _segTTWeather = new Segmented(new[] { "clear", "snow", "rain" }, new[] { Tr("Despejado"), Tr("Nieve"), Tr("Lluvia") }) { SelectedIndex = 0 };
            AddField(grid, 0, "Conjunto", _cboTTSet);
            AddField(grid, 1, "Horario", _cboTT);
            (_ttCompanyLbl, _ttCompanyHost) = AddFieldCore(grid, 2, "Empresa", _cboTTCompany);
            _ttGrid = grid; _ttCompanyRow = 2;
            AddField(grid, 3, "Tren", _cboTTTrain);
            AddField(grid, 4, "Estación", _segTTSeason);
            AddField(grid, 5, "Clima", _segTTWeather);
            SetTTCompanyVisible(false);   // oculto hasta que haya empresas con trenes

            var briefingCard = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, Radius = 12, Padding = new Padding(14, 12, 14, 12) };
            var lblB = MakeSectionLabel("RESUMEN DEL TREN"); lblB.Dock = DockStyle.Top;
            _txtTTBriefing = MakeReadonlyText(); _txtTTBriefing.Dock = DockStyle.Fill;
            var gapTT = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Theme.Surface };  // separación etiqueta→texto
            briefingCard.Controls.Add(_txtTTBriefing);
            briefingCard.Controls.Add(gapTT);
            briefingCard.Controls.Add(lblB);
            var briefHost = Pad(briefingCard, 0, 10, 8, 0); briefHost.Dock = DockStyle.Fill;

            // Derecha: TREN SELECCIONADO (render 3D rotable + composición 2D), igual que en Exploración.
            var previewCard = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(8, 10, 0, 0) };
            var lblP = MakeSectionLabel("TREN SELECCIONADO"); lblP.Dock = DockStyle.Top;
            _ttPreview = new TrainPreviewPanel { Dock = DockStyle.Fill };
            _ttPreview.Dragged += OnTTPreviewDrag;
            _ttPreview.ResetRequested += OnTTPreviewReset;
            _ttPreview.Zoomed += () => { if (_ttPreviewGeom != null) RenderTTLive(); };
            _ttRerender = new Timer { Interval = 140 };
            _ttRerender.Tick += (s, e) => { _ttRerender.Stop(); RenderTTLive(); };
            _ttPreview.Resize += (s, e) => { if (_ttPreviewGeom != null) { _ttRerender.Stop(); _ttRerender.Start(); } };
            var btnMapTT = new RoundButton { Text = Tr("Ver mapa del recorrido"), GlyphKind = "map", Dock = DockStyle.Top, Height = 34, Radius = 9, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 9.5f };
            btnMapTT.Click += (s, e) => OpenTTPathMap();
            var btnCompTT = new RoundButton { Text = Tr("Ver composición 2D"), GlyphKind = "train", Dock = DockStyle.Fill, Height = 34, Radius = 9, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 9.5f };
            btnCompTT.Click += (s, e) => OpenTTComposition();
            AddAdaptiveText(btnCompTT, Tr("Ver composición 2D"), Tr("Composición 2D"),
                            () => _buyWrapTT != null && !_buyWrapTT.Visible ? _buyWrapTT.Width : 0);
            // Fila «Ver composición 2D» + «Comprar este tren» (este último oculto salvo gerente/gestor/superadmin).
            var compRowTT = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Theme.Bg };
            _buyWrapTT = MakeBuyTrainWrap(CurrentTTConsist, 34);
            compRowTT.Controls.Add(btnCompTT);    // Fill
            compRowTT.Controls.Add(_buyWrapTT);   // Right
            var btnsTTHost = new Panel { Dock = DockStyle.Bottom, Height = 90, BackColor = Theme.Bg, Padding = new Padding(0, 8, 0, 0) };
            var gapBtnsTT = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Bg };
            btnsTTHost.Controls.Add(compRowTT);   // añadido 1º → queda abajo
            btnsTTHost.Controls.Add(gapBtnsTT);
            btnsTTHost.Controls.Add(btnMapTT);    // añadido 2º → queda arriba
            var (hdrTT, stChipTT) = MakeTrainHeader(lblP); _lblStatusTT = stChipTT;   // título + estado en la flota
            _teleTT = MakeTeleUi(hdrTT, () => RenderTTLive());                          // teleindicador (si el tren tiene destinos)
            previewCard.Controls.Add(_ttPreview);
            previewCard.Controls.Add(btnsTTHost);
            previewCard.Controls.Add(hdrTT);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
            bottom.Controls.Add(briefHost, 0, 0);
            bottom.Controls.Add(previewCard, 1, 0);

            page.Controls.Add(bottom);
            page.Controls.Add(grid);
            page.Controls.Add(lblH);
            return page;
        }

        Panel BuildMultijugadorPage()
        {
            var page = new Panel { BackColor = Theme.Bg };

            // ---- lista de servidores públicos (relleno) ----
            var srvPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 10, 0, 0) };
            var srvHeader = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Theme.Bg };
            _lblServers = MakeSectionLabel("SERVIDORES PÚBLICOS (tsimserver.com)"); _lblServers.Dock = DockStyle.Left; _lblServers.AutoSize = false; _lblServers.Width = 640;
            var btnRefresh = new RoundButton { Text = Tr("Actualizar"), Icon = "⟲", Width = 120, Height = 26, Radius = 8, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 9f, Dock = DockStyle.Right };
            btnRefresh.Click += (s, e) => LoadServers();
            srvHeader.Controls.Add(_lblServers);
            srvHeader.Controls.Add(btnRefresh);

            _lstServers = MakeListBox(Theme.Surface, 30);
            _lstServers.DrawItem += DrawServerItem;
            _lstServers.SelectedIndexChanged += (s, e) => OnServerSelected();
            _lstServers.DoubleClick += (s, e) => { OnServerSelected(); PlayMultiplayer(); };
            var srvCard = WrapCard(_lstServers); srvCard.Dock = DockStyle.Fill;

            // Cabecera de columnas alineada con las filas (Servidor · Ruta · Nombres · Jugadores).
            var colHead = new Panel { Dock = DockStyle.Top, Height = 22, BackColor = Theme.Bg };
            colHead.Paint += (s2, pe) =>
            {
                var g = pe.Graphics; int pad = 6, H = colHead.Height;
                var row = new Rectangle(pad, 0, Math.Max(60, colHead.Width - 2 * pad), H);
                var addrRect = new Rectangle(row.X + 14, 0, 200, H);
                var playRect = new Rectangle(row.Right - 78, 0, 70, H);
                int mid0 = addrRect.Right + 8, mid1 = playRect.Left - 8, midW = Math.Max(40, mid1 - mid0);
                var routeRect = new Rectangle(mid0, 0, (int)(midW * 0.42f), H);
                var namesRect = new Rectangle(routeRect.Right + 8, 0, mid1 - (routeRect.Right + 8), H);
                using (var f = Theme.Font(8f, FontStyle.Bold))
                {
                    var lf = TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
                    TextRenderer.DrawText(g, Tr("SERVIDOR"), f, addrRect, Theme.Subtle, lf);
                    TextRenderer.DrawText(g, Tr("RUTA"), f, routeRect, Theme.Subtle, lf);
                    TextRenderer.DrawText(g, Tr("NOMBRES"), f, namesRect, Theme.Subtle, lf);
                    TextRenderer.DrawText(g, "👤", f, playRect, Theme.Subtle, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                }
            };
            colHead.Resize += (s2, e2) => colHead.Invalidate();

            srvPanel.Controls.Add(srvCard);
            srvPanel.Controls.Add(colHead);
            srvPanel.Controls.Add(srvHeader);

            var card = new Card { Dock = DockStyle.Top, Height = 228, Fill = Theme.Surface, Radius = 12 };
            var inner = new Panel { Size = new Size(680, 196), BackColor = Theme.Surface };

            var lblM = new Label { Text = Tr("MULTIJUGADOR"), ForeColor = Theme.Accent, BackColor = Theme.Surface, Font = Theme.Font(8.5f, FontStyle.Bold), AutoSize = true, Location = new Point(4, 6) };
            var info = new Label
            {
                Location = new Point(4, 28), Size = new Size(672, 46), ForeColor = Theme.Subtle, BackColor = Theme.Surface, AutoSize = false,
                Text = Tr("Se lanza la selección de «Exploración» (recorrido + tren) en red. Configura tu usuario; como cliente, indica el host y el puerto del servidor.")
            };
            _lblMPInfo = info;

            _rbServer = new ThemeRadio { Text = "  " + Tr("Servidor (alojar partida)"), ForeColor = Theme.Text, Location = new Point(8, 86), Size = new Size(300, 26), Checked = true };
            _rbClient = new ThemeRadio { Text = "  " + Tr("Cliente (unirse a un servidor)"), ForeColor = Theme.Text, Location = new Point(8, 116), Size = new Size(300, 26) };
            _rbClient.CheckedChanged += (s, e) => { ApplyMPMode(); UpdateStatus(); };

            _txtMPUser = MPField(inner, Tr("Usuario"), 86, 340);
            // Usuario de Multijugador con las MISMAS reglas que Open Rails (si no, no conecta):
            // de 4 a 10 caracteres, sin espacios, ni ', " o -, y que no empiece por un número.
            _txtMPUser.MaxLength = 10;
            _lblMPUserRule = new Label { Location = new Point(8, 150), Size = new Size(326, 42), ForeColor = Theme.Subtle, BackColor = Theme.Surface, AutoSize = false, Font = Theme.Font(8.25f),
                                         Text = Tr("Usuario: de 4 a 10 caracteres, sin espacios ni ' \" -, y sin empezar por un número (lo exige Open Rails).") };
            _txtMPUser.TextChanged += (s, e) => OnMPUserChanged();
            _txtMPHost = MPField(inner, Tr("Host (servidor)"), 124, 340);
            _txtMPPort = MPField(inner, Tr("Puerto"), 162, 340);
            ApplyMPMode();

            // CONECTAR vive ahora en la barra inferior, junto al resto de acciones de cada sección.
            inner.Controls.AddRange(new Control[] { lblM, info, _rbServer, _rbClient, _lblMPUserRule });
            card.Controls.Add(inner);
            Action centerInner = () => inner.Location = new Point(Math.Max(8, (card.ClientSize.Width - inner.Width) / 2), 16);
            card.Resize += (s, e) => centerInner();
            card.HandleCreated += (s, e) => centerInner();

            page.Controls.Add(srvPanel);
            page.Controls.Add(card);
            return page;
        }

        void DrawServerItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var sv = (GameServer)_lstServers.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            DrawRowBackground(g, e.Bounds, _lstServers.BackColor, selected);
            int players = 0; int.TryParse(sv.Players, out players);
            // 4 columnas: Servidor (ip:puerto) · Ruta · Nombres de jugadores · Nº jugadores
            var addrRect = new Rectangle(e.Bounds.X + 14, e.Bounds.Y, 200, e.Bounds.Height);
            var playRect = new Rectangle(e.Bounds.Right - 78, e.Bounds.Y, 70, e.Bounds.Height);
            int mid0 = addrRect.Right + 8, mid1 = playRect.Left - 8;
            int midW = Math.Max(40, mid1 - mid0);
            var routeRect = new Rectangle(mid0, e.Bounds.Y, (int)(midW * 0.42f), e.Bounds.Height);
            var namesRect = new Rectangle(routeRect.Right + 8, e.Bounds.Y, mid1 - (routeRect.Right + 8), e.Bounds.Height);
            const TextFormatFlags LF = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
            using (var fb = Theme.Font(9.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, sv.Ip + ":" + sv.Port, fb, addrRect, Theme.Text, LF);
            var route = string.IsNullOrWhiteSpace(sv.Route) || sv.Route == "-" ? Tr("(sin ruta)") : sv.Route;
            TextRenderer.DrawText(g, route, Font, routeRect, Theme.Subtle, LF);
            var names = string.IsNullOrWhiteSpace(sv.PlayerNames) || sv.PlayerNames == "-" ? "—" : sv.PlayerNames.Replace("\t", ", ");
            TextRenderer.DrawText(g, names, Font, namesRect, Theme.Subtle, LF);
            var pcolor = players > 0 ? Theme.Gold : Theme.Subtle;
            TextRenderer.DrawText(g, "👤 " + sv.Players, Font, playRect, pcolor, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }

        // Servidor: quien aloja no usa ninguna IP (Open Rails escucha en todas las conexiones del equipo, en el
        // puerto indicado), así que el campo Host enseña la IP de ESTE equipo en la red local, en solo lectura
        // (se puede seleccionar y copiar), para dársela a los demás. Cliente: la IP del servidor al que unirse.
        void ApplyMPMode()
        {
            if (_txtMPHost == null || _rbServer == null) return;
            if (_rbServer.Checked)
            {
                if (!_mpShowingOwnIp) _mpClientHost = _txtMPHost.Text;
                string ip = LocalIPv4();
                var bg = _txtMPHost.BackColor;
                _txtMPHost.ReadOnly = true; _txtMPHost.BackColor = bg;
                _txtMPHost.ForeColor = Theme.AccentHi;
                _txtMPHost.Text = ip ?? Tr("(sin red)");
                _mpShowingOwnIp = true;
                if (_lblMPInfo != null)
                    _lblMPInfo.Text = ip != null
                        ? string.Format(Tr("Tu IP en la red local es {0}: dásela a los demás jugadores junto con el puerto. Para jugar por internet, abre ese puerto (TCP) en tu router y da tu IP pública."), ip)
                        : Tr("No se ha encontrado ninguna red activa en este equipo.");
            }
            else
            {
                if (_mpShowingOwnIp) _txtMPHost.Text = _mpClientHost ?? "";
                var bg = _txtMPHost.BackColor;
                _txtMPHost.ReadOnly = false; _txtMPHost.BackColor = bg;
                _txtMPHost.ForeColor = Theme.Text;
                _mpShowingOwnIp = false;
                if (_lblMPInfo != null)
                    _lblMPInfo.Text = Tr("Se lanza la selección de «Exploración» (recorrido + tren) en red. Configura tu usuario; como cliente, indica el host y el puerto del servidor.");
            }
            _txtMPHost.Enabled = true;
        }

        // IP de este equipo en la red local (IPv4): la de la conexión con puerta de enlace, y mejor una física
        // que una virtual (VPN, máquinas virtuales…). null si no hay ninguna red activa.
        static string LocalIPv4()
        {
            try
            {
                string best = null; int bestScore = -1;
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                        || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                    var props = ni.GetIPProperties();
                    bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                                             && !g.Address.Equals(System.Net.IPAddress.Any));
                    string d = (ni.Description + " " + ni.Name).ToLowerInvariant();
                    bool virt = d.Contains("virtual") || d.Contains("vmware") || d.Contains("hyper-v") || d.Contains("vbox")
                             || d.Contains("vpn") || d.Contains("tap-") || d.Contains("wsl") || d.Contains("tailscale") || d.Contains("zerotier");
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        string ip = ua.Address.ToString();
                        if (ip.StartsWith("169.254.") || ip.StartsWith("127.")) continue;
                        int score = (gw ? 2 : 0) + (virt ? 0 : 1);
                        if (score > bestScore) { bestScore = score; best = ip; }
                    }
                }
                return best;
            }
            catch { return null; }
        }

        void OnServerSelected()
        {
            if (!(_lstServers.SelectedItem is GameServer sv)) return;
            _rbClient.Checked = true;
            _txtMPHost.Text = sv.Ip;
            _txtMPPort.Text = sv.Port;
            UpdateStatus();
        }

        void LoadServers()
        {
            _lblServers.Text = Tr("SERVIDORES PÚBLICOS — cargando…");
            _lstServers.Items.Clear();
            Task.Run(async () =>
            {
                var list = await Servers.FetchAsync();
                BeginInvoke((Action)(() =>
                {
                    _serversAll = list;
                    _lstServers.BeginUpdate();
                    _lstServers.Items.Clear();
                    foreach (var s in list.OrderByDescending(v => { int.TryParse(v.Players, out int n); return n; }))
                        _lstServers.Items.Add(s);
                    _lstServers.EndUpdate();
                    _lblServers.Text = list.Count > 0
                        ? string.Format(Tr("SERVIDORES PÚBLICOS ({0}) — doble clic para conectar"), list.Count)
                        : Tr("SERVIDORES PÚBLICOS — no disponible (sin conexión o lista vacía)");
                }));
            });
        }

        // ---- Usuario de Multijugador: reglas de Open Rails (Menu: «User name must be 4-10 characters long,
        // cannot contain space, ' , " or - and must not start with a digit») ----
        static string MPUserClean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char ch in s)
                if (!char.IsWhiteSpace(ch) && ch != '\'' && ch != '"' && ch != '-') sb.Append(ch);
            return sb.Length > 10 ? sb.ToString(0, 10) : sb.ToString();
        }

        static bool MPUserValid(string s) => !string.IsNullOrEmpty(s) && s.Length >= 4 && s.Length <= 10 && !char.IsDigit(s[0]) && MPUserClean(s) == s;

        // Nombre de Windows limpio si vale; si no, uno genérico que sí cumple.
        static string MPUserDefault()
        {
            string u = MPUserClean(Environment.UserName);
            return MPUserValid(u) ? u : "Maquinista";
        }

        bool _mpUserFixing;
        void OnMPUserChanged()
        {
            if (_mpUserFixing || _txtMPUser == null) return;
            string t = _txtMPUser.Text, c = MPUserClean(t);
            if (c != t)
            {
                // Se quitan al escribir los caracteres que Open Rails no admite (conservando el cursor).
                int caret = Math.Max(0, _txtMPUser.SelectionStart - (t.Length - c.Length));
                _mpUserFixing = true; _txtMPUser.Text = c; _mpUserFixing = false;
                _txtMPUser.SelectionStart = Math.Min(caret, c.Length);
            }
            if (_lblMPUserRule != null) _lblMPUserRule.ForeColor = MPUserValid(c) ? Theme.Subtle : Color.FromArgb(229, 115, 115);
        }

        TextBox MPField(Control parent, string label, int y, int xLabel)
        {
            var lbl = new Label { Text = label, ForeColor = Theme.Subtle, BackColor = Theme.Surface, AutoSize = false, Width = 120, Height = 30, TextAlign = ContentAlignment.MiddleLeft, Location = new Point(xLabel, y) };
            var inp = new RoundedInput { Width = 220, Height = 32, Location = new Point(xLabel + 118, y - 2) };
            parent.Controls.Add(lbl);
            parent.Controls.Add(inp);
            return inp.Box;
        }

        // ---------- helpers de estilo ----------
        // Altura suficiente para que el TEXTO no se corte (contenido = Height - padding vertical >= alto de
        // la fuente) MÁS un hueco inferior de separación. AutoEllipsis por si el ancho no alcanza.
        Label MakeSectionLabel(string t) => new Label { Text = Tr(t), ForeColor = Theme.Accent, Font = Theme.Font(8.5f, FontStyle.Bold), Height = 34, Dock = DockStyle.Top, Padding = new Padding(2, 3, 0, 11), AutoEllipsis = true, UseCompatibleTextRendering = false };

        CheckBox MakeCheck(string t) => new ThemeCheck { Text = Tr(t), ForeColor = Theme.Text, Height = 28, AutoSize = false };

        Panel Pad(Control inner, int l, int t, int r, int b)
        {
            // Fondo OPACO del contenedor (no transparente): WinForms no limpia los paneles
            // transparentes y dejaban restos verdes/negros de lo que había debajo.
            var p = new Panel { Padding = new Padding(l, t, r, b), Height = inner.Height + t + b };
            p.HandleCreated += (s, e) => p.BackColor = Theme.ResolveBg(p);
            p.ParentChanged += (s, e) => { if (p.Parent != null) p.BackColor = Theme.ResolveBg(p); };
            inner.Dock = DockStyle.Fill;
            p.Controls.Add(inner);
            return p;
        }

        Card WrapCard(Control inner)
        {
            var c = new Card { Fill = Theme.Surface, Radius = 12, Padding = new Padding(6) };
            inner.Dock = DockStyle.Fill;
            c.Controls.Add(inner);
            return c;
        }

        RoundButton MakeChip(string text, int w, string glyph = null)
        {
            return new RoundButton { Text = text, GlyphKind = glyph, Width = w, Height = 32, Radius = 9, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 9.5f };
        }

        ListBox MakeListBox(Color bg, int itemHeight)
        {
            var lb = new ListBox
            {
                BackColor = bg,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None,
                IntegralHeight = false,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = itemHeight,
                Dock = DockStyle.Fill
            };
            Native.UseDarkScrollBars(lb);   // barra de scroll oscura acorde al tema
            return lb;
        }

        // Al cambiar "Empezar en", recalcula los destinos disponibles para ese origen.
        void OnStartChanged()
        {
            if (_cboEnd == null) return;
            var start = _cboStart.SelectedItem as string ?? "";
            _cboEnd.Items.Clear();
            foreach (var en in _pathsAll.Where(p => (p.Start ?? "") == start).Select(p => p.End ?? "").Distinct().OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
                _cboEnd.Items.Add(en);
            if (_cboEnd.Items.Count > 0) _cboEnd.SelectedIndex = 0;
            UpdateStatus();
        }

        // Recorrido (.pat) resultante del par Empezar en / Ir hacia.
        OrPath CurrentPath()
        {
            var st = _cboStart?.SelectedItem as string ?? "";
            var en = _cboEnd?.SelectedItem as string ?? "";
            foreach (var p in _pathsAll)
                if ((p.Start ?? "") == st && (p.End ?? "") == en) return p;
            return _pathsAll.Count > 0 ? _pathsAll[0] : null;
        }

        TextBox MakeReadonlyText()
        {
            var tb = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                Font = Theme.Font(9.5f)
            };
            Native.UseDarkScrollBars(tb);
            return tb;
        }

        ComboBox NewCombo()
        {
            var c = new ThemeCombo { Dock = DockStyle.Fill };
            StyleCombo(c);
            return c;
        }

        void StyleCombo(ComboBox c)
        {
            c.FlatStyle = FlatStyle.Flat;
            c.BackColor = Theme.Surface2;
            c.ForeColor = Theme.Text;
            c.DrawMode = DrawMode.OwnerDrawFixed;
            c.ItemHeight = 22;
            c.Font = Theme.Font(9.5f);
            c.DrawItem += ComboDraw;
            Native.UseDarkScrollBars(c);   // barra de scroll oscura en el desplegable
        }

        void ComboDraw(object s, DrawItemEventArgs e)
        {
            var cb = (ComboBox)s;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Theme.Accent : Theme.Surface2)) e.Graphics.FillRectangle(b, e.Bounds);
            if (e.Index >= 0)
            {
                var txt = cb.GetItemText(cb.Items[e.Index]);
                var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
                // NoPrefix: si no, un «&» en el texto (p. ej. «Cercanias & GL») se pinta como subrayado.
                TextRenderer.DrawText(e.Graphics, txt, cb.Font, r, sel ? Color.White : Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        TableLayoutPanel MakeFieldGrid(int labelW)
        {
            var g = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, BackColor = Theme.Bg, AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelW));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return g;
        }

        void AddField(TableLayoutPanel g, int row, string label, Control ctrl) => AddFieldCore(g, row, label, ctrl);

        (Label lbl, Panel host) AddFieldCore(TableLayoutPanel g, int row, string label, Control ctrl)
        {
            var lbl = new Label { Text = Tr(label), ForeColor = Theme.Subtle, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Font(9.5f) };
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 5, 0, 5), Height = 48, BackColor = Theme.Bg };
            ctrl.Dock = DockStyle.Fill;
            host.Controls.Add(ctrl);
            g.Controls.Add(lbl, 0, row);
            g.Controls.Add(host, 1, row);
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            return (lbl, host);
        }

        // ---------- dibujo de filas de lista ----------
        void DrawRowBackground(Graphics g, Rectangle bounds, Color listBg, bool selected)
        {
            using (var b = new SolidBrush(listBg)) g.FillRectangle(b, bounds);
            if (selected)
            {
                var r = new Rectangle(bounds.X + 2, bounds.Y + 2, bounds.Width - 6, bounds.Height - 4);
                Theme.FillRound(g, r, 7, Theme.Surface2);
                Theme.FillRound(g, new Rectangle(r.X + 2, r.Y + 4, 3, r.Height - 8), 2, Theme.Accent);
            }
        }

        void DrawTextRow(ListBox list, DrawItemEventArgs e, bool star)
        {
            if (e.Index < 0) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            DrawRowBackground(e.Graphics, e.Bounds, list.BackColor, selected);
            var item = list.Items[e.Index];
            int left = e.Bounds.X + 14;
            var textRect = new Rectangle(left, e.Bounds.Y, e.Bounds.Right - left - 8, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, item.ToString(), Font, textRect, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        void DrawRouteItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var route = (Route)_lstRoutes.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            DrawRowBackground(g, e.Bounds, _lstRoutes.BackColor, selected);

            var thumbRect = new Rectangle(e.Bounds.Left + 8, e.Bounds.Top + 5, 66, e.Bounds.Height - 10);
            Theme.FillRound(g, thumbRect, 6, Color.FromArgb(12, 11, 16));
            var imgs = ContentImages.ForRoute(route.Path);
            var thumb = ImageCache.Get(imgs.thumb, 132, 92, SafeInvalidateRoutes);
            if (thumb != null)
            {
                using (var clip = Theme.Round(thumbRect, 6)) g.SetClip(clip);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                // Ajuste automático: recorta (cover) si las proporciones son parecidas; si no, encaja (contain).
                double sCover = Math.Max((double)thumbRect.Width / thumb.Width, (double)thumbRect.Height / thumb.Height);
                double sFit = Math.Min((double)thumbRect.Width / thumb.Width, (double)thumbRect.Height / thumb.Height);
                double arImg = (double)thumb.Width / thumb.Height, arBox = (double)thumbRect.Width / thumbRect.Height;
                double s = (arImg / arBox > 2.2 || arBox / arImg > 2.2) ? sFit : sCover;
                int w = (int)(thumb.Width * s), h = (int)(thumb.Height * s);
                g.DrawImage(thumb, thumbRect.X + (thumbRect.Width - w) / 2, thumbRect.Y + (thumbRect.Height - h) / 2, w, h);
                g.ResetClip();
            }

            int left = thumbRect.Right + 10;
            if (_prefs.Favorites.Contains(route.Path))
            {
                TextRenderer.DrawText(g, "★", Font, new Rectangle(left, e.Bounds.Y, 16, e.Bounds.Height), Theme.Gold, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                left += 18;
            }
            var textRect = new Rectangle(left, e.Bounds.Y, e.Bounds.Right - left - 8, e.Bounds.Height);
            using (var f = Theme.Font(10f))
                TextRenderer.DrawText(g, route.Name, f, textRect, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        void DrawConsistItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var consist = (TrainItem)_lstConsists.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            DrawRowBackground(g, e.Bounds, _lstConsists.BackColor, selected);
            int left = e.Bounds.X + 12;
            if (_prefs.FavoriteTrains.Contains(consist.FilePath))
            {
                TextRenderer.DrawText(g, "★", Font, new Rectangle(left, e.Bounds.Y, 16, e.Bounds.Height), Theme.Gold, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                left += 18;
            }
            int rightEdge = e.Bounds.Right - 8;
            // Una etiqueta por cada empresa (de la que soy socio) que tenga este tren. Se apilan de
            // derecha a izquierda; si el maquinista está en varias con el mismo tren, salen todas.
            var cos = ConsistCompanies(consist);
            for (int ci = 0; ci < cos.Count; ci++)
            {
                string co = cos[ci];
                if (string.IsNullOrEmpty(co)) continue;
                string label = co;   // nombre completo de la empresa; la pill se adapta al ancho
                Size ts = TextRenderer.MeasureText(g, label, BadgeFont);
                int ph = Math.Min(e.Bounds.Height - 6, 18);
                int pw = ts.Width + 14;
                int px = rightEdge - pw, py = e.Bounds.Y + (e.Bounds.Height - ph) / 2;
                if (px < left + 30) break;   // sin sitio para más etiquetas
                var pill = new Rectangle(px, py, pw, ph);
                Theme.FillRound(g, pill, ph / 2, Color.FromArgb(38, Theme.Accent));
                TextRenderer.DrawText(g, label, BadgeFont, pill, Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                rightEdge = px - 6;
            }
            var textRect = new Rectangle(left, e.Bounds.Y, Math.Max(20, rightEdge - left), e.Bounds.Height);
            TextRenderer.DrawText(g, consist.Name, Font, textRect, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        static readonly Font BadgeFont = Theme.Font(7.5f, FontStyle.Bold);

        void SafeInvalidateRoutes()
        {
            try { if (_lstRoutes.IsHandleCreated) _lstRoutes.BeginInvoke((Action)(() => _lstRoutes.Invalidate())); } catch { }
        }

        // ============================ Navegación pestañas ============================
        void ShowPage(int i)
        {
            int previous = _activePage;
            _activePage = i;
            for (int k = 0; k < _pills.Length; k++) { _pills[k].Active = (k == i); _pills[k].Invalidate(); }
            _pageRuta.Visible = i == 0;
            _pageActividad.Visible = i == 1;
            _pageExplora.Visible = i == 2;
            _pageHorarios.Visible = i == 3;
            _pageMulti.Visible = i == 4;
            _pageEditor.Visible = i == PageEditor;
            _pageEmpresas.Visible = i == PageEmpresas;
            ApplySidebarForPage(i);   // Editor y Empresas: sin columna RUTAS (todo el ancho para el contenido)
            _btnPlay.Visible = i != 0 && i != 4 && i != PageEditor && i != PageEmpresas;   // estas pestañas no usan CONDUCIR
            _btnConnect.Visible = i == 4;                                                  // Multijugador: CONECTAR
            UpdateDutyHostVisible();   // barra "de servicio" en Empresas (solo si perteneces a alguna empresa)
            _prefs.LastTab = i;
            if (i == 4 && _serversAll.Count == 0 && _lstServers != null && _lstServers.Items.Count == 0) LoadServers();
            // El editor enseña su estructura al momento y cada lista va con su propio «Cargando…».
            if (i == PageEditor) OnEditorShown();
            if (i == PageEmpresas) OnEmpresasShown();
            if (i == 3) { UpdateTimetableBriefing(); UpdateTimetablePreview(); }   // resumen + render 3D del tren del timetable
            RutaLiveTimerUpdate();   // el mapa en vivo de Ruta solo consulta mientras se ve
            UpdateStatus();
            LayoutBottomBar();   // Ruta y el Editor no llevan barra inferior
            if (previous != i && IsHandleCreated)
            {
                _pageHost.Refresh();          // la sección nueva se pinta ya…
                _pageFade?.Play(_pageHost);   // …y desde esa imagen se funde
            }
        }

        // ============================ Datos ============================
        bool _dataStarted;
        void StartData()
        {
            if (_dataStarted) return;
            _dataStarted = true;
            InitData();
        }

        async void InitData()
        {
            // Auto-login de Empresas en segundo plano (si hay contraseña recordada): así, al abrir
            // la pestaña Empresas, ya hay sesión y no se ve parpadear el formulario de acceso.
            // Antes de leer carpetas (que puede salir pronto si no hay contenido).
            TryAutoLogin();
            try
            {
                if (_settings == null) _settings = new UserSettings(new string[0]);
                _folders = Folder.GetFolders(_settings).OrderBy(f => f.Name).ToList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Tr("No se pudieron leer los ajustes de Open Rails:\n\n") + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _folders = new List<Folder>();
            }

            // Versión de Open Rails que se está leyendo
            try { _lblVersion.Text = "Open Rails  " + ORTS.Common.VersionInfo.VersionOrBuild; } catch { }

            _cboFolder.Items.Clear();
            foreach (var f in _folders) _cboFolder.Items.Add(f.Name);

            // Multiplayer defaults desde settings
            try
            {
                _txtMPUser.Text = MPUserValid(MPUserClean(_settings.Multiplayer_User)) ? MPUserClean(_settings.Multiplayer_User) : MPUserDefault();
                _mpClientHost = string.IsNullOrEmpty(_settings.Multiplayer_Host) ? "127.0.0.1" : _settings.Multiplayer_Host;
                if (_mpShowingOwnIp) ApplyMPMode(); else _txtMPHost.Text = _mpClientHost;
                _txtMPPort.Text = _settings.Multiplayer_Port > 0 ? _settings.Multiplayer_Port.ToString() : "30000";
            }
            catch { }

            LoadStep("folders");
            if (_folders.Count == 0)
            {
                _lblStatus.Text = Tr("No hay carpetas de contenido configuradas. Ábrelas con «Opciones OR».");
                _noContent = true;
                if (_activePage == PageEditor) OnEditorShown();   // el editor deja de decir «Cargando…»
                // No hay contenido que leer: no tiene sentido seguir esperando en la pantalla de inicio.
                foreach (var st in LoadSteps) LoadStep(st.key);
                return;
            }
            int idx = 0;
            if (!string.IsNullOrEmpty(_prefs.LastFolder))
            {
                int found = _folders.FindIndex(f => f.Path == _prefs.LastFolder);
                if (found >= 0) idx = found;
            }
            // Con varias carpetas de contenido, se pregunta cuál cargar ANTES de leer nada (en la propia
            // pantalla de inicio, con la última usada ya elegida). Mientras se elige, no corre la red de
            // seguridad que muestra el menú a los 30 s.
            if (_folders.Count > 1)
            {
                _revealGuard?.Stop();
                StartPrefetch(_folders[idx]);   // mientras se elige, se va leyendo la marcada
                idx = await SplashScreen.AskFolder(_folders.Select(f => f.Name).ToArray(), idx);
                if (idx < 0 || idx >= _folders.Count) idx = 0;
                if (!_uiRevealed) _revealGuard?.Start();
            }
            _cboFolder.SelectedIndex = idx;
        }

        void OnFolderChanged()
        {
            if (_cboFolder.SelectedIndex < 0 || _cboFolder.SelectedIndex >= _folders.Count) return;
            _curFolder = _folders[_cboFolder.SelectedIndex];
            _prefs.LastFolder = _curFolder.Path;
            int token = ++_folderToken;
            _consistsLoading = true;   // el editor lo indica dentro de su propia lista
            SetBusy(string.Format(Tr("Cargando rutas de «{0}»…"), _curFolder.Name));
            _routesOverlay?.SetState(ListStatePanel.Mode.Loading, Tr("Cargando rutas…"));
            _consistsOverlay?.SetState(ListStatePanel.Mode.Loading, Tr("Cargando trenes…"));
            var folder = _curFolder;

            Task.Run(() =>
            {
                WaitPrefetch();
                var pre = string.Equals(_prefetchPath, folder.Path, StringComparison.OrdinalIgnoreCase) ? _prefetchRoutes : null;
                _prefetchRoutes = null;   // se usa una sola vez (F5 vuelve a leer)
                var routes = pre ?? SafeList(() => Route.GetRoutes(folder).OrderBy(r => r.Name).ToList());
                try { foreach (var r in routes) ContentImages.ForRoute(r.Path); } catch { }
                BeginInvoke((Action)(() =>
                {
                    if (token != _folderToken) return;
                    _routesAll = routes;
                    RefreshRouteList();
                    _routesOverlay?.SetState(_routesAll.Count == 0 ? ListStatePanel.Mode.Empty : ListStatePanel.Mode.Hidden, Tr("Sin rutas"));
                    SetIdle();
                    LoadStep("routes");
                    if (!string.IsNullOrEmpty(_prefs.LastRoute))
                    {
                        int i = _routesAll.FindIndex(r => r.Path == _prefs.LastRoute);
                        if (i >= 0) SelectRouteInList(_routesAll[i]);
                    }
                    UpdateStatus();
                }));
            });

            // Los datos de las máquinas (.eng) no dependen de los trenes: se leen ya, en paralelo con
            // el análisis de consists de Open Rails, para que Compra esté lista cuanto antes.
            LoadLog("carpeta elegida: " + folder.Name);
            // La interfaz queda libre mientras el contenido se lee en segundo plano: se aprovecha para crear
            // ya el dispositivo gráfico de la vista 3D (si no, lo pagaría el primer render del tren).
            try { BeginInvoke((Action)(() => { ShapeRenderer.WarmUp(); LoadLog("vista 3D: dispositivo gráfico listo"); })); } catch { }
            Task.Run(() => { try { WaitPrefetch(); PrewarmMachineData(); } catch { } finally { LoadStep("engines"); } });

            _consistsAll = new List<TrainItem>();
            RefreshConsistList();
            Task.Run(() =>
            {
                WaitPrefetch();
                List<TrainItem> consists;
                bool cached; lock (_consistCache) cached = _consistCache.TryGetValue(folder.Path, out consists);
                if (!cached)
                {
                    // Lectura propia de los .con (unos 20 veces más rápida que la de Open Rails);
                    // si no encontrase nada, se recurre a la de Open Rails.
                    consists = FastConsists.Load(folder.Path);
                    if (consists.Count == 0)
                        consists = SafeList(() => Consist.GetConsists(folder).Select(FromOrConsist).OrderBy(c => c.Name).ToList());
                    lock (_consistCache) _consistCache[folder.Path] = consists;
                }
                BeginInvoke((Action)(() =>
                {
                    if (token != _folderToken) return;
                    _consistsLoading = false;
                    _consistsAll = consists;
                    LoadLog($"trenes leídos ({consists.Count}): llenando la lista…");
                    RefreshConsistList();
                    LoadLog("lista de trenes llena");
                    _consistsOverlay?.SetState(_consistsAll.Count == 0 ? ListStatePanel.Mode.Empty : ListStatePanel.Mode.Hidden, Tr("Sin trenes"));
                    UpdateTimetablePreview();   // ya hay consists → resuelve el tren del timetable (si estaba pendiente)
                    RebuildCompanyEngs();       // recalcula qué trenes son de mis empresas (etiqueta)
                    LoadLog("horario y trenes de empresa hechos");
                    LoadStep("consists");
                    EditorContentChanged();     // editor de composiciones: lista de .con de esta carpeta
                    LoadLog("editor preparado");
                    BeginInvoke((Action)(() => LoadLog("interfaz libre tras los trenes")));
                    // Formaciones fijas (automotores) del contenido: se calculan ya, en segundo plano,
                    // para que abrir Compra no tenga que leer los miles de .con en ese momento.
                    Task.Run(() => { try { EnsureEngUnits(); } catch { } finally { LoadStep("units"); } });
                }));
            });
        }

        void OnRouteChanged()
        {
            var route = _lstRoutes.SelectedItem as Route;
            if (route == null) { _curRoute = null; return; }
            _curRoute = route;
            _prefs.LastRoute = route.Path;
            _banner.RouteName = route.Name;
            _routeDescBox.Text = string.IsNullOrEmpty(route.Description) ? "" : route.Description;
            RutaMapRouteChanged(route);
            _banner.Image = null; _banner.Invalidate();
            var imgs = ContentImages.ForRoute(route.Path);
            if (!string.IsNullOrEmpty(imgs.banner))
            {
                var cached = ImageCache.Get(imgs.banner, 1280, 320, () => SetBannerAsync(route.Path, imgs.banner));
                if (cached != null) _banner.Image = cached;
            }

            int token = ++_routeToken;
            SetBusy(Tr("Cargando actividades, recorridos y horarios…"));
            var folder = _curFolder;
            Task.Run(() =>
            {
                var acts = SafeList(() => Activity.GetActivities(folder, route)
                    .Where(a => !(a is DefaultExploreActivity) && !(a is ExploreThroughActivity))
                    .OrderBy(a => a.Name).ToList());
                var paths = SafeList(() => OrPath.GetPaths(route, false).OrderBy(p => p.ToString()).ToList());
                var tts = SafeList(() => TimetableInfo.GetTimetableInfo(folder, route));
                BeginInvoke((Action)(() =>
                {
                    if (token != _routeToken) return;
                    LoadLog("ruta: actividades, recorridos y horarios leídos; llenando listas…");
                    _activitiesAll = acts; _pathsAll = paths; _timetablesAll = tts;
                    RefreshActivityList(); RefreshPathList(); RefreshTimetableSets();
                    SetIdle(); UpdateStatus();
                    LoadLog("ruta: listas llenas");
                }));
            });
        }

        // ---------- refrescos ----------
        void RefreshRouteList()
        {
            string q = _routeSearch.Box.Text.Trim();
            bool favOnly = _chkFavOnly.Checked;
            var prev = _lstRoutes.SelectedItem as Route;
            _lstRoutes.BeginUpdate();
            _lstRoutes.Items.Clear();
            foreach (var r in _routesAll)
            {
                if (favOnly && !_prefs.Favorites.Contains(r.Path)) continue;
                if (q.Length > 0 && (r.Name == null || r.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                _lstRoutes.Items.Add(r);
            }
            _lstRoutes.EndUpdate();
            if (prev != null) SelectRouteInList(prev);
        }

        void SelectRouteInList(Route r)
        {
            for (int i = 0; i < _lstRoutes.Items.Count; i++)
                if (((Route)_lstRoutes.Items[i]).Path == r.Path) { _lstRoutes.SelectedIndex = i; return; }
        }

        void RefreshActivityList()
        {
            string q = _actSearch.Box.Text.Trim();
            _lstActivities.BeginUpdate();
            _lstActivities.Items.Clear();
            foreach (var a in _activitiesAll)
            {
                if (q.Length > 0 && (a.Name == null || a.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                _lstActivities.Items.Add(a);
            }
            _lstActivities.EndUpdate();
            if (_lstActivities.SelectedIndex < 0 && _lstActivities.Items.Count > 0) _lstActivities.SelectedIndex = 0;
            if (_activitiesAll.Count == 0) _txtBriefing.Text = Tr("Esta ruta no tiene actividades. Usa «Exploración» u «Horarios».");
        }

        void RefreshConsistList()
        {
            string q = _consistSearch == null ? "" : _consistSearch.Box.Text.Trim();
            bool favOnly = _chkTrainFavOnly != null && _chkTrainFavOnly.Checked;
            string coFilter = SelectedCompany(_cboTrainCompany);
            var prev = _lstConsists.SelectedItem as TrainItem;
            _lstConsists.BeginUpdate();
            _lstConsists.Items.Clear();
            foreach (var c in _consistsAll)
            {
                if (favOnly && !_prefs.FavoriteTrains.Contains(c.FilePath)) continue;
                if (q.Length > 0 && (c.Name == null || c.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                if (coFilter != null && !HasCo(ConsistCompanies(c), coFilter)) continue;
                _lstConsists.Items.Add(c);
            }
            _lstConsists.EndUpdate();
            if (prev != null)
                for (int i = 0; i < _lstConsists.Items.Count; i++)
                    if (((TrainItem)_lstConsists.Items[i]).FilePath == prev.FilePath) { _lstConsists.SelectedIndex = i; break; }
            if (_lstConsists.SelectedIndex < 0 && !string.IsNullOrEmpty(_prefs.LastConsist))
                for (int i = 0; i < _lstConsists.Items.Count; i++)
                    if (((TrainItem)_lstConsists.Items[i]).FilePath == _prefs.LastConsist) { _lstConsists.SelectedIndex = i; break; }
            if (_lstConsists.SelectedIndex < 0 && _lstConsists.Items.Count > 0) _lstConsists.SelectedIndex = 0;
        }

        void RefreshPathList()
        {
            _cboStart.Items.Clear();
            foreach (var st in _pathsAll.Select(p => p.Start ?? "").Distinct().OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase)) _cboStart.Items.Add(st);
            if (_cboStart.Items.Count > 0) _cboStart.SelectedIndex = 0;   // dispara OnStartChanged → rellena destinos
            else _cboEnd.Items.Clear();
        }

        void RefreshTimetableSets()
        {
            _cboTTSet.Items.Clear();
            foreach (var t in _timetablesAll) _cboTTSet.Items.Add(t);
            if (_cboTTSet.Items.Count > 0) _cboTTSet.SelectedIndex = 0;
            else { _cboTT.Items.Clear(); _cboTTTrain.Items.Clear(); _txtTTBriefing.Text = "Esta ruta no tiene horarios (timetables)."; }
        }

        void OnTimetableSetChanged()
        {
            _cboTT.Items.Clear();
            var set = _cboTTSet.SelectedItem as TimetableInfo;
            if (set != null) foreach (var tt in set.ORTTList) _cboTT.Items.Add(tt);
            if (_cboTT.Items.Count > 0) _cboTT.SelectedIndex = 0;
            else { _cboTTTrain.Items.Clear(); }
        }

        void OnTimetableChanged()
        {
            _cboTTTrain.Items.Clear();
            var set = _cboTTSet.SelectedItem as TimetableInfo;
            if (set != null && _cboTT.SelectedIndex >= 0 && _cboTT.SelectedIndex < set.ORTTList.Count)
            {
                string coFilter = SelectedCompany(_cboTTCompany);
                var trains = set.ORTTList[_cboTT.SelectedIndex].Trains.ToList();
                trains.Sort((a, b) => string.Compare(a?.ToString(), b?.ToString(), StringComparison.CurrentCultureIgnoreCase));
                foreach (var tr in trains)
                {
                    if (coFilter != null && !TrainInCompany(tr as Orts.Formats.OR.TimetableFileLite.TrainInformation, coFilter)) continue;
                    _cboTTTrain.Items.Add(tr);
                }
                if (_cboTTTrain.Items.Count > 0) _cboTTTrain.SelectedIndex = 0;
            }
            UpdateStatus();
            UpdateTimetableBriefing();
            UpdateTimetablePreview();
        }

        // Rellena "RESUMEN DEL TREN" (Horarios) con toda la info del timetable y del tren
        // seleccionados: descripción/briefing del horario, estaciones (si la versión de OR las
        // expone) y del tren su salida, composición, recorrido y briefing propio.
        void UpdateTimetableBriefing()
        {
            if (_txtTTBriefing == null) return;
            var tt = _cboTT?.SelectedItem as Orts.Formats.OR.TimetableFileLite;
            if (tt == null) { _txtTTBriefing.Text = ""; return; }
            var sb = new System.Text.StringBuilder();
            string ttName = !string.IsNullOrWhiteSpace(tt.Description) ? tt.Description.Trim() : tt.ToString();
            sb.AppendLine(Tr("Horario") + ": " + ttName);
            if (!string.IsNullOrWhiteSpace(tt.Briefing)) sb.AppendLine().AppendLine(tt.Briefing.Trim());
            var stations = TtStations(tt);   // solo en versiones de OR que exponen "Stations"
            if (stations != null && stations.Count > 0)
                sb.AppendLine().AppendLine(Tr("Estaciones") + ": " + string.Join(" · ", stations));
            sb.AppendLine().AppendLine(string.Format(Tr("Trenes en este horario: {0}"), tt.Trains?.Count ?? 0));

            var tr = _cboTTTrain?.SelectedItem as Orts.Formats.OR.TimetableFileLite.TrainInformation;
            if (tr != null)
            {
                sb.AppendLine().AppendLine("──  " + Tr("Tren seleccionado") + "  ──");
                if (!string.IsNullOrWhiteSpace(tr.Train)) sb.AppendLine(Tr("Tren") + ": " + tr.Train);
                if (!string.IsNullOrWhiteSpace(tr.StartTime)) sb.AppendLine(Tr("Salida") + ": " + tr.StartTime);
                string consist = !string.IsNullOrWhiteSpace(tr.LeadingConsist) ? tr.LeadingConsist : tr.Consist;
                if (!string.IsNullOrWhiteSpace(consist)) sb.AppendLine(Tr("Composición") + ": " + consist + (tr.ReverseConsist ? "  (" + Tr("invertida") + ")" : ""));
                if (!string.IsNullOrWhiteSpace(tr.Path)) sb.AppendLine(Tr("Recorrido") + ": " + tr.Path);
                if (!string.IsNullOrWhiteSpace(tr.Briefing)) sb.AppendLine().AppendLine(tr.Briefing.Trim());
            }
            _txtTTBriefing.Text = sb.ToString().Replace("\n", "\r\n");
        }

        // "Stations" solo existe en algunas versiones de OR (p. ej. New Year) → por reflexión.
        static List<string> TtStations(object tt)
        {
            try
            {
                var f = tt.GetType().GetField("Stations");
                if (f != null && f.GetValue(tt) is System.Collections.IEnumerable en)
                    return en.Cast<object>().Select(x => x?.ToString() ?? "").Where(s => s.Length > 0).ToList();
            }
            catch { }
            return null;
        }

        // ===== Preview 3D/2D del tren del timetable (Horarios), igual que en Exploración =====
        // Resuelve el consist del tren seleccionado y lanza su render 3D (rotable) + composición 2D.
        void UpdateTimetablePreview()
        {
            if (_ttPreview == null) return;
            var tr = _cboTTTrain?.SelectedItem as Orts.Formats.OR.TimetableFileLite.TrainInformation;
            string ttConsist = tr != null ? (!string.IsNullOrWhiteSpace(tr.LeadingConsist) ? tr.LeadingConsist : tr.Consist) : null;
            var c = ResolveConsist(ttConsist);
            _ttConsist = c;
            TeleRefresh(_teleTT, c);   // destinos del tren (antes del render: la vista 3D ya sale con el cartel elegido)
            _ttPreviewGeom = null; _ttYaw = 0; _ttPitch = 0;
            _ttPreview.Image = null; _ttPreview.Rotatable = false; _ttPreview.Invalidate();
            _ttPreview.Caption = c?.Locomotive != null ? c.Locomotive.Name : (c != null ? c.Name : "");
            if (c == null) return;
            string eng = c.Locomotive?.FilePath;
            if (string.IsNullOrEmpty(eng)) return;
            _ttPreviewFlip = GetLeadFlip(c.FilePath);
            EnsureTTPreviewGeom(c.FilePath, eng);
        }

        // Busca en los consists cargados el que corresponde al nombre que trae el timetable
        // (puede venir como "A+B" o "A $reverse"; tomamos el consist base).
        // Conversión desde el objeto de Open Rails (actividades y respaldo de la lectura propia).
        static TrainItem FromOrConsist(Consist c)
        {
            if (c == null) return null;
            var t = new TrainItem { Name = c.Name ?? "", FilePath = c.FilePath ?? "" };
            var lp = c.Locomotive?.FilePath;
            if (!string.IsNullOrEmpty(lp)) t.Locomotive = new TrainEngine { FilePath = lp, Name = c.Locomotive.Name ?? "" };
            return t;
        }

        TrainItem ResolveConsist(string ttConsist)
        {
            if (string.IsNullOrWhiteSpace(ttConsist)) return null;
            string name = ttConsist.Split('+')[0].Trim();
            int sp = name.IndexOf('$'); if (sp > 0) name = name.Substring(0, sp).Trim();
            if (name.Length == 0) return null;
            foreach (var c in _consistsAll)
                if (string.Equals(SysPath.GetFileNameWithoutExtension(c.FilePath), name, StringComparison.OrdinalIgnoreCase)) return c;
            foreach (var c in _consistsAll)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        void EnsureTTPreviewGeom(string consistPath, string engPath)
        {
            lock (_ttRenderingShapes) { if (!_ttRenderingShapes.Add(engPath)) return; }
            Task.Run(() =>
            {
                ShapeGeom geom = null;
                try { geom = ShapeRenderer.BuildGeometry(engPath); } catch { }
                try { ShapeRenderer.PrefetchTextures(geom); } catch { }   // texturas listas antes de renderizar
                if (!IsHandleCreated) { lock (_ttRenderingShapes) _ttRenderingShapes.Remove(engPath); return; }
                BeginInvoke((Action)(() =>
                {
                    try
                    {
                        if (geom != null && _ttConsist?.FilePath == consistPath)
                        {
                            _ttPreviewGeom = geom;
                            _ttPreview.Rotatable = true;
                            RenderTTLive();
                        }
                    }
                    catch { }
                    lock (_ttRenderingShapes) _ttRenderingShapes.Remove(engPath);
                }));
            });
        }

        void OnTTPreviewDrag(int dx, int dy)
        {
            if (_ttPreviewGeom == null) return;
            _ttYaw += dx * 0.6f;
            _ttPitch = Math.Max(-55f, Math.Min(70f, _ttPitch - dy * 0.6f));
            RenderTTLive();
        }

        void OnTTPreviewReset() { if (_ttPreviewGeom == null) return; _ttYaw = 0; _ttPitch = 0; RenderTTLive(); }

        void RenderTTLive()
        {
            if (_ttPreviewGeom == null || _ttPreview.Width < 40 || _ttPreview.Height < 40) return;
            int w = Math.Min(1400, Math.Max(128, (_ttPreview.Width - 8) * 2));
            int h = Math.Min(820, Math.Max(96, (_ttPreview.Height - 36) * 2));
            TeleRedirect(_teleTT);   // el cartel elegido en Horarios (Exploración puede tener otro para el mismo tren)
            var bmp = ShapeRenderer.Render(_ttPreviewGeom, w, h, _ttYaw, _ttPitch, 1, _ttPreviewFlip, _ttPreview.CamDistance());
            if (bmp != null) _ttPreview.Image = bmp;
        }

        void OpenTTComposition()
        {
            var c = _ttConsist;
            if (c == null) { Warn(Tr("Este tren no tiene una composición reconocible en tu contenido.")); return; }
            if (_curFolder == null) { Warn(Tr("Selecciona una carpeta de contenido.")); return; }
            using (var dlg = new CompositionDialog(c.FilePath, _curFolder.Path, c.Name)) dlg.ShowDialog(this);
        }

        // Abre el mapa del recorrido (.pat) del tren del timetable seleccionado.
        void OpenTTPathMap()
        {
            var tr = _cboTTTrain?.SelectedItem as Orts.Formats.OR.TimetableFileLite.TrainInformation;
            var p = ResolvePath(tr?.Path);
            if (p == null) { Warn(Tr("Este tren no tiene un recorrido reconocible en tu contenido.")); return; }
            using (var dlg = new PathMapDialog(p.FilePath, _curRoute?.Path ?? "", p.Start, p.End, overview: true)) dlg.ShowDialog(this);
        }

        // Resuelve el recorrido del timetable (nombre del .pat) contra los recorridos cargados.
        OrPath ResolvePath(string ttPath)
        {
            if (string.IsNullOrWhiteSpace(ttPath)) return null;
            string name = ttPath.Trim();
            foreach (var p in _pathsAll)
                if (string.Equals(SysPath.GetFileNameWithoutExtension(p.FilePath), name, StringComparison.OrdinalIgnoreCase)) return p;
            foreach (var p in _pathsAll)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        void OnActivitySelected()
        {
            var a = _lstActivities.SelectedItem as Activity;
            if (a == null) { _txtBriefing.Text = ""; return; }
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(a.Description)) sb.AppendLine(a.Description).AppendLine();
            if (!string.IsNullOrEmpty(a.Briefing)) sb.AppendLine(a.Briefing);
            _txtBriefing.Text = sb.ToString().Replace("\n", "\r\n");
            UpdateStatus();
        }

        readonly HashSet<string> _renderingShapes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ShapeGeom _previewGeom;
        string _previewConsistPath;
        bool _previewFlip;   // Flip de la loco líder en el .con → orienta el render 3D como la 2D/OR
        float _yaw, _pitch;
        Timer _previewRerender;

        // Preview 3D/2D del tren del timetable (Horarios), independiente del de Exploración.
        TrainPreviewPanel _ttPreview;
        ShapeGeom _ttPreviewGeom;
        bool _ttPreviewFlip;
        float _ttYaw, _ttPitch;
        TrainItem _ttConsist;   // consist resuelto del tren del timetable (para render y composición 2D)
        Timer _ttRerender;
        readonly HashSet<string> _ttRenderingShapes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void OnConsistSelected()
        {
            var c = _lstConsists.SelectedItem as TrainItem;
            if (c != null) _prefs.LastConsist = c.FilePath;
            _trainPreview.Caption = c?.Locomotive != null ? c.Locomotive.Name : "";
            _trainPreview.Image = null; _trainPreview.Rotatable = false; _trainPreview.Invalidate();
            _previewGeom = null; _yaw = 0; _pitch = 0; _previewConsistPath = c?.FilePath;
            TeleRefresh(_teleExplore, c);   // destinos del tren (antes del render: la vista 3D ya sale con el cartel elegido)
            UpdateStatus();
            if (c == null) return;
            string eng = c.Locomotive?.FilePath;
            if (string.IsNullOrEmpty(eng)) return;
            _previewFlip = GetLeadFlip(c.FilePath);   // dirección de la loco líder según el .con

            // No mostramos una imagen cacheada intermedia (salía pequeña/enmarcada un instante):
            // se construye la geometría y se renderiza directamente a tamaño de panel (RenderLive).
            var png = ShapeRenderer.CachePathFor(eng);
            EnsurePreviewGeom(c.FilePath, eng, png);
        }

        // Construye la geometría (para poder rotar) y, si no hay caché, renderiza la vista por defecto.
        void EnsurePreviewGeom(string consistPath, string engPath, string pngCache)
        {
            lock (_renderingShapes) { if (!_renderingShapes.Add(engPath)) return; }
            Task.Run(() =>
            {
                ShapeGeom geom = null;
                LoadLog("vista 3D: construyendo la geometría…");
                try { geom = ShapeRenderer.BuildGeometry(engPath); } catch { }
                try { ShapeRenderer.PrefetchTextures(geom); } catch { }   // texturas listas antes de renderizar
                LoadLog("vista 3D: geometría y texturas hechas");
                if (!IsHandleCreated) { lock (_renderingShapes) _renderingShapes.Remove(engPath); return; }
                BeginInvoke((Action)(() =>
                {
                    try
                    {
                        bool stillSel = (_lstConsists.SelectedItem as TrainItem)?.FilePath == consistPath;
                        if (geom != null && stillSel)
                        {
                            _previewGeom = geom;
                            _trainPreview.Rotatable = true;
                            // Render a la resolución real del panel (ancho máximo, sin recuadro).
                            LoadLog("vista 3D: renderizando…");
                            RenderLive();
                            LoadLog("vista 3D: renderizada");
                        }
                    }
                    catch { }
                    lock (_renderingShapes) _renderingShapes.Remove(engPath);
                }));
            });
        }

        void OnPreviewDrag(int dx, int dy)
        {
            if (_previewGeom == null) return;
            _yaw += dx * 0.6f;
            _pitch = Math.Max(-55f, Math.Min(70f, _pitch - dy * 0.6f));
            RenderLive();
        }

        void OnPreviewReset() { if (_previewGeom == null) return; _yaw = 0; _pitch = 0; RenderLive(); }

        void RenderLive()
        {
            if (_previewGeom == null || _trainPreview.Width < 40 || _trainPreview.Height < 40) return;
            int w = Math.Min(1400, Math.Max(128, (_trainPreview.Width - 8) * 2));
            int h = Math.Min(820, Math.Max(96, (_trainPreview.Height - 36) * 2));
            TeleRedirect(_teleExplore);
            var bmp = ShapeRenderer.Render(_previewGeom, w, h, _yaw, _pitch, 1, _previewFlip, _trainPreview.CamDistance());
            if (bmp != null) _trainPreview.Image = bmp;
        }

        // Flip de la loco líder (primera con motor) del consist, leído del propio .con (fuente fiable),
        // para orientar el render 3D con la misma regla que la composición 2D / Open Rails.
        static bool GetLeadFlip(string consistPath)
        {
            try
            {
                if (string.IsNullOrEmpty(consistPath)) return false;
                var cf = OrCompat.OpenConsist(consistPath);
                var wagons = cf.Train.TrainCfg.WagonList;
                foreach (var w in wagons) if (w.IsEngine) return w.Flip;
                if (wagons.Count > 0) return wagons[0].Flip;
            }
            catch { }
            return false;
        }

        // Detecta la locomotora líder de un .con: devuelve su nombre de modelo y el tipo
        // (eléctrica/diésel/vapor) leído del .eng. Para dar de alta vehículos en la flota.
        static (string name, string kind) DetectLeadVehicle(string consistPath)
        {
            try
            {
                if (string.IsNullOrEmpty(consistPath)) return (null, null);
                var cf = OrCompat.OpenConsist(consistPath);
                var wagons = cf.Train.TrainCfg.WagonList;
                object lead = null;
                foreach (var w in wagons) if (w.IsEngine) { lead = w; break; }
                bool isEngine = lead != null;
                if (lead == null && wagons.Count > 0) lead = wagons[0];
                if (lead == null) return (null, null);

                string engName = MemberStr(lead, "Name", "FileName", "WagonName", "EngineName");
                string folder = MemberStr(lead, "Folder", "Directory", "FolderName");
                if (string.IsNullOrEmpty(engName)) return (null, null);

                string kind = isEngine ? "locomotora" : "vagón";
                if (isEngine)
                {
                    // .../TRAINS/CONSISTS/x.con  →  .../TRAINS/TRAINSET/<folder>/<engName>.eng
                    var consistsDir = SysPath.GetDirectoryName(consistPath);
                    var trainsDir = SysPath.GetDirectoryName(consistsDir);
                    if (trainsDir != null && folder != null)
                    {
                        var eng = SysPath.Combine(trainsDir, "TRAINSET", folder, engName + ".eng");
                        string k = DetectEngineKind(eng);
                        if (k != null) kind = k;
                    }
                }
                return (engName, kind);
            }
            catch { return (null, null); }
        }

        // Estima los km REALES conducidos leyendo el .save de OR generado en esta sesión.
        // OR no expone la distancia de forma documentada, así que se busca el mayor valor
        // "de distancia" (float en metros) acotado por la longitud de la ruta (patMeters).
        // Devuelve km, o 0 si no hay .save de esta sesión o no se puede acotar.
        static double EstimateKmFromSave(DateTime sinceUtc, double patMeters)
        {
            try
            {
                if (patMeters <= 0) return 0;   // sin ruta con la que acotar, no arriesgamos
                string dir = ResumeDialog.OrDataFolder;   // donde guarda el simulador (no la carpeta de SelectOR)
                if (!Directory.Exists(dir)) return 0;

                string best = null; DateTime bestT = DateTime.MinValue;
                foreach (var f in Directory.GetFiles(dir, "*.save"))
                {
                    var tt = File.GetLastWriteTimeUtc(f);
                    if (tt >= sinceUtc.AddMinutes(-2) && tt > bestT) { bestT = tt; best = f; }
                }
                if (best == null) return 0;   // no se guardó ninguna partida en esta sesión

                var bytes = File.ReadAllBytes(best);
                double hi = patMeters * 1.05, lo = 1000.0;   // entre 1 km y la longitud de la ruta
                float bestV = 0f;
                for (int i = 0; i + 4 <= bytes.Length; i++)
                {
                    float v = BitConverter.ToSingle(bytes, i);
                    if (!float.IsNaN(v) && !float.IsInfinity(v) && v >= lo && v <= hi && v > bestV) bestV = v;
                }
                return bestV > 0 ? Math.Round(bestV / 1000.0, 1) : 0;
            }
            catch { return 0; }
        }

        // Lee un miembro string (campo o propiedad) probando varios nombres posibles.
        static string MemberStr(object obj, params string[] names)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            foreach (var n in names)
            {
                var p = t.GetProperty(n);
                if (p != null && p.PropertyType == typeof(string)) { var v = p.GetValue(obj) as string; if (!string.IsNullOrEmpty(v)) return v; }
                var f = t.GetField(n);
                if (f != null && f.FieldType == typeof(string)) { var v = f.GetValue(obj) as string; if (!string.IsNullOrEmpty(v)) return v; }
            }
            return null;
        }

        static string DetectEngineKind(string engPath)
        {
            try
            {
                if (!File.Exists(engPath)) return null;
                string t = ReadMstsText(engPath);
                if (string.IsNullOrEmpty(t)) return null;
                string low = t.ToLowerInvariant();
                if (low.Contains("electric")) return "eléctrica";
                if (low.Contains("diesel")) return "diésel";
                if (low.Contains("steam")) return "vapor";
            }
            catch { }
            return null;
        }

        // Mapa GENERAL de la ruta (toda la red de vías), sin información de un Path concreto.
        void OpenRouteMap()
        {
            if (_curRoute == null) { Warn(Tr("Selecciona primero una ruta.")); return; }
            using (var dlg = new PathMapDialog(_curRoute.Path ?? "")) dlg.ShowDialog(this);
        }

        void OpenPathMap()
        {
            var p = CurrentPath();
            if (p == null) { Warn(Tr("Selecciona un recorrido.")); return; }
            using (var dlg = new PathMapDialog(p.FilePath, _curRoute?.Path ?? "", p.Start, p.End)) dlg.ShowDialog(this);
        }

        void OpenComposition()
        {
            var c = _lstConsists.SelectedItem as TrainItem;
            if (c == null) { Warn(Tr("Selecciona un tren.")); return; }
            if (_curFolder == null) { Warn(Tr("Selecciona una carpeta de contenido.")); return; }
            using (var dlg = new CompositionDialog(c.FilePath, _curFolder.Path, c.Name)) dlg.ShowDialog(this);
        }

        void SetBannerAsync(string routePath, string imgPath)
        {
            try { if (!IsHandleCreated) return; BeginInvoke((Action)(() =>
            {
                if (_curRoute != null && _curRoute.Path == routePath)
                { var b = ImageCache.Get(imgPath, 1280, 320, null); if (b != null) _banner.Image = b; }
            })); } catch { }
        }

        void SetPreviewAsync(string consistPath, string imgPath)
        {
            try { if (!IsHandleCreated) return; BeginInvoke((Action)(() =>
            {
                var c = _lstConsists.SelectedItem as TrainItem;
                if (c?.FilePath == consistPath) { var b = ImageCache.Get(imgPath, 560, 300, null); if (b != null) _trainPreview.Image = b; }
            })); } catch { }
        }

        // ============================ Lanzar ============================
        void Play()
        {
            if (_curRoute == null) { Warn(Tr("Selecciona primero una ruta.")); return; }
            var args = BuildArguments(out string error);
            if (args == null) { Warn(error); return; }
            Launch(args);
        }

        void PlayMultiplayer()
        {
            if (_curRoute == null) { Warn(Tr("Selecciona primero una ruta.")); return; }
            var c = _lstConsists.SelectedItem as TrainItem;
            var p = CurrentPath();
            if (c == null || p == null) { Warn(Tr("Configura recorrido y tren en «Exploración» antes de conectar.")); return; }

            // El usuario tiene que cumplir las reglas de Open Rails o la conexión falla.
            if (!MPUserValid(_txtMPUser.Text.Trim()))
            {
                Warn(Tr("El usuario de Multijugador debe tener de 4 a 10 caracteres, sin espacios ni ' \" -, y no puede empezar por un número."));
                try { _txtMPUser.Focus(); } catch { }
                return;
            }

            // Puerto: el que abre el servidor o al que se conecta el cliente. Si no es válido, no se lanza (antes
            // se ignoraba sin avisar y se usaba el último guardado).
            if (!int.TryParse(_txtMPPort.Text.Trim(), out int port) || port < 1 || port > 65535)
            {
                Warn(Tr("El puerto debe ser un número entre 1 y 65535."));
                try { _txtMPPort.Focus(); _txtMPPort.SelectAll(); } catch { }
                return;
            }
            // Cliente: hace falta la IP del servidor. (Servidor: no se usa; el campo enseña la IP propia.)
            string host = _txtMPHost.Text.Trim();
            if (_rbClient.Checked && host.Length == 0)
            {
                Warn(Tr("Escribe la IP del servidor al que te quieres unir."));
                try { _txtMPHost.Focus(); } catch { }
                return;
            }

            // Guardar ajustes MP en el registro (RunActivity los lee de ahí)
            try
            {
                _settings.Multiplayer_User = _txtMPUser.Text.Trim();
                if (_rbClient.Checked) _settings.Multiplayer_Host = host;
                _settings.Multiplayer_Port = port;
                _settings.Save();
            }
            catch { }

            string time = _hourSlider.TimeText;
            int season = Math.Max(0, _segSeason.SelectedIndex);
            int weather = Math.Max(0, _segWeather.SelectedIndex);
            string flag = _rbServer.Checked ? "-multiplayerserver" : "-multiplayerclient";
            var args = $"{flag} -explorer \"{p.FilePath}\" \"{c.FilePath}\" {time} {season} {weather}";
            Launch(args);
        }

        string BuildArguments(out string error)
        {
            error = null;
            switch (_activePage)
            {
                case 1: // Actividad
                    var a = _lstActivities.SelectedItem as Activity;
                    if (a == null) { error = Tr("Selecciona una actividad (o usa «Exploración»)."); return null; }
                    return $"-start -activity \"{a.FilePath}\"";
                case 2: // Exploración
                    var c = _lstConsists.SelectedItem as TrainItem;
                    var p = CurrentPath();
                    if (c == null) { error = Tr("Selecciona un tren."); return null; }
                    if (p == null) { error = Tr("Esta ruta no tiene recorridos disponibles."); return null; }
                    {
                        string time = _hourSlider.TimeText;
                        int season = Math.Max(0, _segSeason.SelectedIndex);
                        int weather = Math.Max(0, _segWeather.SelectedIndex);
                        string verb = _chkExploreActivity.Checked ? "-exploreactivity" : "-explorer";
                        return $"-start {verb} \"{p.FilePath}\" \"{c.FilePath}\" {time} {season} {weather}";
                    }
                case 3: // Horarios
                    var set = _cboTTSet.SelectedItem as TimetableInfo;
                    var tt = _cboTT.SelectedItem;
                    var tr = _cboTTTrain.SelectedItem;
                    if (set == null) { error = Tr("Esta ruta no tiene horarios."); return null; }
                    if (tt == null || tr == null) { error = Tr("Selecciona horario y tren."); return null; }
                    {
                        int day = 0;   // día por defecto (se eliminó el selector de día)
                        int season = Math.Max(0, _segTTSeason.SelectedIndex);
                        int weather = Math.Max(0, _segTTWeather.SelectedIndex);
                        // Sin comillas dentro: un nombre con «"» añadiría argumentos al simulador.
                        string ttName = (tt?.ToString() ?? "").Replace("\"", ""), trName = (tr?.ToString() ?? "").Replace("\"", "");
                        return $"-start -timetable \"{set.fileName}\" \"{ttName}:{trName}\" {day} {season} {weather}";
                    }
            }
            error = Tr("Modo no soportado.");
            return null;
        }

        async void Launch(string args)
        {
            var exe = SysPath.Combine(AppContext.BaseDirectory, "RunActivity.exe");
            if (!File.Exists(exe)) { Warn(Tr("No se encuentra RunActivity.exe junto al selector.")); return; }
            _prefs.Save();
            // Teleindicador: el destino elegido para este tren se pone en su carpeta antes de que OR lo cargue.
            if (args.StartsWith("-start", StringComparison.OrdinalIgnoreCase))
                TeleApplyForLaunch(CurrentDrivenConsist());

            // Empresas: si estoy "de servicio", abro el servicio antes de lanzar.
            string serviceId = null;
            if (EmpOnDuty)
            {
                SetBusy(Tr("Registrando servicio…"));
                serviceId = await EmpStartService();
                SetIdle();
                if (serviceId == null)
                {
                    string why = _empStartFailReason;
                    string head = string.IsNullOrEmpty(why)
                        ? Tr("No se pudo registrar el servicio en la empresa.")
                        : why;
                    var r = MessageBox.Show(this,
                        head + "\n\n" + Tr("¿Conducir de todas formas sin registrarlo para la empresa?"),
                        "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r != DialogResult.Yes) return;
                }
            }

            try
            {
                var p = Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false });
                _preLaunchState = WindowState;   // al volver del simulador se deja exactamente así
                WindowState = FormWindowState.Minimized;
                if (p != null)
                {
                    bool svc = serviceId != null;   // servicio de empresa (con tiempo/viajeros) o conducción normal
                    _pendingServiceId = serviceId;
                    _svcOpenedUtc = svc ? DateTime.UtcNow : (DateTime?)null;
                    if (!svc) _driveStartUtc = DateTime.UtcNow;
                    CaptureDrivenTrain();           // tren conducido (para ponerse de servicio desde la barra superior)
                    StartKmTracking(withPax: true); // posición para el mapa y viajeros (en servicio o conducción libre)
                    ShowServiceHud(svc);            // HUD sobre OR (en servicio o no)
                    if (_prefs.CabHudOn) ShowCabHud();   // pupitre, si lo dejaste abierto la última vez
                    if (_prefs.ChatHudOn) ShowChatHud(); // chat de empresa, si lo dejaste abierto
                    ShowDriveBar();                 // barra oculta arriba en el centro (HUD · mapa · servicio)
                    p.EnableRaisingEvents = true;
                    p.Exited += (s, e) => { try { BeginInvoke(new Action(OnDriveReturned)); } catch { } };
                }
            }
            catch (Exception ex) { Warn(Tr("No se pudo iniciar el simulador:\n") + ex.Message); }
        }

        // Partidas guardadas de OR (F2 en el simulador): reanudar o repetir.
        void OpenResume()
        {
            using (var dlg = new ResumeDialog())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedSaveFile))
                    Launch($"{dlg.ModeFlag} \"{dlg.SelectedSaveFile}\"");
            }
        }

        // ============================ Favoritos e imágenes ============================
        void ToggleRouteFavorite()
        {
            var r = _lstRoutes.SelectedItem as Route;
            if (r == null) return;
            if (!_prefs.Favorites.Add(r.Path)) _prefs.Favorites.Remove(r.Path);
            _prefs.Save();
            _lstRoutes.Invalidate();
            if (_chkFavOnly.Checked) RefreshRouteList();
        }

        void ToggleTrainFavorite()
        {
            var c = _lstConsists.SelectedItem as TrainItem;
            if (c == null) return;
            if (!_prefs.FavoriteTrains.Add(c.FilePath)) _prefs.FavoriteTrains.Remove(c.FilePath);
            _prefs.Save();
            _lstConsists.Invalidate();
            if (_chkTrainFavOnly.Checked) RefreshConsistList();
        }

        // ============================ Utilidades ============================
        void UpdateStatus()
        {
            UpdateTrainShortcuts();   // estado del tren + «Comprar este tren» en Exploración/Horarios
            if (_curRoute == null) { _lblStatus.Text = Tr("Selecciona una ruta para empezar."); return; }
            string r = Tr("Ruta") + ": " + _curRoute.Name;
            switch (_activePage)
            {
                case 0: _lblStatus.Text = r; break;
                case 1: _lblStatus.Text = $"{r}   ·   {Tr("Actividad")}: {((_lstActivities.SelectedItem as Activity)?.Name ?? "—")}"; break;
                case 2:
                    _lblStatus.Text = $"{r}   ·   {Tr("Tren")}: {((_lstConsists.SelectedItem as TrainItem)?.Name ?? "—")}"
                        + TeleSuffix(_teleExplore) + EmpSuffix(_lstConsists.SelectedItem as TrainItem);
                    break;
                case 3:
                    var ttr = _cboTTTrain?.SelectedItem as Orts.Formats.OR.TimetableFileLite.TrainInformation;
                    string ttc = ttr != null ? (!string.IsNullOrWhiteSpace(ttr.LeadingConsist) ? ttr.LeadingConsist : ttr.Consist) : null;
                    _lblStatus.Text = $"{r}   ·   {Tr("Horario")}: {(_cboTT.SelectedItem?.ToString() ?? "—")}  ·  {Tr("Tren")}: {(_cboTTTrain.SelectedItem?.ToString() ?? "—")}"
                        + TeleSuffix(_teleTT) + EmpSuffix(ResolveConsist(ttc));
                    break;
                case 4: _lblStatus.Text = $"{r}   ·   {Tr("Multijugador")}: {(_rbServer.Checked ? Tr("Servidor") : Tr("Cliente"))}"; break;
            }
            if (_empOnDutyCompany != null && _activePage != PageEmpresas)
                _lblStatus.Text += $"   ·   🟢 {Tr("De servicio: ")}{_empOnDutyCompany.Name}";
        }

        // Sufijo "   ·   Empresa(s): X, Y" para la barra inferior si el tren pertenece a empresas mías.
        string EmpSuffix(TrainItem c)
        {
            var cos = ConsistCompanies(c);
            if (cos.Count == 0) return "";
            return $"   ·   🏢 {Tr(cos.Count == 1 ? "Empresa" : "Empresas")}: {string.Join(", ", cos)}";
        }

        void SetBusy(string msg) { _lblStatus.Text = "⏳  " + msg; _btnPlay.Enabled = false; UseWaitCursor = true; }
        void SetIdle() { _btnPlay.Enabled = true; UseWaitCursor = false; }
        void Warn(string m) => MessageBox.Show(m, "Selector de Trenes y Rutas", MessageBoxButtons.OK, MessageBoxIcon.Information);

        static List<T> SafeList<T>(Func<List<T>> f)
        {
            try { return f() ?? new List<T>(); } catch { return new List<T>(); }
        }

        void LaunchSibling(string exeName, string args)
        {
            var exe = SysPath.Combine(AppContext.BaseDirectory, exeName);
            if (!File.Exists(exe)) { Warn(Tr("No se encuentra ") + exeName); return; }
            try { Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false }); }
            catch (Exception ex) { Warn(ex.Message); }
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5 && _curFolder != null) { lock (_consistCache) _consistCache.Remove(_curFolder.Path); OnFolderChanged(); e.Handled = true; }
        }

        // Ventana restaurada (no maximizada): Windows a veces la devuelve con el ALTO de la pantalla completa
        // (e incluso algo más), dejando la barra inferior y el botón CONDUCIR fuera de la vista. Aquí se
        // limita cualquier cambio de tamaño/posición al área útil del monitor (sin tapar la barra de tareas).
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

        protected override void WndProc(ref Message m)
        {
            const int WM_WINDOWPOSCHANGING = 0x0046;
            const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002;
            if (m.Msg == WM_WINDOWPOSCHANGING && m.LParam != IntPtr.Zero && IsHandleCreated && !IsZoomed(Handle) && !IsIconic(Handle))
            {
                try
                {
                    var wp = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
                    var wa = Screen.FromHandle(Handle).WorkingArea;
                    bool changed = false;
                    if ((wp.flags & SWP_NOSIZE) == 0)
                    {
                        int maxW = wa.Width + 14, maxH = wa.Height + 7;   // +márgenes invisibles del marco de Windows 10/11
                        if (wp.cx > maxW) { wp.cx = maxW; changed = true; }
                        if (wp.cy > maxH) { wp.cy = maxH; changed = true; }
                    }
                    if ((wp.flags & SWP_NOMOVE) == 0)
                    {
                        int h = (wp.flags & SWP_NOSIZE) == 0 ? wp.cy : Height;
                        if (wp.y < wa.Top) { wp.y = wa.Top; changed = true; }
                        if (wp.y + h > wa.Bottom + 7) { wp.y = Math.Max(wa.Top, wa.Bottom + 7 - h); changed = true; }
                    }
                    if (changed) System.Runtime.InteropServices.Marshal.StructureToPtr(wp, m.LParam, false);
                }
                catch { }
            }
            base.WndProc(ref m);
        }

        void ShowAbout()
        {
            bool check;
            using (var dlg = new AboutDialog()) { dlg.ShowDialog(this); check = dlg.CheckForUpdatesRequested; }
            if (check) CheckForUpdates(manual: true);
        }

        protected override void OnFormClosing(FormClosingEventArgs e) { _prefs.Save(); base.OnFormClosing(e); }
    }
}
