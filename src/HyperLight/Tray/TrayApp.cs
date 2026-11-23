using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Security;
using HyperLight.ColorProcessing;
using HyperLight.Config;

namespace HyperLight.Tray;

/// <summary>
/// 托盘程序（M4）：屏幕右下角一个小圆点图标，上下两半显示当前的上、下两个颜色。
/// 右键菜单：状态（麦克风、主板）、暂停 / 恢复、模式（跟壁纸 / 自由选色）、自由选色的颜色、增强颜色、取色方式、
/// 麦克风 / 机箱灯 / 冷排灯 / 主板灯的开关，机箱灯和冷排灯的取色和灯效、加大差异（滑动条）、主板灯的取色、
/// 开机自启、打开设置文件、重新读取设置、退出。
/// </summary>
/// <remarks>
/// 界面只在主线程（界面线程）里操作。同步工作在 <see cref="SyncEngine"/> 自己的线程里进行，
/// 界面每 0.5 秒读一次它的状态快照来刷新图标和菜单，两边不直接互相调用。
/// 退出或暂停时停止发送：麦克风马上回到自带灯效，主板上的灯（机箱灯、冷排灯、主板灯）保持最后的颜色。
/// </remarks>
internal sealed class TrayApp : ApplicationContext
{
    private const int refreshIntervalMs = 500;

    /// <summary>拖动"加大差异"滑动条停下多久后才生效（拖动过程中不反复重启同步）。</summary>
    private const int spreadApplyDelayMs = 400;
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
    private readonly Dictionary<ColorMode, ToolStripMenuItem> modeItems = new();
    private readonly ToolStripMenuItem freeMicColorItem;
    private readonly ToolStripMenuItem freeCaseColorItem;
    private readonly ToolStripMenuItem freeCaseColor2Item;
    private readonly ToolStripMenuItem freeRadiatorColorItem;
    private readonly ToolStripMenuItem freeRadiatorColor2Item;
    private readonly ToolStripMenuItem freeOnboardColorItem;
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
    private readonly ToolStripMenuItem onboardItem;
    private readonly ToolStripMenuItem onboardSourceMenu;
    private readonly Dictionary<ColorSource, ToolStripMenuItem> onboardSourceItems = new();
    private readonly ToolStripMenuItem spreadLabel;
    private readonly TrackBar spreadSlider;
    private readonly System.Windows.Forms.Timer spreadApplyTimer;
    private readonly ToolStripMenuItem autoStartItem;
    private readonly System.Windows.Forms.Timer refreshTimer;

    private AppConfig config;
    private SyncEngine engine;
    private Icon currentIcon;
    private (ZoneColors? Colors, bool Paused) shownIconState;
    private bool ngenuityWarned;
    private bool openRgbWarned;

    /// <summary>刚点的是开关或多选一的选项：这次点击后菜单不要关，方便连续改好几个。</summary>
    private bool keepMenuOpen;

