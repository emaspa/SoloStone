using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace SoloStone
{
    public static class WindowsInstallPaths
    {
        public static string[] RegisteredFolders() {
            var paths=new List<string>();
            foreach(var hive in new[]{RegistryHive.LocalMachine,RegistryHive.CurrentUser})
                foreach(var view in new[]{RegistryView.Registry32,RegistryView.Registry64})
                    using(var baseKey=RegistryKey.OpenBaseKey(hive,view))
                    using(var key=baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")) {
                        if(key==null) continue;
                        foreach(string subkey in key.GetSubKeyNames())
                            using(var sub=key.OpenSubKey(subkey)) {
                                string publisher=sub?.GetValue("Publisher") as string;
                                string name=sub?.GetValue("DisplayName") as string;
                                if((publisher??"").IndexOf("Blizzard",StringComparison.OrdinalIgnoreCase)<0
                                    && (name??"").IndexOf("Hearthstone",StringComparison.OrdinalIgnoreCase)<0) continue;
                                string path=sub.GetValue("InstallLocation") as string;
                                if(!string.IsNullOrWhiteSpace(path)) paths.Add(path.Trim().Trim('"'));
                            }
                    }
            // Older Battle.net installs store the game location here instead of in Uninstall.
            foreach(var hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine})
                foreach(var view in new[]{RegistryView.Registry32,RegistryView.Registry64})
                    using(var key=RegistryKey.OpenBaseKey(hive,view))
                    using(var game=key.OpenSubKey(@"SOFTWARE\Blizzard Entertainment\Hearthstone")) {
                        string path=game?.GetValue("InstallPath") as string;
                        if(!string.IsNullOrWhiteSpace(path)) paths.Add(path);
                    }
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
    public static partial class FirewallProtection
    {
        public static string[] Executables(InstallRecord record) => Safety.Tree(record.Client)
            .Where(p=>p.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    // Linux runs the Windows client under Wine, which has no Windows Firewall. There the Linux launcher (installer/SoloStone.Linux) starts the whole
    // Wine session, wineserver included, in a network namespace whose only interface is loopback. Wine opens files in wineserver, so /proc/net/dev
    // read through Wine is wineserver's namespace: the one every connection of the game goes through (Wine creates sockets there too). A wineserver
    // started outside the namespace shows the computer's interfaces here, so it is refused. The plugin's network canary (OfflineProbe) runs after
    // this and, under Wine, also requires that a public address has no route at all. That part stays in the plugin: no shared file holds network code.
    public static class NetworkIsolation
    {
        public const string StartFromScript="On Linux, start the game only from the SoloStone launcher, which runs it without network access.";
        // Wine's own path to a Unix file first: it works without the Z: drive.
        static readonly string[] DevFiles={@"\\?\unix\proc\net\dev",@"Z:\proc\net\dev"};
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll",CharSet=CharSet.Ansi,BestFitMapping=false)] static extern IntPtr GetProcAddress(IntPtr module,string name);
        // Wine's ntdll exports wine_get_version; Windows' does not. Off Windows (the tests) kernel32 is missing and this is false.
        public static bool UnderWine { get {
            try { IntPtr ntdll=GetModuleHandleW("ntdll.dll"); return ntdll!=IntPtr.Zero && GetProcAddress(ntdll,"wine_get_version")!=IntPtr.Zero; }
            catch(Exception) { return false; }
        } }
        public static void Verify() {
            string text=null;
            foreach(string path in DevFiles) {
                try { text=File.ReadAllText(path); break; }
                catch(Exception e) when(e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException) { }
            }
            Check(text);
        }
        // Pure. netDev is the text of /proc/net/dev.
        public static void Check(string netDev) {
            string[] names=Interfaces(netDev);
            if(names==null) throw new InvalidOperationException("SoloStone could not read this computer's network interfaces. "+StartFromScript);
            string[] other=names.Where(name=>name!="lo").ToArray();
            if(other.Length!=0) throw new InvalidOperationException("SoloStone found network access ("+string.Join(", ",other)+"). "+StartFromScript);
        }
        // The interface names in /proc/net/dev, or null for anything that does not read as that file. Loopback is always there.
        public static string[] Interfaces(string netDev) {
            if(netDev==null) return null;
            string[] lines=netDev.Replace("\r","").Split('\n');
            if(lines.Length<3 || !lines[0].StartsWith("Inter-|",StringComparison.Ordinal) || !lines[1].StartsWith(" face |",StringComparison.Ordinal)) return null;
            var names=new List<string>();
            for(int i=2;i<lines.Length;i++) {
                string line=lines[i].Trim();
                if(line.Length==0) continue;
                int colon=line.IndexOf(':');
                if(colon<=0) return null;
                names.Add(line.Substring(0,colon).Trim());
            }
            return names.Contains("lo")?names.ToArray():null;
        }
    }
    // What Windows stores for one firewall rule: "v2.33|Action=Block|Active=TRUE|Dir=Out|App=...|Name=...|".
    public sealed class FirewallRuleText
    {
        public readonly Dictionary<string,List<string>> Fields=new Dictionary<string,List<string>>(StringComparer.OrdinalIgnoreCase);
        public static FirewallRuleText Parse(string text) {
            if(string.IsNullOrEmpty(text) || text[0]!='v') return null;
            var rule=new FirewallRuleText();
            string[] parts=text.Split('|');
            for(int i=1;i<parts.Length;i++) {
                if(parts[i].Length==0) continue;
                int equals=parts[i].IndexOf('=');
                if(equals<=0) return null;
                string key=parts[i].Substring(0,equals);
                List<string> values;
                if(!rule.Fields.TryGetValue(key,out values)) rule.Fields[key]=values=new List<string>();
                values.Add(parts[i].Substring(equals+1));
            }
            return rule;
        }
        public string One(string key) { List<string> values; return Fields.TryGetValue(key,out values) && values.Count==1?values[0]:null; }
    }
    public sealed class FirewallSnapshot
    {
        public List<string> Rules=new List<string>();
        public bool ProfileOff, PolicyOff, PolicyNoLocalRules, ServiceStopped;
        public List<ExcludedAdapter> Excluded=new List<ExcludedAdapter>();
    }
    // A network adapter Windows Firewall does not protect, and the profiles that leave it out (Domain, Private, Public).
    public sealed class ExcludedAdapter
    {
        public string Id, Name, Description;
        public bool Disabled;
        public readonly List<string> Profiles=new List<string>();
        // Windows stores the excluded adapters of a profile as GUIDs: one REG_SZ, comma-separated. COM gives an array of names, or of GUIDs for a disabled adapter.
        public static List<string> Ids(object value) {
            var ids=new List<string>();
            // Walked through IEnumerable, not Array: the plugin compiles this file too, and Unity's Mono is only checked
            // for the interface call (installer/mono-assumed.txt).
            var many=value as Array;
            foreach(object item in many!=null?(System.Collections.IEnumerable)many:new[]{value})
                // Anything but a string still counts, as itself: an unexpected shape must refuse, not pass.
                foreach(string part in (item as string??(item==null?"":item.ToString())).Split(','))
                    if(part.Trim().Length>0) ids.Add(part.Trim());
            return ids;
        }
        public static void Add(List<ExcludedAdapter> excluded,string profile,IEnumerable<string> ids,NetworkAdapters adapters) {
            foreach(string id in ids) {
                var adapter=adapters.Describe(id);
                var same=excluded.FirstOrDefault(other=>string.Equals(other.Id,adapter.Id,StringComparison.OrdinalIgnoreCase));
                if(same==null) excluded.Add(same=adapter);
                if(!same.Profiles.Contains(profile)) same.Profiles.Add(profile);
            }
        }
        string Label() {
            string about=Description!=null && Description!=Name?Description:null;
            if(Disabled) about=about==null?"disabled":about+", disabled";
            if(Name==null && about==null) return "an adapter Windows no longer lists ("+Id+")";
            return (Name!=null?"\""+Name+"\"":"ID "+Id)+(about!=null?" ("+about+")":"");
        }
        static string And(IList<string> items) => items.Count<2?string.Join("",items):string.Join(", ",items.Take(items.Count-1))+" and "+items[items.Count-1];
        static readonly string[] Order={"Domain","Private","Public"};
        // The refusal both checks give, the launcher's and the plugin's, so it names the adapter and says how to undo the exclusion.
        public static string Message(IList<ExcludedAdapter> excluded) {
            var lines=excluded.Select(adapter=>adapter.Label()+" in the "+And(Order.Where(adapter.Profiles.Contains).ToList())
                +(adapter.Profiles.Count==1?" profile":" profiles")).ToList();
            var profiles=Order.Where(profile=>excluded.Any(adapter=>adapter.Profiles.Contains(profile))).ToList();
            return "Windows Firewall is switched off for the network "+(excluded.Count==1?"adapter ":"adapters ")+And(lines)
                +". SoloStone's firewall rules do not cover "+(excluded.Count==1?"that adapter":"those adapters")+", so SoloStone cannot start. "
                +"To fix it, open Windows Defender Firewall with Advanced Security and click Windows Defender Firewall Properties. On the "
                +And(profiles.Select(profile=>profile+" Profile").ToList())+(profiles.Count==1?" tab":" tabs")
                +", click Customize next to Protected network connections and tick every adapter. Or run this in an administrator PowerShell: "
                +"Set-NetFirewallProfile -Name "+string.Join(",",profiles)+" -DisabledInterfaceAliases NotConfigured";
        }
    }
    // Names for what Windows Firewall stores about an excluded adapter. The registry keeps an adapter's name, description and disabled
    // flag whether it is enabled, disabled or hidden; COM and PowerShell show only a GUID or a number once it is disabled. Every read
    // is guarded: a name that can't be found leaves the GUID in the message, and never turns the refusal into a different one.
    public sealed class NetworkAdapters
    {
        const string NetClass="{4D36E972-E325-11CE-BFC1-08002BE10318}";
        const string Connections=@"SYSTEM\CurrentControlSet\Control\Network\"+NetClass, Drivers=@"SYSTEM\CurrentControlSet\Control\Class\"+NetClass, Devices=@"SYSTEM\CurrentControlSet\Enum\";
        readonly Func<string,string,object> value;
        readonly Func<string,string[]> subKeys;
        // Tests supply registry contents: value(path, name) and subKeys(path), paths under HKEY_LOCAL_MACHINE.
        public NetworkAdapters(Func<string,string,object> value,Func<string,string[]> subKeys) { this.value=value; this.subKeys=subKeys; }
        public static readonly NetworkAdapters Machine=new NetworkAdapters(
            (path,name)=>{ using(var key=Registry.LocalMachine.OpenSubKey(path)) return key==null?null:key.GetValue(name); },
            path=>{ using(var key=Registry.LocalMachine.OpenSubKey(path)) return key==null?new string[0]:key.GetSubKeyNames(); });
        // `id` is a GUID (the registry, or COM for a disabled adapter) or an adapter's name (COM while it is enabled).
        public ExcludedAdapter Describe(string id) {
            var adapter=new ExcludedAdapter { Id=id };
            string guid=Guid(id);
            if(guid==null) {
                adapter.Name=id;
                guid=Keys(Connections).Select(Guid).FirstOrDefault(key=>key!=null && Text(Connections+@"\"+key+@"\Connection","Name")==id);
                if(guid==null) return adapter;
            } else adapter.Name=Text(Connections+@"\"+guid+@"\Connection","Name");
            adapter.Id=guid;
            foreach(string driver in Keys(Drivers)) {
                string path=Drivers+@"\"+driver;
                if(!string.Equals(Guid(Text(path,"NetCfgInstanceId")),guid,StringComparison.Ordinal)) continue;
                string device=Text(path,"DeviceInstanceID");
                if(device!=null) {
                    adapter.Description=Text(Devices+device,"FriendlyName");
                    object flags=Value(Devices+device,"ConfigFlags");
                    adapter.Disabled=flags is int && ((int)flags&1)!=0; // CONFIGFLAG_DISABLED: switched off in Network Connections or Device Manager.
                }
                adapter.Description=adapter.Description??Text(path,"DriverDesc");
                break;
            }
            return adapter;
        }
        object Value(string path,string name) { try { return value(path,name); } catch(Exception) { return null; } }
        string Text(string path,string name) { string text=Value(path,name) as string; return string.IsNullOrWhiteSpace(text)?null:text.Trim(); }
        string[] Keys(string path) { try { return subKeys(path)??new string[0]; } catch(Exception) { return new string[0]; } }
        // "{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}" in upper case, braces optional on the way in; null for anything else.
        static string Guid(string id) {
            string text=(id??"").Trim();
            if(!text.StartsWith("{",StringComparison.Ordinal)) text="{"+text+"}";
            if(text.Length!=38 || text[37]!='}') return null;
            for(int i=1;i<37;i++) {
                char c=text[i];
                bool dash=i==9 || i==14 || i==19 || i==24;
                if(dash?c!='-':!((c>='0' && c<='9') || (c>='a' && c<='f') || (c>='A' && c<='F'))) return null;
            }
            return text.ToUpperInvariant();
        }
    }
    // The firewall check the plugin runs inside Unity's Mono, which has no COM binder. The launcher runs this too, beside its COM check.
    public static class FirewallRegistry
    {
        const string Policy=@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";
        const string GroupPolicy=@"SOFTWARE\Policies\Microsoft\WindowsFirewall";
        static readonly string[] Profiles={"DomainProfile","StandardProfile","PublicProfile"}, ProfileNames={"Domain","Private","Public"};
        // Group Policy's keys. The Windows Defender Firewall with Advanced Security policy writes the Private profile as PrivateProfile;
        // the older Windows Firewall policy (Administrative Templates) writes it as StandardProfile, like the local settings above.
        internal static readonly string[] PolicyProfiles={"DomainProfile","PrivateProfile","StandardProfile","PublicProfile"};
        const string AllProfiles="Domain|Private|Public";
        public static FirewallSnapshot Read() {
            var snapshot=new FirewallSnapshot();
            snapshot.ServiceStopped=FirewallServiceRunning()==false;
            snapshot.Rules.AddRange(RuleStrings(Policy+@"\FirewallRules"));
            for(int i=0;i<Profiles.Length;i++)
                using(var key=Registry.LocalMachine.OpenSubKey(Policy+@"\"+Profiles[i])) {
                    if(key==null) continue;
                    if(Off(key,"EnableFirewall")) snapshot.ProfileOff=true;
                    ExcludedAdapter.Add(snapshot.Excluded,ProfileNames[i],ExcludedAdapter.Ids(key.GetValue("DisabledInterfaces")),NetworkAdapters.Machine);
                }
            foreach(string profile in PolicyProfiles)
                using(var key=Registry.LocalMachine.OpenSubKey(GroupPolicy+@"\"+profile))
                    if(key!=null && Off(key,"EnableFirewall")) snapshot.PolicyOff=true;
            snapshot.PolicyNoLocalRules=PolicyDropsLocalRules();
            return snapshot;
        }
        // "Apply local firewall rules: No" in any profile's Group Policy: Windows then ignores SoloStone's rules on that profile.
        public static bool PolicyDropsLocalRules() => PolicyProfiles.Any(profile=>PolicyValue(profile,"AllowLocalPolicyMerge")==0);
        // Group Policy's "Block all incoming connections" (DoNotAllowExceptions=1) on every profile in `active`, COM's profile bits
        // (1 Domain, 2 Private, 4 Public). COM's BlockAllInboundTraffic shows only the local setting, so this reads the policy itself.
        // A policy that can't be read counts as not blocking, so state 1 keeps its refusal.
        public static bool PolicyBlocksAllInbound(int active) {
            try {
                foreach(int bit in new[]{1,2,4})
                    if((active & bit)!=0 && !(bit==1?new[]{"DomainProfile"}:bit==2?new[]{"PrivateProfile","StandardProfile"}:new[]{"PublicProfile"})
                        .Any(profile=>PolicyValue(profile,"DoNotAllowExceptions")==1)) return false;
                return true;
            } catch(Exception) { return false; }
        }
        static int? PolicyValue(string profile,string name) {
            using(var key=Registry.LocalMachine.OpenSubKey(GroupPolicy+@"\"+profile)) {
                object value=key==null?null:key.GetValue(name);
                return value is int?(int)value:(int?)null;
            }
        }
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern int RegOpenKeyExW(IntPtr key,string subKey,int options,int access,out IntPtr result);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern int RegEnumValueW(IntPtr key,uint index,StringBuilder name,ref uint nameLength,IntPtr reserved,out uint type,byte[] data,ref uint dataLength);
        [DllImport("advapi32.dll")] static extern int RegCloseKey(IntPtr key);
        // The client's stripped mscorlib has no RegistryKey.GetValueNames, so the value list comes from Win32 directly.
        static List<string> RuleStrings(string path) {
            const int KeyRead=0x20019, Wow64Key64=0x0100, NoMoreItems=259, MoreData=234, RegSz=1, MaxName=16384;
            var found=new List<string>();
            IntPtr key;
            int opened=RegOpenKeyExW(new IntPtr(unchecked((int)0x80000002)),path,0,KeyRead|Wow64Key64,out key);
            if(opened==2) return found;
            if(opened!=0) throw new InvalidOperationException("The firewall rules could not be read.");
            try {
                byte[] data=new byte[65536];
                for(uint index=0;;) {
                    var name=new StringBuilder(MaxName); uint nameLength=MaxName, dataLength=(uint)data.Length, type;
                    int result=RegEnumValueW(key,index,name,ref nameLength,IntPtr.Zero,out type,data,ref dataLength);
                    if(result==NoMoreItems) return found;
                    if(result==MoreData) { data=new byte[Math.Max(data.Length*2,(int)dataLength)]; continue; }
                    if(result!=0) throw new InvalidOperationException("The firewall rules could not be read.");
                    if(type==RegSz) found.Add(Encoding.Unicode.GetString(data,0,(int)dataLength).TrimEnd('\0'));
                    index++;
                }
            } finally { RegCloseKey(key); }
        }
        // The one thing the rules can't say: whether anything enforces them. Null when Windows won't tell us, and only "stopped" refuses.
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="OpenSCManagerW")] static extern IntPtr OpenSCManager(string machine,string database,uint access);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="OpenServiceW")] static extern IntPtr OpenService(IntPtr manager,string name,uint access);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool QueryServiceStatus(IntPtr service,uint[] status);
        [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr handle);
        static bool? FirewallServiceRunning() {
            IntPtr manager=OpenSCManager(null,null,1);
            if(manager==IntPtr.Zero) return null;
            try {
                IntPtr service=OpenService(manager,"mpssvc",4);
                if(service==IntPtr.Zero) return null;
                try { var status=new uint[7]; return QueryServiceStatus(service,status)?status[1]==4:(bool?)null; }
                finally { CloseServiceHandle(service); }
            } finally { CloseServiceHandle(manager); }
        }
        static bool Off(RegistryKey key,string name) { object value=key.GetValue(name); return value is int && (int)value==0; }
        public static void Verify(InstallRecord record,string[] executables) {
            try { Check(record,executables,Read()); }
            catch(InvalidOperationException) { throw; }
            catch(Exception) { throw new InvalidOperationException("Windows Firewall protection could not be verified. SoloStone cannot start. Run SoloStone firewall --apply."); }
        }
        // Pure, and strict: an unknown field may narrow the rule, so only fields known to leave it covering everything pass.
        public static void Check(InstallRecord record,string[] executables,FirewallSnapshot snapshot) {
            if(snapshot.ServiceStopped) throw new InvalidOperationException("The Windows Firewall service is stopped, so firewall rules do nothing. Start the Windows Firewall service, then try again.");
            if(snapshot.PolicyNoLocalRules) throw new InvalidOperationException("Windows policy prevents SoloStone from verifying its local firewall rules. Contact the computer administrator.");
            if(snapshot.ProfileOff || snapshot.PolicyOff) throw new InvalidOperationException("Windows Firewall must be enabled for all network profiles before SoloStone can start.");
            // Any profile, not only the active ones: Windows can move a network to another profile, or enable the adapter, while the game runs.
            if(snapshot.Excluded.Count>0) throw new InvalidOperationException(ExcludedAdapter.Message(snapshot.Excluded));
            var parsed=snapshot.Rules.Select(FirewallRuleText.Parse).Where(rule=>rule!=null).ToList();
            foreach(string exe in executables) {
                string name=FirewallPlan.Name(record,exe);
                var matches=parsed.Where(rule=>rule.One("Name")==name).ToList();
                if(matches.Count!=1) throw new InvalidOperationException("SoloStone firewall rules are missing or duplicated. Run SoloStone firewall --apply.");
                if(!Blocks(matches[0],exe)) throw new InvalidOperationException("SoloStone firewall protection is incomplete. Run SoloStone firewall --apply.");
            }
        }
        static bool Blocks(FirewallRuleText rule,string exe) {
            foreach(var field in rule.Fields) {
                string key=field.Key, value=field.Value.Count==1?field.Value[0]:null;
                bool good;
                switch(key.ToLowerInvariant()) {
                    case "action": good=value!=null && value.Equals("Block",StringComparison.OrdinalIgnoreCase); break;
                    case "active": good=value!=null && value.Equals("TRUE",StringComparison.OrdinalIgnoreCase); break;
                    case "dir": good=value!=null && value.Equals("Out",StringComparison.OrdinalIgnoreCase); break;
                    case "app": good=value!=null && Safety.Same(value,exe); break;
                    case "name": case "desc": good=true; break;
                    case "profile": good=string.Join("|",field.Value.OrderBy(v=>v,StringComparer.OrdinalIgnoreCase))==AllProfiles; break;
                    case "protocol": good=value=="256"; break;
                    case "iftype": good=value!=null && value.Equals("All",StringComparison.OrdinalIgnoreCase); break;
                    case "embedctxt": good=value=="SoloStone"; break;
                    default: good=false; break;
                }
                if(!good) return false;
            }
            return rule.One("Action")!=null && rule.One("Active")!=null && rule.One("Dir")!=null && rule.One("App")!=null;
        }
    }
}
