// (1.2.91, petición 2026-11-07) PREMIOS DE LA SALA COMPARTIDA (sql/sala-premios.sql): plus de sala (+15 % del beneficio),
// plus con jefe de circulación (+5 %) y racha semanal (5 servicios → 1.000 €) para la empresa; los paga el servidor al
// cerrarse el servicio si el servidor de la sala dice que el maquinista estuvo dentro todo el tiempo. El maquinista y el
// jefe no cobran: se les cuenta en Mi perfil (casillas e insignias).

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
        int _roomSvc, _roomWeek, _roomStreaks, _roomDirected, _roomStreakN = 5;
        Label _profRoomVal, _profRoomWeekVal, _profRoomJefeVal;
        Control _profRoomJefeTile;

        // Mi perfil: servicios en la sala, la racha de esta semana y los dirigidos como jefe
        async Task LoadRoomMeritAsync()
        {
            try
            {
                var (json, err) = await Supa.RpcAsync("room_merit", new { });
                if (err != null || string.IsNullOrWhiteSpace(json))
                {
                    _roomSvc = _roomWeek = _roomStreaks = _roomDirected = 0;
                    SetKpi(_profRoomVal, "—"); SetKpi(_profRoomWeekVal, "—");
                    if (_profRoomJefeTile != null) _profRoomJefeTile.Visible = false;
                    return;
                }
                using var d = JsonDocument.Parse(json);
                var r = d.RootElement;
                _roomSvc = (int)Num(r, "services"); _roomWeek = (int)Num(r, "week"); _roomStreaks = (int)Num(r, "streaks");
                _roomDirected = (int)Num(r, "directed"); _roomStreakN = Math.Max(1, (int)Num(r, "streak_n"));
                SetKpi(_profRoomVal, _roomSvc.ToString("N0", EsEs));
                SetKpi(_profRoomWeekVal, Math.Min(_roomWeek, _roomStreakN) + " / " + _roomStreakN);
                SetKpi(_profRoomJefeVal, _roomDirected.ToString("N0", EsEs));
                if (_profRoomJefeTile != null) _profRoomJefeTile.Visible = _roomDirected > 0;
            }
            catch { }
        }

        // el plus de sala de un servicio (resultado al registrarlo y detalle desde la lista)
        async Task RoomBonusInto(ServiceResultDialog.Data r, string serviceId)
        {
            try
            {
                var (json, err) = await Supa.RpcAsync("room_bonus_of", new { p_service = serviceId });
                if (err != null || string.IsNullOrWhiteSpace(json) || json.Trim() == "null") return;
                using var d = JsonDocument.Parse(json);
                var e = d.RootElement; if (e.ValueKind != JsonValueKind.Object) return;
                r.Room = true;
                r.RoomBonus = Num(e, "bonus"); r.RoomPct = Num(e, "pct");
                r.RoomJefeBonus = Num(e, "jefe_bonus"); r.RoomJefePct = Num(e, "jefe_pct");
                r.RoomJefes = e.TryGetProperty("jefes", out var j) && j.ValueKind == JsonValueKind.Array ? string.Join(", ", j.EnumerateArray().Select(x => x.GetString())) : "";
                r.RoomStreak = Num(e, "streak"); r.RoomWeek = (int)Num(e, "week_count"); r.RoomStreakN = Math.Max(1, (int)Num(e, "streak_n"));
            }
            catch { }
        }
    }
}
