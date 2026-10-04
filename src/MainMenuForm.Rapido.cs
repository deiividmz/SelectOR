// Mostrar una sección o una pestaña sin volver a maquetarla entera.
// Hacer visible un panel con tablas y flujos que se ajustan a su contenido obliga a WinForms a recalcular
// todo su árbol (en Mi perfil, ~50 ms cada vez, aunque nada haya cambiado). Si el panel ocupa ya justo
// el hueco de su contenedor (no ha cambiado el tamaño de la ventana mientras estaba oculto), su
// maquetación sigue valiendo: se enseña con todo el árbol en pausa (~9 ms). Si el tamaño no coincide,
// se enseña como siempre.

using System.Collections.Generic;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        static void ShowFast(Control p, bool show)
        {
            if (p == null) return;
            if (!show || p.Visible || p.Parent == null || p.Dock != DockStyle.Fill || p.Size != p.Parent.ClientSize || !p.IsHandleCreated)
            {
                p.Visible = show;
                return;
            }
            var tree = new List<Control>(128);
            void Walk(Control c) { if (c.Controls.Count == 0) return; tree.Add(c); foreach (Control x in c.Controls) Walk(x); }
            Walk(p);
            var parent = p.Parent;
            parent.SuspendLayout();
            foreach (var c in tree) c.SuspendLayout();
            p.Visible = true;
            for (int k = tree.Count - 1; k >= 0; k--) tree[k].ResumeLayout(false);
            parent.ResumeLayout(false);
        }
    }
}
