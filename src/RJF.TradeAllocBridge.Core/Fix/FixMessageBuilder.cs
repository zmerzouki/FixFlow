using Microsoft.Extensions.Logging;
using QuickFix;
using QuickFix.DataDictionary;
using QuickFix.Fields;
using RJF.TradeAllocBridge.Core.Config;
using RJF.TradeAllocBridge.Core.Excel;
using RJF.TradeAllocBridge.Core.Mapping;
using System.Globalization;

namespace RJF.TradeAllocBridge.Core.Fix;

public class FixMessageBuilder
{
    private readonly MappingConfig _mapping;
    private readonly FixConfig _config;
    private readonly ILogger<FixMessageBuilder> _log;
    private int _allocCounter = 10000;
    private readonly string _counterFile = "alloc_counter.chk";
    private string _currentTradeDate = DateTime.Now.ToString("yyyyMMdd");

    public FixMessageBuilder(MappingConfig mapping, FixConfig config, ILogger<FixMessageBuilder> logger)
    {
        _mapping = mapping;
        _config = config;
        _log = logger;
        LoadCounter();
    }

    private void LoadCounter()
    {
        if (File.Exists(_counterFile))
        {
            try
            {
                var text = File.ReadAllText(_counterFile).Trim();
                var parts = text.Split(':');
                _allocCounter = int.Parse(parts[0]);
                _currentTradeDate = parts.Length > 1 ? parts[1] : _currentTradeDate;
                _log.LogInformation("Loaded allocation counter: {Counter} for trade date {TradeDate}", _allocCounter, _currentTradeDate);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to load counter file; resetting counter to 10000.");
                _allocCounter = 10000;
            }
        }
    }

    private void SaveCounter() =>
        File.WriteAllText(_counterFile, $"{_allocCounter}:{_currentTradeDate}");

    // ---------------------------------------------------------------------
    // Builds a FIX Allocation message for a single TradeRecord (non-merged)
    // ---------------------------------------------------------------------
    public Message Build(TradeRecord trade)
    {
        var msg = new Message();
        msg.Header.SetField(new BeginString("FIX.4.2"));
        msg.Header.SetField(new MsgType("J")); // Allocation

        // ✅ Predefined or default header fields
        string senderComp = _mapping.Predefined?.SenderCompID ?? _mapping.ClientId ?? _config.SenderCompId ?? "TRADEALLOC";
        string targetComp = _mapping.Predefined?.TargetCompID ?? _config.TargetCompId ?? "EXECUTOR";
        msg.Header.SetField(new SenderCompID(senderComp));
        msg.Header.SetField(new TargetCompID(targetComp));

        // Auto-generate AllocID
        _allocCounter++;
        SaveCounter();
        string allocId = $"{_allocCounter}-{_currentTradeDate}";
        msg.SetField(new AllocID(allocId));
        msg.SetField(new AllocTransType(AllocTransType.NEW));

        // 🧩 Map flat fields dynamically
        foreach (var kvp in trade.Fields)
        {
            var header = kvp.Key.Trim();
            var value = kvp.Value?.Trim() ?? string.Empty;

            if (_mapping.TradeAllocations.TryGetValue(header, out var tagStr) &&
                int.TryParse(tagStr, out var tag))
            {
                string normalizedValue = FixValueNormalizer.Normalize(tag, value, trade.Fields);
                msg.SetField(new StringField(tag, normalizedValue));
            }
        }

        // 🧠 Row-aware auto-fill for dependent fields
        if (!msg.IsSetField(Tags.SecurityID) || !msg.IsSetField(Tags.SecurityIDSource))
        {
            var (secId, idSrc) = FixValueNormalizer.GetSecurityIdAndSource(trade.Fields);
            if (!string.IsNullOrEmpty(secId))
                msg.SetField(new SecurityID(secId));
            if (!string.IsNullOrEmpty(idSrc))
                msg.SetField(new SecurityIDSource(idSrc));
        }

        if (!msg.IsSetField(Tags.Side))
        {
            if (trade.Fields.TryGetValue("SIDE", out var rawSide))
                msg.SetField(new Side(FixValueNormalizer.Normalize(54, rawSide, trade.Fields)[0]));
            else
                msg.SetField(new Side(Side.BUY));
        }

        if (!msg.IsSetField(Tags.TradeDate))
            msg.SetField(new TradeDate(DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)));

