// Aspecto de la Map Window de Open Rails (Ctrl+9) cuando se juega desde SelectOR, sin cambiar Open Rails:
//  1. TEMA «SelectOR»: se añade a los temas de la ventana (MapThemeProvider: light, dark, solarized…) con los colores de
//     SelectOR (fondo gris oscuro, botones planos, verde de acento, lienzo y vía como el mapa grande del HUD) y es el de
//     por defecto. El botón de cambiar de tema lo incluye en la rotación. Lo elegido se recuerda en
//     %AppData%\Open Rails\SelectOR\mapa-tema.txt; a la configuración de Open Rails NUNCA se le guarda «selector» (si
//     se abriera Open Rails sin SelectOR, ese tema no existiría y la ventana fallaría): se queda con el que tenía.
//  2. TEXTOS EN ESPAÑOL: los de la propia ventana (casillas, botones, menús), si Open Rails va en español.
//  3. TIPOGRAFÍA: Segoe UI en toda la ventana y en sus menús, menús oscuros con el tema SelectOR.
//  4. COLORES DEL MAPA (con el tema SelectOR): los lápices y pinceles con que Open Rails dibuja el mapa son campos de la
//     ventana: se les cambia el color (no el dibujo) para que se lean sobre el fondo oscuro, con la paleta del mapa
//     grande del HUD: etiquetas de trenes, señales, andenes y apartaderos claras, recorrido en el ámbar del itinerario,
//     marco del zoom claro. Los aspectos de las señales siguen en rojo, amarillo y verde. Con otro tema, los de siempre.
// Todo se busca por nombre: lo que una versión de Open Rails no tenga, simplemente no se toca.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using HarmonyLib;

namespace SelectOR.SignalGuard
{
    static class MapStyle
    {
        public const string ThemeName = "selector";
        static readonly Color Bg = Color.FromArgb(34, 36, 38), Surface = Color.FromArgb(45, 48, 51), Surface2 = Color.FromArgb(58, 62, 66),
                              SurfaceHi = Color.FromArgb(74, 79, 84), Border = Color.FromArgb(72, 77, 82), Text = Color.FromArgb(240, 242, 243),
                              Accent = Color.FromArgb(76, 175, 80), AccentHi = Color.FromArgb(102, 197, 106), Accent2 = Color.FromArgb(46, 125, 50),
                              Canvas = Color.FromArgb(30, 34, 38), Track = Color.FromArgb(150, 158, 166);
        static Type _themeStyle;

        public static int Install(Harmony h, Type mapViewer)
        {
            int n = 0;
            var asm = mapViewer.Assembly;
            var provider = asm.GetType("Orts.Viewer3D.Map.MapThemeProvider");
            _themeStyle = asm.GetType("Orts.Viewer3D.Map.ThemeStyle");
            if (provider != null && _themeStyle != null) n += Patch(h, provider, "InitializeThemes", nameof(AfterInitializeThemes), postfix: true);
            n += Patch(h, mapViewer, "InitializeForm", nameof(AfterInitializeForm), postfix: true);
            n += Patch(h, mapViewer, "ApplyThemeRecursively", nameof(AfterApplyTheme), postfix: true);
            n += Patch(h, mapViewer, "rotateThemesButton_Click", nameof(AfterRotate), postfix: true);
            n += Patch(h, mapViewer, "c_ControlChanged", nameof(BeforeControlChanged), postfix: false);
            return n;
        }

        static int Patch(Harmony h, Type t, string method, string handler, bool postfix)
        {
            var m = AccessTools.Method(t, method);
            if (m == null) { Guard.Log($"Map Window: no tiene {t.Name}.{method}"); return 0; }
            var hm = new HarmonyMethod(typeof(MapStyle), handler);
            if (postfix) h.Patch(m, postfix: hm); else h.Patch(m, prefix: hm);
            return 1;
        }

