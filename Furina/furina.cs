// -*- coding: utf-8 -*-
/*
 * furina.exe - 芙宁娜整合启动器（核心逻辑，GUI 见 furina_gui.cs）
 *
 * 职责：一键拉起并监护四个组件——
 *   NewAPI(3000，可选) / GPT-SoVITS(9880) / TTS适配器(9881) / AIRI(桌面宠物)
 *
 * 运行模式：
 *   双击 furina.exe            图形界面（默认）
 *   furina.exe --console       控制台模式：完成初始化后按 Q 退出
 *   furina.exe --exit-after=N  控制台模式：N 秒后自动走完回收退出（冒烟测试）
 *
 * 行为要点：
 *   - 每个组件先 HTTP 探活，活着就跳过，死了才拉起（幂等，可反复执行）；
 *   - NewAPI 为可选组件：NewApiExe 留空则整个网关跳过（启动/探活/备份/回收都不做），
 *     适配器与 AIRI 直接对接用户自己的 LLM/TTS 端点即可；
 *   - 看门狗每 ProbeIntervalSec 秒探活一次，连续失败 FailRestartThreshold 次
 *     自动重拉对应组件（SoVITS 模型加载期有 TtsWarmupGraceSec 宽限，不误判）；
 *   - 配置 NewAPI 时，每次启动前把 one-api.db 备份到 BackupDir（保留最近 10 份）；
 *   - SessionSecret 从 furina.ini 读取；为空则自动生成随机密钥并写回 ini
 *     （密钥不落进源码，仓库可安全公开）；
 *   - AIRI 已在运行时改为"接管监护"：不重复开、退出时也不杀；
 *   - 退出（GUI 停止 / 控制台按 Q / 关窗 / AIRI 关闭）都会触发回收：
 *     按端口关停本地服务。
 *
 * 配置：同目录 furina.ini（UTF-8），缺省时使用下方编译默认值（默认值不含本机路径）。
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public class Furina
{
    public class Cfg
    {
        // 路径默认值刻意留空/相对：开源使用者通过 GUI 浏览选择自己的安装位置，
        // 本机路径一律写在 furina.ini 里（furina.ini 不入库）。
        public string NewApiExe = "";
        public string NewApiDir = "";
        public string NewApiProbe = "http://127.0.0.1:3000/api/status";
        public string SessionSecret = "";
        public string AiriExe = "";
        public string TtsBat = @"..\启动芙宁娜语音服务.bat";
        public string SoVitsProbe = "http://127.0.0.1:9880/";
        public string AdapterProbe = "http://127.0.0.1:9881/health";
        public string OneApiDb = "";
        public string LogDir = "logs";
        public string BackupDir = @"configs\newapi";
        public int ProbeIntervalSec = 15;
        public int FailRestartThreshold = 3;
        public int TtsWarmupGraceSec = 180;
        public bool AutoExitWithAiri = true;
        public bool KeepServicesOnExit = false;
    }

    public static Cfg cfg = new Cfg();
    public static string baseDir;
    public static string logFile;
    static Process airiProc;
    static bool airiAdopted;
    static DateTime ttsLastStart = DateTime.MinValue;
    static bool tornDown = true;   // 开始时视为"已回收"，RunAll 前由 ResetState 复位
    static volatile bool closing = false;
    static volatile bool stopRequested = false;

    // GUI 日志订阅
    public static event Action<string> OnLog;

    // ---- 控制台关闭事件（关窗 / Ctrl+C / 系统注销）----
    delegate bool CtrlHandler(uint ctrlType);
    [DllImport("Kernel32.dll")]
    static extern bool SetConsoleCtrlHandler(CtrlHandler handler, bool add);
    static CtrlHandler ctrlHandler;

    static bool OnCtrl(uint ctrlType)
    {
        // 0=CTRL_C_EVENT 2=CTRL_CLOSE_EVENT 6=CTRL_SHUTDOWN_EVENT
        if (ctrlType == 0 || ctrlType == 2 || ctrlType == 6)
        {
            closing = true;
            Teardown();
        }
        return true;
    }

    // ---------------------------------------------------------------
    // 基础工具
    // ---------------------------------------------------------------

    public static bool NewApiEnabled
    {
        get { return !string.IsNullOrEmpty(cfg.NewApiExe); }
    }

    public static void Log(string msg)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg;
        try { Console.WriteLine(line); } catch { }
        try { File.AppendAllText(logFile, line + "\r\n", new UTF8Encoding(false)); } catch { }
        Action<string> h = OnLog;
        if (h != null) { try { h(line); } catch { } }
    }

    public static string ResolvePath(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        if (Path.IsPathRooted(p)) return p;
        return Path.GetFullPath(Path.Combine(baseDir, p));
    }

    public static void InitPaths()
    {
        baseDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        Directory.CreateDirectory(Path.Combine(baseDir, "logs"));
        logFile = Path.Combine(baseDir, "logs", "furina.log");
    }

    public static void LoadIni()
    {
        string iniPath = Path.Combine(baseDir, "furina.ini");
        if (!File.Exists(iniPath)) { Log("未找到 furina.ini，使用默认配置"); FinalizePaths(); return; }
        string[] lines = File.ReadAllText(iniPath, Encoding.UTF8).Split('\n');
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
            if (line.StartsWith("[")) continue;
            int idx = line.IndexOf('=');
            if (idx < 0) continue;
            string key = line.Substring(0, idx).Trim().ToLowerInvariant();
            string val = line.Substring(idx + 1).Trim();
            Apply(key, val);
        }
        FinalizePaths();
    }

    // 派生路径：OneApiDb 未填 → NewApiDir\one-api.db
    static void FinalizePaths()
    {
        cfg.LogDir = ResolvePath(cfg.LogDir);
        cfg.BackupDir = ResolvePath(cfg.BackupDir);
        cfg.NewApiDir = ResolvePath(cfg.NewApiDir);
        if (string.IsNullOrEmpty(cfg.OneApiDb) && NewApiEnabled)
        {
            cfg.OneApiDb = Path.Combine(cfg.NewApiDir, "one-api.db");
        }
        else
        {
            cfg.OneApiDb = ResolvePath(cfg.OneApiDb);
        }
    }

    public static void SaveIni()
    {
        string iniPath = Path.Combine(baseDir, "furina.ini");
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("# 芙宁娜整合启动器配置（与 furina.exe 同目录）；改完重启 furina.exe 生效");
        sb.AppendLine("# 相对路径基于本文件所在目录（Furina\\）；NewApiExe 留空 = 不使用网关");
        sb.AppendLine("[paths]");
        sb.AppendLine("NewApiExe=" + cfg.NewApiExe);
        sb.AppendLine("NewApiDir=" + cfg.NewApiDir);
        sb.AppendLine("NewApiProbe=" + cfg.NewApiProbe);
        sb.AppendLine("SessionSecret=" + cfg.SessionSecret);
        sb.AppendLine("AiriExe=" + cfg.AiriExe);
        sb.AppendLine("TtsBat=" + cfg.TtsBat);
        sb.AppendLine("SoVitsProbe=" + cfg.SoVitsProbe);
        sb.AppendLine("AdapterProbe=" + cfg.AdapterProbe);
        sb.AppendLine("OneApiDb=" + cfg.OneApiDb);
        sb.AppendLine();
        sb.AppendLine("[dirs]");
        sb.AppendLine("LogDir=logs");
        sb.AppendLine("BackupDir=configs\\newapi");
        sb.AppendLine();
        sb.AppendLine("[watchdog]");
        sb.AppendLine("ProbeIntervalSec=" + cfg.ProbeIntervalSec);
        sb.AppendLine("FailRestartThreshold=" + cfg.FailRestartThreshold);
        sb.AppendLine("TtsWarmupGraceSec=" + cfg.TtsWarmupGraceSec);
        sb.AppendLine("AutoExitWithAiri=" + cfg.AutoExitWithAiri);
        sb.AppendLine("KeepServicesOnExit=" + cfg.KeepServicesOnExit);
        File.WriteAllText(iniPath, sb.ToString(), new UTF8Encoding(false));
    }

    static void Apply(string key, string val)
    {
        switch (key)
        {
            case "newapiexe": cfg.NewApiExe = val; break;
            case "newapidir": cfg.NewApiDir = val; break;
            case "newapiprobe": cfg.NewApiProbe = val; break;
            case "sessionsecret": cfg.SessionSecret = val; break;
            case "airiexe": cfg.AiriExe = val; break;
            case "ttsbat": cfg.TtsBat = val; break;
            case "sovitsprobe": cfg.SoVitsProbe = val; break;
            case "adapterprobe": cfg.AdapterProbe = val; break;
            case "oneapidb": cfg.OneApiDb = val; break;
            case "logdir": cfg.LogDir = val; break;
            case "backupdir": cfg.BackupDir = val; break;
            case "probeintervalsec": int pi; if (int.TryParse(val, out pi) && pi > 0) cfg.ProbeIntervalSec = pi; break;
            case "failrestartthreshold": int fr; if (int.TryParse(val, out fr) && fr > 0) cfg.FailRestartThreshold = fr; break;
            case "ttswarmupgracesec": int wg; if (int.TryParse(val, out wg) && wg > 0) cfg.TtsWarmupGraceSec = wg; break;
            case "autoexitwithairi": bool ax; if (bool.TryParse(val, out ax)) cfg.AutoExitWithAiri = ax; break;
            case "keepservicesonexit": bool ks; if (bool.TryParse(val, out ks)) cfg.KeepServicesOnExit = ks; break;
        }
    }

    public static bool Probe(string url)
    {
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 5000;
            req.ReadWriteTimeout = 5000;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                return true;
            }
        }
        catch (WebException we)
        {
            // 收到了 HTTP 响应（哪怕 404/500）就说明服务活着
            return we.Response != null;
        }
        catch
        {
            return false;
        }
    }

    static bool WaitForProbe(string url, int timeoutSec, string label)
    {
        DateTime deadline = DateTime.Now.AddSeconds(timeoutSec);
        while (DateTime.Now < deadline)
        {
            if (Probe(url)) { Log(label + " 就绪"); return true; }
            Thread.Sleep(2000);
        }
        Log(label + " 在 " + timeoutSec + " 秒内未就绪（看门狗会继续重试）");
        return false;
    }

    // ---------------------------------------------------------------
    // 各组件拉起
    // ---------------------------------------------------------------

    static void StartNewApi()
    {
        if (!NewApiEnabled) return;
        if (Probe(cfg.NewApiProbe)) { Log("NewAPI 已在运行，跳过"); return; }
        try
        {
            if (string.IsNullOrEmpty(cfg.SessionSecret))
            {
                // 自动生成随机会话密钥并写回 ini——密钥不落进源码
                StringBuilder sb = new StringBuilder();
                Random rnd = new Random();
                for (int i = 0; i < 64; i++) sb.Append("0123456789abcdef"[rnd.Next(16)]);
                cfg.SessionSecret = sb.ToString();
                SaveIni();
                Log("已生成随机 SessionSecret 并写入 furina.ini");
            }
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = cfg.NewApiExe;
            psi.WorkingDirectory = cfg.NewApiDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            // PORT/TZ/GIN_MODE/STREAMING_TIMEOUT 与原 启动网关.bat 保持一致
            psi.EnvironmentVariables["PORT"] = "3000";
            psi.EnvironmentVariables["TZ"] = "Asia/Shanghai";
            psi.EnvironmentVariables["GIN_MODE"] = "release";
            psi.EnvironmentVariables["SESSION_SECRET"] = cfg.SessionSecret;
            psi.EnvironmentVariables["STREAMING_TIMEOUT"] = "600";
            Process p = Process.Start(psi);
            Log("NewAPI 已拉起 PID=" + p.Id);
        }
        catch (Exception e)
        {
            Log("NewAPI 启动失败: " + e.Message);
        }
    }

    static void StartTts()
    {
        try
        {
            string bat = ResolvePath(cfg.TtsBat);
            if (!File.Exists(bat))
            {
                Log("TTS 启动脚本不存在: " + bat + "（请在界面中配置正确的启动脚本）");
                return;
            }
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = bat;
            psi.WorkingDirectory = Path.GetDirectoryName(bat);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
            ttsLastStart = DateTime.Now;
            Log("已执行 TTS 启动脚本（内部探活并拉起 SoVITS + 适配器）");
        }
        catch (Exception e)
        {
            Log("TTS 服务启动失败: " + e.Message);
        }
    }

    static void EnsureAiri()
    {
        Process[] running = Process.GetProcessesByName("airi");
        if (running.Length > 0)
        {
            airiProc = running[0];
            airiAdopted = true;
            Log("AIRI 已在运行，转为接管监护（退出时不关闭它）");
            return;
        }
        if (string.IsNullOrEmpty(cfg.AiriExe))
        {
            Log("未配置 AIRI 路径，跳过（可在界面中浏览选择 airi.exe）");
            return;
        }
        try
        {
            airiProc = Process.Start(cfg.AiriExe);
            airiAdopted = false;
            Log("AIRI 已启动 PID=" + airiProc.Id);
        }
        catch (Exception e)
        {
            Log("AIRI 启动失败（可稍后手动打开）: " + e.Message);
        }
    }

    // ---------------------------------------------------------------
    // 备份与回收
    // ---------------------------------------------------------------

    static void BackupNewApiDb()
    {
        try
        {
            if (!NewApiEnabled) return;
            if (string.IsNullOrEmpty(cfg.OneApiDb) || !File.Exists(cfg.OneApiDb))
            {
                Log("未找到 one-api.db，跳过备份");
                return;
            }
            Directory.CreateDirectory(cfg.BackupDir);
            string dest = Path.Combine(cfg.BackupDir, "one-api.db." + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            File.Copy(cfg.OneApiDb, dest);
            Log("one-api.db 已备份 -> " + dest);
            List<string> files = new List<string>(Directory.GetFiles(cfg.BackupDir, "one-api.db.*"));
            files.Sort();
            files.Reverse();
            for (int i = 10; i < files.Count; i++)
            {
                try { File.Delete(files[i]); } catch { }
            }
        }
        catch (Exception e)
        {
            Log("one-api.db 备份失败: " + e.Message);
        }
    }

    static void KillTree(int pid)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("taskkill", "/F /T /PID " + pid);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process p = Process.Start(psi);
            p.WaitForExit(5000);
        }
        catch { }
    }

    static void KillByPort(int port)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(
                "cmd.exe", "/c netstat -ano | findstr \":" + port + "\" | findstr LISTENING");
            psi.RedirectStandardOutput = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            HashSet<int> pids = new HashSet<int>();
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                int pid;
                if (int.TryParse(parts[parts.Length - 1], out pid)) pids.Add(pid);
            }
            foreach (int pid in pids) KillTree(pid);
            if (pids.Count > 0) Log("端口 " + port + " 已回收 " + pids.Count + " 个进程");
        }
        catch { }
    }

    public static void Teardown()
    {
        if (tornDown) return;
        tornDown = true;
        Log("开始回收...");
        try
        {
            if (airiProc != null && !airiAdopted)
            {
                try { if (!airiProc.HasExited) KillTree(airiProc.Id); } catch { }
                Log("AIRI 已关闭");
            }
            if (!cfg.KeepServicesOnExit)
            {
                KillByPort(9881);
                KillByPort(9880);
                if (NewApiEnabled) KillByPort(3000);
                Log(NewApiEnabled ? "NewAPI 与语音服务已停止" : "语音服务已停止");
            }
            else
            {
                Log("KeepServicesOnExit=true，本地服务保持运行");
            }
        }
        catch (Exception e)
        {
            Log("回收出错: " + e.Message);
        }
    }

    // ---------------------------------------------------------------
    // 运行主流程与看门狗
    // ---------------------------------------------------------------

    public static void ResetState()
    {
        tornDown = false;
        closing = false;
        stopRequested = false;
        airiProc = null;
        airiAdopted = false;
        ttsLastStart = DateTime.MinValue;
        FinalizePaths();
    }

    // GUI 与控制台共用的初始化流程
    public static void RunAll()
    {
        Log("=== furina 启动器 | base=" + baseDir + " ===");
        if (NewApiEnabled)
        {
            BackupNewApiDb();
            StartNewApi();
            WaitForProbe(cfg.NewApiProbe, 60, "NewAPI");
        }
        else
        {
            Log("未配置 NewAPI（NewApiExe 为空），跳过网关");
        }
        StartTts();
        WaitForProbe(cfg.SoVitsProbe, cfg.TtsWarmupGraceSec, "SoVITS（模型加载约需 1 分钟）");
        WaitForProbe(cfg.AdapterProbe, 30, "TTS 适配器");
        EnsureAiri();
    }

    public static void RequestStop()
    {
        stopRequested = true;
    }

    // exitAfterSec>0 时到达秒数自动返回；GUI 模式传 0，靠 RequestStop
    public static void WatchdogLoop(int exitAfterSec)
    {
        int newapiFails = 0, sovitsFails = 0, adapterFails = 0;
        int ticks = 0, elapsed = 0;
        while (true)
        {
            if (closing || stopRequested) return;
            if (exitAfterSec > 0 && elapsed >= exitAfterSec)
            {
                Log("到达 --exit-after=" + exitAfterSec + "s，自动退出");
                return;
            }
            try
            {
                if (Console.KeyAvailable)
                {
                    ConsoleKeyInfo k = Console.ReadKey(true);
                    if (k.KeyChar == 'q' || k.KeyChar == 'Q')
                    {
                        Log("用户按 Q 退出");
                        return;
                    }
                }
            }
            catch { }

            if (cfg.AutoExitWithAiri && airiProc != null && !airiAdopted)
            {
                try
                {
                    if (airiProc.HasExited)
                    {
                        Log("AIRI 已退出");
                        return;
                    }
                }
                catch { }
            }

            ticks++;
            elapsed++;
            if (ticks >= cfg.ProbeIntervalSec)
            {
                ticks = 0;

                if (NewApiEnabled)
                {
                    if (Probe(cfg.NewApiProbe))
                    {
                        newapiFails = 0;
                    }
                    else
                    {
                        newapiFails++;
                        Log("NewAPI 探活失败 #" + newapiFails);
                        if (newapiFails >= cfg.FailRestartThreshold)
                        {
                            newapiFails = 0;
                            Log("尝试重拉 NewAPI");
                            StartNewApi();
                        }
                    }
                }

                // SoVITS 模型加载期不判死
                if ((DateTime.Now - ttsLastStart).TotalSeconds > cfg.TtsWarmupGraceSec)
                {
                    bool s = Probe(cfg.SoVitsProbe);
                    bool a = Probe(cfg.AdapterProbe);
                    if (s) sovitsFails = 0; else sovitsFails++;
                    if (a) adapterFails = 0; else adapterFails++;
                    if (sovitsFails >= cfg.FailRestartThreshold || adapterFails >= cfg.FailRestartThreshold)
                    {
                        Log("语音服务连续探活失败（SoVITS #" + sovitsFails + " / 适配器 #" + adapterFails + "），重新拉起");
                        sovitsFails = 0;
                        adapterFails = 0;
                        StartTts();
                    }
                }
            }
            Thread.Sleep(1000);
        }
    }

    // ---------------------------------------------------------------

    static int ParseExitAfter(string[] args)
    {
        foreach (string a in args)
        {
            if (a.StartsWith("--exit-after="))
            {
                int n;
                if (int.TryParse(a.Substring("--exit-after=".Length), out n) && n > 0) return n;
            }
        }
        return 0;
    }

    public static int ConsoleMain(string[] args)
    {
        try { Console.Title = "芙宁娜 · 整合启动器"; } catch { }
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

        InitPaths();
        LoadIni();
        ctrlHandler = new CtrlHandler(OnCtrl);
        SetConsoleCtrlHandler(ctrlHandler, true);

        ResetState();
        RunAll();

        int exitAfter = ParseExitAfter(args);
        Log("初始化完成。按 Q 退出" + (exitAfter > 0 ? ("；" + exitAfter + " 秒后自动退出(测试模式)") : "") + "。");

        WatchdogLoop(exitAfter);
        Teardown();
        return 0;
    }
}
