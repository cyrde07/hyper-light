using System.Text;

namespace HyperLight.Commands;

/// <summary>把诊断报告存成 exe 旁边的文本文件，方便发给别人看。list 和 board 命令共用。</summary>
internal static class ReportFile
{
    /// <summary>
    /// 保存报告，文件名是"前缀-日期-时间.txt"，并在窗口里提示保存结果。
    /// 保存失败不算错误，只提示用户直接复制窗口里的内容。
    /// </summary>
    public static void SaveNextToExe(string prefix, string report)
    {
        string fileName = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        string path = Path.Combine(AppContext.BaseDirectory, fileName);
        try
        {
            // UTF-8 带 BOM：Windows 记事本打开中文不会乱码
            File.WriteAllText(path, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Console.WriteLine($"报告已保存：{path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"报告保存失败（{ex.Message}），请直接复制窗口里的内容。");
        }
    }
}
