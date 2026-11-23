using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyperLight.ColorProcessing;
using HyperLight.Device;

namespace HyperLight.Config;

/// <summary>
/// 程序配置。所有配置项、默认值、允许范围和说明，都只在这个文件里定义（唯一真相来源）。
/// 保存在 exe 旁边的 hyperlight.json，可以用记事本修改；托盘菜单里的开关也会改这个文件。
/// </summary>
/// <remarks>
/// 加新配置项时只改这个文件：加一个属性（写上默认值和 Description 说明），数值类的再在 <see cref="Validated"/> 里写上允许范围。
/// 生成的 json 文件会自动带上每一项的中文说明。
/// </remarks>
internal sealed record AppConfig
{
    [Description("截图间隔（毫秒）：多久截一次壁纸。默认 500，也就是每秒 2 次。范围 100～10000")]
    public int CaptureIntervalMs { get; init; } = 500;

    [Description("发送间隔（毫秒）：多久给麦克风发一次颜色。麦克风一停止接收就会恢复自带灯效，所以要一直发。默认 33，约每秒 30 次。范围 20～100")]
    public int SendIntervalMs { get; init; } = 33;

    [Description("颜色变化门槛（0～255）：新颜色和当前颜色的红、绿、蓝任一项相差达到这个值，才算变了。数值越大越不容易变。默认 12")]
    public int ChangeThreshold { get; init; } = 12;

    [Description("渐变时间（毫秒）：颜色变了以后，花多久过渡到新颜色。0 表示立刻切换。默认 400。范围 0～5000")]
    public int FadeMs { get; init; } = 400;

    [Description("确认次数：颜色变化够大时，新颜色要连续截到几次才切换。默认 2（截图间隔 500 毫秒时约 1 秒），换壁纸时一闪而过的过渡画面就不会传到麦克风。1 表示截到就换。范围 1～5")]
    public int ConfirmCaptures { get; init; } = 2;

    [Description("找不到壁纸多久后（毫秒）停止发送，让麦克风回到自带灯效。默认 3000。范围 0～60000")]
    public int WallpaperLostGraceMs { get; init; } = 3000;

    [Description("增强颜色：提高饱和度，模仿 Wallpaper Engine 的「增强 LED 颜色」。true 打开，false 关闭。默认 true")]
    public bool BoostColors { get; init; } = true;

    [Description("增强倍数：增强颜色打开时，饱和度乘以多少。1.0 表示不变，越大颜色越鲜艳。默认 1.5。范围 1.0～3.0")]
    public double SaturationBoost { get; init; } = 1.5;

    [Description("取色方式：topBottom 上下（上半张→上灯区，下半张→下灯区）；leftRight 左右（左半张→上灯区，右半张→下灯区）；whole 整张壁纸（两个灯区同色）。默认 topBottom")]
    public ZoneLayout ZoneLayout { get; init; } = ZoneLayout.TopBottom;

    [Description("麦克风：true 跟着壁纸变色；false 不控制，麦克风显示自带灯效。默认 true")]
    public bool MicEnabled { get; init; } = true;

    [Description("机箱灯（主板 ARGB_V2_1 上的机箱风扇和灯条）：true 跟着壁纸变色；false 不再改变，停在最后的颜色。默认 true")]
    public bool CaseEnabled { get; init; } = true;

    [Description("冷排灯（主板 ARGB_V2_2 上的水冷）：true 跟着壁纸变色；false 不再改变，停在最后的颜色。默认 true")]
    public bool RadiatorEnabled { get; init; } = true;

    [Description("机箱灯取色：upper 上半区（用上灯区的颜色）；lower 下半区；average 上下平均。默认 lower")]
    public ColorSource CaseColorSource { get; init; } = ColorSource.Lower;

    [Description("冷排灯取色：upper 上半区（用上灯区的颜色）；lower 下半区；average 上下平均。默认 upper（冷排在机箱顶部）")]
    public ColorSource RadiatorColorSource { get; init; } = ColorSource.Upper;

    [Description("机箱灯的灯珠数：发颜色时发给多少颗灯。数多了没关系，数少了后面的灯不会变色。默认 256（主板接口的上限）。范围 1～256")]
    public int CaseLedCount { get; init; } = 256;

    [Description("冷排灯的灯珠数：发颜色时发给多少颗灯。数多了没关系，数少了后面的灯不会变色。默认 256（主板接口的上限）。范围 1～256")]
    public int RadiatorLedCount { get; init; } = 256;

    /// <summary>配置文件的位置：exe 旁边的 hyperlight.json。</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "hyperlight.json");

    /// <summary>取色方式这类选项在文件里写成文字（比如 "leftRight"），不写成数字。</summary>
    private static readonly JsonStringEnumConverter enumAsText = new(JsonNamingPolicy.CamelCase, allowIntegerValues: false);

