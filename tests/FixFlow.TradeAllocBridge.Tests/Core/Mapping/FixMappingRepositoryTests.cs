using FixFlow.TradeAllocBridge.Core.Mapping;
using Microsoft.Extensions.Logging.Abstractions;

namespace FixFlow.TradeAllocBridge.Tests.Core.Mapping;

public class FixMappingRepositoryTests
{
    [Fact]
    public void GetAll_ReturnsValidMappings_AndSkipsInvalidFiles()
    {
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            File.WriteAllText(System.IO.Path.Combine(tempDir.FullName, "PEACE_map.json"), """
            {
              "clientId": "PEACE",
              "fieldMap": {
                "Symbol": "55"
              }
            }
            """);

            File.WriteAllText(System.IO.Path.Combine(tempDir.FullName, "BROKEN_map.json"), "{ invalid json");

            var repository = new FixMappingRepository(tempDir.FullName, NullLogger<FixMappingRepository>.Instance);

            var mappings = repository.GetAll().ToList();

            var mapping = Assert.Single(mappings);
            Assert.Equal("PEACE", mapping.ClientId);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetByClientId_ReturnsNullForUnknownClient()
    {
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            var repository = new FixMappingRepository(tempDir.FullName, NullLogger<FixMappingRepository>.Instance);

            var mapping = repository.GetByClientId("UNKNOWN");

            Assert.Null(mapping);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
