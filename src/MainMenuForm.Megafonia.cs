// Empresas → Megafonía (gestor). Aquí se prepara lo que oirá el maquinista:
//  · el AVISO de cada estación (la frase entera, ya grabada), que puede ser BASE (vale para toda
//    la ruta) o PROPIO DE UNA LÍNEA: la misma estación suena distinto según la línea que lleve
//    el tren, y
//  · las LÍNEAS, con las estaciones en las que paran (el orden da igual: el aviso salta por
//    proximidad y sentido de marcha, así que de la línea solo importa qué estaciones la forman).
// Además de las rutas instaladas, el desplegable trae las que tienen megafonía guardada en la empresa aunque
// no estén en este equipo (megafonias-sin-ruta.sql): se ve y se edita lo grabado, sin las estaciones del .tdb.
// La sección solo existe si el superadmin ha habilitado la megafonía de esa empresa
// (companies.pa_enabled); el superadmin la ve siempre, con el interruptor arriba.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        Panel _paPanel, _paStPanel, _paLinePanel;
        CardTable _paStList, _paLineList;
        Label _paMsg, _paHint;
        ComboBox _paRouteBox, _paLineBox;
        CheckBox _paOnChk;
        RoundButton _paUpBtn, _paTestBtn, _paDelBtn, _paCfgBtn,
                    _paLineNewBtn, _paLineRenBtn, _paLineStopsBtn, _paLineDelBtn;

        sealed class PaRow
        {
            public string Norm, Display;        // clave (normalizada) y nombre legible
            public int Radius = 800, Lead = 20;
            public bool InRoute;                // sale del .tdb de la ruta
            public bool LineStop;               // para en la línea elegida
        }

        // Una ruta del desplegable: instalada (con su carpeta) o solo con megafonía guardada en el servidor.
        sealed class PaRouteItem
        {
            public string Id, Name, Dir;
            public bool Local => Dir != null;
            public override string ToString() => Local ? Name : Name + "   ·  " + I18n.T("no instalada en este equipo");
        }
        readonly List<PaRouteItem> _paRouteItems = new List<PaRouteItem>();
        int _paRemoteSeq;

        sealed class PaLine
        {
            public string Id, Name;
            public readonly List<string> Stops = new List<string>();   // estaciones en las que para
            public override string ToString() => Name;
        }

        readonly List<PaRow> _paRows = new List<PaRow>();
        readonly List<PaLine> _paLines = new List<PaLine>();
        // Audios: clave «estación|tipo|línea» (línea vacía = aviso base de la ruta).
        readonly Dictionary<string, (string path, string stamp)> _paAudio =
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        string _paRoute;        // ruta elegida: su RouteID (lo que la identifica en el servidor)
        string _paRouteName;    // su nombre (el que se ve; y para trasladar lo grabado con él)
        string _paRouteDir;     // carpeta de esa ruta (para leer sus estaciones del .tdb)
        string _paLineId;       // línea elegida en el desplegable (null = base, para todas)
        // Preaviso/antelación PROPIOS de una línea en una estación («línea|estación» → metros, s).
        // Sin entrada, la línea usa los de la estación.
        readonly Dictionary<string, (int radius, int lead)> _paLineCfg = new(StringComparer.OrdinalIgnoreCase);
        static string LineCfgKey(string line, string norm) => (line ?? "") + "|" + (norm ?? "");
        bool _paBusy, _paFillingLines;

        bool PaEnabledHere() => _empSel != null && _empSel.PaEnabled;

        static string AudioKey(string station, string kind, string line)
            => station + "|" + kind + "|" + (line ?? "");

        // Clip que sonaría para esa estación con la línea elegida: primero el propio de la línea,
        // si no, el base de la ruta. `own` dice cuál de los dos se ha usado.
        (string path, string stamp, bool own) StationClip(string station, string lineId)
        {
            if (!string.IsNullOrEmpty(lineId) && _paAudio.TryGetValue(AudioKey(station, "nombre", lineId), out var a) && !string.IsNullOrEmpty(a.path))
                return (a.path, a.stamp, true);
            if (_paAudio.TryGetValue(AudioKey(station, "nombre", null), out var b) && !string.IsNullOrEmpty(b.path))
                return (b.path, b.stamp, false);
            return (null, null, false);
        }

        // ============================ Construcción ============================

        Panel BuildPaSubpanel()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // ruta + línea + interruptor
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // explicación
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // sub-pestañas
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // contenido
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // mensaje

            // ---- barra superior: ruta · línea · (superadmin) interruptor ----
            // Rejilla (no flujo) para que los desplegables crezcan con la ventana y las etiquetas
            // queden alineadas con ellos.
            var top = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 6, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(2, 6, 2, 8)
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));            // «Ruta»
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));       // desplegable de ruta
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));            // «Línea»
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));       // desplegable de línea
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));            // interruptor
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));        // hueco: todo pegado a la izquierda

            top.Controls.Add(PaFieldLabel(Tr("Ruta")), 0, 0);
            _paRouteBox = PaCombo();
            _paRouteBox.SelectedIndexChanged += (s, e) => OnPaRouteChanged();
            top.Controls.Add(_paRouteBox, 1, 0);

            top.Controls.Add(PaFieldLabel(Tr("Línea")), 2, 0);
            _paLineBox = PaCombo();
            _paLineBox.SelectedIndexChanged += (s, e) => OnPaLineChanged();
            top.Controls.Add(_paLineBox, 3, 0);

            _paOnChk = new CheckBox
            {
                Text = Tr("Megafonía habilitada"), AutoSize = true, FlatStyle = FlatStyle.Flat,
                ForeColor = Theme.Text, BackColor = Theme.Bg, Anchor = AnchorStyles.Left,
                Margin = new Padding(14, 8, 2, 4), Visible = false
            };
            _paOnChk.Click += (s, e) => TogglePaEnabled();   // Click (no CheckedChanged): no dispara al rellenar
            top.Controls.Add(_paOnChk, 4, 0);
            t.Controls.Add(top);

            _paHint = PaNote(Tr("El audio de cada estación es el aviso entero, tal como sonará al acercarse a ella. Con «Base (todas las líneas)» grabas el que vale para toda la ruta; eligiendo una línea grabas el suyo propio, que sustituye al base cuando el maquinista lleva esa línea. MP3 o WAV, hasta 2 MB."));
            t.Controls.Add(_paHint);
            t.Resize += (s, e) => FitPaNotes(t);

            // ---- sub-pestañas ----
            _paStPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            _paLinePanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
            t.Controls.Add(MakeSubTabs(new[] { "Estaciones", "Líneas" },
                i => { _paStPanel.Visible = i == 0; _paLinePanel.Visible = i == 1; }));

            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            BuildPaStationsPanel(_paStPanel);
            BuildPaLinesPanel(_paLinePanel);
            host.Controls.Add(_paStPanel);
            host.Controls.Add(_paLinePanel);
            t.Controls.Add(host);

            _paMsg = EmpMsg(); t.Controls.Add(_paMsg);
            return t;
        }

        void BuildPaStationsPanel(Panel host)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // Una tarjeta por estación: su audio y, a la derecha, el preaviso y la antelación del aviso.
            _paStList = EmpCards();
            _paStList.TitleCol = 0; _paStList.SubCols = new[] { 1 }; _paStList.RightCol = 2; _paStList.RightSubCols = new[] { 3 };
            _paStList.Icon = "🚉"; _paStList.Formats[2] = Tr("preaviso {0}"); _paStList.Formats[3] = Tr("antelación {0}");
            _paStList.CardHeight = 66; _paStList.MinWidth = 460; _paStList.Columns = 2;
            t.Controls.Add(_paStList);

            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(2, 4, 2, 2) };
            _paUpBtn = EmpButton(Tr("Subir audio…"), true); _paUpBtn.Width = 170; _paUpBtn.Margin = new Padding(0, 0, 8, 0);
            _paUpBtn.Click += (s, e) => UploadStationAudio();
            _paTestBtn = EmpButton(Tr("▶  Probar aviso")); _paTestBtn.Width = 170; _paTestBtn.Margin = new Padding(0, 0, 8, 0);
            _paTestBtn.Click += (s, e) => TestStationAudio();
            _paCfgBtn = EmpButton(Tr("Antelación…")); _paCfgBtn.Width = 150; _paCfgBtn.Margin = new Padding(0, 0, 8, 0);
            _paCfgBtn.Click += (s, e) => EditStationLead();
            _paDelBtn = EmpButton(Tr("Quitar audio")); _paDelBtn.Width = 150; _paDelBtn.Margin = new Padding(0);   // alineado con los demás
            _paDelBtn.BaseColor = Theme.Surface2; _paDelBtn.HoverColor = Color.FromArgb(150, 60, 60); _paDelBtn.TextColor = RedC;
            _paDelBtn.Click += (s, e) => DeleteStationAudio();
            btns.Controls.Add(_paUpBtn); btns.Controls.Add(_paTestBtn); btns.Controls.Add(_paCfgBtn); btns.Controls.Add(_paDelBtn);
            t.Controls.Add(btns);

            host.Controls.Add(t);
        }

        void BuildPaLinesPanel(Panel host)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // Una tarjeta por línea: cuántas paradas tiene y cuántas con voz propia.
            _paLineList = EmpCards();
            _paLineList.TitleCol = 0; _paLineList.SubCols = new[] { 2 }; _paLineList.RightCol = 1;
            _paLineList.Icon = "🛤"; _paLineList.Formats[1] = Tr("{0} parada") + "|" + Tr("{0} paradas"); _paLineList.Formats[2] = Tr("{0} con voz propia");
            _paLineList.CardHeight = 62; _paLineList.MinWidth = 420; _paLineList.Columns = 2;
            t.Controls.Add(_paLineList);

            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(2, 4, 2, 2) };
            _paLineNewBtn = EmpButton(Tr("Nueva línea…"), true); _paLineNewBtn.Width = 170; _paLineNewBtn.Margin = new Padding(0, 0, 8, 0);
            _paLineNewBtn.Click += (s, e) => NewPaLine();
            _paLineStopsBtn = EmpButton(Tr("Paradas…")); _paLineStopsBtn.Width = 150; _paLineStopsBtn.Margin = new Padding(0, 0, 8, 0);
            _paLineStopsBtn.Click += (s, e) => EditPaLineStops();
            _paLineRenBtn = EmpButton(Tr("Renombrar…")); _paLineRenBtn.Width = 150; _paLineRenBtn.Margin = new Padding(0, 0, 8, 0);
            _paLineRenBtn.Click += (s, e) => RenamePaLine();
            _paLineDelBtn = EmpButton(Tr("Eliminar línea")); _paLineDelBtn.Width = 160; _paLineDelBtn.Margin = new Padding(0);   // alineado con los demás
            _paLineDelBtn.BaseColor = Theme.Surface2; _paLineDelBtn.HoverColor = Color.FromArgb(150, 60, 60); _paLineDelBtn.TextColor = RedC;
            _paLineDelBtn.Click += (s, e) => DeletePaLine();
            btns.Controls.Add(_paLineNewBtn); btns.Controls.Add(_paLineStopsBtn); btns.Controls.Add(_paLineRenBtn); btns.Controls.Add(_paLineDelBtn);
            t.Controls.Add(btns);

            t.Controls.Add(PaNote(Tr("El maquinista elige una de estas líneas en el HUD antes de conducir. Con una línea elegida solo se anuncian las estaciones en las que para. Al eliminar una línea se borran solo sus audios propios; los de base se quedan.")));
            host.Controls.Add(t);
        }

        // Desplegable con el mismo aspecto que los del resto de SelectOR (tema oscuro completo,
        // incluidos el botón de la flecha y la barra del desplegable).
        ComboBox PaCombo()
        {
            var c = new ThemeCombo
            {
                Dock = DockStyle.Fill, Height = 28,
                Margin = new Padding(0, 4, 18, 4), DropDownHeight = 320
            };
            StyleCombo(c);
            return c;
        }

        static Label PaFieldLabel(string text) => new Label
        {
            Text = text, AutoSize = true, ForeColor = Theme.Subtle, BackColor = Theme.Bg,
            Anchor = AnchorStyles.Left, Margin = new Padding(0, 10, 8, 4)
        };

        readonly List<Label> _paNotes = new List<Label>();

        // Párrafo explicativo: se reparte sobre el ancho de la sección (FitPaNotes lo reajusta al
        // cambiar el tamaño de la ventana) en vez de quedarse encajonado en una columna fija.
        Label PaNote(string text)
        {
            var l = new Label
            {
                Text = text, AutoSize = true, MaximumSize = new Size(900, 0),
                ForeColor = Theme.Subtle, Font = Theme.Font(8.5f), BackColor = Theme.Bg,
                Margin = new Padding(2, 0, 2, 8)
            };
            _paNotes.Add(l);
            return l;
        }

        void FitPaNotes(Control host)
        {
            if (host == null || host.IsDisposed) return;
            int w = Math.Max(260, host.ClientSize.Width - 20);
            foreach (var l in _paNotes)
                if (l != null && !l.IsDisposed && l.MaximumSize.Width != w) l.MaximumSize = new Size(w, 0);
        }

        // ============================ Carga ============================

        void OnMegafoniaShown()
        {
            if (_paRouteBox == null) return;
            bool su = Supa.IsSuperadmin;
            _paOnChk.Visible = su;
            _paOnChk.Checked = PaEnabledHere();
            bool on = PaEnabledHere() || su;
            foreach (var c in new Control[] { _paUpBtn, _paTestBtn, _paDelBtn, _paCfgBtn,
                                              _paLineNewBtn, _paLineRenBtn, _paLineStopsBtn, _paLineDelBtn })
                if (c != null) c.Enabled = on;

            FillPaRoutes();
            if (!PaEnabledHere() && !su)
            {
                Msg(_paMsg, Tr("La megafonía no está habilitada para esta empresa."), true);
                return;
            }
            LoadPaRoute();
        }

        bool _paFillingRoutes;

        void FillPaRoutes()
        {
            string wantId = _paRoute, wantDir = _paRouteDir ?? (_paRoute == null ? _curRoute?.Path : null);
            _paRouteItems.Clear();
            foreach (var r in _routesAll) _paRouteItems.Add(new PaRouteItem { Id = RouteIds.IdOf(r.Path, r.Name), Name = r.Name, Dir = r.Path });
            ShowPaRouteItems(wantId, wantDir);
            _ = LoadPaRemoteRoutes();   // y las que solo tienen megafonía guardada (sin la ruta en el equipo)
        }

        // Rellena el desplegable y elige la ruta pedida (por carpeta o por RouteID), o la primera.
        void ShowPaRouteItems(string wantId, string wantDir)
        {
            _paFillingRoutes = true;
            _paRouteBox.BeginUpdate();
            _paRouteBox.Items.Clear();
            foreach (var it in _paRouteItems) _paRouteBox.Items.Add(it);
            _paRouteBox.EndUpdate();
            _paFillingRoutes = false;
            if (_paRouteItems.Count == 0) { _paRoute = null; _paRouteName = null; _paRouteDir = null; return; }
            int sel = -1;
            if (!string.IsNullOrEmpty(wantDir)) sel = _paRouteItems.FindIndex(x => string.Equals(x.Dir, wantDir, StringComparison.OrdinalIgnoreCase));
            if (sel < 0 && !string.IsNullOrEmpty(wantId)) sel = _paRouteItems.FindIndex(x => string.Equals(x.Id, wantId, StringComparison.OrdinalIgnoreCase));
            if (sel < 0) sel = 0;
            _paFillingRoutes = true;
            _paRouteBox.SelectedIndex = sel;
            _paFillingRoutes = false;
            SetPaRoute(_paRouteItems[sel]);
        }

        async Task LoadPaRemoteRoutes()
        {
            var c = _empSel; if (c == null) return;
            int seq = ++_paRemoteSeq;
            var (json, err) = await Supa.RpcAsync("pa_company_routes", new { p_company = c.Id });
            if (err != null || seq != _paRemoteSeq || _empSel?.Id != c.Id) return;   // servidor sin megafonias-sin-ruta.sql: solo las instaladas
            var locals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in _paRouteItems) { locals.Add(it.Id); if (it.Local) locals.Add(it.Name); }   // (el nombre: lo grabado antes del RouteID)
            var extra = new List<PaRouteItem>();
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    string id = Str(e, "route"); if (id.Length == 0 || locals.Contains(id)) continue;
                    extra.Add(new PaRouteItem { Id = id, Name = Str(e, "name").Length > 0 ? Str(e, "name") : id, Dir = null });
                }
            }
            catch { }
            if (extra.Count == 0) return;
            extra.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            _paRouteItems.RemoveAll(x => !x.Local);
            _paRouteItems.AddRange(extra);
            bool hadNone = string.IsNullOrEmpty(_paRoute);
            ShowPaRouteItems(_paRoute, _paRouteDir);
            if (hadNone && !string.IsNullOrEmpty(_paRoute) && (PaEnabledHere() || Supa.IsSuperadmin)) LoadPaRoute();   // sin rutas instaladas: la primera con megafonía
        }

        void OnPaRouteChanged()
        {
            if (_paFillingRoutes) return;
            int i = _paRouteBox.SelectedIndex;
            if (i < 0 || i >= _paRouteItems.Count) return;
            var it = _paRouteItems[i];
            if (string.Equals(it.Id, _paRoute, StringComparison.OrdinalIgnoreCase) && string.Equals(it.Dir, _paRouteDir, StringComparison.OrdinalIgnoreCase)) return;
            SetPaRoute(it);
            _paLineId = null;          // las líneas son de cada ruta
            LoadPaRoute();
        }

        void SetPaRoute(PaRouteItem it)
        {
            _paRouteName = it.Local ? it.Name : null;   // sin la ruta: nada que trasladar por su nombre
            _paRouteDir = it.Dir;
            _paRoute = it.Id;
            _paRouteDisplay = it.Name;
        }
        string _paRouteDisplay;

        void OnPaLineChanged()
        {
            if (_paFillingLines) return;
            int i = _paLineBox.SelectedIndex;
            _paLineId = (i <= 0 || i - 1 >= _paLines.Count) ? null : _paLines[i - 1].Id;
            MarkLineStops();
            FillPaTables();
        }

        // Estaciones de la ruta (del .tdb, sin necesidad de tener Open Rails abierto) + lo guardado.
        async void LoadPaRoute()
        {
            if (_empSel == null || string.IsNullOrEmpty(_paRoute) || _paBusy) return;
            _paBusy = true;
            try
            {
                Msg(_paMsg, Tr("Cargando estaciones…"), false);
                string dir = _paRouteDir;
                var tdb = dir == null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)   // ruta no instalada
                                      : await Task.Run(() => TdbStationNames(dir));

                var (json, err) = await PaBundle(_empSel.Id, _paRoute, _paRouteName);
                if (err != null) { Msg(_paMsg, Tr("Error: ") + err, true); return; }

                _paRows.Clear(); _paLines.Clear(); _paAudio.Clear(); _paLineCfg.Clear();
                var byNorm = new Dictionary<string, PaRow>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in tdb)
                {
                    var row = new PaRow { Norm = kv.Key, Display = kv.Value, InRoute = true };
                    byNorm[kv.Key] = row; _paRows.Add(row);
                }
                ParsePaBundle(json, byNorm);

                _paRows.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase));
                FillPaLineBox();
                MarkLineStops();
                FillPaTables();

                string resumen = string.Format(Tr("{0} estaciones · {1} con audio · {2} líneas."),
                                               _paRows.Count, CountWithAudio(), _paLines.Count);
                if (_paRouteDir == null) resumen += "  " + Tr("Esta ruta no está instalada en este equipo: se ve lo grabado para ella.");
                if (!PaEnabledHere())   // el superadmin la ve aunque esté apagada: que no se le olvide
                    resumen += "  " + Tr("La megafonía está DESHABILITADA para esta empresa: nadie la oirá.");
                Msg(_paMsg, resumen, !PaEnabledHere());
            }
            finally { _paBusy = false; }
        }

        void ParsePaBundle(string json, Dictionary<string, PaRow> byNorm)
        {
            try
            {
                using var d = JsonDocument.Parse(json);
                var root = d.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];

                PaRow Ensure(string norm)
                {
                    if (byNorm.TryGetValue(norm, out var r)) return r;
                    // sin el .tdb (o fuera de él) solo se tiene la clave en minúsculas: con mayúscula inicial
                    r = new PaRow { Norm = norm, Display = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(norm), InRoute = false };
                    byNorm[norm] = r; _paRows.Add(r);
                    return r;
                }

                if (root.TryGetProperty("stations", out var sts) && sts.ValueKind == JsonValueKind.Array)
                    foreach (var e in sts.EnumerateArray())
                    {
                        string norm = Str(e, "station"); if (norm.Length == 0) continue;
                        var row = Ensure(norm);
                        row.Radius = (int)Num(e, "radius_m"); if (row.Radius <= 0) row.Radius = 800;
                        row.Lead = (int)Num(e, "lead_s");
                    }

                if (root.TryGetProperty("lines", out var lns) && lns.ValueKind == JsonValueKind.Array)
                    foreach (var e in lns.EnumerateArray())
                    {
                        var line = new PaLine { Id = Str(e, "id"), Name = Str(e, "name") };
                        if (string.IsNullOrEmpty(line.Id)) continue;
                        if (e.TryGetProperty("stops", out var ss) && ss.ValueKind == JsonValueKind.Array)
                            foreach (var st in ss.EnumerateArray())
                                if (st.ValueKind == JsonValueKind.String)
                                {
                                    string s = st.GetString() ?? ""; if (s.Length == 0) continue;
                                    line.Stops.Add(s); Ensure(s);
                                }
                        _paLines.Add(line);
                    }

                if (root.TryGetProperty("line_cfg", out var lc) && lc.ValueKind == JsonValueKind.Array)
                    foreach (var e in lc.EnumerateArray())
                    {
                        string line = Str(e, "line"), st = Str(e, "station");
                        if (line.Length == 0 || st.Length == 0) continue;
                        _paLineCfg[LineCfgKey(line, st)] = ((int)Num(e, "radius_m"), (int)Num(e, "lead_s"));
                    }

                if (root.TryGetProperty("audio", out var au) && au.ValueKind == JsonValueKind.Array)
                    foreach (var e in au.EnumerateArray())
                    {
                        string station = Str(e, "station"), kind = Str(e, "kind"), line = Str(e, "line");
                        string path = Str(e, "path"), stamp = Str(e, "updated_at");
                        if (string.IsNullOrEmpty(path)) continue;
                        _paAudio[AudioKey(station, kind, line)] = (path, stamp);
                        if (station != "*" && kind == "nombre") Ensure(station);
                    }
            }
            catch { }
        }

        // Marca qué estaciones son parada de la línea elegida.
        void MarkLineStops()
        {
            PaLine line = null;
            foreach (var l in _paLines) if (l.Id == _paLineId) { line = l; break; }
            foreach (var r in _paRows) r.LineStop = false;
            if (line == null) return;
            foreach (string stop in line.Stops)
                foreach (var r in _paRows)
                    if (string.Equals(r.Norm, stop, StringComparison.OrdinalIgnoreCase)) { r.LineStop = true; break; }
        }

        // Estaciones que sonarían con la línea elegida (con su voz propia o con la base).
        int CountWithAudio()
        {
            int n = 0;
            foreach (var r in _paRows) if (StationClip(r.Norm, _paLineId).path != null) n++;
            return n;
        }

        void FillPaLineBox()
        {
            _paFillingLines = true;
            _paLineBox.BeginUpdate();
            _paLineBox.Items.Clear();
            _paLineBox.Items.Add(Tr("Base (todas las líneas)"));
            foreach (var l in _paLines) _paLineBox.Items.Add(l);
            _paLineBox.EndUpdate();
            int sel = 0;
            if (!string.IsNullOrEmpty(_paLineId))
                for (int i = 0; i < _paLines.Count; i++)
                    if (_paLines[i].Id == _paLineId) { sel = i + 1; break; }
            if (sel == 0) _paLineId = null;
            _paLineBox.SelectedIndex = sel;
            _paFillingLines = false;
        }

        void FillPaTables()
        {
            // --- estaciones: con una línea elegida van primero sus paradas ---
            var orden = new List<PaRow>(_paRows);
            if (_paLineId != null)
                orden.Sort((a, b) =>
                {
                    if (a.LineStop != b.LineStop) return a.LineStop ? -1 : 1;
                    return string.Compare(a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase);
                });
            _paStOrder = orden;

            // Misma empresa, ruta y línea: tras editar una fila la tabla se queda donde estaba.
            string ctx = (_empSel?.Id ?? "") + "|" + _paRoute + "|" + _paLineId;
            _paStList.RowKey = c => c.Length > 0 ? c[0].Split(new[] { "   ·  " }, StringSplitOptions.None)[0] : null;
            _paStList.BeginReload(ctx);
            _paStList.ClearRows();
            foreach (var r in orden)
            {
                var (path, _, own) = StationClip(r.Norm, _paLineId);
                string audio = path == null ? Tr("—") : own ? Tr("✓ propio de la línea") : (_paLineId == null ? Tr("✓ grabado") : Tr("· base de la ruta"));
                Color? audioCol = path == null ? Theme.Subtle : (own || _paLineId == null) ? Theme.Accent : (Color?)null;
                string nombre = r.Display;
                if (_paLineId != null && !r.LineStop) nombre += "   ·  " + Tr("no para en esta línea");
                else if (_paLineId == null && !r.InRoute && _paRouteDir != null) nombre += "   ·  " + Tr("no está en la ruta");
                Color? nameCol = (_paLineId != null && !r.LineStop) || (_paLineId == null && !r.InRoute && _paRouteDir != null) ? Theme.Subtle : (Color?)null;
                // Con una línea elegida: su aviso propio (marcado) o, si no tiene, el de la estación.
                (int radius, int lead) lcfg = default;
                bool cfgLinea = _paLineId != null && _paLineCfg.TryGetValue(LineCfgKey(_paLineId, r.Norm), out lcfg);
                int rad = cfgLinea ? lcfg.radius : r.Radius, lead = cfgLinea ? lcfg.lead : r.Lead;
                Color? cfgCol = cfgLinea ? Theme.Accent : (Color?)null;
                _paStList.AddRow(new[] { nombre, audio, rad + " m" + (cfgLinea ? " ·L" : ""), lead + " s" + (cfgLinea ? " ·L" : "") },
                                 new Color?[] { nameCol, audioCol, cfgCol, cfgCol });
            }
            if (orden.Count == 0) _paStList.SetEmpty(_paRouteDir == null ? Tr("Esta ruta no tiene estaciones con megafonía guardada.")
                                                                         : Tr("No se han encontrado estaciones en el .tdb de esta ruta."));
            _paStList.EndReload();

            // --- líneas ---
            _paLineList.BeginReload(ctx);
            _paLineList.ClearRows();
            foreach (var l in _paLines)
            {
                int propios = 0;
                foreach (var s in l.Stops)
                    if (_paAudio.ContainsKey(AudioKey(s, "nombre", l.Id))) propios++;
                _paLineList.AddRow(new[] { l.Name, l.Stops.Count.ToString(), propios.ToString() },
                                   new Color?[] { null, null, propios > 0 ? Theme.Accent : (Color?)Theme.Subtle });
            }
            if (_paLines.Count == 0) _paLineList.SetEmpty(Tr("Todavía no hay líneas: crea una para que el maquinista pueda elegirla."));
            _paLineList.EndReload();
        }

        List<PaRow> _paStOrder = new List<PaRow>();   // orden con el que se pintó la tabla

        // Nombres de estación del .tdb (normalizado → legible). No necesita Open Rails abierto.
        static Dictionary<string, string> TdbStationNames(string routeDir)
        {
            var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                ReadTdbWorld(routeDir, out _, out var stWorld);
                if (stWorld == null) return res;
                foreach (var kv in stWorld)
                {
                    string norm = NormStation(kv.Key); if (norm.Length == 0) continue;
                    if (!res.ContainsKey(norm)) res[norm] = CleanStation(kv.Key);
                }
            }
            catch { }
            return res;
        }

        // ============================ Acciones ============================

        bool PaCanEdit()
        {
            if (_empSel == null) { Msg(_paMsg, Tr("Elige una empresa."), true); return false; }
            if (!Supa.IsSuperadmin && !(CanManage() && PaEnabledHere()))
            {
                Msg(_paMsg, Tr("Solo el gerente o un gestor pueden editar la megafonía, y con ella habilitada."), true);
                return false;
            }
            return true;
        }

        async void TogglePaEnabled()
        {
            if (_empSel == null || !Supa.IsSuperadmin) return;
            bool want = _paOnChk.Checked;
            _paOnChk.Enabled = false;
            var (_, err) = await Supa.RpcAsync("set_company_pa", new { p_company = _empSel.Id, p_on = want });
            _paOnChk.Enabled = true;
            if (err != null) { _paOnChk.Checked = !want; Msg(_paMsg, Tr("Error: ") + err, true); return; }
            _empSel.PaEnabled = want;
            UpdateSubtabVisibility();     // la sección aparece o desaparece para el gestor
            LoadPaRoute();
        }

        PaRow SelectedPaRow()
        {
            int i = _paStList.SelectedRow;
            if (i < 0 || i >= _paStOrder.Count) { Msg(_paMsg, Tr("Selecciona una estación de la lista."), true); return null; }
            return _paStOrder[i];
        }

        PaLine SelectedPaLine()
        {
            int i = _paLineList.SelectedRow;
            if (i < 0 || i >= _paLines.Count) { Msg(_paMsg, Tr("Selecciona una línea de la lista."), true); return null; }
            return _paLines[i];
        }

        static string PickAudioFile()
        {
            using var od = new OpenFileDialog
            {
                Title = I18n.T("Elige el audio"),
                Filter = I18n.T("Audio (*.mp3; *.wav)") + "|*.mp3;*.wav",
                CheckFileExists = true
            };
            return od.ShowDialog() == DialogResult.OK ? od.FileName : null;
        }

        async void UploadStationAudio()
        {
            if (!PaCanEdit()) return;
            var row = SelectedPaRow(); if (row == null) return;
            string file = PickAudioFile(); if (file == null) return;
            await UploadPaClip(file, row.Norm, "nombre", _paLineId);
        }

        // Sube el archivo al bucket y registra la fila. Los clips generales van con route='*'
        // (valen para todas las rutas); el nombre de estación, con su ruta y, si toca, su línea.
        async Task UploadPaClip(string file, string station, string kind, string lineId)
        {
            try
            {
                var fi = new FileInfo(file);
                string ext = fi.Extension.ToLowerInvariant();
                if (ext != ".mp3" && ext != ".wav") { Msg(_paMsg, Tr("Solo se pueden subir audios MP3 o WAV."), true); return; }
                if (fi.Length > 2 * 1024 * 1024)
                {
                    Msg(_paMsg, Tr("El audio no puede pasar de 2 MB. Conviértelo a MP3 (mono, 64 kbps es de sobra)."), true);
                    return;
                }
                string route = station == "*" ? "*" : PaKey(_paRoute, _paRouteName);
                string carpeta = string.IsNullOrEmpty(lineId) ? kind : kind + "/" + lineId;
                string objPath = Megafonia.ObjectPath(_empSel.Id, route, carpeta, station, ext);
                var bytes = File.ReadAllBytes(file);
                if (!Megafonia.LooksLikeAudio(bytes, ext)) { Msg(_paMsg, Tr("Ese archivo no es un audio MP3/WAV válido."), true); return; }
                Msg(_paMsg, Tr("Subiendo audio…"), false);
                string err = await Megafonia.UploadAsync(objPath, bytes, Megafonia.MimeOf(file));
                if (err != null) { Msg(_paMsg, Tr("No se pudo subir el audio: ") + err, true); return; }

                var (_, rerr) = await Supa.RpcAsync("pa_set_audio", new
                {
                    p_company = _empSel.Id, p_route = route, p_station = station,
                    p_kind = kind, p_path = objPath, p_bytes = (int)fi.Length, p_line = lineId
                });
                if (rerr != null) { Msg(_paMsg, Tr("Error: ") + rerr, true); return; }

                _paAudio[AudioKey(station, kind, lineId)] = (objPath, null);
                FillPaTables();
                Msg(_paMsg, string.Format(Tr("Audio guardado ({0} KB){1}."), Math.Max(1, fi.Length / 1024),
                        string.IsNullOrEmpty(lineId) ? "" : " " + Tr("para esta línea")), false);
            }
            catch (Exception e) { Msg(_paMsg, Tr("Error: ") + e.Message, true); }
        }

        async void DeleteStationAudio()
        {
            if (!PaCanEdit()) return;
            var row = SelectedPaRow(); if (row == null) return;
            string key = AudioKey(row.Norm, "nombre", _paLineId);
            if (!_paAudio.TryGetValue(key, out var a) || string.IsNullOrEmpty(a.path))
            {
                Msg(_paMsg, _paLineId == null
                    ? Tr("Esa estación no tiene audio.")
                    : Tr("Esta línea no tiene voz propia aquí: usa la base. Cambia a «Base (todas las líneas)» para quitarla."), true);
                return;
            }
            await DeletePaClip(a.path, row.Norm, "nombre", _paLineId);
        }

        async Task DeletePaClip(string objPath, string station, string kind, string lineId)
        {
            Msg(_paMsg, Tr("Quitando audio…"), false);
            string route = station == "*" ? "*" : PaKey(_paRoute, _paRouteName);
            var (_, rerr) = await Supa.RpcAsync("pa_delete_audio", new
            { p_company = _empSel.Id, p_route = route, p_station = station, p_kind = kind, p_line = lineId });
            if (rerr != null) { Msg(_paMsg, Tr("Error: ") + rerr, true); return; }
            await Megafonia.DeleteAsync(objPath);   // si falla, la fila ya no lo referencia
            _paAudio.Remove(AudioKey(station, kind, lineId));
            FillPaTables();
            Msg(_paMsg, Tr("Audio quitado."), false);
        }

        // Reproduce el aviso de la estación tal como sonará con la línea elegida.
        async void TestStationAudio()
        {
            var row = SelectedPaRow(); if (row == null) return;
            var clip = StationClip(row.Norm, _paLineId);
            if (clip.path == null) { Msg(_paMsg, Tr("Esa estación todavía no tiene aviso."), true); return; }
            var (f, err) = await Megafonia.EnsureLocalAsync(clip.path, clip.stamp);
            if (f == null) { Msg(_paMsg, Tr("No se pudo descargar el audio: ") + err, true); return; }
            Msg(_paMsg, clip.own ? Tr("Sonando la voz propia de la línea…") : Tr("Sonando…"), false);
            Audio.Play(f);
        }

        // Antelación del preaviso: metros y segundos. Salta con el que se cumpla antes, así el
        // aviso llega con tiempo lo mismo en cercanías que a 300 km/h.
        // Con una LÍNEA elegida se cambia solo el aviso de esa línea en esa estación (las demás
        // líneas que paran allí no se tocan); sin línea, el de la estación (lo usan las líneas que
        // no tengan uno propio).
        async void EditStationLead()
        {
            if (!PaCanEdit()) return;
            var row = SelectedPaRow(); if (row == null) return;
            PaLine linea = null;
            if (_paLineId != null) foreach (var l in _paLines) if (l.Id == _paLineId) { linea = l; break; }
            if (linea != null) { await EditLineStationLead(row, linea); return; }
            string txt;
            using (var d = new TextPromptDialog(string.Format(I18n.T("Aviso de «{0}» (estación)"), row.Display),
                       I18n.T("Metros y segundos (por ejemplo: 800 20). Vale para las líneas sin aviso propio."),
                       row.Radius + " " + row.Lead))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                txt = d.Value ?? "";
            }
            var parts = txt.Replace(",", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !int.TryParse(parts[0], out int radius))
            { Msg(_paMsg, Tr("Escribe los metros y, si quieres, los segundos: «800 20»."), true); return; }
            int lead = row.Lead;
            if (parts.Length > 1 && !int.TryParse(parts[1], out lead)) lead = row.Lead;

            var (_, err) = await Supa.RpcAsync("pa_set_station", new
            {
                p_company = _empSel.Id, p_route = PaKey(_paRoute, _paRouteName), p_station = row.Norm,
                p_lat = (double?)null, p_lon = (double?)null, p_radius = radius, p_lead = lead
            });
            if (err != null) { Msg(_paMsg, Tr("Error: ") + err, true); return; }
            row.Radius = Math.Max(50, Math.Min(5000, radius));
            row.Lead = Math.Max(0, Math.Min(120, lead));
            FillPaTables();
            Msg(_paMsg, string.Format(Tr("«{0}»: aviso a {1} m o {2} s."), row.Display, row.Radius, row.Lead), false);
        }

        async Task EditLineStationLead(PaRow row, PaLine linea)
        {
            string key = LineCfgKey(linea.Id, row.Norm);
            bool tiene = _paLineCfg.TryGetValue(key, out var cur);
            int r0 = tiene ? cur.radius : row.Radius, l0 = tiene ? cur.lead : row.Lead;
            string txt;
            using (var d = new TextPromptDialog(string.Format(I18n.T("Aviso de «{0}» en la línea «{1}»"), row.Display, linea.Name),
                       I18n.T("Metros y segundos solo para esta línea (por ejemplo: 800 20). Vacío: usar los de la estación."),
                       r0 + " " + l0))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                txt = (d.Value ?? "").Trim();
            }
            if (txt.Length == 0)
            {
                var (_, cerr) = await Supa.RpcAsync("pa_clear_line_station", new { p_line = linea.Id, p_station = row.Norm });
                if (cerr != null) { Msg(_paMsg, Tr("Error: ") + cerr, true); return; }
                _paLineCfg.Remove(key);
                FillPaTables();
                Msg(_paMsg, string.Format(Tr("«{0}» en «{1}»: vuelve a usar el aviso de la estación ({2} m o {3} s)."),
                                          row.Display, linea.Name, row.Radius, row.Lead), false);
                return;
            }
            var parts = txt.Replace(",", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !int.TryParse(parts[0], out int radius))
            { Msg(_paMsg, Tr("Escribe los metros y, si quieres, los segundos: «800 20»."), true); return; }
            int lead = l0;
            if (parts.Length > 1 && !int.TryParse(parts[1], out lead)) lead = l0;
            var (_, err) = await Supa.RpcAsync("pa_set_line_station", new
            {
                p_line = linea.Id, p_station = row.Norm, p_radius = radius, p_lead = lead
            });
            if (err != null)
            {
                Msg(_paMsg, err.Contains("pa_set_line_station")
                    ? Tr("El servidor aún no permite guardar avisos por línea.")
                    : Tr("Error: ") + err, true);
                return;
            }
            _paLineCfg[key] = (Math.Max(50, Math.Min(5000, radius)), Math.Max(0, Math.Min(120, lead)));
            FillPaTables();
            var v = _paLineCfg[key];
            Msg(_paMsg, string.Format(Tr("«{0}» en la línea «{1}»: aviso a {2} m o {3} s (solo en esta línea)."),
                                      row.Display, linea.Name, v.radius, v.lead), false);
        }

        // ---------------- Líneas ----------------

        async void NewPaLine()
        {
            if (!PaCanEdit()) return;
            if (string.IsNullOrEmpty(_paRoute)) { Msg(_paMsg, Tr("Elige primero una ruta."), true); return; }
            string name;
            using (var d = new TextPromptDialog(I18n.T("Nueva línea"), I18n.T("Nombre de la línea (por ejemplo: R4 · sentido Manresa)"), ""))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                name = (d.Value ?? "").Trim();
            }
            if (name.Length == 0) { Msg(_paMsg, Tr("La línea necesita un nombre."), true); return; }
            var (json, err) = await Supa.RpcAsync("pa_upsert_line", new
            { p_company = _empSel.Id, p_route = PaKey(_paRoute, _paRouteName), p_name = name, p_direction = (string)null, p_id = (string)null });
            if (err != null) { Msg(_paMsg, Tr("Error: ") + err, true); return; }
            string id = (json ?? "").Trim().Trim('"');
            var line = new PaLine { Id = id, Name = name };
            _paLines.Add(line);
            _paLines.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            _paLineId = id;
            FillPaLineBox(); MarkLineStops(); FillPaTables();
            Msg(_paMsg, string.Format(Tr("Línea «{0}» creada. Marca sus paradas con «Paradas…»."), name), false);
        }

        async void RenamePaLine()
        {
            if (!PaCanEdit()) return;
            var line = SelectedPaLine(); if (line == null) return;
            string name;
            using (var d = new TextPromptDialog(I18n.T("Renombrar línea"), I18n.T("Nombre de la línea"), line.Name))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                name = (d.Value ?? "").Trim();
            }
            if (name.Length == 0) return;
            var (_, err) = await Supa.RpcAsync("pa_upsert_line", new
            { p_company = _empSel.Id, p_route = PaKey(_paRoute, _paRouteName), p_name = name, p_direction = (string)null, p_id = line.Id });
            if (err != null) { Msg(_paMsg, Tr("Error: ") + err, true); return; }
            line.Name = name;
            FillPaLineBox(); FillPaTables();
            Msg(_paMsg, Tr("Línea renombrada."), false);
        }

        async void EditPaLineStops()
        {
            if (!PaCanEdit()) return;
            var line = SelectedPaLine(); if (line == null) return;
            var todas = new List<(string norm, string disp)>();
            foreach (var r in _paRows) todas.Add((r.Norm, r.Display));
            List<string> nuevas;
            using (var d = new PaLineStopsDialog(line.Name, todas, line.Stops))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                nuevas = d.Stops;
            }
            var (_, err) = await Supa.RpcAsync("pa_set_line_stops", new { p_line = line.Id, p_stations = nuevas.ToArray() });
            if (err != null) { Msg(_paMsg, Tr("Error: ") + err, true); return; }
            line.Stops.Clear(); line.Stops.AddRange(nuevas);
            MarkLineStops(); FillPaTables();
            Msg(_paMsg, string.Format(Tr("«{0}»: para en {1} estaciones."), line.Name, nuevas.Count), false);
        }

        async void DeletePaLine()
        {
            if (!PaCanEdit()) return;
            var line = SelectedPaLine(); if (line == null) return;
            if (ThemedBox.Show(this,
                    string.Format(Tr("¿Eliminar la línea «{0}»? Se borran sus paradas y los audios propios de esa línea (los de base se quedan)."), line.Name),
                    "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            // Las filas de audio de la línea se van en cascada, pero los ARCHIVOS del bucket no:
            // hay que borrarlos aquí o quedan ocupando sitio sin que nada los referencie.
            var objetos = new List<string>();
            foreach (var kv in _paAudio)
                if (kv.Key.EndsWith("|" + line.Id, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(kv.Value.path))
                    objetos.Add(kv.Value.path);

            var (_, err) = await Supa.RpcAsync("pa_delete_line", new { p_line = line.Id });
            if (err != null) { Msg(_paMsg, Tr("Error: ") + err, true); return; }
            foreach (string o in objetos) await Megafonia.DeleteAsync(o);
            if (_paLineId == line.Id) _paLineId = null;
            _paLines.Remove(line);
            LoadPaRoute();
            Msg(_paMsg, Tr("Línea eliminada."), false);
        }

        static bool Flag(JsonElement e, string prop)
            => e.TryGetProperty(prop, out var v) && (v.ValueKind == JsonValueKind.True
               || (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
    }
}
