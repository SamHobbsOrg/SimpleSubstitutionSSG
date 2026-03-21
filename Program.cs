using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SimpleSubstitutionSSG;

/// <summary>
/// Determines if a top-level key in the configuration YAML file is valid.
/// </summary>
internal sealed class ConfigValidator
{
    private readonly HashSet<string> allowed;

    public ConfigValidator()
    {
        allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "input_dir",
            "output_dir",
            "snippets_dir",
            "block_substitutions",
            "inline_substitutions",
        };
    }

    public bool IsAllowed(string key)
    {
        return allowed.Contains(key);
    }
}

/// <summary>
/// Configuration data parsed from the YAML file.
/// </summary>
/// <param name="InputDir">The input directory containing HTML files to process.</param>
/// <param name="OutputDir">The output directory where processed files will be written.</param>
/// <param name="SnippetsDir">The directory containing snippet files.</param>
/// <param name="BlockSubstitutions">Dictionary of block substitutions mapping names to file paths and line numbers.</param>
/// <param name="InlineSubstitutions">Dictionary of inline substitutions mapping names to text and line numbers.</param>
internal record Configuration(
    string InputDir,
    string OutputDir,
    string SnippetsDir,
    Dictionary<string, (string file, int line)> BlockSubstitutions,
    Dictionary<string, (string text, int line)> InlineSubstitutions
);

/// <summary>
/// Main program class for the Simple Substitution Static Site Generator.
/// Processes the configuration file and performs text substitutions in HTML files.
/// </summary>
internal static partial class Program
{
    private static int Main(string[] args)
    {
        string baseDir = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory();
        // bool runnerIsAction = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") != null;
        try
        {
            string configPath = args.Length > 0 ? args[0] : "conf.yml";
            int exit = Run(configPath, baseDir);
            return exit;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error ::Unhandled exception: {ex.Message}");
            return 99;
        }
    }

    /// <summary>
    /// Processes the configuration file then copies the input files to the
    /// output directory then and applies the substitutions in the HTML files.
    /// </summary>
    /// <param name="configPathArg">The path to the YAML configuration file.
    /// Can be relative or absolute. Must reference a valid configuration file.</param>
    /// <param name="baseDir">The base directory of the configuration and input relative paths.</param>
    /// <returns>An integer error code; 0 upon success; otherwise, a nonzero error
    /// code indicating the first failure encountered.</returns>
    private static int Run(string configPathArg, string baseDir)
    {
        string configPath = ResolvePath(configPathArg, baseDir);

        YamlMappingNode? rootMapping = LoadAndValidateYamlConfig(configPath, out int errorCode);
        if (errorCode != 0)
        {
            return errorCode;
        }

        Configuration? config = ParseConfiguration(rootMapping!, baseDir, configPath, out errorCode);
        if (errorCode != 0)
        {
            return errorCode;
        }

        errorCode = ValidateDirectories(config!, configPath);
        if (errorCode != 0)
        {
            return errorCode;
        }

        errorCode = PrepareOutputDirectory(config!.OutputDir, configPath);
        if (errorCode != 0)
        {
            return errorCode;
        }

        CopyDirectoryContents(config!.InputDir, config.OutputDir);

        ImmutableDictionary<string, string> snippetCache = BuildSnippetCache(config.BlockSubstitutions, config.SnippetsDir, configPath);

        // Process HTML files in parallel
        List<string> htmlFiles = [.. Directory.EnumerateFiles(config.OutputDir, "*.html", SearchOption.AllDirectories)];
        _ = Parallel.ForEach(htmlFiles, file => ProcessHtmlFile(file, config.InlineSubstitutions, snippetCache));

        return 0;
    }

