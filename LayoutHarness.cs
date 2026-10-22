using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace UDA
{
    internal static class LayoutHarness
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length < 5)
            {
                Console.Error.WriteLine("Usage: LayoutHarness <config> <output.png> <width> <height> <page> [scale] [lan]");
                return 2;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            int width = int.Parse(args[2]);
            int height = int.Parse(args[3]);
            float scale = args.Length > 5 ? float.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 1F;
            var config = C.Load(args[0]);
            string logDir = Path.Combine(Path.GetDirectoryName(args[1]), "logs");
            bool lanEdition = args.Length > 6 && string.Equals(args[6], "lan", StringComparison.OrdinalIgnoreCase);
            using (var logger = new L(logDir))
            using (var form = lanEdition
                ? (ResponsiveForm)new LanEditionForm(config, new O(), logger, true)
                : new ResponsiveForm(config, new O(), logger, true))
            {
                form.ClientSize = new Size(width, height);
                form.CreateControl();
                form.LoadPreviewData();
                form.SelectPreviewPage(args[4]);
                if (Math.Abs(scale - 1F) > 0.001F) form.Scale(new SizeF(scale, scale));
                form.SetTestDpiScale(scale);
                // Scale() on a handle-less form loses the non-client delta vertically, so the form's
                // own ClientSize is NOT the intended viewport. Compute it from the arguments instead.
                Size requestedClientSize = new Size((int)Math.Round(width * scale), (int)Math.Round(height * scale));
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000);
                form.Show();
                // Restore the requested viewport after the window manager has created the handle.
                // This permits off-screen DPI tests larger than the physical test monitor.
                form.ClientSize = requestedClientSize;
                form.RefreshResponsiveLayout();
                Application.DoEvents();
                var overflows = new List<string>();
                FindOverflows(form, overflows);
                using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(args[1], ImageFormat.Png);
                }
                File.WriteAllLines(args[1] + ".txt", overflows.ToArray());
                Console.WriteLine("{0} {1}x{2} scale={3} overflow={4}", args[4], form.ClientSize.Width, form.ClientSize.Height, scale, overflows.Count);
                return overflows.Count == 0 ? 0 : 1;
            }
        }

        private static void FindOverflows(Control parent, List<string> results)
        {
            foreach (Control child in parent.Controls)
            {
                if (!child.Visible) continue;
                if (child.Left < -2 || child.Top < -2 || child.Right > parent.ClientSize.Width + 2 || child.Bottom > parent.ClientSize.Height + 2)
                {
                    // AutoScroll containers intentionally own a virtual display rectangle.
                    var scrollable = parent as ScrollableControl;
                    if (scrollable == null || !scrollable.AutoScroll)
                        results.Add(child.GetType().Name + " bounds=" + child.Bounds + " parent=" + parent.ClientSize);
                }
                FindOverflows(child, results);
            }
        }
    }
}
