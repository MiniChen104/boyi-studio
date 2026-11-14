using GlassOJ;
using System.IO;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "BoyiEditorTests-" + Guid.NewGuid().ToString("N"));
var backend = new Backend(root, true);
await backend.Initialize();
int checks = 0;
async Task<JsonElement> Call(string action, object data) => JsonSerializer.SerializeToElement(await backend.Handle(action, JsonSerializer.SerializeToElement(data, Backend.Json)), Backend.Json);
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name);
    checks++;
}
var initial = await Call("init", new { });
var settings = initial.GetProperty("settings");
Check(settings.GetProperty("indentSize").GetInt32() == 4 && settings.GetProperty("autoBrackets").GetBoolean() && settings.GetProperty("codeHints").GetBoolean(), "legacy settings get editor defaults");
var code = "#include <vector>\n#include <algorithm>\n// 中文🙂 keep this comment\nint main(){int n=2;if(n){return n;}return 0;}";
var position = code.IndexOf("return n", StringComparison.Ordinal);
var result = await Call("formatCode", new { code, cursor = position });
var formatted = result.GetProperty("code").GetString()!;
Check(formatted.Contains("int main()\n{\n    int n = 2;"), "Allman braces and four-space indentation");
Check(formatted.Contains("        return n;"), "nested statements indent two levels");
Check(formatted.IndexOf("<vector>", StringComparison.Ordinal) < formatted.IndexOf("<algorithm>", StringComparison.Ordinal), "include order preserved");
Check(formatted.Contains("// 中文🙂 keep this comment"), "Unicode comments preserved");
Check(formatted[result.GetProperty("cursor").GetInt32()..].StartsWith("return n"), "UTF8 formatter cursor maps back to UTF16 after Unicode");
var twice = await Call("formatCode", new { code = formatted, cursor = 0 });
Check(twice.GetProperty("code").GetString() == formatted, "formatting idempotent");
var detail = await Call("detail", new { id = "1000" });
Check(detail.GetProperty("draft").GetString() != formatted, "formatter leaves persisted draft untouched");
foreach (var indent in new[] { 2, 8 })
{
    await Call("settings", new Settings { IndentSize = indent, AutoBrackets = false, CodeHints = false });
    var changed = await Call("formatCode", new { code = "int main(){int n=0;return n;}", cursor = -3 });
    Check(changed.GetProperty("code").GetString()!.Contains("\n" + new string(' ', indent) + "int n = 0;"), $"{indent}-space style honored");
}
var normalized = await Call("settings", new Settings { IndentSize = 13 });
Check(normalized.GetProperty("indentSize").GetInt32() == 4, "invalid indentation normalized");
var blank = await Call("formatCode", new { code = " \n", cursor = 400 });
Check(blank.GetProperty("code").GetString() == " \n" && blank.GetProperty("cursor").GetInt32() == 2, "blank code unchanged and cursor bounded");
bool rejected = false;
try { await Call("formatCode", new { code = new string('x', 500001), cursor = 0 }); } catch { rejected = true; }
Check(rejected, "oversized code rejected");
rejected = false;
try { await Call("formatCode", new { code = "int main(){if(", cursor = 0 }); } catch { rejected = true; }
Check(rejected, "incomplete formatting reported without replacing source");
Console.WriteLine($"{checks} editor backend checks passed");
