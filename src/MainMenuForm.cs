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
        RouteCardList _lstRoutes;
        BannerPanel _banner;
        TextBox _routeDescBox;
        Panel _pageHost;
        RoundButton[] _pills;
        RoundButton _btnPlay, _btnConnect;
        Panel _empDutyHost;   // grupo "Ponerme de servicio" + vehículo en la barra inferior (Empresas)
        Label _lblStatus;

        // Actividad
        RoundedInput _actSearch;
        ActivityCardGrid _lstActivities;
        TextBox _txtBriefing;
        Panel _pageRuta, _pageActividad, _pageExplora, _pageHorarios, _pageMulti, _pageEditor, _pageEmpresas;
        // Exploración
        RoundedInput _consistSearch;
        ViewModeToggle _trainView;
        CheckBox _chkTrainFavOnly;
        ComboBox _cboTrainCompany;   // filtro de trenes por empresa (Exploración)
        Control _trainCompanyHost;   // fila etiqueta+combo del filtro (se oculta si no hay empresas)
        TrainCardGrid _lstConsists;
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
            var pad = new Padding(Theme.Px(14), Theme.Px(12), Theme.Px(16), bottom);
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
            ("vistas2d",   9, "Preparando los trenes y sus vistas 2D…"),
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
        void ApplyTabIcons() => _header?.PerformLayout();

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

        // Barra superior ÚNICA: logotipo, las secciones (pestañas) y las herramientas (contenido, reanudar,
        // opciones y acerca de). Las pestañas miden lo que su texto; si no caben, primero se quitan los
        // rótulos de las herramientas, luego los iconos de las pestañas y por último se acortan.
        static readonly string[] TabNames = { "Ruta", "Actividad", "Exploración", "Horarios", "Multijugador", "Editor de composiciones", "Empresas" };
        static readonly string[] TabKinds = { "map", "activity", "explore", "clock", "globe", "train", "bank" };

        void BuildHeader()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Theme.BgSidebar, Name = "top" };
            var stripe = new LiveryStripe { Dock = DockStyle.Bottom };   // franja verde: separa cabecera y contenido
            var logo = new SelectorLogo { Location = new Point(16, 11), Width = 170, Height = 40 };
            var tip = new ToolTip();
            // La versión de Open Rails ya no ocupa sitio en la barra: sale al pasar el ratón por el logotipo.
            _lblVersion = new Label { Text = "", Visible = false };
            _lblVersion.TextChanged += (s, e) => tip.SetToolTip(logo, _lblVersion.Text);

            var lblPack = new Label { Text = Tr("Contenido"), ForeColor = Theme.Subtle, AutoSize = true, BackColor = Theme.BgSidebar, Font = Theme.Font(8.5f) };
            _cboFolder = new ThemeCombo { Width = 190, DropDownHeight = 300 };
            StyleCombo(_cboFolder);
            _cboFolder.SelectedIndexChanged += (s, e) => OnFolderChanged();

            var btnResume = MakeChip(Tr("Reanudar"), 120, "resume");
            btnResume.Click += (s, e) => OpenResume();
            var btnOptions = MakeChip("", 36, "gear");
            btnOptions.Click += (s, e) => LaunchSibling("Menu.exe", "");
            tip.SetToolTip(btnOptions, Tr("Opciones OR"));
            var btnAbout = MakeChip("", 36, "info");
            btnAbout.Click += (s, e) => ShowAbout();
            tip.SetToolTip(btnAbout, Tr("Acerca de SelectOR"));
            tip.SetToolTip(btnResume, Tr("Reanudar"));

            _pills = new RoundButton[TabNames.Length];
            for (int i = 0; i < TabNames.Length; i++)
            {
                int idx = i;
                var pill = new RoundButton { Text = Tr(TabNames[i]), GlyphKind = TabKinds[i], Tab = true, TabFill = true, FontSize = 10f, Margin = new Padding(0) };
                pill.Click += (s, e) => ShowPage(idx);
                _pills[i] = pill;
                top.Controls.Add(pill);
            }

            top.Controls.AddRange(new Control[] { logo, _lblVersion, lblPack, _cboFolder, btnResume, btnOptions, btnAbout });
            top.Layout += (s, e) =>
            {
                int band = Math.Max(Theme.Px(28), top.ClientSize.Height - stripe.Height);
                int chipH = Math.Max(Theme.Px(24), Math.Min(Theme.Px(34), band - Theme.Px(14)));
                int chipY = Math.Max(0, (band - chipH) / 2);
                int gap = Theme.Px(6), edge = Theme.Px(14);
                logo.Height = Math.Max(Theme.Px(26), Math.Min(Theme.Px(38), band - Theme.Px(10)));
                logo.Width = Theme.Px(165);
                logo.Location = new Point(edge, (band - logo.Height) / 2);
                int tabsX = logo.Right + Theme.Px(14);

                using var fTab = Theme.Font(10f, FontStyle.Bold);
                int TabW(int i, bool glyph, bool shortEditor)
                {
                    string t = i == PageEditor && shortEditor ? Tr("Editor") : Tr(TabNames[i]);
                    return TextRenderer.MeasureText(t, fTab, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.Px(24) + (glyph ? Theme.Px(26) : 0);
                }
                int TabsW(bool glyph, bool shortEd) { int w = 0; for (int i = 0; i < _pills.Length; i++) w += TabW(i, glyph, shortEd) + Theme.Px(2); return w; }

                // Herramientas, de más a menos holgadas; se elige la primera con la que caben las pestañas.
                int right = top.ClientSize.Width - edge;
                using var fChip = Theme.Font(btnResume.FontSize);
                int resumeFull = TextRenderer.MeasureText(Tr("Reanudar"), fChip).Width + 22 + 10 + 28;   // texto + icono + separación + márgenes (como lo dibuja el botón)
                var configs = new[] { (label: true, resume: true, combo: 190), (label: false, resume: true, combo: 170), (label: false, resume: false, combo: 150) };
                var pick = configs[configs.Length - 1]; bool glyphs = false, shortEd = true;
                foreach (var withGlyphs in new[] { true, false })
                {
                    bool found = false;
                    foreach (var c in configs)
                        foreach (var se in new[] { false, true })
                        {
                            int tools = Theme.Px(36) * 2 + gap * 2 + (c.resume ? resumeFull : Theme.Px(36)) + gap + Theme.Px(c.combo) + Theme.Px(14)
                                      + (c.label ? TextRenderer.MeasureText(lblPack.Text, lblPack.Font).Width + gap : 0);
                            if (tabsX + TabsW(withGlyphs, se) + Theme.Px(10) <= right - tools) { pick = c; glyphs = withGlyphs; shortEd = se; found = true; goto done; }
                        }
                    done:
                    if (found) break;
                }

                btnAbout.Size = btnOptions.Size = new Size(Theme.Px(36), chipH);
                btnAbout.Location = new Point(right - btnAbout.Width, chipY);
                btnOptions.Location = new Point(btnAbout.Left - btnOptions.Width - gap, chipY);
                btnResume.Text = pick.resume ? Tr("Reanudar") : "";
                btnResume.Size = new Size(pick.resume ? resumeFull : Theme.Px(36), chipH);
                btnResume.Location = new Point(btnOptions.Left - btnResume.Width - gap, chipY);
                _cboFolder.Width = Theme.Px(pick.combo);
                _cboFolder.Location = new Point(btnResume.Left - _cboFolder.Width - Theme.Px(12), (band - _cboFolder.Height) / 2);
                lblPack.Visible = pick.label;
                lblPack.Location = new Point(_cboFolder.Left - lblPack.Width - gap, (band - lblPack.PreferredHeight) / 2);

                int tabsRight = (pick.label ? lblPack.Left : _cboFolder.Left) - Theme.Px(10);
                int need = TabsW(glyphs, shortEd), avail = Math.Max(10, tabsRight - tabsX);
                double k = need > avail ? avail / (double)need : 1.0;   // último recurso: encoger todas por igual
                int x = tabsX, tabH = Math.Max(Theme.Px(28), Math.Min(Theme.Px(42), band - Theme.Px(10)));
                for (int i = 0; i < _pills.Length; i++)
                {
                    var p = _pills[i];
                    string t = i == PageEditor && shortEd ? Tr("Editor") : Tr(TabNames[i]);
                    if (p.Text != t) p.Text = t;
                    if (p.ShowGlyph != glyphs) { p.ShowGlyph = glyphs; p.Invalidate(); }
                    int w = (int)(TabW(i, glyphs, shortEd) * k);
                    p.Bounds = new Rectangle(x, (band - tabH) / 2, w, tabH);
                    x += w + Theme.Px(2);
                }
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
            BuildCancelPendingButton(dutyFlow);   // el maquinista cancela el servicio pendiente (MainMenuForm.CancelarServicio.cs)
            _empDutyHost.Controls.Add(dutyFlow);

            _lblStatus = new StatusPills { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Subtle, BackColor = Theme.Surface, Padding = new Padding(18, 0, 0, 0), Font = Theme.Font(10f) };

            bottom.Controls.Add(_lblStatus);
            bottom.Controls.Add(rightCol);
            bottom.Controls.Add(_empDutyHost);
            bottom.Controls.Add(stripe);
            _bottomBar = bottom;   // se añade dentro del panel central en BuildCenter
        }

        Label _routesCap;
        FlowLayoutPanel _routeChips;

        // Lateral de RUTAS: buscador, «Todas / ★ Favoritas» y las rutas en tarjetas con su imagen.
        void BuildSidebar()
        {
            var left = new Panel { Dock = DockStyle.Left, Width = 340, BackColor = Theme.BgSidebar, Padding = new Padding(14, 12, 8, 12), Name = "left" };
            _routesCap = new Label { Text = Tr("RUTAS"), Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Subtle, BackColor = Theme.BgSidebar, Font = Theme.Font(8f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };

            _routeSearch = new RoundedInput(Tr("Buscar ruta…")) { Dock = DockStyle.Top, Margin = new Padding(0) };
            _routeSearch.Box.TextChanged += (s, e) => RefreshRouteList();
            var searchHost = Pad(_routeSearch, 0, 6, 0, 6);
            searchHost.Dock = DockStyle.Top;

            // «Solo favoritas» sigue existiendo (lo consultan otras partes), pero lo mandan las pastillas.
            _chkFavOnly = MakeCheck("★  Solo favoritas");
            _chkFavOnly.Visible = false;
            _chkFavOnly.CheckedChanged += (s, e) => RefreshRouteList();
            _routeChips = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.BgSidebar, Margin = new Padding(0), Padding = new Padding(0, 0, 0, 4) };
            var chAll = BankChip(Tr("Todas"), true); var chFav = BankChip("★ " + Tr("Favoritas"), false);
            chAll.BaseColor = chFav.BaseColor = Theme.Surface;
            chAll.Click += (s, e) => { SetChipActive(_routeChips, 0); _chkFavOnly.Checked = false; };
            chFav.Click += (s, e) => { SetChipActive(_routeChips, 1); _chkFavOnly.Checked = true; };
            _routeChips.Controls.Add(chAll); _routeChips.Controls.Add(chFav);

            _lstRoutes = new RouteCardList
            {
                Dock = DockStyle.Fill,
                TitleOf = o => ((Route)o).Name,
                SubOf = o => RouteCardSub((Route)o),
                FavOf = o => _prefs.Favorites.Contains(((Route)o).Path),
                ImageOf = o => RouteCardImage((Route)o),
            };
            _lstRoutes.SelectedIndexChanged += (s, e) => OnRouteChanged();
            // La portada de bienvenida se quita en cuanto el usuario elige una ruta (aunque sea la ya cargada).
            _lstRoutes.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left && _lstRoutes.SelectedItem != null) DismissRutaWelcome(); };
            _lstRoutes.KeyDown += (s, e) => { if (_lstRoutes.SelectedItem != null && e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.Enter) DismissRutaWelcome(); };
            _lstRoutes.ItemActivated += o => ToggleRouteFavorite();
            var routeCtx = MenuStyle.Apply(new ContextMenuStrip());
            var miFav = new ToolStripMenuItem(Tr("Añadir / quitar de favoritas"));
            miFav.Click += (s, e) => ToggleRouteFavorite();
            routeCtx.Items.Add(miFav);
            _lstRoutes.ContextMenuStrip = routeCtx;

            left.Controls.Add(_lstRoutes);
            left.Controls.Add(_routeChips);
            left.Controls.Add(searchHost);
            left.Controls.Add(_routesCap);
            left.Controls.Add(_chkFavOnly);

            _routesOverlay = new ListStatePanel { Bg = Theme.BgSidebar, Icon = "🗺️" };
            left.Controls.Add(_routesOverlay);
            void syncRoutesOverlay() { if (_routesOverlay != null) { _routesOverlay.Bounds = _lstRoutes.Bounds; if (_routesOverlay.Visible) _routesOverlay.BringToFront(); } }
            _lstRoutes.SizeChanged += (s, e) => syncRoutesOverlay();
            _lstRoutes.LocationChanged += (s, e) => syncRoutesOverlay();
            left.SizeChanged += (s, e) => syncRoutesOverlay();
            syncRoutesOverlay();
            _sidebar = left;
        }

        // Imagen de la tarjeta de una ruta: la de cabecera si la hay (es apaisada), si no la miniatura.
        Image RouteCardImage(Route r)
        {
            try
            {
                var imgs = ContentImages.ForRoute(r.Path);
                string f = !string.IsNullOrEmpty(imgs.banner) ? imgs.banner : imgs.thumb;
                if (string.IsNullOrEmpty(f)) return null;
                return ImageCache.Get(f, 420, 120, SafeInvalidateRoutes);
            }
            catch { return null; }
        }

        // «3 actividades · 2 horarios» de cada ruta: se cuentan los archivos (rápido), una vez y en segundo plano.
        readonly Dictionary<string, string> _routeSub = new(StringComparer.OrdinalIgnoreCase);
        string RouteCardSub(Route r)
        {
            if (r?.Path == null) return "";
            lock (_routeSub) { if (_routeSub.TryGetValue(r.Path, out var v)) return v; _routeSub[r.Path] = ""; }
            string path = r.Path;
            System.Threading.Tasks.Task.Run(() =>
            {
                int acts = 0, tts = 0;
                try { var d = SysPath.Combine(path, "ACTIVITIES"); if (Directory.Exists(d)) acts = Directory.GetFiles(d, "*.act").Length; } catch { }
                try
                {
                    var d = SysPath.Combine(path, "OPENRAILS");
                    if (Directory.Exists(d)) tts = Directory.GetFiles(d, "*.timetable_or").Length + Directory.GetFiles(d, "*.timetable-or").Length
                                                 + Directory.GetFiles(d, "*.timetablelist_or").Length + Directory.GetFiles(d, "*.timetablelist-or").Length;
                }
                catch { }
                var parts = new List<string>();
                if (acts > 0) parts.Add(string.Format(Tr(acts == 1 ? "{0} actividad" : "{0} actividades"), acts));
                if (tts > 0) parts.Add(string.Format(Tr(tts == 1 ? "{0} horario" : "{0} horarios"), tts));
                if (parts.Count == 0) parts.Add(Tr("solo exploración"));
                lock (_routeSub) _routeSub[path] = string.Join("  ·  ", parts);
                SafeInvalidateRoutes();
            });
            return "";
        }

        void BuildCenter()
        {
            var center = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(12, 6, 16, 84), Name = "center" };

            _pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

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
            _center = center;

            ShowPage(0);   // SelectOR abre siempre en Ruta (no en la última pestaña usada)
        }

        // ============================ RUTA ============================
        // Portada (imagen, datos en pastillas y descripción con «Leer más»); debajo, el mapa en vivo (o la
        // imagen y «Ver mapa de la ruta» si no hay trazado) y, a la derecha, «Empezar a conducir» y cifras.
        RouteHero _rutaHero;
        RouteGoPanel _rutaGo;
        RouteWelcome _rutaWelcome;
        (string last, string lastSub, string km, string kmSub) _routeStats = ("—", "", "—", "");

        Panel BuildRutaPage()
        {
            var page = new Panel { BackColor = Theme.Bg };
            _rutaHero = new RouteHero { Dock = DockStyle.Top, Height = 170, Caption = Tr("RUTA"), LblMore = Tr("Leer más"), LblLess = Tr("Leer menos"), Placeholder = Tr("Selecciona una ruta para empezar") };
            _rutaHero.ImageOf = () => _banner?.Image;
            _rutaHero.HeightChanged += FitRutaHero;
            _rutaHero.SizeChanged += (s, e) => FitRutaHero();
            var gap = new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Theme.Bg };

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(0, 0, 14, 0) };
            _banner = new BannerPanel { Dock = DockStyle.Fill };
            _banner.ImageChanged += () => _rutaHero?.Invalidate();
            _rutaMap = new RouteLiveMap { Dock = DockStyle.Fill, Visible = false };
            var btnMap = new RoundButton { Text = Tr("Ver mapa de la ruta"), GlyphKind = "map", Dock = DockStyle.Fill, Height = 40, Radius = 9, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 10f };
            btnMap.Click += (s, e) => OpenRouteMap();
            _rutaBtnHost = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Theme.Bg, Padding = new Padding(0, 10, 0, 2) };
            _rutaBtnHost.Controls.Add(btnMap);
            left.Controls.Add(_rutaMap); left.Controls.Add(_banner); left.Controls.Add(_rutaBtnHost);

            _rutaGo = new RouteGoPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Caption = Tr("EMPEZAR A CONDUCIR") };
            _rutaGo.GoTo += ShowPage;
            body.Controls.Add(left, 0, 0); body.Controls.Add(_rutaGo, 1, 0);

            // La descripción completa sigue en esta caja (oculta): la usa la portada y el resto del programa.
            _routeDescBox = new TextBox { Visible = false, Multiline = true, ReadOnly = true };
            page.Controls.Add(body); page.Controls.Add(gap); page.Controls.Add(_rutaHero); page.Controls.Add(_routeDescBox);
            // Hasta que se elige una ruta: bienvenida con el logotipo, encima de todo.
            _rutaWelcome = new RouteWelcome
            {
                Title = Tr("Elige una ruta para empezar"),
                Sub = Tr("Las rutas de tu contenido están en la lista de la izquierda. Elige una para ver su mapa, sus actividades, recorridos y horarios."),
                Arrow = "←  " + Tr("Elige una ruta en la lista"),
                Footer = "SelectOR " + Updater.CurrentVersionText,
                Copyright = Tr("© 2026 David MZP. SelectOR. Todos los derechos reservados.")
            };
            // Novedades de esta versión, en el idioma de SelectOR
            var notes = ReleaseNotes.Load(I18n.English);
            _rutaWelcome.News = notes.Items;
            _rutaWelcome.NewsTitle = string.Format(Tr("NOVEDADES DE LA VERSIÓN {0}"), notes.Version.Length > 0 ? notes.Version : Updater.CurrentVersionText);
            _lblVersion.TextChanged += (s, e) => { _rutaWelcome.Footer = "SelectOR " + Updater.CurrentVersionText + (_lblVersion.Text.Length > 0 ? "   ·   " + _lblVersion.Text.Replace("  ", " ") : ""); _rutaWelcome.Invalidate(); };
            page.Controls.Add(_rutaWelcome);
            // sin acoplar: cubre la página entera (portada incluida)
            page.Resize += (s, e) => _rutaWelcome.Bounds = page.ClientRectangle;
            _rutaWelcome.Bounds = page.ClientRectangle;
            _rutaWelcome.BringToFront();

            _rutaLiveTimer = new Timer { Interval = RutaLiveIntervalMs };
            _rutaLiveTimer.Tick += async (s, e) => await RutaLiveTick();
            return page;
        }

        // Al abrir SelectOR la portada sale primero, aunque la última ruta ya esté cargada detrás.
        bool _rutaWelcomeHold = true;

        void DismissRutaWelcome()
        {
            if (!_rutaWelcomeHold) return;
            _rutaWelcomeHold = false;
            UpdateRouteOverview();
        }

        void FitRutaHero()
        {
            if (_rutaHero == null || _rutaHero.Width <= 0) return;
            int h = _rutaHero.NeededHeight(_rutaHero.Width);
            if (Math.Abs(_rutaHero.Height - h) > 1) _rutaHero.Height = h;
        }

        // Portada y cifras de la ruta elegida (se llama al elegirla y cada vez que llega algo nuevo).
        void UpdateRouteOverview()
        {
            if (_rutaHero == null || _rutaGo == null) return;
            var r = _curRoute;
            int stations = 0;
            if (r?.Path != null && _rutaMapCache.TryGetValue(r.Path, out var md) && md?.StName != null) stations = md.StName.Length;
            int ttFiles = 0, ttTrains = 0;
            foreach (var t in _timetablesAll) { if (t?.ORTTList == null) continue; ttFiles += t.ORTTList.Count; foreach (var f in t.ORTTList) ttTrains += f?.Trains?.Count ?? 0; }
            var chips = new List<string>();
            if (r != null)
            {
                if (stations > 0) chips.Add("🚉 " + Plural(stations, "{0} estación", "{0} estaciones"));
                chips.Add("🧭 " + Plural(_pathsAll.Count, "{0} recorrido", "{0} recorridos"));
                chips.Add("🏁 " + Plural(_activitiesAll.Count, "{0} actividad", "{0} actividades"));
                chips.Add("🕒 " + Plural(ttFiles, "{0} horario", "{0} horarios"));
                if (_curFolder != null) chips.Add("📁 " + _curFolder.Name);
            }
            _rutaHero.SetContent(r?.Name, r?.Description, chips);
            FitRutaHero();
            if (_rutaWelcome != null)
            {
                bool show = r == null || _rutaWelcomeHold;
                if (show)
                {
                    int favs = 0; foreach (var x in _routesAll) if (_prefs.Favorites.Contains(x.Path)) favs++;
                    var wc = new List<string>();
                    if (_routesAll.Count > 0) wc.Add("🗺️ " + Plural(_routesAll.Count, "{0} ruta", "{0} rutas"));
                    if (favs > 0) wc.Add("★ " + Plural(favs, "{0} favorita", "{0} favoritas"));
                    if (_consistsAll.Count > 0) wc.Add("🚆 " + Plural(_consistsAll.Count, "{0} tren", "{0} trenes"));
                    if (_curFolder != null) wc.Add("📁 " + _curFolder.Name);
                    _rutaWelcome.Chips = wc;
                    _rutaWelcome.Invalidate();
                }
                // Se asigna siempre: con la pestaña Ruta oculta, Visible devuelve false aunque la portada esté puesta
                // (si se elegía la ruta desde otra pestaña, la portada seguía al volver a Ruta).
                _rutaWelcome.Visible = show; if (show) _rutaWelcome.BringToFront();
            }
            _rutaGo.Items = new List<RouteGoPanel.Go>
            {
                new() { Icon = "🏁", Title = Tr("Actividades"), Sub = Tr("con briefing, paradas y horario"), Count = _activitiesAll.Count.ToString("N0", EsEs), Tint = Color.FromArgb(251, 146, 60), Page = 1 },
                new() { Icon = "🧭", Title = Tr("Exploración"), Sub = Tr("tu tren, tu recorrido, tu hora"), Count = _pathsAll.Count.ToString("N0", EsEs), Tint = Theme.AccentHi, Page = 2 },
                new() { Icon = "🕒", Title = Tr("Horarios"), Sub = Tr("trenes con su itinerario"), Count = ttTrains.ToString("N0", EsEs), Tint = Color.FromArgb(120, 144, 226), Page = 3 },
            };
            _rutaGo.Stats = new List<(string, string, string)>
            {
                (Tr("TRENES"), _consistsAll.Count.ToString("N0", EsEs), Tr("en tu contenido")),
                (Tr("ESTACIONES"), stations > 0 ? stations.ToString("N0", EsEs) : "—", stations > 0 ? Tr("en el mapa") : Tr("sin trazado")),
                (Tr("TU ÚLTIMA VEZ"), _routeStats.last, _routeStats.lastSub),
                (Tr("KM QUE LLEVAS"), _routeStats.km, _routeStats.kmSub),
            };
            _rutaGo.Invalidate();
        }

        // Tus servicios en esta ruta (si has iniciado sesión en Empresas): cuándo fue el último y cuántos km llevas.
        async void LoadRouteStats(Route r)
        {
            string login = Tr("inicia sesión en Empresas");
            _routeStats = Supa.IsLoggedIn ? ("…", "", "…", "") : ("—", login, "—", login);
            UpdateRouteOverview();
            if (!Supa.IsLoggedIn || r == null) return;
            var (json, err) = await Supa.SelectAsync(
                $"services?select=km,started_at,status,validated&driver_id=eq.{Uri.EscapeDataString(Supa.UserId ?? "")}&route=eq.{Uri.EscapeDataString(r.Name ?? "")}&order=started_at.desc&limit=1000");
            if (!ReferenceEquals(_curRoute, r)) return;
            int n = 0; double km = 0; DateTime last = DateTime.MinValue;
            if (err == null)
            {
                try
                {
                    using var d = System.Text.Json.JsonDocument.Parse(json);
                    foreach (var e in d.RootElement.EnumerateArray())
                    {
                        if (e.TryGetProperty("started_at", out var sa) && sa.ValueKind == System.Text.Json.JsonValueKind.String
                            && DateTime.TryParse(sa.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)
                            && t > last) last = t;
                        bool ok = e.TryGetProperty("validated", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True
                               && e.TryGetProperty("status", out var st) && st.GetString() == "completed";
                        if (!ok) continue;
                        n++;
                        if (e.TryGetProperty("km", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.Number) km += k.GetDouble();
                    }
                }
                catch { }
            }
            string lastTxt = "—", lastSub = Tr("aún no la has conducido");
            if (last != DateTime.MinValue)
            {
                var loc = DateTime.SpecifyKind(last, DateTimeKind.Utc).ToLocalTime();
                int days = (DateTime.Today - loc.Date).Days;
                lastTxt = days <= 0 ? Tr("hoy") : days == 1 ? Tr("ayer") : days < 30 ? string.Format(Tr("hace {0} días"), days) : loc.ToString("d MMM yyyy", I18n.English ? System.Globalization.CultureInfo.GetCultureInfo("en-GB") : EsEs);
                lastSub = Plural(n, "{0} servicio registrado", "{0} servicios registrados");
            }
            _routeStats = (lastTxt, lastSub, km.ToString("N0", EsEs) + " km", Tr("en esta ruta"));
            UpdateRouteOverview();
        }

        // ============================ ACTIVIDAD ============================
        // Filtros (todas · fácil · media · difícil · menos de 1 h) y búsqueda; las actividades en tarjetas y,
        // a la derecha, la ficha de la elegida.
        ActivityDetail _actDetail;
        FlowLayoutPanel _actChips;
        int _actFilter;
        readonly Dictionary<object, ActMeta> _actMeta = new();
        static readonly string[] ActFilterNames = { "Todas", "Fácil", "Media", "Difícil", "Menos de 1 h" };
        // «Media» ya se traduce como tipo de servicio (Regional): la dificultad lleva sus propias palabras.
        static string DiffWord(int i) => I18n.English ? new[] { "Easy", "Medium", "Hard" }[i] : new[] { "Fácil", "Media", "Difícil" }[i];
        static string ActFilterText(int k) => k >= 1 && k <= 3 ? DiffWord(k - 1) : Tr(ActFilterNames[k]);

        Panel BuildActividadPage()
        {
            var page = new Panel { BackColor = Theme.Bg };
            var bar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 44, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _actChips = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0) };
            for (int k = 0; k < ActFilterNames.Length; k++)
            {
                int idx = k;
                var b = BankChip(ActFilterText(k), k == 0);
                b.Click += (s, e) => { _actFilter = idx; SetChipActive(_actChips, idx); RefreshActivityList(); };
                _actChips.Controls.Add(b);
            }
            _actSearch = new RoundedInput(Tr("Buscar actividad…")) { Width = 240, Height = 34, Anchor = AnchorStyles.Right, Margin = new Padding(10, 3, 0, 3) };
            _actSearch.Box.TextChanged += (s, e) => RefreshActivityList();
            bar.Controls.Add(_actChips, 0, 0); bar.Controls.Add(_actSearch, 1, 0);

            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0) };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _lstActivities = new ActivityCardGrid
            {
                Dock = DockStyle.Fill, Margin = new Padding(0, 4, 12, 0), MetaOf = ActMetaOf,
                DiffNames = new[] { DiffWord(0).ToUpperInvariant(), DiffWord(1).ToUpperInvariant(), DiffWord(2).ToUpperInvariant() }, SeasonName = SeasonText, WeatherName = WeatherText
            };
            _lstActivities.SelectedIndexChanged += (s, e) => OnActivitySelected();
            _lstActivities.ItemActivated += o => Play();

            var card = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, Radius = 12, Padding = new Padding(2, 6, 2, 10), Margin = new Padding(0, 4, 0, 0) };
            _thumbs ??= new VehicleThumbs(this);
            _actDetail = new ActivityDetail { Dock = DockStyle.Fill, Thumbs = _thumbs, Caption = Tr("ACTIVIDAD ELEGIDA"), SummaryCap = Tr("RESUMEN"), Empty = Tr("Elige una actividad") };
            var btnMap = new RoundButton { Text = Tr("Ver el recorrido en el mapa"), GlyphKind = "map", Dock = DockStyle.Fill, Radius = 9, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 9.5f };
            btnMap.Click += (s, e) => OpenActivityMap();
            var btnHost = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Theme.Surface, Padding = new Padding(12, 6, 12, 2) };
            btnHost.Controls.Add(btnMap);
            card.Controls.Add(_actDetail); card.Controls.Add(btnHost);
            split.Controls.Add(_lstActivities, 0, 0); split.Controls.Add(card, 1, 0);

            _txtBriefing = MakeReadonlyText(); _txtBriefing.Visible = false;   // (lo siguen rellenando; no se enseña)
            page.Controls.Add(split); page.Controls.Add(bar); page.Controls.Add(_txtBriefing);
            return page;
        }

        string SeasonText(string k) => Tr(k switch { "spring" => "Primavera", "summer" => "Verano", "autumn" => "Otoño", "winter" => "Invierno", _ => k });
        string WeatherText(string k) => Tr(k switch { "clear" => "Despejado", "snow" => "Nieve", "rain" => "Lluvia", _ => k });

        // Datos de una actividad para su tarjeta (se leen una vez; por nombre de campo, vale para cualquier versión de OR).
        ActMeta ActMetaOf(object o)
        {
            if (o is not Activity a) return new ActMeta();
            if (_actMeta.TryGetValue(a, out var m)) return m;
            m = new ActMeta();
            static int? Num(object v, string n)
            {
                if (v == null) return null;
                var t = v.GetType();
                object x = t.GetField(n)?.GetValue(v) ?? t.GetProperty(n)?.GetValue(v);
                return x == null ? null : Convert.ToInt32(x);
            }
            try { int? h = Num(a.StartTime, "Hour"), mi = Num(a.StartTime, "Minute"); if (h != null && mi != null) m.Start = $"{h:00}:{mi:00}"; } catch { }
            try
            {
                int? h = Num(a.Duration, "Hour"), mi = Num(a.Duration, "Minute");
                if (h != null && mi != null && h + mi > 0) { m.DurMin = h.Value * 60 + mi.Value; m.Duration = h > 0 ? (mi > 0 ? $"{h} h {mi} min" : $"{h} h") : $"{mi} min"; }
            }
            catch { }
            try { string d = a.Difficulty.ToString(); m.Diff = d == "Easy" ? 0 : d == "Medium" ? 1 : d == "Hard" ? 2 : -1; } catch { }
            try { m.SeasonKind = a.Season.ToString().ToLowerInvariant(); } catch { }
            try { m.WeatherKind = a.Weather.ToString().ToLowerInvariant(); } catch { }
            try { m.Consist = a.Consist?.Name ?? ""; m.LocoPath = a.Consist?.Locomotive?.FilePath ?? ""; } catch { }
            // Open Rails llama «<missing: nombre>» al tren que no está en el contenido: se muestra el nombre y se avisa
            if ((m.Consist ?? "").StartsWith("<missing:", StringComparison.OrdinalIgnoreCase))
                m.Consist = m.Consist.Substring(9).TrimEnd('>').Trim() + " · " + Tr("no está en tu contenido");
            try { m.From = a.Path?.Start ?? ""; m.To = a.Path?.End ?? ""; m.PathFile = a.Path?.FilePath ?? ""; } catch { }
            _actMeta[a] = m;
            return m;
        }

        void OpenActivityMap()
        {
            var a = _lstActivities.SelectedItem as Activity;
            var m = ActMetaOf(a);
            if (a == null || string.IsNullOrEmpty(m.PathFile)) { Warn(Tr("Esta actividad no tiene un recorrido reconocible.")); return; }
            using (var dlg = new PathMapDialog(m.PathFile, _curRoute?.Path ?? "", m.From, m.To, overview: true)) dlg.ShowDialog(this);   // el recorrido entero
        }

        // ============================ EXPLORACIÓN ============================
        // Arriba «Tu viaje» (salida, llegada, mapa y modo actividad); luego hora, estación y clima en tarjetas;
        // debajo los trenes en tarjetas con filtros; a la derecha el tren elegido con su vista 3D y su ficha.
        Label _tripCap;
        FlowLayoutPanel _trainChips;
        SpecTiles _exSpecs;

        static Color TripFill => Color.FromArgb(40, 47, 43);

        Panel Field(string cap, Control ctl, Color bg, int right = 12)
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = bg, Margin = new Padding(0, 0, right, 0) };
            var l = new Label { Text = Tr(cap), Dock = DockStyle.Top, Height = 18, ForeColor = Theme.Subtle, BackColor = bg, Font = Theme.Font(7.75f, FontStyle.Bold) };
            ctl.Dock = DockStyle.Top;
            p.Controls.Add(ctl); p.Controls.Add(l);
            return p;
        }

        Card CondCard(string cap, Control body, out Panel inner)
        {
            var c = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, Radius = 12, Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0, 0, 10, 0) };
            inner = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
            var l = new Label { Text = Tr(cap), Dock = DockStyle.Top, Height = 20, ForeColor = Theme.Subtle, BackColor = Theme.Surface, Font = Theme.Font(7.75f, FontStyle.Bold) };
            body.Dock = DockStyle.Top;
            inner.Controls.Add(body);
            c.Controls.Add(inner); c.Controls.Add(l);
            return c;
        }

        int _trainKind;                       // 0 todos · 1 viajeros · 2 mercancías · 3 automotores · 4 locomotoras · 5 favoritos
        Label _classifyLbl;
        ConsistStripView _exStrip;
        static readonly string[] TrainKindNames = { "Todos", "Viajeros", "Mercancías", "Automotores", "Locomotoras", "★ Favoritos" };

        Panel BuildExploraPage()
        {
            var page = new Panel { BackColor = Theme.Bg };

            // ---- arriba: tu viaje y condiciones, en forma de billete (MainMenuForm.TuViaje.cs) ----
            var top = BuildTripHeader();

            // ---- elige tu tren: filtros por tipo ----
            var bar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 44, ColumnCount = 6, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var lblT = new Label { Text = Tr("ELIGE TU TREN"), AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.Subtle, Font = Theme.Font(8f, FontStyle.Bold), Margin = new Padding(0, 0, 12, 0) };
            _chkTrainFavOnly = MakeCheck("★  Solo favoritos"); _chkTrainFavOnly.Visible = false;
            _chkTrainFavOnly.CheckedChanged += (s, e) => RefreshConsistList();
            _trainChips = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0) };
            for (int k = 0; k < TrainKindNames.Length; k++)
            {
                int idx = k;
                var b = BankChip(TrainKindText(k), k == 0);
                b.Click += (s, e) =>
                {
                    _trainKind = idx; SetChipActive(_trainChips, idx);
                    _chkTrainFavOnly.Checked = idx == 5;   // (dispara RefreshConsistList)
                    RefreshConsistList();
                };
                _trainChips.Controls.Add(b);
            }
            _classifyLbl = new Label { AutoSize = true, Anchor = AnchorStyles.Right, ForeColor = Theme.Subtle, Font = Theme.Font(8.5f), Margin = new Padding(8, 0, 4, 0), Text = "" };
            var coHost = new Panel { Width = 250, Height = 34, BackColor = Theme.Bg, Anchor = AnchorStyles.Right, Margin = new Padding(8, 3, 0, 3) };
            var coLbl = new Label { Text = Tr("Empresa"), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(9f), Dock = DockStyle.Left, Padding = new Padding(0, 8, 8, 0) };
            _cboTrainCompany = new ThemeCombo { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            StyleCombo(_cboTrainCompany);
            _cboTrainCompany.Items.Add(Tr(AllCompaniesLabel)); _cboTrainCompany.SelectedIndex = 0;
            _cboTrainCompany.SelectedIndexChanged += (s, e) => { if (!_companyFilterLoading) RefreshConsistList(); };
            coHost.Controls.Add(_cboTrainCompany); coHost.Controls.Add(coLbl);
            _trainCompanyHost = coHost; _trainCompanyHost.Visible = false;
            _consistSearch = new RoundedInput(Tr("Buscar tren…")) { Width = 220, Height = 34, Anchor = AnchorStyles.Right, Margin = new Padding(8, 3, 0, 3) };
            // Con miles de trenes, se filtra cuando dejas de escribir (no a cada letra).
            var searchWait = new Timer { Interval = 220 };
            searchWait.Tick += (s, e) => { searchWait.Stop(); RefreshConsistList(); };
            _consistSearch.Box.TextChanged += (s, e) => { searchWait.Stop(); searchWait.Start(); };
            // tarjetas o lista (se recuerda)
            _trainView = new ViewModeToggle { Anchor = AnchorStyles.Right, Margin = new Padding(8, 5, 0, 5), Mode = _prefs.TrainListView ? 1 : 0, TipCards = Tr("Ver en tarjetas"), TipList = Tr("Ver en lista") };
            _trainView.ModeChanged += (s, e) => { _prefs.TrainListView = _trainView.Mode == 1; _lstConsists.ListMode = _prefs.TrainListView; };
            bar.Controls.Add(lblT, 0, 0); bar.Controls.Add(_trainChips, 1, 0); bar.Controls.Add(_classifyLbl, 2, 0); bar.Controls.Add(coHost, 3, 0); bar.Controls.Add(_trainView, 4, 0); bar.Controls.Add(_consistSearch, 5, 0);

            _thumbs ??= new VehicleThumbs(this);
            _lstConsists = new TrainCardGrid
            {
                Dock = DockStyle.Fill, Thumbs = _thumbs, MultiSelect = true,   // Ctrl+clic: varias a la vez (eliminar)
                PathOf = o => (o as TrainItem)?.Locomotive?.FilePath, KeyOf = o => (o as TrainItem)?.FilePath,
                SpecOf = o => TrainSpecAsync(o as TrainItem),
                FavOf = o => o is TrainItem t && _prefs.FavoriteTrains.Contains(t.FilePath),
                CompaniesOf = o => ConsistCompanies(o as TrainItem),
                LblPax = Tr("VIAJEROS"), LblFreight = Tr("MERCANCÍAS"), LblCars = Tr("{0} coches"), LblSeats = Tr("plazas"), LblLoading = Tr("Calculando…"),
                ListMode = _prefs.TrainListView,
            };
            _lstConsists.SelectedIndexChanged += (s, e) => OnConsistSelected();
            _lstConsists.ItemActivated += o => ToggleTrainFavorite();
            var consistCtx = MenuStyle.Apply(new ContextMenuStrip());
            var miTFav = new ToolStripMenuItem(Tr("Añadir / quitar de favoritos")); miTFav.Click += (s, e) => ToggleTrainFavorite();
            consistCtx.Items.Add(miTFav);
            // El tren en el Editor de composiciones, o a la papelera, sin salir de Exploración.
            var miTEdit = new ToolStripMenuItem(Tr("Editar composición")); miTEdit.Click += (s, e) => EditConsistFromExplore(_lstConsists.SelectedItem as TrainItem);
            var miTDel = new ToolStripMenuItem(Tr("Eliminar composición…")) { ForeColor = Color.FromArgb(229, 115, 115) };
            miTDel.Click += (s, e) => DeleteConsistsFromExplore(_lstConsists.SelectedItems.OfType<TrainItem>().ToList());
            var sepT = new ToolStripSeparator();
            consistCtx.Items.Add(sepT);
            consistCtx.Items.Add(miTEdit);
            consistCtx.Items.Add(miTDel);
            consistCtx.Opening += (s, e) =>
            {
                int n = _lstConsists.SelectedItems.OfType<TrainItem>().Count(t => File.Exists(t.FilePath));
                // Con varias marcadas (Ctrl+clic), solo se puede eliminarlas: lo demás no se enseña.
                bool many = _lstConsists.MarkedCount > 1;
                miTFav.Visible = sepT.Visible = miTEdit.Visible = !many;
                miTEdit.Enabled = n == 1;
                miTDel.Enabled = n > 0;
                miTDel.Text = n > 1 ? string.Format(Tr("Eliminar {0} composiciones…"), n) : Tr("Eliminar composición…");
                MenuStyle.Measure(consistCtx);   // con las opciones ya puestas: márgenes iguales a los dos lados
            };
            _lstConsists.ContextMenuStrip = consistCtx;
            var trainsHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 4, 0, 0) };
            trainsHost.Controls.Add(_lstConsists);
            _consistsOverlay = new ListStatePanel { Bg = Theme.Bg, Icon = "🚆" };
            trainsHost.Controls.Add(_consistsOverlay);
            void syncConsistsOverlay() { if (_consistsOverlay != null) { _consistsOverlay.Bounds = _lstConsists.Bounds; if (_consistsOverlay.Visible) _consistsOverlay.BringToFront(); } }
            _lstConsists.SizeChanged += (s, e) => syncConsistsOverlay();
            _lstConsists.LocationChanged += (s, e) => syncConsistsOverlay();
            trainsHost.SizeChanged += (s, e) => syncConsistsOverlay();
            syncConsistsOverlay();

            // ---- abajo: el tren elegido (vista 3D · composición 2D · ficha) ----
            var sel = new Card { Dock = DockStyle.Bottom, Height = 250, Fill = Theme.Surface, Radius = 12, Padding = new Padding(10, 8, 14, 10), Margin = new Padding(0) };
            var selGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface, Margin = new Padding(0) };
            selGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
            selGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            selGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _trainPreview = new TrainPreviewPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 14, 0), BaseDistance = 3.1f };
            _trainPreview.ViewerSource = () => _previewGeom == null ? null : new ShapeViewSource
                { Geom = _previewGeom, Flip = _previewFlip, BaseDistance = _trainPreview.BaseDistance, Yaw = _yaw, Pitch = _pitch, Caption = _trainPreview.Caption, BeforeRender = () => TeleRedirect(_teleExplore) };
            _trainPreview.Dragged += OnPreviewDrag;
            _trainPreview.ResetRequested += OnPreviewReset;
            _trainPreview.Zoomed += () => { if (_previewGeom != null) RenderLive(); };
            _previewRerender = new Timer { Interval = 140 };
            _previewRerender.Tick += (s, e) => { _previewRerender.Stop(); RenderLive(); };
            _trainPreview.Resize += (s, e) => { if (_previewGeom != null) { _previewRerender.Stop(); _previewRerender.Start(); } };
            var info = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = new Padding(0) };
            var lblP = MakeSectionLabel("TREN ELEGIDO"); lblP.Dock = DockStyle.Top;
            lblP.Padding = new Padding(2, 0, 0, 0); lblP.TextAlign = ContentAlignment.MiddleLeft;   // centrado en la cabecera, como el teleindicador
            var (hdrP, stChip) = MakeTrainHeader(lblP); _lblStatusExplore = stChip;   // título + estado en la flota
            _teleExplore = MakeTeleUi(hdrP, () => RenderLive());                       // teleindicador (si el tren tiene destinos)
            _buyWrapExplore = MakeBuyTrainWrap(() => _lstConsists?.SelectedItem as TrainItem, 32);   // oculto salvo gerente/gestor/superadmin
            var headRow = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Surface };
            hdrP.Dock = DockStyle.Fill;
            _buyWrapExplore.Padding = new Padding(8, 3, 0, 5);
            headRow.Controls.Add(hdrP); headRow.Controls.Add(_buyWrapExplore);
            _exStrip = new ConsistStripView { Dock = DockStyle.Top, Height = 104, Hint = Tr("clic: ver la composición completa"), Placeholder = Tr("Elige un tren") };
            _exStrip.Click += (s, e) => OpenComposition();
            _exSpecs = new SpecTiles { Dock = DockStyle.Top, Height = 0, Columns = 6 };
            info.Controls.Add(_exSpecs);
            info.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Surface });
            info.Controls.Add(_exStrip);
            info.Controls.Add(headRow);
            selGrid.Controls.Add(_trainPreview, 0, 0); selGrid.Controls.Add(info, 1, 0);
            sel.Controls.Add(selGrid);

            page.Controls.Add(trainsHost);
            page.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 10, BackColor = Theme.Bg });
            page.Controls.Add(sel);
            page.Controls.Add(bar);
            page.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Theme.Bg });
            page.Controls.Add(top);
            page.Controls.Add(_chkTrainFavOnly);
            return page;
        }

        string TrainKindText(int k) => k == 5 ? "★ " + Tr("Favoritos") : Tr(TrainKindNames[k]);

        // ---- tipo de cada tren (para los filtros): se calcula en segundo plano y se guarda en disco ----
        System.Threading.CancellationTokenSource _classifyCts;
        int _classDone, _classTotal, _classShown = -1;
        Timer _classifyTimer;
        static string ClassCacheFile => SysPath.Combine(AppDataTidy.CacheRoot, "trenes_tipo.txt");
        static readonly Dictionary<string, string> _classDisk = new(StringComparer.OrdinalIgnoreCase);
        static bool _classDiskLoaded;

        static string ClassKey(string conPath) { try { return conPath.ToLowerInvariant() + "|" + File.GetLastWriteTimeUtc(conPath).Ticks; } catch { return conPath.ToLowerInvariant(); } }
        static string SpecToLine(TrainSpec s) => string.Join("\t", s.Freight ? "1" : "0", s.Automotor ? "1" : "0", s.Kw.ToString(System.Globalization.CultureInfo.InvariantCulture),
            s.Kmh.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Capacity.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Cars, s.Engines, (s.Traction ?? "").Replace("\t", " "), (s.Service ?? "").Replace("\t", " "),
            s.MassT.ToString(System.Globalization.CultureInfo.InvariantCulture), s.LengthM.ToString(System.Globalization.CultureInfo.InvariantCulture), ClassFormat);
        // Formato de la línea: «t2» desde la 1.2.50 (la tracción sale del Type del .eng). Las líneas de antes se
        // recalculan una vez: guardaban la tracción del método antiguo.
        const string ClassFormat = "t2";
        static TrainSpec SpecFromLine(string l)
        {
            var p = l.Split('\t'); if (p.Length < 12 || p[11] != ClassFormat) return null;   // líneas antiguas: se recalculan
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return new TrainSpec { Freight = p[0] == "1", Automotor = p[1] == "1", Kw = double.Parse(p[2], ci), Kmh = double.Parse(p[3], ci), Capacity = double.Parse(p[4], ci),
                                   Cars = int.Parse(p[5]), Engines = int.Parse(p[6]), Traction = TractionLocal(p[7]), Service = p[8],
                                   MassT = double.Parse(p[9], ci), LengthM = double.Parse(p[10], ci), Known = true };
        }
        // La línea guarda la tracción en el idioma con el que se calculó: se pasa al idioma actual.
        static string TractionLocal(string s)
        {
            switch (s)
            {
                case "Eléctrica": case "Electric": return TractionName("electric");
                case "Diésel": case "Diesel": return TractionName("diesel");
                case "Vapor": case "Steam": return TractionName("steam");
                default: return s;
            }
        }

        TrainSpec KnownSpec(TrainItem c)
        {
            if (c?.FilePath == null) return null;
            lock (_trainSpecs) return _trainSpecs.TryGetValue(c.FilePath, out var t) && t.IsCompletedSuccessfully ? t.Result : null;
        }

        void StartTrainClassification(List<TrainItem> list)
        {
            _classifyCts?.Cancel();
            var cts = new System.Threading.CancellationTokenSource(); _classifyCts = cts;
            _classTotal = list.Count; _classDone = 0; _classShown = -1;
            if (_classifyTimer == null)
            {
                _classifyTimer = new Timer { Interval = 600 };
                _classifyTimer.Tick += (s, e) => ClassifyTick();
            }
            _classifyTimer.Start();
            var items = new List<TrainItem>(list);
            Task.Run(() =>
            {
                lock (_classDisk)
                {
                    if (!_classDiskLoaded)
                    {
                        _classDiskLoaded = true;
                        try { if (File.Exists(ClassCacheFile)) foreach (var l in File.ReadAllLines(ClassCacheFile)) { int t = l.IndexOf('\t'); if (t > 0) _classDisk[l.Substring(0, t)] = l.Substring(t + 1); } } catch { }
                    }
                }
                var todo = new List<(TrainItem c, string key)>();
                foreach (var c in items)
                {
                    if (cts.IsCancellationRequested) return;
                    if (c?.FilePath == null) { System.Threading.Interlocked.Increment(ref _classDone); continue; }
                    string key = ClassKey(c.FilePath);
                    TrainSpec sp = null;
                    lock (_classDisk) if (_classDisk.TryGetValue(key, out var line)) { try { sp = SpecFromLine(line); } catch { } }
                    if (sp != null) { lock (_trainSpecs) _trainSpecs[c.FilePath] = Task.FromResult(sp); System.Threading.Interlocked.Increment(ref _classDone); }
                    else if (KnownSpec(c) != null) System.Threading.Interlocked.Increment(ref _classDone);
                    else todo.Add((c, key));
                }
                int sinceSave = 0;
                var worker = new System.Threading.Thread(() =>
                {
                    foreach (var x in todo)
                    {
                        if (cts.IsCancellationRequested) break;
                        TrainSpec sp;
                        Task<TrainSpec> pending = null;
                        lock (_trainSpecs) _trainSpecs.TryGetValue(x.c.FilePath, out pending);
                        if (pending != null) { try { sp = pending.Result ?? ComputeTrainSpec(x.c); } catch { sp = ComputeTrainSpec(x.c); } }   // ya pedido por una tarjeta
                        else { sp = ComputeTrainSpec(x.c); lock (_trainSpecs) _trainSpecs[x.c.FilePath] = Task.FromResult(sp); }
                        if (sp.Known) lock (_classDisk) { _classDisk[x.key] = SpecToLine(sp); sinceSave++; }
                        System.Threading.Interlocked.Increment(ref _classDone);
                        if (sinceSave >= 500) { SaveClassDisk(); sinceSave = 0; }   // por si se cierra a medias
                    }
                    if (sinceSave > 0) SaveClassDisk();
                }) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "Clasificar trenes" };
                worker.Start();
            });
        }

        static void SaveClassDisk()
        {
            try
            {
                List<string> lines; lock (_classDisk) { lines = new List<string>(_classDisk.Count); foreach (var kv in _classDisk) lines.Add(kv.Key + "\t" + kv.Value); }
                Directory.CreateDirectory(SysPath.GetDirectoryName(ClassCacheFile));
                File.WriteAllLines(ClassCacheFile + ".tmp", lines);
                File.Move(ClassCacheFile + ".tmp", ClassCacheFile, true);
            }
            catch { }
        }

        void ClassifyTick()
        {
            int done = _classDone, total = _classTotal;
            bool finished = done >= total;
            if (_classifyLbl != null)
            {
                string t = finished || total == 0 ? "" : string.Format(Tr("Clasificando trenes… {0} %"), done * 100 / Math.Max(1, total));
                if (_classifyLbl.Text != t) _classifyLbl.Text = t;
            }
            if (done != _classShown) { _classShown = done; RefreshConsistList(); }
            if (finished) _classifyTimer?.Stop();
        }

        void UpdateTripMeta()
        {
            if (_tripCap != null) _tripCap.Text = Tr("TU VIAJE") + (_curRoute != null ? "  ·  " + _curRoute.Name.ToUpper() : "");
            UpdateTripLine();
        }

        // Datos de un tren para su tarjeta y su ficha (los mismos cálculos que Compra; en segundo plano).
        readonly Dictionary<string, Task<TrainSpec>> _trainSpecs = new(StringComparer.OrdinalIgnoreCase);
        Task<TrainSpec> TrainSpecAsync(TrainItem c)
        {
            if (c?.FilePath == null) return Task.FromResult<TrainSpec>(null);
            lock (_trainSpecs)
            {
                if (_trainSpecs.TryGetValue(c.FilePath, out var t)) return t;
                t = Task.Run(() => ComputeTrainSpec(c));
                _trainSpecs[c.FilePath] = t;
                return t;
            }
        }

        TrainSpec ComputeTrainSpec(TrainItem c)
        {
            var sp = new TrainSpec();
            try
            {
                string eng = c.Locomotive?.FilePath;
                if (!string.IsNullOrEmpty(eng)) { var (kw, kmh, type) = ReadEngineSpecs(eng); sp.Kw = kw; sp.Kmh = kmh; sp.Traction = string.IsNullOrEmpty(type) ? "" : TractionName(type); }
                var an = AnalyzeComposition(c, sp.Kmh);
                sp.Capacity = an.Capacity; sp.Freight = an.Freight && !an.DeclaredPax; sp.Service = an.ServiceType ?? "";
                var (e, tot) = ConsistCounts(c.FilePath); sp.Engines = e; sp.Cars = tot;
                foreach (var r in ConsistCarRefs(c.FilePath))   // masa y longitud: suma de todos los vehículos
                {
                    var v = Veh(ResolveCarFile(r.name, r.folder));
                    if (v == null) continue;
                    if (v.MassT > 0) sp.MassT += v.MassT;
                    if (v.Length > 0) sp.LengthM += v.Length;
                }
                // Automotor: formación fija conocida, cabeza con plazas o todo motrices; un mercancías nunca lo es.
                bool auto = false;
                if (!sp.Freight && !string.IsNullOrEmpty(eng))
                {
                    try
                    {
                        var unit = UnitOfPath(eng);
                        if (unit != null && (unit.Members.Count > 1 || unit.Cars > 1) && string.Equals(unit.Head, SysPath.GetFileNameWithoutExtension(eng), StringComparison.OrdinalIgnoreCase)) auto = true;
                    }
                    catch { }
                    try { if (!auto && (Veh(eng)?.Capacity ?? 0) > 0) auto = true; } catch { }
                    if (!auto && e >= 2 && e == tot) auto = true;
                }
                sp.Automotor = auto;
                sp.Known = true;
            }
            catch { }
            return sp;
        }

        async void UpdateExploreSpecs(TrainItem c)
        {
            if (_exSpecs == null) return;
            ShowExploreStrip(c);
            if (c == null) { _exSpecs.SetItems(null); return; }
            var sp = await TrainSpecAsync(c);
            if (!ReferenceEquals(_lstConsists.SelectedItem, c) || sp == null) return;
            var es = EsEs;
            var items = new List<(string, string)>
            {
                (Tr("TRACCIÓN"), sp.Traction.Length > 0 ? sp.Traction : "—"),
                (Tr("MASA"), sp.MassT > 0 ? sp.MassT.ToString("N0", es) + " t" : "—"),
                (Tr("VEL. MÁXIMA"), sp.Kmh > 0 ? sp.Kmh.ToString("N0", es) + " km/h" : "—"),
                (Tr("SERVICIO"), sp.Service.Length > 0 ? Tr(sp.Service) : (sp.Freight ? Tr("Mercancías") : "—")),
                (Tr("TIPO"), sp.Freight ? Tr("Mercancías") : (sp.Automotor ? Tr("Automotor") : Tr("Locomotora")) + (sp.Service.Length > 0 ? "  ·  " + Tr(sp.Service) : "")),
                (sp.Freight ? Tr("VEHÍCULOS") : Tr("PLAZAS"), sp.Freight ? (sp.Cars > 0 ? sp.Cars.ToString("N0", es) : "—") : (sp.Capacity > 0 ? sp.Capacity.ToString("N0", es) : "—")),
                (Tr("LONGITUD"), sp.LengthM > 0 ? sp.LengthM.ToString("N0", es) + " m" : "—"),
            };
            items.RemoveAt(3);   // el servicio ya va dentro de «TIPO»: seis casillas en una fila
            _exSpecs.SetItems(items);
            if (_exStrip != null && sp.Cars > 0) { _exStrip.Caption = Tr("COMPOSICIÓN 2D") + "  ·  " + Plural(sp.Cars, "{0} vehículo", "{0} vehículos"); _exStrip.Invalidate(); }
        }

        // ---- composición 2D del tren elegido (todos sus vehículos), con caché en disco ----
        readonly Dictionary<string, Bitmap> _stripMem = new(StringComparer.OrdinalIgnoreCase);
        static string StripCacheFile(string conPath)
        {
            string k = ClassKey(conPath);
            using var sha = System.Security.Cryptography.SHA1.Create();
            var h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(k));
            // «composiciones-acopladas»: desde la 1.2.49 los vehículos se dibujan acoplados (topes con topes); las de la
            // carpeta antigua («composiciones», con huecos) las borra AppDataTidy.
            return SysPath.Combine(AppDataTidy.CacheRoot, "composiciones-acopladas", BitConverter.ToString(h, 0, 10).Replace("-", "").ToLowerInvariant() + ".png");
        }

        void ShowExploreStrip(TrainItem c)
        {
            if (_exStrip == null) return;
            string cap = Tr("COMPOSICIÓN 2D");
            if (c?.FilePath == null) { _exStrip.Set(null, cap, false); return; }
            if (_stripMem.TryGetValue(c.FilePath, out var bmp)) { _exStrip.Set(bmp, cap, false); return; }
            _exStrip.Set(null, cap, true);
            string con = c.FilePath, file = StripCacheFile(con);
            Task.Run(async () =>
            {
                Bitmap fromDisk = null;
                try { if (File.Exists(file)) using (var ms = new MemoryStream(File.ReadAllBytes(file))) fromDisk = new Bitmap(Image.FromStream(ms)); } catch { }
                if (fromDisk != null) { try { BeginInvoke((Action)(() => StripReady(c, fromDisk))); } catch { } return; }
                List<(string path, bool flip, string name)> models;
                try
                {
                    var doc = ConsistDoc.Load(con);
                    if (doc == null || doc.Cars.Count == 0) { try { BeginInvoke((Action)(() => StripReady(c, null))); } catch { } return; }
                    models = doc.Cars.Select(x => (ResolveCarFile(x.Name, x.Folder), x.Flip, x.Name)).ToList();
                }
                catch { try { BeginInvoke((Action)(() => StripReady(c, null))); } catch { } return; }
                var cars = new List<(ShapeGeom geom, bool flip, string name)>();
                foreach (var (path, flip, name) in models)
                {
                    ShapeGeom g = null;
                    if (path != null)
                    {
                        lock (_geomCache) _geomCache.TryGetValue(path, out g);
                        if (g == null)
                        {
                            try { g = ShapeRenderer.BuildGeometry(path); } catch { }
                            try { ShapeRenderer.PrefetchTextures(g); } catch { }
                            if (g != null) lock (_geomCache) _geomCache[path] = g;
                        }
                    }
                    cars.Add((g, flip, name));
                }
                try
                {
                    // un modelo por turno de la interfaz: la ventana no se congela mientras se dibuja el tren
                    Bitmap strip = null;
                    try { strip = await ServiceImages.ComposeStripAsync(cars, this); } catch { }
                    if (strip != null)
                    {
                        try { Directory.CreateDirectory(SysPath.GetDirectoryName(file)); strip.Save(file + ".tmp", System.Drawing.Imaging.ImageFormat.Png); File.Move(file + ".tmp", file, true); } catch { }
                    }
                    BeginInvoke((Action)(() => StripReady(c, strip)));
                }
                catch { }
            });
        }

        void StripReady(TrainItem c, Bitmap bmp)
        {
            if (bmp != null)
            {
                if (_stripMem.Count > 40) { foreach (var b in _stripMem.Values) if (!ReferenceEquals(b, _exStrip?.Image)) b.Dispose(); _stripMem.Clear(); }
                _stripMem[c.FilePath] = bmp;
            }
            if (_exStrip != null && ReferenceEquals(_lstConsists?.SelectedItem, c)) _exStrip.Set(bmp, _exStrip.Caption, false);
        }

        // ============================ HORARIOS ============================
        // Arriba los conjuntos (y, si los hay, sus horarios) como pastillas; el panel de salidas a la izquierda y,
        // a la derecha, el tren elegido: vista 3D, su itinerario con paradas y horas, estación y clima.
        TimetableList _ttList;
        Label _ttListCap;
        RoundedInput _ttListSearch;
        string _ttEntriesKey;
        bool _ttListSyncing;
        DepartureBoard _ttBoard;
        ItineraryView _ttItin;
        RoundedInput _ttSearch;
        bool _ttSyncing;
        readonly Dictionary<object, List<TtStops.Stop>> _ttStops = new();

        Panel BuildHorariosPage()
        {
            var page = new Panel { BackColor = Theme.Bg };
            // Los desplegables de siempre siguen siendo los que mandan (otras partes los consultan), pero no se ven.
            var hidden = new Panel { Visible = false };
            _cboTTSet = NewCombo(); _cboTTSet.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboTTSet.FormattingEnabled = true; _cboTTSet.Format += (s, e) => { if (e.ListItem is TimetableInfo t) e.Value = TtLabel(t.Description, t.ToString()); };
            _cboTTSet.SelectedIndexChanged += (s, e) => { OnTimetableSetChanged(); BuildTTChips(); };
            _cboTT = NewCombo();
            _cboTT.FormattingEnabled = true; _cboTT.Format += (s, e) => { if (e.ListItem is Orts.Formats.OR.TimetableFileLite t) e.Value = TtLabel(t.Description, t.ToString()); };
            _cboTT.SelectedIndexChanged += (s, e) => { OnTimetableChanged(); BuildTTChips(); };
            _cboTTTrain = NewCombo(); _cboTTTrain.SelectedIndexChanged += (s, e) => { UpdateStatus(); UpdateTimetableBriefing(); UpdateTimetablePreview(); SyncBoardFromCombo(); UpdateItinerary(); };
            hidden.Controls.Add(_cboTT); hidden.Controls.Add(_cboTTTrain);
            _txtTTBriefing = MakeReadonlyText(); hidden.Controls.Add(_txtTTBriefing);
            page.Controls.Add(hidden);

            // ---- columna: primero el CONJUNTO (desplegable) y debajo sus HORARIOS (lista, con buscador) ----
            var ttCol = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, Radius = 12, Padding = new Padding(10, 8, 8, 10), Margin = new Padding(0, 0, 14, 0) };
            var setCap = new Label { Text = Tr("CONJUNTO"), Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Subtle, BackColor = Theme.Surface, Font = Theme.Font(8f, FontStyle.Bold) };
            _cboTTSet.Dock = DockStyle.Top;
            _ttListCap = new Label { Text = Tr("HORARIO"), Dock = DockStyle.Top, Height = 30, ForeColor = Theme.Subtle, BackColor = Theme.Surface, Font = Theme.Font(8f, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(0, 0, 0, 4) };
            _ttListSearch = new RoundedInput(Tr("Buscar horario…")) { Dock = DockStyle.Top, Height = 34 };
            _ttListSearch.Box.TextChanged += (s, e) => { _ttEntriesKey = null; BuildTTChips(); };
            _ttList = new TimetableList { Dock = DockStyle.Fill, Margin = new Padding(0) };
            _ttList.SelectedIndexChanged += (s, e) => OnTTEntryChosen();
            ttCol.Controls.Add(_ttList);
            ttCol.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Surface });
            ttCol.Controls.Add(_ttListSearch);
            ttCol.Controls.Add(_ttListCap);
            ttCol.Controls.Add(_cboTTSet);
            ttCol.Controls.Add(setCap);

            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0) };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // ---- izquierda: buscador, empresa y panel de salidas ----
            var left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(0, 0, 14, 0) };
            var bar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 44, ColumnCount = 3, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var lblH = new Label { Text = Tr("SALIDAS DEL HORARIO"), AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.Subtle, Font = Theme.Font(8f, FontStyle.Bold) };
            _cboTTCompany = NewCombo(); _cboTTCompany.DropDownStyle = ComboBoxStyle.DropDownList; _cboTTCompany.Dock = DockStyle.Fill;
            _cboTTCompany.Items.Add(Tr(AllCompaniesLabel)); _cboTTCompany.SelectedIndex = 0;
            _cboTTCompany.SelectedIndexChanged += (s, e) => { if (!_companyFilterLoading) OnTimetableChanged(); };
            var coHost = new Panel { Width = 250, Height = 34, BackColor = Theme.Bg, Anchor = AnchorStyles.Right, Margin = new Padding(8, 3, 0, 3) };
            _ttCompanyLbl = new Label { Text = Tr("Empresa"), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(9f), Dock = DockStyle.Left, Padding = new Padding(0, 8, 8, 0) };
            coHost.Controls.Add(_cboTTCompany); coHost.Controls.Add(_ttCompanyLbl);
            _ttCompanyHost = coHost; _ttGrid = null; _ttCompanyRow = -1;
            _ttSearch = new RoundedInput(Tr("Tren o estación…")) { Width = 220, Height = 34, Anchor = AnchorStyles.Right, Margin = new Padding(8, 3, 0, 3) };
            _ttSearch.Box.TextChanged += (s, e) => FillBoard();
            bar.Controls.Add(lblH, 0, 0); bar.Controls.Add(coHost, 1, 0); bar.Controls.Add(_ttSearch, 2, 0);
            SetTTCompanyVisible(false);   // oculto hasta que haya empresas con trenes

            _ttBoard = new DepartureBoard
            {
                Dock = DockStyle.Fill, Hint = Tr("elige un tren para conducirlo"),
                Heads = new[] { Tr("HORA"), Tr("TREN"), Tr("RECORRIDO"), Tr("PARADAS"), Tr("COMPOSICIÓN") },
                LblStops = Tr("{0} paradas"), LblStop = Tr("1 parada"), LblNoStops = Tr("sin paradas"),
            };
            _ttBoard.SelectedIndexChanged += (s, e) =>
            {
                if (_ttSyncing || _ttBoard.SelectedItem is not DepartureBoard.Row r) return;
                _ttSyncing = true;
                try { if (!ReferenceEquals(_cboTTTrain.SelectedItem, r.Train)) _cboTTTrain.SelectedItem = r.Train; }
                finally { _ttSyncing = false; }
            };
            _ttBoard.ItemActivated += o => Play();
            var boardCard = new Card { Dock = DockStyle.Fill, Fill = Color.FromArgb(11, 12, 13), Radius = 12, Padding = new Padding(2, 4, 2, 4), Margin = new Padding(0, 4, 0, 0) };
            boardCard.Controls.Add(_ttBoard);
            left.Controls.Add(boardCard); left.Controls.Add(bar);

            // ---- derecha: tren elegido ----
            var right = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, Radius = 12, Padding = new Padding(12, 8, 12, 12), Margin = new Padding(0) };
            var lblP = MakeSectionLabel("TREN ELEGIDO"); lblP.Dock = DockStyle.Top;
            lblP.Padding = new Padding(2, 0, 0, 0); lblP.TextAlign = ContentAlignment.MiddleLeft;   // centrado en la cabecera, como el teleindicador
            _ttPreview = new TrainPreviewPanel { Dock = DockStyle.Top, Height = 170, BaseDistance = 3.1f };
            _ttPreview.ViewerSource = () => _ttPreviewGeom == null ? null : new ShapeViewSource
                { Geom = _ttPreviewGeom, Flip = _ttPreviewFlip, BaseDistance = _ttPreview.BaseDistance, Yaw = _ttYaw, Pitch = _ttPitch, Caption = _ttPreview.Caption, BeforeRender = () => TeleRedirect(_teleTT) };
            _ttPreview.Dragged += OnTTPreviewDrag;
            _ttPreview.ResetRequested += OnTTPreviewReset;
            _ttPreview.Zoomed += () => { if (_ttPreviewGeom != null) RenderTTLive(); };
            _ttRerender = new Timer { Interval = 140 };
            _ttRerender.Tick += (s, e) => { _ttRerender.Stop(); RenderTTLive(); };
            _ttPreview.Resize += (s, e) => { if (_ttPreviewGeom != null) { _ttRerender.Stop(); _ttRerender.Start(); } };
            _ttItin = new ItineraryView { Dock = DockStyle.Fill, Empty = Tr("Elige un tren del panel de salidas"), LblOrigin = Tr("origen"), LblDest = Tr("destino"),
                                         LblNoStops = Tr("Este horario no detalla las paradas de este tren."), BriefCap = Tr("RESUMEN") };
            // estación y clima: los mismos chips que Exploración (un clic abre sus tarjetas en un desplegable)
            _segTTSeason = new Segmented(SeasonKinds, SeasonNames) { SelectedIndex = 1, CardStyle = true, Height = 58 };
            _segTTWeather = new Segmented(WeatherKinds, WeatherNames) { SelectedIndex = 0, CardStyle = true, Height = 58 };
            var conds = BuildTTCondChips();
            // «Composición 2D» y «Comprar este tren» a partes iguales (o la primera sola si no se puede comprar).
            var btnCompTT = new RoundButton { Text = Tr("Composición 2D"), GlyphKind = "train", Dock = DockStyle.Fill, Height = 34, Radius = 9, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 9.5f, Margin = new Padding(0, 0, 4, 0) };
            btnCompTT.Click += (s, e) => OpenTTComposition();
            var compRowTT = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 42, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface, Padding = new Padding(0, 8, 0, 0), Margin = new Padding(0) };
            compRowTT.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); compRowTT.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            compRowTT.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _buyWrapTT = MakeBuyTrainWrap(CurrentTTConsist, 34);
            _buyWrapTT.Dock = DockStyle.Fill; _buyWrapTT.Margin = new Padding(4, 0, 0, 0); _buyWrapTT.Padding = new Padding(0);
            compRowTT.Controls.Add(btnCompTT, 0, 0); compRowTT.Controls.Add(_buyWrapTT, 1, 0);
            void fitCompRow()
            {
                bool buy = _buyWrapTT.Visible;
                compRowTT.ColumnStyles[0] = new ColumnStyle(SizeType.Percent, buy ? 50 : 100);
                compRowTT.ColumnStyles[1] = buy ? new ColumnStyle(SizeType.Percent, 50) : new ColumnStyle(SizeType.Absolute, 0);
                btnCompTT.Margin = new Padding(0, 0, buy ? 4 : 0, 0);
            }
            _buyWrapTT.VisibleChanged += (s, e) => fitCompRow();
            fitCompRow();
            var (hdrTT, stChipTT) = MakeTrainHeader(lblP); _lblStatusTT = stChipTT;   // título + estado en la flota
            // teleindicador (si el tren tiene destinos): en su propia fila bajo el título (junto a él no cabe)
            var teleRowTT = new Panel { Dock = DockStyle.Top, Height = Theme.Px(40), BackColor = hdrTT.BackColor, Visible = false, Margin = new Padding(0) };
            _teleTT = MakeTeleUi(teleRowTT, () => RenderTTLive(), ownRow: true);
            right.Controls.Add(_ttItin);
            right.Controls.Add(_ttPreview);
            right.Controls.Add(conds);
            right.Controls.Add(compRowTT);
            right.Controls.Add(teleRowTT);
            right.Controls.Add(hdrTT);

            split.Controls.Add(ttCol, 0, 0);
            split.Controls.Add(left, 1, 0);
            split.Controls.Add(right, 2, 0);
            page.Controls.Add(split);
            return page;
        }

        // Lista de HORARIOS del conjunto elegido (con el nº de trenes de cada uno) y buscador.
        void BuildTTChips()
        {
            if (_ttList == null) return;
            string q = _ttListSearch?.Box.Text.Trim() ?? "";
            var set = _cboTTSet.SelectedItem as TimetableInfo;
            string key = _cboTTSet.SelectedIndex + "|" + q + "|" + (set?.ORTTList?.Count ?? 0) + "|" + _cboTTSet.Items.Count;
            if (key != _ttEntriesKey)
            {
                _ttEntriesKey = key;
                var entries = new List<TimetableList.Entry>();
                int files = set?.ORTTList?.Count ?? 0;
                string setName = set == null ? "" : TtLabel(set.Description, set.ToString());
                for (int fi = 0; fi < files; fi++)
                {
                    var f = set.ORTTList[fi];
                    int n = f?.Trains?.Count ?? 0;
                    string fname = f != null ? TtLabel(f.Description, f.ToString()) : setName;
                    var en = new TimetableList.Entry { Set = _cboTTSet.SelectedIndex, File = fi, Title = fname, Sub = Plural(n, "{0} tren", "{0} trenes"), Trains = n };
                    if (q.Length > 0 && !(en.Title + " " + en.Sub).Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                    entries.Add(en);
                }
                _ttListCap.Text = Tr("HORARIO") + (files > 0 ? "  ·  " + files : "");
                _ttList.EmptyText = _cboTTSet.Items.Count == 0 ? Tr("Esta ruta no tiene horarios (timetables).") : Tr("Nada coincide con el filtro.");
                _ttListSyncing = true;
                try
                {
                    _ttList.BeginUpdate();
                    _ttList.Items.Clear();
                    foreach (var en in entries) _ttList.Items.Add(en);
                    _ttList.EndUpdate();
                }
                finally { _ttListSyncing = false; }
            }
            _ttListSyncing = true;
            try
            {
                int idx = -1;
                for (int k = 0; k < _ttList.Items.Count; k++)
                    if (_ttList.Items[k] is TimetableList.Entry en && en.File == Math.Max(0, _cboTT.SelectedIndex)) { idx = k; break; }
                _ttList.SelectedIndex = idx;
            }
            finally { _ttListSyncing = false; }
        }

        void OnTTEntryChosen()
        {
            if (_ttListSyncing || _ttList.SelectedItem is not TimetableList.Entry en) return;
            if (en.File < _cboTT.Items.Count && _cboTT.SelectedIndex != en.File) _cboTT.SelectedIndex = en.File;
        }

        static string FirstHm(string t)
        {
            var m = System.Text.RegularExpressions.Regex.Match(t ?? "", @"(\d{1,2}):(\d{2})");
            return m.Success ? int.Parse(m.Groups[1].Value).ToString("00") + ":" + m.Groups[2].Value : "";
        }

        // Rellena el panel de salidas con los trenes del horario elegido (y sus paradas, del propio archivo).
        void FillBoard()
        {
            if (_ttBoard == null) return;
            var set = _cboTTSet.SelectedItem as TimetableInfo;
            int fi = _cboTT.SelectedIndex;
            TtStops.Table table = null;
            try
            {
                var files = set == null ? new List<string>() : TtStops.FilesOf(set.fileName);
                string file = fi >= 0 && fi < files.Count ? files[fi] : files.Count == 1 ? files[0] : null;
                table = TtStops.Load(file);
            }
            catch { }
            string q = _ttSearch?.Box.Text.Trim() ?? "";
            _ttStops.Clear();
            var rows = new List<DepartureBoard.Row>();
            foreach (var o in _cboTTTrain.Items)
            {
                var tr = o as Orts.Formats.OR.TimetableFileLite.TrainInformation;
                if (tr == null) continue;
                var stops = TtStops.StopsOf(table, TtStops.ColumnOf(table, tr.Train, tr.Column));
                _ttStops[tr] = stops;
                var r = new DepartureBoard.Row
                {
                    Train = tr, Name = tr.Train ?? "", Via = tr.Path ?? "",
                    Time = stops.Count > 0 ? stops[0].Dep : FirstHm(tr.StartTime),
                    From = stops.Count > 0 ? stops[0].Station : "", To = stops.Count > 1 ? stops[^1].Station : "",
                    Consist = !string.IsNullOrWhiteSpace(tr.LeadingConsist) ? tr.LeadingConsist : tr.Consist ?? "",
                    Stops = stops.Count >= 2 ? stops.Count - 2 : (table == null ? -1 : 0),
                };
                if (q.Length > 0 && !(r.Name + " " + r.From + " " + r.To + " " + r.Consist + " " + r.Via).Contains(q, StringComparison.OrdinalIgnoreCase)
                    && !stops.Any(x => x.Station.Contains(q, StringComparison.OrdinalIgnoreCase))) continue;
                rows.Add(r);
            }
            rows.Sort((a, b) => TtStops.Minutes(a.Time).CompareTo(TtStops.Minutes(b.Time)));
            string setName = set == null ? "" : TtLabel(set.Description, set.ToString());
            _ttBoard.Title = (Tr("SALIDAS") + (_curRoute != null ? "  ·  " + _curRoute.Name : "") + (setName.Length > 0 ? "  ·  " + setName : "")).ToUpper();
            _ttBoard.EmptyText = _cboTTSet.Items.Count == 0 ? Tr("Esta ruta no tiene horarios (timetables).") : Tr("Nada coincide con el filtro.");
            using (var fn = new Font("Consolas", 10f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold))
            {
                int nw = 0; foreach (var r in rows) nw = Math.Max(nw, TextRenderer.MeasureText(r.Name, fn, Size.Empty, TextFormatFlags.NoPadding).Width);
                _ttBoard.NameW = nw + Theme.Px(12);
            }
            _ttBoard.BeginUpdate();
            _ttBoard.Items.Clear();
            foreach (var r in rows) _ttBoard.Items.Add(r);
            _ttBoard.EndUpdate();
            SyncBoardFromCombo();
            UpdateItinerary();
        }

        void SyncBoardFromCombo()
        {
            if (_ttBoard == null || _ttSyncing) return;
            _ttSyncing = true;
            try
            {
                int idx = -1;
                for (int k = 0; k < _ttBoard.Items.Count; k++)
                    if (_ttBoard.Items[k] is DepartureBoard.Row r && ReferenceEquals(r.Train, _cboTTTrain.SelectedItem)) { idx = k; break; }
                _ttBoard.SelectedIndex = idx;
            }
            finally { _ttSyncing = false; }
        }

        void UpdateItinerary()
        {
            if (_ttItin == null) return;
            var tr = _cboTTTrain.SelectedItem as Orts.Formats.OR.TimetableFileLite.TrainInformation;
            if (tr == null) { _ttItin.SetContent(null, null, null, null, null); return; }
            _ttStops.TryGetValue(tr, out var stops);
            stops ??= new List<TtStops.Stop>();
            string sub;
            if (stops.Count >= 2)
            {
                int m1 = TtStops.Minutes(stops[^1].Arr), m0 = TtStops.Minutes(stops[0].Dep);
                int mins = m1 - m0; if (mins < 0) mins += 1440;
                sub = string.Format(Tr("Sale {0} de {1}  ·  llega {2}  ·  {3}"), stops[0].Dep, stops[0].Station, stops[^1].Arr,
                                    m0 < 0 || m1 < 0 ? TtStops.NoTime : mins >= 60 ? $"{mins / 60} h {mins % 60:00} min" : $"{mins} min");
            }
            else sub = string.Format(Tr("Sale a las {0}  ·  recorrido {1}"), FirstHm(tr.StartTime), tr.Path ?? "—");
            var brief = new List<string>();
            foreach (var block in new[] { tr.Briefing, (_cboTT.SelectedItem as Orts.Formats.OR.TimetableFileLite)?.Briefing })
                foreach (var p in (block ?? "").Replace("\r", "").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
                    if (p.Trim().Length > 0 && !brief.Contains(p.Trim())) brief.Add(p.Trim());
            _ttItin.SetContent("", tr.Train, sub, stops, brief);
        }

        Panel BuildMultijugadorPage()
        {
            var page = new Panel { BackColor = Theme.Bg };

            // ---- arriba: cómo quieres jugar · tus datos ----
            // (Los tamaños fijos de aquí los escala la ventana; el alto de arriba se calcula con el contenido, ver LayoutMpTop.)
            var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 212, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 12) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            var right = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, Radius = 12, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(6, 0, 0, 0) };
            _mpMode = new MpModeCards(new[] { ("🖥", Tr("Alojar partida"), Tr("Tu PC es el servidor (red local o puerto abierto)")),
                                              ("🔗", Tr("Unirme a un servidor"), Tr("Elige uno público abajo o escribe la dirección")) })
                      { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 6, 0), Caption = Tr("CÓMO QUIERES JUGAR") };
            // La nota sigue en una etiqueta (la rellena ApplyMPMode), pero la pinta la tarjeta.
            var info = new Label { Visible = false, Text = Tr("Se lanza la selección de «Exploración» (recorrido + tren) en red. Configura tu usuario; como cliente, indica el host y el puerto del servidor.") };
            _lblMPInfo = info;
            info.TextChanged += (s, e) => { _mpMode.Info = info.Text; LayoutMpTop(); };
            _mpMode.Info = info.Text;
            var left = _mpMode;

            // Los dos «radio» siguen existiendo (el resto del programa los consulta), pero no se ven: los mandan las tarjetas.
            var radios = new Panel { Visible = false };
            _rbServer = new ThemeRadio { Text = "  " + Tr("Servidor (alojar partida)"), Checked = true };
            _rbClient = new ThemeRadio { Text = "  " + Tr("Cliente (unirse a un servidor)") };
            radios.Controls.Add(_rbServer); radios.Controls.Add(_rbClient);
            _rbClient.CheckedChanged += (s, e) => { ApplyMPMode(); UpdateStatus(); _mpMode?.Select(_rbClient.Checked ? 1 : 0, raise: false); };
            // Primero el de servidor: si se desmarcaba antes el de cliente, ApplyMPMode veía los dos sin marcar
            // y «Alojar partida» se quedaba sin tu IP en «Servidor».
            _mpMode.Changed += i => { _rbServer.Checked = i == 0; _rbClient.Checked = i == 1; ApplyMPMode(); UpdateStatus(); };

            var data = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
            var lblD = new Label { Text = Tr("TUS DATOS"), Location = new Point(0, 0), AutoSize = true, ForeColor = Theme.Subtle, BackColor = Theme.Surface, Font = Theme.Font(8f, FontStyle.Bold) };
            data.Controls.Add(lblD);
            _txtMPUser = MPField(data, Tr("Usuario"), 28, 0);
            // Usuario de Multijugador con las MISMAS reglas que Open Rails (si no, no conecta):
            // de 4 a 10 caracteres, sin espacios, ni ', " o -, y que no empiece por un número.
            _txtMPUser.MaxLength = 10;
            _txtMPUser.TextChanged += (s, e) => OnMPUserChanged();
            _txtMPHost = MPField(data, Tr("Servidor"), 66, 0);
            _txtMPPort = MPField(data, Tr("Puerto"), 104, 0);
            _lblMPUserRule = new Label { Location = new Point(0, 142), Size = new Size(380, 34), ForeColor = Theme.Subtle, BackColor = Theme.Surface, AutoSize = false, Font = Theme.Font(8f),
                                         Text = Tr("Usuario: de 4 a 10 caracteres, sin espacios ni ' \" -, y sin empezar por un número (lo exige Open Rails).") };
            data.Controls.Add(_lblMPUserRule); data.Controls.Add(radios);
            right.Controls.Add(data);
            top.Controls.Add(left, 0, 0); top.Controls.Add(right, 1, 0);
            right.Controls.Add(info);
            _mpTop = top; _mpRight = right;
            top.SizeChanged += (s, e) => LayoutMpTop();
            ApplyMPMode();

            // ---- servidores públicos ----
            var bar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 44, ColumnCount = 4, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _lblServers = new Label { Text = Tr("Servidores públicos"), AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.Text, Font = Theme.Font(11f, FontStyle.Bold), Margin = new Padding(0, 0, 14, 0) };
            _srvTabs = MakeSubTabs(new[] { "Todos", "Con jugadores", "Rutas que tengo" }, i => { _srvFilter = i; FillServerCards(); });
            _srvTabs.Dock = DockStyle.Fill; _srvTabs.Margin = new Padding(0);
            _srvAge = new Label { AutoSize = true, Anchor = AnchorStyles.Right, ForeColor = Theme.Subtle, Font = Theme.Font(8.5f), Text = "", Margin = new Padding(10, 0, 10, 0) };
            var btnRefresh = new RoundButton { Text = Tr("Actualizar"), Icon = "⟲", Width = 120, Height = 30, Radius = 8, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 9f, Anchor = AnchorStyles.Right };
            btnRefresh.Click += (s, e) => LoadServers();
            bar.Controls.Add(_lblServers, 0, 0); bar.Controls.Add(_srvTabs, 1, 0); bar.Controls.Add(_srvAge, 2, 0); bar.Controls.Add(btnRefresh, 3, 0);
            // Si no caben las pestañas enteras, fuera la hora de la lista (las pestañas no se cortan).
            bar.SizeChanged += (s, e) => FitServerBar(bar, btnRefresh);
            _srvBar = bar; _srvRefreshBtn = btnRefresh;

            _srvGrid = new ServerCardGrid
            {
                Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0),
                LblHave = "✓ " + Tr("la tienes"), LblNoRoute = Tr("Ruta que no tienes en tu contenido"), LblNoPlayers = Tr("sin jugadores"), LblJoin = Tr("Unirme"),
                LblWaiting = Tr("Sin ruta todavía"), LblFree = "🔓 " + Tr("Libre"), LblLocked = "🔒 " + Tr("Protegido por {0} · desde las {1}"), LblOpen = "🔓 " + Tr("Abierto por {0} · sin contraseña"),
                ThumbOf = name =>
                {
                    var r = _routesAll.FirstOrDefault(x => string.Equals(x.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)
                                                        || string.Equals(System.IO.Path.GetFileName((x.Path ?? "").TrimEnd('\\', '/')), name, StringComparison.OrdinalIgnoreCase));
                    if (r == null) return null;
                    var imgs = ContentImages.ForRoute(r.Path);
                    return ImageCache.Get(imgs.thumb, 300, 184, () => { try { BeginInvoke((Action)(() => _srvGrid?.Invalidate())); } catch { } });
                }
            };
            _srvGrid.Selected += it => SelectServer(it.S);
            _srvGrid.Join += it => { SelectServer(it.S); PlayMultiplayer(); };
            var srvHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 6, 0, 0) };
            srvHost.Controls.Add(_srvGrid);

            // Se actualiza solo cada 30 s mientras la página está a la vista (lista + candados, de una vez y sin
            // repintar si nada ha cambiado); el contador «hace X s» corre cada segundo.
            _mpStatusTimer = new Timer { Interval = 30000 };
            _mpStatusTimer.Tick += (s, e) => { if (page.Visible && !IsMinimizedNow()) LoadServers(); };
            var ageTimer = new Timer { Interval = 1000 };
            ageTimer.Tick += (s, e) => UpdateServerAge();
            page.VisibleChanged += (s, e) => { if (page.Visible) { _mpStatusTimer.Start(); ageTimer.Start(); LayoutMpTop(); } else { _mpStatusTimer.Stop(); ageTimer.Stop(); } };

            page.Controls.Add(srvHost);
            page.Controls.Add(bar);
            page.Controls.Add(top);
            return page;
        }

        bool IsMinimizedNow() => WindowState == FormWindowState.Minimized;

        TableLayoutPanel _mpTop; Control _mpRight, _srvBar, _srvRefreshBtn;

        // Alto de «Cómo quieres jugar · Tus datos» según lo que ocupa cada tarjeta con su ancho real.
        void LayoutMpTop()
        {
            if (_mpTop == null || _mpMode == null || _mpRight == null || _mpTop.Width <= 0) return;
            int leftW = _mpMode.Width > 0 ? _mpMode.Width : _mpTop.Width / 2;
            int need = _mpMode.NeededHeight(leftW);
            int rightNeed = 0;
            foreach (Control c in _mpRight.Controls) foreach (Control k in c.Controls) if (k.Visible) rightNeed = Math.Max(rightNeed, k.Bottom);
            rightNeed += _mpRight.Padding.Vertical + 6;
            int h = Math.Max(need, rightNeed) + _mpTop.Padding.Vertical;
            if (Math.Abs(_mpTop.Height - h) > 1) _mpTop.Height = h;
            _mpMode.Invalidate();
        }

        void FitServerBar(Control bar, Control btn)
        {
            if (_srvTabs == null || _lblServers == null || _srvAge == null) return;
            int tabsW = 0; foreach (Control c in _srvTabs.Controls) tabsW += c.Width + c.Margin.Horizontal;
            int ageW = TextRenderer.MeasureText(_srvAge.Text.Length > 0 ? _srvAge.Text : "actualizado hace 59 s", _srvAge.Font).Width + _srvAge.Margin.Horizontal;
            int free = bar.Width - _lblServers.Width - _lblServers.Margin.Horizontal - btn.Width - btn.Margin.Horizontal - tabsW;
            bool show = free >= ageW;
            _srvAge.Visible = show;   // siempre: con la pestaña oculta, Visible devuelve false
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

        void SelectServer(GameServer sv)
        {
            if (sv == null) return;
            _rbClient.Checked = true;
            _txtMPHost.Text = sv.Ip;
            _txtMPPort.Text = sv.Port;
            UpdateStatus();
        }

        void LoadServers()
        {
            if (_srvGrid != null && (_serversAll == null || _serversAll.Count == 0)) { _srvGrid.EmptyText = Tr("Cargando…"); _srvGrid.Invalidate(); }
            Task.Run(async () =>
            {
                var list = await Servers.FetchAsync();
                BeginInvoke((Action)(async () =>
                {
                    bool first = _serversAll == null || _serversAll.Count == 0;
                    // Si la descarga falla, se queda la lista que hay (nada desaparece de golpe).
                    if (list != null && (list.Count > 0 || first)) _serversAll = list;
                    _srvLoadedUtc = DateTime.UtcNow;
                    if (first) FillServerCards();   // la primera vez se enseña ya; el candado llega enseguida
                    await LoadMpLocks();             // quién protege cada uno (si hay sesión y el SQL)
                    FillServerCards();
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
        Label MakeSectionLabel(string t) => new Label { Text = Tr(t), ForeColor = Theme.Subtle, Font = Theme.Font(8f, FontStyle.Bold), Height = 34, Dock = DockStyle.Top, Padding = new Padding(2, 3, 0, 11), AutoEllipsis = true, UseCompatibleTextRendering = false };

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
            var lb = new BufferedListBox
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
            if (_uiRevealed && i != 0 && _curRoute != null) DismissRutaWelcome();   // ya usa la ruta cargada: al volver, su ficha
            for (int k = 0; k < _pills.Length; k++) { _pills[k].Active = (k == i); _pills[k].Invalidate(); }
            var __lay = Perf.T("ShowPage " + i + " · maquetación");
            _root?.SuspendLayout();
            _pageHost.SuspendLayout();
            // Se ocultan las demás, se ajusta la columna RUTAS y se enseña la pestaña (sin volver a maquetarla
            // entera si conserva su tamaño: ShowFast).
            var pages = new Control[] { _pageRuta, _pageActividad, _pageExplora, _pageHorarios, _pageMulti, _pageEditor, _pageEmpresas };
            int[] ids = { 0, 1, 2, 3, 4, PageEditor, PageEmpresas };
            for (int k = 0; k < pages.Length; k++) if (ids[k] != i && pages[k].Visible) pages[k].Visible = false;
            ApplySidebarForPage(i);   // Editor y Empresas: sin columna RUTAS (todo el ancho para el contenido)
            for (int k = 0; k < pages.Length; k++) if (ids[k] == i) ShowFast(pages[k], true);
            _pageHost.ResumeLayout(false);
            _root?.ResumeLayout(true);    // una sola maquetación con la página y el ancho ya definitivos
            __lay.Dispose();
            var __rest = Perf.T("ShowPage " + i + " · datos");
            _btnPlay.Visible = i != 0 && i != 4 && i != PageEditor && i != PageEmpresas;   // estas pestañas no usan CONDUCIR
            _btnConnect.Visible = i == 4;                                                  // Multijugador: CONECTAR
            UpdateDutyHostVisible();   // barra "de servicio" en Empresas (solo si perteneces a alguna empresa)
            _prefs.LastTab = i;
            if (i == 4 && (_serversAll == null || _serversAll.Count == 0)) LoadServers();
            // El editor enseña su estructura al momento y cada lista va con su propio «Cargando…».
            if (i == PageEditor) OnEditorShown();
            if (i == PageEmpresas) OnEmpresasShown();
            if (i == 3) { UpdateTimetableBriefing(); UpdateTimetablePreview(); }   // resumen + render 3D del tren del timetable
            RutaLiveTimerUpdate();   // el mapa en vivo de Ruta solo consulta mientras se ve
            UpdateStatus();
            LayoutBottomBar();   // Ruta y el Editor no llevan barra inferior
            __rest.Dispose();
            if (previous != i && IsHandleCreated)
            {
                using (Perf.T("ShowPage " + i + " · pintado")) _pageHost.Refresh();          // la sección nueva se pinta ya…
                using (Perf.T("ShowPage " + i + " · fundido")) _pageFade?.Play(_pageHost);   // …y desde esa imagen se funde
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
            // Acceso directo «SelectOR (Open Rails <versión>)» en el escritorio, una vez por versión de OR.
            try { DesktopShortcut.Ensure(ORTS.Common.VersionInfo.VersionOrBuild, _prefs); } catch { }

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
                    UpdateRouteOverview();
                    _routesOverlay?.SetState(_routesAll.Count == 0 ? ListStatePanel.Mode.Empty : ListStatePanel.Mode.Hidden, Tr("Sin rutas"), Tr("Esta carpeta de contenido no tiene rutas. Elige otra arriba, en «Contenido»."));
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
                    UpdateRouteOverview();
                    StartTrainClassification(consists);   // tipo de cada tren para los filtros (en segundo plano)
                    LoadLog("lista de trenes llena");
                    _consistsOverlay?.SetState(_consistsAll.Count == 0 ? ListStatePanel.Mode.Empty : ListStatePanel.Mode.Hidden, Tr("Sin trenes"), Tr("Esta carpeta de contenido no tiene composiciones (.con) en TRAINS\\CONSISTS."));
                    UpdateTimetablePreview();   // ya hay consists → resuelve el tren del timetable (si estaba pendiente)
                    RebuildCompanyEngs();       // recalcula qué trenes son de mis empresas (etiqueta)
                    LoadLog("horario y trenes de empresa hechos");
                    LoadStep("consists");
                    if (!_uiRevealed) Prewarm2D(); else LoadStep("vistas2d");   // vistas 2D listas antes de abrir el menú
                    EditorContentChanged();     // editor de composiciones: lista de .con de esta carpeta
                    LoadLog("editor preparado");
                    BeginInvoke((Action)(() => LoadLog("interfaz libre tras los trenes")));
                    // Formaciones fijas (automotores) del contenido: se calculan ya, en segundo plano,
                    // para que abrir Compra no tenga que leer los miles de .con en ese momento.
                    Task.Run(() => { try { EnsureEngUnits(); } catch { } finally { LoadStep("units"); } try { PrewarmFleetLists(); } catch { } });
                }));
            });
        }

        void OnRouteChanged()
        {
            var route = _lstRoutes.SelectedItem as Route;
            if (route == null) { _curRoute = null; UpdateRouteOverview(); return; }
            _curRoute = route;
            _prefs.LastRoute = route.Path;
            _banner.RouteName = route.Name;
            _routeDescBox.Text = string.IsNullOrEmpty(route.Description) ? "" : route.Description;
            _actMeta.Clear();
            UpdateRouteOverview();
            LoadRouteStats(route);
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
                    UpdateRouteOverview();
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
            if (_routesCap != null) _routesCap.Text = Tr("RUTAS") + "  ·  " + _lstRoutes.Items.Count;
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
            var counts = new int[ActFilterNames.Length];
            _lstActivities.BeginUpdate();
            _lstActivities.Items.Clear();
            foreach (var a in _activitiesAll)
            {
                if (q.Length > 0 && (a.Name == null || a.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                var m = ActMetaOf(a);
                bool[] pass = { true, m.Diff == 0, m.Diff == 1, m.Diff == 2, m.DurMin >= 0 && m.DurMin < 60 };
                for (int k = 0; k < pass.Length; k++) if (pass[k]) counts[k]++;
                if (!pass[_actFilter]) continue;
                _lstActivities.Items.Add(a);
            }
            _lstActivities.EmptyText = _activitiesAll.Count == 0 ? Tr("Esta ruta no tiene actividades. Usa «Exploración» u «Horarios».") : Tr("Nada coincide con el filtro.");
            _lstActivities.EndUpdate();
            if (_actChips != null)
            {
                using var f = Theme.Font(9f, FontStyle.Bold);
                for (int k = 0; k < counts.Length && k < _actChips.Controls.Count; k++)
                    if (_actChips.Controls[k] is RoundButton b)
                    {
                        string t = ActFilterText(k) + "  " + counts[k];
                        if (b.Text != t) { b.Text = t; b.Width = TextRenderer.MeasureText(t, f).Width + 28; b.Invalidate(); }
                    }
            }
            if (_lstActivities.SelectedIndex < 0 && _lstActivities.Items.Count > 0) _lstActivities.SelectedIndex = 0;
            if (_activitiesAll.Count == 0) _txtBriefing.Text = Tr("Esta ruta no tiene actividades. Usa «Exploración» u «Horarios».");
        }

        void RefreshConsistList()
        {
            string q = _consistSearch == null ? "" : _consistSearch.Box.Text.Trim();
            bool favOnly = _chkTrainFavOnly != null && _chkTrainFavOnly.Checked;
            string coFilter = SelectedCompany(_cboTrainCompany);
            var prev = _lstConsists.SelectedItem as TrainItem;
            var shown = new List<object>(_consistsAll.Count);
            var kindCount = new int[6];
            foreach (var c in _consistsAll)
            {
                if (favOnly && !_prefs.FavoriteTrains.Contains(c.FilePath)) continue;
                if (q.Length > 0 && (c.Name == null || c.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                if (coFilter != null && !HasCo(ConsistCompanies(c), coFilter)) continue;
                var sp = KnownSpec(c);
                if (sp != null) { kindCount[sp.Freight ? 2 : 1]++; kindCount[sp.Automotor ? 3 : (sp.Freight || !sp.Automotor ? 4 : 0)]++; }
                kindCount[0]++;
                if (_trainKind >= 1 && _trainKind <= 4)
                {
                    if (sp == null) continue;   // aún sin clasificar: entra en cuanto se sepa
                    bool ok = _trainKind switch { 1 => !sp.Freight, 2 => sp.Freight, 3 => sp.Automotor, 4 => !sp.Automotor, _ => true };
                    if (!ok) continue;
                }
                shown.Add(c);
            }
            bool same = shown.Count == _lstConsists.Items.Count;
            for (int k = 0; same && k < shown.Count; k++) same = ReferenceEquals(shown[k], _lstConsists.Items[k]);
            _lstConsists.BeginUpdate();
            if (!same) { _lstConsists.Items.Clear(); _lstConsists.Items.AddRange(shown.ToArray()); }
            _lstConsists.EmptyText = _trainKind >= 1 && _trainKind <= 4 && _classDone < _classTotal ? Tr("Clasificando los trenes del contenido…") : Tr("Nada coincide con el filtro.");
            _lstConsists.EndUpdate();
            if (_trainChips != null)
            {
                using var f = Theme.Font(9f, FontStyle.Bold);
                for (int k = 0; k < 5 && k < _trainChips.Controls.Count; k++)
                    if (_trainChips.Controls[k] is RoundButton b)
                    {
                        string t = TrainKindText(k) + (k == 0 || _classDone >= _classTotal ? "  " + kindCount[k].ToString("N0", EsEs) : "");
                        if (b.Text != t) { b.Text = t; b.Width = TextRenderer.MeasureText(t, f).Width + 28; b.Invalidate(); }
                    }
            }
            WarmConsistListSoon();
            if (prev != null)
                for (int i = 0; i < _lstConsists.Items.Count; i++)
                    if (((TrainItem)_lstConsists.Items[i]).FilePath == prev.FilePath) { _lstConsists.SelectedIndex = i; break; }
            if (_lstConsists.SelectedIndex < 0 && !string.IsNullOrEmpty(_prefs.LastConsist))
                for (int i = 0; i < _lstConsists.Items.Count; i++)
                    if (((TrainItem)_lstConsists.Items[i]).FilePath == _prefs.LastConsist) { _lstConsists.SelectedIndex = i; break; }
            if (_lstConsists.SelectedIndex < 0 && _lstConsists.Items.Count > 0) _lstConsists.SelectedIndex = 0;
        }

        // Mientras la pestaña está cerrada, la lista no tiene ventana nativa y Windows se guarda los miles de
        // trenes para volcarlos al abrirla por primera vez. Se crea poco después de llenarla, con la interfaz
        // libre, y así abrir Exploración es inmediato.
        System.Windows.Forms.Timer _consistWarm;
        void WarmConsistListSoon()
        {
            if (_lstConsists == null || _lstConsists.IsHandleCreated || !IsHandleCreated) return;
            if (_consistWarm == null)
            {
                _consistWarm = new System.Windows.Forms.Timer { Interval = 1500 };
                _consistWarm.Tick += (s, e) =>
                {
                    _consistWarm.Stop();
                    try { if (!_lstConsists.IsHandleCreated && IsHandleCreated) _ = _lstConsists.Handle; } catch { }
                };
            }
            _consistWarm.Stop(); _consistWarm.Start();
        }

        void RefreshPathList()
        {
            _cboStart.Items.Clear();
            foreach (var st in _pathsAll.Select(p => p.Start ?? "").Distinct().OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase)) _cboStart.Items.Add(st);
            if (_cboStart.Items.Count > 0) _cboStart.SelectedIndex = 0;   // dispara OnStartChanged → rellena destinos
            else _cboEnd.Items.Clear();
            UpdateTripMeta();
        }

        // Nombre de un conjunto o un horario: su descripción o, si no la trae, el nombre del archivo (no la ruta completa).
        static string TtLabel(string desc, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
            fallback ??= "";
            return fallback.IndexOf('\\') >= 0 || fallback.IndexOf('/') >= 0 ? SysPath.GetFileNameWithoutExtension(fallback) : fallback;
        }

        void RefreshTimetableSets()
        {
            _cboTTSet.Items.Clear();
            foreach (var t in _timetablesAll) _cboTTSet.Items.Add(t);
            if (_cboTTSet.Items.Count > 0) _cboTTSet.SelectedIndex = 0;
            else { _cboTT.Items.Clear(); _cboTTTrain.Items.Clear(); _txtTTBriefing.Text = "Esta ruta no tiene horarios (timetables)."; FillBoard(); }
            BuildTTChips();
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
            FillBoard();
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
            _ttPreview.Caption = c?.FilePath != null ? SysPath.GetFileNameWithoutExtension(c.FilePath) : (c != null ? c.Name : "");   // el .con
            _ttPreview.EmptyText = c == null ? (tr == null ? Tr("Elige un tren del panel de salidas") : Tr("Este tren no está en tu contenido")) : null;
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
            // «A+B» une composiciones; un nombre que lleva «+» va entre < > («<UT 447 + 448>»): ahí no se parte.
            string name = ttConsist.Trim();
            if (name.StartsWith("<")) { int gt = name.IndexOf('>'); name = gt > 0 ? name.Substring(1, gt - 1) : name.TrimStart('<'); }
            else name = name.Split('+')[0];
            name = name.Trim();
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
            if (a == null) { _txtBriefing.Text = ""; _actDetail?.SetContent(null, null, null, null, null); return; }
            if (_actDetail != null)
            {
                var m = ActMetaOf(a);
                Color[] dc = ActivityCardGrid.DiffColors;
                string[] dn = { DiffWord(0), DiffWord(1), DiffWord(2) };
                var facts = new List<(string, string, Color)>
                {
                    (Tr("SALIDA"), m.Start.Length > 0 ? m.Start : "—", Theme.Text),
                    (Tr("DURACIÓN"), m.Duration.Length > 0 ? m.Duration : "—", Theme.Text),
                    (Tr("DIFICULTAD"), m.Diff >= 0 ? dn[m.Diff] : "—", m.Diff >= 0 ? dc[m.Diff] : Theme.Text),
                    (Tr("TREN"), m.Consist.Length > 0 ? m.Consist : "—", Theme.Text),
                    (Tr("ESTACIÓN DEL AÑO"), m.SeasonKind.Length > 0 ? SeasonText(m.SeasonKind) : "—", Theme.Text),
                    (Tr("CLIMA"), m.WeatherKind.Length > 0 ? WeatherText(m.WeatherKind) : "—", Theme.Text),
                };
                var paras = new List<string>();
                foreach (var block in new[] { a.Description, a.Briefing })
                    foreach (var p in (block ?? "").Replace("\r", "").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
                        if (p.Trim().Length > 0 && !paras.Contains(p.Trim())) paras.Add(p.Trim());
                string sub = m.From.Length > 0 ? m.From + (m.To.Length > 0 && m.To != m.From ? "  →  " + m.To : "") : (_curRoute?.Name ?? "");
                _actDetail.SetContent(a.Name, sub, m.LocoPath, facts, paras);
            }
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
            UpdateExploreSpecs(c);
            _trainPreview.Caption = c?.FilePath != null ? SysPath.GetFileNameWithoutExtension(c.FilePath) : "";   // el .con: así se encuentra luego
            _trainPreview.EmptyText = c == null ? Tr("Elige un tren") : null;
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

        async void PlayMultiplayer()
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

            // Guardar ajustes MP en el registro (RunActivity los lee de ahí). Se releen antes las opciones
            // ACTUALES de OR: con las cargadas al abrir SelectOR, se volvían a escribir valores viejos de todas
            // las demás opciones (por ejemplo, si se cambiaron después en «Opciones OR»).
            try
            {
                try { _settings = new UserSettings(new string[0]); } catch { }
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
            // Servidor PÚBLICO: la contraseña la gestiona SelectOR (el primero la pone; los demás la escriben).
            if (_rbClient.Checked)
            {
                var pub = PublicServerFor(host, port.ToString());
                if (pub != null && !await MpGate(pub)) return;
            }
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
            EnsureOrWebServer();   // el HUD, el pupitre, los viajeros y el carné lo necesitan desde el arranque
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
                if (serviceId != null) PublishServiceStrip(serviceId, _empOnDutyCompany?.Id, CurrentDrivenConsist());   // composición 2D del servicio
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
                    _mpClientRun = args.IndexOf("-multiplayerclient", StringComparison.OrdinalIgnoreCase) >= 0;
                    _pendingServiceId = serviceId;
                    _svcOpenedUtc = svc ? DateTime.UtcNow : (DateTime?)null;
                    if (!svc) _driveStartUtc = DateTime.UtcNow;
                    CaptureDrivenTrain();           // tren conducido (para ponerse de servicio desde la barra superior)
                    if (svc) SaveServiceJournal(force: true);   // por si se cierra todo de golpe (MainMenuForm.Recuperar.cs)
                    // Horarios: la hoja de ruta sale ya con el recorrido del tren y sus horas de paso.
                    try { _roadTtPlan = args.IndexOf("-timetable", StringComparison.OrdinalIgnoreCase) >= 0 ? BuildTtRoadPlan() : null; } catch { _roadTtPlan = null; }
                    StartKmTracking(withPax: true); // posición para el mapa y viajeros (en servicio o conducción libre)
                    // Actividad: lo mismo con su recorrido y las paradas del tren del jugador (el .tdb se lee aparte).
                    if (args.IndexOf("-activity", StringComparison.OrdinalIgnoreCase) >= 0 && _lstActivities.SelectedItem is Activity actSel)
                    {
                        string rdir = _curRoute?.Path ?? "";
                        _ = Task.Run(() => BuildActRoadPlan(actSel, rdir)).ContinueWith(t =>
                        {
                            try { if (t.Status == TaskStatus.RanToCompletion && t.Result != null) BeginInvoke((Action)(() => { if (string.Equals(_road.RouteDir, rdir, StringComparison.OrdinalIgnoreCase)) ApplyRoadPlan(t.Result); })); } catch { }
                        });
                    }
                    ShowServiceHud(svc);            // HUD sobre OR (en servicio o no)
                    if (_prefs.CabHudOn) ShowCabHud();   // pupitre, si lo dejaste abierto la última vez
                    if (_prefs.ChatHudOn) ShowChatHud(); // chat de empresa, si lo dejaste abierto
                    ShowDriveBar();                 // barra oculta arriba en el centro (HUD · mapa · servicio)
                    p.EnableRaisingEvents = true;
                    p.Exited += (s, e) => { try { BeginInvoke(new Action(() => { MpRelease(); OnDriveReturned(); })); } catch { } };
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
                case PageEmpresas: _lblStatus.Text = _empSel != null ? $"{Tr("Empresa")}: {_empSel.Name}   ·   {r}" : r; break;
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
