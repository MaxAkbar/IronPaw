using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Persistence.CSharpDb;

public sealed class CSharpDbAgentMessageBus(CSharpDbApplicationStore store, IAgentRegistry agentRegistry) : IAgentMessageBus
{
    public Task<AgentMessage> SendAsync(AgentMessageSendRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sender = RequireAgentId(request.SenderAgentId);
        var recipientId = RequireAgentId(request.RecipientAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Content);
        var recipient = agentRegistry.Get(recipientId) ?? throw new InvalidOperationException($"Recipient agent '{recipientId}' is not registered.");
        if (!recipient.CanReceiveMessagesFrom(sender))
            throw new UnauthorizedAccessException($"Agent '{sender}' is not authorized to message '{recipientId}'.");
        return store.SendMessageAsync(request with { SenderAgentId = sender, RecipientAgentId = recipientId,
            Content = request.Content.Trim(), CorrelationId = Normalize(request.CorrelationId) }, ct);
    }

    public Task<IReadOnlyList<AgentMessage>> ReadAsync(AgentMessageReadRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return store.ReadMessagesAsync(request with { RecipientAgentId = RequireAgentId(request.RecipientAgentId),
            SenderAgentId = Normalize(request.SenderAgentId), CorrelationId = Normalize(request.CorrelationId) }, ct);
    }
    private static string RequireAgentId(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); return value.Trim(); }
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
