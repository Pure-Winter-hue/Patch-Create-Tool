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

        // Ensure the status text is correct even if the window was created before localization.
        StatusText.Text = Localization.T("StatusReady");

        // Default behavior: infer file id from the source path.
        UpdateFileIdFromSource();
    }

    private void SourceBox_TextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        // If the user pastes/edits the path manually, keep the inferred file id in sync.
        UpdateFileIdFromSource();
    }

    private void InferFileIdCheck_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var infer = InferFileIdCheck.IsChecked == true;

        // In infer mode, the file id box is read-only and shows what we detected.
        // In manual mode, it becomes editable so the user can type domain:path.
        FileIdBox.IsReadOnly = infer;

        if (infer)
        {
            UpdateFileIdFromSource();
        }
        else
        {
            // If the user switches to manual, keep whatever was inferred as a helpful starting point.
            // (If we can't infer, the box will be blank and they'll need to fill it in.)
        }
    }

    private void UpdateFileIdFromSource()
    {
        if (InferFileIdCheck?.IsChecked != true) return;

        var source = SourceBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            FileIdBox.Text = "";
            return;
        }

        if (VsFileId.TryInferFileId(source, out var fileId))
            FileIdBox.Text = fileId;
        else
            FileIdBox.Text = "";
    }

    private async void BrowseSource_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickOpenJsonAsync();
        if (file is null) return;

        SourceBox.Text = file;

        UpdateFileIdFromSource();

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
            StatusText.Text = Localization.T("StatusGenerating");

            var source = SourceBox.Text?.Trim();
            var edited = EditedBox.Text?.Trim();
            var outPath = OutBox.Text?.Trim();

            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                throw new InvalidOperationException(Localization.T("ErrPickValidSource"));
            if (string.IsNullOrWhiteSpace(edited) || !File.Exists(edited))
                throw new InvalidOperationException(Localization.T("ErrPickValidEdited"));
            if (string.IsNullOrWhiteSpace(outPath))
                throw new InvalidOperationException(Localization.T("ErrPickOutput"));

            string fileId;
            if (InferFileIdCheck.IsChecked == true)
            {
                if (!VsFileId.TryInferFileId(source, out fileId))
                    throw new InvalidOperationException(Localization.T("ErrInferFileId"));
            }
            else
            {
                fileId = FileIdBox.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(fileId) || VsFileId.GetDomain(fileId) is null)
                    throw new InvalidOperationException(Localization.T("ErrPickValidFileId"));
            }

            var side = SideCombo.SelectedIndex switch
            {
                0 => "server",
                1 => "client",
                _ => null // both => omit side
            };

            var arrayMode = ArrayCombo.SelectedIndex == 0
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

            StatusText.Text = string.Format(
                Localization.T("StatusDoneFormat"),
                result.Count,
                result.fileId,
                outPath);
        }
        catch (Exception ex)
        {
            StatusText.Text = Localization.T("ErrorPrefix") + ex.Message;
        }
    }

    private async void Help_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var text = Localization.T("HelpText");

        var win = new Window
        {
            Title = Localization.T("HelpTitle"),
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

    private void Language_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Build a context menu of languages.
        var langs = VsLanguageDiscovery.GetLanguagesForMenu();

        var menu = new ContextMenu();

        var items = new List<MenuItem>();
        foreach (var lang in langs.OrderBy(l => l.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var isActive = string.Equals(lang.Code, Localization.CurrentLanguage, StringComparison.OrdinalIgnoreCase);
            var mi = new MenuItem
            {
                Header = isActive ? $"✓ {lang.DisplayName}" : lang.DisplayName,
                Tag = lang.Code
            };

            mi.Click += (_, _) =>
            {
                Localization.Load((string)mi.Tag!);
                // FlowDirection is applied by Localization.Load.
            };

            items.Add(mi);
        }

        menu.Items.Clear();
        foreach (var it in items)
            menu.Items.Add(it);
        menu.Open(LanguageButton);
    }

    private async Task<string?> PickOpenJsonAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localization.T("DialogSelectJsonTitle"),
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
            Title = Localization.T("DialogSavePatchTitle"),
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