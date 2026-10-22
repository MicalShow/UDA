// Produce a deterministic visual-QA artifact from the actual served HTML, without a real AI key/server.
const fs = require('fs');
const html = fs.readFileSync(process.argv[2], 'utf8');
const mock = `<script>
window.fetch=async function(url){let data={ok:true};
if(url.includes('/dashboard'))data={repo:{infCount:1847,hardwareIdCount:12840,compatibleIdCount:3020,architecture:'multi',generator:'UDA Web'}};
else if(url.includes('/drivers?'))data={page:1,total:2,items:[{id:'packages/display/test.inf',category:'显卡',inf:'display.inf',provider:'Test Display Vendor',version:'32.0.1.0',date:'10/01/2026',signature:'trusted',hardwareIds:12,deviceName:'Intel UHD Graphics 显卡'},{id:'packages/network/net.inf',category:'无线网卡',inf:'wireless.inf',provider:'Test Network Vendor',version:'23.1.0.0',date:'09/01/2026',signature:'trusted',hardwareIds:8,deviceName:'Intel Wi-Fi 6E AX211 无线网卡'}]};
else if(url.endsWith('/ai'))data={name:'测试订阅',baseUrl:'https://api.example.invalid/v1',model:'test-model',allowedHosts:'download.microsoft.com',hasKey:true};
else if(url.includes('/install-logs'))data={page:1,pages:2,total:26,pageSize:20,machines:26,hardware:320,updates:118,items:[{Utc:'2026-10-16T06:30:00Z',Machine:'DESKTOP-FINANCE-01',Ip:'192.168.10.21',Mac:'00:11:22:33:44:55',RunId:'deployment-20261016-01',Name:'Intel Wi-Fi 6E AX211',Version:'23.120.0.3',Status:'installed',Detail:'驱动安装成功。实际绑定核验通过，设备运行正常。'},{Utc:'2026-10-16T06:29:00Z',Machine:'DESKTOP-FINANCE-01',Ip:'192.168.10.21',Mac:'00:11:22:33:44:55',RunId:'deployment-20261016-01',Name:'Intel UHD Graphics',Version:'32.0.101.6078',Status:'installed',Detail:'下载、SHA256 校验、适用性检查与安装已完成。'},{Utc:'2026-10-16T05:15:00Z',Machine:'LAPTOP-ENGINEER-02',Ip:'192.168.10.32',Mac:'AA:BB:CC:DD:EE:FF',RunId:'maintenance-20261016-02',Name:'Realtek Audio',Version:'6.0.9800.1',Status:'unverified',Detail:'安装命令成功，等待重启后核验绑定。'}]};
else if(url.includes('/driver?id='))data={driver:{id:'packages/network/net.inf',provider:'Intel',driverClass:'Net',hardwareIds:['PCI\\VEN_8086&DEV_51F0'],compatibleIds:[]}};
else if(url.includes('/policy-candidates'))data={scope:{HardwareId:'PCI\\VEN_8086&DEV_51F0',Architecture:'amd64',DriverClass:'Net',MajorVersion:10,MinBuild:22000,MaxBuild:0},policy:{StableHash:'stable',LatestHash:'latest'},items:[{hash:'latest',version:'23.120.0.3',date:'10/01/2026',provider:'Intel',inf:'packages/network/wifi-23.120/netwtw.inf',decorations:['NTamd64.10.0...22000']},{hash:'stable',version:'23.100.0.4',date:'08/10/2026',provider:'Intel',inf:'packages/network/wifi-23.100/netwtw.inf',decorations:['NTamd64.10.0']}]};
else if(url.includes('/release-policies'))data={items:[],invalid:[]};
return {ok:true,status:200,json:async()=>data,text:async()=>JSON.stringify(data)}};
</script>`;
const section = Number(process.argv[4] || 0);
const activate = `<script>setTimeout(()=>{selectWorkspace(${section});${process.argv[5]==='dialog'?"selectDriverPolicy('packages/network/net.inf','stable');":''}},250);</script>`;
fs.writeFileSync(process.argv[3], html.replace('<script>', mock+'<script>').replace('</body>',activate+'</body>'), 'utf8');
console.log('Generated deterministic Web console visual fixture');
