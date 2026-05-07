using QuickFix;

namespace FixFlow.TradeAllocBridge.Core.Fix;

public interface IFixMessageClient
{
    Task<string> SendAsync(Message msg, SessionID? sessionID = null);
    List<string> ValidateAllocation(Message msg);
}
