// -*- coding: utf-8 -*-
/*
 * furina_gui.cs - 芙宁娜整合启动器图形界面（v4）
 *
 * 启动流程：SplashForm（渐变背景 + "稍等片刻，你的蓝莓小蛋糕正在路上……"
 *           逐字弹跳动画：幼圆体、上白下蓝渐变、easeOutBounce 非线性缓动、
 *           渲染线程贴合显示器刷新率）。若配置完整（AIRI 与语音服务脚本路径有效，
 *           NewAPI 可选）则在 Splash 阶段直接拉起全部组件，主界面打开即为
 *           "已启动"状态。
 * 主界面：组件路径（逐项即时校验 ✓/✗）、行为（互斥）、运行（启停 + 状态灯 + 教程）、
 *         实时日志。状态探活在后台线程执行，不卡 UI。
 * 教程窗口：Markdown 全文连贯渲染，左侧章节导航点击即跳转到对应章节。
 *
 * 不带参数双击 = GUI；带参数（如 --console / --exit-after=N）走控制台模式。
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class Program
{
    public static bool AutoStarted;
    public static Thread AutoRunThread;

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

        SplashForm splash = new SplashForm();
        splash.Show();
        ManualResetEvent initDone = new ManualResetEvent(false);
        bool[] autoStart = new bool[1];
        new Thread(delegate ()
        {
            try
            {
                Furina.LoadIni();
                autoStart[0] = ConfigComplete(Furina.cfg);
            }
            catch { }
            Thread.Sleep(2300); // 至少放完一轮动画
            initDone.Set();
        }) { IsBackground = true }.Start();
        while (!initDone.WaitOne(0)) Application.DoEvents();

        if (autoStart[0])
        {
            // 配置完整：在启动画面阶段直接拉起全部组件
            AutoStarted = true;
            Furina.ResetState();
            AutoRunThread = new Thread(CoreRunWorker);
            AutoRunThread.IsBackground = true;
            AutoRunThread.Start();
            Thread.Sleep(500); // 让启动日志先滚动起来
        }
        splash.Close();

        Application.Run(new MainForm());
        return 0;
    }

    // 配置完整性：AIRI 与语音服务脚本必填且存在；NewAPI 可空（不使用网关）
    static bool ConfigComplete(Furina.Cfg c)
    {
        return !string.IsNullOrEmpty(c.AiriExe) && File.Exists(c.AiriExe)
            && !string.IsNullOrEmpty(c.TtsBat) && File.Exists(Furina.ResolvePath(c.TtsBat));
    }

    // 自动启动与手动启动共用的运行体
    public static void CoreRunWorker()
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
        }
    }
}

// ---------------------------------------------------------------
// 启动画面：渐变背景 + 逐字弹跳（非线性缓动，贴合显示器刷新率）
// ---------------------------------------------------------------

class CharLabel : Label
{
    // 文字纵向渐变：上白下蓝，逐渐变深
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (LinearGradientBrush br = new LinearGradientBrush(
            new Rectangle(0, 0, Width, Height),
            Color.White, Color.FromArgb(25, 90, 205), LinearGradientMode.Vertical))
        using (StringFormat fmt = new StringFormat
        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            e.Graphics.DrawString(Text, Font, br, new RectangleF(0, 0, Width, Height), fmt);
        }
    }
}

class SplashForm : Form
{
    const string MSG = "稍等片刻，你的蓝莓小蛋糕正在路上……";
    readonly CharLabel[] chars = new CharLabel[MSG.Length];
    readonly int baseY = 70;
    readonly int[] positions = new int[MSG.Length];
    readonly bool[] shown = new bool[MSG.Length];

    volatile bool stopRender;
    Thread renderThread;
    double cycleStart;
    int refreshHz = 60;

    // 每字延迟登场与弹跳时长（秒），落稳后停留时长
    const double CHAR_DELAY = 0.085, CHAR_DUR = 0.95, HOLD = 0.9;

    public SplashForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(560, 240);
        DoubleBuffered = true;

        Label caption = new Label();
        caption.Text = "furina";
        caption.Font = new Font("Segoe UI", 10, FontStyle.Italic);
        caption.ForeColor = Color.FromArgb(120, 150, 190);
        caption.AutoSize = true;
        caption.BackColor = Color.Transparent;
        caption.Location = new Point((Width - caption.PreferredWidth) / 2, 26);
        Controls.Add(caption);

        Font f = PickFont();
        using (Graphics g = CreateGraphics())
        {
            int total = 0;
            int[] widths = new int[MSG.Length];
            for (int i = 0; i < MSG.Length; i++)
            {
                widths[i] = TextRenderer.MeasureText(g, MSG[i].ToString(), f).Width - 7;
                total += widths[i];
            }
            int x = (Width - total) / 2;
            for (int i = 0; i < MSG.Length; i++)
            {
                chars[i] = new CharLabel();
                chars[i].Text = MSG[i].ToString();
                chars[i].Font = f;
                chars[i].BackColor = Color.Transparent;
                chars[i].Size = new Size(widths[i] + 9, 38);
                chars[i].Location = new Point(x, baseY - 80);
                chars[i].Visible = false;
                Controls.Add(chars[i]);
                x += widths[i];
            }
        }

        ProgressBar bar = new ProgressBar();
        bar.Style = ProgressBarStyle.Marquee;
        bar.Size = new Size(400, 8);
        bar.Location = new Point((Width - 400) / 2, 170);
        Controls.Add(bar);

        refreshHz = GetRefreshHz();
        cycleStart = Environment.TickCount / 1000.0;
        renderThread = new Thread(RenderLoop);
        renderThread.IsBackground = true;
        renderThread.Priority = ThreadPriority.AboveNormal;
        renderThread.Start();
    }

    static Font PickFont()
    {
        string[] preferred = { "幼圆", "YouYuan", "Microsoft YaHei UI" };
        InstalledFontCollection ifc = new InstalledFontCollection();
        foreach (string name in preferred)
        {
            foreach (FontFamily ff in ifc.Families)
            {
                if (ff.Name == name)
                    return new Font(ff, 17, FontStyle.Bold);
            }
        }
        return new Font(FontFamily.GenericSansSerif, 17, FontStyle.Bold);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using (LinearGradientBrush br = new LinearGradientBrush(
            ClientRectangle, Color.White, Color.FromArgb(222, 236, 252),
            LinearGradientMode.Vertical))
        {
            e.Graphics.FillRectangle(br, ClientRectangle);
        }
    }

    // Penner easeOutBounce：非线性下落 + 真实弹跳衰减
    static double EaseOutBounce(double t)
    {
        const double n1 = 7.5625, d1 = 2.75;
        if (t < 1 / d1) return n1 * t * t;
        if (t < 2 / d1) { t -= 1.5 / d1; return n1 * t * t + 0.75; }
        if (t < 2.5 / d1) { t -= 2.25 / d1; return n1 * t * t + 0.9375; }
        t -= 2.625 / d1;
        return n1 * t * t + 0.984375;
    }

    void RenderLoop()
    {
        Stopwatch sw = Stopwatch.StartNew();
        double frameMs = 1000.0 / refreshHz;
        while (!stopRender)
        {
            double frameStart = sw.Elapsed.TotalMilliseconds;
            ComputeFrame();
            if (IsHandleCreated && !IsDisposed)
            {
                try { BeginInvoke((Action)ApplyFrame); } catch { }
            }
            // 贴帧：自旋到下一帧边界（Sleep 的 15ms 粒度达不到高刷）
            while (sw.Elapsed.TotalMilliseconds - frameStart < frameMs)
            {
                Thread.SpinWait(500);
            }
        }
    }

    void ComputeFrame()
    {
        double now = Environment.TickCount / 1000.0;
        double cycleLen = MSG.Length * CHAR_DELAY + CHAR_DUR + HOLD;
        if (now - cycleStart > cycleLen) cycleStart = now; // 循环播放
        double t = now - cycleStart;
        for (int i = 0; i < chars.Length; i++)
        {
            double p = (t - i * CHAR_DELAY) / CHAR_DUR;
            if (p < 0)
            {
                shown[i] = false;
                positions[i] = baseY - 80;
            }
            else if (p >= 1)
            {
                shown[i] = true;
                positions[i] = baseY;
            }
            else
            {
                shown[i] = true;
                positions[i] = baseY - (int)((1 - EaseOutBounce(p)) * 80);
            }
        }
    }

    void ApplyFrame()
    {
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i].IsDisposed) return;
            if (chars[i].Visible != shown[i]) chars[i].Visible = shown[i];
            if (chars[i].Top != positions[i]) chars[i].Top = positions[i];
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        stopRender = true;
        if (renderThread != null) renderThread.Join(300);
        base.OnFormClosing(e);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    static int GetRefreshHz()
    {
        try
        {
            DEVMODE dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (EnumDisplaySettings(null, -1, ref dm) && dm.dmDisplayFrequency > 0)
                return dm.dmDisplayFrequency;
        }
        catch { }
        return 60;
    }
}

// ---------------------------------------------------------------
// 教程窗口：Markdown 全文连贯渲染 + 章节导航跳转
// ---------------------------------------------------------------

class TutorialForm : Form
{
    public TutorialForm()
    {
        Text = "使用教程";
        Width = 820;
        Height = 600;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);

        ListBox nav = new ListBox();
        nav.Dock = DockStyle.Left;
        nav.Width = 210;
        nav.Font = new Font("Microsoft YaHei UI", 10F);
        nav.BorderStyle = BorderStyle.None;
        nav.BackColor = Color.FromArgb(245, 249, 253);
        foreach (string t in TutorialText.Titles) nav.Items.Add(t);

        RichTextBox body = new RichTextBox();
        body.Dock = DockStyle.Fill;
        body.ReadOnly = true;
        body.BackColor = Color.White;
        body.BorderStyle = BorderStyle.None;
        body.DetectUrls = false;
        try
        {
            body.Rtf = MiniMd.ToRtf(TutorialText.FullMarkdown);
        }
        catch
        {
            body.Text = TutorialText.FullMarkdown; // RTF 解析失败兜底为纯文本
        }

        nav.SelectedIndexChanged += delegate
        {
            if (nav.SelectedIndex < 0) return;
            int idx = body.Find(TutorialText.Titles[nav.SelectedIndex]);
            if (idx >= 0)
            {
                body.SelectionStart = idx;
                body.ScrollToCaret();
            }
        };

        Controls.Add(body);
        Controls.Add(nav);
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
        Width = 900;
        Height = 700;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(800, 620);
        Font = new Font("Microsoft YaHei UI", 9F);

        TableLayoutPanel root = new TableLayoutPanel();
        root.Dock = DockStyle.Top;
        root.AutoSize = true;
        root.ColumnCount = 1;
        root.Padding = new Padding(10);
        root.Controls.Add(BuildPathsGroup(), 0, 0);
        root.Controls.Add(BuildBehaviorGroup(), 0, 1);
        root.Controls.Add(BuildRunGroup(), 0, 2);
        Controls.Add(root);

        txtLog = new TextBox();
        txtLog.Dock = DockStyle.Bottom;
        txtLog.Height = 170;
        txtLog.Multiline = true;
        txtLog.ReadOnly = true;
        txtLog.ScrollBars = ScrollBars.Vertical;
        txtLog.BackColor = Color.FromArgb(25, 28, 36);
        txtLog.ForeColor = Color.FromArgb(200, 205, 215);
        txtLog.Font = new Font("Consolas", 9);
        Controls.Add(txtLog);
        root.BringToFront();

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

        // 配置完整 → Splash 阶段已自动启动：界面直接呈现运行中状态
        if (Program.AutoStarted && Program.AutoRunThread != null)
        {
            AttachRun(Program.AutoRunThread);
            AppendLog("检测到完整配置，已自动启动全部组件。");
        }
        else
        {
            AppendLog("配置已载入。浏览选择你的组件路径后点「启动」。");
        }
    }

    // ---------------------------------------------------------------
    // 界面构建
    // ---------------------------------------------------------------

    GroupBox BuildPathsGroup()
    {
        GroupBox grp = new GroupBox();
        grp.Text = "组件路径";
        grp.AutoSize = true;
        grp.Dock = DockStyle.Top;
        grp.Padding = new Padding(8, 4, 8, 8);

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
        txtTtsBat = AddRow(grid, row, "语音服务脚本", "浏览", delegate
        {
            string p = PickFile("选择语音服务启动脚本（bat）", "批处理|*.bat;*.cmd");
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
        txtNewApiProbe = AddRow(grid, row, "NewAPI 地址", null, null, out markNewApiProbe);
        row++;
        txtSoVitsProbe = AddRow(grid, row, "语音服务地址", null, null, out markSoVits);
        row++;
        txtAdapterProbe = AddRow(grid, row, "语音适配器地址", null, null, out markAdapter);
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
        grp.Text = "行为";
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
        chkAutoExit.CheckedChanged += delegate
        {
            if (chkAutoExit.Checked && chkKeep.Checked) chkKeep.Checked = false;
        };
        chkKeep.CheckedChanged += delegate
        {
            if (chkKeep.Checked && chkAutoExit.Checked) chkAutoExit.Checked = false;
        };

        flow.Controls.Add(chkAutoExit);
        flow.Controls.Add(chkKeep);
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

        Button btnSave = FlatButton("保存配置", Color.FromArgb(235, 240, 248));
        btnSave.Click += delegate { FieldsToCfg(); Furina.SaveIni(); Furina.Log("配置已保存到 furina.ini"); };
        btnStart = FlatButton("▶ 启动", Color.FromArgb(198, 239, 206));
        btnStart.Click += delegate { StartRun(); };
        btnStop = FlatButton("■ 停止（回收服务）", Color.FromArgb(255, 199, 206));
        btnStop.Enabled = false;
        btnStop.Click += delegate { Furina.RequestStop(); };
        Button btnTutorial = FlatButton("? 教程", Color.FromArgb(255, 235, 180));
        btnTutorial.Click += delegate { new TutorialForm().Show(this); };

        flow.Controls.Add(btnSave);
        flow.Controls.Add(btnStart);
        flow.Controls.Add(btnStop);
        flow.Controls.Add(btnTutorial);
        flow.Controls.Add(MakeStatusLabel("NewAPI", out lblStNewApi));
        flow.Controls.Add(MakeStatusLabel("语音服务", out lblStSoVits));
        flow.Controls.Add(MakeStatusLabel("语音适配器", out lblStAdapter));
        flow.Controls.Add(MakeStatusLabel("AIRI", out lblStAiri));
        grp.Controls.Add(flow);
        return grp;
    }

    static Button FlatButton(string text, Color back)
    {
        Button b = new Button();
        b.Text = text;
        b.BackColor = back;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.FromArgb(190, 200, 215);
        b.Margin = new Padding(3, 4, 3, 4);
        b.AutoSize = true;
        b.Padding = new Padding(6, 2, 6, 2);
        return b;
    }

    Control MakeStatusLabel(string name, out Label lbl)
    {
        lbl = new Label();
        lbl.Text = name + ": --";
        lbl.AutoSize = true;
        lbl.Padding = new Padding(6, 3, 6, 3);
        lbl.Margin = new Padding(8, 7, 0, 3);
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
        lbl.Padding = new Padding(4, 7, 0, 0);
        grid.Controls.Add(lbl, 0, row);

        TextBox tb = new TextBox();
        tb.Dock = DockStyle.Fill;
        tb.Margin = new Padding(3, 4, 3, 4);
        grid.Controls.Add(tb, 1, row);

        if (btnText != null)
        {
            Button btn = FlatButton(btnText, Color.FromArgb(240, 244, 250));
            btn.Dock = DockStyle.Fill;
            btn.Click += onBrowse;
            grid.Controls.Add(btn, 2, row);
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
        string airi = txtAiri.Text.Trim();
        if (airi.Length == 0) SetMark(markAiri, null, "");
        else if (!File.Exists(airi)) SetMark(markAiri, false, "文件不存在：" + airi);
        else if (!string.Equals(Path.GetFileName(airi), "airi.exe", StringComparison.OrdinalIgnoreCase))
            SetMark(markAiri, false, "文件存在，但文件名不是 airi.exe，请确认选对了 AIRI 主程序");
        else SetMark(markAiri, true, "AIRI 主程序（静态校验通过；是否运行中见下方状态灯）");

        string tts = txtTtsBat.Text.Trim();
        string[] runExts = { ".bat", ".cmd", ".exe", ".ps1", ".vbs" };
        if (tts.Length == 0) SetMark(markTts, null, "");
        else if (!File.Exists(Furina.ResolvePath(tts))) SetMark(markTts, false, "文件不存在：" + Furina.ResolvePath(tts));
        else if (Array.IndexOf(runExts, Path.GetExtension(tts).ToLowerInvariant()) < 0)
            SetMark(markTts, false, "不是可执行的启动脚本（.bat/.cmd/.exe/.ps1/.vbs）");
        else SetMark(markTts, true, "脚本存在（静态校验；能否正常拉起以启动后的状态灯为准）");

        string napi = txtNewApiExe.Text.Trim();
        bool napiOk = false;
        if (napi.Length == 0) SetMark(markNewApi, null, "未配置 = 不使用网关");
        else if (!File.Exists(napi)) SetMark(markNewApi, false, "文件不存在：" + napi);
        else if (!string.Equals(Path.GetExtension(napi), ".exe", StringComparison.OrdinalIgnoreCase))
            SetMark(markNewApi, false, "NewAPI 单文件版应为 .exe");
        else { SetMark(markNewApi, true, "NewAPI 主程序"); napiOk = true; }

        bool gatewayOn = napiOk;
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
    }

    void StartRun()
    {
        FieldsToCfg();
        Furina.SaveIni();
        Furina.ResetState();
        Thread t = new Thread(Program.CoreRunWorker);
        t.IsBackground = true;
        t.Start();
        AttachRun(t);
    }

    // 进入运行中状态：禁用启动键、挂监控线程，运行体自然结束（如 AIRI 关闭）后恢复按钮
    void AttachRun(Thread t)
    {
        runThread = t;
        btnStart.Enabled = false;
        btnStop.Enabled = true;
        Thread watcher = new Thread(delegate ()
        {
            t.Join();
            Ui(delegate
            {
                btnStart.Enabled = true;
                btnStop.Enabled = false;
                AppendLog("已停止。可以修改配置后重新启动。");
            });
        });
        watcher.IsBackground = true;
        watcher.Start();
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
