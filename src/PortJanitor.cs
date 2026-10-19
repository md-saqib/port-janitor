using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("PortJanitor")]
[assembly: AssemblyProduct("PortJanitor")]
[assembly: AssemblyDescription("See which processes hold which ports and kill them.")]
[assembly: AssemblyCopyright("Copyright (c) PortJanitor contributors. MIT License.")]

namespace PortJanitor
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    class ProcInfo
    {
        public int Pid, ParentPid;
        public string Name, Path, CommandLine;
        public DateTime? Started;
        public long WorkingSet;
        public bool IsSystem;
    }

    class PortEntry
    {
        public string Protocol, LocalAddress, RemoteAddress, State;
        public int LocalPort, RemotePort, Pid;
    }

    class Snapshot
    {
        public List<PortEntry> Ports;
        public Dictionary<int, ProcInfo> Processes;
        public DateTime Taken;

        public ProcInfo Get(int pid)
        {
            ProcInfo p;
            return Processes.TryGetValue(pid, out p) ? p : null;
        }

        // Windows never clears a parent PID, so a "parent" that started after its child is a reused PID
        public ProcInfo ParentOf(ProcInfo p)
        {
            ProcInfo parent = p.ParentPid == p.Pid ? null : Get(p.ParentPid);
            if (parent != null && p.Started.HasValue && parent.Started.HasValue && parent.Started.Value > p.Started.Value) return null;
            return parent;
        }
    }

    static class Native
    {
        const int AF_INET = 2, AF_INET6 = 23;
        const int TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1;
        const uint ERROR_INSUFFICIENT_BUFFER = 122;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        delegate uint TableReader(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll")]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll")]
        static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        static readonly string[] TcpStates = { "?", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RCVD", "ESTABLISHED", "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK", "TIME_WAIT", "DELETE_TCB" };

        public static List<PortEntry> GetPorts()
        {
            var list = new List<PortEntry>();
            ReadTcp(list, false);
            ReadTcp(list, true);
            ReadUdp(list, false);
            ReadUdp(list, true);
            return list;
        }

        public static string GetImagePath(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(handle); }
        }

        static IntPtr ReadTable(TableReader reader, int af, int tableClass)
        {
            int size = 0;
            reader(IntPtr.Zero, ref size, false, af, tableClass, 0);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                size += 4096;
                IntPtr buffer = Marshal.AllocHGlobal(size);
                uint rc = reader(buffer, ref size, false, af, tableClass, 0);
                if (rc == 0) return buffer;
                Marshal.FreeHGlobal(buffer);
                if (rc != ERROR_INSUFFICIENT_BUFFER) break;
            }
            return IntPtr.Zero;
        }

        static void ReadTcp(List<PortEntry> list, bool v6)
        {
            IntPtr table = ReadTable(GetExtendedTcpTable, v6 ? AF_INET6 : AF_INET, TCP_TABLE_OWNER_PID_ALL);
            if (table == IntPtr.Zero) return;
            try
            {
                int count = Marshal.ReadInt32(table), rowSize = v6 ? 56 : 24;
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = IntPtr.Add(table, 4 + i * rowSize);
                    var e = new PortEntry { Protocol = "TCP" };
                    int state;
                    if (v6)
                    {
                        e.LocalAddress = Ip(row, 0, 16, 16);
                        e.LocalPort = Port(row, 20);
                        e.RemoteAddress = Ip(row, 24, 16, 40);
                        e.RemotePort = Port(row, 44);
                        state = Marshal.ReadInt32(row, 48);
                        e.Pid = Marshal.ReadInt32(row, 52);
                    }
                    else
                    {
                        state = Marshal.ReadInt32(row, 0);
                        e.LocalAddress = Ip(row, 4, 4, -1);
                        e.LocalPort = Port(row, 8);
                        e.RemoteAddress = Ip(row, 12, 4, -1);
                        e.RemotePort = Port(row, 16);
                        e.Pid = Marshal.ReadInt32(row, 20);
                    }
                    e.State = state > 0 && state < TcpStates.Length ? TcpStates[state] : "?";
                    if (e.State == "LISTEN") e.RemoteAddress = null;
                    list.Add(e);
                }
            }
            finally { Marshal.FreeHGlobal(table); }
        }

        static void ReadUdp(List<PortEntry> list, bool v6)
        {
            IntPtr table = ReadTable(GetExtendedUdpTable, v6 ? AF_INET6 : AF_INET, UDP_TABLE_OWNER_PID);
            if (table == IntPtr.Zero) return;
            try
            {
                int count = Marshal.ReadInt32(table), rowSize = v6 ? 28 : 12;
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = IntPtr.Add(table, 4 + i * rowSize);
                    list.Add(new PortEntry
                    {
                        Protocol = "UDP",
                        State = "",
                        LocalAddress = v6 ? Ip(row, 0, 16, 16) : Ip(row, 0, 4, -1),
                        LocalPort = Port(row, v6 ? 20 : 4),
                        Pid = Marshal.ReadInt32(row, v6 ? 24 : 8),
                    });
                }
            }
            finally { Marshal.FreeHGlobal(table); }
        }

        static string Ip(IntPtr row, int offset, int length, int scopeOffset)
        {
            var bytes = new byte[length];
            Marshal.Copy(IntPtr.Add(row, offset), bytes, 0, length);
            var address = scopeOffset < 0 ? new IPAddress(bytes) : new IPAddress(bytes, (uint)Marshal.ReadInt32(row, scopeOffset));
            return address.ToString();
        }

        // the port sits in the low 16 bits in network byte order
        static int Port(IntPtr row, int offset)
        {
            int raw = Marshal.ReadInt32(row, offset);
            return ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
        }
    }

    static class Collector
    {
        static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

        public static Snapshot Capture()
        {
            var snap = new Snapshot { Ports = Native.GetPorts(), Processes = new Dictionary<int, ProcInfo>() };
            try { ReadWmi(snap.Processes); }
            catch { snap.Processes.Clear(); }
            if (snap.Processes.Count == 0) ReadFallback(snap.Processes);

            foreach (var p in snap.Processes.Values)
            {
                p.Path = p.Pid <= 4 ? null : Native.GetImagePath(p.Pid);
                p.IsSystem = p.Path == null || p.Path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase);
            }
            snap.Taken = DateTime.Now;
            return snap;
        }

        static void ReadWmi(Dictionary<int, ProcInfo> procs)
        {
            const string query = "SELECT ProcessId, ParentProcessId, Name, CommandLine, CreationDate, WorkingSetSize FROM Win32_Process";
            using (var searcher = new ManagementObjectSearcher(query))
            using (var results = searcher.Get())
            {
                foreach (ManagementBaseObject mo in results)
                {
                    using (mo)
                    {
                        var p = new ProcInfo
                        {
                            Pid = Convert.ToInt32(mo["ProcessId"]),
                            ParentPid = Convert.ToInt32(mo["ParentProcessId"]),
                            Name = mo["Name"] as string ?? "",
                            CommandLine = mo["CommandLine"] as string,
                            WorkingSet = Convert.ToInt64(mo["WorkingSetSize"]),
                        };
                        var created = mo["CreationDate"] as string;
                        if (!string.IsNullOrEmpty(created))
                        {
                            try { p.Started = ManagementDateTimeConverter.ToDateTime(created); }
                            catch { }
                        }
                        procs[p.Pid] = p;
                    }
                }
            }
        }

        static void ReadFallback(Dictionary<int, ProcInfo> procs)
        {
            foreach (var proc in Process.GetProcesses())
            {
                using (proc)
                {
                    var p = new ProcInfo { Pid = proc.Id, Name = proc.Id == 0 ? "System Idle Process" : proc.Id == 4 ? "System" : proc.ProcessName + ".exe" };
                    try { p.WorkingSet = proc.WorkingSet64; } catch { }
                    try { p.Started = proc.StartTime; } catch { }
                    procs[p.Pid] = p;
                }
            }
        }
    }

    static class Killer
    {
        static readonly int SelfPid = Process.GetCurrentProcess().Id;

        static readonly HashSet<string> Critical = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "System Idle Process", "Secure System", "Registry", "Memory Compression",
            "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe",
            "svchost.exe", "dwm.exe", "fontdrvhost.exe",
        };

        public static bool IsAdmin()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static string BlockReason(ProcInfo p)
        {
            if (p.Pid == SelfPid) return "this app";
            if (p.Pid <= 4 || Critical.Contains(p.Name ?? "")) return "critical Windows process";
            return null;
        }

        // returns targets parents-first, so a supervisor dies before it can respawn its children
        public static List<ProcInfo> Expand(Snapshot snap, IEnumerable<int> pids, bool tree, List<string> skipped)
        {
            var children = tree ? ChildMap(snap) : null;
            var result = new List<ProcInfo>();
            var seen = new HashSet<int>();
            var queue = new Queue<int>(pids);
            while (queue.Count > 0)
            {
                int pid = queue.Dequeue();
                if (!seen.Add(pid)) continue;
                var p = snap.Get(pid);
                if (p == null) { skipped.Add("PID " + pid + " - no longer running"); continue; }
                string reason = BlockReason(p);
                if (reason != null) { skipped.Add(Describe(p) + " - " + reason); continue; }
                result.Add(p);
                List<ProcInfo> kids;
                if (tree && children.TryGetValue(pid, out kids))
                    foreach (var kid in kids) queue.Enqueue(kid.Pid);
            }
            return result;
        }

        static Dictionary<int, List<ProcInfo>> ChildMap(Snapshot snap)
        {
            var map = new Dictionary<int, List<ProcInfo>>();
            foreach (var p in snap.Processes.Values)
            {
                var parent = snap.ParentOf(p);
                if (parent == null) continue;
                List<ProcInfo> list;
                if (!map.TryGetValue(parent.Pid, out list)) map[parent.Pid] = list = new List<ProcInfo>();
                list.Add(p);
            }
            return map;
        }

        public static string Kill(ProcInfo p)
        {
            try
            {
                using (var proc = Process.GetProcessById(p.Pid))
                {
                    if (p.Started.HasValue)
                    {
                        DateTime started = p.Started.Value;
                        try { started = proc.StartTime; } catch { }
                        if (Math.Abs((started - p.Started.Value).TotalSeconds) > 2) return "PID now belongs to a different process, skipped";
                    }
                    proc.Kill();
                    try { proc.WaitForExit(2000); } catch { }
                    return null;
                }
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (Win32Exception ex) { return ex.NativeErrorCode == 5 ? "access denied" : ex.Message; }
            catch (Exception ex) { return ex.Message; }
        }

        public static string Describe(ProcInfo p)
        {
            string args = ShortArgs(p.CommandLine);
            return p.Name + " (PID " + p.Pid + ")" + (args.Length > 0 ? "   " + args : "");
        }

        static string ShortArgs(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return "";
            cmd = cmd.Trim();
            int end = cmd.StartsWith("\"") ? cmd.IndexOf('"', 1) : cmd.IndexOf(' ');
            string args = end < 0 ? "" : cmd.Substring(end + 1).Trim();
            return args.Length > 90 ? "..." + args.Substring(args.Length - 87) : args;
        }
    }

    class MainForm : Form
    {
        const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        readonly float scale;
        readonly bool isAdmin = Killer.IsAdmin();
        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        readonly TabPage portsTab = new TabPage("Ports"), procTab = new TabPage("Processes");
        readonly DataTable portsTable = new DataTable(), procTable = new DataTable();
        readonly DataGridView portsGrid, procGrid;
        readonly TextBox filterBox = new TextBox();
        readonly CheckBox autoCheck, hideSysCheck, listenCheck, udpCheck;
        readonly Button refreshBtn, killBtn, treeBtn, adminBtn;
        readonly ToolStripStatusLabel infoLabel = new ToolStripStatusLabel();
        readonly ToolStripStatusLabel actionLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        readonly ToolStripStatusLabel userLabel = new ToolStripStatusLabel();
        readonly ToolTip tips = new ToolTip();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 3000 };
        readonly Font boldFont;
        Snapshot snapshot;
        bool refreshing;

        public MainForm()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            Text = "PortJanitor " + Application.ProductVersion + (isAdmin ? " (Administrator)" : "");
            Font = new Font("Segoe UI", 9f);
            boldFont = new Font(Font, FontStyle.Bold);
            ClientSize = new Size(S(1280), S(720));
            MinimumSize = new Size(S(820), S(420));
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            refreshBtn = MakeButton("Refresh", "Refresh now (F5)", 0, delegate { RefreshNow(); });
            autoCheck = MakeCheck("Auto-refresh (3s)", true, 0, null);
            var filterLabel = new Label { Text = "Filter:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(S(18), S(3), 0, S(3)) };
            filterBox.Width = S(240);
            filterBox.Anchor = AnchorStyles.Left;
            filterBox.HandleCreated += delegate { SendMessage(filterBox.Handle, EM_SETCUEBANNER, (IntPtr)1, "port, name, PID or command line"); };
            filterBox.TextChanged += delegate { Render(); };
            tips.SetToolTip(filterBox, "Every space-separated word must match (Ctrl+F to focus, Esc to clear)");
            hideSysCheck = MakeCheck("Hide Windows processes", true, S(12), delegate { Render(); });
            listenCheck = MakeCheck("Listening only", true, 0, delegate { Render(); });
            udpCheck = MakeCheck("Include UDP", false, 0, delegate { Render(); });
            killBtn = MakeButton("Kill", "Kill the selected processes (Del)", S(18), delegate { KillSelected(false); });
            treeBtn = MakeButton("Kill tree", "Kill the selected processes and every child process they started (Shift+Del)", 0, delegate { KillSelected(true); });
            killBtn.ForeColor = treeBtn.ForeColor = Color.FromArgb(185, 28, 28);
            adminBtn = MakeButton("Restart as admin", "Relaunch elevated to inspect and kill elevated or service processes", S(18), delegate { RestartAsAdmin(); });
            adminBtn.Visible = !isAdmin;

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(S(6), S(6), S(6), S(2)) };
            bar.Controls.AddRange(new Control[] { refreshBtn, autoCheck, filterLabel, filterBox, hideSysCheck, listenCheck, udpCheck, killBtn, treeBtn, adminBtn });

            portsGrid = MakeGrid();
            portsTable.Columns.Add("Key", typeof(string));
            AddCol(portsGrid, portsTable, "Proto", "Proto", typeof(string), 60);
            AddCol(portsGrid, portsTable, "Address", "Local address", typeof(string), 150);
            AddCol(portsGrid, portsTable, "Port", "Port", typeof(int), 70, right: true);
            AddCol(portsGrid, portsTable, "State", "State", typeof(string), 100);
            AddCol(portsGrid, portsTable, "PID", "PID", typeof(int), 70, right: true);
            AddCol(portsGrid, portsTable, "Process", "Process", typeof(string), 160);
            AddCol(portsGrid, portsTable, "Remote", "Remote", typeof(string), 170);
            AddCol(portsGrid, portsTable, "Command", "Command line", typeof(string), 300, fill: true);
            portsTable.DefaultView.Sort = "Port ASC";
            portsGrid.DataSource = portsTable;
            portsGrid.ContextMenuStrip = MakeMenu(portsGrid, true);
            portsGrid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) GoToProcess(PidAt(portsGrid, e.RowIndex)); };
            portsGrid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex >= 0 && portsGrid.Columns[e.ColumnIndex].Name == "State" && (e.Value as string) == "LISTEN")
                    e.CellStyle.ForeColor = Color.FromArgb(21, 128, 61);
            };

            procGrid = MakeGrid();
            AddCol(procGrid, procTable, "PID", "PID", typeof(int), 70, right: true);
            AddCol(procGrid, procTable, "Name", "Name", typeof(string), 170);
            AddCol(procGrid, procTable, "PPID", "Parent PID", typeof(int), 80, right: true);
            AddCol(procGrid, procTable, "Parent", "Parent", typeof(string), 140);
            AddCol(procGrid, procTable, "Ports", "Listening ports", typeof(string), 130);
            AddCol(procGrid, procTable, "Memory", "Memory (MB)", typeof(double), 100, format: "N1", right: true);
            AddCol(procGrid, procTable, "Started", "Started", typeof(DateTime), 140, format: "dd MMM HH:mm:ss");
            AddCol(procGrid, procTable, "Command", "Command line", typeof(string), 300, fill: true);
            procTable.DefaultView.Sort = "Name ASC";
            procGrid.DataSource = procTable;
            procGrid.ContextMenuStrip = MakeMenu(procGrid, false);
            procGrid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex >= 0 && procGrid.Columns[e.ColumnIndex].Name == "Ports" && !string.IsNullOrEmpty(e.Value as string))
                {
                    e.CellStyle.ForeColor = Color.FromArgb(29, 78, 216);
                    e.CellStyle.Font = boldFont;
                }
            };

            portsTab.Controls.Add(portsGrid);
            procTab.Controls.Add(procGrid);
            tabs.TabPages.Add(portsTab);
            tabs.TabPages.Add(procTab);

            var status = new StatusStrip();
            status.Items.AddRange(new ToolStripItem[] { infoLabel, actionLabel, userLabel });
            userLabel.Text = isAdmin ? "Running as administrator" : "Not elevated: some processes can't be inspected or killed";

            Controls.Add(tabs);
            Controls.Add(bar);
            Controls.Add(status);
            tabs.BringToFront();

            timer.Tick += delegate { if (autoCheck.Checked && WindowState != FormWindowState.Minimized) RefreshNow(); };
            Shown += delegate { RefreshNow(); timer.Start(); };
            FormClosed += delegate { timer.Stop(); };
        }

        int S(int value) { return (int)Math.Round(value * scale); }

        Button MakeButton(string text, string tip, int leftMargin, EventHandler onClick)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                UseVisualStyleBackColor = true,
                Padding = new Padding(S(4), 0, S(4), 0),
                Margin = new Padding(Math.Max(leftMargin, S(3)), S(3), S(3), S(3)),
                MinimumSize = new Size(S(80), 0),
            };
            b.Click += onClick;
            tips.SetToolTip(b, tip);
            return b;
        }

        CheckBox MakeCheck(string text, bool on, int leftMargin, EventHandler onChange)
        {
            var c = new CheckBox { Text = text, Checked = on, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(Math.Max(leftMargin, S(3)), S(3), S(3), S(3)) };
            if (onChange != null) c.CheckedChanged += onChange;
            return c;
        }

        DataGridView MakeGrid()
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                RowHeadersVisible = false,
                BorderStyle = BorderStyle.None,
                BackgroundColor = SystemColors.Window,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor = Color.FromArgb(232, 232, 232),
                EnableHeadersVisualStyles = false,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                ShowCellToolTips = true,
                StandardTab = true,
            };
            g.RowTemplate.Height = S(24);
            g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(243, 244, 246);
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(243, 244, 246);
            g.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(55, 65, 81);
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(S(4));
            g.DefaultCellStyle.Padding = new Padding(S(4), 0, S(4), 0);
            g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(37, 99, 235);
            g.DefaultCellStyle.SelectionForeColor = Color.White;
            g.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 250, 251);
            typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(g, true, null);

            g.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Delete) return;
                e.Handled = true;
                KillSelected(e.Shift);
            };
            g.CellMouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
                var row = g.Rows[e.RowIndex];
                if (row.Selected) return;
                g.ClearSelection();
                g.CurrentCell = row.Cells[Math.Max(e.ColumnIndex, 0)];
                row.Selected = true;
            };
            return g;
        }

        void AddCol(DataGridView g, DataTable t, string name, string header, Type type, int width, string format = null, bool right = false, bool fill = false)
        {
            t.Columns.Add(name, type);
            var c = new DataGridViewTextBoxColumn { Name = name, DataPropertyName = name, HeaderText = header, Width = S(width), SortMode = DataGridViewColumnSortMode.Automatic };
            if (format != null) c.DefaultCellStyle.Format = format;
            if (right)
            {
                c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
            if (fill)
            {
                c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                c.MinimumWidth = S(width);
            }
            g.Columns.Add(c);
        }

        ContextMenuStrip MakeMenu(DataGridView g, bool isPorts)
        {
            var m = new ContextMenuStrip();
            ((ToolStripMenuItem)m.Items.Add("Kill", null, delegate { KillSelected(false); })).ShortcutKeyDisplayString = "Del";
            ((ToolStripMenuItem)m.Items.Add("Kill process tree", null, delegate { KillSelected(true); })).ShortcutKeyDisplayString = "Shift+Del";
            m.Items.Add(new ToolStripSeparator());
            if (isPorts) m.Items.Add("Go to process", null, delegate { var pids = SelectedPids(g); if (pids.Count > 0) GoToProcess(pids[0]); });
            else m.Items.Add("Go to parent process", null, delegate { GoToParent(g); });
            m.Items.Add("Copy PID", null, delegate { CopyText(string.Join(" ", SelectedPids(g))); });
            m.Items.Add("Copy command line", null, delegate { CopyText(string.Join(Environment.NewLine, SelectedProcs(g).Select(p => p.CommandLine ?? p.Path).Where(s => s != null))); });
            m.Items.Add("Open file location", null, delegate { OpenLocation(g); });
            return m;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5) { RefreshNow(); return true; }
            if (keyData == (Keys.Control | Keys.F)) { filterBox.Focus(); filterBox.SelectAll(); return true; }
            if (keyData == Keys.Escape && filterBox.Focused && filterBox.TextLength > 0) { filterBox.Clear(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        async void RefreshNow()
        {
            if (refreshing) return;
            refreshing = true;
            try
            {
                var snap = await Task.Run(() => Collector.Capture());
                if (IsDisposed) return;
                snapshot = snap;
                Render();
            }
            catch (Exception ex)
            {
                if (!IsDisposed) SetAction("Refresh failed: " + ex.Message);
            }
            finally { refreshing = false; }
        }

        void Render()
        {
            var snap = snapshot;
            if (snap == null) return;
            string[] terms = filterBox.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool hideSys = hideSysCheck.Checked, listenOnly = listenCheck.Checked, udp = udpCheck.Checked;

            var listening = new Dictionary<int, SortedSet<int>>();
            foreach (var e in snap.Ports)
            {
                if (e.Protocol == "UDP" ? !udp : e.State != "LISTEN") continue;
                SortedSet<int> set;
                if (!listening.TryGetValue(e.Pid, out set)) listening[e.Pid] = set = new SortedSet<int>();
                set.Add(e.LocalPort);
            }

            int portRows = 0;
            portsGrid.Columns["Remote"].Visible = !listenOnly;
            Reload(portsGrid, portsTable, "Key", t =>
            {
                foreach (var e in snap.Ports)
                {
                    bool isUdp = e.Protocol == "UDP";
                    if (isUdp ? !udp : listenOnly && e.State != "LISTEN") continue;
                    var p = snap.Get(e.Pid);
                    if (hideSys && (p == null ? e.Pid <= 4 : p.IsSystem)) continue;
                    string name = p != null ? p.Name : "(exited)";
                    string cmd = p != null ? (p.CommandLine ?? p.Path ?? "") : "";
                    string local = Endpoint(e.LocalAddress, e.LocalPort);
                    string remote = e.RemoteAddress == null ? "" : Endpoint(e.RemoteAddress, e.RemotePort);
                    if (!Matches(terms, e.Protocol, local, e.State, e.Pid.ToString(), name, remote, cmd)) continue;
                    t.Rows.Add(e.Protocol + "|" + local + "|" + remote + "|" + e.Pid, e.Protocol, e.LocalAddress, e.LocalPort, e.State, e.Pid, name, remote, cmd);
                    portRows++;
                }
            });

            int procRows = 0, hidden = 0;
            Reload(procGrid, procTable, "PID", t =>
            {
                foreach (var p in snap.Processes.Values)
                {
                    if (hideSys && p.IsSystem) { hidden++; continue; }
                    SortedSet<int> ports;
                    string portText = listening.TryGetValue(p.Pid, out ports) ? string.Join(", ", ports) : "";
                    var parent = snap.ParentOf(p);
                    string parentName = parent != null ? parent.Name : "";
                    string cmd = p.CommandLine ?? p.Path ?? "";
                    if (!Matches(terms, p.Pid.ToString(), p.Name, parentName, portText, cmd)) continue;
                    t.Rows.Add(p.Pid, p.Name, p.ParentPid, parentName, portText, p.WorkingSet / 1048576.0, p.Started.HasValue ? (object)p.Started.Value : DBNull.Value, cmd);
                    procRows++;
                }
            });

            portsTab.Text = "Ports (" + portRows + ")";
            procTab.Text = "Processes (" + procRows + ")";
            int listenPorts = snap.Ports.Where(x => x.State == "LISTEN").Select(x => x.LocalPort).Distinct().Count();
            infoLabel.Text = "Updated " + snap.Taken.ToString("HH:mm:ss") + "  \u00B7  " + listenPorts + " listening TCP ports  \u00B7  "
                + snap.Processes.Count + " processes" + (hideSys ? " (" + hidden + " Windows processes hidden)" : "");
        }

        // rebuilds the rows in place so the grid keeps its sort, selection and scroll position
        static void Reload(DataGridView grid, DataTable table, string keyCol, Action<DataTable> fill)
        {
            var selected = new HashSet<string>();
            foreach (DataGridViewRow r in grid.SelectedRows) selected.Add(RowKey(r, keyCol));
            string current = grid.CurrentRow != null ? RowKey(grid.CurrentRow, keyCol) : null;
            int firstRow = grid.FirstDisplayedScrollingRowIndex;
            int hScroll = grid.HorizontalScrollingOffset;

            table.BeginLoadData();
            table.Rows.Clear();
            fill(table);
            table.EndLoadData();

            if (selected.Count > 0)
            {
                foreach (DataGridViewRow r in grid.Rows)
                    if (RowKey(r, keyCol) == current) { grid.CurrentCell = r.Cells[0]; break; }
                grid.ClearSelection();
                foreach (DataGridViewRow r in grid.Rows)
                    if (selected.Contains(RowKey(r, keyCol))) r.Selected = true;
            }
            else grid.ClearSelection();

            try
            {
                if (firstRow >= 0 && grid.Rows.Count > 0) grid.FirstDisplayedScrollingRowIndex = Math.Min(firstRow, grid.Rows.Count - 1);
                grid.HorizontalScrollingOffset = hScroll;
            }
            catch (InvalidOperationException) { }
        }

        static string RowKey(DataGridViewRow r, string keyCol)
        {
            var drv = r.DataBoundItem as DataRowView;
            return drv == null ? null : Convert.ToString(drv[keyCol]);
        }

        static int PidAt(DataGridView g, int rowIndex)
        {
            var drv = g.Rows[rowIndex].DataBoundItem as DataRowView;
            return drv == null ? -1 : (int)drv["PID"];
        }

        static DataGridViewRow FindRow(DataGridView g, int pid)
        {
            for (int i = 0; i < g.Rows.Count; i++)
                if (PidAt(g, i) == pid) return g.Rows[i];
            return null;
        }

        static List<int> SelectedPids(DataGridView g)
        {
            var pids = new List<int>();
            foreach (DataGridViewRow r in g.SelectedRows)
            {
                var drv = r.DataBoundItem as DataRowView;
                if (drv == null) continue;
                int pid = (int)drv["PID"];
                if (!pids.Contains(pid)) pids.Add(pid);
            }
            return pids;
        }

        List<ProcInfo> SelectedProcs(DataGridView g)
        {
            var list = new List<ProcInfo>();
            var snap = snapshot;
            if (snap == null) return list;
            foreach (int pid in SelectedPids(g))
            {
                var p = snap.Get(pid);
                if (p != null) list.Add(p);
            }
            return list;
        }

        static bool Matches(string[] terms, params string[] fields)
        {
            foreach (var term in terms)
            {
                bool hit = false;
                foreach (var f in fields)
                    if (f != null && f.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                if (!hit) return false;
            }
            return true;
        }

        static string Endpoint(string address, int port)
        {
            return (address.Contains(":") ? "[" + address + "]" : address) + ":" + port;
        }

        async void KillSelected(bool tree)
        {
            var grid = tabs.SelectedTab == portsTab ? portsGrid : procGrid;
            var snap = snapshot;
            var pids = SelectedPids(grid);
            if (snap == null || pids.Count == 0) { SetAction("Select one or more rows first."); return; }

            var skipped = new List<string>();
            var targets = Killer.Expand(snap, pids, tree, skipped);
            if (targets.Count == 0)
            {
                MessageBox.Show(this, "Nothing to kill.\n\n" + string.Join("\n", skipped), "PortJanitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var msg = new StringBuilder();
            msg.Append(tree ? "Kill these " + targets.Count + " processes (selected + their children)?\n\n" : "Kill " + (targets.Count == 1 ? "this process" : "these " + targets.Count + " processes") + "?\n\n");
            foreach (var p in targets.Take(15)) msg.AppendLine(Killer.Describe(p));
            if (targets.Count > 15) msg.AppendLine("...and " + (targets.Count - 15) + " more");
            if (skipped.Count > 0) msg.Append("\nSkipped:\n" + string.Join("\n", skipped));
            if (MessageBox.Show(this, msg.ToString(), "Confirm kill", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            var errors = await Task.Run(() =>
            {
                var failed = new List<string>();
                foreach (var p in targets)
                {
                    string error = Killer.Kill(p);
                    if (error != null) failed.Add(Killer.Describe(p) + ": " + error);
                }
                return failed;
            });
            if (IsDisposed) return;

            int killed = targets.Count - errors.Count;
            SetAction(killed > 0 ? "Killed " + killed + (killed == 1 ? " process." : " processes.") : "Nothing was killed.");
            if (errors.Count > 0)
            {
                string hint = isAdmin ? "" : "\n\nTry \"Restart as admin\" for elevated or service processes.";
                MessageBox.Show(this, "Could not kill:\n\n" + string.Join("\n", errors) + hint, "PortJanitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            RefreshNow();
        }

        void GoToProcess(int pid)
        {
            tabs.SelectedTab = procTab;
            var row = FindRow(procGrid, pid);
            var p = snapshot != null ? snapshot.Get(pid) : null;
            if (row == null && p != null)
            {
                if (filterBox.TextLength > 0) filterBox.Clear();
                if (p.IsSystem && hideSysCheck.Checked) hideSysCheck.Checked = false;
                row = FindRow(procGrid, pid);
            }
            if (row == null) { SetAction("PID " + pid + " is no longer running."); return; }
            procGrid.CurrentCell = row.Cells[0];
            procGrid.ClearSelection();
            row.Selected = true;
            procGrid.Focus();
        }

        void GoToParent(DataGridView g)
        {
            var p = SelectedProcs(g).FirstOrDefault();
            if (p == null) { SetAction("Select a process first."); return; }
            var parent = snapshot.ParentOf(p);
            if (parent == null) { SetAction("The parent of " + p.Name + " is no longer running."); return; }
            GoToProcess(parent.Pid);
        }

        void OpenLocation(DataGridView g)
        {
            var p = SelectedProcs(g).FirstOrDefault(x => x.Path != null);
            if (p == null) { SetAction("File location isn't available for this process."); return; }
            Process.Start("explorer.exe", "/select,\"" + p.Path + "\"");
        }

        void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) { SetAction("Nothing to copy."); return; }
            try { Clipboard.SetText(text); SetAction("Copied to clipboard."); }
            catch (Exception ex) { SetAction("Copy failed: " + ex.Message); }
        }

        void RestartAsAdmin()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas" });
                Close();
            }
            catch (Win32Exception) { SetAction("Elevation was cancelled."); }
        }

        void SetAction(string text) { actionLabel.Text = text; }
    }
}
