namespace FixFlow.TradeAllocBridge.Core.Excel;

public interface ITradeFileParser
{
    List<TradeRecord> Parse(string filePath);
    List<TradeRecord> Parse(string filePath, Config.MappingConfig? mapping);
}
