// Servicios programados como un teleindicador de estación (Empresas → Servicios → «Programados»): reloj arriba,
// hora de salida en ámbar (y hasta cuándo), ruta y recorrido, la composición 2D del tren asignado como en Flota (o
// «cualquier tren de viajeros / mercancías» en la misma franja, como en Servicios) y el estado en una paleta como las de los paneles de salidas.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class SchedBoard : CardListBase
    {
        public sealed class Row
        {
            public string Id, Time = "", UntilLabel = "", UntilTime = "", Day = "", Route = "", Path = "", Note = "", Train = "", Kind = "";   // Kind: pax | freight | "" (cualquiera)
            public string State = "", StateText = "", Who = "", Right = "", RightSub = "";
            public Color StateColor = Color.Gray;
            public bool HasTrain;          // tren concreto de la flota (con su 2D)
            public bool Past;              // cumplido o caducado: la fila va apagada
            public bool Now;               // la franja de salida está abierta ahora
            public bool NextDay;           // la franja acaba al día siguiente («+1»)
        }

        public string Title = "SALIDAS PROGRAMADAS", Hint = "";
        public string[] Heads = { "SALIDA", "DESTINO · RECORRIDO", "TREN", "ESTADO", "PRIMA" };
        public string LblAnyPax = "cualquier tren de viajeros", LblAnyFreight = "cualquier tren de mercancías", LblAny = "cualquier tren", LblDrawing = "dibujando…", LblNoImage = "sin composición 2D", LblNoTrain = "sin tren asignado";
        // La imagen 2D de un tren (ruta del archivo PNG/JPG) o null; se pide una vez por fila.
        public Func<Row, Task<string>> ImageLoader;

        static readonly Color Amber = Color.FromArgb(255, 196, 64), AmberDim = Color.FromArgb(150, 116, 40), BoardBg = Color.FromArgb(11, 12, 13),
                              Line = Color.FromArgb(24, 27, 29), Grey = Color.FromArgb(138, 143, 148);
        readonly Font _fH = Theme.Font(7.75f, FontStyle.Bold), _fTitle = Theme.Font(9f, FontStyle.Bold),
                      _fClock = new Font("Consolas", 15f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold),
                      _fTime = new Font("Consolas", 19f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold),
                      _fSmallMono = new Font("Consolas", 10f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold), _fUntil = Theme.Font(8f),
                      _fRoute = new Font("Consolas", 11.5f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold),
                      _fPath = Theme.Font(9f), _fNote = Theme.Font(8.25f, FontStyle.Italic), _fTrain = Theme.Font(8.25f, FontStyle.Bold),
                      _fFlap = new Font("Consolas", 10.5f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold), _fWho = Theme.Font(8f),
                      _fRight = Theme.Font(11f, FontStyle.Bold), _fRightS = Theme.Font(7.75f);
        readonly Dictionary<string, Bitmap> _img = new();          // fila (Id) → 2D
        readonly Dictionary<string, (int w, Bitmap bmp)> _fit = new();   // la 2D reducida al ancho de la vía (trenes largos), hecha una vez
        int _c0;                                                    // ancho de SALIDA: lo que ocupen la hora, «hasta …» y el día
        readonly HashSet<string> _asked = new(), _loading = new();
        readonly Timer _clock = new() { Interval = 1000 };


        public SchedBoard()
        {
            BackColor = BoardBg;
            _clock.Tick += (s, e) => { if (Visible && IsHandleCreated) Invalidate(new Rectangle(0, 0, ClientSize.Width, HeadH)); };
            _clock.Start();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _clock.Dispose();
                foreach (var f in new[] { _fH, _fTitle, _fClock, _fTime, _fSmallMono, _fUntil, _fRoute, _fPath, _fNote, _fTrain, _fFlap, _fWho, _fRight, _fRightS }) f.Dispose();
                foreach (var b in _img.Values) b?.Dispose();
                foreach (var f in _fit.Values) f.bmp?.Dispose();
                _img.Clear(); _fit.Clear();
            }
            base.Dispose(disposing);
        }

        protected override int MaxCols => 1;
        protected override int CardH => Theme.Px(104);
        protected override int Gap => 0;
        int HeadH => Theme.Px(64);
        protected override int TopOffset => HeadH;

        public void SetRows(IEnumerable<Row> rows)
        {
            var keep = new HashSet<string>();
            BeginUpdate();
            Items.Clear();
            foreach (var r in rows) { Items.Add(r); keep.Add(r.Id); }
            EndUpdate();
            // SALIDA a la medida de lo más ancho («hasta 23:30 +1», «ayer», «mañana»…): nunca con puntos suspensivos
            int c0 = Theme.Px(96);
            foreach (Row r in Items)
            {
                c0 = Math.Max(c0, TW(r.Time, _fTime));
                c0 = Math.Max(c0, UntilW(r));
                c0 = Math.Max(c0, TW((r.Now ? "● " : "") + r.Day.ToUpperInvariant(), _fH));
            }
            _c0 = c0 + Theme.Px(4);
            // las imágenes de filas que ya no están, fuera (las demás se conservan entre recargas)
            foreach (var k in new List<string>(_img.Keys)) if (!keep.Contains(k)) { _img[k]?.Dispose(); _img.Remove(k); _asked.Remove(k); DropFit(k); }
            Relayout();
        }

        // SALIDA · DESTINO/RECORRIDO · TREN (2D) · ESTADO · PRIMA
        static int TW(string s, Font f) => string.IsNullOrEmpty(s) ? 0 : TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        int UntilW(Row r) => TW(r.UntilLabel, _fUntil) + Theme.Px(5) + TW(r.UntilTime, _fSmallMono) + (r.NextDay ? Theme.Px(5) + TW("+1", _fH) + Theme.Px(8) : 0);
        void DropFit(string id) { if (_fit.TryGetValue(id, out var f)) { f.bmp?.Dispose(); _fit.Remove(id); } }

        // La 2D tal como se pinta: a su alto; si no cabe a lo ancho, una copia reducida (como en Servicios).
        Bitmap Fit(string id, Bitmap img, int maxW)
        {
            if (img == null || img.Width <= maxW) return img;
            if (_fit.TryGetValue(id, out var f) && f.w == maxW) return f.bmp;
            f.bmp?.Dispose();
            int h = Math.Max(1, (int)Math.Round(img.Height * (maxW / (double)img.Width)));
            var bmp = new Bitmap(maxW, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(img, new Rectangle(0, 0, maxW, h));
            }
            _fit[id] = (maxW, bmp);
            return bmp;
        }

        int[] Cols5(int w)
        {
            int gaps = Theme.Px(GapPx) * 4 + Theme.Px(PadPx) * 2;
            int t = Math.Max(Theme.Px(96), _c0), st = Theme.Px(150), pr = Theme.Px(120);
            int rest = Math.Max(Theme.Px(320), w - t - st - pr - gaps);
            int route = Math.Max(Theme.Px(170), Math.Min(Theme.Px(330), (int)(rest * 0.42)));
            return new[] { t, route, rest - route, st, pr };
        }

        // Las cinco columnas de una fila (y de la cabecera, con el mismo ancho): x y ancho de cada una; la última llega
        // hasta el margen derecho, igual que el izquierdo.
        const int PadPx = 16, GapPx = 14;
        (int[] x, int[] w) Columns(int left, int width)
        {
            var cw = Cols5(width);
            int[] xs = new int[5], ws = new int[5];
            int x = left + Theme.Px(PadPx);
            for (int k = 0; k < 5; k++) { xs[k] = x; ws[k] = cw[k]; x += cw[k] + Theme.Px(GapPx); }
            ws[4] = Math.Max(Theme.Px(40), left + width - Theme.Px(PadPx) - xs[4]);
            return (xs, ws);
        }

        // Rejilla común a todas las columnas, centrada en la fila: banda A (lo principal: hora, ruta, tren, estado, prima)
        // y banda B debajo, en dos medias líneas (B1 y B2) o entera (la composición 2D).
        static int BandA => Theme.Px(30);
        static int BandGap => Theme.Px(6);
        static int BandB => Theme.Px(44);

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Items.Count > 0) { base.OnPaint(e); return; }
            var g = e.Graphics;
            g.Clear(BackColor);
            PaintHead(g);
            if (!string.IsNullOrEmpty(EmptyText))
                TextRenderer.DrawText(g, EmptyText, FMsg, new Rectangle(Theme.Px(20), HeadH + Theme.Px(30), ClientSize.Width - Theme.Px(40), Theme.Px(60)), Grey, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        }

        protected override void PaintCard(Graphics g, int i, Rectangle rc, bool sel, bool hov)
        {
            if (rc.Bottom < HeadH) return;
            if (Items[i] is not Row r) return;
            var bg = sel ? Color.FromArgb(28, 52, 32) : hov ? Color.FromArgb(22, 24, 26) : (i % 2 == 0 ? BoardBg : Color.FromArgb(15, 17, 18));
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, rc);
            // marca a la izquierda: la elegida (verde) o la que sale ahora (verde claro)
            if (sel || r.Now) using (var b = new SolidBrush(sel ? Theme.Accent : Color.FromArgb(120, 120, 220, 120))) g.FillRectangle(b, rc.X, rc.Y, Theme.Px(3), rc.Height);
            using (var p = new Pen(Line)) g.DrawLine(p, rc.X, rc.Bottom - 1, rc.Right, rc.Bottom - 1);
            var (cx, cw) = Columns(rc.X, rc.Width);
            Color dim(Color c) => r.Past ? Color.FromArgb(c.R * 55 / 100, c.G * 55 / 100, c.B * 55 / 100) : c;

            int yA = rc.Y + (rc.Height - BandA - BandGap - BandB) / 2, yB = yA + BandA + BandGap, half = BandB / 2;
            Rectangle A(int k) => new Rectangle(cx[k], yA, cw[k], BandA);
            Rectangle B1(int k) => new Rectangle(cx[k], yB, cw[k], half);
            Rectangle B2(int k) => new Rectangle(cx[k], yB + half, cw[k], BandB - half);

            // separadores finos entre columnas, a la altura de la rejilla
            using (var p = new Pen(Color.FromArgb(30, 33, 36)))
                for (int k = 1; k < 5; k++)
                {
                    int sx = cx[k] - Theme.Px(GapPx) / 2 - 1;
                    g.DrawLine(p, sx, yA + Theme.Px(2), sx, yB + BandB - Theme.Px(2));
                }

            // SALIDA: A la hora · B1 «hasta» con su hora (y «+1») · B2 el día
            TextRenderer.DrawText(g, r.Time, _fTime, A(0), dim(Amber), L1);
            {
                var u = B1(0);
                int lw = TW(r.UntilLabel, _fUntil);
                TextRenderer.DrawText(g, r.UntilLabel, _fUntil, new Rectangle(u.X, u.Y, lw + Theme.Px(2), u.Height), dim(Grey), L1);
                int ux = u.X + lw + Theme.Px(5), tw = TW(r.UntilTime, _fSmallMono);
                TextRenderer.DrawText(g, r.UntilTime, _fSmallMono, new Rectangle(ux, u.Y, tw + Theme.Px(2), u.Height), dim(Color.FromArgb(232, 178, 64)), L1);
                ux += tw + Theme.Px(5);
                if (r.NextDay)
                {
                    int ph = Theme.Px(14);
                    var pr1 = new Rectangle(ux, u.Y + (u.Height - ph) / 2, TW("+1", _fH) + Theme.Px(8), ph);
                    g.SmoothingMode = SmoothingMode.AntiAlias; Theme.FillRound(g, pr1, Theme.Px(3), Color.FromArgb(r.Past ? 30 : 46, 255, 196, 64)); g.SmoothingMode = SmoothingMode.None;
                    TextRenderer.DrawText(g, "+1", _fH, pr1, dim(Amber), C1);
                }
            }
            TextRenderer.DrawText(g, (r.Now ? "● " : "") + r.Day.ToUpperInvariant(), _fH, B2(0), r.Now ? Color.FromArgb(120, 220, 120) : dim(Grey), L1);

            // DESTINO · RECORRIDO: A la ruta · B1 el recorrido · B2 la nota
            TextRenderer.DrawText(g, r.Route.ToUpperInvariant(), _fRoute, A(1), dim(Color.White), L1);
            TextRenderer.DrawText(g, "→ " + r.Path, _fPath, B1(1), dim(Color.FromArgb(207, 211, 214)), L1);
            if (r.Note.Length > 0) TextRenderer.DrawText(g, "«" + r.Note + "»", _fNote, B2(1), dim(AmberDim), L1);

            // TREN: A el nombre · B entera la composición 2D en la franja de Servicios (sin tren concreto, el aviso)
            string anyKind = r.Kind == "pax" ? LblAnyPax : r.Kind == "freight" ? LblAnyFreight : LblAny;
            TextRenderer.DrawText(g, r.HasTrain ? r.Train : anyKind, _fTrain, A(2), dim(r.HasTrain ? Color.White : Grey), L1);
            var track = new Rectangle(cx[2], yB, cw[2], BandB);
            Bitmap bmp = null;
            if (r.HasTrain && !_img.TryGetValue(r.Id, out bmp) && !_asked.Contains(r.Id)) RequestImage(r);
            bool loading = r.HasTrain && _loading.Contains(r.Id);
            string empty = loading ? LblDrawing : bmp == null ? "🚆  " + (r.HasTrain ? LblNoImage : LblNoTrain) : null;
            Strip(g, track, Fit(r.Id, bmp, track.Width - Theme.Px(16)), empty, _fWho);
            if (r.Past)   // cumplido o caducado: apagado
                using (var b = new SolidBrush(Color.FromArgb(120, BoardBg))) g.FillRectangle(b, track);

            // ESTADO: A la paleta tipo teleindicador · B1 quién
            Flap(g, A(3), r.StateText.ToUpperInvariant(), r.StateColor, r.Past);
            if (r.Who.Length > 0) TextRenderer.DrawText(g, r.Who, _fWho, B1(3), Grey, C1);

            // PRIMA: A la cifra · B1 de dónde sale (a la derecha, con el mismo margen que la izquierda)
            TextRenderer.DrawText(g, r.Right, _fRight, A(4), r.State == "done" ? Color.FromArgb(245, 197, 66) : dim(Color.White), R1);
            TextRenderer.DrawText(g, r.RightSub, _fRightS, B1(4), Grey, R1);
        }

        // La franja de la composición como en Servicios (ServiceCards): fondo oscuro redondeado y el tren a la izquierda,
        // centrado en alto y, si no cabe, reducido; sin imagen, el aviso a la izquierda.
        public static void Strip(Graphics g, Rectangle sr, Bitmap img, string emptyText, Font f)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(g, sr, Theme.Px(6), CardPaint.Rail);
            g.SmoothingMode = SmoothingMode.None;
            if (img != null)
            {
                int w = img.Width, h = img.Height, maxW = sr.Width - Theme.Px(16), maxH = sr.Height - Theme.Px(4);
                if (w > maxW) { h = Math.Max(1, h * maxW / w); w = maxW; }
                if (h > maxH) { w = Math.Max(1, w * maxH / h); h = maxH; }
                g.InterpolationMode = w == img.Width ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(img, new Rectangle(sr.X + Theme.Px(8), sr.Y + (sr.Height - h) / 2, w, h));
                g.PixelOffsetMode = PixelOffsetMode.Default;
            }
            else if (!string.IsNullOrEmpty(emptyText))
                TextRenderer.DrawText(g, emptyText, f, new Rectangle(sr.X + Theme.Px(10), sr.Y, sr.Width - Theme.Px(20), sr.Height), Theme.Subtle,
                                      TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        // Paleta: dos medias placas con la rendija en medio, como los indicadores de lamas.
        void Flap(Graphics g, Rectangle r, string text, Color c, bool past)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var top = new Rectangle(r.X, r.Y, r.Width, r.Height / 2);
            var bot = new Rectangle(r.X, r.Y + r.Height / 2, r.Width, r.Height - r.Height / 2);
            Color face = Color.FromArgb(past ? 26 : 30, past ? 28 : 33, past ? 30 : 36);
            Theme.FillRound(g, r, Theme.Px(4), face);
            using (var b = new SolidBrush(Color.FromArgb(10, 255, 255, 255))) g.FillRectangle(b, top.X + 1, top.Y + 1, top.Width - 2, top.Height - 1);
            g.SmoothingMode = SmoothingMode.None;
            // la rendija entre las dos lamas, detrás del rótulo (si no, parece tachado)
            using (var p = new Pen(Color.FromArgb(14, 15, 16), Math.Max(1, Theme.Px(1)))) g.DrawLine(p, r.X + 1, bot.Y, r.Right - 2, bot.Y);
            TextRenderer.DrawText(g, text, _fFlap, r, past ? Color.FromArgb(c.R * 60 / 100, c.G * 60 / 100, c.B * 60 / 100) : c, C1);
            // bisagras
            using (var b = new SolidBrush(Color.FromArgb(60, 64, 68)))
            {
                g.FillRectangle(b, r.X - Theme.Px(2), bot.Y - Theme.Px(2), Theme.Px(3), Theme.Px(4));
                g.FillRectangle(b, r.Right - 1, bot.Y - Theme.Px(2), Theme.Px(3), Theme.Px(4));
            }
        }

        async void RequestImage(Row r)
        {
            if (ImageLoader == null || r.Id == null) return;
            _asked.Add(r.Id); _loading.Add(r.Id);
            Bitmap bmp = null;
            try
            {
                string file = await ImageLoader(r);
                if (!string.IsNullOrEmpty(file)) bmp = await Task.Run(() => VehicleThumbs.Decode(file, Theme.Px(40)));   // como en Servicios: franja de 44, tren de 40
            }
            catch { }
            if (IsDisposed) { bmp?.Dispose(); return; }
            _loading.Remove(r.Id);
            if (_img.TryGetValue(r.Id, out var old)) old?.Dispose();
            DropFit(r.Id);
            _img[r.Id] = bmp;
            Invalidate();
        }

        protected override void AfterPaint(Graphics g, int first, int last) => PaintHead(g);

        void PaintHead(Graphics g)
        {
            var hr = new Rectangle(0, 0, ClientSize.Width, HeadH);
            using (var b = new SolidBrush(BoardBg)) g.FillRectangle(b, hr);
            int x = Theme.Px(2 + PadPx);
            // rótulo con el pictograma del tren y reloj de andén
            TextRenderer.DrawText(g, "🚆  " + Title, _fTitle, new Rectangle(x, Theme.Px(8), hr.Width / 2, Theme.Px(22)), Amber, L1);
            string clock = DateTime.Now.ToString("HH:mm:ss");
            int cwid = TextRenderer.MeasureText(clock, _fClock, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.Px(16);
            var cr = new Rectangle(hr.Right - Theme.Px(2 + PadPx) - cwid, Theme.Px(6), cwid, Theme.Px(26));
            g.SmoothingMode = SmoothingMode.AntiAlias; Theme.FillRound(g, cr, Theme.Px(4), Color.FromArgb(24, 22, 14)); g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, clock, _fClock, cr, Amber, C1);
            if (Hint.Length > 0) TextRenderer.DrawText(g, Hint, _fH, new Rectangle(hr.Width / 2, Theme.Px(8), cr.X - hr.Width / 2 - Theme.Px(12), Theme.Px(22)), Grey, R1);
            // los rótulos, en las mismas columnas que las filas (mismo ancho y márgenes) y alineados como su contenido
            var (cx, cw) = Columns(Theme.Px(2), ClientSize.Width - Theme.Px(4));
            for (int i = 0; i < Heads.Length && i < 5; i++)
                TextRenderer.DrawText(g, Heads[i], _fH, new Rectangle(cx[i], Theme.Px(40), cw[i], Theme.Px(18)), Grey, i == 3 ? C1 : i == 4 ? R1 : L1);
            using (var p = new Pen(Color.FromArgb(36, 39, 42))) g.DrawLine(p, 0, HeadH - 1, hr.Width, HeadH - 1);
        }
    }
}
