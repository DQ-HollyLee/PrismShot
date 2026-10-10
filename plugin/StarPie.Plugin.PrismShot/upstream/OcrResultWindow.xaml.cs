// 照搬自 StarPie 的 WinPieGestures/OcrResultWindow.xaml.cs —— 行为一字未改：
// 可编辑的文本框、自动全选、ESC 关闭、拖拽移动、复制 / 网页搜索 / 接口设置三个按钮。
//
// 被迫改的只有四处，每一处都是为了"离开宿主还能活"，而不是为了换个样子：
//
// | 上游写的                                | 这里换成                          | 为什么必须是它                                        |
// | --------------------------------------- | --------------------------------- | ----------------------------------------------------- |
// | AppThemeManager.ApplyTheme(this, theme) | OcrTheme.Apply(this)              | 拿不到宿主的当前主题名；而且宿主已经把主题写进了 Application 级资源，DynamicResource 本来就会解析过去 —— 不覆盖反而更贴宿主 |
// | I18n.T("...")                           | 字面中文                          | 插件拿不到宿主的词条表（与 OcrManager 那边的处理一致） |
// | MemoryOptimizer.TrimMemory()            | 删掉                              | 那是宿主的内存治理，插件动不得                        |
// | OcrManager.ShowSettingsDialog()         | 打开 config.json 所在的文件夹     | 照搬的那份 OcrManager 里这个方法已被删掉（它依赖宿主的设置窗口与配置写入）；改成把配置文件指给用户 |
//
// 另外多了一个上游没有的参数 autoCopied：上游默认"已自动存入系统剪贴板"永远成立，
// 而插件这一侧写不写剪贴板是可配的（诊断文本也不写）—— 那行绿字必须说实话。

using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Media = System.Windows.Media;
using System.Windows.Threading;

namespace StarPie.Plugin.PrismShot.Upstream;

public partial class OcrResultWindow : Window
{
    public OcrResultWindow(string text, string engineName, string latency, bool autoCopied)
    {
        InitializeComponent();
        OcrTheme.Apply(this);

        ResultTextBox.Text = text ?? string.Empty;

        // 徽章放不下时会省略，所以完整名字另外挂在提示上 ——
        // 「识别用了哪个引擎」是排查问题时第一个要问的东西，不能被省略号吃掉。
        string badge = $"{engineName} · {latency}";
        EngineText.Text = badge;
        EngineBadge.ToolTip = badge;

        // 这行绿字是给用户的承诺，不是装饰。没写剪贴板就直说，
        // 否则用户会拿着"已存入剪贴板"去粘贴一个其实没写进去的剪贴板。
        if (autoCopied)
        {
            ClipboardStatusText.Text = "已自动存入系统剪贴板";
            ClipboardStatusText.Foreground = new Media.SolidColorBrush(
                (Media.Color)Media.ColorConverter.ConvertFromString("#10B981")
            );
        }
        else
        {
            ClipboardStatusText.Text = "未写入剪贴板";
            ClipboardStatusText.Foreground = new Media.SolidColorBrush(
                (Media.Color)Media.ColorConverter.ConvertFromString("#94A3B8")
            );
        }

        RefreshCharCount();

        ResultTextBox.TextChanged += (s, e) => RefreshCharCount();

        ResultTextBox.SelectAll();
        ResultTextBox.Focus();
    }

    /// <summary>
    /// 弹一个结果窗口。非模态 —— 与上游一致（<c>resWin.Show()</c>），
    /// 认完字就该把界面交还给用户，而不是拿一个对话框把整个宿主卡住。
    /// </summary>
    /// <remarks>
    /// 窗口必须长在 UI 线程上，这是 WPF 的硬要求。宿主在跑的时候当然有
    /// <c>Application.Current.Dispatcher</c>，直接 Invoke 过去即可 ——
    /// 那样窗口还顺带继承了宿主的 Application 级资源（主题）与关闭行为。
    /// 没有宿主 Application 时（独立验证桩、被别的壳调起来）才自己开一条 STA 线程，
    /// 起一个消息循环把窗口养住，关掉就退干净。
    /// </remarks>
    public static void Show(string text, string engineName, string latency, bool autoCopied)
    {
        Dispatcher? host = System.Windows.Application.Current?.Dispatcher;
        if (host is not null && !host.HasShutdownStarted)
        {
            host.Invoke(() => new OcrResultWindow(text, engineName, latency, autoCopied).Show());
            return;
        }

        Thread thread = new(() =>
        {
            OcrResultWindow window = new(text, engineName, latency, autoCopied);

            // 非模态窗口自己不会结束消息循环：关掉它之后这条线程会一直挂着，
            // 于是宿主退出后进程结束不了。关一个窗口就退一次循环，线程自然收尾。
            window.Closed += (s, e) => window.Dispatcher.InvokeShutdown();

            window.Show();
            Dispatcher.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private void RefreshCharCount()
    {
        CharCountText.Text = $"提取文本 (共 {ResultTextBox.Text.Length} 字符):";
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    // KeyEventArgs 要写全名：项目同时开着 WinForms，隐式 using 里也有一个
    // System.Windows.Forms.KeyEventArgs（照搬 OcrManager 时用的 Bitmap 就是这么来的）。
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(ResultTextBox.Text);
            ClipboardStatusText.Text = "已复制到剪贴板";
            ClipboardStatusText.Foreground = Media.Brushes.LightGreen;
        }
        catch
        {
            // 剪贴板被别的进程占着是常有的事。复制失败不该把结果窗口一起搞崩 ——
            // 文本仍然在框里，用户可以自己选中再复制。
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        string query = ResultTextBox.Text.Trim();
        if (query.Length > 80)
        {
            query = query[..80];
        }

        if (!string.IsNullOrEmpty(query))
        {
            try
            {
                string url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(query);
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch
            {
            }
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // 宿主的设置窗口（OcrSettingsDialog）属于宿主内部，插件弹不出来 ——
        // 照搬的那份 OcrManager 里 ShowSettingsDialog 正是因为这一点被删掉的。
        // 但"改 OCR 配置"这个诉求是真的，于是退一步：把那份配置文件指给用户。
        // 引擎、模型、密钥、后处理开关全在里面，改完保存，插件下一次识别就跟着变
        // （OcrConfigLoader 按文件时间戳缓存，不需要重启）。
        try
        {
            // SourcePath 是"最近一次实际读到的路径"，没读过就是空串 ——
            // 所以必须先碰一下 Current 让它去读一次。
            _ = OcrConfigLoader.Current;

            string path = OcrConfigLoader.SourcePath;
            if (path.Length == 0)
            {
                return;
            }

            Process.Start(
                new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                }
            );
        }
        catch
        {
        }
    }
}
