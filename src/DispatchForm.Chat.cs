// PUESTO DE MANDO · CHAT DE LA SALA (1.2.77). A la derecha del tablero, el mismo chat de la sala que el del HUD (y el de
// Open Rails): lo que escriben los maquinistas y quién entra o sale. Va por la conexión del puesto con la sala
// (SwitchPilot: «SAY» / «SELECTOR CHAT»). Escriben los que conducen en la sala y el jefe de circulación desde aquí
// (aunque no conduzca: el servidor le reconoce por su vale). Los mensajes de un jefe de circulación con alguna dependencia
// en ese momento llevan la etiqueta «Jefe de circulación». «›» lo recoge en una franja estrecha (con los nuevos sin leer).

using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class DispatchForm
    {
        Panel _chatPanel, _chatBody;
        Label _chatHead, _chatToggle, _chatNote;
        RichTextBox _chatLog;
        TextBox _chatInput;
        bool _chatCollapsed;
        int _chatUnread;
        static readonly Color ChatJefe = Color.FromArgb(129, 199, 132), ChatName = Color.FromArgb(144, 202, 249), ChatSys = Color.FromArgb(150, 160, 170);

        // (constructor) el panel de la derecha
        Panel BuildChat()
        {
            _chatPanel = new Panel { Dock = DockStyle.Right, Width = Theme.Px(300), BackColor = Theme.BgSidebar, Padding = new Padding(Theme.Px(8), Theme.Px(4), Theme.Px(8), Theme.Px(8)) };
            var head = new Panel { Dock = DockStyle.Top, Height = Theme.Px(26), BackColor = _chatPanel.BackColor };
            _chatHead = new Label { Text = Tr("CHAT DE LA SALA"), Dock = DockStyle.Fill, Font = Theme.Font(8.5f, FontStyle.Bold), ForeColor = Theme.Subtle, BackColor = head.BackColor, TextAlign = ContentAlignment.MiddleLeft };
            _chatToggle = new Label { Text = "›", Dock = DockStyle.Right, Width = Theme.Px(24), Font = Theme.Font(12f, FontStyle.Bold), ForeColor = Theme.Subtle, BackColor = head.BackColor, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand };
            _chatToggle.Click += (s, e) => ChatCollapse(!_chatCollapsed);
            _chatHead.Click += (s, e) => { if (_chatCollapsed) ChatCollapse(false); };
            new ToolTip().SetToolTip(_chatToggle, Tr("Recoger o abrir el chat"));
            head.Controls.Add(_chatHead); head.Controls.Add(_chatToggle);

            _chatBody = new Panel { Dock = DockStyle.Fill, BackColor = _chatPanel.BackColor };
            _chatLog = new RichTextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(24, 29, 36), ForeColor = Theme.Text,
                Font = Theme.Font(9f), DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical, TabStop = false, ShortcutsEnabled = true
            };
            _chatNote = new Label { Dock = DockStyle.Bottom, Height = Theme.Px(34), Font = Theme.Font(8f), ForeColor = Theme.Subtle, BackColor = _chatPanel.BackColor, TextAlign = ContentAlignment.MiddleLeft, Text = Tr("Conectando con la sala…") };
            var inBox = new Panel { Dock = DockStyle.Bottom, Height = Theme.Px(30), BackColor = _chatPanel.BackColor, Padding = new Padding(0, Theme.Px(4), 0, 0) };
            _chatInput = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.Surface2, ForeColor = Theme.Text, Font = Theme.Font(9.5f), MaxLength = 300, PlaceholderText = Tr("Escribe a la sala e Intro…"), Enabled = false };
            _chatInput.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true; e.Handled = true;
                ChatSend();
            };
            inBox.Controls.Add(_chatInput);
            _chatBody.Controls.Add(_chatLog); _chatBody.Controls.Add(inBox); _chatBody.Controls.Add(_chatNote);
            _chatPanel.Controls.Add(_chatBody); _chatPanel.Controls.Add(head);
            return _chatPanel;
        }

        // (Prepare, con la conexión creada)
        void PrepareChat()
        {
            _pilot.Chat += (at, kind, from, text) => { try { if (!IsDisposed) BeginInvoke(new Action(() => ChatAdd(at, kind, from, text))); } catch { } };
            _pilot.ChatReset += () => { try { if (!IsDisposed) BeginInvoke(new Action(ChatReady)); } catch { } };
            _pilot.ChatError += err => { try { if (!IsDisposed) BeginInvoke(new Action(() => ChatErr(err))); } catch { } };
        }

        // conexión lista (o rehecha): el servidor manda otra vez lo último
        void ChatReady()
        {
            _chatLog.Clear(); _chatUnread = 0; ChatHeadText();
            _chatInput.Enabled = true;
            _chatNote.ForeColor = Theme.Subtle;
            _chatNote.Text = Tr("Escriben los que conducen en la sala y el jefe de circulación.");
        }

        void ChatErr(string err)
        {
            _chatNote.ForeColor = Color.FromArgb(255, 183, 77);
            _chatNote.Text = err == "slow" ? Tr("Más despacio: como mucho 5 mensajes cada 10 segundos.")
                                           : Tr("No se ha enviado: escriben los que conducen en la sala y el jefe de circulación con el mando.");
        }

        void ChatSend()
        {
            string t = (_chatInput.Text ?? "").Trim();
            if (t.Length == 0) return;
            if (_pilot == null || !_pilot.Ready || !_pilot.ChatOk) { ChatErr("off"); return; }
            _pilot.Say(t);
            _chatInput.Clear();
            _chatNote.ForeColor = Theme.Subtle;
            _chatNote.Text = Tr("Escriben los que conducen en la sala y el jefe de circulación.");
        }

        // ¿es jefe de circulación con alguna dependencia ahora?
        bool ChatIsJefe(string name) => _pilot != null && _pilot.JefeZones.Any(z => z.Name == name);

        void ChatAdd(DateTime at, char kind, string from, string text)
        {
            var box = _chatLog;
            box.SelectionStart = box.TextLength; box.SelectionLength = 0;
            if (box.TextLength > 0) box.AppendText("\n");
            void Put(string s, Color c, FontStyle st = FontStyle.Regular, float size = 9f)
            {
                box.SelectionStart = box.TextLength; box.SelectionLength = 0;
                box.SelectionColor = c; box.SelectionFont = Theme.Font(size, st);
                box.AppendText(s);
            }
            Put(at.ToLocalTime().ToString("HH:mm") + "  ", ChatSys, FontStyle.Regular, 8f);
            if (kind == 'm')
            {
                bool jefe = ChatIsJefe(from);
                bool me = string.Equals(from, _me, StringComparison.Ordinal);
                Put(from, jefe ? ChatJefe : me ? Theme.Accent : ChatName, FontStyle.Bold);
                if (jefe) Put("  [" + Tr("Jefe de circulación") + "]", ChatJefe, FontStyle.Bold, 8f);
                Put(":  " + text, Theme.Text);
            }
            else Put(string.Format(kind == 'i' ? Tr("{0} ha entrado en la sala") : Tr("{0} ha salido de la sala"), from), ChatSys, FontStyle.Italic);
            // como mucho 300 líneas
            if (box.Lines.Length > 300)
            {
                int cut = box.GetFirstCharIndexFromLine(box.Lines.Length - 300);
                box.Select(0, cut); box.ReadOnly = false; box.SelectedText = ""; box.ReadOnly = true;
            }
            { box.SelectionStart = box.TextLength; box.ScrollToCaret(); }
            if (_chatCollapsed && kind == 'm' && (DateTime.UtcNow - at).TotalSeconds < 20) { _chatUnread++; ChatHeadText(); }
        }

        void ChatCollapse(bool on)
        {
            _chatCollapsed = on;
            _chatBody.Visible = !on;
            _chatPanel.Width = on ? Theme.Px(64) : Theme.Px(300);
            _chatPanel.Padding = on ? new Padding(Theme.Px(4), Theme.Px(4), Theme.Px(2), 0) : new Padding(Theme.Px(8), Theme.Px(4), Theme.Px(8), Theme.Px(8));
            _chatToggle.Text = on ? "‹" : "›";
            if (!on) { _chatUnread = 0; _chatLog.SelectionStart = _chatLog.TextLength; _chatLog.ScrollToCaret(); }
            ChatHeadText();
        }

        void ChatHeadText()
        {
            if (_chatCollapsed) { _chatHead.Text = _chatUnread > 0 ? "💬" + _chatUnread : "💬"; _chatHead.ForeColor = _chatUnread > 0 ? Theme.Accent : Theme.Subtle; _chatHead.Cursor = Cursors.Hand; }
            else { _chatHead.Text = Tr("CHAT DE LA SALA"); _chatHead.ForeColor = Theme.Subtle; _chatHead.Cursor = Cursors.Default; }
        }
    }
}
