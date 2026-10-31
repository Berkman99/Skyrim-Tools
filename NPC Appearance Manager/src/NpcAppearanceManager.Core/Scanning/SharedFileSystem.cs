using System.IO.Abstractions;

namespace NpcAppearanceManager.Core.Scanning;

/// <summary>
/// How NAM opens plugins for reading: other programs may rename, replace or delete them meanwhile. NAM keeps plugins
/// open while it runs, and Vortex deploys by replacing the files in Data; without this it refuses to deploy until NAM
/// is closed. A plugin replaced this way reads on as it was when opened.
/// </summary>
public sealed class SharedFileSystem : FileSystem
{
    public static SharedFileSystem Instance { get; } = new();

    private SharedFileSystem() => File = new SharedFile(this);

    public override IFile File { get; }

    private sealed class SharedFile(IFileSystem fileSystem) : FileWrapper(fileSystem)
    {
        public override FileSystemStream OpenRead(string path) => Shared(path);

        public override FileSystemStream Open(string path, FileMode mode, FileAccess access, FileShare share) =>
            access == FileAccess.Read && mode == FileMode.Open ? Shared(path) : base.Open(path, mode, access, share);

        public override FileSystemStream Open(string path, FileMode mode, FileAccess access) =>
            access == FileAccess.Read && mode == FileMode.Open ? Shared(path) : base.Open(path, mode, access);
    }

    private static FileSystemStream Shared(string path) =>
        new Stream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), path);

    private sealed class Stream(FileStream stream, string path) : FileSystemStream(stream, path, false);
}
