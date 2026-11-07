using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace SteamSmart.Desktop
{
    public sealed class MainWindow : Window
    {
        private readonly RuntimeController controller;
        private readonly FrameworkElement view;
        private readonly Forms.NotifyIcon tray;
        private readonly System.Drawing.Icon trayIcon;
        private readonly ThemeManager theme;
        private readonly List<NodeView> allNodes = new List<NodeView>();
        private ObservableCollection<DomainRow> domainRows = new ObservableCollection<DomainRow>();
        private List<SourceOption> sourceOptions = new List<SourceOption>();
        private bool domainsLoaded;
        private bool domainsLoading;
        private bool settingRows;
        private bool domainsDirty;
        private bool domainOperation;
        private bool closing;
        private bool allowClose;
        private bool disposed;
        private int stopRevision;
        private string selectedPage = "overview";
        private string lastLogSignature = "";
        private string localMessage;
        private bool localMessageError;
        private bool domainFeedbackError;
        private DateTime localMessageUntil;
        private Brush muted { get { return theme.Brush("TextSecondary"); } }
        private Brush green { get { return theme.Brush("Success"); } }
        private Brush error { get { return theme.Brush("Danger"); } }

        public MainWindow(RuntimeController runtimeController)
        {
            if (runtimeController == null) throw new ArgumentNullException("runtimeController");
            controller = runtimeController;
            Title = "Steam 自动优选";
            Width = Math.Min(1200, SystemParameters.WorkArea.Width);
            Height = Math.Min(800, SystemParameters.WorkArea.Height);
            MinWidth = Math.Min(1060, SystemParameters.WorkArea.Width);
            MinHeight = Math.Min(700, SystemParameters.WorkArea.Height);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            FontSize = 14;
            Background = BrushOf("#F3F6FA");
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamSmart.MainWindow.xaml"))
            {
                if (stream == null) throw new InvalidOperationException("缺少应用界面资源。");
                view = (FrameworkElement)XamlReader.Load(stream);
            }
            Content = view;
            Control<ToggleButton>("NavOverview").Click += delegate { SelectPage("overview"); };
            Control<ToggleButton>("NavDomains").Click += delegate { SelectPage("domains"); };
            Control<ToggleButton>("NavLogs").Click += delegate { SelectPage("logs"); };
            Control<ToggleButton>("NavHelp").Click += delegate { SelectPage("help"); };
            Control<Button>("StartButton").Click += async delegate { await StartServiceAsync(); };
            Control<Button>("StopButton").Click += async delegate { await StopServiceAsync(); };
            Control<ComboBox>("NodeFilter").SelectionChanged += delegate { RefreshNodeFilter(); };
            Control<DataGrid>("NodesGrid").SelectionChanged += delegate
            {
                Control<Button>("CopyIpButton").IsEnabled = Control<DataGrid>("NodesGrid").SelectedItem is NodeView;
            };
            Control<Button>("CopyIpButton").Click += delegate { CopySelectedIp(); };
            Control<Button>("AddDomainButton").Click += delegate { AddDomain(); };
            Control<Button>("DeleteDomainButton").Click += delegate { DeleteSelectedDomains(); };
            Control<Button>("DefaultDomainsButton").Click += async delegate { await PreviewDefaultsAsync(); };
            Control<Button>("ReloadDomainsButton").Click += async delegate { await LoadDomainsAsync(false); };
            Control<Button>("ApplyDomainsButton").Click += async delegate { await ApplyDomainsAsync(); };
            Control<DataGrid>("DomainsGrid").ItemsSource = domainRows;
            Control<DataGrid>("DomainsGrid").SelectionChanged += delegate { UpdateDomainControls(); };
            var kindColumn = Control<DataGridComboBoxColumn>("KindColumn");
            kindColumn.ItemsSource = new[] { new KindOption("download", "下载地址"), new KindOption("redirect", "跳转入口") };
            var comboCellStyle = new Style(typeof(DataGridCell), (Style)view.FindResource(typeof(DataGridCell)));
            comboCellStyle.Setters.Add(new Setter(DataGridCell.PaddingProperty, new Thickness(8, 0, 8, 0)));
            kindColumn.CellStyle = comboCellStyle;
            Control<DataGridComboBoxColumn>("SourceColumn").CellStyle = comboCellStyle;
            tray = new Forms.NotifyIcon();
            using (Stream iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamSmart.Icon.ico"))
            {
                if (iconStream != null)
                {
                    using (var embeddedIcon = new System.Drawing.Icon(iconStream)) trayIcon = (System.Drawing.Icon)embeddedIcon.Clone();
                }
                else trayIcon = (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
            }
            tray.Icon = trayIcon;
            Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(trayIcon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(32, 32));
            tray.Text = "Steam 自动优选";
            var trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Items.Add("显示窗口", null, delegate { Dispatcher.BeginInvoke(new Action(ShowFromTray)); });
            trayMenu.Items.Add("停止优选", null, delegate
            {
                Dispatcher.BeginInvoke(new Action(async delegate { ShowFromTray(); await StopServiceAsync(); }));
            });
            trayMenu.Items.Add(new Forms.ToolStripSeparator());
            trayMenu.Items.Add("退出并恢复", null, delegate { Dispatcher.BeginInvoke(new Action(delegate { ShowFromTray(); Close(); })); });
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { Dispatcher.BeginInvoke(new Action(ShowFromTray)); };
            theme = new ThemeManager(this, view, controller.DataDirectory);
            var appearance = Control<ComboBox>("ThemeMode");
            foreach (ComboBoxItem option in appearance.Items)
            {
                if ((string)option.Tag == theme.Mode) { appearance.SelectedItem = option; break; }
            }
            theme.Changed += delegate
            {
                theme.ApplyTray(trayMenu);
                DomainFeedback(Control<TextBlock>("DomainFeedback").Text, domainFeedbackError);
                RefreshStatus();
            };
            theme.Apply();
            appearance.SelectionChanged += delegate
            {
                var option = appearance.SelectedItem as ComboBoxItem;
                if (option != null && (string)option.Tag != theme.Mode && !theme.SetMode((string)option.Tag))
                    ShowRuntimeMessage("外观已切换，但暂时无法保存选择，请检查应用目录的写入权限。", true, true);
            };
            controller.Changed += OnControllerChanged;
            StateChanged += delegate
            {
                if (WindowState == WindowState.Minimized && !closing)
                {
                    tray.Visible = true;
                    Hide();
                }
            };
            Loaded += OnLoaded;
            Closing += OnClosing;
            Closed += delegate { DisposeTray(); };
            SelectPage("overview");
            RefreshStatus();
        }

        private T Control<T>(string name) where T : class
        {
            T found = view.FindName(name) as T;
            if (found == null) throw new InvalidOperationException("界面控件未找到：" + name);
            return found;
        }

        private static Brush BrushOf(string color)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color);
            brush.Freeze();
            return brush;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await LoadDomainsAsync(false);
            if (Program.AutoStart && !closing) await StartServiceAsync();
        }

        public void ShowFromTray()
        {
            if (disposed) return;
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(ShowFromTray)); return; }
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            tray.Visible = false;
            Activate();
        }

        public void SelectPage(string page)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(delegate { SelectPage(page); })); return; }
            if (page == "域名规则") page = "domains";
            if (page == "运行记录") page = "logs";
            if (page == "使用帮助") page = "help";
            if (page != "domains" && page != "logs" && page != "help") page = "overview";
            selectedPage = page;
            string[] keys = { "overview", "domains", "logs", "help" };
            string[] controls = { "Overview", "Domains", "Logs", "Help" };
            string[] titles = { "下载概览", "域名规则", "运行记录", "使用帮助" };
            string[] descriptions = {
                "自动检测可用节点，让每次下载更顺畅。",
                "查看和管理交给本机优选组件的下载域名。",
                "了解服务状态与最近的节点检测结果。",
                "快速开始，了解停止、恢复与支持范围。"
            };
            for (int i = 0; i < keys.Length; i++)
            {
                bool active = page == keys[i];
                Control<ToggleButton>("Nav" + controls[i]).IsChecked = active;
                Control<FrameworkElement>(controls[i] + "Page").Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                if (active)
                {
                    Control<TextBlock>("PageTitle").Text = titles[i];
                    Control<TextBlock>("PageDescription").Text = descriptions[i];
                }
            }
            if (page == "domains" && !domainsLoaded && !domainsLoading && IsLoaded) BeginLoadDomains();
        }

        private async void BeginLoadDomains() { await LoadDomainsAsync(false); }

        private void OnControllerChanged(object sender, EventArgs args)
        {
            if (disposed) return;
            if (Dispatcher.CheckAccess()) RefreshStatus();
            else Dispatcher.BeginInvoke(new Action(delegate { if (!disposed) RefreshStatus(); }));
        }

        private void RefreshStatus()
        {
            StatusSnapshot status = controller.Status;
            bool running = controller.IsRunning;
            bool busy = controller.IsBusy;
            bool hasError = status != null && status.state == "error" && !running && !busy;
            string label = closing ? "正在退出" : (busy ? "正在处理" : (hasError ? "需要处理" : (running ? "运行中" : "已停止")));
            Brush stateColor = hasError ? error : (running && !busy ? green : muted);
            Control<TextBlock>("StateText").Text = label;
            Control<TextBlock>("StateText").Foreground = stateColor;
            Control<Ellipse>("StateDot").Fill = stateColor;
            Control<Border>("StateBadge").Background = theme.Brush(hasError ? "DangerSurface" : (running && !busy ? "SuccessSurface" : "NeutralSurface"));
            Control<TextBlock>("SidebarState").Text = label;
            Control<Ellipse>("SidebarDot").Fill = stateColor;
            Control<Button>("StartButton").Visibility = !running && !busy ? Visibility.Visible : Visibility.Collapsed;
            Control<Button>("StartButton").IsEnabled = !closing && !busy;
            Control<Button>("StopButton").Visibility = running || busy ? Visibility.Visible : Visibility.Collapsed;
            Control<Button>("StopButton").IsEnabled = !closing;
            if (closing) ShowRuntimeMessage("正在停止转发并恢复设置，完成后自动退出…", false, false);
            else if (DateTime.UtcNow < localMessageUntil) ShowRuntimeMessage(localMessage, localMessageError, false);
            else ShowRuntimeMessage(String.IsNullOrWhiteSpace(controller.Message) ? "准备就绪" : controller.Message, hasError, false);

            double rate = status == null ? 0 : Math.Max(0, status.totalRateBps);
            string rateUnit;
            Control<TextBlock>("SpeedValue").Text = FormatRateNumber(rate, out rateUnit);
            Control<TextBlock>("SpeedUnit").Text = rateUnit;
            Control<TextBlock>("TransferredValue").Text = FormatBytes(status == null ? 0 : status.totalBytes);
            Control<TextBlock>("ProbeTraffic").Text = "检测流量 " + FormatBytes(status == null ? 0 : status.probeBytes);
            allNodes.Clear();
            var activeSources = new List<string>();
            int preferred = 0;
            if (status != null && status.groups != null)
            {
                foreach (var group in status.groups)
                {
                    if (group == null) continue;
                    if (group.rateBps > 0 || group.testing) activeSources.Add(group.name);
                    if (group.ips == null) continue;
                    foreach (var ip in group.ips)
                    {
                        if (ip == null || String.IsNullOrWhiteSpace(ip.ip)) continue;
                        if (ip.selected) preferred++;
                        string ipState = NodeState(ip.state, ip.selected, ip.valid);
                        allNodes.Add(new NodeView {
                            Source = group.name, IP = ip.ip, Rate = FormatRate(ip.rateBps),
                            Score = ip.valid && ip.scoreMbps > 0 ? ip.scoreMbps.ToString("0.0", CultureInfo.CurrentCulture) + " Mbps" : "—",
                            State = ipState, Preferred = ip.selected, RateValue = ip.rateBps,
                            StatusBackground = theme.Brush(ip.selected ? "SuccessSurface" : "NeutralSurface"),
                            StatusForeground = ip.selected ? green : muted
                        });
                    }
                }
            }
            allNodes.Sort(delegate(NodeView a, NodeView b) { int result = b.Preferred.CompareTo(a.Preferred); return result != 0 ? result : b.RateValue.CompareTo(a.RateValue); });
            Control<TextBlock>("PreferredCount").Text = preferred.ToString(CultureInfo.CurrentCulture);
            Control<TextBlock>("CurrentSource").Text = activeSources.Count == 0 ? "等待下载" : (activeSources.Count == 1 ? activeSources[0] : activeSources[0] + " 等");
            Control<TextBlock>("CurrentSource").ToolTip = String.Join("、", activeSources);
            Control<TextBlock>("CurrentSourceDetail").Text = activeSources.Count > 1 ? "正在使用 " + activeSources.Count + " 个下载来源" : "跟随 Steam 的下载来源";
            Control<TextBlock>("SpeedDescription").Text = rate > 0 ? "正在转发 Steam 下载数据，节点变化时自动验证。" : (running ? "等待 Steam 下载请求，收到后自动检测节点。" : "启动优选后，开始 Steam 下载即可。");
            RefreshNodeFilter();
            RefreshLogs(status);
            UpdateDomainControls();
            string trayState = running ? "运行中 · " + FormatRate(rate) : "已停止";
            tray.Text = "Steam 自动优选 · " + trayState;
        }

        private void RefreshNodeFilter()
        {
            var grid = Control<DataGrid>("NodesGrid");
            var previous = grid.SelectedItem as NodeView;
            bool onlyPreferred = Control<ComboBox>("NodeFilter").SelectedIndex == 1;
            var rows = allNodes.Where(delegate(NodeView n) { return !onlyPreferred || n.Preferred; }).ToList();
            grid.ItemsSource = rows;
            if (previous != null) grid.SelectedItem = rows.FirstOrDefault(delegate(NodeView n) { return n.IP == previous.IP && n.Source == previous.Source; });
            Control<StackPanel>("NodesEmpty").Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Control<Button>("CopyIpButton").IsEnabled = grid.SelectedItem is NodeView;
        }

        private void CopySelectedIp()
        {
            NodeView selected = Control<DataGrid>("NodesGrid").SelectedItem as NodeView;
            if (selected == null) return;
            try { Clipboard.SetText(selected.IP); ShowRuntimeMessage("已复制 IP 地址。", false, true); }
            catch { ShowRuntimeMessage("暂时无法访问剪贴板，请重试。", true, true); }
        }

        private void RefreshLogs(StatusSnapshot status)
        {
            var entries = status == null || status.events == null ? new List<EventSnapshot>() : status.events;
            string signature = String.Join("\n", entries.Select(delegate(EventSnapshot e) { return e.at + " " + e.message; }));
            if (signature == lastLogSignature) return;
            lastLogSignature = signature;
            var logs = new List<LogView>();
            foreach (var item in entries)
            {
                if (item == null) continue;
                DateTimeOffset time;
                string displayTime = DateTimeOffset.TryParse(item.at, CultureInfo.InvariantCulture, DateTimeStyles.None, out time) ? time.ToLocalTime().ToString("HH:mm:ss") : "";
                logs.Add(new LogView { Time = displayTime, Message = item.message });
            }
            logs.Reverse();
            Control<ListBox>("LogsList").ItemsSource = logs;
            Control<TextBlock>("LogsEmpty").Visibility = logs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task StartServiceAsync()
        {
            if (closing || controller.IsBusy || controller.IsRunning) return;
            localMessage = null;
            localMessageUntil = DateTime.MinValue;
            try { await controller.StartAsync(); }
            catch (Exception ex) { ShowRuntimeMessage(ex.Message, true, true); }
            RefreshStatus();
        }

        private async Task StopServiceAsync()
        {
            if (closing) return;
            stopRevision++;
            try { await controller.StopAsync(); ShowRuntimeMessage("已停止优选并恢复转发设置，可随时再次启动。", false, true); }
            catch (Exception ex) { ShowRuntimeMessage(ex.Message, true, true); }
            RefreshStatus();
        }

        private async Task LoadDomainsAsync(bool defaults)
        {
            if (domainsLoading || closing) return;
            domainsLoading = true;
            UpdateDomainControls();
            try
            {
                ConfigView config = defaults ? await controller.DefaultDomainsAsync() : await controller.ReadDomainsAsync();
                if (closing) return;
                ReplaceDomains(config, defaults);
                DomainFeedback(defaults ? "已载入默认列表，点击“保存并应用”后生效。" : "更改后点击“保存并应用”，转发服务会短暂重启。", false);
            }
            catch (Exception ex) { DomainFeedback(ex.Message, true); }
            finally { domainsLoading = false; UpdateDomainControls(); }
        }

        private async Task PreviewDefaultsAsync() { await LoadDomainsAsync(true); }

        private void ReplaceDomains(ConfigView config, bool dirty)
        {
            settingRows = true;
            try
            {
                foreach (DomainRow row in domainRows) row.PropertyChanged -= OnDomainChanged;
                sourceOptions = config.groups == null ? new List<SourceOption>() : config.groups.ToList();
                domainRows = new ObservableCollection<DomainRow>();
                if (config.rows != null)
                {
                    foreach (DomainRow row in config.rows)
                    {
                        var copy = new DomainRow { domain = row.domain, groupId = row.groupId, kind = row.kind };
                        copy.PropertyChanged += OnDomainChanged;
                        domainRows.Add(copy);
                    }
                }
                Control<DataGridComboBoxColumn>("SourceColumn").ItemsSource = sourceOptions;
                Control<DataGrid>("DomainsGrid").ItemsSource = domainRows;
                domainsLoaded = true;
                domainsDirty = dirty;
            }
            finally { settingRows = false; UpdateDomainCount(); }
        }

        private void OnDomainChanged(object sender, PropertyChangedEventArgs e)
        {
            if (settingRows) return;
            domainsDirty = true;
            UpdateDomainCount();
            DomainFeedback("有未保存的更改。保存并应用后，新的域名规则才会生效。", false);
        }

        private void UpdateDomainCount()
        {
            Control<TextBlock>("DomainCount").Text = domainRows.Count + " 个域名" + (domainsDirty ? " · 未保存" : " · 已保存");
            UpdateDomainControls();
        }

        private void UpdateDomainControls()
        {
            bool enabled = !closing && !domainsLoading && !domainOperation && !controller.IsBusy;
            Control<DataGrid>("DomainsGrid").IsReadOnly = !enabled;
            Control<Button>("AddDomainButton").IsEnabled = enabled && domainsLoaded && sourceOptions.Count > 0;
            Control<Button>("DeleteDomainButton").IsEnabled = enabled && domainsLoaded && Control<DataGrid>("DomainsGrid").SelectedItems.Count > 0;
            Control<Button>("DefaultDomainsButton").IsEnabled = enabled;
            Control<Button>("ReloadDomainsButton").IsEnabled = enabled;
            Control<Button>("ApplyDomainsButton").IsEnabled = enabled && domainsLoaded && domainsDirty;
        }

        private void AddDomain()
        {
            if (sourceOptions.Count == 0 || domainOperation || closing) return;
            var row = new DomainRow { domain = "", groupId = sourceOptions[0].id, kind = "download" };
            row.PropertyChanged += OnDomainChanged;
            domainRows.Add(row);
            domainsDirty = true;
            UpdateDomainCount();
            var grid = Control<DataGrid>("DomainsGrid");
            grid.SelectedItem = row;
            grid.ScrollIntoView(row);
            grid.CurrentCell = new DataGridCellInfo(row, grid.Columns[0]);
            grid.Focus();
            grid.BeginEdit();
        }

        private void DeleteSelectedDomains()
        {
            var grid = Control<DataGrid>("DomainsGrid");
            var selected = grid.SelectedItems.Cast<DomainRow>().ToList();
            foreach (DomainRow row in selected) { row.PropertyChanged -= OnDomainChanged; domainRows.Remove(row); }
            if (selected.Count > 0) { domainsDirty = true; UpdateDomainCount(); DomainFeedback("已从列表移除选中域名，保存并应用后生效。", false); }
        }

        private async Task ApplyDomainsAsync()
        {
            if (closing || domainOperation || controller.IsBusy) return;
            var grid = Control<DataGrid>("DomainsGrid");
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            var rows = domainRows.Select(delegate(DomainRow row) { return new DomainRow { domain = row.domain, groupId = row.groupId, kind = row.kind }; }).ToList();
            domainOperation = true;
            int operationRevision = stopRevision;
            UpdateDomainControls();
            try
            {
                DomainFeedback("正在检查域名和来源…", false);
                await controller.ValidateDomainsAsync(rows);
                if (closing || operationRevision != stopRevision)
                {
                    DomainFeedback("已取消应用，域名更改尚未保存。", false);
                    return;
                }
                DomainFeedback("正在应用规则，转发服务会短暂重启…", false);
                await controller.ApplyDomainsAsync(rows);
                if (closing || operationRevision != stopRevision) return;
                ConfigView saved = await controller.ReadDomainsAsync();
                ReplaceDomains(saved, false);
                DomainFeedback("下载域名规则已保存并应用。", false);
            }
            catch (Exception ex) { DomainFeedback(ex.Message, true); }
            finally { domainOperation = false; UpdateDomainControls(); RefreshStatus(); }
        }

        private void DomainFeedback(string text, bool isError)
        {
            domainFeedbackError = isError;
            Control<TextBlock>("DomainFeedback").Text = text;
            Control<TextBlock>("DomainFeedback").Foreground = isError ? error : muted;
        }

        private void ShowRuntimeMessage(string text, bool isError, bool remember)
        {
            Control<TextBlock>("RuntimeMessage").Text = text ?? "";
            Control<TextBlock>("RuntimeMessage").ToolTip = text;
            Control<TextBlock>("RuntimeMessage").Foreground = isError ? error : muted;
            Control<Border>("MessageBar").Background = isError ? theme.Brush("DangerSurface") : Brushes.Transparent;
            if (remember) { localMessage = text; localMessageError = isError; localMessageUntil = DateTime.UtcNow.AddSeconds(isError ? 30 : 10); }
        }

        private async void OnClosing(object sender, CancelEventArgs e)
        {
            if (allowClose) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            stopRevision++;
            RefreshStatus();
            try
            {
                await controller.ShutdownAsync();
                allowClose = true;
                controller.Changed -= OnControllerChanged;
                DisposeTray();
                Close();
            }
            catch (Exception ex)
            {
                closing = false;
                ShowRuntimeMessage("恢复尚未完成：" + ex.Message + " 请重试停止或关闭窗口。", true, true);
                RefreshStatus();
            }
        }

        private void DisposeTray()
        {
            if (disposed) return;
            disposed = true;
            theme.Dispose();
            tray.Visible = false;
            if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose();
            tray.Dispose();
            trayIcon.Dispose();
        }

        private static string FormatRateNumber(double bytes, out string unit)
        {
            if (Double.IsNaN(bytes) || Double.IsInfinity(bytes) || bytes < 0) bytes = 0;
            if (bytes >= 1073741824) { unit = "GB/s"; return (bytes / 1073741824).ToString("0.00", CultureInfo.CurrentCulture); }
            if (bytes >= 1048576) { unit = "MB/s"; return (bytes / 1048576).ToString("0.00", CultureInfo.CurrentCulture); }
            if (bytes >= 1024) { unit = "KB/s"; return (bytes / 1024).ToString("0.0", CultureInfo.CurrentCulture); }
            unit = "B/s"; return bytes.ToString("0", CultureInfo.CurrentCulture);
        }

        private static string FormatRate(double bytes)
        {
            string unit; string number = FormatRateNumber(bytes, out unit); return number + " " + unit;
        }

        private static string FormatBytes(double bytes)
        {
            if (Double.IsNaN(bytes) || Double.IsInfinity(bytes) || bytes < 0) bytes = 0;
            if (bytes >= 1073741824) return (bytes / 1073741824).ToString("0.00", CultureInfo.CurrentCulture) + " GB";
            if (bytes >= 1048576) return (bytes / 1048576).ToString("0.0", CultureInfo.CurrentCulture) + " MB";
            if (bytes >= 1024) return (bytes / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " KB";
            return bytes.ToString("0", CultureInfo.CurrentCulture) + " B";
        }

        private static string NodeState(string state, bool selected, bool valid)
        {
            if (selected) return "优先使用";
            switch (state)
            {
                case "testing": case "probing": return "正在检测";
                case "redirect": case "redirected": return "跳转其他节点";
                case "timeout": return "本次超时";
                case "failed": case "unavailable": case "error": return "暂不可用";
                case "direct": case "available": case "healthy": case "valid": return "可用";
                default: return valid ? "可用" : "等待检测";
            }
        }

        public sealed class KindOption
        {
            public string Value { get; set; }
            public string Label { get; set; }
            public KindOption(string value, string label) { Value = value; Label = label; }
        }

        public sealed class NodeView
        {
            public string Source { get; set; }
            public string IP { get; set; }
            public string Rate { get; set; }
            public string Score { get; set; }
            public string State { get; set; }
            public bool Preferred { get; set; }
            public double RateValue { get; set; }
            public Brush StatusBackground { get; set; }
            public Brush StatusForeground { get; set; }
        }

        public sealed class LogView
        {
            public string Time { get; set; }
            public string Message { get; set; }
        }
    }
}
