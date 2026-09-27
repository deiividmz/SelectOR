// Pupitre (indicadores del tren) durante la conducción: se muestra u oculta desde la barra
// superior («Pupitre») o desde el HUD del mini-mapa, y se recuerda si lo dejaste abierto (la
// primera vez, oculto). Se cierra al volver.
// Funciona en cualquier conducción: servicios de empresa, Horarios, Actividades o libre.

using System;
using System.IO;
using System.Text.RegularExpressions;
using Activity = ORTS.Menu.Activity;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        CabHudOverlay _cabHud;

        bool CabHudAlive => _cabHud != null && !_cabHud.IsDisposed;

        void ShowCabHud(bool force = false)
        {
            if (_prefs == null) return;
            CloseCabHud();
            try
            {
                var tren = _drivenConsist ?? CurrentDrivenConsist();
                _cabHud = new CabHudOverlay(_prefs, OrWebPort(), TrainMaxKmh(tren), CabDayOfYear(), TrainTraction(tren), TrainIsPassenger(tren),
                                            TrainBrakeDecel(tren));
                _cabHud.TrainName = tren?.Name;
                if (_tHave) _cabHud.SetPosition(_tLat, _tLon);
                var _ = _cabHud.Handle;
                if (_scenarioReady || force) _cabHud.Show(); else _cabWaiting = true;   // aparece con el escenario cargado
            }
            catch { _cabHud = null; }
        }

        // Día del año que usa Open Rails para cada estación (equinoccios y solsticios): con él y la
        // posición del tren, el pupitre calcula la altura del sol para saber si es de noche.
        int CabDayOfYear()
        {
            int s = -1;
            try
            {
                if (_activePage == 2) s = _segSeason.SelectedIndex;
                else if (_activePage == 3) s = _segTTSeason.SelectedIndex;
                else if (_activePage == 1 && _lstActivities.SelectedItem is Activity a) s = ActivitySeason(a.FilePath);
            }
            catch { }
            return s switch { 0 => 82, 1 => 173, 2 => 264, 3 => 355, _ => 173 };
        }

        // Estación de una actividad: «Season ( n )» en la cabecera del .act (0 primavera … 3 invierno).
        static int ActivitySeason(string actFile)
        {
            try
            {
                var m = Regex.Match(File.ReadAllText(actFile), @"Season\s*\(\s*(\d)", RegexOptions.IgnoreCase);
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
            catch { }
            return -1;
        }

        // Velocidad máxima del tren: la menor de sus máquinas (MaxVelocity de cada .eng). Es la que
        // marca el fondo de escala del velocímetro. 0 si no se puede leer (entonces, escala 0-160).
        double TrainMaxKmh(TrainItem c)
        {
            double vmax = 0;
            try
            {
                foreach (var r in ConsistCarRefs(c?.FilePath))
                {
                    if (!r.isEngine) continue;
                    double v = Veh(ResolveCarFile(r.name, r.folder))?.SpeedKmh ?? 0;
                    if (v > 0) vmax = vmax <= 0 ? v : Math.Min(vmax, v);
                }
            }
            catch { }
            return vmax;
        }

        // Tracción del tren, del bloque Engine de sus .eng: «Type ( Electric )», «Diesel» o «Steam»
        // (el bloque Wagon también tiene un Type, pero es «Engine»/«Carriage»: no se confunde). Si
        // alguna máquina es eléctrica, el tren lo es. null si no se puede leer (entonces el pupitre
        // lo deduce de lo que informe el simulador).
        string TrainTraction(TrainItem c)
        {
            bool diesel = false, steam = false;
            try
            {
                foreach (var r in ConsistCarRefs(c?.FilePath))
                {
                    if (!r.isEngine) continue;
                    string k = EngTraction(ResolveCarFile(r.name, r.folder), 0);
                    if (k == "electric") return "electric";
                    if (k == "diesel") diesel = true; else if (k == "steam") steam = true;
                }
            }
            catch { }
            return diesel ? "diesel" : steam ? "steam" : null;
        }

        // ¿Tren de viajeros? Sí si algún vehículo es coche de viajeros («Type ( Carriage )» en el
        // bloque Wagon) o declara plazas (ORTSPassengerCapacity, típico de automotores y
        // eléctricos de viajeros). No si se han leído todos y ninguno. null si no se pudo leer.
        bool? TrainIsPassenger(TrainItem c)
        {
            int leidos = 0;
            try
            {
                foreach (var r in ConsistCarRefs(c?.FilePath))
                {
                    string f = ResolveCarFile(r.name, r.folder);
                    if (string.IsNullOrEmpty(f) || !File.Exists(f)) continue;
                    leidos++;
                    if ((Veh(f)?.Capacity ?? 0) > 0) return true;
                    if (EngFind(f, @"\bType\s*\(\s*""?Carriage\b", 0) != null) return true;
                }
            }
            catch { return null; }
            return leidos > 0 ? false : (bool?)null;
        }

        // Busca un patrón en un .eng/.wag o en sus Include (hasta 3 niveles); devuelve el grupo 1
        // (o el texto encontrado si el patrón no tiene grupos).
        static string EngFind(string file, string pattern, int depth)
        {
            if (string.IsNullOrEmpty(file) || depth > 3 || !File.Exists(file)) return null;
            string t = ReadHead(file, 200000);
            if (string.IsNullOrEmpty(t)) return null;
            var m = Regex.Match(t, pattern, RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value;
            foreach (Match inc in Regex.Matches(t, @"\bInclude\s*\(\s*""?([^"")]+?)""?\s*\)", RegexOptions.IgnoreCase))
            {
                try
                {
                    string rel = inc.Groups[1].Value.Trim().Replace("\\\\", "\\").Replace('/', '\\');
                    string full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file), rel));
                    if (!Native.IsLocalFile(full)) continue;   // nunca rutas de red
                    string k = EngFind(full, pattern, depth + 1);
                    if (k != null) return k;
                }
                catch { }
            }
            return null;
        }

        // El Type del motor puede estar en el propio .eng o en un archivo incluido
        // («Include ( "..\common\450\eng450.inc" )»): se siguen los Include hasta 3 niveles.
        static string EngTraction(string file, int depth)
        {
            if (string.IsNullOrEmpty(file) || depth > 3 || !File.Exists(file)) return null;
            string t = ReadHead(file, 200000);
            if (string.IsNullOrEmpty(t)) return null;
            var m = Regex.Match(t, @"\bType\s*\(\s*""?(Diesel|Electric|Steam)\b", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
            foreach (Match inc in Regex.Matches(t, @"\bInclude\s*\(\s*""?([^"")]+?)""?\s*\)", RegexOptions.IgnoreCase))
            {
                try
                {
                    string rel = inc.Groups[1].Value.Trim().Replace("\\\\", "\\").Replace('/', '\\');
                    string full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file), rel));
                    if (!Native.IsLocalFile(full)) continue;   // nunca rutas de red
                    string k = EngTraction(full, depth + 1);
                    if (k != null) return k;
                }
                catch { }
            }
            return null;
        }

        // Deceleración de servicio para la curva de frenado del pupitre (m/s²): el 65 % de lo que da
        // la fuerza de freno total entre la masa del tren (kN / t = m/s²), entre 0,45 y 1,2. Si no se
        // puede leer, 0,65 (un frenado de servicio normal).
        double TrainBrakeDecel(TrainItem c)
        {
            try
            {
                var (_, masa, freno, _) = ConsistTotals(c);
                if (masa > 0 && freno > 0) return Math.Max(0.45, Math.Min(1.2, 0.65 * freno / masa));
            }
            catch { }
            return 0.65;
        }

        void CloseCabHud()
        {
            _cabWaiting = false;
            try { _cabHud?.CloseHud(); } catch { }
            _cabHud = null;
        }

        void ToggleCabHudFromBar()
        {
            if (_prefs == null) return;
            _cabWaiting = false;   // lo ha pedido a mano: manda lo que diga ahora
            if (!CabHudAlive) { ShowCabHud(force: true); _prefs.CabHudOn = true; }
            else if (_cabHud.Visible) { _cabHud.Hide(); _prefs.CabHudOn = false; }
            else { _cabHud.Show(); _prefs.CabHudOn = true; }
            try { _prefs.Save(); } catch { }
        }
    }
}
