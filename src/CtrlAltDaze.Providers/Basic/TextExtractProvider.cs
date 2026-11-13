using CtrlAltDaze.Core.Catalog;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Basic;

/// <summary>
/// <c>text.extract</c> (T25.2, D-144): takes one part of a text already filled by the runner (D-129): a JSON field, a
/// pattern's first group, what stands between two texts, or one line. All of it is <see cref="TextExtract"/>, the same code the
/// validator uses. A part that isn't there fails the step with a sentence, so the failure link is the "else". Reads nothing
/// from the system, holds nothing (idle = zero). The text is never put in a sentence or summary: it may be a secret.
/// </summary>
public sealed class TextExtractProvider : IStepProvider
{
    public string Id => ProviderCatalog.ExtractId;

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var result = TextExtract.Extract(context.Text("by"), context.Text("text"), context.Text("find"), context.Text("until"));
        if (result.Value is not { } part)
            throw new StepFailedException(result.Failure ?? "Nothing could be taken from the text.");
        context.SetValue("text", part);
        if (TextExtract.IsNumber(part))
            context.SetNumber("number", double.Parse(part.Trim(), System.Globalization.CultureInfo.InvariantCulture));
        context.SetSummary($"Took {part.Length:N0} character{(part.Length == 1 ? "" : "s")}");
        return Task.CompletedTask;
    }
}
