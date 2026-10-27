using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace MadokaDesktop
{
    static class Json
    {
        public static JavaScriptSerializer Codec { get { return new JavaScriptSerializer { MaxJsonLength = 4194304 }; } }
        public static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object>; }
        public static object Get(object value, string key)
        {
            var map = Map(value);
            object found;
            return map != null && map.TryGetValue(key, out found) ? found : null;
        }
        public static string Str(object value) { return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture); }
        public static double? Number(object value)
        {
            double number;
            return value != null && double.TryParse(Str(value), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                && !double.IsNaN(number) && !double.IsInfinity(number) ? (double?)number : null;
        }
        public static object Read(string value) { return Codec.DeserializeObject(value); }
    }

    sealed class QuotaWindow
    {
        public string Name;
        public double Used;
        public double Remaining { get { return Math.Max(0, 100 - Used); } }
        public long? Reset;
        public string ResetLabel
        {
            get
            {
                if (!Reset.HasValue) return "恢复时间暂未提供";
                try
                {
                    var local = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Reset.Value).ToLocalTime();
                    return local <= DateTime.Now ? "已到恢复时间 · 请刷新确认" : "恢复于 " + local.ToString("MM-dd HH:mm");
                }
                catch { return "恢复时间暂未提供"; }
            }
        }
    }

    sealed class UsageSnapshot
    {
        public readonly List<QuotaWindow> Windows = new List<QuotaWindow>();
        public DateTime FetchedAt;
        public string Plan = "";
        public string CreditBalance = "";
        public bool UnlimitedCredits;
        public double MinimumRemaining { get { return Windows.Count == 0 ? 100 : Windows.Min(w => w.Remaining); } }
        public bool Stale
        {
            get
            {
                if (DateTime.UtcNow - FetchedAt.ToUniversalTime() > TimeSpan.FromMinutes(3)) return true;
                var now = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                return Windows.Any(w => w.Reset.HasValue && w.Reset.Value <= now);
            }
        }
        public static UsageSnapshot Parse(object result)
        {
            var snapshot = new UsageSnapshot { FetchedAt = DateTime.Now };
            var byId = Json.Map(Json.Get(result, "rateLimitsByLimitId"));
            if (byId != null && byId.Count > 0)
            {
                foreach (var entry in byId.OrderBy(e => e.Key == "codex" ? "" : e.Key))
                    snapshot.AddBucket(entry.Value, entry.Key, byId.Count > 1);
            }
            else snapshot.AddBucket(Json.Get(result, "rateLimits"), "codex", false);
            return snapshot;
        }
        void AddBucket(object bucket, string id, bool multiple)
        {
            if (bucket == null) return;
            var name = Json.Str(Json.Get(bucket, "limitName"));
            if (name.Length == 0) name = id == "codex" ? "Codex" : id;
            if (Plan.Length == 0) Plan = Json.Str(Json.Get(bucket, "planType"));
            var credits = Json.Get(bucket, "credits");
            if (credits != null && CreditBalance.Length == 0)
            {
                CreditBalance = Json.Str(Json.Get(credits, "balance"));
                UnlimitedCredits = object.Equals(Json.Get(credits, "unlimited"), true);
            }
            foreach (var slot in new[] { "primary", "secondary" })
            {
                var raw = Json.Get(bucket, slot);
                var used = Json.Number(Json.Get(raw, "usedPercent"));
                if (!used.HasValue || used.Value < 0) continue;
                var duration = Json.Number(Json.Get(raw, "windowDurationMins"));
                var label = duration.HasValue && duration > 0 ? Duration(duration.Value) : (slot == "primary" ? "主要额度" : "次要额度");
                var reset = Json.Number(Json.Get(raw, "resetsAt"));
                Windows.Add(new QuotaWindow { Name = (multiple ? name + " · " : "") + label,
                    Used = used.Value, Reset = reset.HasValue && reset.Value > 0 && reset.Value < 253402300799 ? (long?)reset.Value : null });
            }
        }
        public static string Duration(double minutes)
        {
            if (minutes == 10080) return "每周额度";
            if (minutes == 43200) return "30 天额度";
            if (minutes >= 1440 && minutes % 1440 == 0) return (minutes / 1440).ToString("0.#") + " 天额度";
            if (minutes >= 60 && minutes % 60 == 0) return (minutes / 60).ToString("0.#") + " 小时额度";
            return minutes.ToString("0.#") + " 分钟额度";
        }
    }

    sealed class UsageUnavailable : Exception
    {
        public UsageUnavailable(string message) : base(message) { }
    }

    sealed class UsageClient
    {
        readonly string explicitPath;
        readonly object processLock = new object();
        Process active;
        public UsageClient(string path = null) { explicitPath = path; }
        public static string LocateCodex()
        {
            var custom = Environment.GetEnvironmentVariable("MADOKA_CODEX_PATH");
            if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom) && Path.GetExtension(custom).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return custom;
            var candidates = new List<string>();
            foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
                foreach (var item in (Environment.GetEnvironmentVariable("PATH", scope) ?? "").Split(';'))
                {
                    try { candidates.Add(Path.Combine(Environment.ExpandEnvironmentVariables(item.Trim(' ', '"')), "codex.exe")); } catch { }
                }
            var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(bundled))
                candidates.AddRange(Directory.GetDirectories(bundled).OrderByDescending(Directory.GetLastWriteTimeUtc).Select(p => Path.Combine(p, "codex.exe")));
            var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex", "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe");
            candidates.Add(npm);
            return candidates.FirstOrDefault(File.Exists);
        }
        public void Stop()
        {
            lock (processLock)
            {
                try { if (active != null && !active.HasExited) active.Kill(); } catch { }
            }
        }
        public async Task<UsageSnapshot> ReadAsync()
        {
            var path = explicitPath ?? LocateCodex();
            if (path == null) throw new UsageUnavailable("没有找到本机 Codex。请安装并登录 Codex；也可设置 MADOKA_CODEX_PATH 指向 codex.exe。");
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(path, "app-server --stdio") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory };
                try
                {
                    process.Start();
                    lock (processLock) active = process;
                    // Drain without storing or printing logs, which could contain account information.
                    process.ErrorDataReceived += delegate { };
                    process.BeginErrorReadLine();
                    var deadline = DateTime.UtcNow.AddSeconds(25);
                    await Send(process, new { id = 1, method = "initialize", @params = new {
                        clientInfo = new { name = "madoka_desktop_pet", title = "Madoka Desktop Pet", version = "1.0.0" } } }).ConfigureAwait(false);
                    await Response(process, 1, deadline).ConfigureAwait(false);
                    await Send(process, new { method = "initialized", @params = new { } }).ConfigureAwait(false);
                    await Send(process, new { id = 2, method = "account/rateLimits/read" }).ConfigureAwait(false);
                    return UsageSnapshot.Parse(await Response(process, 2, deadline).ConfigureAwait(false));
                }
                catch (UsageUnavailable) { throw; }
                catch { throw new UsageUnavailable("暂时无法连接 Codex。请确认 Codex 已登录、网络正常，再点刷新。"); }
                finally
                {
                    lock (processLock) if (ReferenceEquals(active, process)) active = null;
                    try { process.StandardInput.Close(); } catch { }
                    try { if (!process.HasExited) { process.Kill(); process.WaitForExit(2000); } } catch { }
                }
            }
        }
        static async Task Send(Process process, object message)
        {
            await process.StandardInput.WriteLineAsync(Json.Codec.Serialize(message)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        static async Task<object> Response(Process process, int id, DateTime deadline)
        {
            while (true)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) throw new UsageUnavailable("读取额度超时。请检查网络后再刷新。");
                var read = process.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(read, Task.Delay(left)).ConfigureAwait(false) != read)
                    throw new UsageUnavailable("读取额度超时。请检查网络后再刷新。");
                var line = await read.ConfigureAwait(false);
                if (line == null) throw new UsageUnavailable("Codex 连接已结束。请确认 Codex 能正常启动并已登录。");
                object message;
                try { message = Json.Read(line); } catch { continue; }
                var responseId = Json.Get(message, "id");
                if (Json.Str(responseId) == id.ToString(CultureInfo.InvariantCulture))
                {
                    if (Json.Get(message, "error") != null)
                        throw new UsageUnavailable("Codex 尚未返回额度。请确认已用 ChatGPT 账号登录，API Key 登录不提供订阅额度。");
                    return Json.Get(message, "result");
                }
                // No auth refresh or permission action is ever performed by this read-only client.
                if (responseId != null && Json.Get(message, "method") != null)
                    await Send(process, new { id = responseId, error = new { code = -32601, message = "Read-only client does not support server requests" } }).ConfigureAwait(false);
            }
        }
    }

    sealed class DialogueBook
    {
        readonly Dictionary<string, object> data;
        readonly Random random = new Random();
        string last = "";
        public DialogueBook()
        {
            using (var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("dialogues.json"), Encoding.UTF8))
                data = Json.Map(Json.Read(reader.ReadToEnd()));
        }
        public string Next(string category)
        {
            object values;
            if (!data.TryGetValue(category, out values)) values = data["idle"];
            var lines = ((IEnumerable)values).Cast<object>().Select(Json.Str).Where(s => s != last).ToArray();
            if (lines.Length == 0) lines = ((IEnumerable)values).Cast<object>().Select(Json.Str).ToArray();
            return last = lines[random.Next(lines.Length)];
        }
        public double NextDelay() { return random.Next(55, 111); }
        public int Count { get { return data.Where(e => e.Value is object[]).Sum(e => ((object[])e.Value).Length); } }
    }

    sealed class PetState
    {
        public double Left = double.NaN;
        public double Top = double.NaN;
        public double Scale = 1;
        public bool Paused;
        public bool Topmost = true;
        static string StatePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "state.json"); } }
        public static PetState Load()
        {
            var state = new PetState();
            try
            {
                var data = Json.Read(File.ReadAllText(StatePath, Encoding.UTF8));
                state.Left = Json.Number(Json.Get(data, "left")) ?? double.NaN;
                state.Top = Json.Number(Json.Get(data, "top")) ?? double.NaN;
                state.Scale = Math.Max(.7, Math.Min(1.5, Json.Number(Json.Get(data, "scale")) ?? 1));
                state.Paused = object.Equals(Json.Get(data, "paused"), true);
                state.Topmost = !object.Equals(Json.Get(data, "topmost"), false);
            }
            catch { }
            return state;
        }
        public void Save()
        {
            try { File.WriteAllText(StatePath, Json.Codec.Serialize(new { left = Left, top = Top, scale = Scale, paused = Paused, topmost = Topmost }), new UTF8Encoding(false)); } catch { }
        }
    }

    sealed class PetWindow : Window
    {
        readonly DialogueBook dialogues = new DialogueBook();
        readonly UsageClient client = new UsageClient();
        readonly PetState state = PetState.Load();
        readonly DispatcherTimer talkTimer = new DispatcherTimer();
        readonly DispatcherTimer hideTimer = new DispatcherTimer();
        readonly DispatcherTimer refreshTimer = new DispatcherTimer();
        readonly Image figure = new Image();
        readonly Border bubble = new Border();
        readonly TextBlock bubbleText = new TextBlock();
        readonly TranslateTransform breathing = new TranslateTransform();
        readonly Forms.NotifyIcon tray = new Forms.NotifyIcon();
        readonly ScaleTransform scaleTransform = new ScaleTransform(1, 1);
        Window panel;
        StackPanel panelCards;
        TextBlock panelStatus;
        TextBlock panelLine;
        Button refreshButton;
        UsageSnapshot snapshot;
        string fetchError;
        bool fetching;
        bool closed;
        bool dragged;
        bool preview;
        Point press;
        public static readonly Brush Ink = Brush("#392D38");
        public static readonly Brush Muted = Brush("#89747E");
        public static readonly Brush Wine = Brush("#8F2E4E");
        public static Brush Brush(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }
        static TextBlock Text(string text, double size, Brush color)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.5 };
        }
        public PetWindow(bool forPreview = false, string smokeDirectory = null)
        {
            preview = forPreview;
            Title = "樋口円香 · 桌宠";
            Width = 290; Height = 370;
            AllowsTransparency = true; WindowStyle = WindowStyle.None; Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = state.Topmost;
            FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
            var stage = new Grid { Width = 290, Height = 370, LayoutTransform = scaleTransform };
            Content = stage;
            figure.Width = 224; figure.Height = 254;
            figure.VerticalAlignment = VerticalAlignment.Bottom; figure.HorizontalAlignment = HorizontalAlignment.Center;
            figure.Margin = new Thickness(0, 0, 0, 9);
            figure.Stretch = Stretch.Uniform;
            var bitmap = new BitmapImage();
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("madoka.png"))
            {
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            }
            figure.Source = bitmap; Icon = bitmap;
            figure.ToolTip = "单击查看额度 · 拖动换位置 · 右键打开菜单";
            figure.Cursor = Cursors.Hand; figure.RenderTransform = breathing;
            RenderOptions.SetBitmapScalingMode(figure, BitmapScalingMode.HighQuality);
            stage.Children.Add(figure);
            bubble.Background = Brush("#FFF9F7"); bubble.BorderBrush = Brush("#DFC1CB"); bubble.BorderThickness = new Thickness(1);
            bubble.CornerRadius = new CornerRadius(17, 17, 4, 17); bubble.Padding = new Thickness(16, 12, 16, 12);
            bubble.HorizontalAlignment = HorizontalAlignment.Center; bubble.VerticalAlignment = VerticalAlignment.Top;
            bubble.Width = 258; bubble.Margin = new Thickness(0, 5, 0, 0); bubble.Visibility = Visibility.Hidden;
            bubbleText.FontSize = 13; bubbleText.LineHeight = 21; bubbleText.TextWrapping = TextWrapping.Wrap; bubbleText.Foreground = Ink;
            bubble.Child = bubbleText; stage.Children.Add(bubble);
            figure.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                press = PointToScreen(e.GetPosition(this)); dragged = false; figure.CaptureMouse(); e.Handled = true;
            };
            figure.MouseMove += delegate(object sender, MouseEventArgs e) {
                if (!figure.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
                var now = PointToScreen(e.GetPosition(this));
                if (Math.Abs(now.X - press.X) + Math.Abs(now.Y - press.Y) < 5) return;
                figure.ReleaseMouseCapture(); dragged = true;
                if (panel != null) panel.Hide();
                try { DragMove(); } catch { }
                ClampToScreen(); SaveState(); Say("drag");
            };
            figure.MouseLeftButtonUp += delegate {
                figure.ReleaseMouseCapture();
                if (!dragged) TogglePanel();
            };
            bubble.MouseLeftButtonUp += delegate { bubble.Visibility = Visibility.Hidden; };
            ContextMenu = BuildMenu();
            Loaded += delegate {
                if (preview) return;
                SetScale(state.Scale); RestorePosition(); SetupTray(); StartBreathing();
                talkTimer.Interval = TimeSpan.FromSeconds(dialogues.NextDelay());
                talkTimer.Start(); refreshTimer.Start(); Say("startup");
                RefreshUsage(false);
                if (smokeDirectory != null) RunSmoke(smokeDirectory);
            };
            LocationChanged += delegate { if (panel != null && panel.IsVisible) PositionPanel(); };
            talkTimer.Tick += delegate {
                talkTimer.Interval = TimeSpan.FromSeconds(dialogues.NextDelay());
                if (!state.Paused && (panel == null || !panel.IsVisible)) Say(AmbientCategory());
            };
            hideTimer.Interval = TimeSpan.FromSeconds(9);
            hideTimer.Tick += delegate { bubble.Visibility = Visibility.Hidden; hideTimer.Stop(); };
            refreshTimer.Interval = TimeSpan.FromSeconds(60);
            refreshTimer.Tick += delegate { RefreshUsage(false); };
            Closed += delegate {
                closed = true; SaveState(); talkTimer.Stop(); hideTimer.Stop(); refreshTimer.Stop(); client.Stop();
                if (panel != null) panel.Close(); tray.Visible = false; tray.Dispose();
            };
        }
        void StartBreathing()
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            var animation = new DoubleAnimation(0, -2.5, TimeSpan.FromSeconds(2.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            breathing.BeginAnimation(TranslateTransform.YProperty, animation);
        }
        void Say(string category)
        {
            if (state.Paused) return;
            bubbleText.Text = dialogues.Next(category); bubble.Visibility = Visibility.Visible; bubble.Opacity = 0;
            bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
            hideTimer.Stop(); hideTimer.Start();
        }
        string AmbientCategory()
        {
            if (snapshot != null && fetchError == null && !snapshot.Stale)
            {
                if (snapshot.MinimumRemaining <= 0) return "empty";
                if (snapshot.MinimumRemaining <= 15) return "low";
            }
            return DateTime.Now.Hour >= 23 || DateTime.Now.Hour < 5 ? "night" : "idle";
        }
        ContextMenu BuildMenu()
        {
            var menu = new ContextMenu { FontFamily = FontFamily };
            Action<string, Action> add = (label, action) => { var item = new MenuItem { Header = label }; item.Click += delegate { action(); }; menu.Items.Add(item); };
            add("查看用量", delegate { OpenPanel(); });
            add("换一句台词", delegate { Say(AmbientCategory()); });
            var pause = new MenuItem { Header = "暂停随机台词", IsCheckable = true, IsChecked = state.Paused };
            pause.Click += delegate { SetPaused(pause.IsChecked); }; menu.Items.Add(pause);
            var pinned = new MenuItem { Header = "始终置顶", IsCheckable = true, IsChecked = state.Topmost };
            pinned.Click += delegate { Topmost = state.Topmost = pinned.IsChecked; if (panel != null) panel.Topmost = Topmost; SaveState(); }; menu.Items.Add(pinned);
            var sizes = new MenuItem { Header = "人物大小" };
            foreach (var size in new[] { .8, 1, 1.2, 1.5 })
            {
                var current = size;
                var option = new MenuItem { Header = (size * 100).ToString("0") + "%" };
                option.Click += delegate { SetScale(current); ClampToScreen(); SaveState(); }; sizes.Items.Add(option);
            }
            menu.Items.Add(sizes); add("移回屏幕右下角", delegate { DefaultPosition(); SaveState(); });
            menu.Items.Add(new Separator()); add("退出円香", delegate { Close(); });
            menu.Opened += delegate { pause.IsChecked = state.Paused; pinned.IsChecked = Topmost; };
            return menu;
        }
        void SetupTray()
        {
            tray.Icon = System.Drawing.SystemIcons.Application;
            tray.Text = "樋口円香 · 单击人物查看 Codex 额度";
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示円香", null, delegate { Show(); Activate(); ClampToScreen(); });
            menu.Items.Add("查看用量", null, delegate { OpenPanel(); });
            var pause = new Forms.ToolStripMenuItem("暂停随机台词") { CheckOnClick = true, Checked = state.Paused };
            pause.Click += delegate { SetPaused(pause.Checked); }; menu.Items.Add(pause);
            menu.Opening += delegate { pause.Checked = state.Paused; };
            menu.Items.Add("退出", null, delegate { Close(); });
            tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { OpenPanel(); }; tray.Visible = true;
        }
        void SetPaused(bool paused)
        {
            state.Paused = paused;
            if (paused) { bubble.Visibility = Visibility.Hidden; hideTimer.Stop(); }
            SaveState();
        }
        void SetScale(double scale)
        {
            state.Scale = scale; Width = 290 * scale; Height = 370 * scale; scaleTransform.ScaleX = scale; scaleTransform.ScaleY = scale;
            if (panel != null && panel.IsVisible) PositionPanel();
        }
        void RestorePosition()
        {
            if (double.IsNaN(state.Left) || double.IsNaN(state.Top)) DefaultPosition();
            else { Left = state.Left; Top = state.Top; ClampToScreen(); }
        }
        Rect CurrentWorkArea()
        {
            if (!IsLoaded) return SystemParameters.WorkArea;
            // Forms monitor bounds are physical pixels; WPF uses device-independent units.
            var source = PresentationSource.FromVisual(this);
            if (source == null || source.CompositionTarget == null) return SystemParameters.WorkArea;
            var at = PointToScreen(new Point(Width / 2, Height / 2));
            var bounds = Forms.Screen.FromPoint(new System.Drawing.Point((int)at.X, (int)at.Y)).WorkingArea;
            var transform = source.CompositionTarget.TransformFromDevice;
            var origin = transform.Transform(new Point(bounds.Left, bounds.Top));
            var extent = transform.Transform(new Point(bounds.Right, bounds.Bottom));
            return new Rect(origin, extent);
        }
        void DefaultPosition()
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 28; Top = area.Bottom - Height - 16;
        }
        void ClampToScreen()
        {
            var area = CurrentWorkArea();
            Left = Math.Max(area.Left, Math.Min(area.Right - Width, Left));
            Top = Math.Max(area.Top, Math.Min(area.Bottom - Height, Top));
        }
        void SaveState() { state.Left = Left; state.Top = Top; state.Save(); }
        void TogglePanel() { if (panel != null && panel.IsVisible) panel.Hide(); else OpenPanel(); }
        void OpenPanel()
        {
            if (panel == null) CreatePanel();
            bubble.Visibility = Visibility.Hidden;
            panelLine.Text = dialogues.Next(fetchError == null ? "click" : "error");
            RenderUsage(); PositionPanel(); panel.Show(); panel.Activate();
            if (!fetching && (snapshot == null || fetchError != null || DateTime.Now - snapshot.FetchedAt > TimeSpan.FromSeconds(15))) RefreshUsage(false);
        }
        Button MakeButton(string label, bool primary = false)
        {
            return new Button { Content = label, Padding = new Thickness(12, 7, 12, 7), Cursor = Cursors.Hand,
                FontSize = 12, Foreground = primary ? Brushes.White : Wine, Background = primary ? Wine : Brush("#F7EAF0"),
                BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 8, 0) };
        }
        void CreatePanel()
        {
            panel = new Window { Title = "円香 · Codex 用量", Width = 354, SizeToContent = SizeToContent.Height,
                MaxHeight = Math.Max(320, CurrentWorkArea().Height - 24), WindowStyle = WindowStyle.None,
                AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, Topmost = Topmost, FontFamily = FontFamily };
            if (!preview) panel.Owner = this;
            var shell = new Border { Background = Brush("#FFFAF7"), BorderBrush = Brush("#DAC2C8"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(20), Padding = new Thickness(21, 18, 21, 18) };
            panel.Content = shell;
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(280, CurrentWorkArea().Height - 62) };
            shell.Child = scroll;
            var stack = new StackPanel(); scroll.Content = stack;
            var header = new DockPanel();
            var close = MakeButton("×"); close.FontSize = 18; close.Padding = new Thickness(8, 0, 8, 0); close.Margin = new Thickness(0);
            close.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(close, Dock.Right); close.Click += delegate { panel.Hide(); }; header.Children.Add(close);
            var heading = new StackPanel();
            var eyebrow = Text("CODEX  /  WORK", 10, Wine); eyebrow.FontWeight = FontWeights.SemiBold; heading.Children.Add(eyebrow);
            var title = Text("樋口円香", 23, Ink); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 3, 0, 0); heading.Children.Add(title);
            header.Children.Add(heading); stack.Children.Add(header);
            var speech = new Border { Background = Brush("#F5E8EB"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 14, 0, 14) };
            panelLine = Text("额度在这里。看完就去做事吧。", 12, Ink); speech.Child = panelLine; stack.Children.Add(speech);
            panelCards = new StackPanel(); stack.Children.Add(panelCards);
            panelStatus = Text("正在读取额度…", 10.5, Muted); panelStatus.Margin = new Thickness(0, 5, 0, 12); stack.Children.Add(panelStatus);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            refreshButton = MakeButton("刷新额度", true); refreshButton.Click += delegate { RefreshUsage(true); }; buttons.Children.Add(refreshButton);
            var say = MakeButton("说点什么"); say.Click += delegate { panelLine.Text = dialogues.Next(AmbientCategory()); }; buttons.Children.Add(say);
            stack.Children.Add(buttons);
            var note = Text("台词为同人原创 · 本地随机播放", 10, Muted); note.Margin = new Thickness(0, 13, 0, 0); stack.Children.Add(note);
            panel.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) panel.Hide(); };
            panel.SizeChanged += delegate { if (panel.IsVisible) PositionPanel(); };
        }
        void PositionPanel()
        {
            if (panel == null) return;
            var area = CurrentWorkArea();
            var height = panel.ActualHeight > 1 ? panel.ActualHeight : 500;
            var left = Left - panel.Width + 12;
            if (left < area.Left + 8) left = Left + Width - 12;
            panel.Left = Math.Max(area.Left + 8, Math.Min(area.Right - panel.Width - 8, left));
            panel.Top = Math.Max(area.Top + 8, Math.Min(area.Bottom - height - 8, Top + Height - height));
        }
        void RenderUsage()
        {
            if (panelCards == null) return;
            panelCards.Children.Clear();
            refreshButton.IsEnabled = !fetching; refreshButton.Content = fetching ? "读取中…" : "刷新额度";
            if (snapshot == null)
            {
                var missing = new Border { Background = Brush("#F5EEEA"), CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 8) };
                var text = new StackPanel(); missing.Child = text;
                text.Children.Add(Text(fetching ? "正在查看你的额度" : "暂时无法获取额度", 15, Ink));
                var hint = Text(fetching ? "稍等一下。" : (fetchError ?? "点刷新额度重试。"), 12, Muted); hint.Margin = new Thickness(0, 8, 0, 0); text.Children.Add(hint);
                panelCards.Children.Add(missing); panelStatus.Text = "来源：本机 Codex 已登录账号";
                return;
            }
            bool outdated = fetchError != null || snapshot.Stale;
            if (outdated)
            {
                var warning = Text(fetchError != null ? "本次刷新失败 · 以下是上次成功读取的数据" : "上次数据已过期 · 请刷新确认", 11, Wine);
                warning.Margin = new Thickness(0, 0, 0, 10); panelCards.Children.Add(warning);
            }
            foreach (var quota in snapshot.Windows)
            {
                var card = new Border { Background = Brush("#F5EEEA"), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 9) };
                var content = new StackPanel(); card.Child = content;
                content.Children.Add(Text(quota.Name, 12, Muted));
                var line = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
                var used = Text("已用 " + quota.Used.ToString("0.#") + "%", 12, Muted); used.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(used, Dock.Right); line.Children.Add(used);
                var remaining = Text("剩余 " + quota.Remaining.ToString("0.#") + "%", 23, quota.Remaining <= 15 ? Wine : Ink); remaining.FontWeight = FontWeights.SemiBold;
                line.Children.Add(remaining); content.Children.Add(line);
                var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Min(100, quota.Used), Height = 5, Foreground = Wine,
                    Background = Brush("#E6D7DA"), BorderThickness = new Thickness(0), ToolTip = "进度条表示已用比例" };
                content.Children.Add(bar);
                var reset = Text(quota.ResetLabel, 10.5, Muted); reset.Margin = new Thickness(0, 7, 0, 0); content.Children.Add(reset);
                panelCards.Children.Add(card);
            }
            if (snapshot.Windows.Count == 0) panelCards.Children.Add(Text("账号未提供额度百分比。", 13, Muted));
            if (snapshot.UnlimitedCredits || snapshot.CreditBalance.Length > 0)
            {
                panelCards.Children.Add(Text(snapshot.UnlimitedCredits ? "额外点数：不限量" : "额外点数余额：" + snapshot.CreditBalance, 12, Ink));
            }
            panelStatus.Text = (outdated ? "上次成功：" : "更新于 ") + snapshot.FetchedAt.ToString("MM-dd HH:mm:ss") +
                (snapshot.Plan.Length > 0 ? " · " + snapshot.Plan : "") + "\n来源：本机 Codex · 每 60 秒刷新\n显示额度比例，非 token 数或可发消息数。";
            if (fetchError != null) panelStatus.Text += "\n" + fetchError;
            PositionPanel();
        }
        async void RefreshUsage(bool requested)
        {
            if (fetching || closed) return;
            fetching = true; RenderUsage();
            try
            {
                var result = await Task.Run(() => client.ReadAsync());
                if (closed) return;
                snapshot = result; fetchError = null;
                if (requested && panelLine != null) panelLine.Text = dialogues.Next("refresh");
            }
            catch (UsageUnavailable ex) { if (!closed) fetchError = ex.Message; }
            catch { if (!closed) fetchError = "暂时无法读取额度，请稍后刷新。"; }
            finally { fetching = false; if (!closed) RenderUsage(); }
        }
        public void RenderPreview(string directory)
        {
            Directory.CreateDirectory(directory);
            SetScale(1); bubbleText.Text = "……一直盯着这边，工作就会自己做完吗。"; bubble.Visibility = Visibility.Visible;
            var stage = (Grid)Content; Content = null;
            stage.Background = Brush("#EEE8ED");
            stage.Measure(new Size(290, 370)); stage.Arrange(new Rect(0, 0, 290, 370)); stage.UpdateLayout();
            SaveVisual(stage, 290, 370, Path.Combine(directory, "pet-preview.png"));
            CreatePanel();
            snapshot = UsageSnapshot.Parse(Json.Read("{\"rateLimits\":{\"primary\":{\"usedPercent\":32,\"windowDurationMins\":300,\"resetsAt\":null},\"secondary\":{\"usedPercent\":58,\"windowDurationMins\":10080,\"resetsAt\":null}}}"));
            panelLine.Text = "预览示例 · 数字为演示数据"; RenderUsage();
            panelStatus.Text = "布局预览 · 演示数据，非账号真实用量\n实际运行时从本机 Codex 读取。";
            var root = (FrameworkElement)panel.Content; panel.Content = null;
            root.Measure(new Size(354, 800)); var height = Math.Ceiling(root.DesiredSize.Height);
            root.Arrange(new Rect(0, 0, 354, height)); root.UpdateLayout();
            SaveVisual(root, 354, (int)height, Path.Combine(directory, "usage-preview.png"));
        }
        async void RunSmoke(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (fetching && DateTime.UtcNow < deadline) await Task.Delay(200);
                OpenPanel();
                await Task.Delay(500);
                var pauseBefore = state.Paused;
                SetPaused(true);
                bool pausedCorrectly = bubble.Visibility == Visibility.Hidden;
                SetPaused(pauseBefore);
                var scaleBefore = state.Scale;
                SetScale(1.2);
                bool scaledCorrectly = Math.Abs(Width - 348) < .1;
                SetScale(scaleBefore);
                bool ownershipCorrect = ReferenceEquals(panel.Owner, this) && IsVisible && panel.IsVisible;
                panel.UpdateLayout();
                SaveVisual((Visual)panel.Content, (int)Math.Ceiling(panel.Width), (int)Math.Ceiling(panel.ActualHeight), Path.Combine(directory, "usage-live.png"));
                var report = new { ok = ownershipCorrect && pausedCorrectly && scaledCorrectly && snapshot != null && fetchError == null,
                    petVisible = IsVisible, panelVisible = panel.IsVisible, panelOwnership = ownershipCorrect,
                    pauseWorks = pausedCorrectly, scaleWorks = scaledCorrectly, quotaLoaded = snapshot != null,
                    error = fetchError, timestamp = DateTime.Now.ToString("o") };
                File.WriteAllText(Path.Combine(directory, "ui-smoke.json"), Json.Codec.Serialize(report), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(directory, "ui-smoke.json"), Json.Codec.Serialize(new { ok = false, errorType = ex.GetType().Name, message = ex.Message }), Encoding.UTF8);
            }
            finally { Close(); }
        }
        static void SaveVisual(Visual visual, int width, int height, string path)
        {
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && args[0] == "--self-test")
                {
                    SelfTest.Run(args.Length > 1 ? args[1] : "self-test.txt"); return 0;
                }
                if (args.Length > 0 && args[0] == "--probe")
                {
                    var path = args.Length > 1 ? args[1] : "usage-check.json";
                    try
                    {
                        var data = new UsageClient().ReadAsync().GetAwaiter().GetResult();
                        File.WriteAllText(path, Json.Codec.Serialize(new { ok = true, fetchedAt = data.FetchedAt.ToString("o"),
                            windows = data.Windows.Select(q => new { name = q.Name, usedPercent = q.Used, remainingPercent = q.Remaining, reset = q.Reset }).ToArray(),
                            creditsAvailable = data.CreditBalance.Length > 0 || data.UnlimitedCredits }), Encoding.UTF8); return 0;
                    }
                    catch (Exception ex) { File.WriteAllText(path, Json.Codec.Serialize(new { ok = false, message = ex.Message }), Encoding.UTF8); return 2; }
                }
                var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                if (args.Length > 0 && args[0] == "--preview")
                {
                    new PetWindow(true).RenderPreview(args.Length > 1 ? args[1] : "qa"); return 0;
                }
                bool created;
                using (var mutex = new Mutex(true, "Local\\MadokaDesktopPet_1", out created))
                {
                    if (!created) return 0;
                    application.Run(new PetWindow(false, args.Length > 1 && args[0] == "--smoke" ? args[1] : null));
                }
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup-error.log"), ex.ToString(), Encoding.UTF8); } catch { }
                if (args.Length == 0) MessageBox.Show("円香暂时没能启动。请查看程序旁的 startup-error.log。", "円香桌宠");
                return 1;
            }
        }
    }

    static class SelfTest
    {
        static int checks;
        static void Require(bool condition, string message) { if (!condition) throw new Exception("Test failed: " + message); checks++; }
        public static void Run(string output)
        {
            var single = UsageSnapshot.Parse(Json.Read("{\"rateLimits\":{\"primary\":{\"usedPercent\":25,\"windowDurationMins\":300,\"resetsAt\":2000000000},\"secondary\":{\"usedPercent\":90,\"windowDurationMins\":10080}}}"));
            Require(single.Windows.Count == 2, "both windows");
            Require(single.Windows[0].Remaining == 75 && single.Windows[1].Remaining == 10, "remaining is complement of used");
            Require(single.Windows[0].Name == "5 小时额度" && single.Windows[1].Name == "每周额度", "duration from returned value");
            Require(single.Windows[0].Reset.HasValue && !single.Windows[1].Reset.HasValue, "nullable reset");
            var multi = UsageSnapshot.Parse(Json.Read("{\"rateLimits\":{\"primary\":{\"usedPercent\":99}},\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":20,\"windowDurationMins\":60}},\"other\":{\"limitName\":\"Other\",\"secondary\":{\"usedPercent\":50}}}}"));
            Require(multi.Windows.Count == 2 && multi.Windows[0].Used == 20, "prefer map and avoid duplicate legacy bucket");
            Require(multi.Windows[0].Name.Contains("Codex") && multi.Windows[1].Name.Contains("Other"), "multiple buckets labeled");
            var absent = UsageSnapshot.Parse(Json.Read("{\"rateLimits\":{\"primary\":{\"usedPercent\":null},\"secondary\":null}}"));
            Require(absent.Windows.Count == 0, "missing data never becomes zero");
            Require(UsageSnapshot.Parse(Json.Read("{}" )).Windows.Count == 0, "empty response");
            var over = UsageSnapshot.Parse(Json.Read("{\"rateLimits\":{\"primary\":{\"usedPercent\":105}}}"));
            Require(over.Windows[0].Used == 105 && over.Windows[0].Remaining == 0, "overage without inventing negative remaining");
            single.FetchedAt = DateTime.Now.AddMinutes(-4); Require(single.Stale, "old data stale");
            var reset = UsageSnapshot.Parse(Json.Read("{\"rateLimits\":{\"primary\":{\"usedPercent\":40,\"resetsAt\":1000}}}"));
            Require(reset.Stale && reset.Windows[0].Used == 40, "expired reset doesn't silently invent fresh quota");
            var dialogues = new DialogueBook(); Require(dialogues.Count >= 40, "original dialogue pool");
            foreach (var category in new[] { "idle", "click", "low", "empty", "night", "error", "drag", "refresh", "startup" })
            {
                string previous = "";
                for (int i = 0; i < 20; i++) { var line = dialogues.Next(category); Require(line.Length > 0 && line != previous, "no adjacent repeat: " + category); previous = line; }
            }
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("madoka.png"))
            {
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Require(image.PixelWidth > 100 && image.PixelHeight > 100, "embedded character asset");
                var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
                int transparent = 0, opaque = 0;
                for (int i = 3; i < pixels.Length; i += 4) { if (pixels[i] == 0) transparent++; if (pixels[i] > 200) opaque++; }
                Require(transparent > image.PixelWidth * image.PixelHeight / 5 && opaque > 10000, "real transparent background and visible figure");
            }
            var parent = Path.GetDirectoryName(Path.GetFullPath(output)); Directory.CreateDirectory(parent);
            File.WriteAllText(output, "PASS: " + checks + " assertions\nQuota parsing, freshness, original dialogues, embedded transparent character.\n", Encoding.UTF8);
        }
    }
}
