using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using SteamSmart.Desktop;

internal static class TestRuntime
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static readonly List<string> Passed = new List<string>();
    private static string Root;
    private static string Engine;
    private static string Data;
    private static string Hosts;
    private const string OriginalHosts = "# isolated native controller test\r\n192.0.2.45 keep.example\r\n198.51.100.9 dl.steam.test # original\r\n";
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAILED: " + name);
        Passed.Add(name);
        Console.WriteLine("PASS " + name);
    }
    private static async Task Until(Func<bool> condition, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            if (condition()) return;
            await Task.Delay(75);
        }
        throw new TimeoutException("Expected controller transition did not complete.");
    }
    private static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    private static string FixtureConfig()
    {
        return Json.Serialize(new {
            version = 1, skipLegacy = true, skipWarmup = true,
            hostsPath = Hosts, listen = new { host = "127.0.0.1", port = Port() },
            groups = new[] { new { id = "test", name = "Test CDN", target = "cdn.steam.test:80", hosts = new[] { "dl.steam.test" } } },
            redirects = new Dictionary<string, string>()
        });
    }
    private static string ResolveEngineDirectory(string requestedDirectory)
    {
        if (!String.IsNullOrWhiteSpace(requestedDirectory)) return Path.GetFullPath(requestedDirectory);
        foreach (string start in new[] { AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(Path.GetFullPath(start)); directory != null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "engine");
                if (File.Exists(Path.Combine(candidate, "smart-daemon.mjs"))) return candidate;
            }
        }
        throw new DirectoryNotFoundException("Cannot find the repository engine directory. Pass it as the second argument.");
    }
    private static string ResolveNodePath(string requestedPath)
    {
        if (!String.IsNullOrWhiteSpace(requestedPath))
        {
            string fullPath = Path.GetFullPath(requestedPath);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("The specified Node.js executable does not exist.", fullPath);
            return fullPath;
        }
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
            if (String.IsNullOrWhiteSpace(directory)) continue;
            string candidate = Path.Combine(directory, "node.exe");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException("Cannot find node.exe on PATH. Install Node.js or pass its path as the third argument.");
    }
    private static async Task Run(string baseDirectory, string sourceEngineDirectory, string nodePath)
    {
        Root = Path.Combine(baseDirectory, "runtime-fixture-" + Guid.NewGuid().ToString("N"));
        Engine = Path.Combine(Root, "engine");
        Data = Path.Combine(Root, "data");
        Directory.CreateDirectory(Engine);
        Directory.CreateDirectory(Data);
        Hosts = Path.Combine(Data, "hosts.fixture");
        File.WriteAllText(Hosts, OriginalHosts);
        foreach (string name in new[] { "smart-daemon.mjs", "smart-engine.mjs", "hosts-manager.mjs", "smart-config-manager.mjs", "smart-config-cli.mjs", "SteamSmartBridge.ps1" })
            File.Copy(Path.Combine(sourceEngineDirectory, name), Path.Combine(Engine, name));
        File.Copy(nodePath, Path.Combine(Engine, "node.exe"));
        string originalConfig = FixtureConfig();
        string configPath = Path.Combine(Data, "smart-config.json");
        File.WriteAllText(configPath, originalConfig);
        File.WriteAllText(Path.Combine(Engine, "smart-config.defaults.json"), originalConfig);
        using (var controller = new RuntimeController(Engine, Data))
        {
            try
            {
                Check(File.ReadAllText(Hosts) == OriginalHosts, "constructor_does_not_modify_hosts");
                Check(File.ReadAllText(configPath) == originalConfig, "existing_config_is_preserved");
                var view = await controller.ReadDomainsAsync();
                Check(view.rows.Count == 1 && view.rows[0].domain == "dl.steam.test", "read_real_config_cli");
                await controller.StartAsync();
                await Until(() => controller.IsRunning, 12000);
                Check(File.ReadAllText(Hosts).Contains("127.0.0.1 dl.steam.test"), "start_acquires_only_fixture_mapping");
                Check(File.ReadAllText(Hosts).Contains("192.0.2.45 keep.example"), "unrelated_mapping_is_preserved");
                await controller.StopAsync();
                Check(!controller.IsRunning && File.ReadAllText(Hosts) == OriginalHosts, "manual_stop_restores_hosts_and_keeps_controller_usable");
                await controller.StartAsync();
                await Until(() => controller.IsRunning, 12000);
                int previousPid = controller.Status.pid;
                bool rejected = false;
                try { await controller.ApplyDomainsAsync(new List<DomainRow> { new DomainRow { domain = "https://invalid.example/file", groupId = "test", kind = "download" } }); }
                catch { rejected = true; }
                Check(rejected && controller.IsRunning && controller.Status.pid == previousPid, "invalid_apply_does_not_interrupt_service");
                Check(File.ReadAllText(configPath) == originalConfig, "invalid_apply_does_not_save_config");
                await controller.ApplyDomainsAsync(new List<DomainRow> {
                    new DomainRow { domain = "new.dl.steam.test", groupId = "test", kind = "download" },
                    new DomainRow { domain = "entry.steam.test", groupId = "test", kind = "redirect" }
                });
                await Until(() => controller.IsRunning, 12000);
                Check(controller.Status.pid != previousPid, "valid_apply_restarts_backend");
                string activeHosts = File.ReadAllText(Hosts);
                Check(activeHosts.Contains("127.0.0.1 new.dl.steam.test") && activeHosts.Contains("127.0.0.1 entry.steam.test"), "new_download_and_redirect_domains_are_applied");
                Check(activeHosts.Contains("198.51.100.9 dl.steam.test # original"), "removed_domain_original_mapping_is_restored");
                Check(File.ReadAllText(Path.Combine(Data, "smart-config.previous.json")) == originalConfig, "previous_config_backup_is_exact");
                previousPid = controller.Status.pid;
                using (var child = Process.GetProcessById(previousPid))
                {
                    Check(child.ProcessName == "node", "crash_test_targets_only_owned_node");
                    child.Kill();
                }
                await Until(() => controller.IsRunning && controller.Status.pid != previousPid, 18000);
                Check(controller.IsRunning, "unexpected_backend_exit_is_restarted");
                Check(File.ReadAllText(Hosts).Contains("127.0.0.1 new.dl.steam.test"), "restart_reuses_recoverable_mapping");
                using (var stopper = new RuntimeController(Engine, Data)) { await stopper.StopAsync(); }
                await Until(() => !controller.IsRunning, 5000);
                await Task.Delay(6000);
                Check(!controller.IsRunning && File.ReadAllText(Hosts) == OriginalHosts, "external_explicit_stop_is_not_automatically_reversed");
                await controller.StartAsync();
                await Until(() => controller.IsRunning, 12000);
                Task apply = controller.ApplyDomainsAsync(new List<DomainRow> { new DomainRow { domain = "cancel.dl.steam.test", groupId = "test", kind = "download" } });
                await Task.Delay(15);
                Task shutdown = controller.ShutdownAsync();
                try { await apply; } catch { }
                await shutdown;
                await Task.Delay(1500);
                Check(!controller.IsRunning && File.ReadAllText(Hosts) == OriginalHosts, "shutdown_during_apply_leaves_no_running_proxy_or_mapping");
                string statusPath = Path.Combine(Data, "runtime", "status.json");
                var finalStatus = Json.Deserialize<StatusSnapshot>(File.ReadAllText(statusPath));
                Check(finalStatus.state == "stopped" && finalStatus.restoreResult.hostsReleased, "shutdown_reports_confirmed_restoration");
                Check(finalStatus.totalBytes == 0 && finalStatus.probeBytes == 0, "integration_test_produces_no_download_traffic");
            }
            finally { try { controller.ShutdownAsync().GetAwaiter().GetResult(); } catch { } }
        }
        File.WriteAllText(Path.Combine(Root, "controller-test-result.json"), Json.Serialize(new { ok = true, checks = Passed, fixture = Root }));
        Console.WriteLine("RESULT " + Passed.Count + " checks passed; " + Root);
    }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length > 3) throw new ArgumentException("Usage: TestRuntime.exe [fixture-base-directory] [engine-directory] [node-exe]");
            Run(args.Length > 0 ? Path.GetFullPath(args[0]) : AppDomain.CurrentDomain.BaseDirectory,
                ResolveEngineDirectory(args.Length > 1 ? args[1] : null),
                ResolveNodePath(args.Length > 2 ? args[2] : null)).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
