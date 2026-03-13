using System.Xml.Linq;
using FixFlow.TradeAllocBridge.Web.Shared;
using Microsoft.AspNetCore.Mvc;

namespace FixFlow.TradeAllocBridge.Web.Controllers;

[ApiController]
[Route("api/fix-dictionary")]
public class FixDictionaryController : ControllerBase
{
    private const string DefaultMessageName = "Allocation";
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, FixDictionaryData> DictionaryCache = new(StringComparer.OrdinalIgnoreCase);

    [HttpGet("dictionaries")]
    public ActionResult<IReadOnlyList<FixDictionaryOption>> GetDictionaries()
    {
        var cfgDir = GetDictionaryDirectory();
        if (cfgDir == null)
        {
            return Ok(Array.Empty<FixDictionaryOption>());
        }

        var options = Directory.GetFiles(cfgDir, "FIX*.xml")
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var fileName = Path.GetFileName(path);
                var display = FormatDictionaryDisplay(fileName);
                return new FixDictionaryOption(fileName, display);
            })
            .ToList();

        return Ok(options);
    }

    [HttpGet("load")]
    public ActionResult<FixDictionaryLoadResult> LoadDictionary([FromQuery] string? dictionary)
    {
        var data = GetDictionary(dictionary, out var statusMessage);
        var defaultMessage = data == null ? null : data.Messages
            .FirstOrDefault(option => string.Equals(option.Name, DefaultMessageName, StringComparison.OrdinalIgnoreCase))
            ?.ToShared();

        return Ok(new FixDictionaryLoadResult(statusMessage, defaultMessage));
    }

    [HttpGet("message-suggestions")]
    public ActionResult<IReadOnlyList<FixDictionaryMessageOption>> GetMessageSuggestions(
        [FromQuery] string? dictionary,
        [FromQuery] string? query)
    {
        var data = GetDictionary(dictionary, out _);
        if (data == null)
        {
            return Ok(Array.Empty<FixDictionaryMessageOption>());
        }

        var input = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input) || !char.IsLetter(input[0]))
        {
            return Ok(Array.Empty<FixDictionaryMessageOption>());
        }

        var matches = input.Length == 1
            ? data.Messages.Where(option => option.Name.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            : data.Messages.Where(option => option.Name.Contains(input, StringComparison.OrdinalIgnoreCase));

        var results = matches
            .Take(20)
            .Select(option => option.ToShared())
            .ToList();

        return Ok(results);
    }

    [HttpGet("resolve-message")]
    public ActionResult<FixDictionaryMessageOption?> ResolveMessage(
        [FromQuery] string? dictionary,
        [FromQuery] string? input)
    {
        var data = GetDictionary(dictionary, out _);
        if (data == null)
        {
            return Ok(null);
        }

        var resolved = ResolveMessageOption(data, input);
        return Ok(resolved?.ToShared());
    }

    [HttpGet("tag-suggestions")]
    public ActionResult<IReadOnlyList<FixDictionaryFieldOption>> GetTagSuggestions(
        [FromQuery] string? dictionary,
        [FromQuery] string? messageInput,
        [FromQuery] string? query)
    {
        var data = GetDictionary(dictionary, out _);
        if (data == null)
        {
            return Ok(Array.Empty<FixDictionaryFieldOption>());
        }

        var input = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input) || input.All(char.IsDigit) || !char.IsLetter(input[0]))
        {
            return Ok(Array.Empty<FixDictionaryFieldOption>());
        }

        var selectedMessage = ResolveMessageOption(data, messageInput);
        var options = GetCurrentFieldOptions(data, selectedMessage);

        var matches = input.Length == 1
            ? options.Where(option => option.Name.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            : options.Where(option => option.Name.Contains(input, StringComparison.OrdinalIgnoreCase));

        var results = matches
            .Take(20)
            .Select(option => option.ToShared())
            .ToList();

        return Ok(results);
    }

    [HttpGet("lookup")]
    public ActionResult<FixDictionaryLookupResult> LookupTag(
        [FromQuery] string? dictionary,
        [FromQuery] string? messageInput,
        [FromQuery] string? tagInput)
    {
        var data = GetDictionary(dictionary, out _);
        if (data == null)
        {
            return Ok(EmptyLookup());
        }

        var input = (tagInput ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return Ok(EmptyLookup());
        }

        var selectedMessage = ResolveMessageOption(data, messageInput);
        var field = FindFieldFromInput(data, selectedMessage, input);
        if (field == null)
        {
            return Ok(EmptyLookup());
        }

        var requiredInfo = string.Empty;
        var requiredAnswer = string.Empty;
        var isRequired = false;
        if (selectedMessage != null)
        {
            isRequired = data.MessageRequiredFields.TryGetValue(selectedMessage.Name, out var requiredFields) &&
                         requiredFields.Contains(field.Name);
            requiredInfo = $"Required in {selectedMessage.Display} message?";
            requiredAnswer = isRequired ? "Yes" : "No";
        }

        var enums = field.Enums
            .Select(option => string.IsNullOrWhiteSpace(option.Description)
                ? option.Value
                : $"{option.Value}-{option.Description}")
            .ToList();

        var messages = new List<string>();
        if (selectedMessage == null &&
            data.FieldMessageUsage.TryGetValue(field.Name, out var messageUsage))
        {
            foreach (var message in messageUsage.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                messages.Add(message.Display);
            }
        }

        var header = $"Name: {field.Name} | Tag: {field.Number} | Type: {field.Type}";
        return Ok(new FixDictionaryLookupResult(
            header,
            requiredInfo,
            requiredAnswer,
            isRequired,
            enums,
            messages));
    }

    [HttpGet("message-tags")]
    public ActionResult<IReadOnlyList<FixDictionaryTag>> GetMessageTags(
        [FromQuery] string? dictionary,
        [FromQuery] string? messageInput)
    {
        var data = GetDictionary(dictionary, out _);
        if (data == null)
        {
            return Ok(Array.Empty<FixDictionaryTag>());
        }

        var selectedMessage = ResolveMessageOption(data, messageInput);
        if (selectedMessage == null)
        {
            return Ok(Array.Empty<FixDictionaryTag>());
        }

        var options = GetCurrentFieldOptions(data, selectedMessage);
        var requiredFields = data.MessageRequiredFields.TryGetValue(selectedMessage.Name, out var required)
            ? required
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        data.MessageFieldDepths.TryGetValue(selectedMessage.Name, out var depthMap);
        data.MessageFieldOrder.TryGetValue(selectedMessage.Name, out var orderList);
        Dictionary<string, int>? orderMap = null;
        if (orderList != null && orderList.Count > 0)
        {
            orderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < orderList.Count; i++)
            {
                var name = orderList[i];
                if (!orderMap.ContainsKey(name))
                {
                    orderMap[name] = i;
                }
            }
        }

        var tags = new List<FixDictionaryTag>();
        var fallbackOrder = 0;
        foreach (var option in options)
        {
            if (!int.TryParse(option.Number, out var number))
            {
                continue;
            }

            var depth = 0;
            if (depthMap != null && depthMap.TryGetValue(option.Name, out var mappedDepth))
            {
                depth = mappedDepth;
            }

            var order = orderMap != null && orderMap.TryGetValue(option.Name, out var mappedOrder)
                ? mappedOrder
                : fallbackOrder++;

            tags.Add(new FixDictionaryTag(
                number,
                option.Name,
                option.Type,
                string.Empty,
                requiredFields.Contains(option.Name),
                option.Enums,
                Array.Empty<string>(),
                depth,
                order));
        }

        return Ok(tags);
    }

    [HttpGet("header-tags")]
    public ActionResult<IReadOnlyList<FixDictionaryTag>> GetHeaderTags([FromQuery] string? dictionary)
    {
        var data = GetDictionary(dictionary, out _);
        if (data == null)
        {
            return Ok(Array.Empty<FixDictionaryTag>());
        }

        if (data.HeaderFields.Count == 0)
        {
            return Ok(Array.Empty<FixDictionaryTag>());
        }

        return Ok(BuildHeaderTagResults(data));
    }

    [HttpGet("search")]
    public ActionResult<IReadOnlyList<FixDictionaryTag>> SearchTags(
        [FromQuery] string? dictionary,
        [FromQuery] string? messageInput,
        [FromQuery] string? query)
    {
        var data = GetDictionary(dictionary, out _);
        if (data == null)
        {
            return Ok(Array.Empty<FixDictionaryTag>());
        }

        var input = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return Ok(Array.Empty<FixDictionaryTag>());
        }

        var selectedMessage = ResolveMessageOption(data, messageInput);
        var options = GetCurrentFieldOptions(data, selectedMessage);

        if (input == "*")
        {
            if (selectedMessage == null)
            {
                return Ok(Array.Empty<FixDictionaryTag>());
            }

            return Ok(BuildTagResults(data, options, selectedMessage));
        }

        if (input.All(char.IsDigit))
        {
            var match = options.FirstOrDefault(option =>
                string.Equals(option.Number, input, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                return Ok(Array.Empty<FixDictionaryTag>());
            }

            return Ok(BuildTagResults(data, new List<FieldOption> { match }, selectedMessage));
        }

        var exact = options.FirstOrDefault(option =>
            string.Equals(option.Name, input, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return Ok(BuildTagResults(data, new List<FieldOption> { exact }, selectedMessage));
        }

        if (input.Length >= 2)
        {
            var matches = options
                .Where(option => option.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 1)
            {
                return Ok(BuildTagResults(data, matches, selectedMessage));
            }
        }

        return Ok(Array.Empty<FixDictionaryTag>());
    }

    private static string? GetDictionaryDirectory()
    {
        var cfgDir = Path.Combine(AppContext.BaseDirectory, "cfg");
        return Directory.Exists(cfgDir) ? cfgDir : null;
    }

    private static FixDictionaryData? GetDictionary(string? dictionary, out string statusMessage)
    {
        if (string.IsNullOrWhiteSpace(dictionary))
        {
            statusMessage = "Dictionary file not found.";
            return null;
        }

        var cfgDir = GetDictionaryDirectory();
        if (cfgDir == null)
        {
            statusMessage = "Dictionary folder not found.";
            return null;
        }

        var fileName = Path.GetFileName(dictionary);
        var filePath = Path.Combine(cfgDir, fileName);
        if (!System.IO.File.Exists(filePath))
        {
            statusMessage = "Dictionary file not found.";
            return null;
        }

        lock (CacheLock)
        {
            if (DictionaryCache.TryGetValue(fileName, out var cached))
            {
                statusMessage = "Dictionary loaded.";
                return cached;
            }
        }

        try
        {
            var doc = XDocument.Load(filePath);
            var root = doc.Root;
            if (root == null)
            {
                statusMessage = "Dictionary file is invalid.";
                return null;
            }

            var fields = new List<FieldOption>();
            var fieldByName = new Dictionary<string, FieldOption>(StringComparer.OrdinalIgnoreCase);
            var fieldByNumber = new Dictionary<string, FieldOption>(StringComparer.OrdinalIgnoreCase);

            foreach (var field in root.Element("fields")?.Elements("field") ?? Enumerable.Empty<XElement>())
            {
                var name = (string?)field.Attribute("name");
                var number = (string?)field.Attribute("number");
                var type = (string?)field.Attribute("type");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(number))
                {
                    continue;
                }

                var enums = field.Elements("value")
                    .Select(value => new FixDictionaryEnumValue(
                        (string?)value.Attribute("enum") ?? string.Empty,
                        (string?)value.Attribute("description") ?? string.Empty))
                    .Where(option => !string.IsNullOrWhiteSpace(option.Value))
                    .ToList();

                var option = new FieldOption(name, number, type ?? string.Empty, enums);
                fields.Add(option);
                fieldByName[name] = option;
                fieldByNumber[number] = option;
            }

            var messages = new List<MessageOption>();
            var messageFields = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var messageRequiredFields = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var messageFieldDepths = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            var messageFieldOrder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var fieldMessageUsage = new Dictionary<string, List<MessageOption>>(StringComparer.OrdinalIgnoreCase);

            foreach (var message in root.Element("messages")?.Elements("message") ?? Enumerable.Empty<XElement>())
            {
                var msgcat = (string?)message.Attribute("msgcat");
                if (string.Equals(msgcat, "admin", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = (string?)message.Attribute("name");
                var msgType = (string?)message.Attribute("msgtype");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var msgOption = new MessageOption(name, msgType ?? string.Empty);
                messages.Add(msgOption);

                var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var requiredFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectFieldNames(message, fieldNames, requiredFields);
                var fieldDepths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                CollectFieldDepths(message, fieldDepths, 0);
                var fieldOrder = new List<string>();
                CollectFieldOrder(message, fieldOrder, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

                messageFields[name] = fieldNames;
                messageRequiredFields[name] = requiredFields;
                messageFieldDepths[name] = fieldDepths;
                messageFieldOrder[name] = fieldOrder;

                foreach (var fieldName in fieldNames)
                {
                    if (!fieldMessageUsage.TryGetValue(fieldName, out var list))
                    {
                        list = new List<MessageOption>();
                        fieldMessageUsage[fieldName] = list;
                    }

                    list.Add(msgOption);
                }
            }

            messages.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            fields.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            var headerFieldNames = new List<string>();
            var headerRequiredFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var header = root.Element("header");
            if (header != null)
            {
                CollectHeaderFieldNames(header, headerFieldNames, headerRequiredFields);
            }

            var headerFields = new List<FieldOption>();
            foreach (var name in headerFieldNames)
            {
                if (fieldByName.TryGetValue(name, out var option))
                {
                    headerFields.Add(option);
                }
            }

            var data = new FixDictionaryData(
                fields,
                fieldByName,
                fieldByNumber,
                messages,
                messageFields,
                messageRequiredFields,
                messageFieldDepths,
                messageFieldOrder,
                fieldMessageUsage,
                headerFields,
                headerRequiredFields);

            lock (CacheLock)
            {
                DictionaryCache[fileName] = data;
            }

            statusMessage = "Dictionary loaded.";
            return data;
        }
        catch (Exception ex)
        {
            statusMessage = $"Failed to load dictionary: {ex.Message}";
            return null;
        }
    }

    private static void CollectFieldNames(XElement element, HashSet<string> fieldNames, HashSet<string> requiredFields)
    {
        foreach (var child in element.Elements())
        {
            var local = child.Name.LocalName;
            if (string.Equals(local, "field", StringComparison.OrdinalIgnoreCase))
            {
                var name = (string?)child.Attribute("name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    fieldNames.Add(name);
                    if (IsRequired(child))
                    {
                        requiredFields.Add(name);
                    }
                }
            }
            else if (string.Equals(local, "group", StringComparison.OrdinalIgnoreCase))
            {
                var groupName = (string?)child.Attribute("name");
                if (!string.IsNullOrWhiteSpace(groupName))
                {
                    fieldNames.Add(groupName);
                    if (IsRequired(child))
                    {
                        requiredFields.Add(groupName);
                    }
                }

                CollectFieldNames(child, fieldNames, requiredFields);
            }
        }
    }

    private static void CollectFieldDepths(XElement element, Dictionary<string, int> fieldDepths, int depth)
    {
        foreach (var child in element.Elements())
        {
            var local = child.Name.LocalName;
            if (string.Equals(local, "field", StringComparison.OrdinalIgnoreCase))
            {
                var name = (string?)child.Attribute("name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    SetDepth(fieldDepths, name, depth);
                }
            }
            else if (string.Equals(local, "group", StringComparison.OrdinalIgnoreCase))
            {
                var groupName = (string?)child.Attribute("name");
                if (!string.IsNullOrWhiteSpace(groupName))
                {
                    SetDepth(fieldDepths, groupName, depth);
                }

                CollectFieldDepths(child, fieldDepths, depth + 1);
            }
        }
    }

    private static void CollectFieldOrder(XElement element, List<string> orderedNames, HashSet<string> seen)
    {
        foreach (var child in element.Elements())
        {
            var local = child.Name.LocalName;
            if (string.Equals(local, "field", StringComparison.OrdinalIgnoreCase))
            {
                var name = (string?)child.Attribute("name");
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                {
                    orderedNames.Add(name);
                }
            }
            else if (string.Equals(local, "group", StringComparison.OrdinalIgnoreCase))
            {
                var groupName = (string?)child.Attribute("name");
                if (!string.IsNullOrWhiteSpace(groupName) && seen.Add(groupName))
                {
                    orderedNames.Add(groupName);
                }

                CollectFieldOrder(child, orderedNames, seen);
            }
        }
    }

    private static void SetDepth(Dictionary<string, int> fieldDepths, string name, int depth)
    {
        if (fieldDepths.TryGetValue(name, out var existing))
        {
            fieldDepths[name] = Math.Min(existing, depth);
        }
        else
        {
            fieldDepths[name] = depth;
        }
    }

    private static void CollectHeaderFieldNames(
        XElement element,
        List<string> fieldNames,
        HashSet<string> requiredFields)
    {
        foreach (var child in element.Elements())
        {
            var local = child.Name.LocalName;
            if (string.Equals(local, "field", StringComparison.OrdinalIgnoreCase))
            {
                var name = (string?)child.Attribute("name");
                if (!string.IsNullOrWhiteSpace(name) && !fieldNames.Contains(name))
                {
                    fieldNames.Add(name);
                }
                if (!string.IsNullOrWhiteSpace(name) && IsRequired(child))
                {
                    requiredFields.Add(name);
                }
            }
            else if (string.Equals(local, "group", StringComparison.OrdinalIgnoreCase))
            {
                var groupName = (string?)child.Attribute("name");
                if (!string.IsNullOrWhiteSpace(groupName) && !fieldNames.Contains(groupName))
                {
                    fieldNames.Add(groupName);
                }
                if (!string.IsNullOrWhiteSpace(groupName) && IsRequired(child))
                {
                    requiredFields.Add(groupName);
                }

                CollectHeaderFieldNames(child, fieldNames, requiredFields);
            }
        }
    }

    private static MessageOption? ResolveMessageOption(FixDictionaryData data, string? input)
    {
        var trimmed = (input ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        return data.Messages.FirstOrDefault(option =>
            string.Equals(option.Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.Display, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<FieldOption> GetCurrentFieldOptions(FixDictionaryData data, MessageOption? selectedMessage)
    {
        if (selectedMessage == null)
        {
            return data.Fields;
        }

        if (data.MessageFieldOrder.TryGetValue(selectedMessage.Name, out var orderedNames) &&
            orderedNames.Count > 0)
        {
            var filtered = new List<FieldOption>();
            foreach (var name in orderedNames)
            {
                if (data.FieldByName.TryGetValue(name, out var option))
                {
                    filtered.Add(option);
                }
            }

            return filtered;
        }

        if (data.MessageFields.TryGetValue(selectedMessage.Name, out var fieldNames))
        {
            var filtered = new List<FieldOption>();
            foreach (var name in fieldNames)
            {
                if (data.FieldByName.TryGetValue(name, out var option))
                {
                    filtered.Add(option);
                }
            }

            filtered.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return filtered;
        }

        return data.Fields;
    }

    private static FieldOption? FindFieldFromInput(FixDictionaryData data, MessageOption? selectedMessage, string input)
    {
        var options = GetCurrentFieldOptions(data, selectedMessage);
        if (input.All(char.IsDigit))
        {
            return options.FirstOrDefault(option =>
                string.Equals(option.Number, input, StringComparison.OrdinalIgnoreCase));
        }

        var exact = options.FirstOrDefault(option =>
            string.Equals(option.Name, input, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        if (input.Length >= 2)
        {
            var matches = options
                .Where(option => option.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }
        }

        return null;
    }

    private static FixDictionaryLookupResult EmptyLookup() =>
        new(string.Empty, string.Empty, string.Empty, false, Array.Empty<string>(), Array.Empty<string>());

    private static string FormatDictionaryDisplay(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return fileName;

        var baseName = Path.GetFileNameWithoutExtension(fileName) ?? fileName;
        if (!baseName.StartsWith("FIX", StringComparison.OrdinalIgnoreCase))
        {
            return baseName;
        }

        var suffix = baseName.Substring(3);
        if (suffix.Length >= 2 && suffix.All(char.IsDigit))
        {
            var major = suffix[0];
            var minor = suffix.Substring(1);
            return $"FIX.{major}.{minor}";
        }

        return baseName;
    }

    private static bool IsRequired(XElement element)
    {
        var required = (string?)element.Attribute("required");
        return string.Equals(required, "Y", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<FixDictionaryTag> BuildTagResults(
        FixDictionaryData data,
        IEnumerable<FieldOption> fields,
        MessageOption? selectedMessage)
    {
        HashSet<string>? requiredFields = null;
        IReadOnlyDictionary<string, int>? depthMap = null;
        IReadOnlyDictionary<string, int>? orderMap = null;
        if (selectedMessage != null &&
            data.MessageRequiredFields.TryGetValue(selectedMessage.Name, out var required))
        {
            requiredFields = required;
        }
        if (selectedMessage != null &&
            data.MessageFieldDepths.TryGetValue(selectedMessage.Name, out var depths))
        {
            depthMap = depths;
        }
        if (selectedMessage != null &&
            data.MessageFieldOrder.TryGetValue(selectedMessage.Name, out var orderList) &&
            orderList.Count > 0)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < orderList.Count; i++)
            {
                var name = orderList[i];
                if (!map.ContainsKey(name))
                {
                    map[name] = i;
                }
            }

            orderMap = map;
        }

        var results = new List<FixDictionaryTag>();
        var fallbackOrder = 0;
        foreach (var option in fields)
        {
            if (!int.TryParse(option.Number, out var number))
            {
                continue;
            }

            var depth = 0;
            if (depthMap != null && depthMap.TryGetValue(option.Name, out var mappedDepth))
            {
                depth = mappedDepth;
            }

            var order = orderMap != null && orderMap.TryGetValue(option.Name, out var mappedOrder)
                ? mappedOrder
                : fallbackOrder++;

            var messages = Array.Empty<string>();
            if (selectedMessage == null &&
                data.FieldMessageUsage.TryGetValue(option.Name, out var usage))
            {
                messages = usage
                    .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(m => m.Display)
                    .ToArray();
            }

            results.Add(new FixDictionaryTag(
                number,
                option.Name,
                option.Type,
                string.Empty,
                requiredFields == null ? null : requiredFields.Contains(option.Name),
                option.Enums,
                messages,
                depth,
                order));
        }

        return results;
    }

    private static IReadOnlyList<FixDictionaryTag> BuildHeaderTagResults(FixDictionaryData data)
    {
        var results = new List<FixDictionaryTag>();
        foreach (var option in data.HeaderFields)
        {
            if (!int.TryParse(option.Number, out var number))
            {
                continue;
            }

            results.Add(new FixDictionaryTag(
                number,
                option.Name,
                option.Type,
                string.Empty,
                data.HeaderRequiredFields.Contains(option.Name),
                option.Enums,
                Array.Empty<string>(),
                0,
                results.Count));
        }

        return results;
    }

    private sealed record FixDictionaryData(
        IReadOnlyList<FieldOption> Fields,
        IReadOnlyDictionary<string, FieldOption> FieldByName,
        IReadOnlyDictionary<string, FieldOption> FieldByNumber,
        IReadOnlyList<MessageOption> Messages,
        IReadOnlyDictionary<string, HashSet<string>> MessageFields,
        IReadOnlyDictionary<string, HashSet<string>> MessageRequiredFields,
        IReadOnlyDictionary<string, Dictionary<string, int>> MessageFieldDepths,
        IReadOnlyDictionary<string, List<string>> MessageFieldOrder,
        IReadOnlyDictionary<string, List<MessageOption>> FieldMessageUsage,
        IReadOnlyList<FieldOption> HeaderFields,
        HashSet<string> HeaderRequiredFields);

    private sealed record MessageOption(string Name, string MsgType)
    {
        public string Display => string.IsNullOrWhiteSpace(MsgType) ? Name : $"{Name} ({MsgType})";
        public FixDictionaryMessageOption ToShared() => new(Name, MsgType, Display);
    }

    private sealed record FieldOption(
        string Name,
        string Number,
        string Type,
        List<FixDictionaryEnumValue> Enums)
    {
        public string Display => $"{Name} ({Number})";
        public FixDictionaryFieldOption ToShared() => new(Name, Number, Type, Enums, Display);
    }
}
