# Démon 1 — spécification cible v2

## 1. Mission

Le démon 1 est le **moniteur d’identité et d’interaction humaine** de SentinelCore. Il maintient un profil local du propriétaire à partir de son enrôlement visuel et de ses habitudes d’usage, compare le profil avec la session active, repère les indices d’automatisation et publie un rapport contextualisé au démon 3.

Il ne remplace pas le démon 2 : l’analyse des processus, fichiers, scripts, téléchargements, connexions et menaces système reste au démon 2, qui réutilise les journaux natifs Windows. Le démon 1 décrit **qui semble manipuler le poste, comment l’entrée est produite et à quel point elle ressemble aux habitudes du propriétaire**. Le démon 3 corrèle ces éléments et choisit les actions prévues par sa politique.

## 2. Exigences fonctionnelles

1. Enrôler le propriétaire visuellement et constituer un profil comportemental personnel.
2. Suivre l’usage actif et comparer la session à ce profil, avec un état : `OWNER_LIKELY`, `OTHER_PERSON_LIKELY`, `AUTOMATION_LIKELY`, `AMBIGUOUS`, `NO_SIGNAL`.
3. Distinguer les interactions humaines habituelles des entrées injectées ou automatisées, en combinant la cadence clavier/souris, leur provenance Windows et les événements du démon 2.
4. Calculer un indice de divergence borné de 0 à 100. Publier `BEHAVIOR_DEVIATION` au démon 3 si l’indice est **strictement supérieur à 60**, avec confiance, raisons et disponibilité des capteurs.
5. Détecter les risques liés à l’usage humain du poste : changement d’opérateur, session laissée ouverte, identité non reconnue, activité inhabituelle, saisie automatisée/injectée et actions humaines corrélées à un comportement dangereux signalé par le démon 2.

« Risque lié à l’humain » ne signifie pas que D1 analyse le contenu des frappes ou décide qu’une personne est malveillante. D1 ne conserve pas les caractères tapés et ne qualifie pas une personne de criminelle. Il fournit des observations mesurées à D3.

## 3. Composants à coder

### A. `WindowsSessionContextProvider`

Lit l’identité Windows, le SID, l’identifiant de session, l’état local/distant, verrouillage/déverrouillage et les changements d’utilisateur via les API et journaux Windows disponibles. Il ne reconstruit pas l’historique de processus. D2 fournit les événements de processus natifs (notamment Security Event 4688 quand activé) et le contexte de processus.

### B. `InputTelemetryProvider`

Collecte les événements clavier/souris dans la session utilisateur Windows avec les API Win32 adaptées. Un collecteur de session utilisateur est nécessaire : ne pas placer un collecteur d’interactions dans un service Windows isolé en Session 0.

Les caractères, mots, mots de passe et contenu du presse-papiers ne sont jamais lus ni stockés. Les événements bruts restent uniquement en mémoire le temps de produire des fenêtres agrégées et sont ensuite supprimés.

Caractéristiques par fenêtres glissantes de 10 s, 60 s et 5 min :
- clavier : vitesse (frappes/minute), temps d’appui, délai entre frappes, variance, rafales, pauses et corrections agrégées sans contenu;
- souris : vitesse, accélération, changements de direction, courbure, pauses, clics/minute et intervalles entre clics;
- qualité : nombre d’événements, type de périphérique, perte de données, session distante, disponibilité du capteur.

Le contenu saisi et la trajectoire brute ne sont pas nécessaires au modèle. Les trajectoires sont converties en statistiques de fenêtre puis éliminées. Toute conservation exceptionnelle d’éléments bruts exige une règle séparée et une durée de rétention explicite.

### C. `InputOriginClassifier`

Combine :
- indicateurs Windows d’événement clavier/souris injecté (`LLKHF_INJECTED`, `LLMHF_INJECTED`) lorsqu’ils sont disponibles;
- présence et caractéristiques générales du périphérique Raw Input;
- régularité temporelle artificielle, cadence et répétition des séquences de mouvement;
- contexte de session distante/locale;
- événements D2 sur le processus producteur et sa chaîne de parents.

