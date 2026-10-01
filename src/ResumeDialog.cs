// Diálogo de partidas guardadas: lista los .save de OR (los que se crean con la tecla de guardar del
// simulador, F2 por defecto) y permite reanudar (-resume) o repetir (-replay). Muestra la captura que
// OR guarda junto a cada partida (.png), la ruta, el recorrido y cuándo se guardó.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    public class ResumeDialog : Form
    {
        public string SelectedSaveFile { get; private set; }
        public string ModeFlag { get; private set; } = "-resume";

        public sealed class SaveItem
        {
            public string File;
            public string Route = "";
            public string Path = "";
            public DateTime RealTime;
            public bool Multiplayer;
            public bool HasReplay;
            public override string ToString() => System.IO.Path.GetFileNameWithoutExtension(File);
        }

        // Fechas en el idioma de SelectOR (el de Open Rails), no en el de Windows.
        static System.Globalization.CultureInfo Cul => I18n.English ? new System.Globalization.CultureInfo("en-GB") : new System.Globalization.CultureInfo("es-ES");

        ListBox _list;
        Label _detail;
        PictureBox _thumb;
        Label _noThumb;
        RoundButton _btnReplay;

        public ResumeDialog()
        {
            Text = I18n.T("Reanudar partida");
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(900, 500);
            MinimumSize = new Size(700, 420);
            try { Icon = Icon.ExtractAssociatedIcon((Environment.ProcessPath ?? AppContext.BaseDirectory)); } catch { }

            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var header = new Label { Text = "  " + I18n.T("Partidas guardadas"), Dock = DockStyle.Top, Height = 42, Font = Theme.Font(13f, FontStyle.Bold), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Surface };

            _list = new BufferedListBox
            {
                Dock = DockStyle.Fill, BackColor = Theme.Surface, ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None, IntegralHeight = false,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 44
            };
            _list.DrawItem += DrawItem;
            _list.SelectedIndexChanged += (s, e) => ShowDetail();
            _list.DoubleClick += (s, e) => Accept("-resume");
            var listCard = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, Radius = 12, Padding = new Padding(6) };
            listCard.Controls.Add(_list);

            // Derecha: captura de la partida + datos
            _thumb = new PictureBox { Dock = DockStyle.Top, Height = 190, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Bg };
            _noThumb = new Label { Dock = DockStyle.Top, Height = 190, Text = I18n.T("Sin captura"), ForeColor = Theme.Subtle, TextAlign = ContentAlignment.MiddleCenter, BackColor = Theme.Bg, Visible = false };
            _detail = new Label { Dock = DockStyle.Fill, UseMnemonic = false, ForeColor = Theme.Subtle, Padding = new Padding(4, 10, 4, 0), BackColor = Theme.Surface };
            var infoCard = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, Radius = 12, Padding = new Padding(10) };
            infoCard.Controls.Add(_detail);
            infoCard.Controls.Add(_noThumb);
            infoCard.Controls.Add(_thumb);

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Padding = new Padding(14, 12, 14, 6) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            listCard.Margin = new Padding(0, 0, 6, 0); infoCard.Margin = new Padding(6, 0, 0, 0);
            grid.Controls.Add(listCard, 0, 0);
            grid.Controls.Add(infoCard, 1, 0);

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.Bg, Padding = new Padding(14, 8, 14, 12) };
            var btnResume = new RoundButton { Text = I18n.T("Reanudar"), Icon = "▶", Width = 150, Height = 40, Radius = 10, BaseColor = Theme.Accent, HoverColor = Theme.AccentHi, GradientTo = Theme.Accent2, TextColor = Color.White, FontSize = 11f, FontStyle = FontStyle.Bold, Dock = DockStyle.Right };
            _btnReplay = new RoundButton { Text = I18n.T("Repetir"), Icon = "⟳", Width = 130, Height = 40, Radius = 10, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 11f, Dock = DockStyle.Right };
            var spacer = new Panel { Width = 10, Dock = DockStyle.Right };
            var hint = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Subtle, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Font(8.75f),
                                   Text = I18n.T("«Reanudar» sigue la partida donde se guardó; «Repetir» la vuelve a reproducir desde el principio.") };
            btnResume.Click += (s, e) => Accept("-resume");
            _btnReplay.Click += (s, e) => Accept("-replay");
            buttons.Controls.Add(hint);
            buttons.Controls.Add(btnResume);
            buttons.Controls.Add(spacer);
            buttons.Controls.Add(_btnReplay);

            Controls.Add(grid);
            Controls.Add(buttons);
            Controls.Add(header);
            Controls.Add(stripe);

            LoadSaves();
            FormClosed += (s, e) => { var old = _thumb.Image; _thumb.Image = null; old?.Dispose(); };
        }

        // Carpeta donde el SIMULADOR guarda las partidas: %AppData%\Open Rails. (UserSettings.UserDataFolder
        // depende del programa que lo llama: dentro de SelectOR apuntaba a %AppData%\SelectOR, que está
        // vacía, y la lista salía siempre en blanco.)
        public static string OrDataFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails");

        /// <summary>Partidas guardadas de OR, de la más reciente a la más antigua.</summary>
        public static List<SaveItem> ReadSaves()
        {
            var items = new List<SaveItem>();
            try
            {
                var dir = OrDataFolder;
                if (!Directory.Exists(dir)) return items;
                foreach (var f in Directory.GetFiles(dir, "*.save"))
                {
                    var it = new SaveItem { File = f, RealTime = File.GetLastWriteTime(f), HasReplay = File.Exists(System.IO.Path.ChangeExtension(f, ".replay")) };
                    TryParse(it);
                    items.Add(it);
                }
            }
            catch { }
            return items.OrderByDescending(i => i.RealTime).ToList();
        }

        void LoadSaves()
        {
            _list.Items.Clear();
            foreach (var i in ReadSaves()) _list.Items.Add(i);
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            else
            {
                _detail.Text = I18n.T("No hay partidas guardadas todavía. Se crean al guardar dentro del simulador (tecla F2).");
                _thumb.Visible = false; _noThumb.Visible = true;
            }
        }

        // Cabecera del .save: versión, compilación, ruta ($Multipl$ + ruta en multijugador) y recorrido.
        static void TryParse(SaveItem it)
        {
            try
            {
                using var inf = new BinaryReader(File.Open(it.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                inf.ReadString();               // versión
                inf.ReadString();               // compilación
                var routeOrMp = inf.ReadString();
                if (routeOrMp == "$Multipl$") { it.Multiplayer = true; it.Route = inf.ReadString(); }
                else it.Route = routeOrMp;
                it.Path = inf.ReadString();
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
            string name = string.IsNullOrEmpty(it.Route) ? it.ToString() : it.Route + (it.Multiplayer ? "  " + I18n.T("(multijugador)") : "");
            var top = new Rectangle(e.Bounds.X + 14, e.Bounds.Y + 4, e.Bounds.Width - 24, e.Bounds.Height / 2 - 2);
            var bottom = new Rectangle(e.Bounds.X + 14, e.Bounds.Y + e.Bounds.Height / 2, e.Bounds.Width - 24, e.Bounds.Height / 2 - 4);
            using (var bold = new Font(Font, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, name, bold, top, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            string sub = (string.IsNullOrEmpty(it.Path) ? "" : it.Path + "   ·   ") + it.RealTime.ToString("g", Cul);
            TextRenderer.DrawText(e.Graphics, sub, Font, bottom, Theme.Subtle, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        void ShowDetail()
        {
            if (!(_list.SelectedItem is SaveItem it)) return;
            // Captura que OR guarda junto a la partida (mismo nombre, .png). Se copia a memoria para no
            // dejar el archivo bloqueado.
            var old = _thumb.Image; _thumb.Image = null; old?.Dispose();
            string png = System.IO.Path.ChangeExtension(it.File, ".png");
            try
            {
                if (File.Exists(png))
                    using (var fs = new FileStream(png, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var img = Image.FromStream(fs))
                        _thumb.Image = new Bitmap(img);
            }
            catch { }
            _thumb.Visible = _thumb.Image != null; _noThumb.Visible = _thumb.Image == null;
            _detail.Text = string.Format(I18n.T("Ruta: {0}\nRecorrido: {1}\nGuardada: {2}"),
                string.IsNullOrEmpty(it.Route) ? "—" : it.Route + (it.Multiplayer ? "  " + I18n.T("(multijugador)") : ""),
                string.IsNullOrEmpty(it.Path) ? "—" : it.Path,
                it.RealTime.ToString("f", Cul));
            _btnReplay.Enabled = it.HasReplay;
        }

        void Accept(string mode)
        {
            if (!(_list.SelectedItem is SaveItem it)) return;
            if (mode == "-replay" && !it.HasReplay) return;
            SelectedSaveFile = it.File;
            ModeFlag = mode;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
