// Vista previa 3D de un tren (estilo Shape Viewer): parsea el .s con el ShapeFile de OR
// y lo renderiza offscreen con MonoGame a un Bitmap. Sin tocar el código fuente de OR.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Orts.Formats.Msts;
using GdiBitmap = System.Drawing.Bitmap;
using GdiRect = System.Drawing.Rectangle;
using GdiPixel = System.Drawing.Imaging.PixelFormat;
using GdiLock = System.Drawing.Imaging.ImageLockMode;

namespace SelectOR
{
    public class ShapeGroup
    {
        public string TexPath;
        public int Shader;      // 0 = opaco (Tex/TexDiff), 1 = blend (BlendA*), 2 = add (AddA*)
        public bool AlphaTest;  // alphatestmode == 1 en el .s
        public VertexPositionNormalTexture[] Verts;   // posición + normal + UV (la luz la aplica el shader)
        public int[] Indices;
    }

    public class ShapeGeom
    {
        public List<ShapeGroup> Groups = new List<ShapeGroup>();
        public Vector3 Min = new Vector3(float.MaxValue);
        public Vector3 Max = new Vector3(float.MinValue);
        public bool IsEmpty => Groups.Count == 0;

        // Límites "robustos" para ENCUADRAR la cámara: ignoran geometría atípica separada del cuerpo por un
        // hueco grande (p. ej. pantógrafos rotos a Y=-20 en algún modelo editado). Así el vehículo no sale
        // minúsculo/vacío. Se calcula una vez; Render/RenderSide encuadran con esto, no con Min/Max.
        public Vector3 RMin, RMax;
        bool _robustDone;
        public void EnsureRobustBounds()
        {
            if (_robustDone) return;
            _robustDone = true;
            RMin = Min; RMax = Max;
            var xs = new List<float>(); var ys = new List<float>(); var zs = new List<float>();
            foreach (var g in Groups) foreach (var v in g.Verts) { xs.Add(v.Position.X); ys.Add(v.Position.Y); zs.Add(v.Position.Z); }
            if (xs.Count < 32) return;
            var (x0, x1) = RobustRange(xs); var (y0, y1) = RobustRange(ys); var (z0, z1) = RobustRange(zs);
            RMin = new Vector3(x0, y0, z0); RMax = new Vector3(x1, y1, z1);
        }

        // Recorta clústeres atípicos MINORITARIOS: ordena y, a cada lado de la mediana, busca el mayor
        // "hueco"; recorta ahí solo si (a) el hueco es grande (>20% del rango y >1.5 m) y (b) lo que se
        // elimina es < 35% de los vértices (nunca recorta el cuerpo principal). Ejes sin atípicos: intactos.
        static (float lo, float hi) RobustRange(List<float> v)
        {
            v.Sort();
            int n = v.Count;
            float lo = v[0], hi = v[n - 1], range = hi - lo;
            if (range < 1e-3f) return (lo, hi);
            float med = v[n / 2];
            int maxRemove = (int)(0.35f * n);
            // lado bajo: recorta el clúster inferior si es minoritario
            float bestGap = 0; int bestI = -1;
            for (int i = 1; i < n && v[i] <= med; i++) { float g = v[i] - v[i - 1]; if (g > bestGap) { bestGap = g; bestI = i; } }
            if (bestI > 0 && bestI <= maxRemove && bestGap > 0.20f * range && bestGap > 1.5f) lo = v[bestI];
            // lado alto: recorta el clúster superior si es minoritario
            bestGap = 0; bestI = -1;
            for (int i = n - 1; i >= 1 && v[i - 1] >= med; i--) { float g = v[i] - v[i - 1]; if (g > bestGap) { bestGap = g; bestI = i - 1; } }
            if (bestI >= 0 && (n - 1 - bestI) <= maxRemove && bestGap > 0.20f * range && bestGap > 1.5f) hi = v[bestI];
            return (lo, hi);
        }
    }

    public static class ShapeRenderer
    {
        // ---- recursos MonoGame (creados perezosamente en el hilo de UI) ----
        static System.Windows.Forms.Form _hiddenForm;
        static GraphicsDevice _gd;
        static Texture2D _white;
        static readonly Dictionary<string, Texture2D> _texCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        public static string PreviewCacheDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails", "SelectOR_previews");

        public static string CachePathFor(string engPath)
        {
            try
            {
                var key = "v18|" + engPath.ToLowerInvariant();   // versión: invalida cachés de renders anteriores
                uint h = 2166136261; foreach (char c in key) { h = (h ^ c) * 16777619; }
                return Path.Combine(PreviewCacheDir, "s_" + h.ToString("x8") + ".png");
            }
            catch { return null; }
        }

        // -------- Localiza el .s a partir del .eng --------
        public static string ShapePathFor(string engPath)
        {
            try
            {
                if (string.IsNullOrEmpty(engPath) || !File.Exists(engPath)) return null;
                var dir = Path.GetDirectoryName(engPath);
                var text = ReadMaybeUtf16(engPath);
                var m = Regex.Match(text, @"WagonShape\s*\(\s*([^)\s]+)", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var s = Path.Combine(dir, m.Groups[1].Value.Trim().Trim('"'));
                    if (Native.IsLocalFile(s) && File.Exists(s)) return s;
                }
                // fallback: un .s que coincida con el nombre del .eng
                var baseS = Path.Combine(dir, Path.GetFileNameWithoutExtension(engPath) + ".s");
                if (File.Exists(baseS)) return baseS;
                return null;
            }
            catch { return null; }
        }

