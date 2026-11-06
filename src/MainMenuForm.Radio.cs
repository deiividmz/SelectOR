// (1.2.90) La radio de la sala conduciendo: con la conexión de la sala (la del chat), y su mando en una ventanita encima de
// Open Rails (RadioHud), que sale cuando la conexión es con un servidor de SelectOR (READY con chat).

namespace SelectOR
{
    public partial class MainMenuForm
    {
        RoomRadio _radio; RadioHud _radioHud;

        void RadioHook(SwitchPilot p)
        {
            RadioStop();
            string me = (_txtMPUser?.Text ?? "").Trim();
            _radio = new RoomRadio(p, this, me);
            p.ChatReset += () => { try { if (!IsDisposed) BeginInvoke(new System.Action(RadioShowHud)); } catch { } };
        }

        void RadioShowHud()
        {
            if (_radio == null || _pilot == null || !_pilot.ChatOk) return;
            if (_radioHud == null || _radioHud.IsDisposed) _radioHud = new RadioHud(_prefs, _radio);
            if (!_radioHud.Visible) _radioHud.Show();
        }

        void RadioStop()
        {
            try { _radioHud?.Close(); _radioHud?.Dispose(); } catch { }
            _radioHud = null;
            try { _radio?.Dispose(); } catch { }
            _radio = null;
        }
    }
}
