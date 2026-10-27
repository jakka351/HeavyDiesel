// =============================================================================
//  MainForm.cs - the whole bench UI, built in code (no designer file).
//
//  WHAT IT DOES
//  ------------
//  1. Enumerates every RP1210 adapter registered on this PC (INI files only, no
//     hardware touched) and lets you pick vendor / device / protocol string.
//  2. Connects through Rp1210Provider and, by default, runs a J1939Session: a
//     J1939-81 address claim followed by full J1939-21 transport reassembly, so
//     multi-packet DM1 lists and PGN requests work.
//  3. Shows the bus as a network: one row per source address, its NAME when it
//     announced one, message rate, and the PGNs it transmits with payloads and
//     periods. A second tab shows the DM1/DM2 fault picture per ECU, aged so a
//     lamp that stopped being broadcast stops being shown as lit.
//
//  THREADING RULE
//  --------------
//  Every event from the stack arrives on a worker thread. Handlers here write
//  ONLY to thread-safe model objects (J1939NetworkMap, J1939FaultTracker, the
//  locked log queue). A 250 ms WinForms timer then pulls snapshots and paints.
//  No Invoke/BeginInvoke in the handlers, so a stalled UI can never stall the
//  bus pump - which is the condition that tears down a live transport session.
//
//  CLOCK RULE
//  ----------
//  Observe and Evaluate on the map and the tracker must use ONE clock. In an
//  active session that is the session clock (message SessionClockMs in,
//  Session.ElapsedMs out); in passive mode it is this form's Stopwatch at frame
//  arrival. Never DateTime.Now.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using HeavyDiesel.J1939;
using HeavyDiesel.Rp1210;
using HeavyDiesel.Transport;
using HeavyDiesel.Transport.Simulation;

namespace HeavyDiesel.Bench
{
    internal sealed class MainForm : Form
    {
        private const int REFRESH_INTERVAL_MS = 250;
        private const int MAX_LOG_LINES = 4000;

        // ---- model ------------------------------------------------------------
        private readonly BenchSettings _settings;
        private readonly J1939SpnCatalog _catalogue = new J1939SpnCatalog();
        private readonly J1939NetworkMap _network = new J1939NetworkMap();
        private readonly J1939FaultTracker _faults;
        private readonly J1939EngineState _engine = new J1939EngineState();
        private readonly Stopwatch _uiClock = Stopwatch.StartNew();

        private readonly object _logGate = new object();
        private readonly Queue<string> _logQueue = new Queue<string>();
        private int _logLineCount;

        private readonly object _passiveNameGate = new object();
        private readonly Dictionary<byte, J1939Name> _passiveNames = new Dictionary<byte, J1939Name>();

        private List<Rp1210VendorInfo> _vendors = new List<Rp1210VendorInfo>();
        private IJ1939Link _link;                 // whichever of the two below is live
        private Rp1210Provider _provider;         // real adapter, or null
        private SimulatedJ1939Link _sim;          // virtual truck, or null
        private J1939Session _session;
        private bool _active;
        private bool _passiveMultiFrameWarned;
        private volatile bool _logEveryFrame;
        private Func<double> _now;

        // ---- controls ---------------------------------------------------------
        private ComboBox _vendorCombo;
        private ComboBox _deviceCombo;
        private ComboBox _protocolCombo;
        private CheckBox _claimCheck;
        private Button _refreshButton;
        private Button _connectButton;
        private Button _disconnectButton;

        private TabControl _tabs;
        private Label _networkHeadline;
        private ListView _nodeList;
        private ListView _pgnList;
        private Label _faultHeadline;
        private ListView _faultList;
        private TextBox _faultDetail;
        private TextBox _adapterText;
        private TextBox _logText;
        private CheckBox _logFramesCheck;

        private ToolStripStatusLabel _statusLink;
        private ToolStripStatusLabel _statusClaim;
        private ToolStripStatusLabel _statusEngine;
        private ToolStripStatusLabel _statusCounters;

        private ToolStripMenuItem _requestsMenu;
        private ToolStripMenuItem _connectMenu;
        private ToolStripMenuItem _disconnectMenu;

        private readonly Dictionary<byte, ListViewItem> _nodeRows = new Dictionary<byte, ListViewItem>();
        private readonly Dictionary<uint, ListViewItem> _pgnRows = new Dictionary<uint, ListViewItem>();
        private readonly Dictionary<byte, ListViewItem> _faultRows = new Dictionary<byte, ListViewItem>();
        private int _pgnRowsForAddress = -1;
        private Timer _timer;

        public MainForm()
        {
            _settings = BenchSettings.Load();
            _faults = new J1939FaultTracker(ResolveSpnName);
            _network.PgnNameResolver = ResolvePgnName;
            _now = UiClockNow;

            BuildUi();
            RefreshAdapters();
            RestoreSelection();

            foreach (string line in _settings.DescribeIdentity())
            {
                EnqueueLog(line);
            }

            _timer = new Timer();
            _timer.Interval = REFRESH_INTERVAL_MS;
            _timer.Tick += delegate { RefreshViews(); };
            _timer.Start();
        }

        // =====================================================================
        //  UI construction
        // =====================================================================

        private void BuildUi()
        {
            Text = "HeavyDiesel Bench - RP1210 / J1939";
            StartPosition = FormStartPosition.WindowsDefaultLocation;
            Size = new Size(1180, 760);
            MinimumSize = new Size(900, 560);

            MenuStrip menu = BuildMenu();
            Panel top = BuildAdapterPanel();
            _tabs = BuildTabs();
            StatusStrip status = BuildStatusStrip();

            // Dock order matters: last added docks first.
            Controls.Add(_tabs);
            Controls.Add(top);
            Controls.Add(status);
            Controls.Add(menu);
            MainMenuStrip = menu;
        }

