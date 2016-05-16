﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.DotNet.Cli.Build.Framework;
using Microsoft.DotNet.InternalAbstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.DotNet.Cli.Build;
using static Microsoft.DotNet.Cli.Build.Framework.BuildHelpers;
using static Microsoft.DotNet.Cli.Build.FS;

namespace Microsoft.DotNet.Host.Build
{
    public class CompileTargets
    {
        public static readonly string CoreCLRVersion = "1.0.2-rc2-24027";

        public static readonly bool IsWinx86 = CurrentPlatform.IsWindows && CurrentArchitecture.Isx86;
        public const string SharedFrameworkName = "Microsoft.NETCore.App";

        public static Crossgen CrossgenUtil = new Crossgen(CoreCLRVersion);

        public static string HostPackagePlatformRid => HostPackageSupportedRids[
                             (RuntimeEnvironment.OperatingSystemPlatform == Platform.Windows)
                             ? $"win7-{RuntimeEnvironment.RuntimeArchitecture}"
                             : RuntimeEnvironment.GetRuntimeIdentifier()];

        public static readonly Dictionary<string, string> HostPackageSupportedRids = new Dictionary<string, string>()
        {
            // Key: Current platform RID. Value: The actual publishable (non-dummy) package name produced by the build system for this RID.
            { "win7-x64", "win7-x64" },
            { "win7-x86", "win7-x86" },
            { "osx.10.10-x64", "osx.10.10-x64" },
            { "osx.10.11-x64", "osx.10.10-x64" },
            { "ubuntu.14.04-x64", "ubuntu.14.04-x64" },
            { "centos.7-x64", "rhel.7-x64" },
            { "rhel.7-x64", "rhel.7-x64" },
            { "rhel.7.2-x64", "rhel.7-x64" },
            { "debian.8-x64", "debian.8-x64" }
        };


        private static string DotnetHostBaseName => $"dotnet{Constants.ExeSuffix}";
        private static string DotnetHostFxrBaseName => $"{Constants.DynamicLibPrefix}hostfxr{Constants.DynamicLibSuffix}";
        private static string HostPolicyBaseName => $"{Constants.DynamicLibPrefix}hostpolicy{Constants.DynamicLibSuffix}";

        [Target(nameof(PrepareTargets.Init), 
            nameof(CompileCoreHost), 
            nameof(PackagePkgProjects),
            nameof(PublishSharedFramework))]
        public static BuildTargetResult Compile(BuildTargetContext c)
        {
            return c.Success();
        }

        // We need to generate stub host packages so we can restore our standalone test assets against the metapackage 
        // we built earlier in the build
        // https://github.com/dotnet/cli/issues/2438
        [Target]
        public static BuildTargetResult GenerateStubHostPackages(BuildTargetContext c)
        {
            var hostVersion = c.BuildContext.Get<HostVersion>("HostVersion");
            var currentRid = HostPackagePlatformRid;

            var stubPackageBuilder = new StubPackageBuilder(DotNetCli.Stage0, Dirs.Intermediate, Dirs.CorehostDummyPackages);

            foreach (var hostPackage in hostVersion.LatestHostPackages)
            {
                foreach (var rid in HostPackageSupportedRids.Values.Distinct())
                {
                    if (!rid.Equals(currentRid))
                    {
                        var basePackageId = hostPackage.Key;
                        var packageVersion = hostPackage.Value;

                        var packageId = $"runtime.{rid}.{basePackageId}";

                        stubPackageBuilder.GeneratePackage(packageId, packageVersion);
                    }
                }
            }
            return c.Success();
        }

