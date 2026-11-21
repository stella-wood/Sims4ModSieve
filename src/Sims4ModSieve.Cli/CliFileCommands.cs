using System.Text.Json;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.FileOperations;

namespace Sims4ModSieve.Cli;

internal sealed class CliFileCommands(string dataDirectory, IRecycleBinAdapter bin, string? applicationDirectory)
{
    internal const string LogFileName = "cli-file-operations.jsonl";
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ApplicationDirectoryGuard guard = new(applicationDirectory);
    private readonly FileManagementStore history = new(dataDirectory);
    private readonly ModDisableService disabled = new(dataDirectory, applicationDirectory);
    private readonly PhysicalFileMetadataAccess metadata = new();

    private static readonly (string Command, string Syntax, string Description, string Example)[] Help =
    [
        ("list-disabled", "", "查看已禁用文件", "list-disabled --json -"),
        ("disable", "--path <文件> [--path <文件> …] [--apply]", "禁用文件，改名加 .off", "disable --path \"D:\\Mods\\mod.package\""),
        ("restore", "(--path <原路径或 .off 路径> … | --all) [--apply]", "恢复已禁用文件", "restore --all"),
        ("recycle", "--path <文件> … [--apply --expect-sha256 <哈希> …]", "移入 Windows 回收站", "recycle --path \"D:\\Mods\\mod.package\""),
        ("undo", "[--apply]", "撤回最近一批删除", "undo --json -"),
    ];

    internal static bool IsFileCommand(string command) => Help.Any(item => item.Command == command);

    internal static void PrintHelp(TextWriter output, string? command)
    {
        foreach (var item in Help.Where(item => command is null || item.Command == command))
        {
            var syntax = item.Syntax.Length == 0 ? item.Command : $"{item.Command} {item.Syntax}";
            output.WriteLine($"{syntax} [--json -]  {item.Description}。例：Sims4ModSieve.Cli {item.Example}");
        }
        if (command != "list-disabled") output.WriteLine("  默认预览文件和大小。--apply 才执行。回收时按路径顺序提供每个文件的 SHA-256。");
    }

