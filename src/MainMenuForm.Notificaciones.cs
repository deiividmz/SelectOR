// Avisos al usuario (notificaciones.sql): el servidor guarda un aviso cada vez que pasa algo que te
// afecta (te aceptan o rechazan en una empresa, se resuelve una solicitud de compra, te cambian el
// rol…) y aquí se enseñan como ventanas emergentes abajo a la derecha. Llegan al momento por
// Supabase Realtime y, por si acaso, se consultan cada 90 s; mientras conduces se guardan y salen al
// volver del simulador. Una vez enseñado, el aviso queda como visto.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        sealed class NotifRow
        {
            public string Id, Kind, CompanyId, Company, Actor;
            public JsonElement Data;
            public DateTime At;
        }

        Timer _notifTimer;
        bool _notifBusy, _notifOff;   // _notifOff: falta notificaciones.sql en el servidor
        readonly List<NotificationToast> _toasts = new();
        readonly Queue<NotificationToast> _toastQueue = new();
        const int MaxToasts = 4;

        void StartNotifications()
        {
            if (_notifTimer != null || !Supa.IsLoggedIn) return;
            _notifTimer = new Timer { Interval = 90000 };
            _notifTimer.Tick += (s, e) => NotifyCheck();
            _notifTimer.Start();
            NotifySoon(2500);   // al entrar: los que llegaron mientras no estabas
            StartSessionCheck();   // una sesión por conexión: avisa si el servidor cierra esta
        }

        void StopNotifications()
        {
            StopSessionCheck();
            try { _notifTimer?.Stop(); _notifTimer?.Dispose(); } catch { }
            _notifTimer = null;
            _toastQueue.Clear();
            foreach (var t in _toasts.ToArray()) { try { t.Close(); } catch { } }
            _toasts.Clear();
        }

        void NotifySoon(int ms = 800)
        {
            var t = new Timer { Interval = ms };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); NotifyCheck(); };
            t.Start();
        }

        bool NotifyDriving => _kmTimer != null;   // Open Rails abierto: los avisos esperan a que vuelvas

        async void NotifyCheck()
        {
            if (_notifBusy || _notifOff || !Supa.IsLoggedIn || !Supa.IsConfigured || NotifyDriving || IsDisposed) return;
            _notifBusy = true;
            try
            {
                var (json, err) = await Supa.RpcAsync("my_notifications", new { p_limit = 20 });
                if (err != null) { if (NoLeagueOnServer(err)) _notifOff = true; return; }
                var rows = ParseNotifs(json);
                if (rows.Count == 0) return;
                var ids = new List<string>();
                foreach (var r in rows) ids.Add(r.Id);
                await Supa.RpcAsync("mark_notifications_seen", new { p_ids = ids.ToArray() });

                bool membership = false;
                int shown = 0;
                foreach (var r in rows)
                {
                    if (r.Kind is "join_approved" or "member_added" or "member_removed" or "role_changed" or "company_approved" or "company_deleted") membership = true;
                    if (shown == MaxToasts - 1 && rows.Count > MaxToasts)
                    {
                        // Muchos de golpe: los primeros y un resumen del resto.
                        int rest = rows.Count - shown;
                        EnqueueToast(new NotificationToast("🔔", string.Format(Tr("{0} avisos más"), rest),
                            Tr("Tienes más novedades en Empresas."), "SelectOR", Color.FromArgb(96, 165, 250)) { Opened = () => OpenEmpresasAt(null, -1) });
                        break;
                    }
                    EnqueueToast(BuildToast(r));
                    shown++;
                }
                // Te han añadido, quitado o cambiado de rol: la lista de empresas y los permisos cambian.
                if (membership && _empLoaded) LoadCompanies();
            }
            catch { }
            finally { _notifBusy = false; }
        }

        static List<NotifRow> ParseNotifs(string json)
        {
            var list = new List<NotifRow>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            using var d = JsonDocument.Parse(json);
            if (d.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in d.RootElement.EnumerateArray())
            {
                var r = new NotifRow { Id = Str(e, "id"), Kind = Str(e, "kind"), CompanyId = Str(e, "company_id"), Company = Str(e, "company_name"), Actor = Str(e, "actor") };
                r.Data = e.TryGetProperty("data", out var dd) && dd.ValueKind == JsonValueKind.Object ? dd.Clone() : default;
                DateTime.TryParse(Str(e, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out r.At);
                if (!string.IsNullOrEmpty(r.Id)) list.Add(r);
            }
            return list;
        }

        static string DataStr(NotifRow r, string k) =>
            r.Data.ValueKind == JsonValueKind.Object && r.Data.TryGetProperty(k, out var v)
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetDouble().ToString("0.#", CultureInfo.CurrentCulture) : "")
                : "";

        string RoleText(string role) => (role switch
        {
            "owner" => Tr("Gerente"),
            "manager" => Tr("Gestor"),
            _ => Tr("Maquinista")
        }).ToLower(CultureInfo.CurrentCulture);

        string AgoText(DateTime utc)
        {
            if (utc == default) return "";
            var d = DateTime.UtcNow - utc;
            if (d.TotalMinutes < 1) return Tr("ahora mismo");
            if (d.TotalMinutes < 60) return string.Format(Tr("hace {0} min"), (int)d.TotalMinutes);
            if (d.TotalHours < 24) return string.Format(Tr("hace {0} h"), (int)d.TotalHours);
            return utc.ToLocalTime().ToString(I18n.English ? "dd/MM/yyyy HH:mm" : "dd-MM-yyyy HH:mm");
        }

        // Icono, color, título, texto y adónde lleva el clic, según el tipo de aviso.
        NotificationToast BuildToast(NotifRow r)
        {
            Color ok = Color.FromArgb(76, 175, 80), bad = Color.FromArgb(229, 115, 115), info = Color.FromArgb(96, 165, 250),
                  buy = Color.FromArgb(251, 146, 60), role = Color.FromArgb(167, 139, 250);
            string co = string.IsNullOrWhiteSpace(r.Company) ? Tr("tu empresa") : r.Company;
            string who = string.IsNullOrWhiteSpace(r.Actor) ? Tr("Un responsable") : r.Actor;
            string machine = DataStr(r, "machine"), route = DataStr(r, "route"), km = DataStr(r, "km");
            string routeTxt = string.IsNullOrWhiteSpace(route) ? "" : route + (km.Length > 0 ? $" ({km} km)" : "");
            string icon = "🔔", title = "", body = ""; Color c = info;
            string cid = r.CompanyId; int sub = -1, buyTab = -1;
            switch (r.Kind)
            {
                case "join_approved":
                    icon = "✅"; c = ok; title = Tr("Solicitud aceptada");
                    body = string.Format(Tr("{0} te ha aceptado en {1}. Ya eres maquinista de la empresa."), who, co); sub = 0; break;
                case "join_rejected":
                    icon = "❌"; c = bad; title = Tr("Solicitud rechazada");
                    body = string.Format(Tr("Tu solicitud para unirte a {0} ha sido rechazada."), co); cid = null; break;
                case "join_new":
                    icon = "👤"; c = info; title = Tr("Nueva solicitud de ingreso");
                    body = string.Format(Tr("{0} quiere unirse a {1}."), DataOr(r, "who", Tr("Un usuario")), co)
                         + (DataStr(r, "note").Length > 0 ? "\n«" + DataStr(r, "note") + "»" : ""); sub = 2; break;
                case "member_added":
                    icon = "🏢"; c = ok; title = Tr("Te han añadido a una empresa");
                    body = string.Format(Tr("{0} te ha añadido a {1} como {2}."), who, co, RoleText(DataStr(r, "role"))); sub = 0; break;
                case "role_changed":
                    icon = "🎖"; c = role; title = Tr("Tienes un rol nuevo");
                    body = string.Format(Tr("Ahora eres {0} en {1}."), RoleText(DataStr(r, "role")), co); sub = 0; break;
                case "member_removed":
                    icon = "🚪"; c = bad; title = Tr("Ya no perteneces a una empresa");
                    body = string.Format(Tr("{0} te ha quitado de {1}."), who, co); cid = null; break;
                case "company_deleted":
                    icon = "🗑"; c = bad; title = Tr("Empresa eliminada");
                    body = string.Format(Tr("La empresa {0} ha sido eliminada."), co); cid = null; break;
                case "company_approved":
                    icon = "🎉"; c = ok; title = Tr("Empresa aprobada");
                    body = string.Format(Tr("Tu solicitud de empresa «{0}» ha sido aprobada. Ya eres su gerente."), DataOr(r, "name", co)); sub = 0; break;
                case "company_rejected":
                    icon = "❌"; c = bad; title = Tr("Solicitud de empresa rechazada");
                    body = string.Format(Tr("Tu solicitud de empresa «{0}» ha sido rechazada."), DataOr(r, "name", co)); cid = null; break;
                case "purchase_new":
                    icon = "🛒"; c = buy; title = Tr("Nueva solicitud de compra");
                    body = string.Format(Tr("{0} pide que {1} compre {2}."), DataOr(r, "who", Tr("Un maquinista")), co, machine)
                         + (DataStr(r, "note").Length > 0 ? "\n«" + DataStr(r, "note") + "»" : ""); sub = 10; buyTab = 1; break;
                case "purchase_bought":
                    icon = "🚆"; c = ok; title = Tr("Solicitud de compra aceptada");
                    body = string.Format(Tr("{0} ha comprado {1}, como pediste."), co, machine); sub = 9; break;
                case "purchase_rented":
                    icon = "🚆"; c = ok; title = Tr("Solicitud de compra aceptada");
                    body = string.Format(Tr("{0} ha alquilado {1}, como pediste."), co, machine); sub = 9; break;
                case "purchase_rejected":
                    icon = "❌"; c = bad; title = Tr("Solicitud de compra rechazada");
                    body = string.Format(Tr("{0} ha decidido no comprar {1}."), co, machine); sub = 10; break;
                case "service_validated":
                    icon = "✅"; c = ok; title = Tr("Servicio validado");
                    body = string.Format(Tr("Tu servicio {0} en {1} ya es válido y se ha pagado."), routeTxt, co); sub = 0; break;
                case "service_dismissed":
                    icon = "⚠"; c = buy; title = Tr("Servicio descartado");
                    body = string.Format(Tr("Tu servicio {0} en {1} no se ha dado por válido."), routeTxt, co); sub = 0; break;
                case "service_deleted":
                    icon = "🗑"; c = bad; title = Tr("Servicio eliminado");
                    body = string.Format(Tr("Un administrador ha eliminado tu servicio {0} en {1}."), routeTxt, co); sub = 0; break;
                case "service_annulled":
                    icon = "⛔"; c = bad; title = Tr("Servicio anulado");
                    body = string.Format(Tr("El superadministrador ha anulado tu servicio {0} en {1}."), routeTxt, co)
                         + (DataStr(r, "reason").Length > 0 ? "\n" + Tr("Motivo: ") + DataStr(r, "reason") : ""); sub = 0; break;
                // Préstamos
                case "loan_approved":
                    icon = "🏦"; c = ok; title = Tr("Préstamo concedido");
                    body = string.Format(Tr("El banco ha concedido a {0} un préstamo de {1} € a {2} meses. Cuota: {3} €."), co,
                               Money(r, "amount"), DataStr(r, "months"), Money(r, "payment")); sub = 1; break;
                case "loan_rejected":
                    icon = "🏦"; c = bad; title = Tr("Préstamo denegado");
                    body = string.Format(Tr("El banco ha denegado el préstamo de {0} € que pidió {1}."), Money(r, "amount"), co)
                         + (DataStr(r, "note").Length > 0 ? "\n«" + DataStr(r, "note") + "»" : ""); sub = 1; break;
                case "loan_late":
                    icon = "⚠"; c = bad; title = Tr("Cuota del préstamo impagada");
                    body = string.Format(Tr("{0} no tiene saldo para la cuota {1}/{2} ({3} €). Se cobrará, con un recargo de {4} €, en cuanto haya saldo."), co,
                               DataStr(r, "n"), DataStr(r, "months"), Money(r, "payment"), Money(r, "fee")); sub = 1; break;
                case "loan_paid":
                    icon = "🎉"; c = ok; title = Tr("Préstamo devuelto");
                    body = string.Format(Tr("{0} ha terminado de devolver un préstamo."), co); sub = 1; break;
                // Normas de la empresa
                case "rule_new":
                    icon = "📜"; c = info; title = Tr("Norma nueva en la empresa");
                    body = string.Format(Tr("{0} ha publicado una norma en {1}: «{2}»."), who, co, DataStr(r, "title")); sub = NormasSubtab; break;
                // Chat de empresa
                case "chat_muted":
                    icon = "💬"; c = bad; title = Tr("Sin permiso para escribir en el chat");
                    body = string.Format(Tr("{0} te ha retirado el permiso para escribir en el chat de {1}. Puedes seguir leyéndolo."), who, co); sub = 12; break;
                case "chat_unmuted":
                    icon = "💬"; c = ok; title = Tr("Ya puedes escribir en el chat");
                    body = string.Format(Tr("{0} te ha devuelto el permiso para escribir en el chat de {1}."), who, co); sub = 12; break;
                // Carné por puntos
                case "infraction_new":
                    icon = "🛡"; c = buy; title = Tr("Infracción pendiente de revisión");
                    body = string.Format(Tr("{0}: {1} en {2}. Confírmala o anúlala en Revisión."),
                               DataOr(r, "who", Tr("Un maquinista")), Carne.Label(DataStr(r, "code")), DataOr(r, "route", co)); sub = 6; break;
                case "infraction_confirmed":
                    icon = "⚠"; c = bad; title = Tr("Infracción confirmada");
                    body = string.Format(Tr("{0} en {1}: {2} en tu carné."), Carne.Label(DataStr(r, "code")), DataOr(r, "route", co),
                               Carne.PointsText(int.TryParse(DataStr(r, "points"), out var ip) ? ip : 0)); sub = 5; break;
                case "infraction_annulled":
                    icon = "✅"; c = ok; title = Tr("Infracción anulada");
                    body = string.Format(Tr("{0} en {1} no te resta puntos."), Carne.Label(DataStr(r, "code")), DataOr(r, "route", co)); sub = 5; break;
                case "license_suspended":
                    icon = "⛔"; c = bad; title = Tr("Carné suspendido");
                    body = DateTime.TryParse(DataStr(r, "until"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until)
                        ? string.Format(Tr("Te has quedado sin puntos: no puedes ponerte de servicio hasta el {0}. Después vuelves con 8 puntos."), Carne.FmtLocal(until))
                        : Tr("Te has quedado sin puntos: el carné queda suspendido 7 días. Después vuelves con 8 puntos.");
                    sub = 5; break;
                default:
                    title = Tr("Aviso"); body = co; break;
            }
            body = body.Replace("  ", " ");
            var t = new NotificationToast(icon, title, body, "SelectOR · " + Tr("Empresas") + " · " + AgoText(r.At), c);
            t.Opened = () => OpenEmpresasAt(cid, sub, buyTab);
            return t;
        }

        static string DataOr(NotifRow r, string k, string def) { var v = DataStr(r, k); return string.IsNullOrWhiteSpace(v) ? def : v; }

        // Importe del aviso (viene como número en texto) con separador de miles.
        static string Money(NotifRow r, string k)
        {
            if (r.Data.ValueKind != JsonValueKind.Object || !r.Data.TryGetProperty(k, out var v)) return "";
            double d;
            if (v.ValueKind == JsonValueKind.Number) d = v.GetDouble();
            else if (!double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return v.ToString();
            return d.ToString(Math.Abs(d % 1) < 0.005 ? "N0" : "N2", I18n.English ? CultureInfo.GetCultureInfo("en-GB") : CultureInfo.GetCultureInfo("es-ES"));
        }

        // ---- Pila de avisos: abajo a la derecha, el más nuevo encima; como mucho 4 a la vez ----
        void EnqueueToast(NotificationToast t)
        {
            NotifySound.Play();   // un lote de avisos suena una sola vez
            if (_toasts.Count >= MaxToasts) { _toastQueue.Enqueue(t); return; }
            ShowToast(t);
        }

        void ShowToast(NotificationToast t)
        {
            t.FormClosed += (s, e) =>
            {
                _toasts.Remove(t);
                if (_toastQueue.Count > 0 && !IsDisposed) ShowToast(_toastQueue.Dequeue());
                LayoutToasts();
            };
            _toasts.Add(t);
            LayoutToasts();
            t.Show();
        }

        void LayoutToasts()
        {
            Rectangle wa;
            try { wa = WindowState == FormWindowState.Minimized ? Screen.PrimaryScreen.WorkingArea : Screen.FromControl(this).WorkingArea; }
            catch { wa = Screen.PrimaryScreen.WorkingArea; }
            int y = wa.Bottom - 16;
            foreach (var t in _toasts)
            {
                y -= t.Height;
                t.Location = new Point(wa.Right - t.Width - 16, y);
                y -= 10;
            }
        }

        // Clic en un aviso: SelectOR al frente, en Empresas, con esa empresa y en la sección que toca.
        void OpenEmpresasAt(string companyId, int subtab, int buyTab = -1)
        {
            try
            {
                if (WindowState == FormWindowState.Minimized) WindowState = _preLaunchState == FormWindowState.Minimized ? FormWindowState.Normal : _preLaunchState;
                Activate();
                ShowPage(PageEmpresas);
                // El desplegable tiene los nombres; la empresa se busca en la lista, en el mismo orden.
                if (!string.IsNullOrEmpty(companyId) && _empCoCombo != null && _empSel?.Id != companyId)
                {
                    int ix = _empCompanies.FindIndex(ec => ec.Id == companyId);
                    if (ix >= 0 && ix < _empCoCombo.Items.Count) _empCoCombo.SelectedIndex = ix;
                }
                if (subtab >= 0 && _empSubtabs != null && subtab < _empSubtabs.Length && _empSubtabs[subtab] != null && _empSubtabs[subtab].Visible)
                {
                    ShowSubtab(subtab);
                    if (buyTab >= 0) ShowBuyTab(buyTab);
                }
            }
            catch { }
        }
    }
}
