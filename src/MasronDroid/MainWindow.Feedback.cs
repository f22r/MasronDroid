using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MasronDroid;

public partial class MainWindow
{
    private sealed record OperationNotice(string Message, string Detail, string Kind);
    private OperationNotice? operationNotice, lastNotice;
    private readonly Queue<OperationNotice> notices = new();
    private bool noticeScheduled;
    private int modalDepth;
    private string lastBackgroundNotice = "";

    private void CaptureNotice(string key, string detail, object[] args)
    {
        if (key is "Busy" or "Scanning" or "MirrorStarting" or "MirrorClosed") return;
        string message = key == "Done" ? T("Done", T((string)args[0])) : T(key, args);
        string kind = key switch
        {
            "Done" or "Scanned" or "RootTransferComplete" or "RootModuleDownloaded" or "RootVerified"
                or "WifiPaired" or "WifiConnected" or "UpdateReady" or "RootLatestChecked" or "ApplicationsRefreshed"
                or "RootCatalogUpdated" or "ConsoleCompleted" or "RootTransferPresent" or "RootFlowComplete" => "Success",
            "RootUpToDate" or "StayAwakeEnabled" or "StayAwakeDisabled" or "RootUninstallComplete" => "Success",
            "ModuleRootGranted" => "Success",
            "ConsoleCancelled" or "ModuleManagerOpened" => "Information",
            "UnlockPhone" or "OdinHint" or "MtpHint" or "NoRamCandidates" or "RunningAppsUnavailable"
                or "CloseBusy" or "NoScanTools" or "WifiQrExpired" or "RootCatalogPartial"
                or "RootFlowUnconfirmed" or "RootFlowRootPending" or "RootFlowRebootManual" => "Warning",
            "BatchResult" => Convert.ToInt32(args[1]) == 0 ? "Success"
                : Convert.ToInt32(args[0]) > 0 ? "Warning" : "Error",
            _ => "Error"
        };
        var notice = new OperationNotice(message, detail, kind);
        if (busy)
        {
            if (key == "BatchResult" && kind != "Success" || operationNotice?.Kind != "Error" || kind == "Error")
                operationNotice = notice;
            return;
        }
        string signature = key + message + detail;
        if (signature == lastBackgroundNotice) return;
        lastBackgroundNotice = signature;
        EnqueueNotice(notice);
    }

    private void EnqueueNotice(OperationNotice notice)
    {
        lastNotice = notice;
        if (!persistent || !IsLoaded) return;
        notices.Enqueue(notice);
        ScheduleNotices();
    }