        _log.LogInformation("✅ Built FIX Allocation (AllocID={AllocId}) for {Client}", allocId, senderComp);
        return msg;
    }

    public Message BuildFromAllocGroup(
    string allocId,
    string senderComp,
    string targetComp,
    IEnumerable<TradeRecord> trades,
    MappingConfig mapping)
{
    if (trades == null || !trades.Any())
        throw new ArgumentException("No trades provided to build allocation group.");

    var msg = new Message();
    msg.Header.SetField(new BeginString("FIX.4.2"));
    msg.Header.SetField(new MsgType("J"));
    msg.Header.SetField(new SenderCompID(senderComp));
    msg.Header.SetField(new TargetCompID(targetComp));

    // 🔹 Lookup tag-to-column mappings (reverse lookup: tag -> column name)
    string? GetColumn(int tag) => mapping.TradeAllocations
        .FirstOrDefault(kvp => kvp.Value == tag.ToString())
        .Key;

    var first = trades.First();
    var row = first.Fields;

    // ------------------ Core tags ------------------
    msg.SetField(new AllocID(allocId));
    msg.SetField(new AllocTransType(AllocTransType.NEW));

    // 🔹 Symbol / security identifiers
    var (securityId, idSource) = FixValueNormalizer.GetSecurityIdAndSource(row);
    if (!string.IsNullOrWhiteSpace(securityId))
        msg.SetField(new SecurityID(securityId));
    if (!string.IsNullOrWhiteSpace(idSource))
        msg.SetField(new SecurityIDSource(idSource));

    // 🔹 Side (54)
    string sideCol = GetColumn(54) ?? "SIDE";
    string sideVal = row.GetValueOrDefault(sideCol, "BUY");
    string normalizedSide = FixValueNormalizer.Normalize(Tags.Side, sideVal, row);
    msg.SetField(new Side(normalizedSide[0]));

    // 🔹 Symbol (55)
    string symCol = GetColumn(55) ?? "SYMBOL";
    msg.SetField(new Symbol(row.GetValueOrDefault(symCol, "UNKNOWN")));

    // 🔹 Trade Date (75)
    string tradeDateCol = GetColumn(75) ?? "TRADE DATE";
    msg.SetField(new TradeDate(DateTime.TryParse(row.GetValueOrDefault(tradeDateCol), out var td)
        ? td.ToString("yyyyMMdd")
        : DateTime.UtcNow.ToString("yyyyMMdd")));

    // 🔹 Aggregate totals
    string qtyCol = GetColumn(53) ?? "QUANTITY";
    string priceCol = GetColumn(6) ?? "PRICE";
    decimal totalQty = trades.Sum(t => decimal.TryParse(t.Fields.GetValueOrDefault(qtyCol), out var q) ? q : 0m);
    decimal avgPx = trades.Average(t => decimal.TryParse(t.Fields.GetValueOrDefault(priceCol), out var p) ? p : 0m);

    msg.SetField(new Shares(totalQty));
    msg.SetField(new AvgPx(avgPx));

    // ------------------ NoOrders group ------------------
    var orderGroup = new Group(73, 11);
    orderGroup.SetField(new StringField(11, $"AUTO-{allocId}"));
    msg.SetField(new IntField(73, 1));
    msg.AddGroup(orderGroup);

    // ------------------ NoAllocs group ------------------
    var allocList = trades.ToList();
    msg.SetField(new IntField(78, allocList.Count));

    string allocAcctCol = GetColumn(79) ?? "ALLOC ACCOUNT";
    string allocQtyCol  = GetColumn(80) ?? "QUANTITY";
    string allocPxCol   = GetColumn(153) ?? "PRICE";

    foreach (var alloc in allocList)
    {
        var allocGroup = new Group(78, 79);

        string allocAcct = alloc.Fields.GetValueOrDefault(allocAcctCol, string.Empty);
        if (!string.IsNullOrWhiteSpace(allocAcct))
            allocGroup.SetField(new StringField(79, allocAcct.Trim()));

        string qtyVal = alloc.Fields.GetValueOrDefault(allocQtyCol, string.Empty);
        if (decimal.TryParse(qtyVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty))
            allocGroup.SetField(new DecimalField(80, qty));

        string pxVal = alloc.Fields.GetValueOrDefault(allocPxCol, string.Empty);
        if (decimal.TryParse(pxVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var px))
            allocGroup.SetField(new DecimalField(153, px));

        msg.AddGroup(allocGroup);
    }

    _log.LogInformation("✅ Merged {Count} allocations into AllocID={AllocId}. Generated NoAllocs={AllocCount}.",
        allocList.Count, allocId, allocList.Count);

    _log.LogDebug("FIX RAW => {RawFix}", msg.ToString().Replace('\u0001', '|'));
    return msg;
}

    public string NextAllocId()
    {
        _allocCounter++;
        SaveCounter();
        return _allocCounter.ToString();
    }
}