        // ---------------- 1. el tema ----------------
        static void AfterInitializeThemes(object __instance)
        {
            try
            {
                var themes = Traverse.Create(__instance).Field("Themes").GetValue() as System.Collections.IDictionary;
                if (themes == null || themes.Contains(ThemeName)) return;
                var t = Activator.CreateInstance(_themeStyle);
                var tr = Traverse.Create(t);
                tr.Field("BackColor").SetValue(Surface2);
                tr.Field("ForeColor").SetValue(Text);
                tr.Field("PanelBackColor").SetValue(Bg);
                tr.Field("FlatStyle").SetValue(FlatStyle.Flat);
                tr.Field("MapCanvasColor").SetValue(Canvas);
                tr.Field("TrackColor").SetValue(Track);
                themes[ThemeName] = t;
            }
            catch (Exception ex) { Guard.Log("Map Window, tema: " + ex.Message); }
        }

        // Al montar la ventana (ya con el tema guardado de Open Rails puesto): el de SelectOR (o el que se eligió
        // jugando desde SelectOR), la letra y los textos.
        public static void AfterInitializeForm(object __instance)
        {
            try
            {
                var mv = Traverse.Create(__instance);
                string want = SavedTheme() ?? ThemeName;
                var provider = mv.Field("MapThemeProvider").GetValue();
                var theme = provider == null ? null : Traverse.Create(provider).Method("GetTheme", new[] { typeof(string) }).GetValue(want);
                if (theme != null)
                {
                    mv.Field("ThemeName").SetValue(want);
                    mv.Field("Theme").SetValue(theme);
                    mv.Method("ApplyThemeRecursively", new[] { typeof(Control) }).GetValue(__instance);
                    mv.Field("MapCanvasColor").SetValue(Traverse.Create(theme).Field("MapCanvasColor").GetValue());
                    if (mv.Field("TrackPen").GetValue() is Pen pen) pen.Color = (Color)Traverse.Create(theme).Field("TrackColor").GetValue();
                }
                Fonts(__instance);
                if (Spanish(__instance)) Texts(__instance);
            }
            catch (Exception ex) { Guard.Log("Map Window, aspecto: " + ex.Message); }
        }

