using SentinelCore.Demon1.Learning;

namespace SentinelCore.Demon1.Learning.Controller;

internal static class LearningLabels
{
    public static string State(LearningState state) => state switch
    {
        LearningState.NO_PROFILE => "Aucun profil",
        LearningState.CALIBRATING => "En apprentissage",
        LearningState.PROVISIONAL => "Profil provisoire",
        LearningState.READY => "Profil prêt",
        LearningState.FROZEN => "Apprentissage suspendu",
        _ => "État inconnu"
    };

    public static string State(string? value) => Enum.TryParse<LearningState>(value, out var state) ? State(state) : value ?? "Inconnu";

    public static string Disposition(SampleDisposition disposition) => disposition switch
    {
        SampleDisposition.ACCEPTED => "Acceptée",
        SampleDisposition.QUARANTINED => "En quarantaine",
        SampleDisposition.REJECTED => "Écartée",
        _ => "Inconnue"
    };

    public static string Feature(string key) => key switch
    {
        "typing.rate" => "Cadence de frappe (touches/min)",
        "typing.dwell_median_ms" => "Durée médiane d’appui (ms)",
        "typing.flight_median_ms" => "Intervalle médian entre frappes (ms)",
        "typing.pause_ratio" => "Part des pauses longues",
        "mouse.speed_median" => "Vitesse médiane de la souris",
        "mouse.acceleration_p95" => "Accélération de la souris (95e percentile)",
        "mouse.curvature_median" => "Changement médian de direction",
        "mouse.click_interval_median_ms" => "Intervalle médian entre clics (ms)",
        _ => key.Replace('.', ' ').Replace('_', ' ')
    };

    public static string Context(string key) => key switch
    {
        "LOCAL|DEFAULT_KB|DEFAULT_MOUSE" => "Session locale — clavier et souris standard",
        "LOCAL|INTEGRATED_KB|MOUSE_USB" => "Session locale — clavier intégré et souris USB",
        _ => string.Join(" · ", key.Split('|').Select(part => part switch
        {
            "PROFIL_1" => "Profil 1", "PROFIL_2" => "Profil 2", "PROFIL_3" => "Profil 3", "PROFIL_4" => "Profil 4",
            "PROFIL_5" => "Profil 5", "PROFIL_6" => "Profil 6", "PROFIL_7" => "Profil 7", "PROFIL_8" => "Profil 8",
            "LOCAL" => "Locale", "DEFAULT_KB" => "Clavier standard", "DEFAULT_MOUSE" => "Souris standard",
            "INTEGRATED_KB" => "Clavier intégré", "MOUSE_USB" => "Souris USB", _ => part.Replace('_', ' ')
        }))
    };
}
