// Paradas de una línea de megafonía: simplemente QUÉ estaciones para.
// Sin orden: el aviso salta al acercarse a la estación (por proximidad y sentido de marcha), así
// que de la línea solo importa qué estaciones la forman. Se marcan y se desmarcan con un clic.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SelectOR
{
    public class PaLineStopsDialog : Form
    {
        // Paradas elegidas, con el nombre normalizado (el que se guarda).
        public List<string> Stops { get; private set; } = new List<string>();

        readonly CardTable _list;
        readonly Label _count;
        readonly RoundedInput _filter;
        readonly List<(string norm, string disp)> _all;
        readonly bool[] _on;
        readonly List<int> _view = new List<int>();   // fila visible → índice en _all (con el filtro puesto)

        public PaLineStopsDialog(string lineName, List<(string norm, string disp)> routeStations, List<string> current)
        {
            _all = routeStations ?? new List<(string, string)>();
            _on = new bool[_all.Count];
            if (current != null)
                foreach (string norm in current)
                    for (int i = 0; i < _all.Count; i++)
                        if (string.Equals(_all[i].norm, norm, StringComparison.OrdinalIgnoreCase)) { _on[i] = true; break; }

            Text = string.Format(I18n.T("Paradas de {0}"), lineName);
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MinimizeBox = false;
            ClientSize = new Size(560, 600);
            MinimumSize = new Size(420, 380);
            try { Icon = Icon.ExtractAssociatedIcon((Environment.ProcessPath ?? AppContext.BaseDirectory)); } catch { }

            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var header = new Label
            {
                Text = "  " + Text, Dock = DockStyle.Top, Height = 42,
                Font = Theme.Font(13f, FontStyle.Bold), ForeColor = Theme.Text,
                TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Surface
            };

            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Theme.Bg, ColumnCount = 1, RowCount = 4,
                Padding = new Padding(16, 10, 16, 6)
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // explicación
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // buscador
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // lista
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // botones + cuenta

            body.Controls.Add(new Label
            {
                Text = I18n.T("Marca las estaciones en las que para esta línea: son las únicas que se anunciarán cuando el maquinista la lleve puesta. Un clic marca o desmarca; el buscador filtra la lista y los botones de abajo actúan sobre lo que se está viendo."),
                AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = Theme.Subtle,
                Font = Theme.Font(8.5f), Margin = new Padding(0, 0, 0, 8), BackColor = Theme.Bg
            });

            _filter = new RoundedInput(I18n.T("Buscar estación…")) { Dock = DockStyle.Fill, Height = 34, Margin = new Padding(0, 0, 0, 8) };
            _filter.Box.TextChanged += (s, e) => Fill();
            body.Controls.Add(_filter);

            // Tarjetas finas con su casilla: un clic (o la barra espaciadora) marca o desmarca la parada.
            _list = new CardTable { Dock = DockStyle.Fill, Margin = new Padding(0), CheckCol = 0, TitleCol = 1, ShowAvatar = false,
                                    CardHeight = 40, MinWidth = 230, Columns = 3, BackColor = Theme.Bg };
            _list.MouseClick += (s, e) => Toggle(_list.SelectedRow);
            _list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Space) { Toggle(_list.SelectedRow); e.Handled = true; } };
            _list.Cursor = Cursors.Hand;
            body.Controls.Add(_list);

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 6, 0, 0)
            };
            bottom.Controls.Add(Small(I18n.T("Marcar las mostradas"), () => SetAll(true)));
            bottom.Controls.Add(Small(I18n.T("Quitar las mostradas"), () => SetAll(false)));
            _count = new Label { AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(9f), Margin = new Padding(12, 9, 0, 0), BackColor = Theme.Bg };
            bottom.Controls.Add(_count);
            body.Controls.Add(bottom);

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Theme.Bg, Padding = new Padding(18, 8, 18, 12) };
            var ok = new RoundButton { Text = I18n.T("Guardar"), Width = 150, Height = 38, Radius = 10, BaseColor = Theme.Accent, HoverColor = Theme.AccentHi, GradientTo = Theme.Accent2, TextColor = Color.White, FontSize = 10.5f, FontStyle = FontStyle.Bold, Dock = DockStyle.Right };
            var cancel = new RoundButton { Text = I18n.T("Cancelar"), Width = 120, Height = 38, Radius = 10, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 10.5f, Dock = DockStyle.Right };
            ok.Click += (s, e) =>
            {
                Stops.Clear();
                for (int i = 0; i < _all.Count; i++) if (_on[i]) Stops.Add(_all[i].norm);
                DialogResult = DialogResult.OK; Close();
            };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(new Panel { Width = 10, Dock = DockStyle.Right });
            buttons.Controls.Add(cancel);

            Controls.Add(body);
            Controls.Add(buttons);
            Controls.Add(header);
            Controls.Add(stripe);

            Fill();
        }

        static RoundButton Small(string text, Action onClick)
        {
            var b = new RoundButton
            {
                Text = text, Width = 180, Height = 32, Radius = 9, Margin = new Padding(0, 4, 8, 0),
                BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 9.5f
            };
            b.Click += (s, e) => onClick();
            return b;
        }

        void Toggle(int fila)
        {
            if (fila < 0 || fila >= _view.Count) return;
            int i = _view[fila];
            _on[i] = !_on[i];
            Fill(fila);
        }

        // Los botones actúan sobre lo que se está viendo: con el buscador puesto, solo sobre esas.
        void SetAll(bool value)
        {
            foreach (int i in _view) _on[i] = value;
            Fill(_list.SelectedRow);
        }

        // Comparación tolerante: sin acentos, sin signos y sin mayúsculas.
        static string Loose(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var norm = s.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(norm.Length);
            foreach (char c in norm)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        void Fill(int keep = -1)
        {
            string q = Loose(_filter?.Box.Text);
            _view.Clear();
            _list.ClearRows();
            int marcadas = 0;
            for (int i = 0; i < _all.Count; i++)
            {
                if (_on[i]) marcadas++;
                if (q.Length > 0 && Loose(_all[i].disp).IndexOf(q, StringComparison.Ordinal) < 0) continue;
                _view.Add(i);
                _list.AddRow(new[] { _on[i] ? "✓" : "", _all[i].disp },
                             new Color?[] { Theme.Accent, _on[i] ? (Color?)null : Theme.Subtle });
            }
            if (_all.Count == 0) _list.SetEmpty(I18n.T("No se han encontrado estaciones en esta ruta."));
            else if (_view.Count == 0) _list.SetEmpty(I18n.T("Ninguna estación coincide con la búsqueda."));
            _count.Text = q.Length > 0
                ? string.Format(I18n.T("{0} de {1} marcadas · {2} mostradas"), marcadas, _all.Count, _view.Count)
                : string.Format(I18n.T("{0} de {1} estaciones marcadas"), marcadas, _all.Count);
            if (keep >= 0) _list.SelectRow(keep);
        }
    }
}