        private MenuStrip BuildMenu()
        {
            MenuStrip menu = new MenuStrip();

            ToolStripMenuItem file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add("E&xit", null, delegate { Close(); });

            ToolStripMenuItem adapter = new ToolStripMenuItem("&Adapter");
            adapter.DropDownItems.Add("&Refresh adapter list", null, delegate { RefreshAdapters(); });
            _connectMenu = new ToolStripMenuItem("&Connect", null, delegate { Connect(); });
            _disconnectMenu = new ToolStripMenuItem("&Disconnect", null, delegate { Disconnect(); });
            _disconnectMenu.Enabled = false;
            adapter.DropDownItems.Add(_connectMenu);
            adapter.DropDownItems.Add(_disconnectMenu);

            _requestsMenu = new ToolStripMenuItem("&Requests");
            AddRequest("Address Claimed (60928) - re-map who is on the bus", J1939Pgn.AddressClaimed);
            _requestsMenu.DropDownItems.Add(new ToolStripSeparator());
            AddRequest("VIN (65260)", 65260);
            AddRequest("Component ID (65259) - make / model / serial", 65259);
            AddRequest("Software ID (65242)", 65242);
            AddRequest("ECU ID (64965)", 64965);
            _requestsMenu.DropDownItems.Add(new ToolStripSeparator());
            AddRequest("DM2 - previously active DTCs (65227)", J1939Pgn.Dm2);
            AddRequest("DM5 - diagnostic readiness (65230)", J1939Pgn.Dm5);
            AddRequest("DM19 - calibration ID / CVN (54016)", J1939Pgn.Dm19);
            _requestsMenu.Enabled = false;

            ToolStripMenuItem view = new ToolStripMenuItem("&View");
            view.DropDownItems.Add("&Clear network and fault views", null, delegate { ClearViews(); });

            ToolStripMenuItem catalogue = new ToolStripMenuItem("&Catalogue");
            catalogue.DropDownItems.Add("&Load SPN / PGN names from CSV...", null, delegate { LoadCatalogue(); });
            catalogue.DropDownItems.Add("&Clear loaded names", null, delegate
            {
                _catalogue.Clear();
                EnqueueLog("Catalogue cleared. PGNs show by number; protocol PGNs still name themselves.");
            });

            ToolStripMenuItem help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add("&About HeavyDiesel Bench", null, delegate { ShowAbout(); });

            menu.Items.Add(file);
            menu.Items.Add(adapter);
            menu.Items.Add(_requestsMenu);
            menu.Items.Add(view);
            menu.Items.Add(catalogue);
            menu.Items.Add(help);
            return menu;
        }

        private void AddRequest(string caption, uint pgn)
        {
            uint captured = pgn;
            _requestsMenu.DropDownItems.Add(caption, null, delegate { SendRequest(captured); });
        }

        private Panel BuildAdapterPanel()
        {
            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Top;
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grid.Padding = new Padding(6, 4, 6, 2);
            grid.ColumnCount = 8;
            grid.RowCount = 2;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _vendorCombo = MakeCombo();
            _vendorCombo.SelectedIndexChanged += delegate { PopulateDevices(); };
            _deviceCombo = MakeCombo();
            _deviceCombo.SelectedIndexChanged += delegate { PopulateProtocols(); };
            _protocolCombo = MakeCombo();

            _refreshButton = new Button();
            _refreshButton.Text = "Refresh";
            _refreshButton.AutoSize = true;
            _refreshButton.Click += delegate { RefreshAdapters(); };

            _connectButton = new Button();
            _connectButton.Text = "Connect";
            _connectButton.AutoSize = true;
            _connectButton.Click += delegate { Connect(); };

            _disconnectButton = new Button();
            _disconnectButton.Text = "Disconnect";
            _disconnectButton.AutoSize = true;
            _disconnectButton.Enabled = false;
            _disconnectButton.Click += delegate { Disconnect(); };

            _claimCheck = new CheckBox();
            _claimCheck.Text = "Claim a source address (J1939-81). Transmits; enables multi-packet reassembly and PGN requests. Off = listen only.";
            _claimCheck.AutoSize = true;
            _claimCheck.Checked = _settings.ClaimAddress;

            grid.Controls.Add(MakeLabel("Vendor API"), 0, 0);
            grid.Controls.Add(_vendorCombo, 1, 0);
            grid.Controls.Add(MakeLabel("Device"), 2, 0);
            grid.Controls.Add(_deviceCombo, 3, 0);
            grid.Controls.Add(MakeLabel("Protocol"), 4, 0);
            grid.Controls.Add(_protocolCombo, 5, 0);
            grid.Controls.Add(_refreshButton, 6, 0);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.Margin = new Padding(0);
            buttons.Controls.Add(_connectButton);
            buttons.Controls.Add(_disconnectButton);
            grid.Controls.Add(buttons, 7, 0);

            grid.Controls.Add(_claimCheck, 0, 1);
            grid.SetColumnSpan(_claimCheck, 8);

            Panel host = new Panel();
            host.Dock = DockStyle.Top;
            host.AutoSize = true;
            host.Controls.Add(grid);
            return host;
        }

        private static ComboBox MakeCombo()
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(3, 3, 9, 3);
            return c;
        }

