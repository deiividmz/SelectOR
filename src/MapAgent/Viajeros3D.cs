// (1.2.85, prueba, petición) Gente en los andenes, en el 3D de Open Rails: suben y bajan de tu tren.
//  · Personas realistas animadas de SelectOR (Microsoft Rocketbox, licencia MIT; SELECTOR_GENTE_DIR): valen para cualquier
//    ruta. Cada movimiento son N posturas .s: andar avanza con lo andado (los pies no patinan); esperar va y vuelve. En cada
//    sesión se carga una selección al azar (andar y esperar al principio; las otras esperas, poco a poco en el hilo de carga).
//    Sin esa carpeta, las figuras fijas de la CGL si la ruta las trae (SHAPES\ani33-1.s y compañía).
//  · La estación se ve venir: el andén siguiente del camino del tren (a menos de 650 m) se MIDE en el escenario (suelos de los
//    modelos cercanos entre 0,2 y 1,4 m sobre el carril, a los dos lados de la vía) y se llena con la gente que dice la RUTA:
//    los viajeros esperando de cada andén (PlatformDetails.NumPassengersWaiting; la actividad puede cambiarlos), de su lado
//    (PlatformSide, con la regla de Open Rails para sus trenes: SideOf). Como mínimo, uno cada ~8 m de andén (las rutas suelen
//    poner muy pocos).
//  · El tren llega: lo miran; parado, se acercan a las puertas. Puertas abiertas: por cada puerta bajan primero (desde dentro
//    del coche) y suben después los que esperan cerca, uno detrás de otro. Cuántos, según el TREN: sus plazas (PassengerCapacity
//    de cada .wag; si no las dice, por su largo) y los que van a bordo (al empezar, entre un 30 y un 60 %; en cada parada baja
//    entre un 15 y un 40 % y suben los que caben).
//  · Con el contador de viajeros de SelectOR (en servicio y en Conducción libre; «PAX …» por el enlace local, LocalLink): bajan
//    y suben exactamente los que cuenta, al mismo compás (cada paso del contador, una persona por la puerta); si esperaban
//    menos de los que suben, llegan andando los que faltan. Sin sus cifras en 6 s, las propias.
//  · Al irse el tren: la gente sigue en el andén hasta que está a más de 1 km.
// Dibujo: los modelos se piden al gestor de Open Rails en el hilo de carga (World.Load / Viewer.Mark), se marcan como en uso
// para que no se descarguen y en cada fotograma se dibuja cada persona (World.PrepareFrame → SharedShape.PrepareFrame). Por
// reflexión: vale para los Open Rails de .NET 6 y de .NET Framework.
// Solo si SelectOR lo pide al lanzar (SELECTOR_VIAJEROS3D=1: Ajustes → Gráficos, «Gente en los andenes»).

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Orts.Formats.Msts;
using Orts.MultiPlayer;
using Orts.Simulation;
using Orts.Simulation.Physics;
using Orts.Simulation.RollingStocks;
using ORTS.Common;

namespace SelectOR.MapAgent
{
    public static partial class Viajeros3D
    {
        // figuras sueltas de la CGL (si no hay personas de SelectOR)
        static readonly string[] Models =
        {
            "ANI12-01.s", "ANI12-02.s", "ANI12-03.s", "ANI12-04.s", "ANI12-05.s", "ANI12-08.s", "ANI12-09.s", "ANI12-010.s", "ANI12-011.s",
            "ani33-1.s", "ani33-2.s", "ani33-3.s", "ani33-4.s", "ani33-5.s", "ani33-6.s", "ani33-7.s",
            "ANI16.s", "ANI2.s", "ANI1.s", "ANI4.s", "ANI7.s", "ani25.s",
        };
        const float DefaultH = 0.70f;       // alto del andén sin medir (m sobre el carril)
        const float WalkMps = 1.25f;
        const int MaxPeople = 100;          // a la vez, como mucho (por encima, se ve una parte proporcional; 2026-11-04: 120 → 100, menos carga)
        const float Ahead = 650;            // la estación siguiente se prepara a esta distancia

        static bool _on, _said;
        static string _route;
        static List<string> _paths;
        static readonly List<object> _shapes = new List<object>();       // figuras fijas de la CGL

        // personas animadas: cada una con su andar y sus formas de esperar (posturas .s ya cargadas)
        sealed class Avatar
        {
            public string Name, Dir; public float Speed, Cycle; public object[] Walk;
            public readonly List<(object[] frames, float secs)> Idles = new List<(object[], float)>();
            public readonly Queue<(string anim, float secs)> Pending = new Queue<(string, float)>();
        }
        static readonly List<Avatar> _avatars = new List<Avatar>();
        static int Figures => _avatars.Count > 0 ? _avatars.Count : _shapes.Count;
        static object _mgr;

        static MethodInfo _get, _prep, _mark;
        // (2026-11-04, para no cargar Open Rails) las dos llamadas de cada fotograma, compiladas: sin reflexión ni basura
        static Action<object, object, WorldPosition, object> _prepD; static Action<object> _markD;
        static void Prep(object shape, object frame, WorldPosition wp, object flags)
        {
            if (_prepD != null) _prepD(shape, frame, wp, flags); else _prep.Invoke(shape, new[] { frame, wp, flags });
        }
        static void Mark(object shape) { if (_markD != null) _markD(shape); else _mark.Invoke(shape, null); }
        static void Compile(Type shapeT)
        {
            try
            {
                var ps = _prep.GetParameters();
                var sh = System.Linq.Expressions.Expression.Parameter(typeof(object)); var fr = System.Linq.Expressions.Expression.Parameter(typeof(object));
                var wp = System.Linq.Expressions.Expression.Parameter(typeof(WorldPosition)); var fl = System.Linq.Expressions.Expression.Parameter(typeof(object));
                var call = System.Linq.Expressions.Expression.Call(System.Linq.Expressions.Expression.Convert(sh, shapeT), _prep,
                    System.Linq.Expressions.Expression.Convert(fr, ps[0].ParameterType), wp, System.Linq.Expressions.Expression.Convert(fl, ps[2].ParameterType));
                _prepD = System.Linq.Expressions.Expression.Lambda<Action<object, object, WorldPosition, object>>(call, sh, fr, wp, fl).Compile();
                var sh2 = System.Linq.Expressions.Expression.Parameter(typeof(object));
                _markD = System.Linq.Expressions.Expression.Lambda<Action<object>>(System.Linq.Expressions.Expression.Call(System.Linq.Expressions.Expression.Convert(sh2, shapeT), _mark), sh2).Compile();
            }
            catch (Exception ex) { _prepD = null; _markD = null; Agent.Log("viajeros en los andenes: sin llamadas compiladas (se usa la reflexión): " + ex.GetType().Name + " " + ex.Message); }
        }
        static Func<object, object> _shapeMgr, _worldViewer, _scenery, _worldFiles, _sceneryObjects, _location, _sharedShape, _filePath;
        static object _flags, _noShadow, _world;
        static readonly Random _rnd = new Random();
        static readonly Stopwatch _clock = Stopwatch.StartNew();
        static double _last;
        static float U(float a, float b) => a + (float)_rnd.NextDouble() * (b - a);

        // un campo o una propiedad (según la versión de Open Rails: Viewer.ShapeManager es campo en NY 174 y propiedad en otras)
        static Func<object, object> Member(Type t, string name)
        {
            if (t == null) return null;
            var f = AccessTools.Field(t, name); if (f != null) return o => f.GetValue(o);
            var p = AccessTools.Property(t, name); if (p != null) return o => p.GetValue(o, null);
            return null;
        }

        sealed class Person
        {
            public int Shape, TileX, TileZ, Side, IdleVar; public Vector3 Pos, Target, Then; public float Wait, Heading;
            public int Phase;                 // 0 quieto · 1 andando · 2 fuera
            public float Walked, Clock, Pace = 1, IdleRate = 1;
            public float Face = float.NaN, Base = float.NaN, FaceEnd = float.NaN, Glance;   // (hacia dónde quiere mirar, su orientación de reposo, al llegar…)
            public bool Moving;               // (andando de verdad este fotograma: no girando en el sitio)
            public bool Boards, HideAtEnd;    // (Then.Y < -1e5: no hay siguiente tramo)
            public readonly WorldPosition Wp = new WorldPosition();
        }
        static readonly List<Person> _people = new List<Person>();

        // la estación en curso
        sealed class Station
        {
            public int Platform, TileX, TileZ; public string Why = "";
            public float Base, Start, End;    // lo andado por el tren al verla; el andén, de Start a End m por delante de la cabeza
            public int[] Want = new int[3];   // esperando según la ruta: [0] a la derecha · [1] sin lado · [2] a la izquierda
            public int RouteSide;             // el lado de la ruta: +1 izquierda · −1 derecha · 0 los dos o sin dato
            public volatile bool Measured;
            public List<(Vector3 p, int side, Vector3 fwd)> Spots = new List<(Vector3, int, Vector3)>();   // (side: +1 a la izquierda de la vía en la marcha)
            public float TypicalH = DefaultH;  // la altura del andén sobre el carril (mediana de lo medido)
            public bool Spawned, DoorsSeen, Stopped, Gathered; public int Side;
            public int Tries = 1; public float MeasuredAt;   // (veces medido y lo andado entonces: sin andén, se vuelve a medir al acercarse)
            public double TriedAt;            // (cuándo: entre una y otra, como poco 3 s)
            public List<Vector3> Ends = new List<Vector3>();   // (los extremos del andén según la ruta, en la tesela de la estación)
            public bool AtDoors;              // (ya se ha medido junto a las puertas abiertas)
            // los andenes que declara la ruta (como en el mapa grande): su lado en la marcha (+1 izquierda · −1 derecha · 0 los
            // dos o sin dato) y sus extremos. La gente, solo ahí.
            public List<(int side, bool known, Vector3[] ends)> Decl = new List<(int, bool, Vector3[])>();
            // puertas abiertas: sus puertas, hacia el andén, cuándo queda libre cada una; con el contador, sus cifras
            public Vector3 SideV; public List<(Vector3 at, Vector3 inside, Vector3 fwd, float rail)> DoorList; public double[] Busy;
            public int[] QA, QB;              // (por puerta: los que han bajado y los que van a subir por ella)
            public double OpenedAt; public bool Own, WasOpen; public Count Cnt;
        }
        static Station _st;

        // las cifras del contador de SelectOR para una parada
        sealed class Count
        {
            public int Alight, Board, Onboard, Seats, DoneA, DoneB, LastA, LastB; public float FA = 1, FB = 1; public bool Started, Ended; public double At;
        }
        static readonly ConcurrentQueue<string> _pax = new ConcurrentQueue<string>();
        static bool _counter;          // SelectOR está contando los viajeros
        static Count _lastCnt;         // (la última parada que ha mandado, hasta que la recoja la estación)
        public static void Pax(string s) => _pax.Enqueue(s);

        // los viajeros a bordo de tu tren (para cuántos bajan)
        static int _onboard = -1, _onboardTrain = -1, _onboardCap = -1;

