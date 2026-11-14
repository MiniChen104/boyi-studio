using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GlassOJ;

// The official site keeps its own authorization and forms; no native RPC is exposed.
public sealed class OfficialWindow:Window
{
    readonly Backend backend;
    readonly WebView2 browser=new();
    readonly TextBlock status=new(){Margin=new Thickness(12),Text="正在打开官网完整功能…",TextWrapping=TextWrapping.Wrap};
    readonly string startPath,account;
    readonly Func<string,Task> openProblem;
    public OfficialWindow(Backend backend,string path,Func<string,Task> openProblem)
    {
        this.backend=backend;this.openProblem=openProblem;account=backend.CurrentAccount;
        var uri=Backend.Url(path);
        if(!new[]{"/group/","/lists/","/solution/","/status/"}.Any(prefix=>uri.AbsolutePath.StartsWith(prefix,StringComparison.Ordinal)))throw new Exception("官网入口无效。");
        startPath=uri.ToString();Title="博宜练习室 · 官网完整功能";Width=1150;Height=820;MinWidth=850;MinHeight=600;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel();var bar=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(12)};DockPanel.SetDock(bar,Dock.Top);root.Children.Add(bar);
        void Button(string label,Action action){var button=new Button{Content=label,Padding=new Thickness(12,7,12,7),Margin=new Thickness(0,0,8,0)};button.Click+=(_,_)=>action();bar.Children.Add(button);}
        Button("← 后退",()=>{if(browser.CoreWebView2?.CanGoBack==true)browser.GoBack();});
        Button("刷新",()=>browser.CoreWebView2?.Reload());Button("小组",()=>browser.CoreWebView2?.Navigate("https://oj.czos.cn/group/my-group"));Button("题单",()=>browser.CoreWebView2?.Navigate("https://oj.czos.cn/lists/index"));
        Button("在练习室打开当前题",async ()=>
        {
            try{var match=System.Text.RegularExpressions.Regex.Match(browser.Source?.AbsolutePath??"",@"^/p/(\d+)$");if(!match.Success){status.Text="先在官网打开一道题，再点击此按钮。";return;}if(backend.CurrentAccount!=account)throw new Exception("账号已切换，请重新打开官网窗口。");await openProblem(match.Groups[1].Value);Close();}catch(Exception ex){status.Text=ex.Message;}
        });
        Button("同步此窗口登录",async ()=>
        {
            try
            {
                if(backend.CurrentAccount!=account)throw new Exception("账号已切换，请关闭后重新打开。");
                var cookies=new CookieContainer();foreach(var c in await browser.CoreWebView2.CookieManager.GetCookiesAsync("https://oj.czos.cn"))if(c.Domain.TrimStart('.')=="oj.czos.cn")cookies.Add(new Cookie(c.Name,c.Value,c.Path,c.Domain){Secure=c.IsSecure,HttpOnly=c.IsHttpOnly});
                await backend.ConnectBrowserSession(cookies,account);status.Text="已同步官网登录和通过进度。";
            }
            catch(Exception ex){status.Text=ex.Message;}
        });
        DockPanel.SetDock(status,Dock.Bottom);root.Children.Add(status);root.Children.Add(browser);Content=root;Loaded+=Ready;Closed+=(_,_)=>browser.Dispose();
    }
    async void Ready(object sender,RoutedEventArgs e)
    {
        try
        {
            var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(backend.Root,"official-webview"));var options=env.CreateCoreWebView2ControllerOptions();options.IsInPrivateModeEnabled=true;
            await browser.EnsureCoreWebView2Async(env,options);var web=browser.CoreWebView2;
            web.Settings.IsWebMessageEnabled=false;web.Settings.AreHostObjectsAllowed=false;web.Profile.IsPasswordAutosaveEnabled=false;web.Profile.IsGeneralAutofillEnabled=false;
            web.CookieManager.DeleteAllCookies();
            foreach(Cookie cookie in backend.OfficialCookies())
            {
                var c=web.CookieManager.CreateCookie(cookie.Name,cookie.Value,cookie.Domain,cookie.Path);c.IsSecure=cookie.Secure;c.IsHttpOnly=cookie.HttpOnly;web.CookieManager.AddOrUpdateCookie(c);
            }
            web.NavigationStarting+=(_,args)=>{try{Backend.Url(args.Uri);}catch{args.Cancel=true;status.Text="仅允许访问东方博宜官网。";}};
            web.NewWindowRequested+=(_,args)=>{args.Handled=true;try{web.Navigate(Backend.Url(args.Uri).ToString());}catch{status.Text="链接不属于东方博宜。";}};
            web.PermissionRequested+=(_,args)=>args.State=CoreWebView2PermissionState.Deny;
            web.NavigationCompleted+=(_,args)=>status.Text=args.IsSuccess?"官网完整功能 · 当前练习室账号："+account+" · 操作按官网权限执行；打开题目后可带回练习室。":"官网加载失败，可点击刷新重试。";
            web.Navigate(startPath);
        }
        catch(Exception ex){status.Text=ex.Message;}
    }
}
