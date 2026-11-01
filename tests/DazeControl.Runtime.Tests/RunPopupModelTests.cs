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
