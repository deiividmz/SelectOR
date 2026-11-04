// OBJETOS DE LA SALA (petición 2026-11-04): lo que el administrador pone en la ruta desde el Puesto de Mando de SelectOR
// se ve en el 3D de todos los que conducen en la sala compartida. Solo decoran: no paran trenes ni tocan señales.
//  · SelectOR lee la lista de la sala (Supabase: room_objects) y se la manda al agente por el enlace local cuando cambia:
//    «OBJ BEGIN» · «OBJ id tipo figura tileX tileZ x z rumbo» · «OBJ END» (x, z: metros dentro de la tesela, como MSTS, con
//    la z hacia el norte; rumbo: grados desde el norte en el sentido de las agujas del reloj, hacia donde mira).
//  · Tipos:
//      operarios de vía: obra_trabaja · obra_agachado · obra_espera · obra_saluda (de pie; saluda con la mano y mira al tren
//        cuando pasa uno a menos de 150 m). Figura: «obra_h07»… (gente.txt, del mismo Gente.pack que la gente) o «*».
//      fijos: derrumbe · derrumbe_grande · roca · piedras (tierra y rocas escaneadas: «obj_<tipo>» en gente.txt).
//      fuego · incendio: llamas animadas (16 fotogramas, «obj_<tipo>_NN»; se suman a la imagen: se ven igual de noche), de
//        noche una luz naranja que parpadea en el suelo («obj_luz<tipo>_N») y humo con el sistema de partículas de Open Rails
//        (el de las locomotoras: ParticleEmitterViewer, con obj_humo.ace), saliendo por encima de las llamas.
//  · La altura: el terreno (TileManager) y, si hay un suelo de un modelo por encima (balasto, andén, puente), el más bajo
//    de ellos. Se calcula en el hilo de carga cuando el tren está a menos de 2 km.
// Dibujo (como la gente: World.PrepareFrame → SharedShape.PrepareFrame; Viewer.Mark), pensado para no cargar Open Rails:
//  · las figuras se cargan solo si algún objeto las usa, de una en una en el hilo de carga, y no se descargan (descargar
//    algo que se está dibujando en otro hilo podría cerrar Open Rails);
//  · llamadas a Open Rails con delegados compilados (sin reflexión ni basura por fotograma) y posiciones reutilizadas;
//  · nada a más de 1,5 km; sombras solo cerca y nunca del fuego; humo como mucho en los 6 fuegos más cercanos (a < 3 km);
//  · si algo falla 20 veces, los objetos se apagan para esa sesión (Open Rails sigue).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Orts.MultiPlayer;
using ORTS.Common;

namespace SelectOR.MapAgent
{
    public static partial class Viajeros3D
    {
        static bool _peopleOn, _objOn;
        const int MaxObjects = 300, MaxSmoke = 6;
        const float DrawDist = 1500, SmokeDist = 3000;

        sealed class Fig
        {
            public string Name; public readonly Dictionary<string, (object[] frames, float secs)> Anims = new Dictionary<string, (object[], float)>();
            public volatile bool Ready; public bool Failed;
        }
        sealed class Obj
        {
            public int Id, TileX, TileZ; public string Kind, FigName; public float X, Z, Bearing;   // (MSTS: z al norte)
            public volatile float Y = float.NaN; public float Ground = float.NaN; public double NextTry; public int Tries;
            public float Heading, Clock, Rate = 1; public Fig F; public string Anim;
            public readonly WorldPosition Wp = new WorldPosition();      // (la misma en cada fotograma: sin basura)
            public List<object> Smoke; public WorldPosition SmokeAt; public bool SmokeTried; public float SmokeY = float.NaN;
            public bool IsFire => Kind == "fuego" || Kind == "incendio";
        }
        static volatile List<Obj> _objs = new List<Obj>();   // (se reemplaza entera: la leen el hilo de carga y el de actualización)
        static List<Obj> _objNew;
        static readonly ConcurrentQueue<string> _objq = new ConcurrentQueue<string>();
        static readonly Dictionary<string, Fig> _figs = new Dictionary<string, Fig>(StringComparer.OrdinalIgnoreCase);
        static List<(string name, Dictionary<string, float> anims)> _obra;   // las figuras que hay (gente.txt)
        static bool _objSaid; static int _objErrors;
        public static void ObjCmd(string s) => _objq.Enqueue(s);

