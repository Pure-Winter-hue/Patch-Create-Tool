using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VSPatchGen.Gui;

internal enum ArrayDiffMode
{
    ReplaceWhole,
    IndexByIndex
}

internal sealed class PatchOp
{
    [JsonProperty("dependsOn", NullValueHandling = NullValueHandling.Ignore)]
    public List<DependsOnEntry>? DependsOn { get; set; }

    [JsonProperty("file")]
    public string File { get; set; } = "";

    [JsonProperty("op")]
    public string Op { get; set; } = "";

    [JsonProperty("path")]
    public string Path { get; set; } = "";

    [JsonProperty("fromPath", NullValueHandling = NullValueHandling.Ignore)]
    public string? FromPath { get; set; }

    [JsonProperty("value", NullValueHandling = NullValueHandling.Ignore)]
    public JToken? Value { get; set; }

    [JsonProperty("side", NullValueHandling = NullValueHandling.Ignore)]
    public string? Side { get; set; }
}

internal sealed class DependsOnEntry
{
    [JsonProperty("modid")]
    public string ModId { get; set; } = "";

    [JsonProperty("invert", NullValueHandling = NullValueHandling.Ignore)]
    public bool? Invert { get; set; }
}

internal static class Json5ish
{
    // Reads Json.NET-friendly JSON (comments allowed, etc). Comments are ignored on load.
    public static JToken LoadFile(string path)
    {
        using var sr = File.OpenText(path);
        using var reader = new JsonTextReader(sr)
        {
            FloatParseHandling = FloatParseHandling.Decimal,
            DateParseHandling = DateParseHandling.None
        };

        return JToken.Load(reader, new JsonLoadSettings
        {
            CommentHandling = CommentHandling.Ignore,
            LineInfoHandling = LineInfoHandling.Ignore
        });
    }
}

internal sealed class PatchGeneratorOptions
{
    public ArrayDiffMode ArrayMode { get; init; } = ArrayDiffMode.ReplaceWhole;
    public bool EscapePathSegments { get; init; } = true;

    // null => omit "side" in patch, meaning it applies to both
    public string? Side { get; init; } = "server";

    // Prefer "addmerge" over "add" for compatibility.
    public bool PreferAddMerge { get; init; } = true;

    // If too many ops, collapse to one root replace
    public int CollapseThreshold { get; init; } = 800;
}

internal static class PatchGenerator
{
    public static List<PatchOp> Generate(
        JToken original,
        JToken modified,
        string targetFile,
        PatchGeneratorOptions opt,
        List<DependsOnEntry>? dependsOn = null)
    {
        var ops = new List<PatchOp>();
        Diff(original, modified, "", targetFile, ops, opt, dependsOn);

        if (ops.Count > opt.CollapseThreshold)
        {
            return new List<PatchOp>
            {
                new PatchOp
                {
                    DependsOn = dependsOn,
                    File = targetFile,
                    Op = "replace",
                    Path = "/",
                    Value = modified.DeepClone(),
                    Side = opt.Side
                }
            };
        }

        return ops;
    }