        public static int Install(Harmony h)
        {
            try
            {
                // la gente (Ajustes → Gráficos) y los objetos de la sala (Viajeros3D.Objetos.cs) usan el mismo dibujo
                _peopleOn = Environment.GetEnvironmentVariable("SELECTOR_VIAJEROS3D") == "1";
                _objOn = Environment.GetEnvironmentVariable("SELECTOR_OBJETOS") == "1";
                if (!_peopleOn && !_objOn) return 0;
                if (_on) return 1;   // (ya puesto)
                var world = AccessTools.TypeByName("Orts.Viewer3D.World"); var viewer = AccessTools.TypeByName("Orts.Viewer3D.Viewer");
                var shape = AccessTools.TypeByName("Orts.Viewer3D.SharedShape"); var mgr = AccessTools.TypeByName("Orts.Viewer3D.SharedShapeManager");
                var flags = AccessTools.TypeByName("Orts.Viewer3D.ShapeFlags");
                if (world == null || viewer == null || shape == null || mgr == null || flags == null) return 0;
                var sceneryT = AccessTools.TypeByName("Orts.Viewer3D.SceneryDrawer"); var wfT = AccessTools.TypeByName("Orts.Viewer3D.WorldFile");
                var ssT = AccessTools.TypeByName("Orts.Viewer3D.StaticShape");
                _worldViewer = Member(world, "Viewer"); _shapeMgr = Member(viewer, "ShapeManager");
                _scenery = Member(world, "Scenery"); _worldFiles = Member(sceneryT, "WorldFiles"); _sceneryObjects = Member(wfT, "sceneryObjects");
                _location = Member(ssT, "Location"); _sharedShape = Member(ssT, "SharedShape"); _filePath = Member(shape, "FilePath");
                _get = AccessTools.Method(mgr, "Get", new[] { typeof(string) });
                _mark = AccessTools.Method(shape, "Mark", Type.EmptyTypes);
                _prep = shape.GetMethods(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault(m => m.Name == "PrepareFrame" && m.GetParameters().Length == 3 && m.GetParameters()[1].ParameterType == typeof(WorldPosition));
                // ShadowCaster (solo los cercanos: con mucha gente, las sombras pesan). Por su nombre: cada Open Rails puede numerarlo distinto
                try { _flags = Enum.Parse(flags, "ShadowCaster"); } catch { _flags = Enum.ToObject(flags, 0); }
                _noShadow = Enum.ToObject(flags, 0);
                var load = AccessTools.Method(world, "Load", Type.EmptyTypes);
                var draw = world.GetMethods(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault(m => m.Name == "PrepareFrame" && m.GetParameters().Length == 2);
                var vmark = AccessTools.Method(viewer, "Mark", Type.EmptyTypes);
                var missing = new[] { _worldViewer == null ? "World.Viewer" : null, _shapeMgr == null ? "Viewer.ShapeManager" : null, _get == null ? "SharedShapeManager.Get" : null,
                    _mark == null ? "SharedShape.Mark" : null, _prep == null ? "SharedShape.PrepareFrame" : null, load == null ? "World.Load" : null,
                    draw == null ? "World.PrepareFrame" : null, vmark == null ? "Viewer.Mark" : null }.Where(x => x != null).ToList();
                if (missing.Count > 0) { Agent.Log("viajeros en los andenes: este Open Rails no tiene lo necesario (" + string.Join(", ", missing) + ")"); return 0; }
                Compile(shape);
                if (!CanMeasure)
                    Agent.Log("viajeros en los andenes: sin acceso al escenario (" + string.Join(", ", new[] { _scenery == null ? "World.Scenery" : null, _worldFiles == null ? "SceneryDrawer.WorldFiles" : null,
                        _sceneryObjects == null ? "WorldFile.sceneryObjects" : null, _location == null ? "StaticShape.Location" : null, _sharedShape == null ? "StaticShape.SharedShape" : null,
                        _filePath == null ? "SharedShape.FilePath" : null }.Where(x => x != null)) + "): el andén no se mide");
                h.Patch(load, postfix: new HarmonyMethod(typeof(Viajeros3D), nameof(AfterLoad)));
                // los modelos que Open Rails agrupa (SharedStaticShapeInstance: el grupo está en el centro de todos): se apunta dónde
                // está cada uno, para medir el andén donde de verdad está
                var groupT = AccessTools.TypeByName("Orts.Viewer3D.SharedStaticShapeInstance");
                var gctor = groupT?.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(c => c.GetParameters().Length == 3);
                if (gctor != null) h.Patch(gctor, postfix: new HarmonyMethod(typeof(Viajeros3D), nameof(AfterGroup)));
                else Agent.Log("viajeros en los andenes: sin acceso a los modelos agrupados (el andén puede medirse mal)");
                // (2026-11-04, la causa de los cierres) Open Rails libera los modelos que nadie ha marcado (SharedShapeManager.Sweep,
                // dentro de World.Load, cuando la cámara cambia de tesela: yendo hacia una estación). Antes se marcaban en un postfijo
                // de Viewer.Mark, un método diminuto que .NET acaba copiando dentro de World.Load (inlining): el postfijo dejaba de
                // ejecutarse, se liberaba la gente mientras se dibujaba y Open Rails se cerraba (VertexBuffer.GenerateIfRequired). Ahora
                // se marcan en un prefijo del propio Sweep (con bucles: nunca se copia dentro de otro); Viewer.Mark, solo si no está.
                var sweep = AccessTools.Method(mgr, "Sweep", Type.EmptyTypes);
                if (sweep != null) h.Patch(sweep, prefix: new HarmonyMethod(typeof(Viajeros3D), nameof(BeforeSweep)));
                else { h.Patch(vmark, postfix: new HarmonyMethod(typeof(Viajeros3D), nameof(AfterMark))); Agent.Log("viajeros en los andenes: sin SharedShapeManager.Sweep: se marca en Viewer.Mark"); }
                h.Patch(draw, postfix: new HarmonyMethod(typeof(Viajeros3D), nameof(AfterDraw)));
                _on = true;
                return 1;
            }
            catch (Exception ex) { Agent.Log("viajeros en los andenes: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        // ---- las personas animadas ----
        // gente.txt: «prefijo velocidad metros-por-ciclo idle=s look=s wait=s» (las posturas: prefijo_anim_NN.s, con las
        // texturas al lado) y, si se quiere, «#max N» (cuántas personas distintas por sesión; 16 si no). Prefijo «n…»: niños
        // (como mucho 3 por sesión).
        static bool LoadAvatars(object mgr)
        {
            string dir = Environment.GetEnvironmentVariable("SELECTOR_GENTE_DIR");
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "gente.txt"))) return false;
            var sw = Stopwatch.StartNew(); var ci = System.Globalization.CultureInfo.InvariantCulture;
            int max = 16;
            var all = new List<(string name, float speed, float cycle, List<(string anim, float secs)> anims)>();
            foreach (var line in File.ReadAllLines(Path.Combine(dir, "gente.txt")))
            {
                var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 2 && t[0] == "#max") { int.TryParse(t[1], out max); continue; }
                if (t.Length < 3 || t[0].StartsWith("#")) continue;
                if (IsPerson(t[0]) || t[0].StartsWith("obj_")) continue;   // (operarios de vía, demás oficios y derrumbes: solo como objetos de la sala)
                var anims = t.Skip(3).Select(x => x.Split('=')).Where(x => x.Length == 2).Select(x => (x[0], float.Parse(x[1], ci))).ToList();
                if (!anims.Any(x => x.Item1 == "idle")) anims.Insert(0, ("idle", 4f));
                all.Add((t[0], float.Parse(t[1], ci), float.Parse(t[2], ci), anims));
            }
            max = Math.Min(max, 12);   // (2026-11-04: como mucho 12 personas distintas por sesión, unos 50 MB menos de memoria gráfica)
            var pick = all.OrderBy(x => _rnd.Next()).ToList();
            var kids = pick.Where(x => x.name.StartsWith("n")).Take(3).ToList();
            pick = pick.Where(x => !x.name.StartsWith("n")).Take(Math.Max(1, max - kids.Count)).Concat(kids).ToList();
            foreach (var (name, speed, cycle, anims) in pick)
            {
                var a = new Avatar { Name = name, Dir = dir, Speed = speed, Cycle = cycle, Walk = Frames(mgr, dir, name, "walk") };
                var idle = anims.First(x => x.anim == "idle");
                var f = Frames(mgr, dir, name, "idle");
                if (a.Walk.Length == 0 || f.Length == 0) continue;
                a.Idles.Add((f, idle.secs));
                foreach (var x in anims.Where(x => x.anim != "idle")) a.Pending.Enqueue(x);
                _avatars.Add(a);
            }
            Agent.Log($"viajeros en los andenes: {_avatars.Count} de {all.Count} personas animadas para esta sesión ({string.Join(" ", _avatars.Select(a => a.Name))}), cargadas en {sw.ElapsedMilliseconds} ms; las otras esperas, poco a poco");
            return _avatars.Count > 0;
        }
        static object[] Frames(object mgr, string dir, string name, string anim) =>
            Directory.GetFiles(dir, name + "_" + anim + "_*.s").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => _get.Invoke(mgr, new object[] { f + "\0" + dir })).ToArray();

        // la postura que toca: andando, según lo andado; quieto, ida y vuelta por su forma de esperar
        static object ShapeOf(Person p, double now)
        {
            if (_avatars.Count == 0) return _shapes[p.Shape % _shapes.Count];
            var a = _avatars[p.Shape % _avatars.Count];
            if (p.Moving)
            {
                int n = a.Walk.Length; double c = p.Walked / a.Cycle;
                return a.Walk[(int)((c - Math.Floor(c)) * n) % n];
            }
            var (frames, secs) = a.Idles[p.IdleVar % a.Idles.Count];
            int m = frames.Length; double u = ((now * p.IdleRate + p.Clock) % (2 * secs)) / secs;   // 0..2
            double k = u <= 1 ? u : 2 - u;
            return frames[Math.Min(m - 1, (int)(k * (m - 1) + 0.5))];
        }
        static float SpeedOf(Person p) => _avatars.Count > 0 ? _avatars[p.Shape % _avatars.Count].Speed * p.Pace : WalkMps;

        static readonly ConditionalWeakTable<object, List<WorldPosition>> _groups = new ConditionalWeakTable<object, List<WorldPosition>>();
        static void AfterGroup(object __instance, object __2)
        {
            try
            {
                if (_location == null || !(__2 is IEnumerable list)) return;
                var locs = new List<WorldPosition>();
                foreach (var o in list) if (_location(o) is WorldPosition w) locs.Add(w);
                _groups.Remove(__instance); _groups.Add(__instance, locs);
            }
            catch { }
        }

        // ¿esta ruta trae las figuras de la CGL? (las que haya)
        static List<string> PathsFor(Simulator sim)
        {
            string r = sim?.RoutePath;
            if (r == null) return null;
            if (r == _route) return _paths;
            _route = r; _paths = null; _shapes.Clear();
            var p = new List<string>();
            foreach (var m in Models) { string f = Path.Combine(r, "SHAPES", m); if (File.Exists(f)) p.Add(f); }
            if (p.Count >= 4) _paths = p;
            Agent.Log(_paths == null ? "viajeros en los andenes: esta ruta no trae las figuras de la CGL" : $"viajeros en los andenes: {p.Count} figuras de la ruta");
            return _paths;
        }

