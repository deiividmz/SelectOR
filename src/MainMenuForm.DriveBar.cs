// Barra superior de conducción (DriveTopBar): HUD, mapa grande y servicio de empresa
// SIN salir del simulador. Permite ponerse de servicio a mitad de viaje con el tren que se
// está conduciendo (si es de la flota de la empresa) y registrar el servicio en curso.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using ORTS.Menu;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        DriveTopBar _driveBar;

        // Los HUD (minimapa y pupitre) se crean al lanzar el simulador pero no se enseñan hasta que
        // el escenario está cargado: la misma señal que arranca el cronómetro del modo Empresa, la
        // primera posición válida que da Open Rails. Así no se quedan flotando sobre la pantalla de
        // carga con todo vacío. Si se piden a mano desde la barra superior, salen en el acto.
        bool _scenarioReady, _hudWaiting, _cabWaiting;

        void OnScenarioReady()
        {
            if (_hudWaiting) { _hudWaiting = false; if (HudAlive && !_serviceHud.Visible) _serviceHud.Show(); }
            if (_cabWaiting) { _cabWaiting = false; if (CabHudAlive && !_cabHud.Visible) _cabHud.Show(); }
            if (_chatWaiting) { _chatWaiting = false; if (ChatHudAlive && !_chatHud.Visible) _chatHud.Show(); }
            if (_barWaiting) { _barWaiting = false; CreateDriveBar(); }
            RoadOnScenarioReady();   // hoja de ruta de Horarios / Actividad
        }

        // La barra superior tampoco existe hasta que el escenario está cargado (como el HUD y el pupitre).
        bool _barWaiting;

        // Tren que se está conduciendo (capturado al lanzar: la selección del menú puede cambiar luego).
        TrainItem _drivenConsist;
        string _drivenLabel = "", _drivenPath = "", _drivenRoute = "";

        // Comprobación en caché de si el tren conducido es de la flota (para habilitar el botón).
        string _fleetCheckCompany; DateTime _fleetCheckUtc; string _fleetCheckReason; bool _fleetCheckOk, _fleetChecking;

        void CaptureDrivenTrain()
        {
            _drivenConsist = null;
            try
            {
                _drivenConsist = CurrentDrivenConsist();   // en Actividad, el tren de la actividad
            }
            catch { }
            _drivenLabel = _drivenConsist?.Name ?? CurrentConsistLabel();
            _drivenPath = CurrentPathLabel();
            _drivenRoute = _curRoute?.Name ?? "";
            _fleetCheckCompany = null;
        }

        static TrainItem ActivityConsist(Activity a)
        {
            try { return FromOrConsist(a?.Consist); } catch { return null; }
        }

        // Máquinas de la empresa que habilitan este tren: la de cabeza y su formación fija.
        List<(string name, string folder)> EngineNamesOf(TrainItem c) => FleetAccessNames(c);

        void ShowDriveBar()
        {
            CloseDriveBar();
            if (!_scenarioReady) { _barWaiting = true; return; }   // aparece con el escenario cargado
            CreateDriveBar();
        }

        void CreateDriveBar()
        {
            try
            {
                _driveBar = new DriveTopBar(DriveBarStateNow, ToggleHudFromBar, ToggleBigMapFromBar, ServiceFromBar, CheckDrivenFleet,
                                            ToggleCabHudFromBar, ToggleChatHudFromBar, ToggleRoadHudFromBar);
            }
            catch { _driveBar = null; }
        }

        void CloseDriveBar()
        {
            _barWaiting = false;
            try { _driveBar?.CloseBar(); } catch { }
            _driveBar = null;
        }

        bool HudAlive => _serviceHud != null && !_serviceHud.IsDisposed;

        void ToggleHudFromBar()
        {
            _hudWaiting = false;   // lo ha pedido a mano: manda lo que diga ahora
            if (!HudAlive) { ShowServiceHud(_pendingServiceId != null, force: true); return; }
            if (_serviceHud.Visible) _serviceHud.Hide(); else _serviceHud.Show();
        }

        void ToggleBigMapFromBar()
        {
            if (!HudAlive)
            {
                // El mapa grande usa los datos del HUD: se crea oculto si no existía.
                ShowServiceHud(_pendingServiceId != null, force: true);
                if (!HudAlive) return;
                _serviceHud.Hide();
            }
            _serviceHud.ToggleBigMap();
        }

        DriveBarState DriveBarStateNow()
        {
            var s = new DriveBarState
            {
                HudVisible = HudAlive && _serviceHud.Visible,
                BigMapOpen = HudAlive && _serviceHud.BigMapOpen,
                InService = _pendingServiceId != null,
                CabVisible = CabHudAlive && _cabHud.Visible,
                ChatVisible = ChatHudAlive && _chatHud.Visible,
                RoadVisible = RoadHudAlive && _roadHud.Visible
            };
            if (s.InService)
            {
                int secs = ServiceSeconds();
                s.Status = string.Format(Tr("En servicio · {0}"), _empOnDutyCompany?.Name ?? "") + "\n"
                         + $"{secs / 3600:00}:{secs % 3600 / 60:00}:{secs % 60:00} · " + (_trackedMeters / 1000.0).ToString("0.0", EsEs) + " km"
                         + (_svcPaused ? " · " + Tr("en pausa") : "");
                s.ServiceEnabled = true;
                s.ServiceText = Tr("Registrar servicio");
                return s;
            }
            s.Status = Tr("Conducción libre");   // sin el nombre del tren (consist)
            var co = _empOnDutyCompany ?? _empSel;
            if (!Supa.IsLoggedIn) { s.ServiceText = Tr("Inicia sesión en Empresas para ponerte de servicio"); return s; }
            if (co == null) { s.ServiceText = Tr("Elige una empresa en SelectOR"); return s; }
            if (_drivenConsist == null) { s.ServiceText = Tr("No se puede identificar el tren"); return s; }
            if (_fleetChecking || _fleetCheckCompany != co.Id) { s.ServiceText = Tr("Comprobando la flota…"); return s; }
            if (!_fleetCheckOk) { s.ServiceText = _fleetCheckReason ?? Tr("El tren no está en la flota"); return s; }
            s.ServiceEnabled = true;
            s.ServiceText = string.Format(Tr("Ponerme de servicio · {0}"), co.Name);
            return s;
        }

        // Al desplegar la barra: ¿el tren que conduzco es una unidad DISPONIBLE de la empresa? (caché 20 s)
        async void CheckDrivenFleet()
        {
            if (_pendingServiceId != null || !Supa.IsLoggedIn || _fleetChecking) return;
            var co = _empOnDutyCompany ?? _empSel;
            if (co == null || _drivenConsist == null) return;
            if (_fleetCheckCompany == co.Id && (DateTime.UtcNow - _fleetCheckUtc).TotalSeconds < 20) return;
            _fleetChecking = true;
            try
            {
                var names = EngineNamesOf(_drivenConsist);
                if (names.Count == 0) { _fleetCheckOk = false; _fleetCheckReason = Tr("El tren no tiene una máquina reconocible"); }
                else
                {
                    var prev = _empOnDutyCompany; _empOnDutyCompany = co;   // el mensaje usa el nombre de la empresa
                    var (vid, reason) = await ResolveCompanyUnitReason(co.Id, names);
                    _empOnDutyCompany = prev;
                    _fleetCheckOk = vid != null;
                    _fleetCheckReason = vid != null ? null : ShortFleetReason(reason, co.Name);
                }
                _fleetCheckCompany = co.Id; _fleetCheckUtc = DateTime.UtcNow;
            }
            catch { _fleetCheckOk = false; _fleetCheckReason = null; }
            finally { _fleetChecking = false; }
        }

        // Motivo corto para el botón (los mensajes completos son largos para la barra).
        string ShortFleetReason(string reason, string company)
        {
            switch (_lastUnitReasonCode)
            {
                case "notfleet": return string.Format(Tr("El tren no está en la flota de {0}"), company);
                case "inuse": return Tr("Tren no operativo · en servicio");
                case "maint": return Tr("Tren no operativo · en mantenimiento");
                case "unavail": return Tr("Tren no operativo");
                default: return string.IsNullOrEmpty(reason) ? Tr("El tren no está en la flota") : reason;
            }
        }

        async Task<(bool ok, string msg)> ServiceFromBar()
        {
            return _pendingServiceId != null ? await RegisterServiceMidRun() : await StartServiceMidRun();
        }

        // Ponerse de servicio con OR ya en marcha: abre el servicio con la unidad del tren conducido.
        async Task<(bool ok, string msg)> StartServiceMidRun()
        {
            if (!Supa.IsLoggedIn) return (false, Tr("Inicia sesión en Empresas para ponerte de servicio"));
            var co = _empOnDutyCompany ?? _empSel;
            if (co == null) return (false, Tr("Elige una empresa en SelectOR"));
            var names = EngineNamesOf(_drivenConsist);
            if (names.Count == 0) return (false, Tr("No se puede identificar el tren"));

            var prev = _empOnDutyCompany; _empOnDutyCompany = co;
            var (vid, reason) = await ResolveCompanyUnitReason(co.Id, names);
            if (vid == null) { _empOnDutyCompany = prev; _fleetCheckCompany = null; return (false, ShortFleetReason(reason, co.Name)); }
            string plate = _lastUnitPlate;

            var (json, err) = await StartServiceRpc(co.Id, _drivenRoute, _drivenLabel, _drivenPath, vid, _drivenConsist);
            if (err != null || string.IsNullOrWhiteSpace(json)) { _empOnDutyCompany = prev; return (false, Tr("No se pudo abrir el servicio: ") + err); }
            string sid;
            try
            {
                using var d = JsonDocument.Parse(json);
                sid = d.RootElement.ValueKind == JsonValueKind.String ? d.RootElement.GetString() : json.Trim().Trim('"');
            }
            catch { sid = json.Trim().Trim('"'); }

            // El servicio empieza AHORA: tiempo, km y viajeros desde este momento.
            _pendingServiceId = sid;
            PublishServiceStrip(sid, co.Id, _drivenConsist);   // composición 2D del servicio
            _svcOpenedUtc = DateTime.UtcNow;
            StartSvcClock(_tHave ? DateTime.UtcNow : (DateTime?)null);   // si aún carga, arranca con la 1.ª posición
            _estPatKm = 0; _estimatedKm = 0;
            _trackedMeters = 0; _kmRejected = 0; _kmRejectedM = 0;
            // Los viajeros ya iban contándose en conducción libre: se conservan los que van a bordo
            // y el servicio solo cobra los que suban a partir de ahora.
            if (_paxActive || _paxWanted) { _paxBoarded = 0; _paxKm = 0; }
            else StartPaxTracking(_drivenConsist);
            UpdateDutyUi();
            SaveServiceJournal(force: true);   // por si se cierra todo de golpe (MainMenuForm.Recuperar.cs)

            bool bigWasOpen = HudAlive && _serviceHud.BigMapOpen;
            bool hudWasHidden = HudAlive && !_serviceHud.Visible;
            ShowServiceHud(true, force: true);   // HUD en modo servicio (tiempo + viajeros)
            if (HudAlive)
            {
                if (hudWasHidden) _serviceHud.Hide();
                if (bigWasOpen) _serviceHud.ToggleBigMap();
            }
            return (true, string.IsNullOrEmpty(plate)
                ? string.Format(Tr("De servicio en {0}. ¡Buen viaje!"), co.Name)
                : string.Format(Tr("De servicio en {0} con la unidad {1}. ¡Buen viaje!"), co.Name, plate));
        }

        // Registrar el servicio sin cerrar OR: se sigue conduciendo en modo libre.
        async Task<(bool ok, string msg)> RegisterServiceMidRun()
        {
            if (_pendingServiceId == null) return (false, null);
            _tripDurationS = ServiceSeconds();
            _estimatedKm = Math.Round(_trackedMeters / 1000.0, 1);
            var (ok, summary) = await EmpCloseServiceCore(showDialog: false);
            if (!ok) return (false, summary ?? Tr("No se pudo registrar el servicio."));

            // Vuelta a conducción libre: los viajeros siguen (a bordo se conservan; «subidos» vuelve a 0).
            _paxBoarded = 0; _paxKm = 0;
            _fleetCheckCompany = null;
            bool bigWasOpen = HudAlive && _serviceHud.BigMapOpen;
            bool hudWasHidden = HudAlive && !_serviceHud.Visible;
            ShowServiceHud(false, force: true);
            if (HudAlive)
            {
                if (hudWasHidden) _serviceHud.Hide();
                if (bigWasOpen) _serviceHud.ToggleBigMap();
            }
            return (true, summary);
        }
    }
}
