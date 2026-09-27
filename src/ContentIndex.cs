// Índice en disco del contenido ya analizado.
//
// Leer el contenido cuesta unos 400 MB de disco en cada arranque: la cabecera de los ~47.000
// vehículos, los 11.000 .con y los .eng completos de las máquinas de cabeza. Cuando Windows tiene
// esos archivos en su caché se hace en dos segundos; en frío se va a veinte.
//
// Aquí se guarda lo YA DEDUCIDO de cada carpeta de contenido —trenes con su motriz, cabeceras de
// los vehículos, qué máquinas llevan cabina y las formaciones fijas— en un único archivo comprimido
// dentro de %AppData%\Open Rails. En el siguiente arranque se lee ese archivo (unas décimas) en vez
// de releer el contenido entero.
//
// Para saber si sigue valiendo se compara una HUELLA de las carpetas: cuántos archivos hay y cuál es
// la fecha del más reciente, en CONSISTS y en TRAINSET. Si alguien añade, quita o edita contenido,
// la huella cambia, el índice se descarta y se vuelve a leer todo (y se guarda de nuevo).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SelectOR
{
    public static class ContentIndex
    {
        const string Marca = "SELECTOR-INDEX 2";

        public sealed class UnitRow
        {
            public string Head = "", RepConsist = "";
            public int Cars = 1;
            public bool Rigid;
            public List<string> Members = new();
        }

        // ---- lo que se guarda ----
        public static readonly List<(string name, string conPath, string leadPath)> Consists = new();
        public static readonly ConcurrentDictionary<string, bool> Cab = new(StringComparer.OrdinalIgnoreCase);
        public static readonly ConcurrentDictionary<string, (int coupling, string kind, double powerKw)> Heads = new(StringComparer.OrdinalIgnoreCase);
        public static readonly Dictionary<string, UnitRow> Units = new(StringComparer.OrdinalIgnoreCase);
        public static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase);
        public static readonly Dictionary<string, List<string>> ConsistEngines = new(StringComparer.OrdinalIgnoreCase);
        public static readonly Dictionary<string, string> FirstConsistOfEng = new(StringComparer.OrdinalIgnoreCase);

        static string _folder = "";
        static string _fingerprint = "";
        static bool _loaded;          // el índice del disco vale: no hace falta releer el contenido
        static bool _saved;
        static int _baseHeads, _baseCabs;   // lo que traía el archivo: si se descubre más, se reescribe
        static readonly object _lock = new();

        /// <summary>¿Hay índice válido para esta carpeta? (true = no hace falta leer el contenido)</summary>
        public static bool Loaded => _loaded;
        public static bool HasUnits { get { lock (_lock) return _loaded && Units.Count > 0; } }

        /// <summary>Prepara el índice de una carpeta de contenido: lo carga si sigue valiendo.</summary>
        public static void Open(string folderPath)
        {
            lock (_lock)
            {
                if (_loaded && string.Equals(_folder, folderPath, StringComparison.OrdinalIgnoreCase)) return;
                Reset(folderPath);
                _fingerprint = Fingerprint(folderPath);
                try { _loaded = Load(FilePathFor(folderPath)); }
                catch { _loaded = false; }
                if (!_loaded) ResetData();
                Log(_loaded
                    ? $"índice: se reutiliza ({Consists.Count} trenes, {Heads.Count} cabeceras, {Units.Count} formaciones)"
                    : "índice: no vale (se vuelve a leer el contenido)");
            }
        }

        /// <summary>Guarda lo analizado (se llama cuando ya está todo: trenes, cabeceras y formaciones).
        /// Si el índice venía del disco pero esta sesión ha leído vehículos nuevos, se reescribe con ellos.</summary>
        public static void Save()
        {
            lock (_lock)
            {
                if (_folder.Length == 0) return;
                bool nuevo = !_loaded && !_saved && Consists.Count > 0 && Units.Count > 0;
                // Se reescribe solo si esta sesión ha descubierto bastantes vehículos nuevos: no tiene
                // sentido volcar el índice entero por dos cabeceras sueltas.
                bool ampliado = _loaded && (Heads.Count > _baseHeads + 50 || Cab.Count > _baseCabs + 50);
                if (!nuevo && !ampliado) return;
                try
                {
                    Write(FilePathFor(_folder));
                    _saved = true;
                    _baseHeads = Heads.Count; _baseCabs = Cab.Count;
                    Log($"índice guardado ({Consists.Count} trenes, {Heads.Count} cabeceras, {Cab.Count} cabinas)");
                }
                catch { try { File.Delete(FilePathFor(_folder)); } catch { } }
            }
        }

        public static void SetConsists(IEnumerable<(string name, string conPath, string leadPath)> filas)
        {
            lock (_lock)
            {
                if (_loaded) return;
                Consists.Clear();
                foreach (var f in filas) Consists.Add(f);
            }
        }

        public static void SetUnits(Dictionary<string, UnitRow> units, IEnumerable<string> hidden,
                                    Dictionary<string, List<string>> conEngines, Dictionary<string, string> firstConsist)
        {
            lock (_lock)
            {
                if (_loaded) return;
                Units.Clear(); foreach (var kv in units) Units[kv.Key] = kv.Value;
                Hidden.Clear(); foreach (var h in hidden) Hidden.Add(h);
                ConsistEngines.Clear(); foreach (var kv in conEngines) ConsistEngines[kv.Key] = kv.Value;
                FirstConsistOfEng.Clear(); foreach (var kv in firstConsist) FirstConsistOfEng[kv.Key] = kv.Value;
            }
        }

        // Diagnóstico opcional (SELECTOR_LOADLOG=1), el mismo registro que usa el arranque.
        static void Log(string texto)
        {
            try
            {
                if (Environment.GetEnvironmentVariable("SELECTOR_LOADLOG") != "1") return;
                double ms = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "selector_load.log"),
                    $"{ms,8:F0} ms   {texto}{Environment.NewLine}");
            }
            catch { }
        }

        // ---- huella de la carpeta: cuántos archivos y cuál es el más reciente ----
        static string Fingerprint(string folderPath)
        {
            var sb = new StringBuilder();
            sb.Append(Huella(Path.Combine(folderPath, "TRAINS", "CONSISTS"), "*.con", false)).Append('|');
            sb.Append(Huella(Path.Combine(folderPath, "TRAINS", "TRAINSET"), "*.eng", true)).Append('|');
            sb.Append(Huella(Path.Combine(folderPath, "TRAINS", "TRAINSET"), "*.wag", true));
            return sb.ToString();
        }

        static string Huella(string dir, string patron, bool recursivo)
        {
            try
            {
                if (!Directory.Exists(dir)) return "0:0";
                long n = 0, ultimo = 0;
                var opciones = new EnumerationOptions
                {
                    RecurseSubdirectories = recursivo,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System
                };
                // Solo metadatos del directorio: no se abre ningún archivo.
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles(patron, opciones))
                {
                    n++;
                    long t = f.LastWriteTimeUtc.Ticks;
                    if (t > ultimo) ultimo = t;
                }
                return n + ":" + ultimo;
            }
            catch { return "?"; }
        }

        static string FilePathFor(string folderPath)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails", "SelectOR");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "contenido-" + Hash(folderPath) + ".idx");
        }

        static string Hash(string s)
        {
            using var md5 = MD5.Create();
            var b = md5.ComputeHash(Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
            var sb = new StringBuilder();
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString().Substring(0, 16);
        }

        static void Reset(string folderPath)
        {
            _folder = folderPath ?? "";
            _loaded = false; _saved = false;
            ResetData();
        }

        static void ResetData()
        {
            Consists.Clear(); Cab.Clear(); Heads.Clear();
            Units.Clear(); Hidden.Clear(); ConsistEngines.Clear(); FirstConsistOfEng.Clear();
        }

        // ---- lectura ----
        static bool Load(string file)
        {
            if (!File.Exists(file)) return false;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var sr = new StreamReader(gz, Encoding.UTF8);

            if (sr.ReadLine() != Marca) return false;
            if (!string.Equals(sr.ReadLine(), _folder, StringComparison.OrdinalIgnoreCase)) return false;
            if (sr.ReadLine() != _fingerprint) return false;   // el contenido ha cambiado

            string seccion = "";
            string linea;
            while ((linea = sr.ReadLine()) != null)
            {
                if (linea.Length == 0) continue;
                if (linea[0] == '[') { seccion = linea; continue; }
                var c = linea.Split('\t');
                switch (seccion)
                {
                    case "[consists]":
                        if (c.Length >= 3) Consists.Add((c[0], Abs(c[1]), c[2].Length == 0 ? null : Abs(c[2])));
                        break;
                    case "[cab]":
                        if (c.Length >= 2) Cab[Abs(c[0])] = c[1] == "1";
                        break;
                    case "[head]":
                        if (c.Length >= 4 && int.TryParse(c[1], out var cp) && double.TryParse(c[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var kw))
                            Heads[Abs(c[0])] = (cp, c[2], kw);
                        break;
                    case "[units]":
                        if (c.Length >= 6)
                        {
                            var u = new UnitRow { Head = c[1], Cars = int.TryParse(c[2], out var cars) ? cars : 1, Rigid = c[3] == "1", RepConsist = c[4].Length == 0 ? null : Abs(c[4]) };
                            if (c[5].Length > 0) u.Members.AddRange(c[5].Split('|'));
                            Units[c[0]] = u;
                        }
                        break;
                    case "[hidden]":
                        Hidden.Add(c[0]);
                        break;
                    case "[conengs]":
                        if (c.Length >= 2) ConsistEngines[Abs(c[0])] = c[1].Length == 0 ? new List<string>() : new List<string>(c[1].Split('|'));
                        break;
                    case "[firstcon]":
                        if (c.Length >= 2) FirstConsistOfEng[c[0]] = Abs(c[1]);
                        break;
                }
            }
            _baseHeads = Heads.Count; _baseCabs = Cab.Count;
            return Consists.Count > 0;
        }

        // ---- escritura ----
        static void Write(string file)
        {
            string tmp = file + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            using (var sw = new StreamWriter(gz, new UTF8Encoding(false)))
            {
                sw.WriteLine(Marca);
                sw.WriteLine(_folder);
                sw.WriteLine(_fingerprint);

                sw.WriteLine("[consists]");
                foreach (var (name, conPath, leadPath) in Consists)
                    sw.WriteLine(Limpia(name) + "\t" + Rel(conPath) + "\t" + Rel(leadPath));

                sw.WriteLine("[cab]");
                foreach (var kv in Cab) sw.WriteLine(Rel(kv.Key) + "\t" + (kv.Value ? "1" : "0"));

                sw.WriteLine("[head]");
                foreach (var kv in Heads)
                    sw.WriteLine(Rel(kv.Key) + "\t" + kv.Value.coupling + "\t" + (kv.Value.kind ?? "") + "\t" +
                                 kv.Value.powerKw.ToString(System.Globalization.CultureInfo.InvariantCulture));

                sw.WriteLine("[units]");
                foreach (var kv in Units)
                {
                    var u = kv.Value;
                    sw.WriteLine(Limpia(kv.Key) + "\t" + Limpia(u.Head) + "\t" + u.Cars + "\t" + (u.Rigid ? "1" : "0") + "\t" +
                                 Rel(u.RepConsist) + "\t" + string.Join("|", u.Members));
                }

                sw.WriteLine("[hidden]");
                foreach (var h in Hidden) sw.WriteLine(Limpia(h));

                sw.WriteLine("[conengs]");
                foreach (var kv in ConsistEngines) sw.WriteLine(Rel(kv.Key) + "\t" + string.Join("|", kv.Value));

                sw.WriteLine("[firstcon]");
                foreach (var kv in FirstConsistOfEng) sw.WriteLine(Limpia(kv.Key) + "\t" + Rel(kv.Value));
            }
            try { if (File.Exists(file)) File.Delete(file); } catch { }
            File.Move(tmp, file);
        }

        // Las rutas se guardan relativas a la carpeta de contenido (el archivo ocupa la mitad).
        static string Rel(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (_folder.Length > 0 && path.StartsWith(_folder, StringComparison.OrdinalIgnoreCase))
                return path.Substring(_folder.Length).TrimStart('\\', '/');
            return path;
        }

        static string Abs(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return rel;
            if (rel.Length > 1 && (rel[1] == ':' || rel[0] == '\\')) return rel;   // ya es absoluta
            return Path.Combine(_folder, rel);
        }

        static string Limpia(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace('\t', ' ').Replace('|', '/');
    }
}
