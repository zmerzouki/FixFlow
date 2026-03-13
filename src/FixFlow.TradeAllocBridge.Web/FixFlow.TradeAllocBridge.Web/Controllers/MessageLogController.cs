using System.Globalization;
using System.Text;
using System.Linq;
using FixFlow.TradeAllocBridge.Web.Shared;
using Microsoft.AspNetCore.Mvc;

namespace FixFlow.TradeAllocBridge.Web.Controllers;

[ApiController]
[Route("api/messages")]
public class MessageLogController : ControllerBase
{
    private const string DirectKey = "Direct";
    private const string EmailKey = "Email";
    private const string DirectName = "Direct Ingestion";
    private const string EmailName = "Email Automation";

    [HttpGet]
    public ActionResult<IReadOnlyList<MessageLogEntry>> Get(
        [FromQuery] string? clientId = null,
        [FromQuery] string? source = null,
        [FromQuery] string? status = null)
    {
        var sourceKey = string.IsNullOrWhiteSpace(source) ? DirectKey : source.Trim();
        var entries = LoadEntries(sourceKey);

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            entries = entries
                .Where(e => string.Equals(e.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            entries = entries
                .Where(e => string.Equals(e.ProcessingStatus, status, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var ordered = entries
            .OrderByDescending(e => e.Timestamp)
            .ToList();

        return Ok(ordered);
    }

    [HttpGet("statuses")]
    public ActionResult<IReadOnlyList<string>> GetStatuses([FromQuery] string? source = null)
    {
        var sourceKey = string.IsNullOrWhiteSpace(source) ? DirectKey : source.Trim();
        var entries = LoadEntries(sourceKey);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var statuses = new List<string>();

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.ProcessingStatus)) continue;
            if (seen.Add(entry.ProcessingStatus))
            {
                statuses.Add(entry.ProcessingStatus);
            }
        }

        return Ok(statuses.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static List<MessageLogEntry> LoadEntries(string sourceKey)
    {
        var reportsDir = ResolveReportsDir(sourceKey);

        if (string.IsNullOrWhiteSpace(reportsDir) || !System.IO.Directory.Exists(reportsDir))
        {
            return new List<MessageLogEntry>();
        }

        var files = System.IO.Directory.GetFiles(reportsDir, "fix_validation_*.csv")
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0)
        {
            return new List<MessageLogEntry>();
        }

        var entries = new List<MessageLogEntry>();
        foreach (var file in files)
        {
            entries.AddRange(ParseReport(file, GetSourceName(sourceKey)));
        }

        return entries;
    }

    private static IEnumerable<MessageLogEntry> ParseReport(string path, string sourceName)
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

            var timestampRaw = fields[0];
            var timestamp = ParseTimestampUtc(timestampRaw);

            yield return new MessageLogEntry(
                Timestamp: timestamp,
                ClientId: fields[1],
                AllocId: fields[2],
                Side: fields[3],
                Symbol: fields[4],
                ProcessingStatus: fields[5],
                ErrorDetails: fields[6],
                RawFixMessage: fields[7],
                Source: sourceName,
                ReportFile: System.IO.Path.GetFileName(path));
        }
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

    private static string? ResolveReportsDir(string sourceKey)
    {
        if (string.Equals(sourceKey, EmailKey, StringComparison.OrdinalIgnoreCase))
        {
            var envPath = ResolveEnvPath("FIXFLOW_CLI_REPORTS");
            if (!string.IsNullOrWhiteSpace(envPath))
            {
                return envPath;
            }

            var publishedPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "FixFlowService",
                "reports"));
            if (System.IO.Directory.Exists(publishedPath))
            {
                return publishedPath;
            }

            var solutionRoot = FindAncestorWithFile(AppContext.BaseDirectory, "*.sln", maxLevels: 8);
            if (string.IsNullOrWhiteSpace(solutionRoot))
            {
                return null;
            }

            var cliProj = System.IO.Path.Combine(solutionRoot, "src", "FixFlow.TradeAllocBridge.CLI", "bin");
            if (!System.IO.Directory.Exists(cliProj))
            {
                return null;
            }

            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var cfgDir = System.IO.Path.Combine(cliProj, configuration);
                if (!System.IO.Directory.Exists(cfgDir)) continue;

                foreach (var tfmDir in System.IO.Directory.GetDirectories(cfgDir))
                {
                    var reportsDir = System.IO.Path.Combine(tfmDir, "reports");
                    if (System.IO.Directory.Exists(reportsDir))
                    {
                        return reportsDir;
                    }
                }
            }

            return null;
        }

        return System.IO.Path.Combine(AppContext.BaseDirectory, "reports");
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
            if (!System.IO.Path.IsPathRooted(candidate))
            {
                candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, candidate));
            }
            else
            {
                candidate = System.IO.Path.GetFullPath(candidate);
            }

            return System.IO.Directory.Exists(candidate) ? candidate : null;
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
            var di = new DirectoryInfo(startPath);
            for (int i = 0; i < maxLevels && di != null; i++)
            {
                if (System.IO.Directory.GetFiles(di.FullName, searchPattern).Any()) return di.FullName;
                di = di.Parent;
            }
        }
        catch
        {
        }

        return null;
    }

    private static DateTime ParseTimestampUtc(string raw)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            return dto.UtcDateTime;
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
        {
            return local.ToUniversalTime();
        }

        return DateTime.MinValue;
    }
}
