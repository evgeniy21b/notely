using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Notely.Core;
using Forms = System.Windows.Forms;

namespace Notely.Windows;

internal sealed class MainWindow : Window
{
    private readonly Settings settings = Settings.Load();
    private readonly TextBlock status = new() { Text = "запуск…", Foreground = Brushes.LightGray, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock audioStatus = new() { Foreground = Brushes.LightGray, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock audioWarning = new() { Foreground = Brushes.Orange, FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Ellipse themDot = Dot(), meDot = Dot();
    private readonly RichTextBox answer = new() { IsReadOnly = true, IsDocumentEnabled = true, Background = Brushes.Transparent,
        BorderThickness = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox input = new() { FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 20000 };
    private readonly Image thumbnail = new() { Width = 50, Height = 28, Margin = new Thickness(0, 0, 6, 0), Visibility = Visibility.Collapsed, Stretch = Stretch.Uniform };
    private readonly Border settingsPanel = new() { Visibility = Visibility.Collapsed };
    private readonly PasswordBox deepgramKey = new(), openrouterKey = new();
    private readonly ComboBox outputDevice = new(), inputDevice = new();
    private readonly TextBlock settingsMessage = new() { Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap };
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim restartGate = new(1);
    private Forms.NotifyIcon? tray;
    private NativeWindow? native;
    private Brain? brain;
    private ListeningSession? listening;
    private ApiKeys? keys;
    private BitmapSource? attachment;
    private byte[]? attachmentJpeg;
    private string shown = "", prefix = "";
    private bool closing, busyScreenshot;
    private readonly bool smoke;
    private float themLevel, meLevel;
    private readonly DispatcherTimer levels = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public MainWindow(bool smoke = false)
    {
        this.smoke = smoke;
        Title = "Notely"; Width = settings.Width; Height = settings.Height;
        MinWidth = 420; MinHeight = 180; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = new SolidColorBrush(Color.FromRgb(26, 29, 35)); Foreground = Brushes.WhiteSmoke;
        FontFamily = new FontFamily("Segoe UI");
        Build(); RestorePosition();
        SourceInitialized += (_, _) => InitializeNative();
        IsVisibleChanged += (_, _) => { if (IsVisible) native?.Protect(); };
        Loaded += async (_, _) =>
        {
            if (smoke) return;
            keys = Secrets.Load();
            if (keys is null) { OpenSettings(); Report("Введите ключи Deepgram и OpenRouter в настройках"); }
            else await RestartAsync(true);
        };
        PreviewKeyDown += OnKey;
        PreviewMouseLeftButtonDown += (_, _) => { if (settingsPanel.Visibility == Visibility.Visible) Activate(); };
        Closing += OnClosing;
        levels.Tick += (_, _) =>
        {
            themDot.Fill = themLevel > 0.08f ? Brushes.LimeGreen : Brushes.Gray;
            meDot.Fill = meLevel > 0.08f ? Brushes.LimeGreen : Brushes.Gray;
            themLevel = meLevel = 0;
        };
        levels.Start();
        SetText("Слушаю созвон. Подсказки появляются после вопросов собеседника.\nCtrl+Alt+Space — скрыть/показать · Ctrl+Alt+I — написать вопрос.");
    }

    private static Ellipse Dot() => new() { Width = 8, Height = 8, Fill = Brushes.Gray, Margin = new Thickness(3) };
    private Button Button(string title, Action action)
    {
        var button = new Button { Content = title, Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(2, 0, 2, 0), Focusable = false };
        button.Click += (_, _) => action(); return button;
    }
    private void Build()
    {
        var root = new DockPanel { Margin = new Thickness(10) }; Content = root;
        var top = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 5) };
        top.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is TextBlock or DockPanel) { try { DragMove(); } catch (InvalidOperationException) { } } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Button("⋯", ShowMenu));
        buttons.Children.Add(Button("—", Hide));
        buttons.Children.Add(Button("×", Close));
        DockPanel.SetDock(buttons, Dock.Right); top.Children.Add(buttons);
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 5, 0) };
        dots.Children.Add(themDot); dots.Children.Add(meDot);
        DockPanel.SetDock(dots, Dock.Left); top.Children.Add(dots); top.Children.Add(status);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);

        var bottom = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 5, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Button("Скрин", () => Run(() => ScreenshotAsync(false))));
        actions.Children.Add(Button("Отправить", Send));
        DockPanel.SetDock(actions, Dock.Right); bottom.Children.Add(actions);
        DockPanel.SetDock(thumbnail, Dock.Left); bottom.Children.Add(thumbnail); bottom.Children.Add(input);
        input.PreviewMouseLeftButtonDown += (_, _) => { Activate(); input.Focus(); };
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        DockPanel.SetDock(audioWarning, Dock.Bottom); root.Children.Add(audioWarning);

        settingsPanel.Child = BuildSettings();
        DockPanel.SetDock(settingsPanel, Dock.Top); root.Children.Add(settingsPanel);
        root.Children.Add(answer);
    }

    private UIElement BuildSettings()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        void Label(string text) => stack.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 2), Foreground = Brushes.WhiteSmoke });
        Label("Ключ Deepgram (пусто — сохранить прежний)"); stack.Children.Add(deepgramKey);
        Label("Ключ OpenRouter (пусто — сохранить прежний)"); stack.Children.Add(openrouterKey);
        Label("Звук собеседника: выберите тот же выход, что и в Zoom / Meet / Telemost"); stack.Children.Add(outputDevice);
        Label("Микрофон"); stack.Children.Add(inputDevice);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 6) };
        buttons.Children.Add(Button("Сохранить и начать разговор", () => Run(SaveSettingsAsync)));
        buttons.Children.Add(Button("Закрыть", () => settingsPanel.Visibility = Visibility.Collapsed)); stack.Children.Add(buttons);
        stack.Children.Add(settingsMessage); stack.Children.Add(audioStatus);
        return new ScrollViewer { Content = stack, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void RestorePosition()
    {
        var work = SystemParameters.WorkArea;
        Left = settings.Left >= 0 ? settings.Left : work.Left + Math.Max(0, (work.Width - Width) / 2);
        Top = settings.Top >= 0 ? settings.Top : work.Top + 8;
        // Restore negative-coordinate monitors too, and recover if a monitor was removed.
        if (settings.Left < -1) Left = settings.Left;
        if (settings.Top < -1) Top = settings.Top;
        var visible = Forms.Screen.AllScreens.Any(s =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var x = Left * dpi.DpiScaleX; var y = Top * dpi.DpiScaleY;
            return x + 100 > s.WorkingArea.Left && x < s.WorkingArea.Right - 50 && y + 50 > s.WorkingArea.Top && y < s.WorkingArea.Bottom - 30;
        });
        if (!visible) { Left = work.Left + 8; Top = work.Top + 8; }
    }

    private void InitializeNative()
    {
        native = new NativeWindow(this);
        if (smoke) return;
        native.Add(Key.Space, () => { if (IsVisible) Hide(); else ShowOverlay(); });
        native.Add(Key.I, FocusInput);
        native.Add(Key.S, () => Run(() => ScreenshotAsync(true)));
        native.Add(Key.P, TogglePause);
        native.Add(Key.Up, () => Scroll(-60)); native.Add(Key.Down, () => Scroll(60));
        native.Add(Key.Left, () => { Width = Math.Max(MinWidth, Width - 80); });
        native.Add(Key.Right, () =>
        {
            var screen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            Width = Math.Max(MinWidth, Math.Min(screen.WorkingArea.Width / VisualTreeHelper.GetDpi(this).DpiScaleX - 20, Width + 80));
        });
        native.Add(Key.OemPlus, () => ChangeFont(1)); native.Add(Key.OemMinus, () => ChangeFont(-1));
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Notely", Visible = true };
        tray.MouseClick += (_, e) => Dispatcher.Invoke(() => { if (e.Button == Forms.MouseButtons.Left) ShowOverlay(); else ShowMenu(); });
        if (native.HotkeyErrors.Count > 0) Report(string.Join("; ", native.HotkeyErrors));
        if (!native.CaptureExcluded) Report(native.ProtectionStatus);
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu();
        void Add(string title, Action action)
        { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("Показать / скрыть · Ctrl+Alt+Space", () => { if (IsVisible) Hide(); else ShowOverlay(); });
        Add(brain?.Paused == true ? "Продолжить · Ctrl+Alt+P" : "Пауза · Ctrl+Alt+P", TogglePause);
        Add("Новый разговор", () => Run(() => RestartAsync(true)));
        var models = new MenuItem { Header = "Модель" };
        foreach (var model in Settings.Models)
        {
            var item = new MenuItem { Header = model.Title, IsCheckable = true, IsChecked = settings.Model == model.Id };
            item.Click += (_, _) => { settings.Model = model.Id; SaveGeometry(); RefreshStatus(); }; models.Items.Add(item);
        }
        menu.Items.Add(models);
        Add("Профиль (резюме, вакансия)", () => OpenText("profile.md"));
        Add("Словарь распознавания", () => OpenText("keyterms.txt"));
        Add("Настройки ключей и звука", OpenSettings);
        Add("Перезапустить звук", () => Run(() => RestartAsync(false)));
        Add("Шрифт +", () => ChangeFont(1)); Add("Шрифт −", () => ChangeFont(-1));
        Add("Проверить скрытие на снимке", () => Run(TestCaptureAsync));
        Add("Сохранить диагностику", ExportDiagnostics);
        Add("Выйти", Close);
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource source) NativeWindow.SetWindowDisplayAffinity(source.Handle, 0x11);
        };
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void OpenSettings()
    {
        ShowOverlay(); Height = Math.Max(Height, 540);
        foreach (var (box, output, selection) in new[] { (outputDevice, true, settings.OutputDevice), (inputDevice, false, settings.InputDevice) })
        {
            try
            {
                var devices = AudioService.Devices(output);
                box.ItemsSource = devices; box.DisplayMemberPath = nameof(AudioDevice.Name); box.SelectedValuePath = nameof(AudioDevice.Id);
                box.SelectedValue = devices.Any(d => d.Id == selection) ? selection : "";
            }
            catch (Exception) { box.ItemsSource = new[] { new AudioDevice("", "Нет доступных устройств") }; box.SelectedIndex = 0; }
        }
        settingsMessage.Text = (keys is null ? "Введите оба ключа." : "Ключи сохранены для текущего пользователя Windows.") + "\n" + native?.ProtectionStatus;
        settingsPanel.Visibility = Visibility.Visible;
    }
    private async Task SaveSettingsAsync()
    {
        var dg = deepgramKey.Password.Trim(); var or = openrouterKey.Password.Trim();
        if (dg.Length == 0) dg = keys?.Deepgram ?? "";
        if (or.Length == 0) or = keys?.OpenRouter ?? "";
        if (dg.Length == 0 || or.Length == 0 || dg.Contains('\n') || or.Contains('\n'))
        { settingsMessage.Text = "Нужны оба API-ключа одной строкой."; return; }
        var next = new ApiKeys(dg, or); Secrets.Save(next); keys = next;
        deepgramKey.Clear(); openrouterKey.Clear();
        settings.OutputDevice = outputDevice.SelectedValue as string ?? "";
        settings.InputDevice = inputDevice.SelectedValue as string ?? "";
        SaveGeometry(); settingsPanel.Visibility = Visibility.Collapsed;
        await RestartAsync(true);
    }

    private async Task RestartAsync(bool clear)
    {
        await restartGate.WaitAsync();
        try
        {
            if (closing) return;
            if (clear) { brain?.Reset(); SetText("Контекст очищен. Слушаю."); }
            status.Text = "перезапускаю звук…";
            if (listening is not null) { var old = listening; listening = null; await old.DisposeAsync(); }
            if (closing || keys is null) return;
            if (brain is null || clear)
            {
                brain?.Dispose();
                brain = new Brain(new OpenRouterClient(http, keys.OpenRouter), Paths.Profile, () => settings.Model);
                var owner = brain;
                brain.Changed += change => Dispatcher.BeginInvoke(() => { if (brain == owner && owner.Session == change.Session && !closing) Handle(change); });
            }
            var session = new ListeningSession(keys, settings, brain);
            listening = session;
            session.Level += (output, value) => Dispatcher.BeginInvoke(() =>
            { if (listening == session && !closing) { if (output) themLevel = Math.Max(themLevel, value); else meLevel = Math.Max(meLevel, value); } });
            session.Status += text => Dispatcher.BeginInvoke(() =>
            {
                if (listening != session || closing) return;
                audioStatus.Text = text;
                var warnings = text.Split('\n').Where(s => s.Contains("недоступно") || s.Contains("нет связи"));
                audioWarning.Text = string.Join(" · ", warnings);
                audioWarning.Visibility = audioWarning.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            });
            session.Start(); RefreshStatus();
        }
        finally { restartGate.Release(); }
    }

    private void Handle(BrainEvent change)
    {
        switch (change.Kind)
        {
            case BrainEventKind.Thinking: status.Text = "думаю…"; break;
            case BrainEventKind.Answer:
                if (change.StartsNew) prefix = shown.Length > 0 ? shown + "\n\n" : "";
                shown = prefix + change.Text; Render(change.StartsNew); status.Text = "отвечаю…"; break;
            case BrainEventKind.Error: Report(change.Text); break;
            default: RefreshStatus(); break;
        }
    }
    private void RefreshStatus() => status.Text = brain?.Paused == true ? "пауза" : "слушаю · " + settings.Model.Split('/').Last();
    public void Report(string message) { status.Text = message; status.ToolTip = message; }
    private void SetText(string text) { shown = ""; prefix = ""; answer.Document = Markdown.Render(text, settings.FontSize); }
    internal void SmokeRender(string text) { shown = text; Render(true); }
    private void Render(bool follow)
    {
        var viewer = FindScroll(answer);
        var offset = viewer?.VerticalOffset ?? 0;
        var nearBottom = viewer is null || offset >= viewer.ScrollableHeight - 24;
        answer.Document = Markdown.Render(shown, settings.FontSize);
        answer.UpdateLayout();
        viewer = FindScroll(answer);
        if (follow || nearBottom) viewer?.ScrollToEnd(); else viewer?.ScrollToVerticalOffset(offset);
    }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer found) return found;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var result = FindScroll(VisualTreeHelper.GetChild(parent, i)); if (result is not null) return result; }
        return null;
    }
    private void Scroll(double delta) { var scroll = FindScroll(answer); scroll?.ScrollToVerticalOffset(scroll.VerticalOffset + delta); }
    private void ChangeFont(double delta) { settings.FontSize = Math.Clamp(settings.FontSize + delta, 11, 32); if (shown.Length > 0) Render(false); SaveGeometry(); }
    private void ShowOverlay() { Show(); native?.Protect(); Topmost = true; }
    private void FocusInput() { ShowOverlay(); Activate(); input.Focus(); Keyboard.Focus(input); }
    private void TogglePause() { if (brain is not null) { brain.Paused = !brain.Paused; RefreshStatus(); } }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V && settingsPanel.Visibility != Visibility.Visible)
        {
            try { if (!Clipboard.ContainsText() && Clipboard.ContainsImage()) { Attach(Clipboard.GetImage()); e.Handled = true; } }
            catch (Exception) { Report("Буфер обмена занят — повторите вставку"); }
        }
        else if (e.Key == Key.Enter && input.IsKeyboardFocusWithin) { Send(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        { if (attachment is not null) ClearImage(); else { input.Clear(); Keyboard.ClearFocus(); } e.Handled = true; }
    }
    private void Attach(BitmapSource? image)
    {
        if (image is null) return;
        // Normalize immediately so even clipboard images have a bounded payload.
        attachmentJpeg = Images.Jpeg(image);
        using var stream = new MemoryStream(attachmentJpeg);
        var normalized = new BitmapImage(); normalized.BeginInit(); normalized.CacheOption = BitmapCacheOption.OnLoad;
        normalized.StreamSource = stream; normalized.EndInit(); normalized.Freeze();
        attachment = normalized; thumbnail.Source = normalized; thumbnail.Visibility = Visibility.Visible; FocusInput();
    }
    private void ClearImage() { attachment = null; attachmentJpeg = null; thumbnail.Source = null; thumbnail.Visibility = Visibility.Collapsed; }
    private void Send()
    {
        if (brain is null) { OpenSettings(); return; }
        try
        {
            brain.Ask(input.Text, attachmentJpeg);
            input.Clear(); ClearImage();
        }
        catch (Exception e) { Report(e.Message); }
    }
    private async Task ScreenshotAsync(bool send)
    {
        if (busyScreenshot) return;
        busyScreenshot = true;
        var visible = IsVisible;
        try
        {
            Hide(); await Task.Delay(160);
            if (closing) return;
            var image = Images.Capture();
            ShowOverlay();
            if (send)
            {
                if (brain is null) OpenSettings();
                else brain.Ask("", Images.Jpeg(image));
            }
            else Attach(image);
        }
        finally { if (visible && !closing) ShowOverlay(); busyScreenshot = false; }
    }
    private async Task TestCaptureAsync()
    {
        ShowOverlay(); native?.Protect();
        Report("Проверка: окно Notely должно отсутствовать на снимке");
        await Task.Delay(400);
        if (closing) return;
        // Do not hide the overlay here: this is a real diagnostic, not the normal screenshot path.
        var path = Images.SaveCapture(Images.Capture()); Paths.Open(path);
        Report("Открыл снимок. Отсутствие окна здесь не гарантирует скрытие в Zoom/Meet — проверьте со вторым участником.");
    }
    private void OpenText(string name)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = true };
            start.ArgumentList.Add(Paths.File(name)); System.Diagnostics.Process.Start(start);
        }
        catch (Exception) { Report("Не удалось открыть " + name); }
    }
    private void ExportDiagnostics()
    {
        try
        {
            var report = new { Version = "0.1.0", Windows = Environment.OSVersion.VersionString,
                Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                CaptureExclusion = native?.ProtectionStatus, Hotkeys = native?.HotkeyErrors,
                Audio = audioStatus.Text, HasKeys = keys is not null };
            Paths.WriteAtomic("diagnostics.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Paths.Open(Paths.DirectoryPath); Report("diagnostics.json и notely.log сохранены. Ключей и расшифровок в них нет.");
        }
        catch (Exception) { Report("Не удалось сохранить диагностику"); }
    }
    private void SaveGeometry()
    {
        settings.Width = Width; settings.Height = Height; settings.Left = Left; settings.Top = Top;
        try { settings.Save(); } catch (IOException) { Report("Не удалось сохранить настройки"); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (smoke) { levels.Stop(); native?.Dispose(); http.Dispose(); return; }
        e.Cancel = true;
        if (closing) return;
        closing = true; levels.Stop(); Hide(); SaveGeometry(); brain?.Dispose();
        tray?.Dispose(); native?.Dispose();
        await restartGate.WaitAsync();
        try { if (listening is not null) await listening.DisposeAsync(); }
        finally { restartGate.Release(); http.Dispose(); Closing -= OnClosing; Application.Current.Shutdown(); }
    }
    private async void Run(Func<Task> action)
    { try { await action(); } catch (Exception e) { Log.Write("action", e.GetType().Name); if (!closing) Report("Не удалось выполнить действие: " + e.Message); } }
}
