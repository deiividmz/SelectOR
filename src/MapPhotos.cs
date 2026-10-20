// Fotos de los maquinistas en las fichas redondas de los mapas (LiveMapDraw.Chip): la foto de carnet de quien lo ha
// activado en «Mi perfil» («Mostrar mi foto en los mapas»), la de tus compañeros de empresa y la tuya
// (sql/fotos-mapas.sql: map_photos). Se piden en lote las de los trenes que salen en los mapas (por id de usuario o por
// nombre: los de una sala llevan su nombre de SelectOR), cada uno como mucho cada 5 min, y se guardan ya recortadas en
// cuadrado (la cara, arriba en el retrato 3:4) y pequeñas. Sin el SQL, o sin foto: las iniciales.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text.Json;

namespace SelectOR
{
    public static class MapPhotos
    {
        const int Size = 64;
        static readonly ConcurrentDictionary<string, Image> _img = new(StringComparer.OrdinalIgnoreCase);   // «id:…» o «name:…»
        static readonly ConcurrentDictionary<string, DateTime> _asked = new(StringComparer.OrdinalIgnoreCase);
        static bool _busy, _noSql;

        static string KeyOf(LiveMarker m, out bool isId)
        {
            isId = false;
            if (m == null) return null;
            if (!string.IsNullOrEmpty(m.Id) && Guid.TryParse(m.Id, out _)) { isId = true; return "id:" + m.Id; }
            return string.IsNullOrWhiteSpace(m.Name) ? null : "name:" + m.Name.Trim();
        }

        public static Image Get(LiveMarker m)
        {
            string k = KeyOf(m, out _);
            return k != null && _img.TryGetValue(k, out var i) ? i : null;
        }

        // La tuya (tu tren en el mapa grande, alejado).
        public static Image Mine() => Supa.IsLoggedIn && !string.IsNullOrEmpty(Supa.UserId) && _img.TryGetValue("id:" + Supa.UserId, out var i) ? i : null;

        // Tras cambiar la foto o la opción en «Mi perfil»: se vuelven a pedir.
        public static void Forget() { _asked.Clear(); _img.Clear(); _noSql = false; }

        // (desde el dibujo de los mapas) Pide las que falten de estos trenes, sin esperar.
        public static void Want(IEnumerable<LiveMarker> marks, bool me = false)
        {
            if (_busy || _noSql || !Supa.IsLoggedIn) return;
            var now = DateTime.UtcNow;
            var ids = new List<string>(); var names = new List<string>();
            void Ask(string k, bool isId, string v)
            {
                if (k == null || (_asked.TryGetValue(k, out var at) && now - at < TimeSpan.FromMinutes(5))) return;
                _asked[k] = now;
                if (isId) ids.Add(v); else names.Add(v);
            }
            if (marks != null)
                foreach (var m in marks)
                {
                    string k = KeyOf(m, out bool isId);
                    Ask(k, isId, isId ? m.Id : m.Name.Trim());
                    if (ids.Count + names.Count >= 50) break;
                }
            if (me && !string.IsNullOrEmpty(Supa.UserId)) Ask("id:" + Supa.UserId, true, Supa.UserId);
            if (ids.Count + names.Count == 0) return;
            Fetch(ids, names);
        }

        static async void Fetch(List<string> ids, List<string> names)
        {
            _busy = true;
            try
            {
                var (json, err) = await Supa.RpcAsync("map_photos", new { p_ids = ids.ToArray(), p_names = names.ToArray() });
                if (err != null)
                {
                    if (err.Contains("map_photos") || err.Contains("PGRST202") || err.Contains("does not exist")) _noSql = true;   // (sin fotos-mapas.sql)
                    return;
                }
                using var d = JsonDocument.Parse(json);
                if (d.RootElement.ValueKind != JsonValueKind.Array) return;
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    string uid = e.TryGetProperty("user_id", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    string un = e.TryGetProperty("username", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    string ph = e.TryGetProperty("photo", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                    var img = Decode(ph);
                    if (img == null) continue;
                    if (uid != null) _img["id:" + uid] = img;
                    if (un != null) _img["name:" + un.Trim()] = img;
                }
            }
            catch { }
            finally { _busy = false; }
        }

        // «data:image/jpeg;base64,…» (3:4) → cuadrado de 64 px con la parte de arriba (la cara)
        static Image Decode(string data)
        {
            try
            {
                if (string.IsNullOrEmpty(data)) return null;
                int comma = data.IndexOf(',');
                string b64 = data.StartsWith("data:") && comma > 0 ? data.Substring(comma + 1) : data;
                if (b64.Length > 200_000) return null;
                using var ms = new System.IO.MemoryStream(Convert.FromBase64String(b64.Trim()));
                using var raw = Image.FromStream(ms, false, false);
                if (raw.Width < 8 || raw.Height < 8 || raw.Width > 1024 || raw.Height > 1024) return null;
                int side = Math.Min(raw.Width, raw.Height);
                int sy = raw.Height > side ? (int)((raw.Height - side) * 0.25) : 0;
                var bmp = new Bitmap(Size, Size);
                using var g = Graphics.FromImage(bmp);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(raw, new Rectangle(0, 0, Size, Size), new Rectangle((raw.Width - side) / 2, sy, side, side), GraphicsUnit.Pixel);
                return bmp;
            }
            catch { return null; }
        }
    }
}