        // (hilo de carga) los modelos, una vez
        static void AfterLoad(object __instance)
        {
            try
            {
                _world = __instance;
                if (!_on) return;
                if (_mgr == null) _mgr = _shapeMgr(_worldViewer(__instance));
                if (_objOn) ObjLoad();   // (los objetos de la sala: sus figuras y su altura; World.Load va en cada vuelta del hilo de carga)
                if (_peopleOn && _avatars.Count > 0) { try { LoadPendingIdle(); } catch { } }
                if (!_peopleOn || Figures > 0) return;
                var mgr = _mgr;
                if (LoadAvatars(mgr)) return;
                var paths = PathsFor(MPManager.Simulator); if (paths == null) return;
                foreach (var f in paths) _shapes.Add(_get.Invoke(mgr, new object[] { f }));
                Agent.Log($"viajeros en los andenes: {_shapes.Count} figuras cargadas");
            }
            catch (Exception ex) { _on = false; Agent.Log("viajeros en los andenes (carga): " + ex.GetType().Name + " " + ex.Message); }
        }

        // (hilo de carga, antes del barrido) que no se descarguen; y, de una en una, las otras formas de esperar
        static int _pendingLeft = -1;
        static void AfterMark() => MarkAll();
        static int _sweeps;
        static void BeforeSweep()
        {
            MarkAll();
            int n = _shapes.Count + _avatars.Sum(a => a.Walk.Length + a.Idles.Sum(i => i.frames.Length)) + _figs.Values.Sum(f => f.Anims.Values.Sum(x => x.frames.Length));
            if ((n > 0 || _sweeps == 0) && _sweeps++ < 20) Agent.Log($"viajeros en los andenes: Open Rails libera lo que no se usa (cambio de tesela): marcados antes {n} modelos de gente y objetos");
        }
        // todo lo que dibujamos, marcado como en uso (siempre, aunque la gente o los objetos se hayan apagado: algo ya preparado
        // para dibujar no puede liberarse); cada colección por separado, para que un fallo no deje nada sin marcar
        static int _markFails;
        static void MarkAll()
        {
            try { foreach (var s in _shapes) Mark(s); } catch (Exception ex) { MarkFailed(ex); }
            try { foreach (var a in _avatars) { foreach (var s in a.Walk) Mark(s); foreach (var (fr, _) in a.Idles.ToArray()) foreach (var s in fr) Mark(s); } } catch (Exception ex) { MarkFailed(ex); }
            try { MarkFigs(); } catch (Exception ex) { MarkFailed(ex); }
        }
        static void MarkFailed(Exception ex) { if (_markFails++ < 3) Agent.Log("viajeros en los andenes (marcar): " + ex.GetType().Name + " " + ex.Message); }

        // (hilo de carga, World.Load) las otras formas de esperar, de una en una cada 2 s
        static double _pendingAt;
        static void LoadPendingIdle()
        {
            double t = _clock.Elapsed.TotalSeconds; if (t - _pendingAt < 2) return; _pendingAt = t;
            var next = _avatars.FirstOrDefault(a => a.Pending.Count > 0);
            if (next == null || _mgr == null) return;
            var (anim, secs) = next.Pending.Dequeue();
            var f = Frames(_mgr, next.Dir, next.Name, anim);
            if (f.Length > 0) next.Idles.Add((f, secs));
            if (_avatars.All(a => a.Pending.Count == 0)) Agent.Log($"viajeros en los andenes: cargadas todas las formas de esperar ({_avatars.Sum(a => a.Idles.Count)})");
        }

        // (hilo de actualización, cada fotograma) la gente: qué hace y dónde se dibuja
        static void AfterDraw(object __instance, object __0, object __1)
        {
            if (!_on) return;
            if (_objOn) ObjDraw(__instance, __0, __1);   // (los objetos de la sala; __1: el tiempo del fotograma, para el humo)
            if (!_peopleOn || Figures == 0) return;
            try
            {
                _world = __instance;
                double now = _clock.Elapsed.TotalSeconds; float dt = (float)Math.Min(0.25, Math.Max(0, now - _last)); _last = now;
                var sim = MPManager.Simulator; var train = sim?.PlayerLocomotive?.Train;
                if (train != null) Think(sim, train, dt, now);
                var near = train != null && train.Cars.Count > 0 ? train.Cars[0].WorldPosition : null;
                foreach (var p in _people)
                {
                    if (p.Phase == 2) continue;
                    if (float.IsNaN(p.Pos.X) || float.IsNaN(p.Pos.Y) || float.IsNaN(p.Pos.Z) || float.IsNaN(p.Heading)) { p.Phase = 2; continue; }   // (nunca a Open Rails una posición rota)
                    var m = Matrix.CreateRotationY(p.Heading); m.Translation = p.Pos;
                    p.Wp.TileX = p.TileX; p.Wp.TileZ = p.TileZ; p.Wp.XNAMatrix = m;   // (la misma en cada fotograma: sin basura)
                    Prep(ShapeOf(p, now), __0, p.Wp, near != null && Dist(p, near) < 60 ? _flags : _noShadow);
                }
            }
            catch (Exception ex) { if (!_said) { _said = true; Agent.Log("viajeros en los andenes (dibujo): " + ex.GetType().Name + " " + ex.Message); } }
        }

        // ---- lo que pasa en el andén ----
        static void Think(Simulator sim, Train train, float dt, double now)
        {
            if (train.Cars.Count == 0) return;
            while (_pax.TryDequeue(out var cmd)) PaxCmd(cmd, now);
            var lead = train.Cars[0];
            int tx = lead.WorldPosition.TileX, tz = lead.WorldPosition.TileZ;
            float speed = Math.Abs(train.SpeedMpS);
            bool stopped = speed < 0.2f;
            var cars = PaxCars(train);
            var open = OpenDoorSide(cars);
            float travelled = train.DistanceTravelledM;

            // la estación de antes, ya pasada: se suelta (su gente se queda en el andén)
            if (_st != null)
            {
                float prog = travelled - _st.Base;
                bool passed = _st.Platform >= 0 ? prog > _st.End + 15 || prog < -200 : _st.Stopped && speed > 1f && prog > 80;
                if (passed)
                {
                    foreach (var p in _people.Where(p => p.Phase == 1 && p.Boards && !p.HideAtEnd)) { p.Phase = 0; p.Target = p.Pos; p.Then = new Vector3(0, -1e6f, 0); }
                    _st = null;
                }
            }
            // la siguiente: el andén del camino del tren a menos de 650 m (o, sin andén en la ruta, parado con las puertas abiertas)
            if (_st == null && cars.Count > 0)
            {
                var ah = PlatformAhead(sim, train, Ahead);
                if (ah != null)
                {
                    var (plats, start, end) = ah.Value;
                    var s0 = new Station { Platform = plats[0].idx, TileX = tx, TileZ = tz, Base = travelled, Start = start, End = end };
                    var names = new List<string>(); var sides = new HashSet<int>();
                    foreach (var (idx, dir) in plats)
                    {
                        try
                        {
                            var pd = sim.Signals.PlatformDetailsList[idx];
                            int side = SideOf(pd, dir); sides.Add(side);
                            s0.Want[side + 1] += Math.Max(0, pd.NumPassengersWaiting);
                            names.Add($"{pd.Name} [{(pd.PlatformSide[0] ? 1 : 0)},{(pd.PlatformSide[1] ? 1 : 0)}] sentido {dir} {(pd.PlatformReference[0] == pd.PlatformFrontUiD ? "desde la cabeza" : "desde la cola")} → {(side > 0 ? "izquierda" : side < 0 ? "derecha" : "los dos")} · {pd.NumPassengersWaiting} esperando");
                        }
                        catch { }
                    }
                    s0.RouteSide = sides.Count == 1 ? sides.First() : 0;
                    foreach (var (idx, dir) in plats)
                    {
                        var ends = PlatformEnds(sim, idx, tx, tz).ToArray();
                        s0.Ends.AddRange(ends);
                        try { var pd = sim.Signals.PlatformDetailsList[idx]; s0.Decl.Add((SideOf(pd, dir), pd.PlatformSide[0] || pd.PlatformSide[1], ends)); }
                        catch { s0.Decl.Add((0, false, ends)); }
                    }
                    s0.Why = string.Join(" · ", names.Distinct()) + $" · de {start:0} a {end:0} m";
                    _st = s0;
                    Agent.Log($"viajeros en los andenes: estación a la vista: {s0.Why} · {s0.Ends.Count} extremos de andén");
                    Measure(_st, train, Math.Max(start - 25, -train.Length - 40), Math.Min(end + 25, start + 2500));
                }
                // (sin andén declarado en la ruta, nadie: antes salían al abrir las puertas en cualquier sitio)
            }
            if (_st == null) { Move(dt, lead); return; }
            var s = _st;
            if (stopped) s.Stopped = true;

            // medido sin andén (Open Rails aún no había cargado esa parte del escenario): se vuelve a medir al acercarse
            if (s.Measured && !s.Spawned && s.Spots.Count == 0 && s.Platform >= 0 && s.Tries < 8)
            {
                float pr = travelled - s.Base;
                bool atPlatform = stopped && NearEnds(s, Rel(lead.WorldPosition, s.TileX, s.TileZ), 300);   // (parado EN el andén, no antes)
                if ((pr - s.MeasuredAt >= 150 || (atPlatform && pr - s.MeasuredAt >= 1) || (atPlatform && s.Tries == 1)) && now - s.TriedAt >= 3)
                {
                    s.Tries++; s.TriedAt = now; s.MeasuredAt = pr; s.Measured = false;
                    Agent.Log($"viajeros en los andenes: sin andén medido: se vuelve a medir ({s.Tries}.ª vez, a {s.Start - pr:0} m{(atPlatform ? ", parado en el andén: alrededor del tren" : "")})");
                    if (atPlatform) Measure(s, train, -train.Length - 30, 170, aroundTrain: true);
                    else Measure(s, train, Math.Max(s.Start - pr - 25, -train.Length - 40), Math.Min(s.End - pr + 25, s.Start - pr + 2500));
                }
            }
            // medido: la gente que espera, en cada lado con andén
            if (s.Measured && !s.Spawned && (s.Spots.Count > 0 || s.Platform < 0 || s.Tries >= 8))
            {
                s.Spawned = true;
                float prog = travelled - s.Base;
                bool far = s.Start - prog > 150;   // (lejos: se puede poner en todo el andén sin que se vea aparecer)
                bool fromRoute = s.Want.Sum() > 0;
                int total = 0; var said = new List<string>();
                var want = new int[3];
                foreach (int side in new[] { 1, -1 })
                {
                    int spots = s.Spots.Count(q => q.side == side);
                    if (spots == 0) continue;
                    int w = fromRoute ? s.Want[side + 1] + (s.Want[1] > 0 ? s.Want[1] / (s.Spots.Any(q => q.side == -side) ? 2 : 1) : 0) : 0;
                    w = Math.Max(w, spots / 8);   // (como mínimo, uno cada ~8 m de andén: las rutas suelen poner muy pocos, 3 o 6)
                    want[side + 1] = (int)Math.Round(w * U(0.85f, 1.15f));
                }
                int sum = want.Sum(); float scale = sum > MaxPeople - _people.Count ? (float)Math.Max(0, MaxPeople - _people.Count) / Math.Max(1, sum) : 1;
                foreach (int side in new[] { 1, -1 })
                {
                    if (want[side + 1] <= 0) continue;
                    int n = SpawnWaiting(s, lead, side, (int)(want[side + 1] * scale), restrict: !far, near: false);
                    total += n; said.Add($"{n} a la {(side > 0 ? "izquierda" : "derecha")}" + (scale < 1 ? $" (de {want[side + 1]})" : ""));
                }
                s.Side = s.RouteSide != 0 ? s.RouteSide : (want[2] >= want[0] ? (want[2] > 0 ? 1 : 0) : -1);
                Agent.Log($"viajeros en los andenes: {s.Why} · andén medido: {s.Spots.Count(q => q.side == 1)} puntos a la izquierda, {s.Spots.Count(q => q.side == -1)} a la derecha"
                          + $" · esperando {(fromRoute ? $"(la ruta dice {s.Want.Sum()}; como mínimo, uno cada ~8 m)" : "(la ruta no lo dice: uno cada ~8 m)")}: " + (said.Count > 0 ? string.Join(", ", said) : "nadie"));
            }

            // el tren llega: los que esperan lo miran (con algo de retraso cada uno); parado y aún con las puertas cerradas, se
            // acercan a donde quedan las puertas de su lado
            if (!s.DoorsSeen && s.Spawned)
            {
                var front = Rel(lead.WorldPosition, s.TileX, s.TileZ);
                foreach (var p in _people.Where(p => p.Boards && p.Phase == 0 && p.TileX == s.TileX && p.TileZ == s.TileZ))
                {
                    var to = front - p.Pos; to.Y = 0; float dd = to.Length();
                    if (!stopped && dd > 3 && dd < 250 && p.Glance <= 0) { p.Face = HeadingOf(to / dd); p.Glance = U(0.4f, 1.8f); }
                }
                if (stopped && !s.Gathered && s.Side != 0) { s.Gathered = true; Gather(cars, s, TrainFwd(train)); }
            }

            // puertas abiertas (parado): bajan y suben por ese lado
            if (stopped && open.HasValue && !s.DoorsSeen && s.Measured && !s.AtDoors)
            {
                var fw0 = TrainFwd(train); int sd0 = Vector3.Dot(open.Value, Left(fw0)) >= 0 ? 1 : -1;
                var ds0 = Doors(cars, Left(fw0) * sd0, s);
                bool near = ds0.Any(d => s.Spots.Any(q => q.side == sd0 && (q.p - d.at).LengthSquared() < 30 * 30));
                if (!near)
                {
                    s.AtDoors = true; s.Measured = false;
                    Agent.Log("viajeros en los andenes: puertas abiertas sin andén medido junto a ellas: se mide ahí mismo");
                    Measure(s, train, -train.Length - 30, 60, aroundTrain: true, addToBefore: true);
                }
            }
            if (stopped && open.HasValue && !s.DoorsSeen && s.Measured)
            {
                s.DoorsSeen = true;
                var fwd = TrainFwd(train);
                int side = Vector3.Dot(open.Value, Left(fwd)) >= 0 ? 1 : -1;
                if (!_people.Any(p => p.Boards && p.Phase != 2 && !p.HideAtEnd && p.Side == side && p.TileX == s.TileX && p.TileZ == s.TileZ))
                {
                    int w = s.Want.Sum() > 0 ? s.Want[side + 1] + s.Want[1] : s.Spots.Count(q => q.side == side) / 8;
                    if (w == 0) w = Math.Max(3, s.Spots.Count(q => q.side == side) / 10);
                    int n = SpawnWaiting(s, lead, side, Math.Min(w, Math.Max(0, MaxPeople - _people.Count)), restrict: true, near: true);
                    Agent.Log($"viajeros en los andenes: las puertas se abren a la {(side > 0 ? "izquierda" : "derecha")}, donde no esperaba nadie: llegan {n} andando");
                }
                s.Side = side;
                s.SideV = Left(fwd) * side; s.DoorList = Doors(cars, s.SideV, s); s.Busy = new double[s.DoorList.Count]; s.OpenedAt = now;
                s.QA = new int[s.DoorList.Count]; s.QB = new int[s.DoorList.Count];
                if (!_counter || s.DoorList.Count == 0) { s.Own = true; Board(cars, s.SideV, s, train); }
            }

            // las puertas se cierran (o empiezan a cerrarse): nadie más entra ni sale; si se vuelven a abrir, sigue
            if (s.DoorsSeen)
            {
                if (s.WasOpen && !open.HasValue) DoorsClosed(s);
                s.WasOpen = open.HasValue;
            }

            // con el contador de SelectOR: sus cifras (si no llegan en 6 s, las propias)
            if (s.DoorsSeen && !s.Own && s.DoorList != null)
            {
                if (s.Cnt == null && _lastCnt != null && now - _lastCnt.At < 90) { s.Cnt = _lastCnt; _lastCnt = null; }
                if (s.Cnt == null && now - s.OpenedAt > 6)
                {
                    s.Own = true;
                    Agent.Log("viajeros en los andenes: el contador de SelectOR no ha dado cifras en esta parada: bajan y suben según el tren");
                    Board(cars, s.SideV, s, train);
                }
                else if (s.Cnt != null && !s.Cnt.Started) CounterStart(s, lead, now);
            }

            Move(dt, lead);
        }

