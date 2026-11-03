using System.IO;
using MasronDroid;

namespace MasronDroid.Checks;

internal static class RootFlowChecks
{
    private sealed class Transport : IPlatformTools
    {
        internal string State = "device", Serial = "flow-phone", Facts = "FoxBoot=1\nFoxVersion=R12\nTwrpCli=1";
        internal string Log = "Magisk 31.1 Installer\n- Done\nUpdater process ended with RC=0";
        internal bool MissingRecovery, StaleLog, RootMissing, MissingLog, SideloadExitFailure, RebootFails;
        internal bool Uninstall, MagiskInitiallyPresent, CleanupFails, OtherRoot, AppRemoved, RootSurvives, DataUnavailable, RecoveryRootPresent;
        private bool sideloadBoot;
        internal bool Sent;
        internal List<string[]> Commands = [];
        public bool Has(string tool) => true;
        public Task<ToolResult> Run(string tool, IReadOnlyList<string> args, TimeSpan timeout)
        {
            var command = args.ToArray();
            Commands.Add(command);
            if (args[0] == "devices") return Task.FromResult(new ToolResult(true, Serial + "\t" + State + "\n"));
            if (args.Count < 3 || args[1] != "flow-phone") throw new Exception("Unscoped device command");
            var action = command.Skip(2).ToArray();
            string output = "";
            if (action.SequenceEqual(["reboot", "recovery"])) { State = "recovery"; if (MissingRecovery) Serial = "other-phone"; }
            else if (action.Any(part => part.Contains("FoxBoot="))) output = Facts;
            else if (action.Any(part => part.Contains("ExistingMagisk="))) output = RecoveryRootPresent ? "ExistingMagisk=1" : "";
            else if (action.Any(part => part.Contains("Lines=$(wc"))) output = MissingLog ? "" :
                "Boot=" + (sideloadBoot ? "new-sideload-boot" : "same-boot") + "\nLines=" + (Sent && !StaleLog ? 24 : 10);
            else if (action.SequenceEqual(["shell", "twrp", "sideload"]) || action.SequenceEqual(["reboot", "sideload"]))
            { State = "sideload"; sideloadBoot = action[0] == "reboot"; }
            else if (action[0] == "sideload")
            {
                if (!File.Exists(action[1]) || !action[1].Contains("SideloadPackages")) throw new Exception("Package not staged");
                Sent = true;
                State = "recovery";
                if (SideloadExitFailure) return Task.FromResult(new ToolResult(false, "failed to read command: Success"));
            }
            else if (action.SequenceEqual(["shell", "tail", "-n", sideloadBoot ? "+1" : "+11", "/tmp/recovery.log"])) output = Log;
            else if (action.SequenceEqual(["reboot"]))
            {
                if (RebootFails) return Task.FromResult(new ToolResult(false, "reboot rejected"));
                State = "device";
            }
            else if (action.Any(part => part.Contains("CleanupUid="))) output = "CleanupUid=0\n"
                + (DataUnavailable ? "" : "CleanupData=1\n") + (OtherRoot ? "OtherRoot=1" : "");
            else if (action.Any(part => part.Contains("RootCleanup="))) output = "RootCleanup=" + (CleanupFails ? "0" : "1");
            else if (action.Any(part => part.Contains("RootBoot="))) output = "RootBoot=1";
            else if (action[0] == "push")
            {
                if (!action[2].EndsWith("-uninstall.zip") || !File.Exists(action[1])) throw new Exception("Uninstaller path must preserve uninstall filename");
            }
            else if (action.Length == 4 && action.Take(3).SequenceEqual(["shell", "twrp", "install"])) Sent = true;
            else if (action.SequenceEqual(["uninstall", "com.topjohnwu.magisk"])) { AppRemoved = true; output = "Success"; }
            else if (action.Any(part => part.Contains("Daemon=$v")))
                output = ((Uninstall && (!Rebooted || RootSurvives)) || MagiskInitiallyPresent || (!Uninstall && Rebooted && !RootMissing)
                    ? "Daemon=31.1:MAGISK:R\n" : "") + (AppRemoved ? "" : "App=1\nAppVersion=31.1");
            else throw new Exception("Unexpected command: " + string.Join(" ", action));
            return Task.FromResult(new ToolResult(true, output));
        }
        internal bool Rebooted => Commands.Any(command => command.SequenceEqual(["-s", "flow-phone", "reboot"]));
    }

    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
            Console.WriteLine("PASS " + message);
        }
        string folder = Directory.CreateTempSubdirectory("MasronDroid-root-flow-").FullName;
        string file = Path.Combine(folder, "Magisk.zip");
        File.WriteAllBytes(file, [1, 2]);
        var target = new Target("adb", "flow-phone", "device", "Simulated phone");
        async Task<MagiskRecoveryResult> Flow(Transport fake, List<string>? stages = null) =>
            await new AndroidBridge(fake).FlashMagiskRecovery(target, file, "v31.1", stage => stages?.Add(stage),
                transitionTimeout: TimeSpan.FromMilliseconds(50));
        async Task Reject(Transport fake, string key)
        {
            try { await Flow(fake); throw new Exception("Expected rejection: " + key); }
            catch (BridgeException ex) when (ex.Key == key) { Check(!fake.Rebooted, key + " never reboots"); }
        }
        try
        {
            foreach (string facts in new[] {
                "FoxBoot=1\nFoxVersion=R12\nTwrpCli=1",
                "TwrpBoot=1\nTwrpVersion=3.7\nTwrpCli=1",
                "PbrpVersion=4.0\nTwrpCli=1",
                "ShrpVersion=3.2\nTwrpCli=1",
                "RecoveryBuild=Infinity-X Recovery\nTwrpCli=0",
                "RecoveryName=Lineage Recovery\nTwrpCli=0" })
            {
                var fake = new Transport { Facts = facts };
                var stages = new List<string>();
                var result = await Flow(fake, stages);
                Check(result.RootVerified && fake.Sent && fake.Rebooted, facts.Split('\n')[0] + " completes full flow");
                Check(stages.SequenceEqual(new[] { "RootFlowRecovery", "RootFlowIdentify", "RootFlowSideload",
                    "RootFlowSending", "RootFlowVerify", "RootFlowReboot", "RootFlowAndroid" }), "Stage order includes verified reboot");
                Check(fake.Commands.Any(command => command.SequenceEqual(facts.Contains("TwrpCli=1")
                    ? ["-s", "flow-phone", "shell", "twrp", "sideload"] : ["-s", "flow-phone", "reboot", "sideload"])),
                    "Selects the recovery capability command");
            }
            await Reject(new Transport { Facts = "" }, "RootRecoveryRequired");
            await Reject(new Transport { MissingLog = true }, "RootFlowLogUnavailable");
            await Reject(new Transport { MissingRecovery = true }, "RootFlowRecoveryUnavailable");
            await Reject(new Transport { StaleLog = true }, "RootFlowUnconfirmed");
            await Reject(new Transport { Log = "Magisk 31.1 Installer\n! Boot image patching failed\nInstall from ADB complete (status: 1)" }, "RootFlowUnconfirmed");
            await Reject(new Transport { Log = "Total xfer: 1.00x" }, "RootFlowUnconfirmed");
            var blocked = new Transport { MissingLog = true };
            await Reject(blocked, "RootFlowLogUnavailable");
            Check(!blocked.Sent, "Missing log stops before package transmission");
            var recovery = new Transport { State = "recovery" };
            var resumed = await new AndroidBridge(recovery).FlashMagiskRecovery(target with { State = "recovery" }, file,
                "v31.1", _ => { }, transitionTimeout: TimeSpan.FromMilliseconds(50));
            Check(resumed.RootVerified && !recovery.Commands.Any(command => command.SequenceEqual(["-s", "flow-phone", "reboot", "recovery"])),
                "Already in recovery continues without an extra recovery reboot");
            var transferError = await Flow(new Transport { SideloadExitFailure = true });
            Check(transferError.RootVerified, "Fresh recovery success can confirm a nonzero ADB transfer exit");
            var rebootError = await Flow(new Transport { RebootFails = true });
            Check(!rebootError.RootVerified && !rebootError.RebootRequested, "Failed reboot returns a manual restart outcome");
            var pending = await Flow(new Transport { RootMissing = true });
            Check(!pending.RootVerified && pending.RebootRequested, "Missing root readback is reported as pending");
            Check(!AndroidBridge.MagiskInstallConfirmed("Magisk Installer\n- Done\nUpdater process ended with ERROR: 1"),
                "A failed recovery status overrides Done");
            await Reject(new Transport { MagiskInitiallyPresent = true }, "RootAlreadyInstalled");
            await Reject(new Transport { RecoveryRootPresent = true }, "RootAlreadyInstalled");
            var uninstall = new Transport { Uninstall = true, Log = "Magisk 31.1 Uninstaller\n- Removing Magisk files\n- Done\nUpdater process ended with RC=0" };
            await new AndroidBridge(uninstall).UninstallMagiskRecovery(target, file, _ => { }, TimeSpan.FromMilliseconds(50));
            Check(uninstall.Rebooted && uninstall.AppRemoved, "Uninstall verifies cleanup, reboots and removes the manager APK");
            Check(!AndroidBridge.MagiskInstallConfirmed(uninstall.Log), "An uninstaller log never confirms root installation");
            Check(!uninstall.Commands.Any(command => command.Contains("sideload")) &&
                uninstall.Commands.Any(command => command.Contains("install") && command.Last().EndsWith("-uninstall.zip")),
                "Uninstaller uses the named recovery ZIP rather than sideload renaming");
            foreach (var broken in new[] {
                new Transport { Uninstall = true, OtherRoot = true },
                new Transport { Uninstall = true, DataUnavailable = true },
                new Transport { Uninstall = true, Facts = "RecoveryName=Lineage Recovery\nTwrpCli=0" },
                new Transport { Uninstall = true, Log = "Magisk Installer\n- Done\nUpdater process ended with RC=0" },
                new Transport { Uninstall = true, CleanupFails = true, Log = uninstall.Log },
                new Transport { Uninstall = true, StaleLog = true, Log = uninstall.Log } })
            {
                try { await new AndroidBridge(broken).UninstallMagiskRecovery(target, file, _ => { }, TimeSpan.FromMilliseconds(50)); throw new Exception("Unsafe uninstall accepted"); }
                catch (BridgeException ex) when (ex.Key is "RootUninstallUnavailable" or "RootUninstallUnconfirmed" or "RootUninstallRecovery")
                { Check(!broken.Rebooted && !broken.AppRemoved, "Unconfirmed or incompatible cleanup never reboots or removes the APK"); }
            }
            var survived = new Transport { Uninstall = true, RootSurvives = true, Log = uninstall.Log };
            try { await new AndroidBridge(survived).UninstallMagiskRecovery(target, file, _ => { }, TimeSpan.FromMilliseconds(50)); throw new Exception("Remaining daemon accepted"); }
            catch (BridgeException ex) when (ex.Key == "RootUninstallUnconfirmed")
            { Check(!survived.AppRemoved, "Remaining root after reboot cannot report successful uninstall"); }
            return checks;
        }
        finally { Directory.Delete(folder, true); }
    }
}
