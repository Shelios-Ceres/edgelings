using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.WinUI.Drawer;
using AgentIsland.WinUI.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using WinRT.Interop;

namespace AgentIsland.WinUI.Shell;

internal sealed class HoverCardPreview : Window
{
    private static readonly string[] Names = { "Claude", "Codex", "Kimi" };
    private static readonly uint[] Swatches = { 0xE49A79, 0x9EBDF5, 0x88CDB6 };
    private static readonly int?[] Sequence = { 0, 1, 2, 0, null, 2, 1, 0, 2, null, 1, 2 };

    private readonly Canvas _surface = new();
    private readonly HoverCard _card;
    // The card and bubbles in the drawer's current style.
    private readonly bool _pixel = DrawerStore.Shared.Theme == CharacterTheme.Pixel;
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
        _card = new HoverCard(_surface, inWindow: true);

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
            _bubble ??= new SpeechBubble(_surface, inWindow: true);
            _bubble.Say("Synthetic bubble", Swatches[0], _pixel, characterRight: true, Anchor(0), Area, false, LineKind.Waiting);
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
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_POPUP_FILM") is { Length: > 0 })
        {
            // A film shows the card and the bubble alone, not the test window's own text.
            foreach (var element in new UIElement[] { heading, description, actions, motion, _passThrough, _status, _diagnostics })
                element.Visibility = Visibility.Collapsed;
        }
        _surface.SizeChanged += (_, _) => Layout();
        _surface.Loaded += (_, _) =>
        {
            if (_closed) return;
            Layout();
            if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_POPUP_FILM") is { Length: > 0 } film) _ = FilmAsync(film);
            else Select(0);
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
        _card.Show(new[] { (Card(target), Swatches[target]) }, _pixel, characterRight: true,
            Anchor(target), Area, reducedMotion: _reduced.IsChecked == true || Motion.ReducedMotion, retarget: retarget);
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

    /// AGENTISLAND_DEBUG_POPUP_FILM=<dir>: the card's moments (M3) and the
    /// bubble's (M5), each stepped by hand frame by frame and printed from
    /// this window, so the paper's shadow shows too, as a strip per moment:
    /// Pixel every tick, Classic every 33 ms, until one frame past the end.
    private async Task FilmAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        UiMoment.Manual = true;
        try
        {
            var name = (_pixel ? "pixel" : "classic") + "-" + (Brand.IsDark() ? "dark" : "light") + (Motion.ReducedMotion ? "-reduced" : "");
            var area = Area;
            var card = new Rect(area.Right - 92 - 10 - 300, area.Top, 380, area.Height);
            if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_FILM_ONLY") == "queue")
            {
                UiMoment.Manual = false;
                await QueueFilmAsync(directory, name, new Rect(area.Right - 92 - 10 - 260, Anchor(0).Top - 70, 340, 190));
                DebugTrace.Write("popup film saved in " + directory);
                return;
            }
            if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_FILM_ONLY") == "session")
            {
                // Only the per-session bubbles and the card's list.
                _bubble ??= new SpeechBubble(_surface, inWindow: true);
                await SessionFilm(directory, name, new Rect(area.Right - 92 - 10 - 260, Anchor(0).Top - 70, 340, 190));
                DebugTrace.Write("popup film saved in " + directory);
                return;
            }
            await Film(Path.Combine(directory, $"m3-open-{name}.png"), card, () => ShowTarget(0));
            await Film(Path.Combine(directory, $"m3-switch-{name}.png"), card, () => ShowTarget(1));
            await Film(Path.Combine(directory, $"m3-close-{name}.png"), card, () => _card.Hide(animated: true));
            // M4: Codex's card refreshes in place: a meter climbs past 90 %,
            // and its phase and detail change.
            ShowTarget(1);
            UiMoment.Finish();
            await Task.Delay(500);
            await PopupPrint.FilmSequenceAsync(Path.Combine(directory, $"m4-refresh-{name}.png"), () =>
                _card.Show(new[] { (Refreshed(), Swatches[1]) }, _pixel, characterRight: true, Anchor(1), Area, Motion.ReducedMotion), _pixel, Crop(card));
            // M4 with a height change: from a one-line status, the detail wraps
            // onto a second line (the same layout, taller) and back; then a
            // meter row appears (a new layout) and goes.
            UiMoment.Finish();
            await Task.Delay(500);
            ShowRefreshed(Refreshed("Synthetic edit"));
            UiMoment.Finish();
            await Task.Delay(500);
            const string longer = "Synthetic edit · a longer motion preview detail that needs a second line";
            await PopupPrint.FilmSequenceAsync(Path.Combine(directory, $"m4-taller-{name}.png"), () => ShowRefreshed(Refreshed(longer)), _pixel, Crop(card), before: true);
            UiMoment.Finish();
            await Task.Delay(500);
            await PopupPrint.FilmSequenceAsync(Path.Combine(directory, $"m4-shorter-{name}.png"), () => ShowRefreshed(Refreshed("Synthetic edit")), _pixel, Crop(card), before: true);
            UiMoment.Finish();
            await Task.Delay(500);
            ShowRefreshed(Refreshed(longer));
            UiMoment.Finish();
            await Task.Delay(500);
            await PopupPrint.FilmSequenceAsync(Path.Combine(directory, $"m4-row-{name}.png"), () => ShowRefreshed(Refreshed(longer, month: true)), _pixel, Crop(card), before: true);
            UiMoment.Finish();
            await Task.Delay(500);
            await PopupPrint.FilmSequenceAsync(Path.Combine(directory, $"m4-row-gone-{name}.png"), () => ShowRefreshed(Refreshed(longer)), _pixel, Crop(card), before: true);
            _card.Hide();
            // M3 for the all-agents card, which takes a second column here: it
            // opens, gives way to one character's card, and closes.
            var all = new Rect(area.Right - 92 - 10 - 580, area.Top, 620, area.Height);
            await Film(Path.Combine(directory, $"m3-all-open-{name}.png"), all, ShowAll);
            await Film(Path.Combine(directory, $"m3-all-switch-{name}.png"), all, () => ShowTarget(1));
            _card.Hide();
            ShowAll();
            UiMoment.Finish();
            await Task.Delay(500);
            await Film(Path.Combine(directory, $"m3-all-close-{name}.png"), all, () => _card.Hide(animated: true));
            _card.Hide();
            var bubble = new Rect(area.Right - 92 - 10 - 260, Anchor(0).Top - 70, 340, 190);
            _bubble ??= new SpeechBubble(_surface, inWindow: true);
            foreach (var kind in Enum.GetValues<LineKind>())
            {
                var line = kind switch
                {
                    LineKind.Done => "Done — your turn!",
                    LineKind.Error => "Hit an error.",
                    LineKind.Plain => "Locked in place.\nRight-click to unlock.",
                    _ => "Can I run this? Needs your OK.",
                };
                await Film(Path.Combine(directory, $"m5-{kind.ToString().ToLowerInvariant()}-{name}.png"), bubble,
                    () => _bubble.Say(line, Swatches[0], _pixel, characterRight: true, Anchor(0), Area, Motion.ReducedMotion, kind));
            }
            await Film(Path.Combine(directory, $"m5-dismiss-{name}.png"), bubble, () => _bubble.Hide(animated: true));
            await SessionFilm(directory, name, bubble);
        }
        catch (Exception error)
        {
            DebugTrace.Write("popup film failed: " + error);
        }
        finally
        {
            UiMoment.Manual = false;
        }
        DebugTrace.Write("popup film saved in " + directory);
    }

