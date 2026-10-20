// Mejora gráfica con ReShade (Ajustes de SelectOR → Gráficos). ReShade (https://reshade.me, © Patrick Mours, licencia
// BSD; ReShade\LICENCIA-ReShade.txt) va dentro de SelectOR.dll con el efecto propio de SelectOR (ReShade\SelectOR.fx:
// nitidez y color) y, de SweetFX (MIT), el suavizado de bordes SMAA y Vibrance (la viveza del color):
//  · al lanzar Open Rails desde SelectOR con los gráficos activados, se pone junto a RunActivity: ReShade64.dll como
//    dxgi.dll (Open Rails dibuja con DirectX 11 y en 64 bits), ReShade.ini (rutas, teclas: Inicio = su menú, Bloq Despl
//    = efectos sí/no; sin la tecla de captura, que es la de Open Rails) y SelectOR-ReShade\ (el efecto y el preajuste
//    con los valores elegidos);
//  · al volver (y al abrir SelectOR) se quita el dxgi.dll: así nada más lo carga (p. ej. RunActivity32, de 32 bits, no
//    arrancaría con él). Un dxgi.dll que no es el nuestro (otro ReShade del usuario, otro programa) no se toca nunca.
//  · en CYZYRails (OrFlavor), que trae su propio posproceso, nunca.

