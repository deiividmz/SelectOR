// HORARIO DE LA SALA · GESTORES (1.2.81): el administrador apunta por su ID de usuario a los maquinistas que pueden añadir,
// cambiar y quitar servicios de trenes IA (sql/horario-gestores-vueltas.sql: room_ai_editors). Como los jefes de
// circulación (DispatchForm.Jefe.cs): un menú con los apuntados (clic = quitar) y «Apuntar a un maquinista…».

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class HorarioSalaForm
    {
        async Task EditEditors()
        {
            if (!_admin) return;
            string title = Tr("Gestores del horario");
            var (json, err) = await Rpc("room_ai_editors_list", new { });
            if (IsDisposed) return;
            if (err != null && err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0) err = Tr("Falta ejecutar sql/horario-gestores-vueltas.sql en el servidor.");
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
                m.Items.Add(new ToolStripMenuItem(list.Count == 0 ? Tr("Ningún gestor del horario apuntado") : Tr("Gestores del horario (clic para quitar):")) { Enabled = false });
                foreach (var (id, name) in list)
                {
                    string uid = id, nn = string.IsNullOrEmpty(name) ? "?" : name;
                    m.Items.Add(new ToolStripMenuItem($"   {nn}   ·   ID {(uid != null && uid.Length > 8 ? uid.Substring(0, 8) : uid ?? "?")}", null, async (s, e) =>
                    {
                        if (ThemedBox.Show(this, string.Format(Tr("¿Quitar a {0} de gestor del horario?"), nn), title, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                        var (_, r) = await EditorSet(uid, false);
                        if (r != null) ThemedBox.Show(this, r, title);
                    }));
                }
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem(Tr("Apuntar a un maquinista…"), null, async (s, e) =>
            {
                using var d = new TextPromptDialog(title, Tr("ID de usuario del maquinista (lo copia con «ID ⧉» en su barra de Empresas; también está en Usuarios):"), "", "", Tr("Apuntar"), "🚆");
                if (d.ShowDialog(this) != DialogResult.OK || d.Value.Trim().Length == 0) return;
                string key = d.Value.Trim();
                if (!DispatchForm.LooksLikeId(key)) { ThemedBox.Show(this, Tr("Eso no es un ID de usuario: pega el ID (entero o sus primeros 8 caracteres), no el nombre."), title); return; }
                var (who, r) = await EditorSet(key, true);
                ThemedBox.Show(this, r ?? string.Format(Tr("{0} ya puede añadir, cambiar y quitar trenes IA en el horario de la sala (al volver a entrar en SelectOR)."), who ?? key), title);
            }));
            m.Show(Cursor.Position);
        }

        async Task<(string name, string err)> EditorSet(string id, bool on)
        {
            var (json, err) = await Rpc("room_ai_editor_set_id", new { p_user = id, p_on = on });
            if (err != null) return (null, err.Contains("PGRST202") ? Tr("Falta ejecutar sql/horario-gestores-vueltas.sql en el servidor.") : err);
            try { using var d = JsonDocument.Parse(json); return (d.RootElement.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null, null); }
            catch { return (null, null); }
        }
    }
}
