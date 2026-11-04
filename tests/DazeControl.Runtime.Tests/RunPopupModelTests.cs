using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Runtime.Popup;

namespace DazeControl.Runtime.Tests;

public sealed class RunPopupModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Run = Guid.NewGuid();
    private static readonly PopupSettings Settings = new(PopupPosition.BottomCenter, 3);

    private static readonly Flow Technics = new("connect-technics", "Connect Technics",
    [
        new StepNode("bt", "bluetooth.connect") { Name = "Connect Technics" },
        new StepNode("out", "audio.setDefault"),
        new StepNode("hp", "bluetooth.connect") { Name = "Connect headphones", OnFailure = FailurePolicy.Ignore },
    ]);

    private static RunPopupModel Started(Flow? flow = null)
    {
        var model = new RunPopupModel();
        model.Apply(new RunStarted(Run, "connect-technics", T0, 3), flow ?? Technics, Settings);
        return model;
    }

    private static void Node(RunPopupModel model, string id, NodeOutcome outcome = NodeOutcome.Succeeded, string? error = null)
    {
        model.Apply(new NodeStarted(Run, "connect-technics", T0, id, 1), null, Settings);
        model.Apply(new NodeFinished(Run, "connect-technics", T0, new NodeResult(id, outcome, Error: error)), null, Settings);
    }

    private static PopupView Finish(RunPopupModel model, FlowOutcome outcome, double seconds = 3.1)
    {
        Assert.True(model.Apply(new RunFinished(Run, "connect-technics", T0.AddSeconds(seconds), outcome), null, Settings));
        return model.Current!;
    }

    [Fact]
    public void ARefusedDesktopRunIsAReadableFailureThatIsntClickable()
    {
        var model = new RunPopupModel();

        var view = model.Refused("gone", "Desktop shortcut", "This shortcut's flow isn't there anymore.", Settings);

        Assert.Same(view, model.Current);
        Assert.Equal(PopupTone.Failed, view.Tone);
        Assert.Equal("Didn't start", view.Headline);
        Assert.Equal(RunPopupModel.FailureMinimum, view.HideAfter);
        Assert.False(view.Clickable);
    }

    [Fact]
    public void RunningShowsCountAndCurrentStep()
    {
        var model = Started();
        Assert.Equal("Starting…", model.Current!.Detail);
        Node(model, "bt");
        model.Apply(new NodeStarted(Run, "connect-technics", T0, "out", 1), null, Settings);

        var view = model.Current!;
        Assert.Equal(("Connect Technics", "1 of 3", PopupTone.Running), (view.Title, view.Status, view.Tone));
        Assert.Equal(1 / 3.0, view.Progress, 3);
        Assert.Equal(DazeControl.Core.Catalog.ProviderCatalog.Find("audio.setDefault")!.Title + "…", view.Detail);
        Assert.Null(view.HideAfter);
        Assert.False(view.Clickable);
    }

    [Fact]
    public void UnnamedStepUsesProviderTitleAndParallelStepsAreCounted()
    {
        var model = Started();
        model.Apply(new NodeStarted(Run, "connect-technics", T0, "out", 1), null, Settings);
        model.Apply(new NodeStarted(Run, "connect-technics", T0, "hp", 1), null, Settings);

        Assert.DoesNotContain("out", model.Current!.Detail, StringComparison.Ordinal);
        Assert.EndsWith("… and 1 more", model.Current!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessSaysDoneWithDurationAndIsClickThrough()
    {
        var model = Started();
        Node(model, "bt");
        Node(model, "out");
        Node(model, "hp");
        var view = Finish(model, FlowOutcome.Succeeded);

        Assert.Equal(("Done", "Took 3.1 s", PopupTone.Succeeded, 1.0), (view.Status, view.Detail, view.Tone, view.Progress));
        Assert.Null(view.Headline);
        Assert.Equal(TimeSpan.FromSeconds(3), view.HideAfter);
        Assert.False(view.Clickable);
        Assert.Null(view.Hint);
    }

    [Fact]
    public void PartialNamesTheStepThatDidntWork()
    {
        var model = Started();
        Node(model, "bt");
        Node(model, "hp", NodeOutcome.FailedIgnored, "Bluetooth didn't connect in 10 s.");
        Node(model, "out");
        var view = Finish(model, FlowOutcome.PartiallySucceeded);

        Assert.Equal(("", "Done, with a problem", PopupTone.Partial), (view.Status, view.Headline, view.Tone));
        Assert.Equal("Connect headphones didn't work, so it went on without it.", view.Detail);
        Assert.False(view.Clickable);
    }

    [Fact]
    public void FailureGivesFirstReasonStaysSixSecondsAndTakesClicks()
    {
        var model = Started();
        Node(model, "hp", NodeOutcome.FailedIgnored, "Headphones are off.");
        Node(model, "bt", NodeOutcome.Failed, "Bluetooth didn't connect in 10 s.");
        Node(model, "out", NodeOutcome.Skipped);
        var view = Finish(model, FlowOutcome.Failed);

        Assert.Equal(("", "Didn't finish", PopupTone.Failed), (view.Status, view.Headline, view.Tone));
        Assert.Equal("Bluetooth didn't connect in 10 s.", view.Detail);
        Assert.Equal("Select to see what happened", view.Hint);
        Assert.Equal(RunPopupModel.FailureMinimum, view.HideAfter);
        Assert.True(view.Clickable);
    }

    [Fact]
    public void FailureWithoutReasonNamesTheStep()
    {
        var model = Started();
        Node(model, "bt", NodeOutcome.Failed);
        Assert.Equal("Connect Technics didn't work.", Finish(model, FlowOutcome.Failed).Detail);
    }

    [Fact]
    public void LongerDurationSettingAlsoAppliesToFailure()
    {
        var model = new RunPopupModel();
        var settings = new PopupSettings(PopupPosition.TopLeft, 10);
        model.Apply(new RunStarted(Run, "f", T0, 1), Technics, settings);
        model.Apply(new RunFinished(Run, "f", T0, FlowOutcome.Failed), null, settings);
        Assert.Equal(TimeSpan.FromSeconds(10), model.Current!.HideAfter);
    }

    [Fact]
    public void CancelledIsNeutralAndClickThrough()
    {
        var view = Finish(Started(), FlowOutcome.Cancelled);
        Assert.Equal(("Stopped", PopupTone.Stopped, false), (view.Headline, view.Tone, view.Clickable));
    }

    [Fact]
    public void FlowWithPopupOffShowsNothing()
    {
        var model = Started(Technics with { Popup = false });
        Assert.Null(model.Current);
        Assert.False(model.Apply(new RunFinished(Run, "f", T0, FlowOutcome.Succeeded), null, Settings));
        Assert.Null(model.Current);
    }

    [Fact]
    public void TheRunsOwnSnapshotWinsOverTheGivenFlow()
    {
        var model = new RunPopupModel();
        var started = new RunStarted(Run, "connect-technics", T0, 3) { Flow = Technics with { Name = "As it runs" } };
        model.Apply(started, Technics with { Name = "Reloaded", Popup = false }, Settings);
        Assert.Equal("As it runs", model.Current!.Title);
    }

    /// <summary>The user's Connect Technics flow: Technics, else Sony and the speakers (D-065).</summary>
    private static readonly Flow WithFallback = new("connect-technics", "Connect Technics",
    [
        new StepNode("bt", "bluetooth.connect") { Name = "Connect Technics" },
        new StepNode("out", "audio.setDefault") { After = [new Edge("bt")] },
        new StepNode("connect", "bluetooth.connect") { Name = "Connect Sony", After = [new Edge("bt", EdgeWhen.Failure)] },
        new StepNode("setDefault", "audio.setDefault") { After = [new Edge("connect")] },
    ]);

    private static void NotRun(RunPopupModel model, string id, NodeOutcome outcome) =>
        model.Apply(new NodeFinished(Run, "connect-technics", T0, new NodeResult(id, outcome, Attempts: 0)), null, Settings);

    [Fact]
    public void FallbacksThatWerentNeededCountForNothing()
    {
        var model = Started(WithFallback);
        Assert.Equal("0 of 2", model.Current!.Status);
        Node(model, "bt");
        Assert.Equal(("1 of 2", 0.5), (model.Current!.Status, model.Current!.Progress));
        NotRun(model, "connect", NodeOutcome.NotNeeded);
        NotRun(model, "setDefault", NodeOutcome.NotNeeded);
        Node(model, "out");
        Assert.Equal(("2 of 2", 1.0), (model.Current!.Status, model.Current!.Progress));

        var view = Finish(model, FlowOutcome.Succeeded);
        Assert.Equal(("Done", "Took 3.1 s", PopupTone.Succeeded), (view.Status, view.Detail, view.Tone));
    }

    [Fact]
    public void AFallbackJoinsTheCountWhenItStarts()
    {
        var model = Started(WithFallback);
        Node(model, "bt", NodeOutcome.Failed);
        NotRun(model, "out", NodeOutcome.Skipped);
        Assert.Equal("2 of 2", model.Current!.Status);
        model.Apply(new NodeStarted(Run, "connect-technics", T0, "connect", 1), null, Settings);
        Assert.Equal(("2 of 3", "Connect Sony…"), (model.Current!.Status, model.Current!.Detail));
    }

    [Fact]
    public void AFailureItsFallbackFixedEndsDoneInTheNormalTone()
    {
        var model = Started(WithFallback);
        Node(model, "bt", NodeOutcome.Failed, "Technics isn't in range.");
        NotRun(model, "out", NodeOutcome.Skipped);
        Node(model, "connect");
        Node(model, "setDefault");

        var view = Finish(model, FlowOutcome.Succeeded);

        Assert.Equal(("Done", null, PopupTone.Succeeded, false), (view.Status, view.Headline, view.Tone, view.Clickable));
        Assert.Equal("Connect Technics didn't work, so Connect Sony ran instead.", view.Detail); // #48
        Assert.Equal(1.0, view.Progress);
    }

    [Fact]
    public void EventsOfUnknownRunsAreIgnored()
    {
        var model = new RunPopupModel();
        Assert.False(model.Apply(new NodeStarted(Run, "f", T0, "bt", 1), null, Settings));
        Assert.Null(model.Current);
    }

    [Theory]
    [InlineData(0.04, "0.0 s")]
    [InlineData(3.14, "3.1 s")]
    [InlineData(45.4, "45 s")]
    [InlineData(125, "2 min 5 s")]
    public void DurationWording(double seconds, string expected) =>
        Assert.Equal(expected, RunPopupModel.Seconds(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(PopupPosition.BottomCenter, 930, 1900)]
    [InlineData(PopupPosition.TopCenter, 930, 124)]
    [InlineData(PopupPosition.TopLeft, 124, 124)]
    [InlineData(PopupPosition.TopRight, 1736, 124)]
    [InlineData(PopupPosition.BottomLeft, 124, 1900)]
    [InlineData(PopupPosition.BottomRight, 1736, 1900)]
    public void SixPositionsInTheWorkArea(PopupPosition position, int x, int y) =>
        Assert.Equal((x, y), PopupPlacement.Place(100, 100, 2000, 2000, 340, 176, position, 24));
}
