Prerequisites:
- Public GitHub repository with at least 1 release
- 7z compressed Assets within the release, with your application's files in a subfolder (do not compress your binary directly)
- To support Windows, include an asset that includes `win-x64` within it's name
- To support Linux, include an asset that includes `linux-x64` within it's name

In theory, AutoUpdater should run on the desired platform if the other platform is not supplied, but I haven't tested it yet to be certain. To use it, place `AutoUpdater` in the parent directory of the directory your application lives in, and call it from the directory your application lives in. Use the `-o` param to specify the owner of the repo to access, `-r` param to specify the repo to access, and the optional `-a` param to specify what file to launch within the downloaded assets after decompression.

Sample directory setup:
<img width="911" height="195" alt="image" src="https://github.com/user-attachments/assets/37b1676e-57b9-4510-bdbf-d4dfc145b693" />


Automatic updates *might* work if you run AutoUpdater while the app you're trying to update is still running, but it's very likely it will not and may cause file corruption. It's advised to call AutoUpdater from your code, making sure to launch it as a detached process, and immediately close your application. Example code:

```
string fileToStart;
ProcessStartInfo processStartInfo;
string args = "-r MGSPW-Cheat-Trainer -o sagefantasma -a 'MGSPW MC Cheat Trainer'";

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
	fileToStart = Path.Combine(Directory.GetParent(Environment.CurrentDirectory)!.FullName,
		"AutoUpdater.exe");
	processStartInfo = new ProcessStartInfo { FileName = fileToStart, Arguments = args };
}
else
{
	fileToStart = Path.Combine(Directory.GetParent(Environment.CurrentDirectory)!.FullName,
		"AutoUpdater");
	processStartInfo = new ProcessStartInfo { FileName = "setsid", Arguments = $"\"{fileToStart}\" {args}" };
	UnixFileMode currentMode = File.GetUnixFileMode(fileToStart);
	File.SetUnixFileMode(fileToStart,
		currentMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
}

Process.Start(processStartInfo);
Close();
```

With the above code block, your app will automatically self-identify the platform it's running on, build ProcessStartInfo accordingly, start AutoUpdater, and close itself. AutoUpdater will then take over, download the latest release from your GitHub repository, decompress it, launch the specified file as a detached process, and close itself. 

If doing auto-updates through automated code, it is advised to have a "version check" system in place that handles the AutoUpdater launch process. In example, for the "MGSPW Cheat Trainer", the following code runs to verify whether or not there is an updated version of the project available, utilizing a custom `Version` PropertyGroup in the `.csproj` file compared against the `tag` of the releases on the GitHub repository:

```
public static bool CheckIfNewUpdateExists(string appVersion)
{
	//v0.1.0.0
	GitHubClient gitHubClient = new(new ProductHeaderValue(Repo));
	IReadOnlyList<Release> releases = gitHubClient.Repository.Release.GetAll(Owner, Repo).Result;

	Tag? highestTag = null;
	foreach(Release release in releases)
	{
		Tag tag = new Tag(release.TagName);

		if(highestTag == null)
		{
			highestTag = tag;
		}

		if (highestTag != tag)
			if (CheckIfTagIsNewer(highestTag, tag))
				highestTag = tag;
	}

	Tag currentVersionTag = new Tag($"v{appVersion}");
	if(string.Equals(currentVersionTag.Name, highestTag!.Name))
	{
		//if the current version is the same as the latest release, there is no update
		return false;
	}
	return !CheckIfTagIsNewer(highestTag, currentVersionTag);
}
```

This code snippet will return "true" if there is a release on the GitHub repository with a higher `tag` than the current `Version` in the project reads, which you can use to kick-off the auto-update process.
