using System.Diagnostics;
using System.Runtime.InteropServices;
using Octokit;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;

const string usage =
    "Usage: AutoUpdater -o <owner> -r <repo> [-a <executable to start afterwards>]\n" +
    "Example: AutoUpdater -o sagefantasma -r MGSPW-Cheat-Trainer -a \"MGSPW Cheat Trainer\"";

Options options;
try
{
    // `args` in top-level statements already excludes the exe path.
    options = Options.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"{ex.Message}\n{usage}");
    return 1;
}

try
{
    await Updater.RunAsync(options);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Update failed: {ex.Message}");
    return 1;
}

// ---------------------------------------------------------------------------

record Options(string Owner, string Repo, string? AutoStart)
{
    private static readonly string[] Flags = ["-o", "-r", "-a"];

    public static Options Parse(string[] args)
    {
        string? owner = null, repo = null, autoStart = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o": owner = ReadValue(args, ref i); break;
                case "-r": repo = ReadValue(args, ref i); break;
                case "-a": autoStart = ReadValue(args, ref i); break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Repo owner not supplied (-o).");
        if (string.IsNullOrWhiteSpace(repo)) throw new ArgumentException("Repo name not supplied (-r).");

        return new Options(owner, repo, string.IsNullOrWhiteSpace(autoStart) ? null : autoStart);
    }

    // Reads the value following a flag. Also supports legacy 'single quoted values'
    // that the shell split into several tokens (e.g. -a 'My App').
    private static string ReadValue(string[] args, ref int i)
    {
        string flag = args[i];
        if (i + 1 >= args.Length || Flags.Contains(args[i + 1]))
            throw new ArgumentException($"Missing value after {flag}.");

        string value = args[++i];
        bool opensQuote = value.StartsWith('\'');
        bool closesQuote = value.Length > 1 && value.EndsWith('\'');

        if (opensQuote && !closesQuote)
        {
            while (i + 1 < args.Length)
            {
                value += " " + args[++i];
                if (value.EndsWith('\'')) break;
            }

            if (!value.EndsWith('\''))
                throw new ArgumentException($"Unterminated quote in value for {flag}.");
        }

        return value.Trim('\'');
    }
}

static class Updater
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly string Rid = IsWindows ? "win-x64" : "linux-x64";

    public static async Task RunAsync(Options options)
    {
        // The updater lives in the app's parent folder, so the installation target is the folder it was
        // launched from (the working directory), whatever the user named that folder.
        string installDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.CurrentDirectory));
        string tempDir = Path.Combine(Path.GetTempPath(), $"autoupdater-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string archivePath = Path.Combine(tempDir, "latestRelease.7z");
            string extractDir = Path.Combine(tempDir, "extracted");

            await DownloadLatestReleaseAsync(options, archivePath);
            Extract(archivePath, extractDir);
            Install(extractDir, installDir);

            if (options.AutoStart is not null)
                StartApp(installDir, options.AutoStart);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { Console.Error.WriteLine($"Could not clean up '{tempDir}': {ex.Message}"); }
        }
    }

    private static async Task DownloadLatestReleaseAsync(Options options, string destination)
    {
        Console.WriteLine("Finding latest release...");

        var gitHub = new GitHubClient(new ProductHeaderValue(options.Repo));
        // Optional: raises the unauthenticated rate limit (60/hr).
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(token))
            gitHub.Credentials = new Credentials(token);

        // GetLatest skips drafts and prereleases, unlike GetAll()[0].
        Release release = await gitHub.Repository.Release.GetLatest(options.Owner, options.Repo);

        ReleaseAsset asset = release.Assets.FirstOrDefault(a =>
                a.Name.Contains(Rid, StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Release '{release.TagName}' has no {Rid} .7z asset. " +
                $"Assets found: {string.Join(", ", release.Assets.Select(a => a.Name))}");

        Console.WriteLine($"Downloading {asset.Name} ({release.TagName})...");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{options.Repo}-AutoUpdater");

        using var response = await http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync();
        await using var file = File.Create(destination);
        await source.CopyToAsync(file);
    }

    private static void Extract(string archivePath, string destination)
    {
        Console.WriteLine("Extracting downloaded release...");
        Directory.CreateDirectory(destination);

        using var archive = SevenZipArchive.OpenArchive(archivePath);
        archive.WriteToDirectory(destination,
            new ExtractionOptions { ExtractFullPath = true, Overwrite = true, CheckCrc = true });
    }

    private static void Install(string extractedDir, string installDir)
    {
        // The archive holds a top-level folder whose name ends with win-x64/linux-x64
        // (e.g. "MyApp-win-x64"). If not, but there's a single wrapper folder, use that;
        // otherwise assume the files are at the archive root.
        string[] topDirs = Directory.GetDirectories(extractedDir);
        string sourceDir =
            topDirs.FirstOrDefault(d => Path.GetFileName(d).EndsWith(Rid, StringComparison.OrdinalIgnoreCase))
            ?? (topDirs.Length == 1 && Directory.GetFiles(extractedDir).Length == 0 ? topDirs[0] : extractedDir);

        Console.WriteLine($"Installing into '{installDir}'...");
        CopyDirectory(sourceDir, installDir, skipFile: Environment.ProcessPath);
    }

    private static void CopyDirectory(string source, string destination, string? skipFile)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.GetFiles(source))
        {
            string target = Path.Combine(destination, Path.GetFileName(file));

            // The running updater can't safely overwrite itself (locked on Windows).
            if (skipFile is not null && string.Equals(target, skipFile, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Skipping the running updater ({Path.GetFileName(file)}).");
                continue;
            }

            File.Copy(file, target, overwrite: true);
        }

        foreach (string dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)), skipFile);
    }

    private static void StartApp(string installDir, string name)
    {
        Console.WriteLine("Attempting to auto-start latest release...");

        string fileName = name;
        if (IsWindows)
        {
            if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                fileName += ".exe";
        }

        // Prefer the exe at the installation root; only fall back to searching subfolders.
        string rootCandidate = Path.Combine(installDir, fileName);
        string? path = File.Exists(rootCandidate)
            ? rootCandidate
            : Directory.EnumerateFiles(installDir, fileName, SearchOption.AllDirectories).FirstOrDefault();

        if (path is null)
        {
            Console.WriteLine($"'{fileName}' was not found in the installed release, cannot auto-start.");
            return;
        }

        ProcessStartInfo startInfo;
        if (IsWindows)
        {
            startInfo = new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path)!,
                UseShellExecute = true
            };
        }
        else
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) |
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

            // setsid detaches the app from the updater's session so it outlives us.
            startInfo = new ProcessStartInfo
            {
                FileName = "setsid",
                WorkingDirectory = Path.GetDirectoryName(path)!,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(path);
        }

        Process.Start(startInfo);
    }
}