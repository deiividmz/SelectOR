// Documentos de la ruta (1.2.68): PDF para descargar (horarios, planos…) en la página Ruta, debajo de la ficha.
// La lista está en la web (descargas/rutas/documentos.json: por carpeta de ruta, título, archivo, portada, páginas,
// tamaño, autor y huella SHA-256), así que se añaden más sin sacar versión. Se bajan la primera vez que se abren a
// %AppData%\Open Rails\SelectOR\documentos\<ruta>\ (si la huella no cuadra, no se abren) y se abren con el lector de
// PDF del equipo; «Guardar en Descargas» deja una copia en la carpeta Descargas.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        static string DocsBase => Environment.GetEnvironmentVariable("SELECTOR_DOCS_BASE") ?? "https://www.select-or.app/descargas/rutas/";   // (otra solo en pruebas)

        sealed class RouteDoc
        {
            public string Title { get; set; }
            public string TitleEn { get; set; }
            public string File { get; set; }
            public string Cover { get; set; }
            public string Author { get; set; }
            public int Pages { get; set; }
            public long Bytes { get; set; }
            public string Sha256 { get; set; }
        }

        static readonly HttpClient DocsHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        Panel _rutaDocs;
        FlowLayoutPanel _rutaDocsFlow;
        Dictionary<string, List<RouteDoc>> _docsAll;   // null hasta que llega la lista
        Task _docsLoad;
        string _docsShownFor;

        static string DocsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails", "SelectOR", "documentos");

        // (BuildRutaPage) la franja, oculta hasta que la ruta elegida tenga documentos
        Panel BuildRutaDocs()
        {
            _rutaDocs = new Panel { Dock = DockStyle.Top, Height = 132, BackColor = Theme.Bg, Padding = new Padding(0, 12, 0, 0), Visible = false };
            var box = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(14, 8, 14, 10) };
            var head = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Subtle, BackColor = Theme.Surface, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Text = Tr("DOCUMENTOS DE LA RUTA") };
            _rutaDocsFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, WrapContents = false, AutoScroll = false, Margin = new Padding(0) };
            _rutaDocsFlow.Resize += (s, e) => FitDocCards();
            box.Controls.Add(_rutaDocsFlow); box.Controls.Add(head);
            _rutaDocs.Controls.Add(box);
            return _rutaDocs;
        }

        // (UpdateRouteOverview) la de la ruta elegida
        async void RefreshRutaDocs()
        {
            try
            {
                if (_rutaDocs == null) return;
                string route = _curRoute?.Path == null ? null : Path.GetFileName(_curRoute.Path.TrimEnd('\\', '/'));
                if (route == null) { ShowDocs(null, null); return; }
                if (_docsAll == null) { _docsLoad ??= LoadDocsList(); await _docsLoad; }
                string now = _curRoute?.Path == null ? null : Path.GetFileName(_curRoute.Path.TrimEnd('\\', '/'));
                if (now != route) return;   // ya han elegido otra
                List<RouteDoc> list = null;
                _docsAll?.TryGetValue(route, out list);
                ShowDocs(route, list);
            }
            catch { }
        }

        async Task LoadDocsList()
        {
            try
            {
                string json = await DocsHttp.GetStringAsync(DocsBase + "documentos.json");
                _docsAll = JsonSerializer.Deserialize<Dictionary<string, List<RouteDoc>>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                           ?? new Dictionary<string, List<RouteDoc>>();
                _docsAll = new Dictionary<string, List<RouteDoc>>(_docsAll, StringComparer.OrdinalIgnoreCase);
            }
            catch { _docsAll = new Dictionary<string, List<RouteDoc>>(StringComparer.OrdinalIgnoreCase); _docsLoad = null; }   // (sin red: se vuelve a probar al cambiar de ruta)
        }

        void ShowDocs(string route, List<RouteDoc> list)
        {
            list = list?.Where(d => !string.IsNullOrWhiteSpace(d?.File) && SafeName(d.File)).ToList();
            bool any = list != null && list.Count > 0;
            if (_rutaDocs.Visible != any) { _rutaDocs.Visible = any; }
            if (!any) { _docsShownFor = null; return; }
            if (_docsShownFor == route) return;
            _docsShownFor = route;
            _rutaDocsFlow.SuspendLayout();
            foreach (Control c in _rutaDocsFlow.Controls.Cast<Control>().ToList()) c.Dispose();
            foreach (var d in list) _rutaDocsFlow.Controls.Add(DocCard(route, d));
            FitDocCards();
            _rutaDocsFlow.ResumeLayout();
        }

        // El ancho de las tarjetas, repartido entre las que hay (entre 300 y 400 px): que quepan todas en la fila.
        void FitDocCards()
        {
            var cards = _rutaDocsFlow?.Controls.Cast<Control>().ToList();
            if (cards == null || cards.Count == 0) return;
            int w = Math.Max(300, Math.Min(400, (_rutaDocsFlow.ClientSize.Width - 12 * (cards.Count - 1)) / cards.Count));
            foreach (var c in cards)
            {
                if (c.Width == w) continue;
                c.Width = w;
                foreach (Control k in c.Controls)
                    if (k is Label) k.Width = w - 78;
                    else if (k is RoundButton rb && rb.Left > 100) rb.Width = w - 176;
            }
        }

        // (solo «carpeta/archivo» sin subir de carpeta)
        static bool SafeName(string rel) => !rel.Contains("..") && !rel.StartsWith("/") && !rel.Contains(':') && rel.IndexOfAny(Path.GetInvalidPathChars()) < 0;

        Control DocCard(string route, RouteDoc d)
        {
            var card = new Panel { Width = 400, Height = 84, Margin = new Padding(0, 0, 12, 0), BackColor = Theme.Surface2 };
            var cover = new PictureBox { Left = 8, Top = 6, Width = 52, Height = 72, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.SurfaceHi };
            string title = I18n.English && !string.IsNullOrWhiteSpace(d.TitleEn) ? d.TitleEn : d.Title ?? Path.GetFileNameWithoutExtension(d.File);
            var lt = new Label { Left = 70, Top = 6, Width = 322, Height = 20, ForeColor = Theme.Text, BackColor = Theme.Surface2, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Text = title, AutoEllipsis = true };
            string sub = "PDF" + (d.Pages > 0 ? " · " + Plural(d.Pages, "{0} página", "{0} páginas") : "") + (d.Bytes > 0 ? " · " + (d.Bytes / 1048576.0).ToString("0.0") + " MB" : "")
                         + (string.IsNullOrWhiteSpace(d.Author) ? "" : " · " + string.Format(Tr("por {0}"), d.Author));
            var ls = new Label { Left = 70, Top = 26, Width = 322, Height = 18, ForeColor = Theme.Subtle, BackColor = Theme.Surface2, Font = new Font("Segoe UI", 8.5f), Text = sub, AutoEllipsis = true };
            var bOpen = new RoundButton { Left = 70, Top = 48, Width = 92, Height = 28, Radius = 7, Text = Tr("Abrir"), BaseColor = Theme.Accent2, HoverColor = Theme.Accent, TextColor = Color.White, FontSize = 9f };
            var bSave = new RoundButton { Left = 168, Top = 48, Width = 164, Height = 28, Radius = 7, Text = Tr("Guardar en Descargas"), BaseColor = Theme.SurfaceHi, HoverColor = Theme.Border, TextColor = Theme.Text, FontSize = 9f };
            bOpen.Click += async (s, e) => await DocAction(route, d, bOpen, false);
            bSave.Click += async (s, e) => await DocAction(route, d, bSave, true);
            card.Controls.AddRange(new Control[] { cover, lt, ls, bOpen, bSave });
            _ = LoadDocCover(route, d, cover);
            return card;
        }

        async Task LoadDocCover(string route, RouteDoc d, PictureBox pb)
        {
            if (string.IsNullOrWhiteSpace(d.Cover) || !SafeName(d.Cover)) return;
            try
            {
                string local = Path.Combine(DocsDir, route, Path.GetFileName(d.Cover));
                if (!File.Exists(local))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(local));
                    var bytes = await DocsHttp.GetByteArrayAsync(DocsBase + d.Cover);
                    if (bytes.Length > 2 << 20) return;
                    File.WriteAllBytes(local, bytes);
                }
                using var fs = File.OpenRead(local);
                var img = Image.FromStream(fs);
                if (!pb.IsDisposed) pb.Image = new Bitmap(img);
            }
            catch { }
        }

        bool _docBusy;

        async Task DocAction(string route, RouteDoc d, RoundButton b, bool save)
        {
            if (_docBusy) return;
            _docBusy = true;
            string label = b.Text;
            try
            {
                string local = await DocLocal(route, d, p => { try { b.Text = p < 0 ? Tr("Descargando…") : string.Format(Tr("Descargando… {0} %"), p); b.Invalidate(); } catch { } });
                if (local == null) { ThemedBox.Show(Tr("No se ha podido descargar el documento. Comprueba la conexión y vuelve a probar."), "SelectOR"); return; }
                if (!save) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(local) { UseShellExecute = true }); return; }
                string dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(dl);
                string dst = Path.Combine(dl, Path.GetFileName(local));
                File.Copy(local, dst, true);
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + dst + "\"");
            }
            catch (Exception ex) { ThemedBox.Show(Tr("No se ha podido abrir el documento.") + "\n\n" + ex.Message, "SelectOR"); }
            finally { _docBusy = false; try { b.Text = label; b.Invalidate(); } catch { } }
        }

        // El PDF en la carpeta de documentos (lo baja si no está o no cuadra la huella). null = no se ha podido.
        static async Task<string> DocLocal(string route, RouteDoc d, Action<int> progress)
        {
            string local = Path.Combine(DocsDir, route, Path.GetFileName(d.File));
            if (File.Exists(local) && Matches(local, d)) return local;
            Directory.CreateDirectory(Path.GetDirectoryName(local));
            string tmp = local + ".descarga";
            try
            {
                using (var resp = await DocsHttp.GetAsync(DocsBase + d.File, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? d.Bytes;
                    if (total > 200L << 20) return null;
                    using var src = await resp.Content.ReadAsStreamAsync();
                    using var dst = File.Create(tmp);
                    var buf = new byte[81920]; long got = 0; int n, last = -2;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n); got += n;
                        if (got > 200L << 20) return null;
                        int p = total > 0 ? (int)(100 * got / total) : -1;
                        if (p != last) { last = p; progress?.Invoke(p); }
                    }
                }
                if (!Matches(tmp, d)) { File.Delete(tmp); return null; }
                if (File.Exists(local)) File.Delete(local);
                File.Move(tmp, local);
                return local;
            }
            catch { try { File.Delete(tmp); } catch { } return null; }
        }

        static bool Matches(string file, RouteDoc d)
        {
            if (string.IsNullOrWhiteSpace(d.Sha256)) return new FileInfo(file).Length > 0;
            using var fs = File.OpenRead(file);
            return Convert.ToHexString(SHA256.HashData(fs)).Equals(d.Sha256, StringComparison.OrdinalIgnoreCase);
        }
    }
}
