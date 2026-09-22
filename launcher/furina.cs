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
        public string TtsBat = @"..\start-voice.bat";
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
        // LLM 守卫代理（输出长度前置检查；AIRI 指向它而非直连网关）
        public bool LlmGuardEnabled = true;
        public int LlmGuardPort = 3001;
        public int LlmGuardMaxChars = 1000;
        public string LlmGuardUpstream = "";
        public string PythonExe = @"..\runtime\sovits-venv\Scripts\python.exe";
    }

    public static Cfg cfg = new Cfg();
    public static string baseDir;
    public static string logFile;
    static Process airiProc;
    static bool airiAdopted;
    // 组件"就绪过一次"标记：就绪前享有加载宽限，就绪后掉线立刻计数重拉
    static bool sovitsEverReady = false;
    static bool adapterEverReady = false;
    static volatile bool voiceWarmed = false;
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

    // 派生路径：OneApiDb 未填 → NewApiDir\one-api.db；LlmGuardUpstream 未填 → 由 NewApiProbe 推导
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
        if (string.IsNullOrEmpty(cfg.LlmGuardUpstream) && NewApiEnabled)
        {
            // http://127.0.0.1:3000/api/status → http://127.0.0.1:3000/v1
            string u = cfg.NewApiProbe;
            int cut = u.LastIndexOf("/api/", StringComparison.OrdinalIgnoreCase);
            cfg.LlmGuardUpstream = (cut > 0 ? u.Substring(0, cut) : u.TrimEnd('/')) + "/v1";
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
        sb.AppendLine();
        sb.AppendLine("[llmguard]");
        sb.AppendLine("# 输出长度守卫代理：AIRI 的 API 地址指向它（默认 127.0.0.1:3001/v1），");
        sb.AppendLine("# 超过 MaxChars 字的回复会在显示前被打回上游压缩重生成");
        sb.AppendLine("Enabled=" + cfg.LlmGuardEnabled);
        sb.AppendLine("Port=" + cfg.LlmGuardPort);
        sb.AppendLine("MaxChars=" + cfg.LlmGuardMaxChars);
        sb.AppendLine("Upstream=" + cfg.LlmGuardUpstream);
        sb.AppendLine();
        sb.AppendLine("[runtime]");
        sb.AppendLine("PythonExe=" + cfg.PythonExe);
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
            case "llmguardenabled": bool lg; if (bool.TryParse(val, out lg)) cfg.LlmGuardEnabled = lg; break;
            case "llmguardport": int lgp; if (int.TryParse(val, out lgp) && lgp > 0) cfg.LlmGuardPort = lgp; break;
            case "llmguardmaxchars": int lgm; if (int.TryParse(val, out lgm) && lgm > 0) cfg.LlmGuardMaxChars = lgm; break;
            case "llmguardupstream": cfg.LlmGuardUpstream = val; break;
            case "pythonexe": cfg.PythonExe = val; break;
        }
    }

    enum ProbeResult { Alive, Refused, Timeout }

    // 统一组件状态：Alive=有响应；Busy=超时但进程CPU有占用（在忙）；Dead=拒绝连接；
    // Stalled=超时且进程CPU精确为0（卡死）。只有 Dead/Stalled 才需要重拉。
    public enum ComponentState { Alive, Busy, Dead, Stalled }

    public static bool Probe(string url)
    {
        return ProbeDetailed(url) == ProbeResult.Alive;
    }

    // 统一探活入口：先问 HTTP；超时时再查端口监听进程的 CPU 精确值做最终判定
    public static ComponentState ProbeComponent(string url)
    {
        ProbeResult r = ProbeDetailed(url);
        if (r == ProbeResult.Alive) return ComponentState.Alive;
        if (r == ProbeResult.Refused) return ComponentState.Dead;
        int port = 0;
        try { port = new Uri(url).Port; } catch { }
        int pid = port > 0 ? FirstPidByPort(port) : 0;
        if (pid <= 0) return ComponentState.Dead;   // 超时且无监听者 = 死了
        return ProcessCpuIsExactlyZero(pid) ? ComponentState.Stalled : ComponentState.Busy;
    }

    static int FirstPidByPort(int port)
    {
        foreach (int pid in FindPidsByPort(port)) return pid;
        return 0;
    }

    // 在 300ms 测量窗口内进程未获得任何 CPU 时间片 = 精确的 0（卡死信号）
    static bool ProcessCpuIsExactlyZero(int pid)
    {
        try
        {
            Process p = Process.GetProcessById(pid);
            TimeSpan t1 = p.TotalProcessorTime;
            Thread.Sleep(300);
            p.Refresh();
            return p.TotalProcessorTime == t1;
        }
        catch { return false; }  // 进程刚好退出 → 本轮不算卡死，下轮探活自然判 Dead
    }

    // 区分"拒绝连接"（进程死了，可安全重拉）与"超时"（服务在忙合成，绝不能误杀）
    static ProbeResult ProbeDetailed(string url)
    {
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 5000;
            req.ReadWriteTimeout = 5000;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                return ProbeResult.Alive;
            }
        }
        catch (WebException we)
        {
            if (we.Response != null) return ProbeResult.Alive;
            if (we.Status == WebExceptionStatus.Timeout
                || we.Status == WebExceptionStatus.ReceiveFailure) return ProbeResult.Timeout;
            return ProbeResult.Refused;
        }
        catch
        {
            return ProbeResult.Refused;
        }
    }

    static DateTime lastBusyLog = DateTime.MinValue;
    static void LogBusyOnce(string who)
    {
        if ((DateTime.Now - lastBusyLog).TotalSeconds < 60) return;
        lastBusyLog = DateTime.Now;
        Log(who + " 探活超时（正在合成，判定为忙），暂不干预");
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
                Log("语音服务启动脚本不存在: " + bat + "（请在界面中配置正确的启动脚本）");
                return;
            }
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = bat;
            psi.WorkingDirectory = Path.GetDirectoryName(bat);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
            ttsLastStart = DateTime.Now;
            sovitsEverReady = false;
            adapterEverReady = false;
            voiceWarmed = false;   // 新进程，G2PW 会话需重新热机
            Log("已执行语音服务启动脚本（内部探活并拉起语音服务 + 适配器）");
            DeprioritizeNewPython();
        }
        catch (Exception e)
        {
            Log("语音服务启动失败: " + e.Message);
        }
    }

    // SoVITS 导入 torch/加载模型期间 CPU 与磁盘满载，会拖垮整个桌面的交互响应。
    // 这里对"本次新启动的 python 进程"临时降优先级，保护前端；组件就绪后由
    // 看门狗恢复为 Normal（见 WatchdogLoop）。
    static void DeprioritizeNewPython()
    {
        try
        {
            HashSet<int> before = PidsByImage("python");
            new Thread(delegate ()
            {
                try
                {
                    Thread.Sleep(4000);
                    foreach (int pid in PidsByImage("python"))
                    {
                        if (before.Contains(pid)) continue;
                        try { Process.GetProcessById(pid).PriorityClass = ProcessPriorityClass.BelowNormal; }
                        catch { }
                    }
                }
                catch { }
            }) { IsBackground = true }.Start();
        }
        catch { }
    }

    static HashSet<int> PidsByImage(string name)
    {
        HashSet<int> set = new HashSet<int>();
        try
        {
            foreach (Process p in Process.GetProcessesByName(name))
            {
                try { set.Add(p.Id); } catch { }
            }
        }
        catch { }
        return set;
    }

    // 按端口找监听进程并设置优先级（用于组件就绪后恢复 Normal）
    static void SetPriorityByPort(int port, ProcessPriorityClass cls)
    {
        try
        {
            foreach (int pid in FindPidsByPort(port))
            {
                try { Process.GetProcessById(pid).PriorityClass = cls; }
                catch { }
            }
        }
        catch { }
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

    // 掉线恢复后读取适配器日志，汇报近 10 分钟掉线窗口内的合成失败数量
    static void ReportOutageWindow()
    {
        try
        {
            string logPath = Path.Combine(
                Path.GetDirectoryName(ResolvePath(cfg.TtsBat)), @"voice\adapter.log");
            if (!File.Exists(logPath)) return;
            string[] lines = File.ReadAllLines(logPath, Encoding.UTF8);
            DateTime cutoff = DateTime.Now.AddMinutes(-10);
            int errors = 0;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (lines[i].Length < 20) continue;
                DateTime ts;
                if (!DateTime.TryParse(lines[i].Substring(0, 19), out ts)) continue;
                if (ts < cutoff) break;
                if (lines[i].IndexOf("ERROR", StringComparison.Ordinal) >= 0) errors++;
            }
            Log("掉线窗口（近 10 分钟）适配器共记录 " + errors + " 条合成失败；" +
                "服务恢复后适配器会原地重试，卡住的句子将从断点继续播放");
        }
        catch { }
    }

    // 每次启动清空运行期日志：问题现场只保留本次运行，有问题现场问 AI，不过夜
    static void TruncateRuntimeLogs()
    {
        try
        {
            string rootDir = Path.GetDirectoryName(ResolvePath(cfg.TtsBat));
            string voiceDir = Path.Combine(rootDir, "voice");
            foreach (string name in new string[] { "adapter.log", "tts_api.log" })
            {
                try
                {
                    string p = Path.Combine(voiceDir, name);
                    if (File.Exists(p)) File.WriteAllText(p, "");
                }
                catch { }
            }
            try
            {
                string g = Path.Combine(rootDir, "llm_guard.log");
                if (File.Exists(g)) File.WriteAllText(g, "");
            }
            catch { }
            Log("已清空 adapter.log / tts_api.log / llm_guard.log（只保留本次运行的问题现场）");
        }
        catch { }
    }

    // 语音合成自动热机：SoVITS 新进程首次合成约 19 秒（G2PW 建会话），
    // 就绪后立刻发一句预热请求建立会话，用户开口即是热机速度。
    static void WarmUpVoice()
    {
        if (voiceWarmed) return;
        voiceWarmed = true;
        new Thread(delegate ()
        {
            try
            {
                string baseUrl = cfg.AdapterProbe;
                int cut = baseUrl.LastIndexOf("/health", StringComparison.OrdinalIgnoreCase);
                if (cut > 0) baseUrl = baseUrl.Substring(0, cut);
                string payload = "{\"model\":\"gpt-sovits-tts\",\"input\":\"预热一下。\",\"voice\":\"furina\"}";
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(baseUrl + "/v1/audio/speech");
                req.Method = "POST";
                req.ContentType = "application/json; charset=utf-8";
                req.Timeout = 60000;
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (WebResponse resp = req.GetResponse())
                {
                    // 丢弃音频内容，目的只是建立 G2PW 会话
                }
                Log("语音合成已热机，开口即是热机速度");
            }
            catch (Exception e)
            {
                Log("热机请求失败（不影响使用，首句会稍慢）: " + e.Message);
            }
        }) { IsBackground = true }.Start();
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

    static HashSet<int> FindPidsByPort(int port)
    {
        HashSet<int> pids = new HashSet<int>();
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
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                int pid;
                if (int.TryParse(parts[parts.Length - 1], out pid)) pids.Add(pid);
            }
        }
        catch { }
        return pids;
    }

    static void KillByPort(int port)
    {
        HashSet<int> pids = FindPidsByPort(port);
        foreach (int pid in pids) KillTree(pid);
        if (pids.Count > 0) Log("端口 " + port + " 已回收 " + pids.Count + " 个进程");
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
                if (NewApiEnabled)
                {
                    if (cfg.LlmGuardEnabled) KillByPort(cfg.LlmGuardPort);
                    KillByPort(3000);
                }
                Log(NewApiEnabled ? "NewAPI、LLM 守卫与语音服务已停止" : "语音服务已停止");
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
        sovitsEverReady = false;
        adapterEverReady = false;
        ttsLastStart = DateTime.MinValue;
        FinalizePaths();
    }

    // LLM 守卫代理：输出长度前置检查（AIRI → 守卫 → 网关）
    static void StartLlmGuard()
    {
        if (!cfg.LlmGuardEnabled || !NewApiEnabled) return;
        string health = "http://127.0.0.1:" + cfg.LlmGuardPort + "/health";
        if (Probe(health)) return;
        try
        {
            string py = ResolvePath(cfg.PythonExe);
            string script = Path.Combine(Path.GetDirectoryName(ResolvePath(cfg.TtsBat)), "llm_guard.py");
            string adapterBase = cfg.AdapterProbe;
            int acut = adapterBase.LastIndexOf("/health", StringComparison.OrdinalIgnoreCase);
            if (acut > 0) adapterBase = adapterBase.Substring(0, acut);
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = py;
            psi.Arguments = string.Format(
                "\"{0}\" --port {1} --upstream \"{2}\" --max-chars {3} --adapter \"{4}\"",
                script, cfg.LlmGuardPort, cfg.LlmGuardUpstream, cfg.LlmGuardMaxChars, adapterBase);
            psi.WorkingDirectory = Path.GetDirectoryName(script);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process p = Process.Start(psi);
            Log("LLM 守卫已拉起 PID=" + p.Id + "（:300" + "1，上限 " + cfg.LlmGuardMaxChars + " 字）");
        }
        catch (Exception e)
        {
            Log("LLM 守卫启动失败: " + e.Message);
        }
    }

    // GUI 与控制台共用的初始化流程
    public static void RunAll()
    {
        Log("=== furina 启动器 | base=" + baseDir + " ===");
        TruncateRuntimeLogs();
        if (NewApiEnabled)
        {
            BackupNewApiDb();
            StartNewApi();
            WaitForProbe(cfg.NewApiProbe, 60, "NewAPI");
            StartLlmGuard();
            WaitForProbe("http://127.0.0.1:" + cfg.LlmGuardPort + "/health", 30, "LLM 守卫");
        }
        else
        {
            Log("未配置 NewAPI（NewApiExe 为空），跳过网关与 LLM 守卫");
        }
        StartTts();
        WaitForProbe(cfg.SoVitsProbe, cfg.TtsWarmupGraceSec, "语音服务（模型加载约需 1 分钟）");
        WaitForProbe(cfg.AdapterProbe, 30, "语音适配器");
        WarmUpVoice();
        EnsureAiri();
        Log("初始化完成。");
    }

    public static void RequestStop()
    {
        stopRequested = true;
    }

    // exitAfterSec>0 时到达秒数自动返回；GUI 模式传 0，靠 RequestStop
    public static void WatchdogLoop(int exitAfterSec)
    {
        int newapiFails = 0, sovitsFails = 0, adapterFails = 0, guardFails = 0;
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

            // 语音服务快速通道：每 5 秒探活，连续 2 次"拒绝连接"立即重拉（掉线判定 ≤10 秒）；
            // 探活"超时"视为正在合成（忙），不计失败——杜绝误杀正在工作的服务
            if (ticks % 5 == 0)
            {
                // 只有"组件曾就绪过"或"加载宽限已过"才累计失败次数
                bool graceOver = (DateTime.Now - ttsLastStart).TotalSeconds > cfg.TtsWarmupGraceSec;
                ComponentState cs = ProbeComponent(cfg.SoVitsProbe);
                ComponentState ca = ProbeComponent(cfg.AdapterProbe);

                if (cs == ComponentState.Alive || cs == ComponentState.Busy)
                {
                    if (!sovitsEverReady)
                    {
                        sovitsEverReady = true;
                        SetPriorityByPort(9880, ProcessPriorityClass.Normal);
                        Log("语音服务首次就绪，进程优先级恢复 Normal");
                        WarmUpVoice();
                    }
                    sovitsFails = 0;
                    if (cs == ComponentState.Busy) LogBusyOnce("语音服务");
                }
                else if (sovitsEverReady || graceOver)
                {
                    sovitsFails++;
                    if (cs == ComponentState.Stalled)
                        Log("语音服务探活超时且 CPU 精确为 0，判定为卡死");
                }

                if (ca == ComponentState.Alive || ca == ComponentState.Busy)
                {
                    if (!adapterEverReady)
                    {
                        adapterEverReady = true;
                        SetPriorityByPort(9881, ProcessPriorityClass.Normal);
                    }
                    adapterFails = 0;
                    if (ca == ComponentState.Busy) LogBusyOnce("语音适配器");
                }
                else if (adapterEverReady || graceOver)
                {
                    adapterFails++;
                    if (ca == ComponentState.Stalled)
                        Log("语音适配器探活超时且 CPU 精确为 0，判定为卡死");
                }

                if (sovitsFails >= 2 || adapterFails >= 2)
                {
                    Log("语音服务掉线（主服务连续拒绝 #" + sovitsFails + " / 适配器 #" + adapterFails + "），立即重拉");
                    sovitsFails = 0;
                    adapterFails = 0;
                    ReportOutageWindow();
                    StartTts();
                }
            }

            // NewAPI 慢速通道：保持原节奏
            if (ticks >= cfg.ProbeIntervalSec)
            {
                ticks = 0;

                if (NewApiEnabled)
                {
                    ComponentState cn = ProbeComponent(cfg.NewApiProbe);
                    if (cn == ComponentState.Alive || cn == ComponentState.Busy)
                    {
                        newapiFails = 0;
                    }
                    else
                    {
                        newapiFails++;
                        Log("NewAPI 探活失败 #" + newapiFails
                            + (cn == ComponentState.Stalled ? "（超时且 CPU 为 0，卡死）" : "（拒绝连接）"));
                        if (newapiFails >= cfg.FailRestartThreshold)
                        {
                            newapiFails = 0;
                            Log("尝试重拉 NewAPI");
                            StartNewApi();
                        }
                    }
                }

                // LLM 守卫：同节奏探活，掉了重拉（它只在本机，拒绝连接即真死）
                if (cfg.LlmGuardEnabled && NewApiEnabled)
                {
                    ComponentState cg = ProbeComponent("http://127.0.0.1:" + cfg.LlmGuardPort + "/health");
                    if (cg == ComponentState.Alive || cg == ComponentState.Busy)
                    {
                        guardFails = 0;
                    }
                    else
                    {
                        guardFails++;
                        if (guardFails >= cfg.FailRestartThreshold)
                        {
                            guardFails = 0;
                            Log("尝试重拉 LLM 守卫");
                            StartLlmGuard();
                        }
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
        Log("按 Q 退出" + (exitAfter > 0 ? ("；" + exitAfter + " 秒后自动退出(测试模式)") : "") + "。");

        WatchdogLoop(exitAfter);
        Teardown();
        return 0;
    }
}
