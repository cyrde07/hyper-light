using System.Text;

namespace HyperLight.Diagnostics;

/// <summary>
/// 卡顿记录：出现异常情况（截图很慢、发送中断、壁纸窗口变了、颜色突变、麦克风断开等）时，
/// 把时间和情况记到 exe 旁边的 hyperlight-log.txt。平时什么都不写，只有出事时才写一行。
/// 用来排查"灯暗一下、屏幕卡一下"这类偶发问题：出事后看最后几条，就知道那一刻发生了什么。
/// </summary>
/// <remarks>
/// 文件超过 1 MB 时，旧内容改名为 hyperlight-log.old.txt（只保留一份旧的），不会越积越大。
/// 两个线程都会写，内部加锁。写不进去（比如文件被占用）就算了，不影响同步。
/// </remarks>
internal static class StallLog
{
    private const long maxBytes = 1024 * 1024;

    private static readonly object gate = new();

    /// <summary>记录文件的位置。</summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "hyperlight-log.txt");

    private static string OldFilePath => Path.Combine(AppContext.BaseDirectory, "hyperlight-log.old.txt");

    /// <summary>写一行记录：时间（精确到毫秒）+ 类别 + 内容。</summary>
    public static void Write(string category, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{category}] {message}{Environment.NewLine}";
        lock (gate)
        {
            try
            {
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > maxBytes)
                {
                    File.Move(FilePath, OldFilePath, overwrite: true);
                }

                // UTF-8 带 BOM：新文件开头会写 BOM，Windows 记事本打开中文不会乱码
                File.AppendAllText(FilePath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 写不进记录不影响同步，忽略
            }
        }
    }
}
