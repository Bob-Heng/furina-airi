// -*- coding: utf-8 -*-
/*
 * furina_gui.cs - 芙宁娜整合启动器图形界面（枫丹夜海 · Hallmark 重制）
 *
 * 主题：枫丹夜海。荒形态（深海军蓝）= 运行态；芒形态（浅湖蓝）= 启动画面。
 * 令牌：Theme 静态类集中定义（枫丹夜海调色板 + 字阶 + 时长 + 缓动）。
 * 动效：胶囊按钮 hover/press 缓动插值、状态灯色彩过渡、卡片入场错峰滑入，
 *       全部 transform/不透明度域、指数缓出，贴显示器刷新率渲染。
 *
 * 行为层与旧版完全一致：组件路径校验、行为互斥、自动启动、看门狗对接、教程。
 *
 * 不带参数双击 = GUI；--console / --exit-after=N 走控制台模式；
 * --selfshot 输出一张界面自检截图（launcher\selfshot.png）后退出，不进自动启动。
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
    public static bool SelfShot;

    [STAThread]
    static int Main(string[] args)
    {
        bool console = false;
        foreach (string a in args ?? new string[0])
        {
            if (a == "--selfshot") SelfShot = true;
            else console = true;
        }
        if (console) return Furina.ConsoleMain(args);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
        {
            MessageBox.Show(e.Exception.ToString(), "furina 未处理异常");
        };
        Furina.InitPaths();

        // DPI 系数先行：一个临时窗体读取当前显示器 DPI（96 基准），
        // 之后所有像素值经 Theme.Px() 缩放，字体（pt）自带 DPI 感知不再重复放大
        try
        {
            using (Form probe = new Form())
            using (Graphics g = probe.CreateGraphics())
            {
                Theme.S = g.DpiX / 96f;
            }
        }
        catch { }

        SplashForm splash = new SplashForm();
        splash.Show();
        ManualResetEvent initDone = new ManualResetEvent(false);
        bool[] autoStart = new bool[1];
        new Thread(delegate ()
        {
            try
            {
                Furina.LoadIni();
                autoStart[0] = !SelfShot && ConfigComplete(Furina.cfg);
            }
            catch { }
            Thread.Sleep(2300);
            initDone.Set();
        }) { IsBackground = true }.Start();
        while (!initDone.WaitOne(0)) Application.DoEvents();

        if (autoStart[0])
        {
            AutoStarted = true;
            Furina.ResetState();
            AutoRunThread = new Thread(CoreRunWorker);
            AutoRunThread.IsBackground = true;
            AutoRunThread.Start();
            Thread.Sleep(500);
        }
        splash.Close();

        MainForm f = new MainForm();
        if (SelfShot) RunSelfShot(f);
        Application.Run(f);
        return 0;
    }

    static void RunSelfShot(MainForm f)
    {
        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 1500;
        t.Tick += delegate
        {
            t.Stop();
            try
            {
                f.Activate();
                Rectangle r = new Rectangle(f.PointToScreen(Point.Empty), f.Size);
                using (Bitmap bmp = new Bitmap(r.Width, r.Height))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                    bmp.Save(Path.Combine(Furina.baseDir, "selfshot.png"));
                }
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("form=" + f.Size);
                for (int i = 0; i < f.entranceCards.Count; i++)
                {
                    CardPanel c = f.entranceCards[i];
                    sb.AppendLine("card" + i + " bounds=" + c.Bounds + " children=" + c.Controls.Count);
                    foreach (Control ch in c.Controls)
                        sb.AppendLine("  child " + ch.GetType().Name + " bounds=" + ch.Bounds + " children=" + ch.Controls.Count);
                }
                File.WriteAllText(Path.Combine(Furina.baseDir, "selfshot_debug.txt"), sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { try { File.WriteAllText(Path.Combine(Furina.baseDir, "selfshot_debug.txt"), ex.ToString()); } catch { } }
            Application.Exit();
        };
        t.Start();
    }

    static bool ConfigComplete(Furina.Cfg c)
    {
        return !string.IsNullOrEmpty(c.AiriExe) && File.Exists(c.AiriExe)
            && !string.IsNullOrEmpty(c.TtsBat) && File.Exists(Furina.ResolvePath(c.TtsBat));
    }

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
// 主题令牌：枫丹夜海
// ---------------------------------------------------------------
static class Theme
{
    // 荒形态 · 深海军蓝（运行态）
    public static readonly Color BgTop = Color.FromArgb(15, 23, 52);      // 夜空
    public static readonly Color BgBottom = Color.FromArgb(9, 14, 32);    // 深海
    public static readonly Color Panel = Color.FromArgb(20, 31, 64);      // 卡片
    public static readonly Color PanelBorder = Color.FromArgb(42, 60, 100);  // 发丝边
    public static readonly Color FieldBg = Color.FromArgb(14, 24, 56);    // 输入框
    public static readonly Color Ink = Color.FromArgb(234, 242, 251);     // 主文字
    public static readonly Color Muted = Color.FromArgb(160, 180, 210);   // 次文字
    public static readonly Color Aqua = Color.FromArgb(103, 232, 249);    // 湖水青（主强调）
    public static readonly Color AquaDeep = Color.FromArgb(56, 189, 248); // 深青
    public static readonly Color Gold = Color.FromArgb(231, 198, 107);    // 金饰（仅点饰）
    public static readonly Color Ok = Color.FromArgb(110, 231, 183);      // 运行
    public static readonly Color Err = Color.FromArgb(248, 113, 113);     // 异常
    public static readonly Color Dim = Color.FromArgb(70, 88, 128);       // 未启用灰蓝

    // 芒形态 · 浅湖蓝（启动画面）
    public static readonly Color SplashTop = Color.FromArgb(247, 251, 255);
    public static readonly Color SplashBottom = Color.FromArgb(219, 238, 250);

    // 字阶
    public static readonly Font FontBrand = new Font("幼圆", 15, FontStyle.Bold);
    public static readonly Font FontTitle = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
    public static readonly Font FontBody = new Font("Microsoft YaHei UI", 9.5f);
    public static readonly Font FontSmall = new Font("Microsoft YaHei UI", 8.5f);
    public static readonly Font FontLog = new Font("Consolas", 9.5f);

    // 动效：时长三档 + 指数缓出（Apple 风格）
    public const int DurMicro = 120, DurShort = 220, DurLong = 420;

    // DPI 缩放系数（96dpi 基准）：像素值统一走 Px()；字体为 pt 单位已自带 DPI 感知
    public static float S = 1f;

    public static int Px(int v)
    {
        return (int)Math.Round(v * S);
    }

    public static double EaseOut(double t)   // cubic-bezier(0.16,1,0.3,1) 近似
    {
        double u = 1 - Math.Min(1, Math.Max(0, t));
        return 1 - u * u * u;
    }

    public static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Min(1, Math.Max(0, t));
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    public static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        GraphicsPath p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// ---------------------------------------------------------------
// 自定义控件：胶囊按钮（hover 抬升 + 色彩缓动 + 按压下沉）
// ---------------------------------------------------------------
class CapsuleButton : Control
{
    public Color BaseColor = Theme.Panel;
    public Color HoverColor = Theme.Panel;
    public Color AccentColor = Color.Empty;   // 非空 = 描边强调
    double hoverT, pressT;
    System.Windows.Forms.Timer anim;
    bool hoverGoal, pressGoal;

    public CapsuleButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.FontBody;
        ForeColor = Theme.Ink;
        BackColor = Theme.Panel;   // 圆角外角部融入卡片底色
        // 默认色比面板亮一档；hover 渐亮向湖水青
        BaseColor = Color.FromArgb(32, 48, 88);
        HoverColor = Theme.Lerp(Color.FromArgb(32, 48, 88), Theme.AquaDeep, 0.4);
        anim = new System.Windows.Forms.Timer();
        anim.Interval = 16;
        anim.Tick += delegate
        {
            double ht = hoverGoal ? 1 : 0, pt = pressGoal ? 1 : 0;
            bool dirty = false;
            double nh = hoverT + (ht - hoverT) * 0.28;
            double np = pressT + (pt - pressT) * 0.4;
            if (Math.Abs(nh - hoverT) > 0.002) { hoverT = nh; dirty = true; } else hoverT = ht;
            if (Math.Abs(np - pressT) > 0.002) { pressT = np; dirty = true; } else pressT = pt;
            if (dirty) Invalidate();
            else anim.Stop();
        };
    }

    protected override void OnMouseEnter(EventArgs e) { hoverGoal = true; anim.Start(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hoverGoal = false; pressGoal = false; anim.Start(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressGoal = true; anim.Start(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressGoal = false; anim.Start(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        int lift = (int)Math.Round(-1.5 * hoverT + 1.5 * pressT);
        Color bg = Enabled ? Theme.Lerp(BaseColor, HoverColor, hoverT) : Theme.Dim;
        using (GraphicsPath path = Theme.RoundRect(r, 10))
        using (SolidBrush br = new SolidBrush(bg))
        {
            g.FillPath(br, path);
            Color border = AccentColor != Color.Empty
                ? Theme.Lerp(AccentColor, Color.White, pressT * 0.3)
                : Theme.Lerp(Theme.PanelBorder, Theme.Aqua, hoverT * 0.7);
            using (Pen pen = new Pen(border, AccentColor != Color.Empty ? 1.6f : 1f))
                g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(0, lift, Width, Height), Enabled ? ForeColor : Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

// ---------------------------------------------------------------
// 自定义控件：状态胶囊（圆点 + 文字，色彩随状态平滑过渡）
// ---------------------------------------------------------------
class StatusPill : Control
{
    Color target = Theme.Dim;
    double blend; // 0=当前色, 1=目标色
    Color current = Theme.Dim;
    System.Windows.Forms.Timer anim;
    string state = "--";

    public StatusPill(string name)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.FontSmall;
        Size = new Size(Theme.Px(104), Theme.Px(24));
        BackColor = Theme.Panel;   // 圆角外角部与卡片底色一致，不露出系统灰
        Name_ = name;
        anim = new System.Windows.Forms.Timer();
        anim.Interval = 16;
        anim.Tick += delegate
        {
            double nb = blend + 0.25;
            if (nb >= 1) { blend = 1; anim.Stop(); } else blend = nb;
            Invalidate();
        };
    }

    public string Name_;

    public void SetState(string text, Color color)
    {
        if (state == text) return;
        state = text;
        current = Color.FromArgb(current.R, current.G, current.B);
        blend = 0;
        target = color;
        anim.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color dot = Theme.Lerp(current, target, Theme.EaseOut(blend));
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = Theme.RoundRect(r, Height / 2))
        {
            using (SolidBrush br = new SolidBrush(Color.FromArgb(90, Theme.Lerp(Theme.Panel, dot, 0.25))))
                g.FillPath(br, path);
            using (Pen pen = new Pen(Color.FromArgb(120, dot), 1f))
                g.DrawPath(pen, path);
        }
        using (SolidBrush br = new SolidBrush(dot))
            g.FillEllipse(br, 9, Height / 2 - 4, 8, 8);
        TextRenderer.DrawText(g, Name_ + " " + state, Font,
            new Rectangle(22, 0, Width - 24, Height),
            Theme.Lerp(Theme.Muted, Theme.Ink, Theme.EaseOut(blend) * 0.9 + 0.1),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}

// ---------------------------------------------------------------
// 自定义控件：圆角卡片（面板）——发丝边 + 顶部微高光
// ---------------------------------------------------------------
class CardPanel : Panel
{
    public CardPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Padding = new Padding(Theme.Px(14), Theme.Px(10), Theme.Px(14), Theme.Px(12));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = Theme.RoundRect(r, 14))
        {
            using (SolidBrush br = new SolidBrush(Theme.Panel))
                g.FillPath(br, path);
            using (Pen pen = new Pen(Theme.PanelBorder, 1f))
                g.DrawPath(pen, path);
            using (Pen hi = new Pen(Color.FromArgb(26, 255, 255, 255), 1f))
                g.DrawLine(hi, 14, 1, Width - 14, 1);
        }
        base.OnPaint(e);
    }
}

// ---------------------------------------------------------------
// 启动画面（芒形态 · 浅湖蓝）
// ---------------------------------------------------------------

class CharLabel : Label
{
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (LinearGradientBrush br = new LinearGradientBrush(
            new Rectangle(0, 0, Width, Height),
            Color.FromArgb(196, 233, 250), Color.FromArgb(30, 100, 190), LinearGradientMode.Vertical))
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
    readonly int baseY = Theme.Px(70);
    readonly int fallPx = Theme.Px(80);
    readonly int[] positions = new int[MSG.Length];
    readonly bool[] shown = new bool[MSG.Length];

    volatile bool stopRender;
    Thread renderThread;
    double cycleStart;
    int refreshHz = 60;

    const double CHAR_DELAY = 0.085, CHAR_DUR = 0.95, HOLD = 0.9;

    public SplashForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(Theme.Px(560), Theme.Px(240));
        DoubleBuffered = true;

        Label caption = new Label();
        caption.Text = "furina";
        caption.Font = Theme.FontBrand;
        caption.ForeColor = Theme.Gold;
        caption.AutoSize = true;
        caption.BackColor = Color.Transparent;
        caption.Location = new Point((Width - caption.PreferredWidth) / 2, Theme.Px(24));
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
                chars[i].Location = new Point(x, baseY - fallPx);
                chars[i].Visible = false;
                Controls.Add(chars[i]);
                x += widths[i];
            }
        }

        ProgressBar bar = new ProgressBar();
        bar.Style = ProgressBarStyle.Marquee;
        bar.Size = new Size(Theme.Px(400), Theme.Px(8));
        bar.Location = new Point((Width - bar.Width) / 2, Theme.Px(170));
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
            ClientRectangle, Theme.SplashTop, Theme.SplashBottom, LinearGradientMode.Vertical))
        {
            e.Graphics.FillRectangle(br, ClientRectangle);
        }
    }

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
        if (now - cycleStart > cycleLen) cycleStart = now;
        double t = now - cycleStart;
        for (int i = 0; i < chars.Length; i++)
        {
            double p = (t - i * CHAR_DELAY) / CHAR_DUR;
            if (p < 0)
            {
                shown[i] = false;
                positions[i] = baseY - fallPx;
            }
            else if (p >= 1)
            {
                shown[i] = true;
                positions[i] = baseY;
            }
            else
            {
                shown[i] = true;
                positions[i] = baseY - (int)((1 - EaseOutBounce(p)) * fallPx);
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
// 教程窗口（夜海主题）
// ---------------------------------------------------------------

class TutorialForm : Form
{
    public TutorialForm()
    {
        Text = "使用教程";
        Width = 820;
        Height = 600;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.BgBottom;
        Font = Theme.FontBody;

        ListBox nav = new ListBox();
        nav.Dock = DockStyle.Left;
        nav.Width = 210;
        nav.Font = new Font("Microsoft YaHei UI", 10F);
        nav.BorderStyle = BorderStyle.None;
        nav.BackColor = Theme.Panel;
        nav.ForeColor = Theme.Ink;
        foreach (string t in TutorialText.Titles) nav.Items.Add(t);

        RichTextBox body = new RichTextBox();
        body.Dock = DockStyle.Fill;
        body.ReadOnly = true;
        body.BackColor = Theme.BgBottom;
        body.ForeColor = Theme.Ink;
        body.BorderStyle = BorderStyle.None;
        body.DetectUrls = false;
        try
        {
            body.Rtf = MiniMd.ToRtf(TutorialText.FullMarkdown);
        }
        catch
        {
            body.Text = TutorialText.FullMarkdown;
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
// 主界面（荒形态 · 深海军蓝）
// ---------------------------------------------------------------

class MainForm : Form
{
    TextBox txtAiri, txtTtsBat, txtNewApiExe, txtNewApiDir, txtNewApiProbe,
            txtSoVitsProbe, txtAdapterProbe, txtSession;
    Label markAiri, markTts, markNewApi, markNewApiDir, markNewApiProbe, markSoVits, markAdapter;
    CheckBox chkAutoExit, chkKeep;
    CapsuleButton btnStart, btnStop;
    StatusPill pillNewApi, pillGuard, pillSoVits, pillAdapter, pillAiri;
    TextBox txtLog;
    System.Windows.Forms.Timer statusTimer;
    System.Windows.Forms.Timer validateTimer;
    ToolTip toolTip = new ToolTip();
    volatile bool probing;

    Thread runThread;
    internal List<CardPanel> entranceCards = new List<CardPanel>();
    System.Windows.Forms.Timer entranceTimer;
    int entranceTick;

    public MainForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;   // PerMonitorV2 下按系统 DPI 整体缩放窗体与控件
        Text = "furina · 枫丹夜海";
        Width = 940;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(860, 660);
        Font = Theme.FontBody;
        DoubleBuffered = true;

        BuildLayout();
        ApplyDpiScale();

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

        if (Program.AutoStarted && Program.AutoRunThread != null)
        {
            AttachRun(Program.AutoRunThread);
            AppendLog("检测到完整配置，已自动启动全部组件。");
        }
        else
        {
            AppendLog("配置已载入。浏览选择你的组件路径后点「启动」。");
        }

        Shown += delegate
        {
            PlayEntrance();
            // 清掉首个输入框获得焦点时的全选高亮
            Ui(delegate
            {
                foreach (TextBox tb in new TextBox[] { txtAiri, txtTtsBat })
                {
                    tb.SelectionStart = tb.Text.Length;
                    tb.SelectionLength = 0;
                }
            });
        };
    }

    // 窗体与最小尺寸按 DPI 系数放大；内部固定像素统一走 Theme.Px
    void ApplyDpiScale()
    {
        try
        {
            ClientSize = new Size(Theme.Px(940 - 16), Theme.Px(760 - 40));
            MinimumSize = new Size(Theme.Px(860), Theme.Px(660));
        }
        catch { }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using (LinearGradientBrush br = new LinearGradientBrush(
            ClientRectangle, Theme.BgTop, Theme.BgBottom, LinearGradientMode.Vertical))
        {
            e.Graphics.FillRectangle(br, ClientRectangle);
        }
    }

    // ---------------------------------------------------------------
    // 布局
    // ---------------------------------------------------------------

    void BuildLayout()
    {
        // 顶部品牌条
        Panel header = new Panel();
        header.Dock = DockStyle.Top;
        header.Height = Theme.Px(56);
        header.BackColor = Color.Transparent;
        Label brand = new Label();
        brand.Text = "furina";
        brand.Font = Theme.FontBrand;
        brand.ForeColor = Theme.Aqua;
        brand.AutoSize = true;
        brand.BackColor = Color.Transparent;
        brand.Location = new Point(Theme.Px(18), Theme.Px(12));
        Label tagline = new Label();
        tagline.Text = "桌面上的她 · 一键拉起的全部世界";
        tagline.Font = Theme.FontSmall;
        tagline.ForeColor = Theme.Muted;
        tagline.AutoSize = true;
        tagline.BackColor = Color.Transparent;
        tagline.Location = new Point(brand.Right + Theme.Px(12), Theme.Px(20));
        header.Controls.Add(brand);
        header.Controls.Add(tagline);
        header.Resize += delegate { tagline.Left = brand.Right + 12; };
        Controls.Add(header);

        // 主体区：单列 TableLayout（卡片 Dock=Fill 占满宽度；日志行吃掉剩余高度）
        TableLayoutPanel body = new TableLayoutPanel();
        body.Dock = DockStyle.Fill;
        body.ColumnCount = 1;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowCount = 4;
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.BackColor = Color.Transparent;
        body.Padding = new Padding(Theme.Px(16), Theme.Px(6), Theme.Px(16), Theme.Px(10));

        CardPanel cardPaths = BuildPathsCard();
        CardPanel cardBehavior = BuildBehaviorCard();
        CardPanel cardRun = BuildRunCard();
        CardPanel cardLog = BuildLogCard();

        cardPaths.Dock = DockStyle.Fill; cardPaths.AutoSize = true;
        cardBehavior.Dock = DockStyle.Fill; cardBehavior.AutoSize = true;
        cardRun.Dock = DockStyle.Fill; cardRun.AutoSize = true;
        cardLog.Dock = DockStyle.Fill;

        body.Controls.Add(cardPaths, 0, 0);
        body.Controls.Add(cardBehavior, 0, 1);
        body.Controls.Add(cardRun, 0, 2);
        body.Controls.Add(cardLog, 0, 3);
        // 停靠顺序：先 Add body（Fill），后 Add header（Top 先行停靠到顶部）
        Controls.Add(body);
        Controls.Add(header);

        entranceCards.Add(cardPaths);
        entranceCards.Add(cardBehavior);
        entranceCards.Add(cardRun);
        entranceCards.Add(cardLog);
    }

    Label CardTitle(string text)
    {
        Label l = new Label();
        l.Text = text;
        l.Font = Theme.FontTitle;
        l.ForeColor = Theme.Aqua;
        l.AutoSize = true;
        l.BackColor = Color.Transparent;
        l.Margin = new Padding(0, 2, 0, 8);
        return l;
    }

    CardPanel BuildPathsCard()
    {
        CardPanel card = new CardPanel();
        card.Dock = DockStyle.Top;
        card.AutoSize = true;

        FlowLayoutPanel col = new FlowLayoutPanel();
        col.Dock = DockStyle.Top;
        col.AutoSize = true;
        col.FlowDirection = FlowDirection.TopDown;
        col.WrapContents = false;
        col.BackColor = Color.Transparent;
        col.Controls.Add(CardTitle("组件路径"));

        TableLayoutPanel grid = new TableLayoutPanel();
        grid.Dock = DockStyle.Top;
        grid.AutoSize = true;
        grid.ColumnCount = 4;
        // 全部百分比列：随 DPI 缩放与窗口宽度同比例伸缩（固定像素列在 250% DPI 下
        // 会把中文标签挤换行、行高膨胀到 161px/行——实测踩过）
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 4));
        grid.BackColor = Color.Transparent;

        int row = 0;
        txtAiri = AddRow(grid, row, "AIRI 程序", delegate
        {
            string p = PickFile("选择 AIRI 主程序 (airi.exe)", "可执行文件|*.exe");
            if (p != null) txtAiri.Text = p;
        }, out markAiri);
        row++;
        txtTtsBat = AddRow(grid, row, "语音服务脚本", delegate
        {
            string p = PickFile("选择语音服务启动脚本（bat）", "批处理|*.bat;*.cmd");
            if (p != null) txtTtsBat.Text = p;
        }, out markTts);
        row++;
        txtNewApiExe = AddRow(grid, row, "NewAPI 程序（可空）", delegate
        {
            string p = PickFile("选择 NewAPI 主程序（不需要网关可跳过）", "可执行文件|*.exe");
            if (p != null) txtNewApiExe.Text = p;
        }, out markNewApi);
        row++;
        txtNewApiDir = AddRow(grid, row, "NewAPI 数据目录", delegate
        {
            string p = PickFolder("选择 NewAPI 数据目录（含 one-api.db）");
            if (p != null) txtNewApiDir.Text = p;
        }, out markNewApiDir);
        row++;
        txtNewApiProbe = AddRow(grid, row, "NewAPI 地址", null, out markNewApiProbe);
        row++;
        txtSoVitsProbe = AddRow(grid, row, "语音服务地址", null, out markSoVits);
        row++;
        txtAdapterProbe = AddRow(grid, row, "语音适配器地址", null, out markAdapter);
        row++;
        Label markSession;
        txtSession = AddRow(grid, row, "网关密钥（空=自动生成）", null, out markSession);
        row++;

        col.Controls.Add(grid);
        card.Controls.Add(col);
        return card;
    }

    CardPanel BuildBehaviorCard()
    {
        CardPanel card = new CardPanel();
        card.Dock = DockStyle.Top;
        card.AutoSize = true;
        FlowLayoutPanel col = new FlowLayoutPanel();
        col.Dock = DockStyle.Top;
        col.AutoSize = true;
        col.FlowDirection = FlowDirection.TopDown;
        col.WrapContents = false;
        col.BackColor = Color.Transparent;
        col.Controls.Add(CardTitle("行为"));

        FlowLayoutPanel flow = new FlowLayoutPanel();
        flow.Dock = DockStyle.Top;
        flow.AutoSize = true;
        flow.WrapContents = false;
        flow.BackColor = Color.Transparent;

        chkAutoExit = DarkCheck("AIRI 关闭时自动退出并回收");
        chkKeep = DarkCheck("退出时保留服务运行");
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
        col.Controls.Add(flow);
        card.Controls.Add(col);
        return card;
    }

    CheckBox DarkCheck(string text)
    {
        CheckBox c = new CheckBox();
        c.Text = text;
        c.AutoSize = true;
        c.Font = Theme.FontBody;
        c.ForeColor = Theme.Ink;
        c.BackColor = Color.Transparent;
        c.FlatStyle = FlatStyle.Flat;
        c.FlatAppearance.CheckedBackColor = Theme.Panel;
        c.Margin = new Padding(0, 2, 20, 2);
        return c;
    }

    CardPanel BuildRunCard()
    {
        CardPanel card = new CardPanel();
        card.Dock = DockStyle.Top;
        card.AutoSize = true;
        FlowLayoutPanel col = new FlowLayoutPanel();
        col.Dock = DockStyle.Top;
        col.AutoSize = true;
        col.FlowDirection = FlowDirection.TopDown;
        col.WrapContents = false;
        col.BackColor = Color.Transparent;
        col.Controls.Add(CardTitle("运行"));

        FlowLayoutPanel btnFlow = new FlowLayoutPanel();
        btnFlow.Dock = DockStyle.Top;
        btnFlow.AutoSize = true;
        btnFlow.WrapContents = false;
        btnFlow.BackColor = Color.Transparent;

        CapsuleButton btnSave = new CapsuleButton();
        btnSave.Text = "保存配置";
        btnSave.Size = new Size(Theme.Px(104), Theme.Px(30));
        btnSave.Click += delegate { FieldsToCfg(); Furina.SaveIni(); Furina.Log("配置已保存到 furina.ini"); };
        btnStart = new CapsuleButton();
        btnStart.Text = "▶ 启动";
        btnStart.Size = new Size(Theme.Px(104), Theme.Px(30));
        btnStart.AccentColor = Theme.Ok;
        btnStart.Click += delegate { StartRun(); };
        btnStop = new CapsuleButton();
        btnStop.Text = "■ 停止";
        btnStop.Size = new Size(Theme.Px(96), Theme.Px(30));
        btnStop.AccentColor = Theme.Err;
        btnStop.Enabled = false;
        btnStop.Click += delegate { Furina.RequestStop(); };
        CapsuleButton btnTutorial = new CapsuleButton();
        btnTutorial.Text = "教程";
        btnTutorial.Size = new Size(Theme.Px(88), Theme.Px(30));
        btnTutorial.AccentColor = Theme.Gold;
        btnTutorial.Click += delegate { new TutorialForm().Show(this); };

        btnFlow.Controls.Add(btnSave);
        btnFlow.Controls.Add(btnStart);
        btnFlow.Controls.Add(btnStop);
        btnFlow.Controls.Add(btnTutorial);

        FlowLayoutPanel pillFlow = new FlowLayoutPanel();
        pillFlow.Dock = DockStyle.Top;
        pillFlow.AutoSize = true;
        pillFlow.WrapContents = true;
        pillFlow.BackColor = Color.Transparent;
        pillFlow.Margin = new Padding(0, 8, 0, 0);

        pillNewApi = new StatusPill("NewAPI");
        pillGuard = new StatusPill("LLM守卫");
        pillSoVits = new StatusPill("语音服务");
        pillAdapter = new StatusPill("语音适配器");
        pillAiri = new StatusPill("AIRI");
        pillFlow.Controls.Add(pillNewApi);
        pillFlow.Controls.Add(pillGuard);
        pillFlow.Controls.Add(pillSoVits);
        pillFlow.Controls.Add(pillAdapter);
        pillFlow.Controls.Add(pillAiri);

        col.Controls.Add(btnFlow);
        col.Controls.Add(pillFlow);
        card.Controls.Add(col);
        return card;
    }

    CardPanel BuildLogCard()
    {
        CardPanel card = new CardPanel();
        card.Dock = DockStyle.Top;
        card.AutoSize = true;
        FlowLayoutPanel col = new FlowLayoutPanel();
        col.Dock = DockStyle.Top;
        col.AutoSize = true;
        col.FlowDirection = FlowDirection.TopDown;
        col.WrapContents = false;
        col.BackColor = Color.Transparent;
        col.Controls.Add(CardTitle("日志"));

        txtLog = new TextBox();
        txtLog.Multiline = true;
        txtLog.ReadOnly = true;
        txtLog.ScrollBars = ScrollBars.Vertical;
        txtLog.BackColor = Theme.FieldBg;
        txtLog.ForeColor = Color.FromArgb(180, 220, 235);
        txtLog.Font = Theme.FontLog;
        txtLog.BorderStyle = BorderStyle.None;
        txtLog.Dock = DockStyle.Top;
        txtLog.Height = Theme.Px(170);

        col.Controls.Add(txtLog);
        card.Controls.Add(col);
        return card;
    }

    // ---------------------------------------------------------------
    // 入场编排：卡片错峰滑入（transform 域，指数缓出）
    // ---------------------------------------------------------------

    void PlayEntrance()
    {
        // 窗体级淡入：opacity 单属性、指数缓出、~250ms。不做布局级动画——
        // 在 TableLayout 里改 Margin 会每帧触发全量重排，视觉上就是抖动/诡异。
        Opacity = 0;
        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 16;
        double start = Environment.TickCount / 1000.0;
        t.Tick += delegate
        {
            double p = Math.Min(1.0, (Environment.TickCount / 1000.0 - start) / 0.25);
            Opacity = Theme.EaseOut(p);
            if (p >= 1) t.Stop();
        };
        t.Start();
    }

    // ---------------------------------------------------------------
    // 行构建与选择器
    // ---------------------------------------------------------------

    TextBox AddRow(TableLayoutPanel grid, int row, string label, EventHandler onBrowse, out Label mark)
    {
        grid.RowCount = row + 1;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Label lbl = new Label();
        lbl.Text = label;
        lbl.AutoSize = true;
        lbl.Anchor = AnchorStyles.Left;
        lbl.Padding = new Padding(2, 8, 0, 0);
        lbl.Font = Theme.FontBody;
        lbl.ForeColor = Theme.Muted;
        lbl.BackColor = Color.Transparent;
        grid.Controls.Add(lbl, 0, row);

        Panel fieldWrap = new Panel();
        fieldWrap.Dock = DockStyle.Fill;
        fieldWrap.Height = Theme.Px(26);
        fieldWrap.Padding = new Padding(Theme.Px(8), Theme.Px(4), Theme.Px(8), Theme.Px(4));
        fieldWrap.BackColor = Theme.FieldBg;
        fieldWrap.Margin = new Padding(3, 3, 3, 3);
        TextBox tb = new TextBox();
        tb.Dock = DockStyle.Fill;
        tb.BorderStyle = BorderStyle.None;
        tb.BackColor = Theme.FieldBg;
        tb.ForeColor = Theme.Ink;
        tb.Font = Theme.FontBody;
        fieldWrap.Controls.Add(tb);
        tb.GotFocus += delegate { fieldWrap.BackColor = Theme.Lerp(Theme.FieldBg, Theme.AquaDeep, 0.35); };
        tb.LostFocus += delegate { fieldWrap.BackColor = Theme.FieldBg; };
        grid.Controls.Add(fieldWrap, 1, row);

        if (onBrowse != null)
        {
            CapsuleButton btn = new CapsuleButton();
            btn.Text = "浏览";
            btn.Dock = DockStyle.Fill;
            btn.Margin = new Padding(3, 3, 3, 3);
            btn.Click += onBrowse;
            grid.Controls.Add(btn, 2, row);
        }

        mark = new Label();
        mark.Text = "";
        mark.AutoSize = false;
        mark.Size = new Size(Theme.Px(24), Theme.Px(24));
        mark.TextAlign = ContentAlignment.MiddleCenter;
        mark.Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);
        mark.BackColor = Color.Transparent;
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
    // 校验（逻辑同旧版，仅配色换成令牌）
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
        mark.ForeColor = ok.Value ? Theme.Ok : Theme.Err;
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
    // 配置与运行（逻辑同旧版）
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
    // 状态灯
    // ---------------------------------------------------------------

    void RefreshStatusAsync()
    {
        if (probing) return;
        probing = true;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                Furina.ComponentState stNewApi, stSoVits, stAdapter, stGuard;
                if (Furina.NewApiEnabled)
                {
                    stNewApi = Furina.ProbeComponent(Furina.cfg.NewApiProbe);
                    stGuard = Furina.cfg.LlmGuardEnabled
                        ? Furina.ProbeComponent("http://127.0.0.1:" + Furina.cfg.LlmGuardPort + "/health")
                        : Furina.ComponentState.Dead;
                }
                else
                {
                    stNewApi = Furina.ComponentState.Dead;
                    stGuard = Furina.ComponentState.Dead;
                }
                stSoVits = Furina.ProbeComponent(Furina.cfg.SoVitsProbe);
                stAdapter = Furina.ProbeComponent(Furina.cfg.AdapterProbe);
                bool airi = Process.GetProcessesByName("airi").Length > 0;

                Ui(delegate
                {
                    if (Furina.NewApiEnabled)
                        pillNewApi.SetState(PillText(stNewApi), PillColor(stNewApi));
                    else pillNewApi.SetState("未启用", Theme.Dim);
                    if (Furina.NewApiEnabled && Furina.cfg.LlmGuardEnabled)
                        pillGuard.SetState(PillText(stGuard), PillColor(stGuard));
                    else pillGuard.SetState("未启用", Theme.Dim);
                    pillSoVits.SetState(PillText(stSoVits), PillColor(stSoVits));
                    pillAdapter.SetState(PillText(stAdapter), PillColor(stAdapter));
                    pillAiri.SetState(airi ? "运行中" : "未运行", airi ? Theme.Ok : Theme.Err);
                });
            }
            finally
            {
                probing = false;
            }
        });
    }

    static string PillText(Furina.ComponentState st)
    {
        return st == Furina.ComponentState.Alive || st == Furina.ComponentState.Busy ? "运行中" : "未响应";
    }

    static Color PillColor(Furina.ComponentState st)
    {
        return st == Furina.ComponentState.Alive || st == Furina.ComponentState.Busy ? Theme.Ok : Theme.Err;
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
