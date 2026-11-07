// (1.2.91, petición 2026-11-07) RÉPLICA SATÉLITE DE LA RUTA: cada tesela de 2048 m vista desde arriba con la tarjeta gráfica,
// como la dibuja Open Rails: el terreno con su relieve y sus texturas (TILES\*.t, *_y.raw, TERRTEX) y, encima, todo lo que hay
// en su archivo de mundo (WORLD\*.w): edificios, andenes, vías, señales, pasos a nivel… (sus .s con sus texturas), las vías
// dinámicas (balasto y carriles), los bosques (copas vistas desde arriba, sin las que caen sobre una vía o una carretera, como
// en Open Rails) y los calcos del suelo (TransferObj).
//  · Cámara ortográfica desde arriba: el norte arriba, el este a la derecha; la tesela entera ocupa la imagen.
//  · Coordenadas como Open Rails (XNA): x al este, y arriba, z al SUR (z = −norte); el mundo de un objeto es
//    CreateFromQuaternion(A, B, −C, D) · Translation(X, Y, −Z); los .s, con la z cambiada de signo (AddShapeToGroups).
//  · SatPrepare (en segundo plano): lee y monta todo; SatDraw (en el hilo de la interfaz, donde vive el dispositivo): sube
//    lo nuevo a la tarjeta (modelos y texturas se quedan allí para las teselas siguientes) y dibuja.
//  · Se cogen también los objetos de las 8 teselas vecinas que quedan a menos de 300 m del borde (si no, los edificios del
//    borde saldrían cortados).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Orts.Formats.Msts;
using GdiBitmap = System.Drawing.Bitmap;

namespace SelectOR
{
    public sealed class SatScene
    {
        internal readonly List<ShapeGroup> Meshes = new List<ShapeGroup>();            // terreno, vías dinámicas, árboles, calcos (ya colocados)
        internal readonly List<(string shape, Matrix world)> Inst = new List<(string, Matrix)>();
        internal readonly Dictionary<string, ShapeGeom> NewShapes = new Dictionary<string, ShapeGeom>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, List<(int w, int h, Color[] px)>> NewTex = new Dictionary<string, List<(int, int, Color[])>>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> NeedTex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal float MinY = float.MaxValue, MaxY = float.MinValue;
        public int Objects, Trees, Shapes;
    }

    public static partial class ShapeRenderer
    {
        // ---- en la tarjeta (solo el hilo de la interfaz) ----
        sealed class SatGpuGroup { public ShapeGroup Meta; public VertexBuffer Vb; public IndexBuffer Ib; public int Prims; }
        static readonly Dictionary<string, List<SatGpuGroup>> _satShapes = new Dictionary<string, List<SatGpuGroup>>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Texture2D> _satTex = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        // ---- lo que ya hay en la tarjeta (para que la preparación no lo vuelva a leer) ----
        static readonly ConcurrentDictionary<string, string[]> _satShapeKnown = new ConcurrentDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);   // modelo → sus texturas
        static readonly ConcurrentDictionary<string, byte> _satTexKnown = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        static RasterizerState _satRaster;

        const int SatTexMax = 256;          // (1 m por píxel: de sobra)
        const float Margin = 300f;          // objetos de las vecinas, hasta 300 m del borde

        // =====================================================================================================================
        //  PREPARACIÓN (segundo plano)
        // =====================================================================================================================
        public static SatScene SatPrepare(string routeDir, int tx, int tz)
        {
            var sc = new SatScene();
            string basePath = Path.GetDirectoryName(Path.GetDirectoryName(routeDir.TrimEnd('\\', '/')));
            string global = Path.Combine(basePath ?? "", "GLOBAL");
            var texDirs = new[] { Path.Combine(routeDir, "TEXTURES"), Path.Combine(global, "TEXTURES") };
            var meshes = new Dictionary<string, (List<VertexPositionNormalTexture> v, List<int> i, int shader, bool alpha)>(StringComparer.OrdinalIgnoreCase);

            var toRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var height = SatTerrain(routeDir, tx, tz, sc, meshes);
            var lines = SatLines.For(routeDir);
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var objs = SatWorld(Path.Combine(routeDir, "WORLD", $"w{(tx + dx).ToString("+000000;-000000")}{(tz + dz).ToString("+000000;-000000")}.w"));
                    if (objs == null) continue;
                    bool own = dx == 0 && dz == 0;
                    foreach (var o in objs)
                    {
                        try
                        {
                            if (o?.Position == null) continue;
                            var at = new Vector3(o.Position.X + dx * 2048f, o.Position.Y, -(o.Position.Z + dz * 2048f));
                            if (!own && (Math.Abs(at.X) > 1024 + Margin || Math.Abs(at.Z) > 1024 + Margin)) continue;
                            var rot = SatRot(o);
                            var world = rot * Matrix.CreateTranslation(at);
                            switch (o)
                            {
                                case ForestObj f: SatForest(sc, meshes, f, world, height, lines, tx, tz, texDirs); break;
                                case TransferObj t: SatTransfer(sc, meshes, t, world, height, texDirs); break;
                                case DyntrackObj d: SatDyntrack(sc, meshes, d, rot, at); break;
                                default:
                                    string fn = o.FileName;
                                    if (string.IsNullOrWhiteSpace(fn) || !fn.EndsWith(".s", StringComparison.OrdinalIgnoreCase)) break;
                                    string path = SatShapePath(fn, o is TrackObj, routeDir, global);
                                    if (path == null) break;
                                    if (!_satShapeKnown.ContainsKey(path)) toRead.Add(path);
                                    sc.Inst.Add((path, world));
                                    sc.Objects++;
                                    sc.MinY = Math.Min(sc.MinY, at.Y - 60); sc.MaxY = Math.Max(sc.MaxY, at.Y + 120);
                                    break;
                            }
                        }
                        catch { }
                    }
                }

