using System.Reflection;
using FixFlow.TradeAllocBridge.CLI;
using FixFlow.TradeAllocBridge.Core.Config;

namespace FixFlow.TradeAllocBridge.Tests.Service;

public class ProgramConfigurationTests
{
    [Fact]
    public void ResolveSenderCompId_PrefersMappingPredefinedValue()
    {
        var mapping = new MappingConfig
        {
            ClientId = "PEACE",
            Predefined = new PredefinedFields
            {
                SenderCompID = "SENDER-A"
            }
        };
        var fixConfig = new FixConfig
        {
            SenderCompId = "GLOBAL-SENDER"
        };

        var actual = InvokePrivateStatic<string>("ResolveSenderCompId", mapping, fixConfig);

        Assert.Equal("SENDER-A", actual);
    }

    [Fact]
    public void ParseIntervalSeconds_ReturnsDefaultForInvalidInput()
    {
        var actual = InvokePrivateStatic<int>("ParseIntervalSeconds", new[] { "run", "--interval-seconds", "-5" }, 60);

        Assert.Equal(60, actual);
    }

    private static T InvokePrivateStatic<T>(string methodName, params object[] args)
    {
        var method = typeof(Program).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, args);
        Assert.NotNull(result);
        return (T)result!;
    }
}
