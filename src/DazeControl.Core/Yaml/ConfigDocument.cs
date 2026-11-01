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

    public string? WhenRunning { get; set; }

    public string? Popup { get; set; }

    public string? Enabled { get; set; }

    public List<NodeDocument>? Nodes { get; set; }
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
