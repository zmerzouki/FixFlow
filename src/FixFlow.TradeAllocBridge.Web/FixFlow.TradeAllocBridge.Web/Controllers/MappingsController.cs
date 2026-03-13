using FixFlow.TradeAllocBridge.Core.Mapping;
using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using System.Xml.Linq;

namespace FixFlow.TradeAllocBridge.Web.Controllers;

[ApiController]
[Route("api/mappings")]
public class MappingsController : ControllerBase
{
    private readonly FixMappingRepository _mappingRepo;
    private static readonly Lazy<FixDictionaryCache> FixDictionary = new(LoadFixDictionary);

    public MappingsController(FixMappingRepository mappingRepo)
    {
        _mappingRepo = mappingRepo;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<MappingOption>> Get()
    {
        var mappings = _mappingRepo
            .GetAll()
            .OrderBy(m => m.ClientId, StringComparer.OrdinalIgnoreCase)
            .Select(m =>
            {
                var display = string.IsNullOrWhiteSpace(m.SenderDomain)
                    ? m.ClientId
                    : $"{m.ClientId} ({m.SenderDomain})";
                var validatedOn = string.IsNullOrWhiteSpace(m.DateValidated) ? null : m.DateValidated.Trim();
                var isValidated = !string.IsNullOrWhiteSpace(validatedOn);
                return new MappingOption(m.ClientId, display, isValidated, validatedOn, m.SenderDomain);
            })
            .ToList();

        return Ok(mappings);
    }

    [HttpGet("{clientId}")]
    public ActionResult<MappingDetails> GetByClientId(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return BadRequest("Client ID is required.");
        }

        var mapPath = Path.Combine(_mappingRepo.BaseDirectory, $"{clientId}_map.json");
        if (!System.IO.File.Exists(mapPath))
        {
            return NotFound();
        }

        try
        {
            var mapping = MappingConfig.Load(mapPath);
            return Ok(new MappingDetails(
                ClientId: mapping.ClientId ?? clientId,
                SenderDomain: mapping.SenderDomain,
                FixSenderCompId: mapping.Predefined?.SenderCompID,
                FixTargetCompId: mapping.Predefined?.TargetCompID,
                OnBehalfOfCompId: mapping.Predefined?.OnBehalfOfCompID,
                MappedFieldsCount: mapping.TradeAllocations?.Count ?? 0));
        }
        catch (Exception ex)
        {
            return Problem($"Failed to load mapping: {ex.Message}");
        }
    }

    [HttpGet("{clientId}/fields")]
    public ActionResult<IReadOnlyList<MappingFieldDetail>> GetFields(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return BadRequest("Client ID is required.");
        }

        var mapPath = Path.Combine(_mappingRepo.BaseDirectory, $"{clientId}_map.json");
        if (!System.IO.File.Exists(mapPath))
        {
            return NotFound();
        }

        try
        {
            var mapping = MappingConfig.Load(mapPath);
            var fieldMap = mapping.TradeAllocations ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dictionary = FixDictionary.Value;

            var details = fieldMap
                .Select(kvp => new
                {
                    ColumnName = kvp.Key,
                    Tag = kvp.Value?.Trim() ?? string.Empty
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Tag))
                .Select(item => new MappingFieldDetail(
                    Tag: item.Tag,
                    TagName: dictionary.TagNames.TryGetValue(item.Tag, out var name) ? name : null,
                    ColumnName: item.ColumnName,
                    IsRequired: dictionary.RequiredTags.Contains(item.Tag)))
                .OrderBy(item => int.TryParse(item.Tag, out var tagNum) ? tagNum : int.MaxValue)
                .ThenBy(item => item.ColumnName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Ok(details);
        }
        catch (Exception ex)
        {
            return Problem($"Failed to load mapping fields: {ex.Message}");
        }
    }

    private sealed record FixDictionaryCache(
        Dictionary<string, string> TagNames,
        HashSet<string> RequiredTags);

    private static FixDictionaryCache LoadFixDictionary()
    {
        var tagNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var requiredTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(AppContext.BaseDirectory, "cfg", "FIX42.xml");

        if (!System.IO.File.Exists(path))
        {
            return new FixDictionaryCache(tagNames, requiredTags);
        }

        try
        {
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root == null)
            {
                return new FixDictionaryCache(tagNames, requiredTags);
            }

            var fields = root.Element("fields")?.Elements("field") ?? Enumerable.Empty<XElement>();
            var nameToNumber = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in fields)
            {
                var number = (string?)field.Attribute("number");
                var name = (string?)field.Attribute("name");
                if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(name)) continue;
                tagNames[number.Trim()] = name.Trim();
                nameToNumber[name.Trim()] = number.Trim();
            }

            var components = root.Element("components")?
                .Elements("component")
                .Where(c => !string.IsNullOrWhiteSpace((string?)c.Attribute("name")))
                .ToDictionary(c => (string)c.Attribute("name")!, c => c, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);

            var allocation = root.Element("messages")?
                .Elements("message")
                .FirstOrDefault(m => string.Equals((string?)m.Attribute("name"), "Allocation", StringComparison.OrdinalIgnoreCase));

            if (allocation != null)
            {
                CollectRequiredFields(allocation, nameToNumber, components, requiredTags, parentRequired: true);
            }
        }
        catch
        {
            return new FixDictionaryCache(tagNames, requiredTags);
        }

        return new FixDictionaryCache(tagNames, requiredTags);
    }

    private static void CollectRequiredFields(
        XElement element,
        IDictionary<string, string> nameToNumber,
        IDictionary<string, XElement> components,
        ISet<string> requiredTags,
        bool parentRequired)
    {
        foreach (var child in element.Elements())
        {
            var name = child.Name.LocalName;
            switch (name)
            {
                case "field":
                    if (parentRequired && IsRequired(child))
                    {
                        var fieldName = (string?)child.Attribute("name");
                        if (!string.IsNullOrWhiteSpace(fieldName) &&
                            nameToNumber.TryGetValue(fieldName.Trim(), out var tag))
                        {
                            requiredTags.Add(tag);
                        }
                    }
                    break;
                case "group":
                    CollectRequiredFields(child, nameToNumber, components, requiredTags, parentRequired && IsRequired(child));
                    break;
                case "component":
                    var componentName = (string?)child.Attribute("name");
                    if (!string.IsNullOrWhiteSpace(componentName) && components.TryGetValue(componentName, out var component))
                    {
                        CollectRequiredFields(component, nameToNumber, components, requiredTags, parentRequired && IsRequired(child));
                    }
                    break;
            }
        }
    }

    private static bool IsRequired(XElement element)
    {
        var required = (string?)element.Attribute("required");
        return string.Equals(required, "Y", StringComparison.OrdinalIgnoreCase);
    }
}