    private static readonly DateTimeOffset FilmNow = DateTimeOffset.UtcNow;

    private static SessionEvent Waiting(string title, SessionEventKind kind, int minutesAgo = 1) =>
        new(TriggerTool.Claude, title, title, kind, kind + ":" + title, FilmNow.AddMinutes(-minutesAgo));

    /// A stock line, in the app's language (Pixel's panels read in English).
    private string Stock(string line)
    {
        using (_pixel ? L10n.English() : null) return L10n.Tr(line);
    }

    /// Phase 3: the bubbles that say only stock lines (a merged one says how
    /// many sessions wait), and the card's list of waiting sessions.
    private async Task SessionFilm(string directory, string name, Rect bubble)
    {
        string File(string take) => Path.Combine(directory, $"{take}-{name}.png");
        var reduced = Motion.ReducedMotion;
        void Say(string line, LineKind kind) =>
            _bubble!.Say(line, Swatches[0], _pixel, characterRight: true, Anchor(0), Area, reduced, kind);
        await Film(File("m5-done"), bubble, () => Say(Stock("Done \u2014 your turn!"), LineKind.Done));
        await Film(File("m5-approval"), bubble, () => Say(Stock("Can I run this? Needs your OK."), LineKind.Waiting));
        await Film(File("m5-question"), bubble, () => Say(Stock("Quick question for you."), LineKind.Waiting));
        await Film(File("m5-error"), bubble, () => Say(Stock("Hit an error."), LineKind.Error));
        string Count(int n) { using (_pixel ? L10n.English() : null) return BotLines.SummaryTitle(n); }
        await Film(File("m5-merged"), bubble, () => Say(Count(_pixel ? 3 : 2), LineKind.Waiting));
        _bubble!.Hide(animated: false);

        var card = new Rect(Area.Right - 92 - 10 - 300, Area.Top, 380, Area.Height);
        var few = new[]
        {
            Waiting("Windows Builder", SessionEventKind.Approval),
            Waiting("Main", SessionEventKind.Done, 3),
            Waiting("\u6D4B\u8BD5", SessionEventKind.Done, 2),
        };
        var many = new[]
        {
            Waiting("Windows Builder", SessionEventKind.Approval),
            Waiting("Main", SessionEventKind.Question),
            Waiting("A very long session title that has to wrap onto a second line and then trim", SessionEventKind.Error),
            Waiting("\u6D4B\u8BD5", SessionEventKind.Done, 2),
            Waiting("Classic style design", SessionEventKind.Done, 4),
            Waiting("Pixel-Art design", SessionEventKind.Done, 6),
            Waiting("Context length", SessionEventKind.Done, 8),
        };
        StatusWaitingList List(SessionEvent[] events) =>
            _pixel ? StatusWaitingList.Of(events) : StatusWaitingList.ForClassic(events, FilmNow);
        await Film(File("m6-waiting-few"), card, () =>
            _card.Show(new[] { (Card(0) with { Waiting = List(few) }, Swatches[0]) }, _pixel, characterRight: true, Anchor(0), Area, reduced));
        // A card shown over a card is a refresh, not an open: close it first.
        _card.Hide();
        UiMoment.Finish();
        await Task.Delay(500);
        await Film(File("m6-waiting-many"), card, () =>
            _card.Show(new[] { (Card(0) with { Waiting = List(many) }, Swatches[0]) }, _pixel, characterRight: true, Anchor(0), Area, reduced));
        _card.Hide();
    }

