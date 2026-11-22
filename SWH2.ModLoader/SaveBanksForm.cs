using System.Diagnostics;
using SWH2.Core;

namespace SWH2.ModLoader;

/// <summary>
/// Save banks (<see cref="SaveBanks"/>): copy a game slot into a named bank and put any bank back into any slot,
/// so more runs can be kept than the game has slots.
/// </summary>
public sealed class SaveBanksForm : Form
{
    readonly int _slotCount;
    readonly List<SaveBanks> _accounts = SaveBanks.Accounts();
    SaveBanks? _banks;

    readonly ComboBox _account = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    readonly ListView _slots = NewList(("Slot", 60), ("Last saved", 170), ("Size", 80));
    readonly ListView _bankList = NewList(("Bank", 260), ("Saved", 150), ("From", 70));
    readonly Button _store = new() { Text = "Slot → new bank…", AutoSize = true };
    readonly Button _load = new() { Text = "◀ Bank → slot", AutoSize = true };
    readonly Button _rename = new() { Text = "Rename…", AutoSize = true };
    readonly Button _delete = new() { Text = "Delete", AutoSize = true };
    readonly Button _openFolder = new() { Text = "Open banks folder", AutoSize = true };
    readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText, UseMnemonic = false };

    public SaveBanksForm(int slotCount)
    {
        _slotCount = slotCount;
        Text = "Save banks — SteamWorld Heist 2";
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(980, 560);
        MinimumSize = new Size(760, 420);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 4 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var help = new Label
        {
            AutoSize = true, UseMnemonic = false, MaximumSize = new Size(940, 0), Margin = new Padding(0, 0, 0, 8),
            Text = "A bank is a copy of one save slot kept outside the game. Store a slot to free it for a new run, " +
                   "put a bank back into any slot later. Loading into a used slot keeps that slot as an \"auto …\" bank first.",
        };
        root.Controls.Add(help, 0, 0);
        root.SetColumnSpan(help, 2);

        var slotsBox = new GroupBox { Text = "Game slots", Dock = DockStyle.Fill, Padding = new Padding(8) };
        slotsBox.Controls.Add(_slots);
        var banksBox = new GroupBox { Text = "Banks", Dock = DockStyle.Fill, Padding = new Padding(8) };
        banksBox.Controls.Add(_bankList);
        root.Controls.Add(slotsBox, 0, 1);
        root.Controls.Add(banksBox, 1, 1);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        buttons.Controls.AddRange([_store, _load, _rename, _delete, _openFolder]);
        if (_accounts.Count > 1)
        {
            buttons.Controls.Add(new Label { Text = "Steam account:", AutoSize = true, Margin = new Padding(16, 8, 4, 0) });
            buttons.Controls.Add(_account);
        }
        root.Controls.Add(buttons, 0, 2);
        root.SetColumnSpan(buttons, 2);
        root.Controls.Add(_status, 0, 3);
        root.SetColumnSpan(_status, 2);
        Controls.Add(root);

        foreach (var a in _accounts) _account.Items.Add(a.AccountId);
        _account.SelectedIndexChanged += (_, _) => { _banks = _accounts[_account.SelectedIndex]; RefreshLists(); };
        _slots.SelectedIndexChanged += (_, _) => UpdateButtons();
        _bankList.SelectedIndexChanged += (_, _) => UpdateButtons();
        _bankList.DoubleClick += (_, _) => Rename();
        _store.Click += (_, _) => Store();
        _load.Click += (_, _) => LoadBank();
        _rename.Click += (_, _) => Rename();
        _delete.Click += (_, _) => Delete();
        _openFolder.Click += (_, _) =>
        {
            if (_banks == null) return;
            Directory.CreateDirectory(_banks.BanksDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_banks.BanksDir}\"") { UseShellExecute = true });
        };

        if (_accounts.Count > 0) _account.SelectedIndex = 0; // the account that saved last
        else
        {
            _status.Text = $"No saves found in {SaveBanks.SavesRoot}. Start the game and save once.";
            UpdateButtons();
        }
    }

    static ListView NewList(params (string Title, int Width)[] columns)
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        foreach (var (title, width) in columns) list.Columns.Add(title, width);
        return list;
    }

    SaveBanks.Slot? SelectedSlot => _slots.SelectedItems.Count > 0 ? _slots.SelectedItems[0].Tag as SaveBanks.Slot : null;
    SaveBanks.Bank? SelectedBank => _bankList.SelectedItems.Count > 0 ? _bankList.SelectedItems[0].Tag as SaveBanks.Bank : null;

    void RefreshLists(int? selectSlot = null, string? selectBank = null)
    {
        if (_banks == null) return;
        selectSlot ??= SelectedSlot?.Index;
        selectBank ??= SelectedBank?.Name;

        _slots.BeginUpdate();
        _slots.Items.Clear();
        foreach (var s in _banks.Slots(_slotCount))
        {
            var item = new ListViewItem([(s.Index + 1).ToString(), s.Used ? $"{s.Modified:yyyy-MM-dd HH:mm}" : "(empty)", s.Used ? $"{s.Size / 1024} KB" : ""]) { Tag = s };
            if (!s.Used) item.ForeColor = SystemColors.GrayText;
            _slots.Items.Add(item);
            if (s.Index == selectSlot) item.Selected = true;
        }
        _slots.EndUpdate();

        _bankList.BeginUpdate();
        _bankList.Items.Clear();
        foreach (var b in _banks.Banks())
        {
            var item = new ListViewItem([b.Name, $"{b.Saved:yyyy-MM-dd HH:mm}", b.FromSlot is { } f ? $"slot {f + 1}" : ""]) { Tag = b };
            if (b.IsAuto) item.ForeColor = SystemColors.GrayText;
            _bankList.Items.Add(item);
            if (b.Name.Equals(selectBank, StringComparison.OrdinalIgnoreCase)) item.Selected = true;
        }
        _bankList.EndUpdate();
        UpdateButtons();
    }

    void UpdateButtons()
    {
        _store.Enabled = SelectedSlot is { Used: true };
        _load.Enabled = SelectedSlot != null && SelectedBank != null;
        _rename.Enabled = _delete.Enabled = SelectedBank != null;
        _openFolder.Enabled = _banks != null;
    }

    void Store()
    {
        if (_banks == null || SelectedSlot is not { Used: true } slot) return;
        var name = Prompt("Store slot", $"Name of the bank for slot {slot.Index + 1}:", $"Slot {slot.Index + 1} {DateTime.Now:yyyy-MM-dd HH-mm}");
        if (name == null) return;
        Try(() =>
        {
            bool overwrite = false;
            if (_banks.Find(SaveBanks.CleanName(name)) != null)
            {
                if (Ask($"Bank '{name}' already exists. Replace it?") != DialogResult.Yes) return;
                overwrite = true;
            }
            var bank = _banks.Store(slot.Index, name, overwrite);
            RefreshLists(selectBank: bank.Name);
            _status.Text = $"Slot {slot.Index + 1} stored as '{bank.Name}'. You can now delete the slot in the game and start a new run there.";
        });
    }

    void LoadBank()
    {
        if (_banks == null || SelectedSlot is not { } slot || SelectedBank is not { } bank) return;
        if (Process.GetProcessesByName("SteamWorld Heist 2").Length > 0)
        {
            MessageBox.Show(this, "Close the game first — it keeps the slots in memory and would write over the slot.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var question = slot.Used
            ? $"Put '{bank.Name}' into slot {slot.Index + 1}?\n\nThe save now in slot {slot.Index + 1} is kept as an \"auto …\" bank."
            : $"Put '{bank.Name}' into slot {slot.Index + 1}?";
        if (Ask(question) != DialogResult.Yes) return;
        Try(() =>
        {
            var auto = _banks.Load(bank.Name, slot.Index);
            RefreshLists(slot.Index, bank.Name);
            _status.Text = $"'{bank.Name}' is now in slot {slot.Index + 1}." + (auto != null ? $" The old slot was kept as '{auto.Name}'." : "");
        });
    }

    void Rename()
    {
        if (_banks == null || SelectedBank is not { } bank) return;
        var name = Prompt("Rename bank", "New name:", bank.Name);
        if (name == null || name == bank.Name) return;
        Try(() =>
        {
            var renamed = _banks.Rename(bank.Name, name);
            RefreshLists(selectBank: renamed.Name);
            _status.Text = $"Renamed to '{renamed.Name}'.";
        });
    }

    void Delete()
    {
        if (_banks == null || SelectedBank is not { } bank) return;
        if (Ask($"Delete bank '{bank.Name}'? This can't be undone.", MessageBoxIcon.Warning) != DialogResult.Yes) return;
        Try(() =>
        {
            _banks.Delete(bank.Name);
            RefreshLists();
            _status.Text = $"Deleted '{bank.Name}'.";
        });
    }

    void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            RefreshLists();
        }
    }

    DialogResult Ask(string question, MessageBoxIcon icon = MessageBoxIcon.Question) =>
        MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, icon);

    /// <summary>A one-line text prompt; null when cancelled.</summary>
    string? Prompt(string title, string label, string value)
    {
        using var dlg = new Form
        {
            Text = title, Font = Font, AutoScaleMode = AutoScaleMode.Font, FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            ClientSize = new Size(440, 130),
        };
        var text = new TextBox { Text = value, Location = new Point(12, 40), Width = 416 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(262, 86), AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(348, 86), AutoSize = true };
        dlg.Controls.AddRange([new Label { Text = label, AutoSize = true, Location = new Point(12, 12), UseMnemonic = false }, text, ok, cancel]);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        dlg.Shown += (_, _) => text.SelectAll();
        return dlg.ShowDialog(this) == DialogResult.OK && text.Text.Trim().Length > 0 ? text.Text.Trim() : null;
    }
}
