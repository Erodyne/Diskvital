using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public enum Health { Unknown = 0, Good = 1, Caution = 2, Bad = 3 }

public class SmartAttr {
    public byte Id;
    public ushort Flags;
    public int Current, Worst, Threshold;
    public byte[] RawBytes = new byte[6];
    public ulong Raw;
    public string Name, Shown;
    public Health State = Health.Good;
}

public class VolumeInfo {
    public string Letter, Label, Fs;
    public long Total, Free;
}

public class DiskInfo {
    public int Number;
    public string Model = "", Serial = "", Firmware = "";
    public int BusType;
    public long Size;
    public bool IsSsd, IsNvme, Removable;
    public int Rpm;                    // 0 = sconosciuto, 1 = SSD
    public string Interface = "", TransferMode = "", Standard = "", Access = "";
    public List<string> Features = new List<string>();
    public bool SmartSupported, SmartEnabled, SmartRead;
    public string SmartNote;
    public List<SmartAttr> Attrs = new List<SmartAttr>();

    public int? Temp, LifePercent, AvailSpare, SpareThreshold, PercentUsed;
    public long? PowerOnHours, PowerCycles, Reallocated, Pending, Uncorrectable, MediaErrors, UnsafeShutdowns, ErrorLogEntries, ReallocEvents;
    public double? WrittenGB, ReadGB;
    public int CriticalWarning;
    public int TempWarn = 50, TempCrit = 55;

    public Health Health;
    public List<string> Reasons = new List<string>();
    public List<VolumeInfo> Volumes = new List<VolumeInfo>();
    public DateTime ReadAt = DateTime.Now;

    public string Key {
        get { return Regex.Replace((Model + "_" + Serial).Trim('_'), @"[^A-Za-z0-9._-]+", "_"); }
    }
    public string Letters {
        get { return string.Join(" ", Volumes.Select(v => v.Letter).ToArray()); }
    }
    public string Kind {
        get {
            if (IsNvme) return "SSD NVMe";
            if (IsSsd) return "SSD " + (BusType == 7 ? "USB" : "SATA");
            if (Rpm > 1) return "HDD " + Rpm + " rpm";
            return BusType == 7 ? "Disco USB" : "HDD";
        }
    }
    public int ProblemSectors {
        get { return (int)Math.Min(int.MaxValue, (Reallocated ?? 0) + (Pending ?? 0) + (Uncorrectable ?? 0)); }
    }
    public bool TempHigh { get { return Temp.HasValue && Temp.Value >= TempWarn; } }
}

// Nomi e spiegazioni degli attributi S.M.A.R.T., in parole semplici
public static class SmartDb {
    class Def { public string Name, Ssd, Help; }
    static readonly Dictionary<byte, Def> db = new Dictionary<byte, Def>();
    static void A(byte id, string name, string help, string ssd = null) { db[id] = new Def { Name = name, Help = help, Ssd = ssd }; }

