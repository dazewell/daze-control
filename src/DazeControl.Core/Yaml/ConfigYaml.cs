using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DazeControl.Core.Yaml;

/// <param name="Config">Null when the file can't be used at all (not YAML, newer schema): keep what runs now.</param>
/// <param name="Problems">From reading and mapping; validation adds its own.</param>
public sealed record ConfigLoad(Configuration? Config, ValueList<Problem> Problems);

/// <summary>Reads and writes config.yaml through the static context (D-005).</summary>
public static class ConfigYaml
{
    private static readonly IDeserializer Deserializer = new StaticDeserializerBuilder(new ConfigYamlContext())
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new ParamValueConverter())
        .WithTypeConverter(new AfterConverter())
        .IgnoreUnmatchedProperties() // open question in ARCHITECTURE: unknown keys are not reported yet
        .Build();

    private static readonly ISerializer Serializer = new StaticSerializerBuilder(new ConfigYamlContext())
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new ParamValueConverter())
        .WithTypeConverter(new AfterConverter())
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .WithIndentedSequences()
        .Build();

    public static ConfigLoad Load(string yaml)
    {
        ConfigDocument document;
        try
        {
            document = Deserializer.Deserialize<ConfigDocument?>(yaml) ?? new ConfigDocument();
        }
        catch (YamlException e)
        {
            var message = (e.InnerException ?? e).Message;
            return new ConfigLoad(null, [new Problem(Severity.Error, "", $"Line {e.Start.Line}, column {e.Start.Column}: {message}")]);
        }
#pragma warning disable CA1031 // fail-soft: whatever the deserializer throws, the file is unusable but the engine lives
        catch (Exception e)
#pragma warning restore CA1031
        {
            return new ConfigLoad(null, [new Problem(Severity.Error, "", $"Can't read the file: {e.Message}")]);
        }
        var problems = new List<Problem>();
        var config = ConfigMapper.ToModel(document, problems);
        var unusable = problems.Exists(p => p.Path == "version");
        return new ConfigLoad(unusable ? null : config, new(problems));
    }

    public static string Save(Configuration config) => Serialize(ConfigMapper.ToDocument(config));

    internal static string Serialize(ConfigDocument document) => Serializer.Serialize(document);
}
