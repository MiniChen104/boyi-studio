using GlassOJ;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var root = Path.Combine(Path.GetTempPath(), "BoyiWallpaper-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
var pixels = Enumerable.Repeat((byte)120, 32 * 18 * 4).ToArray();
var bitmap = BitmapSource.Create(32, 18, 96, 96, PixelFormats.Bgr32, null, pixels, 32 * 4);
var encoder = new PngBitmapEncoder();
encoder.Frames.Add(BitmapFrame.Create(bitmap));
using var pngStream = new MemoryStream();
encoder.Save(pngStream);
var png = pngStream.ToArray();
byte[] Texture(int flags = 2, int compressed = 0, int width = 32, byte[]? image = null)
{
    using var output = new MemoryStream();
    using var writer = new BinaryWriter(output);
    writer.Write(Encoding.ASCII.GetBytes("TEXV0005\0TEXI0001\0"));
    foreach (var value in new[] { 0, flags, 32, 32, width, 18, 0 }) writer.Write(value);
    writer.Write(Encoding.ASCII.GetBytes("TEXB0003\0"));
    foreach (var value in new[] { 1, 13, 1, width, 18, compressed, 0 }) writer.Write(value);
    var data = image ?? png;
    writer.Write(data.Length); writer.Write(data);
    return output.ToArray();
}
byte[] Package(byte[] texture, string? scene = null)
{
    var entries = new Dictionary<string, byte[]>
    {
        ["scene.json"] = Encoding.UTF8.GetBytes(scene ?? """
            {"general":{"orthogonalprojection":{"width":32,"height":18}},"objects":[{"image":"models/image.json","origin":"16 9 0","size":"32 18"}]}
            """),
        ["models/image.json"] = Encoding.UTF8.GetBytes("{\"material\":\"materials/image.json\"}"),
        ["materials/image.json"] = Encoding.UTF8.GetBytes("{\"passes\":[{\"textures\":[\"image\"]}]}"),
        ["materials/image.tex"] = texture
    };
    using var output = new MemoryStream();
    using var writer = new BinaryWriter(output);
    void Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
    Text("PKGV0019"); writer.Write(entries.Count);
    int offset = 0;
    foreach (var entry in entries) { Text(entry.Key); writer.Write(offset); writer.Write(entry.Value.Length); offset += entry.Value.Length; }
    foreach (var entry in entries) writer.Write(entry.Value);
    return output.ToArray();
}
var path = Path.Combine(root, "scene.pkg");
File.WriteAllBytes(path, Package(Texture()));
var reader = new SceneWallpaper();
var image = reader.Read(path);
Check(image != null && image.Bytes.SequenceEqual(png) && image.MimeType == "image/png", "full-canvas embedded PNG retained at original resolution");
Check(ReferenceEquals(reader.Read(path), image), "unchanged wallpaper reuses decoded image cache");
var invalidScenes = new[]
{
    "{}",
    "{\"general\":{\"orthogonalprojection\":{\"width\":32,\"height\":18}},\"objects\":[]}",
    "{\"general\":{\"orthogonalprojection\":{\"width\":32,\"height\":18}},\"objects\":[{\"image\":\"x\"},{\"image\":\"y\"}]}",
    "{\"general\":{\"orthogonalprojection\":{\"width\":32,\"height\":18}},\"objects\":[{\"image\":\"../external\",\"origin\":\"16 9 0\",\"size\":\"32 18\"}]}"
};
foreach (var scene in invalidScenes)
{
    var test = Path.Combine(root, Guid.NewGuid() + ".pkg");
    File.WriteAllBytes(test, Package(Texture(), scene));
    Check(reader.Read(test) == null, "unsupported scene keeps preview fallback");
}
foreach (var texture in new[] { Texture(flags: 4), Texture(compressed: 1), Texture(width: 16), Texture(image: new byte[12]) })
{
    var test = Path.Combine(root, Guid.NewGuid() + ".pkg");
    File.WriteAllBytes(test, Package(texture));
    Check(reader.Read(test) == null, "unsupported texture keeps preview fallback");
}
var truncated = Path.Combine(root, "truncated.pkg");
File.WriteAllBytes(truncated, Package(Texture())[..100]);
Check(reader.Read(truncated) == null, "truncated package rejected without exposing arbitrary file data");
File.WriteAllBytes(path, new byte[12]);
Check(reader.Read(path) == null, "changed package invalidates previous image cache");
File.WriteAllBytes(path, Package(Texture()));
File.WriteAllBytes(Path.Combine(root, "preview.png"), png);
File.WriteAllText(Path.Combine(root, "project.json"), "{\"title\":\"Fixture\",\"preview\":\"preview.png\"}");
var config = Path.Combine(root, "config.json");
File.WriteAllText(config, JsonSerializer.Serialize(new Dictionary<string, object>
{
    [Environment.UserName] = new { general = new { wallpaperconfig = new { selectedwallpapers = new { Monitor0 = new { file = path } } } } }
}));
var media = WallpaperBridge.ReadSelection(config);
Check(media?.Background != null && media.Note.Contains("高清原图"), "selected scene prefers high-resolution background over thumbnail");
File.WriteAllBytes(path, new byte[12]);
media = WallpaperBridge.ReadSelection(config);
Check(media?.Background == null && media?.Path.EndsWith("preview.png") == true, "unsupported scene still uses its existing preview");
if (args.Length > 0)
{
    var actual = reader.Read(args[0]);
    Check(actual != null, "selected local wallpaper yields original image");
    using var data = new MemoryStream(actual!.Bytes);
    var frame = BitmapDecoder.Create(data, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
    Console.WriteLine($"Local image dimensions {frame.PixelWidth}x{frame.PixelHeight}");
    if (args.Length > 1) File.WriteAllBytes(args[1], actual.Bytes);
}
Console.WriteLine($"{checks} scene wallpaper checks passed");