    static SmartDb() {
        A(0x01, "Errori di lettura", "Letture che il disco ha dovuto ripetere o correggere. Su molti dischi (Seagate in particolare) il valore grezzo è sempre altissimo ed è normale: conta solo il valore attuale rispetto alla soglia.");
        A(0x02, "Prestazioni di trasferimento", "Indice generale delle prestazioni meccaniche. Un calo marcato può indicare usura.");
        A(0x03, "Tempo di avvio rotazione", "Tempo che il disco impiega a portare i piatti alla velocità di lavoro. Se cresce, il motore può essere affaticato.");
        A(0x04, "Avvii / arresti", "Quante volte i piatti si sono fermati e ripartiti. È un contatore di usura, non un errore.");
        A(0x05, "Settori riallocati", "Settori danneggiati che il disco ha sostituito con settori di riserva. Deve essere 0: se è maggiore di zero, e soprattutto se aumenta, il disco si sta deteriorando. Fai un backup.", "Blocchi riallocati");
        A(0x07, "Errori di posizionamento", "Errori della testina nel posizionarsi. Come per gli errori di lettura, su alcuni dischi il grezzo è sempre alto: guarda il valore attuale.");
        A(0x08, "Prestazioni di posizionamento", "Velocità media di posizionamento delle testine.");
        A(0x09, "Ore di accensione", "Da quante ore in totale il disco è stato acceso.");
        A(0x0A, "Tentativi di avvio rotazione", "Volte in cui il motore non è riuscito a partire al primo colpo. Deve essere 0: altrimenti c'è un problema meccanico o di alimentazione.");
        A(0x0B, "Tentativi di ricalibrazione", "Ricalibrazioni ripetute della testina. Valori in aumento indicano problemi meccanici.");
        A(0x0C, "Accensioni", "Quante volte il disco è stato acceso.");
        A(0x0D, "Errori software di lettura", "Errori di lettura corretti dal firmware.");
        A(0x16, "Elio residuo", "Livello dell'elio nei dischi sigillati ad elio. Se scende, il disco perde la sua tenuta.");
        A(0xAA, "Blocchi di riserva disponibili", "Blocchi di riserva rimasti per sostituire quelli rovinati. Più scende, meno margine ha l'SSD.", "Blocchi di riserva disponibili");
        A(0xAB, "Errori di programmazione", "Scritture NAND fallite. Valori in crescita indicano usura della memoria.", "Errori di programmazione");
        A(0xAC, "Errori di cancellazione", "Cancellazioni NAND fallite. Valori in crescita indicano usura della memoria.", "Errori di cancellazione");
        A(0xAD, "Cancellazioni medie per blocco", "Usura media delle celle: quante volte, in media, ogni blocco è stato cancellato.", "Cancellazioni medie per blocco");
        A(0xAE, "Spegnimenti improvvisi", "Spegnimenti senza preavviso (corrente staccata, blocchi). Non è un guasto ma, se frequenti, rischi di perdere dati.", "Spegnimenti improvvisi");
        A(0xAF, "Errori di programmazione (peggior die)", "Errori di scrittura sul chip di memoria più usurato.");
        A(0xB1, "Livellamento usura", "Quanto è consumata la memoria dell'SSD. Il valore attuale parte da 100 e scende con l'uso.", "Livellamento usura");
        A(0xB3, "Blocchi di riserva usati", "Blocchi di riserva già utilizzati per sostituire blocchi rovinati.");
        A(0xB4, "Blocchi di riserva liberi", "Blocchi di riserva ancora disponibili.");
        A(0xB5, "Errori di programmazione totali", "Scritture NAND fallite in tutta la vita dell'SSD.");
        A(0xB6, "Errori di cancellazione totali", "Cancellazioni NAND fallite in tutta la vita dell'SSD.");
        A(0xB7, "Declassamenti SATA", "Volte in cui il collegamento SATA è sceso a una velocità inferiore: spesso colpa del cavo o della porta.");
        A(0xB8, "Errori end-to-end", "Dati arrivati corrotti tra la cache e i piatti/la memoria. Deve essere 0.");
        A(0xBB, "Errori non correggibili segnalati", "Errori che il disco non è riuscito a correggere: i dati letti erano persi. Deve essere 0.");
        A(0xBC, "Timeout dei comandi", "Comandi non completati in tempo, spesso per cavi, alimentazione o controller. Pochi sono normali.");
        A(0xBD, "Scritture ad altezza anomala", "Scritture eseguite con la testina troppo alta: urti o vibrazioni.");
        A(0xBE, "Temperatura del flusso d'aria", "Temperatura interna misurata dal disco.");
        A(0xBF, "Urti rilevati", "Urti e vibrazioni registrati dal sensore. Attenzione se il disco viene spostato mentre lavora.");
        A(0xC0, "Spegnimenti con parcheggio di emergenza", "Spegnimenti improvvisi in cui le testine sono state ritirate d'emergenza.", "Spegnimenti improvvisi");
        A(0xC1, "Cicli di caricamento testina", "Quante volte le testine sono state parcheggiate. Alcuni dischi da notebook lo fanno spesso: valori di centinaia di migliaia indicano usura.");
        A(0xC2, "Temperatura", "Temperatura attuale del disco.");
        A(0xC3, "Errori corretti dall'ECC", "Errori di lettura corretti al volo dal codice di correzione. Il grezzo spesso non è significativo.");
        A(0xC4, "Eventi di riallocazione", "Quante volte il disco ha tentato di spostare un settore difettoso. Deve essere 0.");
        A(0xC5, "Settori in attesa", "Settori instabili che il disco non riesce a leggere e che aspettano di essere riscritti o sostituiti. Devono essere 0: altrimenti ci sono già dati illeggibili. Fai subito un backup.");
        A(0xC6, "Settori non correggibili", "Settori che non si leggono nemmeno durante i controlli automatici. Devono essere 0: sono dati persi.");
        A(0xC7, "Errori CRC sul cavo", "Errori di trasmissione sul cavo SATA. Non indicano un guasto del disco: se aumentano, cambia il cavo o la porta.");
        A(0xC8, "Errori di scrittura", "Errori durante la scrittura. Valori in crescita indicano problemi della superficie o delle testine.");
        A(0xC9, "Errori software di lettura", "Errori di lettura non correggibili via software.");
        A(0xCA, "Errori di indirizzamento", "Errori nel trovare i dati. Su SSD Crucial/Micron indica invece la vita consumata.", "Vita consumata");
        A(0xCC, "Errori corretti dal software", "Errori corretti dal software ECC.");
        A(0xD1, "Prestazioni di ricerca", "Prestazioni di ricerca offline.");
        A(0xDC, "Spostamento dei piatti", "Spostamento dei piatti rispetto all'asse, di solito per urti.");
        A(0xDF, "Tentativi di caricamento testina", "Tentativi di caricare le testine sui piatti.");
        A(0xE0, "Attrito in caricamento", "Resistenza meccanica durante il caricamento delle testine.");
        A(0xE2, "Tempo di caricamento", "Tempo totale di caricamento delle testine.");
        A(0xE6, "Ampiezza GMR / vita SSD", "Su HDD: ampiezza del segnale della testina. Su alcuni SSD: stato della memoria.");
        A(0xE7, "Vita residua SSD", "Vita residua stimata della memoria dell'SSD in percento. Sotto il 10% conviene sostituirlo.", "Vita residua SSD");
        A(0xE8, "Riserva disponibile", "Spazio di riserva disponibile per sostituire blocchi rovinati.", "Riserva disponibile");
        A(0xE9, "Indicatore di usura", "Usura della memoria (Intel) o totale scritto sulla NAND, secondo il produttore.", "Indicatore di usura / NAND scritta");
        A(0xEA, "Media e massimo cancellazioni", "Conteggio delle cancellazioni dei blocchi.");
        A(0xEB, "Blocchi buoni / spegnimenti", "Specifico del produttore.");
        A(0xF0, "Ore di volo testina", "Ore in cui le testine sono state sopra i piatti.");
        A(0xF1, "Totale scritto", "Totale dei dati scritti nel disco dalla sua prima accensione.");
        A(0xF2, "Totale letto", "Totale dei dati letti dal disco dalla sua prima accensione.");
        A(0xF5, "Vita residua", "Vita residua o NAND scritta, secondo il produttore.");
        A(0xF6, "Settori scritti dal computer", "Totale dei settori scritti dal computer.");
        A(0xF7, "Pagine scritte dal computer", "Pagine NAND scritte su richiesta del computer.");
        A(0xF8, "Pagine scritte dal controller", "Pagine NAND scritte internamente dall'SSD (pulizia, livellamento).");
        A(0xFE, "Protezione da cadute", "Cadute rilevate dal sensore (dischi da notebook).");
    }

