// Megafonías: almacén de los clips (bucket privado 'megafonias' de Supabase) y caché local.
//  · Los audios los sube el gestor de la empresa y los descarga el maquinista UNA vez: el caché
//    vive en %AppData%\Open Rails\SelectOR\megafonias y se revalida con el updated_at de la fila.
//    Sin esto, cada conducción gastaría descargas del plan (5 GB/mes) para oír lo mismo.
//  · El bucket es privado: se lee con el token del usuario, y las políticas del servidor solo
//    dejan pasar a socios de la empresa con la megafonía habilitada.

using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace SelectOR
{
    public static class Megafonia
    {
        public const string Bucket = "megafonias";

        public static string CacheDir
        {
            get
            {
                string d = Path.Combine(AppDataTidy.CacheRoot, "megafonias");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        // Nombre válido para un objeto del bucket: sin acentos ni espacios, y con un sufijo corto
        // para que dos estaciones que se simplifiquen igual no se pisen.
        public static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            var norm = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(norm.Length);
            foreach (char c in norm)
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (c == '.' || c == '-' || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            string baseName = sb.ToString().Trim('_');
            if (baseName.Length > 48) baseName = baseName.Substring(0, 48);
            int h = 17;
            foreach (char c in s) h = h * 31 + c;
            return (baseName.Length == 0 ? "x" : baseName) + "-" + (h & 0xFFFF).ToString("x4");
        }

        // <company>/<route>/<kind>/<station>.<ext>   (la política deduce la empresa del 1.er segmento)
        public static string ObjectPath(string company, string route, string kind, string station, string ext)
            => $"{company}/{Safe(route)}/{kind}/{Safe(station)}{ext}";

        // Tope de lo que se descarga de un clip (el bucket admite 2 MB): nada de respuestas enormes.
        static HttpClient NewHttp(int seconds) => new HttpClient { Timeout = TimeSpan.FromSeconds(seconds), MaxResponseContentBufferSize = 4 << 20 };

        // SEGURIDAD: la ruta de un clip viene de la base de datos (la escribe el gestor de otra
        // empresa). Solo se acepta «<id-empresa>/…/<nombre>.mp3|.wav», sin «..» ni caracteres que
        // cambien la petición: con «../../rest/v1/…» se podía mandar al servidor una petición con
        // el token de quien reproduce o borra el audio.
        public static bool ValidObjPath(string p)
        {
            if (string.IsNullOrWhiteSpace(p) || p.Length > 400) return false;
            if (p.IndexOfAny(new[] { '\\', '?', '#', '%', '"', ':', '\0' }) >= 0) return false;
            var segs = p.Split('/');
            if (segs.Length < 3 || !Guid.TryParse(segs[0], out _)) return false;
            foreach (var s in segs) if (s.Length == 0 || s == "." || s == "..") return false;
            string ext = Path.GetExtension(p).ToLowerInvariant();
            return ext == ".mp3" || ext == ".wav";
        }

        // ¿Es de verdad un MP3 o un WAV? (cabecera del archivo, no el nombre)
        public static bool LooksLikeAudio(byte[] d, string ext)
        {
            if (d == null || d.Length < 12) return false;
            if (ext == ".wav") return d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'A' && d[10] == 'V' && d[11] == 'E';
            if (ext == ".mp3") return (d[0] == 'I' && d[1] == 'D' && d[2] == '3') || (d[0] == 0xFF && (d[1] & 0xE0) == 0xE0);
            return false;
        }

        static HttpRequestMessage Req(HttpMethod m, string path)
        {
            var r = new HttpRequestMessage(m, Supa.BuiltInUrl.TrimEnd('/') + path);
            r.Headers.TryAddWithoutValidation("apikey", Supa.BuiltInAnonKey);
            r.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", string.IsNullOrEmpty(Supa.AccessToken) ? Supa.BuiltInAnonKey : Supa.AccessToken);
            return r;
        }

        static string Escape(string objPath)
        {
            var parts = objPath.Split('/');
            for (int i = 0; i < parts.Length; i++) parts[i] = Uri.EscapeDataString(parts[i]);
            return string.Join("/", parts);
        }

        // Sube (o sustituye) un clip. Devuelve null si va bien o el mensaje de error.
        public static async Task<string> UploadAsync(string objPath, byte[] data, string mime)
        {
            if (!ValidObjPath(objPath)) return I18n.T("Ruta de audio no válida.");
            try
            {
                await Supa.EnsureFreshTokenAsync();
                using var http = NewHttp(120);
                using var req = Req(HttpMethod.Post, $"/storage/v1/object/{Bucket}/{Escape(objPath)}");
                req.Headers.TryAddWithoutValidation("x-upsert", "true");
                req.Content = new ByteArrayContent(data);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
                using var resp = await http.SendAsync(req);
                if (resp.IsSuccessStatusCode) { Forget(objPath); return null; }
                return await resp.Content.ReadAsStringAsync();
            }
            catch (Exception e) { return e.Message; }
        }

        public static async Task<string> DeleteAsync(string objPath)
        {
            if (!ValidObjPath(objPath)) return I18n.T("Ruta de audio no válida.");
            try
            {
                await Supa.EnsureFreshTokenAsync();
                using var http = NewHttp(60);
                using var req = Req(HttpMethod.Delete, $"/storage/v1/object/{Bucket}/{Escape(objPath)}");
                using var resp = await http.SendAsync(req);
                Forget(objPath);
                return resp.IsSuccessStatusCode ? null : await resp.Content.ReadAsStringAsync();
            }
            catch (Exception e) { return e.Message; }
        }

        // En el caché solo hay .mp3 o .wav (la extensión no se toma tal cual de la base de datos).
        static string LocalFile(string objPath) =>
            Path.Combine(CacheDir, Safe(objPath) + (Path.GetExtension(objPath).Equals(".mp3", StringComparison.OrdinalIgnoreCase) ? ".mp3" : ".wav"));
        static string StampFile(string objPath) => LocalFile(objPath) + ".tag";

        static void Forget(string objPath)
        {
            try { File.Delete(LocalFile(objPath)); } catch { }
            try { File.Delete(StampFile(objPath)); } catch { }
        }

        // Deja el clip en el caché y devuelve su ruta local. `stamp` es el updated_at de la fila:
        // si cambia, se vuelve a bajar; si coincide, no se gasta ni una petición.
        public static async Task<(string file, string err)> EnsureLocalAsync(string objPath, string stamp)
        {
            if (string.IsNullOrWhiteSpace(objPath)) return (null, "sin audio");
            if (!ValidObjPath(objPath)) return (null, "ruta de audio no válida");
            string local = LocalFile(objPath), tagf = StampFile(objPath);
            try
            {
                if (File.Exists(local) && (string.IsNullOrEmpty(stamp) || (File.Exists(tagf) && File.ReadAllText(tagf) == stamp)))
                {
                    // Marca de «en uso»: los audios que pasan 45 días sin sonar se borran al arrancar.
                    try { File.SetLastWriteTimeUtc(local, DateTime.UtcNow); } catch { }
                    return (local, null);
                }
            }
            catch { }
            try
            {
                await Supa.EnsureFreshTokenAsync();
                using var http = NewHttp(60);
                using var req = Req(HttpMethod.Get, $"/storage/v1/object/{Bucket}/{Escape(objPath)}");
                using var resp = await http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return (null, await resp.Content.ReadAsStringAsync());
                var data = await resp.Content.ReadAsByteArrayAsync();
                if (!LooksLikeAudio(data, Path.GetExtension(local))) return (null, "no es un audio válido");
                File.WriteAllBytes(local, data);
                if (!string.IsNullOrEmpty(stamp)) File.WriteAllText(tagf, stamp);
                return (local, null);
            }
            catch (Exception e) { return (null, e.Message); }
        }

        public static string MimeOf(string file)
        {
            string ext = Path.GetExtension(file ?? "").ToLowerInvariant();
            return ext == ".mp3" ? "audio/mpeg" : "audio/wav";
        }
    }
}