using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SelectOR
{
    public static class ReShadeFiles
    {
        public const string Version = "6.8.0";
        const string Folder = "SelectOR-ReShade";
        public static string OrDir = AppContext.BaseDirectory;   // (las pruebas ponen otra)
        static string Dxgi => Path.Combine(OrDir, "dxgi.dll");

        static byte[] _dll;
        static byte[] Res(string name)
        {
            using var s = typeof(ReShadeFiles).Assembly.GetManifestResourceStream(name);
            if (s == null) return null;
            using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
        }
        static byte[] Dll => _dll ??= Res("reshade.dxgi.dll");

        static bool SameAsOurs(string path)
        {
            try
            {
                var have = File.ReadAllBytes(path); var want = Dll;
                return want != null && have.Length == want.Length && SHA256.HashData(have).AsSpan().SequenceEqual(SHA256.HashData(want));
            }
            catch { return false; }
        }

        // ¿Hay junto a Open Rails un dxgi.dll que no es el de SelectOR? (entonces no se pone el nuestro)
        public static bool Foreign => File.Exists(Dxgi) && !SameAsOurs(Dxgi);

        // Al lanzar: con los gráficos activados, todo en su sitio. Devuelve por qué no (o null).
        public static string Install(AppPrefs p)
        {
            try
            {
                if (p == null || !p.GfxEnabled) { Remove(); return null; }
                // CYZYRails ya trae bloom, exposición y corrección de color: ReShade encima los duplicaría; con él, nunca
                if (OrFlavor.IsCyzy(OrDir)) { Remove(); return "CYZYRails trae su propio posproceso"; }
                if (Dll == null) return "ReShade no está dentro de SelectOR";
                if (Foreign) return "ya hay otro dxgi.dll junto a Open Rails";
                string dir = Path.Combine(OrDir, Folder), sh = Path.Combine(dir, "Shaders");
                Directory.CreateDirectory(sh); Directory.CreateDirectory(Path.Combine(dir, "Textures"));
                // los efectos y sus texturas (todo lo que va dentro: reshade.shaders.* y reshade.textures.*)
                foreach (var name in typeof(ReShadeFiles).Assembly.GetManifestResourceNames())
                {
                    if (name.StartsWith("reshade.shaders.", StringComparison.Ordinal)) WriteIfChanged(Path.Combine(sh, name.Substring(16)), Res(name));
                    else if (name.StartsWith("reshade.textures.", StringComparison.Ordinal)) WriteIfChanged(Path.Combine(dir, "Textures", name.Substring(17)), Res(name));
                }
                WriteIfChanged(Path.Combine(dir, "LICENCIA-ReShade.txt"), Res("reshade.licencia.txt"));
                // ReShade.ini: si había uno de otro ReShade, se guarda una copia la primera vez
                string ini = Path.Combine(OrDir, "ReShade.ini");
                if (File.Exists(ini) && !File.ReadAllText(ini).Contains(Folder) && !File.Exists(ini + ".antes-de-SelectOR"))
                    File.Copy(ini, ini + ".antes-de-SelectOR");
                File.WriteAllText(ini, IniText(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dir, "SelectOR.ini"), PresetText(p), new UTF8Encoding(false));
                if (!File.Exists(Dxgi)) { string tmp = Dxgi + ".new"; File.WriteAllBytes(tmp, Dll); File.Move(tmp, Dxgi, true); }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // Al volver de conducir y al abrir SelectOR: fuera nuestro dxgi.dll (si Open Rails aún lo tiene abierto, ya se quitará).
        public static void Remove()
        {
            try { if (File.Exists(Dxgi) && SameAsOurs(Dxgi)) File.Delete(Dxgi); } catch { }
        }

        static void WriteIfChanged(string path, byte[] data)
        {
            if (data == null) return;
            if (File.Exists(path)) { var have = File.ReadAllBytes(path); if (have.AsSpan().SequenceEqual(data)) return; }
            File.WriteAllBytes(path, data);
        }

        static string IniText() => string.Join("\r\n", new[]
        {
            "[GENERAL]",
            $@"EffectSearchPaths=.\{Folder}\Shaders",
            $@"TextureSearchPaths=.\{Folder}\Textures",
            $@"PresetPath=.\{Folder}\SelectOR.ini",
            "CheckForUpdates=0",
            "PerformanceMode=0",
            "",
            "[INPUT]",
            "KeyOverlay=36,0,0,0",        // Inicio: el menú de ReShade
            "KeyEffects=145,0,0,0",       // Bloq Despl: efectos sí/no
            "KeyScreenshot=0,0,0,0",      // (Impr Pant es la captura de Open Rails)
            "KeyReload=0,0,0,0",
            "KeyNextPreset=0,0,0,0",
            "KeyPreviousPreset=0,0,0,0",
            "",
            "[OVERLAY]",
            "TutorialProgress=4",
            "ShowFPS=0",
            "ShowClock=0",
            ""
        });

        static string F(double v) => v.ToString("0.000000", CultureInfo.InvariantCulture);

        // El preajuste de ReShade con los valores elegidos en SelectOR: primero SMAA (sobre la imagen tal cual), luego
        // Vibrance y al final la nitidez y el color de SelectOR. Los que no se usan, fuera de «Techniques».
        public static string PresetText(AppPrefs p)
        {
            var on = new System.Collections.Generic.List<string>();
            if (p.GfxEdgeAA) on.Add("SMAA@SMAA.fx");
            if (Math.Abs(p.GfxVibrance) > 0.001) on.Add("Vibrance@Vibrance.fx");
            on.Add("SelectOR@SelectOR.fx");
            return string.Join("\r\n", new[]
            {
                "Techniques=" + string.Join(",", on),
                "TechniqueSorting=SMAA@SMAA.fx,Vibrance@Vibrance.fx,SelectOR@SelectOR.fx",
                "",
                "[SelectOR.fx]",
                "Brightness=" + F(p.GfxBrightness),
                "Contrast=" + F(p.GfxContrast),
                "Saturation=" + F(p.GfxSaturation),
                "Sharpen=" + F(p.GfxSharpen),
                "Vignette=" + F(p.GfxVignette),
                "Warmth=" + F(p.GfxWarmth),
                "",
                "[SMAA.fx]",
                "EdgeDetectionType=1",      // por el color (la profundidad de Open Rails no siempre está)
                "",
                "[Vibrance.fx]",
                "Vibrance=" + F(p.GfxVibrance),
                "VibranceRGBBalance=1.000000,1.000000,1.000000",
                ""
            });
        }

        // ---- preajustes de SelectOR (nitidez, saturación, contraste, brillo, calidez, viñeta) ----
        public static readonly (string id, string label, double sharpen, double sat, double con, double bri, double warm, double vig, double vib)[] Presets =
        {
            // SelectOR: el perfil propio, ajustado sobre la imagen de ejemplo (ReShade\ejemplo.jpg): realza los colores
            // apagados (viveza) y levanta un poco la luz en vez de subir el contraste, para no aplastar la vegetación
            // oscura; un punto de calidez y nitidez para la catenaria y los carriles. Medido: saturación +27 %, luz +4 %,
            // vegetación más verde y nada más oscura, menos del 3 % de la imagen recortada.
            ("selector", "SelectOR", 0.55, 0.00, 0.04, 0.07, 0.06, 0.10, 0.25),
            ("natural", "Natural", 0.35, 0.00, 0.05, 0.00, 0.00, 0.15, 0.15),
            ("nitido", "Nítido", 0.75, 0.05, 0.08, 0.00, 0.00, 0.10, 0.20),
            ("calido", "Cálido de tarde", 0.35, 0.05, 0.08, 0.02, 0.35, 0.25, 0.25),
            ("cine", "Cinematográfico", 0.30, -0.10, 0.18, -0.03, 0.10, 0.45, 0.00),
        };

        public static void ApplyPreset(AppPrefs p, string id)
        {
            foreach (var x in Presets)
                if (x.id == id) { p.GfxPreset = id; p.GfxSharpen = x.sharpen; p.GfxSaturation = x.sat; p.GfxContrast = x.con; p.GfxBrightness = x.bri; p.GfxWarmth = x.warm; p.GfxVignette = x.vig; p.GfxVibrance = x.vib; return; }
        }

        // ---- vista previa (en SelectOR): lo mismo que hacen Vibrance y SelectOR.fx con el color, sin el suavizado ----
        public static System.Drawing.Bitmap Preview(System.Drawing.Bitmap src, AppPrefs p)
        {
            var b = new System.Drawing.Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var r = new System.Drawing.Rectangle(0, 0, src.Width, src.Height);
            using (var g = System.Drawing.Graphics.FromImage(b)) g.DrawImage(src, r);
            var d = b.LockBits(r, System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            int n = d.Stride * d.Height; var px = new byte[n];
            System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, n);
            var orig = (byte[])px.Clone();
            int w = src.Width, h = src.Height, st = d.Stride;
            double aspect = (double)w / h;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * st + x * 4;
                    double cb = orig[i] / 255.0, cg = orig[i + 1] / 255.0, cr = orig[i + 2] / 255.0;
                    if (p.GfxSharpen > 0.001 && x > 0 && y > 0 && x < w - 1 && y < h - 1)
                    {
                        double Sh(int o)
                        {
                            double c = orig[i + o] / 255.0, nN = orig[i - st + o] / 255.0, nS = orig[i + st + o] / 255.0, nE = orig[i + 4 + o] / 255.0, nW = orig[i - 4 + o] / 255.0;
                            double mn = Math.Min(c, Math.Min(Math.Min(nN, nS), Math.Min(nE, nW))), mx = Math.Max(c, Math.Max(Math.Max(nN, nS), Math.Max(nE, nW)));
                            double amp = Math.Sqrt(Math.Clamp(Math.Min(mn, 1 - mx) / Math.Max(mx, 1e-5), 0, 1)), wgt = -amp * 0.2;
                            double sharp = Math.Clamp(((nN + nS + nE + nW) * wgt + c) / (1 + 4 * wgt), 0, 1);
                            return c + (sharp - c) * p.GfxSharpen;
                        }
                        cb = Sh(0); cg = Sh(1); cr = Sh(2);
                    }
                    if (Math.Abs(p.GfxVibrance) > 0.001)
                    {
                        double lv = 0.212656 * cr + 0.715158 * cg + 0.072186 * cb;
                        double sat = Math.Max(cr, Math.Max(cg, cb)) - Math.Min(cr, Math.Min(cg, cb));
                        double f = 1 + p.GfxVibrance * (1 - Math.Sign(p.GfxVibrance) * sat);
                        cr = lv + (cr - lv) * f; cg = lv + (cg - lv) * f; cb = lv + (cb - lv) * f;
                    }
                    double add = p.GfxBrightness * 0.25, k = 1 + p.GfxContrast;
                    cr = (cr + add - 0.5) * k + 0.5; cg = (cg + add - 0.5) * k + 0.5; cb = (cb + add - 0.5) * k + 0.5;
                    double l = cr * 0.299 + cg * 0.587 + cb * 0.114, s = 1 + p.GfxSaturation;
                    cr = l + (cr - l) * s; cg = l + (cg - l) * s; cb = l + (cb - l) * s;
                    cr += 0.06 * p.GfxWarmth; cg += 0.015 * p.GfxWarmth; cb -= 0.06 * p.GfxWarmth;
                    if (p.GfxVignette > 0.001)
                    {
                        double dx = ((x + 0.5) / w - 0.5) * aspect, dy = (y + 0.5) / h - 0.5, len = Math.Sqrt(dx * dx + dy * dy);
                        double t = Math.Clamp((len - 0.25) / 0.70, 0, 1), sm = t * t * (3 - 2 * t), v = 1 - p.GfxVignette * 0.55 * sm;
                        cr *= v; cg *= v; cb *= v;
                    }
                    px[i] = (byte)(Math.Clamp(cb, 0, 1) * 255 + 0.5); px[i + 1] = (byte)(Math.Clamp(cg, 0, 1) * 255 + 0.5); px[i + 2] = (byte)(Math.Clamp(cr, 0, 1) * 255 + 0.5);
                }
            System.Runtime.InteropServices.Marshal.Copy(px, 0, d.Scan0, n);
            b.UnlockBits(d);
            return b;
        }
    }
}
