using YamlDotNet.Serialization;

namespace Argentini.Fdeploy.Domain;

public sealed class FdeployAppState
{
    #region Run Mode Properties

    public bool VersionMode { get; set; }
    public bool InitMode { get; set; }
    public bool HelpMode { get; set; }

    #endregion

    #region App State Properties

    public List<string> CliArguments { get; } = new();
    public static string CliErrorPrefix => "Fdeploy => ";

    public FdeploySettings Settings { get; set; } = new();
    public ConcurrentDictionary<string, string> DiagnosticOutput { get; } = new();
    public string WorkingPathOverride { get; set; } = string.Empty;
    public string SettingsFilePath { get; set; } = string.Empty;
    public string WorkingPath { get; set; } = GetWorkingPath();
    public string YamlPath { get; set; } = string.Empty;

    #endregion

    public FdeployAppState()
    {
    }

    #region Entry Points

    /// <summary>
    /// Initialize the app state. Loads settings JSON file from working path.
    /// Sets up runtime environment for the runner.
    /// </summary>
    /// <param name="args">CLI arguments</param>
    public async Task InitializeAsync(IEnumerable<string> args)
    {
        var timer = new Stopwatch();

        DiagnosticOutput.Clear();

        await ProcessCliArgumentsAsync(args);

        timer.Start();

        if (VersionMode == false && HelpMode == false && InitMode == false)
        {
        }
        
        
        
        
        
        
        
        
        
        var yaml = await File.ReadAllTextAsync(SettingsFilePath);
        var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        
        Settings = deserializer.Deserialize<FdeploySettings>(yaml);
    }

    #endregion

    #region Initialization Methods

    /// <summary>
    /// Process CLI arguments and set properties accordingly.
    /// </summary>
    /// <param name="args"></param>
    private async Task ProcessCliArgumentsAsync(IEnumerable<string>? args)
    {
        CliArguments.Clear();
        CliArguments.AddRange(args?.ToList() ?? new List<string>());

        if (CliArguments.Count < 1)
            return;

        if (CliArguments.Count == 0)
        {
            HelpMode = true;
        }

        else
        {
            if (CliArguments[0] != "help" && CliArguments[0] != "version" && CliArguments[0] != "init")
            {
                await Console.Out.WriteLineAsync(
                    "Invalid command specified; must be: help, init, version, build, or watch");
                await Console.Out.WriteLineAsync("Use command `fdeploy help` for assistance");
                Environment.Exit(1);
            }

            if (CliArguments[0] == "help")
            {
                HelpMode = true;
                return;
            }

            if (CliArguments[0] == "version")
            {
                VersionMode = true;
                return;
            }

            if (CliArguments[0] == "init")
            {
                InitMode = true;
            }

            if (CliArguments.Count > 1)
            {
                for (var x = 1; x < CliArguments.Count; x++)
                {
                    var arg = CliArguments[x];

                    if (arg.Equals("--path", StringComparison.OrdinalIgnoreCase))
                    {
                        if (++x < CliArguments.Count)
                        {
                            var path = CliArguments[x].SetNativePathSeparators();

                            if (path.Contains(Path.DirectorySeparatorChar) == false &&
                                path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                                path = path[..path.LastIndexOf(Path.DirectorySeparatorChar)];

                            try
                            {
                                WorkingPathOverride = Path.GetFullPath(path);
                            }

                            catch
                            {
                                await Console.Out.WriteLineAsync($"{CliErrorPrefix}Invalid project path at {path}");
                                Environment.Exit(1);
                            }
                        }
                    }
                }
            }
        }
    }

    public static string GetWorkingPath()
    {
        return Directory.GetCurrentDirectory();
    }

    #endregion
}