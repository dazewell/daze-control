using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core.Layout;
using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;

namespace DazeControl.Editor.ViewModels.Inspector;

/// <summary>A step or card that "If it fails, run…" can link to.</summary>
public sealed record FallbackTarget(string Id, string Title);

// "If it fails, run…" and the Connections list (UX §5.4): the user asked how to run a step when another fails and
// couldn't find the edge's context menu, so the inspector offers it on the selected step.
public sealed partial class InspectorViewModel
{
    /// <summary>The edges at the selected step (its card's, for a member), with their conditions.</summary>
    public ObservableCollection<ConnectionViewModel> Connections { get; } = [];

    [ObservableProperty]
    public partial bool HasConnections { get; private set; }

    /// <summary>Steps already in the flow that "If it fails, run…" can pick.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<FallbackTarget> FallbackTargets { get; private set; } = [];

    /// <summary>"If it fails, run…" › a new step: it runs only if the selected one fails, and becomes the selection.</summary>
    public string? AddFallback(string step) =>
        NodeId is { } id && CanEdit ? _canvas.AddFallback(id, step) : null;

    /// <summary>"If it fails, run…" › a step in the flow; a refusal (a loop) changes nothing.</summary>
    public ConnectRefusal LinkFallback(string to) =>
        NodeId is { } id && CanEdit ? _canvas.LinkFallback(id, to) : ConnectRefusal.Unknown;

    private void LoadConnections()
    {
        Connections.Clear();
        FallbackTargets = [];
        if (NodeId is not { } id || Flow is not { } flow || Mode is not (InspectorMode.Step or InspectorMode.Card))
        {
            HasConnections = false;
            return;
        }
        var self = FlowGraph.TopLevel(flow, id);
        string Title(string node) => _canvas.Find(node)?.Title ?? node;
        foreach (var edge in _canvas.Edges.Where(e => e.To == self))
            Connections.Add(new ConnectionViewModel(edge.From, edge.To, $"Runs after {Title(edge.From)}", edge.When, OnConnectionChanged));
        foreach (var edge in _canvas.Edges.Where(e => e.From == self))
            Connections.Add(new ConnectionViewModel(edge.From, edge.To, $"{Title(edge.To)} runs after this", edge.When, OnConnectionChanged));
        HasConnections = Connections.Count > 0;
        FallbackTargets = [.. _canvas.FallbackCandidates(id).Select(n => new FallbackTarget(n.Id, n.Title))];
    }

    private void OnConnectionChanged(ConnectionViewModel connection, EdgeWhen when)
    {
        if (_loading || !CanEdit || _canvas.Edges.FirstOrDefault(e => e.From == connection.From && e.To == connection.To) is not { } edge)
            return;
        _canvas.SetWhen(edge, when);
    }
}
