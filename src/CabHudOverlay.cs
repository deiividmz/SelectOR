// Pupitre sobre Open Rails: manómetro de aire (TDP/TFA), cilindro de freno y velocímetro, con una
// fila de pictogramas debajo (puertas, pantógrafo, disyuntor, faros, arena, limpiaparabrisas y
// bocinas) que cambian según la posición del mando. Esferas: réplica de Cockpit-SF, con los
// centros de los tres a la misma altura.
//  · SIN MARCO: la ventana es «layered» con transparencia real por píxel (UpdateLayeredWindow), así
//    que solo se ven las esferas flotando sobre el simulador, con los bordes suaves; los clics en
//    las zonas vacías llegan al simulador.
//  · No roba el foco (WS_EX_NOACTIVATE). Se arrastra pinchando en cualquier esfera y se escala con
//    el tirador de la esquina inferior derecha o con la rueda del ratón. Por defecto, abajo a la
//    derecha.
//  · La escala del velocímetro sale de la velocidad máxima del tren que se conduce.
//  · Los pictogramas son BOTONES: mandan la orden al simulador con la tecla que el maquinista tenga
//    configurada en Open Rails (ver OrControl). Al pasar el ratón se ve qué hace y con qué tecla.
//  · Agujas animadas como en Cockpit-SF: los manómetros persiguen su valor a ~60 fps avanzando
//    un 8 % de lo que les falta en cada fotograma de 17 ms, y el velocímetro va en línea recta
//    hasta cada nueva lectura en 300 ms. Solo se redibuja mientras algo se mueve.
//  · De noche en el simulador (sol más de ~5° bajo el horizonte, el mismo umbral con el que Open
//    Rails enciende la cabina nocturna) las esferas se ven retroiluminadas.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ORTS.Common.Input;
using System.Windows.Forms;

namespace SelectOR
{
    public class CabHudOverlay : Form
    {
        readonly AppPrefs _prefs;
        readonly CabPoller _poller;
        double _speedMax;                // fondo de escala del velocímetro (en km/h, como todo lo interno)
        // Unidades del velocímetro del tren (.cvf): todo se calcula en km/h y se DIBUJA en la unidad
        // del tren. _uf convierte km/h a esa unidad (1 en km/h; 1/1,609344 en millas por hora).
        readonly bool _mph, _unitsKnown;
        readonly double _uf = 1;
        const double KmhPorMph = 1.609344;
        // Manómetros en la unidad de la cabina: las presiones llegan en bar y se DIBUJAN en esa unidad
        // (f = bar → unidad) con la escala de su esfera.
        readonly string _airUnit = "bar", _bcUnit = "bar";
        readonly double _airF = 1, _bcF = 1, _airMax = 12, _bcMax = 10;

        /// <summary>Unidades y escalas de los instrumentos de la cabina del tren (leídas de su .cvf).</summary>
        public sealed class CabUnits
        {
            public bool? SpeedMph;        // null = no se sabe (km/h)
            public double SpeedScale;     // fondo de escala del velocímetro, en su unidad (0 = no se sabe)
            public string AirUnits, BcUnits;   // Units del .cvf de MAIN_RES/BRAKE_PIPE y de BRAKE_CYL
            public double AirScale, BcScale;   // sus fondos de escala, en esa unidad
        }

        // Unidad de presión del .cvf → rótulo y factor desde bar.
        static (string label, double f) PressUnit(string cvf)
        {
            string u = (cvf ?? "").ToUpperInvariant();
            if (u.Contains("PSI")) return ("psi", 14.5037738);
            if (u.Contains("KILOPASCAL") || u.Contains("KILO_PASCAL") || u == "KPA") return ("kPa", 100);
            if (u.Contains("MEGAPASCAL") || u.Contains("MEGA_PASCAL") || u == "MPA") return ("MPa", 0.1);
            if (u.Contains("KGS_PER_SQUARE_CM") || u.Contains("KGF")) return ("kgf/cm²", 1.01971621);
            if (u.Contains("INCHES_OF_MERCURY") || u.Contains("INHG")) return ("inHg", 29.5299831);
            return ("bar", 1);
        }

        // Unidad, factor y escala de un manómetro a partir de lo que declara el .cvf. Hay cabinas con
        // unidades que no cuadran con su esfera (un cilindro «en kPa» de 0-11, que es de bar): si la
        // escala no es creíble para esa unidad, la unidad se deduce por el tamaño de la escala.
        static (string label, double f, double max) PressDial(string cvfUnits, double scale, double defBar)
        {
            var (lab, f) = PressUnit(cvfUnits);
            if (scale <= 0 || scale > 5000) return (lab, f, NiceMax(lab == "inHg" ? 30 : defBar * f));
            bool creible = lab == "inHg" ? scale >= 10 && scale <= 40 : scale / f >= 3 && scale / f <= 30;
            if (!creible)
                (lab, f) = scale <= 20 ? ("bar", 1.0) : scale <= 40 ? ("inHg", 29.5299831)
                         : scale <= 300 ? ("psi", 14.5037738) : ("kPa", 100.0);
            return (lab, f, scale);
        }

        // Número redondo por encima (para una escala por defecto en psi, kPa…).
        static double NiceMax(double v)
        {
            if (v <= 0) return 10;
            foreach (var step in new double[] { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000 })
                if (Math.Ceiling(v / step) <= 12) return Math.Ceiling(v / step) * step;
            return Math.Ceiling(v / 1000) * 1000;
        }
        readonly double _trainMaxKmh;    // velocidad máxima del tren según sus .eng (0 = sin dato)
        bool _scaleFromCab;              // la escala ya se ha tomado del velocímetro de la cabina
        CabValues _v = new CabValues();
        bool _hover, _night;
        float _scale = 1f;
        // Para saber si es de noche: posición del tren (la pasa el menú) y día del año de la estación.
        readonly int _dayOfYear;
        double _lat, _lon; bool _geo;

