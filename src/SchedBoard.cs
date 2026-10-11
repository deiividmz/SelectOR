// Servicios programados como un teleindicador de estación (Empresas → Servicios → «Programados»): reloj arriba,
// hora de salida en ámbar, ruta y recorrido, la composición 2D del tren asignado como en Flota (o la silueta de
// «cualquier tren de viajeros / mercancías») y el estado en una paleta como las de los paneles de salidas.

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
            public string Id, Time = "", Until = "", Day = "", Route = "", Path = "", Note = "", Train = "", Kind = "";   // Kind: pax | freight | "" (cualquiera)
            public string State = "", StateText = "", Who = "", Right = "", RightSub = "";
            public Color StateColor = Color.Gray;
            public bool HasTrain;          // tren concreto de la flota (con su 2D)
            public bool Past;              // cumplido o caducado: la fila va apagada
            public bool Now;               // la franja de salida está abierta ahora
        }

        public string Title = "SALIDAS PROGRAMADAS", Hint = "";
        public string[] Heads = { "SALIDA", "DESTINO · RECORRIDO", "TREN", "ESTADO", "PRIMA" };
        public string LblAnyPax = "cualquier tren de viajeros", LblAnyFreight = "cualquier tren de mercancías", LblAny = "cualquier tren", LblDrawing = "dibujando…";
        // La imagen 2D de un tren (ruta del archivo PNG/JPG) o null; se pide una vez por fila.
        public Func<Row, Task<string>> ImageLoader;

        static readonly Color Amber = Color.FromArgb(255, 196, 64), AmberDim = Color.FromArgb(150, 116, 40), BoardBg = Color.FromArgb(11, 12, 13),
                              Line = Color.FromArgb(24, 27, 29), Grey = Color.FromArgb(138, 143, 148);
        readonly Font _fH = Theme.Font(7.75f, FontStyle.Bold), _fTitle = Theme.Font(9f, FontStyle.Bold),
                      _fClock = new Font("Consolas", 15f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold),
                      _fTime = new Font("Consolas", 19f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold),
                      _fSmallMono = new Font("Consolas", 8.5f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold),
                      _fRoute = new Font("Consolas", 11.5f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold),
                      _fPath = Theme.Font(9f), _fNote = Theme.Font(8.25f, FontStyle.Italic), _fTrain = Theme.Font(8.25f, FontStyle.Bold),
                      _fFlap = new Font("Consolas", 10.5f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold), _fWho = Theme.Font(8f),
                      _fRight = Theme.Font(11f, FontStyle.Bold), _fRightS = Theme.Font(7.75f);
        readonly Dictionary<string, Bitmap> _img = new();          // fila (Id) → 2D
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
                foreach (var f in new[] { _fH, _fTitle, _fClock, _fTime, _fSmallMono, _fRoute, _fPath, _fNote, _fTrain, _fFlap, _fWho, _fRight, _fRightS }) f.Dispose();
                foreach (var b in _img.Values) b?.Dispose();
                _img.Clear();
            }
            base.Dispose(disposing);
        }

        protected override int MaxCols => 1;
        protected override int CardH => Theme.Px(96);
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
            // las imágenes de filas que ya no están, fuera (las demás se conservan entre recargas)
            foreach (var k in new List<string>(_img.Keys)) if (!keep.Contains(k)) { _img[k]?.Dispose(); _img.Remove(k); _asked.Remove(k); }
            Relayout();
        }

        // SALIDA · DESTINO/RECORRIDO · TREN (2D) · ESTADO · PRIMA
        int[] Cols5(int w)
        {
            int gaps = Theme.Px(14) * 4 + Theme.Px(28);
            int t = Theme.Px(96), st = Theme.Px(150), pr = Theme.Px(120);
            int rest = Math.Max(Theme.Px(320), w - t - st - pr - gaps);
            int route = Math.Max(Theme.Px(170), Math.Min(Theme.Px(330), (int)(rest * 0.42)));
            return new[] { t, route, rest - route, st, pr };
        }

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
            if (sel) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, rc.X, rc.Y, Theme.Px(3), rc.Height);
            using (var p = new Pen(Line)) g.DrawLine(p, rc.X, rc.Bottom - 1, rc.Right, rc.Bottom - 1);
            var cw = Cols5(rc.Width);
            int x = rc.X + Theme.Px(14), gap = Theme.Px(14);
            Color dim(Color c) => r.Past ? Color.FromArgb(c.R * 55 / 100, c.G * 55 / 100, c.B * 55 / 100) : c;

            // SALIDA: hora grande en ámbar, «hasta» y el día
            TextRenderer.DrawText(g, r.Time, _fTime, new Rectangle(x, rc.Y + Theme.Px(10), cw[0], Theme.Px(30)), dim(Amber), L1);
            TextRenderer.DrawText(g, r.Until, _fSmallMono, new Rectangle(x, rc.Y + Theme.Px(42), cw[0], Theme.Px(16)), dim(AmberDim), L1);
            TextRenderer.DrawText(g, (r.Now ? "● " : "") + r.Day.ToUpperInvariant(), _fH, new Rectangle(x, rc.Y + Theme.Px(62), cw[0], Theme.Px(16)), r.Now ? Color.FromArgb(120, 220, 120) : dim(Grey), L1);
            x += cw[0] + gap;

            // DESTINO · RECORRIDO (y la nota)
            TextRenderer.DrawText(g, r.Route.ToUpperInvariant(), _fRoute, new Rectangle(x, rc.Y + Theme.Px(12), cw[1], Theme.Px(22)), dim(Color.White), L1);
            TextRenderer.DrawText(g, "→ " + r.Path, _fPath, new Rectangle(x, rc.Y + Theme.Px(38), cw[1], Theme.Px(18)), dim(Color.FromArgb(207, 211, 214)), L1);
            if (r.Note.Length > 0) TextRenderer.DrawText(g, "«" + r.Note + "»", _fNote, new Rectangle(x, rc.Y + Theme.Px(60), cw[1], Theme.Px(18)), dim(AmberDim), L1);
            x += cw[1] + gap;

            // TREN: nombre y su composición 2D sobre la vía
            var tr = new Rectangle(x, rc.Y + Theme.Px(8), cw[2], rc.Height - Theme.Px(16));
            string tname = r.HasTrain ? r.Train : r.Kind == "pax" ? LblAnyPax : r.Kind == "freight" ? LblAnyFreight : LblAny;
            TextRenderer.DrawText(g, tname, _fTrain, new Rectangle(tr.X, tr.Y, tr.Width, Theme.Px(16)), dim(r.HasTrain ? Color.White : Grey), L1);
            // la composición 2D como en Flota y Conducción libre (CardPaint.Rail_: fondo gris, carril y el tren encima)
            var track = new Rectangle(tr.X, tr.Y + Theme.Px(20), tr.Width, tr.Height - Theme.Px(20));
            Bitmap bmp = null;
            if (r.HasTrain && !_img.TryGetValue(r.Id, out bmp) && !_asked.Contains(r.Id)) RequestImage(r);
            bool loading = r.HasTrain && _loading.Contains(r.Id);
            CardPaint.Rail_(g, track, bmp, loading ? LblDrawing : null, _fWho);
            if (bmp == null && !loading)
                Silhouette(g, new Rectangle(track.X + Theme.Px(10), track.Y + Theme.Px(4), track.Width - Theme.Px(20), track.Height - Theme.Px(12)), r.Kind, r.Past);
            if (r.Past)   // cumplido o caducado: apagado
                using (var b = new SolidBrush(Color.FromArgb(120, BoardBg))) g.FillRectangle(b, track);
            x += cw[2] + gap;

            // ESTADO: paleta tipo teleindicador (dos medias placas) y quién
            var fl = new Rectangle(x, rc.Y + Theme.Px(14), cw[3], Theme.Px(34));
            Flap(g, fl, r.StateText.ToUpperInvariant(), r.StateColor, r.Past);
            if (r.Who.Length > 0) TextRenderer.DrawText(g, r.Who, _fWho, new Rectangle(x, fl.Bottom + Theme.Px(6), cw[3], Theme.Px(16)), Grey, C1);
            x += cw[3] + gap;

            // PRIMA
            var pr = new Rectangle(x, rc.Y, rc.Right - x - Theme.Px(14), rc.Height);
            TextRenderer.DrawText(g, r.Right, _fRight, new Rectangle(pr.X, rc.Y + Theme.Px(24), pr.Width, Theme.Px(22)), r.State == "done" ? Color.FromArgb(245, 197, 66) : dim(Color.White), R1);
            TextRenderer.DrawText(g, r.RightSub, _fRightS, new Rectangle(pr.X, rc.Y + Theme.Px(48), pr.Width, Theme.Px(16)), Grey, R1);
        }

        // Silueta de «cualquier tren»: locomotora y coches (con ventanas) o vagones tolva.
        public static void Silhouette(Graphics g, Rectangle a, string kind, bool past)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int h = Math.Min(a.Height, Theme.Px(30)), y = a.Bottom - h, x = a.X;
            Color body = past ? Color.FromArgb(52, 56, 60) : Color.FromArgb(78, 84, 90), win = past ? Color.FromArgb(40, 44, 48) : Color.FromArgb(150, 255, 196, 64);
            int locoW = Theme.Px(46), carW = Theme.Px(kind == "freight" ? 34 : 50), gapC = Theme.Px(3);
            int n = Math.Max(1, Math.Min(4, (a.Width - locoW) / (carW + gapC)));
            using var bb = new SolidBrush(body); using var wb = new SolidBrush(win); using var wheel = new SolidBrush(Color.FromArgb(30, 32, 34));
            // locomotora
            using (var p = new GraphicsPath())
            {
                var lr = new Rectangle(x, y + Theme.Px(3), locoW, h - Theme.Px(8));
                p.AddLine(lr.X + Theme.Px(10), lr.Y, lr.Right, lr.Y);
                p.AddLine(lr.Right, lr.Y, lr.Right, lr.Bottom);
                p.AddLine(lr.Right, lr.Bottom, lr.X, lr.Bottom);
                p.AddLine(lr.X, lr.Bottom, lr.X, lr.Y + Theme.Px(10));
                p.CloseFigure();
                g.FillPath(bb, p);
                g.FillRectangle(wb, lr.X + Theme.Px(4), lr.Y + Theme.Px(4), Theme.Px(8), Theme.Px(6));
            }
            Wheels(g, wheel, x, locoW, a.Bottom);
            x += locoW + gapC;
            for (int k = 0; k < n; k++, x += carW + gapC)
            {
                if (kind == "freight")
                {
                    // tolva: trapecio
                    using var p = new GraphicsPath();
                    int top = y + Theme.Px(6), bot = a.Bottom - Theme.Px(5);
                    p.AddPolygon(new[] { new Point(x, top), new Point(x + carW, top), new Point(x + carW - Theme.Px(6), bot), new Point(x + Theme.Px(6), bot) });
                    g.FillPath(bb, p);
                }
                else
                {
                    var cr = new Rectangle(x, y + Theme.Px(5), carW, h - Theme.Px(10));
                    Theme.FillRound(g, cr, Theme.Px(3), body);
                    for (int wx = cr.X + Theme.Px(5); wx + Theme.Px(6) < cr.Right - Theme.Px(3); wx += Theme.Px(9))
                        g.FillRectangle(wb, wx, cr.Y + Theme.Px(4), Theme.Px(6), Theme.Px(5));
                }
                Wheels(g, wheel, x, carW, a.Bottom);
            }
            g.SmoothingMode = SmoothingMode.None;
        }

        static void Wheels(Graphics g, Brush b, int x, int w, int bottom)
        {
            int d = Theme.Px(7);
            g.FillEllipse(b, x + Theme.Px(4), bottom - d, d, d);
            g.FillEllipse(b, x + w - Theme.Px(4) - d, bottom - d, d, d);
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
                if (!string.IsNullOrEmpty(file)) bmp = await Task.Run(() => VehicleThumbs.Decode(file, Theme.Px(60)));
            }
            catch { }
            if (IsDisposed) { bmp?.Dispose(); return; }
            _loading.Remove(r.Id);
            if (_img.TryGetValue(r.Id, out var old)) old?.Dispose();
            _img[r.Id] = bmp;
            Invalidate();
        }

        protected override void AfterPaint(Graphics g, int first, int last) => PaintHead(g);

        void PaintHead(Graphics g)
        {
            var hr = new Rectangle(0, 0, ClientSize.Width, HeadH);
            using (var b = new SolidBrush(BoardBg)) g.FillRectangle(b, hr);
            int x = Theme.Px(14);
            // rótulo con el pictograma del tren y reloj de andén
            TextRenderer.DrawText(g, "🚆  " + Title, _fTitle, new Rectangle(x, Theme.Px(8), hr.Width / 2, Theme.Px(22)), Amber, L1);
            string clock = DateTime.Now.ToString("HH:mm:ss");
            int cwid = TextRenderer.MeasureText(clock, _fClock, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.Px(16);
            var cr = new Rectangle(hr.Right - cwid - Theme.Px(14), Theme.Px(6), cwid, Theme.Px(26));
            g.SmoothingMode = SmoothingMode.AntiAlias; Theme.FillRound(g, cr, Theme.Px(4), Color.FromArgb(24, 22, 14)); g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, clock, _fClock, cr, Amber, C1);
            if (Hint.Length > 0) TextRenderer.DrawText(g, Hint, _fH, new Rectangle(hr.Width / 2, Theme.Px(8), cr.X - hr.Width / 2 - Theme.Px(12), Theme.Px(22)), Grey, R1);
            var cw = Cols5(hr.Width - Theme.Px(4));
            int gap = Theme.Px(14);
            for (int i = 0; i < Heads.Length && i < cw.Length; i++)
            {
                int w = i == 4 ? hr.Width - x - Theme.Px(16) : cw[i];   // la prima, pegada a la derecha como en las filas
                TextRenderer.DrawText(g, Heads[i], _fH, new Rectangle(x, Theme.Px(40), w, Theme.Px(18)), Grey, i == 3 ? C1 : i == 4 ? R1 : L1);
                x += cw[i] + gap;
            }
            using (var p = new Pen(Color.FromArgb(36, 39, 42))) g.DrawLine(p, 0, HeadH - 1, hr.Width, HeadH - 1);
        }
    }
}
