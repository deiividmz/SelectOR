// Al comprar una máquina se elige QUÉ COMPOSICIÓN sirve de referencia para el precio: lo que se
// adquiere es la motriz —y con ella se puede conducir cualquier .con que la lleve de cabeza—, pero
// el coste simula el de un tren completo, con sus coches.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SelectOR
{
    public class ConsistPriceDialog : Form
    {
        public sealed class Opcion
        {
            public string Nombre;        // nombre del .con
            public int Vehiculos;
            public double Plazas, Masa, Precio;
            public object Tag;           // el TrainItem correspondiente
        }

        public int Elegida { get; private set; } = -1;

        readonly StyledTable _list;
        readonly List<Opcion> _ops;

        public ConsistPriceDialog(string motriz, List<Opcion> opciones, int porDefecto)
        {
            _ops = opciones ?? new List<Opcion>();
            Text = string.Format(I18n.T("Comprar «{0}»"), motriz);
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MinimizeBox = false;
            ClientSize = new Size(720, 480);
            MinimumSize = new Size(560, 360);
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
                Dock = DockStyle.Fill, BackColor = Theme.Bg, ColumnCount = 1, RowCount = 2,
                Padding = new Padding(16, 10, 16, 6)
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            body.Controls.Add(new Label
            {
                Text = I18n.T("Lo que compras es la máquina, y con ella podrás conducir cualquier composición que la lleve de cabeza, la tenga montada como la tenga cada maquinista. Elige con qué composición se tasa la compra: cuantos más coches y más masa arrastre, más cuesta."),
                AutoSize = true, MaximumSize = new Size(660, 0), ForeColor = Theme.Subtle,
                Font = Theme.Font(8.5f), Margin = new Padding(0, 0, 0, 10), BackColor = Theme.Bg
            });

            _list = new StyledTable { Dock = DockStyle.Fill, Margin = new Padding(0) };
            Native.UseDarkScrollBars(_list);
            _list.SetColumns(
                new StyledTable.Col(I18n.T("COMPOSICIÓN"), 260, true),
                new StyledTable.Col(I18n.T("VEHÍCULOS"), 100, false, HorizontalAlignment.Right),
                new StyledTable.Col(I18n.T("PLAZAS"), 90, false, HorizontalAlignment.Right),
                new StyledTable.Col(I18n.T("MASA"), 100, false, HorizontalAlignment.Right),
                new StyledTable.Col(I18n.T("PRECIO"), 130, false, HorizontalAlignment.Right));
            _list.DoubleClick += (s, e) => Aceptar();
            body.Controls.Add(_list);

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Theme.Bg, Padding = new Padding(18, 8, 18, 12) };
            var ok = new RoundButton { Text = I18n.T("Comprar"), Width = 160, Height = 38, Radius = 10, BaseColor = Theme.Accent, HoverColor = Theme.AccentHi, GradientTo = Theme.Accent2, TextColor = Color.White, FontSize = 10.5f, FontStyle = FontStyle.Bold, Dock = DockStyle.Right };
            var cancel = new RoundButton { Text = I18n.T("Cancelar"), Width = 120, Height = 38, Radius = 10, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 10.5f, Dock = DockStyle.Right };
            ok.Click += (s, e) => Aceptar();
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(new Panel { Width = 10, Dock = DockStyle.Right });
            buttons.Controls.Add(cancel);

            Controls.Add(body);
            Controls.Add(buttons);
            Controls.Add(header);
            Controls.Add(stripe);

            Fill(porDefecto);
        }

        void Fill(int sel)
        {
            var es = System.Globalization.CultureInfo.GetCultureInfo("es-ES");
            _list.ClearRows();
            foreach (var o in _ops)
                _list.AddRow(new[]
                {
                    o.Nombre,
                    o.Vehiculos.ToString("N0", es),
                    o.Plazas > 0 ? o.Plazas.ToString("N0", es) : "—",
                    o.Masa > 0 ? o.Masa.ToString("N0", es) + " t" : "—",
                    o.Precio.ToString("N0", es) + " €"
                },
                new Color?[] { null, null, null, null, Theme.Accent });
            if (_ops.Count == 0) _list.SetEmpty(I18n.T("No se ha encontrado ninguna composición con esta máquina."));
            if (sel >= 0 && sel < _list.Items.Count)
            {
                _list.Items[sel].Selected = true;
                _list.Items[sel].EnsureVisible();
            }
        }

        void Aceptar()
        {
            int i = _list.SelectedRow;
            if (i < 0 || i >= _ops.Count) return;
            Elegida = i;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