        // Animación de agujas (valores mostrados, que persiguen a los leídos).
        readonly Timer _anim;
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        double _lastFrameMs;
        double _dMr, _dBp, _dBc;                       // manómetros
        // Velocímetro PROGRESIVO: con cada lectura se estima la aceleración (km/h por segundo) y,
        // entre lecturas, la aguja sigue esa tendencia de forma continua (sin esperar a la siguiente
        // lectura, que es lo que daba los «parones» al acelerar o frenar fuerte).
        // Aguja del velocímetro y triángulo del «Speed target»: el MISMO seguimiento (tendencia
        // entre lecturas + muelle amortiguado), para que se muevan con idéntica suavidad.
        readonly Seguidor _spd = new Seguidor(), _cru = new Seguidor { Consigna = true };
        double _dSpeed;
        // Reloj común con el lector: la lectura lleva la hora EXACTA a la que respondió OR.
        readonly double _clockBaseMs = System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // CURVA DE FRENADO del triángulo: próximo límite más bajo por delante (Track Monitor) y
        // distancia estimada hasta él. El triángulo marca v = √(v_obj² + 2·a·d), la velocidad a la
        // que aún se llega frenando con la deceleración de servicio del tren (a, m/s²).
        readonly double _brakeDecel;
        double _tgtLimit = -1, _tgtDist, _tgtRow;     // km/h · m estimados · m de su fila en el gráfico
        double _limVal;                               // límite actual (km/h)
        double _curveTarget = double.NaN;             // límite objetivo mientras la curva manda
        double _dCruise = double.NaN;                 // triángulo amarillo: velocidad objetivo del regulador (mostrada)
        bool _limShown;                               // hay límite (se enseña la señal)
        bool _animInit;

        // Tamaño de diseño (a escala 1): esferas arriba y la fila de pilotos debajo.
        // Las tres esferas comparten la altura del centro (Cy). El velocímetro se dibuja en un
        // cuadrado de 300 cuyo centro cae 9 px por debajo del centro del cuadrado, con radio 138.
        const int BaseW = 664, BaseH = 386;
        const float ScaleMin = 0.55f, ScaleMax = 1.6f;
        const float Cy = 147;
        const float AirX = 114, AirR = 110;          // manómetro TDP/TFA
        const float BcX = 300, BcR = 70;             // cilindro de freno
        static readonly RectangleF RSpeed = new RectangleF(368, Cy - 159, 300, 300);
        const float SpeedX = 368 + 150, SpeedR = 300 * 0.46f;
        const float LabelPx = 15;
        const float TilesY = 298, TileSize = 58;

        bool _down, _dragging, _resizing;
        int _iconHover = -1, _iconDown = -1;          // pictograma bajo el ratón / pulsado
        string _held; long _heldAtMs;                 // orden «de mantener» pulsada ahora
        // Tras un clic, el icono pasa YA al estado que se ha pedido (sin esperar a que el simulador lo
        // publique). Si el simulador no lo confirma antes de que caduque, vuelve al estado real.
        readonly Dictionary<Ico, (int valor, long hasta)> _previsto = new();
        long _lastCmdAt = -100000;   // última orden mandada (se ignoran clics a menos de 350 ms)
        // Tracción: «electric», «diesel», «steam» o null si no se supo leer del .eng. En diésel y
        // vapor no hay pantógrafo ni disyuntor. Lo que diga el simulador en marcha manda: si informa
        // de pantógrafos, se muestran; si da RPM y nada de pantógrafo/disyuntor, se quitan.
        readonly string _traction;
        bool _seenElectric, _seenDiesel;
        // Puertas: solo en trenes de viajeros (null = no se supo; si la cabina tiene mando de
        // puertas o el simulador informa de puertas abiertas, también se muestran).
        readonly bool? _passenger;
        bool _seenDoors;
        // Órdenes «de mantener» pulsadas desde el teclado (la cabina puede no tener indicador).
        bool _kbHorn, _kbBell, _kbSand;
        int _kbTick;
        Point _downScreen, _formAtDown; float _scaleAtDown; Size _sizeAtDown;
        RectangleF _hitGrip;

        // Escala del velocímetro a partir de la velocidad máxima del tren: un poco por encima de
        // ella y en un número redondo (0-60, 0-140, 0-180, 0-350…).
        public static double ScaleForTrain(double vmaxKmh)
        {
            if (vmaxKmh <= 0) return 160;
            double step = vmaxKmh <= 190 ? 20 : 50;
            return Math.Max(60, Math.Ceiling(vmaxKmh * 1.05 / step) * step);
        }

