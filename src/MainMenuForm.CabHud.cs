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
                var units = TrainCabUnits(tren);   // unidades y escalas de los instrumentos de la cabina (.cvf)
                _cabHud = new CabHudOverlay(_prefs, OrWebPort(), TrainMaxKmh(tren), CabDayOfYear(), TrainTraction(tren), TrainIsPassenger(tren),
                                            TrainBrakeDecel(tren), units);
                _cabHud.TrainName = tren?.Name;
                _cabHud.MachineKey = CabMachineKey(tren);   // escala de los manómetros elegida para esta máquina
                var (mrBar, bcBar) = TrainBrakePressures(tren);
                _cabHud.SetTrainPressures(mrBar, bcBar);    // la escala automática cubre las presiones reales del tren
                if (_tHave) _cabHud.SetPosition(_tLat, _tLon);
                var _ = _cabHud.Handle;
                if (_scenarioReady || force) _cabHud.Show(); else _cabWaiting = true;   // aparece con el escenario cargado
            }
            catch { _cabHud = null; }
        }

        // Máquina de cabeza del tren (carpeta\nombre de su .eng): clave para recordar la escala de sus manómetros.
        static string CabMachineKey(TrainItem tren)
        {
            var fp = tren?.Locomotive?.FilePath;
            if (string.IsNullOrEmpty(fp)) return null;
            return Path.GetFileName(Path.GetDirectoryName(fp)) + "\\" + Path.GetFileNameWithoutExtension(fp);
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
        // marca el fondo de escala del velocímetro. 0 si no se puede leer (entonces, la de la cabina o 0-160).
        double TrainMaxKmh(TrainItem c)
        {
            double vmax = 0;
            try
            {
                foreach (var r in ConsistCarRefs(c?.FilePath))
                {
                    if (!r.isEngine) continue;
                    double v = EngMaxKmh(ResolveCarFile(r.name, r.folder));
                    if (v > 0) vmax = vmax <= 0 ? v : Math.Min(vmax, v);
                }
            }
            catch { }
            return vmax;
        }

        // MaxVelocity de un .eng en km/h. Lo normal es que esté al principio del archivo (Veh), pero muchas
        // máquinas lo traen en un «include ( ../Common/… )» o muy abajo: entonces se lee el archivo entero y
        // se siguen sus include, como hace Open Rails (si hay varios, manda el último que se lee).
        static readonly System.Collections.Generic.Dictionary<string, double> _engMaxKmh = new(StringComparer.OrdinalIgnoreCase);
        static double EngMaxKmh(string eng)
        {
            if (string.IsNullOrEmpty(eng)) return 0;
            lock (_engMaxKmh) if (_engMaxKmh.TryGetValue(eng, out var hit)) return hit;
            double v = Veh(eng)?.SpeedKmh ?? 0;
            if (v <= 0) { try { v = SpeedInFile(eng, 0); } catch { v = 0; } }
            lock (_engMaxKmh) _engMaxKmh[eng] = v;
            return v;
        }

        static double SpeedInFile(string file, int depth)
        {
            if (depth > 4 || !File.Exists(file) || !Native.IsLocalFile(file)) return 0;
            string t = ReadHead(file, 32000000);
            double v = ExtractSpeedKmh(t);
            if (v > 0) return v;
            string dir = Path.GetDirectoryName(file);
            foreach (Match m in Regex.Matches(t, @"\bInclude\s*\(\s*""?([^"")]+?)""?\s*\)", RegexOptions.IgnoreCase))
            {
                string p;
                try { p = Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value.Trim().Replace("\\\\", "\\").Replace('/', '\\'))); } catch { continue; }
                double x = SpeedInFile(p, depth + 1);
                if (x > 0) v = x;
            }
            return v;
        }

        // Instrumentos de la cabina del tren: el .cvf de la máquina de cabeza («CabView ( … )» en su .eng,
        // dentro de su carpeta CABVIEW) dice en qué unidades y con qué escala están su velocímetro
        // («Units ( KM_PER_HOUR )» o «MILES_PER_HOUR», «ScaleRange ( 0 120 )») y sus manómetros
        // (MAIN_RES / BRAKE_PIPE y BRAKE_CYL: BAR, PSI, KILO_PASCALS, INCHES_OF_MERCURY…). El pupitre
        // se dibuja igual. null / 0 = no se ha podido leer (entonces, km/h, bar y escalas por defecto).
        CabHudOverlay.CabUnits TrainCabUnits(TrainItem c)
        {
            try
            {
                foreach (var r in ConsistCarRefs(c?.FilePath))
                {
                    if (!r.isEngine) continue;   // la máquina de cabeza es la que se conduce
                    string eng = ResolveCarFile(r.name, r.folder);
                    string cab = EngFind(eng, @"\bCabView\s*\(\s*""?([^"")]+?)""?\s*\)", 0);
                    if (string.IsNullOrWhiteSpace(cab)) return new CabHudOverlay.CabUnits();
                    string dir = Path.GetDirectoryName(eng), rel = cab.Trim().Replace('/', '\\');
                    foreach (var cvf in new[] { Path.Combine(dir, "CABVIEW", rel), Path.Combine(dir, rel) })
                        if (File.Exists(cvf) && Native.IsLocalFile(cvf)) return CvfUnits(ReadHead(cvf, 4000000));
                    break;
                }
            }
            catch { }
            return new CabHudOverlay.CabUnits();
        }

        // Del .cvf: cada control es un bloque «Type ( SPEEDOMETER DIAL )», «Type ( BRAKE_PIPE DIAL )»…
        // con sus Units y ScaleRange. La escala se toma de las esferas (DIAL); un marcador digital o
        // de agujas solo da las unidades.
        static CabHudOverlay.CabUnits CvfUnits(string t)
        {
            var u = new CabHudOverlay.CabUnits();
            if (string.IsNullOrEmpty(t)) return u;
            foreach (Match m in Regex.Matches(t, @"\bType\s*\(\s*(SPEEDOMETER|MAIN_RES|BRAKE_PIPE|BRAKE_CYL)\s+(\w+)", RegexOptions.IgnoreCase))
            {
                // El bloque del control llega hasta el Type del control siguiente.
                int end = t.IndexOf("Type", m.Index + m.Length, StringComparison.OrdinalIgnoreCase);
                int len = (end > 0 ? end : Math.Min(t.Length, m.Index + 3000)) - m.Index;
                string blk = t.Substring(m.Index, Math.Max(0, len));
                string units = Regex.Match(blk, @"\bUnits\s*\(\s*(\w+)", RegexOptions.IgnoreCase).Groups[1].Value.ToUpperInvariant();
                bool dial = m.Groups[2].Value.Equals("DIAL", StringComparison.OrdinalIgnoreCase);
                double max = 0;
                var sr = Regex.Match(blk, @"\bScaleRange\s*\(\s*(-?[\d.]+)\s+(-?[\d.]+)", RegexOptions.IgnoreCase);
                if (dial && sr.Success) double.TryParse(sr.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out max);
                switch (m.Groups[1].Value.ToUpperInvariant())
                {
                    case "SPEEDOMETER":
                        if (u.SpeedMph == null && units.Length > 0) u.SpeedMph = units.Contains("MILES");
                        if (u.SpeedScale <= 0 && max > 0) u.SpeedScale = units.Contains("METRES") ? max * 3.6 : max;   // en m/s (raro): km/h
                        break;
                    case "BRAKE_CYL":
                        if (u.BcUnits == null && units.Length > 0) u.BcUnits = units;
                        if (u.BcScale <= 0 && max > 0) u.BcScale = max;
                        break;
                    default:   // MAIN_RES y BRAKE_PIPE comparten el manómetro doble (TDP/TFA)
                        if (u.AirUnits == null && units.Length > 0) u.AirUnits = units;
                        if (max > u.AirScale) u.AirScale = max;
                        break;
                }
            }
            return u;
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
            if (SeatsOverride(c) != null) return true;   // la empresa le ha fijado plazas: es de viajeros, como con PassengerCapacity
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

        // Presiones máximas reales de la máquina de cabeza (de su .eng o sus Include), en bar: depósito principal
        // (AirBrakesMainMaxAirPressure) y cilindro de freno (la mayor entre la de frenado máximo y la del freno
        // de la máquina). Sin unidad, MSTS las da en psi. 0 = no se sabe.
        (double mr, double bc) TrainBrakePressures(TrainItem c)
        {
            try
            {
                foreach (var r in ConsistCarRefs(c?.FilePath))
                {
                    if (!r.isEngine) continue;
                    string eng = ResolveCarFile(r.name, r.folder);
                    if (eng == null) return (0, 0);
                    double P(string key) => PressToBar(EngFind(eng, @"\b" + key + @"\s*\(\s*([^)]*)\)", 0));
                    double mr = P("AirBrakesMainMaxAirPressure");
                    double bc = Math.Max(P("BrakeCylinderPressureForMaxBrakeBrakeForce"), P("EngineBrakesControllerMaxSystemPressure"));
                    return (mr, bc);
                }
            }
            catch { }
            return (0, 0);
        }

        static double PressToBar(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            var m = Regex.Match(s.Trim(), @"^(-?[\d.]+)\s*([A-Za-z/^\d]*)");
            if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) || v <= 0) return 0;
            string u = m.Groups[2].Value.ToLowerInvariant();
            if (u.StartsWith("bar")) return v;
            if (u.StartsWith("kpa")) return v / 100.0;
            if (u.StartsWith("mpa")) return v * 10.0;
            if (u.StartsWith("inhg")) return v * 0.0338639;
            if (u.StartsWith("kgf")) return v * 0.980665;
            return v * 0.0689476;   // psi (o sin unidad, como en MSTS)
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