        [Target]
        public static BuildTargetResult CompileCoreHost(BuildTargetContext c)
        {
            var hostVersion = c.BuildContext.Get<HostVersion>("HostVersion");

            // Generate build files
            var cmakeOut = Path.Combine(Dirs.CorehostLatest, "cmake");

            Rmdir(cmakeOut);
            Mkdirp(cmakeOut);

            var configuration = c.BuildContext.Get<string>("Configuration");

            // Run the build
            string rid = DotNetCli.Stage0.GetRuntimeId();
            string corehostSrcDir = Path.Combine(c.BuildContext.BuildDirectory, "src", "corehost");
            string commitHash = c.BuildContext.Get<string>("CommitHash");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Why does Windows directly call cmake but Linux/Mac calls "build.sh" in the corehost dir?
                // See the comment in "src/corehost/build.sh" for details. It doesn't work for some reason.
                var visualStudio = IsWinx86 ? "Visual Studio 14 2015" : "Visual Studio 14 2015 Win64";
                var archMacro = IsWinx86 ? "-DCLI_CMAKE_PLATFORM_ARCH_I386=1" : "-DCLI_CMAKE_PLATFORM_ARCH_AMD64=1";
                var ridMacro = $"-DCLI_CMAKE_RUNTIME_ID:STRING={rid}";
                var arch = IsWinx86 ? "x86" : "x64";
                var baseSupportedRid = $"win7-{arch}";
                var cmakeHostPolicyVer = $"-DCLI_CMAKE_HOST_POLICY_VER:STRING={hostVersion.LatestHostPolicyVersion}";
                var cmakeHostFxrVer = $"-DCLI_CMAKE_HOST_FXR_VER:STRING={hostVersion.LatestHostFxrVersion}";
                var cmakeBaseRid = $"-DCLI_CMAKE_PKG_RID:STRING={baseSupportedRid}";
                var cmakeCommitHash = $"-DCLI_CMAKE_COMMIT_HASH:STRING={commitHash}";

                ExecIn(cmakeOut, "cmake",
                    corehostSrcDir,
                    archMacro,
                    ridMacro,
                    cmakeHostFxrVer,
                    cmakeHostPolicyVer,
                    cmakeBaseRid,
                    cmakeCommitHash,
                    "-G",
                    visualStudio);

                var pf32 = RuntimeInformation.OSArchitecture == Architecture.X64 ?
                    Environment.GetEnvironmentVariable("ProgramFiles(x86)") :
                    Environment.GetEnvironmentVariable("ProgramFiles");

                if (configuration.Equals("Release"))
                {
                    // Cmake calls it "RelWithDebInfo" in the generated MSBuild
                    configuration = "RelWithDebInfo";
                }

                Exec(Path.Combine(pf32, "MSBuild", "14.0", "Bin", "MSBuild.exe"),
                    Path.Combine(cmakeOut, "ALL_BUILD.vcxproj"),
                    $"/p:Configuration={configuration}");

                // Copy the output out
                File.Copy(Path.Combine(cmakeOut, "cli", configuration, "dotnet.exe"), Path.Combine(Dirs.CorehostLatest, "dotnet.exe"), overwrite: true);
                File.Copy(Path.Combine(cmakeOut, "cli", configuration, "dotnet.pdb"), Path.Combine(Dirs.CorehostLatest, "dotnet.pdb"), overwrite: true);
                File.Copy(Path.Combine(cmakeOut, "cli", "dll", configuration, "hostpolicy.dll"), Path.Combine(Dirs.CorehostLatest, "hostpolicy.dll"), overwrite: true);
                File.Copy(Path.Combine(cmakeOut, "cli", "dll", configuration, "hostpolicy.pdb"), Path.Combine(Dirs.CorehostLatest, "hostpolicy.pdb"), overwrite: true);
                File.Copy(Path.Combine(cmakeOut, "cli", "fxr", configuration, "hostfxr.dll"), Path.Combine(Dirs.CorehostLatest, "hostfxr.dll"), overwrite: true);
                File.Copy(Path.Combine(cmakeOut, "cli", "fxr", configuration, "hostfxr.pdb"), Path.Combine(Dirs.CorehostLatest, "hostfxr.pdb"), overwrite: true);
            }
            else
            {
                ExecIn(cmakeOut, Path.Combine(c.BuildContext.BuildDirectory, "src", "corehost", "build.sh"),
                        "--arch",
                        "x64",
                        "--fxrver",
                        hostVersion.LatestHostFxrVersion,
                        "--policyver",
                        hostVersion.LatestHostPolicyVersion,
                        "--rid",
                        rid,
                        "--commithash",
                        commitHash);

                // Copy the output out
                File.Copy(Path.Combine(cmakeOut, "cli", "dotnet"), Path.Combine(Dirs.CorehostLatest, "dotnet"), overwrite: true);
                File.Copy(Path.Combine(cmakeOut, "cli", "dll", HostPolicyBaseName), Path.Combine(Dirs.CorehostLatest, HostPolicyBaseName), overwrite: true);
                File.Copy(Path.Combine(cmakeOut, "cli", "fxr", DotnetHostFxrBaseName), Path.Combine(Dirs.CorehostLatest, DotnetHostFxrBaseName), overwrite: true);
            }
            return c.Success();
        }

