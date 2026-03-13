using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FixFlow.TradeAllocBridge.Core.Config
{
    /// <summary>
    /// Represents a single client's FIX mapping configuration.
    /// Supports flat tag mappings and predefined FIX header fields.
    /// </summary>
    public class MappingConfig
    {
        /// <summary>
        /// Logical client identifier (e.g., "RAJA").
        /// </summary>
        [JsonPropertyName("clientId")]
        public string? ClientId { get; set; }

        /// <summary>
        /// Friendly organization name for the mapping (e.g., "Contoso Asset Management").
        /// </summary>
        [JsonPropertyName("organizationName")]
        public string? OrganizationName { get; set; }

        /// <summary>
        /// Optional sender domain match (e.g., "rjf.local").
        /// Used by GraphEmailService to auto-resolve client maps.
        /// </summary>
        [JsonPropertyName("senderDomain")]
        public string? SenderDomain { get; set; }

        /// <summary>
        /// Optional field delimiter for multi-value cells (default = ';').
        /// Used when splitting repeating group values like "GS;UBS".
        /// </summary>
        [JsonPropertyName("delimiter")]
        public string Delimiter { get; set; } = ";";

        /// <summary>
        /// Flat Excel → FIX tag mappings (column name → tag number).
        /// </summary>
        [JsonPropertyName("tradeAllocations")]
        public Dictionary<string, string> TradeAllocations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Default FIX tag values (tag number → value) when not supplied by the spreadsheet.
        /// </summary>
        [JsonPropertyName("defaultTagValues")]
        public Dictionary<string, string> DefaultTagValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Per-field default tag values (column name → (tag number → value)).
        /// Used when the same tag is mapped multiple times (e.g., repeating groups).
        /// </summary>
        [JsonPropertyName("fieldDefaultTagValues")]
        public Dictionary<string, Dictionary<string, string>> FieldDefaultTagValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Optional predefined FIX header fields for this client (SenderCompID, TargetCompID, etc.).
        /// </summary>
        [JsonPropertyName("predefined")]
        public PredefinedFields? Predefined { get; set; }

        /// <summary>
        /// Date/time of the most recent successful dry-run validation.
        /// </summary>
        [JsonPropertyName("dateValidated")]
        public string? DateValidated { get; set; }


        // Cache the JsonSerializerOptions instance
        private static readonly JsonSerializerOptions CachedJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        /// <summary>
        /// Loads a MappingConfig from the specified JSON file.
        /// </summary>
        public static MappingConfig Load(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Mapping file not found", filePath);

            var json = File.ReadAllText(filePath);

            var config = JsonSerializer.Deserialize<MappingConfig>(json, CachedJsonOptions)
                         ?? throw new InvalidOperationException($"Failed to deserialize mapping file: {filePath}");

            // Fallbacks for backward compatibility
            if (string.IsNullOrWhiteSpace(config.Delimiter))
                config.Delimiter = ";";

            config.TradeAllocations ??= new(StringComparer.OrdinalIgnoreCase);
            config.DefaultTagValues ??= new(StringComparer.OrdinalIgnoreCase);
            config.FieldDefaultTagValues ??= new(StringComparer.OrdinalIgnoreCase);

            if (config.FieldDefaultTagValues.Count > 0)
            {
                var normalized = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var columnEntry in config.FieldDefaultTagValues)
                {
                    var column = columnEntry.Key?.Trim();
                    if (string.IsNullOrWhiteSpace(column))
                    {
                        continue;
                    }

                    var tagDefaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (columnEntry.Value != null)
                    {
                        foreach (var tagEntry in columnEntry.Value)
                        {
                            var tag = tagEntry.Key?.Trim();
                            var value = tagEntry.Value?.Trim() ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(value))
                            {
                                continue;
                            }

                            tagDefaults[tag] = value;
                        }
                    }

                    if (tagDefaults.Count > 0)
                    {
                        normalized[column] = tagDefaults;
                    }
                }

                config.FieldDefaultTagValues = normalized;
            }
            return config;
        }
    }

    /// <summary>
    /// Represents per-client predefined FIX header fields.
    /// </summary>
    public class PredefinedFields
    {
        [JsonPropertyName("49")]
        public string? SenderCompID { get; set; }

        [JsonPropertyName("50")]
        public string? SenderSubID { get; set; }

        [JsonPropertyName("56")]
        public string? TargetCompID { get; set; }

        [JsonPropertyName("57")]
        public string? TargetSubID { get; set; }

        [JsonPropertyName("115")]
        public string? OnBehalfOfCompID { get; set; }

        [JsonPropertyName("128")]
        public string? DeliverToCompID { get; set; }

        [JsonPropertyName("1")]
        public string? Account { get; set; }

        // Extendable for any additional FIX header fields as needed
    }
}
