// (2026-11-09) Los túneles en el puesto (Tuneles.cs): en el trazado real y en el esquema de cada dependencia, el tramo de
// vía en túnel con su contorno a los dos lados (discontinuo) y las bocas en los extremos; con zoom, el rótulo «Túnel».
// (2026-11-10, petición) En el esquema, los de vías paralelas y los seguidos en una misma vía (menos de 200 m entre ellos), en uno.
// Se pueden quitar en «Capas».

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;

namespace SelectOR
{
    public sealed partial class DispatchForm
    {
        static bool _showTunnels = true;
        List<Tuneles.Tunnel> _tunnels; bool _tunnelsAsked;

        // (LayersTick) una vez, cuando ya está el grafo
        void TunnelsTick()
        {
            if (_tunnelsAsked || _g == null) return;
            _tunnelsAsked = true;
            var g = _g;
            Tuneles.For(_routeDir, g).ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion || IsDisposed) return;
                try { BeginInvoke((Action)(() => { _tunnels = t.Result; _canvas.Invalidate(); })); } catch { }
            });
        }

        // (Paint_, tras el trazado real o el esquema)
        void DrawTunnels(Graphics g, Rectangle area)
        {
            if (!_showTunnels || _tunnels == null || _tunnels.Count == 0) return;
            bool board = Board && _board != null;
            if (!board && (Overview || _cur == null)) return;
            float zoom = board ? _bZoom : _zoom;
            float w = board ? Math.Max(Theme.Px(3.5f), Math.Min(Theme.Px(7), 26f * zoom)) : Theme.Px(6);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var view = RectangleF.Inflate(area, 40, 40);
            using var casing = new Pen(Color.FromArgb(200, 150, 160, 176), 1.4f) { DashPattern = new[] { 4f, 2.5f } };
            using var portal = new Pen(Color.FromArgb(230, 182, 190, 202), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var fill = new SolidBrush(Color.FromArgb(46, 120, 130, 145));
            using var lf = Theme.Font(7.6f, FontStyle.Bold);
            using var lb = new SolidBrush(Color.FromArgb(200, 182, 190, 202));
            using var lbg = new SolidBrush(Color.FromArgb(200, 18, 21, 26));
            bool labels = board ? _bZoom >= 0.35f : true;
            var taken = new List<RectangleF>();
            // el túnel de una vía: el contorno a los dos lados, sombreado, con sus bocas y el rótulo
            void DrawOne(PointF[] sp)
            {
                var left = Offset(sp, w); var right = Offset(sp, -w);
                using (var path = new GraphicsPath())
                {
                    path.AddLines(left); path.AddLines(right.Reverse().ToArray()); path.CloseFigure();
                    g.FillPath(fill, path);
                }
                g.DrawLines(casing, left); g.DrawLines(casing, right);
                Mouth(left[0], right[0], Dir(sp[1], sp[0]));
                Mouth(left[^1], right[^1], Dir(sp[^2], sp[^1]));
                if (labels) Label(sp[sp.Length / 2], Normal(sp, sp.Length / 2), w);
            }
            // las bocas: un trazo de un lado a otro, con las alas hacia fuera
            void Mouth(PointF a, PointF b, PointF outDir)
            {
                g.DrawLine(portal, a, b);
                g.DrawLine(portal, a, new PointF(a.X + outDir.X * w * 0.8f + (a.X - b.X) * 0.25f, a.Y + outDir.Y * w * 0.8f + (a.Y - b.Y) * 0.25f));
                g.DrawLine(portal, b, new PointF(b.X + outDir.X * w * 0.8f + (b.X - a.X) * 0.25f, b.Y + outDir.Y * w * 0.8f + (b.Y - a.Y) * 0.25f));
            }
            void Label(PointF mid, PointF n, float off)
            {
                string t = Tr("Túnel");
                var sz = g.MeasureString(t, lf);
                var at = new PointF(mid.X + n.X * (off + 10) - sz.Width / 2, mid.Y + n.Y * (off + 10) - sz.Height / 2);
                var r = new RectangleF(at.X - 3, at.Y - 1, sz.Width + 6, sz.Height + 2);
                if (!taken.Any(o => o.IntersectsWith(r))) { taken.Add(r); g.FillRectangle(lbg, r); g.DrawString(t, lf, lb, at); }
            }
            // cada túnel, en pantalla
            var items = new List<(int i, PointF[] sp, RectangleF bb)>();
            for (int i = 0; i < _tunnels.Count; i++)
            {
                var tn = _tunnels[i];
                PointF[] pc;
                try
                {
                    if (board) pc = _board.Piece(tn.E, tn.A, tn.B);
                    else { if (!_cur.Drawn(tn.E)) continue; pc = _cur.Piece(tn.E, tn.A, tn.B); }
                }
                catch { continue; }
                if (pc == null || pc.Length < 2) continue;
                var sp = new PointF[pc.Length];
                for (int k = 0; k < pc.Length; k++) sp[k] = board ? new PointF(_bOrigin.X + pc[k].X * _bZoom, _bOrigin.Y + pc[k].Y * _bZoom)
                                                                   : new PointF(_origin.X + pc[k].X * _zoom, _origin.Y + pc[k].Y * _zoom);
                var bb = RectangleF.FromLTRB(sp.Min(p => p.X), sp.Min(p => p.Y), sp.Max(p => p.X), sp.Max(p => p.Y));
                if (bb.Width + bb.Height < 3) continue;
                items.Add((i, sp, bb));
            }
            if (board)
            {
                foreach (var it in items) if (view.IntersectsWith(it.bb)) DrawOne(it.sp);
                return;
            }
            // (2026-11-10, petición) en el esquema, uno solo: los de vías paralelas (uno al lado del otro) y los seguidos en una misma
            // vía con poca vía entre ellos (menos de 200 m)
            var same = TunnelRuns();
            int N = items.Count; var grp = Enumerable.Range(0, N).ToArray();
            int Find(int x) { while (grp[x] != x) x = grp[x] = grp[grp[x]]; return x; }
            float rowPx = Schematic.Row * zoom;
            bool Flat(RectangleF r) => r.Height <= 2.5f;
            for (int x = 0; x < N; x++)
                for (int y = x + 1; y < N; y++)
                {
                    var A = items[x].bb; var B = items[y].bb;
                    if (!Flat(A) || !Flat(B) || Math.Abs(A.Top - B.Top) > rowPx * 1.6f + 1) continue;
                    bool sameRun = same.TryGetValue(items[x].i, out int ra) && same.TryGetValue(items[y].i, out int rb) && ra == rb;
                    float ov = Math.Min(A.Right, B.Right) - Math.Max(A.Left, B.Left);
                    bool parallel = Math.Abs(A.Top - B.Top) > 1 && ov > 0.3f * Math.Min(A.Width, B.Width);
                    if (sameRun || parallel) grp[Find(x)] = Find(y);
                }
            foreach (var gq in Enumerable.Range(0, N).GroupBy(Find))
            {
                var mem = gq.Select(k => items[k]).ToList();
                if (mem.Count == 1) { if (view.IntersectsWith(mem[0].bb)) DrawOne(mem[0].sp); continue; }
                var u = mem[0].bb; foreach (var m in mem.Skip(1)) u = RectangleF.Union(u, m.bb);
                if (!view.IntersectsWith(u)) continue;
                var box = RectangleF.FromLTRB(u.Left, u.Top - w, u.Right, u.Bottom + w);
                g.FillRectangle(fill, box);
                g.DrawLine(casing, box.Left, box.Top, box.Right, box.Top); g.DrawLine(casing, box.Left, box.Bottom, box.Right, box.Bottom);
                Mouth(new PointF(box.Left, box.Top), new PointF(box.Left, box.Bottom), new PointF(-1, 0));
                Mouth(new PointF(box.Right, box.Top), new PointF(box.Right, box.Bottom), new PointF(1, 0));
                if (labels) Label(new PointF(box.Left + box.Width / 2, box.Top), new PointF(0, -1), 0);
            }
        }

        // (2026-11-10) los túneles seguidos de una misma vía (menos de 200 m de vía entre uno y otro, en su tramo o pasando un nudo):
        // el mismo número para todos los de una tirada
        Dictionary<int, int> _tunRuns;
        Dictionary<int, int> TunnelRuns()
        {
            if (_tunRuns != null || _tunnels == null || _g == null) return _tunRuns ?? new Dictionary<int, int>();
            int n = _tunnels.Count; var p = Enumerable.Range(0, n).ToArray();
            int F(int x) { while (p[x] != x) x = p[x] = p[p[x]]; return x; }
            const double Gap = 200;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    var a = _tunnels[i]; var b = _tunnels[j];
                    double gap = double.MaxValue;
                    if (a.E == b.E) gap = Math.Max(0, Math.Max(b.A - a.B, a.A - b.B));
                    else
                    {
                        var EA = _g.Edges[a.E]; var EB = _g.Edges[b.E];
                        foreach (var (na, da) in new[] { (EA.A, a.A), (EA.B, EA.Len - a.B) })
                            foreach (var (nb, db) in new[] { (EB.A, b.A), (EB.B, EB.Len - b.B) })
                                if (na >= 0 && na == nb) gap = Math.Min(gap, da + db);
                    }
                    if (gap <= Gap) p[F(i)] = F(j);
                }
            _tunRuns = new Dictionary<int, int>(); for (int i = 0; i < n; i++) _tunRuns[i] = F(i);
            return _tunRuns;
        }

        static PointF Dir(PointF from, PointF to) { float dx = to.X - from.X, dy = to.Y - from.Y, l = (float)Math.Sqrt(dx * dx + dy * dy); return l < 1e-4f ? new PointF(1, 0) : new PointF(dx / l, dy / l); }
        static PointF Normal(PointF[] p, int i)
        {
            var a = p[Math.Max(0, i - 1)]; var b = p[Math.Min(p.Length - 1, i + 1)];
            var d = Dir(a, b); return new PointF(-d.Y, d.X);
        }
        // la línea desplazada a un lado (en pantalla), w píxeles
        static PointF[] Offset(PointF[] p, float w)
        {
            var r = new PointF[p.Length];
            for (int i = 0; i < p.Length; i++) { var n = Normal(p, i); r[i] = new PointF(p[i].X + n.X * w, p[i].Y + n.Y * w); }
            return r;
        }
    }
}
