using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SteamSmart.Desktop
{
    public sealed class DomainRow : INotifyPropertyChanged
    {
        private string _domain = "";
        private string _groupId = "";
        private string _kind = "download";
        public string domain { get { return _domain; } set { Set(ref _domain, value, "domain"); } }
        public string groupId { get { return _groupId; } set { Set(ref _groupId, value, "groupId"); } }
        public string kind { get { return _kind; } set { Set(ref _kind, value, "kind"); } }
        public event PropertyChangedEventHandler PropertyChanged;
        private void Set(ref string field, string value, string name)
        {
            if (field == value) return;
            field = value;
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class SourceOption
    {
        public string id { get; set; }
        public string name { get; set; }
        public string target { get; set; }
    }
    public sealed class ConfigView
    {
        public bool ok { get; set; }
        public List<SourceOption> groups { get; set; }
        public List<DomainRow> rows { get; set; }
        public ConfigView() { groups = new List<SourceOption>(); rows = new List<DomainRow>(); }
    }
    public sealed class StatusSnapshot
    {
        public string runId { get; set; }
        public int pid { get; set; }
        public int parentPid { get; set; }
        public string startedAt { get; set; }
        public string state { get; set; }
        public string message { get; set; }
        public double totalBytes { get; set; }
        public double probeBytes { get; set; }
        public double totalRateBps { get; set; }
        public List<GroupSnapshot> groups { get; set; }
        public List<EventSnapshot> events { get; set; }
        public RestoreResult restoreResult { get; set; }
        public StatusSnapshot() { state = "stopped"; groups = new List<GroupSnapshot>(); events = new List<EventSnapshot>(); }
    }
    public sealed class GroupSnapshot
    {
        public string id { get; set; }
        public string name { get; set; }
        public double rateBps { get; set; }
        public bool testing { get; set; }
        public List<IpSnapshot> ips { get; set; }
        public GroupSnapshot() { ips = new List<IpSnapshot>(); }
    }
    public sealed class IpSnapshot
    {
        public string ip { get; set; }
        public double rateBps { get; set; }
        public double scoreMbps { get; set; }
        public bool valid { get; set; }
        public string state { get; set; }
        public bool selected { get; set; }
    }
    public sealed class EventSnapshot
    {
        public string at { get; set; }
        public string message { get; set; }
    }
    public sealed class RestoreResult
    {
        public bool legacyRestored { get; set; }
        public bool hostsReleased { get; set; }
        public int conflicts { get; set; }
    }

    /// <summary>Owns only its daemon session; all hosts edits remain inside the existing engine.</summary>
    public sealed class RuntimeController : IDisposable
    {
        private readonly string _engineDir, _dataDir, _node, _daemon, _cli, _defaults, _config;
        private readonly string _runtime, _statusFile, _controlFile;
        private readonly object _sync = new object();
        private readonly SemaphoreSlim _lifecycle = new SemaphoreSlim(1, 1);
        private readonly Timer _timer;
        private StatusSnapshot _status = new StatusSnapshot();
        private string _message = "尚未启动 Steam 自动优选。";
        private bool _busy, _running;
        private volatile bool _disposed, _shutdownRequested, _desiredRunning;
        private int _stopVersion, _polling, _expectedPid, _restartAttempts;
        private bool _hadRunning;
        private DateTime? _restartAt;
        private Process _child;
        private string _lastStopRun = "";

        public event EventHandler Changed;
        public string DataDirectory { get { return _dataDir; } }
        public StatusSnapshot Status { get { lock (_sync) return _status; } }
        public string Message { get { lock (_sync) return _message; } }
        public bool IsBusy { get { lock (_sync) return _busy; } }
        public bool IsRunning
        {
            get
            {
                StatusSnapshot snapshot;
                bool running;
                lock (_sync) { snapshot = _status; running = _running; }
                return running && snapshot.state == "running" && MatchingProcess(snapshot);
            }
        }

        public RuntimeController(string engineDir, string dataDir)
        {
            try
            {
                _engineDir = Path.GetFullPath(engineDir);
                _dataDir = Path.GetFullPath(dataDir);
                _node = Path.Combine(_engineDir, "node.exe");
                _daemon = Path.Combine(_engineDir, "smart-daemon.mjs");
                _cli = Path.Combine(_engineDir, "smart-config-cli.mjs");
                _defaults = Path.Combine(_engineDir, "smart-config.defaults.json");
                _config = Path.Combine(_dataDir, "smart-config.json");
                _runtime = Path.Combine(_dataDir, "runtime");
                _statusFile = Path.Combine(_runtime, "status.json");
                _controlFile = Path.Combine(_runtime, "control.json");
                Directory.CreateDirectory(_runtime);
                if (!File.Exists(_config))
                {
                    if (!File.Exists(_defaults)) throw new InvalidOperationException("缺少默认下载源配置，请重新安装程序。");
                    try { File.Copy(_defaults, _config, false); }
                    catch (IOException) { if (!File.Exists(_config)) throw; }
                }
                StatusSnapshot current = ReadStatus();
                if (current != null) Publish(current, MatchingProcess(current), null);
                _timer = new Timer(delegate { Poll(); }, null, 800, 800);
            }
            catch (Exception error) { throw Safe(error, "无法准备运行数据，请检查目录权限后重试。"); }
        }

        public async Task StartAsync()
        {
            ThrowIfDisposed();
            if (_shutdownRequested) throw new InvalidOperationException("程序正在退出，请稍后重新启动。");
            int version = Volatile.Read(ref _stopVersion);
            _desiredRunning = true;
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                SetBusy(true);
                _restartAttempts = 0;
                _restartAt = null;
                if (WantRunning(version)) await StartCoreAsync(version).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _desiredRunning = false;
                InvalidOperationException safe = Safe(error, "服务未能启动，请点击停止并恢复后重试。");
                SetMessage(safe.Message);
                throw safe;
            }
            finally { SetBusy(false); _lifecycle.Release(); }
        }

        public async Task StopAsync()
        {
            ThrowIfDisposed();
            RequestStopIntent(false);
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try { SetBusy(true); await StopCoreAsync("user-stop").ConfigureAwait(false); }
            catch (Exception error)
            {
                InvalidOperationException safe = Safe(error, "恢复尚未完成，请再次点击停止并恢复。");
                SetMessage(safe.Message); throw safe;
            }
            finally { SetBusy(false); _lifecycle.Release(); }
        }

        public async Task ShutdownAsync()
        {
            if (_disposed) return;
            RequestStopIntent(true);
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try { SetBusy(true); await StopCoreAsync("user-stop").ConfigureAwait(false); }
            catch (Exception error)
            {
                InvalidOperationException safe = Safe(error, "退出前未能确认恢复完成，请重试停止并恢复。");
                SetMessage(safe.Message); throw safe;
            }
            finally { SetBusy(false); _lifecycle.Release(); }
        }

        public async Task<ConfigView> ReadDomainsAsync()
        {
            ThrowIfDisposed();
            string json = await RunCliAsync("read", null).ConfigureAwait(false);
            return DeserializeView(json);
        }

        public async Task<ConfigView> DefaultDomainsAsync()
        {
            ThrowIfDisposed();
            string json = await RunCliAsync("defaults", null).ConfigureAwait(false);
            return DeserializeView(json);
        }

        public async Task ValidateDomainsAsync(List<DomainRow> rows)
        {
            ThrowIfDisposed();
            string input = WriteRows(rows);
            try { await RunCliAsync("validate", input).ConfigureAwait(false); }
            finally { DeleteInput(input); }
        }

        public async Task ApplyDomainsAsync(List<DomainRow> rows)
        {
            ThrowIfDisposed();
            if (_shutdownRequested) throw new InvalidOperationException("程序正在退出，设置尚未应用。");
            int version = Volatile.Read(ref _stopVersion);
            string input = WriteRows(rows);
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                SetBusy(true);
                // Validation precedes any stop intent or hosts transition.
                await RunCliAsync("validate", input).ConfigureAwait(false);
                if (_shutdownRequested || version != Volatile.Read(ref _stopVersion))
                    throw new InvalidOperationException("设置应用已取消，正在停止并恢复服务。");
                _desiredRunning = true;
                SetMessage("正在恢复旧域名映射，完成后应用新的下载域名…");
                await StopCoreAsync("domain-settings").ConfigureAwait(false);
                if (!WantRunning(version)) throw new InvalidOperationException("设置应用已取消，服务已停止并恢复。");
                Exception saveError = null;
                try { await RunCliAsync("save", input).ConfigureAwait(false); }
                catch (Exception error) { saveError = error; }
                if (saveError != null)
                {
                    string reason = Safe(saveError, "下载域名设置未能保存。").Message;
                    if (WantRunning(version))
                    {
                        SetMessage("保存失败，正在重新启动原配置…");
                        try { await StartCoreAsync(version).ConfigureAwait(false); }
                        catch { throw new InvalidOperationException(reason + " 原配置尚未重新启动，请点击启动重试。"); }
                    }
                    throw new InvalidOperationException(reason);
                }
                if (!WantRunning(version)) throw new InvalidOperationException("设置已保存，服务已按停止请求恢复并关闭。");
                _restartAttempts = 0;
                _restartAt = null;
                await StartCoreAsync(version).ConfigureAwait(false);
                if (WantRunning(version)) SetMessage("下载域名设置已应用，正在等待 Steam 下载。");
            }
            catch (Exception error)
            {
                InvalidOperationException safe = Safe(error, "下载域名设置未能应用，请检查运行状态后重试。");
                SetMessage(safe.Message); throw safe;
            }
            finally { DeleteInput(input); SetBusy(false); _lifecycle.Release(); }
        }

        private void RequestStopIntent(bool shutdown)
        {
            if (shutdown) _shutdownRequested = true;
            _desiredRunning = false;
            Interlocked.Increment(ref _stopVersion);
            _restartAt = null;
            SetMessage("正在停止并恢复原来的 Steam 转发设置…");
            StatusSnapshot current = ReadStatus();
            if (current != null && MatchingProcess(current)) TrySendStop(current, "user-stop");
        }

        private bool WantRunning(int version)
        {
            return !_disposed && !_shutdownRequested && _desiredRunning && version == Volatile.Read(ref _stopVersion);
        }

        private async Task StartCoreAsync(int version)
        {
            if (!WantRunning(version)) return;
            StatusSnapshot current = ReadStatus();
            if (current != null && MatchingProcess(current))
            {
                _expectedPid = current.pid;
                if (current.state == "running")
                {
                    _hadRunning = true; Publish(current, true, current.message); return;
                }
                await StopCoreAsync("domain-settings").ConfigureAwait(false);
            }
            else if (HasRecoveryRecord() && !Restored(current))
                await StopCoreAsync("domain-settings").ConfigureAwait(false);
            if (!WantRunning(version)) return;
            SetMessage("正在启动 Steam 自动优选…");
            LaunchDaemon(false);
            DateTime deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                if (!WantRunning(version)) { await StopCoreAsync("user-stop").ConfigureAwait(false); return; }
                current = ReadStatus();
                bool belongs = current != null && current.pid == _expectedPid;
                if (belongs && current.state == "running" && MatchingProcess(current))
                {
                    _hadRunning = true; Publish(current, true, current.message); return;
                }
                if (belongs) Publish(current, false, null);
                if (!OwnedChildAlive())
                {
                    if (!Restored(current) && HasRecoveryRecord()) await StopCoreAsync("user-stop").ConfigureAwait(false);
                    throw new InvalidOperationException(belongs ? SafeMessage(current.message, "服务未能启动，请检查下载端口和运行权限。") : "服务未能启动，请检查下载源配置后重试。");
                }
                await Task.Delay(160).ConfigureAwait(false);
            }
            await StopCoreAsync("user-stop").ConfigureAwait(false);
            throw new InvalidOperationException("服务启动超时，已停止并恢复设置，请重试。");
        }

        private async Task StopCoreAsync(string origin)
        {
            SetMessage("正在停止并恢复原来的 Steam 转发设置…");
            DateTime deadline = DateTime.UtcNow.AddSeconds(45);
            bool recovered = false;
            _lastStopRun = "";
            while (DateTime.UtcNow < deadline)
            {
                StatusSnapshot current = ReadStatus();
                bool alive = current != null && MatchingProcess(current);
                bool childAlive = OwnedChildAlive();
                HonorExternalStop(current);
                if (current != null && (_expectedPid == 0 || current.pid == _expectedPid)) Publish(current, false, null);
                if (alive)
                {
                    _expectedPid = current.pid;
                    TrySendStop(current, _desiredRunning ? origin : "user-stop");
                }
                else if (!childAlive)
                {
                    if (Restored(current))
                    {
                        Publish(current, false, "自动优选已停止，Steam 转发设置已恢复。");
                        return;
                    }
                    if (!HasRecoveryRecord())
                    {
                        Publish(new StatusSnapshot { state = "stopped", message = "自动优选已停止。" }, false, "自动优选已停止。");
                        return;
                    }
                    if (!recovered)
                    {
                        recovered = true;
                        SetMessage("正在恢复上次运行的 Steam 转发设置…");
                        LaunchDaemon(true);
                    }
                    else
                        throw new InvalidOperationException(SafeMessage(current == null ? null : current.message, "尚未确认转发设置恢复完成，请再次点击停止并恢复。"));
                }
                await Task.Delay(160).ConfigureAwait(false);
            }
            throw new InvalidOperationException("服务仍在恢复设置，尚未确认完成，请稍后再次点击停止并恢复。");
        }

        private bool HasRecoveryRecord()
        {
            return File.Exists(Path.Combine(_runtime, "hosts-state.json")) || File.Exists(Path.Combine(_runtime, "lifecycle.json"));
        }

        private static bool Restored(StatusSnapshot state)
        {
            return state != null && (state.state == "stopped" || state.state == "error") && state.restoreResult != null && state.restoreResult.hostsReleased;
        }

        private void LaunchDaemon(bool recoverOnly)
        {
            if (!File.Exists(_node) || !File.Exists(_daemon)) throw new InvalidOperationException("缺少自动优选运行组件，请重新安装程序。");
            if (OwnedChildAlive()) throw new InvalidOperationException("原服务尚未退出，请先停止并恢复。");
            Process old = _child;
            _child = null;
            if (old != null) old.Dispose();
            ProcessStartInfo info = NodeStartInfo();
            info.Arguments = Quote(_daemon) + " --config " + Quote(_config) + " --runtime " + Quote(_runtime) + " --parent-pid " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + (recoverOnly ? " --recover-only" : "");
            Process child = new Process();
            child.StartInfo = info;
            child.OutputDataReceived += delegate { };
            child.ErrorDataReceived += delegate { };
            try
            {
                if (!child.Start()) throw new InvalidOperationException("自动优选服务未能启动。");
                _child = child;
                _expectedPid = child.Id;
                _lastStopRun = "";
                child.BeginOutputReadLine();
                child.BeginErrorReadLine();
                Publish(new StatusSnapshot { state = recoverOnly ? "stopping" : "starting", pid = child.Id, parentPid = Process.GetCurrentProcess().Id, message = Message }, false, null);
            }
            catch { if (_child == null) child.Dispose(); throw; }
        }

        private bool OwnedChildAlive()
        {
            try { return _child != null && !_child.HasExited; }
            catch { return false; }
        }

        private static bool MatchingProcess(StatusSnapshot state)
        {
            if (state == null || state.pid <= 0 || String.IsNullOrEmpty(state.runId)) return false;
            DateTimeOffset started;
            if (!DateTimeOffset.TryParse(state.startedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out started)) return false;
            try
            {
                using (Process process = Process.GetProcessById(state.pid))
                {
                    if (process.HasExited || !String.Equals(process.ProcessName, "node", StringComparison.OrdinalIgnoreCase)) return false;
                    double difference = (started.UtcDateTime - process.StartTime.ToUniversalTime()).TotalSeconds;
                    return difference >= -1 && difference <= 15;
                }
            }
            catch { return false; }
        }

        private void TrySendStop(StatusSnapshot state, string origin)
        {
            if (state == null || String.IsNullOrEmpty(state.runId) || !MatchingProcess(state)) return;
            if (_lastStopRun == state.runId && origin != "user-stop") return;
            try
            {
                string json = Json().Serialize(new { runId = state.runId, command = "stop", origin = origin });
                AtomicWrite(_controlFile, json);
                _lastStopRun = state.runId;
            }
            catch { /* Retry while the verified daemon remains alive. */ }
        }

        private void HonorExternalStop(StatusSnapshot state)
        {
            if (!_desiredRunning || state == null || String.IsNullOrEmpty(state.runId)) return;
            try
            {
                FileInfo file = new FileInfo(_controlFile);
                if (!file.Exists || file.Length > 8192) return;
                Dictionary<string, object> command = Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(_controlFile, Encoding.UTF8).TrimStart('\uFEFF'));
                object runId, action, origin;
                if (command != null && command.TryGetValue("runId", out runId) && String.Equals(runId as string, state.runId, StringComparison.Ordinal) &&
                    command.TryGetValue("command", out action) && String.Equals(action as string, "stop", StringComparison.Ordinal) &&
                    (!command.TryGetValue("origin", out origin) || !String.Equals(origin as string, "domain-settings", StringComparison.Ordinal)))
                {
                    _desiredRunning = false;
                    Interlocked.Increment(ref _stopVersion);
                    _restartAt = null;
                }
            }
            catch { }
        }

        private async void Poll()
        {
            if (_disposed || Interlocked.Exchange(ref _polling, 1) != 0) return;
            try
            {
                StatusSnapshot current = ReadStatus();
                bool alive = current != null && MatchingProcess(current);
                HonorExternalStop(current);
                bool currentSession = current != null && (_expectedPid == 0 || current.pid == _expectedPid);
                if (currentSession)
                {
                    Publish(current, alive && current.state == "running", IsBusy || _restartAt.HasValue ? null : current.message);
                    if (!IsBusy && current.state == "stopped")
                    {
                        _desiredRunning = false; _restartAt = null;
                    }
                }
                if (!_desiredRunning || _shutdownRequested || !_hadRunning || IsBusy || alive || OwnedChildAlive()) return;
                if (!_restartAt.HasValue)
                {
                    if (_restartAttempts >= 3)
                    {
                        _desiredRunning = false;
                        SetMessage("自动服务多次退出，已停止重试；请点击停止并恢复。"); return;
                    }
                    _restartAt = DateTime.UtcNow.AddSeconds(5);
                    SetMessage("自动服务意外退出，将在 5 秒后尝试恢复…"); return;
                }
                if (DateTime.UtcNow < _restartAt.Value || !await _lifecycle.WaitAsync(0).ConfigureAwait(false)) return;
                try
                {
                    if (!_desiredRunning || _shutdownRequested || _disposed) return;
                    int version = Volatile.Read(ref _stopVersion);
                    SetBusy(true); _restartAt = null; _restartAttempts++;
                    SetMessage("正在恢复自动服务（" + _restartAttempts.ToString(CultureInfo.InvariantCulture) + "/3）…");
                    await StartCoreAsync(version).ConfigureAwait(false);
                }
                catch (Exception error) { SetMessage(Safe(error, "自动服务恢复未完成，请点击停止并恢复。").Message); }
                finally { SetBusy(false); _lifecycle.Release(); }
            }
            catch { /* A partially replaced status file cannot terminate the UI. */ }
            finally { Volatile.Write(ref _polling, 0); }
        }

        private async Task<string> RunCliAsync(string command, string input)
        {
            try
            {
                if (!File.Exists(_node) || !File.Exists(_cli)) throw new InvalidOperationException("缺少下载域名设置组件，请重新安装程序。");
                ProcessStartInfo info = NodeStartInfo();
                info.Arguments = Quote(_cli) + " " + command + " --config " + Quote(_config);
                if (input != null) info.Arguments += " --input " + Quote(input);
                if (command == "defaults") info.Arguments += " --defaults " + Quote(_defaults);
                using (Process process = new Process())
                {
                    process.StartInfo = info;
                    if (!process.Start()) throw new InvalidOperationException("下载域名设置组件未能启动。");
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    Task<string> errors = process.StandardError.ReadToEndAsync();
                    bool finished = await Task.Run(delegate { return process.WaitForExit(15000); }).ConfigureAwait(false);
                    if (!finished)
                    {
                        // This handle belongs to the CLI process started directly above, never a daemon or arbitrary PID.
                        try { process.Kill(); process.WaitForExit(1000); } catch { }
                        throw new InvalidOperationException("下载域名设置响应超时，请稍后重试。");
                    }
                    string json = (await output.ConfigureAwait(false)).TrimStart('\uFEFF').Trim();
                    await errors.ConfigureAwait(false);
                    if (json.Length > 2 * 1024 * 1024) throw new InvalidOperationException("下载域名设置返回内容过大。");
                    Dictionary<string, object> result = Json().Deserialize<Dictionary<string, object>>(json);
                    object success, message;
                    if (result == null || !result.TryGetValue("ok", out success) || !(success is bool) || !(bool)success || process.ExitCode != 0)
                    {
                        string reason = result != null && result.TryGetValue("message", out message) ? message as string : null;
                        throw new InvalidOperationException(SafeMessage(reason, "下载域名设置未能完成，请检查配置后重试。"));
                    }
                    return json;
                }
            }
            catch (Exception error) { throw Safe(error, "下载域名设置未能完成，请检查运行组件后重试。"); }
        }

        private ProcessStartInfo NodeStartInfo()
        {
            return new ProcessStartInfo {
                FileName = _node, WorkingDirectory = _engineDir, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
        }

        private string WriteRows(List<DomainRow> rows)
        {
            if (rows == null || rows.Count == 0) throw new InvalidOperationException("域名列表不能为空，请至少保留一个下载域名。");
            if (rows.Count > 256) throw new InvalidOperationException("域名数量不能超过 256 条。");
            try
            {
                string json = Json().Serialize(rows);
                if (Encoding.UTF8.GetByteCount(json) > 256 * 1024) throw new InvalidOperationException("域名列表内容过大，请减少域名后重试。");
                string file = Path.Combine(_runtime, "domain-settings-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(file, json, new UTF8Encoding(false));
                return file;
            }
            catch (Exception error) { throw Safe(error, "无法准备下载域名设置，请检查目录权限后重试。"); }
        }

        private void DeleteInput(string file)
        {
            try
            {
                if (Path.GetDirectoryName(file) == _runtime && Path.GetFileName(file).StartsWith("domain-settings-", StringComparison.Ordinal)) File.Delete(file);
            }
            catch { }
        }

        private static ConfigView DeserializeView(string json)
        {
            try
            {
                ConfigView view = Json().Deserialize<ConfigView>(json);
                if (view == null || view.groups == null || view.rows == null) throw new InvalidOperationException();
                return view;
            }
            catch { throw new InvalidOperationException("下载域名列表格式无效，请检查配置后重试。"); }
        }

        private StatusSnapshot ReadStatus()
        {
            try
            {
                byte[] bytes;
                using (FileStream stream = new FileStream(_statusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > 512 * 1024) return null;
                    bytes = new byte[512 * 1024 + 1];
                    int length = 0, count;
                    while (length < bytes.Length && (count = stream.Read(bytes, length, bytes.Length - length)) > 0) length += count;
                    if (length > 512 * 1024) return null;
                    Array.Resize(ref bytes, length);
                }
                StatusSnapshot value = Json().Deserialize<StatusSnapshot>(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
                if (value == null) return null;
                value.groups = (value.groups ?? new List<GroupSnapshot>()).Where(g => g != null).Take(256).ToList();
                value.events = (value.events ?? new List<EventSnapshot>()).Where(e => e != null).Take(30).ToList();
                foreach (GroupSnapshot group in value.groups) group.ips = (group.ips ?? new List<IpSnapshot>()).Where(ip => ip != null).Take(100).ToList();
                return value;
            }
            catch { return null; }
        }

        private static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024, RecursionLimit = 64 }; }

        private static void AtomicWrite(string file, string text)
        {
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                if (File.Exists(file)) File.Replace(temp, file, null);
                else File.Move(temp, file);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        private static string Quote(string value)
        {
            StringBuilder result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); }
                else { result.Append('\\', slashes); result.Append(character); }
                slashes = 0;
            }
            result.Append('\\', slashes * 2); result.Append('"');
            return result.ToString();
        }

        private static string SafeMessage(string message, string fallback)
        {
            if (String.IsNullOrWhiteSpace(message) || message.Length > 360 || message.IndexOfAny(new[] { '\r', '\n', '\\', '/' }) >= 0 || message.IndexOf(":\\", StringComparison.Ordinal) >= 0) return fallback;
            return message.Any(c => c >= '\u4e00' && c <= '\u9fff') ? message : fallback;
        }

        private static InvalidOperationException Safe(Exception error, string fallback)
        {
            InvalidOperationException expected = error as InvalidOperationException;
            return new InvalidOperationException(expected == null ? fallback : SafeMessage(expected.Message, fallback));
        }

        private void Publish(StatusSnapshot status, bool running, string message)
        {
            lock (_sync) { _status = status; _running = running; if (!String.IsNullOrEmpty(message)) _message = message; }
            RaiseChanged();
        }
        private void SetMessage(string message) { lock (_sync) _message = message; RaiseChanged(); }
        private void SetBusy(bool value) { lock (_sync) _busy = value; RaiseChanged(); }
        private void RaiseChanged()
        {
            if (_disposed) return;
            EventHandler handler = Changed;
            if (handler != null) { try { handler(this, EventArgs.Empty); } catch { } }
        }
        private void ThrowIfDisposed() { if (_disposed) throw new InvalidOperationException("程序已关闭，请重新启动后重试。"); }

        public void Dispose()
        {
            if (_disposed) return;
            _desiredRunning = false; _shutdownRequested = true; _disposed = true;
            Interlocked.Increment(ref _stopVersion);
            _timer.Dispose();
            // ShutdownAsync performs restoration before disposal. Disposing a handle never kills a process.
            if (_child != null) { try { _child.Dispose(); } catch { } }
        }
    }
}
