# Mode apprentissage — démon 1

Ce dossier contient le premier mode utilisable du démon 1 : une collecte démarrée manuellement, le calcul de mesures de clavier/souris, la création progressive du profil et l’enregistrement dans SQLite.

## Démarrer la fenêtre de contrôle

Ouvre PowerShell, puis lance le fichier :

```powershell
powershell -ExecutionPolicy Bypass -File ".\Demon 1\Mode_Apprentissage\Controler-Apprentissage.ps1"
```

Le script n’active aucune collecte au démarrage. Dans son menu, choisis **1** et confirme que tu es le propriétaire. Les choix **2**, **3** et **4** servent respectivement à suivre les mesures, consulter la base et arrêter la collecte. Quitter le menu arrête la collecte si tu le confirmes. La collecte s’arrête aussi automatiquement après 90 secondes sans interaction; il faut alors la redémarrer manuellement.

Le collecteur fonctionne seulement pendant cette période manuellement démarrée. Il n’est pas installé en service et ne se lance pas automatiquement avec Windows.

## Ce qui est réellement recueilli

Windows Raw Input fournit les signaux de clavier/souris. Le collecteur travaille sur des périodes de 15 secondes. Il calcule des rythmes et durées : fréquence de frappe, durée d’appui, intervalle entre frappes, pauses, vitesse et variations de la souris, changements de direction et intervalle entre clics. Il ne garde pas le texte tapé. Les codes de touche sont utilisés temporairement en mémoire pour mesurer le temps d’appui, puis abandonnés.

Les événements utilisés dans la fenêtre PowerShell sont exclus du profil pour éviter que les commandes de contrôle n’apprennent les touches de suivi elles-mêmes. Les fenêtres sans assez d’activité sont ignorées. Une période utile doit atteindre une qualité minimale de 0,80.

La collecte ne décide pas qui est devant l’écran. Au démarrage, l’utilisateur confirme manuellement « je suis le propriétaire ». Cette confirmation n’est pas une reconnaissance faciale ni Windows Hello. Le module de comparaison d’identité reste séparé.

## Cycle du profil

- `NO_PROFILE` : aucun échantillon reçu.
- `CALIBRATING` : collecte des périodes acceptées.
- `PROVISIONAL` : au moins 20 périodes réparties sur 3 jours.
- `READY` : au moins 60 périodes sur 7 jours, avec au moins 30 périodes clavier et 30 souris dans le même contexte.
- `FROZEN` : le profil est gelé après réception d’un signal de menace par le service d’apprentissage.

Ces valeurs sont les réglages initiaux. Le collecteur présent n’est pas encore relié au démon 2; en fonctionnement isolé, il ne reçoit donc pas de signal réel de menace.

## Enregistrement et consultation

La base active est créée ici : `%LOCALAPPDATA%\SentinelCore\Demon1Learning\learning.db`. Elle est propre au compte Windows. Les mesures et les profils sont chiffrés avec la protection Windows du compte courant avant leur inscription dans SQLite. La fenêtre PowerShell peut relire les dernières périodes, les déchiffrer localement et afficher les statistiques; elle ne montre ni texte tapé ni image.

Le code de la base est dans `SqliteLearningProfileRepository.cs`; le collecteur Windows est dans `src/SentinelCore.Demon1.Learning.Collector`; la fenêtre de contrôle manuelle est `Controler-Apprentissage.ps1`.

## Mesures de cette version

Compilation de la solution : réussite, 0 avertissement et 0 erreur. Simulation de l’apprentissage SQLite : 70/70 périodes confirmées acceptées et 2/2 périodes non admissibles écartées (une provenance injectée, une période sans confirmation du propriétaire). La simulation a écrit 322 périodes dans SQLite, puis en a relu et déchiffré 5 pour l’affichage. Débit local observé : environ 50 périodes/seconde en mode Release avec chiffrement DPAPI et SQLite. À une période collectée toutes les 15 secondes, cela représente environ 750 fois le débit requis dans cette simulation.

Les deux taux de 100 % sont ceux de règles d’admission testées sur des cas synthétiques; ils ne représentent pas le taux de reconnaissance du propriétaire. Le débit mesuré est très supérieur au rythme réel (une période toutes les 15 secondes), mais il ne valide pas encore le bon fonctionnement de chaque clavier, souris, session à distance ou condition matérielle.

## Limites à garder en tête

- Windows Hello peut gérer la connexion par visage sur les appareils dotés d’une caméra infrarouge compatible, mais son modèle facial est géré par Windows et n’est pas fourni à ce module. Le contrôle actuel utilise une confirmation manuelle du propriétaire.
- Aucun visage n’est encore enregistré dans cette base.
- La comparaison visuelle, la détection de présence réelle et les verdicts d’identité sont laissés à un module distinct.
- Le collecteur est conçu pour les entrées Windows de la session active; les sessions sécurisées et certains périphériques/comportements peuvent ne pas produire des mesures comparables.
- Les performances de reconnaissance (FAR, FRR, EER) ne peuvent être calculées qu’après création d’un protocole avec des personnes consentantes, des sessions séparées et des exemples d’autres utilisateurs.

## Références de comparaison

Le benchmark CMU rapporte une EER de 9,62 % pour Manhattan mis à l’échelle dans son protocole de frappe; le challenge KVC rapporte 3,33 % pour ses meilleurs modèles desktop sur un large corpus. Ces taux concernent des systèmes de comparaison d’identité et des jeux de données spécifiques. Le présent mode apprend et stocke des mesures sans comparer une session à un profil; ces pourcentages ne sont donc pas directement comparables.

## Sources techniques

- [Windows Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/using-raw-input) pour recevoir les événements des périphériques.
- [Windows Hello visage](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/windows-hello-face-authentication) pour l’authentification par caméra infrarouge compatible.
- [Windows Biometric Framework](https://learn.microsoft.com/en-us/windows/win32/secbiomet/biometric-framework-overview) pour les API système d’identification et de vérification biométrique.
- [Benchmark CMU de frappe](https://www.cs.cmu.edu/~keystroke/) et [challenge KVC](https://arxiv.org/abs/2401.16559) pour les comparaisons de taux d’erreur publiés.
