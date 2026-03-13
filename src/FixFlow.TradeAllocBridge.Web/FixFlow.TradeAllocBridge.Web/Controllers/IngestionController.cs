using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Excel;
using FixFlow.TradeAllocBridge.Core.Fix;
using FixFlow.TradeAllocBridge.Core.Mapping;
using FixFlow.TradeAllocBridge.Core.Reporting;
using FixFlow.TradeAllocBridge.Web.Shared;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using QuickFix;

namespace FixFlow.TradeAllocBridge.Web.Controllers;

[ApiController]
[Route("api/ingestion")]
public class IngestionController : ControllerBase
{
    private readonly ExcelParser _excelParser;
    private readonly FixConfig _fixConfig;
    private readonly FixMappingRepository _mappingRepo;
    private readonly ValidationReport _validationReport;
    private readonly FixApp _fixApp;
    private readonly FixEngine _fixEngine;
    private readonly FixClient _fixClient;
    private readonly ILogger<IngestionController> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public IngestionController(
        ExcelParser excelParser,
        FixConfig fixConfig,
        FixMappingRepository mappingRepo,
        ValidationReport validationReport,
        FixApp fixApp,
        FixEngine fixEngine,
        FixClient fixClient,
        ILoggerFactory loggerFactory,
        ILogger<IngestionController> logger)
    {
        _excelParser = excelParser;
        _fixConfig = fixConfig;
        _mappingRepo = mappingRepo;
        _validationReport = validationReport;
        _fixApp = fixApp;
        _fixEngine = fixEngine;
        _fixClient = fixClient;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    [HttpPost("preview")]
    public async Task<ActionResult<IngestionPreviewResponse>> Preview([FromForm] IngestionFormRequest request, CancellationToken cancellationToken)
    {
        var result = await HandleIngestionAsync(request, isProcess: false, cancellationToken);
        return result;
    }

    [HttpPost("process")]
    public async Task<ActionResult<IngestionPreviewResponse>> Process([FromForm] IngestionFormRequest request, CancellationToken cancellationToken)
    {
        var result = await HandleIngestionAsync(request, isProcess: true, cancellationToken);
        return result;
    }

    private async Task<ActionResult<IngestionPreviewResponse>> HandleIngestionAsync(
        IngestionFormRequest request,
        bool isProcess,
        CancellationToken cancellationToken)
    {
        if ((request.File is null || request.File.Length == 0) && string.IsNullOrWhiteSpace(request.SampleName))
        {
            return BadRequest("Allocation file is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ClientId))
        {
            return BadRequest("Client ID is required.");
        }

        if (IsDefaultClient(request.ClientId) && string.IsNullOrWhiteSpace(request.OnBehalfOfCompId))
        {
            return BadRequest("FIX ID (tag 115) is required for DEFAULT client.");
        }

        if (request.File is not null)
        {
            if (!IsSupportedFile(request.File.FileName))
            {
                return BadRequest("Unsupported file type. Please use .xlsx, .xls, or .csv files.");
            }
        }
        else if (!IsSupportedFile(request.SampleName ?? string.Empty))
        {
            return BadRequest("Unsupported sample type. Please use .xlsx, .xls, or .csv files.");
        }

        if (!TryResolveMapping(request.ClientId, out var mapping, out var mappingError))
        {
            return BadRequest(mappingError);
        }

        if (IsDefaultClient(request.ClientId) && !string.IsNullOrWhiteSpace(request.OnBehalfOfCompId))
        {
            mapping.Predefined ??= new PredefinedFields();
            mapping.Predefined.OnBehalfOfCompID = request.OnBehalfOfCompId.Trim();
        }

        string tempPath = string.Empty;
        var shouldCleanup = true;
        try
        {
            if (request.File is not null)
            {
                tempPath = await SaveUploadAsync(request.File, cancellationToken);
            }
            else
            {
                tempPath = ResolveSamplePath(request.SampleName);
                shouldCleanup = false;
            }
            var trades = _excelParser.Parse(tempPath, mapping);

            var dryRun = !isProcess || request.DryRun;
            var response = await BuildIngestionResponseAsync(trades, mapping, dryRun);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process ingestion request.");
            return Problem($"Failed to process ingestion request: {ex.Message}");
        }
        finally
        {
            if (shouldCleanup && !string.IsNullOrWhiteSpace(tempPath) && System.IO.File.Exists(tempPath))
            {
                try
                {
                    System.IO.File.Delete(tempPath);
                }
                catch
                {
                    // Ignore temp cleanup errors.
                }
            }
        }
    }

    private bool TryResolveMapping(string clientId, out MappingConfig mapping, out string error)
    {
        mapping = new MappingConfig();
        error = string.Empty;

        var mapPath = Path.Combine(_mappingRepo.BaseDirectory, $"{clientId}_map.json");
        if (!System.IO.File.Exists(mapPath))
        {
            error = $"Mapping file not found for client '{clientId}'. Expected: {mapPath}";
            return false;
        }

        try
        {
            mapping = MappingConfig.Load(mapPath);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to load mapping: {ex.Message}";
            return false;
        }
    }

    private async Task<string> SaveUploadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.FileName);
        var safeExtension = string.IsNullOrWhiteSpace(extension) ? ".csv" : extension;
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{safeExtension}");

        await using var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await file.CopyToAsync(stream, cancellationToken);
        return tempPath;
    }

    private async Task<IngestionPreviewResponse> BuildIngestionResponseAsync(
        IReadOnlyList<TradeRecord> trades,
        MappingConfig mapping,
        bool dryRun)
    {
        var warnings = new List<string>();
        var messages = new List<FixMessagePreview>();
        var reportEntries = new List<ValidationResult>();

        var allocationsSubmitted = trades.Count;
        var allocationsProcessed = trades.Count;
        var allocationsSent = 0;
        var successfulAllocations = 0;
        var failedAllocations = 0;
        string? resultSuccessMessage = string.Empty;
        string? resultFailureMessage = string.Empty;
        string statusMessage = "Ready";

        if (trades.Count == 0)
        {
            statusMessage = "No trade records found in the uploaded file.";
            warnings.Add(statusMessage);
            return new IngestionPreviewResponse(
                Status: statusMessage,
                TotalTrades: 0,
                ValidTrades: 0,
                Warnings: warnings,
                Messages: messages,
                AllocationsSubmitted: 0,
                AllocationsProcessed: 0,
                AllocationsSent: 0,
                SuccessfulAllocations: 0,
                FailedAllocations: 0,
                ResultSuccessMessage: string.Empty,
                ResultFailureMessage: statusMessage);
        }

        string? GetColumn(int tag) => mapping.TradeAllocations
            .FirstOrDefault(kvp => FixValueNormalizer.TryParseTagNumber(kvp.Value, out var parsed) && parsed == tag)
            .Key;

        string GetFieldValue(TradeRecord trade, string? column)
        {
            if (string.IsNullOrWhiteSpace(column)) return string.Empty;
            return trade.Fields.TryGetValue(column, out var val) ? val.Trim() : string.Empty;
        }

        bool HasValue(TradeRecord trade, string? column) =>
            !string.IsNullOrWhiteSpace(GetFieldValue(trade, column));

        var symbolColumn = GetColumn(55);
        var sideColumn = GetColumn(54);
        var securityIdColumn = GetColumn(48);
        var accountColumn = GetColumn(79);
        var qtyColumn = GetColumn(80);
        var priceColumn = GetColumn(153);
        var tradeDateColumn = GetColumn(75) ?? "TRADEDATE";
        var hasSecurityIdMapping = !string.IsNullOrWhiteSpace(securityIdColumn);
        var requiresSymbol = !hasSecurityIdMapping;

        if (requiresSymbol && string.IsNullOrWhiteSpace(symbolColumn))
        {
            statusMessage = "Missing required mapping for Symbol (tag 55).";
            warnings.Add(statusMessage);
            return new IngestionPreviewResponse(
                Status: statusMessage,
                TotalTrades: allocationsSubmitted,
                ValidTrades: 0,
                Warnings: warnings,
                Messages: messages,
                AllocationsSubmitted: allocationsSubmitted,
                AllocationsProcessed: 0,
                AllocationsSent: 0,
                SuccessfulAllocations: 0,
                FailedAllocations: allocationsSubmitted,
                ResultSuccessMessage: string.Empty,
                ResultFailureMessage: statusMessage);
        }

        int skippedInvalidEquities = 0;
        string? invalidEquityMessage = null;
        if (hasSecurityIdMapping)
        {
            var filteredTrades = trades.Where(t => HasValue(t, securityIdColumn)).ToList();
            skippedInvalidEquities = trades.Count - filteredTrades.Count;
            if (skippedInvalidEquities > 0)
            {
                invalidEquityMessage = $"{skippedInvalidEquities} allocation(s) with invalid equities have been skipped.";
                trades = filteredTrades;
                allocationsProcessed = trades.Count;
            }
        }

        var allocationsEligible = Math.Max(allocationsSubmitted - skippedInvalidEquities, 0);

        string EnsureInvalidEquityNotice(string message)
        {
            if (string.IsNullOrWhiteSpace(invalidEquityMessage))
            {
                return message;
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return invalidEquityMessage;
            }

            return message.Contains(invalidEquityMessage, StringComparison.OrdinalIgnoreCase)
                ? message
                : $"{message} {invalidEquityMessage}";
        }

        if (trades.Count == 0)
        {
            statusMessage = EnsureInvalidEquityNotice("No valid allocations to process.");
            return new IngestionPreviewResponse(
                Status: statusMessage,
                TotalTrades: allocationsSubmitted,
                ValidTrades: 0,
                Warnings: warnings,
                Messages: messages,
                AllocationsSubmitted: allocationsSubmitted,
                AllocationsProcessed: 0,
                AllocationsSent: 0,
                SuccessfulAllocations: 0,
                FailedAllocations: 0,
                ResultSuccessMessage: statusMessage,
                ResultFailureMessage: string.Empty);
        }

        var numericIssues = FixValueNormalizer.FindNumericFieldIssues(trades, mapping);
        if (numericIssues.Count > 0)
        {
            var detail = string.Join(", ",
                numericIssues.Select(issue =>
                    $"{FixValueNormalizer.FormatColumnLabel(issue.ColumnKey)} (tag {issue.Tag}) = '{issue.SampleValue}'"));
            statusMessage = EnsureInvalidEquityNotice($"Unexpected non-numeric value(s) found for numeric FIX fields: {detail}.");
            warnings.Add(statusMessage);
            return new IngestionPreviewResponse(
                Status: statusMessage,
                TotalTrades: allocationsSubmitted,
                ValidTrades: 0,
                Warnings: warnings,
                Messages: messages,
                AllocationsSubmitted: allocationsSubmitted,
                AllocationsProcessed: 0,
                AllocationsSent: 0,
                SuccessfulAllocations: 0,
                FailedAllocations: allocationsEligible,
                ResultSuccessMessage: string.Empty,
                ResultFailureMessage: statusMessage);
        }

        string ResolveSymbolValue(TradeRecord trade)
        {
            var symbolValue = GetFieldValue(trade, symbolColumn);
            if (!string.IsNullOrWhiteSpace(symbolValue))
            {
                return symbolValue;
            }

            return hasSecurityIdMapping ? "NA" : string.Empty;
        }

        string ResolveGroupKey(TradeRecord trade)
        {
            var sideValue = GetFieldValue(trade, sideColumn);
            if (hasSecurityIdMapping)
            {
                var secIdValue = GetFieldValue(trade, securityIdColumn);
                if (!string.IsNullOrWhiteSpace(secIdValue))
                {
                    return $"48:{secIdValue}|54:{sideValue}";
                }
            }

            var symbolValue = ResolveSymbolValue(trade);
            return $"55:{symbolValue}|54:{sideValue}";
        }

        string ResolveGroupDisplay(TradeRecord trade)
        {
            if (hasSecurityIdMapping)
            {
                var secIdValue = GetFieldValue(trade, securityIdColumn);
                if (!string.IsNullOrWhiteSpace(secIdValue))
                {
                    return secIdValue;
                }
            }

            return ResolveSymbolValue(trade);
        }

        bool IsMissing(TradeRecord trade, string? column, bool required = true) =>
            required && !HasValue(trade, column);

        var missingTrades = trades
            .Where(t =>
                IsMissing(t, symbolColumn, requiresSymbol) ||
                IsMissing(t, sideColumn) ||
                IsMissing(t, accountColumn) ||
                IsMissing(t, qtyColumn) ||
                IsMissing(t, priceColumn))
            .ToList();

        var missingTags = new List<string>();
        if (missingTrades.Any(t => IsMissing(t, sideColumn))) missingTags.Add("Side (tag 54)");
        if (requiresSymbol && missingTrades.Any(t => IsMissing(t, symbolColumn))) missingTags.Add("Symbol (tag 55)");
        if (missingTrades.Any(t => IsMissing(t, accountColumn))) missingTags.Add("AllocAccount (tag 79)");
        if (missingTrades.Any(t => IsMissing(t, qtyColumn))) missingTags.Add("AllocShares (tag 80)");
        if (missingTrades.Any(t => IsMissing(t, priceColumn))) missingTags.Add("AllocAvgPx (tag 153)");

        string? missingRequiredMessage = null;
        int missingCount = missingTrades.Count;
        if (missingTrades.Any())
        {
            failedAllocations = missingCount;
            missingRequiredMessage = EnsureInvalidEquityNotice(
                $"{missingCount} out of {allocationsSubmitted} Allocation(s) failed to process. Missing required value(s): {string.Join(", ", missingTags)}.");
            statusMessage = missingRequiredMessage;
            resultFailureMessage = missingRequiredMessage;
            warnings.Add(missingRequiredMessage);
        }

        var validTrades = trades.Where(t => !missingTrades.Contains(t)).ToList();
        allocationsProcessed = validTrades.Count;

        if (validTrades.Count == 0)
        {
            statusMessage = EnsureInvalidEquityNotice(missingRequiredMessage ?? "No valid allocations to process.");
            resultFailureMessage = statusMessage;
            return new IngestionPreviewResponse(
                Status: statusMessage,
                TotalTrades: allocationsSubmitted,
                ValidTrades: 0,
                Warnings: warnings,
                Messages: messages,
                AllocationsSubmitted: allocationsSubmitted,
                AllocationsProcessed: allocationsProcessed,
                AllocationsSent: allocationsSent,
                SuccessfulAllocations: 0,
                FailedAllocations: failedAllocations,
                ResultSuccessMessage: string.Empty,
                ResultFailureMessage: resultFailureMessage);
        }

        // Setup FIX engine
        var senderComp = mapping.Predefined?.SenderCompID ?? mapping.ClientId ?? "TRADEALLOC";
        var targetComp = mapping.Predefined?.TargetCompID ?? "EXECUTOR";
        var senderSubId = mapping.Predefined?.SenderSubID;
        var targetSubId = mapping.Predefined?.TargetSubID;

        _fixEngine.AppendSessionsIfMissing(new[] { (senderComp, targetComp, senderSubId, targetSubId) });
        _fixEngine.ReloadSettings(_fixApp);

        if (!dryRun)
        {
            _fixEngine.Start();
            await Task.Delay(500); // Give engine time to start
        }

        // Group and process trades
        var groupedBySymbolAndSide = validTrades
            .GroupBy(ResolveGroupKey)
            .ToList();

        var preparedMessages = new List<(Message Message, string AllocId, string Symbol, string Side, string GroupKey, int TradeCount, IEnumerable<TradeRecord> Trades)>();
        int failedFixMessages = 0;
        int successfulFixMessages = 0;
        int buildFailures = 0;
        var groupErrors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var allocIdToGroupKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groupTotals = trades
            .GroupBy(ResolveGroupKey)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var groupMeta = trades
            .GroupBy(ResolveGroupKey)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var first = g.First();
                    return new
                    {
                        Side = GetFieldValue(first, sideColumn),
                        Symbol = ResolveSymbolValue(first),
                        Display = ResolveGroupDisplay(first)
                    };
                },
                StringComparer.OrdinalIgnoreCase);
        var missingTagsByGroup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        void AddGroupError(string groupKey, string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return;
            if (!groupErrors.TryGetValue(groupKey, out var errors))
            {
                errors = new List<string>();
                groupErrors[groupKey] = errors;
            }
            errors.Add(error);
        }

