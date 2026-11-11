using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// Launcher only: this needs the COM binder, which Unity's Mono does not have. The plugin uses FirewallRegistry.
namespace SoloStone
{
    public static partial class FirewallProtection
    {
        public static void Verify(InstallRecord record,string[] executables) {
            object policy=null;
            try {
                policy=Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2",true));
                VerifyPolicy(record,executables,policy);
            } catch(InvalidOperationException) { throw; }
            catch(Exception error) { throw new InvalidOperationException("Windows Firewall protection could not be verified. SoloStone cannot start. Run SoloStone firewall --apply.",error); }
            finally { if(policy!=null && Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy); }
        }
        // Tests supply API-shaped snapshots, and registry contents for adapter names.
        internal static void VerifyPolicy(InstallRecord record,string[] executables,dynamic policy,NetworkAdapters adapters=null) {
            // 1: Group Policy owns the profile, so local rules may not apply. 2: "Block all incoming connections" is on; that only stops
            // inbound rules, and SoloStone's are outbound blocks, so they still apply. Group Policy that drops local rules is also caught
            // by FirewallRegistry (AllowLocalPolicyMerge), which the launcher runs after this.
            int modify=(int)policy.LocalPolicyModifyState;
            if(modify!=0 && modify!=2)
                throw new InvalidOperationException("Windows policy prevents SoloStone from verifying its local firewall rules. Contact the computer administrator.");
            int active=(int)policy.CurrentProfileTypes;
            if(active==0 || (active & ~7)!=0)
                throw new InvalidOperationException("Windows Firewall active profiles could not be verified. SoloStone cannot start.");
            // Exclusions count on every profile, as in FirewallRegistry: Windows can move a network to another profile while the game runs.
            var excluded=new List<ExcludedAdapter>();
            foreach(int profile in new[]{1,2,4}) {
                if(!(bool)policy.FirewallEnabled[profile])
                    throw new InvalidOperationException("Windows Firewall must be enabled for all network profiles before SoloStone can start.");
                object ids=policy.ExcludedInterfaces[profile];
                ExcludedAdapter.Add(excluded,profile==1?"Domain":profile==2?"Private":"Public",ExcludedAdapter.Ids(ids),adapters??NetworkAdapters.Machine);
            }
            if(excluded.Count>0) throw new InvalidOperationException(ExcludedAdapter.Message(excluded));
            foreach(string exe in executables) {
                var matches=new List<object>();
                foreach(dynamic rule in policy.Rules)
                    if((string)rule.Name==FirewallPlan.Name(record,exe)) matches.Add(rule);
                if(matches.Count!=1) throw new InvalidOperationException("SoloStone firewall rules are missing or duplicated. Run SoloStone firewall --apply.");
                dynamic r=matches[0];
                // netsh-created rules have no group; retain compatibility with the previous SoloStone group.
                string group=(string)r.Grouping;
                if(!(bool)r.Enabled || (int)r.Direction!=2 || (int)r.Action!=0 || (int)r.Profiles!=int.MaxValue
                    || (!string.IsNullOrEmpty(group) && group!="SoloStone")
                    || !Safety.Same((string)r.ApplicationName,exe) || (int)r.Protocol!=256
                    || (string)r.LocalAddresses!="*" || (string)r.RemoteAddresses!="*" || (string)r.InterfaceTypes!="All"
                    || !string.IsNullOrEmpty((string)r.ServiceName) || HasInterfaces(r.Interfaces))
                    throw new InvalidOperationException("SoloStone firewall protection is incomplete. Run SoloStone firewall --apply.");
            }
        }
        static bool HasInterfaces(object value) => value!=null && (!(value is Array) || ((Array)value).Length!=0);
    }
}
