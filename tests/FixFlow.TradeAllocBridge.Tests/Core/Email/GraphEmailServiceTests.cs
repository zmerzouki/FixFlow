using System.Text.Json;
using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Email;
using FixFlow.TradeAllocBridge.Core.Mapping;
using Microsoft.Extensions.Logging.Abstractions;

namespace FixFlow.TradeAllocBridge.Tests.Core.Email;

public class GraphEmailServiceTests
{
    [Fact]
    public async Task FetchNewEmailsAsync_ReturnsMappedEmail_SavesSupportedAttachments_AndMarksMessageRead()
    {
        var rootDir = Directory.CreateTempSubdirectory();
        var downloadDir = Directory.CreateTempSubdirectory();
        try
        {
            WriteFixMapping(rootDir.FullName, "PEACE", "peace.example");

            var mailboxClient = new FakeGraphMailboxClient
            {
                Messages =
                [
                    new GraphMailboxMessage("msg-1", "Allocations", true, "ops@peace.example")
                ],
                AttachmentsByMessageId =
                {
                    ["msg-1"] =
                    [
                        new GraphMailboxAttachment("allocations.csv", [0x01, 0x02]),
                        new GraphMailboxAttachment("notes.txt", [0x03]),
                        new GraphMailboxAttachment("orders.xlsx", [0x04, 0x05])
                    ]
                }
            };

            var service = new GraphEmailService(
                new EmailConfig
                {
                    MailboxAddress = "allocations@example.com",
                    DownloadPath = downloadDir.FullName
                },
                NullLogger<GraphEmailService>.Instance,
                new FixMappingRepository(rootDir.FullName),
                mailboxClient);

            var result = await service.FetchNewEmailsAsync();

            var email = Assert.Single(result);
            Assert.Equal("PEACE", email.ClientId);
            Assert.Equal("peace.example", email.SenderDomain);
            Assert.Equal("ops@peace.example", email.SenderEmail);
            Assert.Equal("Allocations", email.Subject);
            Assert.Equal(2, email.Attachments.Count);
            Assert.All(email.Attachments, path => Assert.True(File.Exists(path)));
            Assert.Contains(email.Attachments, path => path.EndsWith("allocations.csv", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(email.Attachments, path => path.EndsWith("orders.xlsx", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(email.Attachments, path => path.EndsWith("notes.txt", StringComparison.OrdinalIgnoreCase));
            Assert.Single(mailboxClient.MarkedAsReadIds);
            Assert.Equal("msg-1", mailboxClient.MarkedAsReadIds[0]);
        }
        finally
        {
            rootDir.Delete(recursive: true);
            downloadDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FetchNewEmailsAsync_SkipsMessagesWithoutMatchingSenderDomain()
    {
        var rootDir = Directory.CreateTempSubdirectory();
        var downloadDir = Directory.CreateTempSubdirectory();
        try
        {
            WriteFixMapping(rootDir.FullName, "PEACE", "peace.example");

            var mailboxClient = new FakeGraphMailboxClient
            {
                Messages =
                [
                    new GraphMailboxMessage("msg-1", "Unknown", true, "ops@unknown.example")
                ]
            };

            var service = new GraphEmailService(
                new EmailConfig
                {
                    MailboxAddress = "allocations@example.com",
                    DownloadPath = downloadDir.FullName
                },
                NullLogger<GraphEmailService>.Instance,
                new FixMappingRepository(rootDir.FullName),
                mailboxClient);

            var result = await service.FetchNewEmailsAsync();

            Assert.Empty(result);
            Assert.Empty(mailboxClient.MarkedAsReadIds);
            Assert.Empty(Directory.GetFiles(downloadDir.FullName));
        }
        finally
        {
            rootDir.Delete(recursive: true);
            downloadDir.Delete(recursive: true);
        }
    }

    private static void WriteFixMapping(string directory, string clientId, string senderDomain)
    {
        var payload = JsonSerializer.Serialize(new
        {
            clientId,
            senderDomain,
            fieldMap = new Dictionary<string, string>()
        });

        File.WriteAllText(Path.Combine(directory, $"{clientId}_map.json"), payload);
    }

    private sealed class FakeGraphMailboxClient : IGraphMailboxClient
    {
        public IReadOnlyList<GraphMailboxMessage> Messages { get; init; } = [];
        public Dictionary<string, IReadOnlyList<GraphMailboxAttachment>> AttachmentsByMessageId { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> MarkedAsReadIds { get; } = [];

        public Task<IReadOnlyList<GraphMailboxMessage>> GetUnreadMessagesAsync(string mailboxAddress, CancellationToken cancellationToken = default)
            => Task.FromResult(Messages);

        public Task<IReadOnlyList<GraphMailboxAttachment>> GetAttachmentsAsync(string mailboxAddress, string messageId, CancellationToken cancellationToken = default)
            => Task.FromResult(AttachmentsByMessageId.TryGetValue(messageId, out var attachments)
                ? attachments
                : Array.Empty<GraphMailboxAttachment>());

        public Task MarkAsReadAsync(string mailboxAddress, string messageId, CancellationToken cancellationToken = default)
        {
            MarkedAsReadIds.Add(messageId);
            return Task.CompletedTask;
        }
    }
}
