using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
namespace GlassOJ;
public partial class MainWindow:Window
{
    static Backend CreateBackend()
    {
        var args=Environment.GetCommandLineArgs();
        if(!args.Contains("--test-mode"))return new Backend();
        var option=args.FirstOrDefault(x=>x.StartsWith("--test-data-root=",StringComparison.Ordinal));
        if(option==null)return new Backend(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BoyiStudio-UI-Test"));
        var root=option["--test-data-root=".Length..];
        if(!Path.IsPathFullyQualified(root))throw new ArgumentException("测试数据目录必须是绝对路径。");
        return new Backend(root,isolatedTest:true);
    }
    readonly Backend backend=CreateBackend();
    readonly WallpaperBridge wallpaper=new();
    bool allowClose,closing;
    public MainWindow(){InitializeComponent();if(Environment.GetCommandLineArgs().Contains("--test-mode")){Title="博宜练习室 · 界面检查";TitleText.Text=Title;}Loaded+=Ready;Closing+=OnClosing;}
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h,int a,ref int v,int s);
    async void Ready(object? sender,RoutedEventArgs args)
    {
        try
        {
            var handle=new WindowInteropHelper(this).Handle;
            int corner=2,backdrop=3;
            DwmSetWindowAttribute(handle,33,ref corner,4);
            DwmSetWindowAttribute(handle,38,ref backdrop,4);
            // App-local GPU preference. Chromium falls back when no discrete GPU is available.
            var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(backend.Root,"webview"),new CoreWebView2EnvironmentOptions{AdditionalBrowserArguments="--force-high-performance-gpu"});
            await Web.EnsureCoreWebView2Async(env);
            wallpaper.Attach(Web.CoreWebView2);
            StateChanged+=async (_,_)=>
            {
                try { await Web.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new CustomEvent('boyi-window-visibility',{detail:"+(WindowState!=WindowState.Minimized?"true":"false")+"}))"); }
                catch (InvalidOperationException) { }
            };
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;
            Web.CoreWebView2.Profile.IsPasswordAutosaveEnabled=false;
            Web.CoreWebView2.Profile.IsGeneralAutofillEnabled=false;
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping("app.boyi.local",Path.Combine(AppContext.BaseDirectory,"ui"),CoreWebView2HostResourceAccessKind.DenyCors);
            Directory.CreateDirectory(Path.Combine(backend.Root,"assets"));
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping("assets.boyi.local",Path.Combine(backend.Root,"assets"),CoreWebView2HostResourceAccessKind.DenyCors);
            Web.CoreWebView2.NavigationStarting+=(s,e)=>{if(!e.Uri.StartsWith("https://app.boyi.local/",StringComparison.Ordinal))e.Cancel=true;};
            Web.CoreWebView2.NewWindowRequested+=(s,e)=>e.Handled=true;
            backend.Notify=(topic,data)=>Dispatcher.InvokeAsync(()=>Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{topic,data},Backend.Json))).Task;
            Web.CoreWebView2.WebMessageReceived+=async(s,e)=>
            {
                string? id=null;
                try
                {
                    var msg=JsonDocument.Parse(e.WebMessageAsJson).RootElement;
                    if(e.Source!="https://app.boyi.local/index.html")throw new Exception("消息来源无效。");
                    if(Environment.GetCommandLineArgs().Contains("--test-mode") && Environment.GetCommandLineArgs().Any(x=>x.StartsWith("--test-data-root=",StringComparison.Ordinal)) && Environment.GetCommandLineArgs().Contains("--test-scenario") && msg.TryGetProperty("action",out var testAction) && testAction.GetString()=="__testResult")
                    {
                        await File.WriteAllTextAsync(Path.Combine(backend.Root,"ui-scenario-result.json"),msg.GetProperty("result").GetRawText());
                        allowClose=true;Close();return;
                    }
                    id=msg.GetProperty("id").GetString();
                    if(msg.GetProperty("action").GetString()=="windowCloseReady")
                    {
                        backend.Stop();
                        while(backend.Running)await Task.Delay(200);
                        allowClose=true;Close();return;
                    }
                    object? result;
                    if(msg.GetProperty("action").GetString()=="wallpaperState")
                        result=await Task.Run(wallpaper.GetState);
                    else if(msg.GetProperty("action").GetString()=="officialWorkspace")
                    {
                        var path=msg.GetProperty("data").GetProperty("path").GetString()??"/group/index";
                        result=await Dispatcher.InvokeAsync(()=>new OfficialWindow(backend,path,async problemId=>
                        {
                            await backend.Handle("ensureProblem",JsonSerializer.SerializeToElement(new{id=problemId}));
                            await Web.CoreWebView2.ExecuteScriptAsync("window.boyiUI.open("+JsonSerializer.Serialize(problemId)+")");
                        }){Owner=this}.ShowDialog());
                    }
                    else if(msg.GetProperty("action").GetString()=="officialLogin")
                    {
                        if(backend.Running)throw new Exception("请先暂停提交队列。");
                        var username=msg.GetProperty("data").GetProperty("username").GetString()??"";
                        // Leave the WebView2 callback before starting a modal message loop.
                        result=await Dispatcher.InvokeAsync(()=>new LoginWindow(backend,username){Owner=this}.ShowDialog()==true);
                    }
                    else result=await backend.Handle(msg.GetProperty("action").GetString()!,msg.GetProperty("data"));
                    if(msg.GetProperty("action").GetString()=="settings")UpdateTheme();
                    Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{id,ok=true,result},Backend.Json));
                }
                catch(Exception ex){Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{id,ok=false,error=ex.Message},Backend.Json));}
            };
            await backend.Initialize();
            UpdateTheme();
            if(Environment.GetCommandLineArgs().Contains("--test-mode") && Environment.GetCommandLineArgs().Any(x=>x.StartsWith("--test-data-root=",StringComparison.Ordinal)) && Environment.GetCommandLineArgs().Contains("--test-scenario"))
                await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"ui","test-scenario.js")));
            Web.CoreWebView2.Navigate("https://app.boyi.local/index.html");
            WindowState=WindowState.Normal;
            Show();
            Activate();
        }
        catch(Exception ex){MessageBox.Show(ex.Message,"启动失败");}
    }
    void MinimizeClick(object sender,RoutedEventArgs e)=>WindowState=WindowState.Minimized;
    void UpdateTheme()
    {
        Background=(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(backend.DarkTheme?"#172A43":"#EDF2FA")!;
        TitleBar.Background=(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(backend.DarkTheme?"#203148":"#66FFFFFF")!;
    }
    void MaximizeClick(object sender,RoutedEventArgs e)=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
    void CloseClick(object sender,RoutedEventArgs e)=>Close();
    async void OnClosing(object? sender,System.ComponentModel.CancelEventArgs e)
    {
        if(allowClose || Web.CoreWebView2==null)return;
        e.Cancel=true;
        if(closing)return;
        if(backend.Running && MessageBox.Show("提交任务正在运行。暂停并关闭软件？进度会保留。","关闭练习室",MessageBoxButton.YesNo)!=MessageBoxResult.Yes){e.Cancel=true;return;}
        closing=true;
        try{await Web.CoreWebView2.ExecuteScriptAsync("window.requestSafeClose()");}
        catch{allowClose=true;Close();}
    }
}
