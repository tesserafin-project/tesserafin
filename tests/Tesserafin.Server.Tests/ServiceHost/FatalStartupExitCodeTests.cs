using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Tesserafin.Common.Net;
using Xunit;

namespace Tesserafin.Server.Tests.ServiceHost;

/// <summary>
/// The exit contract from <c>docs/distribution/W0-windows-server.md</c> §2.5 and §4: a fatal
/// startup must leave a non-zero process exit code, and an ordinary exit must keep leaving zero.
/// </summary>
/// <remarks>
/// <para>
/// These run the real published entry point as a child process, because the defect they guard is a
/// property of the process and not of a method. §2.5 measured the unmodified server logging
/// <c>FfmpegException: Failed to find valid ffmpeg</c> and then <b>exiting 0</b>, which a Service
/// Control Manager reads as "stopped normally": no failure action, no record that the server never
/// came up. An in-process assertion cannot see that, because the exit code is written by the host
/// on the way out.
/// </para>
/// <para>
/// No Service Control Manager is required or used. The SCM half of the contract — that the same
/// failure is visible as a non-zero service exit code — is hosted evidence and belongs to
/// <c>.github/workflows/w3-windows-service-host.yml</c>; what is proved here is the process-level
/// half, on whatever platform the suite runs.
/// </para>
/// </remarks>
public sealed class FatalStartupExitCodeTests
{
    /// <summary>
    /// A cold first start applies every database migration. W0 §2.3 allowed 600 s for that on a
    /// hosted Windows runner; this bound is generous rather than tight because a timeout here must
    /// mean "the server never finished", not "the runner was busy".
    /// </summary>
    private static readonly TimeSpan _startupBudget = TimeSpan.FromMinutes(6);

