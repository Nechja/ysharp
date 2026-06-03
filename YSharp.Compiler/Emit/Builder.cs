using System.Diagnostics;

namespace YSharp.Compiler.Emit;

public static class Builder
{
    public static void BuildDll(string csharpSource, string outputPath, bool useDi = false, bool useWeb = false)
    {
        var (assemblyName, projectDir, tempDir, outputDir) = SetupProject(csharpSource, outputPath, useWeb, useDi, aot: false);
        using (tempDir)
        {
            // For DI / web projects we need publish (so the deps fall into one folder).
            // For plain projects, a plain build + copying the dll is enough.
            if (useDi || useWeb)
            {
                RunDotnet(projectDir, "publish -c Release --nologo -v q");
                var publishDir = Path.Combine(projectDir, "bin", "Release", "net10.0", "publish");
                foreach (var file in Directory.GetFiles(publishDir))
                    File.Copy(file, Path.Combine(outputDir, Path.GetFileName(file)), overwrite: true);
            }
            else
            {
                RunDotnet(projectDir, "build -c Release --nologo -v q");
                var built = Path.Combine(projectDir, "bin", "Release", "net10.0", $"{assemblyName}.dll");
                File.Copy(built, outputPath, overwrite: true);
                File.Copy(Path.ChangeExtension(built, ".runtimeconfig.json"),
                          Path.ChangeExtension(outputPath, ".runtimeconfig.json"), overwrite: true);
            }
        }
    }

    /// <summary>Build to a native AOT binary (self-contained, no dotnet required to run).</summary>
    public static void BuildBinary(string csharpSource, string outputPath, bool useDi = false, bool useWeb = false)
    {
        var (assemblyName, projectDir, tempDir, outputDir) = SetupProject(csharpSource, outputPath, useWeb, useDi, aot: true);
        using (tempDir)
        {
            var rid = GetRuntimeIdentifier();
            // AOT runs ILC -- slower than a JIT build, but produces a small
            // native binary with no .NET runtime dependency.
            RunDotnet(projectDir, $"publish -c Release -r {rid} --nologo -v q");

            var binaryName = rid.StartsWith("win") ? $"{assemblyName}.exe" : assemblyName;
            var published = Path.Combine(projectDir, "bin", "Release", "net10.0", rid, "publish", binaryName);
            var finalPath = Path.Combine(outputDir, binaryName);
            File.Copy(published, finalPath, overwrite: true);

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(finalPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    // Writes Program.cs + csproj to a fresh temp dir. Returns the bits both build paths share.
    private static (string assemblyName, string projectDir, IDisposable tempDir, string outputDir)
        SetupProject(string csharpSource, string outputPath, bool useWeb, bool useDi, bool aot)
    {
        var assemblyName = Path.GetFileNameWithoutExtension(outputPath);
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".";

        var tempDir = new TempDirectory();
        var projectDir = Path.Combine(tempDir.Path, assemblyName);
        Directory.CreateDirectory(projectDir);

        File.WriteAllText(Path.Combine(projectDir, "Program.cs"), csharpSource);
        File.WriteAllText(Path.Combine(projectDir, $"{assemblyName}.csproj"), CsprojFor(useWeb, useDi, aot));
        return (assemblyName, projectDir, tempDir, outputDir);
    }

    private static string CsprojFor(bool useWeb, bool useDi, bool aot)
    {
        var sdk = useWeb ? "Microsoft.NET.Sdk.Web" : "Microsoft.NET.Sdk";
        var diPackage = useDi
            ? "\n    <PackageReference Include=\"Microsoft.Extensions.DependencyInjection\" Version=\"9.0.0\" />"
            : "";
        var aotProps = aot
            ? "\n    <PublishAot>true</PublishAot>\n    <InvariantGlobalization>true</InvariantGlobalization>\n    <StripSymbols>true</StripSymbols>"
            : "";

        return $"""
            <Project Sdk="{sdk}">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>{aotProps}
              </PropertyGroup>
              <ItemGroup>{diPackage}
              </ItemGroup>
            </Project>
            """;
    }

    private static void RunDotnet(string workingDir, string arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"dotnet {arguments} failed:\n{stderr}\n{stdout}");
    }

    private static string GetRuntimeIdentifier()
    {
        if (OperatingSystem.IsWindows())
            return Environment.Is64BitOperatingSystem ? "win-x64" : "win-x86";
        if (OperatingSystem.IsLinux())
            return Environment.Is64BitOperatingSystem ? "linux-x64" : "linux-arm";
        if (OperatingSystem.IsMacOS())
            return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                   System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        return "linux-x64";
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"yas_{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* cleanup is best-effort */ }
        }
    }
}
