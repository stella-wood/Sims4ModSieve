using System.Text;
using Sims4ModSieve.Cli;
using Sims4ModSieve.Core.Settings;

Console.OutputEncoding = new UTF8Encoding(false);
return await CliCommands.RunAsync(args, new DuplicateSettingsStore(), Console.Out, Console.Error);
