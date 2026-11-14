using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace GlassOJ;

// PKG/TEX layout follows RePKG (MIT), copyright 2019 notscuffed.
// See ui/vendor/licenses/repkg.txt. Only a verified full-canvas image is exposed.
internal sealed class SceneWallpaper
{
    internal sealed record Background(byte[] Bytes, string MimeType, string Token);
    sealed record Entry(long Offset, int Length);
    readonly object gate = new();
    string? cachedKey;
    Background? cached;

    public Background? Read(string path)
    {
        var file = new FileInfo(path);
        var key = path + "|" + file.LastWriteTimeUtc.Ticks + "|" + file.Length;
        lock (gate)
        {
            if (key == cachedKey) return cached;
            cachedKey = key;
            cached = null;
            try { cached = ReadPackage(path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
                ArgumentException or InvalidOperationException or NotSupportedException or FormatException or OverflowException or KeyNotFoundException) { }
            return cached;
        }
    }

    static Background? ReadPackage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        var magic = ReadString(reader, 32);
        if (magic.Length != 8 || !magic.StartsWith("PKGV", StringComparison.Ordinal) || !magic[4..].All(char.IsAsciiDigit)) return null;
        var count = reader.ReadInt32();
        if (count is < 1 or > 10000) return null;
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var name = ReadString(reader, 255).Replace('\\', '/');
            var offset = reader.ReadInt32();
            var length = reader.ReadInt32();
            if (offset < 0 || length < 0 || !entries.TryAdd(name, new(offset, length))) return null;
        }
        var dataStart = stream.Position;
        if (entries.Values.Any(entry => entry.Offset + entry.Length > stream.Length - dataStart)) return null;

        byte[] ReadEntry(string name, int limit)
        {
            if (!entries.TryGetValue(name, out var entry) || entry.Length > limit) throw new InvalidDataException("Unsupported scene resource.");
            stream.Position = dataStart + entry.Offset;
            return ReadBytes(reader, entry.Length);
        }
        using var scene = JsonDocument.Parse(ReadEntry("scene.json", 2_000_000));
        var projection = scene.RootElement.GetProperty("general").GetProperty("orthogonalprojection");
        var width = projection.GetProperty("width").GetInt32();
        var height = projection.GetProperty("height").GetInt32();
        if (width < 1 || height < 1 || (long)width * height > 64_000_000) return null;
        var objects = scene.RootElement.GetProperty("objects").EnumerateArray()
            .Where(item => !item.TryGetProperty("visible", out var visible) || visible.ValueKind == JsonValueKind.True).ToArray();
        // Layered scenes need their renderer. Picking the largest texture can display a mask or an incomplete layer.
        if (objects.Length != 1 || !objects[0].TryGetProperty("image", out var image)) return null;
        var layer = objects[0];
        if (!VectorEquals(layer, "size", [width, height]) || !VectorEquals(layer, "origin", [width / 2.0, height / 2.0, 0])) return null;
        if (!OptionalVectorEquals(layer, "scale", [1, 1, 1]) || !OptionalVectorEquals(layer, "angles", [0, 0, 0])) return null;
        using var model = JsonDocument.Parse(ReadEntry(image.GetString()!, 2_000_000));
        using var material = JsonDocument.Parse(ReadEntry(model.RootElement.GetProperty("material").GetString()!, 2_000_000));
        var passes = material.RootElement.GetProperty("passes");
        if (passes.GetArrayLength() != 1) return null;
        var texture = passes[0].GetProperty("textures")[0].GetString();
        if (string.IsNullOrWhiteSpace(texture)) return null;
        var textureBytes = ReadEntry("materials/" + texture + ".tex", 64_000_000);
        return ReadTexture(textureBytes, width, height);
    }

    static bool OptionalVectorEquals(JsonElement value, string key, double[] expected) =>
        !value.TryGetProperty(key, out _) || VectorEquals(value, key, expected);

    static bool VectorEquals(JsonElement value, string key, double[] expected)
    {
        if (!value.TryGetProperty(key, out var vector) || vector.ValueKind != JsonValueKind.String) return false;
        var components = vector.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return components.Length == expected.Length && components.Select((text, index) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && Math.Abs(number - expected[index]) < .01).All(match => match);
    }

    static Background? ReadTexture(byte[] bytes, int width, int height)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (Encoding.ASCII.GetString(ReadBytes(reader, 18)) != "TEXV0005\0TEXI0001\0") return null;
        reader.ReadInt32(); // GPU texture format. Embedded PNG/JPEG data uses the container format below.
        var flags = reader.ReadInt32();
        if ((flags & (4 | 32)) != 0) return null;
        reader.ReadInt32(); reader.ReadInt32(); // Padded GPU dimensions.
        if (reader.ReadInt32() != width || reader.ReadInt32() != height) return null;
        reader.ReadInt32();
        var container = Encoding.ASCII.GetString(ReadBytes(reader, 9));
        if (container is not ("TEXB0003\0" or "TEXB0004\0") || reader.ReadInt32() != 1) return null;
        var format = reader.ReadInt32();
        if (format is not (2 or 13)) return null; // FreeImage JPEG and PNG identifiers.
        if (container == "TEXB0004\0" && reader.ReadInt32() != 0) return null;
        var mipmaps = reader.ReadInt32();
        if (mipmaps is < 1 or > 32 || reader.ReadInt32() != width || reader.ReadInt32() != height) return null;
        if (reader.ReadInt32() != 0) return null; // Compressed and raw GPU textures retain the preview fallback.
        reader.ReadInt32();
        var size = reader.ReadInt32();
        if (size is < 8 or > 64_000_000) return null;
        var image = ReadBytes(reader, size);
        var png = image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var jpeg = image[0] == 255 && image[1] == 216 && image[2] == 255;
        if ((format == 13 && !png) || (format == 2 && !jpeg)) return null;
        using var imageStream = new MemoryStream(image, false);
        var decoded = BitmapDecoder.Create(imageStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoded.Frames[0].PixelWidth != width || decoded.Frames[0].PixelHeight != height) return null;
        return new(image, png ? "image/png" : "image/jpeg", Convert.ToHexString(SHA256.HashData(image))[..20]);
    }

    static string ReadString(BinaryReader reader, int limit)
    {
        var size = reader.ReadInt32();
        if (size < 1 || size > limit) throw new InvalidDataException("Invalid package string length.");
        return Encoding.UTF8.GetString(ReadBytes(reader, size));
    }

    static byte[] ReadBytes(BinaryReader reader, int count)
    {
        if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Truncated scene resource.");
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException();
        return bytes;
    }
}
