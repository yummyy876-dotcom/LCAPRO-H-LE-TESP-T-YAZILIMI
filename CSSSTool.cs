// LCAPRO - CS 1.6 SS (Screenshare) Kontrol Aracı  (pencereli arayüz)
// Amaç: Geçmiş/iz temizleme belirtilerini ve CS 1.6 klasöründeki şüpheli dosyaları raporlamak.
// Not: Sonuçlar KESİN KANIT değil, ipucudur. Araç yönetici olarak çalışmak ister (UAC sorar).
//
// Derleme: build.bat dosyasını çalıştırın (CSSSTool.cs + LogoData.cs birlikte derlenir).
//
// Kullanım:
//   CSSSTool.exe                      (Half-Life klasörünü Steam'den otomatik bulur)
//   CSSSTool.exe "D:\Oyunlar\cstrike" (klasörü elle verirsiniz; pencereden de seçilebilir)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Principal;
using System.Threading;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Forms;

class CSSSTool
{
    internal static StringBuilder rapor = new StringBuilder();

    // Şüpheli dosya/program adı parçaları (kendi listenizle genişletin)
    static string[] anahtarlar = new string[] {
        // hile
        "aimbot", "wallhack", "nhack", "cheat", "hack", "injector", "inject",
        "xhack", "hlh", "speedhack", "triggerbot", "norecoil", "recoil",
        // spoofer / ban atlatma
        "spoofer", "spoof", "hwid", "unban", "kdmapper", "volumeid", "macchanger", "tmac",
        // iz/gecmis temizleyiciler
        "ccleaner", "bleachbit", "privazer", "sdelete", "tracecleaner",
        // makro (no-recoil makrolari icin sik kullanilir)
        "autohotkey", "ahk", "bloody"
    };

    // Oyun klasörüne konulduğunda şüpheli sayılan DLL'ler
    static string[] supheliDll = new string[] {
        "opengl32.dll", "dinput8.dll", "d3d9.dll", "winmm.dll", "version.dll"
    };

    internal static Action<string> SatirEklendi;
    static int guclu = 0, zayif = 0;
    static int izIsareti = 0;   // iz temizleme belirtisi sayacı (birleşik değerlendirme için)

    static void IzYaz(string s)
    {
        izIsareti++;
        Yaz(s);
    }

    internal static void Yaz(string s)
    {
        if (s == null) s = "";
        s = Maskele(s);
        lock (rapor)
        {
            rapor.AppendLine(s);
            foreach (string l in s.Split('\n'))
            {
                if (l.StartsWith("[!]", StringComparison.Ordinal)) guclu++;
                else if (l.StartsWith("[~]", StringComparison.Ordinal)) zayif++;
            }
        }
        Action<string> h = SatirEklendi;
        if (h != null) h(s);
    }

    static void Baslik(string s)
    {
        Yaz("");
        Yaz("=== " + s + " ===");
    }

