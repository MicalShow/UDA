// compile: csc /target:winexe /out:DriverHelper.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.Core.dll DriverHelper.cs
// v4.2 — 匹配修复: 全总线类型ID提取, 只显示有驱动的设备
using System;using System.Collections.Generic;using System.ComponentModel;using System.Diagnostics;using System.Drawing;using System.IO;using System.Management;using System.Runtime.InteropServices;using System.Security.Principal;using System.Text;using System.Text.RegularExpressions;using System.Windows.Forms;
namespace DriverHelper{
static class Program{
[STAThread]static void Main(){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
if(!IsAdmin()){try{Process.Start(new ProcessStartInfo{FileName=Application.ExecutablePath,Verb="runas",UseShellExecute=true});}catch{}return;}
try{Application.Run(new MainForm());}catch(Exception ex){MessageBox.Show("启动失败:\n"+ex.Message,"错误",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
static bool IsAdmin(){try{return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);}catch{return false;}}}
class Src{public string Path;public string User;public string Pass;}
class Cfg{public string FP;public List<Src> Ss=new List<Src>();
public static Cfg Load(string dir){
string p=Path.Combine(dir,"Drivers.txt");var c=new Cfg();c.FP=p;c.Ss=new List<Src>();
if(!File.Exists(p)){var b=new StringBuilder();
b.AppendLine("# ─── 万能驱动助手 ───");b.AppendLine("# 格式1: \\\\服务器\\共享名");b.AppendLine("# 格式2: \\\\服务器\\共享名|用户名|密码");b.AppendLine("# {MODEL}→电脑型号");b.AppendLine();b.AppendLine("\\\\192.168.1.100\\Drivers");File.WriteAllText(p,b.ToString(),new UTF8Encoding(false));return c;}
foreach(string ln in File.ReadAllLines(p,Encoding.UTF8)){string t=ln.Trim();if(t.Length==0||t.StartsWith("#"))continue;var e=new Src();var x=t.Split('|');e.Path=x[0].Trim();if(x.Length>1)e.User=x[1].Trim();if(x.Length>2)e.Pass=x[2].Trim();c.Ss.Add(e);}return c;}}
class Smb{[DllImport("mpr.dll",CharSet=CharSet.Unicode)]static extern int WNetUseConnection(IntPtr h,ref NR nr,string p,string u,int f,StringBuilder n,ref int bs,ref int r);struct NR{public int a,b,c,d;public string e,f,g,h;}
public static void Go(string u,string us,string ps){if(string.IsNullOrEmpty(us))return;int sz=0,rr=0;var nr=new NR();WNetUseConnection(IntPtr.Zero,ref nr,ps,us,4,null,ref sz,ref rr);}}
class Api{[DllImport("newdev.dll",CharSet=CharSet.Unicode)]static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr h,string i,string f,int fl,ref bool r);
public static bool Install(string hwid,string inf){bool r=false;return UpdateDriverForPlugAndPlayDevices(IntPtr.Zero,hwid,inf,1,ref r);}}

class MainForm:Form{
TabControl _tb;RichTextBox _log;ListView _lv;ProgressBar _pb;Label _lb,_lt;Button _btn;
BackgroundWorker _bw;Stopwatch _sw=new Stopwatch();Cfg _cfg;int _tot=0,_dn=0;
Dictionary<string,string> _devNames=new Dictionary<string,string>();

public MainForm(){
Text="万能驱动助手 v4";Size=new Size(1000,720);StartPosition=FormStartPosition.CenterScreen;FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;BackColor=Color.FromArgb(240,245,250);Font=new Font("微软雅黑",9F);
var lc=new Label{Text="配置: 加载中...",Location=new Point(10,5),Size=new Size(960,20),ForeColor=Color.DimGray};Controls.Add(lc);
_tb=new TabControl{Location=new Point(10,28),Size=new Size(965,580)};
var tp1=new TabPage("匹配成功的设备");_lv=new ListView{View=View.Details,FullRowSelect=true,GridLines=true,Dock=DockStyle.Fill};
_lv.Columns.Add("设备管理器名称",300);_lv.Columns.Add("硬件ID",250);_lv.Columns.Add("驱动文件",200);_lv.Columns.Add("状态",80);tp1.Controls.Add(_lv);_tb.TabPages.Add(tp1);
var tp2=new TabPage("日志");_log=new RichTextBox{Dock=DockStyle.Fill,ReadOnly=true,BackColor=Color.Black,ForeColor=Color.LightGreen,Font=new Font("Consolas",9F),WordWrap=false};tp2.Controls.Add(_log);_tb.TabPages.Add(tp2);Controls.Add(_tb);
_pb=new ProgressBar{Location=new Point(10,618),Size=new Size(620,26)};Controls.Add(_pb);
_lb=new Label{Text="就绪",Location=new Point(10,650),Size=new Size(620,20),ForeColor=Color.DimGray};Controls.Add(_lb);
_lt=new Label{Text="",Location=new Point(640,650),Size=new Size(200,20),ForeColor=Color.DimGray,TextAlign=ContentAlignment.MiddleRight};Controls.Add(_lt);
_btn=new Button{Text="▶ 开始",Font=new Font("微软雅黑",10F,FontStyle.Bold),BackColor=Color.FromArgb(0,120,212),ForeColor=Color.White,FlatStyle=FlatStyle.Flat,FlatAppearance={BorderSize=0},Size=new Size(200,42),Location=new Point(650,618)};_btn.Click+=OnGo;Controls.Add(_btn);
_bw=new BackgroundWorker{WorkerReportsProgress=true};_bw.DoWork+=BwW;_bw.ProgressChanged+=BwP;_bw.RunWorkerCompleted+=BwD;
Load+=(s,e)=>{string dir=Path.GetDirectoryName(Application.ExecutablePath);_cfg=Cfg.Load(dir);lc.Text="配置: "+_cfg.FP+" ("+_cfg.Ss.Count+"路径)";_btn.Enabled=_cfg.Ss.Count>0;if(_cfg.Ss.Count==0)MessageBox.Show("按示例写路径:\n"+_cfg.FP,"提示",MessageBoxButtons.OK,MessageBoxIcon.Information);};}
void Lg(string t){string ts=DateTime.Now.ToString("HH:mm:ss")+" "+t;if(InvokeRequired){string s=ts;BeginInvoke((MethodInvoker)delegate{_log.AppendText(s+"\n");_log.ScrollToCaret();});return;}_log.AppendText(ts+"\n");_log.ScrollToCaret();}
void SetSt(string t){if(InvokeRequired){string s=t;BeginInvoke((MethodInvoker)delegate{SetSt(s);});return;}_lb.Text=t;}
void OnGo(object s,EventArgs e){_btn.Enabled=false;_lv.Items.Clear();_log.Clear();_pb.Value=0;_tot=0;_dn=0;_lt.Text="";_tb.SelectedIndex=0;Lg("=== 开始 ===");_bw.RunWorkerAsync();}
void BwW(object s,DoWorkEventArgs e){try{
string md=GetModel();var idx=new Dictionary<string,string>();var infs=new HashSet<string>();int nf=0;
foreach(var sr in _cfg.Ss){
string p=Environment.ExpandEnvironmentVariables(sr.Path);if(!string.IsNullOrEmpty(md))p=p.Replace("{MODEL}",md);
if(p.StartsWith("\\\\")&&!string.IsNullOrEmpty(sr.User))Smb.Go(p,sr.User,sr.Pass);if(!Directory.Exists(p)){Lg("跳过: "+p);continue;}
Lg("扫描: "+p);
try{foreach(string f in Directory.GetFiles(p,"*.inf",SearchOption.AllDirectories)){nf++;if(infs.Contains(f.ToLower()))continue;infs.Add(f.ToLower());
try{string cx=File.ReadAllText(f,Encoding.UTF8);int cnt=0;
foreach(Match m in Regex.Matches(cx,@"(?im)\b([A-Z][A-Z0-9_]{0,31}\\[A-Z0-9_&\.\-{}#]+)")){string k=m.Groups[1].Value.ToUpper();if(!idx.ContainsKey(k)){idx[k]=f;cnt++;}
int i1=k.IndexOf('\\');if(i1>0){string bus=k.Substring(0,i1);string rest=k.Substring(i1+1);
if(bus=="PCI"||bus=="USB"||bus=="HDAUDIO"||bus=="ACPI"||bus=="ACP"){var mm=Regex.Match(rest,@"(VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4})");if(mm.Success&&!idx.ContainsKey(mm.Groups[1].Value)){idx[mm.Groups[1].Value]=f;cnt++;}
mm=Regex.Match(rest,@"(ACPI\\[A-Z0-9_]+)");if(mm.Success&&!idx.ContainsKey(mm.Groups[1].Value)){idx[mm.Groups[1].Value]=f;cnt++;}}}}
}catch{}if(nf%200==0)Lg("已处理 "+nf+" INF");}}catch(Exception ex){Lg("扫描异常:"+ex.Message);}}
Lg("索引: "+idx.Count+"硬件ID, "+nf+"INF");
var devHwIds=new Dictionary<string,string>();
try{using(var sw=new ManagementObjectSearcher("SELECT Name,DeviceID,PnpClass FROM Win32_PnPEntity"))using(var cw=sw.Get())foreach(ManagementObject o in cw){string n=(o["Name"]+"").Trim();string id=(o["DeviceID"]+"").Trim();if(string.IsNullOrEmpty(id))continue;if(string.IsNullOrEmpty(n))n="【未知】"+id;string cls=(o["PnpClass"]+"").Trim();if(!string.IsNullOrEmpty(cls)&&!n.Contains(cls))n=cls+":"+n;if(!devHwIds.ContainsKey(id))devHwIds[id]=n;
if(id.Contains("\\")){string rest=id.Substring(id.IndexOf('\\')+1);var mm=Regex.Match(rest,@"(VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4})");if(mm.Success&&!devHwIds.ContainsKey(mm.Groups[1].Value))devHwIds[mm.Groups[1].Value]=n;mm=Regex.Match(rest,@"(ACPI\\[A-Z0-9_]+)");if(mm.Success&&!devHwIds.ContainsKey(mm.Groups[1].Value))devHwIds[mm.Groups[1].Value]=n;}}
using(var sw=new ManagementObjectSearcher("SELECT HardwareID,DeviceName FROM Win32_PnPSignedDriver"))using(var cw=sw.Get())foreach(ManagementObject o in cw){string h=(o["HardwareID"]+"").Trim();string nm=(o["DeviceName"]+"").Trim();if(string.IsNullOrEmpty(h)||string.IsNullOrEmpty(nm))continue;
foreach(string x in h.Split('\n')){string t=x.Trim();if(t.Length>0&&!devHwIds.ContainsKey(t))devHwIds[t]=nm;}}
}catch(Exception ex){Lg("WMI异常:"+ex.Message);}
Lg("设备数: "+devHwIds.Count);
int mc=0;
var matchedItems=new List<ListViewItem>();
foreach(var kv in idx){string hwid=kv.Key;string inf=kv.Value;if(devHwIds.ContainsKey(hwid)){string devName=devHwIds[hwid];bool exists=false;foreach(var x in matchedItems){string tag=x.Tag as string;if(tag!=null&&tag==hwid){exists=true;break;}}if(!exists){mc++;var it=new ListViewItem(devName);it.SubItems.Add(hwid);it.SubItems.Add(Path.GetFileName(inf));it.SubItems.Add("待安装");it.ForeColor=Color.Green;it.Tag=hwid;matchedItems.Add(it);Lg("✓ "+devName+" ← "+Path.GetFileName(inf));}}}
BeginInvoke((MethodInvoker)delegate{foreach(var it in matchedItems)_lv.Items.Add(it);});
Lg("匹配: "+mc+"个");_bw.ReportProgress(0,"MD:"+mc+"匹配");if(mc==0){Lg("无驱动");_bw.ReportProgress(0,"OK:无驱动");return;}
var plan=new List<string>();var planHwids=new List<string>();
foreach(var it in matchedItems){string infName=it.SubItems[2].Text;
string ff=null;foreach(var kv in idx){if(Path.GetFileName(kv.Value)==infName){ff=kv.Value;break;}}
if(ff!=null){plan.Add(ff);planHwids.Add(it.Tag as string);}}
Lg("待装: "+plan.Count+"个");_tot=plan.Count;_dn=0;
for(int i=5;i>=1;i--){_bw.ReportProgress(0,"WT:"+i);System.Threading.Thread.Sleep(1000);}
_sw.Reset();_sw.Start();int ok=0,fl=0,sk=0;
// 第一阶段：全部加入驱动商店（不加/install）
foreach(string f in infs){
string fn=Path.GetFileName(f);
try{var px=Process.Start(new ProcessStartInfo{FileName="pnputil.exe",Arguments="/add-driver \""+f+"\"",UseShellExecute=false,CreateNoWindow=true});if(px!=null)px.WaitForExit(30000);}catch{}
}
Lg("商店阶段完成，触发设备扫描...");
// 第二阶段：PnP扫描（触发Windows自动匹配已入商店的驱动）
try{var ps=Process.Start(new ProcessStartInfo{FileName="pnputil.exe",Arguments="/scan-devices",UseShellExecute=false,CreateNoWindow=true});if(ps!=null)ps.WaitForExit(60000);}catch{}
Lg("设备扫描完成");
// 第三阶段：逐个安装已验证匹配的驱动
for(int pi=0;pi<plan.Count;pi++){_dn++;long ms=_sw.ElapsedMilliseconds;int rm=_dn>0?(int)((ms/_dn)*(plan.Count-_dn)/1000):0;string infFile=plan[pi];string hwid=planHwids[pi];string devName=devHwIds.ContainsKey(hwid)?devHwIds[hwid]:hwid;
_bw.ReportProgress(0,"IN:"+devName+"|"+infFile+"|"+_dn+"|"+plan.Count+"|"+rm);Lg("安装: "+devName+" ← "+Path.GetFileName(infFile));
try{var pr=Process.Start(new ProcessStartInfo{FileName="pnputil.exe",Arguments="/add-driver \""+infFile+"\" /install",UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true});
string output="";if(pr!=null){pr.WaitForExit(60000);output=pr.StandardOutput.ReadToEnd();int ec=pr.ExitCode;
if(ec==0){ok++;UpdateItem(hwid,"✓成功");Lg("  ✓ 成功");}
else if(ec==3010){ok++;UpdateItem(hwid,"✓成功(需重启)");Lg("  ✓ 成功(需重启)");}
else if(ec==259){fl++;UpdateItem(hwid,"⏭跳过(无适用设备)");Lg("  ⏭ code="+ec+" (无适用设备)");}
else{fl++;UpdateItem(hwid,"✗失败(code="+ec+")");Lg("  ✗ code="+ec);if(output.Length>0)Lg("  输出: "+output.Substring(0,Math.Min(200,output.Length)).Replace("\n","|"));}
}else{fl++;UpdateItem(hwid,"✗启动失败");Lg("  ✗ 进程启动失败");}
}catch(Exception ex){fl++;Lg("  ✗ 异常:"+ex.Message);UpdateItem(hwid,"✗异常");}}
_bw.ReportProgress(0,"OK:成功="+ok+",失败="+fl);Lg("完成: 成功"+ok+",失败"+fl);
}catch(Exception ex){_bw.ReportProgress(0,"FT:"+ex.Message);Lg("错误:"+ex.Message);}}
string GetModel(){try{using(var sw=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))using(var cw=sw.Get())foreach(ManagementObject o in cw)return Regex.Replace((o["Manufacturer"]+"")+"-"+(o["Model"]+""),"[^A-Za-z0-9-]","").Trim('-');}catch{}return"";}
void BwP(object s,ProgressChangedEventArgs e){string d=e.UserState as string;if(d==null)return;try{
if(d.StartsWith("MD:")){_lb.Text=d.Substring(3);_tb.SelectedIndex=0;}else if(d.StartsWith("WT:")){int sec=0;int.TryParse(d.Substring(3),out sec);_lb.Text="⏳ "+sec+" 秒";}else if(d.StartsWith("OK:")){_lb.Text="✅ "+d.Substring(3);_lt.Text="";_pb.Value=_pb.Maximum;}else if(d.StartsWith("FT:"))_lb.Text="❌ "+d.Substring(3);
else if(d.StartsWith("IN:")){var p=d.Substring(3).Split('|');int c=0,t=0,r=0;if(p.Length>=3)int.TryParse(p[2],out c);if(p.Length>=4)int.TryParse(p[3],out t);if(p.Length>=5)int.TryParse(p[4],out r);if(t>0){_pb.Maximum=t;_pb.Value=Math.Min(c,t);}_lb.Text="安装: "+(p.Length>=1?p[0]:"");if(r>0)_lt.Text="剩余 "+(r<60?r+"秒":(r/60)+"分"+(r%60)+"秒");}}catch{}}
void BwD(object s,RunWorkerCompletedEventArgs e){_btn.Enabled=true;}
void UpdateItem(string tag,string st){if(InvokeRequired){string t=tag;string s=st;BeginInvoke((MethodInvoker)delegate{UpdateItem(t,s);});return;}
foreach(ListViewItem it in _lv.Items)if((it.Tag as string)==tag){it.SubItems[3].Text=st;if(st.Contains("✓"))it.ForeColor=Color.Green;else if(st.Contains("✗"))it.ForeColor=Color.Red;break;}}
}}
