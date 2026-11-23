using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Security;
using HyperLight.ColorProcessing;
using HyperLight.Config;

namespace HyperLight.Tray;

/// <summary>
/// 托盘程序（M4）：屏幕右下角一个小圆点图标，上下两半显示当前的上、下两个颜色。
/// 右键菜单：状态（麦克风、主板）、暂停 / 恢复、增强颜色、取色方式、
/// 麦克风 / 机箱灯 / 冷排灯的开关，机箱灯和冷排灯的取色和灯效、开机自启、打开设置文件、重新读取设置、退出。
/// </summary>
/// <remarks>
/// 界面只在主线程（界面线程）里操作。同步工作在 <see cref="SyncEngine"/> 自己的线程里进行，
/// 界面每 0.5 秒读一次它的状态快照来刷新图标和菜单，两边不直接互相调用。
/// 退出或暂停时停止发送：麦克风马上回到自带灯效，机箱灯和冷排灯保持最后的颜色。
/// </remarks>
internal sealed class TrayApp : ApplicationContext
{
    private const int refreshIntervalMs = 500;
    private const int balloonTimeoutMs = 5000;

    /// <summary>托盘图标的提示文字最多 127 个字符（Windows 的限制）。</summary>
    private const int maxTooltipLength = 127;

    private static bool showingError;

    private readonly NotifyIcon notifyIcon;
    private readonly ContextMenuStrip menu;
    private readonly ToolStripMenuItem statusItem;
    private readonly ToolStripMenuItem boardStatusItem;
    private readonly ToolStripMenuItem pauseItem;
    private readonly ToolStripMenuItem boostItem;
    private readonly ToolStripMenuItem layoutMenu;
    private readonly Dictionary<ZoneLayout, ToolStripMenuItem> layoutItems = new();
    private readonly ToolStripMenuItem micItem;
    private readonly ToolStripMenuItem caseItem;
    private readonly ToolStripMenuItem radiatorItem;
    private readonly ToolStripMenuItem caseSourceMenu;
    private readonly ToolStripMenuItem radiatorSourceMenu;
    private readonly Dictionary<ColorSource, ToolStripMenuItem> caseSourceItems = new();
    private readonly Dictionary<ColorSource, ToolStripMenuItem> radiatorSourceItems = new();
    private readonly Dictionary<LightEffect, ToolStripMenuItem> caseEffectItems = new();
    private readonly Dictionary<LightEffect, ToolStripMenuItem> radiatorEffectItems = new();
    private readonly ToolStripMenuItem autoStartItem;
    private readonly System.Windows.Forms.Timer refreshTimer;

    private AppConfig config;
    private SyncEngine engine;
    private Icon currentIcon;
    private (ZoneColors? Colors, bool Paused) shownIconState;
    private bool ngenuityWarned;
    private bool openRgbWarned;
    private bool exiting;
    private bool disposed;

    /// <summary>启动托盘程序，直到用户点「退出」。</summary>
    public static int Run(bool startedByAutoStart)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowUnexpectedError(e.Exception);

        // 同一时间只能有一个 HyperLight 控制麦克风（比如开机自启的那个已经在运行）
        using MicOwnerLock? owner = MicOwnerLock.TryAcquire();
        if (owner is null)
        {
            MessageBox.Show(
                "HyperLight 已经在运行了。\n图标在屏幕右下角的托盘区（可能收在 ^ 箭头里面）。",
                "HyperLight",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 3;
        }

        using var app = new TrayApp(startedByAutoStart);
        Application.Run(app);
        return 0;
    }

