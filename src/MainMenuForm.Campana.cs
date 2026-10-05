// Campanita de notificaciones junto al nombre del perfil (Empresas). El número rojo son los avisos sin leer
// (notificaciones-campana.sql); al pulsarla se despliega la lista de avisos de los últimos 60 días y se dan
// por leídos. Un aviso pulsado (aquí o en su ventana emergente) lleva a su sección y queda leído.

using System;
using System.Drawing;
using System.Linq;
using System.Threading;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        BellButton _bell;
        NotificationsPanel _bellPanel;
        bool _bellOff;            // el servidor aún no tiene notificaciones-campana.sql
        int _bellBusy;
        DateTime _bellClosedAt;   // la ventana se cierra al perder el foco: un clic en la campanita justo después no la reabre

        BellButton MakeBell()
        {
            _bell = new BellButton { Dock = System.Windows.Forms.DockStyle.Right, Width = Theme.Px(34), BackColor = Theme.Surface };
            new System.Windows.Forms.ToolTip().SetToolTip(_bell, Tr("Notificaciones"));
            _bell.Click += (s, e) => ToggleNotifPanel();
            return _bell;
        }

        // El número de avisos sin leer (tras cada consulta de avisos y al entrar).
        async void RefreshBellCount()
        {
            if (_bell == null || _bell.IsDisposed || _bellOff || !Supa.IsLoggedIn || !Supa.IsConfigured) return;
            if (Interlocked.Exchange(ref _bellBusy, 1) == 1) return;
            try
            {
                var (json, err) = await Supa.RpcAsync("notifications_unread_count", new { });
                if (err != null) { if (NoLeagueOnServer(err)) { _bellOff = true; _bell.Count = 0; } return; }
                if (int.TryParse((json ?? "").Trim().Trim('"'), out int n) && !_bell.IsDisposed) _bell.Count = n;
            }
            catch { }
            finally { Interlocked.Exchange(ref _bellBusy, 0); }
        }

        async void MarkNotifRead(string id)
        {
            if (_bellOff || string.IsNullOrEmpty(id) || !Supa.IsLoggedIn) return;
            try { await Supa.RpcAsync("mark_notifications_read", new { p_ids = new[] { id } }); } catch { }
            RefreshBellCount();
        }

        async void ToggleNotifPanel()
        {
            if (_bellPanel != null && !_bellPanel.IsDisposed) { _bellPanel.Close(); return; }
            if ((DateTime.UtcNow - _bellClosedAt).TotalMilliseconds < 300) return;   // ese clic ya la ha cerrado
            if (!Supa.IsLoggedIn) return;
            var pnl = new NotificationsPanel { Title = Tr("Notificaciones"), ResolvedText = Tr("Resuelta") };
            _bellPanel = pnl;
            pnl.FormClosed += (s, e) => { if (_bellPanel == pnl) _bellPanel = null; _bellClosedAt = DateTime.UtcNow; };
            // La tarjeta del perfil está abajo a la izquierda: la ventana sale encima de la campanita, hacia la derecha.
            var sp = _bell.PointToScreen(Point.Empty);
            var wa = System.Windows.Forms.Screen.FromControl(this).WorkingArea;
            int x = Math.Min(Math.Max(wa.Left + 4, sp.X - Theme.Px(24)), wa.Right - pnl.Width - 4);
            int y = sp.Y - pnl.Height - Theme.Px(6);
            if (y < wa.Top + 4) y = Math.Min(sp.Y + _bell.Height + Theme.Px(6), wa.Bottom - pnl.Height - 4);
            pnl.Location = new Point(x, y);
            pnl.ShowState(Tr("Cargando…"));
            pnl.Show(this);

            var (json, err) = await Supa.RpcAsync("notification_history", new { p_limit = 60 });
            if (pnl.IsDisposed) return;
            if (err != null)
            {
                pnl.ShowState(NoLeagueOnServer(err) ? Tr("El servidor aún no tiene el historial de avisos (falta notificaciones-campana.sql).")
                                                   : Tr("No se han podido cargar los avisos."));
                return;
            }
            var rows = ParseNotifs(json);
            var items = rows.Select(r =>
            {
                var v = DescribeNotif(r);
                return new NotifItem
                {
                    Icon = v.Icon, Title = v.Title, Body = v.Body, Accent = v.Color, Unread = !r.IsRead, Resolved = r.Resolved,
                    Foot = (string.IsNullOrWhiteSpace(r.Company) ? "" : r.Company + " · ") + AgoText(r.At),
                    Open = () => OpenEmpresasAt(v.Cid, v.Sub, v.BuyTab)
                };
            }).ToList();
            int unread = items.Count(i => i.Unread);
            if (items.Count == 0) pnl.ShowState(Tr("No tienes avisos de los últimos 60 días."));
            else pnl.SetItems(items, unread > 0 ? string.Format(Tr("{0} sin leer"), unread) : Tr("Todo leído"));
            // Abrir la campanita es leerlos: el número rojo se apaga (y ya no saltarán como ventana emergente).
            if (unread > 0) { try { await Supa.RpcAsync("mark_notifications_read", new { p_ids = (string[])null }); } catch { } }
            if (_bell != null && !_bell.IsDisposed) _bell.Count = 0;
        }
    }
}
