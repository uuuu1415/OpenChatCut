using System.Collections.ObjectModel;
using OpenChatCut.Native.Infrastructure;
using OpenChatCut.Native.Models;
using OpenChatCut.Native.Services;

namespace OpenChatCut.Native.ViewModels;

public sealed class EditorViewModel : ObservableObject
{
    private readonly NativeProjectStoreClient _client;
    private readonly List<NativeAgentMessage> _agentHistory = [];
    private ProjectSnapshot _snapshot;
    private ProjectProjection _projection;
    private NativeClipView? _selectedClip;
    private string _status = "工程已加载";
    private string _prompt = "";
    private CancellationTokenSource? _agentCancellation;
    private bool _canUndo;
    private bool _canRedo;

    public EditorViewModel(ProjectSnapshot snapshot, NativeProjectStoreClient client)
    {
        _snapshot = snapshot;
        _client = client;
        _projection = ProjectProjection.From(snapshot.Doc);
        Tracks = [];
        RebuildTracks();
        BackCommand = new RelayCommand(_ => BackRequested?.Invoke());
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        SelectClipCommand = new AsyncRelayCommand<NativeClipView>(SelectClipAsync);
        SplitCommand = new AsyncRelayCommand(SplitSelectedAsync, () => SelectedClip is not null);
        SendCommand = new AsyncRelayCommand(SendAgentPromptAsync, CanSendAgentPrompt);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => CanUndo);
        RedoCommand = new AsyncRelayCommand(RedoAsync, () => CanRedo);
    }

    public string ProjectId => _snapshot.Meta.Id;
    public string ProjectName => _snapshot.Meta.Name;
    public string TimelineName => _projection.TimelineName;
    public string CanvasLabel => $"{_projection.Width} × {_projection.Height} · {_projection.Fps} fps";
    public string DurationLabel => _projection.DurationLabel;
    public ObservableCollection<NativeTrackView> Tracks { get; }
    public ObservableCollection<string> Messages { get; } = ["告诉我你想怎么剪，我会把修改写入真实时间线。"];
    public RelayCommand BackCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand<NativeClipView> SelectClipCommand { get; }
    public AsyncRelayCommand SplitCommand { get; }
    public AsyncRelayCommand SendCommand { get; }
    public AsyncRelayCommand UndoCommand { get; }
    public AsyncRelayCommand RedoCommand { get; }
    public event Action? BackRequested;

    public NativeClipView? SelectedClip
    {
        get => _selectedClip;
        private set
        {
            if (!SetProperty(ref _selectedClip, value)) return;
            SplitCommand.Refresh();
            OnPropertyChanged(nameof(InspectorTitle));
            OnPropertyChanged(nameof(InspectorKind));
            OnPropertyChanged(nameof(InspectorTiming));
        }
    }

    public string InspectorTitle => SelectedClip?.Name ?? "没有选择片段";
    public string InspectorKind => SelectedClip is null ? "在时间线上选择一个片段" : SelectedClip.KindLabel;
    public string InspectorTiming => SelectedClip is null
        ? ""
        : $"起点 {SelectedClip.StartLabel} · 时长 {SelectedClip.DurationLabel}";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool CanUndo
    {
        get => _canUndo;
        private set
        {
            if (!SetProperty(ref _canUndo, value)) return;
            UndoCommand.Refresh();
        }
    }

    public bool CanRedo
    {
        get => _canRedo;
        private set
        {
            if (!SetProperty(ref _canRedo, value)) return;
            RedoCommand.Refresh();
        }
    }

    public string Prompt
    {
        get => _prompt;
        set
        {
            if (!SetProperty(ref _prompt, value)) return;
            SendCommand.Refresh();
        }
    }

    public async Task RefreshAsync()
    {
        try
        {
            _snapshot = await _client.GetProjectAsync(ProjectId);
            _projection = ProjectProjection.From(_snapshot.Doc);
            RebuildTracks();
            await RefreshHistoryAsync();
            OnPropertyChanged(nameof(ProjectName));
            OnPropertyChanged(nameof(TimelineName));
            OnPropertyChanged(nameof(CanvasLabel));
            OnPropertyChanged(nameof(DurationLabel));
            Status = "已同步本地工程";
        }
        catch (Exception error)
        {
            Status = error.Message;
        }
    }

    private async Task SelectClipAsync(NativeClipView clip)
    {
        SelectedClip = clip;
        try
        {
            _snapshot = await _client.ApplyActionAsync(ProjectId, new { type = "select", id = clip.Id });
            await RefreshHistoryAsync();
            Status = "已选择片段";
        }
        catch (Exception error)
        {
            Status = error.Message;
        }
    }

    private async Task SplitSelectedAsync()
    {
        var clip = SelectedClip;
        if (clip is null) return;
        try
        {
            var at = clip.StartFrame + Math.Max(1, clip.DurationInFrames / 2);
            _snapshot = await _client.ApplyActionAsync(ProjectId, new
            {
                type = "split",
                id = clip.Id,
                atFrame = at,
                newId = $"clip_{Guid.NewGuid():N}",
            });
            _projection = ProjectProjection.From(_snapshot.Doc);
            RebuildTracks();
            SelectedClip = null;
            await RefreshHistoryAsync();
            OnPropertyChanged(nameof(DurationLabel));
            Status = "已切分片段";
        }
        catch (Exception error)
        {
            Status = error.Message;
        }
    }

    private bool CanSendAgentPrompt() => !string.IsNullOrWhiteSpace(Prompt);

    private async Task SendAgentPromptAsync()
    {
        var prompt = Prompt.Trim();
        if (prompt.Length == 0) return;
        Prompt = "";
        Messages.Add($"你 · {prompt}");
        Messages.Add("Agent · ");
        var responseIndex = Messages.Count - 1;
        _agentCancellation?.Dispose();
        _agentCancellation = new CancellationTokenSource();
        Status = "Agent 正在处理…";
        try
        {
            var result = await _client.RunAgentAsync(
                ProjectId,
                _agentHistory,
                prompt,
                agentEvent =>
                {
                    if (agentEvent.Type == "tool-request")
                    {
                        Status = "Agent 正在执行编辑工具…";
                        return;
                    }
                    if (agentEvent.Type == "error"
                        && agentEvent.Data.ValueKind == System.Text.Json.JsonValueKind.Object
                        && agentEvent.Data.TryGetProperty("message", out var error)
                        && error.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        Status = error.GetString() ?? "Agent 执行失败";
                    }
                },
                _agentCancellation.Token);
            var answer = string.IsNullOrWhiteSpace(result.Text)
                ? "已完成请求。"
                : result.Text.Trim();
            Messages[responseIndex] = $"Agent · {answer}";
            _agentHistory.Add(new NativeAgentMessage("user", prompt));
            _agentHistory.Add(new NativeAgentMessage("assistant", answer));
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            Messages[responseIndex] = "Agent · 已取消";
            Status = "已取消 Agent 请求";
        }
        catch (Exception error)
        {
            Messages[responseIndex] = $"Agent · {error.Message}";
            Status = error.Message;
        }
        finally
        {
            _agentCancellation?.Dispose();
            _agentCancellation = null;
            SendCommand.Refresh();
        }
    }

    private void RebuildTracks()
    {
        Tracks.Clear();
        foreach (var track in _projection.Tracks) Tracks.Add(track);
    }

    private async Task RefreshHistoryAsync()
    {
        var history = await _client.GetHistoryStateAsync(ProjectId);
        CanUndo = history.CanUndo;
        CanRedo = history.CanRedo;
    }

    private async Task UndoAsync()
    {
        try
        {
            _snapshot = await _client.UndoAsync(ProjectId);
            _projection = ProjectProjection.From(_snapshot.Doc);
            RebuildTracks();
            SelectedClip = null;
            await RefreshHistoryAsync();
            OnPropertyChanged(nameof(TimelineName));
            OnPropertyChanged(nameof(CanvasLabel));
            OnPropertyChanged(nameof(DurationLabel));
            Status = "已撤销上一步";
        }
        catch (Exception error)
        {
            Status = error.Message;
        }
    }

    private async Task RedoAsync()
    {
        try
        {
            _snapshot = await _client.RedoAsync(ProjectId);
            _projection = ProjectProjection.From(_snapshot.Doc);
            RebuildTracks();
            SelectedClip = null;
            await RefreshHistoryAsync();
            OnPropertyChanged(nameof(TimelineName));
            OnPropertyChanged(nameof(CanvasLabel));
            OnPropertyChanged(nameof(DurationLabel));
            Status = "已重做上一步";
        }
        catch (Exception error)
        {
            Status = error.Message;
        }
    }
}
