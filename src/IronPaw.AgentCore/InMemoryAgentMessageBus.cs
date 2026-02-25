using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore;

public sealed class InMemoryAgentMessageBus : IAgentMessageBus
{
    private const int MaxReadLimit = 100;
    private readonly object _lock = new();
    private readonly IAgentRegistry _agentRegistry;
    private readonly Dictionary<MailboxKey, Queue<AgentMessage>> _mailboxes = new();

    public InMemoryAgentMessageBus(IAgentRegistry agentRegistry)
    {
        _agentRegistry = agentRegistry;
    }

    public Task<AgentMessage> SendAsync(AgentMessageSendRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var senderAgentId = RequireAgentId(request.SenderAgentId, nameof(request.SenderAgentId));
        var recipientAgentId = RequireAgentId(request.RecipientAgentId, nameof(request.RecipientAgentId));
        var content = request.Content?.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Message content cannot be empty.", nameof(request.Content));
        }

        var recipient = _agentRegistry.Get(recipientAgentId);
        if (recipient is null)
        {
            throw new InvalidOperationException($"Recipient agent '{recipientAgentId}' is not registered.");
        }

        if (!recipient.CanReceiveMessagesFrom(senderAgentId))
        {
            throw new UnauthorizedAccessException(
                $"Agent '{senderAgentId}' is not authorized to message '{recipientAgentId}'.");
        }

        var message = new AgentMessage(
            MessageId: Guid.NewGuid().ToString("n"),
            SessionKey: request.SessionKey,
            ScopeRunId: request.ScopeRunId,
            SenderAgentId: senderAgentId,
            RecipientAgentId: recipientAgentId,
            Content: content,
            SentAt: DateTimeOffset.UtcNow,
            CorrelationId: NormalizeNullable(request.CorrelationId));

        lock (_lock)
        {
            var key = CreateKey(request.SessionKey, request.ScopeRunId, recipientAgentId);
            if (!_mailboxes.TryGetValue(key, out var queue))
            {
                queue = new Queue<AgentMessage>();
                _mailboxes[key] = queue;
            }

            queue.Enqueue(message);
        }

        return Task.FromResult(message);
    }

    public Task<IReadOnlyList<AgentMessage>> ReadAsync(AgentMessageReadRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var recipientAgentId = RequireAgentId(request.RecipientAgentId, nameof(request.RecipientAgentId));
        var senderFilter = NormalizeNullable(request.SenderAgentId);
        var correlationFilter = NormalizeNullable(request.CorrelationId);
        var max = Math.Clamp(request.MaxMessages, 1, MaxReadLimit);
        var key = CreateKey(request.SessionKey, request.ScopeRunId, recipientAgentId);

        lock (_lock)
        {
            if (!_mailboxes.TryGetValue(key, out var queue) || queue.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<AgentMessage>>(Array.Empty<AgentMessage>());
            }

            if (!request.MarkAsRead)
            {
                var peeked = queue
                    .Where(message => MatchesFilter(message, senderFilter, correlationFilter))
                    .Take(max)
                    .ToArray();
                return Task.FromResult<IReadOnlyList<AgentMessage>>(peeked);
            }

            var messages = new List<AgentMessage>(Math.Min(max, queue.Count));
            var remaining = new Queue<AgentMessage>(queue.Count);
            while (queue.Count > 0)
            {
                var message = queue.Dequeue();
                if (messages.Count < max && MatchesFilter(message, senderFilter, correlationFilter))
                {
                    messages.Add(message);
                    continue;
                }

                remaining.Enqueue(message);
            }

            if (remaining.Count == 0)
            {
                _mailboxes.Remove(key);
            }
            else
            {
                _mailboxes[key] = remaining;
            }

            return Task.FromResult<IReadOnlyList<AgentMessage>>(messages);
        }
    }

    private static MailboxKey CreateKey(SessionKey sessionKey, RunId runId, string recipientAgentId)
    {
        return new MailboxKey(
            sessionKey.Value,
            runId.Value,
            recipientAgentId.Trim().ToUpperInvariant());
    }

    private static string RequireAgentId(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Agent id cannot be empty.", paramName);
        }

        return value.Trim();
    }

    private static string? NormalizeNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool MatchesFilter(AgentMessage message, string? senderFilter, string? correlationFilter)
    {
        if (senderFilter is not null &&
            !string.Equals(message.SenderAgentId, senderFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (correlationFilter is not null &&
            !string.Equals(message.CorrelationId, correlationFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private readonly record struct MailboxKey(string SessionId, string RunId, string RecipientAgentId);
}
