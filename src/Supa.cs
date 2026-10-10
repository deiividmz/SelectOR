// Cliente mínimo de Supabase (Auth + REST + RPC) por HTTP, sin dependencias extra.
// La URL y la anon key se configuran desde la pestaña Empresas (se guardan en las prefs).

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SelectOR
{
    public static class Supa
    {
        // ====================================================================
        //  BACKEND FIJO — TU proyecto Supabase (compartido por todos)
        //  Pega aquí la URL del proyecto y la anon key (pública, es seguro
        //  incrustarla). En cuanto ambas tengan valor:
        //    · Todos los maquinistas usan TU Supabase (no el suyo).
        //    · Desaparece la pantalla de "Configuración del servidor".
        //  Mientras estén vacías, la app pide la config en pantalla (solo para
        //  tus pruebas de desarrollo).
        //  NUNCA pongas aquí la service_role key: solo la anon key.
        // ====================================================================
        public const string BuiltInUrl = "https://fsajtxmtsccbylpgslmu.supabase.co";
        public const string BuiltInAnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImZzYWp0eG10c2NjYnlscGdzbG11Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODg1Nzg4MDksImV4cCI6MjEwNDE1NDgwOX0.3X1bZ1ckvjBkldBTzoOib2eDc9SM_7Qk4GsRriHWlqg";

        public static bool HasBuiltIn =>
            !string.IsNullOrWhiteSpace(BuiltInUrl) && !string.IsNullOrWhiteSpace(BuiltInAnonKey);

        // Superadmin: lo decide el servidor (is_superadmin()) al iniciar sesión; el programa no guarda
        // nada que lo identifique. Esto solo decide qué ve la interfaz: los permisos de verdad los
        // vuelve a comprobar el servidor en cada operación.
        public static bool IsSuperadmin { get; private set; }
        static string _superadminCheckedFor;   // usuario para el que ya se consultó

        static HttpClient _http;

        public static string Url { get; private set; }
        public static string AnonKey { get; private set; }
        public static string AccessToken { get; private set; }
        public static string UserId { get; private set; }
        public static string Email { get; private set; }
        public static string Username { get; private set; }

        // Sesión: el access_token (JWT) de Supabase caduca (por defecto, a la hora). En una partida
        // larga eso hacía fallar el registro del servicio al volver ("JWT expired"). Guardamos también
        // el refresh_token y la caducidad para poder renovar la sesión sola, sin pedir al maquinista
        // que vuelva a iniciar sesión a mitad de un viaje.
        public static string RefreshToken { get; private set; }
        static DateTime _accessExpiresUtc = DateTime.MinValue;
        static readonly SemaphoreSlim _refreshLock = new(1, 1);
        static System.Threading.Timer _refreshTimer;

        public static bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(AnonKey);
        public static bool IsLoggedIn => !string.IsNullOrEmpty(AccessToken);

        public static void Configure(string url, string anonKey)
        {
            Url = string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
            AnonKey = string.IsNullOrWhiteSpace(anonKey) ? null : anonKey.Trim();
            _http = null;
        }

        public static void SignOut()
        {
            AccessToken = null; RefreshToken = null; _accessExpiresUtc = DateTime.MinValue;
            UserId = null; Email = null; Username = null;
            IsSuperadmin = false; _superadminCheckedFor = null;
        }

        public static void SetUsername(string u) { if (!string.IsNullOrWhiteSpace(u)) Username = u; }
        // Nombre a mostrar: nombre de maquinista si lo hay; si no, el correo.
        public static string DisplayName => !string.IsNullOrWhiteSpace(Username) ? Username : (Email ?? "");

        // Peticiones al servidor en curso (la pantalla de inicio espera a que acaben las de Empresas).
        static int _inFlight;
        public static int InFlight => System.Threading.Volatile.Read(ref _inFlight);
        static async Task<HttpResponseMessage> Send(HttpRequestMessage req)
        {
            System.Threading.Interlocked.Increment(ref _inFlight);
            try { return await Http().SendAsync(req); }
            finally { System.Threading.Interlocked.Decrement(ref _inFlight); }
        }

        static HttpClient Http()
        {
            if (_http == null)
            {
                _http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            }
            return _http;
        }

        static HttpRequestMessage Req(HttpMethod m, string path, string bodyJson, bool useUserToken)
        {
            var r = new HttpRequestMessage(m, Url + path);
            r.Headers.TryAddWithoutValidation("apikey", AnonKey);
            var bearer = useUserToken && !string.IsNullOrEmpty(AccessToken) ? AccessToken : AnonKey;
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (bodyJson != null) r.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            return r;
        }

        static string Err(string status, string json)
        {
            try
            {
                using var d = JsonDocument.Parse(json);
                var root = d.RootElement;
                foreach (var k in new[] { "error_description", "msg", "message", "error", "hint" })
                    if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString();
            }
            catch { }
            return string.IsNullOrWhiteSpace(json) ? status : json;
        }

        // ---- Auth ----
        static async Task<string> AuthPost(string path, object body)
        {
            try
            {
                var json = JsonSerializer.Serialize(body);
                var req = Req(HttpMethod.Post, path, json, useUserToken: false);
                var resp = await Send(req);
                var txt = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) return Err(resp.StatusCode.ToString(), txt);
                using var d = JsonDocument.Parse(txt);
                var root = d.RootElement;
                if (root.TryGetProperty("access_token", out var at)) AccessToken = at.GetString();
                // El refresh_token rota en cada uso: SIEMPRE nos quedamos con el último que nos den
                // (login, registro o el propio refresco), o perderíamos la sesión al segundo refresco.
                if (root.TryGetProperty("refresh_token", out var rt)) RefreshToken = rt.GetString();
                _accessExpiresUtc = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var secs)
                    ? DateTime.UtcNow.AddSeconds(secs) : DateTime.MinValue;
                var user = root.TryGetProperty("user", out var u) ? u : root;
                if (user.ValueKind == JsonValueKind.Object)
                {
                    if (user.TryGetProperty("id", out var id)) UserId = id.GetString();
                    if (user.TryGetProperty("email", out var em)) Email = em.GetString();
                }
                EnsureRefreshTimer();
                if (UserId != _superadminCheckedFor) await CheckSuperadminAsync();
                return null; // ok
            }
            catch (Exception e) { return e.Message; }
        }

        // Petición directa (sin el reintento de SendCore): se llama desde AuthPost, que puede estar
        // dentro del refresco de sesión, y el token acaba de emitirse. Si falla (sin red), se deja
        // sin marcar para volver a preguntar en el siguiente refresco.
        static async Task CheckSuperadminAsync()
        {
            bool su = false; string forUser = null;
            try
            {
                var resp = await Send(Req(HttpMethod.Post, "/rest/v1/rpc/is_superadmin", "{}", useUserToken: true));
                var txt = await resp.Content.ReadAsStringAsync();
                if (resp.IsSuccessStatusCode) { su = txt.Trim() == "true"; forUser = UserId; }
            }
            catch { }
            IsSuperadmin = su; _superadminCheckedFor = forUser;
        }

        /// <summary>Devuelve null si OK, o el mensaje de error.</summary>
        public static Task<string> SignInAsync(string email, string password)
            => AuthPost("/auth/v1/token?grant_type=password", new { email, password });

        public static async Task<string> SignUpAsync(string email, string password, string username)
        {
            // lang viaja en los metadatos (raw_user_meta_data) para que la plantilla de
            // correo de Supabase pueda enviarse en el idioma de Open Rails ({{ .Data.lang }}).
            var err = await AuthPost("/auth/v1/signup",
                new { email, password, data = new { username, lang = I18n.English ? "en" : "es" } });
            Username = username;
            return err;
        }

        // ---- Renovación de sesión (evita "JWT expired" en partidas largas) ----

        // Refresca solo si hace falta (o si no sabemos cuándo caduca): deja margen de 5 minutos para
        // no arriesgarnos a que caduque a mitad de una llamada. La llama un timer periódico mientras
        // haya sesión, así el token nunca llega a caducar en uso normal.
        public static Task<bool> EnsureFreshTokenAsync()
        {
            if (string.IsNullOrEmpty(RefreshToken)) return Task.FromResult(false);
            if (_accessExpiresUtc != DateTime.MinValue && _accessExpiresUtc - DateTime.UtcNow > TimeSpan.FromMinutes(5))
                return Task.FromResult(true);
            return RefreshCoreAsync();
        }

        // Refresco incondicional (lo usa también el reintento tras un "JWT expired" real).
        // Serializado con un lock: si dos llamadas caducan a la vez, que solo una gaste el
        // refresh_token (Supabase lo rota; usar uno ya gastado por otra petición fallaría).
        static async Task<bool> RefreshCoreAsync()
        {
            if (string.IsNullOrEmpty(RefreshToken)) return false;
            await _refreshLock.WaitAsync();
            try { return await AuthPost("/auth/v1/token?grant_type=refresh_token", new { refresh_token = RefreshToken }) == null; }
            finally { _refreshLock.Release(); }
        }

        static void EnsureRefreshTimer()
        {
            if (_refreshTimer != null) return;
            var period = TimeSpan.FromMinutes(10);
            _refreshTimer = new System.Threading.Timer(async _ =>
            {
                try { if (IsLoggedIn) await EnsureFreshTokenAsync(); } catch { }
            }, null, period, period);
        }

        static bool IsJwtExpired(string err) => err != null && err.IndexOf("jwt expired", StringComparison.OrdinalIgnoreCase) >= 0;

        // Envía la petición y, si falla justo por token caducado, refresca UNA vez y la repite.
        // Con el timer de EnsureRefreshTimer esto no debería llegar a hacer falta en uso normal;
        // queda como red de seguridad (p. ej. si el PC estuvo en suspensión y el timer no corrió).
        static async Task<(bool ok, string txt, string status)> SendCore(HttpMethod m, string path, string bodyJson, bool useUserToken, Action<HttpRequestMessage> configure = null)
        {
            async Task<(bool ok, string txt, string status)> Once()
            {
                var req = Req(m, path, bodyJson, useUserToken);
                configure?.Invoke(req);
                var resp = await Send(req);
                var txt = await resp.Content.ReadAsStringAsync();
                return (resp.IsSuccessStatusCode, txt, resp.StatusCode.ToString());
            }
            var r = await Once();
            if (!r.ok && useUserToken && IsJwtExpired(Err(r.status, r.txt)) && await RefreshCoreAsync())
                r = await Once();
            return r;
        }

        // ---- RPC (funciones del servidor) ----
        public static async Task<(string json, string err)> RpcAsync(string fn, object args)
        {
            try
            {
                var (ok, txt, status) = await SendCore(HttpMethod.Post, "/rest/v1/rpc/" + fn, JsonSerializer.Serialize(args ?? new { }), true);
                return ok ? (txt, null) : (null, Err(status, txt));
            }
            catch (Exception e) { return (null, e.Message); }
        }

        // ---- RPC COMPLETA (por páginas) ----
        // Igual que SelectAllAsync, para una función que recibe desde qué fila (p_offset) y cuántas (p_limit).
        public static async Task<(string json, string err)> RpcAllAsync(string fn, Func<int, int, object> args, int page = 1000, int maxRows = 50000)
        {
            var sb = new StringBuilder("[");
            int offset = 0; bool primero = true;
            while (true)
            {
                var (json, err) = await RpcAsync(fn, args(page, offset));
                if (err != null) return (null, err);
                int n = 0;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    if (d.RootElement.ValueKind != JsonValueKind.Array) return (json, null);
                    foreach (var e in d.RootElement.EnumerateArray())
                    {
                        if (!primero) sb.Append(',');
                        sb.Append(e.GetRawText());
                        primero = false; n++;
                    }
                }
                catch { return (json, null); }
                if (n < page) break;
                offset += page;
                if (offset >= maxRows) break;
            }
            sb.Append(']');
            return (sb.ToString(), null);
        }

        // ---- RPC que devuelve una tabla, COMPLETA (por páginas con ?limit=&offset=, que PostgREST admite en las funciones
        // que devuelven filas). Para las que no reciben p_limit/p_offset (p. ej. train_list, con miles de trenes). La
        // función tiene que devolver las filas siempre en el mismo orden.
        public static async Task<(string json, string err)> RpcPagedAsync(string fn, object args, int page = 1000, int maxRows = 200000)
        {
            var sb = new StringBuilder("[");
            int offset = 0; bool primero = true;
            string body = JsonSerializer.Serialize(args ?? new { });
            while (true)
            {
                string json, err;
                try
                {
                    var (ok, txt, status) = await SendCore(HttpMethod.Post, "/rest/v1/rpc/" + fn + "?limit=" + page + "&offset=" + offset, body, true);
                    json = ok ? txt : null; err = ok ? null : Err(status, txt);
                }
                catch (Exception e) { return (null, e.Message); }
                if (err != null) return (null, err);
                int n = 0;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    if (d.RootElement.ValueKind != JsonValueKind.Array) return (json, null);
                    foreach (var e in d.RootElement.EnumerateArray())
                    {
                        if (!primero) sb.Append(',');
                        sb.Append(e.GetRawText());
                        primero = false; n++;
                    }
                }
                catch { return (json, null); }
                if (n < page) break;
                offset += page;
                if (offset >= maxRows) break;
            }
            sb.Append(']');
            return (sb.ToString(), null);
        }

        // ---- SELECT COMPLETO (por páginas) ----
        // La API de Supabase devuelve como mucho 1.000 filas por respuesta (límite de PostgREST), así
        // que una flota de miles de unidades llegaba recortada. Aquí se piden páginas sucesivas hasta
        // que una viene incompleta, y se devuelven todas juntas como una sola lista JSON.
        public static async Task<(string json, string err)> SelectAllAsync(string pathQuery, int page = 1000)
        {
            var sb = new StringBuilder("[");
            int offset = 0; bool primero = true;
            while (true)
            {
                var (json, err) = await SelectAsync(pathQuery + "&limit=" + page + "&offset=" + offset);
                if (err != null) return (null, err);
                int n = 0;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    if (d.RootElement.ValueKind != JsonValueKind.Array) return (json, null);   // no es una lista
                    foreach (var e in d.RootElement.EnumerateArray())
                    {
                        if (!primero) sb.Append(',');
                        sb.Append(e.GetRawText());
                        primero = false; n++;
                    }
                }
                catch { return (json, null); }
                if (n < page) break;
                offset += page;
                if (offset > 200000) break;   // tope de seguridad
            }
            sb.Append(']');
            return (sb.ToString(), null);
        }

        // ---- SELECT (lectura de tablas; pathQuery p.ej. "companies?select=*") ----
        public static async Task<(string json, string err)> SelectAsync(string pathQuery)
        {
            try
            {
                var (ok, txt, status) = await SendCore(HttpMethod.Get, "/rest/v1/" + pathQuery, null, true);
                return ok ? (txt, null) : (null, Err(status, txt));
            }
            catch (Exception e) { return (null, e.Message); }
        }

        // ---- INSERT (crea filas; devuelve la representación) ----
        public static async Task<(string json, string err)> InsertAsync(string table, object row)
        {
            try
            {
                var (ok, txt, status) = await SendCore(HttpMethod.Post, "/rest/v1/" + table, JsonSerializer.Serialize(row), true,
                    req => req.Headers.TryAddWithoutValidation("Prefer", "return=representation"));
                return ok ? (txt, null) : (null, Err(status, txt));
            }
            catch (Exception e) { return (null, e.Message); }
        }

        // ---- UPDATE (PATCH; pathQuery p.ej. "profiles?id=eq.XXXX") ----
        public static async Task<string> UpdateAsync(string pathQuery, object patch)
        {
            try
            {
                var (ok, txt, status) = await SendCore(new HttpMethod("PATCH"), "/rest/v1/" + pathQuery, JsonSerializer.Serialize(patch), true);
                return ok ? null : Err(status, txt);
            }
            catch (Exception e) { return e.Message; }
        }

        // ---- Cuenta: contraseña nueva (sistema de cuentas de Supabase, con la sesión del usuario) ----
        public static async Task<string> UpdatePasswordAsync(string newPassword)
        {
            try
            {
                var (ok, txt, status) = await SendCore(HttpMethod.Put, "/auth/v1/user", JsonSerializer.Serialize(new { password = newPassword }), true);
                return ok ? null : Err(status, txt);
            }
            catch (Exception e) { return e.Message; }
        }

        // ---- DELETE (pathQuery p.ej. "vehicles?id=eq.XXXX") ----
        public static async Task<string> DeleteAsync(string pathQuery)
        {
            try
            {
                var (ok, txt, status) = await SendCore(HttpMethod.Delete, "/rest/v1/" + pathQuery, null, true);
                return ok ? null : Err(status, txt);
            }
            catch (Exception e) { return e.Message; }
        }
    }
}
