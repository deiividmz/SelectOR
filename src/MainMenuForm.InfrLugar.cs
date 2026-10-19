// Dónde se cometió cada infracción del carné (para el mapa de Revisión): al registrar el servicio, el rastro del tren
// (_svcTrail, con el segundo del servicio en cada punto) da el punto de la infracción y el tramo que recorrió el tren,
// 2 km antes y 2 km después, con margen (en un exceso de velocidad, después de que acabara). Va en el propio detalle de la
// infracción (lo guarda report_infractions tal cual, hasta 2 000 caracteres; 8 000 con infracciones-mapa.sql):
//   lat, lon   el punto de la infracción;
//   trail      el tramo recorrido (polilínea codificada, como el recorrido del servicio);
//   d0, d1     metros desde el principio del tramo hasta la infracción y hasta donde acabó;
//   rdir       la carpeta de la ruta (para buscarla instalada; el servicio guarda su nombre);
//   map        (1.2.69) el mapa del tramo ya preparado (InfrMap.ToCompact), para verlo sin la ruta instalada.
// Las infracciones de antes de la 1.2.64 no lo llevan.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        const double InfrPlaceSpanM = 2300;   // (con margen: medido sobre lat/lon sale algo más que por la vía; el mapa corta en 2 km de vía)

        void InfrAddPlace(Dictionary<string, object> det)
        {
            try
            {
                if (det == null || _svcTrail.Count < 2 || !det.TryGetValue("at_s", out var atO)) return;
                double atS = Convert.ToDouble(atO);
                double secs = det.TryGetValue("seconds", out var sO) ? Math.Max(0, Convert.ToDouble(sO)) : 0;
                int n = _svcTrail.Count;
                int Idx(double s) { int k = 0; while (k < n - 1 && _svcTrail[k].s < s) k++; return k; }
                int i0 = Idx(atS), iE = Math.Max(i0, Idx(atS + secs));
                double Dist(int a, int b) => Haversine(_svcTrail[a].lat, _svcTrail[a].lon, _svcTrail[b].lat, _svcTrail[b].lon);
                int ib = i0; double back = 0;
                while (ib > 0 && back < InfrPlaceSpanM) { back += Dist(ib - 1, ib); ib--; }
                int ie = iE; double fwd = 0;
                while (ie < n - 1 && fwd < InfrPlaceSpanM) { fwd += Dist(ie, ie + 1); ie++; }
                var pts = new List<(double lat, double lon)>(ie - ib + 1);
                double d0 = 0, d1 = 0, acc = 0;
                for (int k = ib; k <= ie; k++)
                {
                    if (k > ib) acc += Dist(k - 1, k);
                    if (k == i0) d0 = acc;
                    if (k == iE) d1 = acc;
                    pts.Add((_svcTrail[k].lat, _svcTrail[k].lon));
                }
                if (pts.Count < 2) return;
                det["lat"] = Math.Round(_svcTrail[i0].lat, 6);
                det["lon"] = Math.Round(_svcTrail[i0].lon, 6);
                det["d0"] = Math.Round(d0);
                det["d1"] = Math.Round(d1);
                if (!string.IsNullOrEmpty(_curRoute?.Path)) det["rdir"] = Path.GetFileName(_curRoute.Path.TrimEnd('\\', '/'));
                // el tramo, cada vez más simple hasta que el detalle quepa (report_infractions guarda hasta 2 000)
                foreach (int max in new[] { 90, 60, 40, 25 })
                {
                    det["trail"] = EncodePolyline(SimplifyTrail(pts, max));
                    if (JsonSerializer.Serialize(det).Length <= 1900) return;
                }
                det.Remove("trail");
            }
            catch { }
        }

        // (1.2.69) El mapa del tramo, ya preparado aquí (que se tiene la ruta), en el detalle («map»): Revisión lo dibuja aunque
        // el administrador no tenga la ruta. Solo si el servidor admite detalles grandes (infracciones-mapa.sql:
        // infr_detail_max ≥ 8 000); si no cabe entero, con menos vías de alrededor, y si aun así no, sin mapa.
        int _infrDetailMax;   // 0 = sin preguntar

        async Task InfrAddMap(Dictionary<string, object> det)
        {
            try
            {
                if (det == null || !det.ContainsKey("trail") || string.IsNullOrEmpty(_curRoute?.Path)) return;
                if (_infrDetailMax == 0)
                {
                    var (j, e) = await Supa.RpcAsync("infr_detail_max", new { });
                    _infrDetailMax = e == null && int.TryParse((j ?? "").Trim().Trim('"'), out int mx) ? mx : 2000;
                }
                if (_infrDetailMax < 8000) return;
                var g = await RouteGraphFor(_curRoute.Path);
                if (g == null) return;
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(det));
                var place = InfrPlace.From(doc.RootElement);
                if (place == null) return;
                string dir = _curRoute.Path;
                var (map, _) = await Task.Run(() => InfrMap.Build(g, dir, place));
                if (map == null) return;
                foreach (var (step, ctx) in new[] { (20.0, 260), (20.0, 120), (30.0, 60), (40.0, 0) })
                {
                    det["map"] = map.ToCompact(step, ctx);
                    if (JsonSerializer.Serialize(det).Length <= _infrDetailMax - 100) return;
                }
                det.Remove("map");
            }
            catch { det?.Remove("map"); }
        }

        // Revisión: el mapa de una infracción, con la ruta instalada en esta carpeta de contenido (por su carpeta, que
        // guarda la infracción, o por su nombre). Se hace una vez por tarjeta y se repinta la lista al acabar.
        async Task RevBuildMap(ReviewItem it)
        {
            if (it == null || it.MapAsked) return;
            it.MapAsked = true;
            try
            {
                var p = it.Place;
                if (p == null) return;
                var rt = _routesAll.FirstOrDefault(r => p.RDir.Length > 0 && string.Equals(Path.GetFileName((r.Path ?? "").TrimEnd('\\', '/')), p.RDir, StringComparison.OrdinalIgnoreCase))
                      ?? _routesAll.FirstOrDefault(r => string.Equals(r.Name, it.Route, StringComparison.OrdinalIgnoreCase));
                // sin la ruta (o sin poder leerla): el mapa que guardó el equipo del maquinista, si lo guardó (1.2.69)
                InfrMap Saved() => p.MapJson == null ? null : InfrMap.FromCompact(p.MapJson, p.MaxKmh);
                if (rt == null)
                {
                    var sm = Saved();
                    if (sm != null) { it.Map = sm; return; }
                    it.MapNote = string.Format(Tr("Sin mapa: no tienes la ruta «{0}» en esta carpeta de contenido."), it.Route); return;
                }
                var g = await RouteGraphFor(rt.Path);
                if (g == null) { var sm = Saved(); if (sm != null) { it.Map = sm; return; } it.MapNote = Tr("Sin mapa: no se ha podido leer la vía de la ruta."); return; }
                var (map, why) = await Task.Run(() => InfrMap.Build(g, rt.Path, p));
                if (map != null) it.Map = map; else it.MapNote = Tr("Sin mapa: ") + why;
            }
            catch (Exception ex) { it.MapNote = Tr("Sin mapa: ") + ex.Message; }
            finally { try { _revList?.Invalidate(); } catch { } }
        }
    }
}
