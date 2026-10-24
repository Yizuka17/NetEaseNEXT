using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunTrackLocatorChecksAsync(Action<string, bool, object?> check)
    {
        _sessionReady = true; _playback.SetVolume(0); RestoreWindow();
        _settings.Theme = "Light"; _settings.Backdrop = "Solid"; ApplyAppearance();
        var scale = Root.XamlRoot.RasterizationScale;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1120 * scale), (int)(900 * scale)));
        await NavigateAsync("queue");
        var fixture = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var script = Path.Combine(fixture, "bridge.mjs");
            await File.WriteAllTextAsync(script, """
                import {createInterface} from 'node:readline';
                createInterface({input:process.stdin}).on('line',async line=>{
                  const r=JSON.parse(line),p=r.params??{};
                  await new Promise(resolve=>setTimeout(resolve,80));
                  const ids=String(p.ids??'').split(',').filter(Boolean).map(Number);
                  const data=ids.includes(9999)?{code:500,msg:'fixture locator denied'}:{code:200,songs:ids.reverse().map(id=>({id,name:'Song '+id,ar:[{id:1,name:'Artist'}],al:{id:1,name:'Album',picUrl:''},dt:60000}))};
                  process.stdout.write(JSON.stringify({id:r.id,data})+'\n');
                }).on('close',()=>process.exit(0));
                """);
            using var api = new ApiClient(script, fixture);
            void Prepare(long current, int count = 1500)
            {
                _restoringPlayback = true;
                try { _playback.Restore(new() { Queue = [new Track(current, "Song " + current, "Artist", "Album", "", 60000)], CurrentId = current }); }
                finally { _restoringPlayback = false; }
                _view = "playlist"; _playlistId = 900;
                _playlistTrackIds = Enumerable.Range(1, count).Select(i => (long)i).ToArray(); _playlistTrackCount = count;
                Tracks.Clear(); foreach (var id in _playlistTrackIds.Take(100)) Tracks.Add(new Track(id, "Song " + id, "Artist", "Album", "", 60000));
                _offset = 100; EmptyPanel.Visibility = Visibility.Collapsed; Notice.IsOpen = false;
                LoadMoreButton.Visibility = Visibility.Visible; TrackCount.Text = count + " 首"; PageTitle.Text = "长歌单定位检查";
                UpdateTrackLocator(); Root.UpdateLayout();
            }
            Prepare(1251);
            check("Locator appears before the current song's page is loaded", Tracks.Count == 100 && !Tracks.Any(t => t.Id == 1251) && TrackLocateButton.Visibility == Visibility.Visible && TrackLocateButton.IsEnabled, null);
            var requests = api.RequestCount;
            var watch = Stopwatch.StartNew(); await LocateCurrentAsync(api);
            check("Locator reaches songs beyond the old 1000-row limit", TrackList.SelectedItem is Track { Id: 1251 } && Tracks.Count == 1251 && _offset == 1251, new { elapsedMs = watch.ElapsedMilliseconds, count = Tracks.Count });
            var target = (Track)TrackList.SelectedItem;
            var viewport = Stopwatch.StartNew();
            while (TrackList.ContainerFromItem(target) == null && viewport.Elapsed < TimeSpan.FromSeconds(3)) { await Task.Delay(20); Root.UpdateLayout(); }
            var row = TrackList.ContainerFromItem(target) as FrameworkElement;
            var top = row?.TransformToVisual(TrackList).TransformPoint(new Windows.Foundation.Point()).Y ?? -1;
            check("Located song is realized inside the visible list", row != null && top >= -1 && top < TrackList.ActualHeight, new { top, height = TrackList.ActualHeight });
            check("Locator batches at most 500 IDs per request", api.RequestCount - requests == 3, null);
            check("Locator preserves playlist order and playback queue", Tracks.Select(t => t.Id).SequenceEqual(_playlistTrackIds.Take(1251)) && _playback.Queue.Count == 1 && _playback.Current?.Id == 1251 && !_playback.IsPlaying, null);
            check("More pages remain available after locating", LoadMoreButton.Visibility == Visibility.Visible && LoadMoreButton.IsEnabled && TrackLocateButton.IsEnabled, null);
            requests = api.RequestCount; await LocateCurrentAsync(api);
            check("Repeated locate reuses already loaded rows", api.RequestCount == requests && Tracks.Count == 1251, null);
            BeginTrackBatch(TrackList, Tracks[0]);
            await LocateCurrentAsync(api);
            check("Locate preserves active multiselection", TrackList.SelectedItems.Count == 1 && ((Track)TrackList.SelectedItems[0]).Id == 1, null);
            EndTrackBatch();
            await Task.Delay(150); Root.UpdateLayout();
            await SaveLibraryImageAsync(TracksPanel, "long-playlist-locator.png");
            Prepare(1400);
            var pending = LocateCurrentAsync(api); _trackLocateLoad!.Cancel(); await pending;
            check("Canceled locate preserves loaded rows and resets controls", Tracks.Count == 100 && _offset == 100 && _trackLocateLoad == null && TrackLocateButton.IsEnabled, null);
            Prepare(1401);
            var stale = LocateCurrentAsync(api); _route = new("queue", 0, 0, 0, "", "");
            await stale;
            check("Route changes reject stale locator results", Tracks.Count == 100 && _offset == 100, null);
            Prepare(1402);
            var hidden = LocateCurrentAsync(api); HideWindow(); await hidden;
            check("Hiding cancels locator requests and hides its button", _trackLocateLoad == null && Tracks.Count == 100 && TrackLocateButton.Visibility == Visibility.Collapsed, null);
            RestoreWindow(); Prepare(9999, 9999);
            _offset = 9900; Tracks.Clear(); Tracks.Add(new Track(1, "Kept song", "Artist", "Album", "", 60000));
            await LocateCurrentAsync(api);
            check("Failed locate retains data and allows retry", Tracks.Count == 1 && _offset == 9900 && Notice.IsOpen && Notice.Message == "fixture locator denied" && TrackLocateButton.IsEnabled, null);
            Prepare(1700);
            check("Other playlists do not show a false locator", TrackLocateButton.Visibility == Visibility.Collapsed, null);
        }
        finally { _trackLocateLoad?.Cancel(); EndTrackBatch(); await DeleteWorkerFixtureAsync(fixture); }
    }
}
