namespace FixFlow.TradeAllocBridge.Tests.TestSupport;

internal sealed class TemporaryWorkingDirectoryScope : IDisposable
{
    private readonly string _originalDirectory;
    private readonly string _temporaryDirectory;

    public TemporaryWorkingDirectoryScope()
    {
        _originalDirectory = Directory.GetCurrentDirectory();
        _temporaryDirectory = Directory.CreateTempSubdirectory().FullName;
        Directory.SetCurrentDirectory(_temporaryDirectory);
    }

    public string Path => _temporaryDirectory;

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDirectory);
        Directory.Delete(_temporaryDirectory, recursive: true);
    }
}
