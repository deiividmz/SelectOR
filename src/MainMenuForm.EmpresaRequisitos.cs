// Empresas → Normas: requisitos de los servicios de la empresa y su Discord (2026-11-09; sql/empresa-requisitos.sql).
//  · El gerente y los gestores ponen km mínimos y minutos mínimos por servicio (más estrictos que los de SelectOR) y el
//    enlace de invitación a su Discord; los socios lo ven arriba de las normas, con un botón para unirse.
//  · Al empezar un servicio de la empresa, un recorrido más corto que el mínimo no se deja empezar (CompanyReqBlockAsync).
//  · Al registrarlo, el servidor mira los km y el tiempo (service_rules_check, sql/empresa-requisitos-anular.sql): si no
//    llega, lo ANULA con el motivo «No cumple la norma exigida por la empresa…» (sale en el informe; el maquinista puede
//    alegar) y a los gestores les llega el aviso.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        Label _reqText;
        RoundButton _reqDiscordBtn, _reqEditBtn;
        double _reqMinKm; int _reqMinMin; string _reqDiscord; bool _reqCanEdit; string _reqFor;

        Control BuildReqStrip()
        {
            var card = new Card { Fill = Theme.Surface, BorderColor = Theme.Border, Radius = 10, Padding = new Padding(14, 10, 10, 8), Dock = DockStyle.Top, Height = 92, Margin = new Padding(2, 2, 2, 8) };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Surface };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _reqText = new Label { AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Surface, Font = Theme.Font(9.5f), MaximumSize = new Size(900, 0), Text = Tr("Cargando…") };
            t.Controls.Add(_reqText);
            var btns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Surface, Margin = new Padding(0, 4, 0, 0) };
            RoundButton B(string text, Action a, bool primary = false)
            {
                var b = EmpButton(Tr(text), primary); b.Width = 210; b.Margin = new Padding(0, 2, 8, 0); b.Click += (s, e) => a(); btns.Controls.Add(b); return b;
            }
            _reqDiscordBtn = B("Unirse al Discord", OpenCompanyDiscord, true);
            _reqEditBtn = B("Requisitos y Discord…", () => _ = EditCompanyReq());
            t.Controls.Add(btns);
            card.Controls.Add(t);
            card.Resize += (s, e) => { try { _reqText.MaximumSize = new Size(Math.Max(200, card.Width - 40), 0); card.Height = Math.Max(70, t.PreferredSize.Height + 22); } catch { } };
            UpdateReqStrip();
            return card;
        }

        string ReqSummary(double km, int min)
        {
            var parts = new List<string>();
            if (km > 0) parts.Add(string.Format(Tr("al menos {0} km"), km.ToString("0.#", I18n.Culture)));
            if (min > 0) parts.Add(string.Format(Tr("al menos {0} min"), min));
            return parts.Count == 0 ? Tr("sin mínimos propios (valen los de SelectOR)") : string.Join(" · ", parts);
        }

        void UpdateReqStrip()
        {
            if (_reqText == null) return;
            _reqText.Text = Tr("Requisitos de los servicios de la empresa") + ": " + ReqSummary(_reqMinKm, _reqMinMin)
                          + (_reqDiscord != null ? "\n" + Tr("La empresa tiene Discord: únete para hablar con los demás socios.") : "");
            _reqDiscordBtn.Visible = _reqDiscord != null;
            _reqEditBtn.Visible = _reqCanEdit;
            try { _reqText.Parent?.Parent?.PerformLayout(); } catch { }
        }

        async void LoadCompanyReq()
        {
            var c = _empSel; if (c == null || _reqText == null) return;
            var (json, err) = await Supa.RpcAsync("company_rules_get", new { p_company = c.Id });
            if (_empSel?.Id != c.Id) return;
            _reqFor = c.Id; _reqMinKm = 0; _reqMinMin = 0; _reqDiscord = null; _reqCanEdit = false;
            if (err != null)
            {
                _reqText.Text = MissingRpc(err) ? Tr("El servidor aún no tiene los requisitos de las empresas (falta empresa-requisitos.sql).") : Tr("Error: ") + CleanRpcErr(err);
                _reqDiscordBtn.Visible = _reqEditBtn.Visible = false;
                return;
            }
            try
            {
                using var d = JsonDocument.Parse(json); var r = d.RootElement;
                _reqMinKm = Num(r, "min_km"); _reqMinMin = (int)Num(r, "min_minutes");
                string u = Str(r, "discord"); _reqDiscord = DiscordOk(u) ? u : null;
                _reqCanEdit = r.TryGetProperty("can_edit", out var ce) && ce.ValueKind == JsonValueKind.True;
            }
            catch { }
            UpdateReqStrip();
        }

        static bool DiscordOk(string u) => !string.IsNullOrWhiteSpace(u)
            && System.Text.RegularExpressions.Regex.IsMatch(u.Trim(), @"^https://(www\.)?(discord\.gg|discord\.com/invite)/[A-Za-z0-9-]{2,40}/?$");

        void OpenCompanyDiscord()
        {
            if (!DiscordOk(_reqDiscord)) return;
            try { Process.Start(new ProcessStartInfo(_reqDiscord.Trim()) { UseShellExecute = true }); }
            catch { Msg(_rulesMsg, Tr("No se ha podido abrir el enlace."), true); }
        }

        async Task EditCompanyReq()
        {
            var c = _empSel; if (c == null || !_reqCanEdit) return;
            using var dlg = new FormDialog(Tr("Requisitos y Discord"), Tr("Guardar"), 560, "📋", c.Name);
            dlg.AddText("km", Tr("Km mínimos por servicio (0 = sin mínimo)"), _reqMinKm.ToString("0.#", I18n.Culture));
            dlg.AddText("min", Tr("Minutos mínimos por servicio (0 = sin mínimo)"), _reqMinMin.ToString(I18n.Culture));
            dlg.AddText("discord", Tr("Invitación a vuestro Discord (opcional)"), _reqDiscord ?? "", "https://discord.gg/…");
            dlg.AddInfo(Tr("Un recorrido más corto que el mínimo no se puede empezar. Si un servicio no llega a los km o al tiempo, se anula solo con el motivo «No cumple la norma exigida por la empresa» y os llega un aviso."));
            dlg.Validate = async f =>
            {
                string ks = f.Get("km").Trim().Replace(',', '.'), ms = f.Get("min").Trim(), url = f.Get("discord").Trim();
                if (!double.TryParse(ks, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double km) || km < 0 || km > 2000) return Tr("Los km mínimos van de 0 a 2000.");
                if (!int.TryParse(ms, out int mn) || mn < 0 || mn > 1440) return Tr("Los minutos mínimos van de 0 a 1440.");
                if (url.Length > 0 && !DiscordOk(url)) return Tr("El enlace debe ser una invitación de Discord (https://discord.gg/… o https://discord.com/invite/…).");
                var (_, e) = await Supa.RpcAsync("company_rules_set", new { p_company = c.Id, p_min_km = Math.Round(km, 1), p_min_minutes = mn, p_discord = url.Length > 0 ? url : null });
                if (e != null) return MissingRpc(e) ? Tr("El servidor aún no tiene los requisitos de las empresas (falta empresa-requisitos.sql).") : Tr("Error: ") + CleanRpcErr(e);
                return null;
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            Msg(_rulesMsg, Tr("Requisitos guardados."), false);
            LoadCompanyReq();
        }

        // (al empezar un servicio) El motivo por el que el recorrido no vale para la empresa, o null.
        async Task<string> CompanyReqBlockAsync(string companyId, string patFile)
        {
            try
            {
                if (string.IsNullOrEmpty(companyId) || string.IsNullOrEmpty(patFile) || !System.IO.File.Exists(patFile)) return null;
                var (json, err) = await Supa.RpcAsync("company_rules_get", new { p_company = companyId });
                if (err != null) return null;   // (sin el SQL o sin red: decide el servidor como siempre)
                double minKm;
                using (var d = JsonDocument.Parse(json)) minKm = Num(d.RootElement, "min_km");
                if (minKm <= 0) return null;
                double km = await Task.Run(() => EstimatePathKm(patFile));
                if (km <= 0 || km >= minKm) return null;
                return string.Format(Tr("La empresa exige servicios de al menos {0} km y este recorrido tiene unos {1} km. Elige un recorrido más largo."),
                                     minKm.ToString("0.#", I18n.Culture), km.ToString("0.#", I18n.Culture));
            }
            catch { return null; }
        }

        // (al registrar) ¿Cumple los requisitos de la empresa? El texto para el informe, o null.
        async Task<(bool annulled, string text)> CompanyReqCheckAsync(string serviceId)
        {
            try
            {
                var (json, err) = await Supa.RpcAsync("service_rules_check", new { p_service = serviceId });
                if (err != null || string.IsNullOrWhiteSpace(json)) return (false, null);
                using var d = JsonDocument.Parse(json); var r = d.RootElement;
                if (!r.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False) return (false, null);
                double km = Num(r, "km"), mk = Num(r, "min_km"), mi = Num(r, "minutes"), mm = Num(r, "min_minutes");
                var why = new List<string>();
                if (mk > 0 && km < mk) why.Add(string.Format(Tr("{0} km de los {1} que pide"), km.ToString("0.#", I18n.Culture), mk.ToString("0.#", I18n.Culture)));
                if (mm > 0 && mi < mm) why.Add(string.Format(Tr("{0} min de los {1} que pide"), mi.ToString("0", I18n.Culture), mm.ToString("0", I18n.Culture)));
                bool annulled = r.TryGetProperty("annulled", out var an) && an.ValueKind == JsonValueKind.True;
                return annulled ? (true, Tr("Servicio anulado: no cumple la norma exigida por la empresa") + " (" + string.Join(", ", why) + "). " + Tr("Su dinero se ha quitado de la tesorería."))
                                : (false, Tr("Este servicio no cumple los requisitos de la empresa") + ": " + string.Join(", ", why) + ". " + Tr("Les ha llegado un aviso a los gestores."));
            }
            catch { return (false, null); }
        }
    }
}
