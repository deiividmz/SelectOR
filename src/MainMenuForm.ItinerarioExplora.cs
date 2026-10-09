// Conducción libre → «Tu viaje» → chip «Itinerario»: un planificador sobre el plano de la ruta (el mismo detalle que el mapa
// grande del HUD) para marcar el itinerario ANTES de conducir. Clic en la vía: salida, puntos de paso y llegada (el
// camino se traza por las vías, respetando los desvíos, como en el mapa grande); «Seguir el recorrido» lo rellena con
// el recorrido elegido; en la lista se marca en qué estaciones se para.
//  · Lo marcado se guarda como un itinerario más (los «Guardados» del mapa grande, AppPrefs.SavedItineraries) y queda
//    elegido para esa ruta (AppPrefs.ExploreItinerary). El chip lo enseña: «Itinerario: <nombre>».
//  · Al conducir desde Conducción libre se carga solo (RoadDriveStart): el HUD (mini-mapa y mapa grande) y la hoja de ruta
//    salen ya con él. Si el tren empieza a mitad del itinerario, la hoja de ruta se calcula desde donde está.
//  · El planificador usa su propio RoadBook (no el de la conducción) y el grafo de vías de la ruta (RouteGraphFor),
//    que queda preparado para cuando se conduzca.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        TripChip _chipItin;
        bool _launchExplore;   // la conducción en marcha se lanzó desde Conducción libre (para cargar su itinerario)
        Form _itinPlannerOpen;  // para las pruebas

        TripChip BuildItineraryChip(ToolTip tip)
        {
            _chipItin = new TripChip { Glyph = "map", Margin = new Padding(0, 1, 8, 1) };
            tip.SetToolTip(_chipItin, Tr("Marca en el plano de la ruta el itinerario de tu próxima conducción"));
            _chipItin.Click += (s, e) => OpenItineraryPlanner();
            UpdateItineraryChip();
            return _chipItin;
        }

        // El itinerario elegido para la ruta (si sigue guardado), o null.
        SavedItinerary ExploreItineraryFor(string routeDir)
        {
            string key = RouteKey(routeDir);
            if (key.Length == 0 || _prefs?.ExploreItinerary == null || !_prefs.ExploreItinerary.TryGetValue(key, out var id)) return null;
            return _prefs.SavedItineraries?.FirstOrDefault(s => s.Id == id && string.Equals(s.Route, key, StringComparison.OrdinalIgnoreCase));
        }

        void UpdateItineraryChip()
        {
            if (_chipItin == null) return;
            var it = ExploreItineraryFor(_curRoute?.Path);
            string name = it?.Name ?? "";
            if (name.Length > 22) name = name.Substring(0, 21) + "…";
            _chipItin.Text = it == null ? Tr("Itinerario") : string.Format(Tr("Itinerario: {0}"), name);
            _chipItin.Toggle = it != null; _chipItin.Active = it != null;   // con itinerario: verde, con su ✓
            _chipItin.Fit();
        }

        // Al conducir desde Conducción libre (RoadDriveStart): el itinerario elegido para la ruta, al HUD y a la hoja de ruta.
        void ApplyExploreItineraryOnDrive()
        {
            if (!_launchExplore) return;
            var it = ExploreItineraryFor(_road.RouteDir);
            if (it != null) LoadSavedItinerary(it);
        }

        // ============================ el planificador ============================
        void OpenItineraryPlanner()
        {
            var route = _curRoute;
            if (route == null) return;
            string dir = route.Path, key = RouteKey(dir);
            var rb = new RoadBook(); rb.Reset(dir);
            string loadedId = null; bool dirty = false;

            using var f = new Form
            {
                Text = Tr("Itinerario") + " · " + route.Name, BackColor = Theme.Bg, ForeColor = Theme.Text, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, ShowInTaskbar = false, KeyPreview = true, Font = Theme.Font(9.5f),
            };
            try { f.Icon = Icon; } catch { }
            var wa = Screen.FromControl(this).WorkingArea;
            f.ClientSize = new Size(Math.Min(Theme.Px(1360), wa.Width - 60), Math.Min(Theme.Px(860), wa.Height - 60));
            f.MinimumSize = new Size(900, 560);
            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var head = new Label { Text = "  " + string.Format(Tr("Itinerario en {0}"), route.Name), Dock = DockStyle.Top, Height = 42, Font = Theme.Font(13f, FontStyle.Bold), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Surface };

            var view = new RoutePlanView { Dock = DockStyle.Fill, Expandable = false, Hint = Tr("Clic en la vía: añadir un punto · rueda: acercar · arrastrar: mover") };
            view.SetPlan(null, Tr("Preparando el esquema de vías…"));
            var mapHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(14, 12, 8, 8) };
            mapHost.Controls.Add(view);

            // derecha: guardados, resumen y paradas
            var msg = new Label { AutoSize = true, MaximumSize = new Size(Theme.Px(330), 0), ForeColor = Theme.Subtle, Margin = new Padding(0, 0, 0, 2) };
            var side = new TableLayoutPanel { Dock = DockStyle.Right, Width = Theme.Px(360), ColumnCount = 1, RowCount = 8, BackColor = Theme.Bg, Padding = new Padding(8, 12, 14, 8) };
            side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 7; i++) side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label Cap(string t) => new Label { Text = t, AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(8f, FontStyle.Bold), Margin = new Padding(0, 8, 0, 4) };
            var help = new Label
            {
                Text = Tr("Haz clic en la vía para marcar la salida, los puntos de paso y la llegada: el camino va por las vías, como en el mapa grande del HUD. Al conducir desde Conducción libre, el itinerario sale ya en el HUD y en la hoja de ruta."),
                AutoSize = true, MaximumSize = new Size(Theme.Px(330), 0), ForeColor = Theme.Subtle, Margin = new Padding(0, 0, 0, 6)
            };
            var saved = new ThemeCombo { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 4) };
            StyleCombo(saved);
            var sum = new Label { AutoSize = true, MaximumSize = new Size(Theme.Px(330), 0), Font = Theme.Font(10f, FontStyle.Bold), ForeColor = Theme.Text, Margin = new Padding(0, 6, 0, 2) };
            var stops = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.Font(9.5f),
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.Px(30), IntegralHeight = false, Margin = new Padding(0, 4, 0, 0)
            };
            Native.UseDarkScrollBars(stops);
            side.Controls.Add(help); side.Controls.Add(Cap(Tr("GUARDADOS"))); side.Controls.Add(saved);
            // parar en todas / en ninguna
            var allRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 2, 0, 0) };
            RoundButton Small(string t) => new RoundButton { Text = t, Width = Theme.Px(120), Height = 30, Radius = 8, FontSize = 9f, Margin = new Padding(0, 0, 6, 0), BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text };
            var bHaltAll = Small(Tr("Parar en todas")); var bHaltNone = Small(Tr("Ninguna"));
            allRow.Controls.Add(bHaltAll); allRow.Controls.Add(bHaltNone);
            side.Controls.Add(sum); side.Controls.Add(msg); side.Controls.Add(Cap(Tr("PARADAS (clic: parar o pasar sin parar)"))); side.Controls.Add(allRow); side.Controls.Add(stops);

            // abajo: botones
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Padding = new Padding(14, 10, 14, 10) };
            RoundButton Btn(string t, bool primary = false, int w = 150)
            {
                var b = new RoundButton
                {
                    Text = t, Width = Theme.Px(w), Height = 40, Radius = 10, FontSize = 10f, Margin = new Padding(0, 0, 8, 0),
                    BaseColor = primary ? Theme.Accent : Theme.Surface2, HoverColor = primary ? Theme.AccentHi : Theme.SurfaceHi,
                    TextColor = primary ? Color.White : Theme.Text, FontStyle = primary ? FontStyle.Bold : FontStyle.Regular
                };
                if (primary) b.GradientTo = Theme.Accent2;
                return b;
            }
            var bFollow = Btn(Tr("Seguir el recorrido"), w: 190);
            var bUndo = Btn(Tr("Deshacer"), w: 120);
            var bClear = Btn(Tr("Borrar"), w: 110);
            var bAll = Btn(Tr("Ver toda la ruta"), w: 160);
            var bSave = Btn(Tr("Guardar…"), w: 130);
            var bUse = Btn(Tr("Usar al conducir"), primary: true, w: 190);
            var bRemove = Btn(Tr("Quitar"), w: 110); bRemove.TextColor = Color.FromArgb(229, 115, 115);
            var bClose = Btn(Tr("Cerrar"), w: 110);
            bar.Controls.AddRange(new Control[] { bFollow, bUndo, bClear, bAll, bSave, bUse, bRemove, bClose });
            bFollow.Enabled = CurrentPath()?.FilePath != null;

            f.Controls.Add(mapHost); f.Controls.Add(side); f.Controls.Add(bar); f.Controls.Add(head); f.Controls.Add(stripe);

            // ---------- dibujo del itinerario encima del plano ----------
            view.Overlay = (g, proj, area) =>
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (rb.PathLat.Length >= 2)
                {
                    var pts = new PointF[rb.PathLat.Length];
                    for (int i = 0; i < pts.Length; i++) pts[i] = proj(rb.PathLat[i], rb.PathLon[i]);
                    using (var under = new Pen(Color.FromArgb(200, 20, 22, 24), 8f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLines(under, pts);
                    using (var pen = new Pen(HudMapRender.ItineraryCol, 4.5f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLines(pen, pts);
                }
                // paradas marcadas: punto blanco con borde ámbar
                foreach (var s in rb.Stops.Where(s => s.Halt && !s.Synthetic))
                {
                    var p = proj(s.Lat, s.Lon);
                    using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, p.X - 5, p.Y - 5, 10, 10);
                    using (var pen = new Pen(HudMapRender.ItineraryCol, 2.2f)) g.DrawEllipse(pen, p.X - 5, p.Y - 5, 10, 10);
                }
                // puntos marcados: numerados (salida verde, llegada ámbar)
                var vis = rb.Points.Where(p => !p.Guide).ToList();
                using var fN = Theme.Font(8f, FontStyle.Bold);
                for (int i = 0; i < vis.Count; i++)
                {
                    var p = proj(vis[i].Lat, vis[i].Lon);
                    float r = 10;
                    var col = i == 0 ? Theme.Accent : i == vis.Count - 1 && vis.Count > 1 ? HudMapRender.ItineraryCol : Color.FromArgb(96, 165, 250);
                    using (var b = new SolidBrush(col)) g.FillEllipse(b, p.X - r, p.Y - r, 2 * r, 2 * r);
                    using (var pen = new Pen(Color.FromArgb(30, 34, 38), 2f)) g.DrawEllipse(pen, p.X - r, p.Y - r, 2 * r, 2 * r);
                    TextRenderer.DrawText(g, (i + 1).ToString(), fN, Rectangle.Round(new RectangleF(p.X - r, p.Y - r, 2 * r, 2 * r)), Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            };

            // ---------- estado ----------
            void Refresh()
            {
                if (rb.Points.Count >= 2) rb.Replan(false, 0, 0, false, 0);
                else { rb.Replan(false, 0, 0, false, 0); rb.Problem = null; }
                int marks = rb.Points.Count(p => !p.Guide);
                sum.Text = rb.Points.Count < 2
                    ? (marks == 0 ? Tr("Sin itinerario todavía.") : Tr("Marca al menos la salida y la llegada."))
                    : rb.Problem ?? string.Format(Tr("{0} km · {1} paradas"), (rb.TotalLen / 1000.0).ToString("N1", EsEs), rb.HaltCount);
                sum.ForeColor = rb.Problem != null ? Color.FromArgb(229, 115, 115) : Theme.Text;
                stops.BeginUpdate(); stops.Items.Clear();
                foreach (var s in rb.Stops) stops.Items.Add(s);
                stops.EndUpdate();
                bUndo.Enabled = rb.Points.Count > 0; bClear.Enabled = rb.Points.Count > 0;
                bSave.Enabled = bUse.Enabled = rb.Points.Count >= 2 && rb.HasPlan;
                bRemove.Visible = ExploreItineraryFor(dir) != null;
                foreach (var b in new[] { bUndo, bClear, bSave, bUse }) b.Invalidate();
                view.Redraw();
            }
            stops.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                var st = (RoadBook.Stop)stops.Items[e.Index];
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(sel ? Color.FromArgb(50, 62, 54) : Theme.Surface)) e.Graphics.FillRectangle(b, e.Bounds);
                string mark = st.IsEnd ? "🏁" : st.IsReverse ? "⇄" : st.Halt ? "●" : "○";
                var mc = st.Synthetic ? Theme.Subtle : st.Halt ? HudMapRender.ItineraryCol : Theme.Subtle;
                var r = new Rectangle(e.Bounds.X + 10, e.Bounds.Y, 22, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, mark, stops.Font, r, mc, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                string name = st.IsEnd ? Tr("Destino") + (string.IsNullOrWhiteSpace(st.Name) || st.Name == Tr("Destino") || st.Name == "Destino" ? "" : " · " + st.Name)
                            : st.IsReverse ? Tr("Cambio de sentido") : st.Name;
                string km = (st.Dist / 1000.0).ToString("N1", EsEs) + " km";
                var kmW = TextRenderer.MeasureText(km, stops.Font).Width;
                TextRenderer.DrawText(e.Graphics, name + (st.Synthetic || st.Halt ? "" : "  ·  " + Tr("sin parar")), stops.Font,
                    new Rectangle(e.Bounds.X + 34, e.Bounds.Y, e.Bounds.Width - 44 - kmW, e.Bounds.Height), st.Halt || st.Synthetic ? Theme.Text : Theme.Subtle,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(e.Graphics, km, stops.Font, new Rectangle(e.Bounds.Right - kmW - 10, e.Bounds.Y, kmW, e.Bounds.Height), Theme.Subtle, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            };
            stops.MouseClick += (s, e) =>
            {
                int i = stops.IndexFromPoint(e.Location);
                if (i < 0 || i >= rb.Stops.Count || rb.Stops[i].Synthetic) return;
                rb.ToggleHalt(i); dirty = true; stops.Invalidate(); view.Redraw();
                sum.Text = string.Format(Tr("{0} km · {1} paradas"), (rb.TotalLen / 1000.0).ToString("N1", EsEs), rb.HaltCount);
            };

            List<SavedItinerary> savedHere = new();
            void FillSaved(string selectId = null)
            {
                savedHere = (_prefs.SavedItineraries ?? new List<SavedItinerary>()).Where(s => string.Equals(s.Route, key, StringComparison.OrdinalIgnoreCase))
                                                                                   .OrderByDescending(s => s.Created).ToList();
                saved.BeginUpdate(); saved.Items.Clear();
                saved.Items.Add(savedHere.Count == 0 ? Tr("No hay itinerarios guardados en esta ruta") : Tr("Cargar un itinerario guardado…"));
                foreach (var s in savedHere) saved.Items.Add(s.Name + "  ·  " + s.Km.ToString("N1", EsEs) + " km");
                int sel = selectId == null ? 0 : savedHere.FindIndex(s => s.Id == selectId) + 1;
                saved.SelectedIndex = Math.Max(0, sel);
                saved.EndUpdate();
            }
            bool loading = false;
            void Load(SavedItinerary it)
            {
                if (it == null || rb.Graph == null) return;
                rb.ClearAll(); rb.SetHaltChoices(it.Halts);
                int ok = 0;
                foreach (var p in it.Points) if (rb.AddSavedPoint(p.Lat, p.Lon, p.Edge, p.Off, p.Guide, p.Reverse)) ok++;
                if (ok == 0) { rb.ClearAll(); msg.Text = Tr("Ese itinerario no encaja en las vías de esta ruta."); }
                else { loadedId = it.Id; dirty = false; msg.Text = string.Format(Tr("Cargado «{0}»."), it.Name); }
                Refresh();
            }
            saved.SelectedIndexChanged += (s, e) =>
            {
                if (loading || saved.SelectedIndex <= 0) return;
                Load(savedHere[saved.SelectedIndex - 1]);
            };

            // ---------- acciones ----------
            view.MapClick += (lat, lon, mPerPx) =>
            {
                if (rb.Graph == null) return;
                var last = rb.Points.LastOrDefault(p => !p.Guide);
                if (last != null && HudMapDetail.Meters(last.Lat, last.Lon, lat, lon) < Math.Max(3, 6 * mPerPx)) return;   // doble clic: un solo punto
                if (!rb.AddPoint(lat, lon, Math.Max(25, 18 * mPerPx))) { msg.Text = Tr("Haz clic más cerca de una vía."); return; }
                dirty = true; msg.Text = "";
                Refresh();
            };
            bUndo.Click += (s, e) => { rb.Undo(); dirty = true; Refresh(); };
            void Halts(bool on) { rb.SetAllHalts(on); dirty = true; stops.Invalidate(); view.Redraw(); if (rb.HasPlan) sum.Text = string.Format(Tr("{0} km · {1} paradas"), (rb.TotalLen / 1000.0).ToString("N1", EsEs), rb.HaltCount); }
            bHaltAll.Click += (s, e) => Halts(true);
            bHaltNone.Click += (s, e) => Halts(false);
            bClear.Click += (s, e) => { rb.ClearAll(); dirty = true; loadedId = null; Refresh(); };
            bAll.Click += (s, e) => view.ResetView();
            bFollow.Click += (s, e) =>
            {
                string pat = CurrentPath()?.FilePath;
                if (rb.Graph == null || pat == null) return;
                rb.ClearAll(); FollowPat(rb, pat); dirty = true; loadedId = null;
                msg.Text = rb.Points.Count >= 2 ? string.Format(Tr("Recorrido «{0}»: marca o quita paradas en la lista."), CurrentPath()?.ToString()) : Tr("No se ha podido seguir ese recorrido por las vías.");
                Refresh();
            };
            string SaveFromPlanner(bool ask)
            {
                if (!dirty && loadedId != null) return loadedId;
                var names = rb.Stops.Where(x => !x.Synthetic && !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Name).ToList();
                string sug = names.Count >= 2 ? names[0] + " → " + names[^1] : string.Format(Tr("Itinerario {0}"), savedHere.Count + 1);
                string name = sug;
                if (ask)
                {
                    using var dlg = new TextPromptDialog(Tr("Guardar itinerario"), Tr("Nombre del itinerario (lo encontrarás también en la pestaña «Guardados» del mapa grande):"), sug, "", Tr("Guardar"));
                    if (dlg.ShowDialog(f) != DialogResult.OK) return null;
                    name = (dlg.Value ?? "").Trim();
                    if (name.Length == 0) name = sug;
                }
                if (name.Length > 80) name = name.Substring(0, 80);
                var it = new SavedItinerary { Name = name, Route = key, Km = Math.Round(rb.TotalLen / 1000.0, 1), Halts = rb.HaltsForSave() };
                foreach (var p in rb.Points) it.Points.Add(new SavedItineraryPoint { Lat = p.Lat, Lon = p.Lon, Edge = p.Edge, Off = p.Off, Guide = p.Guide, Reverse = p.Reverse });
                _prefs.SavedItineraries.RemoveAll(x => string.Equals(x.Route, key, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Name, it.Name, StringComparison.OrdinalIgnoreCase));
                _prefs.SavedItineraries.Add(it);
                try { _prefs.Save(); } catch { }
                loadedId = it.Id; dirty = false;
                loading = true; FillSaved(it.Id); loading = false;
                return it.Id;
            }
            bSave.Click += (s, e) => { var id = SaveFromPlanner(true); if (id != null) msg.Text = Tr("Itinerario guardado."); };
            bUse.Click += (s, e) =>
            {
                var id = SaveFromPlanner(loadedId == null || dirty);
                if (id == null) return;
                _prefs.ExploreItinerary[key] = id;
                try { _prefs.Save(); } catch { }
                f.DialogResult = DialogResult.OK; f.Close();
            };
            bRemove.Click += (s, e) =>
            {
                _prefs.ExploreItinerary.Remove(key);
                try { _prefs.Save(); } catch { }
                f.DialogResult = DialogResult.OK; f.Close();
            };
            bClose.Click += (s, e) => f.Close();
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) f.Close(); else if (e.KeyCode == Keys.Z && e.Control) { rb.Undo(); dirty = true; Refresh(); } };

            // ---------- carga: el plano y el grafo de vías (en segundo plano) ----------
            f.Shown += async (s, e) =>
            {
                loading = true; FillSaved(); loading = false;
                Refresh();
                var gTask = RouteGraphFor(dir);
                var (_, plan) = await BuildRoutePlanAsync(dir);
                RouteGraph g = null;
                try { g = await gTask; } catch { }
                if (f.IsDisposed) return;
                if (g == null || plan == null || plan.Empty)
                {
                    view.SetPlan(null, Tr("Esta ruta no trae el esquema de vías (.tdb): no se puede trazar el itinerario."));
                    bFollow.Enabled = false;
                    return;
                }
                rb.Graph = g;
                view.SetPlan(plan);
                var cur = ExploreItineraryFor(dir);
                if (cur != null) { Load(cur); loading = true; FillSaved(cur.Id); loading = false; }
                else Refresh();
                view.Focus();
            };
            _itinPlannerOpen = f;
            try { f.ShowDialog(this); }
            finally { _itinPlannerOpen = null; }
            UpdateItineraryChip();
            UpdateStatus();
        }
    }
}
