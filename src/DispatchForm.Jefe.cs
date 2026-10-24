// PUESTO DE MANDO: jefes de circulación (1.2.75). Varios a la vez, cada uno con sus dependencias (cada estación o trayecto,
// un jefe como mucho). Se abre una dependencia (en el plano general o en la lista); arriba, quién manda en ella y, a los
// autorizados por el administrador, «Tomar esta dependencia» / «Dejarla»; en la lista y en el plano, quién manda en cada
// una (lo tuyo, en verde). En tus dependencias:
//  · clic en una aguja: se cambia (en una travesía, un menú con sus agujas);
//  · clic en una señal: establecer o anular un itinerario desde ella, cerrarla (parada) o rebase autorizado
//    (DispatchForm.Itinerarios.cs);
//  · los Tab de los maquinistas ante tus señales llegan como avisos con «Dar paso» y «No».
// Allí los maquinistas no pueden mover agujas ni dar órdenes a las señales (lo corta el servidor de la sala: SelectOR
// Servidor MP\Jefe.cs); donde no hay jefe, todo como siempre. Tus estaciones se sueltan con «Dejar…», al cerrar esta
// ventana o, si se corta la conexión, a los 2 min.
// Qué señales y agujas son de cada estación: cada una, de una sola (la primera en la que sale, las estaciones antes que
// los tramos de línea: DispatchForm.Avisos.cs, IndexSignals). El administrador, además, apunta y quita a los jefes (menú
// «Jefes») y puede quitarle a un jefe una estación o todas. El vale para el servidor lo da Supabase
// (sql/jefe-circulacion.sql).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class DispatchForm
    {
        Label _jefeLbl, _jefeBtn, _jefesBtn;
        bool _chiefAllowed, _chiefAdmin, _taking;
        string _ticket;
        readonly List<(string zone, bool force)> _toAdd = new();   // estaciones pedidas antes de entrar como jefe

        string Me => _pilot?.JefeMe;
        string OwnerOf(string zone) => zone == null ? null : _pilot?.JefeOfZone(zone);
        bool Mine(string zone) => Me != null && OwnerOf(zone) == Me;
        bool MySig(int sig) => Me != null && _pilot.JefeOfSig(sig) == Me;
        bool MyNode(int node) => Me != null && _pilot.JefeOfNode(node) == Me;
        bool IHaveAny => Me != null && _pilot.JefeZones.Any(z => z.Name == Me);

        // (constructor) los controles de arriba
        void BuildJefeBar(Panel top)
        {
            Label Btn(string text) => new Label
            {
                Text = text, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Font(9f, FontStyle.Bold),
                ForeColor = Color.White, BackColor = Theme.Accent, Cursor = Cursors.Hand, Dock = DockStyle.Right, Visible = false,
                Width = Theme.Px(170), Margin = Padding.Empty
            };
            _jefeBtn = Btn(Tr("Tomar esta dependencia"));
            _jefeBtn.Click += async (s, e) => await HeaderToggle(_sel, false);
            _jefesBtn = Btn(Tr("Jefes")); _jefesBtn.Width = Theme.Px(70); _jefesBtn.BackColor = Theme.Surface2; _jefesBtn.ForeColor = Theme.Text;
            _jefesBtn.Click += (s, e) => JefesMenu();
            _jefeLbl = new Label { AutoSize = false, Dock = DockStyle.Right, Width = Theme.Px(330), TextAlign = ContentAlignment.MiddleRight, Font = Theme.Font(9f, FontStyle.Bold),
                                   ForeColor = Theme.Subtle, BackColor = top.BackColor, Padding = new Padding(0, 0, Theme.Px(10), 0) };
            var gap1 = new Panel { Dock = DockStyle.Right, Width = Theme.Px(6), BackColor = top.BackColor };
            var gap2 = new Panel { Dock = DockStyle.Right, Width = Theme.Px(6), BackColor = top.BackColor };
            top.Controls.Add(_jefeLbl); top.Controls.Add(_jefeBtn); top.Controls.Add(gap1); top.Controls.Add(_jefesBtn); top.Controls.Add(gap2);
            // clic derecho en la lista: dejar todo lo tuyo
            _list.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Right || !IHaveAny) return;
                var m = MenuStyle.Apply(new ContextMenuStrip());
                m.Items.Add(new ToolStripMenuItem(Tr("Dejar todas mis dependencias"), null, (o, a) => _pilot.JefeLeave()));
                m.Show(_list, e.Location);
            };
        }

        // (Prepare) ¿puedo ser jefe? y lo que diga la sala
        async Task PrepareJefe()
        {
            _pilot.JefeChanged += () => { try { if (!IsDisposed) BeginInvoke(new Action(OnJefeChanged)); } catch { } };
            _pilot.JefeError += e => { try { if (!IsDisposed) BeginInvoke(new Action(() => JefeErr(e))); } catch { } };
            try { if (ChiefMe != null) (_chiefAllowed, _chiefAdmin) = await ChiefMe(); } catch { }
            UpdateJefe();
        }

        // (MainMenuForm) ¿puedo ser jefe? · un vale · la lista y apuntar o quitar (administrador)
        public Func<Task<(bool allowed, bool admin)>> ChiefMe;
        public Func<Task<(string ticket, string err)>> IssueTicket;
        public Func<Task<(List<string> names, string err)>> ChiefsList;
        public Func<string, bool, Task<string>> ChiefSet;

        void OnJefeChanged()
        {
            // recién entrado como jefe: las estaciones que se habían pedido
            if (Me != null && _toAdd.Count > 0) { foreach (var (z, f) in _toAdd.ToList()) SendAdd(z, f); _toAdd.Clear(); }
            _taking = false;
            if (Me != null) _lastMe = Me;
            _list.Invalidate();
            UpdateJefe();
        }

        // ¿el enclavamiento elegido es mío / de otro / de nadie?
        bool HeaderMine(SchematicHeader h) => h != null && h.Zones.Count > 0 && h.Zones.All(Mine);
        string HeaderOwner(SchematicHeader h) => h?.Zones.Select(OwnerOf).FirstOrDefault(o => o != null && o != Me);
        bool HeaderFree(SchematicHeader h) => h != null && h.Zones.All(z => OwnerOf(z) == null || Mine(z));

        void UpdateJefe()
        {
            if (_jefeLbl == null) return;
            var h = _sel; string own = HeaderOwner(h); bool mine = HeaderMine(h);
            int n = Me == null ? 0 : _pilot.JefeZones.Count(q => q.Name == Me);
            _jefeLbl.Text = h == null ? (_chiefAllowed ? Tr("Abre una dependencia para tomarla") : "")
                          : (mine ? Tr("Esta dependencia es tuya") : own != null ? string.Format(Tr("Jefe de circulación: {0}"), own) : Tr("Sin jefe en esta dependencia"));
            if (n > 0) _jefeLbl.Text += (_jefeLbl.Text.Length > 0 ? "  ·  " : "") + string.Format(n == 1 ? Tr("{0} tuyo") : Tr("{0} tuyos"), n);
            _jefeLbl.ForeColor = h == null || (own == null && !mine) ? Theme.Subtle : mine ? Color.FromArgb(102, 187, 106) : Color.FromArgb(255, 213, 79);
            bool ready = _pilot != null && _pilot.Ready && _sigZone != null && h != null;
            _jefeBtn.Visible = _chiefAllowed && ready && HeaderFree(h);
            _jefeBtn.Text = mine ? Tr("Dejar esta dependencia") : _taking ? Tr("Tomando el mando…") : Tr("Tomar esta dependencia");
            _jefeBtn.BackColor = mine ? Color.FromArgb(183, 28, 28) : Theme.Accent;
            _jefesBtn.Visible = _chiefAdmin && _pilot != null && _pilot.Ready;
            UpdateManualBtn();
            RefreshHeaders();
            _canvas.Invalidate();
        }

        bool JefeBarStale() => _jefeBtn.Visible != (_chiefAllowed && _pilot != null && _pilot.Ready && _sigZone != null && _sel != null && HeaderFree(_sel));

        // tomar o dejar un enclavamiento (todas sus zonas)
        async Task HeaderToggle(SchematicHeader h, bool force)
        {
            if (h == null) return;
            if (HeaderMine(h)) { foreach (var z in h.Zones) _pilot.JefeDel(z); return; }
            foreach (var z in h.Zones.Where(z => !Mine(z))) await ZoneToggle(z, force);
        }

        // tomar o dejar una zona
        async Task ZoneToggle(string zone, bool force)
        {
            if (zone == null || _pilot == null || !_pilot.Ready || _sigZone == null) return;
            if (Mine(zone)) { _pilot.JefeDel(zone); return; }
            if (Me != null) { SendAdd(zone, force); return; }
            if (_taking) { _toAdd.Add((zone, force)); return; }
            _taking = true; UpdateJefe();
            if (!await EnsureTicket()) { _taking = false; UpdateJefe(); return; }
            _toAdd.Add((zone, force));
            _pilot.JefeTake(_ticket);   // (la sala contesta JEFEOK: OnJefeChanged manda las zonas)
            _ = Task.Delay(6000).ContinueWith(_ => { try { BeginInvoke(new Action(() => { if (_taking) { _taking = false; UpdateJefe(); } })); } catch { } });
        }

        void SendAdd(string zone, bool force)
        {
            var sigs = _sigZone.Where(kv => kv.Value == zone).Select(kv => kv.Key).OrderBy(k => k).ToList();
            var nodes = (_nodeZone ?? new()).Where(kv => kv.Value == zone).Select(kv => kv.Key).OrderBy(k => k).ToList();
            var keys = new List<string>();
            foreach (int n in nodes)
                if (_geo != null && _geo.OrderOf.TryGetValue(n, out int o) && o >= 0 && o < _geo.Switches.Count) { var w = _geo.Switches[o]; keys.Add($"{w.Tx}_{w.Tz}_{w.Id}"); }
            _pilot.JefeAdd(zone, sigs, nodes, keys.Distinct(), force);
        }

        void JefeErr(string e)
        {
            _taking = false; _toAdd.Clear(); UpdateJefe();
            if (e.StartsWith("ticket")) _ticket = null;   // (caducado o ya no vale: la próxima vez, uno nuevo)
            string msg;
            if (e.StartsWith("busy "))
            {
                var p = e.Split(' ');
                msg = string.Format(Tr("{0} ya tiene un jefe de circulación: {1}. Cada estación, un jefe."), p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "", p.Length > 2 ? p[2] : "");
            }
            else msg = e.StartsWith("offline") ? Tr("La sala no puede comprobar tu permiso ahora mismo. Inténtalo dentro de un rato.")
                     : Tr("La sala no acepta tu permiso de jefe de circulación (puede haber caducado). Vuelve a intentarlo.");
            ThemedBox.Show(this, msg, Tr("Jefe de circulación"));
        }

        // (al cerrar la ventana) suelta sus estaciones
        void LeaveOnClose() { try { if (Me != null) _pilot.JefeLeave(); } catch { } }

        // (cada segundo) si se cortó y ha vuelto la conexión: otra vez como jefe (sus estaciones le esperan 2 min)
        DateTime _retakeAt;
        void JefeKeepAlive()
        {
            if (_pilot == null || !_pilot.Ready || Me != null || _ticket == null || _taking) return;
            if (!_pilot.JefeZones.Any(z => z.Name == _lastMe) || DateTime.UtcNow < _retakeAt) return;
            _retakeAt = DateTime.UtcNow.AddSeconds(10);
            _pilot.JefeTake(_ticket);
        }
        string _lastMe;

        // ── administrador: los jefes de circulación
        async void JefesMenu()
        {
            if (ChiefsList == null) return;
            var (names, err) = await ChiefsList();
            var m = MenuStyle.Apply(new ContextMenuStrip());
            if (err != null) m.Items.Add(new ToolStripMenuItem(err) { Enabled = false });
            else
            {
                m.Items.Add(new ToolStripMenuItem(names.Count == 0 ? Tr("Ningún jefe de circulación apuntado") : Tr("Jefes de circulación (clic para quitar):")) { Enabled = false });
                foreach (var n in names)
                {
                    string nn = n;
                    m.Items.Add(new ToolStripMenuItem("   " + nn, null, async (s, e) =>
                    {
                        if (ThemedBox.Show(this, string.Format(Tr("¿Quitar a {0} de jefe de circulación?"), nn), Tr("Jefes de circulación"), MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                        string r = await ChiefSet(nn, false);
                        if (r != null) ThemedBox.Show(this, r, Tr("Jefes de circulación"));
                    }));
                }
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem(Tr("Apuntar a un maquinista…"), null, async (s, e) =>
            {
                using var d = new TextPromptDialog(Tr("Jefe de circulación"), Tr("Nombre del maquinista:"), "", "", Tr("Apuntar"), "🚦");
                if (d.ShowDialog(this) != DialogResult.OK || d.Value.Trim().Length == 0) return;
                string r = await ChiefSet(d.Value.Trim(), true);
                ThemedBox.Show(this, r ?? string.Format(Tr("{0} ya puede tomar el mando desde el puesto de mando."), d.Value.Trim()), Tr("Jefes de circulación"));
            }));
            // los que tienen estaciones ahora: quitarles todas
            var now = _pilot?.JefeZones.Select(z => z.Name).Distinct().Where(n => n != Me).ToList() ?? new List<string>();
            if (now.Count > 0) m.Items.Add(new ToolStripSeparator());
            foreach (var n in now)
            {
                string nn = n;
                m.Items.Add(new ToolStripMenuItem(string.Format(Tr("Quitarle el mando a {0}"), nn), null, async (s, e) => { if (await EnsureTicket()) _pilot.JefeDrop(_ticket, nn); }));
            }
            var h0 = _sel; string own = HeaderOwner(h0);
            if (h0 != null && own != null)
                m.Items.Add(new ToolStripMenuItem(string.Format(Tr("Quitarle «{0}» a {1} y tomarla"), h0.Name, own), null, async (s, e) => await HeaderToggle(h0, true)));
            m.Show(_jefesBtn, new Point(0, _jefesBtn.Height));
        }

        async Task<bool> EnsureTicket()
        {
            if (_ticket != null) return true;
            var (t, err) = IssueTicket != null ? await IssueTicket() : (null, null);
            if (t == null) { ThemedBox.Show(this, err ?? Tr("No disponible."), Tr("Jefe de circulación")); return false; }
            _ticket = t; return true;
        }

        // ── en tus estaciones: clic en una aguja o en una señal (true = era una aguja o una señal)
        bool JefeClick(Point p)
        {
            if (Board) return JefeClickBoard(p);
            if (Me == null || _cur == null || _geo == null) return false;
            PointF Sc(PointF a) => new PointF(_origin.X + a.X * _zoom, _origin.Y + a.Y * _zoom);
            float R = Theme.Px(11);
            static float D(PointF a, Point b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
            // las señales primero (son más pequeñas y suelen estar junto a una aguja)
            // (1.2.75) donde se ve la señal (mástil, luces, número); si no, junto al pie del mástil como antes
            var sg = SchematicDraw.SignalAt(_cur, p, Theme.Px(4)) is Schematic.Signal hit && hit.Sig != null && hit.Sig.Obj >= 0 && MySig(hit.Sig.Obj) ? hit
                : _cur.Signals.Where(q => q.Sig != null && q.Sig.Obj >= 0 && MySig(q.Sig.Obj)).Select(q => (q, d: D(Sc(q.At), p))).Where(x => x.d < R).OrderBy(x => x.d).Select(x => x.q).FirstOrDefault();
            if (sg != null) { SignalMenu(sg.Sig, p); return true; }
            var sws = _cur.Switches.Where(q => MyNode(q.Node)).Select(q => (q, d: D(Sc(q.At), p))).Where(x => x.d < R * 1.4f).OrderBy(x => x.d).Select(x => x.q).ToList();
            if (sws.Count == 0) return false;
            var live = Live();
            int Pos(Schematic.Switch s) => live?.SwitchPos != null && s.Order >= 0 ? live.SwitchPos(s.Order) : -1;
            if (sws.Count == 1) { Throw(sws[0].Node, sws[0].Order, Pos(sws[0])); return true; }
            var m = MenuStyle.Apply(new ContextMenuStrip());
            m.Items.Add(new ToolStripMenuItem(string.Format(Tr("{0} agujas aquí"), sws.Count)) { Enabled = false });
            foreach (var s in sws)
            {
                var ss = s; int pos = Pos(s);
                m.Items.Add(new ToolStripMenuItem(string.Format(Tr("Aguja {0}: {1} → cambiar"), s.Node, pos == 0 ? Tr("normal") : pos == 1 ? Tr("desviada") : Tr("sin dato")), null, (o, e) => Throw(ss.Node, ss.Order, pos)));
            }
            m.Show(_canvas, p);
            return true;
        }

        void Throw(int node, int order, int pos)
        {
            int to = pos == 1 ? 0 : 1;
            _pilot.JefeThrowNode(node, to);
            if (order >= 0) lock (_swSet) _swSet[order] = (to, DateTime.UtcNow);   // a la vista ya (la sala lo confirma)
            _canvas.Invalidate();
        }

        // (1.2.76) lo mismo en el tablero: una señal tuya (su figura, su mástil o su número) o una aguja tuya
        bool JefeClickBoard(Point p)
        {
            if (Me == null || _board == null) return false;
            float R = Theme.Px(11);
            static float D(PointF a, Point b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
            var sg = RouteBoardDraw.SignalAt(_board, p, Theme.Px(4)) is RouteBoard.Sg hit && hit.S.Obj >= 0 && MySig(hit.S.Obj) ? hit
                : _board.Signals.Where(q => q.S.Obj >= 0 && MySig(q.S.Obj)).Select(q => (q, d: D(Bp(q.At), p))).Where(x => x.d < R).OrderBy(x => x.d).Select(x => x.q).FirstOrDefault();
            if (sg != null) { SignalMenu(sg.S, p); return true; }
            var sws = _board.Switches.Where(q => MyNode(q.Node)).Select(q => (q, d: D(Bp(q.At), p))).Where(x => x.d < R * 1.4f).OrderBy(x => x.d).Select(x => x.q).ToList();
            if (sws.Count == 0 && RouteBoardDraw.SwitchAt(_board, p) is RouteBoard.Sw bw && MyNode(bw.Node)) sws.Add(bw);
            if (sws.Count == 0) return false;
            var live = LiveBoard();
            int Pos(RouteBoard.Sw s) => live?.SwitchPos != null && s.Order >= 0 ? live.SwitchPos(s.Order) : -1;
            if (sws.Count == 1) { Throw(sws[0].Node, sws[0].Order, Pos(sws[0])); return true; }
            var m = MenuStyle.Apply(new ContextMenuStrip());
            m.Items.Add(new ToolStripMenuItem(string.Format(Tr("{0} agujas aquí"), sws.Count)) { Enabled = false });
            foreach (var s in sws)
            {
                var ss = s; int pos = Pos(s);
                string name = s.Number > 0 ? s.Number.ToString() : s.Node.ToString();
                m.Items.Add(new ToolStripMenuItem(string.Format(Tr("Aguja {0}: {1} → cambiar"), name, pos == 0 ? Tr("normal") : pos == 1 ? Tr("desviada") : Tr("sin dato")), null, (o, e) => Throw(ss.Node, ss.Order, pos)));
            }
            m.Show(_canvas, p);
            return true;
        }

        // (Paint_) con algún enclavamiento tuyo en la línea, una franja abajo que lo recuerda
        void DrawJefeBand(Graphics g, Rectangle area)
        {
            if (Me == null || !_heads.Any(h => h.Zones.Any(Mine))) return;
            if (_pick != null || (_itinMsg != null && DateTime.UtcNow < _itinMsgUntil)) return;   // (arriba sale el itinerario)
            string t = Tr("Dependencia tuya: clic en una aguja para cambiarla · clic en una señal: itinerarios y órdenes");
            using var f = Theme.Font(8.6f, FontStyle.Bold);
            var sz = TextRenderer.MeasureText(t, f);
            var r = new Rectangle(area.Left + Theme.Px(12), area.Bottom - sz.Height - Theme.Px(20), sz.Width + Theme.Px(20), sz.Height + Theme.Px(10));
            using (var b = new SolidBrush(Color.FromArgb(220, 27, 64, 32))) g.FillRectangle(b, r);
            TextRenderer.DrawText(g, t, f, r, Color.FromArgb(200, 240, 200), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        // (lista de líneas) quién manda en ella, a la derecha
        void DrawOwnerTag(Graphics g, Rectangle bounds, string own, bool mine, int rightPad)
        {
            using var f = Theme.Font(7.6f, FontStyle.Bold);
            var col = mine ? Color.FromArgb(102, 187, 106) : Color.FromArgb(255, 213, 79);
            TextRenderer.DrawText(g, own, f, new Rectangle(bounds.Right - rightPad - Theme.Px(66), bounds.Y, Theme.Px(64), bounds.Height), col,
                                  TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        // (MainMenuForm) las llamadas a Supabase
        public static async Task<(bool, bool)> SupaChiefMe()
        {
            var (json, err) = await Supa.RpcAsync("dispatch_chief_me", new { });
            if (err != null || string.IsNullOrWhiteSpace(json)) return (false, false);
            try
            {
                using var d = JsonDocument.Parse(json); var r = d.RootElement;
                return (r.TryGetProperty("allowed", out var a) && a.ValueKind == JsonValueKind.True, r.TryGetProperty("admin", out var b) && b.ValueKind == JsonValueKind.True);
            }
            catch { return (false, false); }
        }

        public static async Task<(string, string)> SupaTicket()
        {
            var (json, err) = await Supa.RpcAsync("dispatch_ticket_issue", new { });
            if (err != null) return (null, err.Contains("PGRST202") ? Tr("El servidor aún no tiene el jefe de circulación (falta jefe-circulacion.sql).") : err);
            try { string t = JsonSerializer.Deserialize<string>(json); return t?.Length == 64 ? (t, null) : (null, Tr("No se ha podido tomar el mando.")); }
            catch { return (null, Tr("No se ha podido tomar el mando.")); }
        }

        public static async Task<(List<string>, string)> SupaChiefs()
        {
            var (json, err) = await Supa.RpcAsync("dispatch_chiefs_list", new { });
            if (err != null) return (null, err);
            var l = new List<string>();
            try { using var d = JsonDocument.Parse(json); foreach (var e in d.RootElement.EnumerateArray()) if (e.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String) l.Add(u.GetString()); }
            catch { }
            return (l, null);
        }

        public static async Task<string> SupaChiefSet(string name, bool on)
        {
            var (_, err) = await Supa.RpcAsync("dispatch_chief_set", new { p_username = name, p_on = on });
            return err;
        }
    }
}
