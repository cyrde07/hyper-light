using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace HyperLight.Config;

/// <summary>
/// 程序配置。所有配置项、默认值、允许范围和说明，都只在这个文件里定义（唯一真相来源）。
/// 保存在 exe 旁边的 hyperlight.json，可以用记事本修改，改完重新启动程序生效。
/// </summary>
/// <remarks>
/// 加新配置项时只改这个文件：加一个属性（写上默认值和 Description 说明），再在 <see cref="Validated"/> 里写上允许范围。
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

    [Description("找不到壁纸多久后（毫秒）停止发送，让麦克风回到自带灯效。默认 3000。范围 0～60000")]
    public int WallpaperLostGraceMs { get; init; } = 3000;

    /// <summary>配置文件的位置：exe 旁边的 hyperlight.json。</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "hyperlight.json");

    private static readonly JsonSerializerOptions readOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

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
            TrySave(defaults, path, notes);
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

    /// <summary>把每一项的数值检查一遍，超出范围的改回默认值。</summary>
    private AppConfig Validated(List<string> notes)
    {
        var defaults = new AppConfig();
        return this with
        {
            CaptureIntervalMs = InRange(nameof(CaptureIntervalMs), CaptureIntervalMs, 100, 10_000, defaults.CaptureIntervalMs, notes),
            SendIntervalMs = InRange(nameof(SendIntervalMs), SendIntervalMs, 20, 100, defaults.SendIntervalMs, notes),
            ChangeThreshold = InRange(nameof(ChangeThreshold), ChangeThreshold, 0, 255, defaults.ChangeThreshold, notes),
            FadeMs = InRange(nameof(FadeMs), FadeMs, 0, 5_000, defaults.FadeMs, notes),
            WallpaperLostGraceMs = InRange(nameof(WallpaperLostGraceMs), WallpaperLostGraceMs, 0, 60_000, defaults.WallpaperLostGraceMs, notes),
        };
    }

    private static int InRange(string propertyName, int value, int min, int max, int fallback, List<string> notes)
    {
        if (value >= min && value <= max)
        {
            return value;
        }

        notes.Add($"配置项 {JsonName(propertyName)} = {value} 超出范围（{min}～{max}），这次使用默认值 {fallback}。");
        return fallback;
    }

    /// <summary>生成带中文说明的配置文件。写不进去只提示，不影响运行。</summary>
    private static void TrySave(AppConfig config, string path, List<string> notes)
    {
        try
        {
            // UTF-8 带 BOM：Windows 记事本打开中文说明不会乱码
            File.WriteAllText(path, ToCommentedJson(config), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            notes.Add($"已生成默认配置文件：{path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"生成配置文件失败（{ex.Message}），这次使用默认设置。");
        }
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
            string value = Convert.ToString(property.GetValue(config), CultureInfo.InvariantCulture) ?? "";
            string comma = i < properties.Length - 1 ? "," : "";
            json.AppendLine($"  // {description}");
            json.AppendLine($"  \"{JsonName(property.Name)}\": {value}{comma}");
        }

        json.AppendLine("}");
        return json.ToString();
    }

    private static string JsonName(string propertyName) => JsonNamingPolicy.CamelCase.ConvertName(propertyName);
}
