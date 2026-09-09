using RaceMind.Models;

namespace RaceMind.Services;

/// <summary>
/// Converts measured handling evidence into conservative setup changes.
/// Recommendations are suppressed when event count or confidence is too low.
/// </summary>
public sealed class SetupRecommendationEngine
{
    public IReadOnlyList<SetupRecommendation> Build(
        PhaseHandlingSummary entry,
        PhaseHandlingSummary mid,
        PhaseHandlingSummary exit,
        byte frontAntiRollBar,
        byte rearAntiRollBar,
        double rearBrakeBias,
        long absSamples,
        long tcSamples)
    {
        var result = new List<SetupRecommendation>();
        AddPhaseRecommendation(result, entry, frontAntiRollBar, rearAntiRollBar, rearBrakeBias, absSamples, tcSamples);
        AddPhaseRecommendation(result, mid, frontAntiRollBar, rearAntiRollBar, rearBrakeBias, absSamples, tcSamples);
        AddPhaseRecommendation(result, exit, frontAntiRollBar, rearAntiRollBar, rearBrakeBias, absSamples, tcSamples);

        return result
            .OrderByDescending(r => r.ConfidencePercent)
            .Take(3)
            .ToArray();
    }

    private static void AddPhaseRecommendation(
        List<SetupRecommendation> output,
        PhaseHandlingSummary phase,
        byte frontArb,
        byte rearArb,
        double rearBrakeBias,
        long absSamples,
        long tcSamples)
    {
        if (phase.Events < 3 || phase.ConfidencePercent < 65)
            return;
        if (phase.State is not (HandlingState.Understeer or HandlingState.Oversteer))
            return;

        var speed = SpeedLabel(phase.DominantSpeedBand);
        var issueCount = phase.State == HandlingState.Understeer ? phase.UndersteerEvents : phase.OversteerEvents;
        var problem = $"{StateLabel(phase.State)} ricorrente {PhaseLabel(phase.Phase)} nelle curve {speed}";
        var evidence = $"Presente in {issueCount}/{phase.Events} eventi analizzati; slip medio anteriore {phase.AverageFrontSlip:0.00} m/s, posteriore {phase.AverageRearSlip:0.00} m/s; carico asse anteriore {phase.AverageFrontLoadN:0} N, posteriore {phase.AverageRearLoadN:0} N.";

        string change;
        string expected;

        if (phase.State == HandlingState.Understeer)
        {
            (change, expected) = phase.Phase switch
            {
                CornerPhase.Entry => (
                    rearBrakeBias > 0.35
                        ? $"Sposta il brake bias di circa 0,5% verso il posteriore rispetto all'attuale {rearBrakeBias:P1}."
                        : "Riduci leggermente la stabilità in ingresso con una piccola variazione del brake bias verso il posteriore.",
                    "Più rotazione nella fase di rilascio/frenata, da validare senza compromettere la stabilità."),
                CornerPhase.Mid => (
                    frontArb > 0
                        ? $"Barra antirollio anteriore: prova -1 click rispetto al valore attuale {frontArb}."
                        : $"Barra antirollio posteriore: prova +1 click rispetto al valore attuale {rearArb}.",
                    "Aumentare la capacità dell'avantreno di seguire la traiettoria a centro curva."),
                _ => (
                    frontArb > 0
                        ? $"Barra antirollio anteriore: prova -1 click rispetto al valore attuale {frontArb}."
                        : "Riduci leggermente la rigidezza relativa dell'avantreno.",
                    "Ridurre la saturazione dell'avantreno in riapertura acceleratore.")
            };
        }
        else
        {
            (change, expected) = phase.Phase switch
            {
                CornerPhase.Entry => (
                    $"Sposta il brake bias di circa 0,5% verso l'anteriore rispetto all'attuale {rearBrakeBias:P1} posteriore.",
                    "Aumentare la stabilità del retrotreno durante la frenata e il turn-in."),
                CornerPhase.Mid => (
                    rearArb > 0
                        ? $"Barra antirollio posteriore: prova -1 click rispetto al valore attuale {rearArb}."
                        : $"Barra antirollio anteriore: prova +1 click rispetto al valore attuale {frontArb}.",
                    "Ridurre la tendenza del posteriore a superare il limite a centro curva."),
                _ => (
                    rearArb > 0
                        ? $"Barra antirollio posteriore: prova -1 click rispetto al valore attuale {rearArb}."
                        : "Aumenta leggermente la stabilità meccanica del retrotreno in trazione.",
                    tcSamples > 0
                        ? "Rendere più progressiva la trazione in uscita senza affidarsi solo al TC."
                        : "Aumentare la trazione e la stabilità del retrotreno in uscita.")
            };
        }

        output.Add(new SetupRecommendation(problem, evidence, change, phase.ConfidencePercent, expected));
    }

    private static string PhaseLabel(CornerPhase phase) => phase switch
    {
        CornerPhase.Entry => "in ingresso",
        CornerPhase.Mid => "a centro curva",
        _ => "in uscita"
    };

    private static string StateLabel(HandlingState state) => state == HandlingState.Understeer ? "Sottosterzo" : "Sovrasterzo";

    private static string SpeedLabel(CornerSpeedBand band) => band switch
    {
        CornerSpeedBand.Low => "lente",
        CornerSpeedBand.Medium => "medio-veloci",
        _ => "veloci"
    };
}