        // -------- Construcción de geometría (hilo cualquiera, solo CPU) --------
        public static ShapeGeom BuildGeometry(string engPath)
        {
            var sPath = ShapePathFor(engPath);
            if (sPath == null) return null;
            try
            {
                var geom = new ShapeGeom();
                var groups = new Dictionary<string, (List<VertexPositionNormalTexture> v, List<int> idx, int shader, bool alphaTest)>(StringComparer.OrdinalIgnoreCase);

                // 1) Shape principal (WagonShape).
                AddShapeToGroups(sPath, geom, groups);
                // 2) Cargas y shapes extra (Freightanim / ORTSFreightAnims): carrocerías en shape aparte
                //    y, sobre todo, los CONTENEDORES y demás cargas que Open Rails coloca sobre el vagón.
                foreach (var extra in FreightAnimShapes(engPath))
                    AddShapeToGroups(extra.path, geom, groups, extra.offset);

                foreach (var kv in groups)
                {
                    if (kv.Value.v.Count == 0) continue;
                    // la clave es "texPath|shader|alphaTest"; recuperamos texPath quitando los dos últimos campos
                    int cut = kv.Key.LastIndexOf('|');
                    cut = cut > 0 ? kv.Key.LastIndexOf('|', cut - 1) : -1;
                    var texPath = cut > 0 ? kv.Key.Substring(0, cut) : "";
                    geom.Groups.Add(new ShapeGroup { TexPath = texPath, Shader = kv.Value.shader, AlphaTest = kv.Value.alphaTest, Verts = kv.Value.v.ToArray(), Indices = kv.Value.idx.ToArray() });
                }
                // orden de dibujo: opaco primero, blend/add al final (para el z-buffer)
                geom.Groups = geom.Groups.OrderBy(g => g.Shader).ToList();
                return geom.IsEmpty ? null : geom;
            }
            catch { return null; }
        }

        // Vuelca la geometría de un .s en el diccionario de grupos (texPath|shader|alphaTest) compartido,
        // acumulando la bbox en geom. Permite combinar WagonShape + Freightanim en un solo modelo.
        static void AddShapeToGroups(string sPath, ShapeGeom geom,
            Dictionary<string, (List<VertexPositionNormalTexture> v, List<int> idx, int shader, bool alphaTest)> groups,
            Vector3 offset = default)
        {
            if (string.IsNullOrEmpty(sPath) || !File.Exists(sPath)) return;
            try
            {
                var sf = OpenShape(sPath);
                if (sf == null) return;
                var shape = sf.shape;
                if (shape.lod_controls.Count == 0) return;
                var dl = shape.lod_controls[0].distance_levels[0];
                var hierarchy = dl.distance_level_header.hierarchy;
                var modelDir = Path.GetDirectoryName(sPath);

                var world = new Matrix[shape.matrices.Count];
                for (int i = 0; i < shape.matrices.Count; i++) world[i] = Accumulate(i, hierarchy, shape.matrices);

                foreach (var sub in dl.sub_objects)
                {
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
                        string texPath = ResolveTexture(shape, ps, modelDir) ?? "";
                        var (shader, alphaTest) = GetShaderInfo(shape, ps);
                        string key = texPath + "|" + shader + "|" + (alphaTest ? 1 : 0);
                        if (!groups.TryGetValue(key, out var grp))
                        {
                            grp = (new List<VertexPositionNormalTexture>(), new List<int>(), shader, alphaTest);
                            groups[key] = grp;
                        }
                        foreach (var tri in prim.indexed_trilist.vertex_idxs)
                        {
                            foreach (int vi in new[] { tri.a, tri.b, tri.c })
                            {
                                if (vi < 0 || vi >= sub.vertices.Count) { continue; }
                                var vtx = sub.vertices[vi];
                                var p = shape.points[vtx.ipoint];
                                // MSTS es left-handed; negamos Z para convertir a right-handed (evita el efecto espejo).
                                var pos = Vector3.Transform(new Vector3(p.X, p.Y, p.Z), M) + offset;
                                pos.Z = -pos.Z;
                                Vector3 nrm = Vector3.Up;
                                if (vtx.inormal >= 0 && vtx.inormal < shape.normals.Count)
                                {
                                    var nv = shape.normals[vtx.inormal];
                                    nrm = Vector3.TransformNormal(new Vector3(nv.X, nv.Y, nv.Z), M);
                                    nrm.Z = -nrm.Z;
                                    if (nrm.LengthSquared() > 1e-9f) nrm.Normalize(); else nrm = Vector3.Up;
                                }
                                var uv = new Vector2(0, 0);
                                if (vtx.vertex_uvs != null && vtx.vertex_uvs.Length > 0)
                                {
                                    int ui = vtx.vertex_uvs[0];
                                    if (ui >= 0 && ui < shape.uv_points.Count)
                                        uv = new Vector2(shape.uv_points[ui].U, shape.uv_points[ui].V);
                                }
                                grp.idx.Add(grp.v.Count);
                                grp.v.Add(new VertexPositionNormalTexture(pos, nrm, uv));
                                geom.Min = Vector3.Min(geom.Min, pos);
                                geom.Max = Vector3.Max(geom.Max, pos);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // Cargas y shapes extra de un vehículo, con su desplazamiento sobre el vagón.
        //   · MSTS:        Freightanim ( archivo.s alto1 alto2 )
        //   · Open Rails:  ORTSFreightAnims ( FreightAnimStatic ( Shape ( "ruta.s" ) Offset ( x, y, z ) ) )
        // Open Rails lee el vehículo de la subcarpeta OPENRAILS si existe (allí es donde el contenido
        // suele declarar los CONTENEDORES), así que se miran los dos archivos. Las rutas de los shapes
        // son relativas a la carpeta del vehículo (la de fuera de OPENRAILS).
        static IEnumerable<(string path, Vector3 offset)> FreightAnimShapes(string engPath)
        {
            var list = new List<(string path, Vector3 offset)>();
            try
            {
                if (string.IsNullOrEmpty(engPath) || !File.Exists(engPath)) return list;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in VehicleFiles(engPath))
                {
                    string baseDir = BaseVehicleDir(file);
                    string text = ReadMaybeUtf16(file);
                    if (string.IsNullOrEmpty(text)) continue;

                    // Bloques de Open Rails con Shape + Offset (contenedores, cargas…).
                    foreach (Match m in Regex.Matches(text,
                        @"(?<![A-Za-z])Shape\s*\(\s*""?([^)""]+?\.s)""?\s*\)(?<tail>(?:[^)]|\)(?!\s*\))){0,400})",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline))
                    {
                        string rel = m.Groups[1].Value.Trim();
                        var off = Vector3.Zero;
                        var mo = Regex.Match(m.Groups["tail"].Value,
                            @"Offset\s*\(\s*(-?[\d.]+)\s*,?\s*(-?[\d.]+)\s*,?\s*(-?[\d.]+)", RegexOptions.IgnoreCase);
                        if (mo.Success)
                        {
                            float.TryParse(mo.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float ox);
                            float.TryParse(mo.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float oy);
                            float.TryParse(mo.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float oz);
                            off = new Vector3(ox, oy, oz);
                        }
                        AddIfExists(list, seen, baseDir, rel, off);
                    }

                    // Freightanim clásico de MSTS (sin desplazamiento).
                    foreach (Match m in Regex.Matches(text, @"(?<![A-Za-z])Freightanim\s*\(\s*""?([^)\s""]+\.s)""?", RegexOptions.IgnoreCase))
                        AddIfExists(list, seen, baseDir, m.Groups[1].Value.Trim(), Vector3.Zero);
                }
            }
            catch { }
            return list;
        }

        // El .eng/.wag indicado y su equivalente en la subcarpeta OPENRAILS (o al revés).
        static IEnumerable<string> VehicleFiles(string engPath)
        {
            var files = new List<string> { engPath };
            try
            {
                string dir = Path.GetDirectoryName(engPath) ?? "";
                string name = Path.GetFileName(engPath);
                bool inOr = string.Equals(Path.GetFileName(dir), "OPENRAILS", StringComparison.OrdinalIgnoreCase);
                string other = inOr
                    ? Path.Combine(Path.GetDirectoryName(dir) ?? "", name)
                    : Path.Combine(dir, "OPENRAILS", name);
                if (File.Exists(other)) files.Add(other);
            }
            catch { }
            return files;
        }

        // Carpeta del vehículo (si el archivo está en OPENRAILS, la de arriba): las rutas de los
        // shapes de carga son relativas a ella.
        static string BaseVehicleDir(string file)
        {
            string dir = Path.GetDirectoryName(file) ?? "";
            return string.Equals(Path.GetFileName(dir), "OPENRAILS", StringComparison.OrdinalIgnoreCase)
                ? (Path.GetDirectoryName(dir) ?? dir) : dir;
        }

        static void AddIfExists(List<(string path, Vector3 offset)> list, HashSet<string> seen, string baseDir, string rel, Vector3 off)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rel)) return;
                rel = rel.Replace("\\\\", "\\").Replace('/', '\\').Trim().Trim('"');
                string full = Path.GetFullPath(Path.Combine(baseDir, rel));
                if (!Native.IsLocalFile(full) || !File.Exists(full)) return;   // nunca rutas de red
                if (!seen.Add(full + "|" + off)) return;
                list.Add((full, off));
            }
            catch { }
        }

