using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GlassOJ;

// A narrowly scoped official verification fallback. No app bridge is exposed to web content.
public sealed class LoginWindow:Window
{
    readonly Backend backend;
    readonly WebView2 browser=new();
    readonly TextBox account=new(){Width=190,Margin=new Thickness(12,0,12,0),VerticalContentAlignment=VerticalAlignment.Center};
    readonly TextBlock status=new(){Text="请在官网完成登录，然后点击“连接此账号”。",VerticalAlignment=VerticalAlignment.Center,Foreground=Brushes.SlateGray};
    readonly Button connect=new(){Content="连接此账号",Padding=new Thickness(18,9,18,9)};
    public LoginWindow(Backend backend,string username)
    {
        this.backend=backend;Title="东方博宜 · 官方登录验证";Width=1050;Height=760;MinWidth=850;MinHeight=600;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;account.Text=username;
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new());grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var header=new TextBlock{Text="官方验证 · https://oj.czos.cn\n仅在需要验证码或短信验证时使用；完成后返回练习室。",Margin=new Thickness(18),FontSize=14};grid.Children.Add(header);
        Grid.SetRow(browser,1);grid.Children.Add(browser);
        var bottom=new DockPanel{Margin=new Thickness(18)};Grid.SetRow(bottom,2);grid.Children.Add(bottom);
        DockPanel.SetDock(connect,Dock.Right);bottom.Children.Add(connect);DockPanel.SetDock(account,Dock.Right);bottom.Children.Add(account);bottom.Children.Add(status);
        Content=grid;Loaded+=Ready;Closed+=(s,e)=>browser.Dispose();connect.Click+=Connect;
    }
    async void Ready(object sender,RoutedEventArgs e)
    {
        try
        {
            var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(backend.Root,"verification-webview"));
            var options=env.CreateCoreWebView2ControllerOptions();options.IsInPrivateModeEnabled=true;
            await browser.EnsureCoreWebView2Async(env,options);
            browser.CoreWebView2.Settings.IsWebMessageEnabled=false;
            browser.CoreWebView2.Settings.AreHostObjectsAllowed=false;
            browser.CoreWebView2.Profile.IsPasswordAutosaveEnabled=false;
            browser.CoreWebView2.Profile.IsGeneralAutofillEnabled=false;
            browser.CoreWebView2.NavigationStarting+=(s,args)=>
            {
                try{Backend.Url(args.Uri);}catch{args.Cancel=true;status.Text="仅允许访问东方博宜官方页面。";}
            };
            browser.CoreWebView2.NewWindowRequested+=(s,args)=>{args.Handled=true;try{browser.CoreWebView2.Navigate(Backend.Url(args.Uri).ToString());}catch{status.Text="该链接不属于东方博宜。";}};
            browser.CoreWebView2.PermissionRequested+=(s,args)=>args.State=CoreWebView2PermissionState.Deny;
            browser.CoreWebView2.DownloadStarting+=(s,args)=>args.Cancel=true;
            browser.CoreWebView2.Navigate("https://oj.czos.cn/site/login");
        }
        catch(Exception ex){status.Text="加载失败："+ex.Message;}
    }
    async void Connect(object sender,RoutedEventArgs e)
    {
        if(browser.CoreWebView2==null)return;
        connect.IsEnabled=false;status.Text="正在核对账号…";
        try
        {
            var cookies=new CookieContainer();
            foreach(var c in await browser.CoreWebView2.CookieManager.GetCookiesAsync("https://oj.czos.cn"))
            {
                if(c.Domain.TrimStart('.')!="oj.czos.cn")continue;
                cookies.Add(new Cookie(c.Name,c.Value,c.Path,c.Domain){Secure=c.IsSecure,HttpOnly=c.IsHttpOnly});
            }
            await backend.ConnectBrowserSession(cookies,account.Text.Trim());DialogResult=true;
        }
        catch(Exception ex){status.Text=ex.Message;connect.IsEnabled=true;}
    }
}
