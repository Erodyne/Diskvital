using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

public class MainForm : Form, IMessageFilter {
    List<DiskInfo> disks = new List<DiskInfo>();
    int sel;
    bool loading = true, monitorOn, monitorBusy;
    string status = "Lettura dei dischi…";
    int tab;                       // 0 attributi, 1 storico, 2 dettagli
    readonly int[] scroll = new int[3];
    readonly int[] scrollMax = new int[3];
    int attrSel;
    List<Snap> hist = new List<Snap>();
    string histKey;

    readonly Canvas title = new Canvas(), side = new Canvas(), content = new Canvas(), bar = new Canvas();
    readonly Btn bMin = new Btn { Kind = BtnKind.Ghost, Text = "—" }, bMax = new Btn { Kind = BtnKind.Ghost, Text = "▢" }, bClose = new Btn { Kind = BtnKind.Ghost, Text = "✕" };
    readonly System.Windows.Forms.Timer auto = new System.Windows.Forms.Timer { Interval = 60000 };

    const int TitleH = 46, SideW = 272, BarH = 34, M = 28;

    DiskInfo Cur { get { return sel >= 0 && sel < disks.Count ? disks[sel] : null; } }

    public MainForm() {
        Text = "DiskVital";
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Th.Bg; ForeColor = Th.Text;
        var wa = Screen.PrimaryScreen.WorkingArea;
        MinimumSize = new Size(Math.Min(1000, wa.Width), Math.Min(640, wa.Height));
        Size = new Size(Math.Min(1200, wa.Width), Math.Min(780, wa.Height));
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        Padding = new Padding(1);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        title.Height = TitleH; title.Dock = DockStyle.Top; title.BackColor = Th.Side;
        side.Width = SideW; side.Dock = DockStyle.Left; side.BackColor = Th.Side;
        bar.Height = BarH; bar.Dock = DockStyle.Bottom;
        content.Dock = DockStyle.Fill;
        Controls.Add(content); Controls.Add(bar); Controls.Add(side); Controls.Add(title);

        title.Paint += PaintTitle;
        title.MouseDown += (s, e) => { if (e.Button != MouseButtons.Left) return; if (e.Clicks == 2) ToggleMax(); else DragWindow(); };
        foreach (var b in new[] { bMin, bMax, bClose }) { b.Size = new Size(46, TitleH); b.Font = Th.F(10f); title.Controls.Add(b); }
        bMin.Click += (s, e) => WindowState = FormWindowState.Minimized;
        bMax.Click += (s, e) => ToggleMax();
        bClose.Click += (s, e) => Close();
        bClose.HoverColor = Th.Red;
        title.Resize += (s, e) => { bClose.Left = title.Width - 46; bMax.Left = bClose.Left - 46; bMin.Left = bMax.Left - 46; };

        side.Paint += PaintSide;
        side.MouseMove += (s, e) => { int h = SideHit(e.Location); side.Cursor = h != -99 ? Cursors.Hand : Cursors.Default; if (h != sideHover) { sideHover = h; side.Invalidate(); } };
        side.MouseLeave += (s, e) => { sideHover = -99; side.Invalidate(); };
        side.MouseClick += (s, e) => SideClick(SideHit(e.Location));

        bar.Paint += PaintBar;
        content.Paint += PaintContent;
        content.MouseClick += ContentClick;
        content.MouseMove += (s, e) => { content.Cursor = ContentHot(e.Location) ? Cursors.Hand : Cursors.Default; };

        KeyPreview = true;
        KeyDown += (s, e) => {
            if (e.KeyCode == Keys.F5) { Reload(); e.Handled = true; }
            else if (e.KeyCode == Keys.Down && disks.Count > 0 && e.Control) Select((sel + 1) % disks.Count);
            else if (e.KeyCode == Keys.Up && disks.Count > 0 && e.Control) Select((sel - 1 + disks.Count) % disks.Count);
            else if (e.Control && e.KeyCode == Keys.C && Cur != null) CopyReport();
        };
        Application.AddMessageFilter(this);
        auto.Tick += (s, e) => { if (!loading && WindowState != FormWindowState.Minimized) Reload(true); };
        Shown += (s, e) => {
            Reload();
            auto.Start();
            ThreadPool.QueueUserWorkItem(_ => { monitorOn = Setup.MonitorScheduled(); Ui(() => side.Invalidate()); });
        };
    }

    void Ui(Action a) { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { } }

