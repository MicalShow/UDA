using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace UDA {
public sealed class ConfigEditorForm : Form {
  readonly TabControl tabs=new TabControl(); readonly ToolStrip tools=new ToolStrip(); readonly StatusStrip status=new StatusStrip();
  readonly ToolStripStatusLabel fileLabel=new ToolStripStatusLabel(), stateLabel=new ToolStripStatusLabel();
  readonly TextBox logDir=new TextBox(), reportDir=new TextBox(), excludeIds=new TextBox();
  readonly NumericUpDown timeout=new NumericUpDown(), countdown=new NumericUpDown(), rebootDelay=new NumericUpDown();
  readonly CheckBox autoReboot=CB("安装完成后自动重启 / Auto reboot"), restore=CB("安装前创建系统还原点 / Create restore point"),
    scanMissing=CB("机型包不存在时扫描所有驱动源 / Scan all sources when model package is missing"),
    scanAfter=CB("安装机型包后继续扫描所有驱动源 / Continue scanning after model package"),
    disconnected=CB("包含当前未连接的设备 / Include disconnected devices"), onlyProblems=CB("运维模式仅显示缺失或异常设备 / Only missing/problem devices in maintenance");
  readonly DataGridView sources=Grid(), mappings=Grid(), categories=Grid(); readonly CheckedListBox included=new CheckedListBox(); readonly SplitContainer filtersSplit=new SplitContainer();
  string currentPath; bool dirty, loading; float testDpiScale;

  public ConfigEditorForm(string initialPath){
    Text="万能驱动助手 - 配置文件生成器"; StartPosition=FormStartPosition.CenterScreen; MinimumSize=new Size(480,360); Size=new Size(1120,760);
    AutoScaleMode=AutoScaleMode.Dpi; Font=new Font("Microsoft YaHei UI",9F); KeyPreview=true;
    BuildToolbar(); BuildGeneral(); BuildSources(); BuildMappings(); BuildCategories(); BuildFilters();
    tabs.Dock=DockStyle.Fill; status.Items.Add(fileLabel); status.Items.Add(new ToolStripStatusLabel(){Spring=true}); status.Items.Add(stateLabel);
    Controls.Add(tabs); Controls.Add(status); Controls.Add(tools); tools.Dock=DockStyle.Top; status.Dock=DockStyle.Bottom;
    FormClosing+=OnClosing; Resize+=delegate{ReflowFilters();}; KeyDown+=delegate(object s,KeyEventArgs e){if(e.Control&&e.KeyCode==Keys.S){Save(false);e.SuppressKeyPress=true;}};
    if(!String.IsNullOrWhiteSpace(initialPath)&&File.Exists(initialPath)) LoadFile(initialPath); else NewConfig();
  }
  static CheckBox CB(string text){return new CheckBox(){Text=text,AutoSize=false,AutoEllipsis=true,Height=32,Margin=new Padding(3,2,3,2)};}
  static DataGridView Grid(){return new DataGridView(){Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AllowUserToResizeRows=false,
    RowHeadersVisible=false,AutoGenerateColumns=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,BackgroundColor=Color.White,
    BorderStyle=BorderStyle.FixedSingle,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.None};}
  static DataGridViewTextBoxColumn TCol(string name,string title,int width){return new DataGridViewTextBoxColumn(){Name=name,HeaderText=title,Width=width,SortMode=DataGridViewColumnSortMode.NotSortable};}
  static DataGridViewCheckBoxColumn BCol(string name,string title,int width){return new DataGridViewCheckBoxColumn(){Name=name,HeaderText=title,Width=width,SortMode=DataGridViewColumnSortMode.NotSortable};}
  void BuildToolbar(){
    AddTool("新建",delegate{if(ConfirmDiscard())NewConfig();}); AddTool("打开...",delegate{if(ConfirmDiscard())Open();}); AddTool("保存",delegate{Save(false);}); AddTool("另存为...",delegate{Save(true);});
    tools.Items.Add(new ToolStripSeparator()); AddTool("检查配置",delegate{ValidateAndShow();}); AddTool("使用说明",delegate{ShowHelp();});
    tools.GripStyle=ToolStripGripStyle.Hidden; tools.ImageScalingSize=new Size(24,24); tools.Padding=new Padding(8,4,8,4);
  }
  void AddTool(string text,EventHandler click){var b=new ToolStripButton(text){DisplayStyle=ToolStripItemDisplayStyle.Text,AutoSize=true,Margin=new Padding(2)};b.Click+=click;tools.Items.Add(b);}
  TabPage Page(string title){var p=new TabPage(title){Padding=new Padding(12),AutoScroll=true};tabs.TabPages.Add(p);return p;}
  static Label Lab(string text){return new Label(){Text=text,AutoSize=true,Anchor=AnchorStyles.Left,Margin=new Padding(3,8,10,8)};}
  static void Row(TableLayoutPanel t,int row,string label,Control c){t.RowStyles.Add(new RowStyle(SizeType.AutoSize));t.Controls.Add(Lab(label),0,row);c.Anchor=AnchorStyles.Left|AnchorStyles.Right;c.Margin=new Padding(3,5,3,5);t.Controls.Add(c,1,row);}
  void BuildGeneral(){
    var p=Page("常规设置");var t=new TableLayoutPanel(){Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Padding=new Padding(8)};t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
    Row(t,0,"日志目录 / Log directory",logDir);Row(t,1,"报告目录 / Report directory",reportDir);
    PrepNum(timeout,10,3600);PrepNum(countdown,0,3600);PrepNum(rebootDelay,0,3600);Row(t,2,"单个驱动安装超时（秒）",timeout);Row(t,3,"SCCM 扫描后倒计时（秒）",countdown);Row(t,4,"自动重启延迟（秒）",rebootDelay);
    var flow=new FlowLayoutPanel(){FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoSize=true,Dock=DockStyle.Fill};foreach(var x in new[]{autoReboot,restore,scanMissing,scanAfter,disconnected})flow.Controls.Add(x);flow.SizeChanged+=delegate{foreach(Control x in flow.Controls)x.Width=Math.Max(120,flow.ClientSize.Width-8);};t.RowStyles.Add(new RowStyle(SizeType.AutoSize));t.Controls.Add(flow,1,5);p.Controls.Add(t);
    Wire(t);
  }
  static void PrepNum(NumericUpDown n,int min,int max){n.Minimum=min;n.Maximum=max;n.Width=130;n.ThousandsSeparator=true;}
  void BuildSources(){
    var p=Page("驱动源");sources.Columns.AddRange(BCol("Enabled","启用",55),TCol("Priority","优先级",65),TCol("Name","名称",150),TCol("Path","SMB/本地路径",310),TCol("Username","用户名",150),TCol("Password","密码",130));
    sources.Columns[3].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill; ((DataGridViewTextBoxColumn)sources.Columns[5]).DefaultCellStyle.NullValue="";
    var bar=ButtonBar(new[]{"添加","删除","上移","下移","测试选中路径"},new EventHandler[]{delegate{sources.Rows.Add(true,100,"新驱动源","\\\\server\\Drivers$","","");MarkDirty();},delegate{RemoveRow(sources);},delegate{MoveRow(sources,-1);},delegate{MoveRow(sources,1);},delegate{TestSource();}});
    var note=Lab("建议：SCCM 部署使用计算机账户/任务序列账户的共享与 NTFS 只读权限；密码会以明文写入 JSON，仅建议运维模式临时使用。");note.ForeColor=Color.FromArgb(160,90,0);note.Dock=DockStyle.Top;
    p.Controls.Add(sources);p.Controls.Add(note);p.Controls.Add(bar);sources.CellValueChanged+=delegate{MarkDirty();};sources.RowsAdded+=delegate{MarkDirty();};
  }
  void BuildMappings(){
    var p=Page("机型映射");mappings.Columns.AddRange(BCol("Enabled","启用",55),TCol("Priority","优先级",65),TCol("Name","名称",170),TCol("Manufacturer","厂商匹配",130),TCol("Model","型号匹配",150),TCol("SKU","SKU 匹配",130),TCol("Path","驱动相对路径",240));mappings.Columns[6].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;
    var bar=ButtonBar(new[]{"添加","复制","删除","上移","下移","测试选中映射"},new EventHandler[]{delegate{mappings.Rows.Add(true,100,"新机型","*","*","*","{Manufacturer}\\{Model}");MarkDirty();},delegate{DuplicateRow(mappings);},delegate{RemoveRow(mappings);},delegate{MoveRow(mappings,-1);},delegate{MoveRow(mappings,1);},delegate{TestMapping();}});
    var note=Lab("匹配支持 * 和 ?；不带通配符时支持精确或包含匹配。优先级数字越小越先匹配；路径可使用 {Manufacturer}、{Model}、{SystemSKU}。");note.Dock=DockStyle.Top;p.Controls.Add(mappings);p.Controls.Add(note);p.Controls.Add(bar);mappings.CellValueChanged+=delegate{MarkDirty();};
  }
  void BuildCategories(){
    var p=Page("驱动分类");categories.Columns.AddRange(TCol("Name","分类名称",190),TCol("Classes","PnP 类名（逗号分隔）",600));categories.Columns[1].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;
    var bar=ButtonBar(new[]{"添加分类","删除分类","恢复默认"},new EventHandler[]{delegate{categories.Rows.Add("NewCategory","");MarkDirty();},delegate{RemoveRow(categories);},delegate{if(MessageBox.Show("确定恢复默认分类？","确认",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes){FillDefaultCategories();MarkDirty();}}});
    var note=Lab("这里填写 Windows PnP Class，例如 Net、Display、MEDIA、SCSIAdapter。分类名称会出现在运维版筛选器中。");note.Dock=DockStyle.Top;p.Controls.Add(categories);p.Controls.Add(note);p.Controls.Add(bar);categories.CellValueChanged+=delegate{RefreshCategoryChecks();MarkDirty();};
  }
  void BuildFilters(){
    var p=Page("过滤规则");var split=filtersSplit;split.Dock=DockStyle.Fill;split.Orientation=Orientation.Vertical;
    var left=new GroupBox(){Text="仅包含以下分类（不勾选表示全部）",Dock=DockStyle.Fill,Padding=new Padding(10)};included.Dock=DockStyle.Fill;included.CheckOnClick=true;left.Controls.Add(included);
    var right=new TableLayoutPanel(){Dock=DockStyle.Fill,RowCount=3,ColumnCount=1,Padding=Padding.Empty};right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));right.RowStyles.Add(new RowStyle(SizeType.AutoSize));right.RowStyles.Add(new RowStyle(SizeType.Percent,100));right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    right.Controls.Add(Lab("排除的硬件 ID（每行一个，支持 * 和 ?）"),0,0);excludeIds.Multiline=true;excludeIds.ScrollBars=ScrollBars.Both;excludeIds.AcceptsReturn=true;excludeIds.WordWrap=false;excludeIds.Dock=DockStyle.Fill;excludeIds.Margin=Padding.Empty;right.Controls.Add(excludeIds,0,1);onlyProblems.Dock=DockStyle.Fill;onlyProblems.Margin=Padding.Empty;right.Controls.Add(onlyProblems,0,2);
    split.Panel1.Controls.Add(left);split.Panel2.Controls.Add(right);p.Controls.Add(split);included.ItemCheck+=delegate{BeginInvoke((MethodInvoker)MarkDirty);};Wire(right);
  }
  void ReflowFilters(){if(filtersSplit.IsDisposed||filtersSplit.Width<1||filtersSplit.Height<1)return;try{float scale=testDpiScale; if(scale<1F)using(var g=CreateGraphics())scale=Math.Max(1F,g.DpiX/96F);int logicalWidth=(int)(ClientSize.Width/scale);if(logicalWidth<760){if(filtersSplit.Orientation!=Orientation.Horizontal)filtersSplit.Orientation=Orientation.Horizontal;filtersSplit.SplitterDistance=Math.Max((int)(120*scale),filtersSplit.Height/2);}else{if(filtersSplit.Orientation!=Orientation.Vertical)filtersSplit.Orientation=Orientation.Vertical;filtersSplit.SplitterDistance=Math.Max((int)(240*scale),Math.Min((int)(380*scale),filtersSplit.Width/3));}}catch(InvalidOperationException){}}
  static FlowLayoutPanel ButtonBar(string[] names,EventHandler[] handlers){var f=new FlowLayoutPanel(){Dock=DockStyle.Top,Height=45,Padding=new Padding(0,4,0,4),WrapContents=false,AutoScroll=true};for(int i=0;i<names.Length;i++){var b=new Button(){Text=names[i],AutoSize=true,Height=30,Margin=new Padding(3)};b.Click+=handlers[i];f.Controls.Add(b);}return f;}
  void Wire(Control root){foreach(Control c in root.Controls){var tb=c as TextBox;if(tb!=null)tb.TextChanged+=delegate{MarkDirty();};var nu=c as NumericUpDown;if(nu!=null)nu.ValueChanged+=delegate{MarkDirty();};var cb=c as CheckBox;if(cb!=null)cb.CheckedChanged+=delegate{MarkDirty();};if(c.HasChildren)Wire(c);}}
  void MarkDirty(){if(loading)return;dirty=true;stateLabel.Text="● 未保存";stateLabel.ForeColor=Color.Firebrick;}

  void NewConfig(){loading=true;currentPath=null;Apply(new C());FillDefaultCategories();loading=false;dirty=false;UpdateStatus("新配置");}
  void FillDefaultCategories(){categories.Rows.Clear();var d=Defaults();foreach(var x in d)categories.Rows.Add(x.Key,String.Join(", ",x.Value));RefreshCategoryChecks();}
  static Dictionary<string,string[]> Defaults(){return new Dictionary<string,string[]>(StringComparer.OrdinalIgnoreCase){{"Network",new[]{"Net","Network","NIC"}},{"Display",new[]{"Display","Video","GPU"}},{"Audio",new[]{"MEDIA","AudioEndpoint","Sound"}},{"Storage",new[]{"SCSIAdapter","HDC","Storage","DiskDrive"}},{"Chipset",new[]{"System","Firmware","SecurityDevices"}},{"Bluetooth",new[]{"Bluetooth"}},{"USB",new[]{"USB"}},{"Other",new string[0]}};}
  void Open(){using(var d=new OpenFileDialog(){Filter="JSON 配置文件 (*.json)|*.json|所有文件 (*.*)|*.*",Title="打开驱动助手配置"})if(d.ShowDialog(this)==DialogResult.OK)LoadFile(d.FileName);}
  void LoadFile(string p){try{loading=true;Apply(C.Load(p));currentPath=Path.GetFullPath(p);dirty=false;UpdateStatus("已加载");}catch(Exception e){MessageBox.Show(this,"无法打开配置文件：\r\n"+e.Message,"打开失败",MessageBoxButtons.OK,MessageBoxIcon.Error);}finally{loading=false;}}
  void Apply(C c){
    logDir.Text=c.General.LogDirectory??"";reportDir.Text=c.General.ReportDirectory??"";SetNum(timeout,c.General.InstallTimeoutSeconds);SetNum(countdown,c.General.DeploymentCountdownSeconds);SetNum(rebootDelay,c.General.RebootDelaySeconds);
    autoReboot.Checked=c.General.AutoReboot;restore.Checked=c.General.CreateRestorePoint;scanMissing.Checked=c.General.ScanAllSourcesWhenModelPackageMissing;scanAfter.Checked=c.General.ScanAllSourcesAfterModelPackage;disconnected.Checked=c.General.IncludeDisconnectedDevices;
    sources.Rows.Clear();foreach(var x in c.Sources)sources.Rows.Add(x.Enabled,x.Priority,x.Name,x.Path,x.Username,x.Password);
    mappings.Rows.Clear();foreach(var x in c.ModelMappings)mappings.Rows.Add(x.Enabled,x.Priority,x.Name,x.ManufacturerPattern,x.ModelPattern,x.SystemSKUPattern,x.DriverPath);
    categories.Rows.Clear();foreach(var x in c.Categories.OrderBy(x=>x.Key,StringComparer.OrdinalIgnoreCase))categories.Rows.Add(x.Key,String.Join(", ",x.Value??new string[0]));if(categories.Rows.Count==0)FillDefaultCategories();
    RefreshCategoryChecks();var inc=new HashSet<string>(c.Filters.IncludeCategories??new string[0],StringComparer.OrdinalIgnoreCase);for(int i=0;i<included.Items.Count;i++)included.SetItemChecked(i,inc.Contains(included.Items[i].ToString()));
    excludeIds.Text=String.Join(Environment.NewLine,c.Filters.ExcludeHardwareIds??new string[0]);onlyProblems.Checked=c.Filters.OnlyMissingOrProblemDevicesInMaintenance;
  }
  static void SetNum(NumericUpDown n,int value){n.Value=Math.Max(n.Minimum,Math.Min(n.Maximum,value));}
  C Collect(){
    sources.EndEdit();mappings.EndEdit();categories.EndEdit();var c=new C();c.General.LogDirectory=logDir.Text.Trim();c.General.ReportDirectory=reportDir.Text.Trim();c.General.InstallTimeoutSeconds=(int)timeout.Value;c.General.DeploymentCountdownSeconds=(int)countdown.Value;c.General.RebootDelaySeconds=(int)rebootDelay.Value;c.General.AutoReboot=autoReboot.Checked;c.General.CreateRestorePoint=restore.Checked;c.General.ScanAllSourcesWhenModelPackageMissing=scanMissing.Checked;c.General.ScanAllSourcesAfterModelPackage=scanAfter.Checked;c.General.IncludeDisconnectedDevices=disconnected.Checked;
    c.Sources.Clear();foreach(DataGridViewRow r in sources.Rows)c.Sources.Add(new S(){Enabled=Bool(r,0),Priority=Int(r,1,100),Name=Str(r,2),Path=Str(r,3),Username=Str(r,4),Password=Str(r,5)});
    c.ModelMappings.Clear();foreach(DataGridViewRow r in mappings.Rows)c.ModelMappings.Add(new M(){Enabled=Bool(r,0),Priority=Int(r,1,100),Name=Str(r,2),ManufacturerPattern=Star(Str(r,3)),ModelPattern=Star(Str(r,4)),SystemSKUPattern=Star(Str(r,5)),DriverPath=Str(r,6)});
    c.Categories.Clear();foreach(DataGridViewRow r in categories.Rows){string n=Str(r,0);if(n.Length>0)c.Categories[n]=Split(Str(r,1),false);}
    var list=new List<string>();foreach(var x in included.CheckedItems)list.Add(x.ToString());c.Filters.IncludeCategories=list.ToArray();c.Filters.ExcludeHardwareIds=Split(excludeIds.Text,true);c.Filters.OnlyMissingOrProblemDevicesInMaintenance=onlyProblems.Checked;return c;
  }
  static string Str(DataGridViewRow r,int i){return Convert.ToString(r.Cells[i].Value).Trim();} static bool Bool(DataGridViewRow r,int i){return Convert.ToBoolean(r.Cells[i].Value??false);} static int Int(DataGridViewRow r,int i,int d){int x;return Int32.TryParse(Str(r,i),out x)?x:d;}static string Star(string s){return s.Length==0?"*":s;}
  static string[] Split(string s,bool lines){return s.Split(lines?new[]{'\r','\n'}:new[]{',',';','\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Trim()).Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();}
  List<string> ValidateConfig(C c){var e=new List<string>();if(String.IsNullOrWhiteSpace(c.General.LogDirectory))e.Add("日志目录不能为空。");if(String.IsNullOrWhiteSpace(c.General.ReportDirectory))e.Add("报告目录不能为空。");if(!c.Sources.Any(x=>x.Enabled))e.Add("至少需要启用一个驱动源。");
    for(int i=0;i<c.Sources.Count;i++){var x=c.Sources[i];if(String.IsNullOrWhiteSpace(x.Name))e.Add("驱动源第 "+(i+1)+" 行缺少名称。");if(x.Enabled&&String.IsNullOrWhiteSpace(x.Path))e.Add("驱动源“"+x.Name+"”缺少路径。");}
    if(c.ModelMappings.Count>0&&!c.ModelMappings.Any(x=>x.Enabled))e.Add("已配置机型映射，但没有启用任何一条。请勾选“启用”，或删除不需要的映射。");
    for(int i=0;i<c.ModelMappings.Count;i++){var x=c.ModelMappings[i];if(x.Enabled&&String.IsNullOrWhiteSpace(x.DriverPath))e.Add("机型映射“"+x.Name+"”缺少驱动路径。");}
    var dup=c.Categories.Keys.GroupBy(x=>x,StringComparer.OrdinalIgnoreCase).FirstOrDefault(x=>x.Count()>1);if(dup!=null)e.Add("分类名称重复："+dup.Key);foreach(var x in c.Filters.IncludeCategories)if(!c.Categories.ContainsKey(x))e.Add("过滤规则引用了不存在的分类："+x);return e;}
  bool ValidateAndShow(){var e=ValidateConfig(Collect());if(e.Count==0){MessageBox.Show(this,"配置检查通过，可以直接用于万能驱动助手。","检查完成",MessageBoxButtons.OK,MessageBoxIcon.Information);return true;}MessageBox.Show(this,"请修正以下问题：\r\n\r\n• "+String.Join("\r\n• ",e),"配置有误",MessageBoxButtons.OK,MessageBoxIcon.Warning);return false;}
  void Save(bool saveAs){var c=Collect();var e=ValidateConfig(c);if(e.Count>0){MessageBox.Show(this,"保存前请修正：\r\n\r\n• "+String.Join("\r\n• ",e),"无法保存",MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}string p=currentPath;if(saveAs||String.IsNullOrWhiteSpace(p)){using(var d=new SaveFileDialog(){Filter="JSON 配置文件 (*.json)|*.json",FileName="DriverConfig.json",Title="保存驱动助手配置",OverwritePrompt=true}){if(d.ShowDialog(this)!=DialogResult.OK)return;p=d.FileName;}}
    try{File.WriteAllText(p,Pretty(new JavaScriptSerializer().Serialize(c)),new UTF8Encoding(false));currentPath=Path.GetFullPath(p);dirty=false;UpdateStatus("保存成功");}catch(Exception ex){MessageBox.Show(this,"保存失败：\r\n"+ex.Message,"错误",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
  public static string Pretty(string json){var b=new StringBuilder();bool str=false,esc=false;int indent=0;for(int i=0;i<json.Length;i++){char ch=json[i];if(str){b.Append(ch);if(esc)esc=false;else if(ch=='\\')esc=true;else if(ch=='\"')str=false;continue;}if(ch=='\"'){str=true;b.Append(ch);}else if(ch=='{'||ch=='['){b.Append(ch).AppendLine();indent++;Pad(b,indent);}else if(ch=='}'||ch==']'){b.AppendLine();indent--;Pad(b,indent);b.Append(ch);}else if(ch==','){b.Append(ch).AppendLine();Pad(b,indent);}else if(ch==':')b.Append(": ");else if(!Char.IsWhiteSpace(ch))b.Append(ch);}return b.ToString();}static void Pad(StringBuilder b,int n){b.Append(' ',n*2);}
  void RefreshCategoryChecks(){var old=new HashSet<string>(included.CheckedItems.Cast<object>().Select(x=>x.ToString()),StringComparer.OrdinalIgnoreCase);included.Items.Clear();foreach(DataGridViewRow r in categories.Rows){string n=Str(r,0);if(n.Length>0)included.Items.Add(n,old.Contains(n));}}
  void UpdateStatus(string state){fileLabel.Text="文件："+(currentPath??"（尚未保存）");stateLabel.Text=state;stateLabel.ForeColor=Color.DarkGreen;}
  bool ConfirmDiscard(){if(!dirty)return true;return MessageBox.Show(this,"当前修改尚未保存，确定放弃吗？","未保存的修改",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)==DialogResult.Yes;}
  void OnClosing(object s,FormClosingEventArgs e){if(!ConfirmDiscard())e.Cancel=true;}
  static void RemoveRow(DataGridView g){if(g.CurrentRow!=null&&!g.CurrentRow.IsNewRow)g.Rows.Remove(g.CurrentRow);}
  static void DuplicateRow(DataGridView g){if(g.CurrentRow==null)return;var a=new object[g.ColumnCount];for(int i=0;i<a.Length;i++)a[i]=g.CurrentRow.Cells[i].Value;g.Rows.Insert(g.CurrentRow.Index+1,a);}
  static void MoveRow(DataGridView g,int direction){if(g.CurrentRow==null)return;int from=g.CurrentRow.Index,to=from+direction;if(to<0||to>=g.Rows.Count)return;var row=g.Rows[from];g.Rows.RemoveAt(from);g.Rows.Insert(to,row);g.CurrentCell=g.Rows[to].Cells[Math.Max(0,g.CurrentCell==null?0:g.CurrentCell.ColumnIndex)];}
  void TestSource(){if(sources.CurrentRow==null){MessageBox.Show(this,"请先选择一个驱动源。","提示");return;}var r=sources.CurrentRow;var s=new S(){Enabled=true,Priority=Int(r,1,100),Name=Str(r,2),Path=Str(r,3),Username=Str(r,4),Password=Str(r,5)};if(String.IsNullOrWhiteSpace(s.Path)){MessageBox.Show(this,"路径不能为空。","测试失败",MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
    Cursor=Cursors.WaitCursor;try{string p=ResolvePath(s.Path);using(var n=new Net()){n.Add(s,false);if(!Directory.Exists(p))throw new DirectoryNotFoundException("目录不存在或无权访问："+p);int count=Directory.EnumerateFiles(p,"*.inf",SearchOption.AllDirectories).Take(10001).Count();MessageBox.Show(this,"连接成功。\r\n解析路径："+p+"\r\n检测到 INF："+(count>10000?"超过 10000":count.ToString()),"路径测试通过",MessageBoxButtons.OK,MessageBoxIcon.Information);}}catch(Exception ex){MessageBox.Show(this,"无法访问驱动源：\r\n"+ex.Message,"路径测试失败",MessageBoxButtons.OK,MessageBoxIcon.Error);}finally{Cursor=Cursors.Default;}}
  void TestMapping(){
    if(mappings.CurrentRow==null){MessageBox.Show(this,"请先选择一条机型映射。","提示");return;}var config=Collect();int row=mappings.CurrentRow.Index;if(row<0||row>=config.ModelMappings.Count)return;var map=config.ModelMappings[row];
    Cursor=Cursors.WaitCursor;try{string maker="",model="",sku="";using(var q=new ManagementObjectSearcher("SELECT Manufacturer,Model,SystemSKUNumber FROM Win32_ComputerSystem"))foreach(ManagementObject x in q.Get()){maker=Convert.ToString(x["Manufacturer"]);model=Convert.ToString(x["Model"]);sku=Convert.ToString(x["SystemSKUNumber"]);break;}
      bool a=MapMatch(maker,map.ManufacturerPattern),b=MapMatch(model,map.ModelPattern),c=MapMatch(sku,map.SystemSKUPattern);var lines=new List<string>{"当前电脑：", "  Manufacturer: "+maker,"  Model: "+model,"  System SKU: "+sku,"", "映射："+map.Name,"  启用: "+map.Enabled,"  厂商匹配: "+a,"  型号匹配: "+b,"  SKU 匹配: "+c,""};
      foreach(var source in config.Sources.Where(x=>x.Enabled).OrderBy(x=>x.Priority)){try{using(var net=new Net()){net.Add(source,false);string child=map.DriverPath.Replace("{Manufacturer}",maker).Replace("{Model}",model).Replace("{SystemSKU}",sku).Replace('/',Path.DirectorySeparatorChar);string path=(child.StartsWith("\\\\")||System.Text.RegularExpressions.Regex.IsMatch(child,"^[A-Za-z]:[\\\\/]"))?child:Path.Combine(ResolvePath(source.Path),child.TrimStart('\\','/'));lines.Add(source.Name+": "+path);if(!Directory.Exists(path))lines.Add("  ✗ 目录不存在或无权访问");else lines.Add(Directory.EnumerateFiles(path,"*.inf",SearchOption.AllDirectories).Any()?"  ✓ 目录可访问，已找到 INF":"  ⚠ 目录可访问，但未找到 INF");}}catch(Exception ex){lines.Add(source.Name+": ✗ "+ex.Message);}}
      MessageBox.Show(this,String.Join("\r\n",lines),a&&b&&c&&map.Enabled?"映射测试结果":"映射未命中",MessageBoxButtons.OK,a&&b&&c&&map.Enabled?MessageBoxIcon.Information:MessageBoxIcon.Warning);
    }catch(Exception ex){MessageBox.Show(this,ex.Message,"映射测试失败",MessageBoxButtons.OK,MessageBoxIcon.Error);}finally{Cursor=Cursors.Default;}}
  static bool MapMatch(string value,string pattern){value=System.Text.RegularExpressions.Regex.Replace((value??"").Trim(),"\\s+"," ");pattern=System.Text.RegularExpressions.Regex.Replace((pattern??"").Trim(),"\\s+"," ");if(pattern.Length==0||pattern=="*")return true;if(pattern.IndexOf('*')<0&&pattern.IndexOf('?')<0)return value.Equals(pattern,StringComparison.OrdinalIgnoreCase)||value.IndexOf(pattern,StringComparison.OrdinalIgnoreCase)>=0;return System.Text.RegularExpressions.Regex.IsMatch(value,"^"+System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*",".*").Replace("\\?",".")+"$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);}
  static string ResolvePath(string p){p=Environment.ExpandEnvironmentVariables(p);try{using(var q=new ManagementObjectSearcher("SELECT Manufacturer,Model,SystemSKUNumber FROM Win32_ComputerSystem")){foreach(ManagementObject m in q.Get()){p=p.Replace("{Manufacturer}",Convert.ToString(m["Manufacturer"])).Replace("{Model}",Convert.ToString(m["Model"])).Replace("{SystemSKU}",Convert.ToString(m["SystemSKUNumber"]));break;}}}catch{}return p;}
  void ShowHelp(){MessageBox.Show(this,"1. 在“驱动源”添加 SMB 根目录或本地目录。\r\n2. 在“机型映射”配置厂商/型号/SKU 与相对路径。\r\n3. 新机型未命中时，启用“扫描所有驱动源”完成硬件 ID 回退匹配。\r\n4. 检查配置并保存为 DriverConfig.json，和主程序放在同一目录。\r\n\r\n提示：优先级数字越小越优先；SCCM 推荐使用计算机账户访问 SMB。","快速使用说明",MessageBoxButtons.OK,MessageBoxIcon.Information);}
  public void SelectTestTab(int index){tabs.SelectedIndex=Math.Max(0,Math.Min(tabs.TabCount-1,index));ReflowFilters();PerformAll(this);PerformAll(this);}
  static void PerformAll(Control parent){parent.PerformLayout();foreach(Control child in parent.Controls)PerformAll(child);}
  public void SetTestDpiScale(float scale){testDpiScale=Math.Max(1F,scale);ReflowFilters();}
  public void PrepareForTest(){if(mappings.Rows.Count>0)mappings.Rows[0].Cells[0].Value=true;}
  public void ExportForTest(string path){var c=Collect();var errors=ValidateConfig(c);if(errors.Count>0)throw new InvalidDataException(String.Join("; ",errors));File.WriteAllText(path,Pretty(new JavaScriptSerializer().Serialize(c)),new UTF8Encoding(false));}
}

static class ConfigGeneratorProgram {
  [STAThread] static int Main(string[] args){try{Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);string p=args.Length>0?args[0]:Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),"DriverConfig.json");Application.Run(new ConfigEditorForm(p));return 0;}catch(Exception e){MessageBox.Show(e.ToString(),"配置生成器启动失败",MessageBoxButtons.OK,MessageBoxIcon.Error);return 2;}}
}
}
