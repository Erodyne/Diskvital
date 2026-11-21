using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Lettura diretta dei dischi: ATA (SATA), ATA tramite SMART_RCV_DRIVE_DATA (driver Intel RST e vecchi),
// SAT per i box USB e registro di salute NVMe. Serve l'amministratore (manifest).
public static class DiskReader {
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, SHARE_RW = 3, OPEN_EXISTING = 3;
    const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    const uint IOCTL_ATA_PASS_THROUGH = 0x0004D02C;
    const uint IOCTL_SCSI_PASS_THROUGH = 0x0004D004;
    const uint SMART_RCV_DRIVE_DATA = 0x0007C088;
    const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int ret, IntPtr ov);

    static bool Io(SafeFileHandle h, uint code, byte[] inb, byte[] outb) {
        int ret;
        return DeviceIoControl(h, code, inb, inb == null ? 0 : inb.Length, outb, outb == null ? 0 : outb.Length, out ret, IntPtr.Zero);
    }

    static int I32(byte[] b, int o) { return BitConverter.ToInt32(b, o); }
    static void P32(byte[] b, int o, int v) { BitConverter.GetBytes(v).CopyTo(b, o); }
    static void P16(byte[] b, int o, int v) { BitConverter.GetBytes((ushort)v).CopyTo(b, o); }
    static void PPtr(byte[] b, int o, long v) { if (IntPtr.Size == 8) BitConverter.GetBytes(v).CopyTo(b, o); else P32(b, o, (int)v); }
    static int W(byte[] d, int word) { return d[word * 2] | (d[word * 2 + 1] << 8); }

    public static List<DiskInfo> ReadAll() {
        var list = new List<DiskInfo>();
        var vols = Volumes();
        for (int n = 0; n < 64; n++) {
            try {
                var d = ReadDisk(n);
                if (d == null) continue;
                List<VolumeInfo> v;
                if (vols.TryGetValue(n, out v)) d.Volumes = v;
                SmartDb.Evaluate(d);
                list.Add(d);
            } catch (Exception e) { Program.Log(e); }
        }
        return list;
    }

    static DiskInfo ReadDisk(int n) {
        string path = @"\\.\PhysicalDrive" + n;
        var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        bool full = !h.IsInvalid;
        if (!full) {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            if (err == 2 || err == 3) return null; // il disco non esiste
            h = CreateFile(path, 0, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); return null; }
        }
        using (h) {
            var d = new DiskInfo { Number = n };
            if (!Descriptor(h, d)) return null;
            if (d.BusType == 0xE || d.BusType == 0xF || d.BusType == 0x10) return null; // dischi virtuali, VHD, Spazi di archiviazione
            var len = new byte[8];
            if (Io(h, IOCTL_DISK_GET_LENGTH_INFO, null, len)) d.Size = BitConverter.ToInt64(len, 0);
            d.IsSsd = NoSeekPenalty(h);
            if (!full) { d.SmartNote = "Serve l'accesso da amministratore per leggere i dati di salute"; return d; }

            bool nvme = d.BusType == 0x11;
            if (nvme || d.BusType == 0x8 || d.BusType == 0x1) {
                if (ReadNvme(h, d)) return d;
                if (nvme) { d.IsNvme = true; d.IsSsd = true; d.Interface = "NVM Express"; return d; }
            }
            // ATA: prima il pass-through standard, poi SMART_RCV_DRIVE_DATA, per USB e SCSI il SAT
            bool scsiLike = d.BusType == 7 || d.BusType == 1 || d.BusType == 0xA;
            var order = new List<KeyValuePair<string, Func<byte, byte, byte[]>>>();
            Func<byte, byte, byte[]> sat = (f, c) => Sat(h, f, c), ata = (f, c) => AtaPt(h, f, c), rcv = (f, c) => SmartRcv(h, n, f, c);
            if (scsiLike) order.Add(new KeyValuePair<string, Func<byte, byte, byte[]>>("SAT", sat));
            order.Add(new KeyValuePair<string, Func<byte, byte, byte[]>>("ATA", ata));
            order.Add(new KeyValuePair<string, Func<byte, byte, byte[]>>("ATA (SMART_RCV)", rcv));
            if (!scsiLike) order.Add(new KeyValuePair<string, Func<byte, byte, byte[]>>("SAT", sat));
            foreach (var kv in order) {
                var cmd = kv.Value;
                byte[] id = null;
                try { id = cmd(0, 0xEC); } catch { }
                if (id == null || id.All(x => x == 0)) continue;
                ParseIdentify(id, d);
                if (!d.SmartSupported) { d.SmartNote = "Il disco non supporta S.M.A.R.T."; return d; }
                if (!d.SmartEnabled) d.SmartNote = "S.M.A.R.T. è disattivato nel disco";
                byte[] data = null, thr = null;
                try { data = cmd(0xD0, 0xB0); thr = cmd(0xD1, 0xB0); } catch { }
                if (data != null) { ParseSmart(data, thr, d); d.SmartRead = d.Attrs.Count > 0; }
                if (d.SmartRead) d.Access = kv.Key;
                return d;
            }
            if (d.BusType == 7) d.SmartNote = "Il box USB non lascia passare i dati di salute del disco";
            else if (d.BusType == 0xC || d.BusType == 0xD) d.SmartNote = "Le schede di memoria non forniscono dati di salute";
            else if (d.BusType == 0x8) d.SmartNote = "Il controller RAID non lascia passare i dati di salute";
            return d;
        }
    }

    // ---------- descrittore del dispositivo ----------
    static bool Descriptor(SafeFileHandle h, DiskInfo d) {
        var q = new byte[12];
        var o = new byte[1024];
        if (!Io(h, IOCTL_STORAGE_QUERY_PROPERTY, q, o)) return false;
        if (o[8] != 0) return false; // non è un disco (lettori ottici ecc.)
        d.Removable = o[10] != 0;
        string vendor = Str(o, I32(o, 12)), product = Str(o, I32(o, 16)), rev = Str(o, I32(o, 20)), serial = Str(o, I32(o, 24));
        d.BusType = I32(o, 28);
        if (vendor.Equals("NVMe", StringComparison.OrdinalIgnoreCase) || vendor.Equals("ATA", StringComparison.OrdinalIgnoreCase)) vendor = "";
        d.Model = (vendor.Length > 0 && !product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? vendor + " " : "") + product;
        d.Firmware = rev;
        d.Serial = serial.Trim().TrimEnd('.');
        d.Interface = BusName(d.BusType);
        return true;
    }

    static string Str(byte[] b, int off) {
        if (off <= 0 || off >= b.Length) return "";
        int e = off; while (e < b.Length && b[e] != 0) e++;
        return Clean(Encoding.ASCII.GetString(b, off, e - off));
    }
    static string Clean(string s) {
        var sb = new StringBuilder();
        foreach (var c in s) if (c >= 32 && c < 127) sb.Append(c);
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    static string BusName(int b) {
        switch (b) {
            case 1: return "SCSI"; case 2: return "ATAPI"; case 3: return "ATA"; case 4: return "FireWire";
            case 6: return "Fibre Channel"; case 7: return "USB"; case 8: return "RAID"; case 9: return "iSCSI";
            case 0xA: return "SAS"; case 0xB: return "SATA"; case 0xC: return "SD"; case 0xD: return "MMC";
            case 0x11: return "NVM Express"; case 0x12: return "SCM"; case 0x13: return "UFS";
        }
        return "Sconosciuta";
    }

    static bool NoSeekPenalty(SafeFileHandle h) {
        var q = new byte[12]; P32(q, 0, 7); // StorageDeviceSeekPenaltyProperty
        var o = new byte[12];
        return Io(h, IOCTL_STORAGE_QUERY_PROPERTY, q, o) && I32(o, 4) >= 9 && o[8] == 0;
    }

    // ---------- ATA pass-through ----------
    static void TaskFile(byte[] tf, int o, byte feat, byte cmd) {
        tf[o + 0] = feat; tf[o + 1] = 1; tf[o + 2] = 1;
        if (cmd == 0xB0) { tf[o + 3] = 0x4F; tf[o + 4] = 0xC2; }
        tf[o + 5] = 0xA0; tf[o + 6] = cmd;
    }

    static byte[] AtaPt(SafeFileHandle h, byte feat, byte cmd) {
        bool x64 = IntPtr.Size == 8;
        int hdr = x64 ? 48 : 40, bufOff = x64 ? 24 : 20, cur = x64 ? 40 : 32;
        var b = new byte[hdr + 512];
        P16(b, 0, hdr);
        P16(b, 2, 0x01 | 0x02); // DRDY_REQUIRED | DATA_IN
        P32(b, 8, 512);
        P32(b, 12, 10);
        PPtr(b, bufOff, hdr);
        TaskFile(b, cur, feat, cmd);
        var o = new byte[b.Length];
        if (!Io(h, IOCTL_ATA_PASS_THROUGH, b, o)) return null;
        if ((o[cur + 6] & 0x01) != 0) return null; // bit ERR nello stato
        var d = new byte[512]; Array.Copy(o, hdr, d, 0, 512);
        return d;
    }
    static byte[] SmartRcv(SafeFileHandle h, int drive, byte feat, byte cmd) {
        var i = new byte[32];
        P32(i, 0, 512);
        TaskFile(i, 4, feat, cmd);
        i[4 + 5] = (byte)(0xA0 | ((drive & 1) << 4));
        i[12] = (byte)drive;
        var o = new byte[16 + 512];
        if (!Io(h, SMART_RCV_DRIVE_DATA, i, o)) return null;
        if (o[4] != 0) return null; // bDriverError
        var d = new byte[512]; Array.Copy(o, 16, d, 0, 512);
        return d;
    }
    // SAT: comando ATA dentro un comando SCSI ATA PASS-THROUGH(12), per i box USB
    static byte[] Sat(SafeFileHandle h, byte feat, byte cmd) {
        bool x64 = IntPtr.Size == 8;
        int hdr = x64 ? 56 : 44, bufOff = x64 ? 24 : 20, senseOff = x64 ? 32 : 24, cdbOff = x64 ? 36 : 28;
        int sense = hdr, data = hdr + 32;
        var b = new byte[data + 512];
        P16(b, 0, hdr);
        b[6] = 12;    // CdbLength
        b[7] = 32;    // SenseInfoLength
        b[8] = 1;     // SCSI_IOCTL_DATA_IN
        P32(b, 12, 512);
        P32(b, 16, 10);
        PPtr(b, bufOff, data);
        P32(b, senseOff, sense);
        var c = cdbOff;
        b[c + 0] = 0xA1; b[c + 1] = 4 << 1; b[c + 2] = 0x0E;
        b[c + 3] = feat; b[c + 4] = 1; b[c + 5] = 1;
        if (cmd == 0xB0) { b[c + 6] = 0x4F; b[c + 7] = 0xC2; }
        b[c + 8] = 0xA0; b[c + 9] = cmd;
        var o = new byte[b.Length];
        if (!Io(h, IOCTL_SCSI_PASS_THROUGH, b, o)) return null;
        if (o[2] != 0) return null; // ScsiStatus
        var d = new byte[512]; Array.Copy(o, data, d, 0, 512);
        return d;
    }

    static string AtaStr(byte[] d, int word, int words) {
        var sb = new StringBuilder();
        for (int i = 0; i < words; i++) { sb.Append((char)d[(word + i) * 2 + 1]); sb.Append((char)d[(word + i) * 2]); }
        return Clean(sb.ToString());
    }

    static void ParseIdentify(byte[] id, DiskInfo d) {
        var model = AtaStr(id, 27, 20);
        if (model.Length > 0) d.Model = model;
        var serial = AtaStr(id, 10, 10);
        if (serial.Length > 0) d.Serial = serial;
        var fw = AtaStr(id, 23, 4);
        if (fw.Length > 0) d.Firmware = fw;
        int w82 = W(id, 82), w83 = W(id, 83), w85 = W(id, 85), w76 = W(id, 76), w77 = W(id, 77), w80 = W(id, 80), w169 = W(id, 169), w217 = W(id, 217);
        d.SmartSupported = w82 == 0 || w82 == 0xFFFF || (w82 & 1) != 0; // campo non valorizzato: si prova lo stesso
        d.SmartEnabled = w85 == 0 || w85 == 0xFFFF || (w85 & 1) != 0;
        if (w217 == 1) { d.Rpm = 1; d.IsSsd = true; }
        else if (w217 >= 0x401 && w217 < 0xFFFF) { d.Rpm = w217; d.IsSsd = false; }
        if (d.Size == 0) {
            long lba = (long)W(id, 100) | ((long)W(id, 101) << 16) | ((long)W(id, 102) << 32);
            d.Size = lba * 512;
        }
        if (w76 != 0 && w76 != 0xFFFF) {
            string max = (w76 & 0x08) != 0 ? "SATA/600" : (w76 & 0x04) != 0 ? "SATA/300" : (w76 & 0x02) != 0 ? "SATA/150" : "";
            int cs = (w77 >> 1) & 7;
            string now = cs == 3 ? "SATA/600" : cs == 2 ? "SATA/300" : cs == 1 ? "SATA/150" : "";
            d.TransferMode = now.Length > 0 && max.Length > 0 ? now + " | " + max : max;
            if (d.BusType == 0xB || d.BusType == 3) d.Interface = "Serial ATA";
            if ((w76 & 0x100) != 0) d.Features.Add("NCQ");
        }
        if (w80 != 0 && w80 != 0xFFFF) {
            string std = (w80 & 0x800) != 0 ? "ACS-4" : (w80 & 0x400) != 0 ? "ACS-3" : (w80 & 0x200) != 0 ? "ACS-2" : (w80 & 0x100) != 0 ? "ATA8-ACS" : (w80 & 0x80) != 0 ? "ATA/ATAPI-7" : "";
            d.Standard = std;
        }
        if (d.SmartSupported) d.Features.Insert(0, "S.M.A.R.T.");
        if ((w83 & 0x400) != 0) d.Features.Add("LBA 48 bit");
        if ((w83 & 0x08) != 0) d.Features.Add("APM");
        if ((w83 & 0x200) != 0) d.Features.Add("AAM");
        if ((w169 & 1) != 0 && w169 != 0xFFFF) d.Features.Add("TRIM");
        if ((w82 & 0x20) != 0 && w82 != 0xFFFF) d.Features.Add("Cache di scrittura");
        if ((W(id, 128) & 1) != 0 && W(id, 128) != 0xFFFF) d.Features.Add("Sicurezza ATA");
        if (d.IsSsd) { d.TempWarn = 60; d.TempCrit = 70; }
    }

    static void ParseSmart(byte[] data, byte[] thr, DiskInfo d) {
        d.Attrs.Clear();
        for (int i = 0; i < 30; i++) {
            int o = 2 + i * 12;
            byte id = data[o];
            if (id == 0) continue;
            var a = new SmartAttr { Id = id, Flags = (ushort)(data[o + 1] | (data[o + 2] << 8)), Current = data[o + 3], Worst = data[o + 4] };
            Array.Copy(data, o + 5, a.RawBytes, 0, 6);
            ulong raw = 0;
            for (int k = 5; k >= 0; k--) raw = (raw << 8) | a.RawBytes[k];
            a.Raw = raw;
            if (thr != null)
                for (int j = 0; j < 30; j++) if (thr[2 + j * 12] == id) { a.Threshold = thr[2 + j * 12 + 1]; break; }
            d.Attrs.Add(a);
        }
    }

    // ---------- NVMe ----------
    static byte[] NvmeQuery(SafeFileHandle h, int dataType, int value, int len) {
        var q = new byte[8 + 40 + len];
        P32(q, 0, 50);           // StorageDeviceProtocolSpecificProperty (con StorageAdapter... per alcuni driver: 49)
        P32(q, 4, 0);
        P32(q, 8, 3);            // ProtocolTypeNvme
        P32(q, 12, dataType);    // 1 = Identify, 2 = LogPage
        P32(q, 16, value);
        P32(q, 20, 0);
        P32(q, 24, 40);          // ProtocolDataOffset
        P32(q, 28, len);
        var o = new byte[q.Length];
        if (!Io(h, IOCTL_STORAGE_QUERY_PROPERTY, q, o)) {
            P32(q, 0, 49);       // StorageAdapterProtocolSpecificProperty
            if (!Io(h, IOCTL_STORAGE_QUERY_PROPERTY, q, o)) return null;
        }
        int off = 8 + I32(o, 8 + 16), l = I32(o, 8 + 20);
        if (off < 8 || l <= 0 || off + Math.Min(l, len) > o.Length) return null;
        var d = new byte[len]; Array.Copy(o, off, d, 0, Math.Min(l, len));
        return d;
    }

    static ulong U64(byte[] b, int o) { return BitConverter.ToUInt64(b, o); } // i contatori NVMe sono da 128 bit: i 64 bassi bastano

    static bool ReadNvme(SafeFileHandle h, DiskInfo d) {
        var log = NvmeQuery(h, 2, 2, 512);
        if (log == null) return false;
        d.IsNvme = true; d.IsSsd = true; d.Rpm = 1;
        d.Interface = "NVM Express"; d.BusType = 0x11;
        d.TempWarn = 70; d.TempCrit = 80;
        var idc = NvmeQuery(h, 1, 1, 4096);
        if (idc != null && idc.Any(x => x != 0)) {
            var mn = Clean(Encoding.ASCII.GetString(idc, 24, 40));
            var sn = Clean(Encoding.ASCII.GetString(idc, 4, 20));
            var fr = Clean(Encoding.ASCII.GetString(idc, 64, 8));
            if (mn.Length > 0) d.Model = mn;
            if (sn.Length > 0) d.Serial = sn;
            if (fr.Length > 0) d.Firmware = fr;
            int ver = I32(idc, 80);
            if (ver != 0) d.Standard = "NVM Express " + ((ver >> 16) & 0xFFFF) + "." + ((ver >> 8) & 0xFF);
            int wc = idc[266] | (idc[267] << 8), cc = idc[268] | (idc[269] << 8);
            if (wc > 273 && wc < 400) d.TempWarn = wc - 273;
            if (cc > 273 && cc < 400) d.TempCrit = cc - 273;
            int oncs = idc[520] | (idc[521] << 8);
            d.Features.Add("S.M.A.R.T.");
            if ((oncs & 0x04) != 0) d.Features.Add("TRIM");
            if ((idc[525] & 1) != 0) d.Features.Add("Cache di scrittura");
            if ((idc[265] & 1) != 0) d.Features.Add("Risparmio energetico autonomo");
            if ((oncs & 0x08) != 0) d.Features.Add("Write Zeroes");
        } else d.Features.Add("S.M.A.R.T.");
        d.SmartSupported = d.SmartEnabled = true;

        d.CriticalWarning = log[0];
        int k = log[1] | (log[2] << 8);
        if (k > 273 && k < 400) d.Temp = k - 273;
        d.AvailSpare = log[3]; d.SpareThreshold = log[4]; d.PercentUsed = log[5];
        double unit = 512000; // un'unità di dati NVMe = 1000 settori da 512 byte
        d.ReadGB = U64(log, 32) * unit / 1e9;
        d.WrittenGB = U64(log, 48) * unit / 1e9;
        d.PowerCycles = (long)U64(log, 112);
        d.PowerOnHours = (long)U64(log, 128);
        d.UnsafeShutdowns = (long)U64(log, 144);
        d.MediaErrors = (long)U64(log, 160);
        d.ErrorLogEntries = (long)U64(log, 176);

        // righe della tabella, come i contatori del registro di salute
        int[] offs = { 0, 1, 3, 4, 5, 32, 48, 64, 80, 96, 112, 128, 144, 160, 176, 192, 196 };
        for (int i = 0; i < offs.Length; i++) {
            ulong raw;
            string shown;
            if (i == 1) { raw = (ulong)Math.Max(0, k - 273); shown = raw + " °C"; }
            else if (i == 0 || (i >= 2 && i <= 4)) { raw = log[offs[i]]; shown = i == 0 ? "0x" + raw.ToString("X2") : raw + " %"; }
            else if (i >= 15) { raw = (ulong)I32(log, offs[i]); shown = Fmt.N((long)raw) + " min"; }
            else {
                raw = U64(log, offs[i]);
                if (i == 5 || i == 6) shown = Fmt.Bytes(raw * unit) + "  (" + Fmt.N((long)raw) + ")";
                else if (i == 11) shown = Fmt.N((long)raw) + " h";
                else if (i == 9) shown = Fmt.N((long)raw) + " min";
                else shown = Fmt.N((long)raw);
            }
            var a = new SmartAttr { Id = (byte)(i + 1), Raw = raw, Shown = shown, Name = SmartDb.NvmeRows[i][0] };
            d.Attrs.Add(a);
        }
        d.SmartRead = true;
        d.Access = "NVMe";
        return true;
    }

    // ---------- volumi (lettere) per ogni disco ----------
    static Dictionary<int, List<VolumeInfo>> Volumes() {
        var map = new Dictionary<int, List<VolumeInfo>>();
        foreach (var di in DriveInfo.GetDrives()) {
            try {
                if (di.DriveType != DriveType.Fixed && di.DriveType != DriveType.Removable) continue;
                var letter = di.Name.Substring(0, 2);
                var v = new VolumeInfo { Letter = letter };
                try { if (di.IsReady) { v.Label = di.VolumeLabel; v.Fs = di.DriveFormat; v.Total = di.TotalSize; v.Free = di.TotalFreeSpace; } } catch { }
                using (var h = CreateFile(@"\\.\" + letter, 0, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero)) {
                    if (h.IsInvalid) continue;
                    var o = new byte[8 + 24 * 8];
                    if (!Io(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, o)) continue;
                    int cnt = Math.Min(I32(o, 0), 8);
                    var seen = new HashSet<int>();
                    for (int i = 0; i < cnt; i++) {
                        int disk = I32(o, 8 + i * 24);
                        if (!seen.Add(disk)) continue;
                        List<VolumeInfo> l;
                        if (!map.TryGetValue(disk, out l)) map[disk] = l = new List<VolumeInfo>();
                        l.Add(v);
                    }
                }
            } catch { }
        }
        return map;
    }
}
