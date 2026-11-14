using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using HtmlAgilityPack;

namespace GlassOJ;

public partial class Backend
{
    bool accountBusy,progressSyncing;
    string progressMessage="尚未同步官网进度";
    CancellationTokenSource? progressCancel;
    Task progressTask=Task.CompletedTask;
    readonly SemaphoreSlim accountGate=new(1,1);
    string AccountDirectory(string account)=>Path.Combine(Root,"accounts",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account))).ToLowerInvariant());
    void EnsureAccountDirectories(string account)
    {
        foreach(var folder in new[]{"drafts","staged"})Directory.CreateDirectory(Path.Combine(AccountDirectory(account),folder));
    }
    void MigrateAccountFiles()
    {
        EnsureAccountDirectories(state.Account);
        if(state.AccountFilesMigrated)return;
        // Copy once; keep the original files as a recovery copy.
        foreach(var folder in new[]{"drafts","staged"})
            foreach(var file in Directory.EnumerateFiles(Path.Combine(Root,folder),"*.cpp"))
            {
                var target=Path.Combine(AccountDirectory(state.Account),folder,Path.GetFileName(file));
                if(!File.Exists(target))File.Copy(file,target);
            }
        state.AccountFilesMigrated=true;
    }
    void SwitchAccount(string account)
    {
        if(account==state.Account)return;
        Save();ownedSubmissions.Clear();
        var settings=state.Settings;
        var lastPost=state.LastPost;
        var path=Path.Combine(AccountDirectory(account),"progress.json");
        state=File.Exists(path)?JsonSerializer.Deserialize<State>(File.ReadAllText(path),Json)??new():new();
        state.Account=account;state.Settings=settings;state.AccountFilesMigrated=true;
        if(state.LastPost<lastPost)state.LastPost=lastPost;
        EnsureAccountDirectories(account);LoadAccountProblems();logs.Clear();Save();
    }
    async Task CancelProgressSync()
    {
        progressCancel?.Cancel();
        await progressTask;
        progressCancel?.Dispose();progressCancel=null;
    }
    static bool Authenticated(HtmlDocument doc,string account)
    {
        if(doc.DocumentNode.SelectSingleNode("//input[@name='LoginForm[password]']")!=null)return false;
        var nav=doc.DocumentNode.SelectSingleNode("//nav");
        if(nav==null)return false;
        var profileMatches=Nodes(nav,".//a[@href]").Any(n=>
        {
            if(!Uri.TryCreate(new Uri(Site),Attr(n,"href"),out var uri) || uri.Host!="oj.czos.cn" || uri.AbsolutePath!="/user/view")return false;
            return System.Web.HttpUtility.ParseQueryString(uri.Query)["id"]==account;
        });
        return (Text(nav).Contains(account,StringComparison.Ordinal) || profileMatches) &&
            Nodes(nav,".//*[@href or @action]").Any(n=>Attr(n,"href").Contains("/site/logout") || Attr(n,"action").Contains("/site/logout"));
    }
    async Task Login(string username,string password)
    {
        if(!Regex.IsMatch(username,@"^[a-zA-Z0-9_]{3,40}$") || password.Length==0)throw new Exception("请输入正确的账号和密码。");
        await accountGate.WaitAsync();accountBusy=true;
        HttpClient? candidate=null;
        try
        {
            if(Running)throw new Exception("请先暂停提交任务。");
            candidate=NewClient();
            var d=Html(await Request(candidate,HttpMethod.Get,"/site/login"));var f=Form(d,"LoginForm[password]");var fields=Fields(f);
            fields["LoginForm[username]"]=username;fields["LoginForm[password]"]=password;fields["LoginForm[rememberMe]"]="0";
            var response=Html(await Request(candidate,HttpMethod.Post,Attr(f,"action"),fields));fields.Clear();
            if(!Authenticated(response,username))
            {
                var error=string.Join(" ",Nodes(response.DocumentNode,"//*[contains(@class,'help-block-error')]").Select(Text).Where(x=>x.Length>0));
                throw new Exception(error.Length>0?error:"未能确认登录成功。如官网要求验证码，请使用“官网验证登录”。");
            }
            await ConnectAccount(candidate,username);candidate=null;
        }
        finally{candidate?.Dispose();accountBusy=false;accountGate.Release();}
    }
    async Task ConnectAccount(HttpClient next,string account)
    {
        await CancelProgressSync();
        SwitchAccount(account);client.Dispose();client=next;loggedIn=true;
        Save();await Log("已连接官网账号 "+account,"success");await Emit("accountChanged",new{account});
        BeginProgressSync();
    }
    public async Task ConnectBrowserSession(CookieContainer cookies,string account)
    {
        if(!Regex.IsMatch(account,@"^[a-zA-Z0-9_]{3,40}$"))throw new Exception("请填写刚才登录的官网账号。");
        await accountGate.WaitAsync();accountBusy=true;HttpClient? candidate=null;
        try
        {
            if(Running)throw new Exception("请先暂停提交任务。");
            candidate=NewClient(cookies);
            var doc=Html(await Request(candidate,HttpMethod.Get,"/problem/index"));
            if(!Authenticated(doc,account))throw new Exception("未确认该账号的登录状态，请完成官网登录后再连接。");
            await ConnectAccount(candidate,account);candidate=null;
        }
        finally{candidate?.Dispose();accountBusy=false;accountGate.Release();}
    }
    async Task Logout()
    {
        await accountGate.WaitAsync();accountBusy=true;
        try
        {
            if(Running)throw new Exception("请先暂停提交。");
            await CancelProgressSync();client.Dispose();client=NewClient();loggedIn=false;ownedSubmissions.Clear();
            progressMessage="已断开连接 · 本地进度保留";await Emit("refresh",new{});
        }
        finally{accountBusy=false;accountGate.Release();}
    }
    void BeginProgressSync()
    {
        if(progressSyncing || !loggedIn)return;
        progressCancel=new();progressTask=SyncAccepted(progressCancel.Token);
    }
    async Task SyncAccepted(CancellationToken ct)
    {
        progressSyncing=true;progressMessage="正在核对官网通过记录…";
        try
        {
            var profile=Html(await Request(client,HttpMethod.Get,"/user/view?id="+Uri.EscapeDataString(state.Account),ct:ct));
            var profilePassed=ProfileAccepted(profile,state.Account);
            if(profilePassed!=null)
            {
                ct.ThrowIfCancellationRequested();
                foreach(var id in profilePassed){var record=GetRecord(id);record.Status="accepted";record.EverAccepted=true;record.Result="通过";}
                state.ProgressSyncedAt=DateTimeOffset.Now;Save();
                progressMessage=$"官网进度已同步 · {profilePassed.Count} 道通过";
                await Log(progressMessage,"success");return;
            }
            string? next="/problem/index";var seen=new HashSet<string>();var passed=new HashSet<string>();int pages=0;
            await Emit("refresh",new{});
            while(next!=null)
            {
                ct.ThrowIfCancellationRequested();
                if(!seen.Add(next))throw new Exception("官网分页出现重复，已停止同步。");
                var doc=Html(await Request(client,HttpMethod.Get,next,ct:ct));
                if(!Authenticated(doc,state.Account))throw new Exception("登录状态已过期，请重新登录。");
                var rows=Nodes(doc.DocumentNode,"//tr[@data-key]").ToList();
                if(rows.Count==0)throw new Exception("未识别到官网题库列表，请稍后重试。");
                foreach(var row in rows)
                {
                    var id=Attr(row,"data-key");var cell=row.SelectSingleNode("./td[1]");
                    if(Regex.IsMatch(id,@"^\d+$") && (cell?.InnerHtml.Contains("fa-check")==true || Text(cell).Contains('\uf00c')))passed.Add(id);
                }
                next=NextPage(doc,"/problem/index");pages++;
                progressMessage=$"已核对 {pages} 页，找到 {passed.Count} 道已通过题";
                await Emit("progressSync",new{message=progressMessage});
                if(next!=null)await Task.Delay(750,ct);
            }
            ct.ThrowIfCancellationRequested();
            foreach(var id in passed){var record=GetRecord(id);record.Status="accepted";record.EverAccepted=true;record.Result="通过";}
            state.ProgressSyncedAt=DateTimeOffset.Now;Save();
            progressMessage=$"官网进度已同步 · {passed.Count} 道通过";
            await Log(progressMessage,"success");
        }
        catch(OperationCanceledException){progressMessage="同步已停止，保留上次进度";}
        catch(Exception ex){progressMessage="同步未完成："+ex.Message;await Log(progressMessage,"warning");}
        finally{progressSyncing=false;await Emit("refresh",new{});}
    }
    static HtmlNode? ProfileRoot(HtmlDocument doc,string account)
    {
        var root=doc.DocumentNode.SelectSingleNode("//div[contains(concat(' ',normalize-space(@class),' '),' user-view ')]");
        return root!=null && Text(root.SelectSingleNode(".//h1"))==account?root:null;
    }
    static HashSet<string>? ProfileAccepted(HtmlDocument doc,string account)
    {
        var root=ProfileRoot(doc,account);if(root==null)return null;
        var heading=Nodes(root,".//h3").FirstOrDefault(h=>Text(h).StartsWith("已解答",StringComparison.Ordinal));
        if(heading==null)return null;
        var count=Regex.Match(Text(heading),@"[（(]\s*(\d+)\s*[)）]");
        if(!count.Success)return null;
        var list=heading.SelectSingleNode("following-sibling::*[1][self::ul]");if(list==null)return null;
        var ids=Nodes(list,".//a[@href]").Select(a=>Regex.Match(Attr(a,"href"),@"^/p/(\d+)$")).Where(m=>m.Success).Select(m=>m.Groups[1].Value).ToHashSet();
        return ids.Count==int.Parse(count.Groups[1].Value)?ids:null;
    }
    static object ProfileHistory(HtmlDocument doc,string account)
    {
        var root=ProfileRoot(doc,account)??throw new Exception("官网返回的个人主页与当前账号不一致。");
        var links=Nodes(root,".//a[contains(concat(' ',normalize-space(@class),' '),' list-group-item ')]")
            .Where(a=>Regex.IsMatch(Attr(a,"href"),@"^/solution/detail\?id=\d+$")).ToList();
        var rows=links.Select(a=>
        {
            var id=Regex.Match(Attr(a,"href"),@"id=(\d+)").Groups[1].Value;
            var spans=Nodes(a,"./span").ToArray();
            string Clean(string value)=>Regex.Replace(value,@"\s+"," ").Trim();
            return new{sid=id,problemId=Regex.Match(Clean(Text(spans.ElementAtOrDefault(0))),@"^(\d+)[.．、\s]").Groups[1].Value,cells=new[]{id,Clean(Text(spans.ElementAtOrDefault(0))),Clean(Text(spans.ElementAtOrDefault(1)))}};
        }).ToArray();
        return new{headers=new[]{"提交编号","题目","评测结果 / 时间"},rows,next=(string?)null,scope="recent",note=$"来自官网个人主页的最近 {rows.Length} 条提交，不代表全部历史记录。"};
    }
    static string? NextPage(HtmlDocument doc,string allowedPath)
    {
        var link=doc.DocumentNode.SelectSingleNode("//li[contains(concat(' ',normalize-space(@class),' '),' next ') and not(contains(@class,'disabled'))]/a[@href]");
        if(link==null)return null;
        var u=Url(Attr(link,"href"));
        if(u.AbsolutePath!=allowedPath)throw new Exception("官网分页地址异常。");
        return u.PathAndQuery;
    }
    string OnlinePath(string path,string expected)
    {
        var uri=Url(string.IsNullOrWhiteSpace(path)?expected:path);
        if(uri.AbsolutePath!=expected)throw new Exception("页面地址无效。");
        return uri.PathAndQuery;
    }
    async Task<HtmlDocument> AccountPage(string path)
    {
        if(!loggedIn)throw new Exception("请先连接你的东方博宜账号。");
        var account=state.Account;var connection=client;
        var doc=Html(await Request(connection,HttpMethod.Get,path));
        if(account!=state.Account || connection!=client)throw new Exception("账号已切换，请重新打开页面。");
        if(!Authenticated(doc,account))throw new Exception("登录已过期，请重新登录。");
        return doc;
    }
    static string JsonText(JsonElement row,string name)=>row.TryGetProperty(name,out var value)?value.ToString():"";
    async Task<object> OnlineLists(string path)
    {
        path=OnlinePath(path,"/lists/index");
        var account=state.Account;var connection=client;
        var doc=await AccountPage(path);
        // The current directory is populated by bootstrap-table's read-only POST endpoint.
        if(doc.DocumentNode.SelectSingleNode("//table[@id='table']")!=null &&
            Nodes(doc.DocumentNode,"//script").Any(n=>n.InnerText.Contains("bootstrapTable")))
        {
            var query=System.Web.HttpUtility.ParseQueryString(Url(path).Query);
            var type=query["t"]??"2";
            if(!new[]{"0","1","2"}.Contains(type))throw new Exception("题单分类无效。");
            if(!int.TryParse(query["offset"]??"0",out var offset) || offset<0 || offset>int.MaxValue-50)throw new Exception("题单分页无效。");
            var search=query["q"]??"";
            var fields=new Dictionary<string,string>{{"t",type},{"a","0"},{"q",search},{"offset",offset.ToString()},{"limit","50"}};
            var csrfName=Attr(doc.DocumentNode.SelectSingleNode("//meta[@name='csrf-param']"),"content");
            var csrfValue=Attr(doc.DocumentNode.SelectSingleNode("//meta[@name='csrf-token']"),"content");
            if(csrfName.Length>0 && csrfValue.Length>0)fields[csrfName]=csrfValue;
            var response=await Request(connection,HttpMethod.Post,"/lists/index",fields);
            if(account!=state.Account || connection!=client)throw new Exception("账号已切换，请重新打开题单。");
            JsonDocument result;
            try{result=JsonDocument.Parse(response);}
            catch(JsonException){throw new Exception("官网题单数据未能读取，登录可能已过期，请重新连接账号后重试。");}
            using(result)
            {
                var value=result.RootElement;
                if(value.ValueKind!=JsonValueKind.Object || !value.TryGetProperty("rows",out var rows) || rows.ValueKind!=JsonValueKind.Array ||
                    !int.TryParse(JsonText(value,"total"),out var total) || total<0)
                    throw new Exception("官网题单返回格式已变化，请通过官网完整功能查看。");
                var items=rows.EnumerateArray().Select(row=>
                {
                    var id=JsonText(row,"id");
                    if(!Regex.IsMatch(id,@"^\d+$"))throw new Exception("官网题单编号无效。");
                    return new{path="/lists/view?id="+id,title=HtmlEntity.DeEntitize(JsonText(row,"name")),description=string.Join(" · ",new[]{JsonText(row,"req")=="0"?"公开":"需访问权限",JsonText(row,"showname"),JsonText(row,"created_at")}.Where(x=>x.Length>0))};
                }).ToArray();
                if(items.Length==0 && offset<total)throw new Exception("官网题单分页数据不完整，请刷新重试。");
                string Page(int start)=>"/lists/index?t="+type+"&q="+Uri.EscapeDataString(search)+"&offset="+start;
                var filters=new[]{new{path="/lists/index?t=2",title="官方题单"},new{path="/lists/index?t=1",title="老师推荐"},new{path="/lists/index?t=0",title="我的题单"}};
                return new{items,filters,next=offset+items.Length<total?Page(offset+items.Length):null,path=Page(0),total,source="东方博宜 · 题单中心"};
            }
        }
        var links=Nodes(doc.DocumentNode,"//a[@href]").Where(n=>Regex.IsMatch(Attr(n,"href"),@"^/lists/view\?id=\d+"));
        var legacyItems=links.GroupBy(n=>Attr(n,"href")).Select(g=>new{path=g.Key,title=Text(g.First()),description=Text(g.First().Ancestors("tr").FirstOrDefault())}).ToArray();
        var legacyFilters=Nodes(doc.DocumentNode,"//ul[contains(@class,'nav-tabs') or contains(@class,'nav-pills')]//a[@href]")
            .Where(n=>Attr(n,"href").StartsWith("/lists/index",StringComparison.Ordinal))
            .Select(n=>new{path=Attr(n,"href"),title=Text(n)}).DistinctBy(x=>x.path).ToArray();
        if(legacyItems.Length==0 && doc.DocumentNode.SelectSingleNode("//*[contains(concat(' ',normalize-space(@class),' '),' empty ')]")==null)
            throw new Exception("未识别到官网题单目录，请刷新或通过官网完整功能查看。");
        return new{items=legacyItems,filters=legacyFilters,next=NextPage(doc,"/lists/index"),path,source="东方博宜 · 当前账号可访问的题单"};
    }
    static string ListProblemId(HtmlNode row)
    {
        var id=Attr(row,"data-pid");
        if(Regex.IsMatch(id,@"^\d+$"))return id;
        foreach(var link in Nodes(row,".//a[@href]"))
        {
            if(!Uri.TryCreate(new Uri(Site),Attr(link,"href"),out var uri) || uri.Host!="oj.czos.cn")continue;
            var match=Regex.Match(uri.AbsolutePath,@"^/p/(\d+)$");
            if(match.Success)return match.Groups[1].Value;
        }
        return "";
    }
    async Task<object> OnlineList(string path)
    {
        path=OnlinePath(path,"/lists/view");
        if(!Regex.IsMatch(path,@"[?&]id=\d+(?:&|$)"))throw new Exception("题单编号无效。");
        var doc=await AccountPage(path);
        var rows=Nodes(doc.DocumentNode,"//tr").Select(row=>new{row,id=ListProblemId(row)}).Where(x=>x.id.Length>0).ToArray();
        var items=rows.Select(x=>
        {
            var links=Nodes(x.row,".//a[@href]").Where(a=>Attr(a,"class").Split(' ').Contains("btn_view") ||
                Uri.TryCreate(new Uri(Site),Attr(a,"href"),out var uri) && uri.Host=="oj.czos.cn" && uri.AbsolutePath=="/p/"+x.id).ToArray();
            var title=Text(links.LastOrDefault());
            if(title.Length==0)title="题目 "+x.id;
            var accepted=x.row.SelectSingleNode("./*[self::td or self::th][1]")?.InnerHtml.Contains("fa-check")==true;
            return new{id=x.id,title,accepted=accepted || GetRecord(x.id).Status=="accepted" || GetRecord(x.id).EverAccepted,available=true};
        }).DistinctBy(x=>x.id).ToArray();
        if(items.Length==0 && !Regex.IsMatch(Text(doc.DocumentNode),@"共\s*0\s*题|暂无题目|没有题目"))
            throw new Exception("未能读取此题单的题目。请刷新重试；加密题单请先通过官网完整功能确认访问权限。");
        var titleNode=doc.DocumentNode.SelectSingleNode("//*[contains(concat(' ',normalize-space(@class),' '),' list-title ')]");
        var title=Text(titleNode);
        if(title.Length==0)title=Text(doc.DocumentNode.SelectSingleNode("//h1|//h2"));
        if(title.Length==0)title=Text(doc.DocumentNode.SelectSingleNode("//ul[contains(@class,'breadcrumb')]/li[last()]"));
        return new{title,items,next=NextPage(doc,"/lists/view")};
    }
    async Task<object> OnlineHistory(string path)
    {
        path=OnlinePath(path,"/status/index");
        // Always bind the official filter to the current account, including pagination.
        var query=System.Web.HttpUtility.ParseQueryString(Url(path).Query);query["SolutionSearch[user_id]"]=state.Account;
        var doc=await AccountPage("/status/index?"+query);
        var table=Nodes(doc.DocumentNode,"//table").FirstOrDefault(t=>Nodes(t,".//a[@href]").Any(a=>Attr(a,"href").Contains("/solution/")) || Text(t.SelectSingleNode(".//thead")).Contains("运行"));
        if(table==null)
        {
            var profile=await AccountPage("/user/view?id="+Uri.EscapeDataString(state.Account));
            var recent=ProfileHistory(profile,state.Account);
            foreach(var row in JsonSerializer.SerializeToElement(recent,Json).GetProperty("rows").EnumerateArray())ownedSubmissions[row.GetProperty("sid").GetString()!]=state.Account;
            var recentJson=JsonSerializer.SerializeToElement(recent,Json);
            var requestedProblem=query["SolutionSearch[problem_id]"];
            if(!string.IsNullOrEmpty(requestedProblem))return new{headers=recentJson.GetProperty("headers"),rows=recentJson.GetProperty("rows").EnumerateArray().Where(r=>r.GetProperty("problemId").GetString()==requestedProblem).ToArray(),next=(string?)null,scope="recent",note="官网完整记录暂不可读；这里只筛选个人主页最近提交中属于本题的记录，不代表本题全部历史。"};
            return recent;
        }
        var headers=Nodes(table,".//thead//th").Select(Text).ToArray();
        var rows=Nodes(table,".//tbody/tr[td]").Where(r=>r.SelectSingleNode(".//*[contains(@class,'empty')]")==null).Select(r=>
        {
            var sid=Nodes(r,".//a[@href]").Select(a=>Regex.Match(Attr(a,"href"),@"^/solution/(?:result|detail)\?id=(\d+)")).FirstOrDefault(m=>m.Success)?.Groups[1].Value??"";
            var pid=Nodes(r,".//a[@href]").Select(a=>Regex.Match(Attr(a,"href"),@"^/p/(\d+)$")).FirstOrDefault(m=>m.Success)?.Groups[1].Value??"";
            var owners=Nodes(r,".//a[contains(@href,'/user/view?')]").Select(a=>System.Web.HttpUtility.ParseQueryString(Url(Attr(a,"href")).Query)["id"]).ToArray();
            if(owners.Length>0 && !owners.All(x=>x==state.Account))return null;
            if(sid.Length>0 && owners.Length>0)ownedSubmissions[sid]=state.Account;
            return new{sid,problemId=pid,cells=Nodes(r,"./td").Select(Text).ToArray()};
        }).Where(r=>r!=null).ToArray();
        return new{headers,rows,next=NextPage(doc,"/status/index")};
    }
}
