using YamlDotNet.Serialization;

namespace Argentini.Fdeploy.Domain;

public sealed class FdeployAppState
{
    #region Run Mode Properties

    public bool VersionMode { get; set; }
    public bool InitMode { get; set; }
    public bool HelpMode { get; set; }

    #endregion

    #region Constants

    public static int MaxConsoleWidth => GetMaxConsoleWidth();
	
    private static int GetMaxConsoleWidth()
    {
        try
        {
            return Console.WindowWidth - 1;
        }
        catch
        {
            return 78;
        }
    }

    public static string CliErrorPrefix => "Fdeploy => ";

    #endregion

    #region App State Properties

    public List<string> Exceptions { get; } = [];
    public List<string> CliArguments { get; } = [];
    public FdeploySettings Settings { get; set; } = new();
    public string YamlProjectFilePath { get; set; } = string.Empty;
    public string WorkingPath { get; set; } = string.Empty;

    #endregion

    public FdeployAppState(IEnumerable<string>? args, CancellationTokenSource cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        
        #region Process Arguments
        
        CliArguments.AddRange(args ?? []);

        if (CliArguments.Count == 0)
            YamlProjectFilePath = Path.Combine(Directory.GetCurrentDirectory(), "fdeploy.yml");
        
        if (CliArguments.Count == 1)
        {
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
                return;
            }

            var projectFilePath = CliArguments[0].SetNativePathSeparators();

            if (projectFilePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) && projectFilePath.Contains(Path.DirectorySeparatorChar))
            {
                YamlProjectFilePath = projectFilePath;
            }
            else
            {
                if (projectFilePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                    YamlProjectFilePath = Path.Combine(Directory.GetCurrentDirectory(), projectFilePath);
                else
                    YamlProjectFilePath = Path.Combine(Directory.GetCurrentDirectory(), $"fdeploy-{projectFilePath}.yml");
            }
        }
        
        #if DEBUG

        YamlProjectFilePath = Path.Combine("/Users/magic/Developer/Fynydd-Website-2024/UmbracoCms", "fdeploy-staging.yml");
        
        #endif

        #endregion
        
        #region Load Settings
        
        if (File.Exists(YamlProjectFilePath) == false)
        {
            Exceptions.Add($"Could not find project file `{YamlProjectFilePath}`");
            cancellationToken.Cancel();
            return;
        }

        if (YamlProjectFilePath.IndexOf(Path.DirectorySeparatorChar) < 0)
        {
            Exceptions.Add($"Invalid project file path `{YamlProjectFilePath}`");
            cancellationToken.Cancel();
            return;
        }
            
        WorkingPath = YamlProjectFilePath[..YamlProjectFilePath.LastIndexOf(Path.DirectorySeparatorChar)];
        
        var yaml = File.ReadAllText(YamlProjectFilePath);
        var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        
        Settings = deserializer.Deserialize<FdeploySettings>(yaml);
        
        #endregion

        #region Normalize Paths

        Settings.Project.ProjectFilePath = NormalizePath(Settings.Project.ProjectFilePath);
        Settings.Paths.RemoteRootPath = NormalizePath(Settings.Paths.RemoteRootPath);

        NormalizePaths(Settings.Paths.StaticFilePaths);
        NormalizePaths(Settings.Paths.RelativeIgnorePaths);
        NormalizePaths(Settings.Paths.RelativeIgnoreFilePaths);

        foreach (var copy in Settings.Paths.StaticFileCopies)
        {
            copy.Source = NormalizePath(copy.Source);
            copy.Destination = NormalizePath(copy.Destination);
        }
        
        foreach (var copy in Settings.Paths.FileCopies)
        {
            copy.Source = NormalizePath(copy.Source);
            copy.Destination = NormalizePath(copy.Destination);
        }
        
        #endregion
    }
    
    public async ValueTask<string> GetEmbeddedYamlPathAsync(CancellationTokenSource cancellationToken)
    {
        var workingPath = System.Reflection.Assembly.GetExecutingAssembly().Location;

        while (workingPath.LastIndexOf(Path.DirectorySeparatorChar) > -1)
        {
            workingPath = workingPath[..workingPath.LastIndexOf(Path.DirectorySeparatorChar)];
            
#if DEBUG
            if (Directory.Exists(Path.Combine(workingPath, "yaml")) == false)
                continue;

            var tempPath = workingPath; 
			
            workingPath = Path.Combine(tempPath, "yaml");
#else
			if (Directory.Exists(Path.Combine(workingPath, "contentFiles")) == false)
				continue;
		
			var tempPath = workingPath; 

			workingPath = Path.Combine(tempPath, "contentFiles", "any", "any", "yaml");
#endif
            break;
        }

        // ReSharper disable once InvertIf
        if (string.IsNullOrEmpty(workingPath) || Directory.Exists(workingPath) == false)
        {
            Exceptions.Add("Embedded YAML resources cannot be found.");
            await cancellationToken.CancelAsync();
            return string.Empty;
        }
        
        return workingPath;
    }

    public async ValueTask OutputExceptionsAsync()
    {
        await Console.Out.WriteLineAsync();

        foreach (var message in Exceptions)
            await Console.Out.WriteLineAsync($"{CliErrorPrefix}{message}");
        
        await Console.Out.WriteLineAsync();
    }

    public static async ValueTask ColonOut(string topic, string message)
    {
        const int maxTopicLength = 20;

        if (topic.Length >= maxTopicLength)
            await Console.Out.WriteAsync($"{topic[..maxTopicLength]}");
        else
            await Console.Out.WriteAsync($"{topic}{" ".Repeat(maxTopicLength - topic.Length)}");
        
        await Console.Out.WriteLineAsync($" : {message}");
    }

    public static string NormalizePath(string path)
    {
        return path.SetNativePathSeparators().Trim(Path.DirectorySeparatorChar);
    }

    public static void NormalizePaths(List<string> source)
    {
        var list = new List<string>();
        
        foreach (var path in source)
            list.Add(NormalizePath(path));

        source.Clear();
        source.AddRange(list);
    }
}