    static string Calistir(string exe, string args)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                return o;
            }
        }
        catch (Exception ex) { return "HATA: " + ex.Message; }
    }

    static bool Eslesir(string ad)
    {
        return Siniflandir(ad) > 0;
    }

    // 1) Olay günlüğü temizleme + USN günlüğü silme
    static void OlayGunlukleri()
    {
        Baslik("Olay günlüğü temizleme kayıtları");
        string[][] sorgular = new string[][] {
            new string[] { "Security", "1102", "Security günlüğü temizlendi" },
            new string[] { "System", "104", "System günlüğü temizlendi" },
            new string[] { "System", "3079", "USN günlüğü silindi" }
        };
        foreach (string[] s in sorgular)
        {
            string cikti = Calistir("wevtutil",
                "qe " + s[0] + " /q:\"*[System[(EventID=" + s[1] + ")]]\" /c:3 /rd:true /f:text");
            bool bulundu = false;
            foreach (string satir in cikti.Split('\n'))
            {
                string t = satir.Trim();
                if (t.StartsWith("Date:"))
                {
                    DateTime olayZamani;
                    bool yeniOlay = true;
                    if (t.Length > 5 && DateTime.TryParse(t.Substring(5).Trim(), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out olayZamani))
                        yeniOlay = (DateTime.Now - olayZamani.ToLocalTime()).TotalDays <= 90;
                    if (yeniOlay) IzYaz("[~] " + s[2] + " -> " + t);
                    else Yaz("[?] " + s[2] + " (90 günden eski, sayılmadı) -> " + t);
                    bulundu = true;
                }
            }
            if (!bulundu)
            {
                if (cikti.Contains("Access is denied") || cikti.StartsWith("HATA"))
                    Yaz("[?] " + s[2] + ": okunamadı (yönetici olarak çalıştırın)");
                else
                    Yaz("[+] " + s[2] + ": kayıt yok");
            }
        }
    }

    // 2) USN günlüğü durumu
    static void UsnDurumu()
    {
        Baslik("USN günlüğü (C:)");
        string o = Calistir("fsutil", "usn queryjournal C:");
        Yaz(o.Trim());
    }

    // 3) Prefetch durumu
    static void Prefetch()
    {
        Baslik("Prefetch");
        string sc = Calistir("sc", "query SysMain");
        Yaz(sc.Contains("RUNNING") ? "[+] SysMain servisi çalışıyor" : "[~] SysMain servisi kapalı (Prefetch devre dışı bırakılmış olabilir)");

        string yol = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\Prefetch";
        if (!Directory.Exists(yol)) { Yaz("[?] Prefetch klasörü yok/erişilemiyor"); return; }

        string[] dosyalar = Directory.GetFiles(yol, "*.pf");
        Yaz("Prefetch dosya sayısı: " + dosyalar.Length);
        if (dosyalar.Length < 20) IzYaz("[~] Çok az dosya var, yakın zamanda temizlenmiş olabilir");

        DateTime enEski = DateTime.MaxValue;
        foreach (string f in dosyalar)
        {
            DateTime c = File.GetCreationTime(f);
            if (c < enEski) enEski = c;
            string ad = Path.GetFileName(f);
            int svp = Siniflandir(ad);
            if (svp > 0) Yaz(Etiket(svp) + "Şüpheli prefetch: " + ad + "  (son çalışma: " + File.GetLastWriteTime(f) + ")");
        }
        if (dosyalar.Length > 0) Yaz("En eski prefetch kaydı: " + enEski);
    }

    // 4) Geri dönüşüm kutusu ve Recent
    static void IzKlasorleri()
    {
        Baslik("Geri dönüşüm kutusu / Recent");
        try
        {
            foreach (string d in Directory.GetDirectories("C:\\$Recycle.Bin"))
                Yaz("Geri dönüşüm kutusu son değişiklik: " + Directory.GetLastWriteTime(d) + "  (" + Path.GetFileName(d) + ")");
        }
        catch { Yaz("[?] Geri dönüşüm kutusu okunamadı"); }

        string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        try
        {
            string[] r = Directory.GetFiles(recent);
            Yaz("Recent dosya sayısı: " + r.Length);
            if (r.Length < 5) IzYaz("[~] Recent neredeyse boş, temizlenmiş olabilir");
            foreach (string f in r)
            {
                    int svr = Siniflandir(Path.GetFileName(f));
                    if (svr > 0) Yaz(Etiket(svr) + "Şüpheli Recent kaydı: " + Path.GetFileName(f));
                }
        }
        catch { Yaz("[?] Recent okunamadı"); }
    }

    // 5) UserAssist (ROT13) - çalıştırılan programlar
    static string Rot13(string s)
    {
        char[] c = s.ToCharArray();
        for (int i = 0; i < c.Length; i++)
        {
            char ch = c[i];
            if (ch >= 'a' && ch <= 'z') c[i] = (char)((ch - 'a' + 13) % 26 + 'a');
            else if (ch >= 'A' && ch <= 'Z') c[i] = (char)((ch - 'A' + 13) % 26 + 'A');
        }
        return new string(c);
    }

    static void UserAssist()
    {
        Baslik("UserAssist (çalıştırılan programlar)");
        try
        {
            using (RegistryKey ua = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist"))
            {
                if (ua == null) { Yaz("[?] UserAssist okunamadı"); return; }
                int toplam = 0;
                foreach (string guid in ua.GetSubKeyNames())
                {
                    using (RegistryKey cnt = ua.OpenSubKey(guid + "\\Count"))
                    {
                        if (cnt == null) continue;
                        foreach (string ad in cnt.GetValueNames())
                        {
                            toplam++;
                            string cozulmus = Rot13(ad);
                            if (UserAssistSistem(cozulmus) || GuvenilirYol(cozulmus)) continue;
                            int sva = Siniflandir(cozulmus);
                            if (sva > 0) Yaz(Etiket(sva) + "Şüpheli program: " + cozulmus);
                        }
                    }
                }
                Yaz("Toplam UserAssist kaydı: " + toplam);
                if (toplam < 10) IzYaz("[~] Çok az kayıt var, temizlenmiş olabilir");
            }
        }
        catch (Exception ex) { Yaz("[?] Hata: " + ex.Message); }
    }

    // 6) CS 1.6 klasör taraması
    static bool OyunKlasoruGecerli(string p)
    {
        try
        {
            return !string.IsNullOrEmpty(p) && Directory.Exists(p)
                && (Directory.Exists(Path.Combine(p, "cstrike")) || File.Exists(Path.Combine(p, "hl.exe")));
        }
        catch (Exception) { return false; }
    }

    // Half-Life ana klasörü (hl.exe'nin durduğu yer). cstrike/valve verilirse bir üst klasöre çıkar.
    static string KokKlasor(string k)
    {
        if (string.IsNullOrEmpty(k)) return k;
        string s = k.Trim().Trim('"').TrimEnd('\\', '/');
        string ad = Path.GetFileName(s).ToLowerInvariant();
        if (ad == "cstrike" || ad == "valve")
        {
            string ust = Path.GetDirectoryName(s);
            if (!string.IsNullOrEmpty(ust)) return ust;
        }
        return s;
    }

    static string OyunKlasoru(string[] args)
    {
        // 1) komut satırı
        if (args.Length > 0 && Directory.Exists(args[0])) return KokKlasor(args[0]);

        // 2) çalışan hl.exe (Steam dışı kurulumlar için en güvenilir yol)
        try
        {
            foreach (Process pr in Process.GetProcessesByName("hl"))
            {
                try
                {
                    string d = Path.GetDirectoryName(pr.MainModule.FileName);
                    if (OyunKlasoruGecerli(d)) return d;
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }

        // 3) Steam ve ek Steam kütüphaneleri
        try
        {
            object steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null);
            if (steam != null)
            {
                string sp = steam.ToString().Replace('/', '\\');
                List<string> kutuphaneler = new List<string>();
                kutuphaneler.Add(sp);
                string vdf = Path.Combine(sp, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        kutuphaneler.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                }
                foreach (string kt in kutuphaneler)
                {
                    string d = Path.Combine(kt, @"steamapps\common\Half-Life");
                    if (OyunKlasoruGecerli(d)) return d;
                }
            }
        }
        catch (Exception) { }

        // 4) Steam dışı kurulumlarda sık kullanılan klasörler
        try
        {
            string[] onekler = new string[] { "", @"Games\", @"Oyunlar\", @"Program Files\", @"Program Files (x86)\" };
            string[] ad = new string[] { "Counter-Strike 1.6", "Counter-Strike", "Counter Strike 1.6", "CS 1.6", "CS1.6", "Half-Life" };
            foreach (DriveInfo dr in DriveInfo.GetDrives())
            {
                if (dr.DriveType != DriveType.Fixed || !dr.IsReady) continue;
                foreach (string o in onekler)
                    foreach (string n in ad)
                    {
                        string d = Path.Combine(dr.Name, o + n);
                        if (OyunKlasoruGecerli(d)) return d;
                    }
            }
        }
        catch (Exception) { }
        return null;
    }

    static void OyunTara(string klasor)
    {
        Baslik("CS 1.6 klasör taraması: " + klasor);
        if (klasor == null)
        {
            Yaz("[?] Oyun klasörü bulunamadı. 'Oyun Klasörü Seç' düğmesiyle seçin.");
            return;
        }

        foreach (string dll in supheliDll)
        {
            string p = Path.Combine(klasor, dll);
            if (!File.Exists(p)) continue;
            if (GuvenilirDosya(p))
            {
                Yaz("[+] " + dll + " oyun klasöründe ama geçerli imzalı, güvenilir yayıncı: " + p);
                continue;
            }
            bool kritik = dll.Equals("opengl32.dll", StringComparison.OrdinalIgnoreCase);
            Yaz((kritik ? "[!] " : "[~] ") + "Oyun klasöründe imzasız " + dll + " (proxy DLL): " + p
                + "  (değişiklik: " + File.GetLastWriteTime(p) + ")");
        }

        int sayac = 0;
        try
        {
            foreach (string f in Directory.GetFiles(klasor, "*.*", SearchOption.AllDirectories))
            {
                string ad = Path.GetFileName(f);
                string uz = Path.GetExtension(f).ToLowerInvariant();
                if (uz == ".dll" || uz == ".exe" || uz == ".cfg" || uz == ".asi" || uz == ".ini")
                {
                    int sv = Siniflandir(ad);
                    if (sv > 0)
                    {
                        Yaz(Etiket(sv) + "Şüpheli dosya: " + f);
                        sayac++;
                    }
                }
            }
        }
        catch (Exception ex) { Yaz("[?] Tarama hatası: " + ex.Message); }
        if (sayac == 0) Yaz("[+] Ad bazlı şüpheli dosya bulunmadı");
    }

    // 7) Çalışan hl.exe içine yüklenmiş modüller (Windows / antivirüs / tanınmış yayıncı imzalıları güvenilir sayılır)
    static void CalisanOyun()
    {
        Baslik("Çalışan hl.exe modülleri");
        Process[] ps = Process.GetProcessesByName("hl");
        if (ps.Length == 0) { Yaz("[?] hl.exe çalışmıyor (oyun açıkken tekrar çalıştırın)"); return; }

        foreach (Process p in ps)
        {
            try
            {
                string oyunDizin = Path.GetDirectoryName(p.MainModule.FileName).ToLowerInvariant();
                int guvenilir = 0, oyunIci = 0, bulgu = 0;
                foreach (ProcessModule m in p.Modules)
                {
                    string yol = m.FileName;
                    string k = yol.ToLowerInvariant();
                    string ad = Path.GetFileName(k);

                    if (k.StartsWith(oyunDizin, StringComparison.Ordinal))
                    {
                        oyunIci++;
                        if (Array.IndexOf(supheliDll, ad) >= 0 && !GuvenilirDosya(yol))
                        {
                            Yaz("[!] Oyun klasöründen imzasız proxy DLL yüklenmiş: " + yol);
                            bulgu++;
                        }
                        else if (Siniflandir(ad) > 0)
                        {
                            Yaz(Etiket(Siniflandir(ad)) + "Oyun klasöründen şüpheli adlı modül yüklenmiş: " + yol);
                            bulgu++;
                        }
                        continue;
                    }

                    if (GuvenilirDosya(yol)) { guvenilir++; continue; }

                    int sv = Siniflandir(ad);
                    if (sv > 0)
                    {
                        Yaz(Etiket(sv) + "Şüpheli adlı modül yüklenmiş: " + yol);
                    }
                    else if (!ImzaGecerli(yol))
                    {
                        if (KullaniciDizini(k))
                            Yaz("[!] Kullanıcı klasöründen imzasız DLL yüklenmiş (enjekte edilmiş olabilir): " + yol);
                        else
                            Yaz("[~] İmzasız modül yüklenmiş: " + yol);
                    }
                    else
                    {
                        string y = Yayinci(yol);
                        Yaz("[~] İmzalı ama tanınmayan yayıncı: " + yol + "  (" + (y == null ? "?" : y) + ")");
                    }
                    bulgu++;
                }
                Yaz((bulgu == 0 ? "[+] " : "") + "hl.exe (PID " + p.Id + "): " + guvenilir
                    + " modül güvenilir (Windows / antivirüs / tanınmış yayıncı), " + oyunIci + " modül oyun klasöründen");
            }
            catch (Exception ex) { Yaz("[?] Modüller okunamadı: " + ex.Message); }
        }
    }

    static DateTime baslangic = DateTime.Now;

    // ================= CFG / ALIAS / NO-RECOIL =================
    static string[] cvarSupheli = new string[] {
        "r_novis", "gl_wireframe", "gl_polyoffset", "r_lightmap"
    };

    static string Kisalt(string s)
    {
        s = s.Trim();
        return s.Length > 140 ? s.Substring(0, 140) + "..." : s;
    }

    static bool Icerir(string l, params string[] parcalar)
    {
        foreach (string p in parcalar)
            if (l.Contains(p)) return true;
        return false;
    }

    static void CfgTara(string klasor)
    {
        Baslik("CFG / alias / no-recoil taraması");
        if (klasor == null) { Yaz("[?] Oyun klasörü yok, atlandı"); return; }

        string[] cfgler;
        try { cfgler = Directory.GetFiles(klasor, "*.cfg", SearchOption.AllDirectories); }
        catch (Exception ex) { Yaz("[?] CFG dosyaları okunamadı: " + ex.Message); return; }

        Yaz("Taranan .cfg sayısı: " + cfgler.Length);
        bool configVar = false, userconfigVar = false;
        int aliasToplam = 0, bulgu = 0;

        foreach (string f in cfgler)
        {
            string ad = Path.GetFileName(f).ToLowerInvariant();
            if (ad == "config.cfg") { configVar = true; Yaz("config.cfg son değişiklik: " + File.GetLastWriteTime(f)); }
            if (ad == "userconfig.cfg") { userconfigVar = true; Yaz("userconfig.cfg son değişiklik: " + File.GetLastWriteTime(f)); }

            string[] satirlar;
            try { satirlar = File.ReadAllLines(f, Encoding.Default); }
            catch { continue; }

            for (int i = 0; i < satirlar.Length; i++)
            {
                string l = satirlar[i].Trim().ToLowerInvariant();
                if (l.Length == 0 || l.StartsWith("//")) continue;
                string yer = f.Substring(klasor.Length).TrimStart('\\') + ":" + (i + 1);
                bool alias = l.StartsWith("alias ");
                if (alias) aliasToplam++;

                // No-recoil: bakış açısını (pitch) saldırı sırasında otomatik oynatan alias/bind'ler
                bool bakis = Icerir(l, "lookdown", "lookup", "pitchspeed", "pitchdown", "pitchup");
                if (bakis && (alias || l.Contains("+attack") || l.Contains(";")))
                {
                    Yaz("[!] No-recoil alias/bind şüphesi  " + yer + "  -> " + Kisalt(l));
                    bulgu++;
                }
                else if (Icerir(l, "cl_pitchspeed", "cl_pitchdown", "cl_pitchup"))
                {
                    Yaz("[~] cl_pitch ayarı (no-recoil scriptlerinde kullanılır)  " + yer + "  -> " + Kisalt(l));
                    bulgu++;
                }

                // Rapid-fire / makro: +attack ile birden çok wait
                int wait = 0, idx = 0;
                while ((idx = l.IndexOf("wait", idx)) >= 0) { wait++; idx += 4; }

                if (l.Contains("+attack") && wait >= 2 && (alias || l.StartsWith("bind")))
                {
                    Yaz("[!] Rapid-fire/makro şüphesi  " + yer + "  -> " + Kisalt(l));
                    bulgu++;
                }
                if (alias && l.Contains("+jump") && wait >= 1)
                {
                    Yaz("[~] Bhop/makro alias  " + yer + "  -> " + Kisalt(l));
                    bulgu++;
                }
                if (l.StartsWith("bind") && l.Contains("mwheel") && l.Contains("jump"))
                {
                    Yaz("[~] Mouse tekerleği ile zıplama bind'i  " + yer + "  -> " + Kisalt(l));
                    bulgu++;
                }
                foreach (string cv in cvarSupheli)
                {
                    if (l.Contains(cv))
                    {
                        Yaz("[~] Wallhack benzeri cvar  " + yer + "  -> " + Kisalt(l));
                        bulgu++;
                        break;
                    }
                }
            }
        }

        Yaz("Toplam alias sayısı: " + aliasToplam);
        if (aliasToplam > 50) Yaz("[~] Çok sayıda alias var, elle göz atın");
        if (!configVar) Yaz("[~] config.cfg bulunamadı (silinmiş olabilir)");
        if (!userconfigVar) Yaz("[~] userconfig.cfg yok (normal olabilir)");
        if (bulgu == 0) Yaz("[+] Şüpheli alias/cfg bulgusu yok");
    }

    // ================= HWID SPOOFER =================
    [DllImport("advapi32.dll", CharSet = CharSet.Auto)]
    static extern int RegQueryInfoKey(SafeRegistryHandle hKey, StringBuilder lpClass, IntPtr lpcbClass,
        IntPtr lpReserved, IntPtr lpcSubKeys, IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen,
        IntPtr lpcValues, IntPtr lpcbMaxValueNameLen, IntPtr lpcbMaxValueLen,
        IntPtr lpcbSecurityDescriptor, out long lpftLastWriteTime);

    static DateTime? AnahtarSonYazma(RegistryKey k)
    {
        long ft;
        int r = RegQueryInfoKey(k.Handle, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out ft);
        if (r != 0) return null;
        return DateTime.FromFileTime(ft);
    }

    static DateTime KurulumTarihi()
    {
        try
        {
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                object v = k.GetValue("InstallDate");
                if (v != null)
                    return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Convert.ToInt64(v)).ToLocalTime();
            }
        }
        catch { }
        return DateTime.MinValue;
    }

    static void KimlikKontrol(string ad, string yol, string deger, DateTime kurulum)
    {
        try
        {
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey k = hklm.OpenSubKey(yol))
            {
                if (k == null) { Yaz("[?] " + ad + ": anahtar yok"); return; }
                object v = k.GetValue(deger);
                DateTime? t = AnahtarSonYazma(k);
                string s = ad + ": " + (v == null ? "(değer yok)" : v.ToString())
                    + "  | anahtar son yazma: " + (t.HasValue ? t.Value.ToString() : "?");
                if (kurulum != DateTime.MinValue && t.HasValue && t.Value > kurulum.AddDays(2))
                    Yaz("[~] " + s + "  -> kurulumdan sonra değişmiş (spoofer izi olabilir)");
                else
                    Yaz("[+] " + s);
            }
        }
        catch (Exception ex) { Yaz("[?] " + ad + ": " + ex.Message); }
    }

    static string[] suruculer = new string[] {
        "iqvw64e", "dbutil", "gdrv", "asrdrv", "rtcore64", "winio", "physmem",
        "capcom", "atszio", "kdmapper", "drvmap", "spoof", "hwid"
    };

    static bool SurucuEslesti(string metin)
    {
        return SurucuSeviye(metin) > 0;
    }

    static void SpooferKontrol()
    {
        Baslik("HWID spoofer kontrolü");
        DateTime kur = KurulumTarihi();
        Yaz("Windows kurulum tarihi: " + (kur == DateTime.MinValue ? "bilinmiyor" : kur.ToString()));

        KimlikKontrol("MachineGuid", @"SOFTWARE\Microsoft\Cryptography", "MachineGuid", kur);
        KimlikKontrol("HwProfileGuid", @"SYSTEM\CurrentControlSet\Control\IDConfigDB\Hardware Profiles\0001", "HwProfileGuid", kur);
        KimlikKontrol("SQM MachineId", @"SOFTWARE\Microsoft\SQMClient", "MachineId", kur);
        Yaz("(Not: Büyük Windows güncellemeleri bu anahtarları da değiştirebilir, tek başına kanıt sayılmaz.)");

        Yaz("");
        Yaz("C: disk seri no (not alın, kayıtlarla karşılaştırın): " + Calistir("cmd", "/c vol C:").Replace("\r", " ").Replace("\n", " ").Trim());

        // MAC adresi elle değiştirilmiş mi?
        try
        {
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey cls = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"))
            {
                bool macBulundu = false;
                if (cls != null)
                {
                    foreach (string sk in cls.GetSubKeyNames())
                    {
                        int dummy;
                        if (!int.TryParse(sk, out dummy)) continue;
                        using (RegistryKey a = cls.OpenSubKey(sk))
                        {
                            if (a == null) continue;
                            object na = a.GetValue("NetworkAddress");
                            if (na != null && na.ToString().Length > 0)
                            {
                                Yaz("[~] MAC override: " + a.GetValue("DriverDesc") + " -> " + na + "  (MAC elle değiştirilmiş olabilir)");
                                macBulundu = true;
                            }
                        }
                    }
                }
                if (!macBulundu) Yaz("[+] MAC override yok");
            }
        }
        catch (Exception ex) { Yaz("[?] MAC kontrolü: " + ex.Message); }

        // Şüpheli çekirdek sürücüleri (kdmapper / zafiyetli sürücü yöntemi)
        Yaz("");
        try
        {
            bool surucuBulundu = false;
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey svc = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
            {
                foreach (string n in svc.GetSubKeyNames())
                {
                    using (RegistryKey s = svc.OpenSubKey(n))
                    {
                        if (s == null) continue;
                        object tip = s.GetValue("Type");
                        if (tip == null) continue;
                        int t = Convert.ToInt32(tip);
                        if (t != 1 && t != 2) continue; // sadece çekirdek sürücüleri
                        object ip = s.GetValue("ImagePath");
                        string img = (ip == null ? "" : ip.ToString()).ToLowerInvariant();

                        int svs = Math.Max(SurucuSeviye(n), SurucuSeviye(img));
                        if (svs > 0)
                        {
                            Yaz(Etiket(svs) + "Şüpheli sürücü kaydı: " + n + "  -> " + img);
                            surucuBulundu = true;
                        }
                        else if (Icerir(img, "\\users\\", "\\temp\\", "\\appdata\\", "\\downloads\\", "\\desktop\\")
                            && !GuvenilirDosya(SurucuYolu(img)))
                        {
                            Yaz("[~] Kullanıcı dizininden yüklenen sürücü: " + n + "  -> " + img);
                            surucuBulundu = true;
                        }
                    }
                }
            }
            if (!surucuBulundu) Yaz("[+] Şüpheli sürücü kaydı yok");
        }
        catch (Exception ex) { Yaz("[?] Sürücü kontrolü: " + ex.Message); }

        // Yakın zamanda kurulan servis/sürücüler (Olay 7045)
        string o = Calistir("wevtutil", "qe System /q:\"*[System[(EventID=7045)]]\" /c:50 /rd:true /f:xml");
        bool yeniBulundu = false;
        foreach (string blok in o.Split(new string[] { "</Event>" }, StringSplitOptions.RemoveEmptyEntries))
        {
            Match mad = Regex.Match(blok, "<Data Name='ServiceName'>([^<]*)</Data>");
            Match mimg = Regex.Match(blok, "<Data Name='ImagePath'>([^<]*)</Data>");
            Match mzm = Regex.Match(blok, "SystemTime='([^']*)'");
            if (!mad.Success || !mimg.Success) continue;
            string birlesik = mad.Groups[1].Value + " " + mimg.Groups[1].Value;
            int sv7 = SurucuSeviye(birlesik);
            bool kullaniciYolu = Icerir(birlesik.ToLowerInvariant(), "\\temp\\", "\\users\\");
            if (sv7 == 0 && kullaniciYolu && GuvenilirDosya(SurucuYolu(mimg.Groups[1].Value))) kullaniciYolu = false;
            if (sv7 > 0 || kullaniciYolu)
            {
                Yaz(Etiket(sv7 > 0 ? sv7 : 1) + "Yeni servis/sürücü kaydı: " + (mzm.Success ? mzm.Groups[1].Value : "?")
                    + " | " + mad.Groups[1].Value + " | " + mimg.Groups[1].Value);
                yeniBulundu = true;
            }
        }
        if (!yeniBulundu) Yaz("[+] Olay günlüğünde şüpheli yeni servis/sürücü kaydı yok");
    }

    // ================= ÇALIŞAN SÜREÇLER =================
    static void CalisanSurecler()
    {
        Baslik("Çalışan şüpheli süreçler (makro / spoofer / temizleyici)");
        bool b = false;
        foreach (Process p in Process.GetProcesses())
        {
            string ad = p.ProcessName;
            int sv = Siniflandir(ad);
            if (sv == 0) continue;
            string yol = null;
            try { yol = p.MainModule.FileName; } catch (Exception) { }
            if (yol != null && GuvenilirDosya(yol)) continue;
            Yaz(Etiket(sv) + "Çalışan süreç: " + ad + " (PID " + p.Id + ")" + (yol != null ? "  " + yol : ""));
            b = true;
        }
        if (!b) Yaz("[+] Şüpheli süreç yok");
    }

    // ================= ORTAK YARDIMCILAR =================
    static DateTime FtLocal(long ft)
    {
        try { if (ft <= 0) return DateTime.MinValue; return DateTime.FromFileTime(ft); }
        catch { return DateTime.MinValue; }
    }

    static string Zaman(DateTime d)
    {
        return d == DateTime.MinValue ? "?" : d.ToString("dd.MM.yyyy HH:mm:ss");
    }

    static string DosyaAdi(string yol)
    {
        int i = yol.LastIndexOf('\\');
        return i >= 0 ? yol.Substring(i + 1) : yol;
    }

    static bool KullaniciDizini(string yol)
    {
        return Icerir(yol.ToLowerInvariant(), "\\temp\\", "\\downloads\\", "\\desktop\\", "\\appdata\\", "\\users\\public\\");
    }

    // ================= BAM / DAM =================
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);

    static Dictionary<string, string> aygitHaritasi;

    // \Device\HarddiskVolume3\Users\... -> C:\Users\...
    static string CihazYolunuCoz(string yol)
    {
        if (aygitHaritasi == null)
        {
            aygitHaritasi = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    string harf = d.Name.Substring(0, 2);
                    StringBuilder sb = new StringBuilder(512);
                    if (QueryDosDevice(harf, sb, sb.Capacity) != 0) aygitHaritasi[sb.ToString()] = harf;
                }
            }
            catch { }
        }
        foreach (KeyValuePair<string, string> kv in aygitHaritasi)
            if (yol.StartsWith(kv.Key + "\\", StringComparison.OrdinalIgnoreCase))
                return kv.Value + yol.Substring(kv.Key.Length);
        return yol;
    }

    static void BamDam()
    {
        Baslik("BAM/DAM (çalıştırılan programlar + son çalışma zamanı)");
        string[] kokler = new string[] {
            @"SYSTEM\CurrentControlSet\Services\bam\State\UserSettings",
            @"SYSTEM\CurrentControlSet\Services\bam\UserSettings",
            @"SYSTEM\CurrentControlSet\Services\dam\State\UserSettings",
            @"SYSTEM\CurrentControlSet\Services\dam\UserSettings"
        };
        int toplam = 0, bulgu = 0, silinmisSayac = 0;
        bool anahtarVar = false;
        try
        {
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                foreach (string kok in kokler)
                {
                    using (RegistryKey k = hklm.OpenSubKey(kok))
                    {
                        if (k == null) continue;
                        anahtarVar = true;
                        string etiket = kok.Contains("\\bam\\") ? "BAM" : "DAM";
                        foreach (string sid in k.GetSubKeyNames())
                        {
                            using (RegistryKey u = k.OpenSubKey(sid))
                            {
                                if (u == null) continue;
                                foreach (string ad in u.GetValueNames())
                                {
                                    byte[] veri = u.GetValue(ad) as byte[];
                                    if (veri == null || veri.Length < 8) continue;
                                    toplam++;
                                    DateTime t = FtLocal(BitConverter.ToInt64(veri, 0));
                                    string yol = CihazYolunuCoz(ad);
                                    bool var = File.Exists(yol);
                                    int svb = Siniflandir(DosyaAdi(yol));
                                    if (svb > 0 && !GuvenilirYol(yol) && !(var && GuvenilirDosya(yol)))
                                    {
                                        Yaz(Etiket(svb) + etiket + ": " + (var ? "" : "(SİLİNMİŞ) ") + yol + "  | son çalışma: " + Zaman(t));
                                        bulgu++;
                                    }
                                    else if (!var && yol.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && KullaniciDizini(yol))
                                    {
                                        silinmisSayac++;
                                        if (silinmisSayac <= 25)
                                            Yaz("[~] " + etiket + ": silinmiş exe (kullanıcı dizini): " + yol + "  | son çalışma: " + Zaman(t));
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) { Yaz("[?] BAM/DAM okunamadı: " + ex.Message); return; }

        if (!anahtarVar) { Yaz("[?] BAM/DAM anahtarı yok (Windows 10 1709 öncesi olabilir)"); return; }
        Yaz("Toplam BAM/DAM kaydı: " + toplam);
        if (silinmisSayac > 25) Yaz("[~] ... ve " + (silinmisSayac - 25) + " silinmiş exe daha (liste kısaltıldı)");
        if (bulgu == 0 && silinmisSayac == 0) Yaz("[+] Ad bazlı şüpheli BAM/DAM kaydı yok");
        Yaz("(Not: BAM'de zaman, programın SON çalıştırılma zamanıdır. Silinmiş exe = disk üzerinde artık yok.)");
    }

    // ================= SHIMCACHE (AppCompatCache) =================
    static void ShimCache()
    {
        Baslik("ShimCache (AppCompatCache)");
        try
        {
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey k = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\AppCompatCache"))
            {
                if (k == null) { Yaz("[?] AppCompatCache anahtarı yok"); return; }
                byte[] d = k.GetValue("AppCompatCache") as byte[];
                if (d == null || d.Length < 0x40) { Yaz("[?] AppCompatCache verisi okunamadı"); return; }

                // Windows 10/11: girişler "10ts" imzasıyla başlar
                int pos = -1;
                for (int i = 0; i < 0x100 && i + 4 <= d.Length; i++)
                    if (d[i] == '1' && d[i + 1] == '0' && d[i + 2] == 't' && d[i + 3] == 's') { pos = i; break; }
                if (pos < 0) { Yaz("[?] Desteklenmeyen ShimCache biçimi (yalnızca Windows 10/11)"); return; }

                int sira = 0, bulgu = 0, silinmis = 0;
                while (pos + 14 <= d.Length)
                {
                    if (!(d[pos] == '1' && d[pos + 1] == '0' && d[pos + 2] == 't' && d[pos + 3] == 's')) break;
                    int boyut = BitConverter.ToInt32(d, pos + 8);
                    int yolUz = BitConverter.ToUInt16(d, pos + 12);
                    if (boyut < 0 || pos + 12 + boyut > d.Length || 2 + yolUz + 8 > boyut) break;

                    string yol = Encoding.Unicode.GetString(d, pos + 14, yolUz);
                    DateTime mod = FtLocal(BitConverter.ToInt64(d, pos + 14 + yolUz));
                    pos += 12 + boyut;
                    sira++;

                    bool var = false;
                    try { var = File.Exists(yol); } catch { }

                    int svh = Siniflandir(DosyaAdi(yol));
                    if (svh > 0 && !GuvenilirYol(yol) && !(var && GuvenilirDosya(yol)))
                    {
                        Yaz(Etiket(svh) + "ShimCache #" + sira + ": " + (var ? "" : "(SİLİNMİŞ) ") + yol + "  | dosya değişiklik zamanı: " + Zaman(mod));
                        bulgu++;
                    }
                    else if (!var && yol.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && KullaniciDizini(yol))
                    {
                        silinmis++;
                        if (silinmis <= 25)
                            Yaz("[~] ShimCache #" + sira + ": silinmiş exe (kullanıcı dizini): " + yol + "  | dosya değişiklik zamanı: " + Zaman(mod));
                    }
                }
                Yaz("Toplam ShimCache girişi: " + sira + "  (#1 = en yeni)");
                if (sira == 0) IzYaz("[~] ShimCache boş, temizlenmiş olabilir");
                if (bulgu == 0 && silinmis == 0) Yaz("[+] Ad bazlı şüpheli ShimCache kaydı yok");
                Yaz("(Not: ShimCache'teki zaman ÇALIŞMA zamanı değil, dosyanın son değişiklik zamanıdır. Kayıt defterine genelde kapanışta yazılır; yeni kayıtlar için yeniden başlatma gerekebilir.)");
            }
        }
        catch (Exception ex) { Yaz("[?] ShimCache: " + ex.Message); }
    }

    // ================= AMCACHE =================
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegLoadAppKey(string hiveFile, out IntPtr hKey, int samDesired, int options, int reserved);

    static void AmCache()
    {
        Baslik("AmCache");
        string kaynak = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\appcompat\\Programs\\Amcache.hve";
        string gecici = Path.Combine(Path.GetTempPath(), "cs_ss_amcache_" + Guid.NewGuid().ToString("N") + ".hve");
        try
        {
            // Dosya sistem tarafından kilitli; gölge kopya (VSS) ile kopyalanır (yönetici gerekir)
            string c = Calistir("esentutl", "/y \"" + kaynak + "\" /vss /d \"" + gecici + "\"");
            if (!File.Exists(gecici))
            {
                Yaz("[?] Amcache.hve kopyalanamadı (yönetici olarak çalıştırın). Elle: AmcacheParser ile bakın.");
                return;
            }
            // Günlük dosyaları varsa yanına kopyala (yeni kayıtların yüklenmesi için)
            Calistir("esentutl", "/y \"" + kaynak + ".LOG1\" /vss /d \"" + gecici + ".LOG1\"");
            Calistir("esentutl", "/y \"" + kaynak + ".LOG2\" /vss /d \"" + gecici + ".LOG2\"");

            IntPtr h;
            int r = RegLoadAppKey(gecici, out h, 0x20019, 0, 0);
            if (r != 0) { Yaz("[?] Amcache hive yüklenemedi (hata " + r + ")"); return; }

            using (SafeRegistryHandle sh = new SafeRegistryHandle(h, true))
            using (RegistryKey kok = RegistryKey.FromHandle(sh, RegistryView.Default))
            {
                int bulgu = 0, toplam = 0;
                using (RegistryKey app = kok.OpenSubKey(@"Root\InventoryApplicationFile"))
                {
                    if (app == null) Yaz("[?] InventoryApplicationFile yok (Windows 10/11 biçimi bekleniyor)");
                    else
                    {
                        foreach (string ad in app.GetSubKeyNames())
                        {
                            using (RegistryKey e = app.OpenSubKey(ad))
                            {
                                if (e == null) continue;
                                toplam++;
                                object p = e.GetValue("LowerCaseLongPath");
                                string yol = p == null ? ad : p.ToString();
                                int svm = Siniflandir(DosyaAdi(yol));
                                if (svm > 0 && !GuvenilirYol(yol))
                                {
                                    DateTime t = AnahtarSonYazma(e) ?? DateTime.MinValue;
                                    bool var = false;
                                    try { var = File.Exists(yol); } catch { }
                                    if (!(var && GuvenilirDosya(yol)))
                                    {
                                        Yaz(Etiket(svm) + "AmCache: " + (var ? "" : "(SİLİNMİŞ) ") + yol + "  | kayıt zamanı: " + Zaman(t));
                                        bulgu++;
                                    }
                                }
                            }
                        }
                    }
                }
                using (RegistryKey drv = kok.OpenSubKey(@"Root\InventoryDriverBinary"))
                {
                    if (drv != null)
                    {
                        foreach (string ad in drv.GetSubKeyNames())
                        {
                            int svd = SurucuSeviye(ad);
                            if (svd > 0)
                            {
                                using (RegistryKey e = drv.OpenSubKey(ad))
                                {
                                    DateTime t = (e == null ? null : AnahtarSonYazma(e)) ?? DateTime.MinValue;
                                    Yaz(Etiket(svd) + "AmCache sürücü kaydı: " + ad + "  | kayıt zamanı: " + Zaman(t));
                                    bulgu++;
                                }
                            }
                        }
                    }
                }
                Yaz("AmCache'te incelenen dosya kaydı: " + toplam);
                if (bulgu == 0) Yaz("[+] Ad bazlı şüpheli AmCache kaydı yok");
                Yaz("(Not: AmCache kayıt zamanı yaklaşık ilk çalıştırma/ilk görülme zamanıdır; kopya, kapanmamış son kayıtları içermeyebilir.)");
            }
        }
        catch (Exception ex) { Yaz("[?] AmCache: " + ex.Message); }
        finally
        {
            try { File.Delete(gecici); } catch { }
            try { File.Delete(gecici + ".LOG1"); } catch { }
            try { File.Delete(gecici + ".LOG2"); } catch { }
        }
    }

    // ================= USN GÜNLÜĞÜ OKUMA =================
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize,
        byte[] outBuf, int outSize, out int returned, IntPtr ovl);

    static void UsnOku()
    {
        Baslik("USN günlüğü içeriği (silinen / yeniden adlandırılan dosyalar)");
        SafeFileHandle h = CreateFile(@"\\.\C:", 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            Yaz("[?] C: sürücüsü açılamadı (yönetici olarak çalıştırın), hata: " + Marshal.GetLastWin32Error());
            return;
        }
        using (h)
        {
            byte[] jd = new byte[80];
            int ret;
            if (!DeviceIoControl(h, 0x90073, null, 0, jd, jd.Length, out ret, IntPtr.Zero))
            {
                int hata = Marshal.GetLastWin32Error();
                if (hata == 1179) IzYaz("[~] USN günlüğü etkin değil / silinmiş (hata 1179)");
                else Yaz("[?] USN günlüğü sorgulanamadı, hata: " + hata);
                return;
            }
            ulong id = BitConverter.ToUInt64(jd, 0);
            long baslangicUsn = BitConverter.ToInt64(jd, 8);

            byte[] girdi = new byte[40];
            byte[] tampon = new byte[1 << 20];
            long kayit = 0;
            long enEski = 0, enYeni = 0;
            int pfSilinen = 0; DateTime pfSon = DateTime.MinValue;
            HashSet<string> gorulen = new HashSet<string>();
            List<string> supheli = new List<string>();
            List<string> silinenExe = new List<string>();
            int dongu = 0;

            while (dongu++ < 5000)
            {
                BitConverter.GetBytes(baslangicUsn).CopyTo(girdi, 0);
                BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(girdi, 8);   // tüm nedenler
                BitConverter.GetBytes(0u).CopyTo(girdi, 12);           // sadece kapanışlar değil
                BitConverter.GetBytes(0L).CopyTo(girdi, 16);           // timeout
                BitConverter.GetBytes(0L).CopyTo(girdi, 24);           // beklenecek bayt
                BitConverter.GetBytes(id).CopyTo(girdi, 32);

                if (!DeviceIoControl(h, 0x900bb, girdi, girdi.Length, tampon, tampon.Length, out ret, IntPtr.Zero)) break;
                if (ret <= 8) break;
                long sonraki = BitConverter.ToInt64(tampon, 0);

                int o = 8;
                while (o + 60 <= ret)
                {
                    int len = BitConverter.ToInt32(tampon, o);
                    if (len <= 0 || o + len > ret) break;
                    ushort major = BitConverter.ToUInt16(tampon, o + 4);
                    if (major == 2)
                    {
                        long ts = BitConverter.ToInt64(tampon, o + 32);
                        uint neden = BitConverter.ToUInt32(tampon, o + 40);
                        int nl = BitConverter.ToUInt16(tampon, o + 56);
                        int no = BitConverter.ToUInt16(tampon, o + 58);
                        if (no + nl <= len)
                        {
                            string ad = Encoding.Unicode.GetString(tampon, o + no, nl);
                            kayit++;
                            if (enEski == 0 || ts < enEski) enEski = ts;
                            if (ts > enYeni) enYeni = ts;

                            bool sil = (neden & 0x200) != 0;        // FILE_DELETE
                            bool eskiAd = (neden & 0x1000) != 0;    // RENAME_OLD_NAME
                            if (sil || eskiAd)
                            {
                                string uz = Path.GetExtension(ad).ToLowerInvariant();
                                DateTime t = FtLocal(ts);
                                string tur = sil ? "silindi" : "adı değişti (eski ad)";
                                string anahtar = ad + "|" + tur;

                                if (sil && uz == ".pf") { pfSilinen++; if (t > pfSon) pfSon = t; }

                                int svu = Siniflandir(ad);
                                if (svu > 0)
                                {
                                    if (gorulen.Add(anahtar))
                                        supheli.Add(Etiket(svu) + "USN: " + ad + "  " + tur + "  | " + Zaman(t));
                                }
                                else if (sil && (uz == ".exe" || uz == ".dll" || uz == ".sys" || uz == ".bat" || uz == ".ps1"))
                                {
                                    if (gorulen.Add(anahtar))
                                    {
                                        silinenExe.Add("[~] USN: " + ad + "  silindi  | " + Zaman(t));
                                        if (silinenExe.Count > 200) silinenExe.RemoveAt(0);
                                    }
                                }
                            }
                        }
                    }
                    o += len;
                }
                if (sonraki <= baslangicUsn) break;
                baslangicUsn = sonraki;
            }

            Yaz("İncelenen USN kaydı: " + kayit);
            if (kayit > 0)
            {
                Yaz("Günlük kapsamı: " + Zaman(FtLocal(enEski)) + "  ->  " + Zaman(FtLocal(enYeni)));
                if ((DateTime.Now - FtLocal(enEski)).TotalHours < 3)
                    IzYaz("[~] Günlük sadece birkaç saatlik; yakın zamanda sıfırlanmış olabilir (veya sistem çok yoğun)");
            }
            else Yaz("[~] USN günlüğünde hiç kayıt okunamadı");

            if (pfSilinen > 0)
                IzYaz("[~] Silinen .pf (Prefetch) dosyası: " + pfSilinen + " adet, en son: " + Zaman(pfSon)
                    + "  (Windows eski kayıtları kendisi de siler; toplu silme/yakın zamanlı ise şüpheli)");

            if (supheli.Count == 0) Yaz("[+] Ad bazlı şüpheli silinen/yeniden adlandırılan dosya yok");
            foreach (string s in supheli.GetRange(Math.Max(0, supheli.Count - 40), Math.Min(40, supheli.Count))) Yaz(s);

            if (silinenExe.Count > 0)
            {
                Yaz("Son silinen çalıştırılabilir dosyalar (en fazla 20):");
                foreach (string s in silinenExe.GetRange(Math.Max(0, silinenExe.Count - 20), Math.Min(20, silinenExe.Count))) Yaz(s);
            }
        }
    }

    // ================= PREFETCH AYRIŞTIRMA =================
    [DllImport("ntdll.dll")]
    static extern uint RtlGetCompressionWorkSpaceSize(ushort format, out uint compressWs, out uint fragmentWs);

    [DllImport("ntdll.dll")]
    static extern uint RtlDecompressBufferEx(ushort format, byte[] uncompressed, uint uncompressedSize,
        byte[] compressed, uint compressedSize, out uint finalSize, byte[] workspace);

    class PfBilgi
    {
        public string Ad;
        public int Sayi;
        public bool Ok;
        public string Hata;
        public List<DateTime> Zamanlar = new List<DateTime>();
    }

    static byte[] PfAc(string yol)
    {
        byte[] ham = File.ReadAllBytes(yol);
        if (ham.Length > 8 && ham[0] == 'M' && ham[1] == 'A' && ham[2] == 'M')
        {
            uint boyut = BitConverter.ToUInt32(ham, 4);
            if (boyut == 0 || boyut > 64 * 1024 * 1024) return null;
            uint ws, frag;
            if (RtlGetCompressionWorkSpaceSize(4, out ws, out frag) != 0) return null;  // 4 = XPRESS_HUFF
            byte[] cikti = new byte[boyut];
            byte[] calisma = new byte[frag];
            byte[] sik = new byte[ham.Length - 8];
            Array.Copy(ham, 8, sik, 0, sik.Length);
            uint son;
            if (RtlDecompressBufferEx(4, cikti, boyut, sik, (uint)sik.Length, out son, calisma) != 0) return null;
            return cikti;
        }
        return ham;
    }

    static PfBilgi PfOku(string yol)
    {
        PfBilgi b = new PfBilgi();
        try
        {
            byte[] d = PfAc(yol);
            if (d == null || d.Length < 216) { b.Hata = "açılamadı/açılımı başarısız"; return b; }
            if (d[4] != 'S' || d[5] != 'C' || d[6] != 'C' || d[7] != 'A') { b.Hata = "SCCA imzası yok"; return b; }
            uint ver = BitConverter.ToUInt32(d, 0);
            if (ver != 26 && ver != 30 && ver != 31) { b.Hata = "desteklenmeyen sürüm " + ver; return b; }

            b.Ad = Encoding.Unicode.GetString(d, 16, 60).Split('\0')[0];
            for (int i = 0; i < 8; i++)
            {
                long ft = BitConverter.ToInt64(d, 128 + i * 8);
                if (ft > 0) b.Zamanlar.Add(FtLocal(ft));
            }
            b.Zamanlar.Sort();
            b.Zamanlar.Reverse();   // en yeni başta

            int say = BitConverter.ToInt32(d, ver == 26 ? 208 : 200);
            if (ver != 26 && (say < 1 || say > 100000 || say < b.Zamanlar.Count))
                say = BitConverter.ToInt32(d, 208);   // bazı Win10 sürümlerinde farklı konum
            b.Sayi = say;
            b.Ok = true;
        }
        catch (Exception ex) { b.Hata = ex.Message; }
        return b;
    }

    static void PrefetchAyristir()
    {
        Baslik("Prefetch ayrıştırma (son 8 çalıştırma zamanı)");
        string yol = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\Prefetch";
        string[] temizlikAdlari = new string[] {
            "FSUTIL.EXE-", "WEVTUTIL.EXE-", "CIPHER.EXE-", "SDELETE", "CCLEANER", "BLEACHBIT", "PRIVAZER"
        };
        string[] dosyalar;
        try { dosyalar = Directory.GetFiles(yol, "*.pf"); }
        catch { Yaz("[?] Prefetch okunamadı"); return; }

        int bulundu = 0;
        foreach (string f in dosyalar)
        {
            string ad = Path.GetFileName(f).ToUpperInvariant();
            bool temizlik = false;
            foreach (string t in temizlikAdlari) if (ad.StartsWith(t)) { temizlik = true; break; }
            int sv = Siniflandir(ad);
            bool supheli = sv > 0;
            if (!temizlik && !supheli) continue;

            PfBilgi b = PfOku(f);
            if (!b.Ok) { Yaz("[?] " + ad + " ayrıştırılamadı: " + b.Hata); continue; }

            // Bu aracın kendi başlattığı fsutil/wevtutil çalıştırmalarını ele
            List<DateTime> eski = new List<DateTime>();
            foreach (DateTime z in b.Zamanlar)
                if (z < baslangic.AddSeconds(-5)) eski.Add(z);
            if (eski.Count == 0 && !supheli) continue;

            StringBuilder sb = new StringBuilder();
            foreach (DateTime z in eski) { if (sb.Length > 0) sb.Append(" | "); sb.Append(Zaman(z)); }
            bool son24 = eski.Count > 0 && (DateTime.Now - eski[0]).TotalHours < 24;
            // fsutil/wevtutil/cipher Windows'un kendi araçlarıdır: yalnızca son 24 saatte çalıştırılmışsa göster
            bool sistemAraci = ad.StartsWith("FSUTIL.EXE-") || ad.StartsWith("WEVTUTIL.EXE-") || ad.StartsWith("CIPHER.EXE-");
            if (sistemAraci && !supheli && !son24) continue;

            Yaz((supheli ? Etiket(sv) : "[~] ") + ad + "  (toplam çalıştırma: " + b.Sayi + ", bu aracın kendi çalıştırmaları dahil olabilir)");
            Yaz("      Son çalıştırmalar: " + sb + (son24 ? "   <- son 24 saat içinde" : ""));
            bulundu++;
        }
        if (bulundu == 0) Yaz("[+] Temizlik/şüpheli araçlar için eski Prefetch kaydı yok");
        else Yaz("(Not: fsutil/wevtutil Windows'ta normal de kullanılır; zamanı USN/olay günlüğü bulgularıyla karşılaştırın.)");
    }

    // ================= DOSYA BÜTÜNLÜĞÜ (temiz_hash.txt) =================
    // Kayıt biçimi: yol|boyut|sha256   (yol: oyun ana klasörüne göre, küçük harf, / ile)
    static string[] hashAtlaDizinler = new string[] { "logs", "save", "demos", "downloaded", "maps", "addons", "custom", "screenshots" };
    static string[] hashAtlaUzantilar = new string[] { ".cfg", ".log", ".dem", ".lst", ".tmp", ".dat", ".rc", ".scr", ".sav", ".bak", ".ini" };
    // Değiştirilmişse KIRMIZI olmasını istediğiniz dosyalar (varsayılan: hepsi sarı). Örn: "cstrike/halflife.wad"
    static string[] baselineKirmizi = new string[] { };

    static string Goreli(string kok, string f)
    {
        return f.Substring(kok.Length).TrimStart('\\', '/').Replace('\\', '/').ToLowerInvariant();
    }

    static bool HashAtla(string rel)
    {
        string[] parca = rel.Split('/');
        for (int i = 0; i < parca.Length - 1; i++)
            if (Array.IndexOf(hashAtlaDizinler, parca[i]) >= 0) return true;
        return Array.IndexOf(hashAtlaUzantilar, Path.GetExtension(rel)) >= 0;
    }

    static string DosyaHash(string yol)
    {
        using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
        using (FileStream fs = new FileStream(yol, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            byte[] h = sha.ComputeHash(fs);
            StringBuilder sb = new StringBuilder();
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    // Yönetici içindir: TEMİZ bir kurulumdan liste üretir (--hash-olustur) veya listeye başka sürüm ekler (--hash-ekle)
    internal static string HashListesiYaz(string kokGirdi, bool ekle)
    {
        string kok = KokKlasor(kokGirdi);
        if (string.IsNullOrEmpty(kok) || !Directory.Exists(kok)) return "Klasör bulunamadı: " + kokGirdi;
        if (!Directory.Exists(Path.Combine(kok, "cstrike")) && !File.Exists(Path.Combine(kok, "hl.exe")))
            return "Bu klasör Half-Life / CS 1.6 klasörüne benzemiyor (hl.exe veya cstrike yok):\n" + kok;

        string hedef = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temiz_hash.txt");
        HashSet<string> kayitli = new HashSet<string>();
        List<string> satirlar = new List<string>();
        if (ekle && File.Exists(hedef))
        {
            foreach (string l in File.ReadAllLines(hedef, Encoding.UTF8))
                if (l.Length > 0 && l[0] != '#' && kayitli.Add(l)) satirlar.Add(l);
        }

        int yeni = 0, atlanan = 0;
        foreach (string f in Directory.GetFiles(kok, "*.*", SearchOption.AllDirectories))
        {
            string rel = Goreli(kok, f);
            if (HashAtla(rel)) continue;
            try
            {
                string satir = rel + "|" + new FileInfo(f).Length + "|" + DosyaHash(f);
                if (kayitli.Add(satir)) { satirlar.Add(satir); yeni++; }
            }
            catch (Exception) { atlanan++; }
        }

        List<string> cikti = new List<string>();
        cikti.Add("# LCAPRO temiz dosya listesi (yol|boyut|sha256). Elle değiştirmeyin.");
        cikti.AddRange(satirlar);
        File.WriteAllLines(hedef, cikti.ToArray(), Encoding.UTF8);

        return (ekle ? "Listeye eklendi: " : "Liste oluşturuldu: ") + hedef
            + "\n\nToplam kayıt: " + satirlar.Count + "   (bu seferki yeni: " + yeni + ", okunamayan: " + atlanan + ")"
            + "\n\nListenin SHA256 değeri (oyunculara verirken not alın):\n" + DosyaHash(hedef);
    }

    static void BaselineKarsilastir(string klasor)
    {
        Baslik("Dosya bütünlüğü (temiz_hash.txt ile karşılaştırma)");
        if (klasor == null) { Yaz("[?] Oyun klasörü bulunamadı."); return; }
        string kok = KokKlasor(klasor);
        string liste = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temiz_hash.txt");
        if (!File.Exists(liste)) { Yaz("[?] temiz_hash.txt bulunamadı: dosya bütünlüğü karşılaştırması atlandı." + (AdminModu ? " Üstteki 'Temiz Liste Oluştur' düğmesiyle oluşturabilirsiniz." : "")); return; }

        Dictionary<string, List<KeyValuePair<long, string>>> kayit = new Dictionary<string, List<KeyValuePair<long, string>>>();
        int say = 0;
        foreach (string l in File.ReadAllLines(liste, Encoding.UTF8))
        {
            if (l.Length == 0 || l[0] == '#') continue;
            string[] p = l.Split('|');
            long b;
            if (p.Length != 3 || !long.TryParse(p[1], out b)) continue;
            List<KeyValuePair<long, string>> girdiListe;
            if (!kayit.TryGetValue(p[0], out girdiListe))
            {
                girdiListe = new List<KeyValuePair<long, string>>();
                kayit[p[0]] = girdiListe;
            }
            girdiListe.Add(new KeyValuePair<long, string>(b, p[2].Trim().ToLowerInvariant()));
            say++;
        }
        Yaz("Karşılaştırma listesi: " + say + " kayıt, SHA256: " + DosyaHash(liste));
        if (say == 0) { Yaz("[?] Liste boş veya okunamadı."); return; }

        int ayni = 0, degisen = 0, fazla = 0, fazlaDiger = 0, okunamayan = 0;
        const int Sinir = 40;
        try
        {
            foreach (string f in Directory.GetFiles(kok, "*.*", SearchOption.AllDirectories))
            {
                string rel = Goreli(kok, f);
                if (HashAtla(rel)) continue;
                string uz = Path.GetExtension(rel);
                bool calistirilabilir = (uz == ".dll" || uz == ".exe" || uz == ".asi");
                List<KeyValuePair<long, string>> girdi;

                if (!kayit.TryGetValue(rel, out girdi))
                {
                    if (calistirilabilir)
                    {
                        if (GuvenilirDosya(f)) continue;
                        fazla++;
                        if (fazla <= Sinir) Yaz("[~] Temiz listede olmayan çalıştırılabilir dosya: " + rel);
                    }
                    else fazlaDiger++;
                    continue;
                }

                try
                {
                    long boyut = new FileInfo(f).Length;
                    bool boyutVar = false, eslesti = false;
                    foreach (KeyValuePair<long, string> kv in girdi)
                        if (kv.Key == boyut) boyutVar = true;
                    if (boyutVar)
                    {
                        string h = DosyaHash(f);
                        foreach (KeyValuePair<long, string> kv2 in girdi)
                            if (kv2.Key == boyut && kv2.Value == h) eslesti = true;
                    }
                    if (eslesti) { ayni++; continue; }
                    // Valve/Microsoft imzalı exe/dll: büyük olasılıkla oyun sürüm farkı, sayma
                    if ((uz == ".dll" || uz == ".exe") && GuvenilirDosya(f)) continue;

                    degisen++;
                    bool kirmizi = Array.IndexOf(baselineKirmizi, rel) >= 0;
                    if (kirmizi || degisen <= Sinir)
                        Yaz((kirmizi ? "[!] " : "[~] ") + "Temiz listeden farklı dosya: " + rel
                            + (boyutVar ? " (içerik farklı)" : " (boyut farklı)"));
                }
                catch (Exception) { okunamayan++; }
            }
        }
        catch (Exception ex) { Yaz("[?] Dosya bütünlüğü taraması yarım kaldı: " + ex.Message); }

        if (fazla > Sinir) Yaz("[~] ... ve " + (fazla - Sinir) + " listede olmayan çalıştırılabilir dosya daha");
        if (degisen > Sinir) Yaz("[~] ... ve " + (degisen - Sinir) + " temiz listeden farklı dosya daha (liste kısaltıldı)");
        if (degisen == 0 && fazla == 0) Yaz("[+] Temiz listeden farklı veya listede olmayan çalıştırılabilir dosya yok");
        Yaz(ayni + " dosya temiz kayıtla birebir aynı"
            + (fazlaDiger > 0 ? "; " + fazlaDiger + " dosya listede yok (özel model/ses/harita olabilir, sayılmadı)" : "")
            + (okunamayan > 0 ? "; " + okunamayan + " dosya okunamadı" : ""));
        Yaz("(Not: Steam / Steam dışı sürüm farkları için listeye --hash-ekle ile ikinci sürüm eklenebilir.)");
    }

    // ================= YENİ KONTROLLER =================

    // Dosyayı parça parça okuyup bayt desenlerini arar (dosyanın içeriği rapora YAZILMAZ)
    static bool[] DosyaTara(string yol, string[] kaliplar)
    {
        bool[] bulundu = new bool[kaliplar.Length];
        const int Parca = 1 << 20;
        const int Ort = 64;
        Encoding enc = Encoding.GetEncoding("iso-8859-1");
        using (FileStream fs = new FileStream(yol, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            byte[] tampon = new byte[Parca + Ort];
            int kalan = 0;
            long toplam = 0;
            int n;
            while (toplam < 200L * 1024 * 1024 && (n = fs.Read(tampon, kalan, Parca)) > 0)
            {
                int uzun = kalan + n;
                string metin = enc.GetString(tampon, 0, uzun);
                for (int i = 0; i < kaliplar.Length; i++)
                    if (!bulundu[i] && metin.IndexOf(kaliplar[i], StringComparison.Ordinal) >= 0) bulundu[i] = true;
                toplam += n;
                kalan = Math.Min(Ort, uzun);
                Buffer.BlockCopy(tampon, uzun - kalan, tampon, 0, kalan);
            }
        }
        return bulundu;
    }

    // Logitech G HUB / Gaming Software Lua betikleri (klasik no-recoil yöntemi) ve .ahk dosyaları
    static void MakroBetikTara()
    {
        Baslik("Makro / no-recoil betikleri (Logitech, AutoHotkey)");
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        List<string> adaylar = new List<string>();
        string gh = Path.Combine(local, @"LGHUB\settings.db");
        if (File.Exists(gh)) adaylar.Add(gh);
        string lgs = Path.Combine(local, @"Logitech\Logitech Gaming Software\profiles");
        if (Directory.Exists(lgs))
        {
            try { adaylar.AddRange(Directory.GetFiles(lgs, "*.xml")); } catch (Exception) { }
        }

        string[] kalip = new string[] { "MoveMouseRelative", "IsMouseButtonPressed", "Sleep(" };
        if (adaylar.Count == 0) Yaz("Logitech G HUB / Gaming Software profili bulunamadı (kullanılmıyor olabilir)");
        int temiz = 0;
        foreach (string f in adaylar)
        {
            try
            {
                bool[] b = DosyaTara(f, kalip);
                if (b[0] && b[1] && b[2])
                    Yaz("[!] No-recoil makro betiği belirtisi (MoveMouseRelative + IsMouseButtonPressed + Sleep): " + f);
                else if (b[0])
                    Yaz("[~] Fare hareketi üreten Logitech betiği (MoveMouseRelative): " + f);
                else temiz++;
            }
            catch (Exception ex) { Yaz("[?] " + Path.GetFileName(f) + " okunamadı: " + ex.Message); }
        }
        if (temiz > 0) Yaz("[+] " + temiz + " Logitech profil dosyasında no-recoil betiği belirtisi yok");

        string[] klasorler = new string[] {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        int ahk = 0;
        foreach (string d in klasorler)
        {
            try
            {
                foreach (string f in Directory.GetFiles(d, "*.ahk", SearchOption.TopDirectoryOnly))
                {
                    Yaz("[~] AutoHotkey betiği: " + f);
                    ahk++;
                }
            }
            catch (Exception) { }
        }
        if (ahk == 0) Yaz("[+] Masaüstü / İndirilenler / Belgeler klasörlerinde .ahk betiği yok");
    }

    // Defender: dışlamalar ve gerçek zamanlı koruma
    static void DefenderKontrol()
    {
        Baslik("Windows Defender: dışlamalar ve gerçek zamanlı koruma");
        try
        {
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                int toplam = 0;
                foreach (string alt in new string[] { "Paths", "Processes", "Extensions" })
                {
                    using (RegistryKey k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Exclusions\" + alt))
                    {
                        if (k == null) continue;
                        foreach (string ad in k.GetValueNames())
                        {
                            toplam++;
                            string kk = ad.ToLowerInvariant();
                            bool riskli = KullaniciDizini(kk) || kk.Contains("cstrike") || kk.Contains("half-life")
                                || kk.Contains("counter-strike") || Siniflandir(ad) > 0;
                            Yaz((riskli ? "[~] " : "") + "Defender dışlaması (" + alt + "): " + ad);
                        }
                    }
                }
                if (toplam == 0) Yaz("[+] Okunabilen Defender dışlaması yok");

                using (RegistryKey rt = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Real-Time Protection"))
                {
                    object v = rt == null ? null : rt.GetValue("DisableRealtimeMonitoring");
                    if (v == null) Yaz("[?] Defender gerçek zamanlı koruma durumu okunamadı");
                    else if (Convert.ToInt32(v) == 1) Yaz("[?] Defender gerçek zamanlı koruması kapalı (başka bir antivirüs kullanılıyor olabilir)");
                    else Yaz("[+] Defender gerçek zamanlı koruması açık");
                }
            }
        }
        catch (Exception ex) { Yaz("[?] Defender ayarları okunamadı: " + ex.Message); }
    }

    // Sürücü imza zorunluluğu: test modu ve Secure Boot
    static void ImzaAyarKontrol()
    {
        Baslik("Sürücü imza ayarları (test modu / Secure Boot)");
        string o = Calistir("bcdedit", "/enum {current}");
        if (o.IndexOf("identifier", StringComparison.OrdinalIgnoreCase) < 0)
        {
            Yaz("[?] bcdedit okunamadı (yönetici olarak çalıştırın)");
        }
        else
        {
            bool bulundu = false;
            foreach (string satir in o.Split('\n'))
            {
                string l = satir.Trim().ToLowerInvariant();
                if (l.StartsWith("testsigning") || l.StartsWith("nointegritychecks"))
                {
                    if (l.EndsWith("yes") || l.EndsWith("evet"))
                    {
                        Yaz("[~] " + satir.Trim() + "  -> sürücü imza zorunluluğu gevşetilmiş (imzasız sürücü yükleyen araçlar için gerekir)");
                        bulundu = true;
                    }
                }
            }
            if (!bulundu) Yaz("[+] Test imzalama modu kapalı");
        }

        try
        {
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey k = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
            {
                object v = k == null ? null : k.GetValue("UEFISecureBootEnabled");
                if (v == null) Yaz("[?] Secure Boot durumu okunamadı (eski BIOS olabilir)");
                else if (Convert.ToInt32(v) == 1) Yaz("[+] Secure Boot açık");
                else Yaz("[?] Secure Boot kapalı (birçok oyuncuda normaldir)");
            }
        }
        catch (Exception ex) { Yaz("[?] Secure Boot okunamadı: " + ex.Message); }
    }

    // Oyun klasöründe son 24 saatte değişen çalıştırılabilir dosyalar (hileler genelde oyundan hemen önce yüklenir)
    static void SonDegisenler(string klasor)
    {
        Baslik("Oyun klasöründe son 24 saatte değişen .dll / .exe / .asi");
        if (klasor == null) { Yaz("[?] Oyun klasörü bulunamadı."); return; }
        int say = 0;
        try
        {
            foreach (string f in Directory.GetFiles(klasor, "*.*", SearchOption.AllDirectories))
            {
                string uz = Path.GetExtension(f).ToLowerInvariant();
                if (uz != ".dll" && uz != ".exe" && uz != ".asi") continue;
                DateTime zaman = File.GetLastWriteTime(f);
                if (zaman > DateTime.Now || (DateTime.Now - zaman).TotalHours > 24) continue;
                if (GuvenilirDosya(f)) continue;
                Yaz("[~] Son 24 saatte değişmiş: " + f + "  (" + zaman + ")");
                say++;
            }
        }
        catch (Exception ex) { Yaz("[?] Tarama hatası: " + ex.Message); }
        if (say == 0) Yaz("[+] Son 24 saatte değişen şüpheli çalıştırılabilir dosya yok");
    }

    // ================= TARAMA YÜRÜTÜCÜ (arayüz buradan çağırır) =================
    // ================= SINIFLANDIRMA: KIRMIZI = hile, SARI = şüpheli, YEŞİL = temiz =================
    // Kırmızı: doğrudan hile/spoofer adı taşıyanlar (listeyi kendinize göre düzenleyin)
    static string[] hileAdlari = new string[] {
        "aimbot", "wallhack", "triggerbot", "norecoil", "no-recoil", "no_recoil", "speedhack",
        "nhack", "xhack", "cheatengine", "cheat engine", "kdmapper", "spoofer",
        "hwidspoof", "hwid-spoof", "hwid_spoof"
    };

    // Sarı: genel/belirsiz adlar, iz temizleyiciler, makro ve MAC/seri değiştiriciler
    static string[] supheliAdlari = new string[] {
        "cheat", "hack", "inject", "recoil", "hlh", "hwid", "unban", "spoof",
        "macchanger", "tmac", "volumeid",
        "ccleaner", "bleachbit", "privazer", "sdelete", "tracecleaner",
        "autohotkey", "ahk", "bloody"
    };

    static bool AdIcerir(string k, string t)
    {
        if (t.Length <= 4)   // kısa ifadeler yalnızca kelime sınırında eşleşir (ör. "ahk")
            return Regex.IsMatch(k, "(^|[^a-z0-9])" + Regex.Escape(t) + "([^a-z0-9]|$)");
        return k.Contains(t);
    }

    // 0 = temiz, 1 = şüpheli (sarı), 2 = hile (kırmızı)
    static int Siniflandir(string ad)
    {
        if (string.IsNullOrEmpty(ad)) return 0;
        string k = ad.ToLowerInvariant();
        foreach (string t in hileAdlari) if (k.Contains(t)) return 2;
        foreach (string t in supheliAdlari) if (AdIcerir(k, t)) return 1;
        return 0;
    }

    static string Etiket(int sv)
    {
        return sv >= 2 ? "[!] " : "[~] ";
    }

    // ---- Güvenilir dosya mantığı: Windows dosyaları, antivirüsler, tanınmış yayıncılar ----
    static string[] guvenilirYayincilar = new string[] {
        "microsoft", "valve", "nvidia", "advanced micro devices", "intel corporation", "realtek semiconductor",
        "logitech", "razer", "steelseries", "corsair", "google llc", "mozilla corporation", "discord inc",
        "epic games", "adobe", "oracle", "vmware", "apple inc",
        // antivirüs / güvenlik yazılımları
        "kaspersky", "avast", "avg technologies", "norton", "gen digital", "bitdefender", "eset", "mcafee",
        "malwarebytes", "avira", "f-secure", "sophos", "trend micro", "comodo", "panda security", "webroot",
        "emsisoft", "g data", "bullguard", "qihoo 360", "doctor web", "check point"
    };

    [StructLayout(LayoutKind.Sequential)]
    struct WtFileInfo { public uint cbStruct; public IntPtr pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    struct WtData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WtData pWVTData);

    static Dictionary<string, bool> imzaOnbellek = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    // Dosyadaki gömülü dijital imza geçerli mi? (ağa çıkmaz)
    static bool ImzaGecerli(string yol)
    {
        bool sonuc;
        if (imzaOnbellek.TryGetValue(yol, out sonuc)) return sonuc;
        sonuc = false;
        IntPtr yolPtr = IntPtr.Zero, fiPtr = IntPtr.Zero;
        try
        {
            yolPtr = Marshal.StringToHGlobalUni(yol);
            WtFileInfo fi = new WtFileInfo();
            fi.cbStruct = (uint)Marshal.SizeOf(typeof(WtFileInfo));
            fi.pcwszFilePath = yolPtr;
            fiPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WtFileInfo)));
            Marshal.StructureToPtr(fi, fiPtr, false);

            WtData d = new WtData();
            d.cbStruct = (uint)Marshal.SizeOf(typeof(WtData));
            d.dwUIChoice = 2;           // WTD_UI_NONE
            d.fdwRevocationChecks = 0;  // WTD_REVOKE_NONE
            d.dwUnionChoice = 1;        // WTD_CHOICE_FILE
            d.pFile = fiPtr;
            d.dwStateAction = 1;        // WTD_STATEACTION_VERIFY
            d.dwProvFlags = 0x1010;     // CACHE_ONLY_URL_RETRIEVAL | REVOCATION_CHECK_NONE
            Guid eylem = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            uint r = WinVerifyTrust(IntPtr.Zero, ref eylem, ref d);
            sonuc = (r == 0);
            d.dwStateAction = 2;        // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, ref eylem, ref d);
        }
        catch (Exception) { sonuc = false; }
        finally
        {
            if (fiPtr != IntPtr.Zero) Marshal.FreeHGlobal(fiPtr);
            if (yolPtr != IntPtr.Zero) Marshal.FreeHGlobal(yolPtr);
        }
        imzaOnbellek[yol] = sonuc;
        return sonuc;
    }

    static string Yayinci(string yol)
    {
        try { return X509Certificate.CreateFromSignedFile(yol).Subject; }
        catch (Exception) { return null; }
    }

    // Windows klasörü (herkesin yazabildiği alt klasörler hariç) ve Windows Defender klasörleri
    static bool GuvenilirYol(string yol)
    {
        if (string.IsNullOrEmpty(yol)) return false;
        string k = yol.ToLowerInvariant().Replace('/', '\\');
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
        if (k.StartsWith(win + "\\", StringComparison.Ordinal))
        {
            if (k.Contains("\\temp\\") || k.Contains("\\tasks\\") || k.Contains("\\tracing\\") || k.Contains("\\debug\\"))
                return false;
            return true;
        }
        string[] kokler = new string[] {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).ToLowerInvariant(),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).ToLowerInvariant(),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).ToLowerInvariant()
        };
        string[] defender = new string[] {
            "\\windows defender\\", "\\windows defender advanced threat protection\\", "\\microsoft\\windows defender\\"
        };
        foreach (string kok in kokler)
        {
            if (kok.Length == 0) continue;
            foreach (string d in defender)
                if (k.StartsWith(kok + d, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // Güvenilir: Windows/Defender yolu YA DA geçerli imza + tanınmış yayıncı (Microsoft, antivirüsler, Valve, NVIDIA...)
    static bool GuvenilirDosya(string yol)
    {
        if (GuvenilirYol(yol)) return true;
        try
        {
            if (!File.Exists(yol)) return false;
            if (!ImzaGecerli(yol)) return false;
            string y = Yayinci(yol);
            if (y == null) return false;
            y = y.ToLowerInvariant();
            foreach (string t in guvenilirYayincilar)
                if (y.Contains(t)) return true;
        }
        catch (Exception) { }
        return false;
    }

    // UserAssist kayıtları bilinen klasör GUID'i ile başlar: System32, SysWOW64, Windows
    static bool UserAssistSistem(string s)
    {
        string k = s.ToLowerInvariant();
        string[] g = new string[] {
            "{1ac14e77-02e7-4e5d-b744-2eb1ae5198b7}",
            "{d65231b0-b2f1-4857-a4ce-a8e7c6ea7d27}",
            "{f38bf404-1d43-42f2-9305-67de0b28fc23}"
        };
        foreach (string x in g)
            if (k.StartsWith(x, StringComparison.Ordinal)) return true;
        return false;
    }

    static string[] suruculerHile = new string[] { "kdmapper", "drvmap", "spoof", "hwid" };
    static string[] suruculerRiskli = new string[] {
        "iqvw64e", "dbutil", "gdrv", "asrdrv", "rtcore64", "winio", "physmem", "capcom", "atszio"
    };

    // 0 = temiz, 1 = riskli bilinen sürücü (sarı), 2 = hile/spoofer sürücüsü (kırmızı)
    static int SurucuSeviye(string metin)
    {
        if (string.IsNullOrEmpty(metin)) return 0;
        string k = metin.ToLowerInvariant();
        foreach (string x in suruculerHile) if (k.Contains(x)) return 2;
        foreach (string x in suruculerRiskli) if (k.Contains(x)) return 1;
        return 0;
    }

    static string SurucuYolu(string img)
    {
        if (string.IsNullOrEmpty(img)) return img;
        string s = img.Trim().Trim('"');
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (s.StartsWith("\\??\\", StringComparison.Ordinal)) s = s.Substring(4);
        else if (s.StartsWith("\\SystemRoot\\", StringComparison.OrdinalIgnoreCase)) s = win + s.Substring(11);
        else if (s.StartsWith("system32\\", StringComparison.OrdinalIgnoreCase)) s = win + "\\" + s;
        int i = s.IndexOf(".sys", StringComparison.OrdinalIgnoreCase);
        if (i > 0) s = s.Substring(0, i + 4);
        return Environment.ExpandEnvironmentVariables(s);
    }

    // Windows kullanıcı adı raporda görünmesin: C:\Users\ali\... -> C:\Users\<kullanıcı>\...
    static string Maskele(string s)
    {
        try
        {
            string u = Environment.UserName;
            if (!string.IsNullOrEmpty(u) && u.Length >= 2 && s.IndexOf(u, StringComparison.OrdinalIgnoreCase) >= 0)
                return Regex.Replace(s, @"(\\users\\)" + Regex.Escape(u) + "(?=[\\\\\\s\"]|$)", "$1<kullanıcı>", RegexOptions.IgnoreCase);
        }
        catch (Exception) { }
        return s;
    }

    // ---- RAPOR SUNUCUSU AYARI (kendi bilgisayarınızdaki RaporSunucu) ----
    // SunucuUrl:  http(s)://ADRES[:PORT]/upload   -> kendi adresinizi yazın
    // SunucuAnahtar: sunucunun sunucu_ayar.txt dosyasındaki "anahtar" değeri
    internal const string SunucuUrl = "http://SUNUCU_ADRESI:8080/upload";
    internal const string SunucuAnahtar = "DEGISTIR_BU_ANAHTARI";

    internal static bool SunucuAyarli()
    {
        return SunucuUrl.IndexOf("SUNUCU_ADRESI", StringComparison.Ordinal) < 0;
    }

    internal static string SunucuHost()
    {
        try { return new Uri(SunucuUrl).Host; }
        catch (Exception) { return "?"; }
    }

    static void RaporGonder()
    {
        if (!SunucuAyarli()) return;
        try
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch (Exception) { }
            byte[] veri = Encoding.UTF8.GetBytes(RaporMetni());
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(SunucuUrl);
            rq.Method = "POST";
            rq.ContentType = "text/plain; charset=utf-8";
            rq.ContentLength = veri.Length;
            rq.Timeout = 20000;
            rq.ReadWriteTimeout = 20000;
            rq.Headers["X-Key"] = SunucuAnahtar;
            rq.Headers["X-Machine"] = Uri.EscapeDataString(Environment.MachineName);
            using (Stream s = rq.GetRequestStream()) s.Write(veri, 0, veri.Length);
            using (HttpWebResponse rs = (HttpWebResponse)rq.GetResponse())
            {
                if ((int)rs.StatusCode >= 200 && (int)rs.StatusCode < 300)
                    Yaz("[+] Rapor " + SunucuHost() + " sunucusuna gönderildi.");
                else
                    Yaz("[?] Sunucu beklenmeyen yanıt verdi: " + (int)rs.StatusCode);
            }
        }
        catch (Exception ex)
        {
            Yaz("[?] Rapor sunucuya gönderilemedi: " + ex.Message);
        }
    }

    internal static string secilenKlasor = null;
    internal static bool AdminModu = false;   // --admin ile ya da temiz_hash.txt yoksa: liste oluşturma düğmeleri görünür

    // Yönetici düğmeleri için: seçilen ya da otomatik bulunan oyun ana klasörü
    internal static string AdminKlasor()
    {
        return secilenKlasor != null ? KokKlasor(secilenKlasor) : OyunKlasoru(komutArgs);
    }

    internal static string[] komutArgs = new string[0];
    internal static bool Yonetici = false;
    internal static string SonRaporDosyasi = null;

    internal static string RaporMetni()
    {
        lock (rapor) { return rapor.ToString(); }
    }

    static void AdimEkle(List<KeyValuePair<string, Action>> l, string ad, Action a)
    {
        l.Add(new KeyValuePair<string, Action>(ad, a));
    }

    internal static void TaramaCalistir(Action<string> durum)
    {
        lock (rapor) { rapor.Length = 0; guclu = 0; zayif = 0; izIsareti = 0; }
        baslangic = DateTime.Now;
        aygitHaritasi = null;
        SonRaporDosyasi = null;

        Yaz("LCAPRO SS Kontrol Aracı - " + DateTime.Now);
        Yaz("Bilgisayar: " + Environment.MachineName + "  (Windows kullanıcı adı rapordan gizlenir)");
        Yaz("Son açılış: " + (DateTime.Now - TimeSpan.FromMilliseconds((uint)Environment.TickCount)));
        if (!Yonetici)
            Yaz("[?] Yönetici yetkisi yok: olay günlüğü, USN, AmCache ve sürücü kontrolleri eksik kalabilir.");
        Yaz("Bilgi: Windows sistem dosyaları ve geçerli imzalı antivirüs / tanınmış yayıncı dosyaları şüpheli sayılmaz.");
        if (SunucuAyarli())
            Yaz("Bilgi: Tarama bitince bu rapor " + SunucuHost() + " sunucusuna gönderilecek.");

        string klasor = secilenKlasor != null ? KokKlasor(secilenKlasor) : OyunKlasoru(komutArgs);

        List<KeyValuePair<string, Action>> adimlar = new List<KeyValuePair<string, Action>>();
        AdimEkle(adimlar, "Olay günlükleri", OlayGunlukleri);
        AdimEkle(adimlar, "USN durumu", UsnDurumu);
        AdimEkle(adimlar, "Prefetch", Prefetch);
        AdimEkle(adimlar, "Geri dönüşüm / Recent", IzKlasorleri);
        AdimEkle(adimlar, "UserAssist", UserAssist);
        AdimEkle(adimlar, "Oyun klasörü", delegate { OyunTara(klasor); });
        AdimEkle(adimlar, "CFG / alias", delegate { CfgTara(klasor); });
        AdimEkle(adimlar, "hl.exe modülleri", CalisanOyun);
        AdimEkle(adimlar, "Çalışan süreçler", CalisanSurecler);
        AdimEkle(adimlar, "HWID spoofer", SpooferKontrol);
        AdimEkle(adimlar, "BAM/DAM", BamDam);
        AdimEkle(adimlar, "ShimCache", ShimCache);
        AdimEkle(adimlar, "AmCache", AmCache);
        AdimEkle(adimlar, "USN içeriği", UsnOku);
        AdimEkle(adimlar, "Prefetch ayrıştırma", PrefetchAyristir);
        AdimEkle(adimlar, "Oyun klasörü değişiklikleri", delegate { SonDegisenler(klasor); });
        AdimEkle(adimlar, "Dosya bütünlüğü", delegate { BaselineKarsilastir(klasor); });
        AdimEkle(adimlar, "Makro / no-recoil betikleri", MakroBetikTara);
        AdimEkle(adimlar, "Defender ayarları", DefenderKontrol);
        AdimEkle(adimlar, "Sürücü imza ayarları", ImzaAyarKontrol);

        foreach (KeyValuePair<string, Action> a in adimlar)
        {
            if (durum != null) durum(a.Key);
            try { a.Value(); }
            catch (Exception ex) { Yaz("[?] " + a.Key + " adımı hata verdi: " + ex.Message); }
        }

        if (izIsareti >= 2)
        {
            DateTime kurT = KurulumTarihi();
            bool eskiKurulum = kurT != DateTime.MinValue && (DateTime.Now - kurT).TotalDays > 30;
            Baslik("Birleşik değerlendirme");
            if (izIsareti >= 4 && eskiKurulum)
                Yaz("[!] Birden fazla iz temizleme belirtisi (" + izIsareti + " adet) ve Windows 30 günden eski: kayıtlar bilerek temizlenmiş olabilir.");
            else
                Yaz("[~] Birden fazla iz temizleme belirtisi (" + izIsareti + " adet). Windows yeni kurulduysa normal olabilir; kurulum tarihine bakın.");
        }

        int g, z;
        lock (rapor) { g = guclu; z = zayif; }

        Baslik("Bitti");
        Yaz("Özet: " + g + " kırmızı (hile), " + z + " sarı (şüpheli).");
        if (g > 0) Yaz("SONUÇ: KIRMIZI - hile belirtisi var, kırmızı bulgulara bakın.");
        else if (z > 0) Yaz("SONUÇ: SARI - şüpheli bulgular var, elle inceleyin.");
        else Yaz("SONUÇ: YEŞİL - belirgin bir bulgu yok (bu kesin temiz olduğu anlamına gelmez).");
        Yaz("Açıklama: Kırmızı [!] = hile, Sarı [~] = şüpheli, Yeşil [+] = temiz, Gri [?] = yalnızca bilgi / okunamadı.");
        Yaz("Bulgular tek başına kanıt değildir; sonuçları elle de doğrulayın.");

        try
        {
            string dosya = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "rapor_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            File.WriteAllText(dosya, RaporMetni(), Encoding.UTF8);
            SonRaporDosyasi = dosya;
            Yaz("Rapor kaydedildi: " + dosya);
        }
        catch (Exception)
        {
            Yaz("[?] Rapor otomatik kaydedilemedi; 'Raporu Kaydet' düğmesini kullanın.");
        }

        if (SunucuAyarli())
        {
            if (durum != null) durum("Rapor sunucuya gönderiliyor");
            RaporGonder();
        }
    }
}

// ================= ARAYÜZ =================

// Arka planı (logo filigranı) gösteren, yarı saydam panelli kaydırmalı sonuç alanı
class LogView : Control
{
    class Satir
    {
        public string Metin;
        public Color Renk;
        public bool Kalin;
        public int Yuk;
    }

    List<Satir> satirlar = new List<Satir>();
    Font normal, kalin;
    int ofset = 0, toplam = 0, olculenGenislik = -1;
    bool surukle = false;
    int surukleY = 0, surukleOfset = 0;
    const int Pad = 10;
    const int Sb = 10;
    const TextFormatFlags Bayrak = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding |
        TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top;

    public LogView()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
            ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        normal = new Font("Consolas", 9.5f, FontStyle.Regular);
        kalin = new Font("Consolas", 9.5f, FontStyle.Bold);
    }

    int MetinGenislik { get { return Math.Max(50, ClientSize.Width - Pad * 2 - Sb - 6); } }
    int MaxOfset { get { return Math.Max(0, toplam + Pad * 2 - ClientSize.Height); } }

    int Olc(Satir s)
    {
        string t = s.Metin.Length == 0 ? " " : s.Metin;
        Size sz = TextRenderer.MeasureText(t, s.Kalin ? kalin : normal, new Size(MetinGenislik, int.MaxValue), Bayrak);
        return Math.Max(sz.Height, normal.Height) + 2;
    }

    void Yeniden()
    {
        olculenGenislik = MetinGenislik;
        toplam = 0;
        foreach (Satir s in satirlar) { s.Yuk = Olc(s); toplam += s.Yuk; }
    }

    static void Renklendir(Satir s)
    {
        string m = s.Metin;
        s.Renk = Color.FromArgb(220, 220, 226);
        if (m.StartsWith("===", StringComparison.Ordinal)) { s.Renk = Color.FromArgb(255, 80, 80); s.Kalin = true; }
        else if (m.StartsWith("[!]", StringComparison.Ordinal)) s.Renk = Color.FromArgb(255, 90, 90);
        else if (m.StartsWith("[~]", StringComparison.Ordinal)) s.Renk = Color.FromArgb(255, 196, 60);
        else if (m.StartsWith("[+]", StringComparison.Ordinal)) s.Renk = Color.FromArgb(90, 225, 125);
        else if (m.StartsWith("[?]", StringComparison.Ordinal)) s.Renk = Color.FromArgb(150, 160, 178);
        else if (m.StartsWith("SONUÇ: KIRMIZI", StringComparison.Ordinal)) { s.Renk = Color.FromArgb(255, 70, 70); s.Kalin = true; }
        else if (m.StartsWith("SONUÇ: SARI", StringComparison.Ordinal)) { s.Renk = Color.FromArgb(255, 196, 60); s.Kalin = true; }
        else if (m.StartsWith("SONUÇ: YEŞİL", StringComparison.Ordinal)) { s.Renk = Color.FromArgb(90, 225, 125); s.Kalin = true; }
        else if (m.StartsWith("Özet:", StringComparison.Ordinal)) { s.Renk = Color.White; s.Kalin = true; }
        else if (m.StartsWith("LCAPRO", StringComparison.Ordinal)) { s.Renk = Color.White; s.Kalin = true; }
    }

    public void Ekle(string metin)
    {
        if (metin == null) return;
        bool altta = ofset >= MaxOfset - 4;
        foreach (string parca in metin.Replace("\r", "").Split('\n'))
        {
            Satir s = new Satir();
            s.Metin = parca;
            Renklendir(s);
            satirlar.Add(s);
            if (olculenGenislik != MetinGenislik) Yeniden();
            else { s.Yuk = Olc(s); toplam += s.Yuk; }
        }
        if (altta) ofset = MaxOfset;
        Invalidate();
    }

    public void Temizle()
    {
        satirlar.Clear();
        toplam = 0;
        ofset = 0;
        Invalidate();
    }

    void Kaydir(int d)
    {
        ofset = Math.Max(0, Math.Min(MaxOfset, ofset + d));
        Invalidate();
    }

    Rectangle Tutamac()
    {
        int h = Height - 4;
        int toplamYuk = Math.Max(1, toplam + Pad * 2);
        int th = Math.Max(30, (int)((long)h * Height / toplamYuk));
        if (th > h) th = h;
        int max = MaxOfset;
        int ty = 2 + (max == 0 ? 0 : (int)((long)(h - th) * ofset / max));
        return new Rectangle(Width - Sb - 3, ty, Sb, th);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Yeniden();
        ofset = Math.Min(ofset, MaxOfset);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (SolidBrush fb = new SolidBrush(Color.FromArgb(205, 6, 6, 9))) g.FillRectangle(fb, r);
        using (Pen p = new Pen(Color.FromArgb(150, 160, 20, 30))) g.DrawRectangle(p, r);

        int y = Pad - ofset;
        int w = MetinGenislik;
        foreach (Satir s in satirlar)
        {
            if (y + s.Yuk >= 0 && y <= Height)
                TextRenderer.DrawText(g, s.Metin, s.Kalin ? kalin : normal,
                    new Rectangle(Pad, y, w, s.Yuk), s.Renk, Bayrak);
            y += s.Yuk;
            if (y > Height) break;
        }

        if (toplam + Pad * 2 > Height)
        {
            using (SolidBrush iz = new SolidBrush(Color.FromArgb(50, 255, 255, 255)))
                g.FillRectangle(iz, new Rectangle(Width - Sb - 3, 2, Sb, Height - 4));
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(210, 190, 30, 40)))
                g.FillRectangle(tb, Tutamac());
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        Focus();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Kaydir(-(e.Delta / 120) * 60);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.X >= Width - Sb - 8 && toplam + Pad * 2 > Height)
        {
            Rectangle t = Tutamac();
            if (t.Contains(e.Location)) { surukle = true; surukleY = e.Y; surukleOfset = ofset; }
            else Kaydir(e.Y < t.Top ? -(Height - 40) : (Height - 40));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!surukle) return;
        int aralik = (Height - 4) - Tutamac().Height;
        if (aralik <= 0) return;
        int yeni = surukleOfset + (int)((long)(e.Y - surukleY) * MaxOfset / aralik);
        ofset = Math.Max(0, Math.Min(MaxOfset, yeni));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        surukle = false;
    }
}

class AnaForm : Form
{
    Image logo;
    LogView log;
    Label durum, aciklama;
    ProgressBar ilerleme;
    Button btnTara, btnKlasor, btnKopyala, btnKaydet, btnListe, btnEkle;
    bool calisiyor = false;
    bool yonetici;

    internal static Image LogoYukle()
    {
        byte[] b = Convert.FromBase64String(LogoVeri.B64);
        using (MemoryStream ms = new MemoryStream(b))
        using (Image tmp = Image.FromStream(ms))
            return new Bitmap(tmp);
    }

    Button Dugme(string metin, int x, int w, int y)
    {
        Button b = new Button();
        b.Text = metin;
        b.SetBounds(x, y, w, 34);
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = Color.FromArgb(120, 10, 20);
        b.ForeColor = Color.White;
        b.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        b.FlatAppearance.BorderColor = Color.FromArgb(200, 30, 40);
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(170, 20, 30);
        b.Cursor = Cursors.Hand;
        b.UseVisualStyleBackColor = false;
        Controls.Add(b);
        return b;
    }

    public AnaForm(bool yoneticiMi)
    {
        yonetici = yoneticiMi;
        Text = "LCAPRO - SS Kontrol Aracı";
        ClientSize = new Size(1000, 700);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(8, 8, 12);

        try
        {
            logo = LogoYukle();
            Icon = Icon.FromHandle(new Bitmap(logo, 32, 32).GetHicon());
        }
        catch (Exception) { }

        btnTara = Dugme("Yeniden Tara", 24, 130, 152);
        btnKlasor = Dugme("Oyun Klasörü Seç", 162, 160, 152);
        btnKopyala = Dugme("Kopyala", 330, 110, 152);
        btnKaydet = Dugme("Raporu Kaydet", 448, 130, 152);

        int adminEk = 0;
        if (CSSSTool.AdminModu)
        {
            // Yönetici modu: bu bilgisayardaki (TEMİZ) oyun klasöründen temiz_hash.txt üretir
            btnListe = Dugme("Temiz Liste Oluştur", 24, 190, 192);
            btnEkle = Dugme("Listeye Ekle (farklı sürüm)", 222, 210, 192);
            Label yn = new Label();
            yn.AutoSize = true;
            yn.BackColor = Color.Transparent;
            yn.ForeColor = Color.FromArgb(255, 196, 60);
            yn.Font = new Font("Segoe UI", 9f);
            yn.Text = "Yönetici modu: bu bilgisayardaki oyun klasörü TEMİZ kabul edilir";
            yn.Location = new Point(444, 201);
            Controls.Add(yn);
            adminEk = 40;
        }

        ilerleme = new ProgressBar();
        ilerleme.Style = ProgressBarStyle.Marquee;
        ilerleme.MarqueeAnimationSpeed = 30;
        ilerleme.SetBounds(ClientSize.Width - 120 - 24, 164, 120, 10);
        ilerleme.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        ilerleme.Visible = false;
        Controls.Add(ilerleme);

        log = new LogView();
        log.SetBounds(24, 200 + adminEk, ClientSize.Width - 48, ClientSize.Height - 200 - adminEk - 44);
        log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(log);

        durum = new Label();
        durum.AutoSize = false;
        durum.AutoEllipsis = true;
        durum.BackColor = Color.Transparent;
        durum.ForeColor = Color.FromArgb(200, 200, 208);
        durum.Font = new Font("Segoe UI", 9.5f);
        durum.Text = "Hazır";
        durum.SetBounds(24, ClientSize.Height - 30, 250, 20);
        durum.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.Add(durum);

        aciklama = new Label();
        aciklama.AutoSize = true;
        aciklama.BackColor = Color.Transparent;
        aciklama.ForeColor = Color.FromArgb(150, 160, 178);
        aciklama.Font = new Font("Segoe UI", 9f);
        aciklama.Text = "[!] hile (kırmızı)    [~] şüpheli (sarı)    [+] temiz (yeşil)    [?] bilgi";
        aciklama.Location = new Point(ClientSize.Width - aciklama.PreferredWidth - 24, ClientSize.Height - 30);
        aciklama.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        Controls.Add(aciklama);

        CSSSTool.SatirEklendi = delegate(string s)
        {
            UIde(delegate { log.Ekle(s); });
        };

        btnTara.Click += delegate { Baslat(); };
        btnKlasor.Click += delegate { KlasorSec(); };
        btnKopyala.Click += delegate { Kopyala(); };
        btnKaydet.Click += delegate { Kaydet(); };
        if (btnListe != null)
        {
            btnListe.Click += delegate { ListeIsle(false); };
            btnEkle.Click += delegate { ListeIsle(true); };
        }
        Shown += delegate { Baslat(); };
    }

    void UIde(MethodInvoker m)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(m); }
        catch (Exception) { }
    }

    void Baslat()
    {
        if (calisiyor) return;
        calisiyor = true;
        btnTara.Enabled = false;
        btnKlasor.Enabled = false;
        if (btnListe != null) { btnListe.Enabled = false; btnEkle.Enabled = false; }
        log.Temizle();
        ilerleme.Visible = true;
        durum.Text = "Başlıyor...";

        Thread t = new Thread(delegate()
        {
            try
            {
                CSSSTool.TaramaCalistir(delegate(string ad)
                {
                    UIde(delegate { durum.Text = "Taranıyor: " + ad; });
                });
            }
            catch (Exception ex)
            {
                CSSSTool.Yaz("[?] Beklenmeyen hata: " + ex.Message);
            }
            UIde(delegate { Bitti(); });
        });
        t.IsBackground = true;
        t.Start();
    }

    // Yönetici: oyun klasörünü otomatik bulup temiz_hash.txt üretir / ekler
    void ListeIsle(bool ekle)
    {
        if (calisiyor) return;
        string klasor = CSSSTool.AdminKlasor();
        if (klasor == null)
        {
            MessageBox.Show(this, "Oyun klasörü bulunamadı. Önce 'Oyun Klasörü Seç' ile klasörü seçin.", "LCAPRO");
            return;
        }
        string soru = (ekle
            ? "Bu klasör mevcut temiz_hash.txt listesine EKLENECEK (farklı sürüm):\n\n"
            : "temiz_hash.txt bu klasörden YENİDEN oluşturulacak (eski liste silinir):\n\n")
            + klasor + "\n\nBu klasör hilesiz ve güncel mi? Devam edilsin mi?";
        if (MessageBox.Show(this, soru, "LCAPRO - temiz liste", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        calisiyor = true;
        btnTara.Enabled = false;
        btnKlasor.Enabled = false;
        btnListe.Enabled = false;
        btnEkle.Enabled = false;
        ilerleme.Visible = true;
        durum.Text = "Dosya listesi hazırlanıyor...";

        Thread t = new Thread(delegate()
        {
            string sonuc;
            try { sonuc = CSSSTool.HashListesiYaz(klasor, ekle); }
            catch (Exception ex) { sonuc = "Hata: " + ex.Message; }
            UIde(delegate
            {
                Bitti();
                durum.Text = "Liste hazır";
                MessageBox.Show(this, sonuc, "LCAPRO - temiz liste");
            });
        });
        t.IsBackground = true;
        t.Start();
    }

    void Bitti()
    {
        calisiyor = false;
        ilerleme.Visible = false;
        btnTara.Enabled = true;
        btnKlasor.Enabled = true;
        if (btnListe != null) { btnListe.Enabled = true; btnEkle.Enabled = true; }
        durum.Text = "Tamamlandı";
    }

    void KlasorSec()
    {
        using (FolderBrowserDialog f = new FolderBrowserDialog())
        {
            f.Description = "Half-Life (veya cstrike) klasörünü seçin";
            if (f.ShowDialog(this) == DialogResult.OK)
            {
                CSSSTool.secilenKlasor = f.SelectedPath;
                Baslat();
            }
        }
    }

    void Kopyala()
    {
        string m = CSSSTool.RaporMetni();
        if (m.Length == 0) return;
        try { Clipboard.SetText(m); durum.Text = "Rapor panoya kopyalandı"; }
        catch (Exception) { durum.Text = "Panoya kopyalanamadı"; }
    }

    void Kaydet()
    {
        string m = CSSSTool.RaporMetni();
        if (m.Length == 0) return;
        using (SaveFileDialog d = new SaveFileDialog())
        {
            d.Filter = "Metin dosyası (*.txt)|*.txt";
            d.FileName = "rapor_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllText(d.FileName, m, Encoding.UTF8); durum.Text = "Kaydedildi: " + d.FileName; }
            catch (Exception ex) { durum.Text = "Kaydedilemedi: " + ex.Message; }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        Rectangle r = ClientRectangle;
        if (r.Width < 2 || r.Height < 2) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        using (LinearGradientBrush br = new LinearGradientBrush(r, Color.FromArgb(8, 8, 12), Color.FromArgb(52, 6, 12), 60f))
            g.FillRectangle(br, r);

        if (logo != null)
        {
            // büyük, silik arka plan filigranı
            int s = (int)(Math.Min(r.Width, r.Height) * 0.95);
            Rectangle w = new Rectangle((r.Width - s) / 2, (r.Height - s) / 2 + 40, s, s);
            ColorMatrix cm = new ColorMatrix();
            cm.Matrix33 = 0.14f;
            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                g.DrawImage(logo, w, 0, 0, logo.Width, logo.Height, GraphicsUnit.Pixel, ia);
            }
            // üst bilgi alanındaki logo
            g.DrawImage(logo, new Rectangle(24, 14, 120, 120));
        }

        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (Font f1 = new Font("Segoe UI", 34f, FontStyle.Bold))
        using (Font f2 = new Font("Segoe UI", 13f))
        using (Font f3 = new Font("Segoe UI", 9.5f, FontStyle.Bold))
        using (SolidBrush gol = new SolidBrush(Color.FromArgb(150, 160, 10, 20)))
        using (SolidBrush beyaz = new SolidBrush(Color.White))
        using (SolidBrush gri = new SolidBrush(Color.FromArgb(200, 200, 208)))
        using (SolidBrush yes = new SolidBrush(Color.FromArgb(90, 225, 125)))
        using (SolidBrush sari = new SolidBrush(Color.FromArgb(255, 196, 60)))
        {
            g.DrawString("LCAPRO", f1, gol, 166, 18);
            g.DrawString("LCAPRO", f1, beyaz, 163, 15);
            g.DrawString("CS 1.6 Screenshare (SS) Kontrol Aracı", f2, gri, 166, 78);
            if (yonetici) g.DrawString("Yönetici: Evet", f3, yes, 166, 108);
            else g.DrawString("Yönetici: HAYIR (bazı kontroller eksik kalır)", f3, sari, 166, 108);
            using (Font f4 = new Font("Segoe UI", 8.5f))
                g.DrawString("Sistemde değişiklik yapmaz  |  Rapor: " + (CSSSTool.SunucuAyarli()
                    ? CSSSTool.SunucuHost() + " sunucusuna gönderilir" : "gönderilmez, yalnızca bu bilgisayarda kalır"),
                    f4, gri, 166, 127);
        }
        using (Pen p = new Pen(Color.FromArgb(170, 190, 30, 40), 2f))
            g.DrawLine(p, 24, 142, r.Width - 24, 142);
    }
}

// Başlamadan önce gösterilen şeffaflık / onay ekranı
class OnayForm : Form
{
    Image logo;
    RichTextBox metin;
    CheckBox onay;
    Button basla, vazgec;

    static string KendiHash()
    {
        try
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream fs = File.OpenRead(Application.ExecutablePath))
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("X2"));
                return sb.ToString();
            }
        }
        catch (Exception) { return "hesaplanamadı"; }
    }

    void Ekle(string s, Color renk, bool kalin)
    {
        metin.SelectionStart = metin.TextLength;
        metin.SelectionLength = 0;
        metin.SelectionColor = renk;
        metin.SelectionFont = new Font("Segoe UI", 10f, kalin ? FontStyle.Bold : FontStyle.Regular);
        metin.SelectedText = s;
    }

    Button Dugme(string yazi, int x, int w, Color zemin)
    {
        Button b = new Button();
        b.Text = yazi;
        b.SetBounds(x, 558, w, 38);
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = zemin;
        b.ForeColor = Color.White;
        b.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        b.FlatAppearance.BorderColor = Color.FromArgb(200, 30, 40);
        b.Cursor = Cursors.Hand;
        b.UseVisualStyleBackColor = false;
        Controls.Add(b);
        return b;
    }

    public OnayForm()
    {
        Text = "LCAPRO SS Kontrol Aracı - Başlamadan önce";
        ClientSize = new Size(780, 612);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(8, 8, 12);
        try
        {
            logo = AnaForm.LogoYukle();
            Icon = Icon.FromHandle(new Bitmap(logo, 32, 32).GetHicon());
        }
        catch (Exception) { }

        metin = new RichTextBox();
        metin.ReadOnly = true;
        metin.BorderStyle = BorderStyle.None;
        metin.DetectUrls = false;
        metin.ScrollBars = RichTextBoxScrollBars.Vertical;
        metin.SetBounds(24, 128, 732, 384);
        metin.BackColor = Color.FromArgb(16, 16, 22);
        metin.ForeColor = Color.FromArgb(220, 220, 226);
        Controls.Add(metin);

        Color acik = Color.FromArgb(220, 220, 226);
        Color baslik = Color.FromArgb(255, 90, 90);
        Color yesil = Color.FromArgb(90, 225, 125);
        Color sari = Color.FromArgb(255, 196, 60);
        bool gonder = CSSSTool.SunucuAyarli();

        Ekle("Bu araç ne yapar?\n", yesil, true);
        Ekle("•  Windows'un zaten tuttuğu kayıtları OKUR: Prefetch, UserAssist, BAM, ShimCache, AmCache, USN günlüğü ve olay günlükleri. Amaç, hile ya da iz temizleme belirtisi olup olmadığına bakmaktır.\n", acik, false);
        Ekle("•  CS 1.6 klasöründeki dosya adlarına, son değişiklik zamanlarına ve .cfg dosyalarının içeriğine bakar; oyun açıksa hl.exe'ye yüklenmiş modülleri listeler.\n", acik, false);
        Ekle("•  Oyun dosyalarının parmak izini (SHA256) hesaplayıp yöneticinin verdiği temiz_hash.txt listesiyle karşılaştırır. Dosyaların içeriği rapora yazılmaz, yalnızca listeden farklı olanların yolu yazılır.\n", acik, false);
        Ekle("•  Okuma amaçlı Windows komutları çalıştırır (wevtutil, fsutil, sc, vol, esentutl, bcdedit).\n", acik, false);
        Ekle("•  Windows Defender dışlama listesine, sürücü imza ayarlarına (test modu, Secure Boot) ve Logitech G HUB / Gaming Software profil dosyalarına bakar. Logitech dosyalarında yalnızca no-recoil makro betiği belirtisi aranır; dosya içeriği rapora yazılmaz. Masaüstü, İndirilenler ve Belgeler klasörlerinde .ahk dosyalarının adlarını arar.\n", acik, false);
        Ekle("•  Sonucu bu pencerede gösterir ve exe'nin yanına .txt rapor olarak kaydeder.\n", acik, false);
        if (gonder)
            Ekle("•  Tarama bitince raporu " + CSSSTool.SunucuHost() + " adresindeki sunucuya gönderir. Gönderilen metin, ekranda gördüğünüz sonuç metnidir; fazlası gönderilmez.\n", acik, false);
        else
            Ekle("•  Hiçbir şeyi internete göndermez; rapor yalnızca bu bilgisayarda kalır.\n", acik, false);
        Ekle("\nRaporda neler yazar?\n", sari, true);
        Ekle("Bilgisayar adı, Windows kurulum tarihi, makine kimlikleri (MachineGuid, HwProfileGuid, SQM MachineId), C: disk seri numarası, eşleşen/şüpheli kayıtlar ve silinmiş .exe dosyalarının yolları ve Defender dışlama yolları, temiz listeden farklı oyun dosyalarının yolları. Windows kullanıcı adınız yollarda <kullanıcı> olarak gizlenir.\n", acik, false);
        Ekle("\nBu araç ne yapmaz?\n", baslik, true);
        Ekle("•  Hiçbir dosyanızı silmez veya değiştirmez, kayıt defterine yazmaz, program kurmaz, bilgisayar açılışına eklenmez. İki küçük istisna: AmCache kaydının geçici bir kopyasını Temp klasörüne alıp işi bitince siler; rapor .txt dosyasını yazar.\n", acik, false);
        Ekle("•  Ekran görüntüsü, klavye/fare kaydı, kamera, mikrofon, tarayıcı geçmişi, şifre, mesaj veya kişisel belgelerinizin içeriğine bakmaz.\n", acik, false);
        Ekle("•  Arka planda çalışmaz; pencereyi kapattığınızda iş biter.\n", acik, false);
        Ekle("•  Yukarıda yazanın dışında hiçbir adrese bağlanmaz.\n", acik, false);
        Ekle("\nNeden yönetici izni (UAC) istenir?\n", sari, true);
        Ekle("Olay günlüğü, USN ve AmCache gibi kayıtlar yönetici izni olmadan okunamaz. İzin yalnızca bu kayıtları okumak için kullanılır. Windows, imzasız programlar için 'Bilinmeyen yayıncı' uyarısı gösterir; bu normaldir.\n", acik, false);
        Ekle("\nBu dosyanın SHA256 değeri\n", sari, true);
        Ekle(KendiHash() + "\n", Color.FromArgb(150, 160, 178), false);
        Ekle("Sunucu yöneticisinin paylaştığı değerle karşılaştırarak dosyanın değiştirilmediğini doğrulayabilirsiniz.\n", acik, false);
        Ekle("\nKabul etmezseniz hiçbir tarama yapılmaz ve hiçbir şey gönderilmez.\n", baslik, true);
        metin.SelectionStart = 0;
        metin.ScrollToCaret();

        onay = new CheckBox();
        onay.Text = "Yukarıdakileri okudum ve taramanın yapılmasını kabul ediyorum.";
        onay.SetBounds(24, 522, 732, 26);
        onay.BackColor = Color.Transparent;
        onay.ForeColor = Color.White;
        onay.Font = new Font("Segoe UI", 10f);
        Controls.Add(onay);

        basla = Dugme("Kabul ediyorum, taramayı başlat", 24, 320, Color.FromArgb(120, 10, 20));
        basla.DialogResult = DialogResult.OK;
        basla.Enabled = false;
        vazgec = Dugme("Vazgeç", 356, 140, Color.FromArgb(50, 50, 60));
        vazgec.DialogResult = DialogResult.Cancel;
        CancelButton = vazgec;

        onay.CheckedChanged += delegate { basla.Enabled = onay.Checked; };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        Rectangle r = ClientRectangle;
        if (r.Width < 2 || r.Height < 2) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using (LinearGradientBrush br = new LinearGradientBrush(r, Color.FromArgb(8, 8, 12), Color.FromArgb(52, 6, 12), 60f))
            g.FillRectangle(br, r);
        if (logo != null) g.DrawImage(logo, new Rectangle(24, 16, 90, 90));
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (Font f1 = new Font("Segoe UI", 22f, FontStyle.Bold))
        using (Font f2 = new Font("Segoe UI", 11f))
        using (SolidBrush beyaz = new SolidBrush(Color.White))
        using (SolidBrush gri = new SolidBrush(Color.FromArgb(200, 200, 208)))
        {
            g.DrawString("LCAPRO SS Kontrol Aracı", f1, beyaz, 128, 22);
            g.DrawString("Başlamadan önce lütfen okuyun: araç ne yapar, ne yapmaz?", f2, gri, 130, 66);
        }
        using (Pen p = new Pen(Color.FromArgb(170, 190, 30, 40), 2f))
            g.DrawLine(p, 24, 116, r.Width - 24, 116);
    }
}

