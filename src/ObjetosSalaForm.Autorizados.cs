// (1.2.91, petición 2026-11-07) OBJETOS DE LA SALA · AUTORIZADOS: el administrador apunta por su ID de usuario a quien puede
// poner y quitar objetos en la sala compartida (sql/objetos-sala-editores.sql: room_object_editors). Como los gestores del
// horario (HorarioSalaForm.Gestores.cs): un menú con los apuntados (clic = quitar) y «Autorizar a un usuario…».

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class ObjetosSalaForm
    {
        const string EditorsSql = "sql/objetos-sala-editores.sql";

        async Task EditEditors(Control under)
        {
            if (!Supa.IsSuperadmin) return;
            string title = Tr("Autorizados para los objetos");
            var (json, err) = await Rpc("room_object_editors_list", new { });
            if (IsDisposed) return;
            if (err != null && err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0) err = string.Format(Tr("Falta ejecutar {0} en el servidor."), EditorsSql);
            var list = new List<(string id, string name)>();
            if (err == null)
                try
                {
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
                    if (d.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var x in d.RootElement.EnumerateArray())
                            list.Add((x.TryGetProperty("user_id", out var u) ? u.GetString() : null, x.TryGetProperty("username", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null));
                }
                catch { }
            var m = MenuStyle.Apply(new ContextMenuStrip());
            if (err != null) m.Items.Add(new ToolStripMenuItem(err) { Enabled = false });
            else
            {
                m.Items.Add(new ToolStripMenuItem(list.Count == 0 ? Tr("Nadie más autorizado") : Tr("Pueden poner y quitar objetos (clic para quitar):")) { Enabled = false });
                foreach (var (id, name) in list)
                {
                    string uid = id, nn = string.IsNullOrEmpty(name) ? "?" : name;
                    m.Items.Add(new ToolStripMenuItem($"   {nn}   ·   ID {(uid != null && uid.Length > 8 ? uid.Substring(0, 8) : uid ?? "?")}", null, async (s, e) =>
                    {
                        if (ThemedBox.Show(this, string.Format(Tr("¿Quitar a {0} de los autorizados para los objetos?"), nn), title, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                        var (_, r) = await EditorSet(uid, false);
                        if (r != null) ThemedBox.Show(this, r, title);
                    }));
                }
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem(Tr("Autorizar a un usuario…"), null, async (s, e) =>
            {
                using var d = new TextPromptDialog(title, Tr("ID de usuario (lo copia con «ID ⧉» en su barra de Empresas; también está en Usuarios):"), "", "", Tr("Autorizar"), "🚧");
                if (d.ShowDialog(this) != DialogResult.OK || d.Value.Trim().Length == 0) return;
                string key = d.Value.Trim();
                if (!DispatchForm.LooksLikeId(key)) { ThemedBox.Show(this, Tr("Eso no es un ID de usuario: pega el ID (entero o sus primeros 8 caracteres), no el nombre."), title); return; }
                var (who, r) = await EditorSet(key, true);
                ThemedBox.Show(this, r ?? string.Format(Tr("{0} ya puede poner y quitar objetos en la sala compartida, desde el Puesto de Mando (al volver a entrar en SelectOR)."), who ?? key), title);
            }));
            m.Show(under, new System.Drawing.Point(0, under.Height));
        }

        async Task<(string name, string err)> EditorSet(string id, bool on)
        {
            var (json, err) = await Rpc("room_object_editor_set_id", new { p_user = id, p_on = on });
            if (err != null) return (null, err.Contains("PGRST202") ? string.Format(Tr("Falta ejecutar {0} en el servidor."), EditorsSql) : err);
            try { using var d = JsonDocument.Parse(json); return (d.RootElement.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null, null); }
            catch { return (null, null); }
        }
    }
}
