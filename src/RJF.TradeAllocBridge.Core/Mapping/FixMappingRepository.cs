using System;
using System.Text.Json;

namespace RJF.TradeAllocBridge.Core.Mapping;

public class FixMappingRepository
{
    public string BaseDirectory { get; }
    public event EventHandler? MappingsChanged;

    public FixMappingRepository(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentNullException(nameof(baseDirectory), "Base directory cannot be null or empty.");

        BaseDirectory = Path.GetFullPath(baseDirectory);
        Directory.CreateDirectory(BaseDirectory);
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
                Console.WriteLine($"??  Failed to load mapping file {file}: {ex.Message}");
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