        // ---- el contador de SelectOR ----
        static int Int(string[] t, int i) => i < t.Length && int.TryParse(t[i], out int v) ? v : 0;
        static void PaxCmd(string cmd, double now)
        {
            var t = cmd.Split(' ');
            var c = _st?.Cnt ?? _lastCnt;
            switch (t[0])
            {
                case "ON":
                    if (!_counter) Agent.Log("viajeros en los andenes: SelectOR cuenta los viajeros: bajan y suben los de su contador");
                    _counter = true; break;
                case "STOP":
                    _counter = true;
                    _lastCnt = new Count { Alight = Int(t, 1), Board = Int(t, 2), Onboard = Int(t, 3), Seats = Int(t, 4), At = now };
                    if (_st != null && _st.DoorsSeen && !_st.Own && (_st.Cnt == null || _st.Cnt.Ended)) { _st.Cnt = _lastCnt; _lastCnt = null; }
                    Agent.Log($"viajeros en los andenes: contador: bajan {Int(t, 1)} · suben {Int(t, 2)} · a bordo {Int(t, 3)} de {Int(t, 4)} plazas");
                    break;
                case "STEP":
                    if (c == null) break;
                    c.LastA = Int(t, 1); c.LastB = Int(t, 2);
                    if (c.Started && _st != null && _st.Cnt == c) CounterStep(_st, now);
                    break;
                case "END":
                    if (c == null) break;
                    c.Ended = true; _onboard = Math.Max(0, c.Onboard - c.LastA + c.LastB);
                    break;
            }
        }

        // puertas cerradas: los que iban a subir y aún no han llegado a la puerta se apartan y se quedan en el andén (el que ya
        // está en la puerta termina de entrar); los que iban a bajar y aún no habían salido se quedan dentro
        static void DoorsClosed(Station s)
        {
            int stay = 0, inside = 0;
            foreach (var p in _people.Where(p => p.Phase == 1 && p.TileX == s.TileX && p.TileZ == s.TileZ))
            {
                if (p.Boards && p.HideAtEnd && p.Then.Y > -1e5f)
                {
                    p.HideAtEnd = false; p.Then = new Vector3(0, -1e6f, 0);
                    var near = s.DoorList?.OrderBy(d => (d.at - p.Pos).LengthSquared()).FirstOrDefault();
                    bool atDoor = near.HasValue && (near.Value.at - p.Pos).Length() < 2.5f;
                    var to = atDoor ? OnPlatform(s, p.Pos + s.SideV * U(0.9f, 1.8f) + near.Value.fwd * U(-1.2f, 1.2f), s.Side) : p.Pos;
                    if (atDoor) to.Y = near.Value.rail + HeightNear(s, to, near.Value.rail);
                    p.Target = to; p.Wait = U(0, 0.7f);
                    p.FaceEnd = HeadingOf(-s.SideV) + U(-0.5f, 0.5f);
                    if (!atDoor) { p.Phase = 0; p.Face = p.Base = p.FaceEnd; p.FaceEnd = float.NaN; }
                    stay++;
                }
                else if (!p.Boards && p.Wait > 0) { p.Phase = 2; inside++; }   // (aún dentro del coche)
            }
            if (s.Cnt != null) { s.Cnt.DoneB = (int)Math.Round(s.Cnt.LastB * s.Cnt.FB); s.Cnt.DoneA = (int)Math.Round(s.Cnt.LastA * s.Cnt.FA); }
            if (stay + inside > 0) Agent.Log($"viajeros en los andenes: puertas cerradas: {stay} se quedan en el andén" + (inside > 0 ? $", {inside} no llegan a bajar" : ""));
        }

        // las que esperan en el lado de las puertas (para subir)
        static IEnumerable<Person> Waiters(Station s) =>
            _people.Where(p => p.Boards && !p.HideAtEnd && p.Phase != 2 && p.Side == s.Side && p.TileX == s.TileX && p.TileZ == s.TileZ);

        // empiezan: si esperan menos de los que suben, llegan andando los que faltan; lo que se ve, proporcional si no caben
        static void CounterStart(Station s, TrainCar lead, double now)
        {
            var c = s.Cnt; c.Started = true;
            int waiting = Waiters(s).Count(), came = 0;
            if (c.Board > waiting)
                came = SpawnWaiting(s, lead, s.Side, Math.Min(c.Board - waiting, Math.Max(0, (MaxPeople - _people.Count) / 2)), restrict: true, near: true);
            int can = waiting + came;
            c.FB = c.Board > 0 ? Math.Min(1f, (float)can / c.Board) : 1;
            int room = Math.Max(0, MaxPeople - _people.Count);
            c.FA = c.Alight > 0 ? Math.Min(1f, (float)room / c.Alight) : 1;
            Agent.Log($"viajeros en los andenes: puertas abiertas a la {(s.Side > 0 ? "izquierda" : "derecha")} ({s.DoorList.Count} puertas), con el contador: bajan {c.Alight}"
                      + (c.FA < 1 ? $" (se ven {(int)(c.Alight * c.FA)})" : "") + $" · suben {c.Board} (esperaban {waiting}" + (came > 0 ? $", llegan {came} andando" : "") + ")"
                      + (c.FB < 1 ? $" (se ven {(int)(c.Board * c.FB)})" : ""));
            CounterStep(s, now);
        }

        // cada paso del contador: los que han bajado, por una puerta (desde dentro); los que han subido, a la puerta más cercana
        static void CounterStep(Station s, double now)
        {
            var c = s.Cnt; var doors = s.DoorList; if (doors == null || doors.Count == 0) return;
            int ta = (int)Math.Round(c.LastA * c.FA), tb = (int)Math.Round(c.LastB * c.FB);
            while (c.DoneA < ta && _people.Count < MaxPeople)
            {
                int di = NextDown(s.QA); s.QA[di]++;   // (repartidos entre todas las puertas)
                float w = (float)Math.Max(0.2, s.Busy[di] - now); s.Busy[di] = now + w + 1.7;
                SpawnAlighter(s, di, w);
                c.DoneA++;
            }
            while (c.DoneB < tb)
            {
                // el siguiente: el que antes puede subir, cada uno por su puerta (lo que anda + los que ya van a ella)
                Person best = null; int bd = 0; float bc = float.MaxValue, bl = 0;
                foreach (var p in Waiters(s))
                {
                    int i = PickDoor(doors, p.Pos, s.QB, out float d); float cost = d + s.QB[i] * 6f;
                    if (cost < bc) { bc = cost; best = p; bd = i; bl = d; }
                }
                if (best == null) break;
                s.QB[bd]++;
                var door = doors[bd];
                double travel = bl / Math.Max(0.5f, SpeedOf(best));
                best.Wait = (float)Math.Max(0, s.Busy[bd] - now - travel); s.Busy[bd] = Math.Max(s.Busy[bd], now + travel) + 1.6;
                best.Target = door.at; best.Then = door.inside; best.HideAtEnd = true; best.Phase = 1;
                c.DoneB++;
            }
        }

