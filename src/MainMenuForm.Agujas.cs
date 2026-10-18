// Conexión aparte con el servidor de la sala (SwitchPilot), mientras Open Rails está dentro de ella. Ya NO pide
// desvíos: los desvíos según el itinerario de la hoja de ruta se quitaron en la 1.2.63 (copia en «Proyecto Menú OR\
// SelectOR Desvíos por itinerario (aparcado)»). Sigue haciendo falta para:
//  · el HUD: si Open Rails está dentro de la sala y si eres el despachador (WHO cada 3 s, «SELECTOR YOU»);
//  · la contraseña de las salas libres: si se va quien la abrió, pasa al que queda de despachador («GATE OWNER»);
//  · el chat de la sala en el HUD (MainMenuForm.ChatSala.cs);
//  · agujas, señales y trenes de la sala en el mapa grande (MainMenuForm.MapaSala.cs).
// Con un servidor que no es el de SelectOR (el de Open Rails), no contesta y la conexión se retira sola.

using System;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        SwitchPilot _pilot;
        bool _schedDriving;   // conduciendo un servicio programado (turno)

        void PilotCreate() => PilotCreateFor(_roomHost, _roomPort);

        // También con «Unirse a un servidor» (host y puerto escritos a mano).
        void PilotCreateFor(string host, int port)
        {
            PilotStop();
            if (string.IsNullOrEmpty(host) || port <= 0) return;
            string name = (_txtMPUser?.Text ?? "").Trim();
            if (name.Length == 0) return;
            _pilot = new SwitchPilot(host, port, name, 1);
            RoomChatHook(_pilot);   // chat de la sala en el HUD (MainMenuForm.ChatSala.cs)
            RoomMapHook(_pilot);    // agujas, señales y trenes en el mapa grande (MainMenuForm.MapaSala.cs)
        }

        void PilotStop()
        {
            try { _pilot?.Dispose(); } catch { }
            _pilot = null;
        }

        // Desde RoadTick: conectar (con el escenario ya abierto) y preguntar si seguimos dentro, sin pedir desvíos.
        void PilotTick()
        {
            if (_pilot == null || !_scenarioReady) return;
            _pilot.EnsureStarted();
            if (_pilot.Ready) _pilot.Tick(Array.Empty<(int, int, int, int, double, double)>(), Array.Empty<(int, int, int)>());
            RoomMapTick();
        }
    }
}
