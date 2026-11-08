// PUESTO DE MANDO: itinerarios (1.2.75). Como en un puesto de ADIF, el jefe no ordena el aspecto de una señal: establece
// un itinerario de una señal a la siguiente (o a una topera) y la señal se abre sola, con el aspecto que le toque, cuando
// llega el tren. (2026-11-08, más rápido) Clic en una señal tuya sin itinerario: a elegir el destino al momento (antes,
// por el menú); doble clic, si tiene un solo destino, establecido; botón derecho, el menú entero; abajo, «Repetir» con los
// últimos; el pedido se ve ya en discontinuo. Antes: clic en una señal tuya → «Establecer itinerario desde aquí…»: se dibujan en discontinuo los caminos
// posibles con un círculo en cada destino (SchematicCircuits.Options); clic en un círculo y la sala (Itinerarios.cs del
// despachador) pone las agujas, las enclava y lo guarda. Esc o clic fuera: nada. «Anular itinerario»: sin tren
// acercándose, al momento; con tren, la señal pasa a parada y las agujas se sueltan a los 2 min.
// En el esquema: los itinerarios establecidos, en verde (anulándose, en ámbar), y sus agujas, enclavadas (azul).
// Arriba, en tus dependencias: «Mando automático» / «Mando manual» (en manual, sin itinerario no se abre ninguna señal).

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
        Label _manualBtn;
        Label _viewBtn;   // (1.2.81) «Esquema» / «Trazado real» en una dependencia
        (RoomMapGeo.Sig origin, List<SchematicCircuits.ItinOption> opts)? _pick;   // (1.2.76: la señal de la ruta, en el tablero o en un esquema)
        string _itinMsg; bool _itinMsgOk; DateTime _itinMsgUntil;

        // (constructor, tras BuildJefeBar) el botón del mando y las respuestas de la sala
        void BuildItinBar(Panel top)
        {
            _manualBtn = new Label
            {
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Font(9f, FontStyle.Bold), Cursor = Cursors.Hand,
                Dock = DockStyle.Right, Visible = false, Width = Theme.Px(150), Margin = Padding.Empty, ForeColor = Theme.Text, BackColor = Theme.Surface2
            };
            _manualBtn.Click += (s, e) =>
            {
                var h = _sel; if (h == null || !HeaderMine(h)) return;
                bool on = !HeaderManual(h);
                foreach (var z in h.Zones) _pilot.JefeManual(z, on);
            };
            new ToolTip().SetToolTip(_manualBtn, Tr("Mando manual: en esta dependencia las señales no se abren sin un itinerario. Automático: se abren solas al llegar el tren (también puedes establecer itinerarios)."));
            var gap = new Panel { Dock = DockStyle.Right, Width = Theme.Px(6), BackColor = top.BackColor };
            top.Controls.Add(_manualBtn); top.Controls.Add(gap);
            // (1.2.81) la vista de la dependencia: esquema recto o trazado real
            _viewBtn = new Label
            {
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Font(9f, FontStyle.Bold), Cursor = Cursors.Hand,
                Dock = DockStyle.Right, Visible = false, Width = Theme.Px(170), Margin = Padding.Empty, ForeColor = Theme.Text, BackColor = Theme.Surface2
            };
            _viewBtn.Click += (s, e) =>
            {
                if (_curZone == null) return;
                _schema = !_schema;
                string z = _curZone;
                ShowZone(z);
                UpdateViewBtn();
                _canvas.Invalidate();
            };
            new ToolTip().SetToolTip(_viewBtn, Tr("Cambia la vista de la dependencia: el esquema recto (vías en filas, agujas a 45°, todo separado) o el trazado real de las vías."));
            var gap2 = new Panel { Dock = DockStyle.Right, Width = Theme.Px(6), BackColor = top.BackColor };
            top.Controls.Add(_viewBtn); top.Controls.Add(gap2);
        }

        void UpdateViewBtn()
        {
            if (_viewBtn == null) return;
            _viewBtn.Visible = _curZone != null && _board != null && !_noSchema.Contains(_curZone);
            _viewBtn.Text = _schema ? Tr("Ver: trazado real") : Tr("Ver: esquema");
        }

        void PrepareItins()
        {
            _pilot.ItinsChanged += () => { try { if (!IsDisposed) BeginInvoke(new Action(() => _canvas.Invalidate())); } catch { } };
            _pilot.WhyChanged += () => { try { if (!IsDisposed) BeginInvoke(new Action(() => _canvas.Invalidate())); } catch { } };   // (1.2.77)
            // lo que la sala no deja (agujas y señales del puesto)
            _pilot.MapError += e =>
            {
                string t = e.StartsWith("jefe ") ? string.Format(Tr("Eso es de la dependencia de {0}."), e.Substring(5).Trim())
                         : e.StartsWith("busy ") ? Tr("No se puede mover la aguja: ") + e.Substring(5)
                         : e == "busy" ? Tr("Open Rails no deja cambiarla: hay un tren encima o en la ruta que tiene reservada.")
                         : e == "nobot" || e == "nodisp" ? Tr("La sala no tiene ahora mismo el despachador fijo de SelectOR.")
                         : e == "notmine" ? Tr("Fuera de tus dependencias, las agujas solo las mueve quien está conduciendo.")
                         : e == "notin" ? Tr("Para eso tienes que estar conduciendo en la sala o tener el mando de esta dependencia.")   // (1.2.81)
                         : null;
                if (t != null) try { if (!IsDisposed) BeginInvoke(new Action(() => ItinSay(t, false))); } catch { }
            };
            _pilot.ItinReply += (who, ok, o, text) =>
            {
                if (Me == null || who != Me) return;
                try { if (!IsDisposed) BeginInvoke(new Action(() => { if (_pending is { } pd && pd.origin == o) _pending = null; ItinSay(ItinText(text), ok); })); } catch { }
            };
        }

        // la parte en español o en inglés del texto de la sala («es · en»)
        static string ItinText(string t)
        {
            int k = t.IndexOf(" · ", StringComparison.Ordinal);
            if (k < 0) return t;
            return I18n.English ? t.Substring(k + 3) : t.Substring(0, k);
        }

        void ItinSay(string text, bool ok) { _itinMsg = text; _itinMsgOk = ok; _itinMsgUntil = DateTime.UtcNow.AddSeconds(ok ? 5 : 9); _canvas.Invalidate(); }

        bool HeaderManual(SchematicHeader h) => h != null && h.Zones.Count > 0 && h.Zones.All(z => _pilot?.JefeZones.FirstOrDefault(q => q.Zone == z)?.Manual == true);

        void UpdateManualBtn()
        {
            UpdateViewBtn();
            if (_manualBtn == null) return;
            var h = _sel;
            bool mine = HeaderMine(h) && (Board || !Overview);
            _manualBtn.Visible = mine;
            if (!mine) return;
            bool man = HeaderManual(h);
            _manualBtn.Text = man ? Tr("Mando manual") : Tr("Mando automático");
            _manualBtn.BackColor = man ? Color.FromArgb(120, 82, 20) : Theme.Surface2;
        }

        // ── el menú de una señal tuya: itinerarios y órdenes
        void SignalMenu(RoomMapGeo.Sig sg, Point p)
        {
            var m = MenuStyle.Apply(new ContextMenuStrip());
            m.Items.Add(new ToolStripMenuItem(string.Format(Tr("Señal {0}"), sg.Obj)) { Enabled = false });
            if (_pilot.SigWhy.TryGetValue(sg.Obj, out var why))   // (1.2.77) por qué está en parada
                m.Items.Add(new ToolStripMenuItem(string.Format(Tr("En parada para «{0}»: {1}"), why.train, SwitchPilot.Lang(why.text))) { ForeColor = RoomMapDraw.WhyColor });
            var it = _pilot.ItinFrom(sg.Obj);
            if (it == null)
                m.Items.Add(new ToolStripMenuItem(Tr("Establecer itinerario desde aquí…"), null, (o, e) => StartPick(sg)) { ForeColor = SchematicDraw.RouteColor });
            else if (it.State == "queue")
            {
                // (en cola: el primero, o quitarlo)
                m.Items.Add(new ToolStripMenuItem(string.Format(Tr("Itinerario {0}: en cola ({1}.º)"), ItinName(it), it.Queue)) { Enabled = false });
                if (!string.IsNullOrEmpty(it.Why)) m.Items.Add(new ToolStripMenuItem(string.Format(Tr("Espera porque {0}"), SwitchPilot.Lang(it.Why))) { ForeColor = RoomMapDraw.WhyColor });   // (1.2.77)
                if (it.Queue > 1) m.Items.Add(new ToolStripMenuItem(Tr("Pasarlo el primero de la cola"), null, (o, e) => _pilot.ItinFirst(it.Origin)) { ForeColor = SchematicDraw.QueueColor });
                m.Items.Add(new ToolStripMenuItem(Tr("Quitarlo de la cola"), null, (o, e) => _pilot.ItinDel(it.Origin)) { ForeColor = Color.FromArgb(255, 183, 77) });
            }
            else
                m.Items.Add(new ToolStripMenuItem(string.Format(Tr("Anular itinerario {0}"), ItinName(it)), null, (o, e) => { _pilot.ItinDel(it.Origin); }) { ForeColor = Color.FromArgb(255, 183, 77) });
            m.Items.Add(new ToolStripSeparator());
            void Item(string text, int pick, Color col)
            {
                var mi = new ToolStripMenuItem(text, null, (o, e) => { _pilot.JefeSignal(sg.Obj, pick); _flash[sg.Obj] = DateTime.Now; _timer.Interval = 250; });
                if (col != Color.Empty) mi.ForeColor = col;
                m.Items.Add(mi);
            }
            Item(Tr("Cerrar la señal (parada)"), 1, RoomMapDraw.AspectColor(0));
            Item(Tr("Rebase autorizado"), 4, RoomMapDraw.CallOnWhite);
            Item(Tr("Quitar la parada (automática)"), 0, Color.Empty);
            m.Show(_canvas, p);
        }

        static string ItinName(SwitchPilot.Itin it) => it.Origin + " → " + (it.Dest >= 0 ? it.Dest.ToString() : Tr("topera"));

        void StartPick(RoomMapGeo.Sig sg)
        {
            if (_g == null || _sbe == null || !SchematicCircuits.SignalPlace(_sbe, sg, out int e, out double off, out int dir))
            { ItinSay(Tr("No se sabe por dónde sigue la vía desde esa señal."), false); return; }
            var opts = SchematicCircuits.Options(_g, _sbe, e, off, dir);
            if (opts.Count == 0) { ItinSay(Tr("Desde esa señal no hay ningún destino a menos de 3 km."), false); return; }
            _pick = (sg, opts);
            _canvas.Invalidate();
        }

        // (2026-11-08, más rápido) clic en una señal tuya: sin itinerario, a elegir el destino al momento (antes había que pasar
        // por el menú); con uno (establecido o en cola), su menú. El menú entero, con el botón derecho (SignalRightClick)
        void SignalLeft(RoomMapGeo.Sig sg, Point p)
        {
            if (_pilot.ItinFrom(sg.Obj) != null) { SignalMenu(sg, p); return; }
            StartPick(sg);
        }

        // (2026-11-08) botón derecho: el menú de la señal (órdenes, itinerario…)
        bool SignalRightClick(Point p)
        {
            var sg = MySignalAt(p);
            if (sg == null) return false;
            PickCancel();
            SignalMenu(sg, p);
            return true;
        }

        // una señal tuya en ese punto (en el tablero o en el esquema que se ve), o null
        RoomMapGeo.Sig MySignalAt(Point p)
        {
            if (Me == null) return null;
            float R = Theme.Px(11);
            static float D(PointF a, Point b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
            if (Board)
            {
                if (_board == null) return null;
                var bs = RouteBoardDraw.SignalAt(_board, p, Theme.Px(4)) is RouteBoard.Sg hit && hit.S.Obj >= 0 && MySig(hit.S.Obj) ? hit
                    : _board.Signals.Where(q => q.S.Obj >= 0 && MySig(q.S.Obj)).Select(q => (q, d: D(Bp(q.At), p))).Where(x => x.d < R).OrderBy(x => x.d).Select(x => x.q).FirstOrDefault();
                return bs?.S;
            }
            if (_cur == null || Overview) return null;
            PointF Sc(PointF a) => new PointF(_origin.X + a.X * _zoom, _origin.Y + a.Y * _zoom);
            var zs = SchematicDraw.SignalAt(_cur, p, Theme.Px(4)) is Schematic.Signal h2 && h2.Sig != null && h2.Sig.Obj >= 0 && MySig(h2.Sig.Obj) ? h2
                : _cur.Signals.Where(q => q.Sig != null && q.Sig.Obj >= 0 && MySig(q.Sig.Obj)).Select(q => (q, d: D(Sc(q.At), p))).Where(x => x.d < R).OrderBy(x => x.d).Select(x => x.q).FirstOrDefault();
            return zs?.Sig;
        }

        // los destinos distintos (cada destino una vez: el camino más corto)
        static List<SchematicCircuits.ItinOption> Dests(List<SchematicCircuits.ItinOption> opts)
            => opts.GroupBy(o => o.Dest >= 0 ? "s" + o.Dest : "e" + o.EndEdge).Select(gr => gr.OrderBy(x => x.Len).First()).ToList();

        // (MouseUp) eligiendo destino: un clic en un círculo lo establece; (2026-11-08) otra vez en la señal de origen (o doble
        // clic en ella), si solo hay un destino, ese; en otra señal tuya, a elegir desde ella; en otro sitio, nada
        bool PickClick(Point p)
        {
            if (_pick is not { } pk) return false;
            var o = PickAt(pk, p);
            if (o == null && MySignalAt(p) is RoomMapGeo.Sig at)
            {
                if (at.Obj != pk.origin.Obj) { StartPick(at); return true; }
                var ds = Dests(pk.opts);
                if (ds.Count != 1) { ItinSay(string.Format(Tr("Desde la señal {0} hay {1} destinos: elige uno (los círculos)."), at.Obj, ds.Count), false); return true; }
                o = ds[0];
            }
            // (1.2.81) a ese destino se llega por más de un camino: a elegir (al pasar por cada uno, se ve en el mapa)
            var vars = o == null ? null : pk.opts.Where(x => x.Dest == o.Dest && (o.Dest >= 0 || x.EndEdge == o.EndEdge)).OrderBy(x => x.Len).ToList();
            if (vars != null && vars.Count > 1)
            {
                var m = MenuStyle.Apply(new ContextMenuStrip());
                m.Items.Add(new ToolStripMenuItem(string.Format(Tr("A {0} se llega por {1} caminos:"), o.Dest >= 0 ? o.Dest.ToString() : Tr("topera"), vars.Count)) { Enabled = false });
                bool chosen = false;
                for (int i = 0; i < vars.Count; i++)
                {
                    var v = vars[i];
                    var mi = new ToolStripMenuItem(VariantText(v, i, vars), null, (s2, e2) => { chosen = true; _pickVariant = null; _pick = null; SetItin(pk.origin, v); }) { ForeColor = SchematicDraw.RouteColor };
                    mi.MouseEnter += (s2, e2) => { _pickVariant = v; _canvas.Invalidate(); };
                    m.Items.Add(mi);
                }
                m.Closed += (s2, e2) => { if (!chosen) { _pickVariant = null; _canvas.Invalidate(); } };
                m.Show(_canvas, p);
                return true;
            }
            _pick = null;
            if (o != null) SetItin(pk.origin, o);
            _canvas.Invalidate();
            return true;
        }

        SchematicCircuits.ItinOption _pickVariant;   // (1.2.81) el camino que se está mirando en el menú de caminos

        void SetItin(RoomMapGeo.Sig origin, SchematicCircuits.ItinOption o)
        {
            if (_pilot.ItinFrom(origin.Obj) is SwitchPilot.Itin had)
            { ItinSay(string.Format(Tr("Ya hay un itinerario desde la señal {0} ({1})."), origin.Obj, ItinName(had)), false); return; }
            _pilot.ItinSet(origin.Obj, o.Dest, o.Switches);
            ItinSay(string.Format(Tr("Estableciendo el itinerario {0} → {1}…"), origin.Obj, o.Dest >= 0 ? o.Dest.ToString() : Tr("topera")), true);
            // (2026-11-08) a la vista ya, en discontinuo, hasta que la sala conteste; y a «Repetir»
            _pending = (origin.Obj, o, DateTime.UtcNow.AddSeconds(10));
            _recent.RemoveAll(r => r.origin.Obj == origin.Obj && r.o.Dest == o.Dest && SameSw(r.o, o));
            _recent.Insert(0, (origin, o));
            if (_recent.Count > 12) _recent.RemoveAt(_recent.Count - 1);
            _canvas.Invalidate();
        }

        static bool SameSw(SchematicCircuits.ItinOption a, SchematicCircuits.ItinOption b)
            => a.Switches.Count == b.Switches.Count && a.Switches.All(w => b.Switches.Contains(w));

        // (2026-11-08) el itinerario pedido, dibujado ya (hasta que la sala lo tenga o conteste) · los últimos, para repetirlos
        (int origin, SchematicCircuits.ItinOption o, DateTime until)? _pending;
        readonly List<(RoomMapGeo.Sig origin, SchematicCircuits.ItinOption o)> _recent = new();
        readonly List<(Rectangle r, RoomMapGeo.Sig origin, SchematicCircuits.ItinOption o)> _recentHit = new();

        // los de «Repetir» que se pueden pedir desde esta vista (su señal se ve aquí y es tuya), como mucho 5
        List<(RoomMapGeo.Sig origin, SchematicCircuits.ItinOption o)> RecentHere()
        {
            if (Overview && !Board) return new();
            bool Here(int obj) => Board ? _board != null && _board.Signals.Any(q => q.S.Obj == obj) : _cur != null && _cur.Signals.Any(q => q.Sig != null && q.Sig.Obj == obj);
            return _recent.Where(r => MySig(r.origin.Obj) && Here(r.origin.Obj)).Take(5).ToList();
        }

        // (MouseUp) un botón de «Repetir»
        bool RecentClick(Point p)
        {
            if (_pick != null) return false;
            foreach (var h in _recentHit)
                if (h.r.Contains(p)) { SetItin(h.origin, h.o); return true; }
            return false;
        }

        void DrawRecent(Graphics g, Rectangle area)
        {
            _recentHit.Clear();
            var list = RecentHere();
            if (list.Count == 0) return;
            using var f = Theme.Font(8.5f, FontStyle.Bold);
            int pad = Theme.Px(8), h = Theme.Px(26), x = area.Left + Theme.Px(12), y = area.Bottom - h - Theme.Px(12);
            string cap = Tr("Repetir:");
            var cs = TextRenderer.MeasureText(cap, f);
            TextRenderer.DrawText(g, cap, f, new Rectangle(x, y, cs.Width + 2, h), Color.FromArgb(200, 210, 216), TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            x += cs.Width + Theme.Px(6);
            var mouse = _canvas.PointToClient(Cursor.Position);
            foreach (var (origin, o) in list)
            {
                string t = origin.Obj + " → " + (o.Dest >= 0 ? o.Dest.ToString() : Tr("topera"));
                var ts = TextRenderer.MeasureText(t, f);
                var r = new Rectangle(x, y, ts.Width + 2 * pad, h);
                if (r.Right > area.Right - Theme.Px(12)) break;
                bool busy = _pilot.ItinFrom(origin.Obj) != null, hov = r.Contains(mouse);
                using (var b = new SolidBrush(busy ? Color.FromArgb(220, 45, 48, 51) : hov ? Color.FromArgb(240, 30, 90, 45) : Color.FromArgb(230, 20, 60, 30))) g.FillRectangle(b, r);
                using (var pn = new Pen(busy ? Color.FromArgb(90, 96, 102) : SchematicDraw.RouteColor, 1.5f)) g.DrawRectangle(pn, r);
                TextRenderer.DrawText(g, t, f, r, busy ? Color.FromArgb(150, 156, 160) : Color.FromArgb(236, 240, 244), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                if (!busy) _recentHit.Add((r, origin, o));
                x = r.Right + Theme.Px(6);
            }
        }

        void DrawPending(Graphics g)
        {
            if (_pending is not { } pd) return;
            bool known = _pilot.ItinFrom(pd.origin) != null;
            if (known || DateTime.UtcNow > pd.until) { _pending = null; return; }
            using var pen = new Pen(SchematicDraw.RouteColor, 3.2f) { DashStyle = DashStyle.Dash, LineJoin = LineJoin.Round };
            foreach (var (e, a, b) in pd.o.Pieces)
            {
                var pc = ScreenPiece(e, a, b);
                if (pc != null && pc.Length >= 2) g.DrawLines(pen, pc);
            }
        }

        // «El más corto (420 m)» / «Por la otra rama de la aguja 7 (480 m)»: lo que cambia frente al más corto
        string VariantText(SchematicCircuits.ItinOption v, int i, List<SchematicCircuits.ItinOption> all)
        {
            string len = string.Format("{0:0} m", v.Len);
            if (i == 0) return string.Format(Tr("El más corto ({0})"), len);
            var first = all[0].Switches.ToDictionary(w => w.node, w => w.pos);
            var diff = v.Switches.Where(w => !first.TryGetValue(w.node, out int pp) || pp != w.pos)
                                 .Select(w => { int n = SwNumber(w.node); return n > 0 ? n.ToString() : "#" + w.node; }).Distinct().ToList();
            return diff.Count == 0 ? string.Format(Tr("Otro camino ({0})"), len)
                 : string.Format(diff.Count == 1 ? Tr("Por la otra rama de la aguja {0} ({1})") : Tr("Por la otra rama de las agujas {0} ({1})"), string.Join(", ", diff), len);
        }

        int SwNumber(int node)
        {
            if (Board) return _board?.Switches.FirstOrDefault(s => s.Node == node)?.Number ?? 0;
            return _cur != null ? SchematicDraw.SwitchNumber(_cur, node) : 0;
        }

        // (1.2.76) un punto de un tramo y un trozo de vía, en pantalla: en el tablero o en el esquema que se ve
        bool ScreenOff(int e, double off, out PointF s)
        {
            if (Board) { s = Bp(_board.At(e, off)); return true; }
            s = default;
            if (_cur == null || !_cur.MapOff(e, off, out var q)) return false;
            s = new PointF(_origin.X + q.X * _zoom, _origin.Y + q.Y * _zoom); return true;
        }
        PointF[] ScreenPiece(int e, double a, double b)
        {
            var pc = Board ? _board.Piece(e, a, b) : _cur?.Piece(e, a, b);
            if (pc == null) return null;
            return Board ? pc.Select(Bp).ToArray() : pc.Select(q => new PointF(_origin.X + q.X * _zoom, _origin.Y + q.Y * _zoom)).ToArray();
        }

        // (1.2.82) dónde se pulsa un destino: en su señal; si esa señal no está en este esquema (es de la estación siguiente o
        // del trayecto), donde su camino sale del esquema (away: el rótulo lleva «→» y la dependencia de la señal). Antes no
        // salía ningún círculo y no se podía establecer el itinerario de salida hacia la otra estación.
        bool EndScreen(SchematicCircuits.ItinOption o, out PointF s, out bool away)
        {
            away = false;
            if (ScreenOff(o.EndEdge, o.EndOff, out s)) return true;
            if (Board || _cur == null || _g == null) return false;
            for (int i = o.Pieces.Count - 1; i >= 0; i--)
            {
                var (e, a, b) = o.Pieces[i];
                if (!_cur.DrawnRange(e, out double da, out double db)) continue;
                double lo = Math.Max(a, da), hi = Math.Min(b, db);
                if (hi - lo < 0.5) { double m = Math.Max(da, Math.Min(db, (a + b) / 2)); lo = Math.Max(da, m - 0.5); hi = Math.Min(db, m + 0.5); }   // (el camino sale del esquema justo ahí: su borde)
                // hacia dónde sigue el camino en ese trozo: al final del itinerario, o al nodo que comparte con el siguiente
                double far;
                if (i == o.Pieces.Count - 1) far = o.EndOff;
                else
                {
                    var E = _g.Edges[e]; var N = _g.Edges[o.Pieces[i + 1].e];
                    int n = E.A == N.A || E.A == N.B ? E.A : E.B;
                    far = E.A == n ? 0 : E.Len;
                }
                double off = Math.Abs(far - lo) < Math.Abs(far - hi) ? lo : hi;
                double back = off == lo ? Math.Min(hi, lo + 1) : Math.Max(lo, hi - 1);   // (un metro hacia dentro: el borde exacto puede no tener sitio)
                if (ScreenOff(e, off, out s) || ScreenOff(e, back, out s)) { away = true; return true; }
                var pc = ScreenPiece(e, lo, hi);
                if (pc != null && pc.Length > 0) { s = off == lo ? pc[0] : pc[^1]; away = true; return true; }
            }
            return false;
        }

        // (1.2.82) el sitio de cada destino; los que caen en el mismo punto (varios fuera del esquema por la misma salida), uno
        // debajo de otro
        Dictionary<SchematicCircuits.ItinOption, (PointF at, bool away)> EndPlaces(List<SchematicCircuits.ItinOption> opts)
        {
            var d = new Dictionary<SchematicCircuits.ItinOption, (PointF, bool)>();
            var used = new List<PointF>(); float step = Theme.Px(22);
            foreach (var o in opts)
            {
                if (!EndScreen(o, out var at, out bool away)) continue;
                for (int k = 0; k < 8 && used.Any(u => Math.Abs(u.X - at.X) < step * 0.8f && Math.Abs(u.Y - at.Y) < step * 0.8f); k++) at = new PointF(at.X, at.Y + step);
                used.Add(at); d[o] = (at, away);
            }
            return d;
        }

        SchematicCircuits.ItinOption PickAt((RoomMapGeo.Sig origin, List<SchematicCircuits.ItinOption> opts) pk, Point p)
        {
            SchematicCircuits.ItinOption best = null; float bd = Theme.Px(16) * Theme.Px(16);
            var places = EndPlaces(pk.opts);
            foreach (var o in pk.opts)
            {
                if (!places.TryGetValue(o, out var pl)) continue; var s = pl.at;
                float d = (s.X - p.X) * (s.X - p.X) + (s.Y - p.Y) * (s.Y - p.Y);
                if (d < bd) { bd = d; best = o; }
            }
            return best;
        }

        bool PickCancel() { if (_pick == null) return false; _pick = null; _pickVariant = null; _canvas.Invalidate(); return true; }

        // (Paint_) los caminos posibles mientras se elige, y el mensaje de la sala
        void DrawPick(Graphics g, Rectangle area)
        {
            if (_pick is { } pk && (Board || _cur != null))
            {
                var mouse = _canvas.PointToClient(Cursor.Position);
                var hov = _pickVariant ?? PickAt(pk, mouse);
                var places = EndPlaces(pk.opts);
                foreach (var o in pk.opts.OrderBy(o => o == hov))
                {
                    bool h = o == hov;
                    using var pen = new Pen(h ? SchematicDraw.RouteColor : Color.FromArgb(200, 236, 238, 240), h ? 4f : 2.4f) { DashStyle = h ? DashStyle.Solid : DashStyle.Dash, LineJoin = LineJoin.Round };
                    foreach (var (e, a, b) in o.Pieces)
                    {
                        var pc = ScreenPiece(e, a, b);
                        if (pc != null && pc.Length >= 2) g.DrawLines(pen, pc);
                    }
                    if (!places.TryGetValue(o, out var pl)) continue;
                    var s = pl.at; bool away = pl.away;
                    float r = Theme.Px(h ? 10 : 8);
                    using (var fb = new SolidBrush(Color.FromArgb(h ? 230 : 160, 20, 60, 30))) g.FillEllipse(fb, s.X - r, s.Y - r, 2 * r, 2 * r);
                    using (var rp = new Pen(h ? SchematicDraw.RouteColor : Color.White, 2f)) g.DrawEllipse(rp, s.X - r, s.Y - r, 2 * r, 2 * r);
                    string lab = o.Dest >= 0 ? o.Dest.ToString() : Tr("topera");
                    if (away) lab = "→ " + lab + (o.Dest >= 0 && _sigZone != null && _sigZone.TryGetValue(o.Dest, out var zn) ? " (" + zn + ")" : "");   // (1.2.82: en otra dependencia)
                    using var f = Theme.Font(8f, FontStyle.Bold);
                    TextRenderer.DrawText(g, lab, f, new Point((int)(s.X + r + 3), (int)(s.Y - r - 2)), h ? SchematicDraw.RouteColor : Color.White, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                }
                Banner(g, area, Dests(pk.opts).Count == 1
                    ? string.Format(Tr("Itinerario desde la señal {0}: clic en el destino o doble clic en la señal · Esc: cancelar"), pk.origin.Obj)
                    : string.Format(Tr("Itinerario desde la señal {0}: clic en el destino (un círculo) · Esc: cancelar"), pk.origin.Obj), Color.FromArgb(230, 20, 60, 30), Color.FromArgb(200, 240, 200));
                return;
            }
            DrawPending(g);
            DrawRecent(g, area);
            if (_itinMsg != null && DateTime.UtcNow < _itinMsgUntil)
                Banner(g, area, _itinMsg, _itinMsgOk ? Color.FromArgb(230, 20, 60, 30) : Color.FromArgb(235, 90, 30, 30), Color.FromArgb(236, 240, 244));
        }

        void Banner(Graphics g, Rectangle area, string t, Color bg, Color fg)
        {
            using var f = Theme.Font(9f, FontStyle.Bold);
            var sz = TextRenderer.MeasureText(t, f, new Size(area.Width - Theme.Px(80), 0), TextFormatFlags.WordBreak);
            var r = new Rectangle(area.Left + (area.Width - sz.Width) / 2 - Theme.Px(12), area.Top + Theme.Px(12), sz.Width + Theme.Px(24), sz.Height + Theme.Px(12));
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
            TextRenderer.DrawText(g, t, f, r, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }

        // (1.2.81) ¿esta señal tiene un itinerario establecido (no en cola)? Entonces su camino es el del itinerario
        bool HasItin(int obj) => _pilot != null && _pilot.Itins.Any(i => i.Origin == obj && i.State != "queue");

        // (Live) los itinerarios de la sala en el esquema: su camino (verde; anulándose, ámbar) y sus agujas, enclavadas
        void AddItins(SchematicLive live, Func<int, int> posNode, HashSet<int> lockedOrders)
        {
            if (_pilot == null || _geo == null || _g == null || _sbe == null) return;
            foreach (var it in _pilot.Itins)
            {
                var sg = _geo.Signals.FirstOrDefault(s => s.Obj == it.Origin);
                if (sg == null || !SchematicCircuits.SignalPlace(_sbe, sg, out int e, out double off, out int dir)) continue;
                var pos = it.Switches.ToDictionary(x => x.node, x => x.pos);
                if (it.State != "queue") foreach (var (n, _) in it.Switches) if (_geo.OrderOf.TryGetValue(n, out int o)) lockedOrders.Add(o);   // (en cola: aún no enclava)
                // (1.2.81) agujas que no están como dice el itinerario (las ha movido otro): en rojo
                if (it.State == "set" || it.State == "in")
                    foreach (var (n, pp) in it.Switches)
                    {
                        int real = posNode(n);
                        if (real >= 0 && real != pp && _geo.OrderOf.TryGetValue(n, out int o2)) live.Discord.Add(o2);
                    }
                bool cancel = it.State == "cancel", queue = it.State == "queue";
                SchematicCircuits.Walk(_g, _sbe, e, off, dir, n => pos.TryGetValue(n, out int pp) ? pp : posNode(n), (ed, a, b) =>
                {
                    var d = cancel ? live.Cancelling : queue ? live.Queued : null;
                    if (d != null) { if (!d.TryGetValue(ed, out var l)) d[ed] = l = new(); l.Add((a, b)); }
                    else { if (!live.Route.TryGetValue(ed, out var rl)) live.Route[ed] = rl = new(); rl.Add((a, b, false)); }
                });
            }
        }
    }
}
