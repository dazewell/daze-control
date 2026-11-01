using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DazeControl.Core.Yaml;

/// <summary>Reads config.yaml through the static context (D-005).</summary>
public static class ConfigYaml
{
    private static readonly IDeserializer Deserializer = new StaticDeserializerBuilder(new ConfigYamlContext())
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties() // the spike DTOs know only part of the schema
        .Build();

    public static ConfigDocument Parse(string yaml) =>
        Deserializer.Deserialize<ConfigDocument>(yaml) ?? new ConfigDocument();
}