        [Target(nameof(CompileTargets.GenerateStubHostPackages))]
        public static BuildTargetResult PackagePkgProjects(BuildTargetContext c)
        {
            var arch = IsWinx86 ? "x86" : "x64";
            
            var hostVersion = c.BuildContext.Get<HostVersion>("HostVersion");
            var hostNugetversion = hostVersion.LatestHostVersion;
            var content = $@"{c.BuildContext["CommitHash"]}{Environment.NewLine}{hostNugetversion}{Environment.NewLine}";
            var pkgDir = Path.Combine(c.BuildContext.BuildDirectory, "pkg");
            File.WriteAllText(Path.Combine(pkgDir, "version.txt"), content);

            if (CurrentPlatform.IsWindows)
            {
                Command.Create(Path.Combine(pkgDir, "pack.cmd"))
                    // Workaround to arg escaping adding backslashes for arguments to .cmd scripts.
                    .Environment("__WorkaroundCliCoreHostBuildArch", arch)
                    .Environment("__WorkaroundCliCoreHostBinDir", Dirs.CorehostLatest)
                    .Environment("__WorkaroundCliCoreHostPolicyVer", hostVersion.LatestHostPolicyVersionNoSuffix)
                    .Environment("__WorkaroundCliCoreHostFxrVer", hostVersion.LatestHostFxrVersionNoSuffix)
                    .Environment("__WorkaroundCliCoreHostVer", hostVersion.LatestHostVersionNoSuffix)
                    .Environment("__WorkaroundCliCoreHostBuildMajor", hostVersion.LatestHostBuildMajor)
                    .Environment("__WorkaroundCliCoreHostVersionTag", hostVersion.LatestHostPrerelease)
                    .ForwardStdOut()
                    .ForwardStdErr()
                    .Execute()
                    .EnsureSuccessful();
            }
            else
            {
                Exec(Path.Combine(pkgDir, "pack.sh"),
                    "--arch",
                    "x64",
                    "--hostbindir",
                    Dirs.CorehostLatest,
                    "--policyver",
                    hostVersion.LatestHostPolicyVersionNoSuffix,
                    "--fxrver",
                    hostVersion.LatestHostFxrVersionNoSuffix,
                    "--hostver",
                    hostVersion.LatestHostVersionNoSuffix,
                    "--build",
                    hostVersion.LatestHostBuildMajor,
                    "--vertag",
                    hostVersion.LatestHostPrerelease);
            }
            foreach (var file in Directory.GetFiles(Path.Combine(pkgDir, "bin", "packages"), "*.nupkg"))
            {
                var fileName = Path.GetFileName(file);
                File.Copy(file, Path.Combine(Dirs.CorehostLocalPackages, fileName), true);

                Console.WriteLine($"Copying package {fileName} to artifacts directory {Dirs.CorehostLocalPackages}.");
            }
            foreach (var item in hostVersion.LatestHostPackages)
            {
                var fileFilter = $"runtime.{HostPackagePlatformRid}.{item.Key}.{item.Value}.nupkg";
                if (Directory.GetFiles(Dirs.CorehostLocalPackages, fileFilter).Length == 0)
                {
                    throw new BuildFailureException($"Nupkg for {fileFilter} was not created.");
                }
            }
            return c.Success();
        }

