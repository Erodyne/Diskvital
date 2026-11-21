using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// Storico: ogni lettura viene salvata (al massimo ogni 30 minuti, subito se qualcosa cambia),
// così si vede l'andamento e si scopre quando un contatore pericoloso ha iniziato a salire.
public class Snap {
    public DateTime T;
    public Health H;
    public int Temp = -1, Life = -1;
    public long Hours = -1, Cycles = -1, Realloc = -1, Pending = -1, Uncorr = -1, Media = -1;
    public double Written = -1;
}

public static class History {
    public static readonly string Dir = Path.Combine(AppSettings.Dir, "storico");
    static readonly object gate = new object();
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const string Header = "tempo;salute;temperatura;ore;accensioni;riallocati;in_attesa;non_correggibili;errori_media;scritti_gb;vita";

    static string FileOf(DiskInfo d) { return Path.Combine(Dir, d.Key + ".csv"); }

    public static Snap From(DiskInfo d) {
        return new Snap {
            T = d.ReadAt, H = d.Health, Temp = d.Temp ?? -1, Life = d.LifePercent ?? -1,
            Hours = d.PowerOnHours ?? -1, Cycles = d.PowerCycles ?? -1, Realloc = d.Reallocated ?? -1,
            Pending = d.Pending ?? -1, Uncorr = d.Uncorrectable ?? -1, Media = d.MediaErrors ?? -1,
            Written = d.WrittenGB ?? -1
        };
    }

    // Salva la lettura e restituisce i peggioramenti rispetto alla precedente (per le notifiche)
    public static List<string> Record(DiskInfo d) {
        var ev = new List<string>();
        if (string.IsNullOrEmpty(d.Serial) && string.IsNullOrEmpty(d.Model)) return ev;
        lock (gate) {
            try {
                Directory.CreateDirectory(Dir);
                var f = FileOf(d);
                var now = From(d);
                Snap last = null;
                if (File.Exists(f)) {
                    string ll = null;
                    foreach (var l in File.ReadLines(f)) if (l.Length > 0 && char.IsDigit(l[0])) ll = l;
                    if (ll != null) last = Parse(ll);
                }
                if (last != null) ev = Worse(last, now);
                bool changed = last == null || last.H != now.H || last.Realloc != now.Realloc || last.Pending != now.Pending || last.Uncorr != now.Uncorr || last.Media != now.Media || last.Life != now.Life;
                if (!changed && (now.T - last.T).TotalMinutes < 30) return ev;
                if (!File.Exists(f)) File.WriteAllText(f, Header + "\r\n");
                File.AppendAllText(f, Line(now) + "\r\n");
                Trim(f);
            } catch (Exception e) { Program.Log(e); }
        }
        return ev;
    }

    public static List<Snap> Load(DiskInfo d) {
        var r = new List<Snap>();
        lock (gate) {
            try {
                var f = FileOf(d);
                if (!File.Exists(f)) return r;
                foreach (var l in File.ReadLines(f)) {
                    if (l.Length == 0 || !char.IsDigit(l[0])) continue;
                    var s = Parse(l);
                    if (s != null) r.Add(s);
                }
            } catch (Exception e) { Program.Log(e); }
        }
        return r;
    }

    // Eventi lungo tutto lo storico: quando i contatori sono saliti, quando lo stato è cambiato
    public static List<KeyValuePair<DateTime, string>> Events(List<Snap> list) {
        var r = new List<KeyValuePair<DateTime, string>>();
        for (int i = 1; i < list.Count; i++)
            foreach (var e in Worse(list[i - 1], list[i], true)) r.Add(new KeyValuePair<DateTime, string>(list[i].T, e));
        r.Reverse();
        return r;
    }

    static List<string> Worse(Snap a, Snap b, bool also = false) {
        var r = new List<string>();
        Action<long, long, string> up = (x, y, name) => { if (x >= 0 && y > x) r.Add(name + ": da " + Fmt.N(x) + " a " + Fmt.N(y)); };
        up(a.Realloc, b.Realloc, "Settori riallocati");
        up(a.Pending, b.Pending, "Settori in attesa");
        up(a.Uncorr, b.Uncorr, "Settori non correggibili");
        up(a.Media, b.Media, "Errori di integrità");
        if (b.H > a.H && a.H != Health.Unknown) r.Add("Stato passato da " + Fmt.HealthText(a.H) + " a " + Fmt.HealthText(b.H));
        else if (also && b.H < a.H && b.H != Health.Unknown && a.H != Health.Unknown) r.Add("Stato tornato " + Fmt.HealthText(b.H));
        if (also && a.Life >= 0 && b.Life >= 0 && b.Life / 10 < a.Life / 10) r.Add("Vita residua scesa al " + b.Life + "%");
        return r;
    }

