using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
[assembly: AssemblyTitle("DiskVital")]
[assembly: AssemblyProduct("DiskVital")]
[assembly: AssemblyCompany("Erodyne")]
[assembly: AssemblyDescription("Salute, temperatura e storico dei dischi")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
// DLL di sistema solo da System32: una DLL omonima lasciata in Download accanto all'exe non viene caricata
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]

public static class Program {
    public const string Version = "1.0.0";
    public const string AppMutex = "Erodyne.DiskVital.App", MonitorMutex = "Erodyne.DiskVital.Monitor", MonitorExit = "Erodyne.DiskVital.MonitorExit";

    public static void Log(object e) {
        try {
            System.IO.Directory.CreateDirectory(AppSettings.Dir);
            var lf = System.IO.Path.Combine(AppSettings.Dir, "errori.log");
            if (System.IO.File.Exists(lf) && new System.IO.FileInfo(lf).Length > 1024 * 1024) System.IO.File.Delete(lf);
            System.IO.File.AppendAllText(lf, DateTime.Now + " " + e + "\r\n");
        } catch { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool SetDllDirectory(string path);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr FindWindow(string c, string t);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int n);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    static bool Has(string[] a, string s) { foreach (var x in a) if (string.Equals(x, s, StringComparison.OrdinalIgnoreCase)) return true; return false; }

    [STAThread]
    static int Main(string[] args) {
        try { SetDllDirectory(""); } catch { }
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log(e.ExceptionObject);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool silent = Has(args, "/S") || Has(args, "--silenzioso");
        try {
            if (Has(args, "--disinstalla")) { Setup.Uninstall(silent); return 0; }
            if (Has(args, "--installa") || Has(args, "/S")) return Setup.Install(silent) ? 0 : 1;
            if (Has(args, "--monitor")) { RunMonitor(); return 0; }
            if (Setup.OfferInstall()) return 0;
            RunApp();
        } catch (Exception e) {
            Log(e);
            if (!silent) MessageBox.Show("DiskVital non riesce ad avviarsi:\n\n" + e.Message, "DiskVital", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        return 0;
    }

    static void RunApp() {
        bool first;
        using (var m = new Mutex(true, AppMutex, out first)) {
            if (!first) {
                var h = FindWindow(null, "DiskVital");
                if (h != IntPtr.Zero) { ShowWindow(h, 9); SetForegroundWindow(h); }
                return;
            }
            Application.ThreadException += (s, e) => { Log(e.Exception); MessageBox.Show(e.Exception.Message, "DiskVital", MessageBoxButtons.OK, MessageBoxIcon.Warning); };
            Application.Run(new MainForm());
        }
    }

    static void RunMonitor() {
        bool first;
        using (var m = new Mutex(true, MonitorMutex, out first)) {
            if (!first) return;
            Application.ThreadException += (s, e) => Log(e.Exception);
            Application.Run(new TrayMonitor());
        }
    }
}
