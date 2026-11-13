using DazeControl.Core.Model;
using YamlDotNet.Serialization;

namespace DazeControl.Core.Yaml;

// The YAML shape of config.yaml (ARCHITECTURE → Config). Scalars are strings so that a typo such as
// "enabled: yes" becomes a problem on its flow in ConfigMapper instead of a parse error that loses the whole
// file (D-021). Null = not written = default.

[YamlSerializable]
public sealed class ConfigDocument
{
    public string? Version { get; set; }

    public SettingsDocument? Settings { get; set; }

    public LibraryDocument? Library { get; set; }

    public Dictionary<string, FlowDocument>? Flows { get; set; }
}

[YamlSerializable]
public sealed class SettingsDocument
{
    public PopupDocument? Popup { get; set; }

    public Dictionary<string, StepDefaultDocument>? StepDefaults { get; set; }
}

/// <summary>A <c>settings.stepDefaults</c> entry (D-080): the step's timing keys, all optional.</summary>
[YamlSerializable]
public sealed class StepDefaultDocument
{
    public string? Timeout { get; set; }

    public RetryDocument? Retry { get; set; }

    public string? OnFailure { get; set; }

    public string? Only { get; set; }
}

[YamlSerializable]
public sealed class PopupDocument
{
    public string? Position { get; set; }

    public string? Seconds { get; set; }
}

[YamlSerializable]
public sealed class LibraryDocument
{
    public Dictionary<string, AudioDocument>? Audio { get; set; }

    public Dictionary<string, BluetoothDocument>? Bluetooth { get; set; }

    public Dictionary<string, DisplayLayoutDocument>? Displays { get; set; }

    public Dictionary<string, AndroidDocument>? Android { get; set; }

    /// <summary>Bound devices (D-116): a Bluetooth key and an audio key used as one device.</summary>
    public Dictionary<string, BoundDocument>? Bound { get; set; }

    /// <summary>The old name of <see cref="Android"/>: read only to point at the new one, never written (D-045).</summary>
    public Dictionary<string, AndroidDocument>? Firetv { get; set; }
}

[YamlSerializable]
public sealed class AndroidDocument
{
    public string? Address { get; set; }

    public string? Adb { get; set; }

    /// <summary>Read whole, like <c>with:</c>, so a wrong shape is a problem on this entry, not on the file (D-021).</summary>
    public ParamDocument? Inputs { get; set; }
}

[YamlSerializable]
public sealed class AudioDocument
{
    public string? Match { get; set; }

    public string? Flow { get; set; }
}

[YamlSerializable]
public sealed class DisplayLayoutDocument
{
    public List<string>? Monitors { get; set; }

    public string? Ccd { get; set; }
}

[YamlSerializable]
public sealed class BoundDocument
{
    public string? Bluetooth { get; set; }

    public string? Audio { get; set; }

    public string? Name { get; set; }
}

[YamlSerializable]
public sealed class BluetoothDocument
{
    public string? Address { get; set; }

    public string? Name { get; set; }
}

[YamlSerializable]
public sealed class FlowDocument
{
    public string? Name { get; set; }

    public string? Glyph { get; set; }

    public TrayDocument? Tray { get; set; }

    public List<string>? Hotkeys { get; set; }

    public List<TriggerDocument>? Triggers { get; set; }

    /// <summary>Read only so an old file still loads (D-086); never written.</summary>
    public List<string>? Gamepad { get; set; }

    public string? WhenRunning { get; set; }

    public string? Popup { get; set; }

    public string? Enabled { get; set; }

    public Dictionary<string, InputDocument>? Inputs { get; set; }

    /// <summary>T23.7: output name → text that takes values.</summary>
    public Dictionary<string, string>? Outputs { get; set; }

    public List<NodeDocument>? Nodes { get; set; }
}

/// <summary>A flow input (D-064): <c>{ kind, name, default }</c>.</summary>
[YamlSerializable]
public sealed class InputDocument
{
    public string? Kind { get; set; }

    public string? Name { get; set; }

    public string? Default { get; set; }

    public string? Ask { get; set; }

    public string? Timeout { get; set; }

    public string? OnTimeout { get; set; }
}

/// <summary>
/// A <c>triggers:</c> item (M20): <c>at</c> (+ <c>days</c>), <c>every</c> (+ <c>between</c>), <c>once</c> or <c>on</c>. Days and
/// between are read whole (a word or a list) so a wrong shape is a problem on this trigger only (D-021).
/// </summary>
[YamlSerializable]
public sealed class TriggerDocument
{
    public string? At { get; set; }

    public ParamDocument? Days { get; set; }

    public string? Every { get; set; }

    public ParamDocument? Between { get; set; }

    public string? Once { get; set; }

    public string? On { get; set; }

    public string? Only { get; set; }

    public string? Ssid { get; set; }

    public string? Via { get; set; }

    public string? IfMissed { get; set; }

    public string? Grace { get; set; }
}

[YamlSerializable]
public sealed class TrayDocument
{
    public string? Show { get; set; }

    public string? Folder { get; set; }

    public string? Separator { get; set; }
}

/// <summary>A step (<see cref="Step"/>) or a card (<see cref="Group"/>), never both.</summary>
[YamlSerializable]
public sealed class NodeDocument
{
    public string? Id { get; set; }

    public string? Step { get; set; }

    public List<string>? Group { get; set; }

    public string? Name { get; set; }

    public Dictionary<string, ParamDocument>? With { get; set; }

    public List<AfterDocument>? After { get; set; }

    public string? OnFailure { get; set; }

    public string? Timeout { get; set; }

    public RetryDocument? Retry { get; set; }

    public string? Only { get; set; }
}

[YamlSerializable]
public sealed class RetryDocument
{
    public string? Count { get; set; }

    public string? Delay { get; set; }
}

/// <summary>An <c>after:</c> item: a bare id, or <c>{ id, when }</c> (<see cref="AfterConverter"/>).</summary>
[YamlSerializable]
public sealed class AfterDocument
{
    public string? Id { get; set; }

    public string? When { get; set; }

    /// <summary>Set by <see cref="AfterConverter"/> when the entry has the wrong shape; never written.</summary>
    public string? Malformed { get; set; }
}

/// <summary>
/// Box for a step param, read and written whole by <see cref="ParamValueConverter"/>. The static generator can't
/// handle the abstract <see cref="ParamValue"/> itself, so it only ever sees this concrete, property-less class.
/// </summary>
[YamlSerializable]
public sealed class ParamDocument
{
    public ParamDocument()
        : this(new ScalarParam(""))
    {
    }

    public ParamDocument(ParamValue value) => _value = value;

    private readonly ParamValue _value;

    internal ParamValue ToValue() => _value;
}

/// <summary>Source-generated YamlDotNet type info: no reflection, safe under Native AOT (D-005).</summary>
[YamlStaticContext]
public partial class ConfigYamlContext : StaticContext;