Sortie : `physical_input_likelihood`, `injected_input_likelihood`, `automation_likelihood`, chacune avec qualité et raisons. Un indicateur d’injection n’est pas une preuve complète : certains robots utilisent un périphérique HID physique, certains logiciels ne sont pas visibles par ce seul indicateur et des outils d’accessibilité légitimes peuvent injecter des entrées. Aucun signal isolé ne doit déclencher un verdict.

### D. `OwnerVisualVerifier`

Enrôlement dans les paramètres SentinelCore, initié explicitement par le propriétaire. À la demande, capture locale d’une courte séquence vidéo (objectif initial : 5 secondes), détection du visage, contrôle de présence/liveness et comparaison au gabarit du propriétaire. La capture est traitée localement; le modèle doit produire `match_score`, `liveness_score`, qualité et résultat. Si la caméra ou le contrôle de présence échoue, résultat `UNKNOWN`, jamais `OWNER` par défaut.

Windows Hello reste une option native de ré-authentification, mais ne livre pas à SentinelCore son modèle facial brut ni son identité multi-profil. Le prototype local actuel utilise YuNet pour détecter le visage, SFace pour le comparer et MiniFASNetV2 pour repérer certaines images/relectures. Il est implémenté en .NET avec OpenCvSharp 5; les poids sont téléchargés par un script avec empreintes SHA-256 vérifiées. L’enrôlement requiert le mot de passe maître et un accord initial; une fois la fonction activée dans les paramètres, les demandes déclenchées par D3 n’affichent pas de confirmation répétée. Aucune image n’est envoyée à Ollama, à un serveur ou à un service tiers.

Une image d’événement est conservée localement sous une référence opaque et chiffrée avec DPAPI. D3 peut la lire par morceaux pour l’intégrer au rapport; le rapport doit conserver sa propre copie une fois intégré. La gestion de durée de conservation et de purge automatique de la preuve locale reste à définir. Les scores des modèles sont des indices, pas des probabilités ni une garantie contre une vidéo d’attaque sophistiquée.

### E. `BehaviorProfileEngine`

Profils séparés par SID et périphérique/classe de périphérique. Les variables sont robustes aux valeurs extrêmes et segmentées par contexte (clavier, souris, heure/jour, session locale/distante). Le modèle apprend progressivement uniquement à partir de fenêtres admissibles : propriétaire vérifié ou forte concordance, capteurs de qualité suffisante, aucun signal sérieux de D2 et aucune suspicion d’automatisation.

Une fenêtre atypique, ambiguë, injectée ou associée à une menace D2 est mise en quarantaine et ne met pas à jour le profil. Maturité et qualité sont transmises séparément du score : un profil immature ne peut pas rendre une forte confiance d’identité.

### F. `HumanContextScorer` et `Demon3Publisher`

La validation comportementale actuelle compare les mesures clavier/souris à des statistiques robustes et renvoie une divergence 0–100 ou `INCONCLUSIVE`. Le prototype visuel produit séparément `MATCH`, `NO_MATCH` ou `INDETERMINATE`, présence réelle, qualité et référence de preuve. Le résumé d’application/heure/CPU/mémoire peut être inclus dans les payloads D1 destinés à D3. Les signaux restent séparés : la présence du propriétaire ne diminue jamais la gravité technique d’un événement D2 par elle-même.

Le client D1 se connecte maintenant au pipe nommé de D3, fait le handshake, envoie ses heartbeats, publie `USER_CONTEXT` pendant une collecte active et transmet `BEHAVIOR_DEVIATION` après une comparaison faite avant l’ajout de la fenêtre courante au profil. Il répond aux demandes de contexte/visuelles et sert la preuve par morceaux. D3 doit encore implémenter l’allowlist et le traitement de ces types, puis copier l’image dans son rapport. D1 n’exécute pas de réponse de sécurité.

