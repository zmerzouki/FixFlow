using System.Globalization;
using System.Text;
using FixFlow.TradeAllocBridge.Core.Reporting;
using FixFlow.TradeAllocBridge.Web.Shared;
using Microsoft.AspNetCore.Mvc;

namespace FixFlow.TradeAllocBridge.Web.Controllers;

[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private const string DirectKey = "Direct";
    private const string EmailKey = "Email";
    private const string DirectName = "Direct Ingestion";
    private const string EmailName = "Email Automation";
    private const string PassedKey = "Passed";
    private const string FailedKey = "Failed";
    private const string UnknownSide = "UNKNOWN";

    [HttpGet("allocations")]
    public ActionResult<IReadOnlyList<DashboardAllocationRecord>> GetAllocations(
        [FromQuery] DateTimeOffset? fromUtc = null,
        [FromQuery] DateTimeOffset? toUtc = null)
    {
        var records = new List<DashboardAllocationRecord>();
        records.AddRange(LoadEntries(DirectKey, fromUtc, toUtc));
        records.AddRange(LoadEntries(EmailKey, fromUtc, toUtc));

        return Ok(records
            .OrderByDescending(r => r.TimestampUtc)
            .ToList());
    }

    [HttpGet("summary")]
    public ActionResult<DashboardSummaryResponse> GetSummary(
        [FromQuery] DateTimeOffset? fromUtc = null,
        [FromQuery] DateTimeOffset? toUtc = null,
        [FromQuery] int localOffsetMinutes = 0,
        [FromQuery] string? rangeMode = null)
    {
        var records = new List<DashboardAllocationRecord>();
        records.AddRange(LoadEntries(DirectKey, fromUtc, toUtc));
        records.AddRange(LoadEntries(EmailKey, fromUtc, toUtc));

        var localOffset = TimeSpan.FromMinutes(localOffsetMinutes);
        var localRangeStartDate = fromUtc?.ToOffset(localOffset).Date;

        var sourceTotals = records
            .Where(record => !record.IsDryRun)
            .GroupBy(record => new { record.SourceKey, record.Source })
            .Select(group => new DashboardSourceTotal(
                group.Key.SourceKey,
                group.Key.Source,
                group.Sum(record => record.TradeCount)))
            .OrderBy(item => item.SourceKey)
            .ToList();

        var outcomeTotals = records
            .Where(record => !record.IsDryRun)
            .GroupBy(record => new { record.SourceKey, OutcomeKey = GetOutcomeKey(record) })
            .Select(group => new DashboardOutcomeTotal(
                group.Key.SourceKey,
                group.Key.OutcomeKey,
                group.Key.OutcomeKey,
                group.Sum(record => record.TradeCount)))
            .OrderBy(item => item.SourceKey)
            .ThenBy(item => item.OutcomeKey)
            .ToList();

        var sideAmountTotals = records
            .GroupBy(record => new
            {
                record.SourceKey,
                OutcomeKey = GetAggregateOutcomeKey(record),
                SideKey = string.IsNullOrWhiteSpace(record.SideLabel) ? UnknownSide : record.SideLabel
            })
            .Select(group => new DashboardSideAmountTotal(
                group.Key.SourceKey,
                group.Key.OutcomeKey,
                group.Key.SideKey,
                group.Key.SideKey,
                group.Sum(record => record.GrossAmount)))
            .ToList();

        var clientCountTotals = records
            .GroupBy(record => new
            {
                record.SourceKey,
                OutcomeKey = GetAggregateOutcomeKey(record),
                ClientId = string.IsNullOrWhiteSpace(record.ClientId) ? "(Unknown)" : record.ClientId
            })
            .Select(group => new DashboardClientCountTotal(
                group.Key.SourceKey,
                group.Key.OutcomeKey,
                group.Key.ClientId,
                group.Sum(record => record.TradeCount)))
            .ToList();

        var clientAmountTotals = records
            .GroupBy(record => new
            {
                record.SourceKey,
                OutcomeKey = GetAggregateOutcomeKey(record),
                ClientId = string.IsNullOrWhiteSpace(record.ClientId) ? "(Unknown)" : record.ClientId
            })
            .Select(group => new DashboardClientAmountTotal(
                group.Key.SourceKey,
                group.Key.OutcomeKey,
                group.Key.ClientId,
                group.Sum(record => record.GrossAmount)))
            .ToList();

        var bucketMode = ResolveBucketMode(rangeMode, fromUtc, toUtc);
        var timeSeriesTotals = records
            .GroupBy(record =>
            {
                var localTimestamp = record.TimestampUtc.ToOffset(localOffset);
                return new
                {
                    record.SourceKey,
                    OutcomeKey = GetAggregateOutcomeKey(record),
                    Bucket = ResolveBucket(localTimestamp, bucketMode, localRangeStartDate)
                };
            })
            .Select(group => new DashboardTimeSeriesTotal(
                group.Key.SourceKey,
                group.Key.OutcomeKey,
                group.Key.Bucket.Key,
                group.Key.Bucket.Label,
                group.Key.Bucket.Position,
                group.Sum(record => record.TradeCount)))
            .ToList();

        return Ok(new DashboardSummaryResponse(
            sourceTotals,
            outcomeTotals,
            sideAmountTotals,
            clientCountTotals,
            clientAmountTotals,
            timeSeriesTotals));
    }

    private static List<DashboardAllocationRecord> LoadEntries(
        string sourceKey,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc)
    {
        var reportsDir = ResolveReportsDir(sourceKey);
        if (string.IsNullOrWhiteSpace(reportsDir) || !Directory.Exists(reportsDir))
        {
            return new List<DashboardAllocationRecord>();
        }

        var files = GetCandidateFiles(reportsDir, fromUtc, toUtc)
            .OrderBy(f => f)
            .ToList();
        if (files.Count == 0)
        {
            return new List<DashboardAllocationRecord>();
        }

        var entries = new List<DashboardAllocationRecord>();
        foreach (var file in files)
        {
            entries.AddRange(ParseReport(file, sourceKey, GetSourceName(sourceKey), fromUtc, toUtc));
        }

        return entries;
    }

    private static IEnumerable<string> GetCandidateFiles(
        string reportsDir,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc)
    {
        var from = fromUtc?.ToUniversalTime();
        var to = toUtc?.ToUniversalTime();

        foreach (var path in Directory.EnumerateFiles(reportsDir, "fix_validation_*.csv"))
        {
            var fileDateUtc = TryParseReportFileDateUtc(path);
            if (fileDateUtc is null)
            {
                yield return path;
                continue;
            }

            var fileStart = fileDateUtc.Value;
            var fileEnd = fileStart.AddDays(1);

            if (from.HasValue && fileEnd <= from.Value)
            {
                continue;
            }

            if (to.HasValue && fileStart >= to.Value)
            {
                continue;
            }

            yield return path;
        }
    }

    private static IEnumerable<DashboardAllocationRecord> ParseReport(
        string path,
        string fallbackSourceKey,
        string fallbackSourceName,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc)
    {
        IEnumerable<string> lines;
        try
        {
            lines = System.IO.File.ReadLines(path).Skip(1);
        }
        catch
        {
            yield break;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = SplitCsv(line);
            if (fields.Count < 8)
            {
                continue;
            }

            var timestampUtc = ParseTimestampUtc(fields[0]);
            if (fromUtc.HasValue && timestampUtc < fromUtc.Value.ToUniversalTime())
            {
                continue;
            }

            if (toUtc.HasValue && timestampUtc >= toUtc.Value.ToUniversalTime())
            {
                continue;
            }

            var status = fields[5];
            var side = fields[3];
            var rawFix = fields[7];
            var sourceKey = NormalizeSourceKey(fields.Count > 8 ? fields[8] : null, fallbackSourceKey);
            var sourceName = GetSourceName(sourceKey);

            var isDryRun = fields.Count > 9 && bool.TryParse(fields[9], out var parsedDryRun)
                ? parsedDryRun
                : string.Equals(status, "Valid. Not Sent", StringComparison.OrdinalIgnoreCase);

            var tradeCount = fields.Count > 10 && int.TryParse(fields[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTradeCount)
                ? parsedTradeCount
                : 0;

            var grossAmount = fields.Count > 11 && decimal.TryParse(fields[11], NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedGrossAmount)
                ? parsedGrossAmount
                : 0m;

            if (tradeCount <= 0 || grossAmount == 0m)
            {
                var derived = DeriveMetricsFromRawFix(rawFix);
                if (tradeCount <= 0)
                {
                    tradeCount = derived.TradeCount;
                }

                if (grossAmount == 0m)
                {
                    grossAmount = derived.GrossAmount;
                }
            }

            if (tradeCount <= 0)
            {
                tradeCount = 1;
            }

            yield return new DashboardAllocationRecord(
                timestampUtc,
                fields[1],
                sourceKey,
                string.IsNullOrWhiteSpace(sourceName) ? fallbackSourceName : sourceName,
                isDryRun,
                status,
                ResolveSideLabel(side, rawFix),
                tradeCount,
                grossAmount);
        }
    }

    private static string ResolveSideLabel(string? rawSide, string? rawFix)
    {
        var rawFixSide = TryGetRawFixValue(rawFix, 54);
        return NormalizeSideLabel(rawFixSide)
            ?? NormalizeSideLabel(rawSide)
            ?? UnknownSide;
    }

    private static string? NormalizeSideLabel(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        return NormalizeSideKey(rawValue) switch
        {
            "1" or "BUY" or "1BUY" => "BUY",
            "2" or "SELL" or "2SELL" => "SELL",
            "3" or "BUYMINUS" or "3BUYMINUS" => "BUY_MINUS",
            "4" or "SELLPLUS" or "4SELLPLUS" => "SELL_PLUS",
            "5" or "SELLSHORT" or "5SELLSHORT" => "SELL_SHORT",
            "6" or "SELLSHORTEXEMPT" or "6SELLSHORTEXEMPT" => "SELL_SHORT_EXEMPT",
            "7" or "UNDISCLOSED" or "7UNDISCLOSED" => "UNDISCLOSED",
            "8" or "CROSS" or "8CROSS" => "CROSS",
            "9" or "CROSSSHORT" or "9CROSSSHORT" => "CROSS_SHORT",
            _ => null
        };
    }

    private static string NormalizeSideKey(string rawValue)
    {
        var builder = new StringBuilder(rawValue.Length);
        foreach (var character in rawValue.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string? TryGetRawFixValue(string? rawFix, int tag)
    {
        if (string.IsNullOrWhiteSpace(rawFix))
        {
            return null;
        }

        var prefix = tag.ToString(CultureInfo.InvariantCulture) + "=";
        foreach (var token in rawFix.Split(['|', '\u0001'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith(prefix, StringComparison.Ordinal))
            {
                return token[prefix.Length..];
            }
        }

        return null;
    }

    private static (int TradeCount, decimal GrossAmount) DeriveMetricsFromRawFix(string rawFix)
    {
        if (string.IsNullOrWhiteSpace(rawFix))
        {
            return (0, 0m);
        }

        int? noAllocs = null;
        int countByAccount = 0;
        int countByQuantity = 0;
        decimal grossAmount = 0m;
        decimal? pendingQuantity = null;

        foreach (var token in rawFix.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = token.IndexOf('=');
            if (separator <= 0 || separator >= token.Length - 1)
            {
                continue;
            }

            var tagText = token[..separator];
            var valueText = token[(separator + 1)..];
            if (!int.TryParse(tagText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tag))
            {
                continue;
            }

            switch (tag)
            {
                case 78:
                    if (int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedNoAllocs))
                    {
                        noAllocs = parsedNoAllocs;
                    }
                    break;
                case 79:
                    countByAccount++;
                    break;
                case 80:
                    if (ValidationMetrics.TryParseDecimal(valueText, out var quantity))
                    {
                        countByQuantity++;
                        pendingQuantity = quantity;
                    }
                    break;
                case 153:
                    if (pendingQuantity.HasValue &&
                        ValidationMetrics.TryParseDecimal(valueText, out var price))
                    {
                        grossAmount += pendingQuantity.Value * price;
                        pendingQuantity = null;
                    }
                    break;
            }
        }

        var tradeCount = noAllocs ?? Math.Max(countByAccount, countByQuantity);
        return (tradeCount, grossAmount);
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        result.Add(sb.ToString());
        return result;
    }

    private static string GetSourceName(string sourceKey)
    {
        return string.Equals(sourceKey, EmailKey, StringComparison.OrdinalIgnoreCase)
            ? EmailName
            : DirectName;
    }

    private static string NormalizeSourceKey(string? source, string fallbackSourceKey)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return fallbackSourceKey;
        }

        if (string.Equals(source, EmailKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, EmailName, StringComparison.OrdinalIgnoreCase))
        {
            return EmailKey;
        }

        if (string.Equals(source, DirectKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, DirectName, StringComparison.OrdinalIgnoreCase))
        {
            return DirectKey;
        }

        return fallbackSourceKey;
    }

    private static string? ResolveReportsDir(string sourceKey)
    {
        if (string.Equals(sourceKey, EmailKey, StringComparison.OrdinalIgnoreCase))
        {
            var envPath = ResolveEnvPath("FIXFLOW_CLI_REPORTS");
            if (!string.IsNullOrWhiteSpace(envPath))
            {
                return envPath;
            }

            var publishedPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "FixFlowService",
                "reports"));
            if (Directory.Exists(publishedPath))
            {
                return publishedPath;
            }

            var solutionRoot = FindAncestorWithFile(AppContext.BaseDirectory, "*.sln", 8);
            if (string.IsNullOrWhiteSpace(solutionRoot))
            {
                return null;
            }

            var cliBinPath = Path.Combine(solutionRoot, "src", "FixFlow.TradeAllocBridge.CLI", "bin");
            if (!Directory.Exists(cliBinPath))
            {
                return null;
            }

            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var configurationPath = Path.Combine(cliBinPath, configuration);
                if (!Directory.Exists(configurationPath))
                {
                    continue;
                }

                foreach (var tfmPath in Directory.GetDirectories(configurationPath))
                {
                    var reportsPath = Path.Combine(tfmPath, "reports");
                    if (Directory.Exists(reportsPath))
                    {
                        return reportsPath;
                    }
                }
            }

            return null;
        }

        return Path.Combine(AppContext.BaseDirectory, "reports");
    }

    private static string? ResolveEnvPath(string envVar)
    {
        try
        {
            var env = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrWhiteSpace(env))
            {
                return null;
            }

            var candidate = env;
            if (!Path.IsPathRooted(candidate))
            {
                candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, candidate));
            }
            else
            {
                candidate = Path.GetFullPath(candidate);
            }

            return Directory.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindAncestorWithFile(string startPath, string searchPattern, int maxLevels)
    {
        try
        {
            var directory = new DirectoryInfo(startPath);
            for (int i = 0; i < maxLevels && directory != null; i++)
            {
                if (Directory.GetFiles(directory.FullName, searchPattern).Any())
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }
        catch
        {
        }

        return null;
    }

    private static DateTimeOffset ParseTimestampUtc(string raw)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            return dto.ToUniversalTime();
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
        {
            return new DateTimeOffset(local.ToUniversalTime(), TimeSpan.Zero);
        }

        return DateTimeOffset.MinValue;
    }

    private static string GetOutcomeKey(DashboardAllocationRecord record)
    {
        return string.Equals(record.ProcessingStatus, "Sent", StringComparison.OrdinalIgnoreCase)
            ? PassedKey
            : FailedKey;
    }

    private static string GetAggregateOutcomeKey(DashboardAllocationRecord record)
    {
        return record.IsDryRun ? string.Empty : GetOutcomeKey(record);
    }

    private static string ResolveBucketMode(string? rangeMode, DateTimeOffset? fromUtc, DateTimeOffset? toUtc)
    {
        if (string.Equals(rangeMode, "today", StringComparison.OrdinalIgnoreCase))
        {
            return "hour";
        }

        if (string.Equals(rangeMode, "last7", StringComparison.OrdinalIgnoreCase))
        {
            return "weekday";
        }

        if (string.Equals(rangeMode, "custom", StringComparison.OrdinalIgnoreCase))
        {
            return "dateRange";
        }

        if (fromUtc.HasValue && toUtc.HasValue)
        {
            var durationDays = (toUtc.Value.ToUniversalTime() - fromUtc.Value.ToUniversalTime()).TotalDays;
            if (durationDays <= 1.01d)
            {
                return "hour";
            }

            if (durationDays <= 7.01d)
            {
                return "weekday";
            }
        }

        return "dayOfMonth";
    }

    private static (string Key, string Label, decimal Position) ResolveBucket(
        DateTimeOffset localTimestamp,
        string bucketMode,
        DateTime? localRangeStartDate)
    {
        return bucketMode switch
        {
            "hour" => (
                localTimestamp.Hour.ToString(CultureInfo.InvariantCulture),
                localTimestamp.ToString("htt", CultureInfo.InvariantCulture).Replace("AM", " AM").Replace("PM", " PM"),
                localTimestamp.Hour),
            "weekday" => (
                localTimestamp.DayOfWeek.ToString(),
                localTimestamp.DayOfWeek.ToString(),
                localTimestamp.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)localTimestamp.DayOfWeek),
            "dateRange" => (
                localTimestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                localTimestamp.ToString("MMM d", CultureInfo.InvariantCulture),
                localRangeStartDate.HasValue
                    ? (decimal)(localTimestamp.Date - localRangeStartDate.Value.Date).TotalDays
                    : 0m),
            _ => (
                localTimestamp.Day.ToString(CultureInfo.InvariantCulture),
                localTimestamp.Day.ToString(CultureInfo.InvariantCulture),
                localTimestamp.Day)
        };
    }

    private static DateTimeOffset? TryParseReportFileDateUtc(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        const string prefix = "fix_validation_";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var datePart = fileName[prefix.Length..];
        if (!DateTime.TryParseExact(
                datePart,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return null;
        }

        return new DateTimeOffset(parsed, TimeSpan.Zero);
    }
}
