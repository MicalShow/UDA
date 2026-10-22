using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace UDA {
internal static class ConfigEditorHarness {
  [STAThread] static int Main(string[] args){
    if(args.Length<5){Console.Error.WriteLine("Usage: ConfigEditorHarness <config> <png> <width> <height> <tab>");return 2;}
    Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);int w=Int32.Parse(args[2]),h=Int32.Parse(args[3]),tab=Int32.Parse(args[4]);float scale=args.Length>5?Single.Parse(args[5],System.Globalization.CultureInfo.InvariantCulture):1F;
    using(var f=new ConfigEditorForm(args[0])){f.MinimumSize=Size.Empty;f.ShowInTaskbar=false;f.StartPosition=FormStartPosition.Manual;f.Location=new Point(-32000,-32000);f.Show();f.ClientSize=new Size(w,h);if(Math.Abs(scale-1F)>0.001F)f.Scale(new SizeF(scale,scale));f.SetTestDpiScale(scale);f.SelectTestTab(tab);Application.DoEvents();
      var issues=new List<string>();Find(f,issues);using(var b=new Bitmap(f.ClientSize.Width,f.ClientSize.Height)){f.DrawToBitmap(b,new Rectangle(Point.Empty,b.Size));b.Save(args[1],ImageFormat.Png);}File.WriteAllLines(args[1]+".txt",issues.ToArray());
      string round=args[1]+".json";f.PrepareForTest();f.ExportForTest(round);var c=C.Load(round);if(c.General==null||c.Sources.Count==0||c.ModelMappings.Count==0||c.Categories.Count==0||c.Filters==null)issues.Add("roundtrip incomplete");
      Console.WriteLine("tab={0} {1}x{2} issues={3}",tab,w,h,issues.Count);return issues.Count==0?0:1;}
  }
  static void Find(Control p,List<string> r){foreach(Control c in p.Controls){if(!c.Visible)continue;if(c.Left<-2||c.Top<-2||c.Right>p.ClientSize.Width+2||c.Bottom>p.ClientSize.Height+2){var s=p as ScrollableControl;if(s==null||!s.AutoScroll)r.Add(c.GetType().Name+" bounds="+c.Bounds+" parent="+p.ClientSize);}Find(c,r);}}
}
}
