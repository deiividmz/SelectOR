// Socios → Solicitudes de ingreso → «Ver perfil» (o doble clic en la solicitud), petición 2026-11-08: el gerente y los
// gestores ven el perfil de quien pide entrar antes de aceptarlo (join_request_profile, sql/perfil-solicitante.sql): foto,
// rango, cuánto ha conducido, trenes y rutas, carné, infracciones de los últimos 90 días, servicios en la sala, títulos y
// las empresas en las que ya está. Desde la misma ventana se acepta o se rechaza.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        async void ShowApplicantProfile()
        {
            if (_joinList == null || _empSel == null || !(CanManage() || Supa.IsSuperadmin)) return;
            int i = _joinList.SelectedRow;
            if (i < 0 || i >= _joinIds.Count) { Msg(_memberMsg, Tr("Selecciona una solicitud de la lista."), true); return; }
            string req = _joinIds[i];
            Msg(_memberMsg, Tr("Cargando el perfil…"), false);
            var (json, err) = await Supa.RpcAsync("join_request_profile", new { p_request = req });
            if (err != null)
            {
                Msg(_memberMsg, MissingRpc(err) ? Tr("El servidor aún no tiene el perfil de los solicitantes (falta perfil-solicitante.sql).") : Tr("Error: ") + err, true);
                return;
            }
            Msg(_memberMsg, "", false);
            JsonElement p;
            try { p = JsonDocument.Parse(json).RootElement; } catch { return; }
            string decision = ApplicantProfileWindow(p);
            if (decision == "ok" || decision == "no")
            {
                // (la misma fila sigue elegida)
                int k = _joinIds.IndexOf(req);
                if (k >= 0) _joinList.SelectRow(k);
                if (decision == "ok") ApproveJoin(); else RejectJoin();
            }
        }

        // la ventana; devuelve «ok» (aceptar), «no» (rechazar) o null
        string ApplicantProfileWindow(JsonElement p)
        {
            string user = Str(p, "username"); if (user.Length == 0) user = "—";
            using var f = new Form
            {
                Text = Tr("Perfil del solicitante"), BackColor = Theme.Bg, ForeColor = Theme.Text, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, KeyPreview = true, Font = Theme.Font(9.5f), FormBorderStyle = FormBorderStyle.FixedDialog,
            };
            DialogKit.Dress(f, false);
            int W = Theme.Px(620);
            f.ClientSize = new Size(W + Theme.Px(40), 200);
            var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, BackColor = Theme.Bg, Padding = new Padding(20, 14, 20, 16) };
            Label L(string t, float size = 9.5f, FontStyle st = FontStyle.Regular, Color? col = null, int top = 0) =>
                new Label { Text = t, AutoSize = true, MaximumSize = new Size(W, 0), Font = Theme.Font(size, st), ForeColor = col ?? Theme.Text, Margin = new Padding(0, top, 0, 2), UseMnemonic = false };
            string Date(string iso) => DateTime.TryParse(iso, out var d) ? d.ToLocalTime().ToString("d", I18n.Culture) : "—";

            // cabecera: foto, nombre, rango, carné y títulos
            var head = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 8) };
            var photo = new PictureBox { Size = new Size(Theme.Px(84), Theme.Px(105)), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Surface, Margin = new Padding(0, 0, 14, 0) };
            try
            {
                string ph = Str(p, "photo"); int c = ph.IndexOf(',');
                if (c > 0) photo.Image = Image.FromStream(new MemoryStream(Convert.FromBase64String(ph.Substring(c + 1))));
            }
            catch { }
            head.Controls.Add(photo, 0, 0);
            var who = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Bg };
            double km = Num(p, "km");
            who.Controls.Add(L(user, 14f, FontStyle.Bold));
            who.Controls.Add(L(Tr(RankTiers[RankTierIndex(km)].name), 10f, FontStyle.Bold, Theme.AccentHi));
            string cert = "";
            if (p.TryGetProperty("cert", out var cb) && cb.ValueKind == JsonValueKind.Object)
            {
                string dl = Str(cb, "d"), jf = Str(cb, "j"), su = Str(cb, "s");
                var parts = new List<string>();
                if (dl.Length > 0) parts.Add("🎓 " + string.Format(Tr("Maquinista {0}"), dl));
                if (jf == "titulo") parts.Add("👮 " + Tr("Jefe de circulación"));
                else if (jf == "practicas") parts.Add("👮 " + Tr("Jefe de circulación en prácticas"));
                if (parts.Count > 0) cert = string.Join("  ·  ", parts) + (su.Length > 0 ? "  (" + Tr("suspendido") + ")" : "");
            }
            if (cert.Length > 0) who.Controls.Add(L(cert, 9.5f, FontStyle.Regular, Carne.Gold));
            string lic = p.TryGetProperty("license", out var lj) && lj.ValueKind == JsonValueKind.Object
                ? string.Format(Tr("Carné: {0} puntos"), (int)Num(lj, "points")) + (Str(lj, "suspended_until").Length > 0 && DateTime.TryParse(Str(lj, "suspended_until"), out var su2) && su2 > DateTime.UtcNow ? "  ·  " + Tr("retirado") : "")
                : string.Format(Tr("Carné: {0} puntos"), 15);
            who.Controls.Add(L(lic));
            who.Controls.Add(L(string.Format(Tr("En SelectOR desde el {0}  ·  último servicio: {1}"), Date(Str(p, "since")), Str(p, "last_service").Length > 0 ? Date(Str(p, "last_service")) : Tr("ninguno")), 9f, FontStyle.Regular, Theme.Subtle));
            head.Controls.Add(who, 1, 0);
            stack.Controls.Add(head);

            // cuánto ha conducido
            double hours = Num(p, "hours"), trips = Num(p, "trips"), avg = hours > 0 ? km / hours : 0;
            stack.Controls.Add(L(Tr("CONDUCCIÓN"), 8.5f, FontStyle.Bold, Theme.Subtle, 6));
            stack.Controls.Add(L(string.Format(Tr("{0} km  ·  {1} viajes  ·  {2} h  ·  {3} km/h de media  ·  {4} viajeros"),
                km.ToString("N0", EsEs), trips.ToString("N0", EsEs), hours.ToString("N1", EsEs), avg.ToString("N0", EsEs), Num(p, "pax").ToString("N0", EsEs)), 10f, FontStyle.Bold));
            int inval = (int)Num(p, "invalid"), room = (int)Num(p, "room_services");
            stack.Controls.Add(L(string.Format(Tr("{0} rutas distintas  ·  {1} trenes distintos  ·  {2} servicios en la sala compartida"), (int)Num(p, "routes"), (int)Num(p, "trains"), room)
                                 + (inval > 0 ? "  ·  " + string.Format(Tr("{0} viajes no válidos"), inval) : ""), 9f, FontStyle.Regular, Theme.Subtle));

            void Top(string cap, string key)
            {
                if (!p.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return;
                stack.Controls.Add(L(cap, 8.5f, FontStyle.Bold, Theme.Subtle, 8));
                foreach (var e in arr.EnumerateArray())
                    stack.Controls.Add(L("•  " + Str(e, "name") + "  —  " + string.Format(Tr("{0} veces · {1} km"), (int)Num(e, "n"), Num(e, "km").ToString("N0", EsEs))));
            }
            Top(Tr("TRENES QUE MÁS USA"), "top_trains");
            Top(Tr("RUTAS QUE MÁS RECORRE"), "top_routes");

            // infracciones de los últimos 90 días
            stack.Controls.Add(L(Tr("ÚLTIMOS 90 DÍAS"), 8.5f, FontStyle.Bold, Theme.Subtle, 8));
            int g = 0, lv = 0, sa = 0;
            if (p.TryGetProperty("infractions", out var inf) && inf.ValueKind == JsonValueKind.Object) { g = (int)Num(inf, "graves"); lv = (int)Num(inf, "leves"); sa = (int)Num(inf, "sanciones"); }
            stack.Controls.Add(g + lv + sa == 0
                ? L("✅  " + Tr("Sin infracciones ni sanciones."), 9.5f, FontStyle.Regular, Color.FromArgb(129, 199, 132))
                : L("⚠  " + string.Format(Tr("{0} infracciones graves  ·  {1} leves  ·  {2} sanciones del administrador"), g, lv, sa), 9.5f, FontStyle.Regular, g + sa > 0 ? Carne.Red : Carne.Gold));

            // empresas y su mensaje
            var cos = p.TryGetProperty("companies", out var ca) && ca.ValueKind == JsonValueKind.Array ? ca.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).ToList() : new List<string>();
            stack.Controls.Add(L(Tr("EMPRESAS"), 8.5f, FontStyle.Bold, Theme.Subtle, 8));
            stack.Controls.Add(L(cos.Count == 0 ? Tr("No está en ninguna empresa.") : string.Format(Tr("Ya está en: {0}"), string.Join(", ", cos))));
            string note = Str(p, "note");
            if (note.Length > 0)
            {
                stack.Controls.Add(L(Tr("SU MENSAJE"), 8.5f, FontStyle.Bold, Theme.Subtle, 8));
                stack.Controls.Add(L("«" + note + "»", 9.5f, FontStyle.Italic));
            }

            string result = null;
            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Anchor = AnchorStyles.Right, Margin = new Padding(0, 14, 0, 0) };
            var acc = EmpButton(Tr("Aceptar"), primary: true); acc.Width = Theme.Px(140); acc.Click += (s, e) => { result = "ok"; f.Close(); };
            var rej = EmpButton(Tr("Rechazar")); rej.Width = Theme.Px(140); rej.Margin = new Padding(8, 10, 0, 2); rej.Click += (s, e) => { result = "no"; f.Close(); };
            var cl = EmpButton(Tr("Cerrar")); cl.Width = Theme.Px(120); cl.Margin = new Padding(8, 10, 0, 2); cl.Click += (s, e) => f.Close();
            btns.Controls.Add(acc); btns.Controls.Add(rej); btns.Controls.Add(cl);
            stack.Controls.Add(btns);
            f.Controls.Add(stack);
            int crownH = DialogKit.Crown(f, Tr("Perfil del solicitante"), string.Format(Tr("{0} pide unirse a {1}"), user, _empSel?.Name ?? ""), "🪪").Height + 3;
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
            f.Load += (s, e) => { var ps = stack.GetPreferredSize(new Size(f.ClientSize.Width, 0)); f.ClientSize = new Size(Math.Max(f.ClientSize.Width, ps.Width), ps.Height + 4 + crownH); };
            _applicantOpen = f;
            try { f.ShowDialog(this); } finally { _applicantOpen = null; }
            return result;
        }
        Form _applicantOpen;   // para las pruebas
    }
}
