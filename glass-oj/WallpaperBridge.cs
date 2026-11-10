using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace GlassOJ;

// Read-only integration: no engine commands, desktop capture or user-data writes.
// Only the current media file is exposed, under a dedicated opaque media URL.
internal sealed class WallpaperBridge
{
    internal sealed record Media(string Path, string Kind, string Title, string Note)
    {
        public string Token => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path + File.GetLastWriteTimeUtc(Path).Ticks)))[..20];
    }
    readonly object gate = new();
    Media? current;
    string? token;
    public object GetState()
    {
        try
        {
            string? config = null;
            foreach (var name in new[] { "wallpaper64", "wallpaper32" })
            {
                foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    try
                    {
                        var folder = System.IO.Path.GetDirectoryName(process.MainModule?.FileName);
                        if (folder != null && File.Exists(System.IO.Path.Combine(folder, "config.json")))
                            config = System.IO.Path.Combine(folder, "config.json");
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
            var running = config != null;
            config ??= FindInstalledConfig();
            if (config == null) return Unavailable("未找到 Wallpaper Engine，显示默认背景。");
            var media = ReadSelection(config);
            if (media == null) return Unavailable("当前壁纸没有可读取的视频或预览，显示默认背景。");
            var key = media.Token;
            lock (gate) { current = media; token = key; }
            return new { available = true, kind = media.Kind, title = media.Title, note = media.Note + (running ? "" : " · 引擎未运行，使用上次选择"), url = "https://wallpaper.boyi.local/__wallpaper/" + key };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Unavailable("暂时无法读取壁纸，稍后会自动重试。");
        }
    }
    static string? FindInstalledConfig()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        steam ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        var libraries = new List<string> { steam };
        var vdf = System.IO.Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
        return libraries.Select(p => System.IO.Path.Combine(p, "steamapps", "common", "wallpaper_engine", "config.json")).FirstOrDefault(File.Exists);
    }
    object Unavailable(string note)
    {
        lock (gate) { current = null; token = null; }
        return new { available = false, note };
    }
    internal static Media? ReadSelection(string config)
    {
        using var stream = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(stream);
        var users = doc.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object)
            .OrderByDescending(p => string.Equals(p.Name, Environment.UserName, StringComparison.OrdinalIgnoreCase));
        foreach (var user in users)
        {
            if (!user.Value.TryGetProperty("general", out var general) ||
                !general.TryGetProperty("wallpaperconfig", out var configNode) ||
                !configNode.TryGetProperty("selectedwallpapers", out var selected)) continue;
            // Follow the engine's first configured display, without changing its layout.
            foreach (var monitor in selected.EnumerateObject())
            {
                if (!monitor.Value.TryGetProperty("file", out var f)) continue;
                var file = f.GetString();
                if (string.IsNullOrWhiteSpace(file) || !System.IO.Path.IsPathFullyQualified(file) || !File.Exists(file)) continue;
                var folder = System.IO.Path.GetDirectoryName(file)!;
                string title = System.IO.Path.GetFileNameWithoutExtension(file);
                string? preview = null;
                var project = System.IO.Path.Combine(folder, "project.json");
                if (File.Exists(project))
                {
                    using var meta = JsonDocument.Parse(File.ReadAllText(project));
                    if (meta.RootElement.TryGetProperty("title", out var t)) title = t.GetString() ?? title;
                    if (meta.RootElement.TryGetProperty("preview", out var p) && p.GetString() is string relative)
                    {
                        var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, relative));
                        if (candidate.StartsWith(System.IO.Path.GetFullPath(folder) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)) preview = candidate;
                    }
                }
                var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".mp4" or ".webm" or ".m4v") return new(file, "video", title, "跟随当前视频壁纸 · 静音独立播放");
                if (ext is ".jpg" or ".jpeg" or ".png" or ".webp") return new(file, "image", title, "跟随当前图片壁纸");
                if (preview != null && System.IO.Path.GetExtension(preview).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif")
                    return new(preview, "image", title, "当前为场景或网页壁纸，显示静态预览");
            }
        }
        return null;
    }
    public void Attach(CoreWebView2 web)
    {
        web.AddWebResourceRequestedFilter("https://wallpaper.boyi.local/*", CoreWebView2WebResourceContext.All);
        web.WebResourceRequested += (_, e) =>
        {
            Media? media; string? key;
            lock (gate) { media = current; key = token; }
            var uri = new Uri(e.Request.Uri);
            if (uri.Host != "wallpaper.boyi.local") return;
            if (media == null || uri.AbsolutePath != "/__wallpaper/" + key || e.Request.Method is not ("GET" or "HEAD"))
            {
                e.Response = web.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store"); return;
            }
            try
            {
                var length = new FileInfo(media.Path).Length;
                string? range = e.Request.Headers.Contains("Range") ? e.Request.Headers.GetHeader("Range") : null;
                if (!TryRange(range, length, out var start, out var end))
                {
                    e.Response = web.Environment.CreateWebResourceResponse(null, 416, "Range Not Satisfiable", $"Content-Range: bytes */{length}"); return;
                }
                var type = System.IO.Path.GetExtension(media.Path).ToLowerInvariant() switch
                {
                    ".webm" => "video/webm", ".mp4" or ".m4v" => "video/mp4", ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/jpeg"
                };
                var partial = range != null;
                var headers = $"Content-Type: {type}\r\nContent-Length: {end - start + 1}\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff";
                if (partial) headers += $"\r\nContent-Range: bytes {start}-{end}/{length}";
                var content = e.Request.Method == "HEAD" ? null : new FileSlice(media.Path, start, end - start + 1);
                e.Response = web.Environment.CreateWebResourceResponse(content, partial ? 206 : 200, partial ? "Partial Content" : "OK", headers);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                e.Response = web.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
            }
        };
    }
    internal static bool TryRange(string? range, long length, out long start, out long end)
    {
        start = 0; end = length - 1;
        if (length <= 0) return false;
        if (range == null) return true;
        if (!range.StartsWith("bytes=", StringComparison.Ordinal) || range.Contains(',')) return false;
        var parts = range[6..].Split('-');
        if (parts.Length != 2) return false;
        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, length - suffix); return true;
        }
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= length) return false;
        if (parts[1].Length > 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start) return false;
            end = Math.Min(end, length - 1);
        }
        return true;
    }
    sealed class FileSlice : Stream
    {
        readonly FileStream file;
        readonly long offset, size;
        public FileSlice(string path, long offset, long size) { file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); this.offset = offset; this.size = size; file.Position = offset; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => size;
        public override long Position { get => file.Position - offset; set { if (value < 0 || value > size) throw new ArgumentOutOfRangeException(nameof(value)); file.Position = offset + value; } }
        public override int Read(byte[] buffer, int offset, int count) => file.Read(buffer, offset, (int)Math.Min(count, size - Position));
        public override long Seek(long offset, SeekOrigin origin) { Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, SeekOrigin.End => size + offset, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }; return Position; }
        protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
