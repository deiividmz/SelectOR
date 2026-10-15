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
        // true = Conectar (los campos quedan con lo escrito); false = cancelado.
        bool MpConnectDialog(bool host, string roomName = null)
        {
            bool room = roomName != null;
            string title = room ? roomName : host ? Tr("Alojar partida") : Tr("Unirme a un servidor");
            string sub = room ? Tr("Servidor SelectOR: entras con tu nombre de jugador.")
                       : host ? Tr("Tu PC es el servidor de la partida.") : Tr("La IP y el puerto del servidor al que te unes.");
            using var dlg = new FormDialog(title, Tr("Conectar"), 500, room || !host ? "🔗" : "🖥", sub);
            var user = dlg.AddText("user", Tr("Nombre del jugador"), (_txtMPUser?.Text ?? "").Trim(), Tr("De 4 a 10 caracteres"));
            user.Box.MaxLength = 10;
            if (!room)
            {
                string ip = host ? (string.IsNullOrWhiteSpace(_mpHostIp) ? "127.0.0.1" : _mpHostIp) : (_rbClient.Checked ? _txtMPHost.Text.Trim() : _mpClientHost ?? "");
                dlg.AddText("ip", host ? Tr("IP") : Tr("IP del servidor"), ip, host ? "127.0.0.1" : Tr("Por ejemplo: 192.168.1.20"));
                dlg.AddText("port", Tr("Puerto"), (_txtMPPort?.Text ?? "30000").Trim(), "30000");
            }
            dlg.AddInfo(Tr("Nombre del jugador: de 4 a 10 caracteres, sin espacios ni ' \" -, y sin empezar por un número (lo exige Open Rails)."));
            dlg.Shown += (s, e) => { if (user.Box.Text.Length == 0 || room) user.Box.Focus(); else (dlg.Field(host ? "port" : "ip") ?? user.Box).Focus(); };
            dlg.Validate = f =>
            {
                string u = MPUserClean(f.Get("user"));
                if (!MPUserValid(u)) return Task.FromResult(Tr("El usuario de Multijugador debe tener de 4 a 10 caracteres, sin espacios ni ' \" -, y no puede empezar por un número."));
                if (!room)
                {
                    if (!host && f.Get("ip").Length == 0) return Task.FromResult(Tr("Escribe la IP del servidor al que te quieres unir."));
                    if (!int.TryParse(f.Get("port"), out int p) || p < 1 || p > 65535) return Task.FromResult(Tr("El puerto debe ser un número entre 1 y 65535."));
                }
                return Task.FromResult<string>(null);
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return false;

            _txtMPUser.Text = MPUserClean(dlg.Get("user"));
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
