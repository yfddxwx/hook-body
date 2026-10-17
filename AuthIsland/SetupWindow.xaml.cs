using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AuthIsland.Cli;

namespace AuthIsland;

public partial class SetupWindow : Window
{
    private bool _installed;
    private bool _busy;

    public SetupWindow()
    {
        InitializeComponent();

        // 已经装过（但没在主目录跑，比如又双击了发布目录的 exe）：直接提示可用
        if (AppPaths.IsInstalled)
        {
            ActionText.Text = "打开胶囊";
            StatusText.Text = "已经安装好了。点下面启动。";
            _installed = true;
        }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    private void OnClose(object sender, MouseButtonEventArgs e) => Close();

    private async void OnAction(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;

        if (_installed)
        {
            Installer.StartCapsule();
            Close();
            return;
        }

        _busy = true;
        ActionButton.IsEnabled = false;
        ActionText.Text = "正在安装…";
        StatusText.Text = "复制程序 · 写入 WorkBuddy 配置 · 注册开机自启";

        var (ok, message) = await Task.Run(() => Installer.Install());

        _busy = false;
        ActionButton.IsEnabled = true;

        if (!ok)
        {
            ActionText.Text = "重试";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71));
            StatusText.Text = message;
            return;
        }

        _installed = true;
        ActionText.Text = "开始使用";
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80));
        StatusText.Text = "装好了。胶囊已经在屏幕顶部待命——回到 WorkBuddy 正常干活就行。";
    }
}