    public static string Name(byte id, bool ssd) {
        Def d;
        if (!db.TryGetValue(id, out d)) return "Specifico del produttore";
        return ssd && d.Ssd != null ? d.Ssd : d.Name;
    }
    public static string Help(byte id) {
        Def d;
        return db.TryGetValue(id, out d) ? d.Help : "Attributo specifico del produttore: il suo significato varia da modello a modello. Conta soprattutto che il valore attuale resti sopra la soglia.";
    }

    // ---------- interpretazione ----------
    public static void Evaluate(DiskInfo d) {
        d.Reasons.Clear();
        if (d.IsNvme) { EvaluateNvme(d); return; }
        if (!d.SmartRead) {
            d.Health = Health.Unknown;
            d.Reasons.Add(d.SmartNote ?? "Il disco non fornisce dati S.M.A.R.T.");
            return;
        }
        var h = Health.Good;
        string model = d.Model.ToUpperInvariant();
        foreach (var a in d.Attrs) {
            a.Name = Name(a.Id, d.IsSsd);
            a.State = Health.Good;
            ulong raw48 = a.Raw;
            switch (a.Id) {
                case 0x09: d.PowerOnHours = (long)(raw48 & 0xFFFFFFFF); break;
                case 0x0C: d.PowerCycles = (long)(raw48 & 0xFFFFFFFF); break;
                case 0x05: d.Reallocated = (long)(raw48 & 0xFFFFFFFF); break;
                case 0xC4: d.ReallocEvents = (long)(raw48 & 0xFFFFFFFF); break;
                case 0xC5: d.Pending = (long)(raw48 & 0xFFFFFFFF); break;
                case 0xC6: d.Uncorrectable = (long)(raw48 & 0xFFFFFFFF); break;
                case 0xAE: case 0xC0: if (d.IsSsd) d.UnsafeShutdowns = (long)(raw48 & 0xFFFFFFFF); break;
                case 0xC2:
                case 0xBE: {
                    int t = a.RawBytes[0];
                    if (t > 0 && t < 120 && (a.Id == 0xC2 || !d.Temp.HasValue)) d.Temp = t;
                    break;
                }
            }
            // sotto la soglia del produttore = guasto previsto
            if (a.Threshold > 0 && a.Threshold < 0xFE && a.Current > 0 && a.Current <= a.Threshold) {
                a.State = Health.Bad;
                d.Reasons.Add("\"" + a.Name + "\" è sotto la soglia del produttore (" + a.Current + " ≤ " + a.Threshold + ")");
                h = Health.Bad;
            }
        }
        // settori problematici: anche uno solo merita attenzione
        foreach (var x in new[] { new { Id = (byte)0x05, V = d.Reallocated, T = "settori riallocati" },
                                  new { Id = (byte)0xC5, V = d.Pending, T = "settori in attesa" },
                                  new { Id = (byte)0xC6, V = d.Uncorrectable, T = "settori non correggibili" } }) {
            if (x.V.HasValue && x.V.Value > 0) {
                var a = d.Attrs.FirstOrDefault(z => z.Id == x.Id);
                if (a != null && a.State != Health.Bad) a.State = Health.Caution;
                d.Reasons.Add(x.V.Value + " " + x.T);
                if (h < Health.Caution) h = Health.Caution;
            }
        }
        // vita residua degli SSD
        if (d.IsSsd) {
            SmartAttr life = null;
            Func<byte, SmartAttr> at = id => d.Attrs.FirstOrDefault(z => z.Id == id);
            life = at(0xE7) ?? at(0xA9);
            if (life == null && model.Contains("SAMSUNG")) life = at(0xB1);
            if (life == null && (model.Contains("CRUCIAL") || model.Contains("MICRON") || model.StartsWith("CT"))) life = at(0xCA);
            if (life == null && model.Contains("INTEL")) life = at(0xE9);
            if (life == null && (model.Contains("KINGSTON") || model.Contains("SANDISK") || model.Contains("WDC") || model.Contains("WD "))) life = at(0xE6) ?? at(0xAD);
            if (life != null && life.Current > 0 && life.Current <= 100) d.LifePercent = life.Current;
            if (d.LifePercent.HasValue && d.LifePercent.Value <= 10) {
                d.Reasons.Add("vita residua della memoria al " + d.LifePercent.Value + "%");
                if (h < Health.Caution) h = Health.Caution;
            }
            d.WrittenGB = Written(d, model, 0xF1) ?? Written(d, model, 0xF6);
            d.ReadGB = Written(d, model, 0xF2);
        }
        foreach (var a in d.Attrs) a.Shown = ShowRaw(a, d);
        if (d.Attrs.Count == 0) { h = Health.Unknown; d.Reasons.Add("nessun attributo letto"); }
        d.Health = h;
    }

