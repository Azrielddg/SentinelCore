namespace SentinelCore.Demon1.Learning.Controller;

internal sealed class VisualReportsDialog : Form
{
    public VisualReportsDialog(string dataRoot)
    {
        Text = "Rapports de vérification visuelle — Démon 1"; StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1100, 560); MinimumSize = new Size(850, 420);
        var rows = VisualCheckReportStore.ReadRecent(dataRoot).Select(x => new
        {
            Date = x.ObservedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"),
            Source = x.Source, Résultat = x.Status, Présence = x.Liveness, Profil = x.ProfileId ?? "—",
            Similarité = x.Similarity?.ToString("0.000") ?? "—", Qualité = x.FaceQuality.ToString("0.00"),
            Images = x.ValidFrames, Motif = x.ReasonCode, Diagnostic = x.DiagnosticSummary ?? "—",
            Preuve = x.EvidenceRef ?? "—", Corrélation = x.CorrelationId
        }).ToArray();
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells, AllowUserToAddRows = false, DataSource = rows };
        Controls.Add(grid);
    }
}
