using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

public sealed class ComputeWorkerInstaller
{
    public sealed class Result
    {
        public string workerPath;
        public string variant;
        public string version;
    }

    [Serializable]
    private sealed class WorkerManifest
    {
        public int schemaVersion;
        public string version;
        public WorkerPackages packages;
    }

    [Serializable]
    private sealed class WorkerPackages
    {
        public WorkerPackage cpu;
        public WorkerPackage cuda;
    }

    [Serializable]
    private sealed class WorkerPackage
    {
        public string variant;
        public string archiveSha256;
        public long archiveSizeBytes;
        public string executablePath;
        public List<WorkerPart> parts = new List<WorkerPart>();
    }

    [Serializable]
    private sealed class WorkerPart
    {
        public string fileName;
        public string url;
        public string sha256;
        public long sizeBytes;
    }

    [Serializable]
    private sealed class InstallMarker
    {
        public string version;
        public string variant;
        public string archiveSha256;
    }

    private const int ManifestSchemaVersion = 1;
    private const int BufferSize = 1024 * 1024;
    private const long MinimumTorchDllBytes = 100L * 1024L * 1024L;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string fileName);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    public IEnumerator Resolve(
        string manifestUrl,
        Action<string> reportProgress,
        Action<string> reportFailure,
        Action<Result> completed
    )
    {
        Action<string> progress = reportProgress ?? delegate { };
        Action<string> failure = reportFailure ?? delegate { };
        Action<Result> finish = completed ?? delegate { };

        if (Application.platform != RuntimePlatform.WindowsPlayer &&
            Application.platform != RuntimePlatform.WindowsEditor)
        {
            failure("Automatic compute workers are currently available only on Windows.");
            finish(null);
            yield break;
        }

        Uri parsedManifestUrl;
        if (!TryValidateDownloadUrl(manifestUrl, out parsedManifestUrl))
        {
            failure("Worker manifest URL must be an absolute HTTPS URL.");
            finish(null);
            yield break;
        }

        string cacheRoot = Path.Combine(
            Application.persistentDataPath,
            "compute-worker-packages"
        );
        string downloadRoot = Path.Combine(cacheRoot, "downloads");
        string installRoot = Path.Combine(cacheRoot, "installed");
        string cachedManifestPath = Path.Combine(cacheRoot, "worker-manifest.json");
        Directory.CreateDirectory(downloadRoot);
        Directory.CreateDirectory(installRoot);

        string manifestJson = null;
        string manifestError = null;
        progress("Checking the compute worker release manifest...");
        yield return DownloadText(
            parsedManifestUrl.AbsoluteUri,
            delegate(string value) { manifestJson = value; },
            delegate(string value) { manifestError = value; }
        );

        if (string.IsNullOrWhiteSpace(manifestJson) && File.Exists(cachedManifestPath))
        {
            progress(
                "The online worker manifest is unavailable; trying the cached manifest."
            );
            try
            {
                manifestJson = File.ReadAllText(cachedManifestPath);
            }
            catch (Exception error)
            {
                manifestError = error.Message;
            }
        }

        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            failure("Could not download the worker manifest: " + manifestError);
            finish(null);
            yield break;
        }

        WorkerManifest manifest;
        string validationError;
        try
        {
            manifest = JsonConvert.DeserializeObject<WorkerManifest>(manifestJson);
        }
        catch (Exception error)
        {
            failure("Worker manifest JSON is invalid: " + error.Message);
            finish(null);
            yield break;
        }

        if (!ValidateManifest(manifest, out validationError))
        {
            failure("Worker manifest was rejected: " + validationError);
            finish(null);
            yield break;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachedManifestPath));
            File.WriteAllText(
                cachedManifestPath,
                manifestJson,
                new System.Text.UTF8Encoding(false)
            );
        }
        catch (Exception error)
        {
            Debug.LogWarning("Could not cache worker manifest: " + error.Message);
        }

        bool preferCuda = HasNvidiaCudaDriver();
        WorkerPackage selectedPackage = preferCuda
            ? manifest.packages.cuda
            : manifest.packages.cpu;
        if (selectedPackage == null && preferCuda)
        {
            selectedPackage = manifest.packages.cpu;
            preferCuda = false;
        }

        progress(
            preferCuda
                ? "An NVIDIA CUDA driver was detected; preparing the GPU worker."
                : "No NVIDIA CUDA driver was detected; preparing the CPU worker."
        );

        Result result = null;
        string installError = null;
        yield return RunSafely(
            InstallPackage(
                manifest.version,
                selectedPackage,
                downloadRoot,
                installRoot,
                progress,
                delegate(Result value) { result = value; },
                delegate(string value) { installError = value; }
            ),
            delegate(Exception error)
            {
                installError = error.Message;
            }
        );

        if (result == null && preferCuda && manifest.packages.cpu != null)
        {
            progress(
                "The CUDA worker could not be installed; trying the CPU worker instead."
            );
            installError = null;
            yield return RunSafely(
                InstallPackage(
                    manifest.version,
                    manifest.packages.cpu,
                    downloadRoot,
                    installRoot,
                    progress,
                    delegate(Result value) { result = value; },
                    delegate(string value) { installError = value; }
                ),
                delegate(Exception error)
                {
                    installError = error.Message;
                }
            );
        }

        if (result == null)
        {
            failure("Could not prepare a compute worker: " + installError);
        }
        finish(result);
    }

    private IEnumerator InstallPackage(
        string version,
        WorkerPackage package,
        string downloadRoot,
        string installRoot,
        Action<string> progress,
        Action<Result> completed,
        Action<string> failed
    )
    {
        if (package == null)
        {
            failed("The selected worker package is not present in the manifest.");
            yield break;
        }

        string safeVersion = SanitizePathSegment(version);
        string safeVariant = SanitizePathSegment(package.variant);
        string finalDirectory = Path.Combine(
            installRoot,
            safeVersion + "-" + safeVariant
        );
        string finalWorkerPath = Path.Combine(
            finalDirectory,
            NormalizeRelativePath(package.executablePath)
        );
        string markerPath = Path.Combine(finalDirectory, "installed-worker.json");
        if (IsInstalledWorkerValid(
            finalWorkerPath,
            markerPath,
            version,
            package
        ))
        {
            progress("Using the cached " + package.variant + " compute worker.");
            completed(new Result
            {
                workerPath = finalWorkerPath,
                variant = package.variant,
                version = version,
            });
            yield break;
        }

        string packageDownloadRoot = Path.Combine(
            downloadRoot,
            safeVersion + "-" + safeVariant
        );
        Directory.CreateDirectory(packageDownloadRoot);
        List<string> partPaths = new List<string>();

        for (int index = 0; index < package.parts.Count; index++)
        {
            WorkerPart part = package.parts[index];
            string partPath = Path.Combine(packageDownloadRoot, part.fileName);
            bool validExistingPart = false;
            if (File.Exists(partPath))
            {
                yield return VerifyFile(
                    partPath,
                    part.sizeBytes,
                    part.sha256,
                    delegate(bool value) { validExistingPart = value; }
                );
            }

            if (!validExistingPart)
            {
                SafeDeleteFile(partPath);
                string temporaryPath = partPath + ".download";
                SafeDeleteFile(temporaryPath);
                progress(
                    "Downloading " + package.variant + " worker part " +
                    (index + 1) + " of " + package.parts.Count + "..."
                );
                string downloadError = null;
                yield return DownloadFile(
                    part.url,
                    temporaryPath,
                    delegate(string value) { downloadError = value; }
                );
                if (!string.IsNullOrWhiteSpace(downloadError))
                {
                    failed(downloadError);
                    yield break;
                }

                bool downloadedPartIsValid = false;
                progress("Verifying " + part.fileName + "...");
                yield return VerifyFile(
                    temporaryPath,
                    part.sizeBytes,
                    part.sha256,
                    delegate(bool value) { downloadedPartIsValid = value; }
                );
                if (!downloadedPartIsValid)
                {
                    SafeDeleteFile(temporaryPath);
                    failed("Checksum or size verification failed for " + part.fileName + ".");
                    yield break;
                }
                File.Move(temporaryPath, partPath);
            }
            partPaths.Add(partPath);
        }

        string archivePath;
        if (partPaths.Count == 1)
        {
            archivePath = partPaths[0];
        }
        else
        {
            archivePath = Path.Combine(
                packageDownloadRoot,
                "combined-" + safeVariant + ".zip"
            );
            SafeDeleteFile(archivePath);
            progress("Combining the worker archive parts...");
            yield return CombineFiles(partPaths, archivePath);
        }

        bool archiveIsValid = false;
        progress("Verifying the complete worker archive...");
        yield return VerifyFile(
            archivePath,
            package.archiveSizeBytes,
            package.archiveSha256,
            delegate(bool value) { archiveIsValid = value; }
        );
        if (!archiveIsValid)
        {
            if (partPaths.Count > 1)
            {
                SafeDeleteFile(archivePath);
            }
            failed("The complete worker archive failed checksum or size verification.");
            yield break;
        }

        string stagingDirectory = finalDirectory + ".staging";
        SafeDeleteDirectory(stagingDirectory);
        Directory.CreateDirectory(stagingDirectory);
        string extractionError = null;
        progress("Installing the " + package.variant + " compute worker...");
        yield return RunSafely(
            ExtractZipSafely(
                archivePath,
                stagingDirectory,
                delegate(string value) { extractionError = value; }
            ),
            delegate(Exception error)
            {
                extractionError = "Could not extract the worker archive: " +
                    error.Message;
            }
        );
        if (!string.IsNullOrWhiteSpace(extractionError))
        {
            SafeDeleteDirectory(stagingDirectory);
            failed(extractionError);
            yield break;
        }

        string stagedWorkerPath = Path.Combine(
            stagingDirectory,
            NormalizeRelativePath(package.executablePath)
        );
        if (!ValidateWorkerFiles(stagedWorkerPath, package.variant))
        {
            SafeDeleteDirectory(stagingDirectory);
            failed("The extracted worker is incomplete or has missing PyTorch DLLs.");
            yield break;
        }

        InstallMarker marker = new InstallMarker
        {
            version = version,
            variant = package.variant,
            archiveSha256 = package.archiveSha256.ToLowerInvariant(),
        };
        File.WriteAllText(
            Path.Combine(stagingDirectory, "installed-worker.json"),
            JsonConvert.SerializeObject(marker, Formatting.Indented),
            new System.Text.UTF8Encoding(false)
        );

        SafeDeleteDirectory(finalDirectory);
        Directory.Move(stagingDirectory, finalDirectory);
        if (partPaths.Count > 1)
        {
            SafeDeleteFile(archivePath);
        }
        foreach (string partPath in partPaths)
        {
            SafeDeleteFile(partPath);
        }

        finalWorkerPath = Path.Combine(
            finalDirectory,
            NormalizeRelativePath(package.executablePath)
        );
        progress("The " + package.variant + " compute worker is ready.");
        completed(new Result
        {
            workerPath = finalWorkerPath,
            variant = package.variant,
            version = version,
        });
    }

    private static IEnumerator DownloadText(
        string url,
        Action<string> completed,
        Action<string> failed
    )
    {
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = 60;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                failed(request.error + " (HTTP " + request.responseCode + ")");
            }
            else
            {
                completed(request.downloadHandler.text);
            }
        }
    }

    private static IEnumerator DownloadFile(
        string url,
        string destinationPath,
        Action<string> failed
    )
    {
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            DownloadHandlerFile handler = new DownloadHandlerFile(destinationPath);
            handler.removeFileOnAbort = true;
            request.downloadHandler = handler;
            request.timeout = 0;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                failed(
                    "Download failed for " + Path.GetFileName(destinationPath) +
                    ": " + request.error + " (HTTP " + request.responseCode + ")"
                );
            }
        }
    }

    private static IEnumerator VerifyFile(
        string path,
        long expectedSize,
        string expectedSha256,
        Action<bool> completed
    )
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expectedSize)
        {
            completed(false);
            yield break;
        }

        byte[] buffer = new byte[BufferSize];
        long bytesSinceYield = 0;
        using (SHA256 hash = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
        {
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.TransformBlock(buffer, 0, count, null, 0);
                bytesSinceYield += count;
                if (bytesSinceYield >= 32L * BufferSize)
                {
                    bytesSinceYield = 0;
                    yield return null;
                }
            }
            hash.TransformFinalBlock(new byte[0], 0, 0);
            string actual = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
            completed(
                string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase)
            );
        }
    }

    private static IEnumerator CombineFiles(List<string> parts, string outputPath)
    {
        byte[] buffer = new byte[BufferSize];
        using (FileStream output = File.Create(outputPath))
        {
            foreach (string partPath in parts)
            {
                using (FileStream input = File.OpenRead(partPath))
                {
                    int count;
                    long bytesSinceYield = 0;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, count);
                        bytesSinceYield += count;
                        if (bytesSinceYield >= 32L * BufferSize)
                        {
                            bytesSinceYield = 0;
                            yield return null;
                        }
                    }
                }
            }
        }
    }

    private static IEnumerator ExtractZipSafely(
        string archivePath,
        string destinationRoot,
        Action<string> failed
    )
    {
        string destinationPrefix = Path.GetFullPath(destinationRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        byte[] buffer = new byte[BufferSize];
        using (FileStream archiveStream = File.OpenRead(archivePath))
        using (ZipArchive archive = new ZipArchive(
            archiveStream,
            ZipArchiveMode.Read,
            false
        ))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string relativeName = NormalizeRelativePath(entry.FullName);
                if (string.IsNullOrWhiteSpace(relativeName))
                {
                    continue;
                }
                string outputPath = Path.GetFullPath(
                    Path.Combine(destinationRoot, relativeName)
                );
                if (!outputPath.StartsWith(
                    destinationPrefix,
                    StringComparison.OrdinalIgnoreCase
                ))
                {
                    failed("Worker archive contains an unsafe path: " + entry.FullName);
                    yield break;
                }

                if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                {
                    Directory.CreateDirectory(outputPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                using (Stream input = entry.Open())
                using (FileStream output = File.Create(outputPath))
                {
                    int count;
                    long bytesSinceYield = 0;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, count);
                        bytesSinceYield += count;
                        if (bytesSinceYield >= 32L * BufferSize)
                        {
                            bytesSinceYield = 0;
                            yield return null;
                        }
                    }
                }
            }
        }
    }

    private static IEnumerator RunSafely(
        IEnumerator operation,
        Action<Exception> failed
    )
    {
        Stack<IEnumerator> operations = new Stack<IEnumerator>();
        operations.Push(operation);
        while (operations.Count > 0)
        {
            bool hasNext;
            object current = null;
            try
            {
                hasNext = operations.Peek().MoveNext();
                if (hasNext)
                {
                    current = operations.Peek().Current;
                }
            }
            catch (Exception error)
            {
                failed(error);
                yield break;
            }
            if (!hasNext)
            {
                operations.Pop();
                continue;
            }
            IEnumerator nested = current as IEnumerator;
            if (nested != null)
            {
                operations.Push(nested);
                continue;
            }
            yield return current;
        }
    }

    private static bool ValidateManifest(
        WorkerManifest manifest,
        out string error
    )
    {
        if (manifest == null || manifest.schemaVersion != ManifestSchemaVersion)
        {
            error = "unsupported or missing schemaVersion";
            return false;
        }
        if (string.IsNullOrWhiteSpace(manifest.version) || manifest.packages == null)
        {
            error = "version or packages is missing";
            return false;
        }
        if (manifest.packages.cpu == null && manifest.packages.cuda == null)
        {
            error = "at least one CPU or CUDA package is required";
            return false;
        }
        if (manifest.packages.cpu != null &&
            !ValidatePackage(manifest.packages.cpu, "cpu", out error))
        {
            return false;
        }
        if (manifest.packages.cuda != null &&
            !ValidatePackage(manifest.packages.cuda, "cuda", out error))
        {
            return false;
        }
        error = "";
        return true;
    }

    private static bool ValidatePackage(
        WorkerPackage package,
        string expectedVariant,
        out string error
    )
    {
        if (!string.Equals(package.variant, expectedVariant, StringComparison.Ordinal))
        {
            error = "package variant must be " + expectedVariant;
            return false;
        }
        if (!IsSha256(package.archiveSha256) || package.archiveSizeBytes <= 0)
        {
            error = expectedVariant + " archive checksum or size is invalid";
            return false;
        }
        if (!IsSafeRelativePath(package.executablePath) ||
            package.parts == null || package.parts.Count == 0)
        {
            error = expectedVariant + " executable path or parts list is invalid";
            return false;
        }
        foreach (WorkerPart part in package.parts)
        {
            Uri parsed;
            if (part == null ||
                string.IsNullOrWhiteSpace(part.fileName) ||
                Path.GetFileName(part.fileName) != part.fileName ||
                !TryValidateDownloadUrl(part.url, out parsed) ||
                !IsSha256(part.sha256) ||
                part.sizeBytes <= 0)
            {
                error = expectedVariant + " package contains an invalid part";
                return false;
            }
        }
        error = "";
        return true;
    }

    private static bool IsInstalledWorkerValid(
        string workerPath,
        string markerPath,
        string version,
        WorkerPackage package
    )
    {
        if (!ValidateWorkerFiles(workerPath, package.variant) || !File.Exists(markerPath))
        {
            return false;
        }
        try
        {
            InstallMarker marker = JsonConvert.DeserializeObject<InstallMarker>(
                File.ReadAllText(markerPath)
            );
            return marker != null &&
                marker.version == version &&
                marker.variant == package.variant &&
                string.Equals(
                    marker.archiveSha256,
                    package.archiveSha256,
                    StringComparison.OrdinalIgnoreCase
                );
        }
        catch
        {
            return false;
        }
    }

    private static bool ValidateWorkerFiles(string workerPath, string variant)
    {
        if (!File.Exists(workerPath))
        {
            return false;
        }
        string workerDirectory = Path.GetDirectoryName(workerPath);
        string torchLibraryDirectory = Path.Combine(
            workerDirectory,
            "_internal",
            "torch",
            "lib"
        );
        string torchCpuPath = Path.Combine(torchLibraryDirectory, "torch_cpu.dll");
        if (!File.Exists(torchCpuPath) ||
            new FileInfo(torchCpuPath).Length < MinimumTorchDllBytes)
        {
            return false;
        }
        if (string.Equals(variant, "cuda", StringComparison.Ordinal))
        {
            string torchCudaPath = Path.Combine(torchLibraryDirectory, "torch_cuda.dll");
            return File.Exists(torchCudaPath) &&
                new FileInfo(torchCudaPath).Length >= MinimumTorchDllBytes;
        }
        return true;
    }

    private static bool HasNvidiaCudaDriver()
    {
        IntPtr module = IntPtr.Zero;
        try
        {
            module = LoadLibrary("nvcuda.dll");
            return module != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (module != IntPtr.Zero)
            {
                FreeLibrary(module);
            }
        }
    }

    private static bool TryValidateDownloadUrl(string value, out Uri parsed)
    {
        parsed = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out parsed))
        {
            return false;
        }
        if (parsed.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }
        return parsed.Scheme == Uri.UriSchemeHttp &&
            (parsed.Host == "127.0.0.1" || parsed.Host == "localhost");
    }

    private static bool IsSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
        {
            return false;
        }
        foreach (char character in value)
        {
            bool isHex = (character >= '0' && character <= '9') ||
                (character >= 'a' && character <= 'f') ||
                (character >= 'A' && character <= 'F');
            if (!isHex)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            return false;
        }
        string normalized = NormalizeRelativePath(value);
        string[] segments = normalized.Split(Path.DirectorySeparatorChar);
        foreach (string segment in segments)
        {
            if (segment == ".." || segment == "." || string.IsNullOrWhiteSpace(segment))
            {
                return false;
            }
        }
        return true;
    }

    private static string NormalizeRelativePath(string value)
    {
        return (value ?? "")
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
    }

    private static string SanitizePathSegment(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] characters = (value ?? "worker").ToCharArray();
        for (int index = 0; index < characters.Length; index++)
        {
            if (Array.IndexOf(invalid, characters[index]) >= 0)
            {
                characters[index] = '_';
            }
        }
        string result = new string(characters).Trim();
        return string.IsNullOrWhiteSpace(result) ? "worker" : result;
    }

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A later download or verification step reports the actionable error.
        }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // A later extraction or move step reports the actionable error.
        }
    }
}
