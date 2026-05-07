using FixFlow.TradeAllocBridge.Core.Config;

namespace FixFlow.TradeAllocBridge.Tests.Core.Config;

public class MappingConfigTests
{
    [Fact]
    public void Load_NormalizesDelimiterAndFieldDefaultValues()
    {
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}_map.json");

        try
        {
            File.WriteAllText(tempPath, """
            {
              "clientId": "PEACE",
              "delimiter": " ",
              "tradeAllocations": {
                " Quantity ": "80"
              },
              "defaultTagValues": {
                "54": "1"
              },
              "fieldDefaultTagValues": {
                " Quantity ": {
                  " 54 ": " 1 ",
                  "79": "",
                  "": "skip"
                },
                "": {
                  "55": "MSFT"
                }
              }
            }
            """);

            var config = MappingConfig.Load(tempPath);

            Assert.Equal(";", config.Delimiter);
            Assert.True(config.TradeAllocations.ContainsKey(" Quantity "));
            Assert.True(config.FieldDefaultTagValues.ContainsKey("Quantity"));
            Assert.Equal("1", config.FieldDefaultTagValues["Quantity"]["54"]);
            Assert.DoesNotContain("79", config.FieldDefaultTagValues["Quantity"].Keys);
            Assert.DoesNotContain(string.Empty, config.FieldDefaultTagValues.Keys);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
