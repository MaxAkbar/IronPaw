namespace IronPaw.AgentCore.Persistence.Sqlite;

public sealed record SqliteStoreOptions(
    string DatabasePath,
    int MaxGlobalMemories = 100,
    int MaxSessionMemories = 50,
    int MaxMemoryContentChars = 500);
