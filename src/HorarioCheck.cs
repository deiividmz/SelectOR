// HORARIO DE LA SALA · COMPROBAR HORARIO (2026-11-11, petición). Antes de publicar los trenes IA, una estimación en la propia
// app (sin servidor) de lo que va a pasar con TODO el horario: cada salida de cada servicio recorrida por la red de la ruta.
//  · Camino: el mismo que dibuja la ventana (HorarioSalaForm.PlanRoute: con las vías marcadas y la vuelta). Sin camino,
//    paradas fuera del camino, vías marcadas que no existen, composición que no se puede leer.
//  · Tiempos: como los calcula el despachador para el horario de cada tren (Trafico.StopsLeg: 20 s de arrancar y frenar por
//    parada, 30/45/60 s de parada según el tipo, 4 min en el cambio de cabina), pero yendo a la velocidad que deja la vía
//    (los postes de velocidad de la ruta, en su sentido) y la del tren, un poco por debajo (90 %).
//  · Cuántos a la vez: el despachador los crea 4 min antes de salir y, si ya hay «como mucho» trenes IA, esa salida no se hace.
//  · Andenes: en cada estación, los trenes que están a la vez (salida, paradas, cambios de cabina, final) frente a las vías
//    con andén que tiene; y los movimientos por su acceso en 5 min.
//  · Vía única: los trayectos de una sola vía (Trayectos.Build), juntos cuando la estación de en medio no tiene dos vías para
//    cruzarse. Dos trenes que van a coincidir de frente en uno, con lo que tendría que esperar uno; y qué mover para que no.
//    Prioridad (2026-11-11, petición): entre viajeros y mercancías espera SIEMPRE el de mercancías (el despachador lo aplica),
//    así que se sugiere mover el de mercancías; y si un mercancías acumula mucha espera en su viaje, se avisa.
// Es una estimación, no la simulación del servidor: el tráfico real (jugadores, señales, esperas) puede cambiarlo.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SelectOR
{
    public static class HorarioCheck
    {
        static string Tr(string s) => I18n.T(s);

        public sealed class Issue
        {
            public int Sev;                       // 2 bloquea · 1 aviso · 0 bien / información
            public string Service = "", When = "", Where = "", What = "", Fix = "";
            public int Svc = -1;                  // el servicio (índice en Services)
            public int ShiftSvc = -1, ShiftMin;   // sugerencia: retrasar ShiftMin minutos todas las salidas de ShiftSvc
            public int EverySvc = -1, NewEvery;   // sugerencia: otra frecuencia
            public readonly List<(double lat, double lon)> Spots = new();
            public readonly List<int> Edges = new();
            public readonly List<int> Minutes = new();   // cuándo (para la línea de tiempo)
            public bool CanApply => ShiftSvc >= 0 && ShiftMin != 0 || EverySvc >= 0 && NewEvery > 0;
        }
        public sealed class Stay { public int T0, T1; public string Place; public bool Stop; }
        public sealed class Trip { public int Svc, Dep, Appear, End; public bool Dropped; public readonly List<Stay> Stays = new(); public readonly List<(int sec, int t0, int t1, string exit)> Single = new(), Line = new(); }
        public sealed class Section { public string Name; public readonly List<int> Edges = new(); public readonly List<(int e, double a, double b)> Items = new(); }
        public sealed class Result
        {
            public readonly List<Issue> Issues = new();
            public readonly List<Trip> Trips = new();
            public readonly int[] Active = new int[1440];
            public int Max; public bool Enabled;
            public List<HorarioSalaForm.Service> Services;
            public readonly List<Section> Sections = new();
            public readonly List<Section> Lines = new();   // cada vía de cada trayecto («Línea A – B, vía 2»)
            public int Bad => Issues.Count(i => i.Sev == 2);
            public int Warn => Issues.Count(i => i.Sev == 1);
        }
        public sealed class Input
        {
            public RouteGraph G; public List<HorarioSalaForm.Service> Services; public int Max; public bool Enabled = true;
            public Func<string, double> ConsistVmax;   // m/s del .con (0: no se puede leer)
        }

        // ── trayectos de la ruta (una vez por ruta: hacer los esquemas de las líneas tarda)
        static RouteGraph _trG; static List<Trayectos.Trayecto> _tr;
        public static List<Trayectos.Trayecto> TrayectosOf(RouteGraph g)
        {
            lock (typeof(HorarioCheck))
            {
                if (_trG == g && _tr != null) return _tr;
                List<Trayectos.Trayecto> t; try { t = Trayectos.Build(g, Schematic.Zones(g)); } catch { t = new(); }
                _trG = g; _tr = t; return t;
            }
        }

        static int Hms(string s) { var p = (s ?? "").Split(':'); return p.Length >= 2 && int.TryParse(p[0], out int h) && int.TryParse(p[1], out int m) ? h * 3600 + m * 60 : -1; }
        public static string T(int sec) { sec = ((sec % 86400) + 86400) % 86400; return $"{sec / 3600:00}:{sec / 60 % 60:00}"; }
        static string Veces(int n) => n == 1 ? Tr("una vez") : string.Format(Tr("{0} veces"), n);
        static string Dur(int sec) => sec >= 3600 ? $"{sec / 3600} h {sec / 60 % 60:00} min" : $"{Math.Max(1, (sec + 30) / 60)} min";
        static string Norm(string s)
        {
            var d = (s ?? "").Normalize(NormalizationForm.FormD); var sb = new StringBuilder();
            foreach (var c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString().Trim();
        }
        // vías con andén de un sitio (cuántos trenes caben parados a la vez)
        static readonly Dictionary<string, int> _cap = new(StringComparer.CurrentCultureIgnoreCase);
        static int Capacity(RouteGraph g, string place)
        {
            if (_cap.TryGetValue(place, out int c)) return c;
            c = Math.Max(1, HorarioSalaForm.PlaceSpots(g, place).Select(x => g.Edges[x.e].Vec).Distinct().Count());
            _cap[place] = c; return c;
        }

        // ── una salida «tipo» de un servicio: tiempos relativos a la salida
        sealed class Template
        {
            public HorarioSalaForm.RoutePlan Plan; public int End;   // s desde la salida hasta que acaba
            public readonly List<Stay> Stays = new();
            public readonly List<(int sec, int t0, int t1, string exit)> Single = new(), Line = new();
            public readonly HashSet<string> Passed = new(StringComparer.CurrentCultureIgnoreCase);
            public double Km;
        }

        public static Result Run(Input inp)
        {
            var g = inp.G; var res = new Result { Max = inp.Max, Enabled = inp.Enabled, Services = inp.Services };
            _cap.Clear();
            if (g == null) { res.Issues.Add(new Issue { Sev = 2, What = Tr("No se ha podido leer la ruta.") }); return res; }
            if (!inp.Enabled) res.Issues.Add(new Issue { Sev = 0, What = Tr("Los trenes IA están desactivados en los ajustes: no saldrá ninguno hasta activarlos.") });

            // vía única: los trayectos de una sola vía, juntos cuando la estación de en medio no tiene dos vías para cruzarse
            var trs = TrayectosOf(g).Where(t => t.Vias.Count == 1).ToList();
            var groupOf = Enumerable.Range(0, trs.Count).ToArray();
            int Find(int i) { while (groupOf[i] != i) i = groupOf[i] = groupOf[groupOf[i]]; return i; }
            for (int i = 0; i < trs.Count; i++)
                for (int j = i + 1; j < trs.Count; j++)
                    foreach (var st in new[] { trs[i].A, trs[i].B }.Intersect(new[] { trs[j].A, trs[j].B }))
                        if (Capacity(g, st) < 2) groupOf[Find(i)] = Find(j);
            var secIdx = new Dictionary<int, int>();
            var edgeSec = new Dictionary<int, List<(int sec, double a, double b, bool toB, string to, string from)>>();
            foreach (var grp in Enumerable.Range(0, trs.Count).GroupBy(Find))
            {
                var sec = new Section();
                var names = new List<string>();
                foreach (int i in grp) { names.Add(trs[i].A); names.Add(trs[i].B); }
                // el nombre: sus estaciones de los extremos (las que salen una vez)
                var ends = names.GroupBy(n => n).Where(q => q.Count() == 1).Select(q => q.Key).ToList();
                sec.Name = ends.Count == 2 ? ends[0] + " – " + ends[1] : string.Join(" – ", names.Distinct());
                // el sentido en toda la sección (no el de cada trayecto): hacia qué extremo va quien recorre un trayecto hacia su B.
                // Si no, dos trenes que van hacia el mismo lado por trayectos distintos de la sección parecían ir de frente
                var towardB = new Dictionary<int, string>();
                if (ends.Count == 2)
                {
                    string cur = ends[0]; var left = grp.ToList();
                    while (left.Count > 0)
                    {
                        int k = left.FindIndex(i => trs[i].A == cur || trs[i].B == cur); if (k < 0) break;
                        int i = left[k]; left.RemoveAt(k);
                        bool fwdB = trs[i].A == cur;
                        towardB[i] = fwdB ? ends[1] : ends[0];
                        cur = fwdB ? trs[i].B : trs[i].A;
                    }
                }
                foreach (int i in grp)
                {
                    var t = trs[i];
                    foreach (var it in t.Vias[0].Items)
                    {
                        sec.Edges.Add(it.Edge); sec.Items.Add((it.Edge, it.From, it.To));
                        if (!edgeSec.TryGetValue(it.Edge, out var l)) edgeSec[it.Edge] = l = new();
                        string to = it.ToB ? t.B : t.A, from = it.ToB ? t.A : t.B;
                        if (towardB.TryGetValue(i, out var eb)) { string ea = eb == ends[1] ? ends[0] : ends[1]; to = it.ToB ? eb : ea; from = it.ToB ? ea : eb; }
                        l.Add((res.Sections.Count, it.From, it.To, it.ToB, to, from));
                    }
                }
                res.Sections.Add(sec);
            }

            // todas las vías de los trayectos (de una o de dos vías): para los que van pegados o se alcanzan
            var edgeLine = new Dictionary<int, List<(int sec, double a, double b, bool toB, string to, string from)>>();
            foreach (var t in TrayectosOf(g))
                for (int k = 0; k < t.Vias.Count; k++)
                {
                    var sec = new Section { Name = t.Name.Replace("Línea ", "") + (t.Vias.Count > 1 ? ", " + Tr("vía") + " " + (k + 1) : "") };
                    foreach (var it in t.Vias[k].Items)
                    {
                        sec.Edges.Add(it.Edge); sec.Items.Add((it.Edge, it.From, it.To));
                        if (!edgeLine.TryGetValue(it.Edge, out var l)) edgeLine[it.Edge] = l = new();
                        l.Add((res.Lines.Count, it.From, it.To, it.ToB, it.ToB ? t.B : t.A, it.ToB ? t.A : t.B));
                    }
                    res.Lines.Add(sec);
                }

            // cada servicio: su salida «tipo»
            var tpl = new Template[inp.Services.Count];
            for (int si = 0; si < inp.Services.Count; si++)
            {
                var s = inp.Services[si];
                if (!s.Active) continue;
                Issue Add(int sev, string what, string fix = "") { var x = new Issue { Sev = sev, Svc = si, Service = s.Name, What = what, Fix = fix }; res.Issues.Add(x); return x; }
                if (HorarioSalaForm.PlaceSpots(g, s.Origin).Count == 0) { Add(2, string.Format(Tr("El origen «{0}» no está en la ruta."), s.Origin), Tr("Elige otro origen en su itinerario.")); continue; }
                if (HorarioSalaForm.PlaceSpots(g, s.Destination).Count == 0) { Add(2, string.Format(Tr("El destino «{0}» no está en la ruta."), s.Destination), Tr("Elige otro destino en su itinerario.")); continue; }
                var plan = HorarioSalaForm.PlanRoute(g, s.Origin, s.Destination, s.Hint, s.Trip);
                if (plan == null) { Add(2, string.Format(Tr("No hay vía de {0} a {1}: el tren no puede salir."), s.Origin, s.Destination), Tr("Revisa el origen, el destino y las vías marcadas.")); continue; }
                foreach (int v in plan.MissingVias) Add(1, string.Format(Tr("La vía marcada {0} ya no existe en la ruta: el despachador no la tiene en cuenta."), v), Tr("Quítala del itinerario."));
                if ((s.Trip == "vuelta" || s.Trip == "circular") && plan.TurnAt < 0) Add(1, string.Format(Tr("No hay vía para volver de {0} a {1}: hace solo la ida."), s.Destination, s.Origin));
                double vmax = inp.ConsistVmax?.Invoke(s.Consist) ?? 0;
                if (vmax <= 0)
                {
                    Add(1, string.Format(Tr("No se puede leer la composición «{0}»: se calcula con la velocidad típica de su tipo."), s.Consist));
                    vmax = s.Kind == "larga" ? 55.6 : s.Kind == "media" ? 44.4 : s.Kind == "mercancias" ? 25 : 33.3;
                }
                var tp = Build(g, s, plan, vmax, edgeSec, edgeLine);
                tpl[si] = tp;
                // paradas pedidas que no están en el camino
                if (s.Stops != "todas" && s.Stops != "ninguna" && s.Kind != "mercancias")
                    foreach (var w in s.Stops.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
                        if (!tp.Passed.Any(p => Norm(p) == Norm(w) || Norm(p).StartsWith(Norm(w)) || MainMenuForm.SamePlace(p, w)))
                            Add(1, string.Format(Tr("La parada «{0}» no está en su camino: no parará ahí."), w), Tr("Márcala como vía de paso en el itinerario o quítala."))
                                .Spots.AddRange(SpotsLL(g, w));
            }

            // las salidas del día
            for (int si = 0; si < inp.Services.Count; si++)
            {
                var s = inp.Services[si]; var tp = tpl[si]; if (tp == null) continue;
                int first = Hms(s.First), last = Hms(s.Last); if (first < 0) continue; if (last < first) last = first;
                int every = Math.Max(0, s.Every) * 60;
                for (int dep = first; dep <= last; dep += every > 0 ? every : 86400)
                {
                    var tr = new Trip { Svc = si, Dep = dep, Appear = dep - 120, End = dep + tp.End };
                    foreach (var st in tp.Stays) tr.Stays.Add(new Stay { T0 = dep + st.T0, T1 = dep + st.T1, Place = st.Place, Stop = st.Stop });
                    foreach (var w in tp.Single) tr.Single.Add((w.sec, dep + w.t0, dep + w.t1, w.exit));
                    foreach (var w in tp.Line) tr.Line.Add((w.sec, dep + w.t0, dep + w.t1, w.exit));
                    res.Trips.Add(tr);
                    if (every <= 0) break;
                }
                // cuántos del mismo servicio a la vez (información)
                int dur = tp.End + 120;
                string kmTxt = tp.Km.ToString("0", CultureInfo.CurrentCulture);
                if (every > 0 && dur > every)
                    res.Issues.Add(new Issue { Sev = 0, Svc = si, Service = s.Name, What = string.Format(Tr("Tarda {0} ({1} km) y sale cada {2} min: habrá {3} trenes de este servicio a la vez."), Dur(tp.End), kmTxt, s.Every, (dur + every - 1) / every) });
            }

            // como mucho N a la vez: el despachador los crea 4 min antes de salir; si ya hay N, esa salida no se hace
            var byCreate = res.Trips.OrderBy(t => t.Dep - 240).ToList();
            var alive = new List<Trip>();
            var dropped = new Dictionary<int, List<int>>();
            foreach (var t in byCreate)
            {
                int now = t.Dep - 240;
                alive.RemoveAll(x => x.End + 60 <= now);
                if (inp.Max <= 0 || alive.Count >= inp.Max)
                {
                    t.Dropped = true;
                    if (!dropped.TryGetValue(t.Svc, out var l)) dropped[t.Svc] = l = new(); l.Add(t.Dep);
                    continue;
                }
                alive.Add(t);
            }
            foreach (var t in res.Trips.Where(x => !x.Dropped))
                for (int m = Math.Max(0, (t.Dep - 240) / 60); m <= Math.Min(1439, (t.End + 60) / 60); m++) res.Active[m]++;
            foreach (var kv in dropped)
            {
                var s = inp.Services[kv.Key];
                var x = new Issue { Sev = 1, Svc = kv.Key, Service = s.Name, When = string.Join(", ", kv.Value.Take(6).Select(T)) + (kv.Value.Count > 6 ? " …" : ""),
                                    What = string.Format(Tr("{0} salidas no se harán: ya habrá {1} trenes IA (el máximo de los ajustes)."), kv.Value.Count, inp.Max),
                                    Fix = Tr("Sube «Como mucho a la vez», alarga la frecuencia o escalona los servicios.") };
                x.Minutes.AddRange(kv.Value.Select(d => d / 60));
                res.Issues.Add(x);
            }
            var live = res.Trips.Where(t => !t.Dropped).ToList();

            // muchas salidas a la vez en toda la ruta
            var deps = live.OrderBy(t => t.Dep).ToList();
            var saidBurst = new HashSet<int>();
            for (int i = 0; i < deps.Count; i++)
            {
                var win = deps.Where(t => t.Dep >= deps[i].Dep && t.Dep < deps[i].Dep + 300).ToList();
                if (win.Count < 5 || !saidBurst.Add(deps[i].Dep / 1800)) continue;
                var x = new Issue { Sev = win.Count >= 8 ? 2 : 1, When = T(deps[i].Dep) + "–" + T(deps[i].Dep + 300), Where = Tr("toda la ruta"),
                                    What = string.Format(Tr("Salen {0} trenes en 5 minutos ({1}): las estaciones y los accesos se llenan a la vez."), win.Count, string.Join(", ", win.Select(t => inp.Services[t.Svc].Name).Distinct())),
                                    Fix = Tr("Escalona las primeras salidas unos minutos (p. ej. 2–3 min entre servicios).") };
                // la sugerencia: el que menos tarda, 3 min más tarde
                var shift = win.Skip(1).OrderBy(t => tpl[t.Svc].End).FirstOrDefault();
                if (shift != null) { x.ShiftSvc = shift.Svc; x.ShiftMin = 3; x.Fix += " " + string.Format(Tr("Por ejemplo: retrasar 3 min «{0}»."), inp.Services[shift.Svc].Name); }
                x.Minutes.Add(deps[i].Dep / 60);
                res.Issues.Add(x);
            }

            // andenes: trenes parados a la vez en cada estación frente a sus vías con andén
            foreach (var grp in live.SelectMany(t => t.Stays.Where(s => s.Stop).Select(s => (t, s))).GroupBy(x => x.s.Place, StringComparer.CurrentCultureIgnoreCase))
            {
                int cap = Capacity(g, grp.Key);
                var ev = new List<(int t, int d, int svc)>();
                foreach (var (t, s) in grp) { ev.Add((s.T0, 1, t.Svc)); ev.Add((s.T1 + 1, -1, t.Svc)); }
                int n = 0, worst = 0, worstAt = -1; var over = new List<int>();
                foreach (var e in ev.OrderBy(e => e.t).ThenBy(e => e.d))
                {
                    n += e.d;
                    if (n > cap) { if (over.Count == 0 || over[^1] != e.t / 60) over.Add(e.t / 60); if (n > worst) { worst = n; worstAt = e.t; } }
                }
                if (over.Count == 0) continue;
                var who = grp.Where(x => x.s.T0 <= worstAt && x.s.T1 >= worstAt).Select(x => inp.Services[x.t.Svc].Name).Distinct().ToList();
                var iss = new Issue { Sev = worst > cap + 1 ? 2 : 1, Where = grp.Key, When = string.Join(", ", over.Take(6).Select(m => T(m * 60))) + (over.Count > 6 ? " …" : ""),
                                      What = string.Format(Tr("Coinciden {0} trenes parados en {1} y tiene {2} vías con andén ({3}): el que llega espera fuera."), worst, grp.Key, cap, string.Join(", ", who)),
                                      Fix = Tr("Separa sus horas en esa estación o quita alguna parada ahí.") };
                // la sugerencia: el que llega último, unos minutos más tarde
                var lastIn = grp.Where(x => x.s.T0 <= worstAt && x.s.T1 >= worstAt).OrderByDescending(x => x.s.T0).FirstOrDefault();
                if (lastIn.t != null)
                {
                    var other = grp.Where(x => x.s.T0 <= worstAt && x.s.T1 >= worstAt && x.t != lastIn.t).Select(x => x.s.T1).DefaultIfEmpty(worstAt).Min();
                    int k = Math.Max(1, Math.Min(20, (other - lastIn.s.T0) / 60 + 1));
                    if (lastIn.t.Svc >= 0) { iss.ShiftSvc = lastIn.t.Svc; iss.ShiftMin = k; iss.Fix += " " + string.Format(Tr("Por ejemplo: retrasar {0} min «{1}»."), k, inp.Services[lastIn.t.Svc].Name); }
                }
                iss.Spots.AddRange(SpotsLL(g, grp.Key)); iss.Minutes.AddRange(over);
                res.Issues.Add(iss);
            }

            // accesos: movimientos (llegadas, salidas y pasos) en 5 min por estación
            foreach (var grp in live.SelectMany(t => t.Stays.Select(s => (t, s))).GroupBy(x => x.s.Place, StringComparer.CurrentCultureIgnoreCase))
            {
                int cap = Capacity(g, grp.Key), lim = Math.Max(6, cap + 2);
                var moves = grp.SelectMany(x => x.s.Stop ? new[] { x.s.T0, x.s.T1 } : new[] { x.s.T0 }).OrderBy(x => x).ToList();
                int best = 0, at = -1;
                for (int i = 0, j = 0; i < moves.Count; i++) { while (moves[i] - moves[j] > 300) j++; if (i - j + 1 > best) { best = i - j + 1; at = moves[j]; } }
                if (best <= lim) continue;
                var iss = new Issue { Sev = 1, Where = grp.Key, When = T(at) + "–" + T(at + 300),
                                      What = string.Format(Tr("{0} movimientos (llegadas, salidas y pasos) por el acceso de {1} en 5 minutos: acceso saturado."), best, grp.Key),
                                      Fix = Tr("Escalona los servicios que pasan por ahí a esa hora.") };
                iss.Spots.AddRange(SpotsLL(g, grp.Key)); iss.Minutes.Add(at / 60);
                res.Issues.Add(iss);
            }

            // vía única: de frente a la vez. Espera el que entra el segundo; entre viajeros y mercancías, el de mercancías
            // (prioridad de los viajeros: si el mercancías ya iba a entrar, se queda a la entrada hasta que salga el de viajeros)
            bool Fr(Trip t) => inp.Services[t.Svc].Kind == "mercancias";
            var freightWait = new Dictionary<Trip, (int total, int n, int first)>();
            for (int sec = 0; sec < res.Sections.Count; sec++)
            {
                var w = live.SelectMany(t => t.Single.Where(x => x.sec == sec).Select(x => (t, x.t0, x.t1, x.exit))).OrderBy(x => x.t0).ToList();
                var pairs = new Dictionary<(int, int), List<(int at, int wait, int waiter)>>();
                for (int i = 0; i < w.Count; i++)
                    for (int j = i + 1; j < w.Count && w[j].t0 < w[i].t1; j++)
                        if (w[i].exit != w[j].exit && w[i].t != w[j].t)
                        {
                            var key = (Math.Min(w[i].t.Svc, w[j].t.Svc), Math.Max(w[i].t.Svc, w[j].t.Svc));
                            if (!pairs.TryGetValue(key, out var l)) pairs[key] = l = new();
                            bool prio = Fr(w[i].t) && !Fr(w[j].t);       // el primero es de mercancías y el segundo de viajeros: espera el mercancías
                            var waiter = prio ? w[i].t : w[j].t;
                            int wait = prio ? w[j].t1 - w[i].t0 : w[i].t1 - w[j].t0;
                            l.Add((prio ? w[i].t0 : w[j].t0, wait, waiter.Svc));
                            if (Fr(waiter)) { freightWait.TryGetValue(waiter, out var fw); freightWait[waiter] = (fw.total + wait, fw.n + 1, fw.n == 0 ? (prio ? w[i].t0 : w[j].t0) : fw.first); }
                        }
                foreach (var kv in pairs)
                {
                    var (a, b) = kv.Key; var l = kv.Value;
                    int maxWait = l.Max(x => x.wait);
                    bool fa = inp.Services[a].Kind == "mercancias", fb = inp.Services[b].Kind == "mercancias", mixed = a != b && fa != fb;
                    int fsv = mixed ? (fa ? a : b) : -1;
                    string names = a == b ? "«" + inp.Services[a].Name + "»" : "«" + inp.Services[a].Name + "» " + Tr("y") + " «" + inp.Services[b].Name + "»";
                    var iss = new Issue { Sev = maxWait > 600 && !mixed ? 2 : 1, Where = Tr("Vía única") + " " + res.Sections[sec].Name, When = string.Join(", ", l.Take(6).Select(x => T(x.at))) + (l.Count > 6 ? " …" : ""),
                                          Service = a == b ? inp.Services[a].Name : inp.Services[a].Name + " · " + inp.Services[b].Name,
                                          What = mixed
                                              ? string.Format(Tr("{0} se cruzan de frente {1} en la vía única {2}, sin sitio para cruzarse: los viajeros tienen prioridad, así que espera «{3}» (hasta {4})."), names, Veces(l.Count), res.Sections[sec].Name, inp.Services[fsv].Name, Dur(maxWait))
                                              : string.Format(Tr("{0} se cruzan de frente {1} en la vía única {2}, sin sitio para cruzarse: uno espera hasta {3}."), names, Veces(l.Count), res.Sections[sec].Name, Dur(maxWait))
                                                 + (maxWait > 600 ? " " + Tr("Más de 10 min: el despachador lo retiraría.") : "") };
                    iss.Edges.AddRange(res.Sections[sec].Edges); iss.Minutes.AddRange(l.Select(x => x.at / 60));
                    if (a != b)
                    {
                        // qué retrasar: el de mercancías si uno lo es (los viajeros no se tocan); si no, el que entra el segundo
                        int mv = mixed ? fsv : b, other = mv == a ? b : a; int k = ShiftFor(live, sec, mv, other);
                        if (k > 0) { iss.ShiftSvc = mv; iss.ShiftMin = k; iss.Fix = string.Format(Tr("Retrasa {0} min todas las salidas de «{1}»."), k, inp.Services[mv].Name); }
                        else iss.Fix = mixed ? string.Format(Tr("Cambia la hora de «{0}» (el de mercancías) o haz que pase por otra vía."), inp.Services[mv].Name)
                                             : Tr("Cambia la hora de uno de los dos o haz que pase por otra vía.");
                    }
                    else
                    {
                        int ne = EveryFor(tpl[a], sec, inp.Services[a].Every);
                        if (ne > 0) { iss.EverySvc = a; iss.NewEvery = ne; iss.Fix = string.Format(Tr("El tren de vuelta se cruza con el siguiente: cada {0} min en vez de {1}."), ne, inp.Services[a].Every); }
                        else iss.Fix = Tr("El tren de vuelta se cruza con el siguiente: espacia más las salidas.");
                    }
                    res.Issues.Add(iss);
                }
            }

            // mercancías que acumulan mucha espera por ceder el paso a los viajeros en las vías únicas (por servicio: la peor salida)
            foreach (var grp in freightWait.GroupBy(kv => kv.Key.Svc))
            {
                var worst = grp.OrderByDescending(kv => kv.Value.total).First();
                int tot = worst.Value.total; if (tot < 300) continue;
                var s0 = inp.Services[grp.Key];
                var iss = new Issue { Sev = tot > 1200 ? 2 : 1, Svc = grp.Key, Service = s0.Name, When = T(worst.Key.Dep),
                                      Where = Tr("Vía única"),
                                      What = string.Format(Tr("«{0}» (mercancías) cede el paso a los viajeros en las vías únicas: la salida de las {1} espera unos {2} en total ({3}); {4} salidas esperan."),
                                                           s0.Name, T(worst.Key.Dep), Dur(tot), Veces(worst.Value.n), grp.Count())
                                           + (tot > 1200 ? " " + Tr("Tanta espera puede hacer que el despachador lo retire o que llegue muy tarde.") : ""),
                                      Fix = Tr("Muévelo a horas con menos viajeros, espacia sus salidas o hazlo pasar por vías dobles.") };
                iss.Minutes.AddRange(grp.Select(kv => kv.Key.Dep / 60));
                res.Issues.Add(iss);
            }

            // por la misma vía y hacia el mismo lado: pegados (menos de 2,5 min) o uno alcanza al de delante (más rápido detrás de
            // uno que para): el de detrás espera ante las señales. Por parejas de servicios, con el primer sitio donde pasa
            var follow = new Dictionary<(int first, int second), List<(int at, int line, int need)>>();
            for (int li = 0; li < res.Lines.Count; li++)
            {
                var w = live.SelectMany(t => t.Line.Where(x => x.sec == li).Select(x => (t, x.t0, x.t1, x.exit))).OrderBy(x => x.t0).ToList();
                for (int i = 0; i < w.Count; i++)
                    for (int j = i + 1; j < w.Count && w[j].t0 < w[i].t1; j++)
                    {
                        if (w[i].exit != w[j].exit || w[i].t == w[j].t) continue;
                        bool close = w[j].t0 - w[i].t0 < 150, catches = w[j].t1 < w[i].t1 + 60;
                        if (!close && !catches) continue;
                        int need = Math.Max(150 - (w[j].t0 - w[i].t0), w[i].t1 + 120 - w[j].t1);
                        var key = (w[i].t.Svc, w[j].t.Svc);
                        if (!follow.TryGetValue(key, out var l)) follow[key] = l = new();
                        l.Add((w[j].t0, li, need));
                    }
            }
            foreach (var kv in follow)
            {
                var (a, b) = kv.Key; var l = kv.Value.OrderBy(x => x.at).ToList();
                var once = l.GroupBy(x => x.at / 600).Select(q => q.First()).ToList();   // (una vez por salida: la primera vía donde pasa)
                int k = Math.Max(1, Math.Min(20, (l.Max(x => x.need) + 59) / 60));
                string sa = inp.Services[a].Name, sb = inp.Services[b].Name;
                var iss = new Issue { Sev = 1, Service = a == b ? sa : sa + " · " + sb, Where = res.Lines[once[0].line].Name,
                                      When = string.Join(", ", once.Take(6).Select(x => T(x.at))) + (once.Count > 6 ? " …" : ""),
                                      What = a == b ? string.Format(Tr("Dos trenes de «{0}» van pegados por la misma vía ({1}): el segundo espera ante las señales."), sa, Veces(once.Count))
                                                    : string.Format(Tr("«{1}» va pegado a «{0}» o lo alcanza por la misma vía y hacia el mismo lado ({2}, la primera por {3}): espera detrás ante las señales."), sa, sb, Veces(once.Count), res.Lines[once[0].line].Name) };
                if (a != b) { iss.ShiftSvc = b; iss.ShiftMin = k; iss.Fix = string.Format(Tr("Retrasa {0} min todas las salidas de «{1}»."), k, sb); }
                else iss.Fix = Tr("Espacia más sus salidas.");
                foreach (var x in once) iss.Minutes.Add(x.at / 60);
                iss.Edges.AddRange(res.Lines[once[0].line].Edges);
                res.Issues.Add(iss);
            }

            // nudos: 3 trenes o más pasan por la misma estación en 2 minutos (se cruzan en sus agujas, una entrada para todos)
            foreach (var grp in live.SelectMany(t => t.Stays.Select(st => (t, st))).GroupBy(x => x.st.Place, StringComparer.CurrentCultureIgnoreCase))
            {
                var ts = grp.Select(x => (at: x.st.T0, svc: x.t.Svc)).OrderBy(x => x.at).ToList();
                var hits = new List<(int at, List<int> svcs)>();
                for (int i = 0, j = 0; i < ts.Count; i++)
                {
                    while (ts[i].at - ts[j].at > 120) j++;
                    if (i - j + 1 >= 3 && (hits.Count == 0 || ts[j].at - hits[^1].at > 600)) hits.Add((ts[j].at, ts.Skip(j).Take(i - j + 1).Select(x => x.svc).Distinct().ToList()));
                }
                if (hits.Count == 0) continue;
                var h0 = hits[0];
                var iss = new Issue { Sev = 1, Where = grp.Key, When = string.Join(", ", hits.Take(6).Select(x => T(x.at))) + (hits.Count > 6 ? " …" : ""),
                                      Service = string.Join(" · ", h0.svcs.Select(x => inp.Services[x].Name)),
                                      What = string.Format(Tr("Varios trenes pasan por {0} en 2 minutos ({1}): se cruzan en sus agujas y alguno espera."), grp.Key, string.Join(", ", h0.svcs.Select(x => inp.Services[x].Name))),
                                      Fix = Tr("Escalona unos minutos uno de ellos.") };
                if (h0.svcs.Count > 0) { var mv = h0.svcs[^1]; iss.ShiftSvc = mv; iss.ShiftMin = 3; iss.Fix = string.Format(Tr("Por ejemplo: retrasar 3 min «{0}»."), inp.Services[mv].Name); }
                iss.Spots.AddRange(SpotsLL(g, grp.Key)); iss.Minutes.AddRange(hits.Select(x => x.at / 60));
                res.Issues.Add(iss);
            }

            // los que van bien
            for (int si = 0; si < inp.Services.Count; si++)
            {
                var s = inp.Services[si];
                if (!s.Active) { res.Issues.Add(new Issue { Sev = 0, Svc = si, Service = s.Name, What = Tr("Desactivado: no sale.") }); continue; }
                if (tpl[si] == null || res.Issues.Any(x => x.Sev > 0 && (x.Svc == si || x.ShiftSvc == si))) continue;
                int n = res.Trips.Count(t => t.Svc == si && !t.Dropped);
                res.Issues.Add(new Issue { Sev = 0, Svc = si, Service = s.Name, What = string.Format(Tr("Bien: {0} salidas, {1} km, tarda {2}."), n, tpl[si].Km.ToString("0", CultureInfo.CurrentCulture), Dur(tpl[si].End)) });
            }
            res.Issues.Sort((x, y) => y.Sev != x.Sev ? y.Sev.CompareTo(x.Sev) : string.Compare(x.Service, y.Service, StringComparison.CurrentCulture));
            return res;
        }

        // los minutos que hay que retrasar todas las salidas de «svc» para que no coincida de frente con «other» en la vía única
        static int ShiftFor(List<Trip> live, int sec, int svc, int other)
        {
            var mine = live.Where(t => t.Svc == svc).SelectMany(t => t.Single.Where(x => x.sec == sec)).ToList();
            var his = live.Where(t => t.Svc == other).SelectMany(t => t.Single.Where(x => x.sec == sec)).ToList();
            for (int k = 1; k <= 30; k++)
            {
                int d = k * 60; bool clash = false;
                foreach (var m in mine)
                {
                    foreach (var h in his) if (m.exit != h.exit && m.t0 + d < h.t1 && h.t0 < m.t1 + d) { clash = true; break; }
                    if (clash) break;
                }
                if (!clash) return k;
            }
            return 0;
        }

        // la frecuencia (min) con la que la vuelta de un tren no coincide de frente con el siguiente en la vía única
        static int EveryFor(Template tp, int sec, int every)
        {
            if (tp == null) return 0;
            var w = tp.Single.Where(x => x.sec == sec).ToList();
            for (int e = Math.Max(5, every + 5); e <= 180; e += 5)
            {
                bool clash = false;
                for (int m = 1; m <= 12 && !clash; m++)
                    foreach (var a in w) { foreach (var b in w) if (a.exit != b.exit && a.t0 < b.t1 + m * e * 60 && b.t0 + m * e * 60 < a.t1) { clash = true; break; } if (clash) break; }
                if (!clash) return e;
            }
            return 0;
        }

        static IEnumerable<(double lat, double lon)> SpotsLL(RouteGraph g, string place)
        {
            foreach (var (e, off) in HorarioSalaForm.PlaceSpots(g, place).Take(12)) { var (x, y) = g.PointAt(e, off); yield return g.Unproj(x, y); }
        }

        // ── recorrer el camino de un servicio con sus tiempos (relativos a la salida)
        static Template Build(RouteGraph g, HorarioSalaForm.Service s, HorarioSalaForm.RoutePlan plan, double vmax, Dictionary<int, List<(int sec, double a, double b, bool toB, string to, string from)>> edgeSec,
                              Dictionary<int, List<(int sec, double a, double b, bool toB, string to, string from)>> edgeLine)
        {
            var tp = new Template { Plan = plan };
            bool freight = s.Kind == "mercancias";
            var wanted = (s.Stops ?? "").Split(',').Select(Norm).Where(x => x.Length > 0).ToList();
            bool all = wanted.Count == 1 && wanted[0] == "todas", none = freight || wanted.Count == 1 && wanted[0] == "ninguna";
            int dwell = s.Kind == "larga" ? 60 : s.Kind == "media" ? 45 : 30;
            double t = 0, dist = 0, lim = vmax;
            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            tp.Stays.Add(new Stay { T0 = -120, T1 = 0, Place = s.Origin, Stop = true });
            seen.Add(Norm(s.Origin));
            var tl = new List<(double d, double t)> { (0, 0) };
            bool Wanted(string n) => !none && (all || wanted.Any(w => Norm(n) == w || Norm(n).StartsWith(w)));
            void StopAt(string n, int extra)
            {
                double v = Math.Min(vmax, lim);
                t += 20 + Math.Min(60, v / 0.6);
                double t0 = t; t += extra;
                tp.Stays.Add(new Stay { T0 = (int)t0, T1 = (int)t, Place = n, Stop = true });
                tl.Add((dist, t));
            }
            for (int pi = 0; pi < plan.Parts.Count; pi++)
            {
                if (pi == plan.TurnAt)
                {
                    // fin de la ida: el destino (cambio de cabina o, en la circular, una parada si es de las suyas)
                    if (s.Trip == "vuelta") StopAt(s.Destination, freight ? 60 : Math.Max(dwell, 240));
                    else if (!freight) StopAt(s.Destination, dwell);
                    seen.Clear(); seen.Add(Norm(s.Destination));
                }
                var (e, a, b) = plan.Parts[pi];
                var E = g.Edges[e];
                bool fwd = b >= a; double lo = Math.Min(a, b), hi = Math.Max(a, b);
                // lo que hay en este trozo, en el orden de la marcha: postes de velocidad (de su sentido) y andenes
                var evs = new List<(double at, int kind, double kmh, string name)>();
                foreach (var (off, kmh, dir) in E.LimitDirs) if (off >= lo && off <= hi && (dir == 0 || dir == (fwd ? 1 : -1))) evs.Add((fwd ? off - a : a - off, 0, kmh, null));
                foreach (var (off, n) in E.Platforms.Concat(E.Sidings)) if (off >= lo && off <= hi && !string.IsNullOrWhiteSpace(n)) evs.Add((fwd ? off - a : a - off, 1, 0, n.Trim()));
                double pos = 0;
                foreach (var ev in evs.OrderBy(x => x.at))
                {
                    double seg = ev.at - pos; if (seg > 0) { t += seg / (0.9 * Math.Min(vmax, lim)); dist += seg; pos = ev.at; tl.Add((dist, t)); }
                    if (ev.kind == 0) { lim = Math.Max(2, ev.kmh / 3.6); continue; }
                    string n = ev.name;
                    tp.Passed.Add(n);
                    bool isEnd = MainMenuForm.SamePlace(n, plan.TurnAt >= 0 && pi >= plan.TurnAt ? s.Origin : s.Destination);
                    if (isEnd || !seen.Add(Norm(n))) continue;
                    if (Wanted(n)) StopAt(n, dwell);
                    else tp.Stays.Add(new Stay { T0 = (int)t, T1 = (int)t, Place = n, Stop = false });
                }
                double rest = hi - lo - pos; if (rest > 0) { t += rest / (0.9 * Math.Min(vmax, lim)); dist += rest; tl.Add((dist, t)); }
                // vía única (y cada vía de los trayectos): cuándo entra y sale de cada una de este tramo
                foreach (var (map, list) in new[] { (edgeSec, tp.Single), (edgeLine, tp.Line) })
                    if (map.TryGetValue(e, out var secs))
                        foreach (var (sec, sa, sb, toB, to, from) in secs)
                        {
                            double o0 = Math.Max(lo, sa), o1 = Math.Min(hi, sb);
                            if (o1 - o0 < 20) continue;
                            double d0 = dist - (hi - lo) + (fwd ? o0 - a : a - o1), d1 = dist - (hi - lo) + (fwd ? o1 - a : a - o0);
                            string exit = fwd == toB ? to : from;
                            AddWin(list, sec, (int)TimeAt(tl, d0, true), (int)TimeAt(tl, d1, false), exit);
                        }
            }
            // final
            if (plan.TurnAt < 0) { t += 20 + Math.Min(60, Math.Min(vmax, lim) / 0.6); tp.Stays.Add(new Stay { T0 = (int)t, T1 = (int)t + 120, Place = s.Destination, Stop = true }); }
            else { t += 20 + Math.Min(60, Math.Min(vmax, lim) / 0.6); tp.Stays.Add(new Stay { T0 = (int)t, T1 = (int)t + 120, Place = s.Origin, Stop = true }); }
            tp.End = (int)t + 120;
            tp.Km = plan.Length / 1000;
            return tp;
        }

        // un tren por los trayectos de una sección de vía única: una ventana por paso (se juntan los trozos seguidos)
        static void AddWin(List<(int sec, int t0, int t1, string exit)> l, int sec, int t0, int t1, string exit)
        {
            if (l.Count > 0)
            {
                var w = l[^1];
                if (w.sec == sec && (w.exit == exit || t0 - w.t1 < 120)) { l[^1] = (sec, Math.Min(w.t0, t0), Math.Max(w.t1, t1), exit); return; }
            }
            l.Add((sec, t0, t1, exit));
        }

        static double TimeAt(List<(double d, double t)> tl, double d, bool first)
        {
            for (int i = 1; i < tl.Count; i++)
            {
                if (tl[i].d < d - 1e-6) continue;
                var (d0, t0) = tl[i - 1]; var (d1, t1) = tl[i];
                if (!first) while (i + 1 < tl.Count && tl[i + 1].d <= d + 1e-6) { i++; (d1, t1) = tl[i]; }
                return d1 - d0 < 1e-6 ? t1 : t0 + (t1 - t0) * (d - d0) / (d1 - d0);
            }
            return tl[^1].t;
        }
    }
}
