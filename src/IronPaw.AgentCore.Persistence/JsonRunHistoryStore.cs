using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Persistence;

public sealed class JsonRunHistoryStore : IRunHistoryStore
{
    private readonly string _historyFilePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonRunHistoryStore(string historyFilePath)
    {
        _historyFilePath = Path.GetFullPath(historyFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_historyFilePath)!);
    }

    public async Task RecordAsync(RunSummary summary, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var storeLock = await FileStoreLock.AcquireAsync(_historyFilePath, ct);
            var line = JsonSerializer.Serialize(summary, _serializerOptions);
            await using var stream = new FileStream(_historyFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            var originalLength = stream.Length;
            stream.Position = originalLength;
            try
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(line + Environment.NewLine), ct);
                await stream.FlushAsync(ct);
            }
            catch
            {
                stream.SetLength(originalLength);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<RunSummary>> LoadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_historyFilePath))
        {
            return [];
        }

        await _gate.WaitAsync(ct);
        try
        {
            await using var storeLock = await FileStoreLock.AcquireAsync(_historyFilePath, ct);
            var lines = await File.ReadAllLinesAsync(_historyFilePath, ct);
            return lines
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonSerializer.Deserialize<RunSummary>(l, _serializerOptions)!)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<RunSummary>> LoadBySessionAsync(SessionKey sessionKey, CancellationToken ct)
    {
        var all = await LoadAllAsync(ct);
        return all
            .Where(s => s.SessionId.Equals(sessionKey.Value, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<RunSummary?> GetAsync(string runId, CancellationToken ct)
    {
        var all = await LoadAllAsync(ct);
        return all.FirstOrDefault(s => s.RunId.Equals(runId, StringComparison.OrdinalIgnoreCase));
    }
}
