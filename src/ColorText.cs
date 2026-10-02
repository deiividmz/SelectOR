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

        public static void Draw(Graphics g, string text, Font font, Rectangle r, Color color, bool right = false)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
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

        // Varios textos cortos centrados en sus celdas (el selector de emojis) con un solo enlace al HDC.
        public static void DrawCells(Graphics g, IReadOnlyList<(string text, Rectangle cell)> cells, Font font, Color color, Rectangle bounds)
        {
            if (cells.Count == 0) return;
            if (Available) { try { Dw.DrawCells(g, cells, font, color, bounds); return; } catch { _ok = false; } }
            foreach (var (t, c) in cells)
                TextRenderer.DrawText(g, t, font, c, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

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
