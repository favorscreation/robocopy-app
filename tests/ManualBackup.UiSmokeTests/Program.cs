using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ManualBackup.App;

internal static class Program
{
    private static int _checks;
    private static readonly List<Exception> DispatcherErrors = [];

    [STAThread]
    private static int Main(string[] args)
    {
        var qaRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".qa"));
        var fixture = Path.Combine(qaRoot, "ui-smoke-" + Guid.NewGuid().ToString("N"));
        var previousDataDirectory = Environment.GetEnvironmentVariable("BACKUPDESK_DATA_DIR");
        var settingsDirectory = Path.Combine(fixture, "settings");
        var snapshot = args.Length == 2 && args[0] == "--snapshot" ? Path.GetFullPath(args[1]) : null;
        MainWindow? window = null;
        Application? app = null;
        try
        {
            Directory.CreateDirectory(fixture);
            Environment.SetEnvironmentVariable("BACKUPDESK_DATA_DIR", settingsDirectory);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            // WPF queues the application's default Startup action in its
            // constructor. Abort that action on our own new Application so
            // pumping bindings never opens the StartupUri window. Resources,
            // controls, event handlers, and actual backup work run normally.
            var startupOperations = new List<DispatcherOperation>();
            DispatcherHookEventHandler captureStartup = (_, e) =>
            {
                if (e.Operation.Priority == DispatcherPriority.Send) startupOperations.Add(e.Operation);
            };
            Dispatcher.CurrentDispatcher.Hooks.OperationPosted += captureStartup;
            try { app = new ManualBackup.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; }
            finally { Dispatcher.CurrentDispatcher.Hooks.OperationPosted -= captureStartup; }
            foreach (var operation in startupOperations) operation.Abort();
            ((ManualBackup.App.App)app).InitializeComponent();
            app.DispatcherUnhandledException += (_, e) => { DispatcherErrors.Add(e.Exception); e.Handled = true; };
            window = new MainWindow();
            var content = (FrameworkElement)window.Content;
            var combo = (ComboBox?)window.FindName("ParallelSourcesComboBox")
                ?? throw new InvalidOperationException("ParallelSourcesComboBox is missing.");
            // An unshown Window keeps all child IsVisible values false. Host
            // its real content offscreen, with the same inherited appearance,
            // so templates and DataGrid rows can be laid out and rendered.
            window.Content = null;
            content.DataContext = window;
            var renderHost = new Border { Background = window.Background, Child = content };
            TextElement.SetFontFamily(renderHost, window.FontFamily);
            TextElement.SetFontSize(renderHost, window.FontSize);
            TextElement.SetForeground(renderHost, window.Foreground);
            // A message-only HWND gives WPF an owned presentation source while
            // remaining permanently absent from the desktop and taskbar.
            using var offscreenSource = new HwndSource(new HwndSourceParameters("BackupDesk UI smoke message-only host")
            {
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE
                WindowStyle = 0x40000000, // WS_CHILD, without WS_VISIBLE
                Width = 1160,
                Height = 1000
            });
            offscreenSource.RootVisual = renderHost;
            var first = window.SelectedProfile ?? throw new InvalidOperationException("Default profile is missing.");
            FlushBindings();

            Check(combo.Items.Cast<int>().SequenceEqual(Enumerable.Range(1, 8)), "ComboBox offers limits 1 through 8");
            Equal(2, first.MaxParallelSources, "new profile defaults to two");
            Equal(2, (int)combo.SelectedItem, "real ComboBox binding selects default two");
            Check(BindingOperations.GetBindingExpression(combo, Selector.SelectedItemProperty) is not null, "selected item has a live binding");
            combo.SelectedItem = 1;
            FlushBindings();
            Equal(1, first.MaxParallelSources, "selecting one updates the profile through binding");
            combo.SelectedItem = 4;
            FlushBindings();
            Equal(4, first.MaxParallelSources, "selecting four updates the profile through binding");

            var second = new BackupProfile { Name = "UI smoke second profile" };
            window.Profiles.Add(second);
            window.SelectedProfile = second;
            FlushBindings();
            Equal(2, (int)combo.SelectedItem, "switching to new profile displays its default two");
            combo.SelectedItem = 1;
            FlushBindings();
            Equal(1, second.MaxParallelSources, "second profile keeps its own selected limit");
            window.SelectedProfile = first;
            FlushBindings();
            Equal(4, (int)combo.SelectedItem, "switching back restores the first profile limit");
            Equal(1, second.MaxParallelSources, "first profile changes do not affect the second profile");

            first.Name = "UI smoke three copy sources";
            first.DestinationPath = Path.Combine(fixture, "backup");
            first.RestoreSourcePath = Path.Combine(fixture, "restore-source");
            first.RestoreDestinationPath = Path.Combine(fixture, "restore-target");
            first.RestoreFolders.Add(new RestoreFolderEntry { SourcePath = first.RestoreSourcePath, DestinationPath = first.RestoreDestinationPath });
            window.SelectedRestoreEntry = first.RestoreFolders[0];
            var expectedFiles = new Dictionary<string, string>();
            first.SourceFolders.Clear();
            for (var i = 1; i <= 3; i++)
            {
                var sourceDirectory = Path.Combine(fixture, "source-" + i);
                Directory.CreateDirectory(sourceDirectory);
                var text = $"Source {i}: UI binding to real backup workflow.\n" + new string((char)('A' + i), 32 * 1024);
                File.WriteAllText(Path.Combine(sourceDirectory, "example.txt"), text);
                var targetName = "Source-" + i;
                first.SourceFolders.Add(new BackupSourceEntry { SourcePath = sourceDirectory, TargetFolderName = targetName });
                expectedFiles.Add(Path.Combine(first.DestinationPath, targetName, "example.txt"), text);
            }
            FlushBindings();
            var originalSources = first.SourceFolders.Select(s => (s.SourcePath, s.TargetFolderName)).ToArray();
            var originalDestination = first.DestinationPath;
            var restoreButton = FindRadio(content, "IsRestore");
            var backupButton = FindRadio(content, "IsBackup");
            restoreButton.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            FlushBindings();
            Check(window.IsRestore, "Restore radio switches operation mode through binding");
            Check(IsCollapsed(combo), "parallel control is collapsed by its Restore-mode ancestor");
            window.ActiveDestinationPath = Path.Combine(fixture, "restore-target-edited");
            Equal(originalDestination, first.DestinationPath, "editing Restore destination preserves backup destination");
            Equal(4, first.MaxParallelSources, "Restore mode preserves backup parallel limit");
            Check(originalSources.SequenceEqual(first.SourceFolders.Select(s => (s.SourcePath, s.TargetFolderName))), "Restore mode preserves all backup source fields");
            backupButton.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            FlushBindings();
            Check(window.IsBackup && !IsCollapsed(combo), "Backup radio restores the parallel control");
            Equal(4, (int)combo.SelectedItem, "returning from Restore keeps the selected limit");

            var previewButton = FindButton(content, b => BindingOperations.GetBindingExpression(b, IsEnabledProperty)?.ParentBinding.Path.Path == "CanRun");
            var runButton = FindButton(content, b => BindingOperations.GetBindingExpression(b, ContentControl.ContentProperty)?.ParentBinding.Path.Path == "RunButtonText");
            Check(previewButton.IsEnabled && runButton.IsEnabled, "real preview and backup buttons accept the fixture");
            previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, previewButton));
            Check(window.IsBusy, "real preview event enters asynchronous busy state");
            FlushBindings();
            Check(!combo.IsEnabled, "asynchronous preview disables the parallel ComboBox");
            PumpUntil(() => !window.IsBusy, TimeSpan.FromSeconds(30));
            Equal("3", window.NewCount, "preview finds files from all three sources");
            Check(!Directory.Exists(first.DestinationPath), "preview does not create backup destination");
            Check(window.LogText.Contains("同時実行数: 4", StringComparison.Ordinal), "preview log reports the selected engine plan limit four");
            Check(combo.IsEnabled, "parallel control re-enables after preview");

            VerifyLayout(renderHost, combo, 980, 900);
            VerifyLayout(renderHost, combo, 1160, 1000);
            if (snapshot is not null)
            {
                Render(renderHost, snapshot, 1160, 1000);
                Console.WriteLine("OFFSCREEN_RENDER " + snapshot);
            }

            combo.SelectedItem = 1;
            FlushBindings();
            runButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, runButton));
            Check(window.IsBusy, "real backup event enters asynchronous busy state");
            FlushBindings();
            Check(!combo.IsEnabled, "real running backup disables the parallel ComboBox");
            PumpUntil(() => !window.IsBusy, TimeSpan.FromSeconds(30));
            Check(window.LogText.Contains("同時実行数: 1", StringComparison.Ordinal), "real backup receives the selected engine plan limit one");
            foreach (var file in expectedFiles) Equal(file.Value, File.ReadAllText(file.Key), "copied data matches " + Path.GetRelativePath(first.DestinationPath, file.Key));
            Equal(100d, window.ProgressPercent, "successful backup reaches 100 percent");
            Check(window.StatusText.Contains("完了", StringComparison.Ordinal), "successful backup publishes completion status");
            Check(first.LastRun.Contains("完了", StringComparison.Ordinal), "successful backup updates profile completion state");
            Check(combo.IsEnabled, "parallel control re-enables after real backup");

            combo.SelectedItem = 4;
            FlushBindings();
            File.AppendAllText(Path.Combine(first.SourceFolders[1].SourcePath, "example.txt"), "\nSecond execution update.");
            runButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, runButton));
            Check(window.IsBusy, "changed-file execution enters actual busy state");
            PumpUntil(() => !window.IsBusy, TimeSpan.FromSeconds(30));
            Check(window.LogText.Contains("同時実行数: 4", StringComparison.Ordinal), "next real backup receives changed limit four");
            Equal("1", window.UpdatedCount, "second execution reports exactly one changed file");
            Equal("2", window.UnchangedCount, "second execution skips the two unchanged files");
            Equal(File.ReadAllText(Path.Combine(first.SourceFolders[1].SourcePath, "example.txt")), File.ReadAllText(Path.Combine(first.DestinationPath, "Source-2", "example.txt")), "second execution copies changed content");
            Equal(100d, window.ProgressPercent, "second execution completes progress");
            Check(window.StatusText.Contains("完了", StringComparison.Ordinal), "second execution completes successfully");

            restoreButton.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            FlushBindings();
            var restoreGrid = (DataGrid)window.FindName("RestoreFoldersGrid");
            first.RestoreFolders.Clear();
            FindButton(content, b => Equals(b.Content, "バックアップ設定から追加")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(3, first.RestoreFolders.Count, "backup configuration imports one restore pair per source");
            Check(first.RestoreFolders.Select(entry => (entry.SourcePath, entry.DestinationPath)).SequenceEqual(
                first.SourceFolders.Select(entry => (Path.Combine(first.DestinationPath, entry.TargetFolderName), entry.SourcePath))), "import reverses backup paths exactly");
            FindButton(content, b => Equals(b.Content, "バックアップ設定から追加")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(3, first.RestoreFolders.Count, "repeat import does not duplicate pairs");
            FindButton(content, b => Equals(b.Content, "組を追加")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(4, first.RestoreFolders.Count, "add creates one editable pair");
            Check(!window.CanRun, "incomplete restore row prevents preview");
            FlushBindings();
            FindButton(content, b => Equals(b.Content, "一覧から削除") && BindingOperations.GetBindingExpression(b, IsEnabledProperty)?.ParentBinding.Path.Path == "CanRemoveRestore")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(3, first.RestoreFolders.Count, "remove unregisters selected pair");
            first.RestoreFolders.RemoveAt(2);
            for (var index = 0; index < first.RestoreFolders.Count; index++) first.RestoreFolders[index].DestinationPath = Path.Combine(fixture, "recovered-" + index);
            FlushBindings();
            Equal(2, restoreGrid.Items.Count, "restore grid binds both paired rows");
            Check(window.CanRun && !window.CanExecute, "restore requires fresh preview");
            VerifyFivePairLayouts(renderHost, restoreGrid, first, window, fixture, snapshot);
            VerifyRestoreLayout(renderHost, restoreGrid, 980, 900);
            VerifyRestoreLayout(renderHost, restoreGrid, 1160, 1000);
            previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, previewButton));
            PumpUntil(() => !window.IsBusy, TimeSpan.FromSeconds(30));
            Equal("2", window.NewCount, "restore preview aggregates both pairs");
            Check(window.CanExecute, "reviewed paired restore enables execute");
            Check(first.RestoreFolders.All(entry => !Directory.Exists(entry.DestinationPath)), "multi-restore preview writes no targets");
            Check(window.PreviewRows.Select(row => row.RelativePath).SequenceEqual(new[] { "[1] example.txt", "[2] example.txt" }), "preview paths identify paired rows");
            window.SelectedRestoreEntry = first.RestoreFolders[1];
            Check(window.CanExecute, "row selection keeps reviewed plan");
            first.RestoreFolders[0].DestinationPath = Path.Combine(fixture, "recovered-edited");
            Check(!window.CanExecute && window.PreviewRows.Count == 0, "editing any pair invalidates review");
            previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, previewButton));
            PumpUntil(() => !window.IsBusy, TimeSpan.FromSeconds(30));
            Check(window.CanExecute, "paired restore can be reviewed again");
            var restorePlan = new ManualBackup.Core.MultiRestoreEngine().CreatePlanAsync(first.RestoreFolders.Select(entry =>
                new ManualBackup.Core.RestoreFolderPair(entry.SourcePath, entry.DestinationPath)).ToArray()).GetAwaiter().GetResult();
            var confirmation = new RestoreConfirmationWindow(restorePlan);
            FlushBindings();
            var confirmationGrid = Descendants(confirmation).OfType<DataGrid>().Single();
            Equal(2, confirmationGrid.Items.Count, "confirmation lists every reviewed mapping");
            confirmation.Close();
            first.RestoreFolders.Add(new RestoreFolderEntry());
            Check(!window.CanExecute && !window.CanRun, "adding a row clears review and requires complete paths");
            first.RestoreFolders.RemoveAt(2);
            backupButton.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            FlushBindings();
            Check(IsCollapsed(restoreGrid) && window.IsBackup, "backup mode hides paired restore controls");

            window.Close();
            window = null;
            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(settingsDirectory, "settings.json")));
            var savedProfiles = saved.RootElement.GetProperty("Profiles").EnumerateArray().ToArray();
            Equal(4, savedProfiles.Single(p => p.GetProperty("Id").GetString() == first.Id).GetProperty("MaxParallelSources").GetInt32(), "first UI selection is persisted");
            Equal(1, savedProfiles.Single(p => p.GetProperty("Id").GetString() == second.Id).GetProperty("MaxParallelSources").GetInt32(), "second UI selection remains independently persisted");
            Equal(2, savedProfiles.Single(p => p.GetProperty("Id").GetString() == first.Id).GetProperty("RestoreFolders").GetArrayLength(), "paired restore rows are persisted");
            Check(DispatcherErrors.Count == 0, "no unhandled WPF dispatcher exceptions");
            Console.WriteLine($"PASS {_checks} in-process WPF UI checks. No window was shown and no external desktop was controlled.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL in-process WPF UI smoke\n" + ex);
            foreach (var dispatcherError in DispatcherErrors) Console.Error.WriteLine("DISPATCHER " + dispatcherError);
            return 1;
        }
        finally
        {
            if (window is not null && !window.IsBusy) window.Close();
            app?.Shutdown();
            Environment.SetEnvironmentVariable("BACKUPDESK_DATA_DIR", previousDataDirectory);
            if (Directory.Exists(fixture))
            {
                var verified = Path.GetFullPath(fixture);
                if (!verified.StartsWith(qaRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup escaped .qa.");
                Directory.Delete(verified, true);
            }
        }
    }

    private static readonly DependencyProperty IsEnabledProperty = UIElement.IsEnabledProperty;

    private static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
        _checks++;
        Console.WriteLine("PASS " + label);
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected {Abbreviate(expected)}, actual {Abbreviate(actual)}");
        Check(true, label);
    }

    private static string Abbreviate<T>(T value)
    {
        var text = value?.ToString() ?? "<null>";
        return text.Length <= 120 ? text : text[..120] + "...";
    }

    private static void FlushBindings() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void PumpUntil(Func<bool> completed, TimeSpan timeout)
    {
        var frame = new DispatcherFrame();
        var stopwatch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (completed() || stopwatch.Elapsed >= timeout || DispatcherErrors.Count > 0) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (DispatcherErrors.Count > 0) throw new AggregateException(DispatcherErrors);
        if (!completed()) throw new TimeoutException("Actual asynchronous UI operation did not complete in " + timeout);
        FlushBindings();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private static Button FindButton(FrameworkElement content, Func<Button, bool> predicate) => Descendants(content).OfType<Button>().Single(predicate);

    private static RadioButton FindRadio(FrameworkElement content, string path) => Descendants(content).OfType<RadioButton>().Single(b => BindingOperations.GetBindingExpression(b, ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path == path);

    private static bool IsCollapsed(DependencyObject control)
    {
        for (DependencyObject? current = control; current is not null; current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current))
        {
            // The intentionally unshown host Window is itself Collapsed.
            // Inspect the application's content visibility, not that host.
            if (current is Window) break;
            if (current is UIElement element && element.Visibility == Visibility.Collapsed) return true;
        }
        return false;
    }

    private static void VerifyLayout(FrameworkElement content, ComboBox combo, int width, int height)
    {
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var bounds = combo.TransformToAncestor(content).TransformBounds(new Rect(combo.RenderSize));
        Check(bounds.Width >= 50 && bounds.Height >= 20, $"parallel control is laid out at {width} by {height}");
        Check(bounds.Left >= 0 && bounds.Right <= width && bounds.Top >= 0 && bounds.Bottom <= height, $"parallel control fits inside {width} by {height} content");
        var row = (FrameworkElement)LogicalTreeHelper.GetParent(LogicalTreeHelper.GetParent(combo));
        Check(row.DesiredSize.Width <= row.ActualWidth + 1, $"parallel control row is not clipped at {width} by {height}");
    }

    private static void VerifyRestoreLayout(FrameworkElement content, DataGrid grid, int width, int height)
    {
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bounds = grid.TransformToAncestor(content).TransformBounds(new Rect(grid.RenderSize));
        Check(bounds.Width > 500 && bounds.Height >= 150 && bounds.Right <= width && bounds.Bottom <= height, $"paired restore table fits {width} by {height}");
        var controls = VisualDescendants(grid).OfType<TextBox>().Where(box => BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.ParentBinding.Path.Path == "DestinationPath").ToArray();
        Check(controls.Length >= 2 && controls.All(box => box.ActualWidth > 100), $"paired row destination editors render at {width} by {height}");
    }

    private static void VerifyFivePairLayouts(FrameworkElement content, DataGrid grid, BackupProfile profile,
        MainWindow window, string fixture, string? snapshot)
    {
        var originalRows = profile.RestoreFolders.ToArray();
        var originalSelection = window.SelectedRestoreEntry;
        RestoreFolderEntry LayoutPair(int index)
        {
            var source = Path.Combine(fixture, "layout-source-" + index);
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "layout-preview.txt"), "Preview row " + index);
            return new RestoreFolderEntry
            {
                SourcePath = source,
                DestinationPath = Path.Combine(fixture, "layout-target-" + index)
            };
        }
        void PreviewLayoutRows()
        {
            FlushBindings();
            FindButton(content, button =>
                BindingOperations.GetBindingExpression(button, IsEnabledProperty)?.ParentBinding.Path.Path == "CanRun")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => !window.IsBusy, TimeSpan.FromSeconds(30));
            Check(window.CanExecute && window.PreviewRows.Count == profile.RestoreFolders.Count,
                "layout fixture has a real preview row for every restore pair");
        }
        try
        {
            for (var index = profile.RestoreFolders.Count; index < 5; index++)
                profile.RestoreFolders.Add(LayoutPair(index));
            PreviewLayoutRows();
            VerifyFivePairLayout(content, grid, 964, 861,
                snapshot is null ? null : Path.ChangeExtension(snapshot, null) + "-restore-five-minimum.png");
            VerifyFivePairLayout(content, grid, 980, 900, null);
            VerifyFivePairLayout(content, grid, 1144, 961,
                snapshot is null ? null : Path.ChangeExtension(snapshot, null) + "-restore-five.png");
            VerifyFivePairLayout(content, grid, 1160, 1000, null);

            // The shared focused-button template uses a two-pixel border.
            // Exercise that larger desired size without needing desktop focus.
            var previewButton = FindButton(content, button =>
                BindingOperations.GetBindingExpression(button, IsEnabledProperty)?.ParentBinding.Path.Path == "CanRun");
            var previousBorder = previewButton.BorderThickness;
            try
            {
                previewButton.SetCurrentValue(Control.BorderThicknessProperty, new Thickness(2));
                VerifyFivePairLayout(content, grid, 964, 861, null);
                Check(true, "restore layout accommodates the focused action-button border");
            }
            finally { previewButton.SetCurrentValue(Control.BorderThicknessProperty, previousBorder); }

            // A sixth row must scroll rather than compressing all the editors.
            profile.RestoreFolders.Add(LayoutPair(5));
            PreviewLayoutRows();
            VerifyFivePairLayout(content, grid, 964, 861, null);
            grid.ScrollIntoView(profile.RestoreFolders[^1]);
            content.UpdateLayout();
            FlushBindings();
            var sixth = grid.ItemContainerGenerator.ContainerFromIndex(5) as DataGridRow;
            Check(sixth is not null && IsWithin(sixth, RestoreViewport(grid)), "sixth restore pair scrolls fully into view");
        }
        finally
        {
            profile.RestoreFolders.Clear();
            foreach (var row in originalRows) profile.RestoreFolders.Add(row);
            window.SelectedRestoreEntry = originalSelection;
            FlushBindings();
            content.Measure(new Size(1160, 1000));
            content.Arrange(new Rect(0, 0, 1160, 1000));
            content.UpdateLayout();
        }
    }

    private static void VerifyFivePairLayout(FrameworkElement content, DataGrid grid, int width, int height, string? snapshot)
    {
        var scroll = VisualDescendants(grid).OfType<ScrollViewer>().First();
        scroll.ScrollToTop();
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        FlushBindings();
        if (snapshot is not null)
        {
            Render(content, snapshot, width, height);
            Console.WriteLine("OFFSCREEN_RESTORE_RENDER " + snapshot);
        }
        var sizeLabel = $"{width} by {height} with {grid.Items.Count} pairs";
        var rows = Enumerable.Range(0, 5)
            .Select(index => grid.ItemContainerGenerator.ContainerFromIndex(index) as DataGridRow).ToArray();
        Check(rows.All(row => row is not null), "first five restore rows are realized at " + sizeLabel);
        var viewport = RestoreViewport(grid);
        Check(rows.All(row => IsWithin(row!, viewport)), "five complete restore pairs fit the viewport at " + sizeLabel);
        foreach (var row in rows.Cast<DataGridRow>())
        {
            var editors = VisualDescendants(row).OfType<TextBox>().ToArray();
            var browse = VisualDescendants(row).OfType<Button>().ToArray();
            Check(editors.Length == 2 && editors.All(box => box.ActualWidth > 100 && box.ActualHeight >= 28 && IsWithin(box, row)),
                $"pair {row.GetIndex() + 1} has two unclipped path editors at {sizeLabel}");
            Check(browse.Length == 2 && browse.All(button => button.ActualWidth >= 40 && button.ActualHeight >= 30 && IsWithin(button, row)),
                $"pair {row.GetIndex() + 1} has two usable browse buttons at {sizeLabel}");
        }
        var preview = VisualDescendants(content).OfType<DataGrid>().Single(candidate =>
            BindingOperations.GetBindingExpression(candidate, ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path == "PreviewRows");
        var previewButton = FindButton(content, button =>
            BindingOperations.GetBindingExpression(button, IsEnabledProperty)?.ParentBinding.Path.Path == "CanRun");
        var runButton = FindButton(content, button =>
            BindingOperations.GetBindingExpression(button, ContentControl.ContentProperty)?.ParentBinding.Path.Path == "RunButtonText");
        var status = Descendants(content).OfType<TextBlock>().Single(block =>
            BindingOperations.GetBindingExpression(block, TextBlock.TextProperty)?.ParentBinding.Path.Path == "StatusText");
        var footerButton = FindButton(content, button =>
            BindingOperations.GetBindingExpression(button, ContentControl.ContentProperty)?.ParentBinding.Path.Path == "OpenDestinationText");
        var previewPanel = VisualAncestors(preview).OfType<Border>().First(border =>
            Grid.GetRow(border) == 3 && VisualTreeHelper.GetParent(border) is Grid);
        var progress = VisualDescendants(content).OfType<ProgressBar>().Single();
        var footerPanel = VisualAncestors(progress).OfType<StackPanel>().First();
        var previewBounds = previewPanel.TransformToAncestor(content).TransformBounds(new Rect(previewPanel.RenderSize));
        var footerBounds = footerPanel.TransformToAncestor(content).TransformBounds(new Rect(footerPanel.RenderSize));
        var previewSlot = LayoutInformation.GetLayoutSlot(previewPanel);
        Console.WriteLine($"EVIDENCE restore-layout {sizeLabel}: contentDesired={content.DesiredSize}; previewBounds={previewBounds}; previewSlot={previewSlot}; previewGridHeight={preview.ActualHeight:F2}; footerBounds={footerBounds}");
        Check(IsWithin(previewButton, content) && IsWithin(runButton, content), "restore action buttons remain visible at " + sizeLabel);
        Check(preview.ActualHeight >= 64 && IsWithin(preview, content), "restore preview remains usable and visible at " + sizeLabel);
        Check(previewPanel.ActualHeight <= previewSlot.Height + 0.5 && previewBounds.Bottom <= footerBounds.Top + 0.5,
            "restore preview is not clipped by its grid row or overlapped by footer at " + sizeLabel);
        var previewRow = preview.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow;
        var previewViewport = RestoreViewport(preview);
        Console.WriteLine($"EVIDENCE restore-preview {sizeLabel}: viewportHeight={previewViewport.ActualHeight:F2}; rowHeight={previewRow?.ActualHeight:F2}");
        Check(previewRow is not null && IsWithin(previewRow, previewViewport),
            "at least one complete real preview row remains visible at " + sizeLabel);
        Check(IsWithin(status, content) && IsWithin(footerButton, content), "restore status and footer remain visible at " + sizeLabel);
    }

    private static FrameworkElement RestoreViewport(DataGrid grid) =>
        VisualDescendants(grid).OfType<ScrollContentPresenter>().First();

    private static bool IsWithin(FrameworkElement child, FrameworkElement ancestor)
    {
        var bounds = child.TransformToAncestor(ancestor).TransformBounds(new Rect(child.RenderSize));
        return child.ActualWidth > 0 && child.ActualHeight > 0 && bounds.Left >= -0.5 && bounds.Top >= -0.5 &&
            bounds.Right <= ancestor.ActualWidth + 0.5 && bounds.Bottom <= ancestor.ActualHeight + 0.5;
    }

    private static IEnumerable<DependencyObject> VisualAncestors(DependencyObject node)
    {
        for (var current = VisualTreeHelper.GetParent(node); current is not null; current = VisualTreeHelper.GetParent(current))
            yield return current;
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject node)
    {
        yield return node;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            foreach (var child in VisualDescendants(VisualTreeHelper.GetChild(node, index))) yield return child;
    }

    private static void Render(FrameworkElement content, string output, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(output);
        encoder.Save(stream);
        Check(stream.Length > 1000, "offscreen WPF content renders to a nonempty PNG");
    }
}
