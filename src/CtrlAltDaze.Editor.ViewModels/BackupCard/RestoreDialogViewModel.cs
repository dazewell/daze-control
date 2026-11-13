using CommunityToolkit.Mvvm.ComponentModel;
using CtrlAltDaze.Core.Backup;

namespace CtrlAltDaze.Editor.ViewModels.BackupCard;

/// <summary>One line of "What will change": the state is text, never colour alone (DESIGN §9).</summary>
public sealed record RestoreItemRow(string Kind, string Name, string State);

/// <summary>The restore dialog's model (DESIGN §6): the preview, the mode and whether Restore may run.</summary>
public sealed partial class RestoreDialogViewModel : ObservableObject
{
    private static readonly (string Kind, string Label)[] Kinds =
        [("flow", "Flows"), ("audio", "Audio"), ("bluetooth", "Bluetooth"), ("display", "Display layouts"), ("android", "Android devices")];

    private readonly BackupCardViewModel _card;
    private readonly BackupArchive _archive;

    internal RestoreDialogViewModel(BackupCardViewModel card, BackupArchive archive, RestorePreview preview)
    {
        _card = card;
        _archive = archive;
        Preview = preview;
        SelectedMode = preview.CanReplace ? RestoreMode.Replace : RestoreMode.AddMissing;
    }

    public RestorePreview Preview { get; }

    [ObservableProperty]
    public partial RestoreMode SelectedMode { get; set; }

    public bool ReplaceSelected
    {
        get => SelectedMode == RestoreMode.Replace;
        set { if (value) SelectedMode = RestoreMode.Replace; }
    }

    public bool AddMissingSelected
    {
        get => SelectedMode == RestoreMode.AddMissing;
        set { if (value) SelectedMode = RestoreMode.AddMissing; }
    }

    partial void OnSelectedModeChanged(RestoreMode value)
    {
        OnPropertyChanged(nameof(ReplaceSelected));
        OnPropertyChanged(nameof(AddMissingSelected));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(HasRows));
    }

    public bool CanReplace => Preview.CanReplace;

    public bool CanAddMissing => Preview.CanAddMissing;

    /// <summary>Restore is off for an unreadable backup config (D-103 choice 9) and for a mode the PC's own config rules out.</summary>
    public bool CanRestore => SelectedMode == RestoreMode.Replace ? Preview.CanReplace : Preview.CanAddMissing;

    public string? Unreadable => Preview.Unreadable;

    public bool HasRows => Preview.Items.Any(i => i.State != ChangeState.Same);

    /// <summary>The modes only matter when the backup can be restored at all.</summary>
    public bool CanChooseMode => Preview.CanReplace;

    public bool IsUnreadable => Preview.Unreadable is not null;

    public string Origin
    {
        get
        {
            var m = Preview.Manifest;
            var app = m.App.Length > 0 ? $" Ctrl Alt Daze {m.App}." : "";
            return $"Made on {m.Machine}, {m.Created:d MMM yyyy, HH:mm}.{app}";
        }
    }

    public string AddMissingHelp => Preview.CanAddMissing
        ? "Adds what's new in the backup. Items you already have keep your version; nothing is removed."
        : "Not available: the configuration on this PC can't be read.";

    /// <summary>Per kind: "Flows: 3 new, 2 changed, 1 only on this PC".</summary>
    public IReadOnlyList<string> Summary
    {
        get
        {
            List<string> lines = [];
            foreach (var (kind, label) in Kinds)
            {
                List<string> parts = [];
                Add(parts, Preview.Count(kind, ChangeState.New), "new");
                Add(parts, Preview.Count(kind, ChangeState.Changed), "changed");
                Add(parts, Preview.Count(kind, ChangeState.OnlyHere), "only on this PC");
                var same = Preview.Count(kind, ChangeState.Same);
                if (parts.Count == 0 && same == 0)
                    continue;
                if (same > 0)
                    parts.Add($"{same} same");
                lines.Add($"{label}: {string.Join(", ", parts)}");
            }
            if (Preview.SettingsChanged)
                lines.Add("Settings: changed");
            return lines;
        }
    }

    /// <summary>Everything that differs; "Same" items are only counted in <see cref="Summary"/>.</summary>
    public IReadOnlyList<RestoreItemRow> Rows =>
        [.. Preview.Items.Where(i => i.State != ChangeState.Same).Select(i => new RestoreItemRow(KindLabel(i.Kind), i.Key, StateText(i)))];

    public IReadOnlyList<string> NeedsAttention => Preview.NeedsAttention;

    public IReadOnlyList<string> Warnings => Preview.Warnings;

    public bool HasNeedsAttention => Preview.NeedsAttention.Count > 0;

    public bool HasWarnings => Preview.Warnings.Count > 0;

    /// <summary>Restores; false when the editor refused (a draft opened meanwhile) or the write failed (the card shows why).</summary>
    public bool Apply() => CanRestore && _card.Apply(_archive, SelectedMode);

    private string StateText(ItemChange i) => i.State switch
    {
        ChangeState.New => "New",
        ChangeState.Changed => i.Summary is { Length: > 0 } s ? "Changed: " + s : "Changed",
        _ => SelectedMode == RestoreMode.Replace ? "Only on this PC, will be removed" : "Only on this PC, kept",
    };

    private static string KindLabel(string kind) => kind switch
    {
        "flow" => "Flow",
        "audio" => "Audio",
        "bluetooth" => "Bluetooth",
        "display" => "Display layout",
        "android" => "Android device",
        _ => kind,
    };

    private static void Add(List<string> parts, int n, string what)
    {
        if (n > 0)
            parts.Add($"{n} {what}");
    }
}
