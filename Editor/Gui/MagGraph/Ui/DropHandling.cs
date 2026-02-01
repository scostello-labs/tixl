#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.IO;
using ImGuiNET;
using T3.Core.Model;
using T3.Core.Operator;
using T3.Core.Operator.Slots;
using T3.Core.Resource;
using T3.Core.Resource.Assets;
using T3.Editor.Gui.MagGraph.Model;
using T3.Editor.Gui.MagGraph.States;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Gui.Windows.AssetLib;
using T3.Editor.UiModel;
using T3.Editor.UiModel.Helpers;
using T3.Editor.UiModel.InputsAndTypes;
using T3.Editor.UiModel.Modification;
using T3.Editor.UiModel.ProjectHandling;

namespace T3.Editor.Gui.MagGraph.Ui;

/// <summary>
/// Handles dropping items onto graph. 
/// </summary>
internal static class DropHandling
{
    internal static void HandleDropOnWindow(GraphUiContext context)
    {
        if (!DragAndDropHandling.IsDragging)
            return;

        ImGui.SetCursorPos(Vector2.Zero);
        ImGui.InvisibleButton("## drop", ImGui.GetWindowSize());

        if (HandleDropSymbol(context))
            return;

        if (HandleDropExternalFile(context))
            return;

        HandleDropAsset(context);
    }

    private static void HandleDropAsset(GraphUiContext context)
    {
        DragAndDropHandling.TryHandleDropOnItem(DragAndDropHandling.DragTypes.FileAsset, out var address, out var assetResult);

        if (assetResult != DragAndDropHandling.DragInteractionResult.Hovering
            && assetResult != DragAndDropHandling.DragInteractionResult.Dropped
            || address == null) return;

        if (!AssetRegistry.TryGetAsset(address, out var asset))
        {
            Log.Warning($"Can't get asset for {address}");
            return;
        }

        // if (assetType == null)
        // {
        //     Log.Warning($"{address} has no asset type");
        //     return;
        // }

        if (assetResult == DragAndDropHandling.DragInteractionResult.Hovering)
        {
            DrawDropPreviewItem(asset);
        }
        else
        {
            CreateAssetOperatorOnGraph(context, asset, Vector2.Zero);
        }
    }

    private static bool HandleDropSymbol(GraphUiContext context)
    {
        DragAndDropHandling.TryHandleDropOnItem(DragAndDropHandling.DragTypes.Symbol, out var data, out var result);

        if (result != DragAndDropHandling.DragInteractionResult.Dropped)
            return false;

        if (!Guid.TryParse(data, out var symbolId))
        {
            Log.Warning("Invalid data format for drop? " + data);
            return true;
        }

        TryCreateSymbolInstanceOnGraph(context, symbolId, Vector2.Zero, out _);
        return false;
    }

    private static bool HandleDropExternalFile(GraphUiContext context)
    {
        SymbolPackage? package = context.CompositionInstance.Symbol.SymbolPackage;
        if (package == null)
            return false;
        
        DragAndDropHandling.TryHandleDropOnItem(DragAndDropHandling.DragTypes.ExternalFile, out var data, out var result);
        
        var packageResourcesFolder = package.AssetsFolder;

        if (result == DragAndDropHandling.DragInteractionResult.Hovering)
        {
            var dl = ImGui.GetForegroundDrawList();
            ReadOnlySpan<char> label = $"""
                                        Import files to...
                                        {packageResourcesFolder}
                                        """;
            var labelSize = ImGui.CalcTextSize(label);
            var mousePos = ImGui.GetMousePos() + new Vector2(-30, -40);
            var area = ImRect.RectWithSize(mousePos, labelSize);
            area.Expand(10);
            dl.AddRectFilled(area.Min, area.Max, UiColors.BackgroundFull.Fade(0.7f), 5);

            dl.AddText(mousePos, UiColors.ForegroundFull, label);
            return true;
        }

        if (result != DragAndDropHandling.DragInteractionResult.Dropped
            || data == null) 
            return false;
        
        var filePaths = data.Split("|");

        var dropOffset = Vector2.Zero;

        foreach (var filepath in filePaths)
        {
            if (!TryImportDroppedFile(filepath, package, out var asset)) 
                continue;

//            ResourceFileWatcher.FileStateChangeCounter++;
            
            if (!CreateAssetOperatorOnGraph(context, asset, dropOffset))
                return false;
            
            dropOffset += new Vector2(20, 100);
        }

        return false;
    }

    
    /// <summary>
    /// 
    /// </summary>
    private static bool TryImportDroppedFile(string sourcePath, SymbolPackage package, [NotNullWhen(true)]out Asset? asset)
    {
        asset = null;
        if (!Path.Exists(sourcePath))
            return false;
        
        var fileName = Path.GetFileName(sourcePath);

        if (!AssetType.TryGetForFilePath(sourcePath, out var assetType, out _))
        {
            Log.Warning("Can't find this asset type.");
            return false;
        }
        
        // Check for existing copies...
        //var alreadyHasWithName = AssetRegistry.TryGetAssetsForFilename(fileName, out var existingAssets);
        // Todo: check for asset type target folders
        
        var destFilepath = Path.Combine(package.AssetsFolder, fileName);
        
        if (!File.Exists(destFilepath))
        {
            // Copy to project first...
            //var fileName = Path.GetFileName(filepath);
            //var destFileName = Path.Combine(packageResourcesFolder, fileName);
            try
            {
                File.Copy(sourcePath, destFilepath);
            }
            catch (Exception)
            {
                Log.Warning($"Failed to copy to {destFilepath}");
                return false;
            }

            Log.Debug($"Copied {fileName} to {package.AssetsFolder}");
            
            FileInfo? destFileInfo;
            try
            {
                destFileInfo = new FileInfo(destFilepath);
            }
            catch (Exception e)
            {
                Log.Warning($"Failed to get fileinfo after dropping to {destFilepath} " + e.Message);
                return false;
            }
            
            asset=AssetRegistry.RegisterPackageEntry(destFileInfo, package, false);
            return true;
        }
        
        
        // else
        // {
        //     Log.Debug("Already project asset: " + sourcePath);
        // }

        if (AssetRegistry.TryToGetAssetFromFilepath(destFilepath, out asset)) 
            return true;
        
        Log.Warning($"Existing file not registered as asset? {destFilepath}");
        return false;

        // if (!AssetRegistry.TryConstructAddressFromFilePath(destFilepath, composition, out var address, out var resourcePackage))
        // {
        //     Log.Warning($"Can't construct address for {destFilepath}");
        //     return false;
        // }
        //
        // if (string.IsNullOrEmpty(package.Name))
        // {
        //     Log.Warning("Can't drop into unnamed package?");
        //     return false;
        // }
        
    }