    private TrayApp(bool startedByAutoStart)
    {
        config = AppConfig.Load(AppConfig.DefaultPath, out List<string> notes);

        statusItem = new ToolStripMenuItem("正在启动…") { Enabled = false };
        boardStatusItem = new ToolStripMenuItem("主板：正在启动…") { Enabled = false };
        pauseItem = new ToolStripMenuItem("暂停同步", null, (_, _) => TogglePause());
        boostItem = new ToolStripMenuItem("增强颜色（模仿 Wallpaper Engine）", null, (_, _) => ToggleBoost());
        layoutMenu = new ToolStripMenuItem("取色方式");
        foreach (ZoneLayout layout in new[] { ZoneLayout.TopBottom, ZoneLayout.LeftRight, ZoneLayout.Whole })
        {
            var item = new ToolStripMenuItem(ZoneLayoutText.Of(layout), null, (_, _) => SelectLayout(layout));
            layoutItems[layout] = item;
            layoutMenu.DropDownItems.Add(item);
        }
        micItem = new ToolStripMenuItem("麦克风", null, (_, _) => ApplyConfig(config with { MicEnabled = !config.MicEnabled }));
        caseItem = new ToolStripMenuItem("机箱灯", null, (_, _) => ApplyConfig(config with { CaseEnabled = !config.CaseEnabled }));
        radiatorItem = new ToolStripMenuItem("冷排灯", null, (_, _) => ApplyConfig(config with { RadiatorEnabled = !config.RadiatorEnabled }));
        caseSourceMenu = BuildChoiceMenu(
            "机箱灯取色", ColorSourceRules.All, ColorSourceRules.TextOf, caseSourceItems,
            source => config.CaseColorSource != source, source => config with { CaseColorSource = source });
        radiatorSourceMenu = BuildChoiceMenu(
            "冷排灯取色", ColorSourceRules.All, ColorSourceRules.TextOf, radiatorSourceItems,
            source => config.RadiatorColorSource != source, source => config with { RadiatorColorSource = source });
        ToolStripMenuItem caseEffectMenu = BuildChoiceMenu(
            "机箱灯灯效", LightEffectRules.All, LightEffectRules.TextOf, caseEffectItems,
            effect => config.CaseEffect != effect, effect => config with { CaseEffect = effect });
        ToolStripMenuItem radiatorEffectMenu = BuildChoiceMenu(
            "冷排灯灯效", LightEffectRules.All, LightEffectRules.TextOf, radiatorEffectItems,
            effect => config.RadiatorEffect != effect, effect => config with { RadiatorEffect = effect });
        autoStartItem = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleAutoStart());

        menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            statusItem,
            boardStatusItem,
            new ToolStripSeparator(),
            pauseItem,
            boostItem,
            layoutMenu,
            new ToolStripSeparator(),
            micItem,
            caseItem,
            caseSourceMenu,
            caseEffectMenu,
            radiatorItem,
            radiatorSourceMenu,
            radiatorEffectMenu,
            new ToolStripSeparator(),
            autoStartItem,
            new ToolStripMenuItem("打开设置文件", null, (_, _) => OpenConfigFile()),
            new ToolStripMenuItem("重新读取设置", null, (_, _) => ReloadConfig()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("退出", null, (_, _) => ExitThread()),
        });

        currentIcon = TrayIconPainter.Paint(null, paused: false);
        shownIconState = (null, false);
        notifyIcon = new NotifyIcon
        {
            Icon = currentIcon,
            Text = "HyperLight：正在启动…",
            ContextMenuStrip = menu,
            Visible = true,
        };

        refreshTimer = new System.Windows.Forms.Timer { Interval = refreshIntervalMs };
        refreshTimer.Tick += (_, _) => RefreshFromEngine();

        engine = StartEngine(config, paused: false);
        TryRefreshAutoStartPath();
        UpdateCheckmarks();
        refreshTimer.Start();

        // 启动提示：手动启动时说明怎么用；配置文件有情况（比如第一次生成）也一起说。开机自启且一切正常时不打扰
        var messages = new List<string>();
        if (!startedByAutoStart)
        {
            messages.Add("已在右下角托盘运行。右键图标可以暂停、退出，或者打开「开机自启」。");
        }

        messages.AddRange(notes);
        if (messages.Count > 0)
        {
            ShowBalloon(string.Join("\n", messages), ToolTipIcon.Info);
        }
    }

    // ───────────────────────── 刷新显示 ─────────────────────────

    /// <summary>读引擎的状态快照，刷新菜单文字、提示文字和图标。</summary>
    private void RefreshFromEngine()
    {
        SyncStatus status = engine.Status;
        string state = DescribeState(status, config.MicEnabled);
        statusItem.Text = state;
        boardStatusItem.Text = $"主板：{status.BoardState}";
        pauseItem.Text = status.Paused ? "恢复同步" : "暂停同步";

        string tooltip = Truncate($"HyperLight：{state}", maxTooltipLength);
        if (notifyIcon.Text != tooltip)
        {
            notifyIcon.Text = tooltip;
        }

        // 图标只在颜色或暂停状态变了时才重画
        (ZoneColors? Colors, bool Paused) iconState = (status.Paused ? null : status.Target, status.Paused);
        if (iconState != shownIconState)
        {
            Icon newIcon = TrayIconPainter.Paint(iconState.Colors, iconState.Paused);
            notifyIcon.Icon = newIcon;
            TrayIconPainter.Destroy(currentIcon);
            currentIcon = newIcon;
            shownIconState = iconState;
        }

        // NGENUITY 每次从"没运行"变成"在运行"时提醒一次
        if (status.NgenuityRunning && !ngenuityWarned)
        {
            ShowBalloon("检测到 NGENUITY 正在运行。两个程序会同时控制麦克风，灯可能乱闪，请从托盘区退出 NGENUITY。", ToolTipIcon.Warning);
            ngenuityWarned = true;
        }
        else if (!status.NgenuityRunning)
        {
            ngenuityWarned = false;
        }

        // OpenRGB 同理：它会和本程序抢主板灯光芯片
        if (status.OpenRgbRunning && !openRgbWarned)
        {
            ShowBalloon("检测到 OpenRGB 正在运行。它会和本程序同时控制主板的灯，灯可能乱变，请完全退出 OpenRGB。", ToolTipIcon.Warning);
            openRgbWarned = true;
        }
        else if (!status.OpenRgbRunning)
        {
            openRgbWarned = false;
        }
    }

    /// <summary>把引擎状态变成一句话（菜单第一行和图标提示文字）。</summary>
    private static string DescribeState(SyncStatus status, bool micEnabled)
    {
        if (status.Paused)
        {
            return "已暂停（麦克风显示自带灯效，机箱灯和冷排灯保持当前颜色）";
        }

        if (status.Target is not ZoneColors target)
        {
            return status.LastError ?? status.WallpaperState;
        }

        if (!micEnabled)
        {
            return $"同步中：{target}（麦克风已关闭）";
        }

        return status.MicConnected ? $"同步中：{target}" : status.MicState;
    }

    private void UpdateCheckmarks()
    {
        boostItem.Checked = config.BoostColors;
        foreach ((ZoneLayout layout, ToolStripMenuItem item) in layoutItems)
        {
            item.Checked = layout == config.ZoneLayout;
        }

        micItem.Checked = config.MicEnabled;
        caseItem.Checked = config.CaseEnabled;
        radiatorItem.Checked = config.RadiatorEnabled;
        CheckOnly(caseSourceItems, config.CaseColorSource);
        CheckOnly(radiatorSourceItems, config.RadiatorColorSource);
        CheckOnly(caseEffectItems, config.CaseEffect);
        CheckOnly(radiatorEffectItems, config.RadiatorEffect);

        // 流动灯效用的是固定的两块区域，取色只在单色时有用：不用时变灰，免得以为点了没反应
        caseSourceMenu.Enabled = config.CaseEffect == LightEffect.Solid;
        radiatorSourceMenu.Enabled = config.RadiatorEffect == LightEffect.Solid;

        autoStartItem.Checked = IsAutoStartEnabledSafe();
    }

    /// <summary>
    /// 生成一个"多选一"子菜单（比如取色、灯效）。机箱灯和冷排灯的取色、灯效都用这个写法。
    /// <paramref name="isChange"/> 判断点的是不是和现在不同的选项；<paramref name="withChoice"/> 生成改好的新设置。
    /// </summary>
    private ToolStripMenuItem BuildChoiceMenu<T>(
        string title,
        IEnumerable<T> choices,
        Func<T, string> textOf,
        Dictionary<T, ToolStripMenuItem> items,
        Func<T, bool> isChange,
        Func<T, AppConfig> withChoice)
        where T : notnull
    {
        var choiceMenu = new ToolStripMenuItem(title);
        foreach (T choice in choices)
        {
            var item = new ToolStripMenuItem(textOf(choice), null, (_, _) =>
            {
                if (isChange(choice))
                {
                    ApplyConfig(withChoice(choice));
                }
            });
            items[choice] = item;
            choiceMenu.DropDownItems.Add(item);
        }

        return choiceMenu;
    }

    /// <summary>"多选一"菜单里只勾选当前的那一项。</summary>
    private static void CheckOnly<T>(Dictionary<T, ToolStripMenuItem> items, T current)
        where T : notnull
    {
        foreach ((T choice, ToolStripMenuItem item) in items)
        {
            item.Checked = EqualityComparer<T>.Default.Equals(choice, current);
        }
    }

    // ───────────────────────── 菜单操作 ─────────────────────────

    private void TogglePause()
    {
        engine.Paused = !engine.Paused;
        RefreshFromEngine();
    }

    private void ToggleBoost() => ApplyConfig(config with { BoostColors = !config.BoostColors });

    private void SelectLayout(ZoneLayout layout)
    {
        if (layout != config.ZoneLayout)
        {
            ApplyConfig(config with { ZoneLayout = layout });
        }
    }

    /// <summary>改设置：先保存到文件，再用新设置重启同步引擎。</summary>
    private void ApplyConfig(AppConfig newConfig)
    {
        if (!AppConfig.TrySave(newConfig, AppConfig.DefaultPath, out string? error))
        {
            ShowBalloon($"设置没能保存到文件（{error}）。这次先按新设置运行，下次启动会恢复原样。", ToolTipIcon.Warning);
        }

        config = newConfig;
        RestartEngine();
        UpdateCheckmarks();
    }

    private void ReloadConfig()
    {
        config = AppConfig.Load(AppConfig.DefaultPath, out List<string> notes);
        RestartEngine();
        UpdateCheckmarks();
        ShowBalloon(
            notes.Count > 0 ? string.Join("\n", notes) : "已重新读取设置。",
            notes.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private void OpenConfigFile()
    {
        string path = AppConfig.DefaultPath;
        if (!File.Exists(path) && !AppConfig.TrySave(config, path, out string? error))
        {
            ShowBalloon($"设置文件不存在，也没能生成（{error}）。", ToolTipIcon.Warning);
            return;
        }

        try
        {
            using Process? notepad = Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
            ShowBalloon("改完保存以后，在托盘菜单里点「重新读取设置」就会生效。", ToolTipIcon.Info);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ShowBalloon($"打不开设置文件（{ex.Message}）。文件位置：{path}", ToolTipIcon.Warning);
        }
    }

    private void ToggleAutoStart()
    {
        try
        {
            if (AutoStart.IsEnabled())
            {
                AutoStart.Disable();
                ShowBalloon("已取消开机自启。", ToolTipIcon.Info);
            }
            else
            {
                AutoStart.Enable();
                ShowBalloon("已设置开机自启：以后开机会自动在托盘运行。", ToolTipIcon.Info);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException)
        {
            ShowBalloon($"设置开机自启失败：{ex.Message}", ToolTipIcon.Warning);
        }

        UpdateCheckmarks();
    }

    // ───────────────────────── 同步引擎 ─────────────────────────

    private static SyncEngine StartEngine(AppConfig config, bool paused)
    {
        var newEngine = new SyncEngine(config) { Paused = paused };
        newEngine.Start();
        return newEngine;
    }

    /// <summary>用当前设置重启同步引擎，保持原来的暂停状态。重启时麦克风可能会闪一下自带灯效，主板灯会把颜色重发一次。</summary>
    private void RestartEngine()
    {
        bool wasPaused = engine.Paused;
        engine.Dispose();
        engine = StartEngine(config, wasPaused);
        RefreshFromEngine();
    }

    // ───────────────────────── 退出 ─────────────────────────

    /// <summary>点「退出」时：停止同步（麦克风马上回到自带灯效，主板灯保持最后的颜色），去掉托盘图标。</summary>
    protected override void ExitThreadCore()
    {
        if (!exiting)
        {
            exiting = true;
            refreshTimer.Stop();
            engine.Dispose();
            notifyIcon.Visible = false;
        }

        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            refreshTimer.Dispose();
            engine.Dispose();
            notifyIcon.Dispose();
            menu.Dispose();
            TrayIconPainter.Destroy(currentIcon);
        }

        base.Dispose(disposing);
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private void ShowBalloon(string text, ToolTipIcon icon) =>
        notifyIcon.ShowBalloonTip(balloonTimeoutMs, "HyperLight", text, icon);

    private void TryRefreshAutoStartPath()
    {
        try
        {
            AutoStart.RefreshPathIfEnabled();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException)
        {
            // 更新不了启动项的位置只影响下次开机自启，不影响现在运行，不打扰用户
        }
    }

    private static bool IsAutoStartEnabledSafe()
    {
        try
        {
            return AutoStart.IsEnabled();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";

    /// <summary>界面线程里没预料到的错误：提示一下，程序继续运行。同一时间只弹一个提示框。</summary>
    private static void ShowUnexpectedError(Exception ex)
    {
        if (showingError)
        {
            return;
        }

        showingError = true;
        try
        {
            MessageBox.Show(
                $"HyperLight 遇到意外错误，程序会继续运行。\n如果反复出现，请把下面的内容发给 Claude：\n\n{ex}",
                "HyperLight",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            showingError = false;
        }
    }
}
