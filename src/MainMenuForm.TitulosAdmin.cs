// Revisión → «Títulos» (certificación, 2026-11-08, solo el administrador; sql/certificacion.sql):
//   · quién tiene qué título (maquinista B/A/A+, jefe de circulación o en prácticas), los suspendidos y por qué, sus exámenes;
//   · suspender un título (aunque cumpla todo) o devolverlo, y quitar un examen aprobado (tendrá que repetirlo);
//   · «Preguntas del examen…»: el banco de preguntas, con la respuesta correcta, para editarlas, añadir o retirar;
//   · «Otorgar título…» (2026-11-08): dar o quitar un título a cualquiera, por su nombre o su ID, sin examen
//     (sql/titulos-otorgar.sql: admin_cert_granted; guarda admin_cert_grant de sql/titulos-otorgados.sql).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        const int RevTabTitles = 8;
        const string RevIntroTitles = "Los títulos de maquinista y de jefe de circulación: quién los tiene, los suspendidos y sus exámenes. Puedes suspender o devolver un título, quitar un examen aprobado y editar las preguntas.";
        Panel _certPanel;
        CardTable _certList;
        EmojiLabel _certDetail;
        Label _certMsg;
        sealed class CertRow { public string Id, User, Driver, Jefe, Susp, Note, DrvAt, JefAt, LastExam; public bool AdminSusp; public double Hours; public int Attempts, LastScore, LastTotal; }
        readonly List<CertRow> _certRows = new(), _certShown = new();

        Panel BuildCertAdminPanel()
        {
            _certPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            _certList = new CardTable { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0), Icon = "🎓", CardHeight = 70, MinWidth = 360, TitleCol = 0, SubCols = new[] { 1 }, PillCol = 2, AutoHeight = true };
            _certList.SelectedIndexChanged += (s, e) => ShowCertRow();
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0) };
            _certDetail = new EmojiLabel { Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Font(9.5f), Text = Tr("Elige un usuario de la lista.") };
            var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Surface };
            RoundButton B(string text, Action a, bool primary = false) { var b = EmpButton(Tr(text), primary); b.Width = 190; b.Margin = new Padding(0, 8, 8, 2); b.Click += (s, e) => a(); btns.Controls.Add(b); return b; }
            B("Suspender…", () => _ = CertAdminAct("suspend"));
            B("Devolver el título", () => _ = CertAdminAct("restore"));
            B("Quitar examen de maquinista", () => _ = CertAdminAct("reset_driver"));
            B("Quitar examen de jefe", () => _ = CertAdminAct("reset_jefe"));
            B("Otorgar título…", () => _ = CertGrant());
            B("Preguntas del examen…", () => _ = OpenCertQuestions(), true);
            _certMsg = EmpMsg(); _certMsg.MaximumSize = new Size(420, 0);
            btns.Controls.Add(_certMsg);
            right.Controls.Add(_certDetail); right.Controls.Add(btns);
            t.Controls.Add(_certList, 0, 0); t.Controls.Add(right, 1, 0);
            _certPanel.Controls.Add(t);
            return _certPanel;
        }

        async void LoadCertAdmin()
        {
            if (!Supa.IsSuperadmin || _certList == null) return;
            var (json, err) = await Supa.RpcAsync("admin_cert_list", new { });
            _certRows.Clear();
            if (err != null) { _certList.SetEmpty(MissingRpc(err) ? Tr("El servidor aún no tiene los títulos (falta certificacion.sql).") : Tr("Error: ") + err); return; }
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                    _certRows.Add(new CertRow
                    {
                        Id = Str(e, "user_id"), User = Str(e, "username").Length > 0 ? Str(e, "username") : "—", Driver = Str(e, "driver"), Jefe = Str(e, "jefe"),
                        Susp = Str(e, "suspended"), AdminSusp = e.TryGetProperty("admin_suspended", out var a) && a.ValueKind == JsonValueKind.True, Note = Str(e, "admin_note"),
                        DrvAt = Str(e, "driver_exam_at"), JefAt = Str(e, "jefe_exam_at"), Hours = Num(e, "jefe_hours"), Attempts = (int)Num(e, "attempts"),
                        LastScore = (int)Num(e, "last_score"), LastTotal = (int)Num(e, "last_total"), LastExam = Str(e, "last_exam")
                    });
            }
            catch { }
            FillCertAdmin();
        }

        static string CertLabel(string driver, string jefe)
        {
            var p = new List<string>();
            if (!string.IsNullOrEmpty(driver)) p.Add("🎓 " + driver);
            if (jefe == "titulo") p.Add("👮 " + Tr("Jefe de circulación")); else if (jefe == "practicas") p.Add("👮 " + Tr("en prácticas"));
            return p.Count == 0 ? Tr("Sin título") : string.Join("  ·  ", p);
        }

        void FillCertAdmin()
        {
            if (_certList == null) return;
            var words = _revQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _certList.ClearRows(); _certShown.Clear();
            foreach (var r in _certRows)
            {
                if (words.Any(w => r.User.ToLowerInvariant().IndexOf(w, StringComparison.Ordinal) < 0)) continue;
                _certShown.Add(r);
                string sub = string.Format(Tr("{0} exámenes entregados"), r.Attempts) + (r.Hours > 0 ? "  ·  " + string.Format(Tr("{0} h de jefe"), r.Hours.ToString("N1", I18n.Culture)) : "");
                string pill = r.Susp.Length > 0 ? Tr("Suspendido") : CertLabel(r.Driver, r.Jefe);
                _certList.AddRow(new[] { r.User, sub, pill }, new Color?[] { null, null, r.Susp.Length > 0 ? Carne.Red : r.Driver.Length > 0 || r.Jefe.Length > 0 ? Carne.Gold : Theme.Subtle }, null, r.Id);
            }
            if (_certShown.Count == 0) _certList.SetEmpty(Tr("Nadie se ha examinado todavía."));
            ShowCertRow();
        }

        CertRow SelectedCert() { int i = _certList?.SelectedRow ?? -1; return i >= 0 && i < _certShown.Count ? _certShown[i] : null; }

        void ShowCertRow()
        {
            if (_certDetail == null) return;
            var r = SelectedCert();
            if (r == null) { _certDetail.Text = Tr("Elige un usuario de la lista."); return; }
            string D(string iso) => DateTime.TryParse(iso, out var d) ? d.ToLocalTime().ToString("g", I18n.Culture) : "—";
            var l = new List<string> { r.User, "", CertLabel(r.Driver, r.Jefe) };
            if (r.Susp.Length > 0) l.Add("⚠  " + string.Format(Tr("Suspendido: {0}."), CertWhy(r.Susp)) + (r.Note.Length > 0 ? "  «" + r.Note + "»" : ""));
            l.Add("");
            l.Add(string.Format(Tr("Examen de maquinista: {0}"), r.DrvAt.Length > 0 ? D(r.DrvAt) : Tr("sin aprobar")));
            l.Add(string.Format(Tr("Examen de jefe: {0}"), r.JefAt.Length > 0 ? D(r.JefAt) : Tr("sin aprobar")));
            l.Add(string.Format(Tr("Horas con el mando en la sala: {0}"), r.Hours.ToString("N1", I18n.Culture)));
            l.Add(string.Format(Tr("Exámenes entregados: {0}"), r.Attempts) + (r.LastTotal > 0 ? "  ·  " + string.Format(Tr("el último ({0}): {1} de {2}"), Tr(r.LastExam == "jefe" ? "jefe" : "maquinista"), r.LastScore, r.LastTotal) : ""));
            _certDetail.Text = string.Join("\n", l);
        }

        async Task CertAdminAct(string action)
        {
            var r = SelectedCert();
            if (r == null) { Msg(_certMsg, Tr("Elige un usuario de la lista."), true); return; }
            string note = null;
            if (action == "suspend")
            {
                using var dlg = new FormDialog(Tr("Suspender el título"), Tr("Suspender"), 520, "⚠", r.User);
                dlg.AddMultiline("note", Tr("Motivo"), "", 80);
                dlg.AddInfo(Tr("El título queda suspendido aunque cumpla todo, hasta que lo devuelvas. Sin título no puede tomar el mando de jefe (salvo que esté autorizado a mano)."));
                dlg.Validate = async f => { await Task.CompletedTask; return f.Get("note").Trim().Length < 3 ? Tr("Escribe el motivo.") : null; };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                note = dlg.Get("note").Trim();
            }
            else if (action.StartsWith("reset") && ThemedBox.Show(this, string.Format(Tr("¿Quitar a {0} el examen aprobado? Tendrá que volver a hacerlo."), r.User), Tr("Títulos"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var (_, err) = await Supa.RpcAsync("admin_cert_set", new { p_user = r.Id, p_action = action, p_note = note });
            if (err != null) { Msg(_certMsg, Tr("Error: ") + CleanRpcErr(err), true); return; }
            Msg(_certMsg, Tr("Hecho."), false);
            LoadCertAdmin();
        }

        // Otorgar (o quitar) un título sin examen: primero quién (el elegido en la lista o cualquiera por nombre o ID),
        // luego qué, con lo que ya tiene otorgado puesto. El nivel de maquinista que cuenta es el más alto entre el
        // otorgado y el ganado con el examen y la práctica.
        async Task CertGrant()
        {
            var sel = SelectedCert();
            string who;
            using (var d1 = new FormDialog(Tr("Otorgar título"), Tr("Siguiente"), 480, "🎓", Tr("Sin examen: lo da el administrador")))
            {
                var box = d1.AddText("u", Tr("Nombre de jugador o ID"), sel?.User ?? "");
                d1.Validate = async f => { await Task.CompletedTask; return f.Get("u").Trim().Length == 0 ? Tr("Escribe el nombre o el ID.") : null; };
                d1.Shown += (s, e) => { try { box.Box.Focus(); box.Box.SelectAll(); } catch { } };
                if (d1.ShowDialog(this) != DialogResult.OK) return;
                who = d1.Get("u").Trim();
            }
            var (json, err) = await Supa.RpcAsync("admin_cert_granted", new { p_name = who });
            if (err != null) { Msg(_certMsg, MissingRpc(err) ? Tr("El servidor aún no tiene esta opción (falta titulos-otorgar.sql).") : Tr("Error: ") + CleanRpcErr(err), true); return; }
            string id, name, drv; bool jefe;
            try
            {
                using var d = JsonDocument.Parse(json); var e = d.RootElement;
                id = Str(e, "user_id"); name = Str(e, "username"); drv = Str(e, "driver");
                jefe = e.TryGetProperty("jefe", out var j) && j.ValueKind == JsonValueKind.True;
            }
            catch { Msg(_certMsg, Tr("Error: ") + Tr("respuesta no válida"), true); return; }
            string[] lv = { "", "B", "A", "A+" };
            using var d2 = new FormDialog(Tr("Otorgar título"), Tr("Guardar"), 520, "🎓", name);
            d2.AddCombo("d", Tr("Maquinista"), new[] { Tr("Sin otorgar"), "B", "A", "A+" }, Math.Max(0, Array.IndexOf(lv, drv ?? "")));
            d2.AddCombo("j", Tr("Jefe de circulación"), new[] { Tr("Sin otorgar"), Tr("Otorgado") }, jefe ? 1 : 0);
            d2.AddInfo(Tr("Cuenta el nivel más alto entre el otorgado y el que gane con el examen y la práctica. «Sin otorgar» quita lo que le diste, no lo que haya ganado él."));
            if (d2.ShowDialog(this) != DialogResult.OK) return;
            string nd = lv[Math.Max(0, d2.Index("d"))]; bool nj = d2.Index("j") == 1;
            var (_, err2) = await Supa.RpcAsync("admin_cert_grant", new { p_user = id, p_driver = nd.Length == 0 ? null : nd, p_jefe = nj });
            if (err2 != null) { Msg(_certMsg, Tr("Error: ") + CleanRpcErr(err2), true); return; }
            Msg(_certMsg, string.Format(Tr("Guardado: {0}."), name), false);
            LoadCertAdmin();
        }

        // ── el banco de preguntas
        sealed class CertQ { public int Id; public string Exam, Topic, QEs, QEn; public string[] OEs = new string[4], OEn = new string[4]; public int Correct; public bool Active; }

        async Task OpenCertQuestions()
        {
            var qs = new List<CertQ>();
            async Task<string> Load()
            {
                var (json, err) = await Supa.RpcAsync("admin_cert_questions", new { });
                if (err != null) return CleanRpcErr(err);
                qs.Clear();
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    var q = new CertQ { Id = (int)Num(e, "id"), Exam = Str(e, "exam"), Topic = Str(e, "topic"), QEs = Str(e, "q_es"), QEn = Str(e, "q_en"), Correct = (int)Num(e, "correct"),
                                        Active = !e.TryGetProperty("active", out var a) || a.ValueKind != JsonValueKind.False };
                    int k = 0; foreach (var o in e.GetProperty("options_es").EnumerateArray()) if (k < 4) q.OEs[k++] = o.GetString();
                    k = 0; foreach (var o in e.GetProperty("options_en").EnumerateArray()) if (k < 4) q.OEn[k++] = o.GetString();
                    qs.Add(q);
                }
                return null;
            }
            string e0 = await Load();
            if (e0 != null) { ThemedBox.Show(this, e0, Tr("Preguntas del examen")); return; }
            using var f = new Form
            {
                Text = Tr("Preguntas del examen"), BackColor = Theme.Bg, ForeColor = Theme.Text, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = true, ShowInTaskbar = false, KeyPreview = true, Font = Theme.Font(9.5f), FormBorderStyle = FormBorderStyle.Sizable,
            };
            DialogKit.Dress(f, false);
            f.ClientSize = new Size(Theme.Px(980), Theme.Px(700));
            var list = new CardTable { Dock = DockStyle.Fill, Margin = new Padding(0), Icon = "❓", CardHeight = 76, MinWidth = 420, TitleCol = 0, SubCols = new[] { 1 }, QuoteCol = 2, PillCol = 3, QuoteMarks = false, AutoHeight = true };
            var shown = new List<CertQ>();
            var filter = new RoundedInput(Tr("🔎  Filtrar…")) { Width = 260, Height = 34, Dock = DockStyle.Right };
            var msg = EmpMsg();
            void Fill()
            {
                var words = filter.Box.Text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                list.ClearRows(); shown.Clear();
                foreach (var q in qs)
                {
                    string all = (q.QEs + " " + q.Topic + " " + string.Join(" ", q.OEs)).ToLowerInvariant();
                    if (words.Any(w => all.IndexOf(w, StringComparison.Ordinal) < 0)) continue;
                    shown.Add(q);
                    list.AddRow(new[] { "#" + q.Id + "  " + q.QEs, (q.Exam == "jefe" ? Tr("Jefe de circulación") : Tr("Maquinista")) + "  ·  " + q.Topic, "✅ " + q.OEs[Math.Max(0, Math.Min(3, q.Correct))], q.Active ? Tr("Activa") : Tr("Retirada") },
                                new Color?[] { null, null, Color.FromArgb(129, 199, 132), q.Active ? Theme.Subtle : Carne.Red }, null, q.Id.ToString());
                }
                if (shown.Count == 0) list.SetEmpty(Tr("No hay preguntas."));
                msg.Text = string.Format(Tr("{0} de maquinista y {1} de jefe activas"), qs.Count(q => q.Active && q.Exam == "driver"), qs.Count(q => q.Active && q.Exam == "jefe"));
            }
            filter.Box.TextChanged += (s, e) => Fill();
            async Task Edit(CertQ q)
            {
                bool nw = q == null; q ??= new CertQ { Exam = "driver", Active = true };
                using var dlg = new FormDialog(nw ? Tr("Pregunta nueva") : Tr("Editar la pregunta"), Tr("Guardar"), 760, "❓", nw ? null : "#" + q.Id);
                dlg.AddCombo("exam", Tr("Examen"), new[] { Tr("Maquinista"), Tr("Jefe de circulación") }, q.Exam == "jefe" ? 1 : 0);
                dlg.AddText("topic", Tr("Tema"), q.Topic ?? "");
                dlg.AddMultiline("qes", Tr("Pregunta (español)"), q.QEs ?? "", 60);
                for (int i = 0; i < 4; i++) dlg.AddText("oes" + i, string.Format(Tr("Respuesta {0} (español)"), i + 1), q.OEs[i] ?? "");
                dlg.AddMultiline("qen", Tr("Pregunta (inglés)"), q.QEn ?? "", 60);
                for (int i = 0; i < 4; i++) dlg.AddText("oen" + i, string.Format(Tr("Respuesta {0} (inglés)"), i + 1), q.OEn[i] ?? "");
                dlg.AddCombo("ok", Tr("La correcta"), new[] { "1", "2", "3", "4" }, Math.Max(0, Math.Min(3, q.Correct)));
                dlg.AddCombo("act", Tr("Estado"), new[] { Tr("Activa"), Tr("Retirada") }, q.Active ? 0 : 1);
                dlg.Validate = async fd =>
                {
                    string qes = fd.Get("qes").Trim(), qen = fd.Get("qen").Trim();
                    var oes = Enumerable.Range(0, 4).Select(i => fd.Get("oes" + i).Trim()).ToArray();
                    var oen = Enumerable.Range(0, 4).Select(i => fd.Get("oen" + i).Trim()).ToArray();
                    if (qes.Length < 5 || qen.Length < 5) return Tr("Escribe la pregunta en los dos idiomas.");
                    if (oes.Any(x => x.Length == 0) || oen.Any(x => x.Length == 0)) return Tr("Faltan respuestas: son cuatro en cada idioma.");
                    var (_, err) = await Supa.RpcAsync("admin_cert_question_save", new
                    {
                        p_id = nw ? (int?)null : q.Id, p_exam = fd.Index("exam") == 1 ? "jefe" : "driver", p_topic = fd.Get("topic").Trim(),
                        p_q_es = qes, p_q_en = qen, p_options_es = oes, p_options_en = oen, p_correct = Math.Max(0, fd.Index("ok")), p_active = fd.Index("act") == 0
                    });
                    return err != null ? Tr("Error: ") + CleanRpcErr(err) : null;
                };
                if (dlg.ShowDialog(f) == DialogResult.OK) { await Load(); Fill(); }
            }
            CertQ Sel() { int i = list.SelectedRow; return i >= 0 && i < shown.Count ? shown[i] : null; }
            list.ItemActivated += async _ => { if (Sel() is CertQ q) await Edit(q); };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Surface, Padding = new Padding(12, 6, 12, 8) };
            var bNew = EmpButton(Tr("Nueva pregunta"), primary: true); bNew.Width = 180; bNew.Click += async (s, e) => await Edit(null);
            var bEd = EmpButton(Tr("Editar")); bEd.Width = 140; bEd.Margin = new Padding(8, 10, 2, 2); bEd.Click += async (s, e) => { if (Sel() is CertQ q) await Edit(q); };
            var bCl = EmpButton(Tr("Cerrar")); bCl.Width = 120; bCl.Margin = new Padding(8, 10, 2, 2); bCl.Click += (s, e) => f.Close();
            msg.Margin = new Padding(14, 20, 2, 2);
            bottom.Controls.Add(bNew); bottom.Controls.Add(bEd); bottom.Controls.Add(bCl); bottom.Controls.Add(msg);
            var top = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Theme.Bg, Padding = new Padding(12, 5, 12, 5) };
            top.Controls.Add(filter);
            f.Controls.Add(list); f.Controls.Add(bottom); f.Controls.Add(top);
            DialogKit.Crown(f, Tr("Preguntas del examen"), Tr("Doble clic en una para editarla. Las retiradas no salen en los exámenes."), "❓");
            Fill();
            f.ShowDialog(this);
        }
    }
}
