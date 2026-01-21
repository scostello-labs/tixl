#nullable enable
using System.Collections.Generic;
using T3.Core.Utils;

namespace T3.Core.Resource;

/// <summary>
/// File handler and GPU resource generator. 
/// </summary>
/// Todo: Should probably be split into multiple classes
public static partial class ResourceManager
{

    static ResourceManager()
    {
    }

    internal static void AddSharedResourceFolder(IResourcePackage resourcePackage, bool allowSharedNonCodeFiles)
    {
        if (allowSharedNonCodeFiles)
            _sharedResourcePackages.Add(resourcePackage);

        _shaderPackages.Add(resourcePackage);
        resourcePackage.ResourcesFolder.ToForwardSlashesUnsafe();
    }

    internal static void RemoveSharedResourceFolder(IResourcePackage resourcePackage)
    {
        _shaderPackages.Remove(resourcePackage);
        _sharedResourcePackages.Remove(resourcePackage);
    }

    public static IReadOnlyList<IResourcePackage> SharedShaderPackages => _shaderPackages;
    private static readonly List<IResourcePackage> _sharedResourcePackages = new(4);
    private static readonly List<IResourcePackage> _shaderPackages = new(4);

    public enum PathMode
    {
        PackageUri, // Always prepend packageName
        Absolute, // Absolute but conformed to forward slashes
    }

    public static void RaiseFileWatchingEvents()
    {
        // dispatched to main thread
        lock (_fileWatchers)
        {
            foreach (var fileWatcher in _fileWatchers)
            {
                fileWatcher.RaiseQueuedFileChanges();
            }
        }
    }

    internal static void UnregisterWatcher(ResourceFileWatcher resourceFileWatcher)
    {
        lock (_fileWatchers)
            _fileWatchers.Remove(resourceFileWatcher);
    }

    internal static void RegisterWatcher(ResourceFileWatcher resourceFileWatcher)
    {
        lock (_fileWatchers)
            _fileWatchers.Add(resourceFileWatcher);
    }

    private static readonly List<ResourceFileWatcher> _fileWatchers = [];
}