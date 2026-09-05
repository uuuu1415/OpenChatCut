using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OpenChatCut.Native.Services;

public sealed record NativeServiceConnection(Uri BaseAddress, string Token);

public sealed class NativeServiceProcess : IAsyncDisposable
{
    private Process? _process;
    private string? _portFile;
    private string? _token;

    public NativeServiceConnection? Connection { get; private set; }

    public async Task<NativeServiceConnection> StartAsync(CancellationToken cancellationToken = default)
    {
        if (Connection is not null && _process is { HasExited: false }) return Connection;

        var root = ResolveRoot();
        var servicePath = Environment.GetEnvironmentVariable("OPENCHATCUT_NATIVE_SERVICE")
            ?? ResolveServicePath(root);
        if (!File.Exists(servicePath))
        {
            throw new FileNotFoundException(
                "找不到本地服务。请先运行 npm run native:build:service，或使用包含 runtime/service.mjs 的安装包。",
                servicePath);
        }

        var nodePath = Environment.GetEnvironmentVariable("OPENCHATCUT_NATIVE_NODE")
            ?? Path.Combine(Path.GetDirectoryName(servicePath) ?? root, "node.exe");
        if (!File.Exists(nodePath)) nodePath = "node";

        _token = CreateToken();
        _portFile = Path.Combine(Path.GetTempPath(), $"openchatcut-native-{Environment.ProcessId}.port");
        TryDeletePortFile();

        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(servicePath);
        startInfo.Environment["OPENCHATCUT_NATIVE_TOKEN"] = _token;
        startInfo.Environment["OPENCHATCUT_NATIVE_PORT_FILE"] = _portFile;

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, args) => Debug.WriteLine($"[native-service] {args.Data}");
        _process.ErrorDataReceived += (_, args) => Debug.WriteLine($"[native-service:error] {args.Data}");
        if (!_process.Start()) throw new InvalidOperationException("本地服务启动失败。");
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        try
        {
            var port = await WaitForPortAsync(cancellationToken);
            Connection = new NativeServiceConnection(new Uri($"http://127.0.0.1:{port}/"), _token);
            return Connection;
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        TryDeletePortFile();
        var process = _process;
        _process = null;
        Connection = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // The process is a private child. If Windows has already reaped it,
            // shutdown is complete; there is no user data in this process.
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task<int> WaitForPortAsync(CancellationToken cancellationToken)
    {
        if (_portFile is null) throw new InvalidOperationException("端口文件未初始化。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            if (_process?.HasExited == true)
            {
                throw new InvalidOperationException("本地服务提前退出，请查看日志后重试。");
            }
            try
            {
                var value = (await File.ReadAllTextAsync(_portFile, timeout.Token)).Trim();
                if (int.TryParse(value, out var port) && port is > 0 and <= 65535) return port;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            await Task.Delay(100, timeout.Token);
        }
        throw new TimeoutException("本地服务启动超时。");
    }

    private static string ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable("OPENCHATCUT_NATIVE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "package.json"))) return current.FullName;
            current = current.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static string ResolveServicePath(string root)
    {
        var packaged = Path.Combine(root, "runtime", "service.mjs");
        if (File.Exists(packaged)) return packaged;
        return Path.Combine(root, "native", "runtime", "service.mjs");
    }

    private static string CreateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private void TryDeletePortFile()
    {
        if (_portFile is null) return;
        try { File.Delete(_portFile); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
