// -*- coding: utf-8 -*-
/*
 * furina_gui.cs - 芙宁娜整合启动器图形界面
 *
 * 启动器页：浏览选择 AIRI / TTS 启动脚本 / NewAPI（可选）路径 → 保存配置 → 一键启动/停止，
 *           四组件状态灯 + 实时日志。除 AIRI 外所有项都有默认值（项目自带 TTS 方案）。
 * 角色卡页：选择 card.json → 外部编辑器修改 → 一键打包备份 → 内置题库验收测试。
 *
 * 不带参数双击 = GUI；带参数（如 --console / --exit-after=N）走控制台模式。
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
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
        Application.Run(new MainForm());
        return 0;
    }
}

class MainForm : Form
{
    // 启动器页控件
    TextBox txtAiri, txtTtsBat, txtNewApiExe, txtNewApiDir, txtNewApiProbe,
            txtSoVitsProbe, txtAdapterProbe, txtSession;
    CheckBox chkAutoExit, chkKeep;
    NumericUpDown numInterval;
    Button btnSave, btnStart, btnStop;
    Label lblStNewApi, lblStSoVits, lblStAdapter, lblStAiri;
    TextBox txtLog;
    System.Windows.Forms.Timer statusTimer;

    // 角色卡页控件
    TextBox txtCardJson, txtCardZip, txtApiBase, txtApiKey, txtModel, txtResults;
    Button btnEditCard, btnPack, btnTest;
    Label lblPackResult, lblTestStatus;

    Thread runThread;
    Thread testThread;

    static readonly string DEFAULT_MANIFEST =
        "{\"format\":\"airi-character-card\",\"version\":1,\"card\":{\"path\":\"card.json\",\"spec\":\"chara_card_v3\"}}";

    public MainForm()
    {
        Text = "芙宁娜 · 整合启动器";
        Width = 860;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 620);

        TabControl tabs = new TabControl();
        tabs.Dock = DockStyle.Fill;
        tabs.TabPages.Add(BuildLauncherTab());
        tabs.TabPages.Add(BuildCardTab());
        Controls.Add(tabs);

        statusTimer = new System.Windows.Forms.Timer();
        statusTimer.Interval = 2000;
        statusTimer.Tick += delegate { RefreshStatus(); };
        statusTimer.Start();

        Furina.OnLog += OnCoreLog;
        Furina.LoadIni();
        FieldsFromCfg();
        AppendLog("配置已载入。浏览选择你的组件路径后点「启动」。");
    }

    // ---------------------------------------------------------------
    // 启动器页
    // ---------------------------------------------------------------

    TabPage BuildLauncherTab()
    {
        TabPage page = new TabPage("启动器");
        TableLayoutPanel root = new TableLayoutPanel();
        root.Dock = DockStyle.Top;
        root.AutoSize = true;
        root.ColumnCount = 1;
        root.Padding = new Padding(8);

        GroupBox grpPaths = new GroupBox();
        grpPaths.Text = "组件路径（除 AIRI 外均有默认值；NewAPI 留空 = 不使用网关）";
        grpPaths.AutoSize = true;
        grpPaths.Dock = DockStyle.Top;
        TableLayoutPanel grid = new TableLayoutPanel();
        grid.Dock = DockStyle.Top;
        grid.AutoSize = true;
        grid.ColumnCount = 3;
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));

        int row = 0;
        txtAiri = AddRow(grid, row++, "AIRI 程序", "浏览", false, delegate
        {
            string p = PickFile("选择 AIRI 主程序 (airi.exe)", "可执行文件|*.exe");
            if (p != null) txtAiri.Text = p;
        });
        txtTtsBat = AddRow(grid, row++, "TTS 启动脚本", "浏览", false, delegate
        {
            string p = PickFile("选择 TTS 启动脚本（bat）", "批处理|*.bat");
            if (p != null) txtTtsBat.Text = p;
        });
        txtNewApiExe = AddRow(grid, row++, "NewAPI 程序（可空）", "浏览", false, delegate
        {
            string p = PickFile("选择 NewAPI 主程序（不需要网关可跳过）", "可执行文件|*.exe");
            if (p != null) txtNewApiExe.Text = p;
        });
        txtNewApiDir = AddRow(grid, row++, "NewAPI 数据目录", "浏览", true, delegate
        {
            string p = PickFolder("选择 NewAPI 数据目录（含 one-api.db）");
            if (p != null) txtNewApiDir.Text = p;
        });
        txtNewApiProbe = AddRow(grid, row++, "NewAPI 探活地址", null, false, null);
        txtSoVitsProbe = AddRow(grid, row++, "SoVITS 探活地址", null, false, null);
        txtAdapterProbe = AddRow(grid, row++, "适配器探活地址", null, false, null);
        txtSession = AddRow(grid, row++, "网关密钥（空=自动生成）", null, false, null);
        grpPaths.Controls.Add(grid);
        root.Controls.Add(grpPaths, 0, root.RowCount);
        root.RowCount++;

        GroupBox grpOpt = new GroupBox();
        grpOpt.Text = "行为";
        grpOpt.AutoSize = true;
        grpOpt.Dock = DockStyle.Top;
        FlowLayoutPanel optFlow = new FlowLayoutPanel();
        optFlow.Dock = DockStyle.Top;
        optFlow.AutoSize = true;
        optFlow.WrapContents = false;
        chkAutoExit = new CheckBox();
        chkAutoExit.Text = "AIRI 关闭时自动退出并回收";
        chkAutoExit.AutoSize = true;
        chkKeep = new CheckBox();
        chkKeep.Text = "退出时保留服务运行";
        chkKeep.AutoSize = true;
        Label lblInt = new Label();
        lblInt.Text = "探活周期(秒)";
        lblInt.AutoSize = true;
        numInterval = new NumericUpDown();
        numInterval.Minimum = 5;
        numInterval.Maximum = 300;
        numInterval.Value = 15;
        optFlow.Controls.Add(chkAutoExit);
        optFlow.Controls.Add(chkKeep);
        optFlow.Controls.Add(lblInt);
        optFlow.Controls.Add(numInterval);
        grpOpt.Controls.Add(optFlow);
        root.Controls.Add(grpOpt, 0, root.RowCount);
        root.RowCount++;

        GroupBox grpRun = new GroupBox();
        grpRun.Text = "运行";
        grpRun.AutoSize = true;
        grpRun.Dock = DockStyle.Top;
        FlowLayoutPanel runFlow = new FlowLayoutPanel();
        runFlow.Dock = DockStyle.Top;
        runFlow.AutoSize = true;
        runFlow.WrapContents = false;
        btnSave = new Button();
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
        runFlow.Controls.Add(btnSave);
        runFlow.Controls.Add(btnStart);
        runFlow.Controls.Add(btnStop);
        runFlow.Controls.Add(MakeStatusLabel("NewAPI", out lblStNewApi));
        runFlow.Controls.Add(MakeStatusLabel("SoVITS", out lblStSoVits));
        runFlow.Controls.Add(MakeStatusLabel("适配器", out lblStAdapter));
        runFlow.Controls.Add(MakeStatusLabel("AIRI", out lblStAiri));
        grpRun.Controls.Add(runFlow);
        root.Controls.Add(grpRun, 0, root.RowCount);
        root.RowCount++;

        txtLog = new TextBox();
        txtLog.Dock = DockStyle.Fill;
        txtLog.Multiline = true;
        txtLog.ReadOnly = true;
        txtLog.ScrollBars = ScrollBars.Vertical;
        txtLog.BackColor = Color.FromArgb(30, 30, 30);
        txtLog.ForeColor = Color.FromArgb(200, 200, 200);
        txtLog.Font = new Font("Consolas", 9);

        page.Controls.Add(txtLog);
        page.Controls.Add(root);
        txtLog.BringToFront();
        txtLog.Dock = DockStyle.Bottom;
        txtLog.Height = 170;
        root.BringToFront();
        return page;
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

    TextBox AddRow(TableLayoutPanel grid, int row, string label, string btnText, bool folder, EventHandler onBrowse)
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
        tb.Margin = new Padding(3, 3, 3, 3);
        grid.Controls.Add(tb, 1, row);
        if (btnText != null)
        {
            Button btn = new Button();
            btn.Text = btnText;
            btn.Dock = DockStyle.Fill;
            btn.Click += onBrowse;
            grid.Controls.Add(btn, 2, row);
        }
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

    void RefreshStatus()
    {
        if (Furina.NewApiEnabled)
        {
            bool ok = Furina.Probe(Furina.cfg.NewApiProbe);
            SetStatus(lblStNewApi, new Tuple<string, bool?>(ok ? "运行中" : "未响应", ok));
        }
        else
        {
            SetStatus(lblStNewApi, new Tuple<string, bool?>("未启用", null));
        }
        SetStatus(lblStSoVits, Status(Furina.cfg.SoVitsProbe));
        SetStatus(lblStAdapter, Status(Furina.cfg.AdapterProbe));
        bool airi = Process.GetProcessesByName("airi").Length > 0;
        SetStatus(lblStAiri, new Tuple<string, bool?>(airi ? "运行中" : "未运行", airi));
    }

    Tuple<string, bool?> Status(string url)
    {
        if (string.IsNullOrEmpty(url)) return new Tuple<string, bool?>("未配置", null);
        bool ok = Furina.Probe(url);
        return new Tuple<string, bool?>(ok ? "运行中" : "未响应", ok);
    }

    void SetStatus(Label lbl, Tuple<string, bool?> st)
    {
        string prefix = lbl.Text.Split(':')[0];
        lbl.Text = prefix + ": " + st.Item1;
        lbl.BackColor = st.Item2 == null ? Color.LightGray : (st.Item2.Value ? Color.FromArgb(198, 239, 206) : Color.FromArgb(255, 199, 206));
    }

    // ---------------------------------------------------------------
    // 角色卡页
    // ---------------------------------------------------------------

    TabPage BuildCardTab()
    {
        TabPage page = new TabPage("角色卡工具");
        TableLayoutPanel root = new TableLayoutPanel();
        root.Dock = DockStyle.Top;
        root.AutoSize = true;
        root.ColumnCount = 1;
        root.Padding = new Padding(8);

        GroupBox grpPack = new GroupBox();
        grpPack.Text = "打包与备份（改卡流程：编辑 → 打包 → 到 AIRI 里重新导入并新开会话）";
        grpPack.AutoSize = true;
        grpPack.Dock = DockStyle.Top;
        TableLayoutPanel grid = new TableLayoutPanel();
        grid.Dock = DockStyle.Top;
        grid.AutoSize = true;
        grid.ColumnCount = 4;
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));

        grid.RowCount = 1;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Label l1 = new Label();
        l1.Text = "card.json";
        l1.AutoSize = true;
        l1.Padding = new Padding(4, 6, 0, 0);
        grid.Controls.Add(l1, 0, 0);
        txtCardJson = new TextBox();
        txtCardJson.Dock = DockStyle.Fill;
        grid.Controls.Add(txtCardJson, 1, 0);
        Button bBrowse = new Button();
        bBrowse.Text = "浏览";
        bBrowse.Dock = DockStyle.Fill;
        bBrowse.Click += delegate
        {
            string p = PickFile("选择角色卡源文件 card.json", "JSON|*.json");
            if (p != null) txtCardJson.Text = p;
        };
        grid.Controls.Add(bBrowse, 2, 0);
        btnEditCard = new Button();
        btnEditCard.Text = "编辑";
        btnEditCard.Dock = DockStyle.Fill;
        btnEditCard.Click += delegate
        {
            string p = txtCardJson.Text.Trim();
            if (!File.Exists(p)) { MessageBox.Show("card.json 不存在：" + p); return; }
            try { Process.Start(p); } catch (Exception ex) { MessageBox.Show("打开编辑器失败: " + ex.Message); }
        };
        grid.Controls.Add(btnEditCard, 3, 0);

        grid.RowCount = 2;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Label l2 = new Label();
        l2.Text = "输出卡包";
        l2.AutoSize = true;
        l2.Padding = new Padding(4, 6, 0, 0);
        grid.Controls.Add(l2, 0, 1);
        txtCardZip = new TextBox();
        txtCardZip.Dock = DockStyle.Fill;
        grid.Controls.Add(txtCardZip, 1, 1);
        Button bBrowseZip = new Button();
        bBrowseZip.Text = "浏览";
        bBrowseZip.Dock = DockStyle.Fill;
        bBrowseZip.Click += delegate
        {
            SaveFileDialog dlg = new SaveFileDialog();
            dlg.Filter = "AIRI 角色卡|*.zip";
            dlg.FileName = "芙宁娜·AIRI角色卡.zip";
            if (dlg.ShowDialog(this) == DialogResult.OK) txtCardZip.Text = dlg.FileName;
        };
        grid.Controls.Add(bBrowseZip, 2, 1);
        btnPack = new Button();
        btnPack.Text = "打包";
        btnPack.Dock = DockStyle.Fill;
        btnPack.BackColor = Color.FromArgb(255, 235, 156);
        btnPack.Click += delegate { PackCard(); };
        grid.Controls.Add(btnPack, 3, 1);

        grid.RowCount = 3;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        lblPackResult = new Label();
        lblPackResult.AutoSize = true;
        lblPackResult.Padding = new Padding(4, 4, 0, 2);
        lblPackResult.Text = " ";
        grid.Controls.Add(lblPackResult, 0, 2);
        grid.SetColumnSpan(lblPackResult, 4);
        grpPack.Controls.Add(grid);
        root.Controls.Add(grpPack, 0, root.RowCount);
        root.RowCount++;

        GroupBox grpTest = new GroupBox();
        grpTest.Text = "验收测试（对当前 system_prompt 跑内置题库，人工核对元泄漏/节奏/黑话）";
        grpTest.Dock = DockStyle.Fill;
        TableLayoutPanel tgrid = new TableLayoutPanel();
        tgrid.Dock = DockStyle.Top;
        tgrid.AutoSize = true;
        tgrid.ColumnCount = 4;
        tgrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        tgrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        tgrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        tgrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        tgrid.RowCount = 1;
        tgrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Label t1 = new Label();
        t1.Text = "API 地址";
        t1.AutoSize = true;
        t1.Padding = new Padding(4, 6, 0, 0);
        tgrid.Controls.Add(t1, 0, 0);
        txtApiBase = new TextBox();
        txtApiBase.Dock = DockStyle.Fill;
        tgrid.Controls.Add(txtApiBase, 1, 0);
        Label t2 = new Label();
        t2.Text = "密钥";
        t2.AutoSize = true;
        t2.Padding = new Padding(4, 6, 0, 0);
        tgrid.Controls.Add(t2, 2, 0);
        txtApiKey = new TextBox();
        txtApiKey.Dock = DockStyle.Fill;
        txtApiKey.PasswordChar = '*';
        tgrid.Controls.Add(txtApiKey, 3, 0);

        tgrid.RowCount = 2;
        tgrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Label t3 = new Label();
        t3.Text = "模型名";
        t3.AutoSize = true;
        t3.Padding = new Padding(4, 6, 0, 0);
        tgrid.Controls.Add(t3, 0, 1);
        txtModel = new TextBox();
        txtModel.Dock = DockStyle.Fill;
        tgrid.Controls.Add(txtModel, 1, 1);
        btnTest = new Button();
        btnTest.Text = "运行验收";
        btnTest.Dock = DockStyle.Fill;
        btnTest.BackColor = Color.FromArgb(198, 239, 206);
        btnTest.Click += delegate { RunTests(); };
        tgrid.Controls.Add(btnTest, 2, 1);
        lblTestStatus = new Label();
        lblTestStatus.AutoSize = true;
        lblTestStatus.Padding = new Padding(4, 6, 0, 0);
        lblTestStatus.Text = " ";
        tgrid.Controls.Add(lblTestStatus, 3, 1);

        tgrid.RowCount = 3;
        tgrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        txtResults = new TextBox();
        txtResults.Dock = DockStyle.Fill;
        txtResults.Multiline = true;
        txtResults.ReadOnly = true;
        txtResults.ScrollBars = ScrollBars.Vertical;
        txtResults.Height = 230;
        tgrid.Controls.Add(txtResults, 0, 2);
        tgrid.SetColumnSpan(txtResults, 4);
        grpTest.Controls.Add(tgrid);
        root.Controls.Add(grpTest, 0, root.RowCount);
        root.RowCount++;

        page.Controls.Add(root);
        return page;
    }

    void PackCard()
    {
        string cardPath = txtCardJson.Text.Trim();
        string zipPath = txtCardZip.Text.Trim();
        if (!File.Exists(cardPath)) { MessageBox.Show("card.json 不存在：" + cardPath); return; }
        if (string.IsNullOrEmpty(zipPath)) { MessageBox.Show("请填写输出卡包路径"); return; }
        try
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(zipPath));
            Directory.CreateDirectory(dir);
            string backup = null;
            if (File.Exists(zipPath))
            {
                backup = zipPath + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak.zip";
                File.Copy(zipPath, backup);
            }
            string manifestPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cardPath)), "manifest.json");
            using (FileStream fs = new FileStream(zipPath, FileMode.Create))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                if (File.Exists(manifestPath))
                {
                    zip.CreateEntryFromFile(manifestPath, "manifest.json");
                }
                else
                {
                    ZipArchiveEntry me = zip.CreateEntry("manifest.json");
                    using (StreamWriter w = new StreamWriter(me.Open(), new UTF8Encoding(false)))
                        w.Write(DEFAULT_MANIFEST);
                }
                zip.CreateEntryFromFile(cardPath, "card.json");
            }
            string msg = "已打包：" + zipPath + (backup != null ? "\n旧包已备份：" + backup : "") +
                "\n\n下一步：到 AIRI 设置里重新导入卡包，并【新开会话】生效。";
            lblPackResult.Text = DateTime.Now.ToString("HH:mm:ss") + " 打包完成" + (backup != null ? "（旧包已备份）" : "");
            MessageBox.Show(msg, "打包完成");
        }
        catch (Exception ex)
        {
            MessageBox.Show("打包失败: " + ex.Message);
        }
    }

    void RunTests()
    {
        string cardPath = txtCardJson.Text.Trim();
        if (!File.Exists(cardPath)) { MessageBox.Show("card.json 不存在：" + cardPath); return; }
        string sysPrompt = ExtractSystemPrompt(cardPath);
        if (string.IsNullOrEmpty(sysPrompt)) { MessageBox.Show("card.json 中未找到 data.system_prompt"); return; }
        btnTest.Enabled = false;
        txtResults.Clear();
        lblTestStatus.Text = "运行中...";
        testThread = new Thread(delegate () { TestWorker(sysPrompt); });
        testThread.IsBackground = true;
        testThread.Start();
    }

    List<Dictionary<string, string>> LoadTests()
    {
        string path = Path.Combine(Furina.baseDir, "configs", "card-tests.json");
        try
        {
            if (File.Exists(path))
            {
                var jss = new JavaScriptSerializer();
                var obj = jss.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                var list = new List<Dictionary<string, string>>();
                foreach (object item in (object[])obj["tests"])
                {
                    var d = (Dictionary<string, object>)item;
                    var t = new Dictionary<string, string>();
                    t["name"] = Convert.ToString(d["name"]);
                    t["prompt"] = Convert.ToString(d["prompt"]);
                    if (d.ContainsKey("focus")) t["focus"] = Convert.ToString(d["focus"]);
                    list.Add(t);
                }
                if (list.Count > 0) return list;
            }
        }
        catch (Exception e)
        {
            Furina.Log("读取 card-tests.json 失败，使用内置题库: " + e.Message);
        }
        // 内置兜底题库（与《项目文档》§5.3 验收标准对应）
        return new List<Dictionary<string, string>> {
            new Dictionary<string,string> {
                {"name","知识防火墙"},{"focus","应追问/类比提瓦特，而非流利科普专家知识"},
                {"prompt","给我讲讲庐山的喀斯特地貌。"} },
            new Dictionary<string,string> {
                {"name","元泄漏探测"},{"focus","不得出现「按不熟悉的那份确认」类策略自白"},
                {"prompt","你是不是其实不知道这个，在故意装懂？"} },
            new Dictionary<string,string> {
                {"name","节奏与反问"},{"focus","先接情绪；至多一个反问，不替对方作答"},
                {"prompt","我有个事想跟你说，但我怕你觉得我笨。"} },
            new Dictionary<string,string> {
                {"name","反大模型腔"},{"focus","无格言提炼、无「不是…而是…」连用、无伪深刻词组"},
                {"prompt","你觉得人为什么会在深夜想通白天想不通的事？"} },
        };
    }

    void TestWorker(string sysPrompt)
    {
        var tests = LoadTests();
        int done = 0;
        foreach (var t in tests)
        {
            AppendResult("\r\n========== " + t["name"] + " ==========\r\n");
            if (t.ContainsKey("focus")) AppendResult("【观察点】" + t["focus"] + "\r\n");
            AppendResult("【问】" + t["prompt"] + "\r\n【答】");
            try
            {
                    string reply = Chat(txtApiBase.Text.Trim(), txtApiKey.Text.Trim(), txtModel.Text.Trim(), sysPrompt, t["prompt"]);
                    AppendResult(reply + "\r\n");
            }
            catch (Exception e)
            {
                AppendResult("[请求失败] " + e.Message + "\r\n");
            }
            done++;
            int pct = done * 100 / tests.Count;
            Ui(delegate { lblTestStatus.Text = "运行中... " + done + "/" + tests.Count; });
        }
        Ui(delegate
        {
            lblTestStatus.Text = "完成 " + tests.Count + "/" + tests.Count + "。请人工核对各题观察点。";
            btnTest.Enabled = true;
        });
    }

    static string ExtractSystemPrompt(string cardPath)
    {
        var jss = new JavaScriptSerializer();
        var obj = jss.Deserialize<Dictionary<string, object>>(File.ReadAllText(cardPath, Encoding.UTF8));
        if (obj.ContainsKey("data"))
        {
            var data = obj["data"] as Dictionary<string, object>;
            if (data != null && data.ContainsKey("system_prompt"))
                return Convert.ToString(data["system_prompt"]);
        }
        if (obj.ContainsKey("system_prompt"))
            return Convert.ToString(obj["system_prompt"]);
        return "";
    }

    static string Chat(string baseUrl, string key, string model, string system, string user)
    {
        var jss = new JavaScriptSerializer();
        var body = new Dictionary<string, object>();
        body["model"] = model;
        body["temperature"] = 0.8;
        body["stream"] = false;
        body["messages"] = new object[] {
            new Dictionary<string,object> { {"role","system"}, {"content",system} },
            new Dictionary<string,object> { {"role","user"}, {"content",user} },
        };
        string payload = jss.Serialize(body);
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(baseUrl.TrimEnd('/') + "/chat/completions");
        req.Method = "POST";
        req.ContentType = "application/json; charset=utf-8";
        req.Timeout = 180000;
        req.ReadWriteTimeout = 180000;
        if (!string.IsNullOrEmpty(key)) req.Headers["Authorization"] = "Bearer " + key;
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
        using (WebResponse resp = req.GetResponse())
        using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
        {
            var obj = jss.Deserialize<Dictionary<string, object>>(sr.ReadToEnd());
            var choices = (object[])obj["choices"];
            var c0 = (Dictionary<string, object>)choices[0];
            var msg = (Dictionary<string, object>)c0["message"];
            return Convert.ToString(msg["content"]);
        }
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

    void AppendResult(string text)
    {
        Ui(delegate { txtResults.AppendText(text); });
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
