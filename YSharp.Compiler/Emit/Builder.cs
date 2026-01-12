using System.Diagnostics;

namespace YSharp.Compiler.Emit;

/// <summary>
/// Builds C# code into .NET assemblies or native binaries.
/// </summary>
public static class Builder
{
    /// <summary>
    /// Build to a .dll (requires dotnet to run).
    /// </summary>
    public static void BuildDll(string csharpSource, string outputPath, bool useDi = false)
    {
        var assemblyName = Path.GetFileNameWithoutExtension(outputPath);
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".";

        using var tempDir = new TempDirectory();
        var projectDir = Path.Combine(tempDir.Path, assemblyName);
        Directory.CreateDirectory(projectDir);

        // Write source
        File.WriteAllText(Path.Combine(projectDir, "Program.cs"), csharpSource);

        // Write csproj
        var diPackage = useDi
            ? "\n    <PackageReference Include=\"Microsoft.Extensions.DependencyInjection\" Version=\"9.0.0\" />"
            : "";

        File.WriteAllText(Path.Combine(projectDir, $"{assemblyName}.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net9.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>{diPackage}
              </ItemGroup>
            </Project>
            """);

        // Build (use publish when dependencies exist to include them)
        if (useDi)
        {
            RunDotnet(projectDir, "publish -c Release --nologo -v q");

            // Copy all published outputs
            var publishDir = Path.Combine(projectDir, "bin", "Release", "net9.0", "publish");
            foreach (var file in Directory.GetFiles(publishDir))
            {
                var destPath = Path.Combine(outputDir, Path.GetFileName(file));
                File.Copy(file, destPath, overwrite: true);
            }
        }
        else
        {
            RunDotnet(projectDir, "build -c Release --nologo -v q");

            // Copy output
            var builtDll = Path.Combine(projectDir, "bin", "Release", "net9.0", $"{assemblyName}.dll");
            var builtConfig = Path.Combine(projectDir, "bin", "Release", "net9.0", $"{assemblyName}.runtimeconfig.json");

            File.Copy(builtDll, outputPath, overwrite: true);
            File.Copy(builtConfig, Path.ChangeExtension(outputPath, ".runtimeconfig.json"), overwrite: true);
        }
    }

    /// <summary>
    /// Build to a native binary (self-contained, no dotnet required).
    /// </summary>
    public static void BuildBinary(string csharpSource, string outputPath, bool useDi = false)
    {
        var assemblyName = Path.GetFileNameWithoutExtension(outputPath);
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".";
        var rid = GetRuntimeIdentifier();

        using var tempDir = new TempDirectory();
        var projectDir = Path.Combine(tempDir.Path, assemblyName);
        Directory.CreateDirectory(projectDir);

        // Write source
        File.WriteAllText(Path.Combine(projectDir, "Program.cs"), csharpSource);

        // Write csproj
        var diPackage = useDi
            ? "\n    <PackageReference Include=\"Microsoft.Extensions.DependencyInjection\" Version=\"9.0.0\" />"
            : "";

        File.WriteAllText(Path.Combine(projectDir, $"{assemblyName}.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net9.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <PublishSingleFile>true</PublishSingleFile>
                <SelfContained>true</SelfContained>
              </PropertyGroup>
              <ItemGroup>{diPackage}
              </ItemGroup>
            </Project>
            """);

        // Publish
        RunDotnet(projectDir, $"publish -c Release -r {rid} --nologo -v q");

        // Copy binary
        var binaryName = rid.StartsWith("win") ? $"{assemblyName}.exe" : assemblyName;
        var publishedBinary = Path.Combine(projectDir, "bin", "Release", "net9.0", rid, "publish", binaryName);
        var finalPath = Path.Combine(outputDir, binaryName);

        File.Copy(publishedBinary, finalPath, overwrite: true);

        // Make executable on Unix
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(finalPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
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
        {
            throw new InvalidOperationException($"dotnet {arguments} failed:\n{stderr}\n{stdout}");
        }
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

    /// <summary>
    /// Helper class to manage temp directory cleanup.
    /// </summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"yas_{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* ignore cleanup errors */ }
        }
    }
}
