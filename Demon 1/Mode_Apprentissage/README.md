# Mode apprentissage — démon 1

Ce dossier contient le premier lot autonome du démon 1 : le service qui calibre et conserve un profil agrégé des habitudes du propriétaire.

## Cycle de profil

- `NO_PROFILE` : aucun échantillon reçu.
- `CALIBRATING` : collecte des fenêtres d’activité acceptées.
- `PROVISIONAL` : au moins 20 fenêtres réparties sur 3 jours; profil consultable, mais immature.
- `READY` : au moins 60 fenêtres sur 7 jours, avec au moins 30 fenêtres clavier et 30 fenêtres souris dans le même contexte. L’adaptation reste filtrée.
- `FROZEN` : signal de menace; aucune nouvelle donnée ne modifie le profil jusqu’à une reprise explicitement approuvée après vérification du propriétaire.

Ces seuils sont les valeurs initiales du produit, à valider sur l’usage réel.

## Admission d’une fenêtre

L’entrée est déjà agrégée : le collecteur qui sera ajouté plus tard transforme Raw Input en mesures statistiques. Le service refuse les caractéristiques inconnues, les valeurs invalides, la qualité inférieure à 0,80, l’identité non vérifiée, une provenance d’entrée injectée ou inconnue, et toute fenêtre associée à un signal de menace. Après maturité, une fenêtre qui dépasse 3,5 dispersions robustes est mise en quarantaine pour réduire le risque d’empoisonnement du profil.

Les champs de frappe sont des mesures de cadence/durée, jamais des caractères. Seuls les agrégats récents (maximum 256 valeurs par caractéristique et contexte) sont persistés. Le dépôt Windows protège le profil par DPAPI CurrentUser et lie l’entropie au SID. L’adaptateur de caméra et le vrai collecteur clavier/souris ne sont pas encore inclus : l’appelant doit fournir une preuve d’identité et une fenêtre agrégée.

## Caractéristiques acceptées

`typing.rate`, `typing.dwell_median_ms`, `typing.flight_median_ms`, `typing.pause_ratio`, `mouse.speed_median`, `mouse.acceleration_p95`, `mouse.curvature_median`, `mouse.click_interval_median_ms`.

Le contexte (par exemple `LOCAL|INTEGRATED_KB|MOUSE_USB`) évite de fusionner directement un clavier intégré, un clavier externe ou une session distante. Ne pas placer un nom de fichier, un chemin ou une donnée personnelle dans `ContextKey`.

## Exécuter la simulation

Depuis le dossier `Demon 1` :

```powershell
dotnet run --project Mode_Apprentissage/simulation/SentinelCore.Demon1.Learning.Simulation
```

La simulation fait apprendre 70 fenêtres synthétiques sur 7 jours, présente deux cas que le filtre doit mettre en quarantaine, puis mesure le débit local du service. Les ratios 70/70 et 2/2 sont des résultats de règles déterministes sur des données artificielles, pas de la précision biométrique terrain.

## Comparaison aux approches existantes

- Le benchmark [CMU de Killourhy et Maxion](https://www.cs.cmu.edu/~keystroke/) compare des détecteurs avec protocole reproductible. Il rapporte notamment 9,62 % d’EER pour Manhattan mis à l’échelle et 11,01 % pour Mahalanobis sur le jeu étudié ([article et résultats](https://www.cs.cmu.edu/~ksk/KillourhyMaxion2009.pdf)). Une EER de 9,62 % signifie que les taux de fausse acceptation et de faux rejet sont chacun proches de 9,62 % à ce point de fonctionnement; ce n’est pas une exactitude universelle. Le jeu porte sur des phrases de mot de passe et ne représente pas notre mode continu souris+clavier.
- [TypingDNA](https://api.typingdna.com/docs/index.html) documente ses limites : faux rejets et fausses acceptations existent, et la biométrie de frappe ne doit pas être le facteur unique.
- Le [challenge KVC desktop](https://arxiv.org/abs/2401.16559) rapporte une EER aussi basse que 3,33 % pour les meilleurs modèles sur son corpus et son protocole. Cela montre ce que peut donner un gros corpus spécialisé, pas ce que l’on peut promettre pour un seul propriétaire sur un poste réel.
- Ce lot choisit médiane/MAD/IQR et un historique borné pour la calibration locale : données rares, calcul léger, inspectabilité et protection contre l’empoisonnement. Il ne prétend pas battre les EER publiées tant qu’un protocole comparable n’a pas été exécuté.

## Résultat mesuré dans l’environnement de développement

La compilation du projet de simulation a réussi avec 0 avertissement et 0 erreur. La simulation synthétique atteint `READY`, accepte 70/70 fenêtres (100 %), met en quarantaine 2/2 observations suspectes (100 %) et mesure 1 063 fenêtres agrégées/s sur 2 000 appels (Windows, .NET 10, build Debug, dépôt mémoire). Le débit dépend du poste et ne représente pas le débit caméra/capteurs ni une performance d’identification.

Notre lot n’est pas encore un vérificateur d’identité et n’a donc pas d’EER/FAR/FRR comparable. Annoncer un pourcentage de reconnaissance maintenant serait inventer un résultat.

## Suite technique

1. Relier l’admission au collecteur Windows de mesures clavier/souris.
2. Relier le champ `OwnerVerification` au vérificateur visuel/Windows Hello.
3. Comparer les sessions avec des données publiques et un jeu pilote volontaire, puis mesurer FAR, FRR, EER, délai de calibration et coûts CPU/mémoire.
4. Publier les changements de maturité vers le démon 3 via le contrat inter-démons.


Une explication en langage simple, avec des exemples pour chaque fonction, se trouve dans [fonctionnement.txt](fonctionnement.txt).
