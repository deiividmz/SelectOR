// Lectura y escritura de archivos de composición (.con) de MSTS / Open Rails.
// Se conserva la estructura del formato original (SIMISA… + Train ( TrainCfg ( … ) )) y la
// codificación del archivo (los .con suelen ser UTF-16LE con BOM).
//
// Solo se tocan los datos que edita el usuario: nombre, orden de los coches, coche invertido
// (Flip) y altas/bajas. Serial, MaxVelocity y Durability se conservan tal cual venían.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SelectOR
{
    public sealed class ConsistCar
    {
        public string Name;      // nombre del .eng/.wag (sin extensión)
        public string Folder;    // carpeta dentro de TRAINS\TRAINSET
        public bool IsEngine;    // Engine ( … ) vs Wagon ( … )
        public bool Flip;        // coche colocado del revés
        public ConsistCar Clone() => new ConsistCar { Name = Name, Folder = Folder, IsEngine = IsEngine, Flip = Flip };
    }

    public sealed class ConsistDoc
    {
        public string Path;
        public string Id = "";                       // identificador tras TrainCfg (
        public string DisplayName = "";              // Name ( "…" )
        public string Serial = "1";
        public string MaxVelocity = "27.77778 0.53000";
        public string Durability = "1.00000";
        public List<ConsistCar> Cars = new();
        public bool Utf16 = true;                    // codificación del archivo original

        const string HeaderLine = "SIMISA@@@@@@@@@@JINX0D0t______";

        // ---- Lectura del texto respetando la codificación (igual criterio que el resto de SelectOR) ----
        public static string ReadText(string file)
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes);
                if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes);   // UTF-16 BE
                int zeros = 0, n = Math.Min(bytes.Length, 200);
                for (int i = 0; i < n; i++) if (bytes[i] == 0) zeros++;
                if (zeros > n / 4) return Encoding.Unicode.GetString(bytes);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }

        static bool IsUtf16(string file)
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF))) return true;
                int zeros = 0, n = Math.Min(bytes.Length, 200);
                for (int i = 0; i < n; i++) if (bytes[i] == 0) zeros++;
                return zeros > n / 4;
            }
            catch { return true; }
        }

        static string Val(string text, string token, string def = "")
        {
            var m = Regex.Match(text, token + @"\s*\(\s*(?:""([^""]*)""|([^)]*?))\s*\)", RegexOptions.IgnoreCase);
            if (!m.Success) return def;
            string v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            return v.Trim();
        }

        public static ConsistDoc Load(string path)
        {
            string t = ReadText(path);
            if (string.IsNullOrEmpty(t)) return null;
            var doc = new ConsistDoc { Path = path, Utf16 = IsUtf16(path) };

            var mId = Regex.Match(t, @"TrainCfg\s*\(\s*(?:""([^""]*)""|([^\s()]+))", RegexOptions.IgnoreCase);
            if (mId.Success) doc.Id = (mId.Groups[1].Success ? mId.Groups[1].Value : mId.Groups[2].Value).Trim();
            doc.DisplayName = Val(t, "Name", doc.Id);
            doc.Serial = Val(t, "Serial", "1");
            doc.MaxVelocity = Val(t, "MaxVelocity", "27.77778 0.53000");
            doc.Durability = Val(t, "Durability", "1.00000");

            // Coches en orden: cada bloque Engine ( … ) / Wagon ( … ) del TrainCfg.
            foreach (Match m in Regex.Matches(t, @"\b(Engine|Wagon)\s*\(", RegexOptions.IgnoreCase))
            {
                bool isEngine = m.Groups[1].Value.Equals("Engine", StringComparison.OrdinalIgnoreCase);
                string block = Block(t, m.Index + m.Length - 1);
                if (block == null) continue;
                var md = Regex.Match(block, @"(?:Engine|Wagon)Data\s*\(\s*(?:""([^""]*)""|([^\s()""]+))\s+(?:""([^""]*)""|([^\s()""]+))\s*\)", RegexOptions.IgnoreCase);
                if (!md.Success) continue;   // p. ej. el propio bloque Engine de un .eng, no del consist
                doc.Cars.Add(new ConsistCar
                {
                    Name = (md.Groups[1].Success ? md.Groups[1].Value : md.Groups[2].Value).Trim(),
                    Folder = (md.Groups[3].Success ? md.Groups[3].Value : md.Groups[4].Value).Trim(),
                    IsEngine = isEngine,
                    Flip = Regex.IsMatch(block, @"\bFlip\s*\(", RegexOptions.IgnoreCase)
                });
            }
            return doc;
        }

        // Devuelve el contenido entre el paréntesis que abre en openIdx y su cierre correspondiente.
        static string Block(string t, int openIdx)
        {
            if (openIdx < 0 || openIdx >= t.Length || t[openIdx] != '(') return null;
            int depth = 0;
            for (int i = openIdx; i < t.Length; i++)
            {
                if (t[i] == '(') depth++;
                else if (t[i] == ')')
                {
                    depth--;
                    if (depth == 0) return t.Substring(openIdx + 1, i - openIdx - 1);
                }
            }
            return null;
        }

        static string Q(string s)   // entrecomilla si hace falta (vacío, espacios o paréntesis)
        {
            s = (s ?? "").Trim();
            bool comillas = s.Length == 0;
            foreach (char ch in s) if (char.IsWhiteSpace(ch) || ch == '(' || ch == ')') { comillas = true; break; }
            return comillas ? "\"" + s.Replace("\"", "") + "\"" : s;
        }

        public string BuildText()
        {
            var sb = new StringBuilder();
            sb.Append(HeaderLine).Append("\r\n\r\n");
            sb.Append("Train (\r\n");
            sb.Append("\tTrainCfg ( ").Append(Q(string.IsNullOrWhiteSpace(Id) ? DisplayName : Id)).Append("\r\n");
            sb.Append("\t\tName ( \"").Append((DisplayName ?? "").Replace("\"", "")).Append("\" )\r\n");
            sb.Append("\t\tSerial ( ").Append(string.IsNullOrWhiteSpace(Serial) ? "1" : Serial).Append(" )\r\n");
            sb.Append("\t\tMaxVelocity ( ").Append(string.IsNullOrWhiteSpace(MaxVelocity) ? "27.77778 0.53000" : MaxVelocity).Append(" )\r\n");
            sb.Append("\t\tNextWagonUID ( ").Append((Cars.Count + 1).ToString(CultureInfo.InvariantCulture)).Append(" )\r\n");
            sb.Append("\t\tDurability ( ").Append(string.IsNullOrWhiteSpace(Durability) ? "1.00000" : Durability).Append(" )\r\n");
            for (int i = 0; i < Cars.Count; i++)
            {
                var c = Cars[i];
                string uid = (i + 1).ToString(CultureInfo.InvariantCulture);
                if (c.IsEngine)
                {
                    sb.Append("\t\tEngine (\r\n");
                    sb.Append("\t\t\tUiD ( ").Append(uid).Append(" )\r\n");
                    sb.Append("\t\t\tEngineData ( ").Append(Q(c.Name)).Append(' ').Append(Q(c.Folder)).Append(" )\r\n");
                }
                else
                {
                    sb.Append("\t\tWagon (\r\n");
                    sb.Append("\t\t\tWagonData ( ").Append(Q(c.Name)).Append(' ').Append(Q(c.Folder)).Append(" )\r\n");
                    sb.Append("\t\t\tUiD ( ").Append(uid).Append(" )\r\n");
                }
                if (c.Flip) sb.Append("\t\t\tFlip ( )\r\n");
                sb.Append("\t\t)\r\n");
            }
            sb.Append("\t)\r\n");
            sb.Append(")\r\n");
            return sb.ToString();
        }

        /// <summary>Guarda el .con (sin copias de seguridad). Devuelve null si OK o el error.</summary>
        public string Save(string path = null)
        {
            path = path ?? Path;
            try
            {
                var enc = Utf16 ? (Encoding)new UnicodeEncoding(false, true) : new UTF8Encoding(false);
                File.WriteAllText(path, BuildText(), enc);
                Path = path;
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        public ConsistDoc Clone()
        {
            var d = new ConsistDoc { Path = Path, Id = Id, DisplayName = DisplayName, Serial = Serial, MaxVelocity = MaxVelocity, Durability = Durability, Utf16 = Utf16 };
            foreach (var c in Cars) d.Cars.Add(c.Clone());
            return d;
        }

        public bool SameAs(ConsistDoc o)
        {
            if (o == null || o.Cars.Count != Cars.Count) return false;
            if (!string.Equals(DisplayName ?? "", o.DisplayName ?? "", StringComparison.Ordinal)) return false;
            for (int i = 0; i < Cars.Count; i++)
            {
                var a = Cars[i]; var b = o.Cars[i];
                if (!string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase) ||
                    a.IsEngine != b.IsEngine || a.Flip != b.Flip) return false;
            }
            return true;
        }
    }
}
