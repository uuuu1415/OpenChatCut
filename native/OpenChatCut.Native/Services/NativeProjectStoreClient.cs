using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenChatCut.Native.Models;

namespace OpenChatCut.Native.Services;

public sealed class NativeProjectStoreClient : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public NativeProjectStoreClient()
    {
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public void Connect(NativeServiceConnection connection)
    {
        _http.BaseAddress = connection.BaseAddress;
        _http.DefaultRequestHeaders.Remove("x-openchatcut-native-token");
        _http.DefaultRequestHeaders.Add("x-openchatcut-native-token", connection.Token);
    }

    public async Task<IReadOnlyList<ProjectSummary>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetAsync<ProjectListResponse>("api/native/projects", cancellationToken);
        return response.Projects;
    }

    public Task<ProjectSnapshot> GetProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        return GetAsync<ProjectSnapshot>($"api/native/projects/{Uri.EscapeDataString(projectId)}", cancellationToken);
    }

    public Task<ProjectSnapshot> CreateProjectAsync(string name, CancellationToken cancellationToken = default)
    {
        return SendAsync<ProjectSnapshot>(
            HttpMethod.Post,
            "api/native/projects",
            new { name },
            cancellationToken);
    }

    public Task<ProjectSnapshot> RenameProjectAsync(string projectId, string name, CancellationToken cancellationToken = default)
    {
        return SendAsync<ProjectSnapshot>(
            HttpMethod.Patch,
            $"api/native/projects/{Uri.EscapeDataString(projectId)}",
            new { name },
            cancellationToken);
    }

    public Task<ProjectSnapshot> DuplicateProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        return SendAsync<ProjectSnapshot>(
            HttpMethod.Post,
            $"api/native/projects/{Uri.EscapeDataString(projectId)}/duplicate",
            null,
            cancellationToken);
    }

    public Task DeleteProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        return SendAsync<object>(
            HttpMethod.Delete,
            $"api/native/projects/{Uri.EscapeDataString(projectId)}",
            null,
            cancellationToken);
    }

    public Task<ProjectSnapshot> ApplyActionAsync(
        string projectId,
        object action,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<ProjectSnapshot>(
            HttpMethod.Post,
            $"api/native/projects/{Uri.EscapeDataString(projectId)}/actions",
            new { action },
            cancellationToken);
    }

    public Task<ProjectHistoryState> GetHistoryStateAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        return GetAsync<ProjectHistoryState>(
            $"api/native/projects/{Uri.EscapeDataString(projectId)}/history",
            cancellationToken);
    }

    public Task<ProjectSnapshot> UndoAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<ProjectSnapshot>(
            HttpMethod.Post,
            $"api/native/projects/{Uri.EscapeDataString(projectId)}/undo",
            null,
            cancellationToken);
    }

    public Task<ProjectSnapshot> RedoAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<ProjectSnapshot>(
            HttpMethod.Post,
            $"api/native/projects/{Uri.EscapeDataString(projectId)}/redo",
            null,
            cancellationToken);
    }

    public async Task<IReadOnlyList<JsonElement>> GetAgentToolsAsync(
        bool askOnly = false,
        CancellationToken cancellationToken = default)
    {
        var catalog = await GetAsync<NativeAgentToolCatalog>(
            $"api/native/agent/tools?askOnly={(askOnly ? 1 : 0)}",
            cancellationToken);
        return catalog.Tools;
    }

    public async Task<NativeAgentRunResult> RunAgentAsync(
        string projectId,
        IReadOnlyList<NativeAgentMessage> history,
        string prompt,
        Action<NativeAgentEvent>? onEvent = null,
        CancellationToken cancellationToken = default)
    {
        var tools = await GetAgentToolsAsync(cancellationToken: cancellationToken);
        var messages = history
            .Concat([new NativeAgentMessage("user", prompt)])
            .TakeLast(63)
            .ToArray();
        var run = await CreateAgentRunAsync(projectId, messages, tools, cancellationToken);
        await StartAgentRunAsync(projectId, run, cancellationToken);
        return await StreamAgentRunAsync(projectId, run, onEvent, cancellationToken);
    }

    public void Dispose() => _http.Dispose();

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken);
        return await ReadResponseAsync<T>(response, cancellationToken);
    }

    private Task<NativeAgentCreatedRun> CreateAgentRunAsync(
        string projectId,
        IReadOnlyList<NativeAgentMessage> messages,
        IReadOnlyList<JsonElement> tools,
        CancellationToken cancellationToken)
    {
        return SendAsync<NativeAgentCreatedRun>(
            HttpMethod.Post,
            "api/agent-runs/",
            new
            {
                projectId,
                runId = Guid.NewGuid().ToString(),
                capability = CreateRunCapability(),
                messages,
                tools,
                askOnly = false,
                references = Array.Empty<object>(),
                cacheMode = "short",
                maxOutputTokens = 8192,
                autonomousAcceptance = false,
                maxAcceptanceIterations = 3,
                backend = "api",
            },
            cancellationToken);
    }

    private async Task StartAgentRunAsync(
        string projectId,
        NativeAgentCreatedRun run,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/agent-runs/{Uri.EscapeDataString(run.Id)}/start");
        request.Headers.Add("x-openchatcut-run-capability", run.Capability);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { projectId }, _json),
            Encoding.UTF8,
            "application/json");
        using var response = await _http.SendAsync(request, cancellationToken);
        _ = await ReadResponseAsync<object>(response, cancellationToken);
    }

    private async Task<NativeAgentRunResult> StreamAgentRunAsync(
        string projectId,
        NativeAgentCreatedRun run,
        Action<NativeAgentEvent>? onEvent,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/agent-runs/{Uri.EscapeDataString(run.Id)}/events"
                + $"?projectId={Uri.EscapeDataString(projectId)}&after=0");
        request.Headers.Add("x-openchatcut-run-capability", run.Capability);
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Native Agent stream returned {(int)response.StatusCode}: {payload}",
                null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var text = new StringBuilder();
        var eventName = "";
        var data = new StringBuilder();
        var status = "completed";

        void DispatchEvent()
        {
            if (string.IsNullOrEmpty(eventName))
            {
                data.Clear();
                return;
            }
            var raw = data.ToString();
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            var payload = document.RootElement.Clone();
            if (eventName == "text-delta"
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("text", out var delta)
                && delta.ValueKind == JsonValueKind.String)
            {
                text.Append(delta.GetString());
            }
            if (eventName == "done"
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("status", out var doneStatus)
                && doneStatus.ValueKind == JsonValueKind.String)
            {
                status = doneStatus.GetString() ?? status;
            }
            onEvent?.Invoke(new NativeAgentEvent(eventName, payload));
            eventName = "";
            data.Clear();
        }

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                DispatchEvent();
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
        }
        DispatchEvent();
        return new NativeAgentRunResult(status, text.ToString());
    }

    private static string CreateRunCapability()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, _json),
                Encoding.UTF8,
                "application/json");
        }
        using var response = await _http.SendAsync(request, cancellationToken);
        return await ReadResponseAsync<T>(response, cancellationToken);
    }

    private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Native service returned {(int)response.StatusCode}: {payload}",
                null,
                response.StatusCode);
        }
        if (typeof(T) == typeof(object)) return default!;
        return JsonSerializer.Deserialize<T>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Native service returned an empty response.");
    }
}
