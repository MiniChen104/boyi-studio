using GlassOJ;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.IO;

static class SubmissionChecks
{
    public static async Task Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"BoyiSubmissions-"+Guid.NewGuid().ToString("N"));
        var backend=new Backend(root,isolatedTest:true);
        await backend.Initialize();
        var pages=new SubmissionPages();
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(Backend).GetField("client",flags)!.SetValue(backend,new HttpClient(pages));
        typeof(Backend).GetField("loggedIn",flags)!.SetValue(backend,true);
        int count=0;
        async Task<JsonElement> Call(string action,object data)=>JsonSerializer.SerializeToElement(
            await backend.Handle(action,JsonSerializer.SerializeToElement(data)),Backend.Json);
        void Check(bool ok,string name)
        {
            if(!ok)throw new Exception(name);
            count++;Console.WriteLine("PASS "+name);
        }
        async Task Reject(string action,object data,string name)
        {
            try{await Call(action,data);}
            catch{Check(true,name);return;}
            throw new Exception("Unexpected acceptance: "+name);
        }

        var history=await Call("problemHistory",new{id="1000"});
        var rows=history.GetProperty("rows");
        Check(rows.GetArrayLength()==3,"problem sidebar returns all displayed attempts with duplicate source links removed");
        Check(rows[2].GetProperty("sid").GetString()=="83","failed attempt uses its source link too");
        Check(rows[0].GetProperty("cells")[1].GetString()=="2026-01-03 12:00:00","absolute submission time retained");
        Check(pages.StatusRequests==0,"problem history avoids obsolete status route");
        var source=await Call("submissionCode",new{sid="81",id="1000"});
        Check(source.GetProperty("code").GetString()==SubmissionPages.Code,"source fragment preserves whitespace and decodes entities exactly once");
        Check(source.GetProperty("problemId").GetString()=="1000","source is bound to problem from verified history");
        Check(pages.SourceRequests==1,"actual source endpoint used without detail request");
        Check((await Call("localSubmissions",new{id="1000"})).GetProperty("items").GetArrayLength()==1,"downloaded source cached in current account");
        await Call("submissionCode",new{sid="81",id="1000"});
        Check((await Call("localSubmissions",new{id="1000"})).GetProperty("items").GetArrayLength()==1,"repeat viewing does not duplicate saved submission");
        await Reject("submissionCode",new{sid="81",id="1002"},"caller cannot attach historical source to another problem");
        var requests=pages.SourceRequests;
        await Reject("submissionCode",new{sid="999",id="1000"},"unrelated page link cannot authorize source request");
        Check(requests==pages.SourceRequests,"unverified source is rejected before network access");
        foreach(var mode in new[]{"wrong-id","login","missing-source","foreign-author","forbidden"})
        {
            pages.Mode=mode;
            await Reject("submissionCode",new{sid="82",id="1000"},"source response rejected: "+mode);
        }
        Check((await Call("localSubmissions",new{id="1000"})).GetProperty("items").GetArrayLength()==1,"invalid source responses never cached");
        pages.Mode="normal";
        var recent=await Call("onlineHistory",new{});
        Check(recent.GetProperty("rows")[0].GetProperty("sid").GetString()=="84","recent profile history recognizes source links");
        Check((await Call("submissionCode",new{sid="84",id="1000"})).GetProperty("code").GetString()==SubmissionPages.Code,"recent history entry opens its original source");
        pages.Empty=true;
        Check((await Call("problemHistory",new{id="1002"})).GetProperty("rows").GetArrayLength()==0,"empty sidebar produces empty history");
        pages.Expired=true;
        await Reject("problemHistory",new{id="1000"},"expired account cannot read problem history");
        pages.Expired=false;
        pages.BeforeSource=()=>typeof(Backend).GetMethod("SwitchAccount",flags)!.Invoke(backend,["other_student"]);
        await Reject("submissionCode",new{sid="83",id="1000"},"account switch during source request discards response");
        Check((await Call("localSubmissions",new{id="1000"})).GetProperty("items").GetArrayLength()==0,"foreign account receives no previous account source");
        pages.BeforeSource=null;
        typeof(Backend).GetMethod("SwitchAccount",flags)!.Invoke(backend,["ui_test"]);
        await Call("logout",new{});
        await Reject("submissionCode",new{sid="81",id="1000"},"logout prevents online source reads");
        var saved=await Call("submissionVersion",new{id="1000",key="official-81"});
        Check(saved.GetProperty("code").GetString()==SubmissionPages.Code,"saved submission remains readable offline");
        Console.WriteLine($"{count} submission checks passed");
    }
}

sealed class SubmissionPages:HttpMessageHandler
{
    public const string Code="\n#include<iostream>\nint main()\n{\n\tstd::cout << \"&lt; & < >\";\n}\n";
    const string Nav="<nav><a href='/user/view?id=17'>ui_test</a><a href='/site/logout'>退出</a></nav>";
    public int SourceRequests,StatusRequests;
    public bool Empty,Expired;
    public string Mode="normal";
    public Action? BeforeSource;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        var uri=request.RequestUri!;
        string body;
        var status=HttpStatusCode.OK;
        if(Expired)body="<input name='LoginForm[password]'>";
        else if(uri.AbsolutePath.StartsWith("/p/"))
        {
            body=Nav+"<h3>1000 - 测试题目</h3><a href='/solution/source?id=999'>其他区域</a><div class='panel panel-default'><div class='panel-heading'>提交记录</div><table><tbody>";
            if(!Empty)foreach(var sid in new[]{"81","82","83"})
                body+=$"<tr><td title='2026-01-03 12:00:00'>5天前</td><td><a href='/solution/{(sid=="83"?"result":"source")}?id={sid}' data-click='solution_info'><strong>{(sid=="83"?"解答错误":"通过")}</strong></a></td><td><a href='/solution/source?id={sid}' title='查看源码'>查看</a></td></tr>";
            body+="</tbody></table></div>";
        }
        else if(uri.AbsolutePath=="/solution/source")
        {
            SourceRequests++;BeforeSource?.Invoke();
            var sid=System.Web.HttpUtility.ParseQueryString(uri.Query)["id"];
            body=$"<div class='solution-view'><div class='row'><p>提交时间：2026-01-03 12:00:00</p><p>运行 ID: <a href='/solution/detail?id={(Mode=="wrong-id"?"999":sid)}'>{sid}</a></p></div><div class='pre'><p>{WebUtility.HtmlEncode(Code)}</p></div></div>";
            if(Mode=="login")body="<input name='LoginForm[password]'>";
            if(Mode=="missing-source")body=$"<div class='solution-view'><a href='/solution/detail?id={sid}'>{sid}</a></div>";
            if(Mode=="foreign-author")body=body.Replace("<div class='pre'>","<a href='/user/view?id=other'>other</a><div class='pre'>");
            if(Mode=="forbidden")status=HttpStatusCode.Forbidden;
        }
        else if(uri.AbsolutePath=="/status/index")
        {
            StatusRequests++;
            body=Nav+"<h1>问题列表</h1>";
        }
        else if(uri.AbsolutePath=="/user/view")
            body=Nav+"<div class='user-view'><h1>ui_test</h1><a class='list-group-item' href='/solution/source?id=84'><span>1000.测试题目</span><span>通过</span></a></div>";
        else throw new Exception("Unexpected request "+uri.AbsolutePath);
        return Task.FromResult(new HttpResponseMessage(status){Content=new StringContent(body)});
    }
}
