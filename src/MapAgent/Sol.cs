// El sol de las rutas CGL a la hora de España (1.2.68).
// Los .env de la ruta traen la salida y la puesta del sol fijas (otoño: 06:15 y 18:01) y, si las traen, Open Rails las
// usa tal cual (SunMoonPos.SolarAngle) en vez de calcularlas: a las 19:00 ya era de noche. Aquí, en memoria (no cambia
// ningún archivo de la ruta ni de Open Rails), se cambian por las de verdad en España (Madrid) para la fecha, con el
// cambio de hora: la de hoy si la estación del juego es la de ahora (sala compartida: siempre); si no, la de mitad de
// esa estación.
// CYZYRails (variante de Open Rails con «cielo astronómico»): calcula el sol, la luna y las estrellas de verdad
// (Astronomy.cs), pero saca la zona horaria de la longitud (para España, UTC+0 en vez de UTC+1, y el cambio de hora solo
// si se elige en sus Opciones), usa la latitud y la longitud de las baldosas de la ruta y la fecha de sus Opciones o de
// mitad de la estación. En CGL: la hora de España (Madrid, con el cambio de hora), en Madrid y en el mismo día que arriba.

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
                Cyzy(h);
                Luces.Install(h);   // CYZYRails: luces lejanas agrupadas y con fundido (Luces.cs)
                SenalesCyzy.Install(h);   // CYZYRails: las luces de las señales con su color, no blancas (SenalesCyzy.cs)
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
                DateTime day = Day(sim, tz);
                double off = tz.GetUtcOffset(day.AddHours(12)).TotalHours;
                var (rise, set) = SunTimes(day, off);
                int r0 = sun.RiseTime, s0 = sun.SetTime;
                sun.RiseTime = (int)Math.Round(rise * 3600); sun.SetTime = (int)Math.Round(set * 3600);
                Agent.Log($"sol de {route} a la hora de España ({day:dd/MM}): salida {Hm(sun.RiseTime)} y puesta {Hm(sun.SetTime)} (la ruta traía {Hm(r0)} y {Hm(s0)})");
            }
            catch (Exception ex) { Agent.Log("sol: " + ex.GetType().Name + " " + ex.Message); }
        }

        // el día del juego: hoy si la estación del juego es la de ahora (sala compartida: siempre); si no, mitad de la estación
        static DateTime Day(Orts.Simulation.Simulator sim, TimeZoneInfo tz)
        {
            DateTime today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;
            int season = (int)sim.Season;   // 0 primavera · 1 verano · 2 otoño · 3 invierno
            return SeasonOf(today.Month) == season ? today : new DateTime(today.Year, new[] { 4, 7, 10, 1 }[season], 15);
        }

        // ── CYZYRails: cielo astronómico (ver arriba)
        static AccessTools.FieldRef<object, double> _lat, _lon;
        static bool? _cgl;
        static bool Cgl()
        {
            if (_cgl == null)
            {
                var sim = MPManager.Simulator;
                if (sim == null) return false;
                string route = System.IO.Path.GetFileName((sim.RoutePathName ?? "").TrimEnd('\\', '/'));
                _cgl = route.StartsWith("CGL", StringComparison.OrdinalIgnoreCase);
                if (_cgl == true)
                    try { Agent.Log($"cielo astronómico de CYZY en {route}: hora de España (Madrid, con el cambio de hora), en Madrid, día {Day(sim, Madrid()):dd/MM}"); } catch { }
            }
            return _cgl == true;
        }

        static void Cyzy(Harmony h)
        {
            try
            {
                var astro = AccessTools.TypeByName("Orts.Viewer3D.Common.Astronomy");
                var sky = AccessTools.TypeByName("Orts.Viewer3D.SkyViewer");
                if (astro == null || sky == null) return;   // (no es CYZY)
                var utc = AccessTools.Method(astro, "LocalToUtc");
                var date = AccessTools.Method(sky, "GetSkyBaseDate");
                var upd = AccessTools.Method(sky, "UpdateAstronomicalSky");
                if (AccessTools.Field(sky, "Latitude") != null && AccessTools.Field(sky, "Longitude") != null)
                { _lat = AccessTools.FieldRefAccess<double>(sky, "Latitude"); _lon = AccessTools.FieldRefAccess<double>(sky, "Longitude"); }
                if (utc != null) h.Patch(utc, prefix: new HarmonyMethod(typeof(Sol), nameof(BeforeUtc)));
                if (date != null) h.Patch(date, prefix: new HarmonyMethod(typeof(Sol), nameof(BeforeDate)));
                if (upd != null && _lat != null) h.Patch(upd, prefix: new HarmonyMethod(typeof(Sol), nameof(BeforeSky)));
                Agent.Log($"sol: cielo astronómico de CYZY: {(utc != null ? "hora" : "—")} · {(date != null ? "día" : "—")} · {(upd != null && _lat != null ? "lugar" : "—")}");
            }
            catch (Exception ex) { Agent.Log("sol (CYZY): " + ex.GetType().Name + " " + ex.Message); }
        }

        // la hora del juego (local) → UTC con la zona horaria de España
        static bool BeforeUtc(DateTime local, ref DateTime __result)
        {
            try
            {
                if (!Cgl()) return true;
                var tz = Madrid();
                var l = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
                __result = tz.IsInvalidTime(l) ? DateTime.SpecifyKind(l.AddHours(-tz.GetUtcOffset(l.AddHours(1)).TotalHours), DateTimeKind.Utc)
                                               : TimeZoneInfo.ConvertTimeToUtc(l, tz);
                return false;
            }
            catch { return true; }
        }

        static bool BeforeDate(ref DateTime __result)
        {
            try
            {
                if (!Cgl()) return true;
                __result = Day(MPManager.Simulator, Madrid());
                return false;
            }
            catch { return true; }
        }

        static void BeforeSky(object __instance)
        {
            try
            {
                if (!Cgl()) return;
                _lat(__instance) = Lat * Math.PI / 180; _lon(__instance) = Lon * Math.PI / 180;
            }
            catch { }
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
