// (2026-11-10, petición) EL SENTIDO DE BLOQUEO EN EL PUESTO DE MANDO. En cada vía de cada trayecto de línea (Trayectos.cs), una
// flecha hacia la estación a la que va el bloqueo: verde con la vía libre, roja ocupada; gris sin sentido establecido. Lo lleva
// el despachador de la sala (Bloqueo.cs del despachador): sin jefe de circulación en el trayecto, automático (lo pone el primer
// tren que entra); con jefe, lo cambia él aquí: clic en la flecha → hacia una estación u otra, o sin sentido (solo con la vía
// libre; si no, el despachador dice por qué). El administrador, en cualquiera.
// Los trayectos se calculan aquí con los mismos datos que el despachador (bloqueos.txt): si la huella no coincide (otra versión),
// no se dibuja nada (y se dice en la barra de abajo). Se apagan en Capas → «Sentido de bloqueo»; en el trazado real, solo de
// cerca (como las señales).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class DispatchForm
    {
        static bool _showBlocks = true;
        List<(Trayectos.Trayecto t, Trayectos.Via v, Trayectos.Item mid)> _bvias;   // en el orden del despachador
        string _bhash; bool _bBuilding, _bHooked, _bFailed, _bMismatchSaid;
        readonly List<(RectangleF r, int idx)> _bHits = new();

        void BlocksPrepare()
        {
            if (_bvias != null || _bBuilding || _bFailed || _g == null || _zones == null) return;
            _bBuilding = true;
            var g = _g; var zl = _zones.ToList();
            System.Threading.Tasks.Task.Run(() => { try { return Trayectos.Build(g, zl); } catch { return null; } })
                .ContinueWith(t =>
                {
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            _bBuilding = false;
                            var l = t.Result;
                            if (l == null) { _bFailed = true; return; }   // (no se vuelve a intentar en cada repintado)
                            _bvias = l.SelectMany(tr => tr.Vias.Select(v => (tr, v, v.Items.OrderByDescending(q => q.To - q.From).First()))).ToList();
                            _bhash = Trayectos.Hash(l);
                            _canvas.Invalidate();
                        }));
                    }
                    catch { }
                });
        }

        void BlocksHook()
        {
            if (_bHooked || _pilot == null) return;
            _bHooked = true;
            _pilot.BlocksChanged += () => { try { if (!IsDisposed) BeginInvoke(new Action(() => _canvas.Invalidate())); } catch { } };
            // «OK …» / «NO …» (y las de antes, sin nada delante: como bien)
            _pilot.BlockReply += text =>
            {
                bool ok = !text.StartsWith("NO ", StringComparison.Ordinal);
                if (text.StartsWith("OK ", StringComparison.Ordinal) || text.StartsWith("NO ", StringComparison.Ordinal)) text = text.Substring(3);
                try { if (!IsDisposed) BeginInvoke(new Action(() => ItinSay(ItinText(text), ok))); } catch { }
            };
        }

        // el estado de cada vía, si es de los mismos trayectos que el despachador
        string[] BlockStates()
        {
            BlocksHook(); BlocksPrepare();
            var st = _pilot?.Blocks;
            if (st == null || _bvias == null) return null;
            if (st.Length != _bvias.Count || _pilot.BlockHash != _bhash)
            {
                if (!_bMismatchSaid) { _bMismatchSaid = true; _status.Text = Tr("Sentido de bloqueo: la sala usa otra versión de los trayectos; no se dibuja."); }
                return null;
            }
            return st;
        }

        // punto y sentido (hacia +metro del tramo) en pantalla; false si no se ve en esta vista
        bool BlockAt(int e, double off, out PointF p, out PointF dir)
        {
            p = dir = PointF.Empty;
            double L = _g.Edges[e].Len, o2 = Math.Min(L, off + 15), o1 = Math.Max(0, off - 15);
            PointF a, b;
            if (Board && _board != null)
            {
                var pa = _board.At(e, o1); var pb = _board.At(e, o2);
                if (float.IsNaN(pa.X) || float.IsNaN(pb.X)) return false;
                a = new PointF(_bOrigin.X + pa.X * _bZoom, _bOrigin.Y + pa.Y * _bZoom); b = new PointF(_bOrigin.X + pb.X * _bZoom, _bOrigin.Y + pb.Y * _bZoom);
            }
            else if (!Overview && _cur != null)
            {
                if (!_cur.MapOff(e, o1, out var pa) || !_cur.MapOff(e, o2, out var pb)) return false;
                a = new PointF(_origin.X + pa.X * _zoom, _origin.Y + pa.Y * _zoom); b = new PointF(_origin.X + pb.X * _zoom, _origin.Y + pb.Y * _zoom);
            }
            else return false;
            float dx = b.X - a.X, dy = b.Y - a.Y, m = (float)Math.Sqrt(dx * dx + dy * dy);
            if (m < 0.01f) return false;
            p = new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2); dir = new PointF(dx / m, dy / m);
            return true;
        }

        // (al pintar, sobre la vía)
        void DrawBlocks(Graphics g, Rectangle area)
        {
            _bHits.Clear();
            if (!_showBlocks) return;
            try
            {
                var st = BlockStates(); if (st == null) return;
                if (!Board && (Overview || _cur == null)) return;
                if (Board && _bZoom < 0.3f) return;   // (el trazado real de lejos: no caben; de cerca, como las señales)
                var zs = ViewZones(); var zset = zs == null ? null : new HashSet<string>(zs, StringComparer.Ordinal);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float s = Theme.Px(7);
                for (int i = 0; i < _bvias.Count && i < st.Length; i++)
                {
                    var (t, v, it) = _bvias[i];
                    string c = st[i];
                    if (c == "-" || c.Length < 3) continue;   // (vía sin cantones: sin bloqueo)
                    if (zset != null && !t.Zones.Any(zset.Contains)) continue;
                    if (!BlockAt(it.Edge, (it.From + it.To) / 2, out var p, out var d)) continue;
                    if (!area.Contains(Point.Round(p))) continue;
                    char dir = c[0]; bool occ = c[1] == '1'; bool jefe = c[2] == 'j';
                    // hacia B: el sentido del tramo si al ir de A a B del tramo se va hacia B
                    if (dir == 'B' && !it.ToB || dir == 'A' && it.ToB) d = new PointF(-d.X, -d.Y);
                    var col = dir == '0' ? Color.FromArgb(150, 150, 158) : occ ? Color.FromArgb(232, 80, 72) : Color.FromArgb(92, 196, 104);
                    var n = new PointF(-d.Y, d.X);
                    PointF P(float along, float side) => new PointF(p.X + d.X * along + n.X * side, p.Y + d.Y * along + n.Y * side);
                    var box = new RectangleF(p.X - s * 1.9f, p.Y - s * 1.9f, s * 3.8f, s * 3.8f);
                    using (var bg = new SolidBrush(Color.FromArgb(215, 14, 16, 20))) g.FillEllipse(bg, box);
                    using (var bp = new Pen(Color.FromArgb(jefe ? 230 : 150, jefe ? Color.FromArgb(255, 213, 79) : col), jefe ? 1.6f : 1.1f)) g.DrawEllipse(bp, box);
                    if (dir == '0')
                    {
                        // sin sentido: una raya
                        using var pen = new Pen(col, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                        g.DrawLine(pen, P(-s * 1.1f, 0), P(s * 1.1f, 0));
                    }
                    else using (var br = new SolidBrush(col)) g.FillPolygon(br, new[] { P(s * 1.3f, 0), P(-s * 0.9f, s * 0.95f), P(-s * 0.4f, 0), P(-s * 0.9f, -s * 0.95f) });
                    _bHits.Add((box, i));
                }
            }
            catch { _bHits.Clear(); }   // (un fallo aquí no deja el puesto sin dibujar)
        }

        // (clic izquierdo) el menú de esa vía; true si era una flecha
        bool BlockClick(Point p)
        {
            var hit = _bHits.LastOrDefault(h => h.r.Contains(p));
            if (hit.r.IsEmpty || _bvias == null) return false;
            var st = BlockStates(); if (st == null || hit.idx >= st.Length) return false;
            var (t, v, _) = _bvias[hit.idx];
            string c = st[hit.idx]; if (c.Length < 3) return false;
            char dir = c[0]; bool occ = c[1] == '1'; bool jefe = c[2] == 'j';
            string name = t.Vias.Count > 1 ? t.Name + ", " + Tr("vía") + " " + (v.K + 1) : t.Name;
            var m = MenuStyle.Apply(new ContextMenuStrip());
            m.Items.Add(new ToolStripMenuItem(name) { Enabled = false });
            m.Items.Add(new ToolStripMenuItem((dir == '0' ? Tr("Sin sentido de bloqueo") : Tr("Sentido de bloqueo hacia") + " " + (dir == 'A' ? t.A : t.B))
                                              + " · " + (occ ? Tr("ocupada") : Tr("libre")) + " · " + (jefe ? Tr("lo lleva el jefe de circulación") : Tr("automático"))) { Enabled = false });
            bool can = IsAdmin || (Me != null && t.Zones.Any(Mine));
            if (can)
            {
                m.Items.Add(new ToolStripSeparator());
                void Opt(string text, char d) { var mi = new ToolStripMenuItem(text, null, (s, e) => _pilot?.SetBlock(hit.idx, d)) { Enabled = d != dir && !occ, Checked = d == dir }; m.Items.Add(mi); }
                Opt(Tr("Hacia") + " " + t.A, 'A');
                Opt(Tr("Hacia") + " " + t.B, 'B');
                Opt(Tr("Sin sentido establecido"), '0');
                if (occ) m.Items.Add(new ToolStripMenuItem(Tr("Solo se cambia con la vía libre")) { Enabled = false });
            }
            else m.Items.Add(new ToolStripMenuItem(jefe ? Tr("Lo cambia el jefe de circulación del trayecto") : Tr("Automático: lo pone el primer tren que entra")) { Enabled = false });
            m.Show(_canvas, p);
            return true;
        }
    }
}
