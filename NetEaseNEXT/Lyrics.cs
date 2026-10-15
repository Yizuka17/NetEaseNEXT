using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using NetEaseNEXT.Models;

namespace NetEaseNEXT;

public sealed partial class MainWindow
{
    private int _activeLyricIndex = -1, _lyricScrollGeneration;
    private ScrollViewer? _lyricScroll;
    private bool _initialLyricCenter, _lyricCenterQueued, _centeringLyrics;
    private void BindLyrics(IEnumerable<LyricLine> lines)
    {
        ++_lyricScrollGeneration;
        _activeLyricIndex = -1; _initialLyricCenter = _lyrics.Count > 0 && _followLyrics;
        var padding = Math.Max(0, LyricsList.ActualHeight / 2);
        // Recreate the spacers with the new collection; recycled header containers
        // can otherwise retain the zero height from their first measurement.
        LyricsList.Header = new Border { Height = padding };
        LyricsList.Footer = new Border { Height = padding };
        LyricsList.ItemsSource = lines;
        QueueInitialLyricCenter(); UpdateProgress();
    }
    private void UpdateLyricPadding()
    {
        var padding = Math.Max(0, LyricsList.ActualHeight / 2);
        if (LyricsList.Header is FrameworkElement top && Math.Abs(top.Height - padding) > .5) top.Height = padding;
        if (LyricsList.Footer is FrameworkElement bottom && Math.Abs(bottom.Height - padding) > .5) bottom.Height = padding;
    }
    private void QueueInitialLyricCenter()
    {
        if (!_initialLyricCenter || !_followLyrics || _hidden || _view != "playing" || _lyricCenterQueued) return;
        _lyricCenterQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _lyricCenterQueued = false;
            if (!_initialLyricCenter || !_followLyrics || _hidden || _view != "playing") return;
            UpdateLyricPadding(); CenterCurrentLyric(false);
        });
    }
    private void Lyric_ContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue) { DispatcherQueue.TryEnqueue(RefreshLyricAppearance); QueueInitialLyricCenter(); }
    }
    private void ConnectLyricsScroll()
    {
        var scroll = FindDescendant<ScrollViewer>(LyricsList);
        if (scroll == null || ReferenceEquals(scroll, _lyricScroll)) return;
        if (_lyricScroll != null) _lyricScroll.ViewChanged -= Lyrics_ViewChanged;
        _lyricScroll = scroll; _lyricScroll.ViewChanged += Lyrics_ViewChanged;
    }
    private void Lyrics_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args) { RefreshLyricAppearance(); if (!args.IsIntermediate) QueueInitialLyricCenter(); }
    private async void CenterCurrentLyric(bool animated = true)
    {
        if (_centeringLyrics || _exiting || !_followLyrics || _hidden || _view != "playing" || _activeLyricIndex < 0 || _activeLyricIndex >= _lyrics.Count) return;
        _centeringLyrics = true;
        var generation = ++_lyricScrollGeneration;
        try
        {
            var initial = _initialLyricCenter;
            var settled = 0;
            for (var attempt = 0; attempt < (initial ? 8 : 2); attempt++)
            {
                if (_exiting || generation != _lyricScrollGeneration || !_followLyrics || _hidden || _view != "playing") return;
                LyricsList.UpdateLayout(); UpdateLyricPadding(); ConnectLyricsScroll();
                if (_lyricScroll == null || _lyricScroll.ViewportHeight < 1) { await Task.Delay(24); continue; }
                var line = _lyrics[_activeLyricIndex];
                if (LyricsList.ContainerFromItem(line) is not FrameworkElement) { LyricsList.ScrollIntoView(line); await Task.Delay(24); continue; }
                if (LyricsList.ContainerFromItem(line) is FrameworkElement row && row.ActualHeight > 0)
                {
                    var point = row.TransformToVisual(_lyricScroll).TransformPoint(new Point(0, row.ActualHeight / 2));
                    var difference = point.Y - _lyricScroll.ViewportHeight / 2;
                    if (Math.Abs(difference) <= 3)
                    {
                        if (initial && ++settled < 3) { await Task.Delay(24); continue; }
                        _initialLyricCenter = false; break;
                    }
                    settled = 0;
                    var target = Math.Clamp(_lyricScroll.VerticalOffset + difference, 0, _lyricScroll.ScrollableHeight);
                    _lyricScroll.ChangeView(null, target, null, initial || !(animated && PlayerAnimationEnabled));
                    if (!initial) break;
                }
                await Task.Delay(24);
            }
            RefreshLyricAppearance();
        }
        catch (InvalidOperationException) { }
        finally { _centeringLyrics = false; if (generation != _lyricScrollGeneration) QueueInitialLyricCenter(); }
    }
    private void RefreshLyricAppearance()
    {
        if (_hidden || _view != "playing") return;
        for (var i = 0; i < _lyrics.Count; i++)
        {
            if (LyricsList.ContainerFromIndex(i) is not ListViewItem row) continue;
            var selected = i == _activeLyricIndex;
            var text = FindDescendant<TextBlock>(row);
            if (text != null) text.Style = (Style)Application.Current.Resources[selected ? "CurrentLyricTextStyle" : "OtherLyricTextStyle"];
            var distance = Math.Abs(i - _activeLyricIndex);
            var visual = ElementCompositionPreview.GetElementVisual(row);
            visual.Opacity = selected ? 1 : (float)Math.Max(.18, .65 - distance * .09);
        }
    }
}
