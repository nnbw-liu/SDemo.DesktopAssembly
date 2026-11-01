using System.IO;
using System.Drawing;
using System.Windows.Forms;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;
using SDemo.Shell.Install;
using SDemo.Shell.Registry;
using SDemo.Shell.Settings;
using SDemo.Shell.Storage;
using SDemo.Shell.Wpf.Widgets;

namespace SDemo.Shell.Wpf.Tray;

/// <summary>
/// 常驻托盘（DA-67 / DA-68 / DA-72 / DA-45 / DA-66）：打开面板、组件入口（可按组件关闭）、
/// 显示/隐藏、置顶、穿透、重启、诊断、截屏隐藏、退出。
/// 也实现 IUserNotifier：通知前检查 notifications 权限，首次使用时询问（DA-66）。
/// </summary>
public sealed class TrayService : IUserNotifier, IDisposable
{
    private readonly Action<bool> _openPanel;
    private readonly WidgetController _controller;
    private readonly AppRegistry _registry;
    private readonly AppInstaller _installer;
    private readonly ShellPaths _paths;
    private readonly ShellStateStore _store;
    private readonly IShellLog _log;
    private readonly Action _exitRequested;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private bool _disposed;

    public TrayService(
        Action<bool> openPanel,
        WidgetController controller,
        AppRegistry registry,
        AppInstaller installer,
        ShellPaths paths,
        ShellStateStore store,
        Action exitRequested,
        IShellLog? log = null)
    {
        _openPanel = openPanel;
        _controller = controller;
        _registry = registry;
        _installer = installer;
        _paths = paths;
        _store = store;
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
        _icon.DoubleClick += (_, _) => _openPanel(false);
    }

    public void Notify(NotifyRequest request, string? appId = null)
    {
        if (!EnsureNotificationPermission(appId))
        {
            return;
        }

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

    /// <summary>notifications 权限：未声明 → 拒绝；首次使用 → 弹一次询问并落盘（DA-66）。</summary>
    private bool EnsureNotificationPermission(string? appId)
    {
        if (string.IsNullOrEmpty(appId))
        {
            return true;
        }

        var entry = _registry.Find(appId);
        if (entry is null)
        {
            return false;
        }

        if (entry.Widget.Permissions?.Contains(PermissionPolicy.Notifications, StringComparer.Ordinal) != true)
        {
            _log.Warn($"{appId} 未声明 notifications 权限，通知已丢弃。");
            return false;
        }

        var decision = PermissionPolicy.DecisionFor(entry.WidgetState, PermissionPolicy.Notifications);
        if (decision == PermissionPolicy.Denied)
        {
            _log.Info($"{appId} 的 notifications 权限已被拒绝，通知已丢弃。");
            return false;
        }

        if (decision == PermissionPolicy.Granted)
        {
            return true;
        }

        return AskNotifications(entry);
    }

    private bool AskNotifications(AppEntry entry)
    {
        var ask = new Func<bool>(() =>
        {
            var answer = MessageBox.Show(
                $"{entry.Manifest.Name} 想向你发送通知。\n\n允许后壳只弹气泡，不保存通知内容（宿主不做通知队列）。",
                "通知权限",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            var granted = answer == DialogResult.Yes;
            _registry.UpdateWidgetState(entry.WidgetId, state =>
                PermissionPolicy.SetDecision(state, PermissionPolicy.Notifications, granted));
            _log.Info($"{entry.AppId} 的 notifications 权限：{(granted ? "granted" : "denied")}");
            return granted;
        });

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            return dispatcher.Invoke(ask);
        }

        return ask();
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

        var open = new ToolStripMenuItem("打开面板", null, (_, _) => _openPanel(false));
        open.Font = new Font(open.Font, System.Drawing.FontStyle.Bold);
        _menu.Items.Add(open);

        if (_registry.InvalidCount > 0)
        {
            _menu.Items.Add(new ToolStripMenuItem(
                $"{_registry.InvalidCount} 个组件加载失败（查看原因）",
                null,
                (_, _) => _openPanel(true)) { ForeColor = Color.IndianRed });
        }

        _menu.Items.Add(new ToolStripMenuItem("显示全部组件", null, (_, _) => _controller.ShowAll()));
        _menu.Items.Add(new ToolStripMenuItem("隐藏全部组件", null, (_, _) => _controller.HideAll()));

        // DA-72：组件入口可按 activation.showInTray / 用户覆盖隐藏。
        var trayEntries = _registry.Entries.Where(static e => e.EffectiveShowInTray).ToArray();
        if (trayEntries.Length > 0)
        {
            _menu.Items.Add(new ToolStripSeparator());
        }

        foreach (var entry in trayEntries)
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

            if (entry.Widget.Renderer == WidgetRendering.Native)
            {
                root.DropDownItems.Add(new ToolStripMenuItem(
                    entry.ClickThrough ? "取消点击穿透" : "点击穿透",
                    null,
                    (_, _) => _controller.ToggleClickThrough(entry.WidgetId)));
            }

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
        _menu.Items.Add(new ToolStripMenuItem(
            "截屏隐藏组件",
            null,
            (_, _) => ToggleHideFromCapture())
        {
            Checked = _store.Settings.Privacy.HideFromCapture,
        });
        _menu.Items.Add(new ToolStripMenuItem("安装应用（zip）", null, (_, _) => InstallFromZip()));
        _menu.Items.Add(new ToolStripMenuItem("打开安装目录", null, (_, _) => OpenFolder(_paths.AppsDirectory)));
        _menu.Items.Add(new ToolStripMenuItem("Win+D 实测（A/B）", null, (_, _) => RecordWinDSpike()));
        _menu.Items.Add(new ToolStripMenuItem("组件诊断", null, (_, _) => _openPanel(true)));
        _menu.Items.Add(new ToolStripMenuItem("打开 WebView2 日志目录", null, (_, _) => OpenFolder(_paths.WebView2Directory)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("退出外壳", null, (_, _) => _exitRequested()));
    }

    private void ToggleHideFromCapture()
    {
        var enabled = !_store.Settings.Privacy.HideFromCapture;
        _store.Settings.Privacy.HideFromCapture = enabled;
        _store.SaveSettings();
        _controller.SetHideFromCapture(enabled);
        if (_registry.Entries.Count > 0)
        {
            _icon.ShowBalloonTip(
                3_000,
                "截屏隐藏",
                enabled ? "组件窗口已在截屏 / 录屏中隐藏。" : "组件窗口已恢复可见。",
                ToolTipIcon.Info);
        }
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
            _log.Warn("打开目录失败。", ex);
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
