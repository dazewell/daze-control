using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Editor.ViewModels.Config;
using CtrlAltDaze.Editor.ViewModels.Engine;
using CtrlAltDaze.Ipc;

namespace CtrlAltDaze.Editor.ViewModels;

/// <summary>Settings page (UX §5.1): start with Windows (the engine's), the pop-up (config.yaml), logs.</summary>
public sealed partial class SettingsPageViewModel : ObservableObject, IDisposable
{
    public const int MinPopupSeconds = 1;
    public const int MaxPopupSeconds = 60; // as ConfigMapper accepts

    private readonly ConfigSession _session;
    private readonly IEngineClient _engine;
    private readonly Action<Action> _post;
    private bool _syncing;

    public SettingsPageViewModel(ConfigSession session, IEngineClient engine, Action<Action> post, string logsFolder)
    {
        _session = session;
        _engine = engine;
        _post = post;
        LogsFolder = logsFolder;
        StepDefaults = new StepDefaultsEditor(session);
        _session.PropertyChanged += OnSessionChanged;
        _engine.StateChanged += OnEngineStateChanged;
        SyncFromConfig();
    }

    public string LogsFolder { get; }

    public StepDefaultsEditor StepDefaults { get; }

    public string ConfigPath => _session.Path;

    /// <summary>Index into <see cref="PopupPosition"/>'s values, in declaration order.</summary>
    [ObservableProperty]
    public partial int PopupPositionIndex { get; set; }

    [ObservableProperty]
    public partial double PopupSeconds { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial bool CanChangeStartWithWindows { get; private set; }

    [ObservableProperty]
    public partial string StartWithWindowsNote { get; private set; } = "";

    public bool CanEdit => _session.CanEdit;

    public async Task RefreshAutostartAsync()
    {
        var answer = await _engine.SendAsync(new PipeRequest { Op = Ops.Autostart }).ConfigureAwait(false);
        _post(() => ShowAutostart(answer));
    }

    partial void OnPopupPositionIndexChanged(int value)
    {
        if (_syncing || value < 0 || !Enum.IsDefined((PopupPosition)value))
            return;
        _session.Update(c => c with { Settings = c.Settings with { Popup = c.Settings.Popup with { Position = (PopupPosition)value } } });
    }

    partial void OnPopupSecondsChanged(double value)
    {
        if (_syncing || double.IsNaN(value))
            return;
        var seconds = (int)Math.Clamp(Math.Round(value), MinPopupSeconds, MaxPopupSeconds);
        _session.Update(c => c with { Settings = c.Settings with { Popup = c.Settings.Popup with { Seconds = seconds } } });
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!_syncing)
            _ = SetAutostartAsync(value);
    }

    private async Task SetAutostartAsync(bool enable)
    {
        var answer = await _engine.SendAsync(new PipeRequest { Op = Ops.Autostart, Enable = enable }).ConfigureAwait(false);
        _post(() => ShowAutostart(answer));
    }

    private void ShowAutostart(PipeMessage? answer)
    {
        _syncing = true;
        try
        {
            var state = answer?.Autostart?.State;
            StartWithWindows = state == "on";
            CanChangeStartWithWindows = answer is { Ok: true } && state != "disabledByWindows";
            StartWithWindowsNote = answer switch
            {
                null => "Start Ctrl Alt Daze to change this.",
                { Ok: false } => answer.Error ?? "Couldn't change it.",
                _ when state == "disabledByWindows" => "Turned off in Windows Settings › Apps › Startup.",
                _ => "Runs in the tray when you sign in.",
            };
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncFromConfig()
    {
        _syncing = true;
        try
        {
            var popup = _session.Current.Settings.Popup;
            PopupPositionIndex = (int)popup.Position;
            PopupSeconds = popup.Seconds;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfigSession.Current))
            SyncFromConfig();
        if (e.PropertyName == nameof(ConfigSession.CanEdit))
            OnPropertyChanged(nameof(CanEdit));
    }

    private void OnEngineStateChanged() => _ = RefreshAutostartAsync();

    public void Dispose()
    {
        _session.PropertyChanged -= OnSessionChanged;
        _engine.StateChanged -= OnEngineStateChanged;
        StepDefaults.Dispose();
    }
}
