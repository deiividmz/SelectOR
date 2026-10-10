// Compra → Comprar: el escaparate de TRENES (.con) del contenido de este equipo (MainMenuForm.TrenesEmpresa.cs), con el
// formato de Conducción libre:
//  · Arriba, «ELIGE TU TREN», las pastillas Todos / Viajeros / Mercancías / Automotores / En tu flota y el buscador.
//  · La lista: una línea por tren con su composición 2D ajustada a la línea, su precio, cuántos tiene ya la empresa y
//    el botón «i» (el desglose del precio, vehículo por vehículo).
//  · Abajo, «TREN ELEGIDO»: la máquina de cabeza en 3D, todos sus datos útiles, el precio y los botones «Comprar tren»
//    y «Alquilar».

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        TrainRowList _shopRows;
        FlowLayoutPanel _shopTabs;
        RoundedInput _shopSearch;
        int _shopKind;
        Label _shopTitle, _shopOwned, _shopBuyPrice, _shopRentPrice, _shopSub;
        SpecTiles _shopSpecs;
        RoundButton _shopBuyBtn, _shopRentBtn, _shopInfoBtn, _shopGrantBtn;
        static readonly string[] ShopKindNames = { "Todos", "Viajeros", "Mercancías", "Automotores", "En tu flota", "⚠ Incompletos" };
        // por .con: el precio del tren y lo que lleva (se calculan al aparecer en pantalla)
        readonly Dictionary<string, (double price, string summary)> _shopInfo = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _shopInfoPending = new(StringComparer.OrdinalIgnoreCase);

        // El escaparate; «viewport» es el visor de Compra (la máquina de cabeza en 3D), que va en «TREN ELEGIDO».
        Control BuildTrainShop(Control viewport)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg, Margin = new Padding(0, 4, 0, 0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));     // rótulo · pastillas · buscador
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));     // los trenes
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 260));    // el elegido

            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var lblT = new Label { Text = Tr("ELIGE TU TREN"), AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.Subtle, Font = Theme.Font(8f, FontStyle.Bold), Margin = new Padding(0, 0, 12, 0) };
            _shopTabs = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 4, 0, 0) };
            for (int k = 0; k < ShopKindNames.Length; k++)
            {
                int idx = k;
                var b = BankChip(Tr(ShopKindNames[k]), k == 0);
                b.Click += (s, e) => { _shopKind = idx; SetChipActive(_shopTabs, idx); FilterShop(); };
                _shopTabs.Controls.Add(b);
            }
            _shopSearch = new RoundedInput(Tr("Buscar tren…")) { Width = 240, Height = 34, Anchor = AnchorStyles.Right, Margin = new Padding(10, 5, 0, 5) };
            var wait = new System.Windows.Forms.Timer { Interval = 220 };   // con miles de trenes, se filtra al dejar de escribir
            wait.Tick += (s, e) => { wait.Stop(); FilterShop(); };
            _shopSearch.Box.TextChanged += (s, e) => { wait.Stop(); wait.Start(); };
            // administrador: ceder a la empresa, sin coste, un ejemplar de cada tren de la lista (con sus filtros y búsqueda)
            _shopGrantBtn = EmpButton(Tr("★ Ceder los trenes de la lista"));
            _shopGrantBtn.Height = 34; _shopGrantBtn.Anchor = AnchorStyles.Right; _shopGrantBtn.Margin = new Padding(10, 5, 0, 5);
            using (var fg = Theme.Font(9.5f, FontStyle.Bold)) _shopGrantBtn.Width = TextRenderer.MeasureText(_shopGrantBtn.Text, fg).Width + 36;
            _shopGrantBtn.Visible = Supa.IsSuperadmin;
            _shopGrantBtn.Click += async (s, e) => await GrantShopListAsync();
            bar.Controls.Add(lblT, 0, 0); bar.Controls.Add(_shopTabs, 1, 0); bar.Controls.Add(_shopGrantBtn, 2, 0); bar.Controls.Add(_shopSearch, 3, 0);
            t.Controls.Add(bar, 0, 0);

            _thumbs ??= new VehicleThumbs(this);
            _shopRows = new TrainRowList
            {
                Dock = DockStyle.Fill, Thumbs = _thumbs, Margin = new Padding(0, 4, 0, 0),
                PathOf = o => (o as TrainItem)?.FilePath,                          // la composición entera
                TitleOf = o => (o as TrainItem)?.Name,
                PillsOf = o => ShopPills(o as TrainItem),
                SubOf = o => ShopInfo(o as TrainItem)?.summary ?? Tr("Calculando…"),
                RightOf = o => ShopInfo(o as TrainItem) is { price: > 0 } i ? TEur(i.price) : null,
                BadgeOf = o => ShopBadge(o as TrainItem),
                MultiSelect = true,   // Ctrl / Mayús: varios trenes a la vez (comprar o alquilar todos)
            };
            _shopRows.SelectedIndexChanged += (s, e) => OnShopSelected();
            _shopRows.MarksChanged += (s, e) => OnShopSelected();
            _shopRows.InfoClicked += o => ShowShopBreakdown(o as TrainItem);
            t.Controls.Add(_shopRows, 0, 1);

            // ---- abajo: el tren elegido (como en Conducción libre) ----
            var sel = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, Radius = 12, Padding = new Padding(10, 8, 14, 10), Margin = new Padding(0, 10, 0, 0) };
            var selGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface, Margin = new Padding(0) };
            selGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
            selGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            selGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            viewport.Dock = DockStyle.Fill; viewport.Margin = new Padding(0, 0, 14, 0);
            if (_buyView != null) { _buyView.Set3D(true); _buyView.ToggleShown = false; }   // como en Conducción libre
            selGrid.Controls.Add(viewport, 0, 0);
            var info = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = new Padding(0) };
            var headRow = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Theme.Surface };
            var lblSel = new Label { Text = Tr("TREN ELEGIDO"), Dock = DockStyle.Left, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Subtle, Font = Theme.Font(8f, FontStyle.Bold) };
            lblSel.Width = TextRenderer.MeasureText(lblSel.Text, lblSel.Font).Width + 12;
            _shopTitle = new OneLineLabel { Text = Tr("Elige un tren"), Dock = DockStyle.Fill, AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Accent, Font = Theme.Font(11f, FontStyle.Bold) };
            var acts = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Surface, Padding = new Padding(0, 5, 0, 0) };
            RoundButton Act(string text, bool primary = false)
            {
                var b = EmpButton(Tr(text), primary); b.Height = 32; b.Margin = new Padding(8, 0, 0, 0);
                using var fb = Theme.Font(9.5f, FontStyle.Bold); b.Width = TextRenderer.MeasureText(b.Text, fb).Width + 36;
                return b;
            }
            _shopBuyBtn = Act("Comprar tren", primary: true); _shopBuyBtn.Width = Math.Max(_shopBuyBtn.Width, 150);
            _shopBuyBtn.Click += async (s, e) => { if (_shopRows.MarkedCount > 1) await BuyTrainsAsync(ShopMarked(), false); else await BuyTrainAsync(_shopRows.SelectedItem as TrainItem, false, _buyMsg); };
            _shopRentBtn = Act("Alquilar");
            _shopRentBtn.Click += async (s, e) => { if (_shopRows.MarkedCount > 1) await BuyTrainsAsync(ShopMarked(), true); else await BuyTrainAsync(_shopRows.SelectedItem as TrainItem, true, _buyMsg); };
            _shopInfoBtn = Act("ⓘ  Desglose del precio");
            _shopInfoBtn.Click += (s, e) => ShowShopBreakdown(_shopRows.SelectedItem as TrainItem);
            // el precio: compra (grande) y alquiler por servicio
            var prices = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(14, 0, 4, 0) };
            _shopBuyPrice = new Label { Text = "—", AutoSize = true, ForeColor = Theme.Accent, Font = Theme.Font(11f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 0) };
            _shopRentPrice = new Label { Text = "", AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(8.25f), Margin = new Padding(0) };
            prices.Controls.Add(_shopBuyPrice); prices.Controls.Add(_shopRentPrice);
            acts.Controls.AddRange(new Control[] { _shopBuyBtn, _shopRentBtn, prices, _shopInfoBtn });
            _shopOwned = new Label { AutoSize = false, Dock = DockStyle.Right, Width = 10, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.AccentHi, Font = Theme.Font(9f, FontStyle.Bold), Visible = false };
            _shopOwned.TextChanged += (s, e) => _shopOwned.Width = TextRenderer.MeasureText(_shopOwned.Text, _shopOwned.Font).Width + 16;
            headRow.Controls.Add(_shopTitle); headRow.Controls.Add(_shopOwned); headRow.Controls.Add(acts); headRow.Controls.Add(lblSel);
            // lo que lleva y el tipo de servicio; debajo, todos sus datos
            _shopSub = new Label { Dock = DockStyle.Top, Height = 24, ForeColor = Theme.Subtle, Font = Theme.Font(9f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            _shopSpecs = new SpecTiles { Dock = DockStyle.Top, Height = 0, Columns = 6 };
            info.Controls.Add(_shopSpecs);
            info.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Surface });
            info.Controls.Add(_shopSub);
            info.Controls.Add(headRow);
            selGrid.Controls.Add(info, 1, 0);
            sel.Controls.Add(selGrid);
            t.Controls.Add(sel, 0, 2);
            return t;
        }

        // Rehace la lista con las pastillas y el buscador (conserva el tren elegido).
        void FilterShop()
        {
            if (_shopRows == null) return;
            if (_shopGrantBtn != null && _shopGrantBtn.Visible != Supa.IsSuperadmin) _shopGrantBtn.Visible = Supa.IsSuperadmin;   // (la sesión puede llegar después)
            string q = (_shopSearch?.Box.Text ?? "").Trim();
            var prev = _shopRows.SelectedItem as TrainItem;
            var shown = new List<object>();
            var counts = new int[6];
            foreach (var c in _consistsAll)
            {
                if (c?.FilePath == null || c.Locomotive == null) continue;   // sin máquina no se puede comprar
                if (q.Length > 0 && (c.Name == null || c.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                // a los que les falta algún .eng o .wag no se les puede tasar bien: van aparte, en «⚠ Incompletos»
                bool incomplete = KnownIncomplete(c) == true;
                if (incomplete) { counts[5]++; if (_shopKind == 5) shown.Add(c); continue; }
                var sp = KnownSpecSeats(c);
                bool own = ShopCopies(c) > 0;
                counts[0]++;
                if (sp != null) { counts[sp.Freight ? 2 : 1]++; if (sp.Automotor) counts[3]++; }
                if (own) counts[4]++;
                if (_shopKind == 5) continue;
                bool ok = _shopKind switch
                {
                    1 => sp != null && !sp.Freight, 2 => sp != null && sp.Freight, 3 => sp != null && sp.Automotor, 4 => own, _ => true
                };
                if (ok) shown.Add(c);
            }
            bool same = shown.Count == _shopRows.Items.Count;
            for (int k = 0; same && k < shown.Count; k++) same = ReferenceEquals(shown[k], _shopRows.Items[k]);
            _shopRows.BeginUpdate();
            if (!same) { _shopRows.Items.Clear(); _shopRows.Items.AddRange(shown.ToArray()); }
            _shopRows.EmptyText = _shopKind == 5 ? Tr("Ningún tren incompleto: a todos les encuentras sus .eng y .wag.")
                                : _shopKind == 4 ? Tr("La empresa aún no tiene ninguno de los trenes de tu contenido.")
                                : _shopKind >= 1 && _classDone < _classTotal ? Tr("Clasificando los trenes del contenido…") : Tr("Nada coincide con el filtro.");
            _shopRows.EndUpdate();
            if (prev != null) { int i = _shopRows.Items.IndexOf(prev); if (i >= 0) _shopRows.SelectedIndex = i; }
            if (_shopRows.SelectedIndex < 0 && _shopRows.Items.Count > 0) _shopRows.SelectedIndex = 0;
            using var f = Theme.Font(9f, FontStyle.Bold);
            for (int k = 0; k < ShopKindNames.Length && k < _shopTabs.Controls.Count; k++)
                if (_shopTabs.Controls[k] is RoundButton b)
                {
                    if (k == 5) b.Visible = counts[5] > 0 || _shopKind == 5;   // incompletos: solo si hay alguno
                    string t = (k == 5 ? "⚠" : Tr(ShopKindNames[k])) + "  " + counts[k].ToString("N0", EsEs);
                    if (b.Text != t) { b.Text = t; b.Width = TextRenderer.MeasureText(t, f).Width + 28; b.Invalidate(); }
                }
        }

        int ShopCopies(TrainItem c) => c?.FilePath != null && _conTrain.TryGetValue(c.FilePath, out var id) ? TrainCopies(id) : 0;
        string ShopBadge(TrainItem c) { int n = ShopCopies(c); return n > 0 ? "✓ " + string.Format(Tr("Tienes {0}"), n) : null; }

        // Distintivos de la línea: viajeros o mercancías, tracción y velocidad.
        List<(string, Color, Color)> ShopPills(TrainItem c)
        {
            var l = new List<(string, Color, Color)>();
            string warn = MissingText(c);
            if (warn != null) { var oc = Color.FromArgb(251, 146, 60); l.Add((warn, oc, Color.FromArgb(55, oc))); return l; }
            var sp = KnownSpecSeats(c);
            if (sp == null) { if (c != null) _ = TrainSpecAsync(c).ContinueWith(_ => { try { BeginInvoke((Action)(() => _shopRows?.Invalidate())); } catch { } }); return l; }
            var tc = sp.Freight ? Color.FromArgb(251, 146, 60) : Color.FromArgb(120, 144, 226);
            l.Add((sp.Freight ? Tr("MERCANCÍAS") : sp.Automotor ? Tr("AUTOMOTOR") : Tr("VIAJEROS"), tc, Color.FromArgb(60, tc)));
            if (!string.IsNullOrEmpty(sp.Traction)) l.Add((sp.Traction, Theme.Subtle, CardPaint.Rail));
            if (sp.Kmh > 0) l.Add((sp.Kmh.ToString("N0", EsEs) + " km/h", Theme.Subtle, CardPaint.Rail));
            return l;
        }

        // Precio y composición de una línea: se calculan al aparecer en pantalla (en segundo plano, dos a la vez).
        (double price, string summary)? ShopInfo(TrainItem c)
        {
            if (c?.FilePath == null) return null;
            if (_shopInfo.TryGetValue(c.FilePath, out var v)) return v;
            RequestShopInfo(c);
            return null;
        }

        async void RequestShopInfo(TrainItem c)
        {
            if (_shopInfoPending.Contains(c.FilePath) || _shopInfoPending.Count >= 2) return;
            _shopInfoPending.Add(c.FilePath);
            (double, string) r = (0, "");
            try { r = await Task.Run(() => { var vs = TrainVehiclesOfCached(c); return (vs.Any(v => !v.Wagon) ? TrainPriceLocal(vs) : 0, TrainSummary(vs)); }); } catch { }
            _shopInfoPending.Remove(c.FilePath);
            _shopInfo[c.FilePath] = r;
            _shopRows?.Invalidate();
        }

        // El tren elegido: su máquina en 3D, su composición, sus datos y su precio.
        int _shopSelSeq;
        async void OnShopSelected()
        {
            var c = _shopRows?.SelectedItem as TrainItem;
            int seq = ++_shopSelSeq;
            if (_shopRows != null && _shopRows.MarkedCount > 1) { ShowShopMulti(seq); return; }
            if (_shopInfoBtn != null) { _shopInfoBtn.Enabled = true; _shopInfoBtn.Invalidate(); }
            _buyView?.Show(c?.Locomotive?.FilePath, c?.Name, Tr("Elige un tren"));
            if (_buyView != null && _buyView.Is3D) RenderBuyMachinePreview();
            _shopTitle.Text = c?.Name ?? Tr("Elige un tren");
            UpdateShopOwned(c);
            if (c == null) { _shopSpecs.SetItems(null); _shopSub.Text = ""; _shopBuyPrice.Text = "—"; _shopRentPrice.Text = ""; return; }
            _shopSub.Text = Tr("Calculando…");
            var vsT = Task.Run(() => TrainVehiclesOfCached(c));
            var sp = await TrainSpecSeatsAsync(c);
            var vs = await vsT;
            double kmh = sp?.Kmh ?? 0;
            var (an, tot) = await Task.Run(() => (AnalyzeComposition(c, kmh), ConsistTotals(c)));
            if (seq != _shopSelSeq) return;
            double price = vs.Any(v => !v.Wagon) ? TrainPriceLocal(vs) : 0;
            _shopInfo[c.FilePath] = (price, TrainSummary(vs));
            int? fixedSeats = SeatsOverride(c);
            _shopSub.Text = string.Join("  ·  ", new[] { vs.Count > 0 ? TrainSummary(vs) : null, an.ServiceType, fixedSeats != null ? Tr("plazas fijadas por la empresa") : null }.Where(x => !string.IsNullOrEmpty(x)));
            _shopSpecs.SetItems(ShopSpecItems(sp, vs, an, tot, ShopCopies(c)));
            _shopBuyPrice.Text = price > 0 ? TEur(price) : "—";
            _shopRentPrice.Text = price > 0 ? string.Format(Tr("alquiler {0} por servicio"), TEur(price * _fleetRentPct)) : "";
            _shopBuyBtn.Enabled = _shopRentBtn.Enabled = price > 0;
            _shopBuyBtn.Invalidate(); _shopRentBtn.Invalidate();
            _shopRows.Invalidate();
        }

        // Todos los datos útiles del tren (dos filas de seis casillas).
        List<(string, string)> ShopSpecItems(TrainSpec sp, List<TrainVeh> vs, CompositionAnalysis an, (int cars, double mass, double brake, double capacity) tot, int copies)
        {
            var es = EsEs;
            double kw = vs.Where(v => !v.Wagon).Sum(v => v.Power * v.Count);
            double mass = sp?.MassT > 0 ? sp.MassT : tot.mass;
            int engines = vs.Where(v => !v.Wagon).Sum(v => v.Count), cars = sp?.Cars > 0 ? sp.Cars : tot.cars;
            bool pax = an.Capacity > 0;
            return new List<(string, string)>
            {
                (Tr("TRACCIÓN"), sp != null && sp.Traction.Length > 0 ? sp.Traction : "—"),
                (Tr("POTENCIA"), kw > 0 ? kw.ToString("N0", es) + " kW" : "—"),
                (Tr("POTENCIA / MASA"), kw > 0 && mass > 0 ? (kw / mass).ToString("N1", es) + " kW/t" : "—"),
                (Tr("VEL. MÁXIMA"), sp?.Kmh > 0 ? sp.Kmh.ToString("N0", es) + " km/h" : "—"),
                (Tr("LONGITUD"), sp?.LengthM > 0 ? sp.LengthM.ToString("N0", es) + " m" : "—"),
                (Tr("MASA"), mass > 0 ? mass.ToString("N0", es) + " t" : "—"),
                (Tr("VEHÍCULOS"), cars > 0 ? cars.ToString("N0", es) + (engines > 0 ? "  ·  " + string.Format(engines == 1 ? Tr("{0} motriz") : Tr("{0} motrices"), engines) : "") : "—"),
                (Tr("FRENO"), tot.brake > 0 ? tot.brake.ToString("N0", es) + " kN" : "—"),
                (Tr("PLAZAS"), pax ? an.Capacity.ToString("N0", es) : "—"),
                (Tr("CONFORT"), pax ? an.Comfort.ToString("N0", es) + "/100" : "—"),
                (Tr("DENSIDAD"), pax && an.Density > 0 ? an.Density.ToString("N1", es) + " pax/m²" : "—"),
                (Tr("EN TU FLOTA"), copies > 0 ? string.Format(copies == 1 ? Tr("{0} ejemplar") : Tr("{0} ejemplares"), copies) : Tr("ninguno")),
            };
        }

        List<TrainItem> ShopMarked() => _shopRows.SelectedItems.OfType<TrainItem>().ToList();

        // Varios trenes elegidos: lo que cuestan todos juntos y lo que llevan.
        async void ShowShopMulti(int seq)
        {
            var list = ShopMarked();
            _shopTitle.Text = string.Format(Tr("{0} trenes elegidos"), list.Count);
            _shopOwned.Visible = false;
            _shopSub.Text = Tr("«Comprar» y «Alquilar» actúan sobre todos (uno de cada)  ·  Esc: quitar la selección");
            _shopInfoBtn.Enabled = false; _shopInfoBtn.Invalidate();
            _shopBuyBtn.Text = string.Format(Tr("Comprar los {0}"), list.Count); _shopRentBtn.Text = string.Format(Tr("Alquilar los {0}"), list.Count);
            FitShopButtons();
            var all = await Task.Run(() => list.Select(c => TrainVehiclesOfCached(c)).ToList());
            if (seq != _shopSelSeq) return;
            double price = all.Where(v => v.Any(x => !x.Wagon)).Sum(v => TrainPriceLocal(v));
            int vehicles = all.Sum(v => v.Sum(x => Math.Max(1, x.Count)));
            int heads = all.Sum(v => v.Where(x => !x.Wagon).Sum(x => Math.Max(1, x.Count)));
            double kw = all.Sum(v => v.Where(x => !x.Wagon).Sum(x => x.Power * x.Count));
            int owned = list.Count(c => ShopCopies(c) > 0);
            var es = EsEs;
            _shopSpecs.SetItems(new List<(string, string)>
            {
                (Tr("TRENES"), list.Count.ToString("N0", es)), (Tr("VEHÍCULOS"), vehicles.ToString("N0", es)), (Tr("MÁQUINAS"), heads.ToString("N0", es)),
                (Tr("POTENCIA"), kw > 0 ? kw.ToString("N0", es) + " kW" : "—"), (Tr("COMPRA"), TEur(price)), (Tr("ALQUILER / SERV."), TEur(price * _fleetRentPct)),
                (Tr("TESORERÍA"), TEur(_empSel?.Balance ?? 0)), (Tr("TRAS LA COMPRA"), TEur((_empSel?.Balance ?? 0) - price)),
                (Tr("YA EN TU FLOTA"), string.Format(Tr("{0} de {1}"), owned, list.Count)),
            });
            _shopBuyPrice.Text = TEur(price);
            _shopRentPrice.Text = string.Format(Tr("alquiler {0} por servicio"), TEur(price * _fleetRentPct));
        }

        // Comprar o alquilar varios trenes (uno de cada), con una sola confirmación.
        async Task BuyTrainsAsync(List<TrainItem> list, bool rent)
        {
            var co = _empSel;
            if (co == null || list.Count == 0 || !CanBuyTrains()) return;
            var all = await Task.Run(() => list.Select(c => (c, vs: TrainVehiclesOfCached(c))).Where(x => x.vs.Any(v => !v.Wagon)).ToList());
            double price = all.Sum(x => TrainPriceLocal(x.vs));
            string q = rent ? string.Format(Tr("¿Alquilar los {0} trenes elegidos? Entre todos se pagan {1} por servicio."), all.Count, TEur(price * _fleetRentPct))
                            : string.Format(Tr("¿Comprar los {0} trenes elegidos por {1}? La tesorería tiene {2}."), all.Count, TEur(price), TEur(co.Balance));
            if (all.Count < list.Count) q += "\n\n" + string.Format(Tr("{0} no se pueden comprar (no se han podido leer o no llevan máquina)."), list.Count - all.Count);
            if (ThemedBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            int ok = 0; string err = null;
            foreach (var (c, _) in all)
            {
                Msg(_buyMsg, string.Format(rent ? Tr("Alquilando {0} de {1}…") : Tr("Comprando {0} de {1}…"), ok + 1, all.Count), false);
                var lbl = new Label();
                if (await BuyTrainAsync(c, rent, lbl, ask: false)) ok++; else { err ??= lbl.Text; if (!rent) break; }   // sin saldo, se para
            }
            Msg(_buyMsg, string.Format(rent ? Tr("{0} de {1} trenes alquilados: ya están en Flota.") : Tr("{0} de {1} trenes comprados: ya están en Flota."), ok, all.Count) + (err != null ? "  " + err : ""), err != null);
            _shopRows.ClearMarks();
        }

        // Administrador: un ejemplar de cada tren de la lista, sin coste (admin_grant_train, trenes-cesion-admin.sql). Se salta
        // los que la empresa ya tiene (por composición), los repetidos de la lista, los incompletos y los que no llevan
        // máquina. Se para al primer error.
        async Task GrantShopListAsync()
        {
            var co = _empSel;
            if (co == null || !Supa.IsSuperadmin || _shopRows == null) return;
            var items = _shopRows.Items.OfType<TrainItem>().Where(c => c?.FilePath != null).ToList();
            if (items.Count == 0) { Msg(_buyMsg, Tr("La lista está vacía."), true); return; }
            Msg(_buyMsg, Tr("Preparando la lista…"), false);
            await LoadCoTrainsAsync(co.Id, force: true);
            var have = new HashSet<string>(_coTrains.Select(t => TrainKey(t.Vehicles)));
            var todo = await Task.Run(() =>
            {
                var l = new List<(TrainItem c, List<TrainVeh> vs)>();
                var seen = new HashSet<string>(have);
                foreach (var c in items)
                {
                    try
                    {
                        if (MissingParts(c).Length > 0) continue;   // incompleto (⚠)
                        var vs = TrainVehiclesOfCached(c);
                        if (!vs.Any(v => !v.Wagon)) continue;
                        if (seen.Add(TrainKey(vs))) l.Add((c, vs));
                    }
                    catch { }
                }
                return l;
            });
            int skip = items.Count - todo.Count;
            if (todo.Count == 0) { Msg(_buyMsg, Tr("La empresa ya tiene todos los trenes de la lista (o no se pueden ceder)."), false); return; }
            string q = string.Format(Tr("¿Ceder a «{0}» un ejemplar de cada uno de los {1} trenes de la lista, sin coste?"), co.Name, todo.Count)
                     + (skip > 0 ? "\n\n" + string.Format(Tr("Se saltan {0}: los que la empresa ya tiene, los repetidos, los incompletos (⚠) y los que no llevan máquina."), skip) : "");
            if (ThemedBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { Msg(_buyMsg, "", false); return; }
            _shopGrantBtn.Enabled = false;
            // Un tren que el servidor no admite (p. ej. una lista de vehículos demasiado larga) se salta y se sigue con los
            // demás; solo se para si el fallo es de todos (falta el SQL, no es administrador, sin conexión…).
            int ok = 0, idx = 0, seguidos = 0; string err = null;
            var fallos = new List<(string name, string why)>();
            try
            {
                foreach (var (c, vs) in todo)
                {
                    idx++;
                    if (co != _empSel) { err = Tr("Se ha cambiado de empresa."); break; }
                    Msg(_buyMsg, string.Format(Tr("Cediendo {0} de {1}: {2}…"), idx, todo.Count, c.Name), false);
                    string conText = null;
                    try { conText = ConsistDoc.ReadText(c.FilePath); } catch { }
                    if (string.IsNullOrEmpty(conText)) { fallos.Add((c.Name, Tr("no se ha podido leer el .con"))); continue; }
                    string img;
                    using (var bmp = await ConsistStripAsync(c)) img = ImageToB64(bmp);
                    var (_, e2) = await Supa.RpcAsync("admin_grant_train", new
                    {
                        p_company = co.Id, p_name = c.Name, p_con_file = System.IO.Path.GetFileNameWithoutExtension(c.FilePath), p_con_text = conText,
                        p_vehicles = vs.Select(v => v.ToJson(v.Count)).ToArray(), p_image = img
                    });
                    if (e2 != null)
                    {
                        if (e2.Contains("PGRST202") || e2.Contains("Could not find")) { err = Tr("Falta ejecutar trenes-cesion-admin.sql en el servidor."); break; }
                        if (e2.Contains("administrador") || e2.Contains("JWT") || ++seguidos >= 5) { err = e2; break; }   // falla con todos
                        fallos.Add((c.Name, e2));
                        continue;
                    }
                    seguidos = 0; ok++;
                }
            }
            finally { _shopGrantBtn.Enabled = true; }
            _coTrainsCompany = null;
            lock (_fleetStatusCache) _fleetStatusCache.Clear();
            string res = string.Format(Tr("{0} de {1} trenes cedidos a «{2}»: ya están en Flota."), ok, todo.Count, co.Name);
            if (fallos.Count > 0) res += "  " + (fallos.Count == 1 ? Tr("1 no se ha podido ceder.") : string.Format(Tr("{0} no se han podido ceder."), fallos.Count));
            Msg(_buyMsg, res + (err != null ? "  " + Tr("Error: ") + err : ""), err != null || fallos.Count > 0);
            if (fallos.Count > 0)
                ThemedBox.Show(this, (fallos.Count == 1 ? Tr("Un tren no se ha podido ceder:") : string.Format(Tr("{0} trenes no se han podido ceder:"), fallos.Count)) + "\n\n"
                    + string.Join("\n", fallos.Take(15).Select(x => "• " + x.name + " — " + x.why))
                    + (fallos.Count > 15 ? "\n" + string.Format(Tr("…y {0} más."), fallos.Count - 15) : ""),
                    "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            LoadCompanies(); LoadFleet();
            _ = MatchLocalTrainsAsync();
        }

        void UpdateShopOwned(TrainItem c)
        {
            if (_shopOwned == null) return;
            int n = ShopCopies(c);
            _shopOwned.Visible = n > 0;
            _shopOwned.Text = "✓ " + string.Format(Tr("{0} en tu flota"), n);
            if (_shopBuyBtn != null) { _shopBuyBtn.Text = n > 0 ? Tr("Comprar otro") : Tr("Comprar tren"); _shopRentBtn.Text = Tr("Alquilar"); FitShopButtons(); }
        }

        // Los botones, a la medida de su texto (cambia con lo elegido).
        void FitShopButtons()
        {
            using var fb = Theme.Font(9.5f, FontStyle.Bold);
            _shopBuyBtn.Width = Math.Max(150, TextRenderer.MeasureText(_shopBuyBtn.Text, fb).Width + 36);
            _shopRentBtn.Width = TextRenderer.MeasureText(_shopRentBtn.Text, fb).Width + 36;
            _shopBuyBtn.Invalidate(); _shopRentBtn.Invalidate();
        }

        async void ShowShopBreakdown(TrainItem c)
        {
            if (c == null) return;
            var vs = await Task.Run(() => TrainVehiclesOfCached(c));
            ShowPriceBreakdown(c.Name, vs, null);
        }

        // Ya se sabe qué trenes del contenido son de la empresa: distintivos de Compra y vistas de Flota al día.
        void OnLocalTrainsMatched()
        {
            ForgetTrainSpecs(_conTrain.Where(kv => _trainSeats.ContainsKey(kv.Value)).Select(kv => kv.Key));   // las plazas de la empresa
            if (_shopRows != null) { if (_shopKind == 4) FilterShop(); else _shopRows.Invalidate(); UpdateShopOwned(_shopRows.SelectedItem as TrainItem); }
            UpdateFleetTrainViews();
        }

        // ============================ el desglose del precio ============================
        string UnitTypeText(TrainVeh v) => !v.Wagon ? (v.Automotor ? string.Format(Tr("Automotor de {0} coches"), (int)Math.Max(1, v.Cars)) : Tr("Locomotora"))
                                         : v.Type == "freight" ? Tr("Vagón de mercancías") : v.Type == "pax" ? Tr("Coche de viajeros") : Tr("Coche o furgón");

        // Cómo se tasa cada vehículo (lo mismo que el servidor: fleet_value y wagon_value).
        string UnitPriceBasis(TrainVeh v)
        {
            var es = EsEs;
            if (!v.Wagon) return string.Join(" · ", new[] { v.Power > 0 ? v.Power.ToString("N0", es) + " kW" : null, v.Speed > 0 ? v.Speed.ToString("N0", es) + " km/h" : null,
                                                            v.Mass > 0 ? v.Mass.ToString("N0", es) + " t" : null, v.Brake > 0 ? v.Brake.ToString("N0", es) + " kN " + Tr("freno") : null }.Where(x => x != null));
            return v.Type == "freight" ? string.Format(Tr("40.000 € + 800 €/t × {0} t"), Math.Min(300, Math.Max(0, v.Mass)).ToString("N1", es))
                 : v.Type == "pax" ? string.Format(Tr("150.000 € + 2.000 €/plaza × {0} plazas"), Math.Min(400, Math.Max(0, v.Capacity)).ToString("N0", es))
                 : Tr("120.000 € (precio fijo)");
        }

        void ShowPriceBreakdown(string title, List<TrainVeh> vs, string note)
        {
            using var f = new Form
            {
                Text = Tr("Desglose del precio"), BackColor = Theme.Bg, ForeColor = Theme.Text, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, KeyPreview = true, Font = Theme.Font(9.5f), FormBorderStyle = FormBorderStyle.FixedDialog,
            };
            try { f.Icon = Icon; } catch { }
            int W = Theme.Px(1000);
            f.ClientSize = new Size(W + Theme.Px(40), 200);
            var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, BackColor = Theme.Bg, Padding = new Padding(20, 14, 20, 16) };
            stack.Controls.Add(new Label { Text = Tr("DESGLOSE DEL PRECIO"), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(8f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) });
            stack.Controls.Add(new Label { Text = title, AutoSize = true, MaximumSize = new Size(W, 0), ForeColor = Theme.Accent, Font = Theme.Font(14f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) });
            stack.Controls.Add(new Label { Text = TrainSummary(vs) + (note != null ? "  ·  " + note : ""), AutoSize = true, MaximumSize = new Size(W, 0), ForeColor = Theme.Subtle, Margin = new Padding(0, 0, 0, 10) });
            stack.Controls.Add(BuildBreakdownTable(vs, W, out double total));
            stack.Controls.Add(new Label { Text = string.Format(Tr("Total del tren: {0}  ·  alquiler: {1} por servicio"), TEur(total), TEur(total * _fleetRentPct)), AutoSize = true, Font = Theme.Font(11f, FontStyle.Bold), Margin = new Padding(0, 10, 0, 4) });
            if (Math.Abs(_fleetScale - 1) > 0.001)
                stack.Controls.Add(new Label { Text = string.Format(Tr("Incluye la escala de precios de la flota (× {0})."), _fleetScale.ToString("0.##", EsEs)), AutoSize = true, ForeColor = Theme.Subtle, Margin = new Padding(0, 0, 0, 4) });
            var ok = EmpButton(Tr("Cerrar"), primary: true); ok.Width = Theme.Px(140); ok.Margin = new Padding(0, 10, 0, 0); ok.Click += (s, e) => f.Close();
            stack.Controls.Add(ok);
            f.Controls.Add(stack);
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter) f.Close(); };
            f.Load += (s, e) => { var ps = stack.GetPreferredSize(new Size(f.ClientSize.Width, 0)); f.ClientSize = new Size(Math.Max(f.ClientSize.Width, ps.Width), ps.Height + 4); };
            _breakdownOpen = f;
            try { f.ShowDialog(this); } finally { _breakdownOpen = null; }
        }
        Form _breakdownOpen;   // para las pruebas

        // Flota: el desglose de un tren de la empresa (sus vehículos, con los datos con que se tasó).
        void ShowFleetTrainBreakdown(FleetModel m)
        {
            if (m == null) return;
            foreach (var kv in _fleetTrainModels)
                if (ReferenceEquals(kv.Value, m))
                {
                    var t = _coTrains.Find(x => x.Id == kv.Key);
                    if (t != null) ShowPriceBreakdown(t.Name, t.Vehicles, Tr("al precio de hoy"));
                    return;
                }
        }
    }
}
