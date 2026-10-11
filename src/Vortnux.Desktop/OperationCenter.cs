namespace Vortnux.Desktop;

public enum OperationStatus { Queued, Running, Completed, Failed, Cancelled }

public sealed class OperationEntry(string title, string? page = null)
{
    public string Title { get; } = title;
    public string? Page { get; } = page;
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;
    public OperationStatus Status { get; internal set; } = OperationStatus.Running;
    public string Message { get; internal set; } = "Starting…";
    public double Progress { get; internal set; }
    public bool IsRead { get; internal set; }
    internal Action? Cancel { get; set; }
    internal Action? Open { get; set; }
}

/// <summary>UI-thread-owned live operations and bounded session notification history.</summary>
public sealed class OperationCenter
{
    private readonly List<OperationEntry> entries = [];
    public IReadOnlyList<OperationEntry> Entries => entries;
    public int UnreadCount => entries.Count(entry => !entry.IsRead && entry.Status is not (OperationStatus.Running or OperationStatus.Queued));
    public int ActiveCount => entries.Count(entry => entry.Status is OperationStatus.Running or OperationStatus.Queued);
    public event Action? Changed;

    public OperationEntry Begin(string title, Action cancel, string page = "Downloads")
    {
        var entry = new OperationEntry(title, page) { Cancel = cancel };
        entries.Insert(0, entry); Changed?.Invoke(); return entry;
    }

    public void Report(OperationEntry entry, double progress, string message)
    {
        if (entry.Status != OperationStatus.Running) return;
        var value = Math.Clamp(progress, 0, 100);
        if (Math.Round(entry.Progress) == Math.Round(value) && entry.Message == message) return;
        entry.Progress = value; entry.Message = message; Changed?.Invoke();
    }

    public void Finish(OperationEntry entry, OperationStatus status, string message)
    {
        entry.Status = status; entry.Message = message; entry.Cancel = null; entry.IsRead = false;
        Trim(); Changed?.Invoke();
    }

    public void Cancel(OperationEntry entry) { if (entries.Contains(entry)) entry.Cancel?.Invoke(); }

    public void MarkAllRead() { foreach (var entry in entries) entry.IsRead = true; Changed?.Invoke(); }
    public void ClearHistory() { entries.RemoveAll(entry => entry.Status is not (OperationStatus.Running or OperationStatus.Queued)); Changed?.Invoke(); }
    public void Refresh() => Changed?.Invoke();
    private void Trim()
    {
        var history = entries.Where(entry => entry.Status is not (OperationStatus.Running or OperationStatus.Queued)).Skip(200).ToArray();
        foreach (var entry in history) entries.Remove(entry);
    }
}
