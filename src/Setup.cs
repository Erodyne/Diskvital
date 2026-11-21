using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Un solo exe che fa anche da installer: "/S" installa in silenzio in Programmi (come gli installer NSIS
// usati dalla suite), "DiskVital-Setup-*.exe" aperto a mano chiede se installare, "--disinstalla" rimuove.
public static class Setup {
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskVital";
    public const string TaskName = "Erodyne DiskVital";
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DiskVital");
    public static readonly string Exe = Path.Combine(Dir, "DiskVital.exe");
    static string Me { get { return Path.GetFullPath(Assembly.GetExecutingAssembly().Location); } }
    static string StartLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "DiskVital.lnk"); } }
    static string DesktopLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "DiskVital.lnk"); } }

    public static bool IsInstalledCopy { get { return string.Equals(Me, Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase); } }
    public static bool Installed { get { return File.Exists(Exe); } }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool DeleteFile(string path);

    static RegistryKey Hklm() { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default); }

    public static bool OfferInstall() {
        if (IsInstalledCopy) return false;
        if (!Path.GetFileName(Me).StartsWith("DiskVital-Setup", StringComparison.OrdinalIgnoreCase)) return false;
        var r = MessageBox.Show("Installare DiskVital " + Program.Version + "?\n\nVa in Programmi, con collegamenti nel menu Start e sul desktop.\n\nNo = usalo senza installarlo.",
            "DiskVital", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return true;
        if (r == DialogResult.No) return false;
        if (!Install(false)) return true;
        try { Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true, WorkingDirectory = Dir }); } catch { }
        return true;
    }

    public static bool Install(bool silent) {
        try {
            bool monitor = MonitorScheduled();
            StopMonitor();
            Directory.CreateDirectory(Dir);
            try { File.Delete(Exe + ".old"); } catch { }
            if (!IsInstalledCopy) {
                var tmp = Exe + ".new";
                File.Copy(Me, tmp, true);
                DeleteFile(tmp + ":Zone.Identifier");
                if (File.Exists(Exe)) File.Move(Exe, Exe + ".old"); // un exe aperto si può rinominare, non sovrascrivere
                File.Move(tmp, Exe);
                try { File.Delete(Exe + ".old"); } catch { }
            }
            Shortcut(StartLnk); Shortcut(DesktopLnk);
            using (var b = Hklm())
            using (var k = b.CreateSubKey(KeyPath)) {
                k.SetValue("DisplayName", "DiskVital");
                k.SetValue("DisplayVersion", Program.Version);
                k.SetValue("Publisher", "Erodyne");
                k.SetValue("DisplayIcon", Exe + ",0");
                k.SetValue("InstallLocation", Dir);
                k.SetValue("UninstallString", "\"" + Exe + "\" --disinstalla");
                k.SetValue("QuietUninstallString", "\"" + Exe + "\" --disinstalla /S");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
            if (monitor) { SetMonitor(true); }
            return true;
        } catch (Exception e) {
            Program.Log(e);
            if (!silent) MessageBox.Show("Installazione non riuscita:\n" + e.Message, "DiskVital", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    public static void Uninstall(bool silent) {
        if (!silent && MessageBox.Show("Disinstallare DiskVital?", "DiskVital", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        bool wipe = !silent && Directory.Exists(History.Dir) &&
            MessageBox.Show("Eliminare anche lo storico dei dischi?", "DiskVital", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        StopMonitor();
        Schtasks("/Delete /TN \"" + TaskName + "\" /F");
        try { File.Delete(StartLnk); } catch { }
        try { File.Delete(DesktopLnk); } catch { }
        try { using (var b = Hklm()) b.DeleteSubKeyTree(KeyPath, false); } catch { }
        if (wipe) try { Directory.Delete(AppSettings.Dir, true); } catch { }
        // la cartella (con l'exe in uso) si cancella dopo la chiusura
        try {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Process.Start(new ProcessStartInfo(cmd, "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + Dir + "\"") {
                WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        } catch { }
        if (!silent) MessageBox.Show("DiskVital è stato disinstallato.", "DiskVital", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static void Shortcut(string lnk) {
        try {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(t);
            try {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                var st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { Exe });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Dir });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { Exe + ",0" });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Salute, temperatura e storico dei dischi" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Marshal.FinalReleaseComObject(sc);
            } finally { Marshal.FinalReleaseComObject(sh); }
        } catch (Exception e) { Program.Log(e); }
    }

    // ---------- monitoraggio all'accesso (Utilità di pianificazione, con i privilegi più elevati: niente richiesta UAC) ----------
    static int Schtasks(string args) {
        try {
            var exe = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            using (var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true })) {
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                return p.HasExited ? p.ExitCode : -1;
            }
        } catch (Exception e) { Program.Log(e); return -1; }
    }

    public static bool MonitorScheduled() { return Schtasks("/Query /TN \"" + TaskName + "\"") == 0; }

    public static bool MonitorRunning() {
        Mutex m;
        if (!Mutex.TryOpenExisting(Program.MonitorMutex, out m)) return false;
        m.Dispose(); return true;
    }

    public static void StopMonitor() {
        try {
            EventWaitHandle ev;
            if (EventWaitHandle.TryOpenExisting(Program.MonitorExit, out ev)) using (ev) ev.Set();
            for (int i = 0; i < 40 && MonitorRunning(); i++) Thread.Sleep(150);
        } catch { }
    }

    // Solo dalla copia installata in Programmi: un'attività "con privilegi più elevati" non deve mai puntare
    // a un exe in una cartella dove l'utente (o un altro programma) può scrivere.
    public static string SetMonitor(bool on) {
        if (!on) {
            Schtasks("/Delete /TN \"" + TaskName + "\" /F");
            StopMonitor();
            return null;
        }
        if (!Installed) return "Il monitoraggio continuo richiede DiskVital installato (dalla Erodyne Suite o con DiskVital-Setup).";
        var user = WindowsIdentity.GetCurrent().Name;
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
            "<RegistrationInfo><Author>Erodyne</Author><Description>DiskVital: controlla la salute dei dischi e avvisa se peggiora.</Description></RegistrationInfo>" +
            "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + "</UserId><Delay>PT1M</Delay></LogonTrigger></Triggers>" +
            "<Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
            "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority>" +
            "<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings></Settings>" +
            "<Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(Exe) + "</Command><Arguments>--monitor</Arguments><WorkingDirectory>" + SecurityElement.Escape(Dir) + "</WorkingDirectory></Exec></Actions></Task>";
        var tmp = Path.Combine(Path.GetTempPath(), "diskvital-" + Guid.NewGuid().ToString("N") + ".xml");
        try {
            File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
            if (Schtasks("/Create /F /TN \"" + TaskName + "\" /XML \"" + tmp + "\"") != 0) return "Impossibile creare l'attività pianificata del monitoraggio.";
        } finally { try { File.Delete(tmp); } catch { } }
        if (!MonitorRunning()) try { Process.Start(new ProcessStartInfo(Exe, "--monitor") { UseShellExecute = false, WorkingDirectory = Dir }); } catch (Exception e) { Program.Log(e); }
        return null;
    }
}
