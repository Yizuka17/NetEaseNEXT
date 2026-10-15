using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetEaseNEXT.Models;
using NetEaseNEXT.Services;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private async Task RunEventsStartupChecksAsync(Action<string, bool, object?> check)
    {
        foreach (var method in new[] { "lyric", "song_detail", "song_music_detail", "album", "playlist_track_all", "playlist_detail", "personalized", "user_playlist", "login_status", "song_red_count", "song_like_check" })
            check(method + " metadata lasts five minutes", MetadataCache.Policy(method).Lifetime == TimeSpan.FromMinutes(5), null);
        check("Playback URLs and writes bypass metadata caching", MetadataCache.Policy("song_url_v1").Lifetime == TimeSpan.Zero && MetadataCache.Policy("share_resource").Lifetime == TimeSpan.Zero, null);
        using var like = JsonDocument.Parse("""
            {"id":1,"notice":{"type":1,"user":{"nickname":"liker"},"track":{"id":12,"user":{"userId":7,"nickname":"author"},"json":"{\"msg\":\"original event\"}","info":{"threadId":"A_EV_2_12_7"}}}}
            """);
        var notice = ParseSocialItem(like.RootElement, "messages", "notices", 7);
        check("Event like notices retain actor, content and original event", notice.Name == "liker" && notice.Action == "赞了你的动态" && notice.Text == "original event" && notice.LinkedEvent?.Name == "author" && notice.ResourceId == 12 && SourceLinkLabel(notice) == "查看动态", null);
        using var reply = JsonDocument.Parse("""
            {"user":{"nickname":"reply"},"content":"reply text","commentId":2,"threadId":"R_SO_4_123","resourceJson":"{\"name\":\"song\"}"}
            """);
        var song = ParseSocialItem(reply.RootElement, "messages", "comments", 7);
        check("Song reply notice links to the source song", song.ResourceId == 123 && song.CommentId == 2 && SourceLinkLabel(song) == "查看歌曲", null);
        using var eventReply = JsonDocument.Parse("""
            {"content":"reply","commentId":2,"threadId":"A_EV_2_12_7","resourceJson":"{\"id\":12,\"user\":{\"userId\":7},\"json\":\"{\\\"msg\\\":\\\"original post\\\"}\"}"}
            """);
        var eventNotice = ParseSocialItem(eventReply.RootElement, "messages", "comments", 7);
        check("Event replies preserve their embedded source post for navigation", eventNotice.LinkedEvent?.Text == "original post" && SourceLinkLabel(eventNotice) == "查看动态", null);
        using var eventJson = JsonDocument.Parse("""
            {"id":12,"user":{"userId":7,"nickname":"author"},"json":"{\"msg\":\"hello\",\"song\":{\"id\":123,\"name\":\"song\",\"album\":{\"name\":\"album\",\"picUrl\":\"https://example.com/cover.jpg\"},\"artists\":[{\"name\":\"artist\"}],\"duration\":60000}}","pics":[{"originUrl":"https://example.com/a.jpg"},{"originUrl":"https://example.com/b.jpg"},{"originUrl":"file:///private"}]}
            """);
        var item = ParseSocialItem(eventJson.RootElement, "events", "", 7);
        check("Events retain every safe image and shared song artwork", item.Pictures.Count == 2 && item.SharedTrack?.Cover == "https://example.com/cover.jpg" && item.SharedTrack.Subtitle == "artist - album" && item.ThreadId == "A_EV_2_12_7", null);
        _playback.Restore(new() { Queue = [new(456, "current", "a", "b", "", 60000)], CurrentId = 456, Mode = "Shuffle" });
        check("Shared song appends without interrupting playback or replacing order", _playback.AddToQueue(item.SharedTrack!) && _playback.Current?.Id == 456 && !_playback.IsPlaying && _playback.Mode == "Shuffle" && _playback.Queue.Select(t => t.Id).SequenceEqual(new long[] { 456, 123 }) && !_playback.AddToQueue(item.SharedTrack!), null);
        _playback.Restore(new());
        var folder = Path.Combine(Path.GetTempPath(), "NetEaseNEXT-startup-fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var script = Path.Combine(folder, "bridge.mjs");
            await File.WriteAllTextAsync(script, """
                import {createInterface} from 'node:readline';
                createInterface({input:process.stdin}).on('line', async line => {
                  const r=JSON.parse(line); await new Promise(resolve=>setTimeout(resolve,250));
                  process.stdout.write(JSON.stringify({id:r.id,data:{code:200,result:[],playlist:{},songs:[]}})+'\n');
                }).on('close',()=>process.exit(0));
                """);
            using var client = new ApiClient(script, folder);
            var startup = new StartupSession(new(), client, () => new() { PlaylistId = 9, View = "playlist" });
            try
            {
                var ticks = 0; var pulse = DispatcherQueue.CreateTimer(); pulse.Interval = TimeSpan.FromMilliseconds(20);
                pulse.Tick += (_, _) => ticks++; pulse.Start();
                try
                {
                    await startup.InitialPage;
                    check("Startup fetches playlist details and tracks without waiting for UI consumption", client.RequestCount == 2 && ticks >= 3, new { client.RequestCount, ticks });
                    var requests = client.RequestCount;
                    await client.CallCachedAsync("playlist_detail", new { id = 9L, s = 0 });
                    await client.CallCachedAsync("playlist_track_all", new { id = 9L, limit = 100, offset = 0 });
                    check("Prepared startup results are reused when the window consumes them", client.RequestCount == requests, null);
                }
                finally { pulse.Stop(); }
            }
            finally { startup.Cancel(); }
        }
        finally { Directory.Delete(folder, true); }
        RestoreWindow(); AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1600));
        var publishTask = ShowPublishEventAsync(); await Task.Delay(100);
        check("Publishing opens a native draft dialog without sending", _dialog is { Title: "发布动态", IsPrimaryButtonEnabled: false }, null);
        _dialog?.Hide(); await publishTask;
        var photoTask = ShowEventPicturesAsync([new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png")).AbsoluteUri], 0);
        await Task.Delay(300);
        var viewport = _dialog?.Content is DependencyObject content ? FindDescendant<ScrollViewer>(content) : null;
        check("Photo viewer supports bounded pinch zoom and touch panning", viewport is { ZoomMode: ZoomMode.Enabled, MinZoomFactor: 1, MaxZoomFactor: 6, HorizontalScrollMode: ScrollMode.Enabled, VerticalScrollMode: ScrollMode.Enabled } && viewport.ViewportWidth > 0 && viewport.ViewportHeight > 0, null);
        viewport!.ChangeView(0, 0, 2, true); await Task.Delay(100);
        check("Image can zoom and expose pannable content", Math.Abs(viewport.ZoomFactor - 2) < .01 && viewport.ScrollableWidth > 0 && viewport.ScrollableHeight > 0, null);
        if (Environment.GetCommandLineArgs().Contains("--inspect-layout")) { AppWindow.Title = "NetEaseNEXT 动态图片检查"; await Task.Delay(45000); }
        _dialog?.Hide(); await photoTask;
        var gallery = new SocialItem { Kind = "event", Name = "fixture", Text = "fixture", ThreadId = "A_EV_2_12_7", ResourceType = 6, ResourceId = 12, ResourceKey = "12_7", Pictures = Enumerable.Repeat("https://example.invalid/photo.jpg", 9).ToList(), SharedTrack = item.SharedTrack };
        _linkedResource = new SocialItem { LinkedEvent = gallery };
        await NavigateAsync("event-detail"); Root.UpdateLayout(); await Task.Delay(100);
        var photos = FindDescendant<Controls.WrapPanel>(SocialList);
        check("All nine touch thumbnails wrap within the social row", photos != null && photos.ActualHeight >= 312 && photos.ActualHeight <= 350 && photos.ActualWidth <= 420 && photos.Children.Count == 9, new { photos?.ActualWidth, photos?.ActualHeight });
        if (Environment.GetCommandLineArgs().Contains("--events-read-check"))
        {
            var profile = await GetProfileAsync();
            check("Own events have an authenticated account", profile.HasValue, null);
            ApplyAccountProfile(profile!.Value); EventScope.SelectedIndex = 1;
            await NavigateAsync("events");
            check("Own events load using the subscribed account", !Notice.IsOpen && !Busy.IsActive && EventToolbar.Visibility == Visibility.Visible && SocialItems.All(i => i.Kind == "event"), new { count = SocialItems.Count });
        }
    }
}