## 4. Définition du seuil de 60 %

Le 60 est un **seuil de divergence du profil**, pas une probabilité que l’utilisateur soit malveillant ni une probabilité d’erreur du modèle. La règle comportementale est implémentée, mais l’indicateur `IsEmpiricallyValidated` reste faux jusqu’à l’évaluation sur des personnes et sessions réelles distinctes. Le score reste un indice non calibré; D3 ne doit pas le traiter comme verdict.

La validation comportementale déclenche maintenant `BEHAVIOR_DEVIATION` au-dessus de 60 si les données sont assez fiables; cette règle logicielle n’établit pas une précision statistique terrain. Le branchement au transport D3 résident reste à faire.

## 5. Contrat de message D1 → D3

Types du contrat commun : `USER_CONTEXT`, `BEHAVIOR_DEVIATION`, `CONTEXT_REQUEST`, `CONTEXT_RESPONSE` et, pour l’identité visuelle, `VISUAL_IDENTITY_REQUEST`, `VISUAL_IDENTITY_RESPONSE`, `VISUAL_EVIDENCE_READ_REQUEST`, `VISUAL_EVIDENCE_CHUNK`. D3 héberge seul `\\.\pipe\SentinelCore.Guard`; D1 reste client. Les messages utilisent l’enveloppe v1, `correlationId`, expiration et limite 64 KiB. Seule la demande visuelle peut attendre jusqu’à 60 secondes; D3 continue d’évaluer les événements D2 pendant ce délai. Les types visuels sont spécifiés dans `Mode_Validation/CONTRAT_VISUEL_D1_D3.txt`.

Exemple de payload `BEHAVIOR_DEVIATION` :

```json
{
  "schemaVersion": 1,
  "observedAtUtc": "2026-11-20T10:42:18Z",
  "sid": "S-1-5-21-...",
  "sessionId": "42",
  "operatorAssessment": "AUTOMATION_LIKELY",
  "deviationPercent": 73,
  "confidence": 0.81,
  "calibrationState": "UNVALIDATED",
  "signals": {
    "keyboardDeviationPercent": 70,
    "mouseDeviationPercent": 76,
    "keyboardFeatureCount": 4,
    "mouseFeatureCount": 4,
    "rawKeystrokesStored": false,
    "applicationContext": {
      "profileId": "PROFIL_1",
      "observedAtUtc": "2026-11-20T10:42:15Z",
      "ageSeconds": 3,
      "foregroundApplication": "Code",
      "foregroundSeconds": 12,
      "localHour": 10,
      "localDayOfWeek": 5,
      "seenInLearnedHistory": true,
      "hourCommonForApplication": true,
      "cpuPercent": 14.2,
      "memoryMiB": 720,
      "systemMemoryUsedPercent": 68,
      "sampleCount": 30,
      "reasonCode": "APPLICATION_AND_HOUR_SEEN_IN_PROFILE"
    }
  },
  "reasonCodes": ["INJECTED_INPUT", "MOUSE_CADENCE_OUTLIER"],
  "evidenceRefs": [],
  "expiresAtUtc": "2026-11-20T10:42:48Z"
}
```

Ne jamais transmettre le texte saisi, les frappes individuelles ou le gabarit facial. Les images sont transférées par morceaux uniquement sur demande de D3, via une référence opaque locale, jamais un chemin arbitraire.

D2 → D3 fournit l’événement de sécurité et la chaîne de processus. D3 corrèle par SID, session, heure et identifiants de corrélation. D1 et D2 ne dépendent pas directement l’un de l’autre.

## 6. Comparaison aux solutions existantes

