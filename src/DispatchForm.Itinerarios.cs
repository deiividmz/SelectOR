// PUESTO DE MANDO: itinerarios (1.2.75). Como en un puesto de ADIF, el jefe no ordena el aspecto de una señal: establece
// un itinerario de una señal a la siguiente (o a una topera) y la señal se abre sola, con el aspecto que le toque, cuando
// llega el tren. Clic en una señal tuya → «Establecer itinerario desde aquí…»: se dibujan en discontinuo los caminos
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
                         : null;
                if (t != null) try { if (!IsDisposed) BeginInvoke(new Action(() => ItinSay(t, false))); } catch { }
            };
            _pilot.ItinReply += (who, ok, o, text) =>
            {
                if (Me == null || who != Me) return;
                try { if (!IsDisposed) BeginInvoke(new Action(() => ItinSay(ItinText(text), ok))); } catch { }
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

        // (MouseUp) eligiendo destino: un clic en un círculo lo establece; en otro sitio, no se hace nada
        bool PickClick(Point p)
        {
            if (_pick is not { } pk) return false;
            _pick = null;
            var o = PickAt(pk, p);
            if (o != null)
            {
                _pilot.ItinSet(pk.origin.Obj, o.Dest, o.Switches);
                ItinSay(string.Format(Tr("Estableciendo el itinerario {0} → {1}…"), pk.origin.Obj, o.Dest >= 0 ? o.Dest.ToString() : Tr("topera")), true);
            }
            _canvas.Invalidate();
            return true;
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

        SchematicCircuits.ItinOption PickAt((RoomMapGeo.Sig origin, List<SchematicCircuits.ItinOption> opts) pk, Point p)
        {
            SchematicCircuits.ItinOption best = null; float bd = Theme.Px(16) * Theme.Px(16);
            foreach (var o in pk.opts)
            {
                if (!ScreenOff(o.EndEdge, o.EndOff, out var s)) continue;
                float d = (s.X - p.X) * (s.X - p.X) + (s.Y - p.Y) * (s.Y - p.Y);
                if (d < bd) { bd = d; best = o; }
            }
            return best;
        }

        bool PickCancel() { if (_pick == null) return false; _pick = null; _canvas.Invalidate(); return true; }

        // (Paint_) los caminos posibles mientras se elige, y el mensaje de la sala
        void DrawPick(Graphics g, Rectangle area)
        {
            if (_pick is { } pk && (Board || _cur != null))
            {
                var mouse = _canvas.PointToClient(Cursor.Position);
                var hov = PickAt(pk, mouse);
                foreach (var o in pk.opts.OrderBy(o => o == hov))
                {
                    bool h = o == hov;
                    using var pen = new Pen(h ? SchematicDraw.RouteColor : Color.FromArgb(200, 236, 238, 240), h ? 4f : 2.4f) { DashStyle = h ? DashStyle.Solid : DashStyle.Dash, LineJoin = LineJoin.Round };
                    foreach (var (e, a, b) in o.Pieces)
                    {
                        var pc = ScreenPiece(e, a, b);
                        if (pc != null && pc.Length >= 2) g.DrawLines(pen, pc);
                    }
                    if (!ScreenOff(o.EndEdge, o.EndOff, out var s)) continue;
                    float r = Theme.Px(h ? 10 : 8);
                    using (var fb = new SolidBrush(Color.FromArgb(h ? 230 : 160, 20, 60, 30))) g.FillEllipse(fb, s.X - r, s.Y - r, 2 * r, 2 * r);
                    using (var rp = new Pen(h ? SchematicDraw.RouteColor : Color.White, 2f)) g.DrawEllipse(rp, s.X - r, s.Y - r, 2 * r, 2 * r);
                    string lab = o.Dest >= 0 ? o.Dest.ToString() : Tr("topera");
                    using var f = Theme.Font(8f, FontStyle.Bold);
                    TextRenderer.DrawText(g, lab, f, new Point((int)(s.X + r + 3), (int)(s.Y - r - 2)), h ? SchematicDraw.RouteColor : Color.White, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                }
                Banner(g, area, string.Format(Tr("Itinerario desde la señal {0}: clic en el destino (un círculo) · Esc: cancelar"), pk.origin.Obj), Color.FromArgb(230, 20, 60, 30), Color.FromArgb(200, 240, 200));
                return;
            }
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
