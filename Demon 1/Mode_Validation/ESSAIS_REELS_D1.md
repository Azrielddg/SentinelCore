# Essais réels du démon 1 — 21 novembre 2026

## Ce qui est testé

Le programme `SentinelCore.Demon1.VisualSimulation` construit une vraie requête `VISUAL_IDENTITY_REQUEST`, la remet au gestionnaire réel de D1, ouvre la caméra configurée pendant cinq secondes et traite les images avec YuNet/SFace/MiniFASNetV2. Il ne choisit pas lui-même le résultat. Les images ne sont pas gardées comme vidéo; les gabarits et preuves temporaires sont supprimés à la fin. Le journal ne garde que des diagnostics et le résultat.

Cette simulation appelle le gestionnaire D1 dans le même processus. Elle ne constitue pas encore un essai d’intégration entre deux processus Windows : le démon 3 de ce dépôt contient actuellement ses spécifications, pas son serveur Named Pipe exécutable.

## Résultats observés

| Essai | Action réelle | Résultat constaté | Interprétation |
|---|---|---|---|
| Enrôlement caméra, 5 s — 10:48 UTC | Ouverture webcam, capture et détection | `ENROLLMENT_FACE_TOO_SMALL` | Refus prudent : les images exploitables étaient trop petites. Aucun profil temporaire conservé. |
| Enrôlement caméra, 5 s — 10:52 UTC | Nouvelle capture réelle | `ENROLLMENT_TOO_FEW_VALID_FACE_FRAMES` | Refus prudent : nombre d’images valides insuffisant. |
| Requête D3→D1, caméra, 5 s — 10:54 UTC | Capture réelle et traitement du modèle | `INDETERMINATE / SPOOF_SUSPECTED`, qualité 0,407, 3 images valides | Le système a détecté un visage, mais la présence réelle n’a pas franchi le seuil. Aucun profil n’étant enrôlé dans le répertoire de test, aucune identité n’a été attribuée. |
| Requête caméra, 5 s — 10:55 UTC | Nouvelle requête réelle | `INDETERMINATE / FACE_NOT_DETECTED`, 0 visage sur 14 images lues | Le système n’a pas inventé d’identité quand aucun visage n’était dans le champ. |
| Enrôlement caméra, 5 s — 11:05 UTC | Capture réelle alors que l’utilisateur avait indiqué être devant la caméra | 14 images lues, 4 avec visage, taille maximale 140 px; présence moyenne 0,123, maximale 0,492; `ENROLLMENT_LIVENESS_NOT_CONFIRMED` | Le visage a été détecté, mais MiniFASNetV2 l’a rejeté comme non confirmé. L’enrôlement a donc été bloqué. C’est un faux rejet possible à investiguer, pas un résultat MATCH. |
| Requête caméra, 5 s — 11:07 UTC | Requête D1 complète avec caméra réelle | `INDETERMINATE / FACE_NOT_DETECTED`, 0 visage sur 14 images | Résultat cohérent avec une caméra sans visage visible au moment précis de la capture. |
| Caméra configurée sur l’index invalide 8 — 11:08 UTC | Demande D3→gestionnaire D1 et ouverture caméra impossible | `INDETERMINATE / VISUAL_ENGINE_ERROR_IOEXCEPTION` en 0,13 s; l’application continue | Le défaut caméra devient un résultat indéterminé et ne fait pas planter le processus. |

Des essais antérieurs avaient aussi donné une capture avec 3 images de visage valides, mais `SPOOF_SUSPECTED`, et une autre sans visage. Ils sont conservés dans `simulation/historique_essais_camera.jsonl`.

## Corrections apportées après inspection

- Le bouton de test D3 utilise maintenant la webcam et le moteur réel; les anciens choix de résultats fictifs sont retirés de son explication.
- Les rapports visuels incluent les diagnostics de capture pour distinguer absence de visage, cadrage, présence non confirmée et erreur caméra.
- Enrôlement et vérification refusent le résultat si une image de la séquence contient plusieurs visages. Une seule personne doit rester visible pendant toute la capture.
- Une caméra absente, une erreur modèle, une capture ambiguë ou une présence non confirmée ne peut pas produire `MATCH`.
- L’anti-usurpation conserve le seuil prudent de 0,80 : le diminuer sans corpus de visages réels et d’attaques imprimées/écran pourrait laisser passer une fausse identité.

## État et essais restant à faire

La compilation du simulateur a réussi le 21 novembre 2026 : 0 avertissement, 0 erreur.

**La reconnaissance faciale de l’utilisateur n’est pas encore validée de bout en bout.** Aucun profil visuel réel n’a pu être enrôlé parce que l’anti-usurpation n’a pas confirmé la capture. Par conséquent, un vrai résultat `MATCH` propriétaire et un vrai `NO_MATCH` pour une autre personne n’ont pas encore été mesurés. Il reste également à tester une photo imprimée ou affichée sur un écran, deux visages simultanés, l’éclairage faible et le délai maximal. Ces cas ne doivent pas être déclarés réussis sur la base d’une simulation de données.

Le prochain essai utile est de refaire l’enrôlement après avoir amélioré le cadrage et l’éclairage, puis d’inspecter la probabilité brute du modèle sur plusieurs séquences réelles. Le seuil ne doit être recalibré qu’avec des exemples vivants et des attaques de présentation clairement étiquetés. Jusqu’à cette calibration, le mot de passe maître reste la voie d’ouverture fiable et la reconnaissance visuelle doit rester prudente.
