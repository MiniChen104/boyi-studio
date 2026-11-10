using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace GlassOJ;

public class ReferenceMetadata
{
    public string Key{get;set;}="default";
    public string Title{get;set;}="参考解法";
    public string Idea{get;set;}="";
    public string TimeComplexity{get;set;}="";
    public string SpaceComplexity{get;set;}="";
    public string Sha256{get;set;}="";
    public string Status{get;set;}="unverified";
    public int Samples{get;set;}
    public int ExtraTests{get;set;}
}
public class ReferenceView:ReferenceMetadata
{
    public string Code{get;set;}="";
}
public partial class Backend
{
    Dictionary<string,List<ReferenceMetadata>> referenceMetadata=[];
    DateTime referenceManifestTime=DateTime.MinValue;
    void LoadReferenceMetadata()
    {
        var path=Path.Combine(Root,"reference-solutions.json");
        var stamp=File.Exists(path)?File.GetLastWriteTimeUtc(path):DateTime.MinValue;
        if(stamp==referenceManifestTime)return;
        referenceMetadata=File.Exists(path)?JsonSerializer.Deserialize<Dictionary<string,List<ReferenceMetadata>>>(File.ReadAllText(path),Json)??[]:[];
        referenceManifestTime=stamp;
    }
    string ReferencePath(string id,string key)
    {
        if(key=="default")return FileAt("references",id,".cpp");
        if(!Regex.IsMatch(key,@"^[a-z0-9][a-z0-9_-]{0,39}$"))throw new Exception("解法编号无效。");
        LoadReferenceMetadata();
        if(!referenceMetadata.TryGetValue(id,out var list) || !list.Any(x=>x.Key==key))throw new Exception("没有找到这份解法。");
        return Path.Combine(Root,"reference-variants",id,key+".cpp");
    }
    int ReferenceCount(string id)
    {
        var count=File.Exists(FileAt("references",id,".cpp"))?1:0;
        if(referenceMetadata.TryGetValue(id,out var list))count+=list.Where(x=>x.Key!="default" && Regex.IsMatch(x.Key,@"^[a-z0-9][a-z0-9_-]{0,39}$")).Select(x=>x.Key).Distinct().Count(key=>File.Exists(Path.Combine(Root,"reference-variants",id,key+".cpp")));
        return count;
    }
    async Task<List<ReferenceView>> ReferenceVersions(string id)
    {
        LoadReferenceMetadata();
        var metadata=referenceMetadata.GetValueOrDefault(id)??[];
        var keys=new[]{"default"}.Concat(metadata.Select(x=>x.Key)).Distinct().ToArray();
        var versions=new List<ReferenceView>();
        foreach(var key in keys)
        {
            if(!Regex.IsMatch(key,@"^[a-z0-9][a-z0-9_-]{0,39}$"))continue;
            var path=ReferencePath(id,key);if(!File.Exists(path))continue;
            var bytes=await File.ReadAllBytesAsync(path);var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var info=metadata.FirstOrDefault(x=>x.Key==key && x.Sha256==hash);
            string status=info?.Status??"unverified";
            if(key=="default" && info==null)
            {
                var manifest=Path.Combine(Root,"reference-status.json");
                if(File.Exists(manifest))
                {
                    using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(manifest));
                    if(doc.RootElement.TryGetProperty(id,out var legacy) && legacy.TryGetProperty("sha256",out var expected) && expected.GetString()==hash && legacy.TryGetProperty("status",out var verified))status=verified.GetString()??"unverified";
                }
            }
            versions.Add(new ReferenceView{Key=key,Title=info?.Title??(key=="default"?"原有参考答案":"其他解法"),Idea=info?.Idea??"",TimeComplexity=info?.TimeComplexity??"",SpaceComplexity=info?.SpaceComplexity??"",Status=status,Samples=info?.Samples??0,ExtraTests=info?.ExtraTests??0,Sha256=hash,Code=await File.ReadAllTextAsync(path)});
        }
        return versions;
    }
}
