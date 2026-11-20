using System.Diagnostics;
using System.Security.Principal;
using SentinelCore.Demon1.Learning;
using Windows.Security.Credentials.UI;

namespace SentinelCore.Demon1.Learning.Controller;

internal sealed class MainForm : Form
{
    private readonly AuthStore _auth;
    private readonly string _sid = WindowsIdentity.GetCurrent().User!.Value;
    private readonly string _dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SentinelCore", "Demon1Learning");
    private LearningSettings _settings;
    private readonly Label _state = new() { AutoSize = true, Font = new Font("Segoe UI", 12, FontStyle.Bold) };
    private readonly Label _progress = new() { AutoSize = true };
    private readonly Label _measures = new() { AutoSize = true, MaximumSize = new Size(720, 0) };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1000 };
    private Process? _worker;

    public MainForm(AuthStore auth)
    {
        _auth = auth;
        _settings = LearningSettings.Load(_dataRoot);
        Text = "SentinelCore — contrôle du mode apprentissage (Démon 1)";
        StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(820, 620); Size = new Size(930, 700);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label { Text = "Apprentissage comportemental — clavier et souris", AutoSize = true, Font = new Font("Segoe UI", 16, FontStyle.Bold) }, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 12, 0, 12) };
        buttons.Controls.Add(Button("Démarrer avec Windows Hello", StartWithHelloAsync, 230));
        buttons.Controls.Add(Button("Démarrer avec le mot de passe maître", StartWithPassword, 270));
        buttons.Controls.Add(Button("Arrêter", StopLearning, 100));
        buttons.Controls.Add(Button("Voir la base", ShowDatabase, 120));
        buttons.Controls.Add(Button("Paramètres", ShowSettings, 120));
        buttons.Controls.Add(Button("Changer le mot de passe", ChangePassword, 180));
        root.Controls.Add(buttons, 0, 1);
        var statePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown };
        statePanel.Controls.Add(_state); statePanel.Controls.Add(_progress); statePanel.Controls.Add(_measures);
        root.Controls.Add(statePanel, 0, 2);
        root.Controls.Add(_log, 0, 3);
        root.Controls.Add(new Label { Text = "Aucune collecte ne commence à l’ouverture. Une authentification et une confirmation sont nécessaires à chaque démarrage.", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 10, 0, 0) }, 0, 4);
        Controls.Add(root);
        _refresh.Tick += (_, _) => RefreshStatus(); _refresh.Start();
        FormClosing += (_, e) =>
        {
            if (_worker is { HasExited: false })
            {
                var choice = MessageBox.Show("La collecte est active. Veux-tu l’arrêter avant de fermer ?", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (choice == DialogResult.Cancel) { e.Cancel = true; return; }
                if (choice == DialogResult.Yes) StopLearning();
            }
        };
        Append("Base locale : " + Path.Combine(_dataRoot, "learning.db"));
        Append("Les mesures sont agrégées; aucun texte tapé n’est enregistré.");
    }

    private static Button Button(string text, Action action, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 42, Margin = new Padding(4) };
        button.Click += (_, _) => action(); return button;
    }

    private async void StartWithHelloAsync()
    {
        if (IsRunning()) { Append("L’apprentissage est déjà actif."); return; }
        try
        {
            var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(Handle, "Confirme ton identité avant de démarrer l’apprentissage SentinelCore.");
            if (result != UserConsentVerificationResult.Verified)
            {
                Append($"Windows Hello n’a pas confirmé l’identité : {result}. Tu peux choisir le mot de passe maître.");
                MessageBox.Show($"La vérification Windows Hello n’a pas abouti ({result}).\nAucune collecte n’a démarré. Tu peux réessayer ou utiliser le mot de passe maître.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ConfirmAndStart("Windows Hello / biométrie Windows");
        }
        catch (Exception ex)
        {
            Append("Windows Hello indisponible : " + ex.Message);
            MessageBox.Show("Windows Hello n’est pas disponible ici. La collecte n’a pas démarré. Utilise le mot de passe maître ou vérifie la configuration Windows Hello.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void StartWithPassword()
    {
        if (IsRunning()) { Append("L’apprentissage est déjà actif."); return; }
        using var dialog = new MasterPasswordDialog(isSetup: false);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Password is null) return;
        bool valid;
        try { valid = _auth.VerifyPassword(dialog.Password); }
        catch (Exception ex) { MessageBox.Show("Configuration du mot de passe illisible : " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (!valid) { Append("Mot de passe maître incorrect; aucun démarrage."); MessageBox.Show("Mot de passe incorrect. Aucune collecte n’a démarré.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        ConfirmAndStart("Mot de passe maître");
    }

    private void ConfirmAndStart(string method)
    {
        if (MessageBox.Show($"Identité confirmée par {method}. Démarrer maintenant l’apprentissage du clavier et de la souris ?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        StartWorker();
    }

    private void StartWorker()
    {
        try
        {
            Directory.CreateDirectory(_dataRoot);
            var stopPath = Path.Combine(_dataRoot, "stop.signal"); if (File.Exists(stopPath)) File.Delete(stopPath);
            var ticketPath = Path.Combine(_dataRoot, "auth-ticket-" + Guid.NewGuid().ToString("N") + ".bin");
            var proof = OneTimeAuthorizationTicket.Create(ticketPath, _sid);
            var worker = new Process { StartInfo = new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden } };
            foreach (var argument in new[] { "--worker", "collect", "--db", Path.Combine(_dataRoot, "learning.db"), "--sid", _sid, "--context", "LOCAL|DEFAULT_KB|DEFAULT_MOUSE", "--status", Path.Combine(_dataRoot, "status.json"), "--stop", stopPath, "--auth-ticket", ticketPath, "--auth-proof", proof, "--window-seconds", _settings.WindowSeconds.ToString(), "--idle-seconds", _settings.IdleStopSeconds.ToString(), "--ignore-pid", Environment.ProcessId.ToString() }) worker.StartInfo.ArgumentList.Add(argument);
            if (!worker.Start()) throw new InvalidOperationException("Le collecteur n’a pas démarré.");
            _worker = worker;
            Append("Collecte démarrée après authentification et confirmation explicite.");
            RefreshStatus();
        }
        catch (Exception ex) { Append("Échec du démarrage : " + ex.Message); MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void StopLearning()
    {
        if (!IsRunning()) { Append("Aucune collecte active."); return; }
        try
        {
            File.WriteAllText(Path.Combine(_dataRoot, "stop.signal"), "stop");
            if (!_worker!.WaitForExit(5000)) { _worker.Kill(entireProcessTree: true); _worker.WaitForExit(3000); }
            Append("Collecte arrêtée; les périodes enregistrées restent dans la base."); RefreshStatus();
        }
        catch (Exception ex) { Append("Erreur à l’arrêt : " + ex.Message); }
    }

    private bool IsRunning() => _worker is { HasExited: false };

    private void RefreshStatus()
    {
        var active = IsRunning(); _state.Text = active ? "ÉTAT : APPRENTISSAGE ACTIF" : "ÉTAT : ARRÊTÉ"; _state.ForeColor = active ? Color.DarkGreen : Color.DimGray;
        var path = Path.Combine(_dataRoot, "status.json"); if (!File.Exists(path)) return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)); var json = doc.RootElement;
            _progress.Text = $"Profil : {Value(json, "State")} | fenêtres acceptées : {Value(json, "AcceptedWindows")} | jours : {Value(json, "DistinctDays")} | calibration : {Value(json, "CalibrationProgressPercent")}%";
            _measures.Text = "Mesures de la dernière période : " + (json.TryGetProperty("latestMeasures", out var measures) ? string.Join(" · ", measures.EnumerateObject().Select(x => $"{x.Name}={x.Value}")) : "en attente");
            var note = Value(json, "note"); if (!string.IsNullOrWhiteSpace(note)) _measures.Text += "\nNote : " + note;
        }
        catch (IOException) { }
        catch (System.Text.Json.JsonException) { }
    }

    private static string Value(System.Text.Json.JsonElement json, string name) => json.TryGetProperty(name, out var value) ? value.ToString() : "—";

    private void ShowDatabase()
    {
        try
        {
            using var repository = new SqliteLearningProfileRepository(Path.Combine(_dataRoot, "learning.db"));
            var entries = repository.ReadRecent(_sid, 200).Select(x => new
            {
                Date = x.ObservedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Résultat = x.Disposition.ToString(), Profil = x.State.ToString(), Fenêtres = x.AcceptedWindows,
                Qualité = Math.Round(x.Quality, 2), Mesures = string.Join("; ", x.Features.Select(f => $"{f.Key}={Math.Round(f.Value, 3)}")), Raison = x.Reason
            }).ToArray();
            var form = new Form { Text = "Base d’apprentissage — dernières périodes", StartPosition = FormStartPosition.CenterParent, Size = new Size(1000, 600), MinimumSize = new Size(800, 400) };
            var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells, DataSource = entries };
            form.Controls.Add(grid); form.ShowDialog(this);
        }
        catch (Exception ex) { MessageBox.Show("Impossible de lire la base : " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ChangePassword()
    {
        using var oldPassword = new MasterPasswordDialog(isSetup: false) { Text = "Vérifier le mot de passe actuel" };
        if (oldPassword.ShowDialog(this) != DialogResult.OK || oldPassword.Password is null || !_auth.VerifyPassword(oldPassword.Password)) { Append("Changement de mot de passe annulé ou vérification incorrecte."); return; }
        using var newPassword = new MasterPasswordDialog(isSetup: true) { Text = "Définir le nouveau mot de passe maître" };
        if (newPassword.ShowDialog(this) == DialogResult.OK && newPassword.Password is not null) { _auth.SetPassword(newPassword.Password); Append("Mot de passe maître modifié."); }
    }

    private void ShowSettings()
    {
        using var dialog = new LearningSettingsDialog(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _settings = dialog.Value; _settings.Save(_dataRoot);
        Append($"Paramètres enregistrés : période {_settings.WindowSeconds}s, arrêt après {_settings.IdleStopSeconds}s d’inactivité.");
    }

    private void Append(string message) => _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
}
