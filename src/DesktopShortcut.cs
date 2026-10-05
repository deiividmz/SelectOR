// Acceso directo en el escritorio: «SelectOR (Open Rails <versión>)», creado en la carga inicial.
//  · Uno por versión de Open Rails: quien tenga SelectOR en varias instalaciones (Testing, NewYear…) tiene uno
//    para cada una, y se distinguen por el nombre.
//  · Si ya existe, o si ya se creó alguna vez para esa versión (aunque después se borrara), no se hace nada:
//    la versión queda anotada en las preferencias (DesktopShortcuts).
//  · Con preferencias o cachés de prueba (SELECTOR_PREFS_FILE / SELECTOR_CACHE_DIR) no toca el escritorio del
//    usuario: solo crea el acceso si las pruebas dicen dónde (SELECTOR_DESKTOP_DIR).

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SelectOR
{
    static class DesktopShortcut
    {
        // Versión legible:
        //  · sin el sufijo interno de compilación («T1.6.1-438-g3a4d79804» → «T1.6.1-438»);
        //  · las New Year se llaman por su número («ORNYMG-Rev. 174.» → «New Year 174»): así cada carpeta con una
        //    New Year distinta (174, 175…) tiene su propio acceso.
        public static string CleanVersion(string orVersion)
        {
            string v = (orVersion ?? "").Trim();
            var ny = System.Text.RegularExpressions.Regex.Match(v, @"^ORNY\w*-?\s*Rev\.?\s*([\d.]*\d)\.?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (ny.Success) return "New Year " + ny.Groups[1].Value;
            v = System.Text.RegularExpressions.Regex.Replace(v, @"-g[0-9a-fA-F]{6,}$", "");
            return v.TrimEnd('.', ' ');
        }

        public static string NameFor(string orVersion)
        {
            string v = CleanVersion(orVersion);
            string name = v.Length > 0 ? $"SelectOR (Open Rails {v})" : "SelectOR";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
            return name;
        }

        // Devuelve la ruta del acceso creado, o null si no había que crear nada.
        public static string Ensure(string orVersion, AppPrefs prefs)
        {
            try
            {
                string desk = Environment.GetEnvironmentVariable("SELECTOR_DESKTOP_DIR");
                bool pruebas = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SELECTOR_PREFS_FILE"))
                               || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SELECTOR_CACHE_DIR"));
                if (string.IsNullOrEmpty(desk)) { if (pruebas) return null; desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
                if (string.IsNullOrEmpty(desk) || !Directory.Exists(desk)) return null;

                string key = CleanVersion(orVersion); if (key.Length == 0) key = "?";
                prefs.DesktopShortcuts ??= new System.Collections.Generic.List<string>();
                string path = Path.Combine(desk, NameFor(orVersion) + ".lnk");
                // Siempre el SelectOR.exe de la carpeta de Open Rails (no «el proceso en marcha», que podría ser otro).
                string exe = Path.Combine(AppContext.BaseDirectory, "SelectOR.exe");
                if (!File.Exists(exe)) exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;
                if (prefs.DesktopShortcuts.Any(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase)))   // ya se creó para esta versión
                {
                    // ...y sigue ahí, pero su carpeta ya no existe (se borró o se movió): pasa a abrir esta
                    if (File.Exists(path) && ReadTarget(path) is string old && !File.Exists(old))
                        Create(path, exe, Path.GetDirectoryName(exe), exe,
                               string.Format(I18n.T("SelectOR · menú y lanzador para Open Rails {0}"), key));
                    return null;
                }
                bool made = false;
                if (!File.Exists(path))
                {
                    Create(path, exe, Path.GetDirectoryName(exe), exe,
                           string.Format(I18n.T("SelectOR · menú y lanzador para Open Rails {0}"), key));
                    made = File.Exists(path);
                }
                prefs.DesktopShortcuts.Add(key);
                try { prefs.Save(); } catch { }
                return made ? path : null;
            }
            catch { return null; }
        }

        // A qué apunta un acceso directo (null si no se puede leer).
        public static string ReadTarget(string lnkPath)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(lnkPath, 0);
                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                return sb.Length > 0 ? sb.ToString() : null;
            }
            catch { return null; }
            finally { Marshal.FinalReleaseComObject(link); }
        }

        static void Create(string lnkPath, string target, string workDir, string icon, string description)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(target);
                link.SetWorkingDirectory(workDir ?? "");
                link.SetIconLocation(icon, 0);
                link.SetDescription(description ?? "");
                ((IPersistFile)link).Save(lnkPath, true);
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }

        // ---------------- COM: IShellLink ----------------
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
        interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }
    }
}
