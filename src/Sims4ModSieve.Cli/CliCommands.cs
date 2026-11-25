using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Core.Settings;
using Sims4ModSieve.Core.Conflicts;
using Sims4ModSieve.Caching;
using Sims4ModSieve.FileOperations;
using Sims4ModSieve.Packages;

namespace Sims4ModSieve.Cli;

internal static class CliCommands
{
    private const string SettingsNotice =
        "本次目录来自已保存的设置。告诉玩家查的是哪里，玩家说不对再换。";

    internal static async Task<int> RunAsync(string[] args, DuplicateSettingsStore settings,
        TextWriter output, TextWriter error, string? documentsDirectory = null,
        long? conflictGroupingMemoryBudgetBytes = null, IStorageProfile? storageProfile = null,
        IRecycleBinAdapter? recycleBin = null, string? applicationDirectory = null)
    {
        if (args is [] or ["-h" or "--help"])
        {
            PrintHelp(output);
            return 0;
        }

        var command = args[0];
        var operands = args[1..];
        try
        {
            if (operands is ["-h" or "--help"]
                && (command is "guide" or "find-mods" or "config" or "scan-conflicts" or "scan-scripts" or "scan-duplicates"
                    || CliFileCommands.IsFileCommand(command))
                || command == "config" && operands is ["show" or "set-mods", "-h" or "--help"])
            {
                PrintHelp(output, command, operands.Length == 2 ? operands[0] : null);
                return 0;
            }

            switch (command)
            {
                case "guide" when operands.Length == 0:
                    using (var stream = typeof(CliCommands).Assembly.GetManifestResourceStream("AI-GUIDE.md")!)
                    using (var reader = new StreamReader(stream))
                        await output.WriteAsync(await reader.ReadToEndAsync());
                    return 0;
                case "find-mods" when operands.Length == 0:
                    var configured = settings.Load()?.ModsRoot;
                    if (Directory.Exists(configured))
                    {
                        await output.WriteLineAsync($"设置中的 Mods：{Path.GetFullPath(configured!)}");
                        return 0;
                    }
                    if (DuplicateSettingsStore.FindDefaultFolder("Mods", documentsDirectory) is { } found)
                    {
                        await output.WriteLineAsync($"系统文档中的 Mods：{found}");
                        return 0;
                    }
                    await error.WriteLineAsync("未找到 Mods 文件夹");
                    return 1;
                case "config" when operands is ["show"]:
                    var saved = settings.Load();
                    await output.WriteLineAsync($"默认 Mods：{DescribeFolder(saved?.ModsRoot)}");
                    await output.WriteLineAsync($"Tray：{DescribeFolder(saved?.TrayRoot)}");
                    await output.WriteLineAsync("扫描来源（旧设置）：");
                    foreach (var source in saved?.Sources ?? [])
                        await output.WriteLineAsync($"  {(source.Enabled ? "启用" : "停用")} · {DescribeFolder(source.Path)}");
                    if (saved is null || saved.Sources.Count == 0) await output.WriteLineAsync("  无");
                    return 0;
                case "config" when operands is ["set-mods", var mods]:
                    var path = NormalizePath(mods);
                    if (!Directory.Exists(path)) throw new ArgumentException($"目录不存在或不可访问：{path}");
                    settings.Update(current => current with { ModsRoot = path });
                    await output.WriteLineAsync($"已保存默认 Mods：{path}");
                    return 0;
                case "scan-conflicts" or "scan-scripts" or "scan-duplicates":
                    return await ScanAsync(command, operands, settings, output, error, documentsDirectory, conflictGroupingMemoryBudgetBytes, storageProfile, applicationDirectory);
                case "list-disabled" or "disable" or "restore" or "recycle" or "undo":
                    return await new CliFileCommands(settings.DataDirectory, recycleBin ?? new WindowsRecycleBinAdapter(),
                        applicationDirectory).RunAsync(command, operands, output, error);
                default:
                    throw new ArgumentException($"未知命令或参数：{string.Join(' ', args)}。请运行 --help。");
            }
        }
        catch (ArgumentException exception)
        {
            await error.WriteLineAsync(exception.Message);
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or NotSupportedException or System.Security.SecurityException)
        {
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    // 用 --source 时只是借已保存的 Mods 来判断保留哪份；设置读不出来就当没有，不让扫描失败。
    private static string? TryLoadSavedModsRoot(DuplicateSettingsStore settings)
    {
        try { return settings.Load()?.ModsRoot; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or System.Security.SecurityException)
        { return null; }
    }

    private static async Task<int> ScanAsync(string command, string[] args, DuplicateSettingsStore settings,
        TextWriter output, TextWriter error, string? documentsDirectory, long? conflictGroupingMemoryBudgetBytes,
        IStorageProfile? storageProfile, string? applicationDirectory)
    {
        var options = ParseScanOptions(command, args);
        var explicitSources = options.Sources.Count > 0;
        string? modsRoot = options.ModsRoot;
        if (explicitSources && modsRoot is null && command != "scan-scripts"
            && TryLoadSavedModsRoot(settings) is { } savedRoot && Directory.Exists(savedRoot))
            modsRoot = NormalizePath(savedRoot);
        if (!explicitSources)
        {
            var savedMods = settings.Load()?.ModsRoot;
            var configured = savedMods ?? DuplicateSettingsStore.FindDefaultFolder("Mods", documentsDirectory);
            if (!Directory.Exists(configured))
                throw new ArgumentException("没有可用的 Mods 目录。请运行 find-mods 或 config set-mods <路径>。");
            modsRoot ??= NormalizePath(configured!);
            options.Sources.Add(configured!);
        }

        var paths = options.Sources.Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!paths.Any(Directory.Exists)) throw new ArgumentException("没有可用的 --source 目录。");
        var registrationRoot = modsRoot ?? TryLoadSavedModsRoot(settings) ?? DuplicateSettingsStore.FindDefaultFolder("Mods", documentsDirectory);
        var externalDisabledPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Action<string>? observeFile = registrationRoot is null ? null : path =>
        {
            if (ModDisableService.TryGetExternalOriginalPath(path, out _)) externalDisabledPaths.Add(path);
        };
        var sources = paths.Select((path, index) => new ScanSource($"source-{index + 1}", path, Order: index, Label: path)).ToArray();
        var ignored = IgnoredFileRecord.Read(settings.DataDirectory)
            .Select(record => NormalizePath(record.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extensions = options.Extensions.Count > 0 || explicitSources ? options.Extensions : [".package", ".ts4script"];
        var stdoutJson = options.JsonPath == "-";
        var summary = options.JsonPath is not null ? error : output;
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var reportName = command == "scan-duplicates" ? "duplicate" : command[5..];
        var jsonPath = stdoutJson ? null : GetReportPath(options.JsonPath ?? $"{reportName}-report-{timestamp}.json");
        var htmlPath = options.HtmlPath;
        if (htmlPath is null && command == "scan-duplicates" && options.JsonPath is null && !options.HasOutputFilters)
            htmlPath = $"duplicate-report-{timestamp}.html";
        if (htmlPath is not null) htmlPath = GetReportPath(htmlPath);
        if (jsonPath is not null && string.Equals(jsonPath, htmlPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("JSON 和 HTML 必须使用不同的报告路径。");

        if (!explicitSources) await error.WriteLineAsync(SettingsNotice);
        await error.WriteLineAsync("正在扫描…");
        using var cache = options.NoCache ? null : CachingStableFileHasher.Open(settings.DataDirectory);
        var scanOptions = new ConflictScanOptions(LoadConcurrency(settings), storageProfile ?? new WindowsStorageProfile())
        {
            ConcurrencyDecided = decision => error.WriteLine(decision.DiagnosticText),
        };
        async Task<CliScanReport> RunScanAsync(IStableFileHasher? hasher, IResourceContentHasher resourceHasher)
        {
            var report = new CliScanReport(command, explicitSources ? "argument" : "settings",
                explicitSources ? null : SettingsNotice, ignored, options, conflictGroupingMemoryBudgetBytes, size =>
                {
                    error.WriteLine(size.DiagnosticText);
                    if (size.ExceedsLimit) error.WriteLine(size.WarningMessage);
                }, memoryObserved: error.WriteLine,
                scanOptions: scanOptions, fileHasher: hasher, resourceHasher: resourceHasher, coverage: cache?.Coverage);
            await report.RunAsync(sources, modsRoot, extensions, observeFile);
            return report;
        }
        var resourceHasher = DbpfResourceContentHasher.CreateDefault();
        var scan = cache is null ? await RunScanAsync(null, resourceHasher) : await cache.RunAsync(resourceHasher, RunScanAsync);
        var hasSources = scan.Sources.Count > 0;
        if (hasSources)
            await summary.WriteLineAsync($"扫描目录（{(explicitSources ? "--source" : "设置")}）：{string.Join("、", scan.Sources.Select(source => source.Path))}");
        else
            await error.WriteLineAsync(explicitSources ? "没有可用的 --source 目录。" :
                "没有可用的 Mods 目录。请运行 find-mods 或 config set-mods <路径>。");
        var json = scan.ToJson();
        if (stdoutJson)
            await output.WriteLineAsync(json);
        else
        {
            await WriteReportAsync(jsonPath!, json);
            await summary.WriteLineAsync($"JSON：{jsonPath}");
        }
        if (htmlPath is not null)
        {
            await WriteReportAsync(htmlPath, scan.ToHtml());
            await summary.WriteLineAsync($"HTML：{htmlPath}");
        }
        await summary.WriteLineAsync(scan.SummaryText);
        if (options.JsonPath is null && (options.HasOutputFilters || options.HideIgnored))
            await error.WriteLineAsync(scan.OutputOmissionsText);
        if (hasSources) cache?.Commit();
        if (registrationRoot is not null)
        {
            try
            {
                var registered = new ModDisableService(settings.DataDirectory, applicationDirectory)
                    .RegisterExternalInMods(registrationRoot, paths, externalDisabledPaths);
                foreach (var failure in registered?.Failures ?? [])
                    await error.WriteLineAsync($"外部禁用记录 · {failure.Path} · {failure.Message}");
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
                or System.Text.Json.JsonException or System.Security.SecurityException)
            {
                await error.WriteLineAsync("无法登记外部禁用文件：" + exception.Message);
            }
        }
        return hasSources ? scan.ExitCode : 2;
    }

    private static DuplicateHashConcurrency LoadConcurrency(DuplicateSettingsStore settings)
    {
        try
        {
            var value = settings.Load()?.HashConcurrency ?? DuplicateHashConcurrency.Auto;
            return Enum.IsDefined(value) ? value : DuplicateHashConcurrency.Auto;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return DuplicateHashConcurrency.Auto;
        }
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string GetReportPath(string path)
    {
        var fullPath = NormalizePath(path);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new ArgumentException($"报告路径已存在：{fullPath}");
        if (fullPath[(Path.GetPathRoot(fullPath)?.Length ?? 0)..].Contains(':')
            || Path.GetFileName(fullPath).ToLowerInvariant() is "duplicate-settings.json" or "file-management.json" or "disabled-mods.json"
                or CliFileCommands.LogFileName or ".file-management.lock"
            || Path.GetExtension(fullPath).ToLowerInvariant() is ".package" or ".ts4script" or ".off"
            || ModDisableService.TryGetExternalOriginalPath(fullPath, out _))
            throw new ArgumentException($"报告路径必须使用报告文件名：{fullPath}");
        return fullPath;
    }

    private static async Task WriteReportAsync(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(file);
        await writer.WriteAsync(text);
    }

    private static string DescribeFolder(string? path) =>
        path is null ? "未设置" : $"{Path.GetFullPath(path)}（{(Directory.Exists(path) ? "存在" : "不存在或不可访问")}）";

    private static ScanOptions ParseScanOptions(string command, string[] args)
    {
        var sources = new List<string>();
        var extensions = new List<string>();
        string? modsRoot = null;
        string? jsonPath = null;
        string? htmlPath = null;
        var hideIgnored = false;
        var severities = new List<string>();
        var kinds = new List<string>();
        int? limit = null;
        string? pathContains = null;
        var omitResourceKeys = false;
        var summaryOnly = false;
        var noCache = false;
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--hide-ignored": hideIgnored = true; continue;
                case "--summary-only": summaryOnly = true; continue;
                case "--no-cache": noCache = true; continue;
                case "--omit-resource-keys" when command == "scan-conflicts": omitResourceKeys = true; continue;
                case "--omit-resource-keys":
                    throw new ArgumentException($"{option} 仅适用于 scan-conflicts。");
            }
            if (index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"缺少参数值：{option}");
            var value = args[++index];
            switch (option)
            {
                case "--source": sources.Add(value); break;
                case "--json": jsonPath = value; break;
                case "--path-contains" when command is "scan-duplicates" or "scan-conflicts": pathContains = value; break;
                case "--severity":
                    if (value is not ("abnormal" or "risk" or "no_effect" or "incomplete"))
                        throw new ArgumentException($"无效的 --severity：{value}。可用值：abnormal、risk、no_effect、incomplete。");
                    severities.Add(value);
                    break;
                case "--kind":
                    var applicable = value == "read_incomplete"
                        || command != "scan-scripts" && value == "duplicate"
                        || command != "scan-duplicates" && value is ("script_collision" or "script_duplicate" or "damaged_file")
                        || command == "scan-conflicts" && value is ("resource_conflict" or "bundle_containment" or "suspected_same_mod");
                    if (!applicable) throw new ArgumentException($"--kind {value} 不适用于 {command} 或不是有效种类。");
                    kinds.Add(value);
                    break;
                case "--limit":
                    if (!int.TryParse(value, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var count))
                        throw new ArgumentException("--limit 必须是大于或等于 0 的整数。");
                    limit = count;
                    break;
                case "--mods-root" when command is "scan-duplicates" or "scan-conflicts": modsRoot = NormalizePath(value); break;
                case "--extension" when command == "scan-duplicates":
                    ArgumentException.ThrowIfNullOrWhiteSpace(value);
                    extensions.Add(value);
                    break;
                case "--html" when command == "scan-duplicates" && value != "-": htmlPath = value; break;
                default: throw new ArgumentException($"未知参数：{option}。请运行 {command} --help。");
            }
        }
        var options = new ScanOptions(sources, extensions, modsRoot, jsonPath, htmlPath, hideIgnored,
            severities.Distinct().ToArray(), kinds.Distinct().ToArray(), limit, omitResourceKeys, summaryOnly, noCache, pathContains);
        if (htmlPath is not null && options.HasOutputFilters)
            throw new ArgumentException("--html 不能与 --severity、--kind、--path-contains、--limit、--omit-resource-keys 或 --summary-only 同用；请另行生成 HTML 报告。");
        return options;
    }

    private static void PrintHelp(TextWriter output, string? command = null, string? subcommand = null)
    {
        output.WriteLine("AI 助手请先运行 `guide`");
        output.WriteLine();
        var scans = new (string Command, string Meaning)[]
        {
            ("scan-duplicates", "按完整文件内容查找重复副本。"),
            ("scan-conflicts", "检查 package 资源覆盖、脚本模块碰撞和完整文件重复。"),
            ("scan-scripts", "检查 .ts4script 同名模块及其内容差异。"),
        };
        foreach (var scan in scans.Where(scan => command is null || command == scan.Command))
        {
            output.WriteLine($"{scan.Command} [--source <目录>] [--json <文件|->] [--hide-ignored]");
            output.WriteLine($"  {scan.Meaning}");
            if (scan.Command == "scan-duplicates" && command is not null)
            {
                output.WriteLine("  --extension <扩展名>  限定扫描类型，可重复指定");
                output.WriteLine("  --html <文件>  指定 HTML 报告路径");
                output.WriteLine("    省略 --json 且未使用 --severity、--kind、--path-contains、--limit 或 --summary-only 时，自动在当前目录生成 HTML。");
            }
            output.WriteLine($"  例：Sims4ModSieve.Cli {scan.Command} --source \"D:\\Mods\" --json \"report.json\"");
        }
        if (command is null || scans.Any(scan => scan.Command == command))
        {
            if (command is null or "scan-duplicates" or "scan-conflicts")
                output.WriteLine("  --mods-root <目录>  scan-duplicates、scan-conflicts 建议保留时优先考虑的 Mods 目录；省略时使用已保存且存在的默认 Mods");
            output.WriteLine("  --source <目录>  临时指定扫描目录，可重复指定。省略时使用默认 Mods");
            output.WriteLine("  --json <文件|->  写入 JSON 文件，或用 - 输出到标准输出。省略时写入当前目录并显示摘要");
            output.WriteLine("  --hide-ignored  隐藏已经忽略的文件");
            output.WriteLine("  --no-cache  跳过整文件与资源哈希缓存的读写");
            output.WriteLine("  --severity <级别>  筛选组和读取问题，可重复：abnormal、risk、no_effect、incomplete");
            output.WriteLine("  --kind <种类>  筛选组和读取问题，可重复；与 severity 同用时取交集");
            if (command is null or "scan-duplicates")
                output.WriteLine("    scan-duplicates：duplicate、read_incomplete");
            if (command is null or "scan-scripts")
                output.WriteLine("    scan-scripts：script_collision、script_duplicate、damaged_file、read_incomplete");
            if (command is null or "scan-conflicts")
            {
                output.WriteLine("    scan-conflicts：resource_conflict、bundle_containment、suspected_same_mod、duplicate、script_collision、script_duplicate、damaged_file、read_incomplete");
                output.WriteLine("  --omit-resource-keys  不列 resourceKeys，保留 resourceKeyCount；仅 scan-conflicts");
            }
            output.WriteLine("  --limit <N>  筛选后最多列 N 组，N 可为 0；不限制读取问题");
            if (command is null or "scan-duplicates" or "scan-conflicts")
                output.WriteLine("  --path-contains <文本>  空格分词，按组内文件路径匹配、排序并保留整组；仅 scan-duplicates 和 scan-conflicts");
            output.WriteLine("  --summary-only  只输出汇总，groups 和 issues 为空");
            output.WriteLine("  筛选只影响 JSON 明细，汇总和退出码按完整结果计算；complete=false 表示输出不完整。");
            output.WriteLine("  显式 --json 时摘要写标准错误；不写 --json 时保持写文件并在标准输出显示摘要。");
            output.WriteLine("  上述新筛选参数不能与 --html 同用，使用时也不自动生成 HTML。");
        }
        if (command is null or "find-mods")
            output.WriteLine("find-mods  查找 Mods 文件夹。例：Sims4ModSieve.Cli find-mods");
        if ((command is null or "config") && (subcommand is null or "show"))
            output.WriteLine("config show  查看目录设置和可用性。例：Sims4ModSieve.Cli config show");
        if ((command is null or "config") && (subcommand is null or "set-mods"))
            output.WriteLine("config set-mods <路径>  保存已存在的 Mods 文件夹。例：Sims4ModSieve.Cli config set-mods \"D:\\Mods\"");
        if (command is null or "guide")
            output.WriteLine("guide  阅读给 AI 助手的操作说明。例：Sims4ModSieve.Cli guide");
        if (command is null || CliFileCommands.IsFileCommand(command)) CliFileCommands.PrintHelp(output, command);
        if (command is null) output.WriteLine("使用 <命令> --help 查看命令帮助。");
        output.WriteLine("退出码：0 成功或无发现，1 有发现、未分析项或读取失败，2 参数错误或无可用目录，3 文件操作全部或部分失败。");
    }

    internal sealed record ScanOptions(List<string> Sources, List<string> Extensions, string? ModsRoot,
        string? JsonPath, string? HtmlPath, bool HideIgnored, IReadOnlyList<string> Severities,
        IReadOnlyList<string> Kinds, int? Limit, bool OmitResourceKeys, bool SummaryOnly, bool NoCache, string? PathContains = null)
    {
        public bool HasOutputFilters => Severities.Count > 0 || Kinds.Count > 0 || Limit.HasValue
            || OmitResourceKeys || SummaryOnly || PathContains is not null;
    }
}
