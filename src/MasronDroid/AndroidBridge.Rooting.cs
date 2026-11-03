using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MasronDroid;

public sealed record RecoveryProfile(string Name, bool TwrpCli);
public sealed record MagiskRecoveryResult(bool RootVerified, bool RebootRequested, string Recovery, string Output);

public sealed partial class AndroidBridge
{
    private async Task<Target?> WaitRootTarget(string serial, string[] states, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            var current = (await List("adb")).SingleOrDefault(device => device.Serial == serial);
            if (current != null && states.Contains(current.State)) return current;
            await Task.Delay(500);
        } while (timer.Elapsed < timeout);
        return null;
    }

    private async Task<(string Boot, int Lines)> RecoveryCursor(Target target)
    {
        var cursor = await ReadFacts(target, """
            echo "Boot=$(cat /proc/sys/kernel/random/boot_id)"
            echo "Lines=$(wc -l < /tmp/recovery.log)"
            """, TimeSpan.FromSeconds(8));
        if (!cursor.TryGetValue("Boot", out var boot) || !int.TryParse(cursor.GetValueOrDefault("Lines"), out int lines)
            || lines < 0) throw new BridgeException("RootFlowLogUnavailable");
        return (boot, lines);
    }

    internal static bool MagiskInstallConfirmed(string log)
        => MagiskOperationConfirmed(log, false);

    internal static bool MagiskUninstallConfirmed(string log)
        => MagiskOperationConfirmed(log, true);

    private static bool MagiskOperationConfirmed(string log, bool uninstall)
    {
        var statuses = Regex.Matches(log, @"(?im)Install from ADB complete \(status:\s*(\d+)\)|Updater process ended with (?:RC=|ERROR:\s*)(\d+)");
        if (statuses.Count > 0)
        {
            var last = statuses[^1];
            string status = last.Groups[1].Success ? last.Groups[1].Value : last.Groups[2].Value;
            if (status != "0") return false;
        }
        return Regex.IsMatch(log, uninstall ? @"(?im)Magisk[^\r\n]*\bUninstaller\b" : @"(?im)Magisk[^\r\n]*\bInstaller\b")
            && (!uninstall || Regex.IsMatch(log, @"(?im)- Removing Magisk files"))
            && Regex.IsMatch(log, @"(?im)^\s*(?:ui_print\s+)?-\s*Done\s*$")
            && !Regex.IsMatch(log, @"(?im)^\s*(?:Error installing zip|Installation aborted|! Unable|! Boot image patching failed)");
    }

    public async Task<MagiskRecoveryResult> FlashMagiskRecovery(Target initial, string file, string version,
        Action<string> progress, bool knownSideloadRecovery = false, TimeSpan? transitionTimeout = null)
    {
        if (initial is not { Tool: "adb", State: "device" or "recovery" or "sideload" })
            throw new BridgeException("WrongMode");
        if (initial.State == "sideload" && !knownSideloadRecovery) throw new BridgeException("RootRecoveryRequired");
        if (initial.State == "device" && (await ReadMagiskStatus(initial)).State == "RootDaemonLoaded")
            throw new BridgeException("RootAlreadyInstalled");
        string staged = await StageSideload(initial, file);
        TimeSpan transition = transitionTimeout ?? TimeSpan.FromSeconds(90);
        Target current = initial;
        RecoveryProfile? profile = null;
        (string Boot, int Lines)? cursor = null;
        if (current.State == "device")
        {
            progress("RootFlowRecovery");
            await Execute(current, DeviceAction.Recovery);
            current = await WaitRootTarget(initial.Serial, ["recovery"], transition)
                ?? throw new BridgeException("RootFlowRecoveryUnavailable");
        }
        if (current.State == "recovery")
        {
            progress("RootFlowIdentify");
            profile = await ReadRecoveryProfile(current) ?? throw new BridgeException("RootRecoveryRequired");
            var existing = await ReadFacts(current,
                "if [ -f /data/adb/magisk/util_functions.sh ]; then echo ExistingMagisk=1; fi", TimeSpan.FromSeconds(8));
            if (existing.GetValueOrDefault("ExistingMagisk") == "1") throw new BridgeException("RootAlreadyInstalled");
            cursor = await RecoveryCursor(current);
            progress("RootFlowSideload");
            await EnterSideload(current, transition);
            current = await WaitRootTarget(initial.Serial, ["sideload"], transition)
                ?? throw new BridgeException("SideloadNotReady");
        }
        await VerifyTarget(current);
        progress("RootFlowSending");
        CommandStarting?.Invoke("adb -s " + current.Serial + " sideload " + staged);
        var transfer = await tools.Run("adb", ["-s", current.Serial, "sideload", staged], TimeSpan.FromMinutes(10));
        progress("RootFlowVerify");
        current = await WaitRootTarget(initial.Serial, ["recovery", "device"], transitionTimeout ?? TimeSpan.FromSeconds(45))
            ?? throw new BridgeException("RootFlowUnconfirmed", transfer.Output);
        string recovery = profile?.Name ?? "Custom recovery";
        string evidence = transfer.Output;
        if (current.State == "recovery")
        {
            if (cursor == null) throw new BridgeException("RootFlowUnconfirmed", transfer.Output);
            var after = await RecoveryCursor(current);
            bool newBoot = after.Boot != cursor.Value.Boot;
            if (newBoot && profile?.TwrpCli != false || !newBoot && after.Lines <= cursor.Value.Lines)
                throw new BridgeException("RootFlowUnconfirmed", evidence);
            // Generic reboot sideload starts a new kernel and a fresh /tmp log.
            int start = newBoot ? 1 : cursor.Value.Lines + 1;
            var result = await tools.Run("adb", ["-s", current.Serial, "shell", "tail", "-n", "+" + start, "/tmp/recovery.log"], TimeSpan.FromSeconds(10));
            evidence += Environment.NewLine + result.Output;
            if (!result.Ok || !MagiskInstallConfirmed(result.Output))
                throw new BridgeException("RootFlowUnconfirmed", evidence);
            await VerifyTarget(current);
            progress("RootFlowReboot");
            CommandStarting?.Invoke("adb -s " + current.Serial + " reboot");
            var reboot = await tools.Run("adb", ["-s", current.Serial, "reboot"], TimeSpan.FromSeconds(10));
            if (!reboot.Ok) return new(false, false, recovery, evidence + Environment.NewLine + reboot.Output);
            progress("RootFlowAndroid");
            var android = await WaitRootTarget(initial.Serial, ["device"], transitionTimeout ?? TimeSpan.FromMinutes(2));
            if (android == null) return new(false, true, recovery, evidence);
            current = android;
        }
        else throw new BridgeException("RootFlowUnconfirmed", evidence);
        try
        {
            await VerifyTarget(current);
            var root = await ReadMagiskStatus(current);
            string expected = version.TrimStart('v', 'V');
            bool verified = root.State == "RootDaemonLoaded" && (root.Version == expected
                || root.Version.StartsWith(expected + ":", StringComparison.Ordinal)
                || root.Version.StartsWith(expected + "(", StringComparison.Ordinal));
            return new(verified, true, recovery, evidence);
        }
        catch (BridgeException ex) { return new(false, true, recovery, evidence + Environment.NewLine + ex.Message); }
    }

    public async Task<string> UninstallMagiskRecovery(Target initial, string file, Action<string> progress,
        TimeSpan? transitionTimeout = null)
    {
        if (initial is not { Tool: "adb", State: "device" or "recovery" }) throw new BridgeException("WrongMode");
        if (initial.State == "device" && (await ReadMagiskStatus(initial)).State != "RootDaemonLoaded")
            throw new BridgeException("RootUninstallUnavailable");
        string staged = await StageSideload(initial, file);
        TimeSpan transition = transitionTimeout ?? TimeSpan.FromSeconds(90);
        var current = initial;
        if (current.State == "device")
        {
            progress("RootFlowRecovery");
            await Execute(current, DeviceAction.Recovery);
            current = await WaitRootTarget(initial.Serial, ["recovery"], transition)
                ?? throw new BridgeException("RootFlowRecoveryUnavailable");
        }
        progress("RootFlowIdentify");
        if ((await ReadRecoveryProfile(current))?.TwrpCli != true)
            throw new BridgeException("RootUninstallRecovery");
        var before = await ReadFacts(current, """
            echo "CleanupUid=$(id -u)"
            if [ -d /data/adb ] && awk '$2 == "/data" { found=1 } END { exit !found }' /proc/mounts; then echo CleanupData=1; fi
            if [ -e /data/adb/ksud ] || [ -e /data/adb/apd ]; then echo OtherRoot=1; fi
            """, TimeSpan.FromSeconds(8));
        if (before.GetValueOrDefault("CleanupUid") != "0" || before.GetValueOrDefault("CleanupData") != "1"
            || before.GetValueOrDefault("OtherRoot") == "1")
            throw new BridgeException("RootUninstallUnavailable");
        var cursor = await RecoveryCursor(current);
        string remote = "/tmp/MasronDroid-" + Guid.NewGuid().ToString("N") + "-uninstall.zip";
        progress("RootUninstallTransfer");
        await VerifyTarget(current);
        CommandStarting?.Invoke("adb -s " + current.Serial + " push " + staged + " " + remote);
        var push = await tools.Run("adb", ["-s", current.Serial, "push", staged, remote], TimeSpan.FromMinutes(2));
        if (!push.Ok) throw new BridgeException("ToolFailed", push.Output);
        progress("RootUninstallWorking");
        await VerifyTarget(current);
        CommandStarting?.Invoke("adb -s " + current.Serial + " shell twrp install " + remote);
        var install = await tools.Run("adb", ["-s", current.Serial, "shell", "twrp", "install", remote], TimeSpan.FromMinutes(10));
        var after = await RecoveryCursor(current);
        if (after.Boot != cursor.Boot || after.Lines <= cursor.Lines)
            throw new BridgeException("RootUninstallUnconfirmed", install.Output);
        var log = await tools.Run("adb", ["-s", current.Serial, "shell", "tail", "-n", "+" + (cursor.Lines + 1), "/tmp/recovery.log"], TimeSpan.FromSeconds(10));
        string evidence = install.Output + Environment.NewLine + log.Output;
        if (!install.Ok || !log.Ok || !MagiskUninstallConfirmed(log.Output))
            throw new BridgeException("RootUninstallUnconfirmed", evidence);
        progress("RootUninstallVerify");
        var cleanup = await ReadFacts(current, """
            clean=1
            [ "$(id -u)" = 0 ] || clean=0
            [ -d /data/adb ] || clean=0
            awk '$2 == "/data" { found=1 } END { exit !found }' /proc/mounts || clean=0
            for p in /data/adb/*magisk* /data/adb/modules* /data/adb/post-fs-data.d /data/adb/service.d; do
              if [ -e "$p" ] || [ -L "$p" ]; then clean=0; fi
            done
            echo "RootCleanup=$clean"
            """, TimeSpan.FromSeconds(8));
        if (cleanup.GetValueOrDefault("RootCleanup") != "1")
            throw new BridgeException("RootUninstallUnconfirmed", evidence);
        await VerifyTarget(current);
        progress("RootFlowReboot");
        CommandStarting?.Invoke("adb -s " + current.Serial + " reboot");
        var reboot = await tools.Run("adb", ["-s", current.Serial, "reboot"], TimeSpan.FromSeconds(10));
        if (!reboot.Ok) throw new BridgeException("RootUninstallReboot", evidence);
        progress("RootUninstallAndroid");
        current = await WaitRootTarget(initial.Serial, ["device"], transitionTimeout ?? TimeSpan.FromMinutes(2))
            ?? throw new BridgeException("RootUninstallPending", evidence);
        var timer = Stopwatch.StartNew();
        bool ready = false;
        do
        {
            var boot = await ReadFacts(current, "echo RootBoot=$(getprop sys.boot_completed)", TimeSpan.FromSeconds(8));
            if (boot.GetValueOrDefault("RootBoot") == "1") { ready = true; break; }
            await Task.Delay(500);
        } while (timer.Elapsed < (transitionTimeout ?? TimeSpan.FromMinutes(2)));
        if (!ready) throw new BridgeException("RootUninstallPending", evidence);
        var status = await ReadMagiskStatus(current);
        if (status.State == "RootDaemonLoaded") throw new BridgeException("RootUninstallUnconfirmed", evidence);
        if (status.AppInstalled)
        {
            progress("RootUninstallApk");
            await VerifyTarget(current);
            CommandStarting?.Invoke("adb -s " + current.Serial + " uninstall com.topjohnwu.magisk");
            var removed = await tools.Run("adb", ["-s", current.Serial, "uninstall", "com.topjohnwu.magisk"], TimeSpan.FromSeconds(30));
            if (!removed.Ok || removed.Output.Trim() != "Success")
                throw new BridgeException("RootUninstallApkFailed", removed.Output);
        }
        status = await ReadMagiskStatus(current);
        if (status.State != "RootNotDetected" || status.AppInstalled)
            throw new BridgeException("RootUninstallUnconfirmed", evidence);
        return evidence;
    }
}
