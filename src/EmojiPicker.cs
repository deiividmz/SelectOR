// Selector de EMOJIS del chat: pestañas por categoría (frecuentes, caras, gestos, trenes y viaje, símbolos)
// y una cuadrícula que se desplaza con la rueda. Se dibujan en color (ColorText). Al elegir uno se avisa
// con EmojiChosen; quien lo usa lo inserta en la caja de texto. EmojiButton es el botón 😊 que lo abre.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace SelectOR
{
    public class EmojiPicker : Control
    {
        static readonly (string icon, string name, string[] items)[] Cats =
        {
            ("🕘", "Recientes", null),
            ("😀", "Caras", Split("😀 😃 😄 😁 😆 😅 😂 🤣 🙂 🙃 😉 😊 😇 🥰 😍 🤩 😘 😋 😛 😜 🤪 😝 🤑 🤗 🤭 🤫 🤔 🤐 🤨 😐 😑 😶 😏 😒 🙄 😬 😌 😔 😪 🤤 😴 😷 🤒 🤕 🤢 🤮 🥵 🥶 🥴 😵 🤯 🤠 🥳 😎 🤓 🧐 😕 😟 🙁 😮 😯 😲 😳 🥺 😦 😧 😨 😰 😥 😢 😭 😱 😖 😣 😞 😓 😩 😫 🥱 😤 😡 😠 🤬")),
            ("👍", "Gestos", Split("👍 👎 👌 ✌️ 🤞 🤟 🤘 🤙 👈 👉 👆 👇 ☝️ ✋ 🤚 🖐️ 🖖 👋 👏 🙌 👐 🤲 🤝 🙏 💪 ✍️ 👀 🧠")),
            ("🚆", "Trenes y viaje", Split("🚆 🚄 🚅 🚂 🚃 🚋 🚞 🚝 🚇 🚈 🚉 🚊 🚟 🚠 🛤️ 🚦 🚥 🚧 🚏 ⛽ 🗺️ 🧭 ⏱️ ⏰ 🕐 📍 🏁 🚩 🎫 🧳 🏔️ 🌉 🌄 🌅 🏙️ 🌧️ ⛈️ ❄️ 🌫️ ☀️ 🌙")),
            ("✅", "Símbolos", Split("✅ ❌ ⚠️ ⛔ 🚫 ❗ ❓ 💯 🔥 ⭐ 🌟 ✨ 🎉 🎊 🏆 🥇 🥈 🥉 ❤️ 💚 💙 💛 🧡 💜 🖤 💰 💶 📈 📉 📊 🔧 🛠️ ⚙️ 🔋 💡 📢 📣 🔔 ☕ 🍺 🍕 🎂")),
        };
        static readonly string[] Default = Split("👍 👌 🙏 💪 👏 😀 😂 😉 😎 🤔 😅 😮 😢 😡 🎉 🔥 ✅ ❌ ⚠️ ❤️ ⭐ 🚆 🚄 🚂");
        // Los últimos usados (para todo SelectOR mientras está abierto)
        static readonly List<string> Recent = new List<string>();

        // Igual que Theme.Font (escala de SelectOR y de Windows), con la letra de los emojis.
        public static Font EmojiFont(float size) => new Font("Segoe UI Emoji", Math.Max(5f, size * Theme.UiScale) * Theme.DpiComp);

        static string[] Split(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        public event Action<string> EmojiChosen;
        int _cat = 1, _scroll, _hover = -1, _hoverTab = -1;
        readonly int _cell, _tabH, _cols;
        readonly Font _fEmoji, _fTab;
        readonly ToolTip _tip = new ToolTip();

        public EmojiPicker(int cols = 8)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            _cols = cols;
            _cell = Theme.Px(36); _tabH = Theme.Px(34);
            _fEmoji = EmojiFont(15f);
            _fTab = EmojiFont(12f);
            BackColor = Color.FromArgb(36, 40, 44);
            Size = new Size(_cols * _cell + Theme.Px(12), _tabH + _cell * 6 + Theme.Px(10));
            if (Recent.Count == 0) _cat = 1; else _cat = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _fEmoji.Dispose(); _fTab.Dispose(); _tip.Dispose(); }
            base.Dispose(disposing);
        }

        string[] Items => _cat == 0 ? (Recent.Count > 0 ? Recent.ToArray() : Default) : Cats[_cat].items;

        Rectangle GridRect => new Rectangle(Theme.Px(6), _tabH + Theme.Px(4), _cols * _cell, Height - _tabH - Theme.Px(8));
        int Rows => (Items.Length + _cols - 1) / _cols;
        int MaxScroll => Math.Max(0, Rows * _cell - GridRect.Height);

        Rectangle TabRect(int i)
        {
            int w = (Width - Theme.Px(12)) / Cats.Length;
            return new Rectangle(Theme.Px(6) + i * w, Theme.Px(3), w, _tabH - Theme.Px(6));
        }

        Rectangle CellRect(int i)
        {
            var g = GridRect;
            return new Rectangle(g.X + (i % _cols) * _cell, g.Y + (i / _cols) * _cell - _scroll, _cell, _cell);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            // pestañas
            var tabs = new List<(string, Rectangle)>();
            for (int i = 0; i < Cats.Length; i++)
            {
                var r = TabRect(i);
                if (i == _cat || i == _hoverTab)
                    Theme.FillRound(g, r, Theme.Px(6), i == _cat ? Theme.Surface2 : Color.FromArgb(48, 52, 56));
                tabs.Add((Cats[i].icon, r));
            }
            ColorText.DrawCells(g, tabs, _fTab, Theme.Text, new Rectangle(0, 0, Width, _tabH));
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, Theme.Px(6), _tabH, Width - Theme.Px(6), _tabH);
            // cuadrícula (solo lo visible)
            var grid = GridRect;
            var items = Items;
            var cells = new List<(string, Rectangle)>();
            g.SetClip(grid);
            for (int i = 0; i < items.Length; i++)
            {
                var r = CellRect(i);
                if (r.Bottom < grid.Top || r.Top > grid.Bottom) continue;
                if (i == _hover) Theme.FillRound(g, Rectangle.Inflate(r, -2, -2), Theme.Px(6), Theme.Surface2);
                cells.Add((items[i], r));
            }
            g.ResetClip();
            ColorText.DrawCells(g, cells, _fEmoji, Theme.Text, grid);
            // el contenido que se sale por arriba o por abajo se tapa (DrawCells no recorta por celdas)
            using (var b = new SolidBrush(BackColor))
            {
                g.FillRectangle(b, 0, _tabH + 1, Width, grid.Top - _tabH - 1);
                g.FillRectangle(b, 0, grid.Bottom, Width, Height - grid.Bottom);
            }
            if (MaxScroll > 0)
            {
                int th = Math.Max(Theme.Px(20), grid.Height * grid.Height / (Rows * _cell));
                int ty = grid.Top + (grid.Height - th) * _scroll / MaxScroll;
                Theme.FillRound(g, new Rectangle(Width - Theme.Px(5), ty, Theme.Px(3), th), 2, Theme.Border);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _scroll = Math.Max(0, Math.Min(MaxScroll, _scroll - e.Delta / 120 * _cell));
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hov = -1, hovTab = -1;
            for (int i = 0; i < Cats.Length; i++) if (TabRect(i).Contains(e.Location)) hovTab = i;
            if (GridRect.Contains(e.Location))
            {
                var items = Items;
                for (int i = 0; i < items.Length; i++) if (CellRect(i).Contains(e.Location)) { hov = i; break; }
            }
            if (hov != _hover || hovTab != _hoverTab)
            {
                if (hovTab != _hoverTab) _tip.SetToolTip(this, hovTab >= 0 ? I18n.T(Cats[hovTab].name) : "");
                _hover = hov; _hoverTab = hovTab; Cursor = hov >= 0 || hovTab >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = _hoverTab = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            for (int i = 0; i < Cats.Length; i++)
                if (TabRect(i).Contains(e.Location)) { _cat = i; _scroll = 0; _hover = -1; Invalidate(); return; }
            if (_hover >= 0 && _hover < Items.Length)
            {
                string em = Items[_hover];
                Recent.Remove(em); Recent.Insert(0, em);
                if (Recent.Count > 24) Recent.RemoveAt(Recent.Count - 1);
                EmojiChosen?.Invoke(em);
            }
        }

        // Inserta un emoji donde está el cursor de la caja (respetando su límite de caracteres).
        public static void InsertInto(TextBox box, string emoji)
        {
            if (box == null || string.IsNullOrEmpty(emoji)) return;
            if (box.MaxLength > 0 && box.TextLength - box.SelectionLength + emoji.Length > box.MaxLength) return;
            int at = box.SelectionStart;
            box.SelectedText = emoji;
            box.SelectionStart = at + emoji.Length; box.SelectionLength = 0;
        }
    }

    // Botón redondo con 😊 en color; al pulsarlo abre el selector debajo o encima (donde quepa).
    public class EmojiButton : Control
    {
        bool _hover;
        readonly Font _f = EmojiPicker.EmojiFont(13f);
        ToolStripDropDown _drop;
        public TextBox Target;               // caja donde se escribe el emoji
        public Color Fill = Theme.Surface;

        public EmojiButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(Theme.Px(40), Theme.Px(40));
            Cursor = Cursors.Hand;
            BackColor = Theme.Bg;
            new ToolTip().SetToolTip(this, I18n.T("Emojis"));
        }

        protected override void Dispose(bool disposing) { if (disposing) { _f.Dispose(); _drop?.Dispose(); } base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Theme.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Theme.Px(10), _hover ? Theme.Surface2 : Fill);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            ColorText.DrawCells(g, new[] { ("😊", new Rectangle(0, 0, Width, Height)) }, _f, Theme.Text, new Rectangle(0, 0, Width, Height));
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (_drop != null && _drop.Visible) { _drop.Close(); return; }
            if (_drop == null)
            {
                var picker = new EmojiPicker();
                picker.EmojiChosen += em => EmojiPicker.InsertInto(Target, em);
                _drop = new ToolStripDropDown { Padding = Padding.Empty, BackColor = picker.BackColor, DropShadowEnabled = true };
                _drop.Items.Add(new ToolStripControlHost(picker) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = picker.Size });
            }
            // encima del botón si abajo no cabe (el chat está abajo de la ventana)
            var scr = Screen.FromControl(this).WorkingArea;
            var below = PointToScreen(new Point(Width - _drop.PreferredSize.Width, Height + 4));
            var p = below.Y + _drop.PreferredSize.Height > scr.Bottom
                ? new Point(below.X, PointToScreen(Point.Empty).Y - _drop.PreferredSize.Height - 4) : below;
            _drop.Show(p);
        }
    }

    // Botón 😊 plano (cajita de escribir del HUD): solo avisa con Click; quien lo usa decide qué abrir.
    public class EmojiButtonFlat : Control
    {
        bool _hover;
        readonly Font _f = EmojiPicker.EmojiFont(12f);

        public EmojiButtonFlat()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand; TabStop = false;
        }

        protected override void Dispose(bool disposing) { if (disposing) _f.Dispose(); base.Dispose(disposing); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (_hover)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Theme.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), 8, Theme.SurfaceHi);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            }
            ColorText.DrawCells(g, new[] { ("😊", new Rectangle(0, 0, Width, Height)) }, _f, Theme.Text, new Rectangle(0, 0, Width, Height));
        }
    }
}
