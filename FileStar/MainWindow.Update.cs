using System.Windows;

namespace FileStar;

/// <summary>도움말 > 업데이트 확인 / FileStar 정보, and the quiet check a few seconds after start-up.</summary>
public partial class MainWindow
{
    bool _checkingForUpdates;

    /// <param name="quiet">The start-up check: says nothing unless an update is ready.</param>
    async Task CheckForUpdatesAsync(bool quiet)
    {
        if (_checkingForUpdates)
            return;
        if (!Updater.IsInstalled)
        {
            if (!quiet)
                MessageBox.Show(this, "이 FileStar는 설치 프로그램(FileStar-win-Setup.exe)으로 설치한 것이 아니라서 자동 업데이트를 할 수 없습니다.\n\n" +
                                      $"새 버전은 {Updater.RepoUrl}/releases 에서 받을 수 있습니다.",
                    AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _checkingForUpdates = true;
        try
        {
            if (quiet)
                await Task.Delay(TimeSpan.FromSeconds(5));
            else
                ShowStatusNote("업데이트 확인 중...");
            var update = Updater.Ready ?? await Updater.CheckAndDownloadAsync(percent =>
                Dispatcher.BeginInvoke(() => ShowStatusNote($"새 버전 받는 중... {percent}%")));
            if (update == null)
            {
                if (!quiet)
                {
                    ShowStatusNote("최신 버전입니다.");
                    MessageBox.Show(this, $"최신 버전을 사용하고 있습니다. (FileStar {Updater.CurrentVersion})", AppPaths.AppName,
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }
            string version = update.TargetFullRelease.Version.ToString();
            ShowStatusNote($"FileStar {version} 업데이트 준비됨 — 닫을 때 설치됩니다.");
            var answer = MessageBox.Show(this,
                $"새 버전 FileStar {version}을(를) 받았습니다. (지금 {Updater.CurrentVersion})\n\n" +
                "지금 다시 시작해서 설치할까요?\n'아니요'를 누르면 FileStar를 닫을 때 설치됩니다.",
                AppPaths.AppName, MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
            {
                // Save every window's state first: the update closes FileStar.
                foreach (var window in Application.Current.Windows.OfType<MainWindow>())
                    window.SaveBeforeUpdate();
                Updater.ApplyAndRestart();
            }
        }
        catch (Exception ex)
        {
            if (!quiet)
                MessageBox.Show(this, "업데이트를 확인할 수 없습니다: " + ex.Message, AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _checkingForUpdates = false;
        }
    }

    void SaveBeforeUpdate()
    {
        FavoritesStore.Save(notify: false);
        ResolvePendingNoteEdit();
        CommitNote();
        _saveTimer.Stop();
        SaveSettings();
    }

    void CheckForUpdates_Click(object sender, RoutedEventArgs e) => _ = CheckForUpdatesAsync(quiet: false);

    void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, $"FileStar {Updater.CurrentVersion}\n\n{Updater.RepoUrl}\n\n" +
                              (Updater.IsInstalled ? "자동 업데이트: 켜짐 (시작할 때 새 버전 확인)" : "자동 업데이트: 꺼짐 (설치 프로그램으로 설치하지 않음)"),
            "FileStar 정보", MessageBoxButton.OK, MessageBoxImage.Information);
}
