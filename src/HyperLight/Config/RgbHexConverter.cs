using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyperLight.Config;

/// <summary>
/// 颜色在设置文件里写成 "FF69B4" 这种 6 位十六进制文字（和命令行、托盘显示的写法一样），方便用记事本改。
/// 格式不对时抛出 JsonException，和其他配置项一样：整个配置文件改用默认值，并提示原因。
/// </summary>
internal sealed class RgbHexConverter : JsonConverter<Rgb>
{
    public override Rgb Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (!Rgb.TryParseHex(text, out Rgb color))
        {
            throw new JsonException($"颜色要写成 \"FF69B4\" 这种 6 位十六进制文字，收到的是：{text ?? reader.TokenType.ToString()}");
        }

        return color;
    }

    public override void Write(Utf8JsonWriter writer, Rgb value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
