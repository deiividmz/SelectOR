// (1.2.91, petición 2026-11-07) LOS TRENES IA DE LA SALA, COMO LOS QUIERA VER CADA UNO: el usuario elige en SelectOR un .con
// suyo para un servicio del horario de la sala, y su Open Rails monta los trenes de ese servicio con esa composición. Solo
// en su pantalla: el despachador y los demás siguen con el del horario.
//  · SelectOR manda la lista por el enlace local: «AICON BEGIN» · «AICON <servicio>\t<ruta del .con>» · «AICON END».
//  · El despachador llama a cada tren «<servicio> HH:MM» (Trafico.Create): al llegar su TRAIN (Sustitutos.BeforeMsg), si su
//    servicio está en la lista, su lista de vehículos se cambia ENTERA por la del .con (como cuando falta algún vehículo:
//    mismo número de vehículos, identificadores y largos, para que no se descuadre el multijugador; si el tren tiene más
//    vehículos, la composición se repite), y cada coche guarda el vehículo que era de verdad.
//  · (2026-11-11, revisión) Los trenes IA que ya estaban en la vía al entrar en la sala (o que tu Open Rails vuelve a pedir)
//    no llegan con TRAIN sino con UPDATETRAIN, que no lleva el nombre del tren: SelectOR manda también qué número de tren es
//    cada tren IA («AICON NUM <número>\t<nombre del tren>», de la lista de trenes de la sala: «IA-nombre-número») y así se
//    les pone también tu composición.
//  · Vale para los trenes IA que aparezcan desde que llega la lista (los que ya están en tu Open Rails, siguen como estaban).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SelectOR.Comun;

namespace SelectOR.MapAgent
{
    public static class IaComposiciones
    {
        static volatile Dictionary<string, string> _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, string> _new;
        static readonly ConcurrentDictionary<string, VehiculosCache.Con> _cons = new ConcurrentDictionary<string, VehiculosCache.Con>(StringComparer.OrdinalIgnoreCase);
        static readonly ConcurrentDictionary<string, bool> _said = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        static readonly ConcurrentDictionary<int, string> _byNum = new ConcurrentDictionary<int, string>();   // número de tren → su nombre
        static readonly Regex Hour = new Regex(@"\s+\d{1,2}:\d{2}$", RegexOptions.Compiled);

        // (los «_» de la lista de trenes de la sala son espacios: se comparan igual)
        static string Key(string s) => (s ?? "").Replace('_', ' ').Trim();

        // (hilo del enlace) la lista de SelectOR
        public static void Cmd(string s)
        {
            if (s == null) return;
            if (s.StartsWith("NUM ", StringComparison.Ordinal))
            {
                int t = s.IndexOf('\t');
                if (t > 4 && int.TryParse(s.Substring(4, t - 4).Trim(), out int num) && t + 1 < s.Length) _byNum[num] = s.Substring(t + 1).Trim();
                return;
            }
            if (s == "BEGIN") { _new = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); return; }
            if (s == "END")
            {
                if (_new == null) return;
                _map = _new; _new = null;
                Agent.Log(_map.Count == 0 ? "trenes IA de la sala: con su composición del horario" : $"trenes IA de la sala: {_map.Count} servicios con tu composición ({string.Join(", ", _map.Select(kv => kv.Key + " → " + Path.GetFileNameWithoutExtension(kv.Value)))})");
                return;
            }
            int k = s.IndexOf('\t');
            if (_new == null || k <= 0) return;
            string name = Key(s.Substring(0, k)), con = s.Substring(k + 1).Trim();
            if (name.Length > 0 && con.Length > 0) _new[name] = con;
        }

        // el nombre del tren IA de ese número, si SelectOR lo ha dicho (o null)
        public static string NameOf(int num) => _byNum.TryGetValue(num, out var n) ? n : null;
        // (al llegar su TRAIN, que sí lo trae)
        public static void Seen(int num, string name) { if (num >= 0 && !string.IsNullOrWhiteSpace(name)) _byNum[num] = name.Trim(); }

        // la composición elegida para el tren de ese nombre («<servicio> HH:MM»), o null
        public static VehiculosCache.Con For(string trainName)
        {
            var map = _map;
            if (map.Count == 0 || string.IsNullOrEmpty(trainName)) return null;
            string service = Key(Hour.Replace(Key(trainName), ""));
            if (!map.TryGetValue(service, out var path)) return null;
            if (_cons.TryGetValue(path, out var c)) return c;
            c = Load(path);
            _cons[path] = c;   // (también null: no se vuelve a leer en la sesión)
            if (c == null && _said.TryAdd(path, true)) Agent.Log($"trenes IA de la sala: no se puede usar {path} (no está o le faltan vehículos): se queda la del horario");
            return c;
        }

        // primero, entre tus composiciones enteras (las que tienen todos sus vehículos); si no, leyendo el .con
        static VehiculosCache.Con Load(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var c = VehiculosCache.Consists.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (c != null) return c;
            try { return VehiculosCache.ReadCon(path, Orts.MultiPlayer.MPManager.Simulator?.BasePath); } catch { return null; }
        }
    }
}
