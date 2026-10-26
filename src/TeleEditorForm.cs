// (1.2.77) Ventana «Teleindicadores del tren» (botón ✎ junto al selector del teleindicador, en Conducción libre y
// Horarios): los destinos del tren (los del autor y los hechos con SelectOR) con su cartel, y Nuevo, Modificar y Eliminar
// (TeleEditor.cs). Nuevo: un destino del tren como modelo, el nombre y el texto (una o varias líneas), con la vista previa
// del cartel al momento. Los del autor sirven de modelo; solo los de SelectOR se modifican o se eliminan.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class TeleEditorForm : Form
    {
        readonly List<string> _folders;
        readonly ListBox _list;
        readonly PictureBox _pic;
        readonly Label _info;
        List<(string name, string sample)> _dests = new();
        public bool Changed { get; private set; }
        public string Created { get; private set; }   // el último creado o modificado (para elegirlo al volver)

        static string Tr(string s) => I18n.T(s);

        public TeleEditorForm(List<string> folders, string trainName)
        {
            _folders = folders;
            Text = Tr("Teleindicadores del tren");
            StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(Theme.Px(760), Theme.Px(470)); BackColor = Theme.Bg;
            DialogKit.Dress(this);

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Px(20), Theme.Px(12), Theme.Px(20), Theme.Px(8)) };
            _list = new ListBox
            {
                Dock = DockStyle.Left, Width = Theme.Px(300), BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.Px(46), IntegralHeight = false, Font = Theme.Font(9.5f)
            };
            _list.DrawItem += DrawItem;
            _list.SelectedIndexChanged += (s, e) => ShowSelected();
            _list.DoubleClick += (s, e) => EditSelected();
            try { Native.UseDarkScrollBars(_list); } catch { }
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Px(14), 0, 0, 0) };
            _pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(24, 26, 30) };
            _info = new Label { Dock = DockStyle.Bottom, Height = Theme.Px(56), ForeColor = Theme.Subtle, BackColor = Theme.Bg, Font = Theme.Font(8.75f), Padding = new Padding(0, Theme.Px(6), 0, 0) };
            right.Controls.Add(_pic); right.Controls.Add(_info);
            body.Controls.Add(right); body.Controls.Add(_list);

            var foot = new DialogKit.Footer();
            var close = DialogKit.Primary(Tr("Cerrar"), 120); close.Click += (s, e) => Close();
            var bNew = DialogKit.Fit(DialogKit.Secondary(Tr("Nuevo…")), 110); bNew.Click += (s, e) => NewFromSelected();
            var bEdit = DialogKit.Fit(DialogKit.Secondary(Tr("Modificar…")), 110); bEdit.Click += (s, e) => EditSelected();
            var bDel = DialogKit.Fit(DialogKit.Secondary(Tr("Eliminar")), 110); bDel.Click += (s, e) => DeleteSelected();
            foot.AddRight(close);
            // (con Dock = Left el margen no cuenta: huecos entre los botones)
            foreach (var b in new Control[] { bDel, bEdit, bNew })
            {
                if (b != bDel) foot.AddLeft(new Panel { Width = Theme.Px(14), BackColor = Theme.Surface });
                foot.AddLeft(b);
            }

            Controls.Add(body); Controls.Add(foot);
            DialogKit.Crown(this, Tr("Teleindicadores del tren"), trainName + " · " + Tr("haz destinos nuevos a partir de los del tren"), "🚉");
            Reload(null);
        }

        void Reload(string select)
        {
            _dests = Teleindicadores.Destinations(_folders);
            _list.Items.Clear();
            foreach (var d in _dests) _list.Items.Add(d.name);
            int i = select == null ? 0 : Math.Max(0, _dests.FindIndex(d => string.Equals(d.name, select, StringComparison.OrdinalIgnoreCase)));
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(i, _list.Items.Count - 1);
            ShowSelected();
        }

        string SelName => _list.SelectedIndex >= 0 && _list.SelectedIndex < _dests.Count ? _dests[_list.SelectedIndex].name : null;
        bool Mine(string name) => name != null && TeleEditor.SpecOf(_folders, name) != null;

        void DrawItem(object s, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _dests.Count) return;
            var (name, sample) = _dests[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            var g = e.Graphics;
            using (var b = new SolidBrush(sel ? Color.FromArgb(40, 60, 46) : Theme.Surface)) g.FillRectangle(b, e.Bounds);
            var th = Teleindicadores.LedThumb(sample, Theme.Px(22));
            int x = e.Bounds.X + Theme.Px(8);
            if (th != null)
            {
                int w = Math.Min(th.Width, Theme.Px(120));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(th, new Rectangle(x, e.Bounds.Y + (e.Bounds.Height - th.Height) / 2, w, th.Height));
                x += Theme.Px(128);
            }
            else x += Theme.Px(128);
            bool mine = Mine(name);
            TextRenderer.DrawText(g, Teleindicadores.Nice(name), _list.Font, new Rectangle(x, e.Bounds.Y + Theme.Px(4), e.Bounds.Right - x - 4, e.Bounds.Height / 2), sel ? Theme.Text : Theme.Text,
                                  TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using var fs = Theme.Font(8f);
            TextRenderer.DrawText(g, mine ? Tr("hecho con SelectOR") : Tr("del autor"), fs, new Rectangle(x, e.Bounds.Y + e.Bounds.Height / 2, e.Bounds.Right - x - 4, e.Bounds.Height / 2 - Theme.Px(4)),
                                  mine ? Theme.AccentHi : Theme.Subtle, TextFormatFlags.Top | TextFormatFlags.NoPrefix);
        }

        void ShowSelected()
        {
            var n = SelName;
            var old = _pic.Image; _pic.Image = null; old?.Dispose();
            if (n == null) { _info.Text = Tr("Este tren no tiene destinos."); return; }
            var sample = _dests[_list.SelectedIndex].sample;
            try
            {
                using var bmp = sample.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? AceImage.LoadDds(sample, out _) : AceImage.Load(sample);
                _pic.Image = Teleindicadores.Readable(bmp, Theme.Px(90));   // (como se ve en el tren, no la textura aplastada)
            }
            catch { }
            var spec = TeleEditor.SpecOf(_folders, n);
            _info.Text = spec != null
                ? string.Format(Tr("Hecho con SelectOR a partir de «{0}»: «{1}». Se puede modificar o eliminar."), Teleindicadores.Nice(spec.model), string.Join(" / ", spec.lines))
                : Tr("Destino del autor: no se toca, pero sirve de modelo para hacer otros («Nuevo…»).");
        }

        void NewFromSelected()
        {
            var n = SelName; if (n == null) return;
            var spec = TeleEditor.SpecOf(_folders, n);
            Edit(spec?.model ?? n, null, new List<string>());
        }

        void EditSelected()
        {
            var n = SelName; if (n == null) return;
            var spec = TeleEditor.SpecOf(_folders, n);
            if (spec == null) { ThemedBox.Show(this, Tr("Ese destino es del autor del tren: no se modifica. Usa «Nuevo…» para hacer uno a partir de él."), "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            Edit(spec.model, n, spec.lines ?? new List<string>());
        }

        void DeleteSelected()
        {
            var n = SelName; if (n == null) return;
            if (!Mine(n)) { ThemedBox.Show(this, Tr("Solo se pueden eliminar los destinos hechos con SelectOR."), "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (ThemedBox.Show(this, string.Format(Tr("¿Eliminar el destino «{0}»?"), Teleindicadores.Nice(n)), "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var err = TeleEditor.Delete(_folders, n);
            if (err != null) { ThemedBox.Show(this, err, "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Changed = true; Reload(null);
        }

        // nuevo (existing == null) o modificar uno de SelectOR
        void Edit(string model, string existing, List<string> lines)
        {
            // modelos: los destinos del autor (y el modelo de siempre de este, si es de SelectOR)
            var models = _dests.Select(d => d.name).Where(d => !Mine(d) || d == model).ToList();
            if (!models.Contains(model)) models.Insert(0, model);
            using var dlg = new FormDialog(existing == null ? Tr("Nuevo teleindicador") : Tr("Modificar el teleindicador"), Tr("Guardar"), 560, "🚉",
                                           Tr("El texto se dibuja como en el cartel del modelo (mismos puntos y colores)."));
            dlg.AddCombo("model", Tr("Modelo (un destino del tren)"), models.Select(Teleindicadores.Nice), Math.Max(0, models.IndexOf(model)));
            var name = dlg.AddText("name", Tr("Nombre del destino"), existing == null ? "" : Teleindicadores.Nice(existing), Tr("p. ej. Valladolid"));
            if (existing != null) name.Enabled = false;
            var l1 = dlg.AddText("l1", Tr("Texto (línea 1)"), lines.ElementAtOrDefault(0) ?? "", Tr("p. ej. VALLADOLID"));
            var l2 = dlg.AddText("l2", Tr("Línea 2 (si el cartel tiene más de una)"), lines.ElementAtOrDefault(1) ?? "");
            var l3 = dlg.AddText("l3", Tr("Línea 3"), lines.ElementAtOrDefault(2) ?? "");
            var pv = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(24, 26, 30) };
            dlg.AddControl(pv, Theme.Px(150));
            var warn = dlg.AddInfo(" ");
            void Update()
            {
                string m = models[Math.Max(0, dlg.Index("model"))];
                var ls = new List<string> { l1.Box.Text, l2.Box.Text, l3.Box.Text };   // (la caja de dentro: el contenedor no tiene el texto)
                var old = pv.Image; pv.Image = null; old?.Dispose();
                try { using var raw = TeleEditor.Preview(_folders, m, ls); pv.Image = Teleindicadores.Readable(raw, Theme.Px(90)); } catch { }
                warn.ForeColor = TeleEditor.LastFits ? Theme.Subtle : Color.FromArgb(255, 183, 77);
                warn.Text = TeleEditor.LastFits ? Tr("Vista previa del cartel (como se verá en el tren).") : Tr("No cabe todo en el cartel: se corta. Abrévialo o usa menos letras.");
            }
            foreach (var t in new[] { l1, l2, l3 }) t.Box.TextChanged += (s, e) => Update();   // (al momento, letra a letra)
            ((ComboBox)dlg.Field("model")).SelectedIndexChanged += (s, e) => Update();
            dlg.Shown += (s, e) => Update();
            dlg.Validate = async d =>
            {
                await System.Threading.Tasks.Task.Yield();
                string m = models[Math.Max(0, d.Index("model"))];
                string n = existing ?? d.Get("name");
                if (existing == null && _dests.Any(x => string.Equals(x.name, TeleEditor.FolderName(n), StringComparison.OrdinalIgnoreCase)))
                    return string.Format(Tr("Ya hay un destino que se llama «{0}»."), n);
                var err = TeleEditor.Save(_folders, m, n, new List<string> { d.Get("l1"), d.Get("l2"), d.Get("l3") }, existing != null);
                if (err == null) Created = existing ?? TeleEditor.FolderName(n);
                return err;
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) { Changed = true; Reload(Created); }
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { _pic.Image?.Dispose(); base.OnFormClosed(e); }
    }
}
