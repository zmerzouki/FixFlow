namespace FixFlow.TradeAllocBridge.Core.Email;

/// <summary>
/// Represents a simplified email with subject and file attachments
/// fetched by GraphEmailService or used for local test mode.
/// </summary>
public class EmailMessage
{
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// List of fully qualified file paths to Excel attachments.
    /// </summary>
    public List<string> Attachments { get; set; } = new();
}
