using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows.Forms;
using System.Text;
using System.Linq;

namespace UDA
{
    public static class ClientLauncherProgram
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool unattended = args.Any(a => string.Equals(a, "--sccm", StringComparison.OrdinalIgnoreCase));
            string target = null;
            string payload = null;
            try
            {
                // Generated clients are self-contained. Never prefer an adjacent payload ZIP,
                // which could silently override the embedded server address or executable.
                payload = Path.Combine(Path.GetTempPath(), "UDA-ClientPayload-" + Guid.NewGuid().ToString("N") + ".zip");
                ExtractAppendedPayload(Application.ExecutablePath, payload);
                target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UniversalDriverAssistant", "ClientRuns", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(target);
                ZipFile.ExtractToDirectory(payload, target);
                try { File.Delete(payload); } catch { }
                string exe = Path.Combine(target, "UniversalDriverAssistant.exe");
                if (!File.Exists(exe)) throw new FileNotFoundException("客户端主程序缺失", exe);
                var start = new ProcessStartInfo(exe) { UseShellExecute = !unattended, WorkingDirectory = target, CreateNoWindow = unattended,
                    Arguments = string.Join(" ", args.Select(QuoteArgument)) };
                if (!unattended) start.Verb = "runas";
                using (var process = Process.Start(start))
                { if (process == null) return 2; process.WaitForExit(); return process.ExitCode; }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (!unattended && ex.NativeErrorCode != 1223) MessageBox.Show("客户端启动失败：" + ex.Message, "万能驱动助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
            catch (Exception ex) { if (!unattended) MessageBox.Show("客户端启动失败：" + ex.Message, "万能驱动助手", MessageBoxButtons.OK, MessageBoxIcon.Error); return 2; }
            finally
            {
                try { if (target != null && Directory.Exists(target)) Directory.Delete(target, true); } catch { }
                try { if (payload != null && File.Exists(payload)) File.Delete(payload); } catch { }
            }
        }

        private static string QuoteArgument(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value) { if (c == '\\') { slashes++; continue; } if (c == '"') result.Append('\\', slashes * 2 + 1); else result.Append('\\', slashes); result.Append(c); slashes = 0; }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        private static void ExtractAppendedPayload(string executable, string output)
        {
            byte[] expected = Encoding.ASCII.GetBytes("UDAZIP01");
            using (var input = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length < 16) throw new InvalidDataException("客户端载荷缺失");
                input.Seek(-16, SeekOrigin.End);
                byte[] magic = new byte[8];
                input.Read(magic, 0, magic.Length);
                for (int i = 0; i < magic.Length; i++) if (magic[i] != expected[i]) throw new InvalidDataException("客户端载荷标记无效");
                byte[] lengthBytes = new byte[8];
                input.Read(lengthBytes, 0, lengthBytes.Length);
                long length = BitConverter.ToInt64(lengthBytes, 0);
                if (length <= 0 || length > input.Length - 16) throw new InvalidDataException("客户端载荷长度无效");
                input.Seek(-(16 + length), SeekOrigin.End);
                using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[1024 * 1024];
                    long remaining = length;
                    while (remaining > 0)
                    {
                        int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read <= 0) throw new EndOfStreamException();
                        target.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
            }
        }
    }
}
