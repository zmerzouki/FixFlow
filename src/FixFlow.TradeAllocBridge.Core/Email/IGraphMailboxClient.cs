namespace FixFlow.TradeAllocBridge.Core.Email;

public interface IGraphMailboxClient
{
    Task<IReadOnlyList<GraphMailboxMessage>> GetUnreadMessagesAsync(string mailboxAddress, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GraphMailboxAttachment>> GetAttachmentsAsync(string mailboxAddress, string messageId, CancellationToken cancellationToken = default);
    Task MarkAsReadAsync(string mailboxAddress, string messageId, CancellationToken cancellationToken = default);
}

public sealed record GraphMailboxMessage(
    string Id,
    string Subject,
    bool HasAttachments,
    string SenderEmail);

public sealed record GraphMailboxAttachment(
    string Name,
    byte[] ContentBytes);
