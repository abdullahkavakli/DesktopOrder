// DesktopOrder: reorder Windows 10 virtual desktops by drag and drop.
//
// Windows 10 can't move a virtual desktop, so a "reorder" here moves every window and each
// desktop's name into its new slot (the same trick MScholtes/VirtualDesktop uses for /SwapDesktop).
// The COM declarations are Explorer's undocumented interfaces for Windows 10 1809-22H2,
// taken from MScholtes/VirtualDesktop (VirtualDesktop.cs).
//
//   DesktopOrder.exe             open the reorder window
//   DesktopOrder.exe --list      print desktops and window counts (-v also lists the windows)
//   DesktopOrder.exe --selftest  swap two throwaway desktops and check the result

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DesktopOrder
{
    #region COM interfaces

    // Only handed back to Explorer, never called, so its methods are left out.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIInspectable), Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513")]
    interface IApplicationView
    {
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
    interface IApplicationViewCollection
    {
        [PreserveSig] int GetViews(out IObjectArray array);
        [PreserveSig] int GetViewsByZOrder(out IObjectArray array);
        [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IObjectArray array);
        [PreserveSig] int GetViewForHwnd(IntPtr hwnd, out IApplicationView view);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("FF72FFDD-BE7E-43FC-9C03-AD81681E88E4")]
    interface IVirtualDesktop
    {
        bool IsViewVisible(IApplicationView view);
        Guid GetId();
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F31574D6-B682-4CDC-BD56-1827860ABEC6")]
    interface IVirtualDesktopManagerInternal
    {
        int GetCount();
        void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
        bool CanViewMoveDesktops(IApplicationView view);
        IVirtualDesktop GetCurrentDesktop();
        void GetDesktops(out IObjectArray desktops);
        [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
        void SwitchDesktop(IVirtualDesktop desktop);
        IVirtualDesktop CreateDesktop();
        void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
        IVirtualDesktop FindDesktop(ref Guid desktopId);
    }

    // Same as IVirtualDesktopManagerInternal plus SetName (Windows 10 2004 and later).
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0F3A72B0-4566-487E-9A33-4ED302F6D6CE")]
    interface IVirtualDesktopManagerInternal2
    {
        int GetCount();
        void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
        bool CanViewMoveDesktops(IApplicationView view);
        IVirtualDesktop GetCurrentDesktop();
        void GetDesktops(out IObjectArray desktops);
        [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
        void SwitchDesktop(IVirtualDesktop desktop);
        IVirtualDesktop CreateDesktop();
        void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
        IVirtualDesktop FindDesktop(ref Guid desktopId);
        void Unknown1(IVirtualDesktop desktop, out IntPtr unknown1, out IntPtr unknown2);
        void SetName(IVirtualDesktop desktop, [MarshalAs(UnmanagedType.HString)] string name);
    }

    // The public, documented interface.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out bool onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    interface IObjectArray
    {
        void GetCount(out int count);
        void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    interface IServiceProvider10
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    #endregion

    // Fresh handles to Explorer's desktop manager. Made per operation so an Explorer restart can't leave us stale.
    class Shell
    {
        static readonly Guid CLSID_ImmersiveShell = new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239");
        static readonly Guid CLSID_VirtualDesktopManagerInternal = new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
        static readonly Guid CLSID_VirtualDesktopManager = new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A");

        public readonly IVirtualDesktopManagerInternal Internal;
        public readonly IVirtualDesktopManagerInternal2 Internal2;  // null when this Windows build can't rename desktops
        public readonly IVirtualDesktopManager Manager;
        public readonly IApplicationViewCollection Views;

        public Shell()
        {
            var provider = (IServiceProvider10)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ImmersiveShell));
            Internal = (IVirtualDesktopManagerInternal)Query(provider, CLSID_VirtualDesktopManagerInternal, typeof(IVirtualDesktopManagerInternal).GUID);
            try { Internal2 = (IVirtualDesktopManagerInternal2)Query(provider, CLSID_VirtualDesktopManagerInternal, typeof(IVirtualDesktopManagerInternal2).GUID); }
            catch (Exception) { Internal2 = null; }
            Manager = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_VirtualDesktopManager));
            Views = (IApplicationViewCollection)Query(provider, typeof(IApplicationViewCollection).GUID, typeof(IApplicationViewCollection).GUID);
        }

        static object Query(IServiceProvider10 provider, Guid service, Guid iid)
        {
            return provider.QueryService(ref service, ref iid);
        }
    }

    class DesktopInfo
    {
        public IVirtualDesktop Desktop;
        public Guid Id;
        public int Index;            // position when the snapshot was taken
        public string Name;          // "" when the user never named it
        public int VisibleWindows;   // windows a person would see in Alt+Tab

        public string Label(int position) { return Name.Length > 0 ? Name : "Desktop " + (position + 1); }
    }

    class WindowInfo
    {
        public IntPtr Hwnd;
        public IApplicationView View;
        public int Desktop;          // index into the desktop list
        public bool Visible;
    }

    static class VirtualDesktops
    {
        const string NamesKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops\";

        public static List<DesktopInfo> GetDesktops(Shell shell)
        {
            IObjectArray array;
            shell.Internal.GetDesktops(out array);
            int count;
            array.GetCount(out count);
            Guid iid = typeof(IVirtualDesktop).GUID;
            var result = new List<DesktopInfo>();
            for (int i = 0; i < count; i++)
            {
                object obj;
                array.GetAt(i, ref iid, out obj);
                var desktop = (IVirtualDesktop)obj;
                Guid id = desktop.GetId();
                result.Add(new DesktopInfo { Desktop = desktop, Id = id, Index = i, Name = ReadName(id) });
            }
            return result;
        }

        public static string ReadName(Guid id)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(NamesKey + id.ToString("B")))
            {
                string name = key == null ? null : key.GetValue("Name") as string;
                return name ?? "";
            }
        }

        public static int CurrentIndex(Shell shell, List<DesktopInfo> desktops)
        {
            Guid id = shell.Internal.GetCurrentDesktop().GetId();
            return desktops.FindIndex(d => d.Id == id);
        }

        // Every window that lives on exactly one desktop. Windows pinned to all desktops report a
        // special desktop id that isn't in the list, so they are skipped without a separate check.
        public static List<WindowInfo> GetWindows(Shell shell, List<DesktopInfo> desktops)
        {
            var index = new Dictionary<Guid, int>();
            for (int i = 0; i < desktops.Count; i++) index[desktops[i].Id] = i;

            var result = new List<WindowInfo>();
            var className = new StringBuilder(256);
            Native.EnumWindowsProc callback = (hwnd, lParam) =>
            {
                Guid id;
                int desktop;
                if (shell.Manager.GetWindowDesktopId(hwnd, out id) != 0 || !index.TryGetValue(id, out desktop))
                    return true;
                // Start, Search and Action Center are top-level CoreWindows. Leave the shell alone.
                className.Length = 0;
                Native.GetClassName(hwnd, className, className.Capacity);
                if (className.ToString() == "Windows.UI.Core.CoreWindow")
                    return true;
                IApplicationView view;
                if (shell.Views.GetViewForHwnd(hwnd, out view) != 0 || view == null)
                    return true;
                bool visible = Native.IsAltTabWindow(hwnd) && !Native.IsOwnWindow(hwnd);
                result.Add(new WindowInfo { Hwnd = hwnd, View = view, Desktop = desktop, Visible = visible });
                return true;
            };
            Native.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return result;
        }

        // Puts the desktops in the given order (desktop ids, first to last). Windows and names move;
        // the user ends up on whichever slot their current desktop moved to.
        // Returns how many visible windows could not be moved.
        public static int ApplyOrder(IList<Guid> order)
        {
            var shell = new Shell();
            if (shell.Internal2 == null)
                throw new NotSupportedException("This version of Windows can't rename desktops, so names couldn't follow their desktops.");

            List<DesktopInfo> desktops = GetDesktops(shell);
            int n = desktops.Count;
            if (order.Count != n)
                throw DesktopsChanged();
            var target = new int[n];  // current index -> new index
            for (int i = 0; i < n; i++)
            {
                target[i] = order.IndexOf(desktops[i].Id);
                if (target[i] < 0)
                    throw DesktopsChanged();
            }
            if (Enumerable.Range(0, n).All(i => target[i] == i))
                return 0;

            List<WindowInfo> windows = GetWindows(shell, desktops);
            int current = CurrentIndex(shell, desktops);
            bool follow = current >= 0 && target[current] != current;
            IntPtr foreground = Native.GetForegroundWindow();
            int failed = 0;

            // The current desktop's windows go first and we follow them right away,
            // so the user's own windows are gone for only a moment.
            if (follow)
            {
                foreach (WindowInfo w in windows)
                    if (w.Desktop == current && !Move(shell, w, desktops[target[current]]) && w.Visible)
                        failed++;
                shell.Internal.SwitchDesktop(desktops[target[current]].Desktop);
            }
            foreach (WindowInfo w in windows)
                if (w.Desktop != current && target[w.Desktop] != w.Desktop && !Move(shell, w, desktops[target[w.Desktop]]) && w.Visible)
                    failed++;

            // Names travel with their desktops. "" clears a name, so unnamed desktops show as "Desktop N" again.
            for (int i = 0; i < n; i++)
                if (target[i] != i)
                    shell.Internal2.SetName(desktops[target[i]].Desktop, desktops[i].Name);

            if (follow && windows.Any(w => w.Hwnd == foreground && w.Desktop == current))
                Native.SetForegroundWindow(foreground);
            return failed;
        }

        static bool Move(Shell shell, WindowInfo w, DesktopInfo to)
        {
            try
            {
                if (shell.Internal.CanViewMoveDesktops(w.View))
                {
                    shell.Internal.MoveViewToDesktop(w.View, to.Desktop);
                    return true;
                }
            }
            catch (COMException)
            {
            }
            if (!Native.IsWindow(w.Hwnd))
                return true;  // closed in the meantime, nothing left to move
            // Our own windows can always be moved through the public API.
            if (Native.IsOwnWindow(w.Hwnd))
            {
                Guid id = to.Id;
                return shell.Manager.MoveWindowToDesktop(w.Hwnd, ref id) == 0;
            }
            return false;
        }

        static Exception DesktopsChanged()
        {
            return new InvalidOperationException("Desktops were added or removed while this window was open. The list has been reloaded, please try again.");
        }
    }

    static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        const uint GW_OWNER = 4;
        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
        const int DWMWA_CLOAKED = 14, DWM_CLOAKED_APP = 1;

        static readonly uint ownProcessId = (uint)Process.GetCurrentProcess().Id;

        // Roughly the windows Alt+Tab would list.
        public static bool IsAltTabWindow(IntPtr hwnd)
        {
            if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0)
                return false;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero || (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0)
                return false;
            // Windows on other desktops are cloaked by the shell; ones cloaked by their app are hidden UWP frames.
            int cloaked;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && (cloaked & DWM_CLOAKED_APP) != 0)
                return false;
            return true;
        }

        public static bool IsOwnWindow(IntPtr hwnd)
        {
            uint processId;
            GetWindowThreadProcessId(hwnd, out processId);
            return processId == ownProcessId;
        }

        public static string ClassOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string TitleOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }

    class OrderForm : Form
    {
        readonly ListBox list = new ListBox();
        readonly Button upButton = new Button(), downButton = new Button(), applyButton = new Button(), closeButton = new Button();
        readonly Label hint = new Label(), status = new Label();
        readonly Font boldFont;
        readonly float scale;

        int current = -1;
        int dragIndex = -1;  // row being dragged
        Point dragStart;
        int dropGap = -1;    // gap (0..count) where the dragged row would land

        public OrderForm()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                scale = g.DpiX / 96f;

            Text = "Reorder desktops";
            Font = SystemFonts.MessageBoxFont;
            boldFont = new Font(Font, FontStyle.Bold);
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }

            Reload();

            int rowHeight = S(32), buttonWidth = S(104), buttonHeight = S(30), margin = S(12);
            int listHeight = Math.Max(list.Items.Count, 5) * rowHeight + 2 * SystemInformation.Border3DSize.Height + 2;
            ClientSize = new Size(S(460), S(40) + listHeight + S(40));
            int right = ClientSize.Width - margin - buttonWidth;

            hint.Text = "Drag desktops into the order you want, then click Apply.";
            hint.AutoSize = false;
            hint.SetBounds(margin, margin, ClientSize.Width - 2 * margin, S(22));
            hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.ItemHeight = rowHeight;
            list.IntegralHeight = false;
            list.AllowDrop = true;
            list.SetBounds(margin, S(40), right - 2 * margin, listHeight);
            list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            upButton.Text = "Move &up";
            upButton.SetBounds(right, S(40), buttonWidth, buttonHeight);
            upButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            downButton.Text = "Move &down";
            downButton.SetBounds(right, S(40) + buttonHeight + S(6), buttonWidth, buttonHeight);
            downButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            closeButton.Text = "Close";
            closeButton.SetBounds(right, S(40) + listHeight - buttonHeight, buttonWidth, buttonHeight);
            closeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            applyButton.Text = "&Apply";
            applyButton.SetBounds(right, closeButton.Top - buttonHeight - S(6), buttonWidth, buttonHeight);
            applyButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            status.Text = "Tip: Ctrl+Up / Ctrl+Down also moves the selected desktop.";
            status.ForeColor = SystemColors.GrayText;
            status.AutoSize = false;
            status.SetBounds(margin, S(40) + listHeight + S(10), ClientSize.Width - 2 * margin, S(22));
            status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            Controls.AddRange(new Control[] { hint, list, upButton, downButton, applyButton, closeButton, status });
            AcceptButton = applyButton;
            CancelButton = closeButton;
            MinimumSize = Size;

            list.DrawItem += DrawRow;
            list.SelectedIndexChanged += (s, e) => UpdateButtons();
            list.MouseDown += (s, e) => { dragIndex = list.IndexFromPoint(e.Location); dragStart = e.Location; };
            list.MouseUp += (s, e) => { dragIndex = -1; };
            list.MouseMove += OnListMouseMove;
            list.DragOver += OnListDragOver;
            list.DragLeave += (s, e) => SetDropGap(-1);
            list.DragDrop += OnListDragDrop;
            list.KeyDown += OnListKeyDown;
            upButton.Click += (s, e) => MoveSelected(-1);
            downButton.Click += (s, e) => MoveSelected(1);
            applyButton.Click += (s, e) => Apply();
            closeButton.Click += (s, e) => Close();
            UpdateButtons();
        }

        int S(int pixels) { return (int)Math.Round(pixels * scale); }

        void Reload()
        {
            var shell = new Shell();
            List<DesktopInfo> desktops = VirtualDesktops.GetDesktops(shell);
            current = VirtualDesktops.CurrentIndex(shell, desktops);
            foreach (WindowInfo w in VirtualDesktops.GetWindows(shell, desktops))
                if (w.Visible)
                    desktops[w.Desktop].VisibleWindows++;

            list.BeginUpdate();
            list.Items.Clear();
            foreach (DesktopInfo d in desktops)
                list.Items.Add(d);
            list.SelectedIndex = current;
            list.EndUpdate();
        }

        void Apply()
        {
            List<Guid> order = list.Items.Cast<DesktopInfo>().Select(d => d.Id).ToList();
            UseWaitCursor = true;
            try
            {
                int failed = VirtualDesktops.ApplyOrder(order);
                Reload();
                status.Text = failed == 0
                    ? "Done. Desktops reordered."
                    : failed + (failed == 1 ? " window" : " windows") + " couldn't be moved. Try running as administrator.";
            }
            catch (Exception ex)
            {
                try { Reload(); }
                catch (Exception) { }
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                UseWaitCursor = false;
                UpdateButtons();
            }
        }

        void UpdateButtons()
        {
            int i = list.SelectedIndex;
            upButton.Enabled = i > 0;
            downButton.Enabled = i >= 0 && i < list.Items.Count - 1;
            applyButton.Enabled = Enumerable.Range(0, list.Items.Count).Any(k => ((DesktopInfo)list.Items[k]).Index != k);
        }

        void MoveSelected(int delta)
        {
            MoveItem(list.SelectedIndex, list.SelectedIndex + delta);
        }

        void MoveItem(int from, int to)
        {
            if (from < 0 || from >= list.Items.Count || to < 0 || to >= list.Items.Count || from == to)
                return;
            object item = list.Items[from];
            list.BeginUpdate();
            list.Items.RemoveAt(from);
            list.Items.Insert(to, item);
            list.SelectedIndex = to;
            list.EndUpdate();
            UpdateButtons();
        }

        void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down))
            {
                MoveSelected(e.KeyCode == Keys.Up ? -1 : 1);
                e.SuppressKeyPress = true;
            }
        }

        void OnListMouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || dragIndex < 0)
                return;
            Size threshold = SystemInformation.DragSize;
            if (Math.Abs(e.X - dragStart.X) < threshold.Width && Math.Abs(e.Y - dragStart.Y) < threshold.Height)
                return;
            list.DoDragDrop(list.Items[dragIndex], DragDropEffects.Move);
            dragIndex = -1;
            SetDropGap(-1);
        }

        void OnListDragOver(object sender, DragEventArgs e)
        {
            e.Effect = dragIndex >= 0 ? DragDropEffects.Move : DragDropEffects.None;
            SetDropGap(dragIndex >= 0 ? GapAt(new Point(e.X, e.Y)) : -1);
        }

        void OnListDragDrop(object sender, DragEventArgs e)
        {
            int gap = GapAt(new Point(e.X, e.Y));
            SetDropGap(-1);
            if (dragIndex >= 0)
                MoveItem(dragIndex, gap > dragIndex ? gap - 1 : gap);
        }

        int GapAt(Point screenPoint)
        {
            Point p = list.PointToClient(screenPoint);
            int gap = list.TopIndex + (p.Y + list.ItemHeight / 2) / list.ItemHeight;
            return Math.Max(0, Math.Min(list.Items.Count, gap));
        }

        void SetDropGap(int gap)
        {
            if (gap == dropGap)
                return;
            dropGap = gap;
            list.Invalidate();
        }

        void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0)
                return;
            var d = (DesktopInfo)list.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Color fore = selected ? SystemColors.HighlightText : SystemColors.WindowText;
            Color dim = selected ? SystemColors.HighlightText : SystemColors.GrayText;
            Rectangle r = e.Bounds;
            Graphics g = e.Graphics;
            using (var back = new SolidBrush(selected ? SystemColors.Highlight : SystemColors.Window))
                g.FillRectangle(back, r);

            const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, (e.Index + 1).ToString(), Font, new Rectangle(r.X, r.Y, S(30), r.Height), dim, flags | TextFormatFlags.Right);

            string detail = (d.Index == current ? "current · " : "") + (d.VisibleWindows == 1 ? "1 window" : d.VisibleWindows + " windows");
            int detailWidth = TextRenderer.MeasureText(g, detail, Font, Size.Empty, flags).Width;
            var detailRect = new Rectangle(r.Right - detailWidth - S(8), r.Y, detailWidth, r.Height);
            TextRenderer.DrawText(g, detail, Font, detailRect, dim, flags | TextFormatFlags.Right);

            Font nameFont = d.Index == current ? boldFont : Font;
            string name = d.Label(d.Index);
            var nameRect = new Rectangle(r.X + S(40), r.Y, Math.Max(0, detailRect.Left - r.X - S(48)), r.Height);
            TextRenderer.DrawText(g, name, nameFont, nameRect, fore, flags | TextFormatFlags.EndEllipsis);
            // Unnamed desktops are numbered by position, so say what this one will be called.
            if (d.Name.Length == 0 && e.Index != d.Index)
            {
                int nameWidth = TextRenderer.MeasureText(g, name, nameFont, Size.Empty, flags).Width;
                var noteRect = new Rectangle(nameRect.X + nameWidth, r.Y, Math.Max(0, nameRect.Width - nameWidth), r.Height);
                TextRenderer.DrawText(g, "→ Desktop " + (e.Index + 1), Font, noteRect, dim, flags | TextFormatFlags.EndEllipsis);
            }

            bool lineAbove = dropGap == e.Index;
            bool lineBelow = dropGap == list.Items.Count && e.Index == list.Items.Count - 1;
            if (lineAbove || lineBelow)
            {
                using (var line = new SolidBrush(selected ? SystemColors.HighlightText : SystemColors.Highlight))
                    g.FillRectangle(line, r.X, lineAbove ? r.Top : r.Bottom - S(3), r.Width, S(3));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                boldFont.Dispose();
            base.Dispose(disposing);
        }
    }

    static class Cli
    {
        public static int List(bool verbose)
        {
            var shell = new Shell();
            List<DesktopInfo> desktops = VirtualDesktops.GetDesktops(shell);
            int current = VirtualDesktops.CurrentIndex(shell, desktops);
            List<WindowInfo> windows = VirtualDesktops.GetWindows(shell, desktops);
            Console.WriteLine("Can rename desktops: " + (shell.Internal2 != null));
            for (int i = 0; i < desktops.Count; i++)
            {
                DesktopInfo d = desktops[i];
                Console.WriteLine("{0}  {1,-16} {2}  visible windows: {3,2}  all views: {4,2}{5}",
                    i + 1, d.Label(i) + (d.Name.Length == 0 ? "*" : ""), d.Id.ToString("B"),
                    windows.Count(w => w.Desktop == i && w.Visible), windows.Count(w => w.Desktop == i),
                    i == current ? "  (current)" : "");
            }
            Console.WriteLine("* = unnamed");
            if (verbose)
                foreach (WindowInfo w in windows.OrderBy(w => w.Desktop))
                    Console.WriteLine("  {0} {1} {2,-34} {3}", w.Desktop + 1, w.Visible ? "V" : " ", Native.ClassOf(w.Hwnd), Native.TitleOf(w.Hwnd));
            return 0;
        }

        // Works only on two throwaway desktops, so the real ones are never touched.
        public static int SelfTest()
        {
            var shell = new Shell();
            IVirtualDesktop home = shell.Internal.GetCurrentDesktop();
            IVirtualDesktop a = shell.Internal.CreateDesktop();
            IVirtualDesktop b = shell.Internal.CreateDesktop();
            Guid aId = a.GetId(), bId = b.GetId();
            Process notepad = null;
            bool pass = false;
            try
            {
                shell.Internal2.SetName(a, "vdtest A");  // b stays unnamed, which checks that names get cleared
                notepad = Process.Start(new ProcessStartInfo("notepad.exe") { WindowStyle = ProcessWindowStyle.Minimized });
                IntPtr hwnd = WaitForMainWindow(notepad);
                IApplicationView view;
                Require(shell.Views.GetViewForHwnd(hwnd, out view) == 0, "got Notepad's view");
                shell.Internal.MoveViewToDesktop(view, a);
                Require(DesktopOf(shell, hwnd) == aId, "Notepad is on test desktop A");
                Require(VirtualDesktops.ReadName(aId) == "vdtest A", "test desktop A is named");

                List<Guid> order = VirtualDesktops.GetDesktops(shell).Select(d => d.Id).ToList();
                int ia = order.IndexOf(aId), ib = order.IndexOf(bId);
                order[ia] = bId;
                order[ib] = aId;
                int failed = VirtualDesktops.ApplyOrder(order);
                Thread.Sleep(500);

                pass = Check(failed == 0, "no window failed to move")
                     & Check(DesktopOf(shell, hwnd) == bId, "Notepad moved with its desktop into the other slot")
                     & Check(VirtualDesktops.ReadName(bId) == "vdtest A", "the name moved with it")
                     & Check(VirtualDesktops.ReadName(aId) == "", "the unnamed desktop's name was cleared")
                     & Check(VirtualDesktops.CurrentIndex(shell, VirtualDesktops.GetDesktops(shell)) >= 0
                             && shell.Internal.GetCurrentDesktop().GetId() == home.GetId(), "still on the starting desktop");
            }
            finally
            {
                if (notepad != null && !notepad.HasExited)
                {
                    notepad.Kill();
                    notepad.WaitForExit(3000);
                }
                shell.Internal.RemoveDesktop(b, home);
                shell.Internal.RemoveDesktop(a, home);
            }
            Console.WriteLine(pass ? "PASS" : "FAIL");
            return pass ? 0 : 1;
        }

        static IntPtr WaitForMainWindow(Process process)
        {
            for (int i = 0; i < 100; i++)
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                    return process.MainWindowHandle;
                Thread.Sleep(100);
            }
            throw new TimeoutException("Notepad's window never appeared.");
        }

        static Guid DesktopOf(Shell shell, IntPtr hwnd)
        {
            Guid id;
            shell.Manager.GetWindowDesktopId(hwnd, out id);
            return id;
        }

        static bool Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
            return ok;
        }

        static void Require(bool ok, string what)
        {
            if (!Check(ok, what))
                throw new Exception("Setup failed: " + what);
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && args[0] == "--list")
                    return Cli.List(args.Contains("-v"));
                if (args.Length > 0 && args[0] == "--selftest")
                    return Cli.SelfTest();

                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new OrderForm());
                return 0;
            }
            catch (Exception ex)
            {
                if (args.Length > 0)
                {
                    Console.Error.WriteLine(ex);
                    return 1;
                }
                MessageBox.Show("Couldn't reach the Windows desktop manager:\n\n" + ex.Message, "Reorder desktops", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
