using System.IO;
using T3.Core.Model;
using T3.Core.Operator;
using T3.Core.Operator.Slots;
using T3.Core.Resource;
using T3.Core.UserData;
using T3.Core.Utils;
using T3.Editor.Gui.InputUi.SimpleInputUis;
using T3.Editor.UiModel;
using T3.Editor.UiModel.InputsAndTypes;

namespace T3.Editor.Gui.Interaction.StartupCheck;

/// <summary>
/// Validates and updates the asset-paths of all loaded symbols 
/// </summary>
/// <remarks>
///
///        Sadly, for legacy projects, this could be...
///         either a project a subfolder in the t3 `Resources/`:
///              Old: Resources\fonts\Roboto-Black.fnt
///               in: .\tixl\Resources\fonts\Roboto-Black.fnt  
///              New: tixl.lib:fonts/Roboto-Black.fnt
///               in: .\tixl\Operators\Lib\Assets\fonts\Roboto-Black.fnt
///                        
///         a local package sub folder:
///              Old: Resources/images/basic/white-pixel.png 
///               in .\tixl\Operators\Lib\Resources\images\basic\white-pixel.png 
///              New: tixl.lib:images/basic/white-pixel.png
///               in .\tixl\Operators\Lib\Assets\images\basic\white-pixel.png 
///         
///         Or a package(!) with shared resource:
///              Old: Resources/lib/img/fx/Default2-vs.hlsl 
///               in .\tixl\Operators\Lib\Resources\img\fx\Default2-vs.hlsl 
///              New: tixl.lib:img/fx/Default2-vs.hlsl
///               in .\tixl\Operators\Lib\Assets\shaders\img\fx\Default2-vs.hlsl 
///
/// </remarks>
internal static class ConformAssetPaths
{
    internal static void ConformAllPaths()
    {
        BuildAssetIndex();

        foreach (var package in SymbolPackage.AllPackages)
        {
            foreach (var symbol in package.Symbols.Values)
            {
                // Symbol Defaults
                SymbolUi symbolUi = null;
                foreach (var inputDef in symbol.InputDefinitions)
                {
                    if (inputDef.ValueType != typeof(string))
                        continue;

                    symbolUi ??= symbol.GetSymbolUi();
                    if (!symbolUi.InputUis.TryGetValue(inputDef.Id, out var inputUi))
                        continue;

                    if (inputDef.DefaultValue is not InputValue<string> stringValue)
                        continue;

                    ProcessStringInputUi(inputUi, stringValue, symbol);
                }

                // Symbol children
                foreach (var child in symbol.Children.Values)
                {
                    foreach (var input in child.Inputs.Values)
                    {
                        if (input.IsDefault)
                            continue;

                        if (input.InputDefinition.ValueType != typeof(string))
                            continue;

                        if (input.Value is not InputValue<string> stringValue)
                            continue;

                        if (string.IsNullOrEmpty(stringValue.Value))
                            continue;

                        if (!SymbolUiRegistry.TryGetSymbolUi(child.Symbol.Id, out var childSymbolUi))
                            continue;

                        if (!childSymbolUi.InputUis.TryGetValue(input.Id, out var inputUi))
                            continue;

                        ProcessStringInputUi(inputUi, stringValue, symbol, child);
                    }
                }
            }
        }
    }

    private static void ProcessStringInputUi(IInputUi inputUi, InputValue<string> stringValue, Symbol symbol,
                                             Symbol.Child symbolChild = null)
    {
        if (inputUi is not StringInputUi stringUi)
            return;

        switch (stringUi.Usage)
        {
            case StringInputUi.UsageType.FilePath:
            {
                if (TryConvertResourcePathFuzzy(stringValue.Value, symbol, out var converted))
                {
                    Log.Debug($"{symbol.SymbolPackage.Name}: {stringValue.Value} -> {converted}");
                    stringValue.Value = converted;
                }

                break;
            }
            case StringInputUi.UsageType.DirectoryPath:
                if (TryConvertResourceFolderPath(stringValue.Value, symbol, out var convertedFolderPath))
                {
                    {
                        Log.Debug($"{symbol}.{inputUi.InputDefinition.Name} Folder:  {symbol.SymbolPackage.Name}: {stringValue.Value} -> {convertedFolderPath}");
                    }
                    stringValue.Value = convertedFolderPath;
                }

                if (!ResourceManager.TryResolveUri(stringValue.Value, null, out var absolutePath, out _, isFolder: true))
                {
                    if (symbolChild == null)
                    {
                        Log.Warning($"Dir not found for default of: {symbol}.{inputUi.InputDefinition.Name}:  {stringValue.Value} => '{absolutePath}'");
                    }
                    else
                    {
                        Log.Warning($"Dir not found in: {symbolChild.Parent} / {symbol.Name}.{inputUi.InputDefinition.Name}: {stringValue.Value} => '{absolutePath}'");
                    }
                }

                return;
        }
    }

    

