// (2026-11-09) Avisos de incidencias en la vía (sql/avisos-sala.sql): desde la ventana Objetos, el administrador y los
// autorizados crean uno o varios avisos (nivel, texto, lugar y, si se quiere, el sitio del objeto elegido en el mapa). A
// todos los Puestos de Mando de la sala les sale un cuadro con cada aviso (DispatchForm.Incidencias.cs) para que el jefe de
// circulación informe a sus maquinistas.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class ObjetosSalaForm
    {
        public sealed class RoomAlert
        {
            public long Id; public string Level, Text, Place; public double? Lat, Lon; public long At;
        }
        public static readonly (string level, string label)[] AlertLevels =
        {
            ("info", "Información"), ("precaucion", "Precaución"), ("grave", "Grave"),
        };
        public static string AlertLevelLabel(string level) => Tr(AlertLevels.FirstOrDefault(x => x.level == level).label ?? level);
        public static Color AlertColor(string level) => level == "grave" ? Color.FromArgb(239, 68, 68) : level == "info" ? Color.FromArgb(79, 195, 247) : Color.FromArgb(255, 176, 32);

        public static List<RoomAlert> ParseAlerts(string json)
        {
            var l = new List<RoomAlert>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return l;
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    double? D(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
                    l.Add(new RoomAlert
                    {
                        Id = e.GetProperty("id").GetInt64(), Level = e.GetProperty("level").GetString() ?? "precaucion",
                        Text = e.GetProperty("text").GetString() ?? "", Place = e.TryGetProperty("place", out var p) ? p.GetString() ?? "" : "",
                        Lat = D("lat"), Lon = D("lon"), At = e.TryGetProperty("at", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt64() : 0
                    });
                }
            }
            catch { }
            return l;
        }

        // el botón, con los de abajo de la lista
        void BuildAlertsBtn(FlowLayoutPanel buttons)
        {
            var b = DialogKit.Secondary("⚠ " + Tr("Avisos…"), 140);
            b.Click += async (s, e) => await OpenAlerts();
            new ToolTip().SetToolTip(b, Tr("Avisos de incidencias en la vía: les sale un cuadro a los Puestos de Mando para que informen a sus maquinistas"));
            buttons.Controls.Add(b);
            int rows = (buttons.Controls.Count + 1) / 2;
            buttons.Height = Math.Max(buttons.Height, Theme.Px(10) + rows * Theme.Px(42));
        }

        async Task OpenAlerts()
        {
            using var f = new Form { Text = Tr("Avisos de incidencias"), StartPosition = FormStartPosition.CenterParent, Size = new Size(Theme.Px(640), Theme.Px(480)), MinimumSize = new Size(Theme.Px(520), Theme.Px(360)) };
            DialogKit.Dress(f);
            var head = new DialogKit.Header(Tr("Avisos de incidencias"), Tr("Les sale a los Puestos de Mando de la sala, para que informen a sus maquinistas"), "⚠");
            var list = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.Font(9.25f),
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.Px(52), IntegralHeight = false
            };
            var msg = new Label { Dock = DockStyle.Bottom, Height = Theme.Px(24), ForeColor = Theme.Subtle, BackColor = Theme.Bg, Padding = new Padding(Theme.Px(16), 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft };
            List<RoomAlert> items = new();
            list.DrawItem += (s, e) =>
            {
                if (e.Index < 0 || e.Index >= items.Count) return;
                var a = items[e.Index]; bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(sel ? Theme.Surface2 : Theme.Bg)) e.Graphics.FillRectangle(b, e.Bounds);
                using (var b = new SolidBrush(AlertColor(a.Level))) e.Graphics.FillRectangle(b, e.Bounds.X + Theme.Px(10), e.Bounds.Y + Theme.Px(8), Theme.Px(4), e.Bounds.Height - Theme.Px(16));
                using var fb = Theme.Font(9.25f, FontStyle.Bold); using var fs = Theme.Font(8.4f);
                int x = e.Bounds.X + Theme.Px(22), w = e.Bounds.Width - Theme.Px(30);
                TextRenderer.DrawText(e.Graphics, a.Text, fb, new Rectangle(x, e.Bounds.Y + Theme.Px(7), w, Theme.Px(20)), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                string sub = AlertLevelLabel(a.Level) + (a.Place.Length > 0 ? " · " + a.Place : "") + (a.Lat != null ? " · " + Tr("con sitio en el mapa") : "");
                TextRenderer.DrawText(e.Graphics, sub, fs, new Rectangle(x, e.Bounds.Y + Theme.Px(28), w, Theme.Px(18)), AlertColor(a.Level), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            };
            try { Native.UseDarkScrollBars(list); } catch { }
            async Task Load()
            {
                var (json, err) = await Rpc("room_alerts_get", new { p_route = _route });
                if (f.IsDisposed) return;
                if (err != null) { msg.ForeColor = Color.FromArgb(229, 115, 115); msg.Text = err.Contains("room_alerts_get") || err.Contains("PGRST202") ? Tr("Falta avisos-sala.sql en el servidor.") : err; return; }
                items = ParseAlerts(json);
                list.BeginUpdate(); list.Items.Clear(); foreach (var a in items) list.Items.Add(a.Id); list.EndUpdate();
                msg.ForeColor = Theme.Subtle;
                msg.Text = items.Count == 0 ? Tr("Ningún aviso. «Nuevo» para crear uno.") : string.Format(Tr("{0} avisos en la sala"), items.Count);
            }
            var foot = new DialogKit.Footer();
            var bNew = DialogKit.Primary(Tr("Nuevo")); DialogKit.Fit(bNew, 120);
            var bEdit = DialogKit.Secondary(Tr("Cambiar"));
            var bDel = DialogKit.Danger(Tr("Quitar"));
            var bClose = DialogKit.Secondary(Tr("Cerrar"));
            bNew.Click += async (s, e) => { if (await EditAlert(f, null)) await Load(); };
            async Task Edit() { if (list.SelectedIndex >= 0 && list.SelectedIndex < items.Count && await EditAlert(f, items[list.SelectedIndex])) await Load(); }
            bEdit.Click += async (s, e) => await Edit();
            list.DoubleClick += async (s, e) => await Edit();
            bDel.Click += async (s, e) =>
            {
                if (list.SelectedIndex < 0 || list.SelectedIndex >= items.Count) return;
                var a = items[list.SelectedIndex];
                if (ThemedBox.Show(f, string.Format(Tr("¿Quitar el aviso «{0}»?"), a.Text), Tr("Avisos de incidencias"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                var (_, err) = await Rpc("room_alert_delete", new { p_id = a.Id });
                if (err != null) { msg.ForeColor = Color.FromArgb(229, 115, 115); msg.Text = err; return; }
                await Load();
            };
            bClose.Click += (s, e) => f.Close();
            foot.AddRight(bNew, bEdit, bDel, bClose);
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Px(12), Theme.Px(8), Theme.Px(12), 0) };
            body.Controls.Add(list);
            f.Controls.Add(body); f.Controls.Add(msg); f.Controls.Add(foot); f.Controls.Add(head);
            f.Shown += async (s, e) => await Load();
            f.ShowDialog(this);
            await Task.CompletedTask;
        }

        // crear (a null) o cambiar un aviso; true si se guardó
        async Task<bool> EditAlert(Form owner, RoomAlert a)
        {
            var selObj = _objs.FirstOrDefault(o => o.Id == _sel);
            using var dlg = new FormDialog(a == null ? Tr("Nuevo aviso de incidencia") : Tr("Cambiar el aviso"), a == null ? Tr("Crear el aviso") : Tr("Guardar"), 560, "⚠",
                                           Tr("Les sale a los Puestos de Mando de la sala"));
            dlg.AddCombo("level", Tr("Nivel"), AlertLevels.Select(x => Tr(x.label)), Math.Max(0, Array.FindIndex(AlertLevels, x => x.level == (a?.Level ?? "precaucion"))));
            dlg.AddMultiline("text", Tr("Aviso"), a?.Text ?? "", Theme.Px(70));
            dlg.AddText("place", Tr("Lugar (opcional)"), a?.Place ?? "", Tr("Entre Bobadilla y Antequera, PK 143"));
            var where = new List<string> { Tr("Sin sitio en el mapa") };
            if (a?.Lat != null) where.Add(Tr("Donde estaba"));
            if (selObj != null) where.Add(string.Format(Tr("En el objeto elegido ({0})"), KindLabel(selObj.Kind)));
            dlg.AddCombo("where", Tr("Sitio en el mapa"), where, a?.Lat != null ? 1 : selObj != null && a == null ? where.Count - 1 : 0);
            dlg.AddInfo(Tr("Para ponerlo en un sitio, elige antes un objeto en el mapa (el aviso queda en su sitio)."));
            dlg.Validate = async d =>
            {
                string text = d.Get("text"); if (text.Length == 0) return Tr("Escribe el aviso.");
                if (text.Length > 200) return Tr("Como mucho 200 letras.");
                string w = where[Math.Max(0, d.Index("where"))];
                double? lat = null, lon = null;
                if (w == Tr("Donde estaba")) { lat = a.Lat; lon = a.Lon; }
                else if (selObj != null && w != Tr("Sin sitio en el mapa")) { lat = selObj.Lat; lon = selObj.Lon; }
                var (_, err) = await Rpc("room_alert_save", new
                {
                    p_id = a?.Id, p_level = AlertLevels[Math.Max(0, d.Index("level"))].level, p_text = text, p_place = d.Get("place"),
                    p_lat = lat, p_lon = lon, p_route = _route
                });
                return err;
            };
            return dlg.ShowDialog(owner) == DialogResult.OK;
        }
    }
}
