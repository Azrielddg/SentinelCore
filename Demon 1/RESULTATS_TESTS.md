# Résultats des tests D1

`dotnet build SentinelCore.Demon1.slnx`: réussite, sans avertissement ni erreur.
`dotnet run --project tests/SentinelCore.Demon1.Tests`: réussite. Vérifiés: baseline, score robuste, exclusion des écarts, gel persistant sur finding D2, reprise par propriétaire authentifié, refus sans consentement, replay des 50 cas et stockage DPAPI avec historique.

Répartition simulée: 15 bénins admis; 20 écarts suspects exclus; 15 ambigus/degradés exclus. Ces chiffres décrivent le jeu synthétique; ils ne sont pas une mesure terrain de faux positifs/faux négatifs. Aucun capteur Windows ni benchmark réel n’a été exécuté.
