using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Basic;

/// <summary>
/// <c>text.make</c> (T23.7, DESIGN §9): gives its text, already filled by the runner, as the value <c>text</c>. Set once per
/// run (each try sets it anew); later steps read it as <c>{id.text}</c>. No summary: the text may hold a Sensitive value.
/// </summary>
public sealed class MakeTextProvider : IStepProvider
{
    public string Id => ProviderCatalog.MakeTextId;

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var text = context.Node.With.TryGetValue(StepValues.MakeTextParam, out var value) && value is ScalarParam scalar ? scalar.Text : "";
        context.SetValue(StepValues.MakeTextParam, text);
        return Task.CompletedTask;
    }
}
