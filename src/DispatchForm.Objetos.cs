// (2026-11-04) PUESTO DE MANDO · «🚧 Objetos»: el administrador (1.2.91: y quien él autorice). Abre la ventana de los objetos de la sala
// (ObjetosSalaForm.cs: operarios de vía en un punto del mapa), que MainMenuForm le da en OpenObjects.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class DispatchForm
    {
        public Action OpenObjects;   // (null: no es el administrador, sin botón)
        Label _objBtn;

        void BuildObjectsBtn(Panel top)
        {
            _objBtn = new EmojiLabel
            {
                Text = "🚧  " + Tr("Objetos"), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Font(9f, FontStyle.Bold), Cursor = Cursors.Hand,
                Dock = DockStyle.Right, Visible = false, Width = Theme.Px(120), Margin = Padding.Empty, ForeColor = Theme.Text, BackColor = Theme.Surface2
            };
            _objBtn.Click += (s, e) => OpenObjects?.Invoke();
            new ToolTip().SetToolTip(_objBtn, Tr("Poner operarios de vía y otros objetos en la ruta: los ven en el 3D todos los que conducen en la sala"));
            var gap = new Panel { Dock = DockStyle.Right, Width = Theme.Px(6), BackColor = top.BackColor };
            top.Controls.Add(_objBtn); top.Controls.Add(gap);
            Shown += (s, e) => _objBtn.Visible = OpenObjects != null;
        }
    }
}
