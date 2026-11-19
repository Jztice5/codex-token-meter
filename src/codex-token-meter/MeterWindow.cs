using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CodexTokenMeter
{
    internal sealed class MeterWindow : Window
    {
        private const double BarHeight = 38, PanelWidth = 349, ChatHeight = 225, DetailGap = 6;
        private const double ModelRowHeight = 27, ModelListMaxHeight = 146, TodayFixedHeight = 155;
        private const int DragReleaseGraceMilliseconds = 750;
        private static readonly CultureInfo NumberCulture = CultureInfo.GetCultureInfo("zh-CN");
        private readonly MeterSettings settings;
        private readonly string codexHome;
        private readonly CodexAppearance appearance;
        private readonly DispatcherTimer placementTimer, usageTimer, todayTimer;
        private readonly SessionTitles titles;
        private readonly TodayStore todayStore;
        private readonly Forms.NotifyIcon tray;
        private readonly Stopwatch sinceDragRelease = new Stopwatch();
        private readonly Grid frame = new Grid();
        private readonly Border bar, detail, dot, cacheTrack, cacheFill;
        private readonly Border divider, detailRule;
        private readonly Button toggle, chatTab, todayTab;
        private readonly Thumb grip;
        private readonly TextBlock model, smallTotal, total, last, input, output, cached, reasoning, status, todayTotal, todayNote;
        private readonly TextBlock[] quietLabels;
        private readonly StackPanel chatBody, todayBody, modelRows;
        private SessionBinding session;
        private UsageSnapshot snapshot;
        private TodaySummary todaySnapshot;
        private string readError, sessionId, observedTitle, selectedModel, followProblem;
        private int missedViews;
        private bool reading, summing, expanded, dragging, probing, closed, showToday, todayUnavailable;
        private IntPtr handle, host;
        private Rect composer = Rect.Empty;
        private Desktop.Box lastHostBox;
        private DateTime nextProbe;
        private int dragX, dragY, dragWindowX, dragWindowY;
        private double cacheFraction;
        private Brush signal, quiet, ink, stroke, surface, track, hover;
        private bool systemDark;
        private bool? paintedDark;

        internal MeterWindow(string initialId, bool demo, MeterSettings testSettings = null)
        {
            settings = testSettings ?? new MeterSettings();
            Title = "Codex Token Meter";
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            ResizeMode = ResizeMode.NoResize;
            Background = Brushes.Transparent;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Width = PanelWidth;
            Height = BarHeight;
            Content = frame;
            frame.RowDefinitions.Add(new RowDefinition());
            frame.RowDefinitions.Add(new RowDefinition());
            frame.RowDefinitions.Add(new RowDefinition());

            bar = Surface(10);
            var line = new Grid { Margin = new Thickness(2, 0, 4, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grip = new Thumb { Cursor = Cursors.SizeAll, Focusable = false, ToolTip = "拖动浮条 · 右键打开菜单" };
            grip.DragStarted += BeginDrag;
            grip.DragDelta += MoveDrag;
            grip.DragCompleted += EndDrag;
            var gripTemplate = new ControlTemplate(typeof(Thumb));
            var clear = new FrameworkElementFactory(typeof(Border));
            clear.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            gripTemplate.VisualTree = clear;
            grip.Template = gripTemplate;
            AutomationProperties.SetName(grip, "拖动浮条");
            line.Children.Add(grip);
            var mark = Text("⠿", 15, false);
            mark.HorizontalAlignment = HorizontalAlignment.Center;
            mark.IsHitTestVisible = false;
            line.Children.Add(mark);
            dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            Put(line, dot, 1);
            model = Text("等待会话", 13, true);
            model.Margin = new Thickness(2, 0, 5, 0);
            Put(line, model, 2);
            divider = new Border { Margin = new Thickness(0, 9, 0, 9) };
            Put(line, divider, 3);
            smallTotal = Text("—", 12, true);
            smallTotal.HorizontalAlignment = HorizontalAlignment.Center;
            Put(line, smallTotal, 4);
            toggle = new Button { Height = 29, Padding = new Thickness(2, 0, 2, 0), Cursor = Cursors.Hand, Content = "明细  ⌄", FontSize = 12, Background = Brushes.Transparent };
            AutomationProperties.SetName(toggle, "展开用量明细");
            toggle.Click += delegate { SetExpanded(!expanded); };
            Put(line, toggle, 5);
            bar.Child = line;
            frame.Children.Add(bar);

            detail = Surface(12);
            var body = new StackPanel { Margin = new Thickness(14, 9, 14, 10) };
            var tabs = Columns();
            tabs.Margin = new Thickness(0, 0, 0, 10);
            chatTab = new Button { Content = "当前对话", Cursor = Cursors.Hand, Height = 26 };
            todayTab = new Button { Content = "今日用量", Cursor = Cursors.Hand, Height = 26 };
            chatTab.Margin = new Thickness(0, 0, 4, 0);
            todayTab.Margin = new Thickness(4, 0, 0, 0);
            chatTab.Click += delegate { SetView(false); };
            todayTab.Click += delegate { SetView(true); };
            tabs.Children.Add(chatTab);
            Put(tabs, todayTab, 1);
            body.Children.Add(tabs);

            chatBody = new StackPanel();
            body.Children.Add(chatBody);
            var summary = Columns();
            var totalsLabel = Text("会话累计", 11, false);
            var lastLabel = Text("最近一次模型请求", 11, false);
            total = Text("—", 22, true);
            last = Text("—", 22, true);
            summary.Children.Add(Metric(totalsLabel, total));
            Put(summary, Metric(lastLabel, last), 1);
            chatBody.Children.Add(summary);
            detailRule = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 7) };
            chatBody.Children.Add(detailRule);

            var metrics = Columns();
            var inputLabel = Text("输入", 11, false);
            var cacheLabel = Text("其中缓存输入", 11, false);
            var outputLabel = Text("输出", 11, false);
            var reasoningLabel = Text("推理", 11, false);
            input = Text("—", 14, true);
            cached = Text("—", 14, true);
            output = Text("—", 14, true);
            reasoning = Text("—", 14, true);
            metrics.Children.Add(Metric(inputLabel, input));
            var cacheBlock = Metric(cacheLabel, cached);
            cacheFill = new Border { Height = 4, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) };
            cacheTrack = new Border { Height = 4, Margin = new Thickness(0, 2, 0, 0), CornerRadius = new CornerRadius(2), Child = cacheFill, ClipToBounds = true };
            cacheTrack.SizeChanged += delegate { ResizeCacheMark(); };
            cacheBlock.Children.Add(cacheTrack);
            Put(metrics, cacheBlock, 1);
            var lowerMetrics = Columns();
            lowerMetrics.Margin = new Thickness(0, 5, 0, 0);
            lowerMetrics.Children.Add(Metric(outputLabel, output));
            Put(lowerMetrics, Metric(reasoningLabel, reasoning), 1);
            chatBody.Children.Add(metrics);
            chatBody.Children.Add(lowerMetrics);
            status = Text("等待本地会话", 11, false);
            status.Margin = new Thickness(0, 7, 0, 0);
            chatBody.Children.Add(status);

            todayBody = new StackPanel { Visibility = Visibility.Collapsed };
            var todayLabel = Text("今日总用量", 11, false);
            todayTotal = Text("—", 24, true);
            todayNote = Text("北京时间 00:00 起 · 本机 · 含缓存输入", 11, false);
            todayNote.Margin = new Thickness(0, 2, 0, 0);
            todayBody.Children.Add(todayLabel);
            todayBody.Children.Add(todayTotal);
            todayBody.Children.Add(todayNote);
            var modelHeading = Text("按模型", 11, true);
            modelHeading.Margin = new Thickness(0, 13, 0, 4);
            todayBody.Children.Add(modelHeading);
            modelRows = new StackPanel();
            var modelScroll = new ScrollViewer { Content = modelRows, MaxHeight = ModelListMaxHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            AutomationProperties.SetName(modelScroll, "今日按模型用量");
            todayBody.Children.Add(modelScroll);
            body.Children.Add(todayBody);
            detail.Child = body;
            frame.Children.Add(detail);
            quietLabels = new[] { mark, totalsLabel, lastLabel, inputLabel, cacheLabel, outputLabel, reasoningLabel, status, todayLabel, todayNote, modelHeading };

            bar.ContextMenu = new ContextMenu();
            AddMenuItem("自动跟随当前对话", EnableFollow);
            AddMenuItem("绑定对话…", ShowBinding);
            AddMenuItem("从剪贴板绑定对话", BindClipboard);
            AddMenuItem("重置浮条位置", ResetPosition);
            bar.ContextMenu.Items.Add(new Separator());
            AddMenuItem("退出浮条", Close);

            tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Text = "Codex Token Meter", Visible = !demo };
            var trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Items.Add("显示浮条（键盘可操作）", null, delegate { KeyboardAccess(); });
            trayMenu.Items.Add("自动跟随当前对话", null, delegate { EnableFollow(); });
            trayMenu.Items.Add("绑定对话…", null, delegate { ShowBinding(); });
            trayMenu.Items.Add("从剪贴板绑定对话", null, delegate { BindClipboard(); });
            trayMenu.Items.Add("重置浮条位置", null, delegate { ResetPosition(); });
            trayMenu.Items.Add(new Forms.ToolStripSeparator());
            trayMenu.Items.Add("退出浮条", null, delegate { Close(); });
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { KeyboardAccess(); };

            SourceInitialized += delegate
            {
                handle = new WindowInteropHelper(this).Handle;
                HwndSource.FromHwnd(handle).AddHook(WindowMessage);
            };
            PreviewKeyDown += HandleKeys;
            Closed += Cleanup;

            string configured = Environment.GetEnvironmentVariable("CODEX_HOME");
            codexHome = String.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : configured;
            appearance = new CodexAppearance(codexHome);
            SystemEvents.UserPreferenceChanged += OnThemeChanged;
            RefreshSystemTheme();
            SizePanel(false);
            titles = new SessionTitles(codexHome);
            todayStore = new TodayStore(codexHome);
            if (!settings.Follow) ChangeSession(initialId ?? settings.SessionId);
            UpdateUsage();

            placementTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(215) };
            placementTimer.Tick += async delegate { await RefreshPlacement(); };
            usageTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(430) };
            usageTimer.Tick += async delegate { RefreshTheme(); await RefreshUsage(); };
            todayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            todayTimer.Tick += async delegate { if (expanded && showToday && IsVisible) await RefreshToday(); };
            if (!demo)
            {
                placementTimer.Start();
                usageTimer.Start();
                todayTimer.Start();
            }
        }

        private static Border Surface(double radius)
        {
            return new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(radius) };
        }

        private static TextBlock Text(string value, double size, bool strong)
        {
            return new TextBlock { Text = value, FontSize = size, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        }

        private static Grid Columns()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return grid;
        }

        private static void Put(Grid grid, UIElement element, int column)
        {
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }

        private static StackPanel Metric(TextBlock label, TextBlock value)
        {
            var metric = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            metric.Children.Add(label);
            metric.Children.Add(value);
            return metric;
        }

        private static Brush Paint(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        private void OnThemeChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (!closed) Dispatcher.BeginInvoke(new Action(RefreshSystemTheme));
        }

        private void RefreshSystemTheme()
        {
            object lightSetting = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            systemDark = lightSetting is int && (int)lightSetting == 0;
            RefreshTheme(true);
        }

        private void RefreshTheme(bool force = false)
        {
            bool dark = appearance.ReadDarkPreference() ?? systemDark;
            if (!force && paintedDark == dark) return;
            paintedDark = dark;
            MeterPalette palette = dark ? MeterPalette.Dark : MeterPalette.Light;
            surface = Paint(palette.Surface);
            ink = Paint(palette.Ink);
            quiet = Paint(palette.Quiet);
            stroke = Paint(palette.Stroke);
            signal = Paint(palette.Signal);
            track = Paint(palette.Track);
            hover = Paint(palette.Hover);
            if (SystemParameters.HighContrast)
            {
                surface = SystemColors.WindowBrush;
                ink = quiet = stroke = SystemColors.WindowTextBrush;
                signal = hover = SystemColors.HighlightBrush;
                track = SystemColors.ControlBrush;
            }

            bar.Background = detail.Background = surface;
            bar.BorderBrush = detail.BorderBrush = stroke;
            divider.Background = detailRule.Background = stroke;
            foreach (TextBlock label in quietLabels) label.Foreground = quiet;
            foreach (TextBlock value in new[] { model, smallTotal, total, last, input, cached, output, reasoning, todayTotal }) value.Foreground = ink;
            cacheTrack.Background = track;
            cacheFill.Background = signal;
            foreach (Button button in new[] { toggle, chatTab, todayTab }) button.Template = ButtonTemplate();
            toggle.Foreground = quiet;
            UpdateTabStyle();
            UpdateUsage();
            UpdateToday();
        }

        private ControlTemplate ButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
            chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            chrome.AppendChild(content);
            template.VisualTree = chrome;
            var over = new Trigger { Property = IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Border.BackgroundProperty, hover, "Chrome"));
            template.Triggers.Add(over);
            var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, signal, "Chrome"));
            template.Triggers.Add(focus);
            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BorderBrushProperty, stroke, "Chrome"));
            template.Triggers.Add(pressed);
            return template;
        }

        private async Task RefreshUsage()
        {
            if (reading || session == null) return;
            reading = true;
            SessionBinding current = session;
            try
            {
                UsageSnapshot read = await Task.Run(() => current.Poll());
                if (session != current || closed) return;
                snapshot = read;
                readError = null;
            }
            catch (IOException) { if (session == current) readError = "暂时无法读取用量，正在重试"; }
            catch (UnauthorizedAccessException) { if (session == current) readError = "无法访问本地会话目录"; }
            finally { reading = false; }
            if (!closed) UpdateUsage();
        }

        private static string ModelLabel(string slug)
        {
            if (String.IsNullOrWhiteSpace(slug)) return "等待模型";
            if (!slug.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)) return slug;
            string[] parts = slug.Substring(4).Split('-');
            for (int index = 1; index < parts.Length; index++) parts[index] = NumberCulture.TextInfo.ToTitleCase(parts[index]);
            return "GPT-" + String.Join(" ", parts);
        }

        private static string Exact(long? number) { return number.HasValue ? number.Value.ToString("N0", NumberCulture) : "—"; }

        private static string Compact(long number)
        {
            if (number >= 1000000000) return (number / 1000000000.0).ToString("0.#", NumberCulture) + "B";
            if (number >= 1000000) return (number / 1000000.0).ToString("0.#", NumberCulture) + "M";
            if (number >= 1000) return (number / 1000.0).ToString("0.#", NumberCulture) + "K";
            return Exact(number);
        }

        private void UpdateUsage()
        {
            UsageSnapshot reading = snapshot;
            TokenCounts counts = reading == null ? null : reading.Total;
            string runtimeModel = reading == null ? null : reading.Model;
            string visibleModel = settings.Follow && (reading == null || !reading.Running || runtimeModel == null) ? selectedModel : null;
            string currentModel = visibleModel ?? runtimeModel;
            model.Text = currentModel != null ? ModelLabel(currentModel) : session == null ? (settings.Follow ? "等待当前对话" : "未绑定对话") : "等待模型";
            model.ToolTip = followProblem ?? currentModel ?? StatusText(reading);
            smallTotal.Text = counts == null ? "— token" : Compact(counts.Total) + " token";
            smallTotal.ToolTip = counts == null ? null : "会话累计 " + Exact(counts.Total) + " token";
            AutomationProperties.SetName(smallTotal, smallTotal.ToolTip as string ?? "等待 token 用量");
            dot.Background = counts != null && readError == null ? signal : stroke;
            total.Text = Exact(counts == null ? (long?)null : counts.Total);
            last.Text = Exact(reading == null || reading.Last == null ? (long?)null : reading.Last.Total);
            input.Text = Exact(counts == null ? (long?)null : counts.Input);
            output.Text = Exact(counts == null ? (long?)null : counts.Output);
            cached.Text = Exact(counts == null ? (long?)null : counts.Cached);
            reasoning.Text = Exact(counts == null ? (long?)null : counts.Reasoning);
            bool hasCacheRatio = counts != null && counts.Input.GetValueOrDefault() > 0 && counts.Cached.HasValue;
            cacheFraction = hasCacheRatio ? Math.Min(1, (double)counts.Cached.Value / counts.Input.Value) : 0;
            ResizeCacheMark();
            cacheTrack.ToolTip = hasCacheRatio ? "缓存输入占比 " + cacheFraction.ToString("P1", NumberCulture) : null;
            status.Text = StatusText(reading);
            status.ToolTip = sessionId == null ? status.Text : status.Text + "\n会话 ID " + sessionId;
        }

        private string StatusText(UsageSnapshot reading)
        {
            if (readError != null) return readError;
            if (session == null)
            {
                if (!settings.Follow) return "右键绑定本地 Codex 对话";
                return followProblem ?? "正在识别当前对话";
            }
            if (reading == null) return "正在读取当前对话用量";
            if (!reading.UpdatedAt.HasValue) return "等待 Codex 回报用量";
            if (reading.MissingUsage) return "当前对话有请求未回报完整用量";
            string time = new DateTimeOffset(reading.UpdatedAt.Value).ToOffset(TodayStore.BeijingOffset).ToString("HH:mm:ss", NumberCulture);
            return (reading.Running ? "生成中 · 上次回报 " : settings.Follow ? "自动跟随 · 更新于 " : "已手动绑定 · 更新于 ") + time;
        }

        private async Task RefreshToday()
        {
            if (summing || todayStore == null || closed) return;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (todaySnapshot != null && todaySnapshot.Day != TodayStore.BeijingDay(now))
            {
                todaySnapshot = null;
                UpdateToday();
            }
            summing = true;
            try
            {
                TodaySummary read = await Task.Run(() => todayStore.Poll(now));
                if (closed || read.Day != TodayStore.BeijingDay(DateTimeOffset.UtcNow)) return;
                if (!todayUnavailable && todaySnapshot != null && todaySnapshot.Day == read.Day && todaySnapshot.Partial == read.Partial
                    && todaySnapshot.Models.Count == read.Models.Count && read.Models.All(x => todaySnapshot.Models.ContainsKey(x.Key) && todaySnapshot.Models[x.Key] == x.Value)) return;
                todaySnapshot = read;
                todayUnavailable = false;
            }
            catch (IOException) { todayUnavailable = true; }
            catch (UnauthorizedAccessException) { todayUnavailable = true; }
            finally { summing = false; }
            if (!closed) UpdateToday();
        }

        private void UpdateToday()
        {
            modelRows.Children.Clear();
            todayTotal.Text = todaySnapshot == null ? "—" : Exact(todaySnapshot.Total) + " token";
            todayNote.Text = todayUnavailable || (todaySnapshot != null && todaySnapshot.Partial)
                ? "北京时间 00:00 起 · 部分用量缺失" : "北京时间 00:00 起 · 本机 · 含缓存输入";
            if (showToday && expanded)
            {
                if (host == IntPtr.Zero) SizePanel(false);
                else ApplyPlacement();
            }
            if (todaySnapshot == null || todaySnapshot.Models.Count == 0)
            {
                var empty = Text(todayUnavailable ? "暂时无法读取本地用量" : todaySnapshot == null ? "正在读取今日用量…" : "今天还没有本地用量", 12, false);
                empty.Foreground = quiet;
                modelRows.Children.Add(empty);
                return;
            }
            foreach (var entry in todaySnapshot.Models.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
            {
                var row = new Grid { MinHeight = ModelRowHeight };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = Text(entry.Key == TodayCounter.UnknownModel ? "模型未知" : ModelLabel(entry.Key), 12, false);
                name.Foreground = ink;
                name.ToolTip = name.Text;
                name.Margin = new Thickness(0, 0, 8, 0);
                var value = Text(Exact(entry.Value), 12, true);
                value.Foreground = ink;
                value.Margin = new Thickness(0, 0, 4, 0);
                row.Children.Add(name);
                Put(row, value, 1);
                modelRows.Children.Add(row);
            }
        }

        private void UpdateTabStyle()
        {
            chatTab.Background = showToday ? Brushes.Transparent : hover;
            todayTab.Background = showToday ? hover : Brushes.Transparent;
            chatTab.Foreground = showToday ? quiet : ink;
            todayTab.Foreground = showToday ? ink : quiet;
            AutomationProperties.SetItemStatus(chatTab, showToday ? "" : "已选中");
            AutomationProperties.SetItemStatus(todayTab, showToday ? "已选中" : "");
        }

        private void SetView(bool today)
        {
            showToday = today;
            chatBody.Visibility = today ? Visibility.Collapsed : Visibility.Visible;
            todayBody.Visibility = today ? Visibility.Visible : Visibility.Collapsed;
            UpdateTabStyle();
            SetExpanded(expanded);
        }

        private void ResizeCacheMark() { cacheFill.Width = cacheTrack.ActualWidth * cacheFraction; }

        private async Task RefreshPlacement()
        {
            IntPtr foreground = Desktop.GetForegroundWindow();
            UpdatePlacement(foreground);
            if (!IsVisible || dragging || probing || foreground != host || host == IntPtr.Zero || DateTime.UtcNow < nextProbe) return;
            IntPtr target = host;
            probing = true;
            nextProbe = DateTime.UtcNow.AddMilliseconds(625);
            try
            {
                var result = await Task.Run(() =>
                {
                    HostView view = Desktop.Inspect(target);
                    return Tuple.Create(view, view == null ? null : titles.Match(view.Route, view.Title, view.Header));
                });
                if (host != target || closed || Desktop.GetForegroundWindow() != target) return;
                if (result.Item1 == null)
                {
                    if (++missedViews >= 2) ObserveHost(new HostView(), new FollowResult(null, "暂时无法识别前台对话"));
                    return;
                }
                missedViews = 0;
                if (!dragging)
                {
                    composer = result.Item1.Composer;
                    if (IsVisible) ApplyPlacement();
                }
                ObserveHost(result.Item1, result.Item2);
            }
            finally { probing = false; }
        }

        private void UpdatePlacement(IntPtr foreground)
        {
            if (host != IntPtr.Zero && !Desktop.IsWindow(host))
            {
                host = IntPtr.Zero;
                composer = Rect.Empty;
            }

            bool hasForeground = foreground != IntPtr.Zero;
            bool hostHasFocus = hasForeground && host != IntPtr.Zero
                && (foreground == host || Desktop.RootOwner(foreground) == host);
            bool meterHasFocus = hasForeground && !hostHasFocus
                && (foreground == handle || Desktop.SameProcess(foreground, handle));
            if (hasForeground && !hostHasFocus && !meterHasFocus && Desktop.IsCodex(foreground))
            {
                host = foreground;
                hostHasFocus = true;
                composer = Rect.Empty;
                nextProbe = DateTime.MinValue;
                observedTitle = selectedModel = followProblem = null;
                missedViews = 0;
                if (settings.Follow) ChangeSession(null);
            }
            else if (hasForeground && host != IntPtr.Zero && !hostHasFocus && !meterHasFocus)
            {
                hostHasFocus = Desktop.SameProcess(foreground, host);
            }

            // Windows can report no foreground window briefly when the drag releases mouse capture.
            bool releasingDrag = !hasForeground && IsVisible && sinceDragRelease.IsRunning
                && sinceDragRelease.ElapsedMilliseconds < DragReleaseGraceMilliseconds;
            bool hostInUse = host != IntPtr.Zero && !Desktop.IsIconic(host)
                && (hostHasFocus || meterHasFocus || releasingDrag);
            if (!hostInUse && !IsActive && !dragging && !bar.ContextMenu.IsOpen)
            {
                if (IsVisible) Hide();
                return;
            }

            if (handle == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
            ApplyPlacement();
            if (!IsVisible) Show();
        }

        private void ObserveHost(HostView view, FollowResult match)
        {
            if (!settings.Follow) return;
            bool changed = observedTitle != view.Title || selectedModel != view.SelectedModel || followProblem != match.Problem;
            observedTitle = view.Title;
            selectedModel = view.SelectedModel;
            followProblem = match.Problem;
            if (ChangeSession(match.Id)) RefreshUsageSoon();
            else if (changed) UpdateUsage();
        }

        private Desktop.Point BasePosition(out Desktop.Box area, out double scale)
        {
            IntPtr anchor = host == IntPtr.Zero ? handle : host;
            area = Desktop.WorkArea(anchor);
            scale = Desktop.Scale(anchor);
            Desktop.Box hostBox;
            if (host == IntPtr.Zero || !Desktop.GetWindowRect(host, out hostBox)) hostBox = area;
            if (hostBox.Width != lastHostBox.Width || hostBox.Height != lastHostBox.Height) composer = Rect.Empty;
            else if (!composer.IsEmpty)
            {
                composer.Offset(hostBox.Left - lastHostBox.Left, hostBox.Top - lastHostBox.Top);
            }
            lastHostBox = hostBox;

            double width = PanelWidth * scale;
            if (!composer.IsEmpty)
            {
                return new Desktop.Point { X = (int)Math.Round(composer.Right + 2 * scale - width), Y = (int)Math.Round(composer.Top - (BarHeight + 10) * scale) };
            }

            double rightPadding = Math.Max(20 * scale, hostBox.Width * .5 - 490 * scale);
            return new Desktop.Point { X = (int)Math.Round(hostBox.Right - rightPadding - width), Y = (int)Math.Round(hostBox.Bottom - 156 * scale) };
        }

        private void ApplyPlacement()
        {
            if (handle == IntPtr.Zero || dragging) return;
            Desktop.Box area;
            double scale;
            Desktop.Point anchor = BasePosition(out area, out scale);
            int x = (int)Math.Round(anchor.X + settings.OffsetX * scale);
            int y = (int)Math.Round(anchor.Y + settings.OffsetY * scale);
            PositionAt(x, y, scale, area);
        }

        private void PositionAt(int x, int y, double scale, Desktop.Box area)
        {
            int barPixels = (int)Math.Ceiling(BarHeight * scale);
            int extra = expanded ? (int)Math.Ceiling((DetailHeight + DetailGap) * scale) : 0;
            x = Clamp(x, area.Left + 6, area.Right - (int)Math.Ceiling(PanelWidth * scale) - 6);
            y = Clamp(y, area.Top + 6, area.Bottom - barPixels - 6);
            bool above = y - extra >= area.Top + 6 || y + barPixels + extra > area.Bottom - 6;
            SizePanel(above);
            int top = Clamp(above ? y - extra : y, area.Top + 6, area.Bottom - barPixels - extra - 6);
            Desktop.Box current;
            if (!Desktop.GetWindowRect(handle, out current) || current.Left != x || current.Top != top)
            {
                Desktop.SetWindowPos(handle, IntPtr.Zero, x, top, 0, 0, Desktop.PositionWithoutActivation);
            }
        }

        private static int Clamp(int value, int low, int high) { return Math.Max(low, Math.Min(value, high)); }

        private double DetailHeight
        {
            get
            {
                if (!showToday) return ChatHeight;
                int rows = todaySnapshot == null ? 1 : Math.Max(1, todaySnapshot.Models.Count);
                return TodayFixedHeight + Math.Min(ModelListMaxHeight, rows * ModelRowHeight);
            }
        }

        private void SizePanel(bool above)
        {
            detail.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            double detailSize = expanded ? DetailHeight : 0;
            double gap = expanded ? DetailGap : 0;
            frame.RowDefinitions[0].Height = new GridLength(above ? detailSize : BarHeight);
            frame.RowDefinitions[1].Height = new GridLength(gap);
            frame.RowDefinitions[2].Height = new GridLength(above ? BarHeight : detailSize);
            Grid.SetRow(detail, above ? 0 : 2);
            Grid.SetRow(bar, above ? 2 : 0);
            Height = BarHeight + gap + detailSize;
        }

        internal void SetExpanded(bool value)
        {
            expanded = value;
            toggle.Content = expanded ? "明细  ⌃" : "明细  ⌄";
            AutomationProperties.SetName(toggle, expanded ? "收起用量明细" : "展开用量明细");
            if (host == IntPtr.Zero) SizePanel(false);
            else ApplyPlacement();
            if (expanded && showToday) RefreshTodaySoon();
        }

        private void BeginDrag(object sender, DragStartedEventArgs e)
        {
            Desktop.Point cursor;
            Desktop.Box window;
            if (!Desktop.GetCursorPos(out cursor) || !Desktop.GetWindowRect(handle, out window)) return;
            dragging = true;
            dragX = cursor.X;
            dragY = cursor.Y;
            dragWindowX = window.Left;
            bool above = expanded && Grid.GetRow(bar) == 2;
            dragWindowY = window.Top + (above ? (int)Math.Ceiling((DetailHeight + DetailGap) * Desktop.Scale(handle)) : 0);
        }

        private void MoveDrag(object sender, DragDeltaEventArgs e)
        {
            Desktop.Point cursor;
            if (!dragging || !Desktop.GetCursorPos(out cursor)) return;
            IntPtr anchor = host == IntPtr.Zero ? handle : host;
            PositionAt(dragWindowX + cursor.X - dragX, dragWindowY + cursor.Y - dragY, Desktop.Scale(anchor), Desktop.WorkArea(anchor));
        }

        private void EndDrag(object sender, DragCompletedEventArgs e)
        {
            if (!dragging) return;
            Desktop.Box window, area;
            double scale;
            Desktop.Point anchor = BasePosition(out area, out scale);
            if (!e.Canceled && Desktop.GetWindowRect(handle, out window))
            {
                int barTop = window.Top + (expanded && Grid.GetRow(bar) == 2 ? (int)Math.Ceiling((DetailHeight + DetailGap) * scale) : 0);
                settings.OffsetX = (window.Left - anchor.X) / scale;
                settings.OffsetY = (barTop - anchor.Y) / scale;
                settings.Save();
            }
            sinceDragRelease.Restart();
            dragging = false;
            ApplyPlacement();
        }

        private void ResetPosition()
        {
            settings.OffsetX = settings.OffsetY = 0;
            settings.Save();
            ApplyPlacement();
        }

        private void KeyboardAccess()
        {
            if (!IsVisible) { Show(); ApplyPlacement(); }
            Activate();
            toggle.Focus();
        }

        private void HandleKeys(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && expanded) { SetExpanded(false); e.Handled = true; return; }
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            switch (e.Key)
            {
                case Key.Left: settings.OffsetX -= 12; break;
                case Key.Right: settings.OffsetX += 12; break;
                case Key.Up: settings.OffsetY -= 12; break;
                case Key.Down: settings.OffsetY += 12; break;
                default: return;
            }
            settings.Save();
            ApplyPlacement();
            e.Handled = true;
        }

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != 0x0021) return IntPtr.Zero;
            handled = true;
            return new IntPtr(3);
        }

        private void AddMenuItem(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += delegate { action(); };
            bar.ContextMenu.Items.Add(item);
        }

        private void BindClipboard()
        {
            string value = null;
            try { if (Clipboard.ContainsText()) value = Clipboard.GetText(); }
            catch (System.Runtime.InteropServices.ExternalException) { }
            string parsed = SessionBinding.ParseId(value);
            if (parsed != null) Bind(parsed);
            else ShowBinding();
        }

        private void ShowBinding()
        {
            var dialog = new Window { Title = "绑定 Codex 对话", Width = 435, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true, FontFamily = FontFamily, Background = surface, Foreground = ink };
            var content = new StackPanel { Margin = new Thickness(20) };
            content.Children.Add(Text("聊天链接或对话 ID", 16, true));
            var hint = Text("可用 Codex 的 /status 查看当前对话 ID。", 12, false);
            hint.Foreground = quiet;
            hint.Margin = new Thickness(0, 6, 0, 10);
            content.Children.Add(hint);
            var value = new TextBox { Text = sessionId ?? settings.SessionId ?? "", FontSize = 13, Padding = new Thickness(7), MaxLength = 1024 };
            AutomationProperties.SetName(value, "聊天链接或对话 ID");
            content.Children.Add(value);
            var feedback = Text(" ", 12, false);
            feedback.Foreground = quiet;
            feedback.Margin = new Thickness(0, 5, 0, 5);
            content.Children.Add(feedback);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "取消", MinWidth = 62, IsCancel = true, Margin = new Thickness(0, 0, 7, 0), Padding = new Thickness(8, 4, 8, 4) };
            var confirm = new Button { Content = "绑定", MinWidth = 62, IsDefault = true, Padding = new Thickness(8, 4, 8, 4) };
            confirm.Click += delegate
            {
                string parsed = SessionBinding.ParseId(value.Text);
                if (parsed == null) { feedback.Text = "请输入有效的聊天链接或对话 ID。"; value.Focus(); return; }
                Bind(parsed);
                dialog.DialogResult = true;
            };
            actions.Children.Add(cancel);
            actions.Children.Add(confirm);
            content.Children.Add(actions);
            dialog.Content = content;
            dialog.Loaded += delegate { value.Focus(); value.SelectAll(); };
            dialog.ShowDialog();
        }

        private void Bind(string id)
        {
            settings.Follow = false;
            settings.SessionId = id;
            followProblem = null;
            settings.Save();
            ChangeSession(id);
            UpdateUsage();
            RefreshUsageSoon();
        }

        private bool ChangeSession(string id)
        {
            if (sessionId == id) return false;
            sessionId = id;
            session = id == null ? null : new SessionBinding(codexHome, id);
            snapshot = null;
            readError = null;
            UpdateUsage();
            return true;
        }

        private void EnableFollow()
        {
            if (settings.Follow) return;
            settings.Follow = true;
            settings.Save();
            observedTitle = selectedModel = followProblem = null;
            ChangeSession(null);
            UpdateUsage();
            nextProbe = DateTime.MinValue;
        }

        private async void RefreshUsageSoon() { await RefreshUsage(); }
        private async void RefreshTodaySoon() { await RefreshToday(); }

        private void Cleanup(object sender, EventArgs e)
        {
            closed = true;
            SystemEvents.UserPreferenceChanged -= OnThemeChanged;
            placementTimer.Stop();
            usageTimer.Stop();
            todayTimer.Stop();
            tray.ContextMenuStrip.Dispose();
            tray.Dispose();
        }

        internal void PreviewUsage(UsageSnapshot example)
        {
            session = new SessionBinding(codexHome, "00000000-0000-0000-0000-000000000000");
            snapshot = example;
            UpdateUsage();
        }

        internal void PreviewToday(TodaySummary example)
        {
            todaySnapshot = example;
            SetView(true);
            UpdateToday();
        }
    }
}