        // Abre un .s con el ShapeFile de OR. Si el parseo normal crashea (bug de OR: en un .s COMPRIMIDO,
        // al encontrar un bloque que dispara un warning intenta leer la posición del stream deflate —no
        // seekable— y lanza NotSupportedException), lo descomprime a un .s temporal SIN comprimir (FileStream
        // con seek) y reintenta. Así se recuperan modelos que si no saldrían en blanco (p. ej. cc_72000).
        static ShapeFile OpenShape(string sPath)
        {
            try { return OrCompat.OpenShapeFile(sPath); }
            catch
            {
                string tmp = null;
                try
                {
                    tmp = DecompressShapeToTemp(sPath);
                    if (tmp != null) return OrCompat.OpenShapeFile(tmp);
                }
                catch { }
                finally { if (tmp != null) try { File.Delete(tmp); } catch { } }
                return null;
            }
        }

        // Reescribe un .s comprimido ("SIMISA@F" + tamaño + zlib) como .s sin comprimir ("SIMISA@@…") en un
        // temporal, para que OR lo lea desde un FileStream con seek y sus warnings no crasheen.
        static string DecompressShapeToTemp(string sPath)
        {
            var comp = File.ReadAllBytes(sPath);
            if (comp.Length < 18) return null;
            if (!(comp[0] == 'S' && comp[1] == 'I' && comp[2] == 'M' && comp[3] == 'I' &&
                  comp[4] == 'S' && comp[5] == 'A' && comp[6] == '@' && comp[7] == 'F')) return null; // no comprimido
            byte[] body;
            using (var ms = new MemoryStream(comp, 18, comp.Length - 18))            // salta cabecera(16)+zlib(2)
            using (var def = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress))
            using (var outMs = new MemoryStream())
            { def.CopyTo(outMs); body = outMs.ToArray(); }                            // = subcabecera + datos
            var outFile = new byte[16 + body.Length];
            Encoding.ASCII.GetBytes("SIMISA@@@@@@@@@@").CopyTo(outFile, 0);
            Buffer.BlockCopy(body, 0, outFile, 16, body.Length);
            var tmp = Path.Combine(Path.GetTempPath(), "selector_" + Guid.NewGuid().ToString("N") + ".s");
            File.WriteAllBytes(tmp, outFile);
            return tmp;
        }

        static Matrix Accumulate(int i, int[] hierarchy, dynamic matrices)
        {
            var M = Matrix.Identity;
            int idx = i, guard = 0;
            while (idx >= 0 && idx < matrices.Count && guard++ < 128)
            {
                M = M * ToXna(matrices[idx]);
                idx = (hierarchy != null && idx < hierarchy.Length) ? hierarchy[idx] : -1;
            }
            return M;
        }

        static Matrix ToXna(dynamic m) => new Matrix(
            m.AX, m.AY, m.AZ, 0,
            m.BX, m.BY, m.BZ, 0,
            m.CX, m.CY, m.CZ, 0,
            m.DX, m.DY, m.DZ, 1);

