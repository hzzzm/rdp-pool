using RdpPool.Config;
var path=args[0]; var api=args[1]; var login=args[2]; var password=args[3];
var v=await ControlPlaneAuth.VerifyLoginAsync(api,login,password);
Console.WriteLine($"ok={v.Ok} {v.Message}");
if(!v.Ok) { Environment.Exit(2); return; }
var s=new ClientSettings{ApiBaseUrl=api,Login=login,Password=password,SkipMstsc=true,AutoRelease=true};
ClientConfigStore.Save(path,s);
Console.WriteLine("SAVED");
