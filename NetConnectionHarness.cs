using System;
using System.IO;

namespace UDA {
internal static class NetConnectionHarness {
  static int Main(string[] args){
    if(args.Length<1||!Directory.Exists(args[0]))return 2;
    var source=new S(){Name="AccessiblePath",Path=args[0],Username="invalid-user-for-test",Password="invalid-password",Enabled=true};
    try{
      using(var network=new Net())network.Add(source,false);
      using(var deploymentNetwork=new Net())deploymentNetwork.Add(source,true);
      Console.WriteLine("PASS accessible path bypasses credential connection");
      return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}
  }
}
}
