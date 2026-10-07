// -*- coding: utf-8 -*-
/*
 * furina_gui.cs - 芙宁娜整合启动器图形界面（产品级重制 · 枫丹夜海）
 *
 * 产品形态：左侧图标导航栏 + 五页面（主页/组件/配置/教程/日志），
 * 页面右滑进入切换，导航 hover/选中动效，组件页图标行 + 悬停反馈。
 * 设计基准：AIRI 设置页的产品语言（图标行、hover 反应、丝滑二级页）×
 * 枫丹夜海调色板。令牌集中在 Theme；动效只做 transform/opacity 域。
 *
 * 行为层与前版完全一致：配置校验、行为互斥、自动启动、看门狗对接。
 * 不带参数双击 = GUI；--console/--exit-after=N 控制台；--selfshot 自检截图。
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
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
        {
            Furina.Log("UI 异常（已拦截，不中断运行）: " + e.Exception);
        };
        Furina.InitPaths();

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
        // 逐页截图：home(荒) -> home(芒) -> components -> 组件二级配置 -> guide -> logs
        // 两拍制：本拍截图、下一拍切页（不阻塞 UI 线程）
        string[] order = { "home", "homemang", "components", "compdetail", "guide", "logs" };
        int idx = 0;
        bool captured = false;
        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 600;
        t.Tick += delegate
        {
            if (idx >= order.Length) { t.Stop(); RunAnimShot(f); return; }
            if (!captured)
            {
                try
                {
                    f.SnapNavForShot();
                    f.Activate();
                    f.Refresh();
                    Rectangle r = new Rectangle(f.PointToScreen(Point.Empty), f.Size);
                    using (Bitmap bmp = new Bitmap(r.Width, r.Height))
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                        bmp.Save(Path.Combine(Furina.baseDir, "selfshot_" + order[idx] + ".png"));
                    }
                }
                catch { }
                captured = true;
                return;
            }
            idx++;
            if (idx < order.Length)
            {
                if (order[idx] == "homemang") f.SwitchThemeForShot(true);
                else
                {
                    if (Theme.Mode == UiTheme.Mang) f.SwitchThemeForShot(false);
                    if (order[idx] == "compdetail") f.OpenComponentDetailForShot(0);
                    else f.NavigateInstant(order[idx]);
                }
                captured = false;
            }
        };
        t.Start();
    }

    // 动画过渡残影验证：动画切页的中段与收尾各截一帧
    static void RunAnimShot(MainForm f)
    {
        f.NavigateInstant("home");
        int phase = 0;
        System.Windows.Forms.Timer t2 = new System.Windows.Forms.Timer();
        t2.Interval = 130;
        t2.Tick += delegate
        {
            phase++;
            if (phase == 1) { f.Navigate("components"); return; }
            try
            {
                f.Activate();
                f.Refresh();
                Rectangle r = new Rectangle(f.PointToScreen(Point.Empty), f.Size);
                using (Bitmap bmp = new Bitmap(r.Width, r.Height))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                    bmp.Save(Path.Combine(Furina.baseDir, phase == 2 ? "selfshot_anim_mid.png" : "selfshot_anim_end.png"));
                }
            }
            catch { }
            if (phase >= 3) { t2.Stop(); Application.Exit(); }
        };
        t2.Start();
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
// 主题令牌：枫丹双形态 —— 荒（停止·夜海）/ 芒（运行·晴昼）
// ---------------------------------------------------------------
enum UiTheme { Huang, Mang }

static class Theme
{
    public static UiTheme Mode = UiTheme.Huang;

    public static Color BgTop, BgMid, BgBottom;
    public static Color Panel, PanelBorder, FieldBg, NavBg, NavHover;
    public static Color Ink, Muted, Aqua, AquaDeep, Gold, Ok, Err, Dim;
    public static Color BtnBase, BtnHover, LogInk;

    // 启动画面固定为芒色（"蓝莓小蛋糕在路上"的预告）
    public static readonly Color SplashTop = Color.FromArgb(247, 251, 255);
    public static readonly Color SplashBottom = Color.FromArgb(219, 238, 250);

    public static void SetMode(UiTheme m)
    {
        Mode = m;
        if (m == UiTheme.Mang)
        {
            BgTop = Color.FromArgb(238, 248, 255);
            BgMid = Color.FromArgb(214, 236, 252);
            BgBottom = Color.FromArgb(186, 219, 246);
            Panel = Color.FromArgb(252, 253, 255);
            PanelBorder = Color.FromArgb(164, 198, 230);
            FieldBg = Color.FromArgb(240, 248, 254);
            NavBg = Color.FromArgb(224, 239, 251);
            NavHover = Color.FromArgb(203, 227, 246);
            Ink = Color.FromArgb(24, 48, 86);
            Muted = Color.FromArgb(84, 114, 148);
            Aqua = Color.FromArgb(16, 142, 192);
            AquaDeep = Color.FromArgb(10, 116, 170);
            Gold = Color.FromArgb(198, 154, 46);
            Ok = Color.FromArgb(18, 158, 106);
            Err = Color.FromArgb(216, 78, 78);
            Dim = Color.FromArgb(140, 164, 192);
            BtnBase = Color.FromArgb(255, 255, 255);
            BtnHover = Color.FromArgb(228, 242, 252);
            LogInk = Color.FromArgb(52, 84, 118);
        }
        else
        {
            BgTop = Color.FromArgb(26, 20, 64);
            BgMid = Color.FromArgb(16, 42, 96);
            BgBottom = Color.FromArgb(10, 26, 56);
            Panel = Color.FromArgb(22, 34, 68);
            PanelBorder = Color.FromArgb(48, 68, 110);
            FieldBg = Color.FromArgb(14, 26, 58);
            NavBg = Color.FromArgb(14, 20, 48);
            NavHover = Color.FromArgb(34, 50, 92);
            Ink = Color.FromArgb(236, 243, 252);
            Muted = Color.FromArgb(158, 182, 214);
            Aqua = Color.FromArgb(103, 232, 249);
            AquaDeep = Color.FromArgb(56, 189, 248);
            Gold = Color.FromArgb(231, 198, 107);
            Ok = Color.FromArgb(110, 231, 183);
            Err = Color.FromArgb(248, 113, 113);
            Dim = Color.FromArgb(76, 96, 138);
            BtnBase = Color.FromArgb(32, 48, 88);
            BtnHover = Color.FromArgb(45, 65, 110);
            LogInk = Color.FromArgb(180, 220, 235);
        }
    }

    public static string UiFontName = "Microsoft YaHei UI";
    public static readonly Font FontBrand;
    public static readonly Font FontHero;
    public static readonly Font FontTitle;
    public static readonly Font FontBody;
    public static readonly Font FontSmall;
    public static readonly Font FontLog = new Font("Consolas", 9.5f);
    public const string FontIcons = "Segoe MDL2 Assets";

    public const int DurMicro = 120, DurShort = 220, DurLong = 420;

    static Theme()
    {
        SetMode(UiTheme.Huang);
        FontFamily fam = null;
        string fontFile = LocateUiFont();
        if (fontFile != null)
        {
            try
            {
                AddFontResourceEx(fontFile, FR_PRIVATE, IntPtr.Zero);
                // 注意：PrivateFontCollection 必须常驻存活，
                // 取出 FontFamily 后 dispose 集合会使其原生句柄失效（GetName 抛 ArgumentException）
                fontPfc = new PrivateFontCollection();
                fontPfc.AddFontFile(fontFile);
                if (fontPfc.Families.Length > 0) fam = fontPfc.Families[0];
            }
            catch { fam = null; }
        }
        if (fam == null) fam = new FontFamily("Microsoft YaHei UI");
        UiFontName = fam.Name;
        FontBrand = new Font(fam, 17);
        FontHero = new Font(fam, 26);
        FontTitle = new Font(fam, 10.5f);
        FontBody = new Font(fam, 10f);
        FontSmall = new Font(fam, 9f);
    }

    static PrivateFontCollection fontPfc;

    // 原神同款 UI 字体：本地 fonts/ → 探测原神安装目录的 SDK 字体 → 内嵌得意黑 → 雅黑
    static string LocateUiFont()
    {
        string dir = null;
        try
        {
            dir = Path.Combine(Furina.baseDir, "fonts");
            Directory.CreateDirectory(dir);
            string local = Path.Combine(dir, "SDK_SC_Web.ttf");
            if (File.Exists(local)) return local;
        }
        catch { }
        const string rel = @"YuanShen_Data\StreamingAssets\MiHoYoSDKRes\HttpServerResources\font\zh-cn.ttf";
        try
        {
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed) continue;
                try
                {
                    string p = Path.Combine(d.RootDirectory.FullName, @"miHoYo Launcher\games\Genshin Impact Game", rel);
                    if (File.Exists(p)) return p;
                    p = Path.Combine(d.RootDirectory.FullName, @"Genshin Impact\Genshin Impact Game", rel);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        }
        catch { }
        try
        {
            // 内嵌的得意黑（SIL OFL 1.1，允许捆绑分发）：首次运行释放到 fonts/
            string smiley = Path.Combine(dir, "SmileySans-Oblique.ttf");
            if (!File.Exists(smiley))
            {
                Stream rs = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("SmileySans-Oblique.ttf");
                if (rs != null)
                {
                    using (rs)
                    using (FileStream fs = File.Create(smiley))
                        rs.CopyTo(fs);
                }
            }
            if (File.Exists(smiley)) return smiley;
        }
        catch { }
        return null;
    }

    public static Color CompAccent(int i)
    {
        switch (i)
        {
            case 0: return Gold;   // NewAPI 网关
            case 1: return Mode == UiTheme.Mang ? Color.FromArgb(120, 96, 220) : Color.FromArgb(167, 139, 250);   // LLM 守卫
            case 2: return Aqua;   // 语音服务
            case 3: return Mode == UiTheme.Mang ? Color.FromArgb(20, 160, 120) : Color.FromArgb(110, 231, 183);   // 语音适配器
            default: return Mode == UiTheme.Mang ? Color.FromArgb(214, 92, 138) : Color.FromArgb(244, 150, 190);  // AIRI
        }
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern int AddFontResourceEx(string lpszFilename, uint fl, IntPtr pdv);
    const uint FR_PRIVATE = 0x10;

    public static float S = 1f;
    public static int Px(int v) { return (int)Math.Round(v * S); }

    public static double EaseOut(double t)
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
// 胶囊按钮
// ---------------------------------------------------------------
class CapsuleButton : Control
{
    public Color BaseColor = Color.Empty;   // Empty = 跟随主题 BtnBase
    public Color HoverColor = Color.Empty;  // Empty = 跟随主题 BtnHover
    public Color AccentColor = Color.Empty;
    double hoverT, pressT;
    bool hoverGoal, pressGoal;
    System.Windows.Forms.Timer anim;

    public CapsuleButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.FontBody;
        ForeColor = Theme.Ink;
        BackColor = Theme.Panel;
        anim = new System.Windows.Forms.Timer();
        anim.Interval = 16;
        anim.Tick += delegate
        {
            double ht = hoverGoal ? 1 : 0, pt = pressGoal ? 1 : 0;
            hoverT += (ht - hoverT) * 0.28;
            pressT += (pt - pressT) * 0.4;
            Invalidate();
            if (hoverT == ht && pressT == pt) anim.Stop();
        };
    }

    protected override void OnMouseEnter(EventArgs e) { hoverGoal = true; anim.Start(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hoverGoal = false; pressGoal = false; anim.Start(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressGoal = true; anim.Start(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressGoal = false; anim.Start(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        int lift = (int)Math.Round(-1.5 * hoverT + 1.5 * pressT);
        Color cBase = BaseColor != Color.Empty ? BaseColor : Theme.BtnBase;
        Color cHover = HoverColor != Color.Empty ? HoverColor : Theme.BtnHover;
        Color bg = Enabled ? Theme.Lerp(cBase, cHover, hoverT) : Theme.Dim;
        using (GraphicsPath path = Theme.RoundRect(r, 10))
        {
            // 悬停外发光（两层扩散描边）
            Color glowSrc = AccentColor != Color.Empty ? AccentColor : Theme.Aqua;
            if (Enabled && hoverT > 0.02)
            {
                using (GraphicsPath g1 = Theme.RoundRect(new Rectangle(-2, -2, Width + 3, Height + 3), 12))
                using (Pen pg1 = new Pen(Color.FromArgb((int)(46 * hoverT), glowSrc), 2.4f))
                    g.DrawPath(pg1, g1);
                using (GraphicsPath g2 = Theme.RoundRect(new Rectangle(-4, -4, Width + 7, Height + 7), 14))
                using (Pen pg2 = new Pen(Color.FromArgb((int)(20 * hoverT), glowSrc), 2f))
                    g.DrawPath(pg2, g2);
            }
            using (SolidBrush br = new SolidBrush(bg))
            {
                g.FillPath(br, path);
                Color border = AccentColor != Color.Empty
                    ? Theme.Lerp(AccentColor, Color.White, pressT * 0.3)
                    : Theme.Lerp(Theme.PanelBorder, Theme.Aqua, hoverT * 0.7);
                using (Pen pen = new Pen(border, AccentColor != Color.Empty ? 1.6f : 1f))
                    g.DrawPath(pen, path);
            }
        }
        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(0, lift, Width, Height), Enabled ? ForeColor : Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

// ---------------------------------------------------------------
// 状态胶囊
// ---------------------------------------------------------------
class StatusPill : Control
{
    Color target = Theme.Dim;
    double blend;
    Color current = Theme.Dim;
    System.Windows.Forms.Timer anim;
    string state = "--";
    public string Name_;

    public StatusPill(string name)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.FontSmall;
        Size = new Size(Theme.Px(104), Theme.Px(24));
        BackColor = Theme.Panel;
        Name_ = name;
        anim = new System.Windows.Forms.Timer();
        anim.Interval = 16;
        anim.Tick += delegate
        {
            blend = Math.Min(1, blend + 0.25);
            Invalidate();
            if (blend >= 1) anim.Stop();
        };
    }

    public void SetState(string text, Color color)
    {
        if (state == text) return;
        state = text;
        blend = 0;
        target = color;
        anim.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
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
// 圆角卡片
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
        if (Width <= 0 || Height <= 0) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = Theme.RoundRect(r, Theme.Px(14)))
        {
            using (SolidBrush br = new SolidBrush(Theme.Panel))
                g.FillPath(br, path);
            using (Pen pen = new Pen(Theme.PanelBorder, 1f))
                g.DrawPath(pen, path);
            using (Pen hi = new Pen(Color.FromArgb(26, 255, 255, 255), 1f))
                g.DrawLine(hi, Theme.Px(14), 1, Width - Theme.Px(14), 1);
        }
        base.OnPaint(e);
    }
}

// ---------------------------------------------------------------
// 导航项（图标 + 文字，hover 渐亮 + 选中指示条）
// ---------------------------------------------------------------
class NavItem : Control
{
    public string Glyph;
    public string Title;
    public string Key;
    public bool Selected;
    double hoverT, selectT;
    System.Windows.Forms.Timer anim;

    public NavItem(string glyph, string title, string key)
    {
        Glyph = glyph;
        Title = title;
        Key = key;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.NavBg;
        Size = new Size(Theme.Px(180), Theme.Px(44));
        anim = new System.Windows.Forms.Timer();
        anim.Interval = 16;
        anim.Tick += delegate
        {
            double ht = _hover ? 1 : 0, st = Selected ? 1 : 0;
            hoverT += (ht - hoverT) * 0.3;
            selectT += (st - selectT) * 0.25;
            Invalidate();
            if (Math.Abs(hoverT - ht) < 0.004 && Math.Abs(selectT - st) < 0.004) anim.Stop();
        };
    }

    bool _hover;
    protected override void OnMouseEnter(EventArgs e) { _hover = true; anim.Start(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; anim.Start(); base.OnMouseLeave(e); }

    public void SetSelected(bool v)
    {
        Selected = v;
        anim.Start();
        Invalidate();
    }

    // 自检截图用：直接跳到动画终态，避免渐变计时器饿死在定时器风暴里
    public void Snap()
    {
        hoverT = _hover ? 1 : 0;
        selectT = Selected ? 1 : 0;
        anim.Stop();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color bg = Theme.Lerp(BackColor, Theme.NavHover, Math.Min(1, hoverT + selectT * 0.6));
        using (SolidBrush br = new SolidBrush(bg)) g.FillRectangle(br, ClientRectangle);
        if (selectT > 0.02)
        {
            using (SolidBrush br = new SolidBrush(Color.FromArgb((int)(230 * selectT), Theme.Aqua)))
                g.FillRectangle(br, 0, Theme.Px(10), Theme.Px(3), Height - Theme.Px(20));
        }
        Color iconColor = Theme.Lerp(Theme.Muted, Theme.Aqua, Math.Min(1, hoverT + selectT));
        using (Font f = new Font(Theme.FontIcons, 11))
        using (SolidBrush br = new SolidBrush(iconColor))
        {
            g.DrawString(Glyph, f, br, Theme.Px(16), Height / 2 - Theme.Px(10));
        }
        using (SolidBrush br = new SolidBrush(Theme.Lerp(Theme.Muted, Theme.Ink, Math.Min(1, hoverT + selectT))))
        {
            g.DrawString(Title, Theme.FontBody, br, Theme.Px(48), Height / 2 - Theme.Px(9));
        }
    }
}

// ---------------------------------------------------------------
// 页面容器（可滑动的内容宿主）
// ---------------------------------------------------------------
class Page : Panel
{
    public string Key;
    public Page(string key)
    {
        Key = key;
        // 不透明 BackColor：切换滑动时不会把下层兄弟页面透出来；
        // 渐变在 OnPaintBackground 画，子控件透明背景取到的也是同一张渐变
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.BgBottom;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 窗口最小化等场景 ClientRectangle 为 0×0，渐变画刷会抛 ArgumentException
        if (Width <= 0 || Height <= 0) return;
        Graphics g = e.Graphics;
        // 三段渐变（参考图2 的夜空层次：靛紫→宝蓝→青）
        using (LinearGradientBrush br = new LinearGradientBrush(
            ClientRectangle, Theme.BgTop, Theme.BgBottom, LinearGradientMode.Vertical))
        {
            ColorBlend blend = new ColorBlend(3);
            blend.Colors = new Color[] { Theme.BgTop, Theme.BgMid, Theme.BgBottom };
            blend.Positions = new float[] { 0f, 0.55f, 1f };
            br.InterpolationColors = blend;
            g.FillRectangle(br, ClientRectangle);
        }
        // 静态星点（按页面 Key 播种，重绘稳定；低亮度氛围）
        Random rnd = new Random(Key.GetHashCode());
        int n = Width / 46;
        for (int i = 0; i < n; i++)
        {
            int x = rnd.Next(Width), y = rnd.Next(Height * 3 / 5);
            int sz = rnd.Next(1, 4);
            int a;
            if (Theme.Mode == UiTheme.Huang) a = rnd.Next(18, 60);
            else a = rnd.Next(14, 40);
            Color c = Theme.Mode == UiTheme.Huang
                ? Color.FromArgb(a, 200, 235, 255)
                : Color.FromArgb(a, 255, 255, 255);
            using (SolidBrush sb = new SolidBrush(c))
                g.FillEllipse(sb, x, y, sz, sz);
        }
    }
}

// ---------------------------------------------------------------
// 快照过渡表面：两个页面的位图在单一表面上做位移动画
// （不移动 HWND，16ms 逐帧 Invalidate 也只重画两张图，无残影）
// ---------------------------------------------------------------
class SlideTransition : Control
{
    Bitmap fromBmp, toBmp;
    int dir;
    double t;
    System.Windows.Forms.Timer timer;

    public SlideTransition(Bitmap fromBmp, Bitmap toBmp, int dir, Action<SlideTransition> finished)
    {
        this.fromBmp = fromBmp;
        this.toBmp = toBmp;
        this.dir = dir;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer, true);
        timer = new System.Windows.Forms.Timer();
        timer.Interval = 16;
        double start = Environment.TickCount / 1000.0;
        timer.Tick += delegate
        {
            t = Math.Min(1.0, (Environment.TickCount / 1000.0 - start) / 0.28);
            Invalidate();
            if (t >= 1)
            {
                timer.Stop();
                if (finished != null) finished(this);
            }
        };
    }

    public void Begin() { timer.Start(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        Graphics g = e.Graphics;
        double ez = Theme.EaseOut(t);
        int toX = (int)Math.Round(dir * Width * (1 - ez));
        int fromX = -(int)Math.Round(dir * Width * 0.3 * ez);
        g.DrawImageUnscaled(fromBmp, fromX, 0);
        g.DrawImageUnscaled(toBmp, toX, 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Stop();
            timer.Dispose();
            if (fromBmp != null) fromBmp.Dispose();
            if (toBmp != null) toBmp.Dispose();
        }
        base.Dispose(disposing);
    }
}

// ---------------------------------------------------------------
// 主题切换淡出层：旧界面快照按透明度渐隐，露出新主题
// ---------------------------------------------------------------
class FadeOverlay : Control
{
    Bitmap old;
    double t;
    System.Windows.Forms.Timer timer;

    FadeOverlay(Bitmap oldBmp)
    {
        old = oldBmp;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer, true);
        timer = new System.Windows.Forms.Timer();
        timer.Interval = 16;
        double start = Environment.TickCount / 1000.0;
        timer.Tick += delegate
        {
            t = Math.Min(1.0, (Environment.TickCount / 1000.0 - start) / 0.45);
            Invalidate();
            if (t >= 1)
            {
                timer.Stop();
                if (Parent != null) Parent.Controls.Remove(this);
                Dispose();
            }
        };
    }

    public static void Begin(MainForm f, Bitmap oldBmp)
    {
        FadeOverlay ov = new FadeOverlay(oldBmp);
        ov.Bounds = f.ClientRectangle;
        f.Controls.Add(ov);
        ov.BringToFront();
        ov.timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        System.Drawing.Imaging.ImageAttributes ia = new System.Drawing.Imaging.ImageAttributes();
        System.Drawing.Imaging.ColorMatrix cm = new System.Drawing.Imaging.ColorMatrix();
        cm.Matrix33 = (float)(1 - t);
        ia.SetColorMatrix(cm);
        e.Graphics.DrawImage(old, new Rectangle(0, 0, Width, Height),
            0, 0, old.Width, old.Height, GraphicsUnit.Pixel, ia);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Stop();
            timer.Dispose();
            if (old != null) old.Dispose();
        }
        base.Dispose(disposing);
    }
}

// ---------------------------------------------------------------
// 主页
// ---------------------------------------------------------------
class HomePage : Page
{
    public CapsuleButton BtnMain;
    public CapsuleButton BtnToSettings, BtnToGuide;
    public StatusPill[] Pills = new StatusPill[5];

    System.Windows.Forms.Timer sceneTimer;
    double t0 = Environment.TickCount / 1000.0;
    float parX, parY, parTX, parTY;
    Image silhouette;
    bool running;
    int x0, brandBottom, subBottom, stTitleY, pillsY, hintY;

    public HomePage(MainForm owner) : base("home")
    {
        x0 = Theme.Px(48);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

        Size bs = TextRenderer.MeasureText("芙宁娜", Theme.FontHero);
        brandBottom = Theme.Px(56) + bs.Height;
        subBottom = brandBottom + Theme.Px(8) + TextRenderer.MeasureText("住在", Theme.FontBody).Height;

        int btnTop = subBottom + Theme.Px(28);
        BtnMain = new CapsuleButton();
        BtnMain.Text = "▶ 启动全部组件";
        BtnMain.Size = new Size(Theme.Px(210), Theme.Px(44));
        BtnMain.BaseColor = Theme.Lerp(Theme.AquaDeep, Color.Black, 0.55);
        BtnMain.HoverColor = Theme.Lerp(Theme.AquaDeep, Color.Black, 0.35);
        BtnMain.Location = new Point(x0, btnTop);
        BtnMain.Click += delegate { owner.ToggleRun(); };
        Controls.Add(BtnMain);

        BtnToGuide = new CapsuleButton();
        BtnToGuide.Text = "使用教程";
        BtnToGuide.Size = new Size(Theme.Px(120), Theme.Px(44));
        BtnToGuide.AccentColor = Theme.Gold;
        BtnToGuide.Location = new Point(BtnMain.Right + Theme.Px(14), btnTop);
        BtnToGuide.Click += delegate { owner.Navigate("guide"); };
        Controls.Add(BtnToGuide);

        BtnToSettings = new CapsuleButton();
        BtnToSettings.Text = "组件配置";
        BtnToSettings.Size = new Size(Theme.Px(120), Theme.Px(44));
        BtnToSettings.Location = new Point(BtnToGuide.Right + Theme.Px(14), btnTop);
        BtnToSettings.Click += delegate { owner.Navigate("components"); };
        Controls.Add(BtnToSettings);

        stTitleY = btnTop + Theme.Px(44) + Theme.Px(40);
        pillsY = stTitleY + TextRenderer.MeasureText("全部组件", Theme.FontTitle).Height + Theme.Px(10);

        string[] names = { "NewAPI", "LLM守卫", "语音服务", "语音适配器", "AIRI" };
        for (int i = 0; i < 5; i++)
        {
            Pills[i] = new StatusPill(names[i]);
            Pills[i].Tag = i;
            Pills[i].Location = new Point(x0 + i * (Theme.Px(112)), pillsY);
            Controls.Add(Pills[i]);
            owner.AllPills.Add(Pills[i]);
        }
        hintY = pillsY + Theme.Px(24) + Theme.Px(28);

        try
        {
            Stream rs = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("furina-silhouette.png");
            if (rs != null)
            {
                using (rs)
                using (Bitmap tmp = (Bitmap)Image.FromStream(rs))
                    silhouette = new Bitmap(tmp);
            }
        }
        catch { }

        sceneTimer = new System.Windows.Forms.Timer();
        sceneTimer.Interval = 33;
        sceneTimer.Tick += delegate
        {
            parX += (parTX - parX) * 0.10f;
            parY += (parTY - parY) * 0.10f;
            Invalidate();
        };
    }

    internal void SetRunning(bool v) { running = v; }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) sceneTimer.Start();
        else sceneTimer.Stop();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Width > 0 && Height > 0)
        {
            parTX = (e.X - Width / 2f) / (Width / 2f);
            parTY = (e.Y - Height / 2f) / (Height / 2f);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            sceneTimer.Stop();
            sceneTimer.Dispose();
            if (silhouette != null) silhouette.Dispose();
        }
        base.Dispose(disposing);
    }

    float Par(float depth) { return depth; }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);   // 三段渐变 + 静态星点（Page）
        if (Width <= 0 || Height <= 0) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool mang = Theme.Mode == UiTheme.Mang;
        double t = Environment.TickCount / 1000.0 - t0;
        float W = Width, H = Height;
        float dx = parX * Theme.Px(14), dy = parY * Theme.Px(9);

        // ---- 极光飘带（参考图1 流动缎带，相位漂移）----
        GraphicsState gs1 = g.Save();
        {
            g.TranslateTransform(dx * 0.35f, dy * 0.35f);
            for (int i = 0; i < 3; i++)
            {
                float yBase = H * (0.20f + 0.17f * i) + (float)Math.Sin(t * 0.13 + i * 2.1) * Theme.Px(26);
                Color rc = mang ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(15 + i * 4, Theme.Aqua);
                using (Pen pen = new Pen(rc, Theme.Px(20 + i * 6)))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawBezier(pen,
                        -W * 0.1f, yBase,
                        W * 0.3f, yBase - Theme.Px(60) + (float)Math.Sin(t * 0.21 + i) * Theme.Px(30),
                        W * 0.7f, yBase + Theme.Px(50),
                        W * 1.1f, yBase - Theme.Px(20));
                }
            }
            g.Restore(gs1);
        }

        // ---- 闪烁星尘 ----
        GraphicsState gs2 = g.Save();
        {
            g.TranslateTransform(dx * 0.2f, dy * 0.2f);
            Random sr = new Random(42);
            int n = 42;
            for (int i = 0; i < n; i++)
            {
                float sx = (float)sr.NextDouble() * W;
                float sy = (float)sr.NextDouble() * H * 0.72f;
                float sz = 1f + (float)sr.NextDouble() * 2.4f;
                double sp = 0.5 + sr.NextDouble() * 1.3;
                double ph = sr.NextDouble() * 6.283;
                double a = 0.45 + 0.55 * Math.Sin(t * sp + ph);
                if (a < 0) a = 0;
                Color c = mang
                    ? Color.FromArgb((int)(90 * a), 255, 255, 255)
                    : Color.FromArgb((int)(140 * a), 200, 235, 255);
                using (SolidBrush sb = new SolidBrush(c))
                    g.FillEllipse(sb, sx, sy, sz * Theme.S, sz * Theme.S);
            }
            g.Restore(gs2);
        }

        // ---- 流星（仅荒夜，约 9 秒一颗）----
        if (!mang)
        {
            double p = ((t + 3.7) % 9.0) / 1.1;
            if (p < 1.0)
            {
                GraphicsState gs3 = g.Save();
                {
                    g.TranslateTransform(dx * 0.25f, dy * 0.25f);
                    float mx0 = W * 0.72f, my0 = H * 0.14f;
                    float mx1 = W * 0.30f, my1 = H * 0.40f;
                    float px = mx0 + (mx1 - mx0) * (float)p, py = my0 + (my1 - my0) * (float)p;
                    double a = Math.Sin(Math.PI * p);
                    float len = Theme.Px(90);
                    float ux = (mx1 - mx0), uy = (my1 - my0);
                    float ul = (float)Math.Sqrt(ux * ux + uy * uy);
                    ux /= ul; uy /= ul;
                    using (Pen glow = new Pen(Color.FromArgb((int)(70 * a), Theme.Aqua), Theme.Px(4)))
                    using (Pen core = new Pen(Color.FromArgb((int)(220 * a), 240, 250, 255), Theme.Px(2)))
                    {
                        glow.StartCap = LineCap.Round; glow.EndCap = LineCap.Round;
                        core.StartCap = LineCap.Round; core.EndCap = LineCap.Round;
                        g.DrawLine(glow, px, py, px - ux * len, py - uy * len);
                        g.DrawLine(core, px, py, px - ux * len * 0.7f, py - uy * len * 0.7f);
                    }
                    g.Restore(gs3);
                }
            }
        }

        // ---- 上升气泡（荒）/ 光尘（芒）----
        GraphicsState gs4 = g.Save();
        {
            g.TranslateTransform(dx * 0.45f, dy * 0.45f);
            Random br = new Random(7);
            for (int i = 0; i < 16; i++)
            {
                float bx = (float)br.NextDouble() * W;
                float sz = Theme.Px(2) + (float)br.NextDouble() * Theme.Px(4);
                double speed = H / (9 + br.NextDouble() * 7);
                double off = br.NextDouble() * H;
                double yRaw = H + 60 - ((t * speed + off) % (H + 120));
                double prog = 1 - (yRaw + 60) / (H + 120);
                double a = Math.Sin(Math.PI * Math.Min(1, Math.Max(0, prog * 1.15)));
                float wx = bx + (float)Math.Sin(t * 1.3 + i * 1.7) * Theme.Px(5);
                if (mang)
                {
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb((int)(70 * a), 255, 244, 210)))
                        g.FillEllipse(sb, wx, (float)yRaw, sz, sz);
                }
                else
                {
                    using (Pen pen = new Pen(Color.FromArgb((int)(80 * a), Theme.Aqua), 1.2f))
                        g.DrawEllipse(pen, wx, (float)yRaw, sz, sz);
                }
            }
            g.Restore(gs4);
        }

        // ---- 底部发光植物剪影（角落框景）----
        GraphicsState gs5 = g.Save();
        {
            g.TranslateTransform(dx * 0.7f, dy * 0.7f);
            DrawPlants(g, mang, 0, H, 1);
            DrawPlants(g, mang, W, H, -1);
            g.Restore(gs5);
        }

        // ---- 剪影水印（右下，用户素材）----
        if (silhouette != null)
        {
            GraphicsState gs6 = g.Save();
            {
                g.TranslateTransform(dx * 0.3f, dy * 0.3f);
                float sh = H * 0.66f;
                float sw = sh * silhouette.Width / silhouette.Height;
                RectangleF dest = new RectangleF(W - sw + Theme.Px(30), H - sh + Theme.Px(26), sw, sh);
                System.Drawing.Imaging.ImageAttributes ia = new System.Drawing.Imaging.ImageAttributes();
                System.Drawing.Imaging.ColorMatrix cm = new System.Drawing.Imaging.ColorMatrix();
                cm.Matrix33 = mang ? 0.10f : 0.16f;
                ia.SetColorMatrix(cm);
                g.DrawImage(silhouette, new Rectangle((int)dest.X, (int)dest.Y, (int)dest.Width, (int)dest.Height), 0, 0, silhouette.Width, silhouette.Height, GraphicsUnit.Pixel, ia);
                g.Restore(gs6);
            }
        }

        // ---- 底部水波（动画相位）----
        GraphicsState gs7 = g.Save();
        {
            g.TranslateTransform(dx * 0.55f, dy * 0.55f);
            float wob1 = (float)Math.Sin(t * 0.5) * Theme.Px(18);
            float wob2 = (float)Math.Sin(t * 0.33 + 1.7) * Theme.Px(22);
            Color wc1 = mang ? Color.FromArgb(34, Theme.AquaDeep) : Color.FromArgb(30, Theme.Aqua);
            Color wc2 = mang ? Color.FromArgb(26, Theme.AquaDeep) : Color.FromArgb(20, Theme.Aqua);
            using (Pen p1 = new Pen(wc1, 2.5f))
            using (Pen p2 = new Pen(wc2, 2f))
            {
                g.DrawBezier(p1, -40, H - Theme.Px(70),
                    Width / 3, H - Theme.Px(150) + wob1,
                    Width * 2 / 3, H - Theme.Px(10) - wob1,
                    Width + 40, H - Theme.Px(90));
                g.DrawBezier(p2, -40, H - Theme.Px(30),
                    Width / 3, H - Theme.Px(110) + wob2,
                    Width * 2 / 3, H + Theme.Px(30) - wob2,
                    Width + 40, H - Theme.Px(50));
            }
            g.Restore(gs7);
        }

        // ---- 主按钮呼吸光环 ----
        if (BtnMain != null && BtnMain.Visible && BtnMain.Enabled)
        {
            double pulse = 0.5 + 0.5 * Math.Sin(t * 1.6);
            Color glowC = running ? Theme.Err : Theme.Aqua;
            int ga = running ? 46 : (int)(26 + 34 * pulse);
            using (GraphicsPath gp = new GraphicsPath())
            {
                Rectangle br = BtnMain.Bounds;
                gp.AddEllipse(br.Left - Theme.Px(10), br.Top - Theme.Px(10),
                    br.Width + Theme.Px(20), br.Height + Theme.Px(20));
                using (PathGradientBrush pgb = new PathGradientBrush(gp))
                {
                    pgb.CenterColor = Color.FromArgb(ga, glowC);
                    pgb.SurroundColors = new Color[] { Color.FromArgb(0, glowC) };
                    g.FillPath(pgb, gp);
                }
            }
        }

        // ---- 文案（随场景每帧重绘，无透明标签撕裂）----
        int tx = x0, ty = Theme.Px(56);
        using (LinearGradientBrush brand = new LinearGradientBrush(
            new Rectangle(tx, ty, 10, brandBottom - ty),
            mang ? Color.FromArgb(20, 110, 200) : Color.FromArgb(240, 250, 255),
            mang ? Color.FromArgb(60, 170, 230) : Theme.Aqua,
            LinearGradientMode.Vertical))
        {
            g.DrawString("芙宁娜", Theme.FontHero, brand, tx, ty);
        }
        using (SolidBrush sb = new SolidBrush(Theme.Muted))
            g.DrawString("住在桌面上的她 · 一键拉起的全部世界", Theme.FontBody, sb, tx + 2, brandBottom + Theme.Px(8));
        using (SolidBrush sb = new SolidBrush(Theme.Aqua))
            g.DrawString("全部组件", Theme.FontTitle, sb, tx, stTitleY);
        using (SolidBrush sb = new SolidBrush(Theme.Dim))
            g.DrawString("双击启动器即可使用：配置完整时将自动启动全部组件。", Theme.FontSmall, sb, tx, hintY);
    }

    // 角落发光植物（参考图2/图3 的框景叶片）
    void DrawPlants(Graphics g, bool mang, float baseX, float baseY, int dir)
    {
        Random pr = new Random(dir > 0 ? 11 : 23);
        Color fill = mang ? Color.FromArgb(110, 120, 185, 205) : Color.FromArgb(150, 10, 52, 74);
        Color rim = mang ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(56, Theme.Aqua);
        for (int i = 0; i < 5; i++)
        {
            float bx = baseX + dir * (Theme.Px(6) + i * Theme.Px(14));
            float hgt = Theme.Px(46) + (float)pr.NextDouble() * Theme.Px(60);
            float bend = dir * (Theme.Px(10) + (float)pr.NextDouble() * Theme.Px(22));
            using (GraphicsPath leaf = new GraphicsPath())
            {
                leaf.AddBezier(bx, baseY + 4, bx + bend * 0.3f, baseY - hgt * 0.5f,
                    bx + bend, baseY - hgt * 0.8f, bx + bend * 1.15f, baseY - hgt);
                leaf.AddBezier(bx + bend * 1.15f, baseY - hgt, bx + bend * 0.9f, baseY - hgt * 0.72f,
                    bx + bend * 0.55f + Theme.Px(5), baseY - hgt * 0.42f, bx + dir * Theme.Px(7), baseY + 4);
                using (SolidBrush fb = new SolidBrush(fill))
                    g.FillPath(fb, leaf);
                using (Pen rp = new Pen(rim, 1f))
                    g.DrawPath(rp, leaf);
            }
        }
    }
}

// ---------------------------------------------------------------
// 组件页（图标行 + 悬停反馈）
// ---------------------------------------------------------------
class ComponentRow : Control
{
    public string Glyph, Title, Desc;
    public StatusPill Pill;
    public Color Accent;
    double hoverT;
    System.Windows.Forms.Timer anim;

    public ComponentRow(string glyph, string title, string desc, StatusPill pill, Color accent)
    {
        Glyph = glyph;
        Title = title;
        Desc = desc;
        Pill = pill;
        Accent = accent;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor, true);
        Height = Theme.Px(72);
        BackColor = Color.Transparent;
        anim = new System.Windows.Forms.Timer();
        anim.Interval = 16;
        anim.Tick += delegate
        {
            double tt = _hover ? 1 : 0;
            hoverT += (tt - hoverT) * 0.3;
            Invalidate();
            if (Math.Abs(hoverT - tt) < 0.004) anim.Stop();
        };
        Pill.Location = new Point(Width - Pill.Width - Theme.Px(40), (Height - Pill.Height) / 2);
        Pill.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(Pill);
    }

    bool _hover;
    protected override void OnMouseEnter(EventArgs e) { _hover = true; anim.Start(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; anim.Start(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color bg = Theme.Lerp(Theme.Panel, Theme.NavHover, hoverT);
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = Theme.RoundRect(r, Theme.Px(12)))
        {
            using (SolidBrush br = new SolidBrush(bg)) g.FillPath(br, path);
            using (Pen pen = new Pen(Theme.Lerp(Theme.PanelBorder, Accent, hoverT), 1f + (float)hoverT))
                g.DrawPath(pen, path);
        }
        // 左侧组件色竖条（专属色语言，hover 时亮起）
        using (SolidBrush br = new SolidBrush(Color.FromArgb(90 + (int)(165 * hoverT), Accent)))
        {
            using (GraphicsPath bar = Theme.RoundRect(new Rectangle(0, Theme.Px(16), Theme.Px(4), Height - Theme.Px(32)), 3))
                g.FillPath(br, bar);
        }
        using (Font f = new Font(Theme.FontIcons, 15))
        using (SolidBrush br = new SolidBrush(Theme.Lerp(Accent, Color.White, hoverT * 0.45)))
        {
            g.DrawString(Glyph, f, br, Theme.Px(18), Height / 2 - Theme.Px(15));
        }
        using (SolidBrush br = new SolidBrush(Theme.Ink))
        {
            g.DrawString(Title, Theme.FontTitle, br, Theme.Px(58), Theme.Px(12));
        }
        using (SolidBrush br = new SolidBrush(Theme.Muted))
        {
            g.DrawString(Desc, Theme.FontSmall, br, Theme.Px(58), Theme.Px(38));
        }
        // 右侧 "›"：hover 时从右滑入，提示可点击
        if (hoverT > 0.02)
        {
            using (SolidBrush br = new SolidBrush(Color.FromArgb((int)(230 * hoverT), Accent)))
            using (Font f = new Font(Theme.UiFontName, 16, FontStyle.Bold))
            {
                float cx = Width - Theme.Px(30) + (1 - (float)Theme.EaseOut(hoverT)) * Theme.Px(10);
                g.DrawString("›", f, br, cx, Height / 2 - Theme.Px(14));
            }
        }
    }
}

class ComponentsPage : Page
{
    MainForm owner;
    Panel listHost;
    Label t, hint;
    internal Page[] details = new Page[5];
    Page activeDetail;
    bool detailOpen;

    public ComponentsPage(MainForm owner) : base("components")
    {
        this.owner = owner;
        int x0 = Theme.Px(32);
        t = new Label();
        t.Text = "组件";
        t.Font = Theme.FontTitle;
        t.ForeColor = Theme.Aqua;
        t.AutoSize = true;
        t.BackColor = Color.Transparent;
        t.Location = new Point(x0, Theme.Px(24));
        Controls.Add(t);

        listHost = new Panel();
        listHost.BackColor = Color.Transparent;
        Controls.Add(listHost);

        string[][] rows = {
            new string[] { "\uECE4", "NewAPI 网关", "密钥托管与模型转发（可选）" },
            new string[] { "\uEA18", "LLM 守卫", "输出长度前置检查 · ACT 情绪分段" },
            new string[] { "\uE720", "语音服务", "GPT-SoVITS 推理引擎" },
            new string[] { "\uECAB", "语音适配器", "OpenAI 协议翻译 · 情绪选音 · 预合成缓存" },
            new string[] { "\uE8BD", "AIRI", "桌面宠物本体" },
        };
        for (int i = 0; i < rows.Length; i++)
        {
            StatusPill pill = new StatusPill(rows[i][1].Split(' ')[0]);
            pill.Tag = i;
            owner.AllPills.Add(pill);
            ComponentRow row = new ComponentRow(rows[i][0], rows[i][1], rows[i][2], pill, Theme.CompAccent(i));
            row.Tag = i;
            row.Top = Theme.Px(12) + i * (Theme.Px(80));
            row.Left = x0;
            row.Width = owner.ContentW - x0 - Theme.Px(20);
            row.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            row.Cursor = Cursors.Hand;
            int idx = i;
            row.Click += delegate { OpenDetail(idx, false); };
            listHost.Controls.Add(row);
        }

        Control behCard = owner.BuildBehaviorCard();
        behCard.Left = x0;
        behCard.Top = Theme.Px(20) + 5 * Theme.Px(80);
        behCard.Width = owner.ContentW - x0 - Theme.Px(20);
        behCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        listHost.Controls.Add(behCard);

        hint = new Label();
        hint.Text = "点击组件行进入对应配置，改动自动保存；状态灯每 2 秒自动刷新。";
        hint.Font = Theme.FontSmall;
        hint.ForeColor = Theme.Dim;
        hint.AutoSize = true;
        hint.BackColor = Color.Transparent;
        hint.Left = x0;
        listHost.Controls.Add(hint);

        // 四个可配置组件的二级页立即构建（文本框须先于 HookTextChanged 存在）
        for (int i = 0; i < 5; i++) details[i] = owner.BuildComponentDetail(i);

        Layout += delegate
        {
            listHost.SetBounds(0, t.Bottom + Theme.Px(4),
                ClientSize.Width, Math.Max(0, ClientSize.Height - t.Bottom - Theme.Px(4)));
            hint.Top = behCard.Bottom + Theme.Px(10);
            if (activeDetail != null && detailOpen)
                activeDetail.Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
        };
    }

    // detail 打开时，列表与标题在真实显示中被 detail 覆盖；
    // 截图（WM_PRINT）的子控件绘制顺序与视觉层级相反，需同步隐藏以保持快照一致
    internal void SyncUnderlayVisibility()
    {
        bool covered = detailOpen && activeDetail != null;
        if (t != null) t.Visible = !covered;
        if (listHost != null) listHost.Visible = !covered;
    }

    // 二级配置页：快照过渡推入（不移动 HWND，无残影）
    internal void OpenDetail(int idx, bool instant)
    {
        Page d = details[idx];
        if (d == null) return;
        if (detailOpen && activeDetail == d) return;
        if (d.Parent == null) Controls.Add(d);
        d.Bounds = new Rectangle(0, 0, Width, Height);
        if (instant)
        {
            d.Visible = true;
            d.BringToFront();
            activeDetail = d;
            detailOpen = true;
            SyncUnderlayVisibility();
            return;
        }
        Bitmap fromBmp = MainForm.Shot(this);
        d.PerformLayout();
        d.Visible = true;
        Bitmap toBmp = MainForm.Shot(d);
        d.Visible = false;
        activeDetail = d;
        detailOpen = true;
        SlideTransition tr = new SlideTransition(fromBmp, toBmp, 1, delegate(SlideTransition self)
        {
            d.Left = 0;
            d.Visible = true;
            d.BringToFront();
            SyncUnderlayVisibility();
            Controls.Remove(self);
            self.Dispose();
        });
        tr.Bounds = new Rectangle(0, 0, Width, Height);
        Controls.Add(tr);
        tr.BringToFront();
        tr.Begin();
    }

    internal void CloseDetail(bool instant)
    {
        if (!detailOpen || activeDetail == null) return;
        Page d = activeDetail;
        detailOpen = false;
        activeDetail = null;
        if (instant) { d.Visible = false; d.Left = 0; SyncUnderlayVisibility(); return; }
        Bitmap fromBmp = MainForm.Shot(d);
        d.Visible = false;
        d.Left = 0;
        SyncUnderlayVisibility();
        Bitmap toBmp = MainForm.Shot(this);
        SlideTransition tr = new SlideTransition(fromBmp, toBmp, -1, delegate(SlideTransition self)
        {
            Controls.Remove(self);
            self.Dispose();
        });
        tr.Bounds = new Rectangle(0, 0, Width, Height);
        Controls.Add(tr);
        tr.BringToFront();
        tr.Begin();
    }
}

// ---------------------------------------------------------------
// 启动画面（芒形态，沿用既有引擎）
// ---------------------------------------------------------------

class CharLabel : Label
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
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
        try
        {
            Stream ics = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("furina.ico");
            if (ics != null) Icon = new Icon(ics);
        }
        catch { }

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
        return new Font(Theme.UiFontName, 18);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
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
// 主界面（导航栏 + 五页面）
// ---------------------------------------------------------------

class MainForm : Form
{
    // 配置字段（配置页）
    TextBox txtAiri, txtTtsBat, txtNewApiExe, txtNewApiDir, txtNewApiProbe,
            txtSoVitsProbe, txtAdapterProbe, txtSession;
    Label markAiri, markTts, markNewApi, markNewApiDir, markNewApiProbe, markSoVits, markAdapter;
    DarkCheckBox chkAutoExit, chkKeep;
    TextBox txtLog;

    // 结构
    Panel navRail, contentPanel;
    Dictionary<string, NavItem> navItems = new Dictionary<string, NavItem>();
    Dictionary<string, Page> pages = new Dictionary<string, Page>();
    string[] navOrder = { "home", "components", "guide", "logs" };
    Page currentPage;
    SlideTransition activeTr;

    internal StatusPill[] Pills = new StatusPill[5];
    internal List<StatusPill> AllPills = new List<StatusPill>();
    internal int ContentW { get { return contentPanel != null ? contentPanel.Width : Theme.Px(760); } }

    internal void SnapNavForShot()
    {
        foreach (NavItem n in navItems.Values) n.Snap();
    }

    internal void OpenComponentDetailForShot(int i)
    {
        NavigateInstant("components");
        ((ComponentsPage)pages["components"]).OpenDetail(i, true);
    }

    Thread runThread;
    HomePage home;
    System.Windows.Forms.Timer statusTimer;
    System.Windows.Forms.Timer validateTimer;
    ToolTip toolTip = new ToolTip();
    volatile bool probing;

    public MainForm()
    {
        Text = "furina";
        try
        {
            Stream ics = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("furina.ico");
            if (ics != null) Icon = new Icon(ics);
        }
        catch { }
        // 配置完整自动启动：直接以芒形态（运行中）构建界面
        if (Program.AutoStarted) Theme.SetMode(UiTheme.Mang);
        Width = Theme.Px(1120);
        Height = Theme.Px(720);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(Theme.Px(960), Theme.Px(640));
        Font = Theme.FontBody;
        DoubleBuffered = true;

        BuildNav();
        BuildPages();
        ApplyDpiScale();
        Navigate("home", true, true);

        validateTimer = new System.Windows.Forms.Timer();
        validateTimer.Interval = 400;
        validateTimer.Tick += delegate { validateTimer.Stop(); ValidateAll(); SaveCfg(); };

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
            AppendLog("配置已载入。浏览选择你的组件路径后，回主页点「启动」。");
        }

        Shown += delegate { PlayEntrance(); };
    }

    void ApplyDpiScale()
    {
        try
        {
            ClientSize = new Size(Theme.Px(1104), Theme.Px(680));
            MinimumSize = new Size(Theme.Px(960), Theme.Px(640));
        }
        catch { }
    }

    // ---------------------------------------------------------------
    // 导航栏
    // ---------------------------------------------------------------

    void BuildNav()
    {
        navRail = new Panel();
        navRail.Dock = DockStyle.Left;
        navRail.Width = Theme.Px(180);
        navRail.BackColor = Theme.NavBg;
        // 注意：navRail 在 BuildNav 末尾才 Add（后 Add 的先停靠到左侧）

        Label brand = new Label();
        brand.Text = "furina";
        brand.Font = Theme.FontBrand;
        brand.ForeColor = Theme.Aqua;
        brand.AutoSize = true;
        brand.BackColor = Color.Transparent;
        brand.Location = new Point(Theme.Px(18), Theme.Px(20));
        navRail.Controls.Add(brand);

        string[][] items = {
            new string[] { "\uE80F", "主页", "home" },
            new string[] { "\uE71D", "组件", "components" },
            new string[] { "\uE8A5", "教程", "guide" },
            new string[] { "\uE8BA", "日志", "logs" },
        };
        for (int i = 0; i < items.Length; i++)
        {
            NavItem nav = new NavItem(items[i][0], items[i][1], items[i][2]);
            nav.Top = brand.Bottom + Theme.Px(24) + i * Theme.Px(50);
            nav.Left = 0;
            nav.Width = navRail.Width;
            nav.Click += Nav_Click;
            navRail.Controls.Add(nav);
            navItems[items[i][2]] = nav;
        }

        // 底部副标题
        Label sub = new Label();
        sub.Text = "枫丹夜海";
        sub.Font = Theme.FontSmall;
        sub.ForeColor = Theme.Dim;
        sub.AutoSize = true;
        sub.BackColor = Color.Transparent;
        sub.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        sub.Location = new Point(Theme.Px(18), Height - Theme.Px(70));
        navRail.Controls.Add(sub);
        navRail.Resize += delegate { sub.Top = navRail.Height - Theme.Px(40); };

        contentPanel = new Panel();
        contentPanel.Dock = DockStyle.Fill;
        contentPanel.BackColor = Color.Transparent;
        // 先 Add contentPanel（Fill 填满剩余区域），后 Add navRail（Left 先行停靠左侧）
        Controls.Add(contentPanel);
        Controls.Add(navRail);
    }

    void Nav_Click(object sender, EventArgs e)
    {
        NavItem nav = (NavItem)sender;
        Navigate(nav.Key);
    }

    // ---------------------------------------------------------------
    // 五页面
    // ---------------------------------------------------------------

    void BuildPages()
    {
        home = new HomePage(this);
        pages["home"] = home;
        pages["components"] = new ComponentsPage(this);
        pages["guide"] = BuildGuidePage();
        pages["logs"] = BuildLogsPage();

        foreach (Page p in pages.Values)
        {
            p.Visible = false;
            p.Size = contentPanel.Size;
            p.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            contentPanel.Controls.Add(p);
        }
    }

    // ---------------------------------------------------------------
    // 组件二级配置页（组件页点击行进入；改动自动保存）
    // ---------------------------------------------------------------

    internal Page BuildComponentDetail(int idx)
    {
        string[][] meta = {
            new string[] { "NewAPI 网关", "密钥托管与模型转发（可选）" },
            new string[] { "LLM 守卫", "输出长度前置检查 · ACT 情绪分段" },
            new string[] { "语音服务", "GPT-SoVITS 推理引擎" },
            new string[] { "语音适配器", "OpenAI 协议翻译 · 情绪选音 · 预合成缓存" },
            new string[] { "AIRI", "桌面宠物本体" },
        };
        Page page = new Page("compDetail" + idx);
        int x0 = Theme.Px(28);
        Color accent = Theme.CompAccent(idx);

        CapsuleButton back = new CapsuleButton();
        back.Text = "‹ 组件列表";
        back.Size = new Size(Theme.Px(112), Theme.Px(30));
        back.Location = new Point(x0, Theme.Px(20));
        back.Click += delegate { ((ComponentsPage)pages["components"]).CloseDetail(false); };
        page.Controls.Add(back);

        Label t = new Label();
        t.Text = meta[idx][0];
        t.Font = Theme.FontTitle;
        t.ForeColor = accent;
        t.Tag = idx;
        t.AutoSize = true;
        t.BackColor = Color.Transparent;
        t.Location = new Point(back.Right + Theme.Px(12), Theme.Px(24));
        page.Controls.Add(t);

        Label desc = new Label();
        desc.Text = meta[idx][1];
        desc.Font = Theme.FontSmall;
        desc.ForeColor = Theme.Muted;
        desc.AutoSize = true;
        desc.BackColor = Color.Transparent;
        desc.Location = new Point(t.Left + 2, t.Bottom + Theme.Px(4));
        page.Controls.Add(desc);

        CardPanel card = new CardPanel();
        card.Top = desc.Bottom + Theme.Px(12);
        card.Left = x0;
        card.AutoSize = true;
        card.Width = ContentW - x0 - Theme.Px(24);
        page.Controls.Add(card);

        FlowLayoutPanel col = new FlowLayoutPanel();
        col.Dock = DockStyle.Top;
        col.AutoSize = true;
        col.FlowDirection = FlowDirection.TopDown;
        col.WrapContents = false;
        col.BackColor = Color.Transparent;
        col.Controls.Add(CardTitle("配置", accent));
        card.Controls.Add(col);

        if (idx == 1)
        {
            Label info = new Label();
            info.Text = "由启动器内置托管，无需配置。\r\n职责：输出长度前置检查（≤1000 字）、ACT 情绪分段、预合成缓存。\r\n监听地址：http://127.0.0.1:3001（随 NewAPI 网关一并启停）";
            info.Font = Theme.FontBody;
            info.ForeColor = Theme.Muted;
            info.AutoSize = true;
            info.BackColor = Color.Transparent;
            info.Margin = new Padding(0, 0, 0, Theme.Px(6));
            col.Controls.Add(info);
            page.Layout += delegate
            {
                int cw0 = page.ClientSize.Width - x0 - Theme.Px(24);
                if (cw0 > Theme.Px(400)) card.Width = cw0;
            };
            return page;
        }

        TableLayoutPanel grid = new TableLayoutPanel();
        grid.AutoSize = true;
        grid.ColumnCount = 4;
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 4));
        grid.BackColor = Color.Transparent;
        grid.Margin = new Padding(0);
        grid.Width = card.Width - Theme.Px(28);
        col.Controls.Add(grid);

        int row = 0;
        if (idx == 0)
        {
            txtNewApiExe = AddRow(grid, row++, "NewAPI 程序（可空）", delegate
            {
                string p = PickFile("选择 NewAPI 主程序（不需要网关可跳过）", "可执行文件|*.exe");
                if (p != null) txtNewApiExe.Text = p;
            }, out markNewApi);
            txtNewApiDir = AddRow(grid, row++, "NewAPI 数据目录", delegate
            {
                string p = PickFolder("选择 NewAPI 数据目录（含 one-api.db）");
                if (p != null) txtNewApiDir.Text = p;
            }, out markNewApiDir);
            txtNewApiProbe = AddRow(grid, row++, "NewAPI 地址", null, out markNewApiProbe);
            Label markSession;
            txtSession = AddRow(grid, row++, "网关密钥（空=自动生成）", null, out markSession);
        }
        else if (idx == 2)
        {
            txtTtsBat = AddRow(grid, row++, "语音服务脚本", delegate
            {
                string p = PickFile("选择语音服务启动脚本（bat）", "批处理|*.bat;*.cmd");
                if (p != null) txtTtsBat.Text = p;
            }, out markTts);
            txtSoVitsProbe = AddRow(grid, row++, "语音服务地址", null, out markSoVits);
        }
        else if (idx == 3)
        {
            txtAdapterProbe = AddRow(grid, row++, "语音适配器地址", null, out markAdapter);
        }
        else if (idx == 4)
        {
            txtAiri = AddRow(grid, row++, "AIRI 程序", delegate
            {
                string p = PickFile("选择 AIRI 主程序 (airi.exe)", "可执行文件|*.exe");
                if (p != null) txtAiri.Text = p;
            }, out markAiri);
        }

        page.Layout += delegate
        {
            int cw = page.ClientSize.Width - x0 - Theme.Px(24);
            if (cw < Theme.Px(400)) cw = Theme.Px(400);
            card.Width = cw;
            grid.Width = cw - Theme.Px(28);
        };
        return page;
    }

    internal Control BuildBehaviorCard()
    {
        CardPanel card = new CardPanel();
        card.AutoSize = true;
        FlowLayoutPanel col = new FlowLayoutPanel();
        col.Dock = DockStyle.Top;
        col.AutoSize = true;
        col.FlowDirection = FlowDirection.TopDown;
        col.WrapContents = false;
        col.BackColor = Color.Transparent;
        col.Controls.Add(CardTitle("行为"));

        chkAutoExit = new DarkCheckBox("AIRI 关闭时自动退出并回收");
        chkKeep = new DarkCheckBox("退出时保留服务运行");
        chkAutoExit.CheckedChanged += delegate
        {
            if (chkAutoExit.Checked && chkKeep.Checked) chkKeep.Checked = false;
            SaveCfg();
        };
        chkKeep.CheckedChanged += delegate
        {
            if (chkKeep.Checked && chkAutoExit.Checked) chkAutoExit.Checked = false;
            SaveCfg();
        };
        FlowLayoutPanel ck = new FlowLayoutPanel();
        ck.Dock = DockStyle.Top;
        ck.AutoSize = true;
        ck.WrapContents = false;
        ck.BackColor = Color.Transparent;
        ck.Controls.Add(chkAutoExit);
        ck.Controls.Add(chkKeep);
        col.Controls.Add(ck);

        Label hint = new Label();
        hint.Text = "改动即时自动保存，无需手动确认。";
        hint.Font = Theme.FontSmall;
        hint.ForeColor = Theme.Dim;
        hint.AutoSize = true;
        hint.BackColor = Color.Transparent;
        hint.Margin = new Padding(0, Theme.Px(6), 0, 0);
        col.Controls.Add(hint);
        card.Controls.Add(col);
        return card;
    }

    void SaveCfg()
    {
        FieldsToCfg();
        Furina.SaveIni();
    }

    Page BuildGuidePage()
    {
        Page page = new Page("guide");
        int x0 = Theme.Px(28);
        Label t = new Label();
        t.Text = "教程";
        t.Font = Theme.FontTitle;
        t.ForeColor = Theme.Aqua;
        t.AutoSize = true;
        t.BackColor = Color.Transparent;
        t.Location = new Point(x0, Theme.Px(22));
        page.Controls.Add(t);

        FlowLayoutPanel navBtns = new FlowLayoutPanel();
        navBtns.AutoSize = true;
        navBtns.WrapContents = true;
        navBtns.BackColor = Color.Transparent;
        navBtns.Location = new Point(x0, t.Bottom + Theme.Px(8));
        navBtns.Width = ContentW - x0 - Theme.Px(24);
        navBtns.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        RichTextBox body = new RichTextBox();
        body.Dock = DockStyle.Fill;
        body.ReadOnly = true;
        body.BackColor = Theme.FieldBg;
        body.ForeColor = Theme.Ink;
        body.BorderStyle = BorderStyle.None;
        body.DetectUrls = false;
        try { body.Rtf = MiniMd.ToRtf(TutorialText.FullMarkdown); }
        catch { body.Text = TutorialText.FullMarkdown; }

        for (int i = 0; i < TutorialText.Titles.Length; i++)
        {
            CapsuleButton b = new CapsuleButton();
            b.Text = TutorialText.Titles[i].Replace(" · ", "·");
            b.Size = new Size(
                TextRenderer.MeasureText(b.Text, Theme.FontBody).Width + Theme.Px(30),
                Theme.Px(28));
            int idx = i;
            b.Click += delegate
            {
                int p = body.Find(TutorialText.Titles[idx]);
                if (p >= 0) { body.SelectionStart = p; body.ScrollToCaret(); }
            };
            navBtns.Controls.Add(b);
        }
        page.Controls.Add(navBtns);

        Panel host = new Panel();
        host.BackColor = Color.Transparent;
        host.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        host.Controls.Add(body);
        page.Controls.Add(host);

        page.Layout += delegate
        {
            navBtns.Width = page.ClientSize.Width - x0 - Theme.Px(24);
            host.SetBounds(x0, navBtns.Bottom + Theme.Px(8),
                page.ClientSize.Width - x0 - Theme.Px(24),
                page.ClientSize.Height - navBtns.Bottom - Theme.Px(24));
        };
        host.SetBounds(x0, navBtns.Bottom + Theme.Px(8),
            ContentW - x0 - Theme.Px(24), contentPanel.Height - navBtns.Bottom - Theme.Px(24));
        return page;
    }

    Page BuildLogsPage()
    {
        Page page = new Page("logs");
        int x0 = Theme.Px(28);
        Label t = new Label();
        t.Text = "日志";
        t.Font = Theme.FontTitle;
        t.ForeColor = Theme.Aqua;
        t.AutoSize = true;
        t.BackColor = Color.Transparent;
        t.Location = new Point(x0, Theme.Px(22));
        page.Controls.Add(t);

        txtLog = new TextBox();
        txtLog.Multiline = true;
        txtLog.ReadOnly = true;
        txtLog.ScrollBars = ScrollBars.Vertical;
        txtLog.BackColor = Theme.FieldBg;
        txtLog.ForeColor = Color.FromArgb(180, 220, 235);
        txtLog.Font = Theme.FontLog;
        txtLog.BorderStyle = BorderStyle.None;
        txtLog.Dock = DockStyle.Fill;

        Panel host = new Panel();
        host.BackColor = Color.Transparent;
        host.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        host.Controls.Add(txtLog);
        page.Controls.Add(host);

        page.Layout += delegate
        {
            host.SetBounds(x0, t.Bottom + Theme.Px(8),
                page.ClientSize.Width - x0 - Theme.Px(24),
                page.ClientSize.Height - t.Bottom - Theme.Px(24));
        };
        host.SetBounds(x0, t.Bottom + Theme.Px(8),
            ContentW - x0 - Theme.Px(24), contentPanel.Height - t.Bottom - Theme.Px(24));
        return page;
    }

    // ---------------------------------------------------------------
    // 页面切换（右滑进入，指数缓出）
    // ---------------------------------------------------------------

    internal void Navigate(string key) { Navigate(key, false, false); }
    internal void NavigateInstant(string key) { Navigate(key, true, true); }

    // 控件内容快照（WM_PRINT 强制绘制，隐藏控件亦可截取）
    internal static Bitmap Shot(Control c)
    {
        Bitmap b = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height));
        c.DrawToBitmap(b, new Rectangle(0, 0, c.Width, c.Height));
        return b;
    }

    void Navigate(string key, bool silent, bool instant)
    {
        if (!pages.ContainsKey(key)) return;
        Page target = pages[key];
        if (currentPage == target && !instant) return;

        foreach (NavItem n in navItems.Values) n.SetSelected(n.Key == key);

        if (instant || currentPage == null)
        {
            target.Visible = true;
            target.BringToFront();
            target.Left = 0;
            if (currentPage != null && currentPage != target) currentPage.Visible = false;
            currentPage = target;
            return;
        }

        // 上一次过渡未播完就被打断：先强制收场，保证只有 currentPage 可见
        if (activeTr != null)
        {
            contentPanel.Controls.Remove(activeTr);
            activeTr.Dispose();
            activeTr = null;
            foreach (Page p in pages.Values)
                if (p != currentPage) { p.Visible = false; p.Left = 0; }
            currentPage.Visible = true;
            currentPage.Left = 0;
            currentPage.BringToFront();
        }

        Page old = currentPage;
        int dir = Array.IndexOf(navOrder, key) > Array.IndexOf(navOrder, old.Key) ? 1 : -1;

        // 快照过渡：两个页面各截一张位图，在单个表面上做纯位移动画。
        // 不移动任何 HWND，根绝滑动残影/叠影。
        old.SetBounds(0, 0, contentPanel.Width, contentPanel.Height);
        Bitmap fromBmp = Shot(old);
        target.SetBounds(0, 0, contentPanel.Width, contentPanel.Height);
        target.PerformLayout();
        target.Visible = true;
        if (target is ComponentsPage) ((ComponentsPage)target).SyncUnderlayVisibility();
        Bitmap toBmp = Shot(target);
        target.Visible = false;
        currentPage = target;

        Page oldPage = old, targetPage = target;
        SlideTransition tr = new SlideTransition(fromBmp, toBmp, dir, delegate(SlideTransition self)
        {
            oldPage.Visible = false;
            oldPage.Left = 0;
            targetPage.Left = 0;
            targetPage.Visible = true;
            targetPage.BringToFront();
            contentPanel.Controls.Remove(self);
            self.Dispose();
            activeTr = null;
        });
        tr.Bounds = new Rectangle(0, 0, contentPanel.Width, contentPanel.Height);
        contentPanel.Controls.Add(tr);
        tr.BringToFront();
        activeTr = tr;
        tr.Begin();
    }

    // ---------------------------------------------------------------
    // 启动/停止
    // ---------------------------------------------------------------

    internal void ToggleRun()
    {
        if (runThread != null && runThread.IsAlive)
        {
            Furina.RequestStop();
        }
        else
        {
            StartRun();
        }
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
        SwitchTheme(UiTheme.Mang, true);
    }

    void AttachRun(Thread t)
    {
        runThread = t;
        SetMainButton(true);
        Thread watcher = new Thread(delegate ()
        {
            t.Join();
            Ui(delegate
            {
                SetMainButton(false);
                AppendLog("已停止。可以修改配置后重新启动。");
                SwitchTheme(UiTheme.Huang, true);
            });
        });
        watcher.IsBackground = true;
        watcher.Start();
    }

    void SetMainButton(bool running)
    {
        if (home == null) return;
        home.SetRunning(running);
        if (running)
        {
            home.BtnMain.Text = "■ 停止全部组件";
            home.BtnMain.AccentColor = Theme.Err;
        }
        else
        {
            home.BtnMain.Text = "▶ 启动全部组件";
            home.BtnMain.AccentColor = Color.Empty;
        }
    }

    // ---------------------------------------------------------------
    // 荒/芒主题切换
    // ---------------------------------------------------------------

    internal void SwitchTheme(UiTheme m, bool animate)
    {
        if (Theme.Mode == m) return;
        Color oldField = Theme.FieldBg, oldMuted = Theme.Muted, oldInk = Theme.Ink,
              oldDim = Theme.Dim, oldAqua = Theme.Aqua, oldGold = Theme.Gold, oldNavHover = Theme.NavHover;
        Bitmap snap = null;
        if (animate)
        {
            try { snap = Shot(this); } catch { }
        }
        Theme.SetMode(m);
        ApplyTheme(oldField, oldMuted, oldInk, oldDim, oldAqua, oldGold, oldNavHover);
        if (snap != null) FadeOverlay.Begin(this, snap);
    }

    void ApplyTheme(Color oldField, Color oldMuted, Color oldInk, Color oldDim,
        Color oldAqua, Color oldGold, Color oldNavHover)
    {
        navRail.BackColor = Theme.NavBg;
        foreach (Control c in Walk(this))
        {
            if (c is NavItem) { c.BackColor = Theme.NavBg; }
            else if (c is TextBoxBase) { c.BackColor = Theme.FieldBg; c.ForeColor = Theme.Ink; }
            else if (c is StatusPill) { c.BackColor = Theme.Panel; }
            else if (c is ComponentRow && c.Tag is int) { ((ComponentRow)c).Accent = Theme.CompAccent((int)c.Tag); }
            else if (c is Label && c.Tag is int) { c.ForeColor = Theme.CompAccent((int)c.Tag); }
            else if (c is Panel && !(c is Page) && !(c is CardPanel) && c.BackColor == oldField) c.BackColor = Theme.FieldBg;
            if (c is Label || c is CapsuleButton)
            {
                if (c.ForeColor == oldMuted) c.ForeColor = Theme.Muted;
                else if (c.ForeColor == oldInk) c.ForeColor = Theme.Ink;
                else if (c.ForeColor == oldDim) c.ForeColor = Theme.Dim;
                else if (c.ForeColor == oldAqua) c.ForeColor = Theme.Aqua;
                else if (c.ForeColor == oldGold) c.ForeColor = Theme.Gold;
            }
        }
        txtLog.ForeColor = Theme.LogInk;
        home.BtnMain.BaseColor = Theme.Lerp(Theme.AquaDeep,
            Theme.Mode == UiTheme.Mang ? Color.White : Color.Black,
            Theme.Mode == UiTheme.Mang ? 0.10 : 0.55);
        home.BtnMain.HoverColor = Theme.Lerp(Theme.AquaDeep,
            Theme.Mode == UiTheme.Mang ? Color.White : Color.Black,
            Theme.Mode == UiTheme.Mang ? 0.0 : 0.35);
        ValidateAll();
        Invalidate(true);
    }

    static System.Collections.Generic.IEnumerable<Control> Walk(Control c)
    {
        yield return c;
        foreach (Control ch in c.Controls)
            foreach (Control x in Walk(ch))
                yield return x;
    }

    internal void SwitchThemeForShot(bool mang)
    {
        SwitchTheme(mang ? UiTheme.Mang : UiTheme.Huang, false);
        SnapNavForShot();
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
                    SetPill(0, Furina.NewApiEnabled, stNewApi);
                    SetPill(1, Furina.NewApiEnabled && Furina.cfg.LlmGuardEnabled, stGuard);
                    SetPill(2, true, stSoVits);
                    SetPill(3, true, stAdapter);
                    SetPillState(4, airi ? "运行中" : "未运行", airi ? Theme.Ok : Theme.Err);
                });
            }
            finally
            {
                probing = false;
            }
        });
    }

    void SetPill(int i, bool enabled, Furina.ComponentState st)
    {
        if (!enabled) { SetPillState(i, "未启用", Theme.Dim); return; }
        bool ok = st == Furina.ComponentState.Alive || st == Furina.ComponentState.Busy;
        SetPillState(i, ok ? "运行中" : "未响应", ok ? Theme.Ok : Theme.Err);
    }

    void SetPillState(int i, string text, Color c)
    {
        foreach (StatusPill p in AllPills)
        {
            if (p.Tag is int && (int)p.Tag == i) p.SetState(text, c);
        }
    }

    // ---------------------------------------------------------------
    // 配置字段辅助
    // ---------------------------------------------------------------

    Label CardTitle(string text) { return CardTitle(text, Theme.Aqua); }

    Label CardTitle(string text, Color c)
    {
        Label l = new Label();
        l.Text = text;
        l.Font = Theme.FontTitle;
        l.ForeColor = c;
        l.AutoSize = true;
        l.BackColor = Color.Transparent;
        l.Margin = new Padding(0, 2, 0, Theme.Px(8));
        return l;
    }

// ---------------------------------------------------------------
// 深色勾选框（自绘：原生 CheckBox 的勾在深色卡片上不可见）
// ---------------------------------------------------------------
class DarkCheckBox : Control
{
    bool _checked;
    public event EventHandler CheckedChanged;
    double hoverT, checkT;
    bool _hover;
    System.Windows.Forms.Timer anim;

    public DarkCheckBox(string text)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor, true);
        Text = text;
        Font = Theme.FontBody;
        BackColor = Color.Transparent;
        Height = Theme.Px(26);
        Width = Theme.Px(30) + TextRenderer.MeasureText(Text, Font).Width + Theme.Px(10);
        Margin = new Padding(0, 2, Theme.Px(22), 2);
        Cursor = Cursors.Hand;
        anim = new System.Windows.Forms.Timer();
        anim.Interval = 16;
        anim.Tick += delegate
        {
            double ht = _hover ? 1 : 0, ct = _checked ? 1 : 0;
            hoverT += (ht - hoverT) * 0.35;
            checkT += (ct - checkT) * 0.45;
            Invalidate();
            if (Math.Abs(hoverT - ht) < 0.01 && Math.Abs(checkT - ct) < 0.01) anim.Stop();
        };
    }

    public bool Checked
    {
        get { return _checked; }
        set
        {
            if (_checked == value) return;
            _checked = value;
            anim.Start();
            Invalidate();
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; anim.Start(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; anim.Start(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int box = Theme.Px(16);
        int by = (Height - box) / 2;
        Rectangle r = new Rectangle(1, by, box, box);
        double t = Theme.EaseOut(checkT);
        using (GraphicsPath path = Theme.RoundRect(r, Theme.Px(5)))
        {
            Color fill = Theme.Lerp(Theme.FieldBg, Theme.AquaDeep, t);
            using (SolidBrush br = new SolidBrush(fill)) g.FillPath(br, path);
            Color border = Theme.Lerp(Theme.Lerp(Theme.PanelBorder, Theme.Aqua, hoverT * 0.6), Theme.Aqua, t);
            using (Pen pen = new Pen(border, 1.4f)) g.DrawPath(pen, path);
        }
        if (t > 0.03)
        {
            using (Pen pen = new Pen(Color.FromArgb((int)(255 * t), Color.FromArgb(8, 18, 38)), 2.2f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                float u = box / 16f;
                g.DrawLines(pen, new PointF[]
                {
                    new PointF(1 + 3.6f * u, by + 8.4f * u),
                    new PointF(1 + 7.0f * u, by + 11.8f * u),
                    new PointF(1 + 12.6f * u, by + 4.6f * u),
                });
            }
        }
        using (SolidBrush br = new SolidBrush(Theme.Lerp(Theme.Muted, Theme.Ink, Math.Max(hoverT, (float)t))))
        {
            TextRenderer.DrawText(g, Text, Font, new Point(Theme.Px(24), Height / 2), br.Color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }
}

    TextBox AddRow(TableLayoutPanel grid, int row, string label, EventHandler onBrowse, out Label mark)
    {
        grid.RowCount = row + 1;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Label lbl = new Label();
        lbl.Text = label;
        lbl.AutoSize = true;
        lbl.Anchor = AnchorStyles.Left;
        lbl.Padding = new Padding(2, Theme.Px(8), 0, 0);
        lbl.Font = Theme.FontBody;
        lbl.ForeColor = Theme.Muted;
        lbl.BackColor = Color.Transparent;
        grid.Controls.Add(lbl, 0, row);

        Panel fieldWrap = new Panel();
        fieldWrap.Dock = DockStyle.Fill;
        fieldWrap.Height = Theme.Px(26);
        fieldWrap.Padding = new Padding(Theme.Px(8), Theme.Px(4), Theme.Px(8), Theme.Px(4));
        fieldWrap.BackColor = Theme.FieldBg;
        fieldWrap.Margin = new Padding(3);
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
            btn.Margin = new Padding(3);
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
        else SetMark(markAiri, true, "AIRI 主程序（静态校验通过；是否运行中见状态灯）");

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

    // ---------------------------------------------------------------
    // 入场淡入
    // ---------------------------------------------------------------

    void PlayEntrance()
    {
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
    // 调试
    // ---------------------------------------------------------------

    internal void DumpControls(Control c, StringBuilder sb, int depth)
    {
        sb.AppendLine(new string(' ', depth * 2) + c.GetType().Name + " bounds=" + c.Bounds + " vis=" + c.Visible);
        foreach (Control ch in c.Controls) DumpControls(ch, sb, depth + 1);
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