        // uno que baja por la puerta di: sale de dentro del coche, cruza la puerta y se aleja por el andén
        static void SpawnAlighter(Station s, int di, float wait)
        {
            var (at, inside, fwd, rail) = s.DoorList[di];
            var (out1, out2) = AwayFromDoor(s, at, fwd, s.SideV, rail);
            _people.Add(new Person
            {
                Shape = _rnd.Next(Figures), Pace = U(0.92f, 1.08f), Clock = U(0, 20), IdleRate = U(0.8f, 1.2f), IdleVar = _rnd.Next(8),
                TileX = s.TileX, TileZ = s.TileZ, Pos = inside, Target = at, Then = out1, Side = s.Side,
                Phase = 1, Wait = wait, Heading = HeadingOf(s.SideV)
            });
            _later.Add((_people[_people.Count - 1], out2));
        }

        // moverse: giros suaves (si hay que girar mucho, primero en el sitio), mirar al llegar, vistazos al esperar
        static void Move(float dt, TrainCar lead)
        {
            Chain();
            foreach (var p in _people)
            {
                if (p.Phase == 2) continue;
                p.Moving = false;
                if (p.Glance > 0) p.Glance -= dt;
                float want = float.IsNaN(p.Face) ? p.Heading : p.Face;
                Vector3 dir = Vector3.Zero; float len = 0;
                if (p.Phase == 1 && p.Wait > 0) p.Wait -= dt;
                else if (p.Phase == 1)
                {
                    var d = p.Target - p.Pos; len = d.Length();
                    if (len < 0.05f)
                    {
                        if (p.Then.Y > -1e5f) { p.Target = p.Then; p.Then = new Vector3(0, -1e6f, 0); continue; }
                        p.Phase = p.HideAtEnd ? 2 : 0;   // los que suben, dentro; los demás se quedan en el andén
                        if (!float.IsNaN(p.FaceEnd)) { p.Face = p.Base = p.FaceEnd; p.FaceEnd = float.NaN; }
                        else { p.Face = p.Base = p.Heading + U(-0.6f, 0.6f); }
                        p.Glance = U(4, 12); p.IdleVar = _rnd.Next(8);
                        continue;
                    }
                    dir = d / len; var flat = new Vector3(dir.X, 0, dir.Z);
                    if (flat.LengthSquared() > 1e-4f) want = HeadingOf(Vector3.Normalize(flat));
                }
                else if (p.Phase == 0 && p.Glance <= 0 && !float.IsNaN(p.Base))
                {
                    p.Face = p.Base + U(-0.55f, 0.55f);   // (esperando: de vez en cuando miran un poco a un lado)
                    p.Glance = U(5, 15);
                }
                float diff = Ang(want - p.Heading), maxTurn = (len > 0 ? 4.0f : 2.2f) * dt;
                p.Heading = Ang(p.Heading + Math.Max(-maxTurn, Math.Min(maxTurn, diff)));
                if (len > 0 && Math.Abs(diff) < 1.0f)
                {
                    float step = Math.Min(len, SpeedOf(p) * dt);
                    p.Pos += dir * step; p.Walked += step; p.Moving = true;
                }
            }
            var c = lead.WorldPosition;
            // (fuera los de lejos; los de la estación que viene, no: su andén puede empezar a más de 1 km, como en Ingeniero Pizias o
            // Urb. Pere Comas, y se borraban nada más ponerlos)
            var st = _st;
            _people.RemoveAll(p => p.Phase == 2 || Dist(p, c) > (st != null && p.TileX == st.TileX && p.TileZ == st.TileZ ? 3500 : 1000));
        }

        static float Dist(Person p, WorldPosition c) => new Vector3((p.TileX - c.TileX) * 2048 + p.Pos.X - c.XNAMatrix.Translation.X, 0, -(p.TileZ - c.TileZ) * 2048 + p.Pos.Z - c.XNAMatrix.Translation.Z).Length();
        // el modelo mira hacia −Z (el +Z de MSTS): ángulo para que mire hacia d
        static float HeadingOf(Vector3 d) => (float)Math.Atan2(-d.X, -d.Z);
        static float Ang(float a) { while (a > Math.PI) a -= (float)(2 * Math.PI); while (a < -Math.PI) a += (float)(2 * Math.PI); return a; }
        static Vector3 Left(Vector3 v) => new Vector3(v.Z, 0, -v.X);
        // (en XNA la Z va al revés que en MSTS: una tesela más al norte es −2048 en Z; antes sumaba y, con la estación y los objetos
        // en teselas distintas de norte a sur, todo quedaba a 4 km: Ingeniero Pizias)
        static Vector3 Rel(WorldPosition w, int tx, int tz) => w.XNAMatrix.Translation + new Vector3((w.TileX - tx) * 2048, 0, -(w.TileZ - tz) * 2048);

        static Vector3 TrainFwd(Train t)
        {
            var front = t.Cars[0].WorldPosition; var rear = t.Cars[t.Cars.Count - 1].WorldPosition;
            var f = Rel(front, rear.TileX, rear.TileZ) - rear.XNAMatrix.Translation; f.Y = 0;
            if (f.LengthSquared() < 1) { f = front.XNAMatrix.Forward; f.Y = 0; }
            return f.LengthSquared() < 1e-6f ? Vector3.Forward : Vector3.Normalize(f);
        }

        // el primer andén del camino del tren a menos de «max» m por delante de la cabeza (o bajo ella): sus andenes (índice y
        // sentido de la sección) y de dónde a dónde va, en m desde la cabeza
        static (List<(int idx, int dir)> plats, float start, float end)? PlatformAhead(Simulator sim, Train t, float max)
        {
            try
            {
                var route = t.ValidRoute[0]; var pos = t.PresentPosition[0]; int i0 = pos.RouteListIndex;
                if (route == null || i0 < 0) return null;
                float d = -pos.TCOffset;
                for (int i = i0; i < route.Count && d < max; i++)
                {
                    var el = route[i]; var tcs = sim.Signals.TrackCircuitList[el.TCSectionIndex];
                    if (tcs.PlatformIndex != null && tcs.PlatformIndex.Count > 0)
                    {
                        var plats = new List<(int idx, int dir)>(); float start = d, end = d + tcs.Length;
                        foreach (var x in tcs.PlatformIndex) plats.Add((x, el.Direction));
                        for (int j = i + 1; j < route.Count && end - start < 600; j++)
                        {
                            var e2 = route[j]; var s2 = sim.Signals.TrackCircuitList[e2.TCSectionIndex];
                            if (s2.PlatformIndex == null || !s2.PlatformIndex.Any(x => plats.Any(q => q.idx == x))) break;
                            end += s2.Length;
                            foreach (var x in s2.PlatformIndex) if (!plats.Any(q => q.idx == x)) plats.Add((x, e2.Direction));
                        }
                        return (plats, start, end);
                    }
                    d += tcs.Length;
                }
            }
            catch { }
            return null;
        }

        // el andén entre sus dos extremos, cada 4 m, en el sentido de la marcha del tren (la altura del carril, la de los extremos)
        static List<(Vector3 p, Vector3 fwd)> ChordSamples(Station s, Train train)
        {
            var res = new List<(Vector3, Vector3)>();
            var tf = TrainFwd(train);
            foreach (var (_, _, ends) in s.Decl)
            {
                if (ends.Length < 2) continue;
                Vector3 a = ends[0], b = ends[1]; var d = b - a; d.Y = 0; float L = d.Length(); if (L < 4 || L > 1500) continue;
                var f = d / L; if (Vector3.Dot(f, tf) < 0) { var t = a; a = b; b = t; f = -f; }
                for (float x = -2; x <= L + 2; x += 4) { var q = a + f * x; q.Y = a.Y + (b.Y - a.Y) * MathHelper.Clamp(x / L, 0, 1); res.Add((q, f)); }
            }
            return res;
        }

        static bool NearEnds(Station s, Vector3 p, float r)
        {
            if (s.Ends.Count == 0) return true;
            for (int i = 0; i < s.Ends.Count; i++)
                for (int j = i; j < s.Ends.Count; j++)
                {
                    Vector3 a = s.Ends[i], b = s.Ends[j]; a.Y = b.Y = p.Y = 0;
                    var ab = b - a; float l2 = ab.LengthSquared();
                    float t = l2 < 1e-3f ? 0 : MathHelper.Clamp(Vector3.Dot(p - a, ab) / l2, 0, 1);
                    if ((a + ab * t - p).LengthSquared() < r * r) return true;
                }
            return false;
        }
        static Func<object, object> _tdb, _trackDB, _trItems, _tiX, _tiY, _tiZ, _tiTX, _tiTZ;
        static IEnumerable<Vector3> PlatformEnds(Simulator sim, int plat, int tx, int tz)
        {
            var res = new List<Vector3>();
            try
            {
                var pd = sim.Signals.PlatformDetailsList[plat];
                if (_tdb == null) _tdb = Member(sim.GetType(), "TDB");
                var tdb = _tdb?.Invoke(sim); if (tdb == null) return res;
                if (_trackDB == null) _trackDB = Member(tdb.GetType(), "TrackDB");
                var db = _trackDB?.Invoke(tdb); if (db == null) return res;
                if (_trItems == null) _trItems = Member(db.GetType(), "TrItemTable");
                if (!(_trItems?.Invoke(db) is Array table)) return res;
                foreach (int r in pd.PlatformReference)
                {
                    if (r < 0 || r >= table.Length) continue;
                    var it = table.GetValue(r); if (it == null) continue;
                    if (_tiX == null) { var t = it.GetType(); while (t.BaseType != null && t.BaseType != typeof(object)) t = t.BaseType; _tiX = Member(t, "X"); _tiY = Member(t, "Y"); _tiZ = Member(t, "Z"); _tiTX = Member(t, "TileX"); _tiTZ = Member(t, "TileZ"); }
                    if (_tiX == null || _tiTX == null) break;
                    float x = Convert.ToSingle(_tiX(it)), y = Convert.ToSingle(_tiY(it)), z = Convert.ToSingle(_tiZ(it));
                    int ix = Convert.ToInt32(_tiTX(it)), iz = Convert.ToInt32(_tiTZ(it));
                    res.Add(new Vector3(x + (ix - tx) * 2048, y, -z - (iz - tz) * 2048));
                }
            }
            catch { }
            return res;
        }

        // el lado de un andén en la marcha del tren: +1 izquierda · −1 derecha · 0 los dos o sin dato. Como abre Open Rails las
        // puertas de sus trenes (TTTrain): «derecha» = (sentido de la sección 1) XOR (la referencia [0] del andén es su cabeza,
        // PlatformFrontUiD); con eso, PlatformSide[0] es la derecha y [1] la izquierda; si no, al revés.
        static int SideOf(Orts.Simulation.Signalling.PlatformDetails pd, int dir)
        {
            bool p0 = pd.PlatformSide[0], p1 = pd.PlatformSide[1];
            if (p0 == p1) return 0;
            bool right0 = (dir == 1) ^ (pd.PlatformReference[0] == pd.PlatformFrontUiD);
            int side0 = right0 ? -1 : 1;
            return p0 ? side0 : -side0;
        }

