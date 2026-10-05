// Detalle del mapa del HUD (mini-mapa y mapa grande), sacado del .tdb de la ruta (a través de RouteGraph): la
// vía, los andenes y los apartaderos con su longitud y nombre, los cruces de vía sin desvío, los puntos de
// carga, las toperas y los hitos kilométricos (PK). Se calcula una vez por ruta, en segundo plano,
// todo en lat/lon.

using System;
using System.Collections.Generic;
using System.Linq;

namespace SelectOR
{
    public sealed class HudMapDetail
    {
        public sealed class Line
        {
            public double[] Lat, Lon;
            public string Name;                     // apartadero: su nombre
            // andén: a qué lado de la vía, según el orden de sus puntos (1 = izquierda, 2 = derecha, 3 = los dos;
            // 0 = no se sabe → centrado en la vía)
            public int Side;
            public double MinLa, MinLo, MaxLa, MaxLo;
        }
        public readonly List<Line> Track = new(), Platforms = new(), Sidings = new();
        public readonly List<(double lat, double lon, float value)> Pk = new();
        public readonly List<(double lat, double lon)> Diamonds = new(), Pickups = new(), Ends = new();
        public readonly List<(double lat, double lon, string name)> SidingNames = new();   // sin pareja en el tramo: solo el rótulo

        // Convención del lado (comprobada con las rutas reales en la prueba «pmapadetalle»): true = respecto al sentido
        // del andén (inicio → fin), la que mejor casa; false = respecto al sentido del tramo del .tdb.
        public static bool SideFromStart = true;

        public static HudMapDetail From(RouteGraph g)
        {
            var d = new HudMapDetail();
            if (g == null || g.Edges.Length == 0) return d;
            var E = g.Edges;

            // ---- la vía: un trazo por tramo
            for (int k = 0; k < E.Length; k++) d.Track.Add(MakeLine(g, k, 0, E[k].Len));

            // ---- andenes y apartaderos: sus dos extremos (mismo nombre en el mismo tramo)
            for (int k = 0; k < E.Length; k++)
            {
                // cada andén: su marca de inicio y la de fin (enlazadas en el .tdb) en el mismo tramo
                var marks = E[k].PlatformMarks;
                foreach (var a in marks)
                {
                    if (!a.start) continue;
                    int bi = marks.FindIndex(m => m.id == a.linked);
                    if (bi < 0) continue;
                    var b = marks[bi];
                    double lo = Math.Min(a.off, b.off), hi = Math.Max(a.off, b.off);
                    if (hi - lo <= 5 || hi - lo >= 700) continue;
                    var l = MakeLine(g, k, lo, hi);
                    uint pd = g.PlatformSides.TryGetValue(a.id, out var v1) ? v1 : g.PlatformSides.TryGetValue(b.id, out var v2) ? v2 : 0;
                    // PlatformLeft (0x2) / PlatformRight (0x4) son del mundo de MSTS (con la Z hacia el norte): su izquierda es
                    // la DERECHA en el mapa. Comprobado con las rutas reales («pmapadetalle»): así casi nunca hay otra vía
                    // donde se dibuja el andén, y al revés lo había en más de la mitad.
                    int side = ((pd & 0x2) != 0 ? 2 : 0) | ((pd & 0x4) != 0 ? 1 : 0);
                    // el lado es respecto al sentido del andén (de su marca de inicio a la de fin); la línea va siempre en
                    // el sentido del tramo: si el andén va al revés, izquierda y derecha se cambian
                    if (SideFromStart && a.off > b.off && (side == 1 || side == 2)) side = 3 - side;
                    l.Side = side;
                    d.Platforms.Add(l);
                }
                foreach (var grp in E[k].Sidings.GroupBy(p => p.name))
                {
                    var offs = grp.Select(p => p.off).OrderBy(x => x).ToList();
                    int i = 0;
                    for (; i + 1 < offs.Count; i += 2)
                        if (offs[i + 1] - offs[i] > 5 && offs[i + 1] - offs[i] < 3000) { var l = MakeLine(g, k, offs[i], offs[i + 1]); l.Name = grp.Key; d.Sidings.Add(l); }
                        else { Lone(k, offs[i], grp.Key); i--; }   // la pareja no casa: el primero suelto y se sigue
                    if (i < offs.Count) Lone(k, offs[i], grp.Key);
                }
            }

            // ---- elementos sueltos
            (double, double) At(int k, double off) { var (x, y) = g.PointAt(k, off); return g.Unproj(x, y); }
            void Lone(int k, double off, string name)
            {
                var (la, lo) = At(k, off);
                // la otra marca del apartadero está en otro tramo: un solo rótulo por nombre y sitio
                if (d.SidingNames.Any(s => s.name == name && Meters(s.lat, s.lon, la, lo) < 800)) return;
                if (d.Sidings.Any(s => s.Name == name && Meters(s.Lat[s.Lat.Length / 2], s.Lon[s.Lon.Length / 2], la, lo) < 800)) return;
                d.SidingNames.Add((la, lo, name));
            }
            for (int k = 0; k < E.Length; k++)
            {
                foreach (var (off, v) in E[k].MilePosts) { var (la, lo) = At(k, off); d.Pk.Add((la, lo, v)); }
                // un cruce lleva una marca en cada una de sus dos vías: uno solo
                foreach (var off in E[k].Diamonds)
                {
                    var (la, lo) = At(k, off);
                    if (!d.Diamonds.Any(c => Meters(c.lat, c.lon, la, lo) < 4)) d.Diamonds.Add((la, lo));
                }
                foreach (var off in E[k].Pickups) { var (la, lo) = At(k, off); d.Pickups.Add((la, lo)); }
            }
            foreach (var (x, y) in g.Ends) d.Ends.Add(g.Unproj(x, y));
            return d;
        }

        static Line MakeLine(RouteGraph g, int k, double from, double to)
        {
            var e = g.Edges[k];
            var pts = new List<(double x, double y)> { g.PointAt(k, from) };
            for (int i = 0; i < e.Cum.Length; i++) if (e.Cum[i] > from && e.Cum[i] < to) pts.Add((e.X[i], e.Y[i]));
            pts.Add(g.PointAt(k, to));
            var l = new Line { Lat = new double[pts.Count], Lon = new double[pts.Count], MinLa = 90, MaxLa = -90, MinLo = 180, MaxLo = -180 };
            for (int i = 0; i < pts.Count; i++)
            {
                var (la, lo) = g.Unproj(pts[i].x, pts[i].y);
                l.Lat[i] = la; l.Lon[i] = lo;
                l.MinLa = Math.Min(l.MinLa, la); l.MaxLa = Math.Max(l.MaxLa, la); l.MinLo = Math.Min(l.MinLo, lo); l.MaxLo = Math.Max(l.MaxLo, lo);
            }
            return l;
        }

        public static double Meters(double la1, double lo1, double la2, double lo2)
        {
            double dy = (la2 - la1) * 111320, dx = (lo2 - lo1) * 111320 * Math.Cos(la1 * Math.PI / 180);
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
