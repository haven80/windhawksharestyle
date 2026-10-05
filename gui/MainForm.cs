using System.Text;
using System.Text.Json;

namespace WindhawkShare.Gui;

/// <summary>
/// Interfaccia grafica: solo uno strato sopra Exporter e Importer, la logica è la stessa del terminale.
/// </summary>
internal sealed class MainForm : Form
{
    private const string LoadingTag = "__loading";
    private static readonly HttpClient Http = CreateHttpClient();

    private WindhawkCli? _cli;
    private List<InstalledMod> _installed = new();
    private bool _updatingChecks;

    // Esporta
    private readonly TreeView _exportTree = new() { CheckBoxes = true, Dock = DockStyle.Fill, HideSelection = false };
    private readonly TextBox _nameBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _authorBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _descriptionBox = new() { Dock = DockStyle.Fill };
    private readonly Button _refreshButton = new() { Text = "Aggiorna elenco", AutoSize = true };
    private readonly Button _saveButton = new() { Text = "Salva pacchetto...", AutoSize = true };

    // Importa
    private readonly Button _openButton = new() { Text = "Apri pacchetto...", AutoSize = true };
    private readonly CheckBox _exactVersionBox = new()
    {
        Text = "Installa la versione indicata nel pacchetto (invece dell'ultima)",
        AutoSize = true,
        Margin = new Padding(12, 6, 3, 3),
    };
    private readonly Label _packageInfo = new() { AutoSize = true, Text = "Nessun pacchetto aperto.", Padding = new Padding(0, 4, 0, 4) };
    private readonly TreeView _importTree = new() { CheckBoxes = true, Dock = DockStyle.Fill };
    private readonly Button _applyButton = new() { Text = "Applica", AutoSize = true, Enabled = false };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
    };

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ToolStripStatusLabel _status = new() { Text = "Pronto" };

    private SetupPackage? _package;
    private string? _packagePath;

    public MainForm()
    {
        Text = "Windhawk Share";
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(860, 680);
        MinimumSize = new Size(640, 480);

        _tabs.TabPages.Add(BuildExportTab());
        _tabs.TabPages.Add(BuildImportTab());

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);

        Controls.Add(_tabs);
        Controls.Add(statusStrip);

        Load += async (_, _) => await StartupAsync();
    }

    // ===================== Costruzione dell'interfaccia =====================

    private TabPage BuildExportTab()
    {
        var page = new TabPage("Esporta") { Padding = new Padding(8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 6),
            Text = "Spunta le mod da condividere. Espandi una mod per scegliere le singole impostazioni " +
                   "(senza espanderla vengono incluse tutte). Le mod locali non sono condivisibili.",
            MaximumSize = new Size(800, 0),
        });
        layout.Controls.Add(_exportTree);

        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(fields, "Nome del pacchetto:", _nameBox);
        AddField(fields, "Autore:", _authorBox);
        AddField(fields, "Descrizione:", _descriptionBox);
        layout.Controls.Add(fields);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.Add(_saveButton);
        buttons.Controls.Add(_refreshButton);
        layout.Controls.Add(buttons);

        page.Controls.Add(layout);

        _exportTree.BeforeCheck += ExportTree_BeforeCheck;
        _exportTree.AfterCheck += ExportTree_AfterCheck;
        _exportTree.BeforeExpand += async (_, e) => await LoadSettingsNodesAsync(e.Node!);
        _refreshButton.Click += async (_, _) => await LoadInstalledAsync();
        _saveButton.Click += async (_, _) => await SavePackageAsync();
        return page;
    }

    private TabPage BuildImportTab()
    {
        var page = new TabPage("Importa") { Padding = new Padding(8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));

        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        top.Controls.Add(_openButton);
        top.Controls.Add(_exactVersionBox);
        layout.Controls.Add(top);

        _packageInfo.MaximumSize = new Size(800, 0);
        layout.Controls.Add(_packageInfo);
        layout.Controls.Add(_importTree);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.Add(_applyButton);
        layout.Controls.Add(buttons);
        layout.Controls.Add(_log);

        page.Controls.Add(layout);

        _openButton.Click += async (_, _) => await OpenPackageAsync();
        _exactVersionBox.CheckedChanged += async (_, _) => { if (_package is not null) await PlanAsync(); };
        _importTree.BeforeCheck += (_, e) =>
        {
            // Solo i nodi delle mod sono spuntabili, e solo se c'è qualcosa da fare.
            if (e.Node?.Tag is not PlannedMod p || p.Action == ModAction.Skip) e.Cancel = true;
        };
        _applyButton.Click += async (_, _) => await ApplyAsync();
        return page;
    }

    private static void AddField(TableLayoutPanel panel, string label, Control box)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
        panel.Controls.Add(box);
    }

    // ===================== Avvio =====================

    private async Task StartupAsync()
    {
        try
        {
            _cli = WindhawkCli.Locate();
        }
        catch (WindhawkCliException)
        {
            var answer = MessageBox.Show(this,
                "Non trovo windhawk-cli.exe. Serve Windhawk 2.0 o successivo.\n\n" +
                "Vuoi indicare tu dove si trova?", "Windhawk Share",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes)
            {
                using var dialog = new OpenFileDialog
                {
                    Title = "Seleziona windhawk-cli.exe",
                    Filter = "windhawk-cli.exe|windhawk-cli.exe",
                };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    try { _cli = WindhawkCli.Locate(dialog.FileName); }
                    catch (WindhawkCliException e) { ShowError(e.Message); }
                }
            }
            if (_cli is null)
            {
                Close();
                return;
            }
        }

        await LoadInstalledAsync();
    }

    // ===================== Esporta =====================

    private async Task LoadInstalledAsync()
    {
        if (_cli is null) return;
        await RunBusyAsync("Lettura delle mod installate...", async () =>
        {
            _installed = await _cli.ListInstalledModsAsync();

            _exportTree.BeginUpdate();
            _exportTree.Nodes.Clear();
            foreach (var mod in _installed.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var isLocal = !OfficialCheck.IsValidModId(mod.Id);
                var node = new TreeNode(isLocal
                    ? $"{mod.Name}  ({mod.Id}) — mod locale, non condivisibile"
                    : $"{mod.Name}  ({mod.Id}, {mod.Version}){(mod.Enabled ? "" : " — disattivata")}")
                {
                    Tag = mod,
                };
                if (isLocal) node.ForeColor = SystemColors.GrayText;
                else node.Nodes.Add(new TreeNode("caricamento...") { Tag = LoadingTag });
                _exportTree.Nodes.Add(node);
                if (isLocal) node.HideCheckBox();
                else node.Nodes[0].HideCheckBox();
            }
            _exportTree.EndUpdate();
            _status.Text = $"{_installed.Count} mod installate";
        });
    }

    private async Task LoadSettingsNodesAsync(TreeNode modNode)
    {
        if (_cli is null || modNode.Tag is not InstalledMod mod) return;
        if (modNode.Nodes.Count != 1 || modNode.Nodes[0].Tag as string != LoadingTag) return;

        try
        {
            var settings = await _cli.GetSettingsAsync(mod.Id);
            var roots = SettingsTools.Roots(settings);

            _updatingChecks = true;
            _exportTree.BeginUpdate();
            modNode.Nodes.Clear();
            foreach (var (root, count) in roots)
            {
                modNode.Nodes.Add(new TreeNode(count == 1 ? root : $"{root}  ({count} valori)")
                {
                    Tag = root,
                    Checked = modNode.Checked,
                });
            }
            if (roots.Count == 0)
                modNode.Text += " — nessuna impostazione";
            _exportTree.EndUpdate();
            _updatingChecks = false;
        }
        catch (WindhawkCliException e)
        {
            modNode.Nodes[0].Text = "errore: " + e.Message;
        }
    }

    private void ExportTree_BeforeCheck(object? sender, TreeViewCancelEventArgs e)
    {
        if (e.Node is null) return;
        if (e.Node.Tag is InstalledMod mod && !OfficialCheck.IsValidModId(mod.Id)) e.Cancel = true;
        if (e.Node.Tag as string == LoadingTag) e.Cancel = true;
    }

    private void ExportTree_AfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_updatingChecks || e.Node is null) return;
        _updatingChecks = true;
        try
        {
            if (e.Node.Tag is InstalledMod)
            {
                // Spuntare la mod spunta tutte le sue impostazioni.
                foreach (TreeNode child in e.Node.Nodes)
                    if (child.Tag as string != LoadingTag) child.Checked = e.Node.Checked;
            }
            else if (e.Node.Parent is { } parent)
            {
                // Spuntare un'impostazione spunta la mod.
                if (e.Node.Checked) parent.Checked = true;
            }
        }
        finally
        {
            _updatingChecks = false;
        }
    }

    private List<ModSelection> CollectSelections()
    {
        var result = new List<ModSelection>();
        foreach (TreeNode node in _exportTree.Nodes)
        {
            if (!node.Checked || node.Tag is not InstalledMod mod) continue;

            var settingNodes = node.Nodes.Cast<TreeNode>().Where(n => n.Tag is string s && s != LoadingTag).ToList();
            if (settingNodes.Count == 0 || settingNodes.All(n => n.Checked))
            {
                result.Add(new ModSelection(mod.Id, null)); // tutte le impostazioni
            }
            else
            {
                var roots = settingNodes.Where(n => n.Checked).Select(n => (string)n.Tag!).ToHashSet(StringComparer.Ordinal);
                result.Add(new ModSelection(mod.Id, roots));
            }
        }
        return result;
    }

    private async Task SavePackageAsync()
    {
        if (_cli is null) return;

        var selections = CollectSelections();
        if (selections.Count == 0)
        {
            ShowInfo("Spunta almeno una mod da condividere.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_nameBox.Text))
        {
            ShowInfo("Dai un nome al pacchetto.");
            _nameBox.Focus();
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Salva pacchetto",
            Filter = "Pacchetto Windhawk Share (*.json)|*.json",
            FileName = SafeFileName(_nameBox.Text) + ".json",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        await RunBusyAsync("Verifica con il repository ufficiale...", async () =>
        {
            var meta = new PackageMeta
            {
                Name = _nameBox.Text.Trim(),
                Author = _authorBox.Text.Trim(),
                Description = _descriptionBox.Text.Trim(),
                CreatedAt = DateTimeOffset.UtcNow,
                WindhawkVersion = await _cli.GetVersionAsync(),
                Windows = Exporter.CurrentWindows(),
            };
            var check = new OfficialCheck(_cli, Http, OfficialCheck.DefaultModsSourceDir);
            var report = await new Exporter(_cli, check).BuildAsync(selections, meta, _installed);

            var text = new StringBuilder();
            foreach (var (id, reason) in report.Excluded) text.AppendLine($"Esclusa {id}: {reason}");
            foreach (var w in report.Warnings) text.AppendLine($"Avviso: {w}");

            if (report.Package.Mods.Count == 0)
            {
                ShowError("Nessuna mod esportabile, pacchetto non creato.\n\n" + text);
                return;
            }

            var json = JsonSerializer.Serialize(report.Package, PackageJsonContext.Default.SetupPackage);
            await File.WriteAllTextAsync(dialog.FileName, json, new UTF8Encoding(false));

            var summary = new StringBuilder($"Pacchetto salvato:\n{dialog.FileName}\n\n");
            foreach (var m in report.Package.Mods) summary.AppendLine($"• {m.Id} {m.Version}: {m.Settings.Count} valori");
            if (text.Length > 0) summary.AppendLine().Append(text);
            ShowInfo(summary.ToString());
            _status.Text = "Pacchetto salvato";
        });
    }

    // ===================== Importa =====================

    private async Task OpenPackageAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Apri pacchetto",
            Filter = "Pacchetto Windhawk Share (*.json)|*.json|Tutti i file|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _package = ImportValidation.Load(dialog.FileName);
            _packagePath = dialog.FileName;
        }
        catch (WindhawkCliException e)
        {
            ShowError(e.Message);
            return;
        }

        var meta = _package.Meta;
        var current = Exporter.CurrentWindows();
        var info = new StringBuilder();
        info.AppendLine($"{meta.Name}   di {(string.IsNullOrWhiteSpace(meta.Author) ? "?" : meta.Author)}");
        if (!string.IsNullOrWhiteSpace(meta.Description)) info.AppendLine(meta.Description);
        info.Append($"Creato su {meta.Windows.Product} (build {meta.Windows.Build}), Windhawk {meta.WindhawkVersion ?? "?"}");
        if (current.Product != meta.Windows.Product)
            info.Append($"\nAttenzione: tu hai {current.Product}, alcune mod potrebbero non funzionare.");
        _packageInfo.Text = info.ToString();

        await PlanAsync();
    }

    private async Task PlanAsync()
    {
        if (_cli is null || _package is null) return;

        await RunBusyAsync("Verifica con il repository ufficiale...", async () =>
        {
            var plan = await new Importer(_cli).PlanAsync(_package, null, _exactVersionBox.Checked);

            _importTree.BeginUpdate();
            _importTree.Nodes.Clear();
            foreach (var p in plan)
            {
                var what = p.Action switch
                {
                    ModAction.Install => $"da installare ({p.InstallVersion ?? "ultima versione"}), {p.Settings.Count} impostazioni",
                    ModAction.UpdateSettingsOnly => $"già installata, aggiorna {p.Settings.Count} impostazioni",
                    _ => "ignorata",
                };
                var node = new TreeNode($"{p.Source.Id} — {what}") { Tag = p };
                if (p.Action == ModAction.Skip) node.ForeColor = SystemColors.GrayText;

                foreach (var note in p.Notes)
                    node.Nodes.Add(new TreeNode(note) { ForeColor = SystemColors.GrayText });

                foreach (var (key, value) in p.Settings.Where(kv => ImportValidation.LooksLikePathOrCommand(kv.Value)))
                {
                    var shown = value.Length > 120 ? value[..120] + "..." : value;
                    node.Nodes.Add(new TreeNode($"⚠ controlla: {key} = {shown}") { ForeColor = Color.DarkOrange });
                }

                _importTree.Nodes.Add(node);
                foreach (TreeNode child in node.Nodes) child.HideCheckBox();
                if (p.Action == ModAction.Skip) node.HideCheckBox();
                // Spunta dopo l'aggiunta: BeforeCheck blocca solo le mod da saltare.
                node.Checked = p.Action != ModAction.Skip;
                if (node.Nodes.Count > 0 && p.Action != ModAction.Skip) node.Expand();
            }
            _importTree.EndUpdate();

            _applyButton.Enabled = plan.Any(p => p.Action != ModAction.Skip);
            _status.Text = "Controlla il piano, poi premi Applica";
        });
    }

    private async Task ApplyAsync()
    {
        if (_cli is null || _package is null) return;

        var chosen = _importTree.Nodes.Cast<TreeNode>()
            .Where(n => n.Checked && n.Tag is PlannedMod p && p.Action != ModAction.Skip)
            .Select(n => (PlannedMod)n.Tag!)
            .ToList();
        if (chosen.Count == 0)
        {
            ShowInfo("Nessuna mod selezionata.");
            return;
        }

        var installs = chosen.Count(p => p.Action == ModAction.Install);
        var updates = chosen.Count - installs;
        var hasWarnings = chosen.Any(p => p.Settings.Any(kv => ImportValidation.LooksLikePathOrCommand(kv.Value)));
        var question = $"Verranno installate {installs} mod e aggiornate le impostazioni di {updates}.";
        if (hasWarnings) question += "\n\nAlcune impostazioni contengono percorsi o comandi (in arancione): le hai controllate?";
        question += "\n\nProcedere?";

        if (MessageBox.Show(this, question, "Conferma", MessageBoxButtons.YesNo,
                hasWarnings ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        await RunBusyAsync("Applicazione in corso...", async () =>
        {
            _log.Clear();
            AppendLog($"Pacchetto: {Path.GetFileName(_packagePath)}");
            var summary = await new Importer(_cli).ExecuteAsync(chosen, AppendLog);
            AppendLog("");
            AppendLog("Riepilogo:");
            foreach (var line in summary) AppendLog("  " + line);
            _status.Text = summary.Any(l => l.StartsWith("ERRORE")) ? "Completato con errori" : "Completato";
        });

        // Le mod installate sono cambiate: si aggiornano elenco e piano.
        await LoadInstalledAsync();
        await PlanAsync();
    }

    private void AppendLog(string line)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(line)); return; }
        _log.AppendText(line + Environment.NewLine);
    }

    // ===================== Utilità =====================

    private async Task RunBusyAsync(string status, Func<Task> work)
    {
        _status.Text = status;
        _tabs.Enabled = false;
        UseWaitCursor = true;
        try
        {
            await work();
        }
        catch (WindhawkCliException e)
        {
            ShowError(e.Message);
            _status.Text = "Errore";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            ShowError(e.Message);
            _status.Text = "Errore";
        }
        finally
        {
            UseWaitCursor = false;
            _tabs.Enabled = true;
        }
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "Windhawk Share", MessageBoxButtons.OK, MessageBoxIcon.Error);

    private void ShowInfo(string message) =>
        MessageBox.Show(this, message, "Windhawk Share", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        return cleaned.Length == 0 ? "pacchetto" : cleaned;
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("windhawk-share-gui/0.1");
        return http;
    }
}
