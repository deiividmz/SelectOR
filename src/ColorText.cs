// Texto con EMOJIS EN COLOR. GDI (TextRenderer) dibuja los emojis en blanco y negro; DirectWrite los dibuja
// en color. Se usa solo con los textos que llevan emojis (el resto sigue con TextRenderer, igual que siempre)
// y a través de SharpDX, que ya viene con Open Rails (no se añade nada al paquete). Si en ese Open Rails no
// está o falla, Available = false y se vuelve a TextRenderer.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace SelectOR
{
    public static class ColorText
    {
        static bool? _ok;

        public static bool Available
        {
            get
            {
                if (_ok == null) { try { _ok = TryInit(); } catch { _ok = false; } }
                return _ok.Value;
            }
        }

        // Aparte (sin «inline»): si falta SharpDX, el fallo salta al compilar esta función, dentro del try de arriba.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool TryInit() => Dw.Init();

        // ¿Lleva algo que GDI no pinta en color? (pares sustitutos = casi todos los emojis; y los símbolos
        // de U+2600–U+27BF y U+2B00–U+2BFF, que con el selector de variante 16 también son emojis).
        public static bool HasEmoji(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if (char.IsSurrogate(c) || (c >= '☀' && c <= '➿') || (c >= '⬀' && c <= '⯿') || c == '️' || c == '‍') return true;
            return false;
        }

        // ¿Solo emojis (y como mucho «max» de ellos)? Esos mensajes se enseñan más grandes, como en otros chats.
        public static bool EmojiOnly(string s, int max = 3)
        {
            if (string.IsNullOrWhiteSpace(s) || !HasEmoji(s)) return false;
            int n = 0;
            var e = System.Globalization.StringInfo.GetTextElementEnumerator(s.Trim());
            while (e.MoveNext())
            {
                string t = (string)e.Current;
                if (t.Trim().Length == 0) continue;
                if (!HasEmoji(t)) return false;
                if (++n > max) return false;
            }
            return n > 0;
        }

        // Mide un texto con saltos de línea en «maxWidth» píxeles (como TextRenderer con WordBreak).
        // Rendimiento: las medidas se recuerdan y cada texto ya dibujado se guarda como imagen (con tope de
        // memoria): al repintar solo se copia. Antes cada repintado medía y dibujaba con DirectWrite otra vez.
        const int MeasureMax = 4000, ImgMaxCount = 900;
        const long ImgMaxPixels = 6_000_000;   // ~24 MB como mucho
        static readonly Dictionary<(string, string, float, int, int), Size> _measure = new();
        sealed class Img { public Bitmap Bmp; public LinkedListNode<(string, string, float, int, int, int, int, bool)> Node; }
        static readonly Dictionary<(string, string, float, int, int, int, int, bool), Img> _imgs = new();
        static readonly LinkedList<(string, string, float, int, int, int, int, bool)> _lru = new();
        static long _imgPixels;

        public static Size Measure(Graphics g, string text, Font font, int maxWidth)
        {
            text ??= "";
            if (text.IndexOf('\n') < 0 && IconSplit(text, out _, out _, out _)) { var isz = IconSize(g, text, font); if (isz.Width <= maxWidth) return isz; }   // (2026-11-09) con su icono propio
            var key = (text, font.FontFamily.Name, font.SizeInPoints * g.DpiY / 72f, (int)font.Style, maxWidth);
            lock (_measure)
                if (_measure.TryGetValue(key, out var hit)) return hit;
            Size sz;
            if (Available) { try { sz = Dw.Measure(g, text, font, maxWidth); goto done; } catch { _ok = false; } }
            sz = TextRenderer.MeasureText(g, text, font, new Size(maxWidth, 0), Flags);
            done:
            lock (_measure)
            {
                if (_measure.Count >= MeasureMax) _measure.Clear();
                _measure[key] = sz;
            }
            return sz;
        }

        public static void Draw(Graphics g, string text, Font font, Rectangle r, Color color, bool right = false, bool raw = false)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            if (!raw && TryIcon(g, text, font, r, color, TextFormatFlags.WordBreak | (right ? TextFormatFlags.Right : 0))) return;   // (2026-11-09) iconos propios
            if (Available)
            {
                try
                {
                    var key = (text ?? "", font.FontFamily.Name, font.SizeInPoints * g.DpiY / 72f, (int)font.Style, color.ToArgb(), r.Width, r.Height, right);
                    if (!_imgs.TryGetValue(key, out var img))
                    {
                        var bmp = Dw.Render(g, text, font, r.Size, color, right);
                        if (bmp == null) { Dw.Draw(g, text, font, r, color, right); return; }
                        img = new Img { Bmp = bmp, Node = _lru.AddLast(key) };
                        _imgs[key] = img; _imgPixels += (long)r.Width * r.Height;
                        while ((_imgs.Count > ImgMaxCount || _imgPixels > ImgMaxPixels) && _lru.First != null && _lru.First != img.Node)
                        {
                            var old = _lru.First.Value; _lru.RemoveFirst();
                            if (_imgs.TryGetValue(old, out var o)) { _imgPixels -= (long)o.Bmp.Width * o.Bmp.Height; o.Bmp.Dispose(); _imgs.Remove(old); }
                        }
                    }
                    else if (img.Node.Next != null) { _lru.Remove(img.Node); _lru.AddLast(img.Node); }
                    g.DrawImageUnscaled(img.Bmp, r.X, r.Y);
                    return;
                }
                catch { _ok = false; }
            }
            TextRenderer.DrawText(g, text, font, r, color, Flags | (right ? TextFormatFlags.Right : 0));
        }

        // (1.2.90) Como TextRenderer.DrawText, pero con los emojis EN COLOR: los textos sin emojis van por TextRenderer como
        // siempre. Con WordBreak, el texto partido en líneas desde arriba; si no, en una línea con su alineación.
        public static void Text(Graphics g, string text, Font font, Rectangle r, Color color, TextFormatFlags flags)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            if (TryIcon(g, text, font, r, color, flags)) return;   // (2026-11-09) iconos propios en vez de emojis
            if (string.IsNullOrEmpty(text) || !HasEmoji(text) || !Available) { TextRenderer.DrawText(g, text, font, r, color, flags); return; }
            bool right = (flags & TextFormatFlags.Right) != 0;
            var st = g.Save();
            try
            {
                g.SetClip(r, System.Drawing.Drawing2D.CombineMode.Intersect);
                if ((flags & TextFormatFlags.WordBreak) != 0)
                {
                    // (1.2.90) también con varias líneas: centrado o abajo como pide (antes, siempre arriba a la izquierda:
                    // «🚧 Objetos» del puesto salía pegado a la esquina)
                    var wsz = Measure(g, text, font, r.Width);
                    int ww = Math.Min(wsz.Width + 2, r.Width), wh = Math.Min(wsz.Height, r.Height);
                    int wx = (flags & TextFormatFlags.HorizontalCenter) != 0 ? r.X + (r.Width - ww) / 2 : right ? r.Right - ww : r.X;
                    int wy = (flags & TextFormatFlags.VerticalCenter) != 0 ? r.Y + (r.Height - wh) / 2 : (flags & TextFormatFlags.Bottom) != 0 ? r.Bottom - wh : r.Y;
                    Draw(g, text, font, new Rectangle(wx, wy, Math.Max(1, ww), Math.Max(1, r.Bottom - wy)), color, right);
                    return;
                }
                var sz = Measure(g, text, font, 100000);
                int w = Math.Min(sz.Width + 2, r.Width), h = sz.Height;
                int x = (flags & TextFormatFlags.HorizontalCenter) != 0 ? r.X + (r.Width - w) / 2 : right ? r.Right - w : r.X;
                int y = (flags & TextFormatFlags.VerticalCenter) != 0 ? r.Y + (r.Height - h) / 2 : (flags & TextFormatFlags.Bottom) != 0 ? r.Bottom - h : r.Y;
                Draw(g, text, font, new Rectangle(x, y, Math.Max(1, w), Math.Max(1, h)), color, right);
            }
            finally { g.Restore(st); }
        }

        // las mismas formas que TextRenderer.DrawText (en un punto, o sin opciones: centrado, como él)
        public static void Text(Graphics g, string text, Font font, Rectangle r, Color color)
        {
            if (TryIcon(g, text, font, r, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix)) return;
            if (string.IsNullOrEmpty(text) || !HasEmoji(text) || !Available) { TextRenderer.DrawText(g, text, font, r, color); return; }
            Text(g, text, font, r, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        public static void Text(Graphics g, string text, Font font, Point p, Color color) => Text(g, text, font, p, color, TextFormatFlags.Default);
        public static void Text(Graphics g, string text, Font font, Point p, Color color, TextFormatFlags flags)
        {
            if (IconSplit(text, out _, out _, out _))
            {
                var isz = IconSize(g, text, font);
                TryIcon(g, text, font, new Rectangle(p.X, p.Y, isz.Width + 2, isz.Height), color, TextFormatFlags.Left | TextFormatFlags.Top);
                return;
            }
            if (string.IsNullOrEmpty(text) || !HasEmoji(text) || !Available) { TextRenderer.DrawText(g, text, font, p, color, flags); return; }
            var sz = Measure(g, text, font, 100000);
            Draw(g, text, font, new Rectangle(p.X, p.Y, sz.Width + 2, sz.Height), color);
        }

        // Varios textos cortos centrados en sus celdas (el selector de emojis) con un solo enlace al HDC.
        public static void DrawCells(Graphics g, IReadOnlyList<(string text, Rectangle cell)> cells, Font font, Color color, Rectangle bounds, bool raw = false)
        {
            if (cells.Count == 0) return;
            if (!raw)   // (2026-11-09) los que son un icono propio, dibujados; el resto, como siempre
            {
                var rest = new List<(string, Rectangle)>();
                foreach (var (t, c) in cells)
                {
                    string k = (t ?? "").Trim();
                    if (k.Length > 0 && LineIcons.Has(k))
                    {
                        float s = Math.Min(Math.Min(c.Width, c.Height), font.GetHeight(g) * 1.15f);
                        LineIcons.Draw(g, k, new RectangleF(c.X + (c.Width - s) / 2f, c.Y + (c.Height - s) / 2f, s, s), IconTint(k, color));
                    }
                    else rest.Add((t, c));
                }
                if (rest.Count == 0) return;
                cells = rest;
            }
            if (Available) { try { Dw.DrawCells(g, cells, font, color, bounds); return; } catch { _ok = false; } }
            foreach (var (t, c) in cells)
                TextRenderer.DrawText(g, t, font, c, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        // ---- (2026-11-09) iconos propios (LineIcons, como en la vista previa aprobada) en vez de los emojis a color ----
        // Valen los textos que empiezan por un emoji que tiene icono (o que son solo ese emoji) y no llevan más emojis: el
        // icono, del alto de la letra, y el texto detrás con TextRenderer. Lo demás (emojis por en medio, el chat) como siempre.
        static bool IconSplit(string text, out string icon, out int spaces, out string rest)
        {
            icon = null; spaces = 0; rest = null;
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.TrimStart();
            if (t.Length == 0 || !HasEmoji(t)) return false;
            string el = System.Globalization.StringInfo.GetNextTextElement(t, 0);
            int n = el.Length;
            while (n < t.Length && (t[n] == '\uFE0F' || t[n] == '\u200D')) { el += t[n]; n++; }
            if (!LineIcons.Has(el)) return false;
            string after = t.Substring(n);
            if (HasEmoji(after)) return false;
            int sp = 0; while (sp < after.Length && char.IsWhiteSpace(after[sp]) && after[sp] != '\n') sp++;
            icon = el; spaces = sp; rest = after.Substring(sp);
            return true;
        }

        // (2026-11-09) ancho de una línea de texto (sin Graphics), con su icono propio si lo lleva: para las pastillas
        public static int LineWidth(string text, Font font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            if (IconSplit(text, out _, out int sp, out string rest))
            {
                int s = Math.Max(8, (int)Math.Round(font.Height * 1.05f));
                return LeadPx(text, font) + s + (rest.Length > 0 ? IconGap(sp, s) + TextRenderer.MeasureText(rest, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width : 0);
            }
            return TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        }

        // (2026-11-09) El fondo sobre el que se va a dibujar (lo pone quien pinta un fondo de color: botones…). Si el color del
        // icono se parece demasiado a él (poco contraste), el icono va del color del texto, que sí se lee sobre ese fondo.
        [ThreadStatic] public static Color? IconBg;
        static double Lum(Color c)
        {
            double L(int v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
        }
        public static double Contrast(Color a, Color b) { double la = Lum(a), lb = Lum(b); return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05); }
        static Color? IconTint(string icon, Color text)
        {
            if (text.A < 160 || Lum(text) < 0.06) return text;
            if (text.R == 255 && text.G == 255 && text.B == 255) return text;   // (blanco puro: un icono sobre una insignia de color)   // (marcas de agua: un color muy oscuro o transparente → el icono, igual de tenue)
            if (IconBg is not Color bg) return null;
            var ic = LineIcons.ColorOf(icon);
            return Contrast(ic, bg) < 2.2 ? text : (Color?)null;
        }

        // los espacios del principio (separan de lo de antes, p. ej. «      🧍 291»): se respetan
        static int LeadPx(string text, Font font)
        {
            int n = 0; while (n < text.Length && text[n] == ' ') n++;
            if (n == 0) return 0;
            var f = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            return Math.Max(0, TextRenderer.MeasureText("a" + new string(' ', n) + "a", font, Size.Empty, f).Width - TextRenderer.MeasureText("aa", font, Size.Empty, f).Width);
        }

        static int IconPx(Graphics g, Font font) => Math.Max(8, (int)Math.Round(font.GetHeight(g) * 1.05f));
        static int IconGap(int spaces, int s) => spaces == 0 ? Math.Max(3, s / 5) : Math.Max(4, (int)(s * 0.32f * Math.Min(3, spaces)));

        static Size IconSize(Graphics g, string text, Font font)
        {
            IconSplit(text, out _, out int sp, out string rest);
            int s = IconPx(g, font);
            var tsz = string.IsNullOrEmpty(rest) ? Size.Empty : TextRenderer.MeasureText(g, rest, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            return new Size(LeadPx(text, font) + s + (tsz.Width > 0 ? IconGap(sp, s) + tsz.Width : 0), Math.Max(s, tsz.Height));
        }

        static bool TryIcon(Graphics g, string text, Font font, Rectangle r, Color color, TextFormatFlags flags)
        {
            if (!IconSplit(text, out string icon, out int sp, out string rest)) return false;
            try
            {
                int s = IconPx(g, font), lineH = Math.Max(s, TextRenderer.MeasureText(g, "Ág", font, Size.Empty, TextFormatFlags.NoPadding).Height);
                bool wrap = (flags & TextFormatFlags.WordBreak) != 0 && rest.Length > 0;
                int gap = rest.Length > 0 ? IconGap(sp, s) : 0;
                int tw = rest.Length == 0 ? 0 : TextRenderer.MeasureText(g, rest, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                int lead = LeadPx(text, font);
                int total = Math.Min(r.Width, lead + s + gap + tw);
                int x = ((flags & TextFormatFlags.HorizontalCenter) != 0 ? r.X + (r.Width - total) / 2 : (flags & TextFormatFlags.Right) != 0 ? r.Right - total : r.X) + lead;
                int blockH = lineH;
                if (wrap) blockH = Math.Max(lineH, TextRenderer.MeasureText(g, rest, font, new Size(Math.Max(1, r.Right - x - s - gap), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height);
                int y = (flags & TextFormatFlags.VerticalCenter) != 0 ? r.Y + (r.Height - Math.Min(r.Height, blockH)) / 2 : (flags & TextFormatFlags.Bottom) != 0 ? r.Bottom - blockH : r.Y;
                var st = g.Save();
                g.SetClip(r, System.Drawing.Drawing2D.CombineMode.Intersect);
                LineIcons.Draw(g, icon, new RectangleF(x, y + (lineH - s) / 2f, s, s), IconTint(icon, color));
                g.Restore(st);
                if (rest.Length > 0)
                {
                    var tr = new Rectangle(x + s + gap, y, Math.Max(1, r.Right - (x + s + gap)), Math.Max(1, (wrap ? r.Bottom : y + lineH) - y));
                    var tf = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | (wrap ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter)
                           | (flags & TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, rest, font, tr, color, tf);
                }
                return true;
            }
            catch { return false; }
        }

        // Todo lo que toca SharpDX va aquí: si la DLL no está, falla al entrar en esta clase (y se captura arriba).
        static class Dw
        {
            static SharpDX.Direct2D1.Factory _d2d;
            static SharpDX.DirectWrite.Factory _dw;
            static SharpDX.Direct2D1.DeviceContextRenderTarget _rt;
            static SharpDX.WIC.ImagingFactory _wic;
            static readonly Dictionary<(string, float, bool, bool), SharpDX.DirectWrite.TextFormat> _formats = new();

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static bool Init()
            {
                _d2d = new SharpDX.Direct2D1.Factory(SharpDX.Direct2D1.FactoryType.SingleThreaded);
                _dw = new SharpDX.DirectWrite.Factory(SharpDX.DirectWrite.FactoryType.Shared);
                var props = new SharpDX.Direct2D1.RenderTargetProperties(
                    SharpDX.Direct2D1.RenderTargetType.Default,
                    new SharpDX.Direct2D1.PixelFormat(SharpDX.DXGI.Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Ignore),
                    96, 96, SharpDX.Direct2D1.RenderTargetUsage.None, SharpDX.Direct2D1.FeatureLevel.Level_DEFAULT);
                _rt = new SharpDX.Direct2D1.DeviceContextRenderTarget(_d2d, props);
                return true;
            }

            static SharpDX.DirectWrite.TextFormat Format(Graphics g, Font f)
            {
                float px = f.SizeInPoints * g.DpiY / 72f;
                var key = (f.FontFamily.Name, px, f.Bold, f.Italic);
                if (!_formats.TryGetValue(key, out var tf))
                {
                    tf = new SharpDX.DirectWrite.TextFormat(_dw, f.FontFamily.Name,
                        f.Bold ? SharpDX.DirectWrite.FontWeight.Bold : SharpDX.DirectWrite.FontWeight.Normal,
                        f.Italic ? SharpDX.DirectWrite.FontStyle.Italic : SharpDX.DirectWrite.FontStyle.Normal,
                        SharpDX.DirectWrite.FontStretch.Normal, px)
                    { WordWrapping = SharpDX.DirectWrite.WordWrapping.Wrap };
                    _formats[key] = tf;
                }
                return tf;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static Size Measure(Graphics g, string text, Font font, int maxWidth)
            {
                using var layout = new SharpDX.DirectWrite.TextLayout(_dw, text ?? "", Format(g, font), Math.Max(1, maxWidth), 100000f);
                var m = layout.Metrics;
                return new Size((int)Math.Ceiling(m.WidthIncludingTrailingWhitespace), (int)Math.Ceiling(m.Height));
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void DrawCells(Graphics g, IReadOnlyList<(string text, Rectangle cell)> cells, Font font, Color color, Rectangle bounds)
            {
                if (bounds.Width <= 0 || bounds.Height <= 0) return;
                var fmt = Format(g, font);
                IntPtr hdc = g.GetHdc();
                try
                {
                    _rt.BindDeviceContext(hdc, new SharpDX.Mathematics.Interop.RawRectangle(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
                    _rt.BeginDraw();
                    _rt.TextAntialiasMode = SharpDX.Direct2D1.TextAntialiasMode.Grayscale;
                    using (var brush = new SharpDX.Direct2D1.SolidColorBrush(_rt, new SharpDX.Mathematics.Interop.RawColor4(color.R / 255f, color.G / 255f, color.B / 255f, 1f)))
                        foreach (var (t, c) in cells)
                        {
                            using var layout = new SharpDX.DirectWrite.TextLayout(_dw, t, fmt, c.Width, c.Height)
                            {
                                TextAlignment = SharpDX.DirectWrite.TextAlignment.Center,
                                ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center
                            };
                            _rt.DrawTextLayout(new SharpDX.Mathematics.Interop.RawVector2(c.X - bounds.X, c.Y - bounds.Y), layout, brush, SharpDX.Direct2D1.DrawTextOptions.EnableColorFont);
                        }
                    _rt.EndDraw();
                }
                finally { g.ReleaseHdc(hdc); }
            }

            // El texto sobre fondo transparente (para guardarlo y copiarlo en cada repintado). null si no se puede.
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static Bitmap Render(Graphics g, string text, Font font, Size size, Color color, bool right)
            {
                _wic ??= new SharpDX.WIC.ImagingFactory();
                using var wb = new SharpDX.WIC.Bitmap(_wic, size.Width, size.Height, SharpDX.WIC.PixelFormat.Format32bppPBGRA, SharpDX.WIC.BitmapCreateCacheOption.CacheOnLoad);
                var props = new SharpDX.Direct2D1.RenderTargetProperties(
                    SharpDX.Direct2D1.RenderTargetType.Default,
                    new SharpDX.Direct2D1.PixelFormat(SharpDX.DXGI.Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Premultiplied),
                    96, 96, SharpDX.Direct2D1.RenderTargetUsage.None, SharpDX.Direct2D1.FeatureLevel.Level_DEFAULT);
                using (var rt = new SharpDX.Direct2D1.WicRenderTarget(_d2d, wb, props))
                using (var layout = new SharpDX.DirectWrite.TextLayout(_dw, text ?? "", Format(g, font), size.Width, Math.Max(size.Height, 1)))
                {
                    if (right) layout.TextAlignment = SharpDX.DirectWrite.TextAlignment.Trailing;
                    rt.BeginDraw();
                    rt.Clear(new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0));
                    rt.TextAntialiasMode = SharpDX.Direct2D1.TextAntialiasMode.Grayscale;   // con transparencia no cabe ClearType
                    using (var brush = new SharpDX.Direct2D1.SolidColorBrush(rt, new SharpDX.Mathematics.Interop.RawColor4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f)))
                        rt.DrawTextLayout(new SharpDX.Mathematics.Interop.RawVector2(0, 0), layout, brush, SharpDX.Direct2D1.DrawTextOptions.EnableColorFont);
                    rt.EndDraw();
                }
                var bmp = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                var bd = bmp.LockBits(new Rectangle(0, 0, size.Width, size.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try { wb.CopyPixels(bd.Stride, bd.Scan0, bd.Stride * size.Height); }
                finally { bmp.UnlockBits(bd); }
                return bmp;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void Draw(Graphics g, string text, Font font, Rectangle r, Color color, bool right)
            {
                if (r.Width <= 0 || r.Height <= 0) return;
                using var layout = new SharpDX.DirectWrite.TextLayout(_dw, text ?? "", Format(g, font), r.Width, Math.Max(r.Height, 1));
                if (right) layout.TextAlignment = SharpDX.DirectWrite.TextAlignment.Trailing;
                // La transformación de Graphics no llega al HDC: el rectángulo va ya en coordenadas del dispositivo.
                var pts = new[] { r.Location };
                g.TransformPoints(System.Drawing.Drawing2D.CoordinateSpace.Device, System.Drawing.Drawing2D.CoordinateSpace.World, pts);
                IntPtr hdc = g.GetHdc();
                try
                {
                    _rt.BindDeviceContext(hdc, new SharpDX.Mathematics.Interop.RawRectangle(pts[0].X, pts[0].Y, pts[0].X + r.Width, pts[0].Y + r.Height));
                    _rt.BeginDraw();
                    _rt.TextAntialiasMode = SharpDX.Direct2D1.TextAntialiasMode.Cleartype;
                    using (var brush = new SharpDX.Direct2D1.SolidColorBrush(_rt, new SharpDX.Mathematics.Interop.RawColor4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f)))
                        _rt.DrawTextLayout(new SharpDX.Mathematics.Interop.RawVector2(0, 0), layout, brush, SharpDX.Direct2D1.DrawTextOptions.EnableColorFont);
                    _rt.EndDraw();
                }
                finally { g.ReleaseHdc(hdc); }
            }
        }
    }
}