        // Cada vez que Open Rails pinta un tema (al abrir y al cambiar): con el de SelectOR, los toques que su tema no
        // sabe dar (bordes, lista y cuadros de texto, títulos en verde, menús oscuros); con los demás, se quitan.
        static void AfterApplyTheme(object __instance, Control parent)
        {
            try
            {
                if (!(parent is Form form)) return;   // (se llama a sí mismo por cada control: solo la vuelta de la ventana)
                bool ours = Traverse.Create(__instance).Field("ThemeName").GetValue() as string == ThemeName;
                if (ours) form.BackColor = Bg;
                Touch(form, ours);
                MapColors(__instance, ours);
                foreach (var f in __instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    if (f.GetValue(__instance) is ContextMenuStrip cm)
                    {
                        cm.Renderer = ours ? new ToolStripProfessionalRenderer(new DarkMenu()) : new ToolStripProfessionalRenderer();
                        foreach (ToolStripItem it in cm.Items) ItemColors(it, ours);
                    }
            }
            catch (Exception ex) { Guard.Log("Map Window, tema: " + ex.Message); }
        }

        static void Touch(Control parent, bool ours)
        {
            foreach (Control c in parent.Controls)
            {
                switch (c)
                {
                    case Button b when b.Tag?.ToString() != "mapCustomization":
                        b.FlatAppearance.BorderColor = ours ? Border : Color.Empty;
                        b.FlatAppearance.MouseOverBackColor = ours ? SurfaceHi : Color.Empty;
                        b.FlatAppearance.MouseDownBackColor = ours ? Accent2 : Color.Empty;
                        b.Cursor = Cursors.Hand;
                        break;
                    case TextBox t:
                        if (ours) { t.BackColor = Surface; t.ForeColor = Text; }
                        t.BorderStyle = ours ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
                        break;
                    case ListBox lb:
                        if (ours) { lb.BackColor = Surface; lb.ForeColor = Text; }
                        lb.BorderStyle = ours ? BorderStyle.None : BorderStyle.Fixed3D;   // (el borde de Windows sale blanco sobre oscuro)
                        break;
                    case ListView lv:
                        if (ours) { lv.BackColor = Surface; lv.ForeColor = Text; }
                        lv.BorderStyle = ours ? BorderStyle.None : BorderStyle.Fixed3D;
                        break;
                    case NumericUpDown nu:
                        if (ours) { nu.BackColor = Surface; nu.ForeColor = Text; }
                        break;
                    case CheckBox _: case RadioButton _:
                        // casillas planas, con el verde de SelectOR al marcarlas
                        var bb = (ButtonBase)c;
                        bb.FlatStyle = ours ? FlatStyle.Flat : FlatStyle.Standard;
                        if (ours) { bb.FlatAppearance.BorderColor = SurfaceHi; bb.FlatAppearance.CheckedBackColor = Accent2; bb.FlatAppearance.MouseOverBackColor = Surface2; }
                        break;
                    case GroupBox g:
                        if (ours) g.ForeColor = AccentHi;   // el título del grupo, en verde (lo de dentro ya tiene su color)
                        break;
                    case LinkLabel l:
                        l.LinkColor = ours ? AccentHi : Color.Empty; l.ActiveLinkColor = ours ? Accent : Color.Empty;
                        break;
                    case Label l2 when l2.Font.Bold && ours:
                        l2.ForeColor = AccentHi;   // títulos («Elementos del mapa», la hora…)
                        break;
                }
                Touch(c, ours);
            }
        }

        // ---------------- 4. los colores del mapa ----------------
        static readonly (string field, Color color, float width)[] Palette =
        {
            ("trainBrush", Color.FromArgb(240, 242, 243), 0),          // nombre del tren (era rojo)
            ("InactiveTrainBrush", Color.FromArgb(170, 176, 181), 0),  // trenes parados (rojo oscuro)
            ("SignalBrush", Color.FromArgb(239, 154, 154), 0),         // nombre de la señal (rojo oscuro)
            ("sidingBrush", Color.FromArgb(222, 205, 252), 0),         // apartaderos (azul): el lila del HUD
            ("PlatformBrush", Color.FromArgb(205, 228, 250), 0),       // andenes (azul oscuro): el azul claro del HUD
            ("pathPen", Color.FromArgb(240, 180, 70), 0),              // recorrido del tren: el ámbar del itinerario
            ("ZoomTargetPen", Color.FromArgb(170, 176, 181), 0),       // marco del zoom (azul casi negro)
            ("grayPen", Color.FromArgb(150, 158, 166), 0),             // señales sin estado
        };
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, Color>> _original = new System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, Color>>();

        static void MapColors(object mv, bool ours)
        {
            var orig = _original.GetValue(mv, _ => new Dictionary<string, Color>());
            foreach (var (field, color, _) in Palette)
            {
                var v = Traverse.Create(mv).Field(field).GetValue();
                Color Get() => v is Pen p ? p.Color : v is SolidBrush b ? b.Color : Color.Empty;
                void Set(Color c) { if (v is Pen p) p.Color = c; else if (v is SolidBrush b) b.Color = c; }
                if (v == null) continue;
                if (!orig.ContainsKey(field)) orig[field] = Get();   // (el de Open Rails, para volver a él)
                Set(ours ? color : orig[field]);
            }
        }

        static void ItemColors(ToolStripItem it, bool ours)
        {
            it.ForeColor = ours ? Text : SystemColors.ControlText;
            it.BackColor = ours ? Surface : SystemColors.Control;
            if (it is ToolStripMenuItem mi) foreach (ToolStripItem sub in mi.DropDownItems) ItemColors(sub, ours);
        }

        sealed class DarkMenu : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Surface;
            public override Color ImageMarginGradientBegin => Surface;
            public override Color ImageMarginGradientMiddle => Surface;
            public override Color ImageMarginGradientEnd => Surface;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Accent2;
            public override Color MenuItemSelected => SurfaceHi;
            public override Color MenuItemSelectedGradientBegin => SurfaceHi;
            public override Color MenuItemSelectedGradientEnd => SurfaceHi;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Surface;
        }

