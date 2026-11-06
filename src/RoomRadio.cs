// (1.2.90) RADIO DE LA SALA: lo que une la conexión con la sala (SwitchPilot), el sonido (RadioAudio), la tecla o el botón
// de hablar y los silencios. Uno por conexión (conduciendo, y el puesto de mando); oye solo el último que se ha puesto en
// marcha (si están los dos, no se oye doble) y la tecla solo la atiende ese.
//   · Pulsar → «RADIO ON»; el servidor da el canal («OK») o dice que está ocupado; soltar → «RADIO OFF».
//   · Un jefe de circulación con mando corta a quien hable (servidor). Cada uno silencia a quien quiera (se recuerda) y el
//     administrador puede silenciar a alguien para todos.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class RoomRadio : IDisposable
    {
        static RadioAudio _audio;
        static readonly List<RoomRadio> _all = new();
        static Timer _keyTimer;
        public static RoomRadio Active => _all.Count > 0 ? _all[^1] : null;
        public static AppPrefs Prefs;   // (MainMenuForm) tecla, volumen y silenciados
        public static event Action AnyChanged;

        readonly SwitchPilot _p; readonly Control _ui;
        public string Me;
        public string Talking { get; private set; }   // quién habla ahora (null: nadie)
        public bool TalkingJefe { get; private set; }
        public bool Mine { get; private set; }        // tengo el canal
        bool _pending, _released, _btn, _joy, _key;
        public string Note { get; private set; }      // lo último que ha pasado (canal ocupado, te han cortado…)
        DateTime _noteAt;
        public string[] Who { get; private set; } = Array.Empty<string>();
        public HashSet<string> GMuted { get; private set; } = new(StringComparer.Ordinal);
        public bool Listening { get; private set; }

        public RoomRadio(SwitchPilot p, Control ui, string me)
        {
            _p = p; _ui = ui; Me = me;
            if (_audio == null) { _audio = new RadioAudio(); _audio.MicError += () => Ui(() => SetNote(I18n.T("No se puede usar el micrófono: mira si está conectado y si Windows deja usarlo a SelectOR."))); }
            ApplyVolume();
            _audio.Captured += OnCaptured;
            p.RadioStart += (who, jefe) => Ui(() => OnStart(who, jefe));
            p.RadioVoice += (who, b) => { if (Active == this && !IsMuted(who) && who != Me) _audio.Receive(who, b); };
            p.RadioEnd += who => Ui(() => OnEnd(who));
            p.RadioCut += by => Ui(() => OnCut(by));
            p.RadioOk += () => Ui(OnOk);
            p.RadioError += (code, who) => Ui(() => OnError(code, who));
            p.RadioWho += l => Ui(() => { Who = l; Changed(); });
            p.RadioGMuted += l => Ui(() => { GMuted = new HashSet<string>(l, StringComparer.Ordinal); Changed(); });
            p.ChatReset += () => Ui(() => { Listening = false; Hello(); });   // (conexión nueva)
            _all.Add(this);
            foreach (var o in _all) if (o != this) o.Listening = false;
            Hello();
            StartKeys();
        }

        void Ui(Action a) { try { if (_ui != null && !_ui.IsDisposed && _ui.IsHandleCreated) _ui.BeginInvoke(a); else a(); } catch { } }
        void Changed() { try { AnyChanged?.Invoke(); } catch { } }

        // decirle al servidor que esta es la que oye, y los silenciados de siempre
        public void Hello()
        {
            if (Listening || Active != this || !_p.Ready || !_p.ChatOk) return;
            _p.RadioListen();
            foreach (var n in MutedList()) _p.RadioMute(n, true);
            Listening = true;
        }

        // ---------------------------------------------------------------- hablar
        public void SetButton(bool down) { _btn = down; Apply(); }
        public static void Joy(bool down) { var a = Active; if (a != null) { a._joy = down; a.Apply(); } }

        void Apply()
        {
            bool want = _btn || _joy || _key;
            if (want) Press(); else Release();
        }

        void Press()
        {
            if (Mine || _pending) return;
            Hello();
            if (!_p.Ready || !_p.ChatOk) { SetNote(I18n.T("La radio no está conectada con la sala.")); return; }
            _pending = true; _released = false;
            _p.RadioOn();
            Changed();
        }

        void Release()
        {
            if (_pending && !Mine) { _released = true; return; }   // (el «sí» del servidor llega después: se suelta entonces)
            if (!Mine) return;
            Mine = false;
            _audio.Talk(false);
            var p = _p;
            Task.Delay(140).ContinueWith(_ => p.RadioOff());   // (lo último que se ha dicho)
            _audio.Squelch(false);
            Changed();
        }

        void OnOk()
        {
            _pending = false;
            if (_released || !(_btn || _joy || _key)) { _p.RadioOff(); _released = false; Changed(); return; }
            Mine = true; Talking = Me;
            _audio.Talk(true);
            _audio.Squelch(true);   // (el chasquido de «ya puedes hablar»)
            Changed();
        }

        void OnCaptured(byte[] block) { if (Mine && Active == this) _p.RadioSend(block); }

        void OnError(string code, string who)
        {
            _pending = false;
            if (code == "busy") { _audio.Busy(); SetNote(string.Format(I18n.T("Canal ocupado: habla {0}."), who)); }
            else if (code == "muted") { _audio.Busy(); SetNote(I18n.T("El administrador te ha silenciado en la radio.")); }
            else if (code == "notin") SetNote(I18n.T("Para hablar por la radio tienes que estar en la sala (conduciendo) o en tu puesto de jefe de circulación."));
            else if (code == "admin") SetNote(I18n.T("Solo el administrador puede silenciar a alguien para todos."));
            Changed();
        }

        void OnCut(string by)
        {
            if (!Mine && !_pending) return;
            Mine = false; _pending = false; _audio.Talk(false);
            SetNote(by == "-" ? I18n.T("Transmisión cortada: como mucho 1 minuto seguido.")
                              : string.Format(I18n.T("{0} ha tomado el canal."), by));
            Changed();
        }

        void OnStart(string who, bool jefe)
        {
            Talking = who; TalkingJefe = jefe;
            if (Active == this && who != Me && !IsMuted(who)) _audio.Squelch(true);
            Changed();
        }

        void OnEnd(string who)
        {
            if (Talking != who) return;
            Talking = null; TalkingJefe = false;
            if (Active == this && who != Me && !IsMuted(who)) _audio.Squelch(false);
            Changed();
        }

        void SetNote(string s) { Note = s; _noteAt = DateTime.UtcNow; Changed(); }
        public string FreshNote => Note != null && (DateTime.UtcNow - _noteAt).TotalSeconds < 6 ? Note : null;

        // ---------------------------------------------------------------- silencios
        static List<string> MutedList() => Prefs?.RadioMuted ?? new List<string>();
        public static bool IsMuted(string name) => name != null && MutedList().Contains(name);
        public void Mute(string name, bool on)
        {
            if (Prefs == null || string.IsNullOrEmpty(name)) return;
            Prefs.RadioMuted ??= new List<string>();
            if (on && !Prefs.RadioMuted.Contains(name)) Prefs.RadioMuted.Add(name);
            if (!on) Prefs.RadioMuted.Remove(name);
            try { Prefs.Save(); } catch { }
            foreach (var r in _all) r._p.RadioMute(name, on);
            Changed();
        }

        // el administrador: para todos (con el vale de Supabase, como el jefe de circulación)
        public async void GMute(string name, bool on)
        {
            var (tok, err) = await DispatchForm.SupaTicket();
            if (tok == null) { SetNote(err ?? I18n.T("Error")); return; }
            _p.RadioGMute(tok, name, on);
        }

        public static void ApplyVolume() { if (_audio != null) _audio.Volume = Math.Max(0f, Math.Min(2f, (Prefs?.RadioVolume ?? 100) / 100f)); }

        // ---------------------------------------------------------------- la tecla (aunque se esté en Open Rails)
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        public static readonly (string id, string label, int vk)[] Keys =
        {
            ("RControl", "Control derecho", 0xA3), ("Scroll", "Bloq Despl", 0x91), ("Oem5", "º (junto al 1)", 0xDC),
            ("XButton1", "Botón lateral del ratón (atrás)", 0x05), ("XButton2", "Botón lateral del ratón (adelante)", 0x06),
            ("Pause", "Pausa", 0x13), ("None", "Ninguna (solo el botón o el mando)", 0),
        };
        static int KeyVk() { string id = Prefs?.RadioKey ?? "RControl"; foreach (var k in Keys) if (k.id == id) return k.vk; return 0xA3; }

        static void StartKeys()
        {
            if (_keyTimer != null) return;
            _keyTimer = new Timer { Interval = 30 };
            _keyTimer.Tick += (s, e) =>
            {
                var a = Active; if (a == null) return;
                int vk = KeyVk();
                bool down = vk != 0 && (GetAsyncKeyState(vk) & 0x8000) != 0;
                if (down != a._key) { a._key = down; a.Apply(); }
            };
            _keyTimer.Start();
        }

        public void Dispose()
        {
            if (Mine) { try { _audio.Talk(false); _p.RadioOff(); } catch { } }
            _audio.Captured -= OnCaptured;
            _all.Remove(this);
            var a = Active; if (a != null) { a.Listening = false; a.Hello(); }
            if (_all.Count == 0) { _keyTimer?.Stop(); _keyTimer?.Dispose(); _keyTimer = null; }
            Changed();
        }
    }
}
