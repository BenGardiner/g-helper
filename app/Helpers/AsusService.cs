using GHelper.Ally;
using System.Diagnostics;
using System.Management;

namespace GHelper.Helpers
{
    public static class AsusService
    {

        static List<string> services = new() {
                "ArmouryCrateControlInterface",
                "ArmouryCrateProArtService",
                "AsHidService",
                "ASUSOptimization",
                "AsusAppService",
                "ASUSLinkNear",
                "ASUSLinkRemote",
                "ASUSSoftwareManager",
                "ASUSLiveUpdateAgent",
                "ASUSSwitch",
                "ASUSSystemAnalysis",
                "ASUSSystemDiagnosis",
                "ASUSXGMobileService",
                "AsusCertService"
        };

        //"AsusPTPService",

        static List<string> servicesAC = new() {
                "ArmouryCrateSEService",
                "ArmouryCrateService",
                "LightingService",
        };

        // Phase-2 fork addition: the exact purge set from the ASUS-Free Setup guide
        // (files/ASUS_FREE_SETUP.md §1/§4) — the six SCI v3 services, the Armoury Crate
        // Control Interface service, the three Armoury Crate core services, and the
        // GameSDK service from the Armoury Crate Full Package's SDK set.
        // Nine of these key names are already used in the lists above. "ROGLiveService"
        // (display name "ROG Live Service") is resolved from ASUS's own installer
        // manifest component name (AppSetup.ini: APP-ROGLiveService); its binary sits in
        // the Full Package's encrypted head and was not recovered in phase 2, so that
        // one name is manifest-sourced rather than binary-verified. "GameSDK Service"
        // is verified from the extracted GameSDK.msi in the Full Package's SDK set:
        // its CustomAction table registers the service with GameSDK.exe -install and
        // starts/stops it via sc start/stop "GameSDK Service", so the key name really
        // does contain a space.
        // ATKWMIACPIIO (AsusWmiAcpi.sys) is deliberately NOT here: it is a kernel
        // driver, not a user-mode service, and G-Helper depends on it.
        static List<string> purgeServices = new() {
                "ASUSOptimization",
                "ASUSSoftwareManager",
                "ASUSSystemDiagnosis",
                "ASUSSystemAnalysis",
                "AsusAppService",
                "ASUSSwitch",
                "ArmouryCrateControlInterface",
                "ArmouryCrateService",
                "ROGLiveService",
                "LightingService",
                "GameSDK Service",
        };

        public static IReadOnlyList<string> PurgeServices => purgeServices;

        private static bool IsRunning(string name)
        {
            var procs = Process.GetProcessesByName(name);
            try { return procs.Length > 0; }
            finally { foreach (var p in procs) p.Dispose(); }
        }

        public static bool IsAsusOptimizationRunning() => IsRunning("AsusOptimization");

        public static bool IsArmouryRunning()
        {
            var acService = IsRunning("ArmouryCrate.Service");
            var lightingService = IsRunning("LightingService");
            Logger.WriteLine($"AC Service: {acService}, Lighting Service: {lightingService}");
            return acService || lightingService;
        }

        public static void RunArmouryUninstaller()
        {
            Process.Start(new ProcessStartInfo("https://dlcdnets.asus.com/pub/ASUS/mb/14Utilities/Armoury_Crate_Uninstall_Tool.zip") { UseShellExecute = true });
        }

        public static bool IsOSDRunning() => IsRunning("AsusOSD");


        private static Dictionary<string, string> GetServiceStates()
        {
            var names = AppConfig.IsStopAC() ? services.Concat(servicesAC) : services;
            var states = new Dictionary<string, string>();
            try
            {
                string filter = string.Join(" OR ", names.Select(name => $"Name='{name}'"));
                using var searcher = new ManagementObjectSearcher($"SELECT Name, State FROM Win32_Service WHERE {filter}");
                foreach (ManagementObject mo in searcher.Get())
                    states[(string)mo["Name"]] = (string)mo["State"];
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.Message);
            }
            return states;
        }

        private static List<string> GetRunningServices()
        {
            return GetServiceStates().Where(s => s.Value != "Stopped").Select(s => s.Key).ToList();
        }

        public static int GetRunningCount()
        {
            return GetRunningServices().Count;
        }


        public static void StopAsusServices()
        {
            foreach (string service in GetRunningServices())
            {
                ProcessHelper.StopDisableService(service, servicesAC.Contains(service) ? "Manual" : "Disabled");
            }

            if (GetRunningCount() == 0) AppConfig.Set("services_disabled", 1);

            if (AppConfig.IsAlly()) AllyControl.ApplyMode((ControllerMode)AppConfig.Get("controller_mode", (int)ControllerMode.Auto), true);
        }

        public static void StartAsusServices()
        {
            AppConfig.Set("services_disabled", 0);
            foreach (string service in GetServiceStates().Keys)
            {
                ProcessHelper.StartEnableService(service);
            }
        }

        // Phase-2 fork addition: states of the purge-list services that are actually
        // installed (name -> State / StartMode), same WMI pattern as GetServiceStates.
        public static Dictionary<string, (string State, string StartMode)> GetPurgeServiceStates()
        {
            var states = new Dictionary<string, (string State, string StartMode)>();
            try
            {
                string filter = string.Join(" OR ", purgeServices.Select(name => $"Name='{name}'"));
                using var searcher = new ManagementObjectSearcher($"SELECT Name, State, StartMode FROM Win32_Service WHERE {filter}");
                foreach (ManagementObject mo in searcher.Get())
                    states[(string)mo["Name"]] = ((string)mo["State"], (string)mo["StartMode"]);
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.Message);
            }
            return states;
        }

        // Phase-2 fork addition: stop + disable every installed purge-list service
        // (stop+disable only, never delete — matches the guide's reversible purge),
        // then verify the outcome per service instead of trusting the stop command:
        // a service counts as purged only when WMI reports it Stopped and Disabled.
        // Returns one (name, purged) result per installed purge-list service.
        public static List<(string Name, bool Purged)> PurgeAsusServices()
        {
            var before = GetPurgeServiceStates();

            foreach (string service in purgeServices)
            {
                if (!before.TryGetValue(service, out var state)) continue; // not installed
                if (state.State == "Stopped" && state.StartMode == "Disabled") continue; // already purged
                ProcessHelper.StopDisableService(service, "Disabled");
            }

            var after = GetPurgeServiceStates();
            var results = new List<(string Name, bool Purged)>();
            foreach (string service in purgeServices)
            {
                if (!before.ContainsKey(service)) continue;
                bool purged = after.TryGetValue(service, out var state) && state.State == "Stopped" && state.StartMode == "Disabled";
                Logger.WriteLine($"Purge {service}: {(purged ? "stopped and disabled" : "FAILED — now " + (after.TryGetValue(service, out var now) ? now.State + " / " + now.StartMode : "unknown"))}");
                results.Add((service, purged));
            }
            return results;
        }

        public static void StopOnStartup()
        {
            if (AppConfig.Is("services_skip")) return;
            if (!AppConfig.Is("services_disabled") || !ProcessHelper.IsUserAdministrator()) return;
            if (GetRunningCount() == 0) return;

            Logger.WriteLine("ASUS services revived, re-stopping on startup");
            Task.Run(() =>
            {
                StopAsusServices();
                Program.inputDispatcher?.Init();
            });
        }

    }

}