    // Totale scritto: le unità cambiano da produttore a produttore
    static double? Written(DiskInfo d, string model, byte id) {
        var a = d.Attrs.FirstOrDefault(z => z.Id == id);
        if (a == null || a.Raw == 0) return null;
        double raw = a.Raw;
        if (model.Contains("INTEL")) return raw * 32 / 1024.0;                          // unità da 32 MiB
        double lbaGb = raw * 512 / 1e9;
        // con molte ore di accensione meno di 1 GB scritto non è credibile: il produttore conta in GiB
        if (lbaGb < 1 && (d.PowerOnHours ?? 0) > 10) return raw * 1.073741824;
        return lbaGb;
    }

    static string ShowRaw(SmartAttr a, DiskInfo d) {
        switch (a.Id) {
            case 0xC2:
            case 0xBE: {
                int t = a.RawBytes[0], lo = a.RawBytes[2], hi = a.RawBytes[4];
                var s = t + " °C";
                if (lo > 0 && hi > lo && hi < 120 && lo <= t) s += "  (min " + lo + ", max " + hi + ")";
                return s;
            }
            case 0x09: return Fmt.N((long)(a.Raw & 0xFFFFFFFF)) + " h";
            case 0xF1: case 0xF2: case 0xF6: {
                var gb = a.Id == 0xF2 ? d.ReadGB : Written(d, d.Model.ToUpperInvariant(), a.Id);
                return gb.HasValue ? Fmt.Bytes(gb.Value * 1e9) + "  (" + Fmt.N((long)a.Raw) + ")" : Fmt.N((long)a.Raw);
            }
            case 0x05: case 0xC5: case 0xC6: case 0x0C: case 0x04: case 0xC4: case 0xC7: case 0xBB: case 0xAE: case 0xC0:
                return Fmt.N((long)(a.Raw & 0xFFFFFFFF));
        }
        return Fmt.N((long)a.Raw);
    }

