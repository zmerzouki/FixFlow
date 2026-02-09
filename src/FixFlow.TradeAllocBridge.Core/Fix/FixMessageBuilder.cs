using Microsoft.Extensions.Logging;
using QuickFix;
using QuickFix.DataDictionary;
using QuickFix.Fields;
using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Excel;
using FixFlow.TradeAllocBridge.Core.Mapping;
using System.Globalization;
using System.Xml.Linq;

namespace FixFlow.TradeAllocBridge.Core.Fix;

public class FixMessageBuilder
{
    private readonly MappingConfig _mapping;
    private readonly FixConfig _config;
    private readonly ILogger<FixMessageBuilder> _log;
    private int _allocCounter = 10000;
    private readonly string _counterFile = "alloc_counter.chk";
    private int _orderCounter = 5000;
    private readonly string _orderCounterFile = "order_counter.chk";
    private string _currentTradeDate = DateTime.UtcNow.ToString("yyyyMMdd");
    private static readonly Lazy<HashSet<int>> NoAllocsTags = new(LoadNoAllocsTags);

    public FixMessageBuilder(MappingConfig mapping, FixConfig config, ILogger<FixMessageBuilder> logger)
    {
        _mapping = mapping;
        _config = config;
        _log = logger;
        LoadCounter();
        LoadOrderCounter();
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

    private void LoadOrderCounter()
    {
        if (File.Exists(_orderCounterFile))
        {
            try
            {
                var text = File.ReadAllText(_orderCounterFile).Trim();
                _orderCounter = int.Parse(text);
                _log.LogInformation("Loaded order counter: {Counter}", _orderCounter);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to load order counter file; resetting order counter to 5000.");
                _orderCounter = 5000;
            }
        }
    }

    private void SaveOrderCounter() =>
        File.WriteAllText(_orderCounterFile, _orderCounter.ToString());

    private string ResolveTradeDate(IDictionary<string, string> row, MappingConfig? mapping)
    {
        // Prefer mapped column for tag 75, fallback to literal "TRADE DATE"
        string col = "TRADE DATE";
        if (mapping != null)
        {
            var mapped = mapping.TradeAllocations.FirstOrDefault(kvp => kvp.Value == "75").Key;
            if (!string.IsNullOrWhiteSpace(mapped))
                col = mapped;
        }

        if (row.TryGetValue(col, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            // Try parse common date formats, otherwise try to normalize yyyyMMdd
            if (DateTime.TryParse(raw, out var dt))
                return dt.ToString("yyyyMMdd");

            // If already looks like yyyyMMdd
            var s = raw.Trim();
            if (s.Length >= 8 && s.All(char.IsDigit))
                return s.Substring(0, 8);
        }

        return DateTime.UtcNow.ToString("yyyyMMdd");
    }

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
        SetOptionalHeaderFields(msg, _mapping.Predefined);

        // Resolve trade date (tag 75) for this row and use it in AllocID
        var tradeDate = ResolveTradeDate(trade.Fields, _mapping);

        // Auto-generate AllocID
        _allocCounter++;
        // persist the alloc counter with the trade date used
        _currentTradeDate = tradeDate;
        SaveCounter();
        string allocId = $"{_allocCounter}_{tradeDate}";
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
        ApplyCommissionRules(msg, trade.Fields, _mapping);

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
    SetOptionalHeaderFields(msg, mapping.Predefined);

    // 🔹 Lookup tag-to-column mappings (reverse lookup: tag -> column name)
    string? GetColumn(int tag) => mapping.TradeAllocations
        .FirstOrDefault(kvp => kvp.Value == tag.ToString())
        .Key;

    var first = trades.First();
    var row = first.Fields;
    var tradeDate = ResolveTradeDate(row, mapping);

    // Ensure AllocID contains the trade date. If caller passed a numeric-only allocId, append the trade date.
    if (!string.IsNullOrWhiteSpace(allocId))
    {
        var lastDash = allocId.LastIndexOf('-');
        var hasDatePart = false;
        if (lastDash >= 0 && lastDash < allocId.Length - 1)
        {
            var part = allocId.Substring(lastDash + 1);
            if (part.Length == 8 && part.All(char.IsDigit))
                hasDatePart = true;
        }

        if (!hasDatePart)
            allocId = $"{allocId}_{tradeDate}";
    }
    else
    {
        // If no allocId provided, generate a new one
        _allocCounter++;
        SaveCounter();
        allocId = $"{_allocCounter}_{tradeDate}";
    }   
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
    if (!row.TryGetValue(symCol, out var symbolValue) || string.IsNullOrWhiteSpace(symbolValue?.Trim()))
        throw new InvalidOperationException("Missing required value: Symbol (tag 55)");

    msg.SetField(new Symbol(symbolValue.Trim()));

    // 🔹 Trade Date (75)
    string tradeDateCol = GetColumn(75) ?? "TRADE DATE";
    msg.SetField(new TradeDate(DateTime.TryParse(row.GetValueOrDefault(tradeDateCol), out var td)
        ? td.ToString("yyyyMMdd")
        : DateTime.UtcNow.ToString("yyyyMMdd")));

    // 🔹 Aggregate totals
    string qtyCol = GetColumn(53) ?? "QUANTITY";
    string priceCol = GetColumn(6) ?? "PRICE";
    decimal totalQty = trades.Sum(t =>
        decimal.TryParse(t.Fields.GetValueOrDefault(qtyCol), out var q) ? Math.Abs(q) : 0m);
    decimal avgPx = trades.Average(t =>
        decimal.TryParse(t.Fields.GetValueOrDefault(priceCol), out var p) ? Math.Abs(p) : 0m);

    msg.SetField(new Shares(totalQty));
    msg.SetField(new AvgPx(avgPx));

    // ------------------ NoOrders group ------------------
    _orderCounter++;
    SaveOrderCounter();
    var orderClOrd = $"0C{_orderCounter}_{tradeDate}";
    var orderGroup = new Group(73, 11);
    orderGroup.SetField(new StringField(11, orderClOrd));
    msg.SetField(new IntField(73, 1));
    msg.AddGroup(orderGroup);

    // ------------------ NoAllocs group ------------------
    var allocList = trades.ToList();
    msg.SetField(new IntField(78, allocList.Count));

    string allocAcctCol = GetColumn(79) ?? "ALLOC ACCOUNT";
    string allocQtyCol  = GetColumn(80) ?? "QUANTITY";
    string allocPxCol   = GetColumn(153) ?? "PRICE";
    string? commCol = GetColumn(12);
    string? commTypeCol = GetColumn(13);
    string? miscFeeAmtCol = GetColumn(137);
    string? miscFeeCurrCol = GetColumn(138);
    string? miscFeeTypeCol = GetColumn(139);

    if (!string.IsNullOrWhiteSpace(commCol) && string.IsNullOrWhiteSpace(commTypeCol))
    {
        _log.LogWarning("Commission (tag 12) is mapped but CommType (tag 13) is not.");
    }

    foreach (var alloc in allocList)
    {
        var allocGroup = new Group(78, 79);

        string allocAcct = alloc.Fields.GetValueOrDefault(allocAcctCol, string.Empty);
        var normalizedAllocAcct = FixValueNormalizer.Normalize(79, allocAcct, alloc.Fields);
        if (!string.IsNullOrWhiteSpace(normalizedAllocAcct))
            allocGroup.SetField(new StringField(79, normalizedAllocAcct));

        string qtyVal = alloc.Fields.GetValueOrDefault(allocQtyCol, string.Empty);
        if (decimal.TryParse(qtyVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty))
            allocGroup.SetField(new DecimalField(80, Math.Abs(qty)));

        string pxVal = alloc.Fields.GetValueOrDefault(allocPxCol, string.Empty);
        if (decimal.TryParse(pxVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var px))
            allocGroup.SetField(new DecimalField(153, Math.Abs(px)));

        if (!string.IsNullOrWhiteSpace(commCol) && !string.IsNullOrWhiteSpace(commTypeCol))
        {
            var commTypeRaw = alloc.Fields.TryGetValue(commTypeCol, out var commTypeValue)
                ? commTypeValue ?? string.Empty
                : string.Empty;
            var commRaw = alloc.Fields.TryGetValue(commCol, out var commValueRaw)
                ? commValueRaw ?? string.Empty
                : string.Empty;

            if (FixValueNormalizer.TryNormalizeCommission(commTypeRaw, commRaw, out var commType, out var commValue))
            {
                allocGroup.SetField(new StringField(13, commType));
                allocGroup.SetField(new StringField(12, commValue));
            }
        }

        if (!string.IsNullOrWhiteSpace(miscFeeAmtCol) || !string.IsNullOrWhiteSpace(miscFeeTypeCol))
        {
            var miscFeeAmtRaw = !string.IsNullOrWhiteSpace(miscFeeAmtCol) &&
                                alloc.Fields.TryGetValue(miscFeeAmtCol, out var miscFeeAmtValue)
                ? miscFeeAmtValue ?? string.Empty
                : string.Empty;
            var miscFeeTypeRaw = !string.IsNullOrWhiteSpace(miscFeeTypeCol) &&
                                 alloc.Fields.TryGetValue(miscFeeTypeCol, out var miscFeeTypeValue)
                ? miscFeeTypeValue ?? string.Empty
                : string.Empty;
            var miscFeeCurrRaw = !string.IsNullOrWhiteSpace(miscFeeCurrCol) &&
                                 alloc.Fields.TryGetValue(miscFeeCurrCol, out var miscFeeCurrValue)
                ? miscFeeCurrValue ?? string.Empty
                : string.Empty;

            var hasMiscFee = !string.IsNullOrWhiteSpace(miscFeeAmtRaw) ||
                             !string.IsNullOrWhiteSpace(miscFeeTypeRaw) ||
                             !string.IsNullOrWhiteSpace(miscFeeCurrRaw);

            if (hasMiscFee)
            {
                if (string.IsNullOrWhiteSpace(miscFeeAmtRaw))
                {
                    _log.LogWarning("MiscFeeAmt (137) is required for NoMiscFees; skipping group.");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(miscFeeTypeRaw))
                    {
                        miscFeeTypeRaw = "7"; // OTHER
                        _log.LogWarning("MiscFeeType (139) missing; defaulting to 7 (OTHER).");
                    }

                    var miscFeeAmt = FixValueNormalizer.Normalize(137, miscFeeAmtRaw, alloc.Fields);
                    var miscFeeGroup = new Group(136, 137);
                    miscFeeGroup.SetField(new StringField(137, miscFeeAmt));
                    miscFeeGroup.SetField(new StringField(139, miscFeeTypeRaw.Trim()));
                    if (!string.IsNullOrWhiteSpace(miscFeeCurrRaw))
                    {
                        miscFeeGroup.SetField(new StringField(138, miscFeeCurrRaw.Trim()));
                    }

                    allocGroup.SetField(new IntField(136, 1));
                    allocGroup.AddGroup(miscFeeGroup);
                }
            }
        }

        var noAllocsTags = NoAllocsTags.Value;
        if (noAllocsTags.Count > 0)
        {
            foreach (var mappingEntry in mapping.TradeAllocations)
            {
                if (!int.TryParse(mappingEntry.Value, out var tag)) continue;
                if (!noAllocsTags.Contains(tag)) continue;
                if (tag == 12 || tag == 13) continue;
                if (allocGroup.IsSetField(tag)) continue;

                if (!alloc.Fields.TryGetValue(mappingEntry.Key, out var raw) ||
                    string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var normalized = FixValueNormalizer.Normalize(tag, raw, alloc.Fields);
                allocGroup.SetField(new StringField(tag, normalized));
            }
        }

        msg.AddGroup(allocGroup);
    }

    _log.LogInformation("✅ Merged {Count} allocations into AllocID={AllocId}. Generated NoAllocs={AllocCount}.",
        allocList.Count, allocId, allocList.Count);

    _log.LogDebug("FIX RAW => {RawFix}", msg.ToString().Replace('\u0001', '|'));
    return msg;
}

    private static HashSet<int> LoadNoAllocsTags()
    {
        var tags = new HashSet<int>();

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.xml");
            if (!File.Exists(path))
            {
                return new HashSet<int> { 79, 80, 153, 12, 13, 154 };
            }

            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root == null) return tags;

            var fieldMap = root.Element("fields")?
                .Elements("field")
                .Select(f => new
                {
                    Name = (string?)f.Attribute("name"),
                    Number = (string?)f.Attribute("number")
                })
                .Where(f => !string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Number))
                .ToDictionary(f => f.Name!, f => f.Number!, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var allocation = root.Element("messages")?
                .Elements("message")
                .FirstOrDefault(m => string.Equals((string?)m.Attribute("name"), "Allocation", StringComparison.OrdinalIgnoreCase));

            if (allocation == null) return tags;

            var noAllocsGroup = allocation.Elements("group")
                .FirstOrDefault(g => string.Equals((string?)g.Attribute("name"), "NoAllocs", StringComparison.OrdinalIgnoreCase));

            if (noAllocsGroup == null) return tags;

            foreach (var field in noAllocsGroup.Elements("field"))
            {
                var name = (string?)field.Attribute("name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!fieldMap.TryGetValue(name, out var number)) continue;
                if (!int.TryParse(number, out var tag)) continue;
                tags.Add(tag);
            }
        }
        catch
        {
            return new HashSet<int> { 79, 80, 153, 12, 13, 154 };
        }

        return tags;
    }

