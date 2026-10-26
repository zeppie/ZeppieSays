using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ZeppieSays.UI;

namespace ZeppieSays;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        if (model.HasNoModels) Tabs.SelectedItem = ModelsTab;
        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != Tabs) return;
            if (Tabs.SelectedItem == StylesTab) model.Styles.Refresh();
            // The streak and heatmap move on at midnight, without a new dictation.
            if (Tabs.SelectedItem == HomeTab || Tabs.SelectedItem == InsightsTab) model.Insights.Refresh();
            UpdateLoadedModelsPolling();
        };
        IsVisibleChanged += (_, _) => UpdateLoadedModelsPolling();
        model.Notetaker.ScrollToLine += line => TranscriptList.ScrollIntoView(line);

        _stripGlide.Tick += (_, _) => GlideStrip();
        JobStrip.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => UpdateStrip()));
        Loaded += (_, _) => UpdateStrip();
    }

    void OpenSettings_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = SettingsTab;

    // ---- The strip of recordings: no scrollbar; drag it, use the arrows or the wheel, and a thin line shows where you are.

    readonly DispatcherTimer _stripGlide = new() { Interval = TimeSpan.FromMilliseconds(15) };
    double _stripTarget;
    Point _stripPressedAt;
    double _stripPressedOffset;
    bool _stripPressed, _stripDragging;

    ScrollViewer? StripViewer => Descendant<ScrollViewer>(JobStrip);

    /// <summary>Shows the arrows and the position line only when the recordings don't fit, and keeps them in step.</summary>
    void UpdateStrip()
    {
        if (StripViewer is not { } viewer) return;
        var overflow = viewer.ExtentWidth > viewer.ViewportWidth + 1;
        var visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        StripLeft.Visibility = StripRight.Visibility = StripTrack.Visibility = visibility;
        if (!overflow) return;

        StripLeft.IsEnabled = viewer.HorizontalOffset > 1;
        StripRight.IsEnabled = viewer.HorizontalOffset < viewer.ScrollableWidth - 1;
        var track = JobStrip.ActualWidth;
        var thumb = Math.Clamp(track * viewer.ViewportWidth / viewer.ExtentWidth, 28, track);
        StripThumb.Width = thumb;
        StripThumb.Margin = new Thickness((track - thumb) * (viewer.HorizontalOffset / Math.Max(1, viewer.ScrollableWidth)), 0, 0, 0);
    }

    void JobStrip_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateStrip();

    void StripLeft_Click(object sender, RoutedEventArgs e) => MoveStrip(-1);
    void StripRight_Click(object sender, RoutedEventArgs e) => MoveStrip(1);

    /// <summary>One step of most of a screen to the left or right, gliding there.</summary>
    void MoveStrip(int direction)
    {
        if (StripViewer is not { } viewer) return;
        var from = _stripGlide.IsEnabled ? _stripTarget : viewer.HorizontalOffset;
        _stripTarget = Math.Clamp(from + direction * viewer.ViewportWidth * 0.8, 0, viewer.ScrollableWidth);
        _stripGlide.Start();
    }

    void GlideStrip()
    {
        if (StripViewer is not { } viewer)
        {
            _stripGlide.Stop();
            return;
        }
        var distance = _stripTarget - viewer.HorizontalOffset;
        if (Math.Abs(distance) < 1)
        {
            viewer.ScrollToHorizontalOffset(_stripTarget);
            _stripGlide.Stop();
        }
        else viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + distance * 0.25);
    }

    // Pressing and moving the mouse drags the cards; a press that doesn't move selects the card, as a click does.
    void JobStrip_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (StripViewer is not { } viewer || IsInside<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource)) return;
        _stripGlide.Stop();
        _stripPressed = true;
        _stripDragging = false;
        _stripPressedAt = e.GetPosition(JobStrip);
        _stripPressedOffset = viewer.HorizontalOffset;
        e.Handled = true; // the card is selected when the mouse is released, unless this turns out to be a drag
    }

    void JobStrip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_stripPressed || StripViewer is not { } viewer) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _stripPressed = false;
            return;
        }
        var moved = e.GetPosition(JobStrip).X - _stripPressedAt.X;
        if (!_stripDragging && Math.Abs(moved) > 6)
        {
            _stripDragging = true;
            JobStrip.CaptureMouse();
        }
        if (_stripDragging) viewer.ScrollToHorizontalOffset(_stripPressedOffset - moved);
    }

    void JobStrip_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_stripPressed) return;
        _stripPressed = false;
        if (_stripDragging)
        {
            _stripDragging = false;
            JobStrip.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = ParentOf(d))
            if (d is ListBoxItem item)
            {
                item.IsSelected = true;
                break;
            }
    }

    static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    static bool IsInside<T>(object? source) where T : DependencyObject
    {
        for (var d = source as DependencyObject; d is not null; d = ParentOf(d))
            if (d is T) return true;
        return false;
    }

    // The recordings strip scrolls sideways, so the mouse wheel does too.
    void JobStrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (StripViewer is not { } viewer) return;
        _stripGlide.Stop();
        viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // Scrolling the transcript by hand stops it following the playback until "Sync with playback".
    void Transcript_PreviewMouseWheel(object sender, MouseWheelEventArgs e) => Model.Notetaker.FollowPlayback = false;

    void Transcript_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != TranscriptList; d = VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ScrollBar)
            {
                Model.Notetaker.FollowPlayback = false;
                return;
            }
    }

    // Editing a paragraph: the text box takes the keyboard at once; Enter saves, Esc cancels.
    void EditBox_Loaded(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        box.Focus();
        box.CaretIndex = box.Text.Length;
    }

    void EditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TranscriptLineViewModel line) return;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            Model.Notetaker.SaveEdit.Execute(line);
        else if (e.Key == Key.Escape)
            Model.Notetaker.CancelEdit.Execute(line);
        else
            return;
        e.Handled = true;
    }

    /// <summary>The cursor of the text box (the button's Tag) says where the paragraph is cut in two.</summary>
    void SplitAtCursor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextBox box, DataContext: TranscriptLineViewModel line })
            Model.Notetaker.SplitLine(line, box.CaretIndex);
    }

    /// <summary>Enter in a speaker's name box applies the name, like leaving the box does.</summary>
    void SpeakerName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ((UIElement)sender).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        e.Handled = true;
    }

    static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    MainViewModel Model => (MainViewModel)DataContext;

    /// <summary>The loaded-models panel asks Ollama only while you can see it.</summary>
    void UpdateLoadedModelsPolling() =>
        Model.Loaded.SetActive(IsVisible && WindowState != WindowState.Minimized && Tabs.SelectedItem == ModelsTab);

    // Our own caption buttons, since the sidebar is drawn into the title bar.
    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>A maximized chrome-less window hangs past the screen edge; pad it back in.</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        Root.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
        base.OnStateChanged(e);
        UpdateLoadedModelsPolling();
    }

    void Notetaker_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Notetaker_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            Model.Notetaker.AddFiles(files);
    }

    void WordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Model.AddWord();
        e.Handled = true;
    }

    // API keys go straight from the password box into encrypted storage; they are never shown again.
    void OpenAiKeyBox_LostFocus(object sender, RoutedEventArgs e) => SaveKey((PasswordBox)sender, Model.Styles.SetOpenAiKey);
    void AnthropicKeyBox_LostFocus(object sender, RoutedEventArgs e) => SaveKey((PasswordBox)sender, Model.Styles.SetAnthropicKey);
    void RemoveOpenAiKey_Click(object sender, RoutedEventArgs e) => Model.Styles.SetOpenAiKey("");
    void RemoveAnthropicKey_Click(object sender, RoutedEventArgs e) => Model.Styles.SetAnthropicKey("");

    static void SaveKey(PasswordBox box, Action<string> save)
    {
        if (box.Password.Trim().Length == 0) return;
        save(box.Password);
        box.Clear();
    }

    /// <summary>Closing only hides the window; the app keeps listening from the tray.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!((App)Application.Current).IsQuitting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
