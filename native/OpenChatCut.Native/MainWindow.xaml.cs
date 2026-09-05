using System.Windows;
using System.Windows.Input;
using OpenChatCut.Native.ViewModels;

namespace OpenChatCut.Native;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    public Task InitializeAsync() => _viewModel.InitializeAsync();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Focus();
    }

    private void OnTitlebarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OnMaximizeClick(sender, e);
            return;
        }
        DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnClosed(object? sender, EventArgs e) => await _viewModel.DisposeAsync();
}