    private static void Diff(
        JToken? a,
        JToken? b,
        string path,
        string file,
        List<PatchOp> ops,
        PatchGeneratorOptions opt,
        List<DependsOnEntry>? dependsOn)
    {
        string AddOp() => opt.PreferAddMerge ? "addmerge" : "add";

        // Missing in original => create it
        if (a is null)
        {
            ops.Add(new PatchOp
            {
                DependsOn = dependsOn,
                File = file,
                Op = AddOp(),
                Path = ToVsPath(path),
                Value = b?.DeepClone(),
                Side = opt.Side
            });
            return;
        }

        // Missing in modified => remove it
        if (b is null)
        {
            ops.Add(new PatchOp
            {
                DependsOn = dependsOn,
                File = file,
                Op = "remove",
                Path = ToVsPath(path),
                Side = opt.Side
            });
            return;
        }

        // Type changed => replace
        if (a.Type != b.Type)
        {
            ops.Add(new PatchOp
            {
                DependsOn = dependsOn,
                File = file,
                Op = "replace",
                Path = ToVsPath(path),
                Value = b.DeepClone(),
                Side = opt.Side
            });
            return;
        }

        // Primitive changed => replace
        if (a is JValue || a.Type is JTokenType.Null or JTokenType.Boolean or JTokenType.Integer or JTokenType.Float or JTokenType.String)
        {
            if (!JToken.DeepEquals(a, b))
            {
                ops.Add(new PatchOp
                {
                    DependsOn = dependsOn,
                    File = file,
                    Op = "replace",
                    Path = ToVsPath(path),
                    Value = b.DeepClone(),
                    Side = opt.Side
                });
            }
            return;
        }

        // Object diff
        if (a is JObject ao && b is JObject bo)
        {
            var aProps = ao.Properties().ToDictionary(p => p.Name, p => p.Value);
            var bProps = bo.Properties().ToDictionary(p => p.Name, p => p.Value);

            // removed keys
            foreach (var removed in aProps.Keys.Except(bProps.Keys))
            {
                var p = Combine(path, EscapeSegment(removed, opt.EscapePathSegments));
                ops.Add(new PatchOp
                {
                    DependsOn = dependsOn,
                    File = file,
                    Op = "remove",
                    Path = ToVsPath(p),
                    Side = opt.Side
                });
            }

            // added keys
            foreach (var added in bProps.Keys.Except(aProps.Keys))
            {
                var p = Combine(path, EscapeSegment(added, opt.EscapePathSegments));
                ops.Add(new PatchOp
                {
                    DependsOn = dependsOn,
                    File = file,
                    Op = AddOp(),
                    Path = ToVsPath(p),
                    Value = bProps[added].DeepClone(),
                    Side = opt.Side
                });
            }

            // changed keys
            foreach (var shared in aProps.Keys.Intersect(bProps.Keys))
            {
                Diff(aProps[shared], bProps[shared],
                    Combine(path, EscapeSegment(shared, opt.EscapePathSegments)),
                    file, ops, opt, dependsOn);
            }

            return;
        }

        // Array diff
        if (a is JArray aa && b is JArray ba)
        {
            if (opt.ArrayMode == ArrayDiffMode.ReplaceWhole)
            {
                if (!JToken.DeepEquals(aa, ba))
                {
                    ops.Add(new PatchOp
                    {
                        DependsOn = dependsOn,
                        File = file,
                        Op = "replace",
                        Path = ToVsPath(path),
                        Value = ba.DeepClone(),
                        Side = opt.Side
                    });
                }
                return;
            }

            // Index-by-index mode
            var min = Math.Min(aa.Count, ba.Count);

            for (int i = 0; i < min; i++)
            {
                Diff(aa[i], ba[i], Combine(path, i.ToString(CultureInfo.InvariantCulture)),
                    file, ops, opt, dependsOn);
            }

            // Remove extra from end
            for (int i = aa.Count - 1; i >= ba.Count; i--)
            {
                ops.Add(new PatchOp
                {
                    DependsOn = dependsOn,
                    File = file,
                    Op = "remove",
                    Path = ToVsPath(Combine(path, i.ToString(CultureInfo.InvariantCulture))),
                    Side = opt.Side
                });
            }

            // Append new entries at end using "-" path
            for (int i = aa.Count; i < ba.Count; i++)
            {
                ops.Add(new PatchOp
                {
                    DependsOn = dependsOn,
                    File = file,
                    Op = AddOp(), // addmerge or add, both support "/-"
                    Path = ToVsPath(Combine(path, "-")),
                    Value = ba[i].DeepClone(),
                    Side = opt.Side
                });
            }

            return;
        }

        // Fallback
        if (!JToken.DeepEquals(a, b))
        {
            ops.Add(new PatchOp
            {
                DependsOn = dependsOn,
                File = file,
                Op = "replace",
                Path = ToVsPath(path),
                Value = b.DeepClone(),
                Side = opt.Side
            });
        }
    }

    private static string Combine(string basePath, string seg)
        => string.IsNullOrEmpty(basePath) ? seg : $"{basePath}/{seg}";

    private static string ToVsPath(string raw)
        => raw.StartsWith("/") ? raw : "/" + raw;

    private static string EscapeSegment(string seg, bool escape)
        => escape ? seg.Replace("~", "~0").Replace("/", "~1") : seg;
}

