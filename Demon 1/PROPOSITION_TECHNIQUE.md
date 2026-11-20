# Proposition technique — moteur comportemental D1

## Audit
Le dépôt SentinelCore contient les spécifications, les trois guides, le contrat inter-démons v1 et les bases de simulation, mais aucune solution .NET ni noyau D1 existant. Le runtime local expose .NET SDK 10.0.404 sous Windows. Aucun stockage, logger, transport de service ou moteur statistique réutilisable n’est donc présent. L’implémentation sera isolée sous `Demon 1/src` et `Demon 1/tests`; elle ne remplace aucun code.

## Choix
Noyau C# pur .NET, sans dépendance NuGet initiale, testé hors capteurs Windows. Profil par SID et versions initial/courant/historique. Caractéristiques indépendantes : créneaux horaires cycliques par jour de semaine, distributions numériques robustes (médiane, MAD et IQR), rareté de catégories d’applications avec lissage, qualité/maturité séparées du score d’écart. Aucun biométrique d’authentification.

MAD est une mesure robuste de dispersion; NIST note également la robustesse de l’IQR aux queues longues. Une période peut être cyclique : les heures sont donc modélisées par créneaux/catégories hebdomadaires, pas par différence numérique linéaire entre 23:59 et 00:01. Une EWMA peut aider à suivre de petites dérives, mais n’est pas utilisée pour mettre à jour automatiquement le profil au premier incrément : son admission reste gardée, et une mise à jour versionnée est appliquée uniquement aux observations admissibles.

## Admission et maturité
État de profil explicite: NO_PROFILE, CALIBRATING, INSUFFICIENT_DATA, LOW_MATURITY, MATURE, DEGRADED, CONFLICT, FROZEN. L’observation suspecte/ambiguë, associée à un finding sérieux D2, un risque Guard, une qualité insuffisante, un conflit ou un consentement absent n’est jamais intégrée au profil courant. Les observations refusées sont conservées comme diagnostic minimisé, sans métriques brutes. Le profil courant évolue lentement, historique append-only et rollback par version.

## Alternatives examinées
- Elastic ML fournit fonctions temporelles et rareté; puissant mais conçu pour pipelines/populations, pas justifié pour un seul endpoint hors ligne. Sa doc indique une période d’apprentissage d’au moins 20 buckets pour certaines fonctions rares.
- Microsoft Sentinel UEBA bâtit des baselines dynamiques à partir de sources cloud/organisationnelles; utile comme modèle conceptuel, mais non embarquable comme dépendance locale.
- River fournit des modèles d’apprentissage en ligne Python; non retenu au noyau pour éviter runtime Python et contamination par apprentissage non filtré. Une évaluation future doit se faire sur les mêmes fixtures et mesurer CPU/mémoire/faux positifs avant adoption.
- Statistiques robustes + filtres explicites gagnent en coût, auditabilité et comportement hors ligne. Aucun réseau neuronal/Isolation Forest sans gain mesuré.

## Sécurité et limites
Aucun contenu de frappe, mot de passe, PIN, presse-papiers, trajectoire souris, image ou gabarit facial. L’implémentation ne capture pas d’entrées globales et ne branche aucun capteur réel. Les profils JSON locaux incluent un hash d’intégrité, mais le hash seul n’est pas une protection contre un attaquant local; DPAPI Windows est prévu derrière l’abstraction de stockage. Si chiffrement/ACL ne peuvent être garantis, la fonctionnalité doit rester mode simulation. Le résultat d’anomalie n’est ni preuve d’identité ni verdict de menace.

## Interfaces
`IBehaviorProvider`, `IProfileStore`, `ILearningAdmissionPolicy`, `IInterDaemonBus`. Publication D1 vers Guard conforme au contrat v1: USER_CONTEXT, BEHAVIOR_DEVIATION et HEARTBEAT. Pas de connexion directe D1-D2; les demandes passent par D3 et ne révèlent que sessionState/localOrRemote/recentAuthBand.

## Coûts et étapes
Pas de bibliothèque tierce initiale; complexité CPU O(n log n) sur petits buffers bornés, mémoire proportionnelle au nombre de caractéristiques et au SID. Etapes: noyau + simulation; relecture fixtures; versionnement/stockage protégé; transport abstrait; seulement ensuite capteurs. Les mesures sur fixtures synthétiques ne seront pas présentées comme taux réels.

## Sources consultées
- Elastic rare functions: https://www.elastic.co/docs/reference/machine-learning/ml-rare-functions
- Microsoft Sentinel UEBA: https://learn.microsoft.com/en-us/azure/sentinel/ueba-reference
- River: https://github.com/online-ml/river
- NIST MAD/IQR: https://www.itl.nist.gov/div898/handbook/eda/section3/eda356.htm
- NIST EWMA: https://www.itl.nist.gov/div898/handbook/mpc/section2/mpc2211.htm
- NIST SP 800-63-4: https://pages.nist.gov/800-63-4/sp800-63b.html
