using System.IO;
using System.Drawing;
using System.Windows.Forms;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;
using SDemo.Shell.Install;
using SDemo.Shell.Registry;
using SDemo.Shell.Wpf.Widgets;

namespace SDemo.Shell.Wpf.Tray;

/// <summary>
/// 常驻托盘（DA-67 / DA-68 / DA-72）：打开面板、显示/隐藏组件、置顶、穿透、重启、诊断、Win+D 实测、退出。
/// 也实现 IUserNotifier（应用的 notify 请求走托盘气泡；宿主不做通知队列）。
/// 面板按需创建（降低常驻工作集），这里只持有一个打开动作。
/// </summary>
public sealed class TrayService : IUserNotifier, IDisposable
{
    private readonly Action _openPanel;
    private readonly WidgetController _controller;
    private readonly AppRegistry _registry;
    private readonly AppInstaller _installer;
    private readonly ShellPaths _paths;
    private readonly IShellLog _log;
    private readonly Action _exitRequested;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private bool _disposed;

    public TrayService(
        Action openPanel,
        WidgetController controller,
        AppRegistry registry,
        AppInstaller installer,
        ShellPaths paths,
        Action exitRequested,
        IShellLog? log = null)
    {
        _openPanel = openPanel;
        _controller = controller;
        _registry = registry;
        _installer = installer;
        _paths = paths;
        _exitRequested = exitRequested;
        _log = log ?? NullShellLog.Instance;

        _menu.Opening += (_, _) => BuildMenu();
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "SDemo DesktopAssembly",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => _openPanel();
    }

    public void Notify(NotifyRequest request)
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(() => ShowBalloon(request));
            }
            else
            {
                ShowBalloon(request);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("显示通知失败。", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    private void ShowBalloon(NotifyRequest request)
    {
        _icon.BalloonTipTitle = request.Title;
        _icon.BalloonTipText = request.Body;
        _icon.BalloonTipIcon = request.Level switch
        {
            "warn" => ToolTipIcon.Warning,
            "error" => ToolTipIcon.Error,
            _ => ToolTipIcon.Info,
        };
        _icon.ShowBalloonTip(5_000);
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();

        var open = new ToolStripMenuItem("打开面板", null, (_, _) => _openPanel());
        open.Font = new Font(open.Font, System.Drawing.FontStyle.Bold);
        _menu.Items.Add(open);

        if (_registry.InvalidCount > 0)
        {
            _menu.Items.Add(new ToolStripMenuItem(
                $"{_registry.InvalidCount} 个组件加载失败（查看原因）",
                null,
                (_, _) => _openPanel()) { ForeColor = Color.IndianRed });
        }

        _menu.Items.Add(new ToolStripMenuItem("显示全部组件", null, (_, _) => _controller.ShowAll()));
        _menu.Items.Add(new ToolStripMenuItem("隐藏全部组件", null, (_, _) => _controller.HideAll()));

        if (_registry.Entries.Count > 0)
        {
            _menu.Items.Add(new ToolStripSeparator());
        }

        foreach (var entry in _registry.Entries)
        {
            var root = new ToolStripMenuItem(entry.Manifest.Name);
            root.DropDownItems.Add(new ToolStripMenuItem(
                entry.Visible ? "隐藏" : "显示",
                null,
                (_, _) =>
                {
                    if (entry.Visible)
                    {
                        _controller.HideWidget(entry.WidgetId);
                    }
                    else
                    {
                        _controller.ShowWidget(entry.WidgetId);
                    }
                }));
            root.DropDownItems.Add(new ToolStripMenuItem(
                entry.AlwaysOnTop ? "取消置顶" : "置顶",
                null,
                (_, _) => _controller.ToggleAlwaysOnTop(entry.WidgetId)));
            root.DropDownItems.Add(new ToolStripMenuItem(
                entry.ClickThrough ? "取消点击穿透" : "点击穿透",
                null,
                (_, _) => _controller.ToggleClickThrough(entry.WidgetId)));
            root.DropDownItems.Add(new ToolStripMenuItem(
                "重启组件",
                null,
                (_, _) => _controller.RestartWidget(entry.WidgetId)));

            if (entry.LastError is { } error)
            {
                root.DropDownItems.Add(new ToolStripMenuItem($"{error.Code}: {error.Message}") { Enabled = false });
            }

            _menu.Items.Add(root);
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("安装应用（zip）", null, (_, _) => InstallFromZip()));
        _menu.Items.Add(new ToolStripMenuItem("打开安装目录", null, (_, _) => OpenFolder(_paths.AppsDirectory)));
        _menu.Items.Add(new ToolStripMenuItem("Win+D 实测（A/B）", null, (_, _) => RecordWinDSpike()));
        _menu.Items.Add(new ToolStripMenuItem("组件诊断", null, (_, _) => _openPanel()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("退出外壳", null, (_, _) => _exitRequested()));
    }

    private void InstallFromZip()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择应用安装包",
            Filter = "应用安装包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        var result = _installer.InstallFromZip(dialog.FileName);
        if (!result.Success)
        {
            MessageBox.Show(result.Message ?? "安装失败。", "安装应用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (result.AppId is not null && _registry.Find(result.AppId) is { } installed)
        {
            _controller.ShowWidget(installed.WidgetId);
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start("explorer.exe", path);
        }
        catch (Exception ex)
        {
            _log.Warn("打开安装目录失败。", ex);
        }
    }

    private void RecordWinDSpike()
    {
        var answer = MessageBox.Show(
            "请先按 Win+D，然后回答：\n\n组件是否仍然可见？\n\n「是」= 结论 A（组件活过 Win+D）\n「否」= 结论 B（被收起，前台状态机已自动恢复）",
            "Win+D 实测",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);

        if (answer == DialogResult.Cancel)
        {
            return;
        }

        var conclusion = answer == DialogResult.Yes
            ? "结论 A：Win+D 后组件仍可见，无需恢复策略"
            : "结论 B：Win+D 后组件被收起，前台轮询状态机恢复生效";
        _controller.RecordWinDSpike(conclusion);
        MessageBox.Show(
            $"已写入：{_controller.WinDSpikeLogPath}",
            "Win+D 实测",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
