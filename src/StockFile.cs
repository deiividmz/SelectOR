// Edición de datos sueltos dentro de los archivos de material rodante (.eng / .wag) de MSTS / Open Rails.
// De momento: PassengerCapacity (plazas de viajeros), que es lo que usa SelectOR para saber si un tren
// lleva viajeros. Se conserva la codificación del archivo (no se dejan copias .bak).

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SelectOR
{
    public static class StockFile
    {
        const string CapPattern = @"[ \t]*PassengerCapacity\s*\(\s*[^)]*\)[ \t]*";

        /// <summary>Plazas declaradas en el archivo, o null si no tiene PassengerCapacity.</summary>
        public static double? GetCapacity(string path)
        {
            string t = ConsistDoc.ReadText(path);
            if (string.IsNullOrEmpty(t)) return null;
            var m = Regex.Match(t, @"PassengerCapacity\s*\(\s*([\d.,]+)", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string v = m.Groups[1].Value.Replace(",", ".");
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : (double?)null;
        }

        /// <summary>Fija las plazas (o las quita si value es null). Devuelve null si OK, o el error.</summary>
        public static string SetCapacity(string path, double? value)
        {
            try
            {
                string t = ConsistDoc.ReadText(path);
                if (string.IsNullOrEmpty(t)) return I18n.T("No se pudo leer el archivo del vehículo.");
                // El BOM lo pone la codificación al escribir: si viene en el texto, se quita (no duplicarlo).
                if (t[0] == '﻿') t = t.Substring(1);

                string nl = t.Contains("\r\n") ? "\r\n" : "\n";
                var m = Regex.Match(t, CapPattern, RegexOptions.IgnoreCase);

                if (value == null)
                {
                    if (!m.Success) return null;   // ya no las tenía
                    int start = m.Index, len = m.Length;
                    // se lleva también el salto de línea que la acompaña
                    if (start + len < t.Length && t.Substring(start + len).StartsWith(nl, StringComparison.Ordinal)) len += nl.Length;
                    t = t.Remove(start, len);
                }
                else
                {
                    string val = Math.Max(0, Math.Round(value.Value)).ToString("0", CultureInfo.InvariantCulture);
                    string line = "PassengerCapacity ( " + val + " )";
                    if (m.Success)
                    {
                        // Ya tenía la línea: se conserva su sangrado y solo cambia el número.
                        string indent = m.Value.Substring(0, m.Value.Length - m.Value.TrimStart().Length);
                        t = t.Remove(m.Index, m.Length).Insert(m.Index, indent + line);
                    }
                    else
                    {
                        // Se inserta dentro del bloque Wagon ( … ), justo tras su primera línea.
                        var w = Regex.Match(t, @"\bWagon\s*\(", RegexOptions.IgnoreCase);
                        if (!w.Success) return I18n.T("El archivo no tiene un bloque Wagon ( … ) donde poner las plazas.");
                        int eol = t.IndexOf(nl, w.Index, StringComparison.Ordinal);
                        if (eol < 0) return I18n.T("Formato de archivo no reconocido.");
                        t = t.Insert(eol + nl.Length, "\t" + line + nl);
                    }
                }

                bool utf16 = IsUtf16(path);
                File.WriteAllText(path, t, utf16 ? (Encoding)new UnicodeEncoding(false, true) : new UTF8Encoding(false));
                return null;
            }
            catch (Exception e) { return e.Message; }
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
    }
}