        static string NormalizeTradeDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var trimmed = raw.Trim();
            if (trimmed.Length == 8 && trimmed.All(char.IsDigit))
            {
                return trimmed;
            }

            if (DateTime.TryParse(trimmed, out var parsed))
            {
                return parsed.ToString("yyyyMMdd");
            }

            return string.Empty;
        }

        static string ResolveTradeDate(IEnumerable<TradeRecord> groupTrades, string tradeDateColumnName)
        {
            foreach (var trade in groupTrades)
            {
                if (trade.Fields.TryGetValue(tradeDateColumnName, out var raw))
                {
                    var normalized = NormalizeTradeDate(raw);
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        return normalized;
                    }
                }
            }

            return string.Empty;
        }

        static string FormatAllocId(string allocId, string tradeDate)
        {
            return string.IsNullOrWhiteSpace(tradeDate) ? allocId : $"{allocId}_{tradeDate}";
        }

        foreach (var trade in missingTrades)
        {
            var key = ResolveGroupKey(trade);

            if (!missingTagsByGroup.TryGetValue(key, out var tags))
            {
                tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                missingTagsByGroup[key] = tags;
            }

            if (IsMissing(trade, sideColumn)) tags.Add("Side (tag 54)");
            if (requiresSymbol && IsMissing(trade, symbolColumn)) tags.Add("Symbol (tag 55)");
            if (IsMissing(trade, accountColumn)) tags.Add("AllocAccount (tag 79)");
            if (IsMissing(trade, qtyColumn)) tags.Add("AllocShares (tag 80)");
            if (IsMissing(trade, priceColumn)) tags.Add("AllocAvgPx (tag 153)");
        }

        foreach (var kvp in missingTagsByGroup)
        {
            var groupTotal = groupTotals.TryGetValue(kvp.Key, out var count) ? count : 0;
            var meta = groupMeta.TryGetValue(kvp.Key, out var details)
                ? details
                : new { Side = string.Empty, Symbol = string.Empty, Display = string.Empty };
            var message =
                $"{groupTotal} out of {allocationsSubmitted} allocations identified for group trade {meta.Side}/{meta.Display} failed to merge because it is missing required value(s): {string.Join(", ", kvp.Value)}. Allocations processing cancelled.";
            AddGroupError(kvp.Key, message);
        }

        var fixBuilderLogger = _loggerFactory.CreateLogger<FixMessageBuilder>();
        var fixBuilder = new FixMessageBuilder(mapping, _fixConfig, fixBuilderLogger);

        foreach (var symbolGroup in groupedBySymbolAndSide)
        {
            var groupKey = symbolGroup.Key;
            var meta = groupMeta.TryGetValue(groupKey, out var details)
                ? details
                : new { Side = string.Empty, Symbol = string.Empty, Display = string.Empty };
            string symbol = meta.Symbol;
            string side = meta.Side;
            var sideDisplay = FixValueNormalizer.FormatSideDisplay(side);
            var allocId = fixBuilder.NextAllocId();
            var groupTradeDate = ResolveTradeDate(symbolGroup, tradeDateColumn);
            var reportAllocId = FormatAllocId(allocId, groupTradeDate);
            allocIdToGroupKey[allocId] = groupKey;

            Message mergedMsg;
            try
            {
                mergedMsg = fixBuilder.BuildFromAllocGroup(allocId, senderComp, targetComp, symbolGroup, mapping);
            }
            catch (Exception ex)
            {
                failedAllocations += symbolGroup.Count();
                buildFailures++;
                messages.Add(new FixMessagePreview(
                    AllocId: allocId,
                    Symbol: symbol,
                    Side: sideDisplay,
                    TradeCount: symbolGroup.Count(),
                    RawFix: string.Empty,
                    Status: "Failed",
                    ErrorMessage: ex.Message));
                reportEntries.Add(new ValidationResult(
                    DateTime.UtcNow.ToString("o"),
                    mapping.ClientId ?? string.Empty,
                    reportAllocId,
                    side,
                    symbol,
                    "Failed",
                    string.Empty,
                    string.Empty
                ));
                AddGroupError(groupKey, ex.Message);
                _logger.LogWarning(ex, "Failed to build FIX allocation for symbol {Symbol}", symbol);
                continue;
            }

            preparedMessages.Add((mergedMsg, allocId, symbol, side, groupKey, symbolGroup.Count(), symbolGroup));
        }

        bool canSendFixMessages = !dryRun && !missingTrades.Any() && buildFailures == 0;

        foreach (var prepared in preparedMessages)
        {
            var mergedMsg = prepared.Message;
            var allocId = prepared.AllocId;
            var symbol = prepared.Symbol;
            var side = prepared.Side;
            var sideDisplay = FixValueNormalizer.FormatSideDisplay(side);
            var tradeCount = prepared.TradeCount;
            var groupTrades = prepared.Trades;
            var groupTradeDate = ResolveTradeDate(groupTrades, tradeDateColumn);
            var groupKey = prepared.GroupKey;

            // Find session
            SessionID? sessionID = null;
            if ((dryRun || canSendFixMessages) && _fixEngine.SessionSettings != null)
            {
                foreach (var sid in _fixEngine.SessionSettings.GetSessions())
                {
                    if (string.Equals(sid.SenderCompID, senderComp, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(sid.TargetCompID, targetComp, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(senderSubId) &&
                            !string.Equals(sid.SenderSubID, senderSubId, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (!string.IsNullOrWhiteSpace(targetSubId) &&
                            !string.Equals(sid.TargetSubID, targetSubId, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        sessionID = sid;
                        break;
                    }
                }
            }

            if ((dryRun || canSendFixMessages) && sessionID is null)
            {
                failedAllocations += tradeCount;
                messages.Add(new FixMessagePreview(
                    AllocId: allocId,
                    Symbol: symbol,
                    Side: sideDisplay,
                    TradeCount: tradeCount,
                    RawFix: mergedMsg.ToString().Replace('\u0001', '|'),
                    Status: "Failed",
                    ErrorMessage: BuildSessionMissingMessage(senderComp, targetComp, senderSubId, targetSubId)));
                AddGroupError(groupKey, BuildSessionMissingMessage(senderComp, targetComp, senderSubId, targetSubId));
                failedFixMessages++;
                continue;
            }

            // Send message
            string sendResult;
            if (dryRun)
            {
                var validationErrors = _fixClient.ValidateAllocation(mergedMsg);
                sendResult = validationErrors.Count > 0
                    ? string.Join("; ", validationErrors)
                    : "OK";
            }
            else if (!canSendFixMessages)
            {
                sendResult = "Skipped";
                if (!groupErrors.ContainsKey(groupKey))
                {
                    var groupTotal = groupTotals.TryGetValue(groupKey, out var count) ? count : tradeCount;
                    var failedAllocationsCount = allocationsSubmitted - groupTotal;
                    var cancelMessage =
                        $"{groupTotal} out of {allocationsSubmitted} allocations identified for group trade {side}/{symbol} were successfully merged. Fix message cannot be sent because {failedAllocationsCount} trades have error(s). Allocations processing cancelled.";
                    AddGroupError(groupKey, cancelMessage);
                }
            }
            else
            {
                sendResult = await _fixClient.SendAsync(mergedMsg, sessionID);
            }

            if (sendResult != "OK")
            {
                failedFixMessages++;
                if (!string.Equals(sendResult, "Skipped", StringComparison.OrdinalIgnoreCase))
                {
                    AddGroupError(groupKey, sendResult);
                }
            }
            else if (!dryRun)
            {
                successfulFixMessages++;
            }

            if (sendResult == "OK")
            {
                successfulAllocations += tradeCount;
            }
            else
            {
                failedAllocations += tradeCount;
            }

            if (!dryRun && sendResult == "OK")
            {
                allocationsSent += tradeCount;
            }

            reportEntries.Add(new ValidationResult(
                DateTime.UtcNow.ToString("o"),
                mapping.ClientId ?? string.Empty,
                FormatAllocId(allocId,
                    mergedMsg.IsSetField(75)
                        ? NormalizeTradeDate(mergedMsg.GetString(75))
                        : groupTradeDate),
                side,
                symbol,
                sendResult == "OK" ? "Sent" : "Failed",
                string.Empty,
                mergedMsg.ToString().Replace('\u0001', '|')
            ));

            messages.Add(new FixMessagePreview(
                AllocId: allocId,
                Symbol: symbol,
                Side: sideDisplay,
                TradeCount: tradeCount,
                RawFix: mergedMsg.ToString().Replace('\u0001', '|'),
                Status: sendResult == "OK" ? "Sent" : "Failed",
                ErrorMessage: sendResult == "OK" ? string.Empty : sendResult));
        }

        if (!dryRun)
        {
            _fixEngine.Stop();
        }

        successfulAllocations = Math.Max(allocationsEligible - failedAllocations, successfulAllocations);

        resultSuccessMessage = $"{validTrades.Count} out of {allocationsSubmitted} allocations successfully processed and merged across {groupedBySymbolAndSide.Count} allocation group(s).";
        if (!dryRun && successfulFixMessages > 0)
        {
            resultSuccessMessage = $"{resultSuccessMessage} {successfulFixMessages} Fix message(s) successfully sent to {targetComp}.";
        }

        string? fixSendFailureMessage = null;
        if (!dryRun && failedFixMessages > 0)
        {
            fixSendFailureMessage = $"{failedFixMessages} Fix message(s) failed to send.";
        }

        var groupErrorSummary = groupErrors.Count > 0
            ? string.Join(" | ", groupErrors.Values.SelectMany(v => v).Distinct())
            : null;

        var failureMessage = string.Join(" ", new[] { missingRequiredMessage, fixSendFailureMessage, groupErrorSummary }
            .Where(m => !string.IsNullOrWhiteSpace(m)));

        resultFailureMessage = failureMessage;
        if (string.IsNullOrWhiteSpace(resultFailureMessage))
        {
            resultSuccessMessage = EnsureInvalidEquityNotice(resultSuccessMessage ?? string.Empty);
        }
        else
        {
            resultFailureMessage = EnsureInvalidEquityNotice(resultFailureMessage);
        }
        statusMessage = string.IsNullOrWhiteSpace(resultFailureMessage) ? resultSuccessMessage : resultFailureMessage;

        var hasProcessingErrors = missingTrades.Any() || buildFailures > 0 || failedFixMessages > 0 || groupErrors.Count > 0;

        string ResolveErrorDetails(ValidationResult entry)
        {
            if (groupErrors.Count == 0)
            {
                return string.Empty;
            }

            if (!allocIdToGroupKey.TryGetValue(entry.AllocID, out var entryGroupKey))
            {
                return string.Empty;
            }

            if (!groupErrors.TryGetValue(entryGroupKey, out var errors))
            {
                return string.Empty;
            }

            var details = errors
                .Distinct()
                .ToList();
            return details.Count > 0 ? string.Join(" | ", details) : string.Empty;
        }

        if (dryRun)
        {
            var dryRunStatus = hasProcessingErrors ? "Failed" : "Valid. Not Sent";
            for (int i = 0; i < reportEntries.Count; i++)
            {
                var entry = reportEntries[i];
                var errorDetails = ResolveErrorDetails(entry);
                var sideDisplay = FixValueNormalizer.FormatSideDisplay(entry.Side);
                _validationReport.Add(entry with
                {
                    Side = sideDisplay,
                    ProcessingStatus = dryRunStatus,
                    ErrorDetails = errorDetails
                });
            }

            if (messages.Count > 0)
            {
                var updated = messages
                    .Select(m => m with
                    {
                        Status = dryRunStatus,
                        ErrorMessage = hasProcessingErrors ? resultFailureMessage ?? string.Empty : string.Empty
                    })
                    .ToList();
                messages = updated;
            }
        }
        else if (reportEntries.Count > 0)
        {
            foreach (var entry in reportEntries)
            {
                var errorDetails = ResolveErrorDetails(entry);
                var sideDisplay = FixValueNormalizer.FormatSideDisplay(entry.Side);
                _validationReport.Add(entry with { Side = sideDisplay, ErrorDetails = errorDetails });
            }
        }

        _validationReport.Save();

        if (dryRun && !hasProcessingErrors && !string.IsNullOrWhiteSpace(mapping.ClientId))
        {
            TryUpdateMappingValidatedDate(mapping.ClientId);
        }

        return new IngestionPreviewResponse(
            Status: statusMessage,
            TotalTrades: allocationsSubmitted,
            ValidTrades: validTrades.Count,
            Warnings: warnings,
            Messages: messages,
            AllocationsSubmitted: allocationsSubmitted,
            AllocationsProcessed: allocationsProcessed,
            AllocationsSent: allocationsSent,
            SuccessfulAllocations: successfulAllocations,
            FailedAllocations: failedAllocations,
            ResultSuccessMessage: resultSuccessMessage,
            ResultFailureMessage: resultFailureMessage);
    }

    private static string BuildSessionMissingMessage(
        string senderComp,
        string targetComp,
        string? senderSubId,
        string? targetSubId)
    {
        var parts = new List<string> { $"{senderComp} -> {targetComp}" };
        if (!string.IsNullOrWhiteSpace(senderSubId))
        {
            parts.Add($"SenderSubID={senderSubId}");
        }
        if (!string.IsNullOrWhiteSpace(targetSubId))
        {
            parts.Add($"TargetSubID={targetSubId}");
        }

        return "No FIX session found for " + string.Join(" ", parts);
    }

    private static bool IsDefaultClient(string clientId) =>
        string.Equals(clientId, "DEFAULT", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedFile(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension is not null &&
               (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".xls", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveSamplePath(string? sampleName)
    {
        if (string.IsNullOrWhiteSpace(sampleName))
        {
            throw new InvalidOperationException("Sample name is required.");
        }

        var safeName = Path.GetFileName(sampleName.Trim());
        if (string.IsNullOrWhiteSpace(safeName))
        {
            throw new InvalidOperationException("Invalid sample name.");
        }

        var samplesDir = Path.Combine(AppContext.BaseDirectory, "samples");
        var samplePath = Path.Combine(samplesDir, safeName);
        if (!System.IO.File.Exists(samplePath))
        {
            throw new FileNotFoundException($"Sample file not found: {safeName}", samplePath);
        }

        if (!IsSupportedFile(samplePath))
        {
            throw new InvalidOperationException("Unsupported sample type. Please use .xlsx, .xls, or .csv files.");
        }

        return samplePath;
    }

    private void TryUpdateMappingValidatedDate(string clientId)
    {
        try
        {
            var mapPath = Path.Combine(_mappingRepo.BaseDirectory, $"{clientId}_map.json");
            if (!System.IO.File.Exists(mapPath))
            {
                return;
            }

            var previousWriteTime = System.IO.File.GetLastWriteTime(mapPath);
            var mapping = MappingConfig.Load(mapPath);
            mapping.DateValidated = DateTime.Now.ToString("M/d/yyyy h:mm tt");

            var json = JsonSerializer.Serialize(mapping, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            System.IO.File.WriteAllText(mapPath, json);
            System.IO.File.SetLastWriteTime(mapPath, previousWriteTime);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update DateValidated for {ClientId}", clientId);
        }
    }
}

public class IngestionFormRequest
{
    public string ClientId { get; set; } = "DEFAULT";
    public bool DryRun { get; set; } = true;
    public string? OnBehalfOfCompId { get; set; }
    public IFormFile? File { get; set; }
    public string? SampleName { get; set; }
}
