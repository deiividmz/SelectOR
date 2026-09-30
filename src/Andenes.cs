// Andenes reales de la ruta, leídos del .tdb. Cada andén son DOS marcas enlazadas (principio y fin,
// LinkedPlatformItemId) sobre la vía de ese andén. Se busca esa vía en el trazado de la ruta y se guarda
// el tramo de vía del andén (con sus curvas) en coordenadas del mundo de la ruta (metros de verdad).
// Open Rails da la posición del tren en lat/lon, y su proyección DEFORMA las distancias según el rumbo
// (en Bobadilla, 520 m de vía miden 439 m en lat/lon): por eso cada andén guarda también la conversión
// local lat/lon → mundo, y todas las cuentas (de lado, a lo largo, longitud del tren) se hacen en metros.
// Además, la lat/lon que da Open Rails va A SALTOS (de ~1 a 2 m según la zona de la ruta, por la precisión
// con que la calcula): la conversión se calibra con muchos puntos, para caer en el centro de cada salto.
// Se usan para: el tamaño de cada estación (metros de andén), saber si ALGÚN coche del tren está
// DENTRO de un andén, sobre SU vía (y no a 200 m, ni en la vía de al lado), y a qué LADO queda el andén
// (del objeto andén del .w, PlatformData: 0x2 izquierda, 0x4 derecha, yendo desde la marca «ffff0000»
// hacia la otra). Ese sentido está comprobado con las rutas: en estaciones de dos vías a 5 m, donde no
// cabe un andén entre ellas, así los dos andenes quedan por fuera (al revés, saldrían los dos por dentro).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SelectOR
{
    public sealed class Anden
    {
        public string Station, Name;
        public double Lat1, Lon1, Lat2, Lon2, LengthM;
        public bool OnTrack;                                   // tramo sacado de la vía real (si no, recta entre marcas)
        public List<(double x, double z)> Path = new();        // vía del andén (mundo), alargada MarginAlongM por cada lado
        public double S0 => Andenes.MarginAlongM;              // el andén dentro de Path (m desde su principio)
        public double S1 => Andenes.MarginAlongM + LengthM;
        public double MidX, MidZ;                              // centro del andén (mundo)
        // Lado del andén: SideCanon (1 = izquierda, 2 = derecha, 3 = los dos; 0 = no se sabe) yendo de la marca
        // «ffff0000» a la otra; CanonSign: +1 si ese sentido es el de Path, −1 si es el contrario, 0 si no se sabe.
        public int SideCanon, CanonSign;
        internal uint Id1, Id2; internal bool FrontIs2, HasFront;
        // Conversiones locales lat/lon → mundo, una cada ~200 m de Path (la proyección cambia con la distancia).
        public List<GeoFrame> Frames = new();
        public bool Valid => LengthM >= Andenes.MinLengthM && LengthM <= Andenes.MaxLengthM && Path.Count >= 2 && Frames.Count > 0;
        public double MidLat => (Lat1 + Lat2) / 2;
        public double MidLon => (Lon1 + Lon2) / 2;
    }

    // mundo = O + J·(lat − OLat, lon − OLon), válida a unos 100 m alrededor de su origen.
    public sealed class GeoFrame { public double OLat, OLon, OX, OZ, Jxl, Jxn, Jzl, Jzn; }

    public static class Andenes
    {
        public const double MinLengthM = 20;      // marcas mal puestas por el autor de la ruta (0–2 m): no cuentan
        public const double MaxLengthM = 1500;    // ni las mal enlazadas (hay alguna de miles de km)
        public const double MarginAlongM = 300;   // Path sigue la vía más allá de cada extremo del andén
        public const double LateralTolM = 2.5;    // sobre la vía del andén; la de al lado está a 4–5 m
        const double MarkerSnapM = 3.0;           // una marca de andén está sobre su vía

        // Andenes de la ruta, o null si no se pueden leer (sin .tdb o sin la conversión de Open Rails).
        public static List<Anden> Load(string routeDir)
        {
            try
            {
                if (string.IsNullOrEmpty(routeDir) || !OrGeo.Available) return null;
                var tdb = Directory.GetFiles(routeDir, "*.tdb");
                if (tdb.Length == 0) return null;
                var db = new Orts.Formats.Msts.TrackDatabaseFile(tdb[0]);
                var items = db.TrackDB?.TrItemTable;
                var tnodes = db.TrackDB?.TrackNodes;
                if (items == null || tnodes == null) return null;
                var tsec = TrackGeometry.Sections(routeDir);

                // Cada marca está en un nodo de vía (el nodo la lista en sus TrItemRefs).
                var nodeOf = new Dictionary<long, int>();
                for (int ni = 0; ni < tnodes.Length; ni++)
                {
                    var refs = tnodes[ni]?.TrVectorNode?.TrItemRefs;
                    if (refs != null) foreach (var r in refs) nodeOf[(long)r] = ni;
                }
                var polys = new Dictionary<int, List<(double x, double z)>>();

                var plats = items.OfType<Orts.Formats.Msts.PlatformItem>().ToList();
                var byId = new Dictionary<uint, Orts.Formats.Msts.PlatformItem>();
                foreach (var p in plats) byId[p.TrItemId] = p;
                var done = new HashSet<uint>();
                var res = new List<Anden>();
                foreach (var p in plats)
                {
                    if (done.Contains(p.TrItemId)) continue;
                    done.Add(p.TrItemId);
                    if (!byId.TryGetValue(p.LinkedPlatformItemId, out var q)) continue;
                    done.Add(q.TrItemId);
                    string st = (p.Station ?? q.Station ?? "").Trim();
                    if (st.Length == 0) continue;
                    double x1 = p.TileX * 2048.0 + p.X, z1 = p.TileZ * 2048.0 + p.Z;
                    double x2 = q.TileX * 2048.0 + q.X, z2 = q.TileZ * 2048.0 + q.Z;
                    if (!OrGeo.TryLatLon(x1, z1, out double la1, out double lo1) || !OrGeo.TryLatLon(x2, z2, out double la2, out double lo2)) continue;
                    var a = new Anden { Station = st, Name = (p.ItemName ?? "").Trim(), Lat1 = la1, Lon1 = lo1, Lat2 = la2, Lon2 = lo2, Id1 = p.TrItemId, Id2 = q.TrItemId };
                    bool f1 = IsFront(p.Flags1), f2 = IsFront(q.Flags1);
                    a.HasFront = f1 != f2; a.FrontIs2 = f2;
                    double chord = Math.Sqrt((x1 - x2) * (x1 - x2) + (z1 - z2) * (z1 - z2));

                    List<(double x, double z)> world = null;
                    if (nodeOf.TryGetValue(p.TrItemId, out int n1) && nodeOf.TryGetValue(q.TrItemId, out int n2) && n1 == n2)
                    {
                        if (!polys.TryGetValue(n1, out var poly)) polys[n1] = poly = NodePath(tnodes, n1, tsec);
                        world = SubPath(poly, x1, z1, x2, z2, out double along);
                        if (world != null) { a.OnTrack = true; a.LengthM = along; }
                    }
                    if (world == null)
                    {
                        // Marcas en nodos distintos (andén que cruza un desvío…): la recta entre marcas, alargada.
                        a.LengthM = chord;
                        if (chord >= 1)
                        {
                            double ux = (x2 - x1) / chord, uz = (z2 - z1) / chord;
                            world = new List<(double, double)> { (x1 - ux * MarginAlongM, z1 - uz * MarginAlongM), (x2 + ux * MarginAlongM, z2 + uz * MarginAlongM) };
                        }
                    }
                    a.MidX = (x1 + x2) / 2; a.MidZ = (z1 + z2) / 2;
                    if (world != null && a.LengthM >= MinLengthM && a.LengthM <= MaxLengthM)
                    {
                        a.Path = world;
                        BuildFrames(a);
                        // ¿El sentido «marca ffff0000 → la otra» es el de Path?
                        if (a.HasFront)
                        {
                            double s1 = AlongPath(a.Path, x1, z1), s2 = AlongPath(a.Path, x2, z2);
                            double sFront = a.FrontIs2 ? s2 : s1, sOther = a.FrontIs2 ? s1 : s2;
                            a.CanonSign = sOther > sFront + 1 ? 1 : sOther < sFront - 1 ? -1 : 0;
                        }
                    }
                    res.Add(a);
                }
                ReadSides(routeDir, res, plats);
                return res;
            }
            catch { return null; }
        }

        // Flags1 de la marca: «ffff0000» señala la marca «delantera» (según versión de OR, texto o número).
        static bool IsFront(object flags)
        {
            string f = Convert.ToString(flags, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            return f.Equals("ffff0000", StringComparison.OrdinalIgnoreCase) || f == "4294901760";
        }

        // Lado de cada andén, del objeto andén de los .w (solo se leen las casillas con marcas de andén, y
        // de cada una solo los andenes). Si no se puede (otra versión de OR, .w ilegible), queda sin saber.
        static void ReadSides(string routeDir, List<Anden> list, List<Orts.Formats.Msts.PlatformItem> plats)
        {
            try
            {
                var dir = Path.Combine(routeDir, "WORLD");
                if (!Directory.Exists(dir) || list.Count == 0) return;
                var ids = new HashSet<long>();
                var tiles = new HashSet<(int, int)>();
                foreach (var p in plats) { ids.Add(p.TrItemId); tiles.Add((p.TileX, p.TileZ)); }
                var data = new Dictionary<long, uint>();
                foreach (var (tx, tz) in tiles)
                {
                    string f = Path.Combine(dir, $"w{tx.ToString("+000000;-000000")}{tz.ToString("+000000;-000000")}.w");
                    if (!File.Exists(f)) continue;
                    // Casi todos los .w son texto: se buscan ahí los bloques «Platform» (milisegundos). Los
                    // binarios o comprimidos, con el lector de Open Rails.
                    if (ScanText(f, (db, id, d) => { if (db == 0 && ids.Contains(id) && !data.ContainsKey(id)) data[id] = d; })) continue;
                    var wf = OpenWorld(f);
                    if (wf?.Tr_Worldfile == null) continue;
                    foreach (var o in wf.Tr_Worldfile)
                        if (o is Orts.Formats.Msts.PlatformObj po)
                            foreach (var t in po.trItemIDList)
                                if (t.db == 0 && ids.Contains(t.dbID) && !data.ContainsKey(t.dbID)) data[t.dbID] = po.PlatformData;
                }
                foreach (var a in list)
                {
                    if (!data.TryGetValue(a.Id1, out uint d) && !data.TryGetValue(a.Id2, out d)) continue;
                    a.SideCanon = ((d & 0x2) != 0 ? 1 : 0) | ((d & 0x4) != 0 ? 2 : 0);
                }
            }
            catch { }
        }

        // .w de texto: cada bloque «Platform ( … )» con sus TrItemId ( db id ) y su PlatformData ( hex ).
        // false si el archivo no es de texto (binario o comprimido) o no se puede leer.
        static bool ScanText(string f, Action<int, long, uint> found)
        {
            try
            {
                var raw = File.ReadAllBytes(f);
                string t;
                if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE) t = System.Text.Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
                else t = System.Text.Encoding.ASCII.GetString(raw);
                if (!t.StartsWith("SIMISA@@@@@@@@@@JINX0w0t", StringComparison.Ordinal)) return false;
                int i = 0;
                while ((i = t.IndexOf("Platform", i, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    int j = i + 8;
                    bool word = (i == 0 || char.IsWhiteSpace(t[i - 1]) || t[i - 1] == '(');
                    while (j < t.Length && char.IsWhiteSpace(t[j])) j++;
                    if (!word || j >= t.Length || t[j] != '(') { i += 8; continue; }
                    // Fin del bloque (paréntesis equilibrados; las cadenas entre comillas no cuentan)
                    int depth = 0, k = j; bool q = false;
                    for (; k < t.Length; k++)
                    {
                        char c = t[k];
                        if (c == '"') q = !q;
                        else if (!q && c == '(') depth++;
                        else if (!q && c == ')' && --depth == 0) break;
                    }
                    string b = t.Substring(j, Math.Min(k, t.Length - 1) - j + 1);
                    var md = System.Text.RegularExpressions.Regex.Match(b, @"PlatformData\s*\(\s*([0-9A-Fa-f]+)\s*\)");
                    uint d = md.Success ? Convert.ToUInt32(md.Groups[1].Value, 16) : 0;
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(b, @"TrItemId\s*\(\s*(\d+)\s+(\d+)\s*\)"))
                        found(int.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value), d);
                    i = k;
                }
                return true;
            }
            catch { return false; }
        }

        // Carga un .w leyendo solo sus bloques «Platform» (el filtro de Open Rails vive en Orts.Parsers.Msts,
        // que SelectOR no enlaza: se pide por reflexión). Sin filtro, el .w entero.
        static Type _tokType; static object _tokList; static bool _tokProbed;
        static Orts.Formats.Msts.WorldFile OpenWorld(string f)
        {
            try
            {
                if (!_tokProbed)
                {
                    _tokProbed = true;
                    try
                    {
                        _tokType = Type.GetType("Orts.Parsers.Msts.TokenID, Orts.Parsers.Msts");
                        if (_tokType != null)
                        {
                            var l = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_tokType));
                            l.Add(Enum.Parse(_tokType, "Platform"));
                            _tokList = l;
                        }
                    }
                    catch { _tokList = null; }
                }
                if (_tokList != null)
                    try { return (Orts.Formats.Msts.WorldFile)Activator.CreateInstance(typeof(Orts.Formats.Msts.WorldFile), f, _tokList); }
                    catch (MissingMethodException) { _tokList = null; }
                return new Orts.Formats.Msts.WorldFile(f);
            }
            catch { return null; }
        }

        static double AlongPath(List<(double x, double z)> pts, double x, double z) => Along(pts, x, z, out _);

        // Puertas que tocan en este andén, desde el puesto del maquinista (1 = izquierda, 2 = derecha, 3 = las
        // dos; 0 = no se sabe → vale cualquiera). Se supone que el maquinista mira hacia donde ha ido el tren:
        // rumbo = del punto del rastro a 8 m o más hasta la locomotora (el último de trail).
        public static int DoorsFor(Anden a, IList<(double lat, double lon)> trail)
        {
            if (a == null || !a.Valid || a.SideCanon == 0 || a.CanonSign == 0 || trail == null || trail.Count < 2) return 0;
            var (lx, lz) = ToWorld(a, trail[trail.Count - 1].lat, trail[trail.Count - 1].lon);
            for (int i = trail.Count - 2; i >= 0; i--)
            {
                var (px, pz) = ToWorld(a, trail[i].lat, trail[i].lon);
                double dx = lx - px, dz = lz - pz;
                if (dx * dx + dz * dz < 64) continue;
                // Sentido de Path cerca del tren
                int k = 0; double bd = double.MaxValue;
                for (int j = 0; j + 1 < a.Path.Count; j++)
                {
                    double mx = (a.Path[j].x + a.Path[j + 1].x) / 2 - lx, mz = (a.Path[j].z + a.Path[j + 1].z) / 2 - lz, d = mx * mx + mz * mz;
                    if (d < bd) { bd = d; k = j; }
                }
                double tx = a.Path[k + 1].x - a.Path[k].x, tz = a.Path[k + 1].z - a.Path[k].z;
                int dir = dx * tx + dz * tz >= 0 ? 1 : -1;
                if (dir == a.CanonSign) return a.SideCanon;
                return (a.SideCanon == 1 ? 2 : a.SideCanon == 2 ? 1 : a.SideCanon);
            }
            return 0;
        }

        // Una conversión cada ~200 m a lo largo de Path (y en sus dos extremos).
        static void BuildFrames(Anden a)
        {
            double acc = 0, next = 0;
            for (int i = 0; i < a.Path.Count; i++)
            {
                if (i > 0) acc += D(a.Path[i - 1], a.Path[i]);
                if (acc >= next || i == a.Path.Count - 1)
                {
                    var f = Frame(a.Path[i].x, a.Path[i].z);
                    if (f != null) a.Frames.Add(f);
                    next = acc + 200;
                }
            }
        }

        // Conversión local lat/lon → mundo alrededor de (x, z), por mínimos cuadrados: se convierten con
        // Open Rails 25 puntos repartidos ±80 m (con pasos no enteros, para caer en sitios distintos de cada
        // salto) y se ajusta la recta que mejor los devuelve. Error: la mitad del salto (±0,5–1 m).
        internal static GeoFrame Frame(double x, double z)
        {
            if (!OrGeo.TryLatLon(x, z, out double l0, out double n0)) return null;
            const int K = 2; const double SX = 37.3, SZ = 41.7;
            var dl = new List<double>(); var dn = new List<double>(); var wx = new List<double>(); var wz = new List<double>();
            for (int i = -K; i <= K; i++)
                for (int j = -K; j <= K; j++)
                {
                    double px = x + i * SX + j * 0.61, pz = z + j * SZ + i * 0.43;
                    if (!OrGeo.TryLatLon(px, pz, out double la, out double lo)) continue;
                    dl.Add(la - l0); dn.Add(lo - n0); wx.Add(px); wz.Add(pz);
                }
            int n = dl.Count;
            if (n < 6) return null;
            double ml = dl.Average(), mn = dn.Average(), mx = wx.Average(), mz = wz.Average();
            double sll = 0, snn = 0, sln = 0, slx = 0, snx = 0, slz = 0, snz = 0;
            for (int k = 0; k < n; k++)
            {
                double u = dl[k] - ml, v = dn[k] - mn, ex = wx[k] - mx, ez = wz[k] - mz;
                sll += u * u; snn += v * v; sln += u * v; slx += u * ex; snx += v * ex; slz += u * ez; snz += v * ez;
            }
            double det = sll * snn - sln * sln;
            if (Math.Abs(det) < 1e-30) return null;
            var f = new GeoFrame { OLat = l0, OLon = n0 };
            f.Jxl = (slx * snn - snx * sln) / det; f.Jxn = (snx * sll - slx * sln) / det;
            f.Jzl = (slz * snn - snz * sln) / det; f.Jzn = (snz * sll - slz * sln) / det;
            f.OX = mx - f.Jxl * ml - f.Jxn * mn;
            f.OZ = mz - f.Jzl * ml - f.Jzn * mn;
            return f;
        }

        // lat/lon de Open Rails → mundo, con la conversión más cercana del andén.
        public static (double x, double z) ToWorld(Anden a, double lat, double lon)
        {
            GeoFrame best = null; double bd = double.MaxValue, cs = Math.Cos(lat * Math.PI / 180.0);
            foreach (var f in a.Frames)
            {
                double d1 = lat - f.OLat, d2 = (lon - f.OLon) * cs, d = d1 * d1 + d2 * d2;
                if (d < bd) { bd = d; best = f; }
            }
            if (best == null) return (double.NaN, double.NaN);
            double dl = lat - best.OLat, dn = lon - best.OLon;
            return (best.OX + best.Jxl * dl + best.Jxn * dn, best.OZ + best.Jzl * dl + best.Jzn * dn);
        }

        // Para las pruebas: la conversión de Open Rails (mundo → lat/lon).
        public static bool ToLatLon(double x, double z, out double lat, out double lon) => OrGeo.TryLatLon(x, z, out lat, out lon);

        // Vía de un nodo (con sus curvas) unida a la posición de sus empalmes/finales.
        internal static List<(double x, double z)> NodePath(Orts.Formats.Msts.TrackNode[] tnodes, int ni, Orts.Formats.Msts.TrackSectionsFile tsec)
        {
            var tn = tnodes[ni];
            var pts = TrackGeometry.NodePolylineD(tn.TrVectorNode.TrVectorSections, tsec);
            if (pts.Count == 0 || tn.TrPins == null) return pts;
            foreach (var pin in tn.TrPins.Take(2))
            {
                if (pin.Link < 0 || pin.Link >= tnodes.Length) continue;
                var en = tnodes[pin.Link];
                if (en?.UiD == null || (en.TrJunctionNode == null && !en.TrEndNode)) continue;
                (double x, double z) e = (en.UiD.TileX * 2048.0 + en.UiD.X, en.UiD.TileZ * 2048.0 + en.UiD.Z);
                double d0 = D(e, pts[0]), d1 = D(e, pts[pts.Count - 1]);
                if (Math.Min(d0, d1) < 0.05) continue;
                if (d0 <= d1) pts.Insert(0, e); else pts.Add(e);
            }
            return pts;
        }

        static double D((double x, double z) a, (double x, double z) b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z));

        // Posición a lo largo de la vía (m) del punto más cercano, y distancia a ella.
        internal static double Along(List<(double x, double z)> pts, double x, double z, out double dist)
        {
            dist = double.MaxValue; double best = 0, acc = 0;
            for (int k = 0; k + 1 < pts.Count; k++)
            {
                double ax = pts[k].x, az = pts[k].z, dx = pts[k + 1].x - ax, dz = pts[k + 1].z - az;
                double L2 = dx * dx + dz * dz, L = Math.Sqrt(L2);
                double t = L2 > 0 ? Math.Max(0, Math.Min(1, ((x - ax) * dx + (z - az) * dz) / L2)) : 0;
                double px = ax + t * dx, pz = az + t * dz, d = Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                if (d < dist) { dist = d; best = acc + t * L; }
                acc += L;
            }
            return best;
        }

        // Punto de la vía a «s» metros del principio (antes del principio o pasado el final, siguiendo recto).
        static (double x, double z) PointAt(List<(double x, double z)> pts, double s)
        {
            double acc = 0;
            for (int k = 0; k + 1 < pts.Count; k++)
            {
                double L = D(pts[k], pts[k + 1]);
                bool last = k + 2 == pts.Count;
                if (L > 0 && (s <= acc + L || last))
                {
                    double t = (s - acc) / L;
                    return (pts[k].x + t * (pts[k + 1].x - pts[k].x), pts[k].z + t * (pts[k + 1].z - pts[k].z));
                }
                acc += L;
            }
            return pts[pts.Count - 1];
        }

        // Tramo de vía del andén (entre sus marcas), alargado MarginAlongM por cada lado.
        static List<(double x, double z)> SubPath(List<(double x, double z)> pts, double x1, double z1, double x2, double z2, out double along)
        {
            along = 0;
            if (pts == null || pts.Count < 2) return null;
            double s1 = Along(pts, x1, z1, out double d1), s2 = Along(pts, x2, z2, out double d2);
            if (d1 > MarkerSnapM || d2 > MarkerSnapM) return null;
            along = Math.Abs(s2 - s1);
            double from = Math.Min(s1, s2) - MarginAlongM, to = Math.Max(s1, s2) + MarginAlongM;
            var res = new List<(double x, double z)> { PointAt(pts, from) };
            double acc = 0;
            for (int k = 0; k + 1 < pts.Count; k++)
            {
                acc += D(pts[k], pts[k + 1]);
                if (acc > from && acc < to) res.Add(pts[k + 1]);
            }
            res.Add(PointAt(pts, to));
            return res;
        }

        // ¿La posición está sobre la vía de ese andén, entre sus dos extremos? marginM: cuánto puede
        // quedar fuera por cada extremo (0 = dentro del andén; como mucho MarginAlongM).
        public static bool Inside(Anden a, double lat, double lon, double marginM, out double lateralM)
        {
            lateralM = double.MaxValue;
            if (a == null || !a.Valid) return false;
            var (x, z) = ToWorld(a, lat, lon);
            return InsideW(a, x, z, marginM, out lateralM);
        }

        static bool InsideW(Anden a, double x, double z, double marginM, out double lateralM)
        {
            lateralM = double.MaxValue;
            double acc = 0, sBest = 0;
            for (int i = 0; i + 1 < a.Path.Count; i++)
            {
                double ax = a.Path[i].x, az = a.Path[i].z, dx = a.Path[i + 1].x - ax, dz = a.Path[i + 1].z - az;
                double L2 = dx * dx + dz * dz, L = Math.Sqrt(L2);
                double t = L2 > 0 ? Math.Max(0, Math.Min(1, ((x - ax) * dx + (z - az) * dz) / L2)) : 0;
                double px = ax + t * dx - x, pz = az + t * dz - z, d = Math.Sqrt(px * px + pz * pz);
                if (d < lateralM) { lateralM = d; sBest = acc + t * L; }
                acc += L;
            }
            double m = Math.Min(Math.Max(0, marginM), MarginAlongM);
            return lateralM <= LateralTolM && sBest >= a.S0 - m && sBest <= a.S1 + m;
        }

        // ¿Algún coche del tren dentro del andén? trail = rastro de la locomotora (del más antiguo al más
        // reciente; el último es donde está ahora). Los coches de detrás van por ese rastro hasta backM
        // metros; los de delante (aheadM), en la dirección de la marcha. Se mira un punto cada 3 m.
        // trailShort: el rastro no llega a cubrir todo el tren (escenario recién cargado): entonces vale
        // también la locomotora a menos de backM del andén, porque no se sabe hacia dónde queda el resto.
        public static bool TrainInside(Anden a, IList<(double lat, double lon)> trail, double backM, double aheadM,
                                       out double lateralM, out bool trailShort)
        {
            lateralM = double.MaxValue; trailShort = true;
            if (a == null || !a.Valid || trail == null || trail.Count == 0) return false;
            var (lx, lz) = ToWorld(a, trail[trail.Count - 1].lat, trail[trail.Count - 1].lon);
            // Lejos del andén: ni se mira (en rutas con miles de andenes, casi todos).
            double far = Math.Sqrt((lx - a.MidX) * (lx - a.MidX) + (lz - a.MidZ) * (lz - a.MidZ));
            if (!(far <= a.LengthM / 2 + backM + aheadM + MarginAlongM + 100)) return false;
            var w = new (double x, double z)[trail.Count];
            for (int i = 0; i < trail.Count - 1; i++) w[i] = ToWorld(a, trail[i].lat, trail[i].lon);
            w[w.Length - 1] = (lx, lz);
            bool inside = false; double best = double.MaxValue;
            void Try(double x, double z, double margin)
            {
                if (InsideW(a, x, z, margin, out double lt)) { inside = true; if (lt < best) best = lt; }
            }
            Try(lx, lz, 0);
            // Por detrás, por el rastro
            double acc = 0, cx = lx, cz = lz;
            for (int i = w.Length - 2; i >= 0 && acc < backM; i--)
            {
                double nx = w[i].x, nz = w[i].z, seg = Math.Sqrt((nx - cx) * (nx - cx) + (nz - cz) * (nz - cz));
                if (seg < 0.01) continue;
                for (double s = 3; s < seg && acc + s <= backM; s += 3) Try(cx + (nx - cx) * s / seg, cz + (nz - cz) * s / seg, 0);
                double take = Math.Min(seg, backM - acc);
                Try(cx + (nx - cx) * take / seg, cz + (nz - cz) * take / seg, 0);
                acc += seg; cx = nx; cz = nz;
            }
            trailShort = acc < backM;
            if (trailShort) Try(lx, lz, backM);
            // Por delante: rumbo = del punto del rastro a 8 m o más hasta la locomotora
            if (aheadM > 3)
                for (int i = w.Length - 2; i >= 0; i--)
                {
                    double dx = lx - w[i].x, dz = lz - w[i].z, d = Math.Sqrt(dx * dx + dz * dz);
                    if (d < 8) continue;
                    for (double s = 3; s <= aheadM; s += 3) Try(lx + dx / d * s, lz + dz / d * s, 0);
                    break;
                }
            lateralM = best;
            return inside;
        }
    }
}
