using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using SoloStone;

// Findings from the independent code review: each of these failed against the code before its fix.
static partial class Program
{
    static string PluginSource(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"plugin-source",name));
    // ---- item 2: first start before the marker, and a newer release over a pending one ----
    static void PendingHealthTests(string temp) {
        // Exit 0: the player closed it. Nothing happens and the check stays pending.
        var a=NewFixture(temp); Check(Update(a,"2.0.0").Outcome==UpdateOutcome.Swapped,"swap");
        string swapped=a.Code;
        var quiet=a.Engine().ReportHealth(Health.Classify(false,false,0),"closed");
        Check(!quiet.Restored && a.Code==swapped && a.Engine().Journal.Phase==SwapJournal.Swapped,"exit 0 before the marker leaves the check pending");
        // A crash-like exit: restore, a day's hold, then for good. Same bookkeeping as a timeout.
        var b=NewFixture(temp); string good=b.Code;
        Check(Update(b,"2.0.0").Outcome==UpdateOutcome.Swapped,"swap");
        Check(b.Engine().ReportHealth(Health.Classify(false,false,unchecked((int)0xC0000005)),"exit -1073741819").Restored && b.Code==good,"a crash-like exit restores");
        var held=b.Engine().Held();
        Check(held.Version=="2.0.0" && held.Retries==1 && held.RetryAfter!=null && held.BlocksAt(DateTime.UtcNow) && !held.BlocksAt(DateTime.UtcNow.AddHours(25)),"first crash holds for a day");
        Check(UpdateService.OfferProblem(new LatestRelease{Version="2.0.0"},"1.0.0",held)!=null,"not offered during the day");
        // The day passes. The same version installs and fails again: permanent, and a kill while the count moves does not lose it.
        var again=BuildRelease(b,"2.0.0"); var engine=b.Engine(); engine.Stage(ZipOf(b,"2.0.0"),again);
        var cut=b.Engine(label=>{ if(label=="cleanup:held") throw new SimulatedCrash(); });
        Throws<SimulatedCrash>(()=>cut.Apply(again,false),"kill just after the hold is cleared");
        Check(b.Engine().Held()==null,"the hold was cleared by the second install");
        Check(b.Engine().Resume()==ResumeOutcome.HealthPending,"resume finishes the swap");
        Check(b.Engine().ReportHealth(HealthOutcome.RetryLater,"exit 1").Restored,"second failure restores");
        var final=b.Engine().Held();
        Check(final.Version=="2.0.0" && final.Retries==2 && final.RetryAfter==null && final.BlocksAt(DateTime.UtcNow.AddYears(1)),"the second failure holds for good, even after a kill at the worst moment");
        // Real mod failure: for good, the first time.
        var c=NewFixture(temp); Check(Update(c,"2.0.0").Outcome==UpdateOutcome.Swapped,"swap");
        Check(c.Engine().ReportHealth(Health.Classify(false,false,42),"hooks").Restored && c.Engine().Held().RetryAfter==null,"exit 42 restores and holds for good");
        // A refusal counts for nothing: no restore, no hold.
        foreach(int code in new[]{Health.ExitFirewallRefused,Health.ExitRefused,Health.ExitCanary}) {
            var d=NewFixture(temp); Check(Update(d,"2.0.0").Outcome==UpdateOutcome.Swapped,"swap"); string kept=d.Code;
            Check(!d.Engine().ReportHealth(Health.Classify(false,false,code),"refused").Restored && d.Code==kept && d.Engine().Held()==null,"a refusal neither restores nor holds: "+code);
        }
        // A swap that failed in the helper opens the launcher, which must not offer it straight back and loop.
        var looping=NewFixture(temp); var loopKey=new TestKey(); var loopSign=new TestKey(); var loopHttp=new FakeHttp(); string loopJson=ManifestJson(1,"2.0.0");
        loopHttp.Add(RawBase+"release-manifest.json",Utf8(loopJson)); loopHttp.Add(RawBase+"release-manifest.json.minisig",SignManifest(loopSign,loopJson,1));
        var loopService=new UpdateService(looping.Root,UpdateConfig.From(loopKey.Public,loopSign.Public,"owner/repo",0),loopHttp,looping.Engine());
        Check(loopService.Check(CancellationToken.None).State==UpdateState.Available,"offered at first");
        looping.Engine().RecordNote("2.0.0","The update could not be installed.");
        var loopStatus=new UpdateService(looping.Root,UpdateConfig.From(loopKey.Public,loopSign.Public,"owner/repo",0),loopHttp,looping.Engine(),true).Check(CancellationToken.None);
        Check(loopStatus.State==UpdateState.Failed && loopStatus.Message.Contains("could not be installed"),"a failed helper is not offered straight back");
        Check(loopService.Check(CancellationToken.None).State==UpdateState.Available,"a start by the player offers it again");
        PendingOverNewer(temp);
    }
    // ---- items 4, 5, 6: paths ----
    static void PathTests(string temp,string source,string bundle,TestedClient tested,string saves) {
        // Item 4: lab guard in hook builds, normalised. This harness compiles with the hooks.
        foreach(string path in new[]{@"D:\Battle"+@"Net\Hearthstone",@"d:\battle"+@"net\hearthstone",@"D:\Battle"+@"Net\Hearthstone\x",@"D:/Battle"+@"Net/Hearthstone",@"D:\x\..\Battle"+@"Net\Hearthstone",@"D:\Battle"+@"Net\.\Hearthstone\",@"C:\Us"+@"ers\HSOffline"+@"Probe",@"C:\Users\"+@"x\..\HSOffline"+@"Probe\AppData\Local\HearthstoneOffline"})
            RejectsBecause(()=>Safety.Canonical(path),"protected lab","hook build refuses "+path);
        Func<string,string> realLong=PhysicalPaths.LongName;
        try {
            PhysicalPaths.LongName=p=>Regex.Replace(p,@"BATTLE~1\\HEARTH~1","Battle"+"Net\\Hearthstone",RegexOptions.IgnoreCase);
            RejectsBecause(()=>Safety.Canonical(@"D:\BATTLE~1\HEARTH~1\x"),"protected lab","hook build refuses an 8.3 spelling");
        } finally { PhysicalPaths.LongName=realLong; }
        Check(Safety.Canonical(Path.Combine(temp,"BattleNetHearthstoneElsewhere")).Length>0 && Safety.Within(@"D:\Battle"+@"Net\Hearthstone\x",@"D:\Battle"+@"Net\Hearthstone") && !Safety.Within(@"D:\Battle"+@"Net\Hearthstone2",@"D:\Battle"+@"Net\Hearthstone"),"the guard stops at folder boundaries");
        // Source, target and discovery result, through the real paths.
        Refuses(()=>Installer.Setup(SetupOf(@"D:\Battle"+@"Net\Hearthstone",Path.Combine(temp,"lab-root"),bundle,tested,saves)),"hook build refuses it as a source");
        Check(TargetCheck.Problem(Facts(@"D:\x\..\Battle"+@"Net\Hearthstone",Path.Combine(temp,"lab-root"),source))!=null,"hook build refuses it as a target");
        RejectsBecause(()=>{ string r=ReleaseScan.PayloadProblem(temp,@"D:\Battle"+@"Net\Hearthstone",true); if(r!=null) throw new InvalidOperationException(r); },"protected lab","hook build refuses it as a client reference");
        Check(Discovery.Find(new[]{@"D:\Battle"+@"Net\Hearthstone",@"D:\x\..\Battle"+@"Net\Hearthstone"}).Length==0,"hook build drops it from discovery");
        Check(!Directory.Exists(Path.Combine(temp,"lab-root")),"those refusals wrote nothing");
        foreach(string file in new[]{"Core.cs","RuntimeProtection.cs","Install.cs","ClientPolicy.cs","CoreLauncher.cs","UpdateEngine.cs","WindowsHost.cs","Program.cs","UpdateClient.cs","LaunchGate.cs","Fs.cs"}) {
            string released=StripHooks(Source(file));
            Check(!released.Contains(@"Battle"+@"Net\Hearthstone") && !released.Contains(@"Battle"+@"Net\\Hearthstone") && !released.Contains("HSOffline"+"Probe"),"no lab path outside a hook region: "+file);
        }
        // Item 5: long names everywhere. A short spelling in, the long one out, and it matches the image path.
        try {
            string shortDir=Path.Combine(temp,"SOLOST~1"), longDir=Path.Combine(temp,"SoloStone Long Name");
            PhysicalPaths.LongName=p=>p.Replace("SOLOST~1","SoloStone Long Name");
            Check(Safety.Canonical(Path.Combine(shortDir,"client"))==Path.Combine(longDir,"client"),"Canonical returns the long name");
            Check(PhysicalPaths.Long(Path.Combine(shortDir,"x"))==Path.Combine(longDir,"x") && PhysicalPaths.Long(Path.Combine(temp,"plain"))==Path.Combine(temp,"plain"),"only a short name is looked up");
            string problemless=Safety.ClientPathProblem(Path.Combine(shortDir,"client"),Path.Combine(temp,"r"),null,null);
            Check(problemless==null,"a short target passes the path check as its long self");
            // Setup stores the long name, so the rule's program path matches what Windows reports for the running game.
            var facts=TargetCheck.Gather(Path.Combine(shortDir,"client"),Path.Combine(temp,"r"),source,new string[0],saves);
            Check(TargetCheck.Problem(facts)==null,"the check passes");
        } finally { PhysicalPaths.LongName=realLong; }
        Check(Source("Core.cs").Contains("GetLongPathNameW"),"Windows long names through the system call");
        string record=Path.Combine(temp,"fw2-root"), client=Path.Combine(record,"client"); Directory.CreateDirectory(client); string exe=Path.Combine(client,"Hearthstone.exe"); File.WriteAllText(exe,"x");
        var rec=SampleRecord(record,client);
        var snapshot=new FirewallSnapshot { Rules=new List<string>{"v2.33|Action=Block|Active=TRUE|Dir=Out|App="+exe+"|Name="+FirewallPlan.Name(rec,exe)+"|"} };
        FirewallRegistry.Check(rec,new[]{exe},snapshot); Check(true,"rules pass while the service runs");
        snapshot.ServiceStopped=true;
        RejectsBecause(()=>FirewallRegistry.Check(rec,new[]{exe},snapshot),"service is stopped","a stopped firewall service refuses");
        Check(Source("RuntimeProtection.cs").Contains("mpssvc"),"the registry read asks the service too");
        // Item 6: an unreachable registered folder only matters on its own drive letter.
        Func<string,string> unplugged=p=>p==@"C:\SoloStone"||p==@"C:\x"?p:throw new InvalidOperationException("A folder's physical location could not be verified");
        Safety.DestinationLocations(@"C:\SoloStone",new[]{@"E:\Games\Hearthstone"},unplugged); Check(true,"an unplugged drive does not stop a folder on another drive");
        Safety.DestinationLocations(@"C:\SoloStone",new[]{@"\\nas\games\Hearthstone"},unplugged); Check(true,"an unreachable network entry has no drive letter to overlap");
        Safety.DestinationLocations(@"C:\SoloStone",new[]{"E:\\bad\0name"},unplugged); Check(true,"a garbage entry on another drive does not stop it");
        RejectsBecause(()=>Safety.DestinationLocations(@"E:\SoloStone",new[]{@"E:\Games\Hearthstone"},p=>p.Contains("Games")?throw new InvalidOperationException("no"):p),"can't be reached","same drive letter refuses");
        RejectsBecause(()=>Safety.DestinationLocations(@"e:\SoloStone",new[]{@"E:\Games\Hearthstone"},p=>p.Contains("Games")?throw new InvalidOperationException("no"):p),"Plug that drive back in","the refusal gives a way forward, whatever the case");
        RejectsBecause(()=>Safety.DestinationLocations(Path.Combine(temp,"reg-a","x"),new[]{Path.Combine(temp,"reg-a")},p=>p),"registered game folder","a spelled overlap is still a retail refusal");
        Check(!Regex.IsMatch(Source("RuntimeProtection.cs").Split(new[]{"public static partial class FirewallProtection"},StringSplitOptions.None)[0],@"\bcatch\b"),"an unreadable registry stays fail-closed: RegisteredFolders swallows nothing");
    }
    // ---- item 3: refusals ----
    static void RefusalTests(string temp,string source,string bundle,TestedClient tested,string saves) {
#if SOLOSTONE_INSTALLER
        Check(InstallerRuntime.ExitFirewallRefused==Health.ExitFirewallRefused && InstallerRuntime.ExitRefused==Health.ExitRefused,"plugin and launcher agree on the codes");
        string root=Path.Combine(temp,"refusal-root");
        var installed=Installer.Setup(SetupOf(source,root,bundle,tested,saves));
        string exe=Path.Combine(installed.Client,"Hearthstone.exe"), data=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), run=Guid.NewGuid().ToString("D"), reports=Path.Combine(root,"reports");
        string[] args={"-hs-installer-root",root,"-hs-native-ui-run",run};
        Directory.CreateDirectory(reports); LaunchGrant.Write(reports,run,"allow",installed.ClientVersion);
        Func<Action<InstallRecord>,int> code=firewall=>{ try { InstallerRuntime.Resolve(exe,data,args,false,false,()=>new string[0],firewall,()=>"37.0"); return 0; } catch(InstallerRefusal e) { return e.ExitCode; } };
        Check(code(_=>{})==0,"admitted");
        Check(code(_=>throw new InvalidOperationException("SoloStone firewall protection is incomplete."))==47,"an unverifiable firewall has its own exit code");
        LaunchGrant.Remove(reports);
        Check(code(_=>{})==48,"a missing grant is a refusal, not a hook failure");
        LaunchGrant.Write(reports,run,"allow",installed.ClientVersion);
        File.AppendAllText(exe,"changed");
        Check(code(_=>{})==48,"a changed client file is a refusal, not a hook failure");
        Installer.Uninstall(root,new string[0]);
        string probe=PluginSource("OfflineProbe.cs");
        Check(Regex.IsMatch(probe,@"#if SOLOSTONE_INSTALLER\s+if \(error is InstallerRefusal refusal\).*?Application\.Quit\(refusal\.ExitCode\);.*?#endif\s+Logger\.LogFatal\(""Offline probe initialization failed",RegexOptions.Singleline),"the plugin exits with the refusal's code, behind the symbol");
        // The window: a firewall refusal offers Fix firewall.
        var model=new LauncherModel();
        string host=Source("WindowsHost.cs");
        Check(host.Contains("case Health.ExitFirewallRefused: return FirewallRefused(record);") && host.Contains("return Messages.FirewallRefused("),"exit 47 is worded by Messages.FirewallRefused");
        model.LaunchEnded(1,Messages.FirewallRefused(null));
        Check(model.FirewallProblem,"a firewall refusal leads to the Fix firewall button");
        model=new LauncherModel(); model.LaunchEnded(1,Regex.Match(host,@"case 42: return ""([^""]+)""").Groups[1].Value);
        Check(!model.FirewallProblem,"a hook failure does not");
#endif
    }
    // ---- item 7: TLS ----
    static void TlsTests(string temp) {
        var asked=new List<SecurityProtocolType>();
        Tls.Allow(asked.Add);
        Check(asked.SequenceEqual(new[]{SecurityProtocolType.SystemDefault}),"Windows picks the protocol");
        asked.Clear();
        Tls.Allow(value=>{ asked.Add(value); if(value==SecurityProtocolType.SystemDefault) throw new NotSupportedException(); });
        Check(asked.Last()==SecurityProtocolType.Tls12,"no throw on a platform without the system default");
        Check(!Source("UpdateClient.cs").Contains("SecurityProtocolType.Tls13"),"TLS 1.3 is never asked for by name");
        var f=NewFixture(temp); var rootKey=new TestKey(); var signing=new TestKey(); var http=new FakeHttp();
        http.Routes[RawBase+"release-manifest.json"]=()=>throw new WebException("The request was aborted",WebExceptionStatus.SecureChannelFailure);
        var service=new UpdateService(f.Root,UpdateConfig.From(rootKey.Public,signing.Public,"owner/repo",0),http,f.Engine());
        var status=service.Check(CancellationToken.None);
        Check(status.State==UpdateState.Failed && status.Message=="Couldn't check for updates." && File.ReadAllText(Texts.LogFile(f.Root)).Contains("secure connection"),"a TLS failure is a visible, readable update failure: "+status.Message);
        Check(Tls.Describe(new WebException("x",WebExceptionStatus.TrustFailure)).Contains("secure connection") && Tls.Describe(new WebException("x",WebExceptionStatus.ConnectFailure)).Contains("not reachable") && Tls.Describe(new InvalidOperationException("words"))=="words","every failure reads plainly");
    }
    // ---- items 8, 9: what a release may carry ----
    static string FindOut(params string[] parts) {
        for(var dir=new DirectoryInfo(AppContext.BaseDirectory);dir!=null;dir=dir.Parent) {
            string candidate=Path.Combine(new[]{dir.FullName}.Concat(parts).ToArray());
            if(File.Exists(candidate)) return candidate;
        }
        return null;
    }
    static void ReleaseTests(string temp,string source,string bundle) {
        Func<string,byte[]> ascii=System.Text.Encoding.ASCII.GetBytes; Func<string,byte[]> wide=System.Text.Encoding.Unicode.GetBytes;
        Check(ReleaseScan.Found(wide(@"x D:\Battle"+@"Net\Hearthstone y"),ReleaseScan.LauncherTokens()).Length==1 && ReleaseScan.Found(ascii("HSOffline"+"Probe"),ReleaseScan.PluginTokens()).Length==1,"lab paths are found, either encoding");
        Check(ReleaseScan.Found(wide("enable-test-control"),ReleaseScan.PluginTokens()).Length==1 && ReleaseScan.Found(ascii("TcpListener"),ReleaseScan.PluginTokens()).Length==1,"the listener and flag file are found");
        foreach(string flag in new[]{"enable-playtest","benchmark-ranks","playtest-opponent","guide-cards-fail.txt","test-loaner-group","screenshot-request.txt"}) Check(ReleaseScan.Found(wide(flag),ReleaseScan.PluginTokens()).Length==1,"the scan knows the flag file "+flag);
        Check(ReleaseScan.Found(ascii("nothing here"),ReleaseScan.LauncherTokens()).Length==0,"clean bytes pass");
        Check(!Source("UpdateEngine.cs").Contains("Battle"+"Net\\\\Hearthstone"),"the scanner's own words are split, so a release does not trip it");
        // The real release binaries, when built in-tree. Build them first; the gate commands do.
        string launcher=FindOut("out","app","SoloStone.exe"), plugin=FindOut("out","plugin-release","bin","OfflineProbe","debug","SoloStone.Plugin.dll");
        if(launcher==null) Skip("release launcher scan: installer/out/app is not built");
        else Check(ReleaseScan.Found(File.ReadAllBytes(launcher),ReleaseScan.LauncherTokens()).Length==0,"the built release launcher has no lab path or hook: "+string.Join(",",ReleaseScan.Found(File.ReadAllBytes(launcher),ReleaseScan.LauncherTokens())));
        if(plugin==null) Skip("release plugin scan: installer/out/plugin-release is not built");
        else Check(ReleaseScan.Found(File.ReadAllBytes(plugin),ReleaseScan.PluginTokens()).Length==0,"the built Installer release plugin has no listener, flag file or lab path: "+string.Join(",",ReleaseScan.Found(File.ReadAllBytes(plugin),ReleaseScan.PluginTokens())));
        string test=PluginSource("NativeTestControl.cs"), protocol=PluginSource("TestControlProtocol.cs");
        Check(Regex.IsMatch(test,@"#if !SOLOSTONE_INSTALLER \|\| SOLOSTONE_TEST_HOOKS\s+private string FlagPath") && Regex.IsMatch(protocol,@"#if !SOLOSTONE_INSTALLER \|\| SOLOSTONE_TEST_HOOKS\s+// Socket worker"),"listener and flag file are behind the symbol");
        Check(Regex.IsMatch(Source("OfflineProbe.csproj"),@"'\$\(Installer\)' == 'true' and '\$\(TestHooks\)' == 'true'"),"only an Installer hook build keeps test control");
        // Item 9: every payload file against the client, and the reference is required.
        string app=Path.Combine(temp,"scan-app"); CopyFixture(bundle,app); WriteRel(app,"SoloStone.exe","clean launcher");
        string reference=Path.Combine(temp,"scan-client"); FakeClient(reference,"37.0.0");
        WriteRel(reference,"BepInEx/core/BepInEx.dll","fake BepInEx/core/BepInEx.dll"); // Loader files in the reference folder are not the client's.
        WriteRel(reference,"winhttp.dll","fake winhttp.dll");
        Check(ReleaseScan.PayloadProblem(app,reference,false)==null,"a clean payload passes: "+ReleaseScan.PayloadProblem(app,reference,false));
        Check(ReleaseScan.PayloadProblem(app,null,false).Contains("--client-reference") && ReleaseScan.PayloadProblem(app,Path.Combine(temp,"absent"),false).Contains("--client-reference"),"no reference, no pass");
        Check(ReleaseScan.PayloadProblem(app,temp,false).Contains("not a Hearthstone client"),"the reference must be a client");
        foreach(string name in new[]{"BepInEx/core/Copied.dll","BepInEx/unstripped_corlib/mscorlib.dll","winhttp2.dll"}) {
            string one=Path.Combine(temp,"scan-"+Math.Abs(name.GetHashCode())); CopyFixture(app,one);
            string target=Safety.Child(Path.Combine(one,"payload"),name);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(Path.Combine(reference,"Hearthstone_Data","Managed","Assembly-CSharp.dll"),target,true);
            Check(ReleaseScan.PayloadProblem(one,reference,false).Contains("identical to the client's"),"a client file in the payload is caught wherever it sits: "+name);
        }
        // Hook scan on any path. A release must have none, a hook build must have them.
        string hooked=Path.Combine(temp,"scan-hooked"); CopyFixture(app,hooked); File.WriteAllText(Path.Combine(hooked,"SoloStone.exe"),"SOLOSTONE_TEST_CONTROL SOLOSTONE_TEST_DISCOVERY SOLOSTONE_TEST_UPDATE_BASE");
        Check(ReleaseScan.PayloadProblem(hooked,reference,false).Contains("test hooks"),"a release launcher with hooks is refused");
        Check(ReleaseScan.PayloadProblem(hooked,reference,true)==null,"a hook build passes with --hook-build");
        Check(ReleaseScan.PayloadProblem(app,reference,true).Contains("should carry"),"a hook build without hooks is refused");
        string listening=Path.Combine(temp,"scan-listening"); CopyFixture(app,listening); File.WriteAllText(Path.Combine(listening,"payload","BepInEx","plugins","SoloStone.Plugin.dll"),"TcpListener");
        Check(ReleaseScan.PayloadProblem(listening,reference,false).Contains("release plugin"),"a release plugin with the listener is refused");
        string build=Source("build.ps1");
        Check(Regex.IsMatch(build,@"\[Parameter\(Mandatory=\$true\)\]\[string\]\$ClientReference") && build.Contains("'--client-reference'") && build.Contains("[switch]$HookBuild") && build.Contains("'--hook-build'"),"build.ps1 requires the reference and passes it on every path");
        Check(build.IndexOf("$checkArgs",StringComparison.Ordinal)>build.IndexOf("if ($Staged)",StringComparison.Ordinal) && !build.Substring(build.IndexOf("if ($Staged)",StringComparison.Ordinal),build.IndexOf("$checkArgs",StringComparison.Ordinal)-build.IndexOf("if ($Staged)",StringComparison.Ordinal)).Contains("exit"),"the check runs after both build paths");
        Check(build.Contains("can't be caught"),"build.ps1 says what a hash compare can't do");
        // LOW: the window says what uninstall deletes.
        Check(Texts.UninstallAskFor(@"E:\Copy").Contains(@"E:\Copy") && Texts.UninstallAskFor(@"E:\Copy").Contains("anything you added"),"uninstall warns that the copy folder goes, with what you added");
        Check(Source("Gui/LauncherForm.cs").Contains("Texts.UninstallAskFor(model.Report?.ClientPath)"),"the window shows that warning");
    }
    static void Round2Tests(string temp,string source,string bundle,TestedClient tested,string saves) {
        PathTests(temp,source,bundle,tested,saves);
        RefusalTests(temp,source,bundle,tested,saves);
        TlsTests(temp);
        ReleaseTests(temp,source,bundle);
        Round3Tests(temp,source,bundle,tested,saves);
    }
}
