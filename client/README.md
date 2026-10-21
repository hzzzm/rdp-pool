# Windows 客户端

依赖：**.NET 6 SDK**（或更新）。仅 Windows 会调 `mstsc`；其它 OS 只打印票据。

```powershell
cd RdpPoolClient
copy ..\appsettings.example.json .\appsettings.json
# 填写 ApiBaseUrl（默认 http://127.0.0.1:8081）、Login/Password
# 测试机关机时: "SkipMstsc": true

dotnet run -- appsettings.json
```

流程对齐架构 §6.6：登录 → 可用组 0/1/>1 → acquire → mstsc →（Enter）release。