        [Target]
        public static BuildTargetResult PublishSharedFramework(BuildTargetContext c)
        {
            var outputDir = Dirs.Stage2;
            var dotnetCli = DotNetCli.Stage0;

            string SharedFrameworkTemplateSourceRoot = Path.Combine(Dirs.RepoRoot, "src", "sharedframework", "framework");
            string SharedFrameworkNugetVersion = c.BuildContext.Get<string>("SharedFrameworkNugetVersion");

            string sharedFrameworkRid;
            if (RuntimeEnvironment.OperatingSystemPlatform == Platform.Windows)
            {
                sharedFrameworkRid = $"win7-{RuntimeEnvironment.RuntimeArchitecture}";
            }
            else
            {
                sharedFrameworkRid = RuntimeEnvironment.GetRuntimeIdentifier();
            }

            string SharedFrameworkSourceRoot = GenerateSharedFrameworkProject(c, SharedFrameworkTemplateSourceRoot, sharedFrameworkRid);

            dotnetCli.Restore(
                "--verbosity", "verbose",
                "--disable-parallel",
                "--infer-runtimes",
                "--fallbacksource", Dirs.CorehostLocalPackages)
                .WorkingDirectory(SharedFrameworkSourceRoot)
                .Execute()
                .EnsureSuccessful();

            // We publish to a sub folder of the PublishRoot so tools like heat and zip can generate folder structures easier.
            string SharedFrameworkNameAndVersionRoot = Path.Combine(outputDir, "shared", SharedFrameworkName, SharedFrameworkNugetVersion);
            c.BuildContext["SharedFrameworkPath"] = SharedFrameworkNameAndVersionRoot;

            if (Directory.Exists(SharedFrameworkNameAndVersionRoot))
            {
                Utils.DeleteDirectory(SharedFrameworkNameAndVersionRoot);
            }

            dotnetCli.Publish(
                "--output", SharedFrameworkNameAndVersionRoot,
                "-r", sharedFrameworkRid,
                SharedFrameworkSourceRoot).Execute().EnsureSuccessful();

            // Clean up artifacts that dotnet-publish generates which we don't need
            DeleteMainPublishOutput(SharedFrameworkNameAndVersionRoot, "framework");
            File.Delete(Path.Combine(SharedFrameworkNameAndVersionRoot, "framework.runtimeconfig.json"));

            // Rename the .deps file
            var destinationDeps = Path.Combine(SharedFrameworkNameAndVersionRoot, $"{SharedFrameworkName}.deps.json");
            File.Move(Path.Combine(SharedFrameworkNameAndVersionRoot, "framework.deps.json"), destinationDeps);
            ChangeEntryPointLibraryName(destinationDeps, null);

            // Generate RID fallback graph
            string runtimeGraphGeneratorRuntime = null;
            switch (RuntimeEnvironment.OperatingSystemPlatform)
            {
                case Platform.Windows:
                    runtimeGraphGeneratorRuntime = "win";
                    break;
                case Platform.Linux:
                    runtimeGraphGeneratorRuntime = "linux";
                    break;
                case Platform.Darwin:
                    runtimeGraphGeneratorRuntime = "osx";
                    break;
            }
            if (!string.IsNullOrEmpty(runtimeGraphGeneratorRuntime))
            {
                var runtimeGraphGeneratorName = "RuntimeGraphGenerator";
                var runtimeGraphGeneratorProject = Path.Combine(Dirs.RepoRoot, "tools", runtimeGraphGeneratorName);
                var runtimeGraphGeneratorOutput = Path.Combine(Dirs.Output, "tools", runtimeGraphGeneratorName);

                dotnetCli.Publish(
                    "--output", runtimeGraphGeneratorOutput,
                    runtimeGraphGeneratorProject).Execute().EnsureSuccessful();
                var runtimeGraphGeneratorExe = Path.Combine(runtimeGraphGeneratorOutput, $"{runtimeGraphGeneratorName}{Constants.ExeSuffix}");

                Cmd(runtimeGraphGeneratorExe, "--project", SharedFrameworkSourceRoot, "--deps", destinationDeps, runtimeGraphGeneratorRuntime)
                    .Execute()
                    .EnsureSuccessful();
            }
            else
            {
                c.Error($"Could not determine rid graph generation runtime for platform {RuntimeEnvironment.OperatingSystemPlatform}");
            }

            File.Copy(
                Path.Combine(Dirs.CorehostLocked, DotnetHostBaseName),
                Path.Combine(SharedFrameworkNameAndVersionRoot, DotnetHostBaseName), true);
            File.Copy(
               Path.Combine(Dirs.CorehostLocked, DotnetHostBaseName),
               Path.Combine(SharedFrameworkNameAndVersionRoot, $"corehost{Constants.ExeSuffix}"), true);
            File.Copy(
                Path.Combine(Dirs.CorehostLocked, DotnetHostFxrBaseName),
                Path.Combine(SharedFrameworkNameAndVersionRoot, DotnetHostFxrBaseName), true);

            // Hostpolicy should be the latest and not the locked version as it is supposed to evolve for
            // the framework and has a tight coupling with coreclr's API in the framework.
            File.Copy(
                Path.Combine(Dirs.CorehostLatest, HostPolicyBaseName),
                Path.Combine(SharedFrameworkNameAndVersionRoot, HostPolicyBaseName), true);

            if (File.Exists(Path.Combine(SharedFrameworkNameAndVersionRoot, "mscorlib.ni.dll")))
            {
                // Publish already places the crossgen'd version of mscorlib into the output, so we can
                // remove the IL version
                File.Delete(Path.Combine(SharedFrameworkNameAndVersionRoot, "mscorlib.dll"));
            }

            CrossgenUtil.CrossgenDirectory(c, SharedFrameworkNameAndVersionRoot);

            // Generate .version file for sharedfx
            var version = SharedFrameworkNugetVersion;
            var content = $@"{c.BuildContext["CommitHash"]}{Environment.NewLine}{version}{Environment.NewLine}";
            File.WriteAllText(Path.Combine(SharedFrameworkNameAndVersionRoot, ".version"), content);

            return c.Success();
        }

