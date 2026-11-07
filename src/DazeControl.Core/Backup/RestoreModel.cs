using DazeControl.Core.Model;

namespace DazeControl.Core.Backup;

/// <summary>Replace = the PC ends up exactly like the backup; AddMissing = new items are added, existing ones keep the current version, nothing is removed (D-103).</summary>
public enum RestoreMode { Replace, AddMissing }

public enum ChangeState { New, Changed, Same, OnlyHere }

/// <summary>One flow or library item in the preview. <paramref name="Kind"/> is "flow", "audio", "bluetooth", "display" or "android"; <paramref name="Summary"/> is a one-line reason for Changed.</summary>
public sealed record ItemChange(string Kind, string Key, ChangeState State, string? Summary = null);

/// <summary>Where a restore reads and writes. Every path is the caller's (tests pass a temp folder, `--config` redirects them).</summary>
public sealed record RestoreTarget(string ConfigPath, string? EditorJsonPath, string SafetyFolder);

/// <summary>What the preview may ask the machine. Credential names come from the engine's `credentials` query (null = unknown, nothing is reported); paths are checked once, at the user's action.</summary>
public sealed record PreviewEnvironment(IReadOnlyCollection<string>? SavedCredentials = null, Func<string, bool>? PathExists = null);

/// <summary>"What will change" (DESIGN §6). CanReplace is false when the backup's config can't be read; CanAddMissing also needs the current one readable. Never throws for content problems.</summary>
public sealed record RestorePreview(
    BackupManifest Manifest,
    bool CanReplace,
    bool CanAddMissing,
    string? Unreadable,
    IReadOnlyList<ItemChange> Items,
    bool SettingsChanged,
    IReadOnlyList<string> NeedsAttention,
    IReadOnlyList<string> Warnings)
{
    public int Count(string kind, ChangeState state) => Items.Count(i => i.Kind == kind && i.State == state);
}

/// <summary>What a restore did. <paramref name="SafetyPath"/> is null when there was no config to save; pass the outcome to <see cref="BackupService.Undo"/>.</summary>
public sealed record RestoreOutcome(
    RestoreMode Mode, string? SafetyPath, bool EditorLayoutRestored, IReadOnlyList<string> KeptCurrent, IReadOnlyList<string> Notes)
{
    /// <summary>editor.json as it was before the restore (kept in memory for Undo; null = there was none).</summary>
    public byte[]? PreviousEditorJson { get; init; }
}

public enum RestoreFailure { Unreadable, CurrentUnreadable, SafetyCopyFailed, WriteFailed }

/// <summary>A restore that changed nothing (or, for WriteFailed, left the old config in place); Message is the sentence for the user (DESIGN §10).</summary>
public sealed class RestoreException(RestoreFailure kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public RestoreFailure Kind { get; } = kind;
}
