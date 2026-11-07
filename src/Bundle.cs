using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace SteamSmart.Desktop
{
    internal static class Bundle
    {
        private const string ResourceName = "SteamSmart.Payload.zip";
        private const string ManifestName = "payload-manifest.json";
        private static readonly string[] FileNames =
        {
            "node.exe",
            "smart-daemon.mjs",
            "smart-engine.mjs",
            "hosts-manager.mjs",
            "smart-config-manager.mjs",
            "smart-config-cli.mjs",
            "SteamSmartBridge.ps1",
            "smart-config.defaults.json",
            "smart-config.json"
        };

        internal sealed class PayloadManifest
        {
            public PayloadManifest() { }
            public int format { get; set; }
            public string version { get; set; }
            public List<PayloadFile> files { get; set; }
        }

        internal sealed class PayloadFile
        {
            public PayloadFile() { }
            public string name { get; set; }
            public long length { get; set; }
            public string sha256 { get; set; }
        }

        public static string EnsureExtracted()
        {
            string hash;
            using (Stream resource = OpenPayload()) hash = Hash(resource);
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (String.IsNullOrWhiteSpace(localData)) throw new IOException("无法找到本机应用数据目录。");
            string cacheRoot = Path.GetFullPath(Path.Combine(localData, "SteamSmart", "bin"));
            EnsureSafeDirectory(cacheRoot);
            string destination = ChildPath(cacheRoot, "v2-" + hash.Substring(0, 24));
            using (var mutex = new Mutex(false, @"Local\SteamSmartBundle-" + hash.Substring(0, 24)))
            {
                bool locked;
                try { locked = mutex.WaitOne(TimeSpan.FromSeconds(45)); }
                catch (AbandonedMutexException) { locked = true; }
                if (!locked) throw new IOException("另一窗口正在准备运行组件，请稍后重试。");
                try
                {
                    using (Stream resource = OpenPayload())
                    using (var archive = new ZipArchive(resource, ZipArchiveMode.Read, false))
                    {
                        PayloadManifest manifest = ReadManifest(archive);
                        if (ValidateCache(destination, manifest)) return destination;
                        if (Directory.Exists(destination) || File.Exists(destination))
                        {
                            AssertNotReparsePoint(destination);
                            // Keep a damaged cache for diagnosis, never execute its files.
                            string quarantine = ChildPath(cacheRoot, "invalid-" + hash.Substring(0, 12) + "-" + Guid.NewGuid().ToString("N"));
                            try
                            {
                                if (Directory.Exists(destination)) Directory.Move(destination, quarantine);
                                else File.Move(destination, quarantine);
                            }
                            catch (IOException)
                            {
                                // A different process can still hold an old cache open.
                                destination = ChildPath(cacheRoot, "v2-" + hash.Substring(0, 24) + "-" + Guid.NewGuid().ToString("N"));
                            }
                            catch (UnauthorizedAccessException)
                            {
                                destination = ChildPath(cacheRoot, "v2-" + hash.Substring(0, 24) + "-" + Guid.NewGuid().ToString("N"));
                            }
                        }
                        string staging = ChildPath(cacheRoot, ".extract-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(staging);
                        try
                        {
                            AssertNotReparsePoint(staging);
                            foreach (PayloadFile file in manifest.files)
                            {
                                ZipArchiveEntry entry = archive.GetEntry(file.name);
                                string target = ChildPath(staging, file.name);
                                using (Stream input = entry.Open())
                                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan))
                                {
                                    byte[] buffer = new byte[65536];
                                    long written = 0;
                                    int count;
                                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                                    {
                                        written += count;
                                        if (written > file.length) throw new InvalidDataException("内置运行组件的大小校验失败。");
                                        output.Write(buffer, 0, count);
                                    }
                                    output.Flush(true);
                                    if (written != file.length) throw new InvalidDataException("内置运行组件不完整。");
                                }
                            }
                            if (!ValidateCache(staging, manifest)) throw new InvalidDataException("运行组件校验失败，请重新获取程序文件。");
                            return PublishVerified(staging, destination, cacheRoot, manifest);
                        }
                        finally { DeleteOwnStaging(staging, cacheRoot); }
                    }
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static string PublishVerified(string staging, string destination, string cacheRoot, PayloadManifest manifest)
        {
            try
            {
                Directory.Move(staging, destination);
                return destination;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            // Some Windows policies deny renaming a directory even though
            // individual files can be created. Keep the payload mutex held and
            // publish only verified files into a fresh directory instead. A
            // crashed or partial publication never passes ValidateCache.
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                if (ValidateCache(destination, manifest)) return destination;
                destination = ChildPath(cacheRoot, Path.GetFileName(destination) + "-" + Guid.NewGuid().ToString("N"));
            }
            Directory.CreateDirectory(destination);
            AssertNotReparsePoint(destination);
            foreach (PayloadFile file in manifest.files)
                File.Copy(ChildPath(staging, file.name), ChildPath(destination, file.name), false);
            if (!ValidateCache(destination, manifest))
                throw new InvalidDataException("运行组件发布后校验失败，尚未启动转发服务。");
            return destination;
        }

        private static Stream OpenPayload()
        {
            Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (resource == null) throw new InvalidDataException("程序缺少内置运行组件。");
            return resource;
        }

        private static PayloadManifest ReadManifest(ZipArchive archive)
        {
            var allowed = new HashSet<string>(FileNames, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (archive.Entries.Count != FileNames.Length + 1) throw new InvalidDataException("内置运行组件清单不正确。");
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.FullName != entry.Name || !seen.Add(entry.FullName) ||
                    (entry.FullName != ManifestName && !allowed.Contains(entry.FullName)))
                    throw new InvalidDataException("内置运行组件包含不允许的文件。");
            }
            ZipArchiveEntry manifestEntry = archive.GetEntry(ManifestName);
            if (manifestEntry == null || manifestEntry.Length < 2 || manifestEntry.Length > 65536)
                throw new InvalidDataException("内置运行组件清单不完整。");
            string json;
            using (Stream stream = manifestEntry.Open())
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                json = reader.ReadToEnd();
            var serializer = new JavaScriptSerializer { MaxJsonLength = 65536, RecursionLimit = 12 };
            PayloadManifest manifest;
            try { manifest = serializer.Deserialize<PayloadManifest>(json); }
            catch (Exception) { throw new InvalidDataException("内置运行组件清单无法读取。"); }
            if (manifest == null || manifest.format != 1 || manifest.version != "2.0.0" ||
                manifest.files == null || manifest.files.Count != FileNames.Length)
                throw new InvalidDataException("内置运行组件版本不正确。");
            seen.Clear();
            foreach (PayloadFile file in manifest.files)
            {
                if (file == null || !allowed.Contains(file.name) || !seen.Add(file.name) ||
                    file.length <= 0 || file.length > 256L * 1024 * 1024 ||
                    file.sha256 == null || !Regex.IsMatch(file.sha256, "^[0-9a-f]{64}$") ||
                    archive.GetEntry(file.name).Length != file.length)
                    throw new InvalidDataException("内置运行组件文件校验信息不正确。");
            }
            return manifest;
        }

        private static bool ValidateCache(string directory, PayloadManifest manifest)
        {
            try
            {
                if (!Directory.Exists(directory)) return false;
                AssertNotReparsePoint(directory);
                if (Directory.GetDirectories(directory).Length != 0 || Directory.GetFiles(directory).Length != FileNames.Length)
                    return false;
                foreach (PayloadFile file in manifest.files)
                {
                    string target = ChildPath(directory, file.name);
                    AssertNotReparsePoint(target);
                    var info = new FileInfo(target);
                    if (!info.Exists || info.Length != file.length) return false;
                    using (Stream stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                        if (!String.Equals(Hash(stream), file.sha256, StringComparison.Ordinal)) return false;
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static string Hash(Stream stream)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string ChildPath(string parent, string child)
        {
            string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string result = Path.GetFullPath(Path.Combine(root, child));
            if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Path.GetDirectoryName(result), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("运行组件路径检查失败。");
            return result;
        }

        private static void AssertNotReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("运行组件目录含有链接，请使用普通本地目录。");
        }

        private static void EnsureSafeDirectory(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            string current = root;
            foreach (string part in full.Substring(root.Length).Split(Path.DirectorySeparatorChar))
            {
                if (part.Length == 0) continue;
                current = Path.Combine(current, part);
                if (File.Exists(current) && !Directory.Exists(current)) throw new IOException("运行组件目录被同名文件占用。");
                if (Directory.Exists(current)) AssertNotReparsePoint(current);
                else Directory.CreateDirectory(current);
            }
        }

        private static void DeleteOwnStaging(string directory, string cacheRoot)
        {
            if (!Directory.Exists(directory)) return;
            try
            {
                if (!String.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), Path.GetFullPath(cacheRoot), StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(directory).StartsWith(".extract-", StringComparison.Ordinal)) return;
                AssertNotReparsePoint(directory);
                if (Directory.GetDirectories(directory).Length != 0) return;
                foreach (string file in Directory.GetFiles(directory))
                {
                    if (!FileNames.Contains(Path.GetFileName(file), StringComparer.Ordinal)) return;
                    AssertNotReparsePoint(file);
                }
                foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
