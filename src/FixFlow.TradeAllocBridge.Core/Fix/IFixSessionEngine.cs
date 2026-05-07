using QuickFix;

namespace FixFlow.TradeAllocBridge.Core.Fix;

public interface IFixSessionEngine
{
    SessionSettings? SessionSettings { get; }
    bool IsStarted { get; }
    void AppendSessionsIfMissing(IEnumerable<(string Sender, string Target, string? SenderSubId, string? TargetSubId)> sessions);
    void ReloadSettings(FixApp app);
    void Start();
    void Stop();
}
