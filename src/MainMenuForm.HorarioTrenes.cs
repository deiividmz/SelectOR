// Horarios: el tren (la composición) de cada línea del horario lo puede elegir el usuario. Clic derecho en el panel de
// salidas → «Elegir el tren de esta línea…»: un selector con los trenes del contenido. La elección se guarda en las
// preferencias (AppPrefs.TtConsistOverride, por archivo de horario y tren) y se ve al momento en el panel, la vista
// 3D/2D, el teleindicador y la barra de abajo.
//  · El horario ORIGINAL no se toca. Al conducir, si algún tren del horario tiene otro tren elegido, se escribe una
//    copia junto al original («<nombre> [SelectOR].timetable_or», en la misma carpeta OpenRails, para que Open Rails
//    encuentre igual los pools y la ruta) con la fila #consist cambiada solo en esas columnas, y se lanza esa copia.
//    Si el conjunto es una lista (.timetablelist_or), también se escribe una lista que apunta a la copia (el resto de
//    horarios, los originales). Las copias no salen en las listas de SelectOR y se sobrescriben en cada conducción
//    (no se borran: una partida guardada puede apuntar a ellas).
//  · Si la copia no se puede escribir (carpeta sin permisos…), se avisa y se conduce el horario original.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using TimetableInfo = ORTS.Menu.TimetableInfo;
using TtTrain = Orts.Formats.OR.TimetableFileLite.TrainInformation;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        const string TtDerivedTag = " [SelectOR]";

        // Las copias que escribe SelectOR (no se enseñan como horarios propios).
        static bool IsTtDerived(string file) => !string.IsNullOrEmpty(file) && Path.GetFileName(file).Contains(TtDerivedTag, StringComparison.OrdinalIgnoreCase);

        // El archivo del horario elegido (dentro de su conjunto), como en FillBoard.
        string CurrentTtFile()
        {
            try
            {
                var set = _cboTTSet?.SelectedItem as TimetableInfo;
                if (set == null) return null;
                var files = TtStops.FilesOf(set.fileName);
                int fi = _cboTT.SelectedIndex;
                return fi >= 0 && fi < files.Count ? files[fi] : files.Count == 1 ? files[0] : null;
            }
            catch { return null; }
        }

        static string TtKey(string file, TtTrain tr) => (file ?? "") + "|" + (tr?.Train ?? "");

        // El .con elegido por el usuario para ese tren (nombre sin extensión), o null.
        string TtOverride(string file, TtTrain tr)
            => tr != null && file != null && _prefs.TtConsistOverride.TryGetValue(TtKey(file, tr), out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        static string TtOriginalConsist(TtTrain tr) => tr == null ? null : !string.IsNullOrWhiteSpace(tr.LeadingConsist) ? tr.LeadingConsist : tr.Consist;

        // Texto de la composición del tren como la entiende ResolveConsist (y Open Rails): la elegida o la del horario.
        string TtConsistText(TtTrain tr, string file = null)
        {
            var o = TtOverride(file ?? CurrentTtFile(), tr);
            return o != null ? TtConsistCell(o) : TtOriginalConsist(tr);
        }

        // Un nombre con «+» va entre < > (si no, Open Rails lo parte en dos composiciones).
        static string TtConsistCell(string name) => name.Contains('+') ? "<" + name + ">" : name;

        // ============================ menú contextual del panel de salidas ============================
        void WireTtBoardMenu()
        {
            if (_ttBoard == null) return;
            var ctx = MenuStyle.Apply(new ContextMenuStrip());
            var miPick = new ToolStripMenuItem(Tr("Elegir el tren de esta línea…")); miPick.Click += (s, e) => ChooseTtTrain();
            var miReset = new ToolStripMenuItem(Tr("Volver al tren del horario")); miReset.Click += (s, e) => ResetTtTrain();
            var miComp = new ToolStripMenuItem(Tr("Composición 2D")); miComp.Click += (s, e) => OpenTTComposition();
            ctx.Items.Add(miPick); ctx.Items.Add(miReset); ctx.Items.Add(new ToolStripSeparator()); ctx.Items.Add(miComp);
            ctx.Opening += (s, e) =>
            {
                // el botón derecho ya elige la fila (CardListBase): el tren del combo es el pulsado
                var tr = (_ttBoard.SelectedItem as DepartureBoard.Row)?.Train as TtTrain;
                if (tr == null) { e.Cancel = true; return; }
                string orig = TtOriginalConsist(tr);
                miReset.Visible = TtOverride(CurrentTtFile(), tr) != null;
                miReset.Text = string.Format(Tr("Volver al tren del horario ({0})"), string.IsNullOrWhiteSpace(orig) ? "—" : orig.Trim());
                miComp.Enabled = _ttConsist != null;
                MenuStyle.Measure(ctx);
            };
            _ttBoard.ContextMenuStrip = ctx;
        }

        void ChooseTtTrain()
        {
            var tr = (_ttBoard?.SelectedItem as DepartureBoard.Row)?.Train as TtTrain ?? _cboTTTrain?.SelectedItem as TtTrain;
            string file = CurrentTtFile();
            if (tr == null || file == null) return;
            if (_consistsAll.Count == 0) { ThemedBox.Show(this, Tr("No hay trenes en esta carpeta de contenido."), "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var current = ResolveConsist(TtConsistText(tr, file));
            var pick = PickTrainDialog(string.Format(Tr("Tren de la línea {0}"), tr.Train), current);
            if (pick == null) return;
            string name = ConsistNameForTimetable(pick);
            if (name == null) { ThemedBox.Show(this, Tr("Ese tren no está en TRAINS\\CONSISTS de la carpeta de contenido elegida."), "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            // el mismo tren que trae el horario: no hace falta guardar nada
            var orig = ResolveConsist(TtOriginalConsist(tr));
            if (orig != null && string.Equals(orig.FilePath, pick.FilePath, StringComparison.OrdinalIgnoreCase)) _prefs.TtConsistOverride.Remove(TtKey(file, tr));
            else _prefs.TtConsistOverride[TtKey(file, tr)] = name;
            _prefs.Save();
            AfterTtTrainChanged();
        }

        void ResetTtTrain()
        {
            var tr = (_ttBoard?.SelectedItem as DepartureBoard.Row)?.Train as TtTrain ?? _cboTTTrain?.SelectedItem as TtTrain;
            string file = CurrentTtFile();
            if (tr == null || file == null) return;
            if (_prefs.TtConsistOverride.Remove(TtKey(file, tr))) { _prefs.Save(); AfterTtTrainChanged(); }
        }

        // Panel, vista del tren, teleindicador, ficha y barra de abajo con el tren nuevo.
        void AfterTtTrainChanged()
        {
            FillBoard();
            UpdateTimetablePreview();
            UpdateTimetableBriefing();
            UpdateStatus();
        }

        // Nombre con el que el horario nombra el .con: su ruta dentro de TRAINS\CONSISTS, sin extensión.
        string ConsistNameForTimetable(TrainItem c)
        {
            try
            {
                string dir = Path.Combine(_curFolder?.Path ?? "", "TRAINS", "CONSISTS");
                if (c?.FilePath == null || !Directory.Exists(dir)) return null;
                string rel = Path.GetRelativePath(dir, c.FilePath);
                if (rel.StartsWith("..") || Path.IsPathRooted(rel)) return null;
                return Path.ChangeExtension(rel, null);
            }
            catch { return null; }
        }

        // ============================ selector de trenes ============================
        TrainItem PickTrainDialog(string title, TrainItem current)
        {
            using var f = new Form
            {
                Text = title, BackColor = Theme.Bg, ForeColor = Theme.Text, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, ShowInTaskbar = false, KeyPreview = true, Font = Theme.Font(9.5f),
            };
            try { f.Icon = Icon; } catch { }
            var wa = Screen.FromControl(this).WorkingArea;
            f.ClientSize = new Size(Math.Min(Theme.Px(1100), wa.Width - 80), Math.Min(Theme.Px(720), wa.Height - 80));
            f.MinimumSize = new Size(640, 420);
            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var head = new Label { Text = "  " + title, Dock = DockStyle.Top, Height = 42, Font = Theme.Font(13f, FontStyle.Bold), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Surface };
            var top = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Theme.Bg, Padding = new Padding(16, 10, 16, 6) };
            var search = new RoundedInput(Tr("Buscar tren…")) { Dock = DockStyle.Left, Width = Theme.Px(320), Height = 34 };
            var hint = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Subtle, TextAlign = ContentAlignment.MiddleRight, Text = Tr("Doble clic o «Elegir» para ponerlo en la línea del horario.") };
            top.Controls.Add(hint); top.Controls.Add(search);
            var grid = new TrainCardGrid
            {
                Dock = DockStyle.Fill, Thumbs = _thumbs ??= new VehicleThumbs(this),
                PathOf = o => (o as TrainItem)?.Locomotive?.FilePath, KeyOf = o => (o as TrainItem)?.FilePath,
                SpecOf = o => TrainSpecSeatsAsync(o as TrainItem),
                FavOf = o => o is TrainItem t && _prefs.FavoriteTrains.Contains(t.FilePath),
                CompaniesOf = o => ConsistCompanies(o as TrainItem),
                LblPax = Tr("VIAJEROS"), LblFreight = Tr("MERCANCÍAS"), LblCars = Tr("{0} coches"), LblSeats = Tr("plazas"), LblLoading = Tr("Calculando…"),
                ListMode = _prefs.TrainListView,
            };
            var gridHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(16, 4, 16, 4) };
            gridHost.Controls.Add(grid);
            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.Bg, Padding = new Padding(16, 10, 16, 12) };
            var ok = new RoundButton { Text = Tr("Elegir"), Width = 160, Height = 38, Radius = 10, BaseColor = Theme.Accent, HoverColor = Theme.AccentHi, GradientTo = Theme.Accent2, TextColor = Color.White, FontSize = 10.5f, FontStyle = FontStyle.Bold, Dock = DockStyle.Right };
            var cancel = new RoundButton { Text = Tr("Cancelar"), Width = 120, Height = 38, Radius = 10, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 10.5f, Dock = DockStyle.Right };
            var gap = new Panel { Width = 10, Dock = DockStyle.Right };
            buttons.Controls.Add(ok); buttons.Controls.Add(gap); buttons.Controls.Add(cancel);
            f.Controls.Add(gridHost); f.Controls.Add(buttons); f.Controls.Add(top); f.Controls.Add(head); f.Controls.Add(stripe);

            // favoritos primero; después por nombre
            var all = _consistsAll.Where(c => c != null)
                .OrderByDescending(c => _prefs.FavoriteTrains.Contains(c.FilePath))
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            TrainItem chosen = null;
            void Fill()
            {
                string q = search.Box.Text.Trim();
                var shown = q.Length == 0 ? all : all.Where(c => (c.Name + " " + Path.GetFileNameWithoutExtension(c.FilePath ?? "") + " " + (c.Locomotive?.Name ?? ""))
                                                                  .Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
                grid.BeginUpdate();
                grid.Items.Clear(); grid.Items.AddRange(shown.Cast<object>().ToArray());
                grid.EndUpdate();
                int i = current != null ? shown.FindIndex(c => string.Equals(c.FilePath, current.FilePath, StringComparison.OrdinalIgnoreCase)) : -1;
                if (i >= 0) grid.SelectedIndex = i; else if (shown.Count > 0 && q.Length > 0) grid.SelectedIndex = 0;
                ok.Enabled = grid.SelectedItem != null; ok.Invalidate();
            }
            void Accept() { if (grid.SelectedItem is TrainItem t) { chosen = t; f.DialogResult = DialogResult.OK; f.Close(); } }
            var wait = new Timer { Interval = 200 };
            wait.Tick += (s, e) => { wait.Stop(); Fill(); };
            search.Box.TextChanged += (s, e) => { wait.Stop(); wait.Start(); };
            search.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; wait.Stop(); Fill(); Accept(); } };
            grid.SelectedIndexChanged += (s, e) => { ok.Enabled = grid.SelectedItem != null; ok.Invalidate(); };
            grid.ItemActivated += o => Accept();
            ok.Click += (s, e) => Accept();
            cancel.Click += (s, e) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { f.DialogResult = DialogResult.Cancel; f.Close(); } };
            f.Shown += (s, e) => { Fill(); search.Box.Focus(); };
            f.FormClosed += (s, e) => wait.Dispose();
            _ttPickerOpen = f;
            try { return f.ShowDialog(this) == DialogResult.OK ? chosen : null; }
            finally { _ttPickerOpen = null; }
        }
        Form _ttPickerOpen;   // para las pruebas

        // ============================ al conducir: la copia del horario ============================
        // Devuelve el archivo del conjunto que hay que lanzar (el original si no hay ningún tren cambiado) y, si se ha
        // escrito una copia, el nombre del horario dentro de ella (ttName). Si falla, warn lleva el motivo.
        string TtLaunchSet(TimetableInfo set, int fileIndex, out string ttName, out string warn)
        {
            ttName = null; warn = null;
            try
            {
                var files = TtStops.FilesOf(set.fileName);
                var changed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // original → copia
                foreach (var file in files)
                {
                    var subs = _prefs.TtConsistOverride.Where(kv => kv.Key.StartsWith(file + "|", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
                                                       .ToDictionary(kv => kv.Key.Substring(file.Length + 1), kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                    if (subs.Count == 0) continue;
                    string copy = DerivedTtPath(file);
                    WriteTtWithConsists(file, copy, subs);
                    changed[file] = copy;
                }
                if (changed.Count == 0) return set.fileName;
                string launch;
                if (Path.GetExtension(set.fileName).Contains("list", StringComparison.OrdinalIgnoreCase))
                {
                    launch = DerivedTtPath(set.fileName);
                    WriteTtList(set.fileName, launch, changed);
                }
                else launch = changed.TryGetValue(set.fileName, out var c) ? c : set.fileName;
                // el nombre del horario, leído de la copia (si no tiene descripción, Open Rails usa el del archivo)
                var launchFiles = TtStops.FilesOf(launch);
                string ttFile = fileIndex >= 0 && fileIndex < launchFiles.Count ? launchFiles[fileIndex] : launchFiles.Count == 1 ? launchFiles[0] : null;
                if (ttFile != null) ttName = new Orts.Formats.OR.TimetableFileLite(ttFile).ToString();
                return launch;
            }
            catch (Exception e) { warn = e.Message; return set.fileName; }
        }

        static string DerivedTtPath(string file)
            => Path.Combine(Path.GetDirectoryName(file) ?? "", Path.GetFileNameWithoutExtension(file) + TtDerivedTag + Path.GetExtension(file));

        // Copia del horario con la fila #consist cambiada en las columnas de los trenes elegidos (misma codificación,
        // mismo separador y mismos saltos de línea que el original).
        static void WriteTtWithConsists(string src, string dst, Dictionary<string, string> byTrain)
        {
            var (text, enc) = ReadTextKeep(src);
            var table = TtStops.Load(src) ?? throw new InvalidDataException(I18n.T("No se ha podido leer el horario."));
            var lines = text.Split('\n');
            char sep = SepOf(lines[0]);
            int row = -1;
            for (int i = 1; i < lines.Length; i++)
            {
                string first = lines[i].TrimEnd('\r').Split(sep)[0].Trim().Trim('"').Trim();
                if (first.StartsWith("#consist", StringComparison.OrdinalIgnoreCase)) { row = i; break; }
            }
            if (row < 0) throw new InvalidDataException(I18n.T("El horario no tiene la fila #consist."));
            bool cr = lines[row].EndsWith("\r");
            var cells = lines[row].TrimEnd('\r').Split(sep);
            foreach (var kv in byTrain)
            {
                int col = TtStops.ColumnOf(table, kv.Key, -1);
                if (col < 1) continue;   // ese tren ya no está en el horario
                if (col >= cells.Length) Array.Resize(ref cells, col + 1);
                cells[col] = TtConsistCell(kv.Value.Trim());
            }
            lines[row] = string.Join(sep, cells.Select(x => x ?? "")) + (cr ? "\r" : "");
            WriteTextKeep(dst, string.Join("\n", lines), enc);
        }

        // Lista de horarios que apunta a las copias (los demás, los originales), relativa a su carpeta como la original.
        static void WriteTtList(string src, string dst, Dictionary<string, string> changed)
        {
            var (text, enc) = ReadTextKeep(src);
            string dir = Path.GetDirectoryName(src) ?? "";
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                bool cr = lines[i].EndsWith("\r");
                string l = lines[i].TrimEnd('\r').Trim();
                if (l.Length == 0 || l.StartsWith("#")) continue;
                string raw = l.Trim('"');
                string full = Path.IsPathRooted(raw) ? raw : Path.Combine(dir, raw);
                if (!changed.TryGetValue(Path.GetFullPath(full), out var copy) && !changed.TryGetValue(full, out copy)) continue;
                string rel = Path.IsPathRooted(raw) ? copy : Path.Combine(Path.GetDirectoryName(raw) ?? "", Path.GetFileName(copy));
                lines[i] = rel + (cr ? "\r" : "");
            }
            WriteTextKeep(dst, string.Join("\n", lines), enc);
        }

        // Separador de un horario de Open Rails: el primer carácter si es uno de los suyos; si no, el más repetido.
        static char SepOf(string head)
        {
            if (!string.IsNullOrEmpty(head) && (head[0] == ';' || head[0] == ',' || head[0] == '\t')) return head[0];
            return new[] { ';', ',', '\t' }.OrderByDescending(ch => (head ?? "").Count(x => x == ch)).First();
        }

        // Texto con su codificación: con BOM, la suya; sin BOM, UTF-8 si lo es y si no Windows-1252 (Latin-1).
        static (string text, Encoding enc) ReadTextKeep(string path)
        {
            var b = File.ReadAllBytes(path);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (Encoding.Unicode.GetString(b, 2, b.Length - 2), new UnicodeEncoding(false, true));
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2), new UnicodeEncoding(true, true));
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (Encoding.UTF8.GetString(b, 3, b.Length - 3), new UTF8Encoding(true));
            try { return (new UTF8Encoding(false, true).GetString(b), new UTF8Encoding(false)); }
            catch (DecoderFallbackException) { return (Encoding.Latin1.GetString(b), Encoding.Latin1); }
        }

        // Escritura atómica (.tmp y luego mover), con el BOM de la codificación si lo lleva.
        static void WriteTextKeep(string path, string text, Encoding enc)
        {
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                var pre = enc.GetPreamble();
                if (pre.Length > 0) fs.Write(pre, 0, pre.Length);
                var data = enc.GetBytes(text);
                fs.Write(data, 0, data.Length);
            }
            File.Move(tmp, path, true);
        }
    }
}
