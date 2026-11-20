using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SentinelCore.Demon1.Core;

/// <summary>Local, per-SID DPAPI profile store. Stores only aggregate features, never raw input.</summary>
public sealed class DpapiVersionedProfileStore(string rootDirectory) : IProfileStore
{
    private readonly string _root=Path.GetFullPath(rootDirectory);
    private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=false};
    public ProfileSnapshot? Load(string sid)
    {
        var file=Path.Combine(UserDirectory(sid),"current.profile"); if(!File.Exists(file))return null;
        try { var json=Dpapi.Unprotect(File.ReadAllBytes(file),Encoding.UTF8.GetBytes(sid)); return JsonSerializer.Deserialize<ProfileSnapshot>(json,JsonOptions); }
        catch(CryptographicException ex){throw new InvalidDataException("Profil D1 corrompu ou protection DPAPI invalide; ne pas réinitialiser automatiquement.",ex);}
    }
    public void Save(string sid,ProfileSnapshot snapshot)
    {
        var dir=UserDirectory(sid);Directory.CreateDirectory(dir);var current=Path.Combine(dir,"current.profile");var initial=Path.Combine(dir,"initial.profile");
        var json=JsonSerializer.SerializeToUtf8Bytes(snapshot,JsonOptions);var protectedBytes=Dpapi.Protect(json,Encoding.UTF8.GetBytes(sid));
        AtomicWrite(current,protectedBytes);if(!File.Exists(initial))AtomicWrite(initial,protectedBytes);
        var history=Path.Combine(dir,"history");Directory.CreateDirectory(history);var version=Path.Combine(history,$"profile-v{snapshot.Version:D6}-{snapshot.UpdatedUtc:yyyyMMdd}.profile");if(!File.Exists(version))AtomicWrite(version,protectedBytes);
    }
    public IReadOnlyList<string> ListHistory(string sid)=>Directory.Exists(Path.Combine(UserDirectory(sid),"history"))?Directory.GetFiles(Path.Combine(UserDirectory(sid),"history"),"*.profile").Order(StringComparer.Ordinal).ToArray():[];
    public void RecordRejected(string sid,DateTimeOffset at,string reason,IReadOnlyCollection<string> featureNames)
    {
        var dir=UserDirectory(sid);Directory.CreateDirectory(dir);var minimal=new{atUtc=at,reason,featureNames=featureNames.ToArray()};var protectedBytes=Dpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(minimal),Encoding.UTF8.GetBytes(sid));var file=Path.Combine(dir,"rejected-observations.log");using var stream=new FileStream(file,FileMode.Append,FileAccess.Write,FileShare.Read);using var writer=new BinaryWriter(stream);writer.Write(protectedBytes.Length);writer.Write(protectedBytes);
    }
    public bool RestoreVersion(string sid,int version,bool ownerVerified)
    {
        if(!ownerVerified)return false;var dir=Path.Combine(UserDirectory(sid),"history");if(!Directory.Exists(dir))return false;var path=Directory.GetFiles(dir,$"profile-v{version:D6}-*.profile").Order(StringComparer.Ordinal).LastOrDefault();if(path is null)return false;
        var old=JsonSerializer.Deserialize<ProfileSnapshot>(Dpapi.Unprotect(File.ReadAllBytes(path),Encoding.UTF8.GetBytes(sid)),JsonOptions);if(old is null)return false;Save(sid,old with{Version=(Load(sid)?.Version??0)+1,UpdatedUtc=DateTimeOffset.UtcNow});return true;
    }
    private string UserDirectory(string sid)=>Path.Combine(_root,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..24]);
    private static void AtomicWrite(string path,byte[] bytes){var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllBytes(temp,bytes);File.Move(temp,path,true);}
}

internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll",SetLastError=true,CharSet=CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input,string? description,ref Blob entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true,CharSet=CharSet.Unicode)] private static extern bool CryptUnprotectData(ref Blob input,IntPtr description,ref Blob entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static byte[] Protect(byte[] data,byte[] entropy)=>Transform(data,entropy,true);
    public static byte[] Unprotect(byte[] data,byte[] entropy)=>Transform(data,entropy,false);
    private static byte[] Transform(byte[] data,byte[] entropy,bool protect)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("DPAPI profile store nécessite Windows.");
        var input=ToBlob(data);var extra=ToBlob(entropy);Blob output=default;
        try{var ok=protect?CryptProtectData(ref input,null,ref extra,IntPtr.Zero,IntPtr.Zero,1,out output):CryptUnprotectData(ref input,IntPtr.Zero,ref extra,IntPtr.Zero,IntPtr.Zero,1,out output);if(!ok)throw new CryptographicException(Marshal.GetLastWin32Error());var result=new byte[output.Length];Marshal.Copy(output.Data,result,0,output.Length);return result;}
        finally{Free(input);Free(extra);if(output.Data!=IntPtr.Zero)LocalFree(output.Data);}
    }
    private static Blob ToBlob(byte[] data){var ptr=Marshal.AllocHGlobal(data.Length);Marshal.Copy(data,0,ptr,data.Length);return new Blob{Length=data.Length,Data=ptr};}
    private static void Free(Blob blob){if(blob.Data!=IntPtr.Zero)Marshal.FreeHGlobal(blob.Data);}
}
