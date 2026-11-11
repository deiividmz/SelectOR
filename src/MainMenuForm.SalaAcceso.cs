// Revisión → «Expulsiones» (2026-11-09, solo el administrador; sql/sala-acceso.sql): sancionar por la conducta en la sala,
// prohibir la sala compartida durante un tiempo (con el motivo que le sale al intentar entrar), ver y levantar las
// prohibiciones y poner los requisitos para entrar en la sala (puntos del carné, título de maquinista, servicios hechos).
// Lo comprueban SelectOR antes de lanzar (MainMenuForm.SalaCgl.cs: SalaAccessDenied) y el servidor de la sala al entrar.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        Label _kickMsg;

        // los botones de debajo de la lista de expulsiones
        Control BuildKickActions()
        {
            var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Bg, Padding = new Padding(0, 6, 0, 0) };
            RoundButton B(string text, Action a, bool primary = false) { var b = EmpButton(Tr(text), primary); b.Width = 200; b.Margin = new Padding(0, 4, 8, 2); b.Click += (s, e) => a(); btns.Controls.Add(b); return b; }
            B("Sancionar…", () => _ = RoomSanctionDialog());
            B("Prohibir la sala…", () => _ = RoomBanDialog());
            B("Prohibiciones…", () => _ = RoomBansDialog());
            _kickMsg = EmpMsg(); _kickMsg.MaximumSize = new System.Drawing.Size(520, 0);
            btns.Controls.Add(_kickMsg);
            return btns;
        }

        // el usuario de la expulsión elegida (para no tener que escribirlo)
        string KickSelectedUser()
        {
            int i = _kickList?.SelectedRow ?? -1;
            return i >= 0 && i < _kickShown.Count ? _kickShown[i] : "";
        }

        async Task RoomSanctionDialog()
        {
            using var dlg = new FormDialog(Tr("Sancionar"), Tr("Sancionar"), 560, "⚖️", Tr("Por su conducta en la sala compartida"));
            dlg.AddText("u", Tr("Nombre de jugador o ID"), KickSelectedUser());
            dlg.AddCombo("pts", Tr("Puntos que se quitan del carné"), Enumerable.Range(1, 15).Select(p => p.ToString(I18n.Culture)), 1);
            dlg.AddMultiline("reason", Tr("Motivo (lo verá el maquinista)"), "", 90);
            dlg.AddInfo(Tr("Le llega el aviso y puede alegar, como en las demás sanciones. Sin servicio, no hay multa ni anulación."));
            string done = null;
            dlg.Validate = async f =>
            {
                string u = f.Get("u").Trim(), reason = f.Get("reason").Trim();
                if (u.Length == 0) return Tr("Escribe el nombre o el ID.");
                if (reason.Length < 3) return Tr("Escribe el motivo de la sanción.");
                int pts = Math.Max(0, f.Index("pts")) + 1;
                var (json, e) = await Supa.RpcAsync("admin_sanction_room", new { p_user = u, p_points = pts, p_reason = reason });
                if (e != null) return MissingRpc(e) ? Tr("El servidor aún no tiene esta opción (falta sala-acceso.sql).") : Tr("Error: ") + CleanRpcErr(e);
                done = string.Format(Tr("Sanción puesta a {0}: {1}. Le ha llegado el aviso."), UserOf(json, u), Carne.PointsText(-pts));
                return null;
            };
            if (dlg.ShowDialog(this) == DialogResult.OK && done != null) Msg(_kickMsg, done, false);
        }

        static readonly int[] BanDays = { 1, 3, 7, 15, 30, 90, 0 };

        async Task RoomBanDialog()
        {
            using var dlg = new FormDialog(Tr("Prohibir la sala"), Tr("Prohibir"), 560, "⛔", Tr("No podrá entrar en la sala compartida"));
            dlg.AddText("u", Tr("Nombre de jugador o ID"), KickSelectedUser());
            dlg.AddCombo("days", Tr("Durante"), BanDays.Select(d => d == 0 ? Tr("Sin fecha de fin") : d == 1 ? Tr("1 día") : string.Format(Tr("{0} días"), d)), 2);
            dlg.AddMultiline("reason", Tr("Motivo (le sale al intentar entrar)"), "", 90);
            dlg.AddInfo(Tr("Si ya está en la sala, sale la próxima vez que entre. La puedes levantar antes en «Prohibiciones…»."));
            string done = null;
            dlg.Validate = async f =>
            {
                string u = f.Get("u").Trim(), reason = f.Get("reason").Trim();
                if (u.Length == 0) return Tr("Escribe el nombre o el ID.");
                if (reason.Length < 3) return Tr("Escribe el motivo.");
                int days = BanDays[Math.Max(0, f.Index("days"))];
                var (json, e) = await Supa.RpcAsync("admin_room_ban", new { p_user = u, p_days = days, p_reason = reason });
                if (e != null) return MissingRpc(e) ? Tr("El servidor aún no tiene esta opción (falta sala-acceso.sql).") : Tr("Error: ") + CleanRpcErr(e);
                done = days == 0 ? string.Format(Tr("{0} no puede entrar en la sala compartida (sin fecha de fin)."), UserOf(json, u))
                                 : string.Format(Tr("{0} no puede entrar en la sala compartida durante {1}."), UserOf(json, u), days == 1 ? Tr("1 día") : string.Format(Tr("{0} días"), days));
                return null;
            };
            if (dlg.ShowDialog(this) == DialogResult.OK && done != null) Msg(_kickMsg, done, false);
        }

        async Task RoomBansDialog()
        {
            var (json, err) = await Supa.RpcAsync("admin_room_bans", new { });
            if (err != null) { Msg(_kickMsg, MissingRpc(err) ? Tr("El servidor aún no tiene esta opción (falta sala-acceso.sql).") : Tr("Error: ") + CleanRpcErr(err), true); return; }
            var act = new List<(long id, string text)>();
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    if (!(e.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True)) continue;
                    string until = Str(e, "until");
                    string when = until.Length > 0 && DateTime.TryParse(until, out var t) ? string.Format(Tr("hasta el {0}"), t.ToLocalTime().ToString("g", I18n.Culture)) : Tr("sin fecha de fin");
                    act.Add(((long)Num(e, "id"), $"{Str(e, "username")} · {when} · «{Str(e, "reason")}»"));
                }
            }
            catch { }
            if (act.Count == 0) { Msg(_kickMsg, Tr("No hay ninguna prohibición vigente."), false); return; }
            using var dlg = new FormDialog(Tr("Prohibiciones de la sala"), Tr("Levantar la prohibición"), 640, "⛔", string.Format(Tr("{0} vigentes"), act.Count));
            dlg.AddCombo("b", Tr("Prohibición"), act.Select(x => x.text), 0);
            dlg.AddInfo(Tr("Al levantarla, puede volver a entrar en la sala enseguida."));
            string done = null;
            dlg.Validate = async f =>
            {
                var b = act[Math.Max(0, f.Index("b"))];
                var (_, e) = await Supa.RpcAsync("admin_room_unban", new { p_id = b.id });
                if (e != null) return Tr("Error: ") + CleanRpcErr(e);
                done = Tr("Prohibición levantada.");
                return null;
            };
            if (dlg.ShowDialog(this) == DialogResult.OK && done != null) Msg(_kickMsg, done, false);
        }

        static readonly string[] ReqLevels = { "", "B", "A", "A+" };

        // (2026-11-09) desde Empresas → Ajustes → Sala compartida (antes, un botón de Expulsiones)
        Label _salaSetMsg;
        Panel BuildSalaSettingsPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
            // (una fila: el texto a la izquierda y el botón a la derecha; así todo cabe sin barras)
            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, BackColor = Theme.Bg, Margin = new Padding(0) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var hdr = EmpHeader("REQUISITOS PARA ENTRAR EN LA SALA (ADMINISTRADOR)"); hdr.Margin = new Padding(2, 4, 2, 2);
            top.Controls.Add(hdr, 0, 0); top.SetColumnSpan(hdr, 2);
            var note = EmpNote(Tr("Puntos mínimos del carné, título de maquinista, servicios hechos y solo con cuenta de SelectOR. Quien no los cumpla ve el motivo al intentar entrar."));
            note.MaximumSize = new System.Drawing.Size(Theme.Px(640), 0);
            var req = EmpButton(Tr("Requisitos de la sala…"), primary: true); req.Width = 240; req.Margin = new Padding(12, 0, 2, 4);
            req.Click += (s, e) => _ = RoomReqDialog();
            top.Controls.Add(note, 0, 1); top.Controls.Add(req, 1, 1);
            _salaSetMsg = EmpMsg(); _salaSetMsg.Margin = new Padding(2, 0, 2, 0); top.Controls.Add(_salaSetMsg, 0, 2); top.SetColumnSpan(_salaSetMsg, 2);
            var hdr2 = EmpHeader("TIEMPOS DE LA SALA (ADMINISTRADOR)"); hdr2.Margin = new Padding(2, 6, 2, 0);
            top.Controls.Add(hdr2, 0, 3); top.SetColumnSpan(hdr2, 2);
            var times = new SalaTiemposPanel();
            page.Controls.Add(times); page.Controls.Add(top);
            page.VisibleChanged += (s, e) => { if (page.Visible) { times.Visible = false; times.Visible = true; } };   // (lee los tiempos al abrirla)
            return page;
        }

        Label _climaSetLbl;
        Panel BuildClimaSettingsPage()
        {
            var p = FormPage(false); p.Visible = false; p.Width = 720;
            p.Controls.Add(EmpHeader("CLIMA DE SELECTOR (ADMINISTRADOR)"));
            p.Controls.Add(EmpNote(Tr("Las imágenes de nubes, lluvia, nieve, luces y cielo nocturno que reciben los usuarios con el clima activado (Ajustes de SelectOR → Clima). Se publican firmadas con la clave de este equipo.")));
            _climaSetLbl = EmpNote(""); p.Controls.Add(_climaSetLbl);
            var b = EmpButton(Tr("Gestionar el clima…"), primary: true); b.Width = 240;
            b.Click += async (s, e) => { await SelectorSettingsDialog.OpenClimaAdmin(this, _prefs); RefreshClimaSummary(); };
            p.Controls.Add(b);
            // (2026-11-09) y el de la sala compartida (estación, tiempo, nubes, niebla y hora): lo mismo que el botón ⛅ de Conducción libre
            p.Controls.Add(EmpHeader("CLIMA DE LA SALA COMPARTIDA (ADMINISTRADOR)"));
            p.Controls.Add(EmpNote(Tr("La estación, el tiempo, las nubes, la niebla y la hora de la sala compartida de CGL, para todos los que están dentro.")));
            _climaSalaLbl = EmpNote(""); p.Controls.Add(_climaSalaLbl);
            var bs = EmpButton(Tr("Clima de la sala…"), primary: true); bs.Width = 240;
            bs.Click += async (s, e) => { await EditSalaWeather(); RefreshClimaSummary(); };
            p.Controls.Add(bs);
            return p;
        }
        Label _climaSalaLbl;
        void RefreshClimaSummary()
        {
            try { if (_climaSetLbl != null) _climaSetLbl.Text = SelectorSettingsDialog.ClimaSummary(); } catch { }
            try { if (_climaSalaLbl != null) { string w = SalaWeatherText(); _climaSalaLbl.Text = Tr("Ahora") + ": " + (w.Length > 0 ? w + " · " : "") + SalaClockText(); } } catch { }
        }

        async Task RoomReqDialog()
        {
            var (json, err) = await Supa.RpcAsync("room_requirements", new { });
            if (err != null) { Msg(_salaSetMsg ?? _kickMsg, MissingRpc(err) ? Tr("El servidor aún no tiene esta opción (falta sala-acceso.sql).") : Tr("Error: ") + CleanRpcErr(err), true); return; }
            int pts = 0, svc = 0; string lv = ""; bool only = false;
            try
            {
                using var d = JsonDocument.Parse(json); var r = d.RootElement;
                pts = (int)Num(r, "min_points"); svc = (int)Num(r, "min_services"); lv = Str(r, "driver_level");
                only = r.TryGetProperty("selector_only", out var o) && o.ValueKind == JsonValueKind.True;
            }
            catch { }
            using var dlg = new FormDialog(Tr("Requisitos de la sala"), Tr("Guardar"), 560, "🛡", Tr("Para entrar en la sala compartida (al administrador no le afectan)"));
            dlg.AddCombo("pts", Tr("Puntos mínimos del carné"), Enumerable.Range(0, 16).Select(p => p == 0 ? Tr("Sin mínimo") : p.ToString(I18n.Culture)), Math.Max(0, Math.Min(15, pts)));
            dlg.AddCombo("lv", Tr("Título de maquinista"), new[] { Tr("No hace falta"), "B", "A", "A+" }, Math.Max(0, Array.IndexOf(ReqLevels, lv ?? "")));
            dlg.AddText("svc", Tr("Servicios hechos (0 = sin mínimo)"), svc.ToString(I18n.Culture));
            dlg.AddCombo("only", Tr("Solo con cuenta de SelectOR"), new[] { Tr("No"), Tr("Sí") }, only ? 1 : 0);
            dlg.AddInfo(Tr("Con un mínimo de puntos, tampoco entra quien tenga el carné suspendido. Quien no los cumpla ve el motivo al intentar entrar."));
            string done = null;
            dlg.Validate = async f =>
            {
                if (!int.TryParse(f.Get("svc").Trim(), out int s) || s < 0 || s > 10000) return Tr("Escribe un número de servicios entre 0 y 10000.");
                var req = new Dictionary<string, object>
                {
                    ["min_points"] = Math.Max(0, f.Index("pts")), ["driver_level"] = ReqLevels[Math.Max(0, f.Index("lv"))] is string l && l.Length > 0 ? l : null,
                    ["min_services"] = s, ["selector_only"] = f.Index("only") == 1
                };
                var (_, e) = await Supa.RpcAsync("admin_room_req_set", new { p_req = req });
                if (e != null) return Tr("Error: ") + CleanRpcErr(e);
                done = Tr("Requisitos guardados: valen desde la próxima vez que alguien entre en la sala.");
                return null;
            };
            if (dlg.ShowDialog(this) == DialogResult.OK && done != null) Msg(_salaSetMsg ?? _kickMsg, done, false);
        }

        static string UserOf(string json, string fallback)
        {
            try { using var d = JsonDocument.Parse(json); var n = Str(d.RootElement, "username"); return n.Length > 0 ? n : fallback; }
            catch { return fallback; }
        }
    }
}
