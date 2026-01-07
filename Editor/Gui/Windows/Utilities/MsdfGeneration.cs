#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Msdfgen;
using MsdfAtlasGen;
using T3.Core.Logging;
using T3.Core.Model;
using T3.Editor.Gui.Input;
using T3.Editor.UiModel.ProjectHandling;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using Msdfgen.Extensions;
using System.Threading.Tasks;
using T3.Editor.UiModel;
using T3.Core.SystemUi;

namespace T3.Editor.Gui.Windows.Utilities
{
    public static class MsdfGeneration
    {
        private static bool _useRecommended = true;

        private enum ColoringStrategy
        {
            Simple,
            InkTrap
        }
        
        private enum ErrorCorrectionMode
        {
            Disabled,
            Indiscriminate,
            EdgeOnly
        }

        private static ColoringStrategy _coloringStrategy = ColoringStrategy.Simple;
        private static ErrorCorrectionMode _errorCorrection = ErrorCorrectionMode.Indiscriminate;
        private static bool _overlap = true;
        private static Vector4 _outerPadding = Vector4.Zero;

        private static float _generationProgress;
        private static string _progressText = "";
        private static bool _isGenerating;

        public static void Draw()
        {
            FormInputs.SetIndent(50);

            FormInputs.AddSectionHeader("MSDF Generation");
            CustomComponents.HelpText("Generate MSDF fonts from .ttf files using MSDF-Sharp.\nChecks for 'Resources/fonts'.");
            FormInputs.AddVerticalSpace();
            
            if (_isGenerating)
            {
                 ImGui.BeginDisabled();
            }
            
            FormInputs.AddFilePicker("Font File", ref _fontFilePath, null, null, "Select .ttf file", FileOperations.FilePickerTypes.File);

            FormInputs.AddCheckBox("Use Recommended Settings", ref _useRecommended);

            if (!_useRecommended)
            {
                FormInputs.SetIndent(120);
                FormInputs.AddFloat("Size", ref _fontSize, 1, 500, 1);
                FormInputs.AddInt("Width", ref _width, 128, 4096, 128);
                FormInputs.AddInt("Height", ref _height, 128, 4096, 128);
                FormInputs.AddFloat("Miter Limit", ref _miterLimit, 0, 10, 0.1f);
                FormInputs.AddInt("Spacing", ref _spacing, 0, 32, 1);
                FormInputs.AddFloat("Range", ref _rangeValue, 0.1f, 10, 0.1f);
                FormInputs.AddFloat("Angle Threshold", ref _angleThreshold, 0, 6, 0.1f);
                
                FormInputs.AddEnumDropdown(ref _coloringStrategy, "Coloring Strategy");
                FormInputs.AddEnumDropdown(ref _errorCorrection, "Error Correction");
                FormInputs.AddCheckBox("Overlap Support", ref _overlap);
                
                FormInputs.DrawFieldSetHeader("Outer Padding");
                var padding = _outerPadding;
                var changed = FormInputs.AddFloat("Top", ref padding.X, 0, 100, 1);
                changed |= FormInputs.AddFloat("Right", ref padding.Y, 0, 100, 1);
                changed |= FormInputs.AddFloat("Bottom", ref padding.Z, 0, 100, 1);
                changed |= FormInputs.AddFloat("Left", ref padding.W, 0, 100, 1);
                if (changed) _outerPadding = padding;

                FormInputs.ApplyIndent();
            }
            FormInputs.AddVerticalSpace();
            FormInputs.SetIndent(90);        
    
            var internalPackage = GetPackageContainingPath(_fontFilePath);
            SymbolPackage? usagePackage = null;

            if (internalPackage != null)
            {
                // Lock to the package containing the file
                string name = internalPackage.DisplayName;
                ImGui.BeginDisabled();
                if (FormInputs.AddStringInput("Target Project", ref name)) { }
                ImGui.EndDisabled();
                usagePackage = internalPackage;
            }
            else
            {
                // Allow selection for external files
                var editablePackages = EditableSymbolProject.AllProjects.ToList();
                
                // Initialize default selection if needed
                if (_selectedPackage == null || !editablePackages.Contains(_selectedPackage))
                {
                    // Try to use the currently valid project from the focused view
                    var focusedProject = ProjectView.Focused?.OpenedProject.Package;
                    if (focusedProject != null && !focusedProject.IsReadOnly)
                    {
                        _selectedPackage = focusedProject;
                    }
                    else
                    {
                        _selectedPackage = editablePackages.FirstOrDefault();
                    }
                }

                var packageNames = editablePackages.Select(p => p.DisplayName).OrderBy(n => n);
                string selectedName = _selectedPackage?.DisplayName ?? "";

                if (FormInputs.AddStringInputWithSuggestions("Target Project", ref selectedName, packageNames, "Select Project"))
                {
                    _selectedPackage = editablePackages.FirstOrDefault(p => p.DisplayName == selectedName);
                }
                
                // If the user typed a valid name but we missed it (e.g. didn't hit enter but clicked away, or typed perfectly), try to resolve
                if (_selectedPackage == null || _selectedPackage.DisplayName != selectedName)
                {
                     var match = editablePackages.FirstOrDefault(p => p.DisplayName == selectedName);
                     if (match != null)
                        _selectedPackage = match;
                }

                usagePackage = _selectedPackage;
                
                if (usagePackage == null)
                {
                    ImGui.Indent(150);
                    ImGui.TextColored(UiColors.StatusError, "Invalid or No Project selected.");
                    ImGui.Unindent(150);
                }
            }

            bool hasFile = !string.IsNullOrEmpty(_fontFilePath) && File.Exists(_fontFilePath);
            if (CustomComponents.DisablableButton("Generate MSDF", hasFile && usagePackage != null && !_isGenerating))
            {
                // Fire and forget, but ensure exceptions are observed and logged
                GenerateAsync(usagePackage!).ContinueWith(t =>
                {
                    if (t.Exception != null)
                    {
                        Log.Error("Unhandled exception in MSDF generation: " + t.Exception);
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
            
            if (_isGenerating)
            {
                ImGui.EndDisabled();
            }
            
            if (!hasFile || usagePackage == null)
            {
                string reason = !hasFile ? "Please select a valid .ttf font file." : "Please select a target project.";
                CustomComponents.TooltipForLastItem(reason);
            }
            
            if (_isGenerating)
            {
                FormInputs.AddVerticalSpace();
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, UiColors.StatusAutomated.Rgba);
                ImGui.ProgressBar(_generationProgress, new System.Numerics.Vector2(-1, 4), "");
                ImGui.PopStyleColor();
                ImGui.Text(_progressText);
                FormInputs.AddVerticalSpace();
            }

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                var color = _isStatusError ? UiColors.StatusError : UiColors.StatusAutomated;
                ImGui.TextColored(color, _statusMessage);
                
                if (!_isStatusError && !_isGenerating && !string.IsNullOrEmpty(_lastOutputDir))
                {
                    if (ImGui.Button("Open Output Folder"))
                    {
                         CoreUi.Instance.OpenWithDefaultApplication(_lastOutputDir);
                    }
                }
            }
        }

        public static void RefreshSelection()
        {
            _selectedPackage = null;
        }

        private static async Task GenerateAsync(SymbolPackage package)
        {
            _statusMessage = "";
            _isStatusError = false;
            _isGenerating = true;
            _generationProgress = 0;
            _progressText = "Starting generation...";

            try
            {
                if (string.IsNullOrEmpty(_fontFilePath) || !File.Exists(_fontFilePath))
                {
                    _statusMessage = "Font file not found";
                    _isStatusError = true;
                    Log.Warning("Font file not found: " + _fontFilePath);
                    return;
                }

                var settings = GetSettings();
                string outputDir = PrepareOutputDirectory(package);
                string fontName = Path.GetFileNameWithoutExtension(settings.FontPath);
                
                string imageOut = Path.Combine(outputDir, $"{fontName}_msdf.png");
                string fntOut = Path.Combine(outputDir, $"{fontName}_msdf.fnt");

                // Run heavy lifting on background thread
                await Task.Run(() =>
                {
                    using var ft = FreetypeHandle.Initialize();
                    if (ft is null)
                    {
                        throw new Exception("Failed to initialize FreeType library (MsdfGen.Extensions.FreetypeHandle.Initialize returned null).");
                    }

                    using var fontHandle = FontHandle.LoadFont(ft, settings.FontPath);
                    if (fontHandle is null)
                    {
                        throw new Exception($"Failed to load font from path '{settings.FontPath}'. Please ensure the file is a valid .ttf font.");
                    }

                    var fontGeometry = SetupFontGeometry(fontHandle, fontName, settings);
                    var glyphs = fontGeometry.GetGlyphs().Glyphs.ToArray();

                    if (!TryPackGlyphs(glyphs, settings, out int finalW, out int finalH))
                        throw new Exception("Packing failed.");

                    var progress = new Progress<GeneratorProgress>(p =>
                    {
                        _generationProgress = (float)p.Proportion;
                        _progressText = $"Generating: {p.Current}/{p.Total} ({p.GlyphName})";
                    });
                    
                    var generator = GenerateAtlas(glyphs, finalW, finalH, settings, progress);

                    SaveResults(generator, fontGeometry, settings, imageOut, fntOut, finalW, finalH);
                });

                _lastOutputDir = outputDir;
                _statusMessage = $"Success! Saved to {Path.GetFileName(imageOut)}";
                _isStatusError = false;
                Log.Debug("MSDF Generation successful!");
            }
            catch (Exception e)
            {
                _statusMessage = $"Error: {e.Message}";
                _isStatusError = true;
                Log.Error($"An error occurred during MSDF generation: {e.Message}");
                Log.Error(e.StackTrace ?? string.Empty);
            }
            finally
            {
                _isGenerating = false;
                _progressText = "";
                _generationProgress = 0;
            }
        }

        private static GenerationSettings GetSettings()
        {
            return new GenerationSettings
                       {
                           FontPath = _fontFilePath ?? string.Empty,
                           FontSize = _useRecommended ? 90.0 : (double)_fontSize,
                           Width = _useRecommended ? 1024 : _width,
                           Height = _useRecommended ? 1024 : _height,
                           MiterLimit = _useRecommended ? 3.0 : (double)_miterLimit,
                           Spacing = _useRecommended ? 2 : _spacing,
                           RangeValue = _useRecommended ? 2.0 : (double)_rangeValue,
                           AngleThreshold = _useRecommended ? 3.0 : (double)_angleThreshold,
                           Strategy = _useRecommended ? ColoringStrategy.Simple : _coloringStrategy,
                           ErrorCorrection = _useRecommended ? ErrorCorrectionMode.Indiscriminate : _errorCorrection,
                           Overlap = _useRecommended ? true : _overlap,
                            OuterPadding = _useRecommended ? new MsdfAtlasGen.Padding(0, 0, 0, 0)
                                                           : new MsdfAtlasGen.Padding((int)_outerPadding.W, (int)_outerPadding.Z, (int)_outerPadding.Y, (int)_outerPadding.X) // Left, Bottom, Right, Top
                        };
        }

        private static string PrepareOutputDirectory(SymbolPackage package)
        {
            string outputDir = Path.Combine(package.ResourcesFolder, "fonts");
            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }
            return outputDir;
        }

        private static FontGeometry SetupFontGeometry(FontHandle fontHandle, string fontName, GenerationSettings settings)
        {
            var fontGeometry = new FontGeometry();
            fontGeometry.LoadCharset(fontHandle, settings.FontSize, Charset.ASCII);
            fontGeometry.SetName(fontName);

            foreach (var glyph in fontGeometry.GetGlyphs().Glyphs)
            {
                switch (settings.Strategy)
                {
                    case ColoringStrategy.Simple:
                        glyph.EdgeColoring(Msdfgen.EdgeColoring.EdgeColoringSimple, settings.AngleThreshold, 0);
                        break;
                    case ColoringStrategy.InkTrap:
                        glyph.EdgeColoring(Msdfgen.EdgeColoring.EdgeColoringInkTrap, settings.AngleThreshold, 0);
                        break;
                }
            }

            return fontGeometry;
        }

        private static bool TryPackGlyphs(GlyphGeometry[] glyphs, GenerationSettings settings, out int finalW, out int finalH)
        {
            var packer = new TightAtlasPacker();
            packer.SetDimensions(settings.Width, settings.Height);
            packer.SetMiterLimit(settings.MiterLimit);
            packer.SetSpacing(settings.Spacing);
            packer.SetPixelRange(new Msdfgen.Range(settings.RangeValue));

            int packResult = packer.Pack(glyphs);
            if (packResult < 0)
            {
                Log.Error("Packing failed!");
                finalW = 0;
                finalH = 0;
                return false;
            }

            packer.GetDimensions(out finalW, out finalH);
            return true;
        }

        private static ImmediateAtlasGenerator<float> GenerateAtlas(GlyphGeometry[] glyphs, int width, int height, GenerationSettings settings, IProgress<GeneratorProgress> progress)
        {
            var errorMode = settings.ErrorCorrection switch
            {
                ErrorCorrectionMode.Disabled => ErrorCorrectionConfig.DistanceErrorCorrectionMode.DISABLED,
                ErrorCorrectionMode.Indiscriminate => ErrorCorrectionConfig.DistanceErrorCorrectionMode.INDISCRIMINATE,
                ErrorCorrectionMode.EdgeOnly => ErrorCorrectionConfig.DistanceErrorCorrectionMode.EDGE_ONLY,
                _ => ErrorCorrectionConfig.DistanceErrorCorrectionMode.INDISCRIMINATE
            };

            var generatorConfig = new MSDFGeneratorConfig(settings.Overlap,
                                                          new ErrorCorrectionConfig(errorMode,
                                                                                      ErrorCorrectionConfig.DistanceCheckMode.CHECK_DISTANCE_ALWAYS));

            var generator = new ImmediateAtlasGenerator<float>(width, height, (bitmap, glyph, attrs) =>
            {
                var proj = glyph.GetBoxProjection();
                var gRange = glyph.GetBoxRange();
                MsdfGenerator.GenerateMSDF(bitmap, glyph.GetShape()!, proj, gRange, generatorConfig);
            }, 3);

            generator.SetThreadCount(Environment.ProcessorCount);
            generator.Generate(glyphs, progress);
            return generator;
        }

        private static void SaveResults(ImmediateAtlasGenerator<float> generator, FontGeometry fontGeometry, GenerationSettings settings, string imageOut, string fntOut, int finalW, int finalH)
        {
            ImageSaver.Save(generator.AtlasStorage.Bitmap, imageOut);

            var metrics = fontGeometry.GetMetrics();
            FntExporter.Export([fontGeometry],
                               ImageType.Msdf,
                               finalW, finalH,
                               settings.FontSize,
                               settings.RangeValue,
                               Path.GetFileName(imageOut),
                               fntOut,
                               metrics,
                               YAxisOrientation.Upward,
                               settings.OuterPadding,
                               settings.Spacing);
        }

        private sealed record GenerationSettings
        {
            public string FontPath { get; init; } = string.Empty;
            public double FontSize { get; init; }
            public int Width { get; init; }
            public int Height { get; init; }
            public double MiterLimit { get; init; }
            public int Spacing { get; init; }
            public double RangeValue { get; init; }
            public double AngleThreshold { get; init; }
            public ColoringStrategy Strategy { get; init; }
            public ErrorCorrectionMode ErrorCorrection { get; init; }
            public bool Overlap { get; init; }
            public MsdfAtlasGen.Padding OuterPadding { get; init; } = new(0, 0, 0, 0);
        }

        private static bool IsPathUnderFolder(string fullPath, string? folderPath)
        {
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(folderPath))
                return false;

            string fullFolderPath;
            try
            {
                fullFolderPath = Path.GetFullPath(folderPath);
            }
            catch (Exception)
            {
                return false;
            }

            var separatorString = Path.DirectorySeparatorChar.ToString();
            if (!fullFolderPath.EndsWith(separatorString, StringComparison.Ordinal))
            {
                fullFolderPath += separatorString;
            }

            return fullPath.StartsWith(fullFolderPath, StringComparison.OrdinalIgnoreCase);
        }

        private static SymbolPackage? GetPackageContainingPath(string? fontPath)
        {
            if (string.IsNullOrWhiteSpace(fontPath))
                return null;

            string fullFontPath;
            try
            {
                fullFontPath = Path.GetFullPath(fontPath);
            }
            catch (Exception)
            {
                Log.Error("Failed to get full path for font path: " + fontPath);
                return null;
            }

            var package = SymbolPackage.AllPackages.FirstOrDefault(p =>
                                                                       IsPathUnderFolder(fullFontPath, p.Folder) ||
                                                                       IsPathUnderFolder(fullFontPath, p.ResourcesFolder));
            return package;
        }

        private static string? _fontFilePath = "";
        private static SymbolPackage? _selectedPackage;
        private static string _statusMessage = "";
        private static string _lastOutputDir = "";
        private static bool _isStatusError = false;
        private static float _fontSize = 90;
        private static int _width = 1024;
        private static int _height = 1024;
        private static float _miterLimit = 3.0f;
        private static int _spacing = 2;
        private static float _rangeValue = 2.0f;
        private static float _angleThreshold = 3.0f;
    }
}