        private static Label MakeLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Left;
            l.Margin = new Padding(3, 6, 3, 3);
            return l;
        }

        private TabControl BuildTabs()
        {
            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;

            // ---- Network ----
            TabPage network = new TabPage("Network");
            _networkHeadline = new Label();
            _networkHeadline.Dock = DockStyle.Top;
            _networkHeadline.AutoSize = false;
            _networkHeadline.Height = 22;
            _networkHeadline.TextAlign = ContentAlignment.MiddleLeft;
            _networkHeadline.Padding = new Padding(4, 0, 0, 0);
            _networkHeadline.Text = "Not connected.";

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Vertical;
            // Size before SplitterDistance: on a not-yet-laid-out container the default
            // 150 px width makes any sensible distance throw.
            split.Size = new Size(1140, 600);
            split.SplitterDistance = 500;
            split.FixedPanel = FixedPanel.Panel1;

            _nodeList = MakeListView();
            AddColumns(_nodeList,
                new string[] { "SA", "Node", "Func", "Mfg", "Inst", "Msgs", "Msg/s", "PGNs", "Last seen", "State" },
                new int[] { 50, 170, 50, 50, 44, 70, 60, 50, 90, 70 });
            _nodeList.SelectedIndexChanged += delegate { _pgnRowsForAddress = -1; };

            _pgnList = MakeListView();
            AddColumns(_pgnList,
                new string[] { "PGN", "Hex", "Name", "Count", "Period ms", "Last ms", "DA", "Len", "Payload" },
                new int[] { 60, 60, 190, 60, 72, 62, 40, 40, 300 });
            _pgnList.Font = new Font(FontFamily.GenericMonospace, 8.5f);

            split.Panel1.Controls.Add(_nodeList);
            split.Panel2.Controls.Add(_pgnList);
            network.Controls.Add(split);
            network.Controls.Add(_networkHeadline);

            // ---- Faults ----
            TabPage faults = new TabPage("Faults (DM1 / DM2)");
            _faultHeadline = new Label();
            _faultHeadline.Dock = DockStyle.Top;
            _faultHeadline.AutoSize = false;
            _faultHeadline.Height = 22;
            _faultHeadline.TextAlign = ContentAlignment.MiddleLeft;
            _faultHeadline.Padding = new Padding(4, 0, 0, 0);
            _faultHeadline.Text = "Not connected.";

            SplitContainer faultSplit = new SplitContainer();
            faultSplit.Dock = DockStyle.Fill;
            faultSplit.Orientation = Orientation.Horizontal;
            faultSplit.Size = new Size(1140, 600);
            faultSplit.SplitterDistance = 240;
            faultSplit.FixedPanel = FixedPanel.Panel1;

            _faultList = MakeListView();
            AddColumns(_faultList,
                new string[] { "SA", "State", "Lamps", "Active", "Prev", "DM1 age", "DM1 count" },
                new int[] { 50, 110, 420, 60, 60, 80, 80 });
            _faultList.SelectedIndexChanged += delegate { RefreshFaultDetail(); };

            _faultDetail = MakeTextBox();
            faultSplit.Panel1.Controls.Add(_faultList);
            faultSplit.Panel2.Controls.Add(_faultDetail);
            faults.Controls.Add(faultSplit);
            faults.Controls.Add(_faultHeadline);

            // ---- Adapter ----
            TabPage adapter = new TabPage("Adapter");
            _adapterText = MakeTextBox();
            adapter.Controls.Add(_adapterText);

            // ---- Log ----
            TabPage log = new TabPage("Log");
            _logText = MakeTextBox();
            _logFramesCheck = new CheckBox();
            _logFramesCheck.Text = "Log every frame (floods on a live bus - use for short captures)";
            _logFramesCheck.AutoSize = true;
            _logFramesCheck.Dock = DockStyle.Top;
            _logFramesCheck.Padding = new Padding(4, 2, 0, 2);
            _logFramesCheck.CheckedChanged += delegate { _logEveryFrame = _logFramesCheck.Checked; };
            log.Controls.Add(_logText);
            log.Controls.Add(_logFramesCheck);

            tabs.TabPages.Add(network);
            tabs.TabPages.Add(faults);
            tabs.TabPages.Add(adapter);
            tabs.TabPages.Add(log);
            return tabs;
        }

        private static ListView MakeListView()
        {
            ListView lv = new ListView();
            lv.Dock = DockStyle.Fill;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.HideSelection = false;
            lv.GridLines = true;
            lv.MultiSelect = false;

            // Flicker-free repaint on the 250 ms tick. DoubleBuffered is protected on
            // ListView; this is the standard way to reach it without subclassing.
            PropertyInfo p = typeof(ListView).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null) p.SetValue(lv, true, null);
            return lv;
        }

        private static void AddColumns(ListView lv, string[] names, int[] widths)
        {
            for (int i = 0; i < names.Length; i++)
            {
                lv.Columns.Add(names[i], widths[i]);
            }
        }

        private static TextBox MakeTextBox()
        {
            TextBox t = new TextBox();
            t.Dock = DockStyle.Fill;
            t.Multiline = true;
            t.ReadOnly = true;
            t.ScrollBars = ScrollBars.Both;
            t.WordWrap = false;
            t.Font = new Font(FontFamily.GenericMonospace, 9f);
            t.BackColor = SystemColors.Window;
            return t;
        }

        private StatusStrip BuildStatusStrip()
        {
            StatusStrip strip = new StatusStrip();
            _statusLink = new ToolStripStatusLabel("Not connected");
            _statusLink.BorderSides = ToolStripStatusLabelBorderSides.Right;
            _statusClaim = new ToolStripStatusLabel("J1939: idle");
            _statusClaim.BorderSides = ToolStripStatusLabelBorderSides.Right;
            _statusEngine = new ToolStripStatusLabel("Engine: -");
            _statusEngine.BorderSides = ToolStripStatusLabelBorderSides.Right;
            _statusCounters = new ToolStripStatusLabel(string.Empty);
            _statusCounters.Spring = true;
            _statusCounters.TextAlign = ContentAlignment.MiddleRight;
            strip.Items.Add(_statusLink);
            strip.Items.Add(_statusClaim);
            strip.Items.Add(_statusEngine);
            strip.Items.Add(_statusCounters);
            return strip;
        }

        // =====================================================================
        //  Adapter enumeration
        // =====================================================================

        /// <summary>ApiName of the pseudo-vendor that stands for the in-process virtual truck.</summary>
        private const string SIMULATOR_API = "SIMULATED";

        private static Rp1210VendorInfo MakeSimulatorVendor()
        {
            Rp1210VendorInfo v = new Rp1210VendorInfo();
            v.ApiName = SIMULATOR_API;
            v.Name = "Simulated bus - no hardware (virtual truck)";
            v.Version = string.Empty;
            v.AutoDetectCapable = false;
            Rp1210DeviceInfo d = new Rp1210DeviceInfo();
            d.DeviceId = 0;
            d.Description = "Virtual truck: engine, transmission, ABS, cluster, one squatter";
            d.Name = "VirtualTruck";
            v.Devices.Add(d);
            Rp1210ProtocolInfo proto = new Rp1210ProtocolInfo();
            proto.ProtocolString = "J1939";
            proto.Description = "Simulated J1939 network";
            proto.Speeds.Add("250");
            v.Protocols.Add(proto);
            return v;
        }

        private void RefreshAdapters()
        {
            _vendors = Rp1210Catalog.EnumerateVendors();
            _vendors.Add(MakeSimulatorVendor());

            _vendorCombo.BeginUpdate();
            _vendorCombo.Items.Clear();
            foreach (Rp1210VendorInfo v in _vendors)
            {
                _vendorCombo.Items.Add(v);
            }
            _vendorCombo.EndUpdate();

            if (_vendorCombo.Items.Count > 0)
            {
                _vendorCombo.SelectedIndex = 0;
            }
            else
            {
                PopulateDevices();
            }

            _adapterText.Text = BuildAdapterReport();
            int real = _vendors.Count - 1;
            EnqueueLog(real == 0
                ? "No RP1210 adapter driver is registered (no %WINDIR%\\RP121032.INI or it lists no usable API). Install a vendor driver (Nexiq, DG, Noregon, PEAK, Cat...) and refresh - or pick the simulated bus to run without hardware."
                : string.Format(CultureInfo.InvariantCulture, "{0} RP1210 vendor API(s) registered, plus the simulated bus. Pick one and connect.", real));
        }

        private void PopulateDevices()
        {
            _deviceCombo.BeginUpdate();
            _deviceCombo.Items.Clear();
            Rp1210VendorInfo vendor = _vendorCombo.SelectedItem as Rp1210VendorInfo;
            if (vendor != null)
            {
                foreach (Rp1210DeviceInfo d in vendor.Devices)
                {
                    _deviceCombo.Items.Add(d);
                }
            }
            _deviceCombo.EndUpdate();

            if (_deviceCombo.Items.Count > 0)
            {
                _deviceCombo.SelectedIndex = 0;
            }
            else
            {
                PopulateProtocols();
            }
        }

        private void PopulateProtocols()
        {
            _protocolCombo.BeginUpdate();
            _protocolCombo.Items.Clear();

            Rp1210VendorInfo vendor = _vendorCombo.SelectedItem as Rp1210VendorInfo;
            Rp1210DeviceInfo device = _deviceCombo.SelectedItem as Rp1210DeviceInfo;
            int preferred = -1;

            if (vendor != null && device != null)
            {
                foreach (Rp1210ProtocolInfo proto in vendor.Protocols)
                {
                    if (!proto.SupportsDevice(device.DeviceId)) continue;

                    foreach (ProtocolChoice choice in ProtocolChoice.Expand(vendor, proto))
                    {
                        int index = _protocolCombo.Items.Add(choice);
                        if (preferred < 0 && choice.IsPreferredDefault) preferred = index;
                    }
                }
            }

            _protocolCombo.EndUpdate();
            if (_protocolCombo.Items.Count > 0)
            {
                _protocolCombo.SelectedIndex = preferred >= 0 ? preferred : 0;
            }
        }

        private void RestoreSelection()
        {
            if (_settings.VendorApi.Length == 0) return;

            for (int i = 0; i < _vendorCombo.Items.Count; i++)
            {
                Rp1210VendorInfo v = (Rp1210VendorInfo)_vendorCombo.Items[i];
                if (!v.ApiName.Equals(_settings.VendorApi, StringComparison.OrdinalIgnoreCase)) continue;
                _vendorCombo.SelectedIndex = i;

                for (int d = 0; d < _deviceCombo.Items.Count; d++)
                {
                    if (((Rp1210DeviceInfo)_deviceCombo.Items[d]).DeviceId == _settings.DeviceId)
                    {
                        _deviceCombo.SelectedIndex = d;
                        break;
                    }
                }

                for (int p = 0; p < _protocolCombo.Items.Count; p++)
                {
                    if (((ProtocolChoice)_protocolCombo.Items[p]).ConnectString.Equals(_settings.ProtocolString, StringComparison.OrdinalIgnoreCase))
                    {
                        _protocolCombo.SelectedIndex = p;
                        break;
                    }
                }
                break;
            }
        }

        private string BuildAdapterReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("RP1210 adapters registered on this machine (from %WINDIR%\\RP121032.INI and each vendor INI)");
            sb.AppendLine("================================================================================");
            sb.AppendLine("Process bitness: " + (IntPtr.Size == 4 ? "32-bit (correct for RP1210)" : "64-BIT - vendor DLLs will NOT load"));
            sb.AppendLine();

            if (_vendors.Count == 0)
            {
                sb.AppendLine("  (none)");
            }

            foreach (Rp1210VendorInfo v in _vendors)
            {
                if (v.ApiName == SIMULATOR_API)
                {
                    sb.AppendLine(v.Name + "   (in-process; see Transport/Simulation/SimulatedJ1939Link.cs)");
                    sb.AppendLine();
                    continue;
                }
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}   DLL: {1}.dll   RP1210 revision: {2}   AutoDetect: {3}",
                    v.ToString(), v.ApiName, string.IsNullOrEmpty(v.Version) ? "(not stated)" : v.Version, v.AutoDetectCapable ? "yes" : "no"));
                foreach (Rp1210DeviceInfo d in v.Devices)
                {
                    sb.AppendLine("    device   " + d.ToString());
                }
                foreach (Rp1210ProtocolInfo p in v.Protocols)
                {
                    sb.Append("    protocol " + p.ToString());
                    if (p.Speeds.Count > 0) sb.Append("  speeds: " + string.Join(",", p.Speeds.ToArray()));
                    if (p.DeviceIds.Count > 0)
                    {
                        string[] ids = new string[p.DeviceIds.Count];
                        for (int i = 0; i < ids.Length; i++) ids[i] = p.DeviceIds[i].ToString(CultureInfo.InvariantCulture);
                        sb.Append("  devices: " + string.Join(",", ids));
                    }
                    sb.AppendLine();
                }
                sb.AppendLine();
            }

            List<Rp1210VendorInfo> realVendors = new List<Rp1210VendorInfo>();
            foreach (Rp1210VendorInfo v in _vendors) if (v.ApiName != SIMULATOR_API) realVendors.Add(v);
            sb.AppendLine(Rp1210CdlSupport.DescribeCdlAvailability(realVendors));
            sb.AppendLine();
            foreach (string line in _settings.DescribeIdentity())
            {
                sb.AppendLine(line);
            }
            return sb.ToString();
        }

        // =====================================================================
        //  Connect / disconnect
        // =====================================================================

        private void Connect()
        {
            if (_link != null)
            {
                EnqueueLog("Already connected. Disconnect first.");
                return;
            }

            Rp1210VendorInfo vendor = _vendorCombo.SelectedItem as Rp1210VendorInfo;
            Rp1210DeviceInfo device = _deviceCombo.SelectedItem as Rp1210DeviceInfo;
            ProtocolChoice choice = _protocolCombo.SelectedItem as ProtocolChoice;
            if (vendor == null || device == null || choice == null)
            {
                EnqueueLog("Pick a vendor API, a device and a protocol before connecting.");
                return;
            }

            _settings.VendorApi = vendor.ApiName;
            _settings.DeviceId = device.DeviceId;
            _settings.ProtocolString = choice.ConnectString;
            _settings.ClaimAddress = _claimCheck.Checked;
            _settings.Save();

            ClearViews();
            _passiveMultiFrameWarned = false;

            StringBuilder report = new StringBuilder(_adapterText.Text);
            report.AppendLine();

            bool j1939Family;
            if (vendor.ApiName == SIMULATOR_API)
            {
                SimulatedJ1939Link sim = new SimulatedJ1939Link();
                sim.Log += OnSimulatorLog;
                sim.Connect();
                _sim = sim;
                _link = sim;
                j1939Family = true;
                report.AppendLine("CONNECTED: simulated bus (virtual truck), \"" + sim.ProtocolString + "\"");
                report.AppendLine("  Capabilities: " + sim.Capabilities.ToString());
            }
            else
            {
                Rp1210Provider provider = new Rp1210Provider(vendor, device);
                provider.Log += OnProviderLog;
                provider.Error += OnProviderError;

                string reason;
                if (!provider.Connect(choice.ConnectString, true, out reason))
                {
                    EnqueueLog("Connect failed: " + reason);
                    provider.Dispose();
                    return;
                }

                _provider = provider;
                _link = provider;
                j1939Family = provider.Family == Rp1210ProtocolFamily.J1939;

                report.AppendLine("CONNECTED: " + vendor.ToString() + " / " + device.ToString() + " / \"" + choice.ConnectString + "\"");
                string dllVer, apiVer, fw;
                if (provider.TryReadVersion(out dllVer, out apiVer))
                {
                    report.AppendLine("  RP1210_ReadVersion: DLL " + dllVer + ", API " + apiVer);
                }
                if (provider.TryReadDetailedVersion(out apiVer, out dllVer, out fw))
                {
                    report.AppendLine("  RP1210_ReadDetailedVersion: API " + apiVer + ", DLL " + dllVer + ", firmware " + fw);
                }
                report.AppendLine("  Capabilities: " + provider.Capabilities.ToString());
            }
            _adapterText.Text = report.ToString();

            _active = _claimCheck.Checked && j1939Family;
            if (_claimCheck.Checked && !_active)
            {
                EnqueueLog("Protocol \"" + choice.ConnectString + "\" is not a J1939-family client, so no address can be claimed. Running in listen-only mode (29-bit frames are still decoded as J1939).");
            }

            if (_active)
            {
                _now = SessionClockNow;
                J1939Session session = new J1939Session(_link, _settings.BuildName());
                session.Log += OnSessionLog;
                session.ApplicationMessageReceived += OnApplicationMessage;
                session.AddressClaimStateChanged += OnClaimStateChanged;
                session.TransportFault += OnTransportFault;
                _session = session;
                _network.NameResolver = ResolveSessionName;
                _faults.NameResolver = ResolveSessionName;
                if (_provider != null) _provider.FrameReceived += OnFrameReceivedForLog;
                if (_sim != null) _sim.MessageReceived += OnSimMessageForLog;
                try
                {
                    session.Start();
                }
                catch (Exception ex)
                {
                    EnqueueLog("Session start failed: " + ex.Message);
                    Disconnect();
                    return;
                }
                _requestsMenu.Enabled = true;
            }
            else
            {
                _now = UiClockNow;
                _network.NameResolver = ResolvePassiveName;
                _faults.NameResolver = ResolvePassiveName;
                if (_provider != null) _provider.FrameReceived += OnFrameReceivedPassive;
                if (_sim != null) _sim.MessageReceived += OnSimMessagePassive;
                EnqueueLog("Listen-only: no address is claimed, nothing is transmitted. Single-frame messages are decoded; multi-packet messages (long DM1 lists, VIN, component ID) are NOT reassembled in this mode.");
            }

            _connectButton.Enabled = false;
            _connectMenu.Enabled = false;
            _disconnectButton.Enabled = true;
            _disconnectMenu.Enabled = true;
            _vendorCombo.Enabled = false;
            _deviceCombo.Enabled = false;
            _protocolCombo.Enabled = false;
            _claimCheck.Enabled = false;
            _refreshButton.Enabled = false;
        }

        private void Disconnect()
        {
            J1939Session session = _session;
            Rp1210Provider provider = _provider;
            SimulatedJ1939Link sim = _sim;
            _session = null;
            _provider = null;
            _sim = null;
            _link = null;
            _requestsMenu.Enabled = false;

            if (session != null)
            {
                try { session.Stop(); }
                catch (Exception ex) { EnqueueLog("Session stop: " + ex.Message); }
                try { session.Dispose(); }
                catch (Exception ex) { EnqueueLog("Session dispose: " + ex.Message); }
            }

            if (provider != null)
            {
                provider.FrameReceived -= OnFrameReceivedPassive;
                provider.FrameReceived -= OnFrameReceivedForLog;
                try { provider.Disconnect(); }
                catch (Exception ex) { EnqueueLog("Disconnect: " + ex.Message); }
                try { provider.Dispose(); }
                catch (Exception ex) { EnqueueLog("Dispose: " + ex.Message); }
            }

            if (sim != null)
            {
                sim.MessageReceived -= OnSimMessagePassive;
                sim.MessageReceived -= OnSimMessageForLog;
                try { sim.Dispose(); }
                catch (Exception ex) { EnqueueLog("Simulator dispose: " + ex.Message); }
            }

            _active = false;
            _now = UiClockNow;
            _network.NameResolver = null;
            _faults.NameResolver = null;

            _connectButton.Enabled = true;
            _connectMenu.Enabled = true;
            _disconnectButton.Enabled = false;
            _disconnectMenu.Enabled = false;
            _vendorCombo.Enabled = true;
            _deviceCombo.Enabled = true;
            _protocolCombo.Enabled = true;
            _claimCheck.Enabled = true;
            _refreshButton.Enabled = true;

            _statusLink.Text = "Not connected";
            _statusClaim.Text = "J1939: idle";
            _statusEngine.Text = "Engine: -";
            _statusCounters.Text = string.Empty;
            EnqueueLog("Disconnected.");
        }

        private void ClearViews()
        {
            _network.Clear();
            _faults.Clear();
            lock (_passiveNameGate) { _passiveNames.Clear(); }

            _nodeList.Items.Clear();
            _nodeRows.Clear();
            _pgnList.Items.Clear();
            _pgnRows.Clear();
            _pgnRowsForAddress = -1;
            _faultList.Items.Clear();
            _faultRows.Clear();
            _faultDetail.Text = string.Empty;
            _networkHeadline.Text = _link == null ? "Not connected." : "Waiting for traffic...";
            _faultHeadline.Text = _link == null ? "Not connected." : "No DM1 or DM2 received yet.";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_link != null)
            {
                Disconnect();
            }

            _settings.ClaimAddress = _claimCheck.Checked;
            _settings.Save();
            base.OnFormClosing(e);
        }

        // =====================================================================
        //  Requests
        // =====================================================================

        private void SendRequest(uint pgn)
        {
            J1939Session session = _session;
            if (session == null)
            {
                EnqueueLog("Requests need an active session (connect with address claiming on).");
                return;
            }

            string reason;
            if (session.RequestPgn(pgn, out reason))
            {
                EnqueueLog("Request sent for " + J1939Pgn.Describe(pgn) + " to the global address. Replies appear in the Network view and, for text PGNs, in this log.");
            }
            else
            {
                EnqueueLog("Request NOT sent: " + reason);
            }
        }

        // =====================================================================
        //  Stack event handlers - WORKER THREADS. Touch model objects only.
        // =====================================================================

        private void OnProviderLog(string text)
        {
            EnqueueLog("RP1210: " + text);
        }

        private void OnProviderError(string text)
        {
            EnqueueLog("RP1210 ERROR: " + text);
        }

        private void OnSimulatorLog(string text)
        {
            EnqueueLog("SIM: " + text);
        }

        private void OnSessionLog(object sender, string text)
        {
            EnqueueLog(text);
        }

        private void OnClaimStateChanged(object sender, J1939AddressClaimEventArgs e)
        {
            EnqueueLog("Address claim: " + e.ToString());
        }

        private void OnTransportFault(object sender, J1939TransportFaultEventArgs e)
        {
            EnqueueLog("Transport fault: " + e.ToString());
        }

        /// <summary>Active session: every complete application message, single frame or reassembled.</summary>
        private void OnApplicationMessage(object sender, J1939ApplicationMessageEventArgs e)
        {
            double at = e.SessionClockMs;
            byte[] payload = e.Payload;

            _network.Observe(e.Pgn, e.SourceAddress, e.DestinationAddress, payload, at, e.WasMultiFrame);
            FeedDiagnostics(e.Pgn, e.SourceAddress, payload, at);
            _engine.Observe(e.Pgn, payload);
            DescribeTextPgn(e.Pgn, e.SourceAddress, payload, e.WasMultiFrame);
        }

        /// <summary>Passive mode: raw frames straight off the adapter. No reassembly.</summary>
        private void OnFrameReceivedPassive(Rp1210Frame frame)
        {
            if (_logEveryFrame) LogFrame(frame);
            if (frame.J1939 == null || frame.IsTxEcho) return;
            ObservePassive(frame.J1939);
        }

        /// <summary>Passive mode on the simulated bus: every frame the virtual ECUs emit.</summary>
        private void OnSimMessagePassive(J1939Message message)
        {
            if (_logEveryFrame) EnqueueLog("RX " + message.ToString());
            ObservePassive(message);
        }

        private void OnSimMessageForLog(J1939Message message)
        {
            if (_logEveryFrame) EnqueueLog("RX " + message.ToString());
        }

        private void ObservePassive(J1939Message m)
        {
            uint pgn = m.Pgn;

            if (pgn == J1939Pgn.TransportProtocolConnectionManagement
                || pgn == J1939Pgn.TransportProtocolDataTransfer
                || pgn == J1939Pgn.ExtendedTransportProtocolConnectionManagement
                || pgn == J1939Pgn.ExtendedTransportProtocolDataTransfer)
            {
                if (!_passiveMultiFrameWarned)
                {
                    _passiveMultiFrameWarned = true;
                    EnqueueLog(string.Format(CultureInfo.InvariantCulture,
                        "Multi-packet transfer seen from SA 0x{0:X2} (TP/ETP frames). Listen-only mode does not reassemble these; a DM1 with more than one code, a VIN or a component ID will not appear. Reconnect with address claiming on to see them.",
                        m.SourceAddress));
                }
                return;
            }

            double at = UiClockNow();

            if (pgn == J1939Pgn.AddressClaimed)
            {
                J1939Name name;
                if (J1939Name.TryFromBytes(m.Data, 0, out name))
                {
                    lock (_passiveNameGate) { _passiveNames[m.SourceAddress] = name; }
                }
            }

            _network.Observe(pgn, m.SourceAddress, m.DestinationAddress, m.Data, at, false);
            FeedDiagnostics(pgn, m.SourceAddress, m.Data, at);
            _engine.Observe(pgn, m.Data);
        }

        private void OnFrameReceivedForLog(Rp1210Frame frame)
        {
            if (_logEveryFrame) LogFrame(frame);
        }

        private void LogFrame(Rp1210Frame frame)
        {
            if (frame.J1939 != null)
            {
                EnqueueLog((frame.IsTxEcho ? "TX " : "RX ") + frame.J1939.ToString());
            }
            else if (frame.Can != null)
            {
                EnqueueLog(frame.Can.ToString());
            }
            else if (!string.IsNullOrEmpty(frame.ParseError))
            {
                EnqueueLog("UNPARSED " + frame.ParseError + " raw=" + J1939Diagnostics.ToHex(frame.Raw));
            }
            else
            {
                EnqueueLog("RAW " + frame.Family + " " + J1939Diagnostics.ToHex(frame.Payload ?? frame.Raw));
            }
        }

        private void FeedDiagnostics(uint pgn, byte sourceAddress, byte[] payload, double at)
        {
            if (pgn == J1939Pgn.Dm1)
            {
                _faults.OnDtcReport(J1939Diagnostics.ParseDm1(payload, sourceAddress), at);
            }
            else if (pgn == J1939Pgn.Dm2)
            {
                _faults.OnDtcReport(J1939Diagnostics.ParseDm2(payload, sourceAddress), at);
            }
        }

        /// <summary>
        /// The handful of PGNs whose payload is '*'-delimited ASCII. Logged as text so a
        /// VIN or a component ID reply is readable without decoding hex by eye.
        /// </summary>
        private void DescribeTextPgn(uint pgn, byte sourceAddress, byte[] payload, bool multiFrame)
        {
            string label;
            switch (pgn)
            {
                case 65260: label = "VIN"; break;
                case 65259: label = "Component ID (make*model*serial*unit)"; break;
                case 65242: label = "Software ID"; break;
                case 64965: label = "ECU ID"; break;
                case 54016: label = "DM19 calibration"; break;
                default: return;
            }

            string text;
            if (pgn == 54016)
            {
                J1939Dm19Report dm19 = J1939Diagnostics.ParseDm19(payload);
                text = dm19.ToString();
            }
            else
            {
                int start = 0;
                if (pgn == 65242 && payload.Length > 0)
                {
                    start = 1;   // byte 0 is the field count
                }
                text = AsciiFields(payload, start);
            }

            EnqueueLog(string.Format(CultureInfo.InvariantCulture, "{0} from SA 0x{1:X2}{2}: {3}",
                label, sourceAddress, multiFrame ? " (multi-packet)" : string.Empty, text));
        }

        private static string AsciiFields(byte[] data, int start)
        {
            StringBuilder sb = new StringBuilder(data.Length);
            for (int i = start; i < data.Length; i++)
            {
                byte b = data[i];
                if (b == (byte)'*') { sb.Append(" | "); continue; }
                if (b == 0xFF || b == 0x00) continue;
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '?');
            }
            return sb.ToString().Trim();
        }

        // =====================================================================
        //  Resolvers (called by the model outside its locks)
        // =====================================================================

        private string ResolveSpnName(uint spn)
        {
            string name;
            return _catalogue.TryGetSpnName(spn, out name) ? name : null;
        }

        private string ResolvePgnName(uint pgn)
        {
            string name;
            return _catalogue.TryGetPgnName(pgn, out name) ? name : null;
        }

        private J1939Name ResolveSessionName(byte sourceAddress)
        {
            J1939Session session = _session;
            J1939Name name;
            if (session != null && session.TryGetNodeName(sourceAddress, out name))
            {
                return name;
            }
            return null;
        }

        private J1939Name ResolvePassiveName(byte sourceAddress)
        {
            lock (_passiveNameGate)
            {
                J1939Name name;
                return _passiveNames.TryGetValue(sourceAddress, out name) ? name : null;
            }
        }

        private double UiClockNow()
        {
            return _uiClock.Elapsed.TotalMilliseconds;
        }

        private double SessionClockNow()
        {
            J1939Session session = _session;
            return session != null ? session.ElapsedMs : _uiClock.Elapsed.TotalMilliseconds;
        }

        // =====================================================================
        //  Log plumbing
        // =====================================================================

        private void EnqueueLog(string text)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + text;
            lock (_logGate)
            {
                _logQueue.Enqueue(line);
                while (_logQueue.Count > MAX_LOG_LINES)
                {
                    _logQueue.Dequeue();   // the UI fell far behind; drop the oldest, say so once below
                }
            }
        }

        private void DrainLog()
        {
            string[] lines;
            lock (_logGate)
            {
                if (_logQueue.Count == 0) return;
                lines = _logQueue.ToArray();
                _logQueue.Clear();
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                sb.Append(lines[i]).Append(Environment.NewLine);
            }

            _logLineCount += lines.Length;
            if (_logLineCount > MAX_LOG_LINES)
            {
                // Keep the box bounded. Everything older has scrolled off; a capture that
                // matters should be written to disk by the host, not scraped from a TextBox.
                string existing = _logText.Text;
                int cut = existing.IndexOf(Environment.NewLine, existing.Length / 2, StringComparison.Ordinal);
                _logText.Text = cut > 0 ? existing.Substring(cut + Environment.NewLine.Length) : string.Empty;
                _logLineCount = lines.Length + (_logText.Lines.Length);
            }

            _logText.AppendText(sb.ToString());
        }

        // =====================================================================
        //  Painting - UI THREAD, on the timer
        // =====================================================================

        private void RefreshViews()
        {
            DrainLog();

            if (_link == null) return;

            double now = _now();
            J1939NetworkSnapshot net = _network.Evaluate(now);
            J1939BusFaultSnapshot faults = _faults.Evaluate(now);

            PaintNetwork(net);
            PaintFaults(faults);
            PaintStatus(net);
        }

        private void PaintNetwork(J1939NetworkSnapshot net)
        {
            _networkHeadline.Text = net.Headline + (net.TimebaseNote != null ? "   [" + net.TimebaseNote + "]" : string.Empty);

            _nodeList.BeginUpdate();
            try
            {
                HashSet<byte> seen = new HashSet<byte>();
                foreach (J1939NodeObservation node in net.Nodes)
                {
                    seen.Add(node.SourceAddress);
                    ListViewItem row;
                    if (!_nodeRows.TryGetValue(node.SourceAddress, out row))
                    {
                        row = new ListViewItem(new string[_nodeList.Columns.Count]);
                        row.Tag = node.SourceAddress;
                        _nodeRows[node.SourceAddress] = row;
                        _nodeList.Items.Insert(InsertIndexFor(_nodeList, node.SourceAddress), row);
                    }

                    SetCell(row, 0, string.Format(CultureInfo.InvariantCulture, "0x{0:X2}", node.SourceAddress));
                    SetCell(row, 1, node.NameKnown ? string.Format(CultureInfo.InvariantCulture, "NAME {0:X16}", node.Name.Value) : "(no claim seen)");
                    SetCell(row, 2, node.NameKnown ? node.Name.Function.ToString(CultureInfo.InvariantCulture) : "-");
                    SetCell(row, 3, node.NameKnown ? node.Name.ManufacturerCode.ToString(CultureInfo.InvariantCulture) : "-");
                    SetCell(row, 4, node.NameKnown ? node.Name.EcuInstance.ToString(CultureInfo.InvariantCulture) : "-");
                    SetCell(row, 5, node.MessageCount.ToString(CultureInfo.InvariantCulture));
                    SetCell(row, 6, node.MessagesPerSecond.ToString("F1", CultureInfo.InvariantCulture));
                    SetCell(row, 7, node.Pgns.Count.ToString(CultureInfo.InvariantCulture));
                    SetCell(row, 8, J1939EcuFaultEntry.FormatAge(node.AgeMs) + " ago");
                    SetCell(row, 9, node.IsSilent ? "SILENT" : (node.IsServiceToolAddress ? "tool addr" : "active"));
                    row.ForeColor = node.IsSilent ? SystemColors.GrayText : SystemColors.WindowText;
                }

                // Nodes never disappear from the map (silence is information), so nothing is
                // removed here; ClearViews is the only thing that empties the list.
            }
            finally
            {
                _nodeList.EndUpdate();
            }

            // PGN panel follows the selected node.
            if (_nodeList.SelectedItems.Count == 0)
            {
                if (_pgnList.Items.Count > 0 && _pgnRowsForAddress != -1)
                {
                    _pgnList.Items.Clear();
                    _pgnRows.Clear();
                    _pgnRowsForAddress = -1;
                }
                return;
            }

            byte selected = (byte)_nodeList.SelectedItems[0].Tag;
            J1939NodeObservation selectedNode;
            if (!net.TryGetNode(selected, out selectedNode)) return;

            _pgnList.BeginUpdate();
            try
            {
                if (_pgnRowsForAddress != selected)
                {
                    _pgnList.Items.Clear();
                    _pgnRows.Clear();
                    _pgnRowsForAddress = selected;
                }

                foreach (J1939PgnObservation pgn in selectedNode.Pgns)
                {
                    ListViewItem row;
                    if (!_pgnRows.TryGetValue(pgn.Pgn, out row))
                    {
                        row = new ListViewItem(new string[_pgnList.Columns.Count]);
                        row.Tag = pgn.Pgn;
                        _pgnRows[pgn.Pgn] = row;
                        _pgnList.Items.Insert(InsertIndexForPgn(_pgnList, pgn.Pgn), row);
                    }

                    SetCell(row, 0, pgn.Pgn.ToString(CultureInfo.InvariantCulture));
                    SetCell(row, 1, string.Format(CultureInfo.InvariantCulture, "0x{0:X5}", pgn.Pgn));
                    SetCell(row, 2, pgn.PgnNameKnown ? pgn.PgnName : string.Empty);
                    SetCell(row, 3, pgn.Count.ToString(CultureInfo.InvariantCulture));
                    SetCell(row, 4, double.IsNaN(pgn.AverageIntervalMs) ? "-" : pgn.AverageIntervalMs.ToString("F0", CultureInfo.InvariantCulture));
                    SetCell(row, 5, double.IsNaN(pgn.LastIntervalMs) ? "-" : pgn.LastIntervalMs.ToString("F0", CultureInfo.InvariantCulture));
                    SetCell(row, 6, pgn.LastDestination == J1939Pgn.GlobalAddress ? "all" : string.Format(CultureInfo.InvariantCulture, "0x{0:X2}", pgn.LastDestination));
                    SetCell(row, 7, (pgn.LastPayload == null ? 0 : pgn.LastPayload.Length).ToString(CultureInfo.InvariantCulture) + (pgn.LastWasMultiFrame ? "*" : string.Empty));
                    SetCell(row, 8, pgn.LastPayloadHex);
                }
            }
            finally
            {
                _pgnList.EndUpdate();
            }
        }

        private void PaintFaults(J1939BusFaultSnapshot faults)
        {
            _faultHeadline.Text = faults.Headline;

            _faultList.BeginUpdate();
            try
            {
                foreach (J1939EcuFaultEntry entry in faults.Entries)
                {
                    ListViewItem row;
                    if (!_faultRows.TryGetValue(entry.SourceAddress, out row))
                    {
                        row = new ListViewItem(new string[_faultList.Columns.Count]);
                        row.Tag = entry.SourceAddress;
                        _faultRows[entry.SourceAddress] = row;
                        _faultList.Items.Insert(InsertIndexFor(_faultList, entry.SourceAddress), row);
                    }

                    SetCell(row, 0, string.Format(CultureInfo.InvariantCulture, "0x{0:X2}", entry.SourceAddress));
                    SetCell(row, 1, entry.ActiveState.ToString());
                    SetCell(row, 2, entry.LampHeadline);
                    SetCell(row, 3, entry.ActiveDtcCount.ToString(CultureInfo.InvariantCulture));
                    SetCell(row, 4, entry.HasDm2 ? entry.PreviouslyActiveDtcCount.ToString(CultureInfo.InvariantCulture) : "-");
                    SetCell(row, 5, J1939EcuFaultEntry.FormatAge(entry.Dm1AgeMs));
                    SetCell(row, 6, entry.Dm1ReportCount.ToString(CultureInfo.InvariantCulture));

                    Color colour = SystemColors.WindowText;
                    switch (entry.EffectiveLampSeverity)
                    {
                        case J1939LampSeverity.StopNow: colour = Color.Firebrick; break;
                        case J1939LampSeverity.Warning: colour = Color.DarkGoldenrod; break;
                        case J1939LampSeverity.Protect:
                        case J1939LampSeverity.Emissions: colour = Color.DarkOrange; break;
                    }
                    if (entry.ActiveState == J1939EcuFaultState.Stale) colour = SystemColors.GrayText;
                    row.ForeColor = colour;
                }
            }
            finally
            {
                _faultList.EndUpdate();
            }

            if (_faultList.SelectedItems.Count > 0)
            {
                byte sa = (byte)_faultList.SelectedItems[0].Tag;
                J1939EcuFaultEntry selected;
                if (faults.TryGetEntry(sa, out selected))
                {
                    string text = selected.ToString();
                    if (!string.Equals(_faultDetail.Text, text, StringComparison.Ordinal))
                    {
                        _faultDetail.Text = text;
                    }
                }
            }
        }

        private void RefreshFaultDetail()
        {
            if (_faultList.SelectedItems.Count == 0)
            {
                _faultDetail.Text = string.Empty;
                return;
            }

            byte sa = (byte)_faultList.SelectedItems[0].Tag;
            _faultDetail.Text = _faults.GetEntry(sa).ToString();
        }

        private void PaintStatus(J1939NetworkSnapshot net)
        {
            IJ1939Link link = _link;
            Rp1210Provider provider = _provider;
            SimulatedJ1939Link sim = _sim;
            J1939Session session = _session;
            if (link == null) return;

            if (provider != null)
            {
                _statusLink.Text = provider.IsConnected
                    ? "RP1210 " + provider.ProtocolString + " client " + provider.ClientId
                    : "RP1210 link DOWN";
            }
            else
            {
                _statusLink.Text = link.IsConnected ? "SIMULATED " + link.ProtocolString : "simulator stopped";
            }

            _statusClaim.Text = session != null
                ? session.DescribeStatus()
                : "J1939: listen only (no address claimed)";

            double? rpm = _engine.EngineSpeedRpm;
            double? coolant = _engine.CoolantTemperatureC;
            _statusEngine.Text = "Engine: "
                + (rpm.HasValue ? rpm.Value.ToString("0", CultureInfo.InvariantCulture) + " rpm" : "- rpm")
                + "  Coolant: "
                + (coolant.HasValue ? coolant.Value.ToString("0", CultureInfo.InvariantCulture) + " C" : "-");

            long frames = provider != null ? provider.FramesReceived : (sim != null ? sim.FramesReceived : 0);
            long dropped = provider != null ? provider.DroppedFrameCount : (sim != null ? sim.DroppedFrameCount : 0);
            long parseFail = provider != null ? provider.ParseFailureCount : 0;
            int queue = provider != null ? provider.QueueDepth : (sim != null ? sim.QueueDepth : 0);
            _statusCounters.Text = string.Format(CultureInfo.InvariantCulture,
                "frames {0}   dropped {1}   parse fail {2}   queue {3}   nodes {4}   msgs {5}",
                frames, dropped, parseFail, queue, net.NodeCount, net.TotalMessages);
        }

        private static void SetCell(ListViewItem row, int index, string text)
        {
            if (!string.Equals(row.SubItems[index].Text, text, StringComparison.Ordinal))
            {
                row.SubItems[index].Text = text;
            }
        }

        private static int InsertIndexFor(ListView lv, byte sourceAddress)
        {
            for (int i = 0; i < lv.Items.Count; i++)
            {
                if ((byte)lv.Items[i].Tag > sourceAddress) return i;
            }
            return lv.Items.Count;
        }

        private static int InsertIndexForPgn(ListView lv, uint pgn)
        {
            for (int i = 0; i < lv.Items.Count; i++)
            {
                if ((uint)lv.Items[i].Tag > pgn) return i;
            }
            return lv.Items.Count;
        }

        // =====================================================================
        //  Catalogue / about
        // =====================================================================

        private void LoadCatalogue()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Load SPN / PGN names (kind,number,name CSV)";
                dialog.Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    Dictionary<uint, string> spns;
                    Dictionary<uint, string> pgns;
                    string report;
                    CatalogueCsv.LoadNames(dialog.FileName, out spns, out pgns, out report);
                    _catalogue.Load(spns, pgns);
                    EnqueueLog("Catalogue: " + report);
                }
                catch (Exception ex)
                {
                    EnqueueLog("Catalogue load failed: " + ex.Message);
                }
            }
        }

        private void ShowAbout()
        {
            string version = typeof(MainForm).Assembly.GetName().Version.ToString();
            MessageBox.Show(this,
                "HeavyDiesel Bench " + version + "\n\n"
                + "Open RP1210 transport + SAE J1939 foundation for heavy-vehicle software.\n"
                + "github.com/jakka351/HeavyDiesel\n\n"
                + "Settings file: " + BenchSettings.FilePath + "\n"
                + "Process: " + (IntPtr.Size == 4 ? "32-bit" : "64-bit") + "\n\n"
                + "No SAE definition data is included. Load your own SPN/PGN names from the Catalogue menu.",
                "About HeavyDiesel Bench", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // =====================================================================
        //  Protocol choice - one RP1210 connect string per combo entry
        // =====================================================================

        private sealed class ProtocolChoice
        {
            public readonly string ConnectString;
            public readonly Rp1210ProtocolInfo Info;
            public readonly bool IsPreferredDefault;

            private ProtocolChoice(string connectString, Rp1210ProtocolInfo info, bool preferred)
            {
                ConnectString = connectString;
                Info = info;
                IsPreferredDefault = preferred;
            }

            /// <summary>
            /// One entry per declared speed ("J1939:Baud=250"), the bare string when the
            /// INI declares none, and ":Baud=Auto" on an auto-detect capable driver for
            /// the CAN-family protocols. 250 kbit/s J1939 is preferred when present: it is
            /// what most of the heavy fleet runs.
            /// </summary>
            public static IEnumerable<ProtocolChoice> Expand(Rp1210VendorInfo vendor, Rp1210ProtocolInfo proto)
            {
                string name = proto.ProtocolString;
                int colon = name.IndexOf(':');
                string family = (colon >= 0 ? name.Substring(0, colon) : name).Trim().ToUpperInvariant();
                bool canFamily = family == "J1939" || family == "CAN" || family == "ISO15765";

                if (proto.Speeds.Count == 0 || !canFamily)
                {
                    yield return new ProtocolChoice(name, proto, family == "J1939");
                }
                else
                {
                    foreach (string speed in proto.Speeds)
                    {
                        string s = speed.Trim();
                        if (s.Length == 0) continue;
                        bool preferred = family == "J1939" && s == "250";
                        yield return new ProtocolChoice(name + ":Baud=" + s, proto, preferred);
                    }
                }

                if (canFamily && vendor.AutoDetectCapable)
                {
                    yield return new ProtocolChoice(name + ":Baud=Auto", proto, false);
                }
            }

            public override string ToString()
            {
                return string.IsNullOrEmpty(Info.Description) ? ConnectString : ConnectString + "   (" + Info.Description + ")";
            }
        }
    }
}