        /// <summary>
        /// Generates the real shared framework project that will get published.
        /// </summary>
        /// <param name="sharedFrameworkTemplatePath">The "sharedFramework" source template folder.</param>
        private static string GenerateSharedFrameworkProject(BuildTargetContext c, string sharedFrameworkTemplatePath, string rid)
        {
            string sharedFrameworkProjectPath = Path.Combine(Dirs.Intermediate, "sharedFramework", "framework");
            Utils.DeleteDirectory(sharedFrameworkProjectPath);
            CopyRecursive(sharedFrameworkTemplatePath, sharedFrameworkProjectPath, true);

            string templateFile = Path.Combine(sharedFrameworkProjectPath, "project.json.template");
            JObject sharedFrameworkProject = JsonUtils.ReadProject(templateFile);
            sharedFrameworkProject["dependencies"]["Microsoft.NETCore.App"] = c.BuildContext.Get<string>("SharedFrameworkNugetVersion");
            ((JObject)sharedFrameworkProject["runtimes"]).RemoveAll();
            sharedFrameworkProject["runtimes"][rid] = new JObject();

            string projectJsonPath = Path.Combine(sharedFrameworkProjectPath, "project.json");
            JsonUtils.WriteProject(sharedFrameworkProject, projectJsonPath);

            Rm(templateFile);

            return sharedFrameworkProjectPath;
        }

        private static void ChangeEntryPointLibraryName(string depsFile, string newName)
        {
            JToken deps;
            using (var file = File.OpenText(depsFile))
            using (JsonTextReader reader = new JsonTextReader(file))
            {
                deps = JObject.ReadFrom(reader);
            }

            string version = null;
            foreach (JProperty target in deps["targets"])
            {
                var targetLibrary = target.Value.Children<JProperty>().FirstOrDefault();
                if (targetLibrary == null)
                {
                    continue;
                }
                version = targetLibrary.Name.Substring(targetLibrary.Name.IndexOf('/') + 1);
                if (newName == null)
                {
                    targetLibrary.Remove();
                }
                else
                {
                    targetLibrary.Replace(new JProperty(newName + '/' + version, targetLibrary.Value));
                }
            }
            if (version != null)
            {
                var library = deps["libraries"].Children<JProperty>().First();
                if (newName == null)
                {
                    library.Remove();
                }
                else
                {
                    library.Replace(new JProperty(newName + '/' + version, library.Value));
                }
                using (var file = File.CreateText(depsFile))
                using (var writer = new JsonTextWriter(file) { Formatting = Formatting.Indented })
                {
                    deps.WriteTo(writer);
                }
            }
        }

        private static void DeleteMainPublishOutput(string path, string name)
        {
            File.Delete(Path.Combine(path, $"{name}{Constants.ExeSuffix}"));
            File.Delete(Path.Combine(path, $"{name}.dll"));
            File.Delete(Path.Combine(path, $"{name}.pdb"));
        }
    }
}
