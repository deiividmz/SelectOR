// Decodificador minimo de texturas MSTS ".ace" a System.Drawing.Bitmap, sin GPU.
// Basado en el formato leido por Orts.Formats.Msts.AceFile (que produce Texture2D).
// Soporta: cabecera comprimida (SIMISA@F / zlib-deflate) y sin comprimir (SIMISA@@),
// datos estructurados (canales 1/8 bpp) y datos "raw" (DXT1/3/5, Bgr565, Bgra5551, Bgra4444).
// Solo se decodifica el nivel de mipmap 0.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;

namespace SelectOR
{
    public static class AceImage
    {
        // Opciones de cabecera
        const int OPT_MIPMAPS = 0x01;
        const int OPT_RAWDATA = 0x10;

        public static Bitmap Load(string fileName) => Load(fileName, out _);

        /// <summary>Decodifica el .ace y además indica los bits de alfa (8 = canal alfa, 1 = máscara, 0 = ninguno),
        /// igual que AceInfo.AlphaBits de Open Rails, para decidir el modo de mezcla correcto.</summary>
        public static Bitmap Load(string fileName, out int alphaBits)
        {
            alphaBits = 0;
            try
            {
                using (var fs = File.OpenRead(fileName))
                using (var br0 = new BinaryReader(fs))
                {
                    var sig = new string(br0.ReadChars(8));
                    if (sig == "SIMISA@F")
                    {
                        br0.ReadUInt32();                 // tamano sin comprimir
                        var at = new string(br0.ReadChars(4));
                        if (at != "@@@@") return null;
                        var zlib = br0.ReadUInt16();      // cabecera zlib (0x78 0x9C)
                        if ((zlib & 0x20FF) != 0x0078) return null;
                        using (var def = new DeflateStream(fs, CompressionMode.Decompress))
                        using (var ms = new MemoryStream())
                        {
                            def.CopyTo(ms);
                            ms.Position = 0;
                            using (var br = new BinaryReader(ms))
                                return Decode(br, out alphaBits);
                        }
                    }
                    if (sig == "SIMISA@@")
                    {
                        var at = new string(br0.ReadChars(8));
                        if (at != "@@@@@@@@") return null;
                        return Decode(br0, out alphaBits);
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Decodifica un DDS (DirectDraw Surface) a Bitmap (mip 0). Muchos modelos de Open Rails
        /// usan .dds (DXT1/3/5 o sin comprimir 32/24-bit) aunque el .s referencie el nombre como .ace.</summary>
        public static Bitmap LoadDds(string fileName, out int alphaBits)
        {
            alphaBits = 0;
            try
            {
                var d = File.ReadAllBytes(fileName);
                if (d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || d[3] != ' ') return null;
                int height = BitConverter.ToInt32(d, 12);
                int width = BitConverter.ToInt32(d, 16);
                if (width <= 0 || height <= 0 || width > 8192 || height > 8192) return null;
                int pfFlags = BitConverter.ToInt32(d, 80);
                string fourcc = System.Text.Encoding.ASCII.GetString(d, 84, 4);
                int rgbBits = BitConverter.ToInt32(d, 88);
                uint rMask = BitConverter.ToUInt32(d, 92), gMask = BitConverter.ToUInt32(d, 96),
                     bMask = BitConverter.ToUInt32(d, 100), aMask = BitConverter.ToUInt32(d, 104);
                int off = 128;
                int[] argb;
                bool fourccFlag = (pfFlags & 0x4) != 0;
                if (fourccFlag && (fourcc == "DXT1" || fourcc == "DXT3" || fourcc == "DXT5"))
                {
                    int variant = fourcc == "DXT1" ? 1 : fourcc == "DXT3" ? 3 : 5;
                    int block = variant == 1 ? 8 : 16;
                    int mip0 = ((width + 3) / 4) * ((height + 3) / 4) * block;
                    if (off + mip0 > d.Length) mip0 = d.Length - off;
                    var buf = new byte[mip0];
                    Array.Copy(d, off, buf, 0, mip0);
                    argb = DecodeDxt(buf, width, height, variant);
                    alphaBits = variant == 1 ? 1 : 8;
                }
                else if ((pfFlags & 0x40) != 0 && (rgbBits == 32 || rgbBits == 24))
                {
                    // sin comprimir RGB(A): decodificar según máscaras
                    int bpp = rgbBits / 8;
                    argb = new int[width * height];
                    int rs = MaskShift(rMask), gs = MaskShift(gMask), bs = MaskShift(bMask), as_ = MaskShift(aMask);
                    bool hasA = aMask != 0 && rgbBits == 32;
                    for (int i = 0; i < width * height; i++)
                    {
                        int p = off + i * bpp;
                        if (p + bpp > d.Length) break;
                        uint v = bpp == 4 ? BitConverter.ToUInt32(d, p) : (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16));
                        byte rr = (byte)((v & rMask) >> rs), gg = (byte)((v & gMask) >> gs), bb = (byte)((v & bMask) >> bs);
                        byte aa = hasA ? (byte)((v & aMask) >> as_) : (byte)0xFF;
                        argb[i] = (aa << 24) | (rr << 16) | (gg << 8) | bb;
                    }
                    alphaBits = hasA ? 8 : 0;
                }
                else return null;   // DX10/otros formatos no soportados
                if (argb == null) return null;
                return BuildBitmap(argb, width, height);
            }
            catch { return null; }
        }

        static int MaskShift(uint mask)
        {
            if (mask == 0) return 0;
            int s = 0; while ((mask & 1) == 0) { mask >>= 1; s++; }
            return s;
        }

        static Bitmap Decode(BinaryReader r, out int alphaBits)
        {
            alphaBits = 0;
            var b0 = r.ReadBytes(4); // 01 00 00 00
            if (b0.Length < 4 || b0[0] != 1) return null;
            int options = r.ReadInt32();
            int width = r.ReadInt32();
            int height = r.ReadInt32();
            int surfaceFormat = r.ReadInt32();
            int channelCount = r.ReadInt32();
            r.ReadBytes(128);
            if (width <= 0 || height <= 0 || width > 8192 || height > 8192) return null;

            bool mip = (options & OPT_MIPMAPS) != 0;
            int imageCount = 1 + (mip ? (int)(Math.Log(width) / Math.Log(2)) : 0);

            // Canales — se conservan EN ORDEN DE DECLARACIÓN (crítico: los datos se almacenan en ese orden)
            var channels = new List<(int size, int type)>();
            for (int c = 0; c < channelCount; c++)
            {
                byte size = (byte)r.ReadUInt64();
                ulong type = r.ReadUInt64();
                if (type < 2 || type > 6) return null;
                channels.Add((size, (int)type));
            }
            // bits de alfa: 8 si hay canal Alpha (tipo 6), 1 si hay Máscara (tipo 2), 0 si ninguno
            if (channels.Exists(c => c.type == 6)) alphaBits = 8;
            else if (channels.Exists(c => c.type == 2)) alphaBits = 1;

            int[] argb; // 0xAARRGGBB (orden GDI logico)
            if ((options & OPT_RAWDATA) != 0)
            {
                r.ReadBytes(imageCount * 4); // tabla de offsets
                byte[] buffer = Array.Empty<byte>();
                // mip 0
                if (width >= 4 && height >= 4)
                    buffer = r.ReadBytes(r.ReadInt32());
                argb = DecodeRaw(buffer, width, height, surfaceFormat);
            }
            else
            {
                // saltar tablas de offset de scanlines de todas las imagenes
                for (int i = 0; i < imageCount; i++)
                    r.ReadBytes(4 * (height / (int)Math.Pow(2, i)));
                argb = DecodeStructured(r, width, height, channels);
            }
            if (argb == null) return null;
            return BuildBitmap(argb, width, height);
        }

        static int[] DecodeStructured(BinaryReader r, int width, int height, List<(int size, int type)> channels)
        {
            var outp = new int[width * height];
            var line = new byte[8][];
            const int RED = 3, GREEN = 4, BLUE = 5, MASK = 2, ALPHA = 6;
            for (int y = 0; y < height; y++)
            {
                // Leer los canales EN ORDEN DE DECLARACIÓN (igual que Orts.Formats.Msts.AceFile)
                foreach (var (size, type) in channels)
                {
                    if (size == 1)
                    {
                        var bytes = r.ReadBytes((int)Math.Ceiling((double)width / 8));
                        line[type] = new byte[width];
                        for (int x = 0; x < width; x++)
                            line[type][x] = (byte)(((bytes[x / 8] >> (7 - (x % 8))) & 1) * 0xFF);
                    }
                    else
                    {
                        line[type] = r.ReadBytes(width);
                    }
                }
                for (int x = 0; x < width; x++)
                {
                    byte rr = line[RED] != null ? line[RED][x] : (byte)0;
                    byte gg = line[GREEN] != null ? line[GREEN][x] : (byte)0;
                    byte bb = line[BLUE] != null ? line[BLUE][x] : (byte)0;
                    byte aa = 0xFF;
                    if (line[ALPHA] != null) aa = line[ALPHA][x];
                    else if (line[MASK] != null) aa = line[MASK][x];
                    outp[y * width + x] = (aa << 24) | (rr << 16) | (gg << 8) | bb;
                }
            }
            return outp;
        }

        static int[] DecodeRaw(byte[] data, int width, int height, int surfaceFormat)
        {
            switch (surfaceFormat)
            {
                case 0x12: return DecodeDxt(data, width, height, 1);
                case 0x14: return DecodeDxt(data, width, height, 3);
                case 0x16: return DecodeDxt(data, width, height, 5);
                case 0x0E: return Decode565(data, width, height);
                case 0x10: return Decode5551(data, width, height);
                case 0x11: return Decode4444(data, width, height);
                default: return null;
            }
        }

        static int[] Decode565(byte[] d, int w, int h)
        {
            var o = new int[w * h];
            for (int i = 0; i < w * h && (i * 2 + 1) < d.Length; i++)
            {
                ushort v = (ushort)(d[i * 2] | (d[i * 2 + 1] << 8));
                o[i] = (0xFF << 24) | (Expand5((v >> 11) & 0x1F) << 16) | (Expand6((v >> 5) & 0x3F) << 8) | Expand5(v & 0x1F);
            }
            return o;
        }
        static int[] Decode5551(byte[] d, int w, int h)
        {
            var o = new int[w * h];
            for (int i = 0; i < w * h && (i * 2 + 1) < d.Length; i++)
            {
                ushort v = (ushort)(d[i * 2] | (d[i * 2 + 1] << 8));
                int a = ((v >> 15) & 1) * 0xFF;
                o[i] = (a << 24) | (Expand5((v >> 10) & 0x1F) << 16) | (Expand5((v >> 5) & 0x1F) << 8) | Expand5(v & 0x1F);
            }
            return o;
        }
        static int[] Decode4444(byte[] d, int w, int h)
        {
            var o = new int[w * h];
            for (int i = 0; i < w * h && (i * 2 + 1) < d.Length; i++)
            {
                ushort v = (ushort)(d[i * 2] | (d[i * 2 + 1] << 8));
                int a = ((v >> 12) & 0xF) * 17, rr = ((v >> 8) & 0xF) * 17, gg = ((v >> 4) & 0xF) * 17, bb = (v & 0xF) * 17;
                o[i] = (a << 24) | (rr << 16) | (gg << 8) | bb;
            }
            return o;
        }
        static int Expand5(int v) => (v << 3) | (v >> 2);
        static int Expand6(int v) => (v << 2) | (v >> 4);

        static int[] DecodeDxt(byte[] d, int w, int h, int variant)
        {
            var o = new int[w * h];
            int bx = (w + 3) / 4, by = (h + 3) / 4;
            int pos = 0;
            for (int cy = 0; cy < by; cy++)
            {
                for (int cx = 0; cx < bx; cx++)
                {
                    var alpha = new int[16];
                    for (int i = 0; i < 16; i++) alpha[i] = 0xFF;

                    if (variant == 3)
                    {
                        if (pos + 8 > d.Length) return o;
                        for (int i = 0; i < 16; i++)
                        {
                            int nib = (d[pos + i / 2] >> ((i % 2) * 4)) & 0xF;
                            alpha[i] = nib * 17;
                        }
                        pos += 8;
                    }
                    else if (variant == 5)
                    {
                        if (pos + 8 > d.Length) return o;
                        int a0 = d[pos], a1 = d[pos + 1];
                        long bits = 0;
                        for (int i = 0; i < 6; i++) bits |= (long)d[pos + 2 + i] << (8 * i);
                        for (int i = 0; i < 16; i++)
                        {
                            int code = (int)((bits >> (3 * i)) & 0x7);
                            int av;
                            if (a0 > a1)
                                av = code == 0 ? a0 : code == 1 ? a1 : ((8 - code) * a0 + (code - 1) * a1) / 7;
                            else
                                av = code == 0 ? a0 : code == 1 ? a1 : code == 6 ? 0 : code == 7 ? 255 : ((6 - code) * a0 + (code - 1) * a1) / 5;
                            alpha[i] = av;
                        }
                        pos += 8;
                    }

                    if (pos + 8 > d.Length) return o;
                    ushort c0 = (ushort)(d[pos] | (d[pos + 1] << 8));
                    ushort c1 = (ushort)(d[pos + 2] | (d[pos + 3] << 8));
                    uint idx = (uint)(d[pos + 4] | (d[pos + 5] << 8) | (d[pos + 6] << 16) | (d[pos + 7] << 24));
                    pos += 8;

                    int r0 = Expand5((c0 >> 11) & 0x1F), g0 = Expand6((c0 >> 5) & 0x3F), b0 = Expand5(c0 & 0x1F);
                    int r1 = Expand5((c1 >> 11) & 0x1F), g1 = Expand6((c1 >> 5) & 0x3F), b1 = Expand5(c1 & 0x1F);
                    var col = new int[4];
                    col[0] = Rgb(r0, g0, b0);
                    col[1] = Rgb(r1, g1, b1);
                    if (c0 > c1 || variant != 1)
                    {
                        col[2] = Rgb((2 * r0 + r1) / 3, (2 * g0 + g1) / 3, (2 * b0 + b1) / 3);
                        col[3] = Rgb((r0 + 2 * r1) / 3, (g0 + 2 * g1) / 3, (b0 + 2 * b1) / 3);
                    }
                    else
                    {
                        col[2] = Rgb((r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2);
                        col[3] = 0; // transparente en DXT1 1-bit alpha
                    }

                    for (int py = 0; py < 4; py++)
                    {
                        for (int px = 0; px < 4; px++)
                        {
                            int i = py * 4 + px;
                            int sel = (int)((idx >> (2 * i)) & 0x3);
                            int x = cx * 4 + px, y = cy * 4 + py;
                            if (x >= w || y >= h) continue;
                            int rgb = col[sel];
                            int a = alpha[i];
                            if (variant == 1 && sel == 3 && !(c0 > c1)) a = 0;
                            o[y * w + x] = (a << 24) | (rgb & 0x00FFFFFF);
                        }
                    }
                }
            }
            return o;
        }
        static int Rgb(int r, int g, int b) => (r << 16) | (g << 8) | b;

        static Bitmap BuildBitmap(int[] argb, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                // Format32bppArgb en memoria = bytes B,G,R,A (int LE 0xAARRGGBB). argb ya es 0xAARRGGBB.
                var buf = new byte[w * h * 4];
                for (int i = 0; i < w * h; i++)
                {
                    int p = argb[i];
                    buf[i * 4 + 0] = (byte)(p & 0xFF);         // B
                    buf[i * 4 + 1] = (byte)((p >> 8) & 0xFF);  // G
                    buf[i * 4 + 2] = (byte)((p >> 16) & 0xFF); // R
                    buf[i * 4 + 3] = (byte)((p >> 24) & 0xFF); // A
                }
                if (data.Stride == w * 4)
                {
                    Marshal.Copy(buf, 0, data.Scan0, buf.Length);
                }
                else
                {
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(buf, y * w * 4, IntPtr.Add(data.Scan0, y * data.Stride), w * 4);
                }
            }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        // (1.2.77, TeleEditor) Guarda un .ace sin comprimir («SIMISA@@@@@@@@@@»), con datos por canales como lo lee Open
        // Rails (Orts.Formats.Msts.AceFile): rojo, verde y azul de 8 bits y, según alphaBits, alfa de 8 bits (8) o máscara de
        // 1 bit (1). Con mipmaps si la textura es cuadrada y potencia de 2 (como las de los trenes); si no, sin ellos.
        public static void Save(Bitmap bmp, string fileName, int alphaBits)
        {
            int w = bmp.Width, h = bmp.Height;
            bool mips = w == h && w > 0 && (w & (w - 1)) == 0;
            var levels = new List<int[]>();
            var px = new int[w * h];
            var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { Marshal.Copy(d.Scan0, px, 0, px.Length); } finally { bmp.UnlockBits(d); }
            levels.Add(px);
            if (mips)
                for (int s = w; s > 1; s /= 2)
                {
                    var src = levels[^1]; int ns = s / 2; var dst = new int[ns * ns];
                    for (int y = 0; y < ns; y++)
                        for (int x = 0; x < ns; x++)
                        {
                            int a = 0, r = 0, g = 0, b = 0;
                            foreach (int c in new[] { src[(2 * y) * s + 2 * x], src[(2 * y) * s + 2 * x + 1], src[(2 * y + 1) * s + 2 * x], src[(2 * y + 1) * s + 2 * x + 1] })
                            { a += (c >> 24) & 255; r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255; }
                            dst[y * ns + x] = ((a / 4) << 24) | ((r / 4) << 16) | ((g / 4) << 8) | (b / 4);
                        }
                    levels.Add(dst);
                }
            var ch = new List<(int size, int type)> { (8, 3), (8, 4), (8, 5) };
            if (alphaBits >= 8) ch.Add((8, 6)); else if (alphaBits == 1) ch.Add((1, 2));
            using var ms = new MemoryStream();
            using (var wr = new BinaryWriter(ms, System.Text.Encoding.ASCII, true))
            {
                wr.Write(System.Text.Encoding.ASCII.GetBytes("SIMISA@@@@@@@@@@"));
                wr.Write(new byte[] { 1, 0, 0, 0 });
                wr.Write(mips ? OPT_MIPMAPS : 0);
                wr.Write(w); wr.Write(h);
                wr.Write(alphaBits >= 8 ? 0x11 : alphaBits == 1 ? 0x10 : 0x0E);   // (informativo con datos por canales)
                wr.Write(ch.Count);
                var name = new byte[128]; System.Text.Encoding.ASCII.GetBytes("SelectOR").CopyTo(name, 0); wr.Write(name);
                foreach (var (size, type) in ch) { wr.Write((ulong)size); wr.Write((ulong)type); }
                // tablas de desplazamiento de cada fila de cada nivel (Open Rails no las usa; se rellenan bien igualmente)
                int lineBytes(int lw) => ch.Sum(c => c.size == 1 ? (lw + 7) / 8 : lw);
                long headerEnd = ms.Position + Enumerable.Range(0, levels.Count).Sum(l => 4 * (h >> l));
                long pos = 0;
                for (int l = 0; l < levels.Count; l++)
                {
                    int lw = w >> l, lh = h >> l;
                    for (int y = 0; y < lh; y++) { wr.Write((int)(headerEnd - 16 + pos)); pos += lineBytes(lw); }
                }
                for (int l = 0; l < levels.Count; l++)
                {
                    int lw = w >> l, lh = h >> l; var lv = levels[l];
                    for (int y = 0; y < lh; y++)
                        foreach (var (size, type) in ch)
                        {
                            if (size == 1)
                            {
                                var bits = new byte[(lw + 7) / 8];
                                for (int x = 0; x < lw; x++) if (((lv[y * lw + x] >> 24) & 255) >= 128) bits[x / 8] |= (byte)(0x80 >> (x % 8));
                                wr.Write(bits);
                            }
                            else
                            {
                                int shift = type == 3 ? 16 : type == 4 ? 8 : type == 5 ? 0 : 24;
                                var line = new byte[lw];
                                for (int x = 0; x < lw; x++) line[x] = (byte)((lv[y * lw + x] >> shift) & 255);
                                wr.Write(line);
                            }
                        }
                }
            }
            string tmp = fileName + ".tmp";
            File.WriteAllBytes(tmp, ms.ToArray());
            File.Move(tmp, fileName, true);
        }
    }
}
