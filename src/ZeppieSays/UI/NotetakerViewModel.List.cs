using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using ZeppieSays.Core;
using ZeppieSays.Notes;

namespace ZeppieSays.UI;

/// <summary>The list of recordings on the Notetaker page: grouped by day, searchable, and collapsible.</summary>
public sealed partial class NotetakerViewModel
{
    ListCollectionView? _jobsView;
    string _jobSearch = "";
    bool _listCollapsed;

    // What the search looks through: each finished transcript's text (it holds the speakers' names), read once and
    // again only when the file changes.
    readonly Dictionary<string, (DateTime Stamp, string Text)> _transcriptText = new(StringComparer.OrdinalIgnoreCase);
    int _searchVersion;

    /// <summary>
    /// The queue at the top, in the order it is worked through, then the finished recordings newest first under a
    /// heading per day, narrowed by <see cref="JobSearch"/>.
    /// </summary>
    public ListCollectionView JobsView => _jobsView ??= CreateJobsView();

    ListCollectionView CreateJobsView()
    {
        var view = new ListCollectionView(Jobs)
        {
            IsLiveSorting = true,
            IsLiveGrouping = true,
            Filter = MatchesSearch,
        };
        view.SortDescriptions.Add(new SortDescription(nameof(NoteJob.QueueRank), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(NoteJob.QueueOrder), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(NoteJob.SortDate), ListSortDirection.Descending));
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NoteJob.ListGroup)) { CustomSort = new GroupOrder() });
        foreach (var property in new[] { nameof(NoteJob.QueueRank), nameof(NoteJob.QueueOrder), nameof(NoteJob.SortDate) })
            view.LiveSortingProperties.Add(property);
        view.LiveGroupingProperties.Add(nameof(NoteJob.ListGroup));
        return view;
    }

    /// <summary>The queue's heading first, then the days, newest first.</summary>
    sealed class GroupOrder : System.Collections.IComparer
    {
        public int Compare(object? x, object? y) => Day(y).CompareTo(Day(x));

        static DateTime Day(object? group) => ((group as CollectionViewGroup)?.Name as ListHeading)?.Day ?? DateTime.MinValue;
    }

    /// <summary>Finds a conversation: by its title, or by who was in it and what was said (the transcript).</summary>
    public string JobSearch
    {
        get => _jobSearch;
        set { if (Set(ref _jobSearch, value ?? "")) _ = ApplyJobSearchAsync(); }
    }

    public bool HasJobSearch => _jobSearch.Trim().Length > 0;

    public RelayCommand ClearJobSearch => _clearJobSearch ??= new RelayCommand(_ => JobSearch = "");
    RelayCommand? _clearJobSearch;

    /// <summary>The list shrinks to a narrow rail with just the buttons, which gives the transcript the room.</summary>
    public bool ListCollapsed
    {
        get => _listCollapsed;
        set { if (Set(ref _listCollapsed, value)) Raise(nameof(ListExpanded)); }
    }

    public bool ListExpanded => !_listCollapsed;

    public RelayCommand ToggleList => _toggleList ??= new RelayCommand(_ => ListCollapsed = !ListCollapsed);
    RelayCommand? _toggleList;

    async Task ApplyJobSearchAsync()
    {
        var version = ++_searchVersion;
        Raise(nameof(HasJobSearch));
        var query = _jobSearch.Trim();
        if (query.Length > 0) await LoadTranscriptTextAsync();
        if (version != _searchVersion) return; // typed on meanwhile
        JobsView.Refresh();
    }

    bool MatchesSearch(object item)
    {
        var query = _jobSearch.Trim();
        if (query.Length == 0 || item is not NoteJob job) return true;
        if (job.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return true;
        return job.TranscriptPath is { } path && _transcriptText.TryGetValue(path, out var entry) &&
               entry.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>Reads the transcripts that weren't read yet or changed since, off the UI thread.</summary>
    async Task LoadTranscriptTextAsync()
    {
        var paths = Jobs.Select(j => j.TranscriptPath).Where(p => p is not null).Select(p => p!).Distinct().ToList();
        var stamps = _transcriptText.ToDictionary(e => e.Key, e => e.Value.Stamp, StringComparer.OrdinalIgnoreCase);
        var read = await Task.Run(() =>
        {
            var found = new List<(string Path, DateTime Stamp, string Text)>();
            foreach (var path in paths)
            {
                try
                {
                    var stamp = File.GetLastWriteTimeUtc(path);
                    if (stamps.TryGetValue(path, out var known) && known == stamp) continue;
                    found.Add((path, stamp, File.ReadAllText(path)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Could not read {path} to search it: {e.Message}");
                }
            }
            return found;
        });
        foreach (var (path, stamp, text) in read) _transcriptText[path] = (stamp, text);
    }
}
