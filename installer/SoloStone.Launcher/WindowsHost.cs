using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace SoloStone
{
    internal static class WindowsHost
    {
        internal static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        internal static string SaveRoot => PublicNames.SaveRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        internal static string[] RegisteredFolders() => WindowsInstallPaths.RegisteredFolders();
        internal static string[] FindClients(bool wizard=false) {
#if SOLOSTONE_TEST_HOOKS
            // A live-test build never looks at the machine's own Hearthstone: the list comes from the test, before any registry or folder read.
            return TestDiscovery.Paths();
#else
            return wizard ? HearthstoneDiscovery.HearthstoneUninstallFolders() : FindInstalledClients();
#endif
        }
        static string[] FindInstalledClients() {
            var paths=new List<string>(RegisteredFolders());
            foreach(string folder in new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)})
                if(!string.IsNullOrEmpty(folder)) paths.Add(Path.Combine(folder,"Hearthstone"));
            return Discovery.Find(paths);
        }
        internal static string[] IdleFailures(string root=null) {
            var failures=new List<string>();
            // The updater outlives Battle.net by a minute or two; while Battle.net itself runs, "quit Battle.net" is the one thing to say.
            if(Process.GetProcessesByName("Battle.net").Length!=0) failures.Add(ClientPolicy.BattleRunning);
            else if(Process.GetProcessesByName("Agent").Length!=0) failures.Add(ClientPolicy.AgentRunning);
            string copy=null;
            try { if(root!=null) copy=Path.Combine(InstallRecord.Load(root).Client,"Hearthstone.exe"); }
            catch(Exception e) when(e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException) { }
            foreach(var process in Process.GetProcessesByName("Hearthstone")) {
                using(process) {
                    string path=null;
                    try { path=process.MainModule?.FileName; } catch(Exception e) when(e is Win32Exception || e is InvalidOperationException || e is NotSupportedException) { }
                    string failure=ClientPolicy.RunningGameFailure(path,copy);
                    if(!failures.Contains(failure)) failures.Add(failure);
                }
            }
            if(failures.Count!=0) return failures.ToArray();
            return new string[0];
        }
        internal static void RequireIdle(string root=null) { foreach(string failure in IdleFailures(root)) throw new InvalidOperationException(failure); }
        internal static string[] Executables(InstallRecord record) => Safety.Tree(record.Client)
            .Where(p=>p.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray();
        static dynamic Policy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2",true));
        internal static bool HasRules(InstallRecord record) {
            dynamic policy=Policy();
            try {
                foreach(dynamic rule in policy.Rules)
                    if(((string)rule.Name).StartsWith("SoloStone-"+record.InstallId+"-",StringComparison.Ordinal)) return true;
                return false;
            } finally { Marshal.FinalReleaseComObject(policy); }
        }
        static void CheckOwnedRules(InstallRecord record) {
            dynamic policy=Policy();
            try {
                foreach(dynamic rule in policy.Rules) {
                    string name=(string)rule.Name;
                    if(!name.StartsWith("SoloStone-"+record.InstallId+"-",StringComparison.Ordinal)) continue;
                    string program=Safety.Canonical((string)rule.ApplicationName);
                    string group=(string)rule.Grouping;
                    FirewallPlan.CheckOwnedRule(record,name,program,group);
                }
            } finally { Marshal.FinalReleaseComObject(policy); }
        }
        internal static void VerifyFirewall(InstallRecord record,string[] executables) => FirewallProtection.Verify(record,executables);
        internal static void Firewall(InstallRecord record,bool apply,Action<string> dryRun=null) {
            // All install/profile reads stay unelevated. Only a fixed System32 command line crosses UAC.
            record=FirewallPlan.VerifiedRecord(record.Root,RegisteredFolders(),apply);
            string[] executables=FirewallPlan.Programs(record);
            if(apply && !new HashSet<string>(executables,StringComparer.OrdinalIgnoreCase).SetEquals(Executables(record)))
                throw new InvalidOperationException("The copied executable list differs from the verified install record. "+InstallRecord.StartOver);
            CheckOwnedRules(record);
            var start=FirewallPlan.Elevation(record,apply,Environment.GetFolderPath(Environment.SpecialFolder.System));
            if(dryRun!=null) { dryRun(FirewallPlan.CommandLine(start)); return; }
            try {
#if SOLOSTONE_TEST_HOOKS
                // The lab cannot answer a UAC prompt. The same fixed command line goes to the test driver, which runs it elevated.
                if(TestElevation.Active) { if(TestElevation.Handoff(FirewallPlan.CommandLine(start))!=0) throw new InvalidOperationException("Firewall changes failed. The copy will not launch until protection is verified."); }
                else
#endif
                using(var child=Process.Start(start)) {
                    child.WaitForExit();
                    if(child.ExitCode!=0) throw new InvalidOperationException("Firewall changes failed. The copy will not launch until protection is verified.");
                }
            } catch(Win32Exception e) when(e.NativeErrorCode==1223) {
                throw new FirewallCancelledException(!apply);
            }
            if(apply) VerifyFirewall(record,executables);
            else if(HasRules(record)) throw new InvalidOperationException("Some SoloStone firewall rules remain. Uninstall was stopped.");
        }
        // No network from here on: every update request finishes before this method runs.
        internal static int Launch(InstallRecord record,string bundle,UpdateEngine engine,bool acceptWarning,Action<string> progress) {
            RequireIdle(record.Root); Safety.Destinations(record,RegisteredFolders());
            engine.RequireReadableJournal(); // Its health report comes right after the plugin confirms. Refuse here, not with a game running.
            return RunGame(record,bundle,engine,acceptWarning,progress);
        }
        static int RunGame(InstallRecord record,string bundle,UpdateEngine engine,bool acceptWarning,Action<string> progress) {
            string reports=Path.Combine(record.Root,"reports"); Safety.NoLinks(reports); Directory.CreateDirectory(reports);
            string previous=null,token=null,target=null;
            var used=new HashSet<string>();
            for(;;) {
                record=InstallRecord.Load(record.Root);
                var facts=ClientFacts.Read(record.Client);
                var verdict=ClientGate.Evaluate(facts,TestedClient.Compiled(),new ManifestStore(record.Root,UpdateConfig.Compiled()).Current(),new NoBindingManifest(),IdleFailures(record.Root));
                var current=record;
                string grant=LaunchGate.Admit(record.Root,verdict,facts,record.ReleaseVersion,acceptWarning,()=>engine.BackupNow(current));
                if(verdict.Kind==VerdictKind.Warning) progress(verdict.Message);
                Hashes.Verify(record.Client,record.Files);
                ClientIdentity.Verify(record.Client,record.IdentityFiles); // A copy without its own company name would write the key retail uses.
                if(record.Payload==null) throw new InvalidOperationException("Missing installed payload record.");
                record.Payload.Verify(record.Client);
                // The shipped manifest, not a user-edited install record, is authoritative.
                var shipped=Json.Read<Payload>(Path.Combine(bundle,"payload.json")); shipped.Verify(record.Client);
                VerifyFirewall(record,Executables(record));
                // The plugin's own check. Run here too, so a refusal is a sentence on this window and not exit 40 from the game.
                FirewallRegistry.Verify(record,Executables(record)); RequireIdle(record.Root);
                string run=Guid.NewGuid().ToString("D");
                foreach(string name in new[]{"plugin-loaded.txt","save-bound.txt","save-bound.txt.tmp"}) {
                    string path=Path.Combine(reports,name); Safety.NoLinks(path); if(File.Exists(path)) File.Delete(path);
                }
                LaunchGrant.Write(reports,run,grant,facts.Version);
                var start=new ProcessStartInfo(Path.Combine(record.Client,"Hearthstone.exe")) {
                    UseShellExecute=false, WorkingDirectory=record.Client,
                    Arguments="-launch -force-d3d11 -uid "+ClientIdentity.GameUid+" -hs-native-ui-run "+run
                        +" -hs-installer-root "+FirewallPlan.Quote(record.Root)
                        +(previous==null?"":" -hs-profile-previous-run "+previous+" -hs-profile-relaunch-token "+token)
                        +" -logFile "+FirewallPlan.Quote(Path.Combine(reports,"Unity.log"))
                };
                DateTime started=DateTime.UtcNow; progress("Starting SoloStone…");
                using(var client=Process.Start(start)) {
                    string identity=WindowsIdentity.GetCurrent().Name;
                    bool timedOut;
                    if(!WaitMarker(client,Path.Combine(reports,"plugin-loaded.txt"),lines=>lines.Length>=2 && lines[0]==run && lines[1]==identity,engine.MarkerSeconds(),out timedOut,()=>ClientCheckFile.Holding(reports,run))) {
                        if(!client.HasExited) { client.Kill(); client.WaitForExit(); }
                        LaunchGrant.Remove(reports);
                        var outcome=Health.Classify(false,timedOut,timedOut?(int?)null:client.ExitCode);
                        var health=engine.ReportHealth(outcome,timedOut?"The game did not confirm the mod in time.":ExitMessage(client.ExitCode,reports,run,record));
                        if(health.Restored) throw new UpdateRestoredException(health.Message);
                        throw new InvalidOperationException(client.ExitCode!=0 ? ExitMessage(client.ExitCode,reports,run,record) : "The offline safety plugin did not confirm this launch. SoloStone was stopped. Check the verified plugin payload.");
                    }
                    LaunchGrant.Remove(reports);
                    engine.ReportHealth(HealthOutcome.Confirmed,null);
                    engine.ClearNote();
                    bool unused;
                    if(previous!=null && !WaitMarker(client,Path.Combine(reports,"save-bound.txt"),
                        lines=>lines.Contains("schema=1") && lines.Contains("runId="+run) && lines.Contains("slotId="+target)
                            && lines.Any(line=>Safety.Same(line,"profilePath="+(target=="legacy" ? Path.Combine(SaveRoot,"profile.xml")
                                : Path.Combine(SaveRoot,"profiles",target,"profile.xml")))),Health.FirstStartSeconds,out unused)) {
                        if(!client.HasExited) { client.Kill(); client.WaitForExit(); }
                        throw new InvalidOperationException("The selected save was not confirmed. SoloStone was stopped; saves were preserved.");
                    }
                    progress("SoloStone is running. Quit from the game to close this launcher.");
                    client.WaitForExit();
                    if(client.ExitCode==46) {
                        string request=Path.Combine(SaveRoot,"profile-switch.xml");
                        token=Relaunch.Read(request,run,started,out target);
                        if(!used.Add(token)) throw new InvalidOperationException("A save-switch token was reused. Start SoloStone again.");
                        previous=run; continue;
                    }
                    if(client.ExitCode!=0) throw new InvalidOperationException(ExitMessage(client.ExitCode,reports,run,record));
                    return 0;
                }
            }
        }
        static bool WaitMarker(Process client,string path,Func<string[],bool> validate,int seconds,out bool timedOut,Func<bool> holding=null) {
            DateTime deadline=DateTime.UtcNow.AddSeconds(seconds), limit=DateTime.UtcNow.AddSeconds(Math.Max(seconds,Health.ChecksWaitSeconds));
            timedOut=false;
            while(DateTime.UtcNow<deadline || (holding!=null && DateTime.UtcNow<limit && holding())) {
                if(client.HasExited) return false;
                Safety.NoLinks(path);
                if(File.Exists(path)) {
                    if(new FileInfo(path).Length>8192) return false;
                    try { if(validate(File.ReadAllLines(path))) return true; } catch(IOException) {}
                }
                Thread.Sleep(200);
            }
            timedOut=true;
            return false;
        }
        static string ExitMessage(int code,string reports,string run,InstallRecord record) {
            switch(code) {
                case Health.ExitFirewallRefused: return FirewallRefused(record);
                case 40: return "The plugin rejected this installation. Open SoloStone again.";
                case 41: return "The game detected an available network connection and stopped. Reapply SoloStone firewall protection.";
                case 42: return Messages.ClientBlocked(ClientCheckFile.BlockDetail(reports,run));
                case Health.ExitSetupFailed: return "The game could not prepare its files or saves and stopped. Check available space on this drive, then try again. Your saves were not changed.";
                case Health.ExitStartupError: return "The game stopped while the offline plugin was starting. The log is in the client's BepInEx folder.";
                case Health.ExitRefused: return "The game refused this launch. Start it from the SoloStone launcher. If this repeats: "+InstallRecord.StartOver;
                default: return "The game stopped (exit "+code+"). Your installation and saves were kept.";
            }
        }
        // The game ran the registry check the launcher ran just before it started, and refused (exit 47). Run it again for the reason:
        // it names the adapter or rule at fault, as the plugin's log does.
        static string FirewallRefused(InstallRecord record) {
            try { FirewallRegistry.Verify(record,Executables(record)); return Messages.FirewallRefused(null); }
            catch(InvalidOperationException e) { return Messages.FirewallRefused(e.Message); }
        }
    }
    // Windows side of the update engine. Runs only the staged launcher's offline check; it opens no network itself.
    internal sealed class WindowsUpdateHost : IUpdateHost
    {
        public string SaveRoot => WindowsHost.SaveRoot;
        public string IdleProblem() => WindowsHost.IdleFailures(installRoot).FirstOrDefault();
        internal static bool HandedOff;
        internal static string OwnFolder => AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        public bool RunningFrom(string folder) => PhysicalPaths.SameFolder(OwnFolder,folder);
        readonly string installRoot;
        internal WindowsUpdateHost(string root) { installRoot=root; }
        void HandOff(string exe,string command,string version) {
            // It shows its own window and holds the instance, so a re-click while it runs does nothing but raise that window.
            var start=new ProcessStartInfo(exe,Options.HelperArguments(command,installRoot,version,Process.GetCurrentProcess().Id)) { UseShellExecute=false, WorkingDirectory=Path.GetDirectoryName(exe) };
            Process.Start(start).Dispose();
            HandedOff=true;
        }
        public void HandOffSwap(string stagedApp,string version) => HandOff(Path.Combine(stagedApp,"SoloStone.exe"),"finish-swap",version);
        public void HandOffUndo(string helperFolder) => HandOff(Path.Combine(helperFolder,"SoloStone.exe"),"finish-undo",null);
        public Verdict CheckStaged(string stagedApp,string root) {
            var start=new ProcessStartInfo(Path.Combine(stagedApp,"SoloStone.exe"),"check-client --root "+FirewallPlan.Quote(root)+" --json") {
                UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true, WorkingDirectory=stagedApp };
            using(var child=Process.Start(start)) {
                var output=child.StandardOutput.ReadToEndAsync(); var errors=child.StandardError.ReadToEndAsync();
                if(!child.WaitForExit(120000)) { child.Kill(); throw new IOException("The new version's client check did not finish."); }
                child.WaitForExit();
                // A launcher that starts and crashes is broken. One that ran but printed nothing readable says nothing, so retry.
                if(child.ExitCode!=0) return ClientPolicy.Blocked("The new version's launcher failed its client check (exit "+child.ExitCode+").");
                var verdict=Verdict.TryParse(output.Result);
                if(verdict==null) throw new IOException("The new version's client check printed no result.");
                return verdict;
            }
        }
    }
}
