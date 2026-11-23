#:property PublishAot=false
using System.Diagnostics;
using System.Text.Json;

var environment = File.Exists("environment.json") ? JsonSerializer.Deserialize<BuildEnvironment>(File.ReadAllText("environment.json")) ?? new() : new();
var output = Path.GetFullPath(environment.Poe1);
Directory.CreateDirectory(output);
using var cache = Process.Start("dotnet", "run --file src/ninja-cache.cs")!;
cache.WaitForExit();
if (cache.ExitCode != 0) return cache.ExitCode;
Directory.SetCurrentDirectory("src");
foreach (var tier in new[] { "normal", "strict", "max" })
{
    using var build = Process.Start(new ProcessStartInfo("poefilterx", ["build", "--profile", $"poe1-{tier}", "--output", Path.Combine(output, $"poe1-{tier}.filter")]))!;
    build.WaitForExit();
    if (build.ExitCode != 0) return build.ExitCode;
}
return 0;

record BuildEnvironment(string Poe1 = ".", string Poe2 = ".");
