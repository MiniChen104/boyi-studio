using System.Net;
using System.Net.Http;

sealed class OfficialPages:HttpMessageHandler
{
    public Dictionary<string,string> LastFields=new();
    public string LastGroupPath="";
    public bool Ajax,Malformed,ExpiredPost,ForeignNext;
    const string Nav="<nav><a href='/user/view?id=ui_test'>ui_test</a><a href='/site/logout'>退出</a></nav>";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        var uri=request.RequestUri!;
        string body;
        if(uri.AbsolutePath=="/lists/index"&&request.Method==HttpMethod.Post)
        {
            var form=System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(token));
            LastFields=form.AllKeys.ToDictionary(k=>k!,k=>form[k]!);
            Ajax=request.Headers.TryGetValues("X-Requested-With",out var headers)&&headers.Contains("XMLHttpRequest");
            body=ExpiredPost?"<input name='LoginForm[password]'>":Malformed?"{}":LastFields["t"]=="0"?
                "{\"total\":0,\"rows\":[]}":"{\"total\":51,\"rows\":[{\"id\":\"1\",\"name\":\"整数 &amp; 小数\",\"req\":\"0\",\"showname\":\"测试教师\",\"created_at\":\"2026-01-01\"}]}";
        }
        else if(uri.AbsolutePath=="/lists/index")
            body=Nav+"<meta name='csrf-param' content='_csrf'><meta name='csrf-token' content='fixture-token'><table id='table'></table><script>$('#table').bootstrapTable({method:'post',url:'/lists/index'});</script>";
        else if(uri.AbsolutePath=="/lists/view")
        {
            body=Nav+(uri.Query=="?id=99"?"<h1>访问受限</h1>":uri.Query=="?id=0"?"<h1>空题单</h1><h4>共0题</h4>":"""
                <ul class="breadcrumb"><li>题单中心</li><li>基本运算</li></ul>
                <table><tbody>
                  <tr data-pid="1000" data-cid="1" data-copyid="1000"><th><span class="fa fa-check text-success"></span></th><td><a href="javascript:void(0);" class="btn_view">1000</a></td><td><a href="javascript:void(0);" class="btn_view">环境练习</a></td><td>108022</td></tr>
                  <tr data-pid="1311" data-cid="1"><th></th><td><a href="javascript:void(0);" class="btn_view">1311</a></td><td><a href="javascript:void(0);" class="btn_view">分跳绳</a></td><td>60576</td></tr>
                  <tr><td></td><td><a href="https://oj.czos.cn/p/1002?from=list">旧版题目</a></td></tr>
                  <tr data-pid="1000"><td><a class="btn_view">重复题目</a></td></tr>
                  <tr><td><a href="https://example.com/p/9000">外站链接</a></td></tr>
                </tbody></table>
                """);
        }
        else if(uri.AbsolutePath=="/group/my-group")
        {
            LastGroupPath=uri.PathAndQuery;
            body=Nav+"<ul class='nav'><li><a href='/group/my-group'>我的小组</a></li><li><a href='/group/index'>全部小组</a></li></ul><div class='card'><a href='/group/view?id=8'>我的班级</a><p class='card-text'>无说明</p></div><ul><li class='next'><a href='"+
                (ForeignNext?"/group/index?page=2":"/group/my-group?name=class&amp;page=2")+"'>下一页</a></li></ul>";
        }
        else throw new Exception("Unexpected request "+uri.AbsolutePath);
        return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)};
    }
}
