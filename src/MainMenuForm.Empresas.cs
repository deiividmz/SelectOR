// Pestaña "Empresas": empresas ferroviarias multi-maquinista sobre Supabase.
// Tres vistas conmutables: configuración del servidor, acceso (login/registro) y panel.
// La economía vive en el servidor (funciones SQL); aquí solo se muestra y se llama por REST.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using ORTS.Menu;
using OrPath = ORTS.Menu.Path;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        // --- Vistas ---
        Panel _empCfg, _empAuth, _empHome, _empConnectingView;
        // --- Config ---
        RoundedInput _empUrl, _empKey; Label _empCfgMsg;
        // --- Auth ---
        RoundedInput _empEmail, _empPass; Label _empAuthMsg; CheckBox _empRemember;
        bool _empAutoTried;   // para no reintentar el auto-login en bucle
        bool _empConnecting;  // auto-login en curso: muestra "Conectando…" en vez del formulario (sin parpadeo)
        bool _empRegisterMode; // muestra el campo "Nombre de maquinista" al pulsar Registrarse
        // --- Home ---
        Label _empUserLbl, _empCoTitle, _empHomeMsg;
        Label _empIdChip;   // chip pequeño con el ID de usuario, copiable con un clic
        ComboBox _empCoCombo;                       // selector de empresa (arriba del menú lateral)
        bool _suppressCoSel;                        // ignora SelectedIndexChanged del selector durante la recarga
        Panel _empStatsCard, _empStatsGap;           // KPIs de la empresa (se ocultan sin empresa y en Ranking / Mi perfil)
        // Valores de los KPIs (la tira se pinta en DrawKpiStrip; se actualizan y se hace Invalidate).
        string _kpiTreasuryTxt = "—", _kpiMembersTxt = "—", _kpiServicesTxt = "—", _kpiKmTxt = "—";
        Color _kpiTreasuryCol = Theme.Accent;
        List<(Label header, int[] items)> _navGroups; // encabezados del menú lateral (se ocultan si su grupo no tiene secciones visibles)
        PictureBox _empLogoPic; Label _logoLink;   // logotipo en la cabecera + enlace para cambiarlo
        Label _empRenameLink;                       // icono ✎ junto al nombre de empresa (renombrar)
        readonly Dictionary<string, (string src, Image img)> _logoCache = new();
        StyledTable _empSvcList;
        Panel _empCreateHost;              // acciones bajo el selector: crear / unirse / eliminar
        bool? _createAreaSuper;            // rol con el que se construyó la zona (para rehacerla al cambiar)
        // --- Refresco en vivo (Supabase Realtime) ---
        SupaRealtime _rt;
        Timer _rtDebounce;                 // agrupa ráfagas de cambios para no recargar de más
        // --- Estado ---
        readonly List<EmpCompany> _empCompanies = new();
        EmpCompany _empSel;
        bool _empForceCfg, _empLoaded;
        // --- Servicio (integración con la conducción) ---
        EmpCompany _empOnDutyCompany;      // empresa "de servicio": registra el próximo viaje
        string _pendingServiceId;          // id del servicio abierto en el servidor
        DateTime? _svcOpenedUtc;           // cuándo se abrió (para medir el viaje si el cronómetro no llegó a arrancar)
        ServiceHudOverlay _serviceHud;     // HUD pequeño sobre OR mientras se conduce el servicio
        string _empStartFailReason;        // motivo legible si no se pudo abrir el servicio (para avisar al maquinista)
        double _estimatedKm;               // km estimados (para prerrellenar el cierre)
        double _estPatKm;                  // longitud de la ruta (.pat), respaldo/cota
        DateTime _driveStartUtc;           // lanzamiento de OR (para localizar el .save de la sesión)
        DateTime? _svcClockUtc;            // inicio del CRONÓMETRO del servicio: cuando el escenario ya está abierto
                                           // (primera posición del tren que da OR), no durante la carga

        // Segundos de servicio (0 si el escenario aún no había terminado de cargar).
        int ServiceSeconds() => _svcClockUtc == null ? 0 : (int)Math.Max(0, (DateTime.UtcNow - _svcClockUtc.Value).TotalSeconds);
        int _tripDurationS;                // duración REAL del viaje en segundos (anti-trampas)
        // Seguimiento de km reales por la API web de OR (posición del tren en vivo)
        Timer _kmTimer; System.Net.Http.HttpClient _kmHttp;
        double _trackedMeters, _tLat, _tLon; bool _tHave;
        // Modelo de viajeros (PseudoPAX): embarque en andenes reales de la ruta
        List<(string station, double lat, double lon)> _paxStations = new();
        readonly HashSet<string> _paxDone = new();   // andenes ya embarcados este servicio
        int _paxBoarded, _paxOnboard, _paxCapacity;  // billetes totales, a bordo ahora, tope de la unidad
        bool _paxActive; bool _paxBusy; volatile bool _paxWanted;   // activo / poll en curso / se quiere seguir cargando andenes
        double _paxDemandBase = 60;                  // demanda base por andén (app_settings.fleet_pax_demand)
        Panel _soloPanel;   // panel "Mi perfil"
        // Panel "Mi perfil" (estadísticas privadas del maquinista)
        BarChart _profTrains; DonutChart _profRoutes;
        Label _profKmVal, _profTripsVal, _profTimeVal, _profSpeedVal, _profPaxVal;
        // Rango + insignias (Mi perfil)
        Label _rankName, _rankNext, _rankStep; Panel _rankTrack; double _rankPct; FlowLayoutPanel _badges;
        // Panel financiero (dentro de Banca)
        Label _finIncome, _finExpense, _finNet; DonutChart _finBreakdown; AreaChart _finMonthly;
        RoundButton _empDutyBtn, _empCloseOpenBtn;
        // --- Socios ---
        readonly List<EmpMember> _members = new();
        string _myRole;                    // mi rol en la empresa seleccionada (owner/manager/driver)
        StyledTable _memberList;
        RoundedInput _memberEmail;
        ComboBox _memberRole;
        Label _memberMsg;
        RoundButton _memberAddBtn, _memberDelBtn;
        // Solicitudes de ingreso (un maquinista pide unirse a la empresa)
        StyledTable _joinList; readonly List<string> _joinIds = new();
        // --- Banca (historial de movimientos) ---
        StyledTable _bankList; Label _bankMsg; Panel _bankPanel;
        // --- Cabecera profesional ---
        Label _empRoleLbl;
        RoundButton _memberRoleBtn;
        // --- Subpestañas (Servicios / Banca / Socios / Ajustes / Ranking / Mi perfil / Revisión) ---
        RoundButton[] _empSubtabs;
        Panel _svcPanel, _memberPanel, _tariffPanel, _rankPanel, _reviewPanel, _usersPanel;
        int _empSubtab;
        // Revisión de servicios sospechosos (superadmin)
        // Gestión de usuarios (solo superadmin)
        StyledTable _usersList; Label _usersMsg;
        readonly List<string> _userIds = new(); readonly List<bool> _userIsSelf = new();
        // Administración de TODAS las empresas (solo superadmin)
        Panel _allCompPanel; StyledTable _allCompList; Label _allCompMsg;
        readonly List<string> _allCompIds = new();
        // Flota de la empresa (vehículos por .eng) — tabla + preview 3D
        Panel _fleetPanel; StyledTable _fleetList; Label _fleetMsg;
        // Compra/alquiler (subpestaña aparte): se compra/alquila un CONSIST completo (no un .eng
        // suelto); cada máquina de tracción que contiene (menos las ya en la flota) se da de alta
        // por separado, así que esa misma máquina queda desbloqueada para cualquier otro consist
        // que la reutilice.
        Panel _buyPanel; Label _buyMsg;
        ListBox _fleetConsistList; RoundedInput _fleetConsistSearch;
        RoundButton _fleetBuyBtn, _fleetRentBtn, _fleetCompBtn, _fleetRemoveBtn, _fleetServiceBtn, _fleetPlateBtn; TrainPreviewPanel _fleetPreview;
        readonly List<string> _fleetPlates = new();   // matrícula de cada fila de la flota ("" = sin matrícula)
        // Ficha técnica del showroom (Comprar/Alquilar): título de tipo, chips de specs y precios.
        Label _fleetHeaderLbl, _fleetSpecType, _vPower, _vSpeed, _vPlazas, _vConfort, _vMasa, _vFreno, _vDensity, _vBuy, _vRent, _fleetOwnedBadge;
        // Estado del render 3D rotatable del preview de Flota (igual que el de Exploración).
        ShapeGeom _fleetGeom; float _fleetYaw, _fleetPitch; bool _fleetFlip; string _fleetPrevEngPath;
        System.Windows.Forms.Timer _fleetRerender;
        readonly HashSet<string> _fleetRenderingShapes = new(StringComparer.OrdinalIgnoreCase);
        // --- Flota (vehículos propios): vista 3D + ficha del vehículo seleccionado (como Compra) ---
        TrainPreviewPanel _fleetOwnPreview; Label _fleetOwnTitle, _voKind, _voProp, _voMaint, _voEstado, _voCap, _voTraction; RoundButton _fleetOwnCompBtn;
        ShapeGeom _fleetOwnGeom; float _fleetOwnYaw, _fleetOwnPitch; bool _fleetOwnFlip; string _fleetOwnEngPath;
        readonly HashSet<string> _fleetOwnRendering = new(StringComparer.OrdinalIgnoreCase);
        readonly List<string> _fleetRowEng = new();          // .eng local por fila (o "" si no lo tienes)
        readonly List<string[]> _fleetRowDetail = new();     // [nombre, tipo, propiedad, mantenimiento, estado]
        readonly List<Color> _fleetRowEstColor = new();
        readonly List<string> _fleetIds = new();
        readonly List<string> _fleetStatus = new();   // estado de cada fila (available/maintenance_due)
        readonly HashSet<string> _fleetOwnedNames = new(StringComparer.OrdinalIgnoreCase);   // carpeta|.eng que la empresa YA tiene (cualquier estado)
        // Trenes que pertenecen a una empresa DE LA QUE SOY SOCIO (para la etiqueta en Exploración/Horarios).
        readonly Dictionary<string, List<string>> _companyVehNames = new(StringComparer.OrdinalIgnoreCase);  // carpeta|.eng comprado → empresas que lo tienen
        readonly Dictionary<string, List<string>> _companyEngs = new(StringComparer.OrdinalIgnoreCase);      // + coches de la unidad → empresas
        List<(string name, string folder, string path, string kind)> _fleetEngs = new();   // .eng del contenido: name → path (para tasar)
        List<TrainItem> _fleetConsists = new();   // consists comprables del contenido actual (showroom)
        // --- Compra POR MÁQUINA: una fila por modelo .eng del contenido (lo común entre maquinistas) ---
        // Separador de la lista de Compra: el modelo (carpeta del contenido) y cuántas máquinas trae.
        sealed class BuyGroupHeader
        {
            public string Folder;
            public int Count;      // máquinas del modelo
            public int Owned;      // unidades de ese modelo que ya tiene la empresa
        }

        sealed class BuyMachine
        {
            public string Name, Folder, Path;
            public int UnitCars;                      // >1 si es un automotor de formación fija
            public override string ToString() => Name;
        }
        bool _buyByMachine = true;                       // vista por defecto

        // --- Formaciones fijas (automotores) deducidas del CONTENIDO ---
        // Muchos automotores (p. ej. el 592: motriz + remolque + motriz) no declaran PassengerCapacity
        // ni circulan en formación de solo motrices, así que no basta con mirar el .eng: se deducen de
        // los .con. Dos .eng son del MISMO vehículo si comparten carpeta y nombre parecido y uno de
        // ellos NUNCA aparece sin el otro, siempre acoplado a menos de MaxUnitGap coches.
        sealed class EngUnit
        {
            public string Head;                       // .eng cabeza (el que más veces encabeza tren)
            public List<string> Members = new();      // todos los .eng de la formación (cabeza incluida)
            public string RepConsist;                 // .con más corto que la lleva entera
            public int Cars = 1;                      // coches del bloque (motrices + remolques)
            public bool RigidCoupled;                 // lo dicen los propios archivos: acople de barra
        }
        const int MaxUnitGap = 3;        // coches de separación máxima para considerarlos acoplados
        const int MaxUnitMembers = 6;    // tope de motrices por formación (evita encadenar unidades)
        Dictionary<string, EngUnit> _engUnits;        // nombre .eng (cualquier miembro) → formación
        Dictionary<string, List<string>> _conEngines;  // .con → sus coches motrices (evita releerlos)
        Dictionary<string, string> _firstConsistOfEng;  // .eng → primer .con donde aparece
        HashSet<string> _engHidden;                   // .eng que NO se ofrecen sueltos (van con su cabeza)
        string _engUnitsFolder; int _engUnitsConsistCount = -1;
        readonly object _engUnitsLock = new();
        ListBox _fleetEngList;                           // lista de máquinas
        Control _buyConsistCard, _buyEngCard;
        readonly List<BuyMachine> _buyMachines = new();
        readonly Dictionary<string, int> _fleetOwnedCount = new(StringComparer.OrdinalIgnoreCase);   // carpeta|.eng → unidades en la flota
        readonly HashSet<string> _buyGrouped = new(StringComparer.OrdinalIgnoreCase);                 // carpetas con cabecera (sus filas van sangradas)
        int _fleetOwnedStamp;                                                                         // huella de la flota: evita rehacer la lista sin motivo
        int _valToken;                                   // tasaciones en curso (la última manda)
        string _fleetEngsFolder;   // carpeta de contenido para la que se enumeraron los .eng/consists
        int _fleetEngsConsistCount = -1;   // nº de consists con el que se filtró (para recomputar al cargar contenido)
        double _fleetScale = 1.0;    // app_settings.fleet_price_scale (para tasar en el cliente)
        double _fleetRentPct = 0.005;// app_settings.fleet_rental_pct (alquiler por servicio)
        // Tasación de la unidad de CABECERA del consist elegido (para los chips de specs)
        double _selPower, _selSpeed, _selPrice, _selRent; string _selType = "electric"; bool _selAutomotor; int _selCars = 1;
        double _selMass, _selBrake, _selCapacity, _selComfort;   // datos técnicos ampliados (.eng/.wag)

        // Tasación de UNA máquina de tracción del consist elegido (una línea del desglose de compra).
        sealed class FleetUnitQuote
        {
            public string Name, Type;
            public double Kw, Kmh, Mass, Brake, Capacity, Comfort, Price, Rent;
            public bool Automotor, Owned;
            public int Cars;
        }
        // Borrado por el superadmin (servicios y movimientos de banca)
        RoundButton _svcDelBtn, _ledgerDelBtn;
        readonly List<string> _svcIds = new(), _ledgerIds = new();
        readonly List<SvcRow> _svcRows = new();   // datos por servicio (para la ventana de detalle)

        sealed class SvcRow
        {
            public string Id, Date, Driver, Route, Status;
            public double Km, DurationS, Pax, Income, Cost, Net;
            public bool Valid;
        }
        // --- Ajustes (tarifas + saldo superadmin) ---
        RoundedInput _tarIncome, _tarCanon, _tarEnergy, _tarSalary, _tarBalance, _defBalance;
        RoundButton _tarSaveBtn, _tarBalanceBtn, _defBalanceBtn;
        Label _tariffMsg;
        Panel _tarBalanceRow, _defBalanceRow;
        // Economía de flota (global, superadmin): escala de precio + % alquiler + % mantenimiento + capacidad de referencia
        RoundButton _fleetBuyAllBtn;              // compra masiva (solo superadmin)
        bool _buyingAll, _buyAllCancel;
        RoundedInput _fsScale, _fsRentPct, _fsMaintPct, _fsCapBase, _fsFareBase, _fsPaxDemand; RoundButton _fsSaveBtn; Panel _fleetSettingsRow;
        // --- Ranking ---
        StyledTable _rankCompanies, _rankDrivers;
        Panel _rankDriversPanel; Label _rankDriversHeader;   // sección de ranking de socios (se oculta si no hay empresa)

        sealed class EmpMember
        {
            public string UserId, Username, Role;
        }
        static readonly CultureInfo EsEs = CultureInfo.GetCultureInfo("es-ES");

        sealed class EmpCompany
        {
            public string Id, Name, Logo; public double Balance;
            public double IncomePerKm = 8, CanonPerKm = 3, EnergyPerKm = 1.5, SalaryPerService = 40;
            public bool PaEnabled;   // megafonías habilitadas por el superadmin
            public override string ToString() => Name;
        }

        // ============================ Construcción ============================
        Panel BuildEmpresasPage()
        {
            var page = new Panel { BackColor = Theme.Bg };

            _empCfg = BuildEmpConfigView();
            _empAuth = BuildEmpAuthView();
            _empHome = BuildEmpHomeView();
            _empConnectingView = BuildEmpConnectingView();
            foreach (var v in new[] { _empCfg, _empAuth, _empHome, _empConnectingView }) { v.Dock = DockStyle.Fill; v.Visible = false; page.Controls.Add(v); }

            _empEmail.Box.Text = _prefs.EmpresasEmail ?? "";
            if (_prefs.RememberPassword)
            {
                _empRemember.Checked = true;
                var pw = Native.Unprotect(_prefs.EmpresasPasswordEnc);
                if (!string.IsNullOrEmpty(pw)) _empPass.Box.Text = pw;
                // Guardada con el cifrado antiguo (sin entropía): se vuelve a cifrar con el nuevo.
                if (!string.IsNullOrEmpty(pw) && Native.IsLegacyProtected(_prefs.EmpresasPasswordEnc))
                {
                    var enc = Native.Protect(pw);
                    if (enc != null) { _prefs.EmpresasPasswordEnc = enc; try { _prefs.Save(); } catch { } }
                }
                // Si hay credenciales recordadas, marca "conectando" desde ya para NO mostrar el
                // formulario de login ni un instante (el intento real se lanza en InitData → TryAutoLogin).
                if (!string.IsNullOrEmpty(pw) && !string.IsNullOrEmpty(_prefs.EmpresasEmail)) _empConnecting = true;
            }
            if (Supa.HasBuiltIn)
            {
                // Backend fijo: todos usan TU Supabase. Sin pantalla de configuración.
                Supa.Configure(Supa.BuiltInUrl, Supa.BuiltInAnonKey);
            }
            else
            {
                // Solo desarrollo: se permite configurar a mano y se recuerda.
                _empUrl.Box.Text = _prefs.SupabaseUrl ?? "";
                _empKey.Box.Text = _prefs.SupabaseKey ?? "";
                Supa.Configure(_prefs.SupabaseUrl, _prefs.SupabaseKey);
            }

            return page;
        }

        const int EmpFormWidth = 548;   // ancho de los campos en los formularios centrados
        const int EmpSideNoteWidth = 336;   // ancho de envoltura de las notas en la columna izquierda (estrecha)

        // Tarjeta centrada y estrecha para formularios (config / acceso)
        static (Panel outer, TableLayoutPanel form) CenteredForm(int width)
        {
            var outer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true };
            Native.UseDarkScrollBars(outer);   // barra de scroll oscura acorde al tema
            var form = new TableLayoutPanel
            {
                ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Bg, Padding = new Padding(0)
            };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            outer.Controls.Add(form);
            void Recenter() { form.Left = Math.Max(0, (outer.ClientSize.Width - form.Width) / 2); form.Top = 40; }
            outer.Resize += (s, e) => Recenter();
            form.Resize += (s, e) => Recenter();
            outer.HandleCreated += (s, e) => Recenter();
            return (outer, form);
        }

        Panel BuildEmpConfigView()
        {
            var (outer, form) = CenteredForm(560);

            form.Controls.Add(EmpTitle(Tr("Empresas ferroviarias")));
            form.Controls.Add(EmpIntro(Tr("Crea una empresa con otros maquinistas: banca, cánon al AI (administrador de infraestructuras), mantenimiento y el registro de todos los viajes. Necesitas un proyecto gratuito de Supabase (ver la guía «Empresas (backend Supabase)»).")));

            form.Controls.Add(EmpHeader("CONEXIÓN CON EL SERVIDOR (SUPABASE)"));
            form.Controls.Add(EmpFieldLabel(Tr("URL del proyecto (Project URL)")));
            _empUrl = EmpInput("https://xxxxxxxx.supabase.co"); form.Controls.Add(_empUrl);
            form.Controls.Add(EmpFieldLabel(Tr("Clave pública (anon key)")));
            _empKey = EmpInput("eyJhbGciOi…"); form.Controls.Add(_empKey);

            var save = EmpButton(Tr("Guardar y continuar"), primary: true);
            save.Click += (s, e) => SaveEmpConfig();
            form.Controls.Add(save);

            _empCfgMsg = EmpMsg(); form.Controls.Add(_empCfgMsg);
            return outer;
        }

        Panel BuildEmpAuthView()
        {
            var (outer, form) = CenteredForm(520);

            form.Controls.Add(EmpTitle(Tr("Acceso de maquinista")));
            form.Controls.Add(EmpIntro(Tr("Entra con tu nombre de usuario o crea una cuenta nueva. Tu nombre de usuario te identifica en las empresas y debe ser único.")));

            form.Controls.Add(EmpFieldLabel(Tr("Nombre de usuario")));
            _empEmail = EmpInput(Tr("p. ej. David MZP")); form.Controls.Add(_empEmail);
            form.Controls.Add(EmpFieldLabel(Tr("Contraseña")));
            _empPass = EmpInput(""); _empPass.Box.UseSystemPasswordChar = true; form.Controls.Add(_empPass);

            _empRemember = new CheckBox
            {
                Text = Tr("Recordar contraseña"), AutoSize = true, ForeColor = Theme.Text,
                Font = Theme.Font(9.5f), Margin = new Padding(2, 8, 2, 2), Padding = new Padding(0, 2, 0, 0)
            };
            form.Controls.Add(_empRemember);

            var login = EmpButton(Tr("Iniciar sesión"), primary: true);
            login.Click += (s, e) => DoSignIn();
            form.Controls.Add(login);
            var reg = EmpButton(Tr("Registrarse"));
            reg.Click += (s, e) => DoSignUp();   // el nombre de usuario es el propio campo de arriba
            form.Controls.Add(reg);

            var back = EmpLink(Tr("⚙  Cambiar servidor"));
            back.Click += (s, e) => { _empForceCfg = true; RefreshEmpresasView(); };
            back.Visible = !Supa.HasBuiltIn;   // con backend fijo no se puede cambiar de servidor
            form.Controls.Add(back);

            _empAuthMsg = EmpMsg(); form.Controls.Add(_empAuthMsg);
            return outer;
        }

        // Pantalla intermedia "Conectando…" que se muestra durante el auto-login para que NUNCA
        // aparezca el formulario de acceso cuando hay contraseña recordada.
        Panel BuildEmpConnectingView()
        {
            var outer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent };
            var t = new Label { Text = Tr("Conectando con Empresas…"), AutoSize = true, ForeColor = Theme.Text, Font = Theme.Font(15f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6), Anchor = AnchorStyles.None };
            var sub = new Label { Text = Tr("Recuperando tu sesión guardada."), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(10f), Anchor = AnchorStyles.None };
            box.Controls.Add(t); box.Controls.Add(sub);
            outer.Controls.Add(box);
            void Center() { box.Left = Math.Max(0, (outer.ClientSize.Width - box.Width) / 2); box.Top = Math.Max(0, (outer.ClientSize.Height - box.Height) / 2); }
            outer.Resize += (s, e) => Center();
            outer.HandleCreated += (s, e) => Center();
            return outer;
        }

        // Secciones de Empresas (el índice es el que usan ShowSubtab / UpdateSubtabVisibility).
        static readonly string[] SubNames = { "Servicios", "Banca", "Socios", "Ajustes", "Ranking", "Mi perfil", "Revisión", "Usuarios", "Administración", "Flota", "Compra", "Megafonía" };
        static readonly string[] SubGlyphs = { "clock", "bank", "connect", "gear", "activity", "info", "explore", "connect", "globe", "train", "train", "speaker" };
        // Agrupación del menú lateral.
        static readonly (string title, int[] items)[] NavGroupDefs =
        {
            ("OPERACIÓN", new[] { 0, 9, 10 }),        // Servicios · Flota · Compra
            ("FINANZAS", new[] { 1, 4 }),             // Banca · Ranking
            ("EMPRESA", new[] { 2, 3, 11, 5 }),       // Socios · Ajustes · Megafonía · Mi perfil
            ("ADMINISTRACIÓN", new[] { 7, 8 }),       // Usuarios · Administración (el 6, «Revisión», ya no existe)
        };
        const int RailW = 236;          // ancho del menú lateral de Empresas
        ToolTip _empLogoTip;            // «Cambiar logotipo» (solo se muestra a quien puede cambiarlo)
        bool _subtabAutoFallback;       // se abrió Ranking automáticamente (sin empresas aún): al cargar, volver a Servicios
        Label _empSectionTitle;         // título de la sección activa (encima de los KPIs)

        // Vista principal de Empresas: MENÚ LATERAL (empresa + secciones + usuario) | CONTENIDO.
        Panel BuildEmpHomeView()
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var rail = BuildEmpRail();          // crea _empSubtabs (lo necesita el contenido al mostrar la 1.ª sección)
            var main = BuildEmpDashColumn();
            host.Controls.Add(main);            // Fill
            host.Controls.Add(rail);            // Left
            return host;
        }

        Panel BuildEmpRail()
        {
            var rail = new Panel { Dock = DockStyle.Left, Width = RailW, BackColor = Theme.BgSidebar, Padding = new Padding(12, 12, 11, 8) };
            rail.Paint += (s, e) => { using var p = new Pen(Theme.Border); e.Graphics.DrawLine(p, rail.Width - 1, 0, rail.Width - 1, rail.Height); };

            // ---------- Tarjeta de EMPRESA: logo + nombre/rol · selector · crear/unirse ----------
            var card = new Card { Dock = DockStyle.Top, Height = 150, Fill = Theme.Surface, Radius = 10, Padding = new Padding(10) };
            var cc = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Theme.Surface, Margin = new Padding(0) };
            cc.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var idRow = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface, Margin = new Padding(0) };
            idRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            idRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _empLogoPic = new PictureBox { Size = new Size(38, 38), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Surface, Margin = new Padding(0, 1, 10, 0), Anchor = AnchorStyles.Left | AnchorStyles.Top, Cursor = Cursors.Hand };
            _empLogoPic.Click += (s, e) => { if (CanManage() || Supa.IsSuperadmin) ChangeLogo(); };   // el maquinista no puede cambiarlo
            _empLogoTip = new ToolTip(); _empLogoTip.SetToolTip(_empLogoPic, Tr("Cambiar logotipo"));

            var txt = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Surface, Margin = new Padding(0), Anchor = AnchorStyles.Left | AnchorStyles.Top };
            var titleRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(0) };
            _empCoTitle = new Label { AutoSize = true, MaximumSize = new Size(RailW - 118, 0), ForeColor = Theme.Text, Font = Theme.Font(11.5f, FontStyle.Bold), Margin = new Padding(0) };
            _empRenameLink = new Label { Text = "✎", AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(10f), Cursor = Cursors.Hand, Margin = new Padding(4, 2, 0, 0), Visible = false };
            _empRenameLink.MouseEnter += (s, e) => _empRenameLink.ForeColor = Theme.Accent;
            _empRenameLink.MouseLeave += (s, e) => _empRenameLink.ForeColor = Theme.Subtle;
            _empRenameLink.Click += (s, e) => RenameCompany();
            new ToolTip().SetToolTip(_empRenameLink, Tr("Cambiar el nombre de la empresa"));
            // Estrella: marca la empresa favorita, la que saldrá elegida al abrir Empresas.
            _empFavLink = new Label { Text = "☆", AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(11f), Cursor = Cursors.Hand, Margin = new Padding(6, 1, 0, 0), Visible = false };
            _empFavLink.Click += (s, e) => ToggleFavoriteCompany();
            _empFavTip = new ToolTip();
            titleRow.Controls.Add(_empCoTitle); titleRow.Controls.Add(_empRenameLink); titleRow.Controls.Add(_empFavLink);
            _empRoleLbl = new Label { AutoSize = true, MaximumSize = new Size(RailW - 96, 0), ForeColor = Theme.Subtle, Font = Theme.Font(8.5f), Margin = new Padding(0, 2, 0, 0) };
            _logoLink = EmpLink(Tr("Cambiar logotipo")); _logoLink.Visible = false;   // compatibilidad (el logo ya es clicable)
            _logoLink.Click += (s, e) => ChangeLogo();
            txt.Controls.Add(titleRow); txt.Controls.Add(_empRoleLbl);
            idRow.Controls.Add(_empLogoPic, 0, 0); idRow.Controls.Add(txt, 1, 0);
            cc.Controls.Add(idRow);

            _empCoCombo = NewCombo(); _empCoCombo.Dock = DockStyle.None; _empCoCombo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _empCoCombo.DropDownStyle = ComboBoxStyle.DropDownList; _empCoCombo.Margin = new Padding(0, 10, 0, 0);
            _empCoCombo.SelectedIndexChanged += (s, e) => OnCompanySelected();
            cc.Controls.Add(_empCoCombo);

            _empCreateHost = new Panel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 30, BackColor = Theme.Surface, Margin = new Padding(0, 8, 0, 0) };
            cc.Controls.Add(_empCreateHost);
            BuildCreateArea();

            card.Controls.Add(cc);
            cc.SizeChanged += (s, e) => card.Height = cc.Height + card.Padding.Vertical;   // la tarjeta se ajusta a su contenido

            // ---------- Navegación agrupada ----------
            // Rejilla de una columna con una fila POR SECCIÓN: el orden lo fija la fila, no el orden
            // en que estén los controles. Antes era un FlowLayoutPanel y bastaba con que WinForms
            // reordenase la colección (al mostrar y ocultar secciones según el rol) para que los
            // encabezados aparecieran donde no tocaba. Sin barras de desplazamiento: si el contenido
            // no cabe —por ejemplo con un nombre de empresa de dos líneas, que agranda la tarjeta de
            // arriba— se recortan las alturas (AjustarNav).
            var nav = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.BgSidebar, Padding = new Padding(0, 6, 0, 4), Margin = new Padding(0), AutoScroll = false };
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Native.UseDarkScrollBars(nav);   // por si en una ventana muy baja hace falta desplazar
            _navRows = new List<(Control c, int tipo)>();
            _empSubtabs = new RoundButton[SubNames.Length];
            _navGroups = new List<(Label header, int[] items)>();
            for (int gi = 0; gi < NavGroupDefs.Length; gi++)
            {
                var (title, items) = NavGroupDefs[gi];
                var h = new Label
                {
                    Text = Tr(title), AutoSize = false, Height = gi == 0 ? NavHeaderH0 : NavHeaderH, ForeColor = Color.FromArgb(122, 130, 136),
                    Font = Theme.Font(7.75f, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft,
                    Padding = new Padding(8, 0, 0, 3), Margin = new Padding(0), BackColor = Theme.BgSidebar
                };
                h.Dock = DockStyle.Fill;
                nav.RowStyles.Add(new RowStyle(SizeType.Absolute, gi == 0 ? NavHeaderH0 : NavHeaderH));
                nav.Controls.Add(h, 0, _navRows.Count);
                _navRows.Add((h, gi == 0 ? 1 : 2));
                foreach (int idx in items)
                {
                    int k = idx;
                    var b = new RoundButton
                    {
                        Text = Tr(SubNames[idx]), GlyphKind = SubGlyphs[idx], LeftAlign = true, LeftPad = 10, Radius = 8, Height = NavItemH,
                        Margin = new Padding(0, 1, 0, 1),
                        BaseColor = Theme.BgSidebar, HoverColor = Theme.Surface,
                        ActiveColor = Color.FromArgb(40, 70, 44), ActiveTextColor = Theme.AccentHi,
                        TextColor = Color.FromArgb(214, 218, 221), FontSize = 9.75f
                    };
                    b.Click += (s, e) => { _subtabAutoFallback = false; ShowSubtab(k); };   // elección manual: se respeta
                    b.Dock = DockStyle.Fill;
                    _empSubtabs[idx] = b;
                    nav.RowStyles.Add(new RowStyle(SizeType.Absolute, NavItemH));
                    nav.Controls.Add(b, 0, _navRows.Count);
                    _navRows.Add((b, 0));
                }
                _navGroups.Add((h, items));
            }
            nav.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // hueco elástico: todo pegado arriba
            nav.RowCount = _navRows.Count + 1;
            _empNav = nav;
            nav.Resize += (s, e) => AjustarNav();
            AjustarNav();

            // ---------- Usuario (abajo): nombre · ID copiable · editar nombre / cerrar sesión ----------
            var user = new Panel { Dock = DockStyle.Bottom, Height = 148, BackColor = Theme.BgSidebar, Padding = new Padding(4, 10, 0, 0) };
            user.Paint += (s, e) => { using var p = new Pen(Theme.Border); e.Graphics.DrawLine(p, 0, 0, user.Width, 0); };
            // Nombre (+ ★ superadmin) en hasta 2 líneas, sin recortar (el emoji da más alto de línea).
            _empUserLbl = new Label { AutoSize = false, Dock = DockStyle.Top, Height = 48, ForeColor = Theme.Text, Font = Theme.Font(9.75f, FontStyle.Bold) };
            _empIdChip = new Label { AutoSize = false, Dock = DockStyle.Top, Height = 20, AutoEllipsis = true, ForeColor = Theme.Subtle, Font = Theme.Font(8.5f), Cursor = Cursors.Hand };
            _empIdChip.Click += (s, e) => CopyMyId();
            _empIdChip.MouseEnter += (s, e) => _empIdChip.ForeColor = Theme.Accent;
            _empIdChip.MouseLeave += (s, e) => _empIdChip.ForeColor = Theme.Subtle;
            new ToolTip().SetToolTip(_empIdChip, Tr("Clic para copiar tu ID de usuario"));
            // Botones apilados a todo el ancho (así el texto nunca se recorta).
            var ub = new TableLayoutPanel { Dock = DockStyle.Top, Height = 72, ColumnCount = 1, RowCount = 2, BackColor = Theme.BgSidebar, Margin = new Padding(0), Padding = new Padding(0, 6, 0, 0) };
            ub.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ub.RowStyles.Add(new RowStyle(SizeType.Absolute, 33)); ub.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
            var nameBtn = EmpButton(Tr("✎  Editar nombre")); nameBtn.Dock = DockStyle.Fill; nameBtn.Height = 30; nameBtn.FontSize = 9f; nameBtn.Margin = new Padding(0, 0, 0, 3);
            nameBtn.Click += (s, e) => EditMaquinistaName();
            var outBtn = EmpButton(Tr("Cerrar sesión")); outBtn.Dock = DockStyle.Fill; outBtn.Height = 30; outBtn.FontSize = 9f; outBtn.Margin = new Padding(0, 0, 0, 3);
            outBtn.Click += (s, e) => DoSignOut();
            ub.Controls.Add(nameBtn, 0, 0); ub.Controls.Add(outBtn, 0, 1);
            if (!Supa.HasBuiltIn)   // con backend fijo no se puede cambiar de servidor
            {
                var cfg = EmpLink(Tr("⚙  Servidor")); cfg.Dock = DockStyle.Top; cfg.AutoSize = false; cfg.Height = 20; cfg.Margin = new Padding(0);
                cfg.Click += (s, e) => { _empForceCfg = true; RefreshEmpresasView(); };
                user.Controls.Add(cfg); user.Height += 20;
            }
            user.Controls.Add(ub);            // (Dock=Top: el último añadido queda arriba)
            user.Controls.Add(_empIdChip);
            user.Controls.Add(_empUserLbl);

            rail.Controls.Add(nav);    // Fill
            rail.Controls.Add(user);   // Bottom
            rail.Controls.Add(card);   // Top
            return rail;
        }

        // Alturas del menú lateral (se encogen si hace falta para que quepa todo sin barras).
        const int NavItemH = 34, NavItemHMin = 26, NavHeaderH = 30, NavHeaderH0 = 22, NavHeaderHMin = 17;
        TableLayoutPanel _empNav;
        List<(Control c, int tipo)> _navRows;   // tipo: 0 sección · 1 primer encabezado · 2 encabezado

        // Reparte el menú lateral: todos los botones al ancho del panel y, si el contenido no cabe
        // (por ejemplo con un nombre de empresa de dos líneas, que agranda la tarjeta de arriba),
        // se recortan poco a poco las alturas hasta que entra. Así nunca salen barras de
        // desplazamiento sobre el menú ni se descoloca el reparto.
        void AjustarNav()
        {
            var nav = _empNav;
            if (nav == null || nav.IsDisposed || _navRows == null || _navRows.Count == 0) return;
            int disponible = nav.ClientSize.Height - nav.Padding.Vertical;
            // En la PRIMERA carga la lista aún no tiene alto (la página no se ha repartido todavía).
            // Antes se salía aquí y las secciones ocultas seguían ocupando su fila, así que el menú
            // salía con huecos hasta que redimensionabas. Ahora se reparte igual con las alturas
            // normales y solo se deja para después lo que necesita saber cuánto hueco hay.
            bool medible = disponible > 0;

            int botones = 0, cabeceras = 0, primera = 0;
            foreach (var (c, tipo) in _navRows)
            {
                if (!c.Visible) continue;
                if (tipo == 0) botones++; else if (tipo == 1) primera++; else cabeceras++;
            }
            if (botones == 0) return;

            // Si no cabe, se recortan primero los encabezados y después las secciones.
            int hb = NavItemH, hh = NavHeaderH, hh0 = NavHeaderH0;
            int Total() => botones * hb + cabeceras * hh + primera * hh0;
            while (medible && Total() > disponible && (hb > NavItemHMin || hh > NavHeaderHMin))
            {
                if (hh > NavHeaderHMin) { hh--; if (hh0 > NavHeaderHMin) hh0--; }
                else hb--;
            }

            // Ventana muy baja: si ni con las alturas mínimas cabe, se permite desplazar. La rejilla
            // mantiene el orden por filas, así que aquí una barra ya no descoloca nada.
            bool desborda = medible && Total() > disponible;
            if (nav.AutoScroll != desborda) nav.AutoScroll = desborda;
            // Si va a haber barra vertical, se le reserva su ancho: así no aparece también la horizontal.
            var relleno = new Padding(0, 6, desborda ? SystemInformation.VerticalScrollBarWidth : 0, 4);
            if (nav.Padding != relleno) nav.Padding = relleno;
            // La última fila es elástica (empuja el menú hacia arriba); cuando hay que desplazar se
            // anula, porque si no la rejilla nunca se declara más alta que su hueco y no hay barra.
            if (_navRows.Count < nav.RowStyles.Count)
                nav.RowStyles[_navRows.Count] = desborda ? new RowStyle(SizeType.Absolute, 0)
                                                        : new RowStyle(SizeType.Percent, 100);

            nav.SuspendLayout();
            for (int i = 0; i < _navRows.Count && i < nav.RowStyles.Count; i++)
            {
                var (c, tipo) = _navRows[i];
                float alto = !c.Visible ? 0 : tipo == 0 ? hb : tipo == 1 ? hh0 : hh;   // oculta → fila de alto 0
                if (Math.Abs(nav.RowStyles[i].Height - alto) > 0.5f || nav.RowStyles[i].SizeType != SizeType.Absolute)
                    nav.RowStyles[i] = new RowStyle(SizeType.Absolute, alto);
            }
            nav.ResumeLayout();
        }

        // Maneja la selección de empresa desde el desplegable del menú lateral.
        void OnCompanySelected()
        {
            if (_suppressCoSel) return;   // durante la recarga del desplegable no se procesa la selección
            int i = _empCoCombo?.SelectedIndex ?? -1;
            _empSel = (i >= 0 && i < _empCompanies.Count) ? _empCompanies[i] : null;
            _members.Clear();   // (no se reinicia _myRole: LoadMembers lo actualiza; evita rebotes de subpestaña)
            UpdateCompanyDash();
            if (_empSel != null)
            {
                LoadServices(_empSel);
                LoadMembers(_empSel);    // para saber mi rol y habilitar acciones
                FillTariffFields();
                if (_empSubtab == 1) LoadLedger();
                if (_empSubtab == 4) LoadRankings();
                if (_empSubtab == 11) OnMegafoniaShown();   // otra empresa → otros audios
            }
            else { _myRole = null; UpdateRoleUi(); }
        }

        // Acciones de empresa bajo el selector del menú lateral: crear / unirse (por diálogo) y,
        // para el superadmin, eliminar. Alta ABIERTA: cualquiera crea su empresa y queda de gerente.
        void BuildCreateArea()
        {
            if (_empCreateHost == null) return;
            _empCreateHost.Controls.Clear();
            // [Eliminar] lo ven el superadmin y el gerente de la empresa elegida (puede borrar la suya).
            bool su = CanDeleteCompany();
            _createAreaSuper = su;
            // Rejilla a lo ancho de la tarjeta de empresa: [Crear | Unirse] y, si puede, [Eliminar].
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = su ? 2 : 1, BackColor = Theme.Surface, Margin = new Padding(0), Padding = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            if (su) t.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

            var createBtn = EmpButton(Tr("➕ Crear")); createBtn.Dock = DockStyle.Fill; createBtn.Height = 30; createBtn.Margin = new Padding(0, 0, 3, 0); createBtn.FontSize = 8.75f;
            createBtn.Click += (s, e) => CreateCompany();
            t.Controls.Add(createBtn, 0, 0);
            var joinBtn = EmpButton(Tr("🙋 Unirse")); joinBtn.Dock = DockStyle.Fill; joinBtn.Height = 30; joinBtn.Margin = new Padding(3, 0, 0, 0); joinBtn.FontSize = 8.75f;
            joinBtn.Click += (s, e) => RequestJoin();
            t.Controls.Add(joinBtn, 1, 0);

            if (su)
            {
                var del = EmpButton(Tr("Eliminar")); del.Dock = DockStyle.Fill; del.Height = 30; del.FontSize = 8.75f; del.Margin = new Padding(0, 6, 0, 0);
                del.BaseColor = Theme.Surface2; del.HoverColor = Color.FromArgb(150, 60, 60); del.TextColor = Color.FromArgb(229, 115, 115);
                del.Click += (s, e) => DeleteCompany();
                t.Controls.Add(del, 0, 1); t.SetColumnSpan(del, 2);
            }
            _empCreateHost.Height = su ? 66 : 30;
            _empCreateHost.Controls.Add(t);
        }

        Control BuildEmpDashColumn()
        {
            // Contenido de la sección: TÍTULO · KPIs compactos · contenido a todo el alto · mensaje.
            // (La identidad de la empresa y el usuario viven ahora en el menú lateral.)
            var t = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(20, 10, 4, 0) };

            var titleBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Bg };
            _empSectionTitle = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Font(16f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
            titleBar.Controls.Add(_empSectionTitle);

            // KPIs (Tesorería · Socios · Servicios · Km) como 4 tarjetas compactas dibujadas a mano.
            _empStatsCard = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Theme.Bg };
            _empStatsCard.Paint += DrawKpiStrip;
            _empStatsCard.Resize += (s, e) => _empStatsCard.Invalidate();
            _empStatsGap = new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Theme.Bg };
            var gap = _empStatsGap;

            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            _svcPanel = BuildSvcSubpanel();
            _bankPanel = BuildBankSubpanel();
            _memberPanel = BuildMembersSubpanel();
            _tariffPanel = BuildTariffSubpanel();
            _rankPanel = BuildRankSubpanel();
            _soloPanel = BuildProfileSubpanel();
            _reviewPanel = new Panel();   // hueco de la antigua sección «Revisión» (los viajes no válidos ya no se guardan)
            _usersPanel = BuildUsersSubpanel();
            _allCompPanel = BuildAllCompaniesSubpanel();
            _fleetPanel = BuildFleetSubpanel();
            _buyPanel = BuildBuySubpanel();
            _paPanel = BuildPaSubpanel();
            foreach (var pnl in new[] { _svcPanel, _bankPanel, _memberPanel, _tariffPanel, _rankPanel, _soloPanel, _reviewPanel, _usersPanel, _allCompPanel, _fleetPanel, _buyPanel, _paPanel }) { pnl.Dock = DockStyle.Fill; pnl.Visible = false; host.Controls.Add(pnl); }

            _empHomeMsg = EmpMsg(); _empHomeMsg.Dock = DockStyle.Bottom;

            // Docking (el último Top añadido queda arriba): título → KPIs → hueco → contenido; mensaje abajo.
            t.Controls.Add(host);
            t.Controls.Add(gap);
            t.Controls.Add(_empStatsCard);
            t.Controls.Add(titleBar);
            t.Controls.Add(_empHomeMsg);

            ShowSubtab(0);
            return t;
        }

        // Barra de SUB-PESTAÑAS (estilo subrayado) para partir secciones con mucho contenido.
        // onSelect recibe el índice elegido; la primera queda activa.
        FlowLayoutPanel MakeSubTabs(string[] names, Action<int> onSelect)
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(0) };
            bar.Paint += (s, e) => { using var p = new Pen(Theme.Border); e.Graphics.DrawLine(p, 0, bar.Height - 1, bar.Width, bar.Height - 1); };
            var btns = new RoundButton[names.Length];
            using var f = Theme.Font(9.75f, FontStyle.Bold);
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                var b = new RoundButton { Text = Tr(names[i]), Tab = true, Height = 37, FontSize = 9.75f, Margin = new Padding(0, 0, 2, 0) };
                b.Width = TextRenderer.MeasureText(b.Text, f).Width + 34;
                b.Click += (s, e) =>
                {
                    for (int k = 0; k < btns.Length; k++) { btns[k].Active = k == idx; btns[k].Invalidate(); }
                    onSelect?.Invoke(idx);
                };
                btns[i] = b; bar.Controls.Add(b);
            }
            if (btns.Length > 0) btns[0].Active = true;
            return bar;
        }

        // BANCA: sub-pestañas «Resumen» (KPIs + gráficas grandes) y «Movimientos» (tabla a todo el alto).
        Panel BuildBankSubpanel()
        {
            var outer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

            // --- Resumen ---
            var res = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Bg };
            res.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            res.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // KPIs
            res.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // gráficas (rellenan)
            var kpis = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 6) };
            kpis.Controls.Add(KpiTile(Tr("INGRESOS"), out _finIncome, "💰", Theme.Accent));
            kpis.Controls.Add(KpiTile(Tr("GASTOS"), out _finExpense, "💸", ColRed));
            kpis.Controls.Add(KpiTile(Tr("NETO"), out _finNet, "📊", Theme.Accent));
            res.Controls.Add(kpis);
            var charts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Theme.Bg, Margin = new Padding(0) };
            charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            charts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            charts.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            charts.Controls.Add(EmpHeader("DESGLOSE DE COSTES"), 0, 0);
            charts.Controls.Add(EmpHeader("INGRESOS POR MES"), 1, 0);
            // Desglose: anillo con leyenda (lo que importa es la proporción de cada concepto).
            _finBreakdown = new DonutChart { Dock = DockStyle.Fill, Margin = new Padding(2, 2, 6, 2), Format = v => v.ToString("N0", EsEs) + " €", EmptyText = Tr("Sin datos"), CenterCaption = Tr("TOTAL") };
            // Evolución mensual: línea con área y rejilla (se ve la tendencia, no solo el tamaño).
            _finMonthly = new AreaChart { Dock = DockStyle.Fill, Margin = new Padding(6, 2, 2, 2), Format = v => v.ToString("N0", EsEs) + " €", EmptyText = Tr("Sin datos") };
            charts.Controls.Add(_finBreakdown, 0, 1);
            charts.Controls.Add(_finMonthly, 1, 1);
            res.Controls.Add(charts);

            // --- Movimientos ---
            var mov = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg, Visible = false };
            mov.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            mov.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // filtro
            mov.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // tabla
            mov.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // borrar (superadmin)
            _bankList = EmpTable();
            mov.Controls.Add(EmpSearch(_bankList, 340));
            _bankList.SetColumns(
                new StyledTable.Col("FECHA", 108),
                new StyledTable.Col("CONCEPTO", 140),
                new StyledTable.Col("IMPORTE", 140, false, HorizontalAlignment.Right),
                new StyledTable.Col("DESCRIPCIÓN", 0, true));
            mov.Controls.Add(_bankList);
            _ledgerDelBtn = EmpButton(Tr("Eliminar movimiento"));
            _ledgerDelBtn.Width = 220; _ledgerDelBtn.BaseColor = Theme.Surface2; _ledgerDelBtn.HoverColor = Color.FromArgb(150, 60, 60); _ledgerDelBtn.TextColor = RedC; _ledgerDelBtn.Visible = false;
            _ledgerDelBtn.Click += (s, e) => DeleteLedgerRow();
            mov.Controls.Add(_ledgerDelBtn);

            var pages = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            pages.Controls.Add(mov); pages.Controls.Add(res);
            var tabs = MakeSubTabs(new[] { "Resumen", "Movimientos" }, i => { res.Visible = i == 0; mov.Visible = i == 1; });
            outer.Controls.Add(pages); outer.Controls.Add(tabs);

            _bankMsg = EmpMsg(); // no se muestra por espacio, pero se reutiliza
            return outer;
        }

        // SERVICIOS: sub-pestañas de ESTADO (filtran la tabla) + búsqueda a la derecha; tabla a todo el alto.
        Panel BuildSvcSubpanel()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // pestañas + búsqueda
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // tabla
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // acciones

            _empSvcList = EmpTable();

            var top = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 42, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 6) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            // Los viajes fallidos o no válidos ya no se guardan (el servidor los borra al cerrarlos).
            var tabs = MakeSubTabs(new[] { "Todos", "En conducción", "Completados" }, i =>
            {
                _empSvcList.RowFilter = i switch
                {
                    1 => cells => cells.Length > 9 && cells[9].Contains(Tr("En conducción")),
                    2 => cells => cells.Length > 9 && cells[9].Contains(Tr("completado")),
                    _ => (Func<string[], bool>)null
                };
                _empSvcList.Refilter();
            });
            tabs.Dock = DockStyle.Fill; tabs.Margin = new Padding(0);
            var search = EmpSearch(_empSvcList, 260); search.Anchor = AnchorStyles.Right; search.Margin = new Padding(10, 3, 0, 3);
            top.Controls.Add(tabs, 0, 0); top.Controls.Add(search, 1, 0);
            t.Controls.Add(top);

            _empSvcList.SetColumns(
                new StyledTable.Col("", 30),                       // ℹ (abre el detalle)
                new StyledTable.Col("FECHA", 92),
                new StyledTable.Col("MAQUINISTA", 128),
                new StyledTable.Col("RUTA", 0, true),
                new StyledTable.Col("KM", 54, false, HorizontalAlignment.Right),
                new StyledTable.Col("TIEMPO", 76, false, HorizontalAlignment.Right),
                new StyledTable.Col("VIAJEROS", 78, false, HorizontalAlignment.Right),
                new StyledTable.Col("INGRESO", 94, false, HorizontalAlignment.Right),
                new StyledTable.Col("NETO", 100, false, HorizontalAlignment.Right),
                new StyledTable.Col("ESTADO", 124));
            _empSvcList.MouseDoubleClick += (s, e) => OpenServiceDetail(_empSvcList.SelectedRow);
            t.Controls.Add(_empSvcList);

            // Acciones: ver detalle (todos) + borrar servicio (solo superadmin).
            var svcBtns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0) };
            var detailBtn = EmpButton(Tr("Ver detalle del servicio"), primary: true); detailBtn.Width = 230;
            detailBtn.Click += (s, e) => OpenServiceDetail(_empSvcList.SelectedRow);
            svcBtns.Controls.Add(detailBtn);
            _svcDelBtn = EmpButton(Tr("Eliminar servicio"));
            _svcDelBtn.Width = 200; _svcDelBtn.Margin = new Padding(8, 10, 2, 2); _svcDelBtn.BaseColor = Theme.Surface2; _svcDelBtn.HoverColor = Color.FromArgb(150, 60, 60); _svcDelBtn.TextColor = RedC; _svcDelBtn.Visible = false;
            _svcDelBtn.Click += (s, e) => DeleteServiceRow();
            svcBtns.Controls.Add(_svcDelBtn);
            t.Controls.Add(svcBtns);
            return t;
        }

        ComboBox _memberAddRole;   // rol del socio NUEVO (pestaña «Añadir socio»); _memberRole es para cambiar el rol

        // SOCIOS: sub-pestañas «Socios» (tabla + cambiar rol / quitar), «Añadir socio» (formulario)
        // y «Solicitudes de ingreso» (aceptar / rechazar).
        Panel BuildMembersSubpanel()
        {
            var outer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

            // --- Socios ---
            var pMem = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg };
            pMem.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pMem.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // filtro
            pMem.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // tabla
            pMem.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // acciones
            _memberList = EmpTable();
            pMem.Controls.Add(EmpSearch(_memberList, 300));
            _memberList.SetColumns(
                new StyledTable.Col("MAQUINISTA", 0, true),
                new StyledTable.Col("ROL", 160));
            pMem.Controls.Add(_memberList);
            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 4, 0, 0) };
            _memberRole = NewCombo(); _memberRole.Dock = DockStyle.None; _memberRole.Width = 170; _memberRole.DropDownStyle = ComboBoxStyle.DropDownList; _memberRole.Margin = new Padding(0, 16, 8, 2);
            _memberRole.Items.AddRange(new object[] { Tr("Maquinista"), Tr("Gestor"), Tr("Gerente") });
            _memberRole.SelectedIndex = 0;
            _memberRoleBtn = EmpButton(Tr("Cambiar al rol elegido")); _memberRoleBtn.Width = 210; _memberRoleBtn.Margin = new Padding(0, 10, 8, 2);
            _memberRoleBtn.Click += (s, e) => ChangeMemberRole();
            _memberDelBtn = EmpButton(Tr("Quitar seleccionado")); _memberDelBtn.Width = 200; _memberDelBtn.Margin = new Padding(0, 10, 2, 2);
            _memberDelBtn.Click += (s, e) => RemoveMember();
            btns.Controls.Add(_memberRole); btns.Controls.Add(_memberRoleBtn); btns.Controls.Add(_memberDelBtn);
            pMem.Controls.Add(btns);

            // --- Añadir socio ---
            var pAdd = FormPage(false);
            pAdd.Controls.Add(EmpHeader("AÑADIR SOCIO (POR ID O NOMBRE DE USUARIO)"));
            pAdd.Controls.Add(EmpIntro(Tr("Pega el ID de usuario (lo copia el maquinista desde su barra de Empresas) o escribe su nombre de usuario exacto.")));
            _memberEmail = EmpInput(Tr("ID de usuario o nombre de usuario")); pAdd.Controls.Add(_memberEmail);
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(2, 2, 2, 2) };
            _memberAddRole = NewCombo(); _memberAddRole.Dock = DockStyle.None; _memberAddRole.Width = 170; _memberAddRole.DropDownStyle = ComboBoxStyle.DropDownList; _memberAddRole.Margin = new Padding(0, 16, 8, 2);
            _memberAddRole.Items.AddRange(new object[] { Tr("Maquinista"), Tr("Gestor"), Tr("Gerente") });
            _memberAddRole.SelectedIndex = 0;
            _memberAddBtn = EmpButton(Tr("Añadir socio"), primary: true); _memberAddBtn.Width = 170; _memberAddBtn.Margin = new Padding(0, 10, 2, 2);
            _memberAddBtn.Click += (s, e) => AddMember();
            row.Controls.Add(_memberAddRole); row.Controls.Add(_memberAddBtn);
            pAdd.Controls.Add(row);

            // --- Solicitudes de ingreso ---
            var pJoin = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Bg, Visible = false };
            pJoin.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pJoin.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // lista
            pJoin.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // aceptar / rechazar
            _joinList = EmpTable(); _joinList.Dock = DockStyle.Fill; _joinList.Margin = new Padding(2);
            _joinList.SetColumns(
                new StyledTable.Col("MAQUINISTA", 150),
                new StyledTable.Col("MENSAJE", 0, true),
                new StyledTable.Col("FECHA", 92));
            pJoin.Controls.Add(_joinList);
            var jbtns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(2, 0, 2, 0) };
            var jAcc = EmpButton(Tr("Aceptar"), primary: true); jAcc.Width = 150; jAcc.Margin = new Padding(0, 10, 8, 2); jAcc.Click += (s, e) => ApproveJoin();
            var jRej = EmpButton(Tr("Rechazar")); jRej.Width = 150; jRej.Margin = new Padding(0, 10, 2, 2); jRej.Click += (s, e) => RejectJoin();
            jbtns.Controls.Add(jAcc); jbtns.Controls.Add(jRej);
            pJoin.Controls.Add(jbtns);

            var pages = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            pages.Controls.Add(pJoin); pages.Controls.Add(pAdd); pages.Controls.Add(pMem);
            var tabs = MakeSubTabs(new[] { "Socios", "Añadir socio", "Solicitudes de ingreso" },
                i => { pMem.Visible = i == 0; pAdd.Visible = i == 1; pJoin.Visible = i == 2; });
            _memberMsg = EmpMsg(); _memberMsg.Dock = DockStyle.Bottom;
            outer.Controls.Add(pages); outer.Controls.Add(tabs); outer.Controls.Add(_memberMsg);
            return outer;
        }

        // Página de formulario desplazable (columna AutoSize), para las sub-pestañas de Ajustes.
        const int FormPageW = 600;   // ancho de los formularios (los campos no se estiran a toda la pantalla)
        static TableLayoutPanel FormPage(bool visible)
        {
            var p = new TableLayoutPanel { Dock = DockStyle.Left, Width = FormPageW, ColumnCount = 1, BackColor = Theme.Bg, AutoScroll = true, Visible = visible };
            Native.UseDarkScrollBars(p);
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return p;
        }

        // AJUSTES (superadmin): sub-pestañas «Tarifas», «Saldos» y «Economía de flota».
        Panel BuildTariffSubpanel()
        {
            var outer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

            // --- Tarifas ---
            var pTar = FormPage(true);
            pTar.Controls.Add(EmpHeader("TARIFAS GLOBALES DE LOS SERVICIOS (€) — SOLO SUPERADMIN"));
            pTar.Controls.Add(EmpFieldLabel(Tr("Ingreso por km")));
            _tarIncome = EmpInput("8"); _tarIncome.Width = 240; pTar.Controls.Add(_tarIncome);
            pTar.Controls.Add(EmpFieldLabel(Tr("Cánon AI (administrador de infraestructuras) por km")));
            _tarCanon = EmpInput("3"); _tarCanon.Width = 240; pTar.Controls.Add(_tarCanon);
            pTar.Controls.Add(EmpFieldLabel(Tr("Energía / combustible por km")));
            _tarEnergy = EmpInput("1.5"); _tarEnergy.Width = 240; pTar.Controls.Add(_tarEnergy);
            pTar.Controls.Add(EmpFieldLabel(Tr("Salario del maquinista por servicio")));
            _tarSalary = EmpInput("40"); _tarSalary.Width = 240; pTar.Controls.Add(_tarSalary);
            _tarSaveBtn = EmpButton(Tr("Guardar tarifas"), primary: true); _tarSaveBtn.Width = 200;
            _tarSaveBtn.Click += (s, e) => SaveTariffs();
            pTar.Controls.Add(_tarSaveBtn);

            // --- Saldos (empresa + saldo inicial global) ---
            var pBal = FormPage(false);
            _tarBalanceRow = new Panel { AutoSize = true, BackColor = Theme.Bg, Margin = new Padding(0) };
            var brInner = new TableLayoutPanel { AutoSize = true, ColumnCount = 1 };
            brInner.Controls.Add(EmpHeader("SALDO DE LA EMPRESA (SUPERADMIN)"));
            brInner.Controls.Add(EmpFieldLabel(Tr("Fijar saldo (€) — solo el superadministrador")));
            _tarBalance = EmpInput("0"); _tarBalance.Width = 240; brInner.Controls.Add(_tarBalance);
            _tarBalanceBtn = EmpButton(Tr("Fijar saldo"), primary: true); _tarBalanceBtn.Width = 200;
            _tarBalanceBtn.Click += (s, e) => AdminSetBalance();
            brInner.Controls.Add(_tarBalanceBtn);
            _tarBalanceRow.Controls.Add(brInner);
            pBal.Controls.Add(_tarBalanceRow);
            _defBalanceRow = new Panel { AutoSize = true, BackColor = Theme.Bg, Margin = new Padding(0, 12, 0, 0) };
            var dbInner = new TableLayoutPanel { AutoSize = true, ColumnCount = 1 };
            dbInner.Controls.Add(EmpHeader("SALDO INICIAL DE EMPRESAS NUEVAS (GLOBAL, SUPERADMIN)"));
            dbInner.Controls.Add(EmpFieldLabel(Tr("Saldo con el que nace toda empresa nueva (€)")));
            _defBalance = EmpInput("0"); _defBalance.Width = 240; dbInner.Controls.Add(_defBalance);
            _defBalanceBtn = EmpButton(Tr("Guardar saldo inicial"), primary: true); _defBalanceBtn.Width = 220;
            _defBalanceBtn.Click += (s, e) => SaveDefaultBalance();
            dbInner.Controls.Add(_defBalanceBtn);
            _defBalanceRow.Controls.Add(dbInner);
            pBal.Controls.Add(_defBalanceRow);

            // --- Economía de flota (global) ---
            var pFs = FormPage(false);
            _fleetSettingsRow = new Panel { AutoSize = true, BackColor = Theme.Bg, Margin = new Padding(0) };
            var fsInner = new TableLayoutPanel { AutoSize = true, ColumnCount = 1 };
            fsInner.Controls.Add(EmpHeader("ECONOMÍA DE FLOTA (GLOBAL, SUPERADMIN)"));
            fsInner.Controls.Add(EmpFieldLabel(Tr("Escala de precio (1 = precios reales en millones)")));
            _fsScale = EmpInput("1"); _fsScale.Width = 240; fsInner.Controls.Add(_fsScale);
            fsInner.Controls.Add(EmpFieldLabel(Tr("Alquiler por servicio (fracción del valor, p. ej. 0,00008)")));
            _fsRentPct = EmpInput("0,00008"); _fsRentPct.Width = 240; fsInner.Controls.Add(_fsRentPct);
            fsInner.Controls.Add(EmpFieldLabel(Tr("Mantenimiento por taller (fracción del valor, p. ej. 0,0015)")));
            _fsMaintPct = EmpInput("0,0015"); _fsMaintPct.Width = 240; fsInner.Controls.Add(_fsMaintPct);
            fsInner.Controls.Add(EmpFieldLabel(Tr("Capacidad de referencia (plazas para el ingreso base)")));
            _fsCapBase = EmpInput("300"); _fsCapBase.Width = 240; fsInner.Controls.Add(_fsCapBase);
            fsInner.Controls.Add(EmpFieldLabel(Tr("Billete base (€) — viajeros: billete = base + confort·0,05 + vel·0,02")));
            _fsFareBase = EmpInput("1,5"); _fsFareBase.Width = 240; fsInner.Controls.Add(_fsFareBase);
            _fsSaveBtn = EmpButton(Tr("Guardar economía de flota"), primary: true); _fsSaveBtn.Width = 240;
            _fsSaveBtn.Click += (s, e) => SaveFleetSettings();
            fsInner.Controls.Add(_fsSaveBtn);
            _fleetSettingsRow.Controls.Add(fsInner);
            pFs.Controls.Add(_fleetSettingsRow);

            // --- Viajeros (modelo de demanda, global) ---
            // Dos columnas para que todo quepa sin barras de desplazamiento:
            // izquierda = demanda (base, tipo de servicio, tamaño de estación); derecha = otros factores y guardar.
            var pPax = FormPage(false);
            pPax.Width = 1420; pPax.ColumnCount = 2;
            pPax.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var L = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, BackColor = Theme.Bg, Margin = new Padding(0) };
            var R = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, BackColor = Theme.Bg, Margin = new Padding(48, 0, 0, 0) };
            pPax.Controls.Add(L, 0, 0); pPax.Controls.Add(R, 1, 0);
            L.Controls.Add(EmpHeader("MODELO DE VIAJEROS (GLOBAL, SUPERADMIN)"));
            L.Controls.Add(EmpNote(Tr("Viajeros que esperan = demanda base × tamaño de estación × tipo de servicio × hora × estación del año × clima × variación aleatoria.")));
            L.Controls.Add(EmpFieldLabel(Tr("Demanda base de viajeros por estación")));
            _fsPaxDemand = EmpInput("60"); _fsPaxDemand.Anchor = AnchorStyles.Left; _fsPaxDemand.Width = 200; L.Controls.Add(_fsPaxDemand);
            var profCols = new[] { "Cercanías", "Media", "Larga", "Alta Vel." };
            var profGrid = ParamGrid(profCols, new[]
            {
                ("Demanda relativa (× base)", _pmDemand = new RoundedInput[4]),
                ("Fracción que baja en cada parada", _pmAlight = new RoundedInput[4]),
            });
            L.Controls.Add(EmpHeader("POR TIPO DE SERVICIO"));
            L.Controls.Add(profGrid);
            L.Controls.Add(EmpHeader("POR TAMAÑO DE ESTACIÓN (Nº DE ANDENES)"));
            L.Controls.Add(ParamGrid(new[] { "1", "2–3", "4–7", "8+" }, new[] { ("Peso de la estación (× base)", _pmStation = new RoundedInput[4]) }));

            R.Controls.Add(EmpHeader("OTROS FACTORES"));
            R.Controls.Add(EmpNote(Tr("Bajada extra: multiplica la fracción que baja en estaciones de 4 o más andenes. Variación: ± aleatorio por estación y visita. Reembarque: distancia a la que hay que alejarse para volver a atender la misma estación.")));
            R.Controls.Add(ParamGrid(new[] { "Bajada extra (×)", "Variación (±%)", "Reembarque (m)" },
                new[] { ("Paradas", _pmMisc = new RoundedInput[3]) }, 90, 132));
            R.Controls.Add(EmpHeader("CLIMA Y ESTACIÓN DEL AÑO"));
            R.Controls.Add(EmpNote(Tr("Multiplica la demanda cuando la actividad tiene nieve o lluvia (la lluvia solo afecta a Cercanías y Media Distancia).")));
            R.Controls.Add(ParamGrid(new[] { "Nieve (×)", "Lluvia Cerc. (×)", "Lluvia Media (×)" },
                new[] { ("Clima", _pmWeather = new RoundedInput[3]) }, 90, 132));
            _pmUseSeason = ParamCheck(Tr("Aplicar la estación del año de la actividad"));
            _pmUseWeather = ParamCheck(Tr("Aplicar el clima de la actividad"));
            R.Controls.Add(_pmUseSeason); R.Controls.Add(_pmUseWeather);
            R.Controls.Add(ParamButtons(Tr("Guardar modelo de viajeros"), () => SavePaxModel(true)));

            // --- Clasificación (tipo de servicio) ---
            var pCls = FormPage(false); pCls.Width = 720;
            pCls.Controls.Add(EmpHeader("CLASIFICACIÓN DEL TIPO DE SERVICIO (GLOBAL, SUPERADMIN)"));
            pCls.Controls.Add(EmpNote(Tr("Se clasifica por la velocidad máxima de la composición. En la banda media, si la densidad de viajeros es alta (muchas plazas de pie), se considera Cercanías.")));
            pCls.Controls.Add(ParamGrid(new[] { "Alta Vel. ≥ km/h", "Larga ≥ km/h", "Media ≥ km/h", "Media < pax/m²" }, new[]
            {
                ("Umbrales", _pmClass = new RoundedInput[4]),
            }));
            pCls.Controls.Add(EmpNote(Tr("Por debajo del umbral de Media (o con densidad igual o superior) el servicio es Cercanías.")));
            pCls.Controls.Add(EmpHeader("LOCOMOTORA / AUTOMOTOR"));
            pCls.Controls.Add(EmpNote(Tr("Formación fija: los coches que van SIEMPRE juntos con la máquina (acople rígido o unidades que nunca circulan por separado) se compran y se tasan como un solo vehículo, sumando su masa y su freno. Una máquina suelta se tasa por su potencia y velocidad.")));
            pCls.Controls.Add(ParamButtons(Tr("Guardar clasificación"), () => SavePaxModel(false)));

            // --- Actualizaciones (publicar nueva versión de SelectOR) ---
            var pUpd = FormPage(false); pUpd.Width = 720;
            pUpd.Controls.Add(EmpHeader("PUBLICAR ACTUALIZACIÓN (SUPERADMIN)"));
            _updInfo = new Label { AutoSize = true, ForeColor = Theme.Text, Font = Theme.Font(10f, FontStyle.Bold), Margin = new Padding(2, 4, 2, 6) };
            pUpd.Controls.Add(_updInfo);
            pUpd.Controls.Add(EmpNote(Tr("Cómo publicar: 1) sube la versión en SelectOR.csproj (p. ej. 1.2.1), 2) compila, 3) abre esa nueva versión, 4) escribe las novedades y pulsa Publicar. Se empaquetan los archivos de SelectOR que se están ejecutando y todos los usuarios recibirán el aviso para instalarla.")));
            pUpd.Controls.Add(EmpFieldLabel(Tr("Novedades (las verán los usuarios en el aviso)")));
            _updNotes = new TextBox
            {
                Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, AcceptsReturn = true,
                BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.Font(9.5f),
                Width = 640, Height = 150, Margin = new Padding(2, 2, 2, 4)
            };
            Native.UseDarkScrollBars(_updNotes);
            pUpd.Controls.Add(_updNotes);
            _updPublishBtn = EmpButton(Tr("Publicar esta versión"), primary: true); _updPublishBtn.Width = 300;
            _updPublishBtn.Click += (s, e) => PublishUpdate();
            pUpd.Controls.Add(_updPublishBtn);

            var pages = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            pages.Controls.Add(pUpd); pages.Controls.Add(pCls); pages.Controls.Add(pPax); pages.Controls.Add(pFs); pages.Controls.Add(pBal); pages.Controls.Add(pTar);
            var tabs = MakeSubTabs(new[] { "Tarifas", "Saldos", "Economía de flota", "Viajeros", "Clasificación", "Actualizaciones" },
                i =>
                {
                    pTar.Visible = i == 0; pBal.Visible = i == 1; pFs.Visible = i == 2; pPax.Visible = i == 3; pCls.Visible = i == 4; pUpd.Visible = i == 5;
                    if (i == 5) RefreshUpdateInfo();
                });
            FillPaxModelInputs(PaxCfg);
            _tariffMsg = EmpMsg(); _tariffMsg.Dock = DockStyle.Bottom;
            outer.Controls.Add(pages); outer.Controls.Add(tabs); outer.Controls.Add(_tariffMsg);
            return outer;
        }

        // Reconstruido con DOCKING simple (como Servicios, que sí pinta) en vez de un
        // TableLayoutPanel de dos filas Percent (con el que el ListView owner-draw no
        // repintaba sus filas). Arriba: ranking público (Fill). Abajo: ranking de
        // maquinistas de la empresa (panel Bottom, se oculta si no perteneces a ninguna).
        Panel BuildRankSubpanel()
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

            // --- Sección inferior: maquinistas de la empresa ---
            _rankDriversPanel = new Panel { Dock = DockStyle.Bottom, Height = 300, BackColor = Theme.Bg };
            _rankDrivers = EmpTable();
            _rankDrivers.SetColumns(
                new StyledTable.Col("#", 44),
                new StyledTable.Col("MAQUINISTA", 0, true),
                new StyledTable.Col("SERV.", 70, false, HorizontalAlignment.Right),
                new StyledTable.Col("KM", 100, false, HorizontalAlignment.Right),
                new StyledTable.Col("NETO", 130, false, HorizontalAlignment.Right));
            _rankDriversHeader = EmpHeader("RANKING DE MAQUINISTAS DE ESTA EMPRESA"); _rankDriversHeader.Dock = DockStyle.Top;
            _rankDriversPanel.Controls.Add(_rankDrivers);         // Fill (primero → relleno interior)
            _rankDriversPanel.Controls.Add(_rankDriversHeader);   // Top (después → franja superior)

            // --- Sección superior: ranking público de empresas (rellena el resto) ---
            _rankCompanies = EmpTable();
            _rankCompanies.ImageColumn = 1;   // logotipo junto al nombre de la empresa
            _rankCompanies.SetColumns(
                new StyledTable.Col("#", 44),
                new StyledTable.Col("EMPRESA", 0, true),
                new StyledTable.Col("KM", 100, false, HorizontalAlignment.Right),
                new StyledTable.Col("SERV.", 70, false, HorizontalAlignment.Right),
                new StyledTable.Col("SALDO", 130, false, HorizontalAlignment.Right));
            var topHeader = EmpHeader("RANKING PÚBLICO DE EMPRESAS (POR KM)"); topHeader.Dock = DockStyle.Top;

            // Orden de docking (WinForms procesa en orden inverso de la colección):
            //   companies(Fill) → topHeader(Top) → driversPanel(Bottom)
            host.Controls.Add(_rankCompanies);
            host.Controls.Add(topHeader);
            host.Controls.Add(_rankDriversPanel);
            return host;
        }

        Panel BuildProfileSubpanel()
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true };
            Native.UseDarkScrollBars(host);   // barra de scroll oscura acorde al tema
            var t = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 8, BackColor = Theme.Bg, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // tarjeta de rango
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // KPIs
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // header insignias
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // insignias
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // header trenes
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 168));  // gráfica trenes
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // header trayectos
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));  // gráfica trayectos (anillo)

            // Anchura del contenido = ancho del host (menos margen para la barra de scroll)
            void FitWidth() { t.Width = Math.Max(10, host.ClientSize.Width - 4); }
            host.Resize += (s, e) => FitWidth();
            host.HandleCreated += (s, e) => FitWidth();
            host.VisibleChanged += (s, e) => { if (host.Visible) FitWidth(); };   // al abrir la pestaña

            // Tarjeta de RANGO
            var rankCard = new Card { Dock = DockStyle.Top, Height = 92, Fill = Theme.Surface, Radius = 12, Margin = new Padding(0, 0, 0, 6), Padding = new Padding(16, 10, 16, 12) };
            var rankInner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent };
            rankInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rankInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            rankInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var rankTop = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
            var rankCap = new Label { Text = Tr("TU RANGO"), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(8f, FontStyle.Bold), Margin = new Padding(0, 6, 10, 0) };
            _rankName = new Label { Text = "—", AutoSize = true, ForeColor = Theme.Accent, Font = Theme.Font(15f, FontStyle.Bold), Margin = new Padding(0) };
            // Escalón dentro de la carrera (p. ej. «5 / 18») y porcentaje hasta el siguiente.
            _rankStep = new Label { Text = "", AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(9f, FontStyle.Bold), Margin = new Padding(12, 6, 0, 0) };
            rankTop.Controls.Add(rankCap); rankTop.Controls.Add(_rankName); rankTop.Controls.Add(_rankStep);
            _rankTrack = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2) };
            _rankTrack.Paint += (s, e) =>
            {
                var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var rr = new Rectangle(0, 1, Math.Max(1, _rankTrack.ClientSize.Width - 1), 8);
                using (var p = Theme.Round(rr, 4)) using (var b = new SolidBrush(Theme.Surface2)) g.FillPath(b, p);
                int fw = (int)(rr.Width * Math.Max(0, Math.Min(1, _rankPct)));
                if (fw > 2) { var fr = new Rectangle(rr.X, rr.Y, fw, rr.Height); using var p2 = Theme.Round(fr, 4); using var b2 = new System.Drawing.Drawing2D.LinearGradientBrush(fr, Theme.AccentHi, Theme.Accent, System.Drawing.Drawing2D.LinearGradientMode.Horizontal); g.FillPath(b2, p2); }
            };
            _rankNext = new Label { Text = "", AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(8.5f), Margin = new Padding(0, 2, 0, 0) };
            rankInner.Controls.Add(rankTop, 0, 0); rankInner.Controls.Add(_rankTrack, 0, 1); rankInner.Controls.Add(_rankNext, 0, 2);
            rankCard.Controls.Add(rankInner);
            t.Controls.Add(rankCard);

            // KPIs
            var kpis = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 4) };
            kpis.Controls.Add(KpiTile(Tr("KM TOTALES"), out _profKmVal, "📏", Theme.Accent));
            kpis.Controls.Add(KpiTile(Tr("VIAJES"), out _profTripsVal, "🚂", ColBlue));
            kpis.Controls.Add(KpiTile(Tr("TIEMPO TOTAL"), out _profTimeVal, "⏱", ColViolet));
            kpis.Controls.Add(KpiTile(Tr("VEL. MEDIA"), out _profSpeedVal, "🚄", ColOrange));
            kpis.Controls.Add(KpiTile(Tr("VIAJEROS"), out _profPaxVal, "🧍", ColTeal));
            t.Controls.Add(kpis);

            t.Controls.Add(EmpHeader("INSIGNIAS"));
            _badges = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 6) };
            t.Controls.Add(_badges);

            t.Controls.Add(EmpHeader("TRENES MÁS UTILIZADOS"));
            _profTrains = new BarChart { Dock = DockStyle.Fill, Margin = new Padding(2, 2, 2, 6), Format = v => string.Format(Tr("{0} viajes"), v.ToString("0")) };
            t.Controls.Add(_profTrains);

            t.Controls.Add(EmpHeader("TRAYECTOS MÁS REPETIDOS"));
            // Los trayectos se ven mejor en anillo: lo interesante es el REPARTO de tus viajes.
            _profRoutes = new DonutChart { Dock = DockStyle.Fill, Margin = new Padding(2, 2, 2, 6), Format = v => $"{v:0}", CenterCaption = Tr("VIAJES") };
            t.Controls.Add(_profRoutes);

            host.Controls.Add(t);
            return host;
        }

        // Paleta de acentos para KPIs / insignias.
        static readonly Color ColBlue = Color.FromArgb(96, 165, 250);
        static readonly Color ColViolet = Color.FromArgb(167, 139, 250);
        static readonly Color ColOrange = Color.FromArgb(251, 146, 60);
        static readonly Color ColRed = Color.FromArgb(229, 115, 115);
        static readonly Color ColGold = Color.FromArgb(245, 197, 66);
        static readonly Color ColTeal = Color.FromArgb(45, 212, 191);

        // Tarjeta KPI: pastilla de icono a color + título + valor grande, con acento propio.
        // Tarjeta de estadística: franja de color a la izquierda, rótulo pequeño con su icono y el
        // valor grande debajo. Misma pieza en Banca y en Mi perfil, para que las dos secciones se
        // lean igual.
        Card KpiTile(string caption, out Label value, string icon, Color accent)
        {
            var card = new Card
            {
                Fill = Blend(Theme.Surface, Theme.Bg, 0.15f),
                BorderColor = Blend(Theme.Surface, Theme.Bg, 0.55f),
                Radius = 14, Width = Theme.Px(196), Height = Theme.Px(92),
                Margin = new Padding(0, 0, Theme.Px(10), Theme.Px(10)), Padding = new Padding(0)
            };
            var stripe = new Panel { Dock = DockStyle.Left, Width = Theme.Px(4), BackColor = accent, Margin = new Padding(0) };
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Padding = new Padding(Theme.Px(13), Theme.Px(9), Theme.Px(12), Theme.Px(9)) };
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var top = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
            top.Controls.Add(IconDot(icon, accent, Theme.Px(22)));
            top.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(8f, FontStyle.Bold), Margin = new Padding(Theme.Px(8), Theme.Px(5), 0, 0) });
            value = new Label { Text = "—", Dock = DockStyle.Fill, ForeColor = accent, Font = Theme.Font(19f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, Theme.Px(2), 0, 0) };
            body.Controls.Add(top, 0, 0); body.Controls.Add(value, 0, 1);
            card.Controls.Add(body);
            card.Controls.Add(stripe);
            return card;
        }

        // Fija el valor de un KPI ajustando el tamaño de fuente para que SIEMPRE quepa entero
        // (los importes grandes ya no se cortan).
        void SetKpi(Label v, string text)
        {
            if (v == null) return;
            v.Text = text;
            int avail = v.Width > 20 ? v.Width - 2 : 158;
            float size = 19f;
            while (size > 10.5f && TextRenderer.MeasureText(text, Theme.Font(size, FontStyle.Bold)).Width > avail) size -= 0.5f;
            if (Math.Abs(v.Font.Size - size) > 0.05f) v.Font = Theme.Font(size, FontStyle.Bold);
        }

        // Pastilla redondeada de color con un emoji/icono centrado (blanco).
        static Panel IconDot(string icon, Color color, int size)
        {
            var pan = new Panel { Width = size, Height = size, BackColor = Color.Transparent, Margin = new Padding(0) };
            pan.Paint += (s, e) =>
            {
                var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var p = Theme.Round(new Rectangle(0, 0, size, size), size / 3))
                using (var b = new SolidBrush(color)) g.FillPath(b, p);
                using var f = new Font("Segoe UI Emoji", size * 0.46f * Theme.DpiComp);
                TextRenderer.DrawText(g, icon, f, new Rectangle(0, 0, size, size), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            };
            return pan;
        }

        void ShowSubtab(int i)
        {
            _empSubtab = i;
            for (int k = 0; k < _empSubtabs.Length; k++) { if (_empSubtabs[k] == null) continue; _empSubtabs[k].Active = k == i; _empSubtabs[k].Invalidate(); }   // el 6 («Revisión») ya no tiene botón
            if (_empSectionTitle != null && i >= 0 && i < SubNames.Length) _empSectionTitle.Text = Tr(SubNames[i]);
            _svcPanel.Visible = i == 0;
            _bankPanel.Visible = i == 1;
            _memberPanel.Visible = i == 2;
            _tariffPanel.Visible = i == 3;
            _rankPanel.Visible = i == 4;
            _soloPanel.Visible = i == 5;
            _reviewPanel.Visible = i == 6;
            _usersPanel.Visible = i == 7;
            _allCompPanel.Visible = i == 8;
            _fleetPanel.Visible = i == 9;
            _buyPanel.Visible = i == 10;
            if (_paPanel != null) _paPanel.Visible = i == 11;
            // Cada subpestaña recarga sus datos al abrirse (ya no hay botón "Actualizar").
            if (i == 0 && _empSel != null) LoadServices(_empSel);
            if (i == 1) LoadLedger();
            if (i == 2 && _empSel != null) LoadMembers(_empSel);
            if (i == 3) { FillTariffFields(); LoadDefaultBalance(); LoadFleetSettings(); }
            if (i == 4) LoadRankings();
            if (i == 5) LoadProfile();
            if (i == 7) LoadUsers();
            if (i == 8) LoadAllCompanies();
            if (i == 9 || i == 10) LoadFleet();   // Flota y Compra comparten los mismos datos (vehicles)
            if (i == 10) LoadPurchaseRequests();
            if (i == 11) OnMegafoniaShown();
            UpdateCompanyKpis();                  // la tira de la empresa se muestra u oculta según la sección
        }

        // ============================ Lógica ============================
        void OnEmpresasShown()
        {
            RefreshEmpresasView();
            AjustarNav();   // al hacerse visible ya se conoce el alto real del menú lateral
            if (Supa.IsLoggedIn && !_empLoaded) LoadCompanies();
            else if (!Supa.IsLoggedIn) TryAutoLogin();
        }

        // Intenta iniciar sesión automáticamente con la contraseña recordada, en segundo plano.
        // Se llama al arrancar (InitData) para que, al abrir Empresas, ya haya sesión y no parpadee el login.
        void TryAutoLogin()
        {
            if (_empAutoTried || Supa.IsLoggedIn) return;
            if (!_prefs.RememberPassword || string.IsNullOrEmpty(_prefs.EmpresasEmail)) return;
            string pw = _empPass?.Box.Text;
            if (string.IsNullOrEmpty(pw)) pw = Native.Unprotect(_prefs.EmpresasPasswordEnc);
            if (string.IsNullOrEmpty(pw)) return;
            _empAutoTried = true;
            _empConnecting = true;
            if (_empEmail != null) _empEmail.Box.Text = _prefs.EmpresasEmail;
            if (_empPass != null) _empPass.Box.Text = pw;
            RefreshEmpresasView();   // muestra "Conectando…" si Empresas está a la vista
            DoSignIn();
        }

        void RefreshEmpresasView()
        {
            bool cfg = !Supa.HasBuiltIn && (!Supa.IsConfigured || _empForceCfg);
            bool connecting = !cfg && !Supa.IsLoggedIn && _empConnecting;
            bool auth = !cfg && !connecting && !Supa.IsLoggedIn;
            bool home = !cfg && Supa.IsLoggedIn;
            _empCfg.Visible = cfg; _empAuth.Visible = auth; _empHome.Visible = home;
            if (_empConnectingView != null) _empConnectingView.Visible = connecting;
            // La barra "de servicio" aparece en Empresas cuando hay sesión (el login puede ser
            // asíncrono y completarse después de ShowPage, que la deja oculta).
            UpdateDutyHostVisible();
            if (home)
            {
                _empUserLbl.Text = UserHeaderText();
                if (_createAreaSuper != CanDeleteCompany()) BuildCreateArea();   // adapta la zona de crear empresa al rol
                UpdateCompanyDash(); UpdateDutyUi(); UpdateRoleUi();
                if (string.IsNullOrWhiteSpace(Supa.Username)) LoadMyUsername();
                StartRealtime();   // escucha de cambios en vivo (una sola vez)
            }
        }

        void SaveEmpConfig()
        {
            var url = _empUrl.Box.Text.Trim();
            var key = _empKey.Box.Text.Trim();
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { Msg(_empCfgMsg, Tr("La URL debe empezar por https://"), true); return; }
            if (key.Length < 20) { Msg(_empCfgMsg, Tr("Pega la clave anon completa."), true); return; }
            Supa.Configure(url, key);
            _prefs.SupabaseUrl = url; _prefs.SupabaseKey = key; _prefs.Save();
            _empForceCfg = false; Msg(_empCfgMsg, "", false);
            RefreshEmpresasView();
        }

        // Acceso por NOMBRE DE USUARIO. Por debajo se usa un correo sintético determinista para
        // Supabase; si el texto ya es un correo (cuentas antiguas / superadmin) se usa tal cual
        // (ruta de compatibilidad). El UUID de Supabase sigue siendo el identificador de usuario.
        const string SyntheticEmailDomain = "selector.local";
        static string UsernameToEmail(string input)
        {
            input = (input ?? "").Trim();
            if (input.Contains("@")) return input;   // ya es un correo → compatibilidad
            var sb = new System.Text.StringBuilder();
            foreach (var ch in input.ToLowerInvariant())
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
            var slug = sb.ToString();
            return slug.Length == 0 ? null : slug + "@" + SyntheticEmailDomain;
        }

        async void DoSignIn()
        {
            var input = _empEmail.Box.Text.Trim(); var pass = _empPass.Box.Text;
            if (input.Length == 0 || pass.Length == 0) { _empConnecting = false; Msg(_empAuthMsg, Tr("Introduce tu nombre de usuario y contraseña."), true); RefreshEmpresasView(); return; }
            string email = UsernameToEmail(input);
            if (email == null) { _empConnecting = false; Msg(_empAuthMsg, Tr("Nombre de usuario no válido."), true); RefreshEmpresasView(); return; }
            Msg(_empAuthMsg, Tr("Conectando…"), false);
            var err = await Supa.SignInAsync(email, pass);
            _empConnecting = false;   // termine como termine, deja de mostrar "Conectando…"
            if (err != null) { Msg(_empAuthMsg, Tr("No se pudo iniciar sesión: ") + err, true); RefreshEmpresasView(); return; }
            SaveRememberedCreds(input, pass);
            _empLoaded = false; Msg(_empAuthMsg, "", false);
            RefreshEmpresasView(); LoadCompanies();
        }

        // Guarda email y (si la casilla está marcada) la contraseña CIFRADA para el próximo inicio.
        void SaveRememberedCreds(string email, string pass)
        {
            _prefs.EmpresasEmail = email;
            if (_empRemember != null && _empRemember.Checked)
            {
                _prefs.RememberPassword = true;
                _prefs.EmpresasPasswordEnc = Native.Protect(pass);
            }
            else
            {
                _prefs.RememberPassword = false;
                _prefs.EmpresasPasswordEnc = null;
            }
            _prefs.Save();
        }

        // Carga el nombre de maquinista desde profiles (al iniciar sesión solo tenemos el correo).
        async void LoadMyUsername()
        {
            if (string.IsNullOrEmpty(Supa.UserId)) return;
            var (json, err) = await Supa.SelectAsync($"profiles?select=username&id=eq.{Uri.EscapeDataString(Supa.UserId)}");
            if (err != null || string.IsNullOrWhiteSpace(json)) return;
            try
            {
                using var d = JsonDocument.Parse(json);
                if (d.RootElement.ValueKind == JsonValueKind.Array && d.RootElement.GetArrayLength() > 0)
                {
                    var u = Str(d.RootElement[0], "username");
                    if (!string.IsNullOrWhiteSpace(u))
                    {
                        Supa.SetUsername(u);
                        if (_empUserLbl != null) _empUserLbl.Text = UserHeaderText();
                    }
                }
            }
            catch { }
        }

        // Texto de la barra superior de Empresas: nombre + superadmin. El ID va en el chip aparte.
        string UserHeaderText()
        {
            string id = Supa.UserId ?? "";
            string shortId = id.Length >= 8 ? id.Substring(0, 8) : id;
            if (_empIdChip != null) _empIdChip.Text = string.IsNullOrEmpty(shortId) ? "" : "ID " + shortId + "  ⧉";
            return "👤  " + Supa.DisplayName + (Supa.IsSuperadmin ? "   ★ superadmin" : "");
        }

        // Copia el ID de usuario (UUID completo) al portapapeles.
        void CopyMyId() => CopyIdToClipboard(Supa.UserId);

        // Copia un ID (UUID) al portapapeles, con aviso.
        void CopyIdToClipboard(string id)
        {
            id = (id ?? "").Trim();
            if (id.Length == 0) return;
            var msg = _usersMsg != null && _usersPanel != null && _usersPanel.Visible ? _usersMsg : _empHomeMsg;
            try { Clipboard.SetText(id); Msg(msg, Tr("ID copiado al portapapeles: ") + id, false); }
            catch { Msg(msg, "ID: " + id, false); }
        }

        // Editar el nombre de maquinista: el NOMBRE VISIBLE en empresas, socios y rankings. Lo cambia
        // el servidor (set_my_username), que comprueba que sea válido y no lo use nadie. El acceso no
        // cambia: se sigue entrando con el nombre de acceso de siempre (o con el correo).
        async void EditMaquinistaName()
        {
            if (!Supa.IsLoggedIn) return;
            string email = Supa.Email ?? "";
            string acceso = email.EndsWith("@" + SyntheticEmailDomain, StringComparison.OrdinalIgnoreCase)
                ? string.Format(Tr("Para iniciar sesión sigues usando tu nombre de acceso «{0}»."), email.Substring(0, email.IndexOf('@')))
                : Tr("Para iniciar sesión sigues usando tu correo.");
            using var dlg = new TextPromptDialog(Tr("Nombre de maquinista"),
                Tr("Este nombre te identifica en empresas y rankings (de 3 a 30 caracteres, sin @).") + " " + acceso,
                Supa.Username ?? "", Tr("p. ej. David MZP"));
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var name = System.Text.RegularExpressions.Regex.Replace((dlg.Value ?? "").Trim(), @"\s+", " ");
            if (name == (Supa.Username ?? "")) return;
            if (name.Length < 3 || name.Length > 30) { Msg(_empHomeMsg, Tr("El nombre debe tener entre 3 y 30 caracteres."), true); return; }
            if (name.IndexOfAny(new[] { '@', '<', '>', '"', '\\' }) >= 0) { Msg(_empHomeMsg, Tr("El nombre no puede llevar @, <, >, comillas ni barras."), true); return; }
            Msg(_empHomeMsg, Tr("Guardando nombre…"), false);
            var (json, err) = await Supa.RpcAsync("set_my_username", new { p_username = name });
            if (err != null)
            {
                bool falta = err.IndexOf("set_my_username", StringComparison.OrdinalIgnoreCase) >= 0
                             || err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0;
                Msg(_empHomeMsg, falta ? Tr("El servidor aún no permite cambiar el nombre.")
                                       : Tr("No se pudo cambiar el nombre: ") + err, true);
                return;
            }
            // El servidor devuelve el nombre tal como lo ha guardado
            try { var saved = JsonSerializer.Deserialize<string>(json); if (!string.IsNullOrWhiteSpace(saved)) name = saved; } catch { }
            Supa.SetUsername(name);
            _empUserLbl.Text = UserHeaderText();
            Msg(_empHomeMsg, Tr("Nombre actualizado."), false);
            if (_empSel != null) LoadMembers(_empSel);   // refresca el nombre en la lista de socios
        }

        // Muestra/oculta el campo "Nombre de maquinista" (modo registro).
        void SetRegisterMode(bool on) { _empRegisterMode = on; }

        async void DoSignUp()
        {
            var user = _empEmail.Box.Text.Trim(); var pass = _empPass.Box.Text;
            if (user.Contains("@")) { Msg(_empAuthMsg, Tr("Elige un nombre de usuario (sin @); el correo ya no se usa."), true); return; }
            if (user.Length < 3) { Msg(_empAuthMsg, Tr("El nombre de usuario debe tener al menos 3 caracteres."), true); return; }
            if (pass.Length < 6) { Msg(_empAuthMsg, Tr("La contraseña debe tener 6 caracteres o más."), true); return; }
            string email = UsernameToEmail(user);
            if (email == null) { Msg(_empAuthMsg, Tr("Nombre de usuario no válido (usa letras o números)."), true); return; }
            // Disponibilidad (si el RPC existe en Supabase); si falla, se confía en la unicidad del correo sintético.
            Msg(_empAuthMsg, Tr("Comprobando disponibilidad…"), false);
            try
            {
                var (aj, ae) = await Supa.RpcAsync("username_available", new { p_username = user });
                if (ae == null && aj != null && aj.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
                { Msg(_empAuthMsg, Tr("Ese nombre de usuario ya existe. Elige otro."), true); return; }
            }
            catch { }
            Msg(_empAuthMsg, Tr("Creando cuenta…"), false);
            var err = await Supa.SignUpAsync(email, pass, user);
            if (err != null)
            {
                bool taken = err.IndexOf("registered", StringComparison.OrdinalIgnoreCase) >= 0
                          || err.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0
                          || err.IndexOf("duplicate", StringComparison.OrdinalIgnoreCase) >= 0;
                Msg(_empAuthMsg, taken ? Tr("Ese nombre de usuario ya existe. Elige otro.") : (Tr("No se pudo registrar: ") + err), true);
                return;
            }
            SaveRememberedCreds(user, pass);
            if (Supa.IsLoggedIn) { _empLoaded = false; Msg(_empAuthMsg, "", false); RefreshEmpresasView(); LoadCompanies(); }
            else Msg(_empAuthMsg, Tr("Cuenta creada. Inicia sesión con tu nombre de usuario."), false);
        }

        void DoSignOut()
        {
            StopRealtime();
            Supa.SignOut();
            // Cerrar sesión explícito: olvidar la contraseña y no auto-reentrar.
            _prefs.RememberPassword = false; _prefs.EmpresasPasswordEnc = null; _prefs.Save();
            _empAutoTried = true;
            if (_empRemember != null) _empRemember.Checked = false;
            if (_empPass != null) _empPass.Box.Text = "";
            SetRegisterMode(false);
            _empCompanies.Clear(); _empSel = null; _empLoaded = false;
            _favCompanyId = null; _favLoaded = false;   // otra cuenta, otra favorita
            _empOnDutyCompany = null; _pendingServiceId = null;
            _members.Clear(); _myRole = null;
            if (_empCoCombo != null) _empCoCombo.Items.Clear();
            if (_empSvcList != null) _empSvcList.ClearRows();
            if (_memberList != null) _memberList.ClearRows();
            if (_bankList != null) _bankList.ClearRows();
            _svcRows.Clear();
            // Al cerrar sesión ya no perteneces a ninguna empresa → quitar los filtros por empresa
            // de Exploración/Horarios (repuebla vacío y oculta) y actualizar KPIs.
            _companyVehNames.Clear();
            RebuildCompanyEngs();
            UpdateCompanyKpis();
            RefreshEmpresasView();
        }

        async void LoadCompanies()
        {
            if (!Supa.IsLoggedIn) return;
            await LoadCompaniesCore();
        }

        async Task LoadCompaniesCore()
        {
            _empLoaded = true;
            Msg(_empHomeMsg, Tr("Cargando empresas…"), false);
            var (json, err) = await Supa.SelectAsync("companies?select=id,name,logo,balance,income_per_km,canon_per_km,energy_per_km,salary_per_service,pa_enabled&order=name.asc");
            if (err != null)   // por si aún no están las columnas logo/pa_enabled (esquema sin re-ejecutar): reintenta sin ellas
                (json, err) = await Supa.SelectAsync("companies?select=id,name,balance,income_per_km,canon_per_km,energy_per_km,salary_per_service&order=name.asc");
            if (err != null) { Msg(_empHomeMsg, Tr("Error: ") + err, true); return; }
            var prevId = _empSel?.Id;
            _empCompanies.Clear();
            _empCompanies.AddRange(ParseCompanies(json));
            _suppressCoSel = true;
            _empCoCombo.Items.Clear();
            foreach (var c in _empCompanies) _empCoCombo.Items.Add(c.Name);
            _suppressCoSel = false;
            if (_empCompanies.Count == 0)
            {
                _empSel = null; _myRole = null; _empSvcList.ClearRows();
                Msg(_empHomeMsg, Tr("Aún no perteneces a ninguna empresa. Crea una o solicita unirte a una desde la izquierda."), false);
                UpdateCompanyDash();
                UpdateRoleUi();            // recalcula subpestañas (sin empresa → solo Ranking/Mi perfil)
                UpdateEmptyStateUi();
                UpdateDutyHostVisible();
                LoadCompanyVehNames();     // etiqueta de "tren de empresa" (aquí: ninguno)
                return;
            }
            Msg(_empHomeMsg, "", false);
            UpdateEmptyStateUi();
            if (!_favLoaded) await LoadFavoriteCompanyAsync();   // la favorita está en el perfil
            int sel = prevId != null ? _empCompanies.FindIndex(c => c.Id == prevId) : -1;
            if (sel < 0 && !string.IsNullOrEmpty(_favCompanyId))   // sin selección previa: la favorita
                sel = _empCompanies.FindIndex(c => c.Id == _favCompanyId);
            if (sel < 0) sel = 0;
            if (_empCoCombo.SelectedIndex == sel) OnCompanySelected();   // mismo índice: forzar recarga
            else _empCoCombo.SelectedIndex = sel;                        // dispara OnCompanySelected
            UpdateDutyHostVisible();
            LoadCompanyVehNames();          // .eng de mis empresas → etiqueta en Exploración/Horarios
        }

        async void LoadServices(EmpCompany c)
        {
            if (c == null) return;
            _empSvcList.BeginReload(c.Id);
            _empSvcList.SetEmpty(Tr("Cargando…"));
            var (json, err) = await Supa.RpcAsync("list_company_services", new { p_company = c.Id });
            _empSvcList.ClearRows(); _svcIds.Clear(); _svcRows.Clear();
            if (err != null) { _empSvcList.SetEmpty(Tr("Error: ") + err); return; }
            int n = 0;
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    n++;
                    string id = Str(e, "id");
                    string date = FmtDate(Str(e, "started_at"));
                    string driver = Str(e, "username"); if (driver.Length == 0) driver = "—";
                    string route = Str(e, "route"); if (route.Length == 0) route = "—";
                    double km = Num(e, "km"), net = Num(e, "net"), dur = Num(e, "duration_s");
                    double inc = Num(e, "income"), cost = Num(e, "cost_total"), pax = Num(e, "pax");
                    bool valid = !e.TryGetProperty("validated", out var vv) || vv.ValueKind != JsonValueKind.False;
                    string rawStatus = Str(e, "status");
                    _svcIds.Add(id);
                    _svcRows.Add(new SvcRow { Id = id, Date = date, Driver = driver, Route = route, Status = rawStatus,
                                              Km = km, DurationS = dur, Pax = pax, Income = inc, Cost = cost, Net = net, Valid = valid });
                    // Servicio "En conducción" (open): aún no hay datos del viaje → km, tiempo, viajeros,
                    // ingreso y neto se muestran en blanco (—) hasta que se registre al terminar.
                    bool open = rawStatus == "open";
                    _empSvcList.AddRow(
                        new[] { "ⓘ", date, driver, route,
                                open ? "—" : km.ToString("N0", EsEs),
                                open ? "—" : FmtDurShort(dur),
                                open || pax <= 0 ? "—" : pax.ToString("N0", EsEs),
                                open ? "—" : inc.ToString("N0", EsEs) + " €",
                                open ? "—" : net.ToString("+#,##0.00;-#,##0.00", EsEs) + " €",
                                StatusLabel(rawStatus, valid) },
                        new Color?[] { Theme.Accent, null, Theme.Subtle, null, null, null, Theme.Subtle, Theme.Subtle,
                                       open ? Theme.Subtle : (net < 0 ? RedC : Theme.Accent), StatusColor(rawStatus, valid) }, null, id);
                }
            }
            catch { }
            if (n == 0) _empSvcList.SetEmpty(Tr("Sin servicios todavía."));
            _empSvcList.EndReload();
            UpdateCompanyKpis();   // nº de servicios y km totales
        }

        // Abre la ventana de detalle (solo lectura) del servicio seleccionado, con TODOS sus datos
        // (desglose económico leído del ledger). Reutiliza la ventana de "Servicio registrado".
        async void OpenServiceDetail(int row)
        {
            if (row < 0 || row >= _svcRows.Count) { Msg(_empHomeMsg, Tr("Selecciona un servicio de la lista."), true); return; }
            var s = _svcRows[row];
            var data = new ServiceResultDialog.Data
            {
                History = true, Company = _empSel?.Name ?? "", Route = s.Route, Driver = s.Driver, DateText = s.Date,
                Valid = s.Valid, Km = s.Km, DurationS = (int)s.DurationS, Pax = (int)s.Pax,
                Income = s.Income, Net = s.Net, StatusText = StatusLabel(s.Status, s.Valid),
                InProgress = s.Status == "open"
            };
            // Datos extra del servicio (tren, recorrido, notas) — máxima info que registró OR/servidor.
            try
            {
                var (sj, se) = await Supa.SelectAsync($"services?select=consist,path,notes&id=eq.{Uri.EscapeDataString(s.Id)}");
                if (se == null && !string.IsNullOrWhiteSpace(sj))
                {
                    using var sd = JsonDocument.Parse(sj);
                    if (sd.RootElement.ValueKind == JsonValueKind.Array && sd.RootElement.GetArrayLength() > 0)
                    {
                        var e = sd.RootElement[0];
                        data.Consist = Str(e, "consist");
                        data.Path = Str(e, "path");
                        data.Notes = Str(e, "notes");
                    }
                }
            }
            catch { }
            // Desglose por concepto desde la banca (los apuntes de este servicio).
            try
            {
                var (lj, le) = await Supa.SelectAsync($"ledger?select=concept,amount,description&service_id=eq.{Uri.EscapeDataString(s.Id)}");
                if (le == null)
                {
                    using var d = JsonDocument.Parse(lj);
                    foreach (var e in d.RootElement.EnumerateArray())
                    {
                        string con = Str(e, "concept"); double amt = Math.Abs(Num(e, "amount"));
                        switch (con)
                        {
                            case "canon": data.Canon = amt; break;
                            case "energy": data.Energy = amt; break;
                            case "salary": data.Salary = amt; break;
                            case "other": data.Rental = amt; break;   // alquiler de la unidad
                        }
                    }
                }
            }
            catch { }
            using var dlg = new ServiceResultDialog(data);
            dlg.ShowDialog(this);
        }

        // Superadmin: elimina el servicio seleccionado (revierte su efecto en la banca en el servidor).
        async void DeleteServiceRow()
        {
            if (!Supa.IsSuperadmin || _empSvcList == null) return;
            int i = _empSvcList.SelectedRow;
            if (i < 0 || i >= _svcIds.Count) { Msg(_empHomeMsg, Tr("Selecciona un servicio de la lista."), true); return; }
            if (MessageBox.Show(this, Tr("¿Eliminar este servicio? Se revertirán también sus apuntes de banca."), "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Msg(_empHomeMsg, Tr("Eliminando servicio…"), false);
            var (_, err) = await Supa.RpcAsync("delete_service", new { p_service = _svcIds[i] });
            if (err != null) { Msg(_empHomeMsg, Tr("Error: ") + err, true); return; }
            Msg(_empHomeMsg, Tr("Servicio eliminado."), false);
            LoadCompanies();
            if (_empSel != null) LoadServices(_empSel);
        }

        // Superadmin: elimina el movimiento de banca seleccionado (revierte su importe en el saldo).
        async void DeleteLedgerRow()
        {
            if (!Supa.IsSuperadmin || _bankList == null) return;
            int i = _bankList.SelectedRow;
            if (i < 0 || i >= _ledgerIds.Count) { Msg(_empHomeMsg, Tr("Selecciona un movimiento de la lista."), true); return; }
            if (MessageBox.Show(this, Tr("¿Eliminar este movimiento de banca? Se ajustará el saldo."), "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Msg(_empHomeMsg, Tr("Eliminando movimiento…"), false);
            var (_, err) = await Supa.RpcAsync("delete_ledger_entry", new { p_ledger = _ledgerIds[i] });
            if (err != null) { Msg(_empHomeMsg, Tr("Error: ") + err, true); return; }
            Msg(_empHomeMsg, Tr("Movimiento eliminado."), false);
            LoadCompanies();
            LoadLedger();
        }

        static readonly Color RedC = Color.FromArgb(229, 115, 115);

        async void LoadLedger()
        {
            if (_bankList == null) return;
            if (_empSel == null) { _bankList.SetEmpty(Tr("Selecciona una empresa.")); return; }
            _bankList.BeginReload(_empSel.Id);
            _bankList.SetEmpty(Tr("Cargando…"));
            var (json, err) = await Supa.SelectAsync(
                $"ledger?select=id,created_at,concept,amount,description&company_id=eq.{Uri.EscapeDataString(_empSel.Id)}&order=created_at.desc&limit=1000");
            _bankList.ClearRows(); _ledgerIds.Clear();
            if (err != null) { _bankList.SetEmpty(Tr("Error: ") + err); return; }
            int n = 0;
            double income = 0, expense = 0;
            var byConcept = new Dictionary<string, double>();     // gastos por concepto (positivo)
            var byMonth = new Dictionary<string, double>();       // ingresos por mes
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    n++;
                    _ledgerIds.Add(Str(e, "id"));
                    string createdAt = Str(e, "created_at");
                    string date = FmtDate(createdAt);
                    string rawConcept = Str(e, "concept");
                    string concept = TrConcept(rawConcept);
                    double amount = Num(e, "amount");
                    string desc = Str(e, "description");
                    _bankList.AddRow(
                        new[] { date, concept, amount.ToString("+#,##0.00;-#,##0.00", EsEs) + " €", desc },
                        new Color?[] { null, null, amount < 0 ? RedC : Theme.Accent, null }, null, _ledgerIds[_ledgerIds.Count - 1]);
                    // Agregados para el panel financiero
                    if (amount >= 0) { income += amount; if (rawConcept == "income" && createdAt.Length >= 7) { string m = createdAt.Substring(0, 7); byMonth[m] = byMonth.TryGetValue(m, out var mv) ? mv + amount : amount; } }
                    else { expense += -amount; byConcept[concept] = byConcept.TryGetValue(concept, out var cv) ? cv - amount : -amount; }
                }
            }
            catch { }
            if (n == 0) _bankList.SetEmpty(Tr("Sin movimientos todavía."));
            _bankList.EndReload();

            // KPIs + gráficas
            SetKpi(_finIncome, income.ToString("N0", EsEs) + " €");
            SetKpi(_finExpense, expense.ToString("N0", EsEs) + " €");
            if (_finNet != null)
            {
                double net = income - expense;
                SetKpi(_finNet, net.ToString("N0", EsEs) + " €");
                _finNet.ForeColor = net < 0 ? RedC : Theme.Accent;
            }
            _finBreakdown?.SetData(SortDesc(byConcept, 6));
            _finMonthly?.SetData(LastMonths(byMonth, 6));
        }

        // Ordena un diccionario por valor descendente y toma los primeros n.
        static List<KeyValuePair<string, double>> SortDesc(Dictionary<string, double> d, int n)
        {
            var list = new List<KeyValuePair<string, double>>(d);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (list.Count > n) list = list.GetRange(0, n);
            return list;
        }

        // Últimos n meses en orden cronológico (etiqueta "MM/aaaa").
        static List<KeyValuePair<string, double>> LastMonths(Dictionary<string, double> d, int n)
        {
            var keys = new List<string>(d.Keys); keys.Sort();   // "aaaa-MM" ordena bien
            if (keys.Count > n) keys = keys.GetRange(keys.Count - n, n);
            var list = new List<KeyValuePair<string, double>>();
            foreach (var k in keys)
            {
                string label = k.Length == 7 ? k.Substring(5, 2) + "/" + k.Substring(0, 4) : k;
                list.Add(new KeyValuePair<string, double>(label, d[k]));
            }
            return list;
        }

        static string TrConcept(string c) => c switch
        {
            "income" => Tr("Ingreso"),
            "canon" => Tr("Cánon AI"),
            "maintenance" => Tr("Mantenimiento"),
            "energy" => Tr("Energía"),
            "salary" => Tr("Salario"),
            "purchase" => Tr("Compra"),
            "loan" => Tr("Préstamo"),
            "other" => Tr("Otro"),
            _ => c
        };

        async void CreateCompany()
        {
            string name;
            using (var dlg = new TextPromptDialog(Tr("Crear empresa"), Tr("Nombre de la empresa"), "", Tr("Nombre de la empresa")))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                name = (dlg.Value ?? "").Trim();
            }
            if (name.Length < 2) { Msg(_empHomeMsg, Tr("Escribe un nombre de empresa."), true); return; }
            // El saldo inicial es el global (lo fija el superadmin en Ajustes); no se envía aquí.
            Msg(_empHomeMsg, Tr("Creando empresa…"), false);
            var (_, err) = await Supa.RpcAsync("create_company", new { p_name = name });
            if (err != null) { Msg(_empHomeMsg, Tr("Error: ") + err, true); return; }
            Msg(_empHomeMsg, Tr("Empresa creada."), false);
            LoadCompanies();
        }

        // El maquinista solicita unirse a una empresa existente (por nombre). El gerente/gestor decide.
        async void RequestJoin()
        {
            // Lista de empresas (públicas) para elegir en un desplegable (sin escribir).
            Msg(_empHomeMsg, Tr("Cargando empresas…"), false);
            var (json, lerr) = await Supa.RpcAsync("public_company_ranking", new { });
            var names = new List<string>();
            if (lerr == null && !string.IsNullOrWhiteSpace(json))
            {
                try { using var d = JsonDocument.Parse(json); foreach (var e in d.RootElement.EnumerateArray()) { var n = Str(e, "name"); if (!string.IsNullOrWhiteSpace(n)) names.Add(n.Trim()); } }
                catch { }
            }
            // Quitar duplicados y las empresas de las que ya soy socio.
            var mine = new HashSet<string>(_empCompanies.ConvertAll(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            names = names.FindAll(n => !mine.Contains(n) && seen.Add(n));
            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            if (names.Count == 0) { Msg(_empHomeMsg, Tr("No hay empresas a las que unirse ahora mismo."), true); return; }
            Msg(_empHomeMsg, "", false);
            string name = PickCompanyDialog(names);
            if (string.IsNullOrEmpty(name)) return;
            Msg(_empHomeMsg, Tr("Enviando solicitud…"), false);
            var (_, err) = await Supa.RpcAsync("request_join_by_name", new { p_name = name, p_note = "" });
            if (err != null) { Msg(_empHomeMsg, Tr("Error: ") + err, true); return; }
            Msg(_empHomeMsg, Tr("Solicitud enviada. El gerente la revisará."), false);
        }

        // Diálogo con un desplegable de empresas para "Solicitar unirse".
        string PickCompanyDialog(List<string> names)
        {
            using var dlg = new Form
            {
                Text = I18n.T("Solicitar unirse"), BackColor = Theme.Bg, ForeColor = Theme.Text,
                Font = Theme.Font(9.5f), StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                ClientSize = new Size(440, 168)
            };
            try { dlg.Icon = Icon.ExtractAssociatedIcon((Environment.ProcessPath ?? AppContext.BaseDirectory)); } catch { }
            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var lbl = new Label { Text = I18n.T("Elige la empresa a la que quieres unirte:"), Location = new Point(18, 22), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(10f) };
            var combo = new ThemeCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 404, Location = new Point(18, 52) };
            StyleCombo(combo);
            combo.Items.AddRange(names.ConvertAll(n => (object)n).ToArray());
            combo.SelectedIndex = 0;
            var ok = new RoundButton { Text = I18n.T("Solicitar unirse"), Size = new Size(180, 40), Location = new Point(242, 108), Radius = 10, BaseColor = Theme.Accent, HoverColor = Theme.AccentHi, GradientTo = Theme.Accent2, TextColor = Color.White, FontSize = 10.5f, FontStyle = FontStyle.Bold };
            ok.Click += (s, e) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };
            var cancel = new RoundButton { Text = I18n.T("Cancelar"), Size = new Size(110, 40), Location = new Point(124, 108), Radius = 10, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 10f };
            cancel.Click += (s, e) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
            dlg.Controls.Add(combo); dlg.Controls.Add(lbl); dlg.Controls.Add(ok); dlg.Controls.Add(cancel); dlg.Controls.Add(stripe);
            dlg.AcceptButton = null;
            return dlg.ShowDialog(this) == DialogResult.OK ? combo.SelectedItem as string : null;
        }

        // Renombra la empresa seleccionada (dueño/gestor o superadmin).
        async void RenameCompany()
        {
            var c = _empSel;
            if (c == null) return;
            if (!(CanManage() || Supa.IsSuperadmin)) { Msg(_empHomeMsg, Tr("No tienes permiso para renombrar la empresa."), true); return; }
            using var dlg = new TextPromptDialog(Tr("Nombre de la empresa"),
                Tr("Cambia el nombre de la empresa."), c.Name, Tr("p. ej. Unifiland"));
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var name = dlg.Value.Trim();
            if (name.Length < 2) { Msg(_empHomeMsg, Tr("El nombre debe tener al menos 2 caracteres."), true); return; }
            if (name == c.Name) return;
            Msg(_empHomeMsg, Tr("Cambiando nombre…"), false);
            var (_, err) = await Supa.RpcAsync("rename_company", new { p_company = c.Id, p_name = name });
            if (err != null) { Msg(_empHomeMsg, Tr("Error: ") + err, true); return; }
            c.Name = name; _empCoTitle.Text = name;
            Msg(_empHomeMsg, Tr("Nombre actualizado."), false);
            LoadCompanies();
        }

        // Superadmin: elimina por completo la empresa seleccionada (con doble confirmación).
        // Elimina la empresa elegida con TODO lo suyo: socios, flota, servicios (también los que estén en
        // marcha), banca, solicitudes y megafonía. La puede eliminar su gerente (o el superadmin). Para
        // confirmar hay que escribir su nombre. Los audios de megafonía (Storage) se borran antes.
        async void DeleteCompany()
        {
            var c = _empSel;
            if (c == null) { Msg(_empHomeMsg, Tr("Selecciona una empresa de la lista."), true); return; }
            if (!CanDeleteCompany()) { Msg(_empHomeMsg, Tr("Solo el gerente de la empresa puede eliminarla."), true); return; }
            if (MessageBox.Show(this,
                    string.Format(Tr("¿Eliminar la empresa «{0}» y TODOS sus datos? Se borran sus socios, su flota, todos sus servicios (también los que estén en marcha), su banca, sus solicitudes y su megafonía. Esta acción no se puede deshacer."), c.Name),
                    "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            using (var d = new TextPromptDialog(Tr("Eliminar empresa"), string.Format(Tr("Para confirmar, escribe el nombre de la empresa: {0}"), c.Name), "", "", Tr("Eliminar")))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!string.Equals((d.Value ?? "").Trim(), c.Name.Trim(), StringComparison.CurrentCultureIgnoreCase))
                { Msg(_empHomeMsg, Tr("El nombre no coincide: la empresa NO se ha eliminado."), true); return; }
            }
            Msg(_empHomeMsg, Tr("Eliminando empresa…"), false);
            // 1) Audios de megafonía: están en el almacén y no se borran con la empresa.
            var (pj, perr) = await Supa.RpcAsync("pa_company_audio_paths", new { p_company = c.Id });
            if (perr == null && !string.IsNullOrWhiteSpace(pj))
            {
                try
                {
                    using var pd = JsonDocument.Parse(pj);
                    foreach (var e in pd.RootElement.EnumerateArray())
                    {
                        string path = e.ValueKind == JsonValueKind.String ? e.GetString()
                                    : e.ValueKind == JsonValueKind.Object ? Str(e, "pa_company_audio_paths") : null;
                        if (!string.IsNullOrEmpty(path)) { try { await Megafonia.DeleteAsync(path); } catch { } }
                    }
                }
                catch { }
            }
            // 2) La empresa (el resto se borra en cascada en el servidor).
            var (_, err) = await Supa.RpcAsync("delete_company", new { p_company = c.Id });
            if (err != null) { Msg(_empHomeMsg, Tr("Error: ") + err, true); return; }
            // Lo que el programa tuviera de esa empresa deja de valer.
            if (_empOnDutyCompany?.Id == c.Id) { _empOnDutyCompany = null; _pendingServiceId = null; _svcOpenedUtc = null; }
            if (_favCompanyId == c.Id) { _favCompanyId = null; if (_prefs != null && _prefs.FavoriteCompany == c.Id) { _prefs.FavoriteCompany = null; try { _prefs.Save(); } catch { } } }
            _empSel = null;
            UpdateDutyUi();
            Msg(_empHomeMsg, string.Format(Tr("Empresa «{0}» eliminada."), c.Name), false);
            LoadCompanies();
        }

        // Bloque KPI (título pequeño + valor grande) para la tira de estadísticas.
        // Pinta la tira de KPIs (Tesorería · Socios · Servicios · Km) a mano: 4 columnas con rótulo
        // arriba y valor debajo, en coordenadas exactas relativas al alto REAL del panel. Al no depender
        // de layouts anidados ni de AutoSize, ningún valor puede quedar recortado contra el borde.
        void DrawKpiStrip(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int W = _empStatsCard.ClientSize.Width, H = _empStatsCard.ClientSize.Height;
            using (var bgB = new SolidBrush(Theme.Bg)) g.FillRectangle(bgB, 0, 0, W, H);
            var cols = new (string cap, string val, Color col)[]
            {
                (Tr("TESORERÍA"),  _kpiTreasuryTxt, _kpiTreasuryCol),
                (Tr("SOCIOS"),     _kpiMembersTxt,  Theme.Text),
                (Tr("SERVICIOS"),  _kpiServicesTxt, Theme.Text),
                (Tr("KM TOTALES"), _kpiKmTxt,       Theme.Text),
            };
            // 4 TARJETAS compactas; la de Tesorería algo más ancha (importes largos).
            float[] w = { 1.35f, 1f, 1f, 1f };
            const int gapX = 10;
            float unit = (W - gapX * 3 - 2) / (w[0] + w[1] + w[2] + w[3]);
            using var capF = Theme.Font(8f, FontStyle.Bold);
            using var valF = Theme.Font(14f, FontStyle.Bold);
            int capH = TextRenderer.MeasureText(g, "Xg", capF, Size.Empty, TextFormatFlags.NoPadding).Height;
            int valH = TextRenderer.MeasureText(g, "0€", valF, Size.Empty, TextFormatFlags.NoPadding).Height;
            int top = Math.Max(4, (H - (capH + 3 + valH)) / 2);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            float x = 0;
            for (int i = 0; i < 4; i++)
            {
                int cw = (int)(unit * w[i]);
                var rc = new Rectangle((int)x, 0, cw, H - 1);
                Theme.FillRound(g, rc, 10, Theme.Surface);
                int tx = rc.X + 14, tw = Math.Max(10, cw - 24);
                TextRenderer.DrawText(g, cols[i].cap, capF, new Rectangle(tx, top, tw, capH), Theme.Subtle, flags);
                TextRenderer.DrawText(g, cols[i].val, valF, new Rectangle(tx, top + capH + 3, tw, valH), cols[i].col, flags);
                x += cw + gapX;
            }
        }

        // Actualiza la tira de KPIs con los datos ya cargados (socios, servicios, km) y la repinta.
        // Ranking y Mi perfil son datos DEL MAQUINISTA, no de la empresa: ahí la tira de tesorería,
        // socios, servicios y km de la empresa no pinta nada y el contenido gana ese espacio.
        // Ranking (4), Mi perfil (5) y Megafonía (11) no hablan de la marcha de la empresa: sus
        // datos no tienen nada que ver con tesorería, socios, servicios ni km, así que en ellas la
        // tira de KPIs solo roba alto a la tabla.
        static bool SubtabShowsCompanyKpis(int subtab) => subtab != 4 && subtab != 5 && subtab != 11;

        void UpdateCompanyKpis()
        {
            if (_empStatsCard == null) return;
            bool show = _empSel != null && SubtabShowsCompanyKpis(_empSubtab);
            _empStatsCard.Visible = show;   // sin empresa (o fuera de la empresa) no hay KPIs que mostrar
            if (_empStatsGap != null) _empStatsGap.Visible = show;
            if (!show) return;
            _kpiMembersTxt = _members.Count.ToString();
            _kpiServicesTxt = _svcRows.Count.ToString();
            double km = 0; foreach (var s in _svcRows) km += s.Km;
            _kpiKmTxt = km.ToString("N0", EsEs) + " km";
            _empStatsCard.Invalidate();
        }

        void UpdateCompanyDash()
        {
            if (_empSel == null)
            {
                _empCoTitle.Text = Tr("Selecciona o crea una empresa");
                if (_empRoleLbl != null) _empRoleLbl.Text = "";
                _kpiTreasuryTxt = "—";
                _kpiTreasuryCol = Theme.Subtle;
                if (_empLogoPic != null) _empLogoPic.Image = null;
                if (_logoLink != null) _logoLink.Visible = false;
                if (_empRenameLink != null) _empRenameLink.Visible = false;
                if (_empFavLink != null) _empFavLink.Visible = false;
                UpdateCompanyKpis();
                return;
            }
            _empCoTitle.Text = _empSel.Name;
            _kpiTreasuryTxt = _empSel.Balance.ToString("N2", EsEs) + " €";
            _kpiTreasuryCol = _empSel.Balance < 0 ? Color.FromArgb(229, 115, 115) : Theme.Accent;
            if (_empLogoPic != null)
                _empLogoPic.Image = LogoFor(_empSel.Id, _empSel.Logo) ?? PlaceholderLogo(_empSel.Name);
            if (_logoLink != null) _logoLink.Visible = CanManage() || Supa.IsSuperadmin;
            if (_fleetBuyAllBtn != null) _fleetBuyAllBtn.Visible = Supa.IsSuperadmin;   // compra masiva
            if (_empRenameLink != null) _empRenameLink.Visible = CanManage() || Supa.IsSuperadmin;
            UpdateFavoriteUi();
            if (_empRoleLbl != null)
            {
                string role = _myRole != null ? TrRole(_myRole) : (Supa.IsSuperadmin ? "superadmin" : "—");
                // En el menú lateral (estrecho): rol y nº de socios en dos líneas limpias.
                _empRoleLbl.Text = string.Format(Tr("Tu rol: {0}   ·   {1} socios"),
                    role, _members.Count).Replace("   ·   ", "\n");
            }
            UpdateCompanyKpis();
        }

        // ============================ Logotipo de la empresa ============================
        // Decodifica el logo (data URI/base64) para un id, con caché que se rehace si cambia.
        Image LogoFor(string id, string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return null;
            if (_logoCache.TryGetValue(id, out var c) && c.src == data) return c.img;
            var img = DecodeLogo(data);
            if (_logoCache.TryGetValue(id, out var old) && old.img != null && old.img != img) { try { old.img.Dispose(); } catch { } }
            _logoCache[id] = (data, img);
            return img;
        }

        static Image DecodeLogo(string data)
        {
            try
            {
                int comma = data.IndexOf(',');
                string b64 = data.StartsWith("data:") && comma > 0 ? data.Substring(comma + 1) : data;
                // SEGURIDAD: el logo lo sube cualquier empresa. Un PNG pequeño puede declarar
                // miles de píxeles y ocupar cientos de MB al abrirlo en TODOS los clientes: se
                // limita el tamaño del texto y de la imagen, y se guarda reducida (máx. 128 px).
                if (b64.Length > 300_000) return null;
                var bytes = Convert.FromBase64String(b64.Trim());
                using var ms = new System.IO.MemoryStream(bytes);
                using var raw = Image.FromStream(ms, false, false);   // solo cabecera: aún no decodifica
                if (raw.Width < 1 || raw.Height < 1 || raw.Width > 2048 || raw.Height > 2048) return null;
                float k = Math.Min(1f, 128f / Math.Max(raw.Width, raw.Height));
                int w = Math.Max(1, (int)Math.Round(raw.Width * k)), h = Math.Max(1, (int)Math.Round(raw.Height * k));
                var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(raw, 0, 0, w, h);
                }
                return bmp;
            }
            catch { return null; }
        }

        // Placeholder: cuadrado redondeado con acento y la inicial de la empresa.
        readonly Dictionary<string, Image> _phCache = new();
        Image PlaceholderLogo(string name)
        {
            string key = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
            if (_phCache.TryGetValue(key, out var img)) return img;
            var bmp = new Bitmap(64, 64);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = RoundRect(new Rectangle(2, 2, 60, 60), 14);
                using (var b = new SolidBrush(Blend(Theme.Surface2, Theme.Accent, 0.30f))) g.FillPath(b, path);
                using var f = Theme.Font(24f, FontStyle.Bold);
                TextRenderer.DrawText(g, key, f, new Rectangle(0, 0, 64, 64), Theme.Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            _phCache[key] = bmp;
            return bmp;
        }

        static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int rad)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            p.AddArc(r.X, r.Y, rad, rad, 180, 90);
            p.AddArc(r.Right - rad, r.Y, rad, rad, 270, 90);
            p.AddArc(r.Right - rad, r.Bottom - rad, rad, rad, 0, 90);
            p.AddArc(r.X, r.Bottom - rad, rad, rad, 90, 90);
            p.CloseFigure();
            return p;
        }

        static Color Blend(Color a, Color b, float t) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        // Elige una imagen, la redimensiona a 96×96 (PNG) y la guarda como logotipo.
        async void ChangeLogo()
        {
            if (_empSel == null) return;
            if (!(CanManage() || Supa.IsSuperadmin)) { Msg(_empHomeMsg, Tr("Solo el gerente, un gestor o el superadministrador pueden cambiar el logotipo."), true); return; }
            using var dlg = new OpenFileDialog { Title = Tr("Elegir logotipo"), Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string dataUri;
            try
            {
                using var src = Image.FromFile(dlg.FileName);
                using var bmp = new Bitmap(96, 96);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.Transparent);
                    // encaja la imagen manteniendo proporción (contain)
                    float sc = Math.Min(96f / src.Width, 96f / src.Height);
                    int w = (int)(src.Width * sc), h = (int)(src.Height * sc);
                    g.DrawImage(src, (96 - w) / 2, (96 - h) / 2, w, h);
                }
                using var ms = new System.IO.MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                dataUri = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
            catch (Exception ex) { Msg(_empHomeMsg, Tr("No se pudo leer la imagen: ") + ex.Message, true); return; }
            if (dataUri.Length > 200000) { Msg(_empHomeMsg, Tr("La imagen es demasiado grande."), true); return; }
            Msg(_empHomeMsg, Tr("Guardando logotipo…"), false);
            var (_, err) = await Supa.RpcAsync("set_company_logo", new { p_company = _empSel.Id, p_logo = dataUri });
            if (err != null) { Msg(_empHomeMsg, Tr("Error: ") + err, true); return; }
            _empSel.Logo = dataUri;
            _logoCache.Remove(_empSel.Id);
            UpdateCompanyDash();
            Msg(_empHomeMsg, Tr("Logotipo actualizado."), false);
            LoadCompanies();
        }

        // ============================ Servicio (conducción) ============================
        void UpdateDutyUi()
        {
            if (_empDutyBtn == null) return;
            bool on = _empOnDutyCompany != null;
            _empDutyBtn.Text = on ? Tr("Salir de servicio") : Tr("Ponerme de servicio");
            _empDutyBtn.BaseColor = on ? Theme.Surface2 : Theme.AccentHi;
            _empDutyBtn.HoverColor = on ? Theme.SurfaceHi : Theme.AccentHi;
            _empDutyBtn.GradientTo = on ? (Color?)null : Theme.Accent2;
            _empDutyBtn.TextColor = on ? Theme.Text : Color.White;
            _empDutyBtn.Invalidate();
            if (_empCloseOpenBtn != null) _empCloseOpenBtn.Visible = _pendingServiceId != null;
            UpdateDutyHostVisible();
        }

        // La barra "Ponerme de servicio" solo tiene sentido si perteneces a alguna empresa.
        void UpdateDutyHostVisible()
        {
            if (_empDutyHost != null)
                _empDutyHost.Visible = _activePage == PageEmpresas && Supa.IsLoggedIn && _empCompanies.Count > 0;
        }

        void ToggleDuty()
        {
            if (_empOnDutyCompany != null) _empOnDutyCompany = null;
            else
            {
                if (_empSel == null) { Msg(_empHomeMsg, Tr("Selecciona primero una empresa."), true); return; }
                _empOnDutyCompany = _empSel;
                Msg(_empHomeMsg, "", false);
            }
            UpdateDutyUi();
            UpdateStatus();
        }

        // ¿Estoy de servicio de empresa y con sesión iniciada? (lo consulta Launch)
        bool EmpOnDuty => _empOnDutyCompany != null && Supa.IsLoggedIn && Supa.IsConfigured;

        // Abre el servicio en el servidor antes de lanzar OR. Devuelve el id o null si falla.
        // Muestra el HUD acompañante (overlay pequeño sobre OR) al empezar a conducir.
        //   service=true  → servicio de empresa: logo/empresa + tiempo + viajeros + mapa.
        //   service=false → conducción normal: solo tren/ruta + mapa (sin tiempo ni viajeros).
        void ShowServiceHud(bool service, bool force = false)
        {
            try
            {
                if (_prefs == null || (!_prefs.ServiceHud && !force)) return;
                CloseServiceHud();
                Image logo = null; string company = "";
                if (service && _empOnDutyCompany != null)
                {
                    company = _empOnDutyCompany.Name;
                    logo = LogoFor(_empOnDutyCompany.Id, _empOnDutyCompany.Logo) ?? PlaceholderLogo(_empOnDutyCompany.Name);
                }
                _serviceHud = new ServiceHudOverlay(company, logo,
                    CurrentConsistLabel(), _curRoute?.Name ?? "", () => _svcClockUtc, _prefs, service,
                    () => (_paxOnboard, _paxBoarded, _paxCapacity),   // viajeros en vivo (solo servicio)
                    SmoothPos,                                        // posición en vivo, suavizada (para el mapa)
                    () => _trackedMeters / 1000.0,                    // km recorridos en vivo
                    PaxHudNote,                                       // próxima parada / último embarque
                    () => (PaAvailable, PaOn, PaHudLineName()),       // megafonía: disponible · encendida · línea
                    PaHudLines, PaHudPickLine, PaHudToggle);
                _serviceHud.CabVisible = () => CabHudAlive && _cabHud.Visible;   // botón del pupitre
                _serviceHud.ToggleCab = ToggleCabHudFromBar;
                var _ = _serviceHud.Handle;   // la ventana existe ya (el trazado del mapa se le pasa aunque esté oculta)
                if (_scenarioReady || force) _serviceHud.Show(); else _hudWaiting = true;
                PushHudMap();   // descarga el trazado de la ruta y lo pasa al HUD
            }
            catch { }
        }

        void CloseServiceHud()
        {
            _hudWaiting = false;
            try { _serviceHud?.CloseHud(); } catch { }
            _serviceHud = null;
        }

        // Descarga el trazado de la ruta (puntos de /API/MAP/INIT/) + estaciones y se lo pasa al HUD para
        // el mini-mapa. Reintenta hasta que el servidor web de OR responde (arranca junto con RunActivity).
        // Antes se rendía a los ~40 intentos (≈1 minuto): en rutas grandes o PCs lentos la carga de OR tarda
        // más, el trazado nunca llegaba y solo se veía la traza del tren. Ahora reintenta mientras el HUD
        // siga abierto (hasta 30 min) y guarda el trazado por ruta para reutilizarlo al reabrir el HUD.
        static readonly Dictionary<string, HudMapData> _hudMapCache = new(StringComparer.OrdinalIgnoreCase);
        int _hudMapGen;   // cada llamada invalida las anteriores (no dos bucles a la vez)

        async void PushHudMap()
        {
            string routeDir = _curRoute?.Path ?? "";
            int gen = ++_hudMapGen;
            if (_hudMapCache.TryGetValue(routeDir, out var cached))
            {
                var h0 = _serviceHud;
                try { h0?.SetMap(cached); } catch { }
                return;
            }
            var until = DateTime.UtcNow.AddMinutes(30);
            while (DateTime.UtcNow < until && gen == _hudMapGen)
            {
                var hudNow = _serviceHud;
                if (hudNow == null || hudNow.IsDisposed) return;
                HudMapData m = null;
                try { m = await Task.Run(() => FetchHudMapData(routeDir)); } catch { }
                if (gen != _hudMapGen) return;
                if (m != null && (m.SegLat.Length > 0 || m.TrkLat.Length >= 2))
                {
                    if (routeDir.Length > 0) _hudMapCache[routeDir] = m;
                    var hud = _serviceHud;
                    try { if (hud != null && !hud.IsDisposed) hud.BeginInvoke(new Action(() => hud.SetMap(m))); } catch { }
                    return;
                }
                await Task.Delay(2000);
            }
        }

        // Prepara el mini-mapa. Fondo = LÍNEAS de vía del TDB (como el mapa de Exploración) + estaciones
        // agrupadas por nombre, convertidas a lat/lon con un ajuste calculado emparejando las estaciones
        // (presentes en el TDB en coords de mundo y en la API en lat/lon). Si el TDB o el emparejado
        // fallan, cae a la nube de puntos + estaciones de la API. La posición del tren siempre va en lat/lon.
        HudMapData FetchHudMapData(string routeDir)
        {
            // --- 1) API /API/MAP/INIT/: nube lat/lon (respaldo) + andenes "Named" (nombre + lat/lon) ---
            var trkLat = new List<double>(); var trkLon = new List<double>();
            var apiPlat = new List<(string name, double lat, double lon)>();
            try
            {
                // Rutas grandes: la respuesta tiene miles de puntos y OR la genera despacio → margen amplio.
                using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{OrWebPort()}"), Timeout = TimeSpan.FromSeconds(120) };
                string txt = http.GetStringAsync("/API/MAP/INIT/").GetAwaiter().GetResult();
                if (string.IsNullOrWhiteSpace(txt)) return null;
                using var d = JsonDocument.Parse(txt);
                if (!d.RootElement.TryGetProperty("PointOnApiMapList", out var pts)) return null;
                foreach (var p in pts.EnumerateArray())
                {
                    if (!p.TryGetProperty("LatLon", out var ll)) continue;
                    if (!ll.TryGetProperty("Lat", out var la) || !ll.TryGetProperty("Lon", out var lo)) continue;
                    double lat = la.GetDouble(), lon = lo.GetDouble();
                    if (lat == 0 && lon == 0) continue;
                    trkLat.Add(lat); trkLon.Add(lon);
                    int type = p.TryGetProperty("TypeOfPointOnApiMap", out var tp) ? tp.GetInt32() : -1;
                    string name = p.TryGetProperty("Name", out var nn) ? nn.GetString() : null;
                    if (type == 1 && !string.IsNullOrEmpty(name) && name.IndexOf("platform", StringComparison.OrdinalIgnoreCase) >= 0)
                        apiPlat.Add((name, lat, lon));
                }
            }
            catch { return null; }
            if (trkLat.Count == 0) return null;   // servidor web de OR aún no listo

            // Andenes API agrupados por estación (nombre normalizado) → centroide lat/lon + nombre legible.
            var apiByNorm = new Dictionary<string, (double sumLat, double sumLon, int n, string disp)>();
            foreach (var pp in apiPlat)
            {
                string norm = NormStation(pp.name);
                if (norm.Length == 0) continue;
                if (!apiByNorm.TryGetValue(norm, out var e)) e = (0, 0, 0, CleanStation(pp.name));
                apiByNorm[norm] = (e.sumLat + pp.lat, e.sumLon + pp.lon, e.n + 1, e.disp);
            }

            // --- 2) TDB: red de vías (polilíneas en mundo) + estaciones (centroide en mundo) ---
            List<PointF[]> net = null;
            Dictionary<string, (double sx, double sz, int n)> stWorld = null;
            try { ReadTdbWorld(routeDir, out net, out stWorld); } catch { net = null; stWorld = null; }

            // --- 2b) Conversión EXACTA de Open Rails (la misma que da la posición del tren): la vía
            //         y la marca del tren quedan una encima de la otra. Si esta versión de OR no la
            //         tiene, se sigue con el ajuste por estaciones de abajo.
            if (net != null && net.Count > 0 && OrGeo.Available)
            {
                var segLatX = new double[net.Count][]; var segLonX = new double[net.Count][];
                bool ok = true;
                for (int s = 0; s < net.Count && ok; s++)
                {
                    var poly = net[s]; var la = new double[poly.Length]; var lo = new double[poly.Length];
                    for (int i = 0; i < poly.Length; i++)
                        if (!OrGeo.TryLatLon(poly[i].X, poly[i].Y, out la[i], out lo[i])) { ok = false; break; }
                    segLatX[s] = la; segLonX[s] = lo;
                }
                if (ok)
                {
                    var sLatX = new List<double>(); var sLonX = new List<double>(); var sNameX = new List<string>();
                    if (stWorld != null)
                        foreach (var kv in stWorld)
                        {
                            if (kv.Value.n <= 0) continue;
                            if (!OrGeo.TryLatLon(kv.Value.sx / kv.Value.n, kv.Value.sz / kv.Value.n, out double la, out double lo)) continue;
                            sLatX.Add(la); sLonX.Add(lo); sNameX.Add(kv.Key.Trim());
                        }
                    return new HudMapData
                    {
                        SegLat = segLatX, SegLon = segLonX,
                        TrkLat = Array.Empty<double>(), TrkLon = Array.Empty<double>(),
                        StLat = sLatX.ToArray(), StLon = sLonX.ToArray(), StName = sNameX.ToArray()
                    };
                }
            }

            // --- 3) Emparejar estaciones por nombre → correspondencias (X,Z) ↔ (lon,lat) ---
            var corr = new List<(double X, double Z, double lon, double lat)>();
            if (net != null && net.Count > 0 && stWorld != null)
                foreach (var kv in stWorld)
                {
                    if (kv.Value.n <= 0) continue;
                    if (apiByNorm.TryGetValue(NormStation(kv.Key), out var a) && a.n > 0)
                        corr.Add((kv.Value.sx / kv.Value.n, kv.Value.sz / kv.Value.n, a.sumLon / a.n, a.sumLat / a.n));
                }

            // --- 4) Ajuste mundo→lat/lon (mínimos cuadrados si ≥3, si no analítico norte-arriba) ---
            double A = 0, B = 0, C = 0, D = 0, E = 0, F = 0; bool haveFit = false;
            if (corr.Count >= 3)
            {
                var X = new double[corr.Count]; var Z = new double[corr.Count];
                var lon = new double[corr.Count]; var lat = new double[corr.Count];
                for (int i = 0; i < corr.Count; i++) { X[i] = corr[i].X; Z[i] = corr[i].Z; lon[i] = corr[i].lon; lat[i] = corr[i].lat; }
                haveFit = FitAffine(X, Z, lon, out A, out B, out C) && FitAffine(X, Z, lat, out D, out E, out F);
            }
            if (!haveFit && corr.Count >= 1)
            {
                var c0 = corr[0];
                double cs = Math.Cos(c0.lat * Math.PI / 180.0); if (cs < 0.01) cs = 0.01;
                double sLon = 1.0 / (111320.0 * cs), sLat = 1.0 / 111320.0;
                A = sLon; B = 0; C = c0.lon - sLon * c0.X;
                D = 0; E = sLat; F = c0.lat - sLat * c0.Z;
                haveFit = true;
            }

            // --- 5) Si hay TDB + ajuste: líneas de vía + estaciones convertidas a lat/lon ---
            if (haveFit && net != null && net.Count > 0)
            {
                var segLat = new double[net.Count][]; var segLon = new double[net.Count][];
                for (int s = 0; s < net.Count; s++)
                {
                    var poly = net[s]; var la = new double[poly.Length]; var lo = new double[poly.Length];
                    for (int i = 0; i < poly.Length; i++) { double x = poly[i].X, z = poly[i].Y; lo[i] = A * x + B * z + C; la[i] = D * x + E * z + F; }
                    segLat[s] = la; segLon[s] = lo;
                }
                var sLatL = new List<double>(); var sLonL = new List<double>(); var sNameL = new List<string>();
                if (stWorld != null)
                    foreach (var kv in stWorld)
                    {
                        if (kv.Value.n <= 0) continue;
                        double x = kv.Value.sx / kv.Value.n, z = kv.Value.sz / kv.Value.n;
                        sLonL.Add(A * x + B * z + C); sLatL.Add(D * x + E * z + F); sNameL.Add(kv.Key.Trim());
                    }
                return new HudMapData
                {
                    SegLat = segLat, SegLon = segLon,
                    TrkLat = Array.Empty<double>(), TrkLon = Array.Empty<double>(),
                    StLat = sLatL.ToArray(), StLon = sLonL.ToArray(), StName = sNameL.ToArray()
                };
            }

            // --- Respaldo: nube de puntos API (submuestreada) + estaciones agrupadas de la API ---
            int n2 = trkLat.Count;
            if (n2 > 1200)
            {
                int step = (int)Math.Ceiling(n2 / 1200.0);
                var rl = new List<double>(); var rn = new List<double>();
                for (int i = 0; i < n2; i += step) { rl.Add(trkLat[i]); rn.Add(trkLon[i]); }
                trkLat = rl; trkLon = rn;
            }
            var fLat = new List<double>(); var fLon = new List<double>(); var fName = new List<string>();
            foreach (var kv in apiByNorm)
            {
                if (kv.Value.n <= 0) continue;
                fLat.Add(kv.Value.sumLat / kv.Value.n); fLon.Add(kv.Value.sumLon / kv.Value.n); fName.Add(kv.Value.disp);
            }
            return new HudMapData
            {
                SegLat = Array.Empty<double[]>(), SegLon = Array.Empty<double[]>(),
                TrkLat = trkLat.ToArray(), TrkLon = trkLon.ToArray(),
                StLat = fLat.ToArray(), StLon = fLon.ToArray(), StName = fName.ToArray()
            };
        }

        // Lee el TDB de la ruta: cada nodo de vía → polilínea (mundo); andenes agrupados por estación.
        // IMPORTANTE: igual que el mapa de Exploración, EXTIENDE cada tramo hasta la posición real de sus
        // empalmes/finales (el .tdb solo guarda el INICIO de cada sección → sin esto quedan huecos en los desvíos).
        static void ReadTdbWorld(string routeDir, out List<PointF[]> net, out Dictionary<string, (double sx, double sz, int n)> stWorld)
        {
            net = new List<PointF[]>();
            stWorld = new Dictionary<string, (double, double, int)>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(routeDir) || !System.IO.Directory.Exists(routeDir)) return;
            var tdb = System.IO.Directory.GetFiles(routeDir, "*.tdb");
            if (tdb.Length == 0) return;
            var db = new Orts.Formats.Msts.TrackDatabaseFile(tdb[0]);
            var tnodes = db.TrackDB.TrackNodes;
            var tsec = TrackGeometry.Sections(routeDir);   // curvas y desvíos como arcos (tsection.dat)

            var edges = new List<(int a, int b, PointF[] pts)>();
            var nodePos = new Dictionary<int, PointF>();   // posición de empalmes/finales (vértices compartidos)
            for (int ni = 0; ni < tnodes.Length; ni++)
            {
                var tn = tnodes[ni]; if (tn == null) continue;
                if (tn.UiD != null && (tn.TrJunctionNode != null || tn.TrEndNode))
                    nodePos[ni] = new PointF(tn.UiD.TileX * 2048f + tn.UiD.X, tn.UiD.TileZ * 2048f + tn.UiD.Z);
                var vs = tn.TrVectorNode?.TrVectorSections;
                if (vs == null || vs.Length < 1) continue;
                var poly = TrackGeometry.NodePolyline(vs, tsec);
                int aN = (tn.TrPins != null && tn.TrPins.Length >= 1) ? tn.TrPins[0].Link : -1;
                int bN = (tn.TrPins != null && tn.TrPins.Length >= 2) ? tn.TrPins[1].Link : -1;
                edges.Add((aN, bN, poly));
            }
            foreach (var (aN, bN, pts) in edges)
            {
                if (pts == null || pts.Length < 1) continue;
                PointF pa = default, pb = default;
                bool haveA = aN >= 0 && nodePos.TryGetValue(aN, out pa);
                bool haveB = bN >= 0 && nodePos.TryGetValue(bN, out pb);
                var joined = TrackGeometry.JoinEnds(pts, haveA, pa, haveB, pb);
                if (joined.Length >= 2) net.Add(joined);
            }

            if (db.TrackDB.TrItemTable != null)
                foreach (var it in db.TrackDB.TrItemTable)
                    if (it is Orts.Formats.Msts.PlatformItem pl && !string.IsNullOrWhiteSpace(pl.Station))
                    {
                        double x = pl.TileX * 2048.0 + pl.X, z = pl.TileZ * 2048.0 + pl.Z;
                        string key = pl.Station.Trim();
                        stWorld.TryGetValue(key, out var e);
                        stWorld[key] = (e.Item1 + x, e.Item2 + z, e.Item3 + 1);
                    }
        }

        // Ajuste lineal v ≈ a·X + b·Z + c por mínimos cuadrados (ecuaciones normales 3×3).
        static bool FitAffine(double[] X, double[] Z, double[] v, out double a, out double b, out double c)
        {
            a = b = c = 0; int n = X.Length; if (n < 3) return false;
            double sXX = 0, sXZ = 0, sX = 0, sZZ = 0, sZ = 0, sXv = 0, sZv = 0, sv = 0;
            for (int i = 0; i < n; i++)
            {
                sXX += X[i] * X[i]; sXZ += X[i] * Z[i]; sX += X[i];
                sZZ += Z[i] * Z[i]; sZ += Z[i];
                sXv += X[i] * v[i]; sZv += Z[i] * v[i]; sv += v[i];
            }
            double[,] m = { { sXX, sXZ, sX }, { sXZ, sZZ, sZ }, { sX, sZ, n } };
            double[] r = { sXv, sZv, sv };
            return Solve3(m, r, out a, out b, out c);
        }

        static bool Solve3(double[,] m, double[] r, out double x0, out double x1, out double x2)
        {
            x0 = x1 = x2 = 0;
            for (int col = 0; col < 3; col++)
            {
                int piv = col; for (int i = col + 1; i < 3; i++) if (Math.Abs(m[i, col]) > Math.Abs(m[piv, col])) piv = i;
                if (Math.Abs(m[piv, col]) < 1e-9) return false;
                if (piv != col) { for (int k = 0; k < 3; k++) (m[col, k], m[piv, k]) = (m[piv, k], m[col, k]); (r[col], r[piv]) = (r[piv], r[col]); }
                for (int i = 0; i < 3; i++)
                {
                    if (i == col) continue;
                    double f = m[i, col] / m[col, col];
                    for (int k = 0; k < 3; k++) m[i, k] -= f * m[col, k];
                    r[i] -= f * r[col];
                }
            }
            x0 = r[0] / m[0, 0]; x1 = r[1] / m[1, 1]; x2 = r[2] / m[2, 2];
            return true;
        }

        // Nombre de estación normalizado para EMPAREJAR (quita ", platform", "Vía N", nº final; minúsculas).
        static string NormStation(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim();
            int comma = s.LastIndexOf(','); if (comma > 0) s = s.Substring(0, comma);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*(v[íi]a|and[ée]n|platform|track|plataforma|plat\.?|pl\.?|n[ºo°])\s*\d+\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+\d+\s*$", "");
            return s.Trim().ToLowerInvariant();
        }

        // Nombre de estación legible (mismo recorte que NormStation pero conservando mayúsculas/acentos).
        static string CleanStation(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            s = s.Trim();
            int comma = s.LastIndexOf(','); if (comma > 0) s = s.Substring(0, comma);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*(v[íi]a|and[ée]n|platform|track|plataforma)\s*\d+\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+\d+\s*$", "");
            return s.Trim();
        }

        async Task<string> EmpStartService()
        {
            _estPatKm = EstimateKm();          // longitud de la ruta (respaldo)
            _estimatedKm = _estPatKm;
            _driveStartUtc = DateTime.UtcNow;  // para localizar el .save de esta sesión
            _empStartFailReason = null;
            // Fuera de flota no cuenta: la máquina de cabeza de tu tren debe ser una unidad de la
            // empresa disponible (y que tienes en local, ya que estás conduciendo ese .con).
            var engNames = CurrentConsistEngineNames();   // la motriz de cabeza y su formación fija
            if (engNames.Count == 0)
            {
                _empStartFailReason = Tr("El tren seleccionado no tiene una máquina de tracción reconocible. Elige un tren con locomotora o automotor.");
                return null;
            }
            var (vehicleId, reason) = await ResolveCompanyUnitReason(_empOnDutyCompany.Id, engNames);
            if (vehicleId == null) { _empStartFailReason = reason; return null; }   // no es de la flota / no disponible
            var (json, err) = await Supa.RpcAsync("start_service", new
            {
                p_company = _empOnDutyCompany.Id,
                p_route = _curRoute?.Name ?? "",
                p_consist = CurrentConsistLabel(),
                p_path = CurrentPathLabel(),
                p_vehicle = vehicleId
            });
            if (err != null || string.IsNullOrWhiteSpace(json))
            {
                _empStartFailReason = err != null ? (Tr("No se pudo abrir el servicio: ") + err) : null;
                return null;
            }
            try
            {
                using var d = JsonDocument.Parse(json);
                if (d.RootElement.ValueKind == JsonValueKind.String) return d.RootElement.GetString();
            }
            catch { }
            return json.Trim().Trim('"');
        }

        // Nombre del .eng líder (máquina de tracción) del tren seleccionado en la pestaña activa.
        string CurrentLeadEngName()
        {
            TrainItem c = _activePage switch
            {
                2 => _lstConsists.SelectedItem as TrainItem,
                4 => _lstConsists.SelectedItem as TrainItem,
                3 => _ttConsist,
                _ => null
            };
            var fp = c?.Locomotive?.FilePath;
            return string.IsNullOrEmpty(fp) ? null : System.IO.Path.GetFileNameWithoutExtension(fp);
        }

        // Nombres .eng de los coches motrices del tren de la pestaña activa (cabeza + coches del
        // consist), para reconocer la unidad aunque se conduzca con un consist invertido.
        List<(string name, string folder)> CurrentConsistEngineNames()
        {
            TrainItem c = _activePage switch
            {
                2 => _lstConsists.SelectedItem as TrainItem,
                4 => _lstConsists.SelectedItem as TrainItem,
                3 => _ttConsist,
                _ => null
            };
            return FleetAccessNames(c);   // la motriz de cabeza (y su formación fija, si la tiene)
        }

        // Busca una unidad de la empresa que corresponda a la máquina de cabeza del tren y esté
        // DISPONIBLE; si no, devuelve un motivo legible (no está en la flota / en servicio / mantenimiento).
        string _lastUnitReasonCode;   // notfleet · inuse · maint · unavail · error (motivo de la última comprobación)
        string _lastUnitPlate = "";   // matrícula de la unidad disponible elegida (si la tiene)

        // ---- Identidad de una unidad: el MODELO de la motriz (el nombre del .eng) ----
        // La carpeta de contenido NO entra: cada maquinista arma su .con con el contenido que tiene
        // instalado, y exigir la misma librea dejaba fuera a quien usaba otra carpeta con la misma
        // máquina. Lo que se compra es «una 256»; la carpeta se guarda solo como dato informativo
        // (la librea que se compró) y se ve en Flota.
        static string ModelKey(string folder, string name) => (name ?? "").Trim();
        static string NameOfModelKey(string key) => key ?? "";

        static string FolderOfPath(string path)
        {
            try { return System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? ""; }
            catch { return ""; }
        }

        // ¿Guarda el backend la carpeta de cada unidad? Se descubre solo: si una consulta falla por
        // la columna «folder» es que el servidor no la tiene, y entonces se compara solo por
        // el nombre del .eng (comportamiento antiguo) para no dejar a nadie sin su flota.
        static bool _vehFolderInDb = true;

        // Cualquier librea de ese modelo sirve: la unidad es la máquina, no la carpeta.
        static bool ModelMatches(string rowFolder, string wantFolder) => true;

        // Unidades que la empresa tiene de ESTE modelo de motriz, sumando todas las libreas.
        int OwnedUnits(string folder, string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            string head = UnitOfEng(name)?.Head ?? name;
            return _fleetOwnedCount.TryGetValue(ModelKey(folder, head), out var a) ? a : 0;
        }

        // Consulta a «vehicles» tolerante: si el backend aún no tiene la columna «folder»
        // (servidor sin actualizar), se repite sin ella.
        async Task<(string json, string err)> SelectVehicles(string cols, string filter)
        {
            // Por páginas: una flota grande pasa de las 1.000 filas que devuelve la API de una vez.
            var (json, err) = await Supa.SelectAllAsync("vehicles?select=" + cols + filter);
            if (err != null && err.IndexOf("folder", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _vehFolderInDb = false;   // servidor sin la columna: se compara solo por el nombre
                string sin = cols.Replace(",folder", "").Replace("folder,", "");
                (json, err) = await Supa.SelectAllAsync("vehicles?select=" + sin + filter);
            }
            return (json, err);
        }

        // Máquinas que dan derecho a conducir un tren para la empresa: la MOTRIZ DE CABEZA y, si
        // forma parte de una formación fija (automotor), el resto de coches motores de esa misma
        // unidad —comprar la cabeza desbloquea el automotor entero, igual que en Compra—. Las demás
        // máquinas acopladas (doble tracción, refuerzos, maniobras) NO dan acceso: cada una es una
        // compra aparte, así que un tren encabezado por una máquina ajena no se puede conducir por
        // llevar detrás otra que sí sea de la empresa.
        List<(string name, string folder)> FleetAccessNames(TrainItem c)
        {
            var names = new List<(string name, string folder)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (c == null) return names;
            string lead = LeadEngineName(c);
            if (string.IsNullOrEmpty(lead)) return names;
            string folder = FolderOfConsistEng(c, lead);   // la librea concreta que lleva ESTE tren
            names.Add((lead, folder)); seen.Add(lead);
            var u = UnitOfEng(lead);
            if (u != null)
            {
                if (!string.IsNullOrEmpty(u.Head) && seen.Add(u.Head)) names.Add((u.Head, folder));
                foreach (var m in u.Members) if (seen.Add(m)) names.Add((m, FolderOfConsistEng(c, m) ?? folder));
            }
            return names;
        }

        // Carpeta con la que este tren llama a una de sus máquinas (el .con lo dice coche a coche).
        string FolderOfConsistEng(TrainItem c, string engName)
        {
            try
            {
                foreach (var r in ConsistCarRefs(c?.FilePath))
                    if (r.isEngine && string.Equals(r.name, engName, StringComparison.OrdinalIgnoreCase))
                        return r.folder;
                var lp = c?.Locomotive?.FilePath;
                if (!string.IsNullOrEmpty(lp) &&
                    string.Equals(System.IO.Path.GetFileNameWithoutExtension(lp), engName, StringComparison.OrdinalIgnoreCase))
                    return FolderOfPath(lp);
            }
            catch { }
            return "";
        }

        // La máquina de cabeza del tren. Si la cabeza no aporta tracción (un coche piloto con
        // cabina, por ejemplo), la unidad de la empresa es la primera máquina que sí tira.
        string LeadEngineName(TrainItem c)
        {
            string lead = null;
            try
            {
                var lp = c?.Locomotive?.FilePath;
                if (!string.IsNullOrEmpty(lp))
                {
                    lead = System.IO.Path.GetFileNameWithoutExtension(lp);
                    if ((Veh(lp)?.PowerKw ?? 0) > 0) return lead;
                }
                foreach (var r in ConsistCarRefs(c?.FilePath))
                {
                    if (!r.isEngine) continue;
                    string path = ResolveCarFile(r.name, r.folder);
                    if (path != null && (Veh(path)?.PowerKw ?? 0) > 0) return r.name;
                    lead ??= r.name;
                }
            }
            catch { }
            return lead;
        }

        // Elemento de un filtro «in.(…)» de la API: entre comillas (con « \ » y « " » escapados) y
        // codificado para la URL. Sin comillas, un nombre con coma o paréntesis rompía la lista.
        static string PgInItem(string v) =>
            Uri.EscapeDataString("\"" + (v ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");

        async Task<(string id, string reason)> ResolveCompanyUnitReason(string companyId, List<(string name, string folder)> engs)
        {
            _lastUnitReasonCode = "error";
            if (engs == null || engs.Count == 0 || string.IsNullOrEmpty(companyId)) return (null, null);
            var esc = new List<string>();
            var folderOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var en in engs) { esc.Add(PgInItem(en.name)); folderOf[en.name] = en.folder ?? ""; }
            string inList = string.Join(",", esc);
            _lastUnitPlate = "";
            string vq = $"&company_id=eq.{Uri.EscapeDataString(companyId)}&name=in.({inList})&order=created_at.asc";
            var (json, err) = await SelectVehicles("id,status,plate,name,folder", vq);
            if (err != null && err.IndexOf("plate", StringComparison.OrdinalIgnoreCase) >= 0)   // aún sin columna de matrícula
                (json, err) = await SelectVehicles("id,status,name,folder", vq);
            if (err != null) return (null, Tr("No se pudo comprobar la flota: ") + err);
            // Reparto de unidades teniendo en cuenta la LIBREA con la que sale este tren:
            //  · Si la empresa tiene unidades de esa misma librea, hay que usar una de ellas; si
            //    están todas ocupadas se deniega, para que no circulen dos máquinas idénticas.
            //    (Con dos unidades compradas de la MISMA librea sí pueden salir las dos a la vez.)
            //  · Si esa librea no está en la flota —el maquinista tiene el contenido en otra
            //    carpeta—, vale cualquier unidad libre del modelo: lo que se compró es la máquina.
            string firstStatus = null; bool anyMaint = false, anyInUse = false;
            int total = 0, enUso = 0, enTaller = 0;
            string idMiLibrea = null, plateMiLibrea = null;   // libre y de la librea de este tren
            string idOtra = null, plateOtra = null;           // libre, de otra librea
            bool hayDeMiLibrea = false;                       // la empresa tiene esa librea (libre u ocupada)
            var libresOtras = new List<string>();             // libreas libres, para sugerirlas
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    string rowName = Str(e, "name"), rowFolder = Str(e, "folder"), st = Str(e, "status"), id = Str(e, "id");
                    folderOf.TryGetValue(rowName, out var want);
                    bool misma = string.Equals((rowFolder ?? "").Trim(), (want ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
                    if (misma) hayDeMiLibrea = true;
                    if (st == "available")
                    {
                        if (misma) { idMiLibrea ??= id; plateMiLibrea ??= Str(e, "plate"); }
                        else
                        {
                            idOtra ??= id; plateOtra ??= Str(e, "plate");
                            if (!string.IsNullOrWhiteSpace(rowFolder) && !libresOtras.Contains(rowFolder)) libresOtras.Add(rowFolder);
                        }
                        continue;
                    }
                    total++;
                    if (st == "maintenance_due") { anyMaint = true; enTaller++; }
                    if (st == "in_use") { anyInUse = true; enUso++; }
                    firstStatus = st;
                }
            }
            catch { }
            if (idMiLibrea != null) { _lastUnitPlate = plateMiLibrea ?? ""; return (idMiLibrea, null); }
            if (hayDeMiLibrea)
            {
                // Tiene esa librea, pero ninguna libre: no puede salir con ella aunque haya otras.
                _lastUnitReasonCode = "inuse";
                string libreas = libresOtras.Count > 0 ? string.Join(", ", libresOtras) : null;
                string engHead = engs[0].name;
                return (null, libreas != null
                    ? string.Format(Tr("Tren no operativo: «{0}» con esa librea ya está en servicio. Puedes salir con estas otras que tienes libres: {1}."), engHead, libreas)
                    : string.Format(Tr("Tren no operativo: «{0}» con esa librea ya está en servicio y no queda ninguna otra libre."), engHead));
            }
            if (idOtra != null) { _lastUnitPlate = plateOtra ?? ""; return (idOtra, null); }   // librea ajena: vale cualquiera
            string co = _empOnDutyCompany?.Name ?? "";
            string engName = engs[0].name;   // representativo (la cabeza) para el mensaje
            _lastUnitReasonCode = firstStatus == null ? "notfleet" : (anyInUse && !anyMaint) ? "inuse" : (anyMaint && !anyInUse) ? "maint" : "unavail";
            if (firstStatus == null)
                return (null, string.Format(Tr("«{0}» no está en la flota de {1}. Un gestor debe comprarla o alquilarla en la sección Flota, o conduce un tren que sí esté en la flota."), engName, co));
            // Con varias unidades del mismo modelo, el maquinista agradece saber CUÁNTAS hay y en qué estado.
            string cuantas = total == 1
                ? string.Format(Tr("la única unidad de «{0}»"), engName)
                : string.Format(Tr("las {0} unidades de «{1}»"), total, engName);
            if (anyInUse && !anyMaint)
                return (null, string.Format(Tr("Tren no operativo: {0} está(n) en servicio con otro maquinista. Espera a que termine o compra otra unidad."), cuantas));
            if (anyMaint && !anyInUse)
                return (null, string.Format(Tr("Tren no operativo: {0} está(n) pendiente(s) de mantenimiento. Un gestor debe llevarla al taller antes de usarla."), cuantas));
            return (null, string.Format(Tr("Tren no operativo: {0} no está(n) disponible(s): {1} en servicio y {2} en mantenimiento."), cuantas, enUso, enTaller));
        }

        // Tren seleccionado en la pestaña activa (Actividad / Exploración / Horarios / Multijugador).
        string CurrentConsistLabel()
        {
            string s = _activePage switch
            {
                1 => (_lstActivities.SelectedItem as Activity)?.Name,
                2 => (_lstConsists.SelectedItem as TrainItem)?.Name,
                3 => _cboTTTrain.SelectedItem?.ToString(),
                4 => (_lstConsists.SelectedItem as TrainItem)?.Name,
                _ => null
            };
            return s ?? "";
        }

        string CurrentPathLabel() => CurrentPath()?.Name ?? "";

        // Llamado cuando OR se cierra tras un lanzamiento con servicio abierto.
        void OnDriveReturned()
        {
            CloseServiceHud();   // cerrar el HUD al volver de conducir
            CloseCabHud();       // y el pupitre
            CloseDriveBar();     // y la barra superior
            RestoreAfterDrive();
            FinalizeService();
        }

        // Estado de la ventana antes de lanzar el simulador (maximizada o normal).
        FormWindowState _preLaunchState = FormWindowState.Maximized;

        // Vuelve a dejar la ventana como estaba antes de conducir y la trae al frente. Se repite a
        // los 0,6 y 2 s: al cerrarse Open Rails (sobre todo desde pantalla completa) Windows
        // recoloca las ventanas y a veces la deja minimizada o detrás.
        void RestoreAfterDrive()
        {
            void Restaurar()
            {
                try
                {
                    if (IsDisposed) return;
                    var st = _preLaunchState == FormWindowState.Minimized ? FormWindowState.Normal : _preLaunchState;
                    if (!Visible) Show();
                    if (WindowState != st) WindowState = st;
                    // Truco para saltarse el bloqueo de primer plano de Windows: un instante encima de todo.
                    TopMost = true; TopMost = false;
                    BringToFront(); Activate();
                }
                catch { }
            }
            Restaurar();
            foreach (int ms in new[] { 600, 2000 })
            {
                var t = new System.Windows.Forms.Timer { Interval = ms };
                t.Tick += (s, e) => { t.Stop(); t.Dispose(); if (WindowState == FormWindowState.Minimized) Restaurar(); };
                t.Start();
            }
        }

        // Mide los km/tiempo REALES del viaje y registra el servicio. Lo usan TANTO el cierre
        // automático (al salir de OR) COMO el botón "Registrar servicio", para que este último
        // NUNCA registre la estimación del .pat con 0 s (medía mal si se pulsaba antes de tiempo).
        void FinalizeService()
        {
            // km reales medidos por la API de OR mientras conducías (lo más fiable)
            double liveKm = StopKmTracking();
            // tiempo REAL de reloj (el acelerador de tiempo de OR no lo afecta)
            _tripDurationS = ServiceSeconds();   // desde que el escenario estuvo abierto (sin la carga)
            if (_pendingServiceId == null) return;
            if (liveKm > 0.2)
            {
                _estimatedKm = liveKm;
            }
            else
            {
                double saveKm = EstimateKmFromSave(_driveStartUtc, _estPatKm * 1000.0);
                if (saveKm > 0) _estimatedKm = saveKm;
                else if (_estPatKm > 0) _estimatedKm = _estPatKm;
            }
            ShowPage(PageEmpresas);   // ir a Empresas
            EmpCloseService();
        }

        // ---- Seguimiento de km reales por la API web de OR (/API/MAP/) ----
        static int OrWebPort()
        {
            try
            {
                var v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\OpenRails\ORTS", "WebServerPort", null);
                if (v != null && int.TryParse(v.ToString(), out var p) && p > 0) return p;
            }
            catch { }
            return 2150;   // puerto por defecto del servidor web de OR
        }

        void StartKmTracking(bool withPax = true)
        {
            _trackedMeters = 0; _tHave = false; _tLat = _tLon = 0;
            _svcClockUtc = null;   // el cronómetro espera a que el escenario esté abierto
            _scenarioReady = false;   // y los HUD también (ver OnScenarioReady)
            _stoppedSinceUtc = null;
            _paxActive = false; _paxWanted = false; _paxBoarded = 0; _paxOnboard = 0; _paxCapacity = 0;
            try { Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\Software\OpenRails\ORTS", "WebServer", 1); } catch { }
            try
            {
                _kmHttp?.Dispose();
                _kmHttp = new System.Net.Http.HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{OrWebPort()}"), Timeout = TimeSpan.FromSeconds(2) };
                _kmTimer?.Dispose();
                _kmTimer = new Timer { Interval = 1500 };
                _kmTimer.Tick += async (s, e) => await PollKm();
                _kmTimer.Start();
                if (withPax) StartPaxTracking(_drivenConsist ?? CurrentDrivenConsist());   // viajeros (servicio o conducción libre)
                PaDriveStart(_curRoute?.Name);   // megafonía: se descarga lo de esta ruta y queda lista
            }
            catch { }
        }

        // TrainItem del tren de la pestaña activa (para el que se conduce).
        TrainItem CurrentDrivenConsist() => _activePage switch
        {
            2 => _lstConsists.SelectedItem as TrainItem,
            4 => _lstConsists.SelectedItem as TrainItem,
            3 => _ttConsist,
            _ => null
        };

        // ======================= MODELO DE VIAJEROS (Fase 1) =======================
        // Demanda por estación = base (Ajustes) × PESO de la estación (nº de andenes) × PERFIL del tipo de
        // servicio × CURVA HORARIA del perfil × ESTACIÓN DEL AÑO × CLIMA × variación aleatoria (±15 %).
        // En cada parada bajan una fracción según el perfil (más en estaciones grandes) y suben los que
        // esperan, hasta las plazas (Cercanías admite +30 % de pie). Una estación se puede volver a
        // atender si el tren se aleja > 1,5 km y regresa (ida y vuelta).
        enum PaxProfile { Cercanias, Media, Larga, AV }
        PaxProfile _paxProfile = PaxProfile.Cercanias;
        int _paxSeason = -1, _paxWeather = 0;            // 0 prim · 1 ver · 2 otoño · 3 inv (−1 = desconocida) / 0 despejado · 1 nieve · 2 lluvia
        int _paxSeed;                                    // semilla del servicio (variación reproducible dentro del viaje)
        readonly Dictionary<string, (string name, double lat, double lon, double weight)> _paxMeta = new();   // estación normalizada → datos
        readonly Dictionary<string, int> _paxVisits = new();
        double _pPrevLat, _pPrevLon, _pDirX, _pDirY; bool _pHavePrev, _pHaveDir;
        string _paxNextName, _paxNextNorm; int _paxNextWaiting; double _paxNextDist;
        string _paxLastEvent; DateTime _paxLastEventUtc;
        int _paxLastHour = 12; DateTime _paxHourUtc;

        // ---------------- Parámetros del modelo (Ajustes → Viajeros / Clasificación) ----------------
        // Se guardan en app_settings.pax_model (jsonb). Si la columna aún no existe, se usan estos valores.
        sealed class PaxModelConfig
        {
            public double[] StationWeights = { 0.6, 1.0, 1.8, 3.0 };      // 1 andén · 2–3 · 4–7 · 8 o más
            public double[] ProfileDemand = { 1.0, 0.8, 0.6, 0.7 };       // Cercanías · Media · Larga · AV
            public double[] ProfileAlight = { 0.45, 0.35, 0.25, 0.20 };   // fracción que baja en cada parada
                public double BigStationAlight = 1.3;                         // en estaciones grandes bajan × esto
            public double Jitter = 0.15;                                  // variación aleatoria ±
            public double ReboardM = 1500;                                // reembarque tras alejarse (m)
            public double SnowFactor = 0.80, RainCercanias = 1.10, RainMedia = 1.05;
            public bool UseSeason = true, UseWeather = true;
            // Clasificación del tipo de servicio
            public double AvKmh = 250, LargaKmh = 160, MediaKmh = 120, MediaMaxDensity = 1.3;
        }
        static PaxModelConfig PaxCfg = new PaxModelConfig();
        static readonly JsonSerializerOptions PaxJsonOpts = new JsonSerializerOptions { IncludeFields = true };

        static PaxModelConfig ParsePaxModel(string json)
        {
            var d = new PaxModelConfig();
            try
            {
                var c = JsonSerializer.Deserialize<PaxModelConfig>(json, PaxJsonOpts);
                if (c == null) return d;
                // Tablas con 4 valores; si vienen mal, se conservan las de por defecto.
                if (c.StationWeights?.Length != 4) c.StationWeights = d.StationWeights;
                if (c.ProfileDemand?.Length != 4) c.ProfileDemand = d.ProfileDemand;
                if (c.ProfileAlight?.Length != 4) c.ProfileAlight = d.ProfileAlight;
                return c;
            }
            catch { return d; }
        }

        // Lee app_settings.pax_model (consulta aparte: si la columna no existe, no rompe el resto).
        async Task LoadPaxModel()
        {
            var (json, err) = await Supa.SelectAsync("app_settings?select=pax_model&id=eq.1");
            if (err != null || string.IsNullOrWhiteSpace(json)) return;
            try
            {
                using var d = JsonDocument.Parse(json); var r = d.RootElement;
                if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0 &&
                    r[0].TryGetProperty("pax_model", out var pm) && pm.ValueKind == JsonValueKind.Object)
                    PaxCfg = ParsePaxModel(pm.GetRawText());
            }
            catch { }
        }

        async void StartPaxTracking(TrainItem c)
        {
            _paxActive = false; _paxStations = new(); _paxDone.Clear(); _paxMeta.Clear(); _paxVisits.Clear();
            _paxBoarded = 0; _paxOnboard = 0; _paxCapacity = 0; _paxBusy = false; _paxWanted = true;
            _pHavePrev = _pHaveDir = false; _paxNextName = null; _paxLastEvent = null; _paxHourUtc = DateTime.MinValue;            _paxSeed = Environment.TickCount;
            // Estación del año y clima elegidos para el viaje (Exploración / Horarios; en Actividad, neutros).
            try
            {
                if (_activePage == 2) { _paxSeason = _segSeason?.SelectedIndex ?? -1; _paxWeather = _segWeather?.SelectedIndex ?? 0; }
                else if (_activePage == 3) { _paxSeason = _segTTSeason?.SelectedIndex ?? -1; _paxWeather = _segTTWeather?.SelectedIndex ?? 0; }
                else { _paxSeason = -1; _paxWeather = 0; }
            }
            catch { _paxSeason = -1; _paxWeather = 0; }
            LoadFleetScale();   // refresca la demanda base de viajeros (y escala/alquiler) antes de conducir
            try
            {
                var engPath = c?.Locomotive?.FilePath;
                if (string.IsNullOrEmpty(engPath)) return;
                double kmh = ReadEngineSpecs(engPath).kmh;
                var analysis = AnalyzeComposition(c, kmh);
                if (analysis.Capacity < 1) return;   // sin plazas (tren de mercancías) → sin embarque
                _paxCapacity = (int)Math.Round(analysis.Capacity);
                _paxProfile = analysis.ServiceType == Tr("Alta Velocidad") ? PaxProfile.AV
                            : analysis.ServiceType == Tr("Larga Distancia") ? PaxProfile.Larga
                            : analysis.ServiceType == Tr("Media Distancia") ? PaxProfile.Media
                            : PaxProfile.Cercanias;
                // El servidor web de OR tarda en responder (el mundo está cargando): se reintenta.
                var list = await Task.Run(() =>
                {
                    // Igual que el mapa: la carga de rutas grandes puede tardar varios minutos.
                    var until = DateTime.UtcNow.AddMinutes(30);
                    while (DateTime.UtcNow < until && _paxWanted)
                    {
                        var r = FetchPaxStations();
                        if (r != null) return r;   // OR respondió (con o sin andenes)
                        System.Threading.Thread.Sleep(3000);
                    }
                    return new List<(string station, double lat, double lon)>();
                });
                if (!_paxWanted) return;
                BuildPaxStations(list);
                _paxActive = _paxStations.Count > 0;
            }
            catch { }
        }

        // Agrupa los andenes por ESTACIÓN (quitando «Vía N») y calcula su peso por nº de andenes.
        void BuildPaxStations(List<(string station, double lat, double lon)> raw)
        {
            var groups = new Dictionary<string, (string pretty, HashSet<string> names, int points, double sLat, double sLon)>();
            foreach (var (station, lat, lon) in raw)
            {
                string norm = NormStation(station);
                if (norm.Length == 0) continue;
                if (!groups.TryGetValue(norm, out var g)) g = (CleanStation(station), new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, 0, 0);
                g.names.Add(station);
                groups[norm] = (g.pretty, g.names, g.points + 1, g.sLat + lat, g.sLon + lon);
                _paxStations.Add((norm, lat, lon));
            }
            foreach (var kv in groups)
            {
                // Andenes: por nombres distintos («Vía 1», «Vía 2»…) o, si comparten nombre, por puntos (2 por andén).
                int platforms = Math.Max(kv.Value.names.Count, (int)Math.Ceiling(kv.Value.points / 2.0));
                var sw = PaxCfg.StationWeights;
                double weight = platforms <= 1 ? sw[0] : platforms <= 3 ? sw[1] : platforms <= 7 ? sw[2] : sw[3];
                _paxMeta[kv.Key] = (kv.Value.pretty, kv.Value.sLat / kv.Value.points, kv.Value.sLon / kv.Value.points, weight);
            }
        }

        // Descarga los andenes de viajeros de la ruta (puntos "Named" acabados en "platform"),
        // con su nombre y posición en el MISMO lat/lon que la posición del tren.
        // null = OR aún no responde (cargando); lista vacía = respondió pero la ruta no tiene andenes con nombre.
        List<(string station, double lat, double lon)> FetchPaxStations()
        {
            var res = new List<(string, double, double)>();
            try
            {
                using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{OrWebPort()}"), Timeout = TimeSpan.FromSeconds(120) };
                string txt = http.GetStringAsync("/API/MAP/INIT/").GetAwaiter().GetResult();
                if (string.IsNullOrWhiteSpace(txt)) return null;
                using var d = JsonDocument.Parse(txt);
                if (!d.RootElement.TryGetProperty("PointOnApiMapList", out var pts)) return null;
                if (pts.GetArrayLength() == 0) return null;   // mundo aún sin cargar
                foreach (var p in pts.EnumerateArray())
                {
                    if (!p.TryGetProperty("TypeOfPointOnApiMap", out var tp) || tp.GetInt32() != 1) continue;   // 1 = Named
                    string name = p.TryGetProperty("Name", out var nn) ? nn.GetString() : null;
                    if (string.IsNullOrEmpty(name) || name.IndexOf("platform", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!p.TryGetProperty("LatLon", out var ll)) continue;
                    double lat = ll.GetProperty("Lat").GetDouble(), lon = ll.GetProperty("Lon").GetDouble();
                    int comma = name.LastIndexOf(',');
                    string key = (comma > 0 ? name.Substring(0, comma) : name).Trim();   // quita ", platform"
                    res.Add((key, lat, lon));
                }
            }
            catch { return null; }
            return res;
        }

        // ---- Factores del modelo ----
        static readonly double[][] PaxHourCurve =
        {
            // Cercanías: puntas fuertes de ida y vuelta al trabajo
            new[] { .15, .08, .05, .05, .10, .30, .80, 1.6, 1.8, 1.3, .90, .80, .90, 1.0, 1.0, .90, 1.1, 1.6, 1.7, 1.3, .90, .60, .40, .25 },
            // Media Distancia: puntas suaves
            new[] { .10, .05, .05, .05, .10, .30, .70, 1.2, 1.3, 1.1, 1.0, 1.0, 1.0, 1.1, 1.1, 1.0, 1.1, 1.3, 1.3, 1.1, .90, .60, .35, .20 },
            // Larga Distancia: demanda repartida por el día
            new[] { .10, .05, .05, .05, .10, .30, .80, 1.2, 1.3, 1.2, 1.1, 1.0, 1.0, 1.1, 1.1, 1.1, 1.2, 1.3, 1.3, 1.2, 1.0, .80, .50, .25 },
            // Alta Velocidad: primera hora y tarde, casi nada de madrugada
            new[] { .05, .03, .03, .03, .05, .30, .90, 1.4, 1.4, 1.2, 1.0, 1.0, 1.0, 1.1, 1.1, 1.1, 1.2, 1.4, 1.4, 1.2, 1.0, .70, .40, .15 },
        };
        static double PaxProfileFactor(PaxProfile p) => PaxCfg.ProfileDemand[(int)p];
        static double PaxAlightFraction(PaxProfile p) => PaxCfg.ProfileAlight[(int)p];
        static double PaxSeasonFactor(PaxProfile p, int season)
        {
            if (!PaxCfg.UseSeason || season < 0 || season > 3) return 1.0;
            double[] f = p switch
            {
                PaxProfile.Cercanias => new[] { 1.00, 0.85, 1.05, 1.05 },   // en verano baja el viaje al trabajo
                PaxProfile.Media     => new[] { 1.00, 1.10, 1.00, 0.95 },
                PaxProfile.Larga     => new[] { 1.00, 1.25, 0.95, 0.90 },   // vacaciones
                _                    => new[] { 1.05, 1.20, 1.00, 0.95 },
            };
            return f[season];
        }
        static double PaxWeatherFactor(PaxProfile p, int weather) => !PaxCfg.UseWeather ? 1.0 : weather switch
        {
            1 => PaxCfg.SnowFactor,                                                             // nieve: se viaja menos
            2 => p == PaxProfile.Cercanias ? PaxCfg.RainCercanias : p == PaxProfile.Media ? PaxCfg.RainMedia : 1.0,   // lluvia
            _ => 1.0
        };

        // Variación reproducible dentro del servicio: misma estación y misma visita → mismo valor.
        double PaxJitter(string norm, int salt, double spread)
        {
            int h = _paxSeed ^ (salt * 7919);
            foreach (char ch in norm ?? "") h = unchecked(h * 31 + ch);
            double frac = ((h & 0x7FFFFFFF) % 2001) / 1000.0 - 1.0;   // −1..1
            return 1.0 + frac * spread;
        }

        // Viajeros que esperan en una estación a una hora dada.
        int PaxDemandAt(string norm, int hour)
        {
            double weight = _paxMeta.TryGetValue(norm, out var m) ? m.weight : 1.0;
            int visit = _paxVisits.TryGetValue(norm, out var v) ? v : 0;
            var curve = PaxHourCurve[(int)_paxProfile];
            double d = _paxDemandBase * weight * PaxProfileFactor(_paxProfile) * curve[((hour % 24) + 24) % 24]
                     * PaxSeasonFactor(_paxProfile, _paxSeason) * PaxWeatherFactor(_paxProfile, _paxWeather)
                     * PaxJitter(norm, visit, PaxCfg.Jitter);
            return Math.Max(0, (int)Math.Round(d));
        }

        // Rumbo del tren (para saber qué estaciones quedan POR DELANTE).
        void UpdatePaxHeading(double lat, double lon)
        {
            if (!_pHavePrev) { _pPrevLat = lat; _pPrevLon = lon; _pHavePrev = true; return; }
            double dy = (lat - _pPrevLat) * 111320.0;
            double dx = (lon - _pPrevLon) * 111320.0 * Math.Cos(lat * Math.PI / 180.0);
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 15) return;   // espera a moverse lo suficiente
            _pDirX = dx / len; _pDirY = dy / len; _pHaveDir = true;
            _pPrevLat = lat; _pPrevLon = lon;
        }

        // Reembarque: una estación ya atendida vuelve a estar disponible si el tren se aleja > 1,5 km.
        void ReleaseFarStations(double lat, double lon)
        {
            if (_paxDone.Count == 0) return;
            foreach (var norm in new List<string>(_paxDone))
                if (_paxMeta.TryGetValue(norm, out var m) && Haversine(lat, lon, m.lat, m.lon) > PaxCfg.ReboardM)
                { _paxDone.Remove(norm); _paSaidNext.Remove(norm); }
        }

        // Próxima estación por delante (≤ 8 km) y cuántos viajeros esperan en ella.
        void UpdateNextStation(double lat, double lon)
        {
            string best = null; double bestD = double.MaxValue;
            foreach (var kv in _paxMeta)
            {
                if (_paxDone.Contains(kv.Key)) continue;
                double dist = Haversine(lat, lon, kv.Value.lat, kv.Value.lon);
                if (dist > 8000 || dist >= bestD) continue;
                if (_pHaveDir && dist > 300)
                {
                    double vy = (kv.Value.lat - lat) * 111320.0;
                    double vx = (kv.Value.lon - lon) * 111320.0 * Math.Cos(lat * Math.PI / 180.0);
                    if (vx * _pDirX + vy * _pDirY <= 0) continue;   // queda por detrás
                }
                best = kv.Key; bestD = dist;
            }
            if (best == null) { _paxNextName = null; _paxNextNorm = null; return; }
            _paxNextName = _paxMeta[best].name; _paxNextNorm = best; _paxNextDist = bestD;
            _paxNextWaiting = PaxDemandAt(best, _paxLastHour);
        }

        // Texto de viajeros para el HUD: solo el embarque en curso / recién terminado (sin próxima parada).
        string PaxHudNote()
        {
            if (!_paxActive) return "";
            if (_paxAnimating) return $"{_paxAnimStation}:  +{_paxAnimBoarded} / −{_paxAnimAlighted}";
            if (_paxLastEvent != null && (DateTime.UtcNow - _paxLastEventUtc).TotalSeconds < 20) return _paxLastEvent;
            return "";
        }

        // ---- Embarque PROGRESIVO: los viajeros bajan y suben poco a poco (no de golpe) ----
        System.Windows.Forms.Timer _paxAnimTimer;
        readonly Random _paxRnd = new Random();
        bool _paxAnimating, _paxAnimDoorCheck;
        string _paxAnimStation;
        int _paxAnimAlightLeft, _paxAnimBoardLeft, _paxAnimAlighted, _paxAnimBoarded, _paxAnimTicks, _paxAnimStep;

        void StartPaxAnimation(string station, int alight, int board)
        {
            _paxAnimStation = station;
            _paxAnimAlightLeft = alight; _paxAnimBoardLeft = board;
            _paxAnimAlighted = 0; _paxAnimBoarded = 0; _paxAnimTicks = 0;
            // Ritmo: ~3 viajeros/s en paradas pequeñas; en las grandes sube el ritmo para no pasar de ~25 s.
            _paxAnimStep = Math.Max(1, (int)Math.Ceiling((alight + board) / 80.0));
            _paxAnimating = true;
            if (_paxAnimTimer == null)
            {
                _paxAnimTimer = new System.Windows.Forms.Timer { Interval = 300 };
                _paxAnimTimer.Tick += async (s, e) => await PaxAnimTick();
            }
            _paxAnimTimer.Start();
        }

        async Task PaxAnimTick()
        {
            if (!_paxAnimating) { _paxAnimTimer?.Stop(); return; }
            if (!_paxActive) { EndPaxAnimation(); return; }
            _paxAnimTicks++;
            // Cada ~1,5 s: si se cierran las puertas, se acaba el intercambio (los que faltan se quedan).
            if (_paxAnimTicks % 5 == 0 && !_paxAnimDoorCheck)
            {
                _paxAnimDoorCheck = true;
                bool stop = false;
                try
                {
                    var (hasDoors, open) = await FetchDoorsState();
                    // Con mandos de puertas: se corta al cerrarlas. Sin ellos: al arrancar el tren.
                    stop = hasDoors ? !open : (_stoppedSinceUtc == null);
                }
                catch { }
                _paxAnimDoorCheck = false;
                if (stop) { EndPaxAnimation(); return; }
            }
            // Flujo irregular: a veces nadie, a veces un grupo.
            int n = _paxRnd.Next(0, 2 * _paxAnimStep + 1);
            if (_paxAnimAlightLeft > 0)          // primero bajan…
            {
                n = Math.Min(n, _paxAnimAlightLeft);
                _paxAnimAlightLeft -= n; _paxAnimAlighted += n;
                _paxOnboard = Math.Max(0, _paxOnboard - n);
            }
            else if (_paxAnimBoardLeft > 0)      // …y después suben
            {
                // Tope duro: las plazas que declara la composición (PassengerCapacity).
                int libres = _paxCapacity > 0 ? Math.Max(0, _paxCapacity - _paxOnboard) : n;
                n = Math.Min(Math.Min(n, _paxAnimBoardLeft), libres);
                if (libres <= 0) { EndPaxAnimation(); return; }   // tren completo: los demás se quedan
                _paxAnimBoardLeft -= n; _paxAnimBoarded += n;
                _paxOnboard += n; _paxBoarded += n;
            }
            else EndPaxAnimation();
        }

        void EndPaxAnimation()
        {
            _paxAnimTimer?.Stop();
            if (!_paxAnimating) return;
            _paxAnimating = false;
            _paxLastEvent = $"{_paxAnimStation}:  +{_paxAnimBoarded} / −{_paxAnimAlighted}";
            _paxLastEventUtc = DateTime.UtcNow;
        }

        // En cada sondeo: rumbo, reembarque, próxima parada y, si el tren está PARADO con PUERTAS
        // ABIERTAS junto a un andén, bajan/suben viajeros (una vez por estación y visita).
        async Task PollPax(double lat, double lon)
        {
            UpdatePaxHeading(lat, lon);
            ReleaseFarStations(lat, lon);
            if ((DateTime.UtcNow - _paxHourUtc).TotalSeconds > 30)   // hora del juego (para la curva) cada 30 s
            {
                _paxHourUtc = DateTime.UtcNow;
                try { _paxLastHour = await FetchGameHour(); } catch { }
            }
            UpdateNextStation(lat, lon);
            PaUpdateSpeed(lat, lon);                                      // megafonía: velocidad para la antelación
            await PaScan(lat, lon);                                       // megafonía: barrido de andenes
            if (_paxBusy || _paxAnimating) return;
            double best = double.MaxValue; string bestNorm = null;
            foreach (var st in _paxStations)
            {
                double dm = Haversine(lat, lon, st.lat, st.lon);
                if (dm < best) { best = dm; bestNorm = st.station; }
            }
            if (bestNorm == null || best > 250 || _paxDone.Contains(bestNorm)) return;
            _paxBusy = true;
            try
            {
                // Parado: por la posición (vale con OR en cualquier idioma); si no, por la velocidad del HUD de OR.
                if (!TrainStoppedByPosition(3))
                {
                    double speed = await FetchSpeedKmh();
                    if (speed > 2.0) return;             // aún en movimiento
                }
                // Puertas: si la cabina tiene mandos de puertas, deben estar ABIERTAS. Si no los tiene (no se
                // puede saber), cuenta como parada comercial tras ~6 s detenido junto al andén.
                var (hasDoors, open) = await FetchDoorsState();
                if (hasDoors && !open) return;                          // puertas cerradas → no embarca
                if (!hasDoors && !TrainStoppedByPosition(6)) return;    // sin mandos de puertas: esperar la parada
                int hour = await FetchGameHour(); _paxLastHour = hour;
                double weight = _paxMeta.TryGetValue(bestNorm, out var m) ? m.weight : 1.0;
                int visit = _paxVisits.TryGetValue(bestNorm, out var vv) ? vv : 0;
                // Bajan: fracción del perfil, más en estaciones grandes (±15 %).
                bool big = weight >= PaxCfg.StationWeights[2];
                double frac = PaxAlightFraction(_paxProfile) * (big ? PaxCfg.BigStationAlight : 1.0) * PaxJitter(bestNorm, visit + 101, PaxCfg.Jitter);
                int alight = (int)Math.Floor(_paxOnboard * Math.Min(0.9, Math.Max(0, frac)));
                // Suben: los que esperan, hasta el aforo. El aforo es el PassengerCapacity sumado de
                // toda la composición: no se admiten más viajeros a bordo que plazas declara el tren.
                int board = Math.Max(0, Math.Min(PaxDemandAt(bestNorm, hour), _paxCapacity - (_paxOnboard - alight)));
                _paxDone.Add(bestNorm);
                _paxVisits[bestNorm] = visit + 1;
                // El intercambio se hace poco a poco (el HUD va mostrando cómo cambian las cifras).
                StartPaxAnimation(m.name ?? bestNorm, alight, board);
            }
            catch { }
            finally { _paxBusy = false; }
        }

        async Task<double> FetchSpeedKmh()
        {
            try
            {
                string txt = await _kmHttp.GetStringAsync("/API/HUD/0");
                using var d = JsonDocument.Parse(txt);
                var vals = d.RootElement.GetProperty("commonTable").GetProperty("values");
                for (int k = 0; k + 2 < vals.GetArrayLength(); k += 3)
                    if (string.Equals(vals[k].GetString(), "Speed", StringComparison.OrdinalIgnoreCase))
                    {
                        string sp = vals[k + 2].GetString() ?? "";   // p.ej. "2,6 km/h"
                        var m = System.Text.RegularExpressions.Regex.Match(sp, @"([\d.,]+)");
                        if (m.Success && double.TryParse(m.Groups[1].Value.Replace(".", "").Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return v;
                    }
            }
            catch { }
            return 999;   // si no se puede leer, se asume en movimiento (no embarca)
        }

        async Task<bool> FetchDoorsOpen() => (await FetchDoorsState()).open;

        // Puertas según los mandos de la CABINA. hasControl=false si la cabina no tiene ningún mando/indicador
        // de puertas (muy habitual): entonces SelectOR no puede saber si están abiertas.
        async Task<(bool hasControl, bool open)> FetchDoorsState()
        {
            bool has = false;
            try
            {
                string txt = await _kmHttp.GetStringAsync("/API/CABCONTROLS");
                using var d = JsonDocument.Parse(txt);
                foreach (var c in d.RootElement.EnumerateArray())
                {
                    string tn = c.TryGetProperty("TypeName", out var t) ? t.GetString() : "";
                    if (tn != null && (tn == "DOORS_DISPLAY" || tn == "ORTS_LEFTDOOR" || tn == "ORTS_RIGHTDOOR"))
                    {
                        has = true;
                        if (c.TryGetProperty("RangeFraction", out var rf) && rf.GetDouble() >= 0.5) return (true, true);
                    }
                }
            }
            catch { }
            return (has, false);
        }

        // ¿Tren parado? Por la POSICIÓN (independiente del idioma de OR): sin moverse más de ~0,8 m entre
        // sondeos durante al menos 3 s. Respaldo: velocidad del HUD de OR (solo si está en inglés, «Speed»).
        DateTime? _stoppedSinceUtc;
        bool TrainStoppedByPosition(double minSeconds = 3) =>
            _stoppedSinceUtc != null && (DateTime.UtcNow - _stoppedSinceUtc.Value).TotalSeconds >= minSeconds;

        async Task<int> FetchGameHour()
        {
            try
            {
                string txt = await _kmHttp.GetStringAsync("/API/TIME");
                if (double.TryParse(txt.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out double secs))
                    return ((int)(secs / 3600.0)) % 24;
            }
            catch { }
            return 12;
        }

        async Task PollKm()
        {
            if (OrControl.Quiet) return;   // hay una orden del pupitre en marcha: no molestar al simulador
            try
            {
                var txt = await _kmHttp.GetStringAsync("/API/MAP/");
                if (string.IsNullOrWhiteSpace(txt) || txt == "null") return;
                using var d = JsonDocument.Parse(txt);
                if (!d.RootElement.TryGetProperty("LatLon", out var ll)) return;
                double lat = ll.GetProperty("Lat").GetDouble();
                double lon = ll.GetProperty("Lon").GetDouble();
                if (lat == 0 && lon == 0) return;
                // Primera posición válida = el escenario ya está abierto (OR no la da durante la carga):
                // aquí arranca el cronómetro del servicio.
                if (_svcClockUtc == null) _svcClockUtc = DateTime.UtcNow;
                if (!_scenarioReady) { _scenarioReady = true; OnScenarioReady(); }   // y aquí aparecen los HUD
                if (_tHave)
                {
                    double dm = Haversine(_tLat, _tLon, lat, lon);
                    if (dm >= 0.5 && dm < 3000) _trackedMeters += dm;   // ignora jitter y saltos/teleports
                    // Parado = casi sin desplazamiento entre sondeos (~1,5 s): < 0,8 m ≈ < 2 km/h.
                    if (dm < 0.8) { if (_stoppedSinceUtc == null) _stoppedSinceUtc = DateTime.UtcNow; }
                    else _stoppedSinceUtc = null;
                }
                SmoothFix(lat, lon);   // mapas del HUD: la marca del tren se desliza hasta aquí
                _tLat = lat; _tLon = lon; _tHave = true;
                if (CabHudAlive) _cabHud.SetPosition(lat, lon);   // pupitre: para saber si es de noche
                if (_paxActive && _paxStations.Count > 0) await PollPax(lat, lon);   // embarque de viajeros
            }
            catch { }   // servidor aún no listo, pausa, etc.
        }

        // ---- Movimiento suave del tren en los mapas del HUD ----
        // OR da la posición cada 1,5 s; en vez de saltar de una a otra, la marca del tren recorre
        // en línea recta el tramo entre la posición que estaba mostrando y la nueva, en el mismo
        // tiempo que tardó en llegar la lectura. Va un sondeo por detrás (a 100 km/h, unos 40 m,
        // que en el mapa no se aprecian) a cambio de moverse de forma continua.
        readonly System.Diagnostics.Stopwatch _smClock = System.Diagnostics.Stopwatch.StartNew();
        double _smFromLat, _smFromLon, _smToLat, _smToLon, _smT0, _smDur = 1.5, _smLastFix;
        bool _smHave;

        void SmoothFix(double lat, double lon)
        {
            double now = _smClock.Elapsed.TotalSeconds;
            var (_, curLat, curLon) = SmoothPos();
            // Primera posición o salto (teletransporte, cambio de tren): directo, sin deslizar.
            if (!_smHave || Haversine(curLat, curLon, lat, lon) > 3000)
            {
                _smFromLat = _smToLat = lat; _smFromLon = _smToLon = lon; _smHave = true;
            }
            else
            {
                _smFromLat = curLat; _smFromLon = curLon; _smToLat = lat; _smToLon = lon;
                _smDur = Math.Max(0.3, Math.Min(3.0, now - _smLastFix));
            }
            _smT0 = now; _smLastFix = now;
        }

        (bool has, double lat, double lon) SmoothPos()
        {
            if (!_smHave) return (_tHave, _tLat, _tLon);
            double u = Math.Min(1, (_smClock.Elapsed.TotalSeconds - _smT0) / _smDur);
            return (true, _smFromLat + (_smToLat - _smFromLat) * u, _smFromLon + (_smToLon - _smFromLon) * u);
        }

        double StopKmTracking()
        {
            _smHave = false;
            _paxActive = false; _paxWanted = false;
            EndPaxAnimation();
            PaDriveStop();   // megafonía: corta lo que suene y olvida la ruta
            try { _kmTimer?.Stop(); _kmTimer?.Dispose(); _kmTimer = null; } catch { }
            try { _kmHttp?.Dispose(); _kmHttp = null; } catch { }
            return Math.Round(_trackedMeters / 1000.0, 1);
        }

        // Duración en formato horas para listas: "0h 02m" / "1h 05m".
        static string FmtDurShort(double seconds)
        {
            int s = (int)Math.Max(0, seconds);
            int h = s / 3600, m = (s % 3600) / 60;
            return $"{h}h {m:00}m";
        }

        // Fecha ISO ("AAAA-MM-DD…") → "DD-MM-AAAA".
        static string FmtDate(string iso)
        {
            if (string.IsNullOrEmpty(iso) || iso.Length < 10) return iso ?? "";
            var d = iso.Substring(0, 10);
            return d.Length == 10 && d[4] == '-' && d[7] == '-'
                ? d.Substring(8, 2) + "-" + d.Substring(5, 2) + "-" + d.Substring(0, 4)
                : d;
        }

        static double Haversine(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000.0;
            double dLat = (lat2 - lat1) * Math.PI / 180.0, dLon = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                     + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * R * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
        }

        // ============================ Mi perfil (estadísticas privadas) ============================
        // Agrega TODA mi conducción (servicios de empresa donde soy maquinista), solo viajes válidos,
        // y rellena KPIs y gráficas (trenes/trayectos).
        async void LoadProfile()
        {
            if (!Supa.IsLoggedIn) return;

            var trainCount = new Dictionary<string, double>();
            var routeCount = new Dictionary<string, double>();
            double totalKm = 0, totalS = 0, totalPax = 0; int trips = 0, invalid = 0;

            void Tally(string route, string consist, double km, double dur, double pax, bool valid, bool completed)
            {
                if (completed && !valid) invalid++;
                if (!valid || !completed) return;
                trips++; totalKm += km; totalS += dur; totalPax += pax;
                if (!string.IsNullOrWhiteSpace(consist)) trainCount[consist] = trainCount.TryGetValue(consist, out var a) ? a + 1 : 1;
                if (!string.IsNullOrWhiteSpace(route)) routeCount[route] = routeCount.TryGetValue(route, out var b) ? b + 1 : 1;
            }

            // Servicios de empresa donde soy el maquinista
            var (sj, se) = await Supa.SelectAsync(
                $"services?select=route,consist,km,duration_s,pax,status,validated&driver_id=eq.{Uri.EscapeDataString(Supa.UserId ?? "")}&limit=1000");
            if (se == null && sj != null)
            {
                try
                {
                    using var d = JsonDocument.Parse(sj);
                    foreach (var e in d.RootElement.EnumerateArray())
                        Tally(Str(e, "route"), Str(e, "consist"), Num(e, "km"), Num(e, "duration_s"), Num(e, "pax"),
                              !e.TryGetProperty("validated", out var vv) || vv.ValueKind != JsonValueKind.False,
                              Str(e, "status") == "completed");
                }
                catch { }
            }

            // KPIs
            double hours = totalS / 3600.0;
            double avg = totalS > 0 ? totalKm / hours : 0;
            SetKpi(_profKmVal, totalKm.ToString("N0", EsEs) + " km");
            SetKpi(_profTripsVal, trips.ToString());
            SetKpi(_profTimeVal, hours.ToString("N1", EsEs) + " h");
            SetKpi(_profSpeedVal, avg.ToString("N0", EsEs) + " km/h");
            SetKpi(_profPaxVal, totalPax.ToString("N0", EsEs));

            UpdateRankAndBadges(totalKm, trips, hours, invalid, routeCount.Count, totalPax, trainCount.Count, avg);

            // Gráficas (top 6)
            _profTrains?.SetData(TopN(trainCount, 6));
            _profRoutes?.SetData(TopN(routeCount, 6));
        }

        // Escala de rangos por km VÁLIDOS conducidos.
        static readonly (double km, string name)[] RankTiers =
        {
            (0, "Aprendiz"), (150, "Ayudante de Maquinista"), (500, "Maquinista"),
            (1200, "Maquinista de Primera"), (2500, "Veterano"), (5000, "Especialista"),
            (10000, "Experto"), (17500, "Instructor"), (25000, "Maestro"),
            (35000, "Jefe de Tren"), (50000, "Jefe de Maquinistas"), (70000, "Inspector de Tracción"),
            (100000, "Leyenda"), (150000, "Ícono del Riel"), (250000, "Gran Maestro"),
            (400000, "Maestro Mayor"), (600000, "Mito Ferroviario"), (1000000, "Inmortal del Riel"),
        };

        // Índice del rango (RankTiers) alcanzado con esos km válidos.
        static int RankTierIndex(double km)
        {
            int tier = 0;
            for (int i = 0; i < RankTiers.Length; i++) if (km >= RankTiers[i].km) tier = i;
            return tier;
        }

        void UpdateRankAndBadges(double km, int trips, double hours, int invalid, int distinctRoutes,
                                 double pax = 0, int distinctTrains = 0, double avgKmh = 0)
        {
            // Rango: mayor tramo alcanzado + progreso al siguiente.
            int tier = RankTierIndex(km);
            if (_rankName != null) _rankName.Text = Tr(RankTiers[tier].name);
            if (_rankStep != null) _rankStep.Text = string.Format(Tr("escalón {0} de {1}"), tier + 1, RankTiers.Length);
            if (tier < RankTiers.Length - 1)
            {
                double from = RankTiers[tier].km, to = RankTiers[tier + 1].km;
                _rankPct = to > from ? (km - from) / (to - from) : 1;
                if (_rankNext != null) _rankNext.Text = string.Format(Tr("Siguiente: {0}  ·  te faltan {1} km  ({2} %)"),
                    Tr(RankTiers[tier + 1].name), Math.Max(0, to - km).ToString("N0", EsEs),
                    (Math.Max(0, Math.Min(1, _rankPct)) * 100).ToString("N0", EsEs));
            }
            else { _rankPct = 1; if (_rankNext != null) _rankNext.Text = Tr("¡Rango máximo alcanzado!"); }
            _rankTrack?.Invalidate();

            // Insignias (cada una con su color)
            if (_badges != null)
            {
                _badges.Controls.Clear();
                void Badge(string icon, string text, bool earned, Color color) => _badges.Controls.Add(BadgeChip(icon, text, earned, color));
                // Servicios
                Badge("🚂", Tr("Primer viaje"), trips >= 1, ColBlue);
                Badge("🏅", Tr("10 servicios"), trips >= 10, ColGold);
                Badge("🎖️", Tr("50 servicios"), trips >= 50, ColGold);
                Badge("🏆", Tr("100 servicios"), trips >= 100, ColOrange);
                Badge("👑", Tr("250 servicios"), trips >= 250, ColOrange);
                Badge("💎", Tr("500 servicios"), trips >= 500, ColViolet);
                Badge("🌟", Tr("1.000 servicios"), trips >= 1000, ColGold);
                // Distancia
                Badge("📐", Tr("100 km"), km >= 100, Theme.Accent);
                Badge("📏", Tr("1.000 km"), km >= 1000, Theme.Accent);
                Badge("🌍", Tr("10.000 km"), km >= 10000, ColTeal);
                Badge("🛰", Tr("50.000 km"), km >= 50000, ColTeal);
                Badge("🚀", Tr("100.000 km"), km >= 100000, ColBlue);
                // Tiempo al mando
                Badge("⏱", Tr("10 horas"), hours >= 10, ColViolet);
                Badge("🕰", Tr("100 horas"), hours >= 100, ColViolet);
                Badge("📅", Tr("500 horas"), hours >= 500, ColViolet);
                Badge("♾", Tr("1.000 horas"), hours >= 1000, ColGold);
                // Rutas y material
                Badge("🗺", Tr("Explorador (5 rutas)"), distinctRoutes >= 5, ColOrange);
                Badge("🧭", Tr("Explorador (10 rutas)"), distinctRoutes >= 10, ColOrange);
                Badge("🌐", Tr("Explorador (25 rutas)"), distinctRoutes >= 25, ColTeal);
                Badge("🚆", Tr("10 trenes distintos"), distinctTrains >= 10, ColBlue);
                Badge("🚄", Tr("25 trenes distintos"), distinctTrains >= 25, ColBlue);
                // Viajeros
                Badge("🧍", Tr("1.000 viajeros"), pax >= 1000, ColTeal);
                Badge("👥", Tr("10.000 viajeros"), pax >= 10000, ColTeal);
                Badge("🏙", Tr("100.000 viajeros"), pax >= 100000, ColGold);
                // Estilo de conducción
                Badge("✅", Tr("Conducción limpia"), trips >= 5 && invalid == 0, Theme.Accent);
                Badge("🛡", Tr("Impecable (50 servicios)"), trips >= 50 && invalid == 0, Theme.Accent);
                Badge("⚡", Tr("Alta velocidad (media 100 km/h)"), trips >= 10 && avgKmh >= 100, ColOrange);
            }
        }

        // Insignia: pastilla con icono a color (blanco sobre disco del color) + texto.
        // Conseguida = disco a color + fondo teñido + texto claro; bloqueada = disco gris + texto atenuado.
        static Control BadgeChip(string icon, string text, bool earned, Color color)
        {
            var dotColor = earned ? color : Blend(Theme.Surface2, Theme.Bg, 0.3f);
            var card = new Card
            {
                Fill = earned ? Blend(Theme.Surface, color, 0.14f) : Theme.Surface,
                BorderColor = earned ? Blend(color, Theme.Bg, 0.15f) : Blend(Theme.Surface, Theme.Bg, 0.35f),
                Radius = 11, AutoSize = false, Height = 40, Margin = new Padding(0, 0, 8, 8), Padding = new Padding(7, 6, 12, 6)
            };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var dot = IconDot(icon, dotColor, 26); dot.Anchor = AnchorStyles.Left;
            var lbl = new Label
            {
                Text = text, AutoSize = true, Anchor = AnchorStyles.Left,
                ForeColor = earned ? Theme.Text : Theme.Subtle,
                Font = Theme.Font(9f, earned ? FontStyle.Bold : FontStyle.Regular),
                Margin = new Padding(6, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft
            };
            grid.Controls.Add(dot, 0, 0); grid.Controls.Add(lbl, 1, 0);
            card.Controls.Add(grid);
            card.Width = 30 + 6 + TextRenderer.MeasureText(text, lbl.Font).Width + 24;
            return card;
        }

        // ============================ Usuarios (solo superadmin) ============================
        Panel BuildUsersSubpanel()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // filtro/intro
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // tabla
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // botón
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // msg

            _usersList = EmpTable();
            t.Controls.Add(EmpSearch(_usersList, 300));
            _usersList.SetColumns(
                new StyledTable.Col("USUARIO", 200),
                new StyledTable.Col("ID", 0, true),
                new StyledTable.Col("EMPRESAS", 110, false, HorizontalAlignment.Right));
            // Clic en la celda del ID → copia el ID al portapapeles.
            _usersList.MouseClick += (s, e) =>
            {
                var hit = _usersList.HitTest(e.Location);
                if (hit.Item == null || hit.SubItem == null) return;
                if (hit.Item.SubItems.IndexOf(hit.SubItem) == 1) CopyIdToClipboard(hit.SubItem.Text);
            };
            _usersList.Cursor = Cursors.Hand;
            t.Controls.Add(_usersList);

            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(2, 4, 2, 2) };
            var copyBtn = EmpButton(Tr("⧉  Copiar ID")); copyBtn.Width = 160; copyBtn.Margin = new Padding(0, 0, 8, 0);
            copyBtn.Click += (s, e) => { int i = _usersList.SelectedRow; if (i >= 0 && i < _userIds.Count) CopyIdToClipboard(_userIds[i]); else Msg(_usersMsg, Tr("Selecciona un usuario de la lista."), true); };
            var delBtn = EmpButton(Tr("Eliminar acceso")); delBtn.Width = 200;
            delBtn.BaseColor = Theme.Surface2; delBtn.HoverColor = Color.FromArgb(150, 60, 60); delBtn.TextColor = RedC;
            delBtn.Click += (s, e) => DeleteUserAccess();
            btns.Controls.Add(copyBtn); btns.Controls.Add(delBtn);
            t.Controls.Add(btns);

            _usersMsg = EmpMsg(); t.Controls.Add(_usersMsg);
            return t;
        }

        async void LoadUsers()
        {
            if (_usersList == null || !Supa.IsSuperadmin) return;
            _usersList.SetEmpty(Tr("Cargando…"));
            var (json, err) = await Supa.RpcAsync("list_all_users", new { });
            _usersList.ClearRows(); _userIds.Clear(); _userIsSelf.Clear();
            if (err != null) { _usersList.SetEmpty(Tr("Error: ") + err); return; }
            int n = 0;
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    n++;
                    string uid = Str(e, "user_id");
                    _userIds.Add(uid);
                    bool self = e.TryGetProperty("is_self", out var sv) && sv.ValueKind == JsonValueKind.True;
                    _userIsSelf.Add(self);
                    string user = Str(e, "username"); if (user.Length == 0) user = "—";
                    if (self) user += "  ★";
                    double nco = Num(e, "company_count");
                    _usersList.AddRow(new[] { user, uid, nco.ToString("N0", EsEs) },
                        new Color?[] { self ? Theme.Accent : (Color?)null, Theme.Subtle, null });
                }
            }
            catch { }
            if (n == 0) _usersList.SetEmpty(Tr("No hay usuarios."));
        }

        async void DeleteUserAccess()
        {
            if (!Supa.IsSuperadmin || _usersList == null) return;
            int i = _usersList.SelectedRow;
            if (i < 0 || i >= _userIds.Count) { Msg(_usersMsg, Tr("Selecciona un usuario de la lista."), true); return; }
            if (i < _userIsSelf.Count && _userIsSelf[i]) { Msg(_usersMsg, Tr("No puedes eliminar tu propio acceso."), true); return; }
            if (MessageBox.Show(this,
                    Tr("¿Eliminar el acceso de este usuario? Se borrará su cuenta, su perfil, sus membresías, solicitudes y servicios. Esta acción no se puede deshacer."),
                    "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Msg(_usersMsg, Tr("Eliminando acceso…"), false);
            var (_, err) = await Supa.RpcAsync("delete_user_access", new { p_user = _userIds[i] });
            if (err != null) { Msg(_usersMsg, Tr("No se pudo eliminar: ") + err, true); return; }
            Msg(_usersMsg, Tr("Acceso eliminado."), false);
            LoadUsers();
        }

        // ============================ Todas las empresas (superadmin) ============================
        // Sección de administración: lista TODAS las empresas con nº de socios/servicios y saldo,
        // permite ABRIR una (acceso completo con todos los permisos) o ELIMINARLA.
        Panel BuildAllCompaniesSubpanel()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // cabecera
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // filtro
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // tabla
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // botones
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // msg

            t.Controls.Add(EmpHeader("TODAS LAS EMPRESAS (ADMINISTRACIÓN)"));
            _allCompList = EmpTable();
            t.Controls.Add(EmpSearch(_allCompList, 300));
            _allCompList.SetColumns(
                new StyledTable.Col("EMPRESA", 220),
                new StyledTable.Col("GERENTE", 0, true),
                new StyledTable.Col("SOCIOS", 90, false, HorizontalAlignment.Right),
                new StyledTable.Col("SERVICIOS", 100, false, HorizontalAlignment.Right),
                new StyledTable.Col("SALDO", 130, false, HorizontalAlignment.Right));
            t.Controls.Add(_allCompList);

            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0) };
            var openBtn = EmpButton(Tr("Abrir empresa"), primary: true); openBtn.Width = 200;
            openBtn.Click += (s, e) => OpenCompanyFromAll();
            var delBtn = EmpButton(Tr("Eliminar empresa")); delBtn.Width = 200;
            delBtn.BaseColor = Theme.Surface2; delBtn.HoverColor = Color.FromArgb(150, 60, 60); delBtn.TextColor = RedC;
            delBtn.Click += (s, e) => DeleteCompanyFromAll();
            btns.Controls.Add(openBtn); btns.Controls.Add(delBtn);
            t.Controls.Add(btns);

            _allCompMsg = EmpMsg(); t.Controls.Add(_allCompMsg);
            return t;
        }

        async void LoadAllCompanies()
        {
            if (_allCompList == null || !Supa.IsSuperadmin) return;
            _allCompList.SetEmpty(Tr("Cargando…"));
            var (json, err) = await Supa.RpcAsync("list_all_companies", new { });
            _allCompList.ClearRows(); _allCompIds.Clear();
            if (err != null) { _allCompList.SetEmpty(Tr("Error: ") + err); return; }
            int n = 0;
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    n++;
                    _allCompIds.Add(Str(e, "id"));
                    string name = Str(e, "name"); if (name.Length == 0) name = "—";
                    string owner = Str(e, "owner_username"); if (owner.Length == 0) owner = "—";
                    double mem = Num(e, "member_count"), svc = Num(e, "service_count"), bal = Num(e, "balance");
                    _allCompList.AddRow(
                        new[] { name, owner, mem.ToString("N0", EsEs), svc.ToString("N0", EsEs), bal.ToString("N2", EsEs) + " €" },
                        new Color?[] { null, Theme.Subtle, null, null, bal < 0 ? RedC : Theme.Accent });
                }
            }
            catch { }
            if (n == 0) _allCompList.SetEmpty(Tr("No hay empresas."));
        }

        // Abre la empresa seleccionada en el panel (con acceso completo): la selecciona en la
        // lista de la izquierda (la RLS trae TODAS las empresas para el superadmin) y va a Servicios.
        void OpenCompanyFromAll()
        {
            if (!Supa.IsSuperadmin || _allCompList == null) return;
            int i = _allCompList.SelectedRow;
            if (i < 0 || i >= _allCompIds.Count) { Msg(_allCompMsg, Tr("Selecciona una empresa de la lista."), true); return; }
            string id = _allCompIds[i];
            int idx = _empCompanies.FindIndex(c => c.Id == id);
            if (idx < 0) { Msg(_allCompMsg, Tr("No se pudo abrir; recargando la lista…"), true); LoadCompanies(); return; }
            Msg(_allCompMsg, "", false);
            if (_empCoCombo.SelectedIndex == idx) OnCompanySelected();   // mismo índice: recargar
            else _empCoCombo.SelectedIndex = idx;   // dispara OnCompanySelected → carga datos y permisos
            ShowSubtab(0);
        }

        // Elimina por completo la empresa seleccionada (delete_company, solo superadmin).
        async void DeleteCompanyFromAll()
        {
            if (!Supa.IsSuperadmin || _allCompList == null) return;
            int i = _allCompList.SelectedRow;
            if (i < 0 || i >= _allCompIds.Count) { Msg(_allCompMsg, Tr("Selecciona una empresa de la lista."), true); return; }
            string id = _allCompIds[i];
            string name = _empCompanies.Find(c => c.Id == id)?.Name ?? "—";
            if (MessageBox.Show(this,
                    string.Format(Tr("¿Eliminar la empresa «{0}» y TODOS sus datos (socios, servicios y banca)? Esta acción no se puede deshacer."), name),
                    "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Msg(_allCompMsg, Tr("Eliminando empresa…"), false);
            var (_, err) = await Supa.RpcAsync("delete_company", new { p_company = id });
            if (err != null) { Msg(_allCompMsg, Tr("No se pudo eliminar: ") + err, true); return; }
            if (_empSel != null && _empSel.Id == id) _empSel = null;
            Msg(_allCompMsg, string.Format(Tr("Empresa «{0}» eliminada."), name), false);
            LoadAllCompanies();
            LoadCompanies();
        }

        // ============================ Flota (vehículos por .eng) ============================
        // Solo la tabla de la flota (mantenimiento, propiedad, estado) y sus acciones — comprar/
        // alquilar vive aparte, en BuildBuySubpanel, para que ninguna de las dos vaya apretada.
        Panel BuildFleetSubpanel()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // cabecera
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // filtro + tabla + acciones — todo el espacio libre
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // msg

            _fleetHeaderLbl = EmpHeader("FLOTA DE LA EMPRESA");
            t.Controls.Add(_fleetHeaderLbl);

            var fleetCard = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, BorderColor = Blend(Theme.Surface, Theme.Bg, 0.5f), Radius = 12, Padding = new Padding(10, 10, 10, 10), Margin = new Padding(2, 2, 2, 4) };

            // DOS COLUMNAS (como Compra): IZQUIERDA buscador + tabla de la flota · DERECHA vista 3D + ficha + acciones.
            var fmain = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface };
            fmain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            fmain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

            // --- IZQUIERDA: buscador + tabla ---
            var fleetGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Surface, Margin = new Padding(0, 0, 12, 0) };
            fleetGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fleetGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // filtro
            fleetGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // tabla

            _fleetList = EmpTable();
            _fleetList.SetColumns(
                new StyledTable.Col("MATRÍCULA", 120),
                new StyledTable.Col("VEHÍCULO", 150),
                new StyledTable.Col("MODELO", 0, true),
                new StyledTable.Col("ESTADO", 230));   // cabe «No operativo · mantenimiento»
            _fleetList.SelectedIndexChanged += (s, e) => OnFleetVehicleSelected();
            var fleetSearch = EmpSearch(_fleetList, 360); fleetSearch.Margin = new Padding(0, 0, 0, 8);
            fleetGrid.Controls.Add(fleetSearch, 0, 0);
            fleetGrid.Controls.Add(_fleetList, 0, 1);
            fmain.Controls.Add(fleetGrid, 0, 0);

            // --- DERECHA: vista 3D + ficha del vehículo + acciones ---
            var fright = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Surface };
            fright.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fright.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // vista 3D (se queda todo el hueco)
            fright.RowStyles.Add(new RowStyle(SizeType.AutoSize));         // título
            fright.RowStyles.Add(new RowStyle(SizeType.AutoSize));         // ficha (chips)
            fright.RowStyles.Add(new RowStyle(SizeType.AutoSize));         // acciones

            var fviewport = new Card { Dock = DockStyle.Fill, Fill = Theme.Bg, BorderColor = Blend(Theme.Surface, Theme.Bg, 0.35f), Radius = 10, Padding = new Padding(4), Margin = new Padding(0, 0, 0, 8) };
            _fleetOwnPreview = new TrainPreviewPanel { Dock = DockStyle.Fill };
            _fleetOwnPreview.Dragged += OnFleetOwnDrag;
            _fleetOwnPreview.ResetRequested += OnFleetOwnReset;
            _fleetOwnPreview.Zoomed += () => { if (_fleetOwnGeom != null) RenderFleetOwnLive(); };
            _fleetOwnPreview.Resize += (s, e) => { if (_fleetOwnGeom != null) RenderFleetOwnLive(); };
            fviewport.Controls.Add(_fleetOwnPreview);
            fright.Controls.Add(fviewport, 0, 0);

            _fleetOwnTitle = new Label { Text = Tr("Elige un vehículo"), AutoSize = true, ForeColor = Theme.Accent, Font = Theme.Font(12.5f, FontStyle.Bold), Margin = new Padding(1, 0, 0, 6) };
            fright.Controls.Add(_fleetOwnTitle, 0, 1);

            var fchips = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 3, BackColor = Theme.Surface };
            fchips.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            fchips.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            fchips.Controls.Add(FleetChip("TIPO", out _voKind), 0, 0);
            fchips.Controls.Add(FleetChip("ESTADO", out _voEstado), 1, 0);
            fchips.Controls.Add(FleetChip("PROPIEDAD", out _voProp), 0, 1);
            fchips.Controls.Add(FleetChip("MANTENIMIENTO", out _voMaint), 1, 1);
            // Las plazas salen aparte: así TIPO es corto y se lee al mismo tamaño que los demás.
            fchips.Controls.Add(FleetChip("PLAZAS", out _voCap), 0, 2);
            fchips.Controls.Add(FleetChip("TRACCIÓN", out _voTraction), 1, 2);
            var fchipsHost = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Surface };
            fchipsHost.Controls.Add(fchips);
            fright.Controls.Add(fchipsHost, 0, 2);

            var fbtns = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 44, ColumnCount = 3, RowCount = 1, BackColor = Theme.Surface, Margin = new Padding(0, 8, 0, 0) };
            fbtns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            fbtns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            fbtns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            _fleetServiceBtn = EmpButton(Tr("Llevar al taller"), primary: true); _fleetServiceBtn.Dock = DockStyle.Fill; _fleetServiceBtn.Height = 44; _fleetServiceBtn.Margin = new Padding(0, 0, 6, 0);
            _fleetServiceBtn.Click += (s, e) => ServiceVehicleUi();
            // Matrícula de la unidad (solo gerente / gestor / superadmin).
            _fleetPlateBtn = EmpButton(Tr("Asignar matrícula")); _fleetPlateBtn.Dock = DockStyle.Fill; _fleetPlateBtn.Height = 44; _fleetPlateBtn.Margin = new Padding(6, 0, 6, 0);
            _fleetPlateBtn.BaseColor = Theme.Surface2; _fleetPlateBtn.HoverColor = Theme.SurfaceHi; _fleetPlateBtn.TextColor = Theme.Text;
            _fleetPlateBtn.Click += (s, e) => AssignPlateUi();
            _fleetRemoveBtn = EmpButton(Tr("Dar de baja")); _fleetRemoveBtn.Dock = DockStyle.Fill; _fleetRemoveBtn.Height = 44; _fleetRemoveBtn.Margin = new Padding(6, 0, 0, 0);
            _fleetRemoveBtn.BaseColor = Theme.Surface2; _fleetRemoveBtn.HoverColor = Color.FromArgb(150, 60, 60); _fleetRemoveBtn.TextColor = RedC;
            _fleetRemoveBtn.Click += (s, e) => RemoveVehicle();
            fbtns.Controls.Add(_fleetServiceBtn, 0, 0); fbtns.Controls.Add(_fleetPlateBtn, 1, 0); fbtns.Controls.Add(_fleetRemoveBtn, 2, 0);
            fright.Controls.Add(fbtns, 0, 3);

            fmain.Controls.Add(fright, 1, 0);
            fleetCard.Controls.Add(fmain);
            t.Controls.Add(fleetCard);

            _fleetMsg = EmpMsg(); t.Controls.Add(_fleetMsg);
            return t;
        }

        // ============================ Compra / alquiler (showroom) ============================
        // Página propia (con scroll: nunca recorta nada, sea cual sea el tamaño de ventana o lo
        // largo que sea el desglose). Se elige un CONSIST completo del contenido; al comprarlo se
        // da de alta cada máquina de tracción que contiene que la empresa aún no tenga (guardada en
        // la tabla vehicles por su .eng) — las que ya tenía no se cobran, y quedan reutilizables en
        // cualquier otro consist que las lleve enganchadas.
        Panel BuildBuySubpanel()
        {
            var scroll = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

            // Sin rótulo propio (el título de sección ya dice «Compra»): showroom + mensaje.
            // OJO: un control INVISIBLE en un TableLayoutPanel no ocupa celda al autocolocar, así
            // que no se añade (si no, el showroom caería en una fila AutoSize y quedaría aplastado).
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // showroom (rellena la altura)
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // msg

            // ---- Showroom a dos columnas (lista · visor+ficha). ----
            var shop = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, BorderColor = Blend(Theme.Surface, Theme.Bg, 0.5f), Radius = 12, Padding = new Padding(14, 12, 14, 12), Margin = new Padding(2, 4, 2, 2) };
            // DOS COLUMNAS (aprovecha todo el espacio): IZQUIERDA = buscador + lista de trenes que
            // rellena la altura · DERECHA = visor 3D + ficha técnica + botones comprar/alquilar.
            var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));

            // --- IZQUIERDA: buscador + lista de trenes (rellena toda la altura disponible) ---
            var pickerHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Surface, Margin = new Padding(0, 0, 12, 0) };
            pickerHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pickerHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pickerHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            // Una sola lista: las MÁQUINAS (.eng) del contenido. Los trenes (.con) los tiene cada
            // uno distintos; las máquinas suelen ser las mismas, así que la compra va por máquina.
            _fleetConsistSearch = new RoundedInput(I18n.T("🔎  Buscar máquina por nombre o carpeta…")) { Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 6) };
            _fleetConsistSearch.Box.TextChanged += (s, e) => FilterBuyList();
            pickerHost.Controls.Add(_fleetConsistSearch, 0, 0);

            // La lista de trenes (.con) ya no se muestra, pero se sigue creando: el desglose de la
            // tasación y el aviso de «ya la tienes» trabajan sobre los consists del contenido.
            _fleetConsistList = MakeListBox(Theme.Surface2, 30);

            _fleetEngList = MakeListBox(Theme.Surface2, 46);
            _fleetEngList.DrawMode = DrawMode.OwnerDrawVariable;
            _fleetEngList.MeasureItem += (s, e) =>
                e.ItemHeight = (e.Index >= 0 && e.Index < _fleetEngList.Items.Count && _fleetEngList.Items[e.Index] is BuyGroupHeader) ? 34 : 46;
            _fleetEngList.DrawItem += DrawBuyMachineItem;
            _fleetEngList.SelectedIndexChanged += (s, e) =>
            {
                if (SkipBuyHeader()) return;   // las cabeceras no se seleccionan
                RenderBuyMachinePreview(); UpdateMachineValuation();
            };
            var engCard = WrapCard(_fleetEngList); engCard.Dock = DockStyle.Fill;
            _buyEngCard = engCard;
            pickerHost.Controls.Add(engCard, 0, 1);
            main.Controls.Add(pickerHost, 0, 0);

            // --- DERECHA: visor 3D (arriba) + ficha (rellena) + botones (abajo) ---
            // El VISOR 3D es el elemento flexible (Percent): absorbe todo el espacio sobrante. La ficha
            // (tipo + chips + desglose + precios) y los botones van con alturas AutoSize/fijas, así SIEMPRE
            // caben enteros y los precios nunca pueden solaparse con los chips por falta de sitio.
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Surface };
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // visor 3D (flexible: se ajusta al hueco)
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));         // ficha técnica (altura determinista)
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));         // botones comprar/alquilar
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));         // compra masiva (superadmin)

            var viewport = new Card { Dock = DockStyle.Fill, Fill = Theme.Bg, BorderColor = Blend(Theme.Surface, Theme.Bg, 0.35f), Radius = 10, Padding = new Padding(4), Margin = new Padding(0, 0, 0, 8) };
            _fleetPreview = new TrainPreviewPanel { Dock = DockStyle.Fill };
            _fleetPreview.Dragged += OnFleetPreviewDrag;
            _fleetPreview.ResetRequested += OnFleetPreviewReset;
            _fleetPreview.Zoomed += () => { if (_fleetGeom != null) RenderFleetLive(); };
            _fleetRerender = new System.Windows.Forms.Timer { Interval = 140 };
            _fleetRerender.Tick += (s, e) => { _fleetRerender.Stop(); RenderFleetLive(); };
            _fleetPreview.Resize += (s, e) => { if (_fleetGeom != null) { _fleetRerender.Stop(); _fleetRerender.Start(); } };
            viewport.Controls.Add(_fleetPreview);
            right.Controls.Add(viewport, 0, 0);

            // Ficha técnica AutoSize: tipo + chips (fijo) + desglose (fijo) + precios. Alturas deterministas
            // → la ficha completa siempre se ve entera y los precios quedan SIEMPRE bajo los chips.
            var spec = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 3, BackColor = Theme.Surface };
            spec.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            spec.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // tipo
            spec.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));  // chips (alto holgado: nunca se recorta)
            spec.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // precios

            var typeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 4) };
            _fleetSpecType = new Label { Text = Tr("Elige una máquina"), AutoSize = true, ForeColor = Theme.Accent, Font = Theme.Font(12.5f, FontStyle.Bold), Margin = new Padding(1, 4, 0, 0) };
            _fleetOwnedBadge = new Label { Text = "  ✓ " + Tr("Ya en tu flota") + "  ", AutoSize = true, ForeColor = Color.White, BackColor = Theme.Accent, Font = Theme.Font(9f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(2, 3, 2, 3), Margin = new Padding(12, 3, 0, 0), Visible = false };
            typeRow.Controls.Add(_fleetSpecType); typeRow.Controls.Add(_fleetOwnedBadge);
            spec.Controls.Add(typeRow, 0, 0);

            // 4 columnas × 2 filas (7 chips): más compacto en vertical y sin recortes.
            var chips = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, BackColor = Theme.Surface };
            for (int ci = 0; ci < 4; ci++) chips.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            chips.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            chips.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            chips.Controls.Add(FleetChip("POTENCIA", out _vPower), 0, 0);
            chips.Controls.Add(FleetChip("VEL. MÁX.", out _vSpeed), 1, 0);
            chips.Controls.Add(FleetChip("PLAZAS", out _vPlazas), 2, 0);
            chips.Controls.Add(FleetChip("CONFORT", out _vConfort), 3, 0);
            chips.Controls.Add(FleetChip("MASA", out _vMasa), 0, 1);
            chips.Controls.Add(FleetChip("FRENO", out _vFreno), 1, 1);
            chips.Controls.Add(FleetChip("DENSIDAD", out _vDensity), 2, 1);
            spec.Controls.Add(chips, 0, 1);

            // (sin línea divisoria: dejaba una raya blanca fea encima de los precios)
            var prices = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface, Margin = new Padding(0, 8, 0, 0) };
            prices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            prices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            prices.Controls.Add(PriceChip(Tr("COMPRA (nuevas)"), Theme.Accent, out _vBuy), 0, 0);
            prices.Controls.Add(PriceChip(Tr("ALQUILER / SERV. (nuevas)"), Color.FromArgb(96, 165, 250), out _vRent), 1, 0);
            spec.Controls.Add(prices, 0, 2);

            right.Controls.Add(spec, 0, 1);

            var buyRow = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 44, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface, Margin = new Padding(0, 10, 0, 0) };
            buyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            buyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            _fleetBuyBtn = EmpButton(Tr("Comprar"), primary: true); _fleetBuyBtn.Dock = DockStyle.Fill; _fleetBuyBtn.Height = 44; _fleetBuyBtn.Margin = new Padding(0, 0, 6, 0);
            _fleetBuyBtn.Click += (s, e) => { if (_buyByMachine) AcquireMachineUi(false); else BuyConsist(); };
            _fleetRentBtn = EmpButton(Tr("Alquilar")); _fleetRentBtn.Dock = DockStyle.Fill; _fleetRentBtn.Height = 44; _fleetRentBtn.Margin = new Padding(6, 0, 0, 0);
            _fleetRentBtn.Click += (s, e) => { if (_buyByMachine) AcquireMachineUi(true); else RentConsist(); };
            buyRow.Controls.Add(_fleetBuyBtn, 0, 0);
            buyRow.Controls.Add(_fleetRentBtn, 1, 0);
            right.Controls.Add(buyRow, 0, 2);

            // Compra masiva: compra de una tacada todas las máquinas que estén en la lista y aún no
            // tenga la empresa. Es una herramienta de superadmin (mueve mucho dinero de una vez).
            _fleetBuyAllBtn = EmpButton(Tr("Comprar TODAS las de la lista"));
            _fleetBuyAllBtn.Dock = DockStyle.Fill; _fleetBuyAllBtn.Height = 40;
            _fleetBuyAllBtn.Margin = new Padding(0, 8, 0, 0);
            _fleetBuyAllBtn.Visible = false;   // se enseña solo al superadmin
            _fleetBuyAllBtn.Click += (s, e) => BuyAllMachinesUi();
            right.Controls.Add(_fleetBuyAllBtn, 0, 3);

            main.Controls.Add(right, 1, 0);
            shop.Controls.Add(main);
            t.Controls.Add(shop);

            _buyMsg = EmpMsg(); t.Controls.Add(_buyMsg);

            // Pestañas «Comprar» (el escaparate) y «Solicitudes» (lo que piden los maquinistas).
            scroll.Controls.Add(WrapBuyWithRequests(t));
            return scroll;
        }

        // Abre la composición 2D del consist elegido en el showroom, con cada coche resaltado:
        // verde = ya la tienes, rojo = haría falta comprarla, gris = vagón/coche sin tracción.
        void OpenBuyComposition()
        {
            if (_buyByMachine)
            {
                var m0 = _fleetEngList?.SelectedItem as BuyMachine;
                if (m0 == null) { Msg(_buyMsg, Tr("Elige una máquina de la lista."), true); return; }
                if (_curFolder == null) { Msg(_buyMsg, Tr("Selecciona una carpeta de contenido."), true); return; }
                using (var d0 = new CompositionDialog(m0.Path, _curFolder.Path, m0.Name)) d0.ShowDialog(this);
                return;
            }
            var c = _fleetConsistList?.SelectedItem as TrainItem;
            if (c == null) { Msg(_buyMsg, Tr("Elige un tren de la lista."), true); return; }
            if (_curFolder == null) { Msg(_buyMsg, Tr("Selecciona una carpeta de contenido."), true); return; }
            using var dlg = new CompositionDialog(c.FilePath, _curFolder.Path, c.Name, ClassifyCarForBuy);
            dlg.ShowDialog(this);
        }

        // Clasifica un coche de la composición para el resaltado: gris si no es tracción (vagón);
        // si es tracción, verde/rojo según si la empresa ya tiene esa máquina — un coche motriz
        // interno de un automotor hereda el color de la cabeza de ESE consist (comprar la cabeza
        // desbloquea todo el automotor, igual que decide ResolveCompanyUnitReason al conducir).
        Color? ClassifyCarForBuy(string name, bool isEngine)
        {
            if (!isEngine) return Theme.Subtle;
            var c = _fleetConsistList?.SelectedItem as TrainItem;
            if (c == null || string.IsNullOrEmpty(name)) return ColRed;
            var heads = new HashSet<string>(PurchasableUnitsInConsist(c), StringComparer.OrdinalIgnoreCase);
            string head = heads.Contains(name) ? name : null;
            if (head == null)
            {
                var engNames = ConsistEngineNames(c.FilePath);
                if (engNames.Count >= 2)
                {
                    var lfp = c.Locomotive?.FilePath;
                    head = !string.IsNullOrEmpty(lfp) ? System.IO.Path.GetFileNameWithoutExtension(lfp) : engNames[0];
                }
                head ??= name;
            }
            return OwnedUnits(FolderOfConsistEng(c, head), head) > 0 ? Theme.Accent : ColRed;
        }

        // Chip de especificación: título pequeño (subtle) + valor (bold). AutoSize: crece con la fuente,
        // se estira a lo ancho de su columna (Anchor L|R) y nunca recorta el texto.
        Card FleetChip(string caption, out Label value)
        {
            Color fill = Blend(Theme.Surface, Theme.Bg, 0.45f);
            var c = new Card { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Fill = fill, BorderColor = Blend(Theme.Surface, Theme.Bg, 0.6f), Radius = 8, Padding = new Padding(9, 3, 8, 4), Margin = new Padding(2, 2, 3, 2) };
            var g = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, BackColor = fill, Margin = new Padding(0) };
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));         // rótulo
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));     // valor (alto holgado → no recorta la fuente)
            var cap = new Label { Text = I18n.T(caption), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(7f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 0) };
            // Valor a todo el ancho de la celda; su fuente se AUTOAJUSTA para caber siempre (sin recortes).
            value = new Label { Text = "—", AutoSize = false, Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Font(10.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) };
            AttachAutoFit(value, 10.5f, 7f);
            g.Controls.Add(cap, 0, 0);
            g.Controls.Add(value, 0, 1);
            c.Controls.Add(g);
            return c;
        }

        // Ajusta la fuente de una etiqueta para que su texto QUEPA en su ancho (entre maxPt y minPt).
        static void AttachAutoFit(Label v, float maxPt, float minPt)
        {
            string fam = v.Font.Name; var style = v.Font.Style;
            void Fit()
            {
                int w = v.ClientSize.Width;
                if (w <= 4) return;
                float chosen = minPt;
                for (float pt = maxPt; pt >= minPt; pt -= 0.5f)
                {
                    using var f = new Font(fam, pt * Theme.DpiComp, style);   // (a tamaño de escala 100 %)
                    if (TextRenderer.MeasureText(v.Text, f).Width <= w - 2) { chosen = pt; break; }
                }
                if (Math.Abs(v.Font.Size - chosen * Theme.DpiComp) > 0.01f)
                {
                    var old = v.Font; v.Font = new Font(fam, chosen * Theme.DpiComp, style); old.Dispose();
                }
            }
            v.TextChanged += (s, e) => Fit();
            v.Resize += (s, e) => Fit();
        }

        // Chip de precio: título + importe grande de color (verde compra / azul alquiler). AutoSize.
        Card PriceChip(string caption, Color color, out Label value)
        {
            Color fill = Blend(Theme.Surface, color, 0.12f);
            var c = new Card { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Fill = fill, BorderColor = Blend(color, Theme.Bg, 0.2f), Radius = 9, Padding = new Padding(11, 5, 9, 6), Margin = new Padding(3, 0, 3, 0) };
            var g = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, BackColor = fill, Margin = new Padding(0) };
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var cap = new Label { Text = I18n.T(caption), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(7f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 1) };
            value = new Label { Text = "—", AutoSize = true, ForeColor = color, Font = Theme.Font(13.5f, FontStyle.Bold), Margin = new Padding(0) };
            g.Controls.Add(cap, 0, 0);
            g.Controls.Add(value, 0, 1);
            c.Controls.Add(g);
            return c;
        }

        async void LoadFleet()
        {
            if (_fleetList == null) return;
            LoadFleetScale();          // escala de precio para tasar en el cliente
            PopulateFleetConsistList(); // consists comprables del contenido actual
            PopulateBuyMachines();      // máquinas (.eng) comprables del contenido actual
            _fleetList.BeginReload(_empSel?.Id);   // misma empresa: tras editar una unidad, la lista no vuelve arriba
            if (_empSel == null) { _fleetList.ClearRows(); _fleetIds.Clear(); _fleetStatus.Clear(); _fleetOwnedNames.Clear(); _fleetPlates.Clear(); _fleetRowEng.Clear(); _fleetRowDetail.Clear(); _fleetRowEstColor.Clear(); OnFleetVehicleSelected(); _fleetList.SetEmpty(Tr("Selecciona una empresa.")); return; }
            _fleetList.SetEmpty(Tr("Cargando…"));
            const string fleetCols = "id,name,folder,kind,engine_type,km_total,km_since_maint,maint_interval_km,ownership,status,rental_per_service,capacity,comfort";
            string fleetFilter = $"&company_id=eq.{Uri.EscapeDataString(_empSel.Id)}&order=name.asc,created_at.asc";
            var (json, err) = await SelectVehicles(fleetCols + ",plate", fleetFilter);
            if (err != null && err.IndexOf("plate", StringComparison.OrdinalIgnoreCase) >= 0)   // servidor aún sin matrículas
                (json, err) = await SelectVehicles(fleetCols, fleetFilter);
            _fleetList.ClearRows(); _fleetIds.Clear(); _fleetStatus.Clear(); _fleetOwnedNames.Clear(); _fleetOwnedCount.Clear(); _fleetPlates.Clear();
            _fleetRowEng.Clear(); _fleetRowDetail.Clear(); _fleetRowEstColor.Clear();
            if (err != null) { _fleetList.SetEmpty(Tr("Error: ") + err); return; }
            // Modelos (.eng) que el usuario tiene en su contenido local: solo puede CONDUCIR esos.
            // engFolder: .eng → carpeta legible del tren; engPath: .eng → ruta del archivo (para el render 3D).
            var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var engFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var engPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fe in _fleetEngs) { localNames.Add(fe.name); if (fe.folder.Length > 0 && !engFolder.ContainsKey(fe.name)) engFolder[fe.name] = fe.folder; if (fe.path.Length > 0 && !engPath.ContainsKey(fe.name)) engPath[fe.name] = fe.path; }
            int n = 0;
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    n++;
                    _fleetIds.Add(Str(e, "id"));
                    string status = Str(e, "status"); _fleetStatus.Add(status);
                    string rawName = Str(e, "name");
                    string rawFolder = Str(e, "folder");
                    if (rawName.Length > 0)
                    {
                        // Cada unidad cuenta para SU librea (carpeta). Las compradas antes de guardarla
                        // se quedan con la carpeta vacía y siguen valiendo para cualquiera.
                        _fleetOwnedNames.Add(ModelKey(rawFolder, rawName));
                        // Si la unidad se compró con el nombre de un coche de la formación, el recuento
                        // va a la CABEZA, que es la fila que se ofrece en Compra.
                        string ownKey = ModelKey(rawFolder, UnitOfEng(rawName)?.Head ?? rawName);
                        _fleetOwnedCount[ownKey] = _fleetOwnedCount.TryGetValue(ownKey, out var oc) ? oc + 1 : 1;
                    }
                    string name = rawName.Length == 0 ? "—" : rawName;
                    double cap = Num(e, "capacity");
                    string ownership = Str(e, "ownership");
                    double rent = Num(e, "rental_per_service");
                    string prop = ownership == "rented"
                        ? Tr("Alquilada") + " · " + rent.ToString("N0", EsEs) + " €/serv."
                        : Tr("Comprada");
                    double since = Num(e, "km_since_maint"), interval = Num(e, "maint_interval_km");
                    double remaining = Math.Max(0, interval - since);
                    string maint = interval > 0 ? remaining.ToString("N0", EsEs) + " km" : "—";
                    bool due = status == "maintenance_due";
                    bool inUse = status == "in_use";
                    string estado = due ? Tr("No operativo · mantenimiento") : inUse ? Tr("No operativo · en servicio") : Tr("Disponible");
                    Color estadoColor = due ? RedC : inUse ? ColOrange : Theme.Accent;
                    // Vista de disponibilidad para el maquinista: gris si NO tiene el modelo en local (no la puede conducir).
                    // El MODELO es la carpeta con la que se compró la unidad; si no la guardó (backend
                    // sin actualizar), se enseña la que haya en el contenido local.
                    bool hasLocal = localNames.Contains(name);
                    string model = rawFolder.Length > 0 ? rawFolder
                                 : engFolder.TryGetValue(name, out var fo) ? fo
                                 : (hasLocal ? "—" : Tr("(no lo tienes en local)"));
                    Color? nameColor = hasLocal ? (Color?)null : Theme.Subtle;
                    string plate = Str(e, "plate");
                    _fleetPlates.Add(plate);
                    _fleetList.AddRow(new[] { plate.Length > 0 ? plate : "—", name, model, estado },
                        new Color?[] { plate.Length > 0 ? (Color?)Theme.AccentHi : Theme.Subtle, nameColor, hasLocal ? (Color?)null : Theme.Subtle, estadoColor },
                        null, _fleetIds.Count > 0 ? _fleetIds[_fleetIds.Count - 1] : null);
                    // Datos para la ficha lateral (columna derecha).
                    _fleetRowEng.Add(engPath.TryGetValue(name, out var ep) ? ep : "");
                    // TIPO = lo que declara el archivo del vehículo (Motriz / Viajeros / Mercancías…);
                    // la tracción y las plazas van en sus propios campos, para que ninguno se encoja.
                    string declared = VehicleTypeShort(DeclaredVehicleType(engPath.TryGetValue(name, out var dp) ? dp : null));
                    if (declared.Length == 0) declared = "—";
                    string traction = TractionName(Str(e, "engine_type"));
                    string plazas = cap > 0 ? cap.ToString("N0", EsEs) : "—";
                    _fleetRowDetail.Add(new[] { plate.Length > 0 ? plate + "  ·  " + name : name, declared, prop, maint, estado, plazas, traction });
                    _fleetRowEstColor.Add(estadoColor);
                }
            }
            catch { }
            if (n == 0) _fleetList.SetEmpty(Tr("Sin vehículos todavía."));
            if (_fleetHeaderLbl != null) _fleetHeaderLbl.Text = Tr("FLOTA DE LA EMPRESA") + (n > 0 ? "  ·  " + n : "");
            EnsureOwnedMachinesListed();   // lo que ya tienes se ve siempre en Compra, con su distintivo
            // La lista solo se rehace si han cambiado las unidades de la empresa (rehacerla cuesta).
            int ownedStamp = 0;
            foreach (var kv in _fleetOwnedCount) ownedStamp = ownedStamp * 31 + kv.Key.GetHashCode() + kv.Value;
            if (ownedStamp != _fleetOwnedStamp) { _fleetOwnedStamp = ownedStamp; FilterBuyList(); }
            else _fleetEngList?.Invalidate();
            _fleetList.EndReload();    // misma fila arriba y la misma unidad elegida que antes de editar
            OnFleetVehicleSelected();  // refresca la ficha/vista del vehículo seleccionado
            UpdateRoleUi();
            UpdateFleetValuation();   // recalcula el desglose del consist elegido (la propiedad puede haber cambiado)
        }

        // Carga los .eng de las unidades de las empresas DE LAS QUE SOY SOCIO (para la etiqueta
        // "pertenece a tu empresa" en Exploración/Horarios). Superadmin: solo sus empresas de socio.
        async void LoadCompanyVehNames()
        {
            _companyVehNames.Clear();
            try
            {
                if (!Supa.IsLoggedIn || string.IsNullOrEmpty(Supa.UserId)) { RebuildCompanyEngs(); return; }
                // Empresas de las que soy socio (no todas las que ve el superadmin).
                var (mj, me) = await Supa.SelectAsync($"company_members?select=company_id&user_id=eq.{Uri.EscapeDataString(Supa.UserId)}");
                if (me != null) { RebuildCompanyEngs(); return; }
                var mine = new HashSet<string>();
                try { using var d = JsonDocument.Parse(mj); foreach (var e in d.RootElement.EnumerateArray()) { var id = Str(e, "company_id"); if (id.Length > 0) mine.Add(id); } } catch { }
                if (mine.Count == 0) { RebuildCompanyEngs(); return; }
                var byId = new Dictionary<string, string>();
                foreach (var c in _empCompanies) if (mine.Contains(c.Id)) byId[c.Id] = c.Name;
                string inList = string.Join(",", mine);
                var (vj, ve) = await SelectVehicles("name,folder,company_id", $"&company_id=in.({Uri.EscapeDataString(inList)})");
                if (ve == null)
                    try
                    {
                        using var d = JsonDocument.Parse(vj);
                        foreach (var e in d.RootElement.EnumerateArray())
                        {
                            string name = Str(e, "name"), cid = Str(e, "company_id");
                            if (name.Length == 0) continue;
                            string cn = byId.TryGetValue(cid, out var n) ? n : "";
                            if (cn.Length == 0) continue;
                            string key = ModelKey(Str(e, "folder"), name);
                            if (!_companyVehNames.TryGetValue(key, out var lst)) { lst = new List<string>(); _companyVehNames[key] = lst; }
                            if (!HasCo(lst, cn)) lst.Add(cn);
                        }
                    }
                    catch { }
            }
            catch { }
            RebuildCompanyEngs();
        }

        // Expande cada .eng comprado a TODOS los coches motrices de su unidad (mirando los consists),
        // para que la etiqueta reconozca también los consists invertidos u otras variantes del mismo tren.
        void RebuildCompanyEngs()
        {
            _companyEngs.Clear();
            foreach (var kv in _companyVehNames) _companyEngs[kv.Key] = new List<string>(kv.Value);
            try
            {
                foreach (var c in _consistsAll)
                {
                    var lp = c?.Locomotive?.FilePath;
                    if (lp == null) continue;
                    string lead = System.IO.Path.GetFileNameWithoutExtension(lp);
                    var cos = CompaniesOwning(FolderOfConsistEng(c, lead), lead);
                    if (cos == null) continue;   // esta unidad (librea incluida) NO es de mis empresas
                    foreach (var r in ConsistCarRefs(c.FilePath))
                        if (r.isEngine) MergeCompanies(ModelKey(r.folder, r.name), cos);
                }
            }
            catch { }
            try { PopulateCompanyFilters(); } catch { }
            try { RefreshConsistList(); } catch { }   // aplica el filtro por empresa a la lista de trenes
            try { if (_lstConsists != null && _lstConsists.IsHandleCreated) _lstConsists.Invalidate(); } catch { }
            try { if (_activePage == 3) OnTimetableChanged(); } catch { }   // refiltra los trenes del horario + preview
        }

        // ¿La lista ya contiene el nombre (sin distinguir mayúsculas)?
        static bool HasCo(List<string> lst, string co)
        {
            foreach (var x in lst) if (string.Equals(x, co, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Añade a un .eng las empresas indicadas, sin duplicar.
        // Empresas mías que tienen esta máquina; null si ninguna. Las unidades antiguas (sin carpeta
        // guardada) siguen valiendo para cualquier librea del mismo .eng.
        List<string> CompaniesOwning(string folder, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_companyVehNames.TryGetValue(ModelKey(folder, name), out var a)) return a;
            if (!_vehFolderInDb && !string.IsNullOrWhiteSpace(folder) && _companyVehNames.TryGetValue(ModelKey("", name), out var b)) return b;
            return null;
        }

        void MergeCompanies(string eng, List<string> cos)
        {
            if (!_companyEngs.TryGetValue(eng, out var lst)) { lst = new List<string>(); _companyEngs[eng] = lst; }
            foreach (var co in cos)
                if (co.Length > 0 && !HasCo(lst, co)) lst.Add(co);
        }

        // Empresas (de socio) a las que pertenece el tren; lista vacía si a ninguna. Por la cabeza del
        // consist (que, gracias a la expansión, cubre también coches internos/consists invertidos).
        // Si el maquinista está en varias empresas que tienen el mismo tren, salen todas.
        static readonly List<string> _noCompanies = new();
        List<string> ConsistCompanies(TrainItem c)
        {
            if (_companyEngs.Count == 0 || c == null) return _noCompanies;
            var fp = c.Locomotive?.FilePath;
            if (string.IsNullOrEmpty(fp)) return _noCompanies;
            string name = System.IO.Path.GetFileNameWithoutExtension(fp);
            string folder = FolderOfConsistEng(c, name);
            if (_companyEngs.TryGetValue(ModelKey(folder, name), out var cos)) return cos;
            if (!_vehFolderInDb && !string.IsNullOrWhiteSpace(folder) && _companyEngs.TryGetValue(ModelKey("", name), out var legacy)) return legacy;
            return _noCompanies;
        }

        // ---- Filtro de trenes por empresa (Exploración / Horarios) ----
        // Nombres de empresa (de socio) que tienen al menos un tren en el contenido actual, ordenados.
        List<string> AllFleetCompanies()
        {
            var set = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var kv in _companyEngs)
                foreach (var co in kv.Value)
                    if (!string.IsNullOrWhiteSpace(co)) set.Add(co);
            return new List<string>(set);
        }

        // Empresa elegida en un combo de filtro; null = "Todas las empresas".
        string SelectedCompany(ComboBox cb)
        {
            var s = cb?.SelectedItem as string;
            return (string.IsNullOrEmpty(s) || s == Tr(AllCompaniesLabel)) ? null : s;
        }

        // ¿El tren de un timetable pertenece a la empresa dada? (resuelve su consist como SelectOR)
        bool TrainInCompany(Orts.Formats.OR.TimetableFileLite.TrainInformation tr, string company)
        {
            if (tr == null || string.IsNullOrEmpty(company)) return false;
            string ttc = !string.IsNullOrWhiteSpace(tr.LeadingConsist) ? tr.LeadingConsist : tr.Consist;
            var c = ResolveConsist(ttc);
            return c != null && HasCo(ConsistCompanies(c), company);
        }

        // Rellena ambos combos de filtro con "Todas las empresas" + las empresas con trenes.
        // Se oculta el de Exploración si no hay ninguna (no aporta nada sin empresas).
        void PopulateCompanyFilters()
        {
            var cos = AllFleetCompanies();
            _companyFilterLoading = true;
            try
            {
                foreach (var cb in new[] { _cboTrainCompany, _cboTTCompany })
                {
                    if (cb == null) continue;
                    string prev = cb.SelectedItem as string;
                    cb.BeginUpdate();
                    cb.Items.Clear();
                    cb.Items.Add(Tr(AllCompaniesLabel));
                    foreach (var c in cos) cb.Items.Add(c);
                    int idx = 0;
                    if (prev != null) { int p = cb.Items.IndexOf(prev); if (p >= 0) idx = p; }
                    cb.SelectedIndex = idx;
                    cb.EndUpdate();
                }
            }
            finally { _companyFilterLoading = false; }
            // Solo se muestran los filtros si el usuario pertenece a alguna empresa con trenes.
            bool has = cos.Count > 0;
            if (_trainCompanyHost != null) _trainCompanyHost.Visible = has;   // Exploración
            SetTTCompanyVisible(has);                                          // Horarios (colapsa la fila)
        }

        // Muestra u oculta (colapsando la fila) el filtro de empresa de Horarios.
        void SetTTCompanyVisible(bool v)
        {
            if (_ttCompanyLbl != null) _ttCompanyLbl.Visible = v;
            if (_ttCompanyHost != null) _ttCompanyHost.Visible = v;
            if (_ttGrid != null && _ttCompanyRow >= 0 && _ttCompanyRow < _ttGrid.RowStyles.Count)
            {
                _ttGrid.RowStyles[_ttCompanyRow].SizeType = SizeType.Absolute;
                _ttGrid.RowStyles[_ttCompanyRow].Height = v ? 48 : 0;
            }
        }

        // Lee la escala de precio y el % de alquiler globales (para tasar en el cliente).
        async void LoadFleetScale()
        {
            var (json, err) = await Supa.SelectAsync("app_settings?select=fleet_price_scale,fleet_rental_pct,fleet_pax_demand&id=eq.1");
            if (err != null || string.IsNullOrWhiteSpace(json)) return;
            try
            {
                using var d = JsonDocument.Parse(json); var r = d.RootElement;
                if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0)
                {
                    double s = Num(r[0], "fleet_price_scale"); if (s > 0) _fleetScale = s;
                    double p = Num(r[0], "fleet_rental_pct"); if (p > 0) _fleetRentPct = p;
                    double dm = Num(r[0], "fleet_pax_demand"); if (dm > 0) _paxDemandBase = dm;
                }
            }
            catch { }
            await LoadPaxModel();   // parámetros del modelo de viajeros y de clasificación
            FillPaxModelInputs(PaxCfg);
        }

        // Rellena la lista de consists comprables del contenido actual (una vez por carpeta; se
        // recomputa cuando cambia el nº de consists cargados, porque el filtrado de automotores los usa).
        void PopulateFleetConsistList()
        {
            if (_fleetConsistList == null) return;
            string root = _curFolder?.Path ?? "";
            if (_fleetEngsFolder == root && _fleetEngsConsistCount == _consistsAll.Count && _fleetConsists.Count > 0) return;
            _fleetEngsFolder = root;
            _fleetEngsConsistCount = _consistsAll.Count;
            _fleetEngs = AvailableEngines();   // name → path (para tasar cada unidad del desglose)
            var hidden = AutomotorMemberEngs();
            _fleetConsists = new List<TrainItem>();
            foreach (var c in _consistsAll)
                if (c?.FilePath != null && PurchasableUnitsInConsist(c, hidden).Count > 0)
                {
                    _fleetConsists.Add(c);
                }
            _fleetConsists.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            FilterFleetConsistList();
        }

        // ---------------- ID ÚNICO de consist (para localizar el tren exacto en Compra) ----------------
        readonly Dictionary<string, string> _consistIdCache = new(StringComparer.OrdinalIgnoreCase);
        Label _lblStatusExplore, _lblStatusTT;   // estado en la flota, en la ficha «TREN SELECCIONADO»
        Panel _buyWrapExplore, _buyWrapTT;      // botón «Comprar este tren» (solo gerente/gestor/superadmin)

        // TrainItem del tren elegido en Horarios (el mismo que usa la barra de estado).
        TrainItem CurrentTTConsist()
        {
            var ttr = _cboTTTrain?.SelectedItem as Orts.Formats.OR.TimetableFileLite.TrainInformation;
            string ttc = ttr != null ? (!string.IsNullOrWhiteSpace(ttr.LeadingConsist) ? ttr.LeadingConsist : ttr.Consist) : null;
            return ResolveConsist(ttc);
        }

        // «Comprar este tren»: SOLO gerente/gestor de la empresa seleccionada o superadmin.
        bool CanBuyTrains() => Supa.IsLoggedIn && _empSel != null && (Supa.IsSuperadmin || CanManage());

        // Refresca el ID visible y el botón «Comprar este tren» de Exploración y Horarios.
        void UpdateTrainShortcuts()
        {
            // Gerente/gestor: «Comprar este tren». Maquinista: «Solicitar compra» (se lo pide a ellos).
            bool canBuy = CanBuyTrains(), canAsk = CanRequestPurchase();
            var ce = _lstConsists?.SelectedItem as TrainItem;
            UpdateFleetStatusChip(_lblStatusExplore, ce);
            SetBuyWrap(_buyWrapExplore, canBuy, (canBuy || canAsk) && ce != null);
            TrainItem ct = null; try { ct = CurrentTTConsist(); } catch { }
            UpdateFleetStatusChip(_lblStatusTT, ct);
            SetBuyWrap(_buyWrapTT, canBuy, (canBuy || canAsk) && ct != null);
        }

        // Cabecera «TREN SELECCIONADO»: a la derecha, el estado del tren en la flota de tu empresa
        // (Operativo / No operativo). El antiguo «ID del tren» servía para pegarlo en el buscador de
        // Compra, y la compra ya no va por tren sino por máquina, así que se ha quitado.
        (Panel hdr, Label status) MakeTrainHeader(Label title)
        {
            var hdr = new Panel { Dock = DockStyle.Top, Height = Math.Max(24, title.PreferredHeight + 6), BackColor = Theme.Bg };
            title.Dock = DockStyle.Left; title.AutoSize = true;
            var status = new Label { Dock = DockStyle.Right, AutoSize = false, Width = 320, TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.Subtle, Font = Theme.Font(8.5f, FontStyle.Bold) };
            hdr.Controls.Add(title); hdr.Controls.Add(status);
            return (hdr, status);
        }

        // ---- «Operativo / No operativo» del tren seleccionado (Exploración y Horarios) ----
        readonly Dictionary<string, (DateTime at, string code)> _fleetStatusCache = new();

        // code: ok · inuse · maint · none (no es de la flota)
        async void UpdateFleetStatusChip(Label lbl, TrainItem c)
        {
            if (lbl == null) return;
            var co = _empOnDutyCompany ?? _empSel;
            var names = c != null ? EngineNamesOf(c) : null;
            if (c == null || co == null || !Supa.IsLoggedIn || names == null || names.Count == 0)
            {
                lbl.Tag = null; lbl.Text = ""; return;
            }
            var keyParts = new List<string>();
            foreach (var en in names) keyParts.Add(en.folder + "/" + en.name);
            string key = co.Id + "|" + string.Join(",", keyParts);
            lbl.Tag = key;
            string code;
            if (_fleetStatusCache.TryGetValue(key, out var hit) && (DateTime.UtcNow - hit.at).TotalSeconds < 20) code = hit.code;
            else
            {
                var esc = new List<string>();
                var folderOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var en in names) { esc.Add(PgInItem(en.name)); folderOf[en.name] = en.folder ?? ""; }
                var (json, err) = await SelectVehicles("status,name,folder",
                    $"&company_id=eq.{Uri.EscapeDataString(co.Id)}&name=in.({string.Join(",", esc)})");
                if (err != null) { if (lbl.Tag as string == key) lbl.Text = ""; return; }
                bool ok = false, inUse = false, maint = false, any = false;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    foreach (var e in d.RootElement.EnumerateArray())
                    {
                        string rowName = Str(e, "name");
                        if (rowName.Length > 0 && folderOf.TryGetValue(rowName, out var want) && !ModelMatches(Str(e, "folder"), want)) continue;
                        any = true;
                        string st = Str(e, "status");
                        if (st == "available") ok = true;
                        else if (st == "in_use") inUse = true;
                        else if (st == "maintenance_due") maint = true;
                    }
                }
                catch { }
                code = !any ? "none" : ok ? "ok" : inUse && !maint ? "inuse" : maint && !inUse ? "maint" : "unavail";
                _fleetStatusCache[key] = (DateTime.UtcNow, code);
            }
            if (lbl.Tag as string != key) return;   // la selección cambió mientras se consultaba
            // El maquinista solo pide trenes que su empresa no tenga disponibles.
            if (code == "ok" && !CanBuyTrains())
            {
                var w = lbl == _lblStatusExplore ? _buyWrapExplore : lbl == _lblStatusTT ? _buyWrapTT : null;
                if (w != null) w.Visible = false;
            }
            switch (code)
            {
                case "ok": lbl.Text = "●  " + string.Format(Tr("Operativo · {0}"), co.Name); lbl.ForeColor = Theme.Accent; break;
                case "inuse": lbl.Text = "●  " + Tr("Tren no operativo · en servicio"); lbl.ForeColor = ColOrange; break;
                case "maint": lbl.Text = "●  " + Tr("Tren no operativo · en mantenimiento"); lbl.ForeColor = RedC; break;
                case "unavail": lbl.Text = "●  " + Tr("Tren no operativo"); lbl.ForeColor = RedC; break;
                default: lbl.Text = ""; break;   // no es de la flota: no se indica nada
            }
        }

        // Botón «Comprar este tren» (se envuelve para poder ocultarlo; la composición 2D ocupa entonces todo el ancho).
        Panel MakeBuyTrainWrap(Func<TrainItem> getConsist, int height)
        {
            var wrap = new Panel { Dock = DockStyle.Right, Width = 210, BackColor = Theme.Bg, Padding = new Padding(8, 0, 0, 0), Visible = false };
            var b = new RoundButton
            {
                Text = Tr("Comprar este tren"), GlyphKind = "bank", Dock = DockStyle.Fill, Height = height, Radius = 9,
                BaseColor = Color.FromArgb(40, 70, 44), HoverColor = Theme.Accent, TextColor = Theme.AccentHi,
                FontSize = 9.5f, FontStyle = FontStyle.Bold
            };
            b.Click += (s, e) => { if (CanBuyTrains()) BuyThisTrain(getConsist()); else RequestPurchase(getConsist()); };
            wrap.Controls.Add(b);
            return wrap;
        }

        // El mismo botón para los dos: texto según quién lo ve.
        void SetBuyWrap(Panel wrap, bool canBuy, bool visible)
        {
            if (wrap == null) return;
            wrap.Visible = visible;
            if (wrap.Controls.Count > 0 && wrap.Controls[0] is RoundButton b)
            {
                string txt = Tr(canBuy ? "Comprar este tren" : "Solicitar compra");
                if (b.Text != txt) { b.Text = txt; b.Invalidate(); }
            }
        }

        // Abre Empresas → Compra con la MÁQUINA de ese tren ya seleccionada: la compra va por .eng,
        // así que se busca la unidad de tracción que encabeza el consist, no el consist.
        void BuyThisTrain(TrainItem c)
        {
            if (c == null || !CanBuyTrains()) return;
            ShowPage(PageEmpresas);
            _subtabAutoFallback = false;
            ShowSubtab(10);   // Compra (LoadFleet rellena la lista de máquinas comprables)
            if (_fleetConsistSearch != null && _fleetConsistSearch.Box.Text.Length > 0) _fleetConsistSearch.Box.Text = "";   // sin filtro

            string machine = MachineNameForConsist(c);
            if (machine == null) { Msg(_buyMsg, Tr("Ese tren no tiene ninguna máquina de tracción reconocible."), true); return; }
            if (SelectBuyMachine(machine)) Msg(_buyMsg, "", false);
            else Msg(_buyMsg, string.Format(Tr("«{0}» no figura entre las máquinas de este contenido."), machine), true);
        }

        // Máquina que representa a un tren: la unidad de tracción de CABEZA y, si esa motriz es un
        // coche interno de un automotor, la cabeza de su formación (que es lo que se compra).
        string MachineNameForConsist(TrainItem c)
        {
            var units = PurchasableUnitsInConsist(c);
            string lead = null;
            var lfp = c?.Locomotive?.FilePath;
            if (!string.IsNullOrEmpty(lfp)) lead = System.IO.Path.GetFileNameWithoutExtension(lfp);
            if (!string.IsNullOrEmpty(lead))
            {
                var u = UnitOfEng(lead);
                if (u != null && !string.IsNullOrEmpty(u.Head)) lead = u.Head;
                foreach (var n in units)
                    if (string.Equals(n, lead, StringComparison.OrdinalIgnoreCase)) return n;
            }
            return units.Count > 0 ? units[0] : null;
        }

        // Selecciona esa máquina en la lista de Compra y la deja a la vista.
        bool SelectBuyMachine(string name)
        {
            if (_fleetEngList == null || string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < _fleetEngList.Items.Count; i++)
                if (_fleetEngList.Items[i] is BuyMachine m && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    _fleetEngList.SelectedIndex = i;
                    _fleetEngList.TopIndex = Math.Max(0, i - 3);
                    return true;
                }
            return false;
        }

        // Reconstruye la lista visible a partir del texto del buscador, conservando la selección si sigue.
        void FilterFleetConsistList()
        {
            if (_fleetConsistList == null) return;
            string filter = (_fleetConsistSearch?.Box.Text ?? "").Trim();
            var prevSel = _fleetConsistList.SelectedItem as TrainItem;
            int top = filter == _consistListFilter ? _fleetConsistList.TopIndex : 0;
            _consistListFilter = filter;
            _fleetConsistList.BeginUpdate();
            _fleetConsistList.Items.Clear();
            foreach (var c in _fleetConsists)
            {
                // Coincide por nombre, ID (#XXXXXXXX, con o sin «#»), nombre del .con o máquinas que lleva.
                string key = c.Name ?? "";
                if (filter.Length == 0 || key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    _fleetConsistList.Items.Add(c);
            }
            _fleetConsistList.EndUpdate();
            if (prevSel != null)
                for (int i = 0; i < _fleetConsistList.Items.Count; i++)
                    if (((TrainItem)_fleetConsistList.Items[i]).FilePath == prevSel.FilePath) { _fleetConsistList.SelectedIndex = i; break; }
            if (prevSel != null && _fleetConsistList.Items.Count > 0) _fleetConsistList.TopIndex = Math.Min(top, _fleetConsistList.Items.Count - 1);
            if (_fleetConsistList.SelectedIndex < 0 && _fleetConsistList.Items.Count > 0) _fleetConsistList.SelectedIndex = 0;   // dispara el preview
            else if (_fleetConsistList.Items.Count == 0) { RenderFleetAddPreview(); UpdateFleetValuation(); }
        }

        void DrawFleetConsistItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var consist = (TrainItem)_fleetConsistList.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            DrawRowBackground(g, e.Bounds, _fleetConsistList.BackColor, selected);
            var textRect = new Rectangle(e.Bounds.X + 14, e.Bounds.Y, Math.Max(20, e.Bounds.Width - 24), e.Bounds.Height);
            TextRenderer.DrawText(g, consist.Name, Font, textRect, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        // ======================= Compra POR MÁQUINA (.eng) =======================
        // Los trenes (.con) los tiene cada uno distintos; las máquinas suelen ser las mismas. Aquí se
        // lista UNA fila por modelo: la cabeza de cada locomotora/automotor del contenido (los coches
        // motrices internos de un automotor no se listan: van incluidos al comprar la cabeza).
        void PopulateBuyMachines()
        {
            _buyMachines.Clear();
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in _fleetEngs)   // AvailableEngines(): ya excluye coches internos de automotor
                    if (seen.Add(e.name))
                    {
                        var u = UnitOfEng(e.name);
                        bool head = u != null && string.Equals(u.Head, e.name, StringComparison.OrdinalIgnoreCase);
                        // Coches declarados como .eng pero SIN tracción (coches piloto, cafeterías de
                        // algunos packs…) no son una máquina que comprar; si encabezan una formación
                        // sí se dejan, porque entonces representan al conjunto.
                        if (!head && DeclaredPowerKw(e.path) == 0) continue;
                        _buyMachines.Add(new BuyMachine
                        {
                            Name = e.name, Folder = e.folder, Path = e.path,
                            UnitCars = head ? u.Cars : 0
                        });
                    }
                // Una fila por MODELO, ordenadas por su nombre: la carpeta ya no agrupa nada,
                // porque la unidad es la máquina y da igual de qué librea salga.
                _buyMachines.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            }
            catch { }
            FilterBuyList();
        }

        // Las máquinas que la empresa YA TIENE aparecen siempre en la lista, aunque el catálogo las
        // haya descartado (coche interior de una formación, sin tracción declarada…): si está en tu
        // flota, tienes que verla con su «en tu flota · N».
        void EnsureOwnedMachinesListed()
        {
            if (_fleetEngList == null || _fleetOwnedCount.Count == 0) return;
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in _buyMachines) { have.Add(ModelKey(m.Folder, m.Name)); have.Add(ModelKey("", m.Name)); }

            List<string> missing = null;
            foreach (var kv in _fleetOwnedCount)
                if (!have.Contains(kv.Key)) (missing ??= new List<string>()).Add(NameOfModelKey(kv.Key));
            if (missing == null) return;

            var folderOfName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, folder, isEngine) in AllVehicleNames())
                if (isEngine && !folderOfName.ContainsKey(name)) folderOfName[name] = folder;

            bool added = false;
            foreach (var name in missing)
            {
                if (!folderOfName.TryGetValue(name, out var folder)) continue;
                string path = ResolveCarFile(name, folder);
                if (path == null) continue;
                var u = UnitOfEng(name);
                bool head = u != null && string.Equals(u.Head, name, StringComparison.OrdinalIgnoreCase);
                _buyMachines.Add(new BuyMachine { Name = name, Folder = folder, Path = path, UnitCars = head ? u.Cars : 0 });
                added = true;
            }
            if (!added) return;
            _buyMachines.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        }

        // Cambia entre «Por máquina» y «Por tren» (no se toca nada de la vista por tren).
        void SetBuyMode(bool byMachine)
        {
            _buyByMachine = byMachine;
            if (_buyEngCard != null) _buyEngCard.Visible = byMachine;
            if (_buyConsistCard != null) _buyConsistCard.Visible = !byMachine;
            if (_fleetConsistSearch != null)
                _fleetConsistSearch.Box.PlaceholderText = I18n.T(byMachine
                    ? "🔎  Buscar máquina por nombre o carpeta…"
                    : "🔎  Buscar por nombre, ID (#…) o máquina…");
            ClearFleetSpec();
            if (_fleetPreview != null) { _fleetPreview.Image = null; _fleetPreview.Rotatable = false; _fleetPreview.Caption = ""; _fleetPreview.Invalidate(); }
            FilterBuyList();
            if (byMachine) { RenderBuyMachinePreview(); UpdateMachineValuation(); }
            else { RenderFleetAddPreview(); UpdateFleetValuation(); }
            Msg(_buyMsg, "", false);
        }

        // Filtra la lista visible (máquinas o trenes) con el mismo buscador.
        string _buyListFilter = "", _consistListFilter = "";

        void FilterBuyList()
        {
            if (!_buyByMachine) { FilterFleetConsistList(); return; }
            if (_fleetEngList == null) return;
            string filter = (_fleetConsistSearch?.Box.Text ?? "").Trim();
            var prev = _fleetEngList.SelectedItem as BuyMachine;
            // Al recargar tras comprar, la lista se queda donde estaba; si cambia el filtro, empieza arriba.
            int top = filter == _buyListFilter ? _fleetEngList.TopIndex : 0;
            _buyListFilter = filter;

            var shown = new List<BuyMachine>();
            foreach (var m in _buyMachines)
                if (filter.Length == 0
                    || m.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                    || (m.Folder ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    shown.Add(m);

            // Sin cabeceras por carpeta: la lista es de MODELOS y cada fila ya es uno. La carpeta
            // sigue viéndose en la propia fila, como referencia de la librea que se compraría.
            _buyGrouped.Clear();
            _fleetEngList.BeginUpdate();
            _fleetEngList.Items.Clear();
            foreach (var m in shown) _fleetEngList.Items.Add(m);
            _fleetEngList.EndUpdate();
            // La lista se rehace con objetos nuevos: la máquina elegida se reconoce por nombre y carpeta.
            if (prev != null)
                for (int i = 0; i < _fleetEngList.Items.Count; i++)
                    if (_fleetEngList.Items[i] is BuyMachine bm && string.Equals(bm.Name, prev.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(bm.Folder ?? "", prev.Folder ?? "", StringComparison.OrdinalIgnoreCase)) { _fleetEngList.SelectedIndex = i; break; }
            if (_fleetEngList.Items.Count > 0) _fleetEngList.TopIndex = Math.Min(top, _fleetEngList.Items.Count - 1);
        }

        // Fila: nombre de la máquina arriba y, debajo, si es un automotor, los coches que incluye y
        // la carpeta del contenido. A la derecha, cuántas unidades tiene ya la empresa.
        void DrawBuyMachineItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _fleetEngList.Items.Count) return;
            var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            if (_fleetEngList.Items[e.Index] is BuyGroupHeader h)   // modelo (carpeta del contenido)
            {
                using (var bg = new SolidBrush(Blend(_fleetEngList.BackColor, Theme.Bg, 0.75f))) g.FillRectangle(bg, e.Bounds);
                using (var stripe = new SolidBrush(Theme.Accent))                       // franja que abre el bloque
                    g.FillRectangle(stripe, new Rectangle(e.Bounds.X, e.Bounds.Y + 6, 3, e.Bounds.Height - 12));
                using (var pen = new Pen(Blend(_fleetEngList.BackColor, Theme.Bg, 0.9f)))
                    g.DrawLine(pen, e.Bounds.X, e.Bounds.Y, e.Bounds.Right, e.Bounds.Y);

                using var fH = Theme.Font(9f, FontStyle.Bold);
                using var fC = Theme.Font(8f, FontStyle.Bold);
                int edge = e.Bounds.Right - 12;
                if (h.Owned > 0)   // unidades de este modelo ya en la empresa
                {
                    string own = "  " + string.Format(I18n.T("en tu flota · {0}"), h.Owned) + "  ";
                    int ow = TextRenderer.MeasureText(g, own, fC).Width;
                    var orr = new Rectangle(edge - ow, e.Bounds.Y + 7, ow, e.Bounds.Height - 14);
                    using (var b = new SolidBrush(Blend(Theme.Surface2, Theme.Accent, 0.55f))) g.FillRectangle(b, orr);
                    TextRenderer.DrawText(g, own, fC, orr, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                    edge = orr.X - 8;
                }
                string count = h.Count == 1 ? I18n.T("1 máquina") : string.Format(I18n.T("{0} máquinas"), h.Count);
                int cw = TextRenderer.MeasureText(g, count, fC).Width + 4;
                TextRenderer.DrawText(g, count, fC, new Rectangle(edge - cw, e.Bounds.Y, cw, e.Bounds.Height),
                    Color.FromArgb(128, 136, 142), TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                TextRenderer.DrawText(g, h.Folder ?? "", fH,
                    new Rectangle(e.Bounds.X + 12, e.Bounds.Y, Math.Max(20, edge - cw - e.Bounds.X - 20), e.Bounds.Height),
                    Theme.AccentHi, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                return;
            }

            var m = (BuyMachine)_fleetEngList.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            DrawRowBackground(g, e.Bounds, _fleetEngList.BackColor, selected);

            int right = e.Bounds.Right - 10;
            int owned = OwnedUnits(m.Folder, m.Name);
            if (owned > 0)
            {
                // Distintivo de «ya la tienes», con las unidades que hay en la empresa.
                string badge = "  " + string.Format(I18n.T("en tu flota · {0}"), owned) + "  ";
                using var fB = Theme.Font(8f, FontStyle.Bold);
                int bw = TextRenderer.MeasureText(g, badge, fB).Width;
                var br = new Rectangle(right - bw, e.Bounds.Y + 9, bw, e.Bounds.Height - 18);
                using (var b = new SolidBrush(Blend(Theme.Surface2, Theme.Accent, 0.55f))) g.FillRectangle(b, br);
                TextRenderer.DrawText(g, badge, fB, br, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                right = br.X - 8;
                using (var dot = new SolidBrush(Theme.Accent))   // marca al margen: se ve de un vistazo
                    g.FillRectangle(dot, new Rectangle(e.Bounds.X, e.Bounds.Y + 8, 3, e.Bounds.Height - 16));
            }

            // Las filas de un modelo con varias máquinas van sangradas bajo su cabecera.
            int x = e.Bounds.X + (_buyGrouped.Contains(m.Folder ?? "") ? 26 : 14), w = Math.Max(20, right - x);
            var nameRect = new Rectangle(x, e.Bounds.Y + 4, w, 20);
            TextRenderer.DrawText(g, m.Name, Font, nameRect, Theme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            using var fSub = Theme.Font(8f);
            int subY = e.Bounds.Y + Theme.Px(24), used = 0;
            // Tipo declarado en el archivo (Motriz, Viajeros, Mercancías…) y, si es una formación
            // fija, los coches que entran en la compra.
            var parts = new List<string>();
            string kind = VehicleTypeShort(DeclaredVehicleType(m.Path));
            if (kind.Length > 0) parts.Add(kind);
            if (m.UnitCars > 1) parts.Add(string.Format(I18n.T("{0} coches"), m.UnitCars));
            if (parts.Count > 0)
            {
                string u = string.Join("  ·  ", parts);
                used = TextRenderer.MeasureText(g, u, fSub).Width;
                TextRenderer.DrawText(g, u, fSub, new Rectangle(x, subY, Math.Min(used, w), Theme.Px(16)), Theme.Accent,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                used += Theme.Px(10);
            }
            // Bajo una cabecera no se repite la carpeta: ya la dice el modelo que encabeza el bloque.
            if (w - used > Theme.Px(30) && !_buyGrouped.Contains(m.Folder ?? ""))
                TextRenderer.DrawText(g, m.Folder ?? "", fSub, new Rectangle(x + used, subY, w - used, Theme.Px(16)),
                    Color.FromArgb(128, 136, 142), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        // Si la selección cae en una cabecera de modelo, salta a la máquina siguiente (o a la anterior
        // si la cabecera es la última fila). Devuelve true cuando ha movido la selección.
        bool _skippingBuyHeader;
        bool SkipBuyHeader()
        {
            if (_skippingBuyHeader || _fleetEngList == null) return false;
            int i = _fleetEngList.SelectedIndex;
            if (i < 0 || i >= _fleetEngList.Items.Count) return false;
            if (!(_fleetEngList.Items[i] is BuyGroupHeader)) return false;
            int next = i + 1 < _fleetEngList.Items.Count ? i + 1 : i - 1;
            _skippingBuyHeader = true;
            try { if (next >= 0 && _fleetEngList.Items[next] is BuyMachine) _fleetEngList.SelectedIndex = next; }
            finally { _skippingBuyHeader = false; }
            return true;
        }

        // Vista 3D de la máquina elegida (mismo visor que la vista por tren).
        void RenderBuyMachinePreview()
        {
            if (!_buyByMachine) return;
            if (_fleetPreview == null || _fleetEngList == null) return;
            var m = _fleetEngList.SelectedItem as BuyMachine;
            _fleetPreview.Image = null; _fleetPreview.Rotatable = false;
            _fleetGeom = null; _fleetYaw = 0; _fleetPitch = 0; _fleetFlip = false;
            string engPath = m?.Path;
            _fleetPreview.EmptyText = string.IsNullOrEmpty(engPath) ? Tr("Elige una máquina") : null;
            if (string.IsNullOrEmpty(engPath)) { _fleetPrevEngPath = null; _fleetPreview.Caption = ""; _fleetPreview.Invalidate(); return; }
            _fleetPrevEngPath = engPath;
            _fleetPreview.Caption = m.Name;
            _fleetPreview.Invalidate();
            lock (_fleetRenderingShapes) { if (!_fleetRenderingShapes.Add(engPath)) return; }
            Task.Run(() =>
            {
                ShapeGeom geom = null;
                try { geom = ShapeRenderer.BuildGeometry(engPath); } catch { }
                if (!IsHandleCreated) { lock (_fleetRenderingShapes) _fleetRenderingShapes.Remove(engPath); return; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        try
                        {
                            if (_fleetPrevEngPath == engPath && geom != null)
                            {
                                _fleetGeom = geom; _fleetPreview.Rotatable = true; RenderFleetLive();
                            }
                        }
                        catch { }
                        lock (_fleetRenderingShapes) _fleetRenderingShapes.Remove(engPath);
                    }));
                }
                catch { lock (_fleetRenderingShapes) _fleetRenderingShapes.Remove(engPath); }
            });
        }

        // Tren de referencia de una máquina: para un automotor, su propia composición; para una
        // locomotora, el primer tren del contenido que la lleve. Sirve para calcular plazas y confort
        // igual que hace la compra por tren.
        TrainItem RepresentativeConsistFor(BuyMachine m)
        {
            try
            {
                string own = FindUnitConsist(m.Path);
                if (own != null)
                    foreach (var c in _consistsAll)
                        if (string.Equals(c?.FilePath, own, StringComparison.OrdinalIgnoreCase)) return c;
                // Dónde sale esa máquina: anotado en el barrido de consists (búsqueda directa).
                var byEng = _firstConsistOfEng;
                if (byEng != null && byEng.TryGetValue(m.Name, out var path))
                    foreach (var c in _consistsAll)
                        if (string.Equals(c?.FilePath, path, StringComparison.OrdinalIgnoreCase)) return c;
            }
            catch { }
            return null;
        }

        // Ficha y precio de la máquina elegida (mismos cálculos que la compra por tren).
        void UpdateMachineValuation()
        {
            if (!_buyByMachine) return;
            if (_fleetEngList == null || _fleetSpecType == null) return;
            var m = _fleetEngList.SelectedItem as BuyMachine;
            if (m == null) { ClearFleetSpec(); return; }
            var rep = RepresentativeConsistFor(m);
            int token = ++_valToken;
            string path = m.Path, name = m.Name, folder = m.Folder ?? "";
            Task.Run(() =>
            {
                var (kw, kmh, type) = ReadEngineSpecs(path);
                var analysis = rep != null ? AnalyzeComposition(rep, kmh) : new CompositionAnalysis { ServiceType = Tr("Mercancías"), Freight = true };
                var (automotor, cars) = ShapeForService(DetectUnitShape(path), analysis.Freight, path);
                var (mass, brake) = AnalyzeUnitPhysics(path, automotor);
                var (price, rentPrice) = FleetPriceLocal(kw, kmh, type, automotor, cars, mass, brake);
                bool rigid = automotor && (UnitOfPath(path)?.RigidCoupled ?? false);   // lo dicen los archivos
                if (!IsHandleCreated) return;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (token != _valToken) return;   // se eligió otra máquina mientras tasábamos
                        _selPower = kw; _selSpeed = kmh; _selType = type; _selAutomotor = automotor; _selCars = cars;
                        _selMass = mass; _selBrake = brake; _selCapacity = analysis.Capacity; _selComfort = analysis.Comfort;
                        _selPrice = price; _selRent = rentPrice;
                        _fleetSpecType.Text = SpecTitle(automotor ? cars : 1, rigid, analysis.ServiceType,
                            VehicleTypeName(DeclaredVehicleType(path)));
                        _fleetSpecType.ForeColor = analysis.DeclaredPax || !analysis.Freight ? Theme.Accent : ColOrange;
                        if (_vPower != null) _vPower.Text = kw.ToString("N0", EsEs) + " kW";
                        if (_vSpeed != null) _vSpeed.Text = kmh.ToString("N0", EsEs) + " km/h";
                        if (_vPlazas != null) _vPlazas.Text = analysis.Capacity > 0 ? analysis.Capacity.ToString("N0", EsEs) : "—";
                        if (_vConfort != null) _vConfort.Text = analysis.Capacity > 0 ? analysis.Comfort.ToString("N0", EsEs) + "/100" : "—";
                        if (_vMasa != null) _vMasa.Text = mass > 0 ? mass.ToString("N0", EsEs) + " t" : "—";
                        if (_vFreno != null) _vFreno.Text = brake > 0 ? brake.ToString("N0", EsEs) + " kN" : "—";
                        if (_vDensity != null) _vDensity.Text = analysis.Density > 0 ? analysis.Density.ToString("N1", EsEs) + " pax/m²" : "—";
                        int owned = OwnedUnits(folder, name);
                        if (_vBuy != null) _vBuy.Text = price.ToString("N0", EsEs) + " €";
                        if (_vRent != null) _vRent.Text = rentPrice.ToString("N0", EsEs) + " €";
                        if (_fleetBuyBtn != null) _fleetBuyBtn.Text = owned > 0 ? Tr("Comprar otra") : Tr("Comprar");
                        if (_fleetRentBtn != null) _fleetRentBtn.Text = owned > 0 ? Tr("Alquilar otra") : Tr("Alquilar");
                        if (_fleetOwnedBadge != null) _fleetOwnedBadge.Visible = owned > 0;
                    }));
                }
                catch { }
            });
        }

        // Compra/alquiler: manda también la carpeta (la librea). Si el backend todavía no la acepta
        // (servidor sin actualizar), se repite sin ella para no bloquear.
        async Task<string> RpcAcquire(bool rent, string company, string name, string folder, string etype,
                                      bool automotor, double power, double speed, int cars,
                                      double mass, double brake, double capacity, double comfort)
        {
            string fn = rent ? "rent_vehicle" : "buy_vehicle";
            var (_, err) = await Supa.RpcAsync(fn, new
            {
                p_company = company, p_name = name, p_etype = etype, p_automotor = automotor,
                p_power = power, p_speed = speed, p_cars = cars,
                p_mass = mass, p_brake = brake, p_capacity = capacity, p_comfort = comfort,
                p_folder = folder ?? ""
            });
            if (err != null && (err.IndexOf("p_folder", StringComparison.OrdinalIgnoreCase) >= 0
                             || err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0))
                (_, err) = await Supa.RpcAsync(fn, new
                {
                    p_company = company, p_name = name, p_etype = etype, p_automotor = automotor,
                    p_power = power, p_speed = speed, p_cars = cars,
                    p_mass = mass, p_brake = brake, p_capacity = capacity, p_comfort = comfort
                });
            return err;
        }

        async void AcquireMachineUi(bool rent) { await AcquireMachine(rent); }

        // Compra/alquila UNA máquina (la unidad completa: en un automotor, cabeza + sus coches).
        // Todo lo que hace falta para comprar/alquilar una máquina (se lee de sus archivos).
        (string type, bool automotor, int cars, double kw, double kmh, double mass, double brake, double capacity, double comfort)
            BuySpecsOf(BuyMachine m)
        {
            var rep = RepresentativeConsistFor(m);
            var (kw, kmh, type) = ReadEngineSpecs(m.Path);
            var analysis = rep != null ? AnalyzeComposition(rep, kmh) : new CompositionAnalysis { ServiceType = Tr("Mercancías"), Freight = true };
            var (automotor, cars) = ShapeForService(DetectUnitShape(m.Path), analysis.Freight, m.Path);
            var (mass, brake) = AnalyzeUnitPhysics(m.Path, automotor);
            // Igual que en la compra por tren: el precio simula la COMPOSICIÓN. Aquí no hay diálogo
            // (compra masiva o máquina sin elección), así que se usa la composición representativa.
            if (rep != null)
            {
                var tot = ConsistTotals(rep);
                if (tot.cars > 0) { cars = tot.cars; mass = tot.mass; brake = tot.brake; }
            }
            return (type, automotor, cars, kw, kmh, mass, brake, analysis.Capacity, analysis.Comfort);
        }

        async void BuyAllMachinesUi() { await BuyAllMachines(); }

        // Compra masiva: todas las máquinas de la lista que la empresa no tenga todavía.
        // El trabajo pesado lo hace el servidor (buy_vehicles_bulk) por tandas; si esa función no
        // existe aún en el servidor, se compran de una en una como antes.
        // Un segundo clic mientras está en marcha la detiene: lo comprado se queda comprado.
        const int BulkChunk = 500;   // unidades por llamada

        async Task BuyAllMachines()
        {
            if (_buyingAll) { _buyAllCancel = true; Msg(_buyMsg, Tr("Deteniendo la compra masiva…"), false); return; }
            if (!Supa.IsSuperadmin) return;
            if (_empSel == null) { Msg(_buyMsg, Tr("Selecciona una empresa."), true); return; }
            if (_fleetEngList == null) return;

            var pend = new List<BuyMachine>();
            foreach (var it in _fleetEngList.Items)
                if (it is BuyMachine m && OwnedUnits(m.Folder, m.Name) == 0) pend.Add(m);
            // Cuando ya están todas, otro clic compra UNA UNIDAD MÁS de cada modelo: así se tienen
            // varias locomotoras iguales y pueden circular varios maquinistas a la vez con ese modelo.
            bool otraRonda = pend.Count == 0;
            if (otraRonda)
                foreach (var it in _fleetEngList.Items)
                    if (it is BuyMachine m2) pend.Add(m2);
            if (pend.Count == 0) { Msg(_buyMsg, Tr("No hay máquinas en la lista."), false); return; }

            string q = string.Format(Tr("Se van a comprar {0} máquinas para {1} (las de la lista que aún no tiene).\n\nSe descontará su valor de la tesorería. Puedes detenerlo en cualquier momento.\n\n¿Continuar?"),
                                     pend.Count.ToString("N0", EsEs), _empSel.Name);
            if (otraRonda)
                q = string.Format(Tr("{1} ya tiene todos los modelos de la lista. Se va a comprar UNA UNIDAD MÁS de cada uno: {0} máquinas." + "\n\n"
                                   + "Cada unidad extra deja conducir a un maquinista más a la vez con ese modelo. Se descontará su valor de la tesorería y puedes detenerlo en cualquier momento." + "\n\n"
                                   + "¿Continuar?"),
                                  pend.Count.ToString("N0", EsEs), _empSel.Name);
            if (MessageBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            _buyingAll = true; _buyAllCancel = false;
            string textoOriginal = _fleetBuyAllBtn.Text;
            _fleetBuyAllBtn.Text = Tr("Detener la compra masiva");
            if (_fleetBuyBtn != null) _fleetBuyBtn.Enabled = false;
            if (_fleetRentBtn != null) _fleetRentBtn.Enabled = false;

            int compradas = 0, saltadas = 0, fallidas = 0; double gastado = 0; string ultimoError = null;
            bool servidor = true;   // mientras la función exista, todo va por tandas
            try
            {
                for (int i = 0; i < pend.Count && !_buyAllCancel; )
                {
                    int n = Math.Min(servidor ? BulkChunk : 1, pend.Count - i);
                    Msg(_buyMsg, string.Format(Tr("Preparando {0} de {1}…"), (i + n).ToString("N0", EsEs), pend.Count.ToString("N0", EsEs)), false);

                    // Los datos de cada máquina se leen de sus archivos (fuera del hilo de la interfaz).
                    var tanda = pend.GetRange(i, n);
                    var datos = await Task.Run(() =>
                    {
                        var lista = new List<object>(tanda.Count);
                        foreach (var m in tanda)
                        {
                            var sp = BuySpecsOf(m);
                            lista.Add(new
                            {
                                name = m.Name, folder = m.Folder ?? "", etype = sp.type, automotor = sp.automotor,
                                power = sp.kw, speed = sp.kmh, cars = sp.cars, mass = sp.mass, brake = sp.brake,
                                capacity = sp.capacity, comfort = sp.comfort
                            });
                        }
                        return lista;
                    });
                    if (_buyAllCancel) break;

                    if (servidor)
                    {
                        Msg(_buyMsg, string.Format(Tr("Comprando {0} de {1}…"), (i + n).ToString("N0", EsEs), pend.Count.ToString("N0", EsEs)), false);
                        var (json, err) = await Supa.RpcAsync("buy_vehicles_bulk", new
                        {
                            p_company = _empSel.Id,
                            p_items = datos,
                            p_skip_owned = !otraRonda
                        });
                        if (err != null && err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            servidor = false;   // servidor sin compra masiva: se sigue de una en una
                            continue;
                        }
                        if (err != null) { fallidas += n; ultimoError = err; break; }
                        var (b, sk, sp2) = ParseBulkResult(json);
                        compradas += b; saltadas += sk; gastado += sp2;
                        // Si el servidor deja de comprar, es que la tesorería se ha acabado.
                        if (b + sk < n) { ultimoError = Tr("Tesorería insuficiente para seguir comprando."); i += n; break; }
                    }
                    else
                    {
                        var m = tanda[0];
                        Msg(_buyMsg, string.Format(Tr("Comprando {0} de {1}…   {2}"), (i + 1).ToString("N0", EsEs), pend.Count.ToString("N0", EsEs), m.Name), false);
                        var sp = BuySpecsOf(m);
                        string err = await RpcAcquire(false, _empSel.Id, m.Name, m.Folder, sp.type, sp.automotor,
                                                      sp.kw, sp.kmh, sp.cars, sp.mass, sp.brake, sp.capacity, sp.comfort);
                        if (err == null) compradas++;
                        else
                        {
                            fallidas++; ultimoError = err;
                            if (err.IndexOf("Saldo insuficiente", StringComparison.OrdinalIgnoreCase) >= 0) { i += n; break; }
                        }
                    }
                    i += n;
                }
            }
            catch (Exception ex) { ultimoError = ex.Message; }
            finally
            {
                _buyingAll = false; _buyAllCancel = false;
                _fleetBuyAllBtn.Text = textoOriginal;
                if (_fleetBuyBtn != null) _fleetBuyBtn.Enabled = true;
                if (_fleetRentBtn != null) _fleetRentBtn.Enabled = true;
            }
            LoadCompanies();
            LoadFleet();

            string resumen = string.Format(Tr("Compra masiva terminada: {0} máquinas compradas"), compradas.ToString("N0", EsEs));
            if (gastado > 0) resumen += "  ·  " + gastado.ToString("N0", EsEs) + " €";
            if (saltadas > 0) resumen += "  ·  " + string.Format(Tr("{0} ya estaban en la flota"), saltadas.ToString("N0", EsEs));
            if (fallidas > 0 || ultimoError != null) resumen += "  ·  " + (ultimoError ?? "");
            Msg(_buyMsg, resumen, fallidas > 0 || ultimoError != null);
        }

        // Respuesta de buy_vehicles_bulk: { bought, skipped, spent, balance }
        static (int bought, int skipped, double spent) ParseBulkResult(string json)
        {
            try
            {
                using var d = JsonDocument.Parse(json);
                var e = d.RootElement;
                if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0) e = e[0];
                int b = e.TryGetProperty("bought", out var pb) && pb.TryGetInt32(out var bi) ? bi : 0;
                int sk = e.TryGetProperty("skipped", out var ps) && ps.TryGetInt32(out var si) ? si : 0;
                double sp = e.TryGetProperty("spent", out var pe) && pe.TryGetDouble(out var sd) ? sd : 0;
                return (b, sk, sp);
            }
            catch { return (0, 0, 0); }
        }

        // Composiciones del contenido que llevan ESTA máquina en cabeza, con lo que costaría comprar
        // el tren completo. Se usan para elegir con qué composición se tasa la compra.
        List<ConsistPriceDialog.Opcion> ConsistOptionsFor(BuyMachine m, out int porDefecto)
        {
            porDefecto = 0;
            var ops = new List<ConsistPriceDialog.Opcion>();
            if (m == null) return ops;
            var rep = RepresentativeConsistFor(m);
            var (kw, kmh, type) = ReadEngineSpecs(m.Path);
            foreach (var c in _consistsAll)
            {
                if (c?.FilePath == null) continue;
                if (!string.Equals(LeadEngineName(c), m.Name, StringComparison.OrdinalIgnoreCase)) continue;
                var (cars, masa, freno, plazas) = ConsistTotals(c);
                if (cars <= 0) continue;
                var analysis = AnalyzeComposition(c, kmh);
                var (automotor, _) = ShapeForService(DetectUnitShape(m.Path), analysis.Freight, m.Path);
                var (precio, _) = FleetPriceLocal(kw, kmh, type, automotor, cars, masa, freno);
                if (ReferenceEquals(c, rep)) porDefecto = ops.Count;
                ops.Add(new ConsistPriceDialog.Opcion
                {
                    Nombre = c.Name, Vehiculos = cars, Plazas = plazas, Masa = masa, Precio = precio, Tag = c
                });
            }
            ops.Sort((a, b) => a.Precio.CompareTo(b.Precio));
            for (int i = 0; i < ops.Count; i++) if (ReferenceEquals(ops[i].Tag, rep)) porDefecto = i;
            return ops;
        }

        // Suma de toda la composición: vehículos, masa, freno y plazas. Es lo que se manda al
        // servidor para tasar la compra, de modo que arrastrar más coches cueste más.
        (int cars, double mass, double brake, double capacity) ConsistTotals(TrainItem c)
        {
            int cars = 0; double mass = 0, brake = 0, cap = 0;
            try
            {
                foreach (var r in ConsistCarRefs(c?.FilePath))
                {
                    cars++;
                    var v = Veh(ResolveCarFile(r.name, r.folder));
                    if (v == null) continue;
                    mass += v.MassT; brake += v.BrakeKn; cap += v.Capacity;
                }
            }
            catch { }
            return (cars, mass, brake, cap);
        }

        async Task<bool> AcquireMachine(bool rent)   // true = comprada/alquilada
        {
            if (_empSel == null) { Msg(_buyMsg, Tr("Selecciona una empresa."), true); return false; }
            if (!CanManage() && !Supa.IsSuperadmin) { Msg(_buyMsg, Tr("Solo el dueño o un gestor pueden gestionar la flota."), true); return false; }
            var m = _fleetEngList?.SelectedItem as BuyMachine;
            if (m == null) { Msg(_buyMsg, Tr("Elige una máquina de la lista."), true); return false; }
            int owned = OwnedUnits(m.Folder, m.Name);
            if (owned > 0)
            {
                string q = string.Format(rent
                        ? Tr("Ya tienes {0} unidad(es) de esta máquina. Cada unidad deja conducir a un maquinista a la vez. ¿Alquilar otra?")
                        : Tr("Ya tienes {0} unidad(es) de esta máquina. Cada unidad deja conducir a un maquinista a la vez. ¿Comprar otra?"), owned);
                if (MessageBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
            }

            // Se compra la MÁQUINA, pero el precio simula el de un tren completo: el gestor elige
            // con qué composición se tasa.
            var sp = BuySpecsOf(m);
            int cars = sp.cars; double mass = sp.mass, brake = sp.brake, capacity = sp.capacity;
            Msg(_buyMsg, Tr("Buscando composiciones de esta máquina…"), false);
            var ops = await Task.Run(() => { int d; var l = ConsistOptionsFor(m, out d); return (l, d); });
            if (ops.l.Count > 0)
            {
                using var dlg = new ConsistPriceDialog(m.Name, ops.l, ops.d);
                if (dlg.ShowDialog(this) != DialogResult.OK) { Msg(_buyMsg, "", false); return false; }
                var elegida = ops.l[dlg.Elegida];
                var tot = ConsistTotals(elegida.Tag as TrainItem);
                cars = tot.cars; mass = tot.mass; brake = tot.brake; capacity = tot.capacity;
            }

            Msg(_buyMsg, rent ? Tr("Alquilando…") : Tr("Comprando…"), false);
            string err = await RpcAcquire(rent, _empSel.Id, m.Name, m.Folder, sp.type, sp.automotor,
                                          sp.kw, sp.kmh, cars, mass, brake, capacity, sp.comfort);
            if (err != null) { Msg(_buyMsg, Tr("Error: ") + err, true); return false; }
            Msg(_buyMsg, (rent ? Tr("Vehículo alquilado.") : Tr("Vehículo comprado.")) + "  " + Tr("Puedes asignarle matrícula en Flota."), false);
            LoadCompanies();
            LoadFleet();
            return true;
        }

        // Máquinas de tracción de un consist que se pueden comprar POR SEPARADO: la cabeza de cada
        // locomotora/automotor que contiene, sin repetir los coches motrices internos de un mismo
        // automotor de formación fija (esos ya están cubiertos comprando la cabeza — AutomotorMemberEngs).
        List<string> PurchasableUnitsInConsist(TrainItem c) => PurchasableUnitsInConsist(c, AutomotorMemberEngs());

        List<string> PurchasableUnitsInConsist(TrainItem c, HashSet<string> hidden)
        {
            var result = new List<string>();
            if (c?.FilePath == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in ConsistEngineNames(c.FilePath))
                if (!hidden.Contains(n) && seen.Add(n)) result.Add(n);
            return result;
        }

        // Resuelve la ruta de fichero de un .eng por su nombre (para tasarlo), a partir del contenido
        // ya enumerado en _fleetEngs.
        string EngPathByName(string name)
        {
            foreach (var e in _fleetEngs)
                if (string.Equals(e.name, name, StringComparison.OrdinalIgnoreCase)) return e.path;
            return null;
        }

        // Enumera los modelos .eng del contenido: <carpeta>\TRAINS\TRAINSET\<folder>\<name>.eng
        List<(string name, string folder, string path, string kind)> AvailableEngines()
        {
            var list = new List<(string name, string folder, string path, string kind)>();
            try
            {
                var root = _curFolder?.Path;
                if (string.IsNullOrEmpty(root)) return list;
                var trainset = System.IO.Path.Combine(root, "TRAINS", "TRAINSET");
                if (!System.IO.Directory.Exists(trainset)) return list;
                foreach (var dir in System.IO.Directory.GetDirectories(trainset))
                {
                    string folder = System.IO.Path.GetFileName(dir);
                    foreach (var eng in System.IO.Directory.GetFiles(dir, "*.eng"))
                        list.Add((System.IO.Path.GetFileNameWithoutExtension(eng), folder, eng, null));
                }
            }
            catch { }
            // Si un .eng forma parte de un AUTOMOTOR (consist con ≥2 coches motrices), se descartan
            // del listado los demás coches motrices de ese mismo automotor: solo se ofrece la cabeza.
            var hidden = AutomotorMemberEngs();
            if (hidden.Count > 0)
                list.RemoveAll(e => hidden.Contains(e.name));
            list.Sort((a, b) => string.Compare(a.folder + "/" + a.name, b.folder + "/" + b.name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        // .eng que NO se ofrecen como vehículo aparte: van incluidos al comprar la cabeza de su
        // formación (ver EnsureEngUnits).
        HashSet<string> AutomotorMemberEngs()
        {
            EnsureEngUnits();
            return _engHidden;
        }

        // Formación fija a la que pertenece un .eng (por nombre de archivo), o null si va suelto.
        EngUnit UnitOfEng(string engName)
        {
            if (string.IsNullOrEmpty(engName)) return null;
            EnsureEngUnits();
            return _engUnits.TryGetValue(engName, out var u) ? u : null;
        }

        EngUnit UnitOfPath(string engPath)
        {
            try { return string.IsNullOrEmpty(engPath) ? null : UnitOfEng(System.IO.Path.GetFileNameWithoutExtension(engPath)); }
            catch { return null; }
        }

        // ¿Es este .eng la CABEZA de una formación fija de varias motrices?
        bool IsUnitHead(string engPath)
        {
            var u = UnitOfPath(engPath);
            return u != null && (u.Members.Count > 1 || u.Cars > 1)
                && string.Equals(u.Head, System.IO.Path.GetFileNameWithoutExtension(engPath), StringComparison.OrdinalIgnoreCase);
        }

        // Deja listo en segundo plano lo que la sección Compra necesita de cada .eng (potencia
        // declarada y tipo de vehículo). Sin esto, la primera vez que se abre Compra se leen miles de
        // archivos en el hilo de la interfaz.
        void PrewarmMachineData()
        {
            try
            {
                // Es trabajo de disco: repartirlo entre varios hilos acorta mucho la espera. Se
                // enumeran TODOS los .eng (sin depender de las formaciones), para poder empezar en
                // cuanto se conoce la carpeta, a la vez que Open Rails lee los trenes.
                // El índice se abre aquí también: esta tarea corre a la vez que la de los trenes y
                // no se puede dar por hecho que la otra haya llegado antes (Open es idempotente).
                ContentIndex.Open(_curFolder?.Path ?? "");
                if (ContentIndex.Loaded && ContentIndex.Heads.Count > 0) return;   // ya está en el índice
                var engs = new List<string>();
                foreach (var (name, folder, isEngine) in AllVehicleNames())
                    if (isEngine) { var p2 = ResolveCarFile(name, folder); if (p2 != null) engs.Add(p2); }
                System.Threading.Tasks.Parallel.ForEach(engs,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) },
                    path => Head(path));
            }
            catch { }
        }

        void EnsureEngUnits()
        {
            lock (_engUnitsLock)
            {
                string root = _curFolder?.Path ?? "";
                if (_engUnits != null && _engUnitsFolder == root && _engUnitsConsistCount == _consistsAll.Count) return;
                _engUnitsFolder = root; _engUnitsConsistCount = _consistsAll.Count;
                BuildEngUnits();
            }
        }

        // Deduce las formaciones fijas del contenido con UN solo barrido de los .con.
        //  1) Por cada consist se anota, de cada coche motriz, en qué tren sale y en qué posición.
        //  2) Candidatos: dos motrices de la MISMA carpeta acopladas a ≤ MaxUnitGap coches.
        //  3) Cabeza: la que más veces encabeza un tren (desempate: la que más sale, luego alfabético).
        //  4) Un candidato entra en la formación de esa cabeza si NUNCA sale sin ella y siempre va
        //     acoplado a ella (y el nombre se parece). Así el 592 forma «motriz + remolque + motriz»
        //     y, en cambio, dos locomotoras que a veces circulan solas no se fusionan.
        //  5) Nº de coches = el bloque completo (con remolques) en el .con más corto que la lleva.
        void BuildEngUnits()
        {
            if (RestoreEngUnitsFromIndex()) return;
            var units = new Dictionary<string, EngUnit>(StringComparer.OrdinalIgnoreCase);
            var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var conEngines = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var firstConsist = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Lectura previa en paralelo: el análisis de formaciones que viene a continuación ya
                // encuentra todos los .con en la caché y no espera al disco.
                try
                {
                    var paths = new List<string>();
                    foreach (var c0 in _consistsAll) if (c0?.FilePath != null) paths.Add(c0.FilePath);
                    System.Threading.Tasks.Parallel.ForEach(paths,
                        new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) },
                        f => ConsistCarRefs(f));
                }
                catch { }

                var conRefs = new List<List<(string name, string folder, bool isEngine)>>();
                var conPaths = new List<string>();
                var posOf = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
                var folderOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var leadCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var near = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                foreach (var c in _consistsAll)
                {
                    if (c?.FilePath == null) continue;
                    var refs = ConsistCarRefs(c.FilePath);
                    if (refs.Count == 0) continue;
                    int ci = conRefs.Count;
                    conRefs.Add(refs); conPaths.Add(c.FilePath);

                    var engs = new List<(int idx, string name, string folder)>();
                    for (int i = 0; i < refs.Count; i++)
                        if (refs[i].isEngine) engs.Add((i, refs[i].name, refs[i].folder));
                    var engNames = new List<string>(engs.Count);
                    foreach (var en in engs) engNames.Add(en.name);
                    conEngines[c.FilePath] = engNames;   // ya leído: que nadie vuelva a abrir el archivo
                    foreach (var en in engNames)
                        if (!firstConsist.ContainsKey(en)) firstConsist[en] = c.FilePath;   // dónde sale cada máquina
                    if (engs.Count == 0) continue;

                    string leadName = engs[0].name;
                    var lfp = c.Locomotive?.FilePath;
                    if (!string.IsNullOrEmpty(lfp)) leadName = System.IO.Path.GetFileNameWithoutExtension(lfp);
                    leadCount[leadName] = leadCount.TryGetValue(leadName, out var lv) ? lv + 1 : 1;

                    foreach (var en in engs)
                    {
                        if (!posOf.TryGetValue(en.name, out var pm)) posOf[en.name] = pm = new Dictionary<int, int>();
                        if (!pm.ContainsKey(ci)) pm[ci] = en.idx;
                        if (!folderOf.ContainsKey(en.name)) folderOf[en.name] = en.folder;
                    }
                    for (int a2 = 0; a2 < engs.Count; a2++)
                        for (int b2 = a2 + 1; b2 < engs.Count && engs[b2].idx - engs[a2].idx <= MaxUnitGap; b2++)
                        {
                            string na = engs[a2].name, nb = engs[b2].name;
                            if (string.Equals(na, nb, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!string.Equals(engs[a2].folder, engs[b2].folder, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!near.TryGetValue(na, out var sa)) near[na] = sa = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            if (!near.TryGetValue(nb, out var sb)) near[nb] = sb = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            sa.Add(nb); sb.Add(na);
                        }
                }

                foreach (var (name, folder) in AllEngNames())
                    if (!folderOf.ContainsKey(name)) folderOf[name] = folder;

                var order = new List<string>(posOf.Keys);
                order.Sort((x, y) =>
                {
                    int lx = leadCount.TryGetValue(x, out var vx) ? vx : 0, ly = leadCount.TryGetValue(y, out var vy) ? vy : 0;
                    if (lx != ly) return ly.CompareTo(lx);
                    int cx = posOf[x].Count, cy = posOf[y].Count;
                    if (cx != cy) return cy.CompareTo(cx);
                    return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
                });
                var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < order.Count; i++) rank[order[i]] = i;

                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var made = new List<EngUnit>();   // formaciones, en orden de preferencia de cabeza
                foreach (var head in order)
                {
                    if (!taken.Add(head)) continue;
                    if (!near.TryGetValue(head, out var cand) || cand.Count == 0) continue;
                    var cands = new List<string>(cand);
                    cands.Sort((x, y) => rank[x].CompareTo(rank[y]));

                    var members = new List<string> { head };
                    foreach (var e in cands)
                    {
                        if (members.Count >= MaxUnitMembers) break;
                        if (taken.Contains(e)) continue;
                        if (!SameUnit(e, head, posOf, folderOf)) continue;
                        taken.Add(e); members.Add(e);
                    }
                    if (members.Count < 2) continue;

                    // .con representativo: el más corto que lleva TODA la formación.
                    string rep = null; int bestTotal = int.MaxValue, cars = members.Count;
                    foreach (var kv in posOf[head])
                    {
                        int ci = kv.Key, lo = kv.Value, hi = kv.Value; bool all = true;
                        foreach (var mn in members)
                        {
                            if (!posOf[mn].TryGetValue(ci, out var ip)) { all = false; break; }
                            if (ip < lo) lo = ip;
                            if (ip > hi) hi = ip;
                        }
                        if (!all) continue;
                        int total = conRefs[ci].Count;
                        if (total < bestTotal) { bestTotal = total; rep = conPaths[ci]; cars = hi - lo + 1; }
                    }

                    var unit = new EngUnit { Head = head, Members = members, RepConsist = rep, Cars = Math.Max(cars, members.Count) };
                    foreach (var mn in members)
                        if (folderOf.TryGetValue(mn, out var fm) && RigidCouplingOf(mn, fm) != Rigid.None) { unit.RigidCoupled = true; break; }
                    foreach (var mn in members) units[mn] = unit;
                    for (int i = 1; i < members.Count; i++) hidden.Add(members[i]);
                    made.Add(unit);
                }

                // Muchos packs traen la formación duplicada en los dos sentidos de marcha
                // (Mot + cola-i y Mot-i + cola): es el MISMO vehículo, así que las dos formaciones se
                // funden en una sola ficha y se queda de cabeza la motriz de verdad (la de más kW).
                var sameFolder = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < made.Count; i++)
                {
                    if (!folderOf.TryGetValue(made[i].Head, out var fh)) continue;
                    if (!sameFolder.TryGetValue(fh, out var lst)) sameFolder[fh] = lst = new List<int>();
                    lst.Add(i);
                }
                foreach (var kvF in sameFolder)
                {
                    var idx = kvF.Value;
                    for (int a = 0; a < idx.Count; a++)
                    {
                        var A = made[idx[a]];
                        if (A == null) continue;
                        for (int b = a + 1; b < idx.Count; b++)
                        {
                            var B = made[idx[b]];
                            if (B == null || B.Members.Count != A.Members.Count) continue;
                            bool mirror = true;
                            foreach (var bm in B.Members)
                            {
                                bool any = false;
                                foreach (var am in A.Members) if (MirrorEngNames(bm, am)) { any = true; break; }
                                if (!any) { mirror = false; break; }
                            }
                            if (!mirror) continue;

                            if (EngPowerKw(B.Head, kvF.Key) > EngPowerKw(A.Head, kvF.Key) + 1)
                            {
                                for (int k = 0; k < A.Members.Count; k++)
                                    if (MirrorEngNames(B.Head, A.Members[k])) { hidden.Add(A.Members[k]); A.Members[k] = B.Head; break; }
                                A.Head = B.Head;
                                if (B.RepConsist != null) A.RepConsist = B.RepConsist;
                            }
                            foreach (var bm in B.Members)
                            {
                                units[bm] = A;
                                if (!string.Equals(bm, A.Head, StringComparison.OrdinalIgnoreCase)) hidden.Add(bm);
                            }
                            hidden.Remove(A.Head);
                            made[idx[b]] = null;
                        }
                    }
                }

                // Variantes espejo: muchos packs traen el mismo coche duplicado con sufijo «-i»
                // (invertido). Si un .eng no ha formado pareja por sí mismo pero su nombre es el de un
                // coche de una formación más 1-2 caracteres al final, es el mismo vehículo: se une a
                // ella (no cuenta como coche extra, solo deja de ofrecerse suelto).
                var byFolder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in units)
                {
                    if (!folderOf.TryGetValue(kv.Key, out var fm)) continue;
                    if (!byFolder.TryGetValue(fm, out var lst)) byFolder[fm] = lst = new List<string>();
                    lst.Add(kv.Key);
                }
                foreach (var (name, folder) in AllEngNames())
                {
                    if (units.ContainsKey(name) || !byFolder.TryGetValue(folder, out var mates)) continue;
                    foreach (var mate in mates)
                        if (MirrorEngNames(name, mate))
                        {
                            var found = units[mate];
                            units[name] = found;
                            if (!string.Equals(name, found.Head, StringComparison.OrdinalIgnoreCase)) hidden.Add(name);
                            break;
                        }
                }

                // Último recurso: .eng que no aparecen en NINGÚN tren del contenido, así que los .con
                // no pueden decir si van juntos.
                //  1) Si los vehículos de la carpeta llevan ACOPLE RÍGIDO, lo dicen ellos mismos: los
                //     de barra en un extremo son las cabezas y los de barra en los dos, los intermedios.
                //  2) Si nadie declara barra, se cae a la carpeta: pocos vehículos y nombres de familia.
                var orphans = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var engsInFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var vehiclesInFolder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, folder, isEngine) in AllVehicleNames())
                {
                    if (!vehiclesInFolder.TryGetValue(folder, out var vl)) vehiclesInFolder[folder] = vl = new List<string>();
                    vl.Add(name);
                    if (!isEngine) continue;
                    engsInFolder[folder] = engsInFolder.TryGetValue(folder, out var c0) ? c0 + 1 : 1;
                    if (units.ContainsKey(name) || posOf.ContainsKey(name)) continue;
                    if (!orphans.TryGetValue(folder, out var lst)) orphans[folder] = lst = new List<string>();
                    lst.Add(name);
                }

                foreach (var kvO in orphans)
                {
                    string folder = kvO.Key;
                    var names = kvO.Value;
                    if (names.Count == 0) continue;

                    // --- 1) por acople rígido ---
                    var rigidEngs = new List<string>();
                    if (vehiclesInFolder.TryGetValue(folder, out var vAll) && vAll.Count <= MaxUnitMembers + 6)
                        foreach (var n2 in names) if (RigidCouplingOf(n2, folder) != Rigid.None) rigidEngs.Add(n2);
                    // Solo en carpetas de UN modelo: en un pack con decenas de vehículos la barra no
                    // basta para saber qué cabeza va con qué cola, y leerlos todos cuesta tiempo.
                    bool smallFolder = vehiclesInFolder.TryGetValue(folder, out var vehicles) && vehicles.Count <= MaxUnitMembers + 6;
                    var rigidEngs2 = rigidEngs;
                    if (smallFolder && rigidEngs.Count > 0)
                    {
                        // Coches reales de la formación: los de barra, sin contar dos veces las
                        // variantes espejo («-i») del mismo coche.
                        var cores = new List<string>();
                        bool anyEnd = false;
                        foreach (var vn in vehicles)
                        {
                            var k = RigidCouplingOf(vn, folder);
                            if (k == Rigid.None) continue;
                            if (k == Rigid.End) anyEnd = true;
                            bool dup = false;
                            foreach (var c2 in cores) if (MirrorEngNames(vn, c2)) { dup = true; break; }
                            if (!dup) cores.Add(vn);
                        }
                        if (anyEnd && cores.Count >= 2 && cores.Count <= MaxUnitMembers + 2)
                        {
                            string head = rigidEngs[0]; double bestKw = -1;
                            foreach (var n2 in rigidEngs)
                            {
                                bool isEnd = RigidCouplingOf(n2, folder) == Rigid.End;
                                double kw = EngPowerKw(n2, folder) + (isEnd ? 1e6 : 0);   // la cabeza es un extremo
                                if (kw > bestKw) { bestKw = kw; head = n2; }
                            }
                            var unit = new EngUnit
                            {
                                Head = head, Members = new List<string>(rigidEngs), RepConsist = null,
                                Cars = cores.Count, RigidCoupled = true
                            };
                            foreach (var mn in rigidEngs)
                            {
                                units[mn] = unit;
                                if (!string.Equals(mn, head, StringComparison.OrdinalIgnoreCase)) hidden.Add(mn);
                            }
                            continue;
                        }
                    }

                    // --- 2) por carpeta y nombres, como hasta ahora ---
                    if (names.Count < 2 || engsInFolder[folder] > MaxUnitMembers) continue;

                    string h2 = names[0]; double best2 = EngPowerKw(h2, folder);
                    foreach (var n2 in names)
                    {
                        double kw = EngPowerKw(n2, folder);
                        if (kw > best2) { best2 = kw; h2 = n2; }
                    }
                    var members2 = new List<string> { h2 };
                    foreach (var n2 in names)
                        if (!string.Equals(n2, h2, StringComparison.OrdinalIgnoreCase) && SimilarEngNames(n2, h2))
                            members2.Add(n2);
                    if (members2.Count < 2) continue;

                    var cores2 = new List<string>();
                    foreach (var mn in members2)
                    {
                        bool dup = false;
                        foreach (var c2 in cores2) if (MirrorEngNames(mn, c2)) { dup = true; break; }
                        if (!dup) cores2.Add(mn);
                    }
                    var unit2 = new EngUnit { Head = h2, Members = members2, RepConsist = null, Cars = Math.Max(cores2.Count, 2) };
                    foreach (var mn in members2)
                    {
                        units[mn] = unit2;
                        if (!string.Equals(mn, h2, StringComparison.OrdinalIgnoreCase)) hidden.Add(mn);
                    }
                }

                // Coches INTERIORES de una composición fija: llevan acople rígido y NUNCA encabezan un
                // tren en todo el contenido. No son una máquina que comprar (una locomotora siempre
                // encabeza alguno y jamás lleva barra). Nunca se descarta la cabeza de una formación.
                foreach (var kv in posOf)
                {
                    string name = kv.Key;
                    if (leadCount.ContainsKey(name)) continue;                        // alguna vez va en cabeza
                    if (units.TryGetValue(name, out var u0) && u0 != null
                        && string.Equals(u0.Head, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!folderOf.TryGetValue(name, out var fo2)) continue;
                    if (RigidCouplingOf(name, fo2) == Rigid.None) continue;
                    hidden.Add(name);
                }
            }
            catch { }
            _engUnits = units;
            _engHidden = hidden;
            _conEngines = conEngines;
            _firstConsistOfEng = firstConsist;
            GuardarEnIndice(units, hidden, conEngines, firstConsist);
        }

        // ---- índice en disco: formaciones fijas ----
        bool RestoreEngUnitsFromIndex()
        {
            try
            {
                if (!ContentIndex.HasUnits) return false;
                var units = new Dictionary<string, EngUnit>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in ContentIndex.Units)
                {
                    var r = kv.Value;
                    var u = new EngUnit { Head = r.Head, RepConsist = r.RepConsist, Cars = r.Cars, RigidCoupled = r.Rigid };
                    u.Members.AddRange(r.Members);
                    units[r.Head] = u;
                    foreach (var m in r.Members) units[m] = u;
                }
                _engUnits = units;
                _engHidden = new HashSet<string>(ContentIndex.Hidden, StringComparer.OrdinalIgnoreCase);
                _conEngines = new Dictionary<string, List<string>>(ContentIndex.ConsistEngines, StringComparer.OrdinalIgnoreCase);
                _firstConsistOfEng = new Dictionary<string, string>(ContentIndex.FirstConsistOfEng, StringComparer.OrdinalIgnoreCase);
                return _engUnits.Count > 0;
            }
            catch { return false; }
        }

        static void GuardarEnIndice(Dictionary<string, EngUnit> units, HashSet<string> hidden,
                                    Dictionary<string, List<string>> conEngines, Dictionary<string, string> firstConsist)
        {
            try
            {
                // Una fila por formación (la cabeza), no una por miembro.
                var filas = new Dictionary<string, ContentIndex.UnitRow>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in units)
                {
                    var u = kv.Value;
                    if (u == null || string.IsNullOrEmpty(u.Head) || filas.ContainsKey(u.Head)) continue;
                    var r = new ContentIndex.UnitRow { Head = u.Head, RepConsist = u.RepConsist, Cars = u.Cars, Rigid = u.RigidCoupled };
                    r.Members.AddRange(u.Members);
                    filas[u.Head] = r;
                }
                ContentIndex.SetUnits(filas, hidden, conEngines, firstConsist);
                // El índice se guarda cuando termina TODA la carga (LoadStep), no aquí: así entran
                // también las cabeceras de los vagones que lee después el editor de composiciones.
            }
            catch { }
        }

        // Todos los .eng del contenido: <carpeta>\TRAINS\TRAINSET\<carpeta del modelo>\<nombre>.eng
        IEnumerable<(string name, string folder)> AllEngNames()
        {
            var list = new List<(string, string)>();
            foreach (var (name, folder, isEngine) in AllVehicleNames())
                if (isEngine) list.Add((name, folder));
            return list;
        }

        // Todos los vehículos del contenido, motrices (.eng) y remolcados (.wag): los remolques
        // cuentan para saber de cuántos coches es una formación de acople rígido.
        static List<(string name, string folder, bool isEngine)> _allVehicles; static string _allVehiclesFolder;

        IEnumerable<(string name, string folder, bool isEngine)> AllVehicleNames()
        {
            // El recorrido de TRAINSET son miles de carpetas: se hace UNA vez por contenido.
            string root0 = _curFolder?.Path ?? "";
            var cached = _allVehicles;
            if (cached != null && _allVehiclesFolder == root0) return cached;

            var list = new List<(string, string, bool)>();
            try
            {
                var trainset = System.IO.Path.Combine(_curFolder?.Path ?? "", "TRAINS", "TRAINSET");
                if (!System.IO.Directory.Exists(trainset)) return list;
                foreach (var dir in System.IO.Directory.GetDirectories(trainset))
                {
                    string folder = System.IO.Path.GetFileName(dir);
                    foreach (var eng in System.IO.Directory.GetFiles(dir, "*.eng"))
                        list.Add((System.IO.Path.GetFileNameWithoutExtension(eng), folder, true));
                    foreach (var wag in System.IO.Directory.GetFiles(dir, "*.wag"))
                        list.Add((System.IO.Path.GetFileNameWithoutExtension(wag), folder, false));
                }
            }
            catch { }
            _allVehiclesFolder = root0;
            _allVehicles = list;
            return list;
        }

        // Enganches de un vehículo. Un acople de BARRA no se puede soltar, así que ese extremo solo
        // tiene sentido dentro de una formación fija: es la prueba, escrita en el propio archivo, de
        // que el coche forma unidad con otro. Además dice dónde va:
        //   End    → barra en un extremo y enganche normal en el otro: coche de cabeza o de cola.
        //   Middle → barra en los dos (o un único bloque de barra, que vale para ambos): intermedio.
        enum Rigid { None, End, Middle }

        // Cabecera de un vehículo: enganches, tipo declarado y potencia salen de la MISMA lectura.
        // Antes se abría el archivo tres veces (una por dato) y eran miles de archivos.
        sealed class VehHead
        {
            public Rigid Coupling = Rigid.None;
            public string Kind = "";
            public double PowerKw = -1;
        }
        static readonly Dictionary<string, VehHead> _vehHead = new(StringComparer.OrdinalIgnoreCase);

        static readonly System.Text.RegularExpressions.Regex CouplingRx = new(
            @"Coupling\s*\((?<body>(?:[^()]|\([^()]*\)){0,400})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        static readonly System.Text.RegularExpressions.Regex TypeRx = new(
            @"(?<![A-Za-z])Type\s*\(\s*([A-Za-z]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        static readonly System.Text.RegularExpressions.Regex PowerRx = new(
            @"MaxPower\s*\(\s*([0-9.]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        static VehHead Head(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (_vehHead) if (_vehHead.TryGetValue(path, out var hit)) return hit;
            // Del índice en disco, si lo trae ya analizado de un arranque anterior.
            if (ContentIndex.Heads.TryGetValue(path, out var idx))
            {
                var hi = new VehHead { Coupling = (Rigid)idx.coupling, Kind = idx.kind ?? "", PowerKw = idx.powerKw };
                lock (_vehHead) _vehHead[path] = hi;
                return hi;
            }
            var h = new VehHead();
            try
            {
                // Principio y final: los enganches y el tipo van arriba; la potencia, en el bloque
                // Engine, al final. Antes se leían 48 KB seguidos y en los archivos grandes la
                // potencia se quedaba fuera.
                string text = FastConsists.ReadHeadTail(path, 16000, 24000);
                int bars = 0, couplings = 0;
                foreach (System.Text.RegularExpressions.Match m in CouplingRx.Matches(text))
                {
                    var ty = TypeRx.Match(m.Groups["body"].Value);
                    if (!ty.Success) continue;
                    couplings++;
                    if (ty.Groups[1].Value.StartsWith("Bar", StringComparison.OrdinalIgnoreCase)) bars++;
                }
                if (bars > 0) h.Coupling = (couplings == 1 || bars >= 2) ? Rigid.Middle : Rigid.End;

                foreach (System.Text.RegularExpressions.Match m in TypeRx.Matches(text))
                {
                    string t = m.Groups[1].Value.ToLowerInvariant();
                    bool known = false;
                    foreach (var k in VehicleTypes) if (t == k) { known = true; break; }
                    if (known) { h.Kind = t; break; }
                }

                var mp = PowerRx.Match(text);
                if (mp.Success && double.TryParse(mp.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var kw))
                    h.PowerKw = kw;
            }
            catch { }
            ContentIndex.Heads[path] = ((int)h.Coupling, h.Kind, h.PowerKw);
            lock (_vehHead) _vehHead[path] = h;
            return h;
        }

        Rigid RigidCoupling(string path) => Head(path)?.Coupling ?? Rigid.None;

        Rigid RigidCouplingOf(string name, string folder) => RigidCoupling(ResolveCarFile(name, folder));

        // ---- Tipo de vehículo DECLARADO en el archivo ----
        // El bloque Wagon de un .eng/.wag trae «Type ( Engine | Carriage | Freight | Tender |
        // Passenger )». Es la clasificación que hace el propio autor del modelo, así que manda sobre
        // cualquier deducción nuestra. Ojo: el bloque Engine tiene OTRO «Type» (Diesel/Electric…),
        // por eso solo se aceptan los valores de vehículo.
        static readonly string[] VehicleTypes = { "engine", "carriage", "freight", "tender", "passenger" };

        string DeclaredVehicleType(string path) => Head(path)?.Kind ?? "";

        // Tipo de tracción del vehículo, como lo guarda la empresa (electric / diesel / steam).
        public static string TractionName(string etype)
        {
            switch ((etype ?? "").ToLowerInvariant())
            {
                case "electric": return I18n.T("Eléctrica");
                case "diesel": return I18n.T("Diésel");
                case "steam": return I18n.T("Vapor");
                default: return (etype ?? "").Length > 0 ? etype : "—";
            }
        }

        // Nombre del tipo para la interfaz (en inglés se deja el término del archivo).
        public static string VehicleTypeName(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "engine": return I18n.T("Motriz");
                case "carriage": return I18n.T("Coche de viajeros");
                case "passenger": return I18n.T("Coche de viajeros");
                case "freight": return I18n.T("Vagón de mercancías");
                case "tender": return I18n.T("Ténder");
                default: return "";
            }
        }

        // Versión corta, para columnas de tabla.
        public static string VehicleTypeShort(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "engine": return I18n.T("Motriz");
                case "carriage": case "passenger": return I18n.T("Viajeros");
                case "freight": return I18n.T("Mercancías");
                case "tender": return I18n.T("Ténder");
                default: return "";
            }
        }

        // ¿La composición lleva coches DE VIAJEROS según sus archivos? (Carriage / Passenger)
        bool ConsistCarriesPeople(TrainItem c)
        {
            try
            {
                if (c?.FilePath == null) return false;
                foreach (var r in ConsistCarRefs(c.FilePath))
                {
                    string k = DeclaredVehicleType(ResolveCarFile(r.name, r.folder));
                    if (k == "carriage" || k == "passenger") return true;
                }
            }
            catch { }
            return false;
        }

        // Principio de un archivo de vehículo (los datos que interesan van arriba y hay miles).
        static string ReadHead(string path, int maxBytes = 160000)
        {
            try
            {
                byte[] buf;
                using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                {
                    buf = new byte[Math.Min(maxBytes, fs.Length)];
                    int read = fs.Read(buf, 0, buf.Length);
                    if (read < buf.Length) Array.Resize(ref buf, Math.Max(0, read));
                }
                if (buf.Length > 1 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF)))
                    return System.Text.Encoding.Unicode.GetString(buf);
                int zeros = 0, n2 = Math.Min(buf.Length, 200);
                for (int i = 0; i < n2; i++) if (buf[i] == 0) zeros++;
                return zeros > n2 / 4 ? System.Text.Encoding.Unicode.GetString(buf)
                                      : System.Text.Encoding.UTF8.GetString(buf);
            }
            catch { return ""; }
        }


        // Potencia de tracción DECLARADA en el .eng: 0 si declara MaxPower ( 0 ), -1 si no la declara
        // (muchas locomotoras la traen en un include, así que «no declarada» nunca significa sin
        // tracción). Lee solo el principio del archivo: la lista tiene miles de .eng.
        double DeclaredPowerKw(string engPath) => Head(engPath)?.PowerKw ?? -1;

        // Potencia declarada (kW) de un .eng del contenido; 0 si no se puede leer.
        double EngPowerKw(string name, string folder)
        {
            try
            {
                string path = ResolveCarFile(name, folder);
                return path == null ? 0 : ReadEngineSpecs(path).kw;
            }
            catch { return 0; }
        }

        // El mismo coche con otro nombre: uno es el otro más 1-2 caracteres al final («-i», «_i»…).
        static bool MirrorEngNames(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            string x = a.ToLowerInvariant(), y = b.ToLowerInvariant();
            if (x == y) return false;
            string lo = x.Length < y.Length ? x : y, hi = x.Length < y.Length ? y : x;
            int extra = hi.Length - lo.Length;
            return extra >= 1 && extra <= 2 && hi.StartsWith(lo, StringComparison.Ordinal);
        }

        // ¿El .eng «e» es un coche de la formación que encabeza «head»? Nunca sale sin ella, siempre
        // acoplado a menos de MaxUnitGap coches, misma carpeta y nombre de la misma familia.
        bool SameUnit(string e, string head,
            Dictionary<string, Dictionary<int, int>> posOf, Dictionary<string, string> folderOf)
        {
            if (!folderOf.TryGetValue(e, out var fe) || !folderOf.TryGetValue(head, out var fh)) return false;
            if (!string.Equals(fe, fh, StringComparison.OrdinalIgnoreCase)) return false;
            // Nombres de la misma familia… o, mejor aún, que los dos lleven acople rígido: eso ya
            // dice que van en formación fija, aunque el pack los haya nombrado de cualquier manera.
            if (!SimilarEngNames(e, head)
                && !(RigidCouplingOf(e, fe) != Rigid.None && RigidCouplingOf(head, fh) != Rigid.None)) return false;
            if (!posOf.TryGetValue(e, out var pe) || !posOf.TryGetValue(head, out var ph)) return false;
            if (pe.Count > ph.Count) return false;
            foreach (var kv in pe)
            {
                if (!ph.TryGetValue(kv.Key, out var ip)) return false;            // sale sin la cabeza
                if (Math.Abs(ip - kv.Value) > MaxUnitGap) return false;           // no va acoplado a ella
            }
            return true;
        }

        // Nombres de la misma familia: uno contiene al otro, o comparten 3 caracteres de principio o
        // de final (592-S-Mot / 592-S-Cola, UT592Mg / UT592MBg, MCab592 / MCola592…).
        static bool SimilarEngNames(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            string x = a.ToLowerInvariant(), y = b.ToLowerInvariant();
            if (x.Contains(y) || y.Contains(x)) return true;
            int n = Math.Min(x.Length, y.Length), pre = 0, suf = 0;
            while (pre < n && x[pre] == y[pre]) pre++;
            while (suf < n && x[x.Length - 1 - suf] == y[y.Length - 1 - suf]) suf++;
            return pre >= 3 || suf >= 3;
        }

        // Nombres de los coches MOTRICES (.eng) de un .con: primer token de cada bloque
        // "EngineData ( <nombre> <carpeta> )" (formato texto MSTS, independiente de versión).
        List<string> ConsistEngineNames(string conPath)
        {
            var cache = _conEngines;
            if (cache != null && conPath != null && cache.TryGetValue(conPath, out var known)) return known;
            var list = new List<string>();
            try
            {
                string t = ReadMstsText(conPath);
                if (!string.IsNullOrEmpty(t))
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                                 t, @"EngineData\s*\(\s*([^\s)]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        list.Add(m.Groups[1].Value.Trim().Trim('"'));
            }
            catch { }
            return list;
        }

        // Preview 3D rotatable de la CABEZA del consist seleccionado en "Comprar / alquilar":
        // construye la geometría (para poder girar con el ratón) y la renderiza en vivo.
        void RenderFleetAddPreview()
        {
            if (_buyByMachine) return;   // la vista activa es «Por máquina»
            if (_fleetPreview == null || _fleetConsistList == null) return;
            var c = _fleetConsistList.SelectedItem as TrainItem;
            _fleetPreview.Image = null; _fleetPreview.Rotatable = false;
            _fleetGeom = null; _fleetYaw = 0; _fleetPitch = 0; _fleetFlip = false;
            string engPath = c?.Locomotive?.FilePath;
            if (c == null || string.IsNullOrEmpty(engPath)) { _fleetPrevEngPath = null; _fleetPreview.Caption = ""; _fleetPreview.Invalidate(); return; }
            _fleetPrevEngPath = engPath;
            _fleetPreview.Caption = c.Name;
            _fleetPreview.Invalidate();
            lock (_fleetRenderingShapes) { if (!_fleetRenderingShapes.Add(engPath)) return; }
            Task.Run(() =>
            {
                ShapeGeom geom = null;
                try { geom = ShapeRenderer.BuildGeometry(engPath); } catch { }
                if (!IsHandleCreated) { lock (_fleetRenderingShapes) _fleetRenderingShapes.Remove(engPath); return; }
                BeginInvoke((Action)(() =>
                {
                    try
                    {
                        if (_fleetPrevEngPath == engPath && geom != null)
                        {
                            _fleetGeom = geom;
                            _fleetPreview.Rotatable = true;
                            RenderFleetLive();
                        }
                    }
                    catch { }
                    lock (_fleetRenderingShapes) _fleetRenderingShapes.Remove(engPath);
                }));
            });
        }

        // Renderiza la geometría actual del preview de Flota a la resolución real del panel.
        void RenderFleetLive()
        {
            if (_fleetGeom == null || _fleetPreview == null || _fleetPreview.Width < 40 || _fleetPreview.Height < 40) return;
            int w = Math.Min(1400, Math.Max(128, (_fleetPreview.Width - 8) * 2));
            int h = Math.Min(820, Math.Max(96, (_fleetPreview.Height - 36) * 2));
            var bmp = ShapeRenderer.Render(_fleetGeom, w, h, _fleetYaw, _fleetPitch, 1, _fleetFlip, _fleetPreview.CamDistance());
            if (bmp != null) _fleetPreview.Image = bmp;
        }

        void OnFleetPreviewDrag(int dx, int dy)
        {
            if (_fleetGeom == null) return;
            _fleetYaw += dx * 0.6f;
            _fleetPitch = Math.Max(-55f, Math.Min(70f, _fleetPitch - dy * 0.6f));
            RenderFleetLive();
        }

        void OnFleetPreviewReset() { if (_fleetGeom == null) return; _fleetYaw = 0; _fleetPitch = 0; RenderFleetLive(); }

        // ---- Vista 3D + ficha del vehículo propio seleccionado (pestaña Flota, como Compra) ----
        void OnFleetVehicleSelected()
        {
            if (_fleetOwnTitle == null) return;
            int i = _fleetList?.SelectedRow ?? -1;
            if (i < 0 || i >= _fleetRowDetail.Count)
            {
                _fleetOwnTitle.Text = Tr("Elige un vehículo");
                if (_voKind != null) _voKind.Text = "—"; if (_voProp != null) _voProp.Text = "—";
                if (_voMaint != null) _voMaint.Text = "—"; if (_voEstado != null) { _voEstado.Text = "—"; _voEstado.ForeColor = Theme.Text; }
                if (_voCap != null) _voCap.Text = "—"; if (_voTraction != null) _voTraction.Text = "—";
                RenderFleetOwn(null);
                return;
            }
            var det = _fleetRowDetail[i];
            _fleetOwnTitle.Text = det[0];
            if (_voKind != null) _voKind.Text = string.IsNullOrEmpty(det[1]) ? "—" : det[1];
            if (_voProp != null) _voProp.Text = string.IsNullOrEmpty(det[2]) ? "—" : det[2];
            if (_voMaint != null) _voMaint.Text = string.IsNullOrEmpty(det[3]) ? "—" : det[3];
            if (_voEstado != null) { _voEstado.Text = det[4]; _voEstado.ForeColor = i < _fleetRowEstColor.Count ? _fleetRowEstColor[i] : Theme.Text; }
            if (_voCap != null) _voCap.Text = det.Length > 5 && det[5].Length > 0 ? det[5] : "—";
            if (_voTraction != null) _voTraction.Text = det.Length > 6 && det[6].Length > 0 ? det[6] : "—";
            RenderFleetOwn(_fleetRowEng[i]);
        }

        void RenderFleetOwn(string engPath)
        {
            if (_fleetOwnPreview == null) return;
            // Misma perspectiva que los visores de Exploración, Horarios y Compra (vista de costado).
            _fleetOwnGeom = null; _fleetOwnYaw = 0; _fleetOwnPitch = 0; _fleetOwnFlip = false; _fleetOwnEngPath = engPath;
            _fleetOwnPreview.Image = null; _fleetOwnPreview.Rotatable = false;
            _fleetOwnPreview.EmptyText = string.IsNullOrEmpty(engPath) ? Tr("Elige un vehículo") : null;
            if (string.IsNullOrEmpty(engPath)) { _fleetOwnPreview.Caption = ""; _fleetOwnPreview.Invalidate(); return; }
            _fleetOwnPreview.Caption = _fleetOwnTitle?.Text ?? "";
            _fleetOwnPreview.Invalidate();
            lock (_fleetOwnRendering) { if (!_fleetOwnRendering.Add(engPath)) return; }
            Task.Run(() =>
            {
                ShapeGeom geom = null;
                try { geom = ShapeRenderer.BuildGeometry(engPath); } catch { }
                if (!IsHandleCreated) { lock (_fleetOwnRendering) _fleetOwnRendering.Remove(engPath); return; }
                BeginInvoke((Action)(() =>
                {
                    try { if (_fleetOwnEngPath == engPath && geom != null) { _fleetOwnGeom = geom; _fleetOwnPreview.Rotatable = true; RenderFleetOwnLive(); } }
                    catch { }
                    lock (_fleetOwnRendering) _fleetOwnRendering.Remove(engPath);
                }));
            });
        }

        // El marco de Flota es alto: la cámara se separa un poco más para que el vehículo respire.
        const float FleetOwnCamDistance = 2.7f;

        void RenderFleetOwnLive()
        {
            if (_fleetOwnGeom == null || _fleetOwnPreview == null || _fleetOwnPreview.Width < 40 || _fleetOwnPreview.Height < 40) return;
            int w = Math.Min(1400, Math.Max(128, (_fleetOwnPreview.Width - 8) * 2));
            int h = Math.Min(820, Math.Max(96, (_fleetOwnPreview.Height - 36) * 2));
            var bmp = ShapeRenderer.Render(_fleetOwnGeom, w, h, _fleetOwnYaw, _fleetOwnPitch, 1, _fleetOwnFlip, _fleetOwnPreview.CamDistance(FleetOwnCamDistance));
            if (bmp != null) _fleetOwnPreview.Image = bmp;
        }

        void OnFleetOwnDrag(int dx, int dy)
        {
            if (_fleetOwnGeom == null) return;
            _fleetOwnYaw += dx * 0.6f;
            _fleetOwnPitch = Math.Max(-55f, Math.Min(70f, _fleetOwnPitch - dy * 0.6f));
            RenderFleetOwnLive();
        }
        void OnFleetOwnReset() { if (_fleetOwnGeom == null) return; _fleetOwnYaw = 0; _fleetOwnPitch = 0; RenderFleetOwnLive(); }

        // Composición 2D del vehículo de la flota seleccionado (misma ventana que Compra, con leyenda).
        void OpenFleetComposition()
        {
            int i = _fleetList?.SelectedRow ?? -1;
            if (i < 0 || i >= _fleetRowEng.Count) { Msg(_fleetMsg, Tr("Elige un vehículo de la lista."), true); return; }
            string eng = _fleetRowEng[i];
            if (string.IsNullOrEmpty(eng)) { Msg(_fleetMsg, Tr("Este vehículo no lo tienes en tu contenido local, no se puede ver."), true); return; }
            if (_curFolder == null) { Msg(_fleetMsg, Tr("Selecciona una carpeta de contenido."), true); return; }
            string title = i < _fleetRowDetail.Count ? _fleetRowDetail[i][0] : "";
            using var dlg = new CompositionDialog(eng, _curFolder.Path, title, ClassifyCarForFleet);
            dlg.ShowDialog(this);
        }

        // Los vehículos de la flota son propios → verde ("ya la tienes"); coches sin tracción, gris.
        Color? ClassifyCarForFleet(string name, bool isEngine) => isEngine ? Theme.Accent : Theme.Subtle;

        // ---- Tasación en el cliente (refleja fleet_value del backend, que es la autoridad) ----

        // Lee potencia (kW), velocidad máxima (km/h) y tipo (electric/diesel/steam) del .eng.
        (double kw, double kmh, string type) ReadEngineSpecs(string engPath)
        {
            double kw = 0, kmh = 0; string type = "electric";
            var v = Veh(engPath);
            if (v != null) { kw = v.PowerKw; kmh = v.SpeedKmh; type = v.EngineType; }
            if (kw <= 0) kw = 1000;   // valores razonables si el .eng no los declara
            if (kmh <= 0) kmh = 100;
            return (kw, kmh, type);
        }

        static double ExtractPowerKw(string t)
        {
            var m = System.Text.RegularExpressions.Regex.Match(t, @"MaxPower\s*\(\s*([\d.]+)\s*([a-zA-Z/]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return 0;
            string u = m.Groups[2].Value.ToLowerInvariant();
            if (u.Contains("kw")) return v;
            if (u.Contains("hp")) return v * 0.7457;
            if (u.Contains("w")) return v / 1000.0;                     // vatios
            return v > 10000 ? v / 1000.0 : v;                         // sin unidad: MSTS usa vatios
        }

        static double ExtractSpeedKmh(string t)
        {
            var m = System.Text.RegularExpressions.Regex.Match(t, @"MaxVelocity\s*\(\s*([\d.]+)\s*([a-zA-Z/]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return 0;
            string u = m.Groups[2].Value.ToLowerInvariant();
            double kmh;
            if (u.Contains("kmh") || u.Contains("km/h") || u.Contains("kph")) kmh = v;
            else if (u.Contains("mph")) kmh = v * 1.60934;
            else kmh = v * 3.6;                                         // m/s por defecto
            return kmh > 400 ? 400 : kmh;
        }

        // Cuenta cars motrices (Engine) y totales de un .con (formato texto MSTS, independiente de versión).
        static (int engineCars, int totalCars) ConsistCounts(string conPath)
        {
            int eng = 0, total = 0;
            foreach (var r in ConsistCarRefs(conPath)) { total++; if (r.isEngine) eng++; }
            return (eng, total);
        }

        // Decide si el .eng suele formar un automotor (≥2 cars motrices en un consist) y cuántos coches lleva.
        // Locomotora o automotor:
        //  1) Si la propia MOTRIZ lleva viajeros (PassengerCapacity o vista de pasajero) → AUTOMOTOR.
        //     Las locomotoras nunca llevan viajeros; las motrices de un automotor casi siempre sí.
        //     Nº de coches = el consist MÁS CORTO (≥ 2 coches) que encabeza (evita contar dobles composiciones).
        //  2) Respaldo (motriz sin datos de viajeros): automotor solo si en algún consist todos los coches
        //     son motrices (formación fija sin vagones). Así la DOBLE TRACCIÓN (2 locos + vagones) es locomotora.
        (bool automotor, int cars) DetectUnitShape(string engPath)
        {
            // Formación fija deducida del contenido (motriz + remolque + motriz, dobles motrices…).
            // Manda sobre el resto: la mayoría de automotores no declara PassengerCapacity en el .eng
            // y lleva remolques intermedios, así que ninguna de las dos reglas de abajo los detecta.
            var unit = UnitOfPath(engPath);
            if (unit != null && (unit.Members.Count > 1 || unit.Cars > 1)
                && string.Equals(unit.Head, System.IO.Path.GetFileNameWithoutExtension(engPath), StringComparison.OrdinalIgnoreCase))
                return (true, Math.Max(unit.Cars, 2));

            // Solo PLAZAS DECLARADAS: la vista de pasajero NO sirve en una motriz (hay locomotoras,
            // p. ej. la 310, que la incluyen y no llevan viajeros).
            bool carriesPax = (Veh(engPath)?.Capacity ?? 0) > 0;
            int shortest = int.MaxValue, fixedFormation = int.MaxValue;
            try
            {
                foreach (var c in _consistsAll)
                {
                    var lp = c?.Locomotive?.FilePath;
                    if (lp == null || !string.Equals(lp, engPath, StringComparison.OrdinalIgnoreCase)) continue;
                    var (e, tot) = ConsistCounts(c.FilePath);
                    if (tot >= 2 && tot < shortest) shortest = tot;
                    if (e >= 2 && e == tot && tot < fixedFormation) fixedFormation = tot;   // solo motrices
                }
            }
            catch { }
            if (carriesPax) return (true, shortest != int.MaxValue ? shortest : 1);
            if (fixedFormation != int.MaxValue) return (true, fixedFormation);
            return (false, 1);
        }

        // Espejo de fleet_value(...) del backend (escala real), con masa/freno y la escala/% de alquiler.
        (double price, double rent) FleetPriceLocal(double power, double speed, string etype, bool automotor, int cars, double mass, double brake)
        {
            double basev;
            if (automotor)
            {
                double f = etype == "steam" ? 0.8 : etype == "diesel" ? 0.95 : 1.0;
                basev = (Math.Max(1, cars) * 1_600_000.0 + mass * 1500.0 + brake * 2000.0) * f;
            }
            else
            {
                double f = etype == "steam" ? 0.7 : etype == "diesel" ? 1.0 : 1.1;
                basev = (power * 600.0 + speed * 4000.0 + mass * 1500.0 + brake * 2000.0 + 250_000.0) * f;
            }
            double price = basev * _fleetScale;
            return (price, price * _fleetRentPct);
        }

        // --- Lectura de datos técnicos ampliados del .eng/.wag (masa, freno, capacidad, tamaño) ---
        static double ExtractMassT(string t)
        {
            // Algunos .wag ponen la masa entrecomillada, p. ej. Mass ( "17t" ) — el "?" la tolera.
            var m = System.Text.RegularExpressions.Regex.Match(t, @"Mass\s*\(\s*""?\s*([\d.]+)\s*([a-zA-Z]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return 0;
            string u = m.Groups[2].Value.ToLowerInvariant();
            if (u.StartsWith("t")) return v;                 // toneladas
            if (u.StartsWith("kg")) return v / 1000.0;       // kilos
            return v > 1000 ? v / 1000.0 : v;                // sin unidad: MSTS suele usar kg si es grande
        }

        static double ExtractBrakeKn(string t)
        {
            var m = System.Text.RegularExpressions.Regex.Match(t, @"MaxBrakeForce\s*\(\s*([\d.]+)\s*([a-zA-Z/]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return 0;
            string u = m.Groups[2].Value.ToLowerInvariant();
            if (u.Contains("kn")) return v;
            if (u.Contains("n")) return v / 1000.0;          // newtons
            return v > 1000 ? v / 1000.0 : v;                // sin unidad: MSTS suele usar N
        }

        // Size ( ancho alto largo ) en m → devuelve (ancho, largo). Los valores pueden llevar la
        // unidad pegada, p. ej. "2.9786m 3.74862m 25.5292m".
        static (double w, double l) ExtractSize(string t)
        {
            var m = System.Text.RegularExpressions.Regex.Match(t, @"Size\s*\(\s*([\d.]+)[a-zA-Z]*\s+([\d.]+)[a-zA-Z]*\s+([\d.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return (0, 0);
            double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double w);
            double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double l);
            return (w, l);
        }

        // Plazas de pasajeros: SOLO si el .eng/.wag declara PassengerCapacity explícitamente — no se
        // estima por área. Un coche sin ese dato se ignora (no cuenta ni penaliza) en capacidad,
        // confort, densidad y tipo de servicio; ver AnalyzeComposition.
        static double ExtractCapacity(string t)
        {
            var m = System.Text.RegularExpressions.Regex.Match(t, @"PassengerCapacity\s*\(\s*([\d.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        }

        static bool IsTilting(string t)
        {
            string low = t.ToLowerInvariant();
            return low.Contains("basculante") || low.Contains("pendular") || low.Contains("pendolino") || low.Contains("tilting");
        }

        // Referencias de coches de un .con: (nombre, carpeta, ¿es motriz?). La carpeta puede ir
        // entrecomillada (contiene espacios), p. ej. EngineData ( RN446CerMa_001 "[V3D] UT446-001 …" ).
        // Se compila una vez: se usa en los miles de .con del contenido.
        static readonly System.Text.RegularExpressions.Regex CarRefRx = new(
            @"(Engine|Wagon)Data\s*\(\s*([^\s)]+)\s+(?:""([^""]*)""|([^\s)]+))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        static List<(string name, string folder, bool isEngine)> ConsistCarRefs(string conPath)
        {
            if (string.IsNullOrEmpty(conPath)) return new List<(string, string, bool)>();
            lock (_conRefs) if (_conRefs.TryGetValue(conPath, out var hit)) return hit;
            var list = new List<(string name, string folder, bool isEngine)>();
            try
            {
                string t = ReadMstsText(conPath);
                if (!string.IsNullOrEmpty(t))
                    foreach (System.Text.RegularExpressions.Match m in CarRefRx.Matches(t))
                    {
                        string name = m.Groups[2].Value.Trim().Trim('"');
                        string folder = (m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value).Trim().Trim('"');
                        bool isEng = m.Groups[1].Value.Equals("Engine", StringComparison.OrdinalIgnoreCase);
                        list.Add((name, folder, isEng));
                    }
            }
            catch { }
            lock (_conRefs) _conRefs[conPath] = list;
            return list;
        }

        // TrainItem "representativo" del automotor cuya cabeza es engPath (el de más coches motrices).
        string FindUnitConsist(string engPath)
        {
            var unit = UnitOfPath(engPath);
            if (unit?.RepConsist != null && (unit.Members.Count > 1 || unit.Cars > 1)) return unit.RepConsist;
            string best = null; int bestEng = -1;
            try
            {
                foreach (var c in _consistsAll)
                {
                    var lp = c?.Locomotive?.FilePath;
                    if (lp == null || !string.Equals(lp, engPath, StringComparison.OrdinalIgnoreCase)) continue;
                    var refs = ConsistCarRefs(c.FilePath);
                    int eng = 0; foreach (var r in refs) if (r.isEngine) eng++;
                    if (eng > bestEng) { bestEng = eng; best = c.FilePath; }
                }
            }
            catch { }
            return best;
        }

        // Ruta del archivo de un coche: <root>\TRAINS\TRAINSET\<carpeta>\<nombre>.eng (o .wag).
        string ResolveCarFile(string name, string folder)
        {
            try
            {
                string root = _curFolder?.Path;
                if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(folder)) return null;
                string dir = System.IO.Path.Combine(root, "TRAINS", "TRAINSET", folder);
                string eng = System.IO.Path.Combine(dir, name + ".eng");
                if (System.IO.File.Exists(eng)) return eng;
                string wag = System.IO.Path.Combine(dir, name + ".wag");
                if (System.IO.File.Exists(wag)) return wag;
            }
            catch { }
            return null;
        }

        // Masa (t) y freno (kN) de la MÁQUINA DE TRACCIÓN a tasar: para un automotor de formación
        // fija, sumados de todos sus propios coches (siempre van juntos, es un único vehículo real);
        // para una locomotora suelta, solo los suyos (los coches que arrastre no son "suyos": son
        // rodante independiente, y su confort/capacidad se calculan aparte, por composición).
        (double mass, double brake) AnalyzeUnitPhysics(string engPath, bool automotor)
        {
            var carPaths = new List<string>();
            if (automotor)
            {
                string con = FindUnitConsist(engPath);
                if (con != null)
                    foreach (var r in ConsistCarRefs(con))
                    {
                        string p = ResolveCarFile(r.name, r.folder);
                        if (p != null) carPaths.Add(p);
                    }
            }
            if (carPaths.Count == 0) carPaths.Add(engPath);   // loco o sin consist reconocible

            double mass = 0, brake = 0;
            foreach (var p in carPaths)
            {
                var v = Veh(p);
                if (v == null) continue;
                mass += v.MassT; brake += v.BrakeKn;
            }
            return (mass, brake);
        }

        // Resultado de analizar una composición completa (tracción + coches) para el showroom.
        sealed class CompositionAnalysis
        {
            public double Capacity;      // plazas totales (suma de los coches con PassengerCapacity conocido)
            public double Comfort;       // 0-100; 0 si es un tren de mercancías (sin coches de viajeros)
            public double Density;       // pax / m², de los coches con capacidad conocida
            public string ServiceType = "";   // Cercanías / Media Distancia / Larga Distancia / Alta Velocidad / Mercancías
            public bool Freight;         // composición de MERCANCÍAS → su tracción NUNCA es un automotor
            public bool DeclaredPax;     // sus archivos declaran coches de viajeros (Type ( Carriage ))
        }

        // Un tren de mercancías no puede ser un automotor: su unidad de tracción se trata como
        // LOCOMOTORA (1 coche) para la ficha, el precio y el alta en la flota.
        (bool automotor, int cars) ShapeForService((bool automotor, int cars) detected, bool freight, string engPath = null)
            => (freight && !IsUnitHead(engPath)) ? (false, 1) : detected;

        // Analiza TODA la composición del consist REALMENTE elegido en el showroom (no un consist
        // "representativo" encontrado en otra parte del contenido) — cabeza de tracción Y coches
        // (.wag incluidos). SOLO cuentan los coches que declaran PassengerCapacity: basta con que UNO
        // del consist lo tenga para que sea tren de viajeros (suban y bajen). Si ninguno lo declara,
        // se clasifica como mercancías.
        CompositionAnalysis AnalyzeComposition(TrainItem c, double leadSpeedKmh)
        {
            var result = new CompositionAnalysis();
            double capSum = 0, areaSum = 0; bool anyTilting = false;
            if (c?.FilePath != null)
            {
                foreach (var r in ConsistCarRefs(c.FilePath))
                {
                    var v = Veh(ResolveCarFile(r.name, r.folder));
                    if (v == null || v.Capacity <= 0) continue;   // sin PassengerCapacity declarado: no cuenta
                    capSum += v.Capacity;
                    areaSum += (v.Width > 0 && v.Length > 0) ? v.Width * v.Length : 0;
                    if (v.Tilting) anyTilting = true;
                }
            }
            result.DeclaredPax = ConsistCarriesPeople(c);
            if (capSum <= 0)
            {
                // Sin plazas declaradas: si los archivos dicen que son coches de viajeros, es un tren
                // de viajeros aunque nadie haya puesto PassengerCapacity (solo cambia la etiqueta; el
                // embarque de viajeros sigue necesitando PassengerCapacity).
                result.ServiceType = result.DeclaredPax ? ClassifyServiceType(leadSpeedKmh, 0) : Tr("Mercancías");
                result.Freight = true;
                return result;   // Capacity=Comfort=Density=0
            }

            result.Capacity = capSum;
            result.Density = areaSum > 0 ? capSum / areaSum : 0;

            // Confort: la velocidad máxima es el indicador principal (a más velocidad de diseño, mejor
            // suspensión/insonorización/aerodinámica), penalizado por ir denso (cercanías, con plazas
            // de pie) y con un plus si el material es basculante.
            double speedComfort = Math.Max(20, Math.Min(95, 20 + leadSpeedKmh * 0.28));
            double densityPenalty = Math.Max(0, Math.Min(25, result.Density * 12));
            result.Comfort = Math.Round(Math.Max(0, Math.Min(100, speedComfort - densityPenalty + (anyTilting ? 8 : 0))));

            result.ServiceType = ClassifyServiceType(leadSpeedKmh, result.Density);
            return result;
        }

        // Clasificación de servicio: MSTS/OR no trae un dato de "tipo de tren", así que se infiere
        // por velocidad máxima (el corte habitual entre cercanías/media/larga/alta velocidad) y,
        // para desempatar en la banda media, la densidad de viajeros (cercanías va más denso/de pie
        // que media distancia a velocidades parecidas). Umbrales ajustables, no un estándar oficial.
        static string ClassifyServiceType(double speedKmh, double density)
        {
            var c = PaxCfg;
            if (speedKmh >= c.AvKmh) return Tr("Alta Velocidad");
            if (speedKmh >= c.LargaKmh) return Tr("Larga Distancia");
            if (speedKmh >= c.MediaKmh && density < c.MediaMaxDensity) return Tr("Media Distancia");
            return Tr("Cercanías");
        }

        // Encabezado de la ficha: los coches que se llevan (si son varios), si van con acople rígido y
        // el tipo de servicio. Sin etiquetas de «locomotora» o «automotor»: lo que importa al comprar
        // es cuántos coches entran en el trato y para qué sirve el tren.
        string SpecTitle(int cars, bool rigid, string service, string kind = null)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(kind)) parts.Add(kind);
            if (cars > 1) parts.Add(string.Format(Tr("{0} coches"), cars));
            if (rigid) parts.Add(Tr("acople rígido"));
            if (!string.IsNullOrWhiteSpace(service)) parts.Add(service);
            return parts.Count > 0 ? string.Join("  ·  ", parts) : Tr("Sin datos");
        }

        // Vacía la ficha técnica del showroom (sin tren seleccionado).
        void ClearFleetSpec()
        {
            if (_fleetSpecType != null) { _fleetSpecType.Text = Tr(_buyByMachine ? "Elige una máquina" : "Elige un tren"); _fleetSpecType.ForeColor = Theme.Subtle; }
            if (_fleetOwnedBadge != null) _fleetOwnedBadge.Visible = false;
            foreach (var l in new[] { _vPower, _vSpeed, _vPlazas, _vConfort, _vMasa, _vFreno, _vDensity, _vBuy, _vRent })
                if (l != null) l.Text = "—";
            if (_fleetBuyBtn != null) _fleetBuyBtn.Text = Tr("Comprar");
            if (_fleetRentBtn != null) _fleetRentBtn.Text = Tr("Alquilar");
        }

        // Tasa cada máquina de tracción comprable del consist elegido (desglose) — confort/capacidad/
        // densidad/tipo se calculan UNA VEZ para TODA la composición (AnalyzeComposition), no por
        // .eng — marca las que la empresa ya tiene y actualiza chips + totales + textos de botones.
        void UpdateFleetValuation()
        {
            if (_buyByMachine) return;   // la vista activa es «Por máquina»
            if (_fleetConsistList == null || _fleetSpecType == null) return;
            var c = _fleetConsistList.SelectedItem as TrainItem;
            if (c == null) { ClearFleetSpec(); return; }
            var units = PurchasableUnitsInConsist(c);
            if (units.Count == 0) { ClearFleetSpec(); return; }
            string selPath = c.FilePath;
            Task.Run(() =>
            {
                string leadName = null;
                var lfp = c.Locomotive?.FilePath;
                if (!string.IsNullOrEmpty(lfp)) leadName = System.IO.Path.GetFileNameWithoutExtension(lfp);
                string leadPath = (leadName != null ? EngPathByName(leadName) : null) ?? EngPathByName(units[0]);
                var (leadKw, leadKmh, leadType) = leadPath != null ? ReadEngineSpecs(leadPath) : (0, 0, "electric");
                var analysis = AnalyzeComposition(c, leadKmh);   // primero: si es de mercancías, no hay automotor
                var (leadAutomotor, leadCars) = ShapeForService(leadPath != null ? DetectUnitShape(leadPath) : (false, 1), analysis.Freight, leadPath);
                var (leadMass, leadBrake) = leadPath != null ? AnalyzeUnitPhysics(leadPath, leadAutomotor) : (0, 0);

                var quotes = new List<FleetUnitQuote>();
                foreach (var name in units)
                {
                    string path = EngPathByName(name);
                    if (path == null) continue;   // no debería pasar: el nombre viene de AvailableEngines()
                    var (kw, kmh, type) = ReadEngineSpecs(path);
                    var (automotor, cars) = ShapeForService(DetectUnitShape(path), analysis.Freight, path);
                    var (mass, brake) = AnalyzeUnitPhysics(path, automotor);
                    var (price, rentPrice) = FleetPriceLocal(kw, kmh, type, automotor, cars, mass, brake);
                    quotes.Add(new FleetUnitQuote
                    {
                        Name = name, Type = type, Kw = kw, Kmh = kmh, Mass = mass, Brake = brake,
                        Capacity = analysis.Capacity, Comfort = analysis.Comfort, Price = price, Rent = rentPrice,
                        Automotor = automotor, Cars = cars, Owned = _fleetOwnedNames.Contains(name)
                    });
                }
                if (!IsHandleCreated) return;
                BeginInvoke((Action)(() =>
                {
                    if ((_fleetConsistList.SelectedItem as TrainItem)?.FilePath != selPath) return;   // la selección cambió mientras tasábamos
                    if (quotes.Count == 0) { ClearFleetSpec(); return; }
                    _selPower = leadKw; _selSpeed = leadKmh; _selType = leadType; _selAutomotor = leadAutomotor; _selCars = leadCars;
                    _selMass = leadMass; _selBrake = leadBrake; _selCapacity = analysis.Capacity; _selComfort = analysis.Comfort;
                    _fleetSpecType.Text = SpecTitle(leadAutomotor ? leadCars : 1, false, analysis.ServiceType,
                        VehicleTypeName(DeclaredVehicleType(leadPath)));
                    _fleetSpecType.ForeColor = analysis.DeclaredPax || !analysis.Freight ? Theme.Accent : ColOrange;
                    if (_vPower != null) _vPower.Text = leadKw.ToString("N0", EsEs) + " kW";
                    if (_vSpeed != null) _vSpeed.Text = leadKmh.ToString("N0", EsEs) + " km/h";
                    if (_vPlazas != null) _vPlazas.Text = analysis.Capacity > 0 ? analysis.Capacity.ToString("N0", EsEs) : "—";
                    if (_vConfort != null) _vConfort.Text = analysis.Capacity > 0 ? analysis.Comfort.ToString("N0", EsEs) + "/100" : "—";
                    if (_vMasa != null) _vMasa.Text = leadMass > 0 ? leadMass.ToString("N0", EsEs) + " t" : "—";
                    if (_vFreno != null) _vFreno.Text = leadBrake > 0 ? leadBrake.ToString("N0", EsEs) + " kN" : "—";
                    if (_vDensity != null) _vDensity.Text = analysis.Density > 0 ? analysis.Density.ToString("N1", EsEs) + " pax/m²" : "—";

                    double buyTotal = 0, rentTotal = 0; bool anyPending = false;
                    foreach (var q in quotes)
                        if (!q.Owned) { buyTotal += q.Price; rentTotal += q.Rent; anyPending = true; }
                    // Ya las tiene todas: se puede tener el mismo tren varias veces → precio de OTRA unidad completa.
                    if (!anyPending) foreach (var q in quotes) { buyTotal += q.Price; rentTotal += q.Rent; }
                    _selPrice = buyTotal; _selRent = rentTotal;
                    if (_vBuy != null) _vBuy.Text = buyTotal.ToString("N0", EsEs) + " €";
                    if (_vRent != null) _vRent.Text = rentTotal.ToString("N0", EsEs) + " €";
                    // Comprar / Alquilar; si ya está en la flota, «Comprar otra» / «Alquilar otra».
                    if (_fleetBuyBtn != null) _fleetBuyBtn.Text = anyPending ? Tr("Comprar") : Tr("Comprar otra");
                    if (_fleetRentBtn != null) _fleetRentBtn.Text = anyPending ? Tr("Alquilar") : Tr("Alquilar otra");
                    if (_fleetOwnedBadge != null) _fleetOwnedBadge.Visible = !anyPending;
                }));
            });
        }

        async void BuyConsist() { await AcquireConsist(false); }
        async void RentConsist() { await AcquireConsist(true); }

        // Compra/alquila, UNA A UNA, las máquinas de tracción del consist elegido que la empresa
        // aún no tiene (mismo RPC de siempre, buy_vehicle/rent_vehicle: no hace falta backend nuevo).
        // Las que ya tenía no se tocan ni se cobran, y las nuevas quedan reutilizables en cualquier
        // otro consist que las lleve enganchadas (la puerta de acceso ya lo permite: ResolveCompanyUnitReason).
        async Task AcquireConsist(bool rent)
        {
            if (_empSel == null) { Msg(_buyMsg, Tr("Selecciona una empresa."), true); return; }
            if (!CanManage() && !Supa.IsSuperadmin) { Msg(_buyMsg, Tr("Solo el dueño o un gestor pueden gestionar la flota."), true); return; }
            var c = _fleetConsistList?.SelectedItem as TrainItem;
            if (c == null) { Msg(_buyMsg, Tr("Elige un tren de la lista."), true); return; }
            var units = PurchasableUnitsInConsist(c);
            if (units.Count == 0) { Msg(_buyMsg, Tr("Ese tren no tiene ninguna máquina de tracción reconocible."), true); return; }
            // Un tren se puede tener VARIAS veces (cada unidad con su matrícula). Si faltan unidades del
            // tren, se completan; si ya están todas, se pregunta si se quiere otra composición completa.
            var pending = new List<string>();
            foreach (var n in units) if (OwnedUnits(FolderOfConsistEng(c, n), n) == 0) pending.Add(n);
            if (pending.Count == 0)
            {
                int have = 0;
                foreach (var p in _fleetRowDetail) if (p.Length > 0 && units.Exists(u => p[0] == u || p[0].EndsWith("·  " + u))) have++;
                string q = string.Format(rent
                        ? Tr("Ya tienes este tren en la flota ({0} unidad(es)). ¿Alquilar otra unidad más?")
                        : Tr("Ya tienes este tren en la flota ({0} unidad(es)). ¿Comprar otra unidad más?"), have);
                if (MessageBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                pending.AddRange(units);
            }
            Msg(_buyMsg, rent ? Tr("Alquilando…") : Tr("Comprando…"), false);

            // Confort/capacidad se calculan UNA VEZ para TODA la composición elegida (no por .eng) y
            // se aplican por igual a cada máquina de tracción nueva que se dé de alta desde este tren.
            string leadName = null;
            var lfp = c.Locomotive?.FilePath;
            if (!string.IsNullOrEmpty(lfp)) leadName = System.IO.Path.GetFileNameWithoutExtension(lfp);
            string leadPathForSpeed = (leadName != null ? EngPathByName(leadName) : null) ?? EngPathByName(units[0]);
            double leadKmh = leadPathForSpeed != null ? ReadEngineSpecs(leadPathForSpeed).kmh : 0;
            var analysis = AnalyzeComposition(c, leadKmh);
            // La máquina de CABEZA se tasa con el tren entero (vehículos, masa y freno sumados),
            // igual que al comprar por máquina eligiendo composición. Las demás máquinas de tracción
            // del tren (doble tracción, refuerzos) se tasan por sí solas: son compras aparte.
            var tot = ConsistTotals(c);
            string cabeza = LeadEngineName(c);

            int done = 0;
            foreach (var name in pending)
            {
                // La ruta sale del PROPIO consist: así se compra la librea que lleva este tren y no
                // otra carpeta cualquiera con un .eng del mismo nombre.
                string folder = FolderOfConsistEng(c, name);
                string path = ResolveCarFile(name, folder) ?? EngPathByName(name);
                if (path == null) continue;   // no debería pasar: el nombre viene de AvailableEngines()
                if (string.IsNullOrEmpty(folder)) folder = FolderOfPath(path);
                var (kw, kmh, type) = ReadEngineSpecs(path);
                var (automotor, cars) = ShapeForService(DetectUnitShape(path), analysis.Freight, path);   // mercancías → locomotora
                var (mass, brake) = AnalyzeUnitPhysics(path, automotor);
                if (string.Equals(name, cabeza, StringComparison.OrdinalIgnoreCase) && tot.cars > 0)
                { cars = tot.cars; mass = tot.mass; brake = tot.brake; }   // la cabeza paga el tren completo
                string err = await RpcAcquire(rent, _empSel.Id, name, folder, type, automotor, kw, kmh, cars,
                                              mass, brake, analysis.Capacity, analysis.Comfort);
                if (err != null)
                {
                    Msg(_buyMsg, string.Format(Tr("Se {0} {1} de {2} unidades. Error en «{3}»: {4}"),
                        rent ? Tr("alquilaron") : Tr("compraron"), done, pending.Count, name, err), true);
                    LoadCompanies(); LoadFleet();
                    return;
                }
                done++;
            }
            Msg(_buyMsg, (rent ? Tr("Vehículos alquilados.") : Tr("Vehículos comprados.")) + "  " + Tr("Puedes asignarles matrícula en Flota."), false);
            LoadCompanies();   // refresca el saldo de la empresa
            LoadFleet();
        }

        // Llevar al taller la unidad seleccionada (paga el mantenimiento y resetea el contador de km).
        async void ServiceVehicleUi()
        {
            if (!CanManage() && !Supa.IsSuperadmin) { Msg(_fleetMsg, Tr("Solo el dueño o un gestor pueden gestionar la flota."), true); return; }
            int i = _fleetList?.SelectedRow ?? -1;
            if (i < 0 || i >= _fleetIds.Count) { Msg(_fleetMsg, Tr("Selecciona un vehículo de la lista."), true); return; }
            Msg(_fleetMsg, Tr("Llevando al taller…"), false);
            var (_, err) = await Supa.RpcAsync("service_vehicle", new { p_vehicle = _fleetIds[i] });
            if (err != null) { Msg(_fleetMsg, Tr("Error: ") + err, true); return; }
            Msg(_fleetMsg, Tr("Mantenimiento realizado."), false);
            LoadCompanies();
            LoadFleet();
        }

        // Asignar / cambiar la matrícula de la unidad seleccionada (SOLO gerente, gestor o superadmin;
        // el servidor lo vuelve a comprobar). Única dentro de la empresa. Vacía = quitar matrícula.
        async void AssignPlateUi()
        {
            if (!CanManage() && !Supa.IsSuperadmin) { Msg(_fleetMsg, Tr("Solo el gerente, un gestor o el superadministrador pueden asignar matrículas."), true); return; }
            int i = _fleetList?.SelectedRow ?? -1;
            if (i < 0 || i >= _fleetIds.Count) { Msg(_fleetMsg, Tr("Selecciona un vehículo de la lista."), true); return; }
            string current = i < _fleetPlates.Count ? _fleetPlates[i] : "";
            string plate;
            using (var dlg = new TextPromptDialog(Tr("Asignar matrícula"), Tr("Matrícula de la unidad (p. ej. 446-140). Vacío = sin matrícula."), current, "446-140"))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                plate = (dlg.Value ?? "").Trim().ToUpperInvariant();
            }
            if (plate.Length > 20) { Msg(_fleetMsg, Tr("La matrícula no puede tener más de 20 caracteres."), true); return; }
            if (plate == current) return;
            Msg(_fleetMsg, Tr("Guardando matrícula…"), false);
            var (_, err) = await Supa.RpcAsync("set_vehicle_plate", new { p_vehicle = _fleetIds[i], p_plate = plate });
            if (err != null)
            {
                string m = err.IndexOf("set_vehicle_plate", StringComparison.OrdinalIgnoreCase) >= 0 || err.IndexOf("plate", StringComparison.OrdinalIgnoreCase) >= 0 && err.IndexOf("column", StringComparison.OrdinalIgnoreCase) >= 0
                    ? err + "  " + Tr("(el servidor aún no está actualizado)") : err;
                Msg(_fleetMsg, Tr("Error: ") + m, true); return;
            }
            Msg(_fleetMsg, plate.Length > 0 ? string.Format(Tr("Matrícula {0} asignada."), plate) : Tr("Matrícula quitada."), false);
            LoadFleet();
        }

        // Dar de baja / vender la unidad (si es propia, reembolsa parte del valor).
        async void RemoveVehicle()
        {
            if (!CanManage() && !Supa.IsSuperadmin) { Msg(_fleetMsg, Tr("Solo el dueño o un gestor pueden gestionar la flota."), true); return; }
            int i = _fleetList?.SelectedRow ?? -1;
            if (i < 0 || i >= _fleetIds.Count) { Msg(_fleetMsg, Tr("Selecciona un vehículo de la lista."), true); return; }
            if (MessageBox.Show(this, Tr("¿Dar de baja esta unidad? Si es propia se reembolsa parte de su valor; si es alquilada, se devuelve."),
                    "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Msg(_fleetMsg, Tr("Dando de baja…"), false);
            var (_, err) = await Supa.RpcAsync("retire_vehicle", new { p_vehicle = _fleetIds[i] });
            if (err != null) { Msg(_fleetMsg, Tr("Error: ") + err, true); return; }
            Msg(_fleetMsg, Tr("Unidad dada de baja."), false);
            LoadCompanies();
            LoadFleet();
        }

        static string FmtMinSec(int s) => s >= 60 ? $"{s / 60} min {s % 60:00} s" : $"{s} s";

        static List<KeyValuePair<string, double>> TopN(Dictionary<string, double> d, int n)
        {
            var list = new List<KeyValuePair<string, double>>(d);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (list.Count > n) list = list.GetRange(0, n);
            return list;
        }

        // Registra el servicio automáticamente (los km los mide OR, no se editan) y muestra
        // una ventana de RESULTADO de solo lectura con todos los datos del viaje y el rango.
        const double MinServiceKm = 3.0;   // por debajo, el viaje no se registra
        const int MinServiceSeconds = 300; // y tampoco si dura menos de 5 minutos

        // Duración con la que se decide si el viaje es corto: la de conducción (desde que el
        // escenario está abierto). Si el cronómetro no llegó a arrancar (sin servidor web de OR),
        // el tiempo desde que se abrió el servicio: así nunca se descarta un viaje por no medirlo.
        int ServiceSecondsForRule()
        {
            if (_svcClockUtc != null) return _tripDurationS;
            if (_svcOpenedUtc != null) return (int)Math.Max(0, (DateTime.UtcNow - _svcOpenedUtc.Value).TotalSeconds);
            return int.MaxValue;
        }

        async void EmpCloseService() => await EmpCloseServiceCore(true);

        // showDialog=false → registro desde la barra superior con OR aún abierto: sin ventanas
        // modales (quedarían detrás del simulador). Devuelve (ok, resumen corto para la barra).
        async Task<(bool ok, string summary)> EmpCloseServiceCore(bool showDialog)
        {
            if (_pendingServiceId == null) return (false, null);
            string companyName = _empOnDutyCompany?.Name ?? _empSel?.Name ?? "";
            string route = _curRoute?.Name ?? "";
            var svc = _pendingServiceId;

            // Viajes de menos de 3 km o de menos de 5 minutos: no se registran (se descarta el
            // servicio y se libera la unidad). Al maquinista se le explica por qué.
            int durRule = ServiceSecondsForRule();
            bool cortoKm = _estimatedKm < MinServiceKm, cortoTiempo = durRule < MinServiceSeconds;
            if (cortoKm || cortoTiempo)
            {
                Msg(_empHomeMsg, Tr("Descartando viaje corto…"), false);
                var (_, derr) = await Supa.RpcAsync("discard_short_service",
                    new { p_service = svc, p_km = _estimatedKm, p_duration_s = durRule });
                // Servidor sin actualizar: solo conoce la regla de los km.
                bool sinFuncion = derr != null && (derr.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0
                                                   || derr.IndexOf("p_duration_s", StringComparison.OrdinalIgnoreCase) >= 0);
                if (sinFuncion && cortoKm)
                    (_, derr) = await Supa.RpcAsync("discard_short_service", new { p_service = svc, p_km = _estimatedKm });
                if (derr != null && !(sinFuncion && !cortoKm))
                { Msg(_empHomeMsg, Tr("No se pudo descartar el viaje corto: ") + derr, true); return (false, Tr("No se pudo descartar el viaje corto: ") + derr); }
                if (derr == null)
                {
                    _pendingServiceId = null; _svcOpenedUtc = null;
                    UpdateDutyUi();
                    var motivos = new List<string>();
                    if (cortoKm) motivos.Add(string.Format(Tr("Recorrido de {0} km: los viajes de menos de 3 km no se registran."), _estimatedKm.ToString("0.0", EsEs)));
                    if (cortoTiempo) motivos.Add(string.Format(Tr("Duración de {0}: los viajes de menos de 5 minutos no se registran."), FmtMinSec(durRule)));
                    var nr = new ServiceResultDialog.Data
                    {
                        Company = companyName, Route = route, Valid = false, Km = _estimatedKm,
                        DurationS = durRule == int.MaxValue ? _tripDurationS : durRule, Reasons = motivos
                    };
                    _estimatedKm = 0;
                    return ShowNotRegistered(nr, showDialog);
                }
                // (sin el SQL nuevo, un viaje de más de 3 km pero corto de tiempo se registra como antes)
            }

            Msg(_empHomeMsg, Tr("Registrando servicio…"), false);
            var (jr, err) = await Supa.RpcAsync("close_service", new
            {
                p_service = svc,
                p_km = _estimatedKm,
                p_completed = true,
                p_notes = "",
                p_duration_s = _tripDurationS,
                p_pax = _paxBoarded
            });
            // Si falla, dejamos el servicio pendiente para poder reintentar con "Registrar servicio".
            if (err != null) { Msg(_empHomeMsg, Tr("No se pudo registrar el servicio: ") + err, true); return (false, Tr("No se pudo registrar el servicio: ") + err); }
            _pendingServiceId = null; _svcOpenedUtc = null;
            UpdateDutyUi();

            // Datos del viaje devueltos por el servidor (economía autoritativa) → ventana de resultado.
            var r = new ServiceResultDialog.Data { Company = companyName, Route = route };
            try
            {
                using var d = JsonDocument.Parse(jr);
                var root = d.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
                r.Valid = !(root.TryGetProperty("validated", out var vv) && vv.ValueKind == JsonValueKind.False);
                r.Km = Num(root, "km");
                r.DurationS = (int)Num(root, "duration_s");
                r.Income = Num(root, "income");
                r.Canon = Num(root, "canon");
                r.Energy = Num(root, "energy");
                r.Salary = Num(root, "salary");
                r.Maintenance = Num(root, "maintenance");
                r.Net = Num(root, "net");
                r.Balance = Num(root, "balance");
                r.Pax = (int)Num(root, "pax");
                if (!r.Valid)
                {
                    // El servidor no lo ha registrado (y, con el SQL de la 1.2.35, lo ha borrado): por qué.
                    string reason = Str(root, "reason");
                    double avg = Num(root, "avg_kmh");
                    if (avg <= 0 && r.DurationS > 0) avg = r.Km / (r.DurationS / 3600.0);
                    r.Reasons.Add(reason == "failed"
                        ? Tr("El servicio terminó como fallido.")
                        : string.Format(Tr("Velocidad media imposible ({0} km/h; el máximo son 350 km/h)."), avg.ToString("N0", EsEs)));
                    r.Pax = 0;
                }
                double validAfter = Num(root, "driver_valid_km");
                double validBefore = validAfter - (r.Valid ? r.Km : 0);
                int tBefore = RankTierIndex(validBefore);
                int tAfter = RankTierIndex(validAfter);
                r.ValidKm = validAfter;
                r.RankName = Tr(RankTiers[tAfter].name);
                r.RankedUp = tAfter > tBefore;
                if (tAfter < RankTiers.Length - 1)
                {
                    double from = RankTiers[tAfter].km, to = RankTiers[tAfter + 1].km;
                    r.RankPct = to > from ? (validAfter - from) / (to - from) : 1;
                    r.NextRankName = Tr(RankTiers[tAfter + 1].name);
                    r.NextRankKm = to;
                }
                else { r.RankPct = 1; r.NextRankName = null; }
            }
            catch { }

            _estimatedKm = 0;
            if (!r.Valid) return ShowNotRegistered(r, showDialog);
            Msg(_empHomeMsg, Tr("Servicio registrado."), false);
            // Registro publicado → refresca saldo y la subpestaña visible (sin botón "Actualizar").
            LoadCompanies();
            RefreshActiveSubtab();

            if (showDialog)
            {
                using var dlg = new ServiceResultDialog(r);
                dlg.ShowDialog(this);
            }
            string sum = string.Format(Tr("Servicio registrado · {0} km · neto {1}"), r.Km.ToString("0.0", EsEs), r.Net.ToString("+#,##0.00 €;-#,##0.00 €", EsEs));
            return (true, sum);
        }

        // Viaje que NO se registra (corto, velocidad imposible o fallido): el servicio ya no existe;
        // se avisa al maquinista de por qué (ventana al volver de conducir; con OR abierto, en la barra).
        (bool ok, string summary) ShowNotRegistered(ServiceResultDialog.Data r, bool showDialog)
        {
            string motivos = string.Join(" ", r.Reasons);
            Msg(_empHomeMsg, Tr("Servicio no registrado: ") + motivos, true);
            LoadCompanies();
            RefreshActiveSubtab();
            if (showDialog)
            {
                using var dlg = new ServiceResultDialog(r);
                dlg.ShowDialog(this);
            }
            return (true, Tr("Servicio no registrado: ") + motivos);
        }

        // Recarga los datos de la subpestaña visible (auto-refresh al publicar un registro).
        void RefreshActiveSubtab()
        {
            switch (_empSubtab)
            {
                case 0: if (_empSel != null) LoadServices(_empSel); break;
                case 1: LoadLedger(); break;
                case 2: if (_empSel != null) LoadMembers(_empSel); break;
                case 4: LoadRankings(); break;
                case 5: LoadProfile(); break;
                case 7: LoadUsers(); break;
                case 8: LoadAllCompanies(); break;
                case 9: case 10: LoadFleet(); if (_empSubtab == 10) LoadPurchaseRequests(); break;
            }
        }

        // ---- Refresco en vivo (Supabase Realtime) ----
        // Arranca la escucha de cambios una vez iniciada la sesión. La RLS de cada tabla
        // hace que solo lleguen los cambios que el usuario puede ver.
        void StartRealtime()
        {
            if (_rt != null || !Supa.IsLoggedIn) return;
            _rtDebounce = new Timer { Interval = 500 };
            _rtDebounce.Tick += (s, e) => { _rtDebounce.Stop(); DoRealtimeRefresh(); };
            // Un topic por tabla (en SupaRealtime): si alguna no está publicada aún, solo falla su topic.
            _rt = new SupaRealtime(
                Supa.Url, Supa.AnonKey, () => Supa.AccessToken,
                new[] { "services", "ledger", "companies", "company_members", "join_requests" },
                OnRealtimeChange);
            _rt.Start();
        }

        void StopRealtime()
        {
            try { _rt?.Stop(); } catch { }
            _rt = null;
            try { _rtDebounce?.Dispose(); } catch { }
            _rtDebounce = null;
        }

        // Llega desde un hilo de fondo: marshaliza al hilo de UI y agrupa (debounce).
        void OnRealtimeChange(string table)
        {
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke((Action)(() => { if (_rtDebounce != null) { _rtDebounce.Stop(); _rtDebounce.Start(); } }));
            }
            catch { }
        }

        void DoRealtimeRefresh()
        {
            if (!Supa.IsLoggedIn || _activePage != PageEmpresas) return;   // solo refresca si estás mirando Empresas
            LoadCompanies();                 // saldo/tesorería y lista de empresas
            RefreshActiveSubtab();           // la subpestaña visible
        }

        // ============================ Socios y roles ============================
        async void LoadMembers(EmpCompany c)
        {
            if (c == null) return;
            var (json, err) = await Supa.RpcAsync("list_members", new { p_company = c.Id });
            _members.Clear();
            if (err == null)
            {
                try
                {
                    using var d = JsonDocument.Parse(json);
                    foreach (var e in d.RootElement.EnumerateArray())
                        _members.Add(new EmpMember { UserId = Str(e, "user_id"), Username = Str(e, "username"), Role = Str(e, "role") });
                }
                catch { }
            }
            _myRole = _members.Find(m => m.UserId == Supa.UserId)?.Role;
            if (_memberList != null)
            {
                _memberList.BeginReload(c.Id);
                _memberList.ClearRows();
                foreach (var m in _members)
                    _memberList.AddRow(new[] { m.Username.Length > 0 ? m.Username : "—", TrRole(m.Role) },
                        new Color?[] { null, m.Role == "owner" ? Theme.Accent : (Color?)null }, null, m.UserId);
                if (_members.Count == 0) _memberList.SetEmpty(err ?? Tr("Sin socios."));
                _memberList.EndReload();
            }
            UpdateRoleUi();
            UpdateCompanyDash();
            LoadJoinRequests(c);
            LoadPurchaseRequests();   // contador «Compra (n)» para gerente y gestores
        }

        // Carga las solicitudes de ingreso pendientes (solo dueño/gestor las obtiene del servidor).
        async void LoadJoinRequests(EmpCompany c)
        {
            if (_joinList == null || c == null) return;
            if (!CanManage() && !Supa.IsSuperadmin) { _joinList.ClearRows(); _joinIds.Clear(); _joinList.SetEmpty(Tr("—")); return; }
            _joinList.BeginReload(c.Id);
            var (json, err) = await Supa.RpcAsync("list_join_requests", new { p_company = c.Id });
            _joinList.ClearRows(); _joinIds.Clear();
            if (err != null) { _joinList.SetEmpty(Tr("Error: ") + err); return; }
            int n = 0;
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    n++;
                    _joinIds.Add(Str(e, "id"));
                    string who = Str(e, "username"); if (who.Length == 0) who = "—";
                    string note = Str(e, "note");
                    _joinList.AddRow(new[] { who, note, FmtDate(Str(e, "created_at")) }, null, null, _joinIds[_joinIds.Count - 1]);
                }
            }
            catch { }
            if (n == 0) _joinList.SetEmpty(Tr("No hay solicitudes de ingreso."));
            _joinList.EndReload();
        }

        async void ApproveJoin()
        {
            if (_joinList == null || _empSel == null || !(CanManage() || Supa.IsSuperadmin)) return;
            int i = _joinList.SelectedRow;
            if (i < 0 || i >= _joinIds.Count) { Msg(_memberMsg, Tr("Selecciona una solicitud de la lista."), true); return; }
            Msg(_memberMsg, Tr("Aceptando…"), false);
            var (_, err) = await Supa.RpcAsync("approve_join_request", new { p_request = _joinIds[i] });
            if (err != null) { Msg(_memberMsg, Tr("Error: ") + err, true); return; }
            Msg(_memberMsg, Tr("Solicitud aceptada. Nuevo maquinista añadido."), false);
            LoadMembers(_empSel);
        }

        async void RejectJoin()
        {
            if (_joinList == null || _empSel == null || !(CanManage() || Supa.IsSuperadmin)) return;
            int i = _joinList.SelectedRow;
            if (i < 0 || i >= _joinIds.Count) { Msg(_memberMsg, Tr("Selecciona una solicitud de la lista."), true); return; }
            Msg(_memberMsg, Tr("Rechazando…"), false);
            var (_, err) = await Supa.RpcAsync("reject_join_request", new { p_request = _joinIds[i] });
            if (err != null) { Msg(_memberMsg, Tr("Error: ") + err, true); return; }
            Msg(_memberMsg, Tr("Solicitud rechazada."), false);
            LoadJoinRequests(_empSel);
        }

        Label _empFavLink; ToolTip _empFavTip;
        string _favCompanyId;      // empresa favorita (profiles.favorite_company)
        bool _favLoaded;

        // La favorita vive en TU PERFIL, así que te sigue a cualquier equipo. En las preferencias
        // locales se deja una copia para que la primera pantalla no dependa de la red.
        async void ToggleFavoriteCompany()
        {
            if (_empSel == null) return;
            bool era = string.Equals(_favCompanyId, _empSel.Id, StringComparison.OrdinalIgnoreCase);
            string nuevo = era ? null : _empSel.Id;
            _favCompanyId = nuevo;
            UpdateFavoriteUi();
            if (_prefs != null) { _prefs.FavoriteCompany = nuevo; try { _prefs.Save(); } catch { } }
            string err = await SaveFavoriteCompanyAsync(nuevo);
            if (err != null)
            {
                Msg(_empHomeMsg, Tr("No se pudo guardar la empresa favorita en tu perfil: ") + err
                    + "  " + Tr("(el servidor aún no está actualizado)"), true);
                return;
            }
            Msg(_empHomeMsg, era ? Tr("Ya no es tu empresa favorita.")
                                 : string.Format(Tr("«{0}» es tu empresa favorita: saldrá elegida al abrir Empresas."), _empSel.Name), false);
        }

        async Task<string> SaveFavoriteCompanyAsync(string companyId)
        {
            if (!Supa.IsLoggedIn || string.IsNullOrEmpty(Supa.UserId)) return null;
            return await Supa.UpdateAsync($"profiles?id=eq.{Uri.EscapeDataString(Supa.UserId)}",
                                          new { favorite_company = companyId });
        }

        // Lee la favorita del perfil. Si el perfil no tiene ninguna pero este equipo sí (versión
        // anterior, que la guardaba solo en local), la sube una vez y ya queda en la cuenta.
        async Task LoadFavoriteCompanyAsync()
        {
            _favLoaded = true;
            _favCompanyId = _prefs?.FavoriteCompany;
            if (!Supa.IsLoggedIn || string.IsNullOrEmpty(Supa.UserId)) return;
            var (json, err) = await Supa.SelectAsync($"profiles?select=favorite_company&id=eq.{Uri.EscapeDataString(Supa.UserId)}");
            if (err != null) return;   // esquema sin la columna: se sigue usando la copia local
            string enPerfil = null;
            try
            {
                using var d = JsonDocument.Parse(json);
                if (d.RootElement.ValueKind == JsonValueKind.Array && d.RootElement.GetArrayLength() > 0)
                {
                    var e = d.RootElement[0];
                    if (e.TryGetProperty("favorite_company", out var v) && v.ValueKind == JsonValueKind.String)
                        enPerfil = v.GetString();
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(enPerfil)) { _favCompanyId = enPerfil; }
            else if (!string.IsNullOrEmpty(_favCompanyId)) await SaveFavoriteCompanyAsync(_favCompanyId);
            if (_prefs != null && !string.Equals(_prefs.FavoriteCompany, _favCompanyId, StringComparison.OrdinalIgnoreCase))
            { _prefs.FavoriteCompany = _favCompanyId; try { _prefs.Save(); } catch { } }
        }

        void UpdateFavoriteUi()
        {
            if (_empFavLink == null) return;
            bool varias = _empCompanies.Count > 1;
            _empFavLink.Visible = _empSel != null && varias;   // con una sola empresa no pinta nada
            bool fav = _empSel != null && string.Equals(_favCompanyId, _empSel.Id, StringComparison.OrdinalIgnoreCase);
            _empFavLink.Text = fav ? "★" : "☆";
            _empFavLink.ForeColor = fav ? Color.FromArgb(232, 196, 84) : Theme.Subtle;
            _empFavTip?.SetToolTip(_empFavLink, fav
                ? Tr("Quitar de favorita")
                : Tr("Marcar como favorita: será la que salga elegida al abrir Empresas"));
        }

        bool CanManage() => _myRole == "owner" || _myRole == "manager";
        bool IsOwner() => _myRole == "owner";
        // Borrar empresa: el superadmin cualquiera; el gerente, la suya.
        bool CanDeleteCompany() => Supa.IsSuperadmin || (_empSel != null && IsOwner());

        void UpdateRoleUi()
        {
            // Lo que requiere permiso se OCULTA (no se desactiva) a quien no lo tiene: el maquinista
            // raso no ve botones de gestión que no puede usar.
            bool manage = CanManage() || Supa.IsSuperadmin;
            bool ownerOrSu = IsOwner() || Supa.IsSuperadmin;
            if (_createAreaSuper != null && _createAreaSuper != CanDeleteCompany()) BuildCreateArea();   // [Eliminar] según el rol
            if (_memberAddBtn != null) _memberAddBtn.Visible = manage;
            if (_memberDelBtn != null) _memberDelBtn.Visible = manage;
            if (_memberRoleBtn != null) _memberRoleBtn.Visible = ownerOrSu;              // cambiar roles: solo gerente/superadmin
            if (_memberRole != null) _memberRole.Visible = ownerOrSu;                    // (su selector de rol va con él)
            // «Añadir socio»: el gestor no puede nombrar gerentes → esa opción solo la ven gerente/superadmin.
            if (_memberAddRole != null)
            {
                int want = ownerOrSu ? 3 : 2;   // Maquinista · Gestor · (Gerente)
                if (_memberAddRole.Items.Count != want)
                {
                    int sel = Math.Min(_memberAddRole.SelectedIndex < 0 ? 0 : _memberAddRole.SelectedIndex, want - 1);
                    _memberAddRole.Items.Clear();
                    _memberAddRole.Items.AddRange(ownerOrSu
                        ? new object[] { Tr("Maquinista"), Tr("Gestor"), Tr("Gerente") }
                        : new object[] { Tr("Maquinista"), Tr("Gestor") });
                    _memberAddRole.SelectedIndex = sel;
                }
            }
            if (_tarSaveBtn != null) _tarSaveBtn.Visible = Supa.IsSuperadmin;          // tarifas: solo superadmin
            bool tarRo = !Supa.IsSuperadmin;
            foreach (var ip in new[] { _tarIncome, _tarCanon, _tarEnergy, _tarSalary })
                if (ip != null) ip.Box.ReadOnly = tarRo;
            if (_tarBalanceRow != null) _tarBalanceRow.Visible = Supa.IsSuperadmin;   // saldo: solo superadmin
            if (_defBalanceRow != null) _defBalanceRow.Visible = Supa.IsSuperadmin;   // saldo inicial global: solo superadmin
            if (_fleetSettingsRow != null) _fleetSettingsRow.Visible = Supa.IsSuperadmin; // economía de flota: solo superadmin
            if (_svcDelBtn != null) _svcDelBtn.Visible = Supa.IsSuperadmin;           // borrar servicio: solo superadmin
            if (_ledgerDelBtn != null) _ledgerDelBtn.Visible = Supa.IsSuperadmin;     // borrar movimiento: solo superadmin
            bool fleetManage = CanManage() || Supa.IsSuperadmin;                      // flota: dueño/gestor/superadmin
            if (_fleetBuyBtn != null) _fleetBuyBtn.Visible = fleetManage;
            if (_fleetRentBtn != null) _fleetRentBtn.Visible = fleetManage;
            if (_fleetRemoveBtn != null) _fleetRemoveBtn.Visible = fleetManage;         // Flota: dar de baja
            if (_fleetServiceBtn != null) _fleetServiceBtn.Visible = fleetManage;       // Flota: llevar al taller
            if (_fleetPlateBtn != null) _fleetPlateBtn.Visible = fleetManage;           // Flota: asignar matrícula
            if (_empLogoPic != null) _empLogoPic.Cursor = manage ? Cursors.Hand : Cursors.Default;   // cambiar logotipo: solo gestión
            if (_empLogoTip != null && _empLogoPic != null) _empLogoTip.SetToolTip(_empLogoPic, manage ? Tr("Cambiar logotipo") : "");
            UpdateTrainShortcuts();   // «Comprar este tren» en Exploración/Horarios según el rol
            UpdateSubtabVisibility();
        }

        // Oculta las subpestañas de gestión que el usuario no puede modificar según su permiso:
        //  · Socios (idx 2): solo dueño/gestor.   · Ajustes (idx 3): solo superadmin.
        // El resto (Servicios, Banca, Ranking, Mi perfil) es visible para TODOS los socios
        // (informativas): la Banca la ven maquinistas, gestores y gerentes por igual.
        void UpdateSubtabVisibility()
        {
            if (_empSubtabs == null) return;
            bool su = Supa.IsSuperadmin;
            // El superadmin ve TODAS las empresas en la lista, así que hasCompany basta (evita
            // depender de _empSel, que se fija DESPUÉS en LoadCompanies → cabecera/subpestañas mal).
            bool hasCompany = _empCompanies.Count > 0;
            // Con empresa → por permiso (el superadmin ve/gestiona TODO). Sin empresa → solo Ranking
            // y Mi perfil (y, para el superadmin, Usuarios y Todas las empresas).
            //   índices: 0 Servicios · 1 Banca · 2 Socios · 3 Ajustes · 4 Ranking · 5 Mi perfil
            //            · 6 (Revisión, ya no existe) · 7 Usuarios · 8 Todas las empresas · 9 Flota · 10 Compra · 11 Megafonía
            // Megafonía: la habilita el superadmin empresa por empresa (companies.pa_enabled). Quien
            // gestiona solo la ve si está habilitada; el superadmin la ve siempre (para habilitarla).
            bool pa = su || (PaEnabledHere() && CanManage());
            bool[] show = hasCompany
                ? new[] { true, true, CanManage() || su, su, true, true, false, su, su, true, CanManage() || su, pa }   // Compra: solo gestión
                : new[] { false, false, false, false, true, true, false, su, su, false, false, false };
            for (int k = 0; k < _empSubtabs.Length && k < show.Length; k++)
                if (_empSubtabs[k] != null) _empSubtabs[k].Visible = show[k];
            UpdateNavGroupHeaders();   // oculta el encabezado de un grupo si ninguna de sus secciones se ve
            AjustarNav();              // con menos secciones visibles cabe más holgado
            // Si la subpestaña activa se acaba de ocultar, cae a una visible.
            // (Solo si el rol ya se conoce: durante un refresco _myRole puede estar a null
            //  un instante y no debe rebotarte fuera de Socios/Ajustes.)
            bool roleKnown = _myRole != null || su || !hasCompany;
            if (roleKnown && _empSubtab < show.Length && !show[_empSubtab])
            {
                ShowSubtab(hasCompany ? 0 : 4);
                _subtabAutoFallback = !hasCompany;   // cayó a Ranking solo porque aún no había empresas
            }
            else if (hasCompany && _subtabAutoFallback)
            {
                _subtabAutoFallback = false;          // ya cargaron las empresas → abrir en Servicios
                ShowSubtab(0);
            }
        }

        // Oculta el encabezado de cada grupo del menú lateral si ninguna de sus secciones está visible.
        void UpdateNavGroupHeaders()
        {
            if (_navGroups == null) return;
            foreach (var (header, items) in _navGroups)
            {
                bool any = false;
                foreach (var idx in items)
                    if (idx >= 0 && idx < _empSubtabs.Length && _empSubtabs[idx] != null && _empSubtabs[idx].Visible) { any = true; break; }
                if (header != null) header.Visible = any;
            }
        }

        // Vista "sin empresa": oculta solo el SELECTOR (no hay nada que elegir). La cabecera se
        // mantiene visible porque ahora aloja las acciones Crear/Unirse (imprescindibles sin empresa).
        void UpdateEmptyStateUi()
        {
            bool has = _empCompanies.Count > 0;
            if (_empCoCombo != null) _empCoCombo.Visible = has;
        }

        async void ChangeMemberRole()
        {
            if (_empSel == null) return;
            if (!IsOwner() && !Supa.IsSuperadmin) { Msg(_memberMsg, Tr("Solo el gerente puede cambiar roles."), true); return; }
            int i = _memberList.SelectedRow;
            if (i < 0 || i >= _members.Count) { Msg(_memberMsg, Tr("Selecciona un socio de la lista."), true); return; }
            var m = _members[i];
            if (m.UserId == Supa.UserId) { Msg(_memberMsg, Tr("No puedes cambiar tu propio rol."), true); return; }
            string role = RoleFromIndex(_memberRole.SelectedIndex);
            Msg(_memberMsg, Tr("Cambiando rol…"), false);
            var (_, err) = await Supa.RpcAsync("set_member_role", new { p_company = _empSel.Id, p_user = m.UserId, p_role = role });
            if (err != null) { Msg(_memberMsg, Tr("Error: ") + err, true); return; }
            Msg(_memberMsg, string.Format(Tr("Rol de «{0}» cambiado a {1}."), m.Username, TrRole(role)), false);
            LoadMembers(_empSel);
        }

        // ============================ Ajustes: tarifas y saldo ============================
        void FillTariffFields()
        {
            if (_tarIncome == null) return;
            // Las tarifas son GLOBALES (iguales para todas las empresas) → se leen de app_settings.
            LoadDefaultTariffs();
            // El saldo "Fijar saldo" sí es de ESTA empresa.
            if (_empSel != null && _tarBalance != null)
                _tarBalance.Box.Text = _empSel.Balance.ToString("0.##", CultureInfo.InvariantCulture);
            UpdateRoleUi();
        }

        // Carga las tarifas GLOBALES (para mostrarlas en Ajustes).
        async void LoadDefaultTariffs()
        {
            if (_tarIncome == null || !Supa.IsSuperadmin) return;
            var (json, err) = await Supa.RpcAsync("get_default_tariffs", new { });
            if (err != null) return;
            try
            {
                using var d = JsonDocument.Parse(json);
                var root = d.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
                // Cultura española (coma decimal) para que el round-trip con ParseNum sea correcto.
                _tarIncome.Box.Text = Num(root, "income_per_km").ToString("0.####", EsEs);
                _tarCanon.Box.Text = Num(root, "canon_per_km").ToString("0.####", EsEs);
                _tarEnergy.Box.Text = Num(root, "energy_per_km").ToString("0.####", EsEs);
                _tarSalary.Box.Text = Num(root, "salary_per_service").ToString("0.##", EsEs);
            }
            catch { }
        }

        async void SaveTariffs()
        {
            if (!Supa.IsSuperadmin) { Msg(_tariffMsg, Tr("Solo el superadministrador puede modificar las tarifas."), true); return; }
            Msg(_tariffMsg, Tr("Guardando tarifas…"), false);
            var (_, err) = await Supa.RpcAsync("set_default_tariffs", new
            {
                p_income = ParseNum(_tarIncome.Box.Text),
                p_canon = ParseNum(_tarCanon.Box.Text),
                p_energy = ParseNum(_tarEnergy.Box.Text),
                p_salary = ParseNum(_tarSalary.Box.Text)
            });
            if (err != null) { Msg(_tariffMsg, Tr("Error: ") + err, true); return; }
            Msg(_tariffMsg, Tr("Tarifas globales guardadas."), false);
        }

        async void AdminSetBalance()
        {
            if (_empSel == null) return;
            if (!Supa.IsSuperadmin) { Msg(_tariffMsg, Tr("Solo el superadministrador puede fijar el saldo."), true); return; }
            double bal = ParseNum(_tarBalance.Box.Text);
            Msg(_tariffMsg, Tr("Fijando saldo…"), false);
            var (_, err) = await Supa.RpcAsync("admin_set_balance", new { p_company = _empSel.Id, p_balance = bal });
            if (err != null) { Msg(_tariffMsg, Tr("Error: ") + err, true); return; }
            Msg(_tariffMsg, Tr("Saldo actualizado."), false);
            LoadCompanies();
        }

        // Carga el saldo inicial GLOBAL de empresas nuevas (para mostrarlo en Ajustes).
        async void LoadDefaultBalance()
        {
            if (_defBalance == null || !Supa.IsSuperadmin) return;
            var (json, err) = await Supa.RpcAsync("get_default_initial_balance", new { });
            if (err != null) return;
            double v = 0;
            try { double.TryParse((json ?? "").Trim().Trim('"'), NumberStyles.Any, CultureInfo.InvariantCulture, out v); } catch { }
            _defBalance.Box.Text = v.ToString("0.##", EsEs);
        }

        // Guarda el saldo inicial GLOBAL de empresas nuevas (SOLO superadmin).
        async void SaveDefaultBalance()
        {
            if (!Supa.IsSuperadmin) { Msg(_tariffMsg, Tr("Solo el superadministrador puede cambiar el saldo inicial."), true); return; }
            double bal = ParseNum(_defBalance.Box.Text);
            Msg(_tariffMsg, Tr("Guardando saldo inicial…"), false);
            var (_, err) = await Supa.RpcAsync("set_default_initial_balance", new { p_balance = bal });
            if (err != null) { Msg(_tariffMsg, Tr("Error: ") + err, true); return; }
            Msg(_tariffMsg, Tr("Saldo inicial guardado."), false);
        }

        // Carga los ajustes de la economía de flota (para mostrarlos en Ajustes).
        async void LoadFleetSettings()
        {
            if (_fsScale == null || !Supa.IsSuperadmin) return;
            var (json, err) = await Supa.RpcAsync("get_fleet_settings", new { });
            if (err != null) return;
            try
            {
                using var d = JsonDocument.Parse(json);
                var root = d.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
                // Se formatea en cultura española (coma decimal) para casar con ParseNum ('.'=miles).
                _fsScale.Box.Text = Num(root, "price_scale").ToString("0.####", EsEs);
                _fsRentPct.Box.Text = Num(root, "rental_pct").ToString("0.########", EsEs);
                _fsMaintPct.Box.Text = Num(root, "maint_pct").ToString("0.########", EsEs);
                if (_fsCapBase != null) _fsCapBase.Box.Text = Num(root, "capacity_base").ToString("0.##", EsEs);
                if (_fsFareBase != null) _fsFareBase.Box.Text = Num(root, "fare_base").ToString("0.##", EsEs);
                if (_fsPaxDemand != null) _fsPaxDemand.Box.Text = Num(root, "pax_demand").ToString("0.##", EsEs);
            }
            catch { }
        }

        // Guarda la economía de flota (escala de precio + % alquiler + % mantenimiento; SOLO superadmin).
        async void SaveFleetSettings()
        {
            if (!Supa.IsSuperadmin) { Msg(_tariffMsg, Tr("Solo el superadministrador puede modificar la economía de flota."), true); return; }
            Msg(_tariffMsg, Tr("Guardando economía de flota…"), false);
            var (_, err) = await Supa.RpcAsync("set_fleet_settings", new
            {
                p_scale = ParseNum(_fsScale.Box.Text),
                p_rental_pct = ParseNum(_fsRentPct.Box.Text),
                p_maint_pct = ParseNum(_fsMaintPct.Box.Text),
                p_capacity_base = ParseNum(_fsCapBase.Box.Text),
                p_fare_base = ParseNum(_fsFareBase.Box.Text),
                p_pax_demand = ParseNum(_fsPaxDemand.Box.Text)
            });
            if (err != null) { Msg(_tariffMsg, Tr("Error: ") + err, true); return; }
            Msg(_tariffMsg, Tr("Economía de flota guardada."), false);
            _fleetScale = ParseNum(_fsScale.Box.Text); _fleetRentPct = ParseNum(_fsRentPct.Box.Text);
            _paxDemandBase = ParseNum(_fsPaxDemand.Box.Text);
        }

        // ---------------- Ajustes → Actualizaciones ----------------
        Label _updInfo; TextBox _updNotes; RoundButton _updPublishBtn;

        async void RefreshUpdateInfo()
        {
            if (_updInfo == null) return;
            string cur = Updater.CurrentVersionText;
            _updInfo.Text = string.Format(Tr("Versión en ejecución: {0}  ·  Última publicada: {1}"), cur, "…");
            _updPublishBtn.Text = string.Format(Tr("Publicar versión {0}"), cur);
            var r = await Updater.GetLatestAsync();
            _updInfo.Text = string.Format(Tr("Versión en ejecución: {0}  ·  Última publicada: {1}"), cur, r?.Version ?? Tr("ninguna"));
        }

        async void PublishUpdate()
        {
            if (!Supa.IsSuperadmin) { Msg(_tariffMsg, Tr("Solo el superadministrador puede publicar actualizaciones."), true); return; }
            string cur = Updater.CurrentVersionText;
            var latest = await Updater.GetLatestAsync();
            if (latest != null && Updater.TryParse(latest.Version, out var lv) && lv >= Updater.CurrentVersion)
            {
                Msg(_tariffMsg, string.Format(Tr("La versión {0} no es más nueva que la publicada ({1}). Sube la versión en SelectOR.csproj y compila."), cur, latest.Version), true);
                return;
            }
            if (MessageBox.Show(this, string.Format(Tr("¿Publicar SelectOR {0}? Todos los usuarios recibirán el aviso de actualización."), cur),
                    "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _updPublishBtn.Enabled = false;
            try
            {
                var st = new Progress<string>(t => Msg(_tariffMsg, t, false));
                string err = await Updater.PublishAsync(_updNotes.Text.Trim(), st);
                if (err != null)
                {
                    Msg(_tariffMsg, Tr("Error: ") + err + "  " + Tr("(el servidor aún no está actualizado)"), true);
                    return;
                }
                Msg(_tariffMsg, string.Format(Tr("Versión {0} publicada. Los usuarios verán el aviso al abrir SelectOR."), cur), false);
                RefreshUpdateInfo();
            }
            finally { _updPublishBtn.Enabled = true; }
        }

        // ---------------- Ajustes → Viajeros / Clasificación ----------------
        RoundedInput[] _pmDemand, _pmAlight, _pmStation, _pmMisc, _pmWeather, _pmClass;
        CheckBox _pmUseSeason, _pmUseWeather;

        static Label EmpNote(string t) => new Label { Text = t, AutoSize = true, MaximumSize = new Size(660, 0), ForeColor = Theme.Subtle, Font = Theme.Font(8.5f), Margin = new Padding(2, 2, 2, 6) };

        // Rejilla: fila de cabeceras + una fila por parámetro (etiqueta + un campo por columna).
        static TableLayoutPanel ParamGrid(string[] cols, (string label, RoundedInput[] inputs)[] rows, int labelW = 230, int colW = 112)
        {
            var g = new TableLayoutPanel { AutoSize = true, ColumnCount = cols.Length + 1, RowCount = rows.Length + 1, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 4) };
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelW));
            for (int i = 0; i < cols.Length; i++) g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, colW));
            g.Controls.Add(new Label { AutoSize = true }, 0, 0);
            for (int i = 0; i < cols.Length; i++)
                g.Controls.Add(new Label { Text = I18n.T(cols[i]), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(8.5f), Margin = new Padding(3, 4, 2, 2) }, i + 1, 0);
            for (int r = 0; r < rows.Length; r++)
            {
                g.Controls.Add(new Label { Text = I18n.T(rows[r].label), AutoSize = true, MaximumSize = new Size(labelW - 4, 0), ForeColor = Theme.Text, Font = Theme.Font(9f), Anchor = AnchorStyles.Left, Margin = new Padding(2, 4, 2, 4) }, 0, r + 1);
                for (int i = 0; i < rows[r].inputs.Length; i++)
                {
                    var inp = new RoundedInput("") { Width = colW - 8, Margin = new Padding(2, 2, 4, 2) };
                    rows[r].inputs[i] = inp;
                    g.Controls.Add(inp, i + 1, r + 1);
                }
            }
            return g;
        }

        static CheckBox ParamCheck(string t) => new CheckBox
        {
            Text = t, AutoSize = true, ForeColor = Theme.Text, Font = Theme.Font(9.5f),
            Margin = new Padding(2, 8, 2, 2), Padding = new Padding(0, 2, 0, 0)
        };

        FlowLayoutPanel ParamButtons(string saveText, Action save)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 12, 0, 8) };
            var bSave = EmpButton(saveText, primary: true); bSave.Width = 300; bSave.Click += (s, e) => save();
            var bReset = EmpButton(Tr("Restablecer valores por defecto"), primary: false); bReset.Width = 320;
            bReset.Click += (s, e) =>
            {
                FillPaxModelInputs(new PaxModelConfig());
                Msg(_tariffMsg, Tr("Valores por defecto cargados. Pulsa Guardar para aplicarlos."), false);
            };
            f.Controls.Add(bSave); f.Controls.Add(bReset);
            return f;
        }

        static string F(double v) => v.ToString("0.###", EsEs);

        void FillPaxModelInputs(PaxModelConfig c)
        {
            if (_pmDemand == null) return;
            for (int i = 0; i < 4; i++)
            {
                _pmDemand[i].Box.Text = F(c.ProfileDemand[i]);
                _pmAlight[i].Box.Text = F(c.ProfileAlight[i]);
                _pmStation[i].Box.Text = F(c.StationWeights[i]);
            }
            _pmMisc[0].Box.Text = F(c.BigStationAlight); _pmMisc[1].Box.Text = F(c.Jitter * 100); _pmMisc[2].Box.Text = F(c.ReboardM);
            _pmWeather[0].Box.Text = F(c.SnowFactor); _pmWeather[1].Box.Text = F(c.RainCercanias); _pmWeather[2].Box.Text = F(c.RainMedia);
            _pmClass[0].Box.Text = F(c.AvKmh); _pmClass[1].Box.Text = F(c.LargaKmh); _pmClass[2].Box.Text = F(c.MediaKmh); _pmClass[3].Box.Text = F(c.MediaMaxDensity);
            _pmUseSeason.Checked = c.UseSeason; _pmUseWeather.Checked = c.UseWeather;
        }

        // Lee los campos; devuelve null (y avisa) si algún valor no tiene sentido.
        PaxModelConfig ReadPaxModelInputs()
        {
            var c = new PaxModelConfig();
            double V(RoundedInput i) => ParseNum(i.Box.Text);
            for (int i = 0; i < 4; i++)
            {
                c.ProfileDemand[i] = V(_pmDemand[i]);
                c.ProfileAlight[i] = V(_pmAlight[i]);
                c.StationWeights[i] = V(_pmStation[i]);
            }
            c.BigStationAlight = V(_pmMisc[0]); c.Jitter = V(_pmMisc[1]) / 100.0; c.ReboardM = V(_pmMisc[2]);
            c.SnowFactor = V(_pmWeather[0]); c.RainCercanias = V(_pmWeather[1]); c.RainMedia = V(_pmWeather[2]);
            c.AvKmh = V(_pmClass[0]); c.LargaKmh = V(_pmClass[1]); c.MediaKmh = V(_pmClass[2]); c.MediaMaxDensity = V(_pmClass[3]);
            c.UseSeason = _pmUseSeason.Checked; c.UseWeather = _pmUseWeather.Checked;

            string bad = null;
            foreach (var a in c.ProfileAlight) if (a < 0 || a > 0.9) bad = Tr("La fracción que baja debe estar entre 0 y 0,9.");
            foreach (var a in c.ProfileDemand) if (a < 0) bad = Tr("Los valores no pueden ser negativos.");
            foreach (var a in c.StationWeights) if (a < 0) bad = Tr("Los valores no pueden ser negativos.");
            if (c.Jitter < 0 || c.Jitter > 0.9) bad = Tr("La variación aleatoria debe estar entre 0 y 90 %.");
            if (c.ReboardM < 300) bad = Tr("El reembarque debe ser de al menos 300 m.");
            if (!(c.AvKmh > c.LargaKmh && c.LargaKmh > c.MediaKmh && c.MediaKmh > 0))
                bad = Tr("Los umbrales deben cumplir Alta Velocidad > Larga > Media > 0.");
            if (c.MediaMaxDensity <= 0) bad = Tr("La densidad máxima de Media debe ser mayor que 0.");
            if (bad != null) { Msg(_tariffMsg, bad, true); return null; }
            return c;
        }

        // Guarda el modelo (app_settings.pax_model). Desde «Viajeros» guarda también la demanda base.
        async void SavePaxModel(bool withDemand)
        {
            if (!Supa.IsSuperadmin) { Msg(_tariffMsg, Tr("Solo el superadministrador puede modificar el modelo de viajeros."), true); return; }
            var c = ReadPaxModelInputs();
            if (c == null) return;
            Msg(_tariffMsg, Tr("Guardando…"), false);
            if (withDemand)
            {
                var (_, e1) = await Supa.RpcAsync("set_fleet_settings", new
                {
                    p_scale = ParseNum(_fsScale.Box.Text),
                    p_rental_pct = ParseNum(_fsRentPct.Box.Text),
                    p_maint_pct = ParseNum(_fsMaintPct.Box.Text),
                    p_capacity_base = ParseNum(_fsCapBase.Box.Text),
                    p_fare_base = ParseNum(_fsFareBase.Box.Text),
                    p_pax_demand = ParseNum(_fsPaxDemand.Box.Text)
                });
                if (e1 != null) { Msg(_tariffMsg, Tr("Error: ") + e1, true); return; }
                _paxDemandBase = ParseNum(_fsPaxDemand.Box.Text);
            }
            var model = JsonDocument.Parse(JsonSerializer.Serialize(c, PaxJsonOpts)).RootElement;
            var (_, err) = await Supa.RpcAsync("set_pax_model", new { p_model = model });
            if (err != null)
            {
                Msg(_tariffMsg, Tr("Error: ") + err + "  " + Tr("(el servidor aún no está actualizado)"), true);
                return;
            }
            PaxCfg = c;
            Msg(_tariffMsg, withDemand ? Tr("Modelo de viajeros guardado.") : Tr("Clasificación guardada."), false);
        }

        // ============================ Rankings ============================
        // El ranking de maquinistas es "de esta empresa": si no perteneces a ninguna,
        // se oculta y el ranking público de empresas ocupa todo.
        void UpdateRankLayout()
        {
            // Sin empresa: se oculta toda la sección de "maquinistas de esta empresa"
            // (panel Bottom) y el ranking público de empresas ocupa todo.
            if (_rankDriversPanel != null) _rankDriversPanel.Visible = _empSel != null;
        }

        async void LoadRankings()
        {
            if (!Supa.IsLoggedIn) return;
            UpdateRankLayout();
            if (_rankCompanies != null) _rankCompanies.SetEmpty(Tr("Cargando…"));
            var (json, err) = await Supa.RpcAsync("public_company_ranking", new { });
            if (_rankCompanies != null)
            {
                _rankCompanies.ClearRows();
                if (err != null) _rankCompanies.SetEmpty(Tr("Error: ") + err);
                else
                {
                    int pos = 0;
                    try
                    {
                        using var d = JsonDocument.Parse(json);
                        foreach (var e in d.RootElement.EnumerateArray())
                        {
                            // Cada fila es independiente: un logo/celda con problema no debe
                            // dejar en blanco todo el ranking.
                            try
                            {
                                string cid = Str(e, "company_id"); string name = Str(e, "name");
                                double km = Num(e, "total_km"); double bal = Num(e, "balance"); double sv = Num(e, "services_count");
                                Image logo = null;
                                try { logo = LogoFor(cid, Str(e, "logo")) ?? PlaceholderLogo(name); } catch { logo = null; }
                                int rank = pos + 1;
                                _rankCompanies.AddRow(new[] { rank + ".", name, km.ToString("N0", EsEs), sv.ToString("N0", EsEs), bal.ToString("N0", EsEs) + " €" },
                                    new Color?[] { rank <= 3 ? Theme.Accent : (Color?)null, null, null, null, null }, logo);
                                pos++;
                            }
                            catch { }
                        }
                    }
                    catch { }
                    if (pos == 0) _rankCompanies.SetEmpty(Tr("Sin datos todavía."));
                }
            }
            if (_empSel != null) await LoadDriverRanking(_empSel);
            else if (_rankDrivers != null) _rankDrivers.SetEmpty(Tr("Selecciona una empresa para ver su ranking."));
        }

        async Task LoadDriverRanking(EmpCompany c)
        {
            if (_rankDrivers == null) return;
            _rankDrivers.SetEmpty(Tr("Cargando…"));
            var (json, err) = await Supa.RpcAsync("company_driver_ranking", new { p_company = c.Id });
            _rankDrivers.ClearRows();
            if (err != null) { _rankDrivers.SetEmpty(Tr("Error: ") + err); return; }
            int pos = 0;
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    pos++;
                    string name = Str(e, "username"); if (name.Length == 0) name = "—";
                    double km = Num(e, "total_km"); double net = Num(e, "total_net"); double sv = Num(e, "services_count");
                    _rankDrivers.AddRow(new[] { pos + ".", name, sv.ToString("N0", EsEs), km.ToString("N0", EsEs), net.ToString("N0", EsEs) + " €" },
                        new Color?[] { pos <= 3 ? Theme.Accent : (Color?)null, null, null, null, net < 0 ? RedC : (Color?)null });
                }
            }
            catch { }
            if (pos == 0) _rankDrivers.SetEmpty(Tr("Sin servicios completados todavía."));
        }

        async void AddMember()
        {
            if (_empSel == null) return;
            if (!CanManage() && !Supa.IsSuperadmin) { Msg(_memberMsg, Tr("Solo el gerente o un gestor pueden añadir socios."), true); return; }
            var input = _memberEmail.Box.Text.Trim();
            if (input.Length < 2) { Msg(_memberMsg, Tr("Pega un ID de usuario o escribe un nombre de usuario."), true); return; }
            string role = RoleFromIndex((_memberAddRole ?? _memberRole).SelectedIndex);   // rol elegido en «Añadir socio»
            if (role == "owner" && !IsOwner() && !Supa.IsSuperadmin) { Msg(_memberMsg, Tr("Solo el gerente puede nombrar gerentes."), true); return; }
            Msg(_memberMsg, Tr("Añadiendo socio…"), false);
            // Si es un UUID → por ID; si no → por nombre de usuario.
            (string json, string err) r = System.Guid.TryParse(input, out _)
                ? await Supa.RpcAsync("add_member_by_id", new { p_company = _empSel.Id, p_user = input, p_role = role })
                : await Supa.RpcAsync("add_member_by_username", new { p_company = _empSel.Id, p_username = input, p_role = role });
            if (r.err != null) { Msg(_memberMsg, Tr("Error: ") + r.err, true); return; }
            _memberEmail.Box.Text = "";
            Msg(_memberMsg, Tr("Socio añadido."), false);
            LoadMembers(_empSel);
        }

        async void RemoveMember()
        {
            if (_empSel == null) return;
            if (!CanManage() && !Supa.IsSuperadmin) { Msg(_memberMsg, Tr("Solo el gerente o un gestor pueden quitar socios."), true); return; }
            int i = _memberList.SelectedRow;
            if (i < 0 || i >= _members.Count) { Msg(_memberMsg, Tr("Selecciona un socio de la lista."), true); return; }
            var m = _members[i];
            if (m.Role == "owner") { Msg(_memberMsg, Tr("No se puede quitar al gerente."), true); return; }
            if (MessageBox.Show(this, string.Format(Tr("¿Quitar a «{0}» de la empresa?"), m.Username), "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var (_, err) = await Supa.RpcAsync("remove_member", new { p_company = _empSel.Id, p_user = m.UserId });
            if (err != null) { Msg(_memberMsg, Tr("Error: ") + err, true); return; }
            Msg(_memberMsg, Tr("Socio quitado."), false);
            LoadMembers(_empSel);
        }

        static string RoleFromIndex(int i) => i switch { 1 => "manager", 2 => "owner", _ => "driver" };
        static string TrRole(string r) => r switch
        {
            "owner" => Tr("Gerente"),
            "manager" => Tr("Gestor"),
            "driver" => Tr("Maquinista"),
            _ => r
        };

        // ============================ Estimación de km ============================
        double EstimateKm()
        {
            try
            {
                var p = CurrentPath();
                if (p != null && !string.IsNullOrEmpty(p.FilePath) && System.IO.File.Exists(p.FilePath))
                    return EstimatePathKm(p.FilePath);
            }
            catch { }
            return 0;
        }

        // Parser ligero de .pat: suma la longitud entre los puntos del recorrido principal.
        static double EstimatePathKm(string patFile)
        {
            try
            {
                string text = ReadMstsText(patFile);
                if (string.IsNullOrEmpty(text)) return 0;

                // Puntos de dibujo: TrackPDP ( tileX tileZ X Y Z ... )
                var pdps = new List<(double x, double z)>();
                var mPdp = System.Text.RegularExpressions.Regex.Matches(
                    text, @"TrackPDP\s*\(\s*(-?\d+)\s+(-?\d+)\s+(-?[\d.eE+]+)\s+(-?[\d.eE+]+)\s+(-?[\d.eE+]+)");
                foreach (System.Text.RegularExpressions.Match m in mPdp)
                {
                    double tileX = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    double tileZ = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    double x = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    double z = double.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
                    pdps.Add((tileX * 2048.0 + x, tileZ * 2048.0 + z));
                }
                if (pdps.Count < 2) return 0;

                // Cadena principal: TrPathNode ( flags nextMain nextSiding pdpIndex )
                var order = new List<int>();
                var mNodes = System.Text.RegularExpressions.Regex.Matches(
                    text, @"TrPathNode\s*\(\s*([0-9a-fA-Fx]+)\s+([0-9a-fA-Fx]+)\s+([0-9a-fA-Fx]+)\s+(\d+)\s*\)");
                if (mNodes.Count >= 2)
                {
                    var next = new List<int>(); var pdpIdx = new List<int>();
                    foreach (System.Text.RegularExpressions.Match m in mNodes)
                    {
                        next.Add(ParseFlex(m.Groups[2].Value));
                        pdpIdx.Add(int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture));
                    }
                    int cur = 0, guard = 0;
                    while (cur >= 0 && cur < next.Count && guard++ < next.Count + 2)
                    {
                        if (pdpIdx[cur] >= 0 && pdpIdx[cur] < pdps.Count) order.Add(pdpIdx[cur]);
                        int nx = next[cur];
                        if (nx == cur || nx < 0 || nx >= next.Count) break;
                        cur = nx;
                    }
                }
                // Fallback: si no se pudo seguir la cadena, usa los PDP en orden de fichero.
                double meters = 0;
                if (order.Count >= 2)
                    for (int i = 1; i < order.Count; i++) meters += Dist(pdps[order[i - 1]], pdps[order[i]]);
                else
                    for (int i = 1; i < pdps.Count; i++) meters += Dist(pdps[i - 1], pdps[i]);
                return Math.Round(meters / 1000.0, 1);
            }
            catch { return 0; }
        }

        static double Dist((double x, double z) a, (double x, double z) b)
        {
            double dx = a.x - b.x, dz = a.z - b.z;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        static int ParseFlex(string s)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (uint.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u))
                    return u == 0xffffffff ? -1 : (int)u;
                return -1;
            }
            if (uint.TryParse(s, out var v)) return v == 0xffffffff ? -1 : (int)v;
            return -1;
        }

        // Lee un .pat de MSTS (texto), tolerando UTF-16 (con BOM) o UTF-8.
        // ---- Cachés de contenido ----
        // Los .con y los .eng/.wag no cambian mientras se usa el programa (y cuando el editor toca
        // alguno se vacían a mano), así que cada archivo se lee y se analiza UNA sola vez. Antes, una
        // tasación releía la composición entera y todos sus vehículos en cada clic.
        sealed class VehStats
        {
            public double Capacity, MassT, BrakeKn, Width, Length, PowerKw, SpeedKmh;
            public bool Tilting;
            public string EngineType = "electric";
        }
        static readonly Dictionary<string, VehStats> _vehStats = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, List<(string name, string folder, bool isEngine)>> _conRefs = new(StringComparer.OrdinalIgnoreCase);

        // Datos de un vehículo (.eng/.wag), leídos y analizados una única vez.
        static VehStats Veh(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (_vehStats) if (_vehStats.TryGetValue(path, out var hit)) return hit;
            var v = new VehStats();
            try
            {
                // Los datos están en los primeros bloques del archivo: no hace falta leer .eng enteros
                // (los hay de varios MB con sonidos y cabina).
                string t = ReadHead(path, 200000);
                if (!string.IsNullOrEmpty(t))
                {
                    v.Capacity = ExtractCapacity(t);
                    v.MassT = ExtractMassT(t);
                    v.BrakeKn = ExtractBrakeKn(t);
                    var (w, l) = ExtractSize(t);
                    v.Width = w; v.Length = l;
                    v.Tilting = IsTilting(t);
                    v.PowerKw = ExtractPowerKw(t);
                    v.SpeedKmh = ExtractSpeedKmh(t);
                    string low = t.ToLowerInvariant();
                    v.EngineType = low.Contains("diesel") ? "diesel" : low.Contains("steam") ? "steam" : "electric";
                }
            }
            catch { }
            lock (_vehStats) _vehStats[path] = v;
            return v;
        }

        // Nº de coches de un .con (sin leer el archivo si ya está en la caché).
        static int ConsistCarCount(string conPath) => ConsistCarRefs(conPath).Count;

        // Datos de un vehículo para el resto de secciones (editor incluido).
        static VehStats VehicleStats(string path) => Veh(path);

        // El editor toca archivos del contenido: sus datos se vuelven a leer la próxima vez.
        public static void ClearContentCaches()
        {
            lock (_vehStats) _vehStats.Clear();
            lock (_conRefs) _conRefs.Clear();
            lock (_vehHead) _vehHead.Clear();
            FastConsists.ClearCache();
            _allVehicles = null;   // pudo añadirse o borrarse algún vehículo
        }

        static string ReadMstsText(string file)
        {
            try
            {
                var bytes = System.IO.File.ReadAllBytes(file);
                if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
                    return System.Text.Encoding.Unicode.GetString(bytes);
                // Heurística: muchos ceros → UTF-16 sin BOM
                int zeros = 0, n = Math.Min(bytes.Length, 200);
                for (int i = 0; i < n; i++) if (bytes[i] == 0) zeros++;
                if (zeros > n / 4) return System.Text.Encoding.Unicode.GetString(bytes);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }

        // ============================ Utilidades ============================
        static List<EmpCompany> ParseCompanies(string json)
        {
            var list = new List<EmpCompany>();
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                    list.Add(new EmpCompany
                    {
                        Id = Str(e, "id"), Name = Str(e, "name"), Logo = Str(e, "logo"), Balance = Num(e, "balance"),
                        IncomePerKm = Num(e, "income_per_km"), CanonPerKm = Num(e, "canon_per_km"),
                        EnergyPerKm = Num(e, "energy_per_km"), SalaryPerService = Num(e, "salary_per_service"),
                        PaEnabled = Flag(e, "pa_enabled")
                    });
            }
            catch { }
            return list;
        }

        static string Str(JsonElement e, string prop)
            => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

        static double Num(JsonElement e, string prop)
        {
            if (!e.TryGetProperty(prop, out var v)) return 0;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s)) return s;
            return 0;
        }

        static double ParseNum(string s)
        {
            s = (s ?? "").Trim().Replace(" ", "").Replace(".", "").Replace(",", ".");
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }

        static string TrStatus(string s) => s switch
        {
            "open" => Tr("En conducción"),
            "completed" => Tr("completado"),
            "failed" => Tr("fallido"),
            "cancelled" => Tr("cancelado"),
            _ => s
        };

        // Icono (glifo monocromo, GDI) para la columna ESTADO de servicios.
        static string StatusIcon(string status, bool valid)
        {
            if (!valid) return "⚠";
            return status switch
            {
                "completed" => "✓",
                "failed" => "✕",
                "cancelled" => "⊘",
                "open" => "▶",
                _ => "•"
            };
        }

        // Color del texto+icono de la columna ESTADO.
        static Color StatusColor(string status, bool valid)
        {
            if (status == "open") return Color.FromArgb(120, 144, 226);   // En conducción (aunque aún no esté validado)
            if (!valid) return RedC;
            return status switch
            {
                "completed" => Theme.Accent,
                "failed" => RedC,
                "cancelled" => RedC,
                _ => Theme.Subtle
            };
        }

        // Etiqueta completa de estado: icono + texto (traducido). Un servicio EN CURSO ("open")
        // se muestra siempre "En conducción", aunque aún no esté validado (no es "no válido").
        static string StatusLabel(string status, bool valid)
            => status == "open"
                ? "▶  " + TrStatus("open")
                : StatusIcon(status, valid) + "  " + (valid ? TrStatus(status) : Tr("no válido"));

        // ---- Fábricas de controles con el tema ----
        static Label EmpTitle(string t) => new Label { Text = I18n.T(t), AutoSize = true, ForeColor = Theme.Text, Font = Theme.Font(17f, FontStyle.Bold), Margin = new Padding(2, 4, 2, 2) };
        static Label EmpIntro(string t) => new Label { Text = I18n.T(t), AutoSize = true, MaximumSize = new Size(540, 0), ForeColor = Theme.Subtle, Font = Theme.Font(9.5f), Margin = new Padding(2, 0, 2, 8) };
        static Label EmpHeader(string t) => new Label { Text = I18n.T(t), AutoSize = true, ForeColor = Theme.Accent, Font = Theme.Font(8.5f, FontStyle.Bold), Margin = new Padding(2, 14, 2, 4), UseCompatibleTextRendering = false };
        static Label EmpFieldLabel(string t) => new Label { Text = I18n.T(t), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(9f), Margin = new Padding(2, 8, 2, 2) };
        static Label EmpMsg() => new Label { Text = "", AutoSize = true, MaximumSize = new Size(540, 0), ForeColor = Theme.Subtle, Font = Theme.Font(9f), Margin = new Padding(2, 10, 2, 2) };

        // Anchor L|R: en columna AutoSize mantiene su ancho fijo; en columna Percent se estira.
        static RoundedInput EmpInput(string placeholder)
            => new RoundedInput(I18n.T(placeholder))
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right, Width = EmpFormWidth, Margin = new Padding(2, 0, 2, 2)
            };

        // Nota de ayuda para los formularios de la columna izquierda (estrecha). Una Label con
        // AutoSize=true dentro de un FlowLayoutPanel se queda en una sola línea (ignora MaximumSize),
        // así que fijamos el ancho y medimos la altura del texto envuelto para que no se recorte.
        static Label EmpSideNote(string text)
        {
            var t = I18n.T(text);
            var f = Theme.Font(9f);
            int h = TextRenderer.MeasureText(t, f, new Size(EmpSideNoteWidth, int.MaxValue), TextFormatFlags.WordBreak).Height;
            return new Label
            {
                Text = t, AutoSize = false, Width = EmpSideNoteWidth, Height = h + 2,
                ForeColor = Theme.Subtle, Font = f, Margin = new Padding(2, 0, 2, 4)
            };
        }

        static RoundButton EmpButton(string text, bool primary = false) => new RoundButton
        {
            Text = I18n.T(text), Radius = 8, Height = 38, Width = 240, AutoSize = false,
            Dock = DockStyle.None, Margin = new Padding(2, 10, 2, 2),
            BaseColor = primary ? Theme.Accent : Theme.Surface2,       // plano, sin degradado (estilo moderno)
            HoverColor = primary ? Theme.AccentHi : Theme.SurfaceHi,
            ActiveColor = Theme.Accent,
            TextColor = primary ? Color.White : Theme.Text,
            FontSize = 10.5f, FontStyle = FontStyle.Bold
        };

        static Label EmpLink(string t)
        {
            var l = new Label { Text = I18n.T(t), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(9f, FontStyle.Underline), Cursor = Cursors.Hand, Margin = new Padding(2, 12, 2, 2) };
            l.MouseEnter += (s, e) => l.ForeColor = Theme.Text;
            l.MouseLeave += (s, e) => l.ForeColor = Theme.Subtle;
            return l;
        }

        static ListBox EmpList()
        {
            var lb = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                BackColor = Theme.Surface2, ForeColor = Theme.Text,
                IntegralHeight = false, Font = Theme.Font(10f), Margin = new Padding(2, 4, 2, 4),
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 42
            };
            Native.UseDarkScrollBars(lb);
            return lb;
        }

        static StyledTable EmpTable()
        {
            return new StyledTable { Dock = DockStyle.Fill, Margin = new Padding(2, 4, 2, 4) };
        }

        // Caja de filtro (🔎) enlazada a una tabla: escribe para filtrar por texto en cualquier columna.
        static RoundedInput EmpSearch(StyledTable table, int width = 300)
        {
            var box = new RoundedInput(I18n.T("🔎  Filtrar…"))
            {
                Anchor = AnchorStyles.Left, Width = width, Height = 34, Margin = new Padding(2, 2, 8, 4)
            };
            box.Box.TextChanged += (s, e) => table.Filter(box.Box.Text);
            return box;
        }

        static void Msg(Label l, string text, bool error)
        {
            if (l == null) return;
            l.Text = text;
            l.ForeColor = error ? Color.FromArgb(229, 115, 115) : Theme.Subtle;
        }
    }
}