    static string Line(Snap s) {
        return string.Join(";", new[] {
            s.T.ToString("yyyy-MM-dd HH:mm:ss", Inv), ((int)s.H).ToString(Inv), s.Temp.ToString(Inv), s.Hours.ToString(Inv), s.Cycles.ToString(Inv),
            s.Realloc.ToString(Inv), s.Pending.ToString(Inv), s.Uncorr.ToString(Inv), s.Media.ToString(Inv), s.Written.ToString("0.###", Inv), s.Life.ToString(Inv) });
    }
    static Snap Parse(string l) {
        try {
            var p = l.Split(';');
            if (p.Length < 11) return null;
            return new Snap {
                T = DateTime.ParseExact(p[0], "yyyy-MM-dd HH:mm:ss", Inv), H = (Health)int.Parse(p[1], Inv), Temp = int.Parse(p[2], Inv),
                Hours = long.Parse(p[3], Inv), Cycles = long.Parse(p[4], Inv), Realloc = long.Parse(p[5], Inv), Pending = long.Parse(p[6], Inv),
                Uncorr = long.Parse(p[7], Inv), Media = long.Parse(p[8], Inv), Written = double.Parse(p[9], Inv), Life = int.Parse(p[10], Inv)
            };
        } catch { return null; }
    }
    // al massimo ~2 anni di letture ogni mezz'ora: oltre si tengono le più recenti
    static void Trim(string f) {
        var fi = new FileInfo(f);
        if (fi.Length < 3 * 1024 * 1024) return;
        var lines = File.ReadAllLines(f).Where(x => x.Length > 0 && char.IsDigit(x[0])).ToList();
        var keep = new List<string> { Header };
        keep.AddRange(lines.Skip(Math.Max(0, lines.Count - 25000)));
        File.WriteAllLines(f, keep.ToArray());
    }
}

