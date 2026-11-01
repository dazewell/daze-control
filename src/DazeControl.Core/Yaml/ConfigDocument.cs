using YamlDotNet.Serialization;

namespace DazeControl.Core.Yaml;

// Minimal shape of config.yaml for the T0.5 AOT spike; the full DTOs and mapper arrive in T1.2.

[YamlSerializable]
public sealed class ConfigDocument
{
    public int Version { get; set; }

    public Dictionary<string, FlowDocument> Flows { get; set; } = [];
}

[YamlSerializable]
public sealed class FlowDocument
{
    public string? Name { get; set; }

    public List<string> Hotkeys { get; set; } = [];
}

/// <summary>Source-generated YamlDotNet type info: no reflection, safe under Native AOT (D-005).</summary>
[YamlStaticContext]
public partial class ConfigYamlContext : StaticContext;
