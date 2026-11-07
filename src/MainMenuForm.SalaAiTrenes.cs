// (1.2.91, petición 2026-11-07) LOS TRENES IA DE LA SALA, COMO LOS QUIERE VER CADA UNO (como «Elegir el tren de esta línea…»
// en Horarios): en la casilla de la sala, 🚆 → «Cómo veo los trenes IA…»: los servicios del horario de la sala
// (room_ai_get, lo puede leer cualquiera) con su composición y, para cada uno, «Elegir…» un tren de tu carpeta o «Quitar»
// (vuelve la del horario). Se guarda en las preferencias (AppPrefs.SalaAiConsist: servicio → .con) y, conduciendo en la
// sala, se manda al agente por el enlace local («AICON …», MapAgent\IaComposiciones.cs), que monta así los trenes IA de
// esos servicios SOLO en tu Open Rails. El administrador y los gestores del horario eligen en el 🚆 entre esto y el horario.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        bool _aiConSent;   // (se vuelve a mandar al conectarse Open Rails o al cambiar la elección)

        void SalaAiButtonClick()
        {
            if (Supa.IsSuperadmin || Supa.IsAiEditor)
            {
                var m = MenuStyle.Apply(new ContextMenuStrip());
                m.Items.Add(Tr("Editar el horario de la sala…"), null, (s, e) => OpenHorarioSala());
                m.Items.Add(Tr("Cómo veo los trenes IA…"), null, (s, e) => _ = OpenSalaAiLooks());
                m.Show(_salaAiBtn, new Point(0, _salaAiBtn.Height));
                return;
            }
            _ = OpenSalaAiLooks();
        }

        sealed class AiService { public string Name, From, To, Con, Kind; public bool Active; }

        async Task OpenSalaAiLooks()
        {
            List<AiService> list;
            try
            {
                var (json, err) = await Supa.RpcAsync("room_ai_get", new { p_route = SalaObjRoute });
                if (err != null || string.IsNullOrWhiteSpace(json)) { ThemedBox.Show(this, Tr("No se ha podido leer el horario de la sala: ") + (err ?? "—"), Tr("Trenes IA de la sala"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                list = new List<AiService>();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("services", out var sv) && sv.ValueKind == JsonValueKind.Array)
                    foreach (var e in sv.EnumerateArray())
                    {
                        string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                        list.Add(new AiService { Name = S("name"), From = S("origin"), To = S("destination"), Con = S("consist"), Kind = S("kind"), Active = !e.TryGetProperty("active", out var a) || a.ValueKind != JsonValueKind.False });
                    }
            }
            catch (Exception ex) { ThemedBox.Show(this, Tr("No se ha podido leer el horario de la sala: ") + ex.Message, Tr("Trenes IA de la sala"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            list = list.Where(x => x.Active && x.Name.Length > 0).ToList();
            if (list.Count == 0) { ThemedBox.Show(this, Tr("La sala no tiene trenes IA en su horario."), Tr("Trenes IA de la sala"), MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            ShowAiLooksDialog(list);
        }

        void ShowAiLooksDialog(List<AiService> list)
        {
            using var f = new Form
            {
                Text = Tr("Trenes IA de la sala"), BackColor = Theme.Bg, ForeColor = Theme.Text, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Theme.Font(9.5f), FormBorderStyle = FormBorderStyle.FixedDialog,
            };
            DialogKit.Dress(f);
            int rowH = Theme.Px(132);
            f.ClientSize = new Size(Theme.Px(820), Math.Min(Theme.Px(720), Theme.Px(150) + list.Count * (rowH + Theme.Px(8)) + Theme.Px(70)));
            var rows = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(Theme.Px(20), Theme.Px(8), Theme.Px(20), Theme.Px(8)) };
            var foot = new Panel { Dock = DockStyle.Bottom }; DialogKit.StyleFooter(foot);
            var close = DialogKit.Primary(Tr("Cerrar"), Theme.Px(140)); close.Dock = DockStyle.Right; close.Click += (s, e) => f.Close();
            var note = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Subtle, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Font(8.6f),
                                   Text = Tr("Solo cambia cómo los ves tú: el despachador y los demás siguen con el del horario. Vale para los trenes que aparezcan desde ese momento.") };
            foot.Controls.Add(note); foot.Controls.Add(close);
            f.Controls.Add(rows); f.Controls.Add(foot);
            DialogKit.Crown(f, Tr("Cómo veo los trenes IA"), Tr("Elige un tren de tu carpeta para cada servicio del horario de la sala"), "🚆");

            void Fill()
            {
                rows.SuspendLayout(); rows.Controls.Clear();
                int y = 0, width = rows.ClientSize.Width - rows.Padding.Horizontal;
                foreach (var sv in list)
                {
                    var row = new Panel { Left = 0, Top = y, Width = width, Height = rowH, BackColor = Theme.Surface, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
                    _prefs.SalaAiConsist.TryGetValue(sv.Name, out var mine);
                    bool has = !string.IsNullOrEmpty(mine);
                    var t1 = new Label { Text = sv.Name, Font = Theme.Font(10f, FontStyle.Bold), ForeColor = Theme.Text, AutoSize = false, Location = new Point(Theme.Px(12), Theme.Px(8)), Size = new Size(width - Theme.Px(260), Theme.Px(20)), AutoEllipsis = true };
                    string route = (sv.From.Length > 0 || sv.To.Length > 0) ? sv.From + " → " + sv.To + "  ·  " : "";
                    var t2 = new Label
                    {
                        Text = route + (has ? string.Format(Tr("tú: {0}"), Path.GetFileNameWithoutExtension(mine)) : string.Format(Tr("del horario: {0}"), Path.GetFileNameWithoutExtension(sv.Con))),
                        Font = Theme.Font(8.6f), ForeColor = has ? Theme.AccentHi : Theme.Subtle, AutoSize = false, Location = new Point(Theme.Px(12), Theme.Px(30)), Size = new Size(width - Theme.Px(260), Theme.Px(18)), AutoEllipsis = true
                    };
                    var pick = DialogKit.Secondary(Tr("Elegir…"), Theme.Px(110)); pick.Anchor = AnchorStyles.Right | AnchorStyles.Top;
                    pick.Location = new Point(row.Width - Theme.Px(12) - pick.Width - (has ? Theme.Px(100) : 0), Theme.Px(10));
                    var svc = sv;
                    pick.Click += (s, e) =>
                    {
                        if (_consistsAll.Count == 0) { ThemedBox.Show(f, Tr("No hay trenes en esta carpeta de contenido."), "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                        _prefs.SalaAiConsist.TryGetValue(svc.Name, out var cur);
                        var current = _consistsAll.FirstOrDefault(c => string.Equals(c.FilePath, cur ?? SalaConPath(svc.Con), StringComparison.OrdinalIgnoreCase));
                        var chosen = PickTrainDialog(string.Format(Tr("Cómo ves «{0}»"), svc.Name), current);
                        if (chosen == null || string.IsNullOrEmpty(chosen.FilePath)) return;
                        // el mismo del horario: no hace falta guardar nada
                        if (string.Equals(chosen.FilePath, SalaConPath(svc.Con), StringComparison.OrdinalIgnoreCase)) _prefs.SalaAiConsist.Remove(svc.Name);
                        else _prefs.SalaAiConsist[svc.Name] = chosen.FilePath;
                        _prefs.Save(); _aiConSent = false;
                        Fill();
                    };
                    row.Controls.Add(t1); row.Controls.Add(t2); row.Controls.Add(pick);
                    if (has)
                    {
                        var drop = DialogKit.Secondary(Tr("Quitar"), Theme.Px(90)); drop.Anchor = AnchorStyles.Right | AnchorStyles.Top;
                        drop.Location = new Point(row.Width - Theme.Px(12) - drop.Width, Theme.Px(10));
                        drop.Click += (s, e) => { _prefs.SalaAiConsist.Remove(svc.Name); _prefs.Save(); _aiConSent = false; Fill(); };
                        row.Controls.Add(drop);
                    }
                    // (1.2.91) su composición 2D: la elegida por ti o, si no, la del horario de la sala
                    var strip = new ConsistStripView
                    {
                        Location = new Point(Theme.Px(12), Theme.Px(54)), Size = new Size(width - Theme.Px(24), rowH - Theme.Px(62)),
                        Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Cursor = Cursors.Default,
                        Placeholder = Tr("No tienes este tren en tu carpeta")
                    };
                    string cap = Tr("COMPOSICIÓN 2D") + "  ·  " + (has ? Tr("la que has elegido") : Tr("la del horario"));
                    string con = has ? mine : SalaConPath(sv.Con);
                    row.Controls.Add(strip);
                    if (con == null || !File.Exists(con)) strip.Set(null, cap, false);
                    else if (_stripMem.TryGetValue(con, out var mem)) strip.Set(mem, cap, false);
                    else
                    {
                        strip.Set(null, cap, true);
                        _ = LoadStrip(strip, con, cap);
                    }
                    rows.Controls.Add(row);
                    y += row.Height + Theme.Px(8);
                }
                rows.ResumeLayout();
            }
            async Task LoadStrip(ConsistStripView strip, string con, string cap)
            {
                try
                {
                    string file = await ConsistStripFileAsync(con);
                    var bmp = await Task.Run(() => LoadStripFile(file));
                    if (bmp != null) { if (_stripMem.Count > 40) _stripMem.Clear(); _stripMem[con] = bmp; }
                    if (!strip.IsDisposed) strip.Set(bmp, cap, false);
                }
                catch { if (!strip.IsDisposed) strip.Set(null, cap, false); }
            }
            f.Shown += (s, e) => Fill();
            f.ShowDialog(this);
        }

        // el .con de un servicio del horario de la sala, en tu carpeta de contenido (la ruta entera si existe; si no, por su
        // nombre en TRAINS\CONSISTS); null si no lo tienes
        string SalaConPath(string con)
        {
            if (string.IsNullOrWhiteSpace(con)) return null;
            try
            {
                if (File.Exists(con)) return con;
                string dir = Path.Combine(_curFolder?.Path ?? "", "TRAINS", "CONSISTS");
                string n = con.Replace('/', '\\');
                foreach (var cand in new[] { n, Path.GetFileName(n) })
                {
                    string p = Path.Combine(dir, cand.EndsWith(".con", StringComparison.OrdinalIgnoreCase) ? cand : cand + ".con");
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        // (LocalMapTick, cada segundo) conduciendo en la sala con el agente conectado: la lista, al conectarse o al cambiar
        void AiConsistTick(bool justConnected)
        {
            if (justConnected) _aiConSent = false;
            if (_aiConSent) return;
            if (_roomJoined == null && _roomPending == null) return;
            var l = _localMap; if (l == null || !l.Connected) return;
            try
            {
                l.Send("AICON BEGIN");
                foreach (var kv in _prefs.SalaAiConsist)
                    if (!string.IsNullOrWhiteSpace(kv.Key) && File.Exists(kv.Value)) l.Send("AICON " + kv.Key.Replace('\t', ' ') + "\t" + kv.Value);
                l.Send("AICON END");
                _aiConSent = true;
                if (_prefs.SalaAiConsist.Count > 0) RoomLog($"trenes IA de la sala: {_prefs.SalaAiConsist.Count} con tu composición mandados a Open Rails");
            }
            catch { }
        }
    }
}
