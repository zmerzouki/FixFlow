using System.Text;

namespace FixFlow.TradeAllocBridge.Tests.TestSupport;

internal sealed class TestFileScope : IDisposable
{
    private readonly string _path;
    private readonly byte[]? _originalBytes;
    private readonly bool _originalExists;

    public TestFileScope(string path)
    {
        _path = path;
        _originalExists = File.Exists(path);
        _originalBytes = _originalExists ? File.ReadAllBytes(path) : null;
    }

    public string Path => _path;

    public void WriteAllText(string content)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_path, content, Encoding.UTF8);
    }

    public void Dispose()
    {
        if (_originalExists)
        {
            File.WriteAllBytes(_path, _originalBytes!);
        }
        else if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
