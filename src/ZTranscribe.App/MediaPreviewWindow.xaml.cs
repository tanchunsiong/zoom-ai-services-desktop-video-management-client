using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZTranscribe.App.Services;
using ZTranscribe.Core.Models;

namespace ZTranscribe.App;

public partial class MediaPreviewWindow : Window
{
    private readonly QueueJob _job;
    private bool _updatingTimeline;
    public VlcPlaybackService Player { get; } = new();

    public MediaPreviewWindow(QueueJob job)
    {
        _job = job;
        InitializeComponent();
        DataContext = this;
        Title = $"Z Scribe / Preview - {job.DisplayName}";
        MediaNameText.Text = job.DisplayName;
        MediaPathText.Text = job.SourcePath;
        Player.PositionChanged += Player_PositionChanged;
        Player.DurationChanged += Player_DurationChanged;
        Player.PlaybackFailed += Player_PlaybackFailed;
        Loaded += (_, _) =>
        {
            ApplyIconography();
            Player.Open(_job);
        };
    }

    private void ApplyIconography()
    {
        foreach (var button in FindVisualChildren<Button>(this))
        {
            if (button.ToolTip is not string toolTip || toolTip is not ("Play" or "Pause")) continue;
            button.Style = (Style)FindResource("IconButton");
            button.Content = new TextBlock
            {
                Text = toolTip == "Play" ? "\uE768" : "\uE769",
                FontFamily = (FontFamily?)Application.Current.TryFindResource("IconFont") ?? new FontFamily("Segoe Fluent Icons"),
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in FindVisualChildren<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
        }
    }

    private void Player_PositionChanged(TimeSpan position) => Dispatcher.BeginInvoke(() =>
    {
        _updatingTimeline = true;
        TimelineSlider.Value = Math.Clamp(position.TotalSeconds, 0, TimelineSlider.Maximum);
        PositionText.Text = FormatTime(position);
        _updatingTimeline = false;
    });

    private void Player_DurationChanged(TimeSpan duration) => Dispatcher.BeginInvoke(() =>
    {
        TimelineSlider.Maximum = Math.Max(1, duration.TotalSeconds);
        DurationText.Text = FormatTime(duration);
    });

    private void Player_PlaybackFailed(string message) => Dispatcher.BeginInvoke(() =>
    {
        PlaybackErrorText.Text = message;
        PlaybackErrorText.Visibility = Visibility.Visible;
    });

    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingTimeline || !IsLoaded) return;
        Player.Seek(TimeSpan.FromSeconds(e.NewValue));
        PositionText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
    }

    private void Play_Click(object sender, RoutedEventArgs e) => Player.Play();

    private void Pause_Click(object sender, RoutedEventArgs e) => Player.Pause();

    private void PlaybackRate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string value } ||
            !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)) return;
        Player.SetRate(rate);
    }

    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
        : $"{time.Minutes:00}:{time.Seconds:00}";

    protected override void OnClosed(EventArgs e)
    {
        Player.PositionChanged -= Player_PositionChanged;
        Player.DurationChanged -= Player_DurationChanged;
        Player.PlaybackFailed -= Player_PlaybackFailed;
        Player.Dispose();
        base.OnClosed(e);
    }
}
