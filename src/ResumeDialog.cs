// Diálogo de partidas guardadas: lista los .save de OR y permite reanudar (-resume) o repetir (-replay).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ORTS.Settings;

namespace SelectOR
{
    public class ResumeDialog : Form
    {
        public string SelectedSaveFile { get; private set; }
        public string ModeFlag { get; private set; } = "-resume";

        class SaveItem
        {
            public string File;
            public string Route = "";
            public string Path = "";
            public TimeSpan GameTime;
            public DateTime RealTime;
            public bool Multiplayer;
            public override string ToString() => System.IO.Path.GetFileNameWithoutExtension(File);
        }

        ListBox _list;
        Label _detail;

        public ResumeDialog()
        {
            Text = I18n.T("Reanudar partida");
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(720, 460);
            MinimumSize = new Size(560, 380);
            try { Icon = Icon.ExtractAssociatedIcon((Environment.ProcessPath ?? AppContext.BaseDirectory)); } catch { }

            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var header = new Label { Text = "  " + I18n.T("Partidas guardadas"), Dock = DockStyle.Top, Height = 42, Font = Theme.Font(13f, FontStyle.Bold), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Surface };

            _list = new ListBox
            {
                Dock = DockStyle.Fill, BackColor = Theme.Surface, ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None, IntegralHeight = false,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 30
            };
            _list.DrawItem += DrawItem;
            _list.SelectedIndexChanged += (s, e) => ShowDetail();
            _list.DoubleClick += (s, e) => Accept("-resume");
            var listCard = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, Radius = 12, Padding = new Padding(6), Margin = new Padding(14) };
            listCard.Controls.Add(_list);
            var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 6), BackColor = Theme.Bg };
            listHost.Controls.Add(listCard);

            _detail = new Label { Dock = DockStyle.Top, Height = 44, ForeColor = Theme.Subtle, Padding = new Padding(16, 0, 16, 0), TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Bg };

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.Bg, Padding = new Padding(14, 8, 14, 12) };
            var btnResume = new RoundButton { Text = I18n.T("Reanudar"), Icon = "▶", Width = 150, Height = 40, Radius = 10, BaseColor = Theme.Accent, HoverColor = Theme.AccentHi, GradientTo = Theme.Accent2, TextColor = Color.White, FontSize = 11f, FontStyle = FontStyle.Bold, Dock = DockStyle.Right };
            var btnReplay = new RoundButton { Text = I18n.T("Repetir"), Icon = "⟳", Width = 130, Height = 40, Radius = 10, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 11f, Dock = DockStyle.Right };
            var spacer = new Panel { Width = 10, Dock = DockStyle.Right };
            btnResume.Click += (s, e) => Accept("-resume");
            btnReplay.Click += (s, e) => Accept("-replay");
            buttons.Controls.Add(btnResume);
            buttons.Controls.Add(spacer);
            buttons.Controls.Add(btnReplay);

            Controls.Add(listHost);
            Controls.Add(_detail);
            Controls.Add(buttons);
            Controls.Add(header);
            Controls.Add(stripe);

            LoadSaves();
        }

        void LoadSaves()
        {
            var items = new List<SaveItem>();
            try
            {
                var dir = UserSettings.UserDataFolder;
                foreach (var f in Directory.GetFiles(dir, "*.save"))
                {
                    var it = new SaveItem { File = f, RealTime = File.GetLastWriteTime(f) };
                    TryParse(it);
                    items.Add(it);
                }
            }
            catch { }
            items = items.OrderByDescending(i => i.RealTime).ToList();
            _list.Items.Clear();
            foreach (var i in items) _list.Items.Add(i);
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            else { _detail.Text = I18n.T("No hay partidas guardadas todavía. Se crean al guardar dentro del simulador (tecla F2)."); }
        }

        static void TryParse(SaveItem it)
        {
            try
            {
                using (var inf = new BinaryReader(File.Open(it.File, FileMode.Open, FileAccess.Read)))
                {
                    inf.ReadString();               // version
                    inf.ReadString();               // build
                    var routeOrMp = inf.ReadString();
                    if (routeOrMp == "$Multipl$") { it.Multiplayer = true; it.Route = inf.ReadString(); }
                    else it.Route = routeOrMp;
                    it.Path = inf.ReadString();
                    it.GameTime = new DateTime().AddSeconds(inf.ReadInt32()).TimeOfDay;
                }
            }
            catch { }
        }

        void DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var it = (SaveItem)_list.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(Theme.Surface)) e.Graphics.FillRectangle(b, e.Bounds);
            if (sel)
            {
                var r = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 2, e.Bounds.Width - 6, e.Bounds.Height - 4);
                Theme.FillRound(e.Graphics, r, 7, Theme.Surface2);
                Theme.FillRound(e.Graphics, new Rectangle(r.X + 2, r.Y + 4, 3, r.Height - 8), 2, Theme.Accent);
            }
            var left = new Rectangle(e.Bounds.X + 14, e.Bounds.Y, e.Bounds.Width - 220, e.Bounds.Height);
            var right = new Rectangle(e.Bounds.Right - 210, e.Bounds.Y, 200, e.Bounds.Height);
            string name = string.IsNullOrEmpty(it.Route) ? it.ToString() : it.Route + (it.Multiplayer ? "  (multijugador)" : "");
            TextRenderer.DrawText(e.Graphics, name, Font, left, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, it.RealTime.ToString("g"), Font, right, Theme.Subtle, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }

        void ShowDetail()
        {
            if (_list.SelectedItem is SaveItem it)
                _detail.Text = $"Recorrido: {(string.IsNullOrEmpty(it.Path) ? "—" : it.Path)}     ·     Tiempo de juego: {it.GameTime:hh\\:mm\\:ss}";
        }

        void Accept(string mode)
        {
            if (!(_list.SelectedItem is SaveItem it)) return;
            SelectedSaveFile = it.File;
            ModeFlag = mode;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