            // los modelos nuevos, en paralelo
            var read = new ConcurrentDictionary<string, ShapeGeom>(StringComparer.OrdinalIgnoreCase);
            System.Threading.Tasks.Parallel.ForEach(toRead, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
                path => read[path] = SatShape(path, texDirs) ?? new ShapeGeom());
            foreach (var kv in read) sc.NewShapes[kv.Key] = kv.Value;

            foreach (var kv in meshes)
            {
                if (kv.Value.v.Count == 0) continue;
                int cut = kv.Key.LastIndexOf('|'); cut = cut > 0 ? kv.Key.LastIndexOf('|', cut - 1) : -1;
                sc.Meshes.Add(new ShapeGroup { TexPath = cut > 0 ? kv.Key.Substring(0, cut) : "", Shader = kv.Value.shader, AlphaTest = kv.Value.alpha, Verts = kv.Value.v.ToArray(), Indices = kv.Value.i.ToArray() });
            }
            sc.Shapes = sc.NewShapes.Count;

            // las texturas que hacen falta y no están en la tarjeta
            foreach (var g in sc.Meshes) sc.NeedTex.Add(g.TexPath ?? "");
            foreach (var g in sc.NewShapes.Values) foreach (var gg in g.Groups) sc.NeedTex.Add(gg.TexPath ?? "");
            foreach (var (s, _) in sc.Inst) if (_satShapeKnown.TryGetValue(s, out var tl)) foreach (var t in tl) sc.NeedTex.Add(t);
            sc.NeedTex.Remove("");
            var dec = new ConcurrentDictionary<string, List<(int w, int h, Color[] px)>>(StringComparer.OrdinalIgnoreCase);
            System.Threading.Tasks.Parallel.ForEach(sc.NeedTex.Where(t => !_satTexKnown.ContainsKey(t)).ToList(),
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
                t => { try { var d = SatDecode(t); if (d != null) dec[t] = d; } catch { } });
            foreach (var kv in dec) sc.NewTex[kv.Key] = kv.Value;
            if (sc.MinY > sc.MaxY) { sc.MinY = -100; sc.MaxY = 1000; }
            return sc;
        }

        static Matrix SatRot(WorldObject o)
        {
            if (o.QDirection != null) return Matrix.CreateFromQuaternion(new Quaternion(o.QDirection.A, o.QDirection.B, -o.QDirection.C, o.QDirection.D));
            if (o.Matrix3x3 != null)
            {
                var m = o.Matrix3x3;
                var M = new Matrix(m.AX, m.AY, m.AZ, 0, m.BX, m.BY, m.BZ, 0, m.CX, m.CY, m.CZ, 0, 0, 0, 0, 1);
                var f = Matrix.CreateScale(1, 1, -1);
                return f * M * f;
            }
            return Matrix.Identity;
        }

        static string SatShapePath(string fn, bool track, string routeDir, string global)
        {
            fn = fn.Replace('/', '\\').TrimStart('\\');
            string a = Path.Combine(routeDir, "SHAPES", fn), b = Path.Combine(global, "SHAPES", fn);
            foreach (var p in track ? new[] { b, a } : new[] { a, b })
                if (Native.IsLocalFile(p) && File.Exists(p)) return p;
            return null;
        }

        static string SatFindTex(string file, string[] dirs, string extraDir = null)
        {
            if (string.IsNullOrWhiteSpace(file)) return "";
            file = file.Replace('/', '\\').TrimStart('\\');
            string stem = Path.GetFileNameWithoutExtension(file), sub = Path.GetDirectoryName(file) ?? "";
            foreach (var d in extraDir == null ? dirs : dirs.Append(extraDir))
            {
                if (string.IsNullOrEmpty(d)) continue;
                string full = Path.Combine(d, file);
                if (File.Exists(full)) return full;
                foreach (var ext in new[] { ".ace", ".dds" })
                {
                    string c = Path.Combine(d, sub, stem + ext);
                    if (File.Exists(c)) return c;
                }
            }
            return "";
        }

        // ---- el terreno: una malla por textura, con la altura de cada muestra y su normal ----
        static Func<float, float, float> SatTerrain(string routeDir, int tx, int tz, SatScene sc,
            Dictionary<string, (List<VertexPositionNormalTexture> v, List<int> i, int shader, bool alpha)> meshes)
        {
            string tfile = Path.Combine(routeDir, "TILES", TileName.FromTileXZ(tx, tz, TileName.Zoom.Small) + ".t");
            if (!File.Exists(tfile)) return null;
            var t = new TerrainFile(tfile).terrain;
            var smp = t.terrain_samples;
            int n = (int)smp.terrain_nsamples; float s = smp.terrain_sample_size, fl = smp.terrain_sample_floor, k = smp.terrain_sample_scale;
            float half = n * s / 2;
            TerrainAltitudeFile alt = null;
            try
            {
                string yfile = Path.Combine(routeDir, "TILES", Path.GetFileNameWithoutExtension(tfile) + "_y.raw");
                if (File.Exists(yfile)) alt = new TerrainAltitudeFile(yfile, n);
            }
            catch { alt = null; }
            float H(int gx, int gz) => alt == null ? fl : fl + alt.GetElevation(Math.Max(0, Math.Min(n - 1, gx)), Math.Max(0, Math.Min(n - 1, gz))) * k;

            string terrtex = Path.Combine(routeDir, "TERRTEX");
            var ps = t.terrain_patchsets[0];
            foreach (var p in ps.terrain_patchset_patches)
            {
                var sh = t.terrain_shaders[Math.Max(0, Math.Min(t.terrain_shaders.Length - 1, p.ShaderIndex))];
                string tex = sh.terrain_texslots != null && sh.terrain_texslots.Length > 0 ? SatFindTex(sh.terrain_texslots[0].Filename, new[] { terrtex }) : "";
                var key = tex + "|0|0";
                if (!meshes.TryGetValue(key, out var m)) meshes[key] = m = (new List<VertexPositionNormalTexture>(), new List<int>(), 0, false);
                float R = p.RadiusM; int nv = Math.Max(2, (int)Math.Round(2 * R / s) + 1);
                float x0 = p.CenterX - half - R, n0 = p.CenterZ + half + R;   // (oeste y norte del parche, desde el centro de la tesela)
                int gx0 = (int)Math.Round((p.CenterX - R) / s), gz0 = (int)Math.Round((-p.CenterZ - R) / s);
                int b = m.v.Count;
                for (int zi = 0; zi < nv; zi++)
                    for (int xi = 0; xi < nv; xi++)
                    {
                        int gx = gx0 + xi, gz = gz0 + zi;
                        float y = H(gx, gz);
                        float ddx = (H(gx + 1, gz) - H(gx - 1, gz)) / (2 * s), ddz = (H(gx, gz + 1) - H(gx, gz - 1)) / (2 * s);   // (gz: hacia el sur = +z)
                        var nrm = Vector3.Normalize(new Vector3(-ddx, 1, -ddz));
                        var uv = new Vector2(p.X + p.W * xi + p.B * zi, p.Y + p.C * xi + p.H * zi);
                        m.v.Add(new VertexPositionNormalTexture(new Vector3(x0 + xi * s, y, -(n0 - zi * s)), nrm, uv));
                        sc.MinY = Math.Min(sc.MinY, y); sc.MaxY = Math.Max(sc.MaxY, y);
                    }
                for (int zi = 0; zi + 1 < nv; zi++)
                    for (int xi = 0; xi + 1 < nv; xi++)
                    {
                        int a = b + zi * nv + xi, c = a + nv;
                        m.i.Add(a); m.i.Add(c); m.i.Add(a + 1);
                        m.i.Add(a + 1); m.i.Add(c); m.i.Add(c + 1);
                    }
            }
            // la altura del suelo en un punto de la tesela (x al este, z al sur, desde su centro)
            return (x, z) => H((int)Math.Round((x + half) / s), (int)Math.Round((z + half) / s));
        }

        // ---- un .s, con sus texturas buscadas en la ruta y en GLOBAL ----
        static ShapeGeom SatShape(string sPath, string[] texDirs)
        {
            try
            {
                var sf = OpenShape(sPath);
                var shape = sf?.shape;
                if (shape == null || shape.lod_controls.Count == 0) return null;
                var geom = new ShapeGeom();
                var groups = new Dictionary<string, (List<VertexPositionNormalTexture> v, List<int> idx, int shader, bool alphaTest)>(StringComparer.OrdinalIgnoreCase);
                string modelDir = Path.GetDirectoryName(sPath);
                var texOf = new Dictionary<int, string>();
                foreach (var lod in shape.lod_controls)
                {
                    if (lod.distance_levels.Count == 0) continue;
                    var dl = lod.distance_levels[0];
                    var hierarchy = dl.distance_level_header.hierarchy;
                    var world = new Matrix[shape.matrices.Count];
                    for (int i = 0; i < shape.matrices.Count; i++) world[i] = Accumulate(i, hierarchy, shape.matrices);
                    foreach (var sub in dl.sub_objects)
                        foreach (var prim in sub.primitives)
                        {
                            if (prim.prim_state_idx < 0 || prim.prim_state_idx >= shape.prim_states.Count) continue;
                            var ps = shape.prim_states[prim.prim_state_idx];
                            var M = Matrix.Identity;
                            if (ps.ivtx_state >= 0 && ps.ivtx_state < shape.vtx_states.Count)
                            {
                                int im = shape.vtx_states[ps.ivtx_state].imatrix;
                                if (im >= 0 && im < world.Length) M = world[im];
                            }
                            if (!texOf.TryGetValue(prim.prim_state_idx, out var texPath))
                            {
                                texPath = "";
                                try
                                {
                                    if (ps.tex_idxs != null && ps.tex_idxs.Length > 0 && ps.tex_idxs[0] >= 0 && ps.tex_idxs[0] < shape.textures.Count)
                                    {
                                        int ii = shape.textures[ps.tex_idxs[0]].iImage;
                                        if (ii >= 0 && ii < shape.images.Count) texPath = SatFindTex(shape.images[ii], texDirs, modelDir);
                                    }
                                }
                                catch { texPath = ""; }
                                texOf[prim.prim_state_idx] = texPath;
                            }
                            var (shader, alphaTest) = GetShaderInfo(shape, ps);
                            string key = texPath + "|" + shader + "|" + (alphaTest ? 1 : 0);
                            if (!groups.TryGetValue(key, out var grp)) groups[key] = grp = (new List<VertexPositionNormalTexture>(), new List<int>(), shader, alphaTest);
                            foreach (var tri in prim.indexed_trilist.vertex_idxs)
                                foreach (int vi in new[] { tri.a, tri.b, tri.c })
                                {
                                    if (vi < 0 || vi >= sub.vertices.Count) continue;
                                    var vtx = sub.vertices[vi];
                                    var pt = shape.points[vtx.ipoint];
                                    var pos = Vector3.Transform(new Vector3(pt.X, pt.Y, pt.Z), M); pos.Z = -pos.Z;
                                    var nrm = Vector3.Up;
                                    if (vtx.inormal >= 0 && vtx.inormal < shape.normals.Count)
                                    {
                                        var nv = shape.normals[vtx.inormal];
                                        nrm = Vector3.TransformNormal(new Vector3(nv.X, nv.Y, nv.Z), M); nrm.Z = -nrm.Z;
                                        if (nrm.LengthSquared() > 1e-9f) nrm.Normalize(); else nrm = Vector3.Up;
                                    }
                                    var uv = Vector2.Zero;
                                    if (vtx.vertex_uvs != null && vtx.vertex_uvs.Length > 0 && vtx.vertex_uvs[0] >= 0 && vtx.vertex_uvs[0] < shape.uv_points.Count)
                                        uv = new Vector2(shape.uv_points[vtx.vertex_uvs[0]].U, shape.uv_points[vtx.vertex_uvs[0]].V);
                                    grp.idx.Add(grp.v.Count);
                                    grp.v.Add(new VertexPositionNormalTexture(pos, nrm, uv));
                                }
                        }
                }
                foreach (var kv in groups)
                {
                    if (kv.Value.v.Count == 0) continue;
                    int cut = kv.Key.LastIndexOf('|'); cut = cut > 0 ? kv.Key.LastIndexOf('|', cut - 1) : -1;
                    geom.Groups.Add(new ShapeGroup { TexPath = cut > 0 ? kv.Key.Substring(0, cut) : "", Shader = kv.Value.shader, AlphaTest = kv.Value.alphaTest, Verts = kv.Value.v.ToArray(), Indices = kv.Value.idx.ToArray() });
                }
                return geom;
            }
            catch { return null; }
        }

        static (List<VertexPositionNormalTexture> v, List<int> i, int shader, bool alpha) SatMesh(
            Dictionary<string, (List<VertexPositionNormalTexture> v, List<int> i, int shader, bool alpha)> meshes, string tex, int shader, bool alpha)
        {
            string key = tex + "|" + shader + "|" + (alpha ? 1 : 0);
            if (!meshes.TryGetValue(key, out var m)) meshes[key] = m = (new List<VertexPositionNormalTexture>(), new List<int>(), shader, alpha);
            return m;
        }

        static void SatQuad((List<VertexPositionNormalTexture> v, List<int> i, int shader, bool alpha) m, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
        {
            int k = m.v.Count;
            m.v.Add(new VertexPositionNormalTexture(a, Vector3.Up, ua)); m.v.Add(new VertexPositionNormalTexture(b, Vector3.Up, ub));
            m.v.Add(new VertexPositionNormalTexture(c, Vector3.Up, uc)); m.v.Add(new VertexPositionNormalTexture(d, Vector3.Up, ud));
            m.i.Add(k); m.i.Add(k + 1); m.i.Add(k + 2); m.i.Add(k); m.i.Add(k + 2); m.i.Add(k + 3);
        }

        // ---- bosques: las copas, como Open Rails coloca los árboles (mismo azar por UID), sin las de encima de una vía ----
        static void SatForest(SatScene sc, Dictionary<string, (List<VertexPositionNormalTexture> v, List<int> i, int shader, bool alpha)> meshes,
            ForestObj f, Matrix world, Func<float, float, float> height, SatLines lines, int tx, int tz, string[] texDirs)
        {
            string tex = SatFindTex(f.TreeTexture, texDirs);
            var m = SatMesh(meshes, "#crown|" + tex, 0, true);
            var rnd = new Random((int)f.UID);
            int pop = Math.Min(f.Population, 40000);
            for (int i = 0; i < pop; i++)
            {
                var lp = new Vector3((0.5f - (float)rnd.NextDouble()) * f.forestArea.X, 0, (0.5f - (float)rnd.NextDouble()) * f.forestArea.Z);
                var wp = Vector3.Transform(lp, world);
                float scale = MathHelper.Lerp(f.scaleRange.Minimum, f.scaleRange.Maximum, (float)rnd.NextDouble());
                float w = f.treeSize.Width * scale, h = f.treeSize.Height * scale;
                if (Math.Abs(wp.X) > 1024 + w || Math.Abs(wp.Z) > 1024 + w) continue;
                if (lines != null && lines.Near(tx * 2048.0 + wp.X, tz * 2048.0 - wp.Z, w * 0.5 + 3) ) continue;
                float y = (height?.Invoke(wp.X, wp.Z) ?? wp.Y) + h * 0.7f, r = Math.Max(0.6f, w * 0.48f);
                SatQuad(m, new Vector3(wp.X - r, y, wp.Z - r), new Vector3(wp.X + r, y, wp.Z - r), new Vector3(wp.X + r, y, wp.Z + r), new Vector3(wp.X - r, y, wp.Z + r),
                    new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1));
                sc.Trees++;
                sc.MaxY = Math.Max(sc.MaxY, y + 1);
            }
        }

        // ---- calcos del suelo ----
        static void SatTransfer(SatScene sc, Dictionary<string, (List<VertexPositionNormalTexture> v, List<int> i, int shader, bool alpha)> meshes,
            TransferObj t, Matrix world, Func<float, float, float> height, string[] texDirs)
        {
            string tex = SatFindTex(t.FileName, texDirs);
            if (tex.Length == 0) return;
            var m = SatMesh(meshes, tex, 1, false);
            float hw = t.Width / 2, hh = t.Height / 2;
            Vector3 P(float x, float z) { var p = Vector3.Transform(new Vector3(x, 0, z), world); p.Y = (height?.Invoke(p.X, p.Z) ?? p.Y) + 0.12f; return p; }
            SatQuad(m, P(-hw, -hh), P(hw, -hh), P(hw, hh), P(-hw, hh), new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1));
        }

        // ---- vías dinámicas: balasto y carriles a lo largo de sus tramos (como DynamicTrack.Decompose de Open Rails) ----
        static void SatDyntrack(SatScene sc, Dictionary<string, (List<VertexPositionNormalTexture> v, List<int> i, int shader, bool alpha)> meshes,
            DyntrackObj d, Matrix rot, Vector3 at)
        {
            var pts = new List<Vector3> { Vector3.Zero };
            var pos = Vector3.Zero; var heading = Vector3.Forward;
            foreach (var sec in d.trackSections)
            {
                float len = sec.param1;
                if (len == 0) continue;
                if (sec.isCurved == 0)
                {
                    int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(len) / 10));
                    for (int k = 1; k <= steps; k++) pts.Add(pos + heading * len * k / steps);
                    pos += heading * len;
                }
                else
                {
                    float radius = sec.param2;
                    var left = radius * Vector3.Cross(len > 0 ? Vector3.Down : Vector3.Up, heading);
                    var O = pos + left;
                    int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(len) * radius / 4));
                    for (int k = 1; k <= steps; k++) pts.Add(Vector3.Transform(-left, Matrix.CreateRotationY(-len * k / steps)) + O);
                    var r = Matrix.CreateRotationY(-len);
                    pos = Vector3.Transform(-left, r) + O; heading = Vector3.Transform(heading, r);
                }
            }
            if (pts.Count < 2) return;
            var m = SatMesh(meshes, "#ballast", 0, false);
            const float hw = 2.4f;
            float along = 0;
            int b = m.v.Count;
            for (int k = 0; k < pts.Count; k++)
            {
                var p = Vector3.Transform(pts[k], rot) + at + new Vector3(0, 0.15f, 0);
                var q0 = Vector3.Transform(pts[Math.Max(0, k - 1)], rot); var q1 = Vector3.Transform(pts[Math.Min(pts.Count - 1, k + 1)], rot);
                var dir = q1 - q0; dir.Y = 0; if (dir.LengthSquared() < 1e-6f) dir = Vector3.Forward; dir.Normalize();
                var side = new Vector3(-dir.Z, 0, dir.X);
                if (k > 0) along += (pts[k] - pts[k - 1]).Length();
                m.v.Add(new VertexPositionNormalTexture(p - side * hw, Vector3.Up, new Vector2(0, along / 5)));
                m.v.Add(new VertexPositionNormalTexture(p + side * hw, Vector3.Up, new Vector2(1, along / 5)));
                if (k > 0) { int a = b + (k - 1) * 2; m.i.Add(a); m.i.Add(a + 1); m.i.Add(a + 2); m.i.Add(a + 1); m.i.Add(a + 3); m.i.Add(a + 2); }
                sc.MinY = Math.Min(sc.MinY, p.Y); sc.MaxY = Math.Max(sc.MaxY, p.Y);
            }
        }

        // ---- los .w (los últimos, en memoria: la tesela de al lado vuelve a pedir los mismos) ----
        static readonly object _satWorldLock = new object();
        static readonly LinkedList<(string f, List<WorldObject> l)> _satWorlds = new LinkedList<(string, List<WorldObject>)>();
        static List<WorldObject> SatWorld(string f)
        {
            lock (_satWorldLock)
                for (var n = _satWorlds.First; n != null; n = n.Next)
                    if (string.Equals(n.Value.f, f, StringComparison.OrdinalIgnoreCase)) { _satWorlds.Remove(n); _satWorlds.AddFirst(n); return n.Value.l; }
            List<WorldObject> l = null;
            try { if (File.Exists(f)) { var wf = new WorldFile(f); l = wf.Tr_Worldfile?.Cast<WorldObject>().ToList(); } } catch { l = null; }
            lock (_satWorldLock)
            {
                _satWorlds.AddFirst((f, l));
                while (_satWorlds.Count > 18) _satWorlds.RemoveLast();
            }
            return l;
        }

        // ---- texturas: como mucho de 256 px, con sus niveles; las de los árboles y el balasto, hechas aquí ----
        static List<(int w, int h, Color[] px)> SatDecode(string key)
        {
            GdiBitmap bmp;
            if (key == "#ballast") bmp = SatBallast();
            else if (key.StartsWith("#crown|", StringComparison.Ordinal)) bmp = SatCrown(key.Substring(7));
            else { bmp = File.Exists(key) ? LoadBitmap(key, out _) : null; }
            if (bmp == null) return null;
            try
            {
                int w = bmp.Width, h = bmp.Height;
                while (w > SatTexMax || h > SatTexMax) { w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); }
                var cur = (w != bmp.Width || h != bmp.Height) ? MipDown(bmp, w, h) : new GdiBitmap(bmp);
                var levels = new List<(int, int, Color[])>();
                while (true)
                {
                    levels.Add((cur.Width, cur.Height, ToColors(cur)));
                    if (cur.Width == 1 && cur.Height == 1) break;
                    var next = MipDown(cur, Math.Max(1, cur.Width / 2), Math.Max(1, cur.Height / 2));
                    cur.Dispose(); cur = next;
                }
                cur.Dispose();
                return levels;
            }
            finally { bmp.Dispose(); }
        }

        static GdiBitmap SatBallast()
        {
            var b = new GdiBitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var rnd = new Random(7);
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    double u = (x + 0.5) / 32;
                    int n = rnd.Next(-10, 11);
                    System.Drawing.Color c;
                    if (Math.Abs(Math.Abs(u - 0.5) - 0.17) < 0.022) c = System.Drawing.Color.FromArgb(150, 148, 144);                // carriles
                    else if (Math.Abs(u - 0.5) < 0.27) c = (y % 4 < 2) ? System.Drawing.Color.FromArgb(96 + n, 84 + n, 72 + n) : System.Drawing.Color.FromArgb(110 + n, 102 + n, 94 + n);   // traviesas
                    else c = System.Drawing.Color.FromArgb(122 + n, 116 + n, 106 + n);                                              // balasto
                    b.SetPixel(x, y, c);
                }
            return b;
        }

        // la copa de un árbol vista desde arriba: un disco del color medio de su textura, con luz del noroeste
        static GdiBitmap SatCrown(string treeTex)
        {
            int r = 70, g = 96, bl = 52;
            try
            {
                using var src = string.IsNullOrEmpty(treeTex) || !File.Exists(treeTex) ? null : LoadBitmap(treeTex, out _);
                if (src != null)
                {
                    using var small = MipDown(src, 32, 32);
                    long sr = 0, sg = 0, sb = 0, cnt = 0;
                    for (int y = 0; y < 22; y++)   // (la parte de arriba: la copa, no el tronco)
                        for (int x = 0; x < 32; x++)
                        {
                            var c = small.GetPixel(x, y);
                            if (c.A < 128) continue;
                            sr += c.R; sg += c.G; sb += c.B; cnt++;
                        }
                    if (cnt > 20) { r = (int)(sr / cnt); g = (int)(sg / cnt); bl = (int)(sb / cnt); }
                }
            }
            catch { }
            const int S = 64;
            var b = new GdiBitmap(S, S, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var rnd = new Random(treeTex?.Length ?? 1);
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    double dx = (x + 0.5 - S / 2.0) / (S / 2.0), dy = (y + 0.5 - S / 2.0) / (S / 2.0), d = Math.Sqrt(dx * dx + dy * dy);
                    double noise = rnd.NextDouble();
                    if (d > 0.86 + 0.14 * noise) { b.SetPixel(x, y, System.Drawing.Color.FromArgb(0, r, g, bl)); continue; }
                    double z = Math.Sqrt(Math.Max(0, 1 - d * d));
                    double lit = (-dx * 0.5 - dy * 0.5 + z * 0.75) / 1.06;   // (luz del noroeste, alta)
                    double f = Math.Max(0.45, Math.Min(1.25, 0.55 + 0.6 * lit + (noise - 0.5) * 0.18));
                    b.SetPixel(x, y, System.Drawing.Color.FromArgb(255, Math.Min(255, (int)(r * f)), Math.Min(255, (int)(g * f)), Math.Min(255, (int)(bl * f))));
                }
            return b;
        }

        // =====================================================================================================================
        //  DIBUJO (hilo de la interfaz)
        // =====================================================================================================================
        public static GdiBitmap SatDraw(SatScene sc, int px)
        {
            if (sc == null) return null;
            try
            {
                EnsureDevice();
                bool custom = EnsureShapeFx();
                if (!custom && !EnsureBasicFx()) return null;
                SatUpload(sc);

                RenderTarget2D rt = null;
                try { rt = new RenderTarget2D(_gd, px, px, false, SurfaceFormat.Color, DepthFormat.Depth24, 4, RenderTargetUsage.DiscardContents); }
                catch { rt = null; }
                rt ??= new RenderTarget2D(_gd, px, px, false, SurfaceFormat.Color, DepthFormat.Depth24);
                using (rt)
                {
                    _gd.SetRenderTarget(rt);
                    _gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Color(92, 100, 80, 255), 1f, 0);
                    float top = sc.MaxY + 200, depth = sc.MaxY - sc.MinY + 600;
                    var view = Matrix.CreateLookAt(new Vector3(0, top, 0), new Vector3(0, top - 1, 0), new Vector3(0, 0, -1));
                    var proj = Matrix.CreateOrthographic(2048, 2048, 1, depth);
                    _satRaster ??= new RasterizerState { CullMode = CullMode.None };
                    _gd.RasterizerState = _satRaster;
                    _gd.SamplerStates[0] = SamplerState.LinearWrap;
                    _gd.BlendState = BlendState.NonPremultiplied;
                    var sunView = Vector3.Normalize(new Vector3(-0.45f, 0.45f, 0.77f));   // (en la vista: x al este, y al norte, z hacia arriba)
                    if (custom) { _pSun.SetValue(sunView); _pAmb.SetValue(new Vector3(0.45f)); _pDif.SetValue(new Vector3(0.62f)); }
                    else _basicFx.DirectionalLight0.Direction = Vector3.Normalize(new Vector3(0.45f, -0.77f, 0.45f));

                    for (int pass = 0; pass < 2; pass++)
                    {
                        _gd.DepthStencilState = pass == 0 ? DepthStencilState.Default : DepthStencilState.DepthRead;
                        foreach (var g in sc.Meshes)
                        {
                            if ((g.Shader != 0) != (pass == 1)) continue;
                            foreach (var p in SatFx(custom, g, Matrix.Identity, view, proj))
                            {
                                p.Apply();
                                _gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, g.Verts, 0, g.Verts.Length, g.Indices, 0, g.Indices.Length / 3);
                            }
                        }
                        foreach (var (s, world) in sc.Inst)
                        {
                            if (!_satShapes.TryGetValue(s, out var gl)) continue;
                            foreach (var gg in gl)
                            {
                                if ((gg.Meta.Shader != 0) != (pass == 1)) continue;
                                _gd.SetVertexBuffer(gg.Vb); _gd.Indices = gg.Ib;
                                foreach (var p in SatFx(custom, gg.Meta, world, view, proj))
                                {
                                    p.Apply();
                                    _gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, gg.Prims);
                                }
                            }
                        }
                    }
                    _gd.SetRenderTarget(null);
                    _gd.SetVertexBuffer(null); _gd.Indices = null;
                    _gd.BlendState = BlendState.Opaque; _gd.DepthStencilState = DepthStencilState.Default;
                    var data = new Color[px * px];
                    rt.GetData(data);
                    for (int i = 0; i < data.Length; i++) data[i].A = 255;
                    return ToBitmap(data, px, px);
                }
            }
            catch { try { _gd?.SetRenderTarget(null); } catch { } return null; }
        }

        static IEnumerable<EffectPass> SatFx(bool custom, ShapeGroup g, Matrix world, Matrix view, Matrix proj)
        {
            Texture2D tex = null;
            if (!string.IsNullOrEmpty(g.TexPath)) _satTex.TryGetValue(g.TexPath, out tex);
            tex ??= White();
            if (custom)
            {
                _pWVP.SetValue(world * view * proj);
                _pWV.SetValue(world * view);
                _pVA.SetValue(g.AlphaTest ? -0.51f : (g.Shader != 0 ? 0f : 1f));
                _pTex.SetValue(tex);
                return _shapeFx.CurrentTechnique.Passes;
            }
            _basicFx.World = world; _basicFx.View = view; _basicFx.Projection = proj; _basicFx.Texture = tex;
            return _basicFx.CurrentTechnique.Passes;
        }

        // sube a la tarjeta lo nuevo; si hay demasiado, quita lo que esta tesela no usa
        static void SatUpload(SatScene sc)
        {
            var useShapes = new HashSet<string>(sc.Inst.Select(i => i.shape), StringComparer.OrdinalIgnoreCase);
            if (_satShapes.Count + sc.NewShapes.Count > 900)
                foreach (var k in _satShapes.Keys.Where(k => !useShapes.Contains(k)).ToList())
                {
                    foreach (var g in _satShapes[k]) { g.Vb?.Dispose(); g.Ib?.Dispose(); }
                    _satShapes.Remove(k); _satShapeKnown.TryRemove(k, out _);
                }
            if (_satTex.Count + sc.NewTex.Count > 700)
                foreach (var k in _satTex.Keys.Where(k => !sc.NeedTex.Contains(k)).ToList())
                {
                    _satTex[k]?.Dispose(); _satTex.Remove(k); _satTexKnown.TryRemove(k, out _);
                }
            foreach (var kv in sc.NewTex)
            {
                if (_satTex.ContainsKey(kv.Key)) continue;
                Texture2D t = null;
                try
                {
                    var l0 = kv.Value[0];
                    t = new Texture2D(_gd, l0.w, l0.h, kv.Value.Count > 1, SurfaceFormat.Color);
                    for (int i = 0; i < kv.Value.Count && i < t.LevelCount; i++) t.SetData(i, null, kv.Value[i].px, 0, kv.Value[i].px.Length);
                }
                catch { t?.Dispose(); t = null; }
                _satTex[kv.Key] = t; _satTexKnown[kv.Key] = 1;
            }
            foreach (var kv in sc.NewShapes)
            {
                if (_satShapes.ContainsKey(kv.Key)) continue;
                var list = new List<SatGpuGroup>();
                foreach (var g in kv.Value.Groups)
                {
                    if (g.Verts.Length == 0 || g.Indices.Length < 3) continue;
                    try
                    {
                        var vb = new VertexBuffer(_gd, typeof(VertexPositionNormalTexture), g.Verts.Length, BufferUsage.WriteOnly); vb.SetData(g.Verts);
                        var ib = new IndexBuffer(_gd, IndexElementSize.ThirtyTwoBits, g.Indices.Length, BufferUsage.WriteOnly); ib.SetData(g.Indices);
                        list.Add(new SatGpuGroup { Meta = new ShapeGroup { TexPath = g.TexPath, Shader = g.Shader, AlphaTest = g.AlphaTest }, Vb = vb, Ib = ib, Prims = g.Indices.Length / 3 });
                    }
                    catch { }
                }
                _satShapes[kv.Key] = list;
                _satShapeKnown[kv.Key] = kv.Value.Groups.Select(g => g.TexPath ?? "").Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    // las vías y las carreteras de la ruta (de su .tdb y su .rdb), por casillas de 50 m: para no poner árboles encima
    sealed class SatLines
    {
        const double G = 50;
        readonly Dictionary<(int, int), List<(double ax, double az, double bx, double bz)>> _grid = new Dictionary<(int, int), List<(double, double, double, double)>>();
        static readonly ConcurrentDictionary<string, SatLines> _cache = new ConcurrentDictionary<string, SatLines>(StringComparer.OrdinalIgnoreCase);

        public static SatLines For(string routeDir) => _cache.GetOrAdd(routeDir, Load);

        static SatLines Load(string routeDir)
        {
            var t = new SatLines();
            try
            {
                var tdb = Directory.GetFiles(routeDir, "*.tdb").FirstOrDefault();
                if (tdb != null) t.Add(new TrackDatabaseFile(tdb).TrackDB.TrackNodes);
            }
            catch { }
            try
            {
                var rdb = Directory.GetFiles(routeDir, "*.rdb").FirstOrDefault();
                if (rdb != null) t.Add(new RoadDatabaseFile(rdb).RoadTrackDB.TrackNodes);
            }
            catch { }
            return t;
        }

        void Add(TrackNode[] nodes)
        {
            if (nodes == null) return;
            foreach (var n in nodes)
            {
                var vs = n?.TrVectorNode?.TrVectorSections; if (vs == null) continue;
                for (int i = 0; i + 1 < vs.Length; i++)
                {
                    double ax = vs[i].TileX * 2048.0 + vs[i].X, az = vs[i].TileZ * 2048.0 + vs[i].Z, bx = vs[i + 1].TileX * 2048.0 + vs[i + 1].X, bz = vs[i + 1].TileZ * 2048.0 + vs[i + 1].Z;
                    if ((ax - bx) * (ax - bx) + (az - bz) * (az - bz) > 4000 * 4000) continue;
                    for (int gx = (int)Math.Floor(Math.Min(ax, bx) / G); gx <= (int)Math.Floor(Math.Max(ax, bx) / G); gx++)
                        for (int gz = (int)Math.Floor(Math.Min(az, bz) / G); gz <= (int)Math.Floor(Math.Max(az, bz) / G); gz++)
                        {
                            if (!_grid.TryGetValue((gx, gz), out var l)) _grid[(gx, gz)] = l = new List<(double, double, double, double)>();
                            l.Add((ax, az, bx, bz));
                        }
                }
            }
        }

        // ¿hay una vía o una carretera a menos de d metros de (x al este, z al norte, del mundo)?
        public bool Near(double x, double z, double d)
        {
            int cx = (int)Math.Floor(x / G), cz = (int)Math.Floor(z / G), r = (int)Math.Ceiling(d / G);
            double d2 = d * d;
            for (int gx = cx - r; gx <= cx + r; gx++)
                for (int gz = cz - r; gz <= cz + r; gz++)
                {
                    if (!_grid.TryGetValue((gx, gz), out var l)) continue;
                    foreach (var (ax, az, bx, bz) in l)
                    {
                        double vx = bx - ax, vz = bz - az, len2 = vx * vx + vz * vz;
                        double u = len2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, ((x - ax) * vx + (z - az) * vz) / len2));
                        double px = ax + u * vx - x, pz = az + u * vz - z;
                        if (px * px + pz * pz < d2) return true;
                    }
                }
            return false;
        }
    }
}