        // Igual que Open Rails: el nombre del shader define blend/add; alphatestmode define el recorte.
        // shader: 0 = opaco (Tex/TexDiff), 1 = blend (BlendA*), 2 = add (AddA*)
        static (int shader, bool alphaTest) GetShaderInfo(shape shape, prim_state ps)
        {
            bool alphaTest = ps.alphatestmode == 1;
            int shader = 0;
            try
            {
                string sh = "";
                if (ps.ishader >= 0 && ps.ishader < shape.shader_names.Count)
                    sh = (shape.shader_names[ps.ishader] ?? "").ToLowerInvariant();
                if (sh.StartsWith("blenda")) shader = 1;
                else if (sh.StartsWith("adda")) shader = 2;
            }
            catch { }
            return (shader, alphaTest);
        }

        // Índice global de texturas por nombre de archivo (una vez por sesión, perezoso, en el hilo de
        // BuildGeometry). Sólo se construye cuando una textura NO se encuentra en la carpeta del modelo.
        static Dictionary<string, string> _texIndex;
        static readonly object _texIndexLock = new object();

        static string ResolveViaIndex(string modelDir, string wantedFile, string stem)
        {
            try
            {
                EnsureTexIndex(modelDir);
                if (_texIndex == null) return null;
                // 1) nombre exacto tal cual lo pide el .s
                if (_texIndex.TryGetValue(wantedFile, out var p) && File.Exists(p)) return p;
                // 2) mismo nombre con otra extensión de textura
                foreach (var ext in new[] { ".ace", ".dds", ".png", ".bmp" })
                    if (_texIndex.TryGetValue(stem + ext, out var q) && File.Exists(q)) return q;
                return null;
            }
            catch { return null; }
        }