    /// <summary>
    /// Sadly, we can't use Path.IsPathRooted() because we the legacy filepaths also starts with "/"
    /// So we're testing for windows paths likes c: 
    /// </summary>
    /// 
    private static bool IsAbsoluteFilePath(string path)
    {
        var colon = path.IndexOf(':');
        return colon == 1;
    }

    private static bool TryConvertResourceFolderPath(string path, Symbol symbol, out string newPath)
    {
        path = path.Replace('\\', '/');
        newPath = path;

        if (string.IsNullOrWhiteSpace(path)) return false;

        var colon = path.IndexOf(':');
        if (colon != -1)
        {
            if (colon <= 1)
                return false;

            return false;
        }

        if (!path.EndsWith("/"))
        {
            path += '/';
        }

        var pathSpan = path.AsSpan();

        var firstSlash = pathSpan.IndexOf('/');
        if (firstSlash == -1)
        {
            newPath = $"{symbol.SymbolPackage.Name}:{newPath}";
            return true;
        }

        var isRooted = firstSlash == 0;
        var nonRooted = isRooted ? pathSpan[1..] : pathSpan;

        // Skip "Resources" prefix
        var resourcesPrefix = "Resources/";
        if (nonRooted.StartsWith(resourcesPrefix))
        {
            nonRooted = nonRooted[resourcesPrefix.Length..];
        }

        //absolutePath = $"{symbol.SymbolPackage.ResourcesFolder}/{nonRooted}";
        newPath = $"{symbol.SymbolPackage.Name}:{nonRooted}";

        return true;
    }

    private static bool TryConvertResourcePathFuzzy(string path, Symbol symbol, out string newPath)
    {
        newPath = path;

        if (string.IsNullOrWhiteSpace(path)) return false;

        // 1. Ignore valid URIs and Absolute Paths
        if (path.Contains(':') || IsAbsoluteFilePath(path))
            return false;

        // 2. Extract the filename from the legacy path
        var legacyFileName = Path.GetFileName(path);

        // 3. Heal the path using the index
        if (_filenameToAddressCache.TryGetValue(legacyFileName, out var healedAddress))
        {
            newPath = healedAddress;
            return true;
        }

        // 4. Fallback: If not found in index, force it into the current package
        var conformed = path.Replace("\\", "/").TrimStart('/');
        newPath = $"{symbol.SymbolPackage.Name}:{conformed}";
        Log.Warning($"Missing assets {symbol} : {legacyFileName}. Try {newPath}");

        return !string.Equals(newPath, path, StringComparison.Ordinal);
    }

    private static void BuildAssetIndex()
    {
        _filenameToAddressCache.Clear();

        foreach (var package in SymbolPackage.AllPackages)
        {
            // Use the absolute path provided by the package
            var root = package.ResourcesFolder; // Future: .AssetsFolder
            if (!Directory.Exists(root)) continue;

            var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                if (FileLocations.IgnoredFiles.Contains(fileName))
                    continue;

                var relativePath = Path.GetRelativePath(root, file).Replace("\\", "/");
                var address = $"{package.Name}:{relativePath}";

                // If filenames are unique, we just store it. 
                // If not, we might need to store a list or prioritize.
                if (!_filenameToAddressCache.TryAdd(fileName, address))
                {
                    Log.Warning($"{fileName} already exists in {_filenameToAddressCache[fileName]}");
                }
            }
        }
    }

    private static readonly Dictionary<string, string> _filenameToAddressCache = new(StringComparer.OrdinalIgnoreCase);
}