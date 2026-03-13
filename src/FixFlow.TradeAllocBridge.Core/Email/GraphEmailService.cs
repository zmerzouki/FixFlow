using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Mapping;
using System.Text.RegularExpressions;

namespace FixFlow.TradeAllocBridge.Core.Email;

/// <summary>
/// Retrieves allocation emails and Excel attachments from Microsoft 365 using Graph API.
/// Resolves client mapping dynamically by sender’s DNS domain.
/// </summary>
public class GraphEmailService
{
    private readonly EmailConfig _config;
    private readonly ILogger<GraphEmailService> _logger;
    private readonly GraphServiceClient _client;
    private readonly FixMappingRepository _mappingRepo;

    public GraphEmailService(EmailConfig config, ILogger<GraphEmailService> logger)
    {
        _config = config;
        _logger = logger;

        var credential = new ClientSecretCredential(_config.TenantId, _config.ClientId, _config.ClientSecret);
        _client = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });

        // ✅ Dynamically resolve configs directory (supports running from CLI, bin/, or root)
        string configDir;

        var probePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "configs"),
            Path.Combine(Directory.GetCurrentDirectory(), "configs"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "configs")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "configs"))
        };

        configDir = probePaths.FirstOrDefault(Directory.Exists)
                    ?? Path.Combine(AppContext.BaseDirectory, "configs");

        Directory.CreateDirectory(configDir);

        _mappingRepo = new FixMappingRepository(configDir);
        _logger.LogInformation("📁 Mapping repository initialized at {Path}", configDir);

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
            var inbox = await _client.Users[_config.MailboxAddress]
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
                });

            if (inbox?.Value == null || inbox.Value.Count == 0)
            {
                _logger.LogInformation("No unread allocation emails found in mailbox {Mailbox}.", _config.MailboxAddress);
                return result;
            }

            Directory.CreateDirectory(_config.DownloadPath ?? "attachments");

            foreach (var msg in inbox.Value)
            {
                try
                {
                    var senderEmail = msg.From?.EmailAddress?.Address ?? string.Empty;
                    var senderDomain = ExtractDomain(senderEmail);

                    // ✅ Resolve FIX mapping dynamically by sender’s domain
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

                    //var allocEmail = new AllocationEmail
                    //{
                    //    Subject = msg.Subject ?? "(No Subject)",
                    //    Attachments = new List<string>(),
                    //    SenderDomain = senderDomain,
                    //    SenderEmail = senderEmail,
                    //    ClientId = mapping.ClientId,
                    //    Mapping = mapping
                    //};
                    var mapFileName = $"{mapping.ClientId}_map.json";
                    var mapPath = Path.Combine(_mappingRepo.BaseDirectory, mapFileName);

                    // fallback if the file is not in the runtime bin folder
                    if (!File.Exists(mapPath))
                    {
                        var fallbackPath = Path.GetFullPath(
                            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "configs", mapFileName));
                        if (File.Exists(fallbackPath))
                            mapPath = fallbackPath;
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

                    if (msg.HasAttachments == true)
                    {
                        var attachments = await _client.Users[_config.MailboxAddress]
                            .Messages[msg.Id]
                            .Attachments
                            .GetAsync();

                        if (attachments?.Value != null)
                        {
                            foreach (var att in attachments.Value)
                            {
                                if (att is FileAttachment fileAttachment &&
                                    fileAttachment.Name != null &&
                                    (fileAttachment.Name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                                     fileAttachment.Name.EndsWith(".xls", StringComparison.OrdinalIgnoreCase) ||
                                     fileAttachment.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
                                {
                                    var safeFileName = Path.GetFileName(fileAttachment.Name);
                                    var savePath = Path.Combine(_config.DownloadPath ?? "attachments", safeFileName);

                                    await File.WriteAllBytesAsync(savePath, fileAttachment.ContentBytes ?? Array.Empty<byte>());
                                    allocEmail.Attachments.Add(savePath);
                                }
                            }
                        }
                    }

                    result.Add(allocEmail);
                    _logger.LogInformation("✅ Fetched email '{Subject}' with {Count} attachment(s) for client '{ClientId}'.",
                        allocEmail.Subject, allocEmail.Attachments.Count, mapping.ClientId);

                    // Mark message as read
                    await _client.Users[_config.MailboxAddress]
                        .Messages[msg.Id]
                        .PatchAsync(new Microsoft.Graph.Models.Message { IsRead = true });
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