    /// AGENTISLAND_DEBUG_QUEUE_FILM=<dir>: two sessions of one agent finish a
    /// second apart, and the bot's queue says them in real time with the
    /// style's own rules: a frame every half second for seven seconds. (The
    /// drawer film shows the bot's hop; here the bubble's timing is the point.)
    private async Task QueueFilmAsync(string directory, string name, Rect crop)
    {
        Directory.CreateDirectory(directory);
        _bubble ??= new SpeechBubble(_surface, inWindow: true);
        var queue = new SpeechQueue { Rules = _pixel ? SpeechRules.Pixel : SpeechRules.Classic };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _bubble.Closed += () => queue.Dismissed(clock.Elapsed.TotalSeconds);
        var frames = new List<(string, List<(byte[], int, int, int, int)>)>();
        var capture = Crop(crop);
        var added = false;
        var nextShot = 0.0;
        while (clock.Elapsed.TotalSeconds < 7)
        {
            var t = clock.Elapsed.TotalSeconds;
            if (t >= 0.0 && !added)
            {
                added = true;
                queue.Add(Waiting("first", SessionEventKind.Done, 0));
            }
            if (t >= 1.0 && queue.Count + (queue.Shown is null ? 0 : 1) < 2 && !_secondAdded)
            {
                _secondAdded = true;
                queue.Add(Waiting("second", SessionEventKind.Done, 0));
            }
            if (queue.Next(t) is { } speech)
            {
                using (_pixel ? L10n.English() : null)
                {
                    // The two sessions say different stock lines, so the hand-off shows.
                    var line = speech.IsSummary ? BotLines.SummaryTitle(speech.Events.Count) : BotLines.For(speech.First, new Random(1)) ?? "";
                    for (var seed = 2; line == _lastLine && seed < 40 && !speech.IsSummary; seed++) line = BotLines.For(speech.First, new Random(seed)) ?? "";
                    _lastLine = line;
                    var seconds = _pixel ? 5.5 : Math.Clamp(2 + 0.05 * line.Length, 4.5, 9);
                    _bubble.Say(line, Swatches[0], _pixel, characterRight: true, Anchor(0), Area, Motion.ReducedMotion, LineKind.Done, TimeSpan.FromSeconds(seconds));
                }
                DebugTrace.Write($"queue film: bubble at {t:F2} s");
            }
            if (t >= nextShot)
            {
                var shot = await Task.Run(capture);
                frames.Add(($"{t:F1} s", shot));
                nextShot += 0.5;
            }
            await Task.Delay(40);
        }
        await PopupPrint.SaveStrip(Path.Combine(directory, $"queue-{name}.png"), frames);
        _bubble.Hide(animated: false);
    }