    private static void DrawDropPreviewItem(Asset asset)
    {
        // if (asset.AssetType == null)
        //     return;

        if (asset.AssetType.PrimaryOperators.Count == 0)
            return;

        if (!SymbolUiRegistry.TryGetSymbolUi(asset.AssetType.PrimaryOperators[0], out var mainSymbolUi))
        {
            return;
        }

        var color = mainSymbolUi.Symbol.OutputDefinitions.Count > 0
                        ? TypeUiRegistry.GetPropertiesForType(mainSymbolUi.Symbol.OutputDefinitions[0]?.ValueType).Color
                        : UiColors.Gray;

        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetMousePos();
        dl.AddRectFilled(pos, pos + MagGraphItem.GridSize, color, 4);
    }

    private static bool CreateAssetOperatorOnGraph(GraphUiContext context,
                                            Asset asset, 
                                            Vector2 dropOffset)
    {
        if (asset.AssetType.PrimaryOperators.Count == 0)
        {
            Log.Warning($"{asset.Address} of type {asset.AssetType} has no matching operator symbols");
            return false;
        }

        if (!TryCreateSymbolInstanceOnGraph(context, asset.AssetType.PrimaryOperators[0], dropOffset, out var newInstance))
        {
            Log.Warning("Failed to create operator instance");
            return false;
        }

        if (!SymbolAnalysis.TryGetFileInputFromInstance(newInstance, out var stringInput, out _))
        {
            Log.Warning("Failed to get file path parameter from op");
            return false;
        }

        Log.Debug($"Created {newInstance} with {asset.Address}", newInstance);

        stringInput.TypedInputValue.Assign(new InputValue<string>(asset.Address));
        stringInput.DirtyFlag.ForceInvalidate();
        stringInput.Parent.Parent?.Symbol.InvalidateInputInAllChildInstances(stringInput);
        stringInput.Input.IsDefault = false;

        AssetRegistry.AddAssetReference(asset,
                                        symbolId: stringInput.Parent.Symbol.Id,
                                        stringInput.Parent.SymbolChildId,
                                        stringInput.Id);
        
        return true;
    }

    private static bool TryCreateSymbolInstanceOnGraph(GraphUiContext context, 
                                                       Guid guid, 
                                                       Vector2 offsetInScreen, 
                                                       [NotNullWhen(true)] out Instance? newInstance)
    {
        newInstance = null;
        if (SymbolUiRegistry.TryGetSymbolUi(guid, out var symbolUi))
        {
            var symbol = symbolUi.Symbol;
            var posOnCanvas = context.View.InverseTransformPositionFloat(ImGui.GetMousePos() + offsetInScreen);
            if (!SymbolUiRegistry.TryGetSymbolUi(context.CompositionInstance.Symbol.Id, out var compositionOpSymbolUi))
            {
                Log.Warning("Failed to get symbol id for " + context.CompositionInstance.SymbolChildId);
                return false;
            }

            var childUi = GraphOperations.AddSymbolChild(symbol, compositionOpSymbolUi, posOnCanvas);
            newInstance = context.CompositionInstance.Children[childUi.Id];
            context.Selector.SetSelection(childUi, newInstance);
            context.Layout.FlagStructureAsChanged();
            return true;
        }

        Log.Warning($"Symbol {guid} not found in registry");
        return false;
    }
}