    private static YamlMappingNode? LoadAndValidateYamlConfig(string configPath, out int errorCode)
    {
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"::error ::Configuration file not found: {configPath}");
            errorCode = 1;
            return null;
        }

        YamlMappingNode rootMapping;
        try
        {
            using StreamReader reader = new (configPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            YamlStream yaml = [];
            yaml.Load(reader);
            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                EmitYamlError("Invalid yaml, reason: root is not a mapping", configPath, 1);
                errorCode = 1;
                return null;
            }
            rootMapping = mapping;
        }
        catch (YamlException ye)
        {
            EmitYamlError($"Invalid yaml, reason: {ye.Message}", configPath, (int) ye.Start.Line);
            errorCode = 1;
            return null;
        }

        ConfigValidator validator = new ();     // Allowed top-level keys
        // Validate top-level keys
        foreach (KeyValuePair<YamlNode, YamlNode> entry in rootMapping.Children)
        {
            if (entry.Key is YamlScalarNode keyNode)
            {
                string key = keyNode.Value ?? string.Empty;
                if (!validator.IsAllowed(key))
                {
                    EmitAnnotation("error", configPath, (int) keyNode.Start.Line, $"Configuration file unknown top-level key: {key}");
                    errorCode = 2;
                    return null;
                }
            }
        }

        errorCode = 0;
        return rootMapping;
    }

    private static Configuration? ParseConfiguration(YamlMappingNode rootMapping, string baseDir, string configPath, out int errorCode)
    {
        string inputDirRaw = GetScalar(rootMapping, "input_dir");
        string outputDirRaw = GetScalar(rootMapping, "output_dir");
        string snippetsDirRaw = GetScalar(rootMapping, "snippets_dir");

        if (string.IsNullOrEmpty(inputDirRaw))
            inputDirRaw = "src";
        if (string.IsNullOrEmpty(outputDirRaw))
            outputDirRaw = "public";
        if (string.IsNullOrEmpty(snippetsDirRaw))
            snippetsDirRaw = "snippets";

        string inputDir = ResolvePath(inputDirRaw, baseDir);
        string outputDir = ResolvePath(outputDirRaw, baseDir);
        string snippetsDir = ResolvePath(snippetsDirRaw, baseDir);

        // Detect lists
        List<(string name, string file, int line)> blockSubs = [];
        List<(string name, string text, int line)> inlineSubs = [];

        if (rootMapping.Children.TryGetValue(new YamlScalarNode("block_substitutions"), out YamlNode? bsNode) && bsNode is YamlSequenceNode bsSeq)
        {
            (bool flowControl, Configuration? value) = ProcessBlockSubstitutions(configPath, out errorCode, blockSubs, bsSeq);
            if (!flowControl)
                return value;
        }

        if (rootMapping.Children.TryGetValue(new YamlScalarNode("inline_substitutions"), out YamlNode? isNode) && isNode is YamlSequenceNode isSeq)
        {
            (bool flowControl, Configuration? value) = ProcessInlineSubstitutions(configPath, out errorCode, inlineSubs, isSeq);
            if (!flowControl)
                return value;
        }

        (Dictionary<string, (string file, int line)> blockDict, Dictionary<string, (string text, int line)> inlineDict) =
            BuildSubstitutionDictionaries(blockSubs, inlineSubs, configPath);

        errorCode = 0;
        return new Configuration(inputDir, outputDir, snippetsDir, blockDict, inlineDict);
    }

    private static (bool flowControl, Configuration? value) ProcessInlineSubstitutions(string configPath, out int errorCode, List<(string name, string text, int line)> inlineSubs, YamlSequenceNode isSeq)
    {
        foreach (YamlNode item in isSeq)
        {
            if (item is YamlMappingNode map)
            {
                HashSet<string> allowedKeys = new (StringComparer.OrdinalIgnoreCase) { "name", "text" };
                foreach (YamlScalarNode k in map.Children.Keys.OfType<YamlScalarNode>())
                {
                    if (!allowedKeys.Contains(k.Value ?? string.Empty))
                    {
                        EmitAnnotation("error", configPath, (int) k.Start.Line, $"Invalid key {k.Value}");
                        errorCode = 3;
                        return (flowControl: false, value: null);
                    }
                }

                string name = GetScalar(map, "name");
                string text = GetScalar(map, "text");
                int line = (int?) (map.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(
                    k => string.Equals(k.Value, "name", StringComparison.OrdinalIgnoreCase))
                    ?.Start.Line) ?? (int) map.Start.Line;
                inlineSubs.Add((name, text, line));
            }
        }

        errorCode = 0;
        return (flowControl: true, value: null);
    }

    private static string GetScalar(YamlMappingNode m, string name)
    {
        return m.Children.TryGetValue(new YamlScalarNode(name), out YamlNode? node) && node is YamlScalarNode sn
            ? sn.Value ?? string.Empty
            : string.Empty;
    }

    private static (bool flowControl, Configuration? value) ProcessBlockSubstitutions(
        string configPath, out int errorCode, List<(string name, string file, int line)> blockSubs, YamlSequenceNode bsSeq)
    {
        foreach (YamlNode item in bsSeq)
        {
            if (item is YamlMappingNode map)
            {
                HashSet<string> allowedKeys = new (StringComparer.OrdinalIgnoreCase) { "name", "file" };
                foreach (YamlScalarNode k in map.Children.Keys.OfType<YamlScalarNode>())
                {
                    if (!allowedKeys.Contains(k.Value ?? string.Empty))
                    {
                        EmitAnnotation("error", configPath, (int) k.Start.Line, $"Invalid key {k.Value}");
                        errorCode = 3;
                        return (flowControl: false, value: null);
                    }
                }

                string name = GetScalar(map, "name");
                string file = GetScalar(map, "file");
                int line = (int?) map.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => string.Equals(k.Value, "name", StringComparison.OrdinalIgnoreCase))?.Start.Line ?? (int) map.Start.Line;
                blockSubs.Add((name, file, line));
            }
        }
        errorCode = 0;
        return (flowControl: true, value: null);
    }

    private static (Dictionary<string, (string file, int line)>, Dictionary<string, (string text, int line)>) BuildSubstitutionDictionaries(
        List<(string name, string file, int line)> blockSubs,
        List<(string name, string text, int line)> inlineSubs,
        string configPath)
    {
        Dictionary<string, (string file, int line)> blockDict = new (StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string file, int line) in blockSubs)
        {
            if (string.IsNullOrEmpty(name))
                continue;
            if (blockDict.ContainsKey(name))
            {
                EmitAnnotation("warning", configPath, line, $"Duplicate name {name} in block block_substitutions");
                continue;
            }

            blockDict[name] = (file, line);
        }

        Dictionary<string, (string text, int line)> inlineDict = new (StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string text, int line) in inlineSubs)
        {
            if (string.IsNullOrEmpty(name))
                continue;
            if (inlineDict.ContainsKey(name))
            {
                EmitAnnotation("warning", configPath, line, $"Duplicate name {name} in block inline_substitutions");
                continue;
            }

            inlineDict[name] = (text, line);
        }

        return (blockDict, inlineDict);
    }

    private static int ValidateDirectories(Configuration config, string configPath)
    {
        // Directory checks
        if (!Directory.Exists(config.InputDir))
        {
            EmitAnnotation("error", configPath, 1, $"Input directory {config.InputDir} does not exist");
            return 4;
        }

        if (!Directory.Exists(config.SnippetsDir))
        {
            EmitAnnotation("error", configPath, 1, $"Snippets directory {config.SnippetsDir} does not exist");
            return 5;
        }

        // canonicalize
        string inputCan = Canonicalize(config.InputDir);
        string outputCan = Canonicalize(config.OutputDir);
        string snippetsCan = Canonicalize(config.SnippetsDir);

        // equality checks
        if (PathsEqual(inputCan, outputCan))
        {
            EmitAnnotation("error", configPath, 1, "input_dir and output_dir resolve to the same path");
            return 6;
        }
        if (PathsEqual(inputCan, snippetsCan))
        {
            EmitAnnotation("error", configPath, 1, "input_dir and snippets_dir resolve to the same path");
            return 6;
        }
        if (PathsEqual(outputCan, snippetsCan))
        {
            EmitAnnotation("error", configPath, 1, "output_dir and snippets_dir resolve to the same path");
            return 6;
        }

        // overlap
        if (IsSubpath(inputCan, outputCan))
        {
            EmitAnnotation("error", configPath, 1, $"Overlapping directory, {inputCan} is a subdirectory of {outputCan}");
            return 7;
        }
        if (IsSubpath(inputCan, snippetsCan))
        {
            EmitAnnotation("error", configPath, 1, $"Overlapping directory, {inputCan} is a subdirectory of {snippetsCan}");
            return 7;
        }
        if (IsSubpath(outputCan, inputCan))
        {
            EmitAnnotation("error", configPath, 1, $"Overlapping directory, {outputCan} is a subdirectory of {inputCan}");
            return 7;
        }
        if (IsSubpath(outputCan, snippetsCan))
        {
            EmitAnnotation("error", configPath, 1, $"Overlapping directory, {outputCan} is a subdirectory of {snippetsCan}");
            return 7;
        }
        if (IsSubpath(snippetsCan, inputCan))
        {
            EmitAnnotation("error", configPath, 1, $"Overlapping directory, {snippetsCan} is a subdirectory of {inputCan}");
            return 7;
        }
        if (IsSubpath(snippetsCan, outputCan))
        {
            EmitAnnotation("error", configPath, 1, $"Overlapping directory, {snippetsCan} is a subdirectory of {outputCan}");
            return 7;
        }

        return 0;
    }

    private static int PrepareOutputDirectory(string outputDir, string configPath)
    {
        try
        {
            if (Directory.Exists(outputDir))
            {
                // clear
                foreach (string entry in Directory.EnumerateFileSystemEntries(outputDir))
                {
                    try
                    {
                        if (Directory.Exists(entry))
                            Directory.Delete(entry, true);
                        else
                            File.Delete(entry);
                    }
                    catch (Exception ex)
                    {
                        EmitAnnotation("error", configPath, 1, $"Output directory cannot be cleared, error: {ex.Message}");
                        return 9;
                    }
                }
            }
            else
            {
                _ = Directory.CreateDirectory(outputDir);
            }
        }
        catch (Exception ex)
        {
            EmitAnnotation("error", configPath, 1, $"Output directory cannot be created, error: {ex.Message}");
            return 8;
        }

        return 0;
    }

    private static void CopyDirectoryContents(string inputDir, string outputDir)
    {
        Stack<(string src, string dst)> stack = new ();
        stack.Push((inputDir, outputDir));
        while (stack.Count > 0)
        {
            (string? src, string? dst) = stack.Pop();
            _ = Directory.CreateDirectory(dst);
            foreach (string file in Directory.GetFiles(src))
            {
                string destFile = Path.Combine(dst, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            foreach (string dir in Directory.GetDirectories(src))
            {
                string destSub = Path.Combine(dst, Path.GetFileName(dir));
                stack.Push((dir, destSub));
            }
        }
    }

    private static ImmutableDictionary<string, string> BuildSnippetCache(
        Dictionary<string, (string file, int line)> blockSubstitutions,
        string snippetsDir,
        string configPath)
    {
        Dictionary<string, string> snippetCacheBuilder = new (StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, (string file, int line)> kv in blockSubstitutions)
        {
            string filename = kv.Value.file ?? string.Empty;
            if (string.IsNullOrEmpty(filename))
            {
                EmitAnnotation("warning", configPath, kv.Value.line, $"File {filename} does not exist");
                continue;
            }

            string snippetPath = ResolvePath(filename, snippetsDir);
            if (!File.Exists(snippetPath))
            {
                EmitAnnotation("warning", configPath, kv.Value.line, $"File {filename} does not exist");
                continue;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(snippetPath);
                string txt = RemoveBomAndDecode(bytes);
                snippetCacheBuilder[kv.Key] = txt;
            }
            catch (Exception)
            {
                EmitAnnotation("warning", configPath, kv.Value.line, $"File {filename} does not exist");
            }
        }

        return snippetCacheBuilder.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static void ProcessHtmlFile(string filePath, Dictionary<string, (string text, int line)> inlineDict, ImmutableDictionary<string, string> snippetCache)
    {
        // read bytes to detect BOM
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(filePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error file={filePath} ::Failed to read file: {ex.Message}");
            return;
        }

        (Encoding? encoding, bool hasBom) = DetectEncoding(bytes);
        if (hasBom)
        {
            EmitAnnotation("warning", filePath, 1, "File has a BOM");
        }

        string content = encoding.GetString(bytes);

        // Regex for comments: <!-- optional space ssgb|ssgi whitespace name optionalspace -->
        Regex rx = TagRegex();
        string newContent = rx.Replace(content, match =>
        {
            string tag = match.Groups[1].Value;
            string name = match.Groups[2].Value;
            int line = GetLineOfPosition(content, match.Index);
            if (string.Equals(tag, "ssgb", StringComparison.OrdinalIgnoreCase))
            {
                if (snippetCache.TryGetValue(name, out string? snippet))
                {
                    return snippet;
                }
                else
                {
                    EmitAnnotation("warning", filePath, line, $"Block name {name} not found");
                    return match.Value;
                }
            }
            else
            {
                if (inlineDict.TryGetValue(name, out (string text, int line) txt))
                {
                    return txt.text;
                }
                else
                {
                    EmitAnnotation("warning", filePath, line, $"Substitution name {name} not found");
                    return match.Value;
                }
            }
        });

        // write back preserving BOM/encoding
        try
        {
            byte[] outBytes = encoding.GetBytes(newContent);
            if (HasBomForEncoding(encoding))
            {
                // ensure BOM present
                byte[] pre = encoding.GetPreamble();
                if (pre != null && pre.Length > 0 && !bytes.Take(pre.Length).SequenceEqual(pre))
                {
                    // prepend preamble
                    // The following collection expression syntax uses the spread operator (..)
                    // to concatenate the preamble and the output bytes.
                    // It creates a new array that contains all elements of the preamble
                    // followed by all elements of the output bytes.
                    outBytes = [.. pre, .. outBytes];
                }
            }

            File.WriteAllBytes(filePath, outBytes);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error file={filePath} ::Failed to write file: {ex.Message}");
        }
    }

    private static int GetLineOfPosition(string s, int index)
    {
        if (index <= 0)
            return 1;
        int line = 1;
        for (int i = 0; i < index && i < s.Length; i++)
        {
            if (s[i] == '\n')
                line++;
        }

        return line;
    }

    private static (Encoding encoding, bool hasBom) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode, true);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode, true);
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return (Encoding.GetEncoding("utf-32BE"), true);

        // default to UTF8 without BOM
        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), false);
    }

    private static string RemoveBomAndDecode(byte[] bytes)
    {
        (Encoding? enc, bool hasBom) = DetectEncoding(bytes);
        if (hasBom)
        {
            byte[] pre = enc.GetPreamble();
            if (pre != null && pre.Length > 0 && bytes.Length >= pre.Length)
            {
                if (bytes.Take(pre.Length).SequenceEqual(pre))
                {
                    return enc.GetString(bytes, pre.Length, bytes.Length - pre.Length);
                }
            }
        }

        return enc.GetString(bytes);
    }

    private static bool HasBomForEncoding(Encoding enc)
    {
        byte[] pre = enc.GetPreamble();
        return pre != null && pre.Length > 0;
    }

    private static string ResolvePath(string path, string baseDir)
    {
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDir, path));
    }

    private static string Canonicalize(string path)
    {
        string p = Path.GetFullPath(path);
        p = p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return p;
    }

    private static bool PathsEqual(string a, string b)
    {
        return OperatingSystem.IsWindows()
            ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            : string.Equals(a, b, StringComparison.Ordinal);
    }

    private static bool IsSubpath(string sub, string parent)
    {
        if (PathsEqual(sub, parent))
            return false;
        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!parent.EndsWith(Path.DirectorySeparatorChar))
            parent += Path.DirectorySeparatorChar;
        return sub.StartsWith(parent, cmp);
    }

    private static void EmitYamlError(string message, string configPath, int line)
    {
        EmitAnnotation("error", configPath, line, message);
    }

    private static void EmitAnnotation(string level, string file, int line, string message)
    {
        // GitHub Actions annotation format
        string prefix = level == "error" ? "error" : "warning";
        Console.WriteLine($"::{prefix} file={file},line={line}::{message}");
    }

    [GeneratedRegex("<!--\\s*(ssgb|ssgi)\\s+([^\t\r\n\f >]+)\\s*-->", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex TagRegex();
}