    private bool _secondAdded;
    private string? _lastLine;

    /// This window's print, cut to `crop` (DIPs), for the hand clock's films.
    private Func<List<(byte[] Pixels, int Left, int Top, int Width, int Height)>> Crop(Rect crop)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = Win32.GetDpiForWindow(hwnd) / 96.0;
        int x0 = (int)(crop.X * scale), y0 = (int)(crop.Y * scale), w = (int)(crop.Width * scale), h = (int)(crop.Height * scale);
        return () =>
        {
            if (WindowPrint.Client(hwnd) is not { } shot) return new();
            var (pixels, width, height) = shot;
            var frame = new byte[w * h * 4];
            for (var y = 0; y < h && y0 + y < height; y++)
            {
                Buffer.BlockCopy(pixels, ((y0 + y) * width + x0) * 4, frame, y * w * 4, Math.Min(w, width - x0) * 4);
            }
            return new() { (frame, x0, y0, w, h) };
        };
    }

    /// Every synthetic character's card at once, as the drawer's Reveal shows it.
    private void ShowAll() =>
        _card.Show(Names.Select((_, i) => (Card(i), Swatches[i])).ToArray(), _pixel, characterRight: true,
            Anchor(1), Area, reducedMotion: _reduced.IsChecked == true || Motion.ReducedMotion);

    private void ShowRefreshed(StatusCard card) =>
        _card.Show(new[] { (card, Swatches[1]) }, _pixel, characterRight: true, Anchor(1), Area, Motion.ReducedMotion);

    /// Codex's card after a refresh: busier, and its 5-hour window past 90 %;
    /// with month, a third meter.
    private static StatusCard Refreshed(string detail = "Synthetic edit · motion preview", bool month = false) => new(DisplayProvider.Codex, "Codex", "Editing files",
        detail,
        new("Tasks", "3 running", "3"),
        month
            ? new[]
            {
                new StatusMeter("5h", "93%", 0.93, 0.93, "Resets in 1h 38m"),
                new StatusMeter("Week", "31%", 0.31, 0.31, "Resets in 4d 6h"),
                new StatusMeter("Month", "12%", 0.12, 0.12, "Resets in 20d"),
            }
            : new[]
            {
                new StatusMeter("5h", "93%", 0.93, 0.93, "Resets in 1h 38m"),
                new StatusMeter("Week", "31%", 0.31, 0.31, "Resets in 4d 6h"),
            },
        new("Tokens", "Today 131K", "131K", "This month 2.4M", "2.4M"),
        new[] { new StatusPhrase("2 reset cards · synthetic preview", new[] { "2" }) });

    /// One moment's frames, printed and cropped to `crop` (DIPs), in rows of eight.
    private async Task Film(string path, Rect crop, Action trigger)
    {
        var before = UiMoment.Latest;
        trigger();
        if (UiMoment.Latest is not { } moment || moment == before) return;
        var step = _pixel ? Motion.TickMs : 1000.0 / 30;
        var count = (int)Math.Ceiling(moment.DurationMs / step - 1e-6) + 2;
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = Win32.GetDpiForWindow(hwnd) / 96.0;
        int x0 = (int)(crop.X * scale), y0 = (int)(crop.Y * scale), w = (int)(crop.Width * scale), h = (int)(crop.Height * scale);
        const int perRow = 8, label = 24, gap = 6;
        var rows = (count + perRow - 1) / perRow;
        var device = CanvasDevice.GetSharedDevice();
        using var strip = new CanvasRenderTarget(device, gap + Math.Min(count, perRow) * (w + gap), gap + rows * (h + label + gap), 96);
        using (var ds = strip.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(255, 0x80, 0x80, 0x80));
            var text = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 16 };
            for (var k = 0; k < count; k++)
            {
                var ms = _pixel ? k * step + step / 2 : k * step;
                if (k > 0) moment.Step(ms);
                await Task.Delay(120);
                if (WindowPrint.Client(hwnd) is not { } shot) continue;
                var (pixels, width, height) = shot;
                var frame = new byte[w * h * 4];
                for (var y = 0; y < h && y0 + y < height; y++)
                {
                    Buffer.BlockCopy(pixels, ((y0 + y) * width + x0) * 4, frame, y * w * 4, Math.Min(w, width - x0) * 4);
                }
                using var bitmap = CanvasBitmap.CreateFromBytes(device, frame, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
                float left = gap + k % perRow * (w + gap), top = gap + k / perRow * (h + label + gap);
                ds.DrawImage(bitmap, left, top);
                ds.DrawText(_pixel ? $"t={k}" : $"{ms:F0} ms", left + 4, top + h + 1, Windows.UI.Color.FromArgb(255, 255, 255, 255), text);
            }
        }
        await strip.SaveAsync(path);
    }

    private static StatusCard Card(int target) => target switch
    {
        0 => new(DisplayProvider.Claude, "Claude", DrawerLooks.PhaseLabel(AgentPhase.Thinking), null,
            StatusCards.Tasks(2),
            new[] { new StatusMeter(L10n.Tr("5h"), "42%", 0.42, 0.42, L10n.TrFormat("Resets {0}", "2h 15m")) },
            null, Array.Empty<StatusPhrase>()),
        1 => new(DisplayProvider.Codex, "Codex", "Running commands", "Synthetic tool · build preview",
            new("Tasks", "3 running", "3"),
            new[]
            {
                new StatusMeter("5h", "67%", 0.67, 0.67, "Resets in 1h 40m"),
                new StatusMeter("Week", "28%", 0.28, 0.28, "Resets in 4d 6h"),
            },
            new("Tokens", "Today 128K", "128K", "This month 2.4M", "2.4M"),
            new[] { new StatusPhrase("2 reset cards · synthetic preview", new[] { "2" }) }),
        _ => new(DisplayProvider.Kimi, "Kimi Code", "Waiting for your approval", "Synthetic permission prompt",
            new("Tasks", "1 running", "1"),
            new[]
            {
                new StatusMeter("5h", "19%", 0.19, 0.19, "Resets in 3h 10m"),
                new StatusMeter("Week", "38%", 0.38, 0.38, "Resets in 5d 2h"),
                new StatusMeter("Month", "54%", 0.54, 0.54, "Resets in 18d"),
            },
            new("Tokens", "Today 96K", "96K", "This month 1.8M", "1.8M"),
            new[] { new StatusPhrase("Kimi Code 35% · Kimi app 19%", new[] { "35%", "19%" }) { Lead = "Month:" } }),
    };
}
