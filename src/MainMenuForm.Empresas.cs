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
        Label _empCoIdLbl;  // ID de la empresa («EMP-XXXXXX»), copiable con un clic
        ComboBox _empCoCombo;                       // selector de empresa (arriba del menú lateral)
        bool _suppressCoSel;                        // ignora SelectedIndexChanged del selector durante la recarga
        EmpKpiStrip _empStatsCard; Panel _empStatsGap;           // KPIs de la empresa (se ocultan sin empresa y en Ranking / Mi perfil)
        // Valores de los KPIs (la tira se pinta en DrawKpiStrip; se actualizan y se hace Invalidate).
        string _kpiTreasuryTxt = "—", _kpiMembersTxt = "—", _kpiServicesTxt = "—", _kpiKmTxt = "—";
        Color _kpiTreasuryCol = Theme.Accent;
        List<(Label header, int[] items)> _navGroups; // encabezados del menú lateral (se ocultan si su grupo no tiene secciones visibles)
        PictureBox _empLogoPic; Label _logoLink;   // logotipo en la cabecera + enlace para cambiarlo
        Label _empRenameLink;                       // icono ✎ junto al nombre de empresa (renombrar)
        readonly Dictionary<string, (string src, Image img)> _logoCache = new();
        ServiceCardList _svcCards;          // servicios de la empresa, en tarjetas
        FlowLayoutPanel _svcTabs;
        string _svcCardsCompany;            // empresa de las tarjetas (si cambia, «Cargando…»)
        string _svcLastJson, _svcLastTrenJson;   // última respuesta: si no cambia, no se rehace nada
        int _svcLoadSeq;                    // descarta respuestas de cargas anteriores
        int _svcColsLevel;                  // columnas que admite el servidor (se recuerda: sin reintentos)
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
        // Cronómetro del servicio: tiempo REAL, pero solo mientras Open Rails está en marcha. Con el juego en
        // pausa (tecla Pausa o menú de Esc) la hora del simulador (/API/TIME) no avanza, y ese tiempo no cuenta.
        // El acelerador de tiempo de OR tampoco cuenta: se suma el tiempo real, no el del juego.
        // Tren del servicio en curso, tal como se envió al abrirlo: plazas (viajeros) o toneladas (mercancías,
        // con las que el servidor calcula el ingreso). NaN = no se sabe.
        double _svcTrainMass = double.NaN, _svcTrainCap = double.NaN;
        double _svcRunS;                   // segundos de marcha acumulados hasta el último sondeo
        DateTime? _svcLastTickUtc;         // último sondeo (el tramo desde ahí se suma si no hay pausa)
        double _svcLastGameS = double.NaN; // hora del juego en el último sondeo
        bool _svcPaused;                   // Open Rails está en pausa

        void StartSvcClock(DateTime? t)
        {
            _svcClockUtc = t;
            _svcRunS = 0; _svcLastTickUtc = t; _svcLastGameS = double.NaN; _svcPaused = false;
            InfrReset();   // carné por puntos: servicio nuevo, nada detectado
            _svcTrail.Clear();   // y su rastro para el mapa del informe
        }

        double ServiceSecondsExact()
        {
            if (_svcClockUtc == null) return 0;
            double s = _svcRunS;
            if (!_svcPaused && _svcLastTickUtc != null) s += Math.Max(0, (DateTime.UtcNow - _svcLastTickUtc.Value).TotalSeconds);
            return s;
        }

        int ServiceSeconds() => (int)Math.Max(0, ServiceSecondsExact());

        // Para el HUD, que cuenta desde una hora de inicio: la que da el tiempo de marcha hasta ahora.
        DateTime? SvcVirtualStart() => _svcClockUtc == null ? (DateTime?)null : DateTime.UtcNow - TimeSpan.FromSeconds(ServiceSecondsExact());

        // En cada sondeo: ¿ha avanzado la hora del juego? Si sí, se suma el tramo; si no, el juego está en pausa.
        async Task SvcClockTick()
        {
            if (_svcClockUtc == null) return;
            double game = await FetchGameSeconds();
            var now = DateTime.UtcNow;
            if (_svcLastTickUtc != null)
            {
                // Sin dato (API que no responde): se cuenta, como antes, para no perder tiempo por un fallo.
                bool running = double.IsNaN(game) || double.IsNaN(_svcLastGameS) || Math.Abs(game - _svcLastGameS) > 0.05;
                if (running) { double add = Math.Max(0, (now - _svcLastTickUtc.Value).TotalSeconds); _svcRunS += add; if (_apOn) _apSeconds += add; }   // (A4: tiempo con piloto automático)
                _svcPaused = !running;
                // Carné (A3): ¿la hora del juego corre más que la real?
                if (!double.IsNaN(game) && !double.IsNaN(_svcLastGameS))
                    InfrAccelTick((now - _svcLastTickUtc.Value).TotalSeconds, game - _svcLastGameS);
            }
            _svcLastTickUtc = now;
            if (!double.IsNaN(game)) _svcLastGameS = game;
        }

        async Task<double> FetchGameSeconds()
        {
            try
            {
                string txt = await OrApi.GetStringAsync(_kmHttp, "/API/TIME");
                if (double.TryParse(txt.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out double secs)) return secs;
            }
            catch { }
            return double.NaN;
        }
        int _tripDurationS;                // duración REAL del viaje en segundos (anti-trampas)
        // Seguimiento de km reales por la API web de OR (posición del tren en vivo)
        Timer _kmTimer; System.Net.Http.HttpClient _kmHttp;
        double _trackedMeters, _tLat, _tLon; bool _tHave;
        // Km creíbles: hora de la lectura anterior y lo que se ha descartado por imposible (diagnóstico).
        readonly System.Diagnostics.Stopwatch _kmClock = System.Diagnostics.Stopwatch.StartNew();
        double _kmLastFixS = double.NaN, _kmRejectedM; int _kmRejected;

        // Lo que el tren puede haber recorrido de verdad en dt segundos: a su velocidad máxima × 1,5 (nunca menos
        // de 200 ni más de 450 km/h, por si el .eng la declara mal) más 60 m de tolerancia (redondeos y lecturas
        // que llegan un poco tarde). Lo que pase de ahí es un salto (cambio de tren o de cámara en Open Rails, una
        // lectura atrasada mientras guarda…) y NO se suma: antes se sumaba cualquier salto de menos de 3 km, los
        // km se inflaban y el servidor veía una velocidad media imposible (A2) que no era real.
        double PlausibleMeters(double dt)
        {
            double vmax = _roadVmax > 0 ? Math.Max(200, Math.Min(450, _roadVmax * 1.5)) : 450;
            double m = vmax / 3.6 * dt + 60;
            // Mejor aún: la velocidad que da el propio Open Rails (Track Monitor, leída en cada sondeo durante un
            // servicio). La posición es la de la LOCOMOTORA del jugador: con el tren parado salta si OR cambia de
            // cabina o de extremo del tren, o de tren desde el despachador, y eso sumaba km sin moverse un metro.
            // Con el tren parado según OR, como mucho 8 m (redondeos), que además se descartan como ruido.
            double now = _kmClock.Elapsed.TotalSeconds;
            if (!double.IsNaN(_orKmhAtS) && now - _orKmhAtS < 5)
            {
                double v = Math.Max(_orKmhLast, double.IsNaN(_orKmhMax) ? 0 : _orKmhMax);
                m = Math.Min(m, v / 3.6 * dt * 1.3 + 8);
            }
            _orKmhMax = double.NaN;   // la próxima lectura de posición mira las velocidades leídas desde ahora
            return m;
        }

        // Velocidad del tren según Open Rails (km/h), leída del Track Monitor.
        double _orKmhLast, _orKmhMax = double.NaN, _orKmhAtS = double.NaN;
        void KmSpeedSample(double kmh)
        {
            if (double.IsNaN(kmh)) return;
            _orKmhLast = kmh; _orKmhMax = double.IsNaN(_orKmhMax) ? kmh : Math.Max(_orKmhMax, kmh);
            _orKmhAtS = _kmClock.Elapsed.TotalSeconds;
        }
        readonly DriveTrail _driveTrail = new();   // rastro del tren en el mapa desde el origen (toda la conducción)
        // Modelo de viajeros (PseudoPAX): embarque en andenes reales de la ruta
        List<(string station, double lat, double lon)> _paxStations = new();
        readonly HashSet<string> _paxDone = new();   // andenes ya embarcados este servicio
        int _paxBoarded, _paxOnboard, _paxCapacity;  // billetes totales, a bordo ahora, tope de la unidad
        double _paxKm;                                // viajeros·km: cada viajero por los km que va a bordo (billete por km)
        bool _paxActive; bool _paxBusy; volatile bool _paxWanted;   // activo / poll en curso / se quiere seguir cargando andenes
        double _paxDemandBase = 60;                  // antigua demanda base fija (app_settings.fleet_pax_demand): ya no se usa, la afluencia sale de las plazas del tren
        Panel _soloPanel;   // panel "Mi perfil"
        // Panel "Mi perfil" (estadísticas privadas del maquinista)
        TrainTopList _profTrains; RouteBars _profRoutes; ProfileHero _profHero; BadgeGrid _badgeGrid;
        Label _profKmVal, _profTripsVal, _profTimeVal, _profSpeedVal, _profPaxVal;
        // Rango + insignias (Mi perfil)
        Label _rankName, _rankNext, _rankStep; Panel _rankTrack; double _rankPct; FlowLayoutPanel _badges;
        // Panel financiero (dentro de Banca)
        BankSummary _bankSummary; BankStatement _bankStmt;
        readonly List<BankStatement.Move> _ledgerAll = new();
        int _bankPeriod = 1;                 // 0 este mes · 1 seis meses · 2 todo
        int _bankConcept;                    // filtro de concepto del extracto (0 = todos)
        string _bankSearch = "";
        FlowLayoutPanel _bankChips;
        RoundButton _empDutyBtn, _empCloseOpenBtn;
        // --- Socios ---
        readonly List<EmpMember> _members = new();
        string _myRole;                    // mi rol en la empresa seleccionada (owner/manager/driver)
        MemberCardGrid _memberCards;   // socios como carnets
        RoundedInput _memberSearch;
        readonly Dictionary<string, (DateTime joined, double rankKm, double services, double km, double net)> _memberCardData = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DateTime> _memberJoined = new(StringComparer.OrdinalIgnoreCase);
        RoundedInput _memberEmail;
        ComboBox _memberRole;
        Label _memberMsg;
        RoundButton _memberAddBtn, _memberDelBtn;
        // Solicitudes de ingreso (un maquinista pide unirse a la empresa)
        CardTable _joinList; readonly List<string> _joinIds = new();
        // --- Banca (historial de movimientos) ---
        Label _bankMsg; Panel _bankPanel;
        // --- Cabecera profesional ---
        Label _empRoleLbl;
        RoundButton _memberRoleBtn;
        // --- Subpestañas (Servicios / Banca / Socios / Ajustes / Ranking / Mi perfil / Revisión) ---
        RoundButton[] _empSubtabs;
        Panel _svcPanel, _memberPanel, _tariffPanel, _rankPanel, _reviewPanel, _usersPanel;
        int _empSubtab;
        // Revisión de servicios sospechosos (superadmin)
        // Gestión de usuarios (solo superadmin)
        UserCardList _usersList; Label _usersMsg;
        readonly List<UserItem> _usersAll = new(); string _usersQuery = "";
        readonly List<string> _userIds = new(); readonly List<bool> _userIsSelf = new(); readonly List<string> _userKeys = new();
        // Administración de TODAS las empresas (solo superadmin)
        Panel _allCompPanel; CardTable _allCompList; Label _allCompMsg;
        readonly List<string> _allCompIds = new();
        // Flota de la empresa (vehículos por .eng) — tabla + preview 3D
        Panel _fleetPanel; FleetCardList _fleetCards; Label _fleetMsg;
        // Vistas 2D de los modelos (caché en disco) y visores 2D/3D de Flota y Compra
        VehicleThumbs _thumbs; VehicleViewport _fleetOwnView, _buyView;
        FlowLayoutPanel _fleetTabs, _buyKindTabs; int _buyKind;   // Compra: 0 todas · 1 en tu flota · 2 automotores · 3 locomotoras
        // Compra/alquiler (subpestaña aparte): se compra/alquila un CONSIST completo (no un .eng
        // suelto); cada máquina de tracción que contiene (menos las ya en la flota) se da de alta
        // por separado, así que esa misma máquina queda desbloqueada para cualquier otro consist
        // que la reutilice.
        Panel _buyPanel; Label _buyMsg;
        ListBox _fleetConsistList; RoundedInput _fleetConsistSearch;
        RoundButton _fleetBuyBtn, _fleetRentBtn, _fleetCompBtn, _fleetRemoveBtn, _fleetPlateBtn; TrainPreviewPanel _fleetPreview;
        readonly List<string> _fleetPlates = new();   // matrícula de cada fila de la flota ("" = sin matrícula)
        // Ficha técnica del showroom (Comprar/Alquilar): título de tipo, chips de specs y precios.
        Label _fleetHeaderLbl, _fleetSpecType, _vPower, _vSpeed, _vPlazas, _vConfort, _vMasa, _vFreno, _vDensity, _vBuy, _vRent, _fleetOwnedBadge;
        // Estado del render 3D rotatable del preview de Flota (igual que el de Conducción libre).
        ShapeGeom _fleetGeom; float _fleetYaw, _fleetPitch; bool _fleetFlip; string _fleetPrevEngPath;
        System.Windows.Forms.Timer _fleetRerender;
        System.Windows.Forms.Timer _fleetOwnRerender;   // vehículo de la flota: render tras cambiar de tamaño
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
        // Trenes que pertenecen a una empresa DE LA QUE SOY SOCIO (para la etiqueta en Conducción libre/Horarios).
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
            public bool Extra;                        // fuera del catálogo: está porque la empresa la tiene
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
        BuyCardGrid _fleetEngList;                       // máquinas en venta, en tarjetas
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
        RoundButton _svcDelBtn, _ledgerDelBtn, _svcAnnulBtn;
        ServiceCalendar _svcCal;
        (long total, long open, long done, long annulled, double km)? _svcStats;   // totales del servidor (sin límite)
        readonly List<string> _ledgerIds = new();
        readonly List<SvcRow> _svcRows = new();   // datos por servicio (para la ventana de detalle)

        sealed class SvcRow
        {
            public string Id, Date, Driver, Route, Status;
            public double Km, DurationS, Pax, Income, Cost, Net;
            public double MassT = double.NaN, Capacity = double.NaN;   // toneladas y plazas del tren (NaN = no se sabe)
            public string Train = "";                                   // tren conducido (composición)
            public int Cars, Engines;                                   // vehículos de su .con y cuántos son motrices (0 = no se sabe)
            public bool Valid;
            public string Path = "";                                    // recorrido (.pat)
            public DateTime Start = DateTime.MinValue;                  // salida, hora local
            public bool HasImage;                                       // tiene composición 2D guardada
            public bool Annulled;                                       // anulado por el superadmin
            public string AnnulReason = "", AnnulledBy = "", AnnulledAt = "";
        }

        // «300 plazas» si el tren lleva viajeros; «1.250 t» si es de mercancías (el ingreso se calcula con esa
        // masa); «—» si el servicio es de una versión antigua que no lo guardaba.
        static string TrainLoadText(double capacity, double massT)
        {
            if (capacity > 0) return string.Format(Tr("{0} plazas"), capacity.ToString("N0", EsEs));
            if (massT > 0) return massT.ToString("N0", EsEs) + " t";
            return "—";
        }
        // --- Ajustes (tarifas + saldo superadmin) ---
        RoundedInput _tarIncome, _tarCanon, _tarEnergy, _tarSalary, _tarSalaryHour, _tarSalaryMaxH, _tarBalance, _defBalance;
        RoundButton _tarSaveBtn, _tarBalanceBtn, _defBalanceBtn;
        Label _tariffMsg;
        Panel _tarBalanceRow, _defBalanceRow;
        // Economía de flota (global, superadmin): escala de precio + % alquiler + % mantenimiento + capacidad de referencia
        RoundButton _fleetBuyAllBtn;              // compra masiva (solo superadmin)
        bool _buyingAll, _buyAllCancel;
        RoundedInput _fsScale, _fsRentPct, _fsMaintPct, _fsFareBase, _fsFareKm, _fsPaxDemand; RoundButton _fsSaveBtn; Panel _fleetSettingsRow;
        double _fsCapBaseVal = 300;   // capacidad de referencia: ya no se usa (sin celda), se reenvía tal cual al guardar
        // --- Ranking ---
        PodiumBoard _rankCompanies, _rankDrivers;   // Ranking → Empresas y Maquinistas (podio + tarjetas)

        sealed class EmpMember
        {
            public string UserId, Username, Role;
        }
        static readonly CultureInfo EsEs = CultureInfo.GetCultureInfo("es-ES");

        sealed class EmpCompany
        {
            public string Id, Name, Logo, Code = ""; public double Balance;   // Code: ID propio («EMP-XXXXXX»)
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
            var forgot = EmpLink(Tr("¿Has olvidado la contraseña?"));
            forgot.Click += (s, e) => ForgotPassword();   // con la clave de recuperación
            form.Controls.Add(forgot);

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
        static readonly string[] SubNames = { "Servicios", "Banca", "Socios", "Ajustes", "Ranking", "Mi perfil", "Revisión", "Usuarios", "Administración", "Flota", "Compra", "Megafonía", "Chat", "Normas", "Préstamos", "Rutas", "Catálogo de rutas" };
        static readonly string[] SubGlyphs = { "clock", "bank", "connect", "gear", "activity", "info", "shield", "connect", "globe", "train", "train", "speaker", "chat", "rules", "bank", "map", "map" };
        // Agrupación del menú lateral.
        static readonly (string title, int[] items)[] NavGroupDefs =
        {
            ("OPERACIÓN", new[] { 0, 9, 10 }),        // Servicios · Flota · Compra
            ("FINANZAS", new[] { 1, 4 }),             // Banca · Ranking
            ("EMPRESA", new[] { 12, 2, 13, 15, 3, 11, 5 }),   // Chat · Socios · Normas · Rutas · Ajustes · Megafonía · Mi perfil
            ("ADMINISTRACIÓN", new[] { 6, 14, 16, 7, 8 }),    // Revisión (carné) · Préstamos · Catálogo de rutas · Usuarios · Administración
        };
        const int RailW = 250;          // ancho del menú lateral de Empresas
        static readonly Color RailCardC = Color.FromArgb(38, 46, 41);   // tarjeta de la empresa (verde muy oscuro)
        AvatarBox _railAvatar;
        // Color de cada sección en el menú (la pastilla de su icono).
        static readonly Color[] SubTints =
        {
            Color.FromArgb(120, 144, 226), Color.FromArgb(240, 196, 90), Color.FromArgb(45, 212, 191), Color.FromArgb(150, 160, 170),
            Color.FromArgb(251, 146, 60), Color.FromArgb(102, 197, 106), Color.FromArgb(229, 115, 115), Color.FromArgb(45, 212, 191),
            Color.FromArgb(167, 139, 250), Color.FromArgb(251, 146, 60), Color.FromArgb(102, 197, 106), Color.FromArgb(167, 139, 250), Color.FromArgb(120, 144, 226),
            Color.FromArgb(240, 196, 90), Color.FromArgb(45, 212, 191), Color.FromArgb(240, 180, 70), Color.FromArgb(240, 180, 70)
        };
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
            var card = new Card { Dock = DockStyle.Top, Height = 150, Fill = RailCardC, BorderColor = Color.FromArgb(70, 76, 120, 84), Radius = 14, Padding = new Padding(12) };
            var cc = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = RailCardC, Margin = new Padding(0) };
            cc.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var idRow = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, BackColor = RailCardC, Margin = new Padding(0) };
            idRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            idRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _empLogoPic = new PictureBox { Size = new Size(46, 46), SizeMode = PictureBoxSizeMode.Zoom, BackColor = RailCardC, Margin = new Padding(0, 1, 10, 0), Anchor = AnchorStyles.Left | AnchorStyles.Top, Cursor = Cursors.Hand };
            _empLogoPic.Click += (s, e) => { if (CanManage() || Supa.IsSuperadmin) ChangeLogo(); };   // el maquinista no puede cambiarlo
            _empLogoTip = new ToolTip(); _empLogoTip.SetToolTip(_empLogoPic, Tr("Cambiar logotipo"));

            var txt = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = RailCardC, Margin = new Padding(0), Anchor = AnchorStyles.Left | AnchorStyles.Top };
            var titleRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = RailCardC, Margin = new Padding(0) };
            _empCoTitle = new Label { AutoSize = true, MaximumSize = new Size(RailW - 124, 0), ForeColor = Theme.Text, Font = Theme.Font(11.5f, FontStyle.Bold), Margin = new Padding(0) };
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
            _empRoleLbl = new Label { AutoSize = true, MaximumSize = new Size(RailW - 100, 0), ForeColor = Color.FromArgb(170, 200, 175), Font = Theme.Font(8.5f), Margin = new Padding(0, 2, 0, 0) };
            _logoLink = EmpLink(Tr("Cambiar logotipo")); _logoLink.Visible = false;   // compatibilidad (el logo ya es clicable)
            _logoLink.Click += (s, e) => ChangeLogo();
            // ID propio de la empresa, copiable con un clic.
            _empCoIdLbl = new Label { AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(8.25f), Cursor = Cursors.Hand, Margin = new Padding(0, 3, 0, 0), BackColor = RailCardC };
            _empCoIdLbl.Click += (s, e) => { if (!string.IsNullOrEmpty(_empSel?.Code)) { try { Clipboard.SetText(_empSel.Code); Msg(_empHomeMsg, string.Format(Tr("ID de la empresa copiado: {0}"), _empSel.Code), false); } catch { } } };
            _empCoIdLbl.MouseEnter += (s, e) => _empCoIdLbl.ForeColor = Theme.Accent;
            _empCoIdLbl.MouseLeave += (s, e) => _empCoIdLbl.ForeColor = Theme.Subtle;
            new ToolTip().SetToolTip(_empCoIdLbl, Tr("Clic para copiar el ID de la empresa"));
            txt.Controls.Add(titleRow); txt.Controls.Add(_empRoleLbl); txt.Controls.Add(_empCoIdLbl);
            idRow.Controls.Add(_empLogoPic, 0, 0); idRow.Controls.Add(txt, 1, 0);
            cc.Controls.Add(idRow);

            _empCoCombo = NewCombo(); _empCoCombo.Dock = DockStyle.None; _empCoCombo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _empCoCombo.DropDownStyle = ComboBoxStyle.DropDownList; _empCoCombo.Margin = new Padding(0, 10, 0, 0);
            _empCoCombo.SelectedIndexChanged += (s, e) => OnCompanySelected();
            cc.Controls.Add(_empCoCombo);

            _empCreateHost = new Panel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 30, BackColor = RailCardC, Margin = new Padding(0, 10, 0, 0) };
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
                    Text = Tr(title), AutoSize = false, Height = gi == 0 ? NavHeaderH0 : NavHeaderH, ForeColor = Theme.Subtle,
                    Font = Theme.Font(7.5f, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft,
                    Padding = new Padding(10, 0, 0, 4), Margin = new Padding(0), BackColor = Theme.BgSidebar
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
                        Text = Tr(SubNames[idx]), GlyphKind = SubGlyphs[idx], NavStyle = true, NavTint = SubTints[idx], Radius = 9, Height = NavItemH,
                        Margin = new Padding(0, 1, 0, 1),
                        BaseColor = Theme.BgSidebar, HoverColor = Theme.Surface,
                        ActiveColor = Color.FromArgb(40, 70, 44), ActiveTextColor = Theme.AccentHi,
                        TextColor = Color.FromArgb(214, 218, 221), FontSize = 9.5f
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

            // ---------- Usuario (abajo): tarjeta con foto/iniciales, nombre, ID copiable y dos botones ----------
            var user = new Panel { Dock = DockStyle.Bottom, Height = 132, BackColor = Theme.BgSidebar, Padding = new Padding(0, 8, 0, 0) };
            var ucard = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, Radius = 12, Padding = new Padding(10, 10, 10, 10) };
            var uTop = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Theme.Surface };
            _railAvatar = new AvatarBox { Dock = DockStyle.Left, Width = 44 };
            var uTxt = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(10, 2, 0, 0) };
            _empUserLbl = new Label { AutoSize = false, Dock = DockStyle.Top, Height = 22, AutoEllipsis = true, ForeColor = Theme.Text, Font = Theme.Font(9.75f, FontStyle.Bold), BackColor = Theme.Surface };
            _empIdChip = new Label { AutoSize = false, Dock = DockStyle.Top, Height = 18, AutoEllipsis = true, ForeColor = Theme.Subtle, Font = Theme.Font(8.25f), Cursor = Cursors.Hand, BackColor = Theme.Surface };
            _empIdChip.Click += (s, e) => CopyMyId();
            _empIdChip.MouseEnter += (s, e) => _empIdChip.ForeColor = Theme.Accent;
            _empIdChip.MouseLeave += (s, e) => _empIdChip.ForeColor = Theme.Subtle;
            new ToolTip().SetToolTip(_empIdChip, Tr("Clic para copiar tu ID de usuario"));
            uTxt.Controls.Add(_empIdChip); uTxt.Controls.Add(_empUserLbl);
            uTop.Controls.Add(uTxt); uTop.Controls.Add(_railAvatar);
            uTop.Controls.Add(MakeBell());   // campanita de notificaciones, a la derecha del nombre
            RefreshBellCount();
            var ub = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 34, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface, Margin = new Padding(0) };
            ub.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); ub.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            ub.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            // «Mi cuenta»: editar el nombre, cambiar la contraseña o eliminar la cuenta.
            var nameBtn = EmpButton(Tr("⚙  Mi cuenta  ▾")); nameBtn.Dock = DockStyle.Fill; nameBtn.Height = 30; nameBtn.FontSize = 8.75f; nameBtn.Margin = new Padding(0, 0, 3, 0); nameBtn.Radius = 15;
            nameBtn.Click += (s, e) => OpenAccountMenu(nameBtn);
            var outBtn = EmpButton(Tr("Salir")); outBtn.Dock = DockStyle.Fill; outBtn.Height = 30; outBtn.FontSize = 8.75f; outBtn.Margin = new Padding(3, 0, 0, 0); outBtn.Radius = 15;
            outBtn.HoverColor = Color.FromArgb(150, 60, 60);
            new ToolTip().SetToolTip(outBtn, Tr("Cerrar sesión"));
            outBtn.Click += (s, e) => DoSignOut();
            ub.Controls.Add(nameBtn, 0, 0); ub.Controls.Add(outBtn, 1, 0);
            ucard.Controls.Add(ub); ucard.Controls.Add(uTop);
            user.Controls.Add(ucard);
            if (!Supa.HasBuiltIn)   // con backend fijo no se puede cambiar de servidor
            {
                var cfg = EmpLink(Tr("⚙  Servidor")); cfg.Dock = DockStyle.Bottom; cfg.AutoSize = false; cfg.Height = 20; cfg.Margin = new Padding(0);
                cfg.Click += (s, e) => { _empForceCfg = true; RefreshEmpresasView(); };
                user.Controls.Add(cfg); user.Height += 20;
            }

            rail.Controls.Add(nav);    // Fill
            rail.Controls.Add(user);   // Bottom
            rail.Controls.Add(card);   // Top
            return rail;
        }

        // Alturas del menú lateral (se encogen si hace falta para que quepa todo sin barras).
        const int NavItemH = 36, NavItemHMin = 24, NavHeaderH = 30, NavHeaderH0 = 20, NavHeaderHMin = 14;
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
                ReloadSectionForCompany();   // la sección abierta, con los datos de la empresa nueva
                _ = MatchLocalTrainsAsync();  // sus trenes en tu contenido (y sus plazas fijadas), para todos los socios
            }
            else { _myRole = null; UpdateRoleUi(); }
        }

        // Al cambiar de empresa, la sección que está abierta se pone al momento con los datos de la nueva (lo mismo que
        // carga al abrirla). Servicios, Socios y Ajustes ya los recarga OnCompanySelected; las de administración
        // (Revisión, Usuarios, Administración, Préstamos y Catálogo) y Mi perfil no dependen de la empresa elegida.
        void ReloadSectionForCompany()
        {
            if (_empSel == null) return;
            switch (_empSubtab)
            {
                case 1: LoadLedger(); if (_loansPage != null && _loansPage.Visible) LoadLoans(); break;   // Banca: movimientos y préstamos
                case 4: LoadRankTab(); break;                              // Ranking (los maquinistas de la empresa)
                case 9: LoadFleet(); break;                                // Flota: los trenes de la empresa
                case 10: LoadFleet(); LoadPurchaseRequests(); break;       // Compra: trenes y solicitudes de la empresa
                case 11: OnMegafoniaShown(); break;                        // Megafonía: otros audios
                case ChatSubtab: OnChatShown(); break;                     // Chat: otro chat
                case NormasSubtab: UpdateRuleButtons(); LoadRules(); break; // Normas
                case RutasSubtab: LoadCoRoutes(); break;                   // Rutas: las de la empresa
            }
            UpdateCompanyKpis();   // la tira de la empresa (tesorería, socios…) de la nueva
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
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = su ? 2 : 1, BackColor = RailCardC, Margin = new Padding(0), Padding = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            if (su) t.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

            var createBtn = EmpButton(Tr("➕ Crear")); createBtn.Dock = DockStyle.Fill; createBtn.Height = 30; createBtn.Margin = new Padding(0, 0, 3, 0); createBtn.FontSize = 8.75f;
            createBtn.Radius = 15; createBtn.BaseColor = Color.FromArgb(52, 64, 56); createBtn.HoverColor = Color.FromArgb(64, 80, 68);
            createBtn.Click += (s, e) => CreateCompany();
            t.Controls.Add(createBtn, 0, 0);
            var joinBtn = EmpButton(Tr("🙋 Unirse")); joinBtn.Dock = DockStyle.Fill; joinBtn.Height = 30; joinBtn.Margin = new Padding(3, 0, 0, 0); joinBtn.FontSize = 8.75f;
            joinBtn.Radius = 15; joinBtn.BaseColor = Color.FromArgb(52, 64, 56); joinBtn.HoverColor = Color.FromArgb(64, 80, 68);
            joinBtn.Click += (s, e) => RequestJoin();
            t.Controls.Add(joinBtn, 1, 0);

            if (su)
            {
                var del = EmpButton(Tr("Eliminar")); del.Dock = DockStyle.Fill; del.Height = 30; del.FontSize = 8.75f; del.Margin = new Padding(0, 6, 0, 0);
                del.Radius = 15; del.BaseColor = Color.FromArgb(60, 44, 44); del.HoverColor = Color.FromArgb(150, 60, 60); del.TextColor = Color.FromArgb(229, 115, 115);
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
            _liveShareBox = BuildLiveShareBox();   // «Compartir mi posición en el mapa» (solo en Mi perfil)
            titleBar.Controls.Add(_liveShareBox);

            // KPIs (Tesorería · Socios · Servicios · Km) como 4 tarjetas compactas dibujadas a mano.
            _empStatsCard = new EmpKpiStrip { Dock = DockStyle.Top, Height = 78 };
            _empStatsGap = new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Theme.Bg };
            var gap = _empStatsGap;

            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            _svcPanel = BuildSvcSubpanel();
            _bankPanel = BuildBankSubpanel();
            _memberPanel = BuildMembersSubpanel();
            _tariffPanel = BuildTariffSubpanel();
            _rankPanel = BuildRankSubpanel();
            _soloPanel = BuildProfileSubpanel();
            _reviewPanel = BuildReviewSubpanel();   // infracciones del carné por puntos (gerente y gestores)
            _chatPanel = BuildChatSubpanel();       // chat de la empresa
            _usersPanel = BuildUsersSubpanel();
            _allCompPanel = BuildAllCompaniesSubpanel();
            _fleetPanel = BuildFleetSubpanel();
            _buyPanel = BuildBuySubpanel();
            _paPanel = BuildPaSubpanel();
            _rulesPanel = BuildRulesSubpanel();           // normas internas de la empresa
            BuildLoansAdminSubpanel();                    // préstamos de todas las empresas (superadmin)
            BuildCoRoutesSubpanel();                      // rutas que afectan a la empresa
            BuildCatalogSubpanel();                       // catálogo global de rutas (superadmin)
            foreach (var pnl in new[] { _svcPanel, _bankPanel, _memberPanel, _tariffPanel, _rankPanel, _soloPanel, _reviewPanel, _usersPanel, _allCompPanel, _fleetPanel, _buyPanel, _paPanel, _chatPanel, _rulesPanel, _loansAdminPanel, _coRoutesPanel, _catPanel }) { pnl.Dock = DockStyle.Fill; pnl.Visible = false; host.Controls.Add(pnl); }

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

        // BANCA: «Resumen» (tarjeta de tesorería, indicadores, en qué se va el dinero, meses) y «Extracto»
        // (por días, con el saldo tras cada movimiento). El periodo (este mes · 6 meses · todo) vale para ambas.
        Panel BuildBankSubpanel()
        {
            var outer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

            _bankSummary = new BankSummary { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var sm = _bankSummary;
            sm.LblTreasury = Tr("TESORERÍA"); sm.LblAvail = Tr("Saldo disponible"); sm.LblHolder = Tr("Titular"); sm.LblMonth = Tr("Movimientos del mes");
            sm.LblIncome = Tr("INGRESOS"); sm.LblExpense = Tr("GASTOS"); sm.LblResult = Tr("RESULTADO"); sm.LblBest = Tr("MEJOR MES");
            sm.LblIncomeSub = Tr("servicios y premios"); sm.LblExpenseSub = Tr("flota, vía, energía y salarios"); sm.LblMargin = Tr("margen del {0} %");
            sm.LblCosts = Tr("EN QUÉ SE VA EL DINERO"); sm.LblMonths = Tr("INGRESOS Y GASTOS POR MES"); sm.LblInc = Tr("Ingresos"); sm.LblExp = Tr("Gastos"); sm.LblNoData = Tr("Sin datos todavía");
            var res = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 4, 0, 0) };
            res.Controls.Add(_bankSummary);

            // --- Extracto ---
            var mov = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg, Visible = false };
            mov.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            mov.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // conceptos + búsqueda
            mov.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // extracto
            mov.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // borrar (superadmin)
            var frow = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 40, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 4) };
            frow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            frow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            frow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _bankChips = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0) };
            string[] chips = { "Todo", "Ingresos", "Cánon AI", "Energía", "Salarios", "Flota", "Otros", "Préstamos" };
            for (int k = 0; k < chips.Length; k++)
            {
                int idx = k;
                var b = BankChip(Tr(chips[k]), k == 0);
                b.Click += (s, e) => { _bankConcept = idx; SetChipActive(_bankChips, idx); FillStatement(); };
                _bankChips.Controls.Add(b);
            }
            var search = new RoundedInput(I18n.T("🔎  Filtrar…")) { Width = 240, Height = 34, Anchor = AnchorStyles.Right, Margin = new Padding(10, 3, 0, 3) };
            search.Box.TextChanged += (s, e) => { _bankSearch = search.Box.Text.Trim(); FillStatement(); };
            frow.Controls.Add(_bankChips, 0, 0); frow.Controls.Add(search, 1, 0);
            mov.Controls.Add(frow);
            _bankStmt = new BankStatement { Dock = DockStyle.Fill, Margin = new Padding(2, 2, 2, 4), LblBalance = Tr("saldo {0}") };
            _bankStmt.DayTitle = BankDayTitle;
            mov.Controls.Add(_bankStmt);
            _ledgerDelBtn = EmpButton(Tr("Eliminar movimiento"));
            _ledgerDelBtn.Width = 220; _ledgerDelBtn.BaseColor = Theme.Surface2; _ledgerDelBtn.HoverColor = Color.FromArgb(150, 60, 60); _ledgerDelBtn.TextColor = RedC; _ledgerDelBtn.Visible = false;
            _ledgerDelBtn.Click += (s, e) => DeleteLedgerRow();
            mov.Controls.Add(_ledgerDelBtn);

            var pages = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var loans = BuildLoansPage();
            pages.Controls.Add(loans); pages.Controls.Add(mov); pages.Controls.Add(res);

            // Pestañas a la izquierda, periodo a la derecha.
            var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 40, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            FlowLayoutPanel periodBar = null;
            var tabs = MakeSubTabs(new[] { "Resumen", "Extracto", "Préstamos" }, i =>
            {
                res.Visible = i == 0; mov.Visible = i == 1; loans.Visible = i == 2;
                if (periodBar != null) periodBar.Visible = i != 2;
                if (i == 2) { UpdateLoanButtons(); LoadLoans(); }
            });
            tabs.Dock = DockStyle.Fill; tabs.Margin = new Padding(0);
            var period = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(8, 2, 0, 0), Anchor = AnchorStyles.Right };
            string[] pnames = { "Este mes", "6 meses", "Todo" };
            for (int k = 0; k < pnames.Length; k++)
            {
                int idx = k;
                var b = BankChip(Tr(pnames[k]), k == _bankPeriod);
                b.Click += (s, e) => { _bankPeriod = idx; SetChipActive(period, idx); RefreshBank(); };
                period.Controls.Add(b);
            }
            periodBar = period;
            top.Controls.Add(tabs, 0, 0); top.Controls.Add(period, 1, 0);
            outer.Controls.Add(pages); outer.Controls.Add(top);

            _bankMsg = EmpMsg(); // no se muestra por espacio, pero se reutiliza
            return outer;
        }

        RoundButton BankChip(string text, bool active)
        {
            var b = new RoundButton { Text = text, Height = 30, Radius = 15, FontSize = 9f, FontStyle = FontStyle.Bold, Margin = new Padding(0, 4, 6, 4), Active = active, BaseColor = Theme.Surface, HoverColor = Theme.SurfaceHi, TextColor = Theme.Subtle };
            using var f = Theme.Font(9f, FontStyle.Bold);
            b.Width = TextRenderer.MeasureText(text, f).Width + 28;
            return b;
        }

        static void SetChipActive(Control bar, int idx)
        {
            for (int k = 0; k < bar.Controls.Count; k++) if (bar.Controls[k] is RoundButton b) { b.Active = k == idx; b.Invalidate(); }
        }

        string BankDayTitle(DateTime d)
        {
            var today = DateTime.Today;
            var ci = System.Globalization.CultureInfo.GetCultureInfo(I18n.English ? "en-GB" : "es-ES");
            string full = d.ToString(I18n.English ? "dddd d MMMM yyyy" : "dddd, d 'de' MMMM 'de' yyyy", ci);
            if (d == today) return Tr("HOY") + " · " + full.ToUpper(ci);
            if (d == today.AddDays(-1)) return Tr("AYER") + " · " + full.ToUpper(ci);
            return full.ToUpper(ci);
        }

        // SERVICIOS: sub-pestañas de ESTADO + búsqueda a la derecha; tarjetas a todo el alto.
        Panel BuildSvcSubpanel()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // pestañas + búsqueda
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // tarjetas
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // acciones

            _svcCards = new ServiceCardList { Dock = DockStyle.Fill, Margin = new Padding(2, 4, 2, 4) };
            _svcCards.FileLoader = id => ServiceImages.GetAsync(_empSel?.Id, id);
            _svcCards.ItemActivated += id => OpenServiceDetail(_svcRows.FindIndex(r => r.Id == id));

            var top = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 42, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 6) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            // Los viajes fallidos o no válidos ya no se guardan (el servidor los borra al cerrarlos).
            var tabs = MakeSubTabs(new[] { "Todos", "En conducción", "Completados" }, i => _svcCards.StatusFilter = i);
            tabs.Dock = DockStyle.Fill; tabs.Margin = new Padding(0);
            _svcTabs = tabs;
            var search = new RoundedInput(I18n.T("🔎  Filtrar…")) { Width = 260, Height = 34, Anchor = AnchorStyles.Right, Margin = new Padding(10, 3, 0, 3) };
            search.Box.TextChanged += (s, e) => _svcCards.Filter(search.Box.Text);
            top.Controls.Add(tabs, 0, 0); top.Controls.Add(search, 1, 0);
            t.Controls.Add(top);
            // Lista y, a la derecha, el calendario (un clic en un día lleva la lista a ese día).
            var mid = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(0) };
            var calHost = new Panel { Dock = DockStyle.Right, Width = Theme.Px(250), BackColor = Theme.Bg, Padding = new Padding(Theme.Px(10), Theme.Px(4), 0, 0) };
            _svcCal = new ServiceCalendar { Dock = DockStyle.Top };
            calHost.Controls.Add(_svcCal);
            calHost.Resize += (s, e) => _svcCal.Height = _svcCal.NeededHeight(Math.Max(100, calHost.ClientSize.Width - calHost.Padding.Horizontal));
            _svcCal.DayClicked += d => _svcCards.ScrollToDay(d);
            _svcCards.DaysChanged += () => _svcCal.SetDays(_svcCards.DayTotals);
            _svcCards.TopDayChanged += d => _svcCal.SetCurrent(d);
            _svcCards.LblAnnulled = Tr("ANULADO");
            mid.Controls.Add(_svcCards);
            mid.Controls.Add(calHost);
            t.Controls.Add(mid);

            // Acciones: ver detalle (todos) + borrar servicio (solo superadmin).
            var svcBtns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0) };
            var detailBtn = EmpButton(Tr("Ver detalle del servicio"), primary: true); detailBtn.Width = 230;
            detailBtn.Click += (s, e) => OpenServiceDetail(SelectedServiceIndex());
            svcBtns.Controls.Add(detailBtn);
            _svcDelBtn = EmpButton(Tr("Eliminar servicio"));
            _svcDelBtn.Width = 200; _svcDelBtn.Margin = new Padding(8, 10, 2, 2); _svcDelBtn.BaseColor = Theme.Surface2; _svcDelBtn.HoverColor = Color.FromArgb(150, 60, 60); _svcDelBtn.TextColor = RedC; _svcDelBtn.Visible = false;
            _svcDelBtn.Click += (s, e) => DeleteServiceRow();
            // Superadmin: anular un servicio registrado (se queda en el historial con el motivo).
            _svcAnnulBtn = EmpButton(Tr("Anular servicio…"));
            _svcAnnulBtn.Width = 200; _svcAnnulBtn.Margin = new Padding(8, 10, 2, 2); _svcAnnulBtn.BaseColor = Theme.Surface2; _svcAnnulBtn.HoverColor = Color.FromArgb(150, 60, 60); _svcAnnulBtn.TextColor = RedC; _svcAnnulBtn.Visible = false;
            _svcAnnulBtn.Click += (s, e) => AnnulServiceRow();
            svcBtns.Controls.Add(_svcAnnulBtn);
            svcBtns.Controls.Add(_svcDelBtn);
            t.Controls.Add(svcBtns);
            return t;
        }

        // Servicio elegido (índice en _svcRows; −1 = ninguno).
        int SelectedServiceIndex() => _svcCards == null ? -1 : _svcRows.FindIndex(r => r.Id == _svcCards.SelectedId);

        // Vacía la lista (cerrar sesión, sin empresas).
        void ClearServices()
        {
            _svcRows.Clear(); _svcLastJson = _svcLastTrenJson = null; _svcCardsCompany = null; _svcStats = null;
            _svcCards?.SetItems(Array.Empty<ServiceCardList.Item>());
            UpdateServiceTabCounts();
        }

        // Pestañas de Servicios con cuántos hay en cada una: «Todos  17», «En conducción  1»…
        void UpdateServiceTabCounts()
        {
            if (_svcTabs == null) return;
            string[] names = { "Todos", "En conducción", "Completados" };
            using var f = Theme.Font(9.75f, FontStyle.Bold);
            for (int k = 0; k < names.Length && k < _svcTabs.Controls.Count; k++)
            {
                if (_svcTabs.Controls[k] is not RoundButton b) continue;
                long n = System.Linq.Enumerable.Count(_svcRows, r => k == 0 || (k == 1 ? r.Status == "open" : r.Status != "open"));
                if (_svcStats is { } st) n = k == 0 ? st.total : k == 1 ? st.open : st.total - st.open;   // los del servidor, sin límite
                b.Text = Tr(names[k]) + (_svcRows.Count > 0 || n > 0 ? "  " + n.ToString("N0", EsEs) : "");
                b.Width = TextRenderer.MeasureText(b.Text, f).Width + 34;
                b.Invalidate();
            }
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
            _memberCards = new MemberCardGrid
            {
                Dock = DockStyle.Fill, Margin = new Padding(2, 4, 2, 4),
                LblLicense = Tr("Carné"), LblServices = Tr("servicios"), LblKm = Tr("en la empresa"), LblNet = Tr("neto"), LblSuspended = Tr("SUSPENDIDO")
            };
            _memberCards.SelectionChanged += () =>
            {
                int i = MemberSelectedIndex();   // el desplegable de rol se pone en el del socio elegido
                if (i >= 0 && _memberRole != null) _memberRole.SelectedIndex = _members[i].Role == "owner" ? 2 : _members[i].Role == "manager" ? 1 : 0;
            };
            _memberSearch = new RoundedInput(I18n.T("🔎  Filtrar…")) { Anchor = AnchorStyles.Left, Width = 300, Height = 34, Margin = new Padding(2, 2, 8, 4) };
            _memberSearch.Box.TextChanged += (s, e) => FillMemberCards();
            pMem.Controls.Add(_memberSearch);
            pMem.Controls.Add(_memberCards);
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
            // Una tarjeta por solicitud: quién, cuándo y su mensaje.
            _joinList = EmpCards(); _joinList.Margin = new Padding(2);
            _joinList.TitleCol = 0; _joinList.SubCols = new[] { 2 }; _joinList.QuoteCol = 1;
            _joinList.Formats[2] = Tr("Solicitud del {0}");
            _joinList.CardHeight = 80; _joinList.MinWidth = 420; _joinList.Columns = 3;
            pJoin.Controls.Add(_joinList);
            var jbtns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(2, 0, 2, 0) };
            var jAcc = EmpButton(Tr("Aceptar"), primary: true); jAcc.Width = 150; jAcc.Margin = new Padding(0, 10, 8, 2); jAcc.Click += (s, e) => ApproveJoin();
            var jRej = EmpButton(Tr("Rechazar")); jRej.Width = 150; jRej.Margin = new Padding(0, 10, 2, 2); jRej.Click += (s, e) => RejectJoin();
            jbtns.Controls.Add(jAcc); jbtns.Controls.Add(jRej);
            pJoin.Controls.Add(jbtns);

            var pages = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var pStats = BuildStatsPage();   // estadísticas por maquinista
            pages.Controls.Add(pStats); pages.Controls.Add(pJoin); pages.Controls.Add(pAdd); pages.Controls.Add(pMem);
            var tabs = MakeSubTabs(new[] { "Socios", "Estadísticas", "Añadir socio", "Solicitudes de ingreso" },
                i => { pMem.Visible = i == 0; pStats.Visible = i == 1; pAdd.Visible = i == 2; pJoin.Visible = i == 3; if (i == 1) LoadStats(); });
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
            pTar.Controls.Add(EmpHeader("TARIFAS GLOBALES DE LOS SERVICIOS (€) — SOLO ADMINISTRADOR"));
            pTar.Controls.Add(EmpFieldLabel(Tr("Ingreso por km (mercancías de 500 t)")));
            _tarIncome = EmpInput("8"); _tarIncome.Width = 240; pTar.Controls.Add(_tarIncome);
            pTar.Controls.Add(EmpFieldLabel(Tr("Cánon AI (administrador de infraestructuras) por km")));
            _tarCanon = EmpInput("3"); _tarCanon.Width = 240; pTar.Controls.Add(_tarCanon);
            pTar.Controls.Add(EmpFieldLabel(Tr("Energía / combustible por km (tren de 500 t; escala con la masa)")));
            _tarEnergy = EmpInput("1.5"); _tarEnergy.Width = 240; pTar.Controls.Add(_tarEnergy);
            // Salario = fijo por servicio + horas de conducción × precio por hora (con tope; salario-por-tiempo.sql).
            pTar.Controls.Add(EmpFieldLabel(Tr("Salario del maquinista: fijo por servicio")));
            _tarSalary = EmpInput("30"); _tarSalary.Width = 240; pTar.Controls.Add(_tarSalary);
            pTar.Controls.Add(EmpFieldLabel(Tr("Salario del maquinista: por hora de conducción")));
            _tarSalaryHour = EmpInput("25"); _tarSalaryHour.Width = 240; pTar.Controls.Add(_tarSalaryHour);
            pTar.Controls.Add(EmpFieldLabel(Tr("Horas de conducción pagadas como máximo por servicio")));
            _tarSalaryMaxH = EmpInput("6"); _tarSalaryMaxH.Width = 240; pTar.Controls.Add(_tarSalaryMaxH);
            _tarSaveBtn = EmpButton(Tr("Guardar tarifas"), primary: true); _tarSaveBtn.Width = 200;
            _tarSaveBtn.Click += (s, e) => SaveTariffs();
            pTar.Controls.Add(_tarSaveBtn);

            // --- Saldos (empresa + saldo inicial global) ---
            var pBal = FormPage(false);
            _tarBalanceRow = new Panel { AutoSize = true, BackColor = Theme.Bg, Margin = new Padding(0) };
            var brInner = new TableLayoutPanel { AutoSize = true, ColumnCount = 1 };
            brInner.Controls.Add(EmpHeader("SALDO DE LA EMPRESA (ADMINISTRADOR)"));
            brInner.Controls.Add(EmpFieldLabel(Tr("Fijar saldo (€) — solo el administrador")));
            _tarBalance = EmpInput("0"); _tarBalance.Width = 240; brInner.Controls.Add(_tarBalance);
            _tarBalanceBtn = EmpButton(Tr("Fijar saldo"), primary: true); _tarBalanceBtn.Width = 200;
            _tarBalanceBtn.Click += (s, e) => AdminSetBalance();
            brInner.Controls.Add(_tarBalanceBtn);
            _tarBalanceRow.Controls.Add(brInner);
            pBal.Controls.Add(_tarBalanceRow);
            _defBalanceRow = new Panel { AutoSize = true, BackColor = Theme.Bg, Margin = new Padding(0, 12, 0, 0) };
            var dbInner = new TableLayoutPanel { AutoSize = true, ColumnCount = 1 };
            dbInner.Controls.Add(EmpHeader("SALDO INICIAL DE EMPRESAS NUEVAS (GLOBAL, ADMINISTRADOR)"));
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
            fsInner.Controls.Add(EmpHeader("ECONOMÍA DE FLOTA (GLOBAL, ADMINISTRADOR)"));
            fsInner.Controls.Add(EmpFieldLabel(Tr("Escala de precio (1 = precios reales en millones)")));
            _fsScale = EmpInput("1"); _fsScale.Width = 240; fsInner.Controls.Add(_fsScale);
            fsInner.Controls.Add(EmpFieldLabel(Tr("Alquiler por servicio (fracción del valor, p. ej. 0,00008)")));
            _fsRentPct = EmpInput("0,00008"); _fsRentPct.Width = 240; fsInner.Controls.Add(_fsRentPct);
            fsInner.Controls.Add(EmpFieldLabel(Tr("Mantenimiento por taller (fracción del valor, p. ej. 0,0015)")));
            _fsMaintPct = EmpInput("0,0015"); _fsMaintPct.Width = 240; fsInner.Controls.Add(_fsMaintPct);
            _fsFareLbl = EmpFieldLabel(FareFormulaText(0.02, 0.005));   // con los coeficientes del servidor al cargar (LoadFleetSettings)
            fsInner.Controls.Add(_fsFareLbl);
            _fsFareBase = EmpInput("1,5"); _fsFareBase.Width = 240; fsInner.Controls.Add(_fsFareBase);
            fsInner.Controls.Add(EmpFieldLabel(Tr("Billete por km (€ por viajero y km a bordo, p. ej. 0,08)")));
            _fsFareKm = EmpInput("0,08"); _fsFareKm.Width = 240; fsInner.Controls.Add(_fsFareKm);
            _fsSaveBtn = EmpButton(Tr("Guardar economía de flota"), primary: true); _fsSaveBtn.Width = 240;
            _fsSaveBtn.Click += (s, e) => SaveFleetSettings();
            fsInner.Controls.Add(_fsSaveBtn);
            fsInner.Controls.Add(BuildTrainModeRow());   // trenes de empresa: no se comprueba / aviso / obligatorio
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
            L.Controls.Add(EmpHeader("MODELO DE VIAJEROS (GLOBAL, ADMINISTRADOR)"));
            L.Controls.Add(EmpNote(Tr("Viajeros que esperan = plazas del tren × % base × tamaño de estación × tipo de servicio × hora × estación del año × clima × variación aleatoria. Nunca suben más viajeros que plazas libres.")));
            L.Controls.Add(EmpFieldLabel(Tr("Viajeros que esperan en una estación de tamaño ×1 (% de las plazas del tren)")));
            _pmCapPct = EmpInput("12"); _pmCapPct.Anchor = AnchorStyles.Left; _pmCapPct.Width = 200; L.Controls.Add(_pmCapPct);
            // La antigua demanda base fija ya no se enseña; el campo se conserva para guardar la economía de flota tal cual.
            _fsPaxDemand = EmpInput("60");
            var profCols = new[] { "Cercanías", "Media", "Larga", "Alta Vel." };
            var profGrid = ParamGrid(profCols, new[]
            {
                ("Demanda relativa (× base)", _pmDemand = new RoundedInput[4]),
                ("Fracción que baja en cada parada", _pmAlight = new RoundedInput[4]),
            });
            L.Controls.Add(EmpHeader("POR TIPO DE SERVICIO"));
            L.Controls.Add(profGrid);
            L.Controls.Add(EmpHeader("POR TAMAÑO DE ESTACIÓN (METROS DE ANDÉN)"));
            L.Controls.Add(ParamGrid(new[] { "< 250 m", "250–600 m", "600–1.400 m", "≥ 1.400 m" }, new[] { ("Peso de la estación (× base)", _pmStation = new RoundedInput[4]) }));

            R.Controls.Add(EmpHeader("OTROS FACTORES"));
            R.Controls.Add(EmpNote(Tr("Bajada extra: multiplica la fracción que baja en estaciones de 600 m de andén o más. Variación: ± aleatorio por estación y visita. Reembarque: distancia a la que hay que alejarse para volver a atender la misma estación.")));
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
            pCls.Controls.Add(EmpHeader("CLASIFICACIÓN DEL TIPO DE SERVICIO (GLOBAL, ADMINISTRADOR)"));
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
            pUpd.Controls.Add(EmpHeader("PUBLICAR ACTUALIZACIÓN (ADMINISTRADOR)"));
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

            var pLiga = BuildLeagueSettingsPage();
            var pages = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            pages.Controls.Add(pUpd); pages.Controls.Add(pLiga); pages.Controls.Add(pCls); pages.Controls.Add(pPax); pages.Controls.Add(pFs); pages.Controls.Add(pBal); pages.Controls.Add(pTar);
            var tabs = MakeSubTabs(new[] { "Tarifas", "Saldos", "Economía de flota", "Viajeros", "Clasificación", "Liga", "Actualizaciones" },
                i =>
                {
                    pTar.Visible = i == 0; pBal.Visible = i == 1; pFs.Visible = i == 2; pPax.Visible = i == 3; pCls.Visible = i == 4; pLiga.Visible = i == 5; pUpd.Visible = i == 6;
                    if (i == 5) LoadLeagueSettings();
                    if (i == 6) RefreshUpdateInfo();
                });
            FillPaxModelInputs(PaxCfg);
            _tariffMsg = EmpMsg(); _tariffMsg.Dock = DockStyle.Bottom;
            outer.Controls.Add(pages); outer.Controls.Add(tabs); outer.Controls.Add(_tariffMsg);
            return outer;
        }

        // Ranking: Liga del mes · Campeones · Empresas (ranking público por km) · Maquinistas (de esta empresa).
        Panel BuildRankSubpanel()
        {
            _rankCompanies = new PodiumBoard { Dock = DockStyle.Fill, MineTag = Tr("tu empresa"), EmptyText = Tr("Cargando…") };
            _rankDrivers = new PodiumBoard { Dock = DockStyle.Fill, MineTag = Tr("tú"), EmptyText = Tr("Cargando…") };
            return WrapRankingTabs(_rankCompanies, _rankDrivers);
        }

        Panel BuildProfileSubpanel()
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true };
            Native.UseDarkScrollBars(host);   // barra de scroll oscura acorde al tema
            var t = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 7, BackColor = Theme.Bg, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // carnet + carné por puntos
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // KPIs
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // header insignias
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // insignias
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // trenes y rutas
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // header historial del carné
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // historial del carné (línea de tiempo)

            // Anchura del contenido = ancho del host (menos margen para la barra de scroll)
            void FitWidth() { t.Width = Math.Max(10, host.ClientSize.Width - 4); }
            host.Resize += (s, e) => FitWidth();
            host.HandleCreated += (s, e) => FitWidth();
            host.VisibleChanged += (s, e) => { if (host.Visible) FitWidth(); };   // al abrir la pestaña

            // Arriba: el carnet de maquinista (rango) y, al lado, el carné por puntos
            var heroRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
            heroRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            heroRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            _profHero = new ProfileHero { Dock = DockStyle.Top, Margin = new Padding(0, 0, 10, 0), Caption = Tr("CARNÉ DE MAQUINISTA · SELECTOR"),
                                          LblAddPhoto = "📷 " + Tr("Añadir foto"), LblChangePhoto = "📷 " + Tr("Cambiar foto") };
            _profHero.PhotoClicked += ShowPhotoMenu;
            heroRow.Controls.Add(_profHero, 0, 0);
            var lic = BuildLicenseCard(); lic.Dock = DockStyle.Fill; lic.Margin = new Padding(0); lic.MinimumSize = new Size(0, _profHero.Height);
            heroRow.Controls.Add(lic, 1, 0);
            t.Controls.Add(heroRow);
            // (la antigua tarjeta de rango: sus etiquetas siguen existiendo pero no se enseñan)
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

            // KPIs
            var kpis = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 4) };
            kpis.Controls.Add(KpiTile(Tr("KM TOTALES"), out _profKmVal, "📏", Theme.Accent));
            kpis.Controls.Add(KpiTile(Tr("VIAJES"), out _profTripsVal, "🚂", ColBlue));
            kpis.Controls.Add(KpiTile(Tr("TIEMPO TOTAL"), out _profTimeVal, "⏱", ColViolet));
            kpis.Controls.Add(KpiTile(Tr("VEL. MEDIA"), out _profSpeedVal, "🚄", ColOrange));
            kpis.Controls.Add(KpiTile(Tr("VIAJEROS"), out _profPaxVal, "🧍", ColTeal));
            t.Controls.Add(kpis);

            _badgesHeader = EmpHeader("INSIGNIAS");
            t.Controls.Add(_badgesHeader);
            _badgeGrid = new BadgeGrid { Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 10) };
            t.Controls.Add(_badgeGrid);

            // Trenes (con su vista 2D) y rutas, a dos columnas
            var tr2 = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
            tr2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tr2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _thumbs ??= new VehicleThumbs(this);
            _profTrains = new TrainTopList { Dock = DockStyle.Top, Margin = new Padding(0, 0, 7, 0), Title = Tr("TRENES MÁS UTILIZADOS"), Thumbs = _thumbs };
            _profRoutes = new RouteBars { Dock = DockStyle.Top, Margin = new Padding(7, 0, 0, 0), Title = Tr("RUTAS MÁS RECORRIDAS") };
            tr2.Controls.Add(_profTrains, 0, 0); tr2.Controls.Add(_profRoutes, 1, 0);
            t.Controls.Add(tr2);

            t.Controls.Add(EmpHeader("HISTORIAL DEL CARNÉ"));
            var hist = BuildLicenseHistory(); hist.Margin = new Padding(2, 2, 2, 8);
            t.Controls.Add(hist);

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
            var __vis = Perf.T("ShowSubtab " + i + " · paneles");
            _empSubtab = i;
            for (int k = 0; k < _empSubtabs.Length; k++) { if (_empSubtabs[k] == null) continue; _empSubtabs[k].Active = k == i; _empSubtabs[k].Invalidate(); }
            if (_empSectionTitle != null && i >= 0 && i < SubNames.Length) _empSectionTitle.Text = Tr(SubNames[i]);
            // Primero se ocultan las demás y después se enseña la elegida (sin volver a maquetarla si no hace falta).
            if (_svcPanel != null && !(i == 0)) _svcPanel.Visible = false;
            if (_bankPanel != null && !(i == 1)) _bankPanel.Visible = false;
            if (_memberPanel != null && !(i == 2)) _memberPanel.Visible = false;
            if (_tariffPanel != null && !(i == 3)) _tariffPanel.Visible = false;
            if (_rankPanel != null && !(i == 4)) _rankPanel.Visible = false;
            if (_soloPanel != null && !(i == 5)) _soloPanel.Visible = false;
            if (_liveShareBox != null && !(i == 5)) _liveShareBox.Visible = false;
            if (_reviewPanel != null && !(i == 6)) _reviewPanel.Visible = false;
            if (_usersPanel != null && !(i == 7)) _usersPanel.Visible = false;
            if (_allCompPanel != null && !(i == 8)) _allCompPanel.Visible = false;
            if (_fleetPanel != null && !(i == 9)) _fleetPanel.Visible = false;
            if (_buyPanel != null && !(i == 10)) _buyPanel.Visible = false;
            if (_paPanel != null && !(i == 11)) _paPanel.Visible = false;
            if (_chatPanel != null && !(i == ChatSubtab)) _chatPanel.Visible = false;
            if (_rulesPanel != null && !(i == NormasSubtab)) _rulesPanel.Visible = false;
            if (_loansAdminPanel != null && !(i == PrestamosSubtab)) _loansAdminPanel.Visible = false;
            if (_coRoutesPanel != null && !(i == RutasSubtab)) _coRoutesPanel.Visible = false;
            if (_catPanel != null && !(i == CatalogoSubtab)) _catPanel.Visible = false;
            ApplyCompanyKpisVisibility();   // el hueco ya con su tamaño definitivo (la tira se rellena al final)
            _pageEmpresas?.PerformLayout();
            if (i == 0) ShowFast(_svcPanel, true);
            if (i == 1) ShowFast(_bankPanel, true);
            if (i == 2) ShowFast(_memberPanel, true);
            if (i == 3) ShowFast(_tariffPanel, true);
            if (i == 4) ShowFast(_rankPanel, true);
            if (i == 5) ShowFast(_soloPanel, true);
            if (i == 5) ShowFast(_liveShareBox, true);
            if (i == 6) ShowFast(_reviewPanel, true);
            if (i == 7) ShowFast(_usersPanel, true);
            if (i == 8) ShowFast(_allCompPanel, true);
            if (i == 9) ShowFast(_fleetPanel, true);
            if (i == 10) ShowFast(_buyPanel, true);
            if (i == 11) ShowFast(_paPanel, true);
            if (i == ChatSubtab) ShowFast(_chatPanel, true);
            if (i == NormasSubtab) ShowFast(_rulesPanel, true);
            if (i == PrestamosSubtab) ShowFast(_loansAdminPanel, true);
            if (i == RutasSubtab) ShowFast(_coRoutesPanel, true);
            if (i == CatalogoSubtab) ShowFast(_catPanel, true);
            __vis.Dispose();
            var __load = Perf.T("ShowSubtab " + i + " · carga");
            // Cada subpestaña recarga sus datos al abrirse (ya no hay botón "Actualizar"); en la precarga visual de la
            // pantalla de inicio, no (los datos ya los ha traído PreloadEmpresas).
            if (_warmingEmp) { __load.Dispose(); return; }
            if (i == 0 && _empSel != null) LoadServices(_empSel);
            if (i == 1) OnBankShown();
            if (i == 2 && _empSel != null) LoadMembers(_empSel);
            if (i == 3) { FillTariffFields(); LoadDefaultBalance(); LoadFleetSettings(); LoadFarePerKm(); }
            if (i == 4) LoadRankTab();
            if (i == 5) LoadProfile();
            if (i == 6) LoadReview();
            if (i == 7) LoadUsers();
            if (i == 8) LoadAllCompanies();
            if (i == 9 || i == 10) LoadFleet();   // Flota y Compra comparten los mismos datos (vehicles)
            if (i == 10) LoadPurchaseRequests();
            if (i == 11) OnMegafoniaShown();
            if (i == ChatSubtab) OnChatShown();
            if (i == NormasSubtab) { UpdateRuleButtons(); LoadRules(); }
            if (i == PrestamosSubtab) LoadLoansAdmin();
            if (i == RutasSubtab) LoadCoRoutes();
            if (i == CatalogoSubtab) LoadCatalog();
            __load.Dispose();
            using (Perf.T("ShowSubtab " + i + " · kpis"))
            UpdateCompanyKpis();                  // la tira de la empresa se muestra u oculta según la sección
        }

        // ============================ Lógica ============================
        void OnEmpresasShown()
        {
            RefreshEmpresasView();
            AjustarNav();   // al hacerse visible ya se conoce el alto real del menú lateral
            if (_warmingEmp) return;   // precarga visual: sin pedir nada al servidor
            if (Supa.IsSuperadmin) { LoadReview(onlyCount: true); LoadLoansAdmin(onlyCount: true); LoadCatalog(onlyCount: true); }   // «Revisión (n)», «Préstamos (n)» y «Catálogo de rutas (n)»
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
                StartNotifications();   // avisos emergentes (solicitudes, compras, roles…)
                if (!_routeStatesAsked) { _routeStatesAsked = true; _ = RefreshLocalRouteStatesAsync(); }   // distintivos de las rutas
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
            UpdateRailAvatar();
            return Supa.DisplayName + (Supa.IsSuperadmin ? "  ★" : "");
        }

        // Foto (o iniciales) del usuario en la tarjeta de abajo del menú lateral.
        void UpdateRailAvatar()
        {
            if (_railAvatar == null) return;
            _railAvatar.NameText = Supa.DisplayName ?? "";
            _railAvatar.Photo = !string.IsNullOrEmpty(Supa.UserId) && _memberPhotos.TryGetValue(Supa.UserId, out var p) ? p.img : null;
            _railAvatar.Invalidate();
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

        // ---------------------------------------------------------------- Mi cuenta
        void OpenAccountMenu(Control anchor)
        {
            if (!Supa.IsLoggedIn) return;
            var cm = MenuStyle.Apply(new ContextMenuStrip() { ShowImageMargin = false, ShowCheckMargin = true });
            cm.Items.Add(Tr("✎  Editar nombre"), null, (s, e) => EditMaquinistaName());
            cm.Items.Add(Tr("🔑  Cambiar contraseña"), null, (s, e) => ChangeMyPassword());
            cm.Items.Add(Tr("🗝  Clave de recuperación"), null, (s, e) => ShowMyRecoveryKey());
            var snd = new ToolStripMenuItem(Tr("🔔  Sonido de avisos y del chat")) { Checked = _prefs?.NotifySound != false, CheckOnClick = true };
            snd.CheckedChanged += (s, e) =>
            {
                if (_prefs == null) return;
                _prefs.NotifySound = snd.Checked;
                try { _prefs.Save(); } catch { }
                if (snd.Checked) NotifySound.Play(force: true);   // para oír cómo suena
            };
            cm.Items.Add(snd);
            cm.Items.Add(new ToolStripSeparator());
            var del = new ToolStripMenuItem(Tr("Eliminar mi cuenta…")) { ForeColor = Color.FromArgb(200, 60, 60) };
            del.Click += (s, e) => DeleteMyAccount();
            cm.Items.Add(del);
            cm.Show(anchor, new Point(0, anchor.Height));
        }

        // Cómo te conoce la cuenta: tu nombre de acceso (o tu correo, en cuentas antiguas).
        string AccountLabel()
        {
            string email = Supa.Email ?? "";
            if (email.EndsWith("@" + SyntheticEmailDomain, StringComparison.OrdinalIgnoreCase)) return "«" + email.Substring(0, email.IndexOf('@')) + "»";
            return string.IsNullOrEmpty(Supa.Username) ? email : "«" + Supa.Username + "»";
        }

        // Cambiar la contraseña: se comprueba la actual (iniciando sesión con ella) y se pone la nueva.
        void ChangeMyPassword()
        {
            if (!Supa.IsLoggedIn) return;
            using var dlg = new ChangePasswordDialog(AccountLabel(), async (cur, nueva) =>
            {
                var e1 = await Supa.SignInAsync(Supa.Email, cur);
                if (e1 != null)
                    return e1.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0 || e1.IndexOf("credentials", StringComparison.OrdinalIgnoreCase) >= 0
                        ? Tr("La contraseña actual no es correcta.") : Tr("No se pudo comprobar la contraseña: ") + e1;
                var e2 = await Supa.UpdatePasswordAsync(nueva);
                if (e2 != null)
                    return e2.IndexOf("different from the old", StringComparison.OrdinalIgnoreCase) >= 0
                        ? Tr("La contraseña nueva es igual que la actual.") : Tr("No se pudo cambiar la contraseña: ") + e2;
                // Si SelectOR recuerda la contraseña, se recuerda la nueva.
                if (_prefs.RememberPassword) { _prefs.EmpresasPasswordEnc = Native.Protect(nueva); try { _prefs.Save(); } catch { } }
                return null;
            });
            if (dlg.ShowDialog(this) == DialogResult.OK) Msg(_empHomeMsg, Tr("Contraseña cambiada."), false);
        }

        // Eliminar la cuenta: lo hace el servidor (delete_my_account, comprueba la contraseña).
        void DeleteMyAccount()
        {
            if (!Supa.IsLoggedIn) return;
            if (_pendingServiceId != null) { Msg(_empHomeMsg, Tr("Termina o registra tu servicio antes de eliminar la cuenta."), true); return; }
            using var dlg = new DeleteAccountDialog(AccountLabel(), async pw =>
            {
                var (_, err) = await Supa.RpcAsync("delete_my_account", new { p_password = pw });
                if (err == null) return null;
                return ChatMissing(err) ? Tr("El servidor aún no permite eliminar cuentas (falta cuenta-usuario.sql).") : ChatErr(err);
            });
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            DoSignOut();
            _prefs.EmpresasEmail = null; _prefs.FavoriteCompany = null;
            try { _prefs.Save(); } catch { }
            if (_empEmail != null) _empEmail.Box.Text = "";
            FancyDialog.Info(this, Tr("Cuenta eliminada"), Tr("Tu cuenta se ha eliminado. Puedes seguir usando SelectOR sin cuenta o crear otra cuando quieras."), "👋");
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
                Msg(_empAuthMsg, taken ? Tr("Ese nombre de usuario ya existe. Elige otro.")
                                 : IsOneAccountPerIpError(err) ? Tr("Ya se ha creado una cuenta desde esta conexión a Internet. Solo se permite una cuenta por conexión.")
                                 : (Tr("No se pudo registrar: ") + err), true);
                return;
            }
            SaveRememberedCreds(user, pass);
            if (Supa.IsLoggedIn) { _empLoaded = false; Msg(_empAuthMsg, "", false); RefreshEmpresasView(); LoadCompanies(); }
            else Msg(_empAuthMsg, Tr("Cuenta creada. Inicia sesión con tu nombre de usuario."), false);
        }

        void DoSignOut()
        {
            StopRealtime();
            StopNotifications();
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
            ClearServices();
            _memberCards?.SetMembers(new List<MemberCardGrid.Member>());
            _ledgerAll.Clear(); RefreshBank();
            _svcRows.Clear();
            // Al cerrar sesión ya no perteneces a ninguna empresa → quitar los filtros por empresa
            // de Conducción libre/Horarios (repuebla vacío y oculta) y actualizar KPIs.
            _companyVehNames.Clear();
            RebuildCompanyEngs();
            UpdateCompanyKpis();
            RefreshEmpresasView();
        }

        // soft = refresco en segundo plano (cambios en vivo, servicio registrado): si la lista de empresas
        // no ha cambiado, no se rehace el desplegable ni se «vuelve a elegir» la empresa (eso recargaba y
        // repintaba toda la pantalla); solo se ponen al día sus datos.
        async void LoadCompanies(bool soft = false)
        {
            if (!Supa.IsLoggedIn) return;
            await LoadCompaniesCore(soft);
        }

        async Task LoadCompaniesCore(bool soft = false)
        {
            _empLoaded = true;
            bool first = _empCompanies.Count == 0;
            if (!soft || first) Msg(_empHomeMsg, Tr("Cargando empresas…"), false);
            var (json, err) = await Supa.SelectAsync("companies?select=id,name,logo,balance,income_per_km,canon_per_km,energy_per_km,salary_per_service,pa_enabled,code&order=name.asc");
            if (err != null)   // servidor sin nombres-unicos.sql (sin el ID de empresa)
                (json, err) = await Supa.SelectAsync("companies?select=id,name,logo,balance,income_per_km,canon_per_km,energy_per_km,salary_per_service,pa_enabled&order=name.asc");
            if (err != null)   // por si aún no están las columnas logo/pa_enabled (esquema sin re-ejecutar): reintenta sin ellas
                (json, err) = await Supa.SelectAsync("companies?select=id,name,balance,income_per_km,canon_per_km,energy_per_km,salary_per_service&order=name.asc");
            if (err != null) { Msg(_empHomeMsg, Tr("Error: ") + err, true); return; }
            var prevId = _empSel?.Id;
            var nuevas = ParseCompanies(json);
            bool mismaLista = nuevas.Count == _empCompanies.Count && _empCoCombo.Items.Count == nuevas.Count;
            for (int k = 0; mismaLista && k < nuevas.Count; k++)
                mismaLista = nuevas[k].Id == _empCompanies[k].Id && nuevas[k].Name == _empCompanies[k].Name;
            _empCompanies.Clear();
            _empCompanies.AddRange(nuevas);
            if (!mismaLista)
            {
                _suppressCoSel = true;
                _empCoCombo.BeginUpdate();
                _empCoCombo.Items.Clear();
                foreach (var c in _empCompanies) _empCoCombo.Items.Add(c.Name);
                _empCoCombo.EndUpdate();
                _suppressCoSel = false;
            }
            if (_empCompanies.Count == 0)
            {
                _empSel = null; _myRole = null; ClearServices();
                Msg(_empHomeMsg, Tr("Aún no perteneces a ninguna empresa. Crea una o solicita unirte a una desde la izquierda."), false);
                UpdateCompanyDash();
                UpdateRoleUi();            // recalcula subpestañas (sin empresa → solo Ranking/Mi perfil)
                UpdateEmptyStateUi();
                UpdateDutyHostVisible();
                LoadCompanyVehNames();     // etiqueta de "tren de empresa" (aquí: ninguno)
                return;
            }
            if (!soft || first) Msg(_empHomeMsg, "", false);
            UpdateEmptyStateUi();
            if (!_favLoaded) await LoadFavoriteCompanyAsync();   // la favorita está en el perfil
            int sel = prevId != null ? _empCompanies.FindIndex(c => c.Id == prevId) : -1;
            if (sel < 0 && !string.IsNullOrEmpty(_favCompanyId))   // sin selección previa: la favorita
                sel = _empCompanies.FindIndex(c => c.Id == _favCompanyId);
            if (sel < 0) sel = 0;
            if (soft && mismaLista && sel == _empCoCombo.SelectedIndex)
            {
                // Misma empresa y misma lista: solo sus datos (saldo, servicios/KPI, socios y rol).
                _empSel = _empCompanies[sel];
                UpdateCompanyDash();
                LoadServices(_empSel);
                LoadMembers(_empSel);
                UpdateDutyHostVisible();
                LoadCompanyVehNames();
                return;
            }
            if (_empCoCombo.SelectedIndex == sel) OnCompanySelected();   // mismo índice: forzar recarga
            else _empCoCombo.SelectedIndex = sel;                        // dispara OnCompanySelected
            UpdateDutyHostVisible();
            LoadCompanyVehNames();          // .eng de mis empresas → etiqueta en Conducción libre/Horarios
            if (!_leagueChecked) { _leagueChecked = true; _ = CheckLeagueAwards(); }   // ¿premios de la liga?
            if (first) _ = RecoverInterruptedServiceAsync();   // ¿quedó un servicio a medias?
        }

        // Servicios de la empresa: la lista (con maquinista y economía) y, a la vez, el tren de cada servicio
        // (la función de la lista no lo devuelve). Si la respuesta es la misma que la anterior (refrescos en
        // segundo plano), no se rehace nada; si mientras tanto se ha pedido otra carga, esta se descarta.
        async void LoadServices(EmpCompany c)
        {
            if (c == null) return;
            int seq = ++_svcLoadSeq;
            if (_svcCardsCompany != c.Id) { _svcCardsCompany = c.Id; _svcLastJson = _svcLastTrenJson = null; _svcCards?.ShowMessage(Tr("Cargando…")); }
            // Todo el historial, por páginas de 1000 (servicios-anulados.sql); un servidor sin ese archivo
            // devuelve los 200 últimos con la función de antes.
            async Task<(string, string)> lista()
            {
                var r = await Supa.RpcAllAsync("list_company_services", (lim, off) => new { p_company = c.Id, p_limit = lim, p_offset = off });
                if (r.err != null && (r.err.Contains("PGRST202") || r.err.Contains("Could not find the function")))
                    r = await Supa.RpcAsync("list_company_services", new { p_company = c.Id });
                return r;
            }
            var tLista = lista();
            var tStats = Supa.RpcAsync("company_service_stats", new { p_company = c.Id });
            string trenQ(string cols) => $"services?select={cols}&company_id=eq.{Uri.EscapeDataString(c.Id)}&order=started_at.desc,id.asc";
            // De más a menos columnas según lo que tenga el servidor (servicio-imagen.sql, motrices-servicio.sql);
            // la que funciona se recuerda para no repetir peticiones fallidas.
            string[] colSets =
            {
                "id,consist,consist_mass,consist_capacity,consist_cars,consist_engines,path,consist_img",
                "id,consist,consist_mass,consist_capacity,consist_cars,consist_engines,path",
                "id,consist,consist_mass,consist_capacity,consist_cars,path",
            };
            var tTren = Supa.SelectAllAsync(trenQ(colSets[_svcColsLevel]));
            var (json, err) = await tLista;
            try
            {
                var (sj, se) = await tStats;
                _svcStats = null;
                if (se == null && !string.IsNullOrWhiteSpace(sj))
                {
                    using var sd = JsonDocument.Parse(sj);
                    var e0 = sd.RootElement.ValueKind == JsonValueKind.Array && sd.RootElement.GetArrayLength() > 0 ? sd.RootElement[0] : sd.RootElement;
                    if (e0.ValueKind == JsonValueKind.Object)
                        _svcStats = ((long)Num(e0, "total"), (long)Num(e0, "open"), (long)Num(e0, "completed"), (long)Num(e0, "annulled"), Num(e0, "km"));
                }
            }
            catch { _svcStats = null; }
            string tj = null;
            try
            {
                var (j, te) = await tTren;
                while (te != null && _svcColsLevel < colSets.Length - 1
                       && (te.IndexOf("consist_img", StringComparison.OrdinalIgnoreCase) >= 0 || te.IndexOf("consist_engines", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    _svcColsLevel++;
                    (j, te) = await Supa.SelectAllAsync(trenQ(colSets[_svcColsLevel]));
                }
                if (te == null) tj = j;
            }
            catch { }
            if (seq != _svcLoadSeq || _empSel?.Id != c.Id) return;   // ya se ha pedido otra
            if (err != null) { _svcRows.Clear(); _svcLastJson = null; _svcCards?.ShowMessage(Tr("Error: ") + err); UpdateServiceTabCounts(); return; }
            if (json == _svcLastJson && tj == _svcLastTrenJson) return;   // nada ha cambiado
            _svcLastJson = json; _svcLastTrenJson = tj;

            var rows = await Task.Run(() => ParseServices(json, tj));   // el JSON se lee fuera del hilo de la interfaz
            if (seq != _svcLoadSeq) return;
            _svcRows.Clear(); _svcRows.AddRange(rows);
            var items = new List<ServiceCardList.Item>(rows.Count);
            foreach (var r in rows)
                items.Add(new ServiceCardList.Item
                {
                    Id = r.Id, Driver = r.Driver, Route = r.Route, Path = r.Path, Train = r.Train == "—" ? "" : r.Train, Status = r.Status,
                    Start = r.Start, Km = r.Km, DurationS = r.DurationS, Pax = r.Pax, Capacity = r.Capacity, MassT = r.MassT,
                    Income = r.Income, Net = r.Net, Cars = r.Cars, Engines = r.Engines, Valid = r.Valid, HasImage = r.HasImage,
                    Annulled = r.Annulled, AnnulReason = r.AnnulReason
                });
            _svcCards?.SetItems(items);
            UpdateServiceTabCounts();
            UpdateCompanyKpis();   // nº de servicios y km totales
        }

        static List<SvcRow> ParseServices(string json, string trenJson)
        {
            var tren = new Dictionary<string, (double mass, double cap, string name, int cars, int engines, string path, bool img)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!string.IsNullOrWhiteSpace(trenJson))
                {
                    using var td = JsonDocument.Parse(trenJson);
                    foreach (var e in td.RootElement.EnumerateArray())
                    {
                        double NumOrNaN(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
                        bool img = e.TryGetProperty("consist_img", out var ci) && ci.ValueKind == JsonValueKind.True;
                        tren[Str(e, "id")] = (NumOrNaN("consist_mass"), NumOrNaN("consist_capacity"), Str(e, "consist"), (int)Num(e, "consist_cars"), (int)Num(e, "consist_engines"), Str(e, "path"), img);
                    }
                }
            }
            catch { }
            var rows = new List<SvcRow>();
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    string id = Str(e, "id");
                    string driver = Str(e, "username"); if (driver.Length == 0) driver = "—";
                    string route = Str(e, "route"); if (route.Length == 0) route = "—";
                    bool valid = !e.TryGetProperty("validated", out var vv) || vv.ValueKind != JsonValueKind.False;
                    var (tm, tc, tn, tcars, teng, tpath, timg) = tren.TryGetValue(id, out var tt) ? tt : (double.NaN, double.NaN, "", 0, 0, "", false);
                    if (string.IsNullOrWhiteSpace(tn)) tn = "—";
                    string started = Str(e, "started_at");
                    var start = DateTime.MinValue;
                    if (DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var so))
                        start = so.LocalDateTime;
                    rows.Add(new SvcRow
                    {
                        Id = id, Date = FmtDate(started), Driver = driver, Route = route, Status = Str(e, "status"),
                        Km = Num(e, "km"), DurationS = Num(e, "duration_s"), Pax = Num(e, "pax"), Income = Num(e, "income"),
                        Cost = Num(e, "cost_total"), Net = Num(e, "net"), Valid = valid,
                        MassT = tm, Capacity = tc, Train = tn, Cars = tcars, Engines = teng,
                        Path = tpath ?? "", Start = start, HasImage = timg,
                        Annulled = Str(e, "annulled_at").Length > 0, AnnulReason = Str(e, "annul_reason"),
                        AnnulledBy = Str(e, "annulled_by_name"), AnnulledAt = FmtDate(Str(e, "annulled_at"))
                    });
                }
            }
            catch { }
            return rows;
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
                InProgress = s.Status == "open", MassT = s.MassT, Capacity = s.Capacity, Cars = s.Cars, Engines = s.Engines,
                ServiceId = s.Id, StartLocal = s.Start
            };
            if (s.Annulled)
            {
                data.StatusText = Tr("Anulado");
                data.AnnulText = "⛔  " + Tr("SERVICIO ANULADO") + (s.AnnulledAt.Length > 0 ? " · " + s.AnnulledAt : "")
                                 + (s.AnnulledBy.Length > 0 ? " · " + s.AnnulledBy : "") + Environment.NewLine
                                 + Tr("Motivo: ") + (s.AnnulReason.Length > 0 ? s.AnnulReason : "—") + Environment.NewLine + Environment.NewLine
                                 + Tr("No cuenta para el ingreso, la liga, el ranking ni el rango.");
            }
            // Datos extra del servicio (tren, recorrido, notas) — máxima info que registró OR/servidor.
            try
            {
                var (sj, se) = await Supa.SelectAsync($"services?select=consist,path,notes,calc,stops,trail&id=eq.{Uri.EscapeDataString(s.Id)}");
                if (se != null && se.IndexOf("trail", StringComparison.OrdinalIgnoreCase) >= 0)   // servidor sin recorrido-servicio.sql
                    (sj, se) = await Supa.SelectAsync($"services?select=consist,path,notes,calc,stops&id=eq.{Uri.EscapeDataString(s.Id)}");
                if (se != null && se.IndexOf("stops", StringComparison.OrdinalIgnoreCase) >= 0)   // servidor sin paradas-servicio.sql
                    (sj, se) = await Supa.SelectAsync($"services?select=consist,path,notes,calc&id=eq.{Uri.EscapeDataString(s.Id)}");
                if (se != null && se.IndexOf("calc", StringComparison.OrdinalIgnoreCase) >= 0)   // servidor sin calculo-servicio.sql
                    (sj, se) = await Supa.SelectAsync($"services?select=consist,path,notes&id=eq.{Uri.EscapeDataString(s.Id)}");
                if (se == null && !string.IsNullOrWhiteSpace(sj))
                {
                    using var sd = JsonDocument.Parse(sj);
                    if (sd.RootElement.ValueKind == JsonValueKind.Array && sd.RootElement.GetArrayLength() > 0)
                    {
                        var e = sd.RootElement[0];
                        data.Consist = Str(e, "consist");
                        data.Path = Str(e, "path");
                        data.Notes = Str(e, "notes");
                        if (e.TryGetProperty("calc", out var ca) && ca.ValueKind == JsonValueKind.Object) data.Calc = ca.Clone();
                        if (e.TryGetProperty("stops", out var sp) && sp.ValueKind == JsonValueKind.Array)
                        {
                            data.Stops = new List<(string, string, int, int)>();
                            foreach (var x in sp.EnumerateArray())
                                data.Stops.Add((Str(x, "s"), Str(x, "t"), (int)Num(x, "b"), (int)Num(x, "a")));
                        }
                        if (e.TryGetProperty("trail", out var tr)) data.Map = ParseTrail(tr);
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
                            case "maintenance": data.Maintenance = amt; break;
                        }
                    }
                }
            }
            catch { }
            // el mapa, sobre el detalle de la ruta si está instalada en este equipo (si no, solo el recorrido)
            if (data.Map != null)
            {
                var ruta = _routesAll.Find(x => string.Equals(x.Name, s.Route, StringComparison.OrdinalIgnoreCase));
                if (ruta != null) data.Map.Detail = await TripDetailFor(ruta.Path);
            }
            using var dlg = new ServiceResultDialog(data);
            dlg.ShowDialog(this);
        }

        // Superadmin: anula el servicio seleccionado con un motivo. Se queda en el historial («Anulado»),
        // deja de contar y su dinero sale de la tesorería; al maquinista le llega un aviso.
        async void AnnulServiceRow()
        {
            if (!Supa.IsSuperadmin || _svcCards == null) return;
            int i = SelectedServiceIndex();
            if (i < 0 || i >= _svcRows.Count) { Msg(_empHomeMsg, Tr("Selecciona un servicio de la lista."), true); return; }
            var s = _svcRows[i];
            if (s.Annulled) { Msg(_empHomeMsg, Tr("Ese servicio ya está anulado."), true); return; }
            if (s.Status == "open") { Msg(_empHomeMsg, Tr("Solo se pueden anular servicios ya registrados."), true); return; }
            string reason;
            using (var dlg = new TextPromptDialog(Tr("Anular servicio"),
                       string.Format(Tr("Servicio de {0} · {1} · {2}. Motivo de la anulación (lo verá el maquinista):"), s.Driver, s.Route, s.Date),
                       "", Tr("Por ejemplo: recorrido hecho con el tiempo acelerado"), Tr("Anular")))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                reason = (dlg.Value ?? "").Trim();
            }
            if (reason.Length < 3) { Msg(_empHomeMsg, Tr("Escribe el motivo de la anulación."), true); return; }
            Msg(_empHomeMsg, Tr("Anulando servicio…"), false);
            var (_, err) = await Supa.RpcAsync("annul_service", new { p_service = s.Id, p_reason = reason });
            if (err != null) { Msg(_empHomeMsg, Tr("Error: ") + err, true); return; }
            Msg(_empHomeMsg, Tr("Servicio anulado. El maquinista ha recibido un aviso."), false);
            LoadCompanies();
            if (_empSel != null) LoadServices(_empSel);
        }

        // Superadmin: elimina el servicio seleccionado (revierte su efecto en la banca en el servidor).
        async void DeleteServiceRow()
        {
            if (!Supa.IsSuperadmin || _svcCards == null) return;
            int i = SelectedServiceIndex();
            if (i < 0 || i >= _svcRows.Count) { Msg(_empHomeMsg, Tr("Selecciona un servicio de la lista."), true); return; }
            if (ThemedBox.Show(this, Tr("¿Eliminar este servicio? Se revertirán también sus apuntes de banca."), "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Msg(_empHomeMsg, Tr("Eliminando servicio…"), false);
            var (_, err) = await Supa.RpcAsync("delete_service", new { p_service = _svcRows[i].Id });
            if (err != null) { Msg(_empHomeMsg, Tr("Error: ") + err, true); return; }
            Msg(_empHomeMsg, Tr("Servicio eliminado."), false);
            LoadCompanies();
            if (_empSel != null) LoadServices(_empSel);
        }

        // Superadmin: elimina el movimiento de banca seleccionado (revierte su importe en el saldo).
        async void DeleteLedgerRow()
        {
            if (!Supa.IsSuperadmin || _bankStmt == null) return;
            string id = _bankStmt.SelectedId;
            if (string.IsNullOrEmpty(id)) { Msg(_empHomeMsg, Tr("Selecciona un movimiento de la lista."), true); return; }
            if (ThemedBox.Show(this, Tr("¿Eliminar este movimiento de banca? Se ajustará el saldo."), "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Msg(_empHomeMsg, Tr("Eliminando movimiento…"), false);
            var (_, err) = await Supa.RpcAsync("delete_ledger_entry", new { p_ledger = id });
            if (err != null) { Msg(_empHomeMsg, Tr("Error: ") + err, true); return; }
            Msg(_empHomeMsg, Tr("Movimiento eliminado."), false);
            LoadCompanies();
            LoadLedger();
        }

        static readonly Color RedC = Color.FromArgb(229, 115, 115);

        async void LoadLedger()
        {
            if (_bankStmt == null) return;
            if (_empSel == null) { _ledgerAll.Clear(); _bankStmt.EmptyText = Tr("Selecciona una empresa."); RefreshBank(); return; }
            string company = _empSel.Id;
            _bankStmt.EmptyText = Tr("Cargando…"); _bankStmt.SetMoves(new List<BankStatement.Move>());
            var (json, err) = await Supa.SelectAsync(
                $"ledger?select=id,created_at,concept,amount,description&company_id=eq.{Uri.EscapeDataString(company)}&order=created_at.desc&limit=1000");
            if (_empSel == null || _empSel.Id != company) return;     // se cambió de empresa mientras tanto
            _ledgerAll.Clear(); _ledgerIds.Clear();
            if (err != null) { _bankStmt.EmptyText = Tr("Error: ") + err; RefreshBank(); return; }
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    var m = new BankStatement.Move { Id = Str(e, "id"), Concept = Str(e, "concept"), Amount = Num(e, "amount"), Desc = Str(e, "description") };
                    m.Label = TrConcept(m.Concept);
                    if (DateTime.TryParse(Str(e, "created_at"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var utc))
                        m.Local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
                    _ledgerAll.Add(m); _ledgerIds.Add(m.Id);
                }
            }
            catch { }
            // Saldo tras cada movimiento: del saldo actual hacia atrás.
            double bal = _empSel.Balance;
            foreach (var m in _ledgerAll) { m.BalanceAfter = bal; bal -= m.Amount; }
            _bankStmt.EmptyText = Tr("Sin movimientos todavía.");
            RefreshBank();
        }

        // Grupo de cada concepto para los filtros del extracto (1 ingresos · 2 cánon · 3 energía · 4 salarios · 5 flota · 6 otros).
        static int BankGroup(BankStatement.Move m) => m.Concept switch
        {
            "income" or "prize" => 1, "canon" => 2, "energy" => 3, "salary" => 4, "purchase" or "maintenance" => 5, "loan" => 7, _ => m.Amount >= 0 && m.Concept != "adjustment" ? 1 : 6
        };

        DateTime BankPeriodStart() => _bankPeriod switch
        {
            0 => new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1),
            1 => new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-5),
            _ => DateTime.MinValue
        };

        // Recalcula el resumen del periodo y el extracto filtrado (sin volver a pedir nada al servidor).
        void RefreshBank()
        {
            if (_bankSummary == null) return;
            var from = BankPeriodStart();
            var ci = System.Globalization.CultureInfo.GetCultureInfo(I18n.English ? "en-GB" : "es-ES");
            double inc = 0, exp = 0;
            var costs = new Dictionary<string, double>();
            foreach (var m in _ledgerAll)
            {
                if (m.Local < from || m.Concept == "adjustment") continue;
                if (m.Concept == "loan" && m.Amount > 0) continue;   // el capital prestado no es un ingreso
                if (m.Amount >= 0) inc += m.Amount;
                else { exp -= m.Amount; costs[m.Concept] = (costs.TryGetValue(m.Concept, out var v) ? v : 0) - m.Amount; }
            }
            // Meses del gráfico: 6 (o hasta 12 con «Todo», desde el primer movimiento).
            var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            int nMonths = 6;
            if (_bankPeriod == 2 && _ledgerAll.Count > 0)
            {
                var oldest = _ledgerAll[_ledgerAll.Count - 1].Local;
                nMonths = Math.Clamp((first.Year - oldest.Year) * 12 + first.Month - oldest.Month + 1, 6, 12);
            }
            var months = new List<(string, double, double)>();
            var incT = new double[nMonths]; var expT = new double[nMonths];
            var start = first.AddMonths(-(nMonths - 1));
            foreach (var m in _ledgerAll)
            {
                if (m.Concept == "adjustment" || m.Local < start || (m.Concept == "loan" && m.Amount > 0)) continue;
                int k = (m.Local.Year - start.Year) * 12 + m.Local.Month - start.Month;
                if (k < 0 || k >= nMonths) continue;
                if (m.Amount >= 0) incT[k] += m.Amount; else expT[k] -= m.Amount;
            }
            int best = -1;
            for (int k = 0; k < nMonths; k++)
            {
                string lbl = start.AddMonths(k).ToString("MMM", ci).TrimEnd('.');
                months.Add((char.ToUpper(lbl[0]) + lbl.Substring(1), incT[k], expT[k]));
                if ((incT[k] > 0 || expT[k] > 0) && (best < 0 || incT[k] - expT[k] > incT[best] - expT[best])) best = k;
            }
            var sm = _bankSummary;
            sm.Company = "SelectOR · " + Tr("BANCA FERROVIARIA");
            sm.Holder = _empSel?.Name ?? "";
            try { sm.Logo = _empSel != null ? LogoFor(_empSel.Id, _empSel.Logo) : null; } catch { sm.Logo = null; }
            sm.Balance = _empSel?.Balance ?? 0;
            string id = (_empSel?.Id ?? "").Replace("-", "").ToUpperInvariant();
            sm.AccountNo = id.Length >= 8 ? "ES" + (Math.Abs(id.GetHashCode()) % 90 + 10) + " " + id.Substring(0, 4) + " •••• •••• " + id.Substring(id.Length - 4) : "";
            sm.MonthNet = incT[nMonths - 1] - expT[nMonths - 1];
            sm.Income = inc; sm.Expense = exp;
            sm.IncomeTrend = incT; sm.ExpenseTrend = expT; sm.Months = months;
            if (best >= 0)
            {
                string bm = start.AddMonths(best).ToString(I18n.English ? "MMMM yyyy" : "MMMM 'de' yyyy", ci);
                sm.BestMonth = char.ToUpper(bm[0]) + bm.Substring(1);
                double bn = incT[best] - expT[best];
                sm.BestMonthSub = string.Format(Tr("{0} de resultado"), (bn >= 0 ? "+" : "−") + Math.Abs(bn).ToString("N0", EsEs) + " €");
            }
            else { sm.BestMonth = "—"; sm.BestMonthSub = ""; }
            sm.Period = _bankPeriod == 0 ? Tr("últimos 6 meses") : string.Format(Tr("últimos {0} meses"), nMonths);
            sm.Costs.Clear();
            foreach (var kv in SortDesc(costs, 6)) { var (ic, col) = BankStatement.IconOf(kv.Key); sm.Costs.Add((ic, TrConcept(kv.Key), kv.Value, col)); }
            sm.Relayout(true);
            FillStatement();
        }

        void FillStatement()
        {
            if (_bankStmt == null) return;
            var from = BankPeriodStart();
            var list = new List<BankStatement.Move>();
            foreach (var m in _ledgerAll)
            {
                if (m.Local < from) continue;
                if (_bankConcept > 0 && BankGroup(m) != _bankConcept) continue;
                if (_bankSearch.Length > 0 && m.Label.IndexOf(_bankSearch, StringComparison.CurrentCultureIgnoreCase) < 0
                    && (m.Desc ?? "").IndexOf(_bankSearch, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                list.Add(m);
            }
            _bankStmt.Selectable = Supa.IsSuperadmin;
            if (list.Count == 0 && _ledgerAll.Count > 0) _bankStmt.EmptyText = Tr("Nada coincide con el filtro.");
            else if (_ledgerAll.Count > 0) _bankStmt.EmptyText = null;
            _bankStmt.SetMoves(list);
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
            "prize" => Tr("Premio de la liga"),
            "adjustment" => Tr("Ajuste de saldo"),
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
            if (!await CompanyNameFree(name, null)) return;
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
            if (!await CompanyNameFree(name, c.Id)) return;
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
            if (ThemedBox.Show(this,
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

        // Tira de indicadores de cada sección (EmpKpiStrip):
        //  · Servicios: tesorería, socios, servicios y km, a todo el ancho.
        //  · Socios: solo los socios, con cuántos hay de cada rol.
        //  · Flota y Compra: solo la tesorería (es con lo que se compra y se mantiene la flota).
        //  · El resto (Banca, Ajustes, Ranking, Mi perfil, Revisión, Usuarios, Administración, Megafonía, Chat): nada.
        static bool SubtabShowsCompanyKpis(int subtab) => subtab == 0 || subtab == 2 || subtab == 9 || subtab == 10;

        static string Plural(int n, string one, string many) => string.Format(Tr(n == 1 ? one : many), n.ToString("N0", EsEs));

        // La tira de cifras de la empresa, a la vista u oculta según la sección. ShowSubtab lo hace ANTES de enseñar la
        // sección: así su panel se enseña ya con el hueco definitivo (si no, se maquetaba dos veces: en Mi perfil, 140 ms).
        bool ApplyCompanyKpisVisibility()
        {
            if (_empStatsCard == null) return false;
            bool show = _empSel != null && SubtabShowsCompanyKpis(_empSubtab);
            if (_empStatsCard.Visible != show) _empStatsCard.Visible = show;   // sin empresa (o fuera de la empresa) no hay KPIs que mostrar
            if (_empStatsGap != null && _empStatsGap.Visible != show) _empStatsGap.Visible = show;
            return show;
        }

        void UpdateCompanyKpis()
        {
            if (_empStatsCard == null) return;
            bool show = ApplyCompanyKpisVisibility();
            // Orden de anclado fijo: título → KPIs → hueco. Si el hueco quedaba por delante, los KPIs bajaban
            // 12 px y se pegaban a las pestañas de la sección.
            var par = _empStatsCard.Parent;
            if (par != null && _empStatsGap != null && _empStatsGap.Parent == par)
            {
                int ic = par.Controls.GetChildIndex(_empStatsCard), ig = par.Controls.GetChildIndex(_empStatsGap);
                if (ic < ig) par.Controls.SetChildIndex(_empStatsCard, ig);
            }
            if (!show) return;
            Color red = Color.FromArgb(229, 115, 115), blue = Color.FromArgb(120, 144, 226), gold = Color.FromArgb(240, 196, 90), teal = Color.FromArgb(45, 212, 191);
            var treasury = new EmpKpiStrip.Kpi
            {
                Icon = "🏦", Caption = Tr("TESORERÍA"), Value = _kpiTreasuryTxt, ValueColor = _kpiTreasuryCol, Tint = _empSel.Balance < 0 ? red : Theme.Accent,
                Sub = _empSubtab == 9 ? Plural(_fleetIds.Count, "saldo para mantener {0} vehículo", "saldo para mantener {0} vehículos")
                    : _empSubtab == 10 ? Tr("disponible para comprar trenes") : Tr("saldo de la empresa")
            };
            var items = new List<EmpKpiStrip.Kpi>();
            if (_empSubtab == 0)
            {
                long open = 0; double km = 0; long done = 0; long total = _svcRows.Count;
                foreach (var r in _svcRows) { if (!r.Annulled) km += r.Km; if (r.Status == "open") open++; else if (!r.Annulled) done++; }
                if (_svcStats is { } st) { total = st.total; open = st.open; done = st.done; km = st.km; }   // sin límite
                int drivers = 0; foreach (var m in _members) if (m.Role == "driver") drivers++;
                items.Add(treasury);
                items.Add(new EmpKpiStrip.Kpi { Icon = "👥", Caption = Tr("SOCIOS"), Value = _members.Count.ToString("N0", EsEs), Tint = blue, Sub = Plural(drivers, "{0} maquinista", "{0} maquinistas") });
                items.Add(new EmpKpiStrip.Kpi { Icon = "🚆", Caption = Tr("SERVICIOS"), Value = total.ToString("N0", EsEs), Tint = gold,
                                                Sub = open > 0 ? string.Format(Tr("{0} en conducción · {1} completados"), open.ToString("N0", EsEs), done.ToString("N0", EsEs)) : Plural((int)Math.Min(int.MaxValue, done), "{0} completado", "{0} completados") });
                items.Add(new EmpKpiStrip.Kpi { Icon = "🛤", Caption = Tr("KM TOTALES"), Value = km.ToString("N0", EsEs) + " km", Tint = teal,
                                                Sub = done > 0 ? string.Format(Tr("media de {0} km por servicio"), (km / done).ToString("N1", EsEs)) : Tr("sin servicios todavía") });
                _empStatsCard.SetItems(items, fill: true);
            }
            else if (_empSubtab == 2)
            {
                int own = 0, man = 0, drv = 0;
                foreach (var m in _members) { if (m.Role == "owner") own++; else if (m.Role == "manager") man++; else drv++; }
                var chips = new List<(string, Color)>();
                if (own > 0) chips.Add(("👑 " + Plural(own, "{0} gerente", "{0} gerentes"), gold));
                if (man > 0) chips.Add(("🗂 " + Plural(man, "{0} gestor", "{0} gestores"), blue));
                if (drv > 0) chips.Add(("🚆 " + Plural(drv, "{0} maquinista", "{0} maquinistas"), Theme.AccentHi));
                items.Add(new EmpKpiStrip.Kpi { Icon = "👥", Caption = Tr("SOCIOS"), Value = Plural(_members.Count, "{0} socio", "{0} socios"), Tint = blue, Chips = chips, Sub = chips.Count == 0 ? Tr("sin socios todavía") : "" });
                if (chips.Count == 0) items[0].Chips = null;
                _empStatsCard.SetItems(items, fill: false);
            }
            else
            {
                items.Add(treasury);
                _empStatsCard.SetItems(items, fill: false);
            }
        }

        void UpdateCompanyDash()
        {
            if (_empSel == null)
            {
                _empCoTitle.Text = Tr("Selecciona o crea una empresa");
                if (_empRoleLbl != null) _empRoleLbl.Text = "";
                if (_empCoIdLbl != null) _empCoIdLbl.Text = "";
                _kpiTreasuryTxt = "—";
                _kpiTreasuryCol = Theme.Subtle;
                if (_empLogoPic != null && _empLogoPic.Image != null) _empLogoPic.Image = null;
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
            {
                var logo = LogoFor(_empSel.Id, _empSel.Logo) ?? PlaceholderLogo(_empSel.Name);
                if (!ReferenceEquals(_empLogoPic.Image, logo)) _empLogoPic.Image = logo;
            }
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
            if (_empCoIdLbl != null) _empCoIdLbl.Text = string.IsNullOrEmpty(_empSel.Code) ? "" : "ID " + _empSel.Code + "  ⧉";
            UpdateCompanyKpis();
        }

        // ============================ Foto de carnet (foto-carnet.sql) ============================
        // Cada uno pone la suya en Mi perfil; sale en su carnet y en el de Socios. Se recorta a 3:4 (centrada)
        // y se sube como JPEG de 240 × 320 px.
        readonly Dictionary<string, (int hash, Image img)> _memberPhotos = new(StringComparer.OrdinalIgnoreCase);
        const int PhotoW = 240, PhotoH = 320;

        static Image DecodePhoto(string data)
        {
            try
            {
                int comma = data.IndexOf(',');
                string b64 = data.StartsWith("data:") && comma > 0 ? data.Substring(comma + 1) : data;
                if (b64.Length > 200_000) return null;
                using var ms = new System.IO.MemoryStream(Convert.FromBase64String(b64.Trim()));
                using var raw = Image.FromStream(ms, false, false);
                if (raw.Width < 1 || raw.Height < 1 || raw.Width > 1024 || raw.Height > 1024) return null;
                return new Bitmap(raw);
            }
            catch { return null; }
        }

        // Guarda (o quita, con data vacío) la foto de un socio, sin volver a decodificarla si no ha cambiado.
        void SetMemberPhoto(string userId, string data)
        {
            if (string.IsNullOrEmpty(userId)) return;
            if (string.IsNullOrEmpty(data)) { _memberPhotos.Remove(userId); return; }   // (las imágenes viejas no se liberan a mano: aún puede estar pintándolas un carnet)
            int h = data.GetHashCode();
            if (_memberPhotos.TryGetValue(userId, out var cur) && cur.hash == h) return;
            var img = DecodePhoto(data);
            if (img == null) return;
            _memberPhotos[userId] = (h, img);
        }

        async void LoadMyPhoto()
        {
            if (!Supa.IsLoggedIn || _profHero == null) return;
            var (json, err) = await Supa.RpcAsync("my_profile_photo", new { });
            if (err != null) { _profHero.CanEditPhoto = !NoMpOnServer(err); _profHero.Invalidate(); return; }   // sin el SQL: no se ofrece
            string data = null;
            try { using var d = JsonDocument.Parse(json); if (d.RootElement.ValueKind == JsonValueKind.String) data = d.RootElement.GetString(); } catch { }
            SetMemberPhoto(Supa.UserId, data);
            _profHero.Photo = _memberPhotos.TryGetValue(Supa.UserId ?? "", out var p) ? p.img : null;
            _profHero.Invalidate();
            UpdateRailAvatar();
        }

        void ShowPhotoMenu(Point screen)
        {
            var menu = MenuStyle.Apply(new ContextMenuStrip() { ShowImageMargin = false });
            menu.Items.Add("📷  " + (_profHero.Photo == null ? Tr("Elegir foto…") : Tr("Cambiar foto…")), null, (s, e) => ChoosePhoto());
            if (_profHero.Photo != null) menu.Items.Add("✕  " + Tr("Quitar foto"), null, (s, e) => SavePhoto(null));
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(screen);
        }

        void ChoosePhoto()
        {
            using var dlg = new OpenFileDialog { Title = Tr("Elegir foto de carnet"), Filter = Tr("Imágenes") + "|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string dataUri;
            try
            {
                using var src = Image.FromFile(dlg.FileName);
                using var bmp = new Bitmap(PhotoW, PhotoH);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.Clear(Color.FromArgb(28, 31, 36));
                    // recorte centrado a 3:4 (un poco hacia arriba: en un retrato la cara suele estar en la parte alta)
                    float k = Math.Max(PhotoW / (float)src.Width, PhotoH / (float)src.Height);
                    float w = src.Width * k, h = src.Height * k;
                    g.DrawImage(src, (PhotoW - w) / 2f, Math.Min(0, (PhotoH - h) * 0.3f), w, h);
                }
                var enc = Array.Find(System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders(), c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                using var ep = new System.Drawing.Imaging.EncoderParameters(1);
                long q = 86;
                byte[] bytes;
                do
                {
                    ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, q);
                    using var ms = new System.IO.MemoryStream();
                    bmp.Save(ms, enc, ep);
                    bytes = ms.ToArray();
                    q -= 10;
                } while (bytes.Length > 100_000 && q > 30);
                dataUri = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
            }
            catch (Exception ex) { Msg(_empHomeMsg, Tr("No se pudo leer la imagen: ") + ex.Message, true); return; }
            SavePhoto(dataUri);
        }

        async void SavePhoto(string dataUri)
        {
            Msg(_empHomeMsg, dataUri == null ? Tr("Quitando la foto…") : Tr("Guardando la foto…"), false);
            var (_, err) = await Supa.RpcAsync("set_profile_photo", new { p_photo = dataUri });
            if (err != null)
            {
                Msg(_empHomeMsg, NoMpOnServer(err) ? Tr("El servidor aún no admite fotos de carnet (falta foto-carnet.sql).") : Tr("Error: ") + err, true);
                return;
            }
            SetMemberPhoto(Supa.UserId, dataUri);
            _profHero.Photo = _memberPhotos.TryGetValue(Supa.UserId ?? "", out var p) ? p.img : null;
            _profHero.Invalidate();
            FillMemberCards();
            UpdateRailAvatar();
            Msg(_empHomeMsg, dataUri == null ? Tr("Foto quitada.") : Tr("Foto de carnet actualizada."), false);
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
            if (!(CanManage() || Supa.IsSuperadmin)) { Msg(_empHomeMsg, Tr("Solo el gerente, un gestor o el administrador pueden cambiar el logotipo."), true); return; }
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
            if (_empCancelOpenBtn != null) _empCancelOpenBtn.Visible = _pendingServiceId != null;
            if (_empDutyHost != null) _empDutyHost.Width = _pendingServiceId != null ? 768 : 584;   // sitio para «Cancelar servicio»
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
                    CurrentConsistLabel(), _curRoute?.Name ?? "", SvcVirtualStart, _prefs, service,
                    () => (_paxOnboard, _paxBoarded, _paxCapacity),   // viajeros en vivo (solo servicio)
                    SmoothPos,                                        // posición en vivo, suavizada (para el mapa)
                    () => _trackedMeters / 1000.0,                    // km recorridos en vivo
                    PaxHudNote,                                       // próxima parada / último embarque
                    () => (PaAvailable, PaOn, PaHudLineName()),       // megafonía: disponible · encendida · línea
                    PaHudLines, PaHudPickLine, PaHudToggle,
                    _driveTrail);                                     // rastro de toda la conducción
                _serviceHud.CabVisible = () => CabHudAlive && _cabHud.Visible;   // botón del pupitre
                _serviceHud.Paused = () => _svcPaused;                            // cronómetro parado: Open Rails en pausa
                _serviceHud.ToggleCab = ToggleCabHudFromBar;
                _serviceHud.Mates = LiveMarkers;   // mapa en vivo: los demás usuarios de la ruta
                _serviceHud.Me = LiveMe;
                WireRoadToHud();                   // hoja de ruta: itinerario en el mapa grande
                var _ = _serviceHud.Handle;   // la ventana existe ya (el trazado del mapa se le pasa aunque esté oculta)
                if (_scenarioReady || force) _serviceHud.Show(); else _hudWaiting = true;
                PushHudMap();   // descarga el trazado de la ruta y lo pasa al HUD
                PushHudDetail();   // mapa grande: vía, andenes, PK, pasos a nivel… (del .tdb)
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

        // /API/MAP/INIT/ (las vías y andenes de la ruta): UNA sola consulta por conducción, compartida por el
        // mini-mapa y los viajeros (antes la hacían los dos a la vez, cada pocos segundos, desde la pantalla de
        // carga). Solo con el escenario ya abierto. Son datos fijos de la ruta, así que no pasa por el turno
        // de OrApi (que la tendría parada mientras OR la genera, en rutas grandes varios segundos).
        readonly object _mapInitLock = new object();
        System.Threading.Tasks.Task<string> _mapInitTask;
        int _mapInitRun = -1, _driveRun;

        string MapInitJson()
        {
            if (!_scenarioReady) return null;   // aún cargando: se reintenta
            System.Threading.Tasks.Task<string> t;
            lock (_mapInitLock)
            {
                bool usable = _mapInitTask != null && _mapInitRun == _driveRun
                              && !(_mapInitTask.IsCompleted && (_mapInitTask.IsFaulted || _mapInitTask.IsCanceled || string.IsNullOrWhiteSpace(_mapInitTask.Result)));
                if (!usable)
                {
                    _mapInitRun = _driveRun;
                    int port = OrWebPort();
                    _mapInitTask = System.Threading.Tasks.Task.Run(async () =>
                    {
                        using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(120) };
                        return await http.GetStringAsync("/API/MAP/INIT/");
                    });
                }
                t = _mapInitTask;
            }
            return t.GetAwaiter().GetResult();
        }

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

        // Prepara el mini-mapa. Fondo = LÍNEAS de vía del TDB (como el mapa de Conducción libre) + estaciones
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
                string txt = MapInitJson();
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
        // IMPORTANTE: igual que el mapa de Conducción libre, EXTIENDE cada tramo hasta la posición real de sus
        // empalmes/finales (el .tdb solo guarda el INICIO de cada sección → sin esto quedan huecos en los desvíos).
        static void ReadTdbWorld(string routeDir, out List<PointF[]> net, out Dictionary<string, (double sx, double sz, int n)> stWorld, bool withNet = true)
        {
            net = new List<PointF[]>();
            stWorld = new Dictionary<string, (double, double, int)>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(routeDir) || !System.IO.Directory.Exists(routeDir)) return;
            var tdb = System.IO.Directory.GetFiles(routeDir, "*.tdb");
            if (tdb.Length == 0) return;
            var db = new Orts.Formats.Msts.TrackDatabaseFile(tdb[0]);
            var tnodes = db.TrackDB.TrackNodes;
            var tsec = withNet ? TrackGeometry.Sections(routeDir) : null;   // curvas y desvíos como arcos (tsection.dat)

            var edges = new List<(int a, int b, PointF[] pts)>();
            var nodePos = new Dictionary<int, PointF>();   // posición de empalmes/finales (vértices compartidos)
            for (int ni = 0; withNet && ni < tnodes.Length; ni++)
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
            await RecoverInterruptedServiceAsync();   // el servidor borra los servicios que queden abiertos
            _estPatKm = EstimateKm();          // longitud de la ruta (respaldo)
            _estimatedKm = _estPatKm;
            _driveStartUtc = DateTime.UtcNow;  // para localizar el .save de esta sesión
            _empStartFailReason = null;
            // Fuera de flota no cuenta: la máquina de cabeza de tu tren debe ser una unidad de la
            // empresa disponible (y que tienes en local, ya que estás conduciendo ese .con).
            if (CurrentDrivenConsist() == null)
            {
                _empStartFailReason = _activePage == 1
                    ? Tr("No se ha podido leer el tren de la actividad elegida.")
                    : Tr("Elige primero el tren que vas a conducir.");
                return null;
            }
            var engNames = CurrentConsistEngineNames();   // la motriz de cabeza y su formación fija
            if (engNames.Count == 0)
            {
                _empStartFailReason = Tr("El tren seleccionado no tiene una máquina de tracción reconocible. Elige un tren con locomotora o automotor.");
                return null;
            }
            // Un ejemplar libre de ese tren de la empresa (o, según el modo, otro con la misma cabeza, o una máquina anterior).
            var (vehicleId, reason) = await ResolveServiceUnitAsync(_empOnDutyCompany.Id, CurrentDrivenConsist(), engNames);
            if (vehicleId == null) { _empStartFailReason = reason; return null; }   // no es de la flota / no disponible
            // Ruta autorizada (rutas-autorizadas.sql): en modo obligatorio, sin ella no hay servicio.
            string routeBlock = await RouteGateAsync(_empOnDutyCompany.Id);
            if (routeBlock != null) { _empStartFailReason = routeBlock; return null; }
            var (json, err) = await StartServiceRpc(_empOnDutyCompany.Id, _curRoute?.Name ?? "", CurrentConsistLabel(),
                                                    CurrentPathLabel(), vehicleId, CurrentDrivenConsist());
            if (err != null || string.IsNullOrWhiteSpace(json))
            {
                _empStartFailReason = err != null ? (Tr("No se pudo abrir el servicio: ") + err) : null;
                return null;
            }
            string sid = json.Trim().Trim('"');
            try
            {
                using var d = JsonDocument.Parse(json);
                if (d.RootElement.ValueKind == JsonValueKind.String) sid = d.RootElement.GetString();
            }
            catch { }
            return sid;
        }

        // Nombre del .eng líder (máquina de tracción) del tren seleccionado en la pestaña activa.
        string CurrentLeadEngName()
        {
            var c = CurrentDrivenConsist();
            var fp = c?.Locomotive?.FilePath;
            return string.IsNullOrEmpty(fp) ? null : System.IO.Path.GetFileNameWithoutExtension(fp);
        }

        // Nombres .eng de los coches motrices del tren de la pestaña activa (cabeza + coches del
        // consist), para reconocer la unidad aunque se conduzca con un consist invertido.
        List<(string name, string folder)> CurrentConsistEngineNames()
            => FleetAccessNames(CurrentDrivenConsist());   // la motriz de cabeza (y su formación fija, si la tiene)

        // Abre el servicio enviando los totales del tren que se conduce (masa, vehículos y plazas): con
        // ellos el servidor calcula el ingreso y la energía de los mercancías según su masa. Si el
        // servidor aún no tiene mercancias-masa.sql, se abre como antes, sin esos datos.
        async Task<(string json, string err)> StartServiceRpc(string company, string route, string consist, string path,
                                                               string vehicle, TrainItem train)
        {
            var tot = train != null ? ConsistTotals(train) : default;
            _svcTrainMass = tot.mass > 0 ? Math.Round(tot.mass, 1) : double.NaN;
            _svcTrainCap = tot.cars > 0 ? Math.Round(tot.capacity) : double.NaN;
            if (tot.cars > 0 && tot.mass > 0)
            {
                // Con las motrices del .con (motrices-servicio.sql); si el servidor aún no las conoce, sin ellas.
                int engines = ConsistEngineCount(train.FilePath);
                // ¿Es un tren de viajeros? (tarifas-equilibrio.sql): sin plazas declaradas cobra como traslado
                // en vacío, no como un mercancías. Si el servidor aún no lo conoce, se abre sin ese dato.
                bool? viajeros = TrainIsPassenger(train);
                // Confort del tren que se conduce (confort-por-servicio.sql): con él se calcula el billete de ESTE
                // servicio, no con el de la unidad al comprarla. Si el servidor aún no lo conoce, se abre sin él.
                double? confort = viajeros != null ? await Task.Run(() => DrivenComfort(train)) : null;
                if (viajeros != null && confort != null)
                {
                    var (jc, ec) = await Supa.RpcAsync("start_service", new
                    {
                        p_company = company, p_route = route, p_consist = consist, p_path = path, p_vehicle = vehicle,
                        p_mass = Math.Round(tot.mass, 1), p_cars = tot.cars, p_capacity = Math.Round(tot.capacity), p_engines = engines,
                        p_passenger = viajeros.Value, p_comfort = confort.Value
                    });
                    bool sinConfort = ec != null && (ec.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0
                                                     || ec.IndexOf("p_comfort", StringComparison.OrdinalIgnoreCase) >= 0
                                                     || ec.IndexOf("Could not find the function", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!sinConfort) return (jc, ec);
                }
                if (viajeros != null)
                {
                    var (j0, e0) = await Supa.RpcAsync("start_service", new
                    {
                        p_company = company, p_route = route, p_consist = consist, p_path = path, p_vehicle = vehicle,
                        p_mass = Math.Round(tot.mass, 1), p_cars = tot.cars, p_capacity = Math.Round(tot.capacity), p_engines = engines,
                        p_passenger = viajeros.Value
                    });
                    bool sinTipo = e0 != null && (e0.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0
                                                  || e0.IndexOf("p_passenger", StringComparison.OrdinalIgnoreCase) >= 0
                                                  || e0.IndexOf("Could not find the function", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!sinTipo) return (j0, e0);
                }
                var (j, e) = await Supa.RpcAsync("start_service", new
                {
                    p_company = company, p_route = route, p_consist = consist, p_path = path, p_vehicle = vehicle,
                    p_mass = Math.Round(tot.mass, 1), p_cars = tot.cars, p_capacity = Math.Round(tot.capacity), p_engines = engines
                });
                bool sinMotrices = e != null && (e.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0
                                                 || e.IndexOf("p_engines", StringComparison.OrdinalIgnoreCase) >= 0
                                                 || e.IndexOf("Could not find the function", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!sinMotrices) return (j, e);
                (j, e) = await Supa.RpcAsync("start_service", new
                {
                    p_company = company, p_route = route, p_consist = consist, p_path = path, p_vehicle = vehicle,
                    p_mass = Math.Round(tot.mass, 1), p_cars = tot.cars, p_capacity = Math.Round(tot.capacity)
                });
                bool viejo = e != null && (e.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0
                                           || e.IndexOf("p_mass", StringComparison.OrdinalIgnoreCase) >= 0
                                           || e.IndexOf("Could not find the function", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!viejo) return (j, e);
            }
            return await Supa.RpcAsync("start_service", new
            { p_company = company, p_route = route, p_consist = consist, p_path = path, p_vehicle = vehicle });
        }

        // Confort (0-100) del tren tal como se va a conducir: el mismo cálculo que la ficha de Compra (velocidad máxima de
        // la cabeza, densidad de viajeros y basculante), con los coches de ESTE .con. Sin plazas declaradas, 0.
        double? DrivenComfort(TrainItem train)
        {
            try
            {
                string eng = train?.Locomotive?.FilePath;
                double kmh = string.IsNullOrEmpty(eng) ? 0 : ReadEngineSpecs(eng).kmh;
                var an = AnalyzeComposition(train, kmh);
                return an.Capacity > 0 ? an.Comfort : 0;
            }
            catch { return null; }
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

        // Unidades de la empresa con un servicio abierto ahora mismo (null si no se pudo saber).
        async Task<HashSet<string>> OpenServiceUnits(string companyId)
        {
            try
            {
                // máquinas y coches/vagones en servicio (vagones-servicio.sql); sin ese SQL, las máquinas de los servicios abiertos
                var (rj, re) = await Supa.RpcAsync("open_service_units", new { p_company = companyId });
                if (re == null && !string.IsNullOrWhiteSpace(rj))
                {
                    var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using var rd = JsonDocument.Parse(rj);
                    if (rd.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in rd.RootElement.EnumerateArray())
                        {
                            string id = e.ValueKind == JsonValueKind.String ? e.GetString() : Str(e, "open_service_units");
                            if (!string.IsNullOrEmpty(id)) all.Add(id);
                        }
                        return all;
                    }
                }
                var (json, err) = await Supa.SelectAsync($"services?select=vehicle_id&company_id=eq.{Uri.EscapeDataString(companyId)}&status=eq.open&vehicle_id=not.is.null");
                if (err != null || string.IsNullOrWhiteSpace(json)) return null;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray()) { var id = Str(e, "vehicle_id"); if (id.Length > 0) set.Add(id); }
                return set;
            }
            catch { return null; }
        }

        // Estado real de una unidad. La marca «in_use» del servidor se queda puesta si alguien cierra el
        // simulador sin terminar el servicio (el servidor la libera cuando otro se pone de servicio): solo está
        // en servicio si tiene un servicio abierto. El taller lo marca solo el servidor: es automático
        // (taller-automatico.sql) y la unidad pasa por él al cerrar el servicio en que cumple sus km. Antes se daba por
        // «en taller» en cuanto los km pasaban del intervalo y, sin el botón «Llevar al taller», se quedaba bloqueada.
        static string RealUnitStatus(string status, string id, HashSet<string> open, double kmSinceMaint, double maintInterval)
        {
            if (open != null ? open.Contains(id ?? "") : status == "in_use") return "in_use";
            if (status == "maintenance_due") return "maintenance_due";
            return "available";
        }

        async Task<(string id, string reason)> ResolveCompanyUnitReason(string companyId, List<(string name, string folder)> engs)
        {
            _lastUnitReasonCode = "error";
            if (engs == null || engs.Count == 0 || string.IsNullOrEmpty(companyId)) return (null, null);
            var esc = new List<string>();
            var folderOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var en in engs) { esc.Add(PgInItem(en.name)); folderOf[en.name] = en.folder ?? ""; }
            string inList = string.Join(",", esc);
            _lastUnitPlate = "";
            string vq = $"&company_id=eq.{Uri.EscapeDataString(companyId)}&name=in.({inList})&or=(kind.is.null,kind.neq.train)&order=created_at.asc";
            var tOpen = OpenServiceUnits(companyId);
            var (json, err) = await SelectVehicles("id,status,plate,name,folder,km_since_maint,maint_interval_km", vq);
            if (err != null && err.IndexOf("plate", StringComparison.OrdinalIgnoreCase) >= 0)   // aún sin columna de matrícula
                (json, err) = await SelectVehicles("id,status,name,folder,km_since_maint,maint_interval_km", vq);
            var openUnits = await tOpen;
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
                    string rowName = Str(e, "name"), rowFolder = Str(e, "folder"), id = Str(e, "id");
                    string st = RealUnitStatus(Str(e, "status"), id, openUnits, Num(e, "km_since_maint"), Num(e, "maint_interval_km"));
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
                return (null, string.Format(Tr("Tren no operativo: {0} está(n) pasando por el taller, que es automático al cumplir sus km. Vuelve a intentarlo en unos minutos."), cuantas));
            return (null, string.Format(Tr("Tren no operativo: {0} no está(n) disponible(s): {1} en servicio y {2} en mantenimiento."), cuantas, enUso, enTaller));
        }

        // Tren seleccionado en la pestaña activa (Actividad / Conducción libre / Horarios / Multijugador).
        string CurrentConsistLabel()
        {
            string s = _activePage switch
            {
                1 => CurrentDrivenConsist()?.Name ?? (_lstActivities.SelectedItem as Activity)?.Name,   // el tren de la actividad
                2 => (_lstConsists.SelectedItem as TrainItem)?.Name,
                3 => _cboTTTrain.SelectedItem?.ToString(),
                4 => (_lstConsists.SelectedItem as TrainItem)?.Name,
                _ => null
            };
            return s ?? "";
        }

        string CurrentPathLabel() => DrivenPath()?.Name ?? "";

        // Recorrido que se va a conducir: en Actividad, el de la actividad; si no, el elegido en Conducción libre.
        OrPath DrivenPath() => _activePage == 1 && _lstActivities.SelectedItem is Activity a && a.Path != null ? a.Path : CurrentPath();

        // Llamado cuando OR se cierra tras un lanzamiento con servicio abierto.
        void OnDriveReturned()
        {
            CloseServiceHud();   // cerrar el HUD al volver de conducir
            CloseCabHud();       // y el pupitre
            CloseChatHud();      // y el chat
            CloseDriveBar();     // y la barra superior
            RestoreAfterDrive();
            FinalizeService();
            NotifySoon(2500);    // los avisos que llegaron mientras conducías
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

        // Servidor web de OR activado ANTES de lanzarlo (antes se activaba después, y la primera vez OR ya
        // había leído sus opciones sin él). Se usa la opción de OR por su nombre (vale para el registro y para
        // OpenRails.ini) y, por si acaso, el valor del registro.
        void EnsureOrWebServer()
        {
            try
            {
                var s = new ORTS.Settings.UserSettings(new string[0]);
                var pi = s.GetType().GetProperty("WebServer");
                if (pi != null && pi.PropertyType == typeof(bool) && pi.CanWrite && !(bool)pi.GetValue(s)) { pi.SetValue(s, true); s.Save(); }
            }
            catch { }
            try { Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\Software\OpenRails\ORTS", "WebServer", 1); } catch { }
        }

        // F2 en una partida multijugador como cliente: Open Rails no guarda (y no avisa). SelectOR lo dice.
        bool _mpClientRun;
        DateTime _mpSaveToldUtc = DateTime.MinValue;
        void OnSimulatorKey(int vk)
        {
            if (vk != 0x71 /*F2*/ || !_mpClientRun || (DateTime.UtcNow - _mpSaveToldUtc).TotalSeconds < 30) return;
            _mpSaveToldUtc = DateTime.UtcNow;
            try
            {
                EnqueueToast(new NotificationToast("💾", Tr("Partida no guardada"),
                    Tr("Open Rails no guarda las partidas multijugador cuando te unes a un servidor (solo el que la aloja puede guardarlas)."),
                    "SelectOR · " + Tr("Multijugador"), Carne.Gold));
            }
            catch { }
        }

        void StartKmTracking(bool withPax = true)
        {
            OrControl.SimulatorKey -= OnSimulatorKey;
            OrControl.SimulatorKey += OnSimulatorKey;
            _trackedMeters = 0; _tHave = false; _tLat = _tLon = 0;
            _kmLastFixS = double.NaN; _kmRejected = 0; _kmRejectedM = 0;
            _orKmhLast = 0; _orKmhMax = double.NaN; _orKmhAtS = double.NaN;
            _driveTrail.Clear();   // conducción nueva: rastro nuevo
            RoadDriveStart();      // y hoja de ruta vacía
            StartSvcClock(null);   // el cronómetro espera a que el escenario esté abierto
            _scenarioReady = false;   // y los HUD también (ver OnScenarioReady)
            _driveRun++;              // conducción nueva: el mapa de la ruta se vuelve a pedir
            _stoppedSinceUtc = null;
            _paxActive = false; _paxWanted = false; _paxBoarded = 0; _paxOnboard = 0; _paxCapacity = 0; _paxKm = 0;
            try { Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\Software\OpenRails\ORTS", "WebServer", 1); } catch { }
            try
            {
                _kmHttp?.Dispose();
                _kmHttp = new System.Net.Http.HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{OrWebPort()}"), Timeout = TimeSpan.FromSeconds(2) };
                _kmTimer?.Dispose();
                _kmTimer = new Timer { Interval = 1500 };
                _kmTimer.Tick += async (s, e) => await PollKm();
                _kmTimer.Start();
                // Lo que hace el usuario en el simulador (F2, F9, clics): mientras, no se le pregunta nada (OrControl).
                _inputWatch?.Dispose();
                _inputWatch = new Timer { Interval = 40 };
                _inputWatch.Tick += (s, e) => OrControl.WatchUserInput();
                _inputWatch.Start();
                if (withPax) StartPaxTracking(_drivenConsist ?? CurrentDrivenConsist());   // viajeros (servicio o conducción libre)
                PaDriveStart(RouteIds.IdOf(_curRoute?.Path, _curRoute?.Name), _curRoute?.Name);   // megafonía: se descarga lo de esta ruta y queda lista
                StartLiveMap();                  // mapa en vivo: mi posición y la de los demás en la ruta
            }
            catch { }
        }

        // TrainItem del tren de la pestaña activa (para el que se conduce).
        TrainItem CurrentDrivenConsist() => _activePage switch
        {
            1 => ActivityConsist(_lstActivities.SelectedItem as Activity),   // Actividad: el tren de la actividad
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
        // Andenes reales (del .tdb, con sus dos extremos): el embarque solo se hace con el tren DENTRO de uno.
        readonly List<(string key, Anden a)> _paxPlat = new();
        readonly HashSet<string> _paxPlatKeys = new(StringComparer.OrdinalIgnoreCase);   // estaciones con andenes medibles
        // Para saber si ALGÚN coche está dentro del andén: Open Rails da la posición de la locomotora que
        // conduces; el resto del tren va por detrás, por la misma vía que ella acaba de recorrer. Se guarda
        // ese rastro (los últimos metros, tantos como mide el tren) y la longitud del tren (sus coches).
        readonly List<(double lat, double lon)> _paxTrail = new();
        double _paxTrainLenM, _paxLeadAheadM;   // longitud del tren y parte que va por delante de la locomotora
        int _paxDoorNeed;                        // puertas del embarque en curso (1 izq., 2 der., 3 las dos, 0 cualquiera)
        string _paxWrongSideSaid;                // estación en la que ya se avisó de «puertas del otro lado»
        readonly Dictionary<string, int> _paxVisits = new();
        double _pPrevLat, _pPrevLon, _pDirX, _pDirY; bool _pHavePrev, _pHaveDir;
        string _paxNextName, _paxNextNorm; int _paxNextWaiting; double _paxNextDist;
        string _paxLastEvent; DateTime _paxLastEventUtc;
        // Paradas comerciales del viaje (puertas abiertas en un andén): para la ventana del servicio.
        sealed class StopRec { public string Station, Time; public DateTime Utc; public int Board, Alight; public double Lat = double.NaN, Lon = double.NaN; }
        readonly List<StopRec> _svcStopsLog = new();
        int _paxLastHour = 12; DateTime _paxHourUtc;

        // ---------------- Parámetros del modelo (Ajustes → Viajeros / Clasificación) ----------------
        // Se guardan en app_settings.pax_model (jsonb). Si la columna aún no existe, se usan estos valores.
        sealed class PaxModelConfig
        {
            public double[] StationWeights = { 0.6, 1.0, 1.8, 3.0 };      // metros de andén: < 250 · 250–600 · 600–1.400 · 1.400+ (sin andenes medibles: 1 · 2–3 · 4–7 · 8+ andenes)
            public double CapacityPct = 12;                               // % de las plazas del tren que espera en una estación ×1
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
                if (!(c.CapacityPct > 0)) c.CapacityPct = d.CapacityPct;   // modelos guardados antes de existir
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
            _svcStopsLog.Clear();
            _paxPlat.Clear(); _paxPlatKeys.Clear(); _paxTrail.Clear(); _paxTrainLenM = 0; _paxLeadAheadM = 0; _paxDoorNeed = 0; _paxWrongSideSaid = null; _paxPaused = false; _paxPausedNorm = null;
            string paxRouteDir = _curRoute?.Path;
            _paxBoarded = 0; _paxOnboard = 0; _paxCapacity = 0; _paxKm = 0; _paxBusy = false; _paxWanted = true;
            _pHavePrev = _pHaveDir = false; _paxNextName = null; _paxLastEvent = null; _paxHourUtc = DateTime.MinValue;            _paxSeed = Environment.TickCount;
            // Estación del año y clima elegidos para el viaje (Conducción libre / Horarios; en Actividad, neutros).
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
                (_paxTrainLenM, _paxLeadAheadM) = TrainLengths(c.FilePath);
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
                // Andenes con sus extremos y su longitud (del .tdb): tamaño de la estación y embarque dentro del andén.
                var plats = await Task.Run(() => Andenes.Load(paxRouteDir));
                if (!_paxWanted) return;
                ApplyPlatforms(plats);
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

        // Andenes del .tdb: cada uno se asigna a su estación (por nombre o, si Open Rails la llama distinto,
        // a la más cercana) y el TAMAÑO de la estación pasa a ser el de sus metros de andén (los andenes mal
        // hechos, de menos de 20 m, no cuentan). Las estaciones sin andenes medibles siguen como antes.
        void ApplyPlatforms(List<Anden> plats)
        {
            if (plats == null || plats.Count == 0) return;
            var metros = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in plats)
            {
                string key = NormStation(a.Station);
                if (!_paxMeta.ContainsKey(key))
                {
                    string k2 = NormStation(a.Name);
                    if (k2.Length > 0 && _paxMeta.ContainsKey(k2)) key = k2;
                    else
                    {
                        double bd = 400; string near = null;
                        foreach (var kv in _paxMeta)
                        {
                            double d = Haversine(a.MidLat, a.MidLon, kv.Value.lat, kv.Value.lon);
                            if (d < bd) { bd = d; near = kv.Key; }
                        }
                        if (near != null) key = near;
                    }
                }
                if (string.IsNullOrEmpty(key)) continue;
                if (!_paxMeta.ContainsKey(key))
                {
                    // Estación que la API de Open Rails no nombra: se añade con los datos del .tdb.
                    _paxMeta[key] = (CleanStation(a.Station), a.MidLat, a.MidLon, PaxCfg.StationWeights[0]);
                    _paxStations.Add((key, a.Lat1, a.Lon1));
                    _paxStations.Add((key, a.Lat2, a.Lon2));
                }
                if (!a.Valid) continue;
                _paxPlat.Add((key, a));
                _paxPlatKeys.Add(key);
                metros[key] = (metros.TryGetValue(key, out var mm) ? mm : 0) + a.LengthM;
            }
            foreach (var kv in metros)
            {
                var m = _paxMeta[kv.Key];
                _paxMeta[kv.Key] = (m.name, m.lat, m.lon, WeightByPlatformMeters(kv.Value));
            }
        }

        // Tamaño de la estación por metros totales de andén (tramos equivalentes a los de antes por nº de
        // andenes, con andenes de ~170 m): < 250 m · 250–600 · 600–1.400 · 1.400 o más.
        static double WeightByPlatformMeters(double m)
        {
            var sw = PaxCfg.StationWeights;
            return m < 250 ? sw[0] : m < 600 ? sw[1] : m < 1400 ? sw[2] : sw[3];
        }

        // Descarga los andenes de viajeros de la ruta (puntos "Named" acabados en "platform"),
        // con su nombre y posición en el MISMO lat/lon que la posición del tren.
        // null = OR aún no responde (cargando); lista vacía = respondió pero la ruta no tiene andenes con nombre.
        List<(string station, double lat, double lon)> FetchPaxStations()
        {
            var res = new List<(string, double, double)>();
            try
            {
                string txt = MapInitJson();
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

        // Viajeros que esperan en una estación a una hora dada. La base es un % de las plazas del tren: un
        // tren grande encuentra más gente que un automotor pequeño, y una estación grande más que un apeadero.
        int PaxDemandAt(string norm, int hour)
        {
            double weight = _paxMeta.TryGetValue(norm, out var m) ? m.weight : 1.0;
            int visit = _paxVisits.TryGetValue(norm, out var v) ? v : 0;
            var curve = PaxHourCurve[(int)_paxProfile];
            double basePax = Math.Max(0, _paxCapacity) * PaxCfg.CapacityPct / 100.0;
            double d = basePax * weight * PaxProfileFactor(_paxProfile) * curve[((hour % 24) + 24) % 24]
                     * PaxSeasonFactor(_paxProfile, _paxSeason) * PaxWeatherFactor(_paxProfile, _paxWeather)
                     * PaxJitter(norm, visit, PaxCfg.Jitter);
            return Math.Max(0, (int)Math.Round(d));
        }

        // Longitud del tren (suma de sus coches) y cuánto va por delante del centro de la locomotora
        // (lo que Open Rails sitúa en /API/MAP): los coches antes de la primera motriz y media motriz.
        (double length, double ahead) TrainLengths(string conPath)
        {
            double len = 0, ahead = 0; bool lead = false;
            try
            {
                foreach (var r in ConsistCarRefs(conPath))
                {
                    double l = Veh(ResolveCarFile(r.name, r.folder))?.Length ?? 0;
                    if (l <= 0 || l > 100) l = 20;   // coche sin medidas: uno normal
                    if (!lead && r.isEngine) { ahead += l / 2; lead = true; }
                    else if (!lead) ahead += l;
                    len += l;
                }
            }
            catch { }
            return (len, lead ? ahead : 0);
        }

        // Rastro de la locomotora: un punto cada ~3 m, los últimos (longitud del tren y algo más: en lat/lon
        // las distancias salen deformadas hasta un ~20 %). Un salto de posición (el tren se coloca al
        // cargar, cambio de tren) lo borra: el rastro de antes ya no vale.
        void AddPaxTrail(double lat, double lon)
        {
            if (_paxTrail.Count > 0)
            {
                var (pl, pn) = _paxTrail[_paxTrail.Count - 1];
                double d = Haversine(pl, pn, lat, lon);
                if (d > 500) _paxTrail.Clear();
                else if (d < 3) return;
            }
            _paxTrail.Add((lat, lon));
            double keep = _paxTrainLenM * 1.4 + 40, acc = 0;
            for (int i = _paxTrail.Count - 1; i > 0; i--)
            {
                acc += Haversine(_paxTrail[i].lat, _paxTrail[i].lon, _paxTrail[i - 1].lat, _paxTrail[i - 1].lon);
                if (acc > keep) { _paxTrail.RemoveRange(0, i - 1); break; }
            }
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
            if (_paxPaused) return $"{_paxAnimStation}:  +{_paxAnimBoarded} / −{_paxAnimAlighted}  ·  " + Tr("puertas cerradas");
            if (_paxLastEvent != null && (DateTime.UtcNow - _paxLastEventUtc).TotalSeconds < 20) return _paxLastEvent;
            return "";
        }

        // ---- Embarque PROGRESIVO: los viajeros bajan y suben poco a poco (no de golpe) ----
        System.Windows.Forms.Timer _paxAnimTimer;
        readonly Random _paxRnd = new Random();
        bool _paxAnimating, _paxAnimDoorCheck;
        string _paxAnimStation;
        // En PAUSA: se cerraron las puertas antes de acabar. Al volver a abrirlas (del lado del andén, con el
        // tren aún en ese andén) sigue donde iba; si el tren se va del andén, se da por terminado.
        bool _paxPaused; string _paxAnimNorm, _paxPausedNorm;
        int _paxAnimAlightLeft, _paxAnimBoardLeft, _paxAnimAlighted, _paxAnimBoarded, _paxAnimTicks, _paxAnimStep;

        void StartPaxAnimation(string station, string norm, int alight, int board)
        {
            _paxAnimStation = station; _paxAnimNorm = norm; _paxPaused = false;
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
                    // Se corta al cerrar las puertas del lado del andén (o, si no se sabe el lado, todas).
                    // Sin forma de saber las puertas: al arrancar el tren.
                    var (sidesKnown, sides) = await FetchDoorSides();
                    if (sidesKnown) stop = _paxDoorNeed != 0 ? (sides & _paxDoorNeed) == 0 : sides == 0;
                    else
                    {
                        var (hasDoors, open) = await FetchDoorsState();
                        stop = hasDoors ? !open : (_stoppedSinceUtc == null);
                    }
                }
                catch { }
                _paxAnimDoorCheck = false;
                if (stop) { PausePaxAnimation(); return; }
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

        void PausePaxAnimation()
        {
            _paxAnimTimer?.Stop();
            if (!_paxAnimating) return;
            _paxAnimating = false;
            _paxPaused = true; _paxPausedNorm = _paxAnimNorm;
        }

        void ResumePaxAnimation()
        {
            if (!_paxPaused) return;
            _paxPaused = false; _paxAnimating = true; _paxAnimDoorCheck = false; _paxAnimTicks = 0;
            _paxAnimTimer?.Start();
        }

        void EndPaxAnimation()
        {
            _paxAnimTimer?.Stop();
            if (!_paxAnimating && !_paxPaused) return;
            _paxAnimating = false; _paxPaused = false;
            _paxLastEvent = $"{_paxAnimStation}:  +{_paxAnimBoarded} / −{_paxAnimAlighted}";
            _paxLastEventUtc = DateTime.UtcNow;
            if (_svcStopsLog.Count > 0 && _svcStopsLog[^1].Station == _paxAnimStation)
            { _svcStopsLog[^1].Board = _paxAnimBoarded; _svcStopsLog[^1].Alight = _paxAnimAlighted; }
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
            AddPaxTrail(lat, lon);
            if (_paxBusy || _paxAnimating) return;
            // ¿Algún coche dentro de un andén? (tramo entre sus dos marcas; si hay varios, el de la vía más cercana)
            string bestNorm = null; Anden bestPlat = null;
            double bestLat = double.MaxValue;
            var trail = new List<(double lat, double lon)>(_paxTrail) { (lat, lon) };
            double back = Math.Max(0, _paxTrainLenM - _paxLeadAheadM);
            foreach (var (key, a) in _paxPlat)
                if (Andenes.TrainInside(a, trail, back, _paxLeadAheadM, out double lt, out _) && lt < bestLat) { bestLat = lt; bestNorm = key; bestPlat = a; }
            if (bestNorm == null)
            {
                // Estaciones sin andenes medibles (o ruta sin .tdb): como antes, a menos de 250 m de un punto de andén.
                double best = double.MaxValue; string near = null;
                foreach (var st in _paxStations)
                {
                    double dm = Haversine(lat, lon, st.lat, st.lon);
                    if (dm < best) { best = dm; near = st.station; }
                }
                if (near != null && best <= 250 && !_paxPlatKeys.Contains(near)) bestNorm = near;
            }
            if (_paxPaused)
            {
                // Intercambio a medias: si el tren ya no está en ese andén, se acaba; si sigue, al volver a
                // abrir las puertas (del lado del andén) continúa.
                if (bestNorm != _paxPausedNorm) { EndPaxAnimation(); return; }
                _paxBusy = true;
                try
                {
                    var (sk, sd) = await FetchDoorSides();
                    bool again;
                    if (sk) again = _paxDoorNeed != 0 ? (sd & _paxDoorNeed) != 0 : sd != 0;
                    else
                    {
                        var (hasDoors, open) = await FetchDoorsState();
                        again = hasDoors ? open : TrainStoppedByPosition(3);
                    }
                    if (again && _paxPaused) ResumePaxAnimation();
                }
                catch { }
                finally { _paxBusy = false; }
                return;
            }
            if (bestNorm == null || _paxDone.Contains(bestNorm)) return;
            _paxBusy = true;
            try
            {
                // Parado: por la posición (vale con OR en cualquier idioma); si no, por la velocidad del HUD de OR.
                if (!TrainStoppedByPosition(3))
                {
                    double speed = await FetchSpeedKmh();
                    if (speed > 2.0) return;             // aún en movimiento
                }
                // Puertas: tienen que estar ABIERTAS y del LADO DEL ANDÉN (si la ruta dice a qué lado queda).
                // Si no hay forma de saber las puertas, cuenta como parada comercial tras ~6 s detenido.
                int need = Andenes.DoorsFor(bestPlat, trail);            // 0: el lado no se sabe → vale cualquiera
                var (sidesKnown, sides) = await FetchDoorSides();
                if (sidesKnown)
                {
                    if (sides == 0) return;                              // puertas cerradas → no embarca
                    if (need != 0 && (sides & need) == 0)
                    {
                        // Abiertas del otro lado: nadie sube ni baja. Se avisa una vez por estación.
                        if (_paxWrongSideSaid != bestNorm)
                        {
                            _paxWrongSideSaid = bestNorm;
                            string st = _paxMeta.TryGetValue(bestNorm, out var mw) ? mw.name : bestNorm;
                            _paxLastEvent = $"{st}:  " + Tr(need == 1 ? "el andén está a la izquierda" : "el andén está a la derecha");
                            _paxLastEventUtc = DateTime.UtcNow;
                        }
                        return;
                    }
                }
                else
                {
                    var (hasDoors, open) = await FetchDoorsState();
                    if (hasDoors && !open) return;                          // puertas cerradas → no embarca
                    if (!hasDoors && !TrainStoppedByPosition(6)) return;    // sin mandos de puertas: esperar la parada
                }
                _paxDoorNeed = sidesKnown ? need : 0;
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
                // Estación de destino final de la Hoja de Ruta (actividad, horario o itinerario del usuario): el tren
                // termina aquí, así que bajan todos y no sube nadie.
                if (IsFinalStation(m.name ?? bestNorm)) { alight = _paxOnboard; board = 0; }
                _paxDone.Add(bestNorm);
                _paxVisits[bestNorm] = visit + 1;
                // Parada comercial: queda anotada (estación y hora del simulador) para la ventana del servicio.
                _svcStopsLog.Add(new StopRec { Station = m.name ?? bestNorm, Time = await FetchGameClock(), Utc = DateTime.UtcNow,
                                                Lat = _tHave ? _tLat : double.NaN, Lon = _tHave ? _tLon : double.NaN });
                SaveServiceJournal(force: true);
                // El intercambio se hace poco a poco (el HUD va mostrando cómo cambian las cifras).
                StartPaxAnimation(m.name ?? bestNorm, bestNorm, alight, board);
            }
            catch { }
            finally { _paxBusy = false; }
        }

        bool IsFinalStation(string station)
        {
            string fin = _road.FinalStation();
            if (string.IsNullOrEmpty(fin) || string.IsNullOrEmpty(station)) return false;
            string a = RoadBook.NormName(CleanStation(fin)), b = RoadBook.NormName(CleanStation(station));
            if (a.Length < 3 || b.Length < 3) return a == b && a.Length > 0;
            return a == b || a.Contains(b) || b.Contains(a);
        }

        async Task<double> FetchSpeedKmh()
        {
            try
            {
                string txt = await OrApi.GetStringAsync(_kmHttp, "/API/HUD/0");
                using var d = JsonDocument.Parse(txt);
                var vals = d.RootElement.GetProperty("commonTable").GetProperty("values");
                for (int k = 0; k + 2 < vals.GetArrayLength(); k += 3)
                    if (Array.Exists(HudSpeedLabels, x => string.Equals(HudClean(vals[k].GetString()), x, StringComparison.OrdinalIgnoreCase)))
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
                string txt = await OrApi.GetStringAsync(_kmHttp, "/API/CABCONTROLS");
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

        // Puertas abiertas por LADO, desde el puesto del maquinista (1 = izquierda, 2 = derecha).
        //  · Cabina con ORTS_LEFTDOOR y ORTS_RIGHTDOOR: Open Rails ya los da respecto al maquinista, aunque
        //    la locomotora vaya dada la vuelta o se conduzca desde la cabina de atrás.
        //  · Si no: la fila «Doors open: Left/Right» de la barra de datos de Open Rails (F5), que existe para
        //    cualquier tren y también va respecto al maquinista. Si la barra se entiende (idioma conocido) y
        //    no tiene esa fila, las puertas están cerradas.
        // known = false: no hay forma de saberlo (idioma de OR desconocido y cabina sin esos mandos).
        static readonly string[] HudDoorLabels = { "Doors open", "Abrir puertas", "Ouverture portes", "Türen offen", "Porte aperte", "Двери открыты", "Drzwi otwarte", "Otevřené dveře", "Ajtók nyitva" };
        static readonly string[] HudSpeedLabels = { "Speed", "Velocidad", "Vitesse", "Geschw", "Velocità", "Скорость", "Prędkość", "Rychlost", "Sebesség" };
        static readonly string[] HudLeftWords = { "Left", "Izquierda", "Gauche", "Links", "Sinistra", "Левые", "Lewy", "Vlevo", "Bal" };
        static readonly string[] HudRightWords = { "Right", "Derecha", "Droite", "Rechts", "Destra", "Правые", "Prawy", "Vpravo", "Jobb" };

        static string HudClean(string s) => (s ?? "").Trim().TrimEnd('?', '!', ' ', ':');

        async Task<(bool known, int open)> FetchDoorSides()
        {
            bool hasL = false, hasR = false; int open = 0;
            try
            {
                string txt = await OrApi.GetStringAsync(_kmHttp, "/API/CABCONTROLS");
                using var d = JsonDocument.Parse(txt);
                foreach (var c in d.RootElement.EnumerateArray())
                {
                    string tn = c.TryGetProperty("TypeName", out var t) ? t.GetString() : "";
                    bool on = c.TryGetProperty("RangeFraction", out var rf) && rf.GetDouble() >= 0.5;
                    if (tn == "ORTS_LEFTDOOR") { hasL = true; if (on) open |= 1; }
                    else if (tn == "ORTS_RIGHTDOOR") { hasR = true; if (on) open |= 2; }
                }
            }
            catch { }
            if (hasL && hasR) return (true, open);
            bool understood = false;
            try
            {
                string txt = await OrApi.GetStringAsync(_kmHttp, "/API/HUD/0");
                using var d = JsonDocument.Parse(txt);
                var vals = d.RootElement.GetProperty("commonTable").GetProperty("values");
                for (int k = 0; k + 2 < vals.GetArrayLength(); k += 3)
                {
                    string label = HudClean(vals[k].ValueKind == JsonValueKind.String ? vals[k].GetString() : "");
                    if (Array.Exists(HudSpeedLabels, x => string.Equals(label, x, StringComparison.OrdinalIgnoreCase))) understood = true;
                    if (!Array.Exists(HudDoorLabels, x => string.Equals(label, x, StringComparison.OrdinalIgnoreCase))) continue;
                    understood = true;
                    string v = vals[k + 2].ValueKind == JsonValueKind.String ? vals[k + 2].GetString() ?? "" : "";
                    foreach (var w in System.Text.RegularExpressions.Regex.Split(v, @"[^\p{L}]+"))
                    {
                        if (Array.Exists(HudLeftWords, x => string.Equals(w, x, StringComparison.OrdinalIgnoreCase))) open |= 1;
                        if (Array.Exists(HudRightWords, x => string.Equals(w, x, StringComparison.OrdinalIgnoreCase))) open |= 2;
                    }
                }
            }
            catch { }
            return (understood, open);
        }

        // ¿Tren parado? Por la POSICIÓN (independiente del idioma de OR): sin moverse más de ~0,8 m entre
        // sondeos durante al menos 3 s. Respaldo: velocidad del HUD de OR (solo si está en inglés, «Speed»).
        DateTime? _stoppedSinceUtc;
        bool TrainStoppedByPosition(double minSeconds = 3) =>
            _stoppedSinceUtc != null && (DateTime.UtcNow - _stoppedSinceUtc.Value).TotalSeconds >= minSeconds;

        // Hora del simulador «HH:mm» ("" si no se puede leer).
        async Task<string> FetchGameClock()
        {
            try
            {
                string txt = await OrApi.GetStringAsync(_kmHttp, "/API/TIME");
                if (double.TryParse(txt.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out double secs))
                {
                    int t = (int)secs % 86400; if (t < 0) t += 86400;
                    return $"{t / 3600:00}:{t % 3600 / 60:00}";
                }
            }
            catch { }
            return "";
        }

        async Task<int> FetchGameHour()
        {
            try
            {
                string txt = await OrApi.GetStringAsync(_kmHttp, "/API/TIME");
                if (double.TryParse(txt.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out double secs))
                    return ((int)(secs / 3600.0)) % 24;
            }
            catch { }
            return 12;
        }

        bool _kmPolling;   // una lectura de posición en marcha: el temporizador no lanza otra encima
        bool _paxPolling;  // un barrido de viajeros/megafonía en marcha
        async Task PollPaxGuarded(double lat, double lon)
        {
            _paxPolling = true;
            try { await PollPax(lat, lon); } catch { }
            finally { _paxPolling = false; }
        }
        Timer _inputWatch; // vigila F2/F9/clics en el simulador (OrControl.WatchUserInput)

        async Task PollKm()
        {
            if (OrControl.Quiet || _kmPolling) return;   // el usuario opera en el simulador, o la anterior aún no ha acabado
            _kmPolling = true;
            try { await PollKmCore(); }
            finally { _kmPolling = false; }
        }

        async Task PollKmCore()
        {
            try
            {
                var txt = await OrApi.GetStringAsync(_kmHttp, "/API/MAP/");
                if (string.IsNullOrWhiteSpace(txt) || txt == "null") return;
                using var d = JsonDocument.Parse(txt);
                if (!d.RootElement.TryGetProperty("LatLon", out var ll)) return;
                double lat = ll.GetProperty("Lat").GetDouble();
                double lon = ll.GetProperty("Lon").GetDouble();
                if (lat == 0 && lon == 0) return;
                // Primera posición válida = el escenario ya está abierto (OR no la da durante la carga):
                // aquí arranca el cronómetro del servicio.
                if (_svcClockUtc == null) StartSvcClock(DateTime.UtcNow);
                else await SvcClockTick();   // ¿Open Rails en pausa? (entonces el tiempo no cuenta)
                if (!_scenarioReady) { _scenarioReady = true; OnScenarioReady(); }   // y aquí aparecen los HUD
                double nowS = _kmClock.Elapsed.TotalSeconds;
                double dt = double.IsNaN(_kmLastFixS) ? 1.5 : Math.Max(1.0, nowS - _kmLastFixS);
                _kmLastFixS = nowS;
                if (_tHave)
                {
                    double dm = Haversine(_tLat, _tLon, lat, lon);
                    InfrCheckJump(dm);   // carné (A1): salto de posición
                    double maxM = PlausibleMeters(dt);
                    bool creible = dm <= maxM;
                    if (!creible && dm >= 0.5) { _kmRejected++; _kmRejectedM += dm; }
                    if (dm >= 0.5 && creible && maxM > 8)   // ignora el jitter, los saltos imposibles y lo que «se mueve» parado
                    {
                        _trackedMeters += dm;
                        if (_apOn) _apMeters += dm;   // A4: km con piloto automático (detalle de la infracción)
                        if (_paxOnboard > 0) _paxKm += _paxOnboard * dm / 1000.0;   // viajeros a bordo × km
                        // Con el HUD cerrado el rastro sigue grabándose (con él abierto lo graba el HUD, más fino).
                        if (!HudAlive) _driveTrail.Add(lat, lon);
                        SvcTrailAdd(lat, lon);   // rastro del servicio (mapa del informe)
                    }
                    // Parado = casi sin desplazamiento entre sondeos (~1,5 s): < 0,8 m ≈ < 2 km/h.
                    if (dm < 0.8) { if (_stoppedSinceUtc == null) _stoppedSinceUtc = DateTime.UtcNow; }
                    else _stoppedSinceUtc = null;
                }
                SmoothFix(lat, lon);   // mapas del HUD: la marca del tren se desliza hasta aquí
                _tLat = lat; _tLon = lon; _tHave = true;
                if (CabHudAlive) _cabHud.SetPosition(lat, lon);   // pupitre: para saber si es de noche
                // Embarque de viajeros y megafonía: aparte, para que sus consultas en las estaciones no retrasen la
                // siguiente lectura de posición (el HUD y el mini-mapa se quedaban parados).
                if (_paxActive && _paxStations.Count > 0 && !_paxPolling) _ = PollPaxGuarded(lat, lon);
                if (InfrActive) _ = InfrSpeedPoll();   // carné (B6/B7): velocidad frente al límite
                if (_pendingServiceId != null) _ = AutopilotPoll();   // carné (A4): ¿conduce Open Rails?
                if (_pendingServiceId != null) SaveServiceJournal();   // por si se cierra todo de golpe
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
            StopLiveMap();   // mapa en vivo: desaparezco del mapa de los demás
            RoadDriveStop(); // hoja de ruta: se cierra con la conducción
            try { _kmTimer?.Stop(); _kmTimer?.Dispose(); _kmTimer = null; } catch { }
            try { _inputWatch?.Stop(); _inputWatch?.Dispose(); _inputWatch = null; } catch { }
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
            var trainKm = new Dictionary<string, double>();
            var routeKm = new Dictionary<string, double>();
            double totalKm = 0, totalS = 0, totalPax = 0; int trips = 0, invalid = 0;

            void Tally(string route, string consist, double km, double dur, double pax, bool valid, bool completed)
            {
                if (completed && !valid) invalid++;
                if (!valid || !completed) return;
                trips++; totalKm += km; totalS += dur; totalPax += pax;
                if (!string.IsNullOrWhiteSpace(consist)) { trainCount[consist] = trainCount.TryGetValue(consist, out var a) ? a + 1 : 1; trainKm[consist] = (trainKm.TryGetValue(consist, out var ak) ? ak : 0) + km; }
                if (!string.IsNullOrWhiteSpace(route)) { routeCount[route] = routeCount.TryGetValue(route, out var b) ? b + 1 : 1; routeKm[route] = (routeKm.TryGetValue(route, out var bk) ? bk : 0) + km; }
            }

            var tLic = LoadLicense();   // carné por puntos (a la vez que los servicios)
            LoadMyPhoto();
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

            var __pf = Perf.T("Mi perfil · cifras");
            // KPIs
            double hours = totalS / 3600.0;
            double avg = totalS > 0 ? totalKm / hours : 0;
            SetKpi(_profKmVal, totalKm.ToString("N0", EsEs) + " km");
            SetKpi(_profTripsVal, trips.ToString());
            SetKpi(_profTimeVal, hours.ToString("N1", EsEs) + " h");
            SetKpi(_profSpeedVal, avg.ToString("N0", EsEs) + " km/h");
            SetKpi(_profPaxVal, totalPax.ToString("N0", EsEs));

            __pf.Dispose();
            try { await tLic; } catch { }
            using (Perf.T("Mi perfil · rango e insignias"))
            UpdateRankAndBadges(totalKm, trips, hours, invalid, routeCount.Count, totalPax, trainCount.Count, avg);
            var __pt = Perf.T("Mi perfil · trenes y rutas");

            // Trenes más usados (con la vista 2D de su máquina, si la tienes) y rutas más recorridas
            var tops = new List<KeyValuePair<string, double>>(trainCount);
            tops.Sort((x, y) => y.Value.CompareTo(x.Value));
            var trains = new List<(string, string, string, string)>();
            foreach (var kv in tops)
            {
                if (trains.Count >= 4) break;
                string path = null;
                foreach (var c in _consistsAll ?? new List<TrainItem>())
                    if (c != null && string.Equals(c.Name, kv.Key, StringComparison.OrdinalIgnoreCase)) { path = c.Locomotive?.FilePath; break; }
                trains.Add((kv.Key, (trainKm.TryGetValue(kv.Key, out var tk) ? tk : 0).ToString("N0", EsEs) + " km", kv.Value.ToString("N0", EsEs) + "×", path));
            }
            _profTrains?.SetItems(trains);
            var rts = new List<KeyValuePair<string, double>>(routeKm);
            rts.Sort((x, y) => y.Value.CompareTo(x.Value));
            double maxKm = rts.Count > 0 ? Math.Max(1, rts[0].Value) : 1;
            var routes = new List<(string, string, double)>();
            foreach (var kv in rts)
            {
                if (routes.Count >= 5) break;
                int n = routeCount.TryGetValue(kv.Key, out var rc) ? (int)rc : 0;
                routes.Add((kv.Key, kv.Value.ToString("N0", EsEs) + " km  ·  " + string.Format(Tr(n == 1 ? "{0} viaje" : "{0} viajes"), n), kv.Value / maxKm));
            }
            _profRoutes?.SetItems(routes);
            __pt.Dispose();
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
            // Rango: mayor tramo alcanzado + progreso al siguiente. Con el carné por debajo de 6 puntos,
            // congelado en los km que tenía al bajar (las insignias siguen contando todos los km).
            double kmBadges = km;
            bool frozen = !double.IsNaN(_licFrozenKm) && _licFrozenKm < km - 0.01;
            km = RankKm(km);
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
            if (frozen && _rankNext != null) _rankNext.Text = "❄ " + Tr("Rango congelado: el carné está por debajo de 6 puntos.") + "  " + _rankNext.Text;
            _rankTrack?.Invalidate();
            if (_profHero != null)
            {
                _profHero.Name_ = string.IsNullOrWhiteSpace(Supa.Username) ? "—" : Supa.Username;
                var meta = new List<string>();
                if (_empSel != null) meta.Add((_myRole != null ? TrRole(_myRole) + " · " : "") + _empSel.Name);
                if (!string.IsNullOrEmpty(Supa.UserId)) meta.Add("ID " + Supa.UserId.Substring(0, Math.Min(8, Supa.UserId.Length)));
                _profHero.Meta = string.Join("  ·  ", meta);
                _profHero.Rank = _rankName?.Text ?? "—"; _profHero.Step = _rankStep?.Text ?? ""; _profHero.Next = _rankNext?.Text ?? "";
                _profHero.Pct = _rankPct; _profHero.Frozen = frozen;
                _profHero.CanEditPhoto = Supa.IsLoggedIn;
                _profHero.Invalidate();
            }
            km = kmBadges;

            // Insignias (cada una con su color)
            if (_badgeGrid != null)
            {
                // Se reúnen primero y solo se rehacen si ha cambiado alguna: rehacerlas en cada recarga
                // (refresco en vivo) hacía parpadear todo el panel.
                var lista = new List<(string icon, string text, bool earned, Color color, double prog)>();
                void Badge(string icon, string text, bool earned, Color color, double prog = 0) => lista.Add((icon, text, earned, color, earned ? 1 : Math.Max(0, Math.Min(0.99, prog))));
                // Servicios
                Badge("🚂", Tr("Primer viaje"), trips >= 1, ColBlue, trips / 1.0);
                Badge("🏅", Tr("10 servicios"), trips >= 10, ColGold, trips / 10.0);
                Badge("🎖️", Tr("50 servicios"), trips >= 50, ColGold, trips / 50.0);
                Badge("🏆", Tr("100 servicios"), trips >= 100, ColOrange, trips / 100.0);
                Badge("👑", Tr("250 servicios"), trips >= 250, ColOrange, trips / 250.0);
                Badge("💎", Tr("500 servicios"), trips >= 500, ColViolet, trips / 500.0);
                Badge("🌟", Tr("1.000 servicios"), trips >= 1000, ColGold, trips / 1000.0);
                // Distancia
                Badge("📐", Tr("100 km"), km >= 100, Theme.Accent, km / 100.0);
                Badge("📏", Tr("1.000 km"), km >= 1000, Theme.Accent, km / 1000.0);
                Badge("🌍", Tr("10.000 km"), km >= 10000, ColTeal, km / 10000.0);
                Badge("🛰", Tr("50.000 km"), km >= 50000, ColTeal, km / 50000.0);
                Badge("🚀", Tr("100.000 km"), km >= 100000, ColBlue, km / 100000.0);
                // Tiempo al mando
                Badge("⏱", Tr("10 horas"), hours >= 10, ColViolet, hours / 10.0);
                Badge("🕰", Tr("100 horas"), hours >= 100, ColViolet, hours / 100.0);
                Badge("📅", Tr("500 horas"), hours >= 500, ColViolet, hours / 500.0);
                Badge("♾", Tr("1.000 horas"), hours >= 1000, ColGold, hours / 1000.0);
                // Rutas y material
                Badge("🗺", Tr("Explorador (5 rutas)"), distinctRoutes >= 5, ColOrange, distinctRoutes / 5.0);
                Badge("🧭", Tr("Explorador (10 rutas)"), distinctRoutes >= 10, ColOrange, distinctRoutes / 10.0);
                Badge("🌐", Tr("Explorador (25 rutas)"), distinctRoutes >= 25, ColTeal, distinctRoutes / 25.0);
                Badge("🚆", Tr("10 trenes distintos"), distinctTrains >= 10, ColBlue, distinctTrains / 10.0);
                Badge("🚄", Tr("25 trenes distintos"), distinctTrains >= 25, ColBlue, distinctTrains / 25.0);
                // Viajeros
                Badge("🧍", Tr("1.000 viajeros"), pax >= 1000, ColTeal, pax / 1000.0);
                Badge("👥", Tr("10.000 viajeros"), pax >= 10000, ColTeal, pax / 10000.0);
                Badge("🏙", Tr("100.000 viajeros"), pax >= 100000, ColGold, pax / 100000.0);
                // Estilo de conducción
                Badge("✅", Tr("Conducción limpia"), trips >= 5 && invalid == 0, Theme.Accent);
                Badge("🛡", Tr("Impecable (50 servicios)"), trips >= 50 && invalid == 0, Theme.Accent);
                Badge("⚡", Tr("Alta velocidad (media 100 km/h)"), trips >= 10 && avgKmh >= 100, ColOrange);
                var firma = new System.Text.StringBuilder();
                foreach (var x in lista) firma.Append(x.text).Append(x.earned ? '1' : '0').Append('|');
                foreach (var x in lista) firma.Append(((int)(x.prog * 20)).ToString());
                if (firma.ToString() != _badgesSig && _badgeGrid != null)
                {
                    _badgesSig = firma.ToString();
                    var items = new List<(string, string, bool, Color, double)>();
                    foreach (var x in lista) items.Add((x.icon, x.text, x.earned, x.color, x.prog));
                    _badgeGrid.SetItems(items);
                    int got = lista.FindAll(x => x.earned).Count;
                    if (_badgesHeader != null) _badgesHeader.Text = Tr("INSIGNIAS") + "  ·  " + string.Format(Tr("{0} de {1}"), got, lista.Count);
                }
            }
        }
        string _badgesSig;   // insignias que se ven ahora (para no rehacerlas si no cambian)
        Label _badgesHeader;

        // Insignia: pastilla con icono a color (blanco sobre disco del color) + texto.
        // Conseguida = disco a color + fondo teñido + texto claro; bloqueada = disco gris + texto atenuado.
        static Control BadgeChip(string icon, string text, bool earned, Color color) => new BadgeChipControl(icon, text, earned, color);

        sealed class BadgeChipControl : Control
        {
            const int H = 40, PadL = 7, DotCol = 30, Dot = 26, Gap = 6, PadR = 12, Radius = 11;
            static readonly Font FBold = Theme.Font(9f, FontStyle.Bold), FReg = Theme.Font(9f, FontStyle.Regular);
            static Font _emoji;
            static Font Emoji => _emoji ??= new Font("Segoe UI Emoji", Dot * 0.46f * Theme.DpiComp);
            readonly string _icon, _text; readonly bool _earned; readonly Color _fill, _border, _dot, _fore;

            public BadgeChipControl(string icon, string text, bool earned, Color color)
            {
                _icon = icon; _text = text; _earned = earned;
                _dot = earned ? color : Blend(Theme.Surface2, Theme.Bg, 0.3f);
                _fill = earned ? Blend(Theme.Surface, color, 0.14f) : Theme.Surface;
                _border = earned ? Blend(color, Theme.Bg, 0.15f) : Blend(Theme.Surface, Theme.Bg, 0.35f);
                _fore = earned ? Theme.Text : Theme.Subtle;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                Margin = new Padding(0, 0, 8, 8);
                Size = new Size(DotCol + Gap + TextRenderer.MeasureText(text, earned ? FBold : FReg).Width + 24, H);
                AccessibleName = text;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; var r = ClientRectangle;
                using (var bg = new SolidBrush(Theme.ResolveBg(this))) g.FillRectangle(bg, r);
                Theme.FillRound(g, r, Radius, _fill);
                Theme.DrawRoundBorder(g, r, Radius, _border);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var dr = new Rectangle(PadL, (H - Dot) / 2, Dot, Dot);
                using (var path = Theme.Round(dr, Dot / 3)) using (var b = new SolidBrush(_dot)) g.FillPath(b, path);
                TextRenderer.DrawText(g, _icon, Emoji, dr, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                int tx = PadL + DotCol + Gap;
                TextRenderer.DrawText(g, _text, _earned ? FBold : FReg, new Rectangle(tx, 0, Math.Max(1, Width - tx - 4), H), _fore,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
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

            // Usuarios en tarjetas (nombre, ID, clave de recuperación, empresas y alta). Doble clic: copia la clave.
            _usersList = new UserCardList { Dock = DockStyle.Fill, Margin = new Padding(2, 4, 2, 4) };
            var usearch = new RoundedInput(I18n.T("🔎  Filtrar…")) { Width = 300, Height = 34, Anchor = AnchorStyles.Left, Margin = new Padding(2, 2, 2, 4) };
            usearch.Box.TextChanged += (s, e) => { _usersQuery = usearch.Box.Text.Trim().ToLowerInvariant(); FillUserCards(); };
            t.Controls.Add(usearch);
            _usersList.ItemActivated += o => AdminCopyRecoveryKey();
            t.Controls.Add(_usersList);

            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(2, 4, 2, 2) };
            var copyBtn = EmpButton(Tr("⧉  Copiar ID")); copyBtn.Width = 160; copyBtn.Margin = new Padding(0, 0, 8, 0);
            copyBtn.Click += (s, e) => { int i = SelectedUserIndex(); if (i >= 0 && i < _userIds.Count) CopyIdToClipboard(_userIds[i]); else Msg(_usersMsg, Tr("Selecciona un usuario de la lista."), true); };
            var delBtn = EmpButton(Tr("Eliminar acceso")); delBtn.Width = 200; delBtn.Margin = new Padding(0);   // alineado con «Copiar ID»
            delBtn.BaseColor = Theme.Surface2; delBtn.HoverColor = Color.FromArgb(150, 60, 60); delBtn.TextColor = RedC;
            delBtn.Click += (s, e) => DeleteUserAccess();
            var keyBtn = EmpButton(Tr("🗝  Copiar clave")); keyBtn.Width = 170; keyBtn.Margin = new Padding(0, 0, 8, 0);
            keyBtn.Click += (s, e) => AdminCopyRecoveryKey();
            var newKeyBtn = EmpButton(Tr("Nueva clave")); newKeyBtn.Width = 150; newKeyBtn.Margin = new Padding(0, 0, 8, 0);
            newKeyBtn.Click += (s, e) => AdminNewRecoveryKey();
            btns.Controls.Add(copyBtn); btns.Controls.Add(keyBtn); btns.Controls.Add(newKeyBtn); btns.Controls.Add(delBtn);
            t.Controls.Add(btns);

            _usersMsg = EmpMsg(); t.Controls.Add(_usersMsg);
            return t;
        }

        async void LoadUsers()
        {
            if (_usersList == null || !Supa.IsSuperadmin) return;
            if (_usersAll.Count == 0) { _usersList.EmptyText = Tr("Cargando…"); _usersList.Invalidate(); }
            var (json, err) = await Supa.RpcAsync("list_all_users", new { });
            _userIds.Clear(); _userIsSelf.Clear(); _userKeys.Clear(); _usersAll.Clear();
            if (err != null) { _usersList.EmptyText = Tr("Error: ") + err; FillUserCards(); return; }
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
                    string key = Str(e, "recovery_key");
                    _userKeys.Add(key);
                    _usersAll.Add(new UserItem
                    {
                        Index = _userIds.Count - 1, Id = uid, Name = user, Email = Str(e, "email"), Key = key, Self = self,
                        Companies = (int)Num(e, "company_count"),
                        // usuarios-carne-empresas.sql: puntos del carné y nombres de sus empresas (sin el SQL, no salen)
                        Points = e.TryGetProperty("license_points", out var lp) && lp.ValueKind == JsonValueKind.Number ? lp.GetInt32() : -1,
                        SuspendedUntil = DateTime.TryParse(Str(e, "suspended_until"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var su) ? su : null,
                        CompanyNames = e.TryGetProperty("company_names", out var cn) && cn.ValueKind == JsonValueKind.Array
                            ? System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(System.Linq.Enumerable.Select(cn.EnumerateArray(), x => x.GetString() ?? ""), x => x.Length > 0)) : new List<string>(),
                        Created = DateTimeOffset.TryParse(Str(e, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ca) ? ca.LocalDateTime : DateTime.MinValue
                    });
                }
            }
            catch { }
            _usersList.EmptyText = Tr("No hay usuarios.");
            FillUserCards();
        }

        // Tarjetas de usuarios según el filtro (la elegida se mantiene si sigue a la vista).
        void FillUserCards()
        {
            if (_usersList == null) return;
            var words = _usersQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _usersList.BeginUpdate();
            _usersList.Items.Clear();
            foreach (var u in _usersAll)
                if (System.Linq.Enumerable.All(words, w => u.SearchText.Contains(w))) _usersList.Items.Add(u);
            _usersList.EndUpdate();
        }

        // Usuario elegido: su posición en _userIds / _userIsSelf / _userKeys (−1 = ninguno).
        int SelectedUserIndex() => (_usersList?.SelectedItem as UserItem)?.Index ?? -1;

        async void DeleteUserAccess()
        {
            if (!Supa.IsSuperadmin || _usersList == null) return;
            int i = SelectedUserIndex();
            if (i < 0 || i >= _userIds.Count) { Msg(_usersMsg, Tr("Selecciona un usuario de la lista."), true); return; }
            if (i < _userIsSelf.Count && _userIsSelf[i]) { Msg(_usersMsg, Tr("No puedes eliminar tu propio acceso."), true); return; }
            if (ThemedBox.Show(this,
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
            // Una tarjeta por empresa: logotipo, nombre, ID, gerente, socios, servicios y saldo.
            _allCompList = EmpCards();
            t.Controls.Add(EmpSearch(_allCompList, 300));
            _allCompList.TitleCol = 0; _allCompList.PillCol = 1; _allCompList.SubCols = new[] { 2, 3, 4 }; _allCompList.RightCol = 5;
            _allCompList.Formats[2] = Tr("Gerente: {0}"); _allCompList.Formats[3] = Tr("{0} socio") + "|" + Tr("{0} socios"); _allCompList.Formats[4] = Tr("{0} servicio") + "|" + Tr("{0} servicios");
            _allCompList.CardHeight = 74; _allCompList.MinWidth = 520; _allCompList.Columns = 2;
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
            _allCompList.ShowLoading(Tr("Cargando…"));
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
                        new[] { name, Str(e, "code").Length > 0 ? Str(e, "code") : "—", owner, mem.ToString("N0", EsEs), svc.ToString("N0", EsEs), bal.ToString("N2", EsEs) + " €" },
                        new Color?[] { null, Theme.AccentHi, Theme.Subtle, null, null, bal < 0 ? RedC : Theme.Accent },
                        LogoFor(Str(e, "id"), Str(e, "logo")) ?? PlaceholderLogo(name));
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
            if (ThemedBox.Show(this,
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
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));     // rótulo · estados · buscador
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));     // los trenes
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 260));    // el elegido
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));         // msg

            // ---- arriba, como en Conducción libre: rótulo, pastillas de estado y buscador ----
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _fleetHeaderLbl = new Label { Text = Tr("TRENES DE LA EMPRESA"), AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.Subtle, Font = Theme.Font(8f, FontStyle.Bold), Margin = new Padding(0, 0, 12, 0) };
            _fleetTabs = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 4, 0, 0) };
            string[] states = { "Todos", "Disponibles", "En servicio" };   // sin «En taller»: el taller es automático
            for (int k = 0; k < states.Length; k++)
            {
                int idx = k;
                var b = BankChip(Tr(states[k]), k == 0);
                b.Click += (s2, e2) => { SetChipActive(_fleetTabs, idx); _fleetCards.StateFilter = idx - 1; };
                _fleetTabs.Controls.Add(b);
            }
            var fleetSearch = new RoundedInput(I18n.T("🔎  Matrícula o tren…")) { Width = 240, Height = 34, Anchor = AnchorStyles.Right, Margin = new Padding(10, 5, 0, 5) };
            fleetSearch.Box.TextChanged += (s2, e2) => _fleetCards.Filter(fleetSearch.Box.Text);
            bar.Controls.Add(_fleetHeaderLbl, 0, 0); bar.Controls.Add(_fleetTabs, 1, 0); bar.Controls.Add(fleetSearch, 2, 0);
            t.Controls.Add(bar, 0, 0);

            // ---- la lista: una línea por tren, con su composición ----
            _thumbs ??= new VehicleThumbs(this);
            _fleetCards = new FleetCardList { Dock = DockStyle.Fill, Thumbs = _thumbs, BackColor = Theme.Bg, Margin = new Padding(0, 4, 0, 0) };
            _fleetCards.SelectionChanged += OnFleetVehicleSelected;
            _fleetCards.MarksChanged += OnFleetVehicleSelected;   // varios a la vez (Ctrl / Mayús)
            _fleetCards.InfoClicked += m => ShowFleetTrainBreakdown(m);
            t.Controls.Add(_fleetCards, 0, 1);

            // ---- abajo: el tren elegido (vista 3D · composición 2D · casillas y acciones) ----
            var sel = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, Radius = 12, Padding = new Padding(10, 8, 14, 10), Margin = new Padding(0, 10, 0, 0) };
            var selGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface, Margin = new Padding(0) };
            selGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
            selGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            selGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _fleetOwnPreview = new TrainPreviewPanel { Dock = DockStyle.Fill };
            _fleetOwnPreview.ViewerSource = () => _fleetOwnGeom == null ? null : new ShapeViewSource
                { Geom = _fleetOwnGeom, Flip = _fleetOwnFlip, BaseDistance = FleetOwnCamDistance, Yaw = _fleetOwnYaw, Pitch = _fleetOwnPitch, Caption = _fleetOwnPreview.Caption };
            _fleetOwnPreview.Dragged += OnFleetOwnDrag;
            _fleetOwnPreview.ResetRequested += OnFleetOwnReset;
            _fleetOwnPreview.Zoomed += () => { if (_fleetOwnGeom != null) RenderFleetOwnLive(); };
            _fleetOwnRerender = new System.Windows.Forms.Timer { Interval = 140 };
            _fleetOwnRerender.Tick += (s2, e2) => { _fleetOwnRerender.Stop(); RenderFleetOwnLive(); };
            _fleetOwnPreview.Resize += (s2, e2) => { if (_fleetOwnGeom != null) { _fleetOwnRerender.Stop(); _fleetOwnRerender.Start(); } };
            _fleetOwnView = new VehicleViewport(_fleetOwnPreview) { Dock = DockStyle.Fill, Thumbs = _thumbs, Margin = new Padding(0, 0, 14, 0) };
            _fleetOwnView.ModeChanged += on => { if (on) RenderFleetOwn(_fleetOwnEngPath); };
            _fleetOwnView.Set3D(true); _fleetOwnView.ToggleShown = false;   // como en Conducción libre: la máquina de cabeza en 3D
            selGrid.Controls.Add(_fleetOwnView, 0, 0);

            var info = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = new Padding(0) };
            // cabecera: TREN ELEGIDO · nombre (estado) y, a la derecha, las acciones
            var headRow = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Surface };
            var lblSel = new Label { Text = Tr("TREN ELEGIDO"), Dock = DockStyle.Left, AutoSize = false, Width = 120, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Subtle, Font = Theme.Font(8f, FontStyle.Bold) };
            lblSel.Width = TextRenderer.MeasureText(lblSel.Text, lblSel.Font).Width + 12;
            _fleetOwnTitle = new OneLineLabel { Text = Tr("Elige un tren"), Dock = DockStyle.Fill, AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Accent, Font = Theme.Font(11f, FontStyle.Bold) };
            var acts = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Surface, Padding = new Padding(0, 3, 0, 0) };
            RoundButton Act(string text, bool primary = false)
            {
                var b = EmpButton(Tr(text), primary); b.Height = 32; b.Margin = new Padding(8, 0, 0, 0);
                using var fb = Theme.Font(9.5f, FontStyle.Bold); b.Width = TextRenderer.MeasureText(b.Text, fb).Width + 36;
                return b;
            }
            _fleetRemoveBtn = Act("Dar de baja"); _fleetRemoveBtn.BaseColor = Theme.Surface2; _fleetRemoveBtn.HoverColor = Color.FromArgb(150, 60, 60); _fleetRemoveBtn.TextColor = RedC;
            _fleetRemoveBtn.Click += (s2, e2) => RemoveVehicle();
            _fleetPlateBtn = Act("Asignar matrícula"); _fleetPlateBtn.BaseColor = Theme.Surface2; _fleetPlateBtn.HoverColor = Theme.SurfaceHi; _fleetPlateBtn.TextColor = Theme.Text;
            _fleetPlateBtn.Click += (s2, e2) => AssignPlateUi();
            // sin «Llevar al taller»: el mantenimiento se hace solo al cumplir los km (taller-automatico.sql)
            _fleetAddConBtn = Act("Añadir a mi contenido"); _fleetAddConBtn.Visible = false; _fleetAddConBtn.Click += (s2, e2) => AddFleetTrainToContent();
            _fleetSeatsBtn = Act("Plazas…"); _fleetSeatsBtn.Visible = false; _fleetSeatsBtn.Click += (s2, e2) => SetTrainSeatsUi();
            _fleetInfoBtn = Act("ⓘ  Desglose del precio"); _fleetInfoBtn.Visible = false;
            _fleetInfoBtn.Click += (s2, e2) => { int i = FleetSelectedRow(); if (i >= 0 && i < _fleetRowTrain.Count && _fleetTrainModels.TryGetValue(_fleetRowTrain[i], out var m)) ShowFleetTrainBreakdown(m); };
            acts.Controls.AddRange(new Control[] { _fleetRemoveBtn, _fleetPlateBtn, _fleetSeatsBtn, _fleetAddConBtn, _fleetInfoBtn });
            headRow.Controls.Add(_fleetOwnTitle); headRow.Controls.Add(acts); headRow.Controls.Add(lblSel);
            // lo que lleva el tren; debajo, todos los datos útiles del ejemplar (dos filas de seis casillas)
            _fleetOwnSub = new Label { Dock = DockStyle.Top, Height = 24, ForeColor = Theme.Subtle, Font = Theme.Font(9f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            // casillas: las mismas que en Compra (SpecTiles); cada valor se guarda en su etiqueta y se pinta desde ellas
            (string cap, Label lbl)[] tiles =
            {
                ("TIPO", _voKind = new Label()), ("ESTADO", _voEstado = new Label()), ("PROPIEDAD", _voProp = new Label()), ("VALOR", _voValue = new Label()),
                ("PRÓXIMO TALLER", _voMaint = new Label()), ("KM TOTALES", _voKm = new Label()), ("PLAZAS", _voCap = new Label()), ("CONFORT", _voComfort = new Label()),
                ("TRACCIÓN", _voTraction = new Label()), ("POTENCIA", _voPower = new Label()), ("VEL. MÁXIMA", _voSpeed = new Label()), ("MASA", _voMass = new Label()),
            };
            _fleetSpecs = new SpecTiles { Dock = DockStyle.Top, Height = 0, Columns = 6 };
            void Repaint() { _fleetSpecs.ValueColors.Clear(); _fleetSpecs.ValueColors[1] = _voEstado.ForeColor; var items = new List<(string, string)>(); foreach (var x in tiles) items.Add((Tr(x.cap), string.IsNullOrEmpty(x.lbl.Text) ? "—" : x.lbl.Text)); _fleetSpecs.SetItems(items); }
            foreach (var (_, lbl) in tiles) { lbl.Text = "—"; lbl.ForeColor = Theme.Text; lbl.TextChanged += (s2, e2) => Repaint(); lbl.ForeColorChanged += (s2, e2) => Repaint(); }
            Repaint();
            info.Controls.Add(_fleetSpecs);
            info.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 4, BackColor = Theme.Surface });
            info.Controls.Add(_fleetOwnSub);
            info.Controls.Add(headRow);
            selGrid.Controls.Add(info, 1, 0);
            sel.Controls.Add(selGrid);
            t.Controls.Add(sel, 0, 2);

            _fleetMsg = EmpMsg(); t.Controls.Add(_fleetMsg, 0, 3);
            return t;
        }


        // Las casillas de más de la ficha de Flota (las de siempre siguen en _voKind…_voTraction).
        RoundButton _fleetInfoBtn, _fleetSeatsBtn;
        SpecTiles _fleetSpecs;
        Label _fleetOwnSub, _voValue, _voKm, _voComfort, _voPower, _voSpeed, _voMass;

        static string[] JoinDetail(params string[][] parts) { var l = new List<string>(); foreach (var x in parts) l.AddRange(x); return l.ToArray(); }

        // Los datos de más de una fila de la flota: valor, km totales, confort, potencia, velocidad y masa.
        static string[] FleetExtraDetail(JsonElement e)
        {
            double price = Num(e, "price"), km = Num(e, "km_total"), comfort = Num(e, "comfort"), kw = Num(e, "power_kw"), kmh = Num(e, "max_speed_kmh"), mass = Num(e, "mass_t");
            return new[]
            {
                price > 0 ? price.ToString("N0", EsEs) + " €" : "—", km.ToString("N0", EsEs) + " km", comfort > 0 ? comfort.ToString("N0", EsEs) + "/100" : "—",
                kw > 0 ? kw.ToString("N0", EsEs) + " kW" : "—", kmh > 0 ? kmh.ToString("N0", EsEs) + " km/h" : "—", mass > 0 ? mass.ToString("N0", EsEs) + " t" : "—"
            };
        }

        // Varios ejemplares elegidos: cuántos y de qué trenes; taller, baja y «Añadir a mi contenido» actúan sobre todos.
        void ShowFleetMulti()
        {
            var ids = _fleetCards.SelectedIds;
            var rows = new List<int>(); foreach (var id in ids) { int r = _fleetIds.IndexOf(id); if (r >= 0) rows.Add(r); }
            var trains = new HashSet<string>(); int inUse = 0, due = 0;
            foreach (var r in rows)
            {
                if (r < _fleetRowTrain.Count && _fleetRowTrain[r].Length > 0) trains.Add(_fleetRowTrain[r]);
                if (r < _fleetStatus.Count) { if (_fleetStatus[r] == "in_use") inUse++; else if (_fleetStatus[r] == "maintenance_due") due++; }
            }
            _fleetOwnTitle.Text = string.Format(Tr("{0} ejemplares elegidos"), rows.Count);
            if (_fleetOwnSub != null) _fleetOwnSub.Text = Tr("«Dar de baja» y «Añadir a mi contenido» actúan sobre todos  ·  Esc: quitar la selección");
            if (_voKind != null) _voKind.Text = string.Format(trains.Count == 1 ? Tr("{0} tren") : Tr("{0} trenes"), trains.Count);
            if (_voEstado != null) { _voEstado.Text = string.Format(Tr("{0} libres"), rows.Count - inUse - due); _voEstado.ForeColor = Theme.Accent; }
            if (_voProp != null) _voProp.Text = string.Format(Tr("{0} en servicio"), inUse);
            if (_voMaint != null) _voMaint.Text = "—";
            foreach (var l in new[] { _voCap, _voTraction, _voValue, _voKm, _voComfort, _voPower, _voSpeed, _voMass }) if (l != null) l.Text = "—";
            foreach (var b in new[] { _fleetPlateBtn, _fleetSeatsBtn, _fleetInfoBtn }) if (b != null) { b.Enabled = false; b.Invalidate(); }
            if (_fleetAddConBtn != null) { bool falta = false; foreach (var t in trains) if (!_trainCon.ContainsKey(t)) falta = true; _fleetAddConBtn.Visible = falta; }
        }

        // Los elegidos en Flota (uno o varios): (fila, id).
        List<(int row, string id)> FleetSelection()
        {
            var l = new List<(int, string)>();
            foreach (var id in _fleetCards?.SelectedIds ?? new List<string>()) { int r = _fleetIds.IndexOf(id); if (r >= 0) l.Add((r, id)); }
            return l;
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
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));   // tarjetas
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));   // visor + ficha

            // --- IZQUIERDA: buscador + lista de trenes (rellena toda la altura disponible) ---
            var pickerHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Surface, Margin = new Padding(0, 0, 12, 0) };
            pickerHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pickerHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // tipos
            pickerHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // buscador
            pickerHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // tarjetas
            _buyKindTabs = MakeSubTabs(new[] { "Todas", "En tu flota", "Automotores", "Locomotoras" }, i => { _buyKind = i; FilterBuyList(); });
            _buyKindTabs.Dock = DockStyle.Top; _buyKindTabs.BackColor = Theme.Surface; _buyKindTabs.Margin = new Padding(0, 0, 0, 6);
            pickerHost.Controls.Add(_buyKindTabs, 0, 0);
            // Una sola lista: las MÁQUINAS (.eng) del contenido. Los trenes (.con) los tiene cada
            // uno distintos; las máquinas suelen ser las mismas, así que la compra va por máquina.
            _fleetConsistSearch = new RoundedInput(I18n.T("🔎  Buscar máquina por nombre o carpeta…")) { Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 6) };
            _fleetConsistSearch.Box.TextChanged += (s, e) => FilterBuyList();
            pickerHost.Controls.Add(_fleetConsistSearch, 0, 1);

            // La lista de trenes (.con) ya no se muestra, pero se sigue creando: el desglose de la
            // tasación y el aviso de «ya la tienes» trabajan sobre los consists del contenido.
            _fleetConsistList = MakeListBox(Theme.Surface2, 30);

            // Tarjetas con la vista 2D de cada máquina: datos y precio al aparecer en pantalla.
            _thumbs ??= new VehicleThumbs(this);
            _fleetEngList = new BuyCardGrid
            {
                Thumbs = _thumbs, BackColor = Theme.Surface,
                PathOf = o => (o as BuyMachine)?.Path, TitleOf = o => (o as BuyMachine)?.Name,
                FolderOf = o => (o as BuyMachine)?.Folder, CarsOf = o => (o as BuyMachine)?.UnitCars ?? 1,
                OwnedOf = o => o is BuyMachine bm ? OwnedUnits(bm.Folder, bm.Name) : 0,
                InfoOf = o => MachineInfoAsync(o as BuyMachine)
            };
            _fleetEngList.SelectedIndexChanged += (s, e) =>
            {
                if (SkipBuyHeader()) return;   // las cabeceras no se seleccionan
                RenderBuyMachinePreview(); UpdateMachineValuation();
            };
            var engCard = WrapCard(_fleetEngList); engCard.Dock = DockStyle.Fill; engCard.Fill = Theme.Surface; engCard.Padding = new Padding(0);
            _buyEngCard = engCard;
            pickerHost.Controls.Add(engCard, 0, 2);
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
            _fleetPreview.ViewerSource = () => _fleetGeom == null ? null : new ShapeViewSource
                { Geom = _fleetGeom, Flip = _fleetFlip, BaseDistance = _fleetPreview.BaseDistance, Yaw = _fleetYaw, Pitch = _fleetPitch, Caption = _fleetPreview.Caption };
            _fleetPreview.Dragged += OnFleetPreviewDrag;
            _fleetPreview.ResetRequested += OnFleetPreviewReset;
            _fleetPreview.Zoomed += () => { if (_fleetGeom != null) RenderFleetLive(); };
            _fleetRerender = new System.Windows.Forms.Timer { Interval = 140 };
            _fleetRerender.Tick += (s, e) => { _fleetRerender.Stop(); RenderFleetLive(); };
            _fleetPreview.Resize += (s, e) => { if (_fleetGeom != null) { _fleetRerender.Stop(); _fleetRerender.Start(); } };
            _buyView = new VehicleViewport(_fleetPreview) { Dock = DockStyle.Fill, Thumbs = _thumbs };
            _buyView.ModeChanged += on => { if (on) { _fleetPrevEngPath = null; RenderBuyMachinePreview(); } };
            viewport.Controls.Add(_buyView);
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
            // El escaparate es de TRENES (MainMenuForm.CompraTrenes.cs). El de máquinas ya no se enseña, pero se sigue
            // creando (lo usan la tasación y las solicitudes antiguas); su visor pasa al de trenes.
            right.Controls.Remove(viewport);
            t.Controls.Add(BuildTrainShop(viewport));
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
            if (_fleetCards == null) return;
            LoadFleetScale();          // escala de precio para tasar en el cliente
            PopulateFleetConsistList(); // consists comprables del contenido actual
            PopulateBuyMachines();      // máquinas (.eng) comprables del contenido actual
            if (_empSel == null) { _fleetIds.Clear(); _fleetStatus.Clear(); _fleetOwnedNames.Clear(); _fleetPlates.Clear(); _fleetRowEng.Clear(); _fleetRowDetail.Clear(); _fleetRowEstColor.Clear(); _fleetRowTrain.Clear(); _fleetRow2D.Clear(); _fleetCards.EmptyText = Tr("Selecciona una empresa."); _fleetCards.SetModels(new List<FleetModel>()); OnFleetVehicleSelected(); UpdateFleetTabCounts(); return; }
            if (_fleetIds.Count == 0) { _fleetCards.EmptyText = Tr("Cargando…"); _fleetCards.Invalidate(); }
            const string fleetCols = "id,name,folder,kind,engine_type,km_total,km_since_maint,maint_interval_km,ownership,status,rental_per_service,capacity,comfort,price,power_kw,max_speed_kmh,mass_t";
            var fleetCo = _empSel;
            string fleetFilter = $"&company_id=eq.{Uri.EscapeDataString(_empSel.Id)}&order=name.asc,created_at.asc";
            var tOpen = OpenServiceUnits(_empSel.Id);   // a la vez que las unidades
            var tTrains = LoadCoTrainsAsync(_empSel.Id, force: true);  // y que los trenes de la empresa (otro gestor puede haber comprado)
            var (json, err) = await SelectVehicles(fleetCols + ",plate,train_id,lead_eng", fleetFilter);
            if (err != null && (err.IndexOf("train_id", StringComparison.OrdinalIgnoreCase) >= 0 || err.IndexOf("lead_eng", StringComparison.OrdinalIgnoreCase) >= 0))
                (json, err) = await SelectVehicles(fleetCols + ",plate", fleetFilter);   // servidor sin trenes-por-con.sql
            if (err != null && err.IndexOf("plate", StringComparison.OrdinalIgnoreCase) >= 0)   // servidor aún sin matrículas
                (json, err) = await SelectVehicles(fleetCols, fleetFilter);
            var openUnits = await tOpen;
            await tTrains;
            if (_empSel != fleetCo) return;   // se cambió de empresa mientras se consultaba: manda la carga de la nueva
            _fleetIds.Clear(); _fleetStatus.Clear(); _fleetOwnedNames.Clear(); _fleetOwnedCount.Clear(); _fleetPlates.Clear();
            _fleetRowEng.Clear(); _fleetRowDetail.Clear(); _fleetRowEstColor.Clear(); _fleetRowTrain.Clear(); _fleetRow2D.Clear();
            _trainCopyCount.Clear(); _fleetTrainModels.Clear();
            if (err != null) { _fleetCards.EmptyText = Tr("Error: ") + err; _fleetCards.SetModels(new List<FleetModel>()); return; }
            _fleetCards.EmptyText = null;
            var models = new List<FleetModel>();
            var modelOf = new Dictionary<string, FleetModel>(StringComparer.OrdinalIgnoreCase);
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
                    // Un ejemplar de un tren (trenes-por-con.sql): una tarjeta por tren, con sus ejemplares.
                    string trainId = Str(e, "train_id");
                    if (trainId.Length > 0 || Str(e, "kind") == "train")
                    {
                        AddFleetTrainRow(e, trainId, models, modelOf, engPath, openUnits);
                        continue;
                    }
                    _fleetRowTrain.Add(""); _fleetRow2D.Add("");
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
                    string real = RealUnitStatus(status, Str(e, "id"), openUnits, since, interval);
                    bool inUse = real == "in_use", due = real == "maintenance_due";
                    bool soon = !due && !inUse && interval > 0 && remaining <= interval * 0.1;   // revisión pronto
                    int state = due ? 2 : inUse ? 1 : soon ? 3 : 0;
                    // Los mismos nombres cortos que las pestañas: caben en la celda ESTADO sin encogerse ni cortarse.
                    string estado = due ? Tr("En taller") : inUse ? Tr("En servicio") : soon ? Tr("Revisión pronto") : Tr("Disponible");
                    Color estadoColor = FleetCardList.StateColors[state];   // el mismo color que su chip
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
                    // Datos para la ficha lateral (columna derecha).
                    _fleetRowEng.Add(engPath.TryGetValue(name, out var ep) ? ep : "");
                    // TIPO = lo que declara el archivo del vehículo (Motriz / Viajeros / Mercancías…);
                    // la tracción y las plazas van en sus propios campos, para que ninguno se encoja.
                    string declared = VehicleTypeShort(DeclaredVehicleType(engPath.TryGetValue(name, out var dp) ? dp : null));
                    if (declared.Length == 0) declared = "—";
                    string traction = TractionName(Str(e, "engine_type"));
                    string plazas = cap > 0 ? cap.ToString("N0", EsEs) : "—";
                    _fleetRowDetail.Add(JoinDetail(new[] { plate.Length > 0 ? plate + "  ·  " + name : name, declared, prop, maint, estado, plazas, traction }, FleetExtraDetail(e), new[] { model }));
                    _fleetRowEstColor.Add(estadoColor);
                    // Tarjeta del modelo (carpeta + .eng) con esta unidad como chip de color
                    string mkey = ModelKey(rawFolder, name);
                    if (!modelOf.TryGetValue(mkey, out var fm))
                    {
                        var sub = new List<string>();
                        if (declared != "—") sub.Add(declared);
                        if (traction != "—") sub.Add(traction);
                        if (cap > 0) sub.Add(cap.ToString("N0", EsEs) + " " + Tr("plazas"));
                        sub.Add(hasLocal ? model : Tr("(no lo tienes en local)"));
                        fm = new FleetModel { Key = mkey, Title = name, Sub = string.Join("  ·  ", sub), Path = _fleetRowEng[_fleetRowEng.Count - 1],
                                              Group = Tr("MÁQUINAS ANTERIORES  ·  valen como cabeza de cualquier tren que las lleve; ya no se compran, solo se venden") };
                        modelOf[mkey] = fm; models.Add(fm);
                    }
                    fm.Units.Add(new FleetUnit
                    {
                        Row = _fleetIds.Count - 1, Id = _fleetIds[_fleetIds.Count - 1],
                        Label = plate.Length > 0 ? plate : string.Format(Tr("Unidad {0}"), fm.Units.Count + 1),
                        State = state
                    });
                }
            }
            catch { }
            models = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OrderBy(models, m => string.IsNullOrEmpty(m.Group) ? 0 : 1));   // primero los trenes; después, las máquinas anteriores
            _fleetCards.SetModels(models);
            if (_fleetCards.SelectedId == null && models.Count > 0 && models[0].Units.Count > 0) _fleetCards.Select(models[0].Units[0].Id, false);
            UpdateFleetTabCounts();
            UpdateCompanyKpis();
            if (_fleetHeaderLbl != null) _fleetHeaderLbl.Text = Tr("TRENES DE LA EMPRESA") + (n > 0 ? "  ·  " + n : "");
            EnsureOwnedMachinesListed();   // lo que ya tienes se ve siempre en Compra, con su distintivo
            // La lista solo se rehace si han cambiado las unidades de la empresa (rehacerla cuesta).
            int ownedStamp = 0;
            foreach (var kv in _fleetOwnedCount) ownedStamp = ownedStamp * 31 + kv.Key.GetHashCode() + kv.Value;
            if (ownedStamp != _fleetOwnedStamp) { _fleetOwnedStamp = ownedStamp; FilterBuyList(); }
            else _fleetEngList?.Invalidate();
            FilterShop();                 // el escaparate de trenes (con lo que ya tiene la empresa)
            _ = MatchLocalTrainsAsync();  // qué trenes de la empresa tienes en tu contenido
            OnFleetVehicleSelected();  // refresca la ficha/vista del vehículo seleccionado
            UpdateRoleUi();
            UpdateFleetValuation();   // recalcula el desglose del consist elegido (la propiedad puede haber cambiado)
        }

        // Un ejemplar de un tren en Flota: su tarjeta es la del tren (composición 2D si lo tienes; si no, su máquina de cabeza).
        readonly List<string> _fleetRowTrain = new(), _fleetRow2D = new();   // por fila: el tren y la vista 2D
        RoundButton _fleetAddConBtn;
        void AddFleetTrainRow(JsonElement e, string trainId, List<FleetModel> models, Dictionary<string, FleetModel> modelOf,
                              Dictionary<string, string> engPath, HashSet<string> openUnits)
        {
            var t = _coTrains.Find(x => x.Id == trainId);
            string name = Str(e, "name").Length > 0 ? Str(e, "name") : t?.Name ?? "—", lead = Str(e, "lead_eng");
            string status = Str(e, "status"), plate = Str(e, "plate"), ownership = Str(e, "ownership");
            double since = Num(e, "km_since_maint"), interval = Num(e, "maint_interval_km"), cap = Num(e, "capacity"), rent = Num(e, "rental_per_service");
            double remaining = Math.Max(0, interval - since);
            string real = RealUnitStatus(status, Str(e, "id"), openUnits, since, interval);
            bool inUse = real == "in_use", due = real == "maintenance_due", soon = !due && !inUse && interval > 0 && remaining <= interval * 0.1;
            int state = due ? 2 : inUse ? 1 : soon ? 3 : 0;
            string estado = due ? Tr("En taller") : inUse ? Tr("En servicio") : soon ? Tr("Revisión pronto") : Tr("Disponible");
            string prop = ownership == "rented" ? Tr("Alquilado") + " · " + rent.ToString("N0", EsEs) + " €/serv." : Tr("Comprado");
            string leadPath = lead.Length > 0 && engPath.TryGetValue(lead, out var lp) ? lp : "";
            bool local = trainId.Length > 0 && _trainCon.TryGetValue(trainId, out _);
            string saved = trainId.Length > 0 ? SavedImageFile("tren", trainId) : "";
            string view = local ? _trainCon[trainId] : System.IO.File.Exists(saved) ? saved : "";
            if (trainId.Length > 0) _trainCopyCount[trainId] = TrainCopies(trainId) + 1;
            _fleetPlates.Add(plate);
            _fleetRowEng.Add(leadPath);
            _fleetRowTrain.Add(trainId); _fleetRow2D.Add(view);
            string kindTxt = t == null ? Tr("Tren") : TrainCarriesPassengers(t.Vehicles) ? Tr("Viajeros") : Tr("Mercancías");
            string seatsTxt = cap > 0 ? cap.ToString("N0", EsEs) + (t?.Seats != null ? " · " + Tr("fijadas") : "") : "—";
            _fleetRowDetail.Add(JoinDetail(new[] { plate.Length > 0 ? plate + "  ·  " + name : name, kindTxt, prop, interval > 0 ? remaining.ToString("N0", EsEs) + " km" : "—",
                                        estado, seatsTxt, TractionName(Str(e, "engine_type")) }
                                , FleetExtraDetail(e), new[] { t != null ? TrainSummary(t.Vehicles) : "" }));
            _fleetRowEstColor.Add(FleetCardList.StateColors[state]);
            string mkey = "train:" + (trainId.Length > 0 ? trainId : name);
            if (!modelOf.TryGetValue(mkey, out var fm))
            {
                fm = new FleetModel { Key = mkey, Title = name, Sub = FleetTrainSub(t, local), Path = view, Info = t != null };
                modelOf[mkey] = fm; models.Add(fm);
                if (trainId.Length > 0) _fleetTrainModels[trainId] = fm;
            }
            fm.Units.Add(new FleetUnit
            {
                Row = _fleetIds.Count - 1, Id = _fleetIds[_fleetIds.Count - 1],
                Label = plate.Length > 0 ? plate : string.Format(Tr("Nº {0}"), fm.Units.Count + 1), State = state
            });
        }

        // Carga los .eng de las unidades de las empresas DE LAS QUE SOY SOCIO (para la etiqueta
        // "pertenece a tu empresa" en Conducción libre/Horarios). Superadmin: solo sus empresas de socio.
        async void LoadCompanyVehNames()
        {
            _companyVehNames.Clear();
            _companyTrainCons = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
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
                var trainCos = new HashSet<string>();   // empresas con trenes (ejemplares): sus etiquetas, por composición
                foreach (var c in _empCompanies) if (mine.Contains(c.Id)) byId[c.Id] = c.Name;
                string inList = string.Join(",", mine);
                var (vj, ve) = await SelectVehicles("name,folder,company_id,lead_eng", $"&company_id=in.({Uri.EscapeDataString(inList)})");
                if (ve != null && ve.IndexOf("lead_eng", StringComparison.OrdinalIgnoreCase) >= 0)   // servidor sin trenes-por-con.sql
                    (vj, ve) = await SelectVehicles("name,folder,company_id", $"&company_id=in.({Uri.EscapeDataString(inList)})");
                if (ve == null)
                    try
                    {
                        using var d = JsonDocument.Parse(vj);
                        foreach (var e in d.RootElement.EnumerateArray())
                        {
                            string cid = Str(e, "company_id");
                            // un ejemplar de un tren: su etiqueta va por su composición exacta (LoadCompanyTrainTagsAsync)
                            if (Str(e, "lead_eng").Length > 0) { trainCos.Add(cid); continue; }
                            string name = Str(e, "name");
                            if (name.Length == 0) continue;
                            string cn = byId.TryGetValue(cid, out var n) ? n : "";
                            if (cn.Length == 0) continue;
                            string key = ModelKey(Str(e, "folder"), name);
                            if (!_companyVehNames.TryGetValue(key, out var lst)) { lst = new List<string>(); _companyVehNames[key] = lst; }
                            if (!HasCo(lst, cn)) lst.Add(cn);
                        }
                    }
                    catch { }
                await LoadCompanyTrainTagsAsync(trainCos, byId);   // los trenes, por su composición exacta
            }
            catch { }
            RebuildCompanyEngs();
        }

        bool _trainTagsShown;

        // Expande cada .eng comprado a TODOS los coches motrices de su unidad (mirando los consists),
        // para que la etiqueta reconozca también los consists invertidos u otras variantes del mismo tren.
        void RebuildCompanyEngs()
        {
            // Sin trenes de ninguna empresa antes ni ahora (lo normal al arrancar, antes de iniciar sesión):
            // no cambia nada, así que no se rehace la lista de trenes (con miles de trenes, se nota).
            bool nada = _companyVehNames.Count == 0 && _companyEngs.Count == 0 && _companyTrainCons.Count == 0 && !_trainTagsShown;
            _trainTagsShown = _companyTrainCons.Count > 0;
            _companyEngs.Clear();
            if (nada) { try { PopulateCompanyFilters(); } catch { } return; }
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
            if (c == null) return _noCompanies;
            var byTrain = c.FilePath != null && _companyTrainCons.TryGetValue(c.FilePath, out var t) ? t : null;   // sus trenes (composición exacta)
            var byEng = ConsistCompaniesByEngine(c);                                                                  // sus máquinas anteriores
            if (byTrain == null) return byEng;
            if (byEng.Count == 0) return byTrain;
            var l = new List<string>(byEng); foreach (var x in byTrain) if (!HasCo(l, x)) l.Add(x);
            return l;
        }

        List<string> ConsistCompaniesByEngine(TrainItem c)
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

        // ---- Filtro de trenes por empresa (Conducción libre / Horarios) ----
        // Nombres de empresa (de socio) que tienen al menos un tren en el contenido actual, ordenados.
        List<string> AllFleetCompanies()
        {
            var set = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var kv in _companyEngs)
                foreach (var co in kv.Value)
                    if (!string.IsNullOrWhiteSpace(co)) set.Add(co);
            foreach (var kv in _companyTrainCons)
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
        // Se oculta el de Conducción libre si no hay ninguna (no aporta nada sin empresas).
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
            if (_trainCompanyHost != null) _trainCompanyHost.Visible = has;   // Conducción libre
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
            _fleetEngList?.ClearInfo();   // precios de las tarjetas con la escala de ahora
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
            // Si ya se calculó en segundo plano al cargar el contenido (PrewarmFleetLists), se usa tal cual.
            var pre = _fleetPre;
            if (pre.list != null && pre.root == root && pre.count == _consistsAll.Count)
            {
                _fleetEngs = pre.engs; _fleetConsists = pre.list;
                FilterFleetConsistList();
                return;
            }
            var (engs, list) = ComputeFleetLists();
            _fleetEngs = engs; _fleetConsists = list;
            FilterFleetConsistList();
        }

        // Máquinas del contenido y trenes con algo comprable: recorre todos los consists (miles), así que se
        // calcula en segundo plano nada más leer el contenido y abrir Flota o Compra ya no espera.
        (List<(string name, string folder, string path, string kind)> engs, List<TrainItem> list) ComputeFleetLists()
        {
            var engs = AvailableEngines();
            var hidden = AutomotorMemberEngs();
            var list = new List<TrainItem>();
            foreach (var c in _consistsAll.ToArray())
                if (c?.FilePath != null && PurchasableUnitsInConsist(c, hidden).Count > 0) list.Add(c);
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return (engs, list);
        }

        (string root, int count, List<(string name, string folder, string path, string kind)> engs, List<TrainItem> list) _fleetPre;
        void PrewarmFleetLists()
        {
            string root = _curFolder?.Path ?? ""; int count = _consistsAll.Count;
            if (count == 0) return;
            try
            {
                var (engs, list) = ComputeFleetLists();
                if (root == (_curFolder?.Path ?? "") && count == _consistsAll.Count) _fleetPre = (root, count, engs, list);
            }
            catch { }
        }

        // ---------------- ID ÚNICO de consist (para localizar el tren exacto en Compra) ----------------
        readonly Dictionary<string, string> _consistIdCache = new(StringComparer.OrdinalIgnoreCase);
        Label _lblStatusExplore, _lblStatusTT;   // estado en la flota, en la ficha «TREN SELECCIONADO»
        Panel _buyWrapExplore, _buyWrapTT;      // botón «Comprar este tren» (solo gerente/gestor/superadmin)

        // TrainItem del tren elegido en Horarios (el mismo que usa la barra de estado).
        TrainItem CurrentTTConsist()
        {
            var ttr = _cboTTTrain?.SelectedItem as Orts.Formats.OR.TimetableFileLite.TrainInformation;
            return ResolveConsist(ttr != null ? TtConsistText(ttr) : null);   // la elegida por el usuario, si la hay
        }

        // «Comprar este tren»: SOLO gerente/gestor de la empresa seleccionada o superadmin.
        bool CanBuyTrains() => Supa.IsLoggedIn && _empSel != null && (Supa.IsSuperadmin || CanManage());

        // Refresca el ID visible y el botón «Comprar este tren» de Conducción libre y Horarios.
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
            var hdr = new Panel { Dock = DockStyle.Top, Height = Math.Max(24, title.PreferredHeight + 6), BackColor = Theme.Surface };   // va dentro de las tarjetas del tren elegido
            // ancho fijo medido (con AutoSize la etiqueta no centra en vertical: el título quedaba más alto que lo demás)
            title.Dock = DockStyle.Left; title.AutoSize = false;
            title.Width = TextRenderer.MeasureText(title.Text, title.Font).Width + title.Padding.Horizontal + 4;
            var status = new Label { Dock = DockStyle.Right, AutoSize = false, Width = 320, TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.Subtle, Font = Theme.Font(8.5f, FontStyle.Bold) };
            // solo lo que mide su texto: así deja sitio al teleindicador de la cabecera
            void FitStatus() { status.Width = string.IsNullOrEmpty(status.Text) ? 0 : TextRenderer.MeasureText(status.Text, status.Font).Width + 8; }
            status.TextChanged += (s, e) => FitStatus();
            FitStatus();
            hdr.Controls.Add(title); hdr.Controls.Add(status);
            return (hdr, status);
        }

        // ---- «Operativo / No operativo» del tren seleccionado (Conducción libre y Horarios) ----
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
            string key = co.Id + "|" + string.Join(",", keyParts) + "|" + c.FilePath;
            lbl.Tag = key;
            string code;
            if (_fleetStatusCache.TryGetValue(key, out var hit) && (DateTime.UtcNow - hit.at).TotalSeconds < 20) code = hit.code;
            else
            {
                var esc = new List<string>();
                var folderOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var en in names) { esc.Add(PgInItem(en.name)); folderOf[en.name] = en.folder ?? ""; }
                var tOpen = OpenServiceUnits(co.Id);
                var (json, err) = await SelectVehicles("id,status,name,folder,km_since_maint,maint_interval_km",
                    $"&company_id=eq.{Uri.EscapeDataString(co.Id)}&name=in.({string.Join(",", esc)})&or=(kind.is.null,kind.neq.train)");
                var openUnits = await tOpen;
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
                        string st = RealUnitStatus(Str(e, "status"), Str(e, "id"), openUnits, Num(e, "km_since_maint"), Num(e, "maint_interval_km"));
                        if (st == "available") ok = true;
                        else if (st == "in_use") inUse = true;
                        else if (st == "maintenance_due") maint = true;
                    }
                }
                catch { }
                code = !any ? "none" : ok ? "ok" : inUse && !maint ? "inuse" : maint && !inUse ? "maint" : "unavail";
                // los ejemplares de ese tren (trenes-por-con.sql) mandan si la máquina anterior no está libre
                string tcode = await TrainStatusCodeAsync(co.Id, c);
                if (code != "ok" && tcode != "none" && (tcode == "ok" || code == "none")) code = tcode;
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
            var wrap = new Panel { Dock = DockStyle.Right, Width = 200, BackColor = Theme.Surface, Padding = new Padding(8, 0, 0, 0), Visible = false };   // va en las tarjetas del tren elegido
            var b = new RoundButton
            {
                Text = Tr("Comprar este tren"), GlyphKind = "bank", Dock = DockStyle.Fill, Height = height, Radius = 9,
                BaseColor = Color.FromArgb(40, 70, 44), HoverColor = Theme.Accent, TextColor = Theme.AccentHi,
                FontSize = 9.5f, FontStyle = FontStyle.Bold
            };
            b.Click += (s, e) => { if (CanBuyTrains()) BuyLocalTrain(getConsist()); else RequestLocalTrain(getConsist()); };   // trenes completos (TrenesEmpresa)
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
        // Catálogo de Compra: solo se recalcula si cambian las máquinas del contenido o las formaciones
        // (antes se rehacía, con su lista de miles de filas, cada vez que se abría Flota o Compra).
        object _buySrcEngs, _buySrcUnits;

        void PopulateBuyMachines()
        {
            if (_buyMachines.Count > 0 && ReferenceEquals(_buySrcEngs, _fleetEngs) && ReferenceEquals(_buySrcUnits, _engUnits))
            {
                if (_fleetEngList != null && _fleetEngList.Items.Count == 0) FilterBuyList();
                return;
            }
            _buySrcEngs = _fleetEngs; _buySrcUnits = _engUnits;
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
            if (_fleetEngList == null) return;
            // Las añadidas por estar en la flota se recalculan cada vez (si ya no las tienes, se quitan).
            _buyMachines.RemoveAll(m => m.Extra);
            if (_fleetOwnedCount.Count == 0) return;
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
                _buyMachines.Add(new BuyMachine { Name = name, Folder = folder, Path = path, UnitCars = head ? u.Cars : 0, Extra = true });
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
            var counts = new int[4];
            foreach (var m in _buyMachines)
            {
                if (filter.Length > 0
                    && m.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && (m.Folder ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                bool own = OwnedUnits(m.Folder, m.Name) > 0, unit = m.UnitCars > 1;
                counts[0]++; if (own) counts[1]++; if (unit) counts[2]++; else counts[3]++;
                if (_buyKind == 1 && !own || _buyKind == 2 && !unit || _buyKind == 3 && unit) continue;
                shown.Add(m);
            }
            UpdateBuyTabCounts(counts);

            // Sin cabeceras por carpeta: la lista es de MODELOS y cada fila ya es uno. La carpeta
            // sigue viéndose en la propia fila, como referencia de la librea que se compraría.
            _buyGrouped.Clear();
            _fleetEngList.BeginUpdate();
            _fleetEngList.Items.Clear();
            _fleetEngList.Items.AddRange(shown.ToArray());
            _fleetEngList.EndUpdate();
            // La lista se rehace con objetos nuevos: la máquina elegida se reconoce por nombre y carpeta.
            if (prev != null)
                for (int i = 0; i < _fleetEngList.Items.Count; i++)
                    if (_fleetEngList.Items[i] is BuyMachine bm && string.Equals(bm.Name, prev.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(bm.Folder ?? "", prev.Folder ?? "", StringComparison.OrdinalIgnoreCase)) { _fleetEngList.SelectedIndex = i; break; }
            if (_fleetEngList.Items.Count > 0) _fleetEngList.TopIndex = Math.Min(top, _fleetEngList.Items.Count - 1);
        }

        void UpdateBuyTabCounts(int[] counts)
        {
            if (_buyKindTabs == null) return;
            string[] names = { "Todas", "En tu flota", "Automotores", "Locomotoras" };
            using var f = Theme.Font(9.75f, FontStyle.Bold);
            for (int k = 0; k < names.Length && k < _buyKindTabs.Controls.Count; k++)
            {
                if (_buyKindTabs.Controls[k] is not RoundButton b) continue;
                b.Text = Tr(names[k]) + "  " + counts[k].ToString("N0", EsEs);
                b.Width = TextRenderer.MeasureText(b.Text, f).Width + 34;
                b.Invalidate();
            }
        }

        // Datos y precio de una tarjeta de Compra (los mismos cálculos que la ficha de la derecha).
        Task<MachineInfo> MachineInfoAsync(BuyMachine m)
        {
            if (m == null || string.IsNullOrEmpty(m.Path)) return Task.FromResult<MachineInfo>(null);
            var rep = RepresentativeConsistFor(m);
            string path = m.Path;
            return Task.Run(() =>
            {
                var (kw, kmh, type) = ReadEngineSpecs(path);
                var analysis = rep != null ? AnalyzeComposition(rep, kmh) : new CompositionAnalysis { ServiceType = Tr("Mercancías"), Freight = true };
                var (automotor, cars) = ShapeForService(DetectUnitShape(path), analysis.Freight, path);
                var (mass, brake) = AnalyzeUnitPhysics(path, automotor);
                var (price, rent) = FleetPriceLocal(kw, kmh, type, automotor, cars, mass, brake);
                return new MachineInfo
                {
                    Kw = kw, Kmh = kmh, Mass = mass, Capacity = analysis.Capacity, Price = price, Rent = rent,
                    Freight = analysis.Freight && !analysis.DeclaredPax, Traction = type ?? ""
                };
            });
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
            if (_fleetPreview == null) return;
            // El visor es el del escaparate de trenes: en 2D, la composición entera (OnShopSelected); en 3D, su máquina de cabeza.
            var c = _shopRows?.SelectedItem as TrainItem;
            if (_buyView != null && !_buyView.Is3D) { _fleetPrevEngPath = null; return; }
            _fleetPreview.Image = null; _fleetPreview.Rotatable = false;
            _fleetGeom = null; _fleetYaw = 0; _fleetPitch = 0; _fleetFlip = false;
            string engPath = c?.Locomotive?.FilePath;
            _fleetPreview.EmptyText = string.IsNullOrEmpty(engPath) ? Tr("Elige un tren") : null;
            if (string.IsNullOrEmpty(engPath)) { _fleetPrevEngPath = null; _fleetPreview.Caption = ""; _fleetPreview.Invalidate(); return; }
            _fleetPrevEngPath = engPath;
            _fleetPreview.Caption = c.Name;
            _fleetPreview.Invalidate();
            lock (_fleetRenderingShapes) { if (!_fleetRenderingShapes.Add(engPath)) return; }
            Task.Run(() =>
            {
                ShapeGeom geom = null;
                lock (_geomCache) _geomCache.TryGetValue(engPath, out geom);
                if (geom == null)
                {
                    try { geom = ShapeRenderer.BuildGeometry(engPath); } catch { }
                    try { ShapeRenderer.PrefetchTextures(geom); } catch { }   // texturas listas antes de renderizar (si no, se descodifican en el hilo de la interfaz)
                    if (geom != null) lock (_geomCache) _geomCache[engPath] = geom;
                }
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
            // Un automotor se tasa con su formación (la composición representativa); una locomotora, con sus propios
            // datos (los coches y vagones se compran aparte).
            if (rep != null && automotor)
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
            if (ThemedBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

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
            if (SeatsOverride(c) is int seats) cap = seats;   // las plazas que ha fijado la empresa
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
                if (ThemedBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
            }

            // Se compra la MÁQUINA y se tasa con sus propios datos: los coches y vagones se compran aparte (trenes
            // completos). Solo un automotor se tasa con su formación (sus coches son parte de la unidad): el gestor
            // elige con qué composición.
            var sp = BuySpecsOf(m);
            int cars = sp.cars; double mass = sp.mass, brake = sp.brake, capacity = sp.capacity;
            var ops = (l: new List<ConsistPriceDialog.Opcion>(), d: 0);
            if (sp.automotor)
            {
                Msg(_buyMsg, Tr("Buscando composiciones de esta máquina…"), false);
                ops = await Task.Run(() => { int d; var l = ConsistOptionsFor(m, out d); return (l, d); });
            }
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

        // ¿Se conoce ya la cabecera (sin leer el archivo)?
        static bool HeadCached(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            lock (_vehHead) if (_vehHead.ContainsKey(path)) return true;
            return ContentIndex.Heads.ContainsKey(path);
        }

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
                if (buf.Length > 1 && buf[0] == 0xFF && buf[1] == 0xFE) return System.Text.Encoding.Unicode.GetString(buf);
                if (buf.Length > 1 && buf[0] == 0xFE && buf[1] == 0xFF) return System.Text.Encoding.BigEndianUnicode.GetString(buf);   // UTF-16 BE
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
                                 t, @"EngineData\s*\(\s*(?:""([^""]*)""|([^\s()""]+))", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        list.Add((m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim());   // con o sin comillas
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
                lock (_geomCache) _geomCache.TryGetValue(engPath, out geom);
                if (geom == null)
                {
                    try { geom = ShapeRenderer.BuildGeometry(engPath); } catch { }
                    try { ShapeRenderer.PrefetchTextures(geom); } catch { }   // texturas listas antes de renderizar (si no, se descodifican en el hilo de la interfaz)
                    if (geom != null) lock (_geomCache) _geomCache[engPath] = geom;
                }
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
        int FleetSelectedRow() => _fleetCards?.SelectedRow ?? -1;

        // Pestañas de Flota con cuántas unidades hay en cada estado.
        void UpdateFleetTabCounts()
        {
            if (_fleetTabs == null || _fleetCards == null) return;
            string[] names = { "Todos", "Disponibles", "En servicio", "En taller" };
            using var f = Theme.Font(9f, FontStyle.Bold);   // las pastillas, como las de Conducción libre
            for (int k = 0; k < names.Length && k < _fleetTabs.Controls.Count; k++)
            {
                if (_fleetTabs.Controls[k] is not RoundButton b) continue;
                int n = _fleetCards.Count(k - 1);
                b.Text = Tr(names[k]) + (_fleetIds.Count > 0 ? "  " + n.ToString("N0", EsEs) : "");
                b.Width = TextRenderer.MeasureText(b.Text, f).Width + 28;
                b.Invalidate();
            }
        }

        // Visor de Flota: la vista 2D al momento; el 3D solo si está abierto.
        void ShowFleetOwn(string engPath, string viewPath = null)
        {
            _fleetOwnEngPath = engPath;
            _fleetOwnView?.Show(viewPath ?? engPath, _fleetOwnTitle?.Text ?? "", Tr("Elige un vehículo"));
            if (_fleetOwnView == null || _fleetOwnView.Is3D) RenderFleetOwn(engPath);
        }

        void OnFleetVehicleSelected()
        {
            if (_fleetOwnTitle == null) return;
            if (_fleetCards != null && _fleetCards.MarkedCount > 1) { ShowFleetMulti(); return; }
            foreach (var b in new[] { _fleetPlateBtn, _fleetSeatsBtn, _fleetInfoBtn }) if (b != null) b.Enabled = true;
            int i = FleetSelectedRow();
            if (i < 0 || i >= _fleetRowDetail.Count)
            {
                _fleetOwnTitle.Text = Tr("Elige un vehículo");
                if (_voKind != null) _voKind.Text = "—"; if (_voProp != null) _voProp.Text = "—";
                if (_voMaint != null) _voMaint.Text = "—"; if (_voEstado != null) { _voEstado.Text = "—"; _voEstado.ForeColor = Theme.Text; }
                if (_voCap != null) _voCap.Text = "—"; if (_voTraction != null) _voTraction.Text = "—";
                foreach (var l in new[] { _voValue, _voKm, _voComfort, _voPower, _voSpeed, _voMass }) if (l != null) l.Text = "—";
                if (_fleetOwnSub != null) _fleetOwnSub.Text = "";
                ShowFleetOwn(null);
                UpdateFleetTrainButtons(-1);
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
            // la tarjeta ya enseña el tren entero: aquí, su máquina de cabeza (o, si no la tienes, la composición guardada)
            ShowFleetOwn(_fleetRowEng[i], _fleetRowEng[i].Length == 0 && i < _fleetRow2D.Count && _fleetRow2D[i].Length > 0 ? _fleetRow2D[i] : null);
            var extra = new[] { _voValue, _voKm, _voComfort, _voPower, _voSpeed, _voMass };
            for (int k = 0; k < extra.Length; k++) if (extra[k] != null) extra[k].Text = det.Length > 7 + k && det[7 + k].Length > 0 ? det[7 + k] : "—";
            if (_fleetOwnSub != null) _fleetOwnSub.Text = det.Length > 13 ? det[13] : "";
            UpdateFleetTrainButtons(i);
        }

        // «Añadir a mi contenido»: un ejemplar de un tren que no tienes en tu contenido (cualquier socio).
        void UpdateFleetTrainButtons(int i)
        {
            if (_fleetAddConBtn == null) return;
            string tid = i >= 0 && i < _fleetRowTrain.Count ? _fleetRowTrain[i] : "";
            _fleetAddConBtn.Visible = tid.Length > 0 && !_trainCon.ContainsKey(tid);
            if (_fleetInfoBtn != null) _fleetInfoBtn.Visible = tid.Length > 0 && _fleetTrainModels.ContainsKey(tid) && _fleetTrainModels[tid].Info;
            if (_fleetSeatsBtn != null) _fleetSeatsBtn.Visible = tid.Length > 0 && (CanManage() || Supa.IsSuperadmin) && _coTrains.Exists(x => x.Id == tid);
        }

        // Tras reconocer los trenes de tu contenido: cada tarjeta de tren con su composición y lo que te falta.
        readonly Dictionary<string, FleetModel> _fleetTrainModels = new(StringComparer.OrdinalIgnoreCase);
        void UpdateFleetTrainViews()
        {
            if (_fleetCards == null) return;
            foreach (var kv in _fleetTrainModels)
            {
                var t = _coTrains.Find(x => x.Id == kv.Key);
                bool local = _trainCon.TryGetValue(kv.Key, out var con);
                if (local) kv.Value.Path = con;
                kv.Value.Sub = FleetTrainSub(t, local);
            }
            for (int i = 0; i < _fleetRowTrain.Count && i < _fleetRow2D.Count; i++)
                if (_fleetRowTrain[i].Length > 0 && _trainCon.TryGetValue(_fleetRowTrain[i], out var con)) _fleetRow2D[i] = con;
            _fleetCards.Invalidate();
            OnFleetVehicleSelected();
            FetchFleetTrainImages();
        }

        // Los trenes que no tienes en tu contenido: su composición guardada en la empresa (se descarga una vez).
        async void FetchFleetTrainImages()
        {
            var co = _empSel; if (co == null) return;
            foreach (var kv in new List<KeyValuePair<string, FleetModel>>(_fleetTrainModels))
            {
                if (_trainCon.ContainsKey(kv.Key) || (kv.Value.Path ?? "").Length > 0) continue;
                var t = _coTrains.Find(x => x.Id == kv.Key);
                string file = await CoTrainImageFileAsync(co.Id, t);
                if (file == null || co != _empSel) continue;
                kv.Value.Path = file;
                for (int i = 0; i < _fleetRowTrain.Count && i < _fleetRow2D.Count; i++) if (_fleetRowTrain[i] == kv.Key) _fleetRow2D[i] = file;
                _fleetCards?.ForgetImage(file);
                OnFleetVehicleSelected();
            }
        }

        string FleetTrainSub(CoTrain t, bool local)
        {
            var sub = new List<string>();
            if (t != null) { sub.Add(TrainCarriesPassengers(t.Vehicles) ? Tr("Viajeros") : Tr("Mercancías")); sub.Add(TrainSummary(t.Vehicles)); }
            if (!local) sub.Add(Tr("(no lo tienes en tu contenido)"));
            return string.Join("  ·  ", sub);
        }

        void RenderFleetOwn(string engPath)
        {
            if (_fleetOwnPreview == null) return;
            // Misma perspectiva que los visores de Conducción libre, Horarios y Compra (vista de costado).
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
                lock (_geomCache) _geomCache.TryGetValue(engPath, out geom);
                if (geom == null)
                {
                    try { geom = ShapeRenderer.BuildGeometry(engPath); } catch { }
                    try { ShapeRenderer.PrefetchTextures(geom); } catch { }   // texturas listas antes de renderizar (si no, se descodifican en el hilo de la interfaz)
                    if (geom != null) lock (_geomCache) _geomCache[engPath] = geom;
                }
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
            int i = FleetSelectedRow();
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
            if (u.Contains("kmh") || u.Contains("km/h") || u.Contains("kph") || u.Contains("kmph")) kmh = v;   // «kmph» antes que «mph»
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
            @"(Engine|Wagon)Data\s*\(\s*(?:""([^""]*)""|([^\s()""]+))\s+(?:""([^""]*)""|([^\s()""]+))",   // nombre y carpeta, con o sin comillas
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
                        string name = (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim();
                        string folder = (m.Groups[4].Success ? m.Groups[4].Value : m.Groups[5].Value).Trim();
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
        // ignoreSeats: las plazas de los archivos aunque la empresa haya fijado otras (para enseñarlas al fijarlas).
        CompositionAnalysis AnalyzeComposition(TrainItem c, double leadSpeedKmh, bool ignoreSeats = false)
        {
            var result = new CompositionAnalysis();
            double capSum = 0, areaSum = 0, areaAll = 0; bool anyTilting = false;
            if (c?.FilePath != null)
            {
                foreach (var r in ConsistCarRefs(c.FilePath))
                {
                    var v = Veh(ResolveCarFile(r.name, r.folder));
                    if (v == null) continue;
                    if (!r.isEngine || v.Capacity > 0) areaAll += (v.Width > 0 && v.Length > 0) ? v.Width * v.Length : 0;
                    if (v.Capacity <= 0) continue;   // sin PassengerCapacity declarado: no cuenta
                    capSum += v.Capacity;
                    areaSum += (v.Width > 0 && v.Length > 0) ? v.Width * v.Length : 0;
                    if (v.Tilting) anyTilting = true;
                }
            }
            // Plazas fijadas por la empresa para este tren (trenes-plazas.sql): valen para todos, en vez del
            // PassengerCapacity de los archivos de cada uno. La superficie, la de los coches que las tenían (o todos).
            if (!ignoreSeats && SeatsOverride(c) is int seats)
            {
                if (capSum <= 0) areaSum = areaAll;
                capSum = seats;
            }
            result.DeclaredPax = ConsistCarriesPeople(c) || (!ignoreSeats && SeatsOverride(c) != null);
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
                if (ThemedBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
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
        // Asignar / cambiar la matrícula de la unidad seleccionada (SOLO gerente, gestor o superadmin;
        // el servidor lo vuelve a comprobar). Única dentro de la empresa. Vacía = quitar matrícula.
        async void AssignPlateUi()
        {
            if (!CanManage() && !Supa.IsSuperadmin) { Msg(_fleetMsg, Tr("Solo el gerente, un gestor o el administrador pueden asignar matrículas."), true); return; }
            int i = FleetSelectedRow();
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
            var sel = FleetSelection();
            if (sel.Count == 0) { Msg(_fleetMsg, Tr("Selecciona un vehículo de la lista."), true); return; }
            string q = sel.Count == 1 ? Tr("¿Dar de baja esta unidad? Si es propia se reembolsa parte de su valor; si es alquilada, se devuelve.")
                                      : string.Format(Tr("¿Dar de baja los {0} ejemplares elegidos? Los propios reembolsan parte de su valor; los alquilados se devuelven."), sel.Count);
            if (ThemedBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Msg(_fleetMsg, Tr("Dando de baja…"), false);
            string err = null; int done = 0;
            foreach (var (_, id) in sel)
            {
                var (_, e) = await Supa.RpcAsync("retire_vehicle", new { p_vehicle = id });
                if (e != null) err ??= e; else done++;
            }
            if (err != null && done == 0) { Msg(_fleetMsg, Tr("Error: ") + err, true); return; }
            Msg(_fleetMsg, sel.Count == 1 ? Tr("Unidad dada de baja.") : string.Format(Tr("{0} de {1} dados de baja."), done, sel.Count) + (err != null ? "  " + Tr("Error: ") + err : ""), err != null);
            _fleetCards?.ClearMarks();
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
            string route = _svcRouteOverride ?? _curRoute?.Name ?? "";
            var svc = _pendingServiceId;
            var openedUtc = _svcOpenedUtc;

            // Viajes de menos de 3 km o de menos de 5 minutos: no se registran (más abajo).
            int durRule = ServiceSecondsForRule();
            bool cortoKm = _estimatedKm < MinServiceKm, cortoTiempo = durRule < MinServiceSeconds;

            // Carné por puntos: primero lo detectado durante la conducción. Un salto de posición o el
            // tiempo acelerado anulan el servicio (el servidor ya lo ha borrado); la infracción queda.
            // En los viajes cortos no cuenta ninguna infracción (el viaje tampoco se registra).
            var infr = await ReportInfractionsAsync(svc, _estimatedKm, durRule == int.MaxValue ? _tripDurationS : durRule, cortoKm || cortoTiempo);
            if (infr.Err != null) { Msg(_empHomeMsg, Tr("No se pudo registrar el servicio: ") + infr.Err, true); return (false, Tr("No se pudo registrar el servicio: ") + infr.Err); }
            if (infr.Voided)
            {
                _pendingServiceId = null; _svcOpenedUtc = null; DeleteServiceJournal();
                UpdateDutyUi();
                var nv = new ServiceResultDialog.Data
                {
                    Company = companyName, Route = route, Valid = false, Km = _estimatedKm, DurationS = _tripDurationS,
                    Reasons = InfrVoidReasons(infr.Items), Infractions = infr.Items, LicensePoints = infr.Points, SuspendedUntil = infr.Until
                };
                _estimatedKm = 0;
                if (showDialog) nv.Map = await BuildTripMap(openedUtc);
                return ShowNotRegistered(nv, showDialog);
            }

            // Viajes de menos de 3 km o de menos de 5 minutos: no se registran (se descarta el
            // servicio y se libera la unidad). Al maquinista se le explica por qué.
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
                    _pendingServiceId = null; _svcOpenedUtc = null; DeleteServiceJournal();
                    UpdateDutyUi();
                    var motivos = new List<string>();
                    if (cortoKm) motivos.Add(string.Format(Tr("Recorrido de {0} km: los viajes de menos de 3 km no se registran."), _estimatedKm.ToString("0.0", EsEs)));
                    if (cortoTiempo) motivos.Add(string.Format(Tr("Duración de {0}: los viajes de menos de 5 minutos no se registran."), FmtMinSec(durRule)));
                    var nr = new ServiceResultDialog.Data
                    {
                        Company = companyName, Route = route, Valid = false, Km = _estimatedKm,
                        DurationS = durRule == int.MaxValue ? _tripDurationS : durRule, Reasons = motivos,
                        Infractions = infr.Items, LicensePoints = infr.Points, SuspendedUntil = infr.Until
                    };
                    _estimatedKm = 0;
                    if (showDialog) nr.Map = await BuildTripMap(openedUtc);
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
                p_pax = _paxBoarded,
                p_pax_km = Math.Round(_paxKm, 1)
            });
            // Servidor sin billete-por-km.sql: no conoce p_pax_km → se registra como antes, sin él.
            if (err != null && (err.IndexOf("p_pax_km", StringComparison.OrdinalIgnoreCase) >= 0
                                || err.IndexOf("Could not find the function", StringComparison.OrdinalIgnoreCase) >= 0))
                (jr, err) = await Supa.RpcAsync("close_service", new
                {
                    p_service = svc, p_km = _estimatedKm, p_completed = true, p_notes = "",
                    p_duration_s = _tripDurationS, p_pax = _paxBoarded
                });
            // Si falla, dejamos el servicio pendiente para poder reintentar con "Registrar servicio".
            if (err != null) { Msg(_empHomeMsg, Tr("No se pudo registrar el servicio: ") + err, true); return (false, Tr("No se pudo registrar el servicio: ") + err); }
            _pendingServiceId = null; _svcOpenedUtc = null; DeleteServiceJournal();
            UpdateDutyUi();
            var trailPayload = TrailPayload(openedUtc);   // el recorrido, para el mapa del historial (se envía si queda registrado)

            // Datos del viaje devueltos por el servidor (economía autoritativa) → ventana de resultado.
            var r = new ServiceResultDialog.Data
            {
                Company = companyName, Route = route, ServiceId = svc, Consist = _drivenLabel ?? "", Path = _drivenPath ?? "",
                Driver = Supa.Username ?? "", DateText = DateTime.Now.ToString(I18n.English ? "d MMM yyyy · HH:mm" : "d 'de' MMMM · HH:mm", I18n.English ? CultureInfo.GetCultureInfo("en-GB") : EsEs)
            };
            // Paradas comerciales (solo trenes de viajeros): las de este servicio, desde que se abrió.
            if (_svcTrainCap > 0)
            {
                r.Stops = new List<(string, string, int, int)>();
                foreach (var st in _svcStopsLog)
                    if (openedUtc == null || st.Utc >= openedUtc.Value.AddSeconds(-5)) r.Stops.Add((st.Station, st.Time, st.Board, st.Alight));
            }
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
                r.Rental = Num(root, "rental");
                r.Net = Num(root, "net");
                if (root.TryGetProperty("calc", out var ca) && ca.ValueKind == JsonValueKind.Object) r.Calc = ca.Clone();
                r.Balance = Num(root, "balance");
                r.Pax = (int)Num(root, "pax");
                // Plazas o toneladas del tren: las que guardó el servidor (con las que ha calculado) o, si no
                // las devuelve, las que se enviaron al abrir el servicio.
                double m = Num(root, "consist_mass");
                r.MassT = m > 0 ? m : _svcTrainMass;
                r.Capacity = _svcTrainCap;
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
                // Carné por puntos: infracciones de este servicio y puntos que quedan.
                if (root.TryGetProperty("infractions", out var inf)) r.Infractions = Carne.ParseItems(inf);
                else r.Infractions = infr.Items;
                if (root.TryGetProperty("license", out var lic)) (r.LicensePoints, r.SuspendedUntil) = Carne.ParseLicense(lic);
                else { r.LicensePoints = infr.Points; r.SuspendedUntil = infr.Until; }
                double validAfter = Num(root, "driver_valid_km");
                double validBefore = validAfter - (r.Valid ? r.Km : 0);
                // Con el carné por debajo de 6 el rango está congelado: el servidor da los km con que se calcula.
                bool frozen = root.TryGetProperty("rank_km", out var rk) && rk.ValueKind == JsonValueKind.Number && rk.GetDouble() < validAfter - 0.01;
                if (frozen) { validAfter = rk.GetDouble(); validBefore = validAfter; }
                r.RankFrozen = frozen;
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
            if (showDialog) r.Map = await BuildTripMap(openedUtc);
            if (!r.Valid) return ShowNotRegistered(r, showDialog);
            Msg(_empHomeMsg, Tr("Servicio registrado."), false);
            if (r.Stops != null) SaveServiceStops(svc, r.Stops);
            SaveServiceTrail(svc, trailPayload);
            // Registro publicado → refresca saldo y la subpestaña visible (sin botón "Actualizar").
            LoadCompanies(soft: true);
            RefreshActiveSubtab(skipCompanyLists: true);

            if (showDialog)
            {
                r.RecoveredNote = _svcRecoveredNote ?? "";
                using var dlg = new ServiceResultDialog(r);
                dlg.ShowDialog(this);
            }
            string sum = string.Format(Tr("Servicio registrado · {0} km · neto {1}"), r.Km.ToString("0.0", EsEs), r.Net.ToString("+#,##0.00 €;-#,##0.00 €", EsEs));
            return (true, sum);
        }

        // Paradas del servicio al servidor (paradas-servicio.sql); si aún no está, se queda solo en la ventana.
        async void SaveServiceStops(string svc, List<(string station, string time, int board, int alight)> stops)
        {
            try
            {
                var arr = new List<object>();
                foreach (var (st, t, b, a) in stops) arr.Add(new { s = st, t, b, a });
                await Supa.RpcAsync("set_service_stops", new { p_service = svc, p_stops = arr });
            }
            catch { }
        }

        // Viaje que NO se registra (corto, velocidad imposible o fallido): el servicio ya no existe;
        // se avisa al maquinista de por qué (ventana al volver de conducir; con OR abierto, en la barra).
        (bool ok, string summary) ShowNotRegistered(ServiceResultDialog.Data r, bool showDialog)
        {
            string motivos = string.Join(" ", r.Reasons);
            Msg(_empHomeMsg, Tr("Servicio no registrado: ") + motivos, true);
            LoadCompanies(soft: true);
            RefreshActiveSubtab(skipCompanyLists: true);
            if (showDialog)
            {
                r.RecoveredNote = _svcRecoveredNote ?? "";
                using var dlg = new ServiceResultDialog(r);
                dlg.ShowDialog(this);
            }
            return (true, Tr("Servicio no registrado: ") + motivos);
        }

        // Recarga los datos de la subpestaña visible (auto-refresh al publicar un registro).
        // skipCompanyLists: servicios y socios ya los recarga LoadCompanies(soft: true).
        void RefreshActiveSubtab(bool skipCompanyLists = false)
        {
            switch (_empSubtab)
            {
                case 0: if (_empSel != null && !skipCompanyLists) LoadServices(_empSel); break;
                case 1: LoadLedger(); break;
                case 2: if (_empSel != null && !skipCompanyLists) LoadMembers(_empSel); break;
                case 4: LoadRankTab(); break;
                case 5: LoadProfile(); break;
                case 6: LoadReview(); break;
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
                new[] { "services", "ledger", "companies", "company_members", "join_requests", "notifications", "company_chat_messages", "company_chat_mutes" },
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
                if (table == "notifications") { BeginInvoke((Action)(() => NotifySoon(300))); return; }   // aviso nuevo para ti
                if (table.StartsWith("company_chat", StringComparison.Ordinal)) { BeginInvoke((Action)(() => ChatRealtime(table))); return; }   // chat
                BeginInvoke((Action)(() => { if (_rtDebounce != null) { _rtDebounce.Stop(); _rtDebounce.Start(); } }));
            }
            catch { }
        }

        void DoRealtimeRefresh()
        {
            if (!Supa.IsLoggedIn || _activePage != PageEmpresas) return;   // solo refresca si estás mirando Empresas
            LoadCompanies(soft: true);                  // saldo/tesorería, servicios, socios y rol
            RefreshActiveSubtab(skipCompanyLists: true); // el resto de la subpestaña visible
        }

        // ============================ Socios y roles ============================
        async void LoadMembers(EmpCompany c)
        {
            if (c == null) return;
            var tPts = LoadMemberPoints(c.Id);   // carné por puntos de cada socio (a la vez)
            var tCards = Supa.RpcAsync("company_member_cards", new { p_company = c.Id });   // rango y servicios de cada socio (socios-carnet.sql)
            var tPhotos = Supa.RpcAsync("company_member_photos", new { p_company = c.Id }); // fotos de carnet (foto-carnet.sql)
            var (json, err) = await Supa.RpcAsync("list_members", new { p_company = c.Id });
            try { await tPts; } catch { }
            _memberCardData.Clear(); _memberJoined.Clear();
            try
            {
                var (cj, ce) = await tCards;
                if (ce == null && !string.IsNullOrWhiteSpace(cj))
                {
                    using var cd = JsonDocument.Parse(cj);
                    foreach (var e in cd.RootElement.EnumerateArray())
                    {
                        DateTime.TryParse(Str(e, "joined_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var j);
                        _memberCardData[Str(e, "user_id")] = (j, Num(e, "rank_km"), Num(e, "services_count"), Num(e, "total_km"), Num(e, "total_net"));
                    }
                }
            }
            catch { }
            try
            {
                var (pj, pe) = await tPhotos;
                if (pe == null && !string.IsNullOrWhiteSpace(pj))
                {
                    using var pd = JsonDocument.Parse(pj);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in pd.RootElement.EnumerateArray()) { string uid = Str(e, "user_id"); seen.Add(uid); SetMemberPhoto(uid, Str(e, "photo")); }
                    foreach (var k in new List<string>(_memberPhotos.Keys)) if (!seen.Contains(k) && !string.Equals(k, Supa.UserId, StringComparison.OrdinalIgnoreCase)) _memberPhotos.Remove(k);
                }
            }
            catch { }
            UpdateRailAvatar();
            _members.Clear();
            if (err == null)
            {
                try
                {
                    using var d = JsonDocument.Parse(json);
                    foreach (var e in d.RootElement.EnumerateArray())
                    {
                        _members.Add(new EmpMember { UserId = Str(e, "user_id"), Username = Str(e, "username"), Role = Str(e, "role") });
                        if (DateTime.TryParse(Str(e, "joined_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var jj))
                            _memberJoined[Str(e, "user_id")] = jj;
                    }
                }
                catch { }
            }
            _myRole = _members.Find(m => m.UserId == Supa.UserId)?.Role;
            if (_memberCards != null)
            {
                _memberCards.EmptyText = _members.Count == 0 ? (err != null ? Tr("Error: ") + err : Tr("Sin socios.")) : null;
                FillMemberCards();
            }
            UpdateRoleUi();
            UpdateCompanyDash();
            LoadJoinRequests(c);
            LoadPurchaseRequests();   // contador «Compra (n)» para gerente y gestores
            if (_empSubtab != 6) LoadReview(onlyCount: true);   // contador «Revisión (n)»
        }

        // Carnets de los socios (con el filtro de texto aplicado).
        void FillMemberCards()
        {
            if (_memberCards == null) return;
            string f = (_memberSearch?.Box.Text ?? "").Trim();
            var list = new List<MemberCardGrid.Member>();
            foreach (var m in _members)
            {
                string name = m.Username.Length > 0 ? m.Username : "—";
                if (f.Length > 0 && name.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0 && TrRole(m.Role).IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var meta = new List<string>();
                DateTime joined = _memberJoined.TryGetValue(m.UserId, out var jj) ? jj : DateTime.MinValue;
                bool has = _memberCardData.TryGetValue(m.UserId, out var cd);
                if (has && cd.joined != DateTime.MinValue) joined = cd.joined;
                if (joined != DateTime.MinValue) meta.Add(string.Format(Tr("Desde {0}"), MonthName(joined.ToLocalTime())));
                if (has) meta.Add(Tr(RankTiers[RankTierIndex(cd.rankKm)].name));
                var card = new MemberCardGrid.Member
                {
                    UserId = m.UserId, Name = name, Role = m.Role, RoleLabel = TrRole(m.Role), Meta = string.Join("  ·  ", meta),
                    Services = has ? cd.services.ToString("N0", EsEs) : "—",
                    Km = has ? cd.km.ToString("N0", EsEs) + " km" : "—",
                    Net = has ? cd.net.ToString("+#,##0;−#,##0", EsEs) + " €" : "—", NetNeg = has && cd.net < 0,
                    Photo = _memberPhotos.TryGetValue(m.UserId, out var mp) ? mp.img : null
                };
                if (_memberPoints.TryGetValue(m.UserId, out var p) && p.points >= 0)
                {
                    card.Points = p.points;
                    if (p.until != null)
                    {
                        card.Suspended = true; card.PointsColor = Carne.Red;
                        card.PointsText = string.Format(Tr("suspendido hasta el {0}"), p.until.Value.ToLocalTime().ToString("d MMM", I18n.English ? CultureInfo.GetCultureInfo("en-GB") : EsEs));
                    }
                    else
                    {
                        string st = p.points < 6 ? Tr("rango congelado") : p.points < 10 ? Tr("aviso") : Tr("en regla");
                        card.PointsText = p.points + " / " + Carne.MaxPoints + "  ·  " + st;
                        card.PointsColor = Carne.PointsColor(p.points);
                    }
                }
                else card.PointsText = "—";
                list.Add(card);
            }
            _memberCards.CompanyName = _empSel?.Name ?? "";
            try { _memberCards.CompanyLogo = _empSel != null ? LogoFor(_empSel.Id, _empSel.Logo) : null; } catch { _memberCards.CompanyLogo = null; }
            _memberCards.SetMembers(list);
        }

        // Socio elegido (índice en _members; −1 = ninguno).
        int MemberSelectedIndex() => _memberCards == null ? -1 : _members.FindIndex(x => x.UserId == _memberCards.SelectedId);

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
            foreach (var ip in new[] { _tarIncome, _tarCanon, _tarEnergy, _tarSalary, _tarSalaryHour, _tarSalaryMaxH })
                if (ip != null) ip.Box.ReadOnly = tarRo;
            if (_tarBalanceRow != null) _tarBalanceRow.Visible = Supa.IsSuperadmin;   // saldo: solo superadmin
            if (_defBalanceRow != null) _defBalanceRow.Visible = Supa.IsSuperadmin;   // saldo inicial global: solo superadmin
            if (_fleetSettingsRow != null) _fleetSettingsRow.Visible = Supa.IsSuperadmin; // economía de flota: solo superadmin
            if (_svcDelBtn != null) _svcDelBtn.Visible = Supa.IsSuperadmin;           // borrar servicio: solo superadmin
            if (_svcAnnulBtn != null) _svcAnnulBtn.Visible = Supa.IsSuperadmin;       // anular servicio: solo superadmin
            if (_ledgerDelBtn != null) _ledgerDelBtn.Visible = Supa.IsSuperadmin;     // borrar movimiento: solo superadmin
            bool fleetManage = CanManage() || Supa.IsSuperadmin;                      // flota: dueño/gestor/superadmin
            if (_fleetBuyBtn != null) _fleetBuyBtn.Visible = fleetManage;
            if (_fleetRentBtn != null) _fleetRentBtn.Visible = fleetManage;
            if (_fleetRemoveBtn != null) _fleetRemoveBtn.Visible = fleetManage;         // Flota: dar de baja
            if (_fleetPlateBtn != null) _fleetPlateBtn.Visible = fleetManage;           // Flota: asignar matrícula
            if (_empLogoPic != null) _empLogoPic.Cursor = manage ? Cursors.Hand : Cursors.Default;   // cambiar logotipo: solo gestión
            if (_empLogoTip != null && _empLogoPic != null) _empLogoTip.SetToolTip(_empLogoPic, manage ? Tr("Cambiar logotipo") : "");
            UpdateTrainShortcuts();   // «Comprar este tren» en Conducción libre/Horarios según el rol
            UpdateCoRouteButtons();   // Rutas: «Solicitar autorización» solo para gerente y gestores
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
            //            · 6 Revisión (carné por puntos) · 7 Usuarios · 8 Todas las empresas · 9 Flota · 10 Compra · 11 Megafonía · 12 Chat
            // Megafonía: la habilita el superadmin empresa por empresa (companies.pa_enabled). Quien
            // gestiona solo la ve si está habilitada; el superadmin la ve siempre (para habilitarla).
            bool pa = su || (PaEnabledHere() && CanManage());
            bool[] show = hasCompany
                ? new[] { true, true, CanManage() || su, su, true, true, su, su, su, true, CanManage() || su, pa, true, true, su, su || _routeMode != "collect", su }   // Compra: solo gestión · Revisión, Préstamos y Catálogo de rutas: superadmin · Rutas: no en «solo recopilar»
                : new[] { false, false, false, false, true, true, su, su, su, false, false, false, false, false, su, false, su };
            _empSubShow = show;   // (con la ventana oculta, Visible siempre dice false: la precarga mira esto)
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
            int i = MemberSelectedIndex();
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
            // Precio por hora y tope (salario-por-tiempo.sql); sin ese SQL, los campos se quedan vacíos.
            var (sj, se) = await Supa.RpcAsync("get_salary_rates", new { });
            if (_tarSalaryHour == null) return;
            try
            {
                using var sd = JsonDocument.Parse(se == null ? sj : "{}");
                _tarSalaryHour.Box.Text = se == null ? Num(sd.RootElement, "per_hour").ToString("0.##", EsEs) : "";
                _tarSalaryMaxH.Box.Text = se == null ? Num(sd.RootElement, "max_hours").ToString("0.##", EsEs) : "";
            }
            catch { }
        }

        async void SaveTariffs()
        {
            if (!Supa.IsSuperadmin) { Msg(_tariffMsg, Tr("Solo el administrador puede modificar las tarifas."), true); return; }
            Msg(_tariffMsg, Tr("Guardando tarifas…"), false);
            var (_, err) = await Supa.RpcAsync("set_default_tariffs", new
            {
                p_income = ParseNum(_tarIncome.Box.Text),
                p_canon = ParseNum(_tarCanon.Box.Text),
                p_energy = ParseNum(_tarEnergy.Box.Text),
                p_salary = ParseNum(_tarSalary.Box.Text)
            });
            if (err != null) { Msg(_tariffMsg, Tr("Error: ") + err, true); return; }
            if (_tarSalaryHour != null && _tarSalaryHour.Box.Text.Trim().Length > 0)
            {
                var (_, serr) = await Supa.RpcAsync("set_salary_rates", new { p_per_hour = ParseNum(_tarSalaryHour.Box.Text), p_max_hours = ParseNum(_tarSalaryMaxH.Box.Text) });
                if (serr != null) { Msg(_tariffMsg, Tr("Error: ") + serr, true); return; }
            }
            Msg(_tariffMsg, Tr("Tarifas globales guardadas."), false);
        }

        async void AdminSetBalance()
        {
            if (_empSel == null) return;
            if (!Supa.IsSuperadmin) { Msg(_tariffMsg, Tr("Solo el administrador puede fijar el saldo."), true); return; }
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
            if (!Supa.IsSuperadmin) { Msg(_tariffMsg, Tr("Solo el administrador puede cambiar el saldo inicial."), true); return; }
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
            LoadFareFormula();
            LoadTrainMode();
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
                if (Num(root, "capacity_base") > 0) _fsCapBaseVal = Num(root, "capacity_base");
                if (_fsFareBase != null) _fsFareBase.Box.Text = Num(root, "fare_base").ToString("0.##", EsEs);
                if (_fsPaxDemand != null) _fsPaxDemand.Box.Text = Num(root, "pax_demand").ToString("0.##", EsEs);
            }
            catch { }
        }

        // La fórmula del billete tal como la aplica el servidor al registrar (close_service): confort y velocidad media
        // del viaje por sus coeficientes (app_settings.fare_comfort_k / fare_speed_k, 0,02 y 0,005 por defecto).
        Label _fsFareLbl;
        string FareFormulaText(double kComfort, double kSpeed)
            => string.Format(Tr("Billete base (€) — viajeros: billete = base + confort × {0} + velocidad media × {1} + €/km × km a bordo"),
                             kComfort.ToString("0.####", EsEs), kSpeed.ToString("0.####", EsEs));

        async void LoadFareFormula()
        {
            if (_fsFareLbl == null) return;
            var (json, err) = await Supa.SelectAsync("app_settings?select=fare_comfort_k,fare_speed_k&id=eq.1");
            if (err != null) return;   // servidor sin tarifas-equilibrio.sql: los de por defecto
            try
            {
                using var d = JsonDocument.Parse(json);
                if (d.RootElement.ValueKind != JsonValueKind.Array || d.RootElement.GetArrayLength() == 0) return;
                var e = d.RootElement[0];
                double kc = e.TryGetProperty("fare_comfort_k", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : 0.02;
                double ks = e.TryGetProperty("fare_speed_k", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetDouble() : 0.005;
                _fsFareLbl.Text = FareFormulaText(kc, ks);
            }
            catch { }
        }

        // €/km por viajero del billete (billete-por-km.sql). Sin ese SQL, la celda se queda con 0,08.
        async void LoadFarePerKm()
        {
            if (_fsFareKm == null || !Supa.IsSuperadmin) return;
            var (json, err) = await Supa.RpcAsync("get_fare_per_km", new { });
            if (err != null) return;
            if (double.TryParse((json ?? "").Trim().Trim('"'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                _fsFareKm.Box.Text = v.ToString("0.####", EsEs);
        }

        // Guarda la economía de flota (escala de precio + % alquiler + % mantenimiento; SOLO superadmin).
        async void SaveFleetSettings()
        {
            if (!Supa.IsSuperadmin) { Msg(_tariffMsg, Tr("Solo el administrador puede modificar la economía de flota."), true); return; }
            Msg(_tariffMsg, Tr("Guardando economía de flota…"), false);
            var (_, err) = await Supa.RpcAsync("set_fleet_settings", new
            {
                p_scale = ParseNum(_fsScale.Box.Text),
                p_rental_pct = ParseNum(_fsRentPct.Box.Text),
                p_maint_pct = ParseNum(_fsMaintPct.Box.Text),
                p_capacity_base = _fsCapBaseVal,
                p_fare_base = ParseNum(_fsFareBase.Box.Text),
                p_pax_demand = ParseNum(_fsPaxDemand.Box.Text)
            });
            if (err != null) { Msg(_tariffMsg, Tr("Error: ") + err, true); return; }
            if (_fsFareKm != null)
            {
                var (_, e2) = await Supa.RpcAsync("set_fare_per_km", new { p_value = ParseNum(_fsFareKm.Box.Text) });
                if (e2 != null) { Msg(_tariffMsg, Tr("Error: ") + e2, true); return; }
            }
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
            if (!Supa.IsSuperadmin) { Msg(_tariffMsg, Tr("Solo el administrador puede publicar actualizaciones."), true); return; }
            string cur = Updater.CurrentVersionText;
            var latest = await Updater.GetLatestAsync();
            if (latest != null && Updater.TryParse(latest.Version, out var lv) && lv >= Updater.CurrentVersion)
            {
                Msg(_tariffMsg, string.Format(Tr("La versión {0} no es más nueva que la publicada ({1}). Sube la versión en SelectOR.csproj y compila."), cur, latest.Version), true);
                return;
            }
            // Lo que se publica debería ser lo firmado por SignPath: si no, se avisa (los antivirus desconfían).
            var sinFirma = Updater.UnsignedPackageFiles();
            if (sinFirma.Count > 0 && ThemedBox.Show(this,
                    string.Format(Tr("Estos archivos NO están firmados: {0}.\n\nSin firma, los antivirus pueden avisar al descargar o instalar SelectOR. Instala primero los firmados (flujo «Compilar y firmar» de GitHub y tools\\instalar-firmado.ps1).\n\n¿Publicar igualmente sin firmar?"), string.Join(", ", sinFirma)),
                    "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (ThemedBox.Show(this, string.Format(Tr("¿Publicar SelectOR {0}? Todos los usuarios recibirán el aviso de actualización."), cur),
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
        RoundedInput _pmCapPct;
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
            if (_pmCapPct != null) _pmCapPct.Box.Text = F(c.CapacityPct);
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
            if (_pmCapPct != null) c.CapacityPct = V(_pmCapPct);

            string bad = null;
            if (!(c.CapacityPct > 0 && c.CapacityPct <= 100)) bad = Tr("El % de plazas debe estar entre 0 y 100.");
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
            if (!Supa.IsSuperadmin) { Msg(_tariffMsg, Tr("Solo el administrador puede modificar el modelo de viajeros."), true); return; }
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
                    p_capacity_base = _fsCapBaseVal,
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
        void UpdateRankLayout() { }

        async void LoadRankings()
        {
            if (!Supa.IsLoggedIn) return;
            var b = _rankCompanies;
            if (b != null)
            {
                if (b.Entries.Count == 0) { b.EmptyText = Tr("Cargando…"); b.Invalidate(); }
                var (json, err) = await Supa.RpcAsync("public_company_ranking", new { });
                var mias = new HashSet<string>();
                foreach (var co in _empCompanies) mias.Add(co.Id);
                var list = new List<PodiumBoard.Entry>();
                if (err == null)
                    try
                    {
                        using var d = JsonDocument.Parse(json);
                        foreach (var e in d.RootElement.EnumerateArray())
                        {
                            string cid = Str(e, "company_id"), name = Str(e, "name");
                            double km = Num(e, "total_km"), bal = Num(e, "balance"), sv = Num(e, "services_count");
                            Image logo = null;
                            try { logo = LogoFor(cid, Str(e, "logo")); } catch { }
                            list.Add(new PodiumBoard.Entry
                            {
                                Id = cid, Name = name, Logo = logo, Pos = list.Count + 1, Mine = mias.Contains(cid),
                                Value = km.ToString("N0", EsEs) + " km", Detail = string.Format(Tr("{0} servicios"), sv.ToString("N0", EsEs)),
                                Cols = new (string, string, Color?)[] { (Tr("KM"), km.ToString("N0", EsEs), null), (Tr("SERVICIOS"), sv.ToString("N0", EsEs), null), (Tr("SALDO"), bal.ToString("N0", EsEs) + " €", null) }
                            });
                        }
                    }
                    catch { }
                b.Title = Tr("Ranking público de empresas (por km)");
                b.Entries = list;
                b.EmptyText = err != null ? Tr("Error: ") + err : list.Count == 0 ? Tr("Sin datos todavía.") : null;
                b.Relayout(true);
            }
            if (_empSel != null) await LoadDriverRanking(_empSel);
            else if (_rankDrivers != null) { _rankDrivers.Entries = new List<PodiumBoard.Entry>(); _rankDrivers.EmptyText = Tr("Selecciona una empresa para ver su ranking."); _rankDrivers.Relayout(true); }
        }

        async Task LoadDriverRanking(EmpCompany c)
        {
            var b = _rankDrivers;
            if (b == null) return;
            if (b.Entries.Count == 0) { b.EmptyText = Tr("Cargando…"); b.Invalidate(); }
            var tPhotos = Supa.RpcAsync("company_member_photos", new { p_company = c.Id });   // la foto de perfil de cada uno
            var (json, err) = await Supa.RpcAsync("company_driver_ranking", new { p_company = c.Id });
            try
            {
                var (pj, pe) = await tPhotos;
                if (pe == null && !string.IsNullOrWhiteSpace(pj))
                {
                    using var pd = JsonDocument.Parse(pj);
                    foreach (var e in pd.RootElement.EnumerateArray()) SetMemberPhoto(Str(e, "user_id"), Str(e, "photo"));
                }
            }
            catch { }
            var list = new List<PodiumBoard.Entry>();
            if (err == null)
                try
                {
                    using var d = JsonDocument.Parse(json);
                    foreach (var e in d.RootElement.EnumerateArray())
                    {
                        string name = Str(e, "username"); if (name.Length == 0) name = "—";
                        double km = Num(e, "total_km"), net = Num(e, "total_net"), sv = Num(e, "services_count");
                        string sign = net.ToString("+#,##0;−#,##0", EsEs) + " €";
                        list.Add(new PodiumBoard.Entry
                        {
                            Id = Str(e, "user_id"), Name = name, Pos = list.Count + 1, Mine = Str(e, "user_id") == Supa.UserId,
                            Logo = _memberPhotos.TryGetValue(Str(e, "user_id"), out var ph) ? ph.img : null,   // sin foto: sus iniciales
                            Value = km.ToString("N0", EsEs) + " km", Detail = string.Format(Tr("{0} servicios"), sv.ToString("N0", EsEs)) + " · " + sign,
                            Cols = new (string, string, Color?)[] { (Tr("SERVICIOS"), sv.ToString("N0", EsEs), null), (Tr("KM"), km.ToString("N0", EsEs), null), (Tr("NETO"), sign, net < 0 ? RedC : (Color?)null) }
                        });
                    }
                }
                catch { }
            b.Title = string.Format(Tr("Maquinistas de {0}"), c.Name);
            b.Entries = list;
            b.EmptyText = err != null ? Tr("Error: ") + err : list.Count == 0 ? Tr("Sin servicios completados todavía.") : null;
            b.Relayout(true);
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
            int i = MemberSelectedIndex();
            if (i < 0 || i >= _members.Count) { Msg(_memberMsg, Tr("Selecciona un socio de la lista."), true); return; }
            var m = _members[i];
            if (m.Role == "owner") { Msg(_memberMsg, Tr("No se puede quitar al gerente."), true); return; }
            if (ThemedBox.Show(this, string.Format(Tr("¿Quitar a «{0}» de la empresa?"), m.Username), "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
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
                var p = DrivenPath();
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
        // Tracción del .eng, como la entiende Open Rails: el Type ( Electric | Diesel | Steam ) del bloque Engine
        // (el Type del bloque Wagon dice «Engine» y no sirve). Antes se buscaba la palabra «diesel» o «steam» en
        // todo el texto, y muchas eléctricas salían diésel (llevan el parámetro genérico de MSTS
        // DieselEngineSpeedOfMaxTractiveEffort o comentarios) o vapor (calefacción de vapor, SteamHeat).
        static readonly System.Text.RegularExpressions.Regex EngBlockRx = new(@"(?<![\w.])Engine\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static readonly System.Text.RegularExpressions.Regex EngTypeRx = new(@"(?<![\w.])Type\s*\(\s*""?([A-Za-z]+)""?\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static readonly System.Text.RegularExpressions.Regex IncludeRx = new(@"(?<![\w.])Include\s*\(\s*""?([^"")]+?)""?\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        static string EngineTypeOf(string t, string path)
        {
            string v = DeclaredEngineType(t, path, 0);
            if (v != null) return v;
            // Sin Type (o en un archivo incluido que no está): las palabras, sin los parámetros que confundían.
            string low = t.ToLowerInvariant().Replace("dieselenginespeedofmaxtractiveeffort", "").Replace("steamheat", "");
            return low.Contains("diesel") ? "diesel" : low.Contains("steam") ? "steam" : "electric";
        }

        // El Type del bloque Engine; los .eng de las carpetas OpenRails suelen empezar con Include ( ../original.eng )
        // y no repetirlo: entonces se busca en el incluido (hasta 3 niveles). null si nadie lo declara.
        static string DeclaredEngineType(string t, string path, int depth)
        {
            if (string.IsNullOrEmpty(t)) return null;
            var m = EngBlockRx.Match(t);
            if (m.Success)
            {
                var k = EngTypeRx.Match(t, m.Index + m.Length);
                if (k.Success)
                {
                    string v = k.Groups[1].Value.ToLowerInvariant();
                    if (v == "electric" || v == "diesel" || v == "steam") return v;
                }
            }
            if (depth >= 3 || string.IsNullOrEmpty(path)) return null;
            foreach (System.Text.RegularExpressions.Match i in IncludeRx.Matches(t))
            {
                try
                {
                    string rel = i.Groups[1].Value.Trim().Replace(@"\\", @"\").Replace('/', System.IO.Path.DirectorySeparatorChar);
                    string q = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? "", rel));
                    if (!System.IO.File.Exists(q)) continue;
                    string v = DeclaredEngineType(ReadHead(q, 200000), q, depth + 1);
                    if (v != null) return v;
                }
                catch { }
            }
            return null;
        }

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
                    v.EngineType = EngineTypeOf(t, path);
                }
            }
            catch { }
            lock (_vehStats) _vehStats[path] = v;
            return v;
        }

        // Nº de coches de un .con (sin leer el archivo si ya está en la caché).
        static int ConsistCarCount(string conPath) => ConsistCarRefs(conPath).Count;
        static int ConsistEngineCount(string conPath) { int n = 0; foreach (var r in ConsistCarRefs(conPath)) if (r.isEngine) n++; return n; }

        // Coches del tren y cuántos son motrices, en corto para la tabla: «8 (2M)» (M = motriz; en el detalle
        // del servicio va entero, «8  (2 motrices)», como en el Editor de composiciones). Sin datos: «—».
        static string CarsText(int cars, int engines) =>
            cars <= 0 ? "—" : cars.ToString("N0", EsEs) + (engines > 0 ? " (" + engines + "M)" : "");

        // Datos de un vehículo para el resto de secciones (editor incluido).
        static VehStats VehicleStats(string path) => Veh(path);

        // El editor toca archivos del contenido: sus datos se vuelven a leer la próxima vez.
        public static void ClearContentCaches()
        {
            lock (_vehStats) _vehStats.Clear();
            lock (_conRefs) _conRefs.Clear();
            lock (_trainVehCache) _trainVehCache.Clear();   // vehículos de cada .con (trenes de empresa)
            lock (_missingCache) _missingCache.Clear();     // lo que le falta a cada .con (trenes incompletos)
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
                        PaEnabled = Flag(e, "pa_enabled"), Code = Str(e, "code")
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
        // Rótulo de sección del estilo común: gris, pequeño y en mayúsculas (el verde queda para lo activo).
        static Label EmpHeader(string t) => new Label { Text = I18n.T(t), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(8f, FontStyle.Bold), Margin = new Padding(2, 14, 2, 4), UseCompatibleTextRendering = false };
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
            var lb = new BufferedListBox
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
        // Lista en tarjetas que sustituye a una tabla (CardTable): mismas operaciones, otra presentación.
        static CardTable EmpCards() => new CardTable { Dock = DockStyle.Fill, Margin = new Padding(2, 4, 2, 4) };

        static RoundedInput EmpSearch(CardTable table, int width = 300)
        {
            var box = new RoundedInput(I18n.T("🔎  Filtrar…")) { Anchor = AnchorStyles.Left, Width = width, Height = 34, Margin = new Padding(2, 2, 8, 4) };
            box.Box.TextChanged += (s, e) => table.Filter(box.Box.Text);
            return box;
        }

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
