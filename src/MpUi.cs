// MULTIJUGADOR: elección de modo en tarjetas, servidores públicos en tarjetas y las ventanas de la
// contraseña (servidores-contrasena.sql). Dibujado a mano, solo lo visible.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    // Tarjeta «CÓMO QUIERES JUGAR»: rótulo, las dos opciones (Alojar partida / Unirme a un servidor) y la nota
    // de debajo. Lo dibuja todo y calcula su alto con el ancho que tenga (NeededHeight): nada se recorta a
    // ninguna escala; si no caben lado a lado, las opciones se apilan.
    public class MpModeCards : Control
    {
        public int Selected { get; private set; }
        public event Action<int> Changed;
        public string Caption = "";
        string _info = "";
        public string Info { get => _info; set { if (_info == value) return; _info = value ?? ""; Invalidate(); } }
        readonly (string icon, string title, string sub)[] _opt;
        readonly Font _fCap = Theme.Font(8f, FontStyle.Bold), _fT = Theme.Font(10f, FontStyle.Bold), _fS = Theme.Font(8.25f), _fI = Theme.Font(8.5f), _fIc = EmojiPicker.EmojiFont(13f);
        int _hover = -1;
        const TextFormatFlags WrapF = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl;
        const TextFormatFlags LineF = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;

        public MpModeCards((string, string, string)[] options)
        {
            _opt = options;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void Dispose(bool disposing) { if (disposing) { _fCap.Dispose(); _fT.Dispose(); _fS.Dispose(); _fI.Dispose(); _fIc.Dispose(); } base.Dispose(disposing); }

        static int Pad => Theme.Px(14);
        bool Stacked(int w) => w < Theme.Px(470);
        int TextH(string t, Font f, int w) => string.IsNullOrEmpty(t) ? 0 : TextRenderer.MeasureText(t, f, new Size(Math.Max(10, w), 10000), WrapF).Height;

        // Rectángulos de las opciones y de la nota para un ancho dado.
        (Rectangle[] opts, Rectangle info) Geo(int w)
        {
            int pad = Pad, inner = w - pad * 2, gap = Theme.Px(10);
            int y = pad + _fCap.Height + Theme.Px(8);
            bool st = Stacked(w);
            int ow = st ? inner : (inner - gap) / 2;
            int textW = ow - Theme.Px(80);   // = ancho del texto al pintar (icono 40 + márgenes)
            int oh = 0;
            foreach (var (_, t, sub) in _opt) oh = Math.Max(oh, Theme.Px(14) + _fT.Height + Theme.Px(4) + TextH(sub, _fS, textW) + Theme.Px(14));
            oh = Math.Max(oh, Theme.Px(62));
            var r = new Rectangle[_opt.Length];
            for (int i = 0; i < _opt.Length; i++)
            {
                r[i] = st ? new Rectangle(pad, y, ow, oh) : new Rectangle(pad + i * (ow + gap), y, ow, oh);
                if (st) y += oh + gap;
            }
            if (!st) y += oh + gap;
            int ih = TextH(_info, _fI, inner);
            return (r, new Rectangle(pad, y + Theme.Px(2), inner, ih));
        }

        // Alto que necesita la tarjeta con este ancho.
        public int NeededHeight(int w) { var (_, info) = Geo(w); return info.Bottom + Pad; }

        public void Select(int i, bool raise = true) { if (Selected == i) return; Selected = i; Invalidate(); if (raise) Changed?.Invoke(i); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            var card = new Rectangle(0, 0, Width - 1, Height - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(g, card, Theme.Px(12), Theme.Surface);
            g.SmoothingMode = SmoothingMode.None;
            int pad = Pad;
            TextRenderer.DrawText(g, Caption, _fCap, new Rectangle(pad, pad, Width - pad * 2, _fCap.Height), Theme.Subtle, LineF);
            var (opts, info) = Geo(Width);
            var icons = new List<(string, Rectangle)>();
            for (int i = 0; i < _opt.Length; i++)
            {
                var r = opts[i]; bool on = i == Selected;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.FillRound(g, r, Theme.Px(10), on ? Color.FromArgb(40, 60, 46) : i == _hover ? Color.FromArgb(60, 64, 68) : Color.FromArgb(50, 54, 58));
                if (on) Theme.DrawRoundBorder(g, Rectangle.Inflate(r, -1, -1), Theme.Px(10), Theme.Accent, 1.5f);
                var ic = new Rectangle(r.X + Theme.Px(14), r.Y + (r.Height - Theme.Px(40)) / 2, Theme.Px(40), Theme.Px(40));
                using (var b = new SolidBrush(on ? Color.FromArgb(60, Theme.Accent) : Color.FromArgb(40, 255, 255, 255))) g.FillEllipse(b, ic);
                g.SmoothingMode = SmoothingMode.None;
                icons.Add((_opt[i].icon, ic));
                // radio a la derecha
                int rd = Theme.Px(16);
                var rr = new Rectangle(r.Right - rd - Theme.Px(12), r.Y + Theme.Px(12), rd, rd);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var p = new Pen(on ? Theme.Accent : Theme.Subtle, 1.6f)) g.DrawEllipse(p, rr);
                if (on) using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, Rectangle.Inflate(rr, -Theme.Px(4), -Theme.Px(4)));
                g.SmoothingMode = SmoothingMode.None;
                int tx = ic.Right + Theme.Px(12), tw = rr.X - tx - Theme.Px(6);
                int th = _fT.Height + Theme.Px(4) + TextH(_opt[i].sub, _fS, r.Right - Theme.Px(14) - tx);
                int ty = r.Y + Math.Max(Theme.Px(10), (r.Height - th) / 2);
                TextRenderer.DrawText(g, _opt[i].title, _fT, new Rectangle(tx, ty, tw, _fT.Height), on ? Theme.AccentHi : Theme.Text, LineF);
                TextRenderer.DrawText(g, _opt[i].sub, _fS, new Rectangle(tx, ty + _fT.Height + Theme.Px(4), r.Right - Theme.Px(14) - tx, r.Bottom - ty - _fT.Height), Theme.Subtle, WrapF);
            }
            ColorText.DrawCells(g, icons, _fIc, Theme.Text, ClientRectangle);
            TextRenderer.DrawText(g, _info, _fI, info, Theme.Subtle, WrapF);
        }
        int HitOpt(Point p) { var (o, _) = Geo(Width); for (int i = 0; i < o.Length; i++) if (o[i].Contains(p)) return i; return -1; }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = HitOpt(e.Location); Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; if (h != _hover) { _hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); int h = HitOpt(e.Location); if (h >= 0) Select(h); }
    }

    public class ServerCardGrid : CanvasPanel
    {
        public sealed class Item
        {
            public GameServer S;
            public string Route = "", Names = "";
            public int Players;
            public bool HaveRoute;
            public string LockOwner;          // null = nadie de SelectOR lo protege ahora
            public bool Protected; public DateTime LockSince;
            public string Key => (S?.Ip + ":" + S?.Port).ToLowerInvariant();
        }
        public List<Item> Items = new List<Item>();
        public Func<string, Image> ThumbOf;   // imagen de la ruta (si la tienes)
        public string SelectedKey { get; private set; }
        public event Action<Item> Selected, Join;
        public string LblHave = "✓ la tienes", LblNoRoute = "Ruta que no tienes en tu contenido", LblNoPlayers = "sin jugadores", LblJoin = "Unirme", LblWaiting = "Sin ruta todavía",
                      LblFree = "🔓 Libre", LblLocked = "🔒 Protegido por {0} · desde las {1}", LblOpen = "🔓 Abierto por {0} · sin contraseña";
        Font _fT, _fIp, _fChip, _fChipB, _fS, _fBtn, _fAge, _fImg;
        readonly List<(Item it, Rectangle r)> _cards = new();
        string _hover; bool _hoverBtn;

        public ServerCardGrid()
        {
            _fT = F(11f, FontStyle.Bold); _fIp = new Font("Consolas", 9f * Theme.UiScale * Theme.DpiComp); _fChip = F(8.25f); _fChipB = F(8.25f, FontStyle.Bold);
            _fS = F(8.5f); _fBtn = F(9f, FontStyle.Bold); _fAge = F(8f); _fImg = F(8f);
            BackColor = Theme.Bg;
        }
        protected override void Dispose(bool disposing) { if (disposing) _fIp.Dispose(); base.Dispose(disposing); }

        protected override int DoLayout(int w)
        {
            _cards.Clear();
            int gap = Theme.Px(12), cols = Math.Max(1, (w + gap) / (Theme.Px(440) + gap)), cw = (w - gap * (cols - 1)) / cols, ch = Theme.Px(128), y = Theme.Px(2);
            for (int i = 0; i < Items.Count; i++)
            {
                int c = i % cols; if (c == 0 && i > 0) y += ch + gap;
                _cards.Add((Items[i], new Rectangle(c * (cw + gap), y, cw, ch)));
            }
            return Items.Count == 0 ? 0 : y + ch + Theme.Px(6);
        }

        Rectangle BtnRect(Rectangle r) { int bw = Theme.Px(96), bh = Theme.Px(30); return new Rectangle(r.Right - bw - Theme.Px(12), r.Bottom - bh - Theme.Px(12), bw, bh); }

        protected override void DoPaint(Graphics g, int top)
        {
            foreach (var (it, r0) in _cards)
            {
                var r = r0; r.Offset(0, -top);
                if (r.Bottom < 0 || r.Y > ClientSize.Height) continue;
                bool sel = it.Key == SelectedKey, hov = it.Key == _hover;
                Round(g, r, Theme.Px(12), hov || sel ? Color.FromArgb(58, 62, 66) : CardC);
                if (sel) Border(g, r, Theme.Px(12), Theme.Accent, 1.5f);
                // imagen de la ruta
                var ir = new Rectangle(r.X + Theme.Px(12), r.Y + Theme.Px(12), Theme.Px(150), r.Height - Theme.Px(24));
                var img = it.HaveRoute ? ThumbOf?.Invoke(it.Route) : null;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Theme.Round(ir, Theme.Px(8)))
                {
                    var st = g.Save(); g.SetClip(path);
                    if (img != null)
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        double k = Math.Max(ir.Width / (double)img.Width, ir.Height / (double)img.Height);
                        int iw = (int)(img.Width * k), ih = (int)(img.Height * k);
                        g.DrawImage(img, ir.X + (ir.Width - iw) / 2, ir.Y + (ir.Height - ih) / 2, iw, ih);
                    }
                    else using (var b = new LinearGradientBrush(ir, it.HaveRoute ? Color.FromArgb(58, 90, 68) : Color.FromArgb(32, 35, 40), Color.FromArgb(28, 31, 36), LinearGradientMode.ForwardDiagonal)) g.FillRectangle(b, ir);
                    g.Restore(st);
                }
                g.SmoothingMode = SmoothingMode.None;
                bool noRoute = string.IsNullOrWhiteSpace(it.Route);
                if (noRoute) { using var gf = EmojiPicker.EmojiFont(22f); ColorText.DrawCells(g, new[] { ("🌐", ir) }, gf, Theme.Subtle, ir); }
                else if (!it.HaveRoute) Text(g, LblNoRoute, _fImg, Rectangle.Inflate(ir, -8, -8), Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                else
                {
                    int hw = TW(LblHave, _fAge) + Theme.Px(12);
                    var hr = new Rectangle(ir.X + Theme.Px(6), ir.Bottom - Theme.Px(24), hw, Theme.Px(18));
                    Round(g, hr, Theme.Px(5), Color.FromArgb(190, 20, 24, 22)); Text(g, LblHave, _fAge, hr, Theme.AccentHi, C1);
                }
                int x = ir.Right + Theme.Px(14), right = r.Right - Theme.Px(12), y = r.Y + Theme.Px(12);
                string age = it.S?.ReportedTime ?? "";
                int aw = TW(age, _fAge) + 4;
                Text(g, age, _fAge, new Rectangle(right - aw, y, aw, Theme.Px(18)), Theme.Subtle, R1);
                int d = Theme.Px(8);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(it.Players > 0 ? Theme.Accent : Theme.Subtle)) g.FillEllipse(b, x, y + Theme.Px(7), d, d);
                g.SmoothingMode = SmoothingMode.None;
                Text(g, noRoute ? LblWaiting : it.Route, _fT, new Rectangle(x + d + Theme.Px(8), y, right - aw - x - d - Theme.Px(16), Theme.Px(22)), noRoute ? Theme.Subtle : Theme.Text, L1);
                y += Theme.Px(24);
                Text(g, (it.S?.Ip ?? "") + " : " + (it.S?.Port ?? ""), _fIp, new Rectangle(x, y, right - x, Theme.Px(18)), Theme.Subtle, L1);
                y += Theme.Px(22);
                // jugadores en chips
                int cx = x, chH = Theme.Px(20);
                if (it.Players > 0)
                {
                    string cnt = "👤 " + it.Players;
                    int cw = TW(cnt, _fChipB) + Theme.Px(16);
                    Round(g, new Rectangle(cx, y, cw, chH), chH / 2, Color.FromArgb(48, 56, 82)); Text(g, cnt, _fChipB, new Rectangle(cx, y, cw, chH), Blue, C1);
                    cx += cw + Theme.Px(5);
                    foreach (var n in (it.Names ?? "").Split(new[] { '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string nn = n.Trim(); if (nn.Length == 0 || nn == "-") continue;
                        int nw = TW(nn, _fChip) + Theme.Px(16);
                        if (cx + nw > right - Theme.Px(4)) break;
                        Round(g, new Rectangle(cx, y, nw, chH), chH / 2, CardPaint.Rail); Text(g, nn, _fChip, new Rectangle(cx, y, nw, chH), Theme.Text, C1);
                        cx += nw + Theme.Px(5);
                    }
                }
                else
                {
                    int nw = TW(LblNoPlayers, _fChip) + Theme.Px(16);
                    Round(g, new Rectangle(cx, y, nw, chH), chH / 2, Theme.Surface2); Text(g, LblNoPlayers, _fChip, new Rectangle(cx, y, nw, chH), Theme.Subtle, C1);
                }
                y += chH + Theme.Px(8);
                // libre / protegido
                var br = BtnRect(r);
                string lockTxt = it.LockOwner == null ? LblFree
                               : it.Protected ? string.Format(LblLocked, it.LockOwner, it.LockSince.ToLocalTime().ToString("HH:mm"))
                               : string.Format(LblOpen, it.LockOwner);
                Color lc = it.LockOwner != null && it.Protected ? Gold : Theme.AccentHi;
                int lw = Math.Min(TW(lockTxt, _fS) + Theme.Px(16), br.X - x - Theme.Px(8));
                var lr = new Rectangle(x, y, lw, Theme.Px(22));
                Round(g, lr, Theme.Px(6), Color.FromArgb(30, lc)); Text(g, lockTxt, _fS, new Rectangle(lr.X + Theme.Px(8), lr.Y, lr.Width - Theme.Px(12), lr.Height), lc, L1);
                // botón
                bool bh = hov && _hoverBtn;
                Round(g, br, Theme.Px(8), it.HaveRoute ? (bh ? Theme.AccentHi : Theme.Accent) : (bh ? Theme.SurfaceHi : Theme.Surface2));
                Text(g, LblJoin, _fBtn, br, it.HaveRoute ? Color.White : Theme.Text, C1);
            }
        }

        (Item it, bool btn) Hit(Point p)
        {
            int y = p.Y + ScrollY;
            foreach (var (it, r) in _cards)
                if (r.Contains(p.X, y)) { var b = BtnRect(r); return (it, b.Contains(p.X, y)); }
            return (null, false);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var (it, b) = Hit(e.Location);
            Cursor = it != null ? Cursors.Hand : Cursors.Default;
            if (it?.Key != _hover || b != _hoverBtn) { _hover = it?.Key; _hoverBtn = b; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = null; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var (it, b) = Hit(e.Location);
            if (it == null) return;
            SelectedKey = it.Key; Invalidate(); Selected?.Invoke(it);
            if (b) Join?.Invoke(it);
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); var (it, _) = Hit(e.Location); if (it != null) Join?.Invoke(it); }
        public void SetItems(List<Item> items)
        {
            if (Sig(items) == Sig(Items) && EmptyText == _lastEmpty) return;   // nada nuevo: ni se toca
            _lastEmpty = EmptyText;
            Items = items; Relayout(true);
        }
        string _lastEmpty;
        static string Sig(List<Item> l)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var i in l) sb.Append(i.Key).Append('|').Append(i.Route).Append('|').Append(i.Names).Append('|').Append(i.Players).Append('|').Append(i.HaveRoute)
                                   .Append('|').Append(i.LockOwner).Append('|').Append(i.Protected).Append('|').Append(i.LockSince.Ticks).Append('|').Append(i.S?.ReportedTime).Append('\n');
            return sb.ToString();
        }
    }

    // Eres el primero: tu contraseña (copiar · otra · sin contraseña) y «Conectar».
    public class MpOwnerDialog : Form
    {
        public string Password { get; private set; }
        readonly Label _pw, _msg;
        public MpOwnerDialog(string server, string route, string password, Func<string, System.Threading.Tasks.Task<(string pw, string err)>> setMode)
        {
            Password = password;
            Text = I18n.T("Eres el primero en este servidor");
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Font(9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.Px(480), Theme.Px(300));
            var info = new Label { AutoSize = false, Bounds = new Rectangle(Theme.Px(20), Theme.Px(16), Theme.Px(440), Theme.Px(52)), ForeColor = Theme.Subtle,
                                   Text = string.Format(I18n.T("Mientras sigas conectado, los demás usuarios de SelectOR necesitarán esta contraseña para unirse a {0} ({1})."), route, server) };
            _pw = new Label { AutoSize = false, Bounds = new Rectangle(Theme.Px(20), Theme.Px(74), Theme.Px(440), Theme.Px(64)), TextAlign = ContentAlignment.MiddleCenter,
                              Font = new Font("Consolas", 24f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold), ForeColor = Theme.Gold, BackColor = Color.FromArgb(28, 31, 36) };
            _msg = new Label { AutoSize = false, Bounds = new Rectangle(Theme.Px(20), Theme.Px(194), Theme.Px(440), Theme.Px(40)), ForeColor = Theme.Subtle, Font = Theme.Font(8.5f),
                               Text = I18n.T("Al cerrar Open Rails la contraseña deja de valer y el siguiente en entrar pone una nueva.") };
            RoundButton Btn(string t, int x, int w, bool primary = false)
            {
                var b = new RoundButton { Text = t, Bounds = new Rectangle(x, Theme.Px(150), w, Theme.Px(34)), Radius = 8, FontSize = 9f, BaseColor = primary ? Theme.Accent : Theme.Surface2, HoverColor = primary ? Theme.AccentHi : Theme.SurfaceHi, TextColor = primary ? Color.White : Theme.Text };
                Controls.Add(b); return b;
            }
            var copy = Btn("📋  " + I18n.T("Copiar"), Theme.Px(20), Theme.Px(110));
            var other = Btn("↻  " + I18n.T("Otra contraseña"), Theme.Px(136), Theme.Px(150));
            var none = Btn(I18n.T("Sin contraseña"), Theme.Px(292), Theme.Px(168));
            var go = new RoundButton { Text = I18n.T("Conectar"), Bounds = new Rectangle(Theme.Px(300), Theme.Px(246), Theme.Px(160), Theme.Px(38)), Radius = 9, FontSize = 10f, FontStyle = FontStyle.Bold, BaseColor = Theme.Accent, HoverColor = Theme.AccentHi, TextColor = Color.White };
            var cancel = new RoundButton { Text = I18n.T("Cancelar"), Bounds = new Rectangle(Theme.Px(184), Theme.Px(246), Theme.Px(108), Theme.Px(38)), Radius = 9, FontSize = 10f, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text };
            Controls.AddRange(new Control[] { info, _pw, _msg, go, cancel });
            ShowPw();
            void ShowPw() => _pw.Text = Password ?? I18n.T("sin contraseña");
            copy.Click += (s, e) => { try { if (Password != null) Clipboard.SetText(Password); _msg.Text = I18n.T("Copiada. Pásasela a quien quieras que se una."); } catch { } };
            other.Click += async (s, e) => { var (pw, err) = await setMode("new"); if (err != null) _msg.Text = err; else { Password = pw; ShowPw(); } };
            none.Click += async (s, e) => { var (_, err) = await setMode("none"); if (err != null) _msg.Text = err; else { Password = null; ShowPw(); _msg.Text = I18n.T("Cualquiera de SelectOR podrá unirse sin contraseña mientras sigas conectado."); } };
            go.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            AcceptButton = null;
        }
    }

    // Servidor protegido: pedir la contraseña.
    public class MpPasswordDialog : Form
    {
        public MpPasswordDialog(string owner, string route, DateTime since, Func<string, System.Threading.Tasks.Task<(bool ok, string msg)>> check)
        {
            Text = I18n.T("Servidor protegido");
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Font(9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.Px(460), Theme.Px(230));
            var info = new Label { AutoSize = false, Bounds = new Rectangle(Theme.Px(20), Theme.Px(16), Theme.Px(420), Theme.Px(52)), ForeColor = Theme.Subtle,
                                   Text = string.Format(I18n.T("{0} está conectado a {1} desde las {2} y ha puesto contraseña. Pídesela para unirte."), owner, route, since.ToLocalTime().ToString("HH:mm")) };
            var input = new RoundedInput("000000") { Bounds = new Rectangle(Theme.Px(20), Theme.Px(76), Theme.Px(420), Theme.Px(40)) };
            input.Box.Font = new Font("Consolas", 14f * Theme.UiScale * Theme.DpiComp, FontStyle.Bold);
            input.Box.MaxLength = 6;                       // la contraseña es un número de 6 cifras
            input.Box.KeyPress += (s, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
            var msg = new Label { AutoSize = false, Bounds = new Rectangle(Theme.Px(20), Theme.Px(124), Theme.Px(420), Theme.Px(36)), ForeColor = Theme.Subtle, Font = Theme.Font(8.5f),
                                  Text = I18n.T("3 intentos por minuto.") };
            var go = new RoundButton { Text = I18n.T("Unirme"), Bounds = new Rectangle(Theme.Px(300), Theme.Px(172), Theme.Px(140), Theme.Px(38)), Radius = 9, FontSize = 10f, FontStyle = FontStyle.Bold, BaseColor = Theme.Accent, HoverColor = Theme.AccentHi, TextColor = Color.White };
            var cancel = new RoundButton { Text = I18n.T("Cancelar"), Bounds = new Rectangle(Theme.Px(184), Theme.Px(172), Theme.Px(108), Theme.Px(38)), Radius = 9, FontSize = 10f, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text };
            Controls.AddRange(new Control[] { info, input, msg, go, cancel });
            bool busy = false;
            async void Try()
            {
                if (busy) return;
                string t = input.Box.Text.Trim();
                if (t.Length == 0) { msg.Text = I18n.T("Escribe la contraseña."); msg.ForeColor = Color.FromArgb(229, 115, 115); return; }
                busy = true; msg.Text = I18n.T("Comprobando…"); msg.ForeColor = Theme.Subtle;
                var (ok, m) = await check(t);
                busy = false;
                if (IsDisposed) return;
                if (ok) { DialogResult = DialogResult.OK; Close(); return; }
                msg.Text = m; msg.ForeColor = Color.FromArgb(229, 115, 115);
                input.Box.SelectAll(); input.Box.Focus();
            }
            go.Click += (s, e) => Try();
            input.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Try(); } };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Shown += (s, e) => input.Box.Focus();
        }
    }
}