        // los coches con viajeros: todo lo que no es de mercancías ni ténder, menos las locomotoras de verdad (70 t o más); si el
        // vehículo dice que lleva viajeros (PassengerCapacity), siempre
        static FieldInfo _hasPax; static bool _paxLooked;
        static List<TrainCar> PaxCars(Train t)
        {
            if (!_paxLooked) { _paxLooked = true; _hasPax = AccessTools.Field(typeof(TrainCar), "HasPassengerCapacity"); }
            bool Pax(TrainCar c)
            {
                try { if (_hasPax != null && (bool)_hasPax.GetValue(c)) return true; } catch { }
                if (c.WagonType == TrainCar.WagonTypes.Freight || c.WagonType == TrainCar.WagonTypes.Tender) return false;
                return !(c is MSTSLocomotive) || c.MassKG < 70000;
            }
            return t.Cars.Where(Pax).ToList();
        }

        // las plazas de un coche: PassengerCapacity de su .wag/.eng; si no lo dice, unas 3 por metro de largo
        static readonly Dictionary<string, int> _seats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static readonly Regex SeatsRx = new Regex(@"PassengerCapacity\s*\(\s*(\d+)", RegexOptions.IgnoreCase);
        static int Seats(TrainCar c)
        {
            string f = c.WagFilePath ?? "";
            if (!_seats.TryGetValue(f, out int n))
            {
                n = 0;
                try
                {
                    if (File.Exists(f))
                    {
                        var b = File.ReadAllBytes(f);
                        string txt = b.Length > 1 && b[0] == 0xFF && b[1] == 0xFE ? System.Text.Encoding.Unicode.GetString(b) : System.Text.Encoding.UTF8.GetString(b);
                        var m = SeatsRx.Match(txt); if (m.Success) n = int.Parse(m.Groups[1].Value);
                    }
                }
                catch { }
                _seats[f] = n;
            }
            return n > 0 ? n : (int)(c.CarLengthM * 3);
        }

        // las puertas de un coche (en cada sitio, a lo largo del coche, desde su centro): dos por lado; una en los cortos
        static IEnumerable<float> DoorOffsets(TrainCar c)
        {
            float L = c.CarLengthM;
            if (L < 12) { yield return 0; yield break; }
            yield return -L * 0.28f; yield return L * 0.28f;
        }

        // «count» personas esperando en el andén del lado «side», repartidas al azar por los puntos medidos (a 0,75 m como poco
        // unas de otras). restrict: no delante de la cabina (a menos de 90 m), para que no se vean aparecer; near: un poco más
        // lejos de la cabina hacia atrás (llegan andando)
        static int SpawnWaiting(Station s, TrainCar lead, int side, int count, bool restrict, bool near)
        {
            var cab = Rel(lead.WorldPosition, s.TileX, s.TileZ);
            var ok = s.Spots.Where(q => q.side == side)
                .Where(q => { if (!restrict) return true; float a = Vector3.Dot(q.p - cab, q.fwd); return near ? a > 90 || a < -25 : a > 90 || a < -10; }).ToList();
            if (ok.Count == 0 || count <= 0) return 0;
            var here = _people.Where(p => p.TileX == s.TileX && p.TileZ == s.TileZ && p.Phase != 2).Select(p => p.Pos).ToList();
            int n = 0;
            for (int tries = 0; n < count && tries < count * 8 && _people.Count < MaxPeople; tries++)
            {
                var (p0, _, fwd) = ok[_rnd.Next(ok.Count)];
                var at = p0 + fwd * U(-1.5f, 1.5f) + Left(fwd) * U(-0.35f, 0.35f);
                if (here.Any(q => (q - at).LengthSquared() < 0.75f * 0.75f)) continue;
                here.Add(at);
                float face = HeadingOf(-Left(fwd) * side) + U(-0.9f, 0.9f);   // (hacia la vía, más o menos)
                _people.Add(new Person
                {
                    Shape = _rnd.Next(Figures), Pace = U(0.92f, 1.08f), Clock = U(0, 20), IdleRate = U(0.8f, 1.2f), IdleVar = _rnd.Next(8),
                    TileX = s.TileX, TileZ = s.TileZ, Pos = at, Target = at, Boards = true, Side = side,
                    Heading = face, Face = face, Base = face, Then = new Vector3(0, -1e6f, 0), Glance = U(0, 6)
                });
                n++;
            }
            return n;
        }

        // las puertas del lado sideV (vector del mundo hacia el andén): en el andén, dentro del coche, la marcha del coche y el carril
        static List<(Vector3 at, Vector3 inside, Vector3 fwd, float rail)> Doors(List<TrainCar> cars, Vector3 sideV, Station s)
        {
            var doors = new List<(Vector3 at, Vector3 inside, Vector3 fwd, float rail)>();
            foreach (var c in cars)
            {
                var m = c.WorldPosition.XNAMatrix; var fwd = m.Forward; fwd.Y = 0; if (fwd.LengthSquared() < 1e-6) continue; fwd.Normalize();
                float half = Math.Max(1.4f, c.CarWidthM / 2);
                var ctr = Rel(c.WorldPosition, s.TileX, s.TileZ);
                foreach (float off in DoorOffsets(c))
                {
                    var at = ctr + fwd * off + sideV * (half + 0.35f);
                    // solo las puertas que dan al andén medido (el tren puede ser más largo que el andén)
                    if (CanMeasure && !s.Spots.Any(q => { var d = q.p - at; d.Y = 0; return d.LengthSquared() < 6 * 6 && Vector3.Dot(q.p - ctr, sideV) > 1.5f; })) continue;
                    at.Y = ctr.Y + HeightNear(s, at, ctr.Y);
                    doors.Add((at, at - sideV * 1.3f, fwd, ctr.Y));   // (dentro: tapado por la carrocería)
                }
            }
            return doors;
        }

        // la puerta para alguien en pos: la que antes le deja subir (lo que anda hasta ella + 6 m por cada uno que ya va a esa puerta)
        static int PickDoor(List<(Vector3 at, Vector3 inside, Vector3 fwd, float rail)> doors, Vector3 pos, int[] load, out float dist)
        {
            int best = 0; float bc = float.MaxValue; dist = 0;
            for (int i = 0; i < doors.Count; i++)
            {
                float d = (doors[i].at - pos).Length(), c = d + load[i] * 6f;
                if (c < bc) { bc = c; best = i; dist = d; }
            }
            return best;
        }
        // la puerta por la que baja el siguiente: la que menos lleva (si empatan, una al azar)
        static int NextDown(int[] qa)
        {
            int min = qa.Min(); var ix = Enumerable.Range(0, qa.Length).Where(i => qa[i] == min).ToList();
            return ix[_rnd.Next(ix.Count)];
        }

        // el tren parado, puertas aún cerradas: los que esperan cerca de su lado se reparten junto a las puertas (sin taparlas)
        static void Gather(List<TrainCar> cars, Station s, Vector3 fwd)
        {
            var sideV = Left(fwd) * s.Side;
            var doors = Doors(cars, sideV, s); if (doors.Count == 0) return;
            int n = 0; var load = new int[doors.Count];
            foreach (var p in _people.Where(p => p.Boards && p.Phase == 0 && p.Side == s.Side && p.TileX == s.TileX && p.TileZ == s.TileZ)
                                     .OrderBy(p => doors.Min(d => (d.at - p.Pos).LengthSquared())))
            {
                int di = PickDoor(doors, p.Pos, load, out float dist);
                if (dist > 40) continue;
                load[di]++;
                var best = doors[di];
                float lr = _rnd.NextDouble() < 0.5 ? -1 : 1;
                var to = OnPlatform(s, best.at + sideV * U(0.9f, 2.6f) + best.fwd * lr * U(0.8f, 2.4f), s.Side);
                to.Y = best.rail + HeightNear(s, to, best.rail);
                p.Target = to; p.Then = new Vector3(0, -1e6f, 0); p.Phase = 1; p.Wait = U(0, 2.5f);
                var look = best.at - to; look.Y = 0; p.FaceEnd = look.LengthSquared() > 1e-4f ? HeadingOf(Vector3.Normalize(look)) : p.Heading;
                n++;
            }
            if (n > 0) Agent.Log($"viajeros en los andenes: tren parado: {n} se acercan a las puertas");
        }

        // puertas abiertas: por cada puerta, primero bajan (desde dentro) y luego suben los que esperan cerca, uno detrás de otro.
        // Cuántos: según las plazas del tren y los que van a bordo
        static void Board(List<TrainCar> cars, Vector3 sideV, Station s, Train train)
        {
            sideV.Y = 0; if (sideV.LengthSquared() < 1e-6f) return; sideV.Normalize();
            var doors = Doors(cars, sideV, s);
            if (doors.Count == 0) return;
            int seats = cars.Sum(Seats);
            if (_onboardTrain != train.Number || _onboardCap != seats || _onboard < 0) { _onboardTrain = train.Number; _onboardCap = seats; _onboard = (int)(seats * U(0.3f, 0.6f)); }
            int alight = Math.Min(_onboard, (int)Math.Round(_onboard * U(0.15f, 0.4f)));
            int free = Math.Max(0, seats - (_onboard - alight));
            // los que suben: los de ese lado a menos de 40 m de una puerta, los más cercanos primero, hasta llenar
            var cands = _people.Where(p => p.Boards && !p.HideAtEnd && p.Phase != 2 && p.Side == (Vector3.Dot(sideV, Left(TrainFwd(train))) >= 0 ? 1 : -1) && p.TileX == s.TileX && p.TileZ == s.TileZ)
                               .Where(p => doors.Min(d => (d.at - p.Pos).Length()) <= 40)
                               .OrderBy(p => doors.Min(d => (d.at - p.Pos).LengthSquared())).ToList();
            int waiting = cands.Count, boardN = Math.Min(waiting, free);
            // los que bajan, repartidos entre las puertas (los que se ven: como mucho, los que caben)
            int showAlight = Math.Min(alight, Math.Max(0, MaxPeople - _people.Count));
            var down = new int[doors.Count];
            for (int k = 0; k < showAlight; k++) down[NextDown(down)]++;   // (repartidos entre todas las puertas)
            var queue = new int[doors.Count];
            foreach (var p in cands.Take(boardN))
            {
                int di = PickDoor(doors, p.Pos, queue, out _);
                var best = doors[di];
                p.Target = best.at; p.Then = best.inside; p.HideAtEnd = true; p.Phase = 1;
                p.Wait = 1.2f + down[di] * 1.9f + queue[di] * 1.6f + U(0, 0.6f);   // (cuando han bajado los de esa puerta)
                queue[di]++;
            }
            for (int di = 0; di < doors.Count; di++)
            {
                var (at, inside, fwd, rail) = doors[di];
                for (int k = 0; k < down[di] && _people.Count < MaxPeople; k++)
                {
                    var (out1, out2) = AwayFromDoor(s, at, fwd, sideV, rail);
                    _people.Add(new Person
                    {
                        Shape = _rnd.Next(Figures), Pace = U(0.92f, 1.08f), Clock = U(0, 20), IdleRate = U(0.8f, 1.2f), IdleVar = _rnd.Next(8),
                        TileX = s.TileX, TileZ = s.TileZ, Pos = inside, Target = at, Then = out1, Side = s.Side,
                        Phase = 1, Wait = 0.8f + k * 1.9f + U(0, 0.4f), Heading = HeadingOf(sideV)
                    });
                    _later.Add((_people[_people.Count - 1], out2));   // (después de la puerta, se alejan: out1 → out2)
                }
            }
            _onboard = _onboard - alight + boardN;
            Agent.Log($"viajeros en los andenes: puertas abiertas a la {(s.Side > 0 ? "izquierda" : "derecha")} ({cars.Count} coches, {doors.Count} puertas, {seats} plazas): "
                      + $"bajan {alight}{(showAlight < alight ? $" (se ven {showAlight})" : "")} · suben {boardN} de {waiting} esperando cerca · a bordo {_onboard}");
        }
        // (el tercer tramo de los que bajan: Person solo guarda dos)
        static readonly List<(Person p, Vector3 to)> _later = new List<(Person, Vector3)>();
        static void Chain()
        {
            for (int i = _later.Count - 1; i >= 0; i--)
            {
                var (p, to) = _later[i];
                if (p.Phase != 1) { _later.RemoveAt(i); continue; }
                if (p.Then.Y < -1e5f) { p.Then = to; _later.RemoveAt(i); }   // (ya va hacia out1: luego, out2)
            }
        }

