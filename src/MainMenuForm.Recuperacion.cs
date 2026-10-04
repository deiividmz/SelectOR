// Clave de recuperación de cada usuario (servidor: clave-recuperacion.sql).
//  · Mi cuenta → «Clave de recuperación»: la ve el usuario (y la copia).
//  · Inicio de sesión → «¿Has olvidado la contraseña?»: usuario + clave + contraseña nueva. La clave usada
//    deja de valer y se genera otra (se ve después en Mi cuenta).
//  · Usuarios (superadmin): la clave de cada uno, para dársela a quien la pida; «Nueva clave» la cambia.

using System;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        // Mi clave de recuperación.
        async void ShowMyRecoveryKey()
        {
            var (json, err) = await Supa.RpcAsync("my_recovery_key", new { });
            if (err != null)
            {
                FancyMsg(err.Contains("PGRST202") ? Tr("El servidor aún no tiene las claves de recuperación (falta clave-recuperacion.sql).") : Tr("Error: ") + err);
                return;
            }
            string key = "";
            key = JsonText(json);
            using var dlg = new FormDialog(Tr("Clave de recuperación"), Tr("Copiar y cerrar"), 520);
            dlg.AddInfo(Tr("Con esta clave puedes poner una contraseña nueva si olvidas la tuya («¿Has olvidado la contraseña?», en la pantalla de inicio de sesión). Guárdala en un sitio seguro y no se la des a nadie. Cada clave vale una sola vez: al usarla se genera otra."));
            var lbl = dlg.AddInfo(key, Theme.AccentHi);
            lbl.Font = new Font("Consolas", 18f, FontStyle.Bold);
            lbl.TextAlign = ContentAlignment.MiddleCenter;
            lbl.BackColor = Theme.Surface;
            dlg.Validate = f => { try { Clipboard.SetText(key); } catch { } return System.Threading.Tasks.Task.FromResult<string>(null); };
            dlg.ShowDialog(this);
        }

        // Texto que devuelve una función del servidor («"ABC"»); tolera que llegue sin comillas.
        static string JsonText(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "";
            try { return JsonSerializer.Deserialize<string>(json) ?? ""; } catch { return json.Trim().Trim('"'); }
        }

        void FancyMsg(string text) => MessageBox.Show(this, text, "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Information);

        // ¿Nombre de empresa libre? (sin distinguir mayúsculas, acentos ni espacios). Avisa si no lo está.
        // Un servidor sin nombres-unicos.sql no lo sabe: se deja pasar.
        async System.Threading.Tasks.Task<bool> CompanyNameFree(string name, string exceptId)
        {
            var (json, err) = await Supa.RpcAsync("company_name_available", new { p_name = name, p_except = exceptId });
            if (err != null || json == null) return true;
            if (json.Trim() == "false") { Msg(_empHomeMsg, Tr("Ya existe una empresa con ese nombre: elige otro."), true); return false; }
            return true;
        }

        // ¿Has olvidado la contraseña? (sin haber iniciado sesión)
        void ForgotPassword()
        {
            using var dlg = new FormDialog(Tr("Restablecer la contraseña"), Tr("Cambiar contraseña"), 520);
            dlg.AddInfo(Tr("Escribe tu nombre de usuario, tu clave de recuperación (la ves en Empresas → Mi cuenta, o te la puede dar el administrador) y la contraseña nueva."));
            dlg.AddText("user", Tr("Nombre de usuario"), _empEmail?.Box.Text?.Trim() ?? "");
            var key = dlg.AddText("key", Tr("Clave de recuperación"), "", "XXXX-XXXX-XXXX-XXXX");
            key.Box.CharacterCasing = CharacterCasing.Upper;
            dlg.AddText("p1", Tr("Contraseña nueva (6 caracteres como mínimo)"), "", "", password: true);
            dlg.AddText("p2", Tr("Repite la contraseña nueva"), "", "", password: true);
            string user = "", pass = "";
            dlg.Validate = async f =>
            {
                user = f.Get("user"); pass = f.Get("p1");
                if (user.Length == 0) return Tr("Escribe tu nombre de usuario.");
                if (f.Get("key").Length < 8) return Tr("Escribe tu clave de recuperación.");
                if (pass.Length < 6) return Tr("La contraseña nueva debe tener al menos 6 caracteres.");
                if (pass != f.Get("p2")) return Tr("Las dos contraseñas no coinciden.");
                var (json, err) = await Supa.RpcAsync("reset_password_with_key", new { p_login = user, p_key = f.Get("key"), p_new_password = pass });
                if (err != null)
                    return err.Contains("PGRST202") ? Tr("El servidor aún no permite restablecer la contraseña (falta clave-recuperacion.sql).") : Tr("Error: ") + err;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    if (d.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                        return Tr(d.RootElement.TryGetProperty("error", out var er) ? er.GetString() ?? "" : "No se pudo cambiar la contraseña.");
                }
                catch { }
                return null;
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (_empEmail != null) _empEmail.Box.Text = user;
            if (_empPass != null) _empPass.Box.Text = pass;
            Msg(_empAuthMsg, Tr("Contraseña cambiada. Ya puedes iniciar sesión (tu clave de recuperación ha cambiado: la nueva está en Mi cuenta)."), false);
        }

        // Usuarios (superadmin): genera otra clave para el usuario elegido.
        async void AdminNewRecoveryKey()
        {
            if (!Supa.IsSuperadmin || _usersList == null) return;
            int i = SelectedUserIndex();
            if (i < 0 || i >= _userIds.Count) { Msg(_usersMsg, Tr("Selecciona un usuario de la lista."), true); return; }
            if (MessageBox.Show(this, Tr("¿Generar otra clave de recuperación para este usuario? La anterior dejará de valer."), "SelectOR",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var (json, err) = await Supa.RpcAsync("admin_new_recovery_key", new { p_user = _userIds[i] });
            if (err != null) { Msg(_usersMsg, Tr("Error: ") + err, true); return; }
            string key = JsonText(json);
            try { if (key.Length > 0) Clipboard.SetText(key); } catch { }
            Msg(_usersMsg, string.Format(Tr("Clave nueva: {0} (copiada)."), key), false);
            LoadUsers();
        }

        void AdminCopyRecoveryKey()
        {
            int i = SelectedUserIndex();
            if (i < 0 || i >= _userKeys.Count) { Msg(_usersMsg, Tr("Selecciona un usuario de la lista."), true); return; }
            string key = _userKeys[i];
            if (string.IsNullOrEmpty(key)) { Msg(_usersMsg, Tr("Ese usuario aún no tiene clave."), true); return; }
            try { Clipboard.SetText(key); Msg(_usersMsg, string.Format(Tr("Clave copiada: {0}"), key), false); } catch { }
        }
    }
}
