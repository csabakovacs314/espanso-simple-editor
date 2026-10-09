using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using EspansoEditor.Core.Model;

namespace EspansoEditor.App;

/// <summary>Library tree: files, groups (comment markers) and snippets.</summary>
public abstract class TreeNode : INotifyPropertyChanged
{
    private bool _isExpanded = true;
    private bool _isSelected;

    public ObservableCollection<TreeNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Notify();
            ExpansionChanged?.Invoke(this);
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Notify();
        }
    }

    /// <summary>Stable identity used to remember which nodes the user collapsed.</summary>
    public abstract string Key { get; }

    public static event Action<TreeNode>? ExpansionChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Tells the UI that every displayed property may have changed.</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public sealed class FileNode(MatchFile file) : TreeNode
{
    public MatchFile File { get; } = file;
    public string Title => File.DisplayName;

    public string Tag =>
        File.LoadError != null ? "cannot read"
        : File.IsPackage ? "package, read-only"
        : File.IsReadOnly ? "read-only"
        : File.IsUnderscore ? "not auto-loaded"
        : "";

    public bool HasTag => Tag.Length > 0;
    public string CountText => File.Snippets.Count.ToString();
    public string ToolTipText => File.ReadOnlyReason ?? (File.IsUnderscore
        ? "Espanso only loads files starting with _ when another file imports them.\n" + File.FullPath
        : File.FullPath);

    public override string Key => "F|" + File.FullPath;
}

public sealed class GroupNode(MatchFile file, string group) : TreeNode
{
    public MatchFile File { get; } = file;
    public string Group { get; } = group;
    public string Title => Group;
    public override string Key => "G|" + File.FullPath + "|" + Group;
}

public sealed class SnippetNode(Snippet snippet) : TreeNode
{
    public Snippet Snippet { get; } = snippet;
    public string Title => Snippet.DisplayName;
    public bool TitleIsTrigger => string.IsNullOrWhiteSpace(Snippet.Label);

    /// <summary>Second line: the trigger when there is a description, else a taste of the text.</summary>
    public string Detail => TitleIsTrigger ? Snippet.PreviewLine : string.Join("  ", Snippet.Triggers);

    public bool IsLocked => Snippet.IsReadOnly;
    public bool IsDirty => Snippet.IsDirty || Snippet.IsNew || Snippet.IsMoved;
    public bool HasWarning { get; set; }
    public string ToolTipText => Snippet.AdvancedReason != null ? "Read-only: this snippet " + Snippet.AdvancedReason + "." : string.Join(", ", Snippet.Triggers);
    public override string Key => "S|" + Snippet.Id;
}
