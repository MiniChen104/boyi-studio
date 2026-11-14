using GlassOJ;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.IO;

var root=Path.Combine(Path.GetTempPath(),"BoyiOnline-"+Guid.NewGuid().ToString("N"));
var backend=new Backend(root,isolatedTest:true);
await backend.Initialize();
var official=new OfficialPages();
var flags=BindingFlags.Instance|BindingFlags.NonPublic;
typeof(Backend).GetField("client",flags)!.SetValue(backend,new HttpClient(official));
typeof(Backend).GetField("loggedIn",flags)!.SetValue(backend,true);
int checks=0;
async Task<JsonElement> Call(string action,string path="")=>JsonSerializer.SerializeToElement(
    await backend.Handle(action,JsonSerializer.SerializeToElement(new{path})),Backend.Json);
void Check(bool ok,string name)
{
    if(!ok)throw new Exception(name);
    checks++;Console.WriteLine("PASS "+name);
}
async Task Reject(string action,string path,string name)
{
    try{await Call(action,path);}
    catch{Check(true,name);return;}
    throw new Exception("Unexpected acceptance: "+name);
}

var directory=await Call("onlineLists");
Check(directory.GetProperty("items")[0].GetProperty("title").GetString()=="整数 & 小数","AJAX directory data decoded");
Check(official.LastFields["t"]=="2"&&official.LastFields["limit"]=="50","official category and page size sent");
Check(official.LastFields["_csrf"]=="fixture-token"&&official.Ajax,"CSRF and AJAX header sent");
Check(directory.GetProperty("filters").GetArrayLength()==3,"all three list categories available");
Check(directory.GetProperty("next").GetString()!.Contains("offset=1"),"pagination based on returned row count");
await Call("onlineLists","/lists/index?t=1&q=%E5%BE%AA%E7%8E%AF&offset=50");
Check(official.LastFields["t"]=="1"&&official.LastFields["q"]=="循环"&&official.LastFields["offset"]=="50","category search and offset retained");
directory=await Call("onlineLists","/lists/index?t=0");
Check(directory.GetProperty("items").GetArrayLength()==0&&directory.GetProperty("next").ValueKind==JsonValueKind.Null,"empty personal list is a valid result");
await Reject("onlineLists","/lists/index?t=admin","invalid category rejected");
await Reject("onlineLists","/lists/index?offset=-1","negative offset rejected");
official.Malformed=true;
await Reject("onlineLists","","malformed AJAX response does not appear empty");
official.Malformed=false;
official.ExpiredPost=true;
await Reject("onlineLists","","login HTML in AJAX response rejected");
official.ExpiredPost=false;

var list=await Call("onlineList","/lists/view?id=1");
var items=list.GetProperty("items");
Check(items.GetArrayLength()==3,"data-pid rows and legacy links parsed with duplicates removed");
Check(items[0].GetProperty("id").GetString()=="1000"&&items[0].GetProperty("title").GetString()=="环境练习","JavaScript link maps to question id and title");
Check(items[0].GetProperty("accepted").GetBoolean(),"passed checkmark in TH recognized");
Check(items[1].GetProperty("id").GetString()=="1311"&&!items[1].GetProperty("accepted").GetBoolean(),"uncatalogued problem remains available without false acceptance");
Check(items[2].GetProperty("id").GetString()=="1002","absolute legacy question link with query parsed");
Check(list.GetProperty("title").GetString()=="基本运算","breadcrumb provides title on current website");
await Reject("onlineList","/lists/view?id=99","unrecognized detail page yields error");
Check((await Call("onlineList","/lists/view?id=0")).GetProperty("items").GetArrayLength()==0,"explicitly empty problem list supported");

var groups=await Call("onlineGroups");
Check(official.LastGroupPath=="/group/my-group","group directory uses official My Groups route");
Check(groups.GetProperty("items").GetArrayLength()==1,"only personal directory results displayed");
Check(groups.GetProperty("filters").GetArrayLength()==1,"public directory filter excluded");
Check(groups.GetProperty("next").GetString()=="/group/my-group?name=class&page=2","group pagination stays scoped");
await Call("onlineGroups","/group/index?name=class&page=2");
Check(official.LastGroupPath=="/group/my-group?name=class&page=2","old cached public route remapped with search and pagination");
await Reject("onlineGroups","https://example.com/group/my-group","external group URL rejected");
official.ForeignNext=true;
await Reject("onlineGroups","","public directory pagination rejected");
Console.WriteLine($"{checks} online regression checks passed");

await SubmissionChecks.Run();
