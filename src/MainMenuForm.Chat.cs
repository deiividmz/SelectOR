// Chat de empresa (chat-empresa.sql): sección «Chat» de Empresas (conversación + permisos para
// escribir) y HUD transparente durante la conducción.
//  · Los mensajes llegan al momento por Supabase Realtime y, por si se cae, se vuelven a pedir cada
//    pocos segundos mientras la sección o el HUD están a la vista (solo los nuevos: chat_list con p_after).
//  · Permisos: el gerente (o el superadmin) retira o devuelve a un socio el permiso para escribir.
//  · HUD: muestra la empresa con la que estás de servicio; si no, la favorita; se puede cambiar con su
//    desplegable (entre las empresas de las que eres socio). Se abre y cierra desde la barra superior.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        const int ChatSubtab = 12;
        readonly Dictionary<string, ChatRoom> _chatRooms = new(StringComparer.OrdinalIgnoreCase);
        Timer _chatTimer, _chatRtDebounce;
        bool _chatMutesChanged;
        static bool _chatNoEdits;   // el servidor aún no tiene chat-editar.sql (sin ediciones ni borrados)
        ChatMsg _chatEditing;       // mensaje que se está editando en la sección (o null)

        // Sección
        Panel _chatPanel, _chatConvPage, _chatPermPage;
        ChatView _chatView;
        RoundedInput _chatInput;
        RoundButton _chatSendBtn;
        Label _chatNote, _chatPermMsg;
        FlowLayoutPanel _chatTabs;
        CardTable _chatPermList;
        readonly List<string> _chatPermIds = new();
        int _chatTab;

        ChatRoom ChatRoomFor(string id, string name = null)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (!_chatRooms.TryGetValue(id, out var r)) { r = new ChatRoom { CompanyId = id }; _chatRooms[id] = r; }
            if (!string.IsNullOrEmpty(name)) r.Name = name;
            return r;
        }

        static bool ChatMissing(string err) => err != null && (err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0
                                                              || err.IndexOf("Could not find the function", StringComparison.OrdinalIgnoreCase) >= 0);

        // Mensajes nuevos de una empresa (la primera vez, los 80 últimos).
        async Task ChatFetch(ChatRoom r)
        {
            if (r == null || r.Loading || !Supa.IsLoggedIn) return;
            r.Loading = true;
            bool changed = false, wasLoaded = r.Loaded;
            int fromOthers = 0;
            try
            {
                string json, err;
                if (!_chatNoEdits && r.LastChange != null)
                {
                    (json, err) = await Supa.RpcAsync("chat_list", new { p_company = r.CompanyId, p_after = r.LastId, p_limit = 80, p_since = r.LastChange });
                    if (err != null && ChatMissing(err)) _chatNoEdits = true;   // servidor sin chat-editar.sql: como antes
                    else goto got;
                }
                (json, err) = await Supa.RpcAsync("chat_list", new { p_company = r.CompanyId, p_after = r.LastId, p_limit = 80 });
            got:
                if (err != null)
                {
                    string e = ChatMissing(err) ? Tr("El servidor aún no tiene el chat de empresa (falta chat-empresa.sql).") : Tr("Error: ") + err;
                    changed = r.Error != e; r.Error = e;
                }
                else
                {
                    if (r.Error != null) { r.Error = null; changed = true; }
                    using var d = JsonDocument.Parse(json);
                    foreach (var e in d.RootElement.EnumerateArray())
                    {
                        long id = e.TryGetProperty("id", out var ie) && ie.ValueKind == JsonValueKind.Number ? ie.GetInt64() : 0;
                        bool edited = e.TryGetProperty("edited", out var ed) && ed.ValueKind == JsonValueKind.True;
                        bool deleted = e.TryGetProperty("deleted", out var de) && de.ValueKind == JsonValueKind.True;
                        string ch = Str(e, "changed_at");
                        if (ch.Length > 0 && (r.LastChange == null || string.CompareOrdinal(ch, r.LastChange) > 0)) r.LastChange = ch;
                        if (id <= r.LastId)
                        {
                            // un mensaje que ya teníamos: editado o eliminado después
                            var old = r.Msgs.Find(x => x.Id == id);
                            if (old != null && (old.Body != Str(e, "body") || old.Edited != edited || old.Deleted != deleted))
                            { old.Body = Str(e, "body"); old.Edited = edited; old.Deleted = deleted; changed = true; }
                            continue;
                        }
                        DateTime.TryParse(Str(e, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at);
                        r.Msgs.Add(new ChatMsg { Id = id, UserId = Str(e, "user_id"), User = Str(e, "username"), Role = Str(e, "role"), Body = Str(e, "body"), AtUtc = at, Edited = edited, Deleted = deleted });
                        r.LastId = id; changed = true;
                        if (wasLoaded && !deleted && !string.Equals(Str(e, "user_id"), Supa.UserId, StringComparison.OrdinalIgnoreCase)) fromOthers++;
                    }
                    if (r.Msgs.Count > 400) { r.Msgs.RemoveRange(0, r.Msgs.Count - 300); changed = true; }
                    if (!r.Loaded) { r.Loaded = true; changed = true; }
                }
            }
            catch { }
            finally { r.Loading = false; }
            if (changed) ChatRoomUpdated(r);
            if (fromOthers > 0) NotifySound.Play();   // mensaje nuevo de otro socio (no al cargar el chat)
        }

        // Mi permiso en esa empresa (y, si modero, quién no puede escribir).
        async Task ChatLoadState(ChatRoom r)
        {
            if (r == null || !Supa.IsLoggedIn) return;
            var (json, err) = await Supa.RpcAsync("chat_state", new { p_company = r.CompanyId });
            if (err != null) { if (ChatMissing(err)) r.Error = Tr("El servidor aún no tiene el chat de empresa (falta chat-empresa.sql)."); ChatRoomUpdated(r); return; }
            try
            {
                using var d = JsonDocument.Parse(json);
                var root = d.RootElement;
                r.Member = root.TryGetProperty("member", out var m) && m.ValueKind == JsonValueKind.True;
                r.Muted = root.TryGetProperty("muted", out var mu) && mu.ValueKind == JsonValueKind.True;
                r.CanModerate = root.TryGetProperty("can_moderate", out var cm) && cm.ValueKind == JsonValueKind.True;
                // superadministrador: escribe en cualquier empresa (servidor con chat-superadmin.sql)
                r.AsSuper = root.TryGetProperty("superadmin", out var sa) && sa.ValueKind == JsonValueKind.True
                            && root.TryGetProperty("can_write", out var cw) && cw.ValueKind == JsonValueKind.True;
                r.MutedUsers.Clear();
                if (root.TryGetProperty("muted_users", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var u in arr.EnumerateArray()) if (u.ValueKind == JsonValueKind.String) r.MutedUsers.Add(u.GetString());
                r.StateLoaded = true;
            }
            catch { }
            ChatRoomUpdated(r);
        }

        // Escribe un mensaje. Devuelve el error (o null si ha ido bien).
        async Task<string> ChatSendText(ChatRoom r, string text)
        {
            if (r == null) return Tr("Elige una empresa.");
            text = (text ?? "").Trim();
            if (text.Length == 0) return null;
            if (text.Length > 500) return Tr("El mensaje es demasiado largo (máximo 500 caracteres).");
            var (_, err) = await Supa.RpcAsync("chat_send", new { p_company = r.CompanyId, p_body = text });
            if (err != null)
            {
                if (ChatMissing(err)) return Tr("El servidor aún no tiene el chat de empresa (falta chat-empresa.sql).");
                if (err.IndexOf("retirado el permiso", StringComparison.OrdinalIgnoreCase) >= 0) { r.Muted = true; ChatRoomUpdated(r); }
                return ChatErr(err);
            }
            await ChatFetch(r);
            return null;
        }

        // Editar o eliminar un mensaje propio. Devuelve el error (o null si ha ido bien).
        async Task<string> ChatEditText(ChatRoom r, ChatMsg m, string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return Tr("El mensaje está vacío.");
            if (text.Length > 500) return Tr("El mensaje es demasiado largo (máximo 500 caracteres).");
            var (_, err) = await Supa.RpcAsync("chat_edit", new { p_id = m.Id, p_body = text });
            if (err != null) return ChatMissing(err) ? Tr("El servidor aún no permite editar mensajes (falta chat-editar.sql).") : ChatErr(err);
            m.Body = text; m.Edited = true; ChatRoomUpdated(r);
            await ChatFetch(r);
            return null;
        }

        async Task<string> ChatDeleteMsg(ChatRoom r, ChatMsg m)
        {
            var (_, err) = await Supa.RpcAsync("chat_delete", new { p_id = m.Id });
            if (err != null) return ChatMissing(err) ? Tr("El servidor aún no permite eliminar mensajes (falta chat-editar.sql).") : ChatErr(err);
            m.Body = ""; m.Deleted = true; ChatRoomUpdated(r);
            await ChatFetch(r);
            return null;
        }

        // Mensaje del servidor sin el envoltorio técnico.
        static string ChatErr(string err)
        {
            try
            {
                using var d = JsonDocument.Parse(err.Substring(Math.Max(0, err.IndexOf('{'))));
                if (d.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString();
            }
            catch { }
            return err;
        }

        // Algo ha cambiado en una empresa: se repinta donde se esté viendo.
        void ChatRoomUpdated(ChatRoom r)
        {
            if (r == null || IsDisposed) return;
            if (ChatSectionRoom() == r) RefreshChatSection();
            if (ChatHudAlive && _chatHudCompanyId == r.CompanyId) RefreshChatHud();
        }

        // ---------------------------------------------------------------- sondeo y tiempo real
        void EnsureChatTimer()
        {
            if (_chatTimer != null) return;
            _chatTimer = new Timer { Interval = 6000 };
            _chatTimer.Tick += async (s, e) => await ChatPollOpen();
            _chatTimer.Start();
            _chatRtDebounce = new Timer { Interval = 300 };
            _chatRtDebounce.Tick += async (s, e) => { _chatRtDebounce.Stop(); await ChatPollOpen(); };
        }

        bool ChatSectionVisible => _chatPanel != null && _chatPanel.Visible && _activePage == PageEmpresas && WindowState != FormWindowState.Minimized;

        ChatRoom ChatSectionRoom() => _empSel == null ? null : ChatRoomFor(_empSel.Id, _empSel.Name);

        // Pide lo nuevo de los chats que se están viendo (sección y HUD).
        async Task ChatPollOpen()
        {
            if (!Supa.IsLoggedIn) return;
            bool mutes = _chatMutesChanged; _chatMutesChanged = false;
            var rooms = new List<ChatRoom>();
            if (ChatSectionVisible) rooms.Add(ChatSectionRoom());
            if (ChatHudAlive && _chatHud.Visible && !string.IsNullOrEmpty(_chatHudCompanyId)) rooms.Add(ChatRoomFor(_chatHudCompanyId));
            var done = new HashSet<ChatRoom>();
            foreach (var r in rooms)
            {
                if (r == null || !done.Add(r)) continue;
                if (mutes) await ChatLoadState(r);
                await ChatFetch(r);
            }
        }

        // Llega un cambio del chat por Realtime (ya en el hilo de la interfaz).
        void ChatRealtime(string table)
        {
            if (table == "company_chat_mutes") _chatMutesChanged = true;
            EnsureChatTimer();
            _chatRtDebounce.Stop(); _chatRtDebounce.Start();
        }

        // ---------------------------------------------------------------- sección «Chat»
        Panel BuildChatSubpanel()
        {
            var outer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

            // --- Conversación ---
            _chatConvPage = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var conv = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg };
            conv.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            conv.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // mensajes
            conv.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // escribir
            conv.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // nota
            var frame = new Card { Dock = DockStyle.Fill, Fill = Color.FromArgb(28, 32, 36), Radius = 12, Padding = new Padding(2, 6, 2, 6), Margin = new Padding(2, 0, 2, 8) };
            _chatView = new ChatView { Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 32, 36), MyUserId = Supa.UserId ?? "", EmptyText = Tr("Cargando el chat…") };
            _chatView.EditRequested = m => ChatStartEdit(m);
            _chatView.DeleteRequested = async m =>
            {
                var r = ChatSectionRoom(); if (r == null) return;
                if (_chatEditing == m) ChatCancelEdit();
                string err = await ChatDeleteMsg(r, m);
                Msg(_chatNote, err ?? Tr("Mensaje eliminado."), err != null);
            };
            frame.Controls.Add(_chatView);
            conv.Controls.Add(frame);

            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Theme.Bg, Height = 46, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _chatInput = new RoundedInput(Tr("Escribe un mensaje para la empresa…")) { Dock = DockStyle.Fill, Height = 40, Margin = new Padding(2, 2, 8, 2) };
            _chatInput.Box.MaxLength = 500;
            _chatInput.Box.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Escape && _chatEditing != null) { e.SuppressKeyPress = true; ChatCancelEdit(); return; }
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                await SendFromSection();
            };
            _chatInput.Box.TextChanged += (s, e) => UpdateChatNote();
            _chatSendBtn = EmpButton(Tr("Enviar"), primary: true); _chatSendBtn.Width = 130; _chatSendBtn.Height = 40; _chatSendBtn.Margin = new Padding(0, 2, 2, 2);
            _chatSendBtn.Click += async (s, e) => await SendFromSection();
            var emoji = new EmojiButton { Target = _chatInput.Box, Fill = Theme.Surface, Margin = new Padding(0, 2, 8, 2) };   // 😊 emojis
            row.Controls.Add(_chatInput, 0, 0); row.Controls.Add(emoji, 1, 0); row.Controls.Add(_chatSendBtn, 2, 0);
            conv.Controls.Add(row);
            _chatNote = EmpMsg(); _chatNote.Margin = new Padding(4, 4, 2, 0); _chatNote.MaximumSize = new Size(900, 0);
            conv.Controls.Add(_chatNote);
            _chatConvPage.Controls.Add(conv);

            // --- Permisos (gerente y superadmin) ---
            _chatPermPage = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
            var perm = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg };
            perm.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            perm.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            perm.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            perm.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var intro = EmpIntro("Quién puede escribir en el chat de la empresa. A quien le retires el permiso podrá seguir leyendo, pero no escribir. Solo el gerente y el administrador pueden cambiarlo.");
            intro.MaximumSize = new Size(900, 0);
            perm.Controls.Add(intro);
            // Una tarjeta por socio: su rol y si puede escribir (verde) o no (rojo).
            _chatPermList = EmpCards();
            _chatPermList.TitleCol = 0; _chatPermList.SubCols = new[] { 1 }; _chatPermList.PillCol = 2;
            _chatPermList.CardHeight = 64; _chatPermList.MinWidth = 380; _chatPermList.Columns = 3;
            perm.Controls.Add(_chatPermList);
            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0) };
            var mute = EmpButton(Tr("Retirar permiso para escribir")); mute.Width = 270;
            mute.HoverColor = Color.FromArgb(150, 60, 60);
            mute.Click += (s, e) => ChatSetMute(true);
            var unmute = EmpButton(Tr("Devolver permiso"), primary: true); unmute.Width = 200; unmute.Margin = new Padding(8, 10, 2, 2);
            unmute.Click += (s, e) => ChatSetMute(false);
            btns.Controls.Add(mute); btns.Controls.Add(unmute);
            _chatPermMsg = EmpMsg(); _chatPermMsg.Margin = new Padding(12, 20, 2, 2); _chatPermMsg.MaximumSize = new Size(600, 0);
            btns.Controls.Add(_chatPermMsg);
            perm.Controls.Add(btns);
            _chatPermPage.Controls.Add(perm);

            var pages = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            pages.Controls.Add(_chatPermPage); pages.Controls.Add(_chatConvPage);
            _chatTabs = MakeSubTabs(new[] { "Conversación", "Permisos" }, i =>
            {
                _chatTab = i;
                _chatConvPage.Visible = i == 0; _chatPermPage.Visible = i == 1;
                if (i == 1) FillChatPerms();
                else _chatView.ScrollToBottom();
            });
            outer.Controls.Add(pages); outer.Controls.Add(_chatTabs);
            return outer;
        }

        // Al abrir la sección (o cambiar de empresa con ella abierta).
        async void OnChatShown()
        {
            EnsureChatTimer();
            var r = ChatSectionRoom();
            if (r == null) return;
            _chatView.MyUserId = Supa.UserId ?? "";
            RefreshChatSection();
            await ChatLoadState(r);
            await ChatFetch(r);
            if (ChatSectionRoom() == r) { RefreshChatSection(); _chatView.ScrollToBottom(); }
        }

        void RefreshChatSection()
        {
            if (_chatView == null) return;
            var r = ChatSectionRoom();
            if (r == null) return;
            _chatView.EmptyText = r.Error ?? (r.Loaded ? Tr("Aún no hay mensajes en el chat de la empresa. ¡Saluda!") : Tr("Cargando el chat…"));
            _chatView.SetMessages(r.Msgs);
            bool can = r.CanWrite && r.Error == null;
            _chatInput.Enabled = can; _chatSendBtn.Enabled = can;
            _chatInput.Box.PlaceholderText = can ? Tr("Escribe un mensaje para la empresa…")
                : !r.Member ? Tr("Solo los socios de la empresa pueden escribir")
                : Tr("No tienes permiso para escribir en este chat");
            // Pestaña «Permisos»: solo quien modera.
            bool mod = r.CanModerate && r.Error == null;
            if (_chatTabs != null && _chatTabs.Controls.Count > 1)
            {
                _chatTabs.Controls[1].Visible = mod;
                if (!mod && _chatTab == 1)
                {
                    _chatTab = 0; _chatConvPage.Visible = true; _chatPermPage.Visible = false;
                    var b0 = (RoundButton)_chatTabs.Controls[0]; var b1 = (RoundButton)_chatTabs.Controls[1];
                    b0.Active = true; b1.Active = false; b0.Invalidate(); b1.Invalidate();
                }
            }
            if (_chatTab == 1) FillChatPerms();
            UpdateChatNote();
        }

        void UpdateChatNote()
        {
            var r = ChatSectionRoom();
            if (_chatNote == null || r == null) return;
            if (r.Error != null) { Msg(_chatNote, r.Error, true); return; }
            if (r.StateLoaded && !r.Member && !r.AsSuper) { Msg(_chatNote, Tr("Estás viendo el chat como administrador: solo los socios de la empresa pueden escribir."), false); return; }
            if (r.Muted) { Msg(_chatNote, Tr("El gerente te ha retirado el permiso para escribir en este chat. Puedes seguir leyéndolo."), true); return; }
            int n = _chatInput.Box.TextLength;
            if (_chatEditing != null) { Msg(_chatNote, Tr("Editando tu mensaje · Intro para guardar · Esc para cancelar"), false); return; }
            Msg(_chatNote, n > 400 ? string.Format(Tr("{0} de 500 caracteres"), n)
                : r.AsSuper && !r.Member ? Tr("Escribes como administrador: tus mensajes llevan la marca «Administrador» y los ven todos los socios de la empresa.")
                : Tr("Intro para enviar. Los mensajes los ven todos los socios de la empresa. Clic derecho en uno tuyo: editarlo o eliminarlo."), false);
        }

        void ChatStartEdit(ChatMsg m)
        {
            if (m == null || m.Deleted || !_chatInput.Enabled) return;
            _chatEditing = m;
            _chatInput.Box.Text = m.Body;
            _chatInput.Box.SelectionStart = _chatInput.Box.TextLength;
            _chatSendBtn.Text = Tr("Guardar");
            UpdateChatNote();
            _chatInput.Box.Focus();
        }

        void ChatCancelEdit()
        {
            _chatEditing = null;
            _chatInput.Box.Text = "";
            _chatSendBtn.Text = Tr("Enviar");
            UpdateChatNote();
        }

        async Task SendFromSection()
        {
            var r = ChatSectionRoom();
            string t = _chatInput.Box.Text;
            if (r == null || string.IsNullOrWhiteSpace(t) || !_chatSendBtn.Enabled) return;
            _chatSendBtn.Enabled = false;
            if (_chatEditing != null)
            {
                string e2 = await ChatEditText(r, _chatEditing, t);
                _chatSendBtn.Enabled = r.CanWrite;
                if (e2 != null) { Msg(_chatNote, e2, true); return; }
                ChatCancelEdit();
                Msg(_chatNote, Tr("Mensaje editado."), false);
                return;
            }
            string err = await ChatSendText(r, t);
            _chatSendBtn.Enabled = r.CanWrite;
            if (err != null) { Msg(_chatNote, err, true); return; }
            _chatInput.Box.Text = "";
            _chatView.ScrollToBottom();
            UpdateChatNote();
            _chatInput.Box.Focus();
        }

        void FillChatPerms()
        {
            var r = ChatSectionRoom();
            if (_chatPermList == null || r == null) return;
            _chatPermList.BeginReload(r.CompanyId);
            _chatPermList.ClearRows(); _chatPermIds.Clear();
            foreach (var m in _members)
            {
                bool muted = r.MutedUsers.Contains(m.UserId);
                _chatPermIds.Add(m.UserId);
                _chatPermList.AddRow(new[] { m.Username.Length > 0 ? m.Username : "—", TrRole(m.Role), muted ? Tr("Sin permiso para escribir") : Tr("Puede escribir") },
                    new Color?[] { null, m.Role == "owner" ? Theme.Accent : (Color?)null, muted ? Carne.Red : Carne.Green }, null, m.UserId);
            }
            if (_members.Count == 0) _chatPermList.SetEmpty(Tr("Sin socios."));
            _chatPermList.EndReload();
        }

        async void ChatSetMute(bool mute)
        {
            var r = ChatSectionRoom();
            if (r == null || !r.CanModerate) return;
            int i = _chatPermList.SelectedRow;
            if (i < 0 || i >= _chatPermIds.Count) { Msg(_chatPermMsg, Tr("Selecciona un socio de la lista."), true); return; }
            string uid = _chatPermIds[i];
            var m = _members.Find(x => x.UserId == uid);
            if (uid == Supa.UserId) { Msg(_chatPermMsg, Tr("No puedes cambiar tu propio permiso."), true); return; }
            if (m != null && m.Role == "owner" && !Supa.IsSuperadmin) { Msg(_chatPermMsg, Tr("No puedes retirar el permiso a un gerente."), true); return; }
            Msg(_chatPermMsg, mute ? Tr("Retirando el permiso…") : Tr("Devolviendo el permiso…"), false);
            var (_, err) = await Supa.RpcAsync("chat_set_mute", new { p_company = r.CompanyId, p_user = uid, p_muted = mute });
            if (err != null) { Msg(_chatPermMsg, Tr("Error: ") + ChatErr(err), true); return; }
            string who = m?.Username ?? "";
            Msg(_chatPermMsg, mute ? string.Format(Tr("{0} ya no puede escribir en el chat."), who) : string.Format(Tr("{0} vuelve a poder escribir en el chat."), who), false);
            await ChatLoadState(r);
            FillChatPerms();
            _chatPermList.SelectRow(i);
        }

        // ---------------------------------------------------------------- HUD del chat
        ChatHudOverlay _chatHud;
        bool _chatWaiting, _chatHudManual;
        string _chatHudCompanyId;
        List<(string id, string name, bool canWrite)> _chatMine;   // empresas de las que soy socio

        bool ChatHudAlive => _chatHud != null && !_chatHud.IsDisposed;

        void ShowChatHud(bool force = false)
        {
            if (_prefs == null || !Supa.IsLoggedIn) return;
            CloseChatHud();
            try
            {
                EnsureChatTimer();
                _chatHudManual = false;
                _chatHud = new ChatHudOverlay(_prefs, Supa.UserId)
                {
                    Companies = () => (_chatMine ?? ChatFallbackCompanies()).ConvertAll(x => (x.id, x.name)),
                    CurrentId = () => _chatHudCompanyId,
                    Pick = id => { _chatHudManual = true; SetChatHudCompany(id); },
                    WriteState = ChatHudWriteState,
                    Compose = ComposeFromHud,
                    EditMsg = m => ComposeFromHud(m),
                    DeleteMsg = async m => { if (!string.IsNullOrEmpty(_chatHudCompanyId)) await ChatDeleteMsg(ChatRoomFor(_chatHudCompanyId), m); },
                    CloseRequested = () => { _chatHud?.Hide(); _prefs.ChatHudOn = false; try { _prefs.Save(); } catch { } },
                    Heartbeat = ChatHudFollowService
                };
                var _ = _chatHud.Handle;
                SetChatHudCompany(ChatHudDefault());
                LoadChatMine();
                if (_scenarioReady || force) _chatHud.Show(); else _chatWaiting = true;
            }
            catch { _chatHud = null; }
        }

        void CloseChatHud()
        {
            _chatWaiting = false;
            try { _chatHud?.CloseHud(); } catch { }
            _chatHud = null;
            try { _prefs?.Save(); } catch { }
        }

        void ToggleChatHudFromBar()
        {
            if (_prefs == null) return;
            _chatWaiting = false;
            if (!Supa.IsLoggedIn) { _driveBar?.ShowMessage(Tr("Inicia sesión en Empresas para ver el chat"), true); return; }
            if (!ChatHudAlive) { ShowChatHud(force: true); _prefs.ChatHudOn = ChatHudAlive; }
            else if (_chatHud.Visible) { _chatHud.Hide(); _prefs.ChatHudOn = false; }
            else { _chatHud.Show(); _prefs.ChatHudOn = true; _ = ChatPollOpen(); }
            try { _prefs.Save(); } catch { }
        }

        // Empresa del HUD: con la que estás de servicio; si no, la favorita; si no, la primera.
        string ChatHudDefault()
        {
            var mine = _chatMine ?? ChatFallbackCompanies();
            bool IsMine(string id) => !string.IsNullOrEmpty(id) && (mine.Count == 0 || mine.Exists(x => x.id == id));
            if (_pendingServiceId != null && IsMine(_empOnDutyCompany?.Id)) return _empOnDutyCompany.Id;
            if (IsMine(_favCompanyId)) return _favCompanyId;
            if (IsMine(_prefs?.FavoriteCompany)) return _prefs.FavoriteCompany;
            if (mine.Count > 0) return mine[0].id;
            return _empSel?.Id;
        }

        // Sin la lista del servidor: las empresas cargadas (el superadmin las ve todas, así que no vale para él).
        List<(string id, string name, bool canWrite)> ChatFallbackCompanies()
        {
            var l = new List<(string, string, bool)>();
            if (!Supa.IsSuperadmin) foreach (var c in _empCompanies) l.Add((c.Id, c.Name, true));
            return l;
        }

        async void LoadChatMine()
        {
            var (json, err) = await Supa.RpcAsync("chat_my_companies", new { });
            if (err != null) return;
            try
            {
                var l = new List<(string id, string name, bool canWrite)>();
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                    l.Add((Str(e, "id"), Str(e, "name"), !(e.TryGetProperty("can_write", out var cw) && cw.ValueKind == JsonValueKind.False)));
                _chatMine = l;
            }
            catch { return; }
            if (!_chatHudManual) SetChatHudCompany(ChatHudDefault());
        }

        // Cada segundo: si te pones de servicio con otra empresa (y no has elegido tú otra), el HUD la sigue.
        string _chatLastDefault;
        void ChatHudFollowService()
        {
            if (!ChatHudAlive) return;
            string def = ChatHudDefault();
            if (def != _chatLastDefault) { _chatLastDefault = def; if (!_chatHudManual && def != _chatHudCompanyId) SetChatHudCompany(def); }
        }

        async void SetChatHudCompany(string id)
        {
            if (string.IsNullOrEmpty(id)) { RefreshChatHud(); return; }
            _chatHudCompanyId = id;
            _chatLastDefault ??= id;
            var r = ChatRoomFor(id, ChatCompanyName(id));
            RefreshChatHud();
            if (!r.StateLoaded) await ChatLoadState(r);
            await ChatFetch(r);
            RefreshChatHud();
        }

        string ChatCompanyName(string id)
        {
            var m = (_chatMine ?? ChatFallbackCompanies()).Find(x => x.id == id);
            if (!string.IsNullOrEmpty(m.name)) return m.name;
            return _empCompanies.Find(c => c.Id == id)?.Name ?? "";
        }

        void RefreshChatHud()
        {
            if (!ChatHudAlive) return;
            if (string.IsNullOrEmpty(_chatHudCompanyId)) { _chatHud.SetRoom(Tr("Chat"), new List<ChatMsg>(), Tr("No perteneces a ninguna empresa.")); return; }
            var r = ChatRoomFor(_chatHudCompanyId);
            if (string.IsNullOrEmpty(r.Name)) r.Name = ChatCompanyName(r.CompanyId);
            _chatHud.SetRoom(r.Name, r.Msgs, r.Error ?? (r.Loaded ? null : Tr("Cargando el chat…")));
        }

        (bool can, string why) ChatHudWriteState()
        {
            if (string.IsNullOrEmpty(_chatHudCompanyId)) return (false, "");
            var r = ChatRoomFor(_chatHudCompanyId);
            if (r.Error != null) return (false, "");
            if (!r.Member && !r.AsSuper) return (false, Tr("Solo los socios pueden escribir"));
            if (r.Muted) return (false, Tr("No tienes permiso para escribir en este chat"));
            return (true, null);
        }

        void ComposeFromHud() => ComposeFromHud(null);

        // Cajita de escribir del HUD; con un mensaje, para editarlo.
        void ComposeFromHud(ChatMsg edit)
        {
            if (!ChatHudAlive || string.IsNullOrEmpty(_chatHudCompanyId)) return;
            var r = ChatRoomFor(_chatHudCompanyId);
            try
            {
                var box = edit == null
                    ? new ChatComposeBox(_chatHud.ComposeScreenRect(), r.Name, t => ChatSendText(r, t))
                    : new ChatComposeBox(_chatHud.ComposeScreenRect(), r.Name, t => ChatEditText(r, edit, t), edit.Body);
                box.Show();
            }
            catch { }
        }
    }
}