    private void ScheduleNotices()
    {
        if (!persistent || !IsLoaded || busy || modalDepth > 0 || noticeScheduled || notices.Count == 0) return;
        noticeScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                while (notices.Count > 0 && IsLoaded && !busy && modalDepth == 0)
                {
                    var next = notices.Dequeue();
                    ShowAppDialog(CreateNoticeDialog(next.Message, next.Detail, next.Kind), true);
                }
            }
            finally { noticeScheduled = false; }
        }));
    }

    private Window CreateNoticeDialog(string message, string detail, string kind)
    {
        bool error = kind == "Error", warning = kind == "Warning", success = kind == "Success";
        var accent = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            error ? "#B52E3C" : warning ? "#946315" : success ? "#19734D" : "#126782"));
        var body = new DockPanel { Margin = new Thickness(24) };
        var heading = new DockPanel();
        heading.Children.Add(new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 12, 0),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                error ? "#FBECEF" : warning ? "#FFF2DE" : success ? "#E4F5EC" : "#E8F2F9")),
            Child = new TextBlock { Text = error ? "\uEA39" : warning ? "\uE7BA" : success ? "\uE73E" : "\uE946",
                FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 21, Foreground = accent,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        heading.Children.Add(new TextBlock { Text = T("Feedback" + kind), FontSize = 22,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(heading, Dock.Top);
        body.Children.Add(heading);
        var messages = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        messages.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var details = new TextBox { Text = detail, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"), FontSize = 12, MaxHeight = 180,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(245, 248, 250)),
                BorderThickness = new Thickness(0), Padding = new Thickness(10),
                Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            var toggle = new Button { Content = T("FeedbackDetails"), HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(9, 4, 9, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(18, 103, 130)),
                Background = new SolidColorBrush(Color.FromRgb(245, 248, 250)) };
            toggle.Click += (_, _) => details.Visibility = details.IsVisible ? Visibility.Collapsed : Visibility.Visible;
            messages.Children.Add(toggle);
            messages.Children.Add(details);
        }
        var dialog = new Window { Title = T("Feedback" + kind), Width = 490, SizeToContent = SizeToContent.Height };
        var close = new Button { Content = T("FeedbackClose"), IsDefault = true, IsCancel = true,
            Style = (Style)FindResource("Primary"), MinWidth = 94, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0) };
        close.Click += (_, _) => dialog.DialogResult = true;
        DockPanel.SetDock(close, Dock.Bottom);
        body.Children.Add(close);
        body.Children.Add(new ScrollViewer { MaxHeight = Math.Max(120, ActualHeight - 240),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = messages });
        dialog.Content = body;
        return dialog;
    }

    private bool? ShowAppDialog(Window dialog, bool hasHeading = false)
    {
        dialog.Owner = this;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        dialog.WindowStyle = WindowStyle.None;
        dialog.ResizeMode = ResizeMode.NoResize;
        dialog.ShowInTaskbar = false;
        dialog.AllowsTransparency = true;
        dialog.Background = Brushes.Transparent;
        dialog.FontFamily = FontFamily;
        dialog.FontSize = FontSize;
        dialog.Foreground = Foreground;
        dialog.MaxWidth = Math.Max(320, ActualWidth - 48);
        dialog.MaxHeight = Math.Max(260, ActualHeight - 48);
        System.Windows.Shell.WindowChrome.SetWindowChrome(dialog, null);
        var content = dialog.Content;
        dialog.Content = null;
        var panel = new DockPanel();
        if (!hasHeading)
        {
            var heading = new DockPanel { Margin = new Thickness(20, 16, 16, 0) };
            var close = new Button { Content = "\uE711", FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Width = 30, Height = 30, Padding = new Thickness(0), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
            close.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, T("Close"));
            close.Click += (_, _) => dialog.DialogResult = false;
            DockPanel.SetDock(close, Dock.Right);
            heading.Children.Add(close);
            heading.Children.Add(new TextBlock { Text = dialog.Title, FontWeight = FontWeights.SemiBold, FontSize = 20,
                VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(heading, Dock.Top);
            panel.Children.Add(heading);
        }
        if (content is UIElement element && (!double.IsNaN(dialog.Height) || content is DockPanel)) panel.Children.Add(element);
        else if (content is UIElement scrollContent) panel.Children.Add(new ScrollViewer
        {
            Content = scrollContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(180, ActualHeight - (hasHeading ? 104 : 156))
        });
        dialog.Content = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(205, 220, 228)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Child = panel };
        modalDepth++;
        RenderActivity();
        try { return dialog.ShowDialog(); }
        finally { modalDepth--; RenderActivity(); ScheduleNotices(); }
    }

    private void ShowInformation(string title, string message)
    {
        var dialog = CreateNoticeDialog(message, "", "Information");
        dialog.Title = title;
        ShowAppDialog(dialog, true);
    }

    private void RenderActivity()
    {
        if (!initialized) return;
        bool working = busy || wifiPending || inventoryLoading || readingFacts || runningAppsReading
            || rootFetching || rootHighlightsFetching || rootLatestChecking || rootStatusReading
            || rootRecoveryReading || rootTransferReading || rootRuntimeReading || screenAwakeUpdating || updateChecking || navigationPending || consoleCommand != null;
        working |= qrInstance != null;
        ActivityBar.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        WifiProgress.Visibility = wifiPending || qrInstance != null ? Visibility.Visible : Visibility.Collapsed;
        RootCatalogProgress.Visibility = rootFetching || rootHighlightsFetching ? Visibility.Visible : Visibility.Collapsed;
        InventoryProgress.Visibility = inventoryLoading ? Visibility.Visible : Visibility.Collapsed;
        OperationOverlay.Visibility = busy && modalDepth == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModalShade.Visibility = modalDepth > 0 ? Visibility.Visible : Visibility.Collapsed;
        VideoSurface.Visibility = busy || modalDepth > 0 ? Visibility.Hidden : Visibility.Visible;
    }
}
