using System.Diagnostics;
using System.Runtime.InteropServices;
using Octokit;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;

string exampleUse = "Example use: 'AutoUpdater.exe -o sagefantasma -r MGSPW-Cheat-Trainer";

args = Environment.GetCommandLineArgs();
if (args.Length < 4)
{
    throw new Exception(
        $"Insufficient args supplied. {exampleUse}");
}

string repo = "";
string owner = "";
string autoStartFile;
try
{
    repo = args[args.IndexOf("-r") + 1];
    owner = args[args.IndexOf("-o") + 1];
    autoStartFile = args[args.IndexOf("-a") + 1];
    if (repo == "-o" || owner == "-r")
    {
        throw new Exception($"You must supply repo owner after -o, and repo name after -r. {exampleUse}");
    }
}
catch 
{
    if (string.IsNullOrEmpty(repo))
        throw new Exception($"Repo not supplied. {exampleUse}");
    if (string.IsNullOrEmpty(owner))
        throw new Exception($"Owner not supplied. {exampleUse}");
    throw;
}

string sevenZipFile = "latestRelease.7z";

GitHubClient gitHubClient = new(new ProductHeaderValue(repo));
Release latestRelease = gitHubClient.Repository.Release.GetAll(owner, repo).Result[0];

ReleaseAsset desiredAsset = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
    //Download latest Windows release
    ? latestRelease.Assets.First(x => x.Name.Contains("win-x64"))
    :
    //Download latest Linux release
    latestRelease.Assets.First(x => x.Name.Contains("linux-x64"));

HttpClient httpClient = new HttpClient();
byte[] fileBytes = httpClient.GetAsync(desiredAsset.BrowserDownloadUrl).Result.Content.ReadAsByteArrayAsync().Result;
File.WriteAllBytes(sevenZipFile, fileBytes);

using (var archive = SevenZipArchive.OpenArchive(sevenZipFile))
{
    archive.WriteToDirectory(Directory.GetParent(Environment.CurrentDirectory)!.FullName, new ExtractionOptions{ExtractFullPath = true, Overwrite = true, CheckCrc = true});
}

File.Delete(sevenZipFile);

if (!string.IsNullOrEmpty(autoStartFile))
{
    if (autoStartFile.Contains(".exe"))
    {
        autoStartFile = autoStartFile.Replace(".exe", "");
    }

    string? fileToStart;
    ProcessStartInfo processStartInfo;
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        fileToStart = Directory
            .GetFiles(Environment.CurrentDirectory, $"{autoStartFile}.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
        processStartInfo = new ProcessStartInfo { FileName = fileToStart };
    }
    else
    {
        fileToStart = Directory.GetFiles(Environment.CurrentDirectory, autoStartFile, SearchOption.AllDirectories)
            .FirstOrDefault();
        processStartInfo = new ProcessStartInfo { FileName = "setsid", Arguments = $"\"{fileToStart}\"" };
    }
    if (string.IsNullOrEmpty(fileToStart))
    {
        Console.WriteLine($"{autoStartFile} was not found in the downloaded and decompressed release, cannot auto-start.");
        return;
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        UnixFileMode currentMode = File.GetUnixFileMode(fileToStart);
        File.SetUnixFileMode(fileToStart, currentMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
    
    Process.Start(processStartInfo);
}