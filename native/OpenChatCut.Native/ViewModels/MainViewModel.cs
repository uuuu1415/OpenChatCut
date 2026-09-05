using OpenChatCut.Native.Infrastructure;
using OpenChatCut.Native.Models;
using OpenChatCut.Native.Services;

namespace OpenChatCut.Native.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly NativeServiceProcess _service = new();
    private readonly NativeProjectStoreClient _client = new();
    private object _currentPage;
    private string _connectionStatus = "正在启动本地服务…";
    private EditorViewModel? _editor;

    public MainViewModel()
    {
        Dashboard = new DashboardViewModel(_client);
        Dashboard.ProjectOpenRequested += OpenProjectAsync;
        Settings = new SettingsViewModel(_service);
        _currentPage = Dashboard;
        ShowDashboardCommand = new RelayCommand(_ => ShowDashboard());
        ShowSettingsCommand = new RelayCommand(_ => CurrentPage = Settings);
    }

    public DashboardViewModel Dashboard { get; }
    public SettingsViewModel Settings { get; }
    public object CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public RelayCommand ShowDashboardCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }

    public async Task InitializeAsync()
    {
        try
        {
            var connection = await _service.StartAsync();
            _client.Connect(connection);
            ConnectionStatus = $"本地服务已连接 · {connection.BaseAddress.Host}:{connection.BaseAddress.Port}";
            Settings.Status = ConnectionStatus;
            await Dashboard.RefreshAsync();
        }
        catch (Exception error)
        {
            ConnectionStatus = error.Message;
            Settings.Status = error.Message;
            Dashboard.Status = error.Message;
        }
    }

    private async Task OpenProjectAsync(ProjectSummary project)
    {
        try
        {
            var snapshot = await _client.GetProjectAsync(project.Id);
            _editor = new EditorViewModel(snapshot, _client);
            _editor.BackRequested += ShowDashboard;
            CurrentPage = _editor;
        }
        catch (Exception error)
        {
            Dashboard.Status = error.Message;
        }
    }

    private void ShowDashboard()
    {
        CurrentPage = Dashboard;
        _editor = null;
        _ = Dashboard.RefreshAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _service.DisposeAsync();
    }
}
