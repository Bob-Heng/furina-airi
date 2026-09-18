// -*- coding: utf-8 -*-
/*
 * furina_gui.cs - 芙宁娜整合启动器图形界面
 *
 * 启动流程：SplashForm（进度条 + "稍等片刻，你的蓝莓小蛋糕正在路上……"逐字弹跳动画）
 *           后台完成配置载入后再进入主界面。
 * 主界面：组件路径（逐项即时校验 ✓/✗，NewAPI 三项依赖网关程序校验通过才可填写）、
 *         行为（两个勾选项互斥）、运行（状态灯 + 启停）、实时日志。
 * 状态探活在后台线程执行，避免 HTTP 超时阻塞 UI。
 *
 * 不带参数双击 = GUI；带参数（如 --console / --exit-after=N）走控制台模式。
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args != null && args.Length > 0)
            return Furina.ConsoleMain(args);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
        {
            MessageBox.Show(e.Exception.ToString(), "furina 未处理异常");
        };
        Furina.InitPaths();

        // 启动画面：动画播放与配置载入并行，两者都完成才进主界面
        SplashForm splash = new SplashForm();
        splash.Show();
        ManualResetEvent initDone = new ManualResetEvent(false);
        new Thread(delegate ()
        {
            try { Furina.LoadIni(); }
            catch { }
            Thread.Sleep(2300); // 至少放完一轮动画
            initDone.Set();
        }) { IsBackground = true }.Start();
        while (!initDone.WaitOne(0)) Application.DoEvents();
        splash.Close();

        Application.Run(new MainForm());
        return 0;
    }
}

// ---------------------------------------------------------------
// 启动画面
// ---------------------------------------------------------------

class SplashForm : Form
{
    const string MSG = "稍等片刻，你的蓝莓小蛋糕正在路上……";
    readonly Label[] chars = new Label[MSG.Length];
    readonly int baseY = 66;
    int tick;
    readonly System.Windows.Forms.Timer tmr;

    public SplashForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(520, 230);
        BackColor = Color.FromArgb(255, 246, 250);
        DoubleBuffered = true;

        Label caption = new Label();
        caption.Text = "furina 正在初始化";
        caption.Font = new Font("Microsoft YaHei UI", 9);
        caption.ForeColor = Color.Gray;
        caption.AutoSize = true;
        caption.Location = new Point((Width - caption.PreferredWidth) / 2, 24);
        Controls.Add(caption);

        using (Graphics g = CreateGraphics())
        {
            // 按实际字宽居中排布每个字
            Font f = new Font("Microsoft YaHei UI", 17, FontStyle.Bold);
            int total = 0;
            int[] widths = new int[MSG.Length];
            for (int i = 0; i < MSG.Length; i++)
            {
                widths[i] = TextRenderer.MeasureText(g, MSG[i].ToString(), f).Width - 6;
                total += widths[i];
            }
            int x = (Width - total) / 2;
            for (int i = 0; i < MSG.Length; i++)
            {
                chars[i] = new Label();
                chars[i].Text = MSG[i].ToString();
                chars[i].Font = f;
                chars[i].ForeColor = Color.FromArgb(214, 84, 140);
                chars[i].AutoSize = false;
                chars[i].Size = new Size(widths[i] + 8, 34);
                chars[i].TextAlign = ContentAlignment.MiddleCenter;
                chars[i].Location = new Point(x, baseY - 60);
                chars[i].Visible = false;
                Controls.Add(chars[i]);
                x += widths[i];
            }
        }

        ProgressBar bar = new ProgressBar();
        bar.Style = ProgressBarStyle.Marquee;
        bar.Size = new Size(380, 10);
        bar.Location = new Point((Width - 380) / 2, 150);
        Controls.Add(bar);

        tmr = new System.Windows.Forms.Timer();
        tmr.Interval = 30;
        tmr.Tick += delegate { Animate(); };
        tmr.Start();
    }

    void Animate()
    {
        tick++;
        bool allLanded = true;
        for (int i = 0; i < chars.Length; i++)
        {
            int start = i * 7;                 // 逐字延迟登场
            double p = (tick - start) / 45.0;  // 每个字 45 tick 完成
            if (p < 0) { chars[i].Visible = false; allLanded = false; continue; }
            chars[i].Visible = true;
            if (p >= 1)
            {
                chars[i].Top = baseY;
                continue;
            }
            allLanded = false;
            chars[i].Top = baseY - BounceOffset(p);
        }
        // 全部落稳后停 30 tick 再循环
        if (allLanded && tick > chars.Length * 7 + 45 + 30) tick = 0;
    }

    // 下落 + 衰减弹跳：p∈[0,1]
    static int BounceOffset(double p)
    {
        if (p < 0.4) return (int)((1 - p / 0.4) * 60);   // 从上方 60px 落下
        double q = (p - 0.4) / 0.6;
        return (int)(Math.Abs(Math.Sin(q * Math.PI * 2.5)) * (1 - q) * 14);
    }
}

// ---------------------------------------------------------------
// 主界面
// ---------------------------------------------------------------

class MainForm : Form
{
    TextBox txtAiri, txtTtsBat, txtNewApiExe, txtNewApiDir, txtNewApiProbe,
            txtSoVitsProbe, txtAdapterProbe, txtSession;
    Label markAiri, markTts, markNewApi, markNewApiDir, markNewApiProbe, markSoVits, markAdapter;
    CheckBox chkAutoExit, chkKeep;
    NumericUpDown numInterval;
    Button btnStart, btnStop;
    Label lblStNewApi, lblStSoVits, lblStAdapter, lblStAiri;
    TextBox txtLog;
    System.Windows.Forms.Timer statusTimer;
    System.Windows.Forms.Timer validateTimer;
    ToolTip toolTip = new ToolTip();
    volatile bool probing;

    Thread runThread;

    public MainForm()
    {
        Text = "芙宁娜 · 整合启动器";
        Width = 880;
        Height = 700;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(780, 620);

        TableLayoutPanel root = new TableLayoutPanel();
        root.Dock = DockStyle.Top;
        root.AutoSize = true;
        root.ColumnCount = 1;
        root.Padding = new Padding(8);
        root.Controls.Add(BuildPathsGroup(), 0, 0);
        root.Controls.Add(BuildBehaviorGroup(), 0, 1);
        root.Controls.Add(BuildRunGroup(), 0, 2);
        Controls.Add(root);

        txtLog = new TextBox();
        txtLog.Dock = DockStyle.Bottom;
        txtLog.Height = 165;
        txtLog.Multiline = true;
        txtLog.ReadOnly = true;
        txtLog.ScrollBars = ScrollBars.Vertical;
        txtLog.BackColor = Color.FromArgb(30, 30, 30);
        txtLog.ForeColor = Color.FromArgb(200, 200, 200);
        txtLog.Font = new Font("Consolas", 9);
        Controls.Add(txtLog);
        root.BringToFront();

        // 校验防抖：输入停顿 400ms 后统一验证
        validateTimer = new System.Windows.Forms.Timer();
        validateTimer.Interval = 400;
        validateTimer.Tick += delegate { validateTimer.Stop(); ValidateAll(); };

        statusTimer = new System.Windows.Forms.Timer();
        statusTimer.Interval = 2000;
        statusTimer.Tick += delegate { RefreshStatusAsync(); };
        statusTimer.Start();

        HookTextChanged();
        FieldsFromCfg();
        ValidateAll();

        Furina.OnLog += OnCoreLog;
        AppendLog("配置已载入。浏览选择你的组件路径后点「启动」。");
    }

    // ---------------------------------------------------------------
    // 界面构建
    // ---------------------------------------------------------------

    GroupBox BuildPathsGroup()
    {
        GroupBox grp = new GroupBox();
        grp.Text = "组件路径（除 AIRI 外均有默认值；NewAPI 留空 = 不使用网关）";
        grp.AutoSize = true;
        grp.Dock = DockStyle.Top;

        TableLayoutPanel grid = new TableLayoutPanel();
        grid.Dock = DockStyle.Top;
        grid.AutoSize = true;
        grid.ColumnCount = 4;
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));

        int row = 0;
        txtAiri = AddRow(grid, row, "AIRI 程序", "浏览", delegate
        {
            string p = PickFile("选择 AIRI 主程序 (airi.exe)", "可执行文件|*.exe");
            if (p != null) txtAiri.Text = p;
        }, out markAiri);
        row++;
        txtTtsBat = AddRow(grid, row, "TTS 启动脚本", "浏览", delegate
        {
            string p = PickFile("选择 TTS 启动脚本（bat）", "批处理|*.bat;*.cmd");
            if (p != null) txtTtsBat.Text = p;
        }, out markTts);
        row++;
        txtNewApiExe = AddRow(grid, row, "NewAPI 程序（可空）", "浏览", delegate
        {
            string p = PickFile("选择 NewAPI 主程序（不需要网关可跳过）", "可执行文件|*.exe");
            if (p != null) txtNewApiExe.Text = p;
        }, out markNewApi);
        row++;
        txtNewApiDir = AddRow(grid, row, "NewAPI 数据目录", "浏览", delegate
        {
            string p = PickFolder("选择 NewAPI 数据目录（含 one-api.db）");
            if (p != null) txtNewApiDir.Text = p;
        }, out markNewApiDir);
        row++;
        txtNewApiProbe = AddRow(grid, row, "NewAPI 探活地址", null, null, out markNewApiProbe);
        row++;
        txtSoVitsProbe = AddRow(grid, row, "SoVITS 探活地址", null, null, out markSoVits);
        row++;
        txtAdapterProbe = AddRow(grid, row, "适配器探活地址", null, null, out markAdapter);
        row++;
        Label markSession;
        txtSession = AddRow(grid, row, "网关密钥（空=自动生成）", null, null, out markSession);
        row++;

        grp.Controls.Add(grid);
        return grp;
    }

    GroupBox BuildBehaviorGroup()
    {
        GroupBox grp = new GroupBox();
        grp.Text = "行为（两个选项互斥）";
        grp.AutoSize = true;
        grp.Dock = DockStyle.Top;
        FlowLayoutPanel flow = new FlowLayoutPanel();
        flow.Dock = DockStyle.Top;
        flow.AutoSize = true;
        flow.WrapContents = false;

        chkAutoExit = new CheckBox();
        chkAutoExit.Text = "AIRI 关闭时自动退出并回收";
        chkAutoExit.AutoSize = true;
        chkKeep = new CheckBox();
        chkKeep.Text = "退出时保留服务运行";
        chkKeep.AutoSize = true;
        // 互斥：勾选其一即取消另一个
        chkAutoExit.CheckedChanged += delegate
        {
            if (chkAutoExit.Checked && chkKeep.Checked) chkKeep.Checked = false;
        };
        chkKeep.CheckedChanged += delegate
        {
            if (chkKeep.Checked && chkAutoExit.Checked) chkAutoExit.Checked = false;
        };

        Label lblInt = new Label();
        lblInt.Text = "探活周期(秒)";
        lblInt.AutoSize = true;
        numInterval = new NumericUpDown();
        numInterval.Minimum = 5;
        numInterval.Maximum = 300;
        numInterval.Value = 15;

        flow.Controls.Add(chkAutoExit);
        flow.Controls.Add(chkKeep);
        flow.Controls.Add(lblInt);
        flow.Controls.Add(numInterval);
        grp.Controls.Add(flow);
        return grp;
    }

    GroupBox BuildRunGroup()
    {
        GroupBox grp = new GroupBox();
        grp.Text = "运行";
        grp.AutoSize = true;
        grp.Dock = DockStyle.Top;
        FlowLayoutPanel flow = new FlowLayoutPanel();
        flow.Dock = DockStyle.Top;
        flow.AutoSize = true;
        flow.WrapContents = false;

        Button btnSave = new Button();
        btnSave.Text = "保存配置";
        btnSave.Click += delegate { FieldsToCfg(); Furina.SaveIni(); Furina.Log("配置已保存到 furina.ini"); };
        btnStart = new Button();
        btnStart.Text = "▶ 启动";
        btnStart.BackColor = Color.FromArgb(198, 239, 206);
        btnStart.Click += delegate { StartRun(); };
        btnStop = new Button();
        btnStop.Text = "■ 停止（回收服务）";
        btnStop.Enabled = false;
        btnStop.BackColor = Color.FromArgb(255, 199, 206);
        btnStop.Click += delegate { Furina.RequestStop(); };

        flow.Controls.Add(btnSave);
        flow.Controls.Add(btnStart);
        flow.Controls.Add(btnStop);
        flow.Controls.Add(MakeStatusLabel("NewAPI", out lblStNewApi));
        flow.Controls.Add(MakeStatusLabel("SoVITS", out lblStSoVits));
        flow.Controls.Add(MakeStatusLabel("适配器", out lblStAdapter));
        flow.Controls.Add(MakeStatusLabel("AIRI", out lblStAiri));
        grp.Controls.Add(flow);
        return grp;
    }

    Control MakeStatusLabel(string name, out Label lbl)
    {
        lbl = new Label();
        lbl.Text = name + ": --";
        lbl.AutoSize = true;
        lbl.Padding = new Padding(6, 3, 6, 3);
        lbl.Margin = new Padding(8, 6, 0, 3);
        lbl.BackColor = Color.LightGray;
        return lbl;
    }

    TextBox AddRow(TableLayoutPanel grid, int row, string label, string btnText, EventHandler onBrowse, out Label mark)
    {
        grid.RowCount = row + 1;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Label lbl = new Label();
        lbl.Text = label;
        lbl.AutoSize = true;
        lbl.Anchor = AnchorStyles.Left;
        lbl.Padding = new Padding(4, 6, 0, 0);
        grid.Controls.Add(lbl, 0, row);

        TextBox tb = new TextBox();
        tb.Dock = DockStyle.Fill;
        grid.Controls.Add(tb, 1, row);

        if (btnText != null)
        {
            Button btn = new Button();
            btn.Text = btnText;
            btn.Dock = DockStyle.Fill;
            btn.Click += onBrowse;
            grid.Controls.Add(btn, 2, row);
        }
        else
        {
            Label filler = new Label();
            grid.Controls.Add(filler, 2, row);
        }

        mark = new Label();
        mark.Text = "";
        mark.AutoSize = false;
        mark.Size = new Size(24, 22);
        mark.TextAlign = ContentAlignment.MiddleCenter;
        mark.Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);
        grid.Controls.Add(mark, 3, row);
        return tb;
    }

    string PickFile(string title, string filter)
    {
        OpenFileDialog dlg = new OpenFileDialog();
        dlg.Title = title;
        dlg.Filter = filter + "|所有文件|*.*";
        if (dlg.ShowDialog(this) == DialogResult.OK) return dlg.FileName;
        return null;
    }

    string PickFolder(string title)
    {
        FolderBrowserDialog dlg = new FolderBrowserDialog();
        dlg.Description = title;
        if (dlg.ShowDialog(this) == DialogResult.OK) return dlg.SelectedPath;
        return null;
    }

    // ---------------------------------------------------------------
    // 校验
    // ---------------------------------------------------------------

    void HookTextChanged()
    {
        foreach (TextBox tb in new TextBox[] {
            txtAiri, txtTtsBat, txtNewApiExe, txtNewApiDir,
            txtNewApiProbe, txtSoVitsProbe, txtAdapterProbe, txtSession })
        {
            tb.TextChanged += delegate { validateTimer.Stop(); validateTimer.Start(); };
        }
    }

    void SetMark(Label mark, bool? ok, string reason)
    {
        if (mark == null) return;
        if (ok == null) { mark.Text = ""; toolTip.SetToolTip(mark, ""); return; }
        mark.Text = ok.Value ? "✓" : "✗";
        mark.ForeColor = ok.Value ? Color.FromArgb(0, 150, 60) : Color.FromArgb(210, 40, 40);
        toolTip.SetToolTip(mark, reason);
    }

    void ValidateAll()
    {
        // AIRI：存在且文件名必须是 airi.exe
        string airi = txtAiri.Text.Trim();
        if (airi.Length == 0) SetMark(markAiri, null, "");
        else if (!File.Exists(airi)) SetMark(markAiri, false, "文件不存在：" + airi);
        else if (!string.Equals(Path.GetFileName(airi), "airi.exe", StringComparison.OrdinalIgnoreCase))
            SetMark(markAiri, false, "文件存在，但文件名不是 airi.exe，请确认选对了 AIRI 主程序");
        else SetMark(markAiri, true, "AIRI 主程序（静态校验通过；是否运行中见下方状态灯）");

        // TTS 启动脚本：存在 + 可执行扩展名
        string tts = txtTtsBat.Text.Trim();
        string[] runExts = { ".bat", ".cmd", ".exe", ".ps1", ".vbs" };
        if (tts.Length == 0) SetMark(markTts, null, "");
        else if (!File.Exists(Furina.ResolvePath(tts))) SetMark(markTts, false, "文件不存在：" + Furina.ResolvePath(tts));
        else if (Array.IndexOf(runExts, Path.GetExtension(tts).ToLowerInvariant()) < 0)
            SetMark(markTts, false, "不是可执行的启动脚本（.bat/.cmd/.exe/.ps1/.vbs）");
        else SetMark(markTts, true, "脚本存在（静态校验；能否正常拉起以启动后的状态灯为准）");

        // NewAPI 程序：可空；通过校验才解锁网关相关三项
        string napi = txtNewApiExe.Text.Trim();
        bool napiOk = false;
        if (napi.Length == 0) SetMark(markNewApi, null, "未配置 = 不使用网关");
        else if (!File.Exists(napi)) SetMark(markNewApi, false, "文件不存在：" + napi);
        else if (!string.Equals(Path.GetExtension(napi), ".exe", StringComparison.OrdinalIgnoreCase))
            SetMark(markNewApi, false, "NewAPI 单文件版应为 .exe");
        else { SetMark(markNewApi, true, "NewAPI 主程序"); napiOk = true; }

        bool gatewayOn = napiOk; // 已验证才允许填写
        txtNewApiDir.Enabled = gatewayOn;
        txtNewApiProbe.Enabled = gatewayOn;
        txtSession.Enabled = gatewayOn;
        if (!gatewayOn)
        {
            SetMark(markNewApiDir, null, "");
            SetMark(markNewApiProbe, null, "");
        }
        else
        {
            string dir = txtNewApiDir.Text.Trim();
            SetMark(markNewApiDir,
                dir.Length == 0 ? (bool?)null : Directory.Exists(dir),
                dir.Length == 0 ? "" : (Directory.Exists(dir) ? "目录存在" : "目录不存在：" + dir));
            SetMark(markNewApiProbe, ValidHttpUrl(txtNewApiProbe.Text.Trim()) ? (bool?)true : (bool?)false,
                "形如 http://127.0.0.1:3000/api/status");
        }

        SetMark(markSoVits, ValidHttpUrl(txtSoVitsProbe.Text.Trim()) ? (bool?)true : (bool?)false,
            "形如 http://127.0.0.1:9880/");
        SetMark(markAdapter, ValidHttpUrl(txtAdapterProbe.Text.Trim()) ? (bool?)true : (bool?)false,
            "形如 http://127.0.0.1:9881/health");
    }

    static bool ValidHttpUrl(string s)
    {
        if (s.Length == 0) return false;
        Uri u;
        return Uri.TryCreate(s, UriKind.Absolute, out u)
            && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
    }

    // ---------------------------------------------------------------
    // 配置与运行
    // ---------------------------------------------------------------

    void FieldsFromCfg()
    {
        Furina.Cfg c = Furina.cfg;
        txtAiri.Text = c.AiriExe;
        txtTtsBat.Text = c.TtsBat;
        txtNewApiExe.Text = c.NewApiExe;
        txtNewApiDir.Text = c.NewApiDir;
        txtNewApiProbe.Text = c.NewApiProbe;
        txtSoVitsProbe.Text = c.SoVitsProbe;
        txtAdapterProbe.Text = c.AdapterProbe;
        txtSession.Text = c.SessionSecret;
        chkAutoExit.Checked = c.AutoExitWithAiri;
        chkKeep.Checked = c.KeepServicesOnExit;
        numInterval.Value = Math.Max(numInterval.Minimum, Math.Min(numInterval.Maximum, c.ProbeIntervalSec));
    }

    void FieldsToCfg()
    {
        Furina.Cfg c = Furina.cfg;
        c.AiriExe = txtAiri.Text.Trim();
        c.TtsBat = txtTtsBat.Text.Trim();
        c.NewApiExe = txtNewApiExe.Text.Trim();
        c.NewApiDir = txtNewApiDir.Text.Trim();
        c.NewApiProbe = txtNewApiProbe.Text.Trim();
        c.SoVitsProbe = txtSoVitsProbe.Text.Trim();
        c.AdapterProbe = txtAdapterProbe.Text.Trim();
        c.SessionSecret = txtSession.Text.Trim();
        c.AutoExitWithAiri = chkAutoExit.Checked;
        c.KeepServicesOnExit = chkKeep.Checked;
        c.ProbeIntervalSec = (int)numInterval.Value;
    }

    void StartRun()
    {
        FieldsToCfg();
        Furina.SaveIni();
        Furina.ResetState();
        btnStart.Enabled = false;
        btnStop.Enabled = true;
        runThread = new Thread(RunWorker);
        runThread.IsBackground = true;
        runThread.Start();
    }

    void RunWorker()
    {
        try
        {
            Furina.RunAll();
            Furina.WatchdogLoop(0);
        }
        catch (Exception e)
        {
            Furina.Log("运行异常: " + e);
        }
        finally
        {
            Furina.Teardown();
            Ui(delegate
            {
                btnStart.Enabled = true;
                btnStop.Enabled = false;
                AppendLog("已停止。可以修改配置后重新启动。");
            });
        }
    }

    // ---------------------------------------------------------------
    // 状态灯（后台线程探活，避免 HTTP 超时卡住界面）
    // ---------------------------------------------------------------

    void RefreshStatusAsync()
    {
        if (probing) return;
        probing = true;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                Tuple<string, bool?> stNewApi, stSoVits, stAdapter, stAiri;
                if (Furina.NewApiEnabled)
                {
                    bool ok = Furina.Probe(Furina.cfg.NewApiProbe);
                    stNewApi = new Tuple<string, bool?>(ok ? "运行中" : "未响应", ok);
                }
                else stNewApi = new Tuple<string, bool?>("未启用", null);

                stSoVits = ProbeStatus(Furina.cfg.SoVitsProbe);
                stAdapter = ProbeStatus(Furina.cfg.AdapterProbe);
                bool airi = Process.GetProcessesByName("airi").Length > 0;
                stAiri = new Tuple<string, bool?>(airi ? "运行中" : "未运行", airi);

                Ui(delegate
                {
                    SetStatus(lblStNewApi, stNewApi);
                    SetStatus(lblStSoVits, stSoVits);
                    SetStatus(lblStAdapter, stAdapter);
                    SetStatus(lblStAiri, stAiri);
                });
            }
            finally
            {
                probing = false;
            }
        });
    }

    static Tuple<string, bool?> ProbeStatus(string url)
    {
        if (string.IsNullOrEmpty(url)) return new Tuple<string, bool?>("未配置", null);
        bool ok = Furina.Probe(url);
        return new Tuple<string, bool?>(ok ? "运行中" : "未响应", ok);
    }

    void SetStatus(Label lbl, Tuple<string, bool?> st)
    {
        string prefix = lbl.Text.Split(':')[0];
        lbl.Text = prefix + ": " + st.Item1;
        lbl.BackColor = st.Item2 == null ? Color.LightGray
            : (st.Item2.Value ? Color.FromArgb(198, 239, 206) : Color.FromArgb(255, 199, 206));
    }

    // ---------------------------------------------------------------
    // UI 辅助
    // ---------------------------------------------------------------

    void Ui(Action a)
    {
        try { if (!IsDisposed) Invoke(a); } catch { }
    }

    void AppendLog(string line)
    {
        Ui(delegate
        {
            if (txtLog.TextLength > 60000) txtLog.Clear();
            txtLog.AppendText(line + "\r\n");
        });
    }

    void OnCoreLog(string line)
    {
        AppendLog(line);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Furina.RequestStop();
        if (runThread != null) runThread.Join(8000);
        base.OnFormClosing(e);
    }
}
