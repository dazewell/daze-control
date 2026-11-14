namespace CtrlAltDaze.Core.Model;

/// <summary>
/// A step parameter as written under <c>with:</c>. Scalars stay text: what "20" or "true" means is the
/// provider's <c>ParamSpec</c>'s business, checked by validation.
/// </summary>
public abstract record ParamValue
{
    public static implicit operator ParamValue(string text) => new ScalarParam(text);
}

public sealed record ScalarParam(string Text) : ParamValue
{
    public override string ToString() => Text;
}

/// <summary>E.g. <c>process.run</c>'s <c>args</c>.</summary>
public sealed record ListParam(ValueList<ParamValue> Items) : ParamValue
{
    public override string ToString() => Items.ToString();
}

/// <summary>E.g. <c>http.request</c>'s <c>headers</c>.</summary>
public sealed record MapParam(ValueDictionary<ParamValue> Entries) : ParamValue
{
    public override string ToString() => Entries.ToString();
}
