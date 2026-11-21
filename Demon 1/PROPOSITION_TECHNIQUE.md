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

Étudier en premier les capacités Windows Hello / Windows Biometric Framework disponibles sur l’appareil. Windows Hello offre re-authentification et anti-spoofing sur matériel compatible, mais ne livre pas à SentinelCore son modèle facial brut. Si un mode propriétaire distinct avec vidéo de 5 secondes est développé, sélectionner et documenter un moteur local, les formats et la protection du gabarit avant l’implémentation. Aucun envoi d’images vers Ollama, un serveur ou un service tiers.

La vidéo ou l’image d’un événement peut être référencée dans le rapport sous un identifiant de preuve, horodatage, hash et état de stockage. Comme le besoin de conserver l’image dans le rapport entre en conflit avec une suppression automatique du fichier, le stockage de la preuve doit avoir une politique de rétention séparée : chiffrement local, ACL Windows strictes, accès journalisé, et suppression explicite par le propriétaire/politique de conservation. Les rapports doivent indiquer si la preuve est encore disponible.

### E. `BehaviorProfileEngine`

Profils séparés par SID et périphérique/classe de périphérique. Les variables sont robustes aux valeurs extrêmes et segmentées par contexte (clavier, souris, heure/jour, session locale/distante). Le modèle apprend progressivement uniquement à partir de fenêtres admissibles : propriétaire vérifié ou forte concordance, capteurs de qualité suffisante, aucun signal sérieux de D2 et aucune suspicion d’automatisation.

Une fenêtre atypique, ambiguë, injectée ou associée à une menace D2 est mise en quarantaine et ne met pas à jour le profil. Maturité et qualité sont transmises séparément du score : un profil immature ne peut pas rendre une forte confiance d’identité.

### F. `HumanContextScorer` et `Demon3Publisher`

Calcule les sous-scores indépendants (visage/liveness, clavier, souris, provenance, session) puis une divergence globale 0–100. Chaque rapport contient les scores disponibles, le niveau de confiance, les raisons explicables et les capteurs indisponibles. Une donnée absente n’est pas convertie en score neutre ou positif.

Publier à D3 si divergence > 60, identité différente probable, automatisation probable, liveness échouée, ou signal de qualité insuffisante dans une situation sensible. Publier également un contexte normal périodique pour que D3 sache que D1 fonctionne. D1 n’exécute pas de réponse destructive; il n’arrête pas seul des processus et ne bloque pas l’accès aux données.

## 4. Définition du seuil de 60 %

Le 60 est un **seuil de divergence du profil**, pas une probabilité que l’utilisateur soit malveillant ni une probabilité d’erreur du modèle. Le score doit être défini et documenté comme indice composite 0–100, étalonné sur des données réelles autorisées et des scénarios séparés par session/jour. Avant cet étalonnage, marquer le score `UNVALIDATED`; D3 peut l’utiliser comme signal, pas comme verdict.

La branche actuelle ne satisfait pas cette exigence : son `AnomalyLevel` est un score heuristique et le publisher avertit à 0,35. Il faut remplacer ce déclenchement par la définition ci-dessus avant de présenter D1 comme conforme au seuil >60.

## 5. Contrat de message D1 → D3

Types : `USER_CONTEXT`, `BEHAVIOR_DEVIATION`, `OWNER_IDENTITY_CHANGE`, `AUTOMATION_SUSPECTED`, `SENSOR_DEGRADED`, `HEARTBEAT`.

Exemple de payload `BEHAVIOR_DEVIATION` :

```json
{
  "schemaVersion": 2,
  "observedAtUtc": "2026-11-20T10:42:18Z",
  "sid": "S-1-5-21-...",
  "sessionId": "42",
  "operatorAssessment": "AUTOMATION_LIKELY",
  "deviationPercent": 73,
  "confidence": 0.81,
  "calibrationState": "UNVALIDATED",
  "signals": {
    "visualMatch": "NOT_REQUESTED",
    "liveness": "NOT_REQUESTED",
    "typingCadence": "HIGH_DEVIATION",
    "mouseDynamics": "HIGH_DEVIATION",
    "injectedInput": "DETECTED",
    "sessionType": "LOCAL"
  },
  "reasonCodes": ["INJECTED_INPUT", "MOUSE_CADENCE_OUTLIER"],
  "evidenceRefs": [],
  "rawKeystrokesStored": false,
  "expiresAtUtc": "2026-11-20T10:42:48Z"
}
```

Ne jamais transmettre le texte saisi, les frappes individuelles, le gabarit facial ou les images par défaut. Les références de preuve sont des identifiants locaux protégés, pas des chemins arbitraires fournis par l’IA.

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

## État d’implémentation du mode apprentissage

Le dossier `Mode_Apprentissage/` contient le collecteur Windows, la fenêtre de contrôle, le mot de passe maître, SQLite/DPAPI et les paramètres. L’ouverture de l’application propose le mot de passe maître ou le moteur facial local SentinelCore (YuNet/SFace/MiniFASNetV2); elle n’appelle pas Windows Hello. `Mode_Validation/` contient les comparaisons comportementale et visuelle ainsi que le client D1 du canal Guard.

La compilation de la solution et la publication Windows autonome passent. Les tests comportementaux internes, dont les 50 scénarios publiés, passent. Les essais webcam réels figurent dans `Mode_Validation/ESSAIS_REELS_D1.md` : l’enrôlement n’a pas encore réussi, car MiniFASNetV2 n’a pas confirmé liveness sur les captures observées. En conséquence, ni `MATCH` propriétaire ni `NO_MATCH` avec une autre personne ne sont validés terrain. La suite doit calibrer qualité/liveness avec des captures réelles étiquetées; elle ne doit pas baisser le seuil sans essais de présentation (photo et écran). Le serveur Named Pipe exécutable de D3 n’est pas encore présent dans ce dépôt, donc l’IPC entre deux processus reste à intégrer et tester.
