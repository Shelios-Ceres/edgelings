using AgentIsland.Model;
using AgentIsland.WinUI.Drawer;
using AgentIsland.WinUI.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace AgentIsland.WinUI.Shell;

internal sealed class HoverCardPreview : Window
{
    private static readonly string[] Names = { "Claude", "Codex", "Kimi" };
    private static readonly uint[] Swatches = { 0xE49A79, 0x9EBDF5, 0x88CDB6 };
    private static readonly int?[] Sequence = { 0, 1, 2, 0, null, 2, 1, 0, 2, null, 1, 2 };

    private readonly Canvas _surface = new();
    private readonly HoverCard _card;
    private SpeechBubble? _bubble;
    private readonly Button _passThrough = new() { Content = "透明区点击", Width = 140, Height = 36 };
    private int _passThroughClicks;
    private readonly Button[] _characters = new Button[3];
    private readonly TextBlock _diagnostics = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly ToggleButton _reduced = new() { Content = "减少动画", MinWidth = 110 };
    private readonly ToggleButton _slow = new() { Content = "慢放4x", MinWidth = 110 };
    private readonly ToggleButton _reverse = new() { Content = "反向布局", MinWidth = 110 };
    private readonly DispatcherQueueTimer _diagnosticsTimer;
    private DispatcherQueueTimer? _sequenceTimer;
    private int _sequenceVersion;
    private int _target;
    private bool _hasTarget;
    private bool _closed;

