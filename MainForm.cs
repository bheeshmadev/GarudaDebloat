using System.ComponentModel;
using System.Drawing;
using System.Text;
using Microsoft.Win32;

namespace GarudaDebloat;

internal sealed class MainForm : Form
{
    private readonly Color _bgMain = ColorTranslator.FromHtml("#0d0d14");
    private readonly Color _bgPanel = ColorTranslator.FromHtml("#141420");
    private readonly Color _bgGrid = ColorTranslator.FromHtml("#11111a");
    private readonly Color _bgGridAlt = ColorTranslator.FromHtml("#181826");
    private readonly Color _bgHover = ColorTranslator.FromHtml("#232339");
    private readonly Color _fgMain = ColorTranslator.FromHtml("#f3f3f7");
    private readonly Color _fgMuted = ColorTranslator.FromHtml("#b9b9c8");
    private readonly Color _accent = ColorTranslator.FromHtml("#e05252");
    private readonly Color _logOk = ColorTranslator.FromHtml("#45d483");
    private readonly Color _logError = ColorTranslator.FromHtml("#ff6b6b");

    private readonly PackageScanner _scanner = new();
    private readonly UninstallEngine _uninstallEngine = new();

    private readonly Dictionary<PackageCategory, TabState> _tabs = new();

    private readonly ToolStripStatusLabel _statusCount = new();
    private readonly ToolStripStatusLabel _statusOperation = new();

    private readonly Button _btnScan = new();
    private readonly Button _btnSelectAll = new();
    private readonly Button _btnDeselectAll = new();
    private readonly Button _btnUninstall = new();

    private readonly TabControl _tabControl = new();
    private readonly RichTextBox _logBox = new();
    private readonly Image _headerLogo;

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _uninstallCts;
    private bool _isBusy;

    private int _hoverRowIndex = -1;

    private sealed class TabState
    {
        public PackageCategory Category { get; init; }

        public TextBox SearchBox { get; init; } = null!;

        public DataGridView Grid { get; init; } = null!;

        public List<PackageEntry> AllItems { get; } = new();

        public BindingList<PackageEntry> ViewItems { get; } = new();

        public BindingSource BindingSource { get; } = new();
    }

    private sealed class UiProgress : IProgress<UninstallEngine.LogMessage>
    {
        private readonly Control _ui;
        private readonly Action<UninstallEngine.LogMessage> _handler;

        public UiProgress(Control ui, Action<UninstallEngine.LogMessage> handler)
        {
            _ui = ui;
            _handler = handler;
        }

        public void Report(UninstallEngine.LogMessage value)
        {
            if (_ui.IsHandleCreated && _ui.InvokeRequired)
            {
                _ui.Invoke(() => _handler(value));
                return;
            }

            _handler(value);
        }
    }

    public MainForm()
    {
        Text = "Garuda Debloat";
        Icon = BrandingAssets.CreateAppIcon();
        Width = 1450;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1100, 700);
        BackColor = _bgMain;
        ForeColor = _fgMain;
        Font = new Font("Segoe UI", 10);
        _headerLogo = BrandingAssets.CreateHeaderLogoBitmap();

        BuildLayout();

        Shown += async (_, _) =>
        {
            if (!EnsureFirstRunWarningAcknowledged())
            {
                Close();
                return;
            }

            await ScanAsync();
        };
        FormClosing += (_, _) =>
        {
            _scanCts?.Cancel();
            _uninstallCts?.Cancel();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _headerLogo.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BuildLayout()
    {
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            BackColor = _bgMain,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildToolbar(), 0, 1);
        root.Controls.Add(BuildTabs(), 0, 2);
        root.Controls.Add(BuildStatusBar(), 0, 3);

        Controls.Add(root);
    }

