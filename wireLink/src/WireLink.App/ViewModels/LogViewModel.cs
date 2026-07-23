using System.Collections.ObjectModel;
using ReactiveUI;
using WireLink.Core.Services;

namespace WireLink.App.ViewModels;

public sealed class LogViewModel : ViewModelBase, IDisposable
{
    private readonly ILogStore _store;
    private string _keyword="";
    private LogLevel _minimum=LogLevel.Information;
    private bool _paused;
    private LogEntry? _selectedEntry;
    private DateTimeOffset _displayFrom=DateTimeOffset.MinValue;
    public LogViewModel(ILogStore store)
    {
        _store=store; Refresh(); store.EntryAdded+=OnEntryAdded;
        ClearCommand=ReactiveCommand.Create(()=> { _displayFrom=DateTimeOffset.Now; Entries.Clear(); });
        RefreshCommand=ReactiveCommand.Create(Refresh);
    }
    public ObservableCollection<LogEntry> Entries { get; }=[];
    public IReadOnlyList<LogLevel> Levels { get; }=Enum.GetValues<LogLevel>();
    public ReactiveCommand<System.Reactive.Unit,System.Reactive.Unit> ClearCommand { get; }
    public ReactiveCommand<System.Reactive.Unit,System.Reactive.Unit> RefreshCommand { get; }
    public string Keyword { get=>_keyword; set { this.RaiseAndSetIfChanged(ref _keyword,value); Refresh(); } }
    public LogLevel Minimum { get=>_minimum; set { this.RaiseAndSetIfChanged(ref _minimum,value); Refresh(); } }
    public bool Paused { get=>_paused; set=>this.RaiseAndSetIfChanged(ref _paused,value); }
    public LogEntry? SelectedEntry { get=>_selectedEntry; set=>this.RaiseAndSetIfChanged(ref _selectedEntry,value); }
    public string LogDirectory=>_store.LogDirectory;
    private void OnEntryAdded(object? sender,LogEntry entry)
    {
        if(Paused || !Matches(entry)) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(()=>Entries.Add(entry));
    }
    private bool Matches(LogEntry entry)=>entry.Timestamp>=_displayFrom && entry.Level>=Minimum && (string.IsNullOrWhiteSpace(Keyword) || entry.Message.Contains(Keyword,StringComparison.OrdinalIgnoreCase));
    private void Refresh() { Entries.Clear(); foreach(var entry in _store.Snapshot.Where(Matches)) Entries.Add(entry); }
    public void Dispose()=>_store.EntryAdded-=OnEntryAdded;
}
