// Geometría real de la vía para los mapas (Ruta, Conducción libre, Horarios, mini-mapa y mapa grande).
// El .tdb solo guarda el PUNTO DE INICIO de cada sección de vía; uniendo esos puntos con rectas,
// las curvas y los desvíos salen quebrados. Cada sección apunta a su definición en tsection.dat
// (radio y ángulo si es curva), así que aquí se reconstruye el arco entero con puntos cada ~2°.
// Comprobado con CGL_V22N_LATAM: el final calculado de cada curva cae a 4 mm del inicio de la
// sección siguiente (2.830 curvas).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Orts.Formats.Msts;

namespace SelectOR
{
    // Conversión mundo → latitud/longitud EXACTAMENTE como la hace Open Rails (WorldLatLon.ConvertWTC,
    // la misma que da la posición del tren en /API/MAP). Así la vía y la marca del tren caen una
    // encima de la otra en los mapas del HUD. Se busca por nombre y por reflexión, porque cada
    // versión de OR la tiene en su DLL (y en la NewYear MG la biblioteca se llama ORTS.Common).
    static class OrGeo
    {
        static object _w; static System.Reflection.MethodInfo _conv; static Type _v3;
        static bool _probed;

        public static bool Available { get { Probe(); return _conv != null; } }

        static void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                var asm = typeof(ORTS.Common.Input.UserCommand).Assembly;
                Type wt = null;
                foreach (var t in asm.GetTypes()) if (t.Name == "WorldLatLon") { wt = t; break; }
                var m = wt?.GetMethod("ConvertWTC");
                if (m == null || m.GetParameters().Length != 5) return;
                _v3 = m.GetParameters()[2].ParameterType;
                _w = Activator.CreateInstance(wt);
                _conv = m;
            }
            catch { _conv = null; }
        }

        // worldX/worldZ = casilla·2048 + posición local (como las guardan los mapas). Grados.
        public static bool TryLatLon(double worldX, double worldZ, out double lat, out double lon)
        {
            lat = lon = 0;
            Probe();
            if (_conv == null) return false;
            try
            {
                int tx = (int)Math.Round(worldX / 2048.0), tz = (int)Math.Round(worldZ / 2048.0);
                var v = Activator.CreateInstance(_v3, (float)(worldX - tx * 2048.0), 0f, (float)(worldZ - tz * 2048.0));
                var args = new object[] { tx, tz, v, 0.0, 0.0 };
                _conv.Invoke(_w, args);
                lat = (double)args[3] * 180.0 / Math.PI;
                lon = (double)args[4] * 180.0 / Math.PI;
                return !(double.IsNaN(lat) || double.IsNaN(lon)) && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180;
            }
            catch { return false; }
        }

        // (objetos de la sala, 2026-11-04) al revés: lat/lon → mundo (casilla·2048 + metros, como TryLatLon). Open Rails no
        // trae esa conversión: se busca con Newton desde el nudo de vía de la ruta más cercano (sus posiciones, del .tdb).
        static readonly Dictionary<string, List<(double x, double z, double lat, double lon)>> _anchors = new(StringComparer.OrdinalIgnoreCase);
        public static bool TryWorld(string routeDir, double lat, double lon, out double worldX, out double worldZ)
        {
            worldX = worldZ = 0;
            if (!Available) return false;
            List<(double x, double z, double lat, double lon)> an;
            lock (_anchors)
                if (!_anchors.TryGetValue(routeDir ?? "", out an))
                {
                    an = new();
                    try
                    {
                        var tdb = Directory.GetFiles(routeDir, "*.tdb");
                        var nodes = new TrackDatabaseFile(tdb[0]).TrackDB?.TrackNodes;
                        if (nodes != null)
                            foreach (var n in nodes)
                            {
                                if (n?.UiD == null || (n.TrJunctionNode == null && !n.TrEndNode)) continue;
                                double x = n.UiD.TileX * 2048.0 + n.UiD.X, z = n.UiD.TileZ * 2048.0 + n.UiD.Z;
                                if (TryLatLon(x, z, out double la, out double lo)) an.Add((x, z, la, lo));
                            }
                    }
                    catch { }
                    _anchors[routeDir ?? ""] = an;
                }
            if (an.Count == 0) return false;
            var best = an.OrderBy(a => (a.lat - lat) * (a.lat - lat) + (a.lon - lon) * (a.lon - lon) * Math.Cos(lat * Math.PI / 180) * Math.Cos(lat * Math.PI / 180)).First();
            // (la lat/lon de Open Rails va a saltos de ~1 m: el jacobiano, con puntos a ±50 m; y se da por buena a menos de 3 m)
            double cos = Math.Cos(lat * Math.PI / 180), wx = best.x, wz = best.z, bestErr = double.MaxValue, bx = wx, bz = wz;
            for (int it = 0; it < 12; it++)
            {
                if (!TryLatLon(wx, wz, out double la0, out double lo0)) return false;
                double err = Math.Sqrt(Math.Pow((lat - la0) * 111320, 2) + Math.Pow((lon - lo0) * 111320 * cos, 2));
                if (err < bestErr) { bestErr = err; bx = wx; bz = wz; }
                if (err < 0.05) break;
                if (!TryLatLon(wx + 50, wz, out double lxp, out double oxp) || !TryLatLon(wx - 50, wz, out double lxm, out double oxm)
                    || !TryLatLon(wx, wz + 50, out double lzp, out double ozp) || !TryLatLon(wx, wz - 50, out double lzm, out double ozm)) return false;
                // jacobiano (grados por metro) y el paso que falta
                double a = (lxp - lxm) / 100, b = (lzp - lzm) / 100, c = (oxp - oxm) / 100, d = (ozp - ozm) / 100, det = a * d - b * c;
                if (Math.Abs(det) < 1e-24) return false;
                double dla = lat - la0, dlo = lon - lo0;
                double dx = (d * dla - b * dlo) / det, dz = (-c * dla + a * dlo) / det;
                wx += dx; wz += dz;
                if (Math.Abs(dx) < 0.02 && Math.Abs(dz) < 0.02) break;
            }
            if (TryLatLon(wx, wz, out double la1, out double lo1))
            {
                double err = Math.Sqrt(Math.Pow((lat - la1) * 111320, 2) + Math.Pow((lon - lo1) * 111320 * cos, 2));
                if (err < bestErr) { bestErr = err; bx = wx; bz = wz; }
            }
            worldX = bx; worldZ = bz;
            return bestErr < 3;
        }
    }

    static class TrackGeometry
    {
        // tsection.dat global + el de la ruta, leídos una vez por ruta (el global es grande).
        static readonly Dictionary<string, TrackSectionsFile> _cache = new(StringComparer.OrdinalIgnoreCase);

        public static TrackSectionsFile Sections(string routeDir)
        {
            if (string.IsNullOrEmpty(routeDir)) return null;
            lock (_cache)
            {
                if (_cache.TryGetValue(routeDir, out var ts)) return ts;
                try
                {
                    // <contenido>\ROUTES\<ruta>  →  <contenido>\GLOBAL\tsection.dat
                    var root = Directory.GetParent(routeDir)?.Parent?.FullName;
                    var global = root == null ? null : Path.Combine(root, "GLOBAL", "tsection.dat");
                    if (global != null && File.Exists(global))
                    {
                        ts = new TrackSectionsFile(global);
                        var local = Path.Combine(routeDir, "tsection.dat");
                        if (File.Exists(local)) ts.AddRouteTSectionDatFile(local);
                    }
                }
                catch { ts = null; }
                _cache[routeDir] = ts;
                return ts;
            }
        }

        // Polilínea de un nodo de vía (coordenadas de mundo X, Z) con las curvas como arcos. Sin
        // tsection.dat, como antes: los puntos de inicio de cada sección.
        public static PointF[] NodePolyline(TrVectorSection[] vs, TrackSectionsFile ts)
        {
            var d = NodePolylineD(vs, ts);
            var res = new PointF[d.Count];
            for (int i = 0; i < d.Count; i++) res[i] = new PointF((float)d[i].x, (float)d[i].z);
            return res;
        }

        // Igual, en doble precisión (en float las coordenadas de mundo se redondean a ~1 m; los andenes
        // necesitan precisión de palmo para distinguir su vía de la de al lado).
        public static List<(double x, double z)> NodePolylineD(TrVectorSection[] vs, TrackSectionsFile ts)
        {
            var pts = new List<(double x, double z)>(vs.Length * 2);
            for (int i = 0; i < vs.Length; i++)
            {
                var s = vs[i];
                double x = s.TileX * 2048.0 + s.X, z = s.TileZ * 2048.0 + s.Z;
                pts.Add((x, z));
                if (ts == null || !ts.TrackSections.TryGetValue(s.SectionIndex, out var sec) || sec?.SectionCurve == null) continue;
                double r = sec.SectionCurve.Radius, ang = sec.SectionCurve.Angle * Math.PI / 180.0;
                if (r <= 0 || Math.Abs(ang) < 1e-6) continue;
                // Rumbo: AY; adelante = (sen AY, cos AY) en (X, Z); a la derecha = (cos AY, −sen AY).
                double fx = Math.Sin(s.AY), fz = Math.Cos(s.AY);
                int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(ang) / (2.0 * Math.PI / 180.0)));
                // Puntos intermedios del arco (el final lo pone la sección siguiente o el empalme).
                for (int k = 1; k < n; k++)
                {
                    double a = ang * k / n;
                    double fwd = r * Math.Sin(Math.Abs(a)), lat = Math.Sign(a) * r * (1 - Math.Cos(a));
                    pts.Add((x + fx * fwd + fz * lat, z + fz * fwd - fx * lat));
                }
                if (i == vs.Length - 1)   // última sección: también su punto final
                {
                    double fwd = r * Math.Sin(Math.Abs(ang)), lat = Math.Sign(ang) * r * (1 - Math.Cos(ang));
                    pts.Add((x + fx * fwd + fz * lat, z + fz * fwd - fx * lat));
                }
            }
            return pts;
        }

        // Une el tramo con la posición real de sus empalmes/finales (el .tdb no la repite en las
        // secciones), por el extremo que corresponda.
        public static PointF[] JoinEnds(PointF[] pts, bool haveA, PointF pa, bool haveB, PointF pb)
        {
            static float D2(PointF a, PointF b) { float dx = a.X - b.X, dz = a.Y - b.Y; return dx * dx + dz * dz; }
            var list = new List<PointF>(pts);
            if (list.Count == 0) return pts;
            if (haveA && haveB)
            {
                if (D2(pa, list[0]) <= D2(pa, list[list.Count - 1])) { list.Insert(0, pa); list.Add(pb); }
                else { list.Insert(0, pb); list.Add(pa); }
            }
            else if (haveA)
            {
                if (D2(pa, list[0]) <= D2(pa, list[list.Count - 1])) list.Insert(0, pa); else list.Add(pa);
            }
            else if (haveB)
            {
                if (D2(pb, list[0]) <= D2(pb, list[list.Count - 1])) list.Insert(0, pb); else list.Add(pb);
            }
            // Fuera puntos repetidos (el empalme suele coincidir con el primer/último punto).
            var clean = new List<PointF>(list.Count);
            foreach (var p in list)
                if (clean.Count == 0 || D2(clean[clean.Count - 1], p) > 0.01f) clean.Add(p);
            return clean.ToArray();
        }
    }
}
