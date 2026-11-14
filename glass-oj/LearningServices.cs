using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace GlassOJ;

public sealed class CodeSnapshot
{
    public string Key{get;set;}="";
    public string ProblemId{get;set;}="";
    public string SubmissionId{get;set;}="";
    public string Code{get;set;}="";
    public string Source{get;set;}="";
    public string Result{get;set;}="等待评测";
    public string Language{get;set;}="C++";
    public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.Now;
}
public sealed class ReviewNote
{
    public string Status{get;set;}="none";
    public string Note{get;set;}="";
    public DateTimeOffset UpdatedAt{get;set;}=DateTimeOffset.Now;
}
public partial class Backend
{
    readonly Dictionary<string,string> ownedSubmissions=new();
    readonly Dictionary<string,CodeSnapshot?> draftCheckpoints=new();
    string LearningFolder(string kind,string id)
    {
        var dir=Path.Combine(AccountDirectory(state.Account),kind,Id(id));
        Directory.CreateDirectory(dir);return dir;
    }
    static string SnapshotKey(string key)=>Regex.IsMatch(key,@"^[a-zA-Z0-9-]{1,80}$")?key:throw new Exception("版本编号无效。");
    IEnumerable<CodeSnapshot> ReadSnapshots(string kind,string id)
    {
        foreach(var file in Directory.EnumerateFiles(LearningFolder(kind,id),"*.json"))
        {
            CodeSnapshot? row=null;
            try{row=JsonSerializer.Deserialize<CodeSnapshot>(File.ReadAllText(file),Json);}catch(JsonException){}
            if(row!=null)yield return row;
        }
    }
    void SaveSnapshot(string kind,CodeSnapshot row)=>WriteJson(Path.Combine(LearningFolder(kind,row.ProblemId),SnapshotKey(row.Key)+".json"),row);
    void SaveDraftVersion(string id,string code,string source="自动保存")
    {
        id=Id(id);if(code.Length>500000)throw new Exception("代码超过500KB。");
        var path=FileAt("drafts",id,".cpp");
        if(File.Exists(path))
        {
            var previous=File.ReadAllText(path);
            if(previous==code)return;
            if(!draftCheckpoints.TryGetValue(path,out var latest))
            {
                var latestFile=Directory.EnumerateFiles(LearningFolder("draft-history",id),"*.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                latest=latestFile==null?null:JsonSerializer.Deserialize<CodeSnapshot>(File.ReadAllText(latestFile),Json);draftCheckpoints[path]=latest;
            }
            // Preserve the pre-change version, coalescing typing to at most one checkpoint per minute.
            // Explicit imports/restores always retain the exact displaced draft.
            if(latest==null || latest.Code!=previous && (source!="自动保存" || DateTimeOffset.Now-latest.CreatedAt>TimeSpan.FromMinutes(1)))
                {var checkpoint=new CodeSnapshot{Key=Guid.NewGuid().ToString("N"),ProblemId=id,Code=previous,Source=source,Result="草稿恢复点"};SaveSnapshot("draft-history",checkpoint);draftCheckpoints[path]=checkpoint;}
        }
        var tmp=path+".tmp";File.WriteAllText(tmp,code);File.Move(tmp,path,true);GetRecord(id).Updated=DateTime.Now;Save();
    }
    object LocalVersions(string id,string kind)=>new{lastKnownSubmission=kind=="submissions"?GetRecord(Id(id)).Submission:"",items=ReadSnapshots(kind,Id(id)).OrderByDescending(x=>x.CreatedAt).Select(x=>new{x.Key,x.ProblemId,x.SubmissionId,x.CreatedAt,x.Source,x.Result,x.Language}).ToArray()};
    CodeSnapshot ReadVersion(string id,string key,string kind)
    {
        var path=Path.Combine(LearningFolder(kind,Id(id)),SnapshotKey(key)+".json");
        return File.Exists(path)?JsonSerializer.Deserialize<CodeSnapshot>(File.ReadAllText(path),Json)??throw new Exception("版本损坏。"):throw new Exception("找不到该代码版本。");
    }
    CodeSnapshot BeginSubmissionSnapshot(string id,string code)
    {
        var row=new CodeSnapshot{Key=Guid.NewGuid().ToString("N"),ProblemId=id,Code=code,Source=GetRecord(id).StageSource=="draft"?"我的代码":GetRecord(id).StageSolutionTitle.Length>0?GetRecord(id).StageSolutionTitle:"参考答案",Result="请求已准备，提交结果待确认"};
        SaveSnapshot("submissions",row);return row;
    }
    void UpdateSubmissionSnapshot(string id,string sid,string result)
    {
        foreach(var row in ReadSnapshots("submissions",id).Where(x=>x.SubmissionId==sid))
        {row.Result=result;SaveSnapshot("submissions",row);}
    }
    ReviewNote ReadReview(string id)
    {
        var path=Path.Combine(LearningFolder("review",id),"note.json");
        return File.Exists(path)?JsonSerializer.Deserialize<ReviewNote>(File.ReadAllText(path),Json)??new():new();
    }
    object SaveReview(string id,string status,string note)
    {
        if(!new[]{"none","review","mastered"}.Contains(status))throw new Exception("复习状态无效。");
        if(note.Length>20000)throw new Exception("笔记最多20000字。");
        var row=new ReviewNote{Status=status,Note=note};WriteJson(Path.Combine(LearningFolder("review",Id(id)),"note.json"),row);return row;
    }
    object ReviewIndex()=>new{items=AllProblems.Where(p=>GetRecord(p.Id).Status=="failed" || File.Exists(Path.Combine(AccountDirectory(state.Account),"review",p.Id,"note.json"))).Select(p=>new{p.Id,p.Title,record=GetRecord(p.Id),review=ReadReview(p.Id)}).ToArray()};
    async Task<object> EnsureProblem(string id)
    {
        id=Id(id);var existing=AllProblems.FirstOrDefault(p=>p.Id==id);
        if(existing!=null)return await Detail(id);
        var problem=new Problem{Id=id,Title="题目 "+id,Url="/p/"+id};
        var account=state.Account;await FetchProblem(problem,accountScoped:true);if(account!=state.Account)throw new Exception("账号已切换。");
        lock(gate){if(!accountProblems.Any(p=>p.Id==id))accountProblems.Add(problem);WriteJson(Path.Combine(AccountDirectory(account),"extra-catalog.json"),accountProblems);}
        return await Detail(id);
    }
    async Task<object> ProblemHistory(string id,string path)
    {
        id=Id(id);
        var query=System.Web.HttpUtility.ParseQueryString(Url(OnlinePath(path,"/status/index")).Query);
        query["SolutionSearch[problem_id]"]=id;
        return await OnlineHistory("/status/index?"+query);
    }
    async Task<object> SubmissionCode(string sid,string problemId)
    {
        sid=Id(sid);var account=state.Account;var connection=client;
        if((!ownedSubmissions.TryGetValue(sid,out var owner) || owner!=account) && !state.Records.Values.Any(r=>r.Submission==sid))throw new Exception("尚未确认此提交属于当前账号，请刷新记录或在官网查看。");
        var doc=await AccountPage("/solution/detail?id="+sid);
        if(state.Account!=account || connection!=client)throw new Exception("账号已切换。");
        var content=doc.DocumentNode.SelectSingleNode("//*[contains(concat(' ',normalize-space(@class),' '),' solution-detail ')]|//*[contains(concat(' ',normalize-space(@class),' '),' solution-view ')]")??doc.DocumentNode;
        var authorLinks=Nodes(content,".//a[contains(@href,'/user/view?')]").Where(n=>!n.Ancestors("nav").Any()).ToArray();
        if(authorLinks.Any() && !authorLinks.Any(n=>System.Web.HttpUtility.ParseQueryString(Url(Attr(n,"href")).Query)["id"]==account))throw new Exception("此提交不属于当前账号。");
        var codeNode=content.SelectSingleNode(".//textarea[@name='Solution[source]' or contains(@id,'source') or contains(@id,'code')]|.//pre[code]|.//pre[contains(@class,'prettyprint') or contains(@class,'source') or contains(@id,'source') or contains(@id,'code')]");
        if(codeNode==null)throw new Exception("官网未提供可读取的源码。可在“官网完整功能”查看原始提交详情。");
        var code=HtmlEntity.DeEntitize(codeNode.InnerText);if(code.Length>500000 || string.IsNullOrWhiteSpace(code))throw new Exception("官网源码为空或超过500KB。");
        var problemLink=Nodes(content,".//a[@href]").FirstOrDefault(n=>Regex.IsMatch(Attr(n,"href"),@"^/p/\d+$"));
        var id=problemLink==null?Id(problemId):Id(Attr(problemLink,"href").Split('/').Last());
        var languageRow=Nodes(content,".//tr").FirstOrDefault(n=>Text(n.SelectSingleNode("./th|./td[1]")).Contains("语言"));
        var language=Text(languageRow?.SelectSingleNode("./td[last()]"));
        var row=new CodeSnapshot{Key="official-"+sid,ProblemId=id,SubmissionId=sid,Code=code,Language=language.Length>0?language:"以官网记录为准",Source="官网历史提交",Result="已从官网读取 · 评测结果见提交记录"};
        var old=ReadSnapshots("submissions",id).FirstOrDefault(x=>x.SubmissionId==sid);
        if(old!=null)return old;
        SaveSnapshot("submissions",row);return row;
    }
    async Task<object> OnlineGroups(string path)
    {
        // Old clients may retain the public directory route. Keep their search and page within My Groups.
        var uri=Url(string.IsNullOrWhiteSpace(path)?"/group/my-group":path);
        if(uri.AbsolutePath!="/group/my-group" && uri.AbsolutePath!="/group/index")throw new Exception("小组目录地址无效。");
        path="/group/my-group"+uri.Query;
        var doc=await AccountPage(path);
        var items=Nodes(doc.DocumentNode,"//a[@href]").Where(n=>Regex.IsMatch(Attr(n,"href"),@"^/group/view\?id=\d+"))
            .DistinctBy(n=>Attr(n,"href")).Select(n=>new{path=Attr(n,"href"),title=Text(n),description=Text(n.Ancestors().FirstOrDefault(a=>Attr(a,"class").Split(' ').Contains("card"))?.SelectSingleNode(".//*[contains(@class,'card-text')]"))}).ToArray();
        var filters=Nodes(doc.DocumentNode,"//ul[contains(@class,'nav')]//a[@href]")
            .Where(n=>Uri.TryCreate(new Uri(Site),Attr(n,"href"),out var link) && link.Host=="oj.czos.cn" && link.AbsolutePath=="/group/my-group")
            .Select(n=>new{path=Url(Attr(n,"href")).PathAndQuery,title=Text(n)}).DistinctBy(x=>x.path).ToArray();
        return new{items,filters,next=NextPage(doc,"/group/my-group"),path,source="我加入或管理的小组"};
    }
    async Task<object> OnlineGroup(string path)
    {
        path=OnlinePath(path,"/group/view");if(!Regex.IsMatch(path,@"[?&]id=\d+(?:&|$)"))throw new Exception("小组编号无效。");
        var doc=await AccountPage(path);
        var main=doc.DocumentNode.SelectSingleNode("//*[contains(concat(' ',normalize-space(@class),' '),' group-view ')]")??doc.DocumentNode;
        var links=Nodes(main,".//a[@href]").Where(n=>Regex.IsMatch(Attr(n,"href"),@"^/(lists|group|contest)/[a-z-]+\?"))
            .DistinctBy(n=>Attr(n,"href")).Select(n=>new{path=Attr(n,"href"),title=Text(n)}).Where(x=>x.title.Length>0).ToArray();
        return new{title=Text(main.SelectSingleNode(".//h1|.//h2")),links,path,note="题单可在练习室打开；成员、公告、排名与管理操作通过官网完整功能使用，权限以官网为准。"};
    }
}
