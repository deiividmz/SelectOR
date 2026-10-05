// Interop mínimo para tematizar lo que WinForms no deja: barras de scroll NATIVAS.
// También el cifrado DPAPI de Windows (por usuario) para "recordar contraseña".

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace SelectOR
{
    static class Native
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        // ---- traer una ventana al frente ----
        // Windows no deja que un programa robe el foco porque sí. Al terminar la carga sí nos
        // corresponde (el usuario acaba de abrir SelectOR), así que se pide el primer plano
        // enganchándose un instante a la entrada de la ventana activa, que es lo que permite el
        // cambio; si aun así Windows lo rechaza, la ventana queda al menos arriba de las demás.
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(int idAttach, int idAttachTo, bool fAttach);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("kernel32.dll")] static extern int GetCurrentThreadId();
        const int SW_SHOW = 5;

        public static IntPtr Foreground() { try { return GetForegroundWindow(); } catch { return IntPtr.Zero; } }

        // Devuelve el primer plano a una ventana sin tocar su estado (para volver al simulador).
        public static void GiveForeground(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            try { SetForegroundWindow(hWnd); } catch { }
        }

        public static void ForceForeground(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            try
            {
                ShowWindow(hWnd, SW_SHOW);
                BringWindowToTop(hWnd);
                if (SetForegroundWindow(hWnd)) return;

                IntPtr fore = GetForegroundWindow();
                int other = GetWindowThreadProcessId(fore, IntPtr.Zero);
                int mine = GetCurrentThreadId();
                if (other == 0 || other == mine) return;
                AttachThreadInput(mine, other, true);
                try { BringWindowToTop(hWnd); SetForegroundWindow(hWnd); }
                finally { AttachThreadInput(mine, other, false); }
            }
            catch { }
        }

        // ---- DPAPI (crypt32) para cifrar la contraseña recordada, ligada a este usuario de Windows ----
        [StructLayout(LayoutKind.Sequential)]
        struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr hMem);

        const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        // SEGURIDAD: rutas sacadas de archivos de contenido (Include, shapes…). Una ruta de red
        // («\\servidor\carpeta\x») hace que Windows se conecte a ese servidor y le entregue la
        // identificación del usuario (hash NTLM). Solo se aceptan rutas locales normales.
        public static bool IsLocalFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (path.StartsWith(@"\\") || path.StartsWith("//")) return false;   // red o \\?\ / \\.\
            try { return !new Uri(Path.GetFullPath(path)).IsUnc; } catch { return false; }
        }

        // DPAPI (usuario actual de Windows) con ENTROPÍA propia de SelectOR: sin ella, cualquier
        // programa del mismo usuario podía descifrar lo guardado con solo pedírselo a Windows.
        // Lo cifrado así lleva el prefijo «v2:»; lo antiguo (sin prefijo) se sigue leyendo.
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SelectOR·DPAPI·v2·7c1e9b04");
        const string V2 = "v2:";

        /// <summary>Cifra un texto con DPAPI (usuario actual + entropía) y lo devuelve en Base64. null si falla.</summary>
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return null;
            var enc = ProtectBytes(Encoding.UTF8.GetBytes(plain));
            return enc == null ? null : V2 + Convert.ToBase64String(enc);
        }

        /// <summary>Descifra lo producido por Protect (también el formato antiguo sin entropía). null si falla.</summary>
        public static string Unprotect(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return null;
            bool v2 = base64.StartsWith(V2, StringComparison.Ordinal);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(v2 ? base64.Substring(V2.Length) : base64); } catch { return null; }
            var plain = UnprotectBytes(bytes, v2);
            return plain == null ? null : Encoding.UTF8.GetString(plain);
        }

        /// <summary>¿Está en el formato antiguo (sin entropía)? Conviene volver a cifrarlo.</summary>
        public static bool IsLegacyProtected(string base64) =>
            !string.IsNullOrEmpty(base64) && !base64.StartsWith(V2, StringComparison.Ordinal);

        public static byte[] ProtectBytes(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            var inBlob = new DATA_BLOB(); var outBlob = new DATA_BLOB(); var entBlob = new DATA_BLOB();
            var h = GCHandle.Alloc(data, GCHandleType.Pinned);
            var he = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
            IntPtr pEnt = IntPtr.Zero;
            try
            {
                inBlob.cbData = data.Length; inBlob.pbData = h.AddrOfPinnedObject();
                entBlob.cbData = Entropy.Length; entBlob.pbData = he.AddrOfPinnedObject();
                pEnt = Marshal.AllocHGlobal(Marshal.SizeOf<DATA_BLOB>());
                Marshal.StructureToPtr(entBlob, pEnt, false);
                if (!CryptProtectData(ref inBlob, "SelectOR", pEnt, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                    return null;
                var outBytes = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
                return outBytes;
            }
            catch { return null; }
            finally
            {
                if (h.IsAllocated) h.Free(); if (he.IsAllocated) he.Free();
                if (pEnt != IntPtr.Zero) Marshal.FreeHGlobal(pEnt);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        public static byte[] UnprotectBytes(byte[] data, bool withEntropy = true)
        {
            if (data == null || data.Length == 0) return null;
            var inBlob = new DATA_BLOB(); var outBlob = new DATA_BLOB(); var entBlob = new DATA_BLOB();
            var h = GCHandle.Alloc(data, GCHandleType.Pinned);
            var he = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
            IntPtr pEnt = IntPtr.Zero;
            try
            {
                inBlob.cbData = data.Length; inBlob.pbData = h.AddrOfPinnedObject();
                if (withEntropy)
                {
                    entBlob.cbData = Entropy.Length; entBlob.pbData = he.AddrOfPinnedObject();
                    pEnt = Marshal.AllocHGlobal(Marshal.SizeOf<DATA_BLOB>());
                    Marshal.StructureToPtr(entBlob, pEnt, false);
                }
                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, pEnt, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                    return null;
                var outBytes = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
                return outBytes;
            }
            catch { return null; }
            finally
            {
                if (h.IsAllocated) h.Free(); if (he.IsAllocated) he.Free();
                if (pEnt != IntPtr.Zero) Marshal.FreeHGlobal(pEnt);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        // Oscurece las barras de scroll NATIVAS de un control (ListBox/TextBox/ComboBox) para que
        // combinen con el tema oscuro (Windows 10 1809+). Si el SO no lo soporta, no hace nada.
        // ---- Enviar un archivo a la PAPELERA de Windows (en vez de borrarlo sin vuelta atrás) ----
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo;
            public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle;
        }
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

        /// <summary>Manda el archivo a la papelera. Si no se puede, lo borra directamente.</summary>
        public static void RecycleFile(string path)
        {
            const uint FO_DELETE = 0x0003;
            const ushort FOF_ALLOWUNDO = 0x0040, FOF_NOCONFIRMATION = 0x0010, FOF_SILENT = 0x0004, FOF_NOERRORUI = 0x0400;
            try
            {
                var op = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = path + "\0\0",
                    fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI)
                };
                if (SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted) return;
            }
            catch { }
            System.IO.File.Delete(path);   // respaldo: borrado normal
        }

        public static void UseDarkScrollBars(Control c)
        {
            if (c == null) return;
            void Apply() { try { if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } }
            c.HandleCreated += (s, e) => Apply();
            Apply();
        }
    }
}
