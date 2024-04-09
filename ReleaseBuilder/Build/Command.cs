using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Duplicati.Library.AutoUpdater;

namespace ReleaseBuilder.Build;

/// <summary>
/// The build command implementation
/// </summary>
public static partial class Command
{
    /// <summary>
    /// The primary project to build for GUI builds
    /// </summary>
    private const string PrimaryGUIProject = "Duplicati.GUI.TrayIcon.csproj";

    /// <summary>
    /// The secondary project to build for CLI builds
    /// </summary>
    private const string PrimaryCLIProject = "Duplicati.CommandLine.csproj";

    /// <summary>
    /// Projects that only makes sense for Windows
    /// </summary>
    private static readonly IReadOnlySet<string> WindowsOnlyProjects = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) { "Duplicati.WindowsService.csproj" };

    /// <summary>
    /// Projects the pull in GUI dependencies
    /// </summary>
    private static readonly IReadOnlySet<string> GUIProjects = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) { "Duplicati.GUI.TrayIcon.csproj" };

    /// <summary>
    /// Some executables have shorter names that follow the Linux convention of all-lowercase
    /// </summary>
    private static readonly IDictionary<string, string> ExecutableRenames = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase)
    {
        { "Duplicati.CommandLine.BackendTester", "duplicati-backend-tester"},
        { "Duplicati.CommandLine.BackendTool", "duplicati-backend-tool" },
        { "Duplicati.CommandLine.RecoveryTool", "duplicati-recovery-tool" },
        { "Duplicati.CommandLine.AutoUpdater", "duplicati-autoupdater" },
        { "Duplicati.CommandLine.ConfigurationImporter", "duplicati-configuration-importer" },
        { "Duplicati.CommandLine", "duplicati-cli" },
        { "Duplicati.Server", "duplicati-server"},
        { "Duplicati.GUI.TrayIcon", "duplicati" }
    };

    /// <summary>
    /// The packages that are required for GUI builds
    /// </summary>
    private static readonly IReadOnlyList<string> DebianGUIDepends = ["libice6", "libsm6", "libfontconfig1", "libicu70 | libicu71 | libicu72", "libssl3"];
    /// <summary>
    /// The packages that are required for CLI builds
    /// </summary>
    private static readonly IReadOnlyList<string> DebianCLIDepends = ["libicu70 | libicu71 | libicu72", "libssl3"];

    /// <summary>
    /// The packages that are required for GUI builds
    /// </summary>
    private static readonly IReadOnlyList<string> FedoraGUIDepends = ["libICE", "libSM", "fontconfig", "libicu"];
    /// <summary>
    /// The packages that are required for CLI builds
    /// </summary>
    private static readonly IReadOnlyList<string> FedoraCLIDepends = ["libicu"];


    /// <summary>
    /// Structure for keeping all variables for a single release
    /// </summary>
    /// <param name="Version">The version to use</param>
    /// <param name="Channel">The release channel</param>
    /// <param name="Timestamp">The release timestamp</param>
    private record ReleaseInfo(Version Version, ReleaseChannel Channel, DateTime Timestamp)
    {
        /// <summary>
        /// Gets the string name for the release
        /// </summary>
        public string ReleaseName => $"{Version}_{Channel.ToString().ToLowerInvariant()}_{Timestamp:yyy-MM-dd}";


        /// <summary>
        /// Create a new release info
        /// </summary>
        /// <param name="type">The release type</param>
        /// <param name="incVersion">The incremental version</param>
        /// <returns>The release info</returns>
        public static ReleaseInfo Create(ReleaseChannel type, int incVersion)
            => new ReleaseInfo(new Version(2, 0, 0, incVersion), type, DateTime.Today);

        /// <summary>
        /// Create a new release info
        /// </summary>
        /// <param name="type">The release type</param>
        /// <param name="version">The version</param>
        /// <returns>The release info</returns>
        public static ReleaseInfo Create(ReleaseChannel type, Version version)
            => new ReleaseInfo(version, type, DateTime.Today);
    }

    /// <summary>
    /// Creates the build command
    /// </summary>
    /// <returns>The command</returns>
    public static System.CommandLine.Command Create()
    {
        var releaseChannelArgument = new Argument<ReleaseChannel>(
            name: "channel",
            description: "The release channel",
            getDefaultValue: () => ReleaseChannel.Canary
        );

        var buildTargetOption = new Option<PackageTarget[]>(
            name: "--targets",
            description: "The possible build targets, multiple arguments supported. Use the format os-arch.package, example: x64-win.msi.",
            parseArgument: arg =>
            {
                if (!arg.Tokens.Any())
                    return Program.SupportedPackageTargets.ToArray();

                var requested = arg.Tokens.Select(x => PackageTarget.ParsePackageId(x.Value)).Distinct().ToArray();
                var invalid = requested.Where(x => !Program.SupportedPackageTargets.Contains(x)).ToList();
                if (invalid.Any())
                    throw new Exception($"Following targets are not supported: {string.Join(", ", invalid.Select(x => x.PackageTargetString))}");

                return requested;
            });

        var gitStashPushOption = new Option<bool>(
            name: "--git-stash-push",
            description: "Performs a git stash command before running the build, and a git commit after updating files",
            getDefaultValue: () => true
        );

        var keepBuildsOption = new Option<bool>(
            name: "--keep-builds",
            description: "Do not delete the build folders if they already exist (re-use build)",
            getDefaultValue: () => false
        );

        var buildTempOption = new Option<DirectoryInfo>(
            name: "--build-path",
            description: "The path to the temporary folder used for builds; will be deleted on startup",
            getDefaultValue: () => new DirectoryInfo(Path.GetFullPath("build-temp"))
        );

        var solutionFileOption = new Option<FileInfo>(
            name: "--solution-file",
            description: "Path to the Duplicati.sln file",
            getDefaultValue: () => new FileInfo(Path.GetFullPath(Path.Combine("..", "Duplicati.sln")))
        );

        var disableAuthenticodeOption = new Option<bool>(
            name: "--disable-authenticode",
            description: "Disables authenticode signing",
            getDefaultValue: () => false
        );

        var disableCodeSignOption = new Option<bool>(
            name: "--disable-signcode",
            description: "Disables Apple signcode signing",
            getDefaultValue: () => false
        );

        var passwordOption = SharedOptions.passwordOption;

        var disableDockerPushOption = new Option<bool>(
            name: "--disable-docker-push",
            description: "Disables pushing the docker image to the repository",
            getDefaultValue: () => false
        );

        var macOsAppNameOption = new Option<string>(
            name: "--macos-app-name",
            description: "The name of the MacOS app bundle",
            getDefaultValue: () => "Duplicati.app"
        );

        var dockerRepoOption = new Option<string>(
            name: "--docker-repo",
            description: "The docker repository to push to",
            getDefaultValue: () => "duplicati/duplicati"
        );

        var changelogFileOption = new Option<FileInfo>(
            name: "--changelog-file",
            description: "The path to the changelog news file. Contents from this file are prepended to the changelog.",
            getDefaultValue: () => new FileInfo(Path.GetFullPath("changelog-news.txt"))
        );

        var disableNotarizeSigningOption = new Option<bool>(
            name: "--disable-notarize-signing",
            description: "Disables notarize signing for MacOS packages",
            getDefaultValue: () => false
        );

        var disableGpgSigningOption = new Option<bool>(
            name: "--disable-gpg-signing",
            description: "Disables GPG signing of packages",
            getDefaultValue: () => false
        );

        var versionOverrideOption = new Option<string?>(
            name: "--version",
            description: "Sets a custom version to use",
            getDefaultValue: () => null
        );


        var command = new System.CommandLine.Command("build", "Builds the packages for a release") {
            gitStashPushOption,
            releaseChannelArgument,
            buildTempOption,
            buildTargetOption,
            solutionFileOption,
            keepBuildsOption,
            disableAuthenticodeOption,
            disableCodeSignOption,
            passwordOption,
            macOsAppNameOption,
            disableDockerPushOption,
            dockerRepoOption,
            changelogFileOption,
            disableNotarizeSigningOption,
            disableGpgSigningOption,
            versionOverrideOption
        };

        command.Handler = CommandHandler.Create<CommandInput>(DoBuild);
        return command;
    }

    /// <summary>
    /// The input for the build command
    /// </summary>
    /// <param name="Targets">The build targets</param>
    /// <param name="BuildPath">The build path</param>
    /// <param name="SolutionFile">The solution path</param>
    /// <param name="GitStashPush">If the git stash should be performed</param>
    /// <param name="Channel">The release channel</param>
    /// <param name="Version">The version override to use</param>
    /// <param name="KeepBuilds">If the builds should be kept</param>
    /// <param name="DisableAuthenticode">If authenticode signing should be disabled</param>
    /// <param name="DisableSignCode">If signcode should be disabled</param>
    /// <param name="Password">The password to use for the keyfile</param>
    /// <param name="DisableDockerPush">If the docker push should be disabled</param>
    /// <param name="MacOSAppName">The name of the MacOS app bundle</param>
    /// <param name="DockerRepo">The docker repository to push to</param>
    /// <param name="ChangelogFile">The path to the changelog file</param>
    /// <param name="DisableNotarizeSigning">If notarize signing should be disabled</param>
    /// <param name="DisableGpgSigning">If GPG signing should be disabled</param>
    record CommandInput(
        PackageTarget[] Targets,
        DirectoryInfo BuildPath,
        FileInfo SolutionFile,
        bool GitStashPush,
        ReleaseChannel Channel,
        string? Version,
        bool KeepBuilds,
        bool DisableAuthenticode,
        bool DisableSignCode,
        string Password,
        bool DisableDockerPush,
        string MacOSAppName,
        string DockerRepo,
        FileInfo ChangelogFile,
        bool DisableNotarizeSigning,
        bool DisableGpgSigning
    );

    static async Task DoBuild(CommandInput input)
    {
        Console.WriteLine($"Building {input.Channel} release ...");

        var buildTargets = input.Targets;

        if (!buildTargets.Any())
            buildTargets = Program.SupportedPackageTargets.ToArray();

        if (!input.SolutionFile.Exists)
            throw new FileNotFoundException($"Solution file not found: {input.SolutionFile.FullName}");

        // This could be fixed, so we will throw an exception if the build is not possible
        if (buildTargets.Any(x => x.Package == PackageType.MSI) && !Program.Configuration.IsMSIBuildPossible())
            throw new Exception("WiX toolset not configured, cannot build MSI files");

        // This will be fixed in the future, but requires a new http-interface for Synology DSM
        if (buildTargets.Any(x => x.Package == PackageType.SynologySpk) && !Program.Configuration.IsSynologyPkgPossible())
            throw new Exception("Synology SPK files are currently not supported");

        // This will not work, so to make it easier for non-MacOS developers, we will remove the MacOS packages
        if (buildTargets.Any(x => x.Package == PackageType.MacPkg || x.Package == PackageType.DMG) && !Program.Configuration.IsMacPkgBuildPossible())
        {
            Console.WriteLine("MacOS packages requested but not running on MacOS, removing from build targets");
            buildTargets = buildTargets.Where(x => x.Package != PackageType.MacPkg && x.Package != PackageType.DMG).ToArray();
        }

        var baseDir = Path.GetDirectoryName(input.SolutionFile.FullName) ?? throw new Exception("Path to solution file was invalid");
        var versionFilePath = Path.Combine(baseDir, "Updates", "build_version.txt");
        if (!File.Exists(versionFilePath))
            throw new FileNotFoundException($"Version file not found: {versionFilePath}");

        var sourceProjects = Directory.EnumerateDirectories(Path.Combine(baseDir, "Executables", "net8"), "*", SearchOption.TopDirectoryOnly)
            .SelectMany(x => Directory.EnumerateFiles(x, "*.csproj", SearchOption.TopDirectoryOnly))
            .ToList();

        var primaryGUI = sourceProjects.FirstOrDefault(x => string.Equals(Path.GetFileName(x), PrimaryGUIProject, StringComparison.OrdinalIgnoreCase)) ?? throw new Exception("Failed to find tray icon executable");
        var primaryCLI = sourceProjects.FirstOrDefault(x => string.Equals(Path.GetFileName(x), PrimaryCLIProject, StringComparison.OrdinalIgnoreCase)) ?? throw new Exception("Failed to find cli executable");
        var windowsOnly = sourceProjects.Where(x => WindowsOnlyProjects.Contains(Path.GetFileName(x))).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Put primary at the end
        sourceProjects.Remove(primaryGUI);
        sourceProjects.Remove(primaryCLI);
        sourceProjects.Add(primaryCLI);
        sourceProjects.Add(primaryGUI);

        if (!File.Exists(primaryGUI))
            throw new Exception($"Failed to locate project file: {primaryGUI}");
        if (!File.Exists(primaryCLI))
            throw new Exception($"Failed to locate project file: {primaryCLI}");

        if (!input.ChangelogFile.Exists)
        {
            Console.WriteLine($"Changelog news file not found: {input.ChangelogFile.FullName}");
            Console.WriteLine($"Create an empty file if you want a release without changes");
            if (OperatingSystem.IsWindows())
                Console.WriteLine($"> type nul > {input.ChangelogFile.FullName}");
            else
                Console.WriteLine($"> touch {input.ChangelogFile.FullName}");

            Program.ReturnCode = 1;
            return;
        }

        var changelogNews = File.ReadAllText(input.ChangelogFile.FullName);

        var releaseInfo = string.IsNullOrWhiteSpace(input.Version)
            ? ReleaseInfo.Create(input.Channel, int.Parse(File.ReadAllText(versionFilePath)) + 1)
            : ReleaseInfo.Create(input.Channel, Version.Parse(input.Version));
        Console.WriteLine($"Building {releaseInfo.ReleaseName} ...");

        var keyfilePassword = string.IsNullOrEmpty(input.Password)
            ? ConsoleHelper.ReadPassword("Enter keyfile password")
            : input.Password;

        var primarySignKey = LoadKeyFile(Program.Configuration.ConfigFiles.UpdaterKeyfile.FirstOrDefault(), keyfilePassword, false);
        var additionalKeys = Program.Configuration.ConfigFiles.UpdaterKeyfile
            .Skip(1)
            .Select(x => LoadKeyFile(x, keyfilePassword, true));

        // Configure runtime environment
        var rtcfg = new RuntimeConfig(
            releaseInfo,
            additionalKeys.Prepend(primarySignKey).ToList(),
            keyfilePassword,
            changelogNews,
            input);

        rtcfg.ToggleAuthenticodeSigning();
        rtcfg.ToggleSignCodeSigning();
        rtcfg.ToggleNotarizeSigning();
        rtcfg.ToggleGpgSigning();
        await rtcfg.ToggleDockerBuild();

        if (!rtcfg.UseDockerBuild)
        {
            var unsupportedBuilds = buildTargets.Where(x => x.Package == PackageType.Docker || x.Package == PackageType.Deb || x.Package == PackageType.RPM).ToList();
            if (unsupportedBuilds.Any())
                throw new Exception($"The following packages cannot be built without Docker: {string.Join(", ", unsupportedBuilds.Select(x => x.PackageTargetString))}");
        }

        if (!input.KeepBuilds && Directory.Exists(input.BuildPath.FullName))
        {
            Console.WriteLine($"Deleting build folder: {input.BuildPath.FullName}");
            Directory.Delete(input.BuildPath.FullName, true);
        }

        if (!Directory.Exists(input.BuildPath.FullName))
            Directory.CreateDirectory(input.BuildPath.FullName);

        // Generally, the builds should happen with a clean source tree, 
        // but this can be disabled for debugging
        if (input.GitStashPush)
            await ProcessHelper.Execute(["git", "stash", "save", $"auto-build-{releaseInfo.Timestamp:yyyy-MM-dd}"], workingDirectory: baseDir);

        // Inject various files that will be embedded into the build artifacts
        await PrepareSourceDirectory(baseDir, releaseInfo, rtcfg);

        // Inject a version tag into the html files
        var revertableFiles = InjectVersionIntoFiles(baseDir, releaseInfo);

        // Perform the main compilations
        await Compile.BuildProjects(baseDir, input.BuildPath.FullName, sourceProjects, windowsOnly, GUIProjects, buildTargets, releaseInfo, input.KeepBuilds, rtcfg);

        // Create the packages
        var builtPackages = await CreatePackage.BuildPackages(baseDir, input.BuildPath.FullName, buildTargets, input.KeepBuilds, rtcfg);

        if (rtcfg.UseNotarizeSigning && builtPackages.Any(x => x.Target.Package == PackageType.DMG || x.Target.Package == PackageType.MacPkg))
        {
            // # Notarize and staple takes a while...
            Console.WriteLine("Performing notarize and staple ...");
            foreach (var p in builtPackages.Where(x => x.Target.Package == PackageType.DMG || x.Target.Package == PackageType.MacPkg))
            {
                await ProcessHelper.Execute(["xcrun", "notarytool", "submit", p.CreatedFile, "--keychain-profile", Program.Configuration.ConfigFiles.NotarizeProfile, "--wait"]);
                await ProcessHelper.Execute(["xcrun", "stapler", "staple", p.CreatedFile]);
            }
        }

        // Build the signed manifest to be uploaded to remote storage
        Console.WriteLine("Build completed, creating signed manifest ...");
        var manifestfile = Path.Combine(input.BuildPath.FullName, "packages", "autoupdate.manifest");
        if (File.Exists(manifestfile))
            File.Delete(manifestfile);

        UpdaterManager.CreateSignedManifest(
            rtcfg.SignKeys.First(),
            null,
            Path.Combine(input.BuildPath.FullName, "packages"),
            version: releaseInfo.Version.ToString(),
            updateFromV1Url: Program.Configuration.ExtraSettings.UpdateFromV1Url,
            genericUpdatePageUrl: Program.Configuration.ExtraSettings.GenericUpdatePageUrl,
            releaseType: releaseInfo.Channel.ToString().ToLowerInvariant(),
            packages: builtPackages.Select(x => new PackageEntry()
            {
                RemoteUrls = Program.Configuration.ExtraSettings.PackageUrls
                    .Select(u =>
                        u.Replace("${RELEASE_TYPE}", releaseInfo.Channel.ToString().ToLowerInvariant())
                        .Replace("${RELEASE_VERSION}", releaseInfo.Version.ToString())
                        .Replace("${RELEASE_TIMESTAMP}", releaseInfo.Timestamp.ToString("yyyy-MM-dd"))
                        .Replace("${FILENAME}", x.CreatedFile)
                    ).ToArray(),
                PackageTypeId = x.Target.PackageTargetString,
                Length = new FileInfo(Path.Combine(input.BuildPath.FullName, x.CreatedFile)).Length,
                MD5 = CalculateHash(Path.Combine(input.BuildPath.FullName, x.CreatedFile), "md5"),
                SHA256 = CalculateHash(Path.Combine(input.BuildPath.FullName, x.CreatedFile), "sha256")
            })
        );

        File.Move(
            Path.Combine(input.BuildPath.FullName, "packages", "autoupdate.manifest"),
            Path.Combine(input.BuildPath.FullName, "packages", "latest-v2.manifest"),
            true
        );

        var files = builtPackages.Select(x => x.CreatedFile).Append("latest-v2.manifest").ToList();

        // Create the GPG signatures for the files
        if (rtcfg.UseGPGSigning)
        {
            Console.WriteLine("Creating GPG signatures ...");
            await GpgSign.SignReleaseFiles(files, rtcfg);
        }

        Console.WriteLine("Build completed, uploading packages ...");


        Console.WriteLine("Upload completed, releasing packages ...");

        Console.WriteLine("Release completed, posting release notes ...");

        // Clean up the source tree
        await ProcessHelper.Execute(new[] {
                "git", "checkout",
                "Duplicati/License/VersionTag.txt",
                "Duplicati/Library/AutoUpdater/AutoUpdateURL.txt",
                "Duplicati/Library/AutoUpdater/AutoUpdateBuildChannel.txt",
                "Duplicati/Library/AutoUpdater/AutoUpdateSignKeys.txt",
            }.Concat(revertableFiles.Select(x => Path.GetRelativePath(baseDir, x)))
         , workingDirectory: baseDir);

        if (input.GitStashPush)
            await GitPush.TagAndPush(baseDir, releaseInfo);

        Console.WriteLine("All done");
    }

    /// <summary>
    /// Injects the version number into some html files
    /// </summary>
    /// <param name="baseDir">The base directory</param>
    /// <param name="releaseInfo">The release info</param>
    /// <returns>The paths that were modified</returns>
    private static string[] InjectVersionIntoFiles(string baseDir, ReleaseInfo releaseInfo)
    {
        var targetfiles = Directory.EnumerateFiles(Path.Combine(baseDir, "Duplicati", "Server", "webroot"), "*", SearchOption.AllDirectories)
            .Where(x => x.EndsWith(".html") || x.EndsWith(".js"))
            .ToArray();

        var versionre = @"(?<version>\d+\.\d+\.(\*|(\d+(\.(\*|\d+)))?))";
        var regex = new Regex(@"\?v\=" + versionre);
        foreach (var file in targetfiles)
            File.WriteAllText(
                file,
                regex.Replace(File.ReadAllText(file), $"?v={releaseInfo.Version}")
            );

        //FILEMAP.Add("AssemblyRedirects.xml", new Regex(@"newVersion\=\""" + versionre + @"\"""));

        return targetfiles;
    }

    /// <summary>
    /// Updates the source directory prior to building.
    /// This writes a stub package manifest inside the source folder,
    /// which will be embedded in the excutable to indicate which version it was built from.
    /// </summary>
    /// <param name="baseDir">The source folder base</param>
    /// <param name="releaseInfo">The release info to use</param>
    /// <param name="rtcfg">The runtime configuration</param>
    /// <returns>An awaitable task</returns>
    static Task PrepareSourceDirectory(string baseDir, ReleaseInfo releaseInfo, RuntimeConfig rtcfg)
    {
        var urlstring = string.Join(";", Program.Configuration.ExtraSettings.UpdaterUrls.Select(x =>
            x.Replace("${RELEASE_TYPE}", releaseInfo.Channel.ToString().ToLowerInvariant())
            .Replace("${RELEASE_VERSION}", releaseInfo.Version.ToString())
            .Replace("${RELEASE_TIMESTAMP}", releaseInfo.Timestamp.ToString("yyyy-MM-dd"))
            .Replace("${FILENAME}", "latest-v2.manifest")
        ));

        File.WriteAllText(Path.Combine(baseDir, "Duplicati", "License", "VersionTag.txt"), releaseInfo.Version.ToString());
        File.WriteAllText(Path.Combine(baseDir, "Duplicati", "Library", "AutoUpdater", "AutoUpdateBuildChannel.txt"), releaseInfo.Channel.ToString().ToLowerInvariant());
        File.WriteAllText(Path.Combine(baseDir, "Duplicati", "Library", "AutoUpdater", "AutoUpdateURL.txt"), urlstring);
        File.WriteAllLines(Path.Combine(baseDir, "Duplicati", "Library", "AutoUpdater", "AutoUpdateSignKeys.txt"), rtcfg.SignKeys.Select(x => x.ToXmlString(false)));

        if (!string.IsNullOrWhiteSpace(rtcfg.ChangelogNews))
            File.WriteAllText(
                Path.Combine(baseDir, "changelog.txt"),

                rtcfg.ChangelogNews + Environment.NewLine +
                File.ReadAllText(Path.Combine(baseDir, "changelog.txt"))
            );

        // Previous versions used to install the assembly redirects after the build
        // but it looks like .Net now handles this automatically
        // If not, we need to adjust the .csproj files before building
        // find "${UPDATE_SOURCE}" - type f - name Duplicati.*.exe - maxdepth 1 - exec cp Installer/ AssemblyRedirects.xml { }.config \;

        var manifestFile = Path.Combine(baseDir, "Duplicati", "Library", "AutoUpdater", "autoupdate.manifest");
        if (File.Exists(manifestFile))
            File.Delete(manifestFile);

        UpdaterManager.CreateSignedManifest(
            rtcfg.SignKeys.First(),
            null,
            Path.Combine(baseDir, "Duplicati", "Library", "AutoUpdater"),
            version: releaseInfo.Version.ToString(),
            updateFromV1Url: Program.Configuration.ExtraSettings.UpdateFromV1Url,
            genericUpdatePageUrl: Program.Configuration.ExtraSettings.GenericUpdatePageUrl,
            releaseType: releaseInfo.Channel.ToString().ToLowerInvariant()
        );



        return Task.CompletedTask;
    }

    /// <summary>
    /// Loads a keyfile and decrypts the RSA key inside
    /// </summary>
    /// <param name="keyfile">The keyfile to decrypt</param>
    /// <param name="password">The keyfile password</param>
    /// <param name="askForNewPassword">Allow asking for a new password if the password did not match</param>
    /// <returns>The matching key</returns>
    static RSA LoadKeyFile(string? keyfile, string password, bool askForNewPassword)
    {
        if (string.IsNullOrWhiteSpace(keyfile))
            throw new Exception("Unable to load keyfile, no keyfile specified");

        if (!File.Exists(keyfile))
            throw new FileNotFoundException($"Keyfile not found: {keyfile}");

        try
        {
            using var ms = new MemoryStream();
            using var fs = File.OpenRead(keyfile);
            SharpAESCrypt.SharpAESCrypt.Decrypt(password, fs, ms);

            var rsa = RSA.Create();
            rsa.FromXmlString(System.Text.Encoding.UTF8.GetString(ms.ToArray()));

            return rsa;
        }
        catch (SharpAESCrypt.SharpAESCrypt.WrongPasswordException)
        {
            if (!askForNewPassword)
                throw;
        }

        password = ConsoleHelper.ReadPassword($"Enter password for {keyfile}");
        return LoadKeyFile(keyfile, password, false);
    }

    /// <summary>
    /// Calculates the hash of a file and returns the hash as a base64 string
    /// </summary>
    /// <param name="file">The file to calculate the has for</param>
    /// <param name="algorithm">The hash algorithm</param>
    /// <returns>The base64 encoded hash</returns>
    static string CalculateHash(string file, string algorithm)
    {
        using var fs = File.OpenRead(file);
        using var hash = algorithm.ToLowerInvariant() switch
        {
            "md5" => (HashAlgorithm)MD5.Create(),
            "sha256" => SHA256.Create(),
            _ => throw new Exception($"Unknown hash algorithm: {algorithm}")
        };

        return Convert.ToBase64String(hash.ComputeHash(fs));
    }
}
