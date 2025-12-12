using QuickFix;

namespace RJF.TradeAllocBridge.Core.Fix
{
    public static class FixExtensions
    {
        /// <summary>
        /// Safely gets a field value by tag, returning an empty string if missing.
        /// </summary>
        public static string GetStringSafe(this Message msg, int tag)
        {
            return msg.IsSetField(tag) ? msg.GetString(tag) : string.Empty;
        }
    }
}
