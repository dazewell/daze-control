using CtrlAltDaze.Core.Model;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace CtrlAltDaze.Core.Yaml;

/// <summary>Step params of any YAML shape; scalars stay text (see <see cref="ParamValue"/>).</summary>
internal sealed class ParamValueConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(ParamDocument);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer) => new ParamDocument(Read(parser));

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        Write(emitter, ((ParamDocument?)value)?.ToValue() ?? new ScalarParam(""));

    private static ParamValue Read(IParser parser)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
            return new ScalarParam(scalar.Value);
        if (parser.TryConsume<SequenceStart>(out _))
        {
            var items = new List<ParamValue>();
            while (!parser.TryConsume<SequenceEnd>(out _))
                items.Add(Read(parser));
            return new ListParam(new(items));
        }
        if (parser.TryConsume<MappingStart>(out _))
        {
            var entries = new List<KeyValuePair<string, ParamValue>>();
            while (!parser.TryConsume<MappingEnd>(out _))
                entries.Add(new(parser.Consume<Scalar>().Value, Read(parser)));
            return new MapParam(new(entries));
        }
        var current = parser.Current;
        throw new YamlException(current?.Start ?? Mark.Empty, current?.End ?? Mark.Empty, "Expected a value, a list or a mapping.");
    }

    private static void Write(IEmitter emitter, ParamValue value)
    {
        switch (value)
        {
            case ScalarParam scalar:
                emitter.Emit(new Scalar(scalar.Text));
                break;
            case ListParam list:
                emitter.Emit(new SequenceStart(null, null, true, SequenceStyle.Flow));
                foreach (var item in list.Items)
                    Write(emitter, item);
                emitter.Emit(new SequenceEnd());
                break;
            case MapParam map:
                emitter.Emit(new MappingStart(null, null, true, MappingStyle.Flow));
                foreach (var (key, item) in map.Entries)
                {
                    emitter.Emit(new Scalar(key));
                    Write(emitter, item);
                }
                emitter.Emit(new MappingEnd());
                break;
        }
    }
}

/// <summary><c>after: [bt, { id: hp, when: failure }]</c>: bare ids for the default <c>when</c>.</summary>
internal sealed class AfterConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(AfterDocument);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        // Wrong shapes become AfterDocument.Malformed, which the mapper reports on the flow (D-023), instead of
        // an exception that would lose the whole file.
        if (parser.TryConsume<Scalar>(out var scalar))
            return new AfterDocument { Id = scalar.Value };
        var after = new AfterDocument();
        if (!parser.TryConsume<MappingStart>(out _))
        {
            parser.SkipThisAndNestedEvents();
            after.Malformed = "Write an id, or { id: …, when: … }.";
            return after;
        }
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            if (!parser.TryConsume<Scalar>(out var keyScalar))
            {
                parser.SkipThisAndNestedEvents(); // complex key
                parser.SkipThisAndNestedEvents(); // its value
                continue;
            }
            var key = keyScalar.Value;
            if (key is not ("id" or "when"))
            {
                parser.SkipThisAndNestedEvents(); // unknown keys are ignored, like everywhere else (ConfigYaml)
                continue;
            }
            if (!parser.TryConsume<Scalar>(out var value))
            {
                parser.SkipThisAndNestedEvents();
                after.Malformed = $"{key} takes a single value.";
                continue;
            }
            if (key == "id")
                after.Id = value.Value;
            else
                after.When = value.Value;
        }
        return after;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var after = (AfterDocument)value!;
        if (after.When is null)
        {
            emitter.Emit(new Scalar(after.Id ?? ""));
            return;
        }
        emitter.Emit(new MappingStart(null, null, true, MappingStyle.Flow));
        emitter.Emit(new Scalar("id"));
        emitter.Emit(new Scalar(after.Id ?? ""));
        emitter.Emit(new Scalar("when"));
        emitter.Emit(new Scalar(after.When));
        emitter.Emit(new MappingEnd());
    }
}
