// Chat de la sala en el HUD del chat (Servidores SelectOR): un canal más del desplegable, el primero mientras estás
// dentro de una sala. Va por la conexión aparte con la sala (SwitchPilot: «SAY» / «SELECTOR CHAT») y está unido al
// chat de Open Rails: lo que escribes aquí les sale a los demás en Open Rails, y lo que ellos escriben allí sale aquí.
// También avisa de quién entra y quién sale. Al entrar en una sala, el HUD del chat se abre con ella.
// No se guarda: el servidor recuerda las últimas 40 líneas mientras quede alguien en la sala.

using System;
using System.Linq;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        const string RoomChatId = "@sala";
        long _roomChatSeq;
        bool _roomChatShown;   // ya se abrió el HUD del chat al entrar en esta sala

        bool RoomChatOn => _pilot != null && _pilot.Ready && _pilot.ChatOk;
        static bool IsRoomChat(string id) => id == RoomChatId;

        string RoomChatName() => "🖥 " + (_roomJoined != null && !string.IsNullOrEmpty(_roomName) ? _roomName : Tr("Sala"));

        ChatRoom RoomChat()
        {
            var r = ChatRoomFor(RoomChatId, RoomChatName());
            r.Loaded = r.StateLoaded = true; r.Member = true; r.Muted = false; r.Error = null;
            return r;
        }

        void RoomChatHook(SwitchPilot p)
        {
            _roomChatShown = false;
            p.Chat += (at, kind, from, text) => { try { BeginInvoke((Action)(() => RoomChatAdd(at, kind, from, text))); } catch { } };
            p.ChatReset += () => { try { BeginInvoke((Action)RoomChatReady); } catch { } };
            p.ChatError += e => { try { BeginInvoke((Action)(() => RoomChatError(e))); } catch { } };
        }

        // Conexión con la sala lista (o rehecha): el servidor manda otra vez lo último; el HUD del chat, con la sala.
        void RoomChatReady()
        {
            var r = RoomChat();
            r.Msgs.Clear(); r.LastId = 0;
            if (!_roomChatShown && Supa.IsLoggedIn)
            {
                _roomChatShown = true;
                _chatHudManual = false;
                if (!ChatHudAlive) ShowChatHud(force: true);
                else { if (!_chatHud.Visible) _chatHud.Show(); SetChatHudCompany(RoomChatId); }
            }
            ChatRoomUpdated(r);
        }

        void RoomChatAdd(DateTime at, char kind, string from, string text)
        {
            var r = RoomChat();
            string me = (_txtMPUser?.Text ?? "").Trim();
            var m = new ChatMsg { Id = ++_roomChatSeq, AtUtc = at, Fixed = true };
            if (kind == 'm')
            {
                m.User = from; m.Body = text;
                // (1.2.77) el jefe de circulación con alguna dependencia ahora, con su etiqueta
                if (_pilot != null && _pilot.JefeZones.Any(z => z.Name == from)) m.User = from + "  · " + Tr("Jefe de circulación");
                // (el chat agrupa por UserId: cada jugador el suyo; los míos, como míos)
                m.UserId = string.Equals(from, me, StringComparison.Ordinal) ? Supa.UserId ?? "" : "@mp:" + from;
            }
            else
            {
                m.User = Tr("Sala"); m.UserId = RoomChatId;
                m.Body = string.Format(kind == 'i' ? Tr("{0} ha entrado en la sala") : Tr("{0} ha salido de la sala"), from);
            }
            r.Msgs.Add(m);
            if (r.Msgs.Count > 200) r.Msgs.RemoveRange(0, r.Msgs.Count - 200);
            r.LastId = m.Id;
            ChatRoomUpdated(r);
            // si no se está viendo la sala en el HUD, en la barra (solo lo de ahora, no lo que se repite al conectarse)
            bool seen = ChatHudAlive && _chatHud.Visible && !_chatHud.Collapsed && _chatHudCompanyId == RoomChatId;
            if (kind == 'm' && !seen && m.UserId != (Supa.UserId ?? "") && (DateTime.UtcNow - at).TotalSeconds < 20)
                try { _driveBar?.ShowMessage("💬 " + from + ": " + text, false, 8); } catch { }
        }

        void RoomChatError(string e)
        {
            string msg = e == "slow" ? Tr("Más despacio: como mucho 5 mensajes cada 10 segundos.") : Tr("No estás dentro de la sala: no se ha enviado.");
            try { _driveBar?.ShowMessage("💬 " + msg, true, 6); } catch { }
        }

        string RoomChatSend(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return null;
            if (!RoomChatOn) return Tr("Sin conexión con la sala.");
            if (text.Length > 300) return Tr("El mensaje es demasiado largo (máximo 300 caracteres).");
            _pilot.Say(text);
            return null;
        }
    }
}
