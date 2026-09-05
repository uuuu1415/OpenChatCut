using OpenChatCut.Native.Infrastructure;
using OpenChatCut.Native.Services;

namespace OpenChatCut.Native.ViewModels;

public sealed class SettingsViewModel(NativeServiceProcess service) : ObservableObject
{
    private string _status = "本地服务由桌面进程管理";

    public NativeServiceProcess Service { get; } = service;

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }
}