        // de qué lado (en el mundo) están abiertas las puertas (null: cerradas). Las «izquierdas» de cada coche, a su izquierda
        // (la del modelo, ya girado si va al revés).
        static FieldInfo _doors, _dl, _dr, _dlo, _dro; static PropertyInfo _state; static bool _doorsLooked;
        static Vector3? OpenDoorSide(List<TrainCar> cars)
        {
            if (!_doorsLooked)
            {
                _doorsLooked = true;
                var w = typeof(MSTSWagon);
                _doors = AccessTools.Field(w, "Doors");
                if (_doors != null) { _dl = AccessTools.Field(_doors.FieldType, "LeftDoor"); _dr = AccessTools.Field(_doors.FieldType, "RightDoor"); _state = _dl == null ? null : AccessTools.Property(_dl.FieldType, "State"); }
                _dlo = AccessTools.Field(w, "DoorLeftOpen"); _dro = AccessTools.Field(w, "DoorRightOpen");
            }
            Vector3 sum = Vector3.Zero; int n = 0;
            foreach (var c in cars)
            {
                if (!(c is MSTSWagon w)) continue;
                bool l = false, r = false;
                try
                {
                    if (_doors != null && _state != null)
                    {
                        var d = _doors.GetValue(w);
                        string ls = _state.GetValue(_dl.GetValue(d))?.ToString(), rs = _state.GetValue(_dr.GetValue(d))?.ToString();
                        l = ls == "Open" || ls == "Opening"; r = rs == "Open" || rs == "Opening";
                    }
                    else if (_dlo != null) { l = (bool)_dlo.GetValue(w); r = _dro != null && (bool)_dro.GetValue(w); }
                }
                catch { }
                if (!l && !r) continue;
                var m = c.WorldPosition.XNAMatrix;
                if (l) { sum += m.Left; n++; }
                if (r) { sum += m.Right; n++; }
            }
            if (n == 0) return null;
            sum.Y = 0; return sum.LengthSquared() < 1e-6 ? (Vector3?)null : Vector3.Normalize(sum);
        }

        // =====================================================================================================================
        // El andén, medido en el escenario
        static bool CanMeasure => _scenery != null && _worldFiles != null && _sceneryObjects != null && _location != null && _sharedShape != null && _filePath != null;

        // los suelos de un modelo: sus triángulos casi horizontales, en el espacio del modelo, con una rejilla de 4 m
        sealed class Floors
        {
            public Vector3[] P = Array.Empty<Vector3>(); public Vector3 Min, Max;
            public readonly Dictionary<long, List<int>> Grid = new Dictionary<long, List<int>>();
        }
        static readonly object _measureLock = new object();
        static readonly ConcurrentDictionary<string, Floors> _floors = new ConcurrentDictionary<string, Floors>(StringComparer.OrdinalIgnoreCase);
        static long Cell(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;
        static int Cl(float v) => (int)Math.Floor(v / 4);

        static Matrix FromMsts(matrix m) => new Matrix(m.AX, m.AY, -m.AZ, 0, m.BX, m.BY, -m.BZ, 0, -m.CX, -m.CY, m.CZ, 0, m.DX, m.DY, -m.DZ, 1);

        // como SharedShape: cada vértice con la matriz de su nodo y la de sus padres, sin la raíz (la del nodo sin padre)
        static Floors LoadFloors(string path) => _floors.GetOrAdd(path, file =>
        {
            var r = new Floors();
            try
            {
                try { if (new FileInfo(file).Length > 24L << 20) return r; } catch { }   // (modelos enormes: ni se miran)
                var sh = new ShapeFile(file, false).shape;
                var mats = sh.matrices.Select(FromMsts).ToArray();
                var dl = sh.lod_controls[0].distance_levels[0]; var hier = dl.distance_level_header.hierarchy;
                var chain = new Matrix[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    var x = Matrix.Identity;
                    for (int k = i, guard = 0; k >= 0 && k < hier.Length && hier[k] != -1 && guard < 64; k = hier[k], guard++) x *= mats[k];
                    chain[i] = x;
                }
                var pts = new List<Vector3>();
                foreach (sub_object so in dl.sub_objects)
                    foreach (primitive pr in so.primitives)
                    {
                        var idx = pr.indexed_trilist?.vertex_idxs; if (idx == null) continue;
                        int im = sh.vtx_states[sh.prim_states[pr.prim_state_idx].ivtx_state].imatrix;
                        var M = im >= 0 && im < chain.Length ? chain[im] : Matrix.Identity;
                        foreach (vertex_idx t in idx)
                        {
                            Vector3 V(int vi) { var q = sh.points[so.vertices[vi].ipoint]; return Vector3.Transform(new Vector3(q.X, q.Y, -q.Z), M); }
                            Vector3 a = V(t.a), b = V(t.b), c = V(t.c);
                            var n = Vector3.Cross(b - a, c - a); if (n.LengthSquared() < 1e-8f) continue; n.Normalize();
                            if (Math.Abs(n.Y) < 0.8f) continue;   // (solo lo casi horizontal: suelos)
                            // y hacia arriba (las normales del modelo): fuera techos por debajo y bajos de los andenes
                            Vector3 N(int vi) { try { var q = sh.normals[so.vertices[vi].inormal]; return Vector3.TransformNormal(new Vector3(q.X, q.Y, -q.Z), M); } catch { return Vector3.Zero; } }
                            var vn = N(t.a) + N(t.b) + N(t.c);
                            if (vn.LengthSquared() > 1e-6f && Vector3.Normalize(vn).Y < 0.7f) continue;
                            pts.Add(a); pts.Add(b); pts.Add(c);
                        }
                    }
                r.P = pts.ToArray();
                if (r.P.Length == 0) return r;
                r.Min = new Vector3(r.P.Min(q => q.X), r.P.Min(q => q.Y), r.P.Min(q => q.Z)); r.Max = new Vector3(r.P.Max(q => q.X), r.P.Max(q => q.Y), r.P.Max(q => q.Z));
                long entries = 0;
                for (int i = 0; i + 2 < r.P.Length; i += 3)
                {
                    Vector3 a = r.P[i], b = r.P[i + 1], c = r.P[i + 2];
                    int x0 = Cl(Math.Min(a.X, Math.Min(b.X, c.X))), x1 = Cl(Math.Max(a.X, Math.Max(b.X, c.X))), z0 = Cl(Math.Min(a.Z, Math.Min(b.Z, c.Z))), z1 = Cl(Math.Max(a.Z, Math.Max(b.Z, c.Z)));
                    if (x1 - x0 > 100 || z1 - z0 > 100) continue;   // (terrenos enormes, fuera)
                    if (entries > 3000000) break;   // (tope de memoria: modelos raros con miles de losas grandes)
                    for (int gx = x0; gx <= x1; gx++)
                        for (int gz = z0; gz <= z1; gz++)
                        {
                            entries++;
                            long k = Cell(gx, gz);
                            if (!r.Grid.TryGetValue(k, out var l)) r.Grid[k] = l = new List<int>();
                            l.Add(i);
                        }
                }
            }
            catch { }
            return r;
        });

        // el suelo más bajo en (x, z) del modelo, entre lo y hi (el del andén, no un banco encima); NaN si no hay
        static float FloorAt(Floors t, float x, float z, float lo, float hi)
        {
            if (!t.Grid.TryGetValue(Cell(Cl(x), Cl(z)), out var l)) return float.NaN;
            float best = float.NaN;
            foreach (int i in l)
            {
                Vector3 a = t.P[i], b = t.P[i + 1], c = t.P[i + 2];
                float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z); if (Math.Abs(d) < 1e-9f) continue;
                float w1 = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d, w2 = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d, w3 = 1 - w1 - w2;
                if (w1 < -1e-4f || w2 < -1e-4f || w3 < -1e-4f) continue;
                float y = w1 * a.Y + w2 * b.Y + w3 * c.Y;
                if (y >= lo && y <= hi && (float.IsNaN(best) || y < best)) best = y;
            }
            return best;
        }