internal static class VsFileId
{
    public static string NormalizeVanillaFileId(string fileId)
    {
        var idx = fileId.IndexOf(':');
        if (idx <= 0) return fileId;

        var domain = fileId[..idx];
        if (domain.Equals("game", StringComparison.OrdinalIgnoreCase) ||
            domain.Equals("creative", StringComparison.OrdinalIgnoreCase) ||
            domain.Equals("survival", StringComparison.OrdinalIgnoreCase))
        {
            return "game" + fileId[idx..];
        }

        return fileId;
    }

    // Infer "modid:relative/path.json" from .../assets/<domain>/...
    public static bool TryInferFileId(string absolutePath, out string fileId, bool vanillaFiles = false)
    {
        fileId = "";
        var p = Path.GetFullPath(absolutePath);

        var parts = p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();

        for (int i = 0; i < parts.Length - 2; i++)
        {
            if (!parts[i].Equals("assets", StringComparison.OrdinalIgnoreCase)) continue;

            var domain = parts[i + 1];

            // Vintage Story vanilla assets can live under multiple folders (e.g. assets/creative, assets/survival)
            // but are commonly targeted via the unified "game" domain for patching.
            if (vanillaFiles && (
                    domain.Equals("game", StringComparison.OrdinalIgnoreCase) ||
                    domain.Equals("creative", StringComparison.OrdinalIgnoreCase) ||
                    domain.Equals("survival", StringComparison.OrdinalIgnoreCase)))
            {
                domain = "game";
            }

            var rel = string.Join("/", parts.Skip(i + 2));
            fileId = $"{domain}:{rel}";
            return true;
        }

        return false;
    }

    public static string? GetDomain(string fileId)
    {
        var idx = fileId.IndexOf(':');
        if (idx <= 0) return null;
        return fileId[..idx];
    }
}

internal static class PatchFileWriter
{
    public static void Write(string outPath, List<PatchOp> ops)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

        using var sw = new StreamWriter(outPath);
        using var writer = new JsonTextWriter(sw)
        {
            Formatting = Formatting.Indented,
            Indentation = 2
        };

        var serializer = new JsonSerializer
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        serializer.Serialize(writer, ops);
        writer.Flush();
    }
}

internal sealed class FolderPatchResult
{
    public int FilesScanned { get; init; }
    public int PatchesWritten { get; init; }
    public int TotalOpsWritten { get; init; }
    public List<string> Errors { get; init; } = new();
}

