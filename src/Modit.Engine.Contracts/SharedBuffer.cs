using System.IO.MemoryMappedFiles;

namespace Modit.Engine;

/// <summary>
/// A named, pagefile-backed shared memory region used to move pixel data between the UI/CLI process
/// and an engine host without copying it through the RPC pipe. The requesting side creates it and
/// passes <see cref="Name"/> in the request; the host opens it by name and writes into it.
/// </summary>
public sealed unsafe class SharedBuffer : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _pointer;

    private SharedBuffer(MemoryMappedFile file, string name, long capacity)
    {
        _file = file;
        Name = name;
        Capacity = capacity;
        _view = file.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
    }

    public string Name { get; }

    public long Capacity { get; }

    public byte* Data => _pointer;

    public Span<byte> Span => new(_pointer, checked((int)Capacity));

    public static SharedBuffer Create(long capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        string name = $"Local\\modit-buf-{Guid.NewGuid():N}";
        MemoryMappedFile file = MemoryMappedFile.CreateNew(name, capacity, MemoryMappedFileAccess.ReadWrite);
        return new SharedBuffer(file, name, capacity);
    }

    public static SharedBuffer Open(string name, long capacity)
    {
        MemoryMappedFile file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
        return new SharedBuffer(file, name, capacity);
    }

    public void Dispose()
    {
        if (_pointer != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }

        _view.Dispose();
        _file.Dispose();
    }
}
