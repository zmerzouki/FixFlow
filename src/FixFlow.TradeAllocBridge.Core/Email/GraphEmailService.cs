using Azure.Identity;
using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Mapping;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System.Text.RegularExpressions;

namespace FixFlow.TradeAllocBridge.Core.Email;

/// <summary>
/// Retrieves allocation emails and Excel attachments from Microsoft 365 using Graph API.
/// Resolves client mapping dynamically by sender’s DNS domain.
/// </summary>
public class GraphEmailService : IAllocationEmailService
{
    private readonly EmailConfig _config;
    private readonly ILogger<GraphEmailService> _logger;
    private readonly IGraphMailboxClient _mailboxClient;
    private readonly FixMappingRepository _mappingRepo;

    public GraphEmailService(
        EmailConfig config,
        ILogger<GraphEmailService> logger,
        FixMappingRepository? mappingRepo = null,
        IGraphMailboxClient? mailboxClient = null)
    {
        _config = config;
        _logger = logger;
        _mailboxClient = mailboxClient ?? new GraphMailboxClient(config);
        _mappingRepo = mappingRepo ?? CreateMappingRepository(logger);
    }

    /// <summary>
    /// Fetches unread emails from the configured mailbox and downloads Excel attachments locally.
    /// Each message is resolved to a FixMapping (ClientID) based on sender DNS.
    /// </summary>
    public async Task<List<AllocationEmail>> FetchNewEmailsAsync()
    {
        var result = new List<AllocationEmail>();

        try
        {
            var inbox = await _mailboxClient.GetUnreadMessagesAsync(_config.MailboxAddress);

            if (inbox == null || inbox.Count == 0)
            {
                _logger.LogInformation("No unread allocation emails found in mailbox {Mailbox}.", _config.MailboxAddress);
                return result;
            }

            Directory.CreateDirectory(_config.DownloadPath ?? "attachments");

            foreach (var msg in inbox)
            {
                try
                {
                    var senderEmail = msg.SenderEmail ?? string.Empty;
                    var senderDomain = ExtractDomain(senderEmail);

                    var mapping = _mappingRepo.GetAll()
                        .FirstOrDefault(m =>
                            !string.IsNullOrWhiteSpace(m.SenderDomain) &&
                            m.SenderDomain.Equals(senderDomain, StringComparison.OrdinalIgnoreCase));

                    if (mapping == null)
                    {
                        _logger.LogWarning("⚠️ No mapping found for sender domain '{Domain}'. Email '{Subject}' will be skipped.",
                            senderDomain, msg.Subject);
                        continue;
                    }

                    _logger.LogInformation("📧 Matched sender domain '{Domain}' to client mapping '{ClientId}'.",
                        senderDomain, mapping.ClientId);

                    var mapFileName = $"{mapping.ClientId}_map.json";
                    var mapPath = Path.Combine(_mappingRepo.BaseDirectory, mapFileName);

                    if (!File.Exists(mapPath))
                    {
                        var fallbackPath = Path.GetFullPath(
                            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "configs", mapFileName));
                        if (File.Exists(fallbackPath))
                        {
                            mapPath = fallbackPath;
                        }
                    }

                    var allocEmail = new AllocationEmail
                    {
                        Subject = msg.Subject ?? "(No Subject)",
                        Attachments = new List<string>(),
                        SenderDomain = senderDomain,
                        SenderEmail = senderEmail,
                        ClientId = mapping.ClientId,
                        Mapping = mapping,
                        MapPath = mapPath
                    };

                    if (msg.HasAttachments)
                    {
                        var attachments = await _mailboxClient.GetAttachmentsAsync(_config.MailboxAddress, msg.Id);
                        foreach (var att in attachments)
                        {
                            if (!string.IsNullOrWhiteSpace(att.Name) &&
                                (att.Name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                                 att.Name.EndsWith(".xls", StringComparison.OrdinalIgnoreCase) ||
                                 att.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
                            {
                                var safeFileName = Path.GetFileName(att.Name);
                                var savePath = Path.Combine(_config.DownloadPath ?? "attachments", safeFileName);

                                await File.WriteAllBytesAsync(savePath, att.ContentBytes ?? Array.Empty<byte>());
                                allocEmail.Attachments.Add(savePath);
                            }
                        }
                    }

                    result.Add(allocEmail);
                    _logger.LogInformation("✅ Fetched email '{Subject}' with {Count} attachment(s) for client '{ClientId}'.",
                        allocEmail.Subject, allocEmail.Attachments.Count, mapping.ClientId);

                    await _mailboxClient.MarkAsReadAsync(_config.MailboxAddress, msg.Id);
                }
                catch (Exception exMsg)
                {
                    _logger.LogWarning(exMsg, "Error processing message {MessageId}.", msg.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch emails via Graph API.");
        }

        return result;
    }

    private static string ExtractDomain(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return string.Empty;
        var match = Regex.Match(email, @"@([\w\.-]+)$");
        return match.Success ? match.Groups[1].Value.Trim().ToLowerInvariant() : string.Empty;
    }

    private static FixMappingRepository CreateMappingRepository(ILogger<GraphEmailService> logger)
    {
        var configDir = ResolveConfigDirectory();
        logger.LogInformation("📁 Mapping repository initialized at {Path}", configDir);
        return new FixMappingRepository(configDir);
    }

    private static string ResolveConfigDirectory()
    {
        var probePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "configs"),
            Path.Combine(Directory.GetCurrentDirectory(), "configs"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "configs")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "configs"))
        };

        var configDir = probePaths.FirstOrDefault(Directory.Exists)
                        ?? Path.Combine(AppContext.BaseDirectory, "configs");

        Directory.CreateDirectory(configDir);
        return configDir;
    }
}