    static void EvaluateNvme(DiskInfo d) {
        if (!d.SmartRead) {
            d.Health = Health.Unknown;
            d.Reasons.Add(d.SmartNote ?? "Il driver non fornisce i dati di salute NVMe");
            return;
        }
        var h = Health.Good;
        int cw = d.CriticalWarning;
        if ((cw & 0x01) != 0) { d.Reasons.Add("riserva di blocchi sotto la soglia"); h = Health.Bad; }
        if ((cw & 0x02) != 0) { d.Reasons.Add("temperatura oltre il limite"); if (h < Health.Caution) h = Health.Caution; }
        if ((cw & 0x04) != 0) { d.Reasons.Add("affidabilità ridotta per errori interni"); h = Health.Bad; }
        if ((cw & 0x08) != 0) { d.Reasons.Add("il disco è passato in sola lettura"); h = Health.Bad; }
        if ((cw & 0x10) != 0) { d.Reasons.Add("guasto della memoria di backup"); h = Health.Bad; }
        if (d.AvailSpare.HasValue && d.SpareThreshold.HasValue && d.SpareThreshold.Value > 0 && d.AvailSpare.Value < d.SpareThreshold.Value && (cw & 1) == 0) {
            d.Reasons.Add("riserva al " + d.AvailSpare + "%, sotto la soglia del " + d.SpareThreshold + "%"); h = Health.Bad;
        }
        if (d.PercentUsed.HasValue && d.PercentUsed.Value >= 90) {
            d.Reasons.Add("vita della memoria consumata al " + d.PercentUsed + "%"); if (h < Health.Caution) h = Health.Caution;
        }
        if ((d.MediaErrors ?? 0) > 0) {
            d.Reasons.Add(d.MediaErrors + " errori di integrità dei dati"); if (h < Health.Caution) h = Health.Caution;
        }
        if (d.PercentUsed.HasValue) d.LifePercent = Math.Max(0, 100 - d.PercentUsed.Value);
        d.Health = h;
        foreach (var a in d.Attrs) {
            if ((a.Id == 0x01 && cw != 0) || (a.Id == 0x0E && a.Raw > 0)) a.State = a.Id == 0x01 ? Health.Bad : Health.Caution;
            if (a.Id == 0x03 && d.SpareThreshold.HasValue && a.Raw < (ulong)Math.Max(0, d.SpareThreshold.Value)) a.State = Health.Bad;
            if (a.Id == 0x05 && a.Raw >= 90) a.State = Health.Caution;
        }
    }