        static void ObjFailed(string where, Exception ex)
        {
            if (!_objSaid) { _objSaid = true; Agent.Log($"objetos de la sala ({where}): {ex.GetType().Name} {ex.Message}"); }
            if (++_objErrors >= 20 && _objOn) { _objOn = false; Agent.Log("objetos de la sala: demasiados fallos; se dejan de dibujar en esta sesión (Open Rails sigue)"); }
        }

        // las figuras de gente.txt para objetos («obra_h07 1.4 1.5 work=4 …», «obj_derrumbe 0 0 idle=1», «obj_fuego_00 …»)
        static List<(string name, Dictionary<string, float> anims)> ObraList()
        {
            if (_obra != null) return _obra;
            var l = new List<(string, Dictionary<string, float>)>();
            try
            {
                string dir = Environment.GetEnvironmentVariable("SELECTOR_GENTE_DIR");
                var f = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "gente.txt");
                if (f != null && File.Exists(f))
                    foreach (var line in File.ReadAllLines(f))
                    {
                        var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (t.Length < 4 || !(t[0].StartsWith("obra_") || t[0].StartsWith("obj_"))) continue;
                        var an = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                        foreach (var x in t.Skip(3)) { var kv = x.Split('='); if (kv.Length == 2 && float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) an[kv[0]] = v; }
                        l.Add((t[0], an));
                    }
            }
            catch { }
            _obra = l;
            Agent.Log(l.Count > 0 ? $"objetos de la sala: {l.Count(x => x.Item1.StartsWith("obra_"))} operarios de vía y {l.Count(x => x.Item1.StartsWith("obj_"))} piezas de objetos disponibles" : "objetos de la sala: no hay figuras (falta la gente de SelectOR: SELECTOR_GENTE_DIR)");
            return l;
        }
        static bool Has(string name) => ObraList().Any(x => x.name.Equals(name, StringComparison.OrdinalIgnoreCase));

        // (hilo de actualización) los mensajes de SelectOR
        static void ObjCommands()
        {
            while (_objq.TryDequeue(out var cmd))
            {
                var t = cmd.Split(' ');
                if (t[0] == "BEGIN") { _objNew = new List<Obj>(); continue; }
                if (t[0] == "END")
                {
                    if (_objNew == null) continue;
                    var old = _objs.ToDictionary(o => o.Id);
                    foreach (var o in _objNew)
                        if (old.TryGetValue(o.Id, out var p) && p.Kind == o.Kind && p.TileX == o.TileX && p.TileZ == o.TileZ && Math.Abs(p.X - o.X) < 0.01f && Math.Abs(p.Z - o.Z) < 0.01f)
                        {
                            o.Y = p.Y; o.Ground = p.Ground; o.Tries = p.Tries; o.NextTry = p.NextTry; o.Heading = p.Heading; o.Clock = p.Clock; o.Rate = p.Rate;
                            o.Smoke = p.Smoke; o.SmokeAt = p.SmokeAt; o.SmokeTried = p.SmokeTried; o.SmokeY = p.SmokeY;   // (su humo sigue)
                        }
                    _objs = _objNew; _objNew = null;
                    Agent.Log($"objetos de la sala: {_objs.Count} en la ruta" + (_objs.Count > 0 ? " (" + string.Join(", ", _objs.GroupBy(o => o.Kind).Select(g => g.Count() + " " + g.Key)) + ")" : ""));
                    continue;
                }
                if (_objNew == null || _objNew.Count >= MaxObjects || t.Length < 8) continue;
                try
                {
                    var ci = CultureInfo.InvariantCulture;
                    var o = new Obj
                    {
                        Id = int.Parse(t[0], ci), Kind = t[1], FigName = t[2], TileX = int.Parse(t[3], ci), TileZ = int.Parse(t[4], ci),
                        X = float.Parse(t[5], ci), Z = float.Parse(t[6], ci), Bearing = float.Parse(t[7], ci)
                    };
                    if (float.IsNaN(o.X) || float.IsNaN(o.Z) || float.IsNaN(o.Bearing)) continue;
                    o.Heading = -o.Bearing * (float)Math.PI / 180f;   // (el modelo mira al norte con 0; girar a la derecha es negativo)
                    o.Clock = (o.Id * 7.3f) % 20; o.Rate = 0.85f + (o.Id * 37 % 30) / 100f;
                    _objNew.Add(o);
                }
                catch { }
            }
        }

