namespace IronPaw.AgentCore.Persistence;

public sealed record FileMemoryStoreOptions(
    string GlobalMemoryPath,
    string SessionMemoriesDirectory,
    int MaxGlobalMemories = 100,
    int MaxSessionMemories = 50,
    int MaxMemoryContentChars = 500);
