namespace FixFlow.TradeAllocBridge.Core.Email;

public interface IAllocationEmailService
{
    Task<List<AllocationEmail>> FetchNewEmailsAsync();
}