        // la figura de un objeto: la que dice o, con «*», una fija según su id; los fijos y el fuego, la de su tipo
        static string FigFor(Obj o)
        {
            var all = ObraList();
            if (o.IsFire) return Has($"obj_{o.Kind}_00") ? "obj_" + o.Kind : null;
            if (!o.Kind.StartsWith("obra_")) { string n = "obj_" + o.Kind; return Has(n) ? n : null; }
            var l = all.Where(x => x.name.StartsWith("obra_")).ToList(); if (l.Count == 0) return null;
            if (o.FigName != "*" && l.Any(x => x.name.Equals(o.FigName, StringComparison.OrdinalIgnoreCase))) return o.FigName;
            return l[Math.Abs(o.Id) % l.Count].name;
        }

        // (hilo de carga, World.Load) las figuras que hacen falta (una por llamada), la altura y el humo
        static Func<object, object> _tilesOf; static MethodInfo _elev, _loadElev; static bool _elevLooked;
        static double _objLoadAt;
        static void ObjLoad()
        {
            double t = _clock.Elapsed.TotalSeconds; if (t - _objLoadAt < 0.5) return; _objLoadAt = t;   // (dos veces por segundo basta)
            try
            {
                var objs = _objs;
                if (objs.Count == 0 || _mgr == null) return;
                string dir = Environment.GetEnvironmentVariable("SELECTOR_GENTE_DIR");
                foreach (var o in objs)
                {
                    if (o.F != null) continue;
                    string name = FigFor(o); if (name == null) { o.F = new Fig { Failed = true }; continue; }   // (esta versión no la trae)
                    if (_figs.TryGetValue(name, out var have)) { o.F = have; continue; }
                    var fig = new Fig { Name = name };
                    if (o.IsFire)
                    {
                        // las llamas: obj_<tipo>_00…15 (una postura cada una); la luz de noche: obj_luz<tipo>_0…2
                        var fr = Enumerable.Range(0, 16).Select(i => Frames(_mgr, dir, $"{name}_{i:00}", "idle")).Where(x => x.Length > 0).Select(x => x[0]).ToArray();
                        var lz = Enumerable.Range(0, 3).Select(i => Frames(_mgr, dir, $"obj_luz{o.Kind}_{i}", "idle")).Where(x => x.Length > 0).Select(x => x[0]).ToArray();
                        if (fr.Length > 0) fig.Anims["idle"] = (fr, 1); if (lz.Length > 0) fig.Anims["luz"] = (lz, 1);
                    }
                    else
                        foreach (var kv in ObraList().First(x => x.name == name).anims)
                        {
                            var fr = Frames(_mgr, dir, name, kv.Key);
                            if (fr.Length > 0) fig.Anims[kv.Key] = (fr, kv.Value);
                        }
                    fig.Failed = fig.Anims.Count == 0; fig.Ready = !fig.Failed;
                    _figs[name] = fig; o.F = fig;
                    Agent.Log($"objetos de la sala: cargado {name} ({string.Join(", ", fig.Anims.Select(a => a.Key + " " + a.Value.frames.Length))})");
                    break;   // (una por llamada: no parar la carga del escenario)
                }
                Heights(objs);
                SmokeLoad(objs);
            }
            catch (Exception ex) { ObjFailed("carga", ex); }
        }
        // (hilo de carga, Viewer.Mark) que Open Rails no descargue lo que se usa
        static void MarkFigs()
        {
            if (_figs.Count == 0 && _smokes.Count == 0) return;
            foreach (var f in _figs.Values) foreach (var (fr, _) in f.Anims.Values) foreach (var s in fr) Mark(s);
            foreach (var s in _smokes) try { _pevMark?.Invoke(s, null); } catch { }
        }

