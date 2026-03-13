using FixFlow.TradeAllocBridge.Core.Excel;
using FixFlow.TradeAllocBridge.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace FixFlow.TradeAllocBridge.Web.Controllers;

[ApiController]
[Route("api/samples")]
public class SamplesController : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();
    private readonly ExcelParser _excelParser;

    public SamplesController(ExcelParser excelParser)
    {
        _excelParser = excelParser;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<SampleFileOption>> GetSamples()
    {
        var samplesDir = Path.Combine(AppContext.BaseDirectory, "samples");
        if (!Directory.Exists(samplesDir))
        {
            return Ok(Array.Empty<SampleFileOption>());
        }

        var results = new List<SampleFileOption>();
        foreach (var filePath in Directory.GetFiles(samplesDir))
        {
            var extension = Path.GetExtension(filePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                continue;
            }

            if (!IsSupportedFile(extension))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(filePath);
                var sizeKb = (long)Math.Ceiling(info.Length / 1024.0);
                results.Add(new SampleFileOption(info.Name, sizeKb));
            }
            catch
            {
                // Ignore unreadable files.
            }
        }

        return Ok(results.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase));
    }

    [HttpGet("preview/{fileName}")]
    public ActionResult<SamplePreviewResponse> GetSamplePreview(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return NotFound();
        }

        var safeName = Path.GetFileName(fileName);
        var extension = Path.GetExtension(safeName);
        if (!IsSupportedFile(extension))
        {
            return NotFound();
        }

        var samplesDir = Path.Combine(AppContext.BaseDirectory, "samples");
        var filePath = Path.Combine(samplesDir, safeName);
        if (!System.IO.File.Exists(filePath))
        {
            return NotFound();
        }

        try
        {
            var records = _excelParser.Parse(filePath);
            var headers = records.FirstOrDefault()?.Fields.Keys.ToList() ?? new List<string>();
            var previewRows = records
                .Take(50)
                .Select(record => (IReadOnlyList<string>)headers
                    .Select(header => record.Fields.TryGetValue(header, out var value) ? value ?? string.Empty : string.Empty)
                    .ToList())
                .ToList();

            return Ok(new SamplePreviewResponse(
                safeName,
                headers,
                previewRows,
                records.Count,
                previewRows.Count));
        }
        catch
        {
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    [HttpGet("{fileName}")]
    public IActionResult GetSample(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return NotFound();
        }

        var safeName = Path.GetFileName(fileName);
        var extension = Path.GetExtension(safeName);
        if (!IsSupportedFile(extension))
        {
            return NotFound();
        }

        var samplesDir = Path.Combine(AppContext.BaseDirectory, "samples");
        var filePath = Path.Combine(samplesDir, safeName);
        if (!System.IO.File.Exists(filePath))
        {
            return NotFound();
        }

        if (!ContentTypeProvider.TryGetContentType(filePath, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return PhysicalFile(filePath, contentType);
    }

    private static bool IsSupportedFile(string extension)
    {
        return extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".xls", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase);
    }
}
