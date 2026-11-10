using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using HtmlAgilityPack;
using Microsoft.Win32;
namespace GlassOJ;

public class Problem
{
    public string Id{get;set;}="";
    public string Title{get;set;}="";
    public List<string> Tags{get;set;}=[];
    public string Difficulty{get;set;}="";
    public string Url{get;set;}="";
}
public class Record
{
    public string Status{get;set;}="untouched";
    public string Result{get;set;}="";
    public string Submission{get;set;}="";
    public bool EverAccepted{get;set;}
    public bool ExplicitRetry{get;set;}
    public string StageSource{get;set;}="";
    public string StageSolutionKey{get;set;}="";
    public string StageSolutionTitle{get;set;}="";
    public DateTime? Updated{get;set;}
}
public class Settings
{
    public string Theme{get;set;}="light";
    public int Glass{get;set;}=78;
    public string GlassQuality{get;set;}="smooth";
    public int FontSize{get;set;}=15;
    public bool Wrap{get;set;}=false;
    public int Interval{get;set;}=60;
    public string Compiler{get;set;}="";
}
public class State
{
    public string Account{get;set;}="";
    public Dictionary<string,Record> Records{get;set;}=[];
    public List<string> Queue{get;set;}=[];
    public DateTimeOffset LastPost{get;set;}=DateTimeOffset.MinValue;
    public Settings Settings{get;set;}=new();
    public DateTimeOffset? ProgressSyncedAt{get;set;}
    public bool AccountFilesMigrated{get;set;}
}
public partial class Backend
{
    public static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=false,PropertyNameCaseInsensitive=true};
    public readonly string Root;
    readonly bool isolatedTest;
    public Backend(string? root=null,bool isolatedTest=false){Root=root??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BoyiStudio");this.isolatedTest=isolatedTest;if(isolatedTest)state.Account="ui_test";}
    const string Site="https://oj.czos.cn";
    static readonly string Template="#include<iostream>\nusing namespace std;\n\nint main()\n{\n    \n    return 0;\n}\n";
    HttpClient client=NewClient();
    readonly HttpClient publicClient=NewClient();
    readonly object gate=new();
    readonly SemaphoreSlim runGate=new(1,1);
    State state=new();
    List<Problem> catalog=[];
    List<Problem> accountProblems=[];
    IEnumerable<Problem> AllProblems=>accountProblems.Concat(catalog).DistinctBy(p=>p.Id);
    void LoadAccountProblems(){var path=Path.Combine(AccountDirectory(state.Account),"extra-catalog.json");accountProblems=File.Exists(path)?JsonSerializer.Deserialize<List<Problem>>(File.ReadAllText(path),Json)??[]:[];}
    readonly List<object> logs=[];
    CancellationTokenSource? taskCancel;
    Mutex? submitMutex;
    bool loggedIn,syncing,compiling;
    public bool Running{get;private set;}
    public bool DarkTheme=>state.Settings.Theme=="dark";
    public Func<string,object,Task>? Notify;
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HttpClient,CookieContainer> sessions=new();
    static HttpClient NewClient(CookieContainer? cookies=null)
    {
        cookies??=new();var http=new HttpClient(new HttpClientHandler{CookieContainer=cookies,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(40)};sessions.Add(http,cookies);return http;
    }
    public string CurrentAccount=>state.Account;
    public CookieCollection OfficialCookies()=>sessions.TryGetValue(client,out var cookies)?cookies.GetCookies(new Uri(Site)):new CookieCollection();
    string FileAt(string folder,string id,string extension)=>Path.Combine((folder is "drafts" or "staged" || folder=="problems" && accountProblems.Any(p=>p.Id==id))?AccountDirectory(state.Account):Root,folder,Id(id)+extension);
    static string Id(string s)=>Regex.IsMatch(s,@"^\d{1,10}$")?s:throw new Exception("题号无效。");
    static HtmlDocument Html(string text){var d=new HtmlDocument();d.LoadHtml(text);return d;}
    static string Text(HtmlNode? n)=>HtmlEntity.DeEntitize(n?.InnerText??"").Trim();
    static IEnumerable<HtmlNode> Nodes(HtmlNode node,string xpath)=>node.SelectNodes(xpath)?.AsEnumerable()??[];
    static string Attr(HtmlNode node,string name)=>HtmlEntity.DeEntitize(node.GetAttributeValue(name,""));
    public static Uri Url(string path){var u=new Uri(new Uri(Site),path);if(u.Scheme!="https" || u.Host!="oj.czos.cn" || !u.IsDefaultPort)throw new Exception("请求地址不属于东方博宜。");return u;}
    static void WriteJson(string path,object data){var tmp=path+".tmp";File.WriteAllText(tmp,JsonSerializer.Serialize(data,Json));File.Move(tmp,path,true);}
    void Save(){lock(gate){EnsureAccountDirectories(state.Account);WriteJson(Path.Combine(AccountDirectory(state.Account),"progress.json"),state);WriteJson(Path.Combine(Root,"state.json"),state);}}
    public async Task Initialize()
    {
        foreach(var dir in new[]{"problems","references","drafts","staged","tests"})Directory.CreateDirectory(Path.Combine(Root,dir));
        var seed=Path.Combine(AppContext.BaseDirectory,"seed");
        if(Directory.Exists(seed))foreach(var file in Directory.EnumerateFiles(seed,"*",SearchOption.AllDirectories))
        {
            var target=Path.Combine(Root,Path.GetRelativePath(seed,file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var relative=Path.GetRelativePath(seed,file);
            var updateable=relative.StartsWith("problems"+Path.DirectorySeparatorChar) || relative.StartsWith("reference-variants"+Path.DirectorySeparatorChar) || relative is "catalog.json" or "reference-status.json" or "reference-solutions.json" or "sync.json" or "image-cache-report.json";
            if(!File.Exists(target) || updateable && File.GetLastWriteTimeUtc(file)>File.GetLastWriteTimeUtc(target))File.Copy(file,target,true);
        }
        var statePath=Path.Combine(Root,"state.json");
        if(File.Exists(statePath))state=JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(statePath),Json)??new();
        MigrateAccountFiles();
        state.Settings.Interval=Math.Max(60,state.Settings.Interval);
        var cp=Path.Combine(Root,"catalog.json");
        if(File.Exists(cp))catalog=JsonSerializer.Deserialize<List<Problem>>(await File.ReadAllTextAsync(cp),Json)??[];
        LoadAccountProblems();
        foreach(var record in state.Records.Values)if(record.Status=="accepted")record.EverAccepted=true;
        LoadReferenceMetadata();
        Save();
    }
    Record GetRecord(string id){lock(gate){if(!state.Records.ContainsKey(id))state.Records[id]=new();return state.Records[id];}}
    object Snapshot()
    {
        lock(gate)return new{catalog=AllProblems.Select(p=>new{p.Id,p.Title,p.Tags,p.Difficulty,reference=ReferenceCount(p.Id)>0,referenceCount=ReferenceCount(p.Id),cached=File.Exists(FileAt("problems",p.Id,".json")),draft=File.Exists(FileAt("drafts",p.Id,".cpp")),record=GetRecord(p.Id)}).ToArray(),queue=state.Queue.ToArray(),state.Settings,account=state.Account,loggedIn,running=Running,syncing,progressSyncing,progressMessage,state.ProgressSyncedAt,acceptedCount=state.Records.Count(p=>p.Value.Status=="accepted" || p.Value.EverAccepted),logs=logs.ToArray(),dataDirectory=Root,lastPost=state.LastPost};
    }
    async Task Emit(string topic,object data){if(Notify!=null)await Notify(topic,data);}
    async Task Log(string message,string kind="info")
    {
        var row=new{time=DateTime.Now.ToString("HH:mm:ss"),message,kind};
        lock(gate){logs.Add(row);if(logs.Count>150)logs.RemoveAt(0);File.AppendAllText(Path.Combine(Root,"activity.log"),DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ")+message+Environment.NewLine);}
        await Emit("log",row);
    }
    async Task<string> Request(HttpClient http,HttpMethod method,string path,Dictionary<string,string>? data=null,CancellationToken ct=default)
    {
        var target=Url(path);var referer=target;var currentMethod=method;
        for(int hop=0;hop<=6;hop++)
        {
            // The website returns HTTP 500 when User-Agent is absent, including redirect hops.
            using var req=new HttpRequestMessage(currentMethod,target);
            req.Headers.TryAddWithoutValidation("User-Agent","Mozilla/5.0");
            req.Headers.Referrer=referer;
            if(target.AbsolutePath=="/solution/verdict")req.Headers.TryAddWithoutValidation("X-Requested-With","XMLHttpRequest");
            if(data!=null)req.Content=new FormUrlEncodedContent(data);
            using var response=await http.SendAsync(req,ct);
            if((int)response.StatusCode is >=300 and <400)
            {
                if((int)response.StatusCode is 307 or 308 && currentMethod==HttpMethod.Post)throw new Exception("提交出现异常重定向，已停止，避免重复发送。");
                var location=response.Headers.Location??throw new Exception("重定向缺少地址。");
                referer=target;target=Url(new Uri(target,location).AbsoluteUri);
                currentMethod=HttpMethod.Get;data=null;
                continue;
            }
            if(response.StatusCode==HttpStatusCode.TooManyRequests)throw new Exception("网站正在限速，请稍后再同步。");
            if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)throw new Exception("网站未授权访问此内容，请登录后重试。");
            if(!response.IsSuccessStatusCode)throw new HttpRequestException($"东方博宜返回 HTTP {(int)response.StatusCode}（{currentMethod} {target.AbsolutePath}），请稍后重试。",null,response.StatusCode);
            return await response.Content.ReadAsStringAsync(ct);
        }
        throw new Exception("网站跳转次数过多，请稍后重试。");
    }
    static HtmlNode Form(HtmlDocument d,string name)=>Nodes(d.DocumentNode,"//form").FirstOrDefault(f=>Nodes(f,".//*[@name]").Any(n=>Attr(n,"name")==name))??throw new Exception("未找到提交表单，可能登录失效或网站页面已变化。");
    static Dictionary<string,string> Fields(HtmlNode f)=>Nodes(f,".//input[@name]").Where(n=>!new[]{"checkbox","radio","submit","button","file"}.Contains(Attr(n,"type"))).GroupBy(n=>Attr(n,"name")).ToDictionary(g=>g.Key,g=>Attr(g.First(),"value"));
    public async Task<object?> Handle(string action,JsonElement data)
    {
        string S(string key)=>data.TryGetProperty(key,out var v)?v.GetString()??"":"";
        switch(action)
        {
            case "init":return Snapshot();
            case "ensureProblem":return await EnsureProblem(S("id"));
            case "localSubmissions":return LocalVersions(S("id"),"submissions");
            case "submissionVersion":return ReadVersion(S("id"),S("key"),"submissions");
            case "draftVersions":return LocalVersions(S("id"),"draft-history");
            case "draftVersion":return ReadVersion(S("id"),S("key"),"draft-history");
            case "restoreDraft":var version=ReadVersion(S("id"),S("key"),"draft-history");SaveDraftVersion(S("id"),version.Code,"恢复前备份");return version.Code;
            case "problemHistory":return await ProblemHistory(S("id"),S("path"));
            case "submissionCode":return await SubmissionCode(S("sid"),S("id"));
            case "review":return ReadReview(Id(S("id")));
            case "saveReview":return SaveReview(S("id"),S("status"),S("note"));
            case "reviewIndex":return ReviewIndex();
            case "onlineGroups":return await OnlineGroups(S("path"));
            case "onlineGroup":return await OnlineGroup(S("path"));
            case "retrySubmission":
            {
                var id=Id(S("id"));if(Running)throw new Exception("请先暂停提交任务。");
                var record=GetRecord(id);if(record.Status is "sending" or "pending")throw new Exception("上次提交尚未确认，请先核对结果。");
                if(!File.Exists(FileAt("staged",id,".cpp")) || string.IsNullOrWhiteSpace(File.ReadAllText(FileAt("staged",id,".cpp"))))throw new Exception("请先保存待提交版本。");
                record.EverAccepted|=record.Status=="accepted";record.ExplicitRetry=true;
                if(!state.Queue.Contains(id))state.Queue.Add(id);Save();return state.Queue;
            }
            case "detail":return await Detail(Id(S("id")));
            case "saveDraft":
            {
                var id=Id(S("id"));var code=S("code");if(code.Length>500000)throw new Exception("代码超过500KB。");
                SaveDraftVersion(id,code,S("reason").Length>0?S("reason"):"自动保存");return true;
            }
            case "stage":
            {
                var id=Id(S("id"));if(Running && state.Queue.Contains(id))throw new Exception("请先暂停任务，再替换队列中的代码。");
                var source=S("source");if(source!="draft" && source!="reference")throw new Exception("代码来源无效。");
                var key=source=="reference"?(string.IsNullOrEmpty(S("solutionKey"))?"default":S("solutionKey")):"";
                ReferenceView? reference=null;
                if(source=="reference")
                {
                    ReferencePath(id,key);
                    reference=(await ReferenceVersions(id)).FirstOrDefault(x=>x.Key==key)??throw new Exception("该代码版本还不存在。");
                }
                var path=FileAt("drafts",id,".cpp");
                if(source=="draft" && !File.Exists(path))throw new Exception("该代码版本还不存在。");
                var code=reference?.Code??File.ReadAllText(path);if(string.IsNullOrWhiteSpace(code))throw new Exception("不能提交空代码。");
                File.WriteAllText(FileAt("staged",id,".cpp"),code);var record=GetRecord(id);record.StageSource=source;record.StageSolutionKey=key;record.StageSolutionTitle=reference?.Title??"";Save();return new{source,code,solutionKey=key,solutionTitle=record.StageSolutionTitle};
            }
            case "queueAdd":
            {
                var ids=data.GetProperty("ids").EnumerateArray().Select(v=>Id(v.GetString()!)).ToList();int count=0;
                foreach(var id in ids)
                {
                    if(GetRecord(id).Status=="accepted" || state.Queue.Contains(id))continue;
                    if(!File.Exists(FileAt("staged",id,".cpp")))continue;
                    state.Queue.Add(id);count++;
                }
                Save();return new{count,queue=state.Queue.ToArray()};
            }
            case "queueRemove":if(Running)throw new Exception("请先暂停任务。");GetRecord(Id(S("id"))).ExplicitRetry=false;state.Queue.Remove(Id(S("id")));Save();return state.Queue;
            case "queueClear":if(Running)throw new Exception("请先暂停任务。");foreach(var id in state.Queue)GetRecord(id).ExplicitRetry=false;state.Queue.Clear();Save();return true;
            case "login":await Login(S("username"),S("password"));return new{account=state.Account,loggedIn};
            case "logout":await Logout();return true;
            case "syncProgress":if(Running)throw new Exception("请先暂停提交，再同步通过进度。");if(!loggedIn)throw new Exception("请先登录。");BeginProgressSync();return true;
            case "onlineLists":return await OnlineLists(S("path"));
            case "onlineList":return await OnlineList(S("path"));
            case "onlineHistory":return await OnlineHistory(S("path"));
            case "start":Start();return true;
            case "stop":Stop();return true;
            case "sync":if(!syncing)_=Sync();return true;
            case "settings":state.Settings=JsonSerializer.Deserialize<Settings>(data.GetRawText(),Json)??new();state.Settings.Interval=Math.Max(60,state.Settings.Interval);state.Settings.Glass=Math.Clamp(state.Settings.Glass,15,90);state.Settings.FontSize=Math.Clamp(state.Settings.FontSize,12,24);Save();return state.Settings;
            case "run":return await RunCode(Id(S("id")),S("code"),S("input"));
            case "openSource":var p=AllProblems.FirstOrDefault(x=>x.Id==S("id"))??throw new Exception("题目不存在。");Process.Start(new ProcessStartInfo(Url(p.Url).ToString()){UseShellExecute=true});return true;
            case "openData":Process.Start(new ProcessStartInfo(Root){UseShellExecute=true});return true;
            case "importReferences":
            {
                var dialog=new OpenFileDialog{Filter="参考答案包 (*.zip)|*.zip",Multiselect=false};if(dialog.ShowDialog()!=true)return null;
                using var archive=ZipFile.OpenRead(dialog.FileName);var files=new Dictionary<string,string>();long total=0;
                foreach(var entry in archive.Entries)
                {
                    var name=Path.GetFileName(entry.FullName.Replace('\\','/'));var id=Path.GetFileNameWithoutExtension(name);
                    if(!name.EndsWith(".cpp",StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(id,@"^\d+$") || !AllProblems.Any(p=>p.Id==id))continue;
                    total+=entry.Length;if(entry.Length>500000 || total>100000000)throw new Exception("答案包内容过大。");
                    using var reader=new StreamReader(entry.Open());files[id]=await reader.ReadToEndAsync();
                }
                if(files.Count==0)throw new Exception("没有找到以题号命名的.cpp文件。");
                if(System.Windows.MessageBox.Show($"将导入或更新{files.Count}份参考答案。你的草稿和待提交代码不会改变。","导入参考答案包",System.Windows.MessageBoxButton.OKCancel)!=System.Windows.MessageBoxResult.OK)return null;
                foreach(var pair in files)File.WriteAllText(FileAt("references",pair.Key,".cpp"),pair.Value);
                await Log($"已导入{files.Count}份参考答案。","success");return new{count=files.Count};
            }
            case "import":
            {
                var dialog=new OpenFileDialog{Filter="C++代码 (*.cpp)|*.cpp",Multiselect=false};if(dialog.ShowDialog()!=true)return null;
                var code=await File.ReadAllTextAsync(dialog.FileName);if(code.Length>500000)throw new Exception("文件超过500KB。");
                SaveDraftVersion(Id(S("id")),code,"导入前备份");return code;
            }
            case "export":
            {
                var dialog=new SaveFileDialog{Filter="C++代码 (*.cpp)|*.cpp",FileName=Id(S("id"))+".cpp"};if(dialog.ShowDialog()!=true)return false;
                await File.WriteAllTextAsync(dialog.FileName,S("code"));return true;
            }
            default:throw new Exception("不支持的操作。");
        }
    }
    async Task<object> Detail(string id)
    {
        var path=FileAt("problems",id,".json");
        if(!File.Exists(path))await FetchProblem(AllProblems.FirstOrDefault(p=>p.Id==id)??throw new Exception("题目不存在。"));
        var problem=JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement.Clone();
        string Read(string folder,string fallback="")=>File.Exists(FileAt(folder,id,".cpp"))?File.ReadAllText(FileAt(folder,id,".cpp")):fallback;
        var references=await ReferenceVersions(id);
        return new{problem,references,reference=references.FirstOrDefault()?.Code??"",referenceStatus=references.FirstOrDefault()?.Status??"unverified",draft=Read("drafts",Template),staged=Read("staged"),record=GetRecord(id)};
    }
    static string Sanitize(HtmlNode root)
    {
        var copy=root.CloneNode(true);
        foreach(var n in Nodes(copy,".//script|.//style|.//iframe|.//object|.//embed|.//form|.//input|.//button|.//link|.//meta").ToList())n.Remove();
        foreach(var n in copy.DescendantsAndSelf().ToList())
            foreach(var a in n.Attributes.ToList())
            {
                if(a.Name is not ("class" or "src" or "alt" or "colspan" or "rowspan"))n.Attributes.Remove(a);
                else if(a.Name=="src")
                {
                    if(Uri.TryCreate(new Uri(Site),HtmlEntity.DeEntitize(a.Value),out var u) && u.Scheme=="https")a.Value=u.ToString();else n.Attributes.Remove(a);
                }
            }
        return copy.InnerHtml;
    }
    async Task FetchProblem(Problem p,bool accountScoped=false)
    {
        var account=state.Account;var connection=accountScoped&&loggedIn?client:publicClient;var doc=Html(await Request(connection,HttpMethod.Get,p.Url));if(connection!=publicClient && (state.Account!=account || connection!=client))throw new Exception("账号已切换。");
        var view=doc.DocumentNode.SelectSingleNode("//div[contains(concat(' ',normalize-space(@class),' '),' problem-view ')]")??throw new Exception("此题题面暂时不可访问。");
        var title=Text(doc.DocumentNode.SelectSingleNode("//h1"));if(title.Length>0)p.Title=title;var sections=new List<object>();string heading="";
        foreach(var n in view.ChildNodes)
        {
            if(Attr(n,"class").Contains("content-header"))heading=Text(n);
            else if(Attr(n,"class").Contains("content-wrapper"))sections.Add(new{heading,html=Sanitize(n),text=Text(n)});
        }
        var ins=Nodes(view,".//div[@class='input']/pre").Select(n=>HtmlEntity.DeEntitize(n.InnerText)).ToArray();
        var outs=Nodes(view,".//div[@class='output']/pre").Select(n=>HtmlEntity.DeEntitize(n.InnerText)).ToArray();
        var samples=ins.Zip(outs,(a,b)=>new{input=a,output=b}).ToArray();
        var parameters=new Dictionary<string,string>();
        foreach(var tr in Nodes(doc.DocumentNode,"//div[contains(@class,'problem-info')]//tr"))
        {var cells=Nodes(tr,"./td").ToList();if(cells.Count>=2)parameters[Text(cells[0])]=Text(cells[1]);}
        var target=accountScoped?Path.Combine(AccountDirectory(state.Account),"problems",p.Id+".json"):FileAt("problems",p.Id,".json");Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        WriteJson(target,new{p.Id,p.Title,p.Tags,p.Difficulty,p.Url,sections,samples,parameters,fetched=DateTime.Now.ToString("s")});
    }
    async Task Sync()
    {
        syncing=true;
        try
        {
            var all=new List<Problem>();var seen=new HashSet<string>();string? next="/problem/index";
            while(next!=null)
            {
                var doc=Html(await Request(publicClient,HttpMethod.Get,next));
                foreach(var tr in Nodes(doc.DocumentNode,"//tr[@data-key]"))
                {
                    var id=Attr(tr,"data-key");var cells=Nodes(tr,"./td").ToList();if(!Regex.IsMatch(id,@"^\d+$") || cells.Count<6 || !seen.Add(id))continue;
                    var a=cells[2].SelectSingleNode(".//a[@href]");if(a==null)continue;
                    all.Add(new(){Id=id,Title=Text(cells[2]),Tags=Nodes(cells[3],".//a").Select(Text).ToList(),Difficulty=Text(cells[4]),Url=Url(Attr(a,"href")).ToString()});
                }
                var link=doc.DocumentNode.SelectSingleNode("//li[contains(concat(' ',@class,' '),' next ')]/a[@href]");next=link==null?null:Attr(link,"href");
                await Emit("sync",new{phase="目录",done=all.Count,total=0});await Task.Delay(700);
            }
            lock(gate){catalog=all;WriteJson(Path.Combine(Root,"catalog.json"),catalog);}
            int done=0,errors=0;
            foreach(var p in all)
            {
                if(!File.Exists(FileAt("problems",p.Id,".json")))
                {try{await FetchProblem(p);}catch{errors++;}await Task.Delay(1000);}
                done++;if(done%10==0 || done==all.Count)await Emit("sync",new{phase="题面",done,total=all.Count,errors});
            }
            await Log($"题库同步完成：{all.Count}题，{errors}题暂时无法获取。",errors==0?"success":"warning");
        }
        catch(Exception ex){await Log("同步暂停："+ex.Message,"error");}
        finally{syncing=false;await Emit("refresh",new{});}
    }
    public void Stop()=>taskCancel?.Cancel();
    void Start()
    {
        if(Running)return;
        if(accountBusy || progressSyncing)throw new Exception("正在同步账号进度，请完成后再开始提交。");
        if(!loggedIn)throw new Exception("请先登录东方博宜。");
        if(state.Queue.Count==0)throw new Exception("请先将待提交代码加入队列。");
        submitMutex=new Mutex(false,"Local\\DongfangOJBatchSubmit161",out var created);
        if(!created){submitMutex.Dispose();submitMutex=null;throw new Exception("原来的自动提交程序仍在运行。请等它结束或暂停并关闭后再启动本软件的队列。");}
        taskCancel=new();Running=true;_=RunQueue(taskCancel.Token);
    }
    static HashSet<string> SubmissionIds(HtmlDocument d)=>Nodes(d.DocumentNode,"//*[@data-submissionid]").Select(n=>Attr(n,"data-submissionid")).Concat(Nodes(d.DocumentNode,"//a[contains(@href,'/solution/result?id=')]").Select(n=>Regex.Match(Attr(n,"href"),@"id=(\d+)").Groups[1].Value)).Where(s=>Regex.IsMatch(s,@"^\d+$")).ToHashSet();
    async Task<bool> IsAccepted(string id,CancellationToken ct)
    {
        var doc=Html(await Request(client,HttpMethod.Get,"/problem/index?q="+id,ct:ct));
        var row=doc.DocumentNode.SelectSingleNode($"//tr[@data-key='{id}']");
        if(row==null)throw new Exception("无法确认该题当前通过状态，已暂停。");
        var cell=row.SelectSingleNode("./td[1]");return cell?.InnerHtml.Contains("fa-check")==true || Text(cell).Contains('\uf00c');
    }
    async Task Resolve(string id,string sid,CancellationToken ct)
    {
        for(int i=0;i<40;i++)
        {
            var raw=await Request(client,HttpMethod.Get,"/solution/verdict?id="+sid,ct:ct);
            var result=JsonDocument.Parse(raw).RootElement;
            if(result.ValueKind==JsonValueKind.String)result=JsonDocument.Parse(result.GetString()!).RootElement;
            if(!result.TryGetProperty("waiting",out var waiting) || !result.TryGetProperty("verdict",out var verdict))throw new Exception("评测响应异常，已保存提交编号，不会重复发送。");
            if(waiting.ToString().Equals("false",StringComparison.OrdinalIgnoreCase))
            {
                var r=GetRecord(id);r.Result=result.TryGetProperty("result",out var text)?text.ToString():verdict.ToString();r.Status=verdict.ToString()=="4"?"accepted":"failed";r.EverAccepted|=r.Status=="accepted";r.ExplicitRetry=false;UpdateSubmissionSnapshot(id,sid,r.Result);Save();
                await Log(id+" · "+r.Result,r.Status=="accepted"?"success":"error");await Emit("record",new{id,record=r});
                if(r.Status!="accepted")throw new Exception("题目 "+id+" 未通过。请修改代码后继续。");return;
            }
            await Task.Delay(3000,ct);
        }
        throw new Exception("评测超过120秒，已暂停并保留提交编号。");
    }
    async Task RunQueue(CancellationToken ct)
    {
        await runGate.WaitAsync();
        try
        {
            await Log("提交队列已启动，每次至少间隔 "+state.Settings.Interval+" 秒。");
            while(state.Queue.Count>0)
            {
                ct.ThrowIfCancellationRequested();string id=state.Queue[0];var r=GetRecord(id);
                if(!r.ExplicitRetry && await IsAccepted(id,ct)){r.Status="accepted";r.EverAccepted=true;r.Result="通过";state.Queue.RemoveAt(0);Save();await Log(id+" 已通过，跳过。");await Emit("refresh",new{});continue;}
                if(r.Status is "sending" or "pending")
                {
                    if(r.Submission.Length==0)throw new Exception(id+"上次提交结果不确定，请先到官网核对；本软件不会自动重发。");
                    await Resolve(id,r.Submission,ct);state.Queue.RemoveAt(0);Save();continue;
                }
                while((DateTimeOffset.Now-state.LastPost).TotalSeconds<state.Settings.Interval)
                {await Emit("countdown",new{id,seconds=(int)Math.Ceiling(state.Settings.Interval-(DateTimeOffset.Now-state.LastPost).TotalSeconds)});await Task.Delay(1000,ct);}
                var doc=Html(await Request(client,HttpMethod.Get,"/p/"+id,ct:ct));var f=Form(doc,"Solution[source]");var data=Fields(f);
                var language=Nodes(f,".//select[@name='Solution[language]']/option").FirstOrDefault(o=>Text(o)=="C++")??throw new Exception("缺少C++语言选项。");
                data["Solution[language]"]=Attr(language,"value");data["Solution[source]"]=await File.ReadAllTextAsync(FileAt("staged",id,".cpp"),ct);
                if(string.IsNullOrWhiteSpace(data["Solution[source]"]))throw new Exception("待提交代码为空。");
                var before=SubmissionIds(doc);ct.ThrowIfCancellationRequested();var snapshot=BeginSubmissionSnapshot(id,data["Solution[source]"]);r.Status="sending";r.Submission="";state.LastPost=DateTimeOffset.Now;Save();await Log("正在提交 "+id);
                // 一旦开始POST就完成读取响应；暂停只阻止下一题，避免取消导致结果不确定。
                var response=Html(await Request(client,HttpMethod.Post,Attr(f,"action"),data));state.LastPost=DateTimeOffset.Now;
                var ids=SubmissionIds(response);ids.ExceptWith(before);if(ids.Count!=1){Save();throw new Exception("无法唯一确认新提交编号，已暂停以避免重发。");}
                r.Submission=ids.Single();snapshot.SubmissionId=r.Submission;snapshot.Result="等待评测";SaveSnapshot("submissions",snapshot);r.Status="pending";Save();await Resolve(id,r.Submission,ct);state.Queue.RemoveAt(0);Save();await Emit("refresh",new{});
            }
            await Log("队列已全部处理完成。","success");
        }
        catch(OperationCanceledException){await Log("已暂停，进度保留。");}
        catch(Exception ex){await Log("任务暂停："+ex.Message,"error");}
        finally{Running=false;submitMutex?.Dispose();submitMutex=null;runGate.Release();await Emit("refresh",new{});}
    }
    async Task<object> RunCode(string id,string code,string input)
    {
        if(compiling)throw new Exception("已有测试正在运行。");if(!File.Exists(state.Settings.Compiler))throw new Exception("未找到C++编译器，请在设置中填写g++.exe路径。");
        compiling=true;var folder=Path.Combine(Root,"tests",id+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            var cpp=Path.Combine(folder,"main.cpp");var exe=Path.Combine(folder,"main.exe");await File.WriteAllTextAsync(cpp,code);
            var build=new ProcessStartInfo(state.Settings.Compiler){UseShellExecute=false,RedirectStandardError=true,RedirectStandardOutput=true,CreateNoWindow=true,WorkingDirectory=folder};
            foreach(var arg in new[]{cpp,"-std=c++11","-O2","-o",exe})build.ArgumentList.Add(arg);
            using var compiler=Process.Start(build)!;var err=compiler.StandardError.ReadToEndAsync();var cout=compiler.StandardOutput.ReadToEndAsync();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try{await compiler.WaitForExitAsync(timeout.Token);}catch{compiler.Kill(true);throw new Exception("编译超时。");}
            if(compiler.ExitCode!=0)return new{ok=false,output=await err,kind="编译失败"};
            var start=new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,WorkingDirectory=folder};
            using var process=Process.Start(start)!;var output=new StringBuilder();var error=new StringBuilder();
            async Task Drain(StreamReader reader,StringBuilder buffer){var chars=new char[4096];int n;while((n=await reader.ReadAsync(chars))>0){if(buffer.Length+n>1000000){try{process.Kill(true);}catch{}throw new Exception("输出超过1MB，已停止运行。");}buffer.Append(chars,0,n);}}
            var outTask=Drain(process.StandardOutput,output);var errorTask=Drain(process.StandardError,error);
            using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try{await process.StandardInput.WriteAsync(input.AsMemory(),limit.Token);process.StandardInput.Close();await process.WaitForExitAsync(limit.Token);await Task.WhenAll(outTask,errorTask);}
            catch(OperationCanceledException){process.Kill(true);throw new Exception("运行超过5秒，已停止。请检查循环或输入。");}
            return new{ok=process.ExitCode==0,output=output.ToString(),error=error.ToString(),exitCode=process.ExitCode,kind="运行完成"};
        }
        finally{compiling=false;}
    }
}
