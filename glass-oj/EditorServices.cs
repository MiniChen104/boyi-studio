using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GlassOJ;

public partial class Backend
{
    static int NormalizeIndent(int size) => size is 2 or 4 or 8 ? size : 4;

    async Task<object> FormatCode(string code, int cursor)
    {
        if (code.Length > 500000) throw new Exception("代码超过50万字符。");
        cursor = Math.Clamp(cursor, 0, code.Length);
        if (cursor > 0 && cursor < code.Length && char.IsHighSurrogate(code[cursor - 1]) && char.IsLowSurrogate(code[cursor])) cursor--;
        if (string.IsNullOrWhiteSpace(code)) return new { code, cursor };
        var executable = Path.Combine(AppContext.BaseDirectory, "tools", "clang-format.exe");
        if (!File.Exists(executable)) throw new Exception("缺少代码整理组件，请使用完整的软件包。");
        var indent = NormalizeIndent(state.Settings.IndentSize);
        var style = $"{{BasedOnStyle: LLVM, BreakBeforeBraces: Allman, AllowShortFunctionsOnASingleLine: None, AllowShortBlocksOnASingleLine: Never, AllowShortIfStatementsOnASingleLine: Never, AllowShortLoopsOnASingleLine: false, IndentWidth: {indent}, ContinuationIndentWidth: {indent}, TabWidth: {indent}, UseTab: Never, ColumnLimit: 100, SortIncludes: Never, ReflowComments: Never}}";
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "--assume-filename=main.cpp", "--style=" + style,
            "--cursor=" + Encoding.UTF8.GetByteCount(code.AsSpan(0, cursor)), "--fail-on-incomplete-format" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new Exception("无法启动代码整理组件。");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.StandardInput.WriteAsync(code.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw new Exception("代码整理超时，请缩短代码后重试。");
        }
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new Exception("暂时无法整理，请检查代码是否完整。\n" + error[..Math.Min(error.Length, 1500)]);
        var headerEnd = output.IndexOf('\n');
        if (headerEnd < 0) throw new Exception("代码整理组件返回了无效结果。");
        using var header = JsonDocument.Parse(output[..headerEnd]);
        if (header.RootElement.TryGetProperty("IncompleteFormat", out var incomplete) && incomplete.GetBoolean())
            throw new Exception("代码尚未完整，请补齐后再整理。");
        var formatted = output[(headerEnd + 1)..];
        var bytes = Encoding.UTF8.GetBytes(formatted);
        var byteCursor = Math.Clamp(header.RootElement.GetProperty("Cursor").GetInt32(), 0, bytes.Length);
        return new { code = formatted, cursor = Encoding.UTF8.GetCharCount(bytes, 0, byteCursor) };
    }
}
