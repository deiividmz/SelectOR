// PUESTO DE MANDO: avisos de Tab (1.2.75). Cuando un maquinista pide paso (Tab), el despachador de la sala lo cuenta al
// puesto («SELECTOR TAB jugador señal 0|1|2 motivo»): aquí salta una notificación arriba a la derecha con un «din-don», la
// dependencia de esa señal se marca con 🔔 en la lista y en el plano general y la señal parpadea en el esquema. Un clic en
// la notificación abre esa dependencia, centrada en la señal.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class DispatchForm
    {
        // State: 0 no · 1 sí · 2 espera al jefe de circulación (con el mando: «Dar paso» y «No»)
        sealed class Notice { public string User, Text, Zone, Answer; public int Sig, State; public DateTime At; public RectangleF Box, BtnOk, BtnNo; }
        readonly List<Notice> _notices = new();
        readonly Dictionary<int, DateTime> _flash = new();
        readonly HashSet<string> _unread = new(StringComparer.Ordinal);
        Dictionary<int, string> _sigZone;   // número de señal → zona donde sale (se prepara en segundo plano)
        Dictionary<int, string> _nodeZone;  // nodo de cada aguja → su zona (la de cada señal y aguja es una sola: la primera, estaciones antes)
        Dictionary<int, string> _edgeZone;  // tramo → su zona (para el plano general)
        static byte[] _chime;

        // (Prepare) qué zona tiene cada señal: se preparan todos los esquemas en segundo plano (también sirve para que
        // cambiar de estación vaya al momento)
        void IndexSignals()
        {
            var g = _g; var geo = _geo; var zones = _zones; if (g == null || zones == null) return;
            Task.Run(() =>
            {
                var idx = new Dictionary<int, string>(); var nidx = new Dictionary<int, string>(); var eidx = new Dictionary<int, string>(); var built = new Dictionary<string, Schematic>(StringComparer.Ordinal);
                foreach (var z in zones.OrderBy(z => z.Line ? 1 : 0))
                {
                    Schematic s = null;
                    try { s = Schematic.Build(g, geo, z); } catch { }
                    built[z.Name] = s;
                    if (s != null) foreach (var sg in s.Signals) if (sg.Sig?.Obj >= 0 && !idx.ContainsKey(sg.Sig.Obj)) idx[sg.Sig.Obj] = z.Name;
                    if (s != null) foreach (int n in s.Switches.Select(w => w.Node).Concat(s.SlipNodes)) if (n > 0 && !nidx.ContainsKey(n)) nidx[n] = z.Name;
                    if (s != null) foreach (int e in s.DrawnEdges) if (!eidx.ContainsKey(e)) eidx[e] = z.Name;
                }
                return (idx, nidx, eidx, built);
            }).ContinueWith(t =>
            {
                if (IsDisposed || t.IsFaulted) return;
                _sigZone = t.Result.idx; _nodeZone = t.Result.nidx; _edgeZone = t.Result.eidx;
                UpdateJefe(); _canvas.Invalidate();
                foreach (var kv in t.Result.built) if (!_cache.ContainsKey(kv.Key)) _cache[kv.Key] = kv.Value;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // (desde la conexión con la sala, en su hilo)
        void OnTab(string user, int sig, int state, string text)
        {
            try { if (!IsDisposed) BeginInvoke(new Action(() => AddNotice(user, sig, state, text))); } catch { }
        }

        void AddNotice(string user, int sig, int state, string text)
        {
            string zone = sig >= 0 && _sigZone != null && _sigZone.TryGetValue(sig, out var z) ? z : null;
            // la respuesta a un Tab que esperaba al jefe sustituye a su aviso
            if (state != 2) _notices.RemoveAll(q => q.State == 2 && q.User == user);
            _notices.Insert(0, new Notice { User = user, Sig = sig, State = state, Text = text, Zone = zone, At = DateTime.Now });
            while (_notices.Count > 6) { var old = _notices.LastOrDefault(q => q.State != 2) ?? _notices[^1]; _notices.Remove(old); }
            if (sig >= 0) _flash[sig] = DateTime.Now;
            // la 🔔, en la dependencia de esa señal (si no es la que se ve)
            if (zone != null && zone != _curZone) { _unread.Add(zone); _list.Invalidate(); }
            Chime();
            FlashTaskbar();
            _timer.Interval = 250;
            _canvas.Invalidate();
        }

        // aviso sonoro: «din-don» (dos notas), hecho aquí mismo (sin archivos)
        static void Chime()
        {
            try
            {
                if (_chime == null)
                {
                    const int rate = 22050; var samples = new List<short>();
                    foreach (var (hz, sec) in new[] { (880.0, 0.16), (660.0, 0.34) })
                    {
                        int n = (int)(rate * sec);
                        for (int i = 0; i < n; i++)
                        {
                            double t = (double)i / rate, env = Math.Min(1, i / 200.0) * Math.Exp(-t * 5);
                            samples.Add((short)(Math.Sin(2 * Math.PI * hz * t) * env * 9000));
                        }
                    }
                    using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
                    int bytes = samples.Count * 2;
                    w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVE".ToCharArray());
                    w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                    w.Write("data".ToCharArray()); w.Write(bytes); foreach (var v in samples) w.Write(v);
                    _chime = ms.ToArray();
                }
                new System.Media.SoundPlayer(new MemoryStream(_chime)).Play();
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)] struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
        void FlashTaskbar()
        {
            try
            {
                if (Form.ActiveForm == this) return;
                var fi = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = Handle, dwFlags = 3 | 12, uCount = 3, dwTimeout = 0 };   // título y barra, hasta que se active
                FlashWindowEx(ref fi);
            }
            catch { }
        }

        // (Live) las señales que parpadean (25 s desde que se pidió paso)
        void AddFlashes(SchematicLive live)
        {
            foreach (var k in _flash.Where(kv => (DateTime.Now - kv.Value).TotalSeconds > 25).Select(kv => kv.Key).ToList()) _flash.Remove(k);
            foreach (var k in _flash.Keys) live.Flash.Add(k);
            if (_flash.Count == 0 && _timer.Interval != 1000) _timer.Interval = 1000;
        }

        // (Paint_) las notificaciones, arriba a la derecha (se van al minuto)
        void DrawNotices(Graphics g, Rectangle area)
        {
            _notices.RemoveAll(n => (DateTime.Now - n.At).TotalSeconds > (n.State == 2 && n.Answer == null ? 300 : 60));
            if (_notices.Count == 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = Theme.Px(340), x = area.Right - w - Theme.Px(12), y = area.Top + Theme.Px(12);
            using var f1 = Theme.Font(9f, FontStyle.Bold); using var f2 = Theme.Font(8.3f); using var fb = new Font("Segoe UI Symbol", 10f);
            foreach (var n in _notices)
            {
                bool ask = n.State == 2 && n.Answer == null && MySig(n.Sig);
                string verdict = n.Answer ?? (n.State == 1 ? Tr("concedido") : n.State == 2 ? Tr("espera al jefe de circulación") : Tr("no concedido"));
                string t2 = (n.Zone != null ? n.Zone + " · " : "") + verdict + (string.IsNullOrEmpty(n.Text) || n.Answer != null ? "" : ": " + n.Text) + $"  ({n.At:HH:mm:ss})";
                float t2h = Math.Min(Theme.Px(50), g.MeasureString(t2, f2, (int)(w - 34)).Height + 2);   // de una a tres líneas
                float h = Theme.Px(30) + t2h + (ask ? Theme.Px(34) : Theme.Px(4));
                var box = new RectangleF(x, y, w, h); n.Box = box; n.BtnOk = n.BtnNo = RectangleF.Empty;
                double age = (DateTime.Now - n.At).TotalSeconds, life = n.State == 2 && n.Answer == null ? 300 : 60;
                int alpha = age < life - 15 ? 240 : (int)(240 * Math.Max(0, (life - age) / 15));
                using (var bg = new SolidBrush(Color.FromArgb(alpha, 26, 31, 38))) g.FillRectangle(bg, box);
                var accent = n.State == 1 ? Color.FromArgb(alpha, 102, 187, 106) : n.State == 2 ? Color.FromArgb(alpha, 79, 195, 247) : Color.FromArgb(alpha, 255, 167, 38);
                using (var ab = new SolidBrush(accent)) g.FillRectangle(ab, box.X, box.Y, 4, box.Height);
                using (var bp = new Pen(Color.FromArgb(alpha / 2, 120, 130, 140), 1f)) g.DrawRectangle(bp, box.X, box.Y, box.Width, box.Height);
                string t1 = string.Format(Tr("{0} pide paso (Tab)"), n.User) + (n.Sig >= 0 ? " · " + string.Format(Tr("señal {0}"), n.Sig) : "");
                using (var bb = new SolidBrush(Color.FromArgb(alpha, 255, 213, 79))) g.DrawString("🔔", fb, bb, box.X + 8, box.Y + 5);   // la campana (Segoe UI Symbol: GDI+ no busca emojis en otras fuentes)
                using (var tb = new SolidBrush(Color.FromArgb(alpha, Theme.Text))) g.DrawString(t1, f1, tb, new RectangleF(box.X + 30, box.Y + 6, box.Width - 34, 18));
                using (var sb = new SolidBrush(Color.FromArgb(alpha, accent))) g.DrawString(t2, f2, sb, new RectangleF(box.X + 30, box.Y + 26, box.Width - 34, t2h));
                if (ask)
                {
                    float bw = Theme.Px(110), bh = Theme.Px(24), by = box.Y + 26 + t2h + Theme.Px(4);
                    n.BtnOk = new RectangleF(box.X + 30, by, bw, bh); n.BtnNo = new RectangleF(box.X + 30 + bw + Theme.Px(8), by, bw, bh);
                    using (var b1 = new SolidBrush(Color.FromArgb(alpha, 46, 125, 50))) g.FillRectangle(b1, n.BtnOk);
                    using (var b2 = new SolidBrush(Color.FromArgb(alpha, 120, 40, 40))) g.FillRectangle(b2, n.BtnNo);
                    var cf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    using (var wb = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
                    {
                        g.DrawString(Tr("Dar paso"), f1, wb, n.BtnOk, cf);
                        g.DrawString(Tr("No"), f1, wb, n.BtnNo, cf);
                    }
                }
                y += h + Theme.Px(6);
            }
        }

        // (clic en el esquema) ¿en una notificación? → a esa estación, centrada en la señal
        bool NoticeClick(Point p)
        {
            var n = _notices.FirstOrDefault(q => q.Box.Contains(p));
            if (n == null) return false;
            // con el mando: la respuesta al Tab
            if (MySig(n.Sig) && n.State == 2 && n.Answer == null && (n.BtnOk.Contains(p) || n.BtnNo.Contains(p)))
            {
                bool ok = n.BtnOk.Contains(p);
                _pilot.TabAnswer(n.User, n.Sig, ok);
                n.Answer = ok ? Tr("das paso…") : Tr("no das paso"); n.At = DateTime.Now;
                _canvas.Invalidate();
                return true;
            }
            if (n.Zone != null)
            {
                Select(n.Zone);
                var sg = _cur?.Signals.FirstOrDefault(q => q.Sig?.Obj == n.Sig);
                if (sg != null)
                {
                    _zoom = Math.Max(_zoom, 1.6f);
                    _origin = new PointF(_canvas.Width / 2f - sg.At.X * _zoom, _canvas.Height / 2f - sg.At.Y * _zoom);
                    _flash[n.Sig] = DateTime.Now; _timer.Interval = 250;
                }
            }
            _notices.Remove(n); _canvas.Invalidate();
            return true;
        }
    }
}
