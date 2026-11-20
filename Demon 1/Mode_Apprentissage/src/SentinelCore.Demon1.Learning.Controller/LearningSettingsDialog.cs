namespace SentinelCore.Demon1.Learning.Controller;

internal sealed class LearningSettingsDialog : Form
{
    private readonly NumericUpDown _window = new() { Minimum = 10, Maximum = 60, Increment = 5, Width = 110 };
    private readonly NumericUpDown _idle = new() { Minimum = 30, Maximum = 900, Increment = 30, Width = 110 };
    public LearningSettings Value => new((int)_window.Value, (int)_idle.Value);

    public LearningSettingsDialog(LearningSettings settings)
    {
        Text = "Paramètres du mode apprentissage"; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _window.Value = settings.WindowSeconds; _idle.Value = settings.IdleStopSeconds;
        var panel = new TableLayoutPanel { Padding = new Padding(18), AutoSize = true, ColumnCount = 2 };
        panel.Controls.Add(new Label { Text = "Durée d’une période de mesure (secondes) :", AutoSize = true, Anchor = AnchorStyles.Left }); panel.Controls.Add(_window);
        panel.Controls.Add(new Label { Text = "Arrêt après inactivité (secondes) :", AutoSize = true, Anchor = AnchorStyles.Left }); panel.Controls.Add(_idle);
        panel.Controls.Add(new Label { Text = "Le texte saisi n’est pas enregistré. Ces valeurs s’appliquent au prochain démarrage.", AutoSize = true, MaximumSize = new Size(380, 0) });
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var save = new Button { Text = "Enregistrer", DialogResult = DialogResult.OK, AutoSize = true }; var cancel = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel); panel.Controls.Add(buttons); panel.SetColumnSpan(buttons, 2); panel.SetColumnSpan(panel.GetControlFromPosition(0, 2)!, 2);
        Controls.Add(panel); AcceptButton = save; CancelButton = cancel;
    }
}