    private Control BuildHeader()
    {
        Panel panel = new()
        {
            Dock = DockStyle.Fill,
            BackColor = _bgPanel,
            Padding = new Padding(18, 10, 18, 10)
        };

        PictureBox logo = new()
        {
            Dock = DockStyle.Left,
            Width = 72,
            Image = _headerLogo,
            SizeMode = PictureBoxSizeMode.Zoom
        };

        Label title = new()
        {
            Text = "Garuda Debloat",
            Dock = DockStyle.Fill,
            ForeColor = _accent,
            Font = new Font("Segoe UI Semibold", 24, FontStyle.Bold),
            AutoSize = false,
            Padding = new Padding(8, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft
        };

        panel.Controls.Add(title);
        panel.Controls.Add(logo);

        return panel;
    }

    private Control BuildToolbar()
    {
        Panel panel = new()
        {
            Dock = DockStyle.Fill,
            BackColor = _bgPanel,
            Padding = new Padding(10, 8, 10, 8)
        };

        FlowLayoutPanel flow = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight
        };

        ConfigureToolbarButton(_btnScan, "Scan / Refresh", _bgHover, _fgMain, async (_, _) => await ScanAsync());
        ConfigureToolbarButton(_btnSelectAll, "Select All", _bgHover, _fgMain, (_, _) => SetSelectionForCurrentTab(true));
        ConfigureToolbarButton(_btnDeselectAll, "Deselect All", _bgHover, _fgMain, (_, _) => SetSelectionForCurrentTab(false));
        ConfigureToolbarButton(_btnUninstall, "Uninstall Selected", _accent, Color.White, async (_, _) => await UninstallSelectedAsync());

        flow.Controls.Add(_btnScan);
        flow.Controls.Add(_btnSelectAll);
        flow.Controls.Add(_btnDeselectAll);
        flow.Controls.Add(_btnUninstall);

        panel.Controls.Add(flow);
        return panel;
    }

    private void ConfigureToolbarButton(Button button, string text, Color bg, Color fg, EventHandler onClick)
    {
        button.Text = text;
        button.Width = text.Contains("Uninstall", StringComparison.OrdinalIgnoreCase) ? 190 : 145;
        button.Height = 36;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = bg;
        button.ForeColor = fg;
        button.Font = new Font("Segoe UI Semibold", 10, FontStyle.Bold);
        button.Margin = new Padding(6, 0, 6, 0);
        button.Cursor = Cursors.Hand;
        button.Click += onClick;

        Color hover = ControlPaint.Light(bg, 0.12f);
        button.MouseEnter += (_, _) => button.BackColor = hover;
        button.MouseLeave += (_, _) => button.BackColor = bg;
    }

    private Control BuildTabs()
    {
        _tabControl.Dock = DockStyle.Fill;
        _tabControl.Appearance = TabAppearance.Normal;
        _tabControl.SizeMode = TabSizeMode.Normal;
        _tabControl.Padding = new Point(20, 8);
        _tabControl.Font = new Font("Segoe UI Semibold", 10);
        _tabControl.SelectedIndexChanged += (_, _) => UpdateStatusCount();

        AddCategoryTab("Win32 Apps", PackageCategory.Win32);
        AddCategoryTab("UWP / Store Apps", PackageCategory.Uwp);
        AddLogTab();

        return _tabControl;
    }

    private void AddCategoryTab(string title, PackageCategory category)
    {
        TabPage page = new()
        {
            Text = title,
            BackColor = _bgMain,
            ForeColor = _fgMain,
            Padding = new Padding(8)
        };

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        TextBox search = new()
        {
            Dock = DockStyle.Fill,
            BackColor = _bgPanel,
            ForeColor = _fgMain,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "Search packages..."
        };

        DataGridView grid = CreateGrid();

        TabState state = new()
        {
            Category = category,
            SearchBox = search,
            Grid = grid
        };

        state.BindingSource.DataSource = state.ViewItems;
        grid.DataSource = state.BindingSource;

        search.TextChanged += (_, _) => ApplyFilter(state);
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty)
            {
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        grid.CellValueChanged += (_, _) => UpdateStatusCount();

        layout.Controls.Add(search, 0, 0);
        layout.Controls.Add(grid, 0, 1);

        page.Controls.Add(layout);
        _tabControl.TabPages.Add(page);

        _tabs[category] = state;
    }

