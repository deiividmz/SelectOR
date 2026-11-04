// (1.2.85) Gente en los andenes: las personas animadas (Microsoft Rocketbox, licencia MIT) se preparan en cada ordenador a
// partir del paquete «Gente.pack» que va junto a SelectOR (24 MB en vez de los ~500 MB de las posturas hechas). En él, por
// persona: su malla (puntos, normales, coordenadas de textura, pesos de los huesos y triángulos), sus texturas (512 px) y,
// por cada postura, la matriz de cada hueso. Aquí se deforma la malla de cada postura con sus huesos y se escribe un .s de
// Open Rails (texto ASCII comprimido «SIMISA@F»: lo leen NY, CYZY, Testing y Estable) y las texturas en .ace, igual que el
// conversor con el que se hizo la prueba (scratchpad rocketbox/conv). Se hace una vez, en segundo plano, en la carpeta de
// cachés de SelectOR (Gente\<huella del paquete>); mientras tanto, esa sesión va sin gente. Open Rails la recibe por
// SELECTOR_GENTE_DIR (MapAgent\Viajeros3D.cs).
// El paquete NO va en la actualización (el actualizador solo cambia SelectOR.exe/.dll…, y el de la 1.2.84 no sabría ponerlo):
// lo sube el administrador al publicar (Updater.PublishAsync, «Gente_<huella>.pack» en el mismo sitio que las versiones) y
// cada SelectOR lo descarga la primera vez; solo se usa si su SHA-256 es PackSha (va dentro de SelectOR.dll, firmado).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SelectOR
{
    public static class GenteFiles
    {
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        const float Scale = 0.01f;   // (el paquete va en cm)
        // la huella del paquete que va con esta versión (rocketbox/Gente.pack del conversor; si se rehace, se cambia aquí)
        public const string PackSha = "9bb7239b7a890a8d7abfdbfbcbcc77aa93f3ba68ef301b1d0133fbcb7f0c9a88";   // (38 personas, sin f06, f10, f16, m15, m19 y m21; y desde 2026-11-04, para los objetos de la sala, 9 operarios de vía «obra_…», 4 derrumbes y el fuego «obj_…»)
        public static string PackName => "Gente_" + PackSha.Substring(0, 12) + ".pack";
        public static string PackUrl => Supa.BuiltInUrl.TrimEnd('/') + "/storage/v1/object/public/updates/" + PackName;
        public static string LocalPack => Path.Combine(AppContext.BaseDirectory, "Gente.pack");   // (junto a SelectOR: el del administrador)
        static string Root => Path.Combine(AppDataTidy.CacheRoot, "Gente");
        static string CachedPack => Path.Combine(Root, PackName);
        static string PackPath;   // el que se usa (comprobado)
        static Task _task; static readonly object _lock = new object();

        // la carpeta lista para Open Rails (null si aún no está)
        public static string ReadyDir()
        {
            try
            {
                var dev = Path.Combine(AppContext.BaseDirectory, "Gente");   // (pruebas: ya hecha junto a SelectOR)
                if (File.Exists(Path.Combine(dev, "gente.txt"))) return dev;
                var d = DirFor();
                return d != null && File.Exists(Path.Combine(d, "listo.txt")) ? d : null;
            }
            catch { return null; }
        }
        static string DirFor() => Path.Combine(Root, PackSha.Substring(0, 12));
        public static bool ShaOk(string file)
        {
            try { using var fs = File.OpenRead(file); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).Equals(PackSha, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        public static bool Busy => _task != null && !_task.IsCompleted;

        // se preparan en segundo plano (una vez), si hace falta
        public static void EnsureAsync(Action<string> log)
        {
            lock (_lock)
            {
                if (_task != null || ReadyDir() != null) return;
                _task = Task.Run(async () => { if (await GetPack(log)) Build(log); });
            }
        }

        // el paquete: el de junto a SelectOR o el ya descargado, si su huella es la buena; si no, se descarga (24 MB)
        static async Task<bool> GetPack(Action<string> log)
        {
            if (File.Exists(LocalPack) && ShaOk(LocalPack)) { PackPath = LocalPack; return true; }
            if (File.Exists(CachedPack) && ShaOk(CachedPack)) { PackPath = CachedPack; return true; }
            try
            {
                Directory.CreateDirectory(Root);
                string tmp = CachedPack + ".tmp";
                log?.Invoke("gente en los andenes: descargando las personas (24 MB, la primera vez)…");
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(15) })
                using (var resp = await http.GetAsync(PackUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!resp.IsSuccessStatusCode) { log?.Invoke($"gente en los andenes: no se ha podido descargar ({(int)resp.StatusCode}); se volverá a intentar en otra sesión"); return false; }
                    using var src = await resp.Content.ReadAsStreamAsync(); using var dst = File.Create(tmp);
                    await src.CopyToAsync(dst);
                }
                if (!ShaOk(tmp)) { File.Delete(tmp); log?.Invoke("gente en los andenes: el paquete descargado no es el bueno (huella distinta): no se usa"); return false; }
                if (File.Exists(CachedPack)) File.Delete(CachedPack);
                File.Move(tmp, CachedPack);
                PackPath = CachedPack;
                return true;
            }
            catch (Exception ex) { log?.Invoke("gente en los andenes: no se ha podido descargar: " + ex.Message); return false; }
        }

        sealed class MeshData
        {
            public int Tex, Nv, Nb; public float[] P, N, UV; public byte[] Wc; public byte[] Wb; public float[] Ww; public ushort[] F;
        }

        static void Build(Action<string> log)
        {
            var sw = Stopwatch.StartNew();
            string dir = DirFor(), tmp = dir + ".tmp";
            try
            {
                Directory.CreateDirectory(Root);
                foreach (var old in Directory.GetDirectories(Root))
                    if (!old.Equals(dir, StringComparison.OrdinalIgnoreCase)) try { Directory.Delete(old, true); } catch { }   // (versiones viejas y restos)
                foreach (var old in Directory.GetFiles(Root, "Gente_*.pack"))
                    if (!old.Equals(CachedPack, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch { }
                Directory.CreateDirectory(tmp);
                var lines = new List<string> { "# prefijo velocidad(m/s) metros-por-ciclo anim=segundos… (Microsoft Rocketbox, licencia MIT; hecho por SelectOR desde Gente.pack)", "#max 16" };
                int people = 0, poses = 0;
                using (var fs = File.OpenRead(PackPath))
                {
                    var magic = new byte[9]; fs.Read(magic, 0, 9);
                    if (Encoding.ASCII.GetString(magic) != "SORGENTE1") throw new InvalidDataException("paquete desconocido");
                    using var z = new DeflateStream(fs, CompressionMode.Decompress);
                    using var r = new BinaryReader(z);
                    int n = r.ReadInt32();
                    for (int a = 0; a < n; a++)
                    {
                        string prefix = r.ReadString(); float speed = r.ReadSingle(), cycle = r.ReadSingle(); string anims = r.ReadString();
                        int nt = r.ReadInt32(); var images = new List<string>(); var alphas = new List<bool>();
                        for (int t = 0; t < nt; t++)
                        {
                            string name = r.ReadString(); bool alpha = r.ReadBoolean(); var data = r.ReadBytes(r.ReadInt32());
                            if (data.Length == 0) { images.Add(name); alphas.Add(alpha); continue; }   // (2026-11-04: la misma que en otra entrada, ya escrita)
                            using (var ms = new MemoryStream(data)) using (var img = new Bitmap(ms)) using (var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb))
                            {
                                using (var g = Graphics.FromImage(bmp)) { g.CompositingMode = CompositingMode.SourceCopy; g.DrawImage(img, 0, 0, img.Width, img.Height); }
                                WriteAce(Path.Combine(tmp, name), bmp, alpha);
                            }
                            images.Add(name); alphas.Add(alpha);
                        }
                        int nm = r.ReadInt32(); var meshes = new List<MeshData>();
                        for (int m = 0; m < nm; m++)
                        {
                            var md = new MeshData { Tex = r.ReadInt32(), Nv = r.ReadInt32(), Nb = r.ReadInt32() };
                            md.P = new float[md.Nv * 3]; md.N = new float[md.Nv * 3]; md.UV = new float[md.Nv * 2];
                            for (int i = 0; i < md.Nv; i++)
                            {
                                md.P[i * 3] = r.ReadSingle(); md.P[i * 3 + 1] = r.ReadSingle(); md.P[i * 3 + 2] = r.ReadSingle();
                                md.N[i * 3] = r.ReadSingle(); md.N[i * 3 + 1] = r.ReadSingle(); md.N[i * 3 + 2] = r.ReadSingle();
                                md.UV[i * 2] = r.ReadSingle(); md.UV[i * 2 + 1] = r.ReadSingle();
                            }
                            md.Wc = new byte[md.Nv]; var wb = new List<byte>(); var ww = new List<float>();
                            for (int i = 0; i < md.Nv; i++) { md.Wc[i] = r.ReadByte(); for (int k = 0; k < md.Wc[i]; k++) { wb.Add(r.ReadByte()); ww.Add(r.ReadSingle()); } }
                            md.Wb = wb.ToArray(); md.Ww = ww.ToArray();
                            int nf = r.ReadInt32(); md.F = new ushort[nf * 3];
                            for (int i = 0; i < nf * 3; i++) md.F[i] = r.ReadUInt16();
                            meshes.Add(md);
                        }
                        int na = r.ReadInt32();
                        for (int k = 0; k < na; k++)
                        {
                            string anim = r.ReadString(); int frames = r.ReadInt32();
                            for (int f = 0; f < frames; f++)
                            {
                                var parts = new List<(MeshData m, float[] pos, float[] nrm)>();
                                foreach (var md in meshes)
                                {
                                    var pal = new float[md.Nb * 12]; for (int i = 0; i < pal.Length; i++) pal[i] = r.ReadSingle();
                                    var (pos, nrm) = Skin(md, pal);
                                    parts.Add((md, pos, nrm));
                                }
                                WriteS(Path.Combine(tmp, $"{prefix}_{anim}_{f:00}.s"), parts, images, alphas);
                                poses++;
                            }
                        }
                        lines.Add(string.Format(CI, "{0} {1:0.##} {2:0.##} {3}", prefix, speed, cycle, anims));
                        people++;
                    }
                }
                File.WriteAllLines(Path.Combine(tmp, "gente.txt"), lines);
                File.WriteAllText(Path.Combine(tmp, "LICENCIA-Rocketbox.txt"), RocketboxLicense);
                File.WriteAllText(Path.Combine(tmp, "listo.txt"), DateTime.Now.ToString("s", CI));
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.Move(tmp, dir);
                if (PackPath == CachedPack) try { File.Delete(CachedPack); } catch { }   // (ya hechas: el paquete descargado sobra)
                log?.Invoke($"gente en los andenes: preparadas {people} personas ({poses} posturas) en {sw.Elapsed.TotalSeconds:0} s");
            }
            catch (Exception ex)
            {
                log?.Invoke("gente en los andenes: no se han podido preparar: " + ex.GetType().Name + " " + ex.Message);
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
        }

        // la malla de una postura: cada vértice, con sus huesos (como el conversor: suma de pesos; sin huesos, tal cual)
        static (float[] pos, float[] nrm) Skin(MeshData md, float[] pal)
        {
            var pos = new float[md.Nv * 3]; var nrm = new float[md.Nv * 3];
            int o = 0;
            for (int i = 0; i < md.Nv; i++)
            {
                float x = md.P[i * 3], y = md.P[i * 3 + 1], zz = md.P[i * 3 + 2], nx = md.N[i * 3], ny = md.N[i * 3 + 1], nz = md.N[i * 3 + 2];
                float px = 0, py = 0, pz = 0, qx = 0, qy = 0, qz = 0, ws = 0;
                for (int k = 0; k < md.Wc[i]; k++, o++)
                {
                    int b = md.Wb[o] * 12; float w = md.Ww[o];
                    px += (pal[b] * x + pal[b + 1] * y + pal[b + 2] * zz + pal[b + 3]) * w;
                    py += (pal[b + 4] * x + pal[b + 5] * y + pal[b + 6] * zz + pal[b + 7]) * w;
                    pz += (pal[b + 8] * x + pal[b + 9] * y + pal[b + 10] * zz + pal[b + 11]) * w;
                    qx += (pal[b] * nx + pal[b + 1] * ny + pal[b + 2] * nz) * w;
                    qy += (pal[b + 4] * nx + pal[b + 5] * ny + pal[b + 6] * nz) * w;
                    qz += (pal[b + 8] * nx + pal[b + 9] * ny + pal[b + 10] * nz) * w;
                    ws += w;
                }
                if (ws > 0) { px /= ws; py /= ws; pz /= ws; } else { px = x; py = y; pz = zz; }
                float l = (float)Math.Sqrt(qx * qx + qy * qy + qz * qz);
                if (l > 1e-6f) { qx /= l; qy /= l; qz /= l; } else { qx = 0; qy = 1; qz = 0; }
                pos[i * 3] = px; pos[i * 3 + 1] = py; pos[i * 3 + 2] = pz; nrm[i * 3] = qx; nrm[i * 3 + 1] = qy; nrm[i * 3 + 2] = qz;
            }
            return (pos, nrm);
        }

        // ---- el .s (texto; el mismo que el conversor) ----
        static string F(float v) => (Math.Abs(v) < 1e-5 ? 0 : v).ToString("0.####", CI);
        static void WriteS(string file, List<(MeshData m, float[] pos, float[] nrm)> parts, List<string> images, List<bool> alphas)
        {
            var sb = new StringBuilder(1 << 21);
            int nv = parts.Sum(q => q.m.Nv), nf = parts.Sum(q => q.m.F.Length / 3);
            float ymin = float.MaxValue, ymax = float.MinValue;
            foreach (var q in parts) for (int i = 0; i < q.m.Nv; i++) { float y = q.pos[i * 3 + 1]; if (y < ymin) ymin = y; if (y > ymax) ymax = y; }
            float cy = (ymin + ymax) / 2 * Scale;
            // (2026-11-04) el radio de verdad (los derrumbes miden hasta 20 m) y, si es grande, se ve desde más lejos
            double r2 = 0;
            foreach (var q in parts) for (int i = 0; i < q.m.Nv; i++) { double x = q.pos[i * 3] * Scale, y = q.pos[i * 3 + 1] * Scale - cy, z = q.pos[i * 3 + 2] * Scale; r2 = Math.Max(r2, x * x + y * y + z * z); }
            float radius = (float)Math.Max(1.2, Math.Sqrt(r2)); int sight = radius > 3 ? 1500 : 400;
            sb.Append("SIMISA@@@@@@@@@@JINX0s1t______\r\n\r\nshape (\r\n\tshape_header ( 00000000 00000000 )\r\n");
            sb.Append($"\tvolumes ( 1\r\n\t\tvol_sphere (\r\n\t\t\tvector ( 0 {F(cy)} 0 ) {F(radius)}\r\n\t\t)\r\n\t)\r\n");
            // (2026-11-04) las texturas «…_add» (llamas y luz del fuego): BlendATex, con su transparencia y sin que la noche las oscurezca
            // (AddATex se sumaba también a la niebla de Open Rails y se veía el rectángulo de cada llama)
            sb.Append("\tshader_names ( 2\r\n\t\tnamed_shader ( TexDiff )\r\n\t\tnamed_shader ( BlendATex )\r\n\t)\r\n");
            sb.Append("\ttexture_filter_names ( 1\r\n\t\tnamed_filter_mode ( MipLinear )\r\n\t)\r\n");
            sb.Append($"\tpoints ( {nv}\r\n");
            foreach (var q in parts) for (int i = 0; i < q.m.Nv; i++) sb.Append($"\t\tpoint ( {F(-q.pos[i * 3] * Scale)} {F(q.pos[i * 3 + 1] * Scale)} {F(q.pos[i * 3 + 2] * Scale)} )\r\n");   // (girada 180°: mira hacia −Z de XNA)
            sb.Append("\t)\r\n");
            sb.Append($"\tuv_points ( {nv}\r\n");
            foreach (var q in parts) for (int i = 0; i < q.m.Nv; i++) sb.Append($"\t\tuv_point ( {F(q.m.UV[i * 2])} {F(q.m.UV[i * 2 + 1])} )\r\n");
            sb.Append("\t)\r\n");
            sb.Append($"\tnormals ( {nv}\r\n");
            foreach (var q in parts) for (int i = 0; i < q.m.Nv; i++) sb.Append($"\t\tvector ( {F(-q.nrm[i * 3])} {F(q.nrm[i * 3 + 1])} {F(q.nrm[i * 3 + 2])} )\r\n");
            sb.Append("\t)\r\n\tsort_vectors ( 0 )\r\n\tcolours ( 0 )\r\n");
            sb.Append("\tmatrices ( 1\r\n\t\tmatrix MAIN ( 1 0 0 0 1 0 0 0 1 0 0 0 )\r\n\t)\r\n");
            sb.Append($"\timages ( {images.Count}\r\n"); foreach (var im in images) sb.Append($"\t\timage ( {im} )\r\n"); sb.Append("\t)\r\n");
            sb.Append($"\ttextures ( {images.Count}\r\n"); for (int i = 0; i < images.Count; i++) sb.Append($"\t\ttexture ( {i} 0 0 ff000000 )\r\n"); sb.Append("\t)\r\n");
            sb.Append("\tlight_materials ( 0 )\r\n\tlight_model_cfgs ( 1\r\n\t\tlight_model_cfg ( 00000000\r\n\t\t\tuv_ops ( 1\r\n\t\t\t\tuv_op_copy ( 1 0 )\r\n\t\t\t)\r\n\t\t)\r\n\t)\r\n");
            sb.Append("\tvtx_states ( 1\r\n\t\tvtx_state ( 00000000 0 -5 0 00000002 )\r\n\t)\r\n");
            sb.Append($"\tprim_states ( {parts.Count}\r\n");
            foreach (var q in parts) sb.Append($"\t\tprim_state ( 00000000 {(images[q.m.Tex].Contains("_add") ? 1 : 0)}\r\n\t\t\ttex_idxs ( 1 {q.m.Tex} ) 0 0 {(alphas[q.m.Tex] ? 1 : 0)} 0 1\r\n\t\t)\r\n");
            sb.Append("\t)\r\n");
            sb.Append("\tlod_controls ( 1\r\n\t\tlod_control (\r\n\t\t\tdistance_levels_header ( 0 )\r\n\t\t\tdistance_levels ( 1\r\n\t\t\t\tdistance_level (\r\n");
            sb.Append("\t\t\t\t\tdistance_level_header (\r\n\t\t\t\t\t\tdlevel_selection ( " + sight + " )\r\n\t\t\t\t\t\thierarchy ( 1 -1 )\r\n\t\t\t\t\t)\r\n");
            sb.Append("\t\t\t\t\tsub_objects ( 1\r\n\t\t\t\t\t\tsub_object (\r\n");
            sb.Append($"\t\t\t\t\t\t\tsub_object_header ( 00000400 -1 -1 000001d2 000001c4\r\n\t\t\t\t\t\t\t\tgeometry_info ( {nf} {parts.Count} 0 {nf * 3} 0 0 {parts.Count} 0 0 0\r\n");
            sb.Append($"\t\t\t\t\t\t\t\t\tgeometry_nodes ( 1\r\n\t\t\t\t\t\t\t\t\t\tgeometry_node ( 2 0 0 0 0\r\n\t\t\t\t\t\t\t\t\t\t\tcullable_prims ( {parts.Count} {nf} {nf * 3} )\r\n\t\t\t\t\t\t\t\t\t\t)\r\n\t\t\t\t\t\t\t\t\t)\r\n");
            sb.Append("\t\t\t\t\t\t\t\t\tgeometry_node_map ( 1 0 )\r\n\t\t\t\t\t\t\t\t)\r\n\t\t\t\t\t\t\t\tsubobject_shaders ( 1 0 )\r\n\t\t\t\t\t\t\t\tsubobject_light_cfgs ( 1 0 ) 0\r\n\t\t\t\t\t\t\t)\r\n");
            sb.Append($"\t\t\t\t\t\t\tvertices ( {nv}\r\n");
            for (int i = 0; i < nv; i++) sb.Append($"\t\t\t\t\t\t\t\tvertex ( 00000000 {i} {i} ffffffff ff000000\r\n\t\t\t\t\t\t\t\t\tvertex_uvs ( 1 {i} )\r\n\t\t\t\t\t\t\t\t)\r\n");
            sb.Append("\t\t\t\t\t\t\t)\r\n");
            sb.Append($"\t\t\t\t\t\t\tvertex_sets ( 1\r\n\t\t\t\t\t\t\t\tvertex_set ( 0 0 {nv} )\r\n\t\t\t\t\t\t\t)\r\n");
            sb.Append($"\t\t\t\t\t\t\tprimitives ( {parts.Count * 2}\r\n");
            int baseV = 0;
            for (int pi = 0; pi < parts.Count; pi++)
            {
                var q = parts[pi]; int f = q.m.F.Length / 3;
                sb.Append($"\t\t\t\t\t\t\t\tprim_state_idx ( {pi} )\r\n\t\t\t\t\t\t\t\tindexed_trilist (\r\n\t\t\t\t\t\t\t\t\tvertex_idxs ( {f * 3}");
                for (int t = 0; t < f; t++) sb.Append(' ').Append(baseV + q.m.F[t * 3]).Append(' ').Append(baseV + q.m.F[t * 3 + 2]).Append(' ').Append(baseV + q.m.F[t * 3 + 1]);   // (al revés: Open Rails invierte la Z)
                sb.Append(" )\r\n\t\t\t\t\t\t\t\t\tnormal_idxs ( ").Append(f);
                for (int t = 0; t < f; t++) sb.Append(' ').Append(baseV + q.m.F[t * 3]).Append(" 3");
                sb.Append(" )\r\n\t\t\t\t\t\t\t\t\tflags ( ").Append(f);
                for (int t = 0; t < f; t++) sb.Append(" 00000000");
                sb.Append(" )\r\n\t\t\t\t\t\t\t\t)\r\n");
                baseV += q.m.Nv;
            }
            sb.Append("\t\t\t\t\t\t\t)\r\n\t\t\t\t\t\t)\r\n\t\t\t\t\t)\r\n\t\t\t\t)\r\n\t\t\t)\r\n\t\t)\r\n\t)\r\n)\r\n");
            var all = Encoding.ASCII.GetBytes(sb.ToString());
            using var fs = File.Create(file);
            fs.Write(Encoding.ASCII.GetBytes("SIMISA@F"));
            fs.Write(BitConverter.GetBytes(all.Length - 16));
            fs.Write(Encoding.ASCII.GetBytes("@@@@"));
            using var zl = new ZLibStream(fs, CompressionLevel.Optimal);
            zl.Write(all, 16, all.Length - 16);   // (sin «SIMISA@@@@@@@@@@»)
        }

        // ---- texturas: .ace sin comprimir con mipmaps (como lo lee AceFile de Open Rails: canales R G B [A] por fila) ----
        static Bitmap Resize(Bitmap src, int size)
        {
            var dst = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            // (2026-11-04) en mosaico: si no, GDI+ mezcla los bordes con el vacío y las texturas que se repiten (la tierra de los
            // derrumbes) salen con rayas claras en las juntas al verlas de lejos
            using (var g = Graphics.FromImage(dst))
            using (var ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.Tile);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, size, size), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
            }
            return dst;
        }
        static void WriteAce(string path, Bitmap bmp, bool withAlpha)
        {
            int w = bmp.Width; int levels = 1 + (int)Math.Round(Math.Log(w, 2));
            using var fs = File.Create(path); using var wr = new BinaryWriter(fs);
            wr.Write(Encoding.ASCII.GetBytes("SIMISA@@@@@@@@@@"));
            wr.Write(1); wr.Write(1 /*mipmaps*/); wr.Write(w); wr.Write(w); wr.Write(withAlpha ? 0x12 : 0x0E); wr.Write(withAlpha ? 4 : 3);
            wr.Write(new byte[128]);
            var chans = withAlpha ? new[] { 3, 4, 5, 6 } : new[] { 3, 4, 5 };
            foreach (var c in chans) { wr.Write((ulong)8); wr.Write((ulong)c); }
            for (int l = 0; l < levels; l++) wr.Write(new byte[4 * (w >> l)]);   // (tabla de filas: Open Rails la salta)
            for (int l = 0; l < levels; l++)
            {
                int s = w >> l; var cur = l == 0 ? bmp : Resize(bmp, s);
                var d = cur.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var px = new byte[s * s * 4]; System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length); cur.UnlockBits(d);
                if (l > 0) cur.Dispose();
                var line = new byte[s];
                for (int y = 0; y < s; y++)
                    foreach (var c in chans)
                    {
                        int off = c == 3 ? 2 : c == 4 ? 1 : c == 5 ? 0 : 3;   // (BGRA en memoria)
                        for (int x = 0; x < s; x++) line[x] = px[(y * s + x) * 4 + off];
                        wr.Write(line);
                    }
            }
        }

        const string RocketboxLicense = @"Las personas de «Gente en los andenes» son de Microsoft Rocketbox Avatar Library
(https://github.com/microsoft/Microsoft-Rocketbox), adaptadas por SelectOR (posturas para Open Rails).

MIT License

Copyright (c) 2020 Microsoft

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the ""Software""), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
";
    }
}
