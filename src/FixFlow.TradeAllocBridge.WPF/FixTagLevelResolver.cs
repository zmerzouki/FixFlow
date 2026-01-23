using System.IO;
using System.Xml.Linq;

namespace FixFlow.TradeAllocBridge.WPF;

public static class FixTagLevelResolver
{
    private static readonly object Sync = new();
    private static Dictionary<string, string>? _tagLevels;

    public static string GetLevel(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "Unknown";

        EnsureLoaded();
        return _tagLevels != null && _tagLevels.TryGetValue(tag.Trim(), out var level)
            ? level
            : "Unknown";
    }

    private static void EnsureLoaded()
    {
        if (_tagLevels != null) return;

        lock (Sync)
        {
            if (_tagLevels != null) return;
            _tagLevels = LoadLevels();
        }
    }

    private static Dictionary<string, string> LoadLevels()
    {
        var levels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.xml");
            if (!File.Exists(path)) return levels;

            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root == null) return levels;

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

            if (allocation == null) return levels;

            foreach (var element in allocation.Elements())
            {
                if (element.Name.LocalName == "field")
                {
                    AddLevel(levels, fieldMap, (string?)element.Attribute("name"), "Header");
                }
                else if (element.Name.LocalName == "group" &&
                         string.Equals((string?)element.Attribute("name"), "NoAllocs", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var groupField in element.Elements("field"))
                    {
                        AddLevel(levels, fieldMap, (string?)groupField.Attribute("name"), "NoAllocs");
                    }

                    foreach (var nestedGroup in element.Elements("group"))
                    {
                        var groupName = (string?)nestedGroup.Attribute("name");
                        if (!string.Equals(groupName, "NoMiscFees", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        foreach (var groupField in nestedGroup.Elements("field"))
                        {
                            AddLevel(levels, fieldMap, (string?)groupField.Attribute("name"), "NoMiscFees");
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore and return what we have.
        }

        return levels;
    }

    private static void AddLevel(
        IDictionary<string, string> levels,
        IDictionary<string, string> fieldMap,
        string? fieldName,
        string level)
    {
        if (string.IsNullOrWhiteSpace(fieldName)) return;
        if (!fieldMap.TryGetValue(fieldName, out var number)) return;
        if (string.IsNullOrWhiteSpace(number)) return;

        levels[number] = level;
    }
}
