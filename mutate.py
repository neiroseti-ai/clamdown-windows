# Мутационная проверка самопроверки ClamDown для Windows. Каждая мутация — намеренная ошибка в ClamDown.cs;
# самопроверка (--selftest) обязана упасть. Мутант собирается во временную папку и запускается там.
# Защита: перед прогоном программа остановлена, после каждого мутанта активная схема питания и её значения
# сверяются с исходными; если мутант что-то изменил — всё возвращается и это пишется в отчёт.
# Запуск: python mutate.py  (из папки clamdown_win; программа ClamDownWin должна быть остановлена)
import io, os, subprocess, sys, tempfile, time, re

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = io.open(os.path.join(HERE, "ClamDown.cs"), encoding="utf-8").read()
EXE = os.path.join(HERE, "ClamDownWin.exe")  # исходная сборка: читает и возвращает настройки
CSC = os.path.join(os.environ["WINDIR"], r"Microsoft.NET\Framework64\v4.0.30319\csc.exe")
WORK = os.path.join(tempfile.gettempdir(), "clamdown_mut")
OUT = os.path.join(HERE, "mut_out.txt")
os.makedirs(WORK, exist_ok=True)

MUT = [
    # --- правила выключения ---
    ("заряд: <= → <", "percent <= floor", "percent < floor"),
    ("заряд: без условия «на батарее»", "if (onBattery && percent >= 0 && percent <= floor)", "if (percent >= 0 && percent <= floor)"),
    ("заряд: без проверки «нет батареи»", "onBattery && percent >= 0 && percent <= floor", "onBattery && percent <= floor"),
    ("таймер: >= → >", "nowUtc >= untilUtc.Value", "nowUtc > untilUtc.Value"),
    ("таймер: >= → <", "nowUtc >= untilUtc.Value", "nowUtc < untilUtc.Value"),
    ("перегрев: >= → <=", "tempC.Value >= tempMax", "tempC.Value <= tempMax"),
    ("перегрев: без HasValue", "if (tempC.HasValue && tempC.Value >= tempMax)", "if (tempC.GetValueOrDefault() >= tempMax)"),
    ("остаток: часы вместо минут", 't.Minutes.ToString("00"', 't.Hours.ToString("00"'),
    ("остаток: отрицательное не обнуляется", "if (t.TotalSeconds < 0) t = TimeSpan.Zero;", ""),
    # --- страховка ---
    ("страховка: нет сохранённых → вернуть", "if (!savedExists) return false;", "if (!savedExists) return true;"),
    ("страховка: тишина < → <=", "nowUtc - lastPulseUtc.Value < Silence) return false;", "nowUtc - lastPulseUtc.Value <= Silence) return false;"),
    ("страховка: без пульса → не возвращать", "if (!lastPulseUtc.HasValue) return true;", "if (!lastPulseUtc.HasValue) return false;"),
    ("страховка: >= Silence → < Silence", "return nowUtc - lastPulseUtc.Value >= Silence;", "return nowUtc - lastPulseUtc.Value < Silence;"),
    ("страховка: жива — всегда не трогать", "if (appRunning && lastPulseUtc.HasValue && nowUtc - lastPulseUtc.Value < Silence) return false;", "if (appRunning) return false;"),
    ("страховка: Silence 90 → 30 с", "TimeSpan.FromSeconds(90);", "TimeSpan.FromSeconds(30);"),
    ("возврат: файл не удаляется", "            Power.Write(scheme, v, apply && scheme == Power.ActiveScheme());\n            File.Delete(savedPath);", "            Power.Write(scheme, v, apply && scheme == Power.ActiveScheme());"),
    ("возврат: не пишет настройки", "            Power.Write(scheme, v, apply && scheme == Power.ActiveScheme());\n", ""),
    ("возврат: битый файл не удаляется", "{ File.Delete(savedPath); return false; }", "{ return false; }"),
    # --- агенты ---
    ("агент: Fresh 2 мин → 20 мин", "TimeSpan.FromMinutes(2);", "TimeSpan.FromMinutes(20);"),
    ("агент: <= Fresh → >= Fresh", "nowUtc - last.Value <= Fresh", "nowUtc - last.Value >= Fresh"),
    ("агент: idle считается занятым", 'status != null && status != "idle"', 'status != null'),
    ("агент: не проверять, жив ли процесс", "int.TryParse(pid, out p) && alive(p)", "int.TryParse(pid, out p)"),
    ("агент: новейший файл → старейший", "if (!best.HasValue || t > best.Value) best = t;", "if (!best.HasValue || t < best.Value) best = t;"),
    ("агент: Codex за сегодня не смотрим", "foreach (DateTime d in new DateTime[] { DateTime.Now, DateTime.Now.AddDays(-1) })", "foreach (DateTime d in new DateTime[] { DateTime.Now.AddDays(-1) })"),
    ("агент: только папки проектов, без файлов в них", 'foreach (FileInfo fi in new DirectoryInfo(dir).EnumerateFiles("*.jsonl"))', 'foreach (FileInfo fi in new DirectoryInfo(dir).EnumerateFiles("*.json"))'),
    ("агент: поле читается без кавычек", 'm.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value', 'm.Groups[3].Value'),
    # --- файл настроек ---
    ("файл: сеть и батарея меняются местами", ".Append(',').Append(p[1].HasValue", ".Append(',').Append(p[0].HasValue"),
    ("файл: пустое значение как 0", 'if (ab[k] == "-") continue;', 'if (ab[k] == "-") { v[i][k] = 0; continue; }'),
    ("файл: мусор принимается", "if (parts.Length < 5 || parts.Length > Items.Length + 1) return false;", ""),
    ("файл: старая версия (4 параметра) не читается", "if (parts.Length < 5 ||", "if (parts.Length < Items.Length + 1 ||"),
    ("файл: схема не читается", "try { scheme = new Guid(parts[0]); } catch { return false; }", "scheme = Guid.Empty;"),
    # --- запись настроек питания ---
    ("питание: «не спать» = 1 вместо 0", "public static readonly uint[] AwakeValues = new uint[] { 0, 0, 0, 0, 0, 0 };", "public static readonly uint[] AwakeValues = new uint[] { 1, 0, 0, 0, 0, 0 };"),
    ("питание: батарея не пишется", "                if (values[i][1].HasValue) Check(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, values[i][1].Value), i);\n", ""),
    ("питание: сеть пишется в батарею", "Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, values[i][0].Value), i);", "Check(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, values[i][0].Value), i);"),
    ("питание: AwakeFor берёт сеть для батареи", "saved[i][1].HasValue ? (uint?)AwakeValues[i] : null", "saved[i][0].HasValue ? (uint?)AwakeValues[i] : null"),
    ("питание: AwakeFor пишет и отсутствующие", "saved[i][0].HasValue ? (uint?)AwakeValues[i] : null, saved[i][1]", "(uint?)AwakeValues[i], saved[i][1]"),
    ("питание: чтение батареи = сеть", "uint rd = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out dc);", "uint rd = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out dc);"),
    ("питание: экран блокировки не меняется", "new Guid[] { SubVideo, VideoIdle }, new Guid[] { SubVideo, VideoConLock } };", "new Guid[] { SubVideo, VideoIdle }, new Guid[] { SubVideo, VideoIdle } };"),
    # --- решения программы в трее (вынесены в Policy) ---
    ("трей: авто включается при низком заряде", "return working && !on && !held && stopReason == null;", "return working && !on && !held;"),
    ("трей: ручное выключение не держит авто", "return byAgent || autoEnabled;", "return false;"),
    ("трей: агент закончил — 5 мин → 0", "public static readonly TimeSpan AgentGrace = TimeSpan.FromMinutes(5);", "public static readonly TimeSpan AgentGrace = TimeSpan.FromMinutes(0);"),
    ("трей: Begin не возвращает хвост прошлого раза", "            if (File.Exists(Store.Saved)) Watchdog.RestoreFromFile(Store.Saved, apply);\n", ""),
    ("трей: выключение Windows усыпляет", "return lidClosed && kind == StopKind.Limit;", "return lidClosed && kind != StopKind.Manual;"),
    ("трей: усыпляет и с открытой крышкой", "return lidClosed && kind == StopKind.Limit;", "return kind == StopKind.Limit;"),
    ("трей: крышка 0 = открыта", "return data == 0;", "return data != 0;"),
    ("трей: без ES_DISPLAY_REQUIRED", "AwakeFlags = ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED;", "AwakeFlags = ES_CONTINUOUS | ES_SYSTEM_REQUIRED;"),
    ("сторож: не ждёт тишины пульса", "return ShouldRestore(true, pulse, nowUtc, false) ? GuardStep.Restore : GuardStep.Wait;", "return GuardStep.Restore;"),
    ("сторож: не замечает перезапуск программы", "if (!savedExists || appRunning) return GuardStep.Done;", "if (!savedExists) return GuardStep.Done;"),
]