    // ---------- finestra senza bordi (come la suite) ----------
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    void DragWindow() { try { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } catch { } }
    void ToggleMax() { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; }
    protected override CreateParams CreateParams {
        get { var cp = base.CreateParams; cp.Style |= 0x10000 | 0x20000 | 0x40000; return cp; }
    }
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        try { int v = 2; DwmSetWindowAttribute(Handle, 33, ref v, 4); } catch { }
        try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
    }
    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x83 && m.WParam != IntPtr.Zero) {
            if (WindowState == FormWindowState.Maximized) {
                var wa = Screen.FromHandle(Handle).WorkingArea;
                Marshal.WriteInt32(m.LParam, 0, wa.Left); Marshal.WriteInt32(m.LParam, 4, wa.Top);
                Marshal.WriteInt32(m.LParam, 8, wa.Right); Marshal.WriteInt32(m.LParam, 12, wa.Bottom);
            }
            m.Result = IntPtr.Zero; return;
        }
        if (m.Msg == 0x84) { int h = HitEdge(m.LParam); if (h != 0) { m.Result = (IntPtr)h; return; } }
        // Windows (uxtheme) ridisegna la vecchia barra del titolo sopra la nostra all'attivazione,
        // al cambio di titolo/icona e dopo il ripristino: si bloccano quei disegni.
        if (m.Msg == 0xAE || m.Msg == 0xAF) { m.Result = IntPtr.Zero; return; }          // WM_NCUAHDRAWCAPTION / WM_NCUAHDRAWFRAME
        if (m.Msg == 0x85) { m.Result = IntPtr.Zero; return; }                           // WM_NCPAINT: non c'è area non client
        if (m.Msg == 0x86) { m.LParam = new IntPtr(-1); base.WndProc(ref m); m.Result = (IntPtr)1; return; } // WM_NCACTIVATE senza ridisegno
        if ((m.Msg == 0x0C || m.Msg == 0x80) && IsHandleCreated) {                        // WM_SETTEXT / WM_SETICON
            int st = GetWindowLong(Handle, -16);
            SetWindowLong(Handle, -16, st & ~0x10000000);                                // senza WS_VISIBLE non disegna
            base.WndProc(ref m);
            SetWindowLong(Handle, -16, st);
            return;
        }
        base.WndProc(ref m);
        if (m.Msg == 0x24) {
            try {
                var sc = Screen.FromHandle(Handle); var wa = sc.WorkingArea; var bd = sc.Bounds;
                Marshal.WriteInt32(m.LParam, 8, wa.Width); Marshal.WriteInt32(m.LParam, 12, wa.Height);
                Marshal.WriteInt32(m.LParam, 16, wa.Left - bd.Left); Marshal.WriteInt32(m.LParam, 20, wa.Top - bd.Top);
            } catch { }
        }
    }
    public int HitEdge(IntPtr lp) {
        if (WindowState != FormWindowState.Normal) return 0;
        long v = lp.ToInt64();
        var p = PointToClient(new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF)));
        const int g = 7; bool l = p.X < g, r = p.X >= Width - g, t = p.Y < g, b = p.Y >= Height - g;
        if (t && l) return 13; if (t && r) return 14; if (b && l) return 16; if (b && r) return 17;
        if (l) return 10; if (r) return 11; if (t) return 12; if (b) return 15;
        return 0;
    }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); bMax.Text = WindowState == FormWindowState.Maximized ? "❐" : "▢"; content.Invalidate(); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using (var p = new Pen(Th.Border)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); }
    protected override void OnFormClosed(FormClosedEventArgs e) { Application.RemoveMessageFilter(this); auto.Stop(); base.OnFormClosed(e); }

    // rotellina sopra il pannello, anche senza che abbia il fuoco
    public bool PreFilterMessage(ref Message m) {
        if (m.Msg != 0x020A || !Visible) return false;
        var p = content.PointToClient(Cursor.Position);
        if (!content.ClientRectangle.Contains(p)) return false;
        int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
        int ns = Math.Max(0, Math.Min(scrollMax[tab], scroll[tab] - delta / 120 * 48));
        if (ns != scroll[tab]) { scroll[tab] = ns; content.Invalidate(); }
        return true;
    }

    // ---------- dati ----------
    void Reload(bool quiet = false) {
        if (loading && !quiet && disks.Count > 0) return;
        loading = true;
        if (!quiet) { status = "Lettura dei dischi…"; bar.Invalidate(); }
        var key = Cur != null ? Cur.Key + "#" + Cur.Number : null;
        ThreadPool.QueueUserWorkItem(_ => {
            List<DiskInfo> list;
            try { list = DiskReader.ReadAll(); }
            catch (Exception e) { Program.Log(e); list = new List<DiskInfo>(); }
            foreach (var d in list) History.Record(d);
            Ui(() => {
                disks = list;
                int i = key == null ? -1 : disks.FindIndex(x => x.Key + "#" + x.Number == key);
                sel = i >= 0 ? i : 0;
                loading = false;
                if (attrSel >= (Cur != null ? Cur.Attrs.Count : 0)) attrSel = 0;
                histKey = null;
                int bad = disks.Count(x => x.Health == Health.Bad), cau = disks.Count(x => x.Health == Health.Caution);
                status = disks.Count == 0 ? "Nessun disco trovato" :
                    disks.Count + (disks.Count == 1 ? " disco" : " dischi") + " · " +
                    (bad > 0 ? bad + " in stato critico" : cau > 0 ? cau + " da tenere d'occhio" : "tutti in buona salute") +
                    " · aggiornato alle " + DateTime.Now.ToString("HH:mm");
                LoadHistory();
                side.Invalidate(); content.Invalidate(); bar.Invalidate();
            });
        });
    }

    void LoadHistory() {
        var d = Cur;
        if (d == null) { hist = new List<Snap>(); return; }
        if (histKey == d.Key) return;
        histKey = d.Key;
        hist = History.Load(d);
    }

    void Select(int i) {
        if (i == sel) return;
        sel = i; attrSel = 0; scroll[0] = scroll[1] = scroll[2] = 0;
        LoadHistory();
        side.Invalidate(); content.Invalidate();
    }

    void SetStatus(string s) { status = s; bar.Invalidate(); }

    void CopyReport() {
        try { Clipboard.SetText(Report.Text(Cur)); SetStatus("Rapporto di " + Cur.Model + " copiato negli appunti"); }
        catch (Exception e) { SetStatus("Copia non riuscita: " + e.Message); }
    }

    // Certificato di stato del disco (pagina A4) in PDF o JPEG
    void SaveCertificate(bool pdf) {
        var d = Cur;
        if (d == null) return;
        var safe = System.Text.RegularExpressions.Regex.Replace(d.Model, @"[\\/:*?""<>|]+", " ").Trim();
        using (var dlg = new SaveFileDialog {
            Title = pdf ? "Salva il certificato in PDF" : "Salva il certificato in JPEG",
            Filter = pdf ? "Documento PDF (*.pdf)|*.pdf" : "Immagine JPEG (*.jpg)|*.jpg",
            FileName = "Certificato DiskVital - " + safe + " - " + DateTime.Now.ToString("yyyy-MM-dd") + (pdf ? ".pdf" : ".jpg") }) {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try {
                using (var bmp = Certificate.Render(d, d.Key == histKey ? hist : History.Load(d))) {
                    if (pdf) Certificate.SavePdf(bmp, dlg.FileName, "Certificato di stato del disco - " + d.Model);
                    else Certificate.SaveJpeg(bmp, dlg.FileName);
                }
                SetStatus("Certificato salvato: " + dlg.FileName + "  ·  ID " + Certificate.DocumentId(d));
                try { Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + dlg.FileName + "\""); } catch { }
            } catch (Exception e) { Program.Log(e); SetStatus("Salvataggio non riuscito: " + e.Message); }
        }
    }

    void Export() {
        if (disks.Count == 0) return;
        using (var dlg = new SaveFileDialog { Title = "Salva il rapporto", Filter = "Pagina web (*.html)|*.html|Testo (*.txt)|*.txt", FileName = "Rapporto DiskVital " + DateTime.Now.ToString("yyyy-MM-dd") + ".html" }) {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try {
                if (dlg.FilterIndex == 2 || dlg.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(dlg.FileName, string.Join("\r\n\r\n", disks.Select(Report.Text).ToArray()), System.Text.Encoding.UTF8);
                else File.WriteAllText(dlg.FileName, Report.Html(disks), System.Text.Encoding.UTF8);
                SetStatus("Rapporto salvato: " + dlg.FileName);
                // aperto da Esplora file, non da questo processo amministratore
                try { Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + dlg.FileName + "\""); } catch { }
            } catch (Exception e) { SetStatus("Salvataggio non riuscito: " + e.Message); }
        }
    }

    void ToggleMonitor() {
        if (monitorBusy) return;
        bool on = !monitorOn;
        if (on && !Setup.Installed) {
            MessageBox.Show(this, "Il monitoraggio continuo funziona con DiskVital installato in Programmi.\n\nInstallalo dalla Erodyne Suite oppure aprendo DiskVital-Setup.", "DiskVital", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        monitorBusy = true; side.Invalidate();
        SetStatus(on ? "Attivazione del monitoraggio…" : "Disattivazione del monitoraggio…");
        ThreadPool.QueueUserWorkItem(_ => {
            string err = Setup.SetMonitor(on);
            bool now = Setup.MonitorScheduled();
            Ui(() => {
                monitorOn = now; monitorBusy = false;
                SetStatus(err ?? (now ? "Monitoraggio attivo: DiskVital controlla i dischi ogni 10 minuti e ti avvisa se qualcosa peggiora" : "Monitoraggio disattivato"));
                side.Invalidate();
            });
        });
    }

    // ---------- barra del titolo ----------
    void PaintTitle(object o, PaintEventArgs e) {
        var g = e.Graphics; Th.Hq(g);
        Th.Logo(g, new RectangleF(16, 11, 24, 24));
        float w;
        using (var f = Th.F(10.5f, FontStyle.Bold)) { Th.Text_(g, "DiskVital", f, Th.Text, new RectangleF(48, 0, 100, TitleH)); w = g.MeasureString("DiskVital", f).Width; }
        using (var f = Th.F(10.5f)) Th.Text_(g, "by Erodyne", f, Th.Dim, new RectangleF(52 + w, 0, 120, TitleH));
        using (var p = new Pen(Th.Border)) g.DrawLine(p, 0, TitleH - 1, title.Width, TitleH - 1);
    }

    // ---------- barra laterale ----------
    int sideHover = -99;
    const int ItemH = 64, ItemTop = 56;
    Rectangle ItemRect(int i) { return new Rectangle(12, ItemTop + i * (ItemH + 4), SideW - 24, ItemH); }
    Rectangle MonitorRect { get { return new Rectangle(12, side.Height - 168, SideW - 24, 52); } }
    Rectangle ExportRect { get { return new Rectangle(12, side.Height - 110, SideW - 24, 34); } }
    Rectangle RefreshRect { get { return new Rectangle(12, side.Height - 74, SideW - 24, 34); } }
    int SideHit(Point p) {
        for (int i = 0; i < disks.Count; i++) { var r = ItemRect(i); if (r.Bottom > MonitorRect.Top - 8) break; if (r.Contains(p)) return i; }
        if (MonitorRect.Contains(p)) return -3;
        if (ExportRect.Contains(p)) return -4;
        if (RefreshRect.Contains(p)) return -2;
        return -99;
    }
    void SideClick(int h) {
        if (h >= 0) Select(h);
        else if (h == -2) Reload();
        else if (h == -3) ToggleMonitor();
        else if (h == -4) Export();
    }

    void PaintSide(object o, PaintEventArgs e) {
        var g = e.Graphics; Th.Hq(g);
        using (var p = new Pen(Th.Border)) g.DrawLine(p, SideW - 1, 0, SideW - 1, side.Height);
        using (var f = Th.F(8f, FontStyle.Bold)) Th.Text_(g, "DISCHI" + (disks.Count > 0 ? "  ·  " + disks.Count : ""), f, Th.Faint, new RectangleF(24, 20, 200, 24));
        if (loading && disks.Count == 0) using (var f = Th.F(9f)) Th.Text_(g, "Lettura in corso…", f, Th.Dim, new RectangleF(24, ItemTop + 8, 200, 24));
        for (int i = 0; i < disks.Count; i++) {
            var r = ItemRect(i);
            if (r.Bottom > MonitorRect.Top - 8) break;
            var d = disks[i];
            bool s = i == sel, hov = sideHover == i;
            if (s || hov) using (var p = Th.Round(r, 12)) using (var b = new SolidBrush(s ? Th.Surface2 : Th.Surface)) g.FillPath(b, p);
            if (s) { var ar = new RectangleF(r.X, r.Y + 18, 4, r.Height - 36); using (var p = Th.Round(ar, 2)) using (var b = Th.Grad(ar, Th.C1, Th.C2)) g.FillPath(b, p); }
            var tile = new RectangleF(r.X + 14, r.Y + 12, 40, 40);
            var hg = Th.HealthGrad(d.Health);
            using (var p = Th.Round(tile, 11)) using (var b = Th.Grad(tile, hg[0], hg[1])) g.FillPath(b, p);
            using (var f = Th.F(d.Temp.HasValue ? 10.5f : 12f, FontStyle.Bold)) Th.Text_(g, d.Temp.HasValue ? d.Temp + "°" : (d.Health == Health.Unknown ? "?" : "✓"), f, Color.White, tile, StringAlignment.Center);
            using (var f = Th.F(10f, FontStyle.Bold)) Th.Text_(g, d.Model.Length > 0 ? d.Model : "Disco " + d.Number, f, Th.Text, new RectangleF(r.X + 66, r.Y + 11, r.Width - 76, 22));
            using (var b = new SolidBrush(Th.HealthColor(d.Health))) g.FillEllipse(b, r.X + 66, r.Y + 39, 7, 7);
            var sub = Fmt.HealthText(d.Health) + (d.Size > 0 ? " · " + Fmt.Bytes(d.Size) : "") + (d.Volumes.Count > 0 ? " · " + d.Letters : "");
            using (var f = Th.F(8.5f)) Th.Text_(g, sub, f, Th.Dim, new RectangleF(r.X + 78, r.Y + 32, r.Width - 88, 20));
        }

        // monitoraggio
        var mr = MonitorRect;
        using (var p = Th.Round(mr, 12)) using (var b = new SolidBrush(sideHover == -3 ? Th.Surface2 : Th.Surface)) g.FillPath(b, p);
        using (var f = Th.F(9.5f, FontStyle.Bold)) Th.Text_(g, "Monitoraggio", f, Th.Text, new RectangleF(mr.X + 14, mr.Y + 7, 160, 20));
        using (var f = Th.F(8f)) Th.Text_(g, monitorBusy ? "attendi…" : monitorOn ? "Attivo · avvisa se peggiora" : "Avvisi in background", f, Th.Dim, new RectangleF(mr.X + 14, mr.Y + 26, mr.Width - 74, 18));
        var sw = new RectangleF(mr.Right - 54, mr.Y + 16, 40, 22);
        using (var p = Th.Round(sw, 11)) {
            if (monitorOn) using (var b = Th.Grad(sw, Th.C1, Th.C2)) g.FillPath(b, p);
            else using (var b = new SolidBrush(Th.Surface3)) g.FillPath(b, p);
        }
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, monitorOn ? sw.Right - 19 : sw.X + 3, sw.Y + 3, 16, 16);

        foreach (var it in new[] { new { R = ExportRect, H = -4, T = "⇩  Esporta rapporto" }, new { R = RefreshRect, H = -2, T = "↻  Aggiorna  (F5)" } }) {
            if (sideHover == it.H) using (var p = Th.Round(it.R, 10)) using (var b = new SolidBrush(Th.Surface)) g.FillPath(b, p);
            using (var f = Th.F(9f, FontStyle.Bold)) Th.Text_(g, it.T, f, sideHover == it.H ? Th.Text : Th.Dim, new RectangleF(it.R.X + 12, it.R.Y, it.R.Width, it.R.Height));
        }
        using (var f = Th.F(8f)) Th.Text_(g, "DiskVital " + Program.Version, f, Th.Faint, new RectangleF(24, side.Height - 34, 200, 24));
    }

    // ---------- barra di stato ----------
    void PaintBar(object o, PaintEventArgs e) {
        var g = e.Graphics; Th.Hq(g);
        using (var p = new Pen(Th.Border)) g.DrawLine(p, 0, 0, bar.Width, 0);
        using (var f = Th.F(8.5f)) Th.Text_(g, status, f, Th.Dim, new RectangleF(20, 0, bar.Width - 260, BarH));
        if (loading) {
            var r = new RectangleF(bar.Width - 220, BarH / 2f - 3, 200, 6);
            using (var p = Th.Round(r, 3)) using (var b = new SolidBrush(Th.Surface3)) g.FillPath(b, p);
            var rr = new RectangleF(r.X, r.Y, r.Width * 0.35f, r.Height);
            using (var p = Th.Round(rr, 3)) using (var b = Th.Grad(rr, Th.C1, Th.C2)) g.FillPath(b, p);
        }
    }

    // ---------- contenuto ----------
    RectangleF hero, tabsR, panel, copyR, pdfR, jpgR;
    RectangleF[] tabR = new RectangleF[3];
    readonly List<RectangleF> rowR = new List<RectangleF>();
    RectangleF listArea;

    void Layout_() {
        float W = content.Width, H = content.Height;
        bool small = H < 600;
        hero = new RectangleF(M, 22, W - 2 * M, small ? 168 : 188);
        float tilesTop = hero.Bottom + 16, tileH = small ? 72 : 82;
        tilesR = new RectangleF(M, tilesTop, W - 2 * M, tileH);
        tabsR = new RectangleF(M, tilesR.Bottom + 18, W - 2 * M, 34);
        panel = new RectangleF(M, tabsR.Bottom + 10, W - 2 * M, Math.Max(80, H - tabsR.Bottom - 10 - 20));
    }
    RectangleF tilesR;

    void PaintContent(object o, PaintEventArgs e) {
        var g = e.Graphics; Th.Hq(g);
        Layout_();
        var d = Cur;
        if (d == null) {
            var r = new RectangleF(0, 0, content.Width, content.Height);
            Th.Logo(g, new RectangleF(r.Width / 2 - 36, r.Height / 2 - 90, 72, 72));
            using (var f = Th.F(13f, FontStyle.Bold)) Th.Text_(g, loading ? "Lettura dei dischi in corso…" : "Nessun disco trovato", f, Th.Text, new RectangleF(0, r.Height / 2 - 4, r.Width, 30), StringAlignment.Center);
            if (!loading) using (var f = Th.F(9.5f)) Th.Text_(g, "Avvia DiskVital come amministratore e premi Aggiorna.", f, Th.Dim, new RectangleF(0, r.Height / 2 + 28, r.Width, 24), StringAlignment.Center);
            return;
        }
        PaintHero(g, d);
        PaintTiles(g, d);
        PaintTabs(g);
        using (var p = Th.Round(panel, 18)) {
            using (var b = new SolidBrush(Th.Surface)) g.FillPath(b, p);
            using (var pen = new Pen(Th.Border)) g.DrawPath(pen, p);
        }
        var st = g.Save();
        g.SetClip(new RectangleF(panel.X + 1, panel.Y + 1, panel.Width - 2, panel.Height - 2));
        if (tab == 0) PaintAttrs(g, d); else if (tab == 1) PaintHistory(g, d); else PaintDetails(g, d);
        g.Restore(st);
    }

    static void Banner(Graphics g, RectangleF r, Color c1, Color c2) {
        if (r.Width < 20 || r.Height < 20) return;
        using (var p = Th.Round(r, 20)) {
            using (var b = Th.Grad(r, c1, c2)) g.FillPath(b, p);
            var clip = g.Clip; g.SetClip(p, CombineMode.Intersect);
            using (var b = new SolidBrush(Color.FromArgb(30, 255, 255, 255))) {
                g.FillEllipse(b, r.Right - r.Height * 1.1f, r.Y - r.Height * 0.55f, r.Height * 1.3f, r.Height * 1.3f);
                g.FillEllipse(b, r.Right - r.Height * 0.6f, r.Y + r.Height * 0.35f, r.Height * 0.9f, r.Height * 0.9f);
            }
            var lr = new RectangleF(r.X, r.Y, r.Width * 0.75f, r.Height);
            using (var b = new LinearGradientBrush(new RectangleF(lr.X - 1, lr.Y, lr.Width + 2, lr.Height), Color.FromArgb(150, 11, 13, 18), Color.FromArgb(0, 11, 13, 18), 0f)) g.FillRectangle(b, lr);
            g.Clip = clip;
        }
    }

    void PaintHero(Graphics g, DiskInfo d) {
        var r = hero;
        var hg = Th.HealthGrad(d.Health);
        Banner(g, r, hg[0], hg[1]);
        bool small = r.Height < 180;
        float gx = r.Right - 28, ring = small ? 104 : 118;
        // anelli a destra: temperatura e vita residua
        int rings = 0;
        if (d.LifePercent.HasValue) { gx -= ring; Ring(g, new RectangleF(gx, r.Y + (r.Height - ring) / 2 - 6, ring, ring), d.LifePercent.Value / 100f, d.LifePercent + "%", "vita residua"); gx -= 18; rings++; }
        if (d.Temp.HasValue) { gx -= ring; Ring(g, new RectangleF(gx, r.Y + (r.Height - ring) / 2 - 6, ring, ring), Math.Min(1f, d.Temp.Value / (float)Math.Max(60, d.TempCrit + 10)), d.Temp + "°C", d.TempHigh ? "troppo caldo" : "temperatura"); gx -= 18; rings++; }
        float tw = gx - r.X - 40;
        float y = r.Y + (small ? 18 : 24);
        using (var f = Th.F(8.5f, FontStyle.Bold)) Th.Text_(g, "STATO DI SALUTE", f, Color.FromArgb(215, 255, 255, 255), new RectangleF(r.X + 32, y, 300, 18));
        using (var f = Th.F(small ? 24f : 28f, FontStyle.Bold)) Th.Text_(g, Fmt.HealthText(d.Health), f, Color.White, new RectangleF(r.X + 30, y + 16, tw, small ? 42 : 48));
        y += small ? 58 : 66;
        using (var f = Th.F(11f, FontStyle.Bold)) Th.Text_(g, d.Model, f, Color.White, new RectangleF(r.X + 32, y, tw, 22));
        y += 28;
        float x = r.X + 32, pw;
        foreach (var t in new[] { d.Size > 0 ? Fmt.Bytes(d.Size) : null, d.Kind, d.Interface, d.Volumes.Count > 0 ? d.Letters : null }) {
            if (string.IsNullOrEmpty(t)) continue;
            if (x + 60 > r.X + 32 + tw) break;
            Th.Pill(g, t, Color.White, new PointF(x, y), out pw, r.X + 32 + tw - x); x += pw + 8;
        }
        y += 32;
        string why = d.Health != Health.Good && d.Reasons.Count > 0 ? Cap(string.Join(", ", d.Reasons.Take(3).ToArray())) + ". " : "";
        string msg = d.Health == Health.Unknown ? (why.Length > 0 ? why + "Capacità e volumi restano visibili." : Fmt.Advice(d)) : why + Fmt.Advice(d);
        using (var f = Th.F(9.5f)) Th.Text_(g, msg, f, Color.FromArgb(230, 255, 255, 255), new RectangleF(r.X + 32, y, tw, r.Bottom - y - 8), StringAlignment.Near, StringAlignment.Near, true);
    }
    static string Cap(string s) { return string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1); }

    static void Ring(Graphics g, RectangleF r, float v, string big, string small) {
        var rr = new RectangleF(r.X + 6, r.Y + 6, r.Width - 12, r.Height - 12);
        using (var pen = new Pen(Color.FromArgb(55, 255, 255, 255), 9f)) g.DrawEllipse(pen, rr);
        if (v > 0.005f) using (var pen = new Pen(Color.White, 9f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, rr, -90, 360 * Math.Min(1f, v));
        using (var f = Th.F(r.Width > 110 ? 17f : 15f, FontStyle.Bold)) Th.Text_(g, big, f, Color.White, new RectangleF(r.X, r.Y + r.Height / 2 - 20, r.Width, 28), StringAlignment.Center);
        using (var f = Th.F(7.5f)) Th.Text_(g, small, f, Color.FromArgb(220, 255, 255, 255), new RectangleF(r.X, r.Y + r.Height / 2 + 6, r.Width, 16), StringAlignment.Center);
    }

    class Tile { public string L, V, S; public Color C; }
    List<Tile> Tiles(DiskInfo d) {
        var t = new List<Tile>();
        if (d.Temp.HasValue) t.Add(new Tile { L = "Temperatura", V = d.Temp + " °C", S = "avviso da " + d.TempWarn + " °C", C = Th.TempColor(d) });
        if (d.PowerOnHours.HasValue) t.Add(new Tile { L = "Ore di accensione", V = Fmt.N(d.PowerOnHours.Value), S = Fmt.Hours(d.PowerOnHours.Value), C = Th.A2 });
        if (d.PowerCycles.HasValue) t.Add(new Tile { L = "Accensioni", V = Fmt.N(d.PowerCycles.Value), S = d.UnsafeShutdowns.HasValue ? Fmt.N(d.UnsafeShutdowns.Value) + " improvvise" : "volte", C = Th.A1 });
        if (d.WrittenGB.HasValue) {
            double perDay = d.PowerOnHours.HasValue && d.PowerOnHours.Value > 48 ? d.WrittenGB.Value / (d.PowerOnHours.Value / 24.0) : -1;
            t.Add(new Tile { L = "Dati scritti", V = Fmt.Bytes(d.WrittenGB.Value * 1e9), S = perDay >= 0 ? "~" + Fmt.Bytes(perDay * 1e9) + " al giorno" : "dalla prima accensione", C = Th.C1 });
        }
        if (d.IsNvme && d.SmartRead) t.Add(new Tile { L = "Errori di integrità", V = Fmt.N(d.MediaErrors ?? 0), S = (d.MediaErrors ?? 0) == 0 ? "nessun dato perso" : "dati non recuperati", C = (d.MediaErrors ?? 0) > 0 ? Th.Amber : Th.Green });
        else if (d.SmartRead && (d.Reallocated.HasValue || d.Pending.HasValue || d.Uncorrectable.HasValue))
            t.Add(new Tile { L = d.IsSsd ? "Blocchi difettosi" : "Settori difettosi", V = Fmt.N(d.ProblemSectors), S = d.ProblemSectors == 0 ? "nessuno" : (d.Reallocated ?? 0) + " riallocati · " + (d.Pending ?? 0) + " in attesa", C = d.ProblemSectors > 0 ? Th.Amber : Th.Green });
        var used = d.Volumes.Where(v => v.Total > 0).ToList();
        if (used.Count > 0) {
            long tot = used.Sum(v => v.Total), free = used.Sum(v => v.Free);
            t.Add(new Tile { L = "Spazio occupato", V = (int)Math.Round(100.0 * (tot - free) / tot) + " %", S = Fmt.Bytes(free) + " liberi", C = (tot - free) * 10 > tot * 9 ? Th.Amber : Th.A2 });
        }
        if (!d.SmartRead) t.Add(new Tile { L = "Dati S.M.A.R.T.", V = "Non disponibili", S = "per questo disco", C = Th.Faint });
        return t.Take(5).ToList();
    }

    void PaintTiles(Graphics g, DiskInfo d) {
        var tl = Tiles(d);
        if (tl.Count == 0) return;
        int n = Math.Max(4, tl.Count);
        float gap = 14, w = (tilesR.Width - gap * (n - 1)) / n;
        for (int i = 0; i < tl.Count; i++) {
            var r = new RectangleF(tilesR.X + i * (w + gap), tilesR.Y, w, tilesR.Height);
            using (var p = Th.Round(r, 16)) {
                using (var b = new SolidBrush(Th.Surface)) g.FillPath(b, p);
                using (var pen = new Pen(Th.Border)) g.DrawPath(pen, p);
            }
            var t = tl[i];
            using (var b = new SolidBrush(t.C)) g.FillEllipse(b, r.X + 16, r.Y + 17, 8, 8);
            using (var f = Th.F(8.5f)) Th.Text_(g, t.L, f, Th.Dim, new RectangleF(r.X + 30, r.Y + 11, r.Width - 40, 20));
            using (var f = Th.F(r.Height < 80 ? 13f : 15f, FontStyle.Bold)) Th.Text_(g, t.V, f, Th.Text, new RectangleF(r.X + 14, r.Y + (r.Height < 80 ? 28 : 32), r.Width - 24, 28));
            using (var f = Th.F(8f)) Th.Text_(g, t.S, f, Th.Faint, new RectangleF(r.X + 16, r.Bottom - 22, r.Width - 26, 18));
        }
    }

    static readonly string[] TabNames = { "Attributi S.M.A.R.T.", "Storico", "Dettagli" };
    void PaintTabs(Graphics g) {
        float x = tabsR.X;
        using (var f = Th.F(9.5f, FontStyle.Bold))
            for (int i = 0; i < 3; i++) {
                float w = g.MeasureString(TabNames[i], f).Width + 34;
                var r = new RectangleF(x, tabsR.Y, w, tabsR.Height);
                tabR[i] = r;
                using (var p = Th.Round(r, 10)) {
                    if (tab == i) using (var b = Th.Grad(r, Th.C1, Th.C2)) g.FillPath(b, p);
                    else using (var b = new SolidBrush(Th.Surface)) g.FillPath(b, p);
                }
                Th.Text_(g, TabNames[i], f, tab == i ? Color.White : Th.Dim, r, StringAlignment.Center);
                x = r.Right + 8;
            }
        using (var f = Th.F(9f, FontStyle.Bold)) {
            float rx = tabsR.Right;
            Func<string, bool, RectangleF> btn = (t, primary) => {
                float w = g.MeasureString(t, f).Width + (primary ? 30 : 20);
                var r = new RectangleF(rx - w, tabsR.Y, w, tabsR.Height);
                if (primary) using (var p = Th.Round(r, 10)) using (var b = new SolidBrush(Th.Surface)) g.FillPath(b, p);
                Th.Text_(g, t, f, primary ? Th.Text : Th.Dim, r, StringAlignment.Center);
                rx = r.Left - 8;
                return r;
            };
            copyR = btn("⧉  Copia", false);
            jpgR = btn("Salva in JPEG", true);
            pdfR = btn("Salva in PDF", true);
        }
    }

    // ---------- scheda: attributi ----------
    const int RowH = 30;
    void PaintAttrs(Graphics g, DiskInfo d) {
        rowR.Clear();
        var p = panel;
        if (d.Attrs.Count == 0) {
            using (var f = Th.F(10f)) Th.Text_(g, d.SmartNote ?? "Nessun attributo disponibile per questo disco.", f, Th.Dim, new RectangleF(p.X + 24, p.Y + 20, p.Width - 48, 40), StringAlignment.Near, StringAlignment.Near, true);
            return;
        }
        float helpH = p.Height > 240 ? 72 : 0;
        float x = p.X + 18, w = p.Width - 36;
        bool nv = d.IsNvme;
        // colonne
        float cId = x + 22, cName = x + 62, cRaw = x + w - 220, cThr = cRaw - 70, cWorst = cThr - 74, cCur = cWorst - 70;
        if (nv) cRaw = x + w - 280;
        float hy = p.Y + 12;
        using (var f = Th.F(8f, FontStyle.Bold)) {
            Th.Text_(g, "ID", f, Th.Faint, new RectangleF(cId, hy, 40, 20));
            Th.Text_(g, "ATTRIBUTO", f, Th.Faint, new RectangleF(cName, hy, 300, 20));
            if (!nv) {
                Th.Text_(g, "ATTUALE", f, Th.Faint, new RectangleF(cCur, hy, 64, 20), StringAlignment.Far);
                Th.Text_(g, "PEGGIORE", f, Th.Faint, new RectangleF(cWorst, hy, 68, 20), StringAlignment.Far);
                Th.Text_(g, "SOGLIA", f, Th.Faint, new RectangleF(cThr, hy, 64, 20), StringAlignment.Far);
            }
            Th.Text_(g, "VALORE", f, Th.Faint, new RectangleF(cRaw, hy, x + w - cRaw - 8, 20), StringAlignment.Far);
        }
        var list = new RectangleF(p.X, p.Y + 38, p.Width, p.Height - 38 - helpH);
        listArea = list;
        scrollMax[0] = (int)Math.Max(0, d.Attrs.Count * RowH - list.Height + 8);
        scroll[0] = Math.Min(scroll[0], scrollMax[0]);
        var st = g.Save();
        g.IntersectClip(list);
        using (var fn = Th.F(9.5f)) using (var fb = Th.F(9.5f, FontStyle.Bold)) using (var fm = Th.F(9f))
            for (int i = 0; i < d.Attrs.Count; i++) {
                var a = d.Attrs[i];
                var r = new RectangleF(x, list.Y + i * RowH - scroll[0], w, RowH - 2);
                rowR.Add(r);
                if (r.Bottom < list.Y || r.Y > list.Bottom) continue;
                if (i == attrSel) using (var pp = Th.Round(r, 8)) using (var b = new SolidBrush(Th.Surface2)) g.FillPath(b, pp);
                else if (i % 2 == 1) using (var pp = Th.Round(r, 8)) using (var b = new SolidBrush(Color.FromArgb(8, 255, 255, 255))) g.FillPath(b, pp);
                using (var b = new SolidBrush(Th.HealthColor(a.State))) g.FillEllipse(b, x + 8, r.Y + RowH / 2f - 5, 8, 8);
                Th.Text_(g, a.Id.ToString("X2"), fm, Th.Faint, new RectangleF(cId, r.Y, 40, r.Height));
                Th.Text_(g, a.Name, a.State != Health.Good ? fb : fn, a.State == Health.Good ? Th.Text : Th.HealthColor(a.State), new RectangleF(cName, r.Y, (nv ? cRaw : cCur) - cName - 8, r.Height));
                if (!nv) {
                    Th.Text_(g, a.Current.ToString(), fn, Th.Text, new RectangleF(cCur, r.Y, 64, r.Height), StringAlignment.Far);
                    Th.Text_(g, a.Worst.ToString(), fn, Th.Dim, new RectangleF(cWorst, r.Y, 68, r.Height), StringAlignment.Far);
                    Th.Text_(g, a.Threshold.ToString(), fn, Th.Dim, new RectangleF(cThr, r.Y, 64, r.Height), StringAlignment.Far);
                }
                Th.Text_(g, a.Shown ?? a.Raw.ToString(), fn, Th.Text, new RectangleF(cRaw, r.Y, x + w - cRaw - 8, r.Height), StringAlignment.Far);
            }
        g.Restore(st);
        ScrollBar(g, list, 0, d.Attrs.Count * RowH);
        if (helpH > 0 && attrSel < d.Attrs.Count) {
            var a = d.Attrs[attrSel];
            var hr = new RectangleF(p.X + 14, p.Bottom - helpH - 8, p.Width - 28, helpH);
            using (var pp = Th.Round(hr, 12)) using (var b = new SolidBrush(Th.Surface2)) g.FillPath(b, pp);
            using (var f = Th.F(9.5f, FontStyle.Bold)) Th.Text_(g, a.Id.ToString("X2") + "  ·  " + a.Name, f, Th.Text, new RectangleF(hr.X + 16, hr.Y + 8, hr.Width - 32, 20));
            string help = nv ? (a.Id - 1 < SmartDb.NvmeRows.Length ? SmartDb.NvmeRows[a.Id - 1][1] : "") : SmartDb.Help(a.Id);
            if (!nv && a.Threshold > 0) help += "  Valore attuale " + a.Current + ", il produttore lo considera guasto a " + a.Threshold + " o meno.";
            using (var f = Th.F(9f)) Th.Text_(g, help, f, Th.Dim, new RectangleF(hr.X + 16, hr.Y + 28, hr.Width - 32, hr.Height - 32), StringAlignment.Near, StringAlignment.Near, true);
        }
    }

    void ScrollBar(Graphics g, RectangleF area, int t, float total) {
        if (scrollMax[t] <= 0) return;
        float h = Math.Max(30, area.Height * area.Height / total);
        float y = area.Y + (area.Height - h) * scroll[t] / scrollMax[t];
        var r = new RectangleF(area.Right - 9, y + 2, 4, h - 4);
        using (var p = Th.Round(r, 2)) using (var b = new SolidBrush(Th.Surface3)) g.FillPath(b, p);
    }

    // ---------- scheda: storico ----------
    void PaintHistory(Graphics g, DiskInfo d) {
        var p = panel;
        var pts = hist.Where(s => s.Temp > 0).ToList();
        float x = p.X + 24, w = p.Width - 48;
        if (hist.Count < 2) {
            using (var f = Th.F(11f, FontStyle.Bold)) Th.Text_(g, "Lo storico si sta riempiendo", f, Th.Text, new RectangleF(x, p.Y + 22, w, 24));
            using (var f = Th.F(9.5f)) Th.Text_(g, "DiskVital salva ogni lettura (al massimo ogni mezz'ora, subito se qualcosa cambia): qui vedrai l'andamento della temperatura e il momento esatto in cui un contatore pericoloso ha iniziato a salire.\n\nAttiva il Monitoraggio nella barra a sinistra per registrare i dati anche quando DiskVital è chiuso e ricevere un avviso se un disco peggiora.", f, Th.Dim, new RectangleF(x, p.Y + 54, Math.Min(w, 720), p.Height - 70), StringAlignment.Near, StringAlignment.Near, true);
            scrollMax[1] = 0;
            return;
        }
        float chartH = Math.Min(220, Math.Max(110, p.Height * 0.5f));
        var first = hist[0];
        using (var f = Th.F(10.5f, FontStyle.Bold)) Th.Text_(g, "Temperatura", f, Th.Text, new RectangleF(x, p.Y + 14, 300, 22));
        using (var f = Th.F(8.5f)) Th.Text_(g, hist.Count + " letture dal " + first.T.ToString("dd/MM/yyyy") + (pts.Count > 0 ? " · min " + pts.Min(s => s.Temp) + " °C · max " + pts.Max(s => s.Temp) + " °C · media " + (int)Math.Round(pts.Average(s => s.Temp)) + " °C" : ""), f, Th.Dim, new RectangleF(x + 110, p.Y + 14, w - 110, 22), StringAlignment.Far);
        var cr = new RectangleF(x + 36, p.Y + 46, w - 40, chartH - 56);
        if (pts.Count >= 2) Chart(g, cr, pts, d);
        else using (var f = Th.F(9f)) Th.Text_(g, "Questo disco non comunica la temperatura.", f, Th.Faint, cr, StringAlignment.Center);

        float ey = p.Y + chartH + 4;
        using (var f = Th.F(10.5f, FontStyle.Bold)) Th.Text_(g, "Eventi", f, Th.Text, new RectangleF(x, ey, 300, 22));
        var evs = History.Events(hist);
        var area = new RectangleF(p.X, ey + 28, p.Width, p.Bottom - ey - 36);
        if (evs.Count == 0) {
            using (var f = Th.F(9.5f)) Th.Text_(g, "Nessun peggioramento registrato: i contatori dei settori difettosi e degli errori non sono mai aumentati.", f, Th.Dim, new RectangleF(x, area.Y, w, 40), StringAlignment.Near, StringAlignment.Near, true);
            scrollMax[1] = 0;
            return;
        }
        scrollMax[1] = (int)Math.Max(0, evs.Count * 28 - area.Height);
        scroll[1] = Math.Min(scroll[1], scrollMax[1]);
        var st = g.Save(); g.IntersectClip(area);
        using (var fd = Th.F(9f)) using (var ft = Th.F(9.5f))
            for (int i = 0; i < evs.Count; i++) {
                float y = area.Y + i * 28 - scroll[1];
                if (y + 28 < area.Y || y > area.Bottom) continue;
                bool good = evs[i].Value.StartsWith("Stato tornato");
                using (var b = new SolidBrush(good ? Th.Green : Th.Amber)) g.FillEllipse(b, x + 2, y + 10, 7, 7);
                Th.Text_(g, evs[i].Key.ToString("dd/MM/yyyy HH:mm"), fd, Th.Faint, new RectangleF(x + 18, y, 130, 26));
                Th.Text_(g, evs[i].Value, ft, Th.Text, new RectangleF(x + 150, y, w - 150, 26));
            }
        g.Restore(st);
        ScrollBar(g, area, 1, evs.Count * 28);
    }

    static void Chart(Graphics g, RectangleF r, List<Snap> pts, DiskInfo d) {
        // al massimo un punto per pixel, con la media dei valori
        var t0 = pts[0].T; var t1 = pts[pts.Count - 1].T;
        double span = Math.Max(1, (t1 - t0).TotalSeconds);
        int lo = Math.Min(pts.Min(s => s.Temp), 20), hi = Math.Max(pts.Max(s => s.Temp), Math.Min(d.TempWarn, 90));
        lo = lo / 10 * 10; hi = (hi + 9) / 10 * 10; if (hi - lo < 20) hi = lo + 20;
        using (var f = Th.F(7.5f)) using (var pen = new Pen(Th.Border) { DashStyle = DashStyle.Dot })
            for (int v = lo; v <= hi; v += 10) {
                float y = r.Bottom - (v - lo) * r.Height / (hi - lo);
                g.DrawLine(pen, r.X, y, r.Right, y);
                Th.Text_(g, v + "°", f, Th.Faint, new RectangleF(r.X - 36, y - 8, 30, 16), StringAlignment.Far);
            }
        if (d.TempWarn > lo && d.TempWarn < hi) {
            float y = r.Bottom - (d.TempWarn - lo) * r.Height / (hi - lo);
            using (var pen = new Pen(Color.FromArgb(140, Th.Amber), 1f) { DashStyle = DashStyle.Dash }) g.DrawLine(pen, r.X, y, r.Right, y);
        }
        var buckets = new SortedDictionary<int, List<int>>();
        foreach (var s in pts) {
            int px = (int)((s.T - t0).TotalSeconds / span * (r.Width - 1));
            List<int> l; if (!buckets.TryGetValue(px, out l)) buckets[px] = l = new List<int>();
            l.Add(s.Temp);
        }
        var line = buckets.Select(kv => new PointF(r.X + kv.Key, r.Bottom - (float)(kv.Value.Average() - lo) * r.Height / (hi - lo))).ToArray();
        if (line.Length >= 2) {
            using (var path = new GraphicsPath()) {
                path.AddLines(line);
                path.AddLine(line[line.Length - 1], new PointF(line[line.Length - 1].X, r.Bottom));
                path.AddLine(new PointF(line[0].X, r.Bottom), line[0]);
                using (var b = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Color.FromArgb(70, Th.C1), Color.FromArgb(0, Th.C1), 90f)) g.FillPath(b, path);
            }
            using (var b = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y, r.Width + 2, r.Height), Th.C1, Th.C2, 0f))
            using (var pen = new Pen(b, 2f) { LineJoin = LineJoin.Round }) g.DrawLines(pen, line);
        }
        using (var f = Th.F(7.5f)) {
            Th.Text_(g, t0.ToString("dd/MM HH:mm"), f, Th.Faint, new RectangleF(r.X, r.Bottom + 4, 120, 14));
            Th.Text_(g, t1.ToString("dd/MM HH:mm"), f, Th.Faint, new RectangleF(r.Right - 120, r.Bottom + 4, 120, 14), StringAlignment.Far);
        }
    }

    // ---------- scheda: dettagli ----------
    void PaintDetails(Graphics g, DiskInfo d) {
        var p = panel;
        float x = p.X + 24, w = p.Width - 48, y0 = p.Y + 18 - scroll[2], y = y0;
        var info = Report.Info(d).Where(kv => kv.Key != "Stato di salute" && kv.Key != "Funzioni" && kv.Key != "Volumi").ToList();
        float colW = w > 760 ? (w - 30) / 2 : w;
        int perCol = colW < w ? (info.Count + 1) / 2 : info.Count;
        using (var fk = Th.F(9f)) using (var fv = Th.F(9.5f, FontStyle.Bold))
            for (int i = 0; i < info.Count; i++) {
                int c = i / perCol, row = i % perCol;
                float cx = x + c * (colW + 30), cy = y + row * 28;
                Th.Text_(g, info[i].Key, fk, Th.Dim, new RectangleF(cx, cy, 170, 24));
                Th.Text_(g, info[i].Value, fv, Th.Text, new RectangleF(cx + 172, cy, colW - 172, 24));
            }
        y += perCol * 28 + 14;
        if (d.Features.Count > 0) {
            using (var f = Th.F(10.5f, FontStyle.Bold)) Th.Text_(g, "Funzioni supportate", f, Th.Text, new RectangleF(x, y, 300, 22));
            y += 30;
            float fx = x, pw;
            foreach (var ft in d.Features) {
                if (fx + 120 > x + w) { fx = x; y += 32; }
                Th.Pill(g, ft, Th.A2, new PointF(fx, y), out pw); fx += pw + 8;
            }
            y += 42;
        }
        if (d.Volumes.Count > 0) {
            using (var f = Th.F(10.5f, FontStyle.Bold)) Th.Text_(g, "Volumi", f, Th.Text, new RectangleF(x, y, 300, 22));
            y += 30;
            using (var fb = Th.F(9.5f, FontStyle.Bold)) using (var fn = Th.F(8.5f))
                foreach (var v in d.Volumes) {
                    Th.Text_(g, v.Letter, fb, Th.Text, new RectangleF(x, y, 40, 22));
                    Th.Text_(g, (string.IsNullOrEmpty(v.Label) ? "Disco locale" : v.Label) + (v.Fs != null ? " · " + v.Fs : ""), fn, Th.Dim, new RectangleF(x + 40, y, 260, 22));
                    if (v.Total > 0) {
                        float used = (float)(v.Total - v.Free) / v.Total;
                        var br = new RectangleF(x + 310, y + 8, Math.Max(80, w - 310 - 200), 7);
                        using (var pp = Th.Round(br, 3.5f)) using (var b = new SolidBrush(Th.Surface3)) g.FillPath(b, pp);
                        var fr = new RectangleF(br.X, br.Y, Math.Max(7, br.Width * used), br.Height);
                        using (var pp = Th.Round(fr, 3.5f)) using (var b = Th.Grad(fr, used > 0.9f ? Th.Amber : Th.C1, used > 0.9f ? Th.Red : Th.C2)) g.FillPath(b, pp);
                        Th.Text_(g, Fmt.Bytes(v.Free) + " liberi su " + Fmt.Bytes(v.Total), fn, Th.Dim, new RectangleF(br.Right + 12, y, 190, 22));
                    }
                    y += 30;
                }
        }
        float total = y - y0 + 10;
        scrollMax[2] = (int)Math.Max(0, total - p.Height);
        scroll[2] = Math.Min(scroll[2], scrollMax[2]);
        ScrollBar(g, p, 2, total);
    }

    // ---------- clic ----------
    bool ContentHot(Point pt) {
        if (Cur == null) return false;
        for (int i = 0; i < 3; i++) if (tabR[i].Contains(pt)) return true;
        if (copyR.Contains(pt) || pdfR.Contains(pt) || jpgR.Contains(pt)) return true;
        return tab == 0 && listArea.Contains(pt) && rowR.Any(r => r.Contains(pt));
    }
    void ContentClick(object s, MouseEventArgs e) {
        if (Cur == null) return;
        for (int i = 0; i < 3; i++) if (tabR[i].Contains(e.Location)) { tab = i; content.Invalidate(); return; }
        if (copyR.Contains(e.Location)) { CopyReport(); return; }
        if (pdfR.Contains(e.Location)) { SaveCertificate(true); return; }
        if (jpgR.Contains(e.Location)) { SaveCertificate(false); return; }
        if (tab == 0 && listArea.Contains(e.Location))
            for (int i = 0; i < rowR.Count; i++) if (rowR[i].Contains(e.Location)) { attrSel = i; content.Invalidate(); return; }
    }
}
