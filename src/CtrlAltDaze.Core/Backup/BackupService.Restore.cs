using System.Text;
using System.Text.Json.Nodes;
using CtrlAltDaze.Core.Validation;
using CtrlAltDaze.Core.Yaml;

namespace CtrlAltDaze.Core.Backup;

// Preview, restore and undo (T19.2, D-103). Runs in the editor and `cad`, never in the engine; the engine just hot-reloads the new file (D-021).
// Callers own the rules about the editor: refuse while a flow draft is open, and keep the outcome for Undo.
public sealed partial class BackupService
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Compares the backup with the config at <paramref name="target"/> (a missing file is an empty config) and lists what needs attention. Reads only.</summary>
    public RestorePreview Preview(BackupArchive archive, RestoreTarget target, PreviewEnvironment? env = null)
    {
        env ??= new PreviewEnvironment();
        var warnings = SecretScan.Find(Utf8.GetString(archive.Config));
        var theirs = ConfigYaml.Load(Utf8.GetString(archive.Config));
        if (theirs.Config is null)
            return new RestorePreview(archive.Manifest, false, false, "config.yaml in this backup can't be read: " + (theirs.Problems.Count > 0 ? theirs.Problems[0].Message : "unknown error"),
                [], false, [], warnings);
        var mine = LoadCurrent(target);
        var (items, settings) = RestoreCompare.Compare(theirs.Config, mine ?? Model.Configuration.Empty);
        var problems = theirs.Problems.Concat(ConfigValidator.Validate(theirs.Config, theirs.Problems)).ToList();
        var attention = RestoreCompare.NeedsAttention(theirs.Config, problems, archive.Manifest.Machine, machine, env.SavedCredentials,
            env.PathExists ?? (p => File.Exists(p) || Directory.Exists(p)));
        return new RestorePreview(archive.Manifest, true, mine is not null, null, items, settings, attention, warnings);
    }

    /// <summary>
    /// Applies the backup: safety copy of the current state first (skipped when there is none), then one atomic config.yaml replace, then editor.json.
    /// Throws <see cref="RestoreException"/>; on any failure the old config is untouched.
    /// </summary>
    public RestoreOutcome Restore(BackupArchive archive, RestoreMode mode, RestoreTarget target, bool includeEditorLayout = false)
    {
        var theirs = ConfigYaml.Load(Utf8.GetString(archive.Config));
        if (theirs.Config is null)
            throw new RestoreException(RestoreFailure.Unreadable, "config.yaml in this backup can't be read, so nothing was restored.");
        var text = archive.Config;
        List<string> kept = [];
        var lossy = false;
        if (mode == RestoreMode.AddMissing)
        {
            var mine = LoadCurrent(target) ?? throw new RestoreException(RestoreFailure.CurrentUnreadable,
                "The config on this PC can't be read, so nothing was merged. Use Replace everything.");
            var (merged, keptCurrent) = RestoreCompare.Merge(theirs.Config, mine);
            var mergedText = ConfigYaml.Save(merged);
            if (ConfigYaml.Load(mergedText).Config is null)
                throw new RestoreException(RestoreFailure.Unreadable, "The merged config wouldn't load, so nothing was restored.");
            text = Utf8.GetBytes(mergedText);
            kept = keptCurrent;
            lossy = true;
        }

        var safety = SafetyCopy(target);
        var notes = new List<string>();
        if (lossy) notes.Add("Add what's missing rewrites config.yaml: comments and settings this version doesn't know are not kept. Undo restores the old file.");
        var previousEditor = target.EditorJsonPath is { } ep ? files.TryReadAll(ep) : null;
        try { files.ReplaceAtomic(target.ConfigPath, text); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RestoreException(RestoreFailure.WriteFailed, $"Couldn't write {target.ConfigPath}: {e.Message} Nothing was changed.", e);
        }

        var editor = false;
        if (includeEditorLayout && archive.EditorJson is { } json && target.EditorJsonPath is { } editorPath)
        {
            if (WithoutWindow(json, files.TryReadAll(editorPath)) is { } layout)
            {
                try { files.ReplaceAtomic(editorPath, layout); editor = true; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    notes.Add("The editor layout couldn't be restored: " + e.Message);
                }
            }
            else notes.Add("The editor layout in this backup can't be read, so it was skipped.");
        }
        return new RestoreOutcome(mode, safety, editor, kept, notes) { PreviousEditorJson = previousEditor };
    }

    /// <summary>Puts back what <see cref="Restore"/> replaced, from its safety copy, without a new safety copy.</summary>
    public void Undo(RestoreOutcome outcome, RestoreTarget target)
    {
        if (outcome.SafetyPath is null)
            files.Delete(target.ConfigPath); // there was no config before the restore
        else
            files.ReplaceAtomic(target.ConfigPath, Open(outcome.SafetyPath).Config);
        if (outcome.EditorLayoutRestored && target.EditorJsonPath is { } path)
        {
            if (outcome.PreviousEditorJson is { } json) files.ReplaceAtomic(path, json);
            else files.Delete(path);
        }
    }

    // null = unreadable or damaged (a missing file is an empty config).
    private Model.Configuration? LoadCurrent(RestoreTarget target) =>
        files.TryReadAll(target.ConfigPath) is not { } bytes ? Model.Configuration.Empty : Whole(ConfigYaml.Load(Utf8.GetString(bytes)));

    // A config the loader had to drop things from can't be merged into: the merge would write the damaged model back.
    private static Model.Configuration? Whole(ConfigLoad load) =>
        load.Problems.Any(p => p.Severity == Severity.Error) ? null : load.Config;

    private string? SafetyCopy(RestoreTarget target)
    {
        try
        {
            if (files.TryReadAll(target.ConfigPath) is not { } config) return null;
            var editor = target.EditorJsonPath is { } p ? files.TryReadAll(p) : null;
            return CreateIn(target.SafetyFolder, new BackupContents(config, editor), BeforeRestore).Path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RestoreException(RestoreFailure.SafetyCopyFailed, "Couldn't make a safety copy first, so nothing was restored.", e);
        }
    }

    // The window rectangle and DPI belong to one screen setup (DESIGN §2): keep this PC's, take the rest. Null = not JSON.
    private static byte[]? WithoutWindow(byte[] backup, byte[]? current)
    {
        try
        {
            if (JsonNode.Parse(backup) is not JsonObject obj) return null;
            obj.Remove("window");
            if (current is not null && JsonNode.Parse(current) is JsonObject cur && cur["window"] is { } w)
                obj["window"] = w.DeepClone();
            return Utf8.GetBytes(obj.ToJsonString());
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
