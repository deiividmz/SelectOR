// Rutas autorizadas (servidor: rutas-autorizadas.sql). Aquí lo que no depende de la interfaz:
//  · la IDENTIDAD de una ruta: su RouteID (del .trk) y la HUELLA de su archivo de vías (.tdb, SHA-256), que
//    distingue una versión de otra y una ruta de una copia con el mismo RouteID;
//  · su FICHA (km de vía, estaciones, límites de velocidad…) y su PLANO: el mismo detalle del mapa grande del
//    HUD (vía, andenes, apartaderos, cruces, puntos de carga, toperas, PK y estaciones), en JSON con las líneas
//    en «encoded polyline» (1e-5), comprimido con gzip y en base64. Así el superadministrador ve la ruta aunque
//    no la tenga instalada;
//  · los textos y colores de cada estado.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SelectOR
{
    // Plano de una ruta, listo para dibujar (RoutePlanView).
    public sealed class RoutePlan
    {
        public HudMapDetail Detail = new HudMapDetail();
        public readonly List<(double lat, double lon, string name)> Stations = new();
        public double MinLat = 90, MaxLat = -90, MinLon = 180, MaxLon = -180;
        public bool Empty => Detail.Track.Count == 0;
    }

    public static class RouteAuth
    {
        public sealed class Ident { public string Dir, Id, Name, Hash; }

        static readonly Dictionary<string, (long stamp, string hash)> _hashes = new(StringComparer.OrdinalIgnoreCase);

        // Huella del .tdb (SHA-256 en hexadecimal). Se guarda mientras el archivo no cambie. null si no hay .tdb.
        public static string TdbHash(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
                var tdb = Directory.GetFiles(dir, "*.tdb").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (tdb == null) return null;
                var fi = new FileInfo(tdb);
                long stamp = fi.LastWriteTimeUtc.Ticks ^ (fi.Length << 1);
                lock (_hashes) if (_hashes.TryGetValue(tdb, out var hit) && hit.stamp == stamp) return hit.hash;
                using var fs = new FileStream(tdb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                string h = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
                lock (_hashes) _hashes[tdb] = (stamp, h);
                return h;
            }
            catch { return null; }
        }

        public static Ident IdentityOf(string dir, string name)
        {
            string h = TdbHash(dir);
            if (h == null) return null;
            return new Ident { Dir = dir, Id = RouteIds.IdOf(dir, name), Name = (name ?? "").Trim(), Hash = h };
        }

        static double R5(double v) => Math.Round(v, 5);

        // Ficha y plano de la ruta (en segundo plano: recorre todo el grafo).
        public static (Dictionary<string, object> stats, RoutePlan plan) BuildPlan(RouteGraph g, HudMapDetail d)
        {
            var stats = new Dictionary<string, object>();
            if (g == null || d == null) return (stats, null);
            var plan = new RoutePlan { Detail = d };

            // estaciones: el centro de sus andenes
            var st = new Dictionary<string, (double la, double lo, int n)>(StringComparer.OrdinalIgnoreCase);
            int limits = 0; double len = 0;
            for (int k = 0; k < g.Edges.Length; k++)
            {
                var e = g.Edges[k];
                len += e.Len; limits += e.Limits.Count;
                foreach (var (off, name) in e.Platforms)
                {
                    var (x, y) = g.PointAt(k, off); var (la, lo) = g.Unproj(x, y);
                    st.TryGetValue(name, out var a);
                    st[name] = (a.la + la, a.lo + lo, a.n + 1);
                }
            }
            foreach (var kv in st) plan.Stations.Add((kv.Value.la / kv.Value.n, kv.Value.lo / kv.Value.n, kv.Key));

            double minLa = 90, maxLa = -90, minLo = 180, maxLo = -180;
            foreach (var l in d.Track) { minLa = Math.Min(minLa, l.MinLa); maxLa = Math.Max(maxLa, l.MaxLa); minLo = Math.Min(minLo, l.MinLo); maxLo = Math.Max(maxLo, l.MaxLo); }
            stats["km"] = Math.Round(len / 1000.0, 1);
            stats["stations"] = st.Count;
            stats["limits"] = limits;
            stats["platforms"] = d.Platforms.Count;
            stats["sidings"] = d.Sidings.Count + d.SidingNames.Count;
            stats["ends"] = d.Ends.Count;
            stats["pk"] = d.Pk.Count;
            if (minLa <= maxLa)
            {
                stats["min_lat"] = R5(minLa); stats["max_lat"] = R5(maxLa); stats["min_lon"] = R5(minLo); stats["max_lon"] = R5(maxLo);
                stats["span_km"] = Math.Round(HudMapDetail.Meters(minLa, minLo, maxLa, maxLo) / 1000.0, 1);
            }
            plan.MinLat = minLa; plan.MaxLat = maxLa; plan.MinLon = minLo; plan.MaxLon = maxLo;
            return (stats, plan);
        }

        // ---- formato del plano: { v, t:[línea…], p:[[lado, línea]…], s:[[nombre, línea]…], sn:[[la,lo,nombre]…],
        //      k:[[la,lo,pk]…], d:línea(cruces), u:línea(puntos de carga), e:línea(toperas), st:[[la,lo,nombre]…] }
        static string Enc(double[] la, double[] lo)
        {
            var pts = new List<(double, double)>(la.Length);
            for (int i = 0; i < la.Length; i++) pts.Add((la[i], lo[i]));
            return MainMenuForm.EncodePolyline(pts);
        }
        static string EncPts(IEnumerable<(double lat, double lon)> p) => MainMenuForm.EncodePolyline(p.ToList());

        public static string Pack(RoutePlan p)
        {
            var d = p.Detail;
            var o = new Dictionary<string, object>
            {
                ["v"] = 1,
                ["t"] = d.Track.Select(l => Enc(l.Lat, l.Lon)).ToArray(),
                ["p"] = d.Platforms.Select(l => new object[] { l.Side, Enc(l.Lat, l.Lon) }).ToArray(),
                ["s"] = d.Sidings.Select(l => new object[] { l.Name ?? "", Enc(l.Lat, l.Lon) }).ToArray(),
                ["sn"] = d.SidingNames.Select(x => new object[] { R5(x.lat), R5(x.lon), x.name }).ToArray(),
                ["k"] = d.Pk.Select(x => new object[] { R5(x.lat), R5(x.lon), Math.Round(x.value, 2) }).ToArray(),
                ["d"] = EncPts(d.Diamonds),
                ["u"] = EncPts(d.Pickups),
                ["e"] = EncPts(d.Ends),
                ["st"] = p.Stations.Select(x => new object[] { R5(x.lat), R5(x.lon), x.name }).ToArray(),
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(o);
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, true)) gz.Write(json, 0, json.Length);
            return Convert.ToBase64String(ms.ToArray());
        }

        public static RoutePlan Unpack(string b64)
        {
            if (string.IsNullOrWhiteSpace(b64)) return null;
            try
            {
                byte[] raw;
                using (var ms = new MemoryStream(Convert.FromBase64String(b64.Trim())))
                using (var gz = new GZipStream(ms, CompressionMode.Decompress))
                using (var outp = new MemoryStream()) { gz.CopyTo(outp); raw = outp.ToArray(); }
                using var doc = JsonDocument.Parse(raw);
                var r = doc.RootElement;
                var p = new RoutePlan(); var d = p.Detail;
                HudMapDetail.Line Line(string enc)
                {
                    var pts = MainMenuForm.DecodePolyline(enc);
                    var l = new HudMapDetail.Line { Lat = new double[pts.Count], Lon = new double[pts.Count], MinLa = 90, MaxLa = -90, MinLo = 180, MaxLo = -180 };
                    for (int i = 0; i < pts.Count; i++)
                    {
                        l.Lat[i] = pts[i].lat; l.Lon[i] = pts[i].lon;
                        l.MinLa = Math.Min(l.MinLa, pts[i].lat); l.MaxLa = Math.Max(l.MaxLa, pts[i].lat);
                        l.MinLo = Math.Min(l.MinLo, pts[i].lon); l.MaxLo = Math.Max(l.MaxLo, pts[i].lon);
                    }
                    return l;
                }
                JsonElement A(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v : default;
                string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                var t = A("t");
                if (t.ValueKind == JsonValueKind.Array) foreach (var x in t.EnumerateArray()) { var l = Line(x.GetString()); if (l.Lat.Length >= 2) d.Track.Add(l); }
                var pl = A("p");
                if (pl.ValueKind == JsonValueKind.Array)
                    foreach (var x in pl.EnumerateArray()) { var l = Line(x[1].GetString()); l.Side = x[0].GetInt32(); if (l.Lat.Length >= 2) d.Platforms.Add(l); }
                var sd = A("s");
                if (sd.ValueKind == JsonValueKind.Array)
                    foreach (var x in sd.EnumerateArray()) { var l = Line(x[1].GetString()); l.Name = x[0].GetString(); if (l.Lat.Length >= 2) d.Sidings.Add(l); }
                var sn = A("sn");
                if (sn.ValueKind == JsonValueKind.Array) foreach (var x in sn.EnumerateArray()) d.SidingNames.Add((x[0].GetDouble(), x[1].GetDouble(), x[2].GetString() ?? ""));
                var pk = A("k");
                if (pk.ValueKind == JsonValueKind.Array) foreach (var x in pk.EnumerateArray()) d.Pk.Add((x[0].GetDouble(), x[1].GetDouble(), (float)x[2].GetDouble()));
                d.Diamonds.AddRange(MainMenuForm.DecodePolyline(S("d")));
                d.Pickups.AddRange(MainMenuForm.DecodePolyline(S("u")));
                d.Ends.AddRange(MainMenuForm.DecodePolyline(S("e")));
                var sts = A("st");
                if (sts.ValueKind == JsonValueKind.Array) foreach (var x in sts.EnumerateArray()) p.Stations.Add((x[0].GetDouble(), x[1].GetDouble(), x[2].GetString() ?? ""));
                foreach (var l in d.Track) { p.MinLat = Math.Min(p.MinLat, l.MinLa); p.MaxLat = Math.Max(p.MaxLat, l.MaxLa); p.MinLon = Math.Min(p.MinLon, l.MinLo); p.MaxLon = Math.Max(p.MaxLon, l.MaxLo); }
                return p;
            }
            catch { return null; }
        }

        // ---- estados
        public static string StateText(string state) => state switch
        {
            "authorized" => I18n.T("Autorizada"),
            "pending" => I18n.T("Pendiente"),
            "version_pending" => I18n.T("Versión pendiente"),
            "version_rejected" => I18n.T("Versión rechazada"),
            "rejected" => I18n.T("Rechazada"),
            "revoked" => I18n.T("Retirada"),
            "proposal" => I18n.T("Propuesta"),
            _ => I18n.T("Sin solicitar"),
        };

        public static Color StateColor(string state) => state switch
        {
            "authorized" => Color.FromArgb(111, 212, 119),
            "pending" or "version_pending" => Color.FromArgb(240, 180, 41),
            "rejected" or "revoked" or "version_rejected" => Color.FromArgb(240, 128, 128),
            "proposal" => Color.FromArgb(96, 165, 250),
            _ => Color.FromArgb(150, 158, 166),
        };

        public static string ModeText(string mode) => mode switch
        {
            "warn" => I18n.T("Aviso"),
            "enforce" => I18n.T("Obligatorio"),
            _ => I18n.T("Solo recopilar"),
        };

        // Alertas automáticas de la ficha (para el superadministrador): (grave, texto).
        public static List<(bool severe, string text)> Alerts(JsonElement stats)
        {
            var a = new List<(bool, string)>();
            if (stats.ValueKind != JsonValueKind.Object || !stats.TryGetProperty("km", out _)) return a;
            double N(string k) => stats.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            double km = N("km");
            if (N("limits") == 0) a.Add((true, I18n.T("Sin límites de velocidad en toda la vía")));
            else if (km > 0 && N("limits") / km < 0.05) a.Add((false, string.Format(I18n.T("Muy pocos límites de velocidad: {0} en {1} km de vía"), N("limits").ToString("N0"), km.ToString("N1"))));
            if (N("ends") == 0) a.Add((false, string.Format(I18n.T("Sin toperas: la vía es un circuito cerrado ({0} km), un tren puede dar vueltas sin fin"), km.ToString("N1"))));
            if (N("stations") == 0) a.Add((false, I18n.T("Sin estaciones: no hay viajeros que subir ni bajar")));
            return a;
        }

        public static string StatsJson(Dictionary<string, object> s) => JsonSerializer.Serialize(s, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict });
    }
}