        static void Heights(List<Obj> objs)
        {
            var sim = MPManager.Simulator; var lead = sim?.PlayerLocomotive?.Train?.Cars.FirstOrDefault()?.WorldPosition;
            if (lead == null || _world == null) return;
            if (!_elevLooked)
            {
                _elevLooked = true;
                var viewer = _worldViewer(_world);
                _tilesOf = Member(viewer.GetType(), "Tiles");
                var tm = _tilesOf?.Invoke(viewer)?.GetType();
                _loadElev = tm?.GetMethods().FirstOrDefault(m => m.Name == "LoadAndGetElevation" && m.GetParameters().Length == 5);
                _elev = tm?.GetMethods().FirstOrDefault(m => m.Name == "GetElevation" && m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == typeof(int));
                if (_loadElev == null && _elev == null) Agent.Log("objetos de la sala: este Open Rails no da la altura del terreno (se ponen a la altura del tren)");
            }
            double now = _clock.Elapsed.TotalSeconds;
            foreach (var o in objs)
            {
                if (!float.IsNaN(o.Y) && o.Tries >= 3) continue;
                if (now < o.NextTry) continue;
                var at = new Vector3((o.TileX - lead.TileX) * 2048 + o.X, 0, -(o.TileZ - lead.TileZ) * 2048 - o.Z);
                var d = at - lead.XNAMatrix.Translation; d.Y = 0;
                if (d.LengthSquared() > 2000f * 2000f) continue;
                o.NextTry = now + 15; o.Tries++;
                float ground = float.NaN;
                try
                {
                    var tiles = _tilesOf?.Invoke(_worldViewer(_world));
                    if (tiles != null && _loadElev != null) ground = Convert.ToSingle(_loadElev.Invoke(tiles, new object[] { o.TileX, o.TileZ, o.X, o.Z, false }));
                    else if (tiles != null && _elev != null) ground = Convert.ToSingle(_elev.Invoke(tiles, new object[] { o.TileX, o.TileZ, o.X, o.Z }));
                }
                catch { }
                if (float.IsNaN(ground)) ground = lead.XNAMatrix.Translation.Y;
                o.Ground = ground;
                if (float.IsNaN(o.Y)) o.Y = ground;
                // un suelo de un modelo encima (el más bajo, hasta 30 m por encima del terreno): se mira aparte
                var oo = o; var pos = new Vector3(o.X, 0, -o.Z);
                var near = NearObjects(o.TileX, o.TileZ, pos, 60);
                if (near.Count == 0) continue;   // (el escenario aún no está cargado: otra vez luego)
                o.Tries = Math.Max(o.Tries, 3);
                Task.Run(() =>
                {
                    try
                    {
                        float best = float.NaN;
                        foreach (var (path, m) in near)
                        {
                            var fl = LoadFloors(path); if (fl.Grid.Count == 0) continue;
                            var inv = Matrix.Invert(m);
                            var top = Vector3.Transform(new Vector3(pos.X, ground + 30, pos.Z), inv);
                            var bot = Vector3.Transform(new Vector3(pos.X, ground - 1, pos.Z), inv);
                            if (top.X < fl.Min.X || top.X > fl.Max.X || top.Z < fl.Min.Z || top.Z > fl.Max.Z) continue;
                            float y = FloorAt(fl, top.X, top.Z, Math.Min(bot.Y, top.Y), Math.Max(bot.Y, top.Y));
                            if (float.IsNaN(y)) continue;
                            float wy = Vector3.Transform(new Vector3(top.X, y, top.Z), m).Y;
                            if (float.IsNaN(best) || wy < best) best = wy;
                        }
                        oo.Y = float.IsNaN(best) ? ground : Math.Max(ground, best);
                    }
                    catch { }
                });
            }
        }

