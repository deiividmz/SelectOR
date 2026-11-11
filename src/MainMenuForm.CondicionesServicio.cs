// (2026-11-10, petición) La hora de salida configurada en el simulador, la estación del año y el clima de cada servicio, para su
// tarjeta (ServiceCards.cs, con los iconos de Conducción libre). Se apuntan al lanzar Open Rails (de lo que se le manda: Conducción
// libre y la sala llevan hora, estación y clima; Horarios, estación y clima; una actividad, lo de su .act) y se guardan en el
// servicio al abrirlo (antes de lanzar o ya conduciendo) con set_service_conditions (sql/servicio-condiciones.sql). Sin ese SQL,
// no se guarda nada y todo sigue igual.

using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Activity = ORTS.Menu.Activity;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        (string time, int season, int weather)? _launchCond;
        static bool _svcCondRpcMissing;

        static readonly Regex CondExplore = new(@"""\s+(\d{1,2}:\d{2})\s+(\d)\s+(\d)\s*$");          // … "path" "consist" 07:30 2 0
        static readonly Regex CondTimetable = new(@"-timetable\s.*\s(\d+)\s+(\d)\s+(\d)\s*$");        // … día estación clima

        void CaptureLaunchConditions(string args)
        {
            _launchCond = null;
            try
            {
                if (string.IsNullOrEmpty(args)) return;
                var m = CondExplore.Match(args);
                if (m.Success) { _launchCond = (Hhmm(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)); return; }
                m = CondTimetable.Match(args);
                if (m.Success) { _launchCond = (null, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)); return; }
                if (args.IndexOf("-activity", StringComparison.OrdinalIgnoreCase) >= 0 && _lstActivities?.SelectedItem is Activity a)
                {
                    var am = ActMetaOf(a);
                    int s = Array.IndexOf(new[] { "spring", "summer", "autumn", "winter" }, am.SeasonKind ?? "");
                    int w = Array.IndexOf(new[] { "clear", "snow", "rain" }, am.WeatherKind ?? "");
                    if (!string.IsNullOrEmpty(am.Start) || s >= 0 || w >= 0) _launchCond = (string.IsNullOrEmpty(am.Start) ? null : am.Start, s, w);
                }
            }
            catch { _launchCond = null; }
        }

        static string Hhmm(string t) { var p = t.Split(':'); return p.Length == 2 && int.TryParse(p[0], out int h) && int.TryParse(p[1], out int mi) ? $"{h:00}:{mi:00}" : null; }

        // al abrir el servicio (sin esperar: si falla, el servicio sigue igual)
        async void SaveServiceConditions(string serviceId)
        {
            var c = _launchCond;
            if (c == null || _svcCondRpcMissing || string.IsNullOrEmpty(serviceId) || !Supa.IsLoggedIn) return;
            try
            {
                var (_, err) = await Supa.RpcAsync("set_service_conditions", new { p_service = serviceId, p_time = c.Value.time, p_season = c.Value.season, p_weather = c.Value.weather });
                if (err != null && (err.Contains("PGRST202") || err.Contains("Could not find the function"))) _svcCondRpcMissing = true;
            }
            catch { }
        }
    }
}
