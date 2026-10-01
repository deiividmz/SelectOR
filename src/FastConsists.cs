// Lectura propia de los trenes (.con) del contenido.
//
// Open Rails analiza cada .con con su tokenizador y, además, abre entero el .eng de la motriz para
// construir su objeto Locomotive: unos 21.000 análisis completos que cuestan ~4 s con 11.000 trenes.
// Aquí se saca de una sola pasada por archivo lo único que SelectOR necesita —nombre, archivo y
// motriz de cabeza— y el nombre de la motriz se lee solo cuando hay que enseñarlo.
//
// Se respetan las reglas de Open Rails para que la lista salga igual:
//   · el nombre es el Name ( ) de TrainCfg y, si no lo lleva, el identificador del propio TrainCfg;
//   · la motriz de cabeza es el primer coche motor conducible: el primero cuyo .eng declare cabina
//     (los remolques motores de un automotor no la llevan) o cuyo .eng falte del contenido;
//   · una composición sin ningún coche motor conducible no se lista, porque no se puede conducir.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SelectOR
{
    // Motriz de cabeza de un tren.
    public sealed class TrainEngine
    {
        string _name;
        public string FilePath { get; set; } = "";
        // El nombre sale del propio .eng, que es grande: se lee la primera vez que se pide.
        public string Name
        {
            get { return _name ?? (_name = FastConsists.EngineDisplayName(FilePath)); }
            set { _name = value; }
        }
        public override string ToString() => Name ?? "";
    }

    // Un tren del contenido.
    public sealed class TrainItem
    {
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
        public TrainEngine Locomotive { get; set; }
        public override string ToString() => Name ?? "";
    }

    public static class FastConsists
    {
        /// <summary>Trenes de una carpeta de contenido, ordenados por nombre. Lista vacía si no hay CONSISTS.</summary>
        public static List<TrainItem> Load(string folderPath)
        {
            var list = new List<TrainItem>();
            try
            {
                if (string.IsNullOrEmpty(folderPath)) return list;

                // ¿Hay índice en disco de esta carpeta? Entonces no hace falta releer nada.
                ContentIndex.Open(folderPath);
                if (ContentIndex.Loaded && ContentIndex.Consists.Count > 0)
                {
                    foreach (var (name, conPath, leadPath) in ContentIndex.Consists)
                        list.Add(new TrainItem
                        {
                            Name = name,
                            FilePath = conPath,
                            Locomotive = string.IsNullOrEmpty(leadPath) ? null : new TrainEngine { FilePath = leadPath }
                        });
                    return list;   // ya venía ordenado al guardarse
                }

                string dir = Path.Combine(folderPath, "TRAINS", "CONSISTS");
                if (!Directory.Exists(dir)) return list;
                var files = Directory.GetFiles(dir, "*.con");
                var items = new TrainItem[files.Length];

                Parallel.For(0, files.Length,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) },
                    i => { items[i] = Parse(files[i], folderPath); });

                foreach (var it in items) if (it != null) list.Add(it);
                // Mismo orden que daba Open Rails: OrderBy por nombre (estable).
                list = list.OrderBy(t => t.Name).ToList();
                // Se apunta para el índice: el próximo arranque se lo ahorra.
                var filas = new List<(string, string, string)>(list.Count);
                foreach (var t in list) filas.Add((t.Name, t.FilePath, t.Locomotive?.FilePath));
                ContentIndex.SetConsists(filas);
            }
            catch { }
            return list;
        }

        // ---------- un .con ----------
        static TrainItem Parse(string file, string folderPath)
        {
            try
            {
                string text = ReadText(file);
                if (string.IsNullOrEmpty(text)) return null;

                string id = null, name = null;
                var engines = new List<(string file, string folder)>();
                var sc = new Scanner(text);
                string block = null, prev = null;   // bloque actual y el que lo contiene
                bool afterTrainCfgOpen = false;

                while (sc.Next(out string tok, out bool open, out bool close))
                {
                    if (close) { sc.PopBlock(ref block, ref prev); afterTrainCfgOpen = false; continue; }
                    if (open)
                    {
                        sc.PushBlock(tok, ref block, ref prev);
                        afterTrainCfgOpen = block == "traincfg";
                        if (block == "name" && prev == "traincfg" && name == null) name = sc.ReadValue(ref block, ref prev);
                        else if (block == "enginedata")
                        {
                            string ef = sc.ReadToken(), fo = sc.ReadToken();
                            if (!string.IsNullOrEmpty(ef) && !string.IsNullOrEmpty(fo)) engines.Add((ef, fo));
                        }
                        continue;
                    }
                    // token suelto: en TrainCfg, el primero es el identificador del tren
                    if (afterTrainCfgOpen && id == null) { id = tok; afterTrainCfgOpen = false; }
                }

                // Con una comilla sin cerrar no hay nada fiable que leer. En cambio, un archivo
                // cortado a medias sí se aprovecha: Open Rails también lista esos trenes.
                if (sc.Malformed) return Reject(file, "comilla sin cerrar");

                // Cabeza del tren: el primer coche motor con cabina (o que falte del contenido).
                string lead = null;
                foreach (var e in engines)
                {
                    string path = Path.Combine(folderPath, "TRAINS", "TRAINSET", e.folder, e.file + ".eng");
                    if (!File.Exists(path) || HasCab(path)) { lead = path; break; }
                }
                if (lead == null) return Reject(file, "sin coche motor conducible");   // Open Rails tampoco lo lista

                string shown = !string.IsNullOrEmpty(name) ? name
                             : !string.IsNullOrEmpty(id) ? id
                             : Path.GetFileNameWithoutExtension(file);

                return new TrainItem
                {
                    FilePath = file,
                    Name = shown.Trim(),
                    Locomotive = new TrainEngine { FilePath = lead }
                };
            }
            catch { return null; }
        }

        // Diagnóstico: por qué se ha descartado un .con (solo para el banco de pruebas).
        public static bool Diagnose;
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Rejected =
            new(StringComparer.OrdinalIgnoreCase);
        static TrainItem Reject(string file, string why)
        {
            if (Diagnose) Rejected[file] = why;
            return null;
        }

        // ---------- cabina de un coche motor ----------
        // Un .eng es conducible si declara cabina, y muchos la declaran dentro de un Include ( ),
        // así que también se miran los archivos incluidos. El resultado se guarda porque el mismo
        // vehículo aparece en decenas de composiciones.
        static bool HasCab(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (ContentIndex.Cab.TryGetValue(path, out bool hit)) return hit;
            bool cab = false;
            try { cab = SearchCab(path, 0); } catch { }
            ContentIndex.Cab[path] = cab;
            return cab;
        }

        static bool SearchCab(string path, int depth)
        {
            if (depth > 4) return false;
            // La cabina se declara en el bloque Engine, al final del archivo, y los Include en el
            // Wagon, al principio: con leer la cabeza y la cola basta casi siempre. Solo si no
            // aparece nada se lee el archivo entero (es lo que costaba antes en todos).
            string text = ReadHeadTail(path, 16000, 32000);
            if (string.IsNullOrEmpty(text)) return false;
            if (HasToken(text, "cabview")) return true;
            // No aparece en los extremos: si el archivo es mayor, se mira entero (pocas veces).
            if (EsParcial(path, 16000 + 32000))
            {
                string completo = ReadText(path);
                if (!string.IsNullOrEmpty(completo))
                {
                    if (HasToken(completo, "cabview")) return true;
                    text = completo;
                }
            }

            // Los Include ( ) llevan rutas al estilo MSTS, relativas a la carpeta del archivo.
            string dir = Path.GetDirectoryName(path) ?? "";
            int i = 0;
            while ((i = IndexOfToken(text, "include", i)) >= 0)
            {
                int j = text.IndexOf('(', i);
                if (j < 0) break;
                var sc = new Scanner(text, j + 1);
                string rel = sc.ReadToken();
                i = j + 1;
                if (string.IsNullOrEmpty(rel)) continue;
                string inc;
                try { inc = Path.GetFullPath(Path.Combine(dir, rel.Replace("/", "\\"))); }
                catch { continue; }
                if (!Native.IsLocalFile(inc)) continue;   // nunca rutas de red
                if (File.Exists(inc) && SearchCab(inc, depth + 1)) return true;
            }
            return false;
        }

        // La palabra suelta seguida de "(" (así no cuentan ORTSCabViewFile ni comentarios sueltos).
        static bool HasToken(string text, string word) => IndexOfToken(text, word, 0) >= 0;

        static int IndexOfToken(string text, string word, int from)
        {
            int i = from;
            while ((i = text.IndexOf(word, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                bool wordStart = i == 0 || !char.IsLetter(text[i - 1]);
                int j = i + word.Length;
                while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                if (wordStart && j < text.Length && text[j] == '(') return i;
                i += word.Length;
            }
            return -1;
        }

        // ---------- nombre de una motriz ----------
        static readonly Dictionary<string, string> _engNames = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Nombre con el que se presenta un .eng: el del bloque Engine y, si no lo lleva, el del archivo.</summary>
        public static string EngineDisplayName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            lock (_engNames) if (_engNames.TryGetValue(path, out var hit)) return hit;
            string name = Path.GetFileNameWithoutExtension(path);
            try
            {
                string text = File.Exists(path) ? ReadText(path) : null;
                if (!string.IsNullOrEmpty(text))
                {
                    string engine = null, wagon = null;
                    var sc = new Scanner(text);
                    string block = null, prev = null;
                    while (sc.Next(out string tok, out bool open, out bool close))
                    {
                        if (close) { sc.PopBlock(ref block, ref prev); continue; }
                        if (!open) continue;
                        sc.PushBlock(tok, ref block, ref prev);
                        if (block != "name") continue;
                        if (prev == "engine" && engine == null) engine = sc.ReadValue(ref block, ref prev);
                        else if (prev == "wagon" && wagon == null) wagon = sc.ReadValue(ref block, ref prev);
                    }
                    if (!string.IsNullOrEmpty(engine)) name = engine.Trim();
                    else if (!string.IsNullOrEmpty(wagon)) name = wagon.Trim();
                }
            }
            catch { }
            lock (_engNames) _engNames[path] = name;
            return name;
        }

        /// <summary>Vacía la caché de nombres (el editor puede haber cambiado archivos).</summary>
        public static void ClearCache() { lock (_engNames) _engNames.Clear(); ContentIndex.Cab.Clear(); }

        // ---------- lector de bloques al estilo MSTS ----------
        // Reconoce  Bloque ( valor )  con cadenas entre comillas (y comillas escapadas).
        sealed class Scanner
        {
            readonly string _s;
            int _i;
            int _depth;                       // paréntesis abiertos
            readonly List<string> _stack = new();

            public int Depth => _depth;
            public bool Malformed { get; private set; }   // comillas o paréntesis sin cerrar

            public Scanner(string s, int start = 0) { _s = s ?? ""; _i = Math.Max(0, Math.Min(start, _s.Length)); }

            public void PushBlock(string tok, ref string block, ref string prev)
            {
                _stack.Add(tok);
                block = tok;
                prev = _stack.Count >= 2 ? _stack[_stack.Count - 2] : null;
            }

            public void PopBlock(ref string block, ref string prev)
            {
                if (_stack.Count > 0) _stack.RemoveAt(_stack.Count - 1);
                block = _stack.Count > 0 ? _stack[_stack.Count - 1] : null;
                prev = _stack.Count >= 2 ? _stack[_stack.Count - 2] : null;
            }

            // Siguiente elemento: un token suelto, la apertura de un bloque (tok = su nombre en
            // minúsculas) o el cierre del bloque actual.
            public bool Next(out string tok, out bool open, out bool close)
            {
                tok = null; open = false; close = false;
                SkipSpace();
                if (_i >= _s.Length) return false;
                char c = _s[_i];
                if (c == ')') { _i++; _depth--; close = true; return true; }
                if (c == '(') { _i++; _depth++; tok = ""; open = true; return true; }   // bloque sin nombre
                if (c == '"') { tok = ReadQuoted(); return true; }
                int start = _i;
                while (_i < _s.Length && !char.IsWhiteSpace(_s[_i]) && _s[_i] != '(' && _s[_i] != ')' && _s[_i] != '"') _i++;
                string raw = _s.Substring(start, _i - start);
                int save = _i;
                SkipSpace();
                if (_i < _s.Length && _s[_i] == '(') { _i++; _depth++; tok = raw.ToLowerInvariant(); open = true; return true; }
                _i = save;
                tok = raw;
                return true;
            }

            // Valor de un bloque recién abierto; consume también su cierre.
            public string ReadValue(ref string block, ref string prev)
            {
                SkipSpace();
                string v;
                if (_i < _s.Length && _s[_i] == '"') v = ReadQuoted();
                else
                {
                    int start = _i;
                    while (_i < _s.Length && _s[_i] != ')' && _s[_i] != '\r' && _s[_i] != '\n') _i++;
                    v = _s.Substring(start, _i - start).Trim();
                }
                while (_i < _s.Length && _s[_i] != ')') _i++;
                if (_i < _s.Length) { _i++; _depth--; }
                PopBlock(ref block, ref prev);
                return v;
            }

            // Un solo token dentro del bloque abierto (sin consumir su cierre).
            public string ReadToken()
            {
                SkipSpace();
                if (_i >= _s.Length || _s[_i] == ')') return null;
                if (_s[_i] == '"') return ReadQuoted();
                int start = _i;
                while (_i < _s.Length && !char.IsWhiteSpace(_s[_i]) && _s[_i] != '(' && _s[_i] != ')') _i++;
                return _s.Substring(start, _i - start);
            }

            void SkipSpace() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

            string ReadQuoted()
            {
                _i++;   // comilla de apertura
                bool closed = false;
                var sb = new StringBuilder();
                while (_i < _s.Length)
                {
                    char c = _s[_i++];
                    if (c == '\\' && _i < _s.Length) { sb.Append(_s[_i++]); continue; }   // comilla escapada
                    if (c == '"') { closed = true; break; }
                    sb.Append(c);
                }
                if (!closed) Malformed = true;
                return sb.ToString();
            }
        }

        // ¿El archivo es más grande que lo que se lee por partes?
        static bool EsParcial(string file, int leidos)
        {
            try { return new FileInfo(file).Length > leidos; } catch { return false; }
        }

        // Principio + final del archivo (los datos que interesan están en los dos extremos).
        public static string ReadHeadTail(string file, int head, int tail)
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long len = fs.Length;
                if (len <= head + tail) return ReadText(file);
                var buf = new byte[head + tail];
                int n1 = fs.Read(buf, 0, head);
                fs.Seek(-tail, SeekOrigin.End);
                int n2 = fs.Read(buf, head, tail);
                if (n1 + n2 < buf.Length) Array.Resize(ref buf, Math.Max(0, n1 + n2));
                return Decode(buf);
            }
            catch { return ""; }
        }

        // Los archivos de MSTS son UTF-16 (con o sin BOM) o UTF-8.
        static string ReadText(string file, int maxBytes = int.MaxValue)
        {
            try
            {
                byte[] buf;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int want = (int)Math.Min(maxBytes, fs.Length);
                    buf = new byte[want];
                    int read = fs.Read(buf, 0, want);
                    if (read < want) Array.Resize(ref buf, Math.Max(0, read));
                }
                return Decode(buf);
            }
            catch { return null; }
        }

        static string Decode(byte[] buf)
        {
            if (buf.Length >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) return Encoding.Unicode.GetString(buf);
            if (buf.Length >= 2 && buf[0] == 0xFE && buf[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(buf);   // UTF-16 BE
            int zeros = 0, n = Math.Min(buf.Length, 200);
            for (int i = 0; i < n; i++) if (buf[i] == 0) zeros++;
            return zeros > n / 4 ? Encoding.Unicode.GetString(buf) : Encoding.UTF8.GetString(buf);
        }
    }
}
