// (2026-11-09) Barra de desplazamiento fina, como la de la lista de trenes del puesto (DispatchForm.Trenes.cs): guía oscura y
// tirador redondeado. Se ve solo si lo de dentro no cabe. Get dice cuánto mide todo, cuánto se ve y por dónde va; Set mueve.
// ForRichText la pone a un RichTextBox (sin su barra de Windows): el chat de la sala del puesto.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class ThinScrollBar : Control
    {
        public static readonly Color TrackCol = Color.FromArgb(34, 39, 46), ThumbCol = Color.FromArgb(96, 106, 118), ThumbDragCol = Color.FromArgb(150, 160, 172);
        public Func<(int total, int view, int pos)> Get;
        public Action<int> Set;
        (int total, int view, int pos) _st;
        readonly Timer _t;
        bool _drag; int _from, _pos0;

        public ThinScrollBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Width = Theme.Px(8); BackColor = TrackCol; Cursor = Cursors.Default;
            _t = new Timer { Interval = 200 };   // (por si cambia sin avisar)
            _t.Tick += (s, e) => Sync();
            HandleCreated += (s, e) => _t.Start();
            Disposed += (s, e) => { _t.Stop(); _t.Dispose(); };
        }

        bool Has => _st.total > _st.view + 1 && _st.view > 0;
        int Max => Math.Max(0, _st.total - _st.view);

        public void Sync()
        {
            if (Get == null || IsDisposed) return;
            (int, int, int) st; try { st = Get(); } catch { return; }
            if (st == _st) return;
            _st = st;
            bool v = Has; if (Visible != v) Visible = v;
            Invalidate();
        }

        Rectangle Thumb()
        {
            if (!Has) return Rectangle.Empty;
            int th = Math.Max(Theme.Px(30), (int)((long)Height * _st.view / Math.Max(1, _st.total)));
            int ty = Max == 0 ? 0 : (int)((long)(Height - th) * Math.Max(0, Math.Min(_st.pos, Max)) / Max);
            return new Rectangle(0, ty, Width, th);
        }

        void Go(int v) { try { Set?.Invoke(Math.Max(0, Math.Min(v, Max))); } catch { } Sync(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Has)
            {
                var th = Thumb();
                if (!th.Contains(e.Location)) Go((int)((long)Max * e.Y / Math.Max(1, Height)));
                _drag = true; _from = e.Y; _pos0 = _st.pos; Capture = true; Invalidate();
            }
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag) { int track = Math.Max(1, Height - Thumb().Height); Go(_pos0 + (int)((long)(e.Y - _from) * Max / track)); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_drag) { _drag = false; Capture = false; Invalidate(); }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(TrackCol);
            if (!Has) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var th = Thumb();
            using var path = Round(new Rectangle(th.X + 1, th.Y + 1, th.Width - 2, th.Height - 2), Theme.Px(3));
            using var b = new SolidBrush(_drag ? ThumbDragCol : ThumbCol);
            g.FillPath(b, path);
        }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = Math.Max(1, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        // ── para un RichTextBox (se le quita su barra de Windows)
        const int EM_GETSCROLLPOS = 0x0400 + 221, EM_SETSCROLLPOS = 0x0400 + 222;
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref Point p);

        public static ThinScrollBar ForRichText(RichTextBox rtb)
        {
            rtb.ScrollBars = RichTextBoxScrollBars.None;
            var bar = new ThinScrollBar { Dock = DockStyle.Right, Visible = false };
            bar.Get = () =>
            {
                if (!rtb.IsHandleCreated) return (0, 0, 0);
                var p = Point.Empty; SendMessage(rtb.Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref p);
                int total = 0;
                if (rtb.TextLength > 0) total = rtb.GetPositionFromCharIndex(rtb.TextLength - 1).Y + p.Y + rtb.Font.Height + Theme.Px(4);
                return (total, rtb.ClientSize.Height, p.Y);
            };
            bar.Set = v => { if (!rtb.IsHandleCreated) return; var p = new Point(0, v); SendMessage(rtb.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref p); };
            rtb.VScroll += (s, e) => bar.Sync();
            rtb.TextChanged += (s, e) => bar.Sync();
            rtb.Resize += (s, e) => bar.Sync();
            return bar;
        }
    }
}