    public HoverCardPreview()
    {
        Title = "Agent Island — Hover card check";
        var scale = Win32.GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(940 * scale), work.Width);
        var height = Math.Min((int)(700 * scale), work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)(800 * scale), work.Width);
            presenter.PreferredMinimumHeight = Math.Min((int)(620 * scale), work.Height);
        }
        var dark = Brand.IsDark();
        _surface.Background = Brand.Brush(dark ? 0x18212Bu : 0xEAF0F5u);
        _surface.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        Content = _surface;
        _card = new HoverCard(_surface);

        var heading = new TextBlock
        {
            Text = "Hover card check",
            FontSize = 25,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        Place(heading, 24, 18);
        var description = new TextBlock
        {
            Text = "合成数据 · 实际悬浮卡 · 不连接账号或扫描会话",
            FontSize = 13,
            Opacity = 0.75,
        };
        Place(description, 25, 54);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        for (var i = 0; i < Names.Length; i++)
        {
            var target = i;
            actions.Children.Add(ActionButton(Names[i], () => Select(target)));
        }
        actions.Children.Add(ActionButton("隐藏", Hide));
        actions.Children.Add(ActionButton("快速切换", StartSequence));
        actions.Children.Add(ActionButton("同值刷新100次", RefreshSameValue));
        actions.Children.Add(ActionButton("回收检查", CollectGarbage));
        actions.Children.Add(ActionButton("气泡检查", () =>
        {
            _bubble ??= new SpeechBubble(_surface);
            _bubble.Say("Synthetic bubble", Swatches[0], true, Anchor(0), Area, false);
        }));
        Place(actions, 24, 85);

        AutomationProperties.SetName(_passThrough, "透明区点击");
        _passThrough.Click += (_, _) =>
        {
            _passThrough.Content = "已穿透 " + ++_passThroughClicks + " 次";
            DebugTrace.Write("preview transparent area click=" + _passThroughClicks);
        };
        _surface.Children.Add(_passThrough);

        AutomationProperties.SetName(_reduced, "减少动画");
        AutomationProperties.SetName(_slow, "慢放4x");
        AutomationProperties.SetName(_reverse, "反向布局");
        _reduced.Click += (_, _) => ChangeMotion();
        _slow.Click += (_, _) => ChangeMotion();
        _reverse.Click += (_, _) =>
        {
            CancelSequence();
            _card.Hide();
            Layout();
            if (_hasTarget) ShowTarget(_target);
            _status.Text = "反向布局：" + (_reverse.IsChecked == true ? "开" : "关");
        };
        var motion = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        motion.Children.Add(_reduced);
        motion.Children.Add(_slow);
        motion.Children.Add(_reverse);
        motion.Children.Add(new TextBlock
        {
            Text = "手动选择会取消自动序列；彩色角色也可点击。",
            FontSize = 12,
            MaxWidth = 280,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        });
        Place(motion, 24, 132);

        for (var i = 0; i < _characters.Length; i++)
        {
            var target = i;
            var character = new Button
            {
                Content = Names[i][..1],
                Width = 48,
                Height = 48,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(24),
                Background = Brand.Brush(Swatches[i]),
                Foreground = Brand.Brush(0x17212A),
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            AutomationProperties.SetName(character, Names[i] + " 角色");
            character.Click += (_, _) => Select(target);
            _characters[i] = character;
            _surface.Children.Add(character);
        }

        AutomationProperties.SetName(_status, "当前预览状态");
        AutomationProperties.SetName(_diagnostics, "悬浮卡诊断计数");
        _surface.Children.Add(_status);
        _surface.Children.Add(_diagnostics);
        _surface.SizeChanged += (_, _) => Layout();
        _surface.Loaded += (_, _) =>
        {
            if (_closed) return;
            Layout();
            Select(0);
        };

        _diagnosticsTimer = DispatcherQueue.CreateTimer();
        _diagnosticsTimer.Interval = TimeSpan.FromMilliseconds(100);
        _diagnosticsTimer.Tick += (_, _) => UpdateDiagnostics();
        _diagnosticsTimer.Start();
        Closed += (_, _) =>
        {
            _closed = true;
            CancelSequence();
            _diagnosticsTimer.Stop();
            _bubble?.Hide(animated: false);
            _card.Dispose();
            Application.Current.Exit();
        };
    }

    private Button ActionButton(string name, Action action)
    {
        var button = new Button { Content = name, MinWidth = 76 };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    private void Place(UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        _surface.Children.Add(element);
    }

    private Rect Area => new(24, 183, Math.Max(320, _surface.ActualWidth - 48), Math.Max(240, _surface.ActualHeight - 260));

    private Rect Anchor(int target)
    {
        var area = Area;
        var position = _reverse.IsChecked == true ? 2 - target : target;
        var top = area.Top + 18 + position * Math.Max(65, (area.Height - 84) / 2);
        return new Rect(area.Right - 92, top, 48, 48);
    }

    private void Layout()
    {
        if (_closed) return;
        for (var i = 0; i < _characters.Length; i++)
        {
            var anchor = Anchor(i);
            Canvas.SetLeft(_characters[i], anchor.X);
            Canvas.SetTop(_characters[i], anchor.Y);
        }
        Canvas.SetLeft(_status, 24);
        Canvas.SetTop(_status, Math.Max(450, _surface.ActualHeight - 63));
        _status.Width = Math.Max(300, _surface.ActualWidth - 48);
        Canvas.SetLeft(_diagnostics, 24);
        Canvas.SetTop(_diagnostics, Math.Max(475, _surface.ActualHeight - 39));
        _diagnostics.Width = Math.Max(300, _surface.ActualWidth - 48);
        Canvas.SetLeft(_passThrough, Math.Max(350, _surface.ActualWidth - 350));
        Canvas.SetTop(_passThrough, Math.Max(450, _surface.ActualHeight - 150));
        if (_hasTarget && _card.IsOpen) ShowTarget(_target);
    }

    private void Select(int target)
    {
        CancelSequence();
        ShowTarget(target);
        _status.Text = "手动目标：" + Names[target];
        UpdateDiagnostics();
    }

    private void ShowTarget(int target)
    {
        if (_closed) return;
        var retarget = _card.IsOpen && _hasTarget && _target != target;
        _target = target;
        _hasTarget = true;
        _card.Show(new[] { (Card(target), Swatches[target]) }, characterRight: true,
            Anchor(target), Area, reducedMotion: _reduced.IsChecked == true, retarget: retarget);
    }

    private void Hide()
    {
        CancelSequence();
        _card.Hide(animated: true);
        _status.Text = "手动隐藏";
        UpdateDiagnostics();
    }

    private void RefreshSameValue()
    {
        CancelSequence();
        if (!_hasTarget || !_card.IsOpen) ShowTarget(_target);
        for (var i = 0; i < 100; i++) ShowTarget(_target);
        _status.Text = Names[_target] + "：已提交100次同值刷新（每次重新创建等值数据）。";
        UpdateDiagnostics();
    }

    private async void CollectGarbage()
    {
        _status.Text = "回收检查中";
        DebugTrace.Write("preview GC begin");
        await Task.Run(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        });
        if (_closed) return;
        _status.Text = "回收检查完成";
        DebugTrace.Write("preview GC complete");
        UpdateDiagnostics();
    }

    private void ChangeMotion()
    {
        CancelSequence();
        _card.DebugDurationScale = _slow.IsChecked == true ? 4 : 1;
        if (_hasTarget && _card.IsOpen)
        {
            _card.Hide();
            ShowTarget(_target);
        }
        _status.Text = $"减少动画：{(_reduced.IsChecked == true ? "开" : "关")} · 速度：{(_slow.IsChecked == true ? "慢放4x" : "正常")}";
        UpdateDiagnostics();
    }

    private void StartSequence()
    {
        CancelSequence();
        var version = _sequenceVersion;
        var index = 0;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(_slow.IsChecked == true ? 360 : 90);
        _sequenceTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (_closed || version != _sequenceVersion)
            {
                timer.Stop();
                return;
            }
            var target = Sequence[index++];
            if (target is { } selected) ShowTarget(selected);
            else _card.Hide(animated: true);
            _status.Text = $"自动序列 {index}/{Sequence.Length} · {(target is { } value ? Names[value] : "隐藏")}";
            if (index == Sequence.Length)
            {
                timer.Stop();
                _sequenceTimer = null;
                _status.Text = "自动序列完成 · 最终目标：" + Names[_target];
            }
            UpdateDiagnostics();
        };
        _status.Text = "自动序列运行中；点击任意手动按钮可接管。";
        timer.Start();
    }

    private void CancelSequence()
    {
        _sequenceVersion++;
        _sequenceTimer?.Stop();
        _sequenceTimer = null;
    }

    private void UpdateDiagnostics()
    {
        if (_closed) return;
        var text = _card.DiagnosticsSummary;
        if (_diagnostics.Text != text)
        {
            _diagnostics.Text = text;
            AutomationProperties.SetName(_diagnostics, "悬浮卡诊断计数 " + text);
            DebugTrace.Write("preview " + text);
        }
    }

    private static StatusCard Card(int target) => target switch
    {
        0 => new(DisplayProvider.Claude, "Claude", "正在思考", null,
            new("Tasks", "2 running", "2"),
            new[] { new StatusMeter("5h", "42%", 0.42, 0.42, "Resets in 2h 15m") },
            null, Array.Empty<StatusPhrase>()),
        1 => new(DisplayProvider.Codex, "Codex", "正在运行工具", "Synthetic tool · build preview",
            new("Tasks", "3 running", "3"),
            new[]
            {
                new StatusMeter("5h", "67%", 0.67, 0.67, "Resets in 1h 40m"),
                new StatusMeter("week", "28%", 0.28, 0.28, "Resets in 4d 6h"),
            },
            new("Tokens", "Today 128K", "128K", "This month 2.4M", "2.4M"),
            new[] { new StatusPhrase("2 reset cards · synthetic preview", new[] { "2" }) }),
        _ => new(DisplayProvider.Kimi, "Kimi Code", "等待你的批准", "Synthetic permission prompt",
            new("Tasks", "1 running", "1"),
            new[]
            {
                new StatusMeter("5h", "19%", 0.19, 0.19, "Resets in 3h 10m"),
                new StatusMeter("week", "38%", 0.38, 0.38, "Resets in 5d 2h"),
                new StatusMeter("month", "54%", 0.54, 0.54, "Resets in 18d"),
            },
            new("Tokens", "今天 96K", "96K", "本月 1.8M", "1.8M"),
            new[] { new StatusPhrase("Month: Kimi Code 35% · Kimi app 19%", new[] { "35%", "19%" }) }),
    };
}
