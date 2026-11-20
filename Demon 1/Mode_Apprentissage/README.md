# Démon 1 — mode apprentissage

## Lancer l’application

L’application de contrôle Windows se trouve après publication dans `artifacts/win-x64/SentinelCore.Demon1.Learning.Controller.exe`. Double-clique sur ce fichier pour ouvrir la fenêtre de suivi. Elle ne démarre jamais la collecte automatiquement.

Pour reconstruire le fichier exécutable depuis la racine du dépôt :

```powershell
.\Demon 1\Mode_Apprentissage\build-release.ps1
```

Le script PowerShell `Controler-Apprentissage.ps1` est maintenant un simple lanceur de l’EXE; toute la configuration et le suivi se font dans la fenêtre.

## Autoriser une étude

À la première ouverture, la fenêtre demande de créer le mot de passe maître (12 caractères minimum). Seule une empreinte PBKDF2 salée est stockée; le fichier est en plus chiffré avec DPAPI pour le compte Windows courant.

Pour commencer une session, l’utilisateur doit réussir soit l’invite de vérification Windows Hello, soit la vérification du mot de passe maître, puis confirmer explicitement le démarrage. Si Hello échoue ou est annulé, aucune collecte ne commence. Le collecteur exige aussi un ticket protégé DPAPI à usage unique, valable 30 secondes, remis par le contrôleur après authentification.

Dans une application desktop .NET, l’intégration utilise l’API Windows `UserConsentVerifierInterop` liée à la fenêtre propriétaire. Windows décide quel fournisseur est disponible/configuré (visage, empreinte, PIN Hello); SentinelCore ne reçoit pas la donnée biométrique. La disponibilité réelle dépend du PC et des paramètres Windows Hello.

## Paramètres et suivi

La fenêtre permet :

- de lancer l’apprentissage via Windows Hello ou le mot de passe maître;
- d’arrêter la collecte manuellement;
- de suivre l’état et les dernières mesures en temps réel;
- de consulter les 200 dernières périodes enregistrées;
- de modifier la période de mesure (10–60 s) et l’arrêt après inactivité (30–900 s);
- de changer le mot de passe maître après vérification de l’ancien.

La base est enregistrée dans `%LOCALAPPDATA%\SentinelCore\Demon1Learning\learning.db`. Les profils et mesures sont chiffrés par DPAPI par compte utilisateur Windows. Le mot de passe maître et les empreintes sont enregistrés dans `auth.bin`, également protégé DPAPI. Les préférences générales sont dans `settings.json`.

## Données collectées

Le collecteur Windows Raw Input agrège les événements par période : fréquence de frappe, durée d’appui, intervalle et pauses entre frappes, vitesse/accélération/curvature de la souris et intervalles de clic. Les codes de touches n’existent qu’en mémoire le temps de calculer une durée d’appui; aucun caractère ni texte n’est écrit dans le profil. Les mesures brutes temporaires sont abandonnées après calcul.

Le système exclut les événements quand la fenêtre SentinelCore est active, n’enregistre pas de mesure si l’activité est insuffisante et interrompt la collecte après le délai d’inactivité configuré. Ce module ne produit pas de verdict d’identité; le module de validation le fera plus tard.

## Structure

- `src/SentinelCore.Demon1.Learning` : profils, règles d’admission, SQLite, DPAPI et ticket de démarrage à usage unique.
- `src/SentinelCore.Demon1.Learning.Collector` : collecte Windows clavier/souris et écriture de l’état.
- `src/SentinelCore.Demon1.Learning.Controller` : interface WinForms, mot de passe maître, Windows Hello, paramètres et contrôles.
- `simulation` : simulation de calibration et test de fumée du collecteur publié.
- `fonctionnement.txt` : explication simple, exemples et comportement attendu.

La branche `feature/demon-1` regroupe le travail du démon 1. Le contrôleur et les composants de l’apprentissage restent dans le dossier de ce module.

## Vérification effectuée

- `dotnet build Demon 1/SentinelCore.Demon1.slnx -c Release` : réussi, 0 avertissement et 0 erreur.
- Publication autonome `win-x64` en fichier unique : réussie.
- Simulation d’apprentissage synthétique : 70/70 périodes admissibles acceptées et 2/2 périodes inadmissibles rejetées; 322 périodes SQLite persistées et relues. Ces pourcentages évaluent uniquement les règles sur des données synthétiques, pas la reconnaissance du propriétaire.
- Test fumée du collecteur publié : un `status.json` déjà présent a été mis à jour, le ticket a été consommé une fois, l’arrêt a été appliqué, puis le JSON final a été relu. L’exception d’accès montrée auparavant n’est pas réapparue.
- Lancement UI : l’EXE s’ouvre et affiche la configuration initiale. La création du mot de passe et l’invite biométrique sont laissées à l’utilisateur; ces invites sensibles ne sont pas automatisées.

Le taux de faux rejet/faux acceptation du futur module de validation n’est pas mesurable avec ce module d’apprentissage seul. La collecte terrain (durées, variations d’appareils et qualité des mesures) reste à évaluer sur des sessions choisies par le propriétaire.

## Documentation Windows

- [Windows Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/using-raw-input)
- [Windows Hello face authentication](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/windows-hello-face-authentication)
- [UserConsentVerifier et vérification dans une application desktop](https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.ui.userconsentverifier)
- [Interop WinRT pour les applications .NET desktop](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-com-interop-csharp)
- [Windows Biometric Framework](https://learn.microsoft.com/en-us/windows/win32/secbiomet/biometric-framework-overview)
