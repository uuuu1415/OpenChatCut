using System.Collections.ObjectModel;
using System.Windows;
using OpenChatCut.Native.Infrastructure;
using OpenChatCut.Native.Models;
using OpenChatCut.Native.Services;

namespace OpenChatCut.Native.ViewModels;

public sealed class DashboardViewModel : ObservableObject
{
    private readonly NativeProjectStoreClient _client;
    private bool _isLoading;
    private string _status = "准备就绪";

    public DashboardViewModel(NativeProjectStoreClient client)
    {
        _client = client;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading);
        CreateProjectCommand = new AsyncRelayCommand(CreateProjectAsync, () => !IsLoading);
        OpenProjectCommand = new AsyncRelayCommand<ProjectSummary>(OpenProjectAsync);
        DuplicateProjectCommand = new AsyncRelayCommand<ProjectSummary>(DuplicateProjectAsync);
        DeleteProjectCommand = new AsyncRelayCommand<ProjectSummary>(DeleteProjectAsync);
    }

    public ObservableCollection<ProjectSummary> Projects { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand CreateProjectCommand { get; }
    public AsyncRelayCommand<ProjectSummary> OpenProjectCommand { get; }
    public AsyncRelayCommand<ProjectSummary> DuplicateProjectCommand { get; }
    public AsyncRelayCommand<ProjectSummary> DeleteProjectCommand { get; }
    public event Func<ProjectSummary, Task>? ProjectOpenRequested;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetProperty(ref _isLoading, value)) return;
            RefreshCommand.Refresh();
            CreateProjectCommand.Refresh();
        }
    }

    public string Status
    {
        get => _status;
        internal set => SetProperty(ref _status, value);
    }

    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var projects = await _client.GetProjectsAsync();
            Projects.Clear();
            foreach (var project in projects.OrderByDescending(item => item.UpdatedAt)) Projects.Add(project);
            Status = Projects.Count == 0 ? "还没有工程 · 创建一个开始剪辑" : $"{Projects.Count} 个本地工程";
        }
        catch (Exception error)
        {
            Status = error.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task CreateProjectAsync()
    {
        try
        {
            var name = $"新工程 {DateTime.Now:MMdd-HHmm}";
            var created = await _client.CreateProjectAsync(name);
            await RefreshAsync();
            await OpenProjectAsync(created.Meta);
        }
        catch (Exception error)
        {
            Status = error.Message;
        }
    }

    private async Task OpenProjectAsync(ProjectSummary project)
    {
        var handler = ProjectOpenRequested;
        if (handler is not null) await handler(project);
    }

    private async Task DuplicateProjectAsync(ProjectSummary project)
    {
        try
        {
            await _client.DuplicateProjectAsync(project.Id);
            await RefreshAsync();
            Status = $"已复制「{project.Name}」";
        }
        catch (Exception error)
        {
            Status = error.Message;
        }
    }

    private async Task DeleteProjectAsync(ProjectSummary project)
    {
        var answer = MessageBox.Show(
            $"确定删除「{project.Name}」吗？\n工程文件和关联的本地素材都会从当前数据目录移除。",
            "删除工程",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await _client.DeleteProjectAsync(project.Id);
            await RefreshAsync();
            Status = $"已删除「{project.Name}」";
        }
        catch (Exception error)
        {
            Status = error.Message;
        }
    }
}
