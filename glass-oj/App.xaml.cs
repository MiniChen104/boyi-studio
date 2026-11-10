using System.Windows;
namespace GlassOJ;
public partial class App:Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        instance=new Mutex(true,"Local\\GlassOJDesktop"+(e.Args.Contains("--test-mode")?"-Test":""),out var created);
        if(!created){MessageBox.Show("博宜练习室已经打开。请使用现有窗口。");Shutdown();return;}
        DispatcherUnhandledException+=(s,a)=>{MessageBox.Show(a.Exception.Message,"博宜练习室");a.Handled=true;};
        base.OnStartup(e);
    }
}