    private DataGridView CreateGrid()
    {
        DataGridView grid = new()
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            RowHeadersVisible = false,
            BackgroundColor = _bgGrid,
            BorderStyle = BorderStyle.None,
            GridColor = _bgHover,
            ScrollBars = ScrollBars.Both,
            EnableHeadersVisualStyles = false,
            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = _bgGridAlt, ForeColor = _fgMain },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = _bgGrid,
                ForeColor = _fgMain,
                SelectionBackColor = _bgHover,
                SelectionForeColor = Color.White,
                Padding = new Padding(6, 3, 6, 3),
                WrapMode = DataGridViewTriState.False
            },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = _bgPanel,
                ForeColor = _fgMain,
                Font = new Font("Segoe UI Semibold", 10, FontStyle.Bold),
                SelectionBackColor = _bgPanel,
                SelectionForeColor = _fgMain
            }
        };

        grid.RowTemplate.Height = 34;
        grid.ColumnHeadersHeight = 38;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

        DataGridViewCheckBoxColumn selectedColumn = new()
        {
            HeaderText = "Select",
            DataPropertyName = nameof(PackageEntry.Selected),
            Width = 72,
            MinimumWidth = 72,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            FlatStyle = FlatStyle.Popup,
            TrueValue = true,
            FalseValue = false,
            ThreeState = false
        };

        DataGridViewTextBoxColumn nameColumn = new()
        {
            HeaderText = "Name",
            DataPropertyName = nameof(PackageEntry.Name),
            Width = 560,
            MinimumWidth = 380,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None
        };

        DataGridViewTextBoxColumn publisherColumn = new()
        {
            HeaderText = "Publisher",
            DataPropertyName = nameof(PackageEntry.Publisher),
            Width = 320,
            MinimumWidth = 220,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None
        };

        DataGridViewTextBoxColumn versionColumn = new()
        {
            HeaderText = "Version",
            DataPropertyName = nameof(PackageEntry.Version),
            Width = 180,
            MinimumWidth = 140,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None
        };

        DataGridViewTextBoxColumn typeColumn = new()
        {
            HeaderText = "Type",
            DataPropertyName = nameof(PackageEntry.TypeBadge),
            Width = 130,
            MinimumWidth = 120,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                ForeColor = _accent,
                Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold)
            }
        };

        grid.Columns.AddRange(selectedColumn, nameColumn, publisherColumn, versionColumn, typeColumn);

        grid.CellMouseEnter += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex != _hoverRowIndex)
            {
                ResetHoverRow(grid);
                _hoverRowIndex = e.RowIndex;
                grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = _bgHover;
            }
        };
        grid.MouseLeave += (_, _) => ResetHoverRow(grid);

        return grid;
    }

    private void ResetHoverRow(DataGridView grid)
    {
        if (_hoverRowIndex >= 0 && _hoverRowIndex < grid.Rows.Count)
        {
            DataGridViewRow row = grid.Rows[_hoverRowIndex];
            bool isAlt = _hoverRowIndex % 2 == 1;
            row.DefaultCellStyle.BackColor = isAlt ? _bgGridAlt : _bgGrid;
        }

        _hoverRowIndex = -1;
    }

    private void AddLogTab()
    {
        TabPage page = new()
        {
            Text = "Log",
            BackColor = _bgMain,
            ForeColor = _fgMain,
            Padding = new Padding(8)
        };

        _logBox.Dock = DockStyle.Fill;
        _logBox.ReadOnly = true;
        _logBox.BackColor = ColorTranslator.FromHtml("#05060a");
        _logBox.ForeColor = ColorTranslator.FromHtml("#45d483");
        _logBox.BorderStyle = BorderStyle.FixedSingle;
        _logBox.Font = new Font("Consolas", 10);
        _logBox.HideSelection = false;

        page.Controls.Add(_logBox);
        _tabControl.TabPages.Add(page);
    }

    private Control BuildStatusBar()
    {
        StatusStrip statusStrip = new()
        {
            Dock = DockStyle.Fill,
            BackColor = _bgPanel,
            ForeColor = _fgMain,
            SizingGrip = false
        };

        _statusCount.Text = "Items: 0";
        _statusCount.ForeColor = _fgMuted;

        _statusOperation.Text = "Ready";
        _statusOperation.ForeColor = _fgMain;

        statusStrip.Items.Add(_statusCount);
        statusStrip.Items.Add(new ToolStripStatusLabel { Spring = true });
        statusStrip.Items.Add(_statusOperation);

        return statusStrip;
    }

    private async Task ScanAsync(bool skipBusyCheck = false)
    {
        if (!skipBusyCheck && !SetBusyState(isBusy: true, "Scanning system packages..."))
        {
            return;
        }

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();

        try
        {
            AppendLog("[INFO] Starting scan across Win32 and UWP app inventories.", false);
            PackageScanner.ScanResult result = await _scanner.ScanAllAsync(_scanCts.Token);

            ReplaceTabData(PackageCategory.Win32, result.Win32Apps);
            ReplaceTabData(PackageCategory.Uwp, result.UwpApps);

            AppendLog($"[OK] Scan complete. Win32={result.Win32Apps.Count}, UWP={result.UwpApps.Count}", false);
            SetOperationStatus("Scan complete.");
            UpdateStatusCount();
        }
        catch (OperationCanceledException)
        {
            AppendLog("[WARN] Scan canceled.", true);
            SetOperationStatus("Scan canceled.");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] Scan failed: {ex.Message}", true);
            SetOperationStatus("Scan failed.");
        }
        finally
        {
            if (!skipBusyCheck)
            {
                SetBusyState(isBusy: false, "Ready");
            }
        }
    }

    private void ReplaceTabData(PackageCategory category, List<PackageEntry> items)
    {
        TabState tab = _tabs[category];
        tab.AllItems.Clear();
        tab.AllItems.AddRange(items);
        ApplyFilter(tab);
    }

    private void ApplyFilter(TabState tab)
    {
        string term = tab.SearchBox.Text.Trim();

        IEnumerable<PackageEntry> filtered = tab.AllItems;
        if (!string.IsNullOrWhiteSpace(term))
        {
            filtered = filtered.Where(x =>
                x.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                x.Publisher.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                x.Version.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                x.TypeBadge.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        tab.ViewItems.RaiseListChangedEvents = false;
        tab.ViewItems.Clear();
        foreach (PackageEntry item in filtered)
        {
            tab.ViewItems.Add(item);
        }

        tab.ViewItems.RaiseListChangedEvents = true;
        tab.ViewItems.ResetBindings();

        UpdateStatusCount();
    }

    private void SetSelectionForCurrentTab(bool selected)
    {
        if (!TryGetCurrentTabState(out TabState? tab))
        {
            return;
        }

        if (tab is null)
        {
            return;
        }

        foreach (PackageEntry item in tab.AllItems)
        {
            item.Selected = selected;
        }

        tab.ViewItems.ResetBindings();
        UpdateStatusCount();
    }

    private async Task UninstallSelectedAsync()
    {
        List<PackageEntry> selectedItems = _tabs.Values
            .SelectMany(x => x.AllItems)
            .Where(x => x.Selected)
            .ToList();

        if (selectedItems.Count == 0)
        {
            MessageBox.Show(
                "No items selected.",
                "Garuda Debloat",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        string preview = BuildConfirmationPreview(selectedItems);
        DialogResult confirm = MessageBox.Show(
            preview,
            "Confirm Uninstall",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (confirm != DialogResult.OK)
        {
            AppendLog("[INFO] Uninstall canceled by user.", false);
            return;
        }

        if (!SetBusyState(isBusy: true, "Uninstalling selected entries..."))
        {
            return;
        }

        _uninstallCts?.Cancel();
        _uninstallCts = new CancellationTokenSource();

        IProgress<UninstallEngine.LogMessage> progress = new UiProgress(this, message =>
        {
            AppendLog(message.Text, message.IsError);
            SetOperationStatus(message.Text);
        });

        try
        {
            AppendLog($"[INFO] Starting uninstall of {selectedItems.Count} selected entries.", false);
            UninstallEngine.UninstallResult result = await _uninstallEngine.UninstallSelectedAsync(selectedItems, progress, _uninstallCts.Token);

            string summary = $"[INFO] Completed. Success={result.Succeeded}, Failed={result.Failed}, Total={result.Total}";
            AppendLog(summary, result.Failed > 0);
            SetOperationStatus(summary);

            foreach (PackageEntry item in selectedItems)
            {
                item.Selected = false;
            }

            await ScanAsync(skipBusyCheck: true);

            if (result.FeatureDisabled)
            {
                DialogResult restart = MessageBox.Show(
                    "One or more Windows features were disabled. Restart Windows now?",
                    "Restart Recommended",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (restart == DialogResult.Yes)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "shutdown",
                        Arguments = "/r /t 5",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("[WARN] Uninstall operation canceled.", true);
            SetOperationStatus("Uninstall canceled.");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] Uninstall failed: {ex.Message}", true);
            SetOperationStatus("Uninstall failed.");
        }
        finally
        {
            SetBusyState(isBusy: false, "Ready");
            UpdateStatusCount();
        }
    }

    private string BuildConfirmationPreview(List<PackageEntry> items)
    {
        StringBuilder sb = new();
        sb.AppendLine("You are about to remove the following entries.");
        sb.AppendLine("This action may permanently remove installed data and cannot always be reversed.");
        sb.AppendLine();

        foreach (PackageEntry item in items.Take(30))
        {
            sb.AppendLine($"- [{item.TypeBadge}] {item.Name}");
        }

        if (items.Count > 30)
        {
            sb.AppendLine($"... and {items.Count - 30} more");
        }

        sb.AppendLine();
        sb.AppendLine("Continue?");
        return sb.ToString();
    }

    private bool EnsureFirstRunWarningAcknowledged()
    {
        const string keyPath = @"Software\GarudaVault\GarudaDebloat";
        const string valueName = "SafetyPromptAccepted";

        using RegistryKey root = Registry.CurrentUser;
        using RegistryKey key = root.CreateSubKey(keyPath, writable: true) ?? throw new InvalidOperationException("Unable to open settings key.");

        object? existing = key.GetValue(valueName);
        if (existing is int accepted && accepted == 1)
        {
            return true;
        }

        string warning =
            "Before continuing, please review this safety notice:\n\n" +
            "Garuda Debloat performs permanent software removal operations. " +
            "Uninstalling apps or cleaning residual files can affect system behavior and may not be fully reversible.\n\n" +
            "Proceed only if you understand the impact and have backups or restore options available.";

        DialogResult result = MessageBox.Show(
            warning,
            "Garuda Debloat Safety Notice",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (result != DialogResult.OK)
        {
            return false;
        }

        key.SetValue(valueName, 1, RegistryValueKind.DWord);
        return true;
    }

    private bool SetBusyState(bool isBusy, string operationText)
    {
        if (isBusy && _isBusy)
        {
            return false;
        }

        _btnScan.Enabled = !isBusy;
        _btnSelectAll.Enabled = !isBusy;
        _btnDeselectAll.Enabled = !isBusy;
        _btnUninstall.Enabled = !isBusy;
        _tabControl.Enabled = !isBusy;
        _isBusy = isBusy;

        Cursor = isBusy ? Cursors.WaitCursor : Cursors.Default;
        SetOperationStatus(operationText);

        return true;
    }

    private void SetOperationStatus(string message)
    {
        _statusOperation.Text = message.Length > 180 ? message[..180] + "..." : message;
    }

    private void AppendLog(string message, bool isError)
    {
        if (InvokeRequired)
        {
            Invoke(() => AppendLog(message, isError));
            return;
        }

        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor = isError ? _logError : _logOk;
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _logBox.SelectionColor = _logOk;
        _logBox.ScrollToCaret();
    }

    private bool TryGetCurrentTabState(out TabState? state)
    {
        state = null;
        TabPage? selected = _tabControl.SelectedTab;
        if (selected is null)
        {
            return false;
        }

        string text = selected.Text;
        if (text.StartsWith("Win32", StringComparison.OrdinalIgnoreCase))
        {
            state = _tabs[PackageCategory.Win32];
            return true;
        }

        if (text.StartsWith("UWP", StringComparison.OrdinalIgnoreCase))
        {
            state = _tabs[PackageCategory.Uwp];
            return true;
        }

        return false;
    }

    private void UpdateStatusCount()
    {
        int selectedCount = _tabs.Values.Sum(t => t.AllItems.Count(x => x.Selected));

        if (TryGetCurrentTabState(out TabState? currentTab) && currentTab is not null)
        {
            _statusCount.Text = $"Visible: {currentTab.ViewItems.Count}  |  Selected (All Tabs): {selectedCount}";
        }
        else
        {
            _statusCount.Text = $"Selected (All Tabs): {selectedCount}";
        }

    }
}
