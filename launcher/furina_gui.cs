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
        Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
        {
            MessageBox.Show(e.Exception.ToString(), "furina 未处理异常");
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
        // 逐页截图：home -> components -> settings -> guide -> logs
        // 两拍制：本拍截图、下一拍切页（滑动动画在计时器间隔内完成，不阻塞 UI 线程）
        string[] order = { "home", "components", "settings", "guide", "logs" };
        int idx = 0;
        bool captured = false;
        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 600;
        t.Tick += delegate
        {
            if (idx >= order.Length) { t.Stop(); Application.Exit(); return; }
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
            if (idx < order.Length) { f.NavigateInstant(order[idx]); captured = false; }
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
    public static readonly Color BgTop = Color.FromArgb(15, 23, 52);
    public static readonly Color BgBottom = Color.FromArgb(9, 14, 32);
    public static readonly Color Panel = Color.FromArgb(20, 31, 64);
    public static readonly Color PanelBorder = Color.FromArgb(42, 60, 100);
    public static readonly Color FieldBg = Color.FromArgb(14, 24, 56);
    public static readonly Color NavBg = Color.FromArgb(12, 19, 44);
    public static readonly Color NavHover = Color.FromArgb(30, 44, 80);
    public static readonly Color Ink = Color.FromArgb(234, 242, 251);
    public static readonly Color Muted = Color.FromArgb(160, 180, 210);
    public static readonly Color Aqua = Color.FromArgb(103, 232, 249);
    public static readonly Color AquaDeep = Color.FromArgb(56, 189, 248);
    public static readonly Color Gold = Color.FromArgb(231, 198, 107);
    public static readonly Color Ok = Color.FromArgb(110, 231, 183);
    public static readonly Color Err = Color.FromArgb(248, 113, 113);
    public static readonly Color Dim = Color.FromArgb(70, 88, 128);

    public static readonly Color SplashTop = Color.FromArgb(247, 251, 255);
    public static readonly Color SplashBottom = Color.FromArgb(219, 238, 250);

    public static readonly Font FontBrand = new Font("幼圆", 16, FontStyle.Bold);
    public static readonly Font FontHero = new Font("幼圆", 24, FontStyle.Bold);
    public static readonly Font FontTitle = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
    public static readonly Font FontBody = new Font("Microsoft YaHei UI", 9.5f);
    public static readonly Font FontSmall = new Font("Microsoft YaHei UI", 8.5f);
    public static readonly Font FontLog = new Font("Consolas", 9.5f);
    public const string FontIcons = "Segoe MDL2 Assets";

    public const int DurMicro = 120, DurShort = 220, DurLong = 420;

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
    public Color BaseColor = Color.FromArgb(32, 48, 88);
    public Color HoverColor = Color.FromArgb(45, 65, 110);
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
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        using (LinearGradientBrush br = new LinearGradientBrush(
            ClientRectangle, Theme.BgTop, Theme.BgBottom, LinearGradientMode.Vertical))
        {
            g.FillRectangle(br, ClientRectangle);
        }
        base.OnPaint(e);
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
    public Label LblHint;

    public HomePage(MainForm owner) : base("home")
    {
        int w = owner.ContentW, x0 = Theme.Px(48);
        Label brand = new Label();
        brand.Text = "芙宁娜";
        brand.Font = Theme.FontHero;
        brand.ForeColor = Theme.Aqua;
        brand.AutoSize = true;
        brand.BackColor = Color.Transparent;
        brand.Location = new Point(x0, Theme.Px(56));
        Controls.Add(brand);

        Label sub = new Label();
        sub.Text = "住在桌面上的她 · 一键拉起的全部世界";
        sub.Font = Theme.FontBody;
        sub.ForeColor = Theme.Muted;
        sub.AutoSize = true;
        sub.BackColor = Color.Transparent;
        sub.Location = new Point(x0 + 2, brand.Bottom + Theme.Px(8));
        Controls.Add(sub);

        BtnMain = new CapsuleButton();
        BtnMain.Text = "▶ 启动全部组件";
        BtnMain.Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold);
        BtnMain.Size = new Size(Theme.Px(210), Theme.Px(44));
        BtnMain.BaseColor = Theme.Lerp(Theme.AquaDeep, Color.Black, 0.55);
        BtnMain.HoverColor = Theme.Lerp(Theme.AquaDeep, Color.Black, 0.35);
        BtnMain.Location = new Point(x0, sub.Bottom + Theme.Px(28));
        BtnMain.Click += delegate { owner.ToggleRun(); };
        Controls.Add(BtnMain);

        BtnToGuide = new CapsuleButton();
        BtnToGuide.Text = "使用教程";
        BtnToGuide.Size = new Size(Theme.Px(120), Theme.Px(44));
        BtnToGuide.AccentColor = Theme.Gold;
        BtnToGuide.Location = new Point(BtnMain.Right + Theme.Px(14), BtnMain.Top);
        BtnToGuide.Click += delegate { owner.Navigate("guide"); };
        Controls.Add(BtnToGuide);

        BtnToSettings = new CapsuleButton();
        BtnToSettings.Text = "组件配置";
        BtnToSettings.Size = new Size(Theme.Px(120), Theme.Px(44));
        BtnToSettings.Location = new Point(BtnToGuide.Right + Theme.Px(14), BtnMain.Top);
        BtnToSettings.Click += delegate { owner.Navigate("settings"); };
        Controls.Add(BtnToSettings);

        Label st = new Label();
        st.Text = "全部组件";
        st.Font = Theme.FontTitle;
        st.ForeColor = Theme.Aqua;
        st.AutoSize = true;
        st.BackColor = Color.Transparent;
        st.Location = new Point(x0, BtnMain.Bottom + Theme.Px(40));
        Controls.Add(st);

        string[] names = { "NewAPI", "LLM守卫", "语音服务", "语音适配器", "AIRI" };
        for (int i = 0; i < 5; i++)
        {
            Pills[i] = new StatusPill(names[i]);
            Pills[i].Tag = i;
            Pills[i].Location = new Point(x0 + i * (Theme.Px(112)), st.Bottom + Theme.Px(10));
            Controls.Add(Pills[i]);
            owner.AllPills.Add(Pills[i]);
        }

        LblHint = new Label();
        LblHint.Text = "双击启动器即可使用：配置完整时将自动启动全部组件。";
        LblHint.Font = Theme.FontSmall;
        LblHint.ForeColor = Theme.Dim;
        LblHint.AutoSize = true;
        LblHint.BackColor = Color.Transparent;
        LblHint.Location = new Point(x0, Pills[0].Bottom + Theme.Px(28));
        Controls.Add(LblHint);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // 底部波缘（参考图1 的水波语言，低透明度曲线）
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = ClientRectangle;
        using (Pen p1 = new Pen(Color.FromArgb(28, Theme.Aqua), 2.5f))
        using (Pen p2 = new Pen(Color.FromArgb(18, Theme.Aqua), 2f))
        {
            g.DrawBezier(p1, -40, r.Bottom - Theme.Px(70),
                r.Width / 3, r.Bottom - Theme.Px(150),
                r.Width * 2 / 3, r.Bottom - Theme.Px(10),
                r.Width + 40, r.Bottom - Theme.Px(90));
            g.DrawBezier(p2, -40, r.Bottom - Theme.Px(30),
                r.Width / 3, r.Bottom - Theme.Px(110),
                r.Width * 2 / 3, r.Bottom + Theme.Px(30),
                r.Width + 40, r.Bottom - Theme.Px(50));
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
    double hoverT;
    System.Windows.Forms.Timer anim;

    public ComponentRow(string glyph, string title, string desc, StatusPill pill)
    {
        Glyph = glyph;
        Title = title;
        Desc = desc;
        Pill = pill;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor, true);
        Height = Theme.Px(72);
        BackColor = Color.Transparent;
        anim = new System.Windows.Forms.Timer();
        anim.Interval = 16;
        anim.Tick += delegate
        {
            double t = _hover ? 1 : 0;
            hoverT += (t - hoverT) * 0.3;
            Invalidate();
            if (Math.Abs(hoverT - t) < 0.004) anim.Stop();
        };
        Pill.Location = new Point(Width - Pill.Width - Theme.Px(14), (Height - Pill.Height) / 2);
        Pill.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(Pill);
    }

    bool _hover;
    protected override void OnMouseEnter(EventArgs e) { _hover = true; anim.Start(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; anim.Start(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color bg = Theme.Lerp(Theme.Panel, Theme.NavHover, hoverT);
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = Theme.RoundRect(r, Theme.Px(12)))
        {
            using (SolidBrush br = new SolidBrush(bg)) g.FillPath(br, path);
            using (Pen pen = new Pen(Theme.Lerp(Theme.PanelBorder, Theme.Aqua, hoverT * 0.5), 1f))
                g.DrawPath(pen, path);
        }
        using (Font f = new Font(Theme.FontIcons, 15))
        using (SolidBrush br = new SolidBrush(Theme.Lerp(Theme.Aqua, Color.White, hoverT * 0.4)))
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
    }
}

class ComponentsPage : Page
{
    public ComponentsPage(MainForm owner) : base("components")
    {
        int x0 = Theme.Px(32);
        Label t = new Label();
        t.Text = "组件";
        t.Font = Theme.FontTitle;
        t.ForeColor = Theme.Aqua;
        t.AutoSize = true;
        t.BackColor = Color.Transparent;
        t.Location = new Point(x0, Theme.Px(24));
        Controls.Add(t);

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
            ComponentRow row = new ComponentRow(rows[i][0], rows[i][1], rows[i][2], pill);
            row.Top = t.Bottom + Theme.Px(12) + i * (Theme.Px(80));
            row.Left = x0;
            row.Width = owner.ContentW - x0 - Theme.Px(20);
            row.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(row);
        }

        Label hint = new Label();
        hint.Text = "状态灯每 2 秒自动刷新；组件页仅作总览，启停与配置请分别使用主页与配置页。";
        hint.Font = Theme.FontSmall;
        hint.ForeColor = Theme.Dim;
        hint.AutoSize = true;
        hint.BackColor = Color.Transparent;
        hint.Top = t.Bottom + Theme.Px(20) + 5 * Theme.Px(80);
        hint.Left = x0;
        Controls.Add(hint);
    }
}

// ---------------------------------------------------------------
// 启动画面（芒形态，沿用既有引擎）
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
// 主界面（导航栏 + 五页面）
// ---------------------------------------------------------------

class MainForm : Form
{
    // 配置字段（配置页）
    TextBox txtAiri, txtTtsBat, txtNewApiExe, txtNewApiDir, txtNewApiProbe,
            txtSoVitsProbe, txtAdapterProbe, txtSession;
    Label markAiri, markTts, markNewApi, markNewApiDir, markNewApiProbe, markSoVits, markAdapter;
    CheckBox chkAutoExit, chkKeep;
    TextBox txtLog;

    // 结构
    Panel navRail, contentPanel;
    Dictionary<string, NavItem> navItems = new Dictionary<string, NavItem>();
    Dictionary<string, Page> pages = new Dictionary<string, Page>();
    string[] navOrder = { "home", "components", "settings", "guide", "logs" };
    Page currentPage;
    System.Windows.Forms.Timer slideTimer;
    int slideDir;
    Page slidePage;

    internal StatusPill[] Pills = new StatusPill[5];
    internal List<StatusPill> AllPills = new List<StatusPill>();
    internal int ContentW { get { return contentPanel != null ? contentPanel.Width : Theme.Px(760); } }

    internal void SnapNavForShot()
    {
        foreach (NavItem n in navItems.Values) n.Snap();
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
            new string[] { "\uE713", "配置", "settings" },
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
        pages["settings"] = BuildSettingsPage();
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

    Page BuildSettingsPage()
    {
        Page page = new Page("settings");
        int x0 = Theme.Px(28);
        Label t = new Label();
        t.Text = "配置";
        t.Font = Theme.FontTitle;
        t.ForeColor = Theme.Aqua;
        t.AutoSize = true;
        t.BackColor = Color.Transparent;
        t.Location = new Point(x0, Theme.Px(22));
        page.Controls.Add(t);

        CardPanel card = new CardPanel();
        card.Top = t.Bottom + Theme.Px(8);
        card.Left = x0;
        card.AutoSize = true;
        card.Width = ContentW - x0 - Theme.Px(24);
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
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 4));
        grid.BackColor = Color.Transparent;
        grid.Margin = new Padding(0);
        grid.Width = card.Width - Theme.Px(28);

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
        page.Controls.Add(card);

        CardPanel card2 = new CardPanel();
        card2.Top = card.Top + Theme.Px(330);
        card2.Left = x0;
        card2.AutoSize = true;
        card2.Width = card.Width;
        FlowLayoutPanel col2 = new FlowLayoutPanel();
        col2.Dock = DockStyle.Top;
        col2.AutoSize = true;
        col2.FlowDirection = FlowDirection.TopDown;
        col2.WrapContents = false;
        col2.BackColor = Color.Transparent;
        col2.Controls.Add(CardTitle("行为"));
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
        FlowLayoutPanel ck = new FlowLayoutPanel();
        ck.Dock = DockStyle.Top;
        ck.AutoSize = true;
        ck.WrapContents = false;
        ck.BackColor = Color.Transparent;
        ck.Controls.Add(chkAutoExit);
        ck.Controls.Add(chkKeep);
        col2.Controls.Add(ck);

        CapsuleButton btnSave = new CapsuleButton();
        btnSave.Text = "保存配置";
        btnSave.Size = new Size(Theme.Px(120), Theme.Px(32));
        btnSave.Margin = new Padding(0, Theme.Px(8), 0, 0);
        btnSave.Click += delegate { FieldsToCfg(); Furina.SaveIni(); Furina.Log("配置已保存到 furina.ini"); };
        col2.Controls.Add(btnSave);
        card2.Controls.Add(col2);
        page.Controls.Add(card2);

        // 页面尺寸确定后统一校正卡片宽度与第二卡片位置（窗口缩放时同步）
        page.Layout += delegate
        {
            int cw = page.ClientSize.Width - x0 - Theme.Px(24);
            if (cw < Theme.Px(400)) cw = Theme.Px(400);
            card.Width = cw;
            card2.Width = cw;
            grid.Width = cw - Theme.Px(28);
            card2.Top = card.Bottom + Theme.Px(10);
        };
        return page;
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
            b.AutoSize = true;
            b.MinimumSize = new Size(Theme.Px(110), Theme.Px(28));
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

        int dir = Array.IndexOf(navOrder, key) > Array.IndexOf(navOrder, currentPage.Key) ? 1 : -1;
        slideDir = dir;
        slidePage = target;
        target.Visible = true;
        target.Left = dir * contentPanel.Width;
        target.BringToFront();
        target.SetBounds(target.Left, 0, contentPanel.Width, contentPanel.Height);

        if (slideTimer != null) slideTimer.Stop();
        slideTimer = new System.Windows.Forms.Timer();
        slideTimer.Interval = 16;
        double start = Environment.TickCount / 1000.0;
        int fromX = target.Left;
        Page old = currentPage;
        int oldFromX = old.Left;
        slideTimer.Tick += delegate
        {
            double p = Math.Min(1.0, (Environment.TickCount / 1000.0 - start) / 0.28);
            double e = Theme.EaseOut(p);
            slidePage.Left = fromX - (int)Math.Round(dir * contentPanel.Width * e);
            old.Left = oldFromX - (int)Math.Round(dir * contentPanel.Width * 0.3 * e);
            if (p >= 1)
            {
                slideTimer.Stop();
                old.Visible = false;
                old.Left = 0;
                slidePage.Left = 0;
                currentPage = slidePage;
            }
        };
        slideTimer.Start();
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
            });
        });
        watcher.IsBackground = true;
        watcher.Start();
    }

    void SetMainButton(bool running)
    {
        if (home == null) return;
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

    Label CardTitle(string text)
    {
        Label l = new Label();
        l.Text = text;
        l.Font = Theme.FontTitle;
        l.ForeColor = Theme.Aqua;
        l.AutoSize = true;
        l.BackColor = Color.Transparent;
        l.Margin = new Padding(0, 2, 0, Theme.Px(8));
        return l;
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
        c.Margin = new Padding(0, 2, Theme.Px(20), 2);
        return c;
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
