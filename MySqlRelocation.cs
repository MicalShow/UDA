// Full UDA MySQL-to-MySQL relocation. No source deletion and no target overwrite.
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UDA {
public static class MySqlRelocationProgram {
 [STAThread] public static void Main() {
  Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
  if (!new WindowsAdminChecker().IsAdministrator()) { MessageBox.Show("请以管理员身份在 192.0.2.10 的 UDA 服务端运行。"); return; }
  try {
   string url="http://localhost:8731", file=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"manager-url.txt");
   if(File.Exists(file)) { Uri uri; if(Uri.TryCreate(File.ReadAllText(file).Trim(),UriKind.Absolute,out uri)) url=new UriBuilder(uri.Scheme,"localhost",uri.Port).Uri.ToString().TrimEnd('/'); }
   using(var login=new MigrationLoginForm(url)) { if(login.ShowDialog()!=DialogResult.OK)return; Application.Run(new MySqlRelocationForm(url,login.Cookies)); }
  } catch(Exception ex) { MessageBox.Show(ex.Message,"迁移工具",MessageBoxButtons.OK,MessageBoxIcon.Error); }
 }
}
internal sealed class RelocationEndpoint : GroupBox {
 public TextBox Host=new TextBox(), Database=new TextBox(), User=new TextBox(), Password=new TextBox();
 public NumericUpDown Port=new NumericUpDown{Minimum=1,Maximum=65535,Value=3306};
 public ComboBox Ssl=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList};
 public TextBox Ca=new TextBox();
 public RelocationEndpoint(string title,string host,string database) {
  Text=title; Width=405; Height=295; Ssl.Items.AddRange(MigrationConnectionOptions.SslModes);Ssl.SelectedIndex=0;
  Host.Text=host;Database.Text=database;Password.UseSystemPasswordChar=true;
  string[] names={"数据库地址","端口","库名","用户名","密码","TLS / SSL","CA 证书（可选）"};
  Control[] inputs={Host,Port,Database,User,Password,Ssl,Ca};
  for(int i=0;i<inputs.Length;i++){ Controls.Add(new Label{Text=names[i],Left=10,Top=30+i*35,Width=115});inputs[i].SetBounds(130,26+i*35,255,27);Controls.Add(inputs[i]); }
 }
 public string Connection(){return MigrationConnectionOptions.Build(Host.Text,(uint)Port.Value,Database.Text,User.Text,Password.Text,Convert.ToString(Ssl.SelectedItem),Ca.Text);}
 public void LoadConnection(string connection) {
  var b=new DbConnectionStringBuilder{ConnectionString=connection};
  foreach(string k in new[]{"Server","Host","Data Source"})if(b.ContainsKey(k))Host.Text=Convert.ToString(b[k]);
  if(b.ContainsKey("Database"))Database.Text=Convert.ToString(b["Database"]);
  foreach(string k in new[]{"User ID","UserID","User Id","Uid","Username","User Name"})if(b.ContainsKey(k))User.Text=Convert.ToString(b[k]);
  foreach(string k in new[]{"Password","Pwd"})if(b.ContainsKey(k))Password.Text=Convert.ToString(b[k]);
  if(b.ContainsKey("Port"))Port.Value=Convert.ToDecimal(b["Port"]);
  foreach(string k in new[]{"SslMode","SSL Mode"})if(b.ContainsKey(k))Ssl.SelectedItem=Convert.ToString(b[k]);
  if(b.ContainsKey("SslCa"))Ca.Text=Convert.ToString(b["SslCa"]);
 }
}
internal sealed class MySqlRelocationForm : Form {
 readonly string url;readonly System.Net.CookieContainer cookies;
 readonly string logFile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"UniversalDriverAssistant","MigrationLogs","mysql-relocation-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".log");
 readonly TextBox root=new TextBox(), log=new TextBox{Multiline=true,ScrollBars=ScrollBars.Vertical,ReadOnly=true};
 readonly RelocationEndpoint source=new RelocationEndpoint("源 MySQL","192.0.2.10",""),target=new RelocationEndpoint("目标 MySQL","192.0.2.10","tineco_UDA");
 readonly Button migrate=new Button{Text="完整迁移、校验并切换"},test=new Button{Text="检查连接与先决条件"},load=new Button{Text="读取源配置"},cancel=new Button{Text="中断"};
 readonly CheckBox allowPacket=new CheckBox{Text="允许提高目标数据包限制",AutoSize=true};
 readonly ProgressBar progress=new ProgressBar();CancellationTokenSource cts; bool busy;
 public MySqlRelocationForm(string serviceUrl,System.Net.CookieContainer session) {
  Directory.CreateDirectory(Path.GetDirectoryName(logFile));File.WriteAllText(logFile,"UDA MySQL relocation log\r\n",new UTF8Encoding(false));ServerStorage.SecureFile(logFile);
  url=serviceUrl;cookies=session;Text="UDA · MySQL 服务器迁移";Font=new Font("Microsoft YaHei UI",9F);ClientSize=new Size(870,670);MinimumSize=Size;StartPosition=FormStartPosition.CenterScreen;
  Controls.Add(new Label{Text="UDA 驱动仓库目录",Left=20,Top=20,Width=140});root.SetBounds(165,16,540,28);Controls.Add(root);load.SetBounds(715,15,135,30);Controls.Add(load);
  source.Location=new Point(20,55);target.Location=new Point(445,55);Controls.Add(source);Controls.Add(target);
  Controls.Add(new Label{Text="迁移时暂停本机 UDA 服务；完整复制所有表和数据，保留源库。目标库必须不存在。驱动压缩包不移动。",Left=20,Top=360,Width=830,Height=38});
  test.SetBounds(20,405,185,34);migrate.SetBounds(220,405,230,34);cancel.SetBounds(465,405,100,34);cancel.Enabled=false;Controls.AddRange(new Control[]{test,migrate,cancel});
  allowPacket.SetBounds(585,410,265,26);Controls.Add(allowPacket);
  progress.SetBounds(20,450,830,18);Controls.Add(progress);log.SetBounds(20,480,830,170);log.Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right;Controls.Add(log);
  load.Click+=delegate{try{LoadSource();}catch(Exception ex){MessageBox.Show(ex.Message);}};
  test.Click+=async delegate{await Execute(false);};migrate.Click+=async delegate{await Execute(true);};cancel.Click+=delegate{if(cts!=null)cts.Cancel();};
  FormClosing+=delegate(object sender,FormClosingEventArgs e){if(busy){e.Cancel=true;MessageBox.Show("请先中断并等待当前操作结束。");}};
  Append("日志文件："+logFile);
  try{using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\UdaLanDriverService")){string cmd=key==null?"":Convert.ToString(key.GetValue("ImagePath",""));var m=System.Text.RegularExpressions.Regex.Match(cmd,"(?:^|\\s)--repo\\s+(?:\"([^\"]*)\"|(\\S+))",System.Text.RegularExpressions.RegexOptions.IgnoreCase);if(m.Success)root.Text=m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Value;}if(root.Text.Length>0)LoadSource();}catch(Exception ex){Append("未自动读取配置："+ex.Message);}
 }
 void Append(string message){string line=DateTime.Now.ToString("HH:mm:ss ")+message+Environment.NewLine;log.AppendText(line);try{File.AppendAllText(logFile,line,new UTF8Encoding(false));}catch(Exception ex){log.AppendText("日志文件写入失败："+ex.Message+Environment.NewLine);}}
 void LoadSource(){string p=Path.Combine(root.Text,".uda-storage.json");if(!File.Exists(p))throw new IOException("请选择包含 .uda-storage.json 的正式仓库目录。");var c=ServerStorage.Config(root.Text);if(!c.Enabled)throw new IOException("源端尚未启用 MySQL。");source.LoadConnection(RelocationEngine.Unprotect(c));Append("已读取本机源数据库配置，密码未输出。");}
 async Task Execute(bool apply){
  if(busy)return;
  try{
   MigrationHttp.Send(url,"/api/admin/database","GET",null,cookies,false); // revalidate admin before every operation
   string sourceConnection=source.Connection(),targetConnection=target.Connection(),repo=Path.GetFullPath(root.Text);
   if(apply&&MessageBox.Show("将暂停 UDA 服务，在目标新建 "+target.Database.Text+"，完整迁移并校验后切换连接。迁移期间客户端无法使用服务。源库不删除。继续？","执行迁移",MessageBoxButtons.OKCancel,MessageBoxIcon.Warning)!=DialogResult.OK)return;
   bool adjustPacket=allowPacket.Checked;busy=true;cts=new CancellationTokenSource();source.Enabled=target.Enabled=root.Enabled=load.Enabled=test.Enabled=migrate.Enabled=allowPacket.Enabled=false;cancel.Enabled=true;progress.Style=ProgressBarStyle.Marquee;
   var report=new Progress<string>(Append);CancellationToken token=cts.Token;
   await Task.Run(()=>{RelocationEngine.PreparePacketLimit(sourceConnection,targetConnection,adjustPacket,m=>((IProgress<string>)report).Report(m));if(apply)RelocationEngine.Relocate(repo,sourceConnection,targetConnection,token,m=>((IProgress<string>)report).Report(m),url);else RelocationEngine.Preflight(sourceConnection,targetConnection,m=>((IProgress<string>)report).Report(m));});
   Append(apply?"完成：数据已校验，连接已切换，UDA 服务已启动。源数据库保留。":"检查通过。尚未创建目标库或修改服务配置。");
  }catch(OperationCanceledException){Append("已中断。源数据保留；目标可能保留未完成的空表，未覆盖已有数据。");}
   catch(Exception ex){Append("失败："+ex.Message);}
  finally{busy=false;source.Enabled=target.Enabled=root.Enabled=load.Enabled=test.Enabled=migrate.Enabled=allowPacket.Enabled=true;cancel.Enabled=false;progress.Style=ProgressBarStyle.Blocks;if(cts!=null){cts.Dispose();cts=null;}}
 }
}
internal static class RelocationEngine {
 internal static string Unprotect(ServerStorageConfig cfg){return Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(cfg.ProtectedConnection),null,System.Security.Cryptography.DataProtectionScope.LocalMachine));}
 internal static string Q(string name){return "`"+name.Replace("`","``")+"`";}
 static DbConnection Open(string connection,bool admin=false){ServerStorage.UseProviderDirectory(Path.Combine(Path.GetDirectoryName(typeof(RelocationEngine).Assembly.Location),"DatabaseProvider"));var b=new DbConnectionStringBuilder{ConnectionString=connection};if(admin)b.Remove("database");b["Pooling"]=false;var type=Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(RelocationEngine).Assembly.Location),"DatabaseProvider","MySqlConnector.dll")).GetType("MySqlConnector.MySqlConnection",true);var db=(DbConnection)Activator.CreateInstance(type);db.ConnectionString=b.ConnectionString;try{db.Open();using(var c=db.CreateCommand()){c.CommandText="SET time_zone='+00:00'";c.ExecuteNonQuery();}return db;}catch{db.Dispose();throw;}}
 static DbCommand Cmd(DbConnection db,DbTransaction tx,string sql,params object[] args){var c=db.CreateCommand();c.Transaction=tx;c.CommandText=sql;c.CommandTimeout=600;for(int i=0;i<args.Length;i++){var p=c.CreateParameter();p.ParameterName="@p"+i;p.Value=args[i]??DBNull.Value;c.Parameters.Add(p);}return c;}
 static object Scalar(DbConnection db,string sql,params object[] args){using(var c=Cmd(db,null,sql,args))return c.ExecuteScalar();}
 static void Exec(DbConnection db,DbTransaction tx,string sql,params object[] args){using(var c=Cmd(db,tx,sql,args))c.ExecuteNonQuery();}
 static string DbName(string c){var b=new DbConnectionStringBuilder{ConnectionString=c};return Convert.ToString(b["Database"]);}
 static List<string> Tables(DbConnection db){var r=new List<string>();using(var c=Cmd(db,null,"SHOW FULL TABLES"))using(var rows=c.ExecuteReader()){while(rows.Read()){if(Convert.ToString(rows[1])!="BASE TABLE")throw new InvalidOperationException("检测到视图；当前工具拒绝不完整迁移，请使用 DBA 完整逻辑备份流程。");r.Add(rows.GetString(0));}}return r.OrderBy(x=>x,StringComparer.Ordinal).ToList();}
 static void Objects(DbConnection db){string database=Convert.ToString(Scalar(db,"SELECT DATABASE()"));foreach(string table in new[]{"TRIGGERS","ROUTINES","EVENTS"}){string field=table=="TRIGGERS"?"TRIGGER_SCHEMA":table=="ROUTINES"?"ROUTINE_SCHEMA":"EVENT_SCHEMA";if(Convert.ToInt64(Scalar(db,"SELECT COUNT(*) FROM information_schema."+table+" WHERE "+field+"=@p0",database))>0)throw new InvalidOperationException("检测到 "+table+"；为避免遗漏，工具已拒绝执行，请使用 DBA 完整逻辑备份。");}}
 static void TargetAbsent(DbConnection target,string database){if(Convert.ToInt64(Scalar(target,"SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME=@p0",database))!=0)throw new IOException("目标库 "+database+" 已存在。为避免覆盖数据，本工具拒绝继续；请先核查该库。");}
 static long RequiredPacket(DbConnection source){long max=0;foreach(string t in Tables(source)){string[] columns;using(var c=Cmd(source,null,"SELECT * FROM "+Q(t)+" LIMIT 0"))using(var r=c.ExecuteReader())columns=Enumerable.Range(0,r.FieldCount).Select(i=>r.GetName(i)).ToArray();object v=Scalar(source,"SELECT MAX("+string.Join("+",columns.Select(c=>"COALESCE(OCTET_LENGTH("+Q(c)+"),0)"))+") FROM "+Q(t));if(v!=DBNull.Value)max=Math.Max(max,Convert.ToInt64(v));}long need=Math.Max(max+1024*1024,Convert.ToInt64(Scalar(source,"SELECT @@max_allowed_packet"))),rounded=1024*1024;while(rounded<need&&rounded<1073741824L)rounded*=2;if(need>1073741824L)throw new IOException("源记录超过 MySQL 单包硬限制，拒绝迁移。");return rounded;}
 public static void PreparePacketLimit(string source,string target,bool allow,Action<string> log){using(var a=Open(source))using(var b=Open(target,true)){TargetAbsent(b,DbName(target));long need=RequiredPacket(a),packet=Convert.ToInt64(Scalar(b,"SELECT @@max_allowed_packet"));if(packet>=need)return;if(!allow)throw new IOException("目标 max_allowed_packet="+packet+"，当前数据至少建议 "+need+" 字节。可勾选允许提高目标数据包限制，或由 DBA 调整后重试；源数据未改动。");Exec(b,null,"SET GLOBAL max_allowed_packet="+need.ToString(CultureInfo.InvariantCulture));log("已将目标实例全局 max_allowed_packet 提高到 "+need+"（新连接生效）。请 DBA 持久化该值，避免目标 MySQL 重启后恢复旧限制。");}}
 public static void Preflight(string source,string target,Action<string> log){if(string.Equals(source,target,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("源和目标连接不能相同。");using(var a=Open(source))using(var b=Open(target,true)){var tables=Tables(a);if(!tables.Contains("uda_documents")||!tables.Contains("uda_schema"))throw new IOException("源库未包含 UDA 必需表。");if(Convert.ToInt64(Scalar(b,"SELECT @@max_allowed_packet"))<RequiredPacket(a))throw new IOException("目标数据包限制不足，尚未创建数据库或停服。");Objects(a);if(Convert.ToInt64(Scalar(a,"SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND (EXTRA LIKE '%VIRTUAL GENERATED%' OR EXTRA LIKE '%STORED GENERATED%')"))>0)throw new IOException("检测到生成列，已拒绝不完整迁移。");TargetAbsent(b,DbName(target));foreach(string t in tables){string engine=Convert.ToString(Scalar(a,"SELECT ENGINE FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@p0",t));if(engine!="InnoDB")throw new IOException("不支持非 InnoDB 表："+t);log("表 "+t+"："+Scalar(a,"SELECT COUNT(*) FROM "+Q(t))+" 行");}log("源 MySQL "+Scalar(a,"SELECT VERSION()")+"；目标 MySQL "+Scalar(b,"SELECT VERSION()"));log("连接、表结构与目标库存在性检查通过；执行时仍需 LOCK TABLES、CREATE 和写入权限。");}}
 internal static string Digest(DbDataReader rows,Action<object[]> row,CancellationToken token,out long count){byte[] total=new byte[32];count=0;while(rows.Read()){token.ThrowIfCancellationRequested();var values=new object[rows.FieldCount];rows.GetValues(values);using(var m=new MemoryStream()){using(var w=new BinaryWriter(m,Encoding.UTF8,true)){foreach(object v in values){if(v==DBNull.Value){w.Write((byte)0);continue;}var bytes=v as byte[];if(bytes!=null){w.Write((byte)1);w.Write(bytes.Length);w.Write(bytes);}else{w.Write((byte)2);string text=v is DateTime?((DateTime)v).ToString("yyyy-MM-dd HH:mm:ss.ffffff",CultureInfo.InvariantCulture):v is decimal?((decimal)v).ToString("G29",CultureInfo.InvariantCulture):Convert.ToString(v,CultureInfo.InvariantCulture);w.Write(text);}}}byte[] h;using(var sha=SHA256.Create())h=sha.ComputeHash(m.ToArray());int carry=0;for(int i=31;i>=0;i--){int n=total[i]+h[i]+carry;total[i]=(byte)n;carry=n>>8;}}if(row!=null)row(values);count++;}return count+":"+BitConverter.ToString(total).Replace("-","");}
 static List<string> PrimaryKeys(DbConnection db,string table){var list=new List<string>();using(var c=Cmd(db,null,"SHOW KEYS FROM "+Q(table)+" WHERE Key_name='PRIMARY'"))using(var r=c.ExecuteReader())while(r.Read())list.Add(Convert.ToString(r["Column_name"]));return list;}
 static void Insert(DbConnection db,DbTransaction tx,string table,string[] columns,List<string> keys,object[] values,CancellationToken token){token.ThrowIfCancellationRequested();Exec(db,tx,"INSERT INTO "+Q(table)+" ("+string.Join(",",columns.Select(Q))+") VALUES ("+string.Join(",",columns.Select((n,i)=>"@p"+i))+")",values);}
 public static void Copy(string source,string target,CancellationToken token,Action<string> log){
  token.ThrowIfCancellationRequested();Preflight(source,target,log);string database=DbName(target);
  using(var a=Open(source))using(var admin=Open(target,true)){
   var tables=Tables(a);var creates=new Dictionary<string,string>();var keys=new Dictionary<string,List<string>>();
   Exec(a,null,"LOCK TABLES "+string.Join(",",tables.Select(t=>Q(t)+" READ")));
   try{
    foreach(string t in tables){using(var c=Cmd(a,null,"SHOW CREATE TABLE "+Q(t)))using(var r=c.ExecuteReader()){r.Read();creates[t]=r.GetString(1);}keys[t]=PrimaryKeys(a,t);if(System.Text.RegularExpressions.Regex.IsMatch(creates[t],@"REFERENCES\s+`[^`]+`\s*\.",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new IOException("检测到跨库外键，拒绝迁移："+t);}
    TargetAbsent(admin,database);Exec(admin,null,"CREATE DATABASE "+Q(database)+" CHARACTER SET utf8mb4 COLLATE utf8mb4_bin");log("目标库已创建："+database);
    using(var b=Open(target)){Exec(b,null,"SET FOREIGN_KEY_CHECKS=0");foreach(string t in tables){token.ThrowIfCancellationRequested();Exec(b,null,creates[t]);}
     using(var tx=b.BeginTransaction()){
      foreach(string t in tables){token.ThrowIfCancellationRequested();string[] cols;using(var c=Cmd(a,null,"SELECT * FROM "+Q(t)+" LIMIT 0"))using(var r=c.ExecuteReader())cols=Enumerable.Range(0,r.FieldCount).Select(i=>r.GetName(i)).ToArray();long count;string original;
       using(var c=Cmd(a,null,"SELECT * FROM "+Q(t)))using(var r=c.ExecuteReader()){long written=0;original=Digest(r,v=>{Insert(b,tx,t,cols,keys[t],v,token);written++;if(written%100==0)log(t+"：已复制 "+written+" 行");},token,out count);}
       long verified;string copied;using(var c=Cmd(b,tx,"SELECT * FROM "+Q(t)))using(var r=c.ExecuteReader())copied=Digest(r,null,token,out verified);
       if(original!=copied)throw new IOException("完整校验失败："+t+"，未切换服务连接。");log("校验通过 "+t+"："+count+" 行，全部列内容摘要一致。");
      }
      token.ThrowIfCancellationRequested();tx.Commit();
     }Exec(b,null,"SET FOREIGN_KEY_CHECKS=1");
    }
   }finally{Exec(a,null,"UNLOCK TABLES");}
  }
 }
 public static void Relocate(string root,string source,string target,CancellationToken token,Action<string> log,string serviceUrl){
  string command;using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\UdaLanDriverService"))command=key==null?"":Convert.ToString(key.GetValue("ImagePath",""));
  var match=System.Text.RegularExpressions.Regex.Match(command,"(?:^|\\s)--repo\\s+(?:\"([^\"]*)\"|(\\S+))",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
  string registered=match.Success?Path.GetFullPath(match.Groups[1].Success?match.Groups[1].Value:match.Groups[2].Value):"";
  if(!string.Equals(registered.TrimEnd('\\','/'),Path.GetFullPath(root).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase))throw new IOException("所选仓库与正式 UDA 服务的仓库参数不一致，已拒绝切换。");
  var cfg=ServerStorage.Config(root);if(!cfg.Enabled||!cfg.DatabaseOnly)throw new IOException("正式源仓库必须已启用 MySQL 数据库主存储。");
  var old=new DbConnectionStringBuilder{ConnectionString=Unprotect(cfg)};var requested=new DbConnectionStringBuilder{ConnectionString=source};
  if(!string.Equals(Convert.ToString(old["Server"]),Convert.ToString(requested["Server"]),StringComparison.OrdinalIgnoreCase)||!string.Equals(DbName(Unprotect(cfg)),DbName(source),StringComparison.Ordinal)||Convert.ToInt32(old.ContainsKey("Port")?old["Port"]:3306)!=Convert.ToInt32(requested.ContainsKey("Port")?requested["Port"]:3306))throw new IOException("源连接与该仓库当前配置不一致，请重新读取源配置。");
  Preflight(source,target,log);bool switched=false;using(var service=new ServiceController("UdaLanDriverService")){
   service.Refresh();bool restart=service.Status!=ServiceControllerStatus.Stopped;
   try{
    if(restart){log("停止本机 UDA 服务…");service.Stop();service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromMinutes(2));}
    ServerStorage.UseProviderDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"DatabaseProvider"));ServerStorage.Sync(root);token.ThrowIfCancellationRequested();
    Copy(source,target,token,log);token.ThrowIfCancellationRequested();
    string path=Path.Combine(root,".uda-storage.json"),backup=path+".before-relocation-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".bak";
    File.Copy(path,backup,false);ServerStorage.SecureFile(backup);
    cfg.ProtectedConnection=Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(target),null,System.Security.Cryptography.DataProtectionScope.LocalMachine));
    string tmp=path+".relocation-"+Guid.NewGuid().ToString("N");File.WriteAllText(tmp,new JavaScriptSerializer().Serialize(cfg),new UTF8Encoding(false));ServerStorage.SecureFile(tmp);File.Replace(tmp,path,null);switched=true;
    log("已保存加密连接，RepositoryId 和数据库主存储设置保持不变。源库未删除。");
   }catch(Exception ex){log("迁移阶段失败："+ex.Message);throw;}finally{
    service.Refresh();if(restart&&service.Status==ServiceControllerStatus.Stopped){log(switched?"启动已切换的 UDA 服务…":"恢复使用原数据库的 UDA 服务…");service.Start();service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromMinutes(2));}
   }
  }
  for(int retry=0;retry<60;retry++){
   try{var health=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(MigrationHttp.Send(serviceUrl,"/api/health","GET",null,null,true));if(health.ContainsKey("RepoLoaded")&&Convert.ToBoolean(health["RepoLoaded"])){log("服务健康检查通过：仓库已加载。");return;}}catch{}
   if(retry%10==0)log("已切换目标连接，等待服务仓库就绪…");Thread.Sleep(1000);
  }
  throw new IOException("数据已校验并切换，但服务仓库未就绪。已保留目标连接，源库未删除；请检查服务日志，不要盲目回滚连接。");
 }
}
}
