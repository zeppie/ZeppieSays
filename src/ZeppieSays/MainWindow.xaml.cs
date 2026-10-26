using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    }

    // The recordings strip scrolls sideways, so the mouse wheel does too.
    void JobStrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Descendant<ScrollViewer>(JobStrip) is not { } viewer) return;
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
