namespace IronPaw.AgentCore.Persistence.CSharpDb;

public sealed record CSharpDbApplicationStoreOptions(
    string DatabasePath,
    string? LegacyJsonStateRoot = null,
    int MaxGlobalMemories = 100,
    int MaxSessionMemories = 50,
    int MaxMemoryContentChars = 500,
    int MessageTtlDays = 30);