        // El elegido al cambiar de tema, para la próxima vez (en SelectOR; no en Open Rails).
        static void AfterRotate(object __instance)
        {
            try
            {
                if (Traverse.Create(__instance).Field("ThemeName").GetValue() is string name) File.WriteAllText(ThemeFile(), name);
            }
            catch { }
        }

        // Open Rails guarda el tema en su configuración al pulsar el botón: «selector» no se le guarda (se queda con el
        // que tenía, que sí existe sin SelectOR).
        static bool BeforeControlChanged(object __instance, object sender)
        {
            try
            {
                return !(sender is Button b && b.Name == "rotateThemesButton"
                         && Traverse.Create(__instance).Field("ThemeName").GetValue() as string == ThemeName);
            }
            catch { return true; }
        }

        static string ThemeFile()
        {
            string dir = Environment.GetEnvironmentVariable("SELECTOR_CACHE_DIR") is { Length: > 0 } t ? t
                       : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails", "SelectOR");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "mapa-tema.txt");
        }

        static string SavedTheme()
        {
            try { var f = ThemeFile(); return File.Exists(f) ? File.ReadAllText(f).Trim() is { Length: > 0 } s ? s : null : null; } catch { return null; }
        }

        // ---------------- 3. la letra ----------------
        static void Fonts(object mv)
        {
            if (!(mv is Form form)) return;
            void Set(Control c)
            {
                foreach (Control k in c.Controls)
                {
                    if (!(k is PictureBox))   // (el lienzo del mapa lo dibuja Open Rails)
                        k.Font = new Font("Segoe UI", 9f, k.Font.Style);
                    Set(k);
                }
            }
            form.Font = new Font("Segoe UI", 9f);
            Set(form);
            foreach (var f in mv.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                if (f.GetValue(mv) is ContextMenuStrip cm)
                {
                    cm.Font = new Font("Segoe UI", 9.5f);
                    foreach (ToolStripItem it in cm.Items) if (it.Font.Bold) it.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
                }
        }

        // ---------------- 2. los textos ----------------
        // ¿Open Rails va en español? (su idioma; si es el del sistema, el de Windows)
        static bool Spanish(object mv)
        {
            try
            {
                var viewer = Traverse.Create(mv).Field("Viewer");
                if (!viewer.FieldExists()) viewer = Traverse.Create(mv).Property("Viewer");
                var settings = viewer.Property("Settings"); if (!settings.PropertyExists()) settings = viewer.Field("Settings");
                var lang = (settings.Field("Language").FieldExists() ? settings.Field("Language") : settings.Property("Language")).GetValue() as string;
                if (!string.IsNullOrEmpty(lang)) return lang.StartsWith("es", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es";
        }

        public static readonly Dictionary<string, string> Es = new Dictionary<string, string>
        {
            ["playerRoleLink"] = "Ayuda",
            ["messageAllButton"] = "Mensaje a todos",
            ["label2"] = "Mensajes:",
            ["timeLabel"] = "Hora de la simulación",
            ["mapCustomizationButton"] = "Personalizar el mapa",
            ["showAllTrainsRadio"] = "Todos los trenes",
            ["showActiveTrainsRadio"] = "Solo los trenes activos",
            ["showTrainStateCheckbox"] = "Estado de los trenes",
            ["showTrainLabelsCheckbox"] = "Nombres de los trenes",
            ["showSignalStateCheckbox"] = "Estado de las señales",
            ["showSignalsCheckbox"] = "Señales",
            ["showSwitchesCheckbox"] = "Desvíos",
            ["showSidingLabelsCheckbox"] = "Nombres de los apartaderos",
            ["showPlatformLabelsCheckbox"] = "Nombres de los andenes",
            ["showPlatformsCheckbox"] = "Andenes",
            ["label1"] = "Elementos del mapa",
            ["rotateThemesButton"] = "Cambiar de tema",
            ["showTimeCheckbox"] = "Ver la hora de la simulación",
            ["useAntiAliasingCheckbox"] = "Suavizado de líneas",
            ["groupBox2"] = "Ajustes del multijugador",
            ["penaltyCheckbox"] = "Penalizar velocidad y rebases",
            ["preferGreenCheckbox"] = "Preferir señales en verde",
            ["allowJoiningCheckbox"] = "Dejar entrar a más jugadores",
            ["groupBox1"] = "Mapa: ajustes y controles",
            ["followMyTrainOnMap"] = "Seguir a mi tren en el mapa",
            ["centerOnMyTrainButton"] = "Ir a mi tren en el mapa",
            ["seeTrainInGameButton"] = "Ir a mi tren en el juego",
            ["label3"] = "Resolución (m):",
            ["allowThrowingSwitchesCheckbox"] = "Permitir cambiar desvíos",
            ["allowChangingSignalsCheckbox"] = "Permitir cambiar señales",
            ["drawPathCheckbox"] = "Dibujar el siguiente tramo",
            ["groupBox3"] = "Jugadores",
            ["messageSelectedPlayerMenuItem"] = "Mensaje al jugador seleccionado",
            ["replyToSelectedPlayerMenuItem"] = "Responder al jugador seleccionado",
            ["playerToolStripMenuItem"] = "Jugador",
            ["makeThisPlayerAnAssistantToolStripMenuItem"] = "Hacer ayudante",
            ["jumpToThisPlayerInGameToolStripMenuItem"] = "Ir a este jugador en el juego",
            ["followToolStripMenuItem"] = "Seguirlo en el mapa",
            ["kickFromMultiplayerSessionToolStripMenuItem"] = "Echar de la partida",
            ["setSwitchToToolStripMenuItem"] = "Poner el desvío a…",
            ["mainRouteToolStripMenuItem"] = "Vía directa",
            ["sideRouteToolStripMenuItem"] = "Vía desviada",
            ["setSignalAspectToToolStripMenuItem"] = "Poner la señal en…",
            ["systemControlledToolStripMenuItem"] = "Automática (la decide el sistema)",
            ["stopToolStripMenuItem"] = "Parada",
            ["approachToolStripMenuItem"] = "Anuncio de parada",
            ["proceedToolStripMenuItem"] = "Vía libre",
            ["allowCallOnToolStripMenuItem"] = "Rebase autorizado",
            ["jumpToThisTrainInGameToolStripMenuItem"] = "Ir a este tren en el juego",
            ["followThisTrainOnTheMapToolStripMenuItem"] = "Seguir este tren en el mapa",
        };

        static void Texts(object mv)
        {
            foreach (var kv in Es)
            {
                var v = Traverse.Create(mv).Field(kv.Key).GetValue();
                if (v is Control c) c.Text = kv.Value;
                else if (v is ToolStripItem it) it.Text = kv.Value;
            }
            if (mv is Form form) form.Text = "Mapa";
        }

        // «Hacer ayudante» / «Quitar de ayudante» (Open Rails lo pone en inglés al abrir el menú de un jugador).
        public static void PlayerMenuText(object mv, bool aider)
        {
            if (!Spanish(mv)) return;
            if (Traverse.Create(mv).Field("makeThisPlayerAnAssistantToolStripMenuItem").GetValue() is ToolStripItem it)
                it.Text = aider ? "Quitar de ayudante" : "Hacer ayudante";
        }
    }
}