- **TypingDNA** : biométrie de frappe pour enrôlement/vérification et intégrations applicatives. Sa propre documentation avertit que la précision n’est pas parfaite, qu’il existe faux rejets/acceptations, et recommande de ne pas en faire le seul facteur. C’est un bon modèle pour les caractéristiques de frappe et les états de vérification, mais pas un agent Windows complet.
- **BioCatch** : combine cadence de frappe, mouvements souris, parcours et signaux de contexte pour l’authentification passive et la détection de fraude. C’est une référence pour la fusion multimodale, principalement orientée applications et fraude numérique.
- **Windows Hello / Windows Biometric Framework** : capacité native à vérifier/re-vérifier l’utilisateur avec biométrie et matériel compatible. À privilégier pour une vérification forte et liveness, sans tenter de récupérer ou refaire le gabarit sécurisé par Windows.
- **Audit Windows 4688** : source native pour l’attribution et l’arbre des processus; consommée par D2, puis corrélée par D3.

La proposition SentinelCore ajoute à la biométrie comportementale applicative une vue poste Windows : provenance de l’entrée, contexte de session, corrélation avec les processus natifs et rapport inter-démons.

## 7. Tests d’acceptation nécessaires

- Enrôlement propriétaire, refus de caméra, faible lumière, changement d’apparence et appareil sans caméra compatible.
- Variations normales : fatigue, vitesse inhabituelle, clavier/souris remplacés, accessibilité, pavé tactile, session distante.
- Autres humains, session laissée ouverte, alternance de personnes.
- Entrée synthétique `SendInput`, macros, robot logiciel, HID automatisé et outil d’accessibilité légitime.
- Faux positifs/faux négatifs distincts pour visage, liveness, cadence et détecteur d’injection.
- Événements D2 concomitants, corrélation D3, panne d’un démon, évènements retardés/dupliqués.
- Vérification du gel d’apprentissage face à un incident et absence d’empoisonnement de profil.
- Tests sur utilisateurs et jours séparés; les 50 fixtures synthétiques actuelles ne constituent pas une validation biométrique terrain.

## 8. Références

- Microsoft Raw Input : https://learn.microsoft.com/en-us/windows/win32/inputdev/using-raw-input
- Microsoft indicateur clavier injecté : https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-kbdllhookstruct
- Microsoft indicateur souris injectée : https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-msllhookstruct
- Microsoft Windows Hello visage et re-authentification : https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/windows-hello-face-authentication
- Microsoft Windows Biometric Framework : https://learn.microsoft.com/en-us/windows/win32/secbiomet/biometric-framework-overview
- TypingDNA API et limites : https://api.typingdna.com/docs/index.html
- BioCatch, biométrie comportementale : https://www.biocatch.com/resources/solution-brief/advanced-behavioral-biometrics
- Étude multimodale frappe + souris : https://www.sciencedirect.com/science/article/pii/S0925231216314321
- Microsoft Audit Event 4688 : https://learn.microsoft.com/windows/security/threat-protection/auditing/event-4688
- OpenCV Zoo YuNet/MIT : https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet
- OpenCV Zoo SFace/Apache-2.0 : https://github.com/opencv/opencv_zoo/tree/main/models/face_recognition_sface
- OpenCvSharp 5 et API faciale : https://shimat.github.io/opencvsharp/api/OpenCvSharp.FaceRecognizerSF.html
- MiniFASNet ONNX : https://github.com/yakhyo/face-anti-spoofing

## État d’implémentation du mode apprentissage

Le dossier `Mode_Apprentissage/` contient le collecteur, le contrôle WinForms et SQLite/DPAPI, avec contexte d’application, heure, CPU et mémoire. `Mode_Validation/` contient la comparaison clavier/souris, le moteur visuel, l’enrôlement par profil et le client D1 du pipe commun. La compilation complète réussit. L’allowlist/traitement des types côté D3, le démarrage persistant de D1, les essais webcam réels, les mesures de faux rejets/acceptations et la copie finale de la preuve dans le rapport demeurent nécessaires.
