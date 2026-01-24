using System.IO;
using System.Text;
using T3.Core.Model;
using T3.Core.Operator;
using T3.Core.Operator.Slots;
using T3.Core.Resource;
using T3.Core.Resource.Assets;
using T3.Core.UserData;
using T3.Core.Utils;
using T3.Editor.Gui.InputUi.SimpleInputUis;
using T3.Editor.UiModel;
using T3.Editor.UiModel.InputsAndTypes;

namespace T3.Editor.Gui.Interaction.StartupCheck;

internal static class ConformAssetPaths
{
    /// <summary>
    /// Should be called before loading assets
    /// </summary>
    public static void RenameResourcesToAssets(SymbolPackage package)
    {
        var oldPath = Path.Combine(package.Folder, FileLocations.LegacyResourcesSubfolder);
        var newPath = Path.Combine(package.Folder, FileLocations.AssetsSubfolder);

        if (!Directory.Exists(oldPath))
            return;

        try
        {
            // 1. Physical Move/Merge
            if (Directory.Exists(newPath))
            {
                MoveFilesRecursively(oldPath, newPath);
                Directory.Delete(oldPath, true);
            }
            else
            {
                Directory.Move(oldPath, newPath);
            }

            // 2. Patch the .csproj file
            UpdateCsprojFile(package.Folder);
            Log.Info($"Migrated {package.Name}: Resources -> Assets (Folder and Project updated)");
        }
        catch (Exception e)
        {
            Log.Error($"Migration failed for {package.Name}: {e.Message}");
        }
    }

    private static void MoveFilesRecursively(string sourcePath, string targetPath)
    {
        var sourceDi = new DirectoryInfo(sourcePath);
        Directory.CreateDirectory(targetPath);

        foreach (var file in sourceDi.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, file.FullName);
            var targetFile = Path.Combine(targetPath, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        
            // Use true to overwrite if a file with the same name exists in the target
            file.MoveTo(targetFile, true); 
        }
    }
    
    private static void UpdateCsprojFile(string packageFolder)
    {
        var projectFiles = Directory.GetFiles(packageFolder, "*.csproj");
        foreach (var projFile in projectFiles)
        {
            var content = File.ReadAllText(projFile);
        
            // This targets both the Include and Link attributes in your ItemGroup
            var updatedContent = content
                                .Replace("Include=\"Resources/", "Include=\"Assets/")
                                .Replace("Include=\"Resources\\", "Include=\"Assets\\")
                                .Replace("<Link>Resources/", "<Link>Assets/");

            if (content != updatedContent)
            {
                File.WriteAllText(projFile, updatedContent, Encoding.UTF8);
                Log.Debug($"Updated project file: {Path.GetFileName(projFile)}");
            }
        }
    }
    
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
    internal static void ConformAllPaths()
    {
        //BuildAssetIndex();

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
                    Log.Debug($"{symbol}.{inputUi.InputDefinition.Name} Folder:  {symbol.SymbolPackage.Name}: {stringValue.Value} -> {convertedFolderPath}");
                    stringValue.Value = convertedFolderPath;
                }

                if (!AssetRegistry.TryResolveUri(stringValue.Value, null, out var absolutePath, out _, isFolder: true))
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
        
        // Skip package name
        if (nonRooted.StartsWith(symbol.SymbolPackage.Name + "/"))
        {
            nonRooted = nonRooted[(symbol.SymbolPackage.Name.Length + 1)..];
        }
        

        //absolutePath = $"{symbol.SymbolPackage.ResourcesFolder}/{nonRooted}";
        newPath = $"{symbol.SymbolPackage.Name}:{nonRooted}";

        return true;
    }

    private static bool TryConvertResourcePathFuzzy(string path, Symbol symbol, out string newPath)
    {
        newPath = path;
        if (string.IsNullOrWhiteSpace(path) || IsAbsoluteFilePath(path))
            return false;

        // Check if already valid
        if (path.Contains(':') && AssetRegistry.TryGetAsset(path, out _))
            return false;

        //var fileName = ;

        var fileName = string.Empty;
        var invalidFormat = path.Count(c => c == AssetRegistry.PackageSeparator) > 1;
        if (invalidFormat)
        {
            var lastAddressIndex = path.LastIndexOfAny([':','/']);
            if(lastAddressIndex < path.Length-1)
                fileName = path[(lastAddressIndex+1)..];
        }
        else
        {
            fileName= Path.GetFileName(path);
        }
        

        // 1. Use the pre-built global index from the registry
        if (AssetRegistry.TryHealPath(fileName, out var healedAddress))
        {
            newPath = healedAddress;
            return true;
        }

        // 2. Fallback: Force into current package
        var conformed = path.Replace("\\", "/").TrimStart('/');
        
        // Strip the legacy "Resources/" prefix if it exists in the string
        const string legacyPrefix = "Resources/";
        if (conformed.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            conformed = conformed[legacyPrefix.Length..];
            newPath = $"{symbol.SymbolPackage.Name}:{conformed}";
            return true;
        }

        return false;
    }


    private static readonly Dictionary<string, string> _filenameToAddressCache = new(StringComparer.OrdinalIgnoreCase);
}