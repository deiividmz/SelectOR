// Multijugador: ya no hay tarjeta «Tus datos» en la página. Al pulsar «Alojar partida» o «Unirme a un servidor» (o
// «Unirme» en un servidor de la lista, o al entrar en una sala de SelectOR) se abre esta ventana con el nombre del
// jugador, la IP y el puerto, y el botón Conectar. Al alojar, la IP es 127.0.0.1 (este equipo) salvo que se cambie.
// En las salas de SelectOR solo el nombre (su IP no se enseña).

using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        // El nombre del jugador en el multijugador es el nombre de SelectOR (1.2.65): hay que haber iniciado sesión, y si
        // el nombre no vale para Open Rails (cuentas de antes: espacios, más de 10 caracteres…), se pide cambiarlo (es el
        // mismo nombre en todo SelectOR). Deja _txtMPUser con él. null = no se puede jugar.
        async Task<string> MpPlayerName(string title)
        {
            if (!Supa.IsLoggedIn)
            {
                ThemedBox.Show(this, Tr("Para jugar en multijugador inicia sesión en SelectOR (en Empresas): juegas con tu nombre de usuario de SelectOR."),
                               title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            string n = (Supa.Username ?? "").Trim();
            if (!MPUserValid(n))
            {
                var r = ThemedBox.Show(this,
                    string.Format(Tr("Tu nombre de SelectOR «{0}» no vale como nombre de jugador en Open Rails: tiene que tener de 4 a 10 caracteres, sin espacios ni ' \" -, y no empezar por un número. Cámbialo para jugar en multijugador; será tu nombre en todo SelectOR (tu acceso no cambia)."), n),
                    title, MessageBoxButtons.OKCancel, MessageBoxIcon.Information, new[] { Tr("Cambiar el nombre…"), Tr("Cancelar") });
                if (r != DialogResult.OK) return null;
                n = await ChangeMaquinistaName(null);
                if (n == null || !MPUserValid(n)) return null;
            }
            if (_txtMPUser != null && _txtMPUser.Text != n) _txtMPUser.Text = n;
            return n;
        }

        // true = Conectar (los campos quedan con lo escrito); false = cancelado.
        bool MpConnectDialog(bool host, string roomName = null)
        {
            bool room = roomName != null;
            string title = room ? roomName : host ? Tr("Alojar partida") : Tr("Unirme a un servidor");
            string sub = room ? Tr("Entras en la sala con tu nombre de jugador.")
                       : host ? Tr("Tu PC es el servidor de la partida.") : Tr("La IP y el puerto del servidor al que te unes.");
            using var dlg = new FormDialog(title, Tr("Conectar"), 500, room || !host ? "🔗" : "🖥", sub);
            // el nombre del jugador es el de SelectOR (MpPlayerName, antes de abrir esta ventana): solo se enseña
            var user = dlg.AddText("user", Tr("Nombre del jugador"), (_txtMPUser?.Text ?? "").Trim(), "");
            user.Box.ReadOnly = true; user.Box.TabStop = false;
            if (!room)
            {
                string ip = host ? (string.IsNullOrWhiteSpace(_mpHostIp) ? "127.0.0.1" : _mpHostIp) : (_rbClient.Checked ? _txtMPHost.Text.Trim() : _mpClientHost ?? "");
                dlg.AddText("ip", host ? Tr("IP") : Tr("IP del servidor"), ip, host ? "127.0.0.1" : Tr("Por ejemplo: 192.168.1.20"));
                dlg.AddText("port", Tr("Puerto"), (_txtMPPort?.Text ?? "30000").Trim(), "30000");
            }
            dlg.AddInfo(Tr("Juegas con tu nombre de SelectOR. Se cambia en Empresas → Mi cuenta → «Editar nombre»."));
            dlg.Shown += (s, e) => { if (!room) (dlg.Field(host ? "port" : "ip") ?? user.Box).Focus(); };
            dlg.Validate = f =>
            {
                if (!room)
                {
                    if (!host && f.Get("ip").Length == 0) return Task.FromResult(Tr("Escribe la IP del servidor al que te quieres unir."));
                    if (!int.TryParse(f.Get("port"), out int p) || p < 1 || p > 65535) return Task.FromResult(Tr("El puerto debe ser un número entre 1 y 65535."));
                }
                return Task.FromResult<string>(null);
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return false;

            if (room) return true;
            _txtMPPort.Text = dlg.Get("port");
            string ipOk = dlg.Get("ip");
            if (host)
            {
                _mpHostIp = ipOk.Length > 0 ? ipOk : "127.0.0.1";
                if (!_rbServer.Checked) { _mpMode?.Select(0, raise: false); _rbServer.Checked = true; }
                ApplyMPMode();
            }
            else
            {
                _mpClientHost = ipOk;
                if (!_rbClient.Checked) { _mpMode?.Select(1, raise: false); _rbClient.Checked = true; }
                _txtMPHost.Text = ipOk;
            }
            UpdateStatus();
            return true;
        }
    }
}
