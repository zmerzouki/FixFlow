using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RJF.TradeAllocBridge.Core.Mapping
{
    /// <summary>
    /// Represents a FIX transformation map for a specific client.
    /// </summary>
    public class FixMapping
    {
        /// <summary>Unique client identifier (maps to SenderCompID / Tag 49).</summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>DNS domain of the email sender (e.g., 'brokerabc.com').</summary>
        public string? SenderDomain { get; set; }

        /// <summary>Optional TargetSubID (Tag 57).</summary>
        public string? TargetSubID { get; set; }

        /// <summary>Optional OnBehalfOfCompID (Tag 115).</summary>
        public string? OnBehalfOfCompID { get; set; }

        /// <summary>Optional DeliverToCompID (Tag 128).</summary>
        public string? DeliverToCompID { get; set; }

        /// <summary>Mapping of spreadsheet column names to FIX tags (e.g. "Trade Date" → "75").</summary>
        public Dictionary<string, string> FieldMap { get; set; } = new();

        /// <summary>Load a FixMapping from a JSON file.</summary>
        public static FixMapping Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));

            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var mapping = JsonSerializer.Deserialize<FixMapping>(json, options)
                          ?? throw new InvalidDataException($"File '{path}' does not contain a valid FixMapping object.");

            // Basic validation
            if (string.IsNullOrWhiteSpace(mapping.ClientId))
                throw new InvalidDataException($"FixMapping in '{path}' is missing required property '{nameof(ClientId)}'.");

            if (mapping.FieldMap == null || mapping.FieldMap.Count == 0)
                mapping.FieldMap = new Dictionary<string, string>();

            return mapping;
        }
    }
}
