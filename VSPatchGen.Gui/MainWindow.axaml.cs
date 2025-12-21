using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace VSPatchGen.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void BrowseSource_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickOpenJsonAsync();
        if (file is null) return;

        SourceBox.Text = file;

        if (string.IsNullOrWhiteSpace(OutBox.Text))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            OutBox.Text = Path.Combine(Path.GetDirectoryName(file) ?? "", $"{name}.patches.json");
        }
    }

    private async void BrowseEdited_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickOpenJsonAsync();
        if (file is null) return;
        EditedBox.Text = file;
    }

    private async void BrowseOut_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickSaveJsonAsync();
        if (file is null) return;
        OutBox.Text = file;
    }

    private async void Generate_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Generating...";

            var source = SourceBox.Text?.Trim();
            var edited = EditedBox.Text?.Trim();
            var outPath = OutBox.Text?.Trim();

            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                throw new InvalidOperationException("Pick a valid Source JSON file.");
            if (string.IsNullOrWhiteSpace(edited) || !File.Exists(edited))
                throw new InvalidOperationException("Pick a valid Edited JSON file.");
            if (string.IsNullOrWhiteSpace(outPath))
                throw new InvalidOperationException("Pick an output file path.");

            if (!VsFileId.TryInferFileId(source, out var fileId))
                throw new InvalidOperationException("Could not infer target file id. Make sure Source is inside .../assets/<modid>/...");

            var side = SideCombo.SelectedIndex switch
            {
                0 => "server",
                1 => "client",
                _ => null // both => omit side
            };

            var arrayMode = ArrayCombo.SelectedIndex == 1
                ? ArrayDiffMode.IndexByIndex
                : ArrayDiffMode.ReplaceWhole;

            var preferAddMerge = PreferAddMergeCheck.IsChecked == true;

            List<DependsOnEntry>? dependsOn = null;
            if (AutoDependsCheck.IsChecked == true)
            {
                var dom = VsFileId.GetDomain(fileId);
                if (!string.IsNullOrWhiteSpace(dom) && !dom.Equals("game", StringComparison.OrdinalIgnoreCase))
                {
                    dependsOn = new List<DependsOnEntry> { new() { ModId = dom } };
                }
            }

            var result = await Task.Run(() =>
            {
                var origTok = Json5ish.LoadFile(source);
                var editTok = Json5ish.LoadFile(edited);

                var ops = PatchGenerator.Generate(
                    origTok,
                    editTok,
                    fileId,
                    new PatchGeneratorOptions
                    {
                        ArrayMode = arrayMode,
                        Side = side,
                        PreferAddMerge = preferAddMerge
                    },
                    dependsOn);

                PatchFileWriter.Write(outPath, ops);
                return (fileId, ops.Count);
            });

            StatusText.Text = $"Done. Wrote {result.Count} patch op(s).\nTarget file: {result.fileId}\nOutput: {outPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
        }
    }

    private async void Help_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Keep this short and super practical.
        var text =
@"SIDE
• Server: gameplay data (recipes/worldgen/traders/most stats). Avoids client warnings for server-only assets.
• Client: visuals (shapes/animations).
• Both: omit side (default). Use when both sides should patch.

ARRAY MODE (how THIS TOOL writes patches)
• Replace whole arrays: safest output, but more likely to conflict (overwrites the whole list).
• Index-by-index: better for 'add a few entries' (like trader stock), but can break if other mods reorder the same list.

PREFER ADDMERGE (compat)
• Uses addmerge instead of add when creating keys/sections.
• If the target already exists and is an array/object, addmerge appends/merges instead of replacing.
• Best for mod compatibility when multiple mods touch the same list.";

        var win = new Window
        {
            Title = "Help",
            Width = 700,
            Height = 440,
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(16)
                }
            }
        };

        await win.ShowDialog(this);
    }

    private async Task<string?> PickOpenJsonAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select JSON file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
                FilePickerFileTypes.All
            }
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickSaveJsonAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save patch JSON",
            SuggestedFileName = "patches.json",
            DefaultExtension = "json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
                FilePickerFileTypes.All
            }
        });

        return file?.TryGetLocalPath();
    }
}