    private static readonly JsonSerializerOptions readOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { enumAsText },
    };

    private static readonly JsonSerializerOptions writeValueOptions = new() { Converters = { enumAsText } };

    /// <summary>
    /// 读取配置。文件不存在就用默认值，并生成一份带说明的配置文件；
    /// 内容有问题就用默认值，原因写进 notes。本方法不会因为配置文件的问题抛出异常。
    /// </summary>
    public static AppConfig Load(string path, out List<string> notes)
    {
        notes = new List<string>();
        if (!File.Exists(path))
        {
            var defaults = new AppConfig();
            if (TrySave(defaults, path, out string? error))
            {
                notes.Add($"已生成默认配置文件：{path}");
            }
            else
            {
                notes.Add($"生成配置文件失败（{error}），这次使用默认设置。");
            }

            return defaults;
        }

        AppConfig? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), readOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            notes.Add($"配置文件读取失败（{ex.Message}），这次使用默认设置。可以删掉 {path}，程序下次启动会重新生成。");
            return new AppConfig();
        }

        if (loaded is null)
        {
            notes.Add("配置文件是空的，这次使用默认设置。");
            return new AppConfig();
        }

        return loaded.Validated(notes);
    }

    /// <summary>
    /// 把配置写成带中文说明的 json 文件（托盘菜单改开关时用）。
    /// 写不进去时返回 false，原因放在 error 里，不抛异常。
    /// </summary>
    public static bool TrySave(AppConfig config, string path, out string? error)
    {
        try
        {
            // UTF-8 带 BOM：Windows 记事本打开中文说明不会乱码
            File.WriteAllText(path, ToCommentedJson(config), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>把每一项的数值检查一遍，超出范围的改回默认值。开关（true / false）和取色方式、取色来源（读取时已检查）不需要再检查。</summary>
    private AppConfig Validated(List<string> notes)
    {
        var defaults = new AppConfig();
        return this with
        {
            CaptureIntervalMs = InRange(nameof(CaptureIntervalMs), CaptureIntervalMs, 100, 10_000, defaults.CaptureIntervalMs, notes),
            SendIntervalMs = InRange(nameof(SendIntervalMs), SendIntervalMs, 20, 100, defaults.SendIntervalMs, notes),
            ChangeThreshold = InRange(nameof(ChangeThreshold), ChangeThreshold, 0, 255, defaults.ChangeThreshold, notes),
            FadeMs = InRange(nameof(FadeMs), FadeMs, 0, 5_000, defaults.FadeMs, notes),
            ConfirmCaptures = InRange(nameof(ConfirmCaptures), ConfirmCaptures, 1, 5, defaults.ConfirmCaptures, notes),
            WallpaperLostGraceMs = InRange(nameof(WallpaperLostGraceMs), WallpaperLostGraceMs, 0, 60_000, defaults.WallpaperLostGraceMs, notes),
            SaturationBoost = InRange(nameof(SaturationBoost), SaturationBoost, 1.0, 3.0, defaults.SaturationBoost, notes),
            CaseLedCount = InRange(nameof(CaseLedCount), CaseLedCount, 1, MotherboardLightProtocol.MaxLedsPerZone, defaults.CaseLedCount, notes),
            RadiatorLedCount = InRange(nameof(RadiatorLedCount), RadiatorLedCount, 1, MotherboardLightProtocol.MaxLedsPerZone, defaults.RadiatorLedCount, notes),
        };
    }

    private static T InRange<T>(string propertyName, T value, T min, T max, T fallback, List<string> notes)
        where T : IComparable<T>
    {
        if (value.CompareTo(min) >= 0 && value.CompareTo(max) <= 0)
        {
            return value;
        }

        notes.Add($"配置项 {JsonName(propertyName)} = {value} 超出范围（{min}～{max}），这次使用默认值 {fallback}。");
        return fallback;
    }

    /// <summary>
    /// 把配置写成 json 文本，每一项上面加一行 // 中文说明（读取时会跳过这些说明）。
    /// 说明文字来自属性上的 Description，所以说明和默认值不会对不上。
    /// </summary>
    private static string ToCommentedJson(AppConfig config)
    {
        PropertyInfo[] properties = typeof(AppConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(property => property.MetadataToken)
            .ToArray();

        var json = new StringBuilder();
        json.AppendLine("{");
        for (int i = 0; i < properties.Length; i++)
        {
            PropertyInfo property = properties[i];
            string description = property.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

            // 用 json 序列化器写数值，保证格式正确（比如 true 是小写、小数点不受系统语言影响）
            string value = JsonSerializer.Serialize(property.GetValue(config), property.PropertyType, writeValueOptions);
            string comma = i < properties.Length - 1 ? "," : "";
            json.AppendLine($"  // {description}");
            json.AppendLine($"  \"{JsonName(property.Name)}\": {value}{comma}");
        }

        json.AppendLine("}");
        return json.ToString();
    }

    private static string JsonName(string propertyName) => JsonNamingPolicy.CamelCase.ConvertName(propertyName);
}