    // Attributi NVMe mostrati come righe della tabella (id = posizione nel registro di salute, come CrystalDiskInfo)
    public static readonly string[][] NvmeRows = {
        new[] { "Avvisi critici", "Segnalazioni di guasto del controller (riserva esaurita, temperatura, sola lettura...). Deve essere 0." },
        new[] { "Temperatura", "Temperatura attuale del controller NVMe." },
        new[] { "Riserva disponibile", "Percentuale di blocchi di riserva ancora disponibili per sostituire quelli rovinati." },
        new[] { "Soglia della riserva", "Sotto questa percentuale di riserva il disco si considera a rischio." },
        new[] { "Vita consumata", "Percentuale della vita prevista dal produttore già consumata. Può superare 100: il disco continua a funzionare ma va tenuto d'occhio." },
        new[] { "Dati letti", "Totale dei dati letti dal computer." },
        new[] { "Dati scritti", "Totale dei dati scritti dal computer. Confrontalo con il TBW dichiarato dal produttore." },
        new[] { "Comandi di lettura", "Numero di comandi di lettura eseguiti." },
        new[] { "Comandi di scrittura", "Numero di comandi di scrittura eseguiti." },
        new[] { "Tempo di attività del controller", "Minuti in cui il controller è stato occupato." },
        new[] { "Accensioni", "Quante volte il disco è stato acceso." },
        new[] { "Ore di accensione", "Da quante ore in totale il disco è stato acceso." },
        new[] { "Spegnimenti improvvisi", "Spegnimenti senza preavviso. Non è un guasto, ma se frequenti rischi di perdere dati." },
        new[] { "Errori di integrità dei dati", "Errori non recuperati sui dati. Deve essere 0." },
        new[] { "Voci nel registro errori", "Errori registrati dal controller, spesso innocui (comandi non supportati inviati dal sistema)." },
        new[] { "Minuti sopra la temperatura di avviso", "Tempo passato oltre la temperatura di avviso." },
        new[] { "Minuti sopra la temperatura critica", "Tempo passato oltre la temperatura critica." }
    };
}

public static class Fmt {
    static readonly System.Globalization.CultureInfo It = new System.Globalization.CultureInfo("it-IT");
    public static string N(long v) { return v.ToString("#,0", It); }
    public static string N(double v, int dec) { return v.ToString("#,0." + new string('0', Math.Max(1, dec)), It); }
    public static string Bytes(double b) {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        int i = 0;
        while (b >= 1000 && i < u.Length - 1) { b /= 1000; i++; }
        return (i == 0 ? ((long)b).ToString() : b >= 100 ? b.ToString("0", It) : b.ToString("0.#", It)) + " " + u[i];
    }
    public static string Hours(long h) {
        if (h < 24) return N(h) + " h";
        long days = h / 24;
        long y = days / 365, m = (days % 365) / 30, dd = (days % 365) % 30;
        string s = y > 0 ? y + (y == 1 ? " anno" : " anni") + (m > 0 ? " e " + m + (m == 1 ? " mese" : " mesi") : "")
                 : m > 0 ? m + (m == 1 ? " mese" : " mesi") + (dd > 0 ? " e " + dd + (dd == 1 ? " giorno" : " giorni") : "")
                 : days + (days == 1 ? " giorno" : " giorni");
        return s;
    }
    public static string HealthText(Health h) {
        switch (h) { case Health.Good: return "Buono"; case Health.Caution: return "Attenzione"; case Health.Bad: return "Critico"; }
        return "Sconosciuto";
    }
    public static string Advice(DiskInfo d) {
        switch (d.Health) {
            case Health.Good: return d.TempHigh ? "Nessun guasto rilevato, ma il disco è caldo: controlla ventole e aerazione." : "Nessun problema rilevato. Continua a fare backup regolari.";
            case Health.Caution: return "Fai subito un backup dei dati importanti e tieni d'occhio il disco: se i valori peggiorano, sostituiscilo.";
            case Health.Bad: return "Il disco rischia di guastarsi a breve: copia subito i dati altrove e sostituiscilo.";
        }
        return "Il disco non fornisce dati di salute (capita con alcuni box USB, schede SD e controller RAID).";
    }
}