public static class AppSettings {
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiskVital");
    static readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static string FilePath { get { return Path.Combine(Dir, "impostazioni.txt"); } }
    static AppSettings() {
        try {
            if (!File.Exists(FilePath)) return;
            foreach (var line in File.ReadAllLines(FilePath)) {
                int i = line.IndexOf('=');
                if (i > 0 && !line.StartsWith("#")) map[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
        } catch { }
    }
    public static string Get(string k, string def = null) { string v; return map.TryGetValue(k, out v) ? v : def; }
    public static int GetInt(string k, int def) { int v; return int.TryParse(Get(k), out v) ? v : def; }
    public static void Set(string k, string v) {
        if (string.IsNullOrEmpty(v)) map.Remove(k); else map[k] = v;
        try {
            Directory.CreateDirectory(Dir);
            var l = new List<string> { "# DiskVital" };
            foreach (var kv in map) l.Add(kv.Key + "=" + kv.Value);
            File.WriteAllLines(FilePath, l.ToArray());
        } catch { }
    }
}

// Rapporto: testo da copiare e pagina HTML da salvare o stampare
public static class Report {
    public static string Text(DiskInfo d) {
        var sb = new StringBuilder();
        sb.AppendLine("DiskVital " + Program.Version + " · " + d.ReadAt.ToString("dd/MM/yyyy HH:mm"));
        sb.AppendLine(new string('-', 60));
        foreach (var kv in Info(d)) sb.AppendLine(kv.Key.PadRight(27) + kv.Value);
        sb.AppendLine();
        if (d.Attrs.Count > 0) {
            sb.AppendLine(d.IsNvme ? "ID  Attributo                              Valore" : "ID  Attributo                         Att. Pegg. Soglia  Grezzo");
            foreach (var a in d.Attrs) {
                if (d.IsNvme) sb.AppendLine(a.Id.ToString("X2") + "  " + a.Name.PadRight(38) + " " + a.Shown);
                else sb.AppendLine(a.Id.ToString("X2") + "  " + Cut(a.Name, 32).PadRight(32) + " " + a.Current.ToString().PadLeft(4) + " " + a.Worst.ToString().PadLeft(5) + " " + a.Threshold.ToString().PadLeft(6) + "  " + a.Shown);
            }
        }
        return sb.ToString();
    }
    static string Cut(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

    public static List<KeyValuePair<string, string>> Info(DiskInfo d) {
        var l = new List<KeyValuePair<string, string>>();
        Action<string, string> A = (k, v) => { if (!string.IsNullOrEmpty(v)) l.Add(new KeyValuePair<string, string>(k, v)); };
        A("Modello", d.Model);
        A("Stato di salute", Fmt.HealthText(d.Health) + (d.Reasons.Count > 0 && d.Health != Health.Good ? " (" + string.Join(", ", d.Reasons.ToArray()) + ")" : ""));
        A("Temperatura", d.Temp.HasValue ? d.Temp + " °C" : null);
        A("Vita residua", d.LifePercent.HasValue ? d.LifePercent + " %" : null);
        A("Capacità", d.Size > 0 ? Fmt.Bytes(d.Size) : null);
        A("Tipo", d.Kind);
        A("Numero di serie", d.Serial);
        A("Firmware", d.Firmware);
        A("Interfaccia", d.Interface);
        A("Modalità di trasferimento", d.TransferMode);
        A("Standard", d.Standard);
        A("Ore di accensione", d.PowerOnHours.HasValue ? Fmt.N(d.PowerOnHours.Value) + " h (" + Fmt.Hours(d.PowerOnHours.Value) + ")" : null);
        A("Accensioni", d.PowerCycles.HasValue ? Fmt.N(d.PowerCycles.Value) : null);
        A("Dati scritti", d.WrittenGB.HasValue ? Fmt.Bytes(d.WrittenGB.Value * 1e9) : null);
        A("Dati letti", d.ReadGB.HasValue ? Fmt.Bytes(d.ReadGB.Value * 1e9) : null);
        A("Volumi", d.Volumes.Count > 0 ? d.Letters : null);
        A("Funzioni", d.Features.Count > 0 ? string.Join(", ", d.Features.ToArray()) : null);
        A("Disco fisico", "PhysicalDrive" + d.Number + (string.IsNullOrEmpty(d.Access) ? "" : " · lettura " + d.Access));
        return l;
    }

    static string H(string s) { return System.Net.WebUtility.HtmlEncode(s ?? ""); }

    public static string Html(List<DiskInfo> disks) {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"it\"><head><meta charset=\"utf-8\"><title>Rapporto DiskVital</title><style>");
        sb.Append("body{background:#0B0D12;color:#E8EAF2;font:14px 'Segoe UI',sans-serif;margin:0;padding:32px}h1{font-size:26px;margin:0 0 4px}");
        sb.Append(".sub{color:#9AA0B8;margin-bottom:24px}.card{background:#141823;border:1px solid #222736;border-radius:18px;padding:22px;margin-bottom:22px}");
        sb.Append(".hero{display:flex;gap:16px;align-items:center;margin-bottom:14px}.badge{padding:6px 14px;border-radius:999px;font-weight:700}");
        sb.Append(".g{background:#34D39926;color:#34D399}.c{background:#FBBF2426;color:#FBBF24}.b{background:#F8717126;color:#F87171}.u{background:#6B729026;color:#9AA0B8}");
        sb.Append("table{border-collapse:collapse;width:100%}td,th{padding:6px 10px;border-bottom:1px solid #222736;text-align:left}th{color:#6B7290;font-weight:600}");
        sb.Append(".kv td:first-child{color:#9AA0B8;width:220px}.num{text-align:right;font-variant-numeric:tabular-nums}h2{margin:0;font-size:19px}");
        sb.Append(".bar{height:4px;border-radius:2px;background:linear-gradient(90deg,#34D399,#7C5CFF);margin:-6px 0 18px}@media print{body{background:#fff;color:#000}.card{background:#fff}}");
        sb.Append("</style></head><body><h1>Rapporto DiskVital</h1><div class=\"bar\"></div><div class=\"sub\">");
        sb.Append(H(Environment.MachineName + " · " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + " · DiskVital " + Program.Version)).Append("</div>");
        foreach (var d in disks) {
            string cls = d.Health == Health.Good ? "g" : d.Health == Health.Caution ? "c" : d.Health == Health.Bad ? "b" : "u";
            sb.Append("<div class=\"card\"><div class=\"hero\"><span class=\"badge ").Append(cls).Append("\">").Append(H(Fmt.HealthText(d.Health))).Append("</span><h2>").Append(H(d.Model)).Append("</h2></div>");
            sb.Append("<p>").Append(H(Fmt.Advice(d))).Append("</p><table class=\"kv\">");
            foreach (var kv in Info(d)) sb.Append("<tr><td>").Append(H(kv.Key)).Append("</td><td>").Append(H(kv.Value)).Append("</td></tr>");
            sb.Append("</table>");
            if (d.Attrs.Count > 0) {
                sb.Append("<br><table><tr><th>ID</th><th>Attributo</th>");
                if (!d.IsNvme) sb.Append("<th class=\"num\">Attuale</th><th class=\"num\">Peggiore</th><th class=\"num\">Soglia</th>");
                sb.Append("<th class=\"num\">Valore</th></tr>");
                foreach (var a in d.Attrs) {
                    string col = a.State == Health.Bad ? " style=\"color:#F87171\"" : a.State == Health.Caution ? " style=\"color:#FBBF24\"" : "";
                    sb.Append("<tr").Append(col).Append("><td>").Append(a.Id.ToString("X2")).Append("</td><td>").Append(H(a.Name)).Append("</td>");
                    if (!d.IsNvme) sb.Append("<td class=\"num\">").Append(a.Current).Append("</td><td class=\"num\">").Append(a.Worst).Append("</td><td class=\"num\">").Append(a.Threshold).Append("</td>");
                    sb.Append("<td class=\"num\">").Append(H(a.Shown)).Append("</td></tr>");
                }
                sb.Append("</table>");
            }
            sb.Append("</div>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