    internal async Task<int> RunAsync(string command, string[] args, TextWriter output, TextWriter error)
    {
        var options = new Options(args.Contains("--apply"), command);
        var errors = new List<string>();
        var targets = new List<Target>();
        var parameterError = false;
        try { options.Parse(args); }
        catch (ArgumentException exception) { errors.Add(exception.Message); parameterError = true; }

        async Task ExecuteAsync()
        {
            if (parameterError)
            {
                targets.AddRange(options.Paths.Select(path => new Target(path) { Status = "failed" }));
                return;
            }
            try { await ProcessAsync(command, options, targets, errors); }
            catch (Exception exception) when (IsFileError(exception))
            {
                errors.Add(exception.Message);
                if (targets.Count == 0) targets.AddRange(options.Paths.Select(path => new Target(path)));
                foreach (var target in targets.Where(target => target.Status == "preview")) target.Reject(exception.Message);
            }
        }

        if (options.Apply && command != "list-disabled")
        {
            FileStream OpenLog() => FileManagementLock.OpenWriter(Path.Combine(dataDirectory, LogFileName),
                FileMode.Append, FileShare.Read, "操作日志正在使用，请稍后重试");
            try
            {
                // Check logging before any file changes. The actual append takes its own exclusive writer.
                using (OpenLog()) { }
                try
                {
                    using var operation = history.WriteLock.Enter();
                    await ExecuteAsync();
                }
                catch (Exception exception) when (IsFileError(exception))
                {
                    errors.Add(exception.Message);
                    targets.AddRange(options.Paths.Select(path => new Target(path) { Status = "failed" }));
                }

                await using var log = new StreamWriter(OpenLog());
                var files = targets.Select(target => new { Path = RedactPath(target.Path), target.Status });
                await log.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    Time = DateTimeOffset.Now, Command = command, Files = files,
                    Result = errors.Count > 0 || targets.Any(target => target.Status == "failed" || target.Messages.Count > 0)
                        ? "failed" : "completed",
                }, Json));
            }
            catch (Exception exception) when (IsFileError(exception))
            {
                errors.Add("无法写入操作日志：" + exception.Message);
                if (targets.Count == 0) targets.AddRange(options.Paths.Select(path => new Target(path) { Status = "failed" }));
            }
        }
        else await ExecuteAsync();

        var report = targets.Select(target => new
        {
            target.Path, target.Size, target.Destination, target.Status, target.Messages,
        }).ToArray();
        if (options.JsonOutput)
            await output.WriteLineAsync(JsonSerializer.Serialize(new { SchemaVersion = 1, Command = command, options.Apply, Files = report, Errors = errors }, Json));
        else
        {
            if (report.Length > 0 && command != "list-disabled")
                await output.WriteLineAsync(Help.Single(item => item.Command == command).Description);
            foreach (var file in report)
            {
                var label = file.Status switch { "listed" => "已禁用", "preview" => "预览", "completed" => "已完成", _ => "失败" };
                var size = file.Size is { } bytes ? $" ({bytes} 字节)" : "";
                var destination = file.Destination is null ? "" : $" → {file.Destination}";
                await output.WriteLineAsync($"{label} · {file.Path}{size}{destination}");
                foreach (var message in file.Messages) await error.WriteLineAsync($"{file.Path} · {message}");
            }
            if (report.Length == 0 && errors.Count == 0) await output.WriteLineAsync("没有待处理的文件");
            foreach (var message in errors) await error.WriteLineAsync(message);
        }

        if (parameterError) return 2;
        if (errors.Count == 0 && report.All(file => file.Status != "failed" && file.Messages.Count == 0)) return 0;
        return command == "list-disabled" ? 1 : 3;
    }

    private async Task ProcessAsync(string command, Options options, List<Target> targets, List<string> errors)
    {
        if (command == "undo")
        {
            await UndoAsync(options.Apply, targets, errors);
            return;
        }

        var state = command == "recycle" ? new DisabledModsResult([], []) : disabled.LoadReadOnly();
        var records = state.Records.ToDictionary(record => record.OriginalPath, Paths);
        if (command == "list-disabled")
        {
            targets.AddRange(state.Records.Select(record => new Target(record.DisabledPath)
                { Size = record.Size, Destination = record.OriginalPath, Status = "listed" }));
            ReportFailures(targets, state.Failures, errors);
            return;
        }

        var paths = options.All ? state.Records.Select(record => record.DisabledPath).ToArray() : options.Paths.ToArray();
        for (var index = 0; index < paths.Length; index++)
        {
            var target = new Target(paths[index]);
            targets.Add(target);
            try
            {
                target.Path = ValidatePath(target.Path, allowDisabled: command == "restore");
                if (command == "restore")
                {
                    var original = HasDisabledSuffix(target.Path) ? target.Path[..^4] : target.Path;
                    if (!records.TryGetValue(original, out var record)) throw new IOException("找不到匹配的禁用记录");
                    target.Path = ValidatePath(record.DisabledPath, allowDisabled: true);
                    target.Destination = ValidatePath(record.OriginalPath);
                    target.RecordId = record.Id;
                    target.Stamp = ReadStamp(target);
                    if (target.Stamp != new FileStamp(record.Size, record.LastWriteTimeUtc, record.Identity))
                        throw new IOException("已禁用文件与账本不符，请人工核对");
                    RequireFreeDestination(target.Destination);
                }
                else
                {
                    target.Stamp = ReadStamp(target);
                    if (command == "disable")
                    {
                        if (records.ContainsKey(target.Path)) throw new IOException("已有禁用记录，请先处理原记录");
                        target.Destination = target.Path + ".off";
                        RequireFreeDestination(target.Destination);
                    }
                    else
                    {
                        if (!bin.CanRecycle(target.Path)) throw new IOException("该位置不支持可靠的 Windows 回收站操作");
                        if (options.Apply) target.Hash = options.Hashes[index];
                    }
                }
            }
            catch (Exception exception) when (IsFileError(exception)) { target.Reject(exception.Message); }
        }

        var repeated = targets.GroupBy(target => target.Path, Paths).Where(group => group.Count() > 1);
        foreach (var target in repeated.SelectMany(group => group).Where(target => target.Status == "preview"))
            target.Reject("同一个文件被指定了多次");
        var ready = targets.Where(target => target.Status == "preview").ToArray();
        if (ready.Length == 0 || !options.Apply) return;

        if (command == "recycle")
        {
            var candidates = ready.Select(target => new FileDeletionCandidate(target.Path, target.Stamp.Length,
                target.Stamp.LastWriteTimeUtc, "", ExpectedIdentity: target.Stamp.Identity, ExpectedSha256: target.Hash)).ToArray();
            var result = await CreateFileActions().DeleteAsync(new(candidates, [], []));
            ReportBatch(ready, result.CompletedPaths, result.Failures, errors);
        }
        else
        {
            var result = command == "disable"
                ? disabled.Disable(ready.Select(target => target.Path).ToArray(), ready.ToDictionary(target => target.Path, target => target.Stamp, Paths))
                : disabled.Restore(ready.Select(target => target.RecordId!).ToArray());
            ReportBatch(ready, result.CompletedPaths, result.Failures, errors, useDestination: command == "restore");
        }
    }

    private async Task UndoAsync(bool apply, List<Target> targets, List<string> errors)
    {
        var data = history.Load();
        var ids = data.GetLastDeleteIds().ToHashSet(StringComparer.Ordinal);
        var eligible = new List<(Target Target, DeletedFileRecord Record)>();
        foreach (var record in data.Deleted.Where(record => ids.Contains(record.Id)))
        {
            var target = new Target(record.Item.OriginalPath) { Size = record.Size };
            targets.Add(target);
            try
            {
                target.Path = ValidatePath(target.Path);
                RequireFreeDestination(target.Path);
                eligible.Add((target, record));
            }
            catch (Exception exception) when (IsFileError(exception)) { target.Reject(exception.Message); }
        }
        if (eligible.Count == 0) return;

        if (apply)
        {
            var result = await CreateFileActions().RestoreDeletedAsync(eligible.Select(item => item.Record.Id).ToArray());
            ReportBatch(eligible.Select(item => item.Target).ToArray(), result.CompletedPaths, result.Failures, errors);
        }
        else
        {
            var presence = await bin.QueryAsync(eligible.Select(item => item.Record.Item).ToArray());
            ReportFailures(targets, presence.Failures.Concat(presence.MissingItems
                .Select(item => new FileActionFailure(item.OriginalPath, "找不到回收站中的文件"))), errors);
            foreach (var target in targets.Where(target => target.Messages.Count > 0)) target.Status = "failed";
        }
    }

    private FileActionService CreateFileActions() => new(bin, metadata, history, disabled, applicationDirectory);

    private string ValidatePath(string value, bool allowDisabled = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        guard.EnsureAllowed(path);
        var tail = path[(Path.GetPathRoot(path)?.Length ?? 0)..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || tail.Contains(':') || tail.Split('\\').Any(part => part.EndsWith('.') || part.EndsWith(' ')))
            throw new IOException("不支持设备路径、备用数据流或含有末尾空格或点的路径");
        if (!ModDisableService.IsModPath(allowDisabled && HasDisabledSuffix(path) ? path[..^4] : path))
            throw new IOException("只能操作 .package 或 .ts4script 文件；.off 请用 restore");
        // A junction in a parent could redirect the operation outside the checked path.
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不支持经过目录链接的文件操作");
        return path;
    }

    private FileStamp ReadStamp(Target target)
    {
        if ((File.GetAttributes(target.Path) & FileAttributes.Directory) != 0) throw new IOException("目标是文件夹");
        var stamp = metadata.GetFileStamp(target.Path);
        target.Size = stamp.Length;
        if (stamp.Identity is null) throw new IOException("无法取得可靠文件身份");
        return stamp;
    }

    private static void RequireFreeDestination(string path)
    {
        try { File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        throw new IOException("目标已有同名文件或文件夹，未覆盖");
    }

    private static void ReportBatch(Target[] targets, IReadOnlyList<string> completed, IReadOnlyList<FileActionFailure> failures,
        List<string> errors, bool useDestination = false)
    {
        foreach (var target in targets)
            if (completed.Contains(useDestination ? target.Destination : target.Path, Paths)) target.Status = "completed";
        ReportFailures(targets, failures, errors);
        foreach (var target in targets.Where(target => target.Status != "completed"))
        {
            target.Status = "failed";
            if (target.Messages.Count == 0) target.Messages.Add("文件操作未完成");
        }
    }

    private static void ReportFailures(IReadOnlyList<Target> targets, IEnumerable<FileActionFailure> failures, List<string> errors)
    {
        foreach (var failure in failures)
        {
            var target = targets.FirstOrDefault(target => Paths.Equals(target.Path, failure.Path)
                || Paths.Equals(target.Destination, failure.Path) || Paths.Equals(target.RecordId, failure.Path));
            if (target is null) errors.Add(string.IsNullOrEmpty(failure.Path) ? failure.Message : $"{failure.Path} · {failure.Message}");
            else target.Messages.Add(failure.Message);
        }
    }

    private static bool HasDisabledSuffix(string path) => path.EndsWith(".off", StringComparison.OrdinalIgnoreCase);

    private static string RedactPath(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0 && path.Replace('/', '\\').StartsWith(profile + '\\', StringComparison.OrdinalIgnoreCase)
            ? "<用户>" + path[profile.Length..] : path;
    }

    private static bool IsFileError(Exception exception) => exception is IOException or InvalidDataException or JsonException
        or ArgumentException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException;

    private sealed class Target(string path)
    {
        public string Path { get; set; } = path;
        public long? Size { get; set; }
        public string? Destination { get; set; }
        public string Status { get; set; } = "preview";
        public List<string> Messages { get; } = [];
        public FileStamp Stamp { get; set; }
        public string? RecordId { get; set; }
        public string? Hash { get; set; }

        public void Reject(string message)
        {
            Status = "failed";
            Messages.Add(message);
        }
    }

    private sealed class Options(bool apply, string command)
    {
        public bool Apply => apply;
        public bool JsonOutput { get; private set; }
        public bool All { get; private set; }
        public List<string> Paths { get; } = [];
        public List<string> Hashes { get; } = [];

        public void Parse(string[] args)
        {
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--apply" when command != "list-disabled": break;
                    case "--all" when command == "restore": All = true; break;
                    case "--path" when command is "disable" or "restore" or "recycle": Paths.Add(Value()); break;
                    case "--expect-sha256" when command == "recycle": Hashes.Add(Value()); break;
                    case "--json":
                        if (Value() != "-") throw new ArgumentException("文件操作的 JSON 请用 --json - 输出到标准输出。");
                        JsonOutput = true;
                        break;
                    default: throw new ArgumentException($"{command} 的参数无效，请运行 {command} --help。");
                }

                string Value()
                {
                    if (index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"{args[index]} 缺少值");
                    return args[++index];
                }
            }
            if (command is "disable" or "restore" or "recycle" && All == (Paths.Count > 0))
                throw new ArgumentException(command == "restore" ? "请指定 --path 或 --all，两者只能选一个" : "至少需要一个 --path");
            if (command == "recycle" && (Apply || Hashes.Count > 0)
                && (Hashes.Count != Paths.Count || Hashes.Any(hash => hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))))
                throw new ArgumentException("每个 --path 必须按顺序配一个 64 位十六进制 --expect-sha256");
        }
    }
}
