// (1.2.85, prueba, petición) Gente en los andenes, en el 3D de Open Rails: suben y bajan de tu tren.
// Solo en las rutas que traen las figuras de personas de la CGL (SHAPES\ani33-1.s y compañía, con permiso de sus autores).
//  · El andén se MIDE en el escenario: a lo largo de la vía (desde la cola hasta 170 m por delante de la cabeza), a los dos
//    lados, se busca en los modelos cargados cerca (World.Scenery → sus objetos; los triángulos del .s, compuestos como los
//    compone Open Rails) la superficie horizontal entre 0,2 y 1,4 m sobre el carril. Solo se pone gente donde hay andén, y a
//    su altura. Se mide en segundo plano al entrar en la estación.
//  · Al entrar en una estación (la cabeza en una sección con andén, por debajo de 60 km/h): gente esperando del lado del andén
//    (el de la ruta, PlatformDetails.PlatformSide: [0] a la derecha del sentido de la sección, [1] a la izquierda; o el único
//    lado donde se ha medido andén), solo lejos por delante de la cabina o por detrás de ella, para que no aparezcan de golpe a
//    la vista. Con andén a los dos lados, nadie hasta saber de qué lado se abren las puertas.
//  · Puertas abiertas: si es del otro lado, fuera los que esperaban allí. Los que esperan cerca van a la puerta más cercana y
//    entran (hasta quedar dentro del coche, ocultos); por cada puerta salen otros DESDE DENTRO del coche, cruzan la puerta y se
//    alejan por el andén, y se quedan allí.
//  · Al irse el tren: la gente sigue en el andén hasta que está a más de 400 m.
// Las figuras no tienen animación de andar: se desplazan a paso de persona.
// Dibujo: los modelos se piden al gestor de Open Rails en el hilo de carga (World.Load), se marcan como en uso para que no se
// descarguen (Viewer.Mark, antes del barrido) y en cada fotograma se dibuja cada figura (World.PrepareFrame →
// SharedShape.PrepareFrame). Por reflexión: vale para los Open Rails de .NET 6 y de .NET Framework.
// Solo si SelectOR lo pide al lanzar (SELECTOR_VIAJEROS3D=1: Ajustes → Gráficos, «Gente en los andenes»).

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
    public static class Viajeros3D
    {
        // figuras sueltas (de 1,3 a 2 m, ligeras: las de 25 000 puntos o más, fuera)
        static readonly string[] Models =
        {
            "ANI12-01.s", "ANI12-02.s", "ANI12-03.s", "ANI12-04.s", "ANI12-05.s", "ANI12-08.s", "ANI12-09.s", "ANI12-010.s", "ANI12-011.s",
            "ani33-1.s", "ani33-2.s", "ani33-3.s", "ani33-4.s", "ani33-5.s", "ani33-6.s", "ani33-7.s",
            "ANI16.s", "ANI2.s", "ANI1.s", "ANI4.s", "ANI7.s", "ani25.s",
        };
        const float DefaultH = 0.70f;       // alto del andén sin medir (m sobre el carril)
        const float WalkMps = 1.25f;
        const int MaxPeople = 80;

        static bool _on, _said;
        static string _route;
        static List<string> _paths;
        static readonly List<object> _shapes = new List<object>();       // SharedShape cargadas (en el orden de _paths)
        static MethodInfo _get, _prep, _mark;
        static Func<object, object> _shapeMgr, _worldViewer, _scenery, _worldFiles, _sceneryObjects, _location, _sharedShape, _filePath;
        static object _flags, _world;
        static readonly Random _rnd = new Random();
        static readonly Stopwatch _clock = Stopwatch.StartNew();
        static double _last;

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
            public int Shape, TileX, TileZ; public Vector3 Pos, Target, Then; public float Wait, Heading;
            public int Phase;                 // 0 quieto · 1 andando · 2 fuera
            public bool Boards, HideAtEnd;    // (Then.Y < -1e5: no hay siguiente tramo)
        }
        static readonly List<Person> _people = new List<Person>();

        // la estación en curso: su andén medido (puntos por lado: +1 a la izquierda de la marcha, −1 a la derecha)
        sealed class Station
        {
            public int Platform, TileX, TileZ; public string Why = "";
            public int RouteSide;             // el lado de la ruta: +1 izquierda · −1 derecha · 0 los dos o sin dato
            public Vector3 Fwd;               // la marcha al entrar
            public volatile bool Measured;
            public List<(Vector3 p, int side)> Spots = new List<(Vector3, int)>();
            public bool Spawned, DoorsSeen, Stopped; public int Side;   // el lado de la gente: +1/−1; 0 sin decidir
        }
        static Station _st;

        public static int Install(Harmony h)
        {
            try
            {
                if (Environment.GetEnvironmentVariable("SELECTOR_VIAJEROS3D") != "1") return 0;
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
                _flags = Enum.ToObject(flags, 1);   // ShadowCaster
                var load = AccessTools.Method(world, "Load", Type.EmptyTypes);
                var draw = world.GetMethods(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault(m => m.Name == "PrepareFrame" && m.GetParameters().Length == 2);
                var vmark = AccessTools.Method(viewer, "Mark", Type.EmptyTypes);
                var missing = new[] { _worldViewer == null ? "World.Viewer" : null, _shapeMgr == null ? "Viewer.ShapeManager" : null, _get == null ? "SharedShapeManager.Get" : null,
                    _mark == null ? "SharedShape.Mark" : null, _prep == null ? "SharedShape.PrepareFrame" : null, load == null ? "World.Load" : null,
                    draw == null ? "World.PrepareFrame" : null, vmark == null ? "Viewer.Mark" : null }.Where(x => x != null).ToList();
                if (missing.Count > 0) { Agent.Log("viajeros en los andenes: este Open Rails no tiene lo necesario (" + string.Join(", ", missing) + ")"); return 0; }
                if (!CanMeasure)
                    Agent.Log("viajeros en los andenes: sin acceso al escenario (" + string.Join(", ", new[] { _scenery == null ? "World.Scenery" : null, _worldFiles == null ? "SceneryDrawer.WorldFiles" : null,
                        _sceneryObjects == null ? "WorldFile.sceneryObjects" : null, _location == null ? "StaticShape.Location" : null, _sharedShape == null ? "StaticShape.SharedShape" : null,
                        _filePath == null ? "SharedShape.FilePath" : null }.Where(x => x != null)) + "): el andén no se mide");
                h.Patch(load, postfix: new HarmonyMethod(typeof(Viajeros3D), nameof(AfterLoad)));
                h.Patch(vmark, postfix: new HarmonyMethod(typeof(Viajeros3D), nameof(AfterMark)));
                h.Patch(draw, postfix: new HarmonyMethod(typeof(Viajeros3D), nameof(AfterDraw)));
                _on = true;
                return 1;
            }
            catch (Exception ex) { Agent.Log("viajeros en los andenes: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        // ¿esta ruta trae las figuras? (las que haya)
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
                if (!_on || _shapes.Count > 0) return;
                var paths = PathsFor(MPManager.Simulator); if (paths == null) return;
                var mgr = _shapeMgr(_worldViewer(__instance));
                foreach (var f in paths) _shapes.Add(_get.Invoke(mgr, new object[] { f }));
                Agent.Log($"viajeros en los andenes: {_shapes.Count} figuras cargadas");
            }
            catch (Exception ex) { _on = false; Agent.Log("viajeros en los andenes (carga): " + ex.GetType().Name + " " + ex.Message); }
        }

        // (hilo de carga, antes del barrido) que no se descarguen
        static void AfterMark()
        {
            try { if (_on) foreach (var s in _shapes) _mark.Invoke(s, null); } catch { }
        }

        // (hilo de actualización, cada fotograma) la gente: qué hace y dónde se dibuja
        static void AfterDraw(object __instance, object __0)
        {
            if (!_on || _shapes.Count == 0) return;
            try
            {
                _world = __instance;
                double now = _clock.Elapsed.TotalSeconds; float dt = (float)Math.Min(0.25, Math.Max(0, now - _last)); _last = now;
                var sim = MPManager.Simulator; var train = sim?.PlayerLocomotive?.Train;
                if (train != null) Think(sim, train, dt);
                foreach (var p in _people)
                {
                    if (p.Phase == 2) continue;
                    var m = Matrix.CreateRotationY(p.Heading); m.Translation = p.Pos;
                    var wp = new WorldPosition { TileX = p.TileX, TileZ = p.TileZ, XNAMatrix = m };
                    _prep.Invoke(_shapes[p.Shape % _shapes.Count], new object[] { __0, wp, _flags });
                }
            }
            catch (Exception ex) { if (!_said) { _said = true; Agent.Log("viajeros en los andenes (dibujo): " + ex.GetType().Name + " " + ex.Message); } }
        }

        // ---- lo que pasa en el andén ----
        static void Think(Simulator sim, Train train, float dt)
        {
            if (train.Cars.Count == 0) return;
            var lead = train.Cars[0];
            int tx = lead.WorldPosition.TileX, tz = lead.WorldPosition.TileZ;
            float speed = Math.Abs(train.SpeedMpS);
            bool stopped = speed < 0.2f;
            var cars = PaxCars(train);
            var open = OpenDoorSide(cars);

            // entrando en una estación (o, sin andén en la ruta, parado con las puertas abiertas): se mide su andén
            int plat = PlatformUnder(sim, train);
            bool newStation = cars.Count > 0 && (plat >= 0 ? speed < 16.7f && (_st == null || _st.Platform != plat) : _st == null && stopped && open.HasValue);
            if (newStation)
            {
                var f = TrainFwd(train);
                _st = new Station { Platform = plat >= 0 ? plat : -2, TileX = tx, TileZ = tz, Fwd = f };
                if (plat >= 0) _st.RouteSide = RouteSide(sim, train, plat, out _st.Why); else _st.Why = "sin andén en la ruta, por las puertas abiertas";
                Measure(_st, train);
            }
            if (_st == null) return;
            var s = _st;
            if (stopped) s.Stopped = true;

            // medido: los que esperan, si se sabe el lado
            if (s.Measured && !s.Spawned)
            {
                s.Spawned = true;
                var sides = s.Spots.Select(q => q.side).Distinct().ToList();
                int side = s.RouteSide != 0 && (sides.Contains(s.RouteSide) || !CanMeasure) ? s.RouteSide : sides.Count == 1 ? sides[0] : 0;
                int n = 0;
                if (side != 0 && !s.DoorsSeen) { s.Side = side; n = SpawnWaiting(s, lead, near: false); }
                Agent.Log($"viajeros en los andenes: {s.Why} · andén medido: {s.Spots.Count(q => q.side == 1)} puntos a la izquierda, {s.Spots.Count(q => q.side == -1)} a la derecha"
                          + (side != 0 ? $" · {n} esperando a la {(side > 0 ? "izquierda" : "derecha")}" : " · nadie esperando hasta abrir las puertas"));
            }

            // puertas abiertas (parado): su lado manda
            if (stopped && open.HasValue && !s.DoorsSeen && s.Measured)
            {
                s.DoorsSeen = true;
                int side = Vector3.Dot(open.Value, Left(s.Fwd)) >= 0 ? 1 : -1;
                if (s.Side != side)
                {
                    if (s.Side != 0)
                    {
                        Agent.Log($"viajeros en los andenes: las puertas se abren del otro lado que el andén de la ruta ({s.Why}): fuera los de ese lado");
                        _people.RemoveAll(p => p.Boards && p.Phase == 0 && p.TileX == s.TileX && p.TileZ == s.TileZ);
                    }
                    s.Side = side;
                    SpawnWaiting(s, lead, near: true);   // (más lejos de la cabina; luego van andando a las puertas)
                }
                Board(cars, open.Value, s);
            }

            // en marcha y ya fuera del andén: la estación acaba (los que quedan, en el andén)
            if (s.Stopped && speed > 1f && plat != s.Platform)
            {
                foreach (var p in _people.Where(p => p.Phase == 1 && p.Boards)) { p.Phase = 0; p.Target = p.Pos; p.Then = new Vector3(0, -1e6f, 0); }
                _st = null;
            }

            // moverse
            Chain();
            foreach (var p in _people)
            {
                if (p.Phase == 2) continue;
                if (p.Wait > 0) { p.Wait -= dt; continue; }
                if (p.Phase != 1) continue;
                var d = p.Target - p.Pos; float len = d.Length();
                if (len < 0.05f)
                {
                    if (p.Then.Y > -1e5f) { p.Target = p.Then; p.Then = new Vector3(0, -1e6f, 0); continue; }
                    p.Phase = p.HideAtEnd ? 2 : 0;   // los que suben, dentro; los que bajan se quedan en el andén
                    continue;
                }
                d /= len; var flat = new Vector3(d.X, 0, d.Z); if (flat.LengthSquared() > 1e-4f) p.Heading = HeadingOf(Vector3.Normalize(flat));
                p.Pos += d * Math.Min(len, WalkMps * dt);
            }
            var c = lead.WorldPosition;
            _people.RemoveAll(p => p.Phase == 2 || Dist(p, c) > 400);
        }

        static float Dist(Person p, WorldPosition c) => new Vector3((p.TileX - c.TileX) * 2048 + p.Pos.X - c.XNAMatrix.Translation.X, 0, (p.TileZ - c.TileZ) * 2048 + p.Pos.Z - c.XNAMatrix.Translation.Z).Length();
        // el modelo mira hacia −Z (el +Z de MSTS): ángulo para que mire hacia d
        static float HeadingOf(Vector3 d) => (float)Math.Atan2(-d.X, -d.Z);
        static Vector3 Left(Vector3 v) => new Vector3(v.Z, 0, -v.X);
        static Vector3 Rel(WorldPosition w, int tx, int tz) => w.XNAMatrix.Translation + new Vector3((w.TileX - tx) * 2048, 0, (w.TileZ - tz) * 2048);

        static Vector3 TrainFwd(Train t)
        {
            var front = t.Cars[0].WorldPosition; var rear = t.Cars[t.Cars.Count - 1].WorldPosition;
            var f = Rel(front, rear.TileX, rear.TileZ) - rear.XNAMatrix.Translation; f.Y = 0;
            if (f.LengthSquared() < 1) { f = front.XNAMatrix.Forward; f.Y = 0; }
            return Vector3.Normalize(f);
        }

        // la sección de la cabeza, si tiene andén: el índice del andén; −1 si no
        static int PlatformUnder(Simulator sim, Train t)
        {
            try
            {
                int sec = t.PresentPosition[0].TCSectionIndex; if (sec < 0) return -1;
                var tcs = sim.Signals.TrackCircuitList[sec];
                return tcs.PlatformIndex != null && tcs.PlatformIndex.Count > 0 ? tcs.PlatformIndex[0] : -1;
            }
            catch { return -1; }
        }

        // el lado del andén según la ruta, en la marcha del tren: +1 izquierda · −1 derecha · 0 los dos o sin dato.
        // (2026-11-01, primera prueba en Ingeniero Pizias: [0] es la DERECHA del sentido de la sección y [1] la izquierda)
        static int RouteSide(Simulator sim, Train t, int plat, out string why)
        {
            why = "";
            try
            {
                var pd = sim.Signals.PlatformDetailsList[plat]; var pos = t.PresentPosition[0];
                bool p0 = pd.PlatformSide[0], p1 = pd.PlatformSide[1];
                why = $"{pd.Name}, sección {pos.TCSectionIndex} sentido {pos.TCDirection}, PlatformSide [{(p0 ? 1 : 0)},{(p1 ? 1 : 0)}]";
                if (p0 == p1) return 0;
                int inSection = p0 ? -1 : 1;
                return pos.TCDirection == 0 ? inSection : -inSection;
            }
            catch { return 0; }
        }

        // los coches con viajeros: todo lo que no es de mercancías ni ténder, menos las locomotoras de verdad (70 t o más: la 252
        // pesa unas 90, la 333 unas 120; los coches motores de las unidades, como la 446 o la Civia, rondan las 50). Si el
        // vehículo dice que lleva viajeros (ORTSPassengerCapacity), siempre.
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

        // las puertas de un coche (en cada sitio, a lo largo del coche, desde su centro): dos por lado; una en los cortos
        static IEnumerable<float> DoorOffsets(TrainCar c)
        {
            float L = c.CarLengthM;
            if (L < 12) { yield return 0; yield break; }
            yield return -L * 0.28f; yield return L * 0.28f;
        }

        // los que esperan, en los puntos medidos del lado s.Side: uno cada 7 m de andén más o menos. Para que no aparezcan a la
        // vista: a más de 90 m por delante de la cabina o por detrás de ella (near: al abrir las puertas, a más de 25 m)
        static int SpawnWaiting(Station s, TrainCar lead, bool near)
        {
            var cab = Rel(lead.WorldPosition, s.TileX, s.TileZ);
            var ok = s.Spots.Where(q => q.side == s.Side).Select(q => q.p)
                .Where(p => { float a = Vector3.Dot(p - cab, s.Fwd); return near ? a > 90 || a < -25 : a > 90 || a < -10; }).ToList();
            int want = Math.Min(MaxPeople - _people.Count, ok.Count / 2), n = 0;
            var face = -Left(s.Fwd) * s.Side;   // (hacia la vía)
            for (; n < want && ok.Count > 0; n++)
            {
                int i = _rnd.Next(ok.Count); var at = ok[i];
                ok.RemoveAll(q => (q - at).LengthSquared() < 7f * 7f);   // (repartidos, no uno encima de otro)
                at += s.Fwd * (float)(_rnd.NextDouble() * 2 - 1);
                _people.Add(new Person
                {
                    Shape = _rnd.Next(_shapes.Count), TileX = s.TileX, TileZ = s.TileZ, Pos = at, Target = at, Boards = true,
                    Heading = HeadingOf(face) + (float)(_rnd.NextDouble() - 0.5) * 1.8f, Then = new Vector3(0, -1e6f, 0)
                });
            }
            return n;
        }

        // puertas abiertas: los que esperan cerca, a la puerta más cercana (y dentro); por cada puerta, los que bajan (desde dentro)
        static void Board(List<TrainCar> cars, Vector3 sideV, Station s)
        {
            sideV.Y = 0; sideV.Normalize();
            var doors = new List<(Vector3 at, Vector3 inside, Vector3 fwd, float rail)>();
            foreach (var c in cars)
            {
                var m = c.WorldPosition.XNAMatrix; var fwd = m.Forward; fwd.Y = 0; if (fwd.LengthSquared() < 1e-6) continue; fwd.Normalize();
                float half = Math.Max(1.4f, c.CarWidthM / 2);
                var ctr = Rel(c.WorldPosition, s.TileX, s.TileZ);
                foreach (float off in DoorOffsets(c))
                {
                    var at = ctr + fwd * off + sideV * (half + 0.35f);
                    at.Y = ctr.Y + HeightNear(s, at, ctr.Y);
                    doors.Add((at, at - sideV * 1.3f, fwd, ctr.Y));   // (dentro: tapado por la carrocería)
                }
            }
            if (doors.Count == 0) return;
            int boarding = 0, alight = 0;
            foreach (var p in _people.Where(p => p.Boards && p.Phase == 0 && p.TileX == s.TileX && p.TileZ == s.TileZ))
            {
                var best = doors.OrderBy(d => (d.at - p.Pos).LengthSquared()).First();
                if ((best.at - p.Pos).Length() > 40) continue;   // (lejos: esperan a otro tren)
                p.Target = best.at; p.Then = best.inside; p.HideAtEnd = true; p.Phase = 1; p.Wait = 1.5f + (float)_rnd.NextDouble() * 6; boarding++;
            }
            int must = _rnd.Next(0, doors.Count);   // (al menos de una puerta baja alguien)
            for (int di = 0; di < doors.Count; di++)
            {
                var (at, inside, fwd, rail) = doors[di];
                int n = _rnd.Next(0, 4); if (di == must && n < 2) n = 2;
                for (int k = 0; k < n && _people.Count < MaxPeople; k++)
                {
                    float along = (float)(_rnd.NextDouble() < 0.5 ? -1 : 1) * (6 + (float)_rnd.NextDouble() * 12);
                    var out1 = at + sideV * (2.0f + (float)_rnd.NextDouble() * 2.5f);
                    var out2 = out1 + fwd * along;
                    out1.Y = rail + HeightNear(s, out1, rail); out2.Y = rail + HeightNear(s, out2, rail);
                    _people.Add(new Person
                    {
                        Shape = _rnd.Next(_shapes.Count), TileX = s.TileX, TileZ = s.TileZ, Pos = inside, Target = at, Then = out1,
                        Phase = 1, Wait = 1.5f + k * 2.4f + (float)_rnd.NextDouble(), Heading = HeadingOf(sideV)
                    });
                    // (después de la puerta, se alejan: out1 → out2; se encadena al llegar a out1)
                    _later.Add((_people[_people.Count - 1], out2));
                    alight++;
                }
            }
            Agent.Log($"viajeros en los andenes: puertas abiertas a la {(Vector3.Dot(sideV, Left(s.Fwd)) >= 0 ? "izquierda" : "derecha")} ({cars.Count} coches, {doors.Count} puertas): suben {boarding} · bajan {alight}");
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
                            pts.Add(a); pts.Add(b); pts.Add(c);
                        }
                    }
                r.P = pts.ToArray();
                if (r.P.Length == 0) return r;
                r.Min = new Vector3(r.P.Min(q => q.X), r.P.Min(q => q.Y), r.P.Min(q => q.Z)); r.Max = new Vector3(r.P.Max(q => q.X), r.P.Max(q => q.Y), r.P.Max(q => q.Z));
                for (int i = 0; i + 2 < r.P.Length; i += 3)
                {
                    Vector3 a = r.P[i], b = r.P[i + 1], c = r.P[i + 2];
                    int x0 = Cl(Math.Min(a.X, Math.Min(b.X, c.X))), x1 = Cl(Math.Max(a.X, Math.Max(b.X, c.X))), z0 = Cl(Math.Min(a.Z, Math.Min(b.Z, c.Z))), z1 = Cl(Math.Max(a.Z, Math.Max(b.Z, c.Z)));
                    if (x1 - x0 > 100 || z1 - z0 > 100) continue;   // (terrenos enormes, fuera)
                    for (int gx = x0; gx <= x1; gx++)
                        for (int gz = z0; gz <= z1; gz++)
                        {
                            long k = Cell(gx, gz);
                            if (!r.Grid.TryGetValue(k, out var l)) r.Grid[k] = l = new List<int>();
                            l.Add(i);
                        }
                }
            }
            catch { }
            return r;
        });

        // el suelo más alto en (x, z) del modelo, entre lo y hi; NaN si no hay
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
                if (y >= lo && y <= hi && (float.IsNaN(best) || y > best)) best = y;
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
                        if (!(_location(o) is WorldPosition loc)) continue;
                        var m = loc.XNAMatrix; m.Translation = Rel(loc, tx, tz);
                        var dd = m.Translation - at; dd.Y = 0; if (dd.LengthSquared() > r * r) continue;
                        var ss = _sharedShape(o); if (ss == null) continue;
                        if (_filePath(ss) is string p && p.Length > 0) res.Add((p, m));
                    }
                }
            }
            catch { }
            return res;
        }

        // la vía, cada 4 m: desde 30 m por detrás de la cola hasta 170 m por delante de la cabeza (por donde estén las agujas)
        static List<(Vector3 p, Vector3 fwd)> TrackSamples(Train t, int tx, int tz)
        {
            var res = new List<(Vector3, Vector3)>();
            try
            {
                var tr = new Traveller(t.RearTDBTraveller);
                tr.ReverseDirection(); tr.Move(30); tr.ReverseDirection();
                Vector3 Here() => new Vector3(tr.X + (tr.TileX - tx) * 2048, tr.Y, -tr.Z - (tr.TileZ - tz) * 2048);
                var prev = Here(); float total = t.Length + 200;
                for (float d = 0; d < total; d += 4)
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

        // el andén: en cada punto de la vía, a los dos lados (de 2,4 a 5,6 m del eje), el suelo entre 0,2 y 1,4 m sobre el carril.
        // El lado, respecto a la marcha al entrar (s.Fwd).
        static void Measure(Station s, Train train)
        {
            var track = TrackSamples(train, s.TileX, s.TileZ);
            if (track.Count == 0) { s.Measured = true; return; }
            var mid = track[track.Count / 2].p; float reach = (track[0].p - track[track.Count - 1].p).Length() / 2 + 150;
            var objs = NearObjects(s.TileX, s.TileZ, mid, reach);
            var fwd0 = s.Fwd;
            Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var insts = new List<(Floors f, Matrix m, Matrix inv)>();
                    foreach (var (path, m) in objs)
                    {
                        var fl = LoadFloors(path); if (fl.Grid.Count == 0) continue;
                        insts.Add((fl, m, Matrix.Invert(m)));
                    }
                    var spots = new List<(Vector3, int)>();
                    foreach (var (p, f) in track)
                    {
                        var left = Left(f); int lside = Vector3.Dot(left, Left(fwd0)) >= 0 ? 1 : -1;
                        foreach (int side in new[] { 1, -1 })
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
                                    if (float.IsNaN(best) || wy > best) best = wy;
                                }
                                if (!float.IsNaN(best)) spots.Add((new Vector3(w.X, best, w.Z), side * lside));
                            }
                    }
                    s.Spots = spots;
                    Agent.Log($"viajeros en los andenes: medido el andén en {sw.ElapsedMilliseconds} ms ({objs.Count} objetos cerca, {insts.Count} con suelo, {track.Count} puntos de vía)");
                }
                catch (Exception ex) { Agent.Log("viajeros en los andenes (medir el andén): " + ex.GetType().Name + " " + ex.Message); }
                finally { s.Measured = true; }
            });
        }

        // la altura del andén sobre el carril junto a un punto: la del punto medido más cercano (a menos de 6 m); si no, la fija
        static float HeightNear(Station s, Vector3 at, float railY)
        {
            float best = 36, h = DefaultH;
            foreach (var (p, _) in s.Spots)
            {
                float d = (p.X - at.X) * (p.X - at.X) + (p.Z - at.Z) * (p.Z - at.Z);
                if (d < best) { best = d; h = p.Y - railY; }
            }
            return h;
        }
    }
}
