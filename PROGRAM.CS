// Program.cs
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Msd81J2534Flasher
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        // UI
        ComboBox cbDll;
        TextBox txtDllPath;
        Button btnBrowseDll;
        ComboBox cbProtocol;
        Button btnOpen;
        Button btnClose;
        Label lblStatus;
        TextBox txtTxId;
        TextBox txtRxId;
        Button btnFlash;
        TextBox txtLog;
        OpenFileDialog ofdBin;

        // J2534 handles
        int devId = 0;
        int chanId = 0;
        CancellationTokenSource pollCts;

        public MainForm()
        {
            Text = "MSD81 J2534 Flasher (prototype)";
            Width = 820;
            Height = 560;
            StartPosition = FormStartPosition.CenterScreen;

            Label l1 = new Label() { Left = 10, Top = 10, Width = 120, Text = "J2534 DLL:" };
            txtDllPath = new TextBox() { Left = 130, Top = 8, Width = 480, Text = "YourJ2534.dll" };
            btnBrowseDll = new Button() { Left = 620, Top = 6, Width = 80, Text = "Browse" };
            btnBrowseDll.Click += (s, e) =>
            {
                using var ofd = new OpenFileDialog();
                ofd.Filter = "DLL files|*.dll|All files|*.*";
                if (ofd.ShowDialog() == DialogResult.OK) txtDllPath.Text = ofd.FileName;
            };

            Label l2 = new Label() { Left = 10, Top = 40, Width = 120, Text = "Protocol:" };
            cbProtocol = new ComboBox() { Left = 130, Top = 38, Width = 200 };
            cbProtocol.Items.AddRange(new object[] { "D-CAN (ISO15765 11/500)", "K-Line (ISO9141 10400)" });
            cbProtocol.SelectedIndex = 0;

            Label l3 = new Label() { Left = 350, Top = 40, Width = 60, Text = "TxId:" };
            txtTxId = new TextBox() { Left = 410, Top = 38, Width = 80, Text = "7E0" };
            Label l4 = new Label() { Left = 500, Top = 40, Width = 60, Text = "RxId:" };
            txtRxId = new TextBox() { Left = 560, Top = 38, Width = 80, Text = "7E8" };

            btnOpen = new Button() { Left = 660, Top = 6, Width = 120, Text = "Open (PassThruOpen)" };
            btnClose = new Button() { Left = 660, Top = 36, Width = 120, Text = "Close" };
            btnOpen.Click += BtnOpen_Click;
            btnClose.Click += BtnClose_Click;

            lblStatus = new Label() { Left = 10, Top = 72, Width = 770, Text = "Status: Closed" };

            btnFlash = new Button() { Left = 10, Top = 100, Width = 120, Text = "Flash file..." };
            btnFlash.Click += BtnFlash_Click;

            txtLog = new TextBox() { Left = 10, Top = 140, Width = 770, Height = 380, Multiline = true, ScrollBars = ScrollBars.Vertical };

            Controls.AddRange(new Control[] { l1, txtDllPath, btnBrowseDll, l2, cbProtocol, l3, txtTxId, l4, txtRxId, btnOpen, btnClose, lblStatus, btnFlash, txtLog });
            ofdBin = new OpenFileDialog() { Filter = "BIN files|*.bin;*.raw;*.binfile|All files|*.*" };
        }

        void Log(string s)
        {
            if (InvokeRequired) Invoke((Action)(() => Log(s)));
            else
            {
                txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}");
                txtLog.SelectionStart = txtLog.Text.Length;
                txtLog.ScrollToCaret();
            }
        }

        private void BtnOpen_Click(object? sender, EventArgs e)
        {
            string dll = txtDllPath.Text.Trim();
            if (!File.Exists(dll))
            {
                MessageBox.Show("DLL not found. Place your J2534 DLL and point here.", "DLL missing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Set the DLL name in J2534Api runtime
            J2534Api.SetDllPath(dll);

            int rc = J2534Api.PassThruOpen(IntPtr.Zero, ref devId);
            if (rc != 0)
            {
                Log($"PassThruOpen failed: {rc}");
                MessageBox.Show($"PassThruOpen failed: {rc}");
                return;
            }
            Log("PassThruOpen OK (devId=" + devId + ")");

            uint proto = (cbProtocol.SelectedIndex == 0) ? J2534Api.ISO15765 : J2534Api.ISO9141;
            uint baud = (cbProtocol.SelectedIndex == 0) ? 500000u : 10400u;
            int ch = 0;
            rc = J2534Api.PassThruConnect(devId, proto, 0, baud, ref ch);
            if (rc != 0)
            {
                Log($"PassThruConnect failed: {rc}");
                MessageBox.Show($"PassThruConnect failed: {rc}");
                return;
            }
            chanId = ch;
            Log($"PassThruConnect OK (chanId={chanId})");
            lblStatus.Text = $"Status: Connected (dev {devId}, ch {chanId})";
        }

        private void BtnClose_Click(object? sender, EventArgs e)
        {
            try
            {
                if (chanId != 0) { J2534Api.PassThruDisconnect(chanId); Log("Disconnected channel"); chanId = 0; }
                if (devId != 0) { J2534Api.PassThruClose(devId); Log("Closed device"); devId = 0; }
                lblStatus.Text = "Status: Closed";
            }
            catch (Exception ex) { Log("Close error: " + ex.Message); }
        }

        private async void BtnFlash_Click(object? sender, EventArgs e)
        {
            if (devId == 0 || chanId == 0) { MessageBox.Show("Open device first"); return; }
            if (ofdBin.ShowDialog() != DialogResult.OK) return;
            string file = ofdBin.FileName;
            Log("Selected file: " + file);

            // parse IDs
            uint tx = Convert.ToUInt32(txtTxId.Text.Trim(), 16);
            uint rx = Convert.ToUInt32(txtRxId.Text.Trim(), 16);

            var flasher = new Msd81Flasher(devId, chanId, tx, rx, J2534Api.PassThruWriteMsgs, J2534Api.PassThruReadMsgs, Log);

            // Dry-run prompt
            var dr = MessageBox.Show("Perform DRY-RUN first (no full transfer). Recommend YES. Continue?", "Dry-run", MessageBoxButtons.YesNoCancel);
            if (dr == DialogResult.Cancel) return;

            try
            {
                // Step: Enter programming session
                Log("Entering programming session...");
                await Task.Run(() => flasher.EnterProgrammingSession());
                Log("Programming session entered.");

                // Security: get seed, ask for key provider
                Log("Requesting seed...");
                byte[] seed = null;
                await Task.Run(() => { seed = flasher.RequestSeed(); });
                Log("Seed: " + BitConverter.ToString(seed));

                // **KEY PROVIDER** — here we prompt user to paste hex key (must be correct for ECU)
                string keyHex = Microsoft.VisualBasic.Interaction.InputBox("Enter security key (hex, e.g. AABBCC...):", "Security Key", "");
                if (string.IsNullOrEmpty(keyHex))
                {
                    MessageBox.Show("No key provided. Aborting flash.", "Key missing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                byte[] key = HexToBytes(keyHex);
                Log("Sending key...");
                await Task.Run(() => flasher.SendKey(key));
                Log("Key accepted.");

                // RequestDownload
                var fi = new FileInfo(file);
                Log($"Requesting download, size={fi.Length} bytes...");
                await Task.Run(() => flasher.RequestDownload(fi.Length));
                Log("RequestDownload OK");

                // If user chose Dry-run: send only small chunk
                if (dr == DialogResult.Yes)
                {
                    Log("Performing DRY-RUN small transfer (8KB)...");
                    await Task.Run(() =>
                    {
                        using var fs = File.OpenRead(file);
                        var copy = new MemoryStream();
                        var buf = new byte[8192];
                        int r = fs.Read(buf, 0, buf.Length);
                        copy.Write(buf, 0, r);
                        copy.Position = 0;
                        flasher.TransferData(copy, (s, t) => Log($"Dry-run progress: {s}/{t}"));
                    });
                    Log("Dry-run transfer complete.");
                    MessageBox.Show("Dry-run complete. If OK, run flash again and choose NO to perform full transfer.");
                    return;
                }

                // Full transfer
                Log("Starting full transfer...");
                await Task.Run(() =>
                {
                    using var fs = File.OpenRead(file);
                    flasher.TransferData(fs, (sent, total) => Log($"Progress: {sent}/{total}"));
                });
                Log("TransferData complete");

                // RequestTransferExit
                await Task.Run(() => flasher.RequestTransferExit());
                Log("RequestTransferExit OK");

                // Reboot ECU
                await Task.Run(() => flasher.RebootEcu());
                Log("ECU reset sent. Flashing sequence complete.");
                MessageBox.Show("Flashing complete (no exceptions thrown) — verify ECU functionality.", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("Flasher error: " + ex.Message);
                MessageBox.Show("Flasher error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static byte[] HexToBytes(string hex)
        {
            hex = hex.Replace(" ", "").Replace("-", "");
            if (hex.Length % 2 != 0) hex = "0" + hex;
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { if (chanId != 0) J2534Api.PassThruDisconnect(chanId); } catch { }
            try { if (devId != 0) J2534Api.PassThruClose(devId); } catch { }
            base.OnFormClosing(e);
        }
    }

    // ---------- J2534 API wrapper (runtime DLL path settable) ----------
    public static class J2534Api
    {
        public const uint ISO15765 = 6;
        public const uint ISO9141 = 3;

        private const string DefaultDll = "YourJ2534.dll";
        private static string dllPath = DefaultDll;
        private static IntPtr libHandle = IntPtr.Zero;

        public static void SetDllPath(string path)
        {
            dllPath = path;
            // Load native library explicit so DllImport calls bind to it. We'll use LoadLibrary & GetProcAddress approach.
            if (libHandle != IntPtr.Zero) FreeLibrary(libHandle);
            libHandle = LoadLibrary(dllPath);
            if (libHandle == IntPtr.Zero) throw new Exception("LoadLibrary failed for " + dllPath);
            // We don't resolve functions here; DllImport uses dll name, so for simplicity we assume dll is in working dir and name matches.
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr LoadLibrary(string lpFileName);
        [DllImport("kernel32", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        // NOTE: Many J2534 libraries expose functions with same names. DllImport below calls the dll by name.
        // Make sure the dll name matches the file you loaded above or is in PATH.
        [DllImport("YourJ2534.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int PassThruOpen(IntPtr pName, ref int DeviceID);

        [DllImport("YourJ2534.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int PassThruClose(int DeviceID);

        [DllImport("YourJ2534.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int PassThruConnect(int DeviceID, uint ProtocolID, uint Flags, uint BaudRate, ref int ChannelID);

        [DllImport("YourJ2534.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int PassThruDisconnect(int ChannelID);

        [StructLayout(LayoutKind.Sequential)]
        public struct PASSTHRU_MSG
        {
            public uint ProtocolID;
            public uint RxStatus;
            public uint TxFlags;
            public uint Timestamp;
            public uint DataSize;
            public uint ExtraDataIndex;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4128)]
            public byte[] Data;
        }

        [DllImport("YourJ2534.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int PassThruWriteMsgs(int ChannelID, ref PASSTHRU_MSG pMsg, ref uint pNumMsgs, uint Timeout);

        [DllImport("YourJ2534.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern int PassThruReadMsgs(int ChannelID, ref PASSTHRU_MSG pMsg, ref uint pNumMsgs, uint Timeout);

        // Helper wrappers to adapt signatures for delegates used by IsoTp earlier
        public static int WriteMsgsWrapper(int chan, ref PASSTHRU_MSG msg, ref uint n, uint t) => PassThruWriteMsgs(chan, ref msg, ref n, t);
        public static int ReadMsgsWrapper(int chan, ref PASSTHRU_MSG msg, ref uint n, uint t) => PassThruReadMsgs(chan, ref msg, ref n, t);
    }

    // ---------- IsoTP + helper flasher (simplified) ----------
    public class IsoTp
    {
        private readonly int channelId;
        private readonly uint txId;
        private readonly uint rxId;
        private readonly Func<int, ref J2534Api.PASSTHRU_MSG, ref uint, uint, int> writeMsgs;
        private readonly Func<int, ref J2534Api.PASSTHRU_MSG, ref uint, uint, int> readMsgs;
        private readonly int readTimeoutMs = 2000;

        public IsoTp(int channelId, uint txId, uint rxId,
            Func<int, ref J2534Api.PASSTHRU_MSG, ref uint, uint, int> writeMsgs,
            Func<int, ref J2534Api.PASSTHRU_MSG, ref uint, uint, int> readMsgs)
        {
            this.channelId = channelId;
            this.txId = txId;
            this.rxId = rxId;
            this.writeMsgs = writeMsgs;
            this.readMsgs = readMsgs;
        }

        public byte[] SendAndReceive(byte[] payload)
        {
            if (payload.Length <= 7)
            {
                byte pci = (byte)((0x0 << 4) | (payload.Length & 0x0F));
                byte[] frame = new byte[8];
                frame[0] = pci;
                Array.Copy(payload, 0, frame, 1, payload.Length);
                SendCanFrame(frame);
            }
            else
            {
                int totalLen = payload.Length;
                byte[] ff = new byte[8];
                ff[0] = (byte)((0x1 << 4) | ((totalLen >> 8) & 0x0F));
                ff[1] = (byte)(totalLen & 0xFF);
                Array.Copy(payload, 0, ff, 2, Math.Min(6, payload.Length));
                SendCanFrame(ff);

                var fc = ReadCanUntil(rx => ((rx[0] >> 4) & 0x0F) == 0x3, readTimeoutMs);
                if (fc == null) throw new Exception("No Flow Control after FF");
                byte flowStatus = (byte)(fc[0] & 0x0F);
                if (flowStatus != 0x0) throw new Exception($"Flow status {flowStatus}");

                int stMin = fc.Length > 2 ? fc[2] : 0;
                int stMinMs = (stMin <= 0x7F) ? stMin : 1;

                int bytesSent = 6;
                byte seq = 1;
                while (bytesSent < totalLen)
                {
                    int toSend = Math.Min(7, totalLen - bytesSent);
                    byte[] cf = new byte[8];
                    cf[0] = (byte)((0x2 << 4) | (seq & 0x0F));
                    Array.Copy(payload, bytesSent, cf, 1, toSend);
                    SendCanFrame(cf);
                    bytesSent += toSend;
                    seq++; if (seq > 0x0F) seq = 0;
                    if (stMinMs > 0) Thread.Sleep(stMinMs);
                }
            }

            var resp = ReadIsoTpResponse();
            return resp;
        }

        private void SendCanFrame(byte[] data8)
        {
            var msg = new J2534Api.PASSTHRU_MSG
            {
                ProtocolID = J2534Api.ISO15765,
                TxFlags = 0,
                Data = new byte[4128],
                DataSize = 8
            };
            // pack ID as 4 bytes BE then payload at offset 4
            msg.Data[0] = (byte)((txId >> 24) & 0xFF);
            msg.Data[1] = (byte)((txId >> 16) & 0xFF);
            msg.Data[2] = (byte)((txId >> 8) & 0xFF);
            msg.Data[3] = (byte)(txId & 0xFF);
            Array.Copy(data8, 0, msg.Data, 4, data8.Length);
            uint num = 1;
            int rc = writeMsgs(channelId, ref msg, ref num, 1000);
            if (rc != 0) throw new Exception("PassThruWriteMsgs rc=" + rc);
        }

        private byte[] ReadCanUntil(Func<byte[], bool> predicate, int timeout)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeout);
            while (DateTime.UtcNow < until)
            {
                var frame = ReadCanFrame();
                if (frame == null) continue;
                if (predicate(frame)) return frame;
            }
            return null;
        }

        private byte[] ReadIsoTpResponse()
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(readTimeoutMs + 2000);
            int expectedLen = -1;
            byte[] buffer = Array.Empty<byte>();
            while (DateTime.UtcNow < end)
            {
                var rx = ReadCanFrame();
                if (rx == null) continue;
                int payloadOffset = 0; // here frame is payload bytes (we read payload in ReadCanFrame)
                byte pci = rx[payloadOffset];
                int ft = (pci >> 4) & 0x0F;
                if (ft == 0x0)
                {
                    int len = pci & 0x0F;
                    byte[] data = new byte[len];
                    Array.Copy(rx, payloadOffset + 1, data, 0, len);
                    return data;
                }
                else if (ft == 0x1)
                {
                    int lenHigh = pci & 0x0F;
                    int lenLow = rx[payloadOffset + 1];
                    expectedLen = (lenHigh << 8) + lenLow;
                    buffer = new byte[expectedLen];
                    int copy = Math.Min(6, expectedLen);
                    Array.Copy(rx, payloadOffset + 2, buffer, 0, copy);
                    int copied = copy;
                    SendFlowControlContinue();

                    while (copied < expectedLen)
                    {
                        var cf = ReadCanFrame();
                        if (cf == null) throw new Exception("Timeout CF");
                        byte pciCf = cf[payloadOffset];
                        int ft2 = (pciCf >> 4) & 0x0F;
                        if (ft2 != 0x2) continue;
                        int toCopy = Math.Min(7, expectedLen - copied);
                        Array.Copy(cf, payloadOffset + 1, buffer, copied, toCopy);
                        copied += toCopy;
                    }
                    return buffer;
                }
            }
            throw new Exception("Timeout waiting ISO-TP response");
        }

        private void SendFlowControlContinue()
        {
            byte[] fc = new byte[8];
            fc[0] = 0x30; fc[1] = 0x00; fc[2] = 0x00;
            SendCanFrame(fc);
        }

        private byte[] ReadCanFrame()
        {
            var rx = new J2534Api.PASSTHRU_MSG { Data = new byte[4128] };
            uint num = 1;
            int rc = readMsgs(channelId, ref rx, ref num, (uint)readTimeoutMs);
            if (rc != 0 || num == 0) return null;
            // We packed payload at Data[4..11] on write — some drivers return similarly.
            // Try to extract payload: if DataSize >= 8 and Data[4..] not zeros, return those 8 bytes.
            if (rx.Data == null) return null;
            byte[] frame = new byte[8];
            Array.Copy(rx.Data, 4, frame, 0, 8);
            return frame;
        }
    }

    public class Msd81Flasher
    {
        private readonly int devId;
        private readonly int chanId;
        private readonly uint txId;
        private readonly uint rxId;
        private readonly Func<int, ref J2534Api.PASSTHRU_MSG, ref uint, uint, int> writeFunc;
        private readonly Func<int, ref J2534Api.PASSTHRU_MSG, ref uint, uint, int> readFunc;
        private readonly Action<string> logger;

        private IsoTp isotp;

        public Msd81Flasher(int devId, int chanId, uint txId, uint rxId,
            Func<int, ref J2534Api.PASSTHRU_MSG, ref uint, uint, int> writeFunc,
            Func<int, ref J2534Api.PASSTHRU_MSG, ref uint, uint, int> readFunc,
            Action<string> logger)
        {
            this.devId = devId; this.chanId = chanId; this.txId = txId; this.rxId = rxId;
            this.writeFunc = writeFunc; this.readFunc = readFunc; this.logger = logger;
            isotp = new IsoTp(chanId, txId, rxId, writeFunc, readFunc);
        }

        void Log(string s) { logger?.Invoke(s); }

        public void EnterProgrammingSession()
        {
            Log("Sending 0x10 0x02 (enter programming) ...");
            var r = isotp.SendAndReceive(new byte[] { 0x10, 0x02 });
            if (r.Length < 2 || r[0] != 0x50 || r[1] != 0x02) throw new Exception("Enter programming session failed");
            Log("Entered programming session.");
        }

        public byte[] RequestSeed()
        {
            Log("Requesting seed (0x27 0x01) ...");
            var r = isotp.SendAndReceive(new byte[] { 0x27, 0x01 });
            if (r.Length < 2 || r[0] != 0x67 || r[1] != 0x01) throw new Exception("Seed request failed");
            byte[] seed = new byte[r.Length - 2];
            Array.Copy(r, 2, seed, 0, seed.Length);
            return seed;
        }

        public void SendKey(byte[] key)
        {
            Log("Sending key (0x27 0x02) ...");
            byte[] req = new byte[2 + key.Length];
            req[0] = 0x27; req[1] = 0x02; Array.Copy(key, 0, req, 2, key.Length);
            var r = isotp.SendAndReceive(req);
            if (r.Length < 2 || r[0] != 0x67 || r[1] != 0x02) throw new Exception("Key rejected");
            Log("Key accepted.");
        }

        public void RequestDownload(long byteCount)
        {
            Log("RequestDownload (0x34) ...");
            int size = (int)byteCount;
            byte[] sizeBytes = new byte[3] { (byte)((size >> 16) & 0xFF), (byte)((size >> 8) & 0xFF), (byte)(size & 0xFF) };
            byte[] req = new byte[2 + 3];
            req[0] = 0x34; req[1] = 0x00; Array.Copy(sizeBytes, 0, req, 2, 3);
            var r = isotp.SendAndReceive(req);
            if (r.Length < 1 || r[0] != 0x74) throw new Exception("RequestDownload rejected");
            Log("RequestDownload accepted.");
        }

        public void TransferData(Stream binStream, Action<int, long>? progress = null)
        {
            const int blockPayload = 1024;
            byte blockCounter = 1;
            long total = binStream.Length;
            long sent = 0;
            byte[] buffer = new byte[blockPayload];
            int read;
            while ((read = binStream.Read(buffer, 0, blockPayload)) > 0)
            {
                byte[] payload = new byte[2 + read];
                payload[0] = 0x36; payload[1] = blockCounter;
                Array.Copy(buffer, 0, payload, 2, read);
                var r = isotp.SendAndReceive(payload);
                if (r.Length < 2 || r[0] != 0x76 || r[1] != blockCounter) throw new Exception($"TransferData failed at block {blockCounter}");
                sent += read;
                progress?.Invoke((int)sent, total);
                blockCounter++; if (blockCounter == 0) blockCounter = 1;
            }
            Log("TransferData finished.");
        }

        public void RequestTransferExit()
        {
            var r = isotp.SendAndReceive(new byte[] { 0x37 });
            if (r.Length < 1 || r[0] != 0x77) throw new Exception("RequestTransferExit failed");
            Log("RequestTransferExit OK");
        }

        public void RebootEcu()
        {
            var r = isotp.SendAndReceive(new byte[] { 0x11, 0x01 });
            if (r.Length < 2 || r[0] != 0x51) throw new Exception("ECU reset not positive");
            Log("ECU reset positive.");
        }
    }
}
