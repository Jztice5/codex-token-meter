using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using ShapeEllipse = Avalonia.Controls.Shapes.Ellipse;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace CodexTokenMeter;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        if (arguments is ["--check-data", string root])
        {
            TodaySummary usage = new TodayStore(root).Poll(DateTimeOffset.UtcNow);
            Console.WriteLine(JsonSerializer.Serialize(new { usage.Day, usage.Total, usage.Models, usage.Partial }));
            return 0;
        }

        bool preview = arguments.Contains("--preview", StringComparer.Ordinal);
        if (!preview && !OperatingSystem.IsMacOS()) return 2;
        using var instance = new Mutex(true, "CodexTokenMeter.Portable", out bool first);
        if (!first) return 0;
        MeterApplication.Preview = preview;
        return AppBuilder.Configure<MeterApplication>().UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .StartWithClassicDesktopLifetime(arguments, ShutdownMode.OnExplicitShutdown);
    }
}

internal sealed class MeterApplication : Application
{
    internal static bool Preview;
    private Meter meter;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            meter = new Meter(this, desktop, Preview);
            meter.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class Preferences
{
    public string Pinned { get; set; }
    public double X { get; set; }
    public double Y { get; set; }

    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "Codex Token Meter", "placement.json");

    internal static Preferences Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Preferences value = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath));
                if (value != null) return value;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return new Preferences();
    }

    internal void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string draft = FilePath + ".tmp";
            File.WriteAllText(draft, JsonSerializer.Serialize(this));
            File.Move(draft, FilePath, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class Meter
{
    private const double Width = 349, BarHeight = 38, Gap = 6;
    private const double ChatHeight = 256, TodayFixedHeight = 157, ModelRowHeight = 30, ModelListHeight = 150;
    private const int DragReleaseGraceMilliseconds = 1200;
    private static readonly CultureInfo Digits = CultureInfo.GetCultureInfo("en-US");
    private static readonly FontFamily NumberFont = new("Menlo, Cascadia Mono, Consolas");
    private static readonly Geometry ChevronDown = Geometry.Parse(MeterLayout.ChevronDown), ChevronUp = Geometry.Parse(MeterLayout.ChevronUp);

    private readonly SolidColorBrush surface = new(), stroke = new(), quiet = new(), ink = new(), accent = new(),
        selected = new(), inset = new(), track = new();
    private readonly Application app;
    private readonly IClassicDesktopStyleApplicationLifetime lifetime;
    private readonly bool preview;
    private readonly Preferences config;
    private readonly string home;
    private readonly CodexAppearance appearance;
    private readonly SessionTitles titles;
    private readonly TodayStore today;
    private readonly Window window;
    private readonly StackPanel stack = new() { Spacing = Gap };
    private readonly Border bar, details;
    private readonly TextBlock model, smallTotal, status;
    private readonly ShapeEllipse dot = new()
    {
        Width = MeterLayout.StatusDotSize, Height = MeterLayout.StatusDotSize,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
    };
    private readonly Button toggle, chatTab, todayTab;
    private readonly ShapePath chevron;
    private readonly StackPanel mainPanel = new() { Spacing = 8 };
    private readonly DispatcherTimer timer;
    private readonly TrayIcon tray;
    private SessionBinding binding;
    private UsageSnapshot current;
    private TodaySummary daily;
    private string selectedId, followProblem, readProblem;
    private MacView view;
    private int host, misses;
    private bool busy, summing, expanded, showToday, dragging, closed;
    private DateTime nextToday = DateTime.MinValue;
    private PixelPoint dragStart;
    private Point dragOffset;
    private readonly Stopwatch sinceDragRelease = new();

    internal Meter(Application app, IClassicDesktopStyleApplicationLifetime lifetime, bool preview)
    {
        this.app = app;
        this.lifetime = lifetime;
        this.preview = preview;
        config = preview ? new Preferences() : Preferences.Load();
        config.Pinned = SessionBinding.ParseId(config.Pinned);
        home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } configured ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        appearance = new CodexAppearance(home);
        titles = new SessionTitles(home);
        today = new TodayStore(home);
        (bar, details) = (Panel(10), Panel(12));
        (model, smallTotal, status) = (Label("等待模型", 13, true), NumberLabel("—", 14), Label("", 11, subdued: true));
        chevron = new ShapePath
        {
            Width = MeterLayout.ChevronWidth, Height = MeterLayout.ChevronHeight, Data = ChevronDown,
            Stroke = quiet, StrokeThickness = 1.35, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        var detailsLabel = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, Children = { Label("明细", 12, true, true), chevron }
        };
        (toggle, chatTab, todayTab) = (Control(detailsLabel), Control("当前对话"), Control("今日用量"));
        toggle.Background = inset;
        toggle.BorderBrush = stroke;
        toggle.BorderThickness = new Thickness(1);
        AutomationProperties.SetName(toggle, "展开用量明细");
        window = new Window
        {
            Title = "Codex Token Meter", Width = Width, Height = BarHeight, CanResize = false,
            WindowDecorations = WindowDecorations.None, ShowInTaskbar = false, ShowActivated = preview,
            Topmost = true, Background = Brushes.Transparent, Content = stack,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent }
        };
        app.ActualThemeVariantChanged += OnThemeChanged;
        RefreshThemePreference();
        PaintTheme();
        window.KeyDown += (_, args) => { if (args.Key == Key.Escape && expanded) SetExpanded(false); };
        window.Closed += (_, _) => Stop();
        BuildPanel();

        tray = new TrayIcon
        {
            Icon = new WindowIcon(new MemoryStream(Convert.FromBase64String(TrayImage))), ToolTipText = "Codex Token Meter",
            Menu = BuildMenu(), IsVisible = !preview
        };
        TrayIcon.SetIcons(app, new TrayIcons { tray });
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(480), DispatcherPriority.Background, (_, _) => Tick());
        timer.Stop();
    }

    private void RefreshThemePreference()
    {
        ThemeVariant theme = appearance.ReadDarkPreference() switch
        {
            true => ThemeVariant.Dark,
            false => ThemeVariant.Light,
            null => ThemeVariant.Default
        };
        if (app.RequestedThemeVariant != theme) app.RequestedThemeVariant = theme;
    }

    private void OnThemeChanged(object sender, EventArgs args) => PaintTheme();

    private void PaintTheme()
    {
        MeterPalette palette = app.ActualThemeVariant == ThemeVariant.Dark ? MeterPalette.Dark : MeterPalette.Light;
        surface.Color = Color.Parse(palette.Surface);
        stroke.Color = Color.Parse(palette.Stroke);
        quiet.Color = Color.Parse(palette.Quiet);
        ink.Color = Color.Parse(palette.Ink);
        accent.Color = Color.Parse(palette.Signal);
        selected.Color = Color.Parse(palette.Hover);
        inset.Color = Color.Parse(palette.Inset);
        track.Color = Color.Parse(palette.Track);
    }

    internal void Start()
    {
        if (preview)
        {
            view = new MacView { Model = "GPT-6.2 Sol" };
            current = new UsageSnapshot { UpdatedAt = DateTime.UtcNow, Total = TokenCounts.Read(new JsonReader().Read(
                "{\"value\":{\"total_tokens\":299054,\"input_tokens\":296605,\"output_tokens\":2449,\"cached_input_tokens\":249344,\"reasoning_output_tokens\":275}}"), "value") };
            daily = new TodaySummary(TodayStore.BeijingDay(DateTimeOffset.UtcNow), new Dictionary<string, long> { { "gpt-6.2-sol", 299054 }, { "gpt-6.1-sol", 280011 } }, false);
            Render();
            window.Position = new PixelPoint(150, 200);
            SetExpanded(true);
            window.Show();
            return;
        }

        if (!MacDesktop.Trusted) MacDesktop.RequestAccess();
        timer.Start();
        Tick();
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();
        AddMenu(menu, "自动跟随当前对话", () => SetPinned(null));
        AddMenu(menu, "绑定对话 ID…", BindDialog);
        AddMenu(menu, "打开辅助功能设置", () =>
        {
            MacDesktop.RequestAccess();
            System.Diagnostics.Process.Start("/usr/bin/open", "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility");
        });
        AddMenu(menu, "重置浮条位置", () => { config.X = config.Y = 0; config.Save(); Place(); });
        menu.Items.Add(new NativeMenuItemSeparator());
        AddMenu(menu, "退出浮条", () => lifetime.Shutdown());
        return menu;
    }

    private static void AddMenu(NativeMenu menu, string name, Action action)
    {
        var item = new NativeMenuItem(name);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void BuildPanel()
    {
        stack.Children.Add(bar);
        stack.Children.Add(details);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("26,13,*,1,102,62"), Margin = new Thickness(3, 0, 6, 0), Height = BarHeight - 2 };
        var grip = Label("⠿", 15, false, true);
        grip.Cursor = new Cursor(StandardCursorType.SizeAll);
        grip.HorizontalAlignment = HorizontalAlignment.Stretch;
        grip.TextAlignment = TextAlignment.Center;
        AutomationProperties.SetName(grip, "拖动浮条");
        ToolTip.SetTip(grip, "拖动浮条");
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            dragStart = window.PointToScreen(e.GetPosition(window));
            dragOffset = new Point(config.X, config.Y);
            dragging = true;
            e.Pointer.Capture(grip);
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            PixelPoint next = window.PointToScreen(e.GetPosition(window));
            config.X = dragOffset.X + next.X - dragStart.X;
            config.Y = dragOffset.Y + next.Y - dragStart.Y;
            Place();
        };
        grip.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            FinishDrag();
            e.Pointer.Capture(null);
        };
        grip.PointerCaptureLost += (_, _) => FinishDrag();
        Put(row, grip, 0);
        Put(row, dot, 1);
        Put(row, model, 2);
        Put(row, new Border { Background = stroke, Margin = new Thickness(0, 8) }, 3);
        var barCount = AmountWithUnit(smallTotal, 10, 1);
        barCount.HorizontalAlignment = HorizontalAlignment.Center;
        Put(row, barCount, 4);
        toggle.Click += (_, _) => SetExpanded(!expanded);
        Put(row, toggle, 5);
        bar.Child = row;

        var body = new StackPanel { Spacing = 11, Margin = new Thickness(14, 10, 14, 10) };
        var tabs = new Grid { ColumnDefinitions = new ColumnDefinitions("*,2,*") };
        chatTab.Click += (_, _) => ChangeTab(false);
        todayTab.Click += (_, _) => ChangeTab(true);
        Put(tabs, chatTab, 0);
        Put(tabs, todayTab, 2);
        body.Children.Add(new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(3), Background = inset, Child = tabs });
        body.Children.Add(mainPanel);
        details.Child = body;
    }

    private void FinishDrag()
    {
        if (!dragging) return;
        dragging = false;
        sinceDragRelease.Restart();
        config.Save();
    }

    private async void Tick()
    {
        if (closed || preview) return;
        RefreshThemePreference();
        var foreground = MacDesktop.Frontmost();
        if (foreground.Pid == 0 && host != 0 && window.IsVisible &&
            (dragging || (sinceDragRelease.IsRunning && sinceDragRelease.ElapsedMilliseconds < DragReleaseGraceMilliseconds)))
            return;
        if (!foreground.Codex && !(foreground.Pid == Environment.ProcessId && host != 0))
        {
            host = 0;
            if (window.IsVisible) window.Hide();
            return;
        }
        if (foreground.Codex && host != foreground.Pid)
        {
            host = foreground.Pid;
            misses = 0;
            view = null;
            Select(null);
        }
        Place();
        if (!window.IsVisible) window.Show();
        if (expanded && showToday && DateTime.UtcNow >= nextToday) RefreshToday();
        if (busy || !foreground.Codex) return;

        int expected = host;
        string pinned = config.Pinned;
        busy = true;
        try
        {
            var detected = await Task.Run(() =>
            {
                MacView info = MacDesktop.Inspect(expected);
                FollowResult match = pinned != null ? new FollowResult(pinned) : info != null
                    ? titles.Match(info.Route, info.Title, info.Header) : null;
                return (info, match);
            });
            if (closed || expected != host || config.Pinned != pinned || MacDesktop.Frontmost().Pid != expected) return;
            if (detected.info == null && pinned == null)
            {
                if (++misses >= 2)
                {
                    view = null;
                    followProblem = MacDesktop.Trusted ? "暂时无法识别前台对话" : "请在系统设置中授权辅助功能";
                    Select(null);
                    Render();
                }
                return;
            }
            misses = 0;
            view = detected.info;
            followProblem = detected.match?.Problem;
            Select(detected.match?.Id);
            RenderIfChanged();
            Place();
            SessionBinding candidate = binding;
            if (candidate == null) return;
            UsageSnapshot snapshot = await Task.Run(() => candidate.Poll());
            if (closed || expected != host || candidate != binding) return;
            current = snapshot;
            readProblem = null;
            RenderIfChanged();
        }
        catch (IOException) { readProblem = "暂时无法读取本地记录，正在重试"; RenderIfChanged(); }
        catch (UnauthorizedAccessException) { readProblem = "无法访问本地 Codex 记录"; RenderIfChanged(); }
        finally { busy = false; }
    }

    private async void RefreshToday()
    {
        if (preview || summing || closed) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (daily != null && daily.Day != TodayStore.BeijingDay(now)) { daily = null; Render(); }
        summing = true;
        nextToday = now.UtcDateTime.AddSeconds(2);
        try
        {
            TodaySummary snapshot = await Task.Run(() => today.Poll(now));
            if (closed || snapshot.Day != TodayStore.BeijingDay(DateTimeOffset.UtcNow)) return;
            daily = snapshot;
            RenderIfChanged();
        }
        catch (IOException) { daily = new TodaySummary(TodayStore.BeijingDay(now), daily?.Models ?? new(), true); RenderIfChanged(); }
        catch (UnauthorizedAccessException) { daily = new TodaySummary(TodayStore.BeijingDay(now), daily?.Models ?? new(), true); RenderIfChanged(); }
        finally { summing = false; }
    }

    private void Select(string id)
    {
        if (selectedId == id) return;
        selectedId = id;
        binding = id == null ? null : new SessionBinding(home, id);
        current = null;
        readProblem = null;
    }

    private void SetPinned(string id)
    {
        config.Pinned = id;
        config.Save();
        followProblem = null;
        Select(id);
        Render();
        Tick();
    }

    private void BindDialog()
    {
        var dialog = new Window { Title = "绑定 Codex 对话", Width = 428, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var field = new TextBox { Text = selectedId ?? "", PlaceholderText = "聊天链接或 /status 显示的对话 ID" };
        var prompt = Label("在 Codex 输入 /status 可查看对话 ID。", 12, false, true);
        var confirm = new Button { Content = "绑定", HorizontalAlignment = HorizontalAlignment.Right };
        confirm.Click += (_, _) =>
        {
            string id = SessionBinding.ParseId(field.Text);
            if (id == null) { prompt.Text = "请输入有效的聊天链接或对话 ID。"; return; }
            SetPinned(id);
            dialog.Close();
        };
        dialog.Content = new StackPanel { Margin = new Thickness(18), Spacing = 10, Children = { prompt, field, confirm } };
        dialog.Opened += (_, _) => field.Focus();
        dialog.Show();
        dialog.Activate();
    }

    private void SetExpanded(bool setting)
    {
        expanded = setting;
        chevron.Data = expanded ? ChevronUp : ChevronDown;
        AutomationProperties.SetName(toggle, expanded ? "收起用量明细" : "展开用量明细");
        Render();
        Place();
        if (expanded && showToday) RefreshToday();
    }

    private void ChangeTab(bool useToday) { showToday = useToday; SetExpanded(true); }

    private double DetailHeight => showToday
        ? TodayFixedHeight + Math.Min(ModelListHeight, Math.Max(1, daily?.Models.Count ?? 1) * ModelRowHeight)
        : ChatHeight;

    private void Place()
    {
        double height = BarHeight + (expanded ? DetailHeight + Gap : 0);
        window.Height = height;
        if (preview) return;
        var primary = window.Screens.Primary;
        if (primary == null) return;
        DesktopRect box = view?.Composer ?? default;
        DesktopRect frame = view?.Window ?? default;
        DesktopRect hostBox = box.Valid ? box : frame;
        var hostScreen = hostBox.Valid ? window.Screens.ScreenFromPoint(new PixelPoint((int)(hostBox.X + hostBox.Width * .5), (int)(hostBox.Y + hostBox.Height * .5))) ?? primary : primary;
        double scale = window.RenderScaling;
        if (OperatingSystem.IsMacOS()) scale = 1; // Avalonia.Native uses screen points for positions on macOS.
        PixelRect work = hostScreen.WorkingArea;
        double x = box.Valid ? box.Right - Width : frame.Valid ? frame.X + frame.Width * .72 - Width : work.Right - (Width + 30) * scale;
        double y = box.Valid ? box.Y - BarHeight - 10 : frame.Valid ? frame.Bottom - 156 : work.Bottom - 156 * scale;
        x += config.X;
        y += config.Y;
        var target = window.Screens.ScreenFromPoint(new PixelPoint((int)(x + Width * scale / 2), (int)y)) ?? hostScreen;
        work = target.WorkingArea;
        x = Math.Clamp(x, work.X + 6, Math.Max(work.X + 6, work.Right - Width * scale - 6));
        y = Math.Clamp(y, work.Y + 6, Math.Max(work.Y + 6, work.Bottom - BarHeight * scale - 6));
        double extra = (height - BarHeight) * scale;
        bool above = expanded && y - extra >= work.Y + 6;
        if (stack.Children[0] != (above ? details : bar))
        {
            stack.Children.Clear();
            if (above) { stack.Children.Add(details); stack.Children.Add(bar); }
            else { stack.Children.Add(bar); stack.Children.Add(details); }
        }
        var position = new PixelPoint((int)Math.Round(x), (int)Math.Max(work.Y + 6, Math.Min(y - (above ? extra : 0), work.Bottom - height * scale - 6)));
        if (window.Position != position) window.Position = position;
    }

    private string lastPaint;
    private void RenderIfChanged()
    {
        var key = string.Join("|", selectedId, followProblem, readProblem, view?.Model, current?.Model, current?.UpdatedAt?.Ticks,
            current?.Total?.Total, current?.Running, current?.MissingUsage, daily?.Total, daily?.Partial,
            daily == null ? "" : string.Join(";", daily.Models.OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Value)));
        if (lastPaint == key) return;
        lastPaint = key;
        Render();
    }

    private void Render()
    {
        string picked = config.Pinned == null && current?.Running != true ? view?.Model : null;
        string slug = picked ?? current?.Model;
        model.Text = slug == null ? "等待当前对话" : FormatModel(slug);
        ToolTip.SetTip(model, followProblem ?? readProblem ?? slug);
        TokenCounts counts = current?.Total;
        dot.Fill = counts == null || current.MissingUsage ? stroke : accent;
        smallTotal.Text = counts == null ? "—" : Compact(counts.Total);
        AutomationProperties.SetName(smallTotal, counts == null ? "等待会话用量" : "会话累计 " + Number(counts.Total) + " token");
        details.IsVisible = expanded;
        if (!expanded) return;
        details.Height = DetailHeight;
        chatTab.Background = showToday ? Brushes.Transparent : selected;
        todayTab.Background = showToday ? selected : Brushes.Transparent;
        chatTab.Foreground = showToday ? quiet : ink;
        todayTab.Foreground = showToday ? ink : quiet;
        mainPanel.Children.Clear();

        if (showToday)
        {
            mainPanel.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    Label("今日总用量", 11, false, true),
                    AmountWithUnit(NumberLabel(Number(daily?.Total), 25), 11, 6),
                    Label(daily?.Partial == true ? "北京时间 00:00 起 · 部分用量缺失" : "北京时间 00:00 起 · 本机记录", 11, false, true)
                }
            });
            mainPanel.Children.Add(Label("按模型", 11, false, true));
            if (daily == null || daily.Models.Count == 0)
                mainPanel.Children.Add(Label(daily == null ? "正在读取本地用量…" : "今天还没有本地用量", 12, false, true));
            else
            {
                var rows = new StackPanel();
                foreach (var entry in daily.Models.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = ModelRowHeight };
                    Put(row, Label(entry.Key.Length == 0 ? "模型未知" : FormatModel(entry.Key), 12), 0);
                    Put(row, NumberLabel(Number(entry.Value), 13), 1);
                    rows.Children.Add(row);
                }
                mainPanel.Children.Add(new ScrollViewer { MaxHeight = ModelListHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rows });
            }
        }
        else
        {
            var recent = Metric("最近一次请求", Number(current?.Last?.Total), 17);
            recent.VerticalAlignment = VerticalAlignment.Bottom;
            mainPanel.Children.Add(Pair(Metric("会话累计", Number(counts?.Total), 25), recent, "3*,2*"));
            mainPanel.Children.Add(new Border { Height = 1, Background = stroke });
            mainPanel.Children.Add(new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    Pair(Metric("输入", Number(counts?.Input), 16), Metric("输出", Number(counts?.Output), 16)),
                    Pair(Metric("其中缓存", Number(counts?.Cached), 13), Metric("其中推理", Number(counts?.Reasoning), 13))
                }
            });
            mainPanel.Children.Add(CacheIndicator(MeterLayout.CacheShare(counts)));
            status.Text = readProblem ?? followProblem ?? (current?.MissingUsage == true ? "当前对话有请求未回报完整用量" : current?.UpdatedAt == null
                ? "等待当前对话回报用量" : (config.Pinned == null ? "自动跟随" : "手动绑定") + " · 更新于 " + current.UpdatedAt.Value.Add(TodayStore.BeijingOffset).ToString("HH:mm:ss", Digits));
            mainPanel.Children.Add(status);
        }
        Place();
    }

    private void Stop()
    {
        if (closed) return;
        closed = true;
        timer.Stop();
        app.ActualThemeVariantChanged -= OnThemeChanged;
        tray.Dispose();
        lifetime.Shutdown();
    }

    private static Grid Pair(Control first, Control second, string columns = "*,*")
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
        Put(row, first, 0);
        Put(row, second, 1);
        return row;
    }

    private StackPanel Metric(string name, string exact, int size) => new()
    {
        Spacing = 1,
        Children =
        {
            Label(name, 11, false, true),
            new Viewbox
            {
                Child = NumberLabel(exact, size), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left
            }
        }
    };

    private StackPanel CacheIndicator(double? share)
    {
        var caption = Pair(Label("缓存输入占比", 11, false, true), NumberLabel(share?.ToString("P1", Digits) ?? "—", 11), "*,Auto");
        double fraction = share.GetValueOrDefault();
        var fill = new Border { Background = accent, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
        var rail = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = track, Child = fill };
        rail.SizeChanged += (_, _) => fill.Width = rail.Bounds.Width * fraction;
        return new StackPanel { Spacing = 3, Children = { caption, rail } };
    }

    private StackPanel AmountWithUnit(TextBlock amount, double size, double offset)
    {
        var unit = Label("token", size, false, true);
        unit.Margin = new Thickness(0, offset, 0, 0);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { amount, unit } };
    }

    private TextBlock NumberLabel(string exact, double size)
    {
        var label = Label(exact, size, true);
        label.FontFamily = NumberFont;
        return label;
    }
    private Border Panel(int radius) => new() { CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), BorderBrush = stroke, Background = surface };
    private TextBlock Label(string title, double size, bool strong = false, bool subdued = false) => new()
    {
        Text = title, FontSize = size, FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal, Foreground = subdued ? quiet : ink,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1
    };
    private Button Control(object title) => new()
    {
        Content = title, FontSize = 12, Height = 26, Background = Brushes.Transparent, Foreground = ink, Padding = new Thickness(2, 2),
        CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center
    };
    private static void Put(Grid grid, Control control, int column) { Grid.SetColumn(control, column); grid.Children.Add(control); }
    private static string Number(long? amount) => amount?.ToString("N0", Digits) ?? "—";
    private static string Compact(long amount) => amount >= 1_000_000_000 ? (amount / 1e9).ToString("0.#", Digits) + "B"
        : amount >= 1_000_000 ? (amount / 1e6).ToString("0.#", Digits) + "M" : amount >= 1_000 ? (amount / 1e3).ToString("0.#", Digits) + "K" : Number(amount);
    private static string FormatModel(string name) => name.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
        ? "GPT-" + string.Join(" ", name[4..].Split('-').Select(Digits.TextInfo.ToTitleCase)) : name;

    private const string TrayImage = "iVBORw0KGgoAAAANSUhEUgAAABgAAAAYCAYAAADgdz34AAAAL0lEQVR4nGNgGMwg6vy2/8h41IIhbAEug0YtGIIWkGrQqAWjFoxaMBgtGAUMWAAAH3EEN8JBrNkAAAAASUVORK5CYII=";
}