    [Fact]
    public async Task MissingEncoder_FatalStartup_ExitsNonZero()
    {
        using var sandbox = new ServerSandbox();

        // The port matters even though the failure does not involve it. The encoder check runs in
        // RunStartupTasksAsync, which is reached only after Kestrel has bound, so leaving the
        // default 8096 in place would let anything else already listening on this host turn a
        // proven encoder failure into an unexplained one.
        sandbox.PinHttpPort(FreeTcpPort());

        var result = await sandbox.RunAsync(Array.Empty<string>());

        Assert.False(
            result.TimedOut,
            $"The server never exited within {_startupBudget}. Output:\n{result.Output}");

        // Both halves are load-bearing. The exit code alone would also be satisfied by a Kestrel
        // bind failure or a bad path, and a test that accepts any failure would keep passing if the
        // encoder path stopped being fatal at all.
        Assert.Contains("FfmpegException", result.Output, StringComparison.Ordinal);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task OrdinaryExit_StillExitsZero()
    {
        using var sandbox = new ServerSandbox();

        // `--mode MigrateSystem` runs the migrations and shuts down without starting Kestrel and
        // without running the startup tasks, so it needs no encoder and binds no port: an exit that
        // is normal by construction, on the same tree and the same absent PATH as the test above.
        // It is the control that says the non-zero code is a property of the failure and not a new
        // property of exiting.
        var result = await sandbox.RunAsync(["--mode", "MigrateSystem"]);

        Assert.False(
            result.TimedOut,
            $"The server never exited within {_startupBudget}. Output:\n{result.Output}");
        Assert.Equal(0, result.ExitCode);
    }

    private static int FreeTcpPort()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)listener.LocalEndPoint!).Port;
    }

    private sealed record RunResult(int ExitCode, string Output, bool TimedOut);

    /// <summary>
    /// Fresh state directories, a PATH that contains nothing, and the server assembly that this
    /// test project already builds against.
    /// </summary>
    private sealed class ServerSandbox : IDisposable
    {
        private readonly string _root;

        internal ServerSandbox()
        {
            _root = Path.Combine(Path.GetTempPath(), "tesserafin-w3a0-" + Guid.NewGuid().ToString("N"));
            foreach (var name in new[] { "config", "data", "cache", "log", "emptypath" })
            {
                Directory.CreateDirectory(Path.Combine(_root, name));
            }
        }

        private string ConfigDir => Path.Combine(_root, "config");

        internal void PinHttpPort(int port)
        {
            // Written through the real configuration type rather than as hand-shaped XML, so the
            // element names cannot drift away from what the server deserialises.
            var configuration = new NetworkConfiguration { InternalHttpPort = port };
            var path = Path.Combine(ConfigDir, NetworkConfigurationStore.StoreKey + ".xml");
            using var stream = File.Create(path);
            new XmlSerializer(typeof(NetworkConfiguration)).Serialize(stream, configuration);
        }

        internal async Task<RunResult> RunAsync(IReadOnlyList<string> extraArguments)
        {
            var serverAssembly = Path.Combine(AppContext.BaseDirectory, "tesserafin.dll");
            Assert.True(
                File.Exists(serverAssembly),
                $"The server assembly was not copied next to the tests: {serverAssembly}");

            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveDotnetHost(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = _root
            };

            startInfo.ArgumentList.Add(serverAssembly);
            startInfo.ArgumentList.Add("--nowebclient");
            startInfo.ArgumentList.Add("--configdir");
            startInfo.ArgumentList.Add(ConfigDir);
            startInfo.ArgumentList.Add("--datadir");
            startInfo.ArgumentList.Add(Path.Combine(_root, "data"));
            startInfo.ArgumentList.Add("--cachedir");
            startInfo.ArgumentList.Add(Path.Combine(_root, "cache"));
            startInfo.ArgumentList.Add("--logdir");
            startInfo.ArgumentList.Add(Path.Combine(_root, "log"));
            foreach (var argument in extraArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            // No `--ffmpeg`, no `TESSERAFIN_FFmpeg__path`, and a PATH holding one empty directory.
            // That is W0 §2.5's negative control: the three ways MediaEncoder.SetFFmpegPath can find
            // an encoder are all closed, so it must fail, and it must fail for that reason.
            foreach (var key in new List<string>(startInfo.Environment.Keys))
            {
                if (key.StartsWith("TESSERAFIN_", StringComparison.OrdinalIgnoreCase))
                {
                    startInfo.Environment.Remove(key);
                }
            }

            startInfo.Environment["PATH"] = Path.Combine(_root, "emptypath");
            startInfo.Environment["DOTNET_ROOT"] = DotnetRoot();
            startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            startInfo.Environment["DOTNET_NOLOGO"] = "1";

            using var process = new Process { StartInfo = startInfo };
            var output = new StringBuilder();
            process.OutputDataReceived += (_, e) => Append(output, e.Data);
            process.ErrorDataReceived += (_, e) => Append(output, e.Data);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = new CancellationTokenSource(_startupBudget);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It exited between the timeout and the kill; the exit code below is still real.
                }

                return new RunResult(-1, output.ToString(), true);
            }

            return new RunResult(process.ExitCode, output.ToString(), false);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover sandbox in the temp directory is not worth failing a green run over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void Append(StringBuilder builder, string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (builder)
            {
                builder.AppendLine(line);
            }
        }

        private static string DotnetRoot()
        {
            // .../dotnet/shared/Microsoft.NETCore.App/<version>/ -> .../dotnet
            var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
            return Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
        }

        private static string ResolveDotnetHost()
        {
            // Never a bare "dotnet": the child is launched with an empty PATH on purpose, and the
            // parent's PATH is not consulted for an absolute file name.
            var fromMsBuild = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrEmpty(fromMsBuild) && File.Exists(fromMsBuild))
            {
                return fromMsBuild;
            }

            var candidate = Path.Combine(
                DotnetRoot(),
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            Assert.True(
                File.Exists(candidate),
                string.Create(CultureInfo.InvariantCulture, $"No dotnet host found at {candidate}"));
            return candidate;
        }
    }
}
