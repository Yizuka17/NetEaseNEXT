using Microsoft.UI.Xaml;
using NetEaseNEXT.Models;
using Windows.Storage;
using Windows.System;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async void ContextTrackInfo_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: Track track }) await ShowSongDetailsAsync(track, SongInfoApiOverride ?? _api);
    }
    private async void ContextOpenFile_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: Track track } || ShareFilePath(track) is not { } path) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var folder = await file.GetParentAsync();
            if (folder == null) { ShowError("无法打开文件所在文件夹"); return; }
            var options = new FolderLauncherOptions(); options.ItemsToSelect.Add(file);
            if (!await Launcher.LaunchFolderAsync(folder, options)) ShowError("无法打开文件所在文件夹");
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private async void OpenLocalPlaylistFolder_Click(object sender, RoutedEventArgs args)
    {
        var id = (sender as FrameworkElement)?.DataContext is LocalPlaylistCard card ? card.Id : _localPlaylistId;
        var path = _localLibrary.Playlists.FirstOrDefault(p => p.Id == id)?.FolderPath;
        if (string.IsNullOrEmpty(path)) return;
        try { if (!Directory.Exists(path) || !await Launcher.LaunchFolderPathAsync(path)) ShowError("无法打开歌单文件夹"); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}
