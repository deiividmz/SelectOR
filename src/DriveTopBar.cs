// Barra de acceso rápido en la parte CENTRAL SUPERIOR de la pantalla mientras se conduce.
// Está oculta: aparece solo al llevar el ratón al borde superior, en el centro, y se esconde
// al apartarlo. Da acceso a: HUD (mini-mapa), mapa grande y servicio de empresa (ponerse de
// servicio con el tren que se conduce si es de la flota, o registrar el servicio en curso).
// Como el HUD, no roba el foco al simulador (WS_EX_NOACTIVATE) y se dibuja a mano.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class DriveBarState
    {
        public bool HudVisible, BigMapOpen, InService, CabVisible, ChatVisible;
        public bool ServiceEnabled;        // se puede pulsar el botón de servicio
        public string ServiceText;         // «Ponerme de servicio» / «Registrar servicio» / motivo
        public string Status;              // «En servicio · RENFE · 00:12» / «Conducción libre · tren»
    }

    public class DriveTopBar : Form
    {
        readonly Func<DriveBarState> _state;
        readonly Action _toggleHud, _toggleMap, _toggleCab, _toggleChat;
        readonly Func<Task<(bool ok, string msg)>> _service;
        readonly Action _onShowing;   // p. ej. comprobar si el tren es de la flota al desplegarse
        readonly System.Windows.Forms.Timer _poll;

        DriveBarState _st = new DriveBarState();
        Rectangle _hitHud, _hitMap, _hitCab, _hitChat, _hitSvc;
        int _hoverBtn = -1;
        bool _busy, _confirm;
        DateTime _confirmUntil, _msgUntil, _lastInside;
        string _msg; bool _msgErr;

        const int BarW = 930, BarH = 58, MsgH = 24, HotW = 420, HotH = 6;
        static readonly Color Teal = Color.FromArgb(94, 190, 155);
        static readonly Color Rec = Color.FromArgb(224, 86, 86);

        public DriveTopBar(Func<DriveBarState> state, Action toggleHud, Action toggleMap,
                           Func<Task<(bool ok, string msg)>> service, Action onShowing = null, Action toggleCab = null, Action toggleChat = null)
        {
            _state = state; _toggleHud = toggleHud; _toggleMap = toggleMap; _service = service; _onShowing = onShowing;
            _toggleCab = toggleCab; _toggleChat = toggleChat;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Surface;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Opacity = 0.94;
            Size = new Size(BarW, BarH);

            // Sondeo del ratón: más fiable que MouseEnter para una ventana oculta.
            _poll = new System.Windows.Forms.Timer { Interval = 90 };
            _poll.Tick += (s, e) => Poll();
            _poll.Start();
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/;
                return cp;
            }
        }

        // Aparece también si se llama desde fuera (p. ej. para mostrar un mensaje).
        public void Reveal()
        {
            var scr = Screen.FromPoint(Cursor.Position).Bounds;
            PlaceOn(scr);
            if (!Visible) { _onShowing?.Invoke(); Show(); }
            _lastInside = DateTime.UtcNow;
            Refresh_();
        }

        void PlaceOn(Rectangle scr)
        {
            int h = BarH + (HasMsg ? MsgH : 0);
            var target = new Rectangle(scr.Left + (scr.Width - BarW) / 2, scr.Top, BarW, h);
            if (Bounds != target) { Bounds = target; ApplyRegion(); }
        }

        bool HasMsg => !string.IsNullOrEmpty(_msg) && DateTime.UtcNow < _msgUntil;

        void Poll()
        {
            var cur = Cursor.Position;
            var scr = Screen.FromPoint(cur).Bounds;
            var hot = new Rectangle(scr.Left + (scr.Width - HotW) / 2, scr.Top, HotW, HotH);

            if (!Visible)
            {
                if (hot.Contains(cur)) Reveal();
                return;
            }

            var inside = Bounds; inside.Inflate(28, 0); inside.Height += 28;   // margen para no cerrarse al rozar el borde
            if (inside.Contains(cur) || hot.Contains(cur)) _lastInside = DateTime.UtcNow;

            if (_confirm && DateTime.UtcNow > _confirmUntil) { _confirm = false; Invalidate(); }

            // Se esconde tras apartar el ratón (salvo mientras trabaja o muestra un resultado).
            bool keep = _busy || HasMsg;
            if (!keep && (DateTime.UtcNow - _lastInside).TotalMilliseconds > 650) { Hide(); _confirm = false; return; }

            PlaceOn(Screen.FromPoint(new Point(Left + Width / 2, Top + 1)).Bounds);
            int hb = HitIndex(PointToClient(cur));
            if (hb != _hoverBtn) { _hoverBtn = hb; Invalidate(); }
            Refresh_();
        }

        void Refresh_()
        {
            try { _st = _state?.Invoke() ?? new DriveBarState(); } catch { }
            Invalidate();
        }

        int HitIndex(Point p)
        {
            if (_hitHud.Contains(p)) return 0;
            if (_hitMap.Contains(p)) return 1;
            if (_hitSvc.Contains(p)) return 2;
            if (_hitCab.Contains(p)) return 3;
            if (_hitChat.Contains(p)) return 4;
            return -1;
        }

        protected override async void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || _busy) return;
            int i = HitIndex(e.Location);
            if (i == 0) { _toggleHud?.Invoke(); Refresh_(); }
            else if (i == 1) { _toggleMap?.Invoke(); Refresh_(); }
            else if (i == 3) { _toggleCab?.Invoke(); Refresh_(); }
            else if (i == 4) { _toggleChat?.Invoke(); Refresh_(); }
            else if (i == 2 && _st.ServiceEnabled && _service != null)
            {
                // Registrar pide una segunda pulsación (no hay diálogos: quedarían tras el simulador).
                if (_st.InService && !_confirm)
                {
                    _confirm = true; _confirmUntil = DateTime.UtcNow.AddSeconds(4);
                    Invalidate(); return;
                }
                _confirm = false; _busy = true; Invalidate();
                (bool ok, string msg) r;
                try { r = await _service(); }
                catch (Exception ex) { r = (false, ex.Message); }
                _busy = false;
                ShowMessage(r.msg, !r.ok);
                Refresh_();
            }
        }

        public void ShowMessage(string msg, bool error, int seconds = 7)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;
            _msg = msg; _msgErr = error; _msgUntil = DateTime.UtcNow.AddSeconds(seconds);
            Reveal();
        }

        void ApplyRegion()
        {
            using var p = BottomRound(new Rectangle(0, 0, Width, Height), 14);
            Region = new Region(p);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, ClientRectangle);
            using (var acc = new SolidBrush(Theme.Accent)) g.FillRectangle(acc, 0, 0, Width, 3);

            // Estado (izquierda)
            var dotCol = _st.InService ? Rec : Teal;
            using (var b = new SolidBrush(dotCol)) g.FillEllipse(b, 14, BarH / 2 - 4, 9, 9);
            TextRenderer.DrawText(g, _st.Status ?? "", Theme.Font(9f, FontStyle.Bold),
                new Rectangle(30, 5, 186, BarH - 8), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // Botones (derecha)
            int y = 10, h = BarH - 18;
            _hitHud = new Rectangle(222, y, 84, h);
            _hitMap = new Rectangle(_hitHud.Right + 6, y, 104, h);
            _hitCab = new Rectangle(_hitMap.Right + 6, y, 110, h);
            _hitChat = new Rectangle(_hitCab.Right + 6, y, 94, h);
            _hitSvc = new Rectangle(_hitChat.Right + 6, y, Width - _hitChat.Right - 6 - 10, h);

            DrawBtn(g, _hitHud, "HUD", _st.HudVisible, true, _hoverBtn == 0, Glyph.Hud);
            DrawBtn(g, _hitMap, I18n.T("Mapa"), _st.BigMapOpen, true, _hoverBtn == 1, Glyph.Map);
            DrawBtn(g, _hitCab, I18n.T("Pupitre"), _st.CabVisible, true, _hoverBtn == 3, Glyph.Cab);
            DrawBtn(g, _hitChat, I18n.T("Chat"), _st.ChatVisible, true, _hoverBtn == 4, Glyph.Chat);

            string svcText = _busy ? I18n.T("Un momento…")
                : _confirm ? I18n.T("Pulsa otra vez para registrar")
                : (_st.ServiceText ?? "");
            bool primary = _st.ServiceEnabled && !_st.InService;
            DrawSvcBtn(g, _hitSvc, svcText, _st.ServiceEnabled && !_busy, primary, _st.InService || _confirm, _hoverBtn == 2);

            if (HasMsg)
            {
                var mr = new Rectangle(14, BarH, Width - 28, MsgH - 4);
                TextRenderer.DrawText(g, _msg, Theme.Font(8.8f, FontStyle.Bold), mr,
                    _msgErr ? Color.FromArgb(235, 130, 130) : Teal,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }

        enum Glyph { Hud, Map, Cab, Chat }

        void DrawBtn(Graphics g, Rectangle r, string text, bool active, bool enabled, bool hover, Glyph glyph)
        {
            var fill = active ? Blend(Theme.Surface2, Teal, 0.22f) : (hover ? Theme.SurfaceHi : Theme.Surface2);
            using (var b = new SolidBrush(fill)) FillRound(g, r, 9, b);
            if (active) using (var p = new Pen(Teal, 1.2f)) DrawRound(g, r, 9, p);
            var fg = active ? Teal : (enabled ? Theme.Text : Theme.Subtle);
            var ico = new Rectangle(r.Left + 10, r.Top + (r.Height - 14) / 2, 14, 14);
            using (var pen = new Pen(fg, 1.5f))
            {
                if (glyph == Glyph.Hud)
                {
                    g.DrawRectangle(pen, ico.Left, ico.Top + 2, ico.Width, ico.Height - 4);
                    g.DrawLine(pen, ico.Left + 3, ico.Top + 6, ico.Right - 3, ico.Top + 6);
                    g.DrawLine(pen, ico.Left + 3, ico.Top + 9, ico.Right - 6, ico.Top + 9);
                }
                else if (glyph == Glyph.Chat)
                {
                    // Bocadillo de conversación.
                    g.DrawRectangle(pen, ico.Left, ico.Top + 1, ico.Width, ico.Height - 5);
                    g.DrawLines(pen, new[] { new Point(ico.Left + 3, ico.Bottom - 4), new Point(ico.Left + 2, ico.Bottom), new Point(ico.Left + 7, ico.Bottom - 4) });
                }
                else if (glyph == Glyph.Cab)
                {
                    // Esfera con aguja: los indicadores del tren.
                    g.DrawArc(pen, ico.Left, ico.Top + 1, ico.Width, ico.Width, 150, 240);
                    int cx = ico.Left + ico.Width / 2, cy = ico.Top + 1 + ico.Width / 2;
                    g.DrawLine(pen, cx, cy, ico.Right - 3, ico.Top + 4);
                }
                else
                {
                    g.DrawRectangle(pen, ico.Left, ico.Top + 1, ico.Width, ico.Height - 2);
                    g.DrawLine(pen, ico.Left + ico.Width / 3, ico.Top + 1, ico.Left + ico.Width / 3, ico.Bottom - 1);
                    g.DrawLine(pen, ico.Left + 2 * ico.Width / 3, ico.Top + 1, ico.Left + 2 * ico.Width / 3, ico.Bottom - 1);
                }
            }
            TextRenderer.DrawText(g, text, Theme.Font(9.5f, FontStyle.Bold),
                new Rectangle(ico.Right + 6, r.Top, r.Right - ico.Right - 8, r.Height), fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        void DrawSvcBtn(Graphics g, Rectangle r, string text, bool enabled, bool primary, bool danger, bool hover)
        {
            Color fill;
            if (!enabled) fill = Blend(Theme.Surface2, Theme.Surface, 0.4f);
            else if (danger) fill = hover ? Color.FromArgb(200, 80, 80) : Color.FromArgb(176, 64, 64);
            else if (primary) fill = hover ? Theme.AccentHi : Theme.Accent;
            else fill = hover ? Theme.SurfaceHi : Theme.Surface2;
            using (var b = new SolidBrush(fill)) FillRound(g, r, 9, b);
            var fg = !enabled ? Theme.Subtle : ((primary || danger) ? Color.White : Theme.Text);
            TextRenderer.DrawText(g, text, Theme.Font(enabled ? 9.5f : 8.6f, FontStyle.Bold),
                new Rectangle(r.Left + 8, r.Top, r.Width - 16, r.Height), fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        public void CloseBar()
        {
            try { _poll?.Stop(); } catch { }
            try { Close(); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { _poll?.Stop(); _poll?.Dispose(); } catch { }
            base.OnFormClosing(e);
        }

        static void FillRound(Graphics g, Rectangle rc, int r, Brush b) { using var p = RoundPath(rc, r); g.FillPath(b, p); }
        static void DrawRound(Graphics g, Rectangle rc, int r, Pen pen) { using var p = RoundPath(rc, r); g.DrawPath(pen, p); }
        static GraphicsPath RoundPath(Rectangle rc, int r)
        {
            var p = new GraphicsPath();
            p.AddArc(rc.X, rc.Y, r, r, 180, 90);
            p.AddArc(rc.Right - r, rc.Y, r, r, 270, 90);
            p.AddArc(rc.Right - r, rc.Bottom - r, r, r, 0, 90);
            p.AddArc(rc.X, rc.Bottom - r, r, r, 90, 90);
            p.CloseFigure();
            return p;
        }
        // Solo esquinas inferiores redondeadas (la barra «cuelga» del borde superior).
        static GraphicsPath BottomRound(Rectangle rc, int r)
        {
            var p = new GraphicsPath();
            p.AddLine(rc.X, rc.Y, rc.Right, rc.Y);
            p.AddArc(rc.Right - r, rc.Bottom - r, r, r, 0, 90);
            p.AddArc(rc.X, rc.Bottom - r, r, r, 90, 90);
            p.CloseFigure();
            return p;
        }
        static Color Blend(Color a, Color b, float t)
            => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
}