        static void EnsureTexIndex(string modelDir)
        {
            if (_texIndex != null) return;
            lock (_texIndexLock)
            {
                if (_texIndex != null) return;
                var idx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var root = FindTrainsetRoot(modelDir);
                    if (root != null)
                        foreach (var pat in new[] { "*.ace", "*.dds" })
                            foreach (var f in Directory.EnumerateFiles(root, pat, SearchOption.AllDirectories))
                            {
                                var name = Path.GetFileName(f);
                                if (!idx.ContainsKey(name)) idx[name] = f;
                            }
                }
                catch { }
                _texIndex = idx;   // aunque quede vacío, evita reintentar
            }
        }

        static string FindTrainsetRoot(string modelDir)
        {
            var d = modelDir;
            while (!string.IsNullOrEmpty(d))
            {
                if (string.Equals(Path.GetFileName(d), "TRAINSET", StringComparison.OrdinalIgnoreCase)) return d;
                d = Path.GetDirectoryName(d);
            }
            return Path.GetDirectoryName(modelDir);
        }

        static string ResolveTexture(shape shape, prim_state ps, string modelDir)
        {
            try
            {
                if (ps.tex_idxs == null || ps.tex_idxs.Length == 0) return null;
                int ti = ps.tex_idxs[0];
                if (ti < 0 || ti >= shape.textures.Count) return null;
                int iImage = shape.textures[ti].iImage;
                if (iImage < 0 || iImage >= shape.images.Count) return null;
                var file = shape.images[iImage];
                if (string.IsNullOrWhiteSpace(file)) return null;
                var full = Path.Combine(modelDir, file);
                if (!Native.IsLocalFile(full)) return null;   // nunca rutas de red
                if (File.Exists(full)) return full;
                // El .s suele referenciar ".ace" aunque el modelo traiga la textura en otro formato
                // (muchos modelos de Open Rails usan .dds). Probamos varias extensiones y la subcarpeta OPENRAILS.
                var stem = Path.GetFileNameWithoutExtension(file);
                foreach (var dir in new[] { modelDir, Path.Combine(modelDir, "OPENRAILS"), Path.Combine(modelDir, "TEXTURES") })
                {
                    if (dir == null) continue;
                    foreach (var ext in new[] { ".ace", ".dds", ".png", ".tga", ".bmp" })
                    {
                        var cand = Path.Combine(dir, stem + ext);
                        if (File.Exists(cand)) return cand;
                    }
                }
                // Último recurso: textura compartida en OTRA carpeta del árbol TRAINSET (bases/repintados
                // que reutilizan texturas comunes como CORAIL_B.ace, blank.ace…). Solo se usa si la
                // resolución local ha fallado, así que nunca cambia un modelo que ya se ve bien.
                return ResolveViaIndex(modelDir, file, stem);
            }
            catch { return null; }
        }

        // -------- Render en perspectiva frontal-lateral, con rotación opcional (grados) --------
        // conFlip: flag Flip del vehículo en el .con. Si se pasa, la orientación sigue la MISMA regla que la
        // composición 2D (fuente fiable = Open Rails): rotate = Flip XOR K, en vez de detectar la cabina.
        // Si es null (sin contexto de consist), se mantiene el comportamiento anterior (cabina hacia cámara).
        // distance: separación de la cámara en radios del modelo (a más, el tren se ve más lejos).
        public static GdiBitmap Render(ShapeGeom geom, int w, int h, float yawDeg = 0f, float pitchDeg = 0f, int ss = 1, bool? conFlip = null, float distance = 2.25f)
        {
            if (geom == null || geom.IsEmpty) return null;
            try
            {
                EnsureDevice();
                int rw = w * ss, rh = h * ss; // supersampling: renderizamos a mayor resolución y reducimos (antialiasing)
                using (var rt = new RenderTarget2D(_gd, rw, rh, false, SurfaceFormat.Color, DepthFormat.Depth24))
                {
                    _gd.SetRenderTarget(rt);
                    _gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Microsoft.Xna.Framework.Color(0, 0, 0, 0), 1f, 0);

                    geom.EnsureRobustBounds();
                    var center = (geom.RMin + geom.RMax) * 0.5f;
                    float radius = (geom.RMax - geom.RMin).Length() * 0.5f;
                    if (radius < 0.01f) radius = 1f;

                    // cámara esférica orbitando el modelo (vista frontal-lateral 3/4, con más zoom).
                    // La cámara mira hacia el lado +Z; si el morro está en -Z, giramos el modelo 180°
                    // para que la cabina quede siempre hacia la cámara (frente a la izquierda, vista buena).
                    float az = 0.62f + MathHelper.ToRadians(yawDeg);
                    float el = MathHelper.Clamp(0.13f + MathHelper.ToRadians(pitchDeg), -1.4f, 1.4f);
                    var dir = new Vector3((float)(Math.Cos(el) * Math.Sin(az)), (float)Math.Sin(el), (float)(Math.Cos(el) * Math.Cos(az)));
                    var eye = center + dir * radius * Math.Max(0.5f, distance);   // más lejos → el tren no toca el marco
                    var view = Matrix.CreateLookAt(eye, center, Vector3.Up);
                    var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(30f), (float)rw / rh, Math.Max(0.05f, radius * 0.05f), radius * 16f);

                    // Orientación: si tenemos el flag Flip del .con, usamos la MISMA regla que la 2D
                    // (rotate = Flip XOR K, K=true) para que el 3D apunte en la dirección del consist,
                    // igual que Open Rails. Sin consist, orientamos la cabina hacia la cámara (textura→geometría).
                    bool rotate180;
                    if (conFlip.HasValue)
                    {
                        const bool K = true;   // misma constante de coordenadas que CompositionDialog
                        rotate180 = conFlip.Value ^ K;
                    }
                    else
                    {
                        int headSign = CabZSign(geom); if (headSign == 0) headSign = NoseZSign(geom);
                        rotate180 = headSign < 0;
                    }
                    var world = rotate180
                        ? Matrix.CreateTranslation(-center) * Matrix.CreateRotationY(MathHelper.Pi) * Matrix.CreateTranslation(center)
                        : Matrix.Identity;
                    DrawGeom(geom, view, proj, false, world);
                    _gd.SetRenderTarget(null);

                    var data = new Microsoft.Xna.Framework.Color[rw * rh];
                    rt.GetData(data);
                    using (var big = ToBitmap(data, rw, rh)) return Downscale(big, w, h);
                }
            }
            catch { return null; }
        }

        static GdiBitmap Downscale(GdiBitmap src, int w, int h)
        {
            if (src.Width == w && src.Height == h) return (GdiBitmap)src.Clone();
            var dst = new GdiBitmap(w, h, GdiPixel.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(dst))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.DrawImage(src, 0, 0, w, h);
            }
            return dst;
        }

        // Palabras clave de texturas de cabina/frontal (subcadena, minúsculas). "cab" cubre cabina/cab.
        static readonly string[] CabTexKeywords =
            { "frente", "front", "fte", "frontal", "cab", "morro", "nariz", "nose", "parab", "windscreen", "trompa" };

        /// <summary>
        /// Determina en qué extremo (Z) está la CABINA de un vehículo, de forma fiable, por el NOMBRE de la
        /// textura (los modelos suelen nombrarla "front/frente/cab/morro..."): promedia la Z de esa geometría.
        /// +1 = cabina en +Z, -1 = cabina en -Z, 0 = indeterminado/simétrico (doble cabina → frontal a ambos
        /// extremos, mediaZ≈0). Si no hay textura de cabina reconocible, recae en NoseZSign (geometría).
        /// Es la señal ABSOLUTA que, combinada con el flag Flip del consist, orienta bien la composición 2D.
        /// </summary>
        public static int CabZSign(ShapeGeom g)
        {
            if (g == null || g.IsEmpty) return 0;
            float len = g.Max.Z - g.Min.Z;
            if (len < 3f) return 0;
            double sumZ = 0; long cnt = 0;
            foreach (var grp in g.Groups)
            {
                string tn = "";
                try { tn = (Path.GetFileNameWithoutExtension(grp.TexPath) ?? "").ToLowerInvariant(); } catch { }
                if (tn.Length == 0) continue;
                bool hit = false;
                foreach (var k in CabTexKeywords) if (tn.Contains(k)) { hit = true; break; }
                if (!hit) continue;
                foreach (var v in grp.Verts) sumZ += v.Position.Z;
                cnt += grp.Verts.Length;
            }
            if (cnt > 30)
            {
                double meanZ = sumZ / cnt;
                // La cabina debe quedar claramente descentrada (un solo extremo). Doble cabina → mediaZ≈0.
                float thr = Math.Max(1.2f, len * 0.10f);
                if (meanZ > thr) return +1;
                if (meanZ < -thr) return -1;
            }
            // Sin textura de cabina reconocible → 0 (simétrico). NO recurrimos a NoseZSign aquí: en la
            // composición 2D eso giraría vagones de mercancías asimétricos de forma alterna (incoherente).
            return 0;
        }

        /// <summary>
        /// Detecta en qué extremo (Z) está el "morro" de un vehículo comparando la sección transversal
        /// (ancho×alto) del cuarto delantero frente al trasero. +1 = morro en +Z, -1 = morro en -Z,
        /// 0 = simétrico (coche/loco de doble cabina: no conviene girarlo para no espejar la textura).
        /// </summary>
        public static int NoseZSign(ShapeGeom g)
        {
            if (g == null || g.IsEmpty) return 0;
            float zmin = g.Min.Z, zmax = g.Max.Z, len = zmax - zmin;
            if (len < 3f) return 0;
            // Altura del techo (maxY) en la punta delantera/trasera (12%) y en el cuerpo central (40%).
            float tip = len * 0.12f, m0 = zmin + len * 0.30f, m1 = zmax - len * 0.30f;
            float fTop = float.MinValue, bTop = float.MinValue, bodyTop = float.MinValue;
            int fc = 0, bc = 0;
            foreach (var grp in g.Groups)
                foreach (var v in grp.Verts)
                {
                    float z = v.Position.Z, y = v.Position.Y;
                    if (z >= zmax - tip) { fTop = Math.Max(fTop, y); fc++; }
                    else if (z <= zmin + tip) { bTop = Math.Max(bTop, y); bc++; }
                    if (z >= m0 && z <= m1) bodyTop = Math.Max(bodyTop, y);
                }
            if (fc < 10 || bc < 10 || bodyTop <= float.MinValue) return 0;
            // Normalizamos respecto a la base (minY) para medir la caída de la línea de techo.
            float baseY = g.Min.Y;
            float fh = fTop - baseY, bh = bTop - baseY, body = bodyTop - baseY;
            if (body < 0.5f) return 0;
            float fr = fh / body, br = bh / body;
            // 1) Un morro aerodinámico hace caer el techo en la punta (~<0.85 del cuerpo) más que el otro extremo.
            if (fr < 0.85f && fr < br - 0.06f) return +1;   // morro en +Z
            if (br < 0.85f && br < fr - 0.06f) return -1;   // morro en -Z
            // 2) Respaldo (frentes planos): la cabina tiene más detalle (parabrisas, faros, limpias, topes)
            //    → claramente más vértices en su extremo que el extremo de enganche/pasillo.
            if (fc > bc * 1.35f && fc > 40) return +1;      // cabina en +Z
            if (bc > fc * 1.35f && bc > 40) return -1;      // cabina en -Z
            return 0;                                        // simétrico / indeterminado
        }

        // -------- Render lateral ortográfico a escala consistente (composición 2D) --------
        // ppm = píxeles por metro; worldHeight = altura de encuadre en metros (igual para todos los coches).
        public static GdiBitmap RenderSide(ShapeGeom geom, float ppm, float worldHeight, bool flip)
        {
            if (geom == null || geom.IsEmpty) return null;
            try
            {
                EnsureDevice();
                geom.EnsureRobustBounds();
                float lenZ = Math.Max(0.3f, geom.RMax.Z - geom.RMin.Z) * 1.02f;
                int w = Math.Max(8, (int)(lenZ * ppm));
                int h = Math.Max(8, (int)(worldHeight * ppm));
                if (w > 4000) w = 4000;
                int ss = 2, rw = w * ss, rh = h * ss;   // supersampling ×2: suaviza ruido/aliasing de texturas

                using (var rt = new RenderTarget2D(_gd, rw, rh, false, SurfaceFormat.Color, DepthFormat.Depth24))
                {
                    _gd.SetRenderTarget(rt);
                    _gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Microsoft.Xna.Framework.Color(0, 0, 0, 0), 1f, 0);

                    float cz = (geom.RMin.Z + geom.RMax.Z) * 0.5f;
                    var center = new Vector3((geom.RMin.X + geom.RMax.X) * 0.5f, (geom.RMin.Y + geom.RMax.Y) * 0.5f, cz);
                    var target = new Vector3(center.X, geom.RMin.Y + worldHeight * 0.5f, cz);
                    // Se mira SIEMPRE desde el mismo lado de la vía (+X) para conservar el texto correcto.
                    // El Flip se aplica como ROTACIÓN REAL de 180° sobre el eje Y del propio coche
                    // (matriz de mundo), no moviendo la cámara: así el conjunto queda como en la vía.
                    float dist = Math.Max(50f, (geom.Max.X - geom.Min.X) * 4 + 10);
                    var eye = new Vector3(target.X + dist, target.Y, target.Z);   // lado +X: conserva el texto correcto
                    var view = Matrix.CreateLookAt(eye, target, Vector3.Up);
                    var proj = Matrix.CreateOrthographic(lenZ, worldHeight, 0.01f, 1000f);
                    // El Flip se aplica como ROTACIÓN REAL de 180° sobre el eje Y (matriz de mundo), sin espejo.
                    var world = flip
                        ? Matrix.CreateTranslation(-center) * Matrix.CreateRotationY(MathHelper.Pi) * Matrix.CreateTranslation(center)
                        : Matrix.Identity;
                    DrawGeom(geom, view, proj, false, world);
                    _gd.SetRenderTarget(null);

                    var data = new Microsoft.Xna.Framework.Color[rw * rh];
                    rt.GetData(data);
                    using (var big = ToBitmap(data, rw, rh)) return Downscale(big, w, h);
                }
            }
            catch { return null; }
        }

        static RasterizerState _cullSolid, _cullSolidBack;
        static Effect _shapeFx;
        static EffectParameter _pWVP, _pWV, _pSun, _pAmb, _pDif, _pVA, _pTex;
        static bool _shapeFxTried;
        static readonly Vector3 SunView = Vector3.Normalize(new Vector3(-1f, 2f, 1f));   // sol en espacio de vista (TSRE5)

        static bool EnsureShapeFx()
        {
            if (_shapeFx != null) return true;
            if (_shapeFxTried) return false;
            _shapeFxTried = true;
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "shape.mgfx");
                _shapeFx = new Effect(_gd, File.ReadAllBytes(path));
                _pWVP = _shapeFx.Parameters["WorldViewProjection"];
                _pWV = _shapeFx.Parameters["WorldView"];
                _pSun = _shapeFx.Parameters["SunDir"];
                _pAmb = _shapeFx.Parameters["Ambient"];
                _pDif = _shapeFx.Parameters["Diffuse"];
                _pVA = _shapeFx.Parameters["VAlpha"];
                _pTex = _shapeFx.Parameters["Tex"];
                return true;
            }
            catch { _shapeFx = null; return false; }
        }

        static BasicEffect _basicFx;

        // BasicEffect integrado en MonoGame: respaldo cuando el shader propio (shape.mgfx) no carga
        // (p. ej. con una versión de MonoGame distinta a la de compilación, como en OR "New Year").
        // Garantiza que los trenes SE VEAN en cualquier versión de Open Rails, aunque la luz no sea
        // idéntica a la del shader de TSRE5.
        static bool EnsureBasicFx()
        {
            if (_basicFx != null) return true;
            try
            {
                _basicFx = new BasicEffect(_gd)
                {
                    TextureEnabled = true,
                    VertexColorEnabled = false,
                    LightingEnabled = true,
                    PreferPerPixelLighting = true,
                    AmbientLightColor = new Vector3(0.62f),
                    DiffuseColor = Vector3.One,
                    SpecularColor = Vector3.Zero,
                };
                _basicFx.DirectionalLight0.Enabled = true;
                _basicFx.DirectionalLight0.DiffuseColor = new Vector3(0.5f);
                _basicFx.DirectionalLight0.SpecularColor = Vector3.Zero;
                _basicFx.DirectionalLight1.Enabled = false;
                _basicFx.DirectionalLight2.Enabled = false;
                return true;
            }
            catch { _basicFx = null; return false; }
        }

        // Dibuja con el shader propio (réplica de TSRE5) si carga; si no, con BasicEffect (respaldo).
        static void DrawGeom(ShapeGeom geom, Matrix view, Matrix proj, bool backSide = false, Matrix? worldOpt = null)
        {
            var world = worldOpt ?? Matrix.Identity;
            _cullSolid ??= new RasterizerState { CullMode = CullMode.CullCounterClockwiseFace };
            _cullSolidBack ??= new RasterizerState { CullMode = CullMode.CullClockwiseFace };

            bool custom = EnsureShapeFx();
            if (!custom && !EnsureBasicFx()) return;   // no hay forma de dibujar

            _gd.BlendState = BlendState.NonPremultiplied;
            _gd.DepthStencilState = DepthStencilState.Default;
            _gd.RasterizerState = backSide ? _cullSolidBack : _cullSolid;
            _gd.SamplerStates[0] = SamplerState.LinearWrap;

            if (custom)
            {
                _pWVP.SetValue(world * view * proj);
                _pWV.SetValue(world * view);
                _pSun.SetValue(SunView);
                _pAmb.SetValue(new Vector3(0.3f));
                _pDif.SetValue(new Vector3(0.7f));
            }
            else
            {
                _basicFx.World = world;
                _basicFx.View = view;
                _basicFx.Projection = proj;
                // luz desde arriba-frente (espacio de mundo); ambient alto para que nada quede negro
                _basicFx.DirectionalLight0.Direction = Vector3.Normalize(new Vector3(-0.35f, -1f, -0.5f));
            }

            foreach (var grp in geom.Groups)
            {
                var tex = LoadTexture(grp.TexPath, out _) ?? White();
                Effect fx;
                if (custom)
                {
                    float vAlpha = grp.AlphaTest ? -0.51f : (grp.Shader != 0 ? 0f : 1f);
                    _pVA.SetValue(vAlpha);
                    _pTex.SetValue(tex);
                    fx = _shapeFx;
                }
                else
                {
                    _basicFx.Texture = tex;
                    fx = _basicFx;
                }
                foreach (var pass in fx.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    _gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, grp.Verts, 0, grp.Verts.Length, grp.Indices, 0, grp.Indices.Length / 3);
                }
            }
            _gd.BlendState = BlendState.Opaque;
            _gd.DepthStencilState = DepthStencilState.Default;
        }

        /// <summary>Crea ya el dispositivo gráfico (hilo de la interfaz, cuando está libre): así el primer
        /// render de la vista 3D no tiene que pagar su creación.</summary>
        public static void WarmUp() { try { EnsureDevice(); } catch { } }

        static void EnsureDevice()
        {
            if (_gd != null) return;
            _hiddenForm = new System.Windows.Forms.Form { ShowInTaskbar = false };
            var pp = new PresentationParameters
            {
                BackBufferWidth = 16,
                BackBufferHeight = 16,
                DeviceWindowHandle = _hiddenForm.Handle,
                IsFullScreen = false,
                DepthStencilFormat = DepthFormat.Depth24
            };
            _gd = new GraphicsDevice(GraphicsAdapter.DefaultAdapter, GraphicsProfile.HiDef, pp);
        }

        static Texture2D White()
        {
            if (_white == null)
            {
                _white = new Texture2D(_gd, 1, 1);
                _white.SetData(new[] { new Microsoft.Xna.Framework.Color(200, 200, 205) });
            }
            return _white;
        }

        static readonly Dictionary<string, int> _texAlpha = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Teleindicador elegido (Teleindicadores.cs): la vista 3D enseña el cartel del destino en lugar
        // del fichero que hay ahora en la carpeta del tren. Clave = ruta completa del fichero del tren.
        static readonly Dictionary<string, string> _texRedirect = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Sustituye las redirecciones de esas carpetas de tren por las nuevas.
        public static void SetTextureRedirects(IEnumerable<string> trainDirs, Dictionary<string, string> map)
        {
            foreach (var d in trainDirs)
            {
                string pre = Path.GetFullPath(d).TrimEnd('\\') + "\\";
                foreach (var k in new List<string>(_texRedirect.Keys))
                    if (k.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) _texRedirect.Remove(k);
            }
            if (map != null) foreach (var kv in map) { try { _texRedirect[Path.GetFullPath(kv.Key)] = kv.Value; } catch { } }
        }

        static string Redirect(string path)
        {
            if (_texRedirect.Count == 0) return path;
            try { return _texRedirect.TryGetValue(Path.GetFullPath(path), out var r) && File.Exists(r) ? r : path; }
            catch { return path; }
        }

        // ---- Texturas preparadas en segundo plano ----
        // Descodificar una textura (.ace/.dds), hacer sus versiones reducidas y convertir los colores es lo
        // que más tarda del primer render (casi un segundo con un tren nuevo). PrefetchTextures lo hace en
        // cualquier hilo en cuanto hay geometría; al renderizar solo queda subirlo a la tarjeta.
        sealed class Decoded { public readonly List<(int w, int h, Microsoft.Xna.Framework.Color[] px)> Levels = new(); public int Bits; }
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Decoded> _texReady = new(StringComparer.OrdinalIgnoreCase);
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _texUploaded = new(StringComparer.OrdinalIgnoreCase);

        public static void PrefetchTextures(ShapeGeom geom)
        {
            if (geom == null) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in geom.Groups)
            {
                string p = g?.TexPath;
                if (string.IsNullOrEmpty(p)) continue;
                try { p = Redirect(p); } catch { }
                if (!seen.Add(p) || _texUploaded.ContainsKey(p) || _texReady.ContainsKey(p)) continue;
                try { var d = Decode(p); if (d != null) _texReady[p] = d; } catch { }
            }
            if (_texReady.Count > 300) _texReady.Clear();   // por si se acumulan texturas que nunca se piden
        }

        static Decoded Decode(string path)
        {
            using var bmp = LoadBitmap(path, out int bits);
            if (bmp == null) return null;
            var d = new Decoded { Bits = bits };
            d.Levels.Add((bmp.Width, bmp.Height, ToColors(bmp)));
            GdiBitmap cur = bmp; bool disposeCur = false; int lw = bmp.Width, lh = bmp.Height;
            while (lw > 1 || lh > 1)
            {
                int nw = Math.Max(1, lw / 2), nh = Math.Max(1, lh / 2);
                var down = MipDown(cur, nw, nh);
                if (disposeCur) cur.Dispose();
                d.Levels.Add((nw, nh, ToColors(down)));
                cur = down; disposeCur = true; lw = nw; lh = nh;
            }
            if (disposeCur) cur.Dispose();
            return d;
        }

        static GdiBitmap MipDown(GdiBitmap src, int nw, int nh)
        {
            var down = new GdiBitmap(nw, nh, GdiPixel.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(down))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, nw, nh);
            }
            return down;
        }

        static Texture2D FromDecoded(Decoded d)
        {
            var l0 = d.Levels[0];
            Texture2D tex;
            try
            {
                tex = new Texture2D(_gd, l0.w, l0.h, d.Levels.Count > 1, SurfaceFormat.Color);
                for (int i = 0; i < d.Levels.Count; i++) tex.SetData(i, null, d.Levels[i].px, 0, d.Levels[i].px.Length);
            }
            catch
            {
                tex = new Texture2D(_gd, l0.w, l0.h, false, SurfaceFormat.Color);
                tex.SetData(0, null, l0.px, 0, l0.px.Length);
            }
            return tex;
        }

        static Texture2D LoadTexture(string path, out int alphaBits)
        {
            alphaBits = 0;
            if (string.IsNullOrEmpty(path)) return null;
            path = Redirect(path);
            if (_texCache.TryGetValue(path, out var t)) { _texAlpha.TryGetValue(path, out alphaBits); return t; }
            _texUploaded[path] = 0;
            if (_texReady.TryRemove(path, out var ready))
            {
                Texture2D rt = null;
                try { rt = FromDecoded(ready); } catch { rt = null; }
                _texCache[path] = rt; _texAlpha[path] = ready.Bits; alphaBits = ready.Bits;
                return rt;
            }
            Texture2D tex = null;
            int bits = 0;
            try
            {
                using (var bmp = LoadBitmap(path, out bits))
                {
                    if (bmp != null)
                    {
                        int w = bmp.Width, h = bmp.Height;
                        // nº de niveles mip (hasta 1x1): reduce el ruido/aliasing al minificar (techos, rejillas)
                        int levels = 1; for (int mw = w, mh = h; mw > 1 || mh > 1; ) { mw = Math.Max(1, mw / 2); mh = Math.Max(1, mh / 2); levels++; }
                        try { tex = new Texture2D(_gd, w, h, true, SurfaceFormat.Color); SetTexLevel(tex, 0, bmp); }
                        catch { tex = new Texture2D(_gd, w, h, false, SurfaceFormat.Color); SetTexLevel(tex, 0, bmp); levels = 1; }
                        if (levels > 1)
                        {
                            GdiBitmap cur = bmp; bool disposeCur = false; int lw = w, lh = h;
                            for (int lvl = 1; lvl < levels; lvl++)
                            {
                                int nw = Math.Max(1, lw / 2), nh = Math.Max(1, lh / 2);
                                var down = new GdiBitmap(nw, nh, GdiPixel.Format32bppArgb);
                                using (var g = System.Drawing.Graphics.FromImage(down))
                                {
                                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                                    g.DrawImage(cur, 0, 0, nw, nh);
                                }
                                if (disposeCur) cur.Dispose();
                                SetTexLevel(tex, lvl, down);
                                cur = down; disposeCur = true; lw = nw; lh = nh;
                            }
                            if (disposeCur) cur.Dispose();
                        }
                    }
                }
            }
            catch { tex = null; }
            _texCache[path] = tex;
            _texAlpha[path] = bits;
            alphaBits = bits;
            return tex;
        }

        static void SetTexLevel(Texture2D tex, int level, GdiBitmap bmp)
        {
            var data = ToColors(bmp);
            tex.SetData(level, null, data, 0, data.Length);
        }

        static Microsoft.Xna.Framework.Color[] ToColors(GdiBitmap bmp)
        {
            var data = new Microsoft.Xna.Framework.Color[bmp.Width * bmp.Height];
            var bd = bmp.LockBits(new GdiRect(0, 0, bmp.Width, bmp.Height), GdiLock.ReadOnly, GdiPixel.Format32bppArgb);
            var buf = new byte[bmp.Width * bmp.Height * 4];
            System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
            bmp.UnlockBits(bd);
            for (int i = 0; i < data.Length; i++) // BGRA -> RGBA
                data[i] = new Microsoft.Xna.Framework.Color(buf[i * 4 + 2], buf[i * 4 + 1], buf[i * 4 + 0], buf[i * 4 + 3]);
            return data;
        }

        static GdiBitmap LoadBitmap(string path, out int alphaBits)
        {
            alphaBits = 0;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".ace") return AceImage.Load(path, out alphaBits);
            if (ext == ".dds") return AceImage.LoadDds(path, out alphaBits);
            using (var img = System.Drawing.Image.FromFile(path))
            {
                alphaBits = System.Drawing.Image.IsAlphaPixelFormat(img.PixelFormat) ? 8 : 0;
                return new GdiBitmap(img);
            }
        }

        static GdiBitmap ToBitmap(Microsoft.Xna.Framework.Color[] data, int w, int h)
        {
            var bmp = new GdiBitmap(w, h, GdiPixel.Format32bppArgb);
            var bd = bmp.LockBits(new GdiRect(0, 0, w, h), GdiLock.WriteOnly, GdiPixel.Format32bppArgb);
            var buf = new byte[w * h * 4];
            for (int i = 0; i < data.Length; i++)
            {
                buf[i * 4 + 0] = data[i].B; buf[i * 4 + 1] = data[i].G; buf[i * 4 + 2] = data[i].R; buf[i * 4 + 3] = data[i].A;
            }
            System.Runtime.InteropServices.Marshal.Copy(buf, 0, bd.Scan0, buf.Length);
            bmp.UnlockBits(bd);
            return bmp;
        }

        static string ReadMaybeUtf16(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
