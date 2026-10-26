// (1.2.77) Teleindicadores hechos por el usuario, en su PC: a partir de un destino del tren (el «modelo»: su carpeta
// Destinos\X\, con el .ace del cartel), un destino nuevo con el texto que se escriba, en la misma carpeta Destinos del tren
// (Destinos\Nuevo\), así sale en el selector de Conducción libre y Horarios como los del autor (Teleindicadores.cs).
//  · El cartel del modelo es destinos.ace o el primer .ace de su carpeta; el resto de sus archivos (placas…) se copian.
//  · Dos clases de cartel, según el modelo:
//    LED (fondo oscuro, puntos encendidos): se miden sus filas y columnas de puntos (alto, ancho y paso de cada punto; en
//      la textura van comprimidos a lo ancho: el modelo 3D los estira) y se escribe el texto con una fuente de matriz de
//      5×7 (o, en matrices de 12 filas o más, con una letra normal pasada a puntos), del color de los puntos del modelo;
//    placa (fondo claro, letras oscuras): se buscan las líneas de texto del modelo, se borran con el color de la placa y
//      se escriben las nuevas en su sitio, del mismo alto, centradas, del color de las letras del modelo.
//  · Lo hecho con SelectOR lleva una marca (selector-tele.json, con su texto y su modelo) que NO se copia al tren: solo
//    eso se puede modificar o eliminar desde SelectOR; los destinos del autor no se tocan (sirven de modelo).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SelectOR
{
    public static class TeleEditor
    {
        public const string Marker = "selector-tele.json";
        public sealed class Spec { public List<string> lines { get; set; } = new(); public string model { get; set; } public string created { get; set; } }

        static string DestDir(string trainDir)
        {
            try { foreach (var d in Directory.GetDirectories(trainDir)) if (string.Equals(Path.GetFileName(d), "Destinos", StringComparison.OrdinalIgnoreCase)) return d; } catch { }
            return null;
        }

        // ¿Lo ha hecho SelectOR? (su marca, en alguna de las carpetas del tren)
        public static Spec SpecOf(List<string> folders, string name)
        {
            foreach (var f in folders)
            {
                var dd = DestDir(f); if (dd == null) continue;
                string m = Path.Combine(dd, name, Marker);
                if (!File.Exists(m)) continue;
                try { return JsonSerializer.Deserialize<Spec>(File.ReadAllText(m, Encoding.UTF8)); } catch { return new Spec(); }
            }
            return null;
        }

        // El cartel de una carpeta de destino: destinos.ace o el primer .ace
        public static string SampleAce(string destSub)
        {
            try
            {
                var aces = Directory.GetFiles(destSub, "*.ace");
                return aces.FirstOrDefault(x => string.Equals(Path.GetFileNameWithoutExtension(x), "destinos", StringComparison.OrdinalIgnoreCase)) ?? aces.OrderBy(x => x).FirstOrDefault();
            }
            catch { return null; }
        }

        // Nombre de carpeta válido para un destino («Atocha Cercanías» → «Atocha_Cercanías»)
        public static string FolderName(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            var s = new string((name ?? "").Trim().Select(c => bad.Contains(c) ? '_' : c == ' ' ? '_' : c).ToArray()).Trim('_', '.');
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }

        // ---------------------------------------------------------------- crear, modificar, eliminar
        // Crea (o rehace, si ya es de SelectOR) el destino «name» a partir del modelo, en cada carpeta del tren que tenga
        // ese modelo. Devuelve null si va bien, o el motivo.
        public static string Save(List<string> folders, string model, string name, List<string> lines, bool replace)
        {
            string folder = FolderName(name);
            if (folder.Length == 0) return I18n.T("Ponle un nombre al destino.");
            lines = (lines ?? new List<string>()).Select(l => (l ?? "").Trim()).ToList();
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count == 0) return I18n.T("Escribe el texto del cartel.");
            int done = 0;
            // (si está puesto en el tren, al acabar se vuelve a poner: con el texto nuevo)
            bool wasOn = replace && string.Equals(Teleindicadores.Installed(folders), folder, StringComparison.OrdinalIgnoreCase);
            try
            {
                foreach (var f in folders)
                {
                    var dd = DestDir(f); if (dd == null) continue;
                    string src = Path.Combine(dd, model), dst = Path.Combine(dd, folder);
                    if (!Directory.Exists(src)) continue;
                    if (Directory.Exists(dst) && !File.Exists(Path.Combine(dst, Marker)))
                        return string.Format(I18n.T("Ya hay un destino del autor que se llama «{0}»."), Teleindicadores.Nice(folder));
                    if (Directory.Exists(dst) && !replace) return string.Format(I18n.T("Ya hay un destino que se llama «{0}»."), Teleindicadores.Nice(folder));
                    string sample = SampleAce(src);
                    if (sample == null) return I18n.T("El modelo no tiene ningún .ace.");
                    using var bmp = AceImage.Load(sample, out int alphaBits);
                    if (bmp == null) return string.Format(I18n.T("No se puede leer {0}."), Path.GetFileName(sample));
                    using var outBmp = Render(bmp, lines);
                    // en una carpeta nueva y luego en su sitio (si algo falla, el destino de antes sigue entero)
                    string tmp = dst + ".selector-tmp";
                    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                    Directory.CreateDirectory(tmp);
                    foreach (var x in Directory.GetFiles(src))
                    {
                        string n = Path.GetFileName(x);
                        if (string.Equals(n, Marker, StringComparison.OrdinalIgnoreCase) || string.Equals(n, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        File.Copy(x, Path.Combine(tmp, n), true);
                    }
                    AceImage.Save(outBmp, Path.Combine(tmp, Path.GetFileName(sample)), alphaBits);
                    var spec = new Spec { lines = lines, model = model, created = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
                    File.WriteAllText(Path.Combine(tmp, Marker), JsonSerializer.Serialize(spec), new UTF8Encoding(false));
                    if (Directory.Exists(dst)) Directory.Delete(dst, true);
                    Directory.Move(tmp, dst);
                    done++;
                }
            }
            catch (Exception ex) { return ex.Message; }
            if (done > 0 && wasOn) { var aerr = Teleindicadores.Apply(folders, folder); if (aerr != null) return aerr; }
            return done > 0 ? null : I18n.T("Ninguna carpeta del tren tiene ese modelo.");
        }

        // Elimina un destino hecho con SelectOR (si está puesto en el tren, antes vuelve el original).
        public static string Delete(List<string> folders, string name)
        {
            if (SpecOf(folders, name) == null) return I18n.T("Solo se pueden eliminar los destinos hechos con SelectOR.");
            try
            {
                if (string.Equals(Teleindicadores.Installed(folders), name, StringComparison.OrdinalIgnoreCase))
                {
                    var err = Teleindicadores.Apply(folders, null);
                    if (err != null) return err;
                }
                foreach (var f in folders)
                {
                    var dd = DestDir(f); if (dd == null) continue;
                    string d = Path.Combine(dd, name);
                    if (Directory.Exists(d) && File.Exists(Path.Combine(d, Marker))) Directory.Delete(d, true);
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // Vista previa: el cartel del modelo con el texto (sin guardar nada). null si no se puede.
        public static Bitmap Preview(List<string> folders, string model, List<string> lines)
        {
            foreach (var f in folders)
            {
                var dd = DestDir(f); if (dd == null) continue;
                string src = Path.Combine(dd, model); if (!Directory.Exists(src)) continue;
                string sample = SampleAce(src); if (sample == null) continue;
                using var bmp = AceImage.Load(sample, out _);
                if (bmp == null) continue;
                return Render(bmp, (lines ?? new List<string>()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList());
            }
            return null;
        }

        // ---------------------------------------------------------------- dibujar
        public static bool IsLed(Bitmap b)
        {
            long sum = 0; int n = 0;
            ForEach(b, (x, y, c) => { sum += Lum(c); n++; });
            return n > 0 && sum / n < 85;
        }

        // (después de Render) ¿cabía todo el texto? (si no, se ha cortado)
        [ThreadStatic] public static bool LastFits;
        [ThreadStatic] public static string LastInfo;   // (pruebas) lo que se ha medido del modelo

        public static Bitmap Render(Bitmap model, List<string> lines)
        {
            LastFits = true;
            var px = Read(model);
            int w = model.Width, h = model.Height;
            bool led = IsLed(model);
            if (led) RenderLed(px, w, h, lines); else RenderPlate(px, w, h, lines);
            return Write(px, w, h);
        }

        static int Lum(int c) => (((c >> 16) & 255) * 299 + ((c >> 8) & 255) * 587 + (c & 255) * 114) / 1000;
        static int A(int c) => (c >> 24) & 255;
        static void ForEach(Bitmap b, Action<int, int, int> a) { var p = Read(b); for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++) a(x, y, p[y * b.Width + x]); }
        static int[] Read(Bitmap b)
        {
            var r = new int[b.Width * b.Height];
            var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { System.Runtime.InteropServices.Marshal.Copy(d.Scan0, r, 0, r.Length); } finally { b.UnlockBits(d); }
            return r;
        }
        static Bitmap Write(int[] px, int w, int h)
        {
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { System.Runtime.InteropServices.Marshal.Copy(px, 0, d.Scan0, px.Length); } finally { b.UnlockBits(d); }
            return b;
        }
        static int Rgb(int c, int alpha) => (alpha << 24) | (c & 0xFFFFFF);
        static int Mean(IEnumerable<int> cs)
        {
            long r = 0, g = 0, b = 0; int n = 0;
            foreach (var c in cs) { r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255; n++; }
            if (n == 0) return 0;
            return (int)(((r / n) << 16) | ((g / n) << 8) | (b / n));
        }
        static List<(int a, int b)> Bands(bool[] on, int gap)
        {
            var res = new List<(int, int)>(); int s = -1, last = -100;
            for (int i = 0; i < on.Length; i++)
            {
                if (!on[i]) continue;
                if (s < 0) s = i; else if (i - last > gap + 1) { res.Add((s, last)); s = i; }
                last = i;
            }
            if (s >= 0) res.Add((s, last));
            return res;
        }
        static int Median(List<int> v) { if (v.Count == 0) return 0; v.Sort(); return v[v.Count / 2]; }

        // ---- LED
        static void RenderLed(int[] px, int w, int h, List<string> lines)
        {
            bool Lit(int c) => Lum(c) > 120 && A(c) > 40;
            var rowOn = new bool[h]; var colOn = new bool[w];
            int x0 = w, x1 = -1, y0 = h, y1 = -1;
            var litC = new List<int>();
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int c = px[y * w + x]; if (!Lit(c)) continue;
                    rowOn[y] = colOn[x] = true; litC.Add(c);
                    if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
            if (x1 < 0) { x0 = w / 10; x1 = w * 9 / 10; y0 = h / 4; y1 = h * 3 / 4; }
            var rows = Bands(rowOn, 0); var cols = Bands(colOn, 0);
            // paso y tamaño de cada punto (en la textura, a lo ancho van comprimidos)
            int nRows = Math.Max(1, rows.Count);
            int dotH = rows.Count > 0 ? Median(rows.Select(r => r.b - r.a + 1).ToList()) : Math.Max(1, (y1 - y0 + 1) / 7);
            int pitchY = rows.Count > 1 ? Math.Max(dotH, (rows[^1].a - rows[0].a) / (rows.Count - 1)) : dotH + 1;
            // a lo ancho: el centro de cada punto encendido en el centro de cada fila; juntando los de todas las filas salen
            // las columnas de la matriz, y su paso (con decimales: al escalar la textura, los puntos salen de 2 y 3 píxeles)
            var runW = new List<int>(); var centers = new List<double>();
            foreach (var (ra, rb) in rows)
            {
                int y = (ra + rb) / 2, start = -1;
                for (int x = 0; x <= w; x++)
                {
                    bool l = x < w && Lit(px[y * w + x]);
                    if (l && start < 0) start = x;
                    else if (!l && start >= 0) { runW.Add(x - start); centers.Add((start + x - 1) / 2.0); start = -1; }
                }
            }
            centers.Sort();
            var colPos = new List<double>();
            double tol = Math.Max(1.5, (runW.Count > 0 ? Median(runW.ToList()) : 2) * 0.7);   // (dos centros más cerca que eso: el mismo punto)
            var grp = new List<List<double>>();
            foreach (var c in centers) { if (grp.Count > 0 && c - grp[^1].Average() <= tol) grp[^1].Add(c); else grp.Add(new List<double> { c }); }
            foreach (var g2 in grp) colPos.Add(g2.Average());
            // el paso entre puntos de una letra (los huecos pequeños entre columnas) y el de una letra a la siguiente: muchos
            // carteles van por celdas (5 columnas por letra y, entre letras, un hueco que no es una columna apagada entera)
            int dw0 = runW.Count > 0 ? Median(runW.ToList()) : 1;
            var diffs = new List<double>(); for (int i2 = 1; i2 < colPos.Count; i2++) diffs.Add(colPos[i2] - colPos[i2 - 1]);
            diffs.Sort();
            double pitchX, charGap;
            if (diffs.Count >= 4)
            {
                double q = diffs[diffs.Count / 4];
                var inner = diffs.Where(d => d <= q * 1.3).ToList();
                pitchX = inner.Count > 0 ? inner.Average() : q;
                var outer = diffs.Where(d => d > pitchX * 1.3 && d < pitchX * 2.6).ToList();
                charGap = outer.Count >= 2 ? outer.OrderBy(d => d).ElementAt(outer.Count / 2) : 2 * pitchX;
            }
            else { pitchX = dw0 + 1; charGap = 2 * pitchX; }
            int dotW = Math.Max(1, Math.Min(dw0, (int)Math.Floor(pitchX - 0.5)));
            if (colPos.Count > 0) { x0 = (int)Math.Round(colPos[0] - dotW / 2.0); x1 = (int)Math.Round(colPos[^1] + dotW / 2.0); }
            LastInfo = $"LED {nRows} filas · punto {dotW}×{dotH} px · paso {pitchX:0.##}×{pitchY} px · entre letras {charGap:0.##} px · panel {x0}-{x1} × {y0}-{y1}";
            int top = rows.Count > 0 ? rows[0].a : y0;
            // colores: los puntos encendidos (los más brillantes) y el fondo
            int on = Mean(litC.OrderByDescending(Lum).Take(Math.Max(1, litC.Count / 2)));
            var bgC = new List<int>();
            for (int y = Math.Max(0, y0); y <= Math.Min(h - 1, y1); y++) for (int x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++) { int c = px[y * w + x]; if (!Lit(c)) bgC.Add(c); }
            int bg = Mean(bgC);
            // borrar lo encendido del modelo (todo el panel, con su alfa)
            for (int y = Math.Max(0, y0 - 1); y <= Math.Min(h - 1, y1 + 1); y++)
                for (int x = Math.Max(0, x0 - 1); x <= Math.Min(w - 1, x1 + 1); x++) px[y * w + x] = Rgb(bg, A(px[y * w + x]));
            void Dot(double cx, int r)
            {
                int gx = (int)Math.Round(cx - dotW / 2.0), gy = top + r * pitchY;
                for (int y = gy; y < gy + dotH && y < h; y++) for (int x = gx; x < gx + dotW && x < w; x++) if (x >= 0 && y >= 0) px[y * w + x] = Rgb(on, A(px[y * w + x]) == 0 ? 255 : A(px[y * w + x]));
            }
            double c0 = x0 + dotW / 2.0, span = x1 - x0 - dotW;   // centros de la primera y la última columna
            var text = lines.Where(l => l.Length > 0).ToList();
            if (text.Count == 0) return;
            if (nRows >= 12)
            {
                // matrices grandes: una letra normal pasada a la cuadrícula
                int gridCols = Math.Max(5, (int)Math.Round(span / pitchX) + 1);
                var mask = new bool[nRows, gridCols];
                MaskFromFont(mask, nRows, gridCols, text);
                for (int r = 0; r < nRows; r++) for (int c = 0; c < gridCols; c++) if (mask[r, c]) Dot(c0 + c * pitchX, r);
                return;
            }
            // 5×7, por celdas; dos líneas solo si hay 16 filas o más
            if (nRows >= 16 && text.Count > 1)
            {
                int half = nRows / 2;
                Glyphs(text[0], Math.Max(0, (half - 7) / 2), c0, span, pitchX, charGap, Dot);
                Glyphs(text[1], half + Math.Max(0, (nRows - half - 7) / 2), c0, span, pitchX, charGap, Dot);
            }
            else Glyphs(string.Join("  ", text), Math.Max(0, (nRows - 7) / 2), c0, span, pitchX, charGap, Dot);
        }

        // una línea con la fuente 5×7, centrada; cada letra, 5 columnas a «pitch» y la siguiente a «gap» de su última
        // columna. Si no cabe, primero con las letras más juntas y luego cortada (LastFits = false)
        static void Glyphs(string s, int row0, double c0, double span, double pitch, double gap, Action<double, int> dot)
        {
            var glyphs = Normalize(s).Select(ch => Font5x7.TryGetValue(ch, out var g) ? g : Font5x7['?']).ToList();
            double Width(double gp) { double w = 0; for (int i = 0; i < glyphs.Count; i++) w += glyphs[i].Length == 0 ? (4 * pitch + gp) * 0.6 : 4 * pitch + (i < glyphs.Count - 1 ? gp : 0); return w; }
            if (Width(gap) > span) gap = Math.Max(pitch * 1.0, Math.Min(gap, pitch * 1.4));
            double width = Width(gap);
            if (width > span + 0.5) LastFits = false;
            double x = c0 + Math.Max(0, (span - width) / 2);
            foreach (var g in glyphs)
            {
                if (g.Length == 0) { x += (4 * pitch + gap) * 0.6; continue; }
                if (x + 4 * pitch > c0 + span + 0.5) break;   // (no cabe: se corta)
                for (int r = 0; r < 7; r++)
                    for (int k = 0; k < 5; k++)
                        if ((g[r] >> (4 - k) & 1) == 1) dot(x + k * pitch, row0 + r);
                x += 4 * pitch + gap;
            }
        }
        static string Normalize(string s)
        {
            var d = s.ToUpperInvariant().Replace("Ñ", "\u0001").Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var ch in d) if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(ch == '\u0001' ? 'Ñ' : ch);
            return sb.ToString();
        }
        // matrices grandes: una letra normal a la resolución de la matriz
        static void MaskFromFont(bool[,] mask, int rows, int cols, List<string> text)
        {
            int n = Math.Min(text.Count, rows >= 24 ? 2 : 1);
            using var b = new Bitmap(cols, rows);
            using var g = Graphics.FromImage(b);
            g.Clear(Color.Black); g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            int lineH = rows / n;
            for (int i = 0; i < n; i++)
            {
                string t = n == 1 ? string.Join("  ", text) : text[i];
                float size = lineH * 0.85f;
                using var f = new Font("Arial", size, FontStyle.Bold, GraphicsUnit.Pixel);
                var sz = g.MeasureString(t, f);
                float k = sz.Width > cols ? cols / sz.Width : 1;
                var st = g.Save();
                g.TranslateTransform((cols - sz.Width * k) / 2, i * lineH + (lineH - sz.Height) / 2);
                g.ScaleTransform(k, 1);
                g.DrawString(t, f, Brushes.White, 0, 0);
                g.Restore(st);
            }
            for (int y = 0; y < rows; y++) for (int x = 0; x < cols; x++) mask[y, x] = b.GetPixel(x, y).R > 127;
        }

        // ---- placa
        static void RenderPlate(int[] px, int w, int h, List<string> lines)
        {
            // la placa: lo claro; las letras: lo bastante más oscuro que ella, dentro de la placa (sin los bordes, asas, tornillos)
            var light = new List<int>(); int px0 = w, px1 = -1, py0 = h, py1 = -1;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int c = px[y * w + x]; if (Lum(c) <= 140 || A(c) < 128) continue;
                    light.Add(Lum(c)); if (x < px0) px0 = x; if (x > px1) px1 = x; if (y < py0) py0 = y; if (y > py1) py1 = y;
                }
            if (px1 < 0) { px0 = 0; px1 = w - 1; py0 = 0; py1 = h - 1; }
            int paper = Median(light.ToList());
            int thr = paper * 55 / 100;
            int ix0 = px0 + (px1 - px0) * 12 / 100, ix1 = px1 - (px1 - px0) * 12 / 100, iy0 = py0 + (py1 - py0) * 4 / 100, iy1 = py1 - (py1 - py0) * 4 / 100;
            bool Ink(int x, int y) { int c = px[y * w + x]; return Lum(c) < thr && A(c) >= 128; }
            var rowOn = new bool[h];
            for (int y = iy0; y <= iy1; y++) { int n = 0; for (int x = ix0; x <= ix1; x++) if (Ink(x, y)) n++; rowOn[y] = n >= 2; }
            var bands = Bands(rowOn, 2).Where(b => b.b - b.a + 1 >= 4 && b.b - b.a + 1 < (iy1 - iy0) * 6 / 10).ToList();
            var slots = new List<Rectangle>(); var inkC = new List<int>();
            foreach (var (a, b) in bands)
            {
                int sx0 = w, sx1 = -1;
                for (int y = a; y <= b; y++) for (int x = ix0; x <= ix1; x++) if (Ink(x, y)) { if (x < sx0) sx0 = x; if (x > sx1) sx1 = x; inkC.Add(px[y * w + x]); }
                if (sx1 >= sx0) slots.Add(Rectangle.FromLTRB(sx0, a, sx1 + 1, b + 1));
            }
            if (slots.Count == 0) slots.Add(new Rectangle(ix0, (iy0 + iy1) / 2 - (iy1 - iy0) / 8, ix1 - ix0, (iy1 - iy0) / 4));
            int ink = inkC.Count > 0 ? Mean(inkC.OrderBy(Lum).Take(Math.Max(1, inkC.Count / 2))) : 0x202020;
            // borrar las letras del modelo con el color de la placa de alrededor
            foreach (var s in slots)
            {
                var r = Rectangle.Inflate(s, 3, 3);
                var around = new List<int>();
                for (int x = Math.Max(0, r.Left); x < Math.Min(w, r.Right); x++)
                    foreach (int y in new[] { r.Top - 2, r.Bottom + 1 }) if (y >= 0 && y < h && Lum(px[y * w + x]) > thr) around.Add(px[y * w + x]);
                int pc = around.Count > 0 ? Mean(around) : (paper << 16) | (paper << 8) | paper;
                for (int y = Math.Max(0, r.Top); y < Math.Min(h, r.Bottom); y++) for (int x = Math.Max(0, r.Left); x < Math.Min(w, r.Right); x++) px[y * w + x] = Rgb(pc, A(px[y * w + x]));
            }
            // las líneas nuevas en los huecos de las del modelo (si hay más líneas que huecos, el último se reparte)
            var text = lines.Where(l => l.Length > 0).ToList();
            if (text.Count == 0) return;
            var use = new List<(Rectangle r, string t)>();
            for (int i = 0; i < text.Count; i++)
            {
                if (i < slots.Count) use.Add((slots[i], text[i]));
                else { var last = use[^1]; use[^1] = (last.r, last.t + " " + text[i]); }
            }
            using var bmp = Write(px, w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.SmoothingMode = SmoothingMode.AntiAlias;
                string family = FontExists("Bahnschrift") ? "Bahnschrift" : FontExists("Arial Narrow") ? "Arial Narrow" : "Arial";
                using var brush = new SolidBrush(Color.FromArgb(255, Color.FromArgb(ink)));
                float cx = (px0 + px1) / 2f, maxW = (ix1 - ix0) * 1.05f;
                foreach (var (r, t) in use)
                {
                    // alto de las mayúsculas = alto del hueco
                    using var probe = new Font(family, 100, FontStyle.Bold, GraphicsUnit.Pixel);
                    float cap = CapHeight(g, probe);
                    float size = 100 * r.Height / Math.Max(1f, cap);
                    using var f = new Font(family, size, FontStyle.Bold, GraphicsUnit.Pixel);
                    var sz = g.MeasureString(t, f, int.MaxValue, StringFormat.GenericTypographic);
                    float k = sz.Width > maxW ? maxW / sz.Width : 1;
                    float capTop = CapTop(g, f);
                    var st = g.Save();
                    g.TranslateTransform(cx - sz.Width * k / 2, r.Top - capTop);
                    g.ScaleTransform(k, 1);
                    g.DrawString(t, f, brush, 0, 0, StringFormat.GenericTypographic);
                    g.Restore(st);
                }
            }
            var outp = Read(bmp);
            for (int i = 0; i < px.Length; i++) px[i] = Rgb(outp[i], A(px[i]));   // (el alfa, el del modelo)
        }
        static bool FontExists(string name) { using var f = new Font(name, 10); return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase); }
        // alto de «H» y dónde empieza, en píxeles (medido dibujándola)
        static (float top, float h) HMetrics(Graphics g0, Font f)
        {
            int s = (int)Math.Ceiling(f.Size * 2) + 4;
            using var b = new Bitmap(s, s); using var g = Graphics.FromImage(b);
            g.Clear(Color.Black); g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            g.DrawString("H", f, Brushes.White, 0, 0, StringFormat.GenericTypographic);
            int t = -1, bt = -1;
            for (int y = 0; y < s; y++) for (int x = 0; x < s; x++) if (b.GetPixel(x, y).R > 127) { if (t < 0) t = y; bt = y; break; }
            return t < 0 ? (0, f.Size * 0.7f) : (t, bt - t + 1);
        }
        static float CapHeight(Graphics g, Font f) => HMetrics(g, f).h;
        static float CapTop(Graphics g, Font f) => HMetrics(g, f).top;

        // ---- fuente de matriz 5×7 (cada fila, 5 bits: el de la izquierda, el más alto)
        static readonly Dictionary<char, byte[]> Font5x7 = new()
        {
            [' '] = Array.Empty<byte>(),
            ['A'] = B("01110,10001,10001,11111,10001,10001,10001"), ['B'] = B("11110,10001,10001,11110,10001,10001,11110"),
            ['C'] = B("01110,10001,10000,10000,10000,10001,01110"), ['D'] = B("11110,10001,10001,10001,10001,10001,11110"),
            ['E'] = B("11111,10000,10000,11110,10000,10000,11111"), ['F'] = B("11111,10000,10000,11110,10000,10000,10000"),
            ['G'] = B("01110,10001,10000,10111,10001,10001,01111"), ['H'] = B("10001,10001,10001,11111,10001,10001,10001"),
            ['I'] = B("01110,00100,00100,00100,00100,00100,01110"), ['J'] = B("00111,00010,00010,00010,00010,10010,01100"),
            ['K'] = B("10001,10010,10100,11000,10100,10010,10001"), ['L'] = B("10000,10000,10000,10000,10000,10000,11111"),
            ['M'] = B("10001,11011,10101,10101,10001,10001,10001"), ['N'] = B("10001,10001,11001,10101,10011,10001,10001"),
            ['O'] = B("01110,10001,10001,10001,10001,10001,01110"), ['P'] = B("11110,10001,10001,11110,10000,10000,10000"),
            ['Q'] = B("01110,10001,10001,10001,10101,10010,01101"), ['R'] = B("11110,10001,10001,11110,10100,10010,10001"),
            ['S'] = B("01111,10000,10000,01110,00001,00001,11110"), ['T'] = B("11111,00100,00100,00100,00100,00100,00100"),
            ['U'] = B("10001,10001,10001,10001,10001,10001,01110"), ['V'] = B("10001,10001,10001,10001,10001,01010,00100"),
            ['W'] = B("10001,10001,10001,10101,10101,10101,01010"), ['X'] = B("10001,10001,01010,00100,01010,10001,10001"),
            ['Y'] = B("10001,10001,10001,01010,00100,00100,00100"), ['Z'] = B("11111,00001,00010,00100,01000,10000,11111"),
            ['Ñ'] = B("01110,00000,10001,11001,10101,10011,10001"), ['Ç'] = B("01110,10001,10000,10000,10001,01110,00100"),
            ['0'] = B("01110,10001,10011,10101,11001,10001,01110"), ['1'] = B("00100,01100,00100,00100,00100,00100,01110"),
            ['2'] = B("01110,10001,00001,00010,00100,01000,11111"), ['3'] = B("11111,00010,00100,00010,00001,10001,01110"),
            ['4'] = B("00010,00110,01010,10010,11111,00010,00010"), ['5'] = B("11111,10000,11110,00001,00001,10001,01110"),
            ['6'] = B("00110,01000,10000,11110,10001,10001,01110"), ['7'] = B("11111,00001,00010,00100,01000,01000,01000"),
            ['8'] = B("01110,10001,10001,01110,10001,10001,01110"), ['9'] = B("01110,10001,10001,01111,00001,00010,01100"),
            ['-'] = B("00000,00000,00000,11111,00000,00000,00000"), ['.'] = B("00000,00000,00000,00000,00000,01100,01100"),
            [','] = B("00000,00000,00000,00000,01100,00100,01000"), ['/'] = B("00000,00001,00010,00100,01000,10000,00000"),
            ['\''] = B("01100,00100,01000,00000,00000,00000,00000"), ['('] = B("00010,00100,01000,01000,01000,00100,00010"),
            [')'] = B("01000,00100,00010,00010,00010,00100,01000"), [':'] = B("00000,01100,01100,00000,01100,01100,00000"),
            ['?'] = B("01110,10001,00001,00010,00100,00000,00100"), ['!'] = B("00100,00100,00100,00100,00100,00000,00100"),
            ['+'] = B("00000,00100,00100,11111,00100,00100,00000"), ['&'] = B("01100,10010,10100,01000,10101,10010,01101"),
        };
        static byte[] B(string s) => s.Split(',').Select(r => Convert.ToByte(r, 2)).ToArray();
    }
}
