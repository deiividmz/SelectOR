// Chat de empresa (chat-empresa.sql): la lista de mensajes dibujada a mano (en la sección «Chat» de
// Empresas y en el HUD de conducción), el HUD transparente con su desplegable de empresas y la cajita
// para escribir desde el HUD.
//  · El HUD, como los demás, no roba el foco a Open Rails (WS_EX_NOACTIVATE). Para escribir se abre una
//    cajita que sí toma el teclado; al enviar (Intro) o cancelar (Esc) el teclado vuelve al simulador.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class ChatMsg
    {
        public long Id;
        public string UserId = "", User = "", Role = "", Body = "";
        public DateTime AtUtc;
    }

    // Chat de una empresa: mensajes recibidos y mi permiso.
    public sealed class ChatRoom
    {
        public string CompanyId = "", Name = "";
        public readonly List<ChatMsg> Msgs = new();
        public long LastId;
        public bool Loaded, Loading, StateLoaded;
        public bool Member = true, Muted, CanModerate;
        public bool CanWrite => Member && !Muted;
        public readonly HashSet<string> MutedUsers = new(StringComparer.OrdinalIgnoreCase);
        public string Error;   // p. ej. el servidor aún no tiene el chat
    }

    // ---------------------------------------------------------------- lista de mensajes
    public class ChatView : Control
    {
        public bool Compact;              // HUD: sin burbujas, más apretado
        public string MyUserId = "";
        public string EmptyText = "";

        List<ChatMsg> _msgs = new();
        readonly List<Item> _items = new();
        int _layoutW = -1, _contentH, _scroll;
        bool _stick = true, _newBelow;
        Rectangle _newPill;
        long _lastId;

        sealed class Item
        {
            public ChatMsg M;
            public string Day;          // separador de día (solo esta fila)
            public bool Header;         // primera de un grupo: nombre y hora
            public bool Mine;
            public int Y, H;
            public Rectangle Head, Bubble, Text;
        }

        public ChatView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);   // en el HUD no debe tomar el foco (ni robárselo al simulador)
            BackColor = Theme.Bg;
        }

        public void SetMessages(List<ChatMsg> msgs)
        {
            long last = msgs != null && msgs.Count > 0 ? msgs[^1].Id : 0;
            bool more = last > _lastId && _lastId > 0;
            bool mineLast = msgs != null && msgs.Count > 0 && msgs[^1].UserId == MyUserId;
            _msgs = msgs == null ? new List<ChatMsg>() : new List<ChatMsg>(msgs);
            _lastId = last;
            _layoutW = -1;
            Relayout();
            if (_stick || mineLast) { ScrollToBottom(); _newBelow = false; }
            else if (more) _newBelow = true;
            Invalidate();
        }

        public void ScrollToBottom()
        {
            _scroll = Math.Max(0, _contentH - Height);
            _stick = true; _newBelow = false;
            Invalidate();
        }

        Font FName => Theme.Font(Compact ? 8.5f : 9f, FontStyle.Bold);
        Font FBody => Theme.Font(Compact ? 9f : 10f);
        Font FSmall => Theme.Font(Compact ? 7.5f : 8f);
        const TextFormatFlags BodyFlags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        int Pad => Compact ? 8 : 16;

        void Relayout()
        {
            int w = Math.Max(60, ClientSize.Width);
            if (w == _layoutW) return;
            _layoutW = w;
            _items.Clear();
            int y = Compact ? 6 : 12;
            int maxText = Compact ? w - Pad * 2 - 6 : Math.Min((int)(w * 0.70), 640) - 24;
            ChatMsg prev = null; DateTime prevDay = DateTime.MinValue;
            foreach (var m in _msgs)
            {
                var local = m.AtUtc.ToLocalTime();
                var it = new Item { M = m, Mine = m.UserId == MyUserId && MyUserId.Length > 0 };
                if (local.Date != prevDay)
                {
                    var sep = new Item { Day = DayText(local), Y = y, H = Compact ? 20 : 30 };
                    _items.Add(sep); y += sep.H;
                    prev = null;
                }
                prevDay = local.Date;
                it.Header = prev == null || prev.UserId != m.UserId || (m.AtUtc - prev.AtUtc).TotalMinutes > 5;
                if (it.Header && prev != null) y += Compact ? 4 : 8;
                it.Y = y;
                var ts = TextRenderer.MeasureText(m.Body, FBody, new Size(Math.Max(20, maxText), 0), BodyFlags);
                int headH = it.Header ? (Compact ? 16 : 19) : 0;
                if (Compact)
                {
                    it.Head = new Rectangle(Pad, y, w - Pad * 2, headH);
                    it.Text = new Rectangle(Pad + 2, y + headH, Math.Max(20, maxText), ts.Height);
                    it.H = headH + ts.Height + 2;
                }
                else
                {
                    int bw = Math.Max(ts.Width, 24) + 24, bh = ts.Height + 14;
                    int bx = it.Mine ? w - Pad - bw : Pad;
                    it.Head = new Rectangle(Pad, y, w - Pad * 2, headH);
                    it.Bubble = new Rectangle(bx, y + headH, bw, bh);
                    it.Text = new Rectangle(bx + 12, y + headH + 7, Math.Max(20, ts.Width), ts.Height);
                    it.H = headH + bh + 3;
                }
                y += it.H;
                _items.Add(it);
                prev = m;
            }
            _contentH = y + (Compact ? 6 : 12);
            ClampScroll();
        }

        static string DayText(DateTime local)
        {
            var today = DateTime.Now.Date;
            if (local.Date == today) return I18n.T("Hoy");
            if (local.Date == today.AddDays(-1)) return I18n.T("Ayer");
            return local.ToString(I18n.English ? "dd/MM/yyyy" : "dd-MM-yyyy");
        }

        void ClampScroll()
        {
            int max = Math.Max(0, _contentH - Height);
            _scroll = Math.Max(0, Math.Min(_scroll, max));
            _stick = _scroll >= max - 2;
            if (_stick) _newBelow = false;
        }

        protected override void OnResize(EventArgs e)
        {
            bool stick = _stick;
            _layoutW = -1; Relayout();
            if (stick) _scroll = Math.Max(0, _contentH - Height);
            ClampScroll();
            base.OnResize(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _scroll -= e.Delta / 120 * (Compact ? 36 : 54);
            ClampScroll();
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_newBelow && _newPill.Contains(e.Location)) { ScrollToBottom(); return; }
            if (e.Button == MouseButtons.Right && !Compact)
            {
                var it = _items.Find(x => x.M != null && x.Bubble.Contains(e.X, e.Y + _scroll));
                if (it != null)
                {
                    var cm = new ContextMenuStrip();
                    cm.Items.Add(I18n.T("Copiar mensaje"), null, (s, a) => { try { Clipboard.SetText(it.M.Body); } catch { } });
                    cm.Show(this, e.Location);
                }
            }
            base.OnMouseUp(e);
        }

        static readonly Color[] NamePalette =
        {
            Color.FromArgb(96, 165, 250), Color.FromArgb(251, 146, 60), Color.FromArgb(45, 212, 191), Color.FromArgb(244, 114, 182),
            Color.FromArgb(163, 230, 53), Color.FromArgb(129, 140, 248), Color.FromArgb(250, 204, 21), Color.FromArgb(56, 189, 248),
        };

        public static Color NameColor(string userId)
        {
            int h = 0;
            foreach (char c in userId ?? "") h = unchecked(h * 31 + c);
            return NamePalette[(h & 0x7fffffff) % NamePalette.Length];
        }

        public static string RoleText(string role) => role switch
        {
            "owner" => I18n.T("Gerente"), "manager" => I18n.T("Gestor"), _ => ""
        };

        static Color RoleColor(string role) => role == "owner" ? Color.FromArgb(245, 197, 66) : Color.FromArgb(167, 139, 250);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
            if (_layoutW != ClientSize.Width) Relayout();

            if (_items.Count == 0)
            {
                TextRenderer.DrawText(g, EmptyText ?? "", Theme.Font(Compact ? 8.5f : 10f), new Rectangle(12, 0, Width - 24, Height), Theme.Subtle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            foreach (var it in _items)
            {
                int top = it.Y - _scroll;
                if (top > Height || top + it.H < 0) continue;
                if (it.Day != null)
                {
                    var r = new Rectangle(0, top, Width, it.H);
                    var sz = TextRenderer.MeasureText(it.Day, FSmall);
                    var pill = new Rectangle((Width - sz.Width - 18) / 2, top + (it.H - sz.Height - 4) / 2, sz.Width + 18, sz.Height + 4);
                    using (var p = new Pen(Color.FromArgb(60, 255, 255, 255)))
                    {
                        g.DrawLine(p, Pad, r.Top + r.Height / 2, pill.Left - 8, r.Top + r.Height / 2);
                        g.DrawLine(p, pill.Right + 8, r.Top + r.Height / 2, Width - Pad, r.Top + r.Height / 2);
                    }
                    TextRenderer.DrawText(g, it.Day, FSmall, pill, Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    continue;
                }
                var m = it.M;
                string time = m.AtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
                var dy = new Point(0, -_scroll);
                if (it.Header)
                {
                    var hr = it.Head; hr.Offset(dy);
                    string name = it.Mine ? I18n.T("Tú") : (string.IsNullOrWhiteSpace(m.User) ? "—" : m.User);
                    var nc = it.Mine ? Theme.AccentHi : NameColor(m.UserId);
                    string role = RoleText(m.Role);
                    var nsz = TextRenderer.MeasureText(name, FName, Size.Empty, TextFormatFlags.NoPadding);
                    var rsz = role.Length > 0 ? TextRenderer.MeasureText(role, FSmall, Size.Empty, TextFormatFlags.NoPadding) : Size.Empty;
                    var tsz = TextRenderer.MeasureText(time, FSmall, Size.Empty, TextFormatFlags.NoPadding);
                    int total = nsz.Width + (role.Length > 0 ? rsz.Width + 14 : 0) + tsz.Width + 8;
                    int x = (!Compact && it.Mine) ? hr.Right - total : hr.Left + (Compact ? 0 : 2);
                    TextRenderer.DrawText(g, name, FName, new Point(x, hr.Top), nc, TextFormatFlags.NoPadding);
                    x += nsz.Width + 6;
                    if (role.Length > 0)
                    {
                        var rr = new Rectangle(x, hr.Top + (nsz.Height - rsz.Height) / 2 - 1, rsz.Width + 8, rsz.Height + 2);
                        using (var rb = new SolidBrush(Color.FromArgb(40, RoleColor(m.Role)))) using (var rp = Theme.Round(rr, 4)) g.FillPath(rb, rp);
                        TextRenderer.DrawText(g, role, FSmall, new Point(rr.Left + 4, rr.Top + 1), RoleColor(m.Role), TextFormatFlags.NoPadding);
                        x = rr.Right + 6;
                    }
                    TextRenderer.DrawText(g, time, FSmall, new Point(x, hr.Top + (nsz.Height - tsz.Height) / 2), Theme.Subtle, TextFormatFlags.NoPadding);
                }
                var tr = it.Text; tr.Offset(dy);
                if (!Compact)
                {
                    var br = it.Bubble; br.Offset(dy);
                    var fill = it.Mine ? Color.FromArgb(38, 74, 50) : Theme.Surface;
                    using (var b = new SolidBrush(fill)) using (var p = Theme.Round(br, 10)) g.FillPath(b, p);
                }
                TextRenderer.DrawText(g, m.Body, FBody, tr, Theme.Text, BodyFlags);
            }

            // Barra de desplazamiento fina
            if (_contentH > Height)
            {
                int th = Math.Max(24, Height * Height / _contentH);
                int ty = (int)((Height - th) * (_scroll / (double)Math.Max(1, _contentH - Height)));
                using var b = new SolidBrush(Color.FromArgb(70, 255, 255, 255));
                using var p = Theme.Round(new Rectangle(Width - 6, ty + 2, 4, th - 4), 2);
                g.FillPath(b, p);
            }
            if (_newBelow)
            {
                string t = "↓ " + I18n.T("Mensajes nuevos");
                var sz = TextRenderer.MeasureText(t, FSmall);
                _newPill = new Rectangle((Width - sz.Width - 20) / 2, Height - sz.Height - 14, sz.Width + 20, sz.Height + 6);
                using (var b = new SolidBrush(Theme.Accent)) using (var p = Theme.Round(_newPill, _newPill.Height / 2)) g.FillPath(b, p);
                TextRenderer.DrawText(g, t, FSmall, _newPill, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    // ---------------------------------------------------------------- HUD del chat
    public class ChatHudOverlay : Form
    {
        readonly AppPrefs _prefs;
        readonly ChatView _view;
        readonly System.Windows.Forms.Timer _tick;
        public Func<List<(string id, string name)>> Companies;   // empresas de las que soy socio
        public Func<string> CurrentId;
        public Action<string> Pick;
        public Func<(bool can, string why)> WriteState;
        public Action Compose;
        public Action CloseRequested;
        public Action Heartbeat;          // cada segundo (la ventana principal sigue la empresa en servicio)

        string _title = "", _serverNote;
        bool _collapsed, _hover, _down, _dragging, _resizing;
        Point _downScreen, _formAtDown; Size _sizeAtDown;
        int _unread; long _seenId;
        Rectangle _hitCo, _hitCollapse, _hitClose, _hitCompose, _hitGrip;
        HudListPicker _picker;
        int _hoverZone = -1;

        const int HdrH = 32, FootH = 30, MinW = 250, MinH = 150, MaxW = 900, MaxH = 900;
        const double IdleOpacity = 0.80;
        static readonly Color Teal = Color.FromArgb(94, 190, 155);

        public ChatHudOverlay(AppPrefs prefs, string myUserId)
        {
            _prefs = prefs;
            _collapsed = prefs?.ChatHudCollapsed ?? false;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Surface;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Opacity = IdleOpacity;
            _view = new ChatView { Compact = true, MyUserId = myUserId ?? "", BackColor = Color.FromArgb(28, 32, 36), EmptyText = I18n.T("Cargando el chat…") };
            Controls.Add(_view);
            Size = TargetSize();
            Location = InitialLocation();
            LayoutView(); ApplyRegion();
            _tick = new System.Windows.Forms.Timer { Interval = 250 };
            int n = 0;
            _tick.Tick += (s, e) =>
            {
                bool inside = Bounds.Contains(Cursor.Position) || (_picker != null && !_picker.IsDisposed && _picker.Visible);
                if (inside != _hover) { _hover = inside; Opacity = inside ? 1.0 : IdleOpacity; Invalidate(); }
                if (++n % 4 == 0) { try { Heartbeat?.Invoke(); } catch { } }
            };
            _tick.Start();
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

        public bool Collapsed => _collapsed;

        Size TargetSize()
        {
            int w = Math.Max(MinW, Math.Min(MaxW, _prefs?.ChatHudW ?? 360));
            int h = Math.Max(MinH, Math.Min(MaxH, _prefs?.ChatHudH ?? 300));
            return _collapsed ? new Size(w, HdrH) : new Size(w, h);
        }

        Point InitialLocation()
        {
            try
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                if (_prefs != null && _prefs.ChatHudX >= 0 && _prefs.ChatHudY >= 0)
                    return new Point(Math.Min(Math.Max(_prefs.ChatHudX, wa.Left), wa.Right - Width),
                                     Math.Min(Math.Max(_prefs.ChatHudY, wa.Top), wa.Bottom - Height));
                return new Point(wa.Right - Width - 18, wa.Bottom - Height - 18);   // abajo a la derecha
            }
            catch { return new Point(60, 60); }
        }

        void LayoutView()
        {
            _view.Visible = !_collapsed;
            if (!_collapsed) _view.Bounds = new Rectangle(1, HdrH, Width - 2, Math.Max(10, Height - HdrH - FootH));
        }

        void ApplyRegion()
        {
            using var p = Theme.Round(new Rectangle(0, 0, Width, Height), 12);
            Region = new Region(p);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); if (_view != null) { LayoutView(); ApplyRegion(); } }

        // Mensajes de la empresa elegida. titleName = su nombre.
        public void SetRoom(string titleName, List<ChatMsg> msgs, string serverNote = null)
        {
            bool other = titleName != _title;
            _title = titleName ?? "";
            _serverNote = serverNote;
            _view.EmptyText = serverNote ?? I18n.T("Aún no hay mensajes. ¡Saluda!");
            long last = msgs != null && msgs.Count > 0 ? msgs[^1].Id : 0;
            if (other) { _seenId = last; _unread = 0; }
            else if (_collapsed && msgs != null)
            {
                int u = 0;
                foreach (var m in msgs) if (m.Id > _seenId && m.UserId != _view.MyUserId) u++;
                _unread = u;
            }
            if (!_collapsed) _seenId = last;
            _view.SetMessages(msgs);
            if (other) _view.ScrollToBottom();
            Invalidate();
        }

        void ToggleCollapse()
        {
            _collapsed = !_collapsed;
            if (_prefs != null) _prefs.ChatHudCollapsed = _collapsed;
            int bottom = Bottom;
            Size = TargetSize();
            Top = Math.Max(0, bottom - Height);   // crece o encoge hacia arriba
            if (!_collapsed) { _unread = 0; _view.ScrollToBottom(); }
            LayoutView(); ApplyRegion(); Invalidate();
            SavePos();
        }

        void SavePos()
        {
            if (_prefs == null) return;
            _prefs.ChatHudX = Left; _prefs.ChatHudY = Top;
            if (!_collapsed) { _prefs.ChatHudW = Width; _prefs.ChatHudH = Height; }
        }

        int Zone(Point p)
        {
            if (_hitClose.Contains(p)) return 0;
            if (_hitCollapse.Contains(p)) return 1;
            if (_hitCo.Contains(p)) return 2;
            if (!_collapsed && _hitCompose.Contains(p)) return 3;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _downScreen = Cursor.Position; _formAtDown = Location;
                if (!_collapsed && _hitGrip.Contains(e.Location)) { _resizing = true; _sizeAtDown = Size; }
                else _down = true;
                _dragging = false;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var now = Cursor.Position;
            if (_resizing)
            {
                int w = Math.Max(MinW, Math.Min(MaxW, _sizeAtDown.Width + now.X - _downScreen.X));
                int h = Math.Max(MinH, Math.Min(MaxH, _sizeAtDown.Height + now.Y - _downScreen.Y));
                if (w != Width || h != Height) Size = new Size(w, h);
            }
            else if (_down)
            {
                if (!_dragging && (Math.Abs(now.X - _downScreen.X) > 3 || Math.Abs(now.Y - _downScreen.Y) > 3)) _dragging = true;
                if (_dragging) Location = new Point(_formAtDown.X + now.X - _downScreen.X, _formAtDown.Y + now.Y - _downScreen.Y);
            }
            else
            {
                int z = Zone(e.Location);
                if (z != _hoverZone) { _hoverZone = z; Cursor = z >= 0 ? Cursors.Hand : (!_collapsed && _hitGrip.Contains(e.Location) ? Cursors.SizeNWSE : Cursors.Default); Invalidate(); }
                else if (z < 0) Cursor = !_collapsed && _hitGrip.Contains(e.Location) ? Cursors.SizeNWSE : Cursors.Default;
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool click = _down && !_dragging;
            bool moved = _dragging || _resizing;
            _down = _dragging = _resizing = false;
            if (moved) SavePos();
            if (click && e.Button == MouseButtons.Left)
            {
                switch (Zone(e.Location))
                {
                    case 0: CloseRequested?.Invoke(); break;
                    case 1: ToggleCollapse(); break;
                    case 2: OpenPicker(); break;
                    case 3: { var ws = SafeWrite(); if (ws.can) Compose?.Invoke(); break; }
                    default: if (_collapsed) ToggleCollapse(); break;
                }
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e) { if (_hoverZone != -1) { _hoverZone = -1; Invalidate(); } base.OnMouseLeave(e); }

        (bool can, string why) SafeWrite() { try { return WriteState?.Invoke() ?? (false, ""); } catch { return (false, ""); } }

        void OpenPicker()
        {
            try { _picker?.Close(); } catch { }
            List<(string id, string name)> list = null;
            try { list = Companies?.Invoke(); } catch { }
            if (list == null || list.Count == 0) return;
            string cur = null; try { cur = CurrentId?.Invoke(); } catch { }
            _picker = new HudListPicker(list, cur, id => Pick?.Invoke(id));
            var p = PointToScreen(new Point(_hitCo.Left, _hitCo.Bottom + 2));
            _picker.ShowAt(p.X, p.Y, _hitCo.Width);
        }

        // Rectángulo de la línea de escribir, en pantalla (ahí se abre la cajita).
        public Rectangle ComposeScreenRect() => RectangleToScreen(new Rectangle(0, Height - FootH, Width, FootH));

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, ClientRectangle);
            using (var acc = new SolidBrush(Teal)) g.FillRectangle(acc, 0, 0, Width, 2);

            // Cabecera: bocadillo · «Chat · Empresa ▾» · nuevos · plegar · cerrar
            var ico = new Rectangle(10, (HdrH - 16) / 2 + 1, 18, 15);
            DrawBubble(g, ico, Teal);
            _hitClose = new Rectangle(Width - 28, 5, 22, HdrH - 10);
            _hitCollapse = new Rectangle(_hitClose.Left - 24, 5, 22, HdrH - 10);
            string badge = _collapsed && _unread > 0 ? string.Format(I18n.T("{0} nuevos"), _unread) : null;
            int badgeW = badge != null ? TextRenderer.MeasureText(badge, Theme.Font(8f, FontStyle.Bold)).Width + 12 : 0;
            int coRight = _hitCollapse.Left - 6 - (badgeW > 0 ? badgeW + 6 : 0);
            _hitCo = new Rectangle(ico.Right + 6, 4, Math.Max(40, coRight - ico.Right - 6), HdrH - 8);
            if (_hoverZone == 2) using (var hb = new SolidBrush(Theme.SurfaceHi)) using (var hp = Theme.Round(_hitCo, 7)) g.FillPath(hb, hp);
            string title = string.IsNullOrEmpty(_title) ? I18n.T("Chat") : _title;
            var tf = Theme.Font(9.5f, FontStyle.Bold);
            TextRenderer.DrawText(g, title + "  ▾", tf, new Rectangle(_hitCo.Left + 6, _hitCo.Top, _hitCo.Width - 8, _hitCo.Height), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (badge != null)
            {
                var br = new Rectangle(_hitCollapse.Left - 6 - badgeW, (HdrH - 18) / 2, badgeW, 18);
                using (var b = new SolidBrush(Color.FromArgb(224, 86, 86))) using (var p = Theme.Round(br, 9)) g.FillPath(b, p);
                TextRenderer.DrawText(g, badge, Theme.Font(8f, FontStyle.Bold), br, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            DrawHdrBtn(g, _hitCollapse, _collapsed ? "+" : "–", _hoverZone == 1);
            DrawHdrBtn(g, _hitClose, "×", _hoverZone == 0);
            if (_collapsed) return;

            // Pie: escribir
            var fr = new Rectangle(0, Height - FootH, Width, FootH);
            using (var p = new Pen(Color.FromArgb(50, 255, 255, 255))) g.DrawLine(p, 8, fr.Top, Width - 8, fr.Top);
            var ws = SafeWrite();
            _hitCompose = new Rectangle(8, fr.Top + 4, Width - 34, FootH - 8);
            if (ws.can && _hoverZone == 3) using (var hb = new SolidBrush(Theme.SurfaceHi)) using (var hp = Theme.Round(_hitCompose, 7)) g.FillPath(hb, hp);
            string ft = ws.can ? "✎  " + I18n.T("Escribe un mensaje…") : (ws.why ?? "");
            TextRenderer.DrawText(g, ft, Theme.Font(8.8f), new Rectangle(_hitCompose.Left + 6, _hitCompose.Top, _hitCompose.Width - 8, _hitCompose.Height),
                ws.can ? Theme.Subtle : Color.FromArgb(229, 150, 115), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            // Asa para agrandar
            _hitGrip = new Rectangle(Width - 18, Height - 18, 18, 18);
            using (var gp = new Pen(Color.FromArgb(110, 255, 255, 255), 1.2f))
                for (int k = 4; k <= 12; k += 4) g.DrawLine(gp, Width - 3 - k, Height - 3, Width - 3, Height - 3 - k);
        }

        void DrawHdrBtn(Graphics g, Rectangle r, string t, bool hover)
        {
            if (hover) using (var b = new SolidBrush(Theme.SurfaceHi)) using (var p = Theme.Round(r, 6)) g.FillPath(b, p);
            TextRenderer.DrawText(g, t, Theme.Font(11f, FontStyle.Bold), r, hover ? Theme.Text : Theme.Subtle,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        public static void DrawBubble(Graphics g, Rectangle r, Color c)
        {
            using var pen = new Pen(c, 1.6f) { LineJoin = LineJoin.Round };
            var body = new Rectangle(r.Left, r.Top, r.Width, r.Height - 4);
            using var p = Theme.Round(body, 4);
            g.DrawPath(pen, p);
            g.DrawLines(pen, new[] { new Point(r.Left + 4, body.Bottom), new Point(r.Left + 3, r.Bottom), new Point(r.Left + 9, body.Bottom) });
            using var b = new SolidBrush(c);
            for (int i = 0; i < 3; i++) g.FillEllipse(b, r.Left + 4 + i * 4, body.Top + body.Height / 2 - 1, 2.4f, 2.4f);
        }

        public void CloseHud()
        {
            try { _tick.Stop(); _tick.Dispose(); } catch { }
            try { _picker?.Close(); } catch { }
            SavePos();
            try { Close(); } catch { }
        }
    }

    // ---------------------------------------------------------------- desplegable (sin robar el foco)
    public class HudListPicker : Form
    {
        readonly List<(string id, string name)> _items;
        readonly string _current;
        readonly Action<string> _pick;
        int _hover = -1;
        const int RowH = 26, PadV = 6;

        public HudListPicker(List<(string id, string name)> items, string currentId, Action<string> pick)
        {
            _items = items; _current = currentId; _pick = pick;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Surface;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Opacity = 0.96;
            // Se cierra al pulsar fuera.
            var t = new System.Windows.Forms.Timer { Interval = 120 };
            t.Tick += (s, e) =>
            {
                if (IsDisposed) { t.Dispose(); return; }
                if ((MouseButtons & (MouseButtons.Left | MouseButtons.Right)) != 0 && !Bounds.Contains(Cursor.Position)) { t.Dispose(); Close(); }
            };
            t.Start();
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080; return cp; }
        }

        // above ≥ 0: abrir HACIA ARRIBA terminando en esa y de pantalla (el HUD está abajo).
        public void ShowAt(int x, int y, int minWidth, int above = -1)
        {
            int w = Math.Max(minWidth, 200);
            using (var f = Theme.Font(9.5f, FontStyle.Bold))
                foreach (var it in _items) w = Math.Max(w, TextRenderer.MeasureText(it.name, f).Width + 40);
            int h = _items.Count * RowH + PadV * 2;
            if (above >= 0) y = above - h - 4;
            var wa = Screen.FromPoint(new Point(x, y)).WorkingArea;
            if (y + h > wa.Bottom) y = Math.Max(wa.Top, wa.Bottom - h - 4);
            if (y < wa.Top) y = wa.Top;
            if (x + w > wa.Right) x = Math.Max(wa.Left, wa.Right - w - 4);
            Bounds = new Rectangle(x, y, w, h);
            using (var p = Theme.Round(new Rectangle(0, 0, w, h), 10)) Region = new Region(p);
            Show();
        }

        int IndexAt(Point p) { int i = (p.Y - PadV) / RowH; return p.Y < PadV || i < 0 || i >= _items.Count ? -1 : i; }

        protected override void OnMouseMove(MouseEventArgs e) { int i = IndexAt(e.Location); if (i != _hover) { _hover = i; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i >= 0) try { _pick?.Invoke(_items[i].id); } catch { }
            Close();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, ClientRectangle);
            using (var pen = new Pen(Color.FromArgb(70, 76, 82))) using (var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), 10)) g.DrawPath(pen, path);
            for (int i = 0; i < _items.Count; i++)
            {
                var r = new Rectangle(4, PadV + i * RowH, Width - 8, RowH);
                bool sel = _items[i].id == _current;
                if (i == _hover) using (var hb = new SolidBrush(Color.FromArgb(52, 58, 64))) using (var p = Theme.Round(r, 7)) g.FillPath(hb, p);
                if (sel) using (var mb = new SolidBrush(Color.FromArgb(94, 190, 155))) g.FillRectangle(mb, r.Left + 2, r.Top + 7, 3, r.Height - 14);
                TextRenderer.DrawText(g, _items[i].name, Theme.Font(9.5f, sel ? FontStyle.Bold : FontStyle.Regular),
                    new Rectangle(r.Left + 12, r.Top, r.Width - 16, r.Height), sel ? Color.FromArgb(224, 232, 228) : Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }
    }

    // ---------------------------------------------------------------- cajita para escribir desde el HUD
    // Esta sí toma el teclado (hay que escribir). Intro envía, Esc cancela; después el teclado vuelve a
    // la ventana que lo tenía (Open Rails).
    public class ChatComposeBox : Form
    {
        readonly TextBox _box;
        readonly Label _note;
        readonly Func<string, System.Threading.Tasks.Task<string>> _send;   // devuelve el error o null
        readonly IntPtr _prevFg;
        bool _busy, _closing;

        public ChatComposeBox(Rectangle over, string company, Func<string, System.Threading.Tasks.Task<string>> send)
        {
            _send = send;
            _prevFg = Native.Foreground();
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Surface2;
            KeyPreview = true;
            int h = 58;
            Bounds = new Rectangle(over.Left, over.Bottom - h, over.Width, h);
            using (var p = Theme.Round(new Rectangle(0, 0, Width, Height), 10)) Region = new Region(p);
            _box = new TextBox
            {
                BorderStyle = BorderStyle.None, BackColor = Theme.Surface2, ForeColor = Theme.Text, Font = Theme.Font(10f),
                MaxLength = 500, Bounds = new Rectangle(12, 10, Width - 24, 22),
                PlaceholderText = string.Format(I18n.T("Mensaje para {0}"), company)
            };
            _note = new Label
            {
                AutoSize = false, Bounds = new Rectangle(12, 34, Width - 24, 18), ForeColor = Theme.Subtle, Font = Theme.Font(8f),
                Text = I18n.T("Intro para enviar · Esc para cancelar"), BackColor = Theme.Surface2
            };
            Controls.Add(_box); Controls.Add(_note);
            _box.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Finish(); }
                else if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    string t = _box.Text.Trim();
                    if (t.Length == 0 || _busy) return;
                    _busy = true; _note.Text = I18n.T("Enviando…"); _note.ForeColor = Theme.Subtle;
                    string err = null;
                    try { err = await _send(t); } catch (Exception ex) { err = ex.Message; }
                    _busy = false;
                    if (IsDisposed) return;
                    if (err == null) Finish();
                    else { _note.Text = err; _note.ForeColor = Color.FromArgb(229, 115, 115); }
                }
            };
            Deactivate += (s, e) => { if (!_closing && !_busy) { _closing = true; BeginInvoke((Action)Close); } };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(94, 190, 155), 1.5f);
            using var p = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), 10);
            e.Graphics.DrawPath(pen, p);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Native.ForceForeground(Handle);
            _box.Focus();
        }

        // Cierra y devuelve el teclado al simulador.
        void Finish()
        {
            _closing = true;
            Close();
            if (_prevFg != IntPtr.Zero) Native.ForceForeground(_prevFg);
        }
    }
}