    /// <summary>程序自己在设置滑动条的位置（不是用户拖的），这时不要当成修改。</summary>
    private bool updatingSlider;
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
        pauseItem = new ToolStripMenuItem("暂停同步", null, KeepOpen(TogglePause));
        boostItem = new ToolStripMenuItem("增强颜色（模仿 Wallpaper Engine）", null, KeepOpen(ToggleBoost));
        ToolStripMenuItem modeMenu = BuildChoiceMenu(
            "模式", ColorModeRules.All, ColorModeRules.TextOf, modeItems,
            mode => config.ColorMode != mode, mode => config with { ColorMode = mode });
        freeMicColorItem = new ToolStripMenuItem("麦克风颜色…", null,
            CloseAfter(() => PickColor(config.FreeMicColor, color => config with { FreeMicColor = color })));
        freeCaseColorItem = new ToolStripMenuItem("机箱灯颜色…", null,
            CloseAfter(() => PickColor(config.FreeCaseColor, color => config with { FreeCaseColor = color })));
        freeCaseColor2Item = new ToolStripMenuItem("机箱灯第二个颜色（双色、流动用）…", null,
            CloseAfter(() => PickColor(config.FreeCaseColor2, color => config with { FreeCaseColor2 = color })));
        freeRadiatorColorItem = new ToolStripMenuItem("冷排灯颜色…", null,
            CloseAfter(() => PickColor(config.FreeRadiatorColor, color => config with { FreeRadiatorColor = color })));
        freeRadiatorColor2Item = new ToolStripMenuItem("冷排灯第二个颜色（双色、流动用）…", null,
            CloseAfter(() => PickColor(config.FreeRadiatorColor2, color => config with { FreeRadiatorColor2 = color })));
        freeOnboardColorItem = new ToolStripMenuItem("主板灯颜色…", null,
            CloseAfter(() => PickColor(config.FreeOnboardColor, color => config with { FreeOnboardColor = color })));
        var freeColorMenu = new ToolStripMenuItem("自由选色");
        freeColorMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            freeMicColorItem,
            freeCaseColorItem,
            freeCaseColor2Item,
            freeRadiatorColorItem,
            freeRadiatorColor2Item,
            freeOnboardColorItem,
        });
        layoutMenu = new ToolStripMenuItem("取色方式");
        foreach (ZoneLayout layout in new[] { ZoneLayout.TopBottom, ZoneLayout.LeftRight, ZoneLayout.Whole })
        {
            var item = new ToolStripMenuItem(ZoneLayoutText.Of(layout), null, KeepOpen(() => SelectLayout(layout)));
            layoutItems[layout] = item;
            layoutMenu.DropDownItems.Add(item);
        }
        micItem = new ToolStripMenuItem("麦克风", null, KeepOpen(() => ApplyConfig(config with { MicEnabled = !config.MicEnabled })));
        caseItem = new ToolStripMenuItem("机箱灯", null, KeepOpen(() => ApplyConfig(config with { CaseEnabled = !config.CaseEnabled })));
        radiatorItem = new ToolStripMenuItem("冷排灯", null, KeepOpen(() => ApplyConfig(config with { RadiatorEnabled = !config.RadiatorEnabled })));
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
        onboardItem = new ToolStripMenuItem("主板灯", null, KeepOpen(() => ApplyConfig(config with { OnboardEnabled = !config.OnboardEnabled })));
        onboardSourceMenu = BuildChoiceMenu(
            "主板灯取色", ColorSourceRules.All, ColorSourceRules.TextOf, onboardSourceItems,
            source => config.OnboardColorSource != source, source => config with { OnboardColorSource = source });
        spreadLabel = new ToolStripMenuItem("加大差异") { Enabled = false };
        spreadSlider = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            SmallChange = 5,
            LargeChange = 10,
            AutoSize = false,
            Width = 220,
            Height = 32,
        };
        spreadSlider.ValueChanged += (_, _) => OnSpreadSliderChanged();
        var spreadSliderHost = new ToolStripControlHost(spreadSlider) { AutoSize = false, Width = 230, Height = 34 };
        spreadApplyTimer = new System.Windows.Forms.Timer { Interval = spreadApplyDelayMs };
        spreadApplyTimer.Tick += (_, _) => ApplySpreadFromSlider();
        autoStartItem = new ToolStripMenuItem("开机自启", null, KeepOpen(ToggleAutoStart));

        menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            statusItem,
            boardStatusItem,
            new ToolStripSeparator(),
            pauseItem,
            modeMenu,
            freeColorMenu,
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
            spreadLabel,
            spreadSliderHost,
            onboardItem,
            onboardSourceMenu,
            new ToolStripSeparator(),
            autoStartItem,
            new ToolStripMenuItem("打开设置文件", null, CloseAfter(OpenConfigFile)),
            new ToolStripMenuItem("重新读取设置", null, CloseAfter(ReloadConfig)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("退出", null, CloseAfter(ExitThread)),
        });
        AttachKeepOpen(menu);

        // 每次打开菜单都从"点了就关"开始
        menu.Opening += (_, _) => keepMenuOpen = false;

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
        string state = DescribeState(status, config);
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
    private static string DescribeState(SyncStatus status, AppConfig config)
    {
        if (status.Paused)
        {
            return "已暂停（麦克风显示自带灯效，主板上的灯保持当前颜色）";
        }

        if (status.Target is not ZoneColors target)
        {
            return status.LastError ?? status.WallpaperState;
        }

        string doing = config.ColorMode == ColorMode.Free ? "自由选色" : "同步中";
        if (!config.MicEnabled)
        {
            return $"{doing}：{target}（麦克风已关闭）";
        }

        return status.MicConnected ? $"{doing}：{target}" : status.MicState;
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
        onboardItem.Checked = config.OnboardEnabled;
        CheckOnly(onboardSourceItems, config.OnboardColorSource);
        CheckOnly(caseSourceItems, config.CaseColorSource);
        CheckOnly(radiatorSourceItems, config.RadiatorColorSource);
        CheckOnly(caseEffectItems, config.CaseEffect);
        CheckOnly(radiatorEffectItems, config.RadiatorEffect);

        updatingSlider = true;
        spreadSlider.Value = config.TwoColorSpread;
        updatingSlider = false;
        spreadLabel.Text = SpreadText(config.TwoColorSpread);

        // 双色、流动灯效用的是固定的两块区域，取色只在单色时有用：不用时变灰，免得以为点了没反应
        // 自由选色时不截图，和壁纸有关的选项（增强颜色、取色方式、取色、加大差异）都用不到，一起变灰
        bool wallpaperMode = config.ColorMode == ColorMode.Wallpaper;
        boostItem.Enabled = wallpaperMode;
        layoutMenu.Enabled = wallpaperMode;
        spreadSlider.Enabled = wallpaperMode;
        caseSourceMenu.Enabled = wallpaperMode && config.CaseEffect == LightEffect.Solid;
        radiatorSourceMenu.Enabled = wallpaperMode && config.RadiatorEffect == LightEffect.Solid;
        onboardSourceMenu.Enabled = wallpaperMode;

        CheckOnly(modeItems, config.ColorMode);
        SetSwatch(freeMicColorItem, config.FreeMicColor);
        SetSwatch(freeCaseColorItem, config.FreeCaseColor);
        SetSwatch(freeCaseColor2Item, config.FreeCaseColor2);
        SetSwatch(freeRadiatorColorItem, config.FreeRadiatorColor);
        SetSwatch(freeRadiatorColor2Item, config.FreeRadiatorColor2);
        SetSwatch(freeOnboardColorItem, config.FreeOnboardColor);

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
            var item = new ToolStripMenuItem(textOf(choice), null, KeepOpen(() =>
            {
                if (isChange(choice))
                {
                    ApplyConfig(withChoice(choice));
                }
            }));
            items[choice] = item;
            choiceMenu.DropDownItems.Add(item);
        }

        return choiceMenu;
    }

    /// <summary>
    /// 包装开关、多选一选项的点击动作：点完以后菜单不关，勾选马上更新，可以接着改别的。
    /// 「打开设置文件」「重新读取设置」「退出」这些不用它，点完照常关闭菜单。
    /// </summary>
    /// <remarks>
    /// 标记不在点击处理完后自动清掉：改设置时要等同步线程停下，等待期间 Windows 会处理排队的消息，
    /// 如果用排队的方式清标记，会在菜单关闭之前就被清掉。所以改成在打开菜单时、点"会关闭菜单的选项"时清。
    /// </remarks>
    private EventHandler KeepOpen(Action action) => (_, _) =>
    {
        keepMenuOpen = true;
        action();
    };

    /// <summary>包装「打开设置文件」「重新读取设置」「退出」这类选项：点完照常关闭菜单。</summary>
    private EventHandler CloseAfter(Action action) => (_, _) =>
    {
        keepMenuOpen = false;
        action();
    };

    /// <summary>给菜单和它所有的子菜单挂上"点了开关不关闭"的处理。</summary>
    private void AttachKeepOpen(ToolStripDropDown dropDown)
    {
        dropDown.Closing += (_, e) =>
        {
            if (keepMenuOpen && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
            {
                e.Cancel = true;
            }
        };

        foreach (ToolStripItem item in dropDown.Items)
        {
            if (item is ToolStripMenuItem { HasDropDownItems: true } parent)
            {
                AttachKeepOpen(parent.DropDown);
            }
        }
    }

    /// <summary>
    /// 弹出 Windows 的选色窗口，从 <paramref name="current"/> 开始选。点了确定就保存新颜色并马上生效；点取消什么都不改。
    /// </summary>
    private void PickColor(Rgb current, Func<Rgb, AppConfig> withColor)
    {
        using var dialog = new ColorDialog
        {
            Color = Color.FromArgb(current.R, current.G, current.B),
            FullOpen = true,
            AnyColor = true,
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            Color picked = dialog.Color;
            ApplyConfig(withColor(new Rgb(picked.R, picked.G, picked.B)));
        }
    }

    /// <summary>在菜单项前面画一个小色块，显示现在选的颜色。旧的色块图片会被释放。</summary>
    private static void SetSwatch(ToolStripMenuItem item, Rgb color)
    {
        var swatch = new Bitmap(16, 16);
        using (Graphics graphics = Graphics.FromImage(swatch))
        {
            using var fill = new SolidBrush(Color.FromArgb(color.R, color.G, color.B));
            graphics.FillRectangle(fill, 1, 1, 14, 14);
            graphics.DrawRectangle(Pens.Gray, 1, 1, 13, 13);
        }

        Image? old = item.Image;
        item.Image = swatch;
        old?.Dispose();
    }

    /// <summary>拖动滑动条时：马上更新文字，停下一会儿再生效（拖动过程中不反复重启同步）。</summary>
    private void OnSpreadSliderChanged()
    {
        if (updatingSlider)
        {
            return;
        }

        spreadLabel.Text = SpreadText(spreadSlider.Value);
        spreadApplyTimer.Stop();
        spreadApplyTimer.Start();
    }

    private void ApplySpreadFromSlider()
    {
        spreadApplyTimer.Stop();
        if (spreadSlider.Value != config.TwoColorSpread)
        {
            ApplyConfig(config with { TwoColorSpread = spreadSlider.Value });
        }
    }

    /// <summary>滑动条上面那行说明文字。</summary>
    private static string SpreadText(int spread) =>
        spread == 0
            ? "加大差异：关闭（往右拖打开，双色、流动时用）"
            : $"加大差异：{spread}（拖到最左边关闭，双色、流动时用）";

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
            spreadApplyTimer.Stop();
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
            spreadApplyTimer.Dispose();
            engine.Dispose();
            notifyIcon.Dispose();
            foreach (ToolStripMenuItem item in new[]
            {
                freeMicColorItem, freeCaseColorItem, freeCaseColor2Item, freeRadiatorColorItem, freeRadiatorColor2Item, freeOnboardColorItem,
            })
            {
                item.Image?.Dispose();
            }

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
