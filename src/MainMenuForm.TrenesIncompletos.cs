// Trenes INCOMPLETOS: los .con a los que les falta algún .eng o .wag (no se pueden conducir ni tasar bien). Se buscan en
// segundo plano (una vez por sesión; la caché se vacía con ClearContentCaches) y se agrupan en su propio filtro «⚠
// Incompletos» en Conducción libre y en Compra; en el resto de filtros no salen.
//
// Y las etiquetas de empresa de los TRENES (ejemplares de trenes-por-con.sql) en Conducción libre y Horarios: por su
// composición exacta (TrainKey), no por la máquina de cabeza (un tren simple no etiqueta los dobles).
//
// Y la SINCRONIZACIÓN: cuando cambian los .con (el Editor, «Añadir a mi contenido» o cualquier programa fuera de
// SelectOR) o el material (se instala o se quita una carpeta de TRAINSET), Conducción libre, Compra y Flota se ponen al
// día solas (OnConsistsChanged; WatchContent vigila las carpetas).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        // .con → lo que le falta (vacío: completo). Solo lo ya mirado.
        static readonly Dictionary<string, string[]> _missingCache = new(StringComparer.OrdinalIgnoreCase);

        // Lo que le falta a un tren (nombre del vehículo · carpeta); lo calcula si aún no se sabe.
        string[] MissingParts(TrainItem c)
        {
            if (c?.FilePath == null) return Array.Empty<string>();
            lock (_missingCache) if (_missingCache.TryGetValue(c.FilePath, out var hit)) return hit;
            var miss = new List<string>();
            foreach (var r in ConsistCarRefs(c.FilePath))
                if (ResolveCarFile(r.name, r.folder) == null)
                {
                    string m = string.IsNullOrEmpty(r.folder) ? r.name : r.folder + "\\" + r.name;
                    if (!miss.Contains(m, StringComparer.OrdinalIgnoreCase)) miss.Add(m);
                }
            var arr = miss.ToArray();
            lock (_missingCache) _missingCache[c.FilePath] = arr;
            return arr;
        }

        // null si aún no se ha mirado (no se para la interfaz a mirarlo).
        bool? KnownIncomplete(TrainItem c)
        {
            if (c?.FilePath == null) return null;
            lock (_missingCache) return _missingCache.TryGetValue(c.FilePath, out var m) ? m.Length > 0 : null;
        }

        string MissingText(TrainItem c)
        {
            string[] m; lock (_missingCache) if (c?.FilePath == null || !_missingCache.TryGetValue(c.FilePath, out m) || m.Length == 0) return null;
            return "⚠ " + Tr("Falta") + ": " + string.Join(", ", m.Take(2)) + (m.Length > 2 ? " (+" + (m.Length - 2) + ")" : "");
        }

        // Mira en segundo plano los trenes que aún no se han mirado y, al acabar, rehace las listas.
        bool _missingScanning;
        async void ScanMissingSoon()
        {
            if (_missingScanning) return;
            List<TrainItem> todo;
            lock (_missingCache) todo = _consistsAll.Where(c => c?.FilePath != null && !_missingCache.ContainsKey(c.FilePath)).ToList();
            if (todo.Count == 0) return;
            _missingScanning = true;
            try { await Task.Run(() => { foreach (var c in todo) { try { MissingParts(c); } catch { } } }); }
            finally { _missingScanning = false; }
            if (IsDisposed) return;
            try { RefreshConsistList(); } catch { }
            try { FilterShop(); } catch { }
        }

        // ============================ etiquetas de empresa de los trenes ============================
        // .con de este equipo → empresas (de socio) que tienen ese tren (composición exacta).
        Dictionary<string, List<string>> _companyTrainCons = new(StringComparer.OrdinalIgnoreCase);

        List<string> _tagCos = new(); Dictionary<string, string> _tagNames = new();
        async Task LoadCompanyTrainTagsAsync(IEnumerable<string> companyIds, Dictionary<string, string> nameOf)
        {
            _tagCos = companyIds.Distinct().ToList(); _tagNames = nameOf;   // para volver a etiquetar si cambian los .con
            var byKey = new Dictionary<string, List<string>>();
            var leads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cid in companyIds.Distinct())
            {
                var (json, err, _) = await TrainListJsonAsync(cid);   // la misma lista guardada que usan Flota y Compra
                if (err != null || !nameOf.TryGetValue(cid, out var cn)) continue;
                try
                {
                    using var d = System.Text.Json.JsonDocument.Parse(json);
                    foreach (var e in d.RootElement.EnumerateArray())
                    {
                        if (!e.TryGetProperty("vehicles", out var vs) || vs.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                        var list = vs.EnumerateArray().Select(TrainVeh.FromJson).ToList();
                        string lead = list.FirstOrDefault(v => !v.Wagon)?.Name;
                        if (string.IsNullOrEmpty(lead)) continue;
                        leads.Add(lead);
                        string key = TrainKey(list);
                        if (!byKey.TryGetValue(key, out var cos)) byKey[key] = cos = new List<string>();
                        if (!HasCo(cos, cn)) cos.Add(cn);
                    }
                }
                catch { }
            }
            var all = _consistsAll.Where(c => c?.FilePath != null).ToList();
            var result = await Task.Run(() =>
            {
                var r = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                if (byKey.Count == 0) return r;
                foreach (var c in all)
                {
                    string lead = LeadEngineName(c);
                    if (lead == null || !leads.Contains(lead)) continue;
                    if (byKey.TryGetValue(TrainKey(TrainVehiclesOfCached(c)), out var cos)) r[c.FilePath] = cos;
                }
                return r;
            });
            _companyTrainCons = result;
        }

        // ============================ los trenes del contenido, al día en todas partes ============================
        // Tras leer (o volver a leer) los .con: el escaparate de Compra, los trenes de la empresa en Flota (y sus plazas
        // fijadas), las etiquetas de empresa de Conducción libre y los incompletos.
        void OnConsistsChanged()
        {
            try { FilterShop(); } catch { }
            _ = MatchLocalTrainsAsync();
            if (_tagCos.Count > 0) _ = RetagTrainsAsync();
            ScanMissingSoon();
        }

        async Task RetagTrainsAsync()
        {
            await LoadCompanyTrainTagsAsync(_tagCos, _tagNames);
            RebuildCompanyEngs();
        }

        // Vigila TRAINS\CONSISTS (los .con) y TRAINS\TRAINSET (el material) de la carpeta de contenido elegida. Los cambios
        // se juntan (1,5 s sin más cambios) y se aplican de una vez.
        FileSystemWatcher _conWatch, _setWatch;
        System.Windows.Forms.Timer _watchTimer;
        readonly HashSet<string> _watchCons = new(StringComparer.OrdinalIgnoreCase);
        bool _watchMaterial;
        string _watchRoot;

        void WatchContent(string root)
        {
            if (string.IsNullOrEmpty(root) || string.Equals(root, _watchRoot, StringComparison.OrdinalIgnoreCase)) return;
            _watchRoot = root;
            try { _conWatch?.Dispose(); } catch { }
            try { _setWatch?.Dispose(); } catch { }
            _conWatch = _setWatch = null;
            string cons = Path.Combine(root, "TRAINS", "CONSISTS"), set = Path.Combine(root, "TRAINS", "TRAINSET");
            try
            {
                if (Directory.Exists(cons))
                {
                    var w = new FileSystemWatcher(cons, "*.con") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, IncludeSubdirectories = false };
                    w.Created += (s, e) => Watched(e.FullPath, true); w.Changed += (s, e) => Watched(e.FullPath, true); w.Deleted += (s, e) => Watched(e.FullPath, true);
                    w.Renamed += (s, e) => { Watched(e.OldFullPath, true); Watched(e.FullPath, true); };
                    w.EnableRaisingEvents = true; _conWatch = w;
                }
            }
            catch { }
            try
            {
                if (Directory.Exists(set))
                {
                    var w = new FileSystemWatcher(set) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, IncludeSubdirectories = true, InternalBufferSize = 65536 };
                    void M(string p) { string x = Path.GetExtension(p ?? ""); if (x.Length == 0 || x.Equals(".eng", StringComparison.OrdinalIgnoreCase) || x.Equals(".wag", StringComparison.OrdinalIgnoreCase)) Watched(null, false); }
                    w.Created += (s, e) => M(e.FullPath); w.Deleted += (s, e) => M(e.FullPath); w.Renamed += (s, e) => M(e.FullPath);
                    w.EnableRaisingEvents = true; _setWatch = w;
                }
            }
            catch { }
        }

        void Watched(string con, bool isCon)
        {
            lock (_watchCons) { if (isCon && con != null) _watchCons.Add(con); else _watchMaterial = true; }
            try
            {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke((Action)(() =>
                {
                    if (_watchTimer == null) { _watchTimer = new System.Windows.Forms.Timer { Interval = 1500 }; _watchTimer.Tick += (s, e) => ApplyWatched(); }
                    _watchTimer.Stop(); _watchTimer.Start();
                }));
            }
            catch { }
        }

        void ApplyWatched()
        {
            _watchTimer?.Stop();
            List<string> cons; bool material;
            lock (_watchCons) { cons = _watchCons.ToList(); _watchCons.Clear(); material = _watchMaterial; _watchMaterial = false; }
            if (_curFolder == null || !string.Equals(_curFolder.Path, _watchRoot, StringComparison.OrdinalIgnoreCase)) return;
            if (material) { ClearContentCaches(); _shopInfo.Clear(); }   // otro material: lo que falta y los datos de los vehículos, de nuevo
            if (cons.Count > 0 || material) ReloadConsistsAfterEdit(changed: cons);
        }
    }
}
