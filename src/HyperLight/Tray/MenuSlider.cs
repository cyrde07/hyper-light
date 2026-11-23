namespace HyperLight.Tray;

/// <summary>
/// 托盘菜单里的一个 0～100 滑动条：上面一行说明文字，下面是滑动条。
/// 拖动时马上更新文字；停下一会儿才通知"值改了"，拖动过程中不反复重启同步。
/// 加大差异、主板灯颜色加强都用它。
/// </summary>
internal sealed class MenuSlider : IDisposable
{
    /// <summary>拖动停下多久后才生效（毫秒）。</summary>
    private const int applyDelayMs = 400;

    private readonly TrackBar slider;
    private readonly System.Windows.Forms.Timer applyTimer;
    private readonly Func<int, string> textOf;

    /// <summary>程序自己在设置滑动条的位置（不是用户拖的），这时不要当成修改。</summary>
    private bool settingValue;

    /// <param name="textOf">根据当前值生成说明文字。</param>
    /// <param name="onChanged">用户拖动停下后，用新值调用。</param>
    public MenuSlider(Func<int, string> textOf, Action<int> onChanged)
    {
        this.textOf = textOf;

        Label = new ToolStripMenuItem(textOf(0)) { Enabled = false };
        slider = new TrackBar
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
        slider.ValueChanged += (_, _) => OnSliderMoved();
        Host = new ToolStripControlHost(slider) { AutoSize = false, Width = 230, Height = 34 };

        applyTimer = new System.Windows.Forms.Timer { Interval = applyDelayMs };
        applyTimer.Tick += (_, _) =>
        {
            applyTimer.Stop();
            onChanged(slider.Value);
        };
    }

    /// <summary>说明文字那一行（放进菜单）。</summary>
    public ToolStripMenuItem Label { get; }

    /// <summary>滑动条那一行（放进菜单）。</summary>
    public ToolStripControlHost Host { get; }

    /// <summary>能不能拖。</summary>
    public bool Enabled
    {
        get => slider.Enabled;
        set => slider.Enabled = value;
    }

    /// <summary>按设置显示当前值（不会当成用户修改）。</summary>
    public void Show(int value)
    {
        settingValue = true;
        slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        settingValue = false;
        Label.Text = textOf(slider.Value);
    }

    /// <summary>停掉还没生效的修改（退出程序时用）。</summary>
    public void Stop() => applyTimer.Stop();

    public void Dispose() => applyTimer.Dispose();

    private void OnSliderMoved()
    {
        if (settingValue)
        {
            return;
        }

        Label.Text = textOf(slider.Value);
        applyTimer.Stop();
        applyTimer.Start();
    }
}
