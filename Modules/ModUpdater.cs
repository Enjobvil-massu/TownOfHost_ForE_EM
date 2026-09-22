using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static TownOfHostForE.Translator;

namespace TownOfHostForE
{
    [HarmonyPatch]
    public class ModUpdater
    {
        private const int ManifestSchema = 1;
        private const long MaxDownloadBytes = 64L * 1024L * 1024L;
        private const int MaxReleaseRows = 6;
        private const int MaxChangesPerRelease = 4;

        public static bool hasUpdate = false;
        public static bool isBroken = false;
        public static bool isChecked = false;
        public static Version latestVersion = null;
        public static string latestTitle = null;
        public static string downloadUrl = null;
        public static string newFileName = "TownOfHostForE_EM.dll";
        public static GenericPopup InfoPopup;
        private const string ReleaseInstallButtonName = "TOH4E_EM_UpdateInstallButton";
        private static bool releasePopupButtonBaseCaptured = false;
        private static UnityEngine.Vector3 releasePopupButtonBasePosition;

        private static string expectedSha256 = null;
        private static long expectedSize = 0;
        private static string latestAmongUsVersion = null;
        private static string latestPublishedAt = null;
        private static JArray releases = new();
        private static string lastCheckError = null;

        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start)), HarmonyPostfix, HarmonyPriority(Priority.LowerThanNormal)]
        public static void StartPostfix(MainMenuManager __instance)
        {
            CleanupConfirmedBackup();

            InfoPopup = UnityEngine.Object.Instantiate(Twitch.TwitchManager.Instance.TwitchPopup);
            InfoPopup.name = "TOH4E_EM_UpdatePopup";
            releasePopupButtonBaseCaptured = false;

            // EM_UPDATER_POPUP_BACKGROUND_WIDTH_V1:
            // TextAreaやPopup全体をscaleせず、Updater専用cloneのBackground SpriteRendererだけを横へ広げる。
            // GenericPopupのExitGameはBackgroundとは別childなので、ボタンtransformや文字サイズは変更しない。
            var popupBackground = InfoPopup.transform.FindChild("Background");
            if (popupBackground != null)
            {
                var backgroundRenderer = popupBackground.GetComponent<SpriteRenderer>();
                if (backgroundRenderer != null)
                {
                    var backgroundSize = backgroundRenderer.size;
                    // EM_UPDATER_POPUP_FRAME_BALANCED_V316: 幅だけ極端に広げず、更新履歴+2ボタン用に縦も拡張。
                backgroundSize.x *= 2.05f;
                backgroundSize.y *= 1.65f;
                    backgroundRenderer.size = backgroundSize;
                }
            }

            InfoPopup.TextAreaTMP.GetComponent<RectTransform>().sizeDelta = new(5.6f, 4.6f);

            // EM_UPDATER_POPUP_TEXT_FIT_V2:
            // 長い更新履歴でも枠内へ収まりやすいよう本文だけAutoSizeする。
            InfoPopup.TextAreaTMP.enableAutoSizing = true;
            InfoPopup.TextAreaTMP.fontSizeMin = 1.25f;
            InfoPopup.TextAreaTMP.fontSizeMax = 2.1f;
            InfoPopup.TextAreaTMP.fontSize = 2.1f;

            hasUpdate = false;
            isBroken = false;
            isChecked = false;
            lastCheckError = null;

            SetMainMenuButtonText(GetString("updateButton"));

            // EM_UPDATER_AUTO_BADGE_CHECK_V1:
            // トップ画面表示時にmanifestを確認し、更新がある場合は即座に「!」を表示する。
            // 通信失敗はRefreshRemoteData側でFail-Open処理され、MOD起動は継続する。
            // EM_UPDATER_STARTUP_FAILOPEN_V3:
            // MainMenuManager.Start 中にネットワークを同期待機しない。
            // Coroutineでメニュー起動後にHTTPだけをバックグラウンド実行し、
            // Unity UI反映は必ずメインスレッドへ戻ってから行う。
            try
            {
                __instance.StartCoroutine(CoStartupCheckFailOpen().WrapToIl2Cpp());
            }
            catch (Exception ex)
            {
                hasUpdate = false;
                isBroken = false;
                isChecked = true;
                lastCheckError = ex.Message;

                Logger.Warn(
                    "起動時アップデート確認を開始できませんでした。MOD起動を継続します。 / " +
                    ex.Message,
                    "ModUpdater");
            }
        }

        private static System.Collections.IEnumerator CoStartupCheckFailOpen()
        {
            // MainMenuManager.Startを完全に抜けてから更新確認を開始する。
            yield return new WaitForSeconds(0.1f);

            Task<bool> checkTask;
            string currentApplicationVersion = Application.version;

            try
            {
                // HTTP/JSON処理はバックグラウンドへ退避。
                // updateUi=false により、このTask内ではUnity UIを一切操作しない。
                checkTask = Task.Run(() =>
                    RefreshRemoteData(
                        updateUi: false,
                        currentApplicationVersion: currentApplicationVersion));
            }
            catch (Exception ex)
            {
                hasUpdate = false;
                isBroken = false;
                isChecked = true;
                lastCheckError = ex.Message;

                Logger.Warn(
                    "起動時アップデート確認Taskの生成に失敗しました。MOD起動を継続します。 / " +
                    ex.Message,
                    "ModUpdater");
                yield break;
            }

            while (!checkTask.IsCompleted)
            {
                yield return null;
            }

            if (checkTask.IsCanceled)
            {
                hasUpdate = false;
                isBroken = false;
                isChecked = true;
                lastCheckError = "Startup update check was canceled.";
            }
            else if (checkTask.IsFaulted)
            {
                hasUpdate = false;
                isBroken = false;
                isChecked = true;

                Exception taskError =
                    checkTask.Exception?.GetBaseException();
                lastCheckError =
                    taskError?.Message ??
                    "Unknown startup update check failure.";

                Logger.Warn(
                    "起動時アップデート確認で想定外エラーが発生しました。MOD起動を継続します。 / " +
                    lastCheckError,
                    "ModUpdater");
            }

            // ここはCoroutine＝Unityメインスレッド。
            // hasUpdate=true の通知バッジを含め、UI操作はここでだけ反映する。
            SetMainMenuButtonText(GetString(
"updateButton"
));
        }

        public static void ShowReleaseList()
        {
            if (InfoPopup == null)
            {
                return;
            }

            ShowPopup(GetString("updatePleaseWait"));

            bool refreshed = RefreshRemoteData().GetAwaiter().GetResult();
            string message = BuildReleaseListText();
            bool canInstall = refreshed && hasUpdate;

            Logger.Info(
                "UPDATER_RELEASE_POPUP_V316 " +
                "Refreshed=" + refreshed +
                " HasUpdate=" + hasUpdate +
                " Current=v" + GetCurrentBuildVersion() +
                " Latest=" + (latestVersion == null ? "<null>" : "v" + latestVersion),
                "ModUpdater");

            ShowReleasePopup(message, canInstall);
        }

        public static async Task<bool> CheckRelease(bool beta = false)
        {
            return await RefreshRemoteData();
        }

        private static async Task<bool> RefreshRemoteData(
            bool updateUi = true,
            string currentApplicationVersion = null)
        {
            // Startup background task passes a value captured on the Unity main thread.
            // Manual calls keep the legacy behavior and read currentApplicationVersion here.
            if (string.IsNullOrWhiteSpace(currentApplicationVersion))
            {
                currentApplicationVersion = Application.version;
            }
            hasUpdate = false;
            isBroken = false;
            isChecked = false;
            latestVersion = null;
            latestTitle = null;
            downloadUrl = null;
            expectedSha256 = null;
            expectedSize = 0;
            latestAmongUsVersion = null;
            latestPublishedAt = null;
            releases = new JArray();
            lastCheckError = null;

            if (!TryGetBaseUri(out var baseUri))
            {
                lastCheckError = GetString("updateServerNotConfigured");
                isChecked = true;
                if (updateUi)
                {
                    SetMainMenuButtonText(GetString("updateButton"));
                }
                return false;
            }

            try
            {
                using HttpClient client = CreateHttpClient();

                string manifestJson = await GetHttpString(client, new Uri(baseUri, "manifest.json"));
                JObject manifest = JObject.Parse(manifestJson);

                int schema = 0;
                _ = int.TryParse(manifest["schema"]?.ToString(), out schema);
                if (schema != ManifestSchema)
                {
                    throw new InvalidDataException("Unsupported manifest schema: " + schema);
                }

                string versionText = manifest["version"]?.ToString()?.Trim().TrimStart('v');
                if (!Version.TryParse(versionText, out var parsedLatest))
                {
                    throw new InvalidDataException("Invalid manifest version.");
                }

                string amongUsVersion = manifest["amongUsVersion"]?.ToString()?.Trim();
                string fileRelative = manifest["file"]?.ToString()?.Trim();
                string sha256 = manifest["sha256"]?.ToString()?.Trim().ToUpperInvariant();
                long size = 0;
                _ = long.TryParse(manifest["size"]?.ToString(), out size);
                string title = manifest["title"]?.ToString()?.Trim();
                string publishedAt = manifest["publishedAt"]?.ToString()?.Trim();

                if (string.IsNullOrWhiteSpace(amongUsVersion))
                {
                    throw new InvalidDataException("amongUsVersion is missing.");
                }

                if (string.IsNullOrWhiteSpace(fileRelative))
                {
                    throw new InvalidDataException("file is missing.");
                }

                if (!IsValidSha256(sha256))
                {
                    throw new InvalidDataException("sha256 is invalid.");
                }

                if (size <= 0 || size > MaxDownloadBytes)
                {
                    throw new InvalidDataException("size is outside the accepted range.");
                }

                if (!TryResolveSameHostHttpsUri(baseUri, fileRelative, out var binaryUri))
                {
                    throw new InvalidDataException("file URL is not an allowed relative HTTPS URL.");
                }

                latestVersion = parsedLatest;
                latestTitle = string.IsNullOrWhiteSpace(title)
                    ? "v" + parsedLatest
                    : title;
                latestAmongUsVersion = amongUsVersion;
                latestPublishedAt = publishedAt;
                downloadUrl = binaryUri.AbsoluteUri;
                expectedSha256 = sha256;
                expectedSize = size;
                newFileName = Path.GetFileName(Assembly.GetExecutingAssembly().Location);

                Version currentVersion = GetCurrentBuildVersion();
                bool amongUsCompatible =
                    string.Equals(
                        currentApplicationVersion,
                        latestAmongUsVersion,
                        StringComparison.OrdinalIgnoreCase);

                hasUpdate =
                    amongUsCompatible &&
                    latestVersion.CompareTo(currentVersion) > 0;

                try
                {
                    string releasesJson = await GetHttpString(client, new Uri(baseUri, "releases.json"));
                    JObject releaseObject = JObject.Parse(releasesJson);

                    if (releaseObject["releases"] is JArray objectArray)
                    {
                        releases = objectArray;
                    }
                    else
                    {
                        releases = new JArray();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("releases.json の取得に失敗: " + ex.Message, "ModUpdater");
                    releases = new JArray();
                }

                isChecked = true;

                // バージョン番号は更新一覧側で表示し、トップボタンは固定文言＋通知「!」にする。
                if (updateUi)
                {
                    SetMainMenuButtonText(GetString("updateButton"));
                }

                return true;
            }
            catch (Exception ex)
            {
                isBroken = false;
                isChecked = true;
                hasUpdate = false;
                lastCheckError = ex.Message;
                Logger.Error(
                    "アップデート情報の取得に失敗しました。\n" + ex,
                    "ModUpdater",
                    false);
                if (updateUi)
                {
                    SetMainMenuButtonText(GetString("updateButton"));
                }
                return false;
            }
        }

        public static void StartUpdate(string url)
        {
            if (!hasUpdate ||
                string.IsNullOrWhiteSpace(downloadUrl) ||
                string.IsNullOrWhiteSpace(expectedSha256) ||
                expectedSize <= 0)
            {
                ShowPopup(
                    GetString("updateNoInstallableRelease"),
                    GetString("updateCloseButton"),
                    (Action)(() => CloseUpdatePopup()));
                return;
            }

            ShowPopup(GetString("updatePleaseWait"));
            _ = DownloadAndInstall();
        }

        private static async Task<bool> DownloadAndInstall()
        {
            string pendingPath = null;
            bool pendingOwnedByHelper = false;

            try
            {
                if (!TryGetBaseUri(out var baseUri))
                {
                    throw new InvalidOperationException(GetString("updateServerNotConfigured"));
                }

                if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var binaryUri) ||
                    !string.Equals(binaryUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(binaryUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Download URL is outside the configured HTTPS host.");
                }

                using HttpClient client = CreateHttpClient();
                using var response = await client.GetAsync(binaryUri, HttpCompletionOption.ResponseContentRead);

                if (!response.IsSuccessStatusCode || response.Content == null)
                {
                    throw new HttpRequestException("HTTP status: " + response.StatusCode);
                }

                byte[] bytes = await response.Content.ReadAsByteArrayAsync();

                if (bytes.LongLength != expectedSize)
                {
                    throw new InvalidDataException(
                        "Downloaded size mismatch. Expected=" +
                        expectedSize +
                        " Actual=" +
                        bytes.LongLength);
                }

                string actualSha256 = ComputeSha256(bytes);

                if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "SHA-256 mismatch. Expected=" +
                        expectedSha256 +
                        " Actual=" +
                        actualSha256);
                }

                string currentPath = Assembly.GetExecutingAssembly().Location;
                string currentDirectory = Path.GetDirectoryName(currentPath);
                string stagingDirectory = Path.Combine(currentDirectory, ".toh4e-em-update");

                Directory.CreateDirectory(stagingDirectory);

                pendingPath = Path.Combine(
                    stagingDirectory,
                    "TownOfHostForE_EM.dll.pending");

                File.WriteAllBytes(pendingPath, bytes);

                AssemblyName pendingName = AssemblyName.GetAssemblyName(pendingPath);
                AssemblyName currentName = Assembly.GetExecutingAssembly().GetName();

                if (!string.Equals(pendingName.Name, currentName.Name, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Assembly name mismatch. Expected=" +
                        currentName.Name +
                        " Actual=" +
                        pendingName.Name);
                }

                // EM_UPDATER_DEFERRED_INSTALL_V360:
                // EM_UPDATER_NULLABILITY_CLEANUP_V361:
                // The currently loaded plugin DLL cannot be reliably overwritten on Windows.
                // Re-verify the current/pending files, then hand replacement to an external
                // Windows PowerShell process that waits for this Among Us process to exit.
                string currentSha256 = ComputeSha256(File.ReadAllBytes(currentPath));

                if (!LaunchDeferredInstaller(
                    currentPath,
                    pendingPath,
                    currentSha256,
                    actualSha256,
                    bytes.LongLength))
                {
                    throw new IOException("Deferred updater helper failed to start.");
                }

                // Ownership of pendingPath is now transferred to the external helper.
                pendingOwnedByHelper = true;

                Logger.Info(
                    "アップデート適用を終了後処理へ予約: v" +
                    latestVersion +
                    " SHA256=" +
                    actualSha256,
                    "ModUpdater");

                ShowPopup(
                    GetString("updateRestart"),
                    GetString("updateQuitButton"),
                    (Action)(() => Application.Quit()));

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(
                    "アップデートに失敗しました。\n" + ex,
                    "ModUpdater",
                    false);

                if (!pendingOwnedByHelper && !string.IsNullOrWhiteSpace(pendingPath))
                {
                    TryDeleteFile(pendingPath);
                }

                ShowPopup(
                    GetString("updateFailed") +
                    "\n" +
                    ex.Message,
                    GetString("updateCloseButton"),
                    (Action)(() => CloseUpdatePopup()));

                return false;
            }
        }

        private static bool LaunchDeferredInstaller(
            string currentPath,
            string pendingPath,
            string expectedCurrentSha256,
            string expectedNewSha256,
            long expectedNewSize)
        {
            try
            {
                string stagingDirectory =
                    Path.GetDirectoryName(pendingPath)
                    ?? throw new InvalidOperationException(
                        "Pending updater directory could not be resolved.");
                string backupPath = GetBackupPath(currentPath);
                string resultPath = Path.Combine(
                    stagingDirectory,
                    "deferred-update-result.txt");

                TryDeleteFile(resultPath);

                string systemDirectory =
                    Environment.GetFolderPath(Environment.SpecialFolder.System);

                string powershellPath =
                    Path.Combine(
                        systemDirectory,
                        @"WindowsPowerShell\v1.0\powershell.exe");

                if (!File.Exists(powershellPath))
                {
                    powershellPath = "powershell.exe";
                }

                var startInfo =
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = powershellPath,
                        Arguments =
                            "-NoLogo -NoProfile -NonInteractive " +
                            "-ExecutionPolicy Bypass -EncodedCommand " +
                            GetDeferredInstallerEncodedCommand(),
                        WorkingDirectory = stagingDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                startInfo.EnvironmentVariables["TOH4E_WAIT_PID"] =
                    System.Diagnostics.Process.GetCurrentProcess().Id.ToString();

                startInfo.EnvironmentVariables["TOH4E_CURRENT_PATH"] =
                    currentPath;

                startInfo.EnvironmentVariables["TOH4E_PENDING_PATH"] =
                    pendingPath;

                startInfo.EnvironmentVariables["TOH4E_BACKUP_PATH"] =
                    backupPath;

                startInfo.EnvironmentVariables["TOH4E_RESULT_PATH"] =
                    resultPath;

                startInfo.EnvironmentVariables["TOH4E_EXPECTED_OLD_SHA"] =
                    expectedCurrentSha256;

                startInfo.EnvironmentVariables["TOH4E_EXPECTED_NEW_SHA"] =
                    expectedNewSha256;

                startInfo.EnvironmentVariables["TOH4E_EXPECTED_NEW_SIZE"] =
                    expectedNewSize.ToString();

                var process =
                    System.Diagnostics.Process.Start(startInfo);

                if (process == null)
                {
                    throw new InvalidOperationException(
                        "Windows PowerShell helper process was not created.");
                }

                int helperPid = process.Id;
                process.Dispose();

                Logger.Info(
                    "DEFERRED_INSTALLER_LAUNCHED " +
                    "HelperPid=" + helperPid +
                    " WaitPid=" +
                    System.Diagnostics.Process.GetCurrentProcess().Id +
                    " Pending=" +
                    Path.GetFileName(pendingPath),
                    "ModUpdater");

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(
                    "終了後アップデーターの起動に失敗しました。\n" + ex,
                    "ModUpdater",
                    false);
                return false;
            }
        }

        private static string GetDeferredInstallerEncodedCommand()
        {
            const string script = @"
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$currentPath = [string]$env:TOH4E_CURRENT_PATH
$pendingPath = [string]$env:TOH4E_PENDING_PATH
$backupPath = [string]$env:TOH4E_BACKUP_PATH
$resultPath = [string]$env:TOH4E_RESULT_PATH
$expectedOldSha = ([string]$env:TOH4E_EXPECTED_OLD_SHA).ToUpperInvariant()
$expectedNewSha = ([string]$env:TOH4E_EXPECTED_NEW_SHA).ToUpperInvariant()
$expectedNewSize = [long]$env:TOH4E_EXPECTED_NEW_SIZE
$waitPid = [int]$env:TOH4E_WAIT_PID

function Get-Sha256([string]$Path)
{
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Write-Result([string]$Status, [string]$Detail)
{
    $lines = @(
        $Status + '|UTC=' + [DateTime]::UtcNow.ToString('o'),
        $Detail
    )

    [IO.File]::WriteAllLines(
        $resultPath,
        $lines,
        [Text.UTF8Encoding]::new($false))
}

try
{
    if ([string]::IsNullOrWhiteSpace($currentPath) -or
        [string]::IsNullOrWhiteSpace($pendingPath) -or
        [string]::IsNullOrWhiteSpace($backupPath) -or
        [string]::IsNullOrWhiteSpace($resultPath))
    {
        throw 'Deferred updater path argument is empty.'
    }

    $target = Get-Process -Id $waitPid -ErrorAction SilentlyContinue

    if ($null -ne $target)
    {
        $target.WaitForExit()
        $target.Dispose()
    }

    Start-Sleep -Milliseconds 300

    if (-not (Test-Path -LiteralPath $currentPath -PathType Leaf))
    {
        throw 'Current plugin DLL is missing.'
    }

    if (-not (Test-Path -LiteralPath $pendingPath -PathType Leaf))
    {
        throw 'Pending plugin DLL is missing.'
    }

    $currentSha = Get-Sha256 $currentPath

    if ($currentSha -cne $expectedOldSha)
    {
        throw (
            'Current DLL SHA mismatch before replacement. Expected=' +
            $expectedOldSha +
            ' Actual=' +
            $currentSha)
    }

    $pendingSize = [long](Get-Item -LiteralPath $pendingPath).Length

    if ($pendingSize -ne $expectedNewSize)
    {
        throw (
            'Pending DLL size mismatch. Expected=' +
            $expectedNewSize +
            ' Actual=' +
            $pendingSize)
    }

    $pendingSha = Get-Sha256 $pendingPath

    if ($pendingSha -cne $expectedNewSha)
    {
        throw (
            'Pending DLL SHA mismatch. Expected=' +
            $expectedNewSha +
            ' Actual=' +
            $pendingSha)
    }

    if (Test-Path -LiteralPath $backupPath)
    {
        Remove-Item -LiteralPath $backupPath -Force
    }

    Copy-Item -LiteralPath $currentPath -Destination $backupPath -Force

    $backupSha = Get-Sha256 $backupPath

    if ($backupSha -cne $expectedOldSha)
    {
        throw (
            'Backup DLL SHA mismatch. Expected=' +
            $expectedOldSha +
            ' Actual=' +
            $backupSha)
    }

    try
    {
        Copy-Item -LiteralPath $pendingPath -Destination $currentPath -Force

        $installedSize = [long](Get-Item -LiteralPath $currentPath).Length

        if ($installedSize -ne $expectedNewSize)
        {
            throw (
                'Installed DLL size mismatch. Expected=' +
                $expectedNewSize +
                ' Actual=' +
                $installedSize)
        }

        $installedSha = Get-Sha256 $currentPath

        if ($installedSha -cne $expectedNewSha)
        {
            throw (
                'Installed DLL SHA mismatch. Expected=' +
                $expectedNewSha +
                ' Actual=' +
                $installedSha)
        }
    }
    catch
    {
        Copy-Item -LiteralPath $backupPath -Destination $currentPath -Force

        $rollbackSha = Get-Sha256 $currentPath

        if ($rollbackSha -cne $expectedOldSha)
        {
            throw (
                'Rollback verification failed. Expected=' +
                $expectedOldSha +
                ' Actual=' +
                $rollbackSha)
        }

        throw
    }

    Remove-Item -LiteralPath $pendingPath -Force

    Write-Result (
        'SUCCESS|NEW_SHA=' +
        $expectedNewSha +
        '|OLD_SHA=' +
        $expectedOldSha +
        '|SIZE=' +
        $expectedNewSize
    ) 'Deferred update applied and verified successfully.'

    exit 0
}
catch
{
    $failure = $_.Exception.ToString()
    $rollbackState = 'NOT_REQUIRED'

    try
    {
        if (Test-Path -LiteralPath $backupPath -PathType Leaf)
        {
            $currentIsOld =
                (Test-Path -LiteralPath $currentPath -PathType Leaf) -and
                ((Get-Sha256 $currentPath) -ceq $expectedOldSha)

            if (-not $currentIsOld)
            {
                Copy-Item -LiteralPath $backupPath -Destination $currentPath -Force

                $rollbackSha = Get-Sha256 $currentPath

                if ($rollbackSha -cne $expectedOldSha)
                {
                    throw (
                        'Rollback verification failed. Expected=' +
                        $expectedOldSha +
                        ' Actual=' +
                        $rollbackSha)
                }

                $rollbackState = 'RESTORED'
            }
            else
            {
                $rollbackState = 'CURRENT_ALREADY_OLD'
            }
        }
    }
    catch
    {
        $rollbackState = 'ROLLBACK_FAILED: ' + $_.Exception.Message
    }

    try
    {
        if (Test-Path -LiteralPath $pendingPath)
        {
            Remove-Item -LiteralPath $pendingPath -Force
        }
    }
    catch
    {
    }

    try
    {
        Write-Result (
            'FAIL|ROLLBACK=' +
            $rollbackState
        ) $failure
    }
    catch
    {
    }

    exit 1
}
";

            return Convert.ToBase64String(
                System.Text.Encoding.Unicode.GetBytes(script));
        }

        private static void CleanupConfirmedBackup()
        {
            try
            {
                string currentPath = Assembly.GetExecutingAssembly().Location;
                string backupPath = GetBackupPath(currentPath);

                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                    Logger.Info(
                        Path.GetFileName(backupPath) +
                        " を正常起動確認後に削除",
                        "ModUpdater");
                }

                string stagingDirectory = Path.Combine(
                    Path.GetDirectoryName(currentPath),
                    ".toh4e-em-update");

                if (Directory.Exists(stagingDirectory))
                {
                    foreach (string stalePending in Directory.EnumerateFiles(
                        stagingDirectory,
                        "*.pending"))
                    {
                        TryDeleteFile(stalePending);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(
                    "アップデート一時ファイルのクリーンアップに失敗: " +
                    ex.Message,
                    "ModUpdater");
            }
        }

        private static string BuildReleaseListText()
        {
            var builder = new StringBuilder();

            builder.AppendLine(GetString("updateListTitle"));
            builder.AppendLine(
                GetString("updateCurrentVersion") +
                ": v" +
                GetCurrentBuildVersion());

            if (!string.IsNullOrWhiteSpace(lastCheckError))
            {
                builder.AppendLine();
                builder.AppendLine(GetString("updateCheckFailed"));
                builder.AppendLine(lastCheckError);
                return builder.ToString();
            }

            if (latestVersion == null)
            {
                builder.AppendLine();
                builder.AppendLine(GetString("updateNoInstallableRelease"));
                return builder.ToString();
            }

            builder.AppendLine(
                GetString("updateLatestVersion") +
                ": v" +
                latestVersion);

            // EM_UPDATER_DISPLAY_COMPAT_V2:
            // exact version (latestAmongUsVersion) は互換性判定専用。画面には対応系統だけ表示する。

            bool compatible =
                string.Equals(
                    Application.version,
                    latestAmongUsVersion,
                    StringComparison.OrdinalIgnoreCase);

            if (!compatible)
            {
                builder.AppendLine();
                builder.AppendLine(
                    GetString("updateAmongUsMismatch") +
                    " (" +
                    Application.version +
                    " -> " +
                    latestAmongUsVersion +
                    ")");
            }
            else if (hasUpdate)
            {
                builder.AppendLine();
                builder.AppendLine(GetString("updateAvailable"));
            }
            else
            {
                builder.AppendLine();
                builder.AppendLine(GetString("updateAlreadyLatest"));
            }

            if (releases != null && releases.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("────────────");

                int rowCount = 0;

                for (int releaseIndex = 0; releaseIndex < releases.Count; releaseIndex++)
                {
                    JToken release = releases[releaseIndex];
                    if (rowCount >= MaxReleaseRows)
                    {
                        break;
                    }

                    string version =
                        release["version"]?.ToString()?.Trim().TrimStart('v');
                    string title =
                        release["title"]?.ToString()?.Trim();

                    if (string.IsNullOrWhiteSpace(version))
                    {
                        continue;
                    }

                    builder.Append("v");
                    builder.Append(version);

                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        builder.Append("  ");
                        builder.Append(title);
                    }

                    builder.AppendLine();

                    if (release["changes"] is JArray changes)
                    {
                        int changeCount = 0;

                        for (int changeIndex = 0; changeIndex < changes.Count; changeIndex++)
                        {
                            JToken change = changes[changeIndex];
                            if (changeCount >= MaxChangesPerRelease)
                            {
                                break;
                            }

                            string changeText = change?.ToString()?.Trim();

                            if (!string.IsNullOrWhiteSpace(changeText))
                            {
                                builder.Append("・");
                                builder.AppendLine(changeText);
                                changeCount++;
                            }
                        }
                    }

                    rowCount++;
                }
            }

            return builder.ToString();
        }

        private static bool TryGetBaseUri(out Uri baseUri)
        {
            baseUri = null;

            string configured = Main.UpdateBaseURL?.Value?.Trim();

            if (string.IsNullOrWhiteSpace(configured))
            {
                return false;
            }

            if (!configured.EndsWith("/", StringComparison.Ordinal))
            {
                configured += "/";
            }

            if (!Uri.TryCreate(configured, UriKind.Absolute, out var parsed))
            {
                return false;
            }

            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            baseUri = parsed;
            return true;
        }

        private static bool TryResolveSameHostHttpsUri(
            Uri baseUri,
            string relative,
            out Uri result)
        {
            result = null;

            if (!Uri.TryCreate(relative, UriKind.Relative, out var relativeUri))
            {
                return false;
            }

            Uri resolved = new(baseUri, relativeUri);

            if (!string.Equals(resolved.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(resolved.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            result = resolved;
            return true;
        }

        private static HttpClient CreateHttpClient()
        {
            HttpClient client = new();
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.Add(
                "User-Agent",
                "TownOfHostForE-EM-Updater/" + Main.PleviewPluginVersion);
            return client;
        }

        private static async Task<string> GetHttpString(HttpClient client, Uri uri)
        {
            using var response = await client.GetAsync(
                uri,
                HttpCompletionOption.ResponseContentRead);

            if (!response.IsSuccessStatusCode || response.Content == null)
            {
                throw new HttpRequestException(
                    "HTTP " +
                    response.StatusCode +
                    " " +
                    uri.AbsoluteUri);
            }

            return await response.Content.ReadAsStringAsync();
        }

        private static Version GetCurrentBuildVersion()
        {
            string displayVersion =
                Main.PleviewPluginVersion?.Trim().TrimStart('v');

            if (Version.TryParse(displayVersion, out var parsed))
            {
                return parsed;
            }

            return Main.version;
        }

        private static bool IsValidSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            {
                return false;
            }

            foreach (char c in value)
            {
                bool isHex =
                    (c >= '0' && c <= '9') ||
                    (c >= 'A' && c <= 'F') ||
                    (c >= 'a' && c <= 'f');

                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using SHA256 sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(bytes);
            return BitConverter
                .ToString(hash)
                .Replace("-", string.Empty)
                .ToUpperInvariant();
        }

        private static string GetBackupPath(string currentPath)
        {
            return currentPath + ".toh4e-em.bak";
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(
                    "一時ファイル削除に失敗: " +
                    path +
                    " / " +
                    ex.Message,
                    "ModUpdater");
            }
        }

        private static void SetMainMenuButtonText(string text)
        {
            try
            {
                if (MainMenuManagerPatch.UpdateButton == null ||
                    MainMenuManagerPatch.UpdateButton.Button == null)
                {
                    return;
                }

                MainMenuManagerPatch.UpdateButton.Button.gameObject.SetActive(true);
                var tmp =
                    MainMenuManagerPatch.UpdateButton.Button
                        .transform
                        .Find("FontPlacer/Text_TMP")
                        .GetComponent<TMPro.TMP_Text>();

                if (tmp != null)
                {
                    tmp.SetText(text);
                }

                bool updatePopupVisible =
                    InfoPopup != null &&
                    InfoPopup.gameObject != null &&
                    InfoPopup.gameObject.activeSelf;

                MainMenuManagerPatch.SetUpdateNotificationBadge(
                    hasUpdate && !updatePopupVisible);
            }
            catch (Exception ex)
            {
                Logger.Warn(
                    "Updateボタン表示更新に失敗: " +
                    ex.Message,
                    "ModUpdater");
            }
        }

        private static void CaptureReleasePopupButtonBase(UnityEngine.Transform closeButton)
        {
            if (releasePopupButtonBaseCaptured || closeButton == null)
            {
                return;
            }

            releasePopupButtonBasePosition = closeButton.localPosition;
            releasePopupButtonBaseCaptured = true;
        }

        private static UnityEngine.Transform GetOrCreateReleaseInstallButton(
            UnityEngine.Transform closeButton)
        {
            if (InfoPopup == null || closeButton == null)
            {
                return null;
            }

            var existing =
                InfoPopup.transform.FindChild(ReleaseInstallButtonName);

            if (existing != null)
            {
                return existing;
            }

            var installObject =
                UnityEngine.Object.Instantiate(closeButton.gameObject);

            installObject.name = ReleaseInstallButtonName;
            installObject.transform.SetParent(closeButton.parent, false);
            installObject.transform.localPosition = closeButton.localPosition;
            installObject.transform.localRotation = closeButton.localRotation;
            installObject.transform.localScale = closeButton.localScale;

            return installObject.transform;
        }

        private static void ConfigurePopupButton(
            UnityEngine.Transform button,
            string label,
            Action action,
            bool visible)
        {
            if (button == null)
            {
                return;
            }

            button.gameObject.SetActive(visible);

            if (!visible)
            {
                return;
            }

            var translator =
                button.GetComponentInChildren<TextTranslatorTMP>();

            if (translator != null)
            {
                translator.enabled = false;
            }

            var buttonText =
                button.GetComponentInChildren<TMPro.TMP_Text>();

            if (buttonText != null)
            {
                buttonText.SetText(label);
            }

            var passiveButton =
                button.GetComponent<PassiveButton>();

            if (passiveButton == null)
            {
                button.gameObject.SetActive(false);
                return;
            }

            passiveButton.OnClick = new();
            passiveButton.OnClick.AddListener(action);
        }

        private static void CloseUpdatePopup()
        {
            if (InfoPopup != null)
            {
                InfoPopup.gameObject.SetActive(false);
            }

            MainMenuManagerPatch.SetUpdateNotificationBadge(hasUpdate);
        }

        private static void ShowReleasePopup(
            string message,
            bool updateAvailable)
        {
            if (InfoPopup == null)
            {
                return;
            }

            MainMenuManagerPatch.SetUpdateNotificationBadge(false);

            InfoPopup.Show(message);

            var closeButton =
                InfoPopup.transform.FindChild("ExitGame");

            if (closeButton == null)
            {
                return;
            }

            CaptureReleasePopupButtonBase(closeButton);

            var installButton =
                GetOrCreateReleaseInstallButton(closeButton);

            if (updateAvailable && installButton != null)
            {
                // EM_UPDATER_RELEASE_TWO_BUTTONS_V316:
                // 左=更新実行、右=閉じる。どちらも同時表示し、更新しない選択肢を常に残す。
                installButton.localPosition =
                    releasePopupButtonBasePosition +
                    new UnityEngine.Vector3(-1.35f, 0f, 0f);

                closeButton.localPosition =
                    releasePopupButtonBasePosition +
                    new UnityEngine.Vector3(1.35f, 0f, 0f);

                ConfigurePopupButton(
                    installButton,
                    GetString("updateInstallButton"),
                    (Action)(() => StartUpdate(downloadUrl)),
                    true);

                ConfigurePopupButton(
                    closeButton,
                    GetString("updateCloseButton"),
                    (Action)(() => CloseUpdatePopup()),
                    true);

                return;
            }

            closeButton.localPosition =
                releasePopupButtonBasePosition;

            if (installButton != null)
            {
                installButton.gameObject.SetActive(false);
            }

            ConfigurePopupButton(
                closeButton,
                GetString("updateCloseButton"),
                (Action)(() => CloseUpdatePopup()),
                true);
        }

        private static void ShowPopup(
            string message,
            string buttonLabel = null,
            Action buttonAction = null)
        {
            if (InfoPopup == null)
            {
                return;
            }

            MainMenuManagerPatch.SetUpdateNotificationBadge(false);

            InfoPopup.Show(message);

            var button =
                InfoPopup.transform.FindChild("ExitGame");

            if (button == null)
            {
                return;
            }

            CaptureReleasePopupButtonBase(button);

            if (releasePopupButtonBaseCaptured)
            {
                button.localPosition =
                    releasePopupButtonBasePosition;
            }

            var installButton =
                InfoPopup.transform.FindChild(ReleaseInstallButtonName);

            if (installButton != null)
            {
                installButton.gameObject.SetActive(false);
            }

            bool showButton =
                !string.IsNullOrWhiteSpace(buttonLabel) &&
                buttonAction != null;

            ConfigurePopupButton(
                button,
                buttonLabel,
                buttonAction,
                showButton);
        }
    }
}
