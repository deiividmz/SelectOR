// Clima (Ajustes de SelectOR → Clima): imágenes de clima de SelectOR (nubes, lluvia, nieve, brillo de las luces, cielo
// nocturno) en la carpeta Content de Open Rails mientras se conduce desde SelectOR, como ReShade:
//  · al lanzar, con el clima activado: de cada grupo elegido, la imagen de SelectOR en Content (la de Open Rails se guarda
//    antes en %AppData%\Open Rails\SelectOR\clima\respaldo\…); solo se sustituyen imágenes que ya existen en Content;
//  · al volver (y al abrir SelectOR) se devuelven las de Open Rails. Una imagen que ya no es la nuestra (la ha cambiado
//    el usuario u otra versión de Open Rails) no se toca.
// Las imágenes están en el servidor (bucket público «clima», con el nombre de su huella) y las cambia el administrador
// desde SelectOR sin publicar versión (sql/clima.sql). El manifiesto (nombre, huella y tamaño de cada una) va FIRMADO con
// la clave de las actualizaciones (Updater.SignData; solo está en su PC): cada SelectOR comprueba la firma, la huella y
// que sea una imagen de verdad con un nombre de la lista antes de usarla.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SelectOR
{
    public static class ClimaFiles
    {
        // Grupos y las imágenes de Content de Open Rails que se pueden cambiar (ninguna otra).
        public static readonly (string id, string label, string[] files)[] Groups =
        {
            ("clouds", "Nubes", new[] { "Clouds01.png", "SkyDome1.png" }),
            ("rain", "Lluvia", new[] { "Raindrop.png" }.Concat(Enumerable.Range(1, 10).Select(i => $"Raindrop{i}.png")).ToArray()),
            ("snow", "Nieve", new[] { "Snowflake.png" }),
            ("lights", "Brillo de las luces", new[] { "LightGlow.png", "SignalLightGlow.png", "ORTSLightGlow.png" }),
            ("night", "Cielo nocturno", new[] { "StarDome1.bmp", "Starmap_N.png", "Starmap_S.png", "MoonMap.png", "MoonMask.png" }),
        };
        public const long MaxBytes = 25L * 1024 * 1024;
        const string Bucket = "clima";

        public static string GroupOf(string name)
        {
            foreach (var g in Groups) if (g.files.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase))) return g.id;
            return null;
        }
        public static string CanonicalName(string name)
        {
            foreach (var g in Groups) foreach (var f in g.files) if (string.Equals(f, name, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        public static string OrDir = AppContext.BaseDirectory;   // (las pruebas ponen otra)
        public static string ContentDir => Path.Combine(OrDir, "Content");
        static string CacheDir => Path.Combine(AppDataTidy.CacheRoot, "clima");
        static string BackupDir
        {
            get
            {
                var h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(OrDir).TrimEnd('\\', '/').ToLowerInvariant())))[..12];
                return Path.Combine(CacheDir, "respaldo", h);
            }
        }
        static string StatePath => Path.Combine(BackupDir, "estado.json");

        // ---------------------------------------------------------------- manifiesto
        public sealed class Item { public string name { get; set; } public string sha { get; set; } public long size { get; set; } }
        public sealed class Manifest
        {
            public int v { get; set; } = 1;
            public int ver { get; set; }
            public string date { get; set; }
            public List<Item> files { get; set; } = new();
        }
        static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

        public static string Serialize(Manifest m) => JsonSerializer.Serialize(m, JsonOpts);

        // Comprueba la firma y que todo tenga sentido; null si no vale.
        public static Manifest Parse(string text, string signature)
        {
            if (string.IsNullOrEmpty(text) || !Updater.VerifyPackage(Encoding.UTF8.GetBytes(text), signature)) return null;
            try
            {
                var m = JsonSerializer.Deserialize<Manifest>(text);
                if (m?.files == null || m.v != 1) return null;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in m.files)
                {
                    f.name = CanonicalName(f.name);
                    if (f.name == null || !seen.Add(f.name)) return null;
                    if (f.sha == null || f.sha.Length != 64 || !f.sha.All(Uri.IsHexDigit)) return null;
                    f.sha = f.sha.ToLowerInvariant();
                    if (f.size <= 0 || f.size > MaxBytes) return null;
                }
                return m;
            }
            catch { return null; }
        }

        static string Ext(string name) => Path.GetExtension(name).ToLowerInvariant();
        static string CachedFile(Item f) => Path.Combine(CacheDir, f.sha + Ext(f.name));
        public static string ObjectUrl(Item f) => Supa.Url.TrimEnd('/') + "/storage/v1/object/public/" + Bucket + "/" + f.sha + Ext(f.name);

        static string Sha(byte[] d) => Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();
        static string ShaFile(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }

        // ¿Es una imagen de verdad (PNG o BMP) que se puede abrir?
        public static bool IsImage(byte[] d, string ext)
        {
            try
            {
                bool png = d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47;
                bool bmp = d.Length > 2 && d[0] == 0x42 && d[1] == 0x4D;
                if (!(ext == ".png" && png) && !(ext == ".bmp" && bmp)) return false;
                using var ms = new MemoryStream(d);
                using var img = Image.FromStream(ms, false, true);
                return img.Width > 0 && img.Height > 0 && img.Width <= 16384 && img.Height <= 16384;
            }
            catch { return false; }
        }

        // El último clima descargado y comprobado (null si no hay).
        public static Manifest LoadCached()
        {
            try
            {
                string mf = Path.Combine(CacheDir, "manifiesto.json"), sg = Path.Combine(CacheDir, "manifiesto.sig");
                if (!File.Exists(mf) || !File.Exists(sg)) return null;
                return Parse(File.ReadAllText(mf, Encoding.UTF8), File.ReadAllText(sg).Trim());
            }
            catch { return null; }
        }

        public static bool IsCached(Item f) { try { return File.Exists(CachedFile(f)) && new FileInfo(CachedFile(f)).Length == f.size; } catch { return false; } }
        public static string CachedPath(Item f) => IsCached(f) ? CachedFile(f) : null;

        // El clima publicado en el servidor: (texto, firma, versión). null si no hay ninguno o falla.
        public static async Task<(string text, string sig, int ver, string err)> FetchAsync()
        {
            var (json, err) = await Supa.RpcAsync("climate_get", new { });
            if (err != null) return (null, null, 0, err);
            try
            {
                using var d = JsonDocument.Parse(json);
                var r = d.RootElement;
                if (r.ValueKind != JsonValueKind.Object) return (null, null, 0, null);   // aún no hay clima publicado
                string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                int ver = r.TryGetProperty("version", out var vv) && vv.ValueKind == JsonValueKind.Number ? vv.GetInt32() : 0;
                return (S("manifest"), S("signature"), ver, null);
            }
            catch (Exception ex) { return (null, null, 0, ex.Message); }
        }

        // Descarga el clima publicado (manifiesto firmado e imágenes que falten) y lo deja en la caché. progress(hechas, total).
        public static async Task<(Manifest m, string err)> SyncAsync(Action<int, int> progress = null)
        {
            var (text, sig, _, err) = await FetchAsync();
            if (err != null) return (LoadCached(), err);
            if (text == null) return (null, null);
            var m = Parse(text, sig);
            if (m == null) return (LoadCached(), I18n.T("El clima del servidor no tiene una firma válida: no se usa."));
            Directory.CreateDirectory(CacheDir);
            int done = 0;
            foreach (var f in m.files)
            {
                progress?.Invoke(done, m.files.Count);
                if (!IsCached(f))
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, ObjectUrl(f));
                        using var resp = await Supa.HttpBig().SendAsync(req);
                        if (!resp.IsSuccessStatusCode) return (LoadCached(), string.Format(I18n.T("No se ha podido descargar {0}."), f.name));
                        var data = await resp.Content.ReadAsByteArrayAsync();
                        if (data.LongLength != f.size || Sha(data) != f.sha || !IsImage(data, Ext(f.name)))
                            return (LoadCached(), string.Format(I18n.T("{0} no coincide con el clima publicado: no se usa."), f.name));
                        string tmp = CachedFile(f) + ".part";
                        await File.WriteAllBytesAsync(tmp, data);
                        File.Move(tmp, CachedFile(f), true);
                    }
                    catch (Exception ex) { return (LoadCached(), ex.Message); }
                }
                done++;
            }
            progress?.Invoke(done, m.files.Count);
            File.WriteAllText(Path.Combine(CacheDir, "manifiesto.json"), text, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(CacheDir, "manifiesto.sig"), sig);
            // fuera las imágenes que ya no están en el clima
            var keep = new HashSet<string>(m.files.Select(f => Path.GetFileName(CachedFile(f))), StringComparer.OrdinalIgnoreCase);
            foreach (var p in Directory.GetFiles(CacheDir))
            {
                string n = Path.GetFileName(p), e = Path.GetExtension(n).ToLowerInvariant();
                if ((e == ".png" || e == ".bmp" || e == ".part") && !keep.Contains(n)) try { File.Delete(p); } catch { }
            }
            return (m, null);
        }

        // ---------------------------------------------------------------- respaldo de las imágenes de Open Rails
        sealed class Rec { public string orig { get; set; } public string ours { get; set; } }
        static Dictionary<string, Rec> LoadState()
        {
            try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<Dictionary<string, Rec>>(File.ReadAllText(StatePath)) ?? new() : new(); }
            catch { return new(); }
        }
        static void SaveState(Dictionary<string, Rec> st)
        {
            Directory.CreateDirectory(BackupDir);
            if (st.Count == 0) { try { File.Delete(StatePath); } catch { } return; }
            File.WriteAllText(StatePath, JsonSerializer.Serialize(st));
        }
        static void CopyOver(string src, string dst) { string tmp = dst + ".selector-tmp"; File.Copy(src, tmp, true); File.Move(tmp, dst, true); }

        // Al lanzar: con el clima activado, las imágenes de los grupos elegidos en Content. Devuelve por qué no (o null).
        public static string Install(AppPrefs p)
        {
            try
            {
                if (p == null || !p.ClimaEnabled) { Remove(); return null; }
                var m = LoadCached();
                if (m == null) { Remove(); return "sin clima descargado"; }
                if (!Directory.Exists(ContentDir)) return "sin carpeta Content";
                var groups = new HashSet<string>(p.ClimaGroups ?? new List<string>());
                var st = LoadState();
                // primero se devuelven las de los grupos que ya no se quieren (o que el clima ya no trae)
                foreach (var name in st.Keys.ToList())
                {
                    var f = m.files.FirstOrDefault(x => x.name == name);
                    if (f == null || !groups.Contains(GroupOf(name)) || f.sha != st[name].ours) RestoreOne(st, name);
                }
                int n = 0;
                foreach (var f in m.files)
                {
                    if (!groups.Contains(GroupOf(f.name))) continue;
                    string src = CachedPath(f), dst = Path.Combine(ContentDir, f.name);
                    if (src == null || !File.Exists(dst)) continue;   // (solo se cambian imágenes que Open Rails ya tiene)
                    if (ShaFile(src) != f.sha) continue;              // la caché no es la buena
                    string cur = ShaFile(dst);
                    if (cur == f.sha) continue;                         // ya está (o es igual que la de Open Rails)
                    string bak = Path.Combine(BackupDir, f.name);
                    Directory.CreateDirectory(BackupDir);
                    if (!st.TryGetValue(f.name, out var rec) || cur != rec.ours)
                    {
                        File.Copy(dst, bak, true);   // la de Open Rails (o la que haya puesto el usuario desde la última vez)
                        st[f.name] = new Rec { orig = cur, ours = f.sha };
                    }
                    CopyOver(src, dst);
                    n++;
                }
                SaveState(st);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // Al volver de conducir y al abrir SelectOR: las imágenes de Open Rails otra vez en su sitio.
        public static void Remove()
        {
            try
            {
                if (!File.Exists(StatePath)) return;
                var st = LoadState();
                foreach (var name in st.Keys.ToList()) RestoreOne(st, name);
                SaveState(st);
            }
            catch { }
        }

        static void RestoreOne(Dictionary<string, Rec> st, string name)
        {
            try
            {
                string dst = Path.Combine(ContentDir, name), bak = Path.Combine(BackupDir, name);
                var rec = st[name];
                bool ours = File.Exists(dst) && ShaFile(dst) == rec.ours;
                if ((ours || !File.Exists(dst)) && File.Exists(bak) && ShaFile(bak) == rec.orig) CopyOver(bak, dst);
                else if (File.Exists(dst) && !ours) { }   // ya no es la nuestra: no se toca
                else return;                              // (no se ha podido: queda apuntada para la próxima vez)
                try { File.Delete(bak); } catch { }
                st.Remove(name);
            }
            catch { }   // Open Rails la tiene abierta: la próxima vez
        }

        // ¿Hay imágenes de SelectOR puestas ahora en Content? (para las pruebas y el aviso)
        public static int InstalledCount => LoadState().Count;

        // ---------------------------------------------------------------- administrador
        // Sube una imagen (con el nombre de su huella; si ya está, no pasa nada). Devuelve el elemento o el error.
        public static async Task<(Item item, string err)> UploadAsync(string name, byte[] data)
        {
            name = CanonicalName(name);
            if (name == null) return (null, I18n.T("Ese nombre no es de ninguna imagen de clima de Open Rails."));
            if (data == null || data.LongLength == 0 || data.LongLength > MaxBytes) return (null, I18n.T("La imagen es demasiado grande (25 MB como mucho)."));
            if (!IsImage(data, Ext(name))) return (null, I18n.T("No es una imagen PNG o BMP válida."));
            if (!Supa.IsSuperadmin || !Supa.IsLoggedIn) return (null, I18n.T("Solo el administrador."));
            await Supa.EnsureFreshTokenAsync();
            var item = new Item { name = name, sha = Sha(data), size = data.LongLength };
            try
            {
                using var req = Supa.NewRequest(HttpMethod.Post, "/storage/v1/object/" + Bucket + "/" + item.sha + Ext(name));
                req.Headers.TryAddWithoutValidation("x-upsert", "true");
                req.Content = new ByteArrayContent(data);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue(Ext(name) == ".bmp" ? "image/bmp" : "image/png");
                using var resp = await Supa.HttpBig().SendAsync(req);
                if (!resp.IsSuccessStatusCode) return (null, I18n.T("No se ha podido subir: ") + await resp.Content.ReadAsStringAsync());
            }
            catch (Exception ex) { return (null, ex.Message); }
            // a la caché, para la vista previa y para usarla ya
            try { Directory.CreateDirectory(CacheDir); File.WriteAllBytes(CachedFile(item), data); } catch { }
            return (item, null);
        }

        // Publica el clima (firmado con la clave de este equipo). Devuelve la versión publicada o el error.
        public static async Task<(int ver, string err)> PublishAsync(List<Item> files)
        {
            if (!Supa.IsSuperadmin) return (0, I18n.T("Solo el administrador."));
            if (!Updater.HasSigningKey) return (0, I18n.T("Este equipo no tiene la clave de firma de SelectOR: el clima solo se puede publicar desde el PC con la clave."));
            var (_, _, pubVer, ferr) = await FetchAsync();
            if (ferr != null && ferr.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0) return (0, I18n.T("El servidor aún no tiene el clima (falta clima.sql)."));
            var m = new Manifest { ver = Math.Max(pubVer, LoadCached()?.ver ?? 0) + 1, date = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                                   files = files.Select(f => new Item { name = f.name, sha = f.sha, size = f.size }).OrderBy(f => f.name, StringComparer.Ordinal).ToList() };
            string text = Serialize(m);
            string sig = Updater.SignData(Encoding.UTF8.GetBytes(text));
            if (sig == null) return (0, I18n.T("No se ha podido firmar el clima con la clave de este equipo."));
            var (_, err) = await Supa.RpcAsync("climate_publish", new { p_manifest = text, p_signature = sig, p_version = m.ver });
            if (err != null) return (0, err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0 ? I18n.T("El servidor aún no tiene el clima (falta clima.sql).") : err);
            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(Path.Combine(CacheDir, "manifiesto.json"), text, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(CacheDir, "manifiesto.sig"), sig);
            }
            catch { }
            return (m.ver, null);
        }

        // ¿Es igual que la de Open Rails? (entonces al conducir no hace falta ponerla). Se calcula una vez por imagen.
        static readonly Dictionary<string, bool> _same = new();
        public static bool SameAsOriginal(Item f)
        {
            string key = f.name + ":" + f.sha;
            lock (_same) if (_same.TryGetValue(key, out bool v)) return v;
            bool same = false;
            try { string o = OriginalPath(f.name); same = o != null && new FileInfo(o).Length == f.size && ShaFile(o) == f.sha; } catch { }
            lock (_same) _same[key] = same;
            return same;
        }

        // La imagen de Open Rails (la de Content o, si está puesta la nuestra, la guardada). null si no hay.
        public static string OriginalPath(string name)
        {
            string bak = Path.Combine(BackupDir, name), cur = Path.Combine(ContentDir, name);
            if (File.Exists(bak)) return bak;
            return File.Exists(cur) ? cur : null;
        }
    }
}
