namespace IronPaw.AgentCore.Persistence;

public sealed record FileSessionStoreOptions(
    string StoreFilePath,
    TimeSpan CacheTtl,
    int MaxReadRetries = 5,
    int RetryDelayMilliseconds = 40);