static class Program
{
    static bool YoneticiMi()
    {
        try
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { return false; }
    }

    [STAThread]
    static void Main(string[] args)
    {
        bool onayli = false;
        bool adminArg = false;
        List<string> temiz = new List<string>();
        foreach (string a in args)
        {
            if (a == "--onayli") onayli = true;
            else if (a == "--admin") adminArg = true;
            else temiz.Add(a);
        }
        if (adminArg) onayli = true;   // yönetici kendi aracını açıyor: onay ekranı gösterilmez

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Yönetici komutu: temiz kurulumdan dosya listesi üret / ekle (arayüz ve onay gerekmez)
        if (args.Length >= 1 && (args[0] == "--hash-olustur" || args[0] == "--hash-ekle"))
        {
            string sonuc;
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                sonuc = CSSSTool.HashListesiYaz(args.Length >= 2 ? args[1] : null, args[0] == "--hash-ekle");
            }
            catch (Exception ex) { sonuc = "Hata: " + ex.Message; }
            Cursor.Current = Cursors.Default;
            MessageBox.Show(sonuc, "LCAPRO - temiz dosya listesi");
            return;
        }

        // Önce açıklama ve onay; kabul edilmezse hiçbir şey yapılmaz (UAC bile sorulmaz)
        if (!onayli)
        {
            using (OnayForm of = new OnayForm())
            {
                if (of.ShowDialog() != DialogResult.OK) return;
            }
        }

        bool yonetici = YoneticiMi();
        if (!yonetici)
        {
            // Onaydan sonra yönetici olarak yeniden başlat (UAC reddedilirse yetkisiz devam eder)
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                StringBuilder sb = new StringBuilder("--onayli " + (adminArg ? "--admin " : ""));
                foreach (string a in temiz) sb.Append('"').Append(a.TrimEnd('\\')).Append("\" ");
                psi.Arguments = sb.ToString();
                Process.Start(psi);
                return;
            }
            catch (Exception) { }
        }

        CSSSTool.Yonetici = yonetici;
        CSSSTool.AdminModu = adminArg || !File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temiz_hash.txt"));
        CSSSTool.komutArgs = temiz.ToArray();
        Application.Run(new AnaForm(yonetici));
    }
}
