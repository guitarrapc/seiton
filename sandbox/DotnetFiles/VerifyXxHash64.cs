#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property ManagePackageVersionsCentrally=false
#:package System.IO.Hashing@9.0.5
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

Console.WriteLine("=== XXH64 Verification ===");

var tests = new (string N, byte[] D, ulong S)[]
{
    ("Empty", new byte[0], 0),
    ("1 byte", new byte[]{0}, 0),
    ("4 bytes", new byte[]{0,0,0,0}, 0),
    ("14 bytes", Enumerable.Range(0, 14).Select(i => (byte)i).ToArray(), 0),
    ("32 bytes", Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), 0),
    ("64 bytes", Enumerable.Range(0, 64).Select(i => (byte)i).ToArray(), 0),
    ("hello world", System.Text.Encoding.UTF8.GetBytes("hello world"), 0),
    ("seed=42", System.Text.Encoding.UTF8.GetBytes("hello world"), 42),
    ("workflow_dispatch", System.Text.Encoding.UTF8.GetBytes("workflow_dispatch"), 0),
};

var ok = true;
foreach (var (n, d, s) in tests)
{
    var r = System.IO.Hashing.XxHash64.HashToUInt64(d, (long)s);
    var o = XXH.Hash(d, s);
    var m = r == o; if (!m) ok = false;
    Console.WriteLine(string.Format("{0} {1,-20}: ref=0x{2:X16} ours=0x{3:X16}", m?"PASS":"FAIL", n, r, o));
}
Console.WriteLine(ok ? "ALL PASSED" : "SOME FAILED");

static class XXH
{
    const ulong P1=11400714785074694791,P2=14029467366897019727,P3=1609587929392839161,P4=9650029242287828579,P5=2870177450012600261;
    public static ulong Hash(ReadOnlySpan<byte> d, ulong seed=0)
    {
        var len=d.Length; ulong h;
        if(len>=32){var v1=seed+P1+P2;var v2=seed+P2;var v3=seed;var v4=seed-P1;var o=0;
        do{v1=R(v1,BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(o)));v2=R(v2,BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(o+8)));
        v3=R(v3,BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(o+16)));v4=R(v4,BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(o+24)));o+=32;}while(o<=len-32);
        h=L(v1,1)+L(v2,7)+L(v3,12)+L(v4,18);h=M(h,v1);h=M(h,v2);h=M(h,v3);h=M(h,v4);d=d.Slice(o);}
        else{h=seed+P5;}
        h+=(ulong)len;
        while(d.Length>=8){h^=R(0,BinaryPrimitives.ReadUInt64LittleEndian(d));h=L(h,27)*P1+P4;d=d.Slice(8);}
        if(d.Length>=4){h^=BinaryPrimitives.ReadUInt32LittleEndian(d)*P1;h=L(h,23)*P2+P3;d=d.Slice(4);}
        for(var i=0;i<d.Length;i++){h^=d[i]*P5;h=L(h,11)*P1;}
        h^=h>>33;h*=P2;h^=h>>29;h*=P3;h^=h>>32;return h;
    }
    static ulong R(ulong a,ulong i){a+=i*P2;a=L(a,31);a*=P1;return a;}
    static ulong M(ulong a,ulong v){v=R(0,v);a^=v;a=a*P1+P4;return a;}
    static ulong L(ulong v,int c)=>(v<<c)|(v>>(64-c));
}