DUMP = os.path.join(WORK, "now.txt")
def state():
    schemes = sorted(set(re.findall(r"[0-9a-f]{8}-[0-9a-f-]{27}", subprocess.run(["powercfg", "/list"], capture_output=True, text=True, errors="replace").stdout)))
    subprocess.run([EXE, "--dump", DUMP], timeout=60)
    return io.open(DUMP, encoding="ascii").read().strip(), schemes

def repair(base, schemes0):
    subprocess.run(["powercfg", "/setactive", base.split(";")[0]], capture_output=True)
    for g in state()[1]:
        if g not in schemes0: subprocess.run(["powercfg", "/delete", g], capture_output=True)
    f = os.path.join(WORK, "base.txt"); io.open(f, "w", encoding="ascii").write(base)
    subprocess.run([EXE, "--apply", f], timeout=60)

def main():
    if "ClamDownWin.exe" in subprocess.run(["tasklist"], capture_output=True, text=True, errors="replace").stdout:
        sys.exit("Сначала остановите ClamDownWin: мутанты меняют копии схемы питания.")
    base, schemes0 = state()
    lines = ["Мутационная проверка ClamDown.cs, " + time.strftime("%d.%m.%Y %H:%M"), "Исходные настройки: " + base, ""]
    killed = survived = broke = 0
    for i, (name, a, b) in enumerate(MUT, 1):
        n = SRC.count(a)
        if n != 1:
            lines.append("%2d ОШИБКА МУТАЦИИ (найдено %d раз): %s" % (i, n, name)); print(lines[-1], flush=True); continue
        cs = os.path.join(WORK, "m.cs"); exe = os.path.join(WORK, "m%d.exe" % i); res = os.path.join(WORK, "r%d.txt" % i)
        io.open(cs, "w", encoding="utf-8").write(SRC.replace(a, b))
        c = subprocess.run([CSC, "/nologo", "/target:winexe", "/out:" + exe, "/r:System.Management.dll", "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll", cs],
                           capture_output=True, text=True, errors="replace")
        if c.returncode != 0:
            verdict = "не собрался (считается убитым)"; killed += 1
        else:
            try:
                r = subprocess.run([exe, "--selftest", res], timeout=120)
                fails = [l for l in io.open(res, encoding="utf-8").read().splitlines() if l.startswith("FAIL")] if os.path.exists(res) else ["нет отчёта"]
                if r.returncode != 0: verdict = "УБИТ: " + (fails[0][5:] if fails else "код " + str(r.returncode)); killed += 1
                else: verdict = "ВЫЖИЛ"; survived += 1
            except subprocess.TimeoutExpired:
                subprocess.run(["taskkill", "/F", "/IM", os.path.basename(exe)], capture_output=True)
                verdict = "УБИТ: завис"; killed += 1
        now, schemes = state()
        if now != base or schemes != schemes0:
            repair(base, schemes0); broke += 1
            verdict += " | ВНИМАНИЕ: мутант изменил настройки, возвращено: " + ("ок" if state()[0] == base else "НЕ УДАЛОСЬ")
        lines.append("%2d %-55s %s" % (i, name, verdict)); print(lines[-1], flush=True)
        for f in (exe, res):
            try: os.remove(f)
            except OSError: pass
    lines += ["", "Итог: убито %d, выжило %d из %d; мутантов, тронувших настройки: %d" % (killed, survived, len(MUT), broke),
              "Настройки после прогона: " + ("совпадают с исходными" if state()[0] == base else "ОТЛИЧАЮТСЯ")]
    io.open(OUT, "w", encoding="utf-8").write("\n".join(lines) + "\n")
    print(lines[-2]); print(lines[-1])

if __name__ == "__main__":
    main()
