// HUD · ¿estoy conectado de verdad al servidor de multijugador? (fila entre los viajeros y la megafonía)
//  · Con cualquier servidor: si Open Rails tiene abierta la conexión con él (tabla de conexiones de Windows; sin contar
//    la del piloto de desvíos, que va al mismo sitio).
//  · En los servidores de SelectOR, además, el propio servidor dice si mi Open Rails está dentro de la sala (y si soy
//    el despachador): se lo pregunta el piloto cada 3 s («WHO», RoomGate/Gate.cs).
//  · Si protejo yo la sala libre, la contraseña a la derecha, para pasársela a quien quiera unirse.

using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        DateTime _netAt = DateTime.MinValue, _netInAt = DateTime.MinValue;
        (int level, string text, string extra) _netLast;
        IPAddress[] _netIps; string _netIpsFor;

        // level: 0 sin fila (no es multijugador) · 1 en línea · 2 entrando · 3 sin conexión
        (int level, string text, string extra) HudNetStatus()
        {
            if (!_mpClientRun) return (0, null, null);
            if ((DateTime.UtcNow - _netAt).TotalSeconds < 2) return _netLast;
            _netAt = DateTime.UtcNow;
            string host = _roomJoined != null ? _roomHost : _settings?.Multiplayer_Host;
            int port = _roomJoined != null ? _roomPort : _settings?.Multiplayer_Port ?? 0;
            bool tcp = OrConnectedTo(host, port);
            string extra = null;
            var p = _pilot;
            string pres = p != null && p.Ready && (DateTime.UtcNow - p.PresenceAt).TotalSeconds < 10 ? p.Presence : null;
            (int, string, string) r;
            if (pres == "in" || (pres == null && tcp && _scenarioReady))
            {
                _netInAt = DateTime.UtcNow;
                r = (1, Tr("En línea") + (pres == "in" && p.IsDispatcher ? " · " + Tr("despachador") : ""), extra);   // (sin el nombre de la sala: no cabe)
            }
            else if (pres == "wait" || (!_scenarioReady && (tcp || _netInAt == DateTime.MinValue)))
                r = (2, Tr("Conectando con el servidor…"), extra);
            else if (pres == "out" && tcp && _netInAt == DateTime.MinValue)
                r = (2, Tr("Entrando en la sala…"), extra);
            else
                r = (3, pres == "out" ? Tr("Fuera de la sala") : Tr("Sin conexión con el servidor"), null);
            _netLast = r;
            return r;
        }

        // ¿Hay una conexión establecida con ese servidor que no sea la del piloto?
        bool OrConnectedTo(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host) || port <= 0) return false;
            try
            {
                if (_netIpsFor != host)
                {
                    _netIpsFor = host;
                    _netIps = IPAddress.TryParse(host.Trim(), out var ip) ? new[] { ip } : Dns.GetHostAddresses(host.Trim());
                }
                int mine = _pilot?.LocalPort ?? 0;
                static IPAddress N(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
                var want = _netIps.Select(N).ToArray();
                foreach (var c in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
                    if (c.State == TcpState.Established && c.RemoteEndPoint.Port == port && c.LocalEndPoint.Port != mine
                        && want.Any(a => a.Equals(N(c.RemoteEndPoint.Address))))
                        return true;
            }
            catch { }
            return false;
        }
    }
}
