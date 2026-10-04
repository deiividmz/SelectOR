// Menús (clic derecho, «Mi cuenta»…) con el estilo de SelectOR: fondo oscuro, filas holgadas, resaltado
// redondeado, casillas en verde, separadores finos y, en Windows 11, la ventana con las esquinas
// redondeadas. MenuStyle.Apply(menu) y listo: los colores propios de cada opción (p. ej. en rojo las que
// borran) se respetan.
//
// Márgenes simétricos: Windows reserva a la izquierda una columna para iconos y a la derecha un hueco para
// los atajos de teclado, así que el texto quedaba descentrado. Aquí cada opción mide exactamente su texto
// más el mismo margen a izquierda y derecha (y la casilla, si el menú tiene), y el texto y la casilla se
// dibujan a mano en su sitio.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SelectOR
{
    public static class MenuStyle
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

        static int Pad => Theme.Px(14);          // margen del texto, igual a izquierda y derecha
        static int Edge => Theme.Px(6);          // margen del menú, igual en los cuatro lados
        static int RowH => Theme.Px(34);
        static int SepH => Theme.Px(11);
        static int CheckW => Theme.Px(26);       // columna de la casilla (solo si el menú tiene casillas)

        public static T Apply<T>(T menu) where T : ToolStripDropDown
        {
            menu.Renderer = new DarkMenuRenderer();
            menu.BackColor = Theme.Surface;
            menu.ForeColor = Theme.Text;
            menu.Font = Theme.Font(9.75f);
            menu.DropShadowEnabled = true;
            menu.HandleCreated += (s, e) => Round(menu.Handle);
            // Se mide al abrirse (antes de que el menú se coloque en pantalla). Si quien usa el menú oculta opciones
            // o les cambia el texto en su propio «Opening», llama después a MenuStyle.Measure(menu).
            menu.Opening += (s, e) => Measure(menu);
            if (menu.IsHandleCreated) Round(menu.Handle);
            return menu;
        }

        // Ancho del contenido de cada menú (casilla + texto más largo): se dibuja centrado en el menú, así el
        // hueco de la izquierda y el de la derecha son siempre iguales.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolStrip, System.Runtime.CompilerServices.StrongBox<int>> _content = new();

        // Dónde empieza el contenido, en coordenadas del menú.
        static int ContentX(ToolStrip menu)
        {
            if (menu == null || !_content.TryGetValue(menu, out var c)) return Edge + Pad;
            return Math.Max(Edge + Pad, (menu.Width - c.Value) / 2);
        }

        static void Round(IntPtr h) { try { int v = DWMWCP_ROUND; DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, 4); } catch { } }

        static bool HasChecks(ToolStrip menu)
        {
            foreach (ToolStripItem it in menu.Items)
                if (it.Available && it is ToolStripMenuItem mi && (mi.CheckOnClick || mi.Checked)) return true;
            return false;
        }

        // El menú, con su tamaño exacto: el mismo margen arriba y abajo (Edge) y a izquierda y derecha
        // (Edge + Pad hasta el texto). Windows reserva columnas para iconos y atajos, cambia el margen interior
        // cada vez que se tocan esas columnas y guarda anchos viejos; por eso el tamaño lo fija SelectOR.
        public static void Measure(ToolStripDropDown menu)
        {
            if (menu is ToolStripDropDownMenu dm)
            {
                if (dm.ShowImageMargin) dm.ShowImageMargin = false;   // tocarlas reinicia el margen interior
                if (dm.ShowCheckMargin) dm.ShowCheckMargin = false;
            }
            int check = HasChecks(menu) ? CheckW : 0, textW = 0, h = 0;
            foreach (ToolStripItem it in menu.Items)
            {
                if (!it.Available) continue;
                h += it is ToolStripSeparator ? SepH : RowH;
                if (it is ToolStripSeparator) continue;
                textW = Math.Max(textW, TextRenderer.MeasureText(it.Text ?? "", it.Font ?? menu.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width);
                if (it is ToolStripMenuItem mi && mi.HasDropDownItems) Apply(mi.DropDown);
            }
            int menuW = Edge + Pad + check + textW + Pad + Edge;
            _content.AddOrUpdate(menu, new System.Runtime.CompilerServices.StrongBox<int>(check + textW));
            // Windows pone su propio margen interior al abrir el menú (y lo repone si se cambia): el que falta
            // hasta Edge, arriba y abajo, va como margen de la primera y la última opción visibles.
            ToolStripItem first = null, last = null;
            foreach (ToolStripItem it in menu.Items) if (it.Available) { first ??= it; last = it; }
            int top = Math.Max(0, Edge - menu.Padding.Top), bottom = Math.Max(0, Edge - menu.Padding.Bottom);
            menu.SuspendLayout();
            foreach (ToolStripItem it in menu.Items)
            {
                var size = new Size(menuW, it is ToolStripSeparator ? SepH : RowH);   // cada opción, de lado a lado
                var margin = new Padding(0, it == first ? top : 0, 0, it == last ? bottom : 0);
                if (!it.AutoSize && it.Size == size && it.Margin == margin) continue;   // ya está: no se vuelve a maquetar
                it.AutoSize = false;
                it.Margin = margin;
                it.Padding = Padding.Empty;
                it.Size = size;
            }
            menu.AutoSize = false;
            var total = new Size(menuW, menu.Padding.Top + top + h + bottom + menu.Padding.Bottom);
            if (menu.Size != total) menu.Size = total;
            menu.ResumeLayout(true);
        }

        sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Theme.Surface;
            public override Color ImageMarginGradientBegin => Theme.Surface;
            public override Color ImageMarginGradientMiddle => Theme.Surface;
            public override Color ImageMarginGradientEnd => Theme.Surface;
            public override Color MenuBorder => Color.FromArgb(70, 76, 82);
            public override Color MenuItemBorder => Color.Transparent;
            public override Color MenuItemSelected => Theme.SurfaceHi;
            public override Color SeparatorDark => Color.FromArgb(64, 69, 74);
            public override Color SeparatorLight => Color.Transparent;
        }

        sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
        {
            public DarkMenuRenderer() : base(new Colors()) { RoundedEdges = false; }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using var b = new SolidBrush(Theme.Surface);
                e.Graphics.FillRectangle(b, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
                using var p = new Pen(Color.FromArgb(70, 76, 82));
                e.Graphics.DrawRectangle(p, r);
            }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

            // Resaltado (con el mismo margen a los dos lados) y, en los menús con casillas, la casilla.
            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                var g = e.Graphics;
                if (e.Item.Selected && e.Item.Enabled)
                {
                    int x0 = Edge - e.Item.Bounds.X;   // el resaltado, con el mismo margen a los dos lados del menú
                    var r = new Rectangle(x0, 1, e.ToolStrip.Width - 2 * Edge, e.Item.Height - 2);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Theme.FillRound(g, r, Theme.Px(6), Theme.SurfaceHi);
                    g.SmoothingMode = SmoothingMode.None;
                }
                if (e.Item is ToolStripMenuItem mi && mi.Checked)
                {
                    int s = Theme.Px(16);
                    var box = new Rectangle(ContentX(e.ToolStrip) - e.Item.Bounds.X, (e.Item.Height - s) / 2, s, s);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Theme.FillRound(g, box, Theme.Px(4), Theme.Accent);
                    using (var p = new Pen(Color.White, Math.Max(1.6f, s / 8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLines(p, new[] { new PointF(box.X + s * 0.25f, box.Y + s * 0.52f), new PointF(box.X + s * 0.43f, box.Y + s * 0.70f), new PointF(box.X + s * 0.76f, box.Y + s * 0.32f) });
                    g.SmoothingMode = SmoothingMode.None;
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                var fc = e.Item.ForeColor;
                var color = !e.Item.Enabled ? Color.FromArgb(110, 116, 122)
                          : fc == SystemColors.ControlText || fc.IsEmpty ? Theme.Text : fc;
                int left = ContentX(e.ToolStrip);
                int x = left - e.Item.Bounds.X + (e.ToolStrip != null && HasChecks(e.ToolStrip) ? CheckW : 0);
                int right = (e.ToolStrip?.Width ?? e.Item.Width) - left - e.Item.Bounds.X;   // mismo margen a la derecha
                var r = new Rectangle(x, 0, Math.Max(1, right - x), e.Item.Height);
                TextRenderer.DrawText(e.Graphics, e.Item.Text, e.TextFont, r, color,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using var p = new Pen(Color.FromArgb(64, 69, 74));
                int left = ContentX(e.ToolStrip), ox = e.Item.Bounds.X;
                e.Graphics.DrawLine(p, left - ox, y, e.ToolStrip.Width - left - ox, y);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }   // la dibuja OnRenderMenuItemBackground

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Theme.Subtle;
                base.OnRenderArrow(e);
            }
        }
    }
}
