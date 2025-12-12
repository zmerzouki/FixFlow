using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RJF.TradeAllocBridge.Core.Config
{
    /// <summary>
    /// Represents a single client's FIX mapping configuration.
    /// Supports flat tag mappings, predefined FIX header fields,
    /// and optional repeating group definitions.
    /// </summary>
    public class MappingConfig
    {
        /// <summary>
        /// Logical client identifier (e.g., "RAYMONDJAMES").
        /// </summary>
        [JsonPropertyName("clientId")]
        public string? ClientId { get; set; }

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
        /// Optional predefined FIX header fields for this client (SenderCompID, TargetCompID, etc.).
        /// </summary>
        [JsonPropertyName("predefined")]
        public PredefinedFields? Predefined { get; set; }

        /// <summary>
        /// Optional repeating group definitions.
        /// Example:
        /// {
        ///   "NoOrders": { "11": "ClOrdID" },
        ///   "NoAllocs": { "79": "AllocAccount", "80": "AllocQty", "153": "AllocAvgPx" }
        /// }
        /// </summary>
        [JsonPropertyName("repeatingGroups")]
        public Dictionary<string, Dictionary<string, string>>? RepeatingGroups { get; set; }

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
            config.RepeatingGroups ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

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
