using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PortJanitor.Tests
{
    static class SmokeTest
    {
        static int failures;

        static int Main()
        {
            int self = Process.GetCurrentProcess().Id;
            var tcp4 = new TcpListener(IPAddress.Loopback, 0);
            tcp4.Start();
            int tcp4Port = ((IPEndPoint)tcp4.LocalEndpoint).Port;
            TcpListener tcp6 = null;
            int tcp6Port = -1;
            if (Socket.OSSupportsIPv6)
            {
                tcp6 = new TcpListener(IPAddress.IPv6Loopback, 0);
                tcp6.Start();
                tcp6Port = ((IPEndPoint)tcp6.LocalEndpoint).Port;
            }
            var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int udpPort = ((IPEndPoint)udp.Client.LocalEndPoint).Port;
            var client = new TcpClient();
            client.Connect(IPAddress.Loopback, tcp4Port);
            var accepted = tcp4.AcceptTcpClient();

            var sw = Stopwatch.StartNew();
            var snap = Collector.Capture();
            Console.WriteLine("Captured " + snap.Ports.Count + " sockets and " + snap.Processes.Count + " processes in " + sw.ElapsedMilliseconds + " ms");

            Check(snap.Ports.Any(e => e.Protocol == "TCP" && e.State == "LISTEN" && e.LocalAddress == "127.0.0.1" && e.LocalPort == tcp4Port && e.Pid == self), "IPv4 TCP listener is found with our PID");
            if (tcp6 != null)
                Check(snap.Ports.Any(e => e.Protocol == "TCP" && e.State == "LISTEN" && e.LocalAddress == "::1" && e.LocalPort == tcp6Port && e.Pid == self), "IPv6 TCP listener is found with our PID");
            Check(snap.Ports.Any(e => e.State == "ESTABLISHED" && e.RemotePort == tcp4Port && e.Pid == self), "Established connection is found with its remote port");
            Check(snap.Ports.Any(e => e.Protocol == "UDP" && e.LocalPort == udpPort && e.Pid == self), "UDP socket is found with our PID");

            var me = snap.Get(self);
            Check(me != null && me.Name.Equals("SmokeTest.exe", StringComparison.OrdinalIgnoreCase), "Own process is listed by name");
            Check(me != null && me.CommandLine != null && me.CommandLine.Contains("SmokeTest"), "Own command line is read");
            Check(me != null && me.Path != null && !me.IsSystem, "Own process has a path and is not flagged as a Windows process");
            Check(me != null && Killer.BlockReason(me) != null, "Killing ourselves is refused");
            var skipped = new List<string>();
            Check(Killer.Expand(snap, new[] { 4 }, true, skipped).Count == 0 && skipped.Count == 1, "The System process is refused");

            TestTreeKill();
            TestPidReuseGuard();

            client.Close();
            accepted.Close();
            tcp4.Stop();
            if (tcp6 != null) tcp6.Stop();
            udp.Close();
            Console.WriteLine(failures == 0 ? "All checks passed." : failures + " check(s) failed.");
            return failures;
        }

        static void TestTreeKill()
        {
            using (var shell = StartPing())
            {
                try
                {
                    Snapshot snap = null;
                    ProcInfo child = null;
                    for (int i = 0; i < 50 && child == null; i++)
                    {
                        Thread.Sleep(100);
                        snap = Collector.Capture();
                        child = snap.Processes.Values.FirstOrDefault(p => p.ParentPid == shell.Id && p.Name.Equals("PING.EXE", StringComparison.OrdinalIgnoreCase));
                    }
                    Check(child != null, "Child process is linked to its parent");
                    if (child == null) return;

                    var targets = Killer.Expand(snap, new[] { shell.Id }, true, new List<string>());
                    Check(targets.Count >= 2 && targets[0].Pid == shell.Id && targets.Any(t => t.Pid == child.Pid), "Tree expansion lists the parent first, then its children");
                    Check(targets.All(t => Killer.Kill(t) == null), "Every process in the tree is killed without errors");
                    Check(shell.WaitForExit(3000) && !IsRunning(child), "Parent and child are gone");
                }
                finally { Cleanup(shell); }
            }
        }

        static void TestPidReuseGuard()
        {
            using (var shell = StartPing())
            {
                try
                {
                    var stale = new ProcInfo { Pid = shell.Id, Name = "cmd.exe", Started = DateTime.Now.AddHours(-1) };
                    Check(Killer.Kill(stale) != null && !shell.HasExited, "A PID that now belongs to a newer process is not killed");
                }
                finally { Cleanup(shell); }
            }
        }

        static Process StartPing()
        {
            return Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true });
        }

        static void Cleanup(Process shell)
        {
            if (shell.HasExited) return;
            foreach (var p in Killer.Expand(Collector.Capture(), new[] { shell.Id }, true, new List<string>())) Killer.Kill(p);
            if (!shell.HasExited) shell.Kill();
        }

        static bool IsRunning(ProcInfo p)
        {
            try
            {
                using (var proc = Process.GetProcessById(p.Pid))
                    return !proc.HasExited && (!p.Started.HasValue || Math.Abs((proc.StartTime - p.Started.Value).TotalSeconds) < 2);
            }
            catch (ArgumentException) { return false; }
        }

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
            if (!ok) failures++;
        }
    }
}
