// Servidores SelectOR (salas-multijugador.sql): las salas oficiales que crea el administrador, en tarjetas como las de
// los servidores públicos: imagen de la ruta, nombre, ruta y descripción, jugadores (con su barra de ocupación y sus
// nombres), distintivos (contraseña, quién puede entrar, carné mínimo, cerrada) y el botón «Entrar» (o el motivo por
// el que no puedes entrar).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public class RoomCardGrid : CanvasPanel
    {
        public sealed class Item
        {
            public string Id = "", Name = "", Description = "", Route = "", Access = "all", Denied, Host, Port;
            public int MaxPlayers, Players, MinPoints;
            public string[] Names = Array.Empty<string>();
            public bool HasPassword, Active = true, Mine, HaveRoute;
        }
        public List<Item> Items = new List<Item>();
        public Func<string, Image> ThumbOf;
        public string SelectedId { get; private set; }
        public event Action<Item> Selected, Join;
        public string LblJoin = "Entrar", LblInside = "Estás dentro", LblNoRoute = "Ruta que no tienes en tu contenido", LblHave = "✓ la tienes",
                      LblNoPlayers = "sin jugadores", LblPass = "🔒 Con contraseña", LblFree = "🔓 Sin contraseña", LblAll = "👥 Para todos",
                      LblCompanies = "🏢 Empresas autorizadas", LblUsers = "⭐ Por invitación", LblPoints = "🪪 Carné ≥ {0} puntos", LblClosed = "⛔ Cerrada", LblFull = "Llena";
        Font _fT, _fRoute, _fD, _fChip, _fChipB, _fBtn, _fCnt, _fIp, _fImg;
        readonly List<(Item it, Rectangle r)> _cards = new();
        string _hover; bool _hoverBtn;

        public RoomCardGrid()
        {
            _fT = F(11.5f, FontStyle.Bold); _fRoute = F(9f, FontStyle.Bold); _fD = F(8.5f); _fChip = F(8.25f); _fChipB = F(8.25f, FontStyle.Bold);
            _fBtn = F(9.5f, FontStyle.Bold); _fCnt = F(10f, FontStyle.Bold); _fIp = new Font("Consolas", 8.5f * Theme.UiScale * Theme.DpiComp); _fImg = F(8f);
            BackColor = Theme.Bg;
        }
        protected override void Dispose(bool disposing) { if (disposing) _fIp.Dispose(); base.Dispose(disposing); }

        protected override int DoLayout(int w)
        {
            _cards.Clear();
            int gap = Theme.Px(12), cols = Math.Max(1, (w + gap) / (Theme.Px(520) + gap)), cw = (w - gap * (cols - 1)) / cols, ch = Theme.Px(158), y = Theme.Px(2);
            for (int i = 0; i < Items.Count; i++)
            {
                int c = i % cols; if (c == 0 && i > 0) y += ch + gap;
                _cards.Add((Items[i], new Rectangle(c * (cw + gap), y, cw, ch)));
            }
            return Items.Count == 0 ? 0 : y + ch + Theme.Px(6);
        }

        Rectangle BtnRect(Rectangle r) { int bw = Theme.Px(112), bh = Theme.Px(34); return new Rectangle(r.Right - bw - Theme.Px(14), r.Bottom - bh - Theme.Px(14), bw, bh); }
        bool Full(Item it) => it.Players >= it.MaxPlayers && !it.Mine;

        protected override void DoPaint(Graphics g, int top)
        {
            foreach (var (it, r0) in _cards)
            {
                var r = r0; r.Offset(0, -top);
                if (r.Bottom < 0 || r.Y > ClientSize.Height) continue;
                bool sel = it.Id == SelectedId, hov = it.Id == _hover;
                Round(g, r, Theme.Px(14), hov || sel ? Color.FromArgb(58, 62, 66) : CardC);
                // franja de la sala: verde (puedes entrar), ámbar (contraseña), gris (no puedes o cerrada)
                Color band = !it.Active || it.Denied != null ? Color.FromArgb(110, 115, 120) : it.HasPassword ? Gold : Theme.Accent;
                using (var b = new SolidBrush(band)) g.FillRectangle(b, r.X, r.Y + Theme.Px(14), Theme.Px(3), r.Height - Theme.Px(28));
                if (sel) Border(g, r, Theme.Px(14), Theme.Accent, 1.5f);

                // imagen de la ruta
                var ir = new Rectangle(r.X + Theme.Px(14), r.Y + Theme.Px(14), Theme.Px(170), r.Height - Theme.Px(28));
                var img = it.HaveRoute ? ThumbOf?.Invoke(it.Route) : null;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Theme.Round(ir, Theme.Px(10)))
                {
                    var st = g.Save(); g.SetClip(path);
                    if (img != null)
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        double k = Math.Max(ir.Width / (double)img.Width, ir.Height / (double)img.Height);
                        int iw = (int)(img.Width * k), ih = (int)(img.Height * k);
                        g.DrawImage(img, ir.X + (ir.Width - iw) / 2, ir.Y + (ir.Height - ih) / 2, iw, ih);
                    }
                    else using (var b = new LinearGradientBrush(ir, Color.FromArgb(44, 58, 50), Color.FromArgb(28, 31, 36), LinearGradientMode.ForwardDiagonal)) g.FillRectangle(b, ir);
                    g.Restore(st);
                }
                g.SmoothingMode = SmoothingMode.None;
                if (!it.HaveRoute) Text(g, LblNoRoute, _fImg, Rectangle.Inflate(ir, -10, -10), Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                else
                {
                    int hw = TW(LblHave, _fImg) + Theme.Px(12);
                    var hr = new Rectangle(ir.X + Theme.Px(6), ir.Bottom - Theme.Px(24), hw, Theme.Px(18));
                    Round(g, hr, Theme.Px(5), Color.FromArgb(190, 20, 24, 22)); Text(g, LblHave, _fImg, hr, Theme.AccentHi, C1);
                }

                int x = ir.Right + Theme.Px(16), right = r.Right - Theme.Px(14), y = r.Y + Theme.Px(12);
                // jugadores: «3 / 8» y su barra
                string cnt = "👤 " + it.Players + " / " + it.MaxPlayers;
                int cntW = TW(cnt, _fCnt) + 4;
                Text(g, cnt, _fCnt, new Rectangle(right - cntW, y, cntW, Theme.Px(22)), Full(it) ? Color.FromArgb(229, 115, 115) : Blue, R1);
                var bar = new Rectangle(right - cntW, y + Theme.Px(24), cntW, Theme.Px(4));
                Round(g, bar, Theme.Px(2), Theme.Surface2);
                double fr = it.MaxPlayers > 0 ? Math.Min(1, it.Players / (double)it.MaxPlayers) : 0;
                if (fr > 0) Round(g, new Rectangle(bar.X, bar.Y, Math.Max(Theme.Px(4), (int)(bar.Width * fr)), bar.Height), Theme.Px(2), Full(it) ? Color.FromArgb(229, 115, 115) : Blue);
                // nombre de la sala
                Text(g, it.Name, _fT, new Rectangle(x, y, right - cntW - x - Theme.Px(10), Theme.Px(22)), it.Active ? Theme.Text : Theme.Subtle, L1);
                y += Theme.Px(24);
                // ruta · descripción
                string rt = "🗺 " + it.Route;
                int rw = Math.Min(TW(rt, _fRoute) + 4, right - cntW - x - Theme.Px(10));
                Text(g, rt, _fRoute, new Rectangle(x, y, rw, Theme.Px(18)), Theme.AccentHi, L1);
                if (!string.IsNullOrEmpty(it.Host))   // (solo el administrador recibe la IP en la lista)
                    Text(g, "   " + it.Host + ":" + it.Port, _fIp, new Rectangle(x + rw, y, Math.Max(0, right - cntW - x - rw - Theme.Px(10)), Theme.Px(18)), Theme.Subtle, L1);
                y += Theme.Px(19);
                if (!string.IsNullOrWhiteSpace(it.Description))
                    Text(g, it.Description, _fD, new Rectangle(x, y, right - x, Theme.Px(17)), Theme.Subtle, L1);
                y += Theme.Px(21);
                // jugadores dentro
                int cx = x, chH = Theme.Px(20);
                if (it.Names.Length == 0)
                {
                    int nw = TW(LblNoPlayers, _fChip) + Theme.Px(16);
                    Round(g, new Rectangle(cx, y, nw, chH), chH / 2, Theme.Surface2); Text(g, LblNoPlayers, _fChip, new Rectangle(cx, y, nw, chH), Theme.Subtle, C1);
                }
                else
                    foreach (var n in it.Names)
                    {
                        if (string.IsNullOrWhiteSpace(n)) continue;
                        int nw = TW(n, _fChip) + Theme.Px(16);
                        if (cx + nw > right - Theme.Px(4)) break;
                        Round(g, new Rectangle(cx, y, nw, chH), chH / 2, CardPaint.Rail); Text(g, n, _fChip, new Rectangle(cx, y, nw, chH), Theme.Text, C1);
                        cx += nw + Theme.Px(5);
                    }
                y += chH + Theme.Px(10);

                // distintivos (o el motivo por el que no se puede entrar)
                var br = BtnRect(r);
                int bx = x, by = br.Y + (br.Height - Theme.Px(22)) / 2;
                void Badge(string t, Color c)
                {
                    int w = TW(t, _fChipB) + Theme.Px(16);
                    if (bx + w > br.X - Theme.Px(8)) return;
                    var rr = new Rectangle(bx, by, w, Theme.Px(22));
                    Round(g, rr, Theme.Px(6), Color.FromArgb(34, c)); Text(g, t, _fChipB, rr, c, C1);
                    bx += w + Theme.Px(6);
                }
                if (!it.Active) Badge(LblClosed, Color.FromArgb(229, 115, 115));
                Badge(it.HasPassword ? LblPass : LblFree, it.HasPassword ? Gold : Theme.AccentHi);
                Badge(it.Access == "companies" ? LblCompanies : it.Access == "users" ? LblUsers : LblAll, it.Access == "all" ? Color.FromArgb(170, 176, 181) : Color.FromArgb(150, 190, 255));
                if (it.MinPoints > 0) Badge(string.Format(LblPoints, it.MinPoints), Color.FromArgb(240, 170, 90));
                if (it.Denied != null)
                {
                    int dw = br.X - x - Theme.Px(8);
                    Text(g, "⛔ " + it.Denied, _fD, new Rectangle(x, by - Theme.Px(22), dw, Theme.Px(18)), Color.FromArgb(229, 115, 115), L1);
                }
                else if (it.Mine) Text(g, "● " + LblInside, _fChipB, new Rectangle(x, by - Theme.Px(22), br.X - x, Theme.Px(18)), Theme.AccentHi, L1);

                // botón
                bool can = it.Denied == null && it.Active && !Full(it);
                bool bh = hov && _hoverBtn;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (can)
                    using (var b = new LinearGradientBrush(br, bh ? Theme.AccentHi : Theme.Accent, Theme.Accent2, LinearGradientMode.Horizontal))
                    using (var path = Theme.Round(br, Theme.Px(9))) g.FillPath(b, path);
                else Theme.FillRound(g, br, Theme.Px(9), bh ? Theme.SurfaceHi : Theme.Surface2);
                g.SmoothingMode = SmoothingMode.None;
                Text(g, Full(it) ? LblFull : "▶  " + LblJoin, _fBtn, br, can ? Color.White : Theme.Subtle, C1);
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
            if (it?.Id != _hover || b != _hoverBtn) { _hover = it?.Id; _hoverBtn = b; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = null; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var (it, b) = Hit(e.Location);
            if (it == null) return;
            SelectedId = it.Id; Invalidate(); Selected?.Invoke(it);
            if (b) Join?.Invoke(it);
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); var (it, _) = Hit(e.Location); if (it != null) Join?.Invoke(it); }

        public Item SelectedItem => Items.Find(i => i.Id == SelectedId);
        public void SetItems(List<Item> items)
        {
            Items = items;
            if (SelectedId != null && !items.Exists(i => i.Id == SelectedId)) SelectedId = null;
            Relayout(true);
        }
    }
}
