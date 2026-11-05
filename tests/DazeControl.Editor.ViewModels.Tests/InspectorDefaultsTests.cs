using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Inspector;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>T14.4: the inspector's Default / Set rows, Reset, "Use these for all … steps…", chips and the D-059 switch.</summary>
public sealed class InspectorDefaultsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DazeControl.Tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private readonly ConfigSession _session;
    private readonly FlowCanvasViewModel _canvas;
    private readonly InspectorViewModel _inspector;

    public InspectorDefaultsTests()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.yaml");
        File.WriteAllText(path, FlowCanvasViewModelTests.Yaml);
        _session = new ConfigSession(new ConfigStore(path, Path.Combine(_folder, "backups"), _time), _time, action => action());
        _session.Load();
        _canvas = new FlowCanvasViewModel(_session, "f");
        _inspector = new InspectorViewModel(_session, _canvas, new FakeEngine { IsRunning = true }, action => action(), _ => true);
    }

    public void Dispose()
    {
        _inspector.Dispose();
        _canvas.Dispose();
        _session.Dispose();
        _time.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private StepNode Step(string id) => (StepNode)_canvas.Flow!.FindNode(id)!;

    private void SetDefault(string stepId, StepDefault? value)
    {
        using var editor = new StepDefaultsEditor(_session);
        editor.Set(stepId, value);
    }

    [Fact]
    public void ResetClearsOnlyThatField()
    {
        _canvas.Select("vol");
        Assert.True(_inspector.TimeoutIsSet);
        Assert.True(_inspector.RetriesIsSet);

        _inspector.ResetTimeoutCommand.Execute(null);

        Assert.Null(Step("vol").Timeout);
        Assert.Equal(3, Step("vol").RetryCount);
        Assert.False(_inspector.TimeoutIsSet);
        Assert.True(_inspector.RetriesIsSet);
    }

    [Fact]
    public void AChangedDefaultMovesInheritedStepsAndNotSetOnes()
    {
        SetDefault("wait", new StepDefault { Timeout = TimeSpan.FromSeconds(9) });
        _canvas.Select("w1");
        _inspector.Timeout = 3; // w2 stays inheriting
        Assert.True(_inspector.TimeoutIsSet);

        SetDefault("wait", new StepDefault { Timeout = TimeSpan.FromSeconds(12) });
        Assert.Equal(3, _inspector.Timeout);
        _canvas.Select("w2");
        Assert.Equal(12, _inspector.Timeout);
        Assert.False(_inspector.TimeoutIsSet);
        Assert.Equal("Use the default (12 s)", _inspector.ResetTimeoutTip);
        Assert.Equal("From the default for Wait steps", _inspector.DefaultsCaption);
    }

    [Fact]
    public void PickingTheOtherPolicySetsItEvenOverADefault()
    {
        SetDefault("wait", new StepDefault { OnFailure = FailurePolicy.Skip });
        _canvas.Select("w1");
        Assert.Equal((int)FailurePolicy.Skip, _inspector.PolicyIndex);
        Assert.False(_inspector.PolicyIsSet);

        _inspector.PolicyIndex = (int)FailurePolicy.Stop;

        Assert.Equal(FailurePolicy.Stop, Step("w1").OnFailure);
        Assert.True(_inspector.PolicyIsSet);
        _inspector.ResetPolicyCommand.Execute(null);
        Assert.Null(Step("w1").OnFailure);
    }

    [Fact]
    public void UseForAllShowsHowManyStepsFollowAndCanBeUndone()
    {
        _canvas.Select("w1");
        _inspector.Timeout = 4;
        Assert.Equal("Use these for all Wait steps…", _inspector.UseForAllText);
        Assert.Contains("Applies to 1 other step in 1 flow", _inspector.UseForAllApplies);
        Assert.Contains("Give up after 4 s", _inspector.UseForAllValues);

        _inspector.ApplyUseForAllCommand.Execute(null);

        var saved = _session.Current.Settings.StepDefaults["wait"];
        Assert.Equal(TimeSpan.FromSeconds(4), saved.Timeout);
        Assert.Equal(0, saved.RetryCount);
        Assert.Equal(FailurePolicy.Stop, saved.OnFailure);
        Assert.Equal(TimeSpan.FromSeconds(4), Step("w1").Timeout); // steps keep what they set
        Assert.Null(Step("w2").Timeout);
        Assert.Equal("Saved as the default for Wait steps", _inspector.DefaultsNotice);

        _canvas.Select("w2");
        Assert.Equal(4, _inspector.Timeout);

        _canvas.Select("w1");
        _inspector.ApplyUseForAllCommand.Execute(null);
        _inspector.UndoDefaultsCommand.Execute(null);
        Assert.Equal(TimeSpan.FromSeconds(4), _session.Current.Settings.StepDefaults["wait"].Timeout);
        Assert.False(_inspector.HasDefaultsNotice);

        _inspector.ApplyUseForAllCommand.Execute(null);
        SetDefault("wait", null);
        _canvas.Select("w1");
        _inspector.Timeout = 6;
        _inspector.ApplyUseForAllCommand.Execute(null);
        _inspector.UndoDefaultsCommand.Execute(null);
        Assert.False(_session.Current.Settings.StepDefaults.ContainsKey("wait"));
    }

    [Fact]
    public void ChipsShowEffectiveValuesDimmerWhenInherited()
    {
        var node = _canvas.Find("w1")!;
        Assert.Empty(node.Badges);

        SetDefault("wait", new StepDefault { Timeout = TimeSpan.FromSeconds(9), RetryCount = 2 });

        node = _canvas.Find("w1")!;
        Assert.Equal(["×2", "9s"], node.Badges.Select(b => b.Text));
        Assert.True(node.BadgesInherited);
        Assert.False(_canvas.Find("vol")!.BadgesInherited); // sets its own retry and timeout
    }

    [Fact]
    public void TheFallbackSwitchUsesTheEffectivePolicy()
    {
        _canvas.Select("w1");
        SetDefault("wait", new StepDefault { OnFailure = FailurePolicy.Skip });
        _canvas.AddFallback("w1", "notify");
        Assert.Null(Step("w1").OnFailure); // inherited Skip already does what a fallback needs

        SetDefault("bluetooth.connect", new StepDefault { OnFailure = FailurePolicy.Stop });
        _canvas.AddFallback("bt", "notify");
        Assert.Equal(FailurePolicy.Skip, Step("bt").OnFailure); // inherited Stop: Skip is set on the step
    }

    [Fact]
    public void OnlyIfFollowsTheTypeDefaultAlwaysOverridesItAndResetReturns()
    {
        SetDefault("bluetooth.connect", new StepDefault { Only = "disconnected" });
        _canvas.Select("bt");
        Assert.Equal(1, _inspector.OnlyIfIndex); // follows the default, not set on the step
        Assert.False(_inspector.OnlyIfIsSet);
        Assert.Equal("Always", _inspector.OnlyIfOptions[0]);
        Assert.Contains("it isn't connected", _inspector.ResetOnlyIfTip.Replace("’", "'"));
        Assert.Contains(_canvas.Find("bt")!.Badges, b => b.Text == "Only if…" && b.Inherited);

        _inspector.OnlyIfIndex = 0;
        Assert.Equal("always", Step("bt").Only); // explicit: still runs
        Assert.True(_inspector.OnlyIfIsSet);

        _inspector.ResetOnlyIfCommand.Execute(null);
        Assert.Null(Step("bt").Only);
        Assert.Equal(1, _inspector.OnlyIfIndex);

        SetDefault("bluetooth.connect", null);
        _canvas.Select("bt");
        Assert.Equal("Always (default)", _inspector.OnlyIfOptions[0]);
        _inspector.OnlyIfIndex = 0;
        Assert.Null(Step("bt").Only); // no default to override: no key
    }
}