internal static class FolderPatchGenerator
{
    /// <summary>
    /// Recursively compares <paramref name="editedRoot"/> against <paramref name="sourceRoot"/>.
    /// For every JSON file under editedRoot that produces at least one patch operation, writes a patch
    /// file into <paramref name="outRoot"/> (all patches in one flat folder).
    /// </summary>
    public static FolderPatchResult GenerateFolder(
        string sourceRoot,
        string editedRoot,
        string outRoot,
        PatchGeneratorOptions opt,
        bool autoDepends,
        bool vanillaFiles = false)
    {
        var errors = new List<string>();

        sourceRoot = Path.GetFullPath(sourceRoot);
        editedRoot = Path.GetFullPath(editedRoot);
        outRoot = Path.GetFullPath(outRoot);

        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Source folder not found: {sourceRoot}");
        if (!Directory.Exists(editedRoot))
            throw new DirectoryNotFoundException($"Edited folder not found: {editedRoot}");

        Directory.CreateDirectory(outRoot);

        // Folder compare is typically run repeatedly while iterating.
        // To avoid confusing "duplicates" from earlier runs (e.g. when collision
        // hashing rules change, or when the set of input files changes), we clear
        // the output folder's top-level JSON files before writing new ones.
        // Output folders are intended to be dedicated patch destinations.
        foreach (var existing in Directory.EnumerateFiles(outRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            try { File.Delete(existing); }
            catch { /* ignore */ }
        }

        // Enumerate only from editedRoot so the output structure matches the edited structure.
        var editedFiles = Directory.EnumerateFiles(editedRoot, "*", SearchOption.AllDirectories)
            .Where(f => string.Equals(Path.GetExtension(f), ".json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Build a stable mapping from edited relative paths to output file names.
        // We do this up-front to avoid run-to-run filename flips when two different
        // relative paths collapse to the same flattened/sanitized filename.
        var relPaths = editedFiles
            .Select(f => Path.GetRelativePath(editedRoot, f))
            .ToList();

        var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in relPaths.GroupBy(MakeFlatPatchFileName, StringComparer.OrdinalIgnoreCase))
        {
            var baseName = group.Key;

            if (group.Count() == 1)
            {
                var rel = group.First();
                nameMap[rel] = baseName;
                continue;
            }

            // Collision: give every colliding relative path its own deterministic name.
            foreach (var rel in group.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
            {
                nameMap[rel] = AppendHashBeforeExtension(baseName, ShortHash(rel));
            }

            // If older versions wrote a non-hashed file for the first entry, remove it
            // so users don't end up with "split" outputs after reruns.
            var legacyPath = Path.Combine(outRoot, baseName);
            if (File.Exists(legacyPath))
            {
                try { File.Delete(legacyPath); }
                catch { /* ignore */ }
            }
        }

        int patchesWritten = 0;
        int totalOps = 0;

        foreach (var editedFile in editedFiles)
        {
            var rel = Path.GetRelativePath(editedRoot, editedFile);
            var sourceFile = Path.Combine(sourceRoot, rel);

            // Flatten relative paths to a single output folder.
            // Name is deterministic (see precomputed nameMap).
            var outName = nameMap.TryGetValue(rel, out var mapped)
                ? mapped
                : AppendHashBeforeExtension(MakeFlatPatchFileName(rel), ShortHash(rel));

            var outFile = Path.Combine(outRoot, outName);

            try
            {
                // FileId inference prefers the edited file path (most common), but falls back.
                string fileId;
                if (!VsFileId.TryInferFileId(editedFile, out fileId, vanillaFiles) &&
                    !VsFileId.TryInferFileId(sourceFile, out fileId, vanillaFiles))
                {
                    // Best-effort fallback so folder mode still works outside an /assets/<domain>/ tree.
                    var relUnix = rel.Replace('\\', '/');
                    fileId = $"game:{relUnix}";
                }

                List<DependsOnEntry>? dependsOn = null;
                if (autoDepends)
                {
                    var dom = VsFileId.GetDomain(fileId);
                    if (!string.IsNullOrWhiteSpace(dom) && !dom.Equals("game", StringComparison.OrdinalIgnoreCase))
                        dependsOn = new List<DependsOnEntry> { new() { ModId = dom } };
                }

                JToken? origTok = null;
                if (File.Exists(sourceFile))
                    origTok = Json5ish.LoadFile(sourceFile);

                var editTok = Json5ish.LoadFile(editedFile);

                var ops = PatchGenerator.Generate(
                    origTok,
                    editTok,
                    fileId,
                    opt,
                    dependsOn);

                if (ops.Count == 0) continue;

                PatchFileWriter.Write(outFile, ops);
                patchesWritten++;
                totalOps += ops.Count;
            }
            catch (Exception ex)
            {
                errors.Add($"{rel}: {ex.Message}");
            }
        }

        return new FolderPatchResult
        {
            FilesScanned = editedFiles.Count,
            PatchesWritten = patchesWritten,
            TotalOpsWritten = totalOps,
            Errors = errors
        };
    }

    private static string MakeFlatPatchFileName(string relativePath)
    {
        // Example: "blocks/stone/rock.json" -> "blocks__stone__rock.json"
        var relUnix = relativePath.Replace('\\', '/');
        var flattened = relUnix.Replace("/", "__");

        // Sanitize for Windows/macOS/Linux filenames.
        var invalid = Path.GetInvalidFileNameChars();
        var chars = flattened.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var name = new string(chars);

        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            name += ".json";

        return name;
    }

    private static string AppendHashBeforeExtension(string fileName, string hash)
    {
        var ext = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return $"{stem}__{hash}{ext}";
    }

    private static string ShortHash(string s)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
        // 8 hex chars is plenty for collision avoidance here.
        return BitConverter.ToString(bytes, 0, 4).Replace("-", "").ToLowerInvariant();
    }
}
