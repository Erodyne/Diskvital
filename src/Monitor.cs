using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

// Monitoraggio nell'area di notifica: controlla i dischi ogni 10 minuti, salva lo storico
// e avvisa quando la salute peggiora, compaiono settori difettosi o la temperatura è troppo alta.
public class TrayMonitor : ApplicationContext {
    readonly NotifyIcon icon = new NotifyIcon();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Dictionary<string, DateTime> tempAlert = new Dictionary<string, DateTime>();
    readonly EventWaitHandle exitSignal;
    readonly Control ui = new Control();
    int busy;

    public TrayMonitor() {
        ui.CreateControl();
        bool created;
        exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.MonitorExit, out created);
        ThreadPool.RegisterWaitForSingleObject(exitSignal, (s, t) => { try { ui.BeginInvoke((Action)Quit); } catch { } }, null, -1, true);

        try { icon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { icon.Icon = SystemIcons.Information; }
        icon.Text = "DiskVital: monitoraggio dei dischi";
        var menu = new ContextMenuStrip();
        menu.Items.Add("Apri DiskVital", null, (s, e) => OpenApp());
        menu.Items.Add("Controlla ora", null, (s, e) => Check(true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Disattiva il monitoraggio", null, (s, e) => {
            if (MessageBox.Show("Disattivare il monitoraggio dei dischi?\n\nDiskVital non controllerà più i dischi in background né all'accesso.", "DiskVital",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            ThreadPool.QueueUserWorkItem(_ => Setup.SetMonitor(false));
        });
        menu.Items.Add("Esci", null, (s, e) => Quit());
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (s, e) => OpenApp();
        icon.BalloonTipClicked += (s, e) => OpenApp();
        icon.Visible = true;

        timer.Interval = Math.Max(5, AppSettings.GetInt("monitor.minuti", 10)) * 60 * 1000;
        timer.Tick += (s, e) => Check(false);
        timer.Start();
        Check(false);
    }

    void OpenApp() {
        try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = false }); } catch (Exception e) { Program.Log(e); }
    }

    void Check(bool manual) {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ => {
            var msgs = new List<string>();
            Health worst = Health.Good;
            int count = 0;
            try {
                foreach (var d in DiskReader.ReadAll()) {
                    count++;
                    if (d.Health > worst && d.Health != Health.Unknown) worst = d.Health;
                    var name = Short(d);
                    foreach (var ev in History.Record(d)) msgs.Add(name + " — " + ev);
                    if (d.Temp.HasValue && d.Temp.Value >= d.TempCrit) {
                        DateTime last;
                        if (!tempAlert.TryGetValue(d.Key, out last) || (DateTime.Now - last).TotalMinutes > 60) {
                            tempAlert[d.Key] = DateTime.Now;
                            msgs.Add(name + " — temperatura " + d.Temp + " °C (limite " + d.TempCrit + " °C)");
                        }
                    }
                }
            } catch (Exception e) { Program.Log(e); }
            Interlocked.Exchange(ref busy, 0);
            try {
                ui.BeginInvoke((Action)(() => {
                    icon.Text = "DiskVital: " + count + (count == 1 ? " disco, " : " dischi, ") + (worst == Health.Good ? "tutto a posto" : Fmt.HealthText(worst).ToLowerInvariant());
                    if (msgs.Count > 0)
                        icon.ShowBalloonTip(15000, "DiskVital: controlla i dischi", string.Join("\n", msgs.Take(4).ToArray()) + (msgs.Count > 4 ? "\n…" : ""), worst == Health.Bad ? ToolTipIcon.Error : ToolTipIcon.Warning);
                    else if (manual)
                        icon.ShowBalloonTip(5000, "DiskVital", worst == Health.Good ? "Tutti i dischi sono in buona salute." : "Nessun nuovo peggioramento. Stato peggiore: " + Fmt.HealthText(worst) + ".", ToolTipIcon.Info);
                }));
            } catch { }
        });
    }

    static string Short(DiskInfo d) { return d.Model + (d.Volumes.Count > 0 ? " (" + d.Letters + ")" : ""); }

    void Quit() {
        timer.Stop();
        icon.Visible = false;
        icon.Dispose();
        ExitThread();
    }
}
