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

    public CancellationTokenSource CancellationTokenSource { get; set; } = new();
    public List<string> Exceptions { get; } = [];
    public List<string> CliArguments { get; } = [];
    public FdeploySettings Settings { get; set; } = new();
    public string YamlProjectFilePath { get; set; } = string.Empty;
    public string WorkingPath { get; set; } = string.Empty;

    #endregion

    public FdeployAppState(IEnumerable<string>? args)
    {
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
            CancellationTokenSource.Cancel();
            return;
        }

        if (YamlProjectFilePath.IndexOf(Path.DirectorySeparatorChar) < 0)
        {
            Exceptions.Add($"Invalid project file path `{YamlProjectFilePath}`");
            CancellationTokenSource.Cancel();
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