// (1.2.90) El mando de la RADIO DE LA SALA: un botón «📻» que se mantiene pulsado para hablar, quién habla (con su
// «jefe de circulación»), y con el botón derecho: silenciar a cada uno, el volumen y, para el administrador, silenciar a
// alguien para todos. Va dentro del puesto de mando (RadioPanel) y, conduciendo, en una ventanita encima de Open Rails
// (RadioHud, que no quita el foco al juego).

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    public class RadioPanel : Control
    {
        public RoomRadio Radio;
        public bool Grip;               // (RadioHud) el asa de la izquierda para moverla
        public event Action<Point> GripDrag;
        bool _down, _gripDown; Point _gripAt;
        readonly Timer _blink = new Timer { Interval = 120 };
        int _phase;
        static readonly Color Red = Color.FromArgb(229, 80, 80), Amber = Color.FromArgb(255, 190, 70);

        public RadioPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface; Height = Theme.Px(44); Cursor = Cursors.Hand;
            RoomRadio.AnyChanged += OnChanged;
            _blink.Tick += (s, e) => { _phase++; Invalidate(); };
            _blink.Start();
        }

        void OnChanged() { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)Invalidate); }

        Rectangle GripRect => Grip ? new Rectangle(0, 0, Theme.Px(16), Height) : Rectangle.Empty;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Grip && GripRect.Contains(e.Location)) { _gripDown = true; _gripAt = e.Location; return; }
            if (e.Button == MouseButtons.Left && Radio != null) { _down = true; Radio.SetButton(true); Invalidate(); }
            if (e.Button == MouseButtons.Right) ShowMenu(e.Location);
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e) { if (_gripDown) GripDrag?.Invoke(new Point(e.X - _gripAt.X, e.Y - _gripAt.Y)); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_gripDown) { _gripDown = false; return; }
            if (_down) { _down = false; Radio?.SetButton(false); Invalidate(); }
            base.OnMouseUp(e);
        }
        protected override void OnMouseCaptureChanged(EventArgs e) { if (_down && !Capture) { _down = false; Radio?.SetButton(false); } base.OnMouseCaptureChanged(e); }

        void ShowMenu(Point at)
        {
            var r = Radio; if (r == null) return;
            var m = new ContextMenuStrip { ShowImageMargin = false, BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.Font(9.5f) };
            m.Items.Add(new ToolStripLabel(I18n.T("Silenciar en mi radio")) { ForeColor = Theme.Subtle });
            var others = r.Who.Where(n => n != r.Me).ToList();
            if (others.Count == 0) m.Items.Add(new ToolStripLabel("   " + I18n.T("(no hay nadie más en la sala)")) { ForeColor = Theme.Subtle });
            foreach (var n in others)
            {
                string who = n; bool muted = RoomRadio.IsMuted(n);
                var it = new ToolStripMenuItem((muted ? "🔇  " : "🔊  ") + n + (r.GMuted.Contains(n) ? "  · " + I18n.T("silenciado por el administrador") : "")) { Checked = muted, ForeColor = Theme.Text };
                it.Click += (s, e) => r.Mute(who, !RoomRadio.IsMuted(who));
                m.Items.Add(it);
            }
            if (Supa.IsSuperadmin && others.Count > 0)
            {
                m.Items.Add(new ToolStripSeparator());
                var adm = new ToolStripMenuItem(I18n.T("Silenciar para todos (administrador)")) { ForeColor = Theme.Text };
                foreach (var n in others)
                {
                    string who = n; bool on = r.GMuted.Contains(n);
                    var it = new ToolStripMenuItem(n) { Checked = on, ForeColor = Color.Black };
                    it.Click += (s, e) => r.GMute(who, !on);
                    adm.DropDownItems.Add(it);
                }
                m.Items.Add(adm);
            }
            m.Items.Add(new ToolStripSeparator());
            var vol = new ToolStripMenuItem(I18n.T("Volumen")) { ForeColor = Theme.Text };
            foreach (int v in new[] { 50, 75, 100, 125, 150, 200 })
            {
                int vv = v;
                var it = new ToolStripMenuItem(v + " %") { Checked = (RoomRadio.Prefs?.RadioVolume ?? 100) == v, ForeColor = Color.Black };
                it.Click += (s, e) => { if (RoomRadio.Prefs != null) { RoomRadio.Prefs.RadioVolume = vv; try { RoomRadio.Prefs.Save(); } catch { } } RoomRadio.ApplyVolume(); };
                vol.DropDownItems.Add(it);
            }
            m.Items.Add(vol);
            m.Items.Add(new ToolStripLabel(string.Format(I18n.T("Para hablar: mantén pulsado el botón o «{0}» (Ajustes → Mandos)"), KeyLabel())) { ForeColor = Theme.Subtle });
            m.Closed += (s, e) => BeginInvoke((Action)(() => m.Dispose()));
            m.Show(this, at);
        }

        public static string KeyLabel()
        {
            string id = RoomRadio.Prefs?.RadioKey ?? "RControl";
            foreach (var k in RoomRadio.Keys) if (k.id == id) return I18n.T(k.label);
            return id;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            var r = Radio;
            var rc = new Rectangle(0, 0, Width - 1, Height - 1);
            bool mine = r?.Mine == true, someone = r?.Talking != null && !mine;
            Color edge = mine ? Red : someone ? (r.TalkingJefe ? Amber : Theme.AccentHi) : Theme.Border;
            Theme.FillRound(g, rc, Theme.Px(10), _down || mine ? Color.FromArgb(70, 40, 40) : Color.FromArgb(32, 36, 40));
            using (var p = new Pen(edge, mine || someone ? 2f : 1f)) using (var path = Theme.Round(rc, Theme.Px(10))) g.DrawPath(p, path);
            int x = Theme.Px(8);
            if (Grip)
            {
                using var gb = new SolidBrush(Theme.Subtle);
                for (int i = 0; i < 3; i++) for (int j = 0; j < 2; j++) g.FillEllipse(gb, Theme.Px(4 + j * 4), Height / 2 - Theme.Px(6) + i * Theme.Px(5), 2.5f, 2.5f);
                x = Theme.Px(18);
            }
            // la luz: roja al hablar, verde/ámbar parpadeando cuando habla otro
            int d = Theme.Px(10); var led = new Rectangle(x, (Height - d) / 2, d, d);
            Color lc = mine ? Red : someone ? ((_phase / 3) % 2 == 0 ? edge : Color.FromArgb(90, edge)) : Color.FromArgb(70, 78, 86);
            using (var lb = new SolidBrush(lc)) g.FillEllipse(lb, led);
            x = led.Right + Theme.Px(8);
            using var f1 = Theme.Font(9.5f, FontStyle.Bold); using var f2 = Theme.Font(8f);
            string top = "📻  " + (mine ? I18n.T("Hablando…") : someone ? (r.TalkingJefe ? string.Format(I18n.T("{0} (jefe de circulación)"), r.Talking) : r.Talking) : I18n.T("Radio de la sala"));
            string sub = r == null ? I18n.T("Sin conexión con la sala")
                       : r.FreshNote ?? (someone ? I18n.T("está hablando") : string.Format(I18n.T("Mantén pulsado para hablar · «{0}»"), KeyLabel()));
            int w = Width - x - Theme.Px(8);
            ColorText.Text(g, top, f1, new Rectangle(x, Theme.Px(5), w, Theme.Px(18)), mine ? Color.White : Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, sub, f2, new Rectangle(x, Theme.Px(23), w, Theme.Px(16)), r?.FreshNote != null ? Amber : Theme.Subtle, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { RoomRadio.AnyChanged -= OnChanged; _blink.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // Conduciendo: el mando de la radio en una ventanita encima de Open Rails, sin quitarle el foco (se mueve por el asa)
    public sealed class RadioHud : Form
    {
        public readonly RadioPanel Panel;
        readonly AppPrefs _prefs;
        public RadioHud(AppPrefs prefs, RoomRadio radio)
        {
            _prefs = prefs;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(32, 36, 40);
            Size = new Size(Theme.Px(360), Theme.Px(44));   // (300 cortaba «Mantén pulsado para hablar · «Control derecho»»)
            Panel = new RadioPanel { Dock = DockStyle.Fill, Radio = radio, Grip = true, BackColor = BackColor };
            Panel.GripDrag += dp => { Location = new Point(Left + dp.X, Top + dp.Y); };
            Panel.MouseUp += (s, e) => { _prefs.RadioHudX = Left; _prefs.RadioHudY = Top; try { _prefs.Save(); } catch { } };
            Controls.Add(Panel);
            var wa = Screen.PrimaryScreen.WorkingArea;
            var at = new Point(prefs.RadioHudX, prefs.RadioHudY);
            // de serie abajo en el centro: abajo a la izquierda está el HUD de SelectOR
            Location = prefs.RadioHudX != int.MinValue && Screen.AllScreens.Any(s => s.WorkingArea.Contains(at)) ? at : new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - Theme.Px(16));
            using (var path = Theme.Round(new Rectangle(0, 0, Width, Height), Theme.Px(10))) Region = new Region(path);
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x00000080 /*TOOLWINDOW*/; return cp; } }
    }
}
