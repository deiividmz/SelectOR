// Actualizaciones de SelectOR.
//  · La última versión publicada está en la tabla app_releases de Supabase (lectura pública).
//  · El paquete es un .zip con los archivos propios de SelectOR (no toca los de Open Rails),
//    alojado en el bucket público "updates" de Supabase Storage (o en cualquier URL https).
//  · Instalación sin script externo: Windows permite RENOMBRAR un .exe/.dll en uso, así que los
//    archivos actuales pasan a *.old, se copian los nuevos y se reinicia la app. Al arrancar se
//    borran los *.old que queden.
//  · El superadmin publica desde Ajustes → Actualizaciones (empaqueta los archivos que están
//    ejecutándose, los sube y registra la versión).
//  · SEGURIDAD: cada paquete va FIRMADO (ECDSA P-256) con una clave privada que solo existe en el
//    PC del superadmin (cifrada con DPAPI en %AppData%\Open Rails\SelectOR\firma-actualizaciones.key).
//    La app lleva dentro la clave PÚBLICA y solo instala paquetes con firma válida, alojados en el
//    almacén propio de Supabase y con su huella SHA-256. Aunque alguien consiguiera la contraseña
//    del superadmin, no podría repartir un ejecutable a los demás.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SelectOR
{
    public sealed class ReleaseInfo
    {
        public string Version, Url, Sha256, Notes, Signature;
        public DateTime PublishedAt;
    }

    public static class Updater
    {
        // Archivos propios de SelectOR que forman el paquete de actualización.
        public static readonly string[] PackageFiles =
            { "SelectOR.exe", "SelectOR.dll", "SelectOR.deps.json", "SelectOR.runtimeconfig.json", "shape.mgfx" };

        const string Bucket = "updates";

        // Clave PÚBLICA de firma de actualizaciones (SubjectPublicKeyInfo, Base64). La privada NO va
        // en la app: está solo en el PC del superadmin.
        const string UpdatePublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE8sfTH4Tc35KaRMr9DOmXZIe1c2gQf8/f+XvLt5MnJhpoB/AGhLE3DTURnYYsmiVmGKXxSg3RBLnN30kYBDmw/g==";

        // Único origen aceptado para descargar paquetes.
        static string AllowedPrefix => Supa.BuiltInUrl.TrimEnd('/') + "/storage/v1/object/public/" + Bucket + "/";

        static string SigningKeyPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails", "SelectOR", "firma-actualizaciones.key");

        public static bool HasSigningKey => File.Exists(SigningKeyPath);

        // Crea el par de claves (una sola vez, en el PC del superadmin). Guarda la privada cifrada con
        // DPAPI y devuelve la pública, que hay que poner en UpdatePublicKey.
        public static string CreateSigningKey()
        {
            if (File.Exists(SigningKeyPath)) throw new InvalidOperationException("Ya existe una clave de firma: no se sobrescribe.");
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var enc = Native.ProtectBytes(ec.ExportPkcs8PrivateKey()) ?? throw new InvalidOperationException("No se pudo cifrar la clave.");
            Directory.CreateDirectory(Path.GetDirectoryName(SigningKeyPath));
            File.WriteAllBytes(SigningKeyPath, enc);
            return Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
        }

        static string SignPackage(byte[] data)
        {
            if (!File.Exists(SigningKeyPath)) return null;
            var pkcs8 = Native.UnprotectBytes(File.ReadAllBytes(SigningKeyPath));
            if (pkcs8 == null) return null;
            try
            {
                using var ec = ECDsa.Create();
                ec.ImportPkcs8PrivateKey(pkcs8, out _);
                var sig = ec.SignData(data, HashAlgorithmName.SHA256);
                // La firma tiene que casar con la clave pública que llevan las copias instaladas.
                return VerifyPackage(data, Convert.ToBase64String(sig)) ? Convert.ToBase64String(sig) : null;
            }
            finally { Array.Clear(pkcs8, 0, pkcs8.Length); }
        }

        public static bool VerifyPackage(byte[] data, string signatureB64)
        {
            if (data == null || string.IsNullOrWhiteSpace(signatureB64)) return false;
            try
            {
                using var ec = ECDsa.Create();
                ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdatePublicKey), out _);
                return ec.VerifyData(data, Convert.FromBase64String(signatureB64.Trim()), HashAlgorithmName.SHA256);
            }
            catch { return false; }
        }

        public static Version CurrentVersion =>
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

        public static string CurrentVersionText => Short(CurrentVersion);

        public static string Short(Version v) => v == null ? "?" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

        public static bool TryParse(string s, out Version v)
        {
            v = null;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().TrimStart('v', 'V');
            if (!Version.TryParse(s, out var p)) return false;
            v = new Version(p.Major, p.Minor, Math.Max(0, p.Build), Math.Max(0, p.Revision));
            return true;
        }

        static string AppDir => AppContext.BaseDirectory;

        static HttpClient NewHttp(int seconds = 30) => new HttpClient { Timeout = TimeSpan.FromSeconds(seconds) };

        static HttpRequestMessage SupaReq(HttpMethod m, string path, string token = null)
        {
            var r = new HttpRequestMessage(m, Supa.BuiltInUrl.TrimEnd('/') + path);
            r.Headers.TryAddWithoutValidation("apikey", Supa.BuiltInAnonKey);
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", string.IsNullOrEmpty(token) ? Supa.BuiltInAnonKey : token);
            return r;
        }

        // Versiones publicadas (las últimas 50), de la MÁS ALTA a la más baja por número de versión
        // (no por fecha: si alguna vez se republica una versión antigua, no «gana» a la más nueva).
        static async Task<List<(Version v, ReleaseInfo r)>> GetReleasesAsync()
        {
            var list = new List<(Version, ReleaseInfo)>();
            if (!Supa.HasBuiltIn) return list;
            try
            {
                using var http = NewHttp(15);
                using var req = SupaReq(HttpMethod.Get, "/rest/v1/app_releases?select=version,url,sha256,notes,signature,created_at&order=created_at.desc&limit=50");
                using var resp = await http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return list;
                using var d = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (d.RootElement.ValueKind != JsonValueKind.Array) return list;
                var seen = new HashSet<Version>();
                foreach (var r in d.RootElement.EnumerateArray())
                {
                    string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    var info = new ReleaseInfo { Version = S("version"), Url = S("url"), Sha256 = S("sha256"), Notes = S("notes") ?? "", Signature = S("signature") };
                    // Sin firma, sin huella o fuera del almacén propio: no es una versión instalable.
                    if (string.IsNullOrWhiteSpace(info.Signature) || string.IsNullOrWhiteSpace(info.Sha256)
                        || info.Url == null || !info.Url.StartsWith(AllowedPrefix, StringComparison.Ordinal)) continue;
                    if (DateTime.TryParse(S("created_at"), out var dt)) info.PublishedAt = dt;
                    if (!TryParse(info.Version, out var ver) || string.IsNullOrWhiteSpace(info.Url)) continue;
                    if (!seen.Add(ver)) continue;   // misma versión publicada dos veces → la más reciente
                    list.Add((ver, info));
                }
                list.Sort((a, b) => b.Item1.CompareTo(a.Item1));
            }
            catch { }
            return list;
        }

        // Versión publicada más alta (null si no hay o no se pudo consultar).
        public static async Task<ReleaseInfo> GetLatestAsync()
        {
            var all = await GetReleasesAsync();
            return all.Count > 0 ? all[0].r : null;
        }

        // ¿Hay una versión más nueva que la que se está ejecutando? Devuelve SIEMPRE la más nueva (el paquete
        // es completo: se salta las intermedias) y, si había varias pendientes, junta sus novedades.
        public static async Task<ReleaseInfo> CheckAsync()
        {
            var all = await GetReleasesAsync();
            var cur = CurrentVersion;
            var pending = all.FindAll(x => x.v > cur);
            if (pending.Count == 0) return null;
            var newest = pending[0].r;
            if (pending.Count == 1) return newest;
            var sb = new StringBuilder();
            foreach (var (v, r) in pending)
            {
                sb.AppendLine("▸ " + I18n.T("Versión") + " " + Short(v));
                sb.AppendLine(string.IsNullOrWhiteSpace(r.Notes) ? I18n.T("Mejoras y correcciones.") : r.Notes.Trim());
                sb.AppendLine();
            }
            return new ReleaseInfo { Version = newest.Version, Url = newest.Url, Sha256 = newest.Sha256, Signature = newest.Signature, Notes = sb.ToString().TrimEnd(), PublishedAt = newest.PublishedAt };
        }

        // Borra los restos de una actualización anterior (*.old) al arrancar.
        public static void CleanupOld()
        {
            foreach (var f in PackageFiles)
            {
                try { var o = Path.Combine(AppDir, f + ".old"); if (File.Exists(o)) File.Delete(o); } catch { }
            }
        }

        // Descarga, verifica, instala y reinicia. Devuelve null si va bien (la app se cerrará),
        // o el mensaje de error (sin haber tocado nada, o tras deshacer los cambios).
        public static async Task<string> DownloadAndInstallAsync(ReleaseInfo r, IProgress<double> progress)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.Url)) return I18n.T("No hay enlace de descarga.");
            if (!r.Url.StartsWith(AllowedPrefix, StringComparison.Ordinal)) return I18n.T("El paquete no viene del almacén de SelectOR. No se ha instalado nada.");
            if (string.IsNullOrWhiteSpace(r.Sha256) || string.IsNullOrWhiteSpace(r.Signature))
                return I18n.T("Esta versión no está firmada. No se ha instalado nada.");

            string tmp = Path.Combine(Path.GetTempPath(), "SelectOR_update_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            string zipPath = Path.Combine(tmp, "update.zip");
            try
            {
                // 1) Descarga con progreso
                using (var http = NewHttp(300))
                using (var resp = await http.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!resp.IsSuccessStatusCode) return I18n.T("No se pudo descargar la actualización: ") + (int)resp.StatusCode;
                    long total = resp.Content.Headers.ContentLength ?? -1;
                    if (total > 100L * 1024 * 1024) return I18n.T("El paquete es demasiado grande. No se ha instalado nada.");
                    using var src = await resp.Content.ReadAsStreamAsync();
                    using var dst = File.Create(zipPath);
                    var buf = new byte[81920]; long got = 0; int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n);
                        got += n;
                        if (got > 100L * 1024 * 1024) return I18n.T("El paquete es demasiado grande. No se ha instalado nada.");
                        if (total > 0) progress?.Report(Math.Min(1.0, got / (double)total));
                    }
                }
                progress?.Report(1.0);

                // 2) Integridad y AUTENTICIDAD: huella SHA-256 y firma del superadmin.
                string sha = Sha256File(zipPath);
                if (!sha.Equals(r.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    return I18n.T("La descarga está dañada (la huella SHA-256 no coincide). No se ha instalado nada.");
                if (!VerifyPackage(File.ReadAllBytes(zipPath), r.Signature))
                    return I18n.T("La firma del paquete no es válida: no lo ha publicado SelectOR. No se ha instalado nada.");

                // 3) Extraer y comprobar que es un paquete de SelectOR
                string ex = Path.Combine(tmp, "x");
                ZipFile.ExtractToDirectory(zipPath, ex);
                string root = File.Exists(Path.Combine(ex, "SelectOR.exe")) ? ex : FindDirWith(ex, "SelectOR.exe");
                if (root == null || !File.Exists(Path.Combine(root, "SelectOR.dll")))
                    return I18n.T("El paquete descargado no contiene SelectOR.");

                // 4) Instalar: actual → *.old, nuevo → su sitio. Si algo falla, se deshace.
                var done = new List<string>();
                try
                {
                    foreach (var f in PackageFiles)
                    {
                        string nw = Path.Combine(root, f);
                        if (!File.Exists(nw)) continue;
                        string cur = Path.Combine(AppDir, f), old = cur + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        if (File.Exists(cur)) File.Move(cur, old);
                        done.Add(f);
                        File.Copy(nw, cur, true);
                    }
                }
                catch (Exception e)
                {
                    foreach (var f in done)
                    {
                        try
                        {
                            string cur = Path.Combine(AppDir, f), old = cur + ".old";
                            if (File.Exists(old)) { if (File.Exists(cur)) File.Delete(cur); File.Move(old, cur); }
                        }
                        catch { }
                    }
                    if (e is UnauthorizedAccessException)
                        return I18n.T("Sin permiso para escribir en la carpeta de SelectOR. Ejecútalo como administrador o muévelo a una carpeta de tu usuario.");
                    return I18n.T("No se pudo instalar la actualización: ") + e.Message;
                }

                // 5) Reiniciar con la nueva versión
                Process.Start(new ProcessStartInfo(Path.Combine(AppDir, "SelectOR.exe"))
                {
                    UseShellExecute = true, WorkingDirectory = AppDir, Arguments = "--updated"
                });
                return null;
            }
            catch (Exception e) { return I18n.T("No se pudo instalar la actualización: ") + e.Message; }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        static string FindDirWith(string dir, string file)
        {
            foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
                if (File.Exists(Path.Combine(d, file))) return d;
            return null;
        }

        public static string Sha256File(string path)
        {
            using var s = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
        }

        // ------------------------ Publicar (superadmin) ------------------------

        // Empaqueta los archivos de SelectOR que se están ejecutando en un .zip (en memoria).
        public static byte[] BuildPackage()
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                foreach (var f in PackageFiles)
                {
                    string p = Path.Combine(AppDir, f);
                    if (!File.Exists(p)) continue;
                    var e = zip.CreateEntry(f, CompressionLevel.Optimal);
                    using var es = e.Open();
                    using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    fs.CopyTo(es);
                }
            }
            return ms.ToArray();
        }

        // Sube el paquete al bucket público y registra la versión. Devuelve null si OK o el error.
        public static async Task<string> PublishAsync(string notes, IProgress<string> status)
        {
            if (!Supa.IsSuperadmin || string.IsNullOrEmpty(Supa.AccessToken)) return I18n.T("Solo el superadministrador puede publicar actualizaciones.");
            if (!HasSigningKey) return I18n.T("Este equipo no tiene la clave de firma de actualizaciones: sin ella las copias instaladas rechazarían el paquete.");
            await Supa.EnsureFreshTokenAsync();
            string ver = CurrentVersionText;
            try
            {
                status?.Report(I18n.T("Empaquetando…"));
                byte[] pkg = BuildPackage();
                string sha = Convert.ToHexString(SHA256.HashData(pkg)).ToLowerInvariant();
                string sig = SignPackage(pkg);
                if (sig == null) return I18n.T("No se pudo firmar el paquete con la clave de este equipo.");
                string obj = $"SelectOR_{ver}.zip";

                status?.Report(I18n.T("Subiendo paquete…"));
                using (var http = NewHttp(300))
                using (var req = SupaReq(HttpMethod.Post, $"/storage/v1/object/{Bucket}/{Uri.EscapeDataString(obj)}", Supa.AccessToken))
                {
                    req.Headers.TryAddWithoutValidation("x-upsert", "true");
                    req.Content = new ByteArrayContent(pkg);
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                    using var resp = await http.SendAsync(req);
                    if (!resp.IsSuccessStatusCode)
                        return I18n.T("No se pudo subir el paquete: ") + await resp.Content.ReadAsStringAsync();
                }
                string url = $"{Supa.BuiltInUrl.TrimEnd('/')}/storage/v1/object/public/{Bucket}/{Uri.EscapeDataString(obj)}";

                status?.Report(I18n.T("Registrando versión…"));
                var (_, err) = await Supa.RpcAsync("publish_release", new { p_version = ver, p_url = url, p_sha256 = sha, p_notes = notes ?? "", p_signature = sig });
                return err;
            }
            catch (Exception e) { return e.Message; }
        }
    }
}
