// (2026-11-10, petición) La columna de DEPENDENCIAS se pliega sobre el margen izquierdo (««» junto al título): queda una tira
// estrecha con «»» y el rótulo en vertical; un clic en ella la vuelve a abrir. Se recuerda (FoldGet/FoldSet: las preferencias).

using System;
using System.Drawing;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class DispatchForm
    {
        public Func<bool> FoldGet;      // (MainMenuForm) plegada la última vez
        public Action<bool> FoldSet;
        Panel _side, _foldStrip; Label _foldBtn, _sideCap; int _sideW; bool _folded, _foldLoaded;

        void BuildFold(Panel side, Label cap)
        {
            _side = side; _sideCap = cap; _sideW = side.Width;
            _foldBtn = new Label
            {
                Text = "«", Dock = DockStyle.Right, Width = Theme.Px(26), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand,
                Font = Theme.Font(11f, FontStyle.Bold), ForeColor = Theme.Subtle, BackColor = cap.BackColor
            };
            _foldBtn.MouseEnter += (s, e) => _foldBtn.ForeColor = Theme.Text;
            _foldBtn.MouseLeave += (s, e) => _foldBtn.ForeColor = Theme.Subtle;
            _foldBtn.Click += (s, e) => SetFolded(true);
            new ToolTip().SetToolTip(_foldBtn, Tr("Plegar la lista de dependencias"));
            cap.Controls.Add(_foldBtn);

            _foldStrip = new Panel { Dock = DockStyle.Fill, BackColor = side.BackColor, Cursor = Cursors.Hand, Visible = false };
            _foldStrip.Paint += (s, e) =>
            {
                var g = e.Graphics; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (var f = Theme.Font(11f, FontStyle.Bold))
                    TextRenderer.DrawText(g, "»", f, new Rectangle(0, 0, _foldStrip.Width, Theme.Px(30)), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                // el rótulo, de arriba abajo
                using var f2 = Theme.Font(8.5f, FontStyle.Bold); using var b = new SolidBrush(Theme.Subtle);
                var st = g.Save();
                g.TranslateTransform(_foldStrip.Width / 2f + Theme.Px(6), Theme.Px(40));
                g.RotateTransform(90);
                g.DrawString(Tr("DEPENDENCIAS"), f2, b, 0, 0);
                g.Restore(st);
            };
            _foldStrip.Click += (s, e) => SetFolded(false);
            new ToolTip().SetToolTip(_foldStrip, Tr("Abrir la lista de dependencias"));
            side.Controls.Add(_foldStrip);
            _foldStrip.BringToFront();
            Shown += (s, e) => { if (_foldLoaded) return; _foldLoaded = true; bool f = false; try { f = FoldGet?.Invoke() ?? false; } catch { } if (f) SetFolded(true, false); };
        }

        void SetFolded(bool on, bool save = true)
        {
            if (_side == null || _folded == on) return;
            _folded = on;
            SuspendLayout();
            foreach (Control c in _side.Controls) if (c != _foldStrip) c.Visible = !on;
            if (!on && _mineBtn != null) MineRefresh();   // (el botón «Solo las mías», como estaba)
            _foldStrip.Visible = on;
            _side.Padding = on ? Padding.Empty : new Padding(Theme.Px(8));
            _side.Width = on ? Theme.Px(30) : _sideW;
            ResumeLayout(true);
            if (save) try { FoldSet?.Invoke(on); } catch { }
            // (el plano se queda donde estaba: se corre lo que ha cambiado el ancho)
            _canvas.Invalidate();
        }
    }
}