        public CabHudOverlay(AppPrefs prefs, int port, double trainMaxKmh, int dayOfYear = 173, string traction = null, bool? passenger = null,
                             double brakeDecel = 0.65, CabUnits units = null)
        {
            units ??= new CabUnits();
            bool? speedoMph = units.SpeedMph;
            double cabScale = units.SpeedScale;
            // Manómetros: unidad y escala de las esferas de la cabina; si no las declara, 12 bar
            // (depósito/tubería) y 10 bar (cilindro) pasados a esa unidad y redondeados.
            (_airUnit, _airF, _airMax) = PressDial(units.AirUnits, units.AirScale, 12);
            (_bcUnit, _bcF, _bcMax) = PressDial(units.BcUnits, units.BcScale, 10);
            _brakeDecel = Math.Max(0.35, Math.Min(1.3, brakeDecel));
            _traction = traction;
            _passenger = passenger;
            _prefs = prefs;
            _dayOfYear = dayOfYear;
            OrControl.Reload();   // teclado de OR tal como lo tenga configurado ahora
            _trainMaxKmh = trainMaxKmh > 0 ? trainMaxKmh : 0;
            _unitsKnown = speedoMph != null;
            _mph = speedoMph == true;
            _uf = _mph ? 1 / KmhPorMph : 1;
            // Escala: la de la esfera de la cabina (.cvf, ya en su unidad) o, si no la declara, un número
            // redondo en esa unidad un poco por encima de la velocidad máxima del tren.
            if (cabScale >= 25 && cabScale <= 600) { _speedMax = cabScale / _uf; _scaleFromCab = true; }
            else _speedMax = ScaleForTrain(_trainMaxKmh * _uf) / _uf;
            _scale = Math.Max(ScaleMin, Math.Min(ScaleMax, prefs != null && prefs.CabHudScale > 0 ? prefs.CabHudScale : 1f));

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = TargetSize();
            Location = InitialLocation();

            _poller = new CabPoller(port, v =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke((Action)(() => OnData(v))); } catch { }
            });
            _anim = new Timer { Interval = 16 };
            _anim.Tick += (s, e) => Animate();
        }

        // Las señales de límite y de anuncio nunca pasan de la velocidad máxima del tren (.eng): si la
        // vía admite 160 y el tren 120, el límite que manda para el maquinista es 120.
        double TrainCap(double kmh) => _trainMaxKmh > 0 ? Math.Min(kmh, Math.Round(_trainMaxKmh)) : kmh;

        // Escala del velocímetro: la de la esfera de la cabina del tren (ScaleRange del .cvf, que da Open
        // Rails) y, mientras no llegue, la calculada con la velocidad máxima de sus .eng.
        void ApplyCabScale(double dial)
        {
            if (_scaleFromCab || dial <= 0) return;
            // Llega en la unidad de la cabina. Si no se ha podido leer el .cvf, una esfera claramente por
            // debajo de la velocidad del tren es de millas.
            bool enMillas = _unitsKnown ? _mph : _trainMaxKmh > 0 && dial < _trainMaxKmh * 0.9;
            double kmh = enMillas ? dial * KmhPorMph : dial;
            if (kmh < 40 || kmh > 1000) return;
            _scaleFromCab = true;
            _speedMax = _unitsKnown ? kmh : Math.Round(kmh);
            _cacheKey = null;   // la esfera (escala y números) se vuelve a dibujar
        }

        void OnData(CabValues v)
        {
            // Velocidad o límite leídos del mando de la cabina: vienen en SUS unidades.
            if (_mph && v.SpeedFromCab) { v.SpeedKmh *= KmhPorMph; v.SpeedFromCab = false; }
            if (_mph && v.LimitFromCab) { v.LimitKmh *= KmhPorMph; v.LimitFromCab = false; }
            _v = v;
            if (v.Connected)
            {
                if (v.Has("panto") || v.Has("breaker")) _seenElectric = true;
                else if (v.Has("rpm")) _seenDiesel = true;
                if (v.Has("doorl") || v.Has("doorr") || v.Has("doorhud")) _seenDoors = true;
            }
            if (v.Connected) _night = IsNight(v.Time);
            ApplyCabScale(v.SpeedoMax);
            double sp = v.Has("speed") ? Math.Max(0, Math.Min(_speedMax, Math.Abs(v.SpeedKmh))) : 0;
            if (!_animInit)
            {
                _dMr = v.MrBar; _dBp = v.BpBar; _dBc = v.BcBar;
                _dSpeed = _spd.Value = sp;
                _animInit = v.Connected;
            }
            // Lectura nueva de velocidad (y de «Speed target»): tendencia suavizada entre lecturas.
            double tms = v.StampTicks > 0
                ? v.StampTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency - _clockBaseMs
                : _clock.Elapsed.TotalMilliseconds;
            if (v.Has("speed")) _spd.Sample(sp, tms); else _spd.Reset();
            if (HayCruise(v)) _cru.Sample(Math.Min(_speedMax, v.SpeedTarget), tms); else _cru.Reset();
            // Límite: la primera vez aparece en su sitio; después, al cambiar, el triángulo se
            // desliza por el aro desde donde estaba hasta el nuevo valor.
            bool hayLimite = v.Has("limit") && v.LimitKmh > 0;
            double lim = hayLimite ? Math.Min(_speedMax, TrainCap(v.LimitKmh)) : 0;
            _limVal = lim;
            if (hayLimite && !_limShown) _limShown = true;
            else if (!hayLimite && v.Connected) _limShown = false;
            UpdateTarget(v, hayLimite, lim);
            RevisarPrevistos();
            Render();
        }

        // Próximo límite MÁS BAJO por delante. Su distancia real está entre su fila del gráfico y la
        // anterior; se estima avanzando con la velocidad del tren y se corrige cada vez que el límite
        // pasa a la fila siguiente (en ese instante está justo en esa fila).
        void UpdateTarget(CabValues v, bool hayLimite, double lim)
        {
            if (!hayLimite || v.LimitsAhead == null || v.RowStepM <= 0) { _tgtLimit = -1; return; }
            double step = v.RowStepM;
            (double d, double l)? primero = null;
            foreach (var (d, l0) in v.LimitsAhead) { double l = TrainCap(l0); if (l < lim - 0.5) { primero = (d, l); break; } }
            if (primero == null) { _tgtLimit = -1; return; }
            var (fd, fl) = primero.Value;
            if (_tgtLimit < 0 || Math.Abs(fl - _tgtLimit) > 0.5 || fd > _tgtRow + step * 0.5)
            {
                _tgtLimit = fl; _tgtRow = fd; _tgtDist = Math.Max(0, fd - step * 0.5);   // nuevo: a media fila
            }
            else if (fd < _tgtRow - step * 0.5)
            {
                _tgtRow = fd; _tgtDist = fd;                                              // cambió de fila: está ahí
            }
            else _tgtDist = Math.Max(fd - step, Math.Min(fd, _tgtDist));                  // misma fila: acotada
        }

        // Un fotograma: acerca lo mostrado a lo leído y redibuja solo si algo ha cambiado.
        // Nombre del tren para el archivo de diagnóstico.
        public string TrainName { set { try { _poller.DiagTrain = value ?? ""; } catch { } } }

        void Animate()
        {
            if (!Visible) return;
            // Cada ~50 ms: ¿bocinas o arena pulsadas en el teclado del simulador?
            if (++_kbTick % 3 == 0)
            {
                bool h = OrControl.IsDown(CmdHorn), b = OrControl.IsDown(CmdBell), sa = OrControl.IsDown(CmdSand);
                if (h != _kbHorn || b != _kbBell || sa != _kbSand) { _kbHorn = h; _kbBell = b; _kbSand = sa; Render(); }
                if (RevisarPrevistos()) Render();   // un clic que el simulador no confirmó: vuelve a lo real
            }
            double now = _clock.Elapsed.TotalMilliseconds;
            double dt = Math.Max(1, Math.Min(100, now - _lastFrameMs));
            _lastFrameMs = now;
            // 8 % por fotograma de 17 ms (Cockpit), ajustado al tiempo real entre fotogramas.
            double k = 1 - Math.Pow(1 - 0.08, dt / 17.0);
            bool changed = false;
            double Step(double cur, double target)
            {
                double diff = target - cur;
                if (Math.Abs(diff) > 0.002) { changed = true; return cur + diff * k; }
                if (cur != target) changed = true;
                return target;
            }
            _dMr = Step(_dMr, _v.MrBar);
            _dBp = Step(_dBp, _v.BpBar);
            _dBc = Step(_dBc, _v.BcBar);
            // Objetivo: la última lectura MÁS lo que habrá cambiado desde entonces según la tendencia
            // (como mucho 0,4 s hacia delante). La aguja lo persigue con suavidad (constante ~70 ms).
            // La aguja va como un muelle amortiguado (críticamente): sin cambios bruscos de ritmo
            // cuando llega una lectura nueva.
            _spd.Value = _dSpeed;
            if (_spd.Step(now, dt, _speedMax)) { _dSpeed = _spd.Value; changed = true; }
            if (_limShown)
            {
                double wanted = _limVal;
                double tgt = double.NaN;
                if (_tgtLimit >= 0)
                {
                    _tgtDist = Math.Max(0, _tgtDist - Math.Max(0, _dSpeed) / 3.6 * dt / 1000.0);   // lo recorrido
                    double vt = _tgtLimit / 3.6, dEff = Math.Max(0, _tgtDist - 10);                   // 10 m de margen
                    double vc = Math.Sqrt(vt * vt + 2 * _brakeDecel * dEff) * 3.6;
                    if (vc < wanted - 0.2) { wanted = Math.Max(_tgtLimit, vc); tgt = _tgtLimit; }
                }
                // Mientras la curva manda, la señal de abajo pasa a enseñar el próximo límite.
                if (!(double.IsNaN(tgt) && double.IsNaN(_curveTarget)) && tgt != _curveTarget) { _curveTarget = tgt; changed = true; }
            }
            else if (!double.IsNaN(_curveTarget)) { _curveTarget = double.NaN; changed = true; }
            // Velocidad objetivo del regulador («Speed target»): el triángulo amarillo la sigue igual
            // que la aguja a la velocidad. Al aparecer, sale directamente en su sitio.
            if (!_cru.Have) { if (!double.IsNaN(_dCruise)) { _dCruise = double.NaN; changed = true; } }
            else if (double.IsNaN(_dCruise)) { _dCruise = _cru.Value = _cru.Last; _cru.Vel = 0; changed = true; }
            else
            {
                _cru.Value = _dCruise;
                if (_cru.Step(now, dt, _speedMax)) { _dCruise = _cru.Value; changed = true; }
            }
            if (changed) Render();
        }

        static bool HayCruise(CabValues v) => v.Has("starget") && v.SpeedTarget > 0
                                               && !v.CruiseStatus.Equals("off", StringComparison.OrdinalIgnoreCase);

        // Seguimiento suave de un valor que llega a saltos (una lectura cada ~0,2–0,5 s):
        // el objetivo es la última lectura MÁS lo que habrá cambiado desde entonces según la
        // tendencia (como mucho 0,4 s hacia delante), y el valor mostrado lo persigue como un
        // muelle amortiguado críticamente, sin cambios bruscos de ritmo al llegar otra lectura.
        sealed class Seguidor
        {
            public double Last, LastMs, Rate, Value, Vel;
            public bool Have;
            // Consigna (el «Speed target»): cambia a saltos y se para en seco. Si llega una lectura
            // igual a la anterior, la tendencia se anula para no pasarse del valor y volver.
            public bool Consigna;

            public void Sample(double x, double tms)
            {
                if (Have)
                {
                    double dts = (tms - LastMs) / 1000.0;
                    if (dts > 0.04 && dts < 1.5)
                    {
                        double r = (x - Last) / dts;                        // km/h por segundo
                        r = Math.Max(-40, Math.Min(40, r));                 // (un tren no pasa de ahí)
                        Rate += 0.35 * (r - Rate);
                    }
                    else Rate = 0;
                    if (Consigna && Math.Abs(x - Last) < 0.01) Rate = 0;
                }
                Last = x; LastMs = tms; Have = true;
                if (x < 0.05 && Math.Abs(Rate) < 0.3) Rate = 0;             // parado: sin deriva
            }

            public void Reset() { Have = false; Rate = 0; }

            // Avanza dtMs; devuelve si el valor ha cambiado.
            public bool Step(double nowMs, double dtMs, double max)
            {
                if (!Have) return false;
                double v0 = Value, v = Value;
                // (la consigna anticipa menos: así apenas se pasa cuando se para en seco)
                double adelante = Math.Min(Consigna ? 0.12 : 0.4, Math.Max(0, (nowMs - LastMs) / 1000.0));
                double objetivo = Math.Max(0, Math.Min(max, Last + Rate * adelante));
                const double w = 11.0;                           // rad/s (≈ 90 ms)
                double resto = dtMs / 1000.0;
                while (resto > 0)
                {
                    double h = Math.Min(0.008, resto); resto -= h;
                    double acc = w * w * (objetivo - v) - 2 * w * Vel;
                    Vel += acc * h;
                    v += Vel * h;
                }
                v = Math.Max(0, Math.Min(max, v));
                if (Math.Abs(objetivo - v) < 0.005 && Math.Abs(Vel) < 0.01) { v = objetivo; Vel = 0; }
                Value = v;
                return v != v0;
            }
        }

        public void SetPosition(double lat, double lon) { _lat = lat; _lon = lon; _geo = true; }

        bool IsNight(string time)
        {
            if (!_geo || string.IsNullOrEmpty(time)) return _night;
            var m = Regex.Match(time, @"(\d{1,2}):(\d{2})(?::(\d{2}))?");
            if (!m.Success) return _night;
            double h = int.Parse(m.Groups[1].Value) + int.Parse(m.Groups[2].Value) / 60.0
                       + (m.Groups[3].Success ? int.Parse(m.Groups[3].Value) / 3600.0 : 0);
            return SunElevation(_lat, _lon, _dayOfYear, h) <= -4.9;
        }

        // Altura del sol en grados (fórmulas de la NOAA). La hora del simulador se toma como hora
        // oficial del huso que corresponde a la longitud.
        public static double SunElevation(double lat, double lon, int dayOfYear, double clockHours)
        {
            double gm = 2 * Math.PI / 365 * (dayOfYear - 1 + (clockHours - 12) / 24);
            double eqt = 229.18 * (0.000075 + 0.001868 * Math.Cos(gm) - 0.032077 * Math.Sin(gm)
                                   - 0.014615 * Math.Cos(2 * gm) - 0.040849 * Math.Sin(2 * gm));
            double decl = 0.006918 - 0.399912 * Math.Cos(gm) + 0.070257 * Math.Sin(gm) - 0.006758 * Math.Cos(2 * gm)
                          + 0.000907 * Math.Sin(2 * gm) - 0.002697 * Math.Cos(3 * gm) + 0.00148 * Math.Sin(3 * gm);
            double solarMin = clockHours * 60 + eqt + 4 * lon - 60 * Math.Round(lon / 15);
            double ha = (solarMin / 4 - 180) * Math.PI / 180;
            double la = lat * Math.PI / 180;
            double cz = Math.Sin(la) * Math.Sin(decl) + Math.Cos(la) * Math.Cos(decl) * Math.Cos(ha);
            return 90 - Math.Acos(Math.Max(-1, Math.Min(1, cz))) * 180 / Math.PI;
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Render();
            _poller.Start();
            _anim.Start();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && IsHandleCreated) Render();
        }

        public void CloseHud()
        {
            try { OrControl.ReleaseAll(); } catch { }
            try { _poller.Dispose(); } catch { }
            try { _anim.Stop(); _anim.Dispose(); } catch { }
            try { _cache?.Dispose(); _cache = null; } catch { }
            SavePrefs();
            try { Close(); } catch { }
        }

        void SavePrefs()
        {
            if (_prefs == null) return;
            _prefs.CabPanelX = Left; _prefs.CabPanelY = Top; _prefs.CabHudScale = _scale;
            try { _prefs.Save(); } catch { }
        }

        Size TargetSize() => new Size((int)Math.Round(BaseW * _scale), (int)Math.Round(BaseH * _scale));

        Point InitialLocation()
        {
            var sz = TargetSize();
            if (_prefs != null && _prefs.CabPanelX >= 0 && _prefs.CabPanelY >= 0)
            {
                var p = new Point(_prefs.CabPanelX, _prefs.CabPanelY);
                foreach (var s in Screen.AllScreens)
                    if (s.WorkingArea.IntersectsWith(new Rectangle(p, sz))) return p;
            }
            var wa = Screen.PrimaryScreen.WorkingArea;
            return new Point(wa.Right - sz.Width - 16, wa.Bottom - sz.Height - 12);   // abajo a la derecha
        }

        // ============================ ratón ============================
        PointF ToDesign(Point p) => new PointF(p.X / _scale, p.Y / _scale);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int ic = IconAt(ToDesign(e.Location));
            if (ic >= 0 && (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right))
            {
                _iconDown = ic;
                IconPressed(ic, e.Button == MouseButtons.Right);
                Render();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            _down = true; _downScreen = Cursor.Position; _formAtDown = Location;
            _resizing = _hitGrip.Contains(ToDesign(e.Location));
            _scaleAtDown = _scale; _sizeAtDown = Size;
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_hover) { _hover = true; Render(); }
            if (_down)
            {
                var cur = Cursor.Position;
                int dx = cur.X - _downScreen.X, dy = cur.Y - _downScreen.Y;
                if (_resizing)
                {
                    float fx = (_sizeAtDown.Width + dx) / (float)_sizeAtDown.Width;
                    float fy = (_sizeAtDown.Height + dy) / (float)_sizeAtDown.Height;
                    float ns = Math.Max(ScaleMin, Math.Min(ScaleMax, _scaleAtDown * (Math.Abs(dx) > Math.Abs(dy) ? fx : fy)));
                    if (Math.Abs(ns - _scale) > 0.005f) { _scale = ns; Size = TargetSize(); Render(); }
                }
                else if (_dragging || Math.Abs(dx) > 3 || Math.Abs(dy) > 3)
                {
                    _dragging = true;
                    Location = new Point(_formAtDown.X + dx, _formAtDown.Y + dy);
                }
            }
            else if (_iconDown < 0)
            {
                var d = ToDesign(e.Location);
                int ic = IconAt(d);
                if (ic != _iconHover) { _iconHover = ic; Render(); }
                Cursor = ic >= 0 ? Cursors.Hand : _hitGrip.Contains(d) ? Cursors.SizeNWSE : Cursors.SizeAll;
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_iconDown >= 0)
            {
                ReleaseHeld();
                _iconDown = -1;
                Render();
                base.OnMouseUp(e);
                return;
            }
            bool cambio = _dragging || _resizing;
            _down = _dragging = _resizing = false;
            if (cambio) SavePrefs();
            if (!Bounds.Contains(Cursor.Position)) _hover = false;   // soltado fuera: deja de captar el ratón
            Render();
            base.OnMouseUp(e);
        }

        // Rueda del ratón encima del pupitre: lo agranda o lo encoge sin mover su esquina inferior
        // derecha (donde suele estar).
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            float ns = Math.Max(ScaleMin, Math.Min(ScaleMax, _scale + (e.Delta > 0 ? 0.05f : -0.05f)));
            if (Math.Abs(ns - _scale) > 0.001f)
            {
                var br = new Point(Right, Bottom);
                _scale = ns;
                var sz = TargetSize();
                SetBounds(br.X - sz.Width, br.Y - sz.Height, sz.Width, sz.Height);
                Render();
                SavePrefs();
            }
            base.OnMouseWheel(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (!_down && _iconDown < 0) { _hover = false; _iconHover = -1; Render(); }
            base.OnMouseLeave(e);
        }

        // ============================ pictogramas: botones ============================
        enum Ico { DoorL, Panto, Breaker, Lights, Sand, Wipers, HornHigh, HornLow, DoorR }

        bool ShowElectric => _seenElectric || (!_seenDiesel && _traction != "diesel" && _traction != "steam");

        // Pictogramas en el orden del pupitre; sin pantógrafo ni disyuntor en diésel y vapor.
        bool ShowDoors => _seenDoors || _passenger != false;

        List<Ico> Icons()
        {
            var l = new List<Ico>();
            if (ShowDoors) l.Add(Ico.DoorL);
            if (ShowElectric) { l.Add(Ico.Panto); l.Add(Ico.Breaker); }
            l.AddRange(new[] { Ico.Lights, Ico.Sand, Ico.Wipers, Ico.HornHigh, Ico.HornLow });
            if (ShowDoors) l.Add(Ico.DoorR);
            return l;
        }

        // Posición del pictograma i de n (diseño): repartidos de borde a borde de las esferas.
        static RectangleF IconRect(int i, int n)
        {
            float izq = AirX - AirR, der = SpeedX + SpeedR;
            float paso = n > 1 ? (der - TileSize - izq) / (n - 1) : 0;
            return new RectangleF(izq + i * paso, TilesY, TileSize, TileSize);
        }

        int IconAt(PointF d)
        {
            int n = Icons().Count;
            for (int i = 0; i < n; i++)
            {
                var r = IconRect(i, n);
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, dx = d.X - cx, dy = d.Y - cy;
                if (dx * dx + dy * dy <= r.Width * r.Width / 4f) return i;
            }
            return -1;
        }

        // Órdenes de OR por NOMBRE (ver OrControl): nameof comprueba al compilar que existen.
        static readonly string[] Pantos =
        {
            nameof(UserCommand.ControlPantograph1), nameof(UserCommand.ControlPantograph2),
            nameof(UserCommand.ControlPantograph3), nameof(UserCommand.ControlPantograph4),
        };
        const string CmdDoorL = nameof(UserCommand.ControlDoorLeft), CmdDoorR = nameof(UserCommand.ControlDoorRight),
                     CmdBrkClose = nameof(UserCommand.ControlCircuitBreakerClosingOrder),
                     CmdBrkOpen = nameof(UserCommand.ControlCircuitBreakerOpeningOrder),
                     CmdLightUp = nameof(UserCommand.ControlHeadlightIncrease), CmdLightDown = nameof(UserCommand.ControlHeadlightDecrease),
                     CmdSand = nameof(UserCommand.ControlSander), CmdWiper = nameof(UserCommand.ControlWiper),
                     CmdBell = nameof(UserCommand.ControlBell), CmdHorn = nameof(UserCommand.ControlHorn);

        // Qué hace cada pictograma (texto del rótulo y la orden principal, para enseñar su tecla).
        (string what, string cmd) IconInfo(Ico ic) => ic switch
        {
            Ico.DoorL => (I18n.T("Puertas izquierdas"), CmdDoorL),
            Ico.Panto => (_v.Panto ? I18n.T("Bajar pantógrafo") : I18n.T("Subir pantógrafo"), Pantos[0]),
            Ico.Breaker => (_v.Breaker ? I18n.T("Abrir disyuntor") : I18n.T("Cerrar disyuntor"), _v.Breaker ? CmdBrkOpen : CmdBrkClose),
            Ico.Lights => (I18n.T("Faros · clic derecho: bajar"), CmdLightUp),
            Ico.Sand => (I18n.T("Arena (mantener)"), CmdSand),
            Ico.Wipers => (I18n.T("Limpiaparabrisas"), CmdWiper),
            Ico.HornHigh => (I18n.T("Bocina aguda (mantener)"), CmdBell),
            Ico.HornLow => (I18n.T("Bocina grave (mantener)"), CmdHorn),
            _ => (I18n.T("Puertas derechas"), CmdDoorR),
        };

        // Estado real de un icono de dos o tres posiciones (1/0; faros: 0, 1 o 2).
        int Real(Ico ic) => ic switch
        {
            Ico.DoorL => _v.DoorLeft ? 1 : 0,
            Ico.DoorR => _v.DoorRight ? 1 : 0,
            Ico.Panto => _v.Panto ? 1 : 0,
            Ico.Breaker => _v.Breaker ? 1 : 0,
            Ico.Lights => Math.Max(0, Math.Min(2, _v.HeadlightLevel)),
            Ico.Wipers => _v.Wipers ? 1 : 0,
            _ => 0,
        };

        // Lo que se enseña: lo previsto tras un clic mientras no caduque; si no, lo real.
        int Shown(Ico ic) =>
            _previsto.TryGetValue(ic, out var p) && Environment.TickCount64 < p.hasta ? p.valor : Real(ic);

        void Prever(Ico ic, int valor, int ms)
        {
            _previsto[ic] = (valor, Environment.TickCount64 + ms);
        }

        // Quita lo previsto cuando el simulador ya lo confirma o cuando caduca. true si cambió algo.
        bool RevisarPrevistos()
        {
            if (_previsto.Count == 0) return false;
            bool cambio = false;
            long now = Environment.TickCount64;
            foreach (var ic in new List<Ico>(_previsto.Keys))
            {
                var p = _previsto[ic];
                if (now >= p.hasta || Real(ic) == p.valor) { _previsto.Remove(ic); cambio |= now >= p.hasta && Real(ic) != p.valor; }
            }
            return cambio;
        }

        string IconImage(Ico ic) => ic switch
        {
            Ico.DoorL => Shown(ic) == 1 ? "door_open_l" : "door_close",
            Ico.Panto => Shown(ic) == 1 ? "panto_up" : "panto_down",
            Ico.Breaker => Shown(ic) == 1 ? "breaker_closed" : "breaker_open",          // abierto amarillo, cerrado claro
            Ico.Lights => Shown(ic) >= 2 ? "light_high" : Shown(ic) == 1 ? "light_on" : "light_off",
            Ico.Sand => _v.Sand || _kbSand || _held == CmdSand ? "sand_on" : "sand_off",
            Ico.Wipers => Shown(ic) == 1 ? "wipers_on" : "wipers_off",
            Ico.HornHigh => _v.Bell || _kbBell || _held == CmdBell ? "horn_high_on" : "horn_high_off",           // bocina aguda = mando «Bell» de OR
            Ico.HornLow => _v.Horn || _kbHorn || _held == CmdHorn ? "horn_low_on" : "horn_low_off",              // bocina grave = mando «Horn» de OR
            _ => Shown(ic) == 1 ? "door_open_r" : "door_close",
        };

        void IconPressed(int i, bool right)
        {
            var l = Icons(); if (i < 0 || i >= l.Count) return;
            var ic = l[i];
            // Clics muy seguidos: se ignoran (cada orden necesita su tiempo en el simulador).
            long ahora = Environment.TickCount64;
            if (ahora - _lastCmdAt < 350) return;
            _lastCmdAt = ahora;
            int antes = Shown(ic);   // estado de partida (antes de prever el nuevo)
            // Cambio inmediato del icono (el pantógrafo tarda unos segundos en subir: más margen).
            switch (ic)
            {
                case Ico.DoorL: case Ico.DoorR: case Ico.Wipers: case Ico.Breaker:
                    Prever(ic, 1 - Shown(ic), 3000); break;
                case Ico.Panto:
                    Prever(ic, 1 - Shown(ic), 8000); break;
                case Ico.Lights:
                    int f = Shown(ic);
                    Prever(ic, right ? Math.Max(0, f - 1) : (f >= 2 ? 0 : f + 1), 3000); break;
            }
            switch (ic)
            {
                case Ico.DoorL: _ = OrControl.Press(CmdDoorL); break;
                case Ico.Panto: _ = PantoClick(); break;
                case Ico.Breaker: Hold(_v.Breaker ? CmdBrkOpen : CmdBrkClose); break;
                case Ico.Lights: _ = LightsClick(right, antes); break;
                case Ico.Sand: Hold(CmdSand); break;
                case Ico.Wipers: _ = OrControl.Press(CmdWiper); break;
                case Ico.HornHigh: Hold(CmdBell); break;
                case Ico.HornLow: Hold(CmdHorn); break;
                case Ico.DoorR: _ = OrControl.Press(CmdDoorR); break;
            }
        }

        // Órdenes de mantener: pulsada mientras dure el clic (al menos 120 ms, para que OR la lea).
        void Hold(string c)
        {
            ReleaseHeld();
            if (OrControl.Down(c)) { _held = c; _heldAtMs = Environment.TickCount64; }
        }

        async void ReleaseHeld()
        {
            if (_held is not string c) return;
            _held = null;
            long falta = 120 - (Environment.TickCount64 - _heldAtMs);
            if (falta > 0) await Task.Delay((int)falta);
            OrControl.Up(c);
        }

        // Pantógrafo: si hay alguno subido, se bajan los que estén subidos (cada uno con su tecla);
        // si están todos bajados, se sube el delantero (el 1).
        async Task PantoClick()
        {
            if (_v.Panto)
            {
                bool alguno = false;
                var cmd = _v.PantoCmd;
                if (cmd != null)
                    for (int k = 0; k < Math.Min(cmd.Length, Pantos.Length); k++)
                        if (cmd[k]) { if (alguno) await Task.Delay(150); await OrControl.Press(Pantos[k]); alguno = true; }
                if (!alguno) await OrControl.Press(Pantos[0]);
            }
            else await OrControl.Press(Pantos[0]);
        }

        // Faros: clic = subir un paso (apagados → cortos → largos → apagados); clic derecho = bajar.
        // «nivel» = el que se veía al hacer clic. De largas a apagadas son dos pulsaciones: van
        // separadas lo mismo que las haría una persona (antes, 150 ms: demasiado seguidas).
        async Task LightsClick(bool right, int nivel)
        {
            if (right) { if (nivel > 0) await OrControl.Press(CmdLightDown); return; }
            if (nivel >= 2)
            {
                await OrControl.Press(CmdLightDown);
                await Task.Delay(600);
                await OrControl.Press(CmdLightDown);
            }
            else await OrControl.Press(CmdLightUp);
        }

        // ============================ pintado ============================
        CabDraw.Needle[] AirNeedles() => new[]
        {
            new CabDraw.Needle(I18n.T("TDP"), CabDraw.Red, _dMr * _airF, _v.Has("mr")),
            new CabDraw.Needle(I18n.T("TFA"), CabDraw.Amber, _dBp * _airF, _v.Has("bp")),
        };

        // Imagen de cada pictograma según la posición actual de su mando.
        List<string> Pictograms() => Icons().ConvertAll(IconImage);

        // Caché de lo fijo (esferas, escalas, rótulos y pictogramas): se rehace solo si cambia algo
        // de eso (tamaño, noche, datos disponibles o posición de algún mando).
        Bitmap _cache; string _cacheKey;

        void Draw(Graphics g)
        {
            var ps = Pictograms();
            string key = $"{Width}x{Height}|{_scale}|{_speedMax}|{_night}|{_v.Has("speed")}{_v.Has("mr")}{_v.Has("bp")}{_v.Has("bc")}|{string.Join(",", ps)}";
            if (_cache == null || _cache.Width != Width || _cache.Height != Height || key != _cacheKey)
            {
                _cache?.Dispose();
                _cache = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), PixelFormat.Format32bppArgb);
                using (var gc = Graphics.FromImage(_cache))
                {
                    gc.Clear(Color.Transparent);
                    DrawLayer(gc, 1, ps);
                }
                _cacheKey = key;
            }
            g.DrawImageUnscaled(_cache, 0, 0);
            DrawLayer(g, 2, ps);
        }

        void DrawLayer(Graphics g, int layer, List<string> ps)
        {
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.ScaleTransform(_scale, _scale);
            CabDraw.Lit = _night;
            CabDraw.Layer = layer;
            try
            {
                // Manómetros en la unidad y con la escala de la cabina del tren (bar, psi, kPa…).
                CabDraw.Manometer(g, AirX, Cy, AirR, "", _airUnit, 0, _airMax, AirNeedles(), true, LabelPx);
                CabDraw.Manometer(g, BcX, Cy, BcR, I18n.T("CIL. FRENO"), _bcUnit, 0, _bcMax,
                    new[] { new CabDraw.Needle("CF", CabDraw.White, _dBc * _bcF, _v.Has("bc")) }, false, LabelPx);
                // Se dibuja en la unidad del velocímetro del tren (km/h o mph).
                CabDraw.Speedometer(g, RSpeed, _dSpeed * _uf, TrainCap(_v.LimitKmh) * _uf, _v.Has("limit"), _speedMax * _uf, _v.Has("speed"),
                    _curveTarget * _uf, _dCruise * _uf, _mph ? "mph" : "km/h");

                if (layer == 1)
                {
                    // Fila de pictogramas a TODO el ancho de las esferas: el primero bajo el borde
                    // izquierdo del manómetro TDP/TFA y el último bajo el borde derecho del
                    // velocímetro, repartidos a partes iguales entre medias.
                    for (int i = 0; i < ps.Count; i++)
                        CabDraw.Pictogram(g, IconRect(i, ps.Count), ps[i]);
                }
                else
                {
                    // Tirador de escala en la esquina (bajo la fila de iconos): siempre visible,
                    // discreto, y con fondo propio para que Windows le entregue el ratón (lo
                    // transparente lo atraviesa).
                    // Pulsado: aro claro y más grueso (sin oscurecer el icono, que ya enseña el
                    // estado nuevo: encendido mientras se mantiene arena o bocina).
                    if (_iconDown >= 0 && _iconDown < ps.Count)
                        using (var p = new Pen(Color.FromArgb(235, 255, 255, 255), 3.2f))
                            g.DrawEllipse(p, RectangleF.Inflate(IconRect(_iconDown, ps.Count), 2, 2));
                    int hi = _iconDown >= 0 ? _iconDown : _iconHover;
                    if (hi >= 0 && hi < ps.Count)
                    {
                        using (var p = new Pen(Color.FromArgb(200, 255, 255, 255), 2f))
                            g.DrawEllipse(p, RectangleF.Inflate(IconRect(hi, ps.Count), 1, 1));
                        var (what, cmd) = IconInfo(Icons()[hi]);
                        string tecla = OrControl.KeyName(cmd);
                        string txt = string.IsNullOrEmpty(tecla) ? what : what + "  ·  " + tecla;
                        using var f = new Font(Theme.FontFamily, 12f, FontStyle.Bold, GraphicsUnit.Pixel);
                        var sz = g.MeasureString(txt, f);
                        var r = IconRect(hi, ps.Count);
                        float x = Math.Max(2, Math.Min(BaseW - 32 - sz.Width, r.X + r.Width / 2 - sz.Width / 2));
                        CabDraw.Shadowed(g, txt, f, Color.FromArgb(235, 240, 248), x, TilesY + TileSize + 5);
                    }

                    _hitGrip = new RectangleF(BaseW - 26, BaseH - 26, 24, 24);
                    using (var path = CabDraw.Round(_hitGrip, 5))
                    using (var b = new SolidBrush(Color.FromArgb(_hover ? 170 : 90, 20, 25, 34)))
                        g.FillPath(b, path);
                    using (var pen = new Pen(Color.FromArgb(_hover ? 230 : 150, CabDraw.Dim), 1.6f))
                        for (int i = 0; i < 3; i++)
                        {
                            float o = 6 + i * 5;
                            g.DrawLine(pen, _hitGrip.Right - o, _hitGrip.Bottom - 4, _hitGrip.Right - 4, _hitGrip.Bottom - o);
                        }
                }
            }
            finally
            {
                CabDraw.Lit = false;
                CabDraw.Layer = 0;
                g.Restore(st);
            }
        }

        // Dibuja en un mapa de bits con canal alfa y lo entrega a Windows tal cual: lo transparente
        // queda transparente de verdad (sin marco ni color de fondo).
        void Render()
        {
            if (IsDisposed || !IsHandleCreated || Width <= 0 || Height <= 0) return;
            using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                // Con el ratón encima (o arrastrando), un fondo casi invisible (alfa 1) hace que toda la
                // ventana reciba el ratón y no se pierda al pasar por los huecos entre esferas.
                g.Clear(_hover || _down ? Color.FromArgb(1, 0, 0, 0) : Color.Transparent);
                Draw(g);
            }
            IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(memDc, hBmp);
            try
            {
                var size = new SIZE { cx = Width, cy = Height };
                var src = new POINT { x = 0, y = 0 };
                var dst = new POINT { x = Left, y = Top };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = 0 /*AC_SRC_OVER*/, BlendFlags = 0,
                    SourceConstantAlpha = (byte)(_hover ? 255 : 232),   // un poco translúcido, opaco al pasar el ratón
                    AlphaFormat = 1 /*AC_SRC_ALPHA*/
                };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
            }
            finally
            {
                SelectObject(memDc, old);
                DeleteObject(hBmp);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // ---------------- Win32 ----------------
        const int WS_EX_LAYERED = 0x00080000;
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc,
                                               ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
    }
}
