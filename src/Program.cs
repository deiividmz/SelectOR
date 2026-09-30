// Selector de Trenes y Rutas — menu paralelo para Open Rails.
// No modifica el codigo fuente de Open Rails: referencia sus DLLs ya compiladas
// y lanza RunActivity.exe con los mismos argumentos que el menu original.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Forms;

namespace SelectOR
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // Aseguramos que las DLLs de Open Rails (Orts.*, MonoGame, GNU.Gettext, ...)
            // que estan junto al ejecutable se puedan resolver aunque no figuren en el deps.json.
            AssemblyLoadContext.Default.Resolving += ResolveFromAppFolder;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ConfigurarEscalado();
            // Letra por defecto (la de los controles que no fijan otra) a tamaño de escala 100 %.
            Theme.InitDpi();
            try { Application.SetDefaultFont(new System.Drawing.Font(Theme.FontFamily, 9f * Theme.DpiComp)); } catch { }

            // SelectOR necesita estar DENTRO de la carpeta de Open Rails: reutiliza sus bibliotecas
            // y lanza su RunActivity. Si se ha copiado a otro sitio, se avisa y no se abre.
            if (!EstaEnCarpetaDeOpenRails())
            {
                AvisarCarpetaIncorrecta();
                return;
            }

            try
            {
                bool kiosk = false;
                foreach (var a in Environment.GetCommandLineArgs())
                    if (a.Equals("-kiosk", StringComparison.OrdinalIgnoreCase) ||
                        a.Equals("/kiosk", StringComparison.OrdinalIgnoreCase))
                        kiosk = true;

                // Pantalla de inicio: se abre ya, en su propio hilo, y no se cierra hasta que el
                // contenido está leído (lo hace MainMenuForm al terminar la carga).
                try { Theme.ComputeUiScale(Screen.PrimaryScreen.WorkingArea); } catch { }
                SplashScreen.Begin();

                // Actualización automática: si hay versión nueva, se instala aquí mismo, con la pantalla de
                // carga, y se arranca la nueva sin abrir el menú de esta.
                if (StartupUpdate.Run())
                {
                    SplashScreen.Finish();
                    Updater.LaunchPending();
                    Environment.Exit(0);
                }
                System.Threading.Tasks.Task.Run(AppDataTidy.Run);   // %AppData%: fuera lo que sobra (en segundo plano)

                Application.Run(new MainMenuForm(kiosk));

                // Tras actualizar: la ventana ya se ha cerrado y las preferencias están guardadas. Se arranca
                // la versión nueva y esta termina YA (sin esperar a hilos que se queden colgados), para que
                // nunca estén abiertas las dos a la vez.
                if (Updater.RestartPending)
                {
                    Updater.LaunchPending();
                    Environment.Exit(0);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Error al iniciar el selector",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---- escala de Windows (125 %, 150 %…) ----
        // SelectOR dibuja él mismo, nítido («SystemAware»). La interfaz está maquetada en píxeles,
        // así que las letras se crean a tamaño de escala 100 % (Theme.DpiComp) para que casen con sus
        // cajas en cualquier equipo; antes crecían con la escala de Windows y se cortaban.
        // (El modo «GDI escalado» que se probó descolocaba las letras de los desplegables.)
        // SELECTOR_DPI=unaware: que Windows amplíe la ventana entera (más grande, algo borrosa).
        private static void ConfigurarEscalado()
        {
            try
            {
                if (string.Equals(Environment.GetEnvironmentVariable("SELECTOR_DPI"), "unaware", StringComparison.OrdinalIgnoreCase))
                { Application.SetHighDpiMode(HighDpiMode.DpiUnaware); return; }
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
            }
            catch { }
        }

        // ---- comprobación de la carpeta ----
        // Señas de una instalación de Open Rails junto al ejecutable. Con dos basta: hay versiones
        // (oficial, Testing, New Year...) que no traen exactamente los mismos archivos.
        private static readonly string[] SenasOpenRails =
        {
            "OpenRails.exe", "RunActivity.exe", "Menu.exe",
            "Orts.Menu.dll", "Orts.Settings.dll", "Orts.Common.dll",
            "Orts.Formats.Msts.dll", "MonoGame.Framework.dll"
        };

        private static bool EstaEnCarpetaDeOpenRails()
        {
            try
            {
                string dir = AppContext.BaseDirectory;
                int n = 0;
                foreach (var f in SenasOpenRails)
                    if (File.Exists(Path.Combine(dir, f)) && ++n >= 2) return true;
                return false;
            }
            catch { return true; }   // ante la duda, se deja abrir
        }

        // El aviso sale en el idioma de Windows (aquí todavía no se pueden leer los ajustes de
        // Open Rails, que son los que manda dentro del programa).
        private static void AvisarCarpetaIncorrecta()
        {
            bool es = false;
            try { es = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                       .Equals("es", StringComparison.OrdinalIgnoreCase); }
            catch { }
            string dir = "";
            try { dir = AppContext.BaseDirectory; } catch { }

            string titulo = es ? "SelectOR · carpeta incorrecta" : "SelectOR · wrong folder";
            string texto = es
                ? "SelectOR solo funciona DENTRO de la carpeta donde tienes instalado Open Rails "
                  + "(la que contiene OpenRails.exe), porque reutiliza sus componentes y lanza su simulador.\n\n"
                  + "Copia SelectOR.exe y los archivos que lo acompañan a esa carpeta y ábrelo desde allí.\n\n"
                  + "Carpeta desde la que se ha abierto:\n" + dir
                : "SelectOR only works INSIDE your Open Rails installation folder "
                  + "(the one with OpenRails.exe), because it reuses its components and launches its simulator.\n\n"
                  + "Copy SelectOR.exe and its files into that folder and run it from there.\n\n"
                  + "Folder it was started from:\n" + dir;

            MessageBox.Show(texto, titulo, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static Assembly ResolveFromAppFolder(AssemblyLoadContext ctx, AssemblyName name)
        {
            try
            {
                var candidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
                if (File.Exists(candidate))
                    return ctx.LoadFromAssemblyPath(candidate);
            }
            catch { }
            return null;
        }
    }
}
