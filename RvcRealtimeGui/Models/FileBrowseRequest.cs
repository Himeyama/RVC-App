namespace RvcRealtimeGui.Models;

public sealed class FileBrowseRequest(string extensionFilter, string? initialPath, Action<string> onSelected)
{
    public string ExtensionFilter { get; } = extensionFilter;
    public string? InitialPath { get; } = initialPath;
    public Action<string> OnSelected { get; } = onSelected;
}
