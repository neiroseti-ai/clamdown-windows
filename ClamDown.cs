// ClamDown для Windows (неофициальный). Ноутбук не засыпает при закрытой крышке, пока вы этого хотите
// или пока работает ИИ-агент. Идея — ClamDown для macOS (github.com/delef/ClamDown, Apache-2.0); код свой.
// Сборка: csc.exe из .NET Framework 4 (C# 5), один файл. Режимы: без ключей — трей; --watchdog — страховка;
// --selftest <файл> — самопроверка на временной копии схемы питания (ваша схема не меняется).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("ClamDown для Windows")]
[assembly: System.Reflection.AssemblyProduct("ClamDown для Windows (неофициальный)")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace HbAi.ClamDown
{
    // ---------------- настройки питания: чтение, запись, сохранение, возврат ----------------
    public static class Power
    {
        public static readonly Guid SubButtons = new Guid("4f971e89-eebd-4455-a8de-9e59040e7347");
        public static readonly Guid LidAction = new Guid("5ca83367-6e45-459f-a27b-476b1d01c936");
        public static readonly Guid SubSleep = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
        public static readonly Guid StandbyIdle = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
        public static readonly Guid HibernateIdle = new Guid("9d7815a6-7ee4-497e-8888-515a05f02364");
        public static readonly Guid UnattendedSleep = new Guid("7bc4a2f9-d8fc-4469-b07b-33eb785aaca0");
        public static readonly Guid SubVideo = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
        public static readonly Guid VideoIdle = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");       // гасить экран через
        public static readonly Guid VideoConLock = new Guid("8ec4b3a5-6868-48c2-be75-4f3044be88a7");    // гасить экран блокировки через

        // что меняем: (подгруппа, параметр, значение на время работы). Экран тоже: на ноутбуках с Modern Standby
        // погасший экран (в том числе экран блокировки через ~30–60 с) уводит систему в режим ожидания — проверено 17.11.2026.
        public static readonly Guid[][] Items = new Guid[][] {
            new Guid[] { SubButtons, LidAction }, new Guid[] { SubSleep, StandbyIdle },
            new Guid[] { SubSleep, HibernateIdle }, new Guid[] { SubSleep, UnattendedSleep },
            new Guid[] { SubVideo, VideoIdle }, new Guid[] { SubVideo, VideoConLock } };
        public static readonly uint[] AwakeValues = new uint[] { 0, 0, 0, 0, 0, 0 }; // крышка: ничего не делать; тайм-ауты: никогда

        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerDuplicateScheme(IntPtr root, ref Guid source, ref IntPtr dest);
        [DllImport("powrprof.dll")] static extern uint PowerDeleteScheme(IntPtr root, ref Guid scheme);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

        public static Guid ActiveScheme()
        {
            IntPtr p;
            uint r = PowerGetActiveScheme(IntPtr.Zero, out p);
            if (r != 0) throw new Exception("Не удалось узнать схему питания, код " + r);
            Guid g = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
            LocalFree(p);
            return g;
        }

        /// <summary>Снимок: для каждого параметра пара [сеть, батарея]. Параметра нет в схеме — null.</summary>
        public static uint?[][] Read(Guid scheme)
        {
            uint?[][] res = new uint?[Items.Length][];
            for (int i = 0; i < Items.Length; i++)
            {
                Guid sub = Items[i][0], set = Items[i][1];
                uint ac, dc;
                uint ra = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out ac);
                uint rd = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out dc);
                res[i] = new uint?[] { ra == 0 ? (uint?)ac : null, rd == 0 ? (uint?)dc : null };
            }
            return res;
        }

        /// <summary>Записать снимок (null — пропустить). apply = сделать схему активной, чтобы значения вступили в силу.</summary>
        public static void Write(Guid scheme, uint?[][] values, bool apply)
        {
            for (int i = 0; i < Items.Length; i++)
            {
                Guid sub = Items[i][0], set = Items[i][1];
                if (values[i][0].HasValue) Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, values[i][0].Value), i);
                if (values[i][1].HasValue) Check(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, values[i][1].Value), i);
            }
            if (apply) PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        }
        static void Check(uint r, int i) { if (r != 0) throw new Exception("Windows не дала изменить настройку питания №" + i + ", код " + r); }

        /// <summary>Значения «не спать» только для тех параметров, что есть в снимке.</summary>
        public static uint?[][] AwakeFor(uint?[][] saved)
        {
            uint?[][] v = new uint?[Items.Length][];
            for (int i = 0; i < Items.Length; i++)
                v[i] = new uint?[] { saved[i][0].HasValue ? (uint?)AwakeValues[i] : null, saved[i][1].HasValue ? (uint?)AwakeValues[i] : null };
            return v;
        }

        public static Guid Duplicate(Guid source)
        {
            IntPtr p = IntPtr.Zero;
            uint r = PowerDuplicateScheme(IntPtr.Zero, ref source, ref p);
            if (r != 0) throw new Exception("Копия схемы не создалась, код " + r);
            Guid g = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
            LocalFree(p);
            return g;
        }
        public static void Delete(Guid g) { PowerDeleteScheme(IntPtr.Zero, ref g); }

        // ---- файл сохранённых настроек: «схема;сеть,батарея;…», null — «-» ----
        public static string Serialize(Guid scheme, uint?[][] v)
        {
            StringBuilder sb = new StringBuilder(scheme.ToString());
            foreach (uint?[] p in v)
                sb.Append(';').Append(p[0].HasValue ? p[0].Value.ToString(CultureInfo.InvariantCulture) : "-")
                  .Append(',').Append(p[1].HasValue ? p[1].Value.ToString(CultureInfo.InvariantCulture) : "-");
            return sb.ToString();
        }
        public static bool TryParse(string s, out Guid scheme, out uint?[][] v)
        {
            scheme = Guid.Empty; v = null;
            if (string.IsNullOrEmpty(s)) return false;
            string[] parts = s.Trim().Split(';');
            // версия 1.0 сохраняла 4 параметра; недостающие — «не трогать»
            if (parts.Length < 5 || parts.Length > Items.Length + 1) return false;
            try { scheme = new Guid(parts[0]); } catch { return false; }
            v = new uint?[Items.Length][];
            for (int i = 0; i < Items.Length; i++)
            {
                if (i + 1 >= parts.Length) { v[i] = new uint?[2]; continue; }
                string[] ab = parts[i + 1].Split(',');
                if (ab.Length != 2) return false;
                v[i] = new uint?[2];
                for (int k = 0; k < 2; k++)
                {
                    if (ab[k] == "-") continue;
                    uint x;
                    if (!uint.TryParse(ab[k], NumberStyles.None, CultureInfo.InvariantCulture, out x)) return false;
                    v[i][k] = x;
                }
            }
            return true;
        }
    }

    // ---------------- файлы состояния ----------------
    public static class Store
    {
        public static string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClamDownWin");
        public static string Saved { get { return Path.Combine(Dir, "saved.txt"); } }       // исходные настройки, пока режим включён
        public static string Pulse { get { return Path.Combine(Dir, "pulse.txt"); } }       // пульс программы
        public static string Settings { get { return Path.Combine(Dir, "settings.txt"); } }
        public static string Log { get { return Path.Combine(Dir, "log.txt"); } }

        public static void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, Encoding.UTF8);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
        public static void Note(string msg)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(Log) && new FileInfo(Log).Length > 200000) File.Delete(Log);
                File.AppendAllText(Log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }

    // ---------------- страховка: вернуть настройки, если программа пропала ----------------
    public static class Watchdog
    {
        public const string TaskName = "ClamDown для Windows — страховка";
        public static readonly TimeSpan Silence = TimeSpan.FromSeconds(90);

        /// <summary>Нужно ли возвращать: сохранённые настройки есть, а пульса нет дольше Silence.</summary>
        public static bool ShouldRestore(bool savedExists, DateTime? lastPulseUtc, DateTime nowUtc, bool appRunning)
        {
            if (!savedExists) return false;
            if (appRunning && lastPulseUtc.HasValue && nowUtc - lastPulseUtc.Value < Silence) return false;
            if (!lastPulseUtc.HasValue) return true;
            return nowUtc - lastPulseUtc.Value >= Silence;
        }

        public static DateTime? ReadPulse(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                long ticks;
                if (long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ticks)) return new DateTime(ticks, DateTimeKind.Utc);
            }
            catch { }
            return null;
        }

        /// <summary>Вернуть настройки из файла. Возвращает true, если что-то вернули.</summary>
        public static bool RestoreFromFile(string savedPath, bool apply)
        {
            if (!File.Exists(savedPath)) return false;
            Guid scheme; uint?[][] v;
            if (!Power.TryParse(File.ReadAllText(savedPath), out scheme, out v)) { File.Delete(savedPath); return false; }
            Power.Write(scheme, v, apply && scheme == Power.ActiveScheme());
            File.Delete(savedPath);
            return true;
        }

        public static int Run()
        {
            bool appRunning;
            try { using (Mutex.OpenExisting(App.MutexName)) { } appRunning = true; }
            catch (WaitHandleCannotBeOpenedException) { appRunning = false; }
            catch (UnauthorizedAccessException) { appRunning = true; }
            DateTime? pulse = ReadPulse(Store.Pulse);
            bool saved = File.Exists(Store.Saved);
            if (saved && (!pulse.HasValue || DateTime.UtcNow - pulse.Value > TimeSpan.FromSeconds(60)))
                Store.Note("Страховка: пульс " + (pulse.HasValue ? ((int)(DateTime.UtcNow - pulse.Value).TotalSeconds) + " с назад" : "не найден") + ", программа " + (appRunning ? "запущена" : "не запущена") + ".");
            if (ShouldRestore(saved, pulse, DateTime.UtcNow, appRunning))
            {
                try { if (RestoreFromFile(Store.Saved, true)) Store.Note("Страховка: программа молчала больше 90 с, настройки питания возвращены."); }
                catch (Exception e) { Store.Note("Страховка: ошибка возврата: " + e.Message); return 1; }
            }
            return 0;
        }

        /// <summary>Задача раз в минуту. Через XML: у задач из /SC MINUTE по умолчанию «не запускать от батареи»,
        /// а страховка нужна именно на батарее.</summary>
        /// <summary>Сторож-процесс: запускается самой программой и ждёт её завершения. Нужен потому, что задачу планировщика
        /// антивирус может запускать в «песочнице» с отдельной копией папки (проверено 17.11.2026: задача видит пустую папку).</summary>
        public const string GuardMutex = "Local\\HbAi.ClamDown.Windows.Guard";
        public static int Guard(int parentPid)
        {
            bool created;
            using (Mutex m = new Mutex(true, GuardMutex, out created))
            {
                if (!created) return 0;
                try { using (Process p = Process.GetProcessById(parentPid)) p.WaitForExit(); } catch { }
                // программа закрылась: если штатно — сохранённых настроек нет; если её убили — ждём тишины пульса и возвращаем
                for (int i = 0; i < 24; i++)
                {
                    bool appRunning;
                    try { using (Mutex.OpenExisting(App.MutexName)) { } appRunning = true; }
                    catch (WaitHandleCannotBeOpenedException) { appRunning = false; }
                    catch (UnauthorizedAccessException) { appRunning = true; }
                    GuardStep step = GuardDecide(File.Exists(Store.Saved), appRunning, ReadPulse(Store.Pulse), DateTime.UtcNow);
                    if (step == GuardStep.Done) return 0;
                    if (step == GuardStep.Restore)
                    {
                        try { if (RestoreFromFile(Store.Saved, true)) Store.Note("Сторож: программа закрылась аварийно, настройки питания возвращены."); }
                        catch (Exception e) { Store.Note("Сторож: ошибка возврата: " + e.Message); }
                        return 0;
                    }
                    Thread.Sleep(15000);
                }
            }
            return 0;
        }
        public enum GuardStep { Done, Restore, Wait }
        /// <summary>Шаг сторожа: настроек нет или программа снова запущена — конец; пульс молчит 90 с — вернуть; иначе ждать.</summary>
        public static GuardStep GuardDecide(bool savedExists, bool appRunning, DateTime? pulse, DateTime nowUtc)
        {
            if (!savedExists || appRunning) return GuardStep.Done;
            return ShouldRestore(true, pulse, nowUtc, false) ? GuardStep.Restore : GuardStep.Wait;
        }
        public static void StartGuard(string exe)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, "--guard " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                Process.Start(psi).Dispose();
            }
            catch (Exception e) { Store.Note("Сторож не запустился: " + e.Message); }
        }

        public static void Install(string exe)
        {
            string esc = System.Security.SecurityElement.Escape(exe);
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                " <Triggers><TimeTrigger><StartBoundary>2026-01-01T00:00:00</StartBoundary><Enabled>true</Enabled>" +
                "<Repetition><Interval>PT1M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition></TimeTrigger></Triggers>\r\n" +
                " <Settings><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                "<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><StartWhenAvailable>true</StartWhenAvailable>" +
                "<ExecutionTimeLimit>PT2M</ExecutionTimeLimit><Enabled>true</Enabled><Hidden>true</Hidden></Settings>\r\n" +
                " <Actions><Exec><Command>" + esc + "</Command><Arguments>--watchdog</Arguments></Exec></Actions>\r\n" +
                "</Task>\r\n";
            string tmp = Path.Combine(Path.GetTempPath(), "clamdown_task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            Schtasks("/Create /F /TN \"" + TaskName + "\" /XML \"" + tmp + "\"");
            try { File.Delete(tmp); } catch { }
        }
        public static void Remove() { Schtasks("/Delete /F /TN \"" + TaskName + "\""); }
        public static bool Installed() { return Schtasks("/Query /TN \"" + TaskName + "\"") == 0; }
        static int Schtasks(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
                psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(15000); return p.ExitCode; }
            }
            catch { return -1; }
        }
    }

    // ---------------- работает ли агент ----------------
    public static class Agents
    {
        public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);   // файл сессии менялся недавно — агент работает
        public static string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>Последнее изменение файлов сессий (UTC) или null. Claude Code: верхний уровень папок проектов
        /// (полный перебор с подпапками на большой истории занимает секунды); Codex: папки сессий за сегодня и вчера.</summary>
        public static DateTime? LastActivity(string home)
        {
            DateTime? best = null;
            List<string> dirs = new List<string>();
            try
            {
                string cp = Path.Combine(home, ".claude", "projects");
                if (Directory.Exists(cp)) dirs.AddRange(Directory.EnumerateDirectories(cp));
            }
            catch { }
            foreach (DateTime d in new DateTime[] { DateTime.Now, DateTime.Now.AddDays(-1) })
                dirs.Add(Path.Combine(home, ".codex", "sessions", d.ToString("yyyy", CultureInfo.InvariantCulture),
                    d.ToString("MM", CultureInfo.InvariantCulture), d.ToString("dd", CultureInfo.InvariantCulture)));
            foreach (string dir in dirs)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (FileInfo fi in new DirectoryInfo(dir).EnumerateFiles("*.jsonl"))
                    {
                        DateTime t = fi.LastWriteTimeUtc;
                        if (!best.HasValue || t > best.Value) best = t;
                    }
                }
                catch { }
            }
            return best;
        }

        /// <summary>Есть ли занятая сессия Claude Code: ~/.claude/sessions/&lt;pid&gt;.json со статусом не idle и живым процессом.
        /// Так ловится и долгая тихая команда, когда файл истории не пишется.</summary>
        public static readonly TimeSpan BusyMax = TimeSpan.FromHours(3); // «занят» дольше трёх часов без обновления — не верим
        public static bool BusySession(string home, Func<int, bool> alive)
        {
            try
            {
                string dir = Path.Combine(home, ".claude", "sessions");
                if (!Directory.Exists(dir)) return false;
                foreach (FileInfo fi in new DirectoryInfo(dir).EnumerateFiles("*.json"))
                {
                    if (DateTime.UtcNow - fi.LastWriteTimeUtc > BusyMax) continue;
                    string s;
                    try { s = File.ReadAllText(fi.FullName); } catch { continue; }
                    string status = Field(s, "status"), pid = Field(s, "pid");
                    int p;
                    if (status != null && status != "idle" && pid != null && int.TryParse(pid, out p) && alive(p)) return true;
                }
            }
            catch { }
            return false;
        }
        static string Field(string json, string name)
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(json, "\"" + name + "\"\\s*:\\s*(\"([^\"]*)\"|(-?\\d+))");
            if (!m.Success) return null;
            return m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        }
        public static bool Alive(int pid)
        {
            try { using (Process p = Process.GetProcessById(pid)) { try { return !p.HasExited; } catch (System.ComponentModel.Win32Exception) { return true; } } }
            catch { return false; }
        }
        public static bool Working(DateTime? last, DateTime nowUtc) { return last.HasValue && nowUtc - last.Value <= Fresh; }
        public static bool Working(string home, DateTime nowUtc) { return BusySession(home, Alive) || Working(LastActivity(home), nowUtc); }
    }

    // ---------------- решение: когда выключаться ----------------
    public static class Rules
    {
        /// <summary>Причина выключения или null. percent: 0..100 или -1, если батареи нет.</summary>
        public static string StopReason(bool onBattery, int percent, int floor, DateTime? untilUtc, DateTime nowUtc, double? tempC, double tempMax)
        {
            if (onBattery && percent >= 0 && percent <= floor) return "заряд упал до " + percent + "%";
            if (untilUtc.HasValue && nowUtc >= untilUtc.Value) return "кончилось время";
            if (tempC.HasValue && tempC.Value >= tempMax) return "перегрев: " + Math.Round(tempC.Value) + " °C";
            return null;
        }
        public static string Left(TimeSpan t)
        {
            if (t.TotalSeconds < 0) t = TimeSpan.Zero;
            return ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture) + " ч " + t.Minutes.ToString("00", CultureInfo.InvariantCulture) + " мин";
        }
    }

    // ---------------- решения программы в трее (вынесены, чтобы их проверяла самопроверка) ----------------
    public static class Policy
    {
        public static readonly TimeSpan AgentGrace = TimeSpan.FromMinutes(5);  // агент затих — выключаемся через столько
        public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
        /// <summary>Флаги на время работы: экран «логически включён», иначе Modern Standby (проверено 17.11.2026).</summary>
        public const uint AwakeFlags = ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED;
        /// <summary>Включиться самой из-за агента: агент работает, режим выключен, авто не придержано, нет причины выключиться.</summary>
        public static bool AutoStart(bool working, bool on, bool held, string stopReason) { return working && !on && !held && stopReason == null; }
        /// <summary>Придержать авто после выключения (вручную, по заряду, по таймеру), пока агент не затихнет.</summary>
        public static bool HoldAfterStop(bool byAgent, bool autoEnabled) { return byAgent || autoEnabled; }
        /// <summary>Агент затих достаточно давно — выключаться.</summary>
        public static bool AgentDone(DateTime? quietSince, DateTime nowUtc) { return quietSince.HasValue && nowUtc - quietSince.Value >= AgentGrace; }
        /// <summary>Байт из сообщения Windows о крышке: 0 — закрыта.</summary>
        public static bool LidClosed(byte data) { return data == 0; }
        /// <summary>Усыплять ли после выключения: только по заряду, таймеру, перегреву и концу работы агента, и только с закрытой крышкой.</summary>
        public static bool SleepAfter(StopKind kind, bool lidClosed) { return lidClosed && kind == StopKind.Limit; }

        /// <summary>Начать: если остался хвост прошлого раза — сначала вернуть его; потом запомнить и записать «не спать».</summary>
        public static void Begin(Guid scheme, bool apply)
        {
            if (File.Exists(Store.Saved)) Watchdog.RestoreFromFile(Store.Saved, apply);
            uint?[][] saved = Power.Read(scheme);
            Store.Write(Store.Saved, Power.Serialize(scheme, saved));
            Power.Write(scheme, Power.AwakeFor(saved), apply);
        }
    }
    public enum StopKind { Manual, Limit, System }

    // ---------------- температура, если Windows её отдаёт ----------------
    public static class Thermal
    {
        public static bool Tried, Available;
        public static double? Read()
        {
            if (Tried && !Available) return null;
            Tried = true;
            try
            {
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher("root\\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                {
                    double best = double.MinValue;
                    foreach (System.Management.ManagementObject o in s.Get())
                    {
                        double c = Convert.ToDouble(o["CurrentTemperature"], CultureInfo.InvariantCulture) / 10.0 - 273.15;
                        if (c > best) best = c;
                    }
                    Available = best > -50 && best < 150;
                    return Available ? (double?)best : null;
                }
            }
            catch { Available = false; return null; }
        }
    }

    // ---------------- настройки программы ----------------
    public class Prefs
    {
        public int Hours = 0;          // 0 — без ограничения
        public int Floor = 15;         // порог заряда, %
        public bool Auto = true;       // включаться, пока работает агент
        public bool LockOnLid = true;
        public double TempMax = 90;

        public static Prefs Load()
        {
            Prefs p = new Prefs();
            try
            {
                if (!File.Exists(Store.Settings)) return p;
                foreach (string line in File.ReadAllLines(Store.Settings))
                {
                    int i = line.IndexOf('=');
                    if (i < 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    int n;
                    if (k == "hours" && int.TryParse(v, out n) && n >= 0 && n <= 24) p.Hours = n;
                    if (k == "floor" && int.TryParse(v, out n) && n >= 5 && n <= 50) p.Floor = n;
                    if (k == "auto") p.Auto = v == "1";
                    if (k == "lock") p.LockOnLid = v == "1";
                }
            }
            catch { }
            return p;
        }
        public void Save()
        {
            try { Store.Write(Store.Settings, "hours=" + Hours + "\r\nfloor=" + Floor + "\r\nauto=" + (Auto ? 1 : 0) + "\r\nlock=" + (LockOnLid ? 1 : 0) + "\r\n"); } catch { }
        }
    }

    // ---------------- программа в трее ----------------
    public class App : Form
    {
        public const string MutexName = "Local\\HbAi.ClamDown.Windows";
        public const string Vk = "https://vk.com/neiroseti_ai";
        public const string Version = "1.0";

        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        const uint ES_CONTINUOUS = Policy.ES_CONTINUOUS;
        [DllImport("user32.dll")] static extern IntPtr RegisterPowerSettingNotification(IntPtr h, ref Guid g, int flags);
        [DllImport("user32.dll")] static extern bool LockWorkStation();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("powrprof.dll", SetLastError = true)] static extern bool SetSuspendState(bool hibernate, bool force, bool wakeup);
        static Guid LidSwitch = new Guid("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");
        const int WM_POWERBROADCAST = 0x218, PBT_POWERSETTINGCHANGE = 0x8013, WM_SYSCOMMAND = 0x112, SC_MONITORPOWER = 0xF170;

        [StructLayout(LayoutKind.Sequential)]
        struct PowerBroadcastSetting { public Guid PowerSetting; public int DataLength; public byte Data; }

        NotifyIcon tray = new NotifyIcon();
        ContextMenuStrip menu = new ContextMenuStrip();
        System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        Prefs prefs = Prefs.Load();
        bool on;              // режим включён
        bool byAgent;         // включён автоматически из-за агента
        bool lidClosed;
        DateTime? untilUtc;
        DateTime? agentQuietSince;
        Icon iconOn, iconOff;
        ToolStripMenuItem itemOn, itemStatus, itemAuto, itemLock, itemTimer, itemFloor, itemStartup;

        /// <summary>Снимок меню и окна «О программе» в PNG без включения режима (для README и проверки вида).</summary>
        public static int Shot(string dir)
        {
            Application.EnableVisualStyles();
            App a = new App(false);
            a.Refresh2();
            a.menu.Show(new Point(40, 40));
            Application.DoEvents();
            using (Bitmap b = new Bitmap(a.menu.Width, a.menu.Height)) { a.menu.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height)); b.Save(Path.Combine(dir, "shot_menu.png")); }
            a.menu.Close();
            Form f = BuildAbout();
            f.StartPosition = FormStartPosition.Manual; f.Location = new Point(40, 40); f.Show();
            Application.DoEvents();
            using (Bitmap b = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height)); b.Save(Path.Combine(dir, "shot_about.png")); }
            f.Close();
            a.tray.Visible = false;
            return 0;
        }

        public App() : this(true) { }
        App(bool live)
        {
            ShowInTaskbar = false; WindowState = FormWindowState.Minimized; FormBorderStyle = FormBorderStyle.FixedToolWindow;
            Opacity = 0; Size = new Size(1, 1);
            iconOn = MakeIcon(true); iconOff = MakeIcon(false);
            BuildMenu();
            if (!live) return;
            CreateHandle(); // окно невидимое, но handle нужен: через него приходят сигналы о крышке и выключении Windows
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Toggle(); };
            tray.Visible = true;
            tick.Interval = 15000; tick.Tick += delegate { Tick(); }; tick.Start();
            sleepLater.Interval = 3000; sleepLater.Tick += delegate { sleepLater.Stop(); GoToSleep(); };
            // остались настройки от прошлого запуска (сбой) — вернуть
            try { if (Watchdog.RestoreFromFile(Store.Saved, true)) Store.Note("При запуске найдены несохранённые настройки, возвращены."); } catch (Exception e) { Store.Note("Возврат при запуске: " + e.Message); }
            try { Watchdog.Install(Application.ExecutablePath); } catch { }
            Watchdog.StartGuard(Application.ExecutablePath);
            Pulse();
            Refresh2();
            Tick();
        }

        System.Windows.Forms.Timer sleepLater = new System.Windows.Forms.Timer();
        Guid onScheme;              // схема, которую мы изменили
        bool autoHeld;              // авто не включать, пока агент не затихнет (выключили вручную или по заряду/таймеру)

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterPowerSettingNotification(Handle, ref LidSwitch, 0);
        }
        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }

        const int WM_QUERYENDSESSION = 0x11, WM_ENDSESSION = 0x16, PBT_APMRESUMEAUTOMATIC = 0x12;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_POWERBROADCAST && m.WParam.ToInt32() == PBT_POWERSETTINGCHANGE)
            {
                PowerBroadcastSetting ps = (PowerBroadcastSetting)Marshal.PtrToStructure(m.LParam, typeof(PowerBroadcastSetting));
                if (ps.PowerSetting == LidSwitch) OnLid(Policy.LidClosed(ps.Data));
            }
            if (m.Msg == WM_POWERBROADCAST && m.WParam.ToInt32() == PBT_APMRESUMEAUTOMATIC) Pulse();
            if (m.Msg == WM_QUERYENDSESSION || (m.Msg == WM_ENDSESSION && m.WParam != IntPtr.Zero))
                Stop("выключение или перезагрузка Windows", false, StopKind.System);
            base.WndProc(ref m);
        }

        void OnLid(bool closed)
        {
            lidClosed = closed;
            Store.Note(closed ? "Крышка закрыта." : "Крышка открыта.");
            // экран ноутбука при закрытой крышке гаснет сам; гасить дисплеи командой нельзя:
            // на ноутбуках с Modern Standby это уводит систему в сон
            if (closed && on && prefs.LockOnLid) LockWorkStation();
        }

        // ---- включение / выключение ----
        void Start(bool auto)
        {
            if (on) return;
            try
            {
                Guid scheme = Power.ActiveScheme();
                Policy.Begin(scheme, true);
                SetThreadExecutionState(Policy.AwakeFlags);
                on = true; byAgent = auto; onScheme = scheme;
                untilUtc = !auto && prefs.Hours > 0 ? (DateTime?)DateTime.UtcNow.AddHours(prefs.Hours) : null;
                agentQuietSince = null;
                Store.Note(auto ? "Включено: работает агент." : "Включено вручную.");
                Pulse();
            }
            catch (Exception e)
            {
                Store.Note("Не включилось: " + e.Message);
                try { if (File.Exists(Store.Saved)) Watchdog.RestoreFromFile(Store.Saved, true); } catch { }
                tray.ShowBalloonTip(5000, "ClamDown", "Не удалось включить: " + e.Message, ToolTipIcon.Error);
            }
            Refresh2();
        }

        /// <summary>kind = Limit: усыпить ноутбук, если крышка закрыта (заряд, таймер, перегрев, конец работы агента).</summary>
        void Stop(string reason, bool notify, StopKind kind)
        {
            if (!on) return;
            if (Policy.HoldAfterStop(byAgent, prefs.Auto)) autoHeld = true; // пока агент работает, само не включаться
            on = false; byAgent = false; untilUtc = null;
            SetThreadExecutionState(ES_CONTINUOUS);
            try { Watchdog.RestoreFromFile(Store.Saved, true); } catch (Exception e) { Store.Note("Возврат настроек: " + e.Message); }
            Store.Note("Выключено: " + reason + ".");
            Refresh2();
            if (notify) tray.ShowBalloonTip(8000, "ClamDown выключен", "Причина: " + reason + ". Настройки сна вернулись.", ToolTipIcon.Info);
            if (Policy.SleepAfter(kind, lidClosed)) sleepLater.Start();
        }

        void GoToSleep()
        {
            if (on || !lidClosed) return;
            if (!SetSuspendState(false, false, false))
                // Modern Standby: обычный сон недоступен, выключение дисплея уводит систему в режим ожидания
                SendMessage((IntPtr)0xFFFF, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)2);
        }

        void Toggle() { if (on) Stop("вручную", false, StopKind.Manual); else { autoHeld = false; Start(false); } }

        // ---- каждые 15 секунд ----
        void Tick()
        {
            Pulse();
            DateTime now = DateTime.UtcNow;
            // состояние разошлось: страховка уже вернула настройки (например, после сна) или сменили схему питания
            if (on && !File.Exists(Store.Saved)) { on = false; byAgent = false; SetThreadExecutionState(ES_CONTINUOUS); Store.Note("Настройки вернула страховка, режим снят."); }
            if (on) { try { if (Power.ActiveScheme() != onScheme) Stop("сменили схему питания", true, StopKind.Manual); } catch { } }

            PowerStatus ps = SystemInformation.PowerStatus;
            bool battery = ps.PowerLineStatus == PowerLineStatus.Offline;
            int pct = ps.BatteryChargeStatus == BatteryChargeStatus.NoSystemBattery ? -1 : (int)Math.Round(ps.BatteryLifePercent * 100);
            double? temp = Thermal.Read();
            string batteryWhy = Rules.StopReason(battery, pct, prefs.Floor, null, now, temp, prefs.TempMax);

            if (prefs.Auto)
            {
                bool working = Agents.Working(Agents.Home, now);
                if (!working) autoHeld = false;
                if (working) { agentQuietSince = null; if (Policy.AutoStart(working, on, autoHeld, batteryWhy)) Start(true); }
                else if (on && byAgent)
                {
                    if (!agentQuietSince.HasValue) agentQuietSince = now;
                    else if (Policy.AgentDone(agentQuietSince, now)) Stop("агент закончил работу", true, StopKind.Limit);
                }
            }
            if (on)
            {
                string why = Rules.StopReason(battery, pct, prefs.Floor, untilUtc, now, temp, prefs.TempMax);
                if (why != null) Stop(why, true, StopKind.Limit);
            }
            Refresh2();
        }

        void Pulse() { try { Store.Write(Store.Pulse, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)); } catch { } }

        // ---- меню ----
        void BuildMenu()
        {
            menu.Font = new Font("Segoe UI", 9.5f);
            itemOn = new ToolStripMenuItem("Не спать при закрытой крышке", null, delegate { Toggle(); });
            itemOn.Font = new Font(menu.Font, FontStyle.Bold);
            itemStatus = new ToolStripMenuItem("") { Enabled = false };
            itemTimer = new ToolStripMenuItem("Таймер");
            foreach (int h in new int[] { 1, 2, 4, 8, 0 })
            {
                int hh = h;
                itemTimer.DropDownItems.Add(new ToolStripMenuItem(h == 0 ? "Без ограничения" : h + " ч", null, delegate {
                    prefs.Hours = hh; prefs.Save();
                    if (on && !byAgent) untilUtc = hh > 0 ? (DateTime?)DateTime.UtcNow.AddHours(hh) : null;
                    Refresh2(); }) { Tag = hh });
            }
            itemFloor = new ToolStripMenuItem("Выключить при заряде");
            foreach (int f in new int[] { 10, 15, 20, 30 })
            {
                int ff = f;
                itemFloor.DropDownItems.Add(new ToolStripMenuItem(f + "%", null, delegate { prefs.Floor = ff; prefs.Save(); Refresh2(); }) { Tag = ff });
            }
            itemAuto = new ToolStripMenuItem("Включаться, пока работает агент (Claude Code, Codex)", null, delegate { prefs.Auto = !prefs.Auto; prefs.Save(); Refresh2(); });
            itemLock = new ToolStripMenuItem("Блокировать экран при закрытии крышки", null, delegate { prefs.LockOnLid = !prefs.LockOnLid; prefs.Save(); Refresh2(); });
            itemStartup = new ToolStripMenuItem("Запускать вместе с Windows", null, delegate { SetStartup(!StartupOn()); Refresh2(); });
            ToolStripMenuItem vk = new ToolStripMenuItem("Нейросети и AI на практике — наше сообщество ВКонтакте", null, delegate { Open(Vk); });
            vk.ForeColor = Color.FromArgb(0, 119, 255);
            menu.Items.AddRange(new ToolStripItem[] {
                itemOn, itemStatus, new ToolStripSeparator(), itemTimer, itemFloor, itemAuto, itemLock, itemStartup,
                new ToolStripSeparator(), vk,
                new ToolStripMenuItem("О программе", null, delegate { About(); }),
                new ToolStripMenuItem("Выход", null, delegate { Quit(); }) });
        }

        void Refresh2()
        {
            itemOn.Checked = on;
            foreach (ToolStripMenuItem i in itemTimer.DropDownItems) i.Checked = (int)i.Tag == prefs.Hours;
            foreach (ToolStripMenuItem i in itemFloor.DropDownItems) i.Checked = (int)i.Tag == prefs.Floor;
            itemAuto.Checked = prefs.Auto; itemLock.Checked = prefs.LockOnLid; itemStartup.Checked = StartupOn();
            string st;
            if (!on) st = "Сейчас выключено: крышка работает как обычно";
            else if (byAgent) st = "Включено, пока работает агент";
            else if (untilUtc.HasValue) st = "Включено, осталось " + Rules.Left(untilUtc.Value - DateTime.UtcNow);
            else st = "Включено без ограничения по времени";
            itemStatus.Text = st;
            tray.Icon = on ? iconOn : iconOff;
            string tip = "ClamDown: " + (on ? "не спит с закрытой крышкой" : "выключен");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        static void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

        void About() { using (Form f = BuildAbout()) f.ShowDialog(); }

        static Form BuildAbout()
        {
            Form f = new Form();
            f.Text = "О программе"; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false;
            f.StartPosition = FormStartPosition.CenterScreen; f.ClientSize = new Size(460, 300); f.BackColor = Color.White; f.ShowIcon = false;
            f.Font = new Font("Segoe UI", 10f);
            Label t = new Label { Text = "ClamDown для Windows " + Version, Font = new Font("Segoe UI Semibold", 14f), AutoSize = true, Location = new Point(24, 20) };
            Label d = new Label
            {
                Text = "Ноутбук не засыпает при закрытой крышке, пока вы этого хотите или пока работает ИИ-агент. " +
                       "Сам выключается по таймеру, при низком заряде и при перегреве, если Windows сообщает температуру.\n\n" +
                       "Неофициальная версия для Windows. Идея — ClamDown для macOS (delef). Вентиляторами Windows-версия не управляет.",
                Location = new Point(24, 62), Size = new Size(412, 140)
            };
            LinkLabel vk = new LinkLabel { Text = "Сделано в сообществе «Нейросети и AI на практике»", Location = new Point(24, 208), AutoSize = true };
            vk.LinkClicked += delegate { Open(Vk); };
            LinkLabel orig = new LinkLabel { Text = "Оригинал для Mac: github.com/delef/ClamDown", Location = new Point(24, 236), AutoSize = true };
            orig.LinkClicked += delegate { Open("https://github.com/delef/ClamDown"); };
            Button ok = new Button { Text = "OK", Location = new Point(360, 258), Size = new Size(76, 30), DialogResult = DialogResult.OK };
            f.Controls.AddRange(new Control[] { t, d, vk, orig, ok });
            f.AcceptButton = ok;
            return f;
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static bool StartupOn()
        {
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("ClamDownWin") != null; } catch { return false; }
        }
        static void SetStartup(bool v)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                    if (v) k.SetValue("ClamDownWin", "\"" + Application.ExecutablePath + "\""); else k.DeleteValue("ClamDownWin", false);
            }
            catch { }
        }

        void Quit()
        {
            Stop("вручную", false, StopKind.Manual);
            try { File.Delete(Store.Pulse); } catch { }
            tray.Visible = false;
            Application.Exit();
        }
        protected override void OnFormClosing(FormClosingEventArgs e) { if (on) Stop("программа закрыта", false, StopKind.System); tray.Visible = false; base.OnFormClosing(e); }

        // ---- значок: раскрытая раковина-ноутбук; зелёная точка — режим включён ----
        static Icon MakeIcon(bool active)
        {
            Bitmap b = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                Color c = active ? Color.FromArgb(124, 92, 255) : Color.FromArgb(120, 120, 130);
                using (Brush br = new SolidBrush(c))
                {
                    g.FillRectangle(br, 6, 6, 20, 13);                                   // крышка
                    g.FillPolygon(br, new Point[] { new Point(2, 24), new Point(30, 24), new Point(27, 20), new Point(5, 20) }); // основание
                }
                using (Brush inner = new SolidBrush(Color.White)) g.FillRectangle(inner, 8, 8, 16, 9);
                if (active) using (Brush dot = new SolidBrush(Color.FromArgb(46, 204, 113))) g.FillEllipse(dot, 18, 17, 13, 13);
            }
            return Icon.FromHandle(b.GetHicon());
        }

        // ---------------- точка входа ----------------
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--watchdog") return Watchdog.Run();
            int gp;
            if (args.Length > 1 && args[0] == "--guard" && int.TryParse(args[1], out gp)) return Watchdog.Guard(gp);
            if (args.Length > 1 && args[0] == "--selftest") return SelfTest.Run(args[1]);
            if (args.Length > 1 && args[0] == "--shot") return Shot(args[1]);
            // служебное: снимок текущих настроек питания в файл и запись снимка обратно (для мутационной проверки)
            if (args.Length > 1 && args[0] == "--dump")
            {
                Guid g = Power.ActiveScheme();
                File.WriteAllText(args[1], Power.Serialize(g, Power.Read(g)), Encoding.ASCII);
                return 0;
            }
            if (args.Length > 1 && args[0] == "--apply")
            {
                Guid g; uint?[][] v;
                if (!Power.TryParse(File.ReadAllText(args[1]), out g, out v)) return 2;
                Power.Write(g, v, true);
                return 0;
            }
            if (args.Length > 0 && args[0] == "--uninstall")
            {
                try { Watchdog.RestoreFromFile(Store.Saved, true); } catch { }
                Watchdog.Remove(); SetStartup(false);
                return 0;
            }
            bool created;
            using (Mutex m = new Mutex(true, MutexName, out created))
            {
                if (!created) { MessageBox.Show("ClamDown уже запущен: значок в трее.", "ClamDown"); return 0; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App());
            }
            return 0;
        }
    }

    // ---------------- самопроверка (на временной копии схемы питания) ----------------
    public static class SelfTest
    {
        static StringBuilder log = new StringBuilder();
        static int fails;
        static void Ok(bool cond, string name) { log.AppendLine((cond ? "OK   " : "FAIL ") + name); if (!cond) fails++; }

        public static int Run(string outFile)
        {
            try
            {
                // 1. Правила выключения
                DateTime n = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
                Ok(Rules.StopReason(true, 15, 15, null, n, null, 90) == "заряд упал до 15%", "порог заряда на батарее");
                Ok(Rules.StopReason(false, 5, 15, null, n, null, 90) == null, "на зарядке низкий заряд не выключает");
                Ok(Rules.StopReason(true, -1, 15, null, n, null, 90) == null, "без батареи заряд не проверяется");
                Ok(Rules.StopReason(false, 80, 15, n, n, null, 90) == "кончилось время", "таймер");
                Ok(Rules.StopReason(false, 80, 15, n.AddMinutes(1), n, null, 90) == null, "таймер ещё идёт");
                Ok(Rules.StopReason(false, 80, 15, null, n, 91.2, 90) == "перегрев: 91 °C", "перегрев");
                Ok(Rules.StopReason(false, 80, 15, null, n, null, 90) == null, "температуры нет — не выключать");
                Ok(Rules.Left(TimeSpan.FromMinutes(102)) == "1 ч 42 мин", "остаток времени");
                Ok(Rules.Left(TimeSpan.FromMinutes(-5)) == "0 ч 00 мин", "остаток после срока — ноль");
                Ok(Rules.StopReason(false, 80, 15, null, n, 90.0, 90) == "перегрев: 90 °C", "перегрев ровно на пороге");

                // 1б. Решения программы в трее
                Ok(Policy.AutoStart(true, false, false, null), "агент работает — включиться");
                Ok(!Policy.AutoStart(true, false, false, "заряд упал до 10%"), "агент работает, но заряд низкий — не включаться");
                Ok(!Policy.AutoStart(true, false, true, null), "выключили вручную — само не включаться");
                Ok(!Policy.AutoStart(false, false, false, null), "агент не работает — не включаться");
                Ok(!Policy.AutoStart(true, true, false, null), "уже включено — не включать заново");
                Ok(Policy.HoldAfterStop(true, false) && Policy.HoldAfterStop(false, true) && !Policy.HoldAfterStop(false, false), "придержать авто после выключения");
                Ok(!Policy.AgentDone(n.AddMinutes(-4), n) && Policy.AgentDone(n.AddMinutes(-5), n) && !Policy.AgentDone(null, n), "агент затих 5 мин — выключаться");
                Ok(Policy.LidClosed(0) && !Policy.LidClosed(1), "крышка: 0 — закрыта");
                Ok((Policy.AwakeFlags & Policy.ES_DISPLAY_REQUIRED) != 0 && (Policy.AwakeFlags & Policy.ES_SYSTEM_REQUIRED) != 0 && (Policy.AwakeFlags & Policy.ES_CONTINUOUS) != 0, "флаги: система и экран не спят");
                Ok(Policy.SleepAfter(StopKind.Limit, true), "по заряду или таймеру с закрытой крышкой — усыпить");
                Ok(!Policy.SleepAfter(StopKind.Limit, false), "крышка открыта — не усыплять");
                Ok(!Policy.SleepAfter(StopKind.System, true) && !Policy.SleepAfter(StopKind.Manual, true), "выключение Windows или вручную — не усыплять");

                // 2. Страховка
                Ok(!Watchdog.ShouldRestore(false, null, n, false), "нет сохранённых — ничего не делать");
                Ok(Watchdog.ShouldRestore(true, null, n, false), "сохранённые есть, пульса нет — вернуть");
                Ok(Watchdog.ShouldRestore(true, n.AddSeconds(-91), n, false), "пульс старше 90 с — вернуть");
                Ok(!Watchdog.ShouldRestore(true, n.AddSeconds(-30), n, true), "программа жива — не трогать");
                Ok(Watchdog.ShouldRestore(true, n.AddSeconds(-200), n, true), "программа зависла (пульс старый) — вернуть");
                Ok(Watchdog.ShouldRestore(true, n.AddSeconds(-90), n, true), "пульс ровно 90 с — вернуть");
                Ok(Watchdog.GuardDecide(false, false, null, n) == Watchdog.GuardStep.Done, "сторож: настроек нет — конец");
                Ok(Watchdog.GuardDecide(true, true, n.AddSeconds(-200), n) == Watchdog.GuardStep.Done, "сторож: программу запустили снова — конец");
                Ok(Watchdog.GuardDecide(true, false, n.AddSeconds(-30), n) == Watchdog.GuardStep.Wait, "сторож: пульс свежий — ждать");
                Ok(Watchdog.GuardDecide(true, false, n.AddSeconds(-95), n) == Watchdog.GuardStep.Restore, "сторож: тишина 95 с — вернуть");

                // 3. Агенты
                string tmp = Path.Combine(Path.GetTempPath(), "clamdown_selftest_" + Guid.NewGuid().ToString("N"));
                string proj = Path.Combine(tmp, ".claude", "projects", "x");
                Directory.CreateDirectory(proj);
                Ok(Agents.LastActivity(tmp) == null, "сессий нет");
                string f = Path.Combine(proj, "s.jsonl");
                File.WriteAllText(f, "{}");
                File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddSeconds(-30));
                Ok(Agents.Working(Agents.LastActivity(tmp), DateTime.UtcNow), "сессия менялась 30 с назад — агент работает");
                File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-10));
                Ok(!Agents.Working(Agents.LastActivity(tmp), DateTime.UtcNow), "сессия молчит 10 мин — не работает");
                string ses = Path.Combine(tmp, ".claude", "sessions");
                Directory.CreateDirectory(ses);
                File.WriteAllText(Path.Combine(ses, "123.json"), "{\"pid\":123,\"status\":\"idle\"}");
                Ok(!Agents.BusySession(tmp, delegate (int p) { return true; }), "сессия ждёт (idle) — не работает");
                File.WriteAllText(Path.Combine(ses, "123.json"), "{\"pid\":123,\"status\":\"busy\"}");
                Ok(Agents.BusySession(tmp, delegate (int p) { return p == 123; }), "сессия занята, процесс жив — работает");
                Ok(!Agents.BusySession(tmp, delegate (int p) { return false; }), "сессия занята, но процесс умер — не работает");
                string cx = Path.Combine(tmp, ".codex", "sessions", DateTime.Now.ToString("yyyy"), DateTime.Now.ToString("MM"), DateTime.Now.ToString("dd"));
                Directory.CreateDirectory(cx);
                File.WriteAllText(Path.Combine(cx, "r.jsonl"), "{}");
                Ok(Agents.Working(Agents.LastActivity(tmp), DateTime.UtcNow), "сессия Codex за сегодня — работает");
                Directory.Delete(tmp, true);

                // 4. Формат файла сохранённых настроек
                uint?[][] sample = new uint?[][] { new uint?[] { 1, 1 }, new uint?[] { 900, null }, new uint?[] { 0, 10800 }, new uint?[] { 120, 120 }, new uint?[] { 600, 300 }, new uint?[] { 60, 60 } };
                Guid sg = Guid.NewGuid();
                string ser = Power.Serialize(sg, sample);
                Guid pg; uint?[][] pv;
                Ok(Power.TryParse(ser, out pg, out pv) && pg == sg && Same(pv, sample), "запись и чтение файла настроек");
                Ok(!Power.TryParse("мусор", out pg, out pv), "битый файл не принимается");
                Ok(!Power.TryParse(sg + ";1,1", out pg, out pv), "обрезанный файл не принимается");
                uint?[][] half = new uint?[][] { new uint?[] { 1, null }, new uint?[] { null, 1800 }, new uint?[] { 5, 5 }, new uint?[] { 5, 5 }, new uint?[] { 5, 5 }, new uint?[] { 5, 5 } };
                uint?[][] aw = Power.AwakeFor(half);
                Ok(aw[0][0] == 0 && !aw[0][1].HasValue && !aw[1][0].HasValue && aw[1][1] == 0, "«не спать» только там, где параметр есть");
                HashSet<string> pairs = new HashSet<string>();
                foreach (Guid[] it in Power.Items) pairs.Add(it[0] + "/" + it[1]);
                Ok(pairs.Count == Power.Items.Length && pairs.Contains(Power.SubVideo + "/" + Power.VideoConLock) && pairs.Contains(Power.SubButtons + "/" + Power.LidAction), "меняем крышку, сон, экран и экран блокировки — без повторов");
                Ok(Power.TryParse(sg + ";1,1;0,1800;900,900;120,120", out pg, out pv) && pv[0][0] == 1 && pv[1][1] == 1800 && !pv[4][0].HasValue, "файл версии 1.0 (4 параметра) читается");

                // 5. Настоящие настройки питания — на временной КОПИИ схемы (активная схема не меняется)
                Guid active = Power.ActiveScheme();
                uint?[][] before = Power.Read(active);
                Guid copy = Power.Duplicate(active);
                try
                {
                    uint?[][] orig = Power.Read(copy);
                    Ok(Same(orig, before), "копия схемы совпадает с вашей");
                    // разные значения для сети и батареи читаются раздельно
                    uint?[][] probe = new uint?[Power.Items.Length][];
                    for (int i = 0; i < probe.Length; i++) probe[i] = new uint?[2];
                    probe[1] = new uint?[] { 600, 1200 };
                    Power.Write(copy, probe, false);
                    uint?[][] pr = Power.Read(copy);
                    Ok(pr[1][0] == 600 && pr[1][1] == 1200, "сон: сеть 600 и батарея 1200 читаются раздельно");
                    Power.Write(copy, orig, false);
                    // Begin: хвост прошлого раза сначала возвращается, исходные настройки не теряются
                    string realDir = Store.Dir;
                    Store.Dir = Path.Combine(Path.GetTempPath(), "clamdown_selftest_store_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        Policy.Begin(copy, false);
                        Policy.Begin(copy, false); // второй раз подряд, как после сбоя
                        Guid sg2; uint?[][] sv2;
                        Ok(Power.TryParse(File.ReadAllText(Store.Saved), out sg2, out sv2) && Same(sv2, orig), "двойной запуск не затирает исходные настройки");
                        Ok(Watchdog.RestoreFromFile(Store.Saved, false) && Same(Power.Read(copy), orig), "после двойного запуска всё возвращается");
                        File.WriteAllText(Store.Saved, "битый файл");
                        Ok(!Watchdog.RestoreFromFile(Store.Saved, false) && !File.Exists(Store.Saved), "битый файл сохранённых удаляется");
                    }
                    finally { try { Directory.Delete(Store.Dir, true); } catch { } Store.Dir = realDir; }
                    Ok(orig[0][0].HasValue && orig[0][1].HasValue, "действие крышки читается");
                    string savedPath = Path.Combine(Path.GetTempPath(), "clamdown_saved_" + Guid.NewGuid().ToString("N") + ".txt");
                    File.WriteAllText(savedPath, Power.Serialize(copy, orig));
                    Power.Write(copy, Power.AwakeFor(orig), false);
                    uint?[][] awake = Power.Read(copy);
                    Ok(awake[0][0] == 0 && awake[0][1] == 0, "крышка: «ничего не делать» записано");
                    Ok(awake[1][0] == 0 && awake[1][1] == 0, "сон: «никогда» записано");
                    Ok((!orig[5][0].HasValue || awake[5][0] == 0) && (!orig[4][0].HasValue || awake[4][0] == 0), "экран и экран блокировки: «никогда» записано");
                    log.AppendLine("INFO экран: " + orig[4][0] + "/" + orig[4][1] + " с, экран блокировки: " + orig[5][0] + "/" + orig[5][1] + " с");
                    Ok(Watchdog.RestoreFromFile(savedPath, false) && !File.Exists(savedPath), "возврат из файла");
                    Ok(Same(Power.Read(copy), orig), "после возврата настройки как были");
                }
                finally { Power.Delete(copy); }
                Ok(Power.ActiveScheme() == active && Same(Power.Read(active), before), "ваша схема не тронута");
                double? t = Thermal.Read();
                log.AppendLine("INFO температура: " + (t.HasValue ? Math.Round(t.Value) + " °C" : "Windows не отдаёт"));
            }
            catch (Exception e) { log.AppendLine("FAIL исключение: " + e); fails++; }
            log.AppendLine(fails == 0 ? "ИТОГ: всё прошло" : "ИТОГ: ошибок " + fails);
            File.WriteAllText(outFile, log.ToString(), Encoding.UTF8);
            return fails == 0 ? 0 : 1;
        }

        static bool Same(uint?[][] a, uint?[][] b)
        {
            for (int i = 0; i < a.Length; i++) for (int k = 0; k < 2; k++) if (a[i][k] != b[i][k]) return false;
            return true;
        }
    }
}
