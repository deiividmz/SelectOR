// Empresas → Normas: el reglamento interno de la empresa. El gerente y los gestores crean, editan,
// ordenan y borran normas; todos los socios las leen (y reciben un aviso al publicarse una nueva).
// Servidor: normas-y-estadisticas.sql.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        const int NormasSubtab = 13, PrestamosSubtab = 14;
        Panel _rulesPanel;
        RulesView _rulesView;
        Label _rulesMsg;
        FlowLayoutPanel _rulesBtns;
        RoundButton _ruleEditBtn, _ruleUpBtn, _ruleDownBtn, _ruleDelBtn;
        readonly List<RuleItem> _rulesAll = new();
        int _rulesCat = -1;           // -1 = todas
        string _rulesQuery = "";
        int _rulesSeq;

        Panel BuildRulesSubpanel()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // explicación
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // categorías + búsqueda
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // normas
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // acciones

            var intro = EmpIntro("El reglamento interno de la empresa: lo que todos los socios deben cumplir. El gerente y los gestores pueden crear, editar, ordenar y borrar normas; al publicar una nueva, los socios reciben un aviso.");
            intro.MaximumSize = new Size(900, 0);
            t.Controls.Add(intro);

            var top = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 42, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 6) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var names = new List<string> { "Todas" };
            names.AddRange(RulesView.Categories.Select(c => c.label));
            var tabs = MakeSubTabs(names.ToArray(), i => { _rulesCat = i - 1; FillRules(); });
            tabs.Dock = DockStyle.Fill; tabs.Margin = new Padding(0);
            var search = new RoundedInput(I18n.T("🔎  Filtrar…")) { Width = 240, Height = 34, Anchor = AnchorStyles.Right, Margin = new Padding(10, 3, 0, 3) };
            search.Box.TextChanged += (s, e) => { _rulesQuery = search.Box.Text.Trim().ToLowerInvariant(); FillRules(); };
            top.Controls.Add(tabs, 0, 0); top.Controls.Add(search, 1, 0);
            t.Controls.Add(top);

            _rulesView = new RulesView { Dock = DockStyle.Fill, Margin = new Padding(2, 4, 2, 4), EmptyText = Tr("Esta empresa todavía no tiene normas.") };
            _rulesView.SelectionChanged += UpdateRuleButtons;
            t.Controls.Add(_rulesView);

            _rulesBtns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0) };
            var add = EmpButton(Tr("Nueva norma"), primary: true); add.Width = 170;
            add.Click += (s, e) => EditRule(null);
            _ruleEditBtn = EmpButton(Tr("Editar")); _ruleEditBtn.Width = 120; _ruleEditBtn.Margin = new Padding(8, 10, 2, 2);
            _ruleEditBtn.Click += (s, e) => EditRule(_rulesView.Selected);
            _ruleUpBtn = EmpButton("▲  " + Tr("Subir")); _ruleUpBtn.Width = 110; _ruleUpBtn.Margin = new Padding(8, 10, 2, 2);
            _ruleUpBtn.Click += (s, e) => MoveRule(-1);
            _ruleDownBtn = EmpButton("▼  " + Tr("Bajar")); _ruleDownBtn.Width = 110; _ruleDownBtn.Margin = new Padding(8, 10, 2, 2);
            _ruleDownBtn.Click += (s, e) => MoveRule(1);
            _ruleDelBtn = EmpButton(Tr("Eliminar")); _ruleDelBtn.Width = 120; _ruleDelBtn.Margin = new Padding(8, 10, 2, 2);
            _ruleDelBtn.BaseColor = Theme.Surface2; _ruleDelBtn.HoverColor = Color.FromArgb(150, 60, 60); _ruleDelBtn.TextColor = RedC;
            _ruleDelBtn.Click += (s, e) => DeleteRule();
            _rulesBtns.Controls.AddRange(new Control[] { add, _ruleEditBtn, _ruleUpBtn, _ruleDownBtn, _ruleDelBtn });
            _rulesMsg = EmpMsg(); _rulesMsg.Margin = new Padding(12, 20, 2, 2); _rulesMsg.MaximumSize = new Size(500, 0);
            _rulesBtns.Controls.Add(_rulesMsg);
            t.Controls.Add(_rulesBtns);
            return t;
        }

        void UpdateRuleButtons()
        {
            bool can = CanManage() || Supa.IsSuperadmin;
            if (_rulesBtns != null) foreach (Control c in _rulesBtns.Controls) if (c is RoundButton) c.Visible = can;
            bool sel = _rulesView?.Selected != null, filtered = _rulesCat >= 0 || _rulesQuery.Length > 0;
            if (_ruleEditBtn != null) _ruleEditBtn.Enabled = sel;
            if (_ruleDelBtn != null) _ruleDelBtn.Enabled = sel;
            if (_ruleUpBtn != null) _ruleUpBtn.Enabled = sel && !filtered;     // se ordena viendo todas
            if (_ruleDownBtn != null) _ruleDownBtn.Enabled = sel && !filtered;
        }

        async void LoadRules(string selectId = null)
        {
            var c = _empSel;
            if (c == null || _rulesView == null) return;
            int seq = ++_rulesSeq;
            if (_rulesAll.Count == 0) _rulesView.EmptyText = Tr("Cargando…");
            var (json, err) = await Supa.RpcAsync("list_company_rules", new { p_company = c.Id });
            if (seq != _rulesSeq || _empSel?.Id != c.Id) return;
            _rulesAll.Clear();
            if (err != null)
                _rulesView.EmptyText = err.Contains("PGRST202") ? Tr("El servidor aún no tiene las normas (falta normas-y-estadisticas.sql).") : Tr("Error: ") + err;
            else
            {
                _rulesView.EmptyText = Tr("Esta empresa todavía no tiene normas.") + ((CanManage() || Supa.IsSuperadmin) ? "\n" + Tr("Pulsa «Nueva norma» para escribir la primera.") : "");
                try
                {
                    using var d = JsonDocument.Parse(json);
                    int n = 0;
                    foreach (var e in d.RootElement.EnumerateArray())
                        _rulesAll.Add(new RuleItem
                        {
                            Id = Str(e, "id"), Title = Str(e, "title"), Body = Str(e, "body"), Category = Str(e, "category"),
                            By = Str(e, "created_by_name"), EditedBy = Str(e, "updated_by_name"),
                            When = FmtDate(Str(e, "created_at")), EditedWhen = FmtDate(Str(e, "updated_at")), Number = ++n
                        });
                }
                catch { }
            }
            FillRules();
            if (selectId != null) _rulesView.Select(selectId);
        }

        void FillRules()
        {
            if (_rulesView == null) return;
            var words = _rulesQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var list = _rulesAll.Where(r =>
            {
                if (_rulesCat >= 0 && r.Category != RulesView.Categories[_rulesCat].key) return false;
                string s = (r.Title + " " + r.Body + " " + RulesView.Cat(r.Category).label).ToLowerInvariant();
                return words.All(w => s.Contains(w));
            });
            _rulesView.SetItems(list);
            UpdateRuleButtons();
        }

        void EditRule(RuleItem r)
        {
            if (_empSel == null || !(CanManage() || Supa.IsSuperadmin)) return;
            var cats = RulesView.Categories.Select(c => Tr(c.label)).ToList();
            int ci = Math.Max(0, Array.FindIndex(RulesView.Categories, c => c.key == (r?.Category ?? (_rulesCat >= 0 ? RulesView.Categories[_rulesCat].key : "general"))));
            string companyId = _empSel.Id;
            string savedId = null;
            using var dlg = new FormDialog(r == null ? Tr("Nueva norma") : Tr("Editar norma"), r == null ? Tr("Publicar") : Tr("Guardar"), 620);
            dlg.AddText("title", Tr("Título"), r?.Title ?? "", Tr("Por ejemplo: Respetar siempre las señales"));
            dlg.AddCombo("cat", Tr("Categoría"), cats, ci);
            dlg.AddMultiline("body", Tr("Texto de la norma"), r?.Body ?? "", 180);
            if (r == null) dlg.AddInfo(Tr("Al publicarla, todos los socios recibirán un aviso."));
            dlg.Validate = async f =>
            {
                string title = f.Get("title"), body = f.Get("body");
                int k = f.Index("cat");
                if (title.Length < 3) return Tr("El título es demasiado corto.");
                if (title.Length > 120) return Tr("El título es demasiado largo (120 caracteres como mucho).");
                if (body.Length > 4000) return Tr("El texto es demasiado largo (4000 caracteres como mucho).");
                var (json, err) = await Supa.RpcAsync("save_company_rule", new
                {
                    p_company = companyId, p_id = r?.Id, p_title = title, p_body = body,
                    p_category = RulesView.Categories[Math.Max(0, k)].key
                });
                if (err != null) return Tr("Error: ") + err;
                savedId = JsonText(json);
                return null;
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            Msg(_rulesMsg, r == null ? Tr("Norma publicada.") : Tr("Norma guardada."), false);
            LoadRules(savedId ?? r?.Id);
        }

        async void MoveRule(int dir)
        {
            var r = _rulesView?.Selected; if (r == null) return;
            var (_, err) = await Supa.RpcAsync("move_company_rule", new { p_id = r.Id, p_dir = dir });
            if (err != null) { Msg(_rulesMsg, Tr("Error: ") + err, true); return; }
            LoadRules(r.Id);
        }

        async void DeleteRule()
        {
            var r = _rulesView?.Selected; if (r == null) return;
            if (MessageBox.Show(this, string.Format(Tr("¿Eliminar la norma «{0}»?"), r.Title), "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var (_, err) = await Supa.RpcAsync("delete_company_rule", new { p_id = r.Id });
            if (err != null) { Msg(_rulesMsg, Tr("Error: ") + err, true); return; }
            Msg(_rulesMsg, Tr("Norma eliminada."), false);
            LoadRules();
        }
    }
}
