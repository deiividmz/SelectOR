// Servicio interrumpido: si Open Rails y SelectOR se cierran de golpe (cuelgue, corte de luz…), el viaje
// no se pierde.
//  · Mientras se conduce un servicio, SelectOR guarda cada pocos segundos en disco lo recogido hasta ese
//    momento: km, tiempo de marcha, viajeros, paradas e infracciones detectadas.
//  · Al volver a abrir SelectOR (con la misma cuenta), si ese servicio sigue abierto en el servidor, se
//    registra con esos datos por el mismo camino que un cierre normal: con los mínimos de siempre (3 km y
//    5 minutos) y el carné por puntos. Si Open Rails sigue abierto (solo se cayó SelectOR), espera a que se
//    cierre.
//  · Antes de ponerse de servicio otra vez también se recupera: al abrir un servicio nuevo, el servidor
//    borra los que el maquinista hubiera dejado abiertos.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        sealed class SvcJournal
        {
            public int V { get; set; } = 1;
            public string UserId { get; set; } = "";
            public string ServiceId { get; set; } = "";
            public string CompanyId { get; set; } = "";
            public string CompanyName { get; set; } = "";
            public string Route { get; set; } = "";
            public string Consist { get; set; } = "";
            public string Path { get; set; } = "";
            public DateTime OpenedUtc { get; set; }
            public DateTime SavedUtc { get; set; }
            public bool ClockStarted { get; set; }     // el escenario llegó a abrirse (hay tiempo de marcha)
            public double Km { get; set; }
            public int DurationS { get; set; }
            public int PaxBoarded { get; set; }
            public double PaxKm { get; set; }
            public double? TrainCap { get; set; }
            public double? TrainMass { get; set; }
            public List<JStop> Stops { get; set; } = new();
            public List<JInfr> Infr { get; set; } = new();
        }
        sealed class JStop { public string Station { get; set; } = ""; public string Time { get; set; } = ""; public DateTime Utc { get; set; } public int Board { get; set; } public int Alight { get; set; } }
        sealed class JInfr { public string Code { get; set; } = ""; public Dictionary<string, object> Detail { get; set; } }

        // Junto a las cachés de SelectOR (las pruebas la llevan a su carpeta con SELECTOR_CACHE_DIR).
        static string JournalPath => System.IO.Path.Combine(AppDataTidy.CacheRoot, "servicio-en-curso.json");

        DateTime _journalSavedUtc = DateTime.MinValue;
        string _svcRouteOverride;      // ruta del servicio recuperado (para la ventana de resultado)
        string _svcRecoveredNote;      // aviso «servicio recuperado» en la ventana de resultado
        bool _recovering;
        System.Windows.Forms.Timer _recoverWait;   // espera a que se cierre un Open Rails que siguió abierto

        // Guarda lo recogido del servicio en curso (como mucho cada 5 s, salvo force).
        void SaveServiceJournal(bool force = false)
        {
            if (_pendingServiceId == null || _recovering || string.IsNullOrEmpty(Supa.UserId)) return;
            if (!force && (DateTime.UtcNow - _journalSavedUtc).TotalSeconds < 5) return;
            _journalSavedUtc = DateTime.UtcNow;
            try
            {
                var j = new SvcJournal
                {
                    UserId = Supa.UserId, ServiceId = _pendingServiceId,
                    CompanyId = _empOnDutyCompany?.Id ?? _empSel?.Id ?? "", CompanyName = _empOnDutyCompany?.Name ?? _empSel?.Name ?? "",
                    Route = string.IsNullOrEmpty(_drivenRoute) ? _curRoute?.Name ?? "" : _drivenRoute,
                    Consist = _drivenLabel ?? "", Path = _drivenPath ?? "",
                    OpenedUtc = _svcOpenedUtc ?? DateTime.UtcNow, SavedUtc = DateTime.UtcNow,
                    ClockStarted = _svcClockUtc != null,
                    Km = Math.Round(_trackedMeters / 1000.0, 2), DurationS = ServiceSeconds(),
                    PaxBoarded = _paxBoarded, PaxKm = Math.Round(_paxKm, 1),
                    TrainCap = double.IsNaN(_svcTrainCap) ? null : _svcTrainCap,
                    TrainMass = double.IsNaN(_svcTrainMass) ? null : _svcTrainMass,
                    Stops = _svcStopsLog.Where(s => _svcOpenedUtc == null || s.Utc >= _svcOpenedUtc.Value.AddSeconds(-5))
                                        .Select(s => new JStop { Station = s.Station ?? "", Time = s.Time ?? "", Utc = s.Utc, Board = s.Board, Alight = s.Alight }).ToList(),
                    Infr = _infrItems.Select(i => new JInfr { Code = i.code, Detail = i.detail }).ToList()
                };
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(JournalPath));
                string tmp = JournalPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(j));
                File.Move(tmp, JournalPath, true);   // se sustituye de una vez: nunca queda a medias
            }
            catch { }
        }

        static void DeleteServiceJournal() { try { File.Delete(JournalPath); } catch { } }

        static SvcJournal ReadServiceJournal()
        {
            try { return File.Exists(JournalPath) ? JsonSerializer.Deserialize<SvcJournal>(File.ReadAllText(JournalPath)) : null; }
            catch { return null; }
        }

        // ¿Se está conduciendo desde este SelectOR? (entonces los datos en memoria son los del viaje actual)
        bool DrivingNow => _kmTimer != null || _pendingServiceId != null;

        // Registra el servicio que quedó abierto si SelectOR se cerró de golpe. Devuelve cuando ha terminado
        // (o enseguida si no hay nada que recuperar).
        async Task RecoverInterruptedServiceAsync(bool showDialog = true)
        {
            if (_recovering || !Supa.IsLoggedIn || DrivingNow) return;
            var j = ReadServiceJournal();
            if (j == null) { if (File.Exists(JournalPath)) DeleteServiceJournal(); return; }
            if (!string.Equals(j.UserId, Supa.UserId, StringComparison.OrdinalIgnoreCase)) return;   // de otra cuenta: para cuando entre
            if (OpenRailsRunning()) { WaitForOpenRailsThenRecover(); return; }   // (MainMenuForm.Updates.cs)

            _recovering = true;
            try
            {
                // ¿Sigue abierto? Si ya se cerró (o se borró), no hay nada que hacer.
                var (json, err) = await Supa.SelectAsync($"services?select=status&id=eq.{Uri.EscapeDataString(j.ServiceId)}");
                if (err != null) return;   // sin conexión: se reintentará la próxima vez
                bool open = false;
                try { using var d = JsonDocument.Parse(json); foreach (var e in d.RootElement.EnumerateArray()) open = Str(e, "status") == "open"; } catch { }
                if (!open) { DeleteServiceJournal(); return; }

                // Los datos del viaje, tal como quedaron, en el estado del servicio.
                var prevDuty = _empOnDutyCompany;
                _empOnDutyCompany = _empCompanies.FirstOrDefault(c => c.Id == j.CompanyId) ?? prevDuty;
                _pendingServiceId = j.ServiceId;
                _svcOpenedUtc = j.OpenedUtc;
                StartSvcClock(j.ClockStarted ? j.OpenedUtc : (DateTime?)null);   // (también vacía las infracciones)
                _tripDurationS = j.ClockStarted ? j.DurationS : 0;
                if (!j.ClockStarted) _svcClockUtc = j.OpenedUtc;   // el escenario no llegó a abrirse: 0 s de marcha
                _estimatedKm = Math.Round(j.Km, 1);
                _paxBoarded = j.PaxBoarded; _paxKm = j.PaxKm;
                _svcTrainCap = j.TrainCap ?? double.NaN; _svcTrainMass = j.TrainMass ?? double.NaN;
                _drivenLabel = j.Consist; _drivenPath = j.Path;
                _svcStopsLog.Clear();
                foreach (var s in j.Stops) _svcStopsLog.Add(new StopRec { Station = s.Station, Time = s.Time, Utc = s.Utc, Board = s.Board, Alight = s.Alight });
                foreach (var i in j.Infr) _infrItems.Add((i.Code, i.Detail ?? new Dictionary<string, object>()));
                _svcRouteOverride = j.Route;
                _svcRecoveredNote = string.Format(Tr("Viaje recuperado: Open Rails y SelectOR se cerraron durante el servicio. Se han usado los datos recogidos hasta el {0}."),
                    j.SavedUtc.ToLocalTime().ToString(I18n.English ? "d MMM · HH:mm" : "d 'de' MMMM 'a las' HH:mm", I18n.English ? System.Globalization.CultureInfo.GetCultureInfo("en-GB") : EsEs));

                if (showDialog) ShowPage(PageEmpresas);
                var (ok, _) = await EmpCloseServiceCore(showDialog);
                if (ok || _pendingServiceId == null) DeleteServiceJournal();
                _pendingServiceId = null; _svcOpenedUtc = null; _svcClockUtc = null;
                _empOnDutyCompany = prevDuty;
                UpdateDutyUi();
            }
            finally
            {
                _svcRouteOverride = null; _svcRecoveredNote = null;
                _recovering = false;
            }
        }

        // Solo se cayó SelectOR y Open Rails sigue abierto: se registra en cuanto se cierre.
        void WaitForOpenRailsThenRecover()
        {
            if (_recoverWait != null) return;
            _recoverWait = new System.Windows.Forms.Timer { Interval = 5000 };
            _recoverWait.Tick += async (s, e) =>
            {
                if (OpenRailsRunning()) return;
                _recoverWait.Stop(); _recoverWait.Dispose(); _recoverWait = null;
                await RecoverInterruptedServiceAsync();
            };
            _recoverWait.Start();
        }
    }
}
