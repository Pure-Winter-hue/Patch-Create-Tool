using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    // Infer "modid:relative/path.json" from .../assets/<domain>/...
    public static bool TryInferFileId(string absolutePath, out string fileId)
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
