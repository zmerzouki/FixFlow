using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FixFlow.TradeAllocBridge.Core.Mapping;

public class FixMappingRepository
{
    public string BaseDirectory { get; }
    public event EventHandler? MappingsChanged;

    private readonly ILogger<FixMappingRepository>? _logger;

    // Keep backwards compatibility by making logger optional.
    public FixMappingRepository(string baseDirectory, ILogger<FixMappingRepository>? logger = null)
    {
        _logger = logger;

        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentNullException(nameof(baseDirectory), "Base directory cannot be null or empty.");

        BaseDirectory = Path.GetFullPath(baseDirectory);
        Directory.CreateDirectory(BaseDirectory);

        _logger?.LogInformation("FixMappingRepository initialized with BaseDirectory: {BaseDirectory}", BaseDirectory);
    }

    public void NotifyMappingsChanged()
    {
        MappingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IEnumerable<FixMapping> GetAll()
    {
        if (!Directory.Exists(BaseDirectory))
            yield break;

        foreach (var file in Directory.EnumerateFiles(BaseDirectory, "*_map.json"))
        {
            FixMapping? mapping = null;
            try
            {
                mapping = FixMapping.Load(file);
            }
            catch (Exception ex)
            {
                // Use ILogger instead of Console.WriteLine so library consumers can observe logs.
                _logger?.LogWarning(ex, "Failed to load mapping file {File}", file);
            }
            if (mapping != null)
                yield return mapping;
        }
    }

    public FixMapping? GetByClientId(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return null;

        var path = Path.Combine(BaseDirectory, $"{clientId}_map.json");
        return File.Exists(path) ? FixMapping.Load(path) : null;
    }
}

