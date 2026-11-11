using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;

namespace SoloStone
{
    // Where this player's prefix is and which Wine runs it. Setup writes it; every other command reads it.
    [DataContract] internal sealed class LinuxConfig
    {
        [DataMember(Name="schema")] public int Schema=1;
        [DataMember(Name="prefix")] public string Prefix;
        [DataMember(Name="wine")] public string Wine;
        static string ConfigHome => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is string x && x.StartsWith("/") ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".config");
        internal static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is string x && x.StartsWith("/") ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".local","share");
        internal static string FilePath => Path.Combine(ConfigHome,"solostone","linux.json");
        internal static string DefaultPrefix => Path.Combine(DataHome,"SoloStone","prefix");
        internal static LinuxConfig Load() => File.Exists(FilePath) ? Json.Read<LinuxConfig>(FilePath) : null;
        internal void Save() { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)); Json.Save(FilePath,this); }
        // A command name is looked up on PATH now, so a later PATH change can't swap the Wine that runs the game.
        internal static string FindWine(string wine) {
            if(wine.Contains('/')) { string full=Path.GetFullPath(wine); if(File.Exists(full)) return full; }
            else if(OnPath(wine) is string found) return found;
            throw new InvalidOperationException("Wine was not found ("+wine+"). Install Wine, or pass --wine <path to the wine program>.");
        }
        internal static string OnPath(string name) {
            foreach(string dir in (Environment.GetEnvironmentVariable("PATH")??"").Split(':').Where(d=>d.StartsWith("/"))) {
                string candidate=Path.Combine(dir,name); if(File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }

    // The applications-menu entry, written by setup and removed by uninstall. It starts the game with no terminal (see Desktop).
    internal static class DesktopEntry
    {
        internal static string FilePath => Path.Combine(LinuxConfig.DataHome,"applications","solostone.desktop");
        // Desktop Entry Exec quoting: a quoted argument escapes " ` $ and \ with a backslash. The key file's string escapes apply first,
        // so each of those backslashes is written \\: $ is \\$, \ is \\\\ (GLib refuses an entry with a single one).
        internal static string Quote(string arg) {
            // The spec writes a literal % as %%, but GLib and KDE both look the program up before they undo it, and a bare % starts a
            // field code: no spelling of such a path starts. Refused, so setup says so instead of adding an entry that does nothing.
            if(arg.Contains('%')) throw new InvalidOperationException("The applications menu can't start a program whose path holds %: "+arg+". Move the SoloStone folder somewhere without %, then run: solostone desktop-entry");
            return "\""+string.Concat(arg.Select(c=>c=='\\'?@"\\\\":"\"`$".IndexOf(c)>=0?@"\\"+c:c.ToString()))+"\"";
        }
        internal static string Text(string exe,string icon) => "[Desktop Entry]\nType=Application\nName=SoloStone\nComment=Play Hearthstone offline against computer opponents\n"
            +"Exec="+Quote(exe)+" play --desktop\n"+(icon!=null?"Icon="+icon+"\n":"")+"Terminal=false\nCategories=Game;CardGame;\n";
        internal static void Write(string exe,string icon) {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath,Text(exe,icon!=null && File.Exists(icon)?icon:null));
        }
        internal static void Remove() { if(File.Exists(FilePath) && File.ReadAllText(FilePath).Contains("\nName=SoloStone\n")) File.Delete(FilePath); }
    }

    internal static class LinuxHost
    {
        internal static string[] Registered => new string[0]; // The prefix's registry lists no retail Hearthstone; the source is kept apart by Safety.Pair.
        // Where players usually have Hearthstone on Linux: Battle.net in a Wine, Lutris, Bottles or Steam (Proton) prefix.
        internal static string[] FindClients() {
            string home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var prefixes=new List<string>{Path.Combine(home,".wine")};
            foreach(string parent in new[]{Path.Combine(home,"Games"),Path.Combine(home,"games"),Path.Combine(home,".var/app/com.usebottles.bottles/data/bottles/bottles"),
                Path.Combine(home,".local/share/bottles/bottles"),Path.Combine(home,".local/share/Steam/steamapps/compatdata"),Path.Combine(home,".steam/steam/steamapps/compatdata")})
                if(Directory.Exists(parent)) foreach(string dir in Directory.GetDirectories(parent)) { prefixes.Add(dir); prefixes.Add(Path.Combine(dir,"pfx")); }
            var candidates=new List<string>();
            foreach(string prefix in prefixes)
                foreach(string programs in new[]{"Program Files (x86)","Program Files"}) candidates.Add(Path.Combine(prefix,"drive_c",programs,"Hearthstone"));
            foreach(string parent in new[]{Path.Combine(home,"Games"),Path.Combine(home,"games")}) candidates.Add(Path.Combine(parent,"Hearthstone"));
            return Discovery.Find(candidates);
        }
        // Battle.net in any prefix can change the source while it is copied, as on Windows. And the prefix's wineserver must not be running yet.
        internal static string[] IdleFailures(WinePrefix prefix) {
            var failures=new List<string>();
            var images=WineImages();
            if(images.Contains("battle.net.exe")) failures.Add(ClientPolicy.BattleRunning);
            else if(images.Contains("agent.exe")) failures.Add(ClientPolicy.AgentRunning);
            if(prefix.ServerRunning()) failures.Add(prefix.ServerRunningMessage);
            return failures.ToArray();
        }
        internal static void RequireIdle(WinePrefix prefix) { foreach(string failure in IdleFailures(prefix)) throw new InvalidOperationException(failure); }
        // The Windows program names of this account's Wine processes, lower case. Wine puts the image path in argv[0].
        static HashSet<string> WineImages() {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(string dir in Directory.GetDirectories("/proc")) {
                if(!Path.GetFileName(dir).All(char.IsDigit)) continue;
                try {
                    string first=File.ReadAllText(Path.Combine(dir,"cmdline")).Split('\0')[0];
                    int slash=Math.Max(first.LastIndexOf('\\'),first.LastIndexOf('/'));
                    string name=first.Substring(slash+1).ToLowerInvariant();
                    if(name.EndsWith(".exe",StringComparison.Ordinal)) names.Add(name);
                } catch(Exception e) when(e is IOException || e is UnauthorizedAccessException) { }
            }
            return names;
        }

        // No network from here on: the game runs in the namespace, and the plugin refuses to start if it can see any interface but loopback.
        // confirm: asks the player about a client warning (the menu entry's dialog); null leaves it to --accept-warning.
        internal static int Launch(WinePrefix prefix,InstallRecord record,string bundle,UpdateEngine engine,bool acceptWarning,Action<string> progress,Func<string,bool> confirm=null) {
            RequireIdle(prefix); Safety.Destinations(record,Registered);
            engine.RequireReadableJournal();
            string reports=Path.Combine(record.Root,"reports"); Safety.NoLinks(reports); Directory.CreateDirectory(reports);
            // Wine, Unity and DXVK write hundreds of lines to the console; the terminal keeps the launcher's own messages.
            string output=Path.Combine(reports,"wine.log"); Safety.NoLinks(output); File.WriteAllText(output,"");
            string previous=null,token=null,target=null;
            var used=new HashSet<string>();
            for(;;) {
                record=InstallRecord.Load(record.Root);
                var facts=ClientFacts.Read(record.Client);
                var verdict=ClientGate.Evaluate(facts,TestedClient.Compiled(),new ManifestStore(record.Root,UpdateConfig.Compiled()).Current(),new NoBindingManifest(),IdleFailures(prefix));
                var current=record;
                if(verdict.Kind==VerdictKind.Warning && !acceptWarning && confirm!=null) {
                    if(!confirm(verdict.Message)) { progress("Not started: the client warning was not confirmed."); return 0; }
                    acceptWarning=true;
                }
                string grant=LaunchGate.Admit(record.Root,verdict,facts,record.ReleaseVersion,acceptWarning,()=>engine.BackupNow(current));
                if(verdict.Kind==VerdictKind.Warning) progress(verdict.Message);
                Hashes.Verify(record.Client,record.Files);
                ClientIdentity.Verify(record.Client,record.IdentityFiles);
                if(record.Payload==null) throw new InvalidOperationException("Missing installed payload record.");
                record.Payload.Verify(record.Client);
                var shipped=Json.Read<Payload>(Path.Combine(bundle,"payload.json")); shipped.Verify(record.Client);
                RequireIdle(prefix);
                string run=Guid.NewGuid().ToString("D");
                foreach(string name in new[]{"plugin-loaded.txt","save-bound.txt","save-bound.txt.tmp"}) {
                    string path=Path.Combine(reports,name); Safety.NoLinks(path); if(File.Exists(path)) File.Delete(path);
                }
                LaunchGrant.Write(reports,run,grant,facts.Version);
                var arguments=new List<string>{prefix.ToWine(Path.Combine(record.Client,"Hearthstone.exe")),"-launch","-force-d3d11","-uid",ClientIdentity.GameUid,
                    "-hs-native-ui-run",run,"-hs-installer-root",prefix.ToWine(record.Root)};
                if(previous!=null) arguments.AddRange(new[]{"-hs-profile-previous-run",previous,"-hs-profile-relaunch-token",token});
                arguments.AddRange(new[]{"-logFile",prefix.ToWine(Path.Combine(reports,"Unity.log"))});
                prefix.InstallImguiFont(bundle);
                // DXVK writes its log where the game starts unless told otherwise: the reports folder, not the verified copy.
                var start=WinePrefix.OutputTo(prefix.Start(record.Client,true,prefix.InstallDxvk(bundle)?prefix.ToWine(reports):null,arguments.ToArray()),output);
                DateTime started=DateTime.UtcNow; progress("Starting SoloStone…");
                using(var client=Process.Start(start)) {
                    try {
                        bool timedOut;
                        // Wine reports the prefix's own user here, not this account, so the run id alone identifies the launch.
                        if(!WaitMarker(client,Path.Combine(reports,"plugin-loaded.txt"),lines=>lines.Length>=2 && lines[0]==run,engine.MarkerSeconds(),out timedOut,()=>ClientCheckFile.Holding(reports,run))) {
                            Stop(client,prefix);
                            LaunchGrant.Remove(reports);
                            var outcome=Health.Classify(false,timedOut,timedOut?(int?)null:client.ExitCode);
                            var health=engine.ReportHealth(outcome,timedOut?"The game did not confirm the mod in time.":ExitMessage(client.ExitCode,reports,run));
                            if(health.Restored) throw new UpdateRestoredException(health.Message);
                            throw new InvalidOperationException(client.ExitCode!=0 ? ExitMessage(client.ExitCode,reports,run) : "The offline safety plugin did not confirm this launch. SoloStone was stopped. Check the verified plugin payload.");
                        }
                        LaunchGrant.Remove(reports);
                        engine.ReportHealth(HealthOutcome.Confirmed,null);
                        engine.ClearNote();
                        bool unused;
                        if(previous!=null && !WaitMarker(client,Path.Combine(reports,"save-bound.txt"),
                            lines=>lines.Contains("schema=1") && lines.Contains("runId="+run) && lines.Contains("slotId="+target)
                                && lines.Any(line=>Safety.Same(line,"profilePath="+prefix.ToWine(target=="legacy" ? Path.Combine(prefix.SaveRoot,"profile.xml")
                                    : Path.Combine(prefix.SaveRoot,"profiles",target,"profile.xml")))),Health.FirstStartSeconds,out unused)) {
                            Stop(client,prefix);
                            throw new InvalidOperationException("The selected save was not confirmed. SoloStone was stopped; saves were preserved.");
                        }
                        progress("SoloStone is running. Quit from the game to close this launcher.");
                        client.WaitForExit();
                    } finally { prefix.WaitServer(); }
                    if(client.ExitCode==Health.ExitSaveSwitch) {
                        token=Relaunch.Read(Path.Combine(prefix.SaveRoot,"profile-switch.xml"),run,started,out target);
                        if(!used.Add(token)) throw new InvalidOperationException("A save-switch token was reused. Start SoloStone again.");
                        previous=run; continue;
                    }
                    if(client.ExitCode!=0) throw new InvalidOperationException(ExitMessage(client.ExitCode,reports,run));
                    return 0;
                }
            }
        }
        // The game and whatever Wine started with it: killing the wine process alone leaves the server and its services.
        static void Stop(Process client,WinePrefix prefix) {
            if(!client.HasExited) { client.Kill(); client.WaitForExit(); }
            prefix.KillServer();
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
        static string ExitMessage(int code,string reports,string run) {
            switch(code) {
                case Health.ExitRejected: return "The plugin rejected this installation. Start SoloStone again.";
                case Health.ExitCanary: return "The game found a network connection and stopped. Start the game only from SoloStone, which runs it without network.";
                case Health.ExitHooks: return Messages.ClientBlocked(ClientCheckFile.BlockDetail(reports,run));
                case Health.ExitFirewallRefused: return "The game found network access and stopped. "+NetworkIsolation.StartFromScript;
                case Health.ExitSetupFailed: return "The game could not prepare its files or saves and stopped. Check available space on this drive, then try again. Your saves were not changed.";
                case Health.ExitStartupError: return "The game stopped while the offline plugin was starting. The log is in the copy's BepInEx folder.";
                case Health.ExitRefused: return "The game refused this launch. Start it from SoloStone. If this repeats, uninstall SoloStone (keeping your saves) and set it up again.";
                default: return "The game stopped (exit "+code+"). Your installation and saves were kept.";
            }
        }
    }

    // Updates replace the launcher in place, which this launcher doesn't do yet: a Linux release is a new download. It opens no network.
    internal sealed class LinuxUpdateHost : IUpdateHost
    {
        readonly WinePrefix prefix;
        internal LinuxUpdateHost(WinePrefix prefix) { this.prefix=prefix; }
        public string SaveRoot => prefix.SaveRoot;
        public string IdleProblem() => LinuxHost.IdleFailures(prefix).FirstOrDefault();
        public Verdict CheckStaged(string stagedApp,string root) => throw new InvalidOperationException(NotYet);
        public bool RunningFrom(string folder) => false;
        public void HandOffSwap(string stagedApp,string version) => throw new InvalidOperationException(NotYet);
        public void HandOffUndo(string helperFolder) => throw new InvalidOperationException(NotYet);
        internal const string NotYet="Updates are not available in the Linux launcher yet. Download the new release instead.";
    }
}
