using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    public ObservableCollection<DownloadJob> DownloadJobs { get; } = [];
    public ObservableCollection<DownloadJob> QueueDownloads { get; } = [];
    public ObservableCollection<DownloadJob> FailedDownloads { get; } = [];
    private ObservableCollection<DownloadJob> VisibleDownloads => DownloadScope.SelectedIndex == 1 ? FailedDownloads : QueueDownloads;
    private DownloadedLibrary _downloadedLibrary = DownloadedLibrary.Shared;
    private readonly SemaphoreSlim _downloadSlots = new(2);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, SemaphoreSlim> _downloadSongGates = new();
    private static readonly HttpClient DownloadClient = new(new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.None }) { Timeout = TimeSpan.FromMinutes(10) };
    private static string DownloadStatePath => Path.Combine(SettingsStore.DataDirectory, "downloads.json");
    private bool _downloadBulkBusy;
    private readonly HashSet<DownloadJob> _observedDownloads = [];
    private void InitializeDownloads()
    {
        DownloadJobs.CollectionChanged += DownloadCollectionChanged;
        try
        {
            if (!File.Exists(DownloadStatePath) || new FileInfo(DownloadStatePath).Length > 8 * 1024 * 1024) return;
            foreach (var job in (JsonSerializer.Deserialize<List<DownloadJob>>(File.ReadAllText(DownloadStatePath)) ?? []).Take(1000))
            {
                if (job.Active) job.Status = "已暂停";
                if (job.Finished && File.Exists(job.FilePath))
                {
                    if (AudioQuality.Rank(job.ActualLevel) < 0) job.ActualLevel = Qualities.FirstOrDefault(q => q.Name == job.ActualQuality).Level ?? "";
                    if (AudioQuality.Rank(job.ActualLevel) >= 0 && _downloadedLibrary.FindPath(job.FilePath) == null) _downloadedLibrary.Register(job);
                    job.AudioReady = true;
                }
                DownloadJobs.Add(job);
            }
        }
        catch (Exception ex) { Log.Write(ex); }
        finally { UpdateDownloadLists(); }
    }
    private void DownloadCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        foreach (var old in _observedDownloads.Where(j => !DownloadJobs.Contains(j)).ToArray()) { old.PropertyChanged -= DownloadChanged; _observedDownloads.Remove(old); }
        foreach (var job in DownloadJobs) if (_observedDownloads.Add(job)) job.PropertyChanged += DownloadChanged;
        UpdateDownloadLists();
    }
    private void DownloadChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DownloadJob.Status)) UpdateDownloadLists();
    }
    private void UpdateDownloadLists()
    {
        if (DownloadScope == null || DownloadList == null || FailedDownloadsList == null || DownloadSummary == null || ClearDownloadsButton == null) return;
        var failed = DownloadScope.SelectedIndex == 1;
        static void Sync(ObservableCollection<DownloadJob> list, DownloadJob[] items)
        {
            foreach (var old in list.Where(j => !items.Contains(j)).ToArray()) list.Remove(old);
            for (var i = 0; i < items.Length; i++) if (i >= list.Count || !ReferenceEquals(list[i], items[i])) { list.Remove(items[i]); list.Insert(i, items[i]); }
        }
        Sync(QueueDownloads, DownloadJobs.Where(j => j.Status != "失败").ToArray());
        Sync(FailedDownloads, DownloadJobs.Where(j => j.Status == "失败").ToArray());
        DownloadList.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
        FailedDownloadsList.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        var visible = VisibleDownloads.ToArray();
        DownloadEmpty.Text = failed ? "没有失败的下载" : "暂无下载任务"; DownloadEmpty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadSummary.Text = failed ? $"{visible.Length} 项失败" : $"{DownloadJobs.Count(j => j.Active)} 项进行中，{DownloadJobs.Count(j => j.Status == "已暂停")} 项已暂停，{DownloadJobs.Count(j => j.Finished)} 项已完成，{FailedDownloads.Count} 项已失败";
        PauseAllDownloadsButton.Visibility = ResumeAllDownloadsButton.Visibility = CancelAllDownloadsButton.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
        RetryFailedDownloadsButton.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        PauseAllDownloadsButton.IsEnabled = !_downloadBulkBusy && DownloadJobs.Any(j => j.Active);
        ResumeAllDownloadsButton.IsEnabled = !_downloadBulkBusy && DownloadJobs.Any(j => j.Status == "已暂停");
        CancelAllDownloadsButton.IsEnabled = !_downloadBulkBusy && DownloadJobs.Any(j => j.Active || j.Status == "已暂停");
        RetryFailedDownloadsButton.IsEnabled = !_downloadBulkBusy && DownloadJobs.Any(j => j.Status == "失败");
        ClearDownloadsButton.Content = failed ? "清除失败记录" : "清空全部队列";
        ClearDownloadsButton.IsEnabled = !_downloadBulkBusy && (failed ? FailedDownloads.Count > 0 : DownloadJobs.Count > 0);
    }
    private void DownloadScope_Changed(object sender, SelectionChangedEventArgs args) => UpdateDownloadLists();
    private void SaveDownloads()
    {
        if (_runningSmokeTest) return;
        try { Directory.CreateDirectory(SettingsStore.DataDirectory); File.WriteAllText(DownloadStatePath + ".tmp", JsonSerializer.Serialize(DownloadJobs)); File.Move(DownloadStatePath + ".tmp", DownloadStatePath, true); }
        catch (Exception ex) { Log.Write(ex); }
    }
    private async void ShowDownloadOptions(FrameworkElement anchor, IReadOnlyList<Track> tracks) => await ConfirmDownloadsAsync(tracks);
    private async Task ConfirmDownloadsAsync(IReadOnlyList<Track> tracks, bool upgradeExisting = true)
    {
        if (_dialog != null) return;
        var singleExisting = upgradeExisting && tracks.Count == 1 ? DownloadForTrack(tracks[0]) : null;
        tracks = tracks.Select(t => t.IsLocal ? SourceSongId(t) > 0 ? t.OnlineSource(SourceSongId(t)) : null : t).OfType<Track>().DistinctBy(t => t.Id).ToArray();
        if (tracks.Count == 0) { ShowPlayerMessage("歌曲已在本机"); return; }
        var upgrade = singleExisting != null;
        var choices = Qualities.Where(q => !upgrade || AudioQuality.Rank(q.Level) > AudioQuality.Rank(singleExisting!.Level)).Reverse().ToArray();
        if (choices.Length == 0)
        {
            var complete = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "已下载", Content = "这首歌曲已下载" + AudioQuality.Name(singleExisting!.Level) + "，已是最高音质。", CloseButtonText = "关闭" };
            _dialog = complete; try { await ShowThemedDialogAsync(complete); } finally { _dialog = null; } return;
        }
        var quality = new ComboBox { Header = upgrade ? "升级至" : "目标最高音质", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 44 };
        foreach (var item in choices) quality.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item.Level });
        SelectTag(quality, _settings.DownloadQuality);
        if (quality.SelectedIndex < 0) quality.SelectedIndex = 0;
        var body = new StackPanel { Width = 370, Spacing = 14 };
        body.Children.Add(new TextBlock { Text = tracks.Count == 1 ? tracks[0].Title : $"下载 {tracks.Count} 首歌曲", TextWrapping = TextWrapping.Wrap, MaxLines = 2 });
        body.Children.Add(quality);
        body.Children.Add(new TextBlock { Text = upgrade ? "当前已下载" + AudioQuality.Name(singleExisting!.Level) + "。只保存更高音质，原文件保留。" : "缺少目标音质时，下载不高于目标的最高可用音质。", TextWrapping = TextWrapping.Wrap, Opacity = .65 });
        body.Children.Add(new TextBlock { Text = "来源：" + (_settings.DownloadSource == "legacy" ? "MusicUnlock 同款网易直连" : "网易下载接口"), TextWrapping = TextWrapping.Wrap, Opacity = .65 });
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = upgrade ? "升级音质" : "下载歌曲", Content = body, PrimaryButtonText = upgrade ? "升级下载" : "开始下载", CloseButtonText = "取消" };
        _dialog = dialog;
        try
        {
            if (await ShowThemedDialogAsync(dialog) != ContentDialogResult.Primary) return;
            _settings.DownloadQuality = (quality.SelectedItem as ComboBoxItem)?.Tag as string ?? "lossless"; SaveSettings();
            var queued = 0;
            var targetDirectory = upgrade ? Path.GetDirectoryName(singleExisting!.Path)! : _settings.DownloadDirectory;
            foreach (var track in tracks) { if (_exiting || _downloads.IsCancellationRequested) break; if (await DownloadAsync(track, targetDirectory)) queued++; }
            ShowPlayerMessage(queued > 0 ? "已加入下载队列" : "歌曲已达到所选音质，无需重复下载");
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _dialog = null; }
    }
    private Task<bool> DownloadAsync(Track track, string? directory = null)
    {
        if (track.IsLocal) return Task.FromResult(false);
        directory ??= _settings.DownloadDirectory;
        var completed = _downloadedLibrary.Find(track.Id, directory: directory);
        if (completed != null && AudioQuality.Rank(completed.Level) >= AudioQuality.Rank(_settings.DownloadQuality)) return Task.FromResult(false);
        var existing = DownloadJobs.FirstOrDefault(j => j.Track.Id == track.Id && j.Quality == _settings.DownloadQuality && DownloadedLibrary.SameDirectory(j.Directory, directory) && !j.Finished);
        if (existing != null)
        {
            if (!existing.Active)
            {
                if (!existing.AudioReady && existing.Source != _settings.DownloadSource) { DeleteOwnedPartial(existing); existing.Source = _settings.DownloadSource; existing.Progress = 0; }
                StartDownload(existing);
            }
            return Task.FromResult(true);
        }
        var job = new DownloadJob { Track = track, Quality = _settings.DownloadQuality, Source = _settings.DownloadSource, Directory = directory, UpgradeFromLevel = completed?.Level ?? "" };
        DownloadJobs.Insert(0, job); SaveDownloads(); StartDownload(job); return Task.FromResult(true);
    }
    private void StartDownload(DownloadJob job)
    {
        if (job.Cancellation != null || _exiting || _downloadBulkBusy) return;
        if (!job.AudioReady && job.Status is "失败" or "已取消" && job.Source != _settings.DownloadSource)
        {
            DeleteOwnedPartial(job); job.TagsReady = false; job.Source = _settings.DownloadSource; job.Progress = 0;
        }
        job.Status = "等待中"; job.Error = "";
        job.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_downloads.Token);
        job.Completion = RunDownloadAsync(job, job.Cancellation);
    }
    private async Task RunDownloadAsync(DownloadJob job, CancellationTokenSource cancellation, ApiClient? client = null, HttpClient? transport = null)
    {
        var api = client ?? _api; var ct = cancellation.Token; var acquired = false; var songAcquired = false;
        var songGate = _downloadSongGates.GetOrAdd(job.Track.Id, _ => new SemaphoreSlim(1));
        try
        {
            await songGate.WaitAsync(ct); songAcquired = true;
            await _downloadSlots.WaitAsync(ct); acquired = true;
            if (job.AudioReady && File.Exists(job.FilePath)) { CompleteDownload(job); return; }
            if (job.TagsReady && File.Exists(job.PartialPath)) { ct.ThrowIfCancellationRequested(); FinishTaggedDownload(job); return; }
            var existing = _downloadedLibrary.Find(job.Track.Id, directory: job.Directory);
            if (existing != null && AudioQuality.Rank(existing.Level) >= AudioQuality.Rank(job.Quality)) { UseExistingDownload(job, existing); return; }
            var data = await DownloadResolver.ResolveAsync(api, job, ct);
            existing = _downloadedLibrary.Find(job.Track.Id, directory: job.Directory);
            if (existing != null && AudioQuality.Rank(existing.Level) >= AudioQuality.Rank(data.Text("level"))) { UseExistingDownload(job, existing); return; }
            var uri = new Uri(data.Text("url")); var extension = data.Text("type");
            job.ActualLevel = data.Text("level"); job.ActualQuality = AudioQuality.Name(job.ActualLevel);
            var name = string.Concat($"{job.Track.Artist} - {job.Track.Title}".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.'); name = name[..Math.Min(100, name.Length)];
            Directory.CreateDirectory(job.Directory);
            var path = Path.Combine(job.Directory, $"{name} ({job.Track.Id}) [{job.ActualLevel}].{extension}");
            var partial = Path.Combine(job.Directory, $".{name}-{job.Track.Id}-{job.Quality}.part.{extension}");
            if (job.PartialPath.Length > 0 && !string.Equals(job.PartialPath, partial, StringComparison.OrdinalIgnoreCase)) DeleteOwnedPartial(job);
            job.FilePath = path; job.PartialPath = partial;
            if (File.Exists(path))
            {
                var completed = _downloadedLibrary.FindPath(path);
                if (completed?.Path.Equals(path, StringComparison.OrdinalIgnoreCase) == true) { job.AudioReady = true; CompleteDownload(job); return; }
                throw new IOException("目标文件已存在，请打开下载文件夹确认；未覆盖原文件");
            }
            job.Status = "下载中"; SaveDownloads();
            await TransferDownloadAsync(uri, partial, data.Number("size"), data.Text("md5"), job, ct, transport);
            job.Status = "写入信息";
            var lyrics = await api.CallCachedAsync("lyric", new { id = job.Track.Id }, ct); var lyric = Object(lyrics, "lrc").Text("lyric"); var translatedLyric = Object(lyrics, "tlyric").Text("lyric");
            byte[]? cover = null;
            if (job.Track.Cover.Length > 0) { var image = await Artwork.GetFileAsync(job.Track.Cover, 1024, ct); cover = await File.ReadAllBytesAsync(image, ct); }
            await Task.Run(() => AudioTags.Write(partial, job.Track.Title, job.Track.Artist, job.Track.Album, lyric, cover, job.Track.Id, job.ActualLevel, job.Track.ArtistId, job.Track.AlbumId, translatedLyric), ct);
            job.TagsReady = true; SaveDownloads();
            ct.ThrowIfCancellationRequested(); FinishTaggedDownload(job);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { job.Status = "已暂停"; }
        catch (OperationCanceledException) { job.Error = "请求超时，请重试"; job.Status = "失败"; }
        catch (Exception ex) { job.Error = ex.Message; job.Status = "失败"; }
        finally { if (acquired) _downloadSlots.Release(); if (songAcquired) songGate.Release(); job.Cancellation = null; cancellation.Dispose(); SaveDownloads(); UpdateDownloadLists(); }
    }
    private void UseExistingDownload(DownloadJob job, DownloadedFile existing)
    {
        DeleteOwnedPartial(job); job.FilePath = existing.Path; job.Directory = Path.GetDirectoryName(existing.Path)!; job.ActualLevel = existing.Level; job.ActualQuality = AudioQuality.Name(existing.Level); job.AudioReady = true; CompleteDownload(job, true);
    }
    private void CompleteDownload(DownloadJob job, bool unchanged = false)
    {
        if (!unchanged && (!_runningSmokeTest || _checkingLibraryWorkflow)) _downloadedLibrary.Register(job);
        job.Progress = 100; job.Error = ""; job.Status = unchanged ? "无需升级" : "已完成";
        UpdateDownloadIndicator();
    }
    private void FinishTaggedDownload(DownloadJob job)
    {
        var partial = OwnedPartialPath(job);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(job.FilePath)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.Directory)), StringComparison.OrdinalIgnoreCase)) throw new IOException("下载目标目录已变化");
        if (File.Exists(job.FilePath) && _downloadedLibrary.FindPath(job.FilePath) is { } existing && existing.SongId == job.Track.Id && existing.Level == job.ActualLevel) File.Delete(partial);
        else File.Move(partial, job.FilePath);
        job.PartialPath = ""; job.AudioReady = true; CompleteDownload(job);
    }
    private async Task TransferDownloadAsync(Uri uri, string partial, long expectedSize, string md5, DownloadJob job, CancellationToken ct, HttpClient? transport = null)
    {
        await Task.Run(async () =>
        {
            var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (expectedSize > 0 && existing > expectedSize) { File.Delete(partial); existing = 0; }
            if (expectedSize <= 0 || existing != expectedSize)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri); if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
                using var response = await (transport ?? DownloadClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); response.EnsureSuccessStatusCode();
                var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == existing;
                if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From != (append ? existing : 0)) throw new InvalidDataException("下载分段不完整，请重试");
                long received = append ? existing : 0;
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920]; var sample = Stopwatch.StartNew();
                while (await input.ReadAsync(buffer, ct).ConfigureAwait(false) is > 0 and var count)
                {
                    await file.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false); received += count;
                    if (sample.ElapsedMilliseconds > 250) { var percent = expectedSize > 0 ? Math.Clamp(100d * received / expectedSize, 0, 100) : 0; DispatcherQueue.TryEnqueue(() => { if (!_hidden && job.Active) job.Progress = percent; }); sample.Restart(); }
                }
            }
            if (expectedSize > 0 && new FileInfo(partial).Length != expectedSize) throw new InvalidDataException("音频未下载完整");
            if (md5.Length == 32)
            {
                await using var file = File.OpenRead(partial); var digest = Convert.ToHexString(await MD5.HashDataAsync(file, ct).ConfigureAwait(false));
                if (!digest.Equals(md5, StringComparison.OrdinalIgnoreCase)) { file.Close(); File.Delete(partial); throw new InvalidDataException("音频校验失败，请重试"); }
            }
        }, ct);
    }
    private static string OwnedPartialPath(DownloadJob job)
    {
        var partial = Path.GetFullPath(job.PartialPath); var directory = Path.GetFullPath(job.Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!partial.StartsWith(directory, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(partial).StartsWith('.') || !Path.GetFileName(partial).Contains($"-{job.Track.Id}-{job.Quality}.part.", StringComparison.Ordinal)) throw new IOException("无法确认临时下载文件归属");
        return partial;
    }
    private static void DeleteOwnedPartial(DownloadJob job) { if (job.PartialPath.Length == 0) return; File.Delete(OwnedPartialPath(job)); job.PartialPath = ""; job.TagsReady = false; }
    private async Task ChangeDownloadsAsync(IReadOnlyList<DownloadJob> jobs, bool cancel, bool remove)
    {
        if (_downloadBulkBusy) return; _downloadBulkBusy = true; UpdateDownloadLists();
        try
        {
            foreach (var job in jobs) job.Cancellation?.Cancel();
            await Task.WhenAll(jobs.Where(j => j.Completion != null).Select(j => j.Completion!));
            foreach (var job in jobs)
            {
                if (cancel && !job.Finished) { DeleteOwnedPartial(job); job.Progress = 0; job.Error = ""; job.Status = "已取消"; }
                if (remove) DownloadJobs.Remove(job);
            }
            SaveDownloads();
        }
        finally { _downloadBulkBusy = false; UpdateDownloadLists(); }
    }
    private async void PauseAllDownloads_Click(object sender, RoutedEventArgs args) { try { await ChangeDownloadsAsync(DownloadJobs.Where(j => j.Active).ToArray(), false, false); } catch (Exception ex) { ShowError(ex.Message); } }
    private void ResumeAllDownloads_Click(object sender, RoutedEventArgs args) { foreach (var job in DownloadJobs.Where(j => j.Status == "已暂停").ToArray()) StartDownload(job); }
    private async void CancelAllDownloads_Click(object sender, RoutedEventArgs args) { try { await ChangeDownloadsAsync(DownloadJobs.Where(j => j.Active || j.Status == "已暂停").ToArray(), true, false); } catch (Exception ex) { ShowError(ex.Message); } }
    private void RetryFailedDownloads_Click(object sender, RoutedEventArgs args)
    {
        foreach (var job in DownloadJobs.Where(j => j.Status == "失败").ToArray())
            try { StartDownload(job); } catch (Exception ex) { job.Error = ex.Message; ShowError(ex.Message); }
    }
    private async void ClearDownloads_Click(object sender, RoutedEventArgs args)
    {
        if (_dialog != null || _downloadBulkBusy || DownloadJobs.Count == 0) return;
        var failed = DownloadScope.SelectedIndex == 1;
        var targets = failed ? FailedDownloads.ToArray() : DownloadJobs.ToArray();
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = failed ? "清除失败记录？" : "清空全部下载队列？", Content = "未完成的下载会取消，已下载的音乐文件会保留。", PrimaryButtonText = failed ? "清除记录" : "清空队列", CloseButtonText = "返回" }; _dialog = dialog;
        try { if (await ShowThemedDialogAsync(dialog) == ContentDialogResult.Primary) await ChangeDownloadsAsync(targets, true, true); }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _dialog = null; }
    }
    private async void DownloadAction_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: DownloadJob job } || _downloadBulkBusy) return;
        try { if (job.Active) job.Cancellation?.Cancel(); else if (job.Finished) { if (!await Windows.System.Launcher.LaunchFolderPathAsync(job.Directory)) ShowError("无法打开下载文件夹"); } else StartDownload(job); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private async void RemoveDownload_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: DownloadJob job }) return;
        try { await ChangeDownloadsAsync([job], true, true); } catch (Exception ex) { ShowError(ex.Message); }
    }
    private async void DownloadFolder_Click(object sender, RoutedEventArgs args)
    {
        try { Directory.CreateDirectory(_settings.DownloadDirectory); if (!await Windows.System.Launcher.LaunchFolderPathAsync(_settings.DownloadDirectory)) ShowError("无法打开下载文件夹"); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void DownloadSource_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!_ready || DownloadSourceCombo.SelectedItem is not ComboBoxItem { Tag: string source }) return;
        _settings.DownloadSource = source; SaveSettings();
    }
    private void DownloadRow_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not Grid row || row.Children.OfType<StackPanel>().ToArray() is not [var details, var actions]) return;
        var narrow = args.NewSize.Width < 600;
        Grid.SetColumnSpan(details, narrow ? 2 : 1); Grid.SetRow(actions, narrow ? 1 : 0); Grid.SetColumn(actions, narrow ? 0 : 1);
        Grid.SetColumnSpan(actions, narrow ? 2 : 1); actions.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right; actions.Margin = new Thickness(0, narrow ? 8 : 0, 0, 0);
    }
}
