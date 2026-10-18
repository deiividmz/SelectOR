// El sol de las rutas CGL a la hora de España (1.2.68).
// Los .env de la ruta traen la salida y la puesta del sol fijas (otoño: 06:15 y 18:01) y, si las traen, Open Rails las
// usa tal cual (SunMoonPos.SolarAngle) en vez de calcularlas: a las 19:00 ya era de noche. Aquí, en memoria (no cambia
// ningún archivo de la ruta ni de Open Rails), se cambian por las de verdad en España (Madrid) para la fecha, con el
// cambio de hora: la de hoy si la estación del juego es la de ahora (sala compartida: siempre); si no, la de mitad de
// esa estación.

using System;
using HarmonyLib;
using Orts.Formats.Msts;
using Orts.MultiPlayer;

namespace SelectOR.MapAgent
{
    public static class Sol
    {
        const double Lat = 40.4168, Lon = -3.7038;   // Madrid
        static object _done;                          // el sol (del .env) ya puesto

        // (desde Agent.Install) 1 = puesto.
        public static int Install(Harmony h)
        {
            try
            {
                var t = AccessTools.TypeByName("Orts.Viewer3D.Common.SunMoonPos");
                var m = t == null ? null : AccessTools.Method(t, "SolarAngle");
                if (m == null) return 0;
                h.Patch(m, prefix: new HarmonyMethod(typeof(Sol), nameof(Before)));
                return 1;
            }
            catch (Exception ex) { Agent.Log("sol: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        static void Before(EnvironmentFile.SkySatellite sun)
        {
            if (sun == null || ReferenceEquals(sun, _done)) return;
            _done = sun;
            try
            {
                var sim = MPManager.Simulator;
                string route = System.IO.Path.GetFileName((sim?.RoutePathName ?? "").TrimEnd('\\', '/'));
                if (!route.StartsWith("CGL", StringComparison.OrdinalIgnoreCase) || sun.RiseTime <= 0 || sun.SetTime <= 0) return;
                var tz = Madrid();
                DateTime today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;
                int season = (int)sim.Season;   // 0 primavera · 1 verano · 2 otoño · 3 invierno
                DateTime day = SeasonOf(today.Month) == season ? today : new DateTime(today.Year, new[] { 4, 7, 10, 1 }[season], 15);
                double off = tz.GetUtcOffset(day.AddHours(12)).TotalHours;
                var (rise, set) = SunTimes(day, off);
                int r0 = sun.RiseTime, s0 = sun.SetTime;
                sun.RiseTime = (int)Math.Round(rise * 3600); sun.SetTime = (int)Math.Round(set * 3600);
                Agent.Log($"sol de {route} a la hora de España ({day:dd/MM}): salida {Hm(sun.RiseTime)} y puesta {Hm(sun.SetTime)} (la ruta traía {Hm(r0)} y {Hm(s0)})");
            }
            catch (Exception ex) { Agent.Log("sol: " + ex.GetType().Name + " " + ex.Message); }
        }

        static int SeasonOf(int month) => month >= 3 && month <= 5 ? 0 : month >= 6 && month <= 8 ? 1 : month >= 9 && month <= 11 ? 2 : 3;

        static TimeZoneInfo Madrid()
        {
            foreach (var id in new[] { "Romance Standard Time", "Europe/Madrid" })
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
            return TimeZoneInfo.Local;
        }

        // Salida y puesta del sol en Madrid (horas locales, con «off» horas sobre UTC). Fórmulas de la NOAA.
        public static (double rise, double set) SunTimes(DateTime day, double off)
        {
            double g = 2 * Math.PI / 365 * (day.DayOfYear - 1);
            double eq = 229.18 * (0.000075 + 0.001868 * Math.Cos(g) - 0.032077 * Math.Sin(g) - 0.014615 * Math.Cos(2 * g) - 0.040849 * Math.Sin(2 * g));
            double decl = 0.006918 - 0.399912 * Math.Cos(g) + 0.070257 * Math.Sin(g) - 0.006758 * Math.Cos(2 * g) + 0.000907 * Math.Sin(2 * g)
                        - 0.002697 * Math.Cos(3 * g) + 0.00148 * Math.Sin(3 * g);
            double lat = Lat * Math.PI / 180;
            double ha = Math.Acos(Math.Cos(90.833 * Math.PI / 180) / (Math.Cos(lat) * Math.Cos(decl)) - Math.Tan(lat) * Math.Tan(decl)) * 180 / Math.PI;
            double rise = 720 - 4 * (Lon + ha) - eq, set = 720 - 4 * (Lon - ha) - eq;   // minutos UTC
            return (rise / 60 + off, set / 60 + off);
        }

        static string Hm(int s) => $"{s / 3600:00}:{s / 60 % 60:00}";
    }
}