        // ---- el humo: el sistema de partículas de Open Rails (el de las locomotoras) ----
        static ConstructorInfo _pevNew; static Type _pedT; static FieldInfo _pedLoc, _pedDir, _pedNoz;
        static MethodInfo _pevInit, _pevOut, _pevPrep, _pevMark; static bool _pevLooked, _pevOk;
        static readonly List<object> _smokes = new List<object>();   // (todos los creados: se marcan)
        static void SmokeLook()
        {
            if (_pevLooked) return; _pevLooked = true;
            try
            {
                var pev = AccessTools.TypeByName("Orts.Viewer3D.ParticleEmitterViewer");
                _pedT = AccessTools.TypeByName("Orts.Simulation.RollingStocks.ParticleEmitterData") ?? AccessTools.AllTypes().FirstOrDefault(t => t.Name == "ParticleEmitterData");
                _pevNew = pev?.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 3 && c.GetParameters()[1].ParameterType == _pedT && c.GetParameters()[2].ParameterType == typeof(WorldPosition));
                _pedLoc = _pedT == null ? null : AccessTools.Field(_pedT, "XNALocation"); _pedDir = _pedT == null ? null : AccessTools.Field(_pedT, "XNADirection"); _pedNoz = _pedT == null ? null : AccessTools.Field(_pedT, "NozzleWidth");
                _pevInit = pev == null ? null : AccessTools.Method(pev, "Initialize", new[] { typeof(string) });
                _pevOut = pev?.GetMethods().FirstOrDefault(m => m.Name == "SetOutput" && m.GetParameters().Length == 4);
                _pevPrep = pev?.GetMethods().FirstOrDefault(m => m.Name == "PrepareFrame" && m.GetParameters().Length == 2);
                _pevMark = pev == null ? null : AccessTools.Method(pev, "Mark", Type.EmptyTypes);
                _pevOk = _pevNew != null && _pedLoc != null && _pedDir != null && _pedNoz != null && _pevInit != null && _pevOut != null && _pevPrep != null;
                if (!_pevOk) Agent.Log("objetos de la sala: este Open Rails no deja hacer humo (el fuego, sin humo)");
            }
            catch (Exception ex) { _pevOk = false; Agent.Log("objetos de la sala: sin humo: " + ex.GetType().Name + " " + ex.Message); }
        }

        // (hilo de carga) el humo de los fuegos cercanos: como mucho MaxSmoke, los más cerca del tren y a menos de SmokeDist
        static void SmokeLoad(List<Obj> objs)
        {
            if (!objs.Any(o => o.IsFire)) return;
            SmokeLook(); if (!_pevOk) return;
            var lead = MPManager.Simulator?.PlayerLocomotive?.Train?.Cars.FirstOrDefault()?.WorldPosition; if (lead == null) return;
            string tex = Path.Combine(Environment.GetEnvironmentVariable("SELECTOR_GENTE_DIR") ?? "", "obj_humo.ace");
            if (!File.Exists(tex)) return;
            var near = objs.Where(o => o.IsFire && !float.IsNaN(o.Y)).Select(o => (o, d: DistTo(o, lead))).Where(x => x.d < SmokeDist).OrderBy(x => x.d).Take(MaxSmoke).Select(x => x.o).ToList();
            foreach (var o in near)
            {
                if (o.SmokeTried) { if (o.SmokeAt != null && Math.Abs(o.SmokeY - o.Y) > 0.05f) { var mt = Matrix.CreateTranslation(o.X, o.Y, -o.Z); o.SmokeAt.XNAMatrix = mt; o.SmokeY = o.Y; } continue; }
                o.SmokeTried = true;
                try
                {
                    var viewer = _worldViewer(_world);
                    o.SmokeAt = new WorldPosition { TileX = o.TileX, TileZ = o.TileZ, XNAMatrix = Matrix.CreateTranslation(o.X, o.Y, -o.Z) }; o.SmokeY = o.Y;
                    o.Smoke = new List<object>();
                    bool big = o.Kind == "incendio";
                    // (focos de humo, encima de las puntas de las llamas para no taparlas: el incendio, dos; la hoguera, uno)
                    var spots = big ? new[] { new Vector3(-1.6f, 5.8f, -0.4f), new Vector3(1.4f, 6.2f, 0.5f) } : new[] { new Vector3(0, 2.6f, 0) };
                    foreach (var s in spots)
                    {
                        var data = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(_pedT);
                        var r = Matrix.CreateRotationY(o.Heading); var off = Vector3.Transform(s, r);
                        _pedLoc.SetValue(data, off); _pedDir.SetValue(data, Vector3.Up); _pedNoz.SetValue(data, big ? 0.9f : 0.5f);
                        var e = _pevNew.Invoke(new object[] { viewer, data, o.SmokeAt });
                        _pevInit.Invoke(e, new object[] { tex });
                        // velocidad inicial (m/s), volumen (m³/s), duración (s), color (2026-11-04, petición: menos humo, encima de la llama)
                        // (2.ª vez, «demasiado humo»: unas seis veces menos, que dure menos y más claro)
                        _pevOut.Invoke(e, new object[] { big ? 1.8f : 1.3f, big ? 1.4f : 0.4f, big ? 8f : 6f, big ? new Color(85, 83, 80) : new Color(105, 103, 100) });
                        o.Smoke.Add(e); _smokes.Add(e);
                    }
                    Agent.Log($"objetos de la sala: humo para el {o.Kind} #{o.Id} ({o.Smoke.Count} focos)");
                }
                catch (Exception ex) { o.Smoke = null; Agent.Log("objetos de la sala: el humo no se ha podido crear: " + (ex.InnerException ?? ex).GetType().Name + " " + (ex.InnerException ?? ex).Message); }
            }
        }
        static float DistTo(Obj o, WorldPosition c)
        {
            var d = new Vector3((o.TileX - c.TileX) * 2048 + o.X - c.XNAMatrix.Translation.X, 0, -(o.TileZ - c.TileZ) * 2048 - o.Z - c.XNAMatrix.Translation.Z);
            return d.Length();
        }

        // ¿de noche? (la hora del simulador; al anochecer y al amanecer, la luz más floja)
        static int NightLevel()
        {
            try { double h = (MPManager.Simulator.ClockTime / 3600.0) % 24; if (h < 0) h += 24; return h < 6.8 || h >= 21.3 ? 2 : h < 7.8 || h >= 20.3 ? 1 : 0; }
            catch { return 0; }
        }

        // (hilo de actualización, cada fotograma) cada objeto, con su movimiento
        static readonly List<WorldPosition> _trainsNow = new List<WorldPosition>();
        // dónde está la cámara (las llamas se giran hacia ella: nunca se ven de canto)
        static Func<object, object> _camOf, _camTX, _camTZ, _camLoc; static bool _camLooked;
        static bool CameraAt(object world, out int tx, out int tz, out Vector3 msts)
        {
            tx = tz = 0; msts = Vector3.Zero;
            try
            {
                var viewer = _worldViewer(world);
                if (!_camLooked)
                {
                    _camLooked = true; _camOf = Member(viewer.GetType(), "Camera");
                    var ct = _camOf?.Invoke(viewer)?.GetType();
                    while (ct != null && _camTX == null) { _camTX = Member(ct, "TileX"); _camTZ = Member(ct, "TileZ"); _camLoc = Member(ct, "Location"); if (_camTX == null) ct = ct.BaseType; }
                    if (_camTX == null || _camLoc == null) Agent.Log("objetos de la sala: sin la posición de la cámara (las llamas no se giran)");
                }
                var cam = _camOf?.Invoke(viewer); if (cam == null || _camTX == null || _camLoc == null) return false;
                tx = (int)_camTX(cam); tz = (int)_camTZ(cam); msts = (Vector3)_camLoc(cam);
                return true;
            }
            catch { return false; }
        }
        static object[] _smokeArgs = new object[2];
        static void ObjDraw(object world, object frame, object elapsed)
        {
            if (!_objOn) return;
            try
            {
                _world = world;
                ObjCommands();
                var objs = _objs; if (objs.Count == 0) return;
                var sim = MPManager.Simulator; var lead = sim?.PlayerLocomotive?.Train?.Cars.FirstOrDefault()?.WorldPosition;
                if (lead == null) return;
                double now = _clock.Elapsed.TotalSeconds;
                _trainsNow.Clear();
                bool anyWave = false; foreach (var o in objs) if (o.Kind == "obra_saluda") { anyWave = true; break; }
                if (anyWave) try { foreach (var t in sim.Trains) if (t.Cars.Count > 0) { _trainsNow.Add(t.Cars[0].WorldPosition); if (t.Cars.Count > 1) _trainsNow.Add(t.Cars[t.Cars.Count - 1].WorldPosition); } } catch { }
                int night = -1; bool camOk = false, camLooked = false; int ctx = 0, ctz = 0; Vector3 cloc = Vector3.Zero;
                float dt = 1 / 30f;
                foreach (var o in objs)
                {
                    var f = o.F; if (f == null || !f.Ready || float.IsNaN(o.Y)) continue;
                    float dist = DistTo(o, lead);
                    // el humo, aunque las llamas aún no se vean (sube alto)
                    if (o.Smoke != null && dist < SmokeDist && _pevPrep != null && elapsed != null)
                        foreach (var e in o.Smoke) { _smokeArgs[0] = frame; _smokeArgs[1] = elapsed; _pevPrep.Invoke(e, _smokeArgs); }
                    if (dist > DrawDist) continue;
                    if (o.IsFire)
                    {
                        if (!camLooked) { camLooked = true; camOk = CameraAt(world, out ctx, out ctz, out cloc); }
                        if (camOk)
                        {
                            var to = new Vector3((ctx - o.TileX) * 2048 + cloc.X - o.X, 0, -((ctz - o.TileZ) * 2048 + cloc.Z) + o.Z);
                            if (to.LengthSquared() > 0.01f) o.Heading = HeadingOf(Vector3.Normalize(to));
                        }
                    }
                    var m = Matrix.CreateRotationY(o.Heading); m.Translation = new Vector3(o.X, o.Y, -o.Z);
                    o.Wp.TileX = o.TileX; o.Wp.TileZ = o.TileZ; o.Wp.XNAMatrix = m;
                    if (o.IsFire)
                    {
                        // las llamas, en bucle (14 fotogramas por segundo); de noche, la luz en el suelo, que parpadea
                        if (f.Anims.TryGetValue("idle", out var fl)) Prep(fl.frames[(int)(now * 14 + o.Clock * 16) % fl.frames.Length], frame, o.Wp, _noShadow);
                        if (night < 0) night = NightLevel();
                        if (night > 0 && f.Anims.TryGetValue("luz", out var lz))
                        {
                            int k = night == 1 ? 0 : 1 + (int)((((int)(now * 9) + o.Id) * 2654435761u) % 2u);
                            Prep(lz.frames[Math.Min(lz.frames.Length - 1, k)], frame, o.Wp, _noShadow);
                        }
                        continue;
                    }
                    // qué hace
                    string want = o.Kind == "obra_trabaja" ? "work" : o.Kind == "obra_agachado" ? "crouch" : "idle";
                    float face = -o.Bearing * (float)Math.PI / 180f;   // (su rumbo; saludando, hacia el tren)
                    if (o.Kind == "obra_saluda")
                    {
                        Vector3 close = Vector3.Zero; bool any = false; float bd = 150 * 150;
                        foreach (var w in _trainsNow)
                        {
                            var q = new Vector3((w.TileX - o.TileX) * 2048, 0, -(w.TileZ - o.TileZ) * 2048) + w.XNAMatrix.Translation - new Vector3(o.X, 0, -o.Z); q.Y = 0;
                            float l = q.LengthSquared(); if (l < bd && l > 1) { bd = l; close = q; any = true; }
                        }
                        if (any) { want = f.Anims.ContainsKey("wave") ? "wave" : "idle"; face = HeadingOf(Vector3.Normalize(close)); }
                    }
                    if (!f.Anims.ContainsKey(want)) want = f.Anims.ContainsKey("idle") ? "idle" : f.Anims.Keys.First();
                    o.Anim = want;
                    if (o.Kind.StartsWith("obra_"))
                    {
                        float diff = Ang(face - o.Heading); float turn = 2.2f * dt;
                        o.Heading = Ang(o.Heading + Math.Max(-turn, Math.Min(turn, diff)));
                    }
                    // su postura: ida y vuelta por el movimiento (como la gente al esperar); los fijos tienen una
                    var (frames, secs) = f.Anims[want];
                    int n = frames.Length; object shape;
                    if (n == 1 || dist > 300) shape = frames[0];   // (lejos, quieto: no se nota y ahorra)
                    else { double u = ((now * o.Rate + o.Clock) % (2 * secs)) / secs; double k = u <= 1 ? u : 2 - u; shape = frames[Math.Min(n - 1, (int)(k * (n - 1) + 0.5))]; }
                    Prep(shape, frame, o.Wp, dist < 80 && o.Kind.StartsWith("obra_") ? _flags : _noShadow);
                }
            }
            catch (Exception ex) { ObjFailed("dibujo", ex); }
        }
    }
}
