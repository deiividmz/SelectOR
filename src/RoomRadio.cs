// (1.2.90) RADIO DE LA SALA: lo que une la conexión con la sala (SwitchPilot), el sonido (RadioAudio), la tecla o el botón
// de hablar y los silencios. Uno por conexión (conduciendo, y el puesto de mando); oye solo el último que se ha puesto en
// marcha (si están los dos, no se oye doble) y la tecla solo la atiende ese.
//   · Pulsar → «RADIO ON»; el servidor da el canal («OK») o dice que está ocupado; soltar → «RADIO OFF».
//   · Un jefe de circulación con mando corta a quien hable (servidor). Cada uno silencia a quien quiera (se recuerda) y el
//     administrador puede silenciar a alguien para todos.
//   · (2026-11-10, petición) CANALES: cada banda de regulación el suyo («b3»). Conduciendo, el de la banda por la que va tu tren
//     (Bands, lo calcula MainMenuForm.Radio.cs); en el puesto, los de tus dependencias y el de la banda que ves. Los jefes de
//     circulación tienen además el suyo («jefes», sin maquinistas) y el administrador puede hablar a todas («todas»). Se oye a
//     la vez a quien hable en cada canal (RadioAudio mezcla). Sin banda aún: todo, como antes («*»).

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
        public string Talking { get; private set; }   // quién habla ahora (null: nadie; si hablan varios, el último que empezó)
        public bool TalkingJefe { get; private set; }
        public string TalkingCh { get; private set; }  // (2026-11-10) en qué canal
        readonly List<(string who, bool jefe, string ch)> _talkers = new();   // todos los que hablan ahora (uno por canal)
        public int TalkingCount => _talkers.Count;
        // (2026-11-10) los canales: los de banda que oye (null o vacío: aún no se sabe, todo), los que puede elegir para hablar (el
        // primero, el de siempre) y el elegido (null: el de siempre)
        public Func<IReadOnlyList<string>> Bands;
        public Func<IReadOnlyList<string>> Choices;
        public string Chosen;
        string _chSent;
        readonly Timer _chTimer;
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
            RadioAudio.InDevice = Prefs?.RadioMic; RadioAudio.OutDevice = Prefs?.RadioOut;   // (2026-11-09) los de Ajustes
            if (_audio == null) { _audio = new RadioAudio(); _audio.MicError += () => Ui(() => SetNote(I18n.T("No se puede usar el micrófono: mira si está conectado y si Windows deja usarlo a SelectOR."))); }
            ApplyVolume();
            _audio.Captured += OnCaptured;
            p.RadioStart += (who, jefe, ch) => Ui(() => OnStart(who, jefe, ch));
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
            _chTimer = new Timer { Interval = 1000 };
            _chTimer.Tick += (s, e) => SendChannels();
            _chTimer.Start();
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
            _chSent = null; SendChannels();
        }

        // ---------------------------------------------------------------- canales
        // (cada segundo, y al conectar) los de banda que oye, si han cambiado
        void SendChannels()
        {
            if (!Listening || Active != this || !_p.Ready || !_p.ChatOk) return;
            IReadOnlyList<string> b = null; try { b = Bands?.Invoke(); } catch { }
            string s = b == null || b.Count == 0 ? "*" : string.Join(",", b.Distinct().OrderBy(x => x, StringComparer.Ordinal));
            if (s == _chSent) return;
            _chSent = s;
            _p.RadioChannels(s == "*" ? null : s.Split(','));
            Changed();
        }

        // por dónde se habla: el elegido si se puede; si no, el primero de los que se puede (o el de siempre)
        public string TalkChannel
        {
            get
            {
                IReadOnlyList<string> c = null; try { c = Choices?.Invoke(); } catch { }
                if (c == null || c.Count == 0) { IReadOnlyList<string> b = null; try { b = Bands?.Invoke(); } catch { } return b != null && b.Count > 0 ? b[0] : "*"; }
                return Chosen != null && c.Contains(Chosen) ? Chosen : c[0];
            }
        }
        public IReadOnlyList<string> ChoiceList { get { try { return Choices?.Invoke() ?? Array.Empty<string>(); } catch { return Array.Empty<string>(); } } }
        public void Choose(string ch) { Chosen = ch; Changed(); }

        // el nombre de un canal para la pantalla
        public static string ChannelLabel(string ch) => ch == null || ch == "*" ? I18n.T("Toda la sala")
            : ch == "jefes" ? I18n.T("Jefes de circulación") : ch == "todas" ? I18n.T("Todas las bandas")
            : ch.Length > 1 && ch[0] == 'b' ? "BR" + ch.Substring(1) : ch;

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
            _p.RadioOn(TalkChannel);
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
            else if (code == "nochan") SetNote(I18n.T("No puedes hablar por ese canal."));
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

        void OnStart(string who, bool jefe, string ch)
        {
            _talkers.RemoveAll(t => t.who == who || t.ch == ch);
            _talkers.Add((who, jefe, ch));
            Talking = who; TalkingJefe = jefe; TalkingCh = ch;
            if (Active == this && who != Me && !IsMuted(who)) _audio.Squelch(true, who);
            Changed();
        }

        void OnEnd(string who)
        {
            if (_talkers.RemoveAll(t => t.who == who) == 0 && Talking != who) return;
            if (_talkers.Count > 0) { var l = _talkers[^1]; Talking = l.who; TalkingJefe = l.jefe; TalkingCh = l.ch; }
            else { Talking = null; TalkingJefe = false; TalkingCh = null; }
            if (Active == this && who != Me && !IsMuted(who)) _audio.Squelch(false, who);
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
            try { _chTimer.Stop(); _chTimer.Dispose(); } catch { }
            _audio.Captured -= OnCaptured;
            _all.Remove(this);
            var a = Active; if (a != null) { a.Listening = false; a.Hello(); }
            if (_all.Count == 0) { _keyTimer?.Stop(); _keyTimer?.Dispose(); _keyTimer = null; }
            Changed();
        }
    }
}
