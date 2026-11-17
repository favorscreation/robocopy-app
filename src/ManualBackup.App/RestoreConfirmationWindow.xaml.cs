using System.Windows;
using ManualBackup.Core;

namespace ManualBackup.App;

public partial class RestoreConfirmationWindow : Window
{
    public RestoreConfirmationWindow(MultiRestorePlan plan)
    {
        InitializeComponent();
        DataContext = new
        {
            Summary = $"{plan.Entries.Count:N0} 組 / 追加 {plan.Files.Count(file => file.Kind == BackupChangeKind.New):N0} 件 / 上書き {plan.Files.Count(file => file.Kind == BackupChangeKind.Updated):N0} 件 / {MainWindow.FormatSize(plan.TotalBytes)}",
            Rows = plan.Entries.Select((entry, index) => new
            {
                Number = index + 1, entry.SourcePath, entry.DestinationPath,
                Added = entry.Files.Count(file => file.Kind == BackupChangeKind.New),
                Updated = entry.Files.Count(file => file.Kind == BackupChangeKind.Updated)
            }).ToArray()
        };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