        // los objetos del escenario a menos de r m (en la tesela tx, tz), con su matriz
        static List<(string path, Matrix m)> NearObjects(int tx, int tz, Vector3 at, float r)
        {
            var res = new List<(string, Matrix)>();
            if (!CanMeasure || _world == null) return res;
            try
            {
                var wfs = (_worldFiles(_scenery(_world)) as IEnumerable)?.Cast<object>().ToArray() ?? Array.Empty<object>();
                foreach (var wf in wfs)
                {
                    var objs = (_sceneryObjects(wf) as IEnumerable)?.Cast<object>().ToArray(); if (objs == null) continue;
                    foreach (var o in objs)
                    {
                        try
                        {
                            var ss = _sharedShape(o); if (ss == null) continue;
                            if (!(_filePath(ss) is string p) || p.Length == 0) continue;
                            var locs = _groups.TryGetValue(o, out var g) ? g : (_location(o) is WorldPosition loc ? new List<WorldPosition> { loc } : null);
                            if (locs == null) continue;
                            foreach (var l in locs)
                            {
                                var m = l.XNAMatrix; m.Translation = Rel(l, tx, tz);
                                var dd = m.Translation - at; dd.Y = 0; if (dd.LengthSquared() > r * r) continue;
                                res.Add((p, m));
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return res;
        }

        // la vía del camino del tren, cada 4 m, de «from» a «to» m por delante de la cabeza (negativo: por detrás)
        static List<(Vector3 p, Vector3 fwd)> TrackSamplesAt(Train t, float from, float to, int tx, int tz)
        {
            var res = new List<(Vector3, Vector3)>();
            try
            {
                var tr = new Traveller(t.FrontTDBTraveller);
                if (from < 0) { tr.ReverseDirection(); tr.Move(-from); tr.ReverseDirection(); } else if (from > 0) tr.Move(from);
                Vector3 Here() => new Vector3(tr.X + (tr.TileX - tx) * 2048, tr.Y, -tr.Z - (tr.TileZ - tz) * 2048);
                var prev = Here();
                for (float d = from; d < to; d += 4)
                {
                    if (tr.Move(4) > 0.01f) break;   // (fin de la vía)
                    var p = Here(); var f = p - prev; f.Y = 0;
                    if (f.LengthSquared() > 1e-4f) res.Add((p, Vector3.Normalize(f)));
                    prev = p;
                }
            }
            catch { }
            return res;
        }

        // el andén: en cada punto de la vía, a los dos lados (de 2,4 a 5,7 m del eje), el suelo entre 0,2 y 1,4 m sobre el carril.
        // El lado, respecto a la marcha en ese punto (+1 a la izquierda)
        static void Measure(Station s, Train train, float from, float to, bool aroundTrain = false, bool addToBefore = false)
        {
            var track = TrackSamplesAt(train, from, to, s.TileX, s.TileZ);
            if (track.Count == 0) { s.Measured = true; return; }
            if (aroundTrain && s.Ends.Count > 0) { var near = track.Where(t => NearEnds(s, t.p, 60)).ToList(); if (near.Count > 0) track = near; }   // (solo junto al andén)
            var before = addToBefore ? s.Spots.ToList() : null;
            // solo entre los extremos del andén que da la ruta (las secciones de vía con andén pueden ser muy largas)
            if (!aroundTrain && s.Ends.Count > 0)
            {
                var hits = new List<int>();
                foreach (var e in s.Ends)
                {
                    int bi = -1; float bd = 40 * 40;
                    for (int i = 0; i < track.Count; i++) { var d = track[i].p - e; d.Y = 0; float l = d.LengthSquared(); if (l < bd) { bd = l; bi = i; } }
                    if (bi >= 0) hits.Add(bi);
                }
                if (hits.Count > 0)
                {
                    int pad = hits.Count >= 2 ? 6 : 40;   // (±24 m; con un solo extremo, ±160 m)
                    int i0 = Math.Max(0, hits.Min() - pad), i1 = Math.Min(track.Count - 1, hits.Max() + pad);
                    track = track.GetRange(i0, i1 - i0 + 1);
                }
                else
                {
                    // la vía medida no pasa por el andén (las agujas aún no van hacia él, o el tramo es muy largo): se mide sobre el
                    // propio andén, de un extremo a otro (con la cuerda: en curva, algo apartado del eje)
                    var chord = ChordSamples(s, train);
                    if (chord.Count > 0) { track = chord; Agent.Log("viajeros en los andenes: la vía no pasa por los extremos del andén: se mide de un extremo a otro del andén"); }
                    else if (track.Count > 160) track = track.GetRange(0, 160);   // (sin extremos en la vía: los primeros ~640 m)
                }
            }
            // lo que cubre cada andén declarado sobre esta vía (m a lo largo de ella, de extremo a extremo, ±2 m) y su lado: solo
            // ahí se mide (como en el mapa grande: ni más allá de sus extremos ni al otro lado de la vía)
            var along = new float[track.Count];
            for (int i = 1; i < track.Count; i++) { var d = track[i].p - track[i - 1].p; d.Y = 0; along[i] = along[i - 1] + d.Length(); }
            var cover = new List<(float a0, float a1, int side)>();
            foreach (var (side, _, ends) in s.Decl)
            {
                if (ends.Length == 0) { cover.Add((float.MinValue, float.MaxValue, side)); continue; }
                var xs = new List<float>();
                foreach (var e in ends)
                {
                    int bi = -1; float bd = 40 * 40;
                    for (int i = 0; i < track.Count; i++) { var d = track[i].p - e; d.Y = 0; float l = d.LengthSquared(); if (l < bd) { bd = l; bi = i; } }
                    if (bi >= 0) { var d = e - track[bi].p; d.Y = 0; xs.Add(along[bi] + Vector3.Dot(d, track[bi].fwd)); }
                    else { var d0 = e - track[0].p; var d1 = e - track[track.Count - 1].p; d0.Y = d1.Y = 0; xs.Add(d0.LengthSquared() < d1.LengthSquared() ? -1e6f : 1e6f); }
                }
                float a0 = xs.Min(), a1 = xs.Max();
                if (xs.Count == 1) { a0 -= 160; a1 += 160; } else { a0 -= 2; a1 += 2; }
                cover.Add((a0, a1, side));
            }
            bool anyKnown = s.Decl.Any(x => x.known);
            bool Allowed(int i, int side) => s.Decl.Count == 0 || cover.Any(c => (c.side == 0 || c.side == side) && along[i] >= c.a0 && along[i] <= c.a1);
            var mid = track[track.Count / 2].p; float reach = (track[0].p - track[track.Count - 1].p).Length() / 2 + 150;
            var objs = NearObjects(s.TileX, s.TileZ, mid, reach);
            // solo los que están a menos de 80 m de la vía (los andenes; fuera edificios y demás lejos: se mide mucho antes)
            objs = objs.Where(o => track.Any(t => { var d = o.m.Translation - t.p; d.Y = 0; return d.LengthSquared() < 80 * 80; })).Take(800).ToList();
            if (_floors.Count > 400) _floors.Clear();   // (los suelos de estaciones pasadas: fuera de la memoria)
            Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                lock (_measureLock)   // (de una en una)
                try
                {
                    var insts = new List<(Floors f, Matrix m, Matrix inv)>();
                    foreach (var (path, m) in objs)
                    {
                        var fl = LoadFloors(path); if (fl.Grid.Count == 0) continue;
                        insts.Add((fl, m, Matrix.Invert(m)));
                    }
                    var spots = new List<(Vector3, int, Vector3)>(); var hs = new List<float>(); int outside = 0;
                    for (int ti = 0; ti < track.Count; ti++)
                    {
                        var (p, f) = track[ti];
                        var left = Left(f);
                        foreach (int side in new[] { 1, -1 })
                        {
                            if (!Allowed(ti, side)) { outside++; continue; }
                            for (float d = 2.4f; d <= 5.7f; d += 1.1f)
                            {
                                var w = p + left * (side * d);
                                float best = float.NaN;
                                foreach (var (fl, m, inv) in insts)
                                {
                                    var top = Vector3.Transform(new Vector3(w.X, p.Y + 1.4f, w.Z), inv);
                                    var bot = Vector3.Transform(new Vector3(w.X, p.Y + 0.2f, w.Z), inv);
                                    if (top.X < fl.Min.X || top.X > fl.Max.X || top.Z < fl.Min.Z || top.Z > fl.Max.Z) continue;
                                    float y = FloorAt(fl, top.X, top.Z, Math.Min(bot.Y, top.Y), Math.Max(bot.Y, top.Y));
                                    if (float.IsNaN(y)) continue;
                                    float wy = Vector3.Transform(new Vector3(top.X, y, top.Z), m).Y;
                                    if (float.IsNaN(best) || wy < best) best = wy;
                                }
                                if (!float.IsNaN(best)) { spots.Add((new Vector3(w.X, best, w.Z), side, f)); hs.Add(best - p.Y); }
                            }
                        }
                    }
                    // a cada lado, solo los puntos a la altura del andén (fuera lo que se aparta más de 30 cm de su mediana)
                    float Med(IEnumerable<float> v) { var a = v.OrderBy(x => x).ToList(); return a.Count == 0 ? float.NaN : a[a.Count / 2]; }
                    var keep = new List<(Vector3, int, Vector3)>(); var keptH = new List<float>(); int dropped = 0; var meds = new List<string>();
                    foreach (int side in new[] { 1, -1 })
                    {
                        var ix = Enumerable.Range(0, spots.Count).Where(i => spots[i].Item2 == side).ToList();
                        if (ix.Count == 0) continue;
                        float med = Med(ix.Select(i => hs[i]));
                        meds.Add($"{(side > 0 ? "izquierda" : "derecha")} a {med:0.00} m");
                        foreach (int i in ix) { if (Math.Abs(hs[i] - med) <= 0.3f) { keep.Add(spots[i]); keptH.Add(hs[i]); } else dropped++; }
                    }
                    // la ruta no dice el lado: el que tiene andén (si el otro tiene mucho menos, fuera: suele ser otra cosa)
                    string guessed = "";
                    if (s.Decl.Count > 0 && !anyKnown)
                    {
                        int nl = keep.Count(k => k.Item2 == 1), nr = keep.Count(k => k.Item2 == -1);
                        int drop = nl < nr * 0.6f ? 1 : nr < nl * 0.6f ? -1 : 0;
                        if (drop != 0) { keep.RemoveAll(k => k.Item2 == drop); guessed = $" · la ruta no dice el lado: a la {(drop > 0 ? "derecha" : "izquierda")}, que es donde hay andén"; }
                    }
                    if (keptH.Count > 0) s.TypicalH = Med(keptH);
                    if (before != null) keep.AddRange(before.Where(b => !keep.Any(k => (k.Item1 - b.p).LengthSquared() < 1)));
                    s.Spots = keep;
                    Agent.Log($"viajeros en los andenes: medido el andén en {sw.ElapsedMilliseconds} ms ({objs.Count} objetos cerca, {insts.Count} con suelo, {track.Count} puntos de vía)"
                              + (meds.Count > 0 ? " · andén " + string.Join(", ", meds) : "") + (dropped > 0 ? $" · {dropped} puntos fuera (bancos, otros niveles)" : "")
                              + (outside > 0 ? $" · {outside} tramos fuera del andén declarado" : "") + guessed);
                }
                catch (Exception ex) { Agent.Log("viajeros en los andenes (medir el andén): " + ex.GetType().Name + " " + ex.Message); }
                finally { s.Measured = true; }
            });
        }

        // un punto del andén medido de ese lado: el mismo si está a menos de 2 m de lo medido; si no, el punto medido más cercano
        static Vector3 OnPlatform(Station s, Vector3 at, int side)
        {
            float best = float.MaxValue; var bp = at;
            foreach (var (p, sd, _) in s.Spots)
            {
                if (sd != side) continue;
                float d = (p.X - at.X) * (p.X - at.X) + (p.Z - at.Z) * (p.Z - at.Z);
                if (d < best) { best = d; bp = p; }
            }
            return best == float.MaxValue || best < 2f * 2f ? at : bp + new Vector3(U(-0.4f, 0.4f), 0, U(-0.4f, 0.4f));
        }
        static bool IsOnPlatform(Station s, Vector3 at, int side) =>
            s.Spots.Count == 0 || s.Spots.Any(q => q.side == side && (q.p.X - at.X) * (q.p.X - at.X) + (q.p.Z - at.Z) * (q.p.Z - at.Z) < 2f * 2f);
        // a dónde se alejan los que bajan: por el andén, hacia donde quede andén (si por un lado se acaba, por el otro)
        static (Vector3 out1, Vector3 out2) AwayFromDoor(Station s, Vector3 at, Vector3 fwd, Vector3 sideV, float rail)
        {
            var out1 = OnPlatform(s, at + sideV * U(2.0f, 4.5f), s.Side);
            float along = (_rnd.NextDouble() < 0.5 ? -1 : 1) * U(12, 37);
            var out2 = out1 + fwd * along;
            if (!IsOnPlatform(s, out2, s.Side)) { var o = out1 - fwd * along; out2 = IsOnPlatform(s, o, s.Side) ? o : OnPlatform(s, out2, s.Side); }
            out1.Y = rail + HeightNear(s, out1, rail); out2.Y = rail + HeightNear(s, out2, rail);
            return (out1, out2);
        }

        // la altura del andén sobre el carril junto a un punto: la del punto medido más cercano (a menos de 6 m); si no, la típica
        // de ese andén (y si no se midió nada, la fija)
        static float HeightNear(Station s, Vector3 at, float railY)
        {
            float best = 36, h = s.TypicalH;
            foreach (var (p, _, _) in s.Spots)
            {
                float d = (p.X - at.X) * (p.X - at.X) + (p.Z - at.Z) * (p.Z - at.Z);
                if (d < best) { best = d; h = p.Y - railY; }
            }
            return h;
        }
    }
}
