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
        SourceInitialized += (_, _) => Services.TaskbarIdentity.Apply(this);
        DataContext = model;
        if (model.HasNoModels) Tabs.SelectedItem = ModelsTab;
        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != Tabs) return;
            if (Tabs.SelectedItem == StylesTab) model.Styles.Refresh();
            if (Tabs.SelectedItem == SettingsTab) model.RefreshVoices();
            else model.StopVoice();
            // The streak and heatmap move on at midnight, without a new dictation.
            if (Tabs.SelectedItem == HomeTab || Tabs.SelectedItem == InsightsTab) model.Insights.Refresh();
            UpdateLoadedModelsPolling();
        };
        IsVisibleChanged += (_, _) => UpdateLoadedModelsPolling();
        model.Notetaker.ScrollToLine += line => TranscriptList.ScrollIntoView(line);
        model.Notetaker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NotetakerViewModel.ListCollapsed)) ApplyListCollapsed(model.Notetaker.ListCollapsed);
        };
    }

    double _listWidth = 165;

    /// <summary>Collapsed, the conversations list is a rail of 56 px with the buttons; expanded, it gets its width back.</summary>
    void ApplyListCollapsed(bool collapsed)
    {
        if (collapsed)
        {
            _listWidth = NoteListColumn.ActualWidth;
            NoteListColumn.MinWidth = 0;
            NoteListColumn.MaxWidth = 56;
            NoteListColumn.Width = new GridLength(56);
        }
        else
        {
            NoteListColumn.MinWidth = 120;
            NoteListColumn.MaxWidth = 420;
            NoteListColumn.Width = new GridLength(Math.Clamp(_listWidth, 120, 420));
        }
    }

    void OpenSettings_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = SettingsTab;

    /// <summary>Opens Settings at the AI providers card, once the page has been laid out.</summary>
    void OpenAiProviders_Click(object sender, RoutedEventArgs e)
    {
        Tabs.SelectedItem = SettingsTab;
        Dispatcher.BeginInvoke(() => AiProvidersCard.BringIntoView(), DispatcherPriority.Loaded);
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

    // Renaming a recording: F2 or Rename on its card puts the cursor in its name; Enter renames, Esc keeps the name.
    void RenameJob_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Notes.NoteJob job) BeginRename(job);
    }

    void JobStrip_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2 || Model.Notetaker.Selected is not { IsDone: true } job) return;
        BeginRename(job);
        e.Handled = true;
    }

    void BeginRename(Notes.NoteJob job)
    {
        Model.Notetaker.Selected = job;
        // The name box shows once the page has laid out the selected recording.
        Dispatcher.BeginInvoke(() =>
        {
            RecordingTitle.Focus();
            RecordingTitle.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void RecordingTitle_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            RecordingTitle.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        else if (e.Key == Key.Escape)
            RecordingTitle.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        else
            return;
        Keyboard.Focus(JobStrip);
        e.Handled = true;
    }

    /// <summary>Enter in a speaker's name box applies the name, like leaving the box does.</summary>
    void SpeakerName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ((UIElement)sender).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        e.Handled = true;
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

    // Dragging a recording in the queue to change which is transcribed first. Rows in the queue can be picked up;
    // a line shows where it lands, above or below the row under the mouse.
    const string QueueDragFormat = "ZeppieSays.QueuedRecording";
    Point _dragStart;
    Notes.NoteJob? _dragJob;
    ListBoxItem? _dropRow;

    void JobStrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragJob = null;
        if (e.OriginalSource is not DependencyObject source || Ancestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null) return;
        if (Ancestor<ListBoxItem>(source)?.DataContext is Notes.NoteJob { InQueue: true } job)
        {
            _dragJob = job;
            _dragStart = e.GetPosition(JobStrip);
        }
    }

    void JobStrip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragJob is not { } job || e.LeftButton != MouseButtonState.Pressed) return;
        var moved = e.GetPosition(JobStrip) - _dragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragJob = null;
        try { DragDrop.DoDragDrop(JobStrip, new DataObject(QueueDragFormat, job), DragDropEffects.Move); }
        finally { ShowDropLine(null, false); }
    }

    void JobStrip_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(QueueDragFormat)) return; // files: the page takes them
        var (row, after) = DropTarget(e);
        ShowDropLine(row, after);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    void JobStrip_DragLeave(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(QueueDragFormat)) ShowDropLine(null, false);
    }

    void JobStrip_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(QueueDragFormat) is not Notes.NoteJob job) return;
        var (row, after) = DropTarget(e);
        ShowDropLine(null, false);
        Model.Notetaker.MoveInQueue(job, row?.DataContext as Notes.NoteJob, after);
        e.Handled = true;
    }

    /// <summary>
    /// The queued row the mouse is over, and whether the dragged one goes below it (the lower half). Below the
    /// queue, among the finished recordings, it goes to the end of the queue: shown under the queue's last row.
    /// </summary>
    (ListBoxItem? Row, bool After) DropTarget(DragEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && Ancestor<ListBoxItem>(source) is { DataContext: Notes.NoteJob { InQueue: true } } row)
            return (row, e.GetPosition(row).Y > row.ActualHeight / 2);
        var last = Model.Notetaker.JobsView.OfType<Notes.NoteJob>().LastOrDefault(j => j.InQueue);
        return (last is null ? null : JobStrip.ItemContainerGenerator.ContainerFromItem(last) as ListBoxItem, true);
    }

    void ShowDropLine(ListBoxItem? row, bool after)
    {
        if (_dropRow is not null && _dropRow != row) _dropRow.Tag = null;
        _dropRow = row;
        if (row is not null) row.Tag = after ? "Below" : "Above";
    }

    static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as T;
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
    void ElevenLabsKeyBox_LostFocus(object sender, RoutedEventArgs e) => SaveKey((PasswordBox)sender, Model.Styles.SetElevenLabsKey);
    void RemoveElevenLabsKey_Click(object sender, RoutedEventArgs e) => Model.Styles.SetElevenLabsKey("");

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
