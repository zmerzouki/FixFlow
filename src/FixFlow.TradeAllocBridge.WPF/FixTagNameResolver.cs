using System.IO;
using System.Xml.Linq;

namespace FixFlow.TradeAllocBridge.WPF;

public static class FixTagNameResolver
{
    private static readonly object Sync = new();
    private static Dictionary<string, string>? _tagNames;

    public static string GetDisplay(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return tag;

        EnsureLoaded();
        if (_tagNames != null && _tagNames.TryGetValue(tag.Trim(), out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return $"Tag {tag.Trim()} ({name})";
        }

        return $"Tag {tag.Trim()}";
    }

    private static void EnsureLoaded()
    {
        if (_tagNames != null) return;

        lock (Sync)
        {
            if (_tagNames != null) return;
            _tagNames = LoadNames();
        }
    }

    private static Dictionary<string, string> LoadNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.xml");
            if (!File.Exists(path)) return names;

            var doc = XDocument.Load(path);
            var fields = doc.Root?.Element("fields")?.Elements("field");
            if (fields == null) return names;

            foreach (var field in fields)
            {
                var number = (string?)field.Attribute("number");
                var name = (string?)field.Attribute("name");
                if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(name)) continue;
                names[number] = name;
            }
        }
        catch
        {
            return names;
        }

        return names;
    }
}