internal sealed class GraphMailboxClient : IGraphMailboxClient
{
    private readonly GraphServiceClient _client;

    public GraphMailboxClient(EmailConfig config)
    {
        var credential = new ClientSecretCredential(config.TenantId, config.ClientId, config.ClientSecret);
        _client = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
    }

    public async Task<IReadOnlyList<GraphMailboxMessage>> GetUnreadMessagesAsync(string mailboxAddress, CancellationToken cancellationToken = default)
    {
        var inbox = await _client.Users[mailboxAddress]
            .MailFolders["Inbox"]
            .Messages
            .GetAsync(options =>
            {
                options.QueryParameters.Filter = "isRead eq false";
                options.QueryParameters.Top = 20;
                options.QueryParameters.Select = new[]
                {
                    "id", "subject", "hasAttachments", "receivedDateTime", "from"
                };
            }, cancellationToken);

        return inbox?.Value?.Select(message => new GraphMailboxMessage(
            message.Id ?? string.Empty,
            message.Subject ?? "(No Subject)",
            message.HasAttachments == true,
            message.From?.EmailAddress?.Address ?? string.Empty))
            .ToList()
            ?? new List<GraphMailboxMessage>();
    }

    public async Task<IReadOnlyList<GraphMailboxAttachment>> GetAttachmentsAsync(string mailboxAddress, string messageId, CancellationToken cancellationToken = default)
    {
        var attachments = await _client.Users[mailboxAddress]
            .Messages[messageId]
            .Attachments
            .GetAsync(cancellationToken: cancellationToken);

        return attachments?.Value?
            .OfType<FileAttachment>()
            .Select(att => new GraphMailboxAttachment(att.Name ?? string.Empty, att.ContentBytes ?? Array.Empty<byte>()))
            .ToList()
            ?? new List<GraphMailboxAttachment>();
    }

    public async Task MarkAsReadAsync(string mailboxAddress, string messageId, CancellationToken cancellationToken = default)
    {
        await _client.Users[mailboxAddress]
            .Messages[messageId]
            .PatchAsync(new Message { IsRead = true }, cancellationToken: cancellationToken);
    }
}

/// <summary>
/// Represents an allocation email and associated mapping context.
/// </summary>
public class AllocationEmail
{
    public string Subject { get; set; } = string.Empty;
    public List<string> Attachments { get; set; } = new();
    public string? SenderDomain { get; set; }
    public string? ClientId { get; set; }
    public FixMapping? Mapping { get; set; }
    public string? SenderEmail { get; set; }
    public string? MapPath { get; set; }
}
