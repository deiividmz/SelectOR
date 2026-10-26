// HORARIO DE LA SALA · RECORRIDO EN EL MAPA (1.2.79). Para un servicio de trenes IA, el administrador pincha en el mapa:
//  · la vía de salida (una vía con andén de su estación de origen) y la de llegada (una de la de destino);
//  · por qué vías quiere que pase, en orden (en los tramos de vía doble, la de la circulación normal).
// Son preferencias: el despachador de la sala las cambia si lo pide el tráfico (vía ocupada, otro tren de frente,
// atasco en la entrada de una estación). Se guardan como «o=nodo;d=nodo;v=nodo,nodo» (nodos vectoriales del .tdb;
// sql/horario-recorrido.sql: room_ai_service_route) y el despachador las lee con el horario.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class HorarioRecorridoForm : Form
    {
        static string Tr(string s) => I18n.T(s);
        readonly RouteGraph _g;
        readonly RouteLiveMap _map;
        readonly string _origin, _dest;
        readonly List<(int edge, List<(double lat, double lon)> pts)> _lines = new();
        Func<double, double, PointF> _scr;      // la proyección de la última vez que se pintó el mapa
        int _mode;                             // 0 salida · 1 llegada · 2 pasar por
        int _hover = -1;
        Point _down;
        readonly Label[] _modeBtn = new Label[3];
        readonly ListBox _vias;
        readonly Label _help;

        public int Origin = -1, Dest = -1;     // nodos vectoriales (Edge.Vec)
        public readonly List<int> Vias = new();
        public string Hint => Origin < 0 && Dest < 0 && Vias.Count == 0 ? "" :
            string.Join(";", new[] { Origin >= 0 ? "o=" + Origin : null, Dest >= 0 ? "d=" + Dest : null, Vias.Count > 0 ? "v=" + string.Join(",", Vias) : null }.Where(x => x != null));

        static readonly Color COrigin = Color.FromArgb(102, 187, 106), CDest = Color.FromArgb(239, 83, 80), CVia = Color.FromArgb(66, 165, 245);

        public HorarioRecorridoForm(string service, string origin, string dest, string hint, RouteGraph g, RouteMapData data)
        {
            _g = g; _origin = origin ?? ""; _dest = dest ?? "";
            Text = Tr("Recorrido del servicio");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(Theme.Px(1240), Theme.Px(780)); MinimumSize = new Size(Theme.Px(900), Theme.Px(560));
            DialogKit.Dress(this);
            Font = Theme.Font(9.5f);
            Parse(hint);

            var head = new DialogKit.Header(string.Format(Tr("Recorrido de {0}"), service), $"{_origin}  →  {_dest}", "🗺") { Dock = DockStyle.Top };

            var side = new Panel { Dock = DockStyle.Right, Width = Theme.Px(330), BackColor = Theme.Surface, Padding = new Padding(Theme.Px(14)) };
            _help = new Label
            {
                Dock = DockStyle.Bottom, Height = Theme.Px(150), ForeColor = Theme.Subtle, BackColor = side.BackColor, Font = Theme.Font(8.75f),
                Text = Tr("Elige qué marcar y pincha en la vía del mapa (rueda: acercar; arrastrar: mover).") + "\n\n" +
                       Tr("Son preferencias: si la vía está ocupada, hay un tren de frente o se acumulan trenes a la entrada de una estación, el despachador de la sala usa otra.")
            };
            _vias = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Surface2, ForeColor = Theme.Text, Font = Theme.Font(9f),
                IntegralHeight = false
            };
            var viaBar = new Panel { Dock = DockStyle.Bottom, Height = Theme.Px(40), BackColor = side.BackColor, Padding = new Padding(0, Theme.Px(6), 0, 0) };
            var del = DialogKit.Secondary(Tr("Quitar la elegida"), 150); del.Dock = DockStyle.Left;
            del.Click += (s, e) => { int i = _vias.SelectedIndex; if (i >= 0 && i < Vias.Count) { Vias.RemoveAt(i); Refresh0(); } };
            var clear = DialogKit.Secondary(Tr("Borrar todo"), 120); clear.Dock = DockStyle.Right;
            clear.Click += (s, e) => { Origin = Dest = -1; Vias.Clear(); Refresh0(); };
            viaBar.Controls.Add(del); viaBar.Controls.Add(clear);
            var viaCap = new Label { Dock = DockStyle.Top, Height = Theme.Px(26), Text = Tr("PASA POR (EN ORDEN)"), Font = Theme.Font(8.5f, FontStyle.Bold), ForeColor = Theme.Subtle, BackColor = side.BackColor, TextAlign = ContentAlignment.BottomLeft };
            var modes = new Panel { Dock = DockStyle.Top, Height = Theme.Px(3 * 46 + 6), BackColor = side.BackColor };
            string[] names = { Tr("① Vía de salida"), Tr("② Vía de llegada"), Tr("③ Añadir una vía de paso") };
            Color[] cols = { COrigin, CDest, CVia };
            for (int k = 0; k < 3; k++)
            {
                int kk = k;
                var b = new Label
                {
                    Location = new Point(0, k * Theme.Px(46)), Size = new Size(Theme.Px(300), Theme.Px(40)), Cursor = Cursors.Hand, TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(Theme.Px(10), 0, 0, 0), Font = Theme.Font(9.5f, FontStyle.Bold), Tag = cols[k]
                };
                b.Click += (s, e) => { _mode = kk; Refresh0(); };
                _modeBtn[k] = b; modes.Controls.Add(b);
                b.Text = names[k];
            }
            side.Controls.Add(_vias); side.Controls.Add(viaCap); side.Controls.Add(viaBar); side.Controls.Add(_help); side.Controls.Add(modes);

            _map = new RouteLiveMap { Dock = DockStyle.Fill };
            _map.SetData(data);
            _map.Overlay = (gr, scr) => { _scr = scr; Draw(gr, scr); };
            _map.MouseDown += (s, e) => _down = e.Location;
            _map.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left || Math.Abs(e.X - _down.X) + Math.Abs(e.Y - _down.Y) > 5) return;
                int edge = EdgeAt(e.Location); if (edge >= 0) Pick(edge);
            };
            _map.MouseMove += (s, e) => { int h = EdgeAt(e.Location); if (h != _hover) { _hover = h; _map.Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; _map.Invalidate(); } };

            var foot = new DialogKit.Footer();
            var ok = DialogKit.Primary(Tr("Guardar"), 140);
            var cancel = DialogKit.Secondary(Tr("Cancelar"), 120);
            ok.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            foot.AddRight(cancel, ok);

            Controls.Add(_map); Controls.Add(side); Controls.Add(head); Controls.Add(foot);

            // las vías del mapa, en latitud y longitud (para pinchar)
            for (int e = 0; e < _g.Edges.Length; e++)
            {
                var E = _g.Edges[e];
                if (E.Vec < 0 || E.X == null || E.X.Length < 2) continue;
                var pts = new List<(double, double)>();
                for (int i = 0; i < E.X.Length; i++) pts.Add(_g.Unproj(E.X[i], E.Y[i]));
                _lines.Add((e, pts));
            }
            Shown += (s, e) =>
            {
                var path = HorarioSalaForm.PathPoints(_g, _origin, _dest);
                if (path != null && path.Count > 1) _map.FitLL(path); else _map.FitAll();
            };
            Refresh0();
        }

        void Parse(string h)
        {
            if (string.IsNullOrWhiteSpace(h)) return;
            foreach (var part in h.Split(';'))
            {
                var kv = part.Split('='); if (kv.Length != 2) continue;
                if (kv[0] == "o" && int.TryParse(kv[1], out int o)) Origin = o;
                else if (kv[0] == "d" && int.TryParse(kv[1], out int d)) Dest = d;
                else if (kv[0] == "v") foreach (var x in kv[1].Split(',')) if (int.TryParse(x, out int v) && !Vias.Contains(v)) Vias.Add(v);
            }
        }

        static bool Is(string a, string b) => a != null && string.Equals(a.Trim(), b?.Trim(), StringComparison.CurrentCultureIgnoreCase);
        RouteGraph.Edge EdgeOfVec(int vec) => _g.Edges.FirstOrDefault(E => E.Vec == vec);

        // nombre para la lista: la estación (con su vía) si tiene andén; si no, «vía (nodo n)»
        string Describe(int vec)
        {
            var E = EdgeOfVec(vec); if (E == null) return string.Format(Tr("vía (nodo {0})"), vec);
            var raw = E.PlatformNames.Select(x => x.raw).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? E.Platforms.Select(x => x.name).FirstOrDefault();
            return raw != null ? $"{raw} ({vec})" : string.Format(Tr("vía (nodo {0})"), vec);
        }

        // (también lo usan las pruebas) marcar una vía por su tramo del grafo
        public void Pick(int edge)
        {
            var E = _g.Edges[edge]; if (E.Vec < 0) return;
            if (_mode == 0)
            {
                if (!E.Platforms.Any(p => Is(p.name, _origin))) { Warn(string.Format(Tr("Esa vía no tiene andén de {0}: la de salida tiene que ser una vía de la estación de origen."), _origin)); return; }
                Origin = E.Vec; _mode = 1;
            }
            else if (_mode == 1)
            {
                if (!E.Platforms.Any(p => Is(p.name, _dest))) { Warn(string.Format(Tr("Esa vía no tiene andén de {0}: la de llegada tiene que ser una vía de la estación de destino."), _dest)); return; }
                Dest = E.Vec; _mode = 2;
            }
            else
            {
                if (Vias.Contains(E.Vec)) Vias.Remove(E.Vec); else Vias.Add(E.Vec);   // (otro clic en la misma: se quita)
            }
            Refresh0();
        }

        void Warn(string msg) => ThemedBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);

        void Refresh0()
        {
            string[] names = { Tr("① Vía de salida"), Tr("② Vía de llegada"), Tr("③ Añadir una vía de paso") };
            string[] vals = { Origin >= 0 ? Describe(Origin) : Tr("la que esté libre"), Dest >= 0 ? Describe(Dest) : Tr("la que esté libre"), string.Format(Tr("{0} marcadas"), Vias.Count) };
            for (int k = 0; k < 3; k++)
            {
                var b = _modeBtn[k]; var c = (Color)b.Tag;
                b.Text = names[k] + "\n" + vals[k];
                b.Font = Theme.Font(8.75f, FontStyle.Bold);
                b.BackColor = _mode == k ? Color.FromArgb(70, c) : Theme.Surface2;
                b.ForeColor = _mode == k ? Color.White : Theme.Text;
            }
            _vias.BeginUpdate(); _vias.Items.Clear();
            for (int i = 0; i < Vias.Count; i++) _vias.Items.Add($"{i + 1}.  {Describe(Vias[i])}");
            _vias.EndUpdate();
            _map?.Invalidate();
        }

        int EdgeAt(Point p)
        {
            if (_scr == null) return -1;
            double best = Theme.Px(9); int bestE = -1;
            foreach (var (e, pts) in _lines)
            {
                PointF prev = default; bool first = true;
                foreach (var (lat, lon) in pts)
                {
                    var q = _scr(lat, lon);
                    if (!first)
                    {
                        double d = SegDist(p, prev, q);
                        if (d < best) { best = d; bestE = e; }
                    }
                    prev = q; first = false;
                }
            }
            return bestE;
        }

        static double SegDist(Point p, PointF a, PointF b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
            double t = l2 < 1e-6 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2));
            double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
            return Math.Sqrt(x * x + y * y);
        }

        void Draw(Graphics g, Func<double, double, PointF> scr)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            void Line(int vec, Color c, float w, string tag = null)
            {
                var E = EdgeOfVec(vec); if (E == null) return;
                int e = Array.IndexOf(_g.Edges, E);
                var l = _lines.FirstOrDefault(x => x.edge == e); if (l.pts == null) return;
                var pts = l.pts.Select(x => scr(x.lat, x.lon)).ToArray();
                using var pen = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                if (pts.Length > 1) g.DrawLines(pen, pts);
                if (tag != null && pts.Length > 0)
                {
                    var mid = pts[pts.Length / 2];
                    using var f = Theme.Font(8.5f, FontStyle.Bold);
                    var sz = TextRenderer.MeasureText(tag, f);
                    var r = new Rectangle((int)mid.X - sz.Width / 2 - 3, (int)mid.Y - sz.Height - 8, sz.Width + 6, sz.Height + 2);
                    using (var bb = new SolidBrush(Color.FromArgb(230, 24, 28, 34))) g.FillRectangle(bb, r);
                    TextRenderer.DrawText(g, tag, f, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
            if (_hover >= 0 && _hover < _g.Edges.Length) Line(_g.Edges[_hover].Vec, Color.FromArgb(200, 255, 255, 255), 5f);
            for (int i = 0; i < Vias.Count; i++) Line(Vias[i], CVia, 6f, (i + 1).ToString(CultureInfo.InvariantCulture));
            if (Origin >= 0) Line(Origin, COrigin, 7f, Tr("salida"));
            if (Dest >= 0) Line(Dest, CDest, 7f, Tr("llegada"));
        }
    }
}