    private void ApplyCommissionRules(Message msg, IDictionary<string, string> row, MappingConfig mapping)
    {
        var commCol = mapping.TradeAllocations.FirstOrDefault(kvp => kvp.Value == "12").Key;
        if (string.IsNullOrWhiteSpace(commCol))
        {
            return;
        }

        var commTypeCol = mapping.TradeAllocations.FirstOrDefault(kvp => kvp.Value == "13").Key;
        if (string.IsNullOrWhiteSpace(commTypeCol))
        {
            _log.LogWarning("Commission (tag 12) is mapped but CommType (tag 13) is not.");
            return;
        }

        var commTypeRaw = row.TryGetValue(commTypeCol, out var commTypeValue)
            ? commTypeValue ?? string.Empty
            : string.Empty;
        var commRaw = row.TryGetValue(commCol, out var commValueRaw)
            ? commValueRaw ?? string.Empty
            : string.Empty;

        if (!FixValueNormalizer.TryNormalizeCommission(commTypeRaw, commRaw, out var commType, out var commValue))
        {
            if (!string.IsNullOrWhiteSpace(commRaw) && !string.IsNullOrWhiteSpace(commTypeRaw))
            {
                _log.LogWarning("Commission values could not be normalized. CommType='{CommType}' Comm='{Comm}'.", commTypeRaw, commRaw);
            }

            return;
        }

        msg.SetField(new StringField(13, commType));
        msg.SetField(new StringField(12, commValue));
    }

    private static void SetOptionalHeaderFields(Message msg, PredefinedFields? predefined)
    {
        if (predefined == null) return;

        var targetSubId = predefined.TargetSubID;
        if (!string.IsNullOrWhiteSpace(targetSubId))
        {
            msg.Header.SetField(new TargetSubID(targetSubId));
        }

        var onBehalfOfCompId = predefined.OnBehalfOfCompID;
        if (!string.IsNullOrWhiteSpace(onBehalfOfCompId))
        {
            msg.Header.SetField(new OnBehalfOfCompID(onBehalfOfCompId));
        }

        var deliverToCompId = predefined.DeliverToCompID;
        if (!string.IsNullOrWhiteSpace(deliverToCompId))
        {
            msg.Header.SetField(new DeliverToCompID(deliverToCompId));
        }
    }

    public string NextAllocId()
    {
        _allocCounter++;
        SaveCounter();
        return _allocCounter.ToString();
    }
}
