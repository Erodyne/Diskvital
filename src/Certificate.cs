using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

// Certificato di stato del disco: una pagina A4 (200 dpi) con identificazione, giudizio, valori principali,
// tutti gli attributi S.M.A.R.T., riepilogo dello storico e un ID documento. Salvato in JPEG o in PDF.
public static class Certificate {
    public const int W = 1654, H = 2339; // A4 a 200 dpi
    const int M = 110;
    static readonly Color Ink = Color.FromArgb(0x11, 0x18, 0x27), Dim = Color.FromArgb(0x6B, 0x72, 0x80),
        Line = Color.FromArgb(0xE5, 0xE7, 0xEB), Soft = Color.FromArgb(0xF4, 0xF5, 0xF9);

    static Color HC(Health h) {
        switch (h) {
            case Health.Good: return Color.FromArgb(0x05, 0x96, 0x69);
            case Health.Caution: return Color.FromArgb(0xD9, 0x77, 0x06);
            case Health.Bad: return Color.FromArgb(0xDC, 0x26, 0x26);
        }
        return Dim;
    }
    static Font P(float px, FontStyle st = FontStyle.Regular) { return new Font(Th.Family, px, st, GraphicsUnit.Pixel); }

    public static string DocumentId(DiskInfo d) {
        using (var sha = SHA256.Create()) {
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(Environment.MachineName + "|" + Report.Text(d)));
            var s = string.Concat(h.Take(8).Select(b => b.ToString("X2")).ToArray());
            return s.Substring(0, 4) + "-" + s.Substring(4, 4) + "-" + s.Substring(8, 4) + "-" + s.Substring(12, 4);
        }
    }

    public static Bitmap Render(DiskInfo d, List<Snap> hist) {
        var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb);
        bmp.SetResolution(200, 200);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.White);
            Draw(g, d, hist ?? new List<Snap>());
        }
        return bmp;
    }

    static void Draw(Graphics g, DiskInfo d, List<Snap> hist) {
        var now = DateTime.Now;
        // ---- intestazione ----
        var head = new RectangleF(0, 0, W, 300);
        using (var b = new LinearGradientBrush(head, Th.C1, Th.C2, 0f)) g.FillRectangle(b, head);
        using (var b = new SolidBrush(Color.FromArgb(28, 255, 255, 255))) { g.FillEllipse(b, W - 420, -220, 560, 560); g.FillEllipse(b, W - 200, 120, 360, 360); }
        Th.Logo(g, new RectangleF(M, 84, 132, 132), Color.FromArgb(70, 255, 255, 255), Color.FromArgb(40, 255, 255, 255));
        using (var f = P(26, FontStyle.Bold)) Th.Text_(g, "DISKVITAL BY ERODYNE", f, Color.FromArgb(225, 255, 255, 255), new RectangleF(M + 166, 80, 900, 36));
        using (var f = P(60, FontStyle.Bold)) Th.Text_(g, "Certificato di stato del disco", f, Color.White, new RectangleF(M + 160, 112, W - M - 280, 80));
        using (var f = P(25)) Th.Text_(g, "Emesso il " + now.ToString("dd/MM/yyyy") + " alle " + now.ToString("HH:mm") + "  ·  computer " + Environment.MachineName, f, Color.FromArgb(235, 255, 255, 255), new RectangleF(M + 166, 194, W - M - 290, 40));

        // ---- giudizio ----
        float y = 350;
        var card = new RectangleF(M, y, W - 2 * M, 280);
        using (var p = Th.Round(card, 30)) using (var b = new SolidBrush(Soft)) g.FillPath(b, p);
        var hc = HC(d.Health);
        using (var b = new SolidBrush(hc)) g.FillRectangle(b, card.X, card.Y + 40, 10, card.Height - 80);
        float ringW = 0;
        var rings = new List<KeyValuePair<float, string[]>>();
        if (d.LifePercent.HasValue) rings.Add(new KeyValuePair<float, string[]>(d.LifePercent.Value / 100f, new[] { d.LifePercent + "%", "vita residua" }));
        if (d.Temp.HasValue) rings.Add(new KeyValuePair<float, string[]>(Math.Min(1f, d.Temp.Value / (float)Math.Max(60, d.TempCrit + 10)), new[] { d.Temp + "°C", "temperatura" }));
        float rx = card.Right - 50;
        foreach (var r in rings) {
            rx -= 190;
            Ring(g, new RectangleF(rx, card.Y + 45, 190, 190), r.Key, r.Value[0], r.Value[1], r.Value[1] == "temperatura" ? TempC(d) : hc);
            rx -= 30; ringW += 220;
        }
        float tw = card.Width - 90 - ringW;
        using (var f = P(24, FontStyle.Bold)) Th.Text_(g, "STATO DI SALUTE", f, Dim, new RectangleF(card.X + 50, card.Y + 34, 500, 32));
        using (var f = P(78, FontStyle.Bold)) Th.Text_(g, Fmt.HealthText(d.Health), f, hc, new RectangleF(card.X + 44, card.Y + 62, tw, 100));
        string why = d.Health != Health.Good && d.Reasons.Count > 0 ? Cap(string.Join(", ", d.Reasons.ToArray())) + ". " : "";
        using (var f = P(25)) Th.Text_(g, why + Fmt.Advice(d), f, Ink, new RectangleF(card.X + 50, card.Y + 166, tw, 100), StringAlignment.Near, StringAlignment.Near, true);

        // ---- identificazione ----
        y = card.Bottom + 50;
        Section(g, "Identificazione del disco", y); y += 54;
        var info = Report.Info(d).Where(kv => kv.Key != "Stato di salute" && kv.Key != "Temperatura" && kv.Key != "Vita residua").ToList();
        int per = (info.Count + 1) / 2;
        float colW = (W - 2 * M - 60) / 2f, rowH = 40;
        using (var fk = P(22)) using (var fv = P(23, FontStyle.Bold))
            for (int i = 0; i < info.Count; i++) {
                float cx = M + (i / per) * (colW + 60), cy = y + (i % per) * rowH;
                Th.Text_(g, info[i].Key, fk, Dim, new RectangleF(cx, cy, 285, rowH));
                Th.Text_(g, info[i].Value, fv, Ink, new RectangleF(cx + 290, cy, colW - 290, rowH));
                using (var pen = new Pen(Line)) g.DrawLine(pen, cx, cy + rowH - 2, cx + colW, cy + rowH - 2);
            }
        y += per * rowH + 40;

        // ---- valori principali ----
        var tiles = new List<string[]>();
        if (d.Temp.HasValue) tiles.Add(new[] { "Temperatura", d.Temp + " °C", "avviso da " + d.TempWarn + " °C" });
        if (d.PowerOnHours.HasValue) tiles.Add(new[] { "Ore di accensione", Fmt.N(d.PowerOnHours.Value), Fmt.Hours(d.PowerOnHours.Value) });
        if (d.PowerCycles.HasValue) tiles.Add(new[] { "Accensioni", Fmt.N(d.PowerCycles.Value), d.UnsafeShutdowns.HasValue ? Fmt.N(d.UnsafeShutdowns.Value) + " improvvise" : "volte" });
        if (d.WrittenGB.HasValue) tiles.Add(new[] { "Dati scritti", Fmt.Bytes(d.WrittenGB.Value * 1e9), "dalla prima accensione" });
        if (d.IsNvme && d.SmartRead) tiles.Add(new[] { "Errori di integrità", Fmt.N(d.MediaErrors ?? 0), (d.MediaErrors ?? 0) == 0 ? "nessun dato perso" : "dati non recuperati" });
        else if (d.SmartRead) tiles.Add(new[] { d.IsSsd ? "Blocchi difettosi" : "Settori difettosi", Fmt.N(d.ProblemSectors), (d.Reallocated ?? 0) + " riallocati · " + (d.Pending ?? 0) + " in attesa" });
        if (tiles.Count > 0) {
            tiles = tiles.Take(5).ToList();
            float gap = 24, tw2 = (W - 2 * M - gap * (tiles.Count - 1)) / tiles.Count;
            for (int i = 0; i < tiles.Count; i++) {
                var r = new RectangleF(M + i * (tw2 + gap), y, tw2, 150);
                using (var p = Th.Round(r, 22)) { using (var b = new SolidBrush(Soft)) g.FillPath(b, p); }
                using (var f = P(21)) Th.Text_(g, tiles[i][0], f, Dim, new RectangleF(r.X + 24, r.Y + 18, r.Width - 40, 30));
                using (var f = P(40, FontStyle.Bold)) Th.Text_(g, tiles[i][1], f, Ink, new RectangleF(r.X + 20, r.Y + 50, r.Width - 34, 56));
                using (var f = P(19)) Th.Text_(g, tiles[i][2], f, Dim, new RectangleF(r.X + 24, r.Y + 106, r.Width - 40, 30));
            }
            y += 150 + 46;
        }

        // ---- attributi ----
        float footerTop = H - 190;
        string histLine = HistLine(hist);
        float histH = histLine != null ? 90 : 0;
        Section(g, d.IsNvme ? "Registro di salute NVMe" : "Attributi S.M.A.R.T.", y); y += 50;
        if (d.Attrs.Count == 0) {
            using (var f = P(24)) Th.Text_(g, d.SmartNote ?? "Il disco non fornisce dati S.M.A.R.T.", f, Dim, new RectangleF(M, y, W - 2 * M, 50));
            y += 60;
        } else {
            float avail = footerTop - histH - y - 20;
            float rh = Math.Max(22, Math.Min(40, avail / (d.Attrs.Count + 1)));
            float fs = Math.Max(15, Math.Min(22, rh * 0.56f));
            float x = M, w = W - 2 * M;
            bool nv = d.IsNvme;
            float cName = x + 90, cRaw = x + w - 360, cThr = cRaw - 110, cWorst = cThr - 120, cCur = cWorst - 120;
            using (var fh = P(fs * 0.85f, FontStyle.Bold)) {
                Th.Text_(g, "ID", fh, Dim, new RectangleF(x + 30, y, 60, rh));
                Th.Text_(g, "ATTRIBUTO", fh, Dim, new RectangleF(cName, y, 500, rh));
                if (!nv) {
                    Th.Text_(g, "ATTUALE", fh, Dim, new RectangleF(cCur, y, 110, rh), StringAlignment.Far);
                    Th.Text_(g, "PEGGIORE", fh, Dim, new RectangleF(cWorst, y, 115, rh), StringAlignment.Far);
                    Th.Text_(g, "SOGLIA", fh, Dim, new RectangleF(cThr, y, 105, rh), StringAlignment.Far);
                }
                Th.Text_(g, "VALORE", fh, Dim, new RectangleF(cRaw, y, x + w - cRaw - 10, rh), StringAlignment.Far);
            }
            y += rh;
            using (var fn = P(fs)) using (var fb = P(fs, FontStyle.Bold))
                for (int i = 0; i < d.Attrs.Count; i++) {
                    var a = d.Attrs[i];
                    var r = new RectangleF(x, y + i * rh, w, rh);
                    if (i % 2 == 0) using (var b = new SolidBrush(Soft)) g.FillRectangle(b, r);
                    var c = a.State == Health.Good ? Ink : HC(a.State);
                    using (var b = new SolidBrush(a.State == Health.Good ? Color.FromArgb(0x10, 0xB9, 0x81) : HC(a.State))) g.FillEllipse(b, x + 10, r.Y + rh / 2 - 5, 10, 10);
                    Th.Text_(g, a.Id.ToString("X2"), fn, Dim, new RectangleF(x + 30, r.Y, 60, rh));
                    Th.Text_(g, a.Name, a.State == Health.Good ? fn : fb, c, new RectangleF(cName, r.Y, (nv ? cRaw : cCur) - cName - 10, rh));
                    if (!nv) {
                        Th.Text_(g, a.Current.ToString(), fn, Ink, new RectangleF(cCur, r.Y, 110, rh), StringAlignment.Far);
                        Th.Text_(g, a.Worst.ToString(), fn, Dim, new RectangleF(cWorst, r.Y, 115, rh), StringAlignment.Far);
                        Th.Text_(g, a.Threshold.ToString(), fn, Dim, new RectangleF(cThr, r.Y, 105, rh), StringAlignment.Far);
                    }
                    Th.Text_(g, a.Shown ?? a.Raw.ToString(), fn, c, new RectangleF(cRaw, r.Y, x + w - cRaw - 10, rh), StringAlignment.Far);
                }
            y += d.Attrs.Count * rh + 20;
        }

        // ---- storico ----
        if (histLine != null) {
            using (var f = P(22, FontStyle.Bold)) Th.Text_(g, "Storico", f, Ink, new RectangleF(M, footerTop - histH, 300, 34));
            using (var f = P(21)) Th.Text_(g, histLine, f, Dim, new RectangleF(M, footerTop - histH + 32, W - 2 * M, 56), StringAlignment.Near, StringAlignment.Near, true);
        }

        // ---- piè di pagina ----
        using (var pen = new Pen(Line, 2)) g.DrawLine(pen, M, footerTop + 10, W - M, footerTop + 10);
        using (var f = P(20)) Th.Text_(g, "ID documento", f, Dim, new RectangleF(M, footerTop + 34, 300, 30));
        using (var f = new Font(FontFamily.GenericMonospace, 30, FontStyle.Bold, GraphicsUnit.Pixel)) Th.Text_(g, DocumentId(d), f, Ink, new RectangleF(M, footerTop + 62, 560, 44));
        using (var f = P(19)) Th.Text_(g,
            "Documento generato automaticamente da DiskVital " + Program.Version + " (Erodyne) con i valori comunicati dal disco al momento della lettura (" +
            d.ReadAt.ToString("dd/MM/yyyy HH:mm") + "). Attesta lo stato rilevato, non garantisce il funzionamento futuro del disco.",
            f, Dim, new RectangleF(M + 600, footerTop + 34, W - 2 * M - 600, 120), StringAlignment.Near, StringAlignment.Near, true);
        using (var b = new LinearGradientBrush(new RectangleF(0, H - 14, W, 14), Th.C1, Th.C2, 0f)) g.FillRectangle(b, 0, H - 14, W, 14);
    }

    static string HistLine(List<Snap> hist) {
        if (hist.Count < 2) return null;
        var t = hist.Where(s => s.Temp > 0).ToList();
        var ev = History.Events(hist);
        var s0 = "Monitorato dal " + hist[0].T.ToString("dd/MM/yyyy") + " con " + Fmt.N(hist.Count) + " letture";
        if (t.Count > 0) s0 += "; temperatura minima " + t.Min(x => x.Temp) + " °C, massima " + t.Max(x => x.Temp) + " °C, media " + (int)Math.Round(t.Average(x => x.Temp)) + " °C";
        s0 += ". " + (ev.Count == 0 ? "Nessun peggioramento registrato nel periodo." : "Eventi: " + string.Join("; ", ev.Take(3).Select(e => e.Key.ToString("dd/MM/yyyy") + " " + e.Value).ToArray()) + (ev.Count > 3 ? " (e altri " + (ev.Count - 3) + ")" : "") + ".");
        return s0;
    }

    static Color TempC(DiskInfo d) {
        if (!d.Temp.HasValue) return Dim;
        return d.Temp.Value >= d.TempCrit ? HC(Health.Bad) : d.Temp.Value >= d.TempWarn ? HC(Health.Caution) : HC(Health.Good);
    }
    static string Cap(string s) { return string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1); }

    static void Section(Graphics g, string t, float y) {
        using (var b = new LinearGradientBrush(new RectangleF(M, y + 8, 8, 30), Th.C1, Th.C2, 90f)) g.FillRectangle(b, M, y + 8, 8, 30);
        using (var f = P(32, FontStyle.Bold)) Th.Text_(g, t, f, Ink, new RectangleF(M + 24, y, W - 2 * M, 46));
    }

    static void Ring(Graphics g, RectangleF r, float v, string big, string small, Color c) {
        var rr = new RectangleF(r.X + 10, r.Y + 10, r.Width - 20, r.Height - 20);
        using (var pen = new Pen(Line, 16f)) g.DrawEllipse(pen, rr);
        if (v > 0.005f) using (var pen = new Pen(c, 16f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, rr, -90, 360 * Math.Min(1f, v));
        using (var f = P(40, FontStyle.Bold)) Th.Text_(g, big, f, Ink, new RectangleF(r.X, r.Y + r.Height / 2 - 36, r.Width, 50), StringAlignment.Center);
        using (var f = P(18)) Th.Text_(g, small, f, Dim, new RectangleF(r.X, r.Y + r.Height / 2 + 12, r.Width, 26), StringAlignment.Center);
    }

    // ---------- salvataggio ----------
    static byte[] Jpeg(Bitmap b, long quality) {
        var enc = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using (var ps = new EncoderParameters(1))
        using (var ms = new MemoryStream()) {
            ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            b.Save(ms, enc, ps);
            return ms.ToArray();
        }
    }

    public static void SaveJpeg(Bitmap b, string path) { File.WriteAllBytes(path, Jpeg(b, 95)); }

    // PDF di una pagina A4 con l'immagine JPEG (DCTDecode): nessuna libreria esterna
    public static void SavePdf(Bitmap b, string path, string title) {
        var jpg = Jpeg(b, 92);
        var ms = new MemoryStream();
        var offs = new List<long>();
        Action<string> w = s => { var x = Encoding.ASCII.GetBytes(s); ms.Write(x, 0, x.Length); };
        Action<int> obj = n => { offs.Add(ms.Position); w(n + " 0 obj\n"); };
        var inv = CultureInfo.InvariantCulture;
        w("%PDF-1.4\n%");
        ms.Write(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 5);
        obj(1); w("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        obj(2); w("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        obj(3); w("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.28 841.89] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        var content = "q 595.28 0 0 841.89 0 0 cm /Im0 Do Q\n";
        obj(4); w("<< /Length " + content.Length + " >>\nstream\n" + content + "endstream\nendobj\n");
        obj(5); w("<< /Type /XObject /Subtype /Image /Width " + b.Width.ToString(inv) + " /Height " + b.Height.ToString(inv) +
                  " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpg.Length.ToString(inv) + " >>\nstream\n");
        ms.Write(jpg, 0, jpg.Length);
        w("\nendstream\nendobj\n");
        obj(6); w("<< /Title " + PdfText(title) + " /Author (Erodyne DiskVital) /Producer (DiskVital " + Program.Version + ") /CreationDate (D:" + DateTime.Now.ToString("yyyyMMddHHmmss", inv) + ") >>\nendobj\n");
        long xref = ms.Position;
        w("xref\n0 " + (offs.Count + 1) + "\n0000000000 65535 f \n");
        foreach (var o in offs) w(o.ToString("D10", inv) + " 00000 n \n");
        w("trailer\n<< /Size " + (offs.Count + 1) + " /Root 1 0 R /Info 6 0 R >>\nstartxref\n" + xref.ToString(inv) + "\n%%EOF\n");
        File.WriteAllBytes(path, ms.ToArray());
    }

    // testo PDF in UTF-16BE esadecimale: accenti e simboli senza problemi di codifica
    static string PdfText(string s) {
        var sb = new StringBuilder("<FEFF");
        foreach (var b in Encoding.BigEndianUnicode.GetBytes(s ?? "")) sb.Append(b.ToString("X2"));
        return sb.Append('>').ToString();
    }